use crate::locale::text as tr;
use crate::{
    capture::{self, MAX_POINTS, Point, Stroke, Tool},
    config::CaptureShape,
    windows::{capture_selector, clipio, desktop},
};
use eframe::egui::{self, Color32, Pos2, Rect, TextureHandle, Vec2};
use image::RgbaImage;
use raw_window_handle::{HasWindowHandle, RawWindowHandle};
use std::{
    io,
    os::windows::ffi::OsStringExt,
    path::{Path, PathBuf},
    sync::Arc,
};
use windows_sys::Win32::{
    Foundation::HWND,
    Graphics::Dwm::DwmFlush,
    UI::{
        Controls::Dialogs::{
            CommDlgExtendedError, GetSaveFileNameW, OFN_EXPLORER, OFN_NOCHANGEDIR,
            OFN_OVERWRITEPROMPT, OFN_PATHMUSTEXIST, OPENFILENAMEW,
        },
        WindowsAndMessaging::{SW_HIDE, SW_SHOW, ShowWindow},
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
        clipio::publish_screenshot(&image, std::ptr::null_mut())?;
        if crate::config::load(&profile)?.capture_editor_after_copy {
            return run_editor(&profile, false);
        }
    }
    Ok(())
}

pub fn run_editor(profile: &Path, for_save: bool) -> AppResult {
    let appearance = crate::locale::load(profile);
    let image = capture::load_last(profile)?;
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
        &format!("Typlo2.0 — {}", tr("paint.title", "Screenshot editor")),
        options,
        Box::new(move |creation| {
            crate::windows::mark_screenshot_editor(hwnd(creation) as usize);
            crate::appearance::configure(&creation.egui_ctx);
            crate::locale::theme(&creation.egui_ctx, appearance.ui_theme);
            crate::locale::fonts(&creation.egui_ctx, &appearance.ui_language);
            Ok(Box::new(Editor {
                texture: texture(&creation.egui_ctx, &image, "latest-screenshot"),
                profile,
                for_save,
                image,
                history: vec![Vec::new()],
                index: 0,
                draft: None,
                tool: Tool::Pen,
                width: 5.,
                color: [239, 68, 68],
                status: if for_save {
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

struct Editor {
    profile: PathBuf,
    for_save: bool,
    image: RgbaImage,
    texture: TextureHandle,
    history: Vec<Vec<Arc<Stroke>>>,
    index: usize,
    draft: Option<Stroke>,
    tool: Tool,
    width: f32,
    color: [u8; 3],
    status: String,
}

impl Editor {
    fn commit(&mut self, strokes: Vec<Arc<Stroke>>) {
        self.history.truncate(self.index + 1);
        self.history.push(strokes);
        if self.history.len() > 101 {
            self.history.remove(0);
        }
        self.index = self.history.len() - 1;
        self.status = tr("paint.changed", "Changes ready · Copy or save to export.").into();
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
        let context = ui.ctx().clone();
        let ctrl = |key| context.input_mut(|input| input.consume_key(Modifiers::CTRL, key));
        if ui.input(|input| input.key_pressed(Key::Escape)) {
            if self.draft.take().is_none() {
                ui.ctx().send_viewport_cmd(egui::ViewportCommand::Close);
            }
            return;
        }
        let mut copy_clicked = false;
        let mut save_clicked = false;
        let mut undo = false;
        let mut redo = false;
        let mut clear = false;
        let mut retake = None;
        egui::Frame::new()
            .inner_margin(egui::Margin::symmetric(22, 12))
            .show(ui, |ui| {
                ui.horizontal(|ui| {
                    ui.vertical(|ui| {
                        ui.label(
                            egui::RichText::new(tr("paint.title", "Screenshot editor"))
                                .size(24.)
                                .strong(),
                        );
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
                    });
                });
                ui.add_space(8.);
                egui::Frame::new()
                    .corner_radius(10)
                    .fill(Color32::from_gray(128).gamma_multiply(0.10))
                    .inner_margin(12_f32)
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
                                let button = egui::Button::new("")
                                    .fill(color)
                                    .stroke(outline)
                                    .min_size(egui::vec2(28., 28.));
                                if ui.add(button).on_hover_text(name).clicked() {
                                    self.color = [color.r(), color.g(), color.b()];
                                }
                            }
                        });
                        ui.horizontal_wrapped(|ui| {
                            ui.label(tr("paint.width", "Width"));
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
                    .inner_margin(egui::Margin::symmetric(22, 10))
                    .show(ui, |ui| {
                        ui.label(egui::RichText::new(&self.status).size(12.));
                        ui.horizontal(|ui| {
                            for (shape, label) in [
                                (
                                    CaptureShape::Rectangle,
                                    tr("capture.rectangle", "Rectangle"),
                                ),
                                (CaptureShape::Free, tr("capture.freehand", "Freehand")),
                            ] {
                                if ui
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
            let shift_redo =
                ui.input_mut(|input| input.consume_key(Modifiers::CTRL | Modifiers::SHIFT, Key::Z));
            if (redo || shift_redo || ctrl(Key::Y)) && self.index + 1 < self.history.len() {
                self.index += 1;
            } else if (undo || ctrl(Key::Z)) && self.index > 0 {
                self.index -= 1;
            }
            if clear {
                self.commit(Vec::new());
            }
            if copy_clicked || ctrl(Key::C) {
                self.status = match clipio::publish_screenshot(&self.rendered(), hwnd(frame)) {
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
