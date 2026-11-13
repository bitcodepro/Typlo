use super::{invalid, read_reply, received_folder};
use crate::{
    config,
    windows::{
        clipio::{Clip, Kind, MAX_CAPTION_BYTES},
        history::{History, Limits, Received},
    },
};
use std::{
    collections::HashSet,
    fs::{self, File, OpenOptions},
    io::{self, Read, Write},
    os::windows::fs::{MetadataExt, OpenOptionsExt},
    path::{Path, PathBuf},
};

pub(super) const VERSION: &[u8] = b"TYPC0001";
pub(super) const RETRY_VERSION: &[u8; 8] = b"TYPC0002";
pub(super) const PING: &[u8; 8] = b"TYPCPING";
const FILES: u8 = 3;
const FILES_WITH_CAPTION: u8 = 4;
const MAX_FILES: usize = 2048;
const MAX_PATH: usize = 4096;
const MAX_FILE_BYTES: u64 = 512 * 1024 * 1024;
const REPARSE_POINT: u32 = 0x400;
const OPEN_REPARSE_POINT: u32 = 0x0020_0000;

pub(super) enum Payload {
    Clip(Clip),
    Files {
        entries: Vec<FileEntry>,
        caption: String,
    },
}

pub(super) struct FileEntry {
    path: PathBuf,
    relative: String,
    length: Option<u64>,
}

impl Payload {
    pub fn prepare(profile: &Path, id: i64) -> io::Result<Self> {
        let clip = History::open(profile)?.load(id)?.ok_or_else(|| {
            io::Error::new(
                io::ErrorKind::NotFound,
                "This history entry no longer exists",
            )
        })?;
        if clip.kind != Kind::Files {
            return Ok(Self::Clip(clip));
        }
        let mut entries = Vec::new();
        let mut total = 0;
        for original in clip.text().lines() {
            let path = Path::new(original);
            let name = path
                .file_name()
                .and_then(|name| name.to_str())
                .ok_or_else(|| invalid("A selected file does not have a transferable name"))?;
            if !path.is_absolute() {
                return Err(invalid("A selected file does not have an absolute path"));
            }
            if entries
                .iter()
                .any(|entry: &FileEntry| entry.relative.eq_ignore_ascii_case(name))
            {
                return Err(invalid("Two selected files or folders have the same name"));
            }
            collect(path, name.to_owned(), &mut entries, &mut total)?;
        }
        if entries.is_empty() {
            return Err(invalid("This history entry contains no files"));
        }
        Ok(Self::Files {
            entries,
            caption: clip.editable_text(),
        })
    }

    pub fn send_body(
        &self,
        stream: &mut (impl Read + Write),
        mut progress: impl FnMut(u64, u64) -> io::Result<()>,
    ) -> io::Result<()> {
        match self {
            Self::Clip(clip) => {
                let bytes = clip.encode();
                let total = bytes.len() as u64;
                progress(0, total)?;
                stream.write_all(&[clip.kind as u8])?;
                stream.write_all(&(bytes.len() as u64).to_be_bytes())?;
                stream.flush()?;
                read_reply(stream)?;
                let mut done = 0;
                for chunk in bytes.chunks(64 * 1024) {
                    stream.write_all(chunk)?;
                    done += chunk.len() as u64;
                    progress(done, total)?;
                }
            }
            Self::Files { entries, caption } => {
                let total = entries.iter().filter_map(|entry| entry.length).sum();
                let mut done = 0;
                progress(0, total)?;
                stream.write_all(&[if caption.is_empty() {
                    FILES
                } else {
                    FILES_WITH_CAPTION
                }])?;
                stream.write_all(&(entries.len() as u64).to_be_bytes())?;
                stream.flush()?;
                read_reply(stream)?;
                if !caption.is_empty() {
                    stream.write_all(&(caption.len() as u64).to_be_bytes())?;
                    stream.write_all(caption.as_bytes())?;
                }
                for entry in entries {
                    progress(done, total)?;
                    stream.write_all(&[u8::from(entry.length.is_some())])?;
                    stream.write_all(&(entry.relative.len() as u16).to_be_bytes())?;
                    stream.write_all(entry.relative.as_bytes())?;
                    if let Some(length) = entry.length {
                        let file = OpenOptions::new()
                            .read(true)
                            .custom_flags(OPEN_REPARSE_POINT)
                            .open(&entry.path)?;
                        let info = file.metadata()?;
                        if info.file_attributes() & REPARSE_POINT != 0
                            || !info.is_file()
                            || info.len() != length
                        {
                            return Err(invalid("A selected file changed before it could be sent"));
                        }
                        stream.write_all(&length.to_be_bytes())?;
                        let mut file = file;
                        let mut buffer = [0; 64 * 1024];
                        let end = done + length;
                        while done < end {
                            let count = (end - done).min(buffer.len() as u64) as usize;
                            file.read_exact(&mut buffer[..count])?;
                            stream.write_all(&buffer[..count])?;
                            done += count as u64;
                            progress(done, total)?;
                        }
                    }
                }
            }
        }
        stream.flush()
    }
}

fn collect(
    path: &Path,
    relative: String,
    entries: &mut Vec<FileEntry>,
    total: &mut u64,
) -> io::Result<()> {
    validate_path(&relative)?;
    if entries.len() >= MAX_FILES {
        return Err(too_large());
    }
    let metadata = fs::symlink_metadata(path)?;
    if metadata.file_attributes() & REPARSE_POINT != 0 || !(metadata.is_file() || metadata.is_dir())
    {
        return Err(invalid("Linked files and folders cannot be sent"));
    }
    let length = metadata.is_file().then_some(metadata.len());
    add_length(total, length.unwrap_or_default())?;
    entries.push(FileEntry {
        path: path.to_owned(),
        relative: relative.clone(),
        length,
    });
    if metadata.is_dir() {
        for child in fs::read_dir(path)? {
            let child = child?;
            let name = child.file_name();
            let name = name
                .to_str()
                .ok_or_else(|| invalid("A selected file does not have a transferable name"))?;
            collect(&child.path(), format!("{relative}/{name}"), entries, total)?;
        }
    }
    Ok(())
}

pub(super) fn receive(
    profile: &Path,
    stream: &mut (impl Read + Write),
    peer: &[u8; 32],
    still_paired: impl Fn() -> io::Result<()>,
) -> io::Result<bool> {
    let mut version = [0; 8];
    stream.read_exact(&mut version)?;
    if version == *PING {
        return still_paired().map(|()| true);
    }
    let mut transfer = [0u8; 16];
    let receipt = if version == *RETRY_VERSION {
        stream.read_exact(&mut transfer)?;
        still_paired()?;
        if History::open(profile)?.has_receipt(peer, &transfer)? {
            stream.write_all(&[4])?;
            stream.flush()?;
            return Ok(false);
        }
        ready(stream)?;
        Some((peer, &transfer))
    } else if version == VERSION {
        None
    } else {
        return Err(invalid("Incompatible Typlo clipboard protocol"));
    };
    let kind = read_byte(stream)?;
    let size = read_size(stream)?;
    let limits = history_limits(profile)?;
    let (clip, directory) = match kind {
        FILES | FILES_WITH_CAPTION => {
            if size == 0 || size > MAX_FILES as u64 {
                return Err(too_large());
            }
            ready(stream)?;
            let caption = if kind == FILES_WITH_CAPTION {
                let length = read_size(stream)?;
                if length == 0 || length > (MAX_CAPTION_BYTES * 2) as u64 {
                    return Err(too_large());
                }
                let mut data = vec![0; length as usize];
                stream.read_exact(&mut data)?;
                Some(String::from_utf8(data).map_err(io::Error::other)?)
            } else {
                None
            };
            let (mut clip, directory, bytes) = receive_files(profile, stream, size as usize)?;
            if let Some(caption) = caption {
                clip.set_editable_text(&caption)?;
            }
            (clip, Some((directory, bytes)))
        }
        1 | 2 => {
            let limit = if kind == Kind::Image as u8 {
                limits.max_image
            } else {
                limits.max_text
            };
            if size == 0 || size > (limit as u64).min(limits.max_total) + 36 {
                return Err(too_large());
            }
            ready(stream)?;
            let mut data = vec![0; size as usize];
            stream.read_exact(&mut data)?;
            let clip = Clip::decode(&data)?;
            if clip.kind as u8 != kind || (clip.kind == Kind::Text && clip.text().is_empty()) {
                return Err(invalid("Invalid clipboard data"));
            }
            (clip, None)
        }
        _ => return Err(invalid("Unsupported clipboard format")),
    };
    let limits = history_limits(profile)?;
    let (clip, preview) = clip
        .normalize_image(limits.max_image)?
        .ok_or_else(|| invalid("Invalid clipboard image"))?;
    still_paired()?;
    let files = directory
        .as_ref()
        .map(|(dir, bytes)| {
            let folder = dir
                .path()
                .file_name()
                .and_then(|name| name.to_str())
                .ok_or_else(|| invalid("Invalid transfer directory"))?;
            Ok::<_, io::Error>((folder, *bytes))
        })
        .transpose()?;
    let saved = History::open(profile)?.store_received(
        &clip,
        limits,
        preview.as_ref(),
        Received { receipt, files },
    )?;
    if saved && let Some((directory, _)) = directory {
        drop(directory.keep());
    }
    Ok(true)
}

fn ready(stream: &mut impl Write) -> io::Result<()> {
    stream.write_all(&[0])?;
    stream.flush()
}

fn history_limits(profile: &Path) -> io::Result<Limits> {
    let settings = config::load(profile)?;
    if !settings.history_enabled {
        return Err(io::Error::new(
            io::ErrorKind::PermissionDenied,
            "Clipboard history is disabled",
        ));
    }
    Ok(Limits {
        max_items: settings.history_items,
        max_total: u64::from(settings.history_total_mib) * 1024 * 1024,
        max_image: settings.history_image_mib as usize * 1024 * 1024,
        max_text: settings.history_text_kib as usize * 1024,
    })
}

fn receive_files(
    profile: &Path,
    stream: &mut impl Read,
    count: usize,
) -> io::Result<(Clip, tempfile::TempDir, u64)> {
    let folder = received_folder(profile);
    fs::create_dir_all(&folder)?;
    let directory = tempfile::Builder::new()
        .prefix("Transfer-")
        .rand_bytes(6)
        .tempdir_in(folder)?;
    let mut roots = Vec::new();
    let mut directories = HashSet::new();
    let mut total = 0;
    for _ in 0..count {
        let kind = read_byte(stream)?;
        let mut length = [0; 2];
        stream.read_exact(&mut length)?;
        let length = u16::from_be_bytes(length) as usize;
        if length == 0 || length > MAX_PATH {
            return Err(invalid("Invalid file name in transfer"));
        }
        let mut name = vec![0; length];
        stream.read_exact(&mut name)?;
        let name = String::from_utf8(name).map_err(io::Error::other)?;
        validate_path(&name)?;
        if let Some((parent, _)) = name.rsplit_once('/')
            && !directories.contains(parent)
        {
            return Err(invalid("Missing parent directory in transfer"));
        }
        let path = directory.path().join(&name);
        if !name.contains('/') {
            roots.push(
                path.to_str()
                    .ok_or_else(|| invalid("The received files location cannot be used"))?
                    .to_owned(),
            );
        }
        match kind {
            0 => {
                fs::create_dir(&path)?;
                directories.insert(name);
            }
            1 => {
                let length = read_size(stream)?;
                add_length(&mut total, length)?;
                let mut file = File::create_new(path)?;
                copy_file(&mut *stream, &mut file, length)?;
                file.sync_all()?;
            }
            _ => return Err(invalid("Invalid file type in transfer")),
        }
    }
    let clip = Clip::from_legacy(FILES, roots.join("\n").into_bytes(), None)?;
    Ok((clip, directory, total))
}

fn validate_path(name: &str) -> io::Result<()> {
    let invalid_name = || invalid("Invalid Windows file name in transfer");
    if name.len() > MAX_PATH || name.split('/').count() > 32 {
        return Err(invalid_name());
    }
    for part in name.split('/') {
        let stem = part
            .split('.')
            .next()
            .unwrap_or_default()
            .trim_end()
            .to_ascii_uppercase();
        let port = stem
            .strip_prefix("COM")
            .or_else(|| stem.strip_prefix("LPT"));
        if part.is_empty()
            || part.ends_with(['.', ' '])
            || part.encode_utf16().count() > 255
            || part.chars().any(|c| {
                c.is_control() || matches!(c, '\\' | ':' | '<' | '>' | '"' | '|' | '?' | '*')
            })
            || matches!(
                stem.as_str(),
                "CON" | "PRN" | "AUX" | "NUL" | "CONIN$" | "CONOUT$"
            )
            || port.is_some_and(|digit| {
                matches!(
                    digit,
                    "0" | "1" | "2" | "3" | "4" | "5" | "6" | "7" | "8" | "9" | "¹" | "²" | "³"
                )
            })
        {
            return Err(invalid_name());
        }
    }
    Ok(())
}

fn too_large() -> io::Error {
    io::Error::new(
        io::ErrorKind::FileTooLarge,
        "Transfer exceeds the file or clipboard limits",
    )
}

fn add_length(total: &mut u64, length: u64) -> io::Result<()> {
    *total = total
        .checked_add(length)
        .filter(|total| *total <= MAX_FILE_BYTES)
        .ok_or_else(too_large)?;
    Ok(())
}

fn read_byte(stream: &mut impl Read) -> io::Result<u8> {
    let mut byte = [0];
    stream.read_exact(&mut byte)?;
    Ok(byte[0])
}

fn read_size(stream: &mut impl Read) -> io::Result<u64> {
    let mut size = [0; 8];
    stream.read_exact(&mut size)?;
    Ok(u64::from_be_bytes(size))
}

fn copy_file(source: impl Read, output: &mut impl Write, length: u64) -> io::Result<()> {
    if io::copy(&mut source.take(length), output)? != length {
        return Err(io::Error::new(
            io::ErrorKind::UnexpectedEof,
            "File transfer was interrupted",
        ));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    struct Input<'a>(&'a [u8]);

    impl Read for Input<'_> {
        fn read(&mut self, buffer: &mut [u8]) -> io::Result<usize> {
            self.0.read(buffer)
        }
    }

    impl Write for Input<'_> {
        fn write(&mut self, buffer: &[u8]) -> io::Result<usize> {
            Ok(buffer.len())
        }

        fn flush(&mut self) -> io::Result<()> {
            Ok(())
        }
    }

    #[test]
    fn refuses_windows_device_names_and_path_escape() {
        for name in [
            "../bad",
            "folder/..",
            "/absolute",
            "folder\\bad",
            "x:stream",
            "CON.txt",
            "lpt²",
            "trailing. ",
            "a//b",
        ] {
            assert!(validate_path(name).is_err(), "{name}");
        }
        assert!(validate_path("Снимки/фото 1.png").is_ok());
    }

    #[test]
    fn partial_file_transfer_leaves_no_history_or_files() {
        let profile = tempfile::tempdir().unwrap();
        let mut data = VERSION.to_vec();
        data.push(FILES);
        data.extend_from_slice(&1u64.to_be_bytes());
        data.extend_from_slice(b"\x01\x00\x01x");
        data.extend_from_slice(&7u64.to_be_bytes());
        data.extend_from_slice(b"bad");
        assert!(receive(profile.path(), &mut Input(&data), &[0; 32], || Ok(())).is_err());
        assert_eq!(
            fs::read_dir(received_folder(profile.path()))
                .unwrap()
                .count(),
            0
        );
        assert!(
            History::open(profile.path())
                .unwrap()
                .list(0)
                .unwrap()
                .is_empty()
        );
    }
}
