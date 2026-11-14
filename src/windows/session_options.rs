use super::wide;
use std::{io, os::windows::ffi::OsStrExt};
use windows_sys::Win32::{
    Foundation::{ERROR_FILE_NOT_FOUND, ERROR_SUCCESS},
    System::{
        Power::{ES_CONTINUOUS, ES_DISPLAY_REQUIRED, ES_SYSTEM_REQUIRED, SetThreadExecutionState},
        Registry::{HKEY_CURRENT_USER, REG_SZ, RegDeleteKeyValueW, RegSetKeyValueW},
        SystemInformation::GetTickCount64,
    },
    UI::Input::KeyboardAndMouse::{GetLastInputInfo, LASTINPUTINFO, VK_F15},
};

pub fn keep_screen_on(enabled: bool) -> io::Result<()> {
    let flags = if enabled {
        ES_CONTINUOUS | ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED
    } else {
        ES_CONTINUOUS
    };
    if unsafe { SetThreadExecutionState(flags) } == 0 {
        return Err(io::Error::last_os_error());
    }
    Ok(())
}

pub(super) fn signal_idle_activity() {
    let mut input = LASTINPUTINFO {
        cbSize: size_of::<LASTINPUTINFO>() as u32,
        ..Default::default()
    };
    if unsafe { GetLastInputInfo(&mut input) } != 0
        && (unsafe { GetTickCount64() } as u32).wrapping_sub(input.dwTime) >= 180_000
    {
        let _ = super::input::send_edit_keys(None, VK_F15, 1);
    }
}

pub fn startup(enabled: bool) -> io::Result<()> {
    let path = wide(r"Software\Microsoft\Windows\CurrentVersion\Run");
    let label = wide("Typlo-Rust");
    let result = if enabled {
        let path_to_exe = super::update::startup_executable_for(&std::env::current_exe()?)?;
        let quote = u16::from(b'"');
        let executable: Vec<_> = [quote]
            .into_iter()
            .chain(path_to_exe.as_os_str().encode_wide())
            .chain([quote, 0])
            .collect();
        unsafe {
            RegSetKeyValueW(
                HKEY_CURRENT_USER,
                path.as_ptr(),
                label.as_ptr(),
                REG_SZ,
                executable.as_ptr().cast(),
                size_of_val(executable.as_slice()) as u32,
            )
        }
    } else {
        unsafe { RegDeleteKeyValueW(HKEY_CURRENT_USER, path.as_ptr(), label.as_ptr()) }
    };
    if result == ERROR_SUCCESS || (!enabled && result == ERROR_FILE_NOT_FOUND) {
        Ok(())
    } else {
        Err(io::Error::from_raw_os_error(result as i32))
    }
}
