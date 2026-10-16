// Linux entry points. The shim is loaded with LD_PRELOAD and exports its own
// dlsym and open64 so the dynamic linker routes the game calls thru it

use std::{ffi::{CStr, CString, OsStr, c_char, c_int, c_void}, os::unix::ffi::OsStrExt, path::Path, sync::OnceLock};

use crate::{CORECLR_HANDLE, REAL_INITIALIZE, ffi::{self, CoreclrInitializeFn, Open64Fn}, hooked_initialize, redirect};

static REAL_OPEN64: OnceLock<Option<Open64Fn>> = OnceLock::new();

/// Intercepts `dlsym` lookups for `coreclr_initialize`.
///
/// # Safety
///
/// `symbol` must point to a valid NUL-terminated C string.
/// `handle` must be a valid dynamic linker handle.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn dlsym(handle: *mut c_void, symbol: *const c_char) -> *mut c_void {
    if symbol.is_null() {
        return std::ptr::null_mut();
    }

    let name = unsafe { CStr::from_ptr(symbol) }.to_string_lossy();

    // replace only the coreclr_initialize lookup, every other symbol is
    // passed directly through to the real dynamic linker
    if name == "coreclr_initialize" {
        debug_log!("intercepted dlsym lookup for coreclr_initialize");

        let real = unsafe { ffi::resolve(handle, c"coreclr_initialize") };
        if real.is_null() {
            return std::ptr::null_mut();
        }

        // save the real fn + handle so hooked_initialize/bootstrap_managed
        // can use them later without recursing into our own dlsym
        let real_fn: CoreclrInitializeFn = unsafe { std::mem::transmute(real) };
        let _ = REAL_INITIALIZE.set(real_fn);
        let _ = CORECLR_HANDLE.set(handle as usize);

        return hooked_initialize as *mut c_void;
    }

    match ffi::real_dlsym() {
        Some(real_dlsym) => unsafe { real_dlsym(handle, symbol) },
        None => std::ptr::null_mut(),
    }
}

/// Redirects framework assembly opens to the complete .NET runtime copy.
///
/// # Safety
///
/// `pathname` must be a valid NUL-terminated C string when non-null.
/// `flags` must be valid for libc's `open64`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn open64(
    // only reachable via LD_PRELOAD, never called ourselves
    pathname: *const c_char,
    flags: c_int,
) -> c_int {
    let Some(real) = REAL_OPEN64.get_or_init(|| {
        let ptr = unsafe { ffi::resolve(libc::RTLD_NEXT, c"open64") };
        if ptr.is_null() {
            None
        } else {
            Some(unsafe { std::mem::transmute::<*mut c_void, Open64Fn>(ptr) })
        }
    }) else {
        return -1;
    };

    unsafe { redirect_open(pathname, flags, *real) }
}

// the open64 hook body, ask redirect.rs if this path gets swapped
unsafe fn redirect_open(pathname: *const c_char, flags: c_int, real: Open64Fn) -> c_int {
    if pathname.is_null() {
        return unsafe { call_real(real, pathname, flags) };
    }

    let requested = unsafe { CStr::from_ptr(pathname) };
    let requested_path = Path::new(OsStr::from_bytes(requested.to_bytes()));

    match redirect::redirect_target(requested_path) {
        Some(replacement) => {
            debug_log!(
                "redirecting {} -> {}",
                requested_path.display(),
                replacement.display()
            );

            let Ok(cstr) = CString::new(replacement.as_os_str().as_encoded_bytes()) else {
                return unsafe { call_real(real, pathname, flags) };
            };

            unsafe { call_real(real, cstr.as_ptr(), flags) }
        }

        None => unsafe { call_real(real, pathname, flags) },
    }
}

// O_CREAT adds a variadic mode arg that Open64Fn can't represent w/ safe rust,
// so use the raw syscall here like the original
unsafe fn call_real(real: Open64Fn, pathname: *const c_char, flags: c_int) -> c_int {
    if flags & libc::O_CREAT != 0 {
        return unsafe {
            libc::syscall(libc::SYS_openat, libc::AT_FDCWD, pathname, flags, 0o666) as c_int
        };
    }

    unsafe { real(pathname, flags) }
}