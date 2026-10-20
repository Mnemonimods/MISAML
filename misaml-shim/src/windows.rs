// The Windows side of misaml-shim. misaml-cli injects this dll before the game
// runs any code/initialises. when coreclr.dll loads, this patches
// coreclr_initialize and redirects the game's trimmed framework dlls to the
// full untrimmed copies in MISAML/runtime/, just like the Linux side.

use std::ffi::c_void;
use std::os::windows::ffi::{OsStrExt, OsStringExt};
use std::path::PathBuf;
use std::sync::OnceLock;
use std::sync::atomic::{AtomicBool, Ordering};

use minhook::MinHook;
use windows_sys::Win32::System::SystemServices::DLL_PROCESS_ATTACH;

use crate::ffi::{
    self, CoreclrInitializeFn, CreateFileWFn, DllNotificationData, LoadLibraryExWFn
};
use crate::{CORECLR_HANDLE, REAL_INITIALIZE, hooked_initialize, redirect};

// only the first coreclr.dll
static DIRECT_HOOK_TRIED: AtomicBool = AtomicBool::new(false);

// the real CreateFileW and LoadLibraryExW, handed back by MinHook
static REAL_CREATE_FILE_W: OnceLock<CreateFileWFn> = OnceLock::new();
static REAL_LOAD_LIBRARY_EX_W: OnceLock<LoadLibraryExWFn> = OnceLock::new();

#[unsafe(no_mangle)]
#[allow(non_snake_case)]
pub extern "system" fn DllMain(_hinst: *mut c_void, reason: u32, _reserved: *mut c_void) -> i32 {
    if reason == DLL_PROCESS_ATTACH {
        log!(
            "shim loaded into {} (pid {})",
            ffi::module_path(std::ptr::null_mut()),
            std::process::id()
        );

        if !configured() {
            log!("MISAML_CORE_DIR is not set, cannot proceed, doing nothing");
            return 1;
        }

        install_dll_notification();
        install_redirect_hooks();
    }

    1 // true
}

// check if there is a MISAML to load
fn configured() -> bool {
    std::env::var("MISAML_CORE_DIR").is_ok_and(|path| std::path::Path::new(&path).is_dir())
}

fn install_dll_notification() {
    if !ffi::register_dll_notification(on_dll_loaded) {
        log!("ntdll has no DLL load notification, MISAML cannot start");
        return;
    }

    log!("watching for coreclr.dll to load");

    // just-in-case check
    let already_loaded = ffi::loaded_module("coreclr.dll");
    if !already_loaded.is_null() {
        log!("coreclr.dll was already loaded");
        hook_coreclr(already_loaded);
    }
}

// called by ntdll for every DLL that gets loaded into the game/process,
// on the loading thread with the loader lock being held, so keep this
// as minimal as possible
unsafe extern "system" fn on_dll_loaded(
    reason: u32,
    data: *const DllNotificationData,
    _context: *mut c_void,
) {
    if reason != ffi::DLL_NOTIFICATION_LOADED || data.is_null() {
        return;
    }

    let data = unsafe { &*data };
    let name = unsafe { ffi::unicode_to_string(data.base_dll_name) };

    if name.eq_ignore_ascii_case("coreclr.dll") {
        log!("coreclr.dll loaded from {}", unsafe {
            ffi::unicode_to_string(data.full_dll_name)
        });
        hook_coreclr(data.dll_base);
    } else {
        debug_log!("loaded {name} from {}", unsafe {
            ffi::unicode_to_string(data.full_dll_name)
        });
    }
}

// patches coreclr_initialize inside the new loaded coreclr.dll
fn hook_coreclr(module: *mut c_void) {
    if DIRECT_HOOK_TRIED.swap(true, Ordering::AcqRel) {
        return;
    }

    let target = unsafe { ffi::resolve(module, c"coreclr_initialize") };

    if target.is_null() {
        log!("coreclr.dll has no coreclr_initialize, nothing to patch");
        return;
    }

    let original = match unsafe { MinHook::create_hook(target, hooked_initialize as *mut c_void) } {
        Ok(original) => original,
        Err(e) => {
            log!("could not patch coreclr_initialize at {target:?}: {e:?}");
            return;
        }
    };

    // stored before patching, hooked_initialize needs real_fn the instant
    // something calls it
    let real_fn: CoreclrInitializeFn = unsafe { std::mem::transmute(original) };
    let _ = REAL_INITIALIZE.set(real_fn);
    let _ = CORECLR_HANDLE.set(module as usize);

    if let Err(e) = unsafe { MinHook::enable_hook(target) } {
        log!("could not enable the coreclr_initialize patch: {e:?}");
        return;
    }

    log!("coreclr_initialize patched at {target:?}");
}

// the game ships with trimmed framework DLLs, so redirect loads to the full,
// untrimmed copies bundled with MISAML. redirect.rs selects the files, and this
// hooks the APIs.
//
// Linux hooks open64 since CoreCLR maps assemblies from file descriptors,
// however, Windows CoreCLR checks files with CreateFileW, then maps them by path
// via LoadLibraryExW, both APIs must be hooked to redirect both steps consistently
fn install_redirect_hooks() {
    // no framework folder means nothing to redirect to, an issue
    let framework_dir = std::env::var("MISAML_FRAMEWORK_DIR").unwrap_or_default();
    if !std::path::Path::new(&framework_dir).is_dir() {
        log!("MISAML_FRAMEWORK_DIR is not set, framework redirect is disabled");
        return;
    }

    // both live in kernelbase.dll, kernel32.dll only forwards to them
    let create_file = ffi::system_export("kernelbase.dll", c"CreateFileW");
    let load_library = ffi::system_export("kernelbase.dll", c"LoadLibraryExW");

    if create_file.is_null() || load_library.is_null() {
        log!("could not find CreateFileW/LoadLibraryExW, framework redirect is disabled");
        return;
    }

    // both trampolines are stored before either hook goes live
    let created = (
        unsafe { MinHook::create_hook(create_file, hooked_create_file_w as *mut c_void) },
        unsafe { MinHook::create_hook(load_library, hooked_load_library_ex_w as *mut c_void) },
    );

    let (Ok(real_create_file), Ok(real_load_library)) = created else {
        log!("could not create the redirect hooks, the framework redirect is off: {created:?}");
        return;
    };

    // MinHook hands back pointers that run the original code, safe
    let _ = REAL_CREATE_FILE_W
        .set(unsafe { std::mem::transmute::<*mut c_void, CreateFileWFn>(real_create_file) });
    let _ = REAL_LOAD_LIBRARY_EX_W
        .set(unsafe { std::mem::transmute::<*mut c_void, LoadLibraryExWFn>(real_load_library) });

    for (name, target) in [
        ("CreateFileW", create_file),
        ("LoadLibraryExW", load_library),
    ] {
        match unsafe { MinHook::enable_hook(target) } {
            Ok(()) => log!("{name} hooked (framework redirect)"),
            Err(e) => log!("could not enable the {name} hook: {e:?}"),
        }
    }
}

// runs for every file the process opens/reads
unsafe extern "system" fn hooked_create_file_w(
    name: *const u16,
    access: u32,
    share: u32,
    security: *const c_void,
    disposition: u32,
    flags: u32,
    template: *mut c_void,
) -> *mut c_void {
    let Some(&real) = REAL_CREATE_FILE_W.get() else {
        // hook only goes live after this is set, can't happen
        return -1isize as *mut c_void; // INVALID_HANDLE_VALUE
    };

    match unsafe { redirected_name(name) } {
        Some(replacement) => unsafe {
            real(
                replacement.as_ptr(),
                access,
                share,
                security,
                disposition,
                flags,
                template,
            )
        },
        None => unsafe { real(name, access, share, security, disposition, flags, template) },
    }
}

// runs for every lib the process loads, this is the call CoreCLR maps its
// assemblies w/
unsafe extern "system" fn hooked_load_library_ex_w(
    name: *const u16,
    file: *mut c_void,
    flags: u32,
) -> *mut c_void {
    let Some(&real) = REAL_LOAD_LIBRARY_EX_W.get() else {
        // hook only goes live after this is set, can't happen
        return std::ptr::null_mut();
    };

    match unsafe { redirected_name(name) } {
        Some(replacement) => unsafe { real(replacement.as_ptr(), file, flags) },
        None => unsafe { real(name, file, flags) },
    }
}

// when an open of "name" should go instead, NUL term. None for every file
// that is not a framework dll of the game
unsafe fn redirected_name(name: *const u16) -> Option<Vec<u16>> {
    if name.is_null() {
        return None;
    }

    let units = unsafe { ffi::wide_str(name) };

    // nearly everything the game opens is not a dll, this runs for all of it,
    // so look at the end of the name b4 or else you allocate stuff that doesn't
    // need to be allocated
    let tail = units.get(units.len().checked_sub(4)?..)?;
    if !ffi::wide_eq_ignore_ascii_case(tail, ".dll") {
        return None;
    }

    let requested = PathBuf::from(std::ffi::OsString::from_wide(units));
    let replacement = redirect::redirect_target(&requested)?;

    debug_log!(
        "redirecting {} -> {}",
        requested.display(),
        replacement.display()
    );

    let mut wide: Vec<u16> = replacement.as_os_str().encode_wide().collect();
    wide.push(0);

    Some(wide)
}