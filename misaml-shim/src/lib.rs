// MISAML native shim
//
// The purpose of this crate is to make the games trimmed CoreCLR usable,
// load one managed bootstrap dll, call it.
//
// Mods & Harmony are managed in MISAML.Core

use std::ffi::{CStr, CString, c_char, c_int, c_uint, c_void};
use std::path::PathBuf;
use std::sync::{
    OnceLock,
    atomic::{AtomicU8, Ordering},
};

use crate::ffi::{BootstrapFn, CoreclrCreateDelegateFn, CoreclrInitializeFn};

// reads MISAML_DEBUG once and remembers the answer
pub(crate) fn debug_enabled() -> bool {
    static ENABLED: OnceLock<bool> = OnceLock::new();
    *ENABLED.get_or_init(|| std::env::var_os("MISAML_DEBUG").is_some())
}

macro_rules! debug_log {
    ($($arg:tt)*) => {{
        if $crate::debug_enabled() {
            let message = format!($($arg)*);
            eprintln!("[misaml-shim {}] {message}", env!("CARGO_PKG_VERSION"));
        }
    }};
}

macro_rules! log {
    ($($arg:tt)*) => {{
        let message = format!($($arg)*);
        eprintln!("[misaml-shim {}] {message}", env!("CARGO_PKG_VERSION"));
    }};
}

mod ffi;
mod redirect;
mod tpa;

#[cfg(target_os = "linux")]
mod linux;

#[cfg(target_os = "windows")]
mod windows;

#[repr(u8)]
enum InitState {
    Uninitialized = 0,
    Initializing = 1,
    Initialized = 2,
}

// tracks hooked_initialize setup state
// prevents coreclr_initialize from running this setup twice
static INIT_STATE: AtomicU8 = AtomicU8::new(InitState::Uninitialized as u8);

// the platform entry points, fill these in when the
// game looks up coreclr_initialize
pub(crate) static REAL_INITIALIZE: OnceLock<CoreclrInitializeFn> = OnceLock::new();
pub(crate) static CORECLR_HANDLE: OnceLock<usize> = OnceLock::new();

pub(crate) unsafe extern "C" fn hooked_initialize(
    exe_path: *const c_char,
    app_domain_friendly_name: *const c_char,
    property_count: c_int,
    property_keys: *const *const c_char,
    property_values: *const *const c_char,
    host_handle: *mut *mut c_void,
    domain_id: *mut c_uint,
) -> c_int {
    // reset the state when setup fails before CoreCLR is initialized
    macro_rules! reset {
        () => {
            INIT_STATE.store(InitState::Uninitialized as u8, Ordering::Release);
        };
    }

    let Some(&real) = REAL_INITIALIZE.get() else {
        // shouldn't happen, dlsym always sets this first, fallback
        log!("real coreclr_initialize missing, cannot proceed");
        return -1;
    };

    // used to bail out and with the real unmodified call
    let vanilla = || unsafe {
        real(
            exe_path,
            app_domain_friendly_name,
            property_count,
            property_keys,
            property_values,
            host_handle,
            domain_id,
        )
    };

    if INIT_STATE
        .compare_exchange(
            InitState::Uninitialized as u8,
            InitState::Initializing as u8,
            Ordering::Acquire,
            Ordering::Relaxed,
        )
        .is_err()
    {
        debug_log!("coreclr_initialize called again within process, running unmodified");
        return vanilla();
    }

    // reject obviously invaid input before constructing slices from native arrs
    if property_count < 0
        || (property_count > 0 && (property_keys.is_null() || property_values.is_null()))
        || host_handle.is_null()
        || domain_id.is_null()
    {
        log!("invalid arguments from the runtime, running unmodified");
        reset!();
        return vanilla();
    }

    let count = property_count as usize;

    // CoreCLR owns the og arrays, so cpy the ptr arrs instead of muting the mem directly
    let keys: Vec<*const c_char> =
        unsafe { std::slice::from_raw_parts(property_keys, count) }.to_vec();

    let mut values: Vec<*const c_char> =
        unsafe { std::slice::from_raw_parts(property_values, count) }.to_vec();

    // has to stay alive until corecrl_initialize consumes
    let mut owned: Vec<CString> = Vec::new();

    // grab the game's runtime dir so redirect can scope open64 redir to exactly that folder
    for (key, value) in keys.iter().zip(values.iter()) {
        let key_str = unsafe { CStr::from_ptr(*key) }.to_string_lossy();
        if key_str == "APP_CONTEXT_BASE_DIRECTORY" {
            let value_str = unsafe { CStr::from_ptr(*value) }.to_string_lossy();
            redirect::set_game_runtime_dir(std::path::PathBuf::from(value_str.into_owned()));
        }
    }

    let core_dir = match std::env::var("MISAML_CORE_DIR") {
        Ok(path) if std::path::Path::new(&path).is_dir() => path,
        _ => {
            log!("MISAML_CORE_DIR missing or not a directory, running unmodified");
            reset!();
            return vanilla();
        }
    };

    let framework_assemblies = read_all_dir(std::env::var("MISAML_FRAMEWORK_DIR").ok());
    let payload_assemblies = read_all_dir(Some(core_dir));

    for (key, value) in keys.iter().zip(values.iter_mut()) {
        let key_str = unsafe { CStr::from_ptr(*key) }.to_string_lossy();

        if key_str == "System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault" {
            let cstr = CString::new("true").expect("static string, can't have NUL");
            *value = cstr.as_ptr();
            owned.push(cstr);
            continue;
        }

        if key_str != "TRUSTED_PLATFORM_ASSEMBLIES" {
            continue;
        }

        let existing = unsafe { CStr::from_ptr(*value) }.to_string_lossy();
        let new_tpa = tpa::build_tpa(&existing, &framework_assemblies, &payload_assemblies);

        debug_log!("new TPA built: {} entries", new_tpa.split(tpa::SEP).count());

        let Ok(cstr) = CString::new(new_tpa) else {
            log!("TPA value had a NUL byte, running unmodified");
            reset!();
            return vanilla();
        };

        *value = cstr.as_ptr();
        owned.push(cstr);
    }

    // CoreCLR gets our copied prop arrs with MISAML's assemblies appended to TPA
    let result = unsafe {
        real(
            exe_path,
            app_domain_friendly_name,
            property_count,
            keys.as_ptr(),
            values.as_ptr(),
            host_handle,
            domain_id,
        )
    };

    if result != 0 {
        log!("coreclr_initialize FAILED, hresult = {result:#x}");
        reset!();
        return result;
    }

    let host = unsafe { *host_handle };
    let domain = unsafe { *domain_id };

    debug_log!("coreclr_initialize OK, host_handle={host:?}, domain_id={domain}");

    if !bootstrap_managed(host, domain) {
        INIT_STATE.store(InitState::Uninitialized as u8, Ordering::Release);

        // CoreCLR itself initialized successfully, so return the success
        // the managed bootstrap has its own fallback
        return result;
    }

    INIT_STATE.store(InitState::Initialized as u8, Ordering::Release);

    result
}

fn read_all_dir(dir: Option<String>) -> Vec<PathBuf> {
    let Some(dir) = dir else { return Vec::new() };
    let Ok(entries) = std::fs::read_dir(&dir) else {
        debug_log!("couldn't read directory: {dir}");
        return Vec::new();
    };
    entries
        .filter_map(Result::ok)
        .map(|e| e.path())
        .filter(|p| p.extension().is_some_and(|e| e.eq_ignore_ascii_case("dll")))
        .collect()
}

fn bootstrap_managed(host_handle: *mut c_void, domain_id: c_uint) -> bool {
    let Some(&handle) = CORECLR_HANDLE.get() else {
        log!("no CoreCLR handle saved, cannot bootstrap managed code");
        return false;
    };
    let handle = handle as *mut c_void;

    // coreclr_create_delegate is exported by the same library that gave
    // coreclr_initialize, reuse the handle already saved
    let fn_ptr = unsafe { ffi::resolve(handle, c"coreclr_create_delegate") };
    if fn_ptr.is_null() {
        log!("coreclr_create_delegate not found, running unmodified");
        return false;
    }

    let create_delegate: CoreclrCreateDelegateFn = unsafe { std::mem::transmute(fn_ptr) };

    // the fully qualified managed method to invoke
    let mut delegate_ptr: *mut c_void = std::ptr::null_mut();

    let result = unsafe {
        create_delegate(
            host_handle,
            domain_id,
            c"MISAML.Bootstrap".as_ptr(),
            c"MISAML.Bootstrap.Loader".as_ptr(),
            c"Handoff".as_ptr(),
            &mut delegate_ptr,
        )
    };

    if result != 0 || delegate_ptr.is_null() {
        log!("coreclr_create_delegate FAILED, hresult = {result:#x}, running unmodified");
        return false;
    }

    debug_log!("calling MISAML.Bootstrap.Loader.Handoff() in MISAML.Bootstrap.dll");

    let bootstrap: BootstrapFn = unsafe { std::mem::transmute(delegate_ptr) };
    unsafe { bootstrap() };

    debug_log!("Handoff() returned");

    true
}
