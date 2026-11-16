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
    #[cfg(windows)]
    crash::install(&profile.join("logs").join("typlo.log"), component);
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
pub(crate) struct ResidentSession(());

#[cfg(windows)]
static RESIDENT_MARKER: std::sync::Mutex<Option<PathBuf>> = std::sync::Mutex::new(None);

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
        *RESIDENT_MARKER
            .lock()
            .unwrap_or_else(|error| error.into_inner()) = Some(marker);
        Self(())
    }
}

/// Records a clean stop once. Windows ends the process right after
/// `WM_ENDSESSION`, so a sign-out or shutdown never reaches `Drop`.
#[cfg(windows)]
pub(crate) fn finish_resident_session() {
    let marker = RESIDENT_MARKER
        .lock()
        .unwrap_or_else(|error| error.into_inner())
        .take();
    if let Some(marker) = marker {
        write("INFO", format_args!("Resident stopped"));
        let _ = fs::remove_file(marker);
    }
}

#[cfg(windows)]
impl Drop for ResidentSession {
    fn drop(&mut self) {
        finish_resident_session();
    }
}

/// Leaves one line in the log when Windows ends the process for a native fault
/// (access violation, stack overflow). Panics are covered by the panic hook.
#[cfg(windows)]
mod crash {
    use std::{
        ffi::c_void,
        fmt::{self, Write},
        os::windows::ffi::OsStrExt,
        path::Path,
        sync::OnceLock,
        time::{SystemTime, UNIX_EPOCH},
    };
    use windows_sys::Win32::{
        Foundation::{CloseHandle, INVALID_HANDLE_VALUE},
        Storage::FileSystem::{
            CreateFileW, FILE_APPEND_DATA, FILE_ATTRIBUTE_NORMAL, FILE_SHARE_READ,
            FILE_SHARE_WRITE, OPEN_ALWAYS, WriteFile,
        },
        System::{
            Diagnostics::Debug::{EXCEPTION_POINTERS, SetUnhandledExceptionFilter},
            LibraryLoader::GetModuleHandleW,
        },
    };

    static TARGET: OnceLock<(Vec<u16>, &'static str)> = OnceLock::new();

    pub(super) fn install(log: &Path, component: &'static str) {
        let path = log.as_os_str().encode_wide().chain([0]).collect();
        if TARGET.set((path, component)).is_ok() {
            unsafe {
                SetUnhandledExceptionFilter(Some(filter));
            }
        }
    }

    /// A fixed buffer: the fault may have happened with the heap lock held.
    struct Line {
        bytes: [u8; 200],
        length: usize,
    }

    impl Write for Line {
        fn write_str(&mut self, text: &str) -> fmt::Result {
            let free = &mut self.bytes[self.length..];
            let count = text.len().min(free.len());
            free[..count].copy_from_slice(&text.as_bytes()[..count]);
            self.length += count;
            Ok(())
        }
    }

    fn line(component: &str, timestamp: u128, pid: u32, code: u32, offset: usize) -> Line {
        let mut line = Line {
            bytes: [0; 200],
            length: 0,
        };
        let _ = writeln!(
            line,
            "{timestamp} CRASH {component} v{} pid={pid} code={code:#010x} offset={offset:#x}",
            env!("CARGO_PKG_VERSION")
        );
        line
    }

    unsafe extern "system" fn filter(info: *const EXCEPTION_POINTERS) -> i32 {
        const EXCEPTION_CONTINUE_SEARCH: i32 = 0;
        let Some((path, component)) = TARGET.get() else {
            return EXCEPTION_CONTINUE_SEARCH;
        };
        let record = unsafe { info.as_ref().and_then(|info| info.ExceptionRecord.as_ref()) };
        let (code, address) = record.map_or((0, 0), |record| {
            (
                record.ExceptionCode as u32,
                record.ExceptionAddress as usize,
            )
        });
        // The offset inside the executable stays meaningful across launches.
        let base = unsafe { GetModuleHandleW(std::ptr::null()) } as usize;
        let timestamp = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map_or(0, |elapsed| elapsed.as_millis());
        let line = line(
            component,
            timestamp,
            std::process::id(),
            code,
            address.wrapping_sub(base),
        );
        unsafe {
            let file = CreateFileW(
                path.as_ptr(),
                FILE_APPEND_DATA,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                std::ptr::null(),
                OPEN_ALWAYS,
                FILE_ATTRIBUTE_NORMAL,
                std::ptr::null_mut::<c_void>(),
            );
            if file != INVALID_HANDLE_VALUE {
                let mut written = 0;
                WriteFile(
                    file,
                    line.bytes.as_ptr(),
                    line.length as u32,
                    &mut written,
                    std::ptr::null_mut(),
                );
                CloseHandle(file);
            }
        }
        EXCEPTION_CONTINUE_SEARCH
    }

    #[cfg(test)]
    #[test]
    fn crash_line_matches_the_log_format_and_never_overflows() {
        let line = line("resident", 1_794_000_000_000, 4242, 0xc000_0005, 0x1a2b3c);
        let text = std::str::from_utf8(&line.bytes[..line.length]).unwrap();
        assert_eq!(
            text,
            format!(
                "1794000000000 CRASH resident v{} pid=4242 code=0xc0000005 offset=0x1a2b3c\n",
                env!("CARGO_PKG_VERSION")
            )
        );
        let long = "x".repeat(400);
        assert_eq!(line_length(&long), 200);
    }

    #[cfg(test)]
    fn line_length(component: &str) -> usize {
        line(component, 0, 0, 0, 0).length
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

    #[cfg(windows)]
    #[test]
    fn session_end_clears_the_running_marker_before_the_process_is_ended() {
        let dir = tempfile::tempdir().unwrap();
        fs::create_dir_all(dir.path().join("logs")).unwrap();
        let marker = dir.path().join("logs/resident-running");
        let session = ResidentSession::start(dir.path());
        assert!(marker.is_file());
        // The path Windows takes on shutdown: no destructor runs afterwards.
        finish_resident_session();
        assert!(!marker.exists());
        fs::write(&marker, "another resident").unwrap();
        drop(session);
        assert!(marker.is_file(), "a finished session must not stop twice");
    }
}
