pub use crate::update_package::Update;
use crate::{
    config, unix_desktop,
    update_package::{self, MAX_PACKAGE, download, version},
};
use std::{
    fs::{self, File},
    io::{self, Read},
    path::{Path, PathBuf},
    process::{Command, Stdio},
    time::{Duration, Instant},
};

const MARKER: &str = "active-version";
const BINARIES: [&str; 2] = ["typlo-rust", "typlo-settings"];

fn asset() -> io::Result<&'static str> {
    match (std::env::consts::OS, std::env::consts::ARCH) {
        ("linux", "x86_64") => Ok("typlo-rust-linux-x64.zip"),
        ("linux", "aarch64") => Ok("typlo-rust-linux-arm64.zip"),
        ("macos", "aarch64") => Ok("typlo-rust-macos-arm64.zip"),
        ("macos", "x86_64") => Ok("typlo-rust-macos-x64.zip"),
        _ => Err(io::Error::new(
            io::ErrorKind::Unsupported,
            "No Typlo package for this platform",
        )),
    }
}

pub fn check() -> io::Result<Option<Update>> {
    let profile = config::profile();
    let root = profile.join("install");
    let installed = current(&root)?.unwrap_or_else(|| env!("CARGO_PKG_VERSION").to_owned());
    update_package::check(&installed, asset()?)
}

pub fn spawn_installer(update: &Update) -> io::Result<()> {
    let profile = config::profile();
    let root = profile.join("install");
    fs::create_dir_all(&root)?;
    let installation = unix_desktop::lock_file(&root.join("update.lock"))?;
    installation.try_lock().map_err(io::Error::other)?;
    let approved = check()?
        .filter(|candidate| {
            candidate.version == update.version && candidate.sha256 == update.sha256
        })
        .ok_or_else(|| io::Error::other("The selected Typlo update is no longer available"))?;
    let work = tempfile::tempdir_in(&root)?;
    let archive = work.path().join("update.zip");
    download(&approved.url, &archive, MAX_PACKAGE, 120)?;
    let candidate = update_package::stage(
        &root,
        &archive,
        &approved.version,
        &approved.sha256,
        BINARIES,
    )?;
    let previous_version = current(&root)?;
    let previous = previous_version.as_deref().map_or(
        std::env::current_exe()?.with_file_name(BINARIES[0]),
        |version| executable(&root, version),
    );
    activate(&root, &profile, &candidate, &approved.version, &previous)
}

fn activate(
    root: &Path,
    profile: &Path,
    candidate: &Path,
    version: &str,
    previous: &Path,
) -> io::Result<()> {
    unix_desktop::request_shutdown(profile)?;
    unix_desktop::wait_for_exit(profile, Duration::from_secs(6))?;
    let ready = spawn_ready(candidate, profile);
    let activated =
        ready.and_then(|()| unix_desktop::atomic_write(&root.join(MARKER), version.as_bytes()));
    if let Err(error) = activated {
        let restored = unix_desktop::request_shutdown(profile)
            .and_then(|()| unix_desktop::wait_for_exit(profile, Duration::from_secs(6)))
            .and_then(|()| spawn_ready(previous, profile));
        return Err(io::Error::other(match restored {
            Ok(()) => format!("Update failed ({error}). Previous version restored."),
            Err(restore) => format!(
                "Update failed ({error}). Restarting the previous version failed ({restore})."
            ),
        }));
    }
    Ok(())
}

pub fn launch_installed(profile: &Path) -> io::Result<bool> {
    let root = profile.join("install");
    let Some(current_version) = current(&root)? else {
        return Ok(false);
    };
    let resident = executable(&root, &current_version);
    if fs::canonicalize(&resident).ok() == fs::canonicalize(std::env::current_exe()?).ok() {
        return Ok(false);
    }
    if !resident.is_file() {
        return Ok(false);
    }
    match spawn_ready(&resident, profile) {
        Ok(()) => Ok(true),
        Err(error) => {
            eprintln!("Typlo update startup: {error}");
            Ok(false)
        }
    }
}

fn current(root: &Path) -> io::Result<Option<String>> {
    match fs::read_to_string(root.join(MARKER)) {
        Ok(value) if version(value.trim()).is_some() => Ok(Some(value.trim().to_owned())),
        Ok(_) => Err(io::Error::other("Invalid Typlo installation marker")),
        Err(error) if error.kind() == io::ErrorKind::NotFound => Ok(None),
        Err(error) => Err(error),
    }
}

fn executable(root: &Path, version: &str) -> PathBuf {
    root.join("versions").join(version).join(BINARIES[0])
}

fn spawn_ready(executable: &Path, profile: &Path) -> io::Result<()> {
    let status = tempfile::tempdir()?;
    let ready = status.path().join("ready");
    let log = status.path().join("stderr");
    let mut child = Command::new(executable)
        .arg("--managed-resident")
        .arg(profile)
        .arg(&ready)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(File::create(&log)?)
        .spawn()?;
    let deadline = Instant::now() + Duration::from_secs(8);
    loop {
        if let Some(status) = child.try_wait()? {
            let mut detail = String::new();
            File::open(&log)?.take(4096).read_to_string(&mut detail)?;
            return Err(io::Error::other(format!(
                "Resident exited with {status}. {}",
                detail.trim()
            )));
        }
        if fs::read(&ready).ok().as_deref() == Some(b"ready") {
            return Ok(());
        }
        if Instant::now() >= deadline {
            let _ = child.kill();
            let _ = child.wait();
            return Err(io::Error::new(
                io::ErrorKind::TimedOut,
                "Resident did not report readiness",
            ));
        }
        std::thread::sleep(Duration::from_millis(20));
    }
}

#[cfg(all(test, target_os = "linux"))]
mod tests {
    use super::*;
    use std::os::unix::fs::PermissionsExt;

    #[test]
    fn failing_managed_resident_leaves_previous_version_selected() {
        let profile = tempfile::tempdir().unwrap();
        let root = profile.path().join("install");
        fs::create_dir(&root).unwrap();
        unix_desktop::atomic_write(&root.join(MARKER), b"1.2.3").unwrap();
        let bad = root.join("bad");
        let previous = root.join("previous");
        fs::write(&bad, "#!/bin/sh\nprintf failed-to-start >&2\nexit 8\n").unwrap();
        fs::write(&previous, "#!/bin/sh\nprintf ready > \"$3\"\n sleep 1\n").unwrap();
        for script in [&bad, &previous] {
            fs::set_permissions(script, fs::Permissions::from_mode(0o700)).unwrap();
        }
        let old = unix_desktop::instance(profile.path()).unwrap();
        let resident = std::thread::spawn(move || {
            let old = old;
            let deadline = Instant::now() + Duration::from_secs(2);
            while !old.signal.requested() {
                assert!(Instant::now() < deadline);
                std::thread::sleep(Duration::from_millis(10));
            }
        });
        let result = activate(&root, profile.path(), &bad, "1.2.4", &previous)
            .unwrap_err()
            .to_string();
        resident.join().unwrap();
        assert!(result.contains("failed-to-start"), "{result}");
        assert!(result.contains("Previous version restored"));
        assert_eq!(current(&root).unwrap().as_deref(), Some("1.2.3"));
    }
}
