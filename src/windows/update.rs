use super::{HOST_CLASS, wide};
pub use crate::update_package::Update;
use crate::update_package::{self, MAX_PACKAGE, download, version as parse_version};
use std::{
    fs,
    io::{self, Write},
    os::windows::process::CommandExt,
    path::{Path, PathBuf},
    process::{Child, Command, Stdio},
    thread,
    time::{Duration, Instant},
};
use windows_sys::Win32::{
    Foundation::{CloseHandle, ERROR_ALREADY_EXISTS, GetLastError, HANDLE, WAIT_OBJECT_0},
    System::Threading::{
        CREATE_NO_WINDOW, CreateMutexW, OpenProcess, PROCESS_SYNCHRONIZE, WaitForSingleObject,
    },
    UI::WindowsAndMessaging::{FindWindowW, GetWindowThreadProcessId, PostMessageW, WM_CLOSE},
};

pub(crate) const ASSET: &str = "typlo-win-x64.zip";
const RESIDENT_EXE: &str = "typlo-rust.exe";
const MARKER: &str = "rust-current.txt";
pub(super) const SHUTDOWN_FOR_UPDATE: usize = 0x5459;
#[cfg(test)]
const REQUIRED: [&str; 9] = [
    "typlo-rust.exe",
    "typlo-settings.exe",
    "resources/language-detection/words.sqlite3",
    "resources/spelling/en_US.aff",
    "resources/spelling/en_US.dic",
    "resources/spelling/ru_RU.aff",
    "resources/spelling/ru_RU.dic",
    "resources/spelling/uk_UA.aff",
    "resources/spelling/uk_UA.dic",
];

pub fn check() -> io::Result<Option<Update>> {
    let root = installation_root()?;
    let installed = current(&root)?.unwrap_or_else(|| env!("CARGO_PKG_VERSION").to_owned());
    update_package::check(&installed, ASSET)
}

pub(super) fn auto_install_in_helper() -> io::Result<()> {
    let output = Command::new(std::env::current_exe()?)
        .creation_flags(CREATE_NO_WINDOW)
        .arg("--auto-update")
        .output()?;
    if !output.status.success() {
        return Err(io::Error::other(
            String::from_utf8_lossy(&output.stderr).trim().to_owned(),
        ));
    }
    Ok(())
}

pub fn auto_install() -> io::Result<()> {
    let _lock = InstallLock::acquire()?;
    let Some(update) = check()? else {
        crate::runtime_log::write(
            "INFO",
            format_args!("Startup update check found no newer release"),
        );
        return Ok(());
    };
    install(&update, |_| {})
}

pub fn install_selected(version: &str, hash: &str) -> io::Result<()> {
    install_selected_with_status(version, hash, None)
}

pub fn install_selected_with_status(
    version: &str,
    hash: &str,
    status: Option<&Path>,
) -> io::Result<()> {
    let _lock = InstallLock::acquire()?;
    let update = check()?
        .filter(|update| update.version == version && update.sha256 == hash)
        .ok_or_else(|| {
            io::Error::other("The selected update is no longer available. Check again.")
        })?;
    install(&update, |phase| {
        if let Some(path) = status {
            let _ = fs::write(path, phase);
        }
    })
}

#[cfg(feature = "gui")]
pub fn spawn_installer(update: &Update, status: &Path) -> io::Result<()> {
    let executable = std::env::current_exe()?.with_file_name(RESIDENT_EXE);
    let output = Command::new(executable)
        .creation_flags(CREATE_NO_WINDOW)
        .arg("--install-update")
        .arg(&update.version)
        .arg(&update.sha256)
        .arg(status)
        .output()?;
    if !output.status.success() {
        let detail = String::from_utf8_lossy(&output.stderr);
        return Err(io::Error::other(format!(
            "{} {}",
            output.status,
            detail.trim()
        )));
    }
    Ok(())
}

fn install(update: &Update, status: impl Fn(&str)) -> io::Result<()> {
    let root = installation_root()?;
    let previous = current(&root)?;
    let base = root.join(RESIDENT_EXE);
    let old_executable = previous
        .as_deref()
        .map(|version| installed_executable(&root, version))
        .unwrap_or(base);
    let version =
        parse_version(&update.version).ok_or_else(|| io::Error::other("Invalid update version"))?;
    if previous
        .as_deref()
        .and_then(parse_version)
        .is_some_and(|old| old >= version)
    {
        return Ok(());
    }
    let workspace = tempfile::tempdir_in(&root)?;
    let archive = workspace.path().join("update.zip");
    crate::runtime_log::write(
        "INFO",
        format_args!("Downloading update v{}", update.version),
    );
    status("download");
    download(&update.url, &archive, MAX_PACKAGE, 120)?;
    status("verify");
    crate::runtime_log::write("INFO", format_args!("Verifying update v{}", update.version));
    let new_executable = stage(&root, &archive, &update.version, &update.sha256)?;
    let probe = workspace.path().join("probe-ready.txt");
    let isolated = workspace.path().join("isolated-profile");
    let mut child = hidden(&new_executable)
        .arg("--background-probe")
        .arg(isolated)
        .arg(&probe)
        .spawn()?;
    let probe_result = ready(&mut child, &probe, Duration::from_secs(12));
    let _ = child.kill();
    let _ = child.wait();
    probe_result?;
    status("restart");
    crate::runtime_log::write(
        "INFO",
        format_args!("Restarting for update v{}", update.version),
    );
    let stopped = stop_resident(&old_executable)?;
    activate(
        &root,
        &update.version,
        previous.as_deref(),
        || {
            let marker = workspace.path().join("resident-ready.txt");
            let mut child = hidden(&new_executable)
                .arg("--updated-resident")
                .arg(&marker)
                .spawn()?;
            if let Err(error) = ready(&mut child, &marker, Duration::from_secs(12)) {
                let _ = child.kill();
                let _ = child.wait();
                return Err(error);
            }
            Ok(())
        },
        || {
            if stopped {
                hidden(&old_executable).arg("--restart-previous").spawn()?;
            }
            Ok(())
        },
    )?;
    crate::runtime_log::write(
        "INFO",
        format_args!("Update v{} is running", update.version),
    );
    Ok(())
}

fn activate(
    root: &Path,
    version: &str,
    previous: Option<&str>,
    launch: impl FnOnce() -> io::Result<()>,
    restart: impl FnOnce() -> io::Result<()>,
) -> io::Result<()> {
    let result = set_current(root, Some(version)).and_then(|()| launch());
    if let Err(error) = result {
        let rollback = if current(root).is_ok_and(|active| active.as_deref() == previous) {
            Ok(())
        } else {
            set_current(root, previous)
        };
        let restarted = restart();
        return Err(io::Error::other(match (rollback, restarted) {
            (Ok(()), Ok(())) => format!("Update failed ({error}). Previous version restored."),
            (marker, resident) => format!(
                "Update failed ({error}). Restore marker: {marker:?}. Restart previous: {resident:?}."
            ),
        }));
    }
    Ok(())
}

struct InstallLock(HANDLE);
impl InstallLock {
    fn acquire() -> io::Result<Self> {
        let name = wide(r"Local\Typlo.Rust.Update");
        let handle = unsafe { CreateMutexW(std::ptr::null(), 0, name.as_ptr()) };
        if handle.is_null() {
            return Err(io::Error::last_os_error());
        }
        let error = unsafe { GetLastError() };
        let lock = Self(handle);
        if error == ERROR_ALREADY_EXISTS {
            return Err(io::Error::new(
                io::ErrorKind::AlreadyExists,
                "A Typlo Rust update is already running",
            ));
        }
        Ok(lock)
    }
}
impl Drop for InstallLock {
    fn drop(&mut self) {
        unsafe {
            CloseHandle(self.0);
        }
    }
}

pub fn launch_installed() -> io::Result<bool> {
    let root = installation_root()?;
    let Some(version) = current(&root)? else {
        return Ok(false);
    };
    let executable = installed_executable(&root, &version);
    if !executable.is_file()
        || executable.canonicalize()? == std::env::current_exe()?.canonicalize()?
    {
        return Ok(false);
    }
    hidden(&executable).spawn()?;
    Ok(true)
}

pub(crate) fn installation_root() -> io::Result<PathBuf> {
    let exe = std::env::current_exe()?;
    installation_root_for(&exe)
}

pub(crate) fn installation_root_for(exe: &Path) -> io::Result<PathBuf> {
    let directory = exe
        .parent()
        .ok_or_else(|| io::Error::other("Missing installation directory"))?;
    if let Some(parent) = directory.parent()
        && parent.file_name().is_some_and(|name| name == "versions")
        && directory
            .file_name()
            .and_then(|name| name.to_str())
            .and_then(parse_version)
            .is_some()
        && let Some(root) = parent.parent()
    {
        return Ok(root.to_owned());
    }
    Ok(directory.to_owned())
}

#[cfg(feature = "gui")]
pub fn temporary_workspace() -> io::Result<tempfile::TempDir> {
    tempfile::tempdir_in(installation_root()?)
}

fn stage(root: &Path, archive: &Path, version: &str, digest: &str) -> io::Result<PathBuf> {
    update_package::stage(
        root,
        archive,
        version,
        digest,
        [RESIDENT_EXE, "typlo-settings.exe"],
    )
}

pub fn verify_package(archive: &Path, version: &str, digest: &str) -> io::Result<()> {
    let workspace = tempfile::tempdir()?;
    stage(workspace.path(), archive, version, digest).map(|_| ())
}

fn current(root: &Path) -> io::Result<Option<String>> {
    match fs::read_to_string(root.join(MARKER)) {
        Ok(value) if parse_version(value.trim()).is_some() => Ok(Some(value.trim().to_owned())),
        Ok(_) => Err(invalid("Invalid installed version marker")),
        Err(error) if error.kind() == io::ErrorKind::NotFound => Ok(None),
        Err(error) => Err(error),
    }
}

fn set_current(root: &Path, version: Option<&str>) -> io::Result<()> {
    let marker = root.join(MARKER);
    if let Some(version) = version {
        let mut temp = tempfile::NamedTempFile::new_in(root)?;
        temp.write_all(version.as_bytes())?;
        temp.as_file().sync_all()?;
        temp.persist(marker).map_err(|error| error.error)?;
    } else if marker.try_exists()? {
        fs::remove_file(marker)?;
    }
    Ok(())
}

fn installed_executable(root: &Path, version: &str) -> PathBuf {
    root.join("versions").join(version).join(RESIDENT_EXE)
}

fn invalid(message: &str) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, message)
}

fn hidden(program: &Path) -> Command {
    let mut command = Command::new(program);
    command
        .creation_flags(CREATE_NO_WINDOW)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null());
    command
}

fn ready(child: &mut Child, report: &Path, timeout: Duration) -> io::Result<()> {
    let deadline = Instant::now() + timeout;
    loop {
        if report.is_file() {
            return Ok(());
        }
        if let Some(exit) = child.try_wait()? {
            return Err(io::Error::other(format!("Updated process exited ({exit})")));
        }
        if Instant::now() >= deadline {
            return Err(io::Error::other("Updated process did not become ready"));
        }
        thread::sleep(Duration::from_millis(40));
    }
}

fn stop_resident(executable: &Path) -> io::Result<bool> {
    let window = unsafe { FindWindowW(wide(HOST_CLASS).as_ptr(), std::ptr::null()) };
    if window.is_null() {
        return Ok(false);
    }
    let mut pid = 0;
    if unsafe { GetWindowThreadProcessId(window, &mut pid) } == 0 {
        return Err(io::Error::last_os_error());
    }
    let actual = super::process_name(pid)
        .ok_or_else(|| io::Error::other("Cannot identify the running application"))?;
    if Path::new(&actual).canonicalize()? != executable.canonicalize()? {
        return Err(io::Error::other(
            "Another Typlo Rust installation is running",
        ));
    }
    let process = unsafe { OpenProcess(PROCESS_SYNCHRONIZE, 0, pid) };
    if process.is_null() {
        return Err(io::Error::last_os_error());
    }
    let result = if unsafe { PostMessageW(window, WM_CLOSE, SHUTDOWN_FOR_UPDATE, 0) } == 0 {
        Err(io::Error::last_os_error())
    } else if unsafe { WaitForSingleObject(process, 10_000) } != WAIT_OBJECT_0 {
        Err(io::Error::other("Previous Typlo Rust process did not exit"))
    } else {
        Ok(true)
    };
    unsafe {
        CloseHandle(process);
    }
    result
}

#[cfg(test)]
mod tests {
    use super::*;
    use sha2::{Digest, Sha256};
    use std::fs::File;
    use zip::{ZipWriter, write::SimpleFileOptions};

    #[test]
    fn updated_executables_resolve_the_same_portable_profile_root() {
        let root = Path::new(r"C:\Apps\Typlo");
        for exe in ["typlo-rust.exe", "typlo-settings.exe"] {
            assert_eq!(installation_root_for(&root.join(exe)).unwrap(), root);
            assert_eq!(
                installation_root_for(&root.join("versions/0.1.3").join(exe)).unwrap(),
                root
            );
        }
    }

    #[test]
    fn stages_verified_package_without_changing_active_version() {
        let root = tempfile::tempdir().unwrap();
        let file = root.path().join("test.zip");
        let mut archive = ZipWriter::new(File::create(&file).unwrap());
        for name in REQUIRED {
            archive
                .start_file(name, SimpleFileOptions::default())
                .unwrap();
            archive.write_all(name.as_bytes()).unwrap();
        }
        archive.finish().unwrap();
        set_current(root.path(), Some("0.1.0")).unwrap();
        assert!(stage(root.path(), &file, "0.2.0", &"0".repeat(64)).is_err());
        assert!(!installed_executable(root.path(), "0.2.0").exists());
        let hash = format!("{:x}", Sha256::digest(fs::read(&file).unwrap()));
        let new = stage(root.path(), &file, "0.2.0", &hash).unwrap();
        assert_eq!(fs::read_to_string(&new).unwrap(), RESIDENT_EXE);
        assert_eq!(current(root.path()).unwrap().as_deref(), Some("0.1.0"));
        set_current(root.path(), Some("0.2.0")).unwrap();
        assert_eq!(current(root.path()).unwrap().as_deref(), Some("0.2.0"));
    }

    #[test]
    fn failed_start_rolls_back_marker_and_restarts_previous_version() {
        let root = tempfile::tempdir().unwrap();
        set_current(root.path(), Some("0.1.0")).unwrap();
        let restarted = std::cell::Cell::new(false);
        let result = activate(
            root.path(),
            "0.2.0",
            Some("0.1.0"),
            || {
                assert_eq!(current(root.path()).unwrap().as_deref(), Some("0.2.0"));
                Err(io::Error::other("synthetic startup failure"))
            },
            || {
                assert_eq!(current(root.path()).unwrap().as_deref(), Some("0.1.0"));
                restarted.set(true);
                Ok(())
            },
        );
        assert!(
            result
                .unwrap_err()
                .to_string()
                .contains("synthetic startup failure")
        );
        assert!(restarted.get());
    }

    #[test]
    fn successful_restart_keeps_the_new_version_active() {
        let root = tempfile::tempdir().unwrap();
        set_current(root.path(), Some("0.1.0")).unwrap();
        activate(
            root.path(),
            "0.2.0",
            Some("0.1.0"),
            || {
                assert_eq!(current(root.path()).unwrap().as_deref(), Some("0.2.0"));
                Ok(())
            },
            || panic!("A successful restart must not restore the previous version"),
        )
        .unwrap();
        assert_eq!(current(root.path()).unwrap().as_deref(), Some("0.2.0"));
    }
}
