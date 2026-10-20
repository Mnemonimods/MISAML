// Windows, start the game suspended, make it load the shim DLL,
// then let it run, the shim's DllMain installs the hooks during that load,
// which happens before the game's .NET runtime starts

use std::ffi::c_void;
use std::io;
use std::iter::once;
use std::os::windows::ffi::OsStrExt;
use std::os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle, RawHandle};
use std::os::windows::process::CommandExt;
use std::path::Path;
use std::process::{Child, Command, ExitStatus};

use anyhow::{Context, Result, anyhow, bail};
use windows_sys::Win32::Foundation::{HANDLE, INVALID_HANDLE_VALUE, WAIT_OBJECT_0};
use windows_sys::Win32::System::Diagnostics::Debug::WriteProcessMemory;
use windows_sys::Win32::System::Diagnostics::ToolHelp::{
    CreateToolhelp32Snapshot, TH32CS_SNAPTHREAD, THREADENTRY32, Thread32First, Thread32Next,
};
use windows_sys::Win32::System::LibraryLoader::{GetModuleHandleW, GetProcAddress};
use windows_sys::Win32::System::Memory::{
    MEM_COMMIT, MEM_RELEASE, MEM_RESERVE, PAGE_READWRITE, VirtualAllocEx, VirtualFreeEx,
};
use windows_sys::Win32::System::Threading::{
    CREATE_SUSPENDED, CreateRemoteThread, GetExitCodeThread, OpenThread, ResumeThread,
    THREAD_SUSPEND_RESUME, WaitForSingleObject,
};

const INJECT_TIMEOUT_MS: u32 = 30_000;

// starts the game with the shim loaded, waits for it to exit
pub fn launch(mut cmd: Command, shim: &Path) -> Result<ExitStatus> {
    cmd.creation_flags(CREATE_SUSPENDED);

    let mut child = cmd.spawn().context("could not start the game")?;

    // the game is frozen until resume() runs, so if anything fails here
    // kill or it hangs forever
    if let Err(err) = inject(&child, shim).and_then(|()| resume(child.id())) {
        let _ = child.kill();
        return Err(err);
    }

    child.wait().context("lost track of the game process")
}

// makes the game call LoadLibraryW(shim)
fn inject(child: &Child, shim: &Path) -> Result<()> {
    let process = child.as_raw_handle() as HANDLE;

    // the path as UTF-16 with a NUL at the end
    let path: Vec<u16> = shim.as_os_str().encode_wide().chain(once(0)).collect();
    let size = path.len() * 2;

    unsafe {
        let remote = VirtualAllocEx(
            process,
            std::ptr::null(),
            size,
            MEM_COMMIT | MEM_RESERVE,
            PAGE_READWRITE,
        );
        if remote.is_null() {
            bail!("VirtualAllocEx failed: {}", io::Error::last_os_error());
        }

        let result = load_in_game(process, remote, &path, size);

        // the game has the path copied, free the scratch
        VirtualFreeEx(process, remote, 0, MEM_RELEASE);

        result
    }
}

unsafe fn load_in_game(
    process: HANDLE,
    remote: *mut c_void,
    path: &[u16],
    size: usize,
) -> Result<()> {
    let written = unsafe {
        WriteProcessMemory(
            process,
            remote,
            path.as_ptr().cast(),
            size,
            std::ptr::null_mut(),
        )
    };
    if written == 0 {
        bail!("WriteProcessMemory failed: {}", io::Error::last_os_error());
    }

    // kernel32 sits at the same address in every process, so any
    // LoadLibraryW address is valid inside the game too
    let kernel32: Vec<u16> = "kernel32.dll\0".encode_utf16().collect();
    let module = unsafe { GetModuleHandleW(kernel32.as_ptr()) };
    let Some(load_library) = (unsafe { GetProcAddress(module, c"LoadLibraryW".as_ptr().cast()) })
    else {
        bail!("could not find LoadLibraryW");
    };

    // LoadLibraryW takes one ptr and returns a pointer, which is close
    // enough to a thread start routine for this to be possiblr
    let start: unsafe extern "system" fn(*mut c_void) -> u32 = unsafe {
        std::mem::transmute::<
            unsafe extern "system" fn() -> isize,
            unsafe extern "system" fn(*mut c_void) -> u32,
        >(load_library)
    };

    let thread = unsafe {
        CreateRemoteThread(
            process,
            std::ptr::null(),
            0,
            Some(start),
            remote,
            0,
            std::ptr::null_mut(),
        )
    };
    if thread.is_null() {
        bail!("CreateRemoteThread failed: {}", io::Error::last_os_error());
    }
    // closes the handle when this func ends
    let thread = unsafe { OwnedHandle::from_raw_handle(thread as RawHandle) };
    let thread_handle = thread.as_raw_handle() as HANDLE;

    if unsafe { WaitForSingleObject(thread_handle, INJECT_TIMEOUT_MS) } != WAIT_OBJECT_0 {
        bail!("timeout on loading the shim");
    }

    // the thread's exit code is LoadLibraryW's return value, 0 is fail
    let mut code = 0u32;
    if unsafe { GetExitCodeThread(thread_handle, &mut code) } == 0 || code == 0 {
        return Err(anyhow!("the game could not load the shim DLL"));
    }

    Ok(())
}

// game was started suspended, find its frozen thread and let it go
fn resume(pid: u32) -> Result<()> {
    let snapshot = unsafe { CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0) };
    if snapshot == INVALID_HANDLE_VALUE {
        bail!(
            "could not list the game's threads: {}",
            io::Error::last_os_error()
        );
    }
    let snapshot = unsafe { OwnedHandle::from_raw_handle(snapshot as RawHandle) };
    let snapshot = snapshot.as_raw_handle() as HANDLE;

    let mut entry: THREADENTRY32 = unsafe { std::mem::zeroed() };
    entry.dwSize = size_of::<THREADENTRY32>() as u32;

    let mut more = unsafe { Thread32First(snapshot, &mut entry) } != 0;
    while more {
        if entry.th32OwnerProcessID == pid {
            let thread = unsafe { OpenThread(THREAD_SUSPEND_RESUME, 0, entry.th32ThreadID) };

            if !thread.is_null() {
                let thread = unsafe { OwnedHandle::from_raw_handle(thread as RawHandle) };

                // returns how many times it was suspended before. 0 means it
                // was already running, u32::MAX is an error
                let before = unsafe { ResumeThread(thread.as_raw_handle() as HANDLE) };
                if before != u32::MAX && before > 0 {
                    return Ok(());
                }
            }
        }

        more = unsafe { Thread32Next(snapshot, &mut entry) } != 0;
    }

    bail!("could not find the game's main thread to resume it")
}
