use super::{Target, WinEdit, accessible::Captured, input, wide};
use crate::{
    locale::text as tr,
    snippets::{Snippet, Template},
};
use std::{
    cell::RefCell,
    collections::HashSet,
    fs, io,
    ops::Range,
    path::{Path, PathBuf},
    ptr::null_mut,
    sync::{
        Mutex, OnceLock,
        atomic::{AtomicUsize, Ordering},
    },
};
use windows_sys::Win32::{
    Foundation::{HWND, LPARAM, LRESULT, POINT, RECT, WPARAM},
    Graphics::Gdi::*,
    System::LibraryLoader::GetModuleHandleW,
    UI::{
        HiDpi::{DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2, SetThreadDpiAwarenessContext},
        Input::KeyboardAndMouse::{
            GetKeyState, ReleaseCapture, SetCapture, VK_CONTROL, VK_DOWN, VK_ESCAPE, VK_RETURN,
            VK_UP,
        },
        WindowsAndMessaging::*,
    },
};

pub(super) const READY: u32 = WM_APP + 16;
pub(super) const APPLY: u32 = WM_APP + 17;
const MAX_VISIBLE: usize = 8;

/// Colors of the menu, as Windows COLORREF values (0x00BBGGRR).
#[derive(Clone, Copy)]
struct Palette {
    background: u32,
    header: u32,
    border: u32,
    text: u32,
    muted: u32,
    selected: u32,
    chip: u32,
    chip_text: u32,
    star: u32,
}

const LIGHT: Palette = Palette {
    background: 0x00ffffff,
    header: 0x00f7f5f3,
    border: 0x00e3ddd9,
    text: 0x0028231f,
    muted: 0x0080726b,
    selected: 0x00f4f0ed,
    chip: 0x00ebe7e4,
    chip_text: 0x00514137,
    star: 0x000b9ef5,
};

const DARK: Palette = Palette {
    background: 0x00262220,
    header: 0x00332d2a,
    border: 0x00463e3a,
    text: 0x00ebe8e6,
    muted: 0x00aca19a,
    selected: 0x00403834,
    chip: 0x00493f3a,
    chip_text: 0x00dbd5d1,
    star: 0x0024bffb,
};

/// True when Windows uses the dark theme for apps.
pub(super) fn system_dark() -> bool {
    use windows_sys::Win32::System::Registry::{HKEY_CURRENT_USER, RRF_RT_REG_DWORD, RegGetValueW};
    let key = wide(r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
    let value = wide("AppsUseLightTheme");
    let mut data = 1u32;
    let mut size = size_of::<u32>() as u32;
    let status = unsafe {
        RegGetValueW(
            HKEY_CURRENT_USER,
            key.as_ptr(),
            value.as_ptr(),
            RRF_RT_REG_DWORD,
            null_mut(),
            (&raw mut data).cast(),
            &mut size,
        )
    };
    status == 0 && data == 0
}

#[derive(Clone, Copy)]
pub(super) struct Origin(usize, usize, u32, u32);

impl Origin {
    pub fn new(target: Target) -> Self {
        Self(
            target.foreground as usize,
            target.edit as usize,
            target.thread,
            target.pid,
        )
    }

    pub fn target(self) -> Target {
        Target {
            foreground: self.0 as HWND,
            edit: self.1 as HWND,
            thread: self.2,
            pid: self.3,
        }
    }
}

pub(super) enum Selection {
    Native {
        document: Vec<u16>,
        selection: Range<usize>,
    },
    Accessible(Captured),
    Scintilla(super::scintilla::SnippetSelection),
}

pub(super) struct Prepared {
    pub origin: Origin,
    pub selection: Option<Selection>,
    pub insertion_disabled: bool,
    pub sequence: usize,
    pub anchor: RECT,
    pub snippets: Vec<Snippet>,
}

pub(super) struct Insertion {
    pub origin: Origin,
    pub selection: Selection,
    pub template: Template,
}

static PENDING: Mutex<Option<Prepared>> = Mutex::new(None);
static INSERTION: Mutex<Option<Insertion>> = Mutex::new(None);
static WINDOW: AtomicUsize = AtomicUsize::new(0);
thread_local! { static OPEN: RefCell<Option<Menu>> = const { RefCell::new(None) }; }

struct Menu {
    prepared: Prepared,
    host: HWND,
    index: usize,
    first: usize,
    rows: usize,
    row_height: i32,
    header: i32,
    pressed: Option<Hit>,
    profile: PathBuf,
    pins: HashSet<String>,
    matches: Vec<usize>,
    query: Vec<u16>,
    pin_width: i32,
    pin_error: bool,
    parameters: Option<ParameterForm>,
    palette: Palette,
    /// Rows start this far below the header and are inset this far from the
    /// sides. Painting and hit testing both use [`Rows`].
    rows_layout: Rows,
}

/// Vertical and horizontal placement of list rows, shared by painting and
/// mouse hit testing.
#[derive(Clone, Copy)]
struct Rows {
    header: i32,
    gap: i32,
    margin: i32,
    height: i32,
}

impl Rows {
    fn new(dpi: i32, header: i32, height: i32) -> Self {
        Self {
            header,
            gap: 4 * dpi / 96,
            margin: 6 * dpi / 96,
            height,
        }
    }

    fn top(self, row: usize) -> i32 {
        self.header + self.gap + row as i32 * self.height
    }

    /// The row under a client point, or None in the header and the padding.
    fn at(self, x: i32, y: i32, width: i32) -> Option<usize> {
        let offset = y - self.top(0);
        (offset >= 0 && x >= self.margin && x < width - self.margin)
            .then(|| (offset / self.height) as usize)
    }
}

struct ParameterForm {
    snippet: usize,
    names: Vec<String>,
    values: Vec<Vec<u16>>,
    active: usize,
}

#[derive(Clone, Copy, PartialEq, Eq)]
struct Hit {
    index: usize,
    pin: bool,
}

impl Menu {
    fn refresh(&mut self, keep: Option<usize>) {
        self.matches = matching(
            &self.prepared.snippets,
            &self.pins,
            &String::from_utf16_lossy(&self.query),
        );
        self.index = keep
            .and_then(|keep| self.matches.iter().position(|&index| index == keep))
            .unwrap_or(0);
        self.first = self.index.saturating_add(1).saturating_sub(self.rows);
        self.pressed = None;
    }

    fn toggle_pin(&mut self) {
        let Some(&item) = self.matches.get(self.index) else {
            return;
        };
        let trigger = self.prepared.snippets[item].trigger.to_lowercase();
        let mut pins = self.pins.clone();
        if !pins.remove(&trigger) {
            pins.insert(trigger);
        }
        let save = || -> io::Result<()> {
            let mut file = tempfile::NamedTempFile::new_in(&self.profile)?;
            serde_json::to_writer(file.as_file_mut(), &pins).map_err(io::Error::other)?;
            file.persist(self.profile.join("snippet-pins.json"))
                .map_err(|error| error.error)?;
            Ok(())
        };
        self.pin_error = save().is_err();
        if !self.pin_error {
            self.pins = pins;
            self.refresh(Some(item));
        }
    }

    fn edit_query(&mut self, unit: u16) {
        self.pin_error = false;
        if unit == 8 {
            if self
                .query
                .pop()
                .is_some_and(|last| (0xdc00..=0xdfff).contains(&last))
                && self
                    .query
                    .last()
                    .is_some_and(|last| (0xd800..=0xdbff).contains(last))
            {
                self.query.pop();
            }
        } else if unit >= 32 && unit != 127 && self.query.len() < 128 {
            self.query.push(unit);
        } else {
            return;
        }
        self.refresh(None);
    }
}

/// The first non-empty line of the snippet text, with `{cursor}` removed.
fn preview(snippet: &Snippet) -> String {
    snippet
        .text
        .lines()
        .map(str::trim)
        .find(|line| !line.is_empty())
        .unwrap_or("")
        .replace("{cursor}", " ")
        .split_whitespace()
        .collect::<Vec<_>>()
        .join(" ")
        .chars()
        .take(160)
        .collect()
}

fn title(snippet: &Snippet) -> &str {
    let name = snippet.name.trim();
    if !name.is_empty() {
        return name;
    }
    snippet
        .text
        .lines()
        .map(str::trim)
        .find(|line| !line.is_empty())
        .unwrap_or("")
}

fn matching(snippets: &[Snippet], pins: &HashSet<String>, query: &str) -> Vec<usize> {
    let query = query.to_lowercase();
    let terms = query.split_whitespace().collect::<Vec<_>>();
    let mut result = snippets
        .iter()
        .enumerate()
        .filter_map(|(index, snippet)| {
            let trigger = snippet.trigger.to_lowercase();
            let title = title(snippet).to_lowercase();
            terms
                .iter()
                .all(|term| trigger.contains(term) || title.contains(term))
                .then_some((!pins.contains(&trigger), index))
        })
        .collect::<Vec<_>>();
    result.sort_unstable();
    result.into_iter().map(|(_, index)| index).collect()
}

pub(super) fn publish(host: HWND, prepared: Prepared) {
    if let Ok(mut slot) = PENDING.lock() {
        *slot = Some(prepared);
        unsafe {
            PostMessageW(host, READY, 0, 0);
        }
    }
}

pub(super) fn take_insertion() -> Option<Insertion> {
    INSERTION.lock().ok()?.take()
}

/// True while a menu is on screen; the shortcut then does not open another.
pub(super) fn focus_existing() -> bool {
    !open_window().is_null()
}

/// The open menu, for the keyboard hook that forwards keys to it.
pub(super) fn open_window() -> HWND {
    WINDOW.load(Ordering::Acquire) as HWND
}

/// Closes the menu when the mouse is pressed outside it.
pub(super) fn dismiss_outside(point: POINT) {
    let window = open_window();
    let mut bounds = RECT::default();
    if !window.is_null()
        && unsafe { GetWindowRect(window, &mut bounds) } != 0
        && !((bounds.left..bounds.right).contains(&point.x)
            && (bounds.top..bounds.bottom).contains(&point.y))
    {
        unsafe { PostMessageW(window, WM_CLOSE, 0, 0) };
    }
}

/// Closes the menu when the user switches to a window other than the one it
/// was opened for. Runs on the resident thread, which owns the menu.
pub(super) fn dismiss_unless_foreground(foreground: HWND) {
    let window = open_window();
    let origin = OPEN.with_borrow(|menu| {
        menu.as_ref()
            .map(|menu| menu.prepared.origin.target().foreground)
    });
    if !window.is_null()
        && foreground != window
        && origin.is_some_and(|origin| origin != foreground)
    {
        close(window, false);
    }
}

pub(super) fn caret_anchor(target: Target) -> Option<RECT> {
    let _dpi = DpiScope::new();
    let mut info = GUITHREADINFO {
        cbSize: size_of::<GUITHREADINFO>() as u32,
        ..Default::default()
    };
    if unsafe { GetGUIThreadInfo(target.thread, &mut info) } == 0
        || info.hwndCaret.is_null()
        || unsafe { GetAncestor(info.hwndCaret, GA_ROOT) } != target.foreground
    {
        return None;
    }
    let mut top = POINT {
        x: info.rcCaret.left,
        y: info.rcCaret.top,
    };
    let mut bottom = POINT {
        x: info.rcCaret.right,
        y: info.rcCaret.bottom,
    };
    if unsafe { ClientToScreen(info.hwndCaret, &mut top) } == 0
        || unsafe { ClientToScreen(info.hwndCaret, &mut bottom) } == 0
        || bottom.y <= top.y
    {
        return None;
    }
    Some(RECT {
        left: top.x,
        top: top.y,
        right: bottom.x,
        bottom: bottom.y,
    })
}

pub(super) fn pointer_anchor() -> RECT {
    let mut point = POINT::default();
    unsafe {
        GetPhysicalCursorPos(&mut point);
    }
    RECT {
        left: point.x,
        top: point.y,
        right: point.x,
        bottom: point.y + 2,
    }
}

pub(super) struct DpiScope(windows_sys::Win32::UI::HiDpi::DPI_AWARENESS_CONTEXT);
impl DpiScope {
    pub fn new() -> Self {
        Self(unsafe { SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) })
    }
}
impl Drop for DpiScope {
    fn drop(&mut self) {
        if !self.0.is_null() {
            unsafe {
                SetThreadDpiAwarenessContext(self.0);
            }
        }
    }
}

pub(super) fn show(host: HWND, profile: &Path, dark: bool) -> io::Result<()> {
    let Some(prepared) = PENDING.lock().ok().and_then(|mut slot| slot.take()) else {
        return Ok(());
    };
    if prepared.sequence != input::sequence()
        || WinEdit::default().target().ok() != Some(prepared.origin.target())
        || focus_existing()
    {
        return Ok(());
    }
    let _dpi = DpiScope::new();
    let anchor = prepared.anchor;
    let monitor = unsafe {
        MonitorFromPoint(
            POINT {
                x: anchor.left,
                y: anchor.top,
            },
            MONITOR_DEFAULTTONEAREST,
        )
    };
    let mut info = MONITORINFO {
        cbSize: size_of::<MONITORINFO>() as u32,
        ..Default::default()
    };
    if unsafe { GetMonitorInfoW(monitor, &mut info) } == 0 {
        return Err(io::Error::last_os_error());
    }

    let class = wide("TyploRustSnippetMenu");
    let instance = unsafe { GetModuleHandleW(null_mut()) };
    static REGISTERED: OnceLock<bool> = OnceLock::new();
    if !*REGISTERED.get_or_init(|| unsafe {
        RegisterClassW(&WNDCLASSW {
            style: CS_DROPSHADOW,
            lpfnWndProc: Some(window_proc),
            hInstance: instance,
            lpszClassName: class.as_ptr(),
            hCursor: LoadCursorW(null_mut(), IDC_ARROW),
            ..Default::default()
        }) != 0
    }) {
        return Err(io::Error::last_os_error());
    }
    let title = wide(tr("rust.snippet.menuTitle", "Typlo snippets"));
    let window = unsafe {
        CreateWindowExW(
            // The menu never takes focus; the keyboard hook forwards keys to it.
            WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE,
            class.as_ptr(),
            title.as_ptr(),
            WS_POPUP,
            anchor.left,
            anchor.top,
            1,
            1,
            host,
            null_mut(),
            instance,
            null_mut(),
        )
    };
    if window.is_null() {
        return Err(io::Error::last_os_error());
    }
    // Rounded corners on Windows 11; older versions keep square corners.
    unsafe {
        use windows_sys::Win32::Graphics::Dwm::{
            DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND, DwmSetWindowAttribute,
        };
        let corner = DWMWCP_ROUND;
        DwmSetWindowAttribute(
            window,
            DWMWA_WINDOW_CORNER_PREFERENCE as u32,
            (&raw const corner).cast(),
            size_of_val(&corner) as u32,
        );
    }
    let dpi = unsafe { windows_sys::Win32::UI::HiDpi::GetDpiForWindow(window) }.max(96) as i32;
    let row_height = 36 * dpi / 96;
    let header = 38 * dpi / 96;
    let work = info.rcWork;
    let rows = prepared
        .snippets
        .len()
        .clamp(1, MAX_VISIBLE)
        .min(((work.bottom - work.top - header - 8) / row_height).max(1) as usize);
    let width = (440 * dpi / 96).min(work.right - work.left);
    let height = Rows::new(dpi, header, row_height).top(rows) + 4 * dpi / 96;
    let (x, y) = popup_position(anchor, work, width, height);
    let pins: HashSet<String> = fs::read(profile.join("snippet-pins.json"))
        .ok()
        .and_then(|data| serde_json::from_slice(&data).ok())
        .unwrap_or_default();
    let matches = matching(&prepared.snippets, &pins, "");
    OPEN.replace(Some(Menu {
        prepared,
        host,
        index: 0,
        first: 0,
        rows,
        row_height,
        header,
        pressed: None,
        profile: profile.to_owned(),
        pins,
        matches,
        query: Vec::new(),
        pin_width: 34 * dpi / 96,
        pin_error: false,
        parameters: None,
        palette: if dark { DARK } else { LIGHT },
        rows_layout: Rows::new(dpi, header, row_height),
    }));
    WINDOW.store(window as usize, Ordering::Release);
    unsafe {
        SetWindowPos(
            window,
            HWND_TOPMOST,
            x,
            y,
            width,
            height,
            SWP_SHOWWINDOW | SWP_NOACTIVATE,
        );
    }
    Ok(())
}

fn popup_position(anchor: RECT, work: RECT, width: i32, height: i32) -> (i32, i32) {
    let below = anchor.bottom + 5;
    let above = anchor.top - height - 5;
    let max_x = (work.right - width).max(work.left);
    let max_y = (work.bottom - height).max(work.top);
    let x = anchor.left.clamp(work.left, max_x);
    if (work.top..=max_y).contains(&below) {
        return (x, below);
    }
    if (work.top..=max_y).contains(&above) {
        return (x, above);
    }
    let right = anchor.right + 5;
    let left = anchor.left - width - 5;
    let side = if right <= max_x || left < work.left {
        right
    } else {
        left
    };
    (
        side.clamp(work.left, max_x),
        anchor.top.clamp(work.top, max_y),
    )
}

fn choose(window: HWND) {
    if !OPEN.with_borrow(|slot| {
        slot.as_ref()
            .is_some_and(|menu| menu.prepared.selection.is_some() && !menu.matches.is_empty())
    }) {
        return;
    }
    let form_height = OPEN.with_borrow_mut(|slot| {
        let menu = slot.as_mut()?;
        if menu.parameters.is_some() {
            return None;
        }
        let &snippet = menu.matches.get(menu.index)?;
        let names = menu.prepared.snippets[snippet].parameter_names().ok()?;
        if names.is_empty() {
            return None;
        }
        let height = menu.rows_layout.top(names.len() + 1) + menu.rows_layout.gap;
        menu.parameters = Some(ParameterForm {
            snippet,
            values: vec![Vec::new(); names.len()],
            names,
            active: 0,
        });
        Some(height)
    });
    if let Some(height) = form_height {
        unsafe {
            let mut bounds = RECT::default();
            GetWindowRect(window, &mut bounds);
            let mut info = MONITORINFO {
                cbSize: size_of::<MONITORINFO>() as u32,
                ..Default::default()
            };
            if GetMonitorInfoW(
                MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST),
                &mut info,
            ) != 0
            {
                bounds.top = bounds
                    .top
                    .min(info.rcWork.bottom - height)
                    .max(info.rcWork.top);
            }
            SetWindowPos(
                window,
                HWND_TOPMOST,
                bounds.left,
                bounds.top,
                bounds.right - bounds.left,
                height,
                SWP_NOACTIVATE,
            );
            InvalidateRect(window, null_mut(), 0);
        }
    } else {
        close(window, true);
    }
}

fn close(window: HWND, apply: bool) {
    let menu = OPEN.take();
    WINDOW.store(0, Ordering::Release);
    if let Some(menu) = menu {
        let target = menu.prepared.origin.target();
        if !apply && unsafe { GetForegroundWindow() } == window {
            unsafe {
                SetForegroundWindow(target.foreground);
            }
        }
        if apply
            && let Some(selection) = menu.prepared.selection
            && (unsafe { GetForegroundWindow() } == target.foreground
                || unsafe { SetForegroundWindow(target.foreground) } != 0)
            && let Some(template) = if let Some(form) = menu.parameters {
                let values = form
                    .names
                    .into_iter()
                    .zip(
                        form.values
                            .into_iter()
                            .map(|value| String::from_utf16_lossy(&value)),
                    )
                    .collect::<Vec<_>>();
                menu.prepared.snippets[form.snippet].fill(&values).ok()
            } else {
                menu.matches
                    .get(menu.index)
                    .and_then(|&index| menu.prepared.snippets[index].fill(&[]).ok())
            }
            && let Ok(mut insertion) = INSERTION.lock()
        {
            *insertion = Some(Insertion {
                origin: menu.prepared.origin,
                selection,
                template,
            });
            unsafe {
                PostMessageW(menu.host, APPLY, 0, 0);
            }
        }
    }
    unsafe {
        DestroyWindow(window);
    }
}

fn row_at(window: HWND, menu: &Menu, lparam: LPARAM) -> Option<Hit> {
    let x = lparam as i16 as i32;
    let y = (lparam >> 16) as i16 as i32;
    let mut client = RECT::default();
    if unsafe { GetClientRect(window, &mut client) } == 0 {
        return None;
    }
    let index = menu.first + menu.rows_layout.at(x, y, client.right)?;
    (index < menu.first + menu.rows && index < menu.matches.len()).then_some(Hit {
        index,
        pin: x >= client.right - menu.pin_width,
    })
}

unsafe extern "system" fn window_proc(
    window: HWND,
    message: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    if let Some(submit) = OPEN.with_borrow_mut(|slot| {
        let menu = slot.as_mut()?;
        let form = menu.parameters.as_mut()?;
        match message {
            WM_KEYDOWN if wparam == VK_RETURN as usize => {
                if form.active + 1 == form.names.len() {
                    return Some(true);
                }
                form.active += 1;
            }
            WM_KEYDOWN if wparam == VK_DOWN as usize || wparam == 9 => {
                form.active = (form.active + 1) % form.names.len();
            }
            WM_KEYDOWN if wparam == VK_UP as usize => {
                form.active = form.active.saturating_sub(1);
            }
            WM_CHAR => {
                let value = &mut form.values[form.active];
                let unit = wparam as u16;
                if unit == 8 {
                    if value
                        .pop()
                        .is_some_and(|unit| (0xdc00..=0xdfff).contains(&unit))
                        && value
                            .last()
                            .is_some_and(|unit| (0xd800..=0xdbff).contains(unit))
                    {
                        value.pop();
                    }
                } else if unit >= 32 && unit != 127 && value.len() < 4096 {
                    value.push(unit);
                }
            }
            WM_LBUTTONUP => {
                let x = lparam as i16 as i32;
                let y = (lparam >> 16) as i16 as i32;
                let mut client = RECT::default();
                unsafe { GetClientRect(window, &mut client) };
                if let Some(row) = menu.rows_layout.at(x, y, client.right) {
                    if row == form.names.len() {
                        return Some(true);
                    }
                    if row < form.names.len() {
                        form.active = row;
                    }
                }
            }
            WM_LBUTTONDOWN | WM_MOUSEMOVE | WM_MOUSEWHEEL => {}
            WM_KEYDOWN if wparam != VK_ESCAPE as usize => {}
            _ => return None,
        }
        Some(false)
    }) {
        if submit {
            close(window, true);
        } else {
            unsafe { InvalidateRect(window, null_mut(), 0) };
        }
        return 0;
    }
    match message {
        WM_CLOSE => close(window, false),
        WM_MOUSEACTIVATE => return MA_NOACTIVATE as LRESULT,
        // Like other popups, the menu closes when another window is activated.
        WM_ACTIVATE
            if wparam & 0xffff == WA_INACTIVE as usize && OPEN.with_borrow(Option::is_some) =>
        {
            close(window, false);
        }
        WM_KEYDOWN if wparam == VK_RETURN as usize => {
            if OPEN.with_borrow(|menu| menu.as_ref().is_some_and(|menu| !menu.matches.is_empty())) {
                choose(window);
            }
        }
        WM_KEYDOWN if wparam == VK_ESCAPE as usize => close(window, false),
        // The keyboard hook marks Ctrl+P with lparam 1.
        WM_KEYDOWN
            if wparam == b'P' as usize
                && (lparam == 1 || unsafe { GetKeyState(VK_CONTROL as i32) } < 0) =>
        {
            OPEN.with_borrow_mut(|menu| {
                if let Some(menu) = menu {
                    menu.toggle_pin();
                }
            });
            unsafe {
                InvalidateRect(window, null_mut(), 0);
            }
        }
        WM_CHAR => {
            OPEN.with_borrow_mut(|menu| {
                if let Some(menu) = menu {
                    menu.edit_query(wparam as u16);
                }
            });
            unsafe {
                InvalidateRect(window, null_mut(), 0);
            }
        }
        WM_KEYDOWN if wparam == VK_UP as usize || wparam == VK_DOWN as usize => {
            OPEN.with_borrow_mut(|menu| {
                if let Some(menu) = menu {
                    menu.index = if wparam == VK_UP as usize {
                        menu.index.saturating_sub(1)
                    } else {
                        (menu.index + 1).min(menu.matches.len().saturating_sub(1))
                    };
                    menu.first = menu
                        .first
                        .min(menu.index)
                        .max((menu.index + 1).saturating_sub(menu.rows));
                }
            });
            unsafe {
                InvalidateRect(window, null_mut(), 0);
            }
        }
        WM_MOUSEWHEEL => {
            OPEN.with_borrow_mut(|menu| {
                if let Some(menu) = menu {
                    let step = ((wparam >> 16) as i16 / 120).clamp(-8, 8) as isize;
                    menu.first = menu
                        .first
                        .saturating_add_signed(-step)
                        .min(menu.matches.len().saturating_sub(menu.rows));
                    let last = (menu.first + menu.rows)
                        .min(menu.matches.len())
                        .saturating_sub(1);
                    menu.index = menu.index.clamp(menu.first, last);
                }
            });
            unsafe {
                InvalidateRect(window, null_mut(), 0);
            }
        }
        WM_LBUTTONDOWN => {
            let pressed = OPEN.with_borrow_mut(|menu| {
                let Some(menu) = menu else { return false };
                menu.pressed = row_at(window, menu, lparam);
                menu.pressed.is_some()
            });
            if pressed {
                unsafe {
                    SetCapture(window);
                }
            }
        }
        WM_CAPTURECHANGED => OPEN.with_borrow_mut(|menu| {
            if let Some(menu) = menu {
                menu.pressed = None;
            }
        }),
        WM_LBUTTONUP => {
            let hit = OPEN.with_borrow_mut(|menu| {
                let Some(menu) = menu else { return false };
                let pressed = menu.pressed.take();
                let Some(hit) = row_at(window, menu, lparam) else {
                    return false;
                };
                if pressed != Some(hit) {
                    return false;
                }
                menu.index = hit.index;
                if hit.pin {
                    menu.toggle_pin();
                }
                !hit.pin
            });
            unsafe {
                ReleaseCapture();
            }
            if hit {
                choose(window);
            } else {
                unsafe {
                    InvalidateRect(window, null_mut(), 0);
                }
            }
        }
        WM_MOUSEMOVE => {
            let hit = OPEN.with_borrow_mut(|menu| {
                let Some(menu) = menu else { return false };
                let Some(hit) = row_at(window, menu, lparam) else {
                    return false;
                };
                let changed = menu.index != hit.index;
                menu.index = hit.index;
                changed
            });
            if hit {
                unsafe {
                    InvalidateRect(window, null_mut(), 0);
                }
            }
        }
        WM_ERASEBKGND => return 1,
        WM_PAINT => paint(window),
        _ => return unsafe { DefWindowProcW(window, message, wparam, lparam) },
    }
    0
}

fn font(dpi: i32, size: i32, weight: i32, face: &str) -> HFONT {
    unsafe {
        CreateFontW(
            -(size * dpi / 96),
            0,
            0,
            0,
            weight,
            0,
            0,
            0,
            DEFAULT_CHARSET as u32,
            OUT_DEFAULT_PRECIS as u32,
            CLIP_DEFAULT_PRECIS as u32,
            CLEARTYPE_QUALITY as u32,
            DEFAULT_PITCH as u32,
            wide(face).as_ptr(),
        )
    }
}

struct Fonts {
    body: HFONT,
    small: HFONT,
    code: HFONT,
}

impl Fonts {
    fn new(dpi: i32) -> Self {
        Self {
            body: font(dpi, 14, FW_NORMAL as i32, "Segoe UI"),
            small: font(dpi, 12, FW_NORMAL as i32, "Segoe UI"),
            // Segoe UI ships with every Windows version; a missing monospace
            // font is replaced with one that draws some letters as boxes.
            code: font(dpi, 13, FW_SEMIBOLD as i32, "Segoe UI"),
        }
    }
}

impl Drop for Fonts {
    fn drop(&mut self) {
        for font in [self.body, self.small, self.code] {
            if !font.is_null() {
                unsafe { DeleteObject(font) };
            }
        }
    }
}

fn fill(dc: HDC, rect: &RECT, color: u32, radius: i32) {
    unsafe {
        SetDCBrushColor(dc, color);
        SetDCPenColor(dc, color);
        let brush = SelectObject(dc, GetStockObject(DC_BRUSH));
        let pen = SelectObject(dc, GetStockObject(DC_PEN));
        if radius > 0 {
            RoundRect(
                dc,
                rect.left,
                rect.top,
                rect.right,
                rect.bottom,
                radius,
                radius,
            );
        } else {
            FillRect(dc, rect, GetStockObject(DC_BRUSH) as HBRUSH);
        }
        SelectObject(dc, brush);
        SelectObject(dc, pen);
    }
}

fn text(dc: HDC, font: HFONT, color: u32, value: &str, mut rect: RECT, flags: u32) {
    let value = wide(value);
    unsafe {
        SelectObject(dc, font);
        SetTextColor(dc, color);
        DrawTextW(
            dc,
            value.as_ptr(),
            value.len() as i32 - 1,
            &mut rect,
            DT_SINGLELINE | DT_VCENTER | DT_END_ELLIPSIS | DT_NOPREFIX | flags,
        );
    }
}

fn text_width(dc: HDC, font: HFONT, value: &str) -> i32 {
    let value = wide(value);
    let mut rect = RECT::default();
    unsafe {
        SelectObject(dc, font);
        DrawTextW(
            dc,
            value.as_ptr(),
            value.len() as i32 - 1,
            &mut rect,
            DT_SINGLELINE | DT_NOPREFIX | DT_CALCRECT,
        );
    }
    rect.right - rect.left
}

/// Draws a trigger as a rounded chip and returns where the chip ends.
fn chip(
    dc: HDC,
    fonts: &Fonts,
    palette: Palette,
    dpi: i32,
    label: &str,
    row: RECT,
    max: i32,
) -> i32 {
    let pad = 7 * dpi / 96;
    let width = (text_width(dc, fonts.code, label) + 2 * pad).min(max);
    let height = 22 * dpi / 96;
    let top = (row.top + row.bottom - height) / 2;
    let area = RECT {
        left: row.left,
        top,
        right: row.left + width,
        bottom: top + height,
    };
    fill(dc, &area, palette.chip, 8 * dpi / 96);
    text(
        dc,
        fonts.code,
        palette.chip_text,
        label,
        RECT {
            left: area.left + pad,
            right: area.right - pad,
            ..area
        },
        0,
    );
    area.right
}

fn paint(window: HWND) {
    let mut paint = PAINTSTRUCT::default();
    let target = unsafe { BeginPaint(window, &mut paint) };
    if target.is_null() {
        return;
    }
    let mut client = RECT::default();
    unsafe { GetClientRect(window, &mut client) };
    // Draw off screen first so rows do not flicker while the selection moves.
    let (dc, bitmap) = unsafe {
        let dc = CreateCompatibleDC(target);
        let bitmap = CreateCompatibleBitmap(target, client.right, client.bottom);
        (dc, bitmap)
    };
    let previous = unsafe { SelectObject(dc, bitmap) };
    let dpi = unsafe { windows_sys::Win32::UI::HiDpi::GetDpiForWindow(window) }.max(96) as i32;
    let fonts = Fonts::new(dpi);
    let old_font = unsafe { SelectObject(dc, fonts.body) };
    unsafe { SetBkMode(dc, TRANSPARENT as i32) };
    OPEN.with_borrow(|menu| {
        let Some(menu) = menu else { return };
        let palette = menu.palette;
        let scale = |value: i32| value * dpi / 96;
        fill(dc, &client, palette.background, 0);
        let header = RECT {
            bottom: menu.header,
            ..client
        };
        fill(dc, &header, palette.header, 0);
        fill(
            dc,
            &RECT {
                top: menu.header - 1,
                bottom: menu.header,
                ..client
            },
            palette.border,
            0,
        );
        let margin = menu.rows_layout.margin;
        let inset = |top: i32, bottom: i32| RECT {
            left: margin,
            top,
            right: client.right - margin,
            bottom,
        };
        if let Some(form) = &menu.parameters {
            text(
                dc,
                fonts.small,
                palette.muted,
                tr("rust.snippet.fill", "Fill snippet parameters · Tab to move"),
                RECT {
                    left: scale(14),
                    right: client.right - scale(14),
                    ..header
                },
                0,
            );
            let label_width = form
                .names
                .iter()
                .map(|name| text_width(dc, fonts.code, name) + scale(14))
                .max()
                .unwrap_or(0)
                .min(client.right / 3);
            for (index, name) in form.names.iter().enumerate() {
                let top = menu.rows_layout.top(index);
                let row = inset(top, top + menu.row_height);
                if index == form.active {
                    fill(dc, &row, palette.selected, scale(8));
                }
                chip(
                    dc,
                    &fonts,
                    palette,
                    dpi,
                    name,
                    RECT {
                        left: row.left + scale(8),
                        ..row
                    },
                    label_width,
                );
                let value = String::from_utf16_lossy(&form.values[index]);
                text(
                    dc,
                    fonts.body,
                    palette.text,
                    &value,
                    RECT {
                        left: row.left + scale(16) + label_width,
                        right: row.right - scale(8),
                        ..row
                    },
                    0,
                );
            }
            let top = menu.rows_layout.top(form.names.len());
            let row = inset(top, top + menu.row_height);
            text(
                dc,
                fonts.body,
                palette.chip_text,
                &format!("{}  ↵", tr("rust.snippet.insert", "Insert snippet")),
                RECT {
                    left: row.left + scale(8),
                    right: row.right - scale(8),
                    ..row
                },
                DT_RIGHT,
            );
            return;
        }
        let (hint, typed) = if menu.pin_error {
            (
                tr("rust.snippet.pinError", "Could not save favorites").to_owned(),
                false,
            )
        } else if !menu.query.is_empty() {
            (String::from_utf16_lossy(&menu.query), true)
        } else if menu.prepared.insertion_disabled {
            (
                tr(
                    "rust.snippet.disabledHere",
                    "Snippets are disabled in this app",
                )
                .to_owned(),
                false,
            )
        } else if menu.prepared.selection.is_none() && !menu.prepared.snippets.is_empty() {
            (
                tr(
                    "rust.snippet.readOnly",
                    "Open an editable field to insert snippets",
                )
                .to_owned(),
                false,
            )
        } else {
            (
                tr("rust.snippet.search", "Type to search · Ctrl+P to pin").to_owned(),
                false,
            )
        };
        text(
            dc,
            if typed { fonts.body } else { fonts.small },
            if typed { palette.text } else { palette.muted },
            &hint,
            RECT {
                left: scale(14),
                right: client.right - scale(14),
                ..header
            },
            0,
        );
        if menu.matches.is_empty() {
            let top = menu.rows_layout.top(0);
            text(
                dc,
                fonts.body,
                palette.muted,
                if menu.prepared.snippets.is_empty() {
                    tr("rust.snippet.empty", "Add snippets in Settings")
                } else {
                    tr("history.notFound", "Nothing found")
                },
                RECT {
                    left: scale(14),
                    top,
                    right: client.right - scale(14),
                    bottom: top + menu.row_height,
                },
                0,
            );
        }
        for (row_index, &index) in menu
            .matches
            .iter()
            .skip(menu.first)
            .take(menu.rows)
            .enumerate()
        {
            let snippet = &menu.prepared.snippets[index];
            let top = menu.rows_layout.top(row_index);
            let row = inset(top, top + menu.row_height);
            if menu.first + row_index == menu.index {
                fill(dc, &row, palette.selected, scale(8));
            }
            let content = RECT {
                left: row.left + scale(8),
                right: row.right - menu.pin_width,
                ..row
            };
            let after_chip = chip(
                dc,
                &fonts,
                palette,
                dpi,
                &snippet.trigger,
                content,
                (content.right - content.left) * 2 / 5,
            );
            let mut rest = RECT {
                left: after_chip + scale(10),
                ..content
            };
            let name = snippet.name.trim();
            let body = preview(snippet);
            let (main, extra) = if name.is_empty() {
                (body.as_str(), "")
            } else {
                (name, body.as_str())
            };
            text(dc, fonts.body, palette.text, main, rest, 0);
            if !extra.is_empty() {
                rest.left += text_width(dc, fonts.body, main) + scale(10);
                if rest.left < rest.right - scale(24) {
                    text(dc, fonts.small, palette.muted, extra, rest, 0);
                }
            }
            let pinned = menu.pins.contains(&snippet.trigger.to_lowercase());
            text(
                dc,
                fonts.body,
                if pinned { palette.star } else { palette.muted },
                if pinned { "★" } else { "☆" },
                RECT {
                    left: row.right - menu.pin_width,
                    right: row.right - scale(4),
                    ..row
                },
                DT_CENTER,
            );
        }
    });
    unsafe {
        // A thin border keeps the edge visible on Windows 10, where the shadow is faint.
        let palette = OPEN.with_borrow(|menu| menu.as_ref().map_or(LIGHT, |menu| menu.palette));
        SetDCBrushColor(dc, palette.border);
        FrameRect(dc, &client, GetStockObject(DC_BRUSH) as HBRUSH);
        BitBlt(target, 0, 0, client.right, client.bottom, dc, 0, 0, SRCCOPY);
        SelectObject(dc, old_font);
        SelectObject(dc, previous);
        DeleteObject(bitmap);
        DeleteDC(dc);
        EndPaint(window, &paint);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn search_matches_names_or_triggers_and_prioritizes_favorites() {
        let snippets = [
            Snippet {
                name: "Email Support".into(),
                trigger: ";help".into(),
                ..Default::default()
            },
            Snippet {
                name: "Адрес".into(),
                trigger: ";address".into(),
                ..Default::default()
            },
            Snippet {
                trigger: ";alternate".into(),
                text: "Alternate address\nSecond line".into(),
                ..Default::default()
            },
        ];
        let pins = HashSet::from([";alternate".to_owned()]);
        assert_eq!(matching(&snippets, &pins, ""), vec![2, 0, 1]);
        assert_eq!(matching(&snippets, &pins, "ADDRESS"), vec![2, 1]);
        assert_eq!(matching(&snippets, &pins, "АДРЕС ;addr"), vec![1]);
        assert!(matching(&snippets, &pins, "missing").is_empty());
    }

    #[test]
    fn clicks_select_the_row_that_is_painted_there() {
        for dpi in [96, 120, 144, 192] {
            let header = 38 * dpi / 96;
            let height = 36 * dpi / 96;
            let rows = Rows::new(dpi, header, height);
            let width = 440 * dpi / 96;
            let x = width / 2;
            for row in 0..4 {
                let top = rows.top(row);
                assert_eq!(rows.at(x, top, width), Some(row), "first pixel, dpi {dpi}");
                assert_eq!(
                    rows.at(x, top + height - 1, width),
                    Some(row),
                    "last pixel of row {row} at dpi {dpi}"
                );
                assert_eq!(rows.at(x, top + height, width), Some(row + 1));
            }
            // The gap under the header and the side padding select nothing.
            assert_eq!(rows.at(x, header, width), None);
            assert_eq!(rows.at(x, rows.top(0) - 1, width), None);
            assert_eq!(rows.at(rows.margin - 1, rows.top(0), width), None);
            assert_eq!(rows.at(width - rows.margin, rows.top(0), width), None);
            // With three parameters the bottom pixel of the last one is not
            // the Insert row.
            let last = rows.top(2) + height - 1;
            assert_eq!(rows.at(x, last, width), Some(2), "dpi {dpi}");
            assert_eq!(rows.at(x, last + 1, width), Some(3));
        }
    }

    #[test]
    fn pin_and_unpin_persist_for_the_next_popup() {
        let profile = tempfile::tempdir().unwrap();
        let mut menu = Menu {
            prepared: Prepared {
                origin: Origin(0, 0, 0, 0),
                selection: Some(Selection::Native {
                    document: Vec::new(),
                    selection: 0..0,
                }),
                insertion_disabled: false,
                sequence: 0,
                anchor: RECT::default(),
                snippets: vec![Snippet {
                    trigger: ";One".into(),
                    ..Default::default()
                }],
            },
            host: null_mut(),
            index: 0,
            first: 0,
            rows: 1,
            row_height: 32,
            header: 29,
            pressed: None,
            profile: profile.path().to_owned(),
            pins: HashSet::new(),
            matches: vec![0],
            query: Vec::new(),
            pin_width: 32,
            pin_error: false,
            parameters: None,
            palette: LIGHT,
            rows_layout: Rows::new(96, 38, 36),
        };
        let saved = || {
            serde_json::from_slice::<HashSet<String>>(
                &fs::read(profile.path().join("snippet-pins.json")).unwrap(),
            )
            .unwrap()
        };
        menu.toggle_pin();
        assert_eq!(saved(), HashSet::from([";one".into()]));
        menu.toggle_pin();
        assert!(saved().is_empty());
    }

    #[test]
    fn chooses_the_side_with_space_and_clamps_inside_the_monitor() {
        let work = RECT {
            left: -1200,
            top: 0,
            right: 0,
            bottom: 900,
        };
        let top = RECT {
            left: -80,
            top: 130,
            right: -77,
            bottom: 150,
        };
        let bottom = RECT {
            top: 830,
            bottom: 850,
            ..top
        };
        assert_eq!(popup_position(top, work, 320, 200), (-320, 155));
        assert_eq!(popup_position(bottom, work, 320, 200), (-320, 625));
        let middle = RECT {
            left: -700,
            right: -695,
            top: 440,
            bottom: 460,
        };
        assert_eq!(popup_position(middle, work, 320, 500), (-690, 400));
        assert_eq!(
            popup_position(
                RECT {
                    left: -80,
                    right: -77,
                    ..middle
                },
                work,
                320,
                500
            ),
            (-405, 400)
        );
    }
}
