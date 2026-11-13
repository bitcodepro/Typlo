use super::clipio::{Clip, ImagePreview, Kind};
use aes_gcm::{Aes256Gcm, KeyInit, Nonce, Tag, aead::AeadInPlace};
use hmac::{Hmac, Mac};
use rusqlite::{Connection, OpenFlags, OptionalExtension, TransactionBehavior, params};
use sha2::Sha256;
use std::{
    io,
    path::Path,
    ptr::{null, null_mut},
    time::{Duration, SystemTime, UNIX_EPOCH},
};
use windows_sys::Win32::{
    Foundation::LocalFree,
    Security::Cryptography::{
        BCRYPT_USE_SYSTEM_PREFERRED_RNG, BCryptGenRandom, CRYPT_INTEGER_BLOB,
        CRYPTPROTECT_UI_FORBIDDEN, CryptProtectData, CryptUnprotectData,
    },
};

#[derive(Clone, Copy)]
pub struct Limits {
    pub max_items: u32,
    pub max_total: u64,
    pub max_image: usize,
    pub max_text: usize,
}

impl Limits {
    pub(crate) fn from_config(settings: &crate::config::Config) -> Self {
        Self {
            max_items: settings.history_items,
            max_total: u64::from(settings.history_total_mib) * 1024 * 1024,
            max_image: settings.history_image_mib as usize * 1024 * 1024,
            max_text: settings.history_text_kib as usize * 1024,
        }
    }
}

pub struct History {
    db: Connection,
    key: [u8; 32],
}

const SCHEMA_VERSION: u32 = 1;

#[derive(Clone)]
pub struct Entry {
    pub id: i64,
    pub kind: Kind,
    pub pinned: bool,
    pub preview: String,
    pub bytes: u64,
    pub source: Option<String>,
    pub used_at: Option<i64>,
    pub files: Option<usize>,
    pub formatted: bool,
    pub received_bytes: Option<u64>,
    pub unreadable: bool,
}

impl Entry {
    fn unreadable(id: i64) -> Self {
        Self {
            id,
            kind: Kind::Text,
            pinned: false,
            preview: String::new(),
            bytes: 0,
            source: None,
            used_at: None,
            files: None,
            formatted: false,
            received_bytes: None,
            unreadable: true,
        }
    }
}

macro_rules! entry_query {
    ($suffix:literal) => {
        concat!("SELECT c.id,c.kind,c.pinned,c.preview,c.bytes,d.source,d.used_at,d.files,COALESCE(d.formatted,0),r.bytes,c.fingerprint
             FROM clips c LEFT JOIN clip_details d ON d.clip_id=c.id LEFT JOIN lan_received_files r ON r.clip_id=c.id
             ORDER BY c.pinned DESC,c.display_order DESC,c.id DESC", $suffix)
    };
}

const LIST_ENTRIES: &str = entry_query!(" LIMIT 51 OFFSET ?1");
#[cfg(feature = "gui")]
const SCAN_ENTRIES: &str = entry_query!("");

pub(crate) fn read_entry(row: &rusqlite::Row<'_>, cached: Option<&Entry>) -> io::Result<Entry> {
    let read = || -> rusqlite::Result<_> {
        Ok((
            row.get(0)?,
            row.get::<_, u8>(1)?,
            row.get(2)?,
            row.get_ref(3)?.as_blob()?,
            row.get::<_, i64>(4)?,
            row.get_ref(5)?.as_blob_or_null()?,
            row.get(6)?,
            row.get::<_, Option<i64>>(7)?,
            row.get(8)?,
            row.get::<_, Option<i64>>(9)?,
        ))
    };
    let (id, kind, pinned, preview, bytes, source, used_at, files, formatted, received_bytes) =
        read().map_err(io::Error::other)?;
    let kind = match kind {
        1 => Kind::Text,
        2 => Kind::Image,
        3 => Kind::Files,
        _ => return Err(io::Error::other("Invalid history item type")),
    };
    let (preview, source) = if let Some(cached) = cached {
        (cached.preview.clone(), cached.source.clone())
    } else {
        (
            String::from_utf8(unprotect(preview)?).map_err(io::Error::other)?,
            source
                .and_then(|blob| unprotect(blob).ok())
                .and_then(|blob| String::from_utf8(blob).ok()),
        )
    };
    Ok(Entry {
        id,
        kind,
        pinned,
        preview,
        bytes: bytes.max(0) as u64,
        source,
        used_at,
        files: files.and_then(|count| usize::try_from(count).ok()),
        formatted,
        received_bytes: received_bytes.and_then(|bytes| u64::try_from(bytes).ok()),
        unreadable: false,
    })
}

pub(super) struct Received<'a> {
    pub receipt: Option<(&'a [u8; 32], &'a [u8; 16])>,
    pub files: Option<(&'a str, u64)>,
}

impl History {
    pub fn open(profile: &Path) -> io::Result<Self> {
        std::fs::create_dir_all(profile)?;
        let mut db = Connection::open(profile.join("history.sqlite3")).map_err(io::Error::other)?;
        db.busy_timeout(Duration::from_millis(250))
            .map_err(io::Error::other)?;
        db.execute_batch(
            "PRAGMA secure_delete=ON; PRAGMA mmap_size=0; PRAGMA foreign_keys=ON;
             PRAGMA cache_size=-128; PRAGMA trusted_schema=OFF;",
        )
        .map_err(io::Error::other)?;
        let version: u32 = db
            .pragma_query_value(None, "user_version", |row| row.get(0))
            .map_err(io::Error::other)?;
        if version > SCHEMA_VERSION {
            return Err(io::Error::other(
                "Unsupported clipboard history schema version",
            ));
        }
        // VACUUM INTO preserves user_version but creates a fresh journal.
        let journal: String = db
            .pragma_query_value(None, "journal_mode", |row| row.get(0))
            .map_err(io::Error::other)?;
        if version == SCHEMA_VERSION && journal.eq_ignore_ascii_case("wal") {
            let secret: Vec<u8> = db
                .query_row("SELECT secret FROM metadata WHERE id=1", [], |row| {
                    row.get(0)
                })
                .map_err(io::Error::other)?;
            return Ok(Self {
                db,
                key: decode_key(&secret)?,
            });
        }
        db.pragma_update(None, "journal_mode", "WAL")
            .map_err(io::Error::other)?;
        let tx = db
            .transaction_with_behavior(TransactionBehavior::Immediate)
            .map_err(io::Error::other)?;
        tx.execute_batch(
            "CREATE TABLE IF NOT EXISTS metadata(id INTEGER PRIMARY KEY CHECK(id=1), secret BLOB NOT NULL);
             CREATE TABLE IF NOT EXISTS clips(
                id INTEGER PRIMARY KEY AUTOINCREMENT, kind INTEGER NOT NULL, fingerprint BLOB NOT NULL UNIQUE,
                payload BLOB NOT NULL, preview BLOB NOT NULL, bytes INTEGER NOT NULL, pinned INTEGER NOT NULL DEFAULT 0,
                display_order INTEGER NOT NULL, last_used INTEGER NOT NULL);
             CREATE TABLE IF NOT EXISTS image_previews(
                clip_id INTEGER PRIMARY KEY REFERENCES clips(id) ON DELETE CASCADE,
                png BLOB NOT NULL, width INTEGER NOT NULL, height INTEGER NOT NULL);
             CREATE TABLE IF NOT EXISTS clip_details(
                clip_id INTEGER PRIMARY KEY REFERENCES clips(id) ON DELETE CASCADE,
                source BLOB, used_at INTEGER NOT NULL, files INTEGER, formatted INTEGER NOT NULL);
             CREATE TABLE IF NOT EXISTS lan_receipts(
                peer BLOB NOT NULL, transfer BLOB NOT NULL, PRIMARY KEY(peer, transfer));
             CREATE TABLE IF NOT EXISTS lan_received_files(
                clip_id INTEGER PRIMARY KEY REFERENCES clips(id) ON DELETE CASCADE,
                folder TEXT NOT NULL, bytes INTEGER NOT NULL);
             CREATE INDEX IF NOT EXISTS clip_display ON clips(pinned DESC, display_order DESC, id DESC);
             CREATE INDEX IF NOT EXISTS clip_recency ON clips(last_used DESC, id DESC);
             CREATE TABLE IF NOT EXISTS order_clock(id INTEGER PRIMARY KEY CHECK(id=1), value INTEGER NOT NULL);
             INSERT OR IGNORE INTO order_clock VALUES(1,0);"
        ).map_err(io::Error::other)?;
        let secret: Option<Vec<u8>> = tx
            .query_row("SELECT secret FROM metadata WHERE id=1", [], |row| {
                row.get(0)
            })
            .optional()
            .map_err(io::Error::other)?;
        let key = if let Some(secret) = secret {
            decode_key(&secret)?
        } else {
            let mut key = [0; 32];
            let status = unsafe {
                BCryptGenRandom(
                    null_mut(),
                    key.as_mut_ptr(),
                    key.len() as u32,
                    BCRYPT_USE_SYSTEM_PREFERRED_RNG,
                )
            };
            if status != 0 {
                return Err(io::Error::other(format!(
                    "Unable to generate history key ({status:#x})"
                )));
            }
            tx.execute("INSERT INTO metadata VALUES(1,?1)", [protect(&key)?])
                .map_err(io::Error::other)?;
            key
        };
        tx.pragma_update(None, "user_version", SCHEMA_VERSION)
            .map_err(io::Error::other)?;
        tx.commit().map_err(io::Error::other)?;
        Ok(Self { db, key })
    }

    #[cfg(feature = "gui")]
    pub(crate) fn background_reader(
        &self,
        profile: &Path,
    ) -> impl Fn() -> io::Result<Self> + use<> {
        let path = profile.join("history.sqlite3");
        let key = self.key;
        move || {
            Ok(Self {
                db: Self::read_connection(&path)?,
                key,
            })
        }
    }

    #[cfg(feature = "gui")]
    fn read_connection(path: &Path) -> io::Result<Connection> {
        let db = Connection::open_with_flags(
            path,
            OpenFlags::SQLITE_OPEN_READ_ONLY | OpenFlags::SQLITE_OPEN_NO_MUTEX,
        )
        .map_err(io::Error::other)?;
        db.busy_timeout(Duration::from_millis(250))
            .map_err(io::Error::other)?;
        db.execute_batch("PRAGMA cache_size=-128; PRAGMA mmap_size=0; PRAGMA trusted_schema=OFF;")
            .map_err(io::Error::other)?;
        Ok(db)
    }

    pub fn store(&mut self, clip: &Clip, limits: Limits) -> io::Result<()> {
        self.store_capture(clip, limits, None, None)
    }

    pub(super) fn store_capture(
        &mut self,
        clip: &Clip,
        limits: Limits,
        image: Option<&ImagePreview>,
        source: Option<&str>,
    ) -> io::Result<()> {
        self.store_entry(clip, limits, image, source, None)
            .map(|_| ())
    }

    pub(super) fn store_received(
        &mut self,
        clip: &Clip,
        limits: Limits,
        image: Option<&ImagePreview>,
        received: Received<'_>,
    ) -> io::Result<bool> {
        self.store_entry(clip, limits, image, Some("Typlo LAN"), Some(received))
    }

    pub(super) fn has_receipt(&self, peer: &[u8; 32], transfer: &[u8; 16]) -> io::Result<bool> {
        self.db
            .query_row(
                "SELECT EXISTS(SELECT 1 FROM lan_receipts WHERE peer=?1 AND transfer=?2)",
                params![peer.as_slice(), transfer.as_slice()],
                |row| row.get(0),
            )
            .map_err(io::Error::other)
    }

    fn store_entry(
        &mut self,
        clip: &Clip,
        limits: Limits,
        image: Option<&ImagePreview>,
        source: Option<&str>,
        received: Option<Received<'_>>,
    ) -> io::Result<bool> {
        let must_keep = received.is_some();
        let limit_error = || {
            io::Error::new(
                io::ErrorKind::FileTooLarge,
                "Clipboard history limits would discard this entry",
            )
        };
        let size = clip.size() as u64;
        if size == 0
            || size > limits.max_total
            || (clip.kind == Kind::Image && size > limits.max_image as u64)
            || (clip.kind != Kind::Image && size > limits.max_text as u64)
        {
            return if must_keep {
                Err(limit_error())
            } else {
                Ok(false)
            };
        }
        let fingerprint = fingerprint(&self.key, clip);
        let text = clip.text();
        let files = (clip.kind == Kind::Files).then(|| text.lines().count() as i64);
        let preview = protect(&clip_preview(clip))?;
        let payload = protect(&clip.encode())?;
        let tx = self
            .db
            .transaction_with_behavior(TransactionBehavior::Immediate)
            .map_err(io::Error::other)?;
        if let Some((peer, transfer)) = received.as_ref().and_then(|item| item.receipt)
            && tx
                .execute(
                    "INSERT OR IGNORE INTO lan_receipts(peer, transfer) VALUES(?1, ?2)",
                    params![peer.as_slice(), transfer.as_slice()],
                )
                .map_err(io::Error::other)?
                == 0
        {
            return Ok(false);
        }
        let order = next_order(&tx)?;
        let id: i64 = tx.query_row(
            "INSERT INTO clips(kind,fingerprint,payload,preview,bytes,display_order,last_used) VALUES(?1,?2,?3,?4,?5,?6,?6)
             ON CONFLICT(fingerprint) DO UPDATE SET display_order=excluded.display_order,
             last_used=excluded.last_used, payload=excluded.payload, preview=excluded.preview, bytes=excluded.bytes
             RETURNING id",
            params![clip.kind as u8, fingerprint.as_slice(), payload, preview, i64::try_from(size).map_err(io::Error::other)?, order],
            |row| row.get(0),
        ).map_err(io::Error::other)?;
        let source = source.map(|name| protect(name.as_bytes())).transpose()?;
        tx.execute(
            "INSERT OR REPLACE INTO clip_details(clip_id,source,used_at,files,formatted)
             VALUES(?1,?2,?3,?4,?5)",
            params![id, source, unix_seconds(), files, clip.has_formatting()],
        )
        .map_err(io::Error::other)?;
        if let Some(image) = image {
            let png = protect(&image.png)?;
            tx.execute(
                "INSERT OR REPLACE INTO image_previews(clip_id,png,width,height) VALUES(?1,?2,?3,?4)",
                params![id, png, image.dimensions.0, image.dimensions.1],
            ).map_err(io::Error::other)?;
        }
        if let Some((folder, bytes)) = received.and_then(|item| item.files) {
            tx.execute("INSERT OR REPLACE INTO lan_received_files(clip_id, folder, bytes) VALUES(?1,?2,?3)",
                params![id, folder, i64::try_from(bytes).map_err(io::Error::other)?]).map_err(io::Error::other)?;
        }
        trim(&tx, limits)?;
        if must_keep
            && !tx
                .query_row(
                    "SELECT EXISTS(SELECT 1 FROM clips WHERE id=?1)",
                    [id],
                    |row| row.get::<_, bool>(0),
                )
                .map_err(io::Error::other)?
        {
            return Err(limit_error());
        }
        tx.commit().map_err(io::Error::other)?;
        Ok(true)
    }

    #[cfg(feature = "gui")]
    pub(crate) fn image_preview(
        &self,
        id: i64,
    ) -> io::Result<Option<(image::RgbaImage, (u32, u32))>> {
        type Preview = (Vec<u8>, u32, u32);
        let cached: Option<Preview> = self
            .db
            .query_row(
                "SELECT png,width,height FROM image_previews WHERE clip_id=?1",
                [id],
                |row| Ok((row.get(0)?, row.get(1)?, row.get(2)?)),
            )
            .optional()
            .map_err(io::Error::other)?;
        if let Some((png, width, height)) = cached
            && let Ok(png) = unprotect(&png)
            && let Ok(image) = image::load_from_memory_with_format(&png, image::ImageFormat::Png)
        {
            return Ok(Some((image.into_rgba8(), (width, height))));
        }
        let Some(clip) = self.load(id)? else {
            return Ok(None);
        };
        let image = clip.image()?;
        let dimensions = (image.width(), image.height());
        Ok(Some((
            super::clipio::preview_bitmap(&image).into_rgba8(),
            dimensions,
        )))
    }

    #[cfg(feature = "gui")]
    pub(crate) fn data_version(&self) -> io::Result<i64> {
        self.db
            .query_row("PRAGMA data_version", [], |row| row.get(0))
            .map_err(io::Error::other)
    }

    pub fn list(&self, offset: usize) -> io::Result<Vec<Entry>> {
        let offset = i64::try_from(offset).map_err(io::Error::other)?;
        let mut statement = self
            .db
            .prepare_cached(LIST_ENTRIES)
            .map_err(io::Error::other)?;
        let mut rows = statement.query([offset]).map_err(io::Error::other)?;
        let mut entries = Vec::new();
        while let Some(row) = rows.next().map_err(io::Error::other)? {
            entries.push(match read_entry(row, None) {
                Ok(entry) => entry,
                Err(_) => Entry::unreadable(row.get(0).map_err(io::Error::other)?),
            });
        }
        Ok(entries)
    }

    #[cfg(feature = "gui")]
    pub(crate) fn scan(&self, mut visit: impl FnMut(&rusqlite::Row<'_>) -> bool) -> io::Result<()> {
        let mut statement = self
            .db
            .prepare_cached(SCAN_ENTRIES)
            .map_err(io::Error::other)?;
        let mut rows = statement.query([]).map_err(io::Error::other)?;
        while let Some(row) = rows.next().map_err(io::Error::other)? {
            if !visit(row) {
                break;
            }
        }
        Ok(())
    }

    pub fn search(&self, query: &str, offset: usize) -> io::Result<Vec<Entry>> {
        let query = query.trim().to_lowercase();
        if query.is_empty() {
            return self.list(offset);
        }
        let mut start = 0;
        let mut skipped = 0;
        let mut result = Vec::new();
        loop {
            let page = self.list(start)?;
            let next = page.len() > 50;
            for entry in page.into_iter().take(50).filter(|entry| {
                !entry.unreadable
                    && (entry.preview.to_lowercase().contains(&query)
                        || (entry.kind == Kind::Image && "изображение image".contains(&query)))
            }) {
                if skipped < offset {
                    skipped += 1;
                } else {
                    result.push(entry);
                }
                if result.len() == 51 {
                    return Ok(result);
                }
            }
            if !next {
                return Ok(result);
            }
            start += 50;
        }
    }

    pub fn load(&self, id: i64) -> io::Result<Option<Clip>> {
        let blob: Option<Vec<u8>> = self
            .db
            .query_row("SELECT payload FROM clips WHERE id=?1", [id], |row| {
                row.get(0)
            })
            .optional()
            .map_err(io::Error::other)?;
        blob.map(|blob| Clip::decode(&unprotect(&blob)?))
            .transpose()
    }

    #[cfg(any(test, feature = "gui"))]
    pub fn edit_text(
        &mut self,
        id: i64,
        original: &str,
        text: &str,
        limits: Limits,
    ) -> io::Result<()> {
        let tx = self
            .db
            .transaction_with_behavior(TransactionBehavior::Immediate)
            .map_err(io::Error::other)?;
        let (payload, previous_size): (Vec<u8>, i64) = tx
            .query_row(
                "SELECT payload, bytes FROM clips WHERE id=?1",
                [id],
                |row| Ok((row.get(0)?, row.get(1)?)),
            )
            .optional()
            .map_err(io::Error::other)?
            .ok_or_else(|| {
                io::Error::new(io::ErrorKind::NotFound, "Clipboard entry no longer exists")
            })?;
        let mut clip = Clip::decode(&unprotect(&payload)?)?;
        if clip.editable_text() != original {
            return Err(io::Error::new(
                io::ErrorKind::Interrupted,
                "Clipboard entry changed while editing",
            ));
        }
        if text == original {
            return Ok(());
        }
        clip.set_editable_text(text)?;
        let size = i64::try_from(clip.size()).map_err(io::Error::other)?;
        let item_limit = if clip.kind == Kind::Image {
            limits.max_image
        } else {
            limits.max_text
        };
        let total: i64 = tx
            .query_row("SELECT COALESCE(SUM(bytes), 0) FROM clips", [], |row| {
                row.get(0)
            })
            .map_err(io::Error::other)?;
        if size as u64 > item_limit as u64
            || (size > previous_size && (total - previous_size + size) as u64 > limits.max_total)
        {
            return Err(io::Error::new(
                io::ErrorKind::FileTooLarge,
                "Clipboard history size limit exceeded",
            ));
        }
        let changed = tx.execute(
            "UPDATE OR IGNORE clips SET fingerprint=?2,payload=?3,preview=?4,bytes=?5 WHERE id=?1",
            params![id, fingerprint(&self.key, &clip).as_slice(), protect(&clip.encode())?, protect(&clip_preview(&clip))?, size],
        ).map_err(io::Error::other)?;
        if changed == 0 {
            return Err(io::Error::new(
                io::ErrorKind::AlreadyExists,
                "This clipboard entry already exists",
            ));
        }
        tx.execute(
            "UPDATE clip_details SET formatted=?2 WHERE clip_id=?1",
            params![id, clip.has_formatting()],
        )
        .map_err(io::Error::other)?;
        tx.commit().map_err(io::Error::other)
    }

    #[cfg(feature = "gui")]
    pub(crate) fn count(&self) -> io::Result<usize> {
        self.db
            .query_row("SELECT COUNT(*) FROM clips", [], |row| row.get(0))
            .map_err(io::Error::other)
    }

    #[cfg(feature = "gui")]
    pub fn stats_for_profile(profile: &Path) -> io::Result<(usize, u64)> {
        let path = profile.join("history.sqlite3");
        if !path.try_exists()? {
            return Ok((0, 0));
        }
        Self::query_stats(&Self::read_connection(&path)?)
    }

    #[cfg(feature = "gui")]
    fn query_stats(db: &Connection) -> io::Result<(usize, u64)> {
        db.query_row(
            "SELECT COUNT(*), COALESCE(SUM(bytes),0) FROM clips",
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
    pub fn clear_unpinned(&self, profile: &Path) -> io::Result<()> {
        crate::capture::forget_last(profile)?;
        self.db
            .execute("DELETE FROM clips WHERE pinned=0", [])
            .map(|_| ())
            .map_err(io::Error::other)
    }

    pub fn touch(&mut self, id: i64, move_up: bool) -> io::Result<()> {
        let tx = self
            .db
            .transaction_with_behavior(TransactionBehavior::Immediate)
            .map_err(io::Error::other)?;
        let order = next_order(&tx)?;
        tx.execute("UPDATE clips SET last_used=?1, display_order=CASE WHEN ?2 THEN ?1 ELSE display_order END WHERE id=?3",
            params![order, move_up, id]).map_err(io::Error::other)?;
        tx.execute(
            "UPDATE clip_details SET used_at=?1 WHERE clip_id=?2",
            params![unix_seconds(), id],
        )
        .map_err(io::Error::other)?;
        tx.commit().map_err(io::Error::other)
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

    pub fn pin(&self, id: i64, pinned: bool) -> io::Result<()> {
        self.db
            .execute(
                "UPDATE clips SET pinned=?2 WHERE id=?1",
                params![id, pinned],
            )
            .map_err(io::Error::other)?;
        Ok(())
    }

    pub fn remove(&self, id: i64) -> io::Result<()> {
        self.db
            .execute("DELETE FROM clips WHERE id=?1", [id])
            .map_err(io::Error::other)?;
        Ok(())
    }

    pub(super) fn received_location(&self, id: i64) -> io::Result<(Option<String>, bool)> {
        let (folder, source): (Option<String>, Option<Vec<u8>>) = self.db.query_row(
            "SELECT r.folder,d.source FROM clips c LEFT JOIN lan_received_files r ON r.clip_id=c.id
             LEFT JOIN clip_details d ON d.clip_id=c.id WHERE c.id=?1 AND c.kind=3",
            [id], |row| Ok((row.get(0)?,row.get(1)?))).optional().map_err(io::Error::other)?
            .ok_or_else(|| io::Error::new(io::ErrorKind::NotFound, "File entry no longer exists"))?;
        let legacy = source
            .and_then(|blob| unprotect(&blob).ok())
            .is_some_and(|source| source == b"Typlo LAN");
        Ok((folder, legacy))
    }

    pub(super) fn rebase_received_files(&mut self, from: &Path, to: &Path) -> io::Result<()> {
        let tx = self
            .db
            .transaction_with_behavior(TransactionBehavior::Immediate)
            .map_err(io::Error::other)?;
        let files = tx
            .prepare("SELECT id FROM clips WHERE kind=3")
            .map_err(io::Error::other)?
            .query_map([], |row| row.get::<_, i64>(0))
            .map_err(io::Error::other)?
            .collect::<rusqlite::Result<Vec<_>>>()
            .map_err(io::Error::other)?;
        for id in files {
            let payload: Vec<u8> = tx
                .query_row("SELECT payload FROM clips WHERE id=?1", [id], |row| {
                    row.get(0)
                })
                .map_err(io::Error::other)?;
            let Ok(clip) = unprotect(&payload).and_then(|data| Clip::decode(&data)) else {
                continue;
            };
            let paths = clip.text();
            let mut changed = false;
            let rebased = paths
                .lines()
                .map(|path| {
                    if let Ok(relative) = Path::new(path).strip_prefix(from) {
                        changed = true;
                        to.join(relative).to_string_lossy().into_owned()
                    } else {
                        path.to_owned()
                    }
                })
                .collect::<Vec<_>>();
            if !changed {
                continue;
            }
            let mut replacement = Clip::from_legacy(3, rebased.join("\n").into_bytes(), None)?;
            replacement.set_editable_text(&clip.editable_text())?;
            tx.execute(
                "UPDATE clips SET payload=?2,preview=?3,bytes=?4,fingerprint=?5 WHERE id=?1",
                params![
                    id,
                    protect(&replacement.encode())?,
                    protect(&clip_preview(&replacement))?,
                    i64::try_from(replacement.size()).map_err(io::Error::other)?,
                    fingerprint(&self.key, &replacement).as_slice(),
                ],
            )
            .map_err(io::Error::other)?;
        }
        tx.commit().map_err(io::Error::other)
    }

    pub fn import_legacy(&mut self, directory: &Path) -> io::Result<usize> {
        const LIMIT: i64 = 128 * 1024 * 1024 + 28;
        use std::io::Read;
        let mut secret = Vec::new();
        std::fs::File::open(directory.join("history.key"))?
            .take(16 * 1024 + 1)
            .read_to_end(&mut secret)?;
        if secret.len() > 16 * 1024 {
            return Err(io::Error::other("Invalid Typlo v0.5.14 history key"));
        }
        let master = crypt(&secret, false, Some(b"Typlo.ClipboardHistory.v1"))?;
        let encryption = legacy_cipher(&master)?;
        let legacy = Connection::open_with_flags(
            directory.join("history.db"),
            OpenFlags::SQLITE_OPEN_READ_ONLY,
        )
        .map_err(io::Error::other)?;
        legacy
            .busy_timeout(Duration::from_millis(500))
            .map_err(io::Error::other)?;
        legacy
            .execute_batch("PRAGMA query_only=ON; PRAGMA trusted_schema=OFF; PRAGMA mmap_size=0;")
            .map_err(io::Error::other)?;
        let mut select = legacy
            .prepare(
                "SELECT kind,pinned,
                ROW_NUMBER() OVER (ORDER BY display_used,id),
                ROW_NUMBER() OVER (ORDER BY last_used,id),
                CASE WHEN length(payload) <= ?1 THEN payload END,
                CASE WHEN length(extra) <= ?1 THEN extra END,
                COUNT(*) OVER (), COALESCE(length(extra),0)
             FROM clips ORDER BY display_used,id",
            )
            .map_err(io::Error::other)?;
        let mut rows = select.query([LIMIT]).map_err(io::Error::other)?;
        let tx = self
            .db
            .transaction_with_behavior(TransactionBehavior::Immediate)
            .map_err(io::Error::other)?;
        let first: i64 = tx
            .query_row(
                "SELECT MIN(0, COALESCE(MIN(MIN(display_order,last_used)),0)) FROM clips",
                [],
                |row| row.get(0),
            )
            .map_err(io::Error::other)?;
        let mut count = 0;
        while let Some(row) = rows.next().map_err(io::Error::other)? {
            let kind = row.get::<_, u8>(0).map_err(io::Error::other)?;
            let pinned = row.get::<_, bool>(1).map_err(io::Error::other)?;
            let display = row.get::<_, i64>(2).map_err(io::Error::other)?;
            let recent = row.get::<_, i64>(3).map_err(io::Error::other)?;
            let payload = row.get::<_, Vec<u8>>(4).map_err(io::Error::other)?;
            let extra = row.get::<_, Option<Vec<u8>>>(5).map_err(io::Error::other)?;
            let total = row.get::<_, i64>(6).map_err(io::Error::other)?;
            let extra_size = row.get::<_, i64>(7).map_err(io::Error::other)?;
            if total > 100_000 || extra_size > LIMIT {
                return Err(io::Error::other(
                    "Typlo v0.5.14 history exceeds migration limits",
                ));
            }
            let base = first
                .checked_sub(total)
                .and_then(|first| first.checked_sub(1))
                .ok_or_else(|| io::Error::other("Invalid clipboard history order"))?;
            let payload = legacy_open(&encryption, &payload)?;
            let extra = extra
                .map(|bytes| legacy_open(&encryption, &bytes))
                .transpose()?;
            let clip = Clip::from_legacy(kind, payload, extra.as_deref())?;
            let fingerprint = fingerprint(&self.key, &clip);
            let preview = protect(clip.text().chars().take(400).collect::<String>().as_bytes())?;
            let payload = protect(&clip.encode())?;
            count += tx.execute(
                "INSERT INTO clips(kind,fingerprint,payload,preview,bytes,pinned,display_order,last_used)
                 VALUES(?1,?2,?3,?4,?5,?6,?7,?8) ON CONFLICT(fingerprint) DO NOTHING",
                params![kind, fingerprint.as_slice(), payload, preview, clip.size() as i64, pinned, base+display, base+recent],
            ).map_err(io::Error::other)?;
        }
        tx.commit().map_err(io::Error::other)?;
        Ok(count)
    }
}

fn decode_key(secret: &[u8]) -> io::Result<[u8; 32]> {
    unprotect(secret)?
        .try_into()
        .map_err(|_| io::Error::other("Invalid history key"))
}

fn fingerprint(key: &[u8; 32], clip: &Clip) -> [u8; 32] {
    let mut hash = <Hmac<Sha256> as Mac>::new_from_slice(key).expect("valid HMAC key");
    hash.update(&[clip.kind as u8]);
    if let Some(caption) = clip.caption_bytes() {
        hash.update(&[0x80]);
        hash.update(&(clip.identity().len() as u64).to_le_bytes());
        hash.update(caption);
    }
    hash.update(clip.identity());
    hash.finalize().into_bytes().into()
}

fn clip_preview(clip: &Clip) -> Vec<u8> {
    if clip.kind == Kind::Files && clip.caption_bytes().is_some() {
        let paths: String = clip.text().chars().take(200).collect();
        let caption: String = clip.editable_text().chars().take(400).collect();
        return format!("{paths}\0{caption}").into_bytes();
    }
    clip.searchable_text()
        .chars()
        .take(400)
        .collect::<String>()
        .into_bytes()
}

fn legacy_cipher(master: &[u8]) -> io::Result<Aes256Gcm> {
    if master.len() != 32 {
        return Err(io::Error::other("Invalid Typlo v0.5.14 history key"));
    }
    // HKDF-SHA256, the original format uses an empty salt and "enc" as info.
    let mut extract = <Hmac<Sha256> as Mac>::new_from_slice(&[0; 32]).expect("valid HKDF salt");
    extract.update(master);
    let intermediate = extract.finalize().into_bytes();
    let mut expand = <Hmac<Sha256> as Mac>::new_from_slice(&intermediate).expect("valid HKDF key");
    expand.update(b"enc\x01");
    let key = expand.finalize().into_bytes();
    Ok(Aes256Gcm::new(&key))
}

fn legacy_open(cipher: &Aes256Gcm, blob: &[u8]) -> io::Result<Vec<u8>> {
    let (header, data) = blob
        .split_at_checked(28)
        .ok_or_else(|| io::Error::other("Invalid Typlo v0.5.14 clipboard record"))?;
    let (nonce, tag) = header.split_at(12);
    let mut bytes = data.to_vec();
    cipher
        .decrypt_in_place_detached(
            Nonce::from_slice(nonce),
            b"",
            &mut bytes,
            Tag::from_slice(tag),
        )
        .map_err(|_| {
            io::Error::other("Could not authenticate the Typlo v0.5.14 clipboard record")
        })?;
    Ok(bytes)
}

fn unix_seconds() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs() as i64
}

fn next_order(db: &Connection) -> io::Result<i64> {
    let timestamp = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_err(io::Error::other)?
        .as_millis() as i64;
    db.query_row(
        "UPDATE order_clock SET value=MAX(value+1,?1) WHERE id=1 RETURNING value",
        [timestamp],
        |row| row.get(0),
    )
    .map_err(io::Error::other)
}

fn trim(db: &Connection, limits: Limits) -> io::Result<()> {
    db.execute(
        "DELETE FROM clips WHERE pinned=0 AND id IN (
           SELECT id FROM (
             SELECT id, pinned,
               ROW_NUMBER() OVER (ORDER BY pinned DESC,display_order DESC,id DESC) AS position,
               SUM(bytes) OVER (ORDER BY pinned DESC,display_order DESC,id DESC) AS total
             FROM clips
           ) WHERE pinned=0 AND (position>?1 OR total>?2)
         )",
        params![
            limits.max_items,
            i64::try_from(limits.max_total).map_err(io::Error::other)?
        ],
    )
    .map_err(io::Error::other)?;
    Ok(())
}

pub(crate) fn protect(input: &[u8]) -> io::Result<Vec<u8>> {
    crypt(input, true, None)
}
pub(crate) fn unprotect(input: &[u8]) -> io::Result<Vec<u8>> {
    crypt(input, false, None)
}

fn crypt(input: &[u8], encrypt: bool, entropy: Option<&[u8]>) -> io::Result<Vec<u8>> {
    let size = u32::try_from(input.len()).map_err(io::Error::other)?;
    let data = CRYPT_INTEGER_BLOB {
        cbData: size,
        pbData: input.as_ptr().cast_mut(),
    };
    let mut output = CRYPT_INTEGER_BLOB::default();
    let entropy = entropy.map(|bytes| CRYPT_INTEGER_BLOB {
        cbData: bytes.len() as u32,
        pbData: bytes.as_ptr().cast_mut(),
    });
    let entropy = entropy.as_ref().map_or(null(), |blob| blob as *const _);
    let succeeded = unsafe {
        if encrypt {
            CryptProtectData(
                &data,
                null(),
                entropy,
                null_mut(),
                null(),
                CRYPTPROTECT_UI_FORBIDDEN,
                &mut output,
            )
        } else {
            CryptUnprotectData(
                &data,
                null_mut(),
                entropy,
                null_mut(),
                null(),
                CRYPTPROTECT_UI_FORBIDDEN,
                &mut output,
            )
        }
    };
    if succeeded == 0 {
        return Err(io::Error::last_os_error());
    }
    if output.pbData.is_null() {
        return Err(io::Error::other("Missing DPAPI result"));
    }
    let bytes =
        unsafe { std::slice::from_raw_parts(output.pbData, output.cbData as usize) }.to_vec();
    unsafe {
        LocalFree(output.pbData.cast());
    }
    Ok(bytes)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn reopening_history_reads_during_a_write_and_preserves_legacy_data() {
        let profile = tempfile::tempdir().unwrap();
        let mut writer = History::open(profile.path()).unwrap();
        let limits = Limits::from_config(&crate::config::Config::default());
        writer
            .store(&Clip::from_text("existing entry"), limits)
            .unwrap();
        writer.db.pragma_update(None, "user_version", 0).unwrap();
        let mut writer = History::open(profile.path()).unwrap();
        let transaction = writer
            .db
            .transaction_with_behavior(TransactionBehavior::Immediate)
            .unwrap();
        let reader = History::open(profile.path()).unwrap();
        let id = reader.list(0).unwrap()[0].id;
        assert_eq!(reader.load(id).unwrap().unwrap().text(), "existing entry");
        transaction.rollback().unwrap();
    }

    #[test]
    fn versioned_history_copy_reenables_wal() {
        let source = tempfile::tempdir().unwrap();
        let destination = tempfile::tempdir().unwrap();
        let history = History::open(source.path()).unwrap();
        history
            .db
            .execute(
                "VACUUM INTO ?1",
                [destination
                    .path()
                    .join("history.sqlite3")
                    .to_string_lossy()
                    .as_ref()],
            )
            .unwrap();
        let copied = History::open(destination.path()).unwrap();
        let journal: String = copied
            .db
            .pragma_query_value(None, "journal_mode", |row| row.get(0))
            .unwrap();
        assert_eq!(journal, "wal");
    }

    #[test]
    fn editing_enforces_the_configured_kibibyte_limit() {
        let profile = tempfile::tempdir().unwrap();
        let mut history = History::open(profile.path()).unwrap();
        let limits = Limits::from_config(&crate::config::Config {
            history_text_kib: 1,
            ..Default::default()
        });
        history.store(&Clip::from_text("original"), limits).unwrap();
        let id = history.list(0).unwrap()[0].id;
        let fits = "a".repeat(511);
        history.edit_text(id, "original", &fits, limits).unwrap();
        let error = history
            .edit_text(id, &fits, &"a".repeat(512), limits)
            .unwrap_err();
        assert_eq!(error.kind(), io::ErrorKind::FileTooLarge);
        assert_eq!(history.load(id).unwrap().unwrap().text(), fits);
    }

    #[test]
    fn editing_preserves_trailing_line_endings_in_text_and_captions() {
        let profile = tempfile::tempdir().unwrap();
        let mut history = History::open(profile.path()).unwrap();
        let limits = Limits::from_config(&crate::config::Config::default());
        for ending in ["\n", "\r\n", "\r\n\n"] {
            let original = format!("line{ending}");
            let edited = format!("edited{ending}");
            let mut file = Clip::from_legacy(3, b"C:\\Test\\file.txt".to_vec(), None).unwrap();
            file.set_editable_text(&original).unwrap();
            for clip in [Clip::from_text(&original), file] {
                history.store(&clip, limits).unwrap();
                let id = history.list(0).unwrap()[0].id;
                let loaded = history.load(id).unwrap().unwrap();
                assert_eq!(loaded.editable_text(), original);
                if clip.kind == Kind::Files {
                    assert_eq!(loaded.text(), r"C:\Test\file.txt");
                }
                history.edit_text(id, &original, &edited, limits).unwrap();
                assert_eq!(history.load(id).unwrap().unwrap().editable_text(), edited);
                history.remove(id).unwrap();
            }
        }
    }

    #[test]
    fn editing_an_entry_preserves_its_identity_in_history_and_checks_stale_edits() {
        let profile = tempfile::tempdir().unwrap();
        let mut history = History::open(profile.path()).unwrap();
        let limits = Limits {
            max_items: 10,
            max_total: 64 * 1024,
            max_image: 4096,
            max_text: 4096,
        };
        let file = Clip::from_legacy(3, b"C:\\Test\\file.txt".to_vec(), None).unwrap();
        history.store(&file, limits).unwrap();
        let id = history.list(0).unwrap()[0].id;
        history.pin(id, true).unwrap();
        history
            .edit_text(id, "", "A file caption\n🙂", limits)
            .unwrap();
        assert!(history.edit_text(id, "", "stale", limits).is_err());
        let entry = &history.search("file caption", 0).unwrap()[0];
        assert_eq!(entry.id, id);
        assert!(entry.pinned);
        let edited = history.load(id).unwrap().unwrap();
        assert_eq!(edited.text(), file.text());
        assert_eq!(edited.editable_text(), "A file caption\n🙂");
        history.store(&file, limits).unwrap();
        assert_eq!(history.list(0).unwrap().len(), 2);
        assert!(
            history
                .edit_text(id, "A file caption\n🙂", "", limits)
                .is_err()
        );
        let text = Clip::from_text("old text");
        history.store(&text, limits).unwrap();
        let text_id = history.search("old text", 0).unwrap()[0].id;
        history
            .edit_text(text_id, "old text", "updated text", limits)
            .unwrap();
        assert_eq!(
            history.load(text_id).unwrap().unwrap().text(),
            "updated text"
        );
    }

    #[test]
    #[cfg(feature = "gui")]
    fn image_preview_is_encrypted_persistent_and_deleted_with_its_original() {
        let profile = tempfile::tempdir().unwrap();
        let mut history = History::open(profile.path()).unwrap();
        let image = image::RgbaImage::from_pixel(640, 200, image::Rgba([221, 17, 53, 255]));
        let mut bytes = std::io::Cursor::new(Vec::new());
        image.write_to(&mut bytes, image::ImageFormat::Png).unwrap();
        let original = Clip::from_legacy(2, bytes.into_inner(), None).unwrap();
        let limits = Limits {
            max_items: 5,
            max_total: 1024 * 1024,
            max_image: 65536,
            max_text: 1024,
        };
        let (clip, preview) = original.normalize_image(limits.max_image).unwrap().unwrap();
        history
            .store_capture(&clip, limits, preview.as_ref(), Some("Typlo"))
            .unwrap();
        let entries = history.list(0).unwrap();
        assert_eq!(entries[0].source.as_deref(), Some("Typlo"));
        assert!(entries[0].used_at.is_some());
        let id = entries[0].id;
        drop(history);

        let history = History::open(profile.path()).unwrap();
        let (thumbnail, dimensions) = history.image_preview(id).unwrap().unwrap();
        assert_eq!(dimensions, (640, 200));
        assert_eq!(thumbnail.dimensions(), (320, 100));
        assert_eq!(thumbnail.get_pixel(0, 0), image.get_pixel(0, 0));
        let stored: Vec<u8> = history
            .db
            .query_row(
                "SELECT png FROM image_previews WHERE clip_id=?1",
                [id],
                |row| row.get(0),
            )
            .unwrap();
        assert!(!stored.starts_with(b"\x89PNG\r\n\x1a\n"));
        assert_eq!(
            history
                .load(id)
                .unwrap()
                .unwrap()
                .image()
                .unwrap()
                .into_rgba8(),
            image
        );
        history.remove(id).unwrap();
        assert!(history.image_preview(id).unwrap().is_none());
        let previews: i64 = history
            .db
            .query_row("SELECT count(*) FROM image_previews", [], |row| row.get(0))
            .unwrap();
        assert_eq!(previews, 0);
    }

    #[test]
    fn imports_original_dpapi_and_gcm_records_atomically() {
        let old = tempfile::tempdir().unwrap();
        let profile = tempfile::tempdir().unwrap();
        let master = [19u8; 32];
        let key = crypt(&master, true, Some(b"Typlo.ClipboardHistory.v1")).unwrap();
        std::fs::write(old.path().join("history.key"), key).unwrap();
        let cipher = legacy_cipher(&master).unwrap();
        let encrypt = |data: &[u8], nonce: [u8; 12]| {
            let mut data = data.to_vec();
            let tag = cipher
                .encrypt_in_place_detached(Nonce::from_slice(&nonce), b"", &mut data)
                .unwrap();
            [nonce.as_slice(), tag.as_slice(), &data].concat()
        };
        let db = Connection::open(old.path().join("history.db")).unwrap();
        db.execute_batch("CREATE TABLE clips(id INTEGER PRIMARY KEY,kind INTEGER,pinned INTEGER,display_used INTEGER,last_used INTEGER,payload BLOB,extra BLOB);").unwrap();
        db.execute(
            "INSERT INTO clips VALUES(1,1,1,15,10,?1,?2)",
            params![
                encrypt("старый текст".as_bytes(), [1; 12]),
                encrypt(br#"{"html":"<b>hello</b>","rtf":null}"#, [2; 12])
            ],
        )
        .unwrap();
        db.execute(
            "INSERT INTO clips VALUES(2,3,0,10,15,?1,NULL)",
            [encrypt(b"C:\\tmp\\one.txt\nC:\\tmp\\two.txt", [3; 12])],
        )
        .unwrap();
        db.execute("INSERT INTO clips VALUES(3,1,0,20,20,x'0102',NULL)", [])
            .unwrap();
        let mut history = History::open(profile.path()).unwrap();
        assert!(history.import_legacy(old.path()).is_err());
        assert!(history.list(0).unwrap().is_empty());
        db.execute("DELETE FROM clips WHERE id=3", []).unwrap();
        assert_eq!(history.import_legacy(old.path()).unwrap(), 2);
        assert_eq!(history.import_legacy(old.path()).unwrap(), 0);
        let entries = history.list(0).unwrap();
        assert_eq!(entries[0].preview, "старый текст");
        assert!(entries[0].pinned);
        assert_eq!(entries[1].kind, Kind::Files);
        assert_eq!(history.previous().unwrap(), Some(entries[0].id));
        assert_eq!(
            db.query_row("SELECT COUNT(*) FROM clips", [], |row| row.get::<_, i64>(0))
                .unwrap(),
            2
        );
    }

    #[test]
    #[cfg(feature = "gui")]
    fn stats_read_without_creating_a_history_or_waiting_for_a_writer() {
        let directory = tempfile::tempdir().unwrap();
        let profile = directory.path();
        assert_eq!(History::stats_for_profile(profile).unwrap(), (0, 0));
        assert!(!profile.join("history.sqlite3").exists());

        let mut history = History::open(profile).unwrap();
        let clip = Clip::from_text("one");
        history
            .store(
                &clip,
                Limits {
                    max_items: 10,
                    max_total: 2048,
                    max_image: 512,
                    max_text: 512,
                },
            )
            .unwrap();
        let writer = history
            .db
            .transaction_with_behavior(TransactionBehavior::Immediate)
            .unwrap();
        writer.execute("DELETE FROM clips", []).unwrap();
        assert_eq!(
            History::stats_for_profile(profile).unwrap(),
            (1, clip.size() as u64)
        );
    }

    #[test]
    fn history_preserves_or_changes_display_order_as_configured() {
        let profile = Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("target")
            .join(format!(
                "history-test-{}-{}",
                std::process::id(),
                SystemTime::now()
                    .duration_since(UNIX_EPOCH)
                    .unwrap()
                    .as_nanos(),
            ));
        let mut store = History::open(&profile).unwrap();
        let limits = Limits {
            max_items: 10,
            max_total: 2048,
            max_image: 512,
            max_text: 512,
        };
        store.store(&Clip::from_text("one"), limits).unwrap();
        store.store(&Clip::from_text("two"), limits).unwrap();
        let first = store.previous().unwrap().unwrap();
        store.touch(first, false).unwrap();
        assert_eq!(store.list(0).unwrap()[0].preview, "two");
        assert_eq!(
            store
                .load(store.previous().unwrap().unwrap())
                .unwrap()
                .unwrap()
                .text(),
            "two"
        );
        store.touch(first, true).unwrap();
        assert_eq!(store.list(0).unwrap()[0].preview, "one");
        store.store(&Clip::from_text("one"), limits).unwrap();
        assert_eq!(store.list(0).unwrap().len(), 2);
        store.pin(first, true).unwrap();
        drop(store);
        assert!(History::open(&profile).unwrap().list(0).unwrap()[0].pinned);
    }
}
