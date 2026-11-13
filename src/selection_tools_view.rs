use crate::locale::text as tr;
use crate::{
    dictionary::{Dictionary, Entry},
    lexicon::{LANGUAGES, Vocabulary},
    selection_tools::Request,
};
use eframe::egui;
use std::{
    io::{self, Read, Write},
    path::PathBuf,
};

#[cfg(unix)]
pub fn run_preview(profile: PathBuf) -> Result<(), Box<dyn std::error::Error>> {
    use std::io::{BufRead, BufReader};
    let mut input = BufReader::new(io::stdin());
    let mut request = Vec::new();
    input
        .by_ref()
        .take(64 * 1024)
        .read_until(b'\n', &mut request)?;
    if request.last() != Some(&b'\n') {
        return Err(
            io::Error::new(io::ErrorKind::InvalidData, "Invalid layout preview request").into(),
        );
    }
    let candidates: Vec<crate::lexicon::Candidate> = serde_json::from_slice(&request)?;
    if candidates.is_empty() || candidates.len() > 256 {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "Invalid layout preview candidates",
        )
        .into());
    }
    let settings = crate::locale::load(&profile);
    let options = eframe::NativeOptions {
        viewport: crate::appearance::window()
            .with_inner_size([340., 64.])
            .with_position([24., 40.])
            .with_active(false)
            .with_always_on_top()
            .with_decorations(false)
            .with_taskbar(false)
            .with_mouse_passthrough(true),
        renderer: eframe::Renderer::Glow,
        #[cfg(target_os = "macos")]
        event_loop_builder: Some(Box::new(|builder| {
            use winit::platform::macos::{ActivationPolicy, EventLoopBuilderExtMacOS};
            builder
                .with_activation_policy(ActivationPolicy::Accessory)
                .with_activate_ignoring_other_apps(false);
        })),
        ..Default::default()
    };
    eframe::run_native(
        tr("rust.field.choices", "Layout choices"),
        options,
        Box::new(move |creation| {
            crate::appearance::configure(&creation.egui_ctx);
            crate::locale::theme(&creation.egui_ctx, settings.ui_theme);
            crate::locale::fonts(&creation.egui_ctx, &settings.ui_language);
            let repaint = creation.egui_ctx.clone();
            let (send, updates) = std::sync::mpsc::channel();
            std::thread::spawn(move || {
                let mut byte = [0];
                while input.read_exact(&mut byte).is_ok() {
                    if send.send(usize::from(byte[0])).is_err() {
                        return;
                    }
                    repaint.request_repaint();
                }
                repaint.send_viewport_cmd(egui::ViewportCommand::Close);
            });
            Ok(Box::new(ChoicePreview {
                candidates,
                updates,
                index: 0,
            }))
        }),
    )?;
    Ok(())
}

#[cfg(unix)]
struct ChoicePreview {
    candidates: Vec<crate::lexicon::Candidate>,
    updates: std::sync::mpsc::Receiver<usize>,
    index: usize,
}

#[cfg(unix)]
impl eframe::App for ChoicePreview {
    fn ui(&mut self, ui: &mut egui::Ui, _: &mut eframe::Frame) {
        if let Some(index) = self
            .updates
            .try_iter()
            .last()
            .filter(|&index| index < self.candidates.len())
        {
            self.index = index;
        }
        let candidate = &self.candidates[self.index];
        ui.vertical_centered(|ui| {
            ui.heading(&candidate.text);
            ui.label(format!(
                "{} · {} / {}",
                candidate.language.name(),
                self.index + 1,
                self.candidates.len()
            ));
        });
    }
}

pub fn run(profile: PathBuf) -> Result<(), Box<dyn std::error::Error>> {
    let appearance = crate::locale::load(&profile);
    let mut request: Request = serde_json::from_reader(io::stdin().lock().take(256 * 1024))?;
    crate::config::available_layouts();
    let title = match &mut request {
        Request::Add { language, .. } => {
            *language = (*language).min(LANGUAGES.len() - 1);
            tr("rust.dictionary.addTitle", "Добавить в словарь")
        }
        Request::Choices { .. } => tr("rust.field.choices", "Варианты раскладки"),
        Request::Field { .. } => tr("rust.field.compatibility", "Совместимость поля"),
    };
    eframe::run_native(
        title,
        eframe::NativeOptions {
            viewport: crate::appearance::window()
                .with_inner_size([525., 270.])
                .with_always_on_top(),
            renderer: eframe::Renderer::Glow,
            ..Default::default()
        },
        Box::new(move |creation| {
            crate::appearance::configure(&creation.egui_ctx);
            crate::locale::theme(&creation.egui_ctx, appearance.ui_theme);
            crate::locale::fonts(&creation.egui_ctx, &appearance.ui_language);
            Ok(Box::new(Window {
                request,
                dictionary: Dictionary::new(&profile),
                vocabulary: Vocabulary::installed()?,
                exclusion: false,
                status: String::new(),
                priority: None,
            }))
        }),
    )?;
    Ok(())
}

struct Window {
    request: Request,
    dictionary: Dictionary,
    vocabulary: Vocabulary,
    exclusion: bool,
    status: String,
    priority: Option<Entry>,
}

impl eframe::App for Window {
    fn ui(&mut self, ui: &mut egui::Ui, _: &mut eframe::Frame) {
        match &mut self.request {
            Request::Add { word, language } => {
                ui.label(tr(
                    "dictionary.hint",
                    "Enter the word as it should appear after correction.",
                ));
                ui.horizontal_wrapped(|ui| {
                    ui.text_edit_singleline(word);
                    if !self.exclusion {
                        egui::ComboBox::from_id_salt("add-selected-language")
                            .selected_text(LANGUAGES[*language])
                            .show_ui(ui, |ui| {
                                for (index, code) in LANGUAGES.into_iter().enumerate() {
                                    ui.selectable_value(language, index, code);
                                }
                            });
                    }
                });
                ui.checkbox(
                    &mut self.exclusion,
                    tr("rust.dictionary.never", "Никогда не исправлять"),
                );
                let language_code = if self.exclusion {
                    ""
                } else {
                    LANGUAGES[*language]
                };
                let entry = Entry::new(word, language_code);
                if self.priority.as_ref() != entry.as_ref().ok() {
                    self.priority = None;
                }
                if ui.button(tr("rust.common.add", "Добавить")).clicked() {
                    self.status = match entry.and_then(|entry| {
                        let result = self
                            .dictionary
                            .add(std::slice::from_ref(&entry), &mut self.vocabulary)?;
                        if result.inserted == 1 {
                            return Ok(tr("manage.saved", "Saved").into());
                        }
                        if !entry.language.is_empty()
                            && !self.dictionary.contains(&entry.word, &entry.language)?
                        {
                            self.priority = Some(entry.clone());
                            return Ok(tr(
                                "dictionary.existsBundled",
                                "“{0}” is already in the built-in dictionary.",
                            )
                            .replace("{0}", &entry.word));
                        }
                        Ok(tr("rust.dictionary.duplicate", "Слово уже есть в словаре").into())
                    }) {
                        Ok(status) => status,
                        Err(error) => error.to_string(),
                    };
                }
                if self.priority.is_some()
                    && ui
                        .button(tr("dictionary.addPriority", "Add personal priority"))
                        .clicked()
                    && let Some(entry) = self.priority.take()
                {
                    self.status = match self.dictionary.add_priority(&entry, &mut self.vocabulary) {
                        Ok(true) => tr("manage.saved", "Saved").into(),
                        Ok(false) => {
                            tr("rust.dictionary.duplicate", "Слово уже есть в словаре").into()
                        }
                        Err(error) => error.to_string(),
                    };
                }
            }
            Request::Choices { origin, choices } => {
                if choices.is_empty() {
                    ui.label(tr(
                        "rust.field.noChoices",
                        "No dictionary matches in the allowed layouts.",
                    ));
                }
                for (index, choice) in choices.iter().enumerate() {
                    let selected = ui
                        .button(format!("{} · {}", choice.text, choice.language.name()))
                        .clicked();
                    let first_via_keyboard =
                        index == 0 && ui.input(|input| input.key_pressed(egui::Key::Enter));
                    if selected || first_via_keyboard {
                        match accept(*origin, index) {
                            Ok(()) => ui.ctx().send_viewport_cmd(egui::ViewportCommand::Close),
                            Err(error) => self.status = error.to_string(),
                        }
                    }
                }
            }
            Request::Field {
                method,
                last_automatic_micros,
                reason,
            } => {
                ui.label(method.as_str());
                if let Some(reason) = reason {
                    ui.label(reason.message());
                }
                if let Some(micros) = last_automatic_micros {
                    ui.label(format!(
                        "Последняя проверка автозамены заняла {:.2} мс",
                        *micros as f64 / 1000.
                    ));
                }
            }
        }
        if !self.status.is_empty() {
            ui.label(&self.status);
        }
        if ui.button(tr("update.close", "Закрыть")).clicked()
            || ui.input(|input| input.key_pressed(egui::Key::Escape))
        {
            ui.ctx().send_viewport_cmd(egui::ViewportCommand::Close);
        }
    }
}

fn accept(origin: usize, choice: usize) -> io::Result<()> {
    crate::restore_edit_origin(origin)?;
    let mut output = io::stdout().lock();
    serde_json::to_writer(&mut output, &Some(choice)).map_err(io::Error::other)?;
    output.flush()
}
