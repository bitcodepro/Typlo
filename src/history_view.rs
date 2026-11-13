use crate::windows::{
    self,
    clipio::{Clip, Kind},
    history::{Entry, History, Limits, read_entry},
    lan_clipboard,
};
use crate::{appearance, config, locale, locale::text as tr};
use eframe::egui::{self, Color32, RichText};
use raw_window_handle::{HasWindowHandle, RawWindowHandle};
use std::{
    collections::{HashMap, VecDeque},
    io::{self, Read},
    path::PathBuf,
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
        mpsc,
    },
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};
use windows_sys::Win32::{
    Foundation::{HWND, POINT, RECT},
    Graphics::Gdi::{GetMonitorInfoW, MONITOR_DEFAULTTONEAREST, MONITORINFO, MonitorFromPoint},
    UI::{
        HiDpi::GetDpiForWindow,
        WindowsAndMessaging::{
            GetPhysicalCursorPos, SWP_NOACTIVATE, SWP_NOSIZE, SWP_NOZORDER, SetWindowPos,
        },
    },
};

const WIDTH: i32 = 460;
const HEIGHT: i32 = 560;
const PAGE: usize = 50;
const THUMBNAIL_CACHE_SIZE: usize = 32;

pub fn run(
    profile: PathBuf,
    origin: [usize; 3],
    anchor: Option<[i32; 2]>,
) -> Result<(), Box<dyn std::error::Error>> {
    let store = History::open(&profile)?;
    let config = locale::load(&profile);
    let mut cursor = POINT::default();
    let positioned = if let Some([x, y]) = anchor {
        cursor = POINT { x, y };
        true
    } else {
        (unsafe { GetPhysicalCursorPos(&mut cursor) }) != 0
    };
    let title = tr("history.title", "Typlo2.0 — clipboard history");
    eframe::run_native(
        title,
        eframe::NativeOptions {
            viewport: appearance::window()
                .with_inner_size([WIDTH as f32, HEIGHT as f32])
                .with_decorations(false)
                .with_taskbar(false)
                .with_resizable(false)
                .with_transparent(true)
                .with_always_on_top(),
            renderer: eframe::Renderer::Glow,
            ..Default::default()
        },
        Box::new(move |creation| {
            if positioned {
                place(window(creation), cursor, [WIDTH, HEIGHT], false);
            }
            appearance::configure(&creation.egui_ctx);
            locale::theme(&creation.egui_ctx, config.ui_theme);
            locale::fonts(&creation.egui_ctx, &config.ui_language);
            let (sender, previous) = mpsc::sync_channel(1);
            let context = creation.egui_ctx.clone();
            std::thread::spawn(move || {
                let mut command = [0u8; 1];
                if io::stdin().read_exact(&mut command).is_ok() && command == *b"p" {
                    let _ = sender.send(());
                    context.request_repaint();
                }
            });
            let lan_paired =
                lan_clipboard::paired_address(&profile).is_ok_and(|peer| peer.is_some());
            let lan_job = lan_clipboard::jobs::status(&profile).ok().flatten();
            let mut view = Window {
                data_version: store.data_version()?,
                history_check: Instant::now(),
                store,
                config,
                profile,
                origin,
                previous,
                query: String::new(),
                entries: Vec::new(),
                recent: Vec::new(),
                search: None,
                search_worker: None,
                select_search_result: false,
                received_only: false,
                received_size: None,
                received_size_job: None,
                selected: None,
                total: 0,
                more: false,
                thumbnails: HashMap::new(),
                thumbnail_order: VecDeque::new(),
                thumbnail_job: None,
                formatting: None,
                status: String::new(),
                lan_paired,
                lan_transfer: None,
                lan_job,
                lan_check: Instant::now(),
                delete_received: None,
                deleting_received: None,
                legacy_size: None,
                legacy_size_job: None,
                ocr: None,
                viewer: None,
                edit: None,
                focus_seen: false,
                focus_search: true,
                closing: false,
                scroll_selected: false,
            };
            view.reload()?;
            view.selected = view.recent.first().map(|entry| entry.id);
            Ok(Box::new(view))
        }),
    )?;
    Ok(())
}

struct Window {
    store: History,
    data_version: i64,
    history_check: Instant,
    config: config::Config,
    profile: PathBuf,
    origin: [usize; 3],
    previous: mpsc::Receiver<()>,
    query: String,
    entries: Vec<Entry>,
    recent: Vec<Entry>,
    search: Option<Search>,
    search_worker: Option<mpsc::Sender<SearchRequest>>,
    select_search_result: bool,
    received_only: bool,
    received_size: Option<u64>,
    received_size_job: Option<mpsc::Receiver<io::Result<u64>>>,
    selected: Option<i64>,
    total: usize,
    more: bool,
    thumbnails: HashMap<i64, Option<Thumbnail>>,
    thumbnail_order: VecDeque<i64>,
    thumbnail_job: Option<ThumbnailJob>,
    formatting: Option<(i64, bool)>,
    status: String,
    lan_paired: bool,
    lan_transfer: Option<mpsc::Receiver<io::Result<()>>>,
    lan_job: Option<lan_clipboard::jobs::Job>,
    lan_check: Instant,
    delete_received: Option<i64>,
    deleting_received: Option<(i64, mpsc::Receiver<io::Result<()>>)>,
    legacy_size: Option<(i64, Option<u64>)>,
    legacy_size_job: Option<(i64, mpsc::Receiver<io::Result<u64>>)>,
    ocr: Option<Ocr>,
    viewer: Option<Viewer>,
    edit: Option<EntryEdit>,
    focus_seen: bool,
    focus_search: bool,
    closing: bool,
    scroll_selected: bool,
}

struct Search {
    cancel: Arc<AtomicBool>,
    reply: mpsc::Receiver<io::Result<SearchResults>>,
}

struct SearchRequest {
    query: String,
    received_only: bool,
    image_terms: String,
    cancel: Arc<AtomicBool>,
    reply: mpsc::SyncSender<io::Result<SearchResults>>,
}

#[derive(Default)]
struct SearchResults {
    entries: Vec<Entry>,
    unreadable: usize,
}

struct SearchEntry {
    entry: Entry,
    revision: [u8; 32],
    preview: String,
    source: String,
    full: Option<String>,
    long: bool,
}

impl SearchEntry {
    fn matches(&self, query: &str, image_terms: &str) -> bool {
        self.preview.contains(query)
            || self.source.contains(query)
            || self.full.as_ref().is_some_and(|text| text.contains(query))
            || (self.entry.kind == Kind::Image
                && (image_terms.contains(query) || "image".contains(query)))
    }

    fn bytes(&self) -> usize {
        self.preview.capacity()
            + self.source.capacity()
            + self.full.as_ref().map_or(0, String::capacity)
            + self.entry.preview.capacity()
            + self.entry.source.as_ref().map_or(0, String::capacity)
            + 2 * size_of::<Self>()
    }
}

#[derive(Default)]
struct SearchCache {
    version: Option<i64>,
    complete: bool,
    rows: Vec<SearchEntry>,
}

fn search_history(
    store: &History,
    cache: &mut SearchCache,
    query: &str,
    received_only: bool,
    image_terms: &str,
    canceled: impl Fn() -> bool,
) -> io::Result<SearchResults> {
    const MAX_CACHE_BYTES: usize = 2 * 1024 * 1024;
    let version = store.data_version()?;
    let mut result = SearchResults::default();
    if cache.version == Some(version) && cache.complete {
        for row in &cache.rows {
            if canceled() {
                return Err(io::ErrorKind::Interrupted.into());
            }
            if received_only && !received_file(&row.entry) {
                continue;
            }
            let found = row.matches(query, image_terms)
                || (row.long
                    && row.full.is_none()
                    && match full_search_text(store, row.entry.id) {
                        Ok(text) => text.contains(query),
                        Err(_) => {
                            result.unreadable += 1;
                            false
                        }
                    });
            if found {
                result.entries.push(row.entry.clone());
            }
        }
        return Ok(result);
    }
    let mut previous: HashMap<_, _> = std::mem::take(&mut cache.rows)
        .into_iter()
        .map(|row| (row.entry.id, row))
        .collect();
    cache.complete = false;
    cache.version = None;
    let mut cache_bytes = 0;
    let mut omitted = false;
    store.scan(|record| {
        if canceled() {
            return false;
        }
        let Ok((id, revision)) = search_revision(record) else {
            result.unreadable += 1;
            return true;
        };
        let cached = previous.remove(&id).filter(|row| row.revision == revision);
        let Ok(entry) = read_entry(record, cached.as_ref().map(|row| &row.entry)) else {
            result.unreadable += 1;
            return true;
        };
        let mut row = if let Some(mut row) = cached {
            row.entry = entry;
            row
        } else {
            let preview = entry.preview.to_lowercase();
            let source = entry.source.as_deref().unwrap_or_default().to_lowercase();
            let long = entry.preview.chars().count() >= 400
                || entry
                    .preview
                    .split_once('\0')
                    .is_some_and(|(paths, _)| paths.chars().count() >= 200);
            SearchEntry {
                entry,
                revision,
                preview,
                source,
                full: None,
                long,
            }
        };
        if !received_only || received_file(&row.entry) {
            let mut found = row.matches(query, image_terms);
            if !found && row.long && row.full.is_none() {
                match full_search_text(store, row.entry.id) {
                    Ok(text) => {
                        found = text.contains(query);
                        row.full = Some(text);
                    }
                    Err(_) => result.unreadable += 1,
                }
            }
            if found {
                result.entries.push(row.entry.clone());
            }
        }
        let bytes = row.bytes();
        if cache_bytes + bytes <= MAX_CACHE_BYTES {
            cache_bytes += bytes;
            cache.rows.push(row);
        } else {
            omitted = true;
        }
        true
    })?;
    if canceled() {
        cache.rows.clear();
        return Err(io::ErrorKind::Interrupted.into());
    }
    cache.version = Some(version);
    cache.complete = !omitted && result.unreadable == 0;
    Ok(result)
}

fn search_revision(row: &rusqlite::Row<'_>) -> rusqlite::Result<(i64, [u8; 32])> {
    use sha2::{Digest, Sha256};
    let mut hash = Sha256::new();
    for column in [3, 5, 10] {
        let bytes = row.get_ref(column)?.as_blob_or_null()?.unwrap_or_default();
        hash.update((bytes.len() as u64).to_le_bytes());
        hash.update(bytes);
    }
    Ok((row.get(0)?, hash.finalize().into()))
}

fn full_search_text(store: &History, id: i64) -> io::Result<String> {
    Ok(store
        .load(id)?
        .map_or_else(String::new, |clip| clip.searchable_text().to_lowercase()))
}

fn start_history_search(
    open: impl Fn() -> io::Result<History> + Send + 'static,
    context: egui::Context,
) -> io::Result<mpsc::Sender<SearchRequest>> {
    let (submit, pending) = mpsc::channel::<SearchRequest>();
    std::thread::Builder::new()
        .name("typlo-history-search".into())
        .spawn(move || {
            let mut reader = None;
            let mut cache = SearchCache::default();
            while let Ok(mut request) = pending.recv() {
                loop {
                    match pending.recv_timeout(Duration::from_millis(35)) {
                        Ok(newer) => request = newer,
                        Err(mpsc::RecvTimeoutError::Timeout) => break,
                        Err(mpsc::RecvTimeoutError::Disconnected) => return,
                    }
                }
                let canceled = || request.cancel.load(Ordering::Acquire);
                if canceled() {
                    continue;
                }
                let result = (|| {
                    if reader.is_none() {
                        reader = Some(open()?);
                    }
                    search_history(
                        reader.as_ref().expect("history reader opened"),
                        &mut cache,
                        &request.query,
                        request.received_only,
                        &request.image_terms,
                        canceled,
                    )
                })();
                if !canceled() {
                    let _ = request.reply.send(result);
                    context.request_repaint();
                }
            }
        })?;
    Ok(submit)
}

struct Thumbnail {
    image: egui::TextureHandle,
    dimensions: (u32, u32),
}

struct ThumbnailJob {
    id: i64,
    reply: mpsc::Receiver<Option<(image::RgbaImage, (u32, u32))>>,
}

impl Drop for Search {
    fn drop(&mut self) {
        self.cancel.store(true, Ordering::Release);
    }
}

struct Ocr {
    reply: mpsc::Receiver<io::Result<String>>,
    clipboard: u32,
}

struct Viewer {
    id: i64,
    text: String,
    image: Option<egui::TextureHandle>,
}

struct EntryEdit {
    id: i64,
    kind: Kind,
    original: String,
    text: String,
    error: String,
}

#[derive(Clone, Copy)]
enum Choice {
    Paste(i64, bool),
    Copy(i64),
    Pin(i64, bool),
    Remove(i64),
    View(i64),
    Edit(i64),
    Ocr(i64),
    Send(i64),
    ShowReceived(i64),
    DeleteReceived(i64),
}

impl eframe::App for Window {
    fn clear_color(&self, visuals: &egui::Visuals) -> [f32; 4] {
        if self.viewer.is_some() {
            visuals.panel_fill.to_normalized_gamma_f32()
        } else {
            [0.; 4]
        }
    }

    fn ui(&mut self, root: &mut egui::Ui, frame: &mut eframe::Frame) {
        self.poll_ocr(root, window(frame));
        self.poll_lan_transfer(root.ctx());
        self.poll_received_delete(root.ctx());
        if self.previous.try_recv().is_ok()
            && self.viewer.is_none()
            && self.edit.is_none()
            && self.delete_received.is_none()
            && let Ok(Some(id)) = self.store.previous()
        {
            self.apply(Choice::Paste(id, false), root, window(frame));
        }
        if self.closing {
            return;
        }
        let focused = root.input(|input| input.focused);
        if focused {
            self.focus_seen = true;
        }
        if self.viewer.is_none() && self.focus_seen && !focused {
            if self.ocr.is_some() || self.deleting_received.is_some() {
                root.ctx()
                    .send_viewport_cmd(egui::ViewportCommand::Visible(false));
            } else {
                self.close(root);
            }
            return;
        }
        if root.input(|input| input.key_pressed(egui::Key::Escape)) {
            if self.edit.take().is_some() {
                return;
            }
            if self.delete_received.take().is_some() {
                return;
            }
            self.close(root);
            return;
        }
        if self.viewer.is_some() {
            self.show_viewer(root, window(frame));
            return;
        }
        self.poll_history(root.ctx());
        self.poll_search();
        self.poll_received_size();
        self.poll_legacy_size(root.ctx());
        self.poll_thumbnail(root.ctx());
        let palette = Palette::new(root.visuals().dark_mode);
        egui::Frame::new().fill(palette.panel)
            .stroke(egui::Stroke::new(1_f32, palette.border)).corner_radius(12)
            .show(root, |ui| {
                ui.set_min_size(ui.available_size());
                self.search_box(ui, &palette);
                self.received_filter(ui);
                let command = if self.delete_received.is_some() || self.edit.is_some() { None } else { self.keys(ui) };
                let footer = tr("history.hints", "Enter paste · Shift+Enter plain · Ctrl+T text from image · Ctrl+P pin · Del delete · right click: more");
                egui::Panel::bottom("history-footer")
                    .resizable(false)
                    .show_separator_line(true)
                    .frame(egui::Frame::new().inner_margin(egui::Margin::symmetric(12, 7)))
                    .show_inside(ui, |ui| {
                        ui.label(RichText::new(footer).size(11.5).color(palette.muted));
                        if !self.status.is_empty() {
                            ui.label(RichText::new(&self.status).size(11.5).color(palette.muted));
                        }
                        self.lan_footer(ui);
                    });
                let rows_height = ui.available_height().max(28.);
                let clicked = self.rows(ui, &palette, rows_height);
                if let Some(action) = clicked.or(command) {
                    self.apply(action, ui, window(frame));
                }
            });
        self.confirm_received_delete(root);
        self.edit_dialog(root);
    }
}

impl Window {
    fn filtered(&self) -> bool {
        self.received_only || !self.query.trim().is_empty()
    }

    fn items(&self) -> &[Entry] {
        if self.filtered() {
            &self.entries
        } else {
            &self.recent
        }
    }

    fn reload(&mut self) -> io::Result<()> {
        let mut items = Vec::new();
        let limit = self.recent.len().max(PAGE);
        loop {
            let mut page = self.store.list(items.len())?;
            self.more = page.len() > PAGE;
            page.truncate(PAGE);
            items.extend(page);
            let selected_missing =
                self.selected.is_some() && selection_index(&items, self.selected).is_none();
            if !self.more || items.len() >= limit + if selected_missing { PAGE } else { 0 } {
                break;
            }
        }
        self.recent = items;
        self.total = if self.more {
            self.store.count()?
        } else {
            self.recent.len()
        };
        Ok(())
    }

    fn selected_index(&self) -> Option<usize> {
        selection_index(self.items(), self.selected)
    }

    fn reconcile_selection(&mut self) {
        if self.selected_index().is_none() {
            self.selected = None;
        }
    }

    fn poll_history(&mut self, ctx: &egui::Context) {
        const REFRESH: Duration = Duration::from_millis(350);
        let remaining = REFRESH.saturating_sub(self.history_check.elapsed());
        if !remaining.is_zero() {
            ctx.request_repaint_after(remaining);
            return;
        }
        self.history_check = Instant::now();
        ctx.request_repaint_after(REFRESH);
        if let Ok(version) = self.store.data_version()
            && version != self.data_version
            && self.reload().is_ok()
        {
            self.data_version = version;
            if self.filtered() {
                self.filter(ctx);
            }
            if self.received_only {
                self.refresh_received_size(ctx);
            }
            if !self.filtered() {
                self.reconcile_selection();
            }
        }
    }

    fn received_filter(&mut self, ui: &mut egui::Ui) {
        ui.horizontal(|ui| {
            ui.add_space(12.);
            if ui
                .selectable_label(
                    self.received_only,
                    tr("rust.lan.receivedFilter", "Received files"),
                )
                .clicked()
            {
                self.received_only.toggle();
                self.filter(ui.ctx());
                self.select_first_match();
                if self.received_only {
                    self.refresh_received_size(ui.ctx());
                }
            }
            if self.received_only {
                if let Some(bytes) = self.received_size {
                    ui.weak(format!("{:.1} MiB", bytes as f64 / (1024. * 1024.)));
                }
                if ui
                    .small_button(tr("rust.lan.openFolder", "Open folder"))
                    .clicked()
                    && let Err(error) = lan_clipboard::open_received_folder(&self.profile)
                {
                    self.status = error.to_string();
                }
            }
        });
    }

    fn refresh_received_size(&mut self, ctx: &egui::Context) {
        if self.received_size_job.is_some() {
            return;
        }
        let (sender, receiver) = mpsc::sync_channel(1);
        let profile = self.profile.clone();
        let ctx = ctx.clone();
        std::thread::spawn(move || {
            let _ = sender.send(lan_clipboard::received_bytes(&profile));
            ctx.request_repaint();
        });
        self.received_size_job = Some(receiver);
    }

    fn poll_received_size(&mut self) {
        if let Some(receiver) = &self.received_size_job {
            match receiver.try_recv() {
                Err(mpsc::TryRecvError::Empty) => {}
                result => {
                    self.received_size = result.ok().and_then(Result::ok);
                    self.received_size_job = None;
                }
            }
        }
    }

    fn search_box(&mut self, ui: &mut egui::Ui, palette: &Palette) {
        let field = ui.id().with("search");
        let focused = ui.memory(|memory| memory.has_focus(field));
        egui::Frame::new()
            .outer_margin(egui::Margin {
                left: 12,
                right: 12,
                top: 12,
                bottom: 7,
            })
            .inner_margin(egui::Margin::symmetric(10, 5))
            .fill(if focused {
                palette.focus
            } else {
                palette.search
            })
            .corner_radius(10)
            .stroke(egui::Stroke::new(
                1.5_f32,
                if focused {
                    palette.accent
                } else {
                    Color32::TRANSPARENT
                },
            ))
            .show(ui, |ui| {
                ui.horizontal(|ui| {
                    let (rect, _) =
                        ui.allocate_exact_size(egui::vec2(15., 28.), egui::Sense::hover());
                    let center = rect.center() - egui::vec2(1.8, 1.8);
                    let line = egui::Stroke::new(1.6_f32, palette.muted);
                    ui.painter().circle_stroke(center, 4.3, line);
                    ui.painter().line_segment(
                        [center + egui::vec2(3., 3.), center + egui::vec2(7., 7.)],
                        line,
                    );
                    let count = if !self.filtered() {
                        self.total.to_string()
                    } else {
                        format!(
                            "{} / {}{}",
                            self.entries.len(),
                            self.total,
                            if self.search.is_some() { " …" } else { "" }
                        )
                    };
                    let count_width = ui
                        .painter()
                        .layout_no_wrap(
                            count.clone(),
                            egui::FontId::proportional(11.5),
                            palette.muted,
                        )
                        .size()
                        .x
                        + 12.;
                    let response = ui.add(
                        history_search_input(&mut self.query)
                            .id(field)
                            .desired_width(
                                (ui.available_width() - count_width - ui.spacing().item_spacing.x)
                                    .max(40.),
                            )
                            .frame(egui::Frame::NONE)
                            .hint_text(tr("history.search", "Search clipboard…")),
                    );
                    if self.focus_search {
                        response.request_focus();
                        self.focus_search = false;
                    }
                    if response.changed() {
                        self.scroll_selected = true;
                        self.filter(ui.ctx());
                        self.select_first_match();
                    }
                    egui::Frame::new()
                        .fill(palette.badge)
                        .corner_radius(6)
                        .inner_margin(egui::Margin::symmetric(6, 2))
                        .show(ui, |ui| {
                            ui.label(RichText::new(count).size(11.5).color(palette.muted));
                        });
                });
            });
    }

    fn filter(&mut self, context: &egui::Context) {
        self.search = None;
        let query = self.query.trim().to_lowercase();
        if !self.filtered() {
            return;
        }
        let received_only = self.received_only;
        self.entries = self
            .recent
            .iter()
            .filter(|entry| !received_only || received_file(entry))
            .filter(|entry| {
                entry_matches(entry, &query)
                    || entry.kind == Kind::Image
                        && tr("history.imageSearch", "image picture").contains(&query)
            })
            .cloned()
            .collect();
        let cancel = Arc::new(AtomicBool::new(false));
        let (sender, reply) = mpsc::sync_channel(1);
        let request = SearchRequest {
            query,
            received_only,
            image_terms: tr("history.imageSearch", "image picture").to_owned(),
            cancel: Arc::clone(&cancel),
            reply: sender,
        };
        if self.search_worker.is_none() {
            match start_history_search(self.store.background_reader(&self.profile), context.clone())
            {
                Ok(worker) => self.search_worker = Some(worker),
                Err(error) => {
                    self.status = error.to_string();
                    return;
                }
            }
        }
        if self
            .search_worker
            .as_ref()
            .is_some_and(|worker| worker.send(request).is_ok())
        {
            self.search = Some(Search { cancel, reply });
        } else {
            self.search_worker = None;
            self.status = "Clipboard search stopped".into();
        }
    }

    fn poll_search(&mut self) {
        let Some(search) = &self.search else {
            return;
        };
        let result = match search.reply.try_recv() {
            Ok(result) => result,
            Err(mpsc::TryRecvError::Empty) => return,
            Err(mpsc::TryRecvError::Disconnected) => {
                self.search = None;
                return;
            }
        };
        self.search = None;
        match result {
            Ok(results) => {
                self.entries = results.entries;
                if results.unreadable != 0 {
                    self.status = format!("Unreadable clipboard entries: {}", results.unreadable);
                }
                if std::mem::take(&mut self.select_search_result) {
                    self.selected = self.items().first().map(|entry| entry.id);
                } else {
                    self.reconcile_selection();
                }
            }
            Err(error) => self.status = error.to_string(),
        }
    }

    fn select_first_match(&mut self) {
        self.selected = self.items().first().map(|entry| entry.id);
        self.select_search_result = self.selected.is_none() && self.filtered();
    }

    fn keys(&mut self, ui: &egui::Ui) -> Option<Choice> {
        use egui::{Key, Modifiers as M};
        let key = |mods, key| ui.input_mut(|input| input.consume_key(mods, key));
        let delta = [
            (Key::ArrowDown, 1),
            (Key::ArrowUp, -1),
            (Key::PageDown, 8),
            (Key::PageUp, -8),
        ]
        .into_iter()
        .find_map(|(button, delta)| key(M::NONE, button).then_some(delta));
        if let Some(delta) = delta {
            let next = self
                .selected_index()
                .map_or(0, |index| index.saturating_add_signed(delta));
            if !self.filtered() && next >= self.recent.len() {
                self.load_more();
            }
            self.selected = self
                .items()
                .get(next.min(self.items().len().saturating_sub(1)))
                .map(|entry| entry.id);
            self.scroll_selected = true;
        }
        let number = [
            Key::Num1,
            Key::Num2,
            Key::Num3,
            Key::Num4,
            Key::Num5,
            Key::Num6,
            Key::Num7,
            Key::Num8,
            Key::Num9,
        ];
        if let Some(index) = number.into_iter().position(|button| key(M::CTRL, button)) {
            return self
                .items()
                .get(index)
                .map(|entry| Choice::Paste(entry.id, false));
        }
        let entry = self.items().get(self.selected_index()?)?;
        if key(M::SHIFT, Key::Enter) {
            Some(Choice::Paste(entry.id, true))
        } else if key(M::NONE, Key::Enter) {
            Some(Choice::Paste(entry.id, false))
        } else if key(M::CTRL, Key::O) {
            Some(Choice::View(entry.id))
        } else if key(M::CTRL, Key::T) && entry.kind == Kind::Image {
            Some(Choice::Ocr(entry.id))
        } else if key(M::CTRL, Key::P) {
            Some(Choice::Pin(entry.id, !entry.pinned))
        } else if key(M::CTRL, Key::Delete) || (self.query.is_empty() && key(M::NONE, Key::Delete))
        {
            Some(Choice::Remove(entry.id))
        } else {
            None
        }
    }

    fn rows(&mut self, ui: &mut egui::Ui, palette: &Palette, height: f32) -> Option<Choice> {
        let count = self.items().len();
        if count == 0 {
            let (rect, _) = ui.allocate_exact_size(
                egui::vec2(ui.available_width(), height),
                egui::Sense::hover(),
            );
            let message = if self.total == 0 {
                tr("history.empty", "Nothing yet — copy something")
            } else {
                tr("history.notFound", "Nothing found")
            };
            ui.painter().text(
                rect.center(),
                egui::Align2::CENTER_CENTER,
                message,
                egui::FontId::proportional(14.),
                palette.muted,
            );
            return None;
        }
        let mut offsets = Vec::with_capacity(count + 1);
        offsets.push(0.);
        for entry in self.items() {
            let height = match entry.kind {
                Kind::Text => 46.,
                Kind::Files => 65.,
                Kind::Image => {
                    if entry.preview.is_empty() {
                        145.
                    } else {
                        162.
                    }
                }
            };
            offsets.push(offsets.last().copied().unwrap_or_default() + height);
        }
        let mut choice = None;
        let mut bottom = false;
        let mut scroll = egui::ScrollArea::vertical()
            .max_height(height)
            .auto_shrink([false, false]);
        if std::mem::take(&mut self.scroll_selected)
            && let Some(index) = self.selected_index()
        {
            let current = (offsets[index] - height * 0.5).max(0.);
            scroll = scroll.vertical_scroll_offset(current);
        }
        scroll.show_viewport(ui, |ui, viewport| {
            let bounds = ui.max_rect();
            ui.set_height(offsets[count]);
            let start = offsets[1..].partition_point(|end| *end <= viewport.min.y);
            let end = offsets[..count].partition_point(|start| *start < viewport.max.y);
            bottom = end >= count;
            for index in start..end {
                let entry = self.items()[index].clone();
                let row = egui::Rect::from_x_y_ranges(
                    bounds.x_range(),
                    (bounds.top() + offsets[index])..=(bounds.top() + offsets[index + 1] - 2.),
                )
                .shrink2(egui::vec2(6., 0.));
                let response = ui.interact(row, ui.id().with(entry.id), egui::Sense::click());
                if response.clicked() || response.secondary_clicked() {
                    self.selected = Some(entry.id);
                }
                if response.double_clicked() {
                    choice = Some(Choice::Paste(entry.id, false));
                }
                if self.selected == Some(entry.id) || response.hovered() {
                    ui.painter().rect_filled(
                        row,
                        8,
                        if self.selected == Some(entry.id) {
                            palette.selected
                        } else {
                            palette.hover
                        },
                    );
                }
                let x = row.left() + 8.;
                let y = row.top() + if entry.kind == Kind::Text { 5. } else { 7. };
                if index < 9 {
                    let badge = egui::Rect::from_min_size(egui::pos2(x, y), egui::vec2(20., 20.));
                    ui.painter().rect_filled(badge, 5, palette.badge);
                    ui.painter().text(
                        badge.center(),
                        egui::Align2::CENTER_CENTER,
                        (index + 1).to_string(),
                        egui::FontId::proportional(11.),
                        ui.visuals().text_color(),
                    );
                }
                let send_area = egui::Rect::from_min_size(
                    egui::pos2(row.right() - 29., y),
                    egui::vec2(22., 22.),
                );
                let can_send = !self.lan_busy();
                let send = ui
                    .scope_builder(egui::UiBuilder::new().max_rect(send_area), |ui| {
                        ui.add_enabled_ui(can_send, |ui| {
                            ui.put(send_area, egui::Button::new("↗").small())
                        })
                        .inner
                    })
                    .inner
                    .on_hover_text(if self.lan_paired {
                        tr("rust.lan.send", "Send to paired computer")
                    } else {
                        tr(
                            "rust.lan.setup",
                            "Pair another computer in Clipboard sharing settings",
                        )
                    });
                if send.clicked() {
                    choice = Some(Choice::Send(entry.id));
                }
                let edit_area = send_area.translate(egui::vec2(-26., 0.));
                if ui
                    .put(edit_area, egui::Button::new("✎").small())
                    .on_hover_text(tr("rust.history.edit", "Edit clipboard item"))
                    .clicked()
                {
                    choice = Some(Choice::Edit(entry.id));
                }
                if entry.pinned {
                    ui.painter().text(
                        egui::pos2(row.right() - 58., y),
                        egui::Align2::RIGHT_TOP,
                        "📌",
                        egui::FontId::proportional(13.),
                        palette.muted,
                    );
                }
                let content = egui::Rect::from_min_max(
                    egui::pos2(x + 28., y),
                    egui::pos2(row.right() - 76., row.bottom() - 2.),
                );
                ui.scope_builder(egui::UiBuilder::new().max_rect(content), |ui| {
                    ui.set_clip_rect(ui.clip_rect().intersect(content));
                    ui.spacing_mut().item_spacing.y = 3.;
                    if entry.unreadable {
                        ui.label(tr(
                            "rust.history.unreadableEntry",
                            "Clipboard entry unavailable",
                        ));
                        return;
                    }
                    let size = if entry.bytes < 1024 {
                        tr("history.bytes", "{0} B").replace("{0}", &entry.bytes.to_string())
                    } else {
                        tr("history.kb", "{0:0.#} KB")
                            .replace("{0:0.#}", &format!("{:.1}", entry.bytes as f64 / 1024.))
                    };
                    let meta = match entry.kind {
                        Kind::Image => {
                            self.request_thumbnail(entry.id, ui.ctx());
                            let meta = match self.thumbnails.get(&entry.id) {
                                Some(Some(thumbnail)) => {
                                    let border = egui::Stroke::new(1_f32, palette.border);
                                    egui::Frame::new().stroke(border).corner_radius(6).show(
                                        ui,
                                        |ui| {
                                            ui.add({
                                                let size = thumbnail.image.size_vec2();
                                                let fit = size
                                                    * (110. / size.y)
                                                        .min(ui.available_width() / size.x)
                                                        .min(1.);
                                                egui::Image::new((thumbnail.image.id(), fit))
                                                    .corner_radius(5)
                                            });
                                        },
                                    );
                                    let (width, height) = thumbnail.dimensions;
                                    tr("history.image", "Image · {0} × {1}")
                                        .replace("{0}", &width.to_string())
                                        .replace("{1}", &height.to_string())
                                }
                                Some(None) => {
                                    ui.label(tr("history.loadFailed", "Image preview unavailable"));
                                    size
                                }
                                None => {
                                    ui.spinner();
                                    size
                                }
                            };
                            if !entry.preview.is_empty() {
                                ui.add(
                                    egui::Label::new(
                                        entry.preview.lines().next().unwrap_or_default(),
                                    )
                                    .truncate(),
                                );
                            }
                            meta
                        }
                        Kind::Files => {
                            let (paths, caption) = entry
                                .preview
                                .split_once('\0')
                                .unwrap_or((&entry.preview, ""));
                            let files = paths.lines().collect::<Vec<_>>();
                            let count = entry.files.unwrap_or(files.len());
                            let label = files
                                .iter()
                                .take(4)
                                .map(|path| {
                                    let name = path
                                        .trim_end_matches(['/', '\\'])
                                        .rsplit(['/', '\\'])
                                        .next()
                                        .unwrap_or(path);
                                    format!("📄 {name}")
                                })
                                .collect::<Vec<_>>()
                                .join("\n");
                            ui.add(
                                egui::Label::new(
                                    RichText::new(label.replace('\n', " · ")).size(13.5),
                                )
                                .truncate(),
                            );
                            if !caption.is_empty() {
                                ui.add(
                                    egui::Label::new(caption.lines().next().unwrap_or_default())
                                        .truncate(),
                                );
                            }
                            if count == 1 {
                                tr("history.file", "File").to_owned()
                            } else {
                                tr("history.files", "{0} files").replace("{0}", &count.to_string())
                            }
                        }
                        Kind::Text => {
                            let text = entry
                                .preview
                                .lines()
                                .map(str::trim_end)
                                .find(|line| !line.is_empty())
                                .unwrap_or_default()
                                .replace('\t', " ");
                            let mut galley = egui::text::LayoutJob::simple(
                                text,
                                egui::FontId::proportional(13.5),
                                ui.visuals().text_color(),
                                ui.available_width(),
                            );
                            galley.wrap.max_rows = 1;
                            ui.label(galley);
                            size
                        }
                    };
                    let formatted = entry
                        .formatted
                        .then(|| tr("history.formatted", "Formatted"));
                    let age = entry.used_at.map(relative_age);
                    let transfer_size = entry
                        .received_bytes
                        .or_else(|| {
                            self.legacy_size
                                .filter(|(id, _)| *id == entry.id)
                                .and_then(|(_, bytes)| bytes)
                        })
                        .map(file_size);
                    let meta = [
                        Some(meta.as_str()),
                        formatted,
                        entry.source.as_deref(),
                        age.as_deref(),
                        transfer_size.as_deref(),
                    ]
                    .into_iter()
                    .flatten()
                    .collect::<Vec<_>>()
                    .join("  ·  ");
                    ui.add(
                        egui::Label::new(RichText::new(meta).size(11.).color(palette.muted))
                            .truncate(),
                    );
                });
                response.context_menu(|menu| {
                    let formatted = if entry.kind == Kind::Text {
                        if self.formatting.is_none_or(|(id, _)| id != entry.id) {
                            self.formatting = Some((
                                entry.id,
                                self.store
                                    .load(entry.id)
                                    .ok()
                                    .flatten()
                                    .is_some_and(|clip| clip.has_formatting()),
                            ));
                        }
                        entry.formatted || self.formatting.is_some_and(|(_, formatted)| formatted)
                    } else {
                        false
                    };
                    let mut item = |label, shortcut, action, enabled| {
                        if menu
                            .add_enabled(enabled, egui::Button::new(label).shortcut_text(shortcut))
                            .clicked()
                        {
                            choice = Some(action);
                            menu.close();
                        }
                    };
                    item(
                        tr("history.ctx.paste", "Paste"),
                        "Enter",
                        Choice::Paste(entry.id, false),
                        true,
                    );
                    item(
                        tr("rust.history.copy", "Copy"),
                        "",
                        Choice::Copy(entry.id),
                        true,
                    );
                    item(
                        tr("history.ctx.view", "View…"),
                        "Ctrl+O",
                        Choice::View(entry.id),
                        true,
                    );
                    if entry.kind != Kind::Image {
                        item(
                            tr("history.ctx.plain", "Paste as plain text"),
                            "Shift+Enter",
                            Choice::Paste(entry.id, true),
                            formatted,
                        );
                    }
                    if entry.kind == Kind::Image {
                        item(
                            tr("history.ctx.ocr", "Copy text from image"),
                            "Ctrl+T",
                            Choice::Ocr(entry.id),
                            self.ocr.is_none() && windows::ocr::available(),
                        );
                    }
                    item(
                        tr("rust.lan.send", "Send to paired computer"),
                        "",
                        Choice::Send(entry.id),
                        can_send,
                    );
                    if received_file(&entry) {
                        item(
                            tr("rust.lan.showReceived", "Show in File Explorer"),
                            "",
                            Choice::ShowReceived(entry.id),
                            true,
                        );
                        item(
                            tr("rust.lan.deleteReceived", "Delete entry and files…"),
                            "",
                            Choice::DeleteReceived(entry.id),
                            self.deleting_received.is_none(),
                        );
                    }
                    menu.separator();
                    let pin = if entry.pinned {
                        tr("history.ctx.unpin", "Unpin")
                    } else {
                        tr("history.ctx.pin", "Pin")
                    };
                    if menu
                        .add(egui::Button::new(pin).shortcut_text("Ctrl+P"))
                        .clicked()
                    {
                        choice = Some(Choice::Pin(entry.id, !entry.pinned));
                        menu.close();
                    }
                    if menu
                        .add(
                            egui::Button::new(tr("history.ctx.delete", "Delete"))
                                .shortcut_text("Del"),
                        )
                        .clicked()
                    {
                        choice = Some(Choice::Remove(entry.id));
                        menu.close();
                    }
                });
                if !entry.preview.is_empty() {
                    response.on_hover_text(entry.preview.replace('\0', "\n"));
                }
            }
        });
        if bottom && !self.filtered() {
            self.load_more();
        }
        choice
    }

    fn load_more(&mut self) {
        if !self.more {
            return;
        }
        match self.store.list(self.recent.len()) {
            Ok(mut page) => {
                self.more = page.len() > PAGE;
                page.truncate(PAGE);
                self.recent.extend(page);
            }
            Err(error) => {
                self.status = error.to_string();
                self.more = false;
            }
        }
    }

    fn show_viewer(&mut self, ui: &mut egui::Ui, window: HWND) {
        let viewer = self.viewer.as_mut().expect("viewer open");
        let id = viewer.id;
        let mut paste = false;
        let mut copy = false;
        let mut close = false;
        egui::Frame::new()
            .fill(ui.visuals().panel_fill)
            .inner_margin(12.)
            .show(ui, |ui| {
                egui::ScrollArea::both()
                    .max_height((ui.available_height() - 49.).max(20.))
                    .show(ui, |ui| {
                        if let Some(image) = &viewer.image {
                            ui.image((image.id(), image.size_vec2()));
                        }
                        if !viewer.text.is_empty() {
                            ui.add(
                                egui::TextEdit::multiline(&mut viewer.text.as_str())
                                    .font(egui::TextStyle::Monospace)
                                    .desired_width(f32::INFINITY)
                                    .desired_rows(16),
                            );
                        }
                    });
                ui.separator();
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    paste = ui.button(tr("history.ctx.paste", "Paste")).clicked();
                    copy = ui.button(tr("rust.history.copy", "Copy")).clicked();
                    close = ui.button(tr("update.close", "Close")).clicked();
                });
            });
        if paste {
            self.apply(Choice::Paste(id, false), ui, window);
        }
        if copy {
            self.apply(Choice::Copy(id), ui, window);
        }
        if close {
            self.close(ui);
        }
    }

    fn apply(&mut self, action: Choice, ui: &egui::Ui, window: HWND) {
        let result = (|| match action {
            Choice::Paste(id, plain) => {
                let clip = self.store.load(id)?.ok_or_else(missing)?;
                windows::paste_history(window, self.origin, &clip, plain, self.origin[0] != 0)?;
                self.store.touch(id, self.config.history_move_to_top)?;
                self.close(ui);
                Ok(())
            }
            Choice::Copy(id) => {
                let clip = self.store.load(id)?.ok_or_else(missing)?;
                windows::clipio::publish(window, &clip, false)?;
                self.close(ui);
                Ok(())
            }
            Choice::View(id) => {
                let clip = self.store.load(id)?.ok_or_else(missing)?;
                let image = if clip.kind == Kind::Image {
                    Some(texture(ui.ctx(), &clip.image()?.into_rgba8()))
                } else {
                    None
                };
                self.viewer = Some(Viewer {
                    id,
                    text: clip.searchable_text(),
                    image,
                });
                let ctx = ui.ctx();
                ctx.send_viewport_cmd(egui::ViewportCommand::Title(
                    tr("viewer.title", "Typlo2.0 — clipboard item").into(),
                ));
                ctx.send_viewport_cmd(egui::ViewportCommand::Decorations(true));
                ctx.send_viewport_cmd(egui::ViewportCommand::WindowLevel(
                    egui::WindowLevel::Normal,
                ));
                let mut cursor = POINT::default();
                if unsafe { GetPhysicalCursorPos(&mut cursor) } != 0 {
                    place(window, cursor, [640, 520], true);
                }
                Ok(())
            }
            Choice::Edit(id) => {
                let clip = self.store.load(id)?.ok_or_else(missing)?;
                let text = clip.editable_text();
                self.edit = Some(EntryEdit {
                    id,
                    kind: clip.kind,
                    original: text.clone(),
                    text,
                    error: String::new(),
                });
                Ok(())
            }
            Choice::Pin(id, pinned) => {
                self.store.pin(id, pinned)?;
                self.reload()?;
                self.filter(ui.ctx());
                Ok(())
            }
            Choice::Remove(id) => {
                self.store.remove(id)?;
                self.thumbnails.remove(&id);
                self.thumbnail_order.retain(|&cached| cached != id);
                self.reload()?;
                self.filter(ui.ctx());
                if self.selected == Some(id) || !self.filtered() && self.selected_index().is_none()
                {
                    self.selected = None;
                }
                Ok(())
            }
            Choice::Ocr(id) if self.ocr.is_none() && windows::ocr::available() => {
                self.start_ocr(id, ui.ctx().clone())
            }
            Choice::Ocr(_) => Ok(()),
            Choice::Send(id) => self.start_lan_transfer(id, false, ui.ctx().clone()),
            Choice::ShowReceived(id) => lan_clipboard::received::show(&self.profile, id),
            Choice::DeleteReceived(id) => {
                self.delete_received = Some(id);
                Ok(())
            }
        })();
        if let Err(error) = result {
            self.status = error.to_string();
        }
    }

    fn lan_busy(&self) -> bool {
        self.lan_transfer.is_some()
            || self
                .lan_job
                .as_ref()
                .is_some_and(|job| job.phase == lan_clipboard::jobs::Phase::Sending)
    }

    fn start_lan_transfer(
        &mut self,
        id: i64,
        retry: bool,
        context: egui::Context,
    ) -> io::Result<()> {
        use std::os::windows::process::CommandExt;
        use windows_sys::Win32::System::Threading::CREATE_NO_WINDOW;

        if self.lan_busy() {
            return Ok(());
        }
        if !self.lan_paired {
            self.status = tr(
                "rust.lan.setup",
                "Pair another computer in Clipboard sharing settings",
            )
            .into();
            return Ok(());
        }
        let child =
            std::process::Command::new(std::env::current_exe()?.with_file_name("typlo-rust.exe"))
                .arg(if retry { "--lan-retry" } else { "--lan-send" })
                .arg(&self.profile)
                .arg(id.to_string())
                .stderr(std::process::Stdio::piped())
                .creation_flags(CREATE_NO_WINDOW)
                .spawn()?;
        let (sender, receiver) = mpsc::sync_channel(1);
        std::thread::spawn(move || {
            let result = child.wait_with_output().and_then(|output| {
                if output.status.success() {
                    return Ok(());
                }
                let error = String::from_utf8_lossy(&output.stderr);
                let error = error
                    .trim()
                    .strip_prefix("Typlo Rust: ")
                    .unwrap_or(error.trim());
                Err(io::Error::other(if error.is_empty() {
                    "Clipboard transfer failed"
                } else {
                    error
                }))
            });
            let _ = sender.send(result);
            context.request_repaint();
        });
        self.lan_transfer = Some(receiver);
        self.status.clear();
        Ok(())
    }

    fn poll_lan_transfer(&mut self, ctx: &egui::Context) {
        if let Some(receiver) = &self.lan_transfer {
            match receiver.try_recv() {
                Err(mpsc::TryRecvError::Empty) => {}
                result => {
                    self.lan_transfer = None;
                    if let Ok(Err(error)) = result {
                        self.status = error.to_string();
                    }
                    self.lan_check = Instant::now() - Duration::from_secs(1);
                }
            }
        }
        if (self.lan_paired || self.lan_job.is_some())
            && self.lan_check.elapsed() >= Duration::from_millis(200)
        {
            self.lan_check = Instant::now();
            if let Ok(job) = lan_clipboard::jobs::status(&self.profile) {
                self.lan_job = job;
            }
        }
        if self.lan_busy() {
            ctx.request_repaint_after(Duration::from_millis(200));
        }
    }

    fn lan_footer(&mut self, ui: &mut egui::Ui) {
        use lan_clipboard::jobs::Phase;
        let Some(job) = self.lan_job.clone() else {
            if self.lan_transfer.is_some() {
                ui.label(tr("rust.lan.sending", "Sending…"));
            }
            return;
        };
        match job.phase {
            Phase::Sending => {
                ui.horizontal(|ui| {
                    if job.total == 0 {
                        ui.spinner();
                        ui.label(tr("rust.lan.sending", "Sending…"));
                    } else {
                        let fraction = job.done as f32 / job.total as f32;
                        let label = if job.done >= job.total {
                            tr("rust.lan.waiting", "Waiting for confirmation…").to_owned()
                        } else {
                            format!("{}%", (fraction * 100.) as u32)
                        };
                        ui.add(
                            egui::ProgressBar::new(fraction)
                                .desired_width(195.)
                                .text(label),
                        );
                    }
                    if ui.small_button(tr("cancel", "Cancel")).clicked()
                        && let Err(error) = lan_clipboard::jobs::cancel(&self.profile, &job)
                    {
                        self.status = error.to_string();
                    }
                });
            }
            Phase::Delivered => {
                ui.label(tr("rust.lan.sent", "Saved to the other computer’s history"));
            }
            Phase::Failed | Phase::Cancelled => {
                let label = if job.phase == Phase::Cancelled {
                    tr(
                        "rust.lan.cancelled",
                        "Cancelled. Retry checks if it was already received.",
                    )
                } else {
                    &job.error
                };
                ui.label(label);
                if ui
                    .add_enabled(
                        !self.lan_busy() && self.lan_paired,
                        egui::Button::new(tr("rust.lan.retry", "Retry")),
                    )
                    .clicked()
                    && let Err(error) = self.start_lan_transfer(job.entry, true, ui.ctx().clone())
                {
                    self.status = error.to_string();
                }
            }
        }
    }

    fn confirm_received_delete(&mut self, ui: &egui::Ui) {
        let Some(id) = self.delete_received else {
            return;
        };
        egui::Modal::new(ui.id().with("delete-received")).show(ui.ctx(), |ui| {
            ui.heading(tr("rust.lan.deleteReceived", "Delete entry and files…"));
            ui.label(tr(
                "rust.lan.confirmDelete",
                "The files from this transfer will be permanently removed from this computer.",
            ));
            ui.horizontal(|ui| {
                if ui.button(tr("cancel", "Cancel")).clicked() {
                    self.delete_received = None;
                }
                if ui.button(tr("history.ctx.delete", "Delete")).clicked() {
                    let (sender, receiver) = mpsc::sync_channel(1);
                    let profile = self.profile.clone();
                    let ctx = ui.ctx().clone();
                    std::thread::spawn(move || {
                        let _ = sender.send(lan_clipboard::received::delete(&profile, id));
                        ctx.request_repaint();
                    });
                    self.delete_received = None;
                    self.deleting_received = Some((id, receiver));
                }
            });
        });
    }

    fn edit_dialog(&mut self, ui: &egui::Ui) {
        let Some(edit) = &mut self.edit else { return };
        let mut save = false;
        let mut cancel = false;
        let dialog = egui::Modal::new(ui.id().with("edit-clipboard-item")).show(ui.ctx(), |ui| {
            ui.set_width(310.);
            ui.heading(if edit.kind == Kind::Text {
                tr("rust.history.editText", "Edit clipboard text")
            } else {
                tr("rust.history.caption", "Attachment caption")
            });
            ui.add(
                crate::appearance::text_area(&mut edit.text)
                    .desired_rows(5)
                    .desired_width(f32::INFINITY),
            );
            if !edit.error.is_empty() {
                ui.label(&edit.error);
            }
            ui.horizontal(|ui| {
                save = ui.button(tr("settings.save", "Save")).clicked();
                cancel = ui.button(tr("settings.cancel", "Cancel")).clicked();
            });
        });
        if cancel || dialog.should_close() {
            self.edit = None;
        } else if save {
            let limits = Limits::from_config(&self.config);
            match self
                .store
                .edit_text(edit.id, &edit.original, &edit.text, limits)
            {
                Ok(()) => {
                    self.edit = None;
                    if let Err(error) = self.reload() {
                        self.status = error.to_string();
                    }
                    self.filter(ui.ctx());
                }
                Err(error) => edit.error = error.to_string(),
            }
        }
    }

    fn poll_received_delete(&mut self, ctx: &egui::Context) {
        let Some((id, receiver)) = &self.deleting_received else {
            return;
        };
        let id = *id;
        let result = match receiver.try_recv() {
            Err(mpsc::TryRecvError::Empty) => return,
            Err(mpsc::TryRecvError::Disconnected) => {
                Err(io::Error::other("File deletion was interrupted"))
            }
            Ok(result) => result,
        };
        self.deleting_received = None;
        self.status = match result.and_then(|()| self.reload()) {
            Ok(()) => {
                if self.selected == Some(id) {
                    self.selected = None;
                }
                self.entries.retain(|entry| entry.id != id);
                self.filter(ctx);
                self.refresh_received_size(ctx);
                tr("rust.lan.deleted", "Received files removed").into()
            }
            Err(error) => error.to_string(),
        };
    }

    fn poll_legacy_size(&mut self, ctx: &egui::Context) {
        if let Some((id, receiver)) = &self.legacy_size_job {
            match receiver.try_recv() {
                Err(mpsc::TryRecvError::Empty) => return,
                result => self.legacy_size = Some((*id, result.ok().and_then(Result::ok))),
            }
            self.legacy_size_job = None;
        }
        if let Some(id) = self.selected
            && self.legacy_size.is_none_or(|(done, _)| done != id)
            && self.selected_index().is_some_and(|index| {
                let entry = &self.items()[index];
                entry.received_bytes.is_none() && received_file(entry)
            })
        {
            let profile = self.profile.clone();
            let ctx = ctx.clone();
            let (sender, receiver) = mpsc::sync_channel(1);
            std::thread::spawn(move || {
                let _ = sender.send(lan_clipboard::received::bytes(&profile, id));
                ctx.request_repaint();
            });
            self.legacy_size_job = Some((id, receiver));
        }
    }

    fn close(&mut self, ui: &egui::Ui) {
        if self.ocr.is_some() || self.deleting_received.is_some() {
            self.focus_seen = true;
            ui.ctx()
                .send_viewport_cmd(egui::ViewportCommand::Visible(false));
        } else {
            self.closing = true;
            ui.ctx().send_viewport_cmd(egui::ViewportCommand::Close);
        }
    }

    fn request_thumbnail(&mut self, id: i64, ctx: &egui::Context) {
        if self.thumbnails.contains_key(&id) || self.thumbnail_job.is_some() {
            return;
        }
        let open_reader = self.store.background_reader(&self.profile);
        let ctx = ctx.clone();
        let (send, reply) = mpsc::sync_channel(1);
        std::thread::spawn(move || {
            let thumbnail = open_reader()
                .ok()
                .and_then(|store| store.image_preview(id).ok().flatten());
            let _ = send.send(thumbnail);
            ctx.request_repaint();
        });
        self.thumbnail_job = Some(ThumbnailJob { id, reply });
    }

    fn poll_thumbnail(&mut self, ctx: &egui::Context) {
        let Some(job) = self.thumbnail_job.as_ref() else {
            return;
        };
        let thumbnail = match job.reply.try_recv() {
            Ok(value) => value,
            Err(mpsc::TryRecvError::Empty) => return,
            Err(mpsc::TryRecvError::Disconnected) => None,
        };
        let id = job.id;
        self.thumbnail_job = None;
        if self.thumbnail_order.len() == THUMBNAIL_CACHE_SIZE
            && let Some(oldest) = self.thumbnail_order.pop_front()
        {
            self.thumbnails.remove(&oldest);
        }
        self.thumbnail_order.push_back(id);
        self.thumbnails.insert(
            id,
            thumbnail.map(|(image, dimensions)| Thumbnail {
                image: texture(ctx, &image),
                dimensions,
            }),
        );
    }

    fn poll_ocr(&mut self, ui: &egui::Ui, window: HWND) {
        let Some(job) = &self.ocr else {
            return;
        };
        let result = match job.reply.try_recv() {
            Ok(result) => result,
            Err(mpsc::TryRecvError::Empty) => return,
            Err(mpsc::TryRecvError::Disconnected) => Err(io::Error::other("OCR was interrupted")),
        };
        let safe_copy = windows::clipio::sequence() == job.clipboard;
        self.ocr = None;
        self.status = match result {
            Ok(text) if text.trim().is_empty() => tr("history.ocrNone", "No text found").into(),
            Ok(text) => {
                let clip = Clip::from_text(&text);
                let limits = Limits::from_config(&self.config);
                let result = self
                    .store
                    .store(&clip, limits)
                    .and_then(|()| {
                        if safe_copy {
                            windows::clipio::publish(window, &clip, true).map(|_| ())
                        } else {
                            Ok(())
                        }
                    })
                    .and_then(|()| self.reload());
                self.filter(ui.ctx());
                match result {
                    Ok(()) if safe_copy => tr("history.ocrDone", "Text copied").into(),
                    Ok(()) => {
                        tr("rust.history.ocrSaved", "Recognized text saved in history").into()
                    }
                    Err(error) => error.to_string(),
                }
            }
            Err(error) => error.to_string(),
        };
    }

    fn start_ocr(&mut self, id: i64, context: egui::Context) -> io::Result<()> {
        let profile = self.profile.clone();
        let clipboard = windows::clipio::sequence();
        let (sender, reply) = mpsc::sync_channel(1);
        std::thread::Builder::new()
            .name("typlo-ocr".into())
            .spawn(move || {
                let result = History::open(&profile)
                    .and_then(|store| store.load(id))
                    .and_then(|clip| clip.ok_or_else(missing)?.image())
                    .and_then(|image| windows::ocr::recognize(image.into_rgba8()));
                let _ = sender.send(result);
                context.request_repaint();
            })?;
        self.ocr = Some(Ocr { reply, clipboard });
        self.status = tr("history.ocrBusy", "Recognizing text…").into();
        Ok(())
    }
}

struct Palette {
    panel: Color32,
    border: Color32,
    search: Color32,
    focus: Color32,
    accent: Color32,
    hover: Color32,
    selected: Color32,
    badge: Color32,
    muted: Color32,
}

impl Palette {
    fn new(dark: bool) -> Self {
        let [
            panel,
            border,
            search,
            focus,
            accent,
            hover,
            selected,
            badge,
            muted,
        ] = if dark {
            [
                0x1e1f24, 0x4b4c50, 0x303137, 0x28292e, 0x7da8ff, 0x303137, 0x25385a, 0x38393e,
                0x9a9a9d,
            ]
        } else {
            [
                0xfafafc, 0xd9d9db, 0xeeeeef, 0xffffff, 0x2563eb, 0xebebed, 0xd9e6fb, 0xe6e6e8,
                0x66666a,
            ]
        }
        .map(appearance::rgb);
        Self {
            panel,
            border,
            search,
            focus,
            accent,
            hover,
            selected,
            badge,
            muted,
        }
    }
}

fn received_file(entry: &Entry) -> bool {
    entry.kind == Kind::Files
        && (entry.received_bytes.is_some() || entry.source.as_deref() == Some("Typlo LAN"))
}

fn file_size(bytes: u64) -> String {
    match bytes {
        0..1024 => format!("{bytes} B"),
        1024..1_048_576 => format!("{:.1} KiB", bytes as f64 / 1024.),
        _ => format!("{:.1} MiB", bytes as f64 / 1_048_576.),
    }
}

fn selection_index(entries: &[Entry], selected: Option<i64>) -> Option<usize> {
    let id = selected?;
    entries.iter().position(|entry| entry.id == id)
}

fn entry_matches(entry: &Entry, query: &str) -> bool {
    !entry.unreadable
        && (entry.preview.to_lowercase().contains(query)
            || entry
                .source
                .as_ref()
                .is_some_and(|source| source.to_lowercase().contains(query)))
}

fn relative_age(used_at: i64) -> String {
    let now = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs() as i64;
    let age = now.saturating_sub(used_at).max(0);
    if age < 60 {
        tr("history.justNow", "Just now").to_owned()
    } else {
        let (label, number) = if age < 3600 {
            (tr("history.minAgo", "{0} min ago"), age / 60)
        } else if age < 86400 {
            (tr("history.hAgo", "{0} h ago"), age / 3600)
        } else {
            (tr("history.dAgo", "{0} d ago"), age / 86400)
        };
        label.replace("{0}", &number.to_string())
    }
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

fn missing() -> io::Error {
    io::Error::other(tr("history.notFound", "This item is no longer in history"))
}

fn window(window: &impl HasWindowHandle) -> HWND {
    match window.window_handle().ok().map(|handle| handle.as_raw()) {
        Some(RawWindowHandle::Win32(handle)) => handle.hwnd.get() as HWND,
        _ => std::ptr::null_mut(),
    }
}

fn place(window: HWND, cursor: POINT, size: [i32; 2], center: bool) {
    if window.is_null() {
        return;
    }
    let monitor = unsafe { MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST) };
    let mut info = MONITORINFO {
        cbSize: size_of::<MONITORINFO>() as u32,
        ..Default::default()
    };
    if unsafe { GetMonitorInfoW(monitor, &mut info) } == 0 {
        return;
    }
    // Move the still-hidden window onto the target display before asking for its DPI.
    let flags = SWP_NOZORDER | SWP_NOACTIVATE;
    unsafe {
        SetWindowPos(
            window,
            std::ptr::null_mut(),
            cursor.x,
            cursor.y,
            0,
            0,
            flags | SWP_NOSIZE,
        );
    }
    let dpi = unsafe { GetDpiForWindow(window) }.max(96) as i32;
    let [width, height] = size.map(|pixels| pixels * dpi / 96);
    let area = info.rcWork;
    let width = width.min((area.right - area.left - 16).max(1));
    let height = height.min((area.bottom - area.top - 16).max(1));
    let [x, y] = if center {
        [
            (area.left + area.right - width) / 2,
            (area.top + area.bottom - height) / 2,
        ]
    } else {
        popup_position(cursor, area, width, height)
    };
    unsafe {
        SetWindowPos(window, std::ptr::null_mut(), x, y, width, height, flags);
    }
}

fn popup_position(cursor: POINT, area: RECT, width: i32, height: i32) -> [i32; 2] {
    let y = if cursor.y + 12 + height > area.bottom - 8 {
        cursor.y - height - 12
    } else {
        cursor.y + 12
    };
    [
        (cursor.x + 12).clamp(area.left + 8, (area.right - width - 8).max(area.left + 8)),
        y.clamp(area.top + 8, (area.bottom - height - 8).max(area.top + 8)),
    ]
}

fn history_search_input(query: &mut String) -> egui::TextEdit<'_> {
    // Enter belongs to the selected history row, even while search has focus.
    egui::TextEdit::singleline(query).return_key(None)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn focused_search_leaves_both_paste_shortcuts_for_history() {
        use egui::{Event, Key, Modifiers, RawInput};

        let ctx = egui::Context::default();
        let mut query = String::from("fixture");
        let field = egui::Id::new("history-search-test");
        let _ = ctx.run_ui(RawInput::default(), |ui| {
            ui.add(history_search_input(&mut query).id(field))
                .request_focus();
        });
        for modifiers in [Modifiers::NONE, Modifiers::SHIFT] {
            let input = RawInput {
                modifiers,
                events: vec![Event::Key {
                    key: Key::Enter,
                    physical_key: Some(Key::Enter),
                    pressed: true,
                    repeat: false,
                    modifiers,
                }],
                ..Default::default()
            };
            let _ = ctx.run_ui(input, |ui| {
                assert!(
                    ui.add(history_search_input(&mut query).id(field))
                        .has_focus()
                );
                assert!(ui.input_mut(|input| input.consume_key(modifiers, Key::Enter)));
            });
        }
        assert_eq!(query, "fixture");
    }

    fn search_limits() -> Limits {
        Limits {
            max_items: 2000,
            max_total: 8 * 1024 * 1024,
            max_image: 1024,
            max_text: 32 * 1024,
        }
    }

    #[test]
    fn search_finds_text_past_preview_and_refreshes_after_external_writes() {
        let profile = tempfile::tempdir().unwrap();
        let mut writer = History::open(profile.path()).unwrap();
        writer
            .store(
                &Clip::from_text(&format!("{} удалённый хвост", "x".repeat(410))),
                search_limits(),
            )
            .unwrap();
        let reader = writer.background_reader(profile.path())().unwrap();
        let mut cache = SearchCache::default();
        let find = |cache: &mut SearchCache| {
            search_history(&reader, cache, "хвост", false, "image", || false)
                .unwrap()
                .entries
        };
        let id = find(&mut cache)[0].id;
        assert!(cache.version.is_some());
        assert_eq!(find(&mut cache)[0].id, id);
        writer.remove(id).unwrap();
        writer
            .store(&Clip::from_text("Другой хвост"), search_limits())
            .unwrap();
        let results = find(&mut cache);
        assert_eq!(results.len(), 1);
        assert_ne!(results[0].id, id);
        let error = search_history(&reader, &mut cache, "хвост", false, "image", || true)
            .err()
            .unwrap();
        assert_eq!(error.kind(), io::ErrorKind::Interrupted);
    }

    #[test]
    fn search_skips_a_corrupt_preview_and_reports_it() {
        let profile = tempfile::tempdir().unwrap();
        let mut writer = History::open(profile.path()).unwrap();
        writer
            .store(&Clip::from_text("one broken needle"), search_limits())
            .unwrap();
        writer
            .store(&Clip::from_text("one valid needle"), search_limits())
            .unwrap();
        let broken = writer.search("broken", 0).unwrap()[0].id;
        let db = rusqlite::Connection::open(profile.path().join("history.sqlite3")).unwrap();
        db.execute("UPDATE clips SET preview=x'FF' WHERE id=?1", [broken])
            .unwrap();
        let mut cache = SearchCache::default();
        let result =
            search_history(&writer, &mut cache, "needle", false, "image", || false).unwrap();
        assert_eq!(result.entries.len(), 1);
        assert_eq!(result.entries[0].preview, "one valid needle");
        assert_eq!(result.unreadable, 1);
        let page = writer.list(0).unwrap();
        assert_eq!(page.len(), 2);
        assert_eq!(page[1].id, broken);
        assert!(page[1].unreadable);
    }

    #[test]
    fn search_reuses_unchanged_rows_after_copy_and_updates_pin_order() {
        let profile = tempfile::tempdir().unwrap();
        let mut writer = History::open(profile.path()).unwrap();
        writer
            .store(&Clip::from_text("first needle"), search_limits())
            .unwrap();
        let reader = writer.background_reader(profile.path())().unwrap();
        let mut cache = SearchCache::default();
        let find = |cache: &mut SearchCache| {
            search_history(&reader, cache, "needle", false, "image", || false).unwrap()
        };
        let id = find(&mut cache).entries[0].id;
        let original_allocation = cache.rows[0].preview.as_ptr();
        writer
            .store(&Clip::from_text("second needle"), search_limits())
            .unwrap();
        let found = find(&mut cache);
        assert_eq!(found.entries.len(), 2);
        assert_eq!(
            cache
                .rows
                .iter()
                .find(|row| row.entry.id == id)
                .unwrap()
                .preview
                .as_ptr(),
            original_allocation
        );
        writer.pin(id, true).unwrap();
        let found = find(&mut cache);
        assert_eq!(found.entries[0].id, id);
        assert!(found.entries[0].pinned);
    }

    #[test]
    fn search_keeps_a_partial_cache_and_finds_uncached_long_entries() {
        let profile = tempfile::tempdir().unwrap();
        let mut writer = History::open(profile.path()).unwrap();
        let body = "x".repeat(14_000);
        for i in 0..160 {
            let tail = if i == 0 { " unique-end" } else { "" };
            writer
                .store(
                    &Clip::from_text(&format!("clip {i} {body}{tail}")),
                    search_limits(),
                )
                .unwrap();
        }
        let reader = writer.background_reader(profile.path())().unwrap();
        let mut cache = SearchCache::default();
        for _ in 0..2 {
            let result =
                search_history(&reader, &mut cache, "unique-end", false, "image", || false)
                    .unwrap();
            assert_eq!(result.entries.len(), 1);
            assert!(result.entries[0].preview.starts_with("clip 0 "));
            assert!(!cache.complete);
            assert!(!cache.rows.is_empty());
            assert!(cache.rows.iter().map(SearchEntry::bytes).sum::<usize>() <= 2 * 1024 * 1024);
        }
    }

    #[test]
    fn rapid_search_keystrokes_deliver_only_the_latest_query() {
        let profile = tempfile::tempdir().unwrap();
        let mut writer = History::open(profile.path()).unwrap();
        writer
            .store(&Clip::from_text("previous query result"), search_limits())
            .unwrap();
        writer
            .store(&Clip::from_text("latest query result"), search_limits())
            .unwrap();
        let worker = start_history_search(
            writer.background_reader(profile.path()),
            egui::Context::default(),
        )
        .unwrap();
        let submit = |query: &str| {
            let cancel = Arc::new(AtomicBool::new(false));
            let (reply, result) = mpsc::sync_channel(1);
            assert!(
                worker
                    .send(SearchRequest {
                        query: query.into(),
                        received_only: false,
                        image_terms: "image".into(),
                        cancel: Arc::clone(&cancel),
                        reply,
                    })
                    .is_ok()
            );
            (cancel, result)
        };
        let (obsolete, obsolete_reply) = submit("previous");
        obsolete.store(true, Ordering::Release);
        let (_, latest_reply) = submit("latest");
        let result = latest_reply
            .recv_timeout(Duration::from_secs(5))
            .unwrap()
            .unwrap();
        assert_eq!(result.entries.len(), 1);
        assert_eq!(result.entries[0].preview, "latest query result");
        assert!(matches!(
            obsolete_reply.try_recv(),
            Err(mpsc::TryRecvError::Disconnected)
        ));
    }

    #[test]
    #[ignore = "Measures DPAPI and history lookup on a disposable 1500-entry profile"]
    fn measure_large_clipboard_search() {
        let profile = tempfile::tempdir().unwrap();
        let mut writer = History::open(profile.path()).unwrap();
        for i in 0..1500 {
            let suffix = if i == 7 { " unique-needle" } else { "" };
            writer
                .store(
                    &Clip::from_text(&format!("entry {i} {suffix}")),
                    search_limits(),
                )
                .unwrap();
        }
        let reader = writer.background_reader(profile.path())().unwrap();
        let query = "unique-needle";
        let mut cache = SearchCache::default();
        let mut old = Vec::new();
        let mut cold = Vec::new();
        let mut warm = Vec::new();
        let mut refreshed = Vec::new();
        for _ in 0..5 {
            let started = Instant::now();
            assert_eq!(reader.search(query, 0).unwrap().len(), 1);
            old.push(started.elapsed().as_secs_f64() * 1000.);
            cache = SearchCache::default();
            let started = Instant::now();
            assert_eq!(
                search_history(&reader, &mut cache, query, false, "image", || false)
                    .unwrap()
                    .entries
                    .len(),
                1
            );
            cold.push(started.elapsed().as_secs_f64() * 1000.);
        }
        for _ in 0..200 {
            let started = Instant::now();
            assert_eq!(
                search_history(&reader, &mut cache, query, false, "image", || false)
                    .unwrap()
                    .entries
                    .len(),
                1
            );
            warm.push(started.elapsed().as_secs_f64() * 1000.);
        }
        for i in 0..20 {
            writer
                .store(&Clip::from_text(&format!("new clip {i}")), search_limits())
                .unwrap();
            let started = Instant::now();
            assert_eq!(
                search_history(&reader, &mut cache, query, false, "image", || false)
                    .unwrap()
                    .entries
                    .len(),
                1
            );
            refreshed.push(started.elapsed().as_secs_f64() * 1000.);
        }
        for (label, mut times) in [
            ("previous pages", old),
            ("cold cursor", cold),
            ("warm cache", warm),
            ("after copy", refreshed),
        ] {
            times.sort_by(f64::total_cmp);
            println!(
                "{label}: n={} P50={:.3} ms P95={:.3} ms",
                times.len(),
                times[times.len() / 2],
                times[(times.len() * 95).div_ceil(100) - 1]
            );
        }
    }

    #[test]
    fn keyboard_selection_follows_the_entry_id_and_clears_if_it_is_missing() {
        let entry = |id| Entry {
            id,
            kind: Kind::Text,
            pinned: false,
            preview: String::new(),
            bytes: 0,
            source: None,
            used_at: None,
            files: None,
            formatted: false,
            received_bytes: None,
            unreadable: false,
        };
        assert_eq!(selection_index(&[entry(1), entry(2)], Some(2)), Some(1));
        assert_eq!(
            selection_index(&[entry(3), entry(1), entry(2)], Some(2)),
            Some(2)
        );
        assert_eq!(selection_index(&[entry(3), entry(1)], Some(2)), None);
        assert_eq!(selection_index(&[entry(1)], None), None);
    }

    #[test]
    fn history_uses_cursor_monitor_work_area_and_flips_above_at_the_bottom() {
        let area = RECT {
            left: -1920,
            top: -100,
            right: 0,
            bottom: 940,
        };
        assert_eq!(
            popup_position(POINT { x: -1800, y: 10 }, area, 460, 560),
            [-1788, 22]
        );
        assert_eq!(
            popup_position(POINT { x: -50, y: 910 }, area, 460, 560),
            [-468, 338]
        );
        assert_eq!(
            popup_position(POINT { x: -1900, y: 410 }, area, 460, 560),
            [-1888, -92]
        );
    }
}
