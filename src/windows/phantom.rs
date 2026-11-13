use super::{snippet_menu::DpiScope, wide};
use std::{
    cell::{Cell, RefCell},
    ptr::null_mut,
    sync::{
        OnceLock,
        atomic::{AtomicBool, Ordering},
    },
};
use windows_sys::Win32::{
    Foundation::{HWND, LPARAM, LRESULT, POINT, RECT, SIZE, WPARAM},
    Graphics::Gdi::*,
    System::LibraryLoader::GetModuleHandleW,
    UI::WindowsAndMessaging::*,
};

pub(super) const EVENT: u32 = WM_APP + 18;
pub(super) const START: usize = 1;
pub(super) const MOVE: usize = 2;
pub(super) const FINISH: usize = 3;
pub(super) const RESIZE: usize = 4;
pub(super) const CLEAR: usize = 5;
pub(super) const PRESET: usize = 6;
const RED: u32 = 0x002323f5;
const TRANSPARENT_KEY: u32 = 0x00ff00ff;
const INITIAL_WIDTH: i32 = 6;
const MIN_WIDTH: i32 = 2;
const MAX_WIDTH: i32 = 40;
const BRUSH_WIDTHS: [i32; 9] = [2, 4, 6, 9, 13, 18, 24, 32, 40];
const MAX_STROKES: usize = 128;
const MAX_POINTS_PER_STROKE: usize = 4096;
const MAX_POINTS: usize = 16384;
static HAS_INK: AtomicBool = AtomicBool::new(false);

thread_local! {
    static WINDOW: Cell<HWND> = const { Cell::new(null_mut()) };
    static WINDOW_BOUNDS: Cell<Option<RECT>> = const { Cell::new(None) };
    static SURFACE: RefCell<Option<Surface>> = const { RefCell::new(None) };
    static CANVAS: RefCell<Canvas> = RefCell::new(Canvas::default());
}

#[derive(Default)]
struct Canvas {
    strokes: Vec<Stroke>,
    pointer: Option<POINT>,
    width: i32,
    wheel: i32,
    points: usize,
    ink_bounds: Option<RECT>,
    damage: Option<RECT>,
    full_redraw: bool,
}

struct Stroke {
    width: i32,
    points: Vec<POINT>,
    bounds: RECT,
}

impl Canvas {
    fn width(&self) -> i32 {
        if self.width == 0 {
            INITIAL_WIDTH
        } else {
            self.width
        }
    }

    fn start(&mut self, point: POINT) {
        self.pointer = Some(point);
        self.wheel = 0;
        self.stroke(vec![point]);
    }

    fn stroke(&mut self, points: Vec<POINT>) {
        let width = self.width();
        let bounds = points
            .iter()
            .copied()
            .map(|point| point_bounds(point, width))
            .reduce(union)
            .expect("a stroke contains a point");
        self.points += points.len();
        self.strokes.push(Stroke {
            width,
            points,
            bounds,
        });
        self.ink_bounds = Some(extend(self.ink_bounds, bounds));
        self.damage = Some(extend(self.damage, bounds));
        let mut evicted = false;
        while self.strokes.len() > MAX_STROKES || self.points > MAX_POINTS {
            self.points -= self.strokes.remove(0).points.len();
            evicted = true;
        }
        if evicted {
            self.ink_bounds = self
                .strokes
                .iter()
                .map(|stroke| stroke.bounds)
                .reduce(union);
            self.full_redraw = true;
        }
    }

    fn draw_to(&mut self, point: POINT) {
        let Some(previous) = self.pointer.replace(point) else {
            self.pointer = None;
            return;
        };
        if previous.x == point.x && previous.y == point.y {
            return;
        }
        let width = self.width();
        if let Some(stroke) = self.strokes.last_mut()
            && stroke.width == width
            && stroke.points.len() < MAX_POINTS_PER_STROKE
            && self.points < MAX_POINTS
        {
            stroke.points.push(point);
            let end = point_bounds(point, width);
            stroke.bounds = union(stroke.bounds, end);
            self.ink_bounds = Some(extend(self.ink_bounds, end));
            let segment = union(point_bounds(previous, width), end);
            self.damage = Some(extend(self.damage, segment));
            self.points += 1;
        } else {
            self.stroke(vec![previous, point]);
        }
    }

    fn resize(&mut self, point: POINT, delta: i32) {
        if self.pointer.is_none() {
            return;
        }
        self.draw_to(point);
        self.wheel += delta;
        let steps = self.wheel / 120;
        self.wheel %= 120;
        self.width = (self.width() + steps).clamp(MIN_WIDTH, MAX_WIDTH);
    }

    fn preset(&mut self, digit: usize) {
        if self.pointer.is_some()
            && let Some(&width) = digit
                .checked_sub(1)
                .and_then(|index| BRUSH_WIDTHS.get(index))
        {
            self.width = width;
            self.wheel = 0;
        }
    }

    fn erase(&mut self) {
        self.strokes.clear();
        self.pointer = None;
        self.points = 0;
        self.wheel = 0;
        self.ink_bounds = None;
        self.damage = None;
        self.full_redraw = false;
    }

    fn pointer_bounds(&self) -> Option<RECT> {
        let point = self.pointer?;
        let mut rect = point_bounds(point, self.width());
        rect.right = point.x + self.width() / 2 + 45;
        rect.top = rect.top.min(point.y - 13);
        rect.bottom = rect.bottom.max(point.y + 13);
        Some(rect)
    }

    fn bounds(&self) -> Option<RECT> {
        self.pointer_bounds()
            .map(|rect| extend(self.ink_bounds, rect))
            .or(self.ink_bounds)
    }
}

fn point_bounds(point: POINT, width: i32) -> RECT {
    let margin = width / 2 + 5;
    RECT {
        left: point.x - margin,
        top: point.y - margin,
        right: point.x + margin + 1,
        bottom: point.y + margin + 1,
    }
}

fn union(a: RECT, b: RECT) -> RECT {
    RECT {
        left: a.left.min(b.left),
        top: a.top.min(b.top),
        right: a.right.max(b.right),
        bottom: a.bottom.max(b.bottom),
    }
}

fn extend(a: Option<RECT>, b: RECT) -> RECT {
    a.map_or(b, |a| union(a, b))
}

fn overlaps(a: RECT, b: RECT) -> bool {
    a.left < b.right && a.right > b.left && a.top < b.bottom && a.bottom > b.top
}

fn rect_edges(rect: RECT) -> [i32; 4] {
    [rect.left, rect.top, rect.right, rect.bottom]
}

pub(super) fn post(host: HWND, kind: usize, point: POINT, delta: i16) -> bool {
    let x = point.x.clamp(i16::MIN as i32, i16::MAX as i32) as i16;
    let y = point.y.clamp(i16::MIN as i32, i16::MAX as i32) as i16;
    let packed = u32::from(x as u16) | (u32::from(y as u16) << 16);
    let posted = unsafe {
        PostMessageW(
            host,
            EVENT,
            kind | ((delta as u16 as usize) << 16),
            packed as LPARAM,
        ) != 0
    };
    if posted && kind == START {
        HAS_INK.store(true, Ordering::Release);
    }
    posted
}

pub(super) fn has_ink() -> bool {
    HAS_INK.load(Ordering::Acquire)
}

pub(super) fn clear() {
    HAS_INK.store(false, Ordering::Release);
    CANVAS.with_borrow_mut(Canvas::erase);
    WINDOW_BOUNDS.set(None);
    let window = WINDOW.get();
    if !window.is_null() {
        unsafe {
            ShowWindow(window, SW_HIDE);
        }
    }
    SURFACE.with_borrow_mut(|surface| *surface = None);
}

pub(super) fn handle(wparam: WPARAM, lparam: LPARAM) {
    let point = POINT {
        x: lparam as i16 as i32,
        y: (lparam >> 16) as i16 as i32,
    };
    let kind = wparam & 0xffff;
    if kind == CLEAR {
        clear();
        return;
    }
    CANVAS.with_borrow_mut(|canvas| {
        let before = canvas.pointer_bounds();
        match kind {
            START => {
                HAS_INK.store(true, Ordering::Release);
                canvas.start(point);
            }
            MOVE => canvas.draw_to(point),
            FINISH => {
                canvas.draw_to(point);
                canvas.pointer = None;
            }
            RESIZE => canvas.resize(point, (wparam >> 16) as i16 as i32),
            PRESET => canvas.preset(wparam >> 16),
            _ => {}
        }
        for rect in [before, canvas.pointer_bounds()].into_iter().flatten() {
            canvas.damage = Some(extend(canvas.damage, rect));
        }
    });
    redraw();
}

fn redraw() {
    let (area, damage, full) = CANVAS.with_borrow_mut(|canvas| {
        (
            canvas.bounds(),
            canvas.damage.take(),
            std::mem::take(&mut canvas.full_redraw),
        )
    });
    let Some(area) = area else {
        return;
    };
    let _dpi = DpiScope::new();
    let mut window = WINDOW.get();
    if window.is_null() {
        window = make_window();
        if window.is_null() {
            return;
        }
        WINDOW.set(window);
    }
    let previous = WINDOW_BOUNDS.get();
    let moved = previous.map(rect_edges) != Some(rect_edges(area));
    let Some(dirty) = (if moved || full { Some(area) } else { damage }) else {
        return;
    };
    let painted = SURFACE.with_borrow_mut(|surface| {
        let size = SIZE {
            cx: area.right - area.left,
            cy: area.bottom - area.top,
        };
        if surface
            .as_ref()
            .is_none_or(|surface| surface.capacity.cx < size.cx || surface.capacity.cy < size.cy)
        {
            *surface = Surface::new(size);
        }
        surface
            .as_ref()
            .is_some_and(|surface| surface.present(window, area, size, dirty, moved))
    });
    if painted {
        WINDOW_BOUNDS.set(Some(area));
        if previous.is_none() {
            unsafe { ShowWindow(window, SW_SHOWNOACTIVATE) };
        }
    } else {
        // Repaint the entire surface on the next event if presentation failed.
        CANVAS.with_borrow_mut(|canvas| canvas.full_redraw = true);
    }
}

struct Surface {
    dc: HDC,
    bitmap: HBITMAP,
    previous: HGDIOBJ,
    capacity: SIZE,
}

impl Surface {
    fn new(size: SIZE) -> Option<Self> {
        let reserve = |edge: i32| (edge + 127) & !127;
        let capacity = SIZE {
            cx: reserve(size.cx),
            cy: reserve(size.cy),
        };
        unsafe {
            let screen = GetDC(null_mut());
            if screen.is_null() {
                return None;
            }
            let dc = CreateCompatibleDC(screen);
            let bitmap = CreateCompatibleBitmap(screen, capacity.cx, capacity.cy);
            ReleaseDC(null_mut(), screen);
            let previous = if dc.is_null() || bitmap.is_null() {
                null_mut()
            } else {
                SelectObject(dc, bitmap)
            };
            if previous.is_null() {
                if !dc.is_null() {
                    DeleteDC(dc);
                }
                if !bitmap.is_null() {
                    DeleteObject(bitmap);
                }
                return None;
            }
            Some(Self {
                dc,
                bitmap,
                previous,
                capacity,
            })
        }
    }

    fn present(&self, window: HWND, area: RECT, size: SIZE, dirty: RECT, moved: bool) -> bool {
        let position = POINT {
            x: area.left,
            y: area.top,
        };
        let source = POINT::default();
        let local = RECT {
            left: (dirty.left - area.left).max(0),
            top: (dirty.top - area.top).max(0),
            right: (dirty.right - area.left).min(size.cx),
            bottom: (dirty.bottom - area.top).min(size.cy),
        };
        unsafe {
            let saved = SaveDC(self.dc);
            if saved == 0 {
                return false;
            }
            IntersectClipRect(self.dc, local.left, local.top, local.right, local.bottom);
            SetDCBrushColor(self.dc, TRANSPARENT_KEY);
            FillRect(self.dc, &local, GetStockObject(DC_BRUSH) as HBRUSH);
            CANVAS.with_borrow(|canvas| paint_canvas(self.dc, position, dirty, canvas));
            RestoreDC(self.dc, saved);
            // Publish the complete frame and its new bounds in one compositor update.
            // A resized window never exposes a background before its ink is ready.
            let update = UPDATELAYEREDWINDOWINFO {
                cbSize: size_of::<UPDATELAYEREDWINDOWINFO>() as u32,
                pptDst: &position,
                psize: &size,
                hdcSrc: self.dc,
                pptSrc: &source,
                crKey: TRANSPARENT_KEY,
                dwFlags: ULW_COLORKEY,
                prcDirty: if moved { std::ptr::null() } else { &local },
                ..Default::default()
            };
            UpdateLayeredWindowIndirect(window, &update) != 0
        }
    }
}

impl Drop for Surface {
    fn drop(&mut self) {
        unsafe {
            SelectObject(self.dc, self.previous);
            DeleteObject(self.bitmap);
            DeleteDC(self.dc);
        }
    }
}

fn make_window() -> HWND {
    let class = wide("TyploRustPhantomDrawing");
    let instance = unsafe { GetModuleHandleW(null_mut()) };
    static REGISTERED: OnceLock<bool> = OnceLock::new();
    if !*REGISTERED.get_or_init(|| unsafe {
        RegisterClassW(&WNDCLASSW {
            lpfnWndProc: Some(window_proc),
            hInstance: instance,
            lpszClassName: class.as_ptr(),
            ..Default::default()
        }) != 0
    }) {
        return null_mut();
    }
    unsafe {
        CreateWindowExW(
            WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST,
            class.as_ptr(),
            wide("Typlo screen drawing").as_ptr(),
            WS_POPUP,
            0,
            0,
            1,
            1,
            null_mut(),
            null_mut(),
            instance,
            null_mut(),
        )
    }
}

unsafe extern "system" fn window_proc(
    window: HWND,
    message: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    match message {
        WM_NCHITTEST => HTTRANSPARENT as LRESULT,
        WM_ERASEBKGND => 1,
        _ => unsafe { DefWindowProcW(window, message, wparam, lparam) },
    }
}

fn paint_canvas(dc: HDC, origin: POINT, dirty: RECT, canvas: &Canvas) {
    unsafe {
        let mut old_origin = POINT::default();
        if SetViewportOrgEx(dc, -origin.x, -origin.y, &mut old_origin) == 0 {
            return;
        }
        SetDCBrushColor(dc, RED);
        let old_brush = SelectObject(dc, GetStockObject(DC_BRUSH));
        for stroke in canvas
            .strokes
            .iter()
            .filter(|stroke| overlaps(stroke.bounds, dirty))
        {
            let brush = LOGBRUSH {
                lbStyle: BS_SOLID,
                lbColor: RED,
                lbHatch: 0,
            };
            let pen = ExtCreatePen(
                (PS_GEOMETRIC | PS_SOLID | PS_ENDCAP_ROUND | PS_JOIN_ROUND) as u32,
                stroke.width as u32,
                &brush,
                0,
                null_mut(),
            );
            if pen.is_null() {
                continue;
            }
            let old_pen = SelectObject(dc, pen);
            if stroke.points.len() == 1 {
                let point = stroke.points[0];
                SelectObject(dc, GetStockObject(NULL_PEN));
                let radius = stroke.width / 2;
                Ellipse(
                    dc,
                    point.x - radius,
                    point.y - radius,
                    point.x + radius + 1,
                    point.y + radius + 1,
                );
            } else {
                Polyline(dc, stroke.points.as_ptr(), stroke.points.len() as i32);
            }
            SelectObject(dc, old_pen);
            DeleteObject(pen);
        }
        if let Some(point) = canvas.pointer {
            let radius = canvas.width() / 2;
            let pen = CreatePen(PS_SOLID, 2, 0x00ffffff);
            if !pen.is_null() {
                let old_pen = SelectObject(dc, pen);
                SelectObject(dc, GetStockObject(NULL_BRUSH));
                Ellipse(
                    dc,
                    point.x - radius - 2,
                    point.y - radius - 2,
                    point.x + radius + 3,
                    point.y + radius + 3,
                );
                SelectObject(dc, old_pen);
                DeleteObject(pen);
            }
            SetTextColor(dc, RED);
            SetBkColor(dc, 0x00ffffff);
            let label = wide(&format!("{} px", canvas.width()));
            TextOutW(
                dc,
                point.x + radius + 5,
                point.y - 8,
                label.as_ptr(),
                label.len() as i32 - 1,
            );
        }
        SelectObject(dc, old_brush);
        SetViewportOrgEx(dc, old_origin.x, old_origin.y, null_mut());
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn layered_frame_preserves_ink_when_growing_and_updating_dirty_region() {
        let _dpi = DpiScope::new();
        let window = make_window();
        assert!(!window.is_null());
        let first = POINT { x: -90, y: -70 };
        let second = POINT { x: 10, y: -40 };
        CANVAS.with_borrow_mut(|canvas| canvas.start(first));
        let area = CANVAS.with_borrow(|canvas| canvas.bounds().unwrap());
        let first_size = SIZE {
            cx: area.right - area.left,
            cy: area.bottom - area.top,
        };
        let surface = Surface::new(first_size).unwrap();
        assert!(surface.present(window, area, first_size, area, true));
        CANVAS.with_borrow_mut(|canvas| canvas.draw_to(second));
        let area = CANVAS.with_borrow(|canvas| canvas.bounds().unwrap());
        let size = SIZE {
            cx: area.right - area.left,
            cy: area.bottom - area.top,
        };
        let surface = Surface::new(size).unwrap();
        assert!(surface.present(window, area, size, area, true));
        let mut actual = RECT::default();
        assert_ne!(unsafe { GetWindowRect(window, &mut actual) }, 0);
        assert_eq!(rect_edges(actual), rect_edges(area));
        let dirty = CANVAS.with_borrow_mut(|canvas| {
            let dirty = canvas.pointer_bounds().unwrap();
            canvas.pointer = None;
            dirty
        });
        assert!(surface.present(window, area, size, dirty, false));
        let pixel =
            |point: POINT| unsafe { GetPixel(surface.dc, point.x - area.left, point.y - area.top) };
        assert_eq!(pixel(first), RED);
        assert_eq!(pixel(POINT { x: 35, y: -40 }), TRANSPARENT_KEY);
        unsafe { DestroyWindow(window) };
    }

    #[test]
    fn cached_bounds_and_damage_cover_negative_coordinates_and_eviction() {
        let mut canvas = Canvas::default();
        canvas.start(POINT { x: -1500, y: -200 });
        canvas.damage = None;
        canvas.draw_to(POINT { x: -1350, y: -40 });
        let dirty = canvas.damage.unwrap();
        assert!(dirty.left < -1500 && dirty.right > -1350);
        for i in 0..MAX_STROKES {
            canvas.start(POINT {
                x: i as i32 + 1000,
                y: 20,
            });
        }
        assert!(canvas.full_redraw);
        assert!(canvas.bounds().unwrap().left > 900);
    }

    #[test]
    #[ignore = "Measures drawing geometry with virtual coordinates across two monitors"]
    fn measure_drawing_bounds() {
        use std::{hint::black_box, time::Instant};
        let mut canvas = Canvas::default();
        for stroke in 0..80 {
            canvas.start(POINT {
                x: -1920 + stroke * 45,
                y: -120,
            });
            for point in 1..=160 {
                canvas.draw_to(POINT {
                    x: -1920 + stroke * 45 + point,
                    y: -120 + point,
                });
            }
        }
        let old = || {
            canvas
                .strokes
                .iter()
                .flat_map(|stroke| {
                    stroke
                        .points
                        .iter()
                        .copied()
                        .map(move |point| point_bounds(point, stroke.width))
                })
                .reduce(union)
        };
        assert_eq!(old().map(rect_edges), canvas.ink_bounds.map(rect_edges));
        let iterations = 2000;
        let started = Instant::now();
        for _ in 0..iterations {
            black_box(old());
        }
        let old_time = started.elapsed().as_secs_f64() * 1000.;
        let started = Instant::now();
        for _ in 0..iterations {
            black_box(black_box(&canvas).bounds());
        }
        println!(
            "{iterations} geometry reads: full scan {old_time:.3} ms, cached {:.3} ms",
            started.elapsed().as_secs_f64() * 1000.
        );
        let origin = POINT { x: -1920, y: -120 };
        let dirty = RECT {
            left: -1880,
            top: -80,
            right: -1820,
            bottom: -20,
        };
        let frames = 120;
        unsafe {
            let dc = CreateCompatibleDC(null_mut());
            let bitmap = CreateBitmap(256, 256, 1, 32, std::ptr::null());
            assert!(!dc.is_null() && !bitmap.is_null());
            let old = SelectObject(dc, bitmap);
            IntersectClipRect(dc, 40, 40, 100, 100);
            for (name, region) in [
                ("all strokes", canvas.bounds().unwrap()),
                ("dirty region", dirty),
            ] {
                let started = Instant::now();
                for _ in 0..frames {
                    paint_canvas(dc, origin, region, &canvas);
                    GdiFlush();
                }
                println!(
                    "{frames} GDI frames, {name}: {:.3} ms",
                    started.elapsed().as_secs_f64() * 1000.
                );
            }
            SelectObject(dc, old);
            DeleteObject(bitmap);
            DeleteDC(dc);
        }
    }

    #[test]
    fn wheel_changes_future_ink_and_keeps_existing_stroke_width() {
        let mut canvas = Canvas::default();
        canvas.start(POINT { x: -40, y: 10 });
        canvas.draw_to(POINT { x: -20, y: 10 });
        canvas.resize(POINT { x: -20, y: 10 }, 60);
        assert_eq!(canvas.width(), 6);
        canvas.resize(POINT { x: -20, y: 10 }, 60);
        canvas.draw_to(POINT { x: 10, y: 12 });
        assert_eq!(canvas.width(), 7);
        assert_eq!(canvas.strokes[0].width, 6);
        assert_eq!(canvas.strokes[1].width, 7);
        canvas.erase();
        assert!(canvas.bounds().is_none());
        assert_eq!(canvas.width(), 7);
    }

    #[test]
    fn presets_apply_to_new_ink_and_wheel_adjusts_by_one_pixel() {
        let mut canvas = Canvas::default();
        canvas.preset(9);
        assert_eq!(canvas.width(), INITIAL_WIDTH);
        let start = POINT { x: 10, y: 10 };
        let next = POINT { x: 20, y: 10 };
        canvas.start(start);
        canvas.resize(start, 60);
        canvas.preset(9);
        assert_eq!(canvas.width(), MAX_WIDTH);
        canvas.draw_to(next);
        assert_eq!(canvas.strokes[0].width, INITIAL_WIDTH);
        assert_eq!(canvas.strokes[1].width, MAX_WIDTH);
        canvas.resize(next, -60);
        assert_eq!(canvas.width(), MAX_WIDTH);
        canvas.resize(next, -60);
        assert_eq!(canvas.width(), MAX_WIDTH - 1);
        canvas.preset(1);
        canvas.resize(next, -120);
        assert_eq!(canvas.width(), MIN_WIDTH);
        canvas.preset(0);
        assert_eq!(canvas.width(), MIN_WIDTH);
    }
}
