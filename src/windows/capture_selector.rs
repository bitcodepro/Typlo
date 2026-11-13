use crate::{capture::MAX_POINTS, config::CaptureShape};
use std::{
    cell::Cell,
    io,
    ptr::null_mut,
    sync::mpsc::sync_channel,
    thread::{self, JoinHandle},
};
use windows_sys::Win32::{
    Foundation::{HINSTANCE, HWND, LPARAM, LRESULT, POINT, RECT, WPARAM},
    Graphics::{
        Dwm::{DWMWA_TRANSITIONS_FORCEDISABLED, DwmSetWindowAttribute},
        Gdi::*,
    },
    System::{LibraryLoader::GetModuleHandleW, Threading::GetCurrentThreadId},
    UI::{
        HiDpi::{DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2, SetProcessDpiAwarenessContext},
        Input::KeyboardAndMouse::{
            GetAsyncKeyState, VK_CONTROL, VK_ESCAPE, VK_LWIN, VK_MENU, VK_RWIN,
        },
        WindowsAndMessaging::*,
    },
};

const START: u32 = WM_APP + 1;
const FINISH: u32 = WM_APP + 2;
const CANCEL: u32 = WM_APP + 3;
const SHAPE: u32 = WM_APP + 4;
const OUTLINE_COLOR: u32 = 0x005528ff;
const OUTLINE_WIDTH: u32 = 4;

pub(crate) struct Selection {
    pub points: Vec<POINT>,
    pub shape: CaptureShape,
}

impl Selection {
    pub fn bounds(&self) -> RECT {
        bounds(&self.points)
    }

    fn valid(&self) -> bool {
        if self.points.len()
            < if self.shape == CaptureShape::Free {
                3
            } else {
                2
            }
        {
            return false;
        }
        let area = self.bounds();
        area.right > area.left && area.bottom > area.top
    }

    fn extend(&mut self, point: POINT) {
        if self
            .points
            .last()
            .is_some_and(|last| same_point(*last, point))
        {
            return;
        }
        if (self.shape == CaptureShape::Rectangle && self.points.len() == 2)
            || self.points.len() == MAX_POINTS
        {
            *self.points.last_mut().expect("selection has points") = point;
        } else {
            self.points.push(point);
        }
    }
}

pub(crate) fn select(shape: CaptureShape) -> io::Result<Option<Selection>> {
    unsafe { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) };
    let class = WindowClass::new()?;
    let control = class.window(HWND_MESSAGE, 0, &[])?;
    let outline = class.window(
        null_mut(),
        WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_TRANSPARENT,
        &wide("Typlo Capture Outline"),
    )?;
    if unsafe { SetLayeredWindowAttributes(outline.0, 0, 255, LWA_ALPHA) } == 0 {
        return Err(io::Error::last_os_error());
    }
    let no_animation = 1i32;
    unsafe {
        DwmSetWindowAttribute(
            outline.0,
            DWMWA_TRANSITIONS_FORCEDISABLED as u32,
            (&raw const no_animation).cast(),
            size_of::<i32>() as u32,
        );
    }
    let painter = OutlinePainter::new()?;
    let input = SelectionInput::start(control.0)?;
    if unsafe { SetTimer(control.0, 1, 16, None) } == 0 {
        return Err(io::Error::last_os_error());
    }

    let mut selection = Selection {
        points: Vec::new(),
        shape,
    };
    let mut previous = None;
    let mut message = MSG::default();
    let result = loop {
        match unsafe { GetMessageW(&mut message, null_mut(), 0, 0) } {
            -1 => return Err(io::Error::last_os_error()),
            0 => break None,
            _ => {}
        }
        if message.hwnd == control.0 {
            match message.message {
                START => {
                    selection.points.clear();
                    selection.points.push(message_point(&message));
                    previous = None;
                }
                FINISH if !selection.points.is_empty() => {
                    selection.extend(message_point(&message));
                    if selection.valid() {
                        break Some(selection);
                    }
                    selection.points.clear();
                    previous = None;
                }
                CANCEL | WM_CLOSE => break None,
                SHAPE if selection.points.is_empty() => {
                    selection.shape = if message.wParam == b'F' as usize {
                        CaptureShape::Free
                    } else {
                        CaptureShape::Rectangle
                    };
                }
                WM_TIMER => {
                    let mut cursor = POINT::default();
                    if unsafe { GetCursorPos(&mut cursor) } != 0
                        && !previous.is_some_and(|previous| same_point(previous, cursor))
                    {
                        previous = Some(cursor);
                        if !selection.points.is_empty() {
                            selection.extend(cursor);
                        }
                        painter.redraw(outline.0, &selection, cursor)?;
                    }
                }
                _ => {}
            }
        } else if message.hwnd == outline.0 && message.message == WM_CLOSE {
            break None;
        } else {
            unsafe { DispatchMessageW(&message) };
        }
    };
    drop(input);
    drop(outline);
    Ok(result)
}

fn same_point(left: POINT, right: POINT) -> bool {
    left.x == right.x && left.y == right.y
}

fn point(x: i32, y: i32) -> POINT {
    POINT { x, y }
}

fn crosshair(cursor: POINT) -> Vec<POINT> {
    let x = cursor.x;
    let y = cursor.y;
    vec![
        point(x - 2, y - 8),
        point(x + 2, y - 8),
        point(x + 2, y - 2),
        point(x + 8, y - 2),
        point(x + 8, y + 2),
        point(x + 2, y + 2),
        point(x + 2, y + 8),
        point(x - 2, y + 8),
        point(x - 2, y + 2),
        point(x - 8, y + 2),
        point(x - 8, y - 2),
        point(x - 2, y - 2),
    ]
}

fn message_point(message: &MSG) -> POINT {
    point(message.wParam as i32, message.lParam as i32)
}

fn bounds(points: &[POINT]) -> RECT {
    let first = points[0];
    points.iter().skip(1).fold(
        RECT {
            left: first.x,
            top: first.y,
            right: first.x,
            bottom: first.y,
        },
        |rect, point| RECT {
            left: rect.left.min(point.x),
            top: rect.top.min(point.y),
            right: rect.right.max(point.x),
            bottom: rect.bottom.max(point.y),
        },
    )
}

struct OutlinePainter {
    dc: HDC,
    pen: HPEN,
    previous: HGDIOBJ,
}

impl OutlinePainter {
    fn new() -> io::Result<Self> {
        let mut painter = Self {
            dc: unsafe { CreateCompatibleDC(null_mut()) },
            pen: null_mut(),
            previous: null_mut(),
        };
        if painter.dc.is_null() {
            return Err(io::Error::last_os_error());
        }
        let brush = LOGBRUSH {
            lbStyle: BS_SOLID,
            lbColor: OUTLINE_COLOR,
            lbHatch: 0,
        };
        painter.pen = unsafe {
            ExtCreatePen(
                PS_GEOMETRIC as u32,
                OUTLINE_WIDTH,
                &brush,
                0,
                std::ptr::null(),
            )
        };
        if painter.pen.is_null() {
            return Err(io::Error::last_os_error());
        }
        let previous = unsafe { SelectObject(painter.dc, painter.pen) };
        if previous.is_null() || previous as isize == -1 {
            return Err(io::Error::last_os_error());
        }
        painter.previous = previous;
        Ok(painter)
    }

    fn redraw(&self, window: HWND, selection: &Selection, cursor: POINT) -> io::Result<()> {
        let (mut path, crosshair) = if selection.points.len() < 2 {
            (crosshair(cursor), true)
        } else if selection.shape == CaptureShape::Rectangle {
            let rect = selection.bounds();
            (
                vec![
                    point(rect.left, rect.top),
                    point(rect.right, rect.top),
                    point(rect.right, rect.bottom),
                    point(rect.left, rect.bottom),
                    point(rect.left, rect.top),
                ],
                false,
            )
        } else {
            let mut path = selection.points.clone();
            path.push(path[0]);
            (path, false)
        };
        let area = bounds(&path);
        let x = area.left - 2;
        let y = area.top - 2;
        for point in &mut path {
            point.x -= x;
            point.y -= y;
        }
        let region = self.region(&path, crosshair)?;
        if unsafe { SetWindowRgn(window, region, 1) } == 0 {
            let error = io::Error::last_os_error();
            unsafe { DeleteObject(region) };
            return Err(error);
        }
        // The region is already set before the window can become visible.
        if unsafe {
            SetWindowPos(
                window,
                HWND_TOPMOST,
                x,
                y,
                area.right - x + 2,
                area.bottom - y + 2,
                SWP_NOACTIVATE | SWP_SHOWWINDOW,
            )
        } == 0
        {
            return Err(io::Error::last_os_error());
        }
        Ok(())
    }

    fn region(&self, points: &[POINT], crosshair: bool) -> io::Result<HRGN> {
        let region = unsafe {
            if crosshair {
                CreatePolygonRgn(points.as_ptr(), points.len() as i32, WINDING)
            } else if BeginPath(self.dc) != 0
                && Polyline(self.dc, points.as_ptr(), points.len() as i32) != 0
                && EndPath(self.dc) != 0
                && WidenPath(self.dc) != 0
            {
                PathToRegion(self.dc)
            } else {
                null_mut()
            }
        };
        if region.is_null() {
            let error = io::Error::last_os_error();
            unsafe { AbortPath(self.dc) };
            return Err(error);
        }
        Ok(region)
    }
}

impl Drop for OutlinePainter {
    fn drop(&mut self) {
        unsafe {
            if !self.previous.is_null() {
                SelectObject(self.dc, self.previous);
            }
            if !self.pen.is_null() {
                DeleteObject(self.pen);
            }
            if !self.dc.is_null() {
                DeleteDC(self.dc);
            }
        }
    }
}

struct WindowClass {
    instance: HINSTANCE,
    name: Vec<u16>,
}

impl WindowClass {
    fn new() -> io::Result<Self> {
        let instance = unsafe { GetModuleHandleW(null_mut()) };
        let name = wide("TyploCaptureSelector");
        let class = WNDCLASSW {
            lpfnWndProc: Some(window_proc),
            hInstance: instance,
            lpszClassName: name.as_ptr(),
            ..Default::default()
        };
        if unsafe { RegisterClassW(&class) } == 0 {
            return Err(io::Error::last_os_error());
        }
        Ok(Self { instance, name })
    }

    fn window(&self, parent: HWND, extended: u32, caption: &[u16]) -> io::Result<Window> {
        let caption = if caption.is_empty() {
            std::ptr::null()
        } else {
            caption.as_ptr()
        };
        let style = if parent == HWND_MESSAGE { 0 } else { WS_POPUP };
        let window = unsafe {
            CreateWindowExW(
                extended,
                self.name.as_ptr(),
                caption,
                style,
                0,
                0,
                1,
                1,
                parent,
                null_mut(),
                self.instance,
                std::ptr::null(),
            )
        };
        if window.is_null() {
            Err(io::Error::last_os_error())
        } else {
            Ok(Window(window))
        }
    }
}

impl Drop for WindowClass {
    fn drop(&mut self) {
        unsafe { UnregisterClassW(self.name.as_ptr(), self.instance) };
    }
}

struct Window(HWND);

impl Drop for Window {
    fn drop(&mut self) {
        unsafe { DestroyWindow(self.0) };
    }
}

unsafe extern "system" fn window_proc(
    window: HWND,
    message: u32,
    word: WPARAM,
    long: LPARAM,
) -> LRESULT {
    match message {
        WM_CLOSE => {
            unsafe { PostQuitMessage(0) };
            0
        }
        WM_ERASEBKGND => 1,
        WM_PAINT => {
            let mut paint = PAINTSTRUCT::default();
            unsafe {
                let dc = BeginPaint(window, &mut paint);
                if !dc.is_null() {
                    SetDCBrushColor(dc, OUTLINE_COLOR);
                    FillRect(dc, &paint.rcPaint, GetStockObject(DC_BRUSH) as HBRUSH);
                }
                EndPaint(window, &paint);
            }
            0
        }
        _ => unsafe { DefWindowProcW(window, message, word, long) },
    }
}

fn wide(text: &str) -> Vec<u16> {
    text.encode_utf16().chain([0]).collect()
}

struct SelectionInput {
    id: u32,
    thread: Option<JoinHandle<()>>,
}

impl SelectionInput {
    fn start(window: HWND) -> io::Result<Self> {
        let (send, receive) = sync_channel(1);
        let target = window as usize;
        let thread = thread::Builder::new()
            .name("typlo-capture-input".into())
            .spawn(move || unsafe {
                let mut message = MSG::default();
                PeekMessageW(&mut message, null_mut(), 0, 0, PM_NOREMOVE);
                INPUT.set(InputState {
                    target,
                    ..InputState::default()
                });
                let instance = GetModuleHandleW(null_mut());
                let mouse = SetWindowsHookExW(WH_MOUSE_LL, Some(mouse_hook), instance, 0);
                let keyboard = if mouse.is_null() {
                    null_mut()
                } else {
                    SetWindowsHookExW(WH_KEYBOARD_LL, Some(keyboard_hook), instance, 0)
                };
                if mouse.is_null() || keyboard.is_null() {
                    let _ = send.send(Err(io::Error::last_os_error()));
                } else if send.send(Ok(GetCurrentThreadId())).is_ok() {
                    while GetMessageW(&mut message, null_mut(), 0, 0) > 0 {
                        DispatchMessageW(&message);
                    }
                }
                if !keyboard.is_null() {
                    UnhookWindowsHookEx(keyboard);
                }
                if !mouse.is_null() {
                    UnhookWindowsHookEx(mouse);
                }
            })?;
        match receive
            .recv()
            .map_err(io::Error::other)
            .and_then(|result| result)
        {
            Ok(id) => Ok(Self {
                id,
                thread: Some(thread),
            }),
            Err(error) => {
                let _ = thread.join();
                Err(error)
            }
        }
    }
}

impl Drop for SelectionInput {
    fn drop(&mut self) {
        unsafe { PostThreadMessageW(self.id, WM_QUIT, 0, 0) };
        if let Some(thread) = self.thread.take() {
            let _ = thread.join();
        }
    }
}

#[derive(Clone, Copy, Default)]
struct InputState {
    target: usize,
    left: bool,
    right: bool,
    keys: u8,
}

thread_local! { static INPUT: Cell<InputState> = Cell::new(InputState::default()); }

unsafe extern "system" fn mouse_hook(code: i32, message: WPARAM, data: LPARAM) -> LRESULT {
    if code == HC_ACTION as i32
        && data != 0
        && matches!(
            message as u32,
            WM_LBUTTONDOWN | WM_LBUTTONUP | WM_RBUTTONDOWN | WM_RBUTTONUP
        )
    {
        let mut input = INPUT.get();
        let target = input.target as HWND;
        let cursor = unsafe { (*(data as *const MSLLHOOKSTRUCT)).pt };
        let suppress = match message as u32 {
            WM_LBUTTONDOWN => {
                input.left =
                    unsafe { PostMessageW(target, START, cursor.x as usize, cursor.y as isize) }
                        != 0;
                input.left
            }
            WM_LBUTTONUP if input.left => {
                input.left = false;
                unsafe { PostMessageW(target, FINISH, cursor.x as usize, cursor.y as isize) };
                true
            }
            WM_RBUTTONDOWN => {
                input.right = true;
                true
            }
            WM_RBUTTONUP if input.right => {
                input.right = false;
                unsafe { PostMessageW(target, CANCEL, 0, 0) };
                true
            }
            _ => false,
        };
        INPUT.set(input);
        if suppress {
            return 1;
        }
    }
    unsafe { CallNextHookEx(null_mut(), code, message, data) }
}

unsafe extern "system" fn keyboard_hook(code: i32, message: WPARAM, data: LPARAM) -> LRESULT {
    if code == HC_ACTION as i32 && data != 0 {
        let key = unsafe { (*(data as *const KBDLLHOOKSTRUCT)).vkCode };
        let bit = match key {
            key if key == VK_ESCAPE as u32 => 1,
            key if key == b'R' as u32 => 2,
            key if key == b'F' as u32 => 4,
            _ => 0,
        };
        if bit != 0 {
            let mut input = INPUT.get();
            let down = matches!(message as u32, WM_KEYDOWN | WM_SYSKEYDOWN);
            let held = input.keys & bit != 0;
            let modifiers = [VK_CONTROL, VK_MENU, VK_LWIN, VK_RWIN];
            if down
                && (held
                    || modifiers
                        .iter()
                        .all(|&key| unsafe { GetAsyncKeyState(key as i32) } >= 0))
            {
                input.keys |= bit;
                INPUT.set(input);
                return 1;
            }
            if !down && held {
                input.keys &= !bit;
                INPUT.set(input);
                let event = if bit == 1 { CANCEL } else { SHAPE };
                unsafe { PostMessageW(input.target as HWND, event, key as usize, 0) };
                return 1;
            }
        }
    }
    unsafe { CallNextHookEx(null_mut(), code, message, data) }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_stroked_region_excludes_the_interior_and_the_rest_of_the_screen() {
        let painter = OutlinePainter::new().unwrap();
        let points = [
            point(2, 2),
            point(90, 2),
            point(90, 75),
            point(2, 75),
            point(2, 2),
        ];
        let region = painter.region(&points, false).unwrap();
        unsafe {
            assert_ne!(PtInRegion(region, 45, 2), 0);
            assert_eq!(PtInRegion(region, 45, 40), 0);
            assert_eq!(PtInRegion(region, 140, 120), 0);
            DeleteObject(region);
        }
        let cross = crosshair(point(10, 10));
        let region = painter.region(&cross, true).unwrap();
        unsafe {
            assert_ne!(PtInRegion(region, 10, 10), 0);
            assert_eq!(PtInRegion(region, 4, 4), 0);
            DeleteObject(region);
        }
        let triangle = [point(10, 10), point(80, 20), point(25, 85), point(10, 10)];
        let region = painter.region(&triangle, false).unwrap();
        unsafe {
            assert_ne!(PtInRegion(region, 45, 15), 0);
            assert_eq!(PtInRegion(region, 30, 30), 0);
            DeleteObject(region);
        }
    }
}
