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
const BACKGROUND: u32 = 0x00fbf9f7;
const SELECTED: u32 = 0x00ffead9;
const FOREGROUND: u32 = 0x00252018;

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
}

pub(super) struct Prepared {
    pub origin: Origin,
    pub selection: Selection,
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

pub(super) fn focus_existing() -> bool {
    let window = WINDOW.load(Ordering::Acquire) as HWND;
    if window.is_null() {
        return false;
    }
    unsafe {
        SetForegroundWindow(window);
    }
    true
}

pub(super) fn escape_outside() {
    let window = WINDOW.load(Ordering::Acquire) as HWND;
    if !window.is_null() && unsafe { GetForegroundWindow() } != window {
        unsafe {
            PostMessageW(window, WM_CLOSE, 0, 0);
        }
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

pub(super) fn show(host: HWND, profile: &Path) -> io::Result<()> {
    let Some(prepared) = PENDING.lock().ok().and_then(|mut slot| slot.take()) else {
        return Ok(());
    };
    if prepared.sequence != input::sequence()
        || WinEdit::default().target().ok() != Some(prepared.origin.target())
        || prepared.snippets.is_empty()
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
            WS_EX_TOOLWINDOW | WS_EX_TOPMOST,
            class.as_ptr(),
            title.as_ptr(),
            WS_POPUP | WS_BORDER,
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
    let dpi = unsafe { windows_sys::Win32::UI::HiDpi::GetDpiForWindow(window) }.max(96) as i32;
    let row_height = 32 * dpi / 96;
    let header = 29 * dpi / 96;
    let work = info.rcWork;
    let rows = prepared
        .snippets
        .len()
        .min(MAX_VISIBLE)
        .min(((work.bottom - work.top - header - 8) / row_height).max(1) as usize);
    let width = (350 * dpi / 96).min(work.right - work.left);
    let height = header + rows as i32 * row_height + 2;
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
        pin_width: 32 * dpi / 96,
        pin_error: false,
        parameters: None,
    }));
    WINDOW.store(window as usize, Ordering::Release);
    unsafe {
        SetWindowPos(window, HWND_TOPMOST, x, y, width, height, SWP_SHOWWINDOW);
        SetForegroundWindow(window);
    }
    Ok(())
}

fn popup_position(anchor: RECT, work: RECT, width: i32, height: i32) -> (i32, i32) {
    let below = anchor.bottom + 5;
    let above = anchor.top - height - 5;
    let y = if below + height <= work.bottom
        || above < work.top && work.bottom - anchor.bottom >= anchor.top - work.top
    {
        below
    } else {
        above
    };
    (
        anchor
            .left
            .clamp(work.left, (work.right - width).max(work.left)),
        y.clamp(work.top, (work.bottom - height).max(work.top)),
    )
}

fn choose(window: HWND) {
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
        let height = menu.header + (names.len() as i32 + 1) * menu.row_height + 2;
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
                selection: menu.prepared.selection,
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
    if unsafe { GetClientRect(window, &mut client) } == 0
        || x < 0
        || x >= client.right
        || y < menu.header
    {
        return None;
    }
    let index = menu.first + ((y - menu.header) / menu.row_height) as usize;
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
                let y = (lparam >> 16) as i16 as i32;
                if y >= menu.header {
                    let row = ((y - menu.header) / menu.row_height) as usize;
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
        WM_KEYDOWN if wparam == VK_RETURN as usize => {
            if OPEN.with_borrow(|menu| menu.as_ref().is_some_and(|menu| !menu.matches.is_empty())) {
                choose(window);
            }
        }
        WM_KEYDOWN if wparam == VK_ESCAPE as usize => close(window, false),
        WM_KEYDOWN if wparam == b'P' as usize && unsafe { GetKeyState(VK_CONTROL as i32) } < 0 => {
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

fn paint(window: HWND) {
    let mut paint = PAINTSTRUCT::default();
    let dc = unsafe { BeginPaint(window, &mut paint) };
    if !dc.is_null() {
        unsafe {
            let mut client = RECT::default();
            GetClientRect(window, &mut client);
            let brush = GetStockObject(DC_BRUSH) as HBRUSH;
            SetDCBrushColor(dc, BACKGROUND);
            FillRect(dc, &client, brush);
            SelectObject(dc, GetStockObject(DEFAULT_GUI_FONT));
            SetBkMode(dc, TRANSPARENT as i32);
            SetTextColor(dc, FOREGROUND);
            OPEN.with_borrow(|menu| {
                let Some(menu) = menu else { return };
                if let Some(form) = &menu.parameters {
                    draw_label(
                        dc,
                        tr("rust.snippet.fill", "Fill snippet parameters · Tab to move"),
                        RECT {
                            left: 12,
                            top: 0,
                            right: client.right - 12,
                            bottom: menu.header,
                        },
                    );
                    for (index, name) in form.names.iter().enumerate() {
                        let top = menu.header + index as i32 * menu.row_height;
                        let mut area = RECT {
                            left: 4,
                            top,
                            right: client.right - 4,
                            bottom: top + menu.row_height,
                        };
                        if index == form.active {
                            SetDCBrushColor(dc, SELECTED);
                            FillRect(dc, &area, brush);
                        }
                        area.left += 8;
                        let value = String::from_utf16_lossy(&form.values[index]);
                        draw_label(dc, &format!("{name}    {value}"), area);
                    }
                    let top = menu.header + form.names.len() as i32 * menu.row_height;
                    draw_label(
                        dc,
                        tr("rust.snippet.insert", "Insert snippet"),
                        RECT {
                            left: 12,
                            top,
                            right: client.right - 12,
                            bottom: top + menu.row_height,
                        },
                    );
                    return;
                }
                let query = if menu.pin_error {
                    tr("rust.snippet.pinError", "Could not save favorites").to_owned()
                } else if menu.query.is_empty() {
                    tr("rust.snippet.search", "Type to search · Ctrl+P to pin").to_owned()
                } else {
                    String::from_utf16_lossy(&menu.query)
                };
                draw_label(
                    dc,
                    &query,
                    RECT {
                        left: 12,
                        top: 0,
                        right: client.right - 12,
                        bottom: menu.header,
                    },
                );
                if menu.matches.is_empty() {
                    draw_label(
                        dc,
                        tr("history.notFound", "Nothing found"),
                        RECT {
                            left: 12,
                            top: menu.header,
                            right: client.right - 12,
                            bottom: menu.header + menu.row_height,
                        },
                    );
                }
                for (row, &index) in menu
                    .matches
                    .iter()
                    .skip(menu.first)
                    .take(menu.rows)
                    .enumerate()
                {
                    let snippet = &menu.prepared.snippets[index];
                    let y = menu.header + row as i32 * menu.row_height;
                    let mut rect = RECT {
                        left: 4,
                        top: y,
                        right: client.right - 4,
                        bottom: y + menu.row_height,
                    };
                    if menu.first + row == menu.index {
                        SetDCBrushColor(dc, SELECTED);
                        FillRect(dc, &rect, brush);
                    }
                    rect.left += 8;
                    rect.right -= menu.pin_width;
                    let preview: String = title(snippet)
                        .chars()
                        .take(120)
                        .collect::<String>()
                        .replace("{cursor}", "");
                    draw_label(dc, &format!("{}    {}", snippet.trigger, preview), rect);
                    let star = if menu.pins.contains(&snippet.trigger.to_lowercase()) {
                        "★"
                    } else {
                        "☆"
                    };
                    draw_label(
                        dc,
                        star,
                        RECT {
                            left: client.right - menu.pin_width + 6,
                            top: y,
                            right: client.right - 4,
                            bottom: y + menu.row_height,
                        },
                    );
                }
            });
        }
    }
    unsafe {
        EndPaint(window, &paint);
    }
}

fn draw_label(dc: HDC, text: &str, mut rect: RECT) {
    let text = wide(text);
    unsafe {
        DrawTextW(
            dc,
            text.as_ptr(),
            text.len() as i32 - 1,
            &mut rect,
            DT_SINGLELINE | DT_VCENTER | DT_END_ELLIPSIS | DT_NOPREFIX,
        );
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
    fn pin_and_unpin_persist_for_the_next_popup() {
        let profile = tempfile::tempdir().unwrap();
        let mut menu = Menu {
            prepared: Prepared {
                origin: Origin(0, 0, 0, 0),
                selection: Selection::Native {
                    document: Vec::new(),
                    selection: 0..0,
                },
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
    }
}
