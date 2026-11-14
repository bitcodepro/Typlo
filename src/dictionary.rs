use crate::lexicon::{LANGUAGES, Vocabulary, normalize, valid_word};
use rusqlite::{Connection, OptionalExtension, TransactionBehavior, params};
use serde::{Deserialize, Serialize};
use std::{
    cell::RefCell,
    fs::File,
    io::{self, Read},
    path::{Path, PathBuf},
    time::Duration,
};

const WORD_FORMAT: &str = "typlo-personal-dictionary";
const EXCLUSION_FORMAT: &str = "typlo-auto-correction-exclusions";
const LIMIT: usize = 100_000;
const IMPORT_BYTES: u64 = 64 * 1024 * 1024;

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct Entry {
    pub word: String,
    pub language: String,
}

impl Entry {
    pub fn new(word: &str, language: &str) -> io::Result<Self> {
        let word = normalize(word.trim());
        if !valid_word(&word) || (!language.is_empty() && !LANGUAGES.contains(&language)) {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "Invalid word or language",
            ));
        }
        Ok(Self {
            word,
            language: language.into(),
        })
    }
}

pub struct Dictionary {
    path: Option<PathBuf>,
    connection: RefCell<Option<Connection>>,
}

#[derive(Default)]
pub struct Added {
    pub inserted: usize,
    pub existing: Vec<Entry>,
}

#[derive(Serialize, Deserialize, Default)]
#[serde(deny_unknown_fields)]
pub(crate) struct Snapshot {
    pub entries: Vec<Entry>,
    pub corrections: Vec<(String, String)>,
}

impl Dictionary {
    pub fn new(profile: &Path) -> Self {
        Self {
            path: Some(profile.join("personal-words.sqlite3")),
            connection: RefCell::default(),
        }
    }

    #[cfg(test)]
    pub fn empty() -> Self {
        Self {
            path: None,
            connection: RefCell::default(),
        }
    }

    fn access<T: Default>(
        &self,
        create: bool,
        run: impl FnOnce(&mut Connection) -> rusqlite::Result<T>,
    ) -> io::Result<T> {
        let mut slot = self.connection.borrow_mut();
        if slot.is_none() {
            let Some(path) = &self.path else {
                return Ok(T::default());
            };
            if !create && !path.try_exists()? {
                return Ok(T::default());
            }
            if create && let Some(parent) = path.parent() {
                std::fs::create_dir_all(parent)?;
            }
            let flags = rusqlite::OpenFlags::SQLITE_OPEN_READ_WRITE
                | rusqlite::OpenFlags::SQLITE_OPEN_NO_MUTEX
                | if create {
                    rusqlite::OpenFlags::SQLITE_OPEN_CREATE
                } else {
                    rusqlite::OpenFlags::empty()
                };
            let connection = Connection::open_with_flags(path, flags).map_err(io::Error::other)?;
            connection
                .busy_timeout(Duration::from_millis(40))
                .map_err(io::Error::other)?;
            connection.execute_batch(
                "PRAGMA cache_size=-32; PRAGMA mmap_size=0; PRAGMA trusted_schema=OFF;
                 CREATE TABLE IF NOT EXISTS words (word TEXT NOT NULL, language TEXT NOT NULL, PRIMARY KEY(word,language)) WITHOUT ROWID;
                 CREATE INDEX IF NOT EXISTS words_language ON words(language,word);
                 CREATE TABLE IF NOT EXISTS excluded_words (word TEXT NOT NULL PRIMARY KEY) WITHOUT ROWID;
                 CREATE TABLE IF NOT EXISTS spelling_corrections (error TEXT NOT NULL PRIMARY KEY, correction TEXT NOT NULL) WITHOUT ROWID;
                 CREATE TABLE IF NOT EXISTS rust_settings (id INTEGER PRIMARY KEY CHECK(id=1), data BLOB NOT NULL);",
            ).map_err(io::Error::other)?;
            *slot = Some(connection);
        }
        run(slot.as_mut().expect("dictionary initialized")).map_err(io::Error::other)
    }

    pub(crate) fn read_settings(&self) -> io::Result<Option<Vec<u8>>> {
        self.access(false, |db| {
            db.query_row("SELECT data FROM rust_settings WHERE id=1", [], |row| {
                row.get(0)
            })
            .optional()
        })
    }

    pub(crate) fn write_settings(&self, data: &[u8]) -> io::Result<()> {
        self.access(true, |db| {
            update_settings(db, data)?;
            Ok(())
        })
    }

    pub(crate) fn backup_snapshot(&self) -> io::Result<(Option<Vec<u8>>, Snapshot)> {
        self.access(false, |db| {
            let tx = db.transaction()?;
            let settings = tx.query_row("SELECT data FROM rust_settings WHERE id=1", [], |row| row.get(0)).optional()?;
            let entries = tx.prepare("SELECT word,language FROM words UNION ALL SELECT word,'' FROM excluded_words ORDER BY 1,2")?
                .query_map([], |row| Ok(Entry { word: row.get(0)?, language: row.get(1)? }))?
                .collect::<rusqlite::Result<Vec<_>>>()?;
            let corrections = tx.prepare("SELECT error,correction FROM spelling_corrections ORDER BY error")?
                .query_map([], |row| Ok((row.get(0)?, row.get(1)?)))?
                .collect::<rusqlite::Result<Vec<_>>>()?;
            tx.commit()?;
            Ok((settings, Snapshot { entries, corrections }))
        })
    }

    pub(crate) fn restore_snapshot(&self, settings: &[u8], snapshot: &Snapshot) -> io::Result<()> {
        self.access(true, |db| {
            let tx = db.transaction_with_behavior(TransactionBehavior::Immediate)?;
            tx.execute_batch("DELETE FROM words; DELETE FROM excluded_words;")?;
            for entry in &snapshot.entries {
                if insert(&tx, entry)? != 1 {
                    return Err(rusqlite::Error::InvalidQuery);
                }
            }
            replace_corrections(&tx, &snapshot.corrections)?;
            update_settings(&tx, settings)?;
            tx.commit()
        })
    }

    pub(crate) fn restore_settings(
        &self,
        settings: &[u8],
        corrections: Option<&[(String, String)]>,
    ) -> io::Result<()> {
        self.access(true, |db| {
            let tx = db.transaction_with_behavior(TransactionBehavior::Immediate)?;
            if let Some(corrections) = corrections {
                replace_corrections(&tx, corrections)?;
            }
            update_settings(&tx, settings)?;
            tx.commit()
        })
    }

    pub fn contains(&self, word: &str, language: &str) -> io::Result<bool> {
        self.access(false, |connection| {
            connection
                .prepare_cached("SELECT EXISTS(SELECT 1 FROM words WHERE word=?1 AND language=?2)")?
                .query_row(params![word, language], |row| row.get(0))
        })
    }

    pub fn is_excluded(&self, word: &str) -> io::Result<bool> {
        let full = normalize(word);
        let token = full.trim_matches(|c: char| ".,!?;:\"“”„()[]{}«»…".contains(c));
        self.access(false, |connection| {
            connection
                .prepare_cached(
                    "SELECT EXISTS(SELECT 1 FROM excluded_words WHERE word IN (?1,?2))",
                )?
                .query_row(params![full, token], |row| row.get(0))
        })
    }

    pub fn correction(&self, word: &str) -> io::Result<Option<String>> {
        self.access(false, |connection| {
            connection
                .prepare_cached("SELECT correction FROM spelling_corrections WHERE error=?1")?
                .query_row([normalize(word)], |row| row.get(0))
                .optional()
        })
    }

    pub fn corrections(&self) -> io::Result<Vec<(String, String)>> {
        self.access(false, |connection| {
            connection
                .prepare_cached(
                    "SELECT error,correction FROM spelling_corrections ORDER BY error LIMIT 1000",
                )?
                .query_map([], |row| Ok((row.get(0)?, row.get(1)?)))?
                .collect()
        })
    }

    pub fn set_correction(&self, word: &str, correction: &str) -> io::Result<()> {
        let (word, correction) = validate_correction(word, correction)?;
        let saved: bool = self.access(true, |connection| {
            Ok(connection.execute(
                "INSERT INTO spelling_corrections(error,correction)
                 SELECT ?1,?2 WHERE EXISTS(SELECT 1 FROM spelling_corrections WHERE error=?1)
                     OR (SELECT COUNT(*) FROM spelling_corrections) < 1000
                 ON CONFLICT(error) DO UPDATE SET correction=excluded.correction",
                params![word, correction],
            )? == 1)
        })?;
        if !saved {
            return Err(io::Error::other(
                "Maximum of 1000 spelling corrections reached",
            ));
        }
        Ok(())
    }

    pub fn remove_correction(&self, word: &str) -> io::Result<bool> {
        self.access(false, |connection| {
            Ok(connection.execute(
                "DELETE FROM spelling_corrections WHERE error=?1",
                [normalize(word)],
            )? == 1)
        })
    }

    pub fn add(&self, words: &[Entry], corpus: &mut Vocabulary) -> io::Result<Added> {
        self.merge(words, Some(corpus))
    }

    fn merge(&self, words: &[Entry], mut corpus: Option<&mut Vocabulary>) -> io::Result<Added> {
        if words.len() > LIMIT {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "Too many dictionary entries",
            ));
        }
        let mut existing = Vec::new();
        let mut pending = Vec::new();
        for entry in words {
            let entry = Entry::new(&entry.word, &entry.language)?;
            if let Some(corpus) = corpus.as_deref_mut()
                && let Some(index) = LANGUAGES.iter().position(|&code| code == entry.language)
                && corpus.scores(&entry.word)?[index] > 0
            {
                existing.push(entry);
            } else {
                pending.push(entry);
            }
        }
        if pending.is_empty() {
            return Ok(Added {
                inserted: 0,
                existing,
            });
        }
        let inserted = self.access(true, |connection| {
            let tx = connection.transaction()?;
            let mut count = 0;
            for entry in &pending {
                if insert(&tx, entry)? == 1 {
                    count += 1;
                } else {
                    existing.push(entry.clone());
                }
            }
            tx.commit()?;
            Ok(count)
        })?;
        Ok(Added { inserted, existing })
    }

    pub fn remove(&self, entry: &Entry) -> io::Result<bool> {
        let entry = Entry::new(&entry.word, &entry.language)?;
        self.access(false, |connection| Ok(delete(connection, &entry)? == 1))
    }

    #[cfg(feature = "gui")]
    pub(crate) fn add_priority(&self, entry: &Entry, corpus: &mut Vocabulary) -> io::Result<bool> {
        let entry = Entry::new(&entry.word, &entry.language)?;
        let invalid = || {
            io::Error::new(
                io::ErrorKind::InvalidInput,
                "Expected a built-in dictionary entry",
            )
        };
        let index = LANGUAGES
            .iter()
            .position(|&code| code == entry.language)
            .ok_or_else(invalid)?;
        if corpus.scores(&entry.word)?[index] == 0 {
            return Err(invalid());
        }
        self.access(true, |connection| Ok(insert(connection, &entry)? == 1))
    }

    pub fn edit(
        &self,
        original: &Entry,
        updated: &Entry,
        corpus: &mut Vocabulary,
    ) -> io::Result<bool> {
        let original = Entry::new(&original.word, &original.language)?;
        let updated = Entry::new(&updated.word, &updated.language)?;
        if original == updated {
            return Ok(true);
        }
        if original.language.is_empty() != updated.language.is_empty() {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "Cannot move entries between dictionaries",
            ));
        }
        if let Some(index) = LANGUAGES.iter().position(|&code| code == updated.language)
            && corpus.scores(&updated.word)?[index] > 0
        {
            return Ok(false);
        }
        self.access(false, |connection| {
            let tx = connection.transaction()?;
            if delete(&tx, &original)? != 1 || insert(&tx, &updated)? != 1 {
                return Ok(false);
            }
            tx.commit()?;
            Ok(true)
        })
    }

    pub fn page(
        &self,
        prefix: &str,
        language: Option<&str>,
        exclusions: bool,
        offset: usize,
    ) -> io::Result<Vec<Entry>> {
        let offset = i64::try_from(offset).map_err(io::Error::other)?;
        let mut prefix = normalize(prefix.trim())
            .replace('\\', "\\\\")
            .replace('%', "\\%")
            .replace('_', "\\_");
        prefix.push('%');
        self.access(false, |connection| {
            let query = if exclusions {
                "SELECT word, '' FROM excluded_words WHERE word LIKE ?1 ESCAPE '\\' AND ?2 IS NULL ORDER BY word LIMIT 51 OFFSET ?3"
            } else {
                "SELECT word,language FROM words WHERE word LIKE ?1 ESCAPE '\\' AND (?2 IS NULL OR language=?2) ORDER BY word,language LIMIT 51 OFFSET ?3"
            };
            connection.prepare_cached(query)?.query_map(params![prefix, language, offset], |row| {
                Ok(Entry { word: row.get(0)?, language: row.get(1)? })
            })?.collect()
        })
    }

    pub fn import(&self, file: &Path, exclusions: bool) -> io::Result<Added> {
        let file = File::open(file)?;
        if file.metadata()?.len() > IMPORT_BYTES {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "Dictionary exceeds 64 MiB",
            ));
        }
        let envelope: Transfer =
            serde_json::from_reader(io::BufReader::new(file.take(IMPORT_BYTES + 1)))
                .map_err(io::Error::other)?;
        if envelope.version != 1 || envelope.format != transfer_format(exclusions) {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "Unexpected dictionary format",
            ));
        }
        let entries: Vec<_> = envelope
            .words
            .into_iter()
            .map(|record| match record {
                Record::Word(entry) if !exclusions && !entry.language.is_empty() => Ok(entry),
                Record::Exclusion(word) if exclusions => Entry::new(&word, ""),
                _ => Err(io::Error::new(
                    io::ErrorKind::InvalidData,
                    "Invalid dictionary entry",
                )),
            })
            .collect::<io::Result<_>>()?;
        self.merge(&entries, None)
    }

    pub fn export(&self, file: &Path, exclusions: bool) -> io::Result<()> {
        let words: Vec<Record> = self.access(false, |connection| {
            let query = if exclusions {
                "SELECT word,'' FROM excluded_words ORDER BY word"
            } else {
                "SELECT word,language FROM words ORDER BY word,language"
            };
            connection
                .prepare_cached(query)?
                .query_map([], |row| {
                    let word = row.get(0)?;
                    if exclusions {
                        Ok(Record::Exclusion(word))
                    } else {
                        Ok(Record::Word(Entry {
                            word,
                            language: row.get(1)?,
                        }))
                    }
                })?
                .collect()
        })?;
        let envelope = Transfer {
            format: transfer_format(exclusions).into(),
            version: 1,
            words,
        };
        let output = std::fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(file)?;
        serde_json::to_writer_pretty(&output, &envelope).map_err(io::Error::other)?;
        output.sync_all()
    }
}

pub(crate) fn validate_correction<'a>(
    word: &str,
    correction: &'a str,
) -> io::Result<(String, &'a str)> {
    let word = normalize(word.trim());
    let correction = correction.trim();
    let normalized = normalize(correction);
    if !valid_word(&word)
        || !valid_word(&normalized)
        || word == normalized
        || word.encode_utf16().count() > 64
        || correction.encode_utf16().count() > 64
    {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "Invalid spelling correction",
        ));
    }
    Ok((word, correction))
}

fn replace_corrections(db: &Connection, corrections: &[(String, String)]) -> rusqlite::Result<()> {
    db.execute("DELETE FROM spelling_corrections", [])?;
    let mut insert = db.prepare_cached("INSERT INTO spelling_corrections VALUES (?1,?2)")?;
    for (error, correction) in corrections {
        insert.execute(params![error, correction])?;
    }
    Ok(())
}

fn update_settings(db: &Connection, data: &[u8]) -> rusqlite::Result<()> {
    db.execute(
        "INSERT INTO rust_settings VALUES(1,?1) ON CONFLICT(id) DO UPDATE SET data=excluded.data",
        [data],
    )?;
    Ok(())
}

fn insert(connection: &Connection, entry: &Entry) -> rusqlite::Result<usize> {
    if entry.language.is_empty() {
        connection.execute(
            "INSERT OR IGNORE INTO excluded_words VALUES (?1)",
            [&entry.word],
        )
    } else {
        connection.execute(
            "INSERT OR IGNORE INTO words VALUES (?1,?2)",
            params![entry.word, entry.language],
        )
    }
}

fn delete(connection: &Connection, entry: &Entry) -> rusqlite::Result<usize> {
    if entry.language.is_empty() {
        connection.execute("DELETE FROM excluded_words WHERE word=?1", [&entry.word])
    } else {
        connection.execute(
            "DELETE FROM words WHERE word=?1 AND language=?2",
            params![entry.word, entry.language],
        )
    }
}

#[derive(Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
struct Transfer {
    format: String,
    version: u32,
    words: Vec<Record>,
}

#[derive(Serialize, Deserialize)]
#[serde(untagged)]
enum Record {
    Word(Entry),
    Exclusion(String),
}

fn transfer_format(exclusions: bool) -> &'static str {
    if exclusions {
        EXCLUSION_FORMAT
    } else {
        WORD_FORMAT
    }
}

#[cfg(all(test, feature = "gui"))]
mod tests {
    use super::*;

    #[test]
    fn only_explicit_priority_or_import_can_duplicate_a_built_in_spelling() {
        let fixture = tempfile::tempdir().unwrap();
        let entry = Entry::new("hello", "en").unwrap();
        let original = Dictionary::new(&fixture.path().join("original"));
        let mut corpus = Vocabulary::new(
            Path::new(env!("CARGO_MANIFEST_DIR"))
                .join("resources/language-detection/words.sqlite3"),
        );
        assert_eq!(
            original
                .add(std::slice::from_ref(&entry), &mut corpus)
                .unwrap()
                .inserted,
            0
        );
        assert!(original.add_priority(&entry, &mut corpus).unwrap());
        assert!(!original.add_priority(&entry, &mut corpus).unwrap());
        let export = fixture.path().join("dictionary.json");
        original.export(&export, false).unwrap();
        let restored = Dictionary::new(&fixture.path().join("restored"));
        assert_eq!(restored.import(&export, false).unwrap().inserted, 1);
        assert!(restored.contains(&entry.word, &entry.language).unwrap());
    }
}
