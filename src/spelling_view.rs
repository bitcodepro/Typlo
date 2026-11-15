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
            viewport: crate::appearance::window().with_inner_size([700., 480.]),
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
    fn clear_color(&self, visuals: &egui::Visuals) -> [f32; 4] {
        crate::appearance::clear_color(visuals)
    }

    fn ui(&mut self, ui: &mut egui::Ui, _: &mut eframe::Frame) {
        use crate::appearance::{footer, group, heading, hint, page, primary_button, text_input};
        footer(ui, "spelling-footer", |ui| {
            if !self.status.is_empty() {
                hint(ui, &self.status);
            }
            ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                let can_apply =
                    self.request.origin != 0 && self.choices.iter().any(Option::is_some);
                if ui
                    .add_enabled(
                        can_apply,
                        primary_button(tr("rust.spelling.apply", "Применить к выделению")),
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
        });
        egui::ScrollArea::vertical().show(ui, |ui| {
            page(ui, |ui| {
                heading(
                    ui,
                    tr("rust.spelling.selection", "Проверка выделенного текста"),
                );
                if self.request.issues.is_empty() {
                    hint(
                        ui,
                        if self.request.text.is_empty() {
                            tr(
                                "spell.selection",
                                "Select text in an editor before opening spelling review.",
                            )
                        } else {
                            tr("rust.spelling.empty", "Ошибок не найдено.")
                        },
                    );
                } else {
                    group(ui).show(ui, |ui| {
                        ui.set_min_width(ui.available_width());
                        self.issues(ui);
                    });
                }
                ui.add_space(10.);
                heading(ui, tr("rust.spelling.personal", "Личные исправления"));
                let field = ((ui.available_width() - 140.) / 2.).max(120.);
                ui.horizontal(|ui| {
                    ui.add(
                        text_input(&mut self.wrong)
                            .desired_width(field)
                            .hint_text(tr("rust.spelling.wrong", "Ошибка")),
                    );
                    ui.label("→");
                    ui.add(
                        text_input(&mut self.corrected)
                            .desired_width(field)
                            .hint_text(tr("rust.spelling.correct", "Исправление")),
                    );
                    let ready = !self.wrong.trim().is_empty() && !self.corrected.trim().is_empty();
                    if ui
                        .add_enabled(
                            ready,
                            egui::Button::new(tr("rust.spelling.save", "Сохранить пару")),
                        )
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
                ui.add_space(4.);
                if self.pairs.is_empty() {
                    hint(ui, tr("rust.dictionary.empty", "Записей нет"));
                } else {
                    group(ui).show(ui, |ui| {
                        ui.set_min_width(ui.available_width());
                        self.pair_list(ui);
                    });
                }
            });
        });
    }
}

impl ReviewWindow {
    fn issues(&mut self, ui: &mut egui::Ui) {
        for (index, issue) in self.request.issues.iter().enumerate() {
            ui.horizontal(|ui| {
                ui.label(egui::RichText::new(&issue.word).strong());
                ui.weak(&issue.language);
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    if let Some(choice) = self.choices[index]
                        && ui
                            .small_button(tr("rust.spelling.saveAs", "Сохранить как пару"))
                            .clicked()
                    {
                        self.wrong.clone_from(&issue.word);
                        self.corrected.clone_from(&issue.suggestions[choice]);
                    }
                    egui::ComboBox::from_id_salt(("spelling-choice", index))
                        .width(180.)
                        .selected_text(
                            self.choices[index].map_or(
                                tr("rust.spelling.skip", "Пропустить"),
                                |choice| issue.suggestions[choice].as_str(),
                            ),
                        )
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
                });
            });
        }
    }

    fn pair_list(&mut self, ui: &mut egui::Ui) {
        let mut remove = None;
        for (wrong, corrected) in &self.pairs {
            ui.horizontal(|ui| {
                ui.label(format!("{wrong} → {corrected}"));
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), |ui| {
                    if ui
                        .small_button(tr("history.ctx.delete", "Удалить"))
                        .clicked()
                    {
                        remove = Some(wrong.clone());
                    }
                    if ui
                        .small_button(tr("rust.common.edit", "Изменить"))
                        .clicked()
                    {
                        self.wrong.clone_from(wrong);
                        self.corrected.clone_from(corrected);
                    }
                });
            });
        }
        if let Some(word) = remove {
            match self.dictionary.remove_correction(&word) {
                Ok(_) => self.refresh(),
                Err(error) => self.status = error.to_string(),
            }
        }
    }

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
