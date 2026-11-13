use super::{invalid, received_folder};
use crate::windows::history::History;
use std::{
    fs, io,
    os::windows::fs::MetadataExt,
    path::{Component, Path, PathBuf},
    process::Command,
};

const REPARSE_POINT: u32 = 0x400;

fn transfer_name(name: &str) -> bool {
    name.strip_prefix("Transfer-").is_some_and(|suffix| {
        suffix.len() == 6 && suffix.bytes().all(|byte| byte.is_ascii_alphanumeric())
    })
}

fn location(profile: &Path, store: &History, id: i64) -> io::Result<PathBuf> {
    let root = received_folder(profile);
    let (recorded, legacy) = store.received_location(id)?;
    if recorded.is_none() && !legacy {
        return Err(invalid("This item is outside Typlo's received files"));
    }
    let clip = store
        .load(id)?
        .ok_or_else(|| invalid("File entry no longer exists"))?;
    let mut folder = recorded;
    let mut count = 0;
    for path in clip.text().lines() {
        let relative = Path::new(path)
            .strip_prefix(&root)
            .map_err(|_| invalid("File is outside Typlo's received files"))?;
        let mut parts = relative.components();
        let (Some(Component::Normal(name)), Some(Component::Normal(_)), None) =
            (parts.next(), parts.next(), parts.next())
        else {
            return Err(invalid("File does not belong to one Typlo transfer"));
        };
        let name = name
            .to_str()
            .filter(|name| transfer_name(name))
            .ok_or_else(|| invalid("Invalid Typlo transfer folder"))?;
        match &folder {
            Some(folder) if folder != name => {
                return Err(invalid("Files span more than one transfer folder"));
            }
            Some(_) => {}
            None => folder = Some(name.to_owned()),
        }
        count += 1;
    }
    let folder = folder
        .filter(|_| count != 0)
        .ok_or_else(|| invalid("The transfer does not contain files"))?;
    Ok(root.join(folder))
}

fn check_directory(path: &Path) -> io::Result<bool> {
    match fs::symlink_metadata(path) {
        Err(error) if error.kind() == io::ErrorKind::NotFound => Ok(false),
        Ok(info) if info.is_dir() && info.file_attributes() & REPARSE_POINT == 0 => Ok(true),
        Ok(_) => Err(invalid(
            "Typlo transfer folder is a link or is not a directory",
        )),
        Err(error) => Err(error),
    }
}

fn exists_safely(path: &Path) -> io::Result<bool> {
    let root = path
        .parent()
        .ok_or_else(|| invalid("Invalid Typlo transfer folder"))?;
    Ok(check_directory(root)? && check_directory(path)?)
}

pub fn show(profile: &Path, id: i64) -> io::Result<()> {
    let path = location(profile, &History::open(profile)?, id)?;
    if !exists_safely(&path)? {
        return Err(io::Error::new(
            io::ErrorKind::NotFound,
            "Received files are no longer on disk",
        ));
    }
    Command::new("explorer.exe").arg(path).spawn()?;
    Ok(())
}

pub fn bytes(profile: &Path, id: i64) -> io::Result<u64> {
    let path = location(profile, &History::open(profile)?, id)?;
    if !exists_safely(&path)? {
        return Ok(0);
    }
    super::folder_bytes(path)
}

pub fn delete(profile: &Path, id: i64) -> io::Result<()> {
    let store = History::open(profile)?;
    let path = location(profile, &store, id)?;
    if exists_safely(&path)? {
        fs::remove_dir_all(&path)?;
    }
    store.remove(id)
}
