#![cfg_attr(windows, windows_subsystem = "windows")]

use typlo_rust::locale::text as tr;

use eframe::egui::{self, Color32, Frame, Margin, RichText, Ui};
use std::path::PathBuf;
use typlo_rust::{
    backup::Backup,
    config::{self, Config},
    dictionary_view::DictionaryView,
    layout::Language,
    locale,
};

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let mut args = std::env::args_os().skip(1);
    let mode = args.next();
    #[cfg(unix)]
    if mode
        .as_deref()
        .is_some_and(|mode| mode == "--layout-choice-preview")
    {
        let profile = args
            .next()
            .map(PathBuf::from)
            .unwrap_or_else(config::profile);
        return typlo_rust::selection_tools_view::run_preview(profile);
    }
    #[cfg(any(windows, target_os = "linux", target_os = "macos"))]
    if mode
        .as_deref()
        .is_some_and(|mode| mode == "--selection-tools")
    {
        let profile = args
            .next()
            .map(PathBuf::from)
            .unwrap_or_else(config::profile);
        return typlo_rust::selection_tools_view::run(profile);
    }
    #[cfg(any(target_os = "linux", target_os = "macos"))]
    if mode.as_deref().is_some_and(|mode| mode == "--history") {
        let profile = args
            .next()
            .map(PathBuf::from)
            .unwrap_or_else(config::profile);
        let mut origin = [0; 2];
        for field in &mut origin {
            *field = args
                .next()
                .and_then(|arg| arg.to_str()?.parse().ok())
                .unwrap_or(0);
        }
        let origin = (origin[0] != 0 && origin[1] != 0).then_some(origin);
        return typlo_rust::unix_history_view::run(profile, origin);
    }
    #[cfg(windows)]
    if let Some(mode) = mode.as_deref() {
        if mode == "--ocr-smoke" {
            let input = args
                .next()
                .ok_or_else(|| std::io::Error::other("Missing input PNG"))?;
            let output = args
                .next()
                .ok_or_else(|| std::io::Error::other("Missing OCR report path"))?;
            let image = image::open(input)?.into_rgba8();
            return std::fs::write(output, typlo_rust::windows::ocr::recognize(image)?)
                .map_err(Into::into);
        }
        if mode == "--history" {
            let profile = args
                .next()
                .map(PathBuf::from)
                .unwrap_or_else(config::profile);
            let mut origin = [0; 3];
            for field in &mut origin {
                *field = args
                    .next()
                    .and_then(|arg| arg.to_string_lossy().parse().ok())
                    .unwrap_or(0);
            }
            let mut coordinate = || args.next().and_then(|value| value.to_str()?.parse().ok());
            let anchor = coordinate().zip(coordinate()).map(|(x, y)| [x, y]);
            return typlo_rust::history_view::run(profile, origin, anchor);
        }
        if mode == "--capture" {
            let profile = args
                .next()
                .map(PathBuf::from)
                .unwrap_or_else(config::profile);
            let destination = args.next().unwrap_or_default();
            let shape = if args.next().is_some_and(|arg| arg == "free") {
                config::CaptureShape::Free
            } else {
                config::CaptureShape::Rectangle
            };
            return typlo_rust::capture_view::run_capture(
                profile,
                &destination.to_string_lossy(),
                shape,
            );
        }
        if mode == "--screenshot-editor" {
            let profile = args
                .next()
                .map(PathBuf::from)
                .unwrap_or_else(config::profile);
            let for_save = args.next().is_some_and(|arg| arg == "file");
            return typlo_rust::capture_view::run_editor(&profile, for_save);
        }
    }
    if mode
        .as_deref()
        .is_some_and(|arg| arg == "--spelling-review")
    {
        let profile = args
            .next()
            .map(PathBuf::from)
            .unwrap_or_else(config::profile);
        return typlo_rust::spelling_view::run(profile);
    }
    let page = match mode.as_deref().and_then(|arg| arg.to_str()) {
        Some("--open-updates") => Page::Update,
        Some("--shortcuts") => Page::Shortcuts,
        Some("--tools") => Page::Tools,
        Some("--dictionary") => Page::Dictionary,
        _ => Page::Settings,
    };
    let report = (mode.as_deref().is_some_and(|arg| arg == "--ui-probe"))
        .then(|| args.next().map(PathBuf::from))
        .flatten();
    let profile = args
        .next()
        .map(PathBuf::from)
        .unwrap_or_else(config::profile);
    let report = report.map(|report| UiProbe {
        report,
        frame: args.next().map(PathBuf::from),
        requested: false,
    });
    let workspace = Workspace::new(profile, report, page);
    let options = eframe::NativeOptions {
        viewport: typlo_rust::appearance::window()
            .with_inner_size(page.size())
            .with_min_inner_size(if page == Page::Settings {
                [560., 500.]
            } else {
                [440., 360.]
            }),
        centered: true,
        renderer: eframe::Renderer::Glow,
        ..Default::default()
    };
    eframe::run_native(
        page.title(),
        options,
        Box::new(move |creation| {
            #[cfg(windows)]
            if page == Page::Settings {
                use raw_window_handle::{HasWindowHandle, RawWindowHandle};
                if let Ok(handle) = creation.window_handle()
                    && let RawWindowHandle::Win32(handle) = handle.as_raw()
                {
                    typlo_rust::windows::mark_settings_window(handle.hwnd.get() as usize);
                }
            }
            typlo_rust::appearance::configure(&creation.egui_ctx);
            locale::theme(&creation.egui_ctx, workspace.settings.ui_theme);
            locale::fonts(&creation.egui_ctx, &workspace.settings.ui_language);
            Ok(Box::new(workspace))
        }),
    )?;
    Ok(())
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum Page {
    Settings,
    Shortcuts,
    Dictionary,
    Tools,
    Update,
}

impl Page {
    fn title(self) -> &'static str {
        match self {
            Self::Settings => tr("settings.title", "Typlo2.0 — settings"),
            Self::Shortcuts => tr("shortcuts.title", "Typlo2.0 — keyboard shortcuts"),
            Self::Dictionary => tr("manage.title", "Personal dictionary"),
            Self::Tools => tr("tools.title", "Typlo tools"),
            Self::Update => tr("update.dialogTitle", "Typlo updates"),
        }
    }

    fn size(self) -> [f32; 2] {
        match self {
            Self::Settings => [680., 800.],
            Self::Shortcuts => [560., 640.],
            Self::Tools => [850., 670.],
            Self::Dictionary => [680., 650.],
            Self::Update => [510., 380.],
        }
    }
}

struct Workspace {
    page: Page,
    settings: Config,
    profile: PathBuf,
    status: String,
    report: Option<UiProbe>,
    #[cfg(windows)]
    application_picker: Option<ApplicationPicker>,
    dictionary: DictionaryView,
    #[cfg(not(windows))]
    backup_path: String,
    pending_backup: Option<Backup>,
    #[cfg(any(windows, target_os = "linux", target_os = "macos"))]
    clear_history: bool,
    #[cfg(any(windows, target_os = "linux", target_os = "macos"))]
    history_status: HistoryStatus,
    #[cfg(windows)]
    lan_clipboard: typlo_rust::lan_clipboard_view::Settings,
    #[cfg(windows)]
    resident_memory: Option<(usize, usize)>,
    #[cfg(windows)]
    diagnostics: Option<typlo_rust::windows::DiagnosticReport>,
    #[cfg(any(windows, target_os = "linux", target_os = "macos"))]
    updates: typlo_rust::update_view::UpdateView,
}

#[cfg(any(windows, target_os = "linux", target_os = "macos"))]
#[derive(Default)]
struct HistoryStatus {
    value: Option<(usize, u64)>,
    pending: Option<std::sync::mpsc::Receiver<Option<(usize, u64)>>>,
    next_refresh: Option<std::time::Instant>,
}

#[cfg(any(windows, target_os = "linux", target_os = "macos"))]
impl HistoryStatus {
    fn refresh(&mut self, profile: &std::path::Path, ctx: &egui::Context) {
        use std::{
            sync::mpsc,
            time::{Duration, Instant},
        };
        if let Some(pending) = &self.pending {
            match pending.try_recv() {
                Ok(value) => {
                    self.value = value;
                    self.pending = None;
                    ctx.request_repaint();
                }
                Err(mpsc::TryRecvError::Disconnected) => self.pending = None,
                Err(mpsc::TryRecvError::Empty) => return,
            }
        }

        let now = Instant::now();
        if let Some(next) = self.next_refresh.filter(|&next| next > now) {
            ctx.request_repaint_after(next - now);
            return;
        }

        let (send, receive) = mpsc::channel();
        let profile = profile.to_owned();
        let ctx = ctx.clone();
        self.pending = Some(receive);
        self.next_refresh = Some(now + Duration::from_secs(1));
        std::thread::spawn(move || {
            #[cfg(windows)]
            let result = typlo_rust::windows::history::History::stats_for_profile(&profile).ok();
            #[cfg(any(target_os = "linux", target_os = "macos"))]
            let result = typlo_rust::unix_history::History::stats_for_profile(&profile).ok();
            let _ = send.send(result);
            ctx.request_repaint();
        });
    }
}

struct UiProbe {
    report: PathBuf,
    frame: Option<PathBuf>,
    requested: bool,
}

impl UiProbe {
    fn finish(&mut self, ctx: &egui::Context, page: Page) -> bool {
        let mut report = format!("egui Glow frame ready\n{}\n", page.title());
        if let Some(path) = &self.frame {
            if !self.requested {
                ctx.send_viewport_cmd(egui::ViewportCommand::Screenshot(egui::UserData::default()));
                self.requested = true;
                return false;
            }
            let image = ctx.input(|input| {
                input.events.iter().find_map(|event| match event {
                    egui::Event::Screenshot { image, .. } => Some(image.clone()),
                    _ => None,
                })
            });
            let Some(image) = image else { return false };
            let [width, height] = image.size;
            let [red, green, blue, _] = image.pixels[0].to_array();
            let pixels: Vec<_> = image
                .pixels
                .iter()
                .flat_map(|color| color.to_array())
                .collect();
            match image::save_buffer(
                path,
                &pixels,
                width as u32,
                height as u32,
                image::ColorType::Rgba8,
            ) {
                Ok(()) => report.push_str(&format!("background=#{red:02x}{green:02x}{blue:02x}\n")),
                Err(error) => report.push_str(&format!("Frame capture failed: {error}\n")),
            }
        }
        let _ = std::fs::write(&self.report, report);
        ctx.send_viewport_cmd(egui::ViewportCommand::Close);
        true
    }
}

#[cfg(windows)]
struct ApplicationPicker {
    index: usize,
    search: String,
    apps: Vec<(String, String)>,
    selected: Option<String>,
}

impl Workspace {
    fn saved_status(&self, success: &str) -> String {
        #[cfg(any(target_os = "linux", target_os = "macos"))]
        if let Err(error) =
            typlo_rust::unix_desktop::autostart(&self.profile, self.settings.start_with_windows)
        {
            return format!("{success}. Не удалось применить автозапуск. {error}");
        }
        success.into()
    }

    fn new(profile: PathBuf, report: Option<UiProbe>, page: Page) -> Self {
        let (settings, status) = match config::load(&profile) {
            Ok(settings) => (settings, String::new()),
            Err(error) => (Config::default(), error.to_string()),
        };
        locale::set_language(&settings.ui_language);
        let dictionary = DictionaryView::new(&profile);
        #[cfg(windows)]
        let lan_clipboard = typlo_rust::lan_clipboard_view::Settings::new(&profile);
        #[cfg(windows)]
        let diagnostics = matches!(page, Page::Tools | Page::Dictionary)
            .then(|| typlo_rust::windows::resident_diagnostics(&profile, false))
            .and_then(Result::ok);
        Self {
            page,
            settings,
            profile,
            status,
            report,
            dictionary,
            #[cfg(windows)]
            application_picker: None,
            #[cfg(not(windows))]
            backup_path: String::new(),
            pending_backup: None,
            #[cfg(any(windows, target_os = "linux", target_os = "macos"))]
            clear_history: false,
            #[cfg(any(windows, target_os = "linux", target_os = "macos"))]
            history_status: HistoryStatus::default(),
            #[cfg(windows)]
            lan_clipboard,
            #[cfg(windows)]
            resident_memory: (page == Page::Tools)
                .then(typlo_rust::windows::resident_memory)
                .and_then(Result::ok),
            #[cfg(windows)]
            diagnostics,
            #[cfg(any(windows, target_os = "linux", target_os = "macos"))]
            updates: typlo_rust::update_view::UpdateView::new(page == Page::Update),
        }
    }

    fn navigate(&mut self, ui: &Ui, page: Page) {
        self.page = page;
        #[cfg(windows)]
        if matches!(page, Page::Tools | Page::Dictionary) {
            self.refresh_diagnostics(false);
        }
        ui.ctx()
            .send_viewport_cmd(egui::ViewportCommand::Title(page.title().into()));
        ui.ctx()
            .send_viewport_cmd(egui::ViewportCommand::InnerSize(page.size().into()));
    }

    fn preferences(&mut self, ui: &mut Ui) {
        heading(ui, tr("settings.system", "System"));
        let current = config::UI_LANGUAGES
            .iter()
            .position(|&language| language == self.settings.ui_language)
            .unwrap_or(0);
        let mut selected = current;
        setting_row(ui, tr("settings.language", "Interface language"), |ui| {
            egui::ComboBox::from_id_salt("interface-language")
                .width(ui.available_width())
                .selected_text(locale::NAMES[current])
                .show_ui(ui, |ui| {
                    for (index, name) in locale::NAMES.into_iter().enumerate() {
                        ui.selectable_value(&mut selected, index, name);
                    }
                });
        });
        if selected != current {
            self.settings.ui_language = config::UI_LANGUAGES[selected].into();
            locale::set_language(&self.settings.ui_language);
            locale::fonts(ui.ctx(), &self.settings.ui_language);
            ui.ctx()
                .send_viewport_cmd(egui::ViewportCommand::Title(self.page.title().into()));
        }
        let theme_label = |theme| match theme {
            config::UiTheme::System => tr("theme.system", "System"),
            config::UiTheme::Light => tr("theme.light", "Light"),
            config::UiTheme::Dark => tr("theme.dark", "Dark"),
        };
        let theme = self.settings.ui_theme;
        setting_row(ui, tr("settings.theme", "Theme"), |ui| {
            egui::ComboBox::from_id_salt("interface-theme")
                .width(ui.available_width())
                .selected_text(theme_label(theme))
                .show_ui(ui, |ui| {
                    for theme in [
                        config::UiTheme::System,
                        config::UiTheme::Light,
                        config::UiTheme::Dark,
                    ] {
                        ui.selectable_value(&mut self.settings.ui_theme, theme, theme_label(theme));
                    }
                });
        });
        if self.settings.ui_theme != theme {
            locale::theme(ui.ctx(), self.settings.ui_theme);
        }
        ui.checkbox(
            &mut self.settings.start_with_windows,
            tr("settings.autostart", "Add to autostart"),
        );
        ui.checkbox(
            &mut self.settings.keep_screen_on,
            tr("settings.keepAwake", "Prevent screen lock"),
        );
        hint(
            ui,
            tr(
                "settings.keepAwakeHint",
                "Keep the display on while Typlo is running.",
            ),
        );
        self.backup(ui, false);
    }

    fn conversion_options(&mut self, ui: &mut Ui) {
        ui.checkbox(
            &mut self.settings.convert_last_word,
            tr(
                "settings.lastWord",
                "When nothing is selected, convert only recent input",
            ),
        );
        ui.checkbox(
            &mut self.settings.protect_technical,
            tr("tools.protect", "Protect technical input"),
        );
        ui.checkbox(
            &mut self.settings.snippets_enabled,
            tr("tools.snippets", "Expand snippets"),
        );
        ui.checkbox(
            &mut self.settings.auto_correct,
            tr("auto.enabled", "Automatic layout correction"),
        );
        hint(
            ui,
            tr(
                "auto.hint",
                "Automatically correct a word when you type a space.",
            ),
        );
        if ui.button(tr("tools.title", "Typlo tools")).clicked() {
            self.navigate(ui, Page::Tools);
        }
        if ui
            .button(tr("manage.title", "Personal dictionary"))
            .clicked()
        {
            self.navigate(ui, Page::Dictionary);
        }
        ui.checkbox(
            &mut self.settings.switch_layout,
            tr(
                "settings.switchLayout",
                "Switch the keyboard layout after converting",
            ),
        );
        ui.horizontal(|ui| {
            heading(ui, tr("settings.cycleHeader", "Layout cycle"));
            if ui
                .button(tr("settings.refreshLayouts", "Refresh layouts"))
                .clicked()
            {
                let layouts = config::available_layouts();
                if self.settings.cycle.is_empty() {
                    self.settings.cycle = layouts;
                }
            }
        });
        hint(
            ui,
            tr(
                "settings.cycleHint",
                "Choose the keyboard layouts and their order.",
            ),
        );
        cycle_controls(ui, &mut self.settings.cycle, 1);
    }

    fn applications(&mut self, ui: &mut Ui) {
        heading(ui, tr("appLayouts.title", "Application layouts"));
        hint(
            ui,
            tr(
                "appLayouts.hint",
                "Choose a layout cycle for each application.",
            ),
        );
        let layouts = available_choices();
        let mut remove = None;
        #[cfg(windows)]
        let mut choose = None;
        for (index, rule) in self.settings.applications.iter_mut().enumerate() {
            let border = ui.visuals().window_stroke;
            Frame::new()
                .stroke(border)
                .corner_radius(6)
                .inner_margin(10.)
                .show(ui, |ui| {
                    ui.push_id(index, |ui| {
                        ui.horizontal(|ui| {
                            ui.add(
                                typlo_rust::appearance::text_input(&mut rule.process)
                                    .desired_width((ui.available_width() - 112.).max(100.))
                                    .hint_text(tr("appLayouts.process", "Process name")),
                            );
                            #[cfg(windows)]
                            if ui.button(tr("appLayouts.choose", "Choose…")).clicked() {
                                choose = Some(index);
                            }
                            if ui.button("✕").clicked() {
                                remove = Some(index);
                            }
                        });
                        ui.checkbox(&mut rule.enabled, tr("appLayouts.enabled", "Enabled"));
                        tri_state(
                            ui,
                            &mut rule.auto_correct,
                            tr("tools.profileAuto", "Auto-correction"),
                        );
                        tri_state(
                            ui,
                            &mut rule.snippets,
                            tr("tools.profileSnippets", "Snippets"),
                        );
                        let mut order: Vec<_> = rule
                            .cycle
                            .iter()
                            .copied()
                            .filter(|layout| !cfg!(windows) || layouts.contains(layout))
                            .collect();
                        let selected_count = order.len();
                        for &layout in &layouts {
                            if !order.contains(&layout) {
                                order.push(layout);
                            }
                        }
                        let mut reorder = None;
                        for (position, layout) in order.iter().copied().enumerate() {
                            ui.horizontal(|ui| {
                                let mut enabled = rule.cycle.contains(&layout);
                                if ui.checkbox(&mut enabled, layout.name()).changed() {
                                    if enabled {
                                        rule.cycle.push(layout);
                                    } else {
                                        rule.cycle.retain(|&item| item != layout);
                                    }
                                }
                                let can_move = enabled && position < selected_count;
                                if ui
                                    .add_enabled(can_move && position > 0, egui::Button::new("↑"))
                                    .clicked()
                                {
                                    reorder = Some((layout, order[position - 1]));
                                }
                                if ui
                                    .add_enabled(
                                        can_move && position + 1 < selected_count,
                                        egui::Button::new("↓"),
                                    )
                                    .clicked()
                                {
                                    reorder = Some((layout, order[position + 1]));
                                }
                            });
                        }
                        if let Some((layout, neighbor)) = reorder
                            && let Some(from) = rule.cycle.iter().position(|&item| item == layout)
                            && let Some(to) = rule.cycle.iter().position(|&item| item == neighbor)
                        {
                            rule.cycle.swap(from, to);
                        }
                        let cycle = rule
                            .cycle
                            .iter()
                            .filter(|layout| !cfg!(windows) || layouts.contains(layout))
                            .map(|layout| layout.name())
                            .collect::<Vec<_>>()
                            .join(" → ");
                        if !rule.enabled {
                            hint(ui, tr("hotkey.off", "Off"));
                        } else if cycle.is_empty() {
                            hint(ui, tr("appLayouts.global", "Use the global cycle"));
                        } else {
                            hint(ui, &cycle);
                        }
                    });
                });
        }
        if let Some(index) = remove {
            self.settings.applications.remove(index);
        }
        #[cfg(windows)]
        if let Some(index) = choose {
            self.application_picker = Some(ApplicationPicker {
                index,
                search: String::new(),
                apps: typlo_rust::windows::running_applications(),
                selected: None,
            });
        }
        if ui.button(tr("appLayouts.add", "Add application")).clicked() {
            self.settings.applications.push(config::ApplicationRule {
                cycle: Vec::new(),
                ..Default::default()
            });
        }
        #[cfg(windows)]
        self.choose_application(ui);
    }

    #[cfg(windows)]
    fn choose_application(&mut self, ui: &mut Ui) {
        let Some(picker) = &mut self.application_picker else {
            return;
        };
        let mut close = false;
        let response = egui::Modal::new(ui.id().with("choose-application")).show(ui.ctx(), |ui| {
            ui.set_min_width(380.);
            ui.heading(tr("appLayouts.choose", "Choose an application"));
            ui.horizontal(|ui| {
                ui.add(
                    typlo_rust::appearance::text_input(&mut picker.search)
                        .desired_width(315.)
                        .hint_text(tr("appLayouts.search", "Search applications…")),
                );
                if ui
                    .button("↻")
                    .on_hover_text(tr("appLayouts.refresh", "Refresh"))
                    .clicked()
                {
                    picker.apps = typlo_rust::windows::running_applications();
                    picker.selected = None;
                }
            });
            let query = picker.search.to_lowercase();
            egui::ScrollArea::vertical()
                .max_height(300.)
                .show(ui, |ui| {
                    for (process, title) in &picker.apps {
                        let label = format!("{title} · {process}");
                        if label.to_lowercase().contains(&query)
                            && ui
                                .selectable_label(picker.selected.as_ref() == Some(process), label)
                                .clicked()
                        {
                            picker.selected = Some(process.clone());
                        }
                    }
                });
            ui.horizontal(|ui| {
                if ui.button(tr("settings.cancel", "Cancel")).clicked() {
                    close = true;
                }
                if ui
                    .add_enabled(
                        picker.selected.is_some(),
                        egui::Button::new(tr("appLayouts.choose", "Choose")),
                    )
                    .clicked()
                    && let Some(selected) = picker.selected.take()
                    && let Some(rule) = self.settings.applications.get_mut(picker.index)
                {
                    rule.process = selected;
                    close = true;
                }
            });
        });
        if close || response.should_close() {
            self.application_picker = None;
        }
    }

    fn snippets(&mut self, ui: &mut Ui) {
        card(ui).show(ui, |ui| {
            ui.label(
                RichText::new(tr("settings.snippetsHeader", "Сниппеты"))
                    .size(16.0)
                    .strong(),
            );
            hint(
                ui,
                tr(
                    "settings.snippetsHint",
                    "Type a trigger anywhere and Typlo inserts the snippet.",
                ),
            );
            ui.label(tr(
                "rust.snippet.caret",
                "Добавьте {cursor} в место, где после вставки должен стоять курсор.",
            ));
            let mut remove = None;
            for (index, snippet) in self.settings.snippets.iter_mut().enumerate() {
                ui.push_id(index, |ui| {
                    ui.add(
                        typlo_rust::appearance::text_input(&mut snippet.name)
                            .hint_text(tr("rust.snippet.name", "Name (optional)"))
                            .desired_width(f32::INFINITY),
                    );
                    ui.horizontal(|ui| {
                        ui.add(
                            typlo_rust::appearance::text_input(&mut snippet.trigger)
                                .hint_text(";addr")
                                .desired_width((ui.available_width() - 90.).max(160.)),
                        );
                        if ui.button(tr("history.ctx.delete", "Удалить")).clicked() {
                            remove = Some(index);
                        }
                    });
                    ui.add(
                        typlo_rust::appearance::text_area(&mut snippet.text)
                            .hint_text(tr("settings.snippetText", "Text to insert"))
                            .desired_rows(3)
                            .desired_width(f32::INFINITY),
                    );
                    #[cfg(windows)]
                    {
                        ui.checkbox(&mut snippet.parameters, tr("rust.snippet.parameters", "Ask for parameters"));
                        if snippet.parameters {
                            hint(ui, tr("rust.snippet.parameterHint", "Use {name} or {ticket}. Pick the snippet from the snippet menu to fill in the values."));
                        }
                    }
                });
            }
            if let Some(index) = remove {
                self.settings.snippets.remove(index);
            }
            if ui
                .button(tr("settings.snippetAdd", "Добавить сниппет"))
                .clicked()
            {
                self.settings.snippets.push(Default::default());
            }
        });
    }

    fn hotkeys(&mut self, ui: &mut Ui) {
        use typlo_rust::hotkeys::Action;
        for (action, shortcut) in self.settings.hotkeys.entries_mut() {
            if !cfg!(windows) && matches!(action, Action::SnippetMenu | Action::ClearDrawing) {
                continue;
            }
            if matches!(
                action,
                Action::CaptureCopy | Action::CaptureFile | Action::CaptureEdit | Action::History
            ) {
                continue;
            }
            if matches!(
                action,
                Action::English | Action::Russian | Action::Ukrainian
            ) && shortcut.is_empty()
            {
                continue;
            }
            setting_row(ui, action.label(), |ui| {
                edit_hotkey(ui, action as i32, shortcut)
            });
        }
        #[cfg(windows)]
        edit_drawing_shortcut(ui, &mut self.settings.drawing_shortcut);
        for layout in available_choices() {
            let current = self
                .settings
                .layout_hotkeys
                .iter()
                .position(|setting| setting.layout == layout);
            let label = tr("settings.convertTo", "Convert to {0}").replace("{0}", &layout.name());
            setting_row(ui, &label, |ui| {
                if let Some(index) = current {
                    edit_hotkey(ui, layout, &mut self.settings.layout_hotkeys[index].hotkey);
                } else {
                    let mut shortcut = String::new();
                    if edit_hotkey(ui, layout, &mut shortcut) {
                        self.settings.layout_hotkeys.push(config::LayoutShortcut {
                            layout,
                            hotkey: shortcut,
                        });
                    }
                }
            });
        }
    }

    fn shortcuts(&mut self, ui: &mut Ui) {
        heading(ui, tr("shortcuts.global", "Global shortcuts"));
        for (action, shortcut) in self.settings.hotkeys.entries_mut() {
            if !cfg!(windows)
                && matches!(
                    action,
                    typlo_rust::hotkeys::Action::SnippetMenu
                        | typlo_rust::hotkeys::Action::ClearDrawing
                )
            {
                continue;
            }
            let label = if shortcut.is_empty() {
                tr("hotkey.off", "Off")
            } else {
                shortcut.as_str()
            };
            shortcut_row(ui, label, action.label());
        }
        #[cfg(windows)]
        {
            use typlo_rust::hotkeys::{DrawingButton, DrawingShortcut};
            let shortcut = self.settings.drawing_shortcut;
            let label = if shortcut.button == DrawingButton::Off {
                shortcut.button.label().to_owned()
            } else {
                format!(
                    "{} + {}",
                    DrawingShortcut::modifier_label(shortcut.modifiers),
                    shortcut.button.label()
                )
            };
            shortcut_row(ui, &label, DrawingShortcut::label());
        }
        for layout in available_choices() {
            let label = tr("settings.convertTo", "Convert to {0}").replace("{0}", &layout.name());
            let shortcut = self
                .settings
                .layout_hotkeys
                .iter()
                .find(|setting| setting.layout == layout && !setting.hotkey.is_empty())
                .map_or(tr("hotkey.off", "Off"), |setting| setting.hotkey.as_str());
            shortcut_row(ui, shortcut, &label);
        }
        if !self.settings.hotkeys.history.is_empty() {
            shortcut_row(
                ui,
                &format!("{} ×2", self.settings.hotkeys.history),
                tr("shortcuts.previous", "Paste previous clipboard item"),
            );
        }
        #[cfg(windows)]
        {
            heading(ui, tr("rust.shortcuts.snippets", "In the snippet menu"));
            for (keys, action) in [
                ("↑ ↓", tr("shortcuts.select", "Select item")),
                ("Enter", tr("rust.snippet.insert", "Insert snippet")),
                ("Ctrl+P", tr("history.ctx.pin", "Pin")),
                (
                    "Tab",
                    tr("rust.shortcuts.snippetParameter", "Next snippet parameter"),
                ),
                ("Esc", tr("update.close", "Close")),
            ] {
                shortcut_row(ui, keys, action);
            }
            heading(ui, tr("rust.shortcuts.drawing", "While drawing on screen"));
            shortcut_row(
                ui,
                "1–9",
                tr("rust.shortcuts.brushPreset", "Select brush thickness"),
            );
            shortcut_row(
                ui,
                tr("rust.shortcuts.wheel", "Mouse wheel"),
                tr("rust.shortcuts.brushAdjust", "Adjust brush thickness"),
            );
            heading(ui, tr("paint.openLatest", "Screenshot editor"));
            for (keys, action) in [
                ("Ctrl+C", tr("capture.copy", "Copy screenshot")),
                ("Ctrl+S", tr("capture.save", "Save screenshot")),
                ("Ctrl+Z", tr("rust.shortcuts.undoEdit", "Undo edit")),
                (
                    "Ctrl+Y / Ctrl+Shift+Z",
                    tr("rust.shortcuts.redoEdit", "Redo edit"),
                ),
                ("Esc", tr("update.close", "Close")),
            ] {
                shortcut_row(ui, keys, action);
            }
        }
        heading(
            ui,
            tr("shortcuts.historyWindow", "In the clipboard history"),
        );
        for (keys, action) in [
            ("↑ ↓  PgUp PgDn", tr("shortcuts.select", "Select item")),
            ("Enter", tr("history.ctx.paste", "Paste")),
            (
                "Shift+Enter",
                tr("history.ctx.plain", "Paste as plain text"),
            ),
            ("Ctrl+1 … Ctrl+9", tr("shortcuts.quick", "Paste by number")),
            ("Ctrl+O", tr("history.ctx.view", "View…")),
            ("Ctrl+T", tr("history.ctx.ocr", "Copy text from image")),
            ("Ctrl+P", tr("history.ctx.pin", "Pin")),
            ("Del", tr("history.ctx.delete", "Delete")),
            ("Esc", tr("update.close", "Close")),
        ] {
            shortcut_row(ui, keys, action);
        }
    }

    #[cfg(windows)]
    fn screenshots(&mut self, ui: &mut Ui) {
        heading(ui, tr("capture.title", "Screenshots"));
        hint(
            ui,
            tr("capture.settingsHint", "Select an area to copy or save."),
        );
        let shape = |value| match value {
            config::CaptureShape::Rectangle => tr("rust.capture.rectangle", "Rectangle"),
            config::CaptureShape::Free => tr("rust.capture.free", "Freehand"),
        };
        ui.label(tr("capture.shape", "Selection shape"));
        egui::ComboBox::from_id_salt("capture-shape")
            .width(ui.available_width())
            .selected_text(shape(self.settings.capture_shape))
            .show_ui(ui, |ui| {
                for value in [config::CaptureShape::Rectangle, config::CaptureShape::Free] {
                    ui.selectable_value(&mut self.settings.capture_shape, value, shape(value));
                }
            });
        ui.checkbox(
            &mut self.settings.capture_editor_after_copy,
            tr(
                "capture.editorAfterCopy",
                "Open the editor after copying a screenshot",
            ),
        );
        ui.horizontal(|ui| {
            for (label, mode) in [
                (tr("capture.copy", "Copy screenshot"), "clipboard"),
                (tr("capture.save", "Save screenshot"), "file"),
            ] {
                if ui.button(label).clicked() {
                    let shape = if self.settings.capture_shape == config::CaptureShape::Free {
                        "free"
                    } else {
                        "rectangle"
                    };
                    if let Err(error) = std::env::current_exe().and_then(|exe| {
                        std::process::Command::new(exe)
                            .arg("--capture")
                            .arg(&self.profile)
                            .args([mode, shape])
                            .spawn()
                    }) {
                        self.status = error.to_string();
                    }
                }
            }
        });
        setting_row(ui, tr("capture.copy", "Copy screenshot"), |ui| {
            edit_hotkey(ui, "capture-copy", &mut self.settings.hotkeys.capture_copy)
        });
        setting_row(ui, tr("capture.save", "Save screenshot"), |ui| {
            edit_hotkey(ui, "capture-file", &mut self.settings.hotkeys.capture_file)
        });
        setting_row(ui, tr("paint.openLatest", "Edit last screenshot"), |ui| {
            edit_hotkey(
                ui,
                "capture-editor",
                &mut self.settings.hotkeys.capture_edit,
            )
        });
    }

    fn clipboard_history(&mut self, ui: &mut Ui) {
        heading(ui, tr("settings.historyHeader", "Clipboard history"));
        ui.checkbox(
            &mut self.settings.history_enabled,
            tr("settings.historyEnabled", "Keep history"),
        );
        ui.label(tr("settings.historyUseOrder", "After pasting from history"));
        let order = |move_up| {
            if move_up {
                tr("settings.historyMoveUsed", "Move used item to the top")
            } else {
                tr("settings.historyKeepUsed", "Keep the item in its place")
            }
        };
        egui::ComboBox::from_id_salt("history-order")
            .width(ui.available_width())
            .selected_text(order(self.settings.history_move_to_top))
            .show_ui(ui, |ui| {
                ui.selectable_value(&mut self.settings.history_move_to_top, true, order(true));
                ui.selectable_value(&mut self.settings.history_move_to_top, false, order(false));
            });
        hint(
            ui,
            tr(
                "settings.historyOrderHint",
                "Choose how used items are displayed in history.",
            ),
        );
        setting_row(ui, tr("settings.openHistory", "Open history"), |ui| {
            edit_hotkey(ui, "history", &mut self.settings.hotkeys.history)
        });
        setting_row(ui, tr("settings.maxItems", "Maximum items"), |ui| {
            ui.add(egui::DragValue::new(&mut self.settings.history_items).range(1..=10_000))
        });
        setting_row(ui, tr("settings.maxTotal", "Total size, MB"), |ui| {
            ui.add(egui::DragValue::new(&mut self.settings.history_total_mib).range(1..=4096))
        });
        setting_row(ui, tr("settings.maxImage", "Max image, MB"), |ui| {
            ui.add(egui::DragValue::new(&mut self.settings.history_image_mib).range(1..=128))
        });
        setting_row(ui, tr("rust.history.textLimit", "Max text, KB"), |ui| {
            ui.add(egui::DragValue::new(&mut self.settings.history_text_kib).range(1..=8192))
        });
        ui.label(tr(
            "settings.excluded",
            "Never store from apps (process names, comma-separated)",
        ));
        let mut excluded = self.settings.history_excluded_apps.join(", ");
        if ui
            .add(typlo_rust::appearance::text_input(&mut excluded).desired_width(f32::INFINITY))
            .changed()
        {
            self.settings.history_excluded_apps = excluded
                .split(',')
                .map(str::trim)
                .filter(|name| !name.is_empty())
                .map(str::to_owned)
                .collect();
        }
        #[cfg(any(windows, target_os = "linux", target_os = "macos"))]
        self.history_stats(ui);
        #[cfg(any(target_os = "linux", target_os = "macos"))]
        if ui
            .button(tr("settings.openHistory", "Open history"))
            .clicked()
            && let Err(error) = typlo_rust::unix_clipboard::open_history(&self.profile)
        {
            self.status = error.to_string();
        }
    }

    #[cfg(any(windows, target_os = "linux", target_os = "macos"))]
    fn history_stats(&mut self, ui: &mut Ui) {
        let row = ui.horizontal(|ui| {
            if let Some((count, bytes)) = self.history_status.value {
                hint(
                    ui,
                    &tr("settings.stats", "Now: {0} items, {1:0.0} MB")
                        .replace("{0}", &count.to_string())
                        .replace("{1:0.0}", &format!("{:.1}", bytes as f64 / 1048576.)),
                );
            }
            if ui.button(tr("settings.clear", "Clear history…")).clicked() {
                self.clear_history = true;
            }
        });
        if ui.is_rect_visible(row.response.rect) {
            self.history_status.refresh(&self.profile, ui.ctx());
        }
        if self.clear_history {
            let response = egui::Modal::new(ui.id().with("clear-history")).show(ui.ctx(), |ui| {
                ui.heading(tr("clear.title", "Clear history"));
                ui.label(tr(
                    "clear.question",
                    "Delete the whole history except pinned items?",
                ));
                ui.horizontal(|ui| {
                    if ui.button(tr("settings.cancel", "Cancel")).clicked() {
                        self.clear_history = false;
                    }
                    if ui.button(tr("clear.yes", "Clear")).clicked() {
                        self.clear_history = false;
                        #[cfg(windows)]
                        let result = typlo_rust::windows::history::History::open(&self.profile)
                            .and_then(|store| store.clear_unpinned(&self.profile));
                        #[cfg(any(target_os = "linux", target_os = "macos"))]
                        let result = typlo_rust::unix_history::History::open(&self.profile)
                            .and_then(|store| store.clear_unpinned());
                        match result {
                            Ok(()) => {
                                self.history_status = HistoryStatus::default();
                                ui.ctx().request_repaint();
                            }
                            Err(error) => self.status = error.to_string(),
                        }
                    }
                });
            });
            if response.should_close() {
                self.clear_history = false;
            }
        }
    }

    fn backup(&mut self, ui: &mut Ui, heading_enabled: bool) {
        if heading_enabled {
            heading(ui, tr("rust.backup.heading", "Backup"));
        }
        #[cfg(windows)]
        ui.horizontal_wrapped(|ui| {
            for (save, label) in [
                (true, tr("settings.export", "Export settings…")),
                (false, tr("settings.import", "Import settings…")),
            ] {
                if ui.button(label).clicked() {
                    match settings_file(save, heading_enabled) {
                        Ok(Some(path)) => self.backup_selected(path, save, heading_enabled),
                        Ok(None) => {}
                        Err(error) => self.status = error.to_string(),
                    }
                }
            }
        });
        #[cfg(not(windows))]
        ui.horizontal_wrapped(|ui| {
            ui.add(
                typlo_rust::appearance::text_input(&mut self.backup_path).hint_text(
                    if heading_enabled {
                        tr("rust.backup.path", "Path to .typlo file")
                    } else {
                        "typlo-settings.json"
                    },
                ),
            );
            let path = PathBuf::from(self.backup_path.trim());
            for (save, label) in [
                (true, tr("settings.export", "Export settings…")),
                (false, tr("settings.import", "Import settings…")),
            ] {
                if ui
                    .add_enabled(!path.as_os_str().is_empty(), egui::Button::new(label))
                    .clicked()
                {
                    self.backup_selected(path.clone(), save, heading_enabled);
                }
            }
        });
        if let Some(backup) = &self.pending_backup {
            let (snippets, words, exclusions, pairs) = backup.summary();
            let summary = if backup.settings_only() {
                tr(
                    "rust.backup.settingsSummary",
                    "Snippets: {0} · Correction pairs: {1}",
                )
                .replace("{0}", &snippets.to_string())
                .replace("{1}", &pairs.to_string())
            } else {
                tr(
                    "rust.backup.summary",
                    "Snippets: {0} · Words: {1} · Exceptions: {2} · Pairs: {3}",
                )
                .replace("{0}", &snippets.to_string())
                .replace("{1}", &words.to_string())
                .replace("{2}", &exclusions.to_string())
                .replace("{3}", &pairs.to_string())
            };
            let title = if backup.settings_only() {
                tr("settings.import", "Import settings…")
            } else {
                tr("tools.restore", "Restore backup")
            };
            let response = egui::Modal::new(ui.id().with("restore-backup")).show(ui.ctx(), |ui| {
                ui.heading(title);
                ui.label(summary);
                ui.horizontal(|ui| {
                    if ui.button(tr("settings.cancel", "Cancel")).clicked() {
                        self.pending_backup = None;
                    }
                    if ui.button(tr("tools.restore", "Restore backup")).clicked()
                        && let Some(backup) = self.pending_backup.take()
                    {
                        match backup.restore(&self.profile) {
                            Ok(settings) => {
                                self.settings = settings;
                                locale::set_language(&self.settings.ui_language);
                                locale::theme(ui.ctx(), self.settings.ui_theme);
                                locale::fonts(ui.ctx(), &self.settings.ui_language);
                                ui.ctx().send_viewport_cmd(egui::ViewportCommand::Title(
                                    self.page.title().into(),
                                ));
                                self.dictionary = DictionaryView::new(&self.profile);
                                #[cfg(windows)]
                                typlo_rust::windows::reload_settings();
                                self.status = self
                                    .saved_status(tr("rust.backup.restored", "Backup restored"));
                            }
                            Err(error) => self.status = error.to_string(),
                        }
                    }
                });
            });
            if response.should_close() {
                self.pending_backup = None;
            }
        }
    }

    fn backup_selected(&mut self, path: PathBuf, save: bool, full: bool) {
        self.status.clear();
        self.pending_backup = None;
        if save {
            let exported = if full {
                Backup::export(&self.profile, &path)
            } else {
                Backup::export_settings(&self.profile, &self.settings, &path)
            };
            self.status = match exported {
                Ok(()) => tr("settings.exported", "Settings saved to the file").into(),
                Err(error) => error.to_string(),
            };
        } else {
            match Backup::read(&path) {
                Ok(backup) => self.pending_backup = Some(backup),
                Err(error) => self.status = error.to_string(),
            }
        }
    }

    fn save(&mut self, ui: &Ui) {
        match config::save(&self.profile, &self.settings) {
            Ok(()) => {
                #[cfg(windows)]
                typlo_rust::windows::reload_settings();
                self.status = self.saved_status(tr("rust.settings.saved", "Settings saved"));
                ui.ctx().send_viewport_cmd(egui::ViewportCommand::Close);
            }
            Err(error) => self.status = error.to_string(),
        }
    }

    #[cfg(windows)]
    fn refresh_diagnostics(&mut self, retry: bool) {
        self.resident_memory = typlo_rust::windows::resident_memory().ok();
        match typlo_rust::windows::resident_diagnostics(&self.profile, retry) {
            Ok(report) => self.diagnostics = Some(report),
            Err(error) => {
                self.diagnostics = None;
                if retry {
                    self.status = error.to_string();
                }
            }
        }
    }

    #[cfg(windows)]
    fn automatic_diagnostics(&mut self, ui: &mut Ui) {
        ui.collapsing(
            tr("auto.diagnostics", "Auto-correction diagnostics"),
            |ui| {
                ui.horizontal_wrapped(|ui| {
                    if ui.button(tr("manage.refresh", "Refresh")).clicked() {
                        self.refresh_diagnostics(false);
                    }
                    if ui
                        .button(tr("auto.retry", "Check / retry search"))
                        .clicked()
                    {
                        self.refresh_diagnostics(true);
                    }
                });
                let Some(report) = &self.diagnostics else {
                    ui.weak(tr(
                        "rust.tools.noDiagnostics",
                        "Typlo diagnostics are unavailable.",
                    ));
                    return;
                };
                if let Some(healthy) = report.dictionary_healthy {
                    ui.label(if healthy {
                        tr("rust.dictionary.healthy", "Dictionary check passed")
                    } else {
                        tr("rust.dictionary.failed", "Dictionary check failed")
                    });
                }
                let code = |index: Option<usize>| {
                    index
                        .and_then(|index| typlo_rust::lexicon::LANGUAGES.get(index))
                        .copied()
                        .unwrap_or("—")
                };
                egui::ScrollArea::vertical()
                    .id_salt("automatic-attempts")
                    .max_height(240.)
                    .show(ui, |ui| {
                        for attempt in &report.automatic {
                            let age = std::time::SystemTime::now()
                                .duration_since(std::time::UNIX_EPOCH)
                                .unwrap_or_default()
                                .as_millis()
                                .saturating_sub(attempt.timestamp_ms as u128)
                                / 1000;
                            ui.label(format!(
                                "{age}s · {} → {} · {:.2} ms · {}",
                                code(attempt.source),
                                code(attempt.target),
                                attempt.elapsed_us as f64 / 1000.,
                                attempt.reason.message()
                            ));
                        }
                    });
            },
        );
    }

    #[cfg(windows)]
    fn performance_report(&mut self, ui: &mut Ui) {
        if let Some((working_set, private)) = self.resident_memory {
            let text = tr(
                "rust.tools.memory",
                "Resident memory · working set {0} MiB · private {1} MiB",
            )
            .replace("{0}", &format!("{:.1}", working_set as f64 / 1048576.))
            .replace("{1}", &format!("{:.1}", private as f64 / 1048576.));
            ui.label(text);
        }
        if let Some(report) = &self.diagnostics {
            if let Some(summary) = report.latest_summary() {
                ui.label(summary);
            }
            for ((operation, field), count, median, p95) in report.latency() {
                ui.label(format!(
                    "{} · {field} · n={count} · P50 {:.2} ms · P95 {:.2} ms",
                    typlo_rust::windows::diagnostic_operation_label(operation),
                    median as f64 / 1000.,
                    p95 as f64 / 1000.
                ));
            }
        }
        ui.horizontal(|ui| {
            if ui.button(tr("manage.refresh", "Refresh")).clicked() {
                self.refresh_diagnostics(false);
            }
            if ui.button(tr("tools.report", "Export performance report…")).clicked() {
                match choose_file(true, "Typlo-performance", "txt", None) {
                    Ok(Some(path)) => match typlo_rust::windows::resident_memory().and_then(|(working_set, private)| {
                        let report = typlo_rust::windows::resident_diagnostics(&self.profile, false)?;
                        let report = serde_json::to_string_pretty(&report).map_err(std::io::Error::other)?;
                        std::fs::write(path, format!("Typlo {}\nResident working set: {working_set}\nPrivate bytes: {private}\n{report}\n", env!("CARGO_PKG_VERSION")))
                    }) {
                        Ok(()) => self.status = tr("manage.saved", "Saved").into(),
                        Err(error) => self.status = error.to_string(),
                    },
                    Ok(None) => {}
                    Err(error) => self.status = error.to_string(),
                }
            }
        });
    }
}

impl eframe::App for Workspace {
    fn clear_color(&self, visuals: &egui::Visuals) -> [f32; 4] {
        visuals.panel_fill.to_normalized_gamma_f32()
    }

    fn ui(&mut self, ui: &mut Ui, _: &mut eframe::Frame) {
        let page = self.page;
        if page != Page::Update {
            egui::Panel::bottom("settings-footer")
                .show_separator_line(true)
                .show_inside(ui, |ui| {
                    Frame::new()
                        .inner_margin(Margin::symmetric(20, 12))
                        .show(ui, |ui| {
                            ui.horizontal(|ui| {
                                if page == Page::Settings
                                    && ui.button(tr("update.check", "Check for updates")).clicked()
                                {
                                    #[cfg(any(windows, target_os = "linux", target_os = "macos"))]
                                    {
                                        self.updates =
                                            typlo_rust::update_view::UpdateView::new(true);
                                    }
                                    self.navigate(ui, Page::Update);
                                }
                                if !self.status.is_empty() {
                                    hint(ui, &self.status);
                                }
                                ui.with_layout(
                                    egui::Layout::right_to_left(egui::Align::Center),
                                    |ui| {
                                        let close = |ui: &Ui| {
                                            ui.ctx().send_viewport_cmd(egui::ViewportCommand::Close)
                                        };
                                        if page == Page::Settings {
                                            let save = egui::Button::new(
                                                RichText::new(tr("settings.save", "Save"))
                                                    .color(Color32::WHITE),
                                            )
                                            .fill(typlo_rust::appearance::rgb(0x2563eb));
                                            if ui.add(save).clicked() {
                                                self.save(ui);
                                            }
                                            if ui.button(tr("settings.cancel", "Cancel")).clicked()
                                            {
                                                close(ui);
                                            }
                                        } else {
                                            if ui.button(tr("update.close", "Close")).clicked() {
                                                close(ui);
                                            }
                                            if ui.button(tr("tray.settings", "Settings")).clicked()
                                            {
                                                self.navigate(ui, Page::Settings);
                                            }
                                        }
                                    },
                                );
                            });
                        });
                });
        }
        egui::ScrollArea::vertical().show(ui, |ui| {
            Frame::new().inner_margin(Margin::symmetric(20, 6)).show(ui, |ui| {
                ui.set_min_width(ui.available_width());
                match page {
                    Page::Settings => {
                        #[cfg(windows)]
                        self.lan_clipboard.show(ui, &self.profile);
                        self.preferences(ui);
                        heading(ui, tr("settings.layoutHeader", "Fix the layout of selected text"));
                        hint(ui, tr("settings.layoutHint", "Select text and press a shortcut."));
                        if ui.button(tr("shortcuts.menu", "Keyboard shortcuts…")).clicked() {
                            self.navigate(ui, Page::Shortcuts);
                        }
                        self.hotkeys(ui);
                        self.conversion_options(ui);
                        self.applications(ui);
                        #[cfg(windows)]
                        self.screenshots(ui);
                        #[cfg(not(windows))]
                        {
                            heading(ui, tr("capture.title", "Screenshots"));
                            hint(ui, tr("capture.windowsOnly", "Screenshots are available on Windows."));
                        }
                        self.clipboard_history(ui);
                        self.snippets(ui);
                    }
                    Page::Shortcuts => self.shortcuts(ui),
                    Page::Dictionary => {
                        self.dictionary.show(ui);
                        #[cfg(windows)]
                        self.automatic_diagnostics(ui);
                    }
                    Page::Tools => {
                        heading(ui, tr("tools.measurements", "Measurements"));
                        #[cfg(windows)]
                        self.performance_report(ui);
                        hint(ui, tr("tools.noProbe", "Run the field compatibility shortcut in the target editor to inspect it."));
                        shortcut_row(ui, &self.settings.hotkeys.tools, tr("tools.title", "Typlo tools"));
                        self.backup(ui, true);
                    }
                    Page::Update => {
                        heading(ui, Page::Update.title());
                        #[cfg(any(windows, target_os = "linux", target_os = "macos"))]
                        self.updates.show(ui);
                        if ui.button(tr("update.close", "Close")).clicked() {
                            ui.ctx().send_viewport_cmd(egui::ViewportCommand::Close);
                        }
                    }
                }
            });
        });
        if self
            .report
            .as_mut()
            .is_some_and(|report| report.finish(ui.ctx(), page))
        {
            self.report = None;
        }
    }
}

fn heading(ui: &mut Ui, label: &str) {
    ui.add_space(8.);
    ui.label(RichText::new(label).size(16.).strong());
}

fn hint(ui: &mut Ui, text: &str) {
    ui.label(
        RichText::new(text)
            .size(12.)
            .color(ui.visuals().weak_text_color()),
    );
}

fn setting_row<R>(ui: &mut Ui, label: &str, input: impl FnOnce(&mut Ui) -> R) -> R {
    const LABEL_WIDTH: f32 = 182.;
    ui.horizontal(|ui| {
        ui.allocate_ui_with_layout(
            egui::vec2(LABEL_WIDTH, 32.),
            egui::Layout::left_to_right(egui::Align::Center),
            |ui| {
                ui.set_width(LABEL_WIDTH);
                ui.add(egui::Label::new(label).wrap());
            },
        );
        input(ui)
    })
    .inner
}

fn shortcut_row(ui: &mut Ui, shortcut: &str, action: &str) {
    setting_row(ui, shortcut, |ui| ui.label(action));
}

#[cfg(windows)]
fn edit_drawing_shortcut(ui: &mut Ui, shortcut: &mut typlo_rust::hotkeys::DrawingShortcut) {
    use typlo_rust::hotkeys::{DrawingButton, DrawingShortcut};
    setting_row(ui, DrawingShortcut::label(), |ui| {
        ui.add_enabled_ui(shortcut.button != DrawingButton::Off, |ui| {
            egui::ComboBox::from_id_salt("drawing_modifiers")
                .width(130.)
                .selected_text(DrawingShortcut::modifier_label(shortcut.modifiers))
                .show_ui(ui, |ui| {
                    for modifiers in DrawingShortcut::MODIFIER_OPTIONS {
                        ui.selectable_value(
                            &mut shortcut.modifiers,
                            modifiers,
                            DrawingShortcut::modifier_label(modifiers),
                        );
                    }
                });
        });
        egui::ComboBox::from_id_salt("drawing_mouse_button")
            .width(145.)
            .selected_text(shortcut.button.label())
            .show_ui(ui, |ui| {
                for button in [
                    DrawingButton::Left,
                    DrawingButton::Right,
                    DrawingButton::Middle,
                    DrawingButton::Off,
                ] {
                    ui.selectable_value(&mut shortcut.button, button, button.label());
                }
            });
    });
}

fn tri_state(ui: &mut Ui, value: &mut Option<bool>, label: &str) {
    let mut checked = value.unwrap_or(false);
    if ui
        .add(egui::Checkbox::new(&mut checked, label).indeterminate(value.is_none()))
        .changed()
    {
        *value = match *value {
            None => Some(true),
            Some(true) => Some(false),
            Some(false) => None,
        };
    }
}

fn edit_hotkey(ui: &mut Ui, id: impl std::hash::Hash, shortcut: &mut String) -> bool {
    let id = ui.id().with(id);
    let focused = ui.memory(|memory| memory.has_focus(id));
    if focused {
        #[cfg(windows)]
        let system = {
            use windows_sys::Win32::UI::Input::KeyboardAndMouse::{
                GetAsyncKeyState, VK_LWIN, VK_RWIN,
            };
            [VK_LWIN, VK_RWIN]
                .into_iter()
                .any(|key| unsafe { GetAsyncKeyState(i32::from(key)) } < 0)
        };
        #[cfg(not(windows))]
        let system = false;
        use egui::Key;
        if let Some((key, mods)) = ui.input(|input| {
            input.events.iter().find_map(|event| {
                if let egui::Event::Key {
                    key,
                    physical_key,
                    modifiers,
                    pressed: true,
                    repeat: false,
                } = event
                {
                    Some((physical_key.unwrap_or(*key), *modifiers))
                } else {
                    None
                }
            })
        }) {
            ui.data_mut(|data| data.remove_temp::<bool>(id));
            if key == Key::Backspace {
                shortcut.clear();
                ui.memory_mut(|memory| memory.surrender_focus(id));
                return true;
            }
            if key != Key::Tab && key != Key::Escape {
                let mut value = String::new();
                for (pressed, label) in [
                    (mods.ctrl, "Ctrl+"),
                    (mods.alt, "Alt+"),
                    (mods.shift, "Shift+"),
                    (mods.mac_cmd || system, "Win+"),
                ] {
                    if pressed {
                        value.push_str(label);
                    }
                }
                value.push_str(key.symbol_or_name());
                if typlo_rust::hotkeys::Chord::parse(&value).is_some() {
                    *shortcut = value;
                    ui.memory_mut(|memory| memory.surrender_focus(id));
                    return true;
                }
            }
        }
        let pair = ui.input(|input| {
            input.modifiers.ctrl
                && input.modifiers.shift
                && !input.modifiers.alt
                && !input.modifiers.mac_cmd
                && !system
        });
        if pair {
            ui.data_mut(|data| data.insert_temp(id, true));
        }
        if !pair
            && ui
                .data_mut(|data| data.remove_temp::<bool>(id))
                .unwrap_or(false)
        {
            *shortcut = "Ctrl+Shift".into();
            ui.memory_mut(|memory| memory.surrender_focus(id));
            return true;
        }
    } else {
        ui.data_mut(|data| data.remove_temp::<bool>(id));
    }
    let mut display = if focused {
        tr("hotkey.placeholder", "Press a shortcut…")
    } else {
        shortcut.as_str()
    };
    let visuals = ui.visuals();
    let inactive = &visuals.widgets.inactive;
    let frame = Frame::new()
        .fill(visuals.text_edit_bg_color())
        .inner_margin(Margin::symmetric(12, 9))
        .corner_radius(inactive.corner_radius)
        .stroke(if focused {
            visuals.selection.stroke
        } else {
            inactive.bg_stroke
        });
    ui.add(
        typlo_rust::appearance::text_input(&mut display)
            .id(id)
            .frame(frame)
            .desired_width(ui.available_width().max(90.)),
    );
    false
}

#[cfg(windows)]
fn settings_file(save: bool, full: bool) -> std::io::Result<Option<PathBuf>> {
    if full {
        choose_file(save, "Typlo-backup", "typlo", (!save).then_some("json"))
    } else {
        choose_file(save, "typlo-settings", "json", (!save).then_some("typlo"))
    }
}

#[cfg(windows)]
fn choose_file(
    save: bool,
    name: &str,
    extension: &str,
    alternate: Option<&str>,
) -> std::io::Result<Option<PathBuf>> {
    use std::{io, os::windows::ffi::OsStringExt};
    use windows_sys::Win32::UI::{
        Controls::Dialogs::{
            CommDlgExtendedError, GetOpenFileNameW, GetSaveFileNameW, OFN_EXPLORER,
            OFN_FILEMUSTEXIST, OFN_NOCHANGEDIR, OFN_OVERWRITEPROMPT, OFN_PATHMUSTEXIST,
            OPENFILENAMEW,
        },
        Input::KeyboardAndMouse::GetActiveWindow,
    };
    let pattern = alternate.map_or_else(
        || format!("*.{extension}"),
        |alternate| format!("*.{extension};*.{alternate}"),
    );
    let filter = format!("Typlo ({pattern})\0{pattern}\0\0")
        .encode_utf16()
        .collect::<Vec<_>>();
    let extension_name = format!("{extension}\0").encode_utf16().collect::<Vec<_>>();
    let mut path = vec![0u16; 32768];
    if save {
        for (slot, unit) in path
            .iter_mut()
            .zip(format!("{name}.{extension}").encode_utf16())
        {
            *slot = unit;
        }
    }
    let mut dialog = OPENFILENAMEW {
        lStructSize: size_of::<OPENFILENAMEW>() as u32,
        hwndOwner: unsafe { GetActiveWindow() },
        lpstrFilter: filter.as_ptr(),
        nFilterIndex: 1,
        lpstrFile: path.as_mut_ptr(),
        nMaxFile: path.len() as u32,
        lpstrDefExt: extension_name.as_ptr(),
        Flags: OFN_EXPLORER
            | OFN_NOCHANGEDIR
            | OFN_PATHMUSTEXIST
            | if save {
                OFN_OVERWRITEPROMPT
            } else {
                OFN_FILEMUSTEXIST
            },
        ..Default::default()
    };
    if unsafe {
        if save {
            GetSaveFileNameW(&mut dialog)
        } else {
            GetOpenFileNameW(&mut dialog)
        }
    } == 0
    {
        let error = unsafe { CommDlgExtendedError() };
        return if error == 0 {
            Ok(None)
        } else {
            Err(io::Error::other(format!("File dialog failed ({error:#x})")))
        };
    }
    let end = path
        .iter()
        .position(|&unit| unit == 0)
        .unwrap_or(path.len());
    Ok(Some(std::ffi::OsString::from_wide(&path[..end]).into()))
}

fn cycle_controls(ui: &mut Ui, cycle: &mut Vec<Language>, minimum: usize) {
    let choices = available_choices();
    let visible: Vec<_> = cycle
        .iter()
        .enumerate()
        .filter(|(_, layout)| !cfg!(windows) || choices.contains(layout))
        .map(|(index, _)| index)
        .collect();
    let mut move_up = None;
    let mut remove = None;
    for (position, &index) in visible.iter().enumerate() {
        ui.horizontal(|ui| {
            ui.label(cycle[index].name());
            if ui
                .add_enabled(
                    position != 0,
                    egui::Button::new(tr("rust.common.up", "Выше")),
                )
                .clicked()
            {
                move_up = Some((index, visible[position - 1]));
            }
            if ui
                .add_enabled(
                    visible.len() > minimum,
                    egui::Button::new(tr("rust.common.remove", "Убрать")),
                )
                .clicked()
            {
                remove = Some(index);
            }
        });
    }
    if let Some((index, previous)) = move_up {
        cycle.swap(index, previous);
    }
    if let Some(index) = remove {
        cycle.remove(index);
    }
    ui.horizontal_wrapped(|ui| {
        for language in choices {
            if !cycle.contains(&language) && ui.button(format!("+ {}", language.name())).clicked() {
                cycle.push(language);
            }
        }
    });
}

fn card(ui: &Ui) -> Frame {
    let _ = ui;
    Frame::new()
}

fn available_choices() -> Vec<Language> {
    let choices = config::available_layouts();
    if choices.is_empty() {
        Language::ALL.to_vec()
    } else {
        choices
    }
}
