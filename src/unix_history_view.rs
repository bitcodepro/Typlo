use crate::locale::text as tr;
use crate::unix_history::{Clip, Entry, History};
#[cfg(target_os = "macos")]
use arboard::SetExtApple;
#[cfg(target_os = "linux")]
use arboard::SetExtLinux;
use eframe::egui;
use std::{
    collections::{HashMap, VecDeque},
    io::{self, Cursor, Read},
    path::PathBuf,
    sync::mpsc,
};

pub fn run(profile: PathBuf, origin: Option<[u32; 2]>) -> Result<(), Box<dyn std::error::Error>> {
    let store = History::open(&profile)?;
    let appearance = crate::locale::load(&profile);
    let move_up = appearance.history_move_to_top;
    let clipboard = arboard::Clipboard::new()?;
    eframe::run_native(
        tr("history.title", "История буфера Typlo"),
        eframe::NativeOptions {
            renderer: eframe::Renderer::Glow,
            viewport: crate::appearance::window()
                .with_inner_size([460., 560.])
                .with_resizable(false)
                .with_decorations(false)
                .with_taskbar(false)
                .with_transparent(true)
                .with_always_on_top(),
            ..Default::default()
        },
        Box::new(move |creation| {
            crate::appearance::configure(&creation.egui_ctx);
            crate::locale::theme(&creation.egui_ctx, appearance.ui_theme);
            crate::locale::fonts(&creation.egui_ctx, &appearance.ui_language);
            let (signal, commands) = mpsc::channel();
            let gui = creation.egui_ctx.clone();
            std::thread::spawn(move || {
                let mut stdin = io::stdin().lock();
                let mut message = [0; 8];
                while stdin.read_exact(&mut message).is_ok() {
                    let window = u32::from_le_bytes(message[..4].try_into().unwrap());
                    let pid = u32::from_le_bytes(message[4..].try_into().unwrap());
                    if signal
                        .send((window != 0 && pid != 0).then_some([window, pid]))
                        .is_err()
                    {
                        break;
                    }
                    gui.request_repaint();
                }
            });
            Ok(Box::new(View {
                profile,
                store,
                clipboard,
                commands,
                origin,
                move_up,
                entries: Vec::new(),
                viewer: None,
                selected: 0,
                focus_search: true,
                search: None,
                thumbnails: HashMap::new(),
                thumbnail_order: VecDeque::new(),
                thumbnail_job: None,
                #[cfg(target_os = "linux")]
                ocr: None,
                #[cfg(target_os = "linux")]
                ocr_available: linux_ocr::available(),
                query: String::new(),
                page: 0,
                refresh: true,
                status: String::new(),
            }))
        }),
    )?;
    Ok(())
}

struct View {
    profile: PathBuf,
    store: History,
    clipboard: arboard::Clipboard,
    commands: mpsc::Receiver<Option<[u32; 2]>>,
    origin: Option<[u32; 2]>,
    move_up: bool,
    entries: Vec<Entry>,
    viewer: Option<Viewer>,
    selected: usize,
    focus_search: bool,
    search: Option<mpsc::Receiver<io::Result<Vec<Entry>>>>,
    thumbnails: HashMap<i64, Option<egui::TextureHandle>>,
    thumbnail_order: VecDeque<i64>,
    thumbnail_job: Option<(i64, mpsc::Receiver<Option<image::RgbaImage>>)>,
    #[cfg(target_os = "linux")]
    ocr: Option<linux_ocr::Job>,
    #[cfg(target_os = "linux")]
    ocr_available: bool,
    query: String,
    page: usize,
    refresh: bool,
    status: String,
}

struct Viewer {
    id: i64,
    text: String,
    image: Option<egui::TextureHandle>,
}

#[derive(Clone, Copy)]
enum Action {
    Copy(i64),
    Paste(i64, bool),
    View(i64),
    Pin(i64, bool),
    Remove(i64),
    #[cfg(target_os = "linux")]
    Ocr(i64),
}

impl View {
    fn write_clipboard(&mut self, id: i64, plain: bool) -> io::Result<()> {
        let value = self
            .store
            .load(id)?
            .ok_or_else(|| io::Error::other("Запись удалена"))?;
        let target = self.clipboard.set().exclude_from_history();
        match value {
            Clip::Text {
                text,
                html: Some(html),
            } if !plain => target.html(html, Some(text)),
            Clip::Text { text, .. } => target.text(text),
            Clip::Files(paths) => target.file_list(&paths),
            Clip::Image(bytes) => {
                let image = image(&bytes)?;
                let (width, height) = image.dimensions();
                target.image(arboard::ImageData {
                    width: width as usize,
                    height: height as usize,
                    bytes: image.into_raw().into(),
                })
            }
        }
        .map_err(io::Error::other)
    }

    fn paste(&mut self, id: i64, plain: bool, ui: &egui::Ui) -> io::Result<bool> {
        self.write_clipboard(id, plain)?;
        #[cfg(target_os = "macos")]
        if let Some([_, pid]) = self.origin.filter(|[_, pid]| *pid != std::process::id()) {
            crate::macos::paste_history(pid)?;
            self.origin = None;
            self.store.touch(id, self.move_up)?;
            ui.ctx().send_viewport_cmd(egui::ViewportCommand::Close);
            return Ok(true);
        }
        #[cfg(target_os = "linux")]
        if let Some([window, pid]) = self.origin {
            crate::linux::paste_history(window, pid)?;
            self.origin = None;
            // An X11 clipboard owner must remain alive until the target reads the data.
            ui.ctx()
                .send_viewport_cmd(egui::ViewportCommand::Visible(false));
            self.store.touch(id, self.move_up)?;
            return Ok(true);
        }
        let _ = ui;
        self.store.touch(id, self.move_up)?;
        Ok(false)
    }

    fn refresh(&mut self, ctx: &egui::Context) {
        self.refresh = false;
        self.entries.clear();
        let open = self.store.background_reader(&self.profile);
        let query = self.query.clone();
        let offset = self.page * 50;
        let (send, receive) = mpsc::sync_channel(1);
        let ctx = ctx.clone();
        std::thread::spawn(move || {
            let _ = send.send(open().and_then(|store| store.list(&query, offset)));
            ctx.request_repaint();
        });
        self.search = Some(receive);
    }

    fn poll(&mut self, ctx: &egui::Context) {
        if let Some(search) = &self.search
            && let Ok(result) = search.try_recv()
        {
            self.search = None;
            match result {
                Ok(entries) => {
                    self.entries = entries;
                    self.selected = self
                        .selected
                        .min(self.entries.len().min(50).saturating_sub(1));
                }
                Err(error) => self.status = error.to_string(),
            }
        }
        if let Some((id, receive)) = &self.thumbnail_job
            && let Ok(image) = receive.try_recv()
        {
            let id = *id;
            self.thumbnail_job = None;
            if self.thumbnail_order.len() >= 32
                && let Some(oldest) = self.thumbnail_order.pop_front()
            {
                self.thumbnails.remove(&oldest);
            }
            self.thumbnail_order.push_back(id);
            self.thumbnails
                .insert(id, image.as_ref().map(|image| texture(ctx, image)));
        }
        #[cfg(target_os = "linux")]
        if let Some(job) = &self.ocr
            && let Ok(result) = job.result.try_recv()
        {
            self.ocr = None;
            self.status = match result {
                Ok(text) if text.is_empty() => tr("history.ocrNone", "No text found").into(),
                Ok(text) => match self.clipboard.set().exclude_from_history().text(text) {
                    Ok(()) => tr("history.ocrDone", "Text copied").into(),
                    Err(error) => error.to_string(),
                },
                Err(error) => error.to_string(),
            };
        }
    }

    fn thumbnail(&mut self, id: i64, ctx: &egui::Context) {
        if self.thumbnails.contains_key(&id) || self.thumbnail_job.is_some() {
            return;
        }
        let open = self.store.background_reader(&self.profile);
        let (send, receive) = mpsc::sync_channel(1);
        let ctx = ctx.clone();
        std::thread::spawn(move || {
            let loaded = open().ok().and_then(|store| store.load(id).ok().flatten());
            let thumbnail = match loaded {
                Some(Clip::Image(bytes)) => image(&bytes).ok().map(|image| {
                    let (width, height) = image.dimensions();
                    image::DynamicImage::ImageRgba8(image)
                        .thumbnail(width.min(320), height.min(110))
                        .into_rgba8()
                }),
                _ => None,
            };
            let _ = send.send(thumbnail);
            ctx.request_repaint();
        });
        self.thumbnail_job = Some((id, receive));
    }

    fn action(&mut self, action: Action, ui: &egui::Ui) -> io::Result<()> {
        match action {
            Action::Copy(id) => {
                self.write_clipboard(id, false)?;
                self.store.touch(id, self.move_up)?;
                self.status = tr("rust.history.copied", "Copied to clipboard").into();
                self.refresh = true;
            }
            Action::Paste(id, plain) => {
                if !self.paste(id, plain, ui)? {
                    self.status = tr("rust.history.copied", "Copied to clipboard").into();
                }
                self.refresh = true;
            }
            Action::View(id) => {
                let value = self.store.load(id)?.ok_or_else(|| {
                    io::Error::other(tr("history.notFound", "This item is no longer in history"))
                })?;
                let (text, image) = match value {
                    Clip::Image(bytes) => {
                        let image = image(&bytes)?;
                        (String::new(), Some(texture(ui.ctx(), &image)))
                    }
                    Clip::Text { text, .. } => (text, None),
                    Clip::Files(files) => (
                        files
                            .iter()
                            .map(|path| path.to_string_lossy())
                            .collect::<Vec<_>>()
                            .join("\n"),
                        None,
                    ),
                };
                self.viewer = Some(Viewer { id, text, image });
            }
            Action::Pin(id, pinned) => {
                self.store.pin(id, pinned)?;
                self.refresh = true;
            }
            Action::Remove(id) => {
                self.store.remove(id)?;
                self.thumbnails.remove(&id);
                self.thumbnail_order.retain(|cached| *cached != id);
                self.refresh = true;
            }
            #[cfg(target_os = "linux")]
            Action::Ocr(id) if self.ocr.is_none() => {
                let open = self.store.background_reader(&self.profile);
                self.ocr = Some(linux_ocr::Job::start(
                    move || match open()?.load(id)? {
                        Some(Clip::Image(bytes)) => {
                            image_dimensions(&bytes)?;
                            Ok(bytes)
                        }
                        _ => Err(io::Error::other("Clipboard image is missing")),
                    },
                    ui.ctx().clone(),
                )?);
                self.status = tr("history.ocrBusy", "Reading text…").into();
            }
            #[cfg(target_os = "linux")]
            Action::Ocr(_) => {}
        }
        Ok(())
    }
}

fn image_dimensions(bytes: &[u8]) -> io::Result<(u32, u32)> {
    let (width, height) = image::ImageReader::new(Cursor::new(bytes))
        .with_guessed_format()?
        .into_dimensions()
        .map_err(io::Error::other)?;
    if u64::from(width) * u64::from(height) > crate::capture::MAX_PIXELS {
        return Err(io::Error::other("Изображение превышает допустимый размер"));
    }
    Ok((width, height))
}

fn image(bytes: &[u8]) -> io::Result<image::RgbaImage> {
    image_dimensions(bytes)?;
    Ok(image::ImageReader::new(Cursor::new(bytes))
        .with_guessed_format()?
        .decode()
        .map_err(io::Error::other)?
        .into_rgba8())
}

fn texture(ctx: &egui::Context, image: &image::RgbaImage) -> egui::TextureHandle {
    ctx.load_texture(
        "clipboard-image",
        egui::ColorImage::from_rgba_unmultiplied(
            [image.width() as usize, image.height() as usize],
            image.as_raw(),
        ),
        egui::TextureOptions::LINEAR,
    )
}

impl eframe::App for View {
    fn clear_color(&self, _: &egui::Visuals) -> [f32; 4] {
        [0.; 4]
    }
    fn ui(&mut self, ui: &mut egui::Ui, _frame: &mut eframe::Frame) {
        if let Ok(command) = self.commands.try_recv() {
            if command.is_some_and(|[_, pid]| pid != std::process::id()) {
                self.origin = command;
                self.focus_search = true;
                ui.ctx()
                    .send_viewport_cmd(egui::ViewportCommand::Visible(true));
                ui.ctx().send_viewport_cmd(egui::ViewportCommand::Focus);
                self.refresh = true;
            } else {
                match self
                    .store
                    .previous()
                    .and_then(|id| id.map(|id| self.paste(id, false, ui)).transpose())
                {
                    Ok(Some(true)) => self.status.clear(),
                    Ok(Some(false)) => {
                        self.status = tr(
                            "rust.history.previousCopied",
                            "Предыдущая запись скопирована",
                        )
                        .into()
                    }
                    Ok(None) => {
                        self.status = tr(
                            "rust.history.previousMissing",
                            "Предыдущая запись отсутствует",
                        )
                        .into()
                    }
                    Err(error) => self.status = error.to_string(),
                }
                self.refresh = true;
            }
        }
        if self.refresh {
            self.refresh(ui.ctx());
        }
        self.poll(ui.ctx());
        #[cfg(target_os = "linux")]
        let ocr_pending = self.ocr.is_some();
        #[cfg(not(target_os = "linux"))]
        let ocr_pending = false;
        if ui.input(|input| input.key_pressed(egui::Key::Escape)) {
            if self.viewer.is_some() {
                self.viewer = None;
                self.focus_search = true;
            } else if !ocr_pending {
                ui.ctx().send_viewport_cmd(egui::ViewportCommand::Close);
            }
        }

        let panel = ui.visuals().panel_fill;
        let border = ui.visuals().window_stroke;
        let muted = ui.visuals().weak_text_color();
        let mut operation = None;
        egui::Frame::new()
            .fill(panel)
            .stroke(border)
            .corner_radius(12)
            .inner_margin(10.)
            .show(ui, |ui| {
                ui.set_min_size(ui.available_size());
                if let Some(viewer) = &self.viewer {
                    egui::ScrollArea::both()
                        .max_height((ui.available_height() - 42.).max(32.))
                        .show(ui, |ui| {
                            if let Some(image) = &viewer.image {
                                ui.add(egui::Image::new(image).max_width(424.).max_height(440.));
                            } else {
                                let mut text = viewer.text.as_str();
                                ui.add(
                                    egui::TextEdit::multiline(&mut text)
                                        .desired_width(f32::INFINITY),
                                );
                            }
                        });
                    let id = viewer.id;
                    ui.horizontal(|ui| {
                        if ui.button(tr("history.ctx.paste", "Paste")).clicked() {
                            operation = Some(Action::Paste(id, false));
                        }
                        if ui.button(tr("rust.history.copy", "Copy")).clicked() {
                            operation = Some(Action::Copy(id));
                        }
                        if ui
                            .button(tr("rust.history.closePreview", "Close preview"))
                            .clicked()
                        {
                            self.viewer = None;
                            self.focus_search = true;
                        }
                    });
                    return;
                }
                let search = ui.add(
                    crate::appearance::text_input(&mut self.query)
                        .hint_text(tr("history.search", "Search clipboard"))
                        .desired_width(f32::INFINITY),
                );
                if std::mem::take(&mut self.focus_search) {
                    search.request_focus();
                }
                if search.changed() {
                    self.page = 0;
                    self.selected = 0;
                    self.refresh(ui.ctx());
                }
                let shown = self.entries.len().min(50);
                let mut scrolled = false;
                if shown > 0 {
                    use egui::{Key, Modifiers as M};
                    let key =
                        |modifiers, key| ui.input_mut(|input| input.consume_key(modifiers, key));
                    if key(M::NONE, Key::ArrowDown) {
                        self.selected = (self.selected + 1).min(shown - 1);
                        scrolled = true;
                    } else if key(M::NONE, Key::ArrowUp) {
                        self.selected = self.selected.saturating_sub(1);
                        scrolled = true;
                    } else if key(M::NONE, Key::PageDown) {
                        self.selected = (self.selected + 5).min(shown - 1);
                        scrolled = true;
                    } else if key(M::NONE, Key::PageUp) {
                        self.selected = self.selected.saturating_sub(5);
                        scrolled = true;
                    }
                    let entry = &self.entries[self.selected.min(shown - 1)];
                    operation = if key(M::SHIFT, Key::Enter) {
                        Some(Action::Paste(entry.id, true))
                    } else if key(M::NONE, Key::Enter) {
                        Some(Action::Paste(entry.id, false))
                    } else if key(M::CTRL, Key::O) {
                        Some(Action::View(entry.id))
                    } else if key(M::CTRL, Key::P) {
                        Some(Action::Pin(entry.id, !entry.pinned))
                    } else if key(M::CTRL, Key::Delete)
                        || (self.query.is_empty() || !search.has_focus())
                            && key(M::NONE, Key::Delete)
                    {
                        Some(Action::Remove(entry.id))
                    } else {
                        [
                            Key::Num1,
                            Key::Num2,
                            Key::Num3,
                            Key::Num4,
                            Key::Num5,
                            Key::Num6,
                            Key::Num7,
                            Key::Num8,
                            Key::Num9,
                        ]
                        .into_iter()
                        .zip(&self.entries)
                        .find_map(|(digit, item)| {
                            key(M::CTRL, digit).then_some(Action::Paste(item.id, false))
                        })
                    };
                    #[cfg(target_os = "linux")]
                    if operation.is_none()
                        && entry.kind == 2
                        && !ocr_pending
                        && self.ocr_available
                        && key(M::CTRL, Key::T)
                    {
                        operation = Some(Action::Ocr(entry.id));
                    }
                }
                if shown == 0 && self.search.is_none() {
                    ui.label(tr("history.empty", "Nothing yet — copy something"));
                }
                let list_height = (ui.available_height() - 85.).max(24.);
                egui::ScrollArea::vertical()
                    .max_height(list_height)
                    .auto_shrink([false, false])
                    .show(ui, |ui| {
                        for index in 0..shown {
                            let entry = self.entries[index].clone();
                            let (rect, row) = ui.allocate_exact_size(
                                egui::vec2(
                                    ui.available_width(),
                                    if entry.kind == 2 { 138. } else { 86. },
                                ),
                                egui::Sense::click(),
                            );
                            if scrolled && index == self.selected {
                                row.scroll_to_me(None);
                            }
                            if !ui.is_rect_visible(rect) {
                                continue;
                            }
                            if row.clicked() || row.secondary_clicked() {
                                self.selected = index;
                            }
                            if row.double_clicked() {
                                operation = Some(Action::Paste(entry.id, false));
                            }
                            if self.selected == index || row.hovered() {
                                let color = if self.selected == index {
                                    ui.visuals().selection.bg_fill
                                } else {
                                    ui.visuals().widgets.hovered.bg_fill
                                };
                                ui.painter().rect_filled(rect, 8, color);
                            }
                            let left = rect.left() + 8.;
                            let top = rect.top() + 7.;
                            if index < 9 {
                                let badge = egui::Rect::from_min_size(
                                    egui::pos2(left, top),
                                    egui::vec2(20., 20.),
                                );
                                ui.painter().rect_filled(
                                    badge,
                                    5,
                                    ui.visuals().widgets.inactive.bg_fill,
                                );
                                ui.painter().text(
                                    badge.center(),
                                    egui::Align2::CENTER_CENTER,
                                    (index + 1).to_string(),
                                    egui::FontId::proportional(11.),
                                    ui.visuals().text_color(),
                                );
                            }
                            if entry.pinned {
                                ui.painter().text(
                                    egui::pos2(rect.right() - 7., top),
                                    egui::Align2::RIGHT_TOP,
                                    "📌",
                                    egui::FontId::proportional(13.),
                                    muted,
                                );
                            }
                            let content = egui::Rect::from_min_max(
                                egui::pos2(left + 28., top),
                                egui::pos2(rect.right() - 27., rect.bottom() - 2.),
                            );
                            ui.scope_builder(egui::UiBuilder::new().max_rect(content), |ui| {
                                ui.set_clip_rect(ui.clip_rect().intersect(content));
                                if entry.kind == 2 {
                                    self.thumbnail(entry.id, ui.ctx());
                                    match self.thumbnails.get(&entry.id) {
                                        Some(Some(image)) => {
                                            egui::Frame::new()
                                                .stroke(border)
                                                .corner_radius(6)
                                                .show(ui, |ui| {
                                                    ui.add(
                                                        egui::Image::new(image)
                                                            .max_height(110.)
                                                            .max_width(360.)
                                                            .corner_radius(5),
                                                    );
                                                });
                                        }
                                        Some(None) => {
                                            ui.label(tr(
                                                "history.loadFailed",
                                                "Image preview unavailable",
                                            ));
                                        }
                                        None => {
                                            ui.spinner();
                                        }
                                    }
                                } else {
                                    let text = entry
                                        .preview
                                        .lines()
                                        .take(4)
                                        .map(|line| {
                                            if entry.kind == 3 {
                                                let name = std::path::Path::new(line)
                                                    .file_name()
                                                    .and_then(|name| name.to_str())
                                                    .unwrap_or(line);
                                                format!("📄 {name}")
                                            } else {
                                                line.replace('\t', " ")
                                            }
                                        })
                                        .collect::<Vec<_>>()
                                        .join("\n");
                                    let mut layout = egui::text::LayoutJob::simple(
                                        text,
                                        egui::FontId::proportional(13.5),
                                        ui.visuals().text_color(),
                                        ui.available_width(),
                                    );
                                    layout.wrap.max_rows = 4;
                                    ui.label(layout);
                                }
                            });
                            row.context_menu(|ui| {
                                let mut command = |label, shortcut, action| {
                                    if ui
                                        .add(egui::Button::new(label).shortcut_text(shortcut))
                                        .clicked()
                                    {
                                        operation = Some(action);
                                        ui.close();
                                    }
                                };
                                command(
                                    tr("history.ctx.open", "Open"),
                                    "Ctrl+O",
                                    Action::View(entry.id),
                                );
                                command(
                                    tr("history.ctx.paste", "Paste"),
                                    "Enter",
                                    Action::Paste(entry.id, false),
                                );
                                if entry.kind == 1 {
                                    command(
                                        tr("history.ctx.plain", "Paste as plain text"),
                                        "Shift+Enter",
                                        Action::Paste(entry.id, true),
                                    );
                                }
                                command(
                                    tr("rust.history.copy", "Copy"),
                                    "",
                                    Action::Copy(entry.id),
                                );
                                #[cfg(target_os = "linux")]
                                if entry.kind == 2 && !ocr_pending && self.ocr_available {
                                    command(
                                        tr("history.ctx.ocr", "Copy text from image"),
                                        "Ctrl+T",
                                        Action::Ocr(entry.id),
                                    );
                                }
                                let pin = if entry.pinned {
                                    tr("history.ctx.unpin", "Unpin")
                                } else {
                                    tr("history.ctx.pin", "Pin")
                                };
                                command(pin, "Ctrl+P", Action::Pin(entry.id, !entry.pinned));
                                command(
                                    tr("history.ctx.delete", "Delete"),
                                    "Del",
                                    Action::Remove(entry.id),
                                );
                            });
                            if entry.kind != 2 {
                                row.on_hover_text(entry.preview);
                            }
                        }
                    });
                if self.page > 0 || self.entries.len() > 50 {
                    ui.horizontal(|ui| {
                        if self.page > 0
                            && ui.small_button(tr("rust.common.back", "Back")).clicked()
                        {
                            self.page -= 1;
                            self.selected = 0;
                            self.refresh = true;
                        }
                        if self.entries.len() > 50
                            && ui.small_button(tr("rust.common.next", "Next")).clicked()
                        {
                            self.page += 1;
                            self.selected = 0;
                            self.refresh = true;
                        }
                    });
                }
                ui.separator();
                ui.label(
                    egui::RichText::new(tr(
                        "history.hints",
                        "Enter paste · Ctrl+O open · Ctrl+P pin · Del delete",
                    ))
                    .size(11.5)
                    .color(muted),
                );
                if !self.status.is_empty() {
                    ui.label(egui::RichText::new(&self.status).size(11.5).color(muted));
                }
            });
        if let Some(operation) = operation
            && let Err(error) = self.action(operation, ui)
        {
            self.status = error.to_string();
        }
        if self.refresh {
            ui.ctx().request_repaint();
        }
    }
}

#[cfg(target_os = "linux")]
#[path = "linux/history_ocr.rs"]
mod linux_ocr;
