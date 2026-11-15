use crate::config::{self, Config, UI_LANGUAGES};
use std::{
    cell::Cell,
    collections::HashMap,
    path::Path,
    sync::{
        LazyLock,
        atomic::{AtomicUsize, Ordering},
    },
};
#[cfg(feature = "gui")]
use {crate::config::UiTheme, eframe::egui};

pub const NAMES: [&str; 10] = [
    "English",
    "Русский",
    "Українська",
    "Deutsch",
    "Español",
    "Português",
    "Français",
    "Italiano",
    "中文 (Chinese)",
    "日本語 (Japanese)",
];

static CATALOG: LazyLock<HashMap<String, [String; 10]>> = LazyLock::new(|| {
    serde_json::from_str(include_str!("ui_catalog.json")).expect("validated Typlo locale catalog")
});

// The process-wide language lets background jobs report in the chosen language.
// The thread that chose it keeps its own copy, so parallel windows and tests
// that switch languages do not affect each other mid-frame.
static LANGUAGE: AtomicUsize = AtomicUsize::new(0);

thread_local! {
    static THREAD_LANGUAGE: Cell<Option<usize>> = const { Cell::new(None) };
}

pub fn set_language(code: &str) {
    let index = UI_LANGUAGES
        .iter()
        .position(|&item| item == code)
        .unwrap_or(0);
    LANGUAGE.store(index, Ordering::Relaxed);
    THREAD_LANGUAGE.set(Some(index));
}

pub fn load(profile: &Path) -> Config {
    let config = config::load(profile).unwrap_or_default();
    set_language(&config.ui_language);
    config
}

pub fn text(key: &str, fallback: &'static str) -> &'static str {
    let language = THREAD_LANGUAGE
        .get()
        .unwrap_or_else(|| LANGUAGE.load(Ordering::Relaxed));
    CATALOG
        .get(key)
        .map_or(fallback, |values| values[language].as_str())
}

#[cfg(feature = "gui")]
pub fn theme(ctx: &egui::Context, theme: UiTheme) {
    let (content, window) = match theme {
        UiTheme::System => (
            egui::ThemePreference::System,
            egui::SystemTheme::SystemDefault,
        ),
        UiTheme::Light => (egui::ThemePreference::Light, egui::SystemTheme::Light),
        UiTheme::Dark => (egui::ThemePreference::Dark, egui::SystemTheme::Dark),
    };
    ctx.set_theme(content);
    ctx.send_viewport_cmd(egui::ViewportCommand::SetTheme(window));
}

#[cfg(feature = "gui")]
pub fn fonts(ctx: &egui::Context, language: &str) {
    let mut fonts = egui::FontDefinitions::default();
    #[cfg(windows)]
    if let Some(root) = std::env::var_os("WINDIR") {
        let directory = std::path::PathBuf::from(root).join("Fonts");
        let family = fonts
            .families
            .entry(egui::FontFamily::Proportional)
            .or_default();
        let mut position = 0;
        for (name, file) in [
            ("segoe-ui", "segoeui.ttf"),
            ("segoe-ui-symbol", "seguisym.ttf"),
        ] {
            if let Ok(data) = std::fs::read(directory.join(file)) {
                fonts
                    .font_data
                    .insert(name.into(), egui::FontData::from_owned(data).into());
                family.insert(position, name.into());
                position += 1;
            }
        }
    }
    if !matches!(language, "zh" | "ja") {
        ctx.set_fonts(fonts);
        return;
    }
    #[cfg(windows)]
    let path = std::env::var_os("WINDIR").map(|root| {
        std::path::PathBuf::from(root)
            .join("Fonts")
            .join(if language == "ja" {
                "meiryo.ttc"
            } else {
                "msyh.ttc"
            })
    });
    #[cfg(target_os = "linux")]
    let path = Some(std::path::PathBuf::from(
        "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc",
    ));
    #[cfg(target_os = "macos")]
    let path = Some(std::path::PathBuf::from(if language == "ja" {
        "/System/Library/Fonts/ヒラギノ角ゴシック W3.ttc"
    } else {
        "/System/Library/Fonts/PingFang.ttc"
    }));
    #[cfg(not(any(windows, target_os = "linux", target_os = "macos")))]
    let path: Option<std::path::PathBuf> = None;
    if let Some(data) = path.and_then(|path| std::fs::read(path).ok()) {
        fonts
            .font_data
            .insert("system-cjk".into(), egui::FontData::from_owned(data).into());
        for names in fonts.families.values_mut() {
            names.push("system-cjk".into());
        }
    }
    ctx.set_fonts(fonts);
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn source_and_new_labels_follow_the_interface_language() {
        for code in UI_LANGUAGES {
            set_language(code);
            assert!(!text("settings.language", "Язык интерфейса").is_empty());
        }
        set_language("en");
        assert_eq!(Config::default().ui_language, "en");
        assert_eq!(
            text("settings.language", "Язык интерфейса"),
            "Interface language"
        );
        set_language("uk");
        assert_eq!(
            text("rust.dictionary.heading", "Личный словарь"),
            "Особистий словник"
        );
    }

    #[test]
    fn every_translated_label_has_catalog_entries() {
        fn sources(dir: &Path, out: &mut Vec<std::path::PathBuf>) {
            for entry in std::fs::read_dir(dir).unwrap() {
                let path = entry.unwrap().path();
                if path.is_dir() {
                    sources(&path, out);
                } else if path.extension().is_some_and(|ext| ext == "rs") {
                    out.push(path);
                }
            }
        }
        let mut files = Vec::new();
        sources(
            &Path::new(env!("CARGO_MANIFEST_DIR")).join("src"),
            &mut files,
        );
        let mut missing = Vec::new();
        for file in files {
            let source = std::fs::read_to_string(&file).unwrap();
            for call in ["tr(", "locale::text("] {
                let mut rest = source.as_str();
                while let Some(start) = rest.find(call) {
                    let before = rest[..start].chars().next_back();
                    rest = &rest[start + call.len()..];
                    if call == "tr(" && before.is_some_and(|c| c.is_alphanumeric() || c == '_') {
                        continue;
                    }
                    let Some(literal) = rest.trim_start().strip_prefix('"') else {
                        continue;
                    };
                    let Some(end) = literal.find('"') else {
                        continue;
                    };
                    let key = &literal[..end];
                    if literal[end + 1..].trim_start().starts_with(',')
                        && !CATALOG.contains_key(key)
                    {
                        missing.push(format!("{}: {key}", file.display()));
                    }
                }
            }
        }
        missing.sort();
        missing.dedup();
        assert!(
            missing.is_empty(),
            "missing catalog keys:
{}",
            missing.join(
                "
"
            )
        );
    }

    #[test]
    fn translations_are_not_english_copies() {
        // Short labels may legitimately match English (names, "OK", shortcuts).
        // Longer texts that are spelled the same in a language are listed explicitly.
        const SAME_AS_ENGLISH: &[(&str, &str)] = &[("rust.snippet.name", "de")];
        let mut copies = Vec::new();
        for (key, values) in CATALOG.iter() {
            let english = &values[0];
            if english.chars().filter(|c| c.is_alphabetic()).count() < 12
                || english.contains("Typlo") && english.len() < 20
            {
                continue;
            }
            for (index, value) in values.iter().enumerate().skip(1) {
                if value == english
                    && !SAME_AS_ENGLISH.contains(&(key.as_str(), UI_LANGUAGES[index]))
                {
                    copies.push(format!("{key} [{}]", UI_LANGUAGES[index]));
                }
            }
        }
        copies.sort();
        assert!(
            copies.is_empty(),
            "untranslated catalog entries:
{}",
            copies.join(
                "
"
            )
        );
    }
}
