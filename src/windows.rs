use crate::{
    config,
    dictionary::Dictionary,
    edit::{Editor, Session, Snapshot},
    hotkeys::{Action, Chord},
    layout::{self, Language},
    lexicon::Vocabulary,
    snippets::Snippets,
    text_tools,
    typing::{self, RecentInput},
};

mod accessible;
#[cfg(feature = "gui")]
pub(crate) mod capture_selector;
mod choice;
mod clipboard_edit;
pub mod clipio;
#[cfg(feature = "gui")]
pub mod desktop;
mod diagnostics;
pub mod history;
mod history_monitor;
mod input;
mod keyboard_profiles;
pub mod lan_clipboard;
#[cfg(feature = "gui")]
pub mod ocr;
mod phantom;
mod portable_profile;
pub fn prepare_portable_profile(profile: &Path) -> io::Result<()> {
    portable_profile::prepare(profile)
}
mod scintilla;
mod session_options;
mod snippet_menu;
mod spell_job;
mod tools_job;
pub mod update;
use std::{
    cell::RefCell,
    io,
    ops::Range,
    path::{Path, PathBuf},
    ptr::null_mut,
    time::{Duration, Instant},
};
use windows_sys::Win32::{
    Foundation::{
        CloseHandle, ERROR_ALREADY_EXISTS, GetLastError, HANDLE, HWND, LPARAM, LRESULT, WPARAM,
    },
    Globalization::{GetLocaleInfoW, LOCALE_SNATIVELANGNAME},
    System::{
        DataExchange::{
            AddClipboardFormatListener, GetClipboardOwner, RemoveClipboardFormatListener,
        },
        LibraryLoader::GetModuleHandleW,
        Threading::{
            CreateMutexW, OpenProcess, PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION,
            QueryFullProcessImageNameW,
        },
    },
    UI::{
        Accessibility::{HWINEVENTHOOK, SetWinEventHook, UnhookWinEvent},
        Controls::{EM_GETSEL, EM_REPLACESEL, EM_SETSEL},
        Input::KeyboardAndMouse::{
            GetKeyboardLayout, GetKeyboardLayoutList, HKL, IsWindowEnabled, MAPVK_VSC_TO_VK_EX,
            MOD_NOREPEAT, MapVirtualKeyExW, RegisterHotKey, ToUnicodeEx, UnregisterHotKey,
            VK_SHIFT,
        },
        Shell::{
            NIF_ICON, NIF_MESSAGE, NIF_TIP, NIM_ADD, NIM_DELETE, NOTIFYICONDATAW, Shell_NotifyIconW,
        },
        WindowsAndMessaging::*,
    },
};

const CONVERT: usize = Action::Convert as usize;
const UNDO: usize = Action::Undo as usize;
const SWITCH: usize = Action::Switch as usize;
const CASE: usize = Action::Case as usize;
const TRANSLITERATE: usize = Action::Transliterate as usize;
const SETTINGS: usize = 10;
const QUIT: usize = 11;
const TRAY_HISTORY: usize = 100;
const TRAY_SHORTCUTS: usize = 101;
const TRAY_TOOLS: usize = 102;
const TRAY_LAST_EDIT: usize = 119;
const TRAY_DICTIONARY: usize = 103;
const TRAY_CAPTURE_COPY: usize = 104;
const TRAY_CAPTURE_FILE: usize = 105;
const TRAY_KEEP_AWAKE: usize = 106;
const TRAY_PAUSE_TEN: usize = 107;
const TRAY_PAUSE_HOUR: usize = 108;
const TRAY_PAUSE_RESTART: usize = 109;
const TRAY_RESUME: usize = 110;
const UPDATE_MENU: usize = 21;
const TRAY_MESSAGE: u32 = WM_APP + 1;
const RELOAD_SETTINGS: u32 = WM_APP + 2;
const RELOAD_LAN_CLIPBOARD: u32 = WM_APP + 19;
const FOREGROUND_CHANGED: u32 = WM_APP + 3;
const DIAGNOSTICS: u32 = WM_APP + 15;
const TYPING_TIMER: usize = 1;
const UPDATE_TIMER: usize = 2;
const ACTIVITY_TIMER: usize = 3;
const INPUT_RETRY_TIMER: usize = 4;
const HOST_CLASS: &str = "TyploRustBackground";
const SETTINGS_WINDOW: &str = "TyploRustSettingsWindow";
const SCREENSHOT_EDITOR: &str = "TyploRustScreenshotEditor";
const MAX_TEXT: usize = 60_000;
const EM_GETTEXTMODE: u32 = WM_USER + 90;
const TM_PLAINTEXT: usize = 1;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct Target {
    foreground: HWND,
    edit: HWND,
    thread: u32,
    pid: u32,
}

#[derive(Default)]
struct WinEdit {
    fixed: Option<Target>,
    expected: Option<Target>,
    deadline: Option<Instant>,
    sequence: Option<usize>,
    recent: Option<String>,
}

impl WinEdit {
    fn target(&self) -> io::Result<Target> {
        if self
            .sequence
            .is_some_and(|sequence| sequence != input::sequence())
        {
            return Err(io::Error::new(
                io::ErrorKind::Interrupted,
                "Physical input changed",
            ));
        }
        if let Some(target) = self.fixed {
            return Ok(target);
        }
        let foreground = unsafe { GetForegroundWindow() };
        let mut pid = 0;
        let thread = unsafe { GetWindowThreadProcessId(foreground, &mut pid) };
        let mut info = GUITHREADINFO {
            cbSize: size_of::<GUITHREADINFO>() as u32,
            ..Default::default()
        };
        if thread == 0
            || unsafe { GetGUIThreadInfo(thread, &mut info) } == 0
            || info.hwndFocus.is_null()
        {
            return Err(io::Error::other("No focused editor"));
        }
        let edit_thread = unsafe { GetWindowThreadProcessId(info.hwndFocus, null_mut()) };
        if edit_thread == 0
            || pid == 0
            || unsafe { GetAncestor(info.hwndFocus, GA_ROOT) } != foreground
        {
            return Err(io::Error::new(
                io::ErrorKind::Interrupted,
                "Focused editor changed",
            ));
        }
        let target = Target {
            foreground,
            edit: info.hwndFocus,
            thread: edit_thread,
            pid,
        };
        if self.expected.is_some_and(|expected| expected != target) {
            return Err(io::Error::new(
                io::ErrorKind::Interrupted,
                "Focused editor changed",
            ));
        }
        Ok(target)
    }

    fn send(
        &self,
        window: HWND,
        message: u32,
        wparam: WPARAM,
        lparam: LPARAM,
    ) -> io::Result<usize> {
        let remaining = self
            .deadline
            .map_or(Duration::from_millis(100), |deadline| {
                deadline.saturating_duration_since(Instant::now())
            });
        if remaining.is_zero() {
            return Err(io::Error::new(
                io::ErrorKind::TimedOut,
                "Native edit deadline elapsed",
            ));
        }
        send_timeout(
            window,
            message,
            wparam,
            lparam,
            remaining.as_millis().clamp(1, 100) as u32,
        )
    }

    fn selection(&self, target: Target) -> io::Result<Range<usize>> {
        let packed = self.send(target.edit, EM_GETSEL, 0, 0)?;
        if packed as u32 == u32::MAX {
            return Err(io::Error::other(
                "Selection exceeds the supported native range",
            ));
        }
        Ok((packed & 0xffff)..((packed >> 16) & 0xffff))
    }

    fn is_plain_rich_edit(&self, target: Target) -> io::Result<bool> {
        let mut name = [0u16; 256];
        let length = unsafe { GetClassNameW(target.edit, name.as_mut_ptr(), name.len() as i32) };
        let style = unsafe { GetWindowLongW(target.edit, GWL_STYLE) };
        if length <= 0
            || unsafe { IsWindowUnicode(target.edit) } == 0
            || unsafe { IsWindowEnabled(target.edit) } == 0
            || style & (ES_PASSWORD | ES_READONLY) != 0
        {
            return Err(unsupported_editor());
        }
        if name[..length as usize] == [b'E' as u16, b'd' as u16, b'i' as u16, b't' as u16] {
            return Ok(false);
        }
        let name = String::from_utf16_lossy(&name[..length as usize]).to_ascii_lowercase();
        if name == "edit" || name.starts_with("windowsforms10.edit.") {
            return Ok(false);
        }
        let rich = matches!(name.as_str(), "richedit50w" | "richedit20w")
            || name.starts_with("windowsforms10.richedit50w.")
            || name.starts_with("windowsforms10.richedit20w.");
        // Scalar-only above WM_USER. WM_GETTEXT may return CRLF while RichEdit selection uses CR.
        if rich && self.send(target.edit, EM_GETTEXTMODE, 0, 0)? & TM_PLAINTEXT != 0 {
            return Ok(true);
        }
        Err(unsupported_editor())
    }

    fn supports_native_replacement(&mut self, target: Target) -> io::Result<()> {
        if self.is_plain_rich_edit(target)? {
            // RichEdit and Win32 disagree on multiline selection offsets.
            // Establish the strategy before either path changes the editor.
            self.begin_operation();
            let result = self.snapshot();
            self.deadline = None;
            result?;
        } else if self.send(target.edit, WM_GETTEXTLENGTH, 0, 0)? > MAX_TEXT {
            return Err(unsupported_editor());
        }
        Ok(())
    }
}

impl Editor for WinEdit {
    type Target = Target;

    fn begin_operation(&mut self) {
        self.deadline = Some(Instant::now() + Duration::from_millis(250));
    }

    fn recent_range(&self, snapshot: &Snapshot<Target>) -> Option<Range<usize>> {
        typing::verified_suffix(
            &snapshot.document,
            snapshot.selection.end,
            self.recent.as_deref()?,
        )
    }

    fn move_caret(&mut self, expected: &Snapshot<Target>, caret: usize) -> io::Result<()> {
        if self.snapshot()? != *expected || !is_text_boundary(&expected.document, caret) {
            return Err(io::Error::other("Editor changed before moving the caret"));
        }
        self.send(expected.target.edit, EM_SETSEL, caret, caret as LPARAM)?;
        Ok(())
    }

    fn snapshot(&mut self) -> io::Result<Snapshot<Target>> {
        let target = self.target()?;
        let rich = self.is_plain_rich_edit(target)?;
        let initial_selection = self.selection(target)?;
        let text_length = self.send(target.edit, WM_GETTEXTLENGTH, 0, 0)?;
        if text_length > MAX_TEXT {
            return Err(io::Error::other(
                "Editor exceeds the native text size limit",
            ));
        }
        let mut document = vec![0u16; text_length + 1];
        let read = self.send(
            target.edit,
            WM_GETTEXT,
            document.len(),
            document.as_mut_ptr() as LPARAM,
        )?;
        if read != text_length
            || read != self.send(target.edit, WM_GETTEXTLENGTH, 0, 0)?
            || self.target()? != target
            || self.selection(target)? != initial_selection
            || initial_selection.start > initial_selection.end
            || initial_selection.end > read
        {
            return Err(io::Error::other("Editor changed during capture"));
        }
        document.truncate(read);
        if rich && has_newline(&document) {
            return Err(unsupported_editor());
        }
        Ok(Snapshot {
            target,
            document,
            selection: initial_selection,
        })
    }

    fn replace(
        &mut self,
        expected: &Snapshot<Target>,
        range: Range<usize>,
        replacement: &[u16],
    ) -> io::Result<()> {
        if self.snapshot()? != *expected {
            return Err(io::Error::other("Editor changed before replacement"));
        }
        let target = expected.target;
        if expected.document.get(range.clone()).is_none()
            || !is_text_boundary(&expected.document, range.start)
            || !is_text_boundary(&expected.document, range.end)
            || replacement.contains(&0)
            || expected.document.len() - range.len() + replacement.len() > MAX_TEXT
            || (self.is_plain_rich_edit(target)? && has_newline(replacement))
        {
            return Err(unsupported_editor());
        }
        if range != expected.selection {
            self.send(target.edit, EM_SETSEL, range.start, range.end as LPARAM)?;
            let selected = self.snapshot()?;
            if selected.document != expected.document || selected.selection != range {
                return Err(io::Error::other("Editor changed while selecting the range"));
            }
        }
        if self.target()? != target || self.selection(target)? != range {
            return Err(io::Error::other("Target selection changed"));
        }
        let mut text = Vec::with_capacity(replacement.len() + 1);
        text.extend_from_slice(replacement);
        text.push(0);
        self.target()?;
        self.send(target.edit, EM_REPLACESEL, 1, text.as_ptr() as LPARAM)?;
        Ok(())
    }
}

fn has_newline(text: &[u16]) -> bool {
    text.iter().any(|&unit| matches!(unit, 10 | 13))
}

fn is_text_boundary(text: &[u16], index: usize) -> bool {
    index == 0
        || index == text.len()
        || !matches!(
            (text[index - 1], text[index]),
            (13, 10) | (0xd800..=0xdbff, 0xdc00..=0xdfff)
        )
}

fn unsupported_editor() -> io::Error {
    io::Error::new(
        io::ErrorKind::Unsupported,
        "Select text in Windows Edit or a single-line plain-text RichEdit field",
    )
}

fn send(window: HWND, message: u32, wparam: WPARAM, lparam: LPARAM) -> io::Result<usize> {
    send_timeout(window, message, wparam, lparam, 100)
}

fn send_timeout(
    window: HWND,
    message: u32,
    wparam: WPARAM,
    lparam: LPARAM,
    timeout: u32,
) -> io::Result<usize> {
    let mut result = 0;
    if unsafe {
        SendMessageTimeoutW(
            window,
            message,
            wparam,
            lparam,
            SMTO_BLOCK | SMTO_ABORTIFHUNG | SMTO_ERRORONEXIT,
            timeout,
            &mut result,
        )
    } == 0
    {
        return Err(io::Error::other(
            "The native editor did not acknowledge the operation",
        ));
    }
    Ok(result)
}

struct Resident {
    profile: PathBuf,
    settings: config::Config,
    edits: Session<Target>,
    bindings: Vec<(Action, Chord)>,
    direct: Vec<DirectBinding>,
    input: Option<input::Hooks>,
    recent: RecentInput<Target>,
    snippets: Snippets,
    pending_typing: Option<(usize, Instant)>,
    vocabulary: Vocabulary,
    dictionary: Dictionary,
    spelling: Option<spell_job::Job>,
    capture_process: Option<std::process::Child>,
    editor_process: Option<std::process::Child>,
    history_process: Option<std::process::Child>,
    history_previous_requested: bool,
    last_tray_target: Option<Target>,
    hotkeys_suspended: bool,
    history_monitor: Option<history_monitor::Monitor>,
    history_pause: Option<HistoryPause>,
    lan_listener: Option<lan_clipboard::Listener>,
    accessible: Option<accessible::Worker>,
    tools: Option<tools_job::Job>,
    last_automatic: diagnostics::Shared,
}

#[derive(Clone, Copy)]
enum HistoryPause {
    Until(Instant),
    UntilRestart,
}

impl HistoryPause {
    fn active(self) -> bool {
        match self {
            Self::Until(until) => until > Instant::now(),
            Self::UntilRestart => true,
        }
    }
}

impl Resident {
    fn hook_bindings(&self) -> input::Bindings {
        hook_bindings(
            &self.bindings,
            &self.direct,
            (!self.hotkeys_suspended).then_some(self.settings.drawing_shortcut),
        )
    }

    fn foreground_changed(&mut self, window: HWND) {
        let suspended = settings_window_focused();
        if self.hotkeys_suspended != suspended {
            self.hotkeys_suspended = suspended;
            if suspended {
                install_hotkeys(window, &mut self.bindings, &[]);
                clear_direct(window, &mut self.direct);
            } else if let Ok(bindings) = self.settings.hotkeys.bindings()
                && let Ok(direct) = self.settings.direct_bindings(&bindings)
            {
                report_conflicts(&install_hotkeys(window, &mut self.bindings, &bindings));
                install_direct(window, &mut self.direct, &number_direct(&direct));
            }
            if let Some(hooks) = &self.input {
                hooks.update(self.hook_bindings());
            }
        }
        if let Ok(target) = WinEdit::default().target()
            && tray_target(target)
        {
            self.last_tray_target = Some(target);
        }
    }

    fn accessible_edit(&mut self, host: HWND, request: accessible::Request) -> io::Result<()> {
        if self
            .accessible
            .as_ref()
            .is_none_or(accessible::Worker::is_finished)
        {
            self.accessible = Some(accessible::Worker::start(
                host,
                self.profile.clone(),
                self.last_automatic.clone(),
            )?);
        }
        if let Some(worker) = &mut self.accessible {
            let operation = request.command.diagnostic_name();
            let result = worker.enqueue(request);
            if result.is_err() {
                let mut timing = diagnostics::Timing::new(&self.last_automatic, operation);
                timing.field("Queue");
                timing.finish(&result, |_| false);
            }
            result?;
        }
        Ok(())
    }

    fn open_history(&mut self) -> io::Result<()> {
        self.open_history_at(WinEdit::default().target().ok())
    }

    fn open_history_at(&mut self, target: Option<Target>) -> io::Result<()> {
        use std::{
            io::Write,
            process::{Command, Stdio},
        };
        if let Some(history) = &mut self.history_process
            && history.try_wait()?.is_none()
        {
            if !self.history_previous_requested
                && let Some(input) = &mut history.stdin
            {
                input.write_all(b"p")?;
                self.history_previous_requested = true;
            }
            return Ok(());
        }
        let origin = target.map_or([0; 3], |t| {
            [t.foreground as usize, t.edit as usize, t.pid as usize]
        });
        let mut cursor = windows_sys::Win32::Foundation::POINT::default();
        let has_cursor = unsafe { GetPhysicalCursorPos(&mut cursor) } != 0;
        self.history_process = Some(
            Command::new(std::env::current_exe()?.with_file_name("typlo-settings.exe"))
                .arg("--history")
                .arg(&self.profile)
                .args(origin.map(|value| value.to_string()))
                .args(
                    has_cursor
                        .then(|| [cursor.x.to_string(), cursor.y.to_string()])
                        .into_iter()
                        .flatten(),
                )
                .stdin(Stdio::piped())
                .spawn()?,
        );
        self.history_previous_requested = false;
        Ok(())
    }

    fn capture(&mut self, action: Action) -> io::Result<()> {
        let executable = std::env::current_exe()?.with_file_name("typlo-settings.exe");
        if action == Action::CaptureEdit {
            let editor = marked_window(SCREENSHOT_EDITOR);
            if !editor.is_null() {
                unsafe {
                    if IsIconic(editor) != 0 {
                        ShowWindow(editor, SW_RESTORE);
                    }
                    SetForegroundWindow(editor);
                }
                return Ok(());
            }
            if let Some(child) = &mut self.editor_process
                && child.try_wait()?.is_none()
            {
                return Ok(());
            }
            self.editor_process = Some(
                std::process::Command::new(executable)
                    .arg("--screenshot-editor")
                    .arg(&self.profile)
                    .spawn()?,
            );
            return Ok(());
        }
        if let Some(child) = &mut self.capture_process
            && child.try_wait()?.is_none()
        {
            return Ok(());
        }
        let destination = if action == Action::CaptureFile {
            "file"
        } else {
            "clipboard"
        };
        self.capture_process = Some(
            std::process::Command::new(executable)
                .arg("--capture")
                .arg(&self.profile)
                .arg(destination)
                .arg(self.settings.capture_shape.arg())
                .spawn()?,
        );
        Ok(())
    }

    fn cancel_typing_edit(&mut self, window: HWND) {
        if self.pending_typing.take().is_some() {
            unsafe { KillTimer(window, TYPING_TIMER) };
        }
    }

    fn drain_input(&mut self, window: HWND) {
        let Some(hooks) = &self.input else { return };
        if self.hotkeys_suspended {
            for _ in hooks.events.try_iter() {}
            return;
        }
        let mut latest = None;
        for event in hooks.events.try_iter() {
            let target = event.target();
            let key = event.input();
            let target = target.filter(|target| unsafe {
                GetWindowLongW(target.edit, GWL_STYLE) & ES_PASSWORD == 0
            });
            self.recent.push(target, event.sequence, key, event.time);
            latest = Some((target, event.sequence, key));
        }
        let Some((target, sequence, key)) = latest else {
            return;
        };
        if self.spelling.as_ref().is_some_and(|job| !job.review) {
            self.spelling = None;
        }
        self.cancel_typing_edit(window);
        if let Some(target) = target
            && self
                .recent
                .text(&target, sequence, Instant::now())
                .is_some_and(|text| {
                    (!self.snippets.is_empty() && self.snippets.matches(text).is_some())
                        || (matches!(key, typing::Input::Typed(' '))
                            && (self.settings.auto_correct
                                || self
                                    .settings
                                    .applications
                                    .iter()
                                    .any(|rule| rule.auto_correct == Some(true))))
                })
        {
            self.pending_typing = Some((sequence, Instant::now() + Duration::from_millis(30)));
            unsafe { SetTimer(window, TYPING_TIMER, 30, None) };
        }
    }

    fn apply_pending_typing_edit(&mut self, window: HWND) -> io::Result<()> {
        self.drain_input(window);
        let Some((sequence, deadline)) = self.pending_typing else {
            return Ok(());
        };
        if Instant::now() < deadline {
            return Ok(());
        }
        self.cancel_typing_edit(window);
        let mut editor = WinEdit {
            sequence: Some(sequence),
            ..Default::default()
        };
        let target = editor.target()?;
        editor.expected = Some(target);
        let process = process_name(target.pid).unwrap_or_default();
        let Some(text) = self.recent.text(&target, sequence, Instant::now()) else {
            return Ok(());
        };
        match editor.supports_native_replacement(target) {
            Ok(_) => {}
            Err(error) if error.kind() == io::ErrorKind::Unsupported => {
                let snippet = self
                    .settings
                    .snippets_for(&process)
                    .then(|| self.snippets.matches(text))
                    .flatten()
                    .map(|(trigger, template)| (trigger.to_owned(), template.clone()));
                let word = self
                    .settings
                    .auto_correct_for(&process)
                    .then(|| typing::last_word(text))
                    .flatten()
                    .filter(|word| word.ends_with(' '))
                    .map(str::to_owned);
                if snippet.is_none() && word.is_none() {
                    return Ok(());
                }
                let request = accessible::Request {
                    target,
                    sequence,
                    cycle: self.settings.cycle_for(&process).to_vec(),
                    switch_layout: self.settings.switch_layout,
                    recent: None,
                    command: accessible::Command::Typing {
                        snippet,
                        word,
                        protect: self.settings.protect_technical,
                    },
                };
                return self.accessible_edit(window, request);
            }
            Err(error) => return Err(error),
        }
        if self.settings.snippets_for(&process)
            && let Some((trigger, template)) = self.snippets.matches(text)
            && self.edits.expand_snippet(&mut editor, trigger, template)?
        {
            self.recent.reset();
            return Ok(());
        }
        if self.settings.auto_correct_for(&process)
            && let Some(source) = active_language(target)
            && let Some(word) = typing::last_word(text)
        {
            let started = Instant::now();
            let mut timing = diagnostics::Timing::new(&self.last_automatic, "Auto-correction");
            timing.field("Windows Edit / RichEdit");
            let mut reason = crate::lexicon::Decision::Unverified;
            let result = self.edits.correct_word(&mut editor, word, |token| {
                timing.phase("range verification");
                let (suggestion, decision) = self.vocabulary.decision(
                    &self.dictionary,
                    token,
                    source,
                    self.settings.cycle_for(&process),
                    self.settings.protect_technical,
                )?;
                timing.phase("dictionary");
                reason = decision;
                Ok(suggestion)
            });
            timing.phase("verified replacement");
            timing.finish(&result, Option::is_some);
            timing.dictionary(reason);
            let decision = match &result {
                Ok(Some(_)) => crate::lexicon::Decision::Applied,
                Ok(None) => reason,
                Err(_) => crate::lexicon::Decision::Failed,
            };
            diagnostics::automatic(
                &self.last_automatic,
                started,
                decision,
                Some(source),
                result.as_ref().ok().copied().flatten(),
            );
            if let Some(language) = result? {
                self.recent.reset();
                if self.settings.switch_layout && editor.target()? == target {
                    switch_layout(target, language);
                }
            }
        }
        Ok(())
    }
}

thread_local! {
    static RESIDENT: RefCell<Option<Resident>> = const { RefCell::new(None) };
}

pub fn reload_settings() {
    let window = unsafe { FindWindowW(wide(HOST_CLASS).as_ptr(), std::ptr::null()) };
    if !window.is_null() {
        unsafe {
            PostMessageW(window, RELOAD_SETTINGS, 0, 0);
        }
    }
}

pub fn reload_lan_clipboard() {
    let window = unsafe { FindWindowW(wide(HOST_CLASS).as_ptr(), std::ptr::null()) };
    if !window.is_null() {
        unsafe {
            PostMessageW(window, RELOAD_LAN_CLIPBOARD, 0, 0);
        }
    }
}

pub fn run(profile: &Path, report: Option<&Path>) -> io::Result<()> {
    run_resident(profile, report, report.is_some(), false, report.is_none())
}

pub fn run_updated(profile: &Path, report: &Path) -> io::Result<()> {
    run_resident(profile, Some(report), false, false, false)
}

pub fn run_previous(profile: &Path) -> io::Result<()> {
    run_resident(profile, None, false, false, false)
}

pub fn run_measurement(profile: &Path, report: &Path) -> io::Result<()> {
    use windows_sys::Win32::System::{
        StationsAndDesktops::{GetThreadDesktop, GetUserObjectInformationW, UOI_NAME},
        Threading::GetCurrentThreadId,
    };
    let mut name = [0u16; 128];
    let mut needed = 0;
    let found = unsafe {
        GetUserObjectInformationW(
            GetThreadDesktop(GetCurrentThreadId()),
            UOI_NAME,
            name.as_mut_ptr().cast(),
            size_of_val(&name) as u32,
            &mut needed,
        )
    };
    if found == 0 || !String::from_utf16_lossy(&name).starts_with("TyploBench-") {
        return Err(io::Error::other(
            "Use the private desktop created by measure-full-windows.ps1",
        ));
    }
    run_resident(profile, Some(report), false, true, false)
}

fn run_resident(
    profile: &Path,
    report: Option<&Path>,
    probe: bool,
    measured: bool,
    auto_update: bool,
) -> io::Result<()> {
    let _instance = if probe || measured {
        None
    } else {
        Some(SingleInstance::acquire()?)
    };
    if !probe && !measured {
        portable_profile::prepare(profile)?;
        crate::runtime_log::initialize(profile, "resident");
    }
    let _session =
        (!probe && !measured).then(|| crate::runtime_log::ResidentSession::start(profile));
    let instance = unsafe { GetModuleHandleW(null_mut()) };
    let class = wide(if probe {
        "TyploRustBenchmark"
    } else {
        HOST_CLASS
    });
    let window_class = WNDCLASSW {
        lpfnWndProc: Some(window_proc),
        hInstance: instance,
        lpszClassName: class.as_ptr(),
        ..Default::default()
    };
    if unsafe { RegisterClassW(&window_class) } == 0 {
        return Err(io::Error::last_os_error());
    }
    let window = unsafe {
        CreateWindowExW(
            0,
            class.as_ptr(),
            class.as_ptr(),
            WS_POPUP,
            0,
            0,
            0,
            0,
            null_mut(),
            null_mut(),
            instance,
            std::ptr::null(),
        )
    };
    if window.is_null() {
        return Err(io::Error::last_os_error());
    }
    let icon = tray_data(window);
    let mut foreground_hook: HWINEVENTHOOK = null_mut();
    let result = (|| {
        std::fs::create_dir_all(profile)?;
        let settings = load_resident_config(profile)?;
        if !probe {
            if settings.keep_screen_on
                && let Err(error) = session_options::keep_screen_on(true)
            {
                crate::runtime_error!("Typlo Rust: {error}");
            }
            if settings.start_with_windows
                && let Err(error) = session_options::startup(true)
            {
                crate::runtime_error!("Typlo Rust: {error}");
            }
        }
        let snippets = Snippets::compile_for(&settings.snippets, &available_layouts())?;
        let mut bindings = Vec::new();
        let mut direct = Vec::new();
        if !probe {
            let requested = settings.hotkeys.bindings()?;
            let layouts = settings.direct_bindings(&requested)?;
            let rejected = install_hotkeys(window, &mut bindings, &requested);
            report_conflicts(&rejected);
            install_direct(window, &mut direct, &number_direct(&layouts));
        }
        let mut lan_listener = None;
        if !probe && let Err(error) = lan_clipboard::Listener::refresh(&mut lan_listener, profile) {
            crate::runtime_error!("Typlo LAN clipboard: {error}");
        }
        let drawing = (!settings_window_focused()).then_some(settings.drawing_shortcut);
        RESIDENT.replace(Some(Resident {
            profile: profile.to_owned(),
            settings,
            edits: Session::default(),
            input: if probe {
                None
            } else {
                input::Hooks::start(window, hook_bindings(&bindings, &direct, drawing))
                    .map_err(|error| crate::runtime_error!("Typlo Rust keyboard hook: {error}"))
                    .ok()
            },
            bindings,
            direct,
            recent: RecentInput::default(),
            snippets,
            pending_typing: None,
            vocabulary: Vocabulary::installed()?,
            dictionary: Dictionary::new(profile),
            spelling: None,
            capture_process: None,
            editor_process: None,
            history_process: None,
            history_previous_requested: false,
            last_tray_target: WinEdit::default()
                .target()
                .ok()
                .filter(|&target| tray_target(target)),
            hotkeys_suspended: false,
            history_monitor: if probe {
                None
            } else {
                history_monitor::Monitor::start(profile.to_owned())
                    .map_err(|error| crate::runtime_error!("Typlo Rust history monitor: {error}"))
                    .ok()
            },
            history_pause: None,
            lan_listener,
            accessible: None,
            tools: None,
            last_automatic: Default::default(),
        }));
        if !probe {
            if unsafe { AddClipboardFormatListener(window) } == 0 {
                crate::runtime_error!(
                    "Typlo Rust: Windows did not register clipboard notifications"
                );
            }
            if unsafe { Shell_NotifyIconW(NIM_ADD, &icon) } == 0 && !measured {
                crate::runtime_error!(
                    "Unable to create the tray icon: {}",
                    io::Error::last_os_error()
                );
            } else if !measured {
                crate::runtime_log::write("INFO", format_args!("Tray icon registered"));
            }
            foreground_hook = unsafe {
                SetWinEventHook(
                    EVENT_SYSTEM_FOREGROUND,
                    EVENT_SYSTEM_FOREGROUND,
                    null_mut(),
                    Some(on_foreground),
                    0,
                    0,
                    WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS,
                )
            };
            if foreground_hook.is_null() {
                crate::runtime_error!(
                    "Typlo Rust foreground monitor: {}",
                    io::Error::last_os_error()
                );
            }
            unsafe {
                if auto_update {
                    SetTimer(window, UPDATE_TIMER, 2_000, None);
                }
                SetTimer(window, INPUT_RETRY_TIMER, 5_000, None);
                if RESIDENT.with_borrow(|resident| {
                    resident
                        .as_ref()
                        .is_some_and(|resident| resident.settings.keep_screen_on)
                }) {
                    SetTimer(window, ACTIVITY_TIMER, 30_000, None);
                }
            }
        }
        if let Some(report) = report {
            std::fs::write(report, "Typlo Rust background ready\n")?;
        }
        let mut message = MSG::default();
        loop {
            let result = unsafe { GetMessageW(&mut message, null_mut(), 0, 0) };
            if result <= 0 {
                return if result == 0 {
                    Ok(())
                } else {
                    Err(io::Error::last_os_error())
                };
            }
            unsafe {
                TranslateMessage(&message);
                DispatchMessageW(&message);
            }
        }
    })();
    unsafe {
        if let Some(mut resident) = RESIDENT.take() {
            for (action, chord) in resident.bindings {
                if !hook_shortcut(chord) {
                    UnregisterHotKey(window, action as i32);
                }
            }
            clear_direct(window, &mut resident.direct);
        }
        if !foreground_hook.is_null() {
            UnhookWinEvent(foreground_hook);
        }
        Shell_NotifyIconW(NIM_DELETE, &icon);
        if !probe {
            RemoveClipboardFormatListener(window);
        }
        DestroyWindow(window);
    }
    result
}

fn hook_shortcut(chord: Chord) -> bool {
    chord.intercepted() || input::physical_scan(chord.key).is_some()
}

fn install_hotkeys(
    window: HWND,
    active: &mut Vec<(Action, Chord)>,
    requested: &[(Action, Chord)],
) -> Vec<Action> {
    active.retain(|binding| {
        if requested.contains(binding) {
            return true;
        }
        if !hook_shortcut(binding.1) {
            unsafe {
                UnregisterHotKey(window, binding.0 as i32);
            }
        }
        false
    });
    let mut rejected = Vec::new();
    for &(action, chord) in requested {
        if active.contains(&(action, chord)) {
            continue;
        }
        if hook_shortcut(chord)
            || unsafe {
                RegisterHotKey(
                    window,
                    action as i32,
                    chord.modifiers | MOD_NOREPEAT,
                    chord.key,
                )
            } != 0
        {
            active.push((action, chord));
        } else {
            rejected.push(action);
        }
    }
    rejected
}

fn report_conflicts(actions: &[Action]) {
    for action in actions {
        crate::runtime_error!(
            "Typlo Rust: Windows rejected the shortcut for {}",
            action.label()
        );
    }
}

#[derive(Clone, Copy)]
struct DirectBinding {
    id: i32,
    layout: Language,
    chord: Chord,
}

fn number_direct(layouts: &[(Language, Chord)]) -> Vec<DirectBinding> {
    layouts
        .iter()
        .enumerate()
        .map(|(index, &(layout, chord))| DirectBinding {
            id: 512 + index as i32,
            layout,
            chord,
        })
        .collect()
}

fn clear_direct(window: HWND, active: &mut Vec<DirectBinding>) {
    for binding in active.drain(..) {
        if !hook_shortcut(binding.chord) {
            unsafe {
                UnregisterHotKey(window, binding.id);
            }
        }
    }
}

fn install_direct(
    window: HWND,
    active: &mut Vec<DirectBinding>,
    requested: &[DirectBinding],
) -> bool {
    let mut success = true;
    for &binding in requested {
        if hook_shortcut(binding.chord)
            || unsafe {
                RegisterHotKey(
                    window,
                    binding.id,
                    binding.chord.modifiers | MOD_NOREPEAT,
                    binding.chord.key,
                )
            } != 0
        {
            active.push(binding);
        } else {
            success = false;
            crate::runtime_error!(
                "Typlo Rust: Windows rejected the shortcut for {}",
                binding.layout.name()
            );
        }
    }
    success
}

fn hook_bindings(
    bindings: &[(Action, Chord)],
    direct: &[DirectBinding],
    drawing: Option<crate::hotkeys::DrawingShortcut>,
) -> input::Bindings {
    let chords = bindings
        .iter()
        .map(|entry| entry.1)
        .chain(direct.iter().map(|entry| entry.chord))
        .collect();
    let physical = bindings
        .iter()
        .map(|&(action, chord)| (action as usize, chord))
        .chain(
            direct
                .iter()
                .map(|binding| (binding.id as usize, binding.chord)),
        )
        .filter(|&(_, chord)| input::physical_scan(chord.key).is_some())
        .collect();
    input::Bindings {
        chords,
        physical,
        drawing,
    }
}

fn rebind_hotkeys(
    window: HWND,
    active: &mut Vec<(Action, Chord)>,
    requested: &[(Action, Chord)],
) -> Vec<Action> {
    let previous = active.clone();
    let mut rejected = install_hotkeys(window, active, requested);
    if !rejected.is_empty() {
        rejected.extend(install_hotkeys(window, active, &previous));
    }
    rejected
}

struct SingleInstance(HANDLE);

impl SingleInstance {
    fn acquire() -> io::Result<Self> {
        let name = wide(r"Local\Typlo.Rust.Resident.v1");
        let handle = unsafe { CreateMutexW(std::ptr::null(), 0, name.as_ptr()) };
        if handle.is_null() {
            return Err(io::Error::last_os_error());
        }
        let error = unsafe { GetLastError() };
        let instance = Self(handle);
        if error == ERROR_ALREADY_EXISTS {
            return Err(io::Error::new(
                io::ErrorKind::AlreadyExists,
                "Typlo Rust is already running",
            ));
        }
        Ok(instance)
    }
}

impl Drop for SingleInstance {
    fn drop(&mut self) {
        unsafe {
            CloseHandle(self.0);
        }
    }
}

fn tray_data(window: HWND) -> NOTIFYICONDATAW {
    let mut icon = NOTIFYICONDATAW {
        cbSize: size_of::<NOTIFYICONDATAW>() as u32,
        hWnd: window,
        uID: 1,
        uFlags: NIF_ICON | NIF_MESSAGE | NIF_TIP,
        uCallbackMessage: TRAY_MESSAGE,
        hIcon: unsafe {
            LoadIconW(
                GetModuleHandleW(std::ptr::null()),
                std::ptr::without_provenance(1),
            )
        },
        ..Default::default()
    };
    if icon.hIcon.is_null() {
        icon.hIcon = unsafe { LoadIconW(null_mut(), IDI_APPLICATION) };
    }
    let label = wide("Typlo2.0");
    icon.szTip[..label.len()].copy_from_slice(&label);
    icon
}

unsafe extern "system" fn window_proc(
    window: HWND,
    message: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        dispatch_window(window, message, wparam, lparam)
    }))
    .unwrap_or_else(|_| {
        RESIDENT.with(|slot| {
            if let Ok(mut resident) = slot.try_borrow_mut()
                && let Some(resident) = resident.as_mut()
            {
                resident.cancel_typing_edit(window);
                resident.recent.reset();
            }
        });
        crate::runtime_error!(
            "Typlo Rust: Windows command {message:#x} was interrupted and recovered"
        );
        0
    })
}

fn dispatch_window(window: HWND, message: u32, wparam: WPARAM, lparam: LPARAM) -> LRESULT {
    static TASKBAR_CREATED: std::sync::OnceLock<u32> = std::sync::OnceLock::new();
    let taskbar_created = *TASKBAR_CREATED
        .get_or_init(|| unsafe { RegisterWindowMessageW(wide("TaskbarCreated").as_ptr()) });
    if message != 0 && message == taskbar_created {
        if unsafe { Shell_NotifyIconW(NIM_ADD, &tray_data(window)) } == 0 {
            crate::runtime_error!(
                "Explorer restarted; could not restore the tray icon: {}",
                io::Error::last_os_error()
            );
        } else {
            crate::runtime_log::write(
                "INFO",
                format_args!("Tray icon restored after Explorer restart"),
            );
        }
        return 0;
    }
    match message {
        WM_HOTKEY | input::PHYSICAL_HOTKEY => {
            let result = RESIDENT.with_borrow_mut(|resident| -> io::Result<()> {
                let Some(resident) = resident else {
                    return Ok(());
                };
                if resident.hotkeys_suspended || settings_window_focused() {
                    return Ok(());
                }
                let modifiers = lparam as u32 & 0x000f;
                let key = (lparam as u32 >> 16) & 0xffff;
                let direct = resident
                    .direct
                    .iter()
                    .find(|binding| {
                        binding.id as usize == wparam
                            && binding.chord.modifiers == modifiers
                            && binding.chord.key == key
                            && hook_shortcut(binding.chord) == (message == input::PHYSICAL_HOTKEY)
                    })
                    .map(|binding| binding.layout);
                if direct.is_none()
                    && !resident.bindings.iter().any(|(action, chord)| {
                        *action as usize == wparam
                            && chord.modifiers == modifiers
                            && chord.key == key
                            && hook_shortcut(*chord) == (message == input::PHYSICAL_HOTKEY)
                    })
                {
                    return Ok(());
                }
                resident.drain_input(window);
                let action = resident
                    .bindings
                    .iter()
                    .find(|(action, _)| *action as usize == wparam)
                    .map(|(action, _)| *action);
                if let Some(
                    action @ (Action::Choices | Action::AcceptChoice | Action::CancelChoice),
                ) = action
                {
                    if choice::key(window, action, WinEdit::default().target().ok())
                        || action != Action::Choices
                    {
                        return Ok(());
                    }
                } else {
                    choice::cancel(window);
                }
                resident.cancel_typing_edit(window);
                resident.spelling = None;
                resident.tools = None;
                if action == Some(Action::ClearDrawing) {
                    phantom::clear();
                    return Ok(());
                }
                if action == Some(Action::SnippetMenu) {
                    if snippet_menu::focus_existing() {
                        return Ok(());
                    }
                    let target = WinEdit::default().target()?;
                    let process = process_name(target.pid).unwrap_or_default();
                    let can_insert = resident.settings.snippets_for(&process);
                    return resident.accessible_edit(
                        window,
                        accessible::Request {
                            target,
                            sequence: input::sequence(),
                            command: accessible::Command::PrepareSnippetMenu {
                                snippets: resident.settings.snippets.clone(),
                                can_insert,
                            },
                            cycle: Vec::new(),
                            switch_layout: false,
                            recent: None,
                        },
                    );
                }
                if wparam == Action::History as usize {
                    return resident.open_history();
                }
                if let Some((action, _)) = resident
                    .bindings
                    .iter()
                    .copied()
                    .find(|(action, _)| *action as usize == wparam)
                    && matches!(
                        action,
                        Action::CaptureCopy | Action::CaptureFile | Action::CaptureEdit
                    )
                {
                    return resident.capture(action);
                }
                let sequence = input::sequence();
                let mut editor = WinEdit {
                    sequence: Some(sequence),
                    ..Default::default()
                };
                if let Some((action, _)) = resident
                    .bindings
                    .iter()
                    .copied()
                    .find(|(action, _)| *action as usize == wparam)
                    && matches!(action, Action::AddWord | Action::Choices | Action::Tools)
                {
                    let target = editor.target()?;
                    editor.expected = Some(target);
                    let recent = resident.recent.text(&target, sequence, Instant::now());
                    if action != Action::Tools
                        && editor
                            .is_plain_rich_edit(target)
                            .is_err_and(|error| error.kind() == io::ErrorKind::Unsupported)
                    {
                        let process = process_name(target.pid).unwrap_or_default();
                        let request = accessible::Request {
                            target,
                            sequence,
                            command: accessible::Command::Tools(action),
                            cycle: resident.settings.cycle_for(&process).to_vec(),
                            switch_layout: resident.settings.switch_layout,
                            recent: recent.and_then(typing::last_word).map(str::to_owned),
                        };
                        resident.tools = None;
                        resident.recent.reset();
                        resident.accessible_edit(window, request)?;
                        return Ok(());
                    }
                    let automatic = resident
                        .last_automatic
                        .lock()
                        .ok()
                        .and_then(|report| report.automatic.front().copied());
                    resident.tools = tools_job::Job::start(
                        window,
                        action,
                        editor,
                        tools_job::Context {
                            profile: &resident.profile,
                            settings: &resident.settings,
                            corpus: &mut resident.vocabulary,
                            dictionary: &resident.dictionary,
                            recent,
                            last_automatic_micros: automatic.map(|entry| entry.elapsed_us),
                            last_automatic_reason: automatic.map(|entry| entry.reason),
                        },
                    )?;
                    resident.recent.reset();
                    return Ok(());
                }
                if wparam == Action::Spelling as usize || wparam == Action::SpellingReview as usize
                {
                    let review = wparam == Action::SpellingReview as usize;
                    editor.expected = editor.target().ok();
                    resident.recent.reset();
                    if let Some(target) = editor.expected
                        && editor
                            .is_plain_rich_edit(target)
                            .is_err_and(|error| error.kind() == io::ErrorKind::Unsupported)
                    {
                        resident.spelling = None;
                        resident.accessible_edit(
                            window,
                            accessible::Request {
                                target,
                                sequence,
                                command: accessible::Command::Spelling { review },
                                cycle: Vec::new(),
                                switch_layout: false,
                                recent: None,
                            },
                        )?;
                        return Ok(());
                    }
                    resident.spelling = spell_job::Job::start(
                        window,
                        resident.profile.clone(),
                        editor,
                        sequence,
                        review,
                    )?;
                    return Ok(());
                }
                let direct_layout = direct.or_else(|| match wparam {
                    id if id == Action::English as usize => Some(resolve_layout(Language::En)),
                    id if id == Action::Russian as usize => Some(resolve_layout(Language::Ru)),
                    id if id == Action::Ukrainian as usize => Some(resolve_layout(Language::Uk)),
                    _ => None,
                });
                if wparam == CONVERT || wparam == SWITCH || direct_layout.is_some() {
                    if let Some(worker) = &mut resident.accessible {
                        worker.cancel_typing();
                    }
                    let mut timing =
                        diagnostics::Timing::new(&resident.last_automatic, "Manual conversion");
                    timing.field("Windows Edit / RichEdit");
                    let target = editor.target()?;
                    editor.expected = Some(target);
                    let settings = &resident.settings;
                    let source = active_language(target);
                    let process = if settings.applications.is_empty() {
                        String::new()
                    } else {
                        process_name(target.pid).unwrap_or_default()
                    };
                    let cycle = settings.cycle_for(&process);
                    if wparam == SWITCH {
                        timing.skip();
                        resident.recent.reset();
                        switch_cycle(&editor, target, cycle, source)?;
                        return Ok(());
                    }
                    if let Err(error) = editor.supports_native_replacement(target) {
                        if error.kind() != io::ErrorKind::Unsupported {
                            return Err(error);
                        }
                        let request = accessible::Request {
                            target,
                            sequence,
                            cycle: cycle.to_vec(),
                            command: direct_layout
                                .map_or(accessible::Command::Convert, accessible::Command::To),
                            switch_layout: settings.switch_layout,
                            recent: settings
                                .convert_last_word
                                .then(|| resident.recent.text(&target, sequence, Instant::now()))
                                .flatten()
                                .and_then(typing::last_word)
                                .map(str::to_owned),
                        };
                        resident.recent.reset();
                        timing.skip();
                        return resident.accessible_edit(window, request);
                    }
                    if settings.convert_last_word {
                        editor.recent = resident
                            .recent
                            .text(&target, sequence, Instant::now())
                            .and_then(typing::last_word)
                            .map(str::to_owned);
                    }
                    resident.recent.reset();
                    timing.phase("field and selection strategy");
                    let converted = if let Some(layout) = direct_layout {
                        resident.edits.convert_to(&mut editor, layout, source)
                    } else {
                        resident.edits.convert(&mut editor, cycle, source)
                    };
                    timing.phase("conversion and verified replacement");
                    timing.finish(&converted, Option::is_some);
                    if let Some(language) = converted?
                        && settings.switch_layout
                        && editor.target()? == target
                    {
                        switch_layout(target, language);
                    }
                } else if wparam == UNDO {
                    let mut timing = diagnostics::Timing::new(&resident.last_automatic, "Undo");
                    timing.field("Windows Edit / RichEdit");
                    resident.recent.reset();
                    let target = editor.target()?;
                    editor.expected = Some(target);
                    match editor.supports_native_replacement(target) {
                        Ok(_) => {
                            let undone = resident.edits.undo(&mut editor);
                            timing.phase("verified Undo");
                            timing.finish(&undone, |value| *value);
                            undone?;
                        }
                        Err(error) if error.kind() == io::ErrorKind::Unsupported => {
                            timing.skip();
                            return resident.accessible_edit(
                                window,
                                accessible::Request {
                                    target,
                                    sequence,
                                    cycle: Vec::new(),
                                    switch_layout: false,
                                    recent: None,
                                    command: accessible::Command::Undo,
                                },
                            );
                        }
                        Err(error) => return Err(error),
                    }
                } else if wparam == CASE || wparam == TRANSLITERATE {
                    resident.recent.reset();
                    let target = editor.target()?;
                    editor.expected = Some(target);
                    if let Err(error) = editor.is_plain_rich_edit(target) {
                        if error.kind() != io::ErrorKind::Unsupported {
                            return Err(error);
                        }
                        return resident.accessible_edit(
                            window,
                            accessible::Request {
                                target,
                                sequence,
                                cycle: Vec::new(),
                                switch_layout: false,
                                recent: None,
                                command: if wparam == CASE {
                                    accessible::Command::Case
                                } else {
                                    accessible::Command::Transliterate
                                },
                            },
                        );
                    }
                    let transform: fn(&str) -> String = if wparam == CASE {
                        text_tools::cycle_case
                    } else {
                        text_tools::transliterate
                    };
                    resident.edits.transform(&mut editor, |text, previous| {
                        Some((transform(text), previous))
                    })?;
                }
                Ok(())
            });
            if let Err(error) = result {
                // A rejected target must not take focus away from the application.
                crate::runtime_error!("Typlo Rust: {error}");
            }
            0
        }
        RELOAD_LAN_CLIPBOARD => {
            RESIDENT.with_borrow_mut(|resident| {
                if let Some(resident) = resident
                    && let Err(error) = lan_clipboard::Listener::refresh(
                        &mut resident.lan_listener,
                        &resident.profile,
                    )
                {
                    crate::runtime_error!("Typlo LAN clipboard: {error}");
                }
            });
            0
        }
        RELOAD_SETTINGS => {
            choice::cancel(window);
            RESIDENT.with_borrow_mut(|resident| {
                if let Some(resident) = resident {
                    match load_resident_config(&resident.profile).and_then(|settings| {
                        let hotkeys = settings.hotkeys.bindings()?;
                        let direct = settings.direct_bindings(&hotkeys)?;
                        Ok((
                            hotkeys,
                            direct,
                            Snippets::compile_for(&settings.snippets, &available_layouts())?,
                            settings,
                        ))
                    }) {
                        Ok((requested, direct, snippets, settings)) => {
                            if settings.keep_screen_on != resident.settings.keep_screen_on
                                && let Err(error) =
                                    session_options::keep_screen_on(settings.keep_screen_on)
                            {
                                crate::runtime_error!("Typlo Rust: {error}");
                            }
                            sync_activity_timer(window, settings.keep_screen_on);
                            if settings.start_with_windows != resident.settings.start_with_windows
                                && let Err(error) =
                                    session_options::startup(settings.start_with_windows)
                            {
                                crate::runtime_error!("Typlo Rust: {error}");
                            }
                            if !resident.hotkeys_suspended {
                                let prior_static = resident.bindings.clone();
                                let prior_direct = resident.direct.clone();
                                clear_direct(window, &mut resident.direct);
                                let conflicts =
                                    rebind_hotkeys(window, &mut resident.bindings, &requested);
                                report_conflicts(&conflicts);
                                if !conflicts.is_empty()
                                    || !install_direct(
                                        window,
                                        &mut resident.direct,
                                        &number_direct(&direct),
                                    )
                                {
                                    clear_direct(window, &mut resident.direct);
                                    report_conflicts(&install_hotkeys(
                                        window,
                                        &mut resident.bindings,
                                        &prior_static,
                                    ));
                                    install_direct(window, &mut resident.direct, &prior_direct);
                                }
                            }
                            resident.cancel_typing_edit(window);
                            resident.spelling = None;
                            resident.tools = None;
                            if let Some(monitor) = &resident.history_monitor {
                                monitor.invalidate();
                            }
                            resident.recent.reset();
                            resident.snippets = snippets;
                            resident.settings = settings;
                            if let Some(hooks) = &resident.input {
                                hooks.update(resident.hook_bindings());
                            }
                        }
                        Err(error) => crate::runtime_error!("Typlo Rust: {error}"),
                    }
                }
            });
            0
        }
        DIAGNOSTICS => RESIDENT.with_borrow_mut(|resident| {
            let Some(resident) = resident else {
                return 0;
            };
            if wparam == 1 {
                resident.cancel_typing_edit(window);
                resident.accessible = None;
                let healthy = resident.vocabulary.retry_search().is_ok()
                    && resident.dictionary.contains("hello", "en").is_ok();
                if let Ok(mut report) = resident.last_automatic.lock() {
                    report.dictionary_healthy = Some(healthy);
                }
            }
            resident.last_automatic.lock().ok().is_some_and(|report| {
                report
                    .publish(&resident.profile.join("diagnostics.json"))
                    .is_ok()
            }) as LRESULT
        }),
        FOREGROUND_CHANGED => {
            choice::cancel(window);
            RESIDENT.with_borrow_mut(|resident| {
                let Some(resident) = resident else { return };
                resident.foreground_changed(window);
                resident.recent.reset();
                resident.cancel_typing_edit(window);
                if resident.spelling.as_ref().is_some_and(|job| !job.review) {
                    resident.spelling = None;
                }
                if resident.settings.applications.is_empty() {
                    return;
                }
                let editor = WinEdit::default();
                if let Ok(target) = editor.target()
                    && target.foreground as usize == wparam
                    && let Some(process) = process_name(target.pid)
                    && let Some(language) = resident
                        .settings
                        .layout_on_entry(&process, active_language(target))
                    && editor.target().is_ok_and(|current| current == target)
                {
                    switch_layout(target, language);
                }
            });
            0
        }
        input::READY => {
            RESIDENT.with_borrow_mut(|resident| {
                if let Some(resident) = resident {
                    resident.drain_input(window);
                }
            });
            0
        }
        choice::CHANGED => {
            choice::repaint();
            0
        }
        snippet_menu::READY => {
            let profile =
                RESIDENT.with_borrow(|resident| resident.as_ref().map(|item| item.profile.clone()));
            if let Some(profile) = profile
                && let Err(error) = snippet_menu::show(window, &profile)
            {
                crate::runtime_error!("Typlo Rust snippet menu: {error}");
            }
            0
        }
        snippet_menu::APPLY => {
            let result = RESIDENT.with_borrow_mut(|resident| -> io::Result<()> {
                let (Some(resident), Some(item)) = (resident, snippet_menu::take_insertion())
                else {
                    return Ok(());
                };
                let target = item.origin.target();
                let sequence = input::sequence();
                let mut editor = WinEdit {
                    expected: Some(target),
                    sequence: Some(sequence),
                    ..Default::default()
                };
                editor.target()?;
                resident.recent.reset();
                match item.selection {
                    snippet_menu::Selection::Native {
                        document,
                        selection,
                    } => {
                        resident.edits.insert_snippet(
                            &mut editor,
                            Snapshot {
                                target,
                                document,
                                selection,
                            },
                            &item.template,
                        )?;
                    }
                    selection => {
                        let command = match selection {
                            snippet_menu::Selection::Accessible(capture) => {
                                accessible::Command::InsertMenuSnippet {
                                    capture,
                                    template: item.template,
                                }
                            }
                            snippet_menu::Selection::Scintilla(capture) => {
                                accessible::Command::InsertScintillaSnippet {
                                    capture,
                                    template: item.template,
                                }
                            }
                            snippet_menu::Selection::Native { .. } => unreachable!(),
                        };
                        resident.accessible_edit(
                            window,
                            accessible::Request {
                                target,
                                sequence,
                                command,
                                cycle: Vec::new(),
                                switch_layout: false,
                                recent: None,
                            },
                        )?;
                    }
                }
                Ok(())
            });
            if let Err(error) = result {
                crate::runtime_error!("Typlo Rust snippet insertion: {error}");
            }
            0
        }
        phantom::EVENT => {
            phantom::handle(wparam, lparam);
            0
        }
        input::CYCLE => {
            RESIDENT.with_borrow_mut(|resident| {
                let Some(resident) = resident else { return };
                if !resident
                    .bindings
                    .iter()
                    .any(|(action, chord)| *action == Action::Switch && chord.key == 0)
                {
                    return;
                }
                resident.drain_input(window);
                resident.cancel_typing_edit(window);
                resident.recent.reset();
                resident.spelling = None;
                let mut editor = WinEdit {
                    sequence: Some(lparam as usize),
                    ..Default::default()
                };
                if let Ok(target) = editor.target()
                    && target.foreground as usize == wparam
                {
                    editor.expected = Some(target);
                    let process = if resident.settings.applications.is_empty() {
                        String::new()
                    } else {
                        process_name(target.pid).unwrap_or_default()
                    };
                    let cycle = resident.settings.cycle_for(&process);
                    let _ = switch_cycle(&editor, target, cycle, active_language(target));
                }
            });
            0
        }
        WM_CLIPBOARDUPDATE => {
            RESIDENT.with_borrow(|resident| {
                let Some(resident) = resident else { return };
                if input::selection_active()
                    || !resident.settings.history_enabled
                    || resident.history_pause.is_some_and(HistoryPause::active)
                {
                    return;
                }
                let sequence = clipio::sequence();
                let owner = unsafe { GetClipboardOwner() };
                let mut pid = 0;
                unsafe { GetWindowThreadProcessId(owner, &mut pid) };
                if pid == std::process::id() {
                    return;
                }
                let source = if clipio::is_screenshot() {
                    Some("Typlo".to_owned())
                } else {
                    if owner.is_null() {
                        unsafe { GetWindowThreadProcessId(GetForegroundWindow(), &mut pid) };
                    }
                    process_name(pid)
                        .and_then(|path| Path::new(&path).file_stem()?.to_str().map(str::to_owned))
                };
                if let (Some(monitor), Some(source)) = (&resident.history_monitor, source)
                    && resident.settings.history_allowed(&source)
                {
                    monitor.enqueue(owner, sequence, &resident.settings, source);
                }
            });
            0
        }
        spell_job::READY => {
            RESIDENT.with_borrow_mut(|resident| {
                let Some(resident) = resident else { return };
                if let Some(job) = &resident.spelling {
                    match job.apply(&mut resident.edits) {
                        Ok(false) => {}
                        Ok(true) => resident.spelling = None,
                        Err(error) => {
                            resident.spelling = None;
                            if error.kind() != io::ErrorKind::Interrupted {
                                crate::runtime_error!("Typlo Rust: {error}");
                            }
                        }
                    }
                }
            });
            0
        }
        tools_job::READY => {
            RESIDENT.with_borrow_mut(|resident| {
                let Some(resident) = resident else { return };
                if let Some(job) = &resident.tools {
                    match job.finish(&mut resident.edits, resident.settings.switch_layout) {
                        Ok(false) => {}
                        Ok(true) => resident.tools = None,
                        Err(error) => {
                            resident.tools = None;
                            crate::runtime_error!("Typlo Rust: {error}");
                        }
                    }
                }
            });
            0
        }
        WM_TIMER if wparam == TYPING_TIMER => {
            RESIDENT.with_borrow_mut(|resident| {
                if let Some(resident) = resident
                    && let Err(error) = resident.apply_pending_typing_edit(window)
                    && !matches!(
                        error.kind(),
                        io::ErrorKind::Interrupted | io::ErrorKind::Unsupported
                    )
                {
                    crate::runtime_error!("Typlo Rust: {error}");
                }
            });
            0
        }
        WM_TIMER if wparam == ACTIVITY_TIMER => {
            session_options::signal_idle_activity();
            0
        }
        WM_TIMER if wparam == INPUT_RETRY_TIMER => {
            RESIDENT.with_borrow_mut(|resident| {
                if let Some(resident) = resident
                    && resident
                        .input
                        .as_ref()
                        .is_none_or(input::Hooks::is_finished)
                {
                    resident.input = input::Hooks::start(window, resident.hook_bindings())
                        .map_err(|error| crate::runtime_error!("Typlo Rust keyboard hook: {error}"))
                        .ok();
                }
            });
            0
        }
        WM_TIMER if wparam == UPDATE_TIMER => {
            unsafe {
                KillTimer(window, UPDATE_TIMER);
            }
            if let Err(error) = std::thread::Builder::new()
                .name("typlo-auto-update".into())
                .spawn(|| {
                    if let Err(error) = update::auto_install_in_helper() {
                        crate::runtime_error!("Typlo update: {error}");
                    }
                })
            {
                crate::runtime_error!("Typlo update worker: {error}");
            }
            0
        }
        TRAY_MESSAGE => {
            if lparam as u32 == WM_LBUTTONUP {
                open_ui("--profile");
            }
            if matches!(lparam as u32, WM_RBUTTONUP | WM_CONTEXTMENU) {
                tray_menu(window);
            }
            0
        }
        WM_CLOSE => {
            let reason = if wparam == update::SHUTDOWN_FOR_UPDATE {
                "Shutdown requested by updater"
            } else {
                "Shutdown requested by Windows or another process"
            };
            crate::runtime_log::write("INFO", format_args!("{reason}"));
            unsafe { PostQuitMessage(0) };
            0
        }
        _ => unsafe { DefWindowProcW(window, message, wparam, lparam) },
    }
}

fn tray_menu(window: HWND) {
    use crate::locale::{set_language, text as tr};
    let origin = WinEdit::default()
        .target()
        .ok()
        .filter(|&target| tray_target(target))
        .or_else(|| {
            RESIDENT.with_borrow(|resident| {
                resident
                    .as_ref()
                    .and_then(|resident| resident.last_tray_target)
            })
        });
    let Some(items) = RESIDENT.with_borrow(|resident| {
        let resident = resident.as_ref()?;
        set_language(&resident.settings.ui_language);
        let mut items = vec![
            (
                TRAY_HISTORY,
                tr("tray.history", "Clipboard history").into(),
                MF_STRING,
            ),
            (SETTINGS, tr("tray.settings", "Settings").into(), MF_STRING),
            (
                TRAY_SHORTCUTS,
                tr("shortcuts.menu", "Keyboard shortcuts…").into(),
                MF_STRING,
            ),
            (0, String::new(), MF_SEPARATOR),
            (
                TRAY_TOOLS,
                tr("tools.title", "Typlo tools").into(),
                MF_STRING,
            ),
            (
                TRAY_LAST_EDIT,
                resident
                    .last_automatic
                    .lock()
                    .ok()
                    .and_then(|report| report.latest_summary())
                    .unwrap_or_else(|| tr("rust.edit.noAttempts", "No recent text edits").into()),
                MF_STRING,
            ),
            (
                TRAY_DICTIONARY,
                tr("manage.title", "Personal dictionary").into(),
                MF_STRING,
            ),
            (
                TRAY_CAPTURE_COPY,
                tr("capture.copy", "Copy screenshot").into(),
                MF_STRING,
            ),
            (
                TRAY_CAPTURE_FILE,
                tr("capture.save", "Save screenshot").into(),
                MF_STRING,
            ),
            (0, String::new(), MF_SEPARATOR),
            (
                TRAY_KEEP_AWAKE,
                tr("tray.keepAwake", "Prevent screen lock").into(),
                if resident.settings.keep_screen_on {
                    MF_CHECKED
                } else {
                    MF_STRING
                },
            ),
        ];
        match resident.history_pause.filter(|pause| pause.active()) {
            Some(HistoryPause::UntilRestart) => {
                items.push((
                    TRAY_RESUME,
                    tr(
                        "tray.resumeRestart",
                        "Resume history (paused until restart)",
                    )
                    .into(),
                    MF_STRING,
                ));
            }
            Some(HistoryPause::Until(until)) => {
                let mut now = windows_sys::Win32::Foundation::SYSTEMTIME::default();
                unsafe {
                    windows_sys::Win32::System::SystemInformation::GetLocalTime(&mut now);
                }
                let duration = until
                    .saturating_duration_since(Instant::now())
                    .as_secs()
                    .div_ceil(60);
                let minutes =
                    (u64::from(now.wHour) * 60 + u64::from(now.wMinute) + duration) % 1440;
                let clock = format!("{:02}:{:02}", minutes / 60, minutes % 60);
                let label = tr(
                    "tray.resumeUntil",
                    "Resume history (paused until {0:HH:mm})",
                )
                .replace("{0:HH:mm}", &clock);
                items.push((TRAY_RESUME, label, MF_STRING));
            }
            None => {
                for (id, text) in [
                    (TRAY_PAUSE_TEN, tr("tray.pause10", "For 10 minutes")),
                    (TRAY_PAUSE_HOUR, tr("tray.pause60", "For 1 hour")),
                    (
                        TRAY_PAUSE_RESTART,
                        tr("tray.pauseRestart", "Until Typlo restarts"),
                    ),
                ] {
                    items.push((id, text.into(), MF_STRING));
                }
            }
        }
        items.push((0, String::new(), MF_SEPARATOR));
        items.push((
            UPDATE_MENU,
            tr("update.check", "Check for updates").to_owned(),
            MF_STRING,
        ));
        items.push((QUIT, tr("tray.exit", "Exit").into(), MF_STRING));
        Some(items)
    }) else {
        return;
    };

    let command = unsafe {
        let menu = CreatePopupMenu();
        if menu.is_null() {
            return;
        }
        for (id, label, flags) in items {
            AppendMenuW(menu, flags, id, wide(&label).as_ptr());
        }
        let mut cursor = windows_sys::Win32::Foundation::POINT::default();
        let selected = if GetCursorPos(&mut cursor) != 0 {
            SetForegroundWindow(window);
            let selected = TrackPopupMenu(
                menu,
                TPM_RIGHTBUTTON | TPM_RETURNCMD,
                cursor.x,
                cursor.y,
                0,
                window,
                std::ptr::null(),
            );
            PostMessageW(window, WM_NULL, 0, 0);
            selected as usize
        } else {
            0
        };
        DestroyMenu(menu);
        selected
    };
    let result = match command {
        SETTINGS => {
            open_ui("--profile");
            Ok(())
        }
        UPDATE_MENU => {
            open_ui("--open-updates");
            Ok(())
        }
        TRAY_SHORTCUTS => {
            open_ui("--shortcuts");
            Ok(())
        }
        TRAY_TOOLS | TRAY_LAST_EDIT => {
            open_ui("--tools");
            Ok(())
        }
        TRAY_DICTIONARY => {
            open_ui("--dictionary");
            Ok(())
        }
        QUIT => {
            crate::runtime_log::write("INFO", format_args!("Quit selected in the tray menu"));
            unsafe {
                PostQuitMessage(0);
            }
            Ok(())
        }
        _ => RESIDENT.with_borrow_mut(|resident| {
            let Some(resident) = resident else {
                return Ok(());
            };
            match command {
                TRAY_HISTORY => resident.open_history_at(origin),
                TRAY_CAPTURE_COPY => resident.capture(Action::CaptureCopy),
                TRAY_CAPTURE_FILE => resident.capture(Action::CaptureFile),
                TRAY_KEEP_AWAKE => {
                    let mut settings = config::load(&resident.profile)?;
                    let previous = resident.settings.keep_screen_on;
                    settings.keep_screen_on = !previous;
                    session_options::keep_screen_on(settings.keep_screen_on)?;
                    if let Err(error) = config::save(&resident.profile, &settings) {
                        let _ = session_options::keep_screen_on(previous);
                        return Err(error);
                    }
                    resident.settings.keep_screen_on = settings.keep_screen_on;
                    sync_activity_timer(window, settings.keep_screen_on);
                    Ok(())
                }
                TRAY_PAUSE_TEN | TRAY_PAUSE_HOUR | TRAY_PAUSE_RESTART | TRAY_RESUME => {
                    resident.history_pause = match command {
                        TRAY_PAUSE_TEN => Some(HistoryPause::Until(
                            Instant::now() + Duration::from_secs(600),
                        )),
                        TRAY_PAUSE_HOUR => Some(HistoryPause::Until(
                            Instant::now() + Duration::from_secs(3600),
                        )),
                        TRAY_PAUSE_RESTART => Some(HistoryPause::UntilRestart),
                        _ => None,
                    };
                    if let Some(monitor) = &resident.history_monitor {
                        monitor.invalidate();
                    }
                    Ok(())
                }
                _ => Ok(()),
            }
        }),
    };
    if let Err(error) = result {
        crate::runtime_error!("Typlo: {error}");
    }
}

#[cfg(feature = "gui")]
pub fn paste_history(
    owner: HWND,
    origin: [usize; 3],
    clip: &clipio::Clip,
    plain: bool,
    paste: bool,
) -> io::Result<()> {
    use windows_sys::Win32::UI::Input::KeyboardAndMouse::*;
    let foreground = origin[0] as HWND;
    let edit = origin[1] as HWND;
    let pid = origin[2] as u32;
    if paste
        && (foreground.is_null()
            || edit.is_null()
            || unsafe { SetForegroundWindow(foreground) } == 0)
    {
        return Err(io::Error::other("Исходное окно недоступно"));
    }
    let sequence = clipio::publish(owner, clip, plain)?;
    if !paste {
        return Ok(());
    }
    let deadline = Instant::now() + Duration::from_millis(450);
    while [VK_SHIFT, VK_CONTROL, VK_MENU, VK_LWIN, VK_RWIN]
        .into_iter()
        .any(|key| unsafe { GetAsyncKeyState(key.into()) } < 0)
    {
        if Instant::now() >= deadline {
            return Err(io::Error::other("Клавиши сочетания всё ещё нажаты"));
        }
        std::thread::sleep(Duration::from_millis(10));
    }
    let editor = WinEdit::default();
    if clipio::sequence() != sequence
        || !editor.target().is_ok_and(|target| {
            (target.foreground, target.edit, target.pid) == (foreground, edit, pid)
        })
        || clipio::sequence() != sequence
    {
        return Err(io::Error::other("Исходное поле или буфер изменились"));
    }
    let key = |key, flags| INPUT {
        r#type: INPUT_KEYBOARD,
        Anonymous: INPUT_0 {
            ki: KEYBDINPUT {
                wVk: key,
                dwFlags: flags,
                ..Default::default()
            },
        },
    };
    let inputs = [
        key(VK_CONTROL, 0),
        key(u16::from(b'V'), 0),
        key(u16::from(b'V'), KEYEVENTF_KEYUP),
        key(VK_CONTROL, KEYEVENTF_KEYUP),
    ];
    if unsafe {
        SendInput(
            inputs.len() as u32,
            inputs.as_ptr(),
            size_of::<INPUT>() as i32,
        )
    } != inputs.len() as u32
    {
        let release = key(VK_CONTROL, KEYEVENTF_KEYUP);
        unsafe {
            SendInput(1, &release, size_of::<INPUT>() as i32);
        }
        return Err(io::Error::other("Windows не приняла команду вставки"));
    }
    Ok(())
}

fn sync_activity_timer(window: HWND, enabled: bool) {
    unsafe {
        if enabled {
            SetTimer(window, ACTIVITY_TIMER, 30_000, None);
        } else {
            KillTimer(window, ACTIVITY_TIMER);
        }
    }
}

fn open_ui(mode: &str) {
    if mode == "--profile" {
        let settings = marked_window(SETTINGS_WINDOW);
        if !settings.is_null() {
            unsafe {
                if IsIconic(settings) != 0 {
                    ShowWindow(settings, SW_RESTORE);
                }
                SetForegroundWindow(settings);
            }
            return;
        }
    }
    let profile = RESIDENT
        .with_borrow(|resident| resident.as_ref().map(|resident| resident.profile.clone()))
        .unwrap_or_else(config::profile);
    let result = std::env::current_exe().and_then(|path| {
        std::process::Command::new(path.with_file_name("typlo-settings.exe"))
            .arg(mode)
            .arg(profile)
            .spawn()
    });
    if let Err(error) = result {
        crate::runtime_error!("Typlo: cannot open UI: {error}");
    }
}

#[cfg(feature = "gui")]
pub fn mark_settings_window(window: usize) {
    mark_window(window, SETTINGS_WINDOW);
    fit_settings_window(window as HWND);
}

#[cfg(feature = "gui")]
fn fit_settings_window(window: HWND) {
    use windows_sys::Win32::{
        Foundation::RECT,
        Graphics::Gdi::{
            GetMonitorInfoW, MONITOR_DEFAULTTONEAREST, MONITORINFO, MonitorFromWindow,
        },
    };
    let mut rect = RECT::default();
    let mut info = MONITORINFO {
        cbSize: size_of::<MONITORINFO>() as u32,
        ..Default::default()
    };
    let monitor = unsafe { MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST) };
    if unsafe { GetWindowRect(window, &mut rect) == 0 || GetMonitorInfoW(monitor, &mut info) == 0 }
    {
        return;
    }
    let area = info.rcWork;
    let width = (rect.right - rect.left).min(area.right - area.left);
    let height = (rect.bottom - rect.top).min(area.bottom - area.top);
    let x = rect.left.clamp(area.left, area.right - width);
    let y = rect.top.clamp(area.top, area.bottom - height);
    if [x, y, width, height]
        != [
            rect.left,
            rect.top,
            rect.right - rect.left,
            rect.bottom - rect.top,
        ]
    {
        unsafe {
            SetWindowPos(
                window,
                null_mut(),
                x,
                y,
                width,
                height,
                SWP_NOZORDER | SWP_NOACTIVATE,
            );
        }
    }
}

#[cfg(feature = "gui")]
pub fn mark_screenshot_editor(window: usize) {
    mark_window(window, SCREENSHOT_EDITOR);
}

#[cfg(feature = "gui")]
pub fn resident_memory() -> io::Result<(usize, usize)> {
    use windows_sys::Win32::System::ProcessStatus::{
        K32GetProcessMemoryInfo, PROCESS_MEMORY_COUNTERS_EX,
    };
    let host = unsafe { FindWindowW(wide(HOST_CLASS).as_ptr(), std::ptr::null()) };
    let mut pid = 0;
    if unsafe { GetWindowThreadProcessId(host, &mut pid) } == 0 {
        return Err(io::Error::other("Typlo is not running"));
    }
    let process = unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid) };
    if process.is_null() {
        return Err(io::Error::last_os_error());
    }
    let mut metrics = PROCESS_MEMORY_COUNTERS_EX {
        cb: size_of::<PROCESS_MEMORY_COUNTERS_EX>() as u32,
        ..Default::default()
    };
    let result = if unsafe {
        K32GetProcessMemoryInfo(process, std::ptr::from_mut(&mut metrics).cast(), metrics.cb)
    } == 0
    {
        Err(io::Error::last_os_error())
    } else {
        Ok((metrics.WorkingSetSize, metrics.PrivateUsage))
    };
    unsafe {
        CloseHandle(process);
    }
    result
}

pub use diagnostics::Report as DiagnosticReport;
pub use diagnostics::operation_label as diagnostic_operation_label;

pub fn resident_diagnostics(profile: &Path, retry: bool) -> io::Result<DiagnosticReport> {
    let host = unsafe { FindWindowW(wide(HOST_CLASS).as_ptr(), std::ptr::null()) };
    if host.is_null() {
        return Err(io::Error::other("Typlo is not running"));
    }
    if send_timeout(host, DIAGNOSTICS, usize::from(retry), 0, 1200)? == 0 {
        return Err(io::Error::other("Could not read Typlo diagnostics"));
    }
    let file = std::fs::File::open(profile.join("diagnostics.json"))?;
    serde_json::from_reader(file).map_err(io::Error::other)
}

#[cfg(feature = "gui")]
fn mark_window(window: usize, property: &str) {
    unsafe {
        SetPropW(
            window as HWND,
            wide(property).as_ptr(),
            std::ptr::without_provenance_mut(1),
        );
    }
}

fn marked_window(property: &str) -> HWND {
    struct Search<'a> {
        property: &'a [u16],
        window: HWND,
    }
    unsafe extern "system" fn find(window: HWND, data: LPARAM) -> i32 {
        let search = unsafe { &mut *(data as *mut Search<'_>) };
        if unsafe { GetPropW(window, search.property.as_ptr()) }.is_null() {
            return 1;
        }
        search.window = window;
        0
    }
    let property = wide(property);
    let mut search = Search {
        property: &property,
        window: null_mut(),
    };
    unsafe {
        EnumWindows(Some(find), std::ptr::from_mut(&mut search) as LPARAM);
    }
    search.window
}

#[cfg(feature = "gui")]
pub fn running_applications() -> Vec<(String, String)> {
    use std::collections::BTreeMap;

    unsafe extern "system" fn collect(window: HWND, state: LPARAM) -> i32 {
        if unsafe { IsWindowVisible(window) } == 0 {
            return 1;
        }
        let mut pid = 0;
        unsafe {
            GetWindowThreadProcessId(window, &mut pid);
        }
        let mut title = [0; 260];
        let length = unsafe { GetWindowTextW(window, title.as_mut_ptr(), title.len() as i32) };
        if length > 0
            && pid != std::process::id()
            && let Some(process) = process_name(pid)
            && !process.eq_ignore_ascii_case("typlo-settings.exe")
        {
            let apps = unsafe { &mut *(state as *mut BTreeMap<String, String>) };
            apps.entry(process)
                .or_insert_with(|| String::from_utf16_lossy(&title[..length as usize]));
        }
        1
    }

    let mut apps = BTreeMap::<String, String>::new();
    unsafe {
        EnumWindows(Some(collect), std::ptr::from_mut(&mut apps) as LPARAM);
    }
    apps.into_iter().collect()
}

fn settings_window_focused() -> bool {
    unsafe { !GetPropW(GetForegroundWindow(), wide(SETTINGS_WINDOW).as_ptr()).is_null() }
}

fn tray_target(target: Target) -> bool {
    if target.pid == std::process::id()
        || process_name(target.pid)
            .is_some_and(|name| name.eq_ignore_ascii_case("typlo-settings.exe"))
    {
        return false;
    }
    let mut class = [0; 128];
    let count = unsafe { GetClassNameW(target.foreground, class.as_mut_ptr(), class.len() as i32) };
    count > 0
        && !matches!(
            String::from_utf16_lossy(&class[..count as usize]).as_str(),
            "Shell_TrayWnd" | "Shell_SecondaryTrayWnd" | "NotifyIconOverflowWindow"
        )
}

unsafe extern "system" fn on_foreground(
    _: HWINEVENTHOOK,
    _: u32,
    foreground: HWND,
    _: i32,
    _: i32,
    _: u32,
    _: u32,
) {
    let host = unsafe { FindWindowW(wide(HOST_CLASS).as_ptr(), std::ptr::null()) };
    if !host.is_null() {
        unsafe {
            PostMessageW(host, FOREGROUND_CHANGED, foreground as usize, 0);
        }
    }
}

fn process_name(pid: u32) -> Option<String> {
    let process = unsafe { OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, 0, pid) };
    if process.is_null() {
        return None;
    }
    let mut path = [0u16; 1024];
    let mut count = path.len() as u32;
    let success = unsafe {
        QueryFullProcessImageNameW(process, PROCESS_NAME_WIN32, path.as_mut_ptr(), &mut count)
    };
    unsafe {
        CloseHandle(process);
    }
    (success != 0).then(|| String::from_utf16_lossy(&path[..count as usize]))
}

fn active_language(target: Target) -> Option<Language> {
    let handle = unsafe { GetKeyboardLayout(target.thread) };
    (!handle.is_null()).then(|| register_layout(handle))
}

fn installed_layouts() -> Vec<HKL> {
    let mut layouts = loaded_layouts();
    keyboard_profiles::retain_enabled(&mut layouts);
    layouts
}

fn loaded_layouts() -> Vec<HKL> {
    let count = unsafe { GetKeyboardLayoutList(0, null_mut()) };
    if !(1..=128).contains(&count) {
        return Vec::new();
    }
    let mut layouts = vec![null_mut(); count as usize];
    let actual = unsafe { GetKeyboardLayoutList(count, layouts.as_mut_ptr()) }.max(0) as usize;
    layouts.truncate(actual);
    layouts.retain(|handle| !handle.is_null());
    layouts
}

pub fn available_layouts() -> Vec<Language> {
    installed_layouts()
        .into_iter()
        .map(register_layout)
        .collect()
}

fn load_resident_config(profile: &Path) -> io::Result<config::Config> {
    let mut settings = config::load(profile)?;
    let available = available_layouts();
    if !available.is_empty() {
        restrict_layouts(&mut settings, &available);
    }
    Ok(settings)
}

fn restrict_layouts(settings: &mut config::Config, available: &[Language]) {
    settings.cycle.retain(|layout| available.contains(layout));
    if settings.cycle.is_empty() {
        settings.cycle.extend_from_slice(available);
    }
    for rule in &mut settings.applications {
        rule.cycle.retain(|layout| available.contains(layout));
    }
    settings
        .layout_hotkeys
        .retain(|binding| available.contains(&binding.layout));
}

fn resolve_layout(alias: Language) -> Language {
    available_layouts()
        .into_iter()
        .find(|language| language.dictionary_index() == alias.dictionary_index())
        .unwrap_or(alias)
}

fn register_layout(handle: HKL) -> Language {
    let id = handle as usize as u32;
    let language = Language::Windows(id);
    if !layout::registered_windows_layout(id) {
        let mut label = [0u16; 128];
        let size = unsafe {
            GetLocaleInfoW(
                language.lang_id().into(),
                LOCALE_SNATIVELANGNAME,
                label.as_mut_ptr(),
                label.len() as i32,
            )
        };
        let name = if size > 1 {
            format!(
                "{} ({id:08x})",
                String::from_utf16_lossy(&label[..size as usize - 1])
            )
        } else {
            format!("Windows ({id:08x})")
        };
        let normal = layout_characters(handle, false);
        let shifted = layout_characters(handle, true);
        let ime = (id >> 16) & 0xf000 == 0xe000;
        let maps = (!ime && normal.chars().any(char::is_alphabetic))
            .then_some((normal.as_str(), shifted.as_str()));
        layout::register_windows_layout(id, name, maps);
    }
    language
}

fn layout_characters(handle: HKL, shift: bool) -> String {
    const SCANS: [u32; 48] = [
        0x29, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26,
        27, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 43,
        0x56,
    ];
    let mut state = [0u8; 256];
    if shift {
        state[VK_SHIFT as usize] = 0x80;
    }
    SCANS
        .into_iter()
        .map(|scan| {
            let key = unsafe { MapVirtualKeyExW(scan, MAPVK_VSC_TO_VK_EX, handle) };
            let mut result = [0u16; 4];
            let size = unsafe {
                ToUnicodeEx(
                    key,
                    scan,
                    state.as_ptr(),
                    result.as_mut_ptr(),
                    result.len() as i32,
                    4,
                    handle,
                )
            };
            if size == 1 {
                char::from_u32(result[0].into())
                    .filter(|c| !c.is_control())
                    .unwrap_or('\0')
            } else {
                '\0'
            }
        })
        .collect()
}

fn layout_matches(handle: HKL, language: Language) -> bool {
    let id = handle as usize as u32;
    match language {
        Language::Windows(expected) => id == expected,
        alias => id as u16 == alias.lang_id(),
    }
}

fn switch_cycle(
    editor: &WinEdit,
    target: Target,
    cycle: &[Language],
    source: Option<Language>,
) -> io::Result<()> {
    // The resident cycle contains the enabled profiles from the last settings
    // load. Check for unloaded handles without querying input profiles here.
    let installed = loaded_layouts();
    let available: Vec<_> = cycle
        .iter()
        .copied()
        .filter(|language| {
            installed
                .iter()
                .any(|handle| layout_matches(*handle, *language))
        })
        .collect();
    let next = match available.as_slice() {
        [only] => Some(*only),
        _ => layout::next_language(&available, source),
    };
    if let Some(layout) = next
        .filter(|language| Some(*language) != source)
        .and_then(|language| {
            installed
                .into_iter()
                .find(|handle| layout_matches(*handle, language))
        })
        && editor.target()? == target
    {
        post_layout(target, layout);
    }
    Ok(())
}

fn switch_layout(target: Target, language: Language) {
    if let Some(layout) = installed_layouts()
        .into_iter()
        .find(|layout| layout_matches(*layout, language))
    {
        post_layout(target, layout);
    }
}

fn post_layout(target: Target, layout: HKL) {
    unsafe {
        PostMessageW(target.edit, WM_INPUTLANGCHANGEREQUEST, 0, layout as LPARAM);
    }
}

fn wide(text: &str) -> Vec<u16> {
    text.encode_utf16().chain([0]).collect()
}

pub fn native_self_test() -> io::Result<()> {
    for mode in [
        "edit",
        "plain",
        "formatted",
        "multiline",
        "password",
        "typing",
    ] {
        test_native_editor(mode)
            .map_err(|error| io::Error::new(error.kind(), format!("{mode}: {error}")))?;
    }
    test_hotkey_conflict()
}

pub fn probe_accessible_snapshot(window: usize, label: &str, expected: &str) -> io::Result<()> {
    accessible::probe_snapshot(window, label, expected)
}

pub fn probe_accessible_separator(
    window: usize,
    label: &str,
    original: &str,
    replacement: &str,
) -> io::Result<()> {
    accessible::probe_preserve_separator(window, label, original, replacement)
}

pub fn probe_accessible_range(
    window: usize,
    label: &str,
    start: usize,
    end: usize,
) -> io::Result<()> {
    accessible::probe_range(window, label, start..end)
}

pub fn probe_accessible_correction_range(
    window: usize,
    label: &str,
    typed: &str,
    expected: &str,
) -> io::Result<()> {
    accessible::probe_correction_range(window, label, typed, expected)
}

fn test_hotkey_conflict() -> io::Result<()> {
    let make_window = || unsafe {
        CreateWindowExW(
            0,
            wide("STATIC").as_ptr(),
            std::ptr::null(),
            WS_POPUP,
            0,
            0,
            0,
            0,
            null_mut(),
            null_mut(),
            GetModuleHandleW(null_mut()),
            std::ptr::null(),
        )
    };
    let first = make_window();
    let second = make_window();
    let mut first_active = Vec::new();
    let mut second_active = Vec::new();
    let result = (|| {
        if first.is_null() || second.is_null() {
            return Err(io::Error::last_os_error());
        }
        let chord = Chord {
            modifiers: 7,
            key: 0x86,
        };
        let occupied = Chord {
            modifiers: 7,
            key: 0x87,
        };
        if !install_hotkeys(first, &mut first_active, &[(Action::Case, occupied)]).is_empty()
            || !install_hotkeys(second, &mut second_active, &[(Action::Convert, chord)]).is_empty()
        {
            return Err(io::Error::other("Cannot register test hotkeys"));
        }
        let rejected = rebind_hotkeys(second, &mut second_active, &[(Action::Convert, occupied)]);
        if rejected != [Action::Convert] || second_active != [(Action::Convert, chord)] {
            return Err(io::Error::other(
                "Shortcut conflict did not preserve the old binding",
            ));
        }
        Ok(())
    })();
    install_hotkeys(first, &mut first_active, &[]);
    install_hotkeys(second, &mut second_active, &[]);
    unsafe {
        if !first.is_null() {
            DestroyWindow(first);
        }
        if !second.is_null() {
            DestroyWindow(second);
        }
    }
    result
}

fn test_native_editor(mode: &str) -> io::Result<()> {
    with_native_test_editor(mode, |window, mut editor| {
        check_native_fixture(mode, window, &mut editor)
    })
}

fn with_native_test_editor<T>(
    mode: &str,
    run: impl FnOnce(HWND, WinEdit) -> io::Result<T>,
) -> io::Result<T> {
    use std::{
        io::{BufRead, BufReader},
        os::windows::process::CommandExt,
        process::{Command, Stdio},
    };
    use windows_sys::Win32::System::Threading::CREATE_NO_WINDOW;

    let mut child = Command::new(std::env::current_exe()?)
        .arg("--native-test-editor")
        .arg(mode)
        .creation_flags(CREATE_NO_WINDOW)
        .stdout(Stdio::piped())
        .spawn()?;
    let result = (|| {
        let mut line = String::new();
        BufReader::new(child.stdout.take().expect("fixture stdout is piped"))
            .read_line(&mut line)?;
        let window = line.trim().parse::<usize>().map_err(io::Error::other)? as HWND;
        let mut pid = 0;
        let thread = unsafe { GetWindowThreadProcessId(window, &mut pid) };
        if thread == 0 || pid != child.id() {
            return Err(io::Error::other(
                "Fixture window does not belong to the test process",
            ));
        }
        let target = Target {
            foreground: window,
            edit: window,
            thread,
            pid,
        };
        let editor = WinEdit {
            fixed: Some(target),
            ..Default::default()
        };
        run(window, editor)
    })();
    let _ = child.kill();
    child.wait()?;
    result
}

fn check_native_fixture(mode: &str, window: HWND, editor: &mut WinEdit) -> io::Result<()> {
    if mode == "typing" {
        use crate::snippets::Template;
        let mut session = Session::default();
        let expected = |editor: &mut WinEdit, text: &str, caret: usize| -> io::Result<bool> {
            let state = editor.snapshot()?;
            Ok(state.document.iter().copied().eq(text.encode_utf16())
                && state.selection == (caret..caret))
        };
        send(window, EM_SETSEL, 8, 8)?;
        let template = Template::parse("hello\n🙂{cursor}!")?;
        if !session.expand_snippet(editor, ";ad", &template)?
            || !expected(editor, "keep hello\r\n🙂!", 14)?
            || !session.undo(editor)?
            || !expected(editor, "keep ;ad", 8)?
        {
            return Err(io::Error::other(
                "Multiline snippet, caret or Undo mismatch",
            ));
        }
        let sample = wide("keep rfr ");
        send(window, WM_SETTEXT, 0, sample.as_ptr() as LPARAM)?;
        send(window, EM_SETSEL, 9, 9)?;
        let mut vocabulary = Vocabulary::installed()?;
        let empty = Dictionary::new(
            &std::env::temp_dir().join(format!("typlo-fixture-{}", std::process::id())),
        );
        let mut suggest = |token: &str| {
            vocabulary.suggest(
                &empty,
                token,
                Language::En,
                &[Language::En, Language::Ru],
                true,
            )
        };
        if session.correct_word(editor, "rfr ", &mut suggest)? != Some(Language::Ru)
            || !expected(editor, "keep как ", 9)?
            || !session.undo(editor)?
            || !expected(editor, "keep rfr ", 9)?
            || session
                .correct_word(editor, "rfr ", &mut suggest)?
                .is_some()
        {
            return Err(io::Error::other(
                "Dictionary correction or Undo suppression mismatch",
            ));
        }
        editor.recent = Some("rfr ".into());
        if session.convert_to(editor, Language::Ru, Some(Language::En))? != Some(Language::Ru)
            || !expected(editor, "keep как ", 9)?
        {
            return Err(io::Error::other(
                "Last typed word was not replaced with its space",
            ));
        }
        for separator in [' ', '\u{a0}'] {
            let original = format!("keep ghbdtn{separator}");
            let corrected = format!("keep привет{separator}");
            let text = wide(&original);
            let caret = original.encode_utf16().count();
            send(window, WM_SETTEXT, 0, text.as_ptr() as LPARAM)?;
            send(window, EM_SETSEL, caret, caret as LPARAM)?;
            if session.correct_word(editor, "ghbdtn ", &mut suggest)? != Some(Language::Ru)
                || !expected(editor, &corrected, caret)?
                || !session.undo(editor)?
                || !expected(editor, &original, caret)?
            {
                return Err(io::Error::other(
                    "ghbdtn autocorrection or Undo did not preserve the typed space",
                ));
            }
        }
        return Ok(());
    }
    if !matches!(mode, "edit" | "plain") {
        let snapshot = editor.snapshot();
        return if snapshot.is_err_and(|error| error.kind() == io::ErrorKind::Unsupported) {
            Ok(())
        } else {
            Err(io::Error::other("Unsafe editor was accepted"))
        };
    }
    send(window, EM_SETSEL, 2, 8)?;
    let mut session = Session::default();
    if session.convert(editor, &Language::ALL, Some(Language::En))? != Some(Language::Ru)
        || editor.snapshot()?.document != "🙂приветTAIL".encode_utf16().collect::<Vec<_>>()
        || !session.undo(editor)?
        || editor.snapshot()?.document != "🙂ghbdtnTAIL".encode_utf16().collect::<Vec<_>>()
    {
        return Err(io::Error::other(
            "Native replacement or Undo returned unexpected text",
        ));
    }
    Ok(())
}

pub fn measure_native(iterations: usize) -> io::Result<serde_json::Value> {
    if !(1..=1000).contains(&iterations) {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "Choose 1 to 1000 iterations",
        ));
    }
    let mut results = Vec::new();
    let summarize = |mut samples: Vec<f64>| {
        samples.sort_by(f64::total_cmp);
        let percentile = |p: usize| {
            samples
                .get((samples.len() * p).div_ceil(100).saturating_sub(1))
                .copied()
        };
        serde_json::json!({ "confirmed": samples.len(), "p50_ms": percentile(50), "p95_ms": percentile(95) })
    };
    for mode in ["edit", "plain"] {
        results.push(with_native_test_editor(mode, |window, mut editor| {
            let mut replacement = Vec::with_capacity(iterations);
            let mut undo = Vec::with_capacity(iterations);
            let expected: Vec<_> = "🙂приветTAIL".encode_utf16().collect();
            let original: Vec<_> = "🙂ghbdtnTAIL".encode_utf16().collect();
            for _ in 0..iterations {
                let mut session = Session::default();
                let start_text = wide("🙂ghbdtnTAIL");
                if send(window, WM_SETTEXT, 0, start_text.as_ptr() as LPARAM)? == 0 {
                    return Err(io::Error::other("Native fixture refused to reset"));
                }
                send(window, EM_SETSEL, 2, 8)?;
                let start = Instant::now();
                let converted = session.convert_to(&mut editor, Language::Ru, Some(Language::En));
                let elapsed = start.elapsed().as_secs_f64() * 1000.;
                let after = editor.snapshot()?;
                if !converted.is_ok_and(|result| result == Some(Language::Ru))
                    || after.document != expected
                    || after.selection != (8..8)
                {
                    continue;
                }
                replacement.push(elapsed);
                let start = Instant::now();
                let undone = session.undo(&mut editor);
                let elapsed = start.elapsed().as_secs_f64() * 1000.;
                let after = editor.snapshot()?;
                if undone.is_ok_and(|result| result)
                    && after.document == original
                    && after.selection == (8..8)
                {
                    undo.push(elapsed);
                }
            }
            Ok(serde_json::json!({ "editor": mode, "attempts": iterations,
                "replace": summarize(replacement), "undo": summarize(undo) }))
        })?);
    }
    results.push(with_native_test_editor("typing", |window, mut editor| {
        let profile = tempfile::tempdir()?;
        let dictionary = Dictionary::new(profile.path());
        let mut vocabulary = Vocabulary::installed()?;
        let mut correction = Vec::with_capacity(iterations);
        let mut undo = Vec::with_capacity(iterations);
        for attempt in 0..iterations {
            let separator = if attempt % 2 == 0 { ' ' } else { '\u{a0}' };
            let original = format!("keep ghbdtn{separator}");
            let expected = format!("keep привет{separator}");
            let caret = original.encode_utf16().count();
            let text = wide(&original);
            if send(window, WM_SETTEXT, 0, text.as_ptr() as LPARAM)? == 0 {
                return Err(io::Error::other("Typing fixture refused to reset"));
            }
            send(window, EM_SETSEL, caret, caret as LPARAM)?;
            let mut session = Session::default();
            let started = Instant::now();
            let changed = session.correct_word(&mut editor, "ghbdtn ", |token| {
                vocabulary.suggest(
                    &dictionary,
                    token,
                    Language::En,
                    &[Language::En, Language::Ru],
                    true,
                )
            });
            let elapsed = started.elapsed().as_secs_f64() * 1000.;
            let after = editor.snapshot()?;
            if changed.is_ok_and(|result| result == Some(Language::Ru))
                && after.selection == (caret..caret)
                && after.document.iter().copied().eq(expected.encode_utf16())
            {
                correction.push(elapsed);
                let started = Instant::now();
                let restored = session.undo(&mut editor);
                let elapsed = started.elapsed().as_secs_f64() * 1000.;
                let after = editor.snapshot()?;
                if restored.is_ok_and(|result| result)
                    && after.selection == (caret..caret)
                    && after.document.iter().copied().eq(original.encode_utf16())
                {
                    undo.push(elapsed);
                }
            }
        }
        Ok(
            serde_json::json!({ "editor": "typing", "attempts": iterations,
            "autocorrect": summarize(correction), "undo": summarize(undo) }),
        )
    })?);
    Ok(serde_json::json!(results))
}

pub fn native_test_editor(mode: &str) -> io::Result<()> {
    use std::io::Write;
    use windows_sys::Win32::System::LibraryLoader::{LOAD_LIBRARY_SEARCH_SYSTEM32, LoadLibraryExW};

    let rich = matches!(mode, "plain" | "formatted" | "multiline");
    if !rich && !matches!(mode, "edit" | "password" | "typing") {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "Unknown fixture",
        ));
    }
    if rich
        && unsafe {
            LoadLibraryExW(
                wide("Msftedit.dll").as_ptr(),
                null_mut(),
                LOAD_LIBRARY_SEARCH_SYSTEM32,
            )
        }
        .is_null()
    {
        return Err(io::Error::last_os_error());
    }
    let class = if rich { "RICHEDIT50W" } else { "Edit" };
    let style = WS_POPUP
        | match mode {
            "multiline" | "typing" => ES_MULTILINE as u32,
            "password" => ES_PASSWORD as u32,
            _ => 0,
        };
    let window = unsafe {
        CreateWindowExW(
            0,
            wide(class).as_ptr(),
            std::ptr::null(),
            style,
            0,
            0,
            320,
            40,
            null_mut(),
            null_mut(),
            GetModuleHandleW(null_mut()),
            std::ptr::null(),
        )
    };
    if window.is_null() {
        return Err(io::Error::last_os_error());
    }
    if matches!(mode, "plain" | "multiline") && send(window, WM_USER + 89, TM_PLAINTEXT, 0)? != 0 {
        return Err(io::Error::other("Could not set plain-text mode"));
    }
    let text = match mode {
        "multiline" => "abc\r\nghbdtn",
        "typing" => "keep ;ad",
        _ => "🙂ghbdtnTAIL",
    };
    if send(window, WM_SETTEXT, 0, wide(text).as_ptr() as LPARAM)? == 0 {
        return Err(io::Error::other("Could not initialize fixture"));
    }
    println!("{}", window as usize);
    io::stdout().flush()?;
    let mut message = MSG::default();
    loop {
        let received = unsafe { GetMessageW(&mut message, null_mut(), 0, 0) };
        if received <= 0 {
            return if received == 0 {
                Ok(())
            } else {
                Err(io::Error::last_os_error())
            };
        }
        unsafe {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
    }
}
