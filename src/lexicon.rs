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
// Evidence for a word whose typed form is unknown in its own layout.
const WEAK: u8 = 60;
const WEAK_LENGTH: usize = 4;
// A very common word: beats a short spelled word typed in another layout.
const COMMON: u8 = 170;
const SCORE_CACHE_SIZE: usize = 256;

pub struct Vocabulary {
    path: PathBuf,
    reader: Option<Connection>,
    scores: Vec<Option<(String, [u8; 8])>>,
}

const SPELLING_LOCALES: [&str; 3] = ["en_US", "ru_RU", "uk_UA"];

/// Full spelling dictionaries for English, Russian and Ukrainian, shared by
/// the process and loaded once. The frequency list lacks many ordinary and
/// technical words. Missing or broken files give no dictionary.
static SPELLING_DICTIONARIES: [std::sync::OnceLock<Option<spellbook::Dictionary>>; 3] =
    [const { std::sync::OnceLock::new() }; 3];

/// Loads a spelling dictionary, waiting while another thread loads it.
fn load_spelling_dictionary(
    vocabulary: &Path,
    index: usize,
) -> Option<&'static spellbook::Dictionary> {
    SPELLING_DICTIONARIES[index]
        .get_or_init(|| {
            let stem = vocabulary
                .parent()?
                .parent()?
                .join("spelling")
                .join(SPELLING_LOCALES[index]);
            let aff = std::fs::read_to_string(stem.with_extension("aff")).ok()?;
            let dic = std::fs::read_to_string(stem.with_extension("dic")).ok()?;
            spellbook::Dictionary::new(&aff, &dic).ok()
        })
        .as_ref()
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
                Ok((row.get::<_, [u8; 8]>(0)?, row.get::<_, u8>(1)?))
            })
            .optional()
            .map_err(io::Error::other)?;
        let Some((mut scores, mask)) = found else {
            return Ok([0; 8]);
        };
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

    /// Whether the full spelling dictionary of the language accepts the word.
    /// Missing or broken dictionaries count as no evidence.
    fn spelled(&self, word: &str, language: Language) -> bool {
        let Some(index) = language.dictionary_index().filter(|&index| index < 3) else {
            return false;
        };
        // Leading commas and dots are letter keys on Cyrillic layouts (б, ю),
        // so only quotes and brackets are trimmed at the start.
        let word = word
            .trim_start_matches(|c: char| "\"“”„(«".contains(c))
            .trim_end_matches(|c: char| ".,!?;:\"“”)»…".contains(c));
        // Each dictionary also lists some foreign words; only the language's
        // own script counts.
        let own_script = word.chars().filter(|c| c.is_alphabetic()).all(|c| {
            if index == 0 {
                c.is_ascii_alphabetic()
            } else {
                ('\u{0400}'..='\u{04ff}').contains(&c)
            }
        });
        if !own_script || !word.chars().any(char::is_alphabetic) {
            return false;
        }
        // Never wait for a dictionary on the input path: until it is loaded,
        // the frequency list alone decides, as it did before.
        self.preload_spelling();
        SPELLING_DICTIONARIES[index]
            .get()
            .and_then(Option::as_ref)
            .is_some_and(|dictionary| dictionary.check(word))
    }

    /// Starts loading the spelling dictionaries in the background once, so
    /// typing never waits for them.
    pub fn preload_spelling(&self) {
        static STARTED: std::sync::Once = std::sync::Once::new();
        STARTED.call_once(|| {
            let path = self.path.clone();
            let _ = std::thread::Builder::new()
                .name("typlo-spelling-preload".into())
                .spawn(move || {
                    for index in 0..SPELLING_LOCALES.len() {
                        load_spelling_dictionary(&path, index);
                    }
                });
        });
    }

    /// Loads the spelling dictionaries now, for checks that need them.
    #[cfg(test)]
    fn load_spelling(&self) {
        for index in 0..SPELLING_LOCALES.len() {
            load_spelling_dictionary(&self.path, index);
        }
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
        // A correctly spelled word of the current layout stays, even when it
        // is too rare for the frequency list. Spelling dictionaries accept
        // many short letter groups, so for those only a very common word in
        // another layout wins.
        let spelled = self.spelled(typed, source);
        let short = typed.chars().filter(|c| c.is_alphabetic()).count() < WEAK_LENGTH;
        if spelled && !short {
            return Ok((None, Decision::AlreadyRecognized));
        }
        let mut candidates = Vec::new();
        // The typed text is not a known word at all: a less frequent word in
        // another layout is then enough evidence. Keys typed for letters such
        // as ж, х, ї or є appear as punctuation, so the converted text decides.
        let unknown_source = current == 0 && typed.chars().count() >= WEAK_LENGTH;
        for &target in allowed
            .iter()
            .filter(|&&target| target != source && target.convertible())
        {
            let result = layout::convert(typed, source, target, true);
            // A letter key that becomes punctuation at the edge of the word
            // would be trimmed away and leave a different, shorter word. Only
            // sentence punctuation at the end ("hello." from "руддщю") is a
            // plausible part of what was meant.
            let core = result.trim_end_matches(|c: char| ".,!?;:".contains(c));
            if result == typed
                || result.chars().count() != typed.chars().count()
                || (typed.starts_with(char::is_alphanumeric)
                    && !result.starts_with(char::is_alphanumeric))
                || (typed.ends_with(char::is_alphanumeric)
                    && !core.ends_with(char::is_alphanumeric))
            {
                continue;
            }
            let mut score = self.score(personal, &result, target)?;
            if unknown_source && score < WEAK && self.spelled(core, target) {
                score = WEAK;
            }
            // The word itself decides; trailing sentence punctuation is kept
            // in the result.
            let weak = unknown_source
                && score >= WEAK
                && core
                    .chars()
                    .all(|c| c.is_alphabetic() || c == '\'' || c == '’' || c == '-')
                && core.chars().filter(|c| c.is_alphabetic()).count() >= WEAK_LENGTH;
            if score >= RELIABLE || weak {
                candidates.push((result, target, score));
            }
        }
        // Keep the first configured layout on ties, including identical Russian
        // and Ukrainian output. Compare ambiguity only against different text.
        let Some(index) = candidates
            .iter()
            .enumerate()
            .max_by_key(|(index, candidate)| (candidate.2, std::cmp::Reverse(*index)))
            .map(|(index, _)| index)
        else {
            return Ok((None, Decision::NoCandidate));
        };
        let (text, target, score) = candidates.swap_remove(index);
        if spelled && score < COMMON {
            return Ok((None, Decision::AlreadyRecognized));
        }
        if score.saturating_sub(current) < 32
            || candidates
                .iter()
                .any(|(other, _, evidence)| other != &text && score.saturating_sub(*evidence) < 24)
        {
            return Ok((None, Decision::Ambiguous));
        }
        Ok((Some((text, target)), Decision::Unverified))
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
            .filter(|&language| language != source)
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
        // Brackets stay: on Cyrillic layouts they are the keys of х, ъ and ї.
        let word = normalize(rendered.trim_matches(|c: char| ".,!?;:\"“”„()«»…".contains(c)));
        let Some(index) = language.dictionary_index() else {
            return Ok(0);
        };
        if personal.contains(&word, LANGUAGES[index])? {
            return Ok(250);
        }
        let score = self.normalized_scores(&word)?[index];
        Ok(if index == 0 && technical_term(&word) {
            score.max(TECHNICAL_TERM)
        } else {
            score
        })
    }
}

/// Score of a listed technical term: it counts as a common English word.
const TECHNICAL_TERM: u8 = 150;

fn technical_term(word: &str) -> bool {
    static TERMS: std::sync::LazyLock<std::collections::HashSet<&'static str>> =
        std::sync::LazyLock::new(|| {
            include_str!("it_terms.txt")
                .lines()
                .map(str::trim)
                .filter(|line| !line.is_empty() && !line.starts_with('#'))
                .collect()
        });
    TERMS.contains(word)
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
    let bare = word.trim_matches(|c: char| ".,!?;\"“”„()[]{}«»…".contains(c));
    if bare
        .chars()
        .any(|c| c.is_numeric() || "@_\\/:=$|<>+*&".contains(c))
        // Inline code is quoted with backticks; one inside a word is the key
        // of ё or the Ukrainian apostrophe.
        || (bare.len() > 1 && bare.starts_with('`') && bare.ends_with('`'))
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
            ("xnj", Language::En, "что", Language::Ru),
            ("“Ghbdtn”", Language::En, "“Привет”", Language::Ru),
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
        assert!(!technical("“Ghbdtn”"));
        assert!(!technical("htdm.dftim"));
    }

    #[test]
    fn converts_ukrainian_russian_english_and_technical_words_both_ways() {
        let mut corpus = Vocabulary::new(
            PathBuf::from(env!("CARGO_MANIFEST_DIR"))
                .join("resources/language-detection/words.sqlite3"),
        );
        corpus.load_spelling();
        let empty = Dictionary::empty();
        let all = [Language::En, Language::Ru, Language::Uk];
        let cases: [(Language, &[&str]); 3] = [
            (
                Language::Uk,
                &[
                    "привіт",
                    "дякую",
                    "розробка",
                    "їжак",
                    "тестування",
                    "налаштування",
                    "користувач",
                    "клавіатура",
                    "комп'ютер",
                    "м'ясо",
                    "проєкт",
                    "український",
                    "пам'ятаю",
                    "яблуко",
                    "весна",
                    "кнопка",
                ],
            ),
            (
                Language::Ru,
                &[
                    "привет",
                    "разработка",
                    "ёжик",
                    "клавиатура",
                    "пользователь",
                    "браузер",
                    "подъезд",
                    "объект",
                    "сахар",
                    "кнопка",
                    "мочь",
                ],
            ),
            (
                Language::En,
                &[
                    "hello",
                    "server",
                    "error",
                    "user",
                    "menu",
                    "password",
                    "keyboard",
                    "settings",
                    "docker",
                    "json",
                    "kubernetes",
                    "typescript",
                    "readme",
                ],
            ),
        ];
        for (language, words) in cases {
            for word in words {
                let sources: &[Language] = if language == Language::En {
                    &[Language::Ru, Language::Uk]
                } else {
                    &[Language::En]
                };
                for &source in sources {
                    let typed = layout::convert(word, language, source, true);
                    let result = corpus.suggest(&empty, &typed, source, &all, true).unwrap();
                    assert_eq!(
                        result.as_ref().map(|(text, _)| text.as_str()),
                        Some(*word),
                        "{word} typed as {typed}"
                    );
                    // Text typed the same on Russian and Ukrainian layouts may
                    // switch to either of them.
                    if layout::convert(&typed, source, Language::Ru, true)
                        != layout::convert(&typed, source, Language::Uk, true)
                    {
                        assert_eq!(result.unwrap().1, language, "{word}");
                    }
                }
                assert_eq!(
                    corpus.suggest(&empty, word, language, &all, true).unwrap(),
                    None,
                    "{word} as typed"
                );
            }
        }
        // Rare but correctly spelled words stay in their own layout, including
        // short ones and Russian and Ukrainian look-alikes.
        for (word, language) in [
            ("lei", Language::En),
            ("tut", Language::En),
            ("peke", Language::En),
            ("jwt", Language::En),
            ("вытку", Language::Ru),
            ("хук", Language::Ru),
            ("біло", Language::Uk),
            ("брук", Language::Uk),
            ("сіро", Language::Uk),
        ] {
            assert_eq!(
                corpus.suggest(&empty, word, language, &all, true).unwrap(),
                None,
                "{word}"
            );
        }
        // Sentence punctuation typed with a letter key stays part of the word.
        for (typed, language, expected) in [
            ("руддщю", Language::Ru, "hello."),
            ("цщкдвб", Language::Ru, "world,"),
        ] {
            let result = corpus.suggest(&empty, typed, language, &all, true).unwrap();
            assert_eq!(
                result.map(|(text, _)| text).as_deref(),
                Some(expected),
                "{typed}"
            );
        }
        // A rare word known only to the spelling dictionary converts the same
        // with or without trailing punctuation.
        for word in ["gherkin", "marmot", "brisket", "zeppelin"] {
            for ending in ["", ".", ","] {
                let expected = format!("{word}{ending}");
                let typed = layout::convert(&expected, Language::En, Language::Ru, true);
                let result = corpus
                    .suggest(&empty, &typed, Language::Ru, &all, true)
                    .unwrap();
                assert_eq!(result.map(|(text, _)| text), Some(expected), "{typed}");
            }
        }
    }

    #[test]
    fn known_words_do_not_wait_for_spelling_dictionaries() {
        let mut corpus = Vocabulary::new(
            PathBuf::from(env!("CARGO_MANIFEST_DIR"))
                .join("resources/language-detection/words.sqlite3"),
        );
        let empty = Dictionary::empty();
        let all = [Language::En, Language::Ru];
        // Frequent words and their conversions are decided by the frequency
        // list alone, whether or not the dictionaries have loaded yet.
        assert_eq!(
            corpus
                .suggest(&empty, "ghbdtn", Language::En, &all, true)
                .unwrap()
                .map(|(text, _)| text)
                .as_deref(),
            Some("привет")
        );
        assert_eq!(
            corpus
                .suggest(&empty, "hello", Language::En, &all, true)
                .unwrap(),
            None
        );
    }
}
