use crate::locale::text as tr;
#[cfg(any(target_os = "linux", target_os = "macos"))]
use crate::unix_update::{self as update, Update};
#[cfg(windows)]
use crate::windows::update::{self, Update};
use eframe::egui;
use std::{io, sync::mpsc};

/// The update dialog: check, then Update or Cancel, then progress until the
/// new version runs and this window reopens from it.
#[derive(Default)]
pub struct UpdateView {
    state: State,
    pending: Option<mpsc::Receiver<io::Result<Reply>>>,
    #[cfg(windows)]
    install_status: Option<tempfile::TempDir>,
    phase: Phase,
}

#[derive(Default)]
enum State {
    #[default]
    Idle,
    Checking,
    UpToDate,
    Available(Update),
    Installing(String),
    Failed(String),
}

#[derive(Clone, Copy, Default, PartialEq)]
enum Phase {
    #[default]
    Download,
    Verify,
    Restart,
}

enum Reply {
    Available(Option<Update>),
    Installed(String),
}

/// What the window around the dialog should do after this frame.
#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Outcome {
    Open,
    /// The user closed the dialog.
    Closed,
    /// The tray part runs this new version. The window should save its edits
    /// first, then call [`reopen_settings`] and close.
    Restarted(String),
}

/// How long the new Settings may take to show its first frame.
const REOPEN_TIMEOUT: std::time::Duration = std::time::Duration::from_secs(30);

/// Settings of the new version, started but maybe not yet on screen. The old
/// window stays until the new one reports its first frame.
pub struct Reopening {
    child: std::process::Child,
    ready: tempfile::TempDir,
    version: String,
    started: std::time::Instant,
}

#[derive(Debug, PartialEq, Eq)]
pub enum ReopenState {
    Waiting,
    Ready,
    Failed(String),
}

/// Starts Settings of `profile` from the installed `version`, so the restart
/// is visible.
pub fn reopen_settings(version: &str, profile: &std::path::Path) -> io::Result<Reopening> {
    let settings = update::settings_executable(version)?;
    let ready = tempfile::tempdir()?;
    let child = std::process::Command::new(settings)
        .arg("--updated")
        .arg(version)
        .arg(profile)
        .arg(ready.path().join("ready"))
        .spawn()?;
    Ok(Reopening {
        child,
        ready,
        version: version.to_owned(),
        started: std::time::Instant::now(),
    })
}

impl Reopening {
    /// A wait on an already started `child`, for tests of the window logic.
    #[doc(hidden)]
    pub fn for_tests(child: std::process::Child, version: &str) -> io::Result<Self> {
        Ok(Self {
            child,
            ready: tempfile::tempdir()?,
            version: version.to_owned(),
            started: std::time::Instant::now(),
        })
    }

    /// The file the new window writes after its first frame.
    pub fn ready_file(&self) -> std::path::PathBuf {
        self.ready.path().join("ready")
    }

    pub fn version(&self) -> &str {
        &self.version
    }

    pub fn poll(&mut self) -> ReopenState {
        if self.ready.path().join("ready").exists() {
            return ReopenState::Ready;
        }
        match self.child.try_wait() {
            Ok(Some(status)) => {
                return ReopenState::Failed(format!("the new window exited ({status})"));
            }
            Err(error) => return ReopenState::Failed(error.to_string()),
            Ok(None) => {}
        }
        if self.started.elapsed() > REOPEN_TIMEOUT {
            return ReopenState::Failed(format!(
                "the new window did not open within {} s",
                REOPEN_TIMEOUT.as_secs()
            ));
        }
        ReopenState::Waiting
    }
}

/// Called by the new Settings after its first frame: the old window may close.
pub fn signal_ready(path: &std::path::Path) {
    if let Err(error) = std::fs::write(path, b"ready") {
        crate::runtime_log::write(
            "ERROR",
            format_args!("Could not report that Settings opened: {error}"),
        );
    }
}

impl UpdateView {
    pub fn new(initial_check: bool) -> Self {
        Self {
            state: if initial_check {
                State::Checking
            } else {
                State::Idle
            },
            ..Self::default()
        }
    }

    pub fn is_installing(&self) -> bool {
        matches!(self.state, State::Installing(_))
    }

    pub fn show(&mut self, ui: &mut egui::Ui) -> Outcome {
        if matches!(self.state, State::Checking) && self.pending.is_none() {
            self.start(ui, || update::check().map(Reply::Available));
        }
        #[cfg(windows)]
        if let Some(directory) = &self.install_status {
            ui.ctx()
                .request_repaint_after(std::time::Duration::from_millis(200));
            if let Ok(phase) = std::fs::read_to_string(directory.path().join("status")) {
                self.phase = match phase.as_str() {
                    "verify" => Phase::Verify,
                    "restart" => Phase::Restart,
                    _ => Phase::Download,
                };
            }
        }
        if let Some(outcome) = self.receive(ui) {
            return outcome;
        }
        let current = concat!("v", env!("CARGO_PKG_VERSION"));
        ui.set_min_width(380.);
        ui.set_max_width(440.);
        let mut outcome = Outcome::Open;
        match &self.state {
            State::Idle => {
                title(ui, tr("update.dialogTitle", "Typlo updates"));
                muted(
                    ui,
                    &tr("rust.update.current", "Installed version: {0}").replace("{0}", current),
                );
                ui.add_space(12.);
                buttons(ui, |ui| {
                    if ui
                        .add(primary(tr("update.check", "Check for updates")))
                        .clicked()
                    {
                        self.state = State::Checking;
                    }
                    if ui.button(tr("update.close", "Close")).clicked() {
                        outcome = Outcome::Closed;
                    }
                });
            }
            State::Checking => {
                title(ui, tr("update.checking", "Checking for updates…"));
                ui.add_space(8.);
                ui.add(
                    egui::ProgressBar::new(0.)
                        .animate(true)
                        .desired_height(6.)
                        .fill(ui.visuals().selection.stroke.color),
                );
                ui.add_space(12.);
                buttons(ui, |ui| {
                    if ui.button(tr("settings.cancel", "Cancel")).clicked() {
                        self.pending = None;
                        outcome = Outcome::Closed;
                    }
                });
            }
            State::UpToDate => {
                title(ui, tr("rust.update.latest", "Typlo is up to date"));
                muted(
                    ui,
                    &tr("rust.update.current", "Installed version: {0}").replace("{0}", current),
                );
                ui.add_space(12.);
                buttons(ui, |ui| {
                    if ui.add(primary(tr("update.close", "Close"))).clicked() {
                        outcome = Outcome::Closed;
                    }
                });
            }
            State::Available(available) => {
                title(
                    ui,
                    &tr("update.available", "Typlo v{0} is available")
                        .replace("{0}", &available.version),
                );
                muted(
                    ui,
                    &tr(
                        "rust.update.offer",
                        "You have {0}. Typlo downloads the update, checks it and restarts by itself.",
                    )
                    .replace("{0}", current),
                );
                ui.add_space(12.);
                let available = available.clone();
                buttons(ui, |ui| {
                    if ui
                        .add(primary(tr("update.installNow", "Update now")))
                        .clicked()
                    {
                        self.install(ui, available);
                    }
                    if ui.button(tr("settings.cancel", "Cancel")).clicked() {
                        outcome = Outcome::Closed;
                    }
                });
            }
            State::Installing(version) => {
                title(
                    ui,
                    &tr("rust.update.installing", "Updating to v{0}").replace("{0}", version),
                );
                ui.add_space(8.);
                let (progress, label) = match self.phase {
                    Phase::Download => (0.15, tr("rust.update.downloading", "Downloading update…")),
                    Phase::Verify => (
                        0.55,
                        tr(
                            "rust.update.validating",
                            "Verifying update and testing startup…",
                        ),
                    ),
                    Phase::Restart => (0.85, tr("rust.update.restarting", "Restarting Typlo…")),
                };
                ui.add(
                    egui::ProgressBar::new(progress)
                        .animate(true)
                        .desired_height(6.)
                        .fill(ui.visuals().selection.stroke.color),
                );
                ui.add_space(4.);
                muted(ui, label);
                ui.add_space(4.);
                muted(
                    ui,
                    tr(
                        "rust.update.wait",
                        "Typlo restarts by itself when the update is ready.",
                    ),
                );
            }
            State::Failed(error) => {
                title(ui, tr("rust.update.failed", "Update did not finish"));
                ui.add(egui::Label::new(error.as_str()).wrap());
                ui.add_space(12.);
                buttons(ui, |ui| {
                    if ui
                        .add(primary(tr("rust.update.retry", "Try again")))
                        .clicked()
                    {
                        self.state = State::Checking;
                    }
                    if ui.button(tr("update.close", "Close")).clicked() {
                        outcome = Outcome::Closed;
                    }
                });
            }
        }
        outcome
    }

    fn receive(&mut self, ui: &egui::Ui) -> Option<Outcome> {
        let receiver = self.pending.as_ref()?;
        let reply = match receiver.try_recv() {
            Ok(reply) => reply,
            Err(mpsc::TryRecvError::Empty) => return None,
            Err(mpsc::TryRecvError::Disconnected) => Err(io::Error::other(tr(
                "update.checkFailed",
                "Could not check for updates",
            ))),
        };
        self.pending = None;
        #[cfg(windows)]
        {
            self.install_status = None;
        }
        ui.ctx().request_repaint();
        match reply {
            Ok(Reply::Available(Some(available))) => self.state = State::Available(available),
            Ok(Reply::Available(None)) => self.state = State::UpToDate,
            Ok(Reply::Installed(version)) => return Some(Outcome::Restarted(version)),
            Err(error) => self.state = State::Failed(error.to_string()),
        }
        None
    }

    fn install(&mut self, ui: &egui::Ui, available: Update) {
        let version = available.version.clone();
        self.phase = Phase::Download;
        let installed = version.clone();
        #[cfg(windows)]
        {
            let directory = match update::temporary_workspace() {
                Ok(directory) => directory,
                Err(error) => {
                    self.state = State::Failed(error.to_string());
                    return;
                }
            };
            let path = directory.path().join("status");
            self.start(ui, move || {
                update::spawn_installer(&available, &path).map(|()| Reply::Installed(installed))
            });
            if self.pending.is_some() {
                self.install_status = Some(directory);
            }
        }
        #[cfg(not(windows))]
        self.start(ui, move || {
            update::spawn_installer(&available).map(|()| Reply::Installed(installed))
        });
        if self.pending.is_some() {
            self.state = State::Installing(version);
        }
    }

    fn start(&mut self, ui: &egui::Ui, run: impl FnOnce() -> io::Result<Reply> + Send + 'static) {
        let (sender, receiver) = mpsc::sync_channel(1);
        let context = ui.ctx().clone();
        match std::thread::Builder::new()
            .name("typlo-update-ui".into())
            .spawn(move || {
                let _ = sender.send(run());
                context.request_repaint();
            }) {
            Ok(_) => self.pending = Some(receiver),
            Err(error) => self.state = State::Failed(error.to_string()),
        }
    }
}

fn title(ui: &mut egui::Ui, text: &str) {
    ui.label(egui::RichText::new(text).size(18.).strong());
    ui.add_space(4.);
}

fn muted(ui: &mut egui::Ui, text: &str) {
    ui.add(
        egui::Label::new(
            egui::RichText::new(text)
                .size(13.)
                .color(ui.visuals().weak_text_color()),
        )
        .wrap(),
    );
}

fn primary(label: &str) -> egui::Button<'_> {
    crate::appearance::primary_button(label).min_size(egui::vec2(96., 32.))
}

/// Dialog buttons on the right; the first one added is the rightmost.
fn buttons(ui: &mut egui::Ui, add: impl FnOnce(&mut egui::Ui)) {
    ui.with_layout(egui::Layout::right_to_left(egui::Align::Center), add);
}

#[cfg(test)]
mod tests {
    use super::*;

    fn reopening(code: i32) -> Reopening {
        #[cfg(windows)]
        let child = std::process::Command::new("cmd")
            .args(["/C", &format!("exit {code}")])
            .spawn()
            .unwrap();
        #[cfg(not(windows))]
        let child = std::process::Command::new("sh")
            .args(["-c", &format!("exit {code}")])
            .spawn()
            .unwrap();
        Reopening {
            child,
            ready: tempfile::tempdir().unwrap(),
            version: "9.9.9".into(),
            started: std::time::Instant::now(),
        }
    }

    #[test]
    fn a_new_window_that_exits_before_drawing_is_a_failure() {
        let mut reopening = reopening(3);
        let _ = reopening.child.wait();
        assert!(matches!(reopening.poll(), ReopenState::Failed(_)));
    }

    #[test]
    fn the_first_frame_of_the_new_window_lets_the_old_one_close() {
        let mut reopening = reopening(0);
        signal_ready(&reopening.ready.path().join("ready"));
        assert_eq!(reopening.poll(), ReopenState::Ready);
    }

    #[test]
    fn a_new_window_that_never_draws_times_out() {
        let mut reopening = reopening(0);
        // A child that keeps running without drawing.
        #[cfg(windows)]
        let silent = std::process::Command::new("ping")
            .args(["-n", "30", "127.0.0.1"])
            .stdout(std::process::Stdio::null())
            .spawn();
        #[cfg(not(windows))]
        let silent = std::process::Command::new("sleep").arg("30").spawn();
        reopening.child = silent.unwrap();
        assert_eq!(reopening.poll(), ReopenState::Waiting);
        reopening.started -= REOPEN_TIMEOUT + std::time::Duration::from_secs(1);
        let state = reopening.poll();
        let _ = reopening.child.kill();
        let _ = reopening.child.wait();
        assert!(
            matches!(&state, ReopenState::Failed(error) if error.contains("did not open")),
            "{state:?}"
        );
    }
}
