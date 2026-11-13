use crate::locale::text as tr;
use crate::{
    dictionary::{Added, Dictionary, Entry},
    lexicon::{LANGUAGES, Vocabulary},
};
use eframe::egui::{self, Ui};
use std::{
    io,
    path::{Path, PathBuf},
};

pub struct DictionaryView {
    dictionary: Dictionary,
    corpus: io::Result<Vocabulary>,
    prefix: String,
    language: Option<usize>,
    exclusions: bool,
    page: usize,
    entries: Option<Vec<Entry>>,
    new_words: String,
    new_language: usize,
    edit: Option<(Entry, Entry)>,
    file: String,
    status: String,
}

impl DictionaryView {
    pub fn new(profile: &Path) -> Self {
        Self {
            dictionary: Dictionary::new(profile),
            corpus: Vocabulary::installed(),
            prefix: String::new(),
            language: None,
            exclusions: false,
            page: 0,
            entries: None,
            new_words: String::new(),
            new_language: 0,
            edit: None,
            file: String::new(),
            status: String::new(),
        }
    }

    pub fn show(&mut self, ui: &mut Ui) {
        ui.horizontal_wrapped(|ui| {
            let old = self.exclusions;
            ui.selectable_value(
                &mut self.exclusions,
                false,
                tr("rust.dictionary.words", "Слова"),
            );
            ui.selectable_value(
                &mut self.exclusions,
                true,
                tr("rust.dictionary.never", "Никогда не исправлять"),
            );
            if old != self.exclusions {
                self.entries = None;
                self.page = 0;
                self.edit = None;
                self.status.clear();
            }
        });
        ui.horizontal_wrapped(|ui| {
            let changed = ui
                .add(
                    crate::appearance::text_input(&mut self.prefix)
                        .hint_text(tr("rust.dictionary.prefix", "Начало слова")),
                )
                .changed();
            let language_changed = if self.exclusions {
                false
            } else {
                let old = self.language;
                egui::ComboBox::from_id_salt("filter-language")
                    .selected_text(
                        self.language
                            .map_or(tr("rust.dictionary.all", "Все языки"), |index| {
                                LANGUAGES[index]
                            }),
                    )
                    .show_ui(ui, |ui| {
                        ui.selectable_value(
                            &mut self.language,
                            None,
                            tr("rust.dictionary.all", "Все языки"),
                        );
                        for (index, language) in LANGUAGES.into_iter().enumerate() {
                            ui.selectable_value(&mut self.language, Some(index), language);
                        }
                    });
                old != self.language
            };
            if changed || language_changed || ui.button(tr("manage.refresh", "Refresh")).clicked() {
                self.page = 0;
                self.entries = None;
                self.edit = None;
            }
        });
        if self.entries.is_none() {
            let language = (!self.exclusions)
                .then_some(self.language)
                .flatten()
                .map(|index| LANGUAGES[index]);
            self.entries = Some(
                match self
                    .dictionary
                    .page(&self.prefix, language, self.exclusions, self.page * 50)
                {
                    Ok(entries) => entries,
                    Err(error) => {
                        self.status = error.to_string();
                        Vec::new()
                    }
                },
            );
        }
        let entries = self.entries.as_deref().unwrap_or_default();
        let has_next = entries.len() > 50;
        let mut remove = None;
        egui::ScrollArea::vertical()
            .id_salt("dictionary-entries")
            .max_height(260.)
            .show(ui, |ui| {
                if entries.is_empty() {
                    ui.label(tr("rust.dictionary.empty", "Записей нет"));
                }
                for entry in entries.iter().take(50) {
                    ui.horizontal(|ui| {
                        ui.label(&entry.word);
                        if !entry.language.is_empty() {
                            ui.weak(&entry.language);
                        }
                        if ui
                            .small_button(tr("rust.common.edit", "Изменить"))
                            .clicked()
                        {
                            self.edit = Some((entry.clone(), entry.clone()));
                        }
                        if ui
                            .small_button(tr("history.ctx.delete", "Удалить"))
                            .clicked()
                        {
                            remove = Some(entry.clone());
                        }
                    });
                }
            });
        ui.horizontal(|ui| {
            if ui
                .add_enabled(
                    self.page > 0,
                    egui::Button::new(tr("rust.common.back", "Назад")),
                )
                .clicked()
            {
                self.page -= 1;
                self.entries = None;
            }
            ui.label((self.page + 1).to_string());
            if ui
                .add_enabled(
                    has_next,
                    egui::Button::new(tr("rust.common.next", "Дальше")),
                )
                .clicked()
            {
                self.page += 1;
                self.entries = None;
            }
        });
        if let Some(entry) = remove {
            self.status = match self.dictionary.remove(&entry) {
                Ok(true) => tr("manage.saved", "Saved").into(),
                Ok(false) => tr(
                    "manage.changed",
                    "The entry changed. Refresh and try again.",
                )
                .into(),
                Err(error) => error.to_string(),
            };
            self.edit = None;
            self.entries = None;
        }

        if let Some((_, updated)) = &mut self.edit {
            let mut save = false;
            let mut cancel = false;
            ui.horizontal_wrapped(|ui| {
                ui.add(
                    crate::appearance::text_input(&mut updated.word)
                        .hint_text(tr("rust.dictionary.word", "Слово")),
                );
                if !updated.language.is_empty() {
                    let mut index = LANGUAGES
                        .iter()
                        .position(|&code| code == updated.language)
                        .unwrap_or_default();
                    language_picker(ui, "edit-language", &mut index);
                    updated.language = LANGUAGES[index].into();
                }
                save = ui
                    .button(tr("rust.dictionary.save", "Сохранить слово"))
                    .clicked();
                cancel = ui.button(tr("settings.cancel", "Отмена")).clicked();
            });
            if save && let Some((original, updated)) = self.edit.take() {
                self.status = match self
                    .corpus
                    .as_mut()
                    .map_err(|e| io::Error::other(e.to_string()))
                    .and_then(|corpus| self.dictionary.edit(&original, &updated, corpus))
                {
                    Ok(true) => tr("manage.saved", "Saved").into(),
                    Ok(false) => tr(
                        "manage.changed",
                        "The entry changed. Refresh and try again.",
                    )
                    .into(),
                    Err(error) => error.to_string(),
                };
                self.entries = None;
            }
            if cancel {
                self.edit = None;
            }
        }

        ui.separator();
        ui.label(if self.exclusions {
            tr(
                "exclude.hint",
                "Enter the words Typlo should leave as typed.",
            )
        } else {
            tr(
                "manage.formsHint",
                "Enter the correct forms in the selected language.",
            )
        });
        ui.horizontal_wrapped(|ui| {
            ui.add(
                crate::appearance::text_input(&mut self.new_words)
                    .hint_text(tr("rust.dictionary.forms", "Слова через запятую")),
            );
            if !self.exclusions {
                language_picker(ui, "new-language", &mut self.new_language);
            }
            if ui
                .add_enabled(
                    !self.new_words.trim().is_empty(),
                    egui::Button::new(tr("rust.common.add", "Добавить")),
                )
                .clicked()
            {
                let language = if self.exclusions {
                    ""
                } else {
                    LANGUAGES[self.new_language]
                };
                let entries = self
                    .new_words
                    .split([',', '\n'])
                    .filter(|word| !word.trim().is_empty())
                    .map(|word| Entry::new(word, language))
                    .collect::<io::Result<Vec<_>>>();
                let result = entries.and_then(|entries| {
                    let corpus = self
                        .corpus
                        .as_mut()
                        .map_err(|e| io::Error::other(e.to_string()))?;
                    self.dictionary.add(&entries, corpus)
                });
                if result.is_ok() {
                    self.new_words.clear();
                }
                self.show_added(result);
            }
        });
        ui.horizontal_wrapped(|ui| {
            ui.add(
                crate::appearance::text_input(&mut self.file)
                    .hint_text(tr("rust.dictionary.path", "Путь к JSON")),
            );
            let path = PathBuf::from(self.file.trim());
            if ui
                .add_enabled(
                    !self.file.trim().is_empty(),
                    egui::Button::new(tr("settings.import", "Импорт")),
                )
                .clicked()
            {
                let result = self.dictionary.import(&path, self.exclusions);
                self.show_added(result);
            }
            if ui
                .add_enabled(
                    !self.file.trim().is_empty(),
                    egui::Button::new(tr("settings.export", "Экспорт")),
                )
                .clicked()
            {
                self.status = match self.dictionary.export(&path, self.exclusions) {
                    Ok(()) => tr("settings.exported", "Saved to the file").into(),
                    Err(error) => error.to_string(),
                };
            }
        });
        if !self.status.is_empty() {
            ui.label(&self.status);
        }
    }

    fn show_added(&mut self, result: io::Result<Added>) {
        self.status = match result {
            Ok(added) if added.existing.is_empty() => format!("Добавлено {}", added.inserted),
            Ok(added) => format!(
                "Добавлено {}. Уже в словаре ({}) — {}",
                added.inserted,
                added.existing.len(),
                added
                    .existing
                    .iter()
                    .take(10)
                    .map(|entry| entry.word.as_str())
                    .collect::<Vec<_>>()
                    .join(", ")
            ),
            Err(error) => error.to_string(),
        };
        self.entries = None;
    }
}

fn language_picker(ui: &mut Ui, id: &str, index: &mut usize) {
    egui::ComboBox::from_id_salt(id)
        .selected_text(LANGUAGES[*index])
        .show_ui(ui, |ui| {
            for (value, language) in LANGUAGES.into_iter().enumerate() {
                ui.selectable_value(index, value, language);
            }
        });
}
