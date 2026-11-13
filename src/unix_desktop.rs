use std::{
    env,
    fs::{self, File, OpenOptions},
    io::{self, Seek, Write},
    path::{Path, PathBuf},
    process::{Child, Command, Stdio},
};

const RESIDENT: &str = "resident.lock";
const STOP: &str = "resident.stop";

pub struct Resident {
    _lock: File,
    pub(crate) signal: Shutdown,
}

#[derive(Clone)]
pub(crate) struct Shutdown {
    request: PathBuf,
    token: String,
}

impl Shutdown {
    pub fn requested(&self) -> bool {
        fs::read_to_string(&self.request).is_ok_and(|content| content == self.token)
    }
}

pub fn instance(profile: &Path) -> io::Result<Resident> {
    fs::create_dir_all(profile)?;
    let mut lock = lock_file(&profile.join(RESIDENT))?;
    lock.try_lock()
        .map_err(|error| io::Error::other(format!("Could not start Typlo resident: {error}")))?;
    crate::unix_history::reset_session_pause(profile)?;
    let mut bytes = [0u8; 16];
    getrandom::fill(&mut bytes).map_err(|error| io::Error::other(error.to_string()))?;
    let token: String = bytes
        .into_iter()
        .map(|byte| format!("{byte:02x}"))
        .collect();
    lock.set_len(0)?;
    lock.rewind()?;
    lock.write_all(token.as_bytes())?;
    lock.sync_all()?;
    Ok(Resident {
        _lock: lock,
        signal: Shutdown {
            request: profile.join(STOP),
            token,
        },
    })
}

#[derive(Clone, Copy)]
pub(crate) enum TrayAction {
    Shortcuts,
    Tools,
    Dictionary,
    PauseTen,
    PauseHour,
    PauseSession,
    Resume,
    KeepAwake,
    Update,
}

impl TrayAction {
    pub(crate) const ALL: [Self; 9] = [
        Self::Shortcuts,
        Self::Tools,
        Self::Dictionary,
        Self::PauseTen,
        Self::PauseHour,
        Self::PauseSession,
        Self::Resume,
        Self::KeepAwake,
        Self::Update,
    ];

    pub(crate) fn label(self) -> &'static str {
        use crate::locale::text as tr;
        match self {
            Self::Shortcuts => tr("shortcuts.menu", "Keyboard shortcuts…"),
            Self::Tools => tr("tools.title", "Typlo tools"),
            Self::Dictionary => tr("manage.title", "Personal dictionary"),
            Self::PauseTen => tr("tray.pause10", "Pause history for 10 minutes"),
            Self::PauseHour => tr("tray.pause60", "Pause history for one hour"),
            Self::PauseSession => tr("tray.pauseRestart", "Pause history until restart"),
            Self::Resume => tr("rust.history.resume", "Resume history"),
            Self::KeepAwake => tr("tray.keepAwake", "Prevent screen lock"),
            Self::Update => tr("update.check", "Check for updates…"),
        }
    }

    pub(crate) fn run(self, profile: &Path) -> io::Result<()> {
        use crate::{config, unix_history};
        use std::time::Duration;
        let mode = match self {
            Self::Shortcuts => "--shortcuts",
            Self::Tools => "--tools",
            Self::Dictionary => "--dictionary",
            Self::Update => "--open-updates",
            Self::PauseTen => return unix_history::pause(profile, Some(Duration::from_secs(600))),
            Self::PauseHour => {
                return unix_history::pause(profile, Some(Duration::from_secs(3600)));
            }
            Self::PauseSession => {
                return unix_history::pause(profile, Some(Duration::from_secs(u64::MAX)));
            }
            Self::Resume => return unix_history::pause(profile, None),
            Self::KeepAwake => {
                let mut settings = config::load(profile)?;
                settings.keep_screen_on = !settings.keep_screen_on;
                return config::save(profile, &settings);
            }
        };
        let mut child = Command::new(crate::jobs::gui()?)
            .arg(mode)
            .arg(profile)
            .spawn()?;
        std::thread::spawn(move || {
            let _ = child.wait();
        });
        Ok(())
    }
}

pub(crate) fn lock_file(path: &Path) -> io::Result<File> {
    OpenOptions::new()
        .create(true)
        .truncate(false)
        .read(true)
        .write(true)
        .open(path)
}

pub(crate) fn request_shutdown(profile: &Path) -> io::Result<()> {
    let path = profile.join(RESIDENT);
    let lock = lock_file(&path)?;
    match lock.try_lock() {
        Ok(()) => return Ok(()),
        Err(std::fs::TryLockError::WouldBlock) => {}
        Err(error) => return Err(io::Error::other(error)),
    }
    let token = fs::read_to_string(path)?;
    if token.len() != 32 || !token.bytes().all(|byte| byte.is_ascii_hexdigit()) {
        return Err(io::Error::other("Typlo resident identity is unavailable"));
    }
    atomic_write(&profile.join(STOP), token.as_bytes())
}

pub(crate) fn wait_for_exit(profile: &Path, timeout: std::time::Duration) -> io::Result<()> {
    let path = profile.join(RESIDENT);
    let deadline = std::time::Instant::now() + timeout;
    loop {
        match lock_file(&path)?.try_lock() {
            Ok(()) => return Ok(()),
            Err(std::fs::TryLockError::WouldBlock) if std::time::Instant::now() < deadline => {
                std::thread::sleep(std::time::Duration::from_millis(25));
            }
            Err(std::fs::TryLockError::WouldBlock) => return Err(io::ErrorKind::TimedOut.into()),
            Err(error) => return Err(io::Error::other(error)),
        }
    }
}

pub(crate) fn atomic_write(path: &Path, bytes: &[u8]) -> io::Result<()> {
    let directory = path
        .parent()
        .ok_or_else(|| io::Error::other("Missing output directory"))?;
    fs::create_dir_all(directory)?;
    let mut temp = tempfile::NamedTempFile::new_in(directory)?;
    temp.write_all(bytes)?;
    temp.as_file().sync_all()?;
    temp.persist(path).map_err(|error| error.error)?;
    Ok(())
}

pub fn autostart(profile: &Path, enabled: bool) -> io::Result<()> {
    if profile != crate::config::profile() {
        return Ok(());
    }
    let home = env::var_os("HOME")
        .map(PathBuf::from)
        .ok_or_else(|| io::Error::other("Cannot locate the user directory"))?;
    #[cfg(target_os = "linux")]
    let path = env::var_os("XDG_CONFIG_HOME")
        .map(PathBuf::from)
        .unwrap_or_else(|| home.join(".config"))
        .join("autostart")
        .join("typlo-rust.desktop");
    #[cfg(target_os = "macos")]
    let path = home
        .join("Library")
        .join("LaunchAgents")
        .join("com.bitcodepro.typlo-rust.plist");
    if !enabled {
        return match fs::remove_file(path) {
            Ok(()) => Ok(()),
            Err(error) if error.kind() == io::ErrorKind::NotFound => Ok(()),
            Err(error) => Err(error),
        };
    }
    let directory = path.parent().expect("autostart path has a directory");
    let executable = env::current_exe()?.with_file_name("typlo-rust");
    let executable = executable
        .to_str()
        .filter(|value| !value.contains(['\r', '\n']))
        .ok_or_else(|| io::Error::other("Invalid executable path"))?;
    #[cfg(target_os = "linux")]
    let entry = {
        let mut path = String::new();
        for character in executable.chars() {
            match character {
                '%' => path.push_str("%%"),
                '\\' => path.push_str("\\\\\\\\"),
                '"' | '$' => {
                    path.push_str("\\\\");
                    path.push(character);
                }
                character if character == char::from(96) => {
                    path.push_str("\\\\");
                    path.push(character);
                }
                other => path.push(other),
            }
        }
        format!(
            "[Desktop Entry]\nType=Application\nName=Typlo2.0\nExec=\"{path}\"\nTerminal=false\n"
        )
    };
    #[cfg(target_os = "macos")]
    let entry = {
        let executable = executable
            .replace('&', "&amp;")
            .replace('<', "&lt;")
            .replace('>', "&gt;");
        format!(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<plist version=\"1.0\"><dict>\
          <key>Label</key><string>com.bitcodepro.typlo-rust</string>\
          <key>ProgramArguments</key><array><string>{executable}</string></array>\
          <key>RunAtLoad</key><true/></dict></plist>\n"
        )
    };
    let _ = directory;
    atomic_write(&path, entry.as_bytes())
}

#[derive(Default)]
pub struct Inhibit {
    child: Option<Child>,
    requested: bool,
}

impl Inhibit {
    pub fn apply(&mut self, enabled: bool) -> io::Result<()> {
        if enabled == self.requested {
            return Ok(());
        }
        self.requested = enabled;
        if !enabled {
            self.stop();
            return Ok(());
        }
        #[cfg(target_os = "linux")]
        let mut command = {
            let mut command = Command::new("systemd-inhibit");
            command.args([
                "--what=idle",
                "--mode=block",
                "--who=Typlo",
                "--why=Keep screen on",
                "sleep",
                "infinity",
            ]);
            parent_lifetime(&mut command);
            command
        };
        #[cfg(target_os = "macos")]
        let mut command = {
            let mut command = Command::new("caffeinate");
            command
                .arg("-d")
                .arg("-i")
                .arg("-w")
                .arg(std::process::id().to_string());
            command
        };
        self.child = Some(command.stdin(Stdio::null()).stdout(Stdio::null()).spawn()?);
        Ok(())
    }

    fn stop(&mut self) {
        if let Some(mut child) = self.child.take() {
            let _ = child.kill();
            let _ = child.wait();
        }
    }
}

impl Drop for Inhibit {
    fn drop(&mut self) {
        self.stop();
    }
}

#[cfg(target_os = "linux")]
pub fn parent_lifetime(command: &mut Command) {
    use std::os::unix::process::CommandExt;
    let parent = std::process::id() as i32;
    // Only async-signal-safe system calls are made between fork and exec.
    unsafe {
        command.pre_exec(move || {
            if libc::prctl(libc::PR_SET_PDEATHSIG, libc::SIGTERM) == -1 || libc::getppid() != parent
            {
                return Err(io::Error::from_raw_os_error(libc::ECHILD));
            }
            Ok(())
        });
    }
}
