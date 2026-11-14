use crate::{
    config::{self, Config},
    dictionary::{Entry, Snapshot, validate_correction},
    layout::Language,
};
use rusqlite::{Connection, OpenFlags};
use serde_json::{Value, json};
use std::{
    fs::File,
    io::{self, Read, Write},
};

const MAX_DB: u64 = 64 * 1024 * 1024;

pub fn read(input: File) -> io::Result<(Config, Snapshot)> {
    let mut archive = zip::ZipArchive::new(input).map_err(io::Error::other)?;
    if archive.len() != 3 {
        return Err(invalid("Unexpected entries in Typlo archive"));
    }
    let mut marker = String::new();
    archive
        .by_name("format.txt")
        .map_err(io::Error::other)?
        .take(65)
        .read_to_string(&mut marker)?;
    if marker != "typlo-backup-v1" {
        return Err(invalid("Unsupported archive format"));
    }
    let settings_file = archive.by_name("settings.json").map_err(io::Error::other)?;
    if settings_file.size() > 1024 * 1024 {
        return Err(invalid("Oversized settings"));
    }
    let old: Value =
        serde_json::from_reader(settings_file.take(1024 * 1024 + 1)).map_err(io::Error::other)?;
    let (settings, corrections) = convert_settings(&old)?;
    let database_file = archive
        .by_name("personal.sqlite3")
        .map_err(io::Error::other)?;
    if database_file.size() > MAX_DB {
        return Err(invalid("Oversized personal dictionary"));
    }
    let mut file = tempfile::NamedTempFile::new()?;
    if io::copy(&mut database_file.take(MAX_DB + 1), file.as_file_mut())? > MAX_DB {
        return Err(invalid("Oversized personal dictionary"));
    }
    file.as_file_mut().flush()?;
    let database = Connection::open_with_flags(file.path(), OpenFlags::SQLITE_OPEN_READ_ONLY)
        .map_err(io::Error::other)?;
    database
        .execute_batch("PRAGMA trusted_schema=OFF; PRAGMA query_only=ON; PRAGMA mmap_size=0;")
        .map_err(io::Error::other)?;
    let check: String = database
        .query_row("PRAGMA quick_check(1)", [], |row| row.get(0))
        .map_err(io::Error::other)?;
    if check != "ok" {
        return Err(invalid("Corrupt personal dictionary"));
    }
    let tables: u32 = database.query_row(
        "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name IN ('words','excluded_words') AND sql LIKE 'CREATE TABLE %'",
        [], |row| row.get(0),
    ).map_err(io::Error::other)?;
    if tables != 2 {
        return Err(invalid("Invalid personal dictionary tables"));
    }
    let mut statement = database.prepare("SELECT word,language FROM words UNION ALL SELECT word,'' FROM excluded_words LIMIT 300001")
        .map_err(io::Error::other)?;
    let entries = statement
        .query_map([], |row| {
            Ok(Entry {
                word: row.get(0)?,
                language: row.get(1)?,
            })
        })
        .map_err(io::Error::other)?
        .collect::<rusqlite::Result<_>>()
        .map_err(io::Error::other)?;
    Ok((
        settings,
        Snapshot {
            entries,
            corrections,
        },
    ))
}

const SETTINGS_FIELDS: &[(&str, &str)] = &[
    ("uiLanguage", "ui_language"),
    ("uiTheme", "ui_theme"),
    ("switchSystemLayout", "switch_layout"),
    ("convertLastWord", "convert_last_word"),
    ("protectTechnicalInput", "protect_technical"),
    ("snippetsEnabled", "snippets_enabled"),
    ("autoCorrectEnabled", "auto_correct"),
    ("snippets", "snippets"),
    ("screenshotEditorAfterCopy", "capture_editor_after_copy"),
    ("excludedApps", "history_excluded_apps"),
    ("historyEnabled", "history_enabled"),
    ("historyMoveUsedToTop", "history_move_to_top"),
    ("maxItems", "history_items"),
    ("maxTotalMegabytes", "history_total_mib"),
    ("maxImageMegabytes", "history_image_mib"),
    ("maxTextKilobytes", "history_text_kib"),
    ("startWithSystem", "start_with_windows"),
    ("preventScreenLock", "keep_screen_on"),
    ("drawingShortcut", "drawing_shortcut"),
];
const HOTKEY_FIELDS: &[(&str, &str)] = &[
    ("englishHotkey", "english"),
    ("russianHotkey", "russian"),
    ("ukrainianHotkey", "ukrainian"),
    ("snippetMenuHotkey", "snippet_menu"),
    ("clearDrawingHotkey", "clear_drawing"),
    ("toggleHotkey", "convert"),
    ("layoutSwitchHotkey", "switch"),
    ("caseHotkey", "case"),
    ("translitHotkey", "transliterate"),
    ("undoHotkey", "undo"),
    ("toolsHotkey", "tools"),
    ("choicesHotkey", "choices"),
    ("acceptChoiceHotkey", "accept_choice"),
    ("cancelChoiceHotkey", "cancel_choice"),
    ("addWordHotkey", "add_word"),
    ("spellingHotkey", "spelling"),
    ("spellingReviewHotkey", "spelling_review"),
    ("clipboardHotkey", "history"),
    ("screenshotCopyHotkey", "capture_copy"),
    ("screenshotSaveHotkey", "capture_file"),
    ("screenshotEditorHotkey", "capture_edit"),
];
type Pairs = Vec<(String, String)>;

pub(super) fn convert_settings(old: &Value) -> io::Result<(Config, Pairs)> {
    let marked = old["format"] == "typlo-settings";
    let unmarked = old.get("format").is_none()
        && old.get("clipboardHotkey").is_some()
        && old.get("layouts").is_some();
    if (!marked && !unmarked)
        || old
            .get("schemaVersion")
            .is_some_and(|version| !matches!(version.as_u64(), Some(1 | 2)))
    {
        return Err(invalid("Unknown Typlo settings version"));
    }
    let mut migrated = select_fields(old, SETTINGS_FIELDS);
    if migrated["snippets"].is_null() {
        migrated.as_object_mut().unwrap().remove("snippets");
    }
    let layouts = parse_layouts(&old["layouts"])?;
    let layouts = if layouts.is_empty() {
        config::available_layouts()
    } else {
        layouts
    };
    migrated["cycle"] = json!(if layouts.is_empty() {
        Language::ALL.to_vec()
    } else {
        layouts
    });
    migrated["applications"] = Value::Array(
        items(old, "applicationLayouts")?
            .iter()
            .map(|rule| {
                Ok(json!({
                    "process": rule["processName"], "cycle": parse_layouts(&rule["layouts"])?,
                    "snippets": rule["snippetExpansion"], "auto_correct": rule["autoCorrection"],
                    "enabled": rule["enabled"].as_bool().unwrap_or(true),
                }))
            })
            .collect::<io::Result<_>>()?,
    );
    migrated["capture_shape"] = match old["screenshotShape"].as_u64().unwrap_or(0) {
        0 => json!("rectangle"),
        1 => json!("free"),
        _ => return Err(invalid("Unknown screenshot shape")),
    };
    let hotkeys = select_fields(old, HOTKEY_FIELDS);
    let mut direct = Vec::new();
    for item in items(old, "layoutHotkeys")? {
        let shortcut = as_text(&item["hotkey"])?;
        let layout = parse_layout(as_text(&item["layoutId"])?)?;
        direct.push(json!({"layout": layout, "hotkey": shortcut}));
    }
    migrated["hotkeys"] = hotkeys;
    migrated["layout_hotkeys"] = Value::Array(direct);
    let pairs = items(old, "spellingCorrections")?
        .iter()
        .map(|pair| {
            let (word, replacement) =
                validate_correction(as_text(&pair["error"])?, as_text(&pair["correction"])?)?;
            Ok((word, replacement.to_owned()))
        })
        .collect::<io::Result<_>>()?;
    let mut settings: Config = serde_json::from_value(migrated).map_err(io::Error::other)?;
    #[cfg(windows)]
    if old.get("snippetMenuHotkey").is_none() {
        use crate::hotkeys::Chord;
        let picker = Chord::parse(&settings.hotkeys.snippet_menu);
        if HOTKEY_FIELDS
            .iter()
            .filter(|(key, _)| *key != "snippetMenuHotkey")
            .filter_map(|(key, _)| old[*key].as_str())
            .chain(
                settings
                    .layout_hotkeys
                    .iter()
                    .map(|item| item.hotkey.as_str()),
            )
            .any(|shortcut| picker.is_some() && Chord::parse(shortcut) == picker)
        {
            settings.hotkeys.snippet_menu.clear();
        }
    }
    settings.history_items = settings.history_items.clamp(1, 10_000);
    settings.history_total_mib = settings.history_total_mib.clamp(1, 4096);
    settings.history_image_mib = settings.history_image_mib.clamp(1, 128);
    settings.history_text_kib = settings.history_text_kib.clamp(1, 8192);
    Ok((settings, pairs))
}

pub(super) fn settings_document(
    settings: &Config,
    pairs: &[(String, String)],
) -> io::Result<Value> {
    let _ = config::available_layouts();
    let serialized = serde_json::to_value(settings).map_err(io::Error::other)?;
    let mut document = json!({
        "format": "typlo-settings",
        "schemaVersion": 2,
        "layoutsInitialized": true,
        "windowsShortcutsInitialized": cfg!(windows),
        "layouts": layout_ids(&settings.cycle)?,
        "screenshotShape": u8::from(settings.capture_shape == config::CaptureShape::Free),
        "spellingCorrections": pairs.iter().map(|(error, correction)| json!({
            "error": error, "correction": correction,
        })).collect::<Vec<_>>(),
        "applicationLayouts": settings.applications.iter().map(|rule| Ok(json!({
            "processName": rule.process, "layouts": layout_ids(&rule.cycle)?,
            "snippetExpansion": rule.snippets, "autoCorrection": rule.auto_correct,
            "enabled": rule.enabled,
        }))).collect::<io::Result<Vec<_>>>()?,
        "layoutHotkeys": settings.layout_hotkeys.iter().map(|shortcut| Ok(json!({
            "layoutId": layout_id(shortcut.layout)?, "hotkey": shortcut.hotkey,
        }))).collect::<io::Result<Vec<_>>>()?,
    });
    for &(old, new) in SETTINGS_FIELDS {
        document[old] = serialized[new].clone();
    }
    for &(old, new) in HOTKEY_FIELDS {
        document[old] = serialized["hotkeys"][new].clone();
    }
    Ok(document)
}

#[test]
fn drawing_shortcut_defaults_for_legacy_files_and_survives_export() {
    use crate::hotkeys::{DrawingButton, DrawingShortcut};
    let (mut settings, _) = convert_settings(&json!({"format": "typlo-settings"})).unwrap();
    assert_eq!(settings.drawing_shortcut, DrawingShortcut::default());
    settings.drawing_shortcut = DrawingShortcut {
        modifiers: 1,
        button: DrawingButton::Off,
    };
    let document = settings_document(&settings, &[]).unwrap();
    let (restored, _) = convert_settings(&document).unwrap();
    assert_eq!(restored.drawing_shortcut, settings.drawing_shortcut);
}

fn layout_id(layout: Language) -> io::Result<String> {
    layout.settings_id().ok_or_else(|| {
        invalid("A layout has no portable ID. Reconnect the layout or use a full Typlo backup.")
    })
}

fn layout_ids(layouts: &[Language]) -> io::Result<Vec<String>> {
    layouts.iter().copied().map(layout_id).collect()
}

fn select_fields(old: &Value, mapping: &[(&str, &str)]) -> Value {
    Value::Object(
        mapping
            .iter()
            .filter_map(|&(old_key, new_key)| {
                old.get(old_key)
                    .map(|value| (new_key.to_owned(), value.clone()))
            })
            .collect(),
    )
}

fn items<'a>(document: &'a Value, name: &str) -> io::Result<&'a [Value]> {
    match document.get(name) {
        None | Some(Value::Null) => Ok(&[]),
        Some(Value::Array(values)) => Ok(values),
        _ => Err(invalid("Expected settings array")),
    }
}

fn as_text(value: &Value) -> io::Result<&str> {
    value
        .as_str()
        .ok_or_else(|| invalid("Expected a text setting"))
}

fn parse_layouts(value: &Value) -> io::Result<Vec<Language>> {
    match value {
        Value::Null => Ok(Vec::new()),
        Value::Array(ids) => ids.iter().map(|id| parse_layout(as_text(id)?)).collect(),
        _ => Err(invalid("Invalid layout cycle")),
    }
}

fn parse_layout(id: &str) -> io::Result<Language> {
    Ok(match id {
        "en" => Language::En,
        "ru" => Language::Ru,
        "uk" => Language::Uk,
        _ if id.starts_with("xkb:") => {
            let (source, variant) = id[4..]
                .split_once(':')
                .ok_or_else(|| invalid("Invalid XKB layout"))?;
            let valid = |value: &str| {
                value.len() <= 80
                    && value
                        .bytes()
                        .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'_'))
            };
            if source.is_empty() || !valid(source) || !valid(variant) {
                return Err(invalid("Invalid XKB layout"));
            }
            Language::xkb(source.as_bytes(), variant.as_bytes(), "")
        }
        _ if id.starts_with("mac:") => {
            let source = &id[4..];
            if source.is_empty() || source.len() > 256 || source.chars().any(char::is_control) {
                return Err(invalid("Invalid macOS input source"));
            }
            Language::mac(source, 0)
        }
        _ => {
            let hex = id
                .strip_prefix("win:")
                .filter(|hex| hex.len() == 8)
                .ok_or_else(|| invalid("Archive uses a keyboard layout from another platform"))?;
            Language::Windows(u32::from_str_radix(hex, 16).map_err(io::Error::other)?)
        }
    })
}

fn invalid(message: &str) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, message)
}

#[cfg(test)]
mod tests {
    use super::*;
    use zip::{CompressionMethod, ZipWriter, write::SimpleFileOptions};

    #[test]
    fn imports_original_archive_without_reusing_its_sqlite_schema() {
        let temp = tempfile::tempdir().unwrap();
        let database = temp.path().join("personal.sqlite3");
        let db = Connection::open(&database).unwrap();
        db.execute_batch(
            "CREATE TABLE words(word TEXT NOT NULL,language TEXT NOT NULL);
            CREATE TABLE excluded_words(word TEXT NOT NULL);
            INSERT INTO words VALUES('typlofixture','en');
            INSERT INTO excluded_words VALUES('github');",
        )
        .unwrap();
        drop(db);
        let path = temp.path().join("old.typlo");
        let mut archive = ZipWriter::new(File::create(&path).unwrap());
        let options = SimpleFileOptions::default().compression_method(CompressionMethod::Deflated);
        archive.start_file("format.txt", options).unwrap();
        archive.write_all(b"typlo-backup-v1").unwrap();
        archive.start_file("settings.json", options).unwrap();
        let settings = json!({
            "format": "typlo-settings", "schemaVersion": 2, "layouts": ["win:04090409", "win:04190419"],
            "applicationLayouts": [{"processName": "Teams", "layouts": ["win:04090409"], "enabled": false}],
            "spellingCorrections": [{"error": "helo", "correction": "hello"}],
            "historyMoveUsedToTop": false, "screenshotShape": 1,
            "snippets": [{"trigger": ";abc", "text": "line one\n{cursor}"}],
        });
        serde_json::to_writer(&mut archive, &settings).unwrap();
        archive.start_file("personal.sqlite3", options).unwrap();
        archive
            .write_all(&std::fs::read(database).unwrap())
            .unwrap();
        archive.finish().unwrap();

        let backup = crate::backup::Backup::read_with_layouts(&path, || {
            vec![Language::Windows(0x04090409), Language::Windows(0x04190419)]
        })
        .unwrap();
        assert_eq!(backup.summary(), (1, 1, 1, 1));
        let output = temp.path().join("output");
        let loaded = backup.restore(&output).unwrap();
        assert!(!loaded.history_move_to_top);
        assert!(!loaded.applications[0].enabled);
        assert_eq!(loaded.capture_shape, config::CaptureShape::Free);
        assert!(
            crate::dictionary::Dictionary::new(&output)
                .contains("typlofixture", "en")
                .unwrap()
        );
    }
}
