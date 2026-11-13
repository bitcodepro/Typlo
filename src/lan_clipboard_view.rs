use crate::{
    locale::text as tr,
    windows::{lan_clipboard, reload_lan_clipboard},
};
use eframe::egui;
use std::{io, path::Path, sync::mpsc};

pub struct Settings {
    paired: bool,
    address: String,
    code: String,
    status: String,
    checking: Option<mpsc::Receiver<io::Result<String>>>,
}

impl Settings {
    pub fn new(profile: &Path) -> Self {
        let mut view = Self {
            paired: false,
            address: String::new(),
            code: String::new(),
            status: String::new(),
            checking: None,
        };
        if let Err(error) = view.refresh(profile) {
            view.status = error.to_string();
        }
        view
    }

    fn refresh(&mut self, profile: &Path) -> io::Result<()> {
        let pair = lan_clipboard::paired_address(profile)?;
        self.paired = pair.is_some();
        self.address = pair.unwrap_or_default();
        Ok(())
    }

    pub fn show(&mut self, ui: &mut egui::Ui, profile: &Path) {
        if let Some(receiver) = &self.checking {
            match receiver.try_recv() {
                Ok(result) => {
                    self.checking = None;
                    self.status = result.unwrap_or_else(|error| error.to_string());
                }
                Err(mpsc::TryRecvError::Disconnected) => {
                    self.checking = None;
                    self.status = "Connection check interrupted".into();
                }
                Err(mpsc::TryRecvError::Empty) => {}
            }
        }
        ui.collapsing(tr("rust.lan.heading", "Clipboard sharing with another computer"), |ui| {
            ui.label(tr(
                "rust.lan.instructions",
                "On both computers, copy this computer’s code and enter it on the other computer. Then use ↗ next to an item in history to send it.",
            ));
            if ui.button(tr("rust.lan.copyCode", "Copy this computer’s code")).clicked() {
                self.status = match lan_clipboard::invite(profile) {
                    Ok(code) => {
                        ui.ctx().copy_text(code);
                        tr("rust.lan.copied", "Pairing code copied").into()
                    }
                    Err(error) => error.to_string(),
                };
            }
            if self.paired {
                ui.label(tr("rust.lan.paired", "A computer is paired"));
            } else {
                ui.label(tr("rust.lan.otherCode", "Other computer’s code"));
                ui.add(egui::TextEdit::singleline(&mut self.code).desired_width(f32::INFINITY));
            }
            ui.label(tr("rust.lan.address", "Other computer’s IPv4 address (optional)"));
            ui.add(egui::TextEdit::singleline(&mut self.address).hint_text("192.168.1.20").char_limit(15));
            ui.horizontal_wrapped(|ui| {
                if self.paired {
                    if ui.add_enabled(self.checking.is_none(), egui::Button::new(tr("rust.lan.check", "Check connection"))).clicked() {
                        let (sender, receiver) = mpsc::sync_channel(1);
                        let profile = profile.to_owned();
                        let ctx = ui.ctx().clone();
                        std::thread::spawn(move || {
                            let _ = sender.send(lan_clipboard::check_connection(&profile));
                            ctx.request_repaint();
                        });
                        self.checking = Some(receiver);
                        self.status = tr("rust.lan.checking", "Checking connection…").into();
                    }
                    if ui.button(tr("rust.lan.saveAddress", "Save address")).clicked() {
                        let result = lan_clipboard::set_address(profile, &self.address);
                        self.changed(profile, result, tr("rust.lan.addressSaved", "Address saved"));
                    }
                    if ui.button(tr("rust.lan.forget", "Unpair")).clicked() {
                        let result = lan_clipboard::unpair(profile);
                        self.changed(profile, result, tr("rust.lan.forgot", "Computer unpaired"));
                    }
                } else if ui
                    .add_enabled(!self.code.trim().is_empty(), egui::Button::new(tr("rust.lan.pair", "Pair computer")))
                    .clicked()
                {
                    let result = lan_clipboard::pair(profile, &self.code, &self.address);
                    self.changed(profile, result, tr("rust.lan.paired", "A computer is paired"));
                    if self.paired {
                        self.code.clear();
                    }
                }
                if ui.button(tr("rust.lan.received", "Open received files")).clicked() {
                    self.status = match lan_clipboard::open_received_folder(profile) {
                        Ok(_) => String::new(),
                        Err(error) => error.to_string(),
                    };
                }
            });
            if !self.status.is_empty() {
                ui.label(&self.status);
            }
        });
    }

    fn changed(&mut self, profile: &Path, result: io::Result<()>, success: &str) {
        self.status = match result.and_then(|()| {
            self.checking = None;
            reload_lan_clipboard();
            self.refresh(profile)
        }) {
            Ok(()) => success.into(),
            Err(error) => error.to_string(),
        };
    }
}
