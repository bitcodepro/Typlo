use std::{
    fmt,
    fs::{self, OpenOptions},
    io::{self, Write},
    path::{Path, PathBuf},
    sync::OnceLock,
    time::{SystemTime, UNIX_EPOCH},
};

const MAX_LOG_BYTES: u64 = 256 * 1024;
static LOG: OnceLock<RuntimeLog> = OnceLock::new();

struct RuntimeLog {
    directory: PathBuf,
    component: &'static str,
}

pub fn initialize(profile: &Path, component: &'static str) {
    if LOG
        .set(RuntimeLog {
            directory: profile.join("logs"),
            component,
        })
        .is_err()
    {
        return;
    }
    let previous = std::panic::take_hook();
    std::panic::set_hook(Box::new(move |info| {
        // Panic payloads can contain clipboard contents. The location is enough
        // to identify the failing code without recording the payload.
        if let Some(location) = info.location() {
            write(
                "PANIC",
                format_args!("{}:{}", location.file(), location.line()),
            );
        } else {
            write("PANIC", format_args!("No source location"));
        }
        previous(info);
    }));
}

pub fn write(level: &str, message: fmt::Arguments<'_>) {
    if let Some(log) = LOG.get() {
        let _ = log.append(level, message, MAX_LOG_BYTES);
    }
}

pub fn report_error(message: fmt::Arguments<'_>) {
    write("ERROR", message);
    eprintln!("{message}");
}

#[macro_export]
macro_rules! runtime_error {
    ($($arg:tt)*) => {{
        $crate::runtime_log::report_error(format_args!($($arg)*));
    }};
}

impl RuntimeLog {
    fn append(&self, level: &str, message: fmt::Arguments<'_>, limit: u64) -> io::Result<()> {
        fs::create_dir_all(&self.directory)?;
        // A separate lock file serializes rotation across the resident, UI and updater.
        let lock = OpenOptions::new()
            .create(true)
            .truncate(false)
            .read(true)
            .write(true)
            .open(self.directory.join("typlo.lock"))?;
        lock.lock()?;
        let path = self.directory.join("typlo.log");
        let timestamp = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap_or_default()
            .as_millis();
        let mut message = message.to_string().replace(['\r', '\n'], " ");
        message.truncate(message.floor_char_boundary(2048));
        let line = format!(
            "{timestamp} {level} {} v{} pid={} {message}\n",
            self.component,
            env!("CARGO_PKG_VERSION"),
            std::process::id()
        );
        if fs::metadata(&path).is_ok_and(|file| file.len() + line.len() as u64 > limit) {
            let previous = self.directory.join("typlo.previous.log");
            match fs::remove_file(&previous) {
                Ok(()) => {}
                Err(error) if error.kind() == io::ErrorKind::NotFound => {}
                Err(error) => return Err(error),
            }
            fs::rename(&path, previous)?;
        }
        OpenOptions::new()
            .create(true)
            .append(true)
            .open(path)?
            .write_all(line.as_bytes())
    }
}

#[cfg(windows)]
pub(crate) struct ResidentSession {
    marker: PathBuf,
}

#[cfg(windows)]
impl ResidentSession {
    pub(crate) fn start(profile: &Path) -> Self {
        let marker = profile.join("logs/resident-running");
        if let Ok(previous) = fs::read_to_string(&marker)
            && let Ok(pid) = previous.trim().parse::<u32>()
        {
            write(
                "WARN",
                format_args!("Previous resident did not record a shutdown (pid={pid})"),
            );
        }
        write("INFO", format_args!("Resident starting"));
        let _ = fs::write(&marker, std::process::id().to_string());
        Self { marker }
    }
}

#[cfg(windows)]
impl Drop for ResidentSession {
    fn drop(&mut self) {
        write("INFO", format_args!("Resident stopped"));
        let _ = fs::remove_file(&self.marker);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn log_rotates_and_keeps_only_the_previous_file() {
        let dir = tempfile::tempdir().unwrap();
        let log = RuntimeLog {
            directory: dir.path().to_owned(),
            component: "test",
        };
        for index in 0..3 {
            log.append("ERROR", format_args!("fixture {index}"), 90)
                .unwrap();
        }
        assert!(
            fs::read_to_string(dir.path().join("typlo.log"))
                .unwrap()
                .contains("fixture 2")
        );
        let previous = fs::read_to_string(dir.path().join("typlo.previous.log")).unwrap();
        assert!(previous.contains("fixture 1"));
        assert!(!previous.contains("fixture 0"));
    }
}
