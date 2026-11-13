use std::{fs, io, os::windows::fs::MetadataExt, path::Path};

pub(super) fn prepare(profile: &Path) -> io::Result<()> {
    if profile.try_exists()? {
        return Ok(());
    }
    let root = profile
        .parent()
        .ok_or_else(|| io::Error::other("Missing installation folder"))?;
    let lock = fs::OpenOptions::new()
        .create(true)
        .truncate(false)
        .read(true)
        .write(true)
        .open(root.join("profile-migration.lock"))?;
    lock.lock()?;
    if let Some(base) = std::env::var_os("LOCALAPPDATA") {
        migrate(&Path::new(&base).join("Typlo-Rust"), profile)?;
    }
    fs::create_dir_all(profile)
}

fn migrate(source: &Path, destination: &Path) -> io::Result<()> {
    if destination.try_exists()? || !source.try_exists()? {
        return Ok(());
    }
    let metadata = fs::symlink_metadata(source)?;
    if !metadata.is_dir() || metadata.file_attributes() & 0x400 != 0 {
        return Err(io::Error::other(
            "The old Typlo profile is not an ordinary directory",
        ));
    }
    let root = destination
        .parent()
        .ok_or_else(|| io::Error::other("Missing installation folder"))?;
    let staging = tempfile::tempdir_in(root)?;
    for name in ["history.sqlite3", "personal-words.sqlite3"] {
        let path = source.join(name);
        if path.is_file() {
            let database = rusqlite::Connection::open_with_flags(
                path,
                rusqlite::OpenFlags::SQLITE_OPEN_READ_ONLY,
            )
            .map_err(io::Error::other)?;
            database
                .busy_timeout(std::time::Duration::from_secs(2))
                .map_err(io::Error::other)?;
            let target = staging.path().join(name);
            database
                .execute("VACUUM INTO ?1", [target.to_string_lossy().as_ref()])
                .map_err(io::Error::other)?;
        }
    }
    for name in [
        "settings.json",
        "lan-clipboard-identity",
        "lan-clipboard-peer.json",
        "snippet-pins.json",
        "latest-screenshot.png",
        "latest-screenshot.png.dpapi",
        "diagnostics.json",
        "Received files",
        "logs",
        "lan-outbox",
    ] {
        copy(&source.join(name), &staging.path().join(name))?;
    }
    if staging.path().join("history.sqlite3").is_file() {
        super::history::History::open(staging.path())?.rebase_received_files(
            &super::lan_clipboard::received_folder(source),
            &super::lan_clipboard::received_folder(destination),
        )?;
    }
    // Publish the entire profile at once. The original remains available to old builds.
    fs::rename(staging.path(), destination)
}

fn copy(source: &Path, destination: &Path) -> io::Result<()> {
    let metadata = match fs::symlink_metadata(source) {
        Ok(metadata) => metadata,
        Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(()),
        Err(error) => return Err(error),
    };
    if metadata.file_attributes() & 0x400 != 0 {
        return Err(io::Error::other(
            "Typlo cannot migrate a linked profile file",
        ));
    }
    if metadata.is_dir() {
        fs::create_dir(destination)?;
        for item in fs::read_dir(source)? {
            let item = item?;
            if item.file_name() != "typlo.lock" && item.file_name() != "transfer.lock" {
                copy(&item.path(), &destination.join(item.file_name()))?;
            }
        }
    } else {
        fs::copy(source, destination)?;
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{
        config,
        windows::{
            clipio::Clip,
            history::{History, Limits},
            lan_clipboard,
        },
    };

    #[test]
    fn portable_migration_preserves_data_and_rebases_received_file_paths() {
        let fixture = tempfile::tempdir().unwrap();
        let old = fixture.path().join("legacy");
        let data = fixture.path().join("data");
        let transfer = lan_clipboard::received_folder(&old).join("Transfer-Abc123");
        fs::create_dir_all(&transfer).unwrap();
        let old_file = transfer.join("sample.txt");
        fs::write(&old_file, b"plaintext").unwrap();
        let config = config::Config {
            history_items: 73,
            ..Default::default()
        };
        config::save(&old, &config).unwrap();
        let peer = fixture.path().join("peer");
        let code = lan_clipboard::invite(&peer).unwrap();
        lan_clipboard::pair(&old, &code, "").unwrap();
        let identity = lan_clipboard::invite(&old).unwrap();
        let mut clip =
            Clip::from_legacy(3, old_file.to_string_lossy().as_bytes().to_vec(), None).unwrap();
        clip.set_editable_text("Caption\r\n").unwrap();
        let mut history = History::open(&old).unwrap();
        history.store(&clip, Limits::from_config(&config)).unwrap();
        let id = history.list(0).unwrap()[0].id;
        history.pin(id, true).unwrap();

        migrate(&old, &data).unwrap();
        assert_eq!(config::load(&data).unwrap().history_items, 73);
        assert_eq!(lan_clipboard::invite(&data).unwrap(), identity);
        assert_eq!(
            lan_clipboard::paired_address(&data).unwrap(),
            Some(String::new())
        );
        let portable = History::open(&data).unwrap();
        let entry = portable.list(0).unwrap().remove(0);
        assert_eq!(entry.id, id);
        assert!(entry.pinned);
        let migrated = portable.load(id).unwrap().unwrap();
        let new_file = lan_clipboard::received_folder(&data).join("Transfer-Abc123/sample.txt");
        assert_eq!(Path::new(&migrated.text()), new_file);
        assert_eq!(migrated.editable_text(), "Caption\r\n");
        assert_eq!(fs::read(new_file).unwrap(), b"plaintext");
        assert_eq!(fs::read(&old_file).unwrap(), b"plaintext");
        migrate(&old, &data).unwrap();
        assert_eq!(portable.list(0).unwrap().len(), 1);
    }
}
