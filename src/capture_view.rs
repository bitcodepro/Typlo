use crate::locale::text as tr;
use crate::{
    capture::{self, MAX_POINTS, Point, Stroke, Tool},
    config::CaptureShape,
    windows::{capture_selector, clipio, desktop, history::History},
};
use eframe::egui::{self, Color32, Pos2, Rect, TextureHandle, Vec2};
use image::RgbaImage;
use raw_window_handle::{HasWindowHandle, RawWindowHandle};
use std::{
    collections::HashMap,
    io,
    os::windows::ffi::OsStringExt,
    path::{Path, PathBuf},
    sync::Arc,
};
use windows_sys::Win32::{
    Foundation::HWND,
    Graphics::Dwm::DwmFlush,
    System::Threading::{AttachThreadInput, GetCurrentThreadId},
    UI::{
        Controls::Dialogs::{
            CommDlgExtendedError, GetSaveFileNameW, OFN_EXPLORER, OFN_NOCHANGEDIR,
            OFN_OVERWRITEPROMPT, OFN_PATHMUSTEXIST, OPENFILENAMEW,
        },
        WindowsAndMessaging::{
            GetForegroundWindow, GetWindowThreadProcessId, MB_ICONINFORMATION, MessageBoxW,
            SW_HIDE, SW_SHOW, SetForegroundWindow, ShowWindow,
        },
    },
};

type AppResult = Result<(), Box<dyn std::error::Error>>;

pub fn run_capture(profile: PathBuf, destination: &str, shape: CaptureShape) -> AppResult {
    let to_file = match destination {
        "file" => true,
        "clipboard" => false,
        _ => {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "Unknown screenshot destination",
            )
            .into());
        }
    };
    let Some(selection) = capture_selector::select(shape)? else {
        return Ok(());
    };
    // Wait for the compositor to remove the outline before reading the screen.
    unsafe { DwmFlush() };
    let desktop = desktop::capture(selection.bounds())?;
    let image = if selection.shape == CaptureShape::Free {
        let points: Vec<_> = selection
            .points
            .iter()
            .map(|point| Point {
                x: point.x.saturating_sub(desktop.x) as f32,
                y: point.y.saturating_sub(desktop.y) as f32,
            })
            .collect();
        capture::crop(&desktop.image, &points, true)?
    } else {
        desktop.image
    };
    capture::remember(&image, &profile)?;
    if to_file {
        if let Some(path) = save_path(std::ptr::null_mut())? {
            capture::write_jpeg(&image, &path)?;
        }
    } else {
        // Copying only fills the clipboard. The editor has its own shortcut.
        clipio::publish_screenshot(&image, std::ptr::null_mut())?;
    }
    Ok(())
}

pub fn run_editor(profile: &Path, for_save: bool) -> AppResult {
    let image = capture::load_last(profile)?;
    run_image_editor(profile, for_save, image, String::new(), None)
}

pub fn run_clipboard_images(profile: &Path, origin: usize, selected: Option<i64>) -> AppResult {
    let store = History::open(profile)?;
    let ids = store.image_ids()?;
    let initial = if let Some(id) = selected {
        let position = ids.iter().position(|&entry| entry == id);
        position.and_then(|index| {
            load_history_image(&store, id)
                .ok()
                .map(|image| (index, image))
        })
    } else {
        ids.iter().enumerate().find_map(|(index, &id)| {
            load_history_image(&store, id)
                .ok()
                .map(|image| (index, image))
        })
    };
    let Some((position, (image, original))) = initial else {
        crate::locale::load(profile);
        let message = tr(
            "rust.clipboardImages.empty",
            "No viewable image found in clipboard history.",
        );
        let message: Vec<_> = message.encode_utf16().chain([0]).collect();
        let title: Vec<_> = "Typlo".encode_utf16().chain([0]).collect();
        unsafe {
            MessageBoxW(
                std::ptr::null_mut(),
                message.as_ptr(),
                title.as_ptr(),
                MB_ICONINFORMATION,
            );
        }
        return Ok(());
    };
    let caption = original.editable_text();
    let gallery = Gallery {
        store,
        ids,
        position,
        original,
        annotations: HashMap::new(),
        origin: (origin != 0).then_some(origin),
    };
    run_image_editor(profile, false, image, caption, Some(gallery))
}

fn load_history_image(store: &History, id: i64) -> io::Result<(RgbaImage, clipio::Clip)> {
    let clip = store.load(id)?.ok_or(io::ErrorKind::NotFound)?;
    Ok((clip.image()?.into_rgba8(), clip))
}

fn run_image_editor(
    profile: &Path,
    for_save: bool,
    image: RgbaImage,
    caption: String,
    gallery: Option<Gallery>,
) -> AppResult {
    let appearance = crate::locale::load(profile);
    let browsing_history = gallery.is_some();
    let title = if browsing_history {
        tr("rust.clipboardImages.title", "Clipboard images")
    } else {
        tr("paint.title", "Screenshot editor")
    };
    let profile = profile.to_owned();
    let options = eframe::NativeOptions {
        viewport: crate::appearance::window()
            .with_inner_size([1100., 760.])
            .with_min_inner_size([760., 520.]),
        centered: true,
        renderer: eframe::Renderer::Glow,
        ..Default::default()
    };
    eframe::run_native(
        &format!("Typlo — {title}"),
        options,
        Box::new(move |creation| {
            if browsing_history {
                crate::windows::mark_clipboard_images(hwnd(creation) as usize);
            } else {
                crate::windows::mark_screenshot_editor(hwnd(creation) as usize);
            }
            crate::appearance::configure(&creation.egui_ctx);
            crate::locale::theme(&creation.egui_ctx, appearance.ui_theme);
            crate::locale::fonts(&creation.egui_ctx, &appearance.ui_language);
            Ok(Box::new(Editor {
                texture: texture(&creation.egui_ctx, &image, "latest-screenshot"),
                profile,
                for_save,
                gallery,
                title,
                image,
                caption,
                history: vec![Vec::new()],
                index: 0,
                draft: None,
                tool: Tool::Pen,
                width: 5.,
                color: [239, 68, 68],
                status: if browsing_history {
                    ""
                } else if for_save {
                    tr(
                        "paint.ready",
                        "Draw on the image, then save as JPEG or copy.",
                    )
                } else {
                    tr(
                        "paint.clipboard",
                        "Original copied. Copy again after drawing to update the clipboard.",
                    )
                }
                .into(),
            }))
        }),
    )?;
    Ok(())
}

type UndoHistory = Vec<Vec<Arc<Stroke>>>;

struct Gallery {
    store: History,
    ids: Vec<i64>,
    position: usize,
    original: clipio::Clip,
    annotations: HashMap<i64, (UndoHistory, usize)>,
    origin: Option<usize>,
}

struct Editor {
    profile: PathBuf,
    for_save: bool,
    gallery: Option<Gallery>,
    title: &'static str,
    image: RgbaImage,
    caption: String,
    texture: TextureHandle,
    history: UndoHistory,
    index: usize,
    draft: Option<Stroke>,
    tool: Tool,
    width: f32,
    color: [u8; 3],
    status: String,
}

impl Editor {
    fn navigate(&mut self, context: &egui::Context, direction: isize) {
        let Some(gallery) = &mut self.gallery else {
            return;
        };
        let mut position = gallery.position;
        while let Some(next) = position.checked_add_signed(direction) {
            let Some(&id) = gallery.ids.get(next) else {
                break;
            };
            position = next;
            let Ok((image, original)) = load_history_image(&gallery.store, id) else {
                continue;
            };
            let outgoing = gallery.ids[gallery.position];
            let (history, index) = gallery
                .annotations
                .remove(&id)
                .unwrap_or_else(|| (vec![vec![]], 0));
            if self.history.len() > 1 {
                gallery.annotations.insert(
                    outgoing,
                    (std::mem::replace(&mut self.history, history), self.index),
                );
            } else {
                self.history = history;
            }
            self.index = index;
            gallery.position = position;
            self.texture = texture(context, &image, "clipboard-image");
            self.image = image;
            self.caption = original.editable_text();
            gallery.original = original;
            self.status.clear();
            return;
        }
    }

    fn commit(&mut self, strokes: Vec<Arc<Stroke>>) {
        self.history.truncate(self.index + 1);
        self.history.push(strokes);
        if self.history.len() > 101 {
            self.history.remove(0);
        }
        self.index = self.history.len() - 1;
        self.status = tr("paint.changed", "Changes ready · Copy or save to export.").into();
    }

    fn original_for_copy(&self) -> Option<&clipio::Clip> {
        self.gallery
            .as_ref()
            .filter(|_| self.history[self.index].is_empty())
            .map(|gallery| &gallery.original)
    }

    fn copy(&self, window: HWND) -> io::Result<()> {
        if let Some(clip) = self.original_for_copy() {
            clipio::publish(window, clip, false)?;
            return Ok(());
        }
        clipio::publish_screenshot(&self.rendered(), window)
    }

    fn rendered(&self) -> RgbaImage {
        let mut output = self.image.clone();
        for stroke in &self.history[self.index] {
            stroke.draw(&mut output);
        }
        output
    }

    fn retake(&self, window: HWND, shape: CaptureShape) -> io::Result<()> {
        let executable = std::env::current_exe()?;
        unsafe {
            ShowWindow(window, SW_HIDE);
        }
        let result = std::process::Command::new(executable)
            .arg("--capture")
            .arg(&self.profile)
            .arg(if self.for_save { "file" } else { "clipboard" })
            .arg(shape.arg())
            .spawn();
        if result.is_err() {
            unsafe {
                ShowWindow(window, SW_SHOW);
            }
        }
        result.map(|_| ())
    }
}

impl eframe::App for Editor {
    fn clear_color(&self, visuals: &egui::Visuals) -> [f32; 4] {
        visuals.panel_fill.to_normalized_gamma_f32()
    }

    fn ui(&mut self, ui: &mut egui::Ui, frame: &mut eframe::Frame) {
        use egui::{Key, Modifiers};
        if let Some(origin) = self
            .gallery
            .as_mut()
            .and_then(|gallery| gallery.origin.take())
        {
            focus_gallery(hwnd(frame), origin as HWND);
        }
        let context = ui.ctx().clone();
        let key = |modifiers, key| {
            context
                .input_mut(|input| crate::appearance::consume_shortcut_key(input, modifiers, key))
        };
        let ctrl = |button| key(Modifiers::CTRL, button);
        if ui.input(|input| input.key_pressed(Key::Escape)) {
            if self.draft.take().is_none() {
                ui.ctx().send_viewport_cmd(egui::ViewportCommand::Close);
            }
            return;
        }
        let mut navigate = None;
        if self.gallery.is_some() && self.draft.is_none() {
            if key(Modifiers::NONE, Key::ArrowLeft) {
                navigate = Some(-1);
            } else if key(Modifiers::NONE, Key::ArrowRight) {
                navigate = Some(1);
            }
        }
        let mut copy_clicked = false;
        let mut save_clicked = false;
        let mut undo = false;
        let mut redo = false;
        let mut clear = false;
        let mut retake = None;
        let browsing_history = self.gallery.is_some();
        let top_margin = if browsing_history { 6 } else { 12 };
        egui::Frame::new()
            .inner_margin(egui::Margin::symmetric(22, top_margin))
            .show(ui, |ui| {
                ui.horizontal(|ui| {
                    ui.vertical(|ui| {
                        let title_size = if browsing_history { 18. } else { 24. };
                        ui.label(egui::RichText::new(self.title).size(title_size).strong());
                        ui.label(
                            egui::RichText::new(format!(
                                "{} × {} px · PNG / JPEG {}%",
                                self.image.width(),
                                self.image.height(),
                                capture::JPEG_QUALITY,
                            ))
                            .size(12.),
                        );
                    });
                    ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                        save_clicked = ui
                            .button(tr("capture.save", "Save JPEG…"))
                            .on_hover_text("Ctrl+S")
                            .clicked();
                        copy_clicked = ui
                            .button(tr("capture.copy", "Copy"))
                            .on_hover_text("Ctrl+C")
                            .clicked();
                        if let Some(gallery) = &self.gallery {
                            ui.separator();
                            if ui
                                .add_enabled(
                                    gallery.position + 1 < gallery.ids.len(),
                                    egui::Button::new("→"),
                                )
                                .on_hover_text(tr("rust.clipboardImages.next", "Next image"))
                                .clicked()
                            {
                                navigate = Some(1);
                            }
                            ui.label(format!("{} / {}", gallery.position + 1, gallery.ids.len()));
                            if ui
                                .add_enabled(gallery.position > 0, egui::Button::new("←"))
                                .on_hover_text(tr(
                                    "rust.clipboardImages.previous",
                                    "Previous image",
                                ))
                                .clicked()
                            {
                                navigate = Some(-1);
                            }
                        }
                    });
                });
                if !self.caption.is_empty() {
                    ui.add(
                        egui::Label::new(self.caption.lines().next().unwrap_or_default())
                            .truncate(),
                    )
                    .on_hover_text(&self.caption);
                }
                ui.add_space(if browsing_history { 4. } else { 8. });
                egui::Frame::new()
                    .corner_radius(10)
                    .fill(Color32::from_gray(128).gamma_multiply(0.10))
                    .inner_margin(if browsing_history { 6_f32 } else { 12_f32 })
                    .show(ui, |ui| {
                        ui.horizontal_wrapped(|ui| {
                            for (tool, symbol, name) in [
                                (Tool::Pen, "✎", tr("paint.pen", "Pen")),
                                (Tool::Marker, "▰", tr("paint.marker", "Marker")),
                                (Tool::Arrow, "↗", tr("paint.arrow", "Arrow")),
                                (Tool::Rectangle, "□", tr("paint.rectangle", "Frame")),
                            ] {
                                ui.selectable_value(
                                    &mut self.tool,
                                    tool,
                                    format!("{symbol}  {name}"),
                                );
                            }
                            ui.separator();
                            for (rgb, name) in [
                                (0xef4444, tr("paint.red", "Red")),
                                (0xf97316, tr("paint.orange", "Orange")),
                                (0xfacc15, tr("paint.yellow", "Yellow")),
                                (0x22c55e, tr("paint.green", "Green")),
                                (0x38bdf8, tr("paint.blue", "Blue")),
                                (0xa78bfa, tr("paint.purple", "Purple")),
                                (0xffffff, tr("paint.white", "White")),
                                (0x151922, tr("paint.black", "Black")),
                            ] {
                                let color = crate::appearance::rgb(rgb);
                                let selected = self.color == [color.r(), color.g(), color.b()];
                                let outline = egui::Stroke::new(
                                    if selected { 3_f32 } else { 1_f32 },
                                    if selected {
                                        crate::appearance::rgb(0x3b82f6)
                                    } else {
                                        Color32::GRAY
                                    },
                                );
                                let size = if browsing_history { 20. } else { 28. };
                                let button = egui::Button::new("")
                                    .fill(color)
                                    .stroke(outline)
                                    .min_size(egui::Vec2::splat(size));
                                if ui.add(button).on_hover_text(name).clicked() {
                                    self.color = [color.r(), color.g(), color.b()];
                                }
                            }
                            if browsing_history {
                                ui.separator();
                            } else {
                                ui.end_row();
                            }
                            ui.label(tr("paint.width", "Width"));
                            if browsing_history {
                                ui.spacing_mut().slider_width = 65.;
                            }
                            ui.add(
                                egui::Slider::new(&mut self.width, 1.0..=32.0)
                                    .step_by(1.)
                                    .suffix(" px"),
                            );
                            undo = ui
                                .add_enabled(self.index > 0, egui::Button::new("↶"))
                                .on_hover_text(tr("paint.undo", "Undo"))
                                .clicked();
                            redo = ui
                                .add_enabled(
                                    self.index + 1 < self.history.len(),
                                    egui::Button::new("↷"),
                                )
                                .on_hover_text(tr("paint.redo", "Redo"))
                                .clicked();
                            clear = ui
                                .add_enabled(
                                    !self.history[self.index].is_empty(),
                                    egui::Button::new(tr("paint.clear", "Clear drawings")),
                                )
                                .clicked();
                        });
                    });
            });
        egui::Panel::bottom("paint-footer")
            .show_separator_line(false)
            .show_inside(ui, |ui| {
                egui::Frame::new()
                    .inner_margin(egui::Margin::symmetric(
                        22,
                        if browsing_history { 4 } else { 10 },
                    ))
                    .show(ui, |ui| {
                        if !browsing_history {
                            ui.label(egui::RichText::new(&self.status).size(12.));
                        }
                        ui.horizontal(|ui| {
                            if browsing_history && !self.status.is_empty() {
                                ui.label(egui::RichText::new(&self.status).size(12.));
                            }
                            for (shape, label) in [
                                (
                                    CaptureShape::Rectangle,
                                    tr("capture.rectangle", "Rectangle"),
                                ),
                                (CaptureShape::Free, tr("capture.freehand", "Freehand")),
                            ] {
                                if self.gallery.is_none()
                                    && ui
                                        .button(label)
                                        .on_hover_text(tr("paint.retake", "New capture"))
                                        .clicked()
                                {
                                    retake = Some(shape);
                                }
                            }
                            ui.with_layout(
                                egui::Layout::right_to_left(egui::Align::Center),
                                |ui| {
                                    ui.label(
                                        egui::RichText::new(tr(
                                            "paint.keys",
                                            "Ctrl+Z undo · Ctrl+Y redo · Esc close",
                                        ))
                                        .size(11.),
                                    );
                                },
                            );
                        });
                    });
            });
        if self.draft.is_none() {
            let shift_redo = key(Modifiers::CTRL | Modifiers::SHIFT, Key::Z);
            if (redo || shift_redo || ctrl(Key::Y)) && self.index + 1 < self.history.len() {
                self.index += 1;
            } else if (undo || ctrl(Key::Z)) && self.index > 0 {
                self.index -= 1;
            }
            if clear {
                self.commit(Vec::new());
            }
            if copy_clicked || ctrl(Key::C) {
                self.status = match self.copy(hwnd(frame)) {
                    Ok(()) => tr("capture.copied", "Screenshot copied").into(),
                    Err(error) => error.to_string(),
                };
            }
            if save_clicked || ctrl(Key::S) {
                match save_path(hwnd(frame)).and_then(|path| {
                    path.map(|path| capture::write_jpeg(&self.rendered(), &path))
                        .transpose()
                }) {
                    Ok(Some(())) => self.status = tr("capture.saved", "JPEG saved").into(),
                    Ok(None) => {}
                    Err(error) => self.status = error.to_string(),
                }
            }
            if let Some(shape) = retake {
                match self.retake(hwnd(frame), shape) {
                    Ok(()) => ui.ctx().send_viewport_cmd(egui::ViewportCommand::Close),
                    Err(error) => self.status = error.to_string(),
                }
            }
        }
        if self.draft.is_none()
            && let Some(direction) = navigate
        {
            self.navigate(&context, direction);
            context.request_repaint();
        }
        let backdrop = ui.available_rect_before_wrap().shrink2(egui::vec2(22., 0.));
        ui.painter()
            .rect_filled(backdrop, 10, crate::appearance::rgb(0x181d27));
        let rect = fit(backdrop.shrink(20.), &self.image);
        let painter = ui.painter_at(rect);
        paint_image(&painter, &self.texture, rect);
        let response = ui.interact(
            rect,
            ui.id().with("annotations"),
            egui::Sense::click_and_drag(),
        );
        if response.hovered() {
            ui.ctx().set_cursor_icon(egui::CursorIcon::Crosshair);
        }
        if response.drag_started()
            && self.history[self.index].len() < 512
            && let Some(start) = ui.input(|input| input.pointer.press_origin())
        {
            self.draft = Some(Stroke {
                tool: self.tool,
                color: self.color,
                width: self.width,
                points: vec![to_pixel(start, rect, &self.image)],
            });
        }
        if (response.dragged() || response.drag_stopped())
            && let Some(stroke) = &mut self.draft
            && let Some(position) = ui.input(|input| input.pointer.latest_pos())
        {
            add_point(
                &mut stroke.points,
                to_pixel(position, rect, &self.image),
                matches!(stroke.tool, Tool::Arrow | Tool::Rectangle),
            );
        }
        if response.drag_stopped()
            && let Some(stroke) = self.draft.take()
        {
            let mut strokes = self.history[self.index].clone();
            strokes.push(Arc::new(stroke));
            self.commit(strokes);
        }
        for stroke in self.history[self.index]
            .iter()
            .map(Arc::as_ref)
            .chain(self.draft.iter())
        {
            let [r, g, b] = stroke.color;
            let style = egui::Stroke::new(
                stroke.width * rect.width() / self.image.width() as f32,
                Color32::from_rgba_unmultiplied(r, g, b, stroke.alpha()),
            );
            for path in stroke.paths() {
                painter.add(egui::Shape::line(
                    path.into_iter()
                        .map(|point| from_pixel(point, rect, &self.image))
                        .collect(),
                    style,
                ));
            }
        }
    }
}

fn focus_gallery(viewer: HWND, origin: HWND) {
    unsafe {
        if GetForegroundWindow() != origin || SetForegroundWindow(viewer) != 0 {
            return;
        }
        let thread = GetCurrentThreadId();
        let origin_thread = GetWindowThreadProcessId(origin, std::ptr::null_mut());
        if origin_thread == 0 || AttachThreadInput(thread, origin_thread, 1) == 0 {
            return;
        }
        // Avoid taking focus if the user switched apps while the viewer was opening.
        if GetForegroundWindow() == origin {
            SetForegroundWindow(viewer);
        }
        AttachThreadInput(thread, origin_thread, 0);
    }
}

fn add_point(points: &mut Vec<Point>, point: Point, two: bool) {
    if (two && points.len() == 2) || points.len() == MAX_POINTS {
        if let Some(last) = points.last_mut() {
            *last = point;
        }
    } else if points.last() != Some(&point) {
        points.push(point);
    }
}

fn texture(context: &egui::Context, image: &RgbaImage, label: &str) -> TextureHandle {
    context.load_texture(
        label,
        egui::ColorImage::from_rgba_unmultiplied(
            [image.width() as usize, image.height() as usize],
            image.as_raw(),
        ),
        egui::TextureOptions::LINEAR,
    )
}

fn fit(bounds: Rect, image: &RgbaImage) -> Rect {
    let scale =
        (bounds.width() / image.width() as f32).min(bounds.height() / image.height() as f32);
    Rect::from_center_size(
        bounds.center(),
        Vec2::new(image.width() as f32, image.height() as f32) * scale,
    )
}

fn paint_image(painter: &egui::Painter, texture: &TextureHandle, rect: Rect) {
    painter.image(
        texture.id(),
        rect,
        Rect::from_min_max(Pos2::ZERO, Pos2::new(1., 1.)),
        Color32::WHITE,
    );
}

fn to_pixel(position: Pos2, rect: Rect, image: &RgbaImage) -> Point {
    Point {
        x: ((position.x - rect.left()) / rect.width()).clamp(0., 1.) * image.width() as f32,
        y: ((position.y - rect.top()) / rect.height()).clamp(0., 1.) * image.height() as f32,
    }
}

fn from_pixel(point: Point, rect: Rect, image: &RgbaImage) -> Pos2 {
    Pos2::new(
        rect.left() + point.x * rect.width() / image.width() as f32,
        rect.top() + point.y * rect.height() / image.height() as f32,
    )
}

fn hwnd(frame: &impl HasWindowHandle) -> HWND {
    match frame.window_handle().ok().map(|handle| handle.as_raw()) {
        Some(RawWindowHandle::Win32(handle)) => handle.hwnd.get() as HWND,
        _ => std::ptr::null_mut(),
    }
}

fn save_path(owner: HWND) -> io::Result<Option<PathBuf>> {
    let filter: Vec<_> = "JPEG\0*.jpg;*.jpeg\0\0".encode_utf16().collect();
    let extension: Vec<_> = "jpg\0".encode_utf16().collect();
    let mut path = vec![0u16; 32768];
    for (slot, unit) in path.iter_mut().zip("Typlo.jpg".encode_utf16()) {
        *slot = unit;
    }
    let mut dialog = OPENFILENAMEW {
        lStructSize: size_of::<OPENFILENAMEW>() as u32,
        hwndOwner: owner,
        lpstrFilter: filter.as_ptr(),
        nFilterIndex: 1,
        lpstrFile: path.as_mut_ptr(),
        nMaxFile: path.len() as u32,
        lpstrDefExt: extension.as_ptr(),
        Flags: OFN_EXPLORER | OFN_NOCHANGEDIR | OFN_OVERWRITEPROMPT | OFN_PATHMUSTEXIST,
        ..Default::default()
    };
    if unsafe { GetSaveFileNameW(&mut dialog) } == 0 {
        let error = unsafe { CommDlgExtendedError() };
        return if error == 0 {
            Ok(None)
        } else {
            Err(io::Error::other(format!("Save dialog failed ({error:#x})")))
        };
    }
    let end = path
        .iter()
        .position(|&unit| unit == 0)
        .unwrap_or(path.len());
    Ok(Some(std::ffi::OsString::from_wide(&path[..end]).into()))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::windows::history::Limits;

    #[test]
    fn copying_open_image_keeps_original_formats_and_caption_after_history_removal() {
        let make_clip = |color, caption: &str| {
            let image = RgbaImage::from_pixel(2, 2, image::Rgba(color));
            let mut png = io::Cursor::new(Vec::new());
            image.write_to(&mut png, image::ImageFormat::Png).unwrap();
            let caption: Vec<u8> = caption
                .encode_utf16()
                .chain([0])
                .flat_map(u16::to_le_bytes)
                .collect();
            serde_json::from_value::<clipio::Clip>(serde_json::json!({
                "kind": "Image",
                "formats": [["Png", png.into_inner()], ["Text", caption]]
            }))
            .unwrap()
        };
        let first = make_clip([255, 0, 0, 255], "Red");
        let second = make_clip([0, 255, 0, 255], "Green\n");
        let third = make_clip([0, 0, 255, 255], "Blue");
        let profile = tempfile::tempdir().unwrap();
        let mut writer = History::open(profile.path()).unwrap();
        let mut limits = Limits::from_config(&crate::config::Config::default());
        writer.store(&first, limits).unwrap();
        writer.store(&second, limits).unwrap();

        let store = History::open(profile.path()).unwrap();
        let ids = store.image_ids().unwrap();
        assert_eq!(ids.len(), 2);
        let (current, next) = (ids[0], ids[1]);
        let (image, original) = load_history_image(&store, current).unwrap();
        let context = egui::Context::default();
        let mut editor = Editor {
            profile: profile.path().to_owned(),
            for_save: false,
            gallery: Some(Gallery {
                store,
                ids,
                position: 0,
                original,
                annotations: HashMap::new(),
                origin: None,
            }),
            title: "",
            texture: texture(&context, &image, "test"),
            image,
            caption: "Green\n".into(),
            history: vec![vec![]],
            index: 0,
            draft: None,
            tool: Tool::Pen,
            width: 5.,
            color: [255, 0, 0],
            status: String::new(),
        };

        writer.remove(current).unwrap();
        assert!(writer.load(current).unwrap().is_none());
        let original = editor.original_for_copy().unwrap();
        assert_eq!(original.editable_text(), "Green\n");
        assert_eq!(
            serde_json::to_value(original).unwrap(),
            serde_json::to_value(second).unwrap()
        );

        editor.navigate(&context, 1);
        limits.max_items = 1;
        writer.store(&third, limits).unwrap();
        assert!(writer.load(next).unwrap().is_none());
        let original = editor.original_for_copy().unwrap();
        assert_eq!(original.editable_text(), "Red");
        assert_eq!(
            serde_json::to_value(original).unwrap(),
            serde_json::to_value(first).unwrap()
        );
    }
}
