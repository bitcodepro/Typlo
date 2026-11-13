use crate::{
    dictionary::Dictionary,
    lexicon::{LANGUAGES, Vocabulary, normalize, technical},
};
use serde::{Deserialize, Serialize};
use std::{
    collections::{HashMap, HashSet},
    io,
    ops::Range,
    path::{Path, PathBuf},
};

pub const MAX_CHARACTERS: usize = 20_000;
pub const REQUEST_LIMIT: u64 = 256 * 1024;
const MAX_ISSUES: usize = 300;
const MAX_WORDS: usize = 3000;
const LOCALES: [&str; 3] = ["en_US", "ru_RU", "uk_UA"];
const SPELLING_LANGUAGES: [&str; 3] = ["en", "ru", "uk"];
const CONFIDENT_FREQUENCY: u8 = 120;

#[derive(Clone, Copy)]
struct CommonCorrection {
    language: usize,
    replacement: &'static str,
    needs_context: bool,
}

struct SpellingToken {
    range: Range<usize>,
    scripts: u8,
    correction: Option<CommonCorrection>,
}

struct RankedSuggestion {
    proposal: String,
    distance: usize,
    frequency: u8,
    edge_deletion: bool,
    letter_insertion: bool,
    word_split: bool,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Issue {
    pub range: Range<usize>,
    pub word: String,
    pub language: String,
    pub suggestions: Vec<String>,
}

#[derive(Serialize, Deserialize)]
pub struct Request {
    pub text: String,
    pub language: Option<usize>,
    #[serde(default)]
    pub quick: bool,
}

#[derive(Serialize, Deserialize)]
pub struct Review {
    pub text: String,
    pub issues: Vec<Issue>,
    pub origin: usize,
}

pub fn worker(profile: &Path) -> io::Result<()> {
    use std::io::Read;
    let stdin = io::stdin();
    let request: Request =
        serde_json::from_reader(stdin.lock().take(REQUEST_LIMIT)).map_err(io::Error::other)?;
    let issues = Spelling::installed()?.check_mode(
        &request.text,
        &Dictionary::new(profile),
        request.language,
        request.quick,
    )?;
    serde_json::to_writer(io::stdout().lock(), &issues).map_err(io::Error::other)
}

pub struct Spelling {
    directory: PathBuf,
    dictionaries: [Option<spellbook::Dictionary>; 3],
}

impl Spelling {
    pub fn installed() -> io::Result<Self> {
        let exe = std::env::current_exe()?;
        let parent = exe
            .parent()
            .ok_or_else(|| io::Error::other("Missing executable directory"))?;
        let relative = Path::new("resources/spelling");
        let sidecar = parent.join(relative);
        let development = parent
            .parent()
            .and_then(Path::parent)
            .map(|root| root.join(relative));
        let directory = if sidecar.is_dir() {
            sidecar
        } else {
            development.filter(|path| path.is_dir()).unwrap_or(sidecar)
        };
        Ok(Self::new(directory))
    }

    fn new(directory: PathBuf) -> Self {
        Self {
            directory,
            dictionaries: [None, None, None],
        }
    }

    fn dictionary(&mut self, language: usize) -> io::Result<&spellbook::Dictionary> {
        let slot = &mut self.dictionaries[language];
        if slot.is_none() {
            *slot = Some(Self::load_dictionary(&self.directory, language)?);
        }
        Ok(slot.as_ref().expect("dictionary initialized"))
    }

    fn load_dictionary(directory: &Path, language: usize) -> io::Result<spellbook::Dictionary> {
        let stem = directory.join(LOCALES[language]);
        let aff = std::fs::read_to_string(stem.with_extension("aff"))?;
        let dic = std::fs::read_to_string(stem.with_extension("dic"))?;
        spellbook::Dictionary::new(&aff, &dic)
            .map_err(|error| io::Error::new(io::ErrorKind::InvalidData, error.to_string()))
    }

    fn preload_dictionaries(&mut self, mask: u8) {
        if mask.count_ones() < 2
            || !std::thread::available_parallelism().is_ok_and(|cores| cores.get() > 1)
        {
            return;
        }
        let mut missing = (0..LOCALES.len())
            .filter(|&language| {
                mask & (1 << language) != 0 && self.dictionaries[language].is_none()
            })
            .collect::<Vec<_>>();
        let Some(background) = missing.pop().filter(|_| !missing.is_empty()) else {
            return;
        };
        let directory = &self.directory;
        std::thread::scope(|scope| {
            let loader = std::thread::Builder::new()
                .spawn_scoped(scope, move || Self::load_dictionary(directory, background));
            let Ok(loader) = loader else { return };
            for language in missing {
                self.dictionaries[language] = Self::load_dictionary(directory, language).ok();
            }
            self.dictionaries[background] = loader.join().ok().and_then(Result::ok);
        });
    }

    pub fn check(
        &mut self,
        text: &str,
        personal: &Dictionary,
        forced: Option<usize>,
    ) -> io::Result<Vec<Issue>> {
        self.check_mode(text, personal, forced, false)
    }

    fn check_mode(
        &mut self,
        text: &str,
        personal: &Dictionary,
        forced: Option<usize>,
        quick: bool,
    ) -> io::Result<Vec<Issue>> {
        if text.encode_utf16().count() > MAX_CHARACTERS
            || forced.is_some_and(|language| language >= LOCALES.len())
        {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "Invalid spelling selection",
            ));
        }
        let mut tokens = tokens(text);
        if tokens.len() > MAX_WORDS {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "Too many words",
            ));
        }
        let mut issues = Vec::new();
        let mut known: HashMap<(&str, usize), bool> = HashMap::new();
        let mut suggestion_jobs = Vec::new();
        let mut job_by_word = HashMap::new();
        let mut pending = Vec::new();
        let mut corrections = HashMap::new();
        tokens.retain(|token| token.range.len() <= 128);
        if quick && forced.is_none() {
            let mask = tokens.iter().fold(0, |mask, token| mask | token.scripts);
            self.preload_dictionaries(mask);
        }
        'paragraphs: for paragraph in
            tokens.chunk_by(|left, right| !text[left.range.end..right.range.start].contains('\n'))
        {
            let mut votes = [0i32; 3];
            let needs_context = forced.is_none()
                && paragraph.iter().any(|token| {
                    token.correction.is_some_and(|rule| rule.needs_context)
                        && !inside_quotes(text, &token.range)
                });
            let mut context_votes = [0; 3];
            let mut strong_context = [false; 3];
            let mut context_words = HashSet::new();
            let mut context_vocabulary =
                needs_context.then(|| Vocabulary::new(self.vocabulary_path()));
            for token in paragraph {
                if token.scripts != 0b110 || forced.is_some() {
                    continue;
                }
                let word = &text[token.range.clone()];
                let ru = self.known(word, 1, personal, &mut known)?;
                let uk = self.known(word, 2, personal, &mut known)?;
                if ru && !uk {
                    votes[1] += 1;
                }
                if uk && !ru {
                    votes[2] += 1;
                }
                if let Some(vocabulary) = &mut context_vocabulary
                    && token.correction.is_none()
                    && !inside_quotes(text, &token.range)
                    && context_words.insert(word)
                    && let Some((language, strong)) = cyrillic_evidence(word, ru, uk, vocabulary)
                {
                    context_votes[language] += 1;
                    strong_context[language] |= strong;
                }
            }
            let context_language = forced.or_else(|| match context_votes {
                [_, ru, 0] if ru >= 2 && strong_context[1] => Some(1),
                [_, 0, uk] if uk >= 2 && strong_context[2] => Some(2),
                _ => None,
            });
            for token in paragraph {
                if issues.len() >= MAX_ISSUES {
                    break 'paragraphs;
                }
                let range = &token.range;
                let word = &text[range.clone()];
                let (excluded, pair) = if let Some(rules) = corrections.get(word) {
                    rules
                } else {
                    let excluded = personal.is_excluded(word)?;
                    let pair = if excluded {
                        None
                    } else {
                        personal.correction(word)?
                    };
                    corrections.entry(word).or_insert((excluded, pair))
                };
                if *excluded {
                    continue;
                }
                if let Some(pair) = pair {
                    let corrected = case_like(word, pair);
                    if corrected != word {
                        issues.push(Issue {
                            range: range.clone(),
                            word: word.into(),
                            language: "custom".into(),
                            suggestions: vec![corrected],
                        });
                    }
                    continue;
                }
                if let Some(rule) = token.correction
                    && forced.is_none_or(|language| language == rule.language)
                    && (!rule.needs_context
                        || (context_language == Some(rule.language) && !inside_quotes(text, range)))
                {
                    if personal.contains(&normalize(word), LANGUAGES[rule.language])? {
                        continue;
                    }
                    issues.push(Issue {
                        range: range.clone(),
                        word: word.into(),
                        language: SPELLING_LANGUAGES[rule.language].into(),
                        suggestions: vec![case_like(word, rule.replacement)],
                    });
                    continue;
                }
                let mut mask = forced.map_or(token.scripts, |index| 1 << index);
                let mut accepted = false;
                for language in 0..LOCALES.len() {
                    if mask & (1 << language) != 0
                        && self.known(word, language, personal, &mut known)?
                    {
                        accepted = true;
                        break;
                    }
                }
                if accepted || mask == 0 {
                    continue;
                }
                if mask == 0b110 && (votes[1] - votes[2]).abs() >= 2 {
                    mask = if votes[1] > votes[2] { 0b010 } else { 0b100 };
                }
                let job = *job_by_word.entry((word, mask)).or_insert_with(|| {
                    let index = suggestion_jobs.len();
                    suggestion_jobs.push((word, mask));
                    index
                });
                pending.push((issues.len(), job));
                let language = match mask {
                    1 => "en",
                    2 => "ru",
                    4 => "uk",
                    _ => "ru/uk",
                };
                issues.push(Issue {
                    range: range.clone(),
                    word: word.into(),
                    language: language.into(),
                    suggestions: Vec::new(),
                });
            }
        }
        let suggestions = self.resolve(&suggestion_jobs, quick);
        for (issue, job) in pending {
            issues[issue].suggestions.clone_from(&suggestions[job]);
        }
        Ok(issues)
    }

    fn resolve(&self, jobs: &[(&str, u8)], quick: bool) -> Vec<Vec<String>> {
        if jobs.is_empty() {
            return Vec::new();
        }
        let path = self.vocabulary_path();
        let evaluate = |indices: std::iter::StepBy<Range<usize>>| {
            let mut corpus = Vocabulary::new(path.clone());
            indices
                .map(|index| {
                    let (word, mask) = jobs[index];
                    (index, self.suggest(word, mask, &mut corpus, quick))
                })
                .collect::<Vec<_>>()
        };
        if quick
            && jobs.len() >= 2
            && std::thread::available_parallelism().is_ok_and(|n| n.get() > 1)
        {
            let results = std::thread::scope(|scope| {
                let worker = std::thread::Builder::new()
                    .spawn_scoped(scope, || evaluate((1..jobs.len()).step_by(2)))
                    .ok()?;
                let main = evaluate((0..jobs.len()).step_by(2));
                let background = worker.join().ok()?;
                let mut results = vec![Vec::new(); jobs.len()];
                for (index, suggestions) in main.into_iter().chain(background) {
                    results[index] = suggestions;
                }
                Some(results)
            });
            if let Some(results) = results {
                return results;
            }
        }
        evaluate((0..jobs.len()).step_by(1))
            .into_iter()
            .map(|(_, result)| result)
            .collect()
    }

    fn vocabulary_path(&self) -> PathBuf {
        self.directory
            .with_file_name("language-detection")
            .join("words.sqlite3")
    }

    fn known<'a>(
        &mut self,
        word: &'a str,
        language: usize,
        personal: &Dictionary,
        cache: &mut HashMap<(&'a str, usize), bool>,
    ) -> io::Result<bool> {
        if let Some(&accepted) = cache.get(&(word, language)) {
            return Ok(accepted);
        }
        let accepted =
            personal.contains(word, LANGUAGES[language])? || self.dictionary(language)?.check(word);
        cache.insert((word, language), accepted);
        Ok(accepted)
    }

    fn suggest(&self, word: &str, mask: u8, corpus: &mut Vocabulary, quick: bool) -> Vec<String> {
        if quick && !word.contains('-') {
            let mut ranked = self.rank_suggestions(word, mask, corpus, false);
            if let [first, rest @ ..] = ranked.as_slice()
                && first.distance == 1
                && !first.edge_deletion
                && first.frequency >= CONFIDENT_FREQUENCY
                && !first.proposal.chars().any(char::is_whitespace)
                && rest.iter().all(|other| {
                    other.distance > 1
                        || other.frequency < first.frequency
                        || (first.letter_insertion && other.edge_deletion)
                })
            {
                return vec![ranked.swap_remove(0).proposal];
            }
        }
        self.rank_suggestions(word, mask, corpus, true)
            .into_iter()
            .map(|candidate| candidate.proposal)
            .collect()
    }

    fn rank_suggestions(
        &self,
        word: &str,
        mask: u8,
        corpus: &mut Vocabulary,
        ngram: bool,
    ) -> Vec<RankedSuggestion> {
        let mut candidates = Vec::new();
        let mut seen = HashSet::new();
        let normalized = normalize(word);
        let original: Vec<_> = normalized.chars().collect();
        let after_first = &normalized[original[0].len_utf8()..];
        let before_last = &normalized[..normalized.len() - original[original.len() - 1].len_utf8()];
        for (language, dictionary) in self.dictionaries.iter().enumerate() {
            if mask & (1 << language) == 0 {
                continue;
            }
            let mut suggestions = Vec::new();
            dictionary
                .as_ref()
                .expect("each suggestion language has been checked")
                .suggester()
                .with_ngram_suggestions(ngram)
                .suggest(word, &mut suggestions);
            for proposal in suggestions
                .into_iter()
                .filter(|proposal| proposal.len() <= 128)
                .take(5)
            {
                let proposal = case_like(word, &proposal);
                if proposal == word || !seen.insert(proposal.clone()) {
                    continue;
                }
                let scores = corpus.scores(&proposal).unwrap_or_default();
                let evidence = (0..3)
                    .filter(|index| mask & (1 << index) != 0)
                    .map(|index| scores[index])
                    .max()
                    .unwrap_or(0);
                let candidate = normalize(&proposal);
                let distance = distance(&original, &candidate);
                let word_split = distance == 1
                    && candidate.split_once(' ').is_some_and(|(left, right)| {
                        left.chars().count() >= 2
                            && right.chars().count() >= 2
                            && normalized.strip_prefix(left) == Some(right)
                            && {
                                let left = corpus.scores(left).unwrap_or_default();
                                let right = corpus.scores(right).unwrap_or_default();
                                (0..LOCALES.len()).any(|index| {
                                    mask & (1 << index) != 0
                                        && left[index].min(right[index]) >= CONFIDENT_FREQUENCY
                                })
                            }
                    });
                candidates.push(RankedSuggestion {
                    distance,
                    frequency: evidence,
                    edge_deletion: candidate == after_first || candidate == before_last,
                    letter_insertion: distance == 1 && inserts_letter(&original, &candidate),
                    word_split,
                    proposal,
                });
            }
        }
        let has_insertion = candidates
            .iter()
            .any(|candidate| candidate.letter_insertion);
        candidates.sort_by_key(|candidate| {
            // Favor a spelling that retains the typed letters when the alternative drops an edge.
            let priority = if has_insertion {
                2 * u8::from(candidate.edge_deletion) + u8::from(candidate.word_split)
            } else {
                u8::from(!candidate.word_split)
            };
            (
                candidate.distance,
                priority,
                std::cmp::Reverse(candidate.frequency),
            )
        });
        candidates
    }
}

pub fn apply(text: &str, issues: &[Issue], choices: &[Option<usize>]) -> io::Result<String> {
    if issues.len() != choices.len() {
        return Err(invalid_result());
    }
    let mut output = String::with_capacity(text.len());
    let mut cursor = 0;
    for (issue, choice) in issues.iter().zip(choices) {
        if issue.range.start < cursor
            || issue.range.is_empty()
            || text.get(issue.range.clone()) != Some(&issue.word)
        {
            return Err(invalid_result());
        }
        output.push_str(&text[cursor..issue.range.start]);
        output.push_str(match choice {
            Some(index) => issue.suggestions.get(*index).ok_or_else(invalid_result)?,
            None => &issue.word,
        });
        cursor = issue.range.end;
    }
    output.push_str(&text[cursor..]);
    Ok(output)
}

fn invalid_result() -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, "Invalid spelling result")
}

fn tokens(text: &str) -> Vec<SpellingToken> {
    let mut result = Vec::new();
    let mut in_code = false;
    for (start, chunk) in text.split_whitespace().scan(0, |cursor, chunk| {
        let start = *cursor + text[*cursor..].find(chunk)?;
        *cursor = start + chunk.len();
        Some((start, chunk))
    }) {
        let ticks = chunk.bytes().filter(|&c| c == b'`').count();
        if ticks > 0 {
            if ticks % 2 != 0 {
                in_code = !in_code;
            }
            continue;
        }
        if in_code || chunk.contains("()") || technical(chunk) {
            continue;
        }
        let mut cursor = None;
        for (offset, character) in chunk.char_indices().chain([(chunk.len(), ' ')]) {
            if character.is_alphabetic() {
                cursor.get_or_insert(offset);
                continue;
            }
            if "'’ʼ-‐‑".contains(character)
                && cursor.is_some()
                && chunk[offset + character.len_utf8()..]
                    .chars()
                    .next()
                    .is_some_and(char::is_alphabetic)
            {
                continue;
            }
            if let Some(first) = cursor.take()
                && chunk[first..offset].chars().count() >= 2
            {
                let word = &chunk[first..offset];
                result.push(SpellingToken {
                    range: start + first..start + offset,
                    scripts: script(word),
                    correction: common_correction(word),
                });
            }
        }
    }
    result
}

fn common_correction(word: &str) -> Option<CommonCorrection> {
    fn lookup(word: &str) -> Option<CommonCorrection> {
        let (language, replacement, needs_context) = match word {
            "suprize" | "suprise" | "surpise" => (0, "surprise", false),
            "будь-ласка" | "будьласка" => (2, "будь ласка", false),
            "безплатно" => (1, "бесплатно", true),
            "безплатный" => (1, "бесплатный", true),
            "безплатная" => (1, "бесплатная", true),
            "безплатные" => (1, "бесплатные", true),
            "доведется" => (2, "доведеться", true),
            "станется" => (2, "станеться", true),
            "вдастся" => (2, "вдасться", true),
            _ => return None,
        };
        Some(CommonCorrection {
            language,
            replacement,
            needs_context,
        })
    }

    lookup(word).or_else(|| {
        (word.starts_with(char::is_uppercase) || word.contains(['‐', '‑']))
            .then(|| lookup(&normalize(word)))
            .flatten()
    })
}

fn cyrillic_evidence(
    word: &str,
    ru: bool,
    uk: bool,
    vocabulary: &mut Vocabulary,
) -> Option<(usize, bool)> {
    let russian = word.contains(['ы', 'э', 'ё', 'ъ', 'Ы', 'Э', 'Ё', 'Ъ']);
    let ukrainian = word.contains(['і', 'ї', 'є', 'ґ', 'І', 'Ї', 'Є', 'Ґ']);
    match (russian, ukrainian) {
        (true, false) if ru => return Some((1, true)),
        (false, true) if uk => return Some((2, true)),
        (true, _) | (_, true) => return None,
        _ => {}
    }
    match (ru, uk) {
        (true, false) => Some((1, true)),
        (false, true) => Some((2, true)),
        (true, true) => {
            let scores = vocabulary.scores(word).ok()?;
            // Frequency evidence can corroborate a dictionary signal but cannot establish the language alone.
            if scores[1] >= CONFIDENT_FREQUENCY && scores[1].saturating_sub(scores[2]) >= 12 {
                Some((1, false))
            } else if scores[2] >= CONFIDENT_FREQUENCY && scores[2].saturating_sub(scores[1]) >= 12
            {
                Some((2, false))
            } else {
                None
            }
        }
        (false, false) => None,
    }
}

fn inside_quotes(text: &str, range: &Range<usize>) -> bool {
    let before = text[..range.start].rsplit('\n').next().unwrap_or_default();
    let after = text[range.end..].split('\n').next().unwrap_or_default();
    if before.ends_with(['\'', '"', '‘', '“', '„', '«'])
        && after.starts_with(['\'', '"', '’', '”', '“', '»'])
    {
        return true;
    }
    before.matches('"').count() % 2 == 1 && after.contains('"')
        || [('«', '»'), ('“', '”'), ('„', '“'), ('‘', '’')]
            .into_iter()
            .any(|(open, close)| before.rfind(open) > before.rfind(close) && after.contains(close))
}

fn script(word: &str) -> u8 {
    let mut mask = 0;
    for c in word.chars().filter(|c| c.is_alphabetic() && *c != 'ʼ') {
        if c.is_ascii_alphabetic() {
            mask |= 1;
        } else if ('\u{0400}'..='\u{052f}').contains(&c) {
            mask |= 6;
        } else {
            return 0;
        }
    }
    if mask == 7 { 0 } else { mask }
}

fn case_like(word: &str, replacement: &str) -> String {
    let mut chars = word.chars();
    if word
        .chars()
        .filter(|c| c.is_uppercase() || c.is_lowercase())
        .all(char::is_uppercase)
    {
        replacement.to_uppercase()
    } else if chars.next().is_some_and(char::is_uppercase) {
        let mut chars = replacement.chars();
        chars
            .next()
            .into_iter()
            .flat_map(char::to_uppercase)
            .chain(chars)
            .collect()
    } else {
        replacement.to_owned()
    }
}

fn inserts_letter(original: &[char], candidate: &str) -> bool {
    let mut typed = original.iter().copied().peekable();
    let mut inserted = false;
    for letter in candidate.chars() {
        if typed.peek() == Some(&letter) {
            typed.next();
        } else if !inserted && letter.is_alphabetic() {
            inserted = true;
        } else {
            return false;
        }
    }
    inserted && typed.next().is_none()
}

fn distance(first: &[char], second: &str) -> usize {
    let second: Vec<_> = second.chars().collect();
    let mut older = vec![0; second.len() + 1];
    let mut previous: Vec<_> = (0..=second.len()).collect();
    let mut current = vec![0; second.len() + 1];
    for (i, a) in first.iter().enumerate() {
        current[0] = i + 1;
        for (j, b) in second.iter().enumerate() {
            let mut cost = (previous[j + 1] + 1)
                .min(current[j] + 1)
                .min(previous[j] + usize::from(a != b));
            if i > 0 && j > 0 && *a == second[j - 1] && first[i - 1] == *b {
                cost = cost.min(older[j - 1] + 1);
            }
            current[j + 1] = cost;
        }
        std::mem::swap(&mut older, &mut previous);
        std::mem::swap(&mut previous, &mut current);
    }
    previous[second.len()]
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn quick_spelling_keeps_the_full_review_winner_for_common_typos() {
        let resources = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("resources");
        let mut engine = Spelling::new(resources.join("spelling"));
        let text = "This is a speling test with a mistke. Please send the new adress to me. We lit the latern.\nЭто проверка ошыбки в русском тексте.\nЦе перевірка помлки в українському тексті.";
        let personal = Dictionary::empty();
        let quick = engine.check_mode(text, &personal, None, true).unwrap();
        let full = engine.check(text, &personal, None).unwrap();
        let first_suggestions = |issues: &[Issue]| {
            issues
                .iter()
                .map(|issue| issue.suggestions.first().cloned())
                .collect::<Vec<_>>()
        };
        assert_eq!(first_suggestions(&quick), first_suggestions(&full));
        assert_eq!(
            first_suggestions(&quick),
            [
                "spelling",
                "mistake",
                "address",
                "lantern",
                "ошибки",
                "помилки"
            ]
            .map(|word| Some(word.to_owned()))
        );
        assert!(inserts_letter(&['a', 'd', 'r'], "addr"));
        assert!(inserts_letter(&['a', 'd', 'r'], "ador"));
        assert!(!inserts_letter(&['a', 'd', 'r'], "ad r"));
        assert_eq!(distance(&['a', 'b'], "ba"), 1);
        assert_eq!(distance(&['п', 'р', 'и'], "пр"), 1);
        assert_eq!(distance(&[], "abc"), 3);
    }

    #[test]
    fn common_repairs_respect_cyrillic_context_and_quotes() {
        let directory = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("resources/spelling");
        let mut engine = Spelling::new(directory);
        let personal = Dictionary::empty();
        let text = concat!(
            "It was a pleasant suprize.\n",
            "Они помогли нам безплатно.\n",
            "Зачини вікно, будь‑ласка.\n",
            "Нам доведется зачекати ще годину.\n",
            "У цьому місті послугу надають безплатно.\n",
            "Нам доведется подождать ещё полчаса.\n",
            "Они помогли нам «безплатно сегодня».\n",
            "доведется\n",
        );
        let issues = engine.check_mode(text, &personal, None, true).unwrap();
        let repairs: Vec<_> = issues
            .iter()
            .filter_map(|issue| Some((issue.word.as_str(), issue.suggestions.first()?.as_str())))
            .collect();
        assert_eq!(
            repairs,
            [
                ("suprize", "surprise"),
                ("безплатно", "бесплатно"),
                ("будь‑ласка", "будь ласка"),
                ("доведется", "доведеться"),
            ]
        );

        let profile = tempfile::tempdir().unwrap();
        let personal = Dictionary::new(profile.path());
        let term = crate::dictionary::Entry::new("suprize", LANGUAGES[0]).unwrap();
        personal
            .add(&[term], &mut Vocabulary::new(engine.vocabulary_path()))
            .unwrap();
        assert!(
            engine
                .check_mode("SUPRIZE", &personal, None, true)
                .unwrap()
                .is_empty()
        );
        personal.set_correction("suprize", "unexpected").unwrap();
        let customized = engine.check_mode("SUPRIZE", &personal, None, true).unwrap();
        assert_eq!(customized[0].suggestions, ["UNEXPECTED"]);
    }

    #[test]
    fn corrects_wrapped_prose_and_preserves_paths_and_code() {
        let directory = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("resources/spelling");
        let mut engine = Spelling::new(directory);
        let text = "🙂 (Speling) `Speling` index.ts — привт привiт";
        let issues = engine.check(text, &Dictionary::empty(), Some(0)).unwrap();
        assert_eq!(issues[0].word, "Speling");
        assert!(issues[0].suggestions.contains(&"Spelling".to_owned()));
        let en = issues[0]
            .suggestions
            .iter()
            .position(|word| word == "Spelling");
        let mut choices = vec![None; issues.len()];
        choices[0] = en;
        assert!(
            apply(text, &issues, &choices)
                .unwrap()
                .starts_with("🙂 (Spelling) `Speling` index.ts")
        );
        assert!(engine.dictionary(1).unwrap().check("привет"));
        assert!(engine.dictionary(2).unwrap().check("привіт"));
        assert_eq!(case_like("HELO-WORLD", "hello-world"), "HELLO-WORLD");
    }
}
