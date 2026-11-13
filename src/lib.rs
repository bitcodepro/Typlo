#[cfg(any(windows, feature = "gui"))]
pub(crate) const MAX_IMAGE_PIXELS: u64 = 64_000_000;

#[cfg(feature = "gui")]
pub mod appearance;
pub mod backup;
pub mod config;
pub mod dictionary;
#[cfg(feature = "gui")]
pub mod dictionary_view;
pub mod edit;
pub mod hotkeys;
#[cfg(any(windows, target_os = "linux", target_os = "macos"))]
mod jobs;
pub mod layout;
pub mod lexicon;
#[cfg(target_os = "linux")]
pub mod linux;
pub mod locale;
#[cfg(target_os = "macos")]
pub mod macos;
pub mod runtime_log;
pub mod selection_tools;
#[cfg(all(
    feature = "gui",
    any(windows, target_os = "linux", target_os = "macos")
))]
pub mod selection_tools_view;
pub mod snippets;
pub mod spelling;
#[cfg(feature = "gui")]
pub mod spelling_view;
pub mod text_tools;
pub mod typing;
#[cfg(any(target_os = "linux", target_os = "macos"))]
pub mod unix_clipboard;
#[cfg(any(target_os = "linux", target_os = "macos"))]
pub mod unix_desktop;
#[cfg(any(target_os = "linux", target_os = "macos"))]
pub mod unix_history;
#[cfg(all(feature = "gui", any(target_os = "linux", target_os = "macos")))]
pub mod unix_history_view;
#[cfg(any(target_os = "linux", target_os = "macos"))]
pub mod unix_update;
#[cfg(any(windows, target_os = "linux", target_os = "macos"))]
mod update_package;

#[cfg(feature = "gui")]
pub mod capture;
#[cfg(all(windows, feature = "gui"))]
pub mod capture_view;
#[cfg(all(windows, feature = "gui"))]
pub mod history_view;
#[cfg(all(windows, feature = "gui"))]
pub mod lan_clipboard_view;
#[cfg(all(
    feature = "gui",
    any(windows, target_os = "linux", target_os = "macos")
))]
pub mod update_view;
#[cfg(windows)]
pub mod windows;

#[cfg(feature = "gui")]
fn restore_edit_origin(origin: usize) -> std::io::Result<()> {
    let error = || std::io::Error::other("Не удалось вернуться к исходному окну");
    if origin == 0 {
        return Err(error());
    }
    #[cfg(windows)]
    {
        use windows_sys::Win32::{Foundation::HWND, UI::WindowsAndMessaging::SetForegroundWindow};
        if unsafe { SetForegroundWindow(origin as HWND) } == 0 {
            return Err(error());
        }
    }
    #[cfg(target_os = "linux")]
    linux::focus_window(u32::try_from(origin).map_err(|_| error())?)?;
    #[cfg(target_os = "macos")]
    {
        use objc2_app_kit::{NSApplicationActivationOptions, NSRunningApplication};
        let application = NSRunningApplication::runningApplicationWithProcessIdentifier(
            i32::try_from(origin).map_err(|_| error())?,
        )
        .ok_or_else(error)?;
        if !application.activateWithOptions(NSApplicationActivationOptions::empty()) {
            return Err(error());
        }
    }
    Ok(())
}
