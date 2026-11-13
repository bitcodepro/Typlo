use crate::config::Config;
use aes_gcm::{Aes256Gcm, KeyInit, Nonce, aead::Aead};
use hmac::{Hmac, Mac};
use rusqlite::{Connection, OptionalExtension, TransactionBehavior, params};
use sha2::Sha256;
use std::{
    fs, io,
    path::{Path, PathBuf},
    time::{Duration, SystemTime, UNIX_EPOCH},
};

pub enum Clip {
    Text { text: String, html: Option<String> },
    Image(Vec<u8>),
    Files(Vec<PathBuf>),
}

pub fn paused(profile: &Path) -> bool {
    fs::read_to_string(profile.join("history.pause"))
        .ok()
        .and_then(|text| text.parse::<u64>().ok())
        .is_some_and(|until| unix_seconds() < until)
}

pub fn pause(profile: &Path, duration: Option<Duration>) -> io::Result<()> {
    let path = profile.join("history.pause");
    match duration {
        Some(duration) => crate::unix_desktop::atomic_write(
            &path,
            unix_seconds()
                .saturating_add(duration.as_secs())
                .to_string()
                .as_bytes(),
        ),
        None => match fs::remove_file(path) {
            Ok(()) => Ok(()),
            Err(error) if error.kind() == io::ErrorKind::NotFound => Ok(()),
            Err(error) => Err(error),
        },
    }
}

pub(crate) fn reset_session_pause(profile: &Path) -> io::Result<()> {
    let value = fs::read_to_string(profile.join("history.pause"));
    if value.is_ok_and(|value| value.parse::<u64>() == Ok(u64::MAX)) {
        pause(profile, None)?;
    }
    Ok(())
}

fn unix_seconds() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs()
}

impl Clip {
    fn encode(&self) -> io::Result<(u8, Vec<u8>, String)> {
        let result = match self {
            Self::Text { text, html } => (
                1,
                serde_json::to_vec(&(text, html)).map_err(io::Error::other)?,
                text.chars().take(400).collect(),
            ),
            Self::Image(bytes) => (2, bytes.clone(), String::new()),
            Self::Files(paths) => (
                3,
                serde_json::to_vec(paths).map_err(io::Error::other)?,
                paths
                    .iter()
                    .flat_map(|path| {
                        path.to_string_lossy()
                            .chars()
                            .collect::<Vec<_>>()
                            .into_iter()
                            .chain(['\n'])
                    })
                    .take(400)
                    .collect(),
            ),
        };
        Ok(result)
    }

    fn decode(kind: u8, bytes: Vec<u8>) -> io::Result<Self> {
        match kind {
            1 => {
                let (text, html) = serde_json::from_slice(&bytes).map_err(io::Error::other)?;
                Ok(Self::Text { text, html })
            }
            2 => Ok(Self::Image(bytes)),
            3 => serde_json::from_slice(&bytes)
                .map(Self::Files)
                .map_err(io::Error::other),
            _ => Err(io::Error::other("Invalid clipboard entry type")),
        }
    }
}

#[derive(Clone)]
pub struct Entry {
    pub id: i64,
    pub kind: u8,
    pub pinned: bool,
    pub preview: String,
}

pub struct History {
    db: Connection,
    key: [u8; 32],
}

impl History {
    pub fn open(profile: &Path) -> io::Result<Self> {
        fs::create_dir_all(profile)?;
        let mut db = Connection::open(profile.join("history.sqlite3")).map_err(io::Error::other)?;
        db.busy_timeout(Duration::from_secs(3))
            .map_err(io::Error::other)?;
        db.execute_batch(
            "PRAGMA journal_mode=WAL; PRAGMA secure_delete=ON; PRAGMA mmap_size=0;
             PRAGMA cache_size=-128; PRAGMA trusted_schema=OFF;
             CREATE TABLE IF NOT EXISTS clips(
                id INTEGER PRIMARY KEY AUTOINCREMENT, kind INTEGER NOT NULL, fingerprint BLOB NOT NULL UNIQUE,
                payload BLOB NOT NULL, preview BLOB NOT NULL, bytes INTEGER NOT NULL, pinned INTEGER NOT NULL DEFAULT 0,
                display_order INTEGER NOT NULL, last_used INTEGER NOT NULL);
             CREATE INDEX IF NOT EXISTS clip_display ON clips(pinned DESC, display_order DESC, id DESC);
             CREATE INDEX IF NOT EXISTS clip_recency ON clips(last_used DESC, id DESC);
             CREATE TABLE IF NOT EXISTS order_clock(id INTEGER PRIMARY KEY CHECK(id=1), value INTEGER NOT NULL);
             INSERT OR IGNORE INTO order_clock VALUES(1,0);"
        ).map_err(io::Error::other)?;
        let lock = db
            .transaction_with_behavior(TransactionBehavior::Immediate)
            .map_err(io::Error::other)?;
        let populated: bool = lock
            .query_row("SELECT EXISTS (SELECT 1 FROM clips)", [], |row| row.get(0))
            .map_err(io::Error::other)?;
        let key = platform_key(profile, !populated)?;
        lock.commit().map_err(io::Error::other)?;
        db.busy_timeout(Duration::from_millis(250))
            .map_err(io::Error::other)?;
        Ok(Self { db, key })
    }

    pub fn store(&mut self, clip: &Clip, settings: &Config) -> io::Result<()> {
        let (kind, payload, preview) = clip.encode()?;
        let size = i64::try_from(payload.len()).map_err(io::Error::other)?;
        let max_total = i64::from(settings.history_total_mib) * 1024 * 1024;
        let item_limit = if kind == 2 {
            i64::from(settings.history_image_mib) * 1024 * 1024
        } else {
            i64::from(settings.history_text_kib) * 1024
        };
        if size == 0 || size > max_total || size > item_limit {
            return Ok(());
        }
        let mut hash = <Hmac<Sha256> as Mac>::new_from_slice(&self.key).expect("32-byte key");
        hash.update(&[kind]);
        match clip {
            Clip::Text { text, .. } => hash.update(text.as_bytes()),
            _ => hash.update(&payload),
        }
        let fingerprint = hash.finalize().into_bytes();
        let payload = self.seal(&payload)?;
        let preview = self.seal(preview.as_bytes())?;
        let tx = self
            .db
            .transaction_with_behavior(TransactionBehavior::Immediate)
            .map_err(io::Error::other)?;
        let order = next_order(&tx)?;
        tx.execute(
            "INSERT INTO clips(kind,fingerprint,payload,preview,bytes,display_order,last_used) VALUES(?1,?2,?3,?4,?5,?6,?6)
             ON CONFLICT(fingerprint) DO UPDATE SET display_order=excluded.display_order,last_used=excluded.last_used,
             payload=excluded.payload,preview=excluded.preview,bytes=excluded.bytes",
            params![kind, fingerprint.as_slice(), payload, preview, size, order],
        ).map_err(io::Error::other)?;
        tx.execute(
            "DELETE FROM clips WHERE pinned=0 AND id IN (
                SELECT id FROM (
                    SELECT id, pinned, ROW_NUMBER() OVER (ORDER BY pinned DESC,display_order DESC,id DESC) AS position,
                    SUM(bytes) OVER (ORDER BY pinned DESC,display_order DESC,id DESC) AS total FROM clips
                ) WHERE pinned=0 AND (position>?1 OR total>?2)
             )",
            params![settings.history_items, max_total],
        ).map_err(io::Error::other)?;
        tx.commit().map_err(io::Error::other)
    }

    #[cfg(feature = "gui")]
    pub(crate) fn background_reader(
        &self,
        profile: &Path,
    ) -> impl FnOnce() -> io::Result<Self> + use<> {
        let path = profile.join("history.sqlite3");
        let key = self.key;
        move || {
            use rusqlite::OpenFlags;
            let db = Connection::open_with_flags(
                path,
                OpenFlags::SQLITE_OPEN_READ_ONLY | OpenFlags::SQLITE_OPEN_NO_MUTEX,
            )
            .map_err(io::Error::other)?;
            db.busy_timeout(Duration::from_millis(250))
                .map_err(io::Error::other)?;
            db.execute_batch(
                "PRAGMA cache_size=-128; PRAGMA mmap_size=0; PRAGMA trusted_schema=OFF;",
            )
            .map_err(io::Error::other)?;
            Ok(Self { db, key })
        }
    }

    pub fn list(&self, query: &str, offset: usize) -> io::Result<Vec<Entry>> {
        let query = query.trim().to_lowercase();
        let mut sql = self.db.prepare_cached(
            "SELECT id,kind,pinned,preview FROM clips ORDER BY pinned DESC,display_order DESC,id DESC"
        ).map_err(io::Error::other)?;
        let mut rows = sql.query([]).map_err(io::Error::other)?;
        let mut output = Vec::new();
        let mut passed = 0;
        while let Some(row) = rows.next().map_err(io::Error::other)? {
            let (id, kind, pinned, preview): (i64, u8, bool, Vec<u8>) = (
                row.get(0).map_err(io::Error::other)?,
                row.get(1).map_err(io::Error::other)?,
                row.get(2).map_err(io::Error::other)?,
                row.get(3).map_err(io::Error::other)?,
            );
            let preview = String::from_utf8(self.unseal(&preview)?).map_err(io::Error::other)?;
            if query.is_empty()
                || preview.to_lowercase().contains(&query)
                || (kind == 2
                    && (crate::locale::text("history.imageSearch", "image picture")
                        .contains(&query)
                        || "image".contains(&query)))
                || (kind != 2
                    && preview.chars().count() == 400
                    && self.load(id)?.is_some_and(|clip| match clip {
                        Clip::Text { text, .. } => text.to_lowercase().contains(&query),
                        Clip::Files(paths) => paths
                            .iter()
                            .any(|path| path.to_string_lossy().to_lowercase().contains(&query)),
                        Clip::Image(_) => false,
                    }))
            {
                if passed >= offset {
                    output.push(Entry {
                        id,
                        kind,
                        pinned,
                        preview,
                    });
                    if output.len() >= 51 {
                        break;
                    }
                }
                passed += 1;
            }
        }
        Ok(output)
    }

    pub fn load(&self, id: i64) -> io::Result<Option<Clip>> {
        let row: Option<(u8, Vec<u8>)> = self
            .db
            .query_row("SELECT kind,payload FROM clips WHERE id=?1", [id], |row| {
                Ok((row.get(0)?, row.get(1)?))
            })
            .optional()
            .map_err(io::Error::other)?;
        row.map(|(kind, bytes)| Clip::decode(kind, self.unseal(&bytes)?))
            .transpose()
    }

    pub fn previous(&self) -> io::Result<Option<i64>> {
        self.db
            .query_row(
                "SELECT id FROM clips ORDER BY last_used DESC,id DESC LIMIT 1 OFFSET 1",
                [],
                |row| row.get(0),
            )
            .optional()
            .map_err(io::Error::other)
    }

    pub fn touch(&mut self, id: i64, move_up: bool) -> io::Result<()> {
        let tx = self
            .db
            .transaction_with_behavior(TransactionBehavior::Immediate)
            .map_err(io::Error::other)?;
        let order = next_order(&tx)?;
        tx.execute(
            "UPDATE clips SET last_used=?1, display_order=CASE WHEN ?2 THEN ?1 ELSE display_order END WHERE id=?3",
            params![order, move_up, id],
        ).map_err(io::Error::other)?;
        tx.commit().map_err(io::Error::other)
    }

    pub fn pin(&self, id: i64, pinned: bool) -> io::Result<()> {
        self.db
            .execute(
                "UPDATE clips SET pinned=?2 WHERE id=?1",
                params![id, pinned],
            )
            .map_err(io::Error::other)?;
        Ok(())
    }

    #[cfg(feature = "gui")]
    pub fn stats_for_profile(profile: &Path) -> io::Result<(usize, u64)> {
        let path = profile.join("history.sqlite3");
        if !path.try_exists()? {
            return Ok((0, 0));
        }
        let db = Connection::open_with_flags(path, rusqlite::OpenFlags::SQLITE_OPEN_READ_ONLY)
            .map_err(io::Error::other)?;
        db.busy_timeout(Duration::from_millis(250))
            .map_err(io::Error::other)?;
        db.query_row(
            "SELECT COUNT(*),COALESCE(SUM(bytes),0) FROM clips",
            [],
            |row| {
                Ok((
                    row.get::<_, i64>(0)? as usize,
                    row.get::<_, i64>(1)?.max(0) as u64,
                ))
            },
        )
        .map_err(io::Error::other)
    }

    #[cfg(feature = "gui")]
    pub fn clear_unpinned(&self) -> io::Result<()> {
        self.db
            .execute("DELETE FROM clips WHERE pinned=0", [])
            .map(|_| ())
            .map_err(io::Error::other)
    }

    pub fn remove(&self, id: i64) -> io::Result<()> {
        self.db
            .execute("DELETE FROM clips WHERE id=?1", [id])
            .map_err(io::Error::other)?;
        Ok(())
    }

    fn seal(&self, bytes: &[u8]) -> io::Result<Vec<u8>> {
        let mut nonce = [0u8; 12];
        getrandom::fill(&mut nonce).map_err(random_error)?;
        let payload = Aes256Gcm::new((&self.key).into())
            .encrypt(Nonce::from_slice(&nonce), bytes)
            .map_err(|_| io::Error::other("Could not encrypt clipboard history"))?;
        let mut output = Vec::with_capacity(nonce.len() + payload.len());
        output.extend_from_slice(&nonce);
        output.extend_from_slice(&payload);
        Ok(output)
    }

    fn unseal(&self, bytes: &[u8]) -> io::Result<Vec<u8>> {
        let (nonce, bytes) = bytes
            .split_at_checked(12)
            .ok_or_else(|| io::Error::other("Invalid clipboard ciphertext"))?;
        Aes256Gcm::new((&self.key).into())
            .decrypt(Nonce::from_slice(nonce), bytes)
            .map_err(|_| io::Error::other("Could not authenticate clipboard history"))
    }
}

fn next_order(db: &Connection) -> io::Result<i64> {
    db.query_row(
        "UPDATE order_clock SET value=value+1 WHERE id=1 RETURNING value",
        [],
        |row| row.get(0),
    )
    .map_err(io::Error::other)
}

fn random_error(error: getrandom::Error) -> io::Error {
    io::Error::other(error.to_string())
}

#[cfg(target_os = "linux")]
fn platform_key(profile: &Path, create: bool) -> io::Result<[u8; 32]> {
    use std::io::{Read, Write};
    use std::os::unix::fs::PermissionsExt;
    let path = profile.join("history.key");
    let open = || -> io::Result<[u8; 32]> {
        let attributes = fs::symlink_metadata(&path)?;
        if !attributes.is_file() || attributes.permissions().mode() & 0o077 != 0 {
            return Err(io::Error::new(
                io::ErrorKind::PermissionDenied,
                "History key requires owner-only permissions",
            ));
        }
        let bytes = fs::File::open(&path)?
            .take(33)
            .bytes()
            .collect::<io::Result<Vec<_>>>()?;
        bytes
            .try_into()
            .map_err(|_| io::Error::other("Invalid clipboard key"))
    };
    match open() {
        Ok(key) => Ok(key),
        Err(error) if error.kind() == io::ErrorKind::NotFound && create => {
            if let Some(key) = secret_service(profile, None)? {
                return Ok(key);
            }
            let mut key = [0u8; 32];
            getrandom::fill(&mut key).map_err(random_error)?;
            if secret_service(profile, Some(&key))?.is_some() {
                return Ok(key);
            }
            let mut file = tempfile::NamedTempFile::new_in(profile)?;
            file.write_all(&key)?;
            file.as_file().sync_all()?;
            match file.persist_noclobber(&path) {
                Ok(_) => Ok(key),
                Err(error) if error.error.kind() == io::ErrorKind::AlreadyExists => open(),
                Err(error) => Err(error.error),
            }
        }
        Err(error) if error.kind() == io::ErrorKind::NotFound => {
            secret_service(profile, None)?.ok_or(error)
        }
        Err(error) => Err(error),
    }
}

#[cfg(target_os = "linux")]
fn secret_service(profile: &Path, create: Option<&[u8; 32]>) -> io::Result<Option<[u8; 32]>> {
    use sha2::Digest;
    use std::{
        io::{Read, Write},
        os::unix::ffi::OsStrExt,
        process::{Command, Stdio},
        time::Instant,
    };
    if std::env::var_os("DBUS_SESSION_BUS_ADDRESS").is_none() {
        return Ok(None);
    }
    let account = format!(
        "{:x}",
        Sha256::digest(fs::canonicalize(profile)?.as_os_str().as_bytes())
    );
    let mut command = Command::new("secret-tool");
    if create.is_some() {
        command
            .args(["store", "--label=Typlo Rust Clipboard History"])
            .stdin(Stdio::piped());
    } else {
        command.arg("lookup").stdin(Stdio::null());
    }
    let mut child = match command
        .args(["application", "typlo-rust", "profile", &account])
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .spawn()
    {
        Ok(child) => child,
        Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(None),
        Err(error) => return Err(error),
    };
    if let Some(key) = create {
        let encoded: String = key.iter().map(|byte| format!("{byte:02x}")).collect();
        if child
            .stdin
            .take()
            .expect("secret-tool stdin")
            .write_all(encoded.as_bytes())
            .is_err()
        {
            let _ = child.kill();
            let _ = child.wait();
            return Ok(None);
        }
    }
    let deadline = Instant::now() + Duration::from_secs(1);
    let success = loop {
        match child.try_wait()? {
            Some(status) => break status.success(),
            None if Instant::now() < deadline => std::thread::sleep(Duration::from_millis(10)),
            None => {
                let _ = child.kill();
                let _ = child.wait();
                break false;
            }
        }
    };
    if !success {
        return Ok(None);
    }
    if let Some(key) = create {
        return Ok(Some(*key));
    }
    let mut encoded = String::new();
    child
        .stdout
        .take()
        .expect("secret-tool stdout")
        .take(80)
        .read_to_string(&mut encoded)?;
    let encoded = encoded.trim();
    if encoded.len() != 64 || !encoded.is_ascii() {
        return Err(io::Error::other("Invalid key in Secret Service"));
    }
    let mut key = [0; 32];
    for (index, byte) in key.iter_mut().enumerate() {
        *byte =
            u8::from_str_radix(&encoded[index * 2..index * 2 + 2], 16).map_err(io::Error::other)?;
    }
    Ok(Some(key))
}

#[cfg(target_os = "macos")]
fn platform_key(profile: &Path, create: bool) -> io::Result<[u8; 32]> {
    use sha2::Digest;
    use std::{ffi::c_void, os::unix::ffi::OsStrExt, ptr};
    type Raw = *mut c_void;
    #[link(name = "Security", kind = "framework")]
    unsafe extern "C" {
        fn SecKeychainFindGenericPassword(
            search: Raw,
            service_len: u32,
            service: *const u8,
            account_len: u32,
            account: *const u8,
            length: *mut u32,
            data: *mut Raw,
            item: *mut Raw,
        ) -> i32;
        fn SecKeychainAddGenericPassword(
            keychain: Raw,
            service_len: u32,
            service: *const u8,
            account_len: u32,
            account: *const u8,
            length: u32,
            data: *const u8,
            item: *mut Raw,
        ) -> i32;
        fn SecKeychainItemFreeContent(attributes: Raw, data: Raw) -> i32;
    }
    const SERVICE: &[u8] = b"Typlo Rust Clipboard History";
    let profile = fs::canonicalize(profile)?;
    let account = format!("{:x}", Sha256::digest(profile.as_os_str().as_bytes()));
    let search = || -> io::Result<Option<[u8; 32]>> {
        let mut data = ptr::null_mut();
        let mut size = 0;
        let result = unsafe {
            SecKeychainFindGenericPassword(
                ptr::null_mut(),
                SERVICE.len() as u32,
                SERVICE.as_ptr(),
                account.len() as u32,
                account.as_ptr(),
                &mut size,
                &mut data,
                ptr::null_mut(),
            )
        };
        if result == -25300 {
            return Ok(None);
        }
        if result != 0 {
            return Err(io::Error::other(format!(
                "Keychain lookup returned {result}"
            )));
        }
        let mut key = [0u8; 32];
        if data.is_null() {
            return Err(io::Error::other("Empty Keychain item"));
        }
        let valid = size == 32;
        if valid {
            key.copy_from_slice(unsafe { std::slice::from_raw_parts(data.cast(), 32) });
        }
        unsafe { SecKeychainItemFreeContent(ptr::null_mut(), data) };
        if !valid {
            return Err(io::Error::other("Invalid Keychain item"));
        }
        Ok(Some(key))
    };
    if let Some(key) = search()? {
        return Ok(key);
    }
    if !create {
        return Err(io::Error::other(
            "Clipboard history key is missing from Keychain",
        ));
    }
    let mut key = [0u8; 32];
    getrandom::fill(&mut key).map_err(random_error)?;
    let result = unsafe {
        SecKeychainAddGenericPassword(
            ptr::null_mut(),
            SERVICE.len() as u32,
            SERVICE.as_ptr(),
            account.len() as u32,
            account.as_ptr(),
            key.len() as u32,
            key.as_ptr(),
            ptr::null_mut(),
        )
    };
    if result != 0 {
        return Err(io::Error::other(format!(
            "Keychain write returned {result}"
        )));
    }
    Ok(key)
}

#[cfg(all(test, target_os = "linux"))]
mod tests {
    use super::*;

    #[test]
    fn reads_encrypted_history_and_preserves_fixed_order() {
        let path = tempfile::tempdir().unwrap();
        let settings = Config::default();
        let mut history = History::open(path.path()).unwrap();
        for value in ["isolated fixture secret", "second"] {
            history
                .store(
                    &Clip::Text {
                        text: value.to_owned(),
                        html: None,
                    },
                    &settings,
                )
                .unwrap();
        }
        let old = history.previous().unwrap().unwrap();
        history.touch(old, false).unwrap();
        assert_eq!(history.list("", 0).unwrap()[0].preview, "second");
        assert_eq!(history.list("secret", 0).unwrap()[0].id, old);
        let raw: Vec<u8> = history
            .db
            .query_row("SELECT payload FROM clips WHERE id=?1", [old], |row| {
                row.get(0)
            })
            .unwrap();
        assert!(!raw.windows(6).any(|window| window == b"secret"));
        let Some(Clip::Text { text, .. }) = history.load(old).unwrap() else {
            panic!("missing text")
        };
        assert_eq!(text, "isolated fixture secret");
    }
}
