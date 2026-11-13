use crate::locale::text as tr;
#[cfg(any(target_os = "linux", target_os = "macos"))]
use crate::unix_update::{self as update, Update};
#[cfg(windows)]
use crate::windows::update::{self, Update};
use eframe::egui;
use std::{io, sync::mpsc};

#[derive(Default)]
pub struct UpdateView {
    available: Option<Update>,
    pending: Option<mpsc::Receiver<io::Result<Reply>>>,
    status: String,
    initial_check: bool,
    #[cfg(windows)]
    install_status: Option<tempfile::TempDir>,
}

enum Reply {
    Available(Option<Update>),
    Installed,
}

impl UpdateView {
    pub fn new(initial_check: bool) -> Self {
        Self {
            initial_check,
            ..Self::default()
        }
    }

    pub fn show(&mut self, ui: &mut egui::Ui) {
        ui.label(concat!("v", env!("CARGO_PKG_VERSION")));
        #[cfg(windows)]
        if let Some(directory) = &self.install_status {
            ui.ctx()
                .request_repaint_after(std::time::Duration::from_millis(250));
            if let Ok(phase) = std::fs::read_to_string(directory.path().join("status")) {
                self.status = match phase.as_str() {
                    "download" => tr("rust.update.downloading", "Downloading update…"),
                    "verify" => tr(
                        "rust.update.validating",
                        "Verifying update and testing startup…",
                    ),
                    "restart" => tr("rust.update.restarting", "Restarting Typlo…"),
                    _ => &self.status,
                }
                .to_owned();
            }
        }
        if std::mem::take(&mut self.initial_check) {
            ui.scroll_to_cursor(Some(egui::Align::Max));
            self.start(
                ui,
                tr("update.checking", "Проверяю GitHub…"),
                || update::check().map(Reply::Available),
            );
        }
        if let Some(receiver) = &self.pending {
            let reply = match receiver.try_recv() {
                Ok(reply) => Some(reply),
                Err(mpsc::TryRecvError::Disconnected) => Some(Err(io::Error::other(tr(
                    "update.checkFailed",
                    "Could not check for updates",
                )))),
                Err(mpsc::TryRecvError::Empty) => None,
            };
            if let Some(reply) = reply {
                self.pending = None;
                #[cfg(windows)]
                {
                    self.install_status = None;
                }
                self.status = match reply {
                    Ok(Reply::Available(Some(update))) => {
                        let text = tr("update.available", "Typlo v{0} is available")
                            .replace("{0}", &update.version);
                        self.available = Some(update);
                        text
                    }
                    Ok(Reply::Available(None)) => {
                        self.available = None;
                        tr("rust.update.none", "Новой сборки Typlo Rust нет").into()
                    }
                    Ok(Reply::Installed) => {
                        ui.ctx().send_viewport_cmd(egui::ViewportCommand::Close);
                        tr("rust.update.installed", "Новая версия запущена").into()
                    }
                    Err(error) => error.to_string(),
                };
            }
        }
        ui.horizontal_wrapped(|ui| {
            if ui
                .add_enabled(
                    self.pending.is_none(),
                    egui::Button::new(tr("update.check", "Проверить обновления")),
                )
                .clicked()
            {
                self.start(
                    ui,
                    tr("update.checking", "Проверяю GitHub…"),
                    || update::check().map(Reply::Available),
                );
            }
            if let Some(update) = &self.available
                && ui
                    .add_enabled(
                        self.pending.is_none(),
                        egui::Button::new(tr("update.installNow", "Обновить сейчас")),
                    )
                    .clicked()
            {
                let update = update.clone();
                #[cfg(windows)]
                match tempfile::tempdir() {
                    Ok(directory) => {
                        let path = directory.path().join("status");
                        self.start(
                            ui,
                            tr("update.checking", "Checking for updates…"),
                            move || {
                                update::spawn_installer(&update, &path).map(|()| Reply::Installed)
                            },
                        );
                        if self.pending.is_some() {
                            self.install_status = Some(directory);
                        }
                    }
                    Err(error) => self.status = error.to_string(),
                }
                #[cfg(not(windows))]
                self.start(
                    ui,
                    tr("rust.update.verifying", "Загружаю и проверяю обновление…"),
                    move || update::spawn_installer(&update).map(|()| Reply::Installed),
                );
            }
        });
        if !self.status.is_empty() {
            ui.label(&self.status);
        }
    }

    fn start(
        &mut self,
        ui: &egui::Ui,
        status: &str,
        run: impl FnOnce() -> io::Result<Reply> + Send + 'static,
    ) {
        let (sender, receiver) = mpsc::sync_channel(1);
        let context = ui.ctx().clone();
        match std::thread::Builder::new()
            .name("typlo-update-ui".into())
            .spawn(move || {
                let _ = sender.send(run());
                context.request_repaint();
            }) {
            Ok(_) => {
                self.pending = Some(receiver);
                self.status = status.into();
            }
            Err(error) => self.status = error.to_string(),
        }
    }
}
