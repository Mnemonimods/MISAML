// every raw ptr and C ABI sig the shim needs. nothing outside this file
// should call libc/the Windows API directly or transmute a function pointer.

use std::ffi::{CStr, c_char, c_int, c_uint, c_void};

// function pointer types for symbols exported by CoreCLR, same signature on
// every platform (native CoreCLR ABI, UTF-8 strings everywhere)
pub type CoreclrInitializeFn = unsafe extern "C" fn(
    *const c_char,        // exePath
    *const c_char,        // appDomainFriendlyName
    c_int,                // propertyCount
    *const *const c_char, // propertyKeys
    *const *const c_char, // propertyValues
    *mut *mut c_void,     // hostHandle (out)
    *mut c_uint,          // domainId (out)
) -> c_int;

pub type CoreclrCreateDelegateFn = unsafe extern "C" fn(
    *mut c_void,      // hostHandle
    c_uint,           // domainId
    *const c_char,    // entryPointAssemblyName
    *const c_char,    // entryPointTypeName
    *const c_char,    // entryPointMethodName
    *mut *mut c_void, // delegate (out)
) -> c_int;

// signature of the managed entry point exposed through CoreCLR's native
// callable delegate
pub type BootstrapFn = unsafe extern "C" fn();

// Linux, dlsym/open64, with a LD_PRELOAD shim
#[cfg(target_os = "linux")]
pub type DlsymFn = unsafe extern "C" fn(*mut c_void, *const c_char) -> *mut c_void;

#[cfg(target_os = "linux")]
pub type Open64Fn = unsafe extern "C" fn(*const c_char, c_int, ...) -> c_int;

// resolve the real, unhooked dlsym, bypassing our own override
//
// have to use dlsym() with an explicit glibc symbol ver here, plain
// dlsym(RTLD_NEXT, "dlsym") resolves back into our own exported dlsym since
// that's what the shim relies on, obviously, and we don't want inf recursion.
#[cfg(target_os = "linux")]
pub fn real_dlsym() -> Option<DlsymFn> {
    let ptr = unsafe { libc::dlvsym(libc::RTLD_NEXT, c"dlsym".as_ptr(), c"GLIBC_2.2.5".as_ptr()) };

    if ptr.is_null() {
        return None;
    }

    // safe: dlvsym found w/ name "dlsym" at this glibc version,
    // signature's stable ABI on every glibc that matters here
    Some(unsafe { std::mem::transmute::<*mut c_void, DlsymFn>(ptr) })
}

// resolve a symbol off a specific lib handle, using the real dlsym
#[cfg(target_os = "linux")]
pub unsafe fn resolve(handle: *mut c_void, name: &CStr) -> *mut c_void {
    match real_dlsym() {
        Some(rd) => unsafe { rd(handle, name.as_ptr()) },
        None => std::ptr::null_mut(),
    }
}

// windows shit

// CreateFileW(name, access, share, security, disposition, flags, template) -> HANDLE
#[cfg(target_os = "windows")]
pub type CreateFileWFn = unsafe extern "system" fn(
    *const u16,
    u32,
    u32,
    *const c_void,
    u32,
    u32,
    *mut c_void,
) -> *mut c_void;

// LoadLibraryExW(name, file, flags) -> HMODULE
#[cfg(target_os = "windows")]
pub type LoadLibraryExWFn = unsafe extern "system" fn(*const u16, *mut c_void, u32) -> *mut c_void;

// UNICODE_STRING, length counts bytes not chars
#[cfg(target_os = "windows")]
#[repr(C)]
pub struct UnicodeString {
    pub length: u16,
    pub maximum_length: u16,
    pub buffer: *const u16,
}

// what ntdll passes to a DLL notif callback, LDR_DLL_NOTIFICATION_DATA,
// the LOADED and UNLOADED variants share one layout
#[cfg(target_os = "windows")]
#[repr(C)]
pub struct DllNotificationData {
    pub flags: u32,
    pub full_dll_name: *const UnicodeString,
    pub base_dll_name: *const UnicodeString,
    pub dll_base: *mut c_void,
    pub size_of_image: u32,
}

#[cfg(target_os = "windows")]
pub type DllNotificationFn =
    unsafe extern "system" fn(reason: u32, data: *const DllNotificationData, context: *mut c_void);

// LDR_DLL_NOTIFICATION_REASON_LOADED
#[cfg(target_os = "windows")]
pub const DLL_NOTIFICATION_LOADED: u32 = 1;

#[cfg(target_os = "windows")]
fn wide(text: &str) -> Vec<u16> {
    text.encode_utf16().chain(std::iter::once(0)).collect()
}

// resolve a symbol off a module handle via GetProcAddress
#[cfg(target_os = "windows")]
pub unsafe fn resolve(handle: *mut c_void, name: &CStr) -> *mut c_void {
    use windows_sys::Win32::System::LibraryLoader::GetProcAddress;

    match unsafe { GetProcAddress(handle, name.as_ptr().cast()) } {
        Some(f) => f as usize as *mut c_void,
        None => std::ptr::null_mut(),
    }
}

// handle of a module that is already loaded in this process, null if it is not
#[cfg(target_os = "windows")]
pub fn loaded_module(name: &str) -> *mut c_void {
    use windows_sys::Win32::System::LibraryLoader::GetModuleHandleW;

    unsafe { GetModuleHandleW(wide(name).as_ptr()) }
}

// export of a system dll that is already loaded, found by name. null if
// does not exist
#[cfg(target_os = "windows")]
pub fn system_export(module: &str, name: &CStr) -> *mut c_void {
    let handle = loaded_module(module);
    if handle.is_null() {
        return std::ptr::null_mut();
    }

    unsafe { resolve(handle, name) }
}

// a NUL terminated UTF-16 string as slice, without NUL terminator
#[cfg(target_os = "windows")]
pub unsafe fn wide_str<'a>(text: *const u16) -> &'a [u16] {
    let mut len = 0;
    while unsafe { *text.add(len) } != 0 {
        len += 1;
    }

    unsafe { std::slice::from_raw_parts(text, len) }
}

// does the UTF-16 "text" equal the ASCII "expected", ignore case
#[cfg(target_os = "windows")]
pub fn wide_eq_ignore_ascii_case(text: &[u16], expected: &str) -> bool {
    text.len() == expected.len()
        && text
            .iter()
            .zip(expected.bytes())
            .all(|(&unit, byte)| unit < 0x80 && (unit as u8).eq_ignore_ascii_case(&byte))
}

// full path of the file a module was loaded from, null = the exe itself
#[cfg(target_os = "windows")]
pub fn module_path(module: *mut c_void) -> String {
    use windows_sys::Win32::System::LibraryLoader::GetModuleFileNameW;

    let mut buf = [0u16; 520];
    let len = unsafe { GetModuleFileNameW(module, buf.as_mut_ptr(), buf.len() as u32) } as usize;

    String::from_utf16_lossy(&buf[..len.min(buf.len())])
}

// UNICODE_STRING -> String, empty on a null ptr
#[cfg(target_os = "windows")]
pub unsafe fn unicode_to_string(text: *const UnicodeString) -> String {
    if text.is_null() {
        return String::new();
    }

    let text = unsafe { &*text };
    if text.buffer.is_null() {
        return String::new();
    }

    let units = unsafe { std::slice::from_raw_parts(text.buffer, text.length as usize / 2) };
    String::from_utf16_lossy(units)
}

// asks ntdll to call "callback" every time a DLL finishes loading, on the
// thread that loaded it, before anything can call into said DLL.
// returns false if ntdll doesn't offer callbacks or fails, etc.
//
// i'm going insane.
#[cfg(target_os = "windows")]
pub fn register_dll_notification(callback: DllNotificationFn) -> bool {
    type RegisterFn = unsafe extern "system" fn(
        flags: u32,
        callback: DllNotificationFn,
        context: *mut c_void,
        cookie: *mut *mut c_void,
    ) -> i32;

    let ntdll = loaded_module("ntdll.dll");
    if ntdll.is_null() {
        return false;
    }

    let ptr = unsafe { resolve(ntdll, c"LdrRegisterDllNotification") };
    if ptr.is_null() {
        return false;
    }

    // safe, this is the documented signature of the export
    let register: RegisterFn = unsafe { std::mem::transmute(ptr) };

    let mut cookie: *mut c_void = std::ptr::null_mut();
    let status = unsafe { register(0, callback, std::ptr::null_mut(), &mut cookie) };

    // NTSTATUS, success and informational codes are >= 0
    status >= 0
}
