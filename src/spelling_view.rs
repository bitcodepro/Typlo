use crate::locale::text as tr;
use crate::{dictionary::Dictionary, spelling::Review};
use eframe::egui;
use std::{
    io::{self, Read, Write},
    path::PathBuf,
};

pub fn run(profile: PathBuf) -> Result<(), Box<dyn std::error::Error>> {
    let appearance = crate::locale::load(&profile);
    let stdin = io::stdin();
    let request: Review = serde_json::from_reader(stdin.lock().take(2 * 1024 * 1024))?;
    let dictionary = Dictionary::new(&profile);
    let pairs = dictionary.corrections()?;
    let choices = vec![None; request.issues.len()];
    eframe::run_native(
        tr("rust.spelling.title", "Проверка орфографии"),
        eframe::NativeOptions {
            viewport: crate::appearance::window().with_inner_size([720., 580.]),
            renderer: eframe::Renderer::Glow,
            ..Default::default()
        },
        Box::new(move |creation| {
            crate::appearance::configure(&creation.egui_ctx);
            crate::locale::theme(&creation.egui_ctx, appearance.ui_theme);
            crate::locale::fonts(&creation.egui_ctx, &appearance.ui_language);
            Ok(Box::new(ReviewWindow {
                request,
                dictionary,
                pairs,
                choices,
                wrong: String::new(),
                corrected: String::new(),
                status: String::new(),
            }))
        }),
    )?;
    Ok(())
}

struct ReviewWindow {
    request: Review,
    dictionary: Dictionary,
    pairs: Vec<(String, String)>,
    choices: Vec<Option<usize>>,
    wrong: String,
    corrected: String,
    status: String,
}

impl eframe::App for ReviewWindow {
    fn ui(&mut self, ui: &mut egui::Ui, _: &mut eframe::Frame) {
        ui.heading(tr("rust.spelling.selection", "Проверка выделенного текста"));
        if self.request.issues.is_empty() {
            ui.label(if self.request.text.is_empty() {
                tr(
                    "spell.selection",
                    "Select text in an editor before opening spelling review.",
                )
            } else {
                tr("rust.spelling.empty", "Ошибок не найдено.")
            });
        }
        egui::ScrollArea::vertical()
            .id_salt("spelling-issues")
            .max_height(250.)
            .show(ui, |ui| {
                for (index, issue) in self.request.issues.iter().enumerate() {
                    ui.horizontal_wrapped(|ui| {
                        ui.label(&issue.word);
                        ui.weak(&issue.language);
                        egui::ComboBox::from_id_salt(("spelling-choice", index))
                            .selected_text(self.choices[index].map_or(
                                tr("rust.spelling.skip", "Пропустить"),
                                |choice| issue.suggestions[choice].as_str(),
                            ))
                            .show_ui(ui, |ui| {
                                ui.selectable_value(
                                    &mut self.choices[index],
                                    None,
                                    tr("rust.spelling.skip", "Пропустить"),
                                );
                                for (choice, suggestion) in issue.suggestions.iter().enumerate() {
                                    ui.selectable_value(
                                        &mut self.choices[index],
                                        Some(choice),
                                        suggestion,
                                    );
                                }
                            });
                        if let Some(choice) = self.choices[index]
                            && ui
                                .small_button(tr("rust.spelling.saveAs", "Сохранить как пару"))
                                .clicked()
                        {
                            self.wrong.clone_from(&issue.word);
                            self.corrected.clone_from(&issue.suggestions[choice]);
                        }
                    });
                }
            });
        ui.separator();
        ui.heading(tr("rust.spelling.personal", "Личные исправления"));
        ui.horizontal_wrapped(|ui| {
            ui.add(
                crate::appearance::text_input(&mut self.wrong)
                    .hint_text(tr("rust.spelling.wrong", "Ошибка")),
            );
            ui.add(
                crate::appearance::text_input(&mut self.corrected)
                    .hint_text(tr("rust.spelling.correct", "Исправление")),
            );
            if ui
                .button(tr("rust.spelling.save", "Сохранить пару"))
                .clicked()
            {
                match self.dictionary.set_correction(&self.wrong, &self.corrected) {
                    Ok(()) => {
                        self.wrong.clear();
                        self.corrected.clear();
                        self.refresh();
                    }
                    Err(error) => self.status = error.to_string(),
                }
            }
        });
        let mut remove = None;
        egui::ScrollArea::vertical()
            .id_salt("spelling-pairs")
            .max_height(155.)
            .show(ui, |ui| {
                for (wrong, corrected) in &self.pairs {
                    ui.horizontal_wrapped(|ui| {
                        ui.label(format!("{wrong} → {corrected}"));
                        if ui
                            .small_button(tr("rust.common.edit", "Изменить"))
                            .clicked()
                        {
                            self.wrong.clone_from(wrong);
                            self.corrected.clone_from(corrected);
                        }
                        if ui
                            .small_button(tr("history.ctx.delete", "Удалить"))
                            .clicked()
                        {
                            remove = Some(wrong.clone());
                        }
                    });
                }
            });
        if let Some(word) = remove {
            match self.dictionary.remove_correction(&word) {
                Ok(_) => self.refresh(),
                Err(error) => self.status = error.to_string(),
            }
        }
        ui.separator();
        ui.horizontal(|ui| {
            if ui
                .add_enabled(
                    self.request.origin != 0 && self.choices.iter().any(Option::is_some),
                    egui::Button::new(tr("rust.spelling.apply", "Применить к выделению")),
                )
                .clicked()
            {
                match self.confirm() {
                    Ok(()) => ui.ctx().send_viewport_cmd(egui::ViewportCommand::Close),
                    Err(error) => self.status = error.to_string(),
                }
            }
            if ui.button(tr("update.close", "Закрыть")).clicked() {
                ui.ctx().send_viewport_cmd(egui::ViewportCommand::Close);
            }
        });
        if !self.status.is_empty() {
            ui.label(&self.status);
        }
    }
}

impl ReviewWindow {
    fn refresh(&mut self) {
        match self.dictionary.corrections() {
            Ok(pairs) => {
                self.pairs = pairs;
                self.status.clear();
            }
            Err(error) => self.status = error.to_string(),
        }
    }

    fn confirm(&self) -> io::Result<()> {
        crate::restore_edit_origin(self.request.origin)?;
        let mut output = io::stdout().lock();
        serde_json::to_writer(&mut output, &self.choices).map_err(io::Error::other)?;
        output.flush()
    }
}
