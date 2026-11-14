use crate::hotkeys::{Action, Chord, DrawingShortcut, Hotkeys};
use crate::layout::Language;
use crate::snippets::{Snippet, Snippets};
use serde::{Deserialize, Serialize};
use std::{
    fs, io,
    path::{Path, PathBuf},
};

pub const UI_LANGUAGES: [&str; 10] = ["en", "ru", "uk", "de", "es", "pt", "fr", "it", "zh", "ja"];

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum UiTheme {
    #[default]
    System,
    Light,
    Dark,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(default)]
pub struct Config {
    pub ui_language: String,
    pub ui_theme: UiTheme,
    pub cycle: Vec<Language>,
    pub switch_layout: bool,
    pub convert_last_word: bool,
    pub snippets: Vec<Snippet>,
    pub snippets_enabled: bool,
    pub auto_correct: bool,
    pub protect_technical: bool,
    pub capture_shape: CaptureShape,
    pub capture_editor_after_copy: bool,
    pub history_enabled: bool,
    pub history_move_to_top: bool,
    pub history_items: u32,
    pub history_total_mib: u32,
    pub history_image_mib: u32,
    pub history_text_kib: u32,
    pub history_excluded_apps: Vec<String>,
    pub start_with_windows: bool,
    pub keep_screen_on: bool,
    pub applications: Vec<ApplicationRule>,
    pub hotkeys: Hotkeys,
    pub drawing_shortcut: DrawingShortcut,
    pub layout_hotkeys: Vec<LayoutShortcut>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct LayoutShortcut {
    pub layout: Language,
    pub hotkey: String,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum CaptureShape {
    #[default]
    Rectangle,
    Free,
}

impl CaptureShape {
    pub fn arg(self) -> &'static str {
        match self {
            Self::Rectangle => "rectangle",
            Self::Free => "free",
        }
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(default)]
pub struct ApplicationRule {
    pub process: String,
    pub cycle: Vec<Language>,
    pub snippets: Option<bool>,
    pub auto_correct: Option<bool>,
    pub enabled: bool,
}

impl Default for ApplicationRule {
    fn default() -> Self {
        Self {
            process: String::new(),
            cycle: Vec::new(),
            snippets: None,
            auto_correct: None,
            enabled: true,
        }
    }
}

impl Config {
    pub(crate) fn direct_bindings(
        &self,
        other: &[(Action, Chord)],
    ) -> io::Result<Vec<(Language, Chord)>> {
        let mut result = Vec::new();
        let mut languages = std::collections::HashSet::new();
        if self.layout_hotkeys.len() > 128 {
            return Err(io::Error::other("Too many layout shortcuts"));
        }
        for setting in &self.layout_hotkeys {
            if !languages.insert(setting.layout) {
                return Err(io::Error::other("A keyboard layout has multiple shortcuts"));
            }
            if setting.hotkey.trim().is_empty() {
                continue;
            }
            let chord = Chord::parse(&setting.hotkey)
                .filter(|chord| !chord.intercepted())
                .ok_or_else(|| {
                    io::Error::other(format!("Invalid layout shortcut {}", setting.hotkey))
                })?;
            if result.iter().any(|&(_, existing)| existing == chord)
                || other.iter().any(|&(_, existing)| existing == chord)
            {
                return Err(io::Error::other(format!(
                    "Shortcut is already assigned {}",
                    setting.hotkey
                )));
            }
            result.push((setting.layout, chord));
        }
        Ok(result)
    }

    pub fn application_cycle(&self, process: &str) -> Option<&[Language]> {
        self.application_rule(process)
            .and_then(|rule| (!rule.cycle.is_empty()).then_some(rule.cycle.as_slice()))
    }

    fn application_rule(&self, process: &str) -> Option<&ApplicationRule> {
        let process = process_key(process);
        self.applications
            .iter()
            .find(|rule| rule.enabled && process_key(&rule.process) == process)
    }

    pub fn snippets_for(&self, process: &str) -> bool {
        self.application_rule(process)
            .and_then(|rule| rule.snippets)
            .unwrap_or(self.snippets_enabled)
    }

    pub fn auto_correct_for(&self, process: &str) -> bool {
        self.application_rule(process)
            .and_then(|rule| rule.auto_correct)
            .unwrap_or_else(|| {
                self.auto_correct
                    && (!self.protect_technical
                        || !matches!(
                            process_key(process).as_str(),
                            "cmd"
                                | "powershell"
                                | "powershell_ise"
                                | "pwsh"
                                | "windowsterminal"
                                | "windowsterminalpreview"
                                | "bash"
                                | "zsh"
                                | "fish"
                                | "sh"
                                | "terminal"
                                | "gnome-terminal-server"
                                | "konsole"
                                | "alacritty"
                                | "kitty"
                                | "wezterm-gui"
                                | "iterm2"
                        ))
            })
    }

    pub fn history_allowed(&self, process: &str) -> bool {
        let process = process_key(process);
        self.history_enabled
            && !process.is_empty()
            && !self
                .history_excluded_apps
                .iter()
                .any(|excluded| process_key(excluded) == process)
    }

    pub fn cycle_for(&self, process: &str) -> &[Language] {
        self.application_cycle(process).unwrap_or(&self.cycle)
    }

    pub fn layout_on_entry(&self, process: &str, active: Option<Language>) -> Option<Language> {
        let cycle = self.application_cycle(process)?;
        if active.is_some_and(|language| cycle.contains(&language)) {
            return None;
        }
        cycle
            .iter()
            .copied()
            .find(|layout| layout.dictionary_index() == Some(0))
            .or_else(|| cycle.first().copied())
    }
}

fn process_key(value: &str) -> String {
    let name = value
        .trim()
        .rsplit(['/', '\\'])
        .next()
        .unwrap_or_default()
        .to_ascii_lowercase();
    name.trim_suffix(".exe").to_owned()
}

impl Default for Config {
    fn default() -> Self {
        Self {
            ui_language: "en".into(),
            ui_theme: UiTheme::default(),
            cycle: Language::ALL.to_vec(),
            switch_layout: true,
            convert_last_word: true,
            snippets: Vec::new(),
            snippets_enabled: true,
            auto_correct: true,
            protect_technical: true,
            capture_shape: CaptureShape::Rectangle,
            capture_editor_after_copy: false,
            history_enabled: true,
            history_move_to_top: true,
            history_items: 500,
            history_total_mib: 200,
            history_image_mib: 20,
            history_text_kib: 1024,
            history_excluded_apps: ["KeePass", "KeePassXC", "1Password", "Bitwarden"]
                .map(str::to_owned)
                .into(),
            start_with_windows: false,
            keep_screen_on: false,
            applications: Vec::new(),
            hotkeys: Hotkeys::default(),
            drawing_shortcut: DrawingShortcut::default(),
            layout_hotkeys: Vec::new(),
        }
    }
}

#[cfg(windows)]
pub fn profile() -> PathBuf {
    std::env::current_exe()
        .and_then(|exe| crate::windows::update::installation_root_for(&exe))
        .expect("Windows executable path must identify the Typlo installation")
        .join("data")
}

#[cfg(not(windows))]
pub fn profile() -> PathBuf {
    std::env::var_os("XDG_CONFIG_HOME")
        .map(PathBuf::from)
        .or_else(|| {
            std::env::var_os("HOME").map(|home| {
                let home = PathBuf::from(home);
                if cfg!(target_os = "macos") {
                    home.join("Library").join("Application Support")
                } else {
                    home.join(".config")
                }
            })
        })
        .unwrap_or_else(std::env::temp_dir)
        .join("Typlo-Rust")
}

pub fn load(directory: &Path) -> io::Result<Config> {
    let stored = crate::dictionary::Dictionary::new(directory).read_settings()?;
    match stored
        .map(Ok)
        .unwrap_or_else(|| fs::read(directory.join("settings.json")))
    {
        Ok(data) => {
            let mut settings: Config = serde_json::from_slice(&data).map_err(io::Error::other)?;
            #[cfg(windows)]
            add_windows_hotkey_defaults(&mut settings, &data)?;
            resolve_saved_layouts(&mut settings);
            validate(&settings)?;
            Ok(settings)
        }
        Err(error) if error.kind() == io::ErrorKind::NotFound => {
            let mut settings = Config::default();
            let available = available_layouts();
            if !available.is_empty() {
                settings.layout_hotkeys = available
                    .iter()
                    .copied()
                    .take(3)
                    .enumerate()
                    .map(|(index, layout)| LayoutShortcut {
                        layout,
                        hotkey: format!("Ctrl+Alt+Shift+{}", index + 1),
                    })
                    .collect();
                settings.cycle = available;
            }
            Ok(settings)
        }
        Err(error) => Err(error),
    }
}

#[cfg(windows)]
fn add_windows_hotkey_defaults(settings: &mut Config, data: &[u8]) -> io::Result<()> {
    if !settings.hotkeys.snippet_menu.is_empty() && !settings.hotkeys.clear_drawing.is_empty() {
        return Ok(());
    }
    let stored: serde_json::Value = serde_json::from_slice(data).map_err(io::Error::other)?;
    let defaults = Hotkeys::default();
    let bindings = settings.hotkeys.bindings()?;
    let direct = settings.direct_bindings(&bindings)?;
    let free = |value: &str| {
        Chord::parse(value).is_some_and(|chord| {
            bindings.iter().all(|&(_, used)| used != chord)
                && direct.iter().all(|&(_, used)| used != chord)
        })
    };
    if stored["hotkeys"].get("snippet_menu").is_none() && free(&defaults.snippet_menu) {
        settings.hotkeys.snippet_menu = defaults.snippet_menu;
    }
    if stored["hotkeys"].get("clear_drawing").is_none() && free(&defaults.clear_drawing) {
        settings.hotkeys.clear_drawing = defaults.clear_drawing;
    }
    Ok(())
}

pub fn available_layouts() -> Vec<Language> {
    detected_layouts().unwrap_or_else(|| Language::ALL.to_vec())
}

pub(crate) fn detected_layouts() -> Option<Vec<Language>> {
    #[cfg(windows)]
    return Some(crate::windows::available_layouts());
    #[cfg(target_os = "linux")]
    return crate::linux::available_layouts().ok();
    #[cfg(target_os = "macos")]
    return crate::macos::available_layouts().ok();
    #[cfg(not(any(windows, target_os = "linux", target_os = "macos")))]
    None
}

pub(crate) fn resolve_saved_layouts(settings: &mut Config) {
    let available = available_layouts();
    let resolve = |layout: &mut Language| {
        *layout = installed_layout(*layout, &available).unwrap_or(*layout);
    };
    for cycle in std::iter::once(&mut settings.cycle)
        .chain(settings.applications.iter_mut().map(|rule| &mut rule.cycle))
    {
        for layout in cycle.iter_mut() {
            resolve(layout);
        }
        let mut seen = std::collections::HashSet::new();
        cycle.retain(|layout| seen.insert(*layout));
    }
    for binding in &mut settings.layout_hotkeys {
        resolve(&mut binding.layout);
    }
}

pub(crate) fn restrict_imported_layouts(settings: &mut Config, available: &[Language]) {
    if available.is_empty() {
        return;
    }
    let resolve_cycle = |cycle: &[Language]| {
        let mut seen = std::collections::HashSet::new();
        cycle
            .iter()
            .map(|&layout| installed_layout(layout, available))
            .filter(|item| item.is_none_or(|layout| seen.insert(layout)))
            .collect::<Option<Vec<_>>>()
    };
    settings.cycle = settings
        .cycle
        .iter()
        .filter_map(|&layout| installed_layout(layout, available))
        .collect();
    if settings.cycle.is_empty() {
        settings.cycle.extend_from_slice(available);
    }
    let mut seen = std::collections::HashSet::new();
    settings.cycle.retain(|layout| seen.insert(*layout));
    settings.applications.retain_mut(|rule| {
        // An empty cycle intentionally inherits the global cycle. Dropping
        // missing layouts must never turn a restricted rule into that policy.
        if let Some(cycle) = resolve_cycle(&rule.cycle) {
            rule.cycle = cycle;
            true
        } else {
            false
        }
    });
    let mut seen = std::collections::HashSet::new();
    settings.layout_hotkeys.retain_mut(|binding| {
        if let Some(layout) = installed_layout(binding.layout, available)
            && seen.insert(layout)
        {
            binding.layout = layout;
            true
        } else {
            false
        }
    });
}

fn installed_layout(layout: Language, available: &[Language]) -> Option<Language> {
    available
        .iter()
        .copied()
        .find(|&native| native == layout)
        .or_else(|| {
            if matches!(layout, Language::En | Language::Ru | Language::Uk) {
                available
                    .iter()
                    .copied()
                    .find(|native| native.dictionary_index() == layout.dictionary_index())
            } else {
                None
            }
        })
}

pub fn save(directory: &Path, config: &Config) -> io::Result<()> {
    validate(config)?;
    let data = serde_json::to_vec(config).map_err(io::Error::other)?;
    crate::dictionary::Dictionary::new(directory).write_settings(&data)
}

pub(crate) fn validate(settings: &Config) -> io::Result<()> {
    if !UI_LANGUAGES.contains(&settings.ui_language.as_str()) {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "Unsupported interface language",
        ));
    }
    settings.direct_bindings(&settings.hotkeys.bindings()?)?;
    settings.drawing_shortcut.validate()?;
    Snippets::compile_for(&settings.snippets, &available_layouts())?;
    if settings.cycle.is_empty() || has_duplicates(&settings.cycle) {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "Choose at least one layout and remove duplicates",
        ));
    }
    if !(1..=10_000).contains(&settings.history_items)
        || !(1..=4096).contains(&settings.history_total_mib)
        || !(1..=128).contains(&settings.history_image_mib)
        || !(1..=8192).contains(&settings.history_text_kib)
    {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "Clipboard history limits are out of range",
        ));
    }
    let mut processes = std::collections::HashSet::new();
    for rule in &settings.applications {
        let process = process_key(&rule.process);
        if process.is_empty() || !processes.insert(process) || has_duplicates(&rule.cycle) {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "Application rules need distinct process names and layouts",
            ));
        }
    }
    Ok(())
}

fn has_duplicates(cycle: &[Language]) -> bool {
    cycle
        .iter()
        .enumerate()
        .any(|(index, lang)| cycle[..index].contains(lang))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[cfg(windows)]
    #[test]
    fn added_hotkeys_preserve_existing_bindings_and_explicit_disabling() {
        let legacy = br#"{"hotkeys":{"case":"Ctrl+Alt+Shift+S","clear_drawing":""}}"#;
        let mut settings: Config = serde_json::from_slice(legacy).unwrap();
        add_windows_hotkey_defaults(&mut settings, legacy).unwrap();
        assert_eq!(settings.hotkeys.case, "Ctrl+Alt+Shift+S");
        assert!(settings.hotkeys.snippet_menu.is_empty());
        assert!(settings.hotkeys.clear_drawing.is_empty());
        let legacy = br#"{"hotkeys":{"case":"Ctrl+Alt+Shift+C"}}"#;
        let mut settings: Config = serde_json::from_slice(legacy).unwrap();
        add_windows_hotkey_defaults(&mut settings, legacy).unwrap();
        assert_eq!(settings.hotkeys.snippet_menu, "Ctrl+Alt+Shift+S");
        assert_eq!(settings.hotkeys.clear_drawing, "Ctrl+Alt+Shift+X");
    }

    #[test]
    fn layouts_can_bind_independently_and_detect_conflicts_with_commands() {
        let mut settings = Config {
            layout_hotkeys: vec![
                LayoutShortcut {
                    layout: Language::Windows(0x040c040c),
                    hotkey: "Ctrl+Alt+Shift+1".into(),
                },
                LayoutShortcut {
                    layout: Language::Windows(0x04160416),
                    hotkey: String::new(),
                },
            ],
            ..Config::default()
        };
        let static_keys = settings.hotkeys.bindings().unwrap();
        assert_eq!(settings.direct_bindings(&static_keys).unwrap().len(), 1);
        settings.layout_hotkeys[1].hotkey = settings.hotkeys.convert.clone();
        assert!(settings.direct_bindings(&static_keys).is_err());
    }

    #[test]
    fn restricted_profiles_preserve_allowed_language_and_choose_english_first() {
        let mut config = Config::default();
        config.applications.push(ApplicationRule {
            process: "Teams.exe".into(),
            cycle: vec![Language::Uk, Language::En],
            ..Default::default()
        });
        config.applications.push(ApplicationRule {
            process: "pwsh".into(),
            cycle: vec![Language::En],
            ..Default::default()
        });
        config.applications.push(ApplicationRule {
            process: "chrome".into(),
            cycle: vec![],
            ..Default::default()
        });
        assert_eq!(
            config.cycle_for(r"C:\Apps\TEAMS.EXE"),
            &[Language::Uk, Language::En]
        );
        assert_eq!(
            config.layout_on_entry("teams", Some(Language::Ru)),
            Some(Language::En)
        );
        assert_eq!(config.layout_on_entry("teams", Some(Language::Uk)), None);
        assert_eq!(
            config.layout_on_entry("pwsh.exe", Some(Language::Ru)),
            Some(Language::En)
        );
        assert_eq!(config.cycle_for("chrome.exe"), &Language::ALL);
        assert_eq!(config.layout_on_entry("chrome", Some(Language::Ru)), None);
    }
}
