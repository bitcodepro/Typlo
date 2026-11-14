use crate::{
    config::{self, Config},
    dictionary::{Dictionary, Entry, Snapshot, validate_correction},
};
use serde::{Deserialize, Serialize};
use std::{
    collections::HashSet,
    fs::File,
    io::{self, BufRead, BufReader, BufWriter, Read, Seek, Write},
    path::Path,
};

#[path = "backup_legacy.rs"]
mod legacy;

const FORMAT: &str = "typlo-rust-backup";
const MAX_BYTES: u64 = 64 * 1024 * 1024;

#[derive(Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Backup {
    format: String,
    version: u32,
    settings: Config,
    dictionary: Snapshot,
    #[serde(skip)]
    mode: RestoreMode,
}

#[derive(Default)]
enum RestoreMode {
    #[default]
    Full,
    Settings {
        corrections_present: bool,
    },
}

impl Backup {
    pub fn read(path: &Path) -> io::Result<Self> {
        Self::read_with_layouts(path, || config::detected_layouts().unwrap_or_default())
    }

    pub(super) fn read_with_layouts(
        path: &Path,
        installed: impl FnOnce() -> Vec<crate::layout::Language>,
    ) -> io::Result<Self> {
        let mut file = File::open(path)?;
        if file.metadata()?.len() > MAX_BYTES {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "Backup exceeds 64 MiB",
            ));
        }
        let mut magic = [0u8; 4];
        file.read_exact(&mut magic)?;
        file.rewind()?;
        let mut backup: Self = if magic == *b"PK\x03\x04" {
            let (settings, dictionary) = legacy::read(file)?;
            Self {
                format: FORMAT.into(),
                version: 1,
                settings,
                dictionary,
                mode: RestoreMode::Full,
            }
        } else {
            let mut reader = BufReader::new(file.take(MAX_BYTES + 1));
            if reader.fill_buf()?.starts_with(&[0xef, 0xbb, 0xbf]) {
                reader.consume(3);
            }
            let document: serde_json::Value =
                serde_json::from_reader(reader).map_err(io::Error::other)?;
            if document["format"] == FORMAT {
                serde_json::from_value(document).map_err(io::Error::other)?
            } else {
                let (settings, corrections) = legacy::convert_settings(&document)?;
                Self {
                    format: FORMAT.into(),
                    version: 1,
                    settings,
                    dictionary: Snapshot {
                        entries: Vec::new(),
                        corrections,
                    },
                    mode: RestoreMode::Settings {
                        corrections_present: document.get("spellingCorrections").is_some(),
                    },
                }
            }
        };
        config::restrict_imported_layouts(&mut backup.settings, &installed());
        backup.validate()?;
        Ok(backup)
    }

    pub fn export(profile: &Path, path: &Path) -> io::Result<()> {
        let (settings, dictionary) = Dictionary::new(profile).backup_snapshot()?;
        let settings = settings.map_or_else(
            || config::load(profile),
            |settings| serde_json::from_slice(&settings).map_err(io::Error::other),
        )?;
        let backup = Self {
            format: FORMAT.into(),
            version: 1,
            settings,
            dictionary,
            mode: RestoreMode::Full,
        };
        backup.validate()?;
        write_json(path, &backup)
    }

    pub fn export_settings(profile: &Path, settings: &Config, path: &Path) -> io::Result<()> {
        config::validate(settings)?;
        let pairs = Dictionary::new(profile).corrections()?;
        write_json(path, &legacy::settings_document(settings, &pairs)?)
    }

    pub fn restore(self, profile: &Path) -> io::Result<Config> {
        self.validate()?;
        let settings = serde_json::to_vec(&self.settings).map_err(io::Error::other)?;
        let dictionary = Dictionary::new(profile);
        match self.mode {
            RestoreMode::Full => dictionary.restore_snapshot(&settings, &self.dictionary)?,
            RestoreMode::Settings {
                corrections_present,
            } => dictionary.restore_settings(
                &settings,
                corrections_present.then_some(self.dictionary.corrections.as_slice()),
            )?,
        }
        Ok(self.settings)
    }

    pub fn settings_only(&self) -> bool {
        matches!(self.mode, RestoreMode::Settings { .. })
    }

    pub fn summary(&self) -> (usize, usize, usize, usize) {
        let exclusions = self
            .dictionary
            .entries
            .iter()
            .filter(|entry| entry.language.is_empty())
            .count();
        (
            self.settings.snippets.len(),
            self.dictionary.entries.len() - exclusions,
            exclusions,
            self.dictionary.corrections.len(),
        )
    }

    fn validate(&self) -> io::Result<()> {
        if self.format != FORMAT
            || self.version != 1
            || self.dictionary.entries.len() > 300_000
            || self.dictionary.corrections.len() > 1000
        {
            return Err(invalid());
        }
        config::validate(&self.settings)?;
        let mut seen = HashSet::new();
        for entry in &self.dictionary.entries {
            if Entry::new(&entry.word, &entry.language)? != *entry
                || !seen.insert((&entry.word, &entry.language))
            {
                return Err(invalid());
            }
        }
        let mut corrections = HashSet::new();
        for (error, correction) in &self.dictionary.corrections {
            let (key, value) = validate_correction(error, correction)?;
            if key != *error || value != correction || !corrections.insert(error) {
                return Err(invalid());
            }
        }
        Ok(())
    }
}

fn write_json(path: &Path, document: &impl Serialize) -> io::Result<()> {
    let parent = path
        .parent()
        .filter(|path| !path.as_os_str().is_empty())
        .unwrap_or(Path::new("."));
    let mut file = tempfile::NamedTempFile::new_in(parent)?;
    {
        let mut writer = BufWriter::new(file.as_file_mut());
        serde_json::to_writer_pretty(&mut writer, document).map_err(io::Error::other)?;
        writer.write_all(b"\n")?;
        writer.flush()?;
    }
    file.as_file().sync_all()?;
    file.persist(path).map_err(|error| error.error)?;
    Ok(())
}

fn invalid() -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, "Invalid Typlo Rust backup")
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::layout::Language;

    #[test]
    fn restore_keeps_settings_and_dictionary_in_one_transaction() {
        let directory = tempfile::tempdir().unwrap();
        let profile = directory.path().to_path_buf();
        let dictionary = Dictionary::new(&profile);
        let original = Config::default();
        config::save(&profile, &original).unwrap();
        dictionary
            .set_correction("helo-world", "hello-world")
            .unwrap();
        let path = profile.join("fixture.typlo");
        Backup::export(&profile, &path).unwrap();

        let mut changed = original.clone();
        changed.history_items += 1;
        let data = serde_json::to_vec(&changed).unwrap();
        let entry = Entry::new("example", "").unwrap();
        let broken = Snapshot {
            entries: vec![entry.clone(), entry],
            corrections: Vec::new(),
        };
        assert!(dictionary.restore_snapshot(&data, &broken).is_err());
        assert_eq!(
            config::load(&profile).unwrap().history_items,
            original.history_items
        );
        assert_eq!(
            dictionary.correction("helo-world").unwrap().as_deref(),
            Some("hello-world")
        );
        assert!(!dictionary.is_excluded("example").unwrap());

        config::save(&profile, &changed).unwrap();
        dictionary.remove_correction("helo-world").unwrap();
        Backup::read(&path).unwrap().restore(&profile).unwrap();
        assert_eq!(
            config::load(&profile).unwrap().history_items,
            original.history_items
        );
        assert_eq!(
            dictionary.correction("helo-world").unwrap().as_deref(),
            Some("hello-world")
        );
    }

    #[test]
    fn imports_csharp_json_and_exports_a_compatible_settings_document() {
        let folder = tempfile::tempdir().unwrap();
        let profile = folder.path().join("profile");
        let dictionary = Dictionary::new(&profile);
        let original = Config {
            ui_theme: config::UiTheme::Dark,
            ..Config::default()
        };
        let data = serde_json::to_vec(&original).unwrap();
        dictionary
            .restore_snapshot(
                &data,
                &Snapshot {
                    entries: vec![
                        Entry::new("typlofixture", "en").unwrap(),
                        Entry::new("github", "").unwrap(),
                    ],
                    corrections: vec![("helo".into(), "hello".into())],
                },
            )
            .unwrap();

        let source = folder.path().join("csharp-settings.json");
        let mut payload = vec![0xef, 0xbb, 0xbf];
        serde_json::to_writer(&mut payload, &serde_json::json!({
            "format": "typlo-settings", "schemaVersion": 2,
            "layouts": ["win:04090409", "win:04190419"],
            "layoutHotkeys": [{"layoutId": "win:04190419", "hotkey": "Ctrl+Alt+Shift+2"}],
            "applicationLayouts": [{"processName": "Code", "layouts": ["win:04090409"], "autoCorrection": false}],
            "snippets": [
                {"trigger": ";hello", "text": "Hello\n{cursor}"},
                {"trigger": "жруддщ", "text": "Another snippet"}
            ],
            "maxItems": 321, "screenshotShape": 1, "screenshotEditorAfterCopy": true,
        })).unwrap();
        std::fs::write(&source, payload).unwrap();
        let installed = || vec![Language::Windows(0x04090409), Language::Windows(0x04190419)];
        let imported = Backup::read_with_layouts(&source, installed).unwrap();
        assert!(imported.settings_only());
        let mut settings = imported.restore(&profile).unwrap();
        assert_eq!(settings.snippets.len(), 2);
        settings.snippets[1].name = "Named snippet".into();
        settings.snippets[1].text = "Hello {name}\n{cursor}".into();
        settings.snippets[1].parameters = true;
        config::save(&profile, &settings).unwrap();
        assert_eq!(settings.ui_theme, config::UiTheme::System);
        assert_eq!(settings.history_items, 321);
        assert_eq!(settings.applications[0].auto_correct, Some(false));
        assert!(dictionary.contains("typlofixture", "en").unwrap());
        assert!(dictionary.is_excluded("github").unwrap());
        assert_eq!(
            dictionary.correction("helo").unwrap().as_deref(),
            Some("hello")
        );

        let export = folder.path().join("typlo-settings.json");
        Backup::export_settings(&profile, &settings, &export).unwrap();
        Backup::export_settings(&profile, &settings, &export).unwrap();
        let json: serde_json::Value =
            serde_json::from_reader(File::open(&export).unwrap()).unwrap();
        assert_eq!(json["format"], "typlo-settings");
        assert_eq!(json["schemaVersion"], 2);
        assert_eq!(json["uiTheme"], "system");
        assert_eq!(json["layouts"][1], "win:04190419");
        assert_eq!(json["layoutHotkeys"][0]["layoutId"], "win:04190419");
        assert_eq!(json["spellingCorrections"][0]["correction"], "hello");
        let round_trip = Backup::read_with_layouts(&export, installed)
            .unwrap()
            .restore(&profile)
            .unwrap();
        assert_eq!(
            serde_json::to_value(&round_trip.snippets).unwrap(),
            serde_json::to_value(&settings.snippets).unwrap()
        );
        assert_eq!(round_trip.capture_shape, config::CaptureShape::Free);
        assert!(round_trip.capture_editor_after_copy);

        let full = folder.path().join("full.typlo");
        Backup::export(&profile, &full).unwrap();
        let restored_profile = folder.path().join("restored");
        let restored = Backup::read_with_layouts(&full, installed)
            .unwrap()
            .restore(&restored_profile)
            .unwrap();
        assert_eq!(
            serde_json::to_value(&restored).unwrap(),
            serde_json::to_value(&round_trip).unwrap()
        );
        let restored_dictionary = Dictionary::new(&restored_profile);
        assert!(restored_dictionary.contains("typlofixture", "en").unwrap());
        assert!(restored_dictionary.is_excluded("github").unwrap());
    }

    #[test]
    fn imports_shortcuts_snippets_and_only_supported_application_rules() {
        use crate::config::{ApplicationRule, LayoutShortcut};
        use crate::snippets::Snippet;

        let folder = tempfile::tempdir().unwrap();
        let source = folder.path().join("old-install");
        let en = Language::Windows(0x04090409);
        let ru = Language::Windows(0x04190419);
        let uk = Language::Windows(0x04220422);
        let shortcut = |layout, hotkey: &str| LayoutShortcut {
            layout,
            hotkey: hotkey.into(),
        };
        let rule = |process: &str, cycle| ApplicationRule {
            process: process.into(),
            cycle,
            auto_correct: Some(false),
            snippets: Some(true),
            enabled: process != "Code.exe",
        };
        let mut settings = Config {
            cycle: vec![uk, en, ru],
            layout_hotkeys: vec![
                shortcut(en, "Ctrl+Shift+1"),
                shortcut(ru, ""),
                shortcut(uk, "Ctrl+Shift+3"),
            ],
            applications: vec![
                rule("Teams.exe", vec![ru, en]),
                rule("Code.exe", vec![en]),
                rule("Notepad++.exe", vec![]),
                rule("Missing.exe", vec![uk]),
                rule("Mixed.exe", vec![en, uk]),
            ],
            snippets: vec![Snippet {
                name: "Example".into(),
                trigger: ";example".into(),
                text: "Hello {name}\n{cursor}Second line".into(),
                parameters: true,
            }],
            ..Config::default()
        };
        settings.hotkeys.convert = "Ctrl+Alt+F8".into();
        settings.hotkeys.undo.clear();
        config::save(&source, &settings).unwrap();
        let json = folder.path().join("settings.json");
        let full = folder.path().join("backup.typlo");
        Backup::export_settings(&source, &settings, &json).unwrap();
        Backup::export(&source, &full).unwrap();

        for (index, path) in [json, full].into_iter().enumerate() {
            let profile = folder.path().join(format!("new-install-{index}"));
            let all = Backup::read_with_layouts(&path, || vec![en, ru, uk]).unwrap();
            assert_eq!(
                serde_json::to_value(all.settings).unwrap(),
                serde_json::to_value(&settings).unwrap()
            );
            let backup = Backup::read_with_layouts(&path, || vec![en, ru]).unwrap();
            let mut expected = settings.clone();
            expected.cycle.remove(0);
            expected.layout_hotkeys.pop();
            expected.applications.truncate(3);
            let restored = backup.restore(&profile).unwrap();
            let expected = serde_json::to_value(expected).unwrap();
            assert_eq!(serde_json::to_value(restored).unwrap(), expected);
            let persisted = Dictionary::new(&profile).read_settings().unwrap().unwrap();
            assert_eq!(
                serde_json::from_slice::<serde_json::Value>(&persisted).unwrap(),
                expected
            );
            let fr = Language::Windows(0x040c040c);
            let foreign = Backup::read_with_layouts(&path, || vec![fr]).unwrap();
            assert_eq!(foreign.settings.cycle, [fr]);
            assert!(foreign.settings.layout_hotkeys.is_empty());
            assert_eq!(foreign.settings.applications.len(), 1);
            assert_eq!(foreign.settings.applications[0].process, "Notepad++.exe");
        }
    }
}
