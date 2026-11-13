use crate::config::{self, Config, UI_LANGUAGES};
use std::{cell::Cell, collections::HashMap, path::Path, sync::LazyLock};
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

thread_local! {
    static LANGUAGE: Cell<usize> = const { Cell::new(0) };
}

pub fn set_language(code: &str) {
    let index = UI_LANGUAGES
        .iter()
        .position(|&item| item == code)
        .unwrap_or(0);
    LANGUAGE.set(index);
}

pub fn load(profile: &Path) -> Config {
    let config = config::load(profile).unwrap_or_default();
    set_language(&config.ui_language);
    config
}

pub fn text(key: &str, fallback: &'static str) -> &'static str {
    let language = LANGUAGE.get();
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
}
