use std::{
    ops::Range,
    time::{Duration, Instant},
};

const INPUT_AGE: Duration = Duration::from_secs(15);
const INPUT_LIMIT: usize = 128;

pub struct RecentInput<T> {
    text: String,
    target: Option<T>,
    sequence: usize,
    last_input: Option<Instant>,
}

#[derive(Clone, Copy)]
pub enum Input {
    Typed(char),
    Backspace,
    Reset,
}

impl<T: Eq> Default for RecentInput<T> {
    fn default() -> Self {
        Self {
            text: String::new(),
            target: None,
            sequence: 0,
            last_input: None,
        }
    }
}

impl<T: Eq> RecentInput<T> {
    pub fn push(&mut self, target: Option<T>, sequence: usize, input: Input, now: Instant) {
        if self.target != target
            || self.sequence.wrapping_add(1) != sequence
            || self
                .last_input
                .is_none_or(|last| now.saturating_duration_since(last) > INPUT_AGE)
        {
            self.text.clear();
        }
        self.sequence = sequence;
        self.target = target;
        self.last_input = Some(now);
        match input {
            Input::Typed(character) if self.target.is_some() && !character.is_control() => {
                self.text.push(character);
                if self.text.len() > INPUT_LIMIT {
                    let excess = self.text.len() - INPUT_LIMIT;
                    let first = self.text.ceil_char_boundary(excess);
                    self.text.drain(..first);
                }
            }
            Input::Backspace if self.target.is_some() => {
                self.text.pop();
            }
            _ => self.reset(),
        }
    }

    pub fn reset(&mut self) {
        self.text.clear();
        self.target = None;
        self.last_input = None;
    }

    pub fn text(&self, target: &T, sequence: usize, now: Instant) -> Option<&str> {
        (self.target.as_ref() == Some(target)
            && self.sequence == sequence
            && !self.text.is_empty()
            && self
                .last_input
                .is_some_and(|last| now.saturating_duration_since(last) <= INPUT_AGE))
        .then_some(self.text.as_str())
    }
}

pub fn last_word(typed: &str) -> Option<&str> {
    let trimmed = typed.trim_end_matches(' ');
    if trimmed.is_empty() {
        return None;
    }
    let start = trimmed.rfind(char::is_whitespace).map_or(0, |index| {
        index + trimmed[index..].chars().next().map_or(0, char::len_utf8)
    });
    Some(&typed[start..])
}

pub fn verified_suffix(document: &[u16], caret: usize, expected: &str) -> Option<Range<usize>> {
    let length = expected.encode_utf16().count();
    if length == 0 {
        return None;
    }
    let range = caret.checked_sub(length)?..caret;
    document
        .get(range.clone())?
        .iter()
        .copied()
        .eq(expected.encode_utf16())
        .then_some(range)
}

pub(crate) fn verified_recent_word(
    document: &[u16],
    caret: usize,
    expected: &str,
) -> Option<Range<usize>> {
    let word = expected.trim_end_matches(' ');
    let spaces = expected.len() - word.len();
    let word_end = caret.checked_sub(spaces)?;
    if !document
        .get(word_end..caret)?
        .iter()
        .all(|unit| matches!(unit, 0x0020 | 0x00a0))
    {
        return None;
    }
    let range = verified_suffix(document, word_end, word)?;
    Some(range.start..caret)
}

pub(crate) fn word_boundary(character: char) -> bool {
    character.is_whitespace() || "(［[{“\"«»”’.,!?;:".contains(character)
}

pub(crate) fn verified_auto_word(
    document: &[u16],
    caret: usize,
    token: &str,
) -> Option<(Range<usize>, char)> {
    let word_end = caret.checked_sub(1)?;
    let separator = match document.get(word_end)? {
        0x0020 => ' ',
        // Chromium contenteditable can store a trailing Space as a nonbreaking space.
        0x00a0 => '\u{a0}',
        _ => return None,
    };
    let word = verified_suffix(document, word_end, token)?;
    Some((word.start..caret, separator))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn gaps_focus_changes_and_stale_events_never_include_older_text() {
        let now = Instant::now();
        let mut recent = RecentInput::default();
        recent.push(Some(1), 1, Input::Typed('a'), now);
        recent.push(Some(1), 3, Input::Typed('б'), now);
        assert_eq!(recent.text(&1, 3, now), Some("б"));
        assert_eq!(recent.text(&1, 4, now), None);
        recent.push(Some(2), 4, Input::Typed(','), now);
        assert_eq!(recent.text(&1, 4, now), None);
        assert_eq!(recent.text(&2, 4, now), Some(","));
        assert_eq!(
            recent.text(&2, 4, now + INPUT_AGE + Duration::from_millis(1)),
            None
        );
    }

    #[test]
    fn checks_the_exact_utf16_suffix_and_keeps_trailing_spaces() {
        let doc: Vec<_> = "🙂оставь, ghb TAIL".encode_utf16().collect();
        let caret = "🙂оставь, ghb ".encode_utf16().count();
        let word = last_word("older ghb ").unwrap();
        assert_eq!(word, "ghb ");
        assert_eq!(verified_suffix(&doc, caret, word), Some(caret - 4..caret));
        assert_eq!(verified_suffix(&doc, caret, "rfr "), None);
        assert_eq!(last_word(","), Some(","));
        let doc: Vec<_> = "🙂rfr\u{a0} TAIL".encode_utf16().collect();
        assert_eq!(verified_recent_word(&doc, 7, "rfr  "), Some(2..7));
        assert_eq!(verified_recent_word(&doc, 7, "rfR  "), None);
        assert_eq!(verified_recent_word(&doc, 6, "rfr"), None);
        assert_eq!(verified_recent_word(&doc, 8, "rfr   "), None);
        assert_eq!(verified_recent_word(&doc, 5, "rfr"), Some(2..5));
    }
}
