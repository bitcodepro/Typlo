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

#[cfg(windows)]
fn diagnostic_clock(timestamp_ms: u64) -> Option<String> {
    use windows_sys::Win32::{
        Foundation::{FILETIME, SYSTEMTIME},
        System::Time::{FileTimeToSystemTime, SystemTimeToTzSpecificLocalTimeEx},
    };

    const UNIX_EPOCH_FILETIME: u64 = 116_444_736_000_000_000;
    let ticks = timestamp_ms
        .checked_mul(10_000)?
        .checked_add(UNIX_EPOCH_FILETIME)?;
    let file_time = FILETIME {
        dwLowDateTime: ticks as u32,
        dwHighDateTime: (ticks >> 32) as u32,
    };
    let mut utc = SYSTEMTIME::default();
    let mut local = SYSTEMTIME::default();
    if unsafe { FileTimeToSystemTime(&file_time, &mut utc) } == 0
        || unsafe { SystemTimeToTzSpecificLocalTimeEx(std::ptr::null(), &utc, &mut local) } == 0
    {
        return None;
    }
    Some(format!(
        "{:02}:{:02}:{:02}",
        local.wHour, local.wMinute, local.wSecond
    ))
}

#[cfg(windows)]
struct SettingsDiagnostics {
    report: typlo_rust::windows::DiagnosticReport,
    attempt_clocks: Vec<Option<String>>,
}

#[cfg(windows)]
impl SettingsDiagnostics {
    fn new(report: typlo_rust::windows::DiagnosticReport) -> Self {
        let attempt_clocks = report
            .automatic
            .iter()
            .map(|attempt| diagnostic_clock(attempt.timestamp_ms))
            .collect();
        Self {
            report,
            attempt_clocks,
        }
    }
}

fn main() -> Result<(), Box<dyn std::error::Error>> {
    run().inspect_err(|error| {
        typlo_rust::runtime_log::write("ERROR", format_args!("UI failed: {error}"));
    })
}

fn ui_profile(path: Option<std::ffi::OsString>) -> std::io::Result<PathBuf> {
    let default = config::profile();
    let profile = path.map_or_else(|| default.clone(), PathBuf::from);
    #[cfg(windows)]
    if profile == default {
        typlo_rust::windows::prepare_portable_profile(&profile)?;
    }
    typlo_rust::runtime_log::initialize(&profile, "ui");
    Ok(profile)
}

fn run() -> Result<(), Box<dyn std::error::Error>> {
    let mut args = std::env::args_os().skip(1);
    let mode = args.next();
    #[cfg(unix)]
    if mode
        .as_deref()
        .is_some_and(|mode| mode == "--layout-choice-preview")
    {
        let profile = ui_profile(args.next())?;
        return typlo_rust::selection_tools_view::run_preview(profile);
    }
    #[cfg(any(windows, target_os = "linux", target_os = "macos"))]
    if mode
        .as_deref()
        .is_some_and(|mode| mode == "--selection-tools")
    {
        let profile = ui_profile(args.next())?;
        return typlo_rust::selection_tools_view::run(profile);
    }
    #[cfg(any(target_os = "linux", target_os = "macos"))]
    if mode.as_deref().is_some_and(|mode| mode == "--history") {
        let profile = ui_profile(args.next())?;
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
            let profile = ui_profile(args.next())?;
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
            let profile = ui_profile(args.next())?;
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
            let profile = ui_profile(args.next())?;
            let for_save = args.next().is_some_and(|arg| arg == "file");
            return typlo_rust::capture_view::run_editor(&profile, for_save);
        }
    }
    if mode
        .as_deref()
        .is_some_and(|arg| arg == "--spelling-review")
    {
        let profile = ui_profile(args.next())?;
        return typlo_rust::spelling_view::run(profile);
    }
    // Started by the update dialog of the previous version.
    let updated = mode
        .as_deref()
        .is_some_and(|arg| arg == "--updated")
        .then(|| args.next()?.into_string().ok())
        .flatten();
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
    let profile = ui_profile(args.next())?;
    let ready_signal = updated
        .is_some()
        .then(|| args.next().map(PathBuf::from))
        .flatten();
    let report = report.map(|report| UiProbe {
        report,
        frame: args.next().map(PathBuf::from),
        requested: false,
    });
    // A probe can also pick the Settings section and window width, to check
    // each section and the narrow layout.
    let probe_tab = report
        .is_some()
        .then(|| args.next()?.to_str()?.parse::<usize>().ok())
        .flatten()
        .and_then(|index| SettingsTab::ALL.get(index).copied());
    let probe_width = report
        .is_some()
        .then(|| args.next()?.to_str()?.parse::<f32>().ok())
        .flatten();
    let mut workspace = Workspace::new(profile, report, page);
    if let Some(version) = updated {
        workspace.status = tr("rust.update.done", "Typlo updated to v{0}").replace("{0}", &version);
    }
    workspace.ready_signal = ready_signal;
    if let Some(tab) = probe_tab {
        workspace.tab = tab;
    }
    let size = page.size();
    let size = [probe_width.unwrap_or(size[0]), size[1]];
    let options = eframe::NativeOptions {
        viewport: typlo_rust::appearance::window()
            .with_inner_size(size)
            .with_min_inner_size(if page == Page::Settings {
                [640., 480.]
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

#[derive(Clone, Copy, Default, PartialEq, Eq, Hash)]
enum SettingsTab {
    #[default]
    General,
    Text,
    Shortcuts,
    Applications,
    Clipboard,
    #[cfg(windows)]
    Computers,
    Snippets,
    #[cfg(windows)]
    Capture,
    Dictionary,
    Backup,
    #[cfg(windows)]
    Diagnostics,
}

impl SettingsTab {
    const ALL: &'static [Self] = &[
        Self::General,
        Self::Text,
        Self::Shortcuts,
        Self::Dictionary,
        Self::Applications,
        Self::Clipboard,
        Self::Snippets,
        #[cfg(windows)]
        Self::Capture,
        #[cfg(windows)]
        Self::Computers,
        Self::Backup,
        #[cfg(windows)]
        Self::Diagnostics,
    ];

    fn label(self) -> &'static str {
        match self {
            Self::General => tr("rust.tabs.general", "General"),
            Self::Text => tr("rust.tabs.text", "Text correction"),
            Self::Shortcuts => tr("rust.tabs.shortcuts", "Shortcuts"),
            Self::Applications => tr("rust.tabs.apps", "Apps"),
            Self::Clipboard => tr("rust.tabs.clipboard", "Clipboard"),
            #[cfg(windows)]
            Self::Computers => tr("rust.tabs.computers", "Computers"),
            Self::Snippets => tr("rust.tabs.snippets", "Snippets"),
            #[cfg(windows)]
            Self::Capture => tr("rust.tabs.capture", "Screenshots"),
            Self::Dictionary => tr("rust.tabs.dictionary", "Dictionary"),
            Self::Backup => tr("rust.tabs.backup", "Backup"),
            #[cfg(windows)]
            Self::Diagnostics => tr("rust.tabs.diagnostics", "Diagnostics"),
        }
    }

    fn icon(self) -> &'static str {
        match self {
            Self::General => "\u{2699}",
            Self::Text => "\u{270E}",
            Self::Shortcuts => "\u{2328}",
            Self::Applications => "\u{25A6}",
            Self::Clipboard => "\u{1F4CB}",
            #[cfg(windows)]
            Self::Computers => "\u{1F5A7}",
            Self::Snippets => "\u{2702}",
            #[cfg(windows)]
            Self::Capture => "\u{1F4F7}",
            Self::Dictionary => "\u{1F4D6}",
            Self::Backup => "\u{1F4BE}",
            #[cfg(windows)]
            Self::Diagnostics => "\u{1F527}",
        }
    }

    /// The sidebar section the tab is listed under.
    fn group(self) -> NavGroup {
        match self {
            Self::General | Self::Text | Self::Shortcuts | Self::Applications => NavGroup::Typing,
            Self::Dictionary => NavGroup::Typing,
            Self::Clipboard | Self::Snippets => NavGroup::Clipboard,
            #[cfg(windows)]
            Self::Computers | Self::Capture => NavGroup::Clipboard,
            Self::Backup => NavGroup::Data,
            #[cfg(windows)]
            Self::Diagnostics => NavGroup::Data,
        }
    }

    fn description(self) -> &'static str {
        match self {
            Self::General => tr(
                "rust.nav.generalHint",
                "Language, theme and how Typlo starts with the system.",
            ),
            Self::Text => tr(
                "rust.nav.textHint",
                "How Typlo fixes the keyboard layout of typed and selected text.",
            ),
            Self::Shortcuts => tr(
                "rust.nav.shortcutsHint",
                "Key combinations for every Typlo action.",
            ),
            Self::Applications => tr(
                "rust.nav.appsHint",
                "The keyboard layout Typlo switches to in each application.",
            ),
            Self::Clipboard => tr(
                "rust.nav.clipboardHint",
                "What clipboard history keeps and how pasting from it works.",
            ),
            #[cfg(windows)]
            Self::Computers => tr(
                "rust.nav.computersHint",
                "Share the clipboard with another computer on your network.",
            ),
            Self::Snippets => tr(
                "rust.nav.snippetsHint",
                "Ready-made text you insert from the snippet menu.",
            ),
            #[cfg(windows)]
            Self::Capture => tr(
                "rust.nav.captureHint",
                "Capture an area of the screen, then copy, save or edit it.",
            ),
            Self::Dictionary => tr(
                "rust.nav.dictionaryHint",
                "Your own words for layout and spelling fixes, and words never to change.",
            ),
            Self::Backup => tr(
                "rust.nav.backupHint",
                "Export your settings and personal data to a file, or restore them.",
            ),
            #[cfg(windows)]
            Self::Diagnostics => tr(
                "rust.nav.diagnosticsHint",
                "Checks and logs that help find out why something does not work.",
            ),
        }
    }
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum NavGroup {
    Typing,
    Clipboard,
    Data,
}

impl NavGroup {
    fn label(self) -> &'static str {
        match self {
            Self::Typing => tr("rust.nav.typing", "Typing"),
            Self::Clipboard => tr("rust.nav.clipboard", "Clipboard and screen"),
            Self::Data => tr("rust.nav.data", "Data and support"),
        }
    }
}

impl Page {
    fn title(self) -> &'static str {
        match self {
            Self::Settings => tr("settings.title", "Typlo — settings"),
            Self::Shortcuts => tr("shortcuts.title", "Typlo — keyboard shortcuts"),
            Self::Dictionary => tr("manage.title", "Personal dictionary"),
            Self::Tools => tr("tools.title", "Typlo tools"),
            Self::Update => tr("update.dialogTitle", "Typlo updates"),
        }
    }

    fn size(self) -> [f32; 2] {
        match self {
            Self::Settings => [900., 720.],
            Self::Shortcuts => [560., 640.],
            Self::Tools => [850., 670.],
            Self::Dictionary => [680., 650.],
            Self::Update => [480., 250.],
        }
    }
}

struct Workspace {
    page: Page,
    tab: SettingsTab,
    settings: Config,
    /// Settings as last loaded or saved, used to detect unsaved edits.
    saved: Option<serde_json::Value>,
    confirm_close: bool,
    closing: bool,
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
    diagnostics: Option<SettingsDiagnostics>,
    #[cfg(any(windows, target_os = "linux", target_os = "macos"))]
    /// The update dialog, while it is open.
    updates: Option<typlo_rust::update_view::UpdateView>,
    #[cfg(any(windows, target_os = "linux", target_os = "macos"))]
    /// Settings of the new version, until its window is on screen.
    reopening: Option<typlo_rust::update_view::Reopening>,
    /// Set when the previous version waits for this window to appear.
    ready_signal: Option<PathBuf>,
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
        // Each refresh opens the history database, so poll gently.
        self.next_refresh = Some(now + Duration::from_secs(5));
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
            // The bottom-right corner is window background; the top-left is the sidebar.
            let [red, green, blue, _] = image.pixels[width * height - 1].to_array();
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
            return tr(
                "rust.settings.autostartFailed",
                "{0}. Could not apply autostart: {1}",
            )
            .replace("{0}", success)
            .replace("{1}", &error.to_string());
        }
        success.into()
    }

    fn new(profile: PathBuf, report: Option<UiProbe>, page: Page) -> Self {
        let (settings, repaired, status) = match config::load_repaired(&profile) {
            Ok((settings, repaired)) => (settings, repaired, String::new()),
            Err(error) => (Config::default(), Vec::new(), error.to_string()),
        };
        locale::set_language(&settings.ui_language);
        let status = if repaired.is_empty() {
            status
        } else {
            tr(
                "rust.settings.repaired",
                "Some saved settings were invalid and were reset: {0}. Save to keep the fix.",
            )
            .replace(
                "{0}",
                &repaired
                    .iter()
                    .map(|part| part.label())
                    .collect::<Vec<_>>()
                    .join(", "),
            )
        };
        // A repaired profile differs from the file, so Save stays available.
        let saved = repaired
            .is_empty()
            .then(|| serde_json::to_value(&settings).ok())
            .flatten();
        let dictionary = DictionaryView::new(&profile);
        #[cfg(windows)]
        let lan_clipboard = typlo_rust::lan_clipboard_view::Settings::new(&profile);
        #[cfg(windows)]
        let diagnostics = (page == Page::Tools)
            .then(|| typlo_rust::windows::resident_diagnostics(&profile, false))
            .and_then(Result::ok)
            .map(SettingsDiagnostics::new);
        Self {
            page,
            saved,
            confirm_close: false,
            closing: false,
            tab: SettingsTab::default(),
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
            updates: (page == Page::Update).then(|| typlo_rust::update_view::UpdateView::new(true)),
            #[cfg(any(windows, target_os = "linux", target_os = "macos"))]
            reopening: None,
            ready_signal: None,
        }
    }

    fn navigate(&mut self, ui: &Ui, page: Page) {
        self.page = page;
        #[cfg(windows)]
        if page == Page::Tools {
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
        let lock_shortcut = if cfg!(target_os = "macos") {
            "⌃⌘Q"
        } else if cfg!(windows) {
            "Win+L"
        } else {
            "Super+L"
        };
        hint(
            ui,
            &tr(
                "settings.keepAwakeHint",
                "Keep the display on while Typlo is running.",
            )
            .replace("{0}", lock_shortcut),
        );
    }

    /// Vertical navigation: grouped sections with an icon each. Narrow
    /// windows keep only the icons, with the name as a tooltip.
    fn settings_navigation(&mut self, ui: &mut Ui) {
        let previous = self.tab;
        let compact = ui.available_width() < 760.;
        let width = if compact { 64. } else { 232. };
        let fill = if ui.visuals().dark_mode {
            typlo_rust::appearance::rgb(0x1a1a1a)
        } else {
            typlo_rust::appearance::rgb(0xf0f1f4)
        };
        egui::Panel::left("settings-navigation")
            .exact_size(width)
            .resizable(false)
            .show_separator_line(true)
            .frame(Frame::new().fill(fill))
            .show_inside(ui, |ui| {
                egui::ScrollArea::vertical()
                    .id_salt("settings-navigation-scroll")
                    .show(ui, |ui| {
                        Frame::new()
                            .inner_margin(Margin::symmetric(if compact { 8 } else { 12 }, 16))
                            .show(ui, |ui| {
                                ui.set_width(ui.available_width());
                                if !compact {
                                    ui.horizontal(|ui| {
                                        ui.add_space(6.);
                                        ui.label(RichText::new("Typlo").size(20.).strong());
                                    });
                                    ui.add_space(10.);
                                }
                                let mut group = None;
                                for &tab in SettingsTab::ALL {
                                    if group != Some(tab.group()) {
                                        let first = group.is_none();
                                        if !first {
                                            ui.add_space(10.);
                                        }
                                        group = Some(tab.group());
                                        if compact {
                                            if !first {
                                                ui.separator();
                                            }
                                        } else {
                                            ui.horizontal(|ui| {
                                                ui.add_space(6.);
                                                ui.label(
                                                    RichText::new(tab.group().label())
                                                        .size(12.)
                                                        .color(ui.visuals().weak_text_color()),
                                                );
                                            });
                                            ui.add_space(2.);
                                        }
                                    }
                                    if navigation_item(ui, tab, self.tab == tab, compact).clicked()
                                    {
                                        self.tab = tab;
                                    }
                                }
                            });
                    });
            });
        #[cfg(windows)]
        if self.tab == SettingsTab::Diagnostics && self.tab != previous {
            self.refresh_diagnostics(false);
        }
        if self.tab != previous {
            ui.ctx().request_repaint();
        }
    }

    fn settings_content(&mut self, ui: &mut Ui) {
        ui.add_space(10.);
        ui.label(RichText::new(self.tab.label()).size(24.).strong());
        ui.add_space(2.);
        ui.label(
            RichText::new(self.tab.description())
                .size(13.)
                .color(ui.visuals().weak_text_color()),
        );
        ui.add_space(8.);
        ui.separator();
        match self.tab {
            SettingsTab::General => self.preferences(ui),
            SettingsTab::Text => {
                heading(
                    ui,
                    tr("settings.layoutHeader", "Fix the layout of selected text"),
                );
                self.conversion_options(ui);
            }
            SettingsTab::Shortcuts => {
                heading(ui, tr("shortcuts.global", "Global shortcuts"));
                self.hotkeys(ui);
                if ui
                    .button(tr("rust.tabs.shortcutReference", "Shortcut reference"))
                    .clicked()
                {
                    self.navigate(ui, Page::Shortcuts);
                }
            }
            SettingsTab::Applications => self.applications(ui),
            SettingsTab::Clipboard => self.clipboard_history(ui),
            #[cfg(windows)]
            SettingsTab::Computers => {
                immediate_hint(ui);
                heading(
                    ui,
                    tr(
                        "rust.lan.heading",
                        "Clipboard sharing with another computer",
                    ),
                );
                self.lan_clipboard.show(ui, &self.profile);
            }
            SettingsTab::Snippets => self.snippets(ui),
            #[cfg(windows)]
            SettingsTab::Capture => self.screenshots(ui),
            SettingsTab::Dictionary => {
                immediate_hint(ui);
                self.dictionary.show(ui);
            }
            SettingsTab::Backup => {
                immediate_hint(ui);
                heading(ui, tr("rust.tabs.settingsFile", "Settings file"));
                self.backup(ui, false);
                self.backup(ui, true);
            }
            #[cfg(windows)]
            SettingsTab::Diagnostics => self.diagnostics_content(ui),
        }
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
            #[cfg(windows)]
            {
                self.tab = SettingsTab::Diagnostics;
                self.refresh_diagnostics(false);
            }
            #[cfg(not(windows))]
            {
                self.tab = SettingsTab::Backup;
            }
        }
        if ui
            .button(tr("manage.title", "Personal dictionary"))
            .clicked()
        {
            self.tab = SettingsTab::Dictionary;
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
                if !layouts.is_empty() {
                    self.settings
                        .cycle
                        .retain(|layout| layouts.contains(layout));
                    for layout in layouts {
                        if !self.settings.cycle.contains(&layout) {
                            self.settings.cycle.push(layout);
                        }
                    }
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
                        let buttons: &[_] = if cfg!(windows) {
                            &[tr("appLayouts.choose", "Choose…"), "✕"]
                        } else {
                            &["✕"]
                        };
                        let (_, clicked) = field_with_actions(
                            ui,
                            typlo_rust::appearance::text_input(&mut rule.process)
                                .hint_text(tr("appLayouts.process", "Process name")),
                            buttons,
                        );
                        match clicked {
                            #[cfg(windows)]
                            Some(0) => choose = Some(index),
                            Some(_) => remove = Some(index),
                            None => {}
                        }
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
                            ui.horizontal_wrapped(|ui| {
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
                    let (_, clicked) = field_with_actions(
                        ui,
                        typlo_rust::appearance::text_input(&mut snippet.trigger).hint_text(";addr"),
                        &[tr("history.ctx.delete", "Delete")],
                    );
                    if clicked.is_some() {
                        remove = Some(index);
                    }
                    ui.add(
                        typlo_rust::appearance::text_area(&mut snippet.text)
                            .hint_text(tr("settings.snippetText", "Text to insert"))
                            .desired_rows(3)
                            .clip_text(true)
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

    fn backup(&mut self, ui: &mut Ui, full: bool) {
        if full {
            heading(ui, tr("rust.backup.heading", "Backup"));
        }
        #[cfg(windows)]
        ui.horizontal_wrapped(|ui| {
            for (save, label) in [
                (true, tr("settings.export", "Export settings…")),
                (false, tr("settings.import", "Import settings…")),
            ] {
                if ui.button(label).clicked() {
                    match settings_file(save, full) {
                        Ok(Some(path)) => self.backup_selected(path, save, full),
                        Ok(None) => {}
                        Err(error) => self.status = error.to_string(),
                    }
                }
            }
        });
        #[cfg(not(windows))]
        ui.horizontal_wrapped(|ui| {
            ui.add(
                typlo_rust::appearance::text_input(&mut self.backup_path).hint_text(if full {
                    tr("rust.backup.path", "Path to .typlo file")
                } else {
                    "typlo-settings.json"
                }),
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
                    self.backup_selected(path.clone(), save, full);
                }
            }
        });
    }

    fn confirm_backup_restore(&mut self, ui: &Ui) {
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
            let response = egui::Modal::new(egui::Id::new("restore-backup")).show(ui.ctx(), |ui| {
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
                                self.saved = serde_json::to_value(&self.settings).ok();
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

    /// Writes the settings; returns whether they were saved.
    fn save(&mut self, ui: &Ui) -> bool {
        let saved = config::save(&self.profile, &self.settings);
        let ok = saved.is_ok();
        match saved {
            Ok(()) => {
                #[cfg(windows)]
                typlo_rust::windows::reload_settings();
                self.saved = serde_json::to_value(&self.settings).ok();
                self.status = self.saved_status(tr("rust.settings.saved", "Settings saved"));
                if self.closing {
                    ui.ctx().send_viewport_cmd(egui::ViewportCommand::Close);
                }
            }
            Err(error) => {
                self.closing = false;
                self.status = error.to_string();
            }
        }
        ok
    }

    /// Closes the window, or asks first when settings have unsaved edits.
    /// A `Close` command on the root viewport closes the app without a new
    /// close request, so buttons must not send it directly.
    #[cfg(any(windows, target_os = "linux", target_os = "macos"))]
    fn update_outcome(&mut self, ui: &Ui, outcome: typlo_rust::update_view::Outcome) {
        use typlo_rust::update_view::Outcome;
        match outcome {
            Outcome::Open => {}
            Outcome::Closed if self.page == Page::Update => self.request_close(ui.ctx()),
            Outcome::Closed => self.updates = None,
            Outcome::Restarted(version) => {
                self.updates = None;
                // Save edits before the new window reads the settings; when
                // saving fails, stay open so the edits and the error remain.
                if self.dirty() && !self.save(ui) {
                    return;
                }
                // This window closes only once the new one is on screen.
                match typlo_rust::update_view::reopen_settings(&version, &self.profile) {
                    Ok(reopening) => {
                        self.status = tr("rust.update.done", "Typlo updated to v{0}")
                            .replace("{0}", &version);
                        self.reopening = Some(reopening);
                        self.poll_reopening(ui.ctx());
                    }
                    Err(error) => self.reopen_failed(&version, &error.to_string()),
                }
            }
        }
    }

    /// Closes this window when the new Settings show their first frame, or
    /// stays open with the error when they cannot.
    #[cfg(any(windows, target_os = "linux", target_os = "macos"))]
    fn poll_reopening(&mut self, ctx: &egui::Context) {
        use typlo_rust::update_view::ReopenState;
        let Some(reopening) = &mut self.reopening else {
            return;
        };
        match reopening.poll() {
            ReopenState::Waiting => {
                ctx.request_repaint_after(std::time::Duration::from_millis(200));
            }
            ReopenState::Ready => {
                // Editing is locked while waiting; the normal close path
                // still asks if anything was changed.
                self.reopening = None;
                self.request_close(ctx);
            }
            ReopenState::Failed(error) => {
                let version = reopening.version().to_owned();
                self.reopening = None;
                self.reopen_failed(&version, &error);
            }
        }
    }

    #[cfg(any(windows, target_os = "linux", target_os = "macos"))]
    fn reopen_failed(&mut self, version: &str, error: &str) {
        typlo_rust::runtime_log::write(
            "ERROR",
            format_args!("Could not reopen settings after the update: {error}"),
        );
        self.status = tr(
            "rust.update.reopenFailed",
            "Typlo updated to v{0}, but Settings could not reopen: {1}",
        )
        .replace("{0}", version)
        .replace("{1}", error);
    }

    fn request_close(&mut self, ctx: &egui::Context) {
        if self.dirty() && !self.closing {
            self.confirm_close = true;
        } else {
            self.closing = true;
            ctx.send_viewport_cmd(egui::ViewportCommand::Close);
        }
    }

    fn dirty(&self) -> bool {
        serde_json::to_value(&self.settings).ok() != self.saved
    }

    fn unsaved_changes(&mut self, ui: &Ui) {
        let response = egui::Modal::new(ui.id().with("unsaved-changes")).show(ui.ctx(), |ui| {
            ui.heading(tr("settings.unsavedTitle", "Save changes?"));
            ui.label(tr(
                "settings.unsavedBody",
                "You have changed settings that are not saved yet.",
            ));
            ui.add_space(8.);
            ui.horizontal(|ui| {
                if ui.button(tr("settings.save", "Save")).clicked() {
                    self.confirm_close = false;
                    self.closing = true;
                    self.save(ui);
                }
                if ui.button(tr("settings.discard", "Don't save")).clicked() {
                    self.confirm_close = false;
                    self.closing = true;
                    ui.ctx().send_viewport_cmd(egui::ViewportCommand::Close);
                }
                if ui
                    .button(tr("settings.keepEditing", "Keep editing"))
                    .clicked()
                {
                    self.confirm_close = false;
                }
            });
        });
        if response.should_close() {
            self.confirm_close = false;
        }
    }

    #[cfg(windows)]
    fn refresh_diagnostics(&mut self, retry: bool) {
        self.resident_memory = typlo_rust::windows::resident_memory().ok();
        match typlo_rust::windows::resident_diagnostics(&self.profile, retry) {
            Ok(report) => self.diagnostics = Some(SettingsDiagnostics::new(report)),
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
                hint(
                    ui,
                    tr(
                        "rust.tabs.diagnosticsHint",
                        "Recent attempts after a space. A dash means no replacement was made.",
                    ),
                );
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
                let Some(diagnostics) = &self.diagnostics else {
                    ui.weak(tr(
                        "rust.tools.noDiagnostics",
                        "Typlo diagnostics are unavailable.",
                    ));
                    return;
                };
                if let Some(healthy) = diagnostics.report.dictionary_healthy {
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
                        for (attempt, clock) in diagnostics
                            .report
                            .automatic
                            .iter()
                            .zip(&diagnostics.attempt_clocks)
                        {
                            ui.label(format!(
                                "{} · {:.2} ms",
                                clock.as_deref().unwrap_or("—"),
                                attempt.elapsed_us as f64 / 1000.,
                            ))
                            .on_hover_text(format!(
                                "{} → {} · {}",
                                code(attempt.source),
                                code(attempt.target),
                                attempt.reason.message()
                            ));
                        }
                    });
            },
        );
    }

    #[cfg(windows)]
    fn diagnostics_content(&mut self, ui: &mut Ui) {
        heading(ui, tr("tools.measurements", "Measurements"));
        self.performance_report(ui);
        self.automatic_diagnostics(ui);
        if ui
            .button(tr("rust.logs.open", "Open diagnostic logs"))
            .clicked()
            && let Err(error) = open_diagnostic_logs(&self.profile)
        {
            self.status = error.to_string();
        }
        hint(
            ui,
            tr(
                "tools.noProbe",
                "Run the field compatibility shortcut in the target editor to inspect it.",
            ),
        );
        shortcut_row(
            ui,
            &self.settings.hotkeys.tools,
            tr("tools.title", "Typlo tools"),
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
        if let Some(diagnostics) = &self.diagnostics {
            let report = &diagnostics.report;
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
        self.frame(ui);
    }
}

impl Workspace {
    fn frame(&mut self, ui: &mut Ui) {
        // The previous version closes its window once this one draws.
        if let Some(path) = self.ready_signal.take() {
            typlo_rust::update_view::signal_ready(&path);
        }
        #[cfg(any(windows, target_os = "linux", target_os = "macos"))]
        {
            self.poll_reopening(ui.ctx());
            // Settings are saved and the new version is opening them; edits
            // made here now would be lost when this window closes.
            if self.reopening.is_some() {
                ui.disable();
            }
        }
        let page = self.page;
        let dirty = self.dirty();
        // Every way of closing (title bar, Cancel, Close on another page) keeps unsaved edits.
        if ui.ctx().input(|input| input.viewport().close_requested()) && dirty && !self.closing {
            ui.ctx()
                .send_viewport_cmd(egui::ViewportCommand::CancelClose);
            self.confirm_close = true;
        }
        if self.confirm_close {
            self.unsaved_changes(ui);
        }
        #[cfg(any(windows, target_os = "linux", target_os = "macos"))]
        if page == Page::Settings
            && let Some(view) = &mut self.updates
        {
            let installing = view.is_installing();
            let modal =
                egui::Modal::new(egui::Id::new("update-dialog")).show(ui.ctx(), |ui| view.show(ui));
            let outcome = if modal.should_close() && !installing {
                typlo_rust::update_view::Outcome::Closed
            } else {
                modal.inner
            };
            self.update_outcome(ui, outcome);
        }
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
                                            Some(typlo_rust::update_view::UpdateView::new(true));
                                    }
                                }
                                if page == Page::Settings {
                                    hint(ui, concat!("v", env!("CARGO_PKG_VERSION")));
                                }
                                if !self.status.is_empty() {
                                    hint(ui, &self.status);
                                }
                                ui.with_layout(
                                    egui::Layout::right_to_left(egui::Align::Center),
                                    |ui| {
                                        if page == Page::Settings {
                                            // Accent only when there is something to save:
                                            // white on a faded accent is unreadable.
                                            let save = if dirty {
                                                typlo_rust::appearance::primary_button(tr(
                                                    "settings.save",
                                                    "Save",
                                                ))
                                            } else {
                                                egui::Button::new(tr("settings.save", "Save"))
                                            };
                                            if ui.add_enabled(dirty, save).clicked() {
                                                self.save(ui);
                                            }
                                            if ui.button(tr("settings.cancel", "Cancel")).clicked()
                                            {
                                                self.request_close(ui.ctx());
                                            }
                                        } else {
                                            if ui.button(tr("update.close", "Close")).clicked() {
                                                self.request_close(ui.ctx());
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
        if page == Page::Settings {
            self.settings_navigation(ui);
        }
        let scroll = egui::ScrollArea::vertical();
        let scroll = if page == Page::Settings {
            scroll.id_salt(self.tab)
        } else {
            scroll.id_salt(page.title())
        };
        scroll.show(ui, |ui| {
            Frame::new()
                .inner_margin(if page == Page::Settings {
                    Margin {
                        left: 28,
                        right: 28,
                        top: 12,
                        bottom: 16,
                    }
                } else {
                    Margin::symmetric(20, 6)
                })
                .show(ui, |ui| {
                    // Long Settings pages stay readable on wide windows.
                    let width = if page == Page::Settings {
                        ui.available_width().min(720.)
                    } else {
                        ui.available_width()
                    };
                    ui.set_min_width(width);
                    ui.set_max_width(width);
                    match page {
                        Page::Settings => self.settings_content(ui),
                        Page::Shortcuts => self.shortcuts(ui),
                        Page::Dictionary => self.dictionary.show(ui),
                        Page::Tools => {
                            #[cfg(windows)]
                            self.diagnostics_content(ui);
                            self.backup(ui, true);
                        }
                        Page::Update => {
                            #[cfg(any(windows, target_os = "linux", target_os = "macos"))]
                            if let Some(view) = &mut self.updates {
                                let outcome = view.show(ui);
                                self.update_outcome(ui, outcome);
                            } else {
                                // Settings of the new version did not open.
                                ui.add(egui::Label::new(self.status.as_str()).wrap());
                                ui.add_space(12.);
                                if ui.button(tr("update.close", "Close")).clicked() {
                                    self.request_close(ui.ctx());
                                }
                            }
                        }
                    }
                });
        });
        self.confirm_backup_restore(ui);
        if self
            .report
            .as_mut()
            .is_some_and(|report| report.finish(ui.ctx(), page))
        {
            self.report = None;
        }
    }
}

#[cfg(windows)]
fn open_diagnostic_logs(profile: &std::path::Path) -> std::io::Result<()> {
    let directory = profile.join("logs");
    std::fs::create_dir_all(&directory)?;
    let explorer = std::env::var_os("SystemRoot")
        .ok_or_else(|| std::io::Error::other("Windows directory is unavailable"))?;
    std::process::Command::new(PathBuf::from(explorer).join("explorer.exe"))
        .arg(directory)
        .spawn()?;
    Ok(())
}

fn navigation_item(ui: &mut Ui, tab: SettingsTab, selected: bool, compact: bool) -> egui::Response {
    let height = 36.;
    let (rect, response) = ui.allocate_exact_size(
        egui::vec2(ui.available_width(), height),
        egui::Sense::click(),
    );
    let visuals = ui.visuals();
    let accent = visuals.selection.stroke.color;
    let text = visuals.widgets.noninteractive.fg_stroke.color;
    let painter = ui.painter();
    if selected {
        painter.rect_filled(rect, 8, accent.gamma_multiply(0.16));
        let bar = egui::Rect::from_min_size(
            rect.left_top() + egui::vec2(0., 9.),
            egui::vec2(3., height - 18.),
        );
        painter.rect_filled(bar, 2, accent);
    } else if response.hovered() {
        painter.rect_filled(rect, 8, visuals.widgets.hovered.weak_bg_fill);
    }
    // Keyboard focus (Tab) gets a ring, selected or not.
    if response.has_focus() {
        painter.rect_stroke(
            rect.shrink(1.),
            8,
            egui::Stroke::new(2_f32, accent),
            egui::StrokeKind::Inside,
        );
    }
    let color = if selected { accent } else { text };
    let icon_center = if compact {
        rect.center()
    } else {
        egui::pos2(rect.left() + 22., rect.center().y)
    };
    painter.text(
        icon_center,
        egui::Align2::CENTER_CENTER,
        tab.icon(),
        egui::FontId::proportional(16.),
        color,
    );
    if !compact {
        let label = egui::WidgetText::from(
            RichText::new(tab.label())
                .size(14.)
                .color(color)
                .strong_if(selected),
        )
        .into_galley(
            ui,
            Some(egui::TextWrapMode::Truncate),
            rect.width() - 52.,
            egui::TextStyle::Button,
        );
        ui.painter().galley(
            egui::pos2(rect.left() + 42., rect.center().y - label.size().y / 2.),
            label,
            color,
        );
    }
    let response = response.on_hover_cursor(egui::CursorIcon::PointingHand);
    let response = if compact {
        response.on_hover_text(tab.label())
    } else {
        response
    };
    response.widget_info(|| {
        egui::WidgetInfo::selected(
            egui::WidgetType::SelectableLabel,
            true,
            selected,
            tab.label(),
        )
    });
    response
}

trait StrongIf {
    fn strong_if(self, strong: bool) -> Self;
}

impl StrongIf for RichText {
    fn strong_if(self, strong: bool) -> Self {
        if strong { self.strong() } else { self }
    }
}

fn heading(ui: &mut Ui, label: &str) {
    ui.add_space(8.);
    ui.label(RichText::new(label).size(16.).strong());
}

fn immediate_hint(ui: &mut Ui) {
    hint(
        ui,
        tr(
            "rust.tabs.immediate",
            "Changes on this tab apply immediately and do not need Save.",
        ),
    );
}

fn hint(ui: &mut Ui, text: &str) {
    ui.label(
        RichText::new(text)
            .size(12.)
            .color(ui.visuals().weak_text_color()),
    );
}

fn setting_row<R>(ui: &mut Ui, label: &str, input: impl FnOnce(&mut Ui) -> R) -> R {
    let label_width = (ui.available_width() * 0.35).min(182.);
    ui.horizontal(|ui| {
        ui.allocate_ui_with_layout(
            egui::vec2(label_width, 32.),
            egui::Layout::left_to_right(egui::Align::Center),
            |ui| {
                ui.set_width(label_width);
                ui.add(egui::Label::new(label).wrap());
            },
        );
        input(ui)
    })
    .inner
}

fn field_with_actions(
    ui: &mut Ui,
    field: egui::TextEdit<'_>,
    labels: &[&str],
) -> (egui::Response, Option<usize>) {
    let field = field.id(ui.id().with("action-field"));
    let font = egui::TextStyle::Button.resolve(ui.style());
    let padding = 2. * ui.spacing().button_padding.x;
    let spacing = ui.spacing().item_spacing.x;
    let widths: Vec<_> = labels
        .iter()
        .map(|label| {
            ui.fonts_mut(|fonts| {
                fonts
                    .layout_no_wrap((*label).into(), font.clone(), Color32::WHITE)
                    .size()
                    .x
            })
            .ceil()
                + padding
        })
        .collect();
    let available = ui.available_width();
    let inline_width = available - widths.iter().sum::<f32>() - spacing * labels.len() as f32;
    let actions = |ui: &mut Ui| {
        labels
            .iter()
            .zip(widths)
            .enumerate()
            .fold(None, |clicked, (index, (label, width))| {
                let button = ui.add(egui::Button::new(*label).min_size(egui::vec2(width, 0.)));
                if button.clicked() {
                    Some(index)
                } else {
                    clicked
                }
            })
    };
    if inline_width >= 160. {
        ui.horizontal(|ui| {
            let field = ui.add(field.desired_width(inline_width));
            (field, actions(ui))
        })
        .inner
    } else {
        let field = ui.add(field.desired_width(available));
        (field, ui.horizontal_wrapped(actions).inner)
    }
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
        ui.horizontal_wrapped(|ui| {
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

#[cfg(test)]
mod layout_tests {
    use super::*;

    #[test]
    fn close_buttons_ask_before_discarding_unsaved_settings() {
        let folder = tempfile::tempdir().unwrap();
        let context = egui::Context::default();
        let mut workspace = Workspace::new(folder.path().into(), None, Page::Settings);
        workspace.request_close(&context);
        assert!(
            workspace.closing && !workspace.confirm_close,
            "clean settings close at once"
        );

        let mut workspace = Workspace::new(folder.path().into(), None, Page::Update);
        workspace.settings.auto_correct = !workspace.settings.auto_correct;
        workspace.request_close(&context);
        assert!(workspace.confirm_close, "edits ask before closing");
        assert!(!workspace.closing, "edits are not discarded");
    }

    #[test]
    fn settings_stay_open_when_the_new_version_cannot_reopen_them() {
        let folder = tempfile::tempdir().unwrap();
        let mut workspace = Workspace::new(folder.path().into(), None, Page::Settings);
        workspace.settings.auto_correct = !workspace.settings.auto_correct;
        let context = egui::Context::default();
        let _ = context.run_ui(egui::RawInput::default(), |ui| {
            workspace.update_outcome(
                ui,
                typlo_rust::update_view::Outcome::Restarted("0.0.0-missing".into()),
            );
        });
        assert!(!workspace.closing, "the window stays open");
        assert!(!workspace.dirty(), "edits were saved before reopening");
        assert!(
            workspace.status.contains("0.0.0-missing"),
            "the error is shown: {}",
            workspace.status
        );
    }

    /// A process that keeps running without drawing, standing in for the
    /// new Settings window.
    fn silent_child() -> std::process::Child {
        #[cfg(windows)]
        let child = std::process::Command::new("ping")
            .args(["-n", "30", "127.0.0.1"])
            .stdout(std::process::Stdio::null())
            .spawn();
        #[cfg(not(windows))]
        let child = std::process::Command::new("sleep").arg("30").spawn();
        child.unwrap()
    }

    #[test]
    fn settings_are_locked_while_the_new_version_opens_and_unlock_on_failure() {
        let folder = tempfile::tempdir().unwrap();
        let mut workspace = Workspace::new(folder.path().into(), None, Page::Settings);
        let child = silent_child();
        let child_id = child.id().to_string();
        workspace.reopening =
            Some(typlo_rust::update_view::Reopening::for_tests(child, "9.9.9").unwrap());
        let context = egui::Context::default();
        let render = |workspace: &mut Workspace| {
            let mut enabled = None;
            let _ = context.run_ui(egui::RawInput::default(), |ui| {
                workspace.frame(ui);
                enabled = Some(ui.is_enabled());
            });
            enabled.unwrap()
        };
        assert!(!render(&mut workspace), "editing is locked while waiting");
        assert!(!workspace.closing);

        // The new window exits without drawing: editing comes back.
        #[cfg(windows)]
        let killed = std::process::Command::new("taskkill")
            .args(["/F", "/PID", &child_id])
            .output();
        #[cfg(not(windows))]
        let killed = std::process::Command::new("kill")
            .args(["-9", &child_id])
            .output();
        killed.unwrap();
        let deadline = std::time::Instant::now() + std::time::Duration::from_secs(10);
        while workspace.reopening.is_some() && std::time::Instant::now() < deadline {
            render(&mut workspace);
            std::thread::sleep(std::time::Duration::from_millis(50));
        }
        assert!(render(&mut workspace), "editing is back after a failure");
        assert!(workspace.status.contains("9.9.9"), "{}", workspace.status);
        assert!(!workspace.closing);
    }

    #[test]
    fn edits_made_while_waiting_are_not_dropped_on_the_ready_signal() {
        let folder = tempfile::tempdir().unwrap();
        let mut workspace = Workspace::new(folder.path().into(), None, Page::Settings);
        // The ready file is checked first, so a child that already exited
        // stands in for the new window here.
        #[cfg(windows)]
        let child = std::process::Command::new("cmd")
            .args(["/C", "exit 0"])
            .spawn();
        #[cfg(not(windows))]
        let child = std::process::Command::new("true").spawn();
        let reopening =
            typlo_rust::update_view::Reopening::for_tests(child.unwrap(), "9.9.9").unwrap();
        typlo_rust::update_view::signal_ready(&reopening.ready_file());
        workspace.reopening = Some(reopening);
        // An edit that slipped past the lock.
        workspace.settings.auto_correct = !workspace.settings.auto_correct;
        workspace.poll_reopening(&egui::Context::default());
        assert!(workspace.reopening.is_none());
        assert!(!workspace.closing, "unsaved edits keep the window open");
        assert!(workspace.confirm_close, "the user is asked about the edits");
    }

    #[test]
    fn settings_tabs_keep_their_positions_on_hover_and_selection() {
        let folder = tempfile::tempdir().unwrap();
        for language in ["en", "ru", "uk"] {
            let mut workspace = Workspace::new(folder.path().into(), None, Page::Settings);
            locale::set_language(language);
            let context = egui::Context::default();
            typlo_rust::appearance::configure(&context);
            let render = |workspace: &mut Workspace, events| {
                context
                    .run_ui(
                        egui::RawInput {
                            screen_rect: Some(egui::Rect::from_min_size(
                                egui::Pos2::ZERO,
                                egui::vec2(560., 400.),
                            )),
                            events,
                            ..Default::default()
                        },
                        |ui| workspace.settings_navigation(ui),
                    )
                    .shapes
                    .into_iter()
                    .filter_map(|shape| match shape.shape {
                        egui::Shape::Text(text) => Some(text.pos + text.galley.size() * 0.5),
                        _ => None,
                    })
                    .collect::<Vec<_>>()
            };
            let initial = render(&mut workspace, vec![]);
            assert_eq!(initial.len(), SettingsTab::ALL.len());
            for &position in &initial {
                let hover = render(&mut workspace, vec![egui::Event::PointerMoved(position)]);
                assert_eq!(hover, initial, "{language}: hover moved a tab");
            }
            let position = initial[1];
            render(&mut workspace, vec![egui::Event::PointerMoved(position)]);
            for pressed in [true, false] {
                render(
                    &mut workspace,
                    vec![egui::Event::PointerButton {
                        pos: position,
                        button: egui::PointerButton::Primary,
                        pressed,
                        modifiers: egui::Modifiers::NONE,
                    }],
                );
            }
            assert!(workspace.tab == SettingsTab::Text);
            assert_eq!(render(&mut workspace, vec![]), initial);
        }
    }

    #[test]
    fn tab_key_focus_on_the_sidebar_is_visible() {
        let folder = tempfile::tempdir().unwrap();
        locale::set_language("en");
        let mut workspace = Workspace::new(folder.path().into(), None, Page::Settings);
        let context = egui::Context::default();
        typlo_rust::appearance::configure(&context);
        let mut render = |events| {
            context
                .run_ui(
                    egui::RawInput {
                        screen_rect: Some(egui::Rect::from_min_size(
                            egui::Pos2::ZERO,
                            egui::vec2(1000., 700.),
                        )),
                        events,
                        ..Default::default()
                    },
                    |ui| workspace.settings_navigation(ui),
                )
                .shapes
                .into_iter()
                .filter(|shape| {
                    matches!(&shape.shape, egui::Shape::Rect(rect) if rect.stroke.width >= 2.)
                })
                .count()
        };
        assert_eq!(render(vec![]), 0, "no focus ring before Tab");
        let tab = |pressed| egui::Event::Key {
            key: egui::Key::Tab,
            physical_key: None,
            pressed,
            repeat: false,
            modifiers: egui::Modifiers::NONE,
        };
        render(vec![tab(true), tab(false)]);
        assert_eq!(render(vec![]), 1, "the focused item shows a ring");
    }

    #[test]
    fn backup_tab_shows_one_confirmation_for_each_import_format() {
        let folder = tempfile::tempdir().unwrap();
        let source = folder.path().join("source");
        let profile = folder.path().join("destination");
        let imported = Config {
            history_items: 321,
            ..Config::default()
        };
        config::save(&source, &imported).unwrap();
        typlo_rust::dictionary::Dictionary::new(&source)
            .set_correction("teh", "the")
            .unwrap();
        let json = folder.path().join("settings.json");
        let full_backup = folder.path().join("backup.typlo");
        Backup::export_settings(&source, &imported, &json).unwrap();
        Backup::export(&source, &full_backup).unwrap();

        for (path, full) in [(json, false), (full_backup, true)] {
            let original = Config {
                history_items: 123,
                ..Config::default()
            };
            config::save(&profile, &original).unwrap();
            let dictionary = typlo_rust::dictionary::Dictionary::new(&profile);
            dictionary
                .add(
                    &[typlo_rust::dictionary::Entry::new("example", "").unwrap()],
                    &mut typlo_rust::lexicon::Vocabulary::installed().unwrap(),
                )
                .unwrap();
            dictionary.set_correction("teh", "the").unwrap();
            let mut workspace = Workspace::new(profile.clone(), None, Page::Settings);
            workspace.tab = SettingsTab::Backup;
            let context = egui::Context::default();
            let mut time = 0.;
            let mut render = |workspace: &mut Workspace, events| {
                time += 0.25;
                context.run_ui(
                    egui::RawInput {
                        time: Some(time),
                        screen_rect: Some(egui::Rect::from_min_size(
                            egui::Pos2::ZERO,
                            egui::vec2(800., 640.),
                        )),
                        events,
                        ..Default::default()
                    },
                    |ui| {
                        workspace.settings_content(ui);
                        workspace.confirm_backup_restore(ui);
                    },
                )
            };
            for restore in [false, true] {
                workspace.backup_selected(path.clone(), false, full);
                assert_eq!(
                    workspace.pending_backup.as_ref().unwrap().settings_only(),
                    !full
                );
                let label = if restore {
                    tr("tools.restore", "Restore backup")
                } else {
                    tr("settings.cancel", "Cancel")
                };
                // The initial pass sizes the modal before it is painted.
                render(&mut workspace, vec![]);
                let frame = render(&mut workspace, vec![]);
                let backdrop_count = frame
                    .shapes
                    .iter()
                    .filter(|shape| {
                        matches!(&shape.shape, egui::Shape::Rect(rect)
                        if rect.fill == Color32::from_black_alpha(100)
                            && rect.rect.width() >= 799.
                            && rect.rect.height() >= 639.)
                    })
                    .count();
                assert_eq!(backdrop_count, 1);
                let button = frame
                    .shapes
                    .iter()
                    .rev()
                    .find_map(|shape| match &shape.shape {
                        egui::Shape::Text(text) if text.galley.text() == label => {
                            Some(text.pos + text.galley.size() * 0.5)
                        }
                        _ => None,
                    })
                    .expect("confirmation button");
                for pressed in [true, false] {
                    render(
                        &mut workspace,
                        vec![
                            egui::Event::PointerMoved(button),
                            egui::Event::PointerButton {
                                pos: button,
                                button: egui::PointerButton::Primary,
                                pressed,
                                modifiers: egui::Modifiers::NONE,
                            },
                        ],
                    );
                }
                assert!(workspace.pending_backup.is_none());
                let expected = if restore {
                    imported.history_items
                } else {
                    original.history_items
                };
                assert_eq!(workspace.settings.history_items, expected);
                assert_eq!(config::load(&profile).unwrap().history_items, expected);
                assert_eq!(
                    dictionary.correction("teh").unwrap().as_deref(),
                    Some("the")
                );
                assert_eq!(
                    dictionary.is_excluded("example").unwrap(),
                    !restore || !full
                );
            }
        }
    }

    #[test]
    fn settings_fields_and_localized_actions_fit_the_available_width() {
        let context = egui::Context::default();
        let mut text = "long-unbroken-text".repeat(40);
        let mut wide_field = 0.;
        for width in [500., 240., 500.] {
            let input = egui::RawInput {
                screen_rect: Some(egui::Rect::from_min_size(
                    egui::Pos2::ZERO,
                    egui::vec2(width, 400.),
                )),
                ..Default::default()
            };
            let _ = context.run_ui(input, |ui| {
                let right = ui.max_rect().right();
                let (field, _) = field_with_actions(
                    ui,
                    typlo_rust::appearance::text_input(&mut text),
                    &["Choose an application…", "Remove"],
                );
                let template = ui.add(
                    typlo_rust::appearance::text_area(&mut text)
                        .clip_text(true)
                        .desired_width(f32::INFINITY),
                );
                assert!(ui.min_rect().right() <= right + 1.);
                assert!(template.rect.right() <= right + 1.);
                if width == 240. {
                    assert!((field.rect.right() - right).abs() <= 1.);
                } else {
                    assert!(field.rect.right() < right - 40.);
                    assert!(field.rect.width() > 200.);
                    if wide_field > 0. {
                        assert_eq!(field.rect.width(), wide_field);
                    }
                    wide_field = field.rect.width();
                }
            });
        }
    }
}
