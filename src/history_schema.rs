//! Shared layout of the `clips` table used by the Windows and Unix histories.
//!
//! SQLite stores a large BLOB in a chain of overflow pages. Reading any column
//! that comes after such a BLOB walks the whole chain, so `payload` must be the
//! last column. Otherwise listing, searching, trimming and statistics read every
//! stored image and file list in full.

use rusqlite::{Connection, OptionalExtension, TransactionBehavior};
use std::io;

/// Columns of `clips` in storage order, ending with the large payload.
const COLUMNS: &str = "id,kind,fingerprint,preview,bytes,pinned,display_order,last_used,payload";

macro_rules! clips_table {
    ($name:literal) => {
        concat!(
            "CREATE TABLE IF NOT EXISTS ",
            $name,
            "(
                id INTEGER PRIMARY KEY AUTOINCREMENT, kind INTEGER NOT NULL, fingerprint BLOB NOT NULL UNIQUE,
                preview BLOB NOT NULL, bytes INTEGER NOT NULL, pinned INTEGER NOT NULL DEFAULT 0,
                display_order INTEGER NOT NULL, last_used INTEGER NOT NULL, payload BLOB NOT NULL);"
        )
    };
}

/// Creates `clips` and its indexes with the payload stored last.
pub(crate) const CREATE_CLIPS: &str = concat!(
    clips_table!("clips"),
    "
     CREATE INDEX IF NOT EXISTS clip_display ON clips(pinned DESC, display_order DESC, id DESC);
     CREATE INDEX IF NOT EXISTS clip_recency ON clips(last_used DESC, id DESC);"
);

/// True when `clips` does not exist yet or already stores `payload` last.
pub(crate) fn payload_is_last(db: &Connection) -> io::Result<bool> {
    let last: Option<String> = db
        .query_row(
            "SELECT name FROM pragma_table_info('clips') ORDER BY cid DESC LIMIT 1",
            [],
            |row| row.get(0),
        )
        .optional()
        .map_err(io::Error::other)?;
    Ok(last.is_none_or(|name| name == "payload"))
}

/// Rebuilds a `clips` table created by older builds so that `payload` is the
/// last column. Ids, the AUTOINCREMENT counter, indexes and rows that reference
/// `clips` are kept. Must be called outside a transaction.
pub(crate) fn move_payload_last(db: &mut Connection) -> io::Result<()> {
    if payload_is_last(db)? {
        return Ok(());
    }
    let foreign_keys: bool = db
        .pragma_query_value(None, "foreign_keys", |row| row.get(0))
        .map_err(io::Error::other)?;
    // Dropping the old table must not cascade into rows that reference it.
    db.pragma_update(None, "foreign_keys", false)
        .map_err(io::Error::other)?;
    let result = match rebuild(db) {
        // Another process is rebuilding or writing. The old layout still works,
        // only slower, so the rebuild is retried on the next open.
        Err(error) if is_busy(&error) => Ok(()),
        result => result,
    };
    let restored = db
        .pragma_update(None, "foreign_keys", foreign_keys)
        .map_err(io::Error::other);
    result.and(restored)
}

fn is_busy(error: &io::Error) -> bool {
    error
        .get_ref()
        .and_then(|inner| inner.downcast_ref::<rusqlite::Error>())
        .and_then(rusqlite::Error::sqlite_error_code)
        .is_some_and(|code| {
            matches!(
                code,
                rusqlite::ErrorCode::DatabaseBusy | rusqlite::ErrorCode::DatabaseLocked
            )
        })
}

fn rebuild(db: &mut Connection) -> io::Result<()> {
    let tx = db
        .transaction_with_behavior(TransactionBehavior::Immediate)
        .map_err(io::Error::other)?;
    // Another process may have finished the rebuild while this one waited.
    if payload_is_last(&tx)? {
        return Ok(());
    }
    let sequence: Option<i64> = tx
        .query_row(
            "SELECT seq FROM sqlite_sequence WHERE name='clips'",
            [],
            |row| row.get(0),
        )
        .optional()
        .map_err(io::Error::other)?;
    tx.execute_batch(&format!(
        "{create}
         INSERT INTO clips_reordered({COLUMNS}) SELECT {COLUMNS} FROM clips;
         DROP TABLE clips;
         ALTER TABLE clips_reordered RENAME TO clips;
         CREATE INDEX IF NOT EXISTS clip_display ON clips(pinned DESC, display_order DESC, id DESC);
         CREATE INDEX IF NOT EXISTS clip_recency ON clips(last_used DESC, id DESC);",
        create = clips_table!("clips_reordered"),
    ))
    .map_err(io::Error::other)?;
    if let Some(sequence) = sequence {
        tx.execute(
            "UPDATE sqlite_sequence SET seq=MAX(seq, ?1) WHERE name='clips'",
            [sequence],
        )
        .map_err(io::Error::other)?;
    }
    let broken: Option<String> = tx
        .query_row(
            "SELECT \"table\" FROM pragma_foreign_key_check",
            [],
            |row| row.get(0),
        )
        .optional()
        .map_err(io::Error::other)?;
    if let Some(table) = broken {
        return Err(io::Error::other(format!(
            "Clipboard history rebuild left orphaned rows in {table}"
        )));
    }
    tx.commit().map_err(io::Error::other)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rebuild_moves_payload_last_and_keeps_ids_counter_and_children() {
        let mut db = Connection::open_in_memory().unwrap();
        db.execute_batch(
            "PRAGMA foreign_keys=ON;
             CREATE TABLE clips(
                id INTEGER PRIMARY KEY AUTOINCREMENT, kind INTEGER NOT NULL, fingerprint BLOB NOT NULL UNIQUE,
                payload BLOB NOT NULL, preview BLOB NOT NULL, bytes INTEGER NOT NULL, pinned INTEGER NOT NULL DEFAULT 0,
                display_order INTEGER NOT NULL, last_used INTEGER NOT NULL);
             CREATE TABLE clip_details(
                clip_id INTEGER PRIMARY KEY REFERENCES clips(id) ON DELETE CASCADE, used_at INTEGER NOT NULL);
             INSERT INTO clips(id,kind,fingerprint,payload,preview,bytes,pinned,display_order,last_used)
                VALUES(5,1,x'01',zeroblob(200000),x'aa',200000,1,7,7), (9,2,x'02',x'bb',x'cc',1,0,8,8);
             DELETE FROM clips WHERE id=9;
             INSERT INTO clip_details VALUES(5, 123);",
        )
        .unwrap();
        assert!(!payload_is_last(&db).unwrap());

        move_payload_last(&mut db).unwrap();

        assert!(payload_is_last(&db).unwrap());
        let foreign_keys: bool = db
            .pragma_query_value(None, "foreign_keys", |row| row.get(0))
            .unwrap();
        assert!(foreign_keys, "the previous foreign key mode is restored");
        let (id, length, pinned): (i64, i64, bool) = db
            .query_row("SELECT id, length(payload), pinned FROM clips", [], |row| {
                Ok((row.get(0)?, row.get(1)?, row.get(2)?))
            })
            .unwrap();
        assert_eq!((id, length, pinned), (5, 200_000, true));
        let used_at: i64 = db
            .query_row(
                "SELECT used_at FROM clip_details WHERE clip_id=5",
                [],
                |row| row.get(0),
            )
            .unwrap();
        assert_eq!(used_at, 123, "rows that reference clips survive");
        db.execute(
            "INSERT INTO clips(kind,fingerprint,preview,bytes,display_order,last_used,payload)
             VALUES(1,x'03',x'dd',1,9,9,x'ee')",
            [],
        )
        .unwrap();
        assert_eq!(
            db.last_insert_rowid(),
            10,
            "AUTOINCREMENT does not reuse ids"
        );
        let indexes: i64 = db
            .query_row(
                "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name IN ('clip_display','clip_recency')",
                [],
                |row| row.get(0),
            )
            .unwrap();
        assert_eq!(indexes, 2);
        db.execute("DELETE FROM clips WHERE id=5", []).unwrap();
        let details: i64 = db
            .query_row("SELECT COUNT(*) FROM clip_details", [], |row| row.get(0))
            .unwrap();
        assert_eq!(
            details, 0,
            "ON DELETE CASCADE still points at the rebuilt table"
        );
    }

    #[test]
    fn rebuild_is_skipped_while_another_connection_writes() {
        let folder = tempfile::tempdir().unwrap();
        let path = folder.path().join("history.sqlite3");
        let mut db = Connection::open(&path).unwrap();
        db.execute_batch(
            "PRAGMA journal_mode=WAL;
             CREATE TABLE clips(
                id INTEGER PRIMARY KEY AUTOINCREMENT, kind INTEGER NOT NULL, fingerprint BLOB NOT NULL UNIQUE,
                payload BLOB NOT NULL, preview BLOB NOT NULL, bytes INTEGER NOT NULL, pinned INTEGER NOT NULL DEFAULT 0,
                display_order INTEGER NOT NULL, last_used INTEGER NOT NULL);",
        )
        .unwrap();
        let mut writer = Connection::open(&path).unwrap();
        let busy = writer
            .transaction_with_behavior(TransactionBehavior::Immediate)
            .unwrap();

        move_payload_last(&mut db).unwrap();
        assert!(!payload_is_last(&db).unwrap(), "skipped while busy");

        busy.rollback().unwrap();
        move_payload_last(&mut db).unwrap();
        assert!(payload_is_last(&db).unwrap(), "done on the next open");
    }
}
