use serde::Deserialize;
use sha2::{Digest, Sha256};
use std::{
    collections::HashSet,
    fs::{self, File},
    io::{self, Read},
    path::{Path, PathBuf},
    process::Command,
};

pub const MAX_PACKAGE: u64 = 128 * 1024 * 1024;
const MAX_EXPANDED: u64 = 256 * 1024 * 1024;
const FEED: &str = "https://api.github.com/repos/bitcodepro/Typlo2.0/releases?per_page=20";
const RELEASES: &str = "https://github.com/bitcodepro/Typlo2.0/releases/download/";

#[derive(Clone)]
pub struct Update {
    pub version: String,
    pub(crate) url: String,
    pub(crate) sha256: String,
}

#[derive(Deserialize)]
struct Release {
    tag_name: String,
    draft: bool,
    prerelease: bool,
    assets: Vec<Asset>,
}

#[derive(Deserialize)]
struct Asset {
    name: String,
    browser_download_url: String,
    digest: Option<String>,
    size: u64,
}

pub fn check(installed: &str, asset_name: &str) -> io::Result<Option<Update>> {
    let installed = version(installed).ok_or_else(|| invalid("Invalid installed version"))?;
    let workspace = tempfile::tempdir()?;
    let response = workspace.path().join("releases.json");
    download(FEED, &response, 2 * 1024 * 1024, 20)?;
    let releases: Vec<Release> =
        serde_json::from_reader(File::open(response)?).map_err(io::Error::other)?;
    Ok(releases
        .into_iter()
        .filter(|release| !release.draft && !release.prerelease)
        .filter_map(|release| {
            let tag = release.tag_name.strip_prefix('v')?;
            let newer = version(tag)?;
            if newer <= installed {
                return None;
            }
            let asset = release
                .assets
                .into_iter()
                .find(|asset| asset.name == asset_name)?;
            let expected = format!("{RELEASES}{}/{asset_name}", release.tag_name);
            let digest = asset.digest.as_deref()?.strip_prefix("sha256:")?;
            if asset.browser_download_url != expected
                || !valid_hash(digest)
                || !(1..=MAX_PACKAGE).contains(&asset.size)
            {
                return None;
            }
            Some((
                newer,
                Update {
                    version: tag.to_owned(),
                    url: expected,
                    sha256: digest.to_ascii_lowercase(),
                },
            ))
        })
        .max_by_key(|(version, _)| *version)
        .map(|(_, update)| update))
}

pub fn download(url: &str, path: &Path, max: u64, seconds: u32) -> io::Result<()> {
    #[cfg(windows)]
    let mut command = {
        use std::os::windows::process::CommandExt;
        let windows = std::env::var_os("SystemRoot")
            .ok_or_else(|| io::Error::other("Missing Windows directory"))?;
        let mut command = Command::new(PathBuf::from(windows).join("System32").join("curl.exe"));
        command.creation_flags(windows_sys::Win32::System::Threading::CREATE_NO_WINDOW);
        command
    };
    #[cfg(not(windows))]
    let mut command = Command::new("curl");
    use std::process::Stdio;
    let status = command
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .args([
            "--disable",
            "--proto",
            "=https",
            "--proto-redir",
            "=https",
            "--location",
            "--fail",
            "--silent",
            "--show-error",
            "--user-agent",
            "Typlo2.0",
            "--max-time",
            &seconds.to_string(),
            "--max-filesize",
            &max.to_string(),
            "--output",
        ])
        .arg(path)
        .arg(url)
        .status()?;
    if !status.success() {
        return Err(io::Error::other(format!(
            "Update download failed ({status})"
        )));
    }
    if fs::metadata(path)?.len() > max {
        return Err(invalid("Update download exceeds its size limit"));
    }
    Ok(())
}

const RESOURCES: [&str; 7] = [
    "resources/language-detection/words.sqlite3",
    "resources/spelling/en_US.aff",
    "resources/spelling/en_US.dic",
    "resources/spelling/ru_RU.aff",
    "resources/spelling/ru_RU.dic",
    "resources/spelling/uk_UA.aff",
    "resources/spelling/uk_UA.dic",
];

pub fn stage(
    root: &Path,
    archive: &Path,
    name: &str,
    digest: &str,
    binaries: [&str; 2],
) -> io::Result<PathBuf> {
    if version(name).is_none() || !valid_hash(digest) {
        return Err(invalid("Invalid update identity"));
    }
    let mut file = File::open(archive)?;
    if file.metadata()?.len() > MAX_PACKAGE {
        return Err(invalid("Update archive exceeds its size limit"));
    }
    let mut hash = Sha256::new();
    io::copy(&mut file, &mut hash)?;
    if format!("{:x}", hash.finalize()) != digest {
        return Err(invalid("Update archive SHA-256 does not match"));
    }
    let versions = root.join("versions");
    fs::create_dir_all(&versions)?;
    if fs::symlink_metadata(&versions)?.is_symlink() {
        return Err(invalid("Invalid update directory"));
    }
    let required = || binaries.into_iter().chain(RESOURCES);
    let destination = versions.join(name);
    if destination.exists() {
        if fs::symlink_metadata(&destination)?.is_symlink()
            || fs::read_to_string(destination.join("package.sha256"))?.trim() != digest
            || required().any(|path| {
                !fs::symlink_metadata(destination.join(path)).is_ok_and(|info| info.is_file())
            })
        {
            return Err(invalid(
                "A different package already exists for this version",
            ));
        }
        return Ok(destination.join(binaries[0]));
    }
    let temporary = tempfile::Builder::new()
        .prefix(".typlo-stage-")
        .tempdir_in(&versions)?;
    let mut archive = zip::ZipArchive::new(file).map_err(io::Error::other)?;
    if archive.len() > 128 {
        return Err(invalid("Unexpected update package contents"));
    }
    let mut names = HashSet::new();
    let mut available = MAX_EXPANDED;
    for index in 0..archive.len() {
        let entry = archive.by_index(index).map_err(io::Error::other)?;
        let name = entry.name().to_owned();
        if entry.is_dir()
            && !entry.is_symlink()
            && (matches!(
                name.as_str(),
                "resources/"
                    | "resources/spelling/"
                    | "resources/language-detection/"
                    | "docs/"
                    | "integrations/"
                    | "integrations/gnome/"
            ) || allowed_license_dir(&name))
        {
            continue;
        }
        if !(required().any(|required| required == name) || allowed_extra(&name))
            || entry.is_symlink()
            || !names.insert(name.clone())
            || entry.size() > available
        {
            return Err(invalid("Unsafe or oversized update entry"));
        }
        let output = temporary.path().join(&name);
        if let Some(parent) = output.parent() {
            fs::create_dir_all(parent)?;
        }
        let mut target = File::options().write(true).create_new(true).open(output)?;
        let written = io::copy(&mut entry.take(available + 1), &mut target)?;
        if written > available {
            return Err(invalid("Update exceeds the extraction limit"));
        }
        available -= written;
        #[cfg(unix)]
        if binaries.contains(&name.as_str()) || name == "integrations/gnome/install.sh" {
            use std::os::unix::fs::PermissionsExt;
            target.set_permissions(fs::Permissions::from_mode(0o700))?;
        }
        target.sync_all()?;
    }
    if required().any(|name| !names.contains(name)) {
        return Err(invalid(
            "Update package is missing an executable or dictionary",
        ));
    }
    fs::write(temporary.path().join("package.sha256"), digest)?;
    fs::rename(temporary.path(), &destination)?;
    Ok(destination.join(binaries[0]))
}

fn allowed_extra(name: &str) -> bool {
    let license_file = name.rsplit_once('/').is_some_and(|(directory, file)| {
        license_crate(directory)
            && matches!(
                file,
                "LICENSE"
                    | "LICENSE.txt"
                    | "LICENSE-MIT"
                    | "LICENSE-ISC"
                    | "LICENSE-APACHE"
                    | "LICENSE-BoringSSL"
                    | "LICENSE-other-bits"
            )
    });
    license_file
        || matches!(
            name,
            "README.md"
                | "README-Portable.txt"
                | "vcruntime140.dll"
                | "LICENSE"
                | "docs/parity.md"
                | "docs/comparison.md"
                | "docs/engine.csv"
                | "docs/resident.csv"
                | "resources/language-detection/words.manifest.json"
                | "resources/language-detection/NOTICE.txt"
                | "resources/language-detection/LICENSE-CC-BY-SA-4.0.txt"
                | "resources/spelling/manifest.json"
                | "resources/spelling/NOTICE.txt"
                | "resources/spelling/NOTICE-RUST.txt"
                | "resources/spelling/LICENSE-en_US.txt"
                | "resources/spelling/LICENSE-ru_RU.txt"
                | "resources/spelling/LICENSE-uk_UA.txt"
                | "resources/spelling/LICENSE-Spellbook.txt"
                | "resources/spelling/MPL-1.1.txt"
                | "integrations/gnome/bridge.js"
                | "integrations/gnome/extension.js"
                | "integrations/gnome/install.sh"
                | "integrations/gnome/metadata.json"
        )
}

fn allowed_license_dir(name: &str) -> bool {
    name.strip_suffix('/')
        .is_some_and(|directory| directory == "Third-party-licenses" || license_crate(directory))
}

fn license_crate(directory: &str) -> bool {
    directory
        .strip_prefix("Third-party-licenses/")
        .is_some_and(|name| {
            name.len() <= 100
                && name
                    .as_bytes()
                    .first()
                    .is_some_and(u8::is_ascii_alphanumeric)
                && name
                    .as_bytes()
                    .last()
                    .is_some_and(u8::is_ascii_alphanumeric)
                && name
                    .bytes()
                    .all(|c| c.is_ascii_alphanumeric() || matches!(c, b'.' | b'-' | b'_'))
        })
}

pub fn version(version: &str) -> Option<[u32; 3]> {
    let mut parts = version.split('.');
    let mut output = [0u32; 3];
    for slot in &mut output {
        let part = parts.next()?;
        if part.is_empty()
            || part.len() > 6
            || !part.bytes().all(|byte| byte.is_ascii_digit())
            || (part.starts_with('0') && part.len() > 1)
        {
            return None;
        }
        *slot = part.parse().ok()?;
    }
    parts.next().is_none().then_some(output)
}

pub fn valid_hash(digest: &str) -> bool {
    digest.len() == 64 && digest.bytes().all(|byte| byte.is_ascii_hexdigit())
}

fn invalid(message: &str) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, message)
}
