use crate::layout::{self, Language};
use rusqlite::{Connection, OpenFlags, OptionalExtension};
use std::{
    hash::{DefaultHasher, Hash, Hasher},
    io,
    path::{Path, PathBuf},
    time::Duration,
};

pub const LANGUAGES: [&str; 8] = ["en", "ru", "uk", "fr", "es", "it", "de", "pt"];
const RELIABLE: u8 = 120;
const SCORE_CACHE_SIZE: usize = 256;

pub struct Vocabulary {
    path: PathBuf,
    reader: Option<Connection>,
    scores: Vec<Option<(String, [u8; 8])>>,
}

#[derive(Clone, Debug, serde::Serialize, serde::Deserialize)]
pub struct Candidate {
    pub text: String,
    pub language: Language,
    pub score: u8,
}

#[derive(Clone, Copy, Debug, serde::Serialize, serde::Deserialize)]
pub enum Decision {
    InvalidWord,
    Technical,
    Excluded,
    AlreadyRecognized,
    NoCandidate,
    Ambiguous,
    Unverified,
    Applied,
    Failed,
}

impl Decision {
    pub fn message(self) -> &'static str {
        use crate::locale::text as tr;
        match self {
            Self::InvalidWord => tr(
                "reason.UnsupportedLanguage",
                "The word or layout is not supported",
            ),
            Self::Technical => tr("reason.ProtectedToken", "Protected technical input"),
            Self::Excluded => tr("reason.ExcludedWord", "Word excluded"),
            Self::AlreadyRecognized => tr(
                "reason.AlreadyCorrect",
                "Word is valid in the active layout",
            ),
            Self::NoCandidate => tr("reason.UnknownWord", "No reliable dictionary match"),
            Self::Ambiguous => tr("reason.Ambiguous", "Several dictionary matches"),
            Self::Unverified => tr(
                "reason.Unconfirmed",
                "The field could not confirm the change",
            ),
            Self::Applied => tr("reason.Corrected", "Corrected"),
            Self::Failed => tr("reason.Failed", "The correction failed"),
        }
    }
}

impl Vocabulary {
    pub fn new(path: PathBuf) -> Self {
        Self {
            path,
            reader: None,
            scores: Vec::new(),
        }
    }

    pub fn installed() -> io::Result<Self> {
        let exe = std::env::current_exe()?;
        let parent = exe
            .parent()
            .ok_or_else(|| io::Error::other("Missing executable directory"))?;
        let relative = Path::new("resources/language-detection/words.sqlite3");
        let sidecar = parent.join(relative);
        if sidecar.is_file() {
            return Ok(Self::new(sidecar));
        }
        let development = parent
            .parent()
            .and_then(Path::parent)
            .map(|root| root.join(relative));
        Ok(Self::new(
            development.filter(|path| path.is_file()).unwrap_or(sidecar),
        ))
    }

    fn open(&mut self) -> io::Result<&Connection> {
        if self.reader.is_none() {
            let reader = Connection::open_with_flags(
                &self.path,
                OpenFlags::SQLITE_OPEN_READ_ONLY | OpenFlags::SQLITE_OPEN_NO_MUTEX,
            )
            .map_err(io::Error::other)?;
            reader
                .busy_timeout(Duration::from_millis(20))
                .map_err(io::Error::other)?;
            reader
                .execute_batch("PRAGMA cache_size=-128; PRAGMA mmap_size=0; PRAGMA query_only=ON;")
                .map_err(io::Error::other)?;
            let id: u32 = reader
                .query_row("PRAGMA application_id", [], |row| row.get(0))
                .map_err(io::Error::other)?;
            let version: u32 = reader
                .query_row("PRAGMA user_version", [], |row| row.get(0))
                .map_err(io::Error::other)?;
            let languages: String = reader
                .query_row(
                    "SELECT value FROM metadata WHERE key='languages'",
                    [],
                    |row| row.get(0),
                )
                .map_err(io::Error::other)?;
            let languages: Vec<String> =
                serde_json::from_str(&languages).map_err(io::Error::other)?;
            if id != 0x54594c44 || !matches!(version, 2 | 3) || languages != LANGUAGES {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidData,
                    "Invalid Typlo vocabulary",
                ));
            }
            self.reader = Some(reader);
        }
        Ok(self.reader.as_ref().expect("reader initialized"))
    }

    #[cfg(windows)]
    pub(crate) fn retry_search(&mut self) -> io::Result<()> {
        self.reader = None;
        self.scores.clear();
        self.scores("hello").map(|_| ())
    }

    pub fn scores(&mut self, word: &str) -> io::Result<[u8; 8]> {
        let token = normalize(word);
        self.normalized_scores(&token)
    }

    fn normalized_scores(&mut self, token: &str) -> io::Result<[u8; 8]> {
        if !valid_word(token) {
            return Ok([0; 8]);
        }
        let mut hasher = DefaultHasher::new();
        token.hash(&mut hasher);
        let index = hasher.finish() as usize % SCORE_CACHE_SIZE;
        if let Some(Some((key, scores))) = self.scores.get(index)
            && key == token
        {
            return Ok(*scores);
        }
        let scores = self.lookup_scores(token)?;
        if self.scores.is_empty() {
            self.scores.resize_with(SCORE_CACHE_SIZE, || None);
        }
        self.scores[index] = Some((token.to_owned(), scores));
        Ok(scores)
    }

    fn lookup_scores(&mut self, token: &str) -> io::Result<[u8; 8]> {
        let mut statement = self
            .open()?
            .prepare_cached("SELECT weights, supplement_mask FROM words WHERE word=?1")
            .map_err(io::Error::other)?;
        let found = statement
            .query_row([token], |row| {
                Ok((row.get::<_, Vec<u8>>(0)?, row.get::<_, u8>(1)?))
            })
            .optional()
            .map_err(io::Error::other)?;
        let Some((weights, mask)) = found else {
            return Ok([0; 8]);
        };
        let mut scores: [u8; 8] = weights.try_into().map_err(|_| {
            io::Error::new(io::ErrorKind::InvalidData, "Invalid vocabulary weights")
        })?;
        if mask & !7 != 0 || (mask == 0 && scores == [0; 8]) {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "Invalid vocabulary evidence",
            ));
        }
        for (index, score) in scores.iter_mut().enumerate() {
            if mask & (1 << index) != 0 {
                *score = (*score).max(160);
            }
        }
        Ok(scores)
    }

    pub fn suggest(
        &mut self,
        personal: &crate::dictionary::Dictionary,
        typed: &str,
        source: Language,
        allowed: &[Language],
        protect: bool,
    ) -> io::Result<Option<(String, Language)>> {
        self.decision(personal, typed, source, allowed, protect)
            .map(|(result, _)| result)
    }

    pub fn decision(
        &mut self,
        personal: &crate::dictionary::Dictionary,
        typed: &str,
        source: Language,
        allowed: &[Language],
        protect: bool,
    ) -> io::Result<(Option<(String, Language)>, Decision)> {
        if !source.convertible()
            || source.dictionary_index().is_none()
            || !(2..=128).contains(&typed.encode_utf16().count())
            || typed.chars().any(char::is_whitespace)
            || typed
                .chars()
                .any(|c| c.is_numeric() || "@_\\:=".contains(c))
        {
            return Ok((None, Decision::InvalidWord));
        }
        if protect
            && (technical(typed)
                || (source != Language::En
                    && technical(&layout::convert(typed, source, Language::En, true))))
        {
            return Ok((None, Decision::Technical));
        }
        if personal.is_excluded(typed)? {
            return Ok((None, Decision::Excluded));
        }
        let current = self.score(personal, typed, source)?;
        if current >= RELIABLE {
            return Ok((None, Decision::AlreadyRecognized));
        }
        let mut candidates = Vec::new();
        for &target in allowed
            .iter()
            .filter(|&&target| target != source && target.convertible())
        {
            let result = layout::convert(typed, source, target, true);
            if result == typed {
                continue;
            }
            let score = self.score(personal, &result, target)?;
            // A dot inside a long word can be a Cyrillic letter on the same key.
            let misplaced_dot = typed.len() >= 8
                && typed.split_once('.').is_some_and(|(before, after)| {
                    !before.is_empty()
                        && !after.is_empty()
                        && before
                            .bytes()
                            .chain(after.bytes())
                            .all(|byte| byte.is_ascii_alphabetic())
                });
            let weak_cyrillic = score >= if misplaced_dot { 100 } else { 110 }
                && current == 0
                && typed.len() >= 4
                && (misplaced_dot || typed.bytes().all(|byte| byte.is_ascii_alphabetic()))
                && result
                    .chars()
                    .all(|c| ('\u{0400}'..='\u{04ff}').contains(&c));
            if score >= RELIABLE || weak_cyrillic {
                candidates.push((result, target, score));
            }
        }
        candidates.sort_by_key(|candidate| std::cmp::Reverse(candidate.2));
        let Some((text, target, score)) = candidates.first() else {
            return Ok((None, Decision::NoCandidate));
        };
        if score.saturating_sub(current) < 32
            || candidates
                .iter()
                .any(|(other, _, evidence)| other != text && score.saturating_sub(*evidence) < 24)
        {
            return Ok((None, Decision::Ambiguous));
        }
        Ok((Some((text.clone(), *target)), Decision::Unverified))
    }

    pub fn options(
        &mut self,
        personal: &crate::dictionary::Dictionary,
        text: &str,
        source: Language,
        allowed: &[Language],
    ) -> io::Result<Vec<Candidate>> {
        if !(2..=128).contains(&text.encode_utf16().count())
            || text.chars().any(char::is_whitespace)
            || !source.convertible()
        {
            return Ok(Vec::new());
        }
        let mut choices = Vec::new();
        for language in allowed
            .iter()
            .copied()
            .filter(|language| language.convertible())
        {
            let candidate = layout::convert(text, source, language, true);
            let score = self.score(personal, &candidate, language)?;
            if score > 0 && candidate != text {
                choices.push(Candidate {
                    text: candidate,
                    language,
                    score,
                });
            }
        }
        choices.sort_by_key(|choice| std::cmp::Reverse(choice.score));
        choices.truncate(16);
        Ok(choices)
    }

    fn score(
        &mut self,
        personal: &crate::dictionary::Dictionary,
        rendered: &str,
        language: Language,
    ) -> io::Result<u8> {
        let word = normalize(rendered.trim_matches(|c: char| ".,!?;:\"()[]{}«»…".contains(c)));
        let Some(index) = language.dictionary_index() else {
            return Ok(0);
        };
        if personal.contains(&word, LANGUAGES[index])? {
            return Ok(250);
        }
        Ok(self.normalized_scores(&word)?[index])
    }
}

pub(crate) fn normalize(word: &str) -> String {
    if !word.contains(['’', 'ʼ', '‘', '‐', '‑']) {
        return word.to_lowercase();
    }
    word.chars()
        .map(|character| match character {
            '’' | 'ʼ' | '‘' => '\'',
            '‐' | '‑' => '-',
            other => other,
        })
        .collect::<String>()
        .to_lowercase()
}

pub(crate) fn valid_word(word: &str) -> bool {
    let mut letters = 0;
    let mut last_was_letter = false;
    for character in word.chars() {
        if character.is_alphabetic() {
            letters += 1;
            last_was_letter = true;
        } else if last_was_letter && matches!(character, '\'' | '-') {
            last_was_letter = false;
        } else {
            return false;
        }
    }
    last_was_letter && letters >= 2 && word.encode_utf16().count() <= 128
}

pub fn technical(word: &str) -> bool {
    let bare = word.trim_matches(|c: char| ".,!?;\"()[]{}«»".contains(c));
    if bare
        .chars()
        .any(|c| c.is_numeric() || "@_\\/:=`$|<>+*&".contains(c))
        || bare.starts_with(['-', '~', '#'])
        || bare.contains("--")
        || (bare.chars().skip(1).any(char::is_uppercase) && bare.chars().any(char::is_lowercase))
    {
        return true;
    }
    let Some((first, suffix)) = bare.rsplit_once('.') else {
        return false;
    };
    first
        .split('.')
        .all(|part| !part.is_empty() && part.bytes().all(|c| c.is_ascii_alphabetic()))
        && (first
            .split('.')
            .next()
            .is_some_and(|part| part.eq_ignore_ascii_case("www"))
            || [
                "com", "org", "net", "io", "dev", "app", "ua", "uk", "fr", "de", "es", "it", "pt",
                "json", "xml", "cs", "js", "ts", "py", "txt", "exe", "dll", "md", "sql", "sh",
                "ps",
            ]
            .iter()
            .any(|ext| ext.eq_ignore_ascii_case(suffix)))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::dictionary::Dictionary;

    #[test]
    fn cached_builtin_scores_do_not_hide_edits_to_personal_words() {
        let mut corpus = Vocabulary::new(
            PathBuf::from(env!("CARGO_MANIFEST_DIR"))
                .join("resources/language-detection/words.sqlite3"),
        );
        let profile = tempfile::tempdir().unwrap();
        let personal = Dictionary::new(profile.path());
        let word = "qzxqzxqzx";
        let entry = crate::dictionary::Entry {
            word: word.into(),
            language: "en".into(),
        };
        assert_eq!(corpus.score(&personal, word, Language::En).unwrap(), 0);
        personal
            .add(std::slice::from_ref(&entry), &mut corpus)
            .unwrap();
        assert_eq!(corpus.score(&personal, word, Language::En).unwrap(), 250);
        assert!(personal.remove(&entry).unwrap());
        assert_eq!(corpus.score(&personal, word, Language::En).unwrap(), 0);
        assert_eq!(normalize("L’ŒUVRE‑Test"), "l'œuvre-test");
    }

    #[test]
    fn opens_original_vocabulary_and_respects_application_languages() {
        let mut corpus = Vocabulary::new(
            PathBuf::from(env!("CARGO_MANIFEST_DIR"))
                .join("resources/language-detection/words.sqlite3"),
        );
        let empty = Dictionary::empty();
        for (typed, source, result, target) in [
            ("ghbdtn", Language::En, "привет", Language::Ru),
            ("rfr", Language::En, "как", Language::Ru),
            ("yt", Language::En, "не", Language::Ru),
            ("gjlrk.xbnm", Language::En, "подключить", Language::Ru),
            ("rjvgm.nth", Language::En, "компьютер", Language::Ru),
            ("yfgbib", Language::En, "напиши", Language::Ru),
            ("nfcrf", Language::En, "таска", Language::Ru),
            ("ыйд", Language::Ru, "sql", Language::En),
            ("Пше", Language::Ru, "Git", Language::En),
        ] {
            assert_eq!(
                corpus
                    .suggest(&empty, typed, source, &[source, target], true)
                    .unwrap(),
                Some((result.into(), target)),
                "{typed}"
            );
            assert_eq!(
                corpus
                    .suggest(&empty, typed, source, &[source], true)
                    .unwrap(),
                None,
                "{typed}"
            );
        }
        assert_eq!(
            corpus
                .suggest(
                    &empty,
                    "rjvg.nth",
                    Language::En,
                    &[Language::En, Language::Ru],
                    true
                )
                .unwrap(),
            None,
        );
        assert!(corpus.scores("bonjour").unwrap()[3] > 0);
        assert!(technical("index.ts"));
        assert!(technical("C:\\folder"));
        assert!(!technical("(Speling)"));
        assert!(!technical("htdm.dftim"));
    }
}
