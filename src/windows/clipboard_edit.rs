use super::{Target, WinEdit, accessible, clipio, diagnostics, input};
use crate::{
    layout::{self, Language},
    text_tools,
};
use std::{
    cell::Cell,
    io, thread,
    time::{Duration, Instant},
};
use unicode_segmentation::UnicodeSegmentation;
use windows_sys::Win32::{
    Foundation::HWND,
    UI::{
        Input::KeyboardAndMouse::*,
        WindowsAndMessaging::{GA_ROOT, GetAncestor, GetWindowThreadProcessId},
    },
};

const CONTEXT: usize = 12;
const MAX_SELECTION: usize = super::accessible::MAX_UNITS;
const KEY_BATCH: usize = 256;

#[derive(Default)]
pub(super) struct Fallback {
    undo: Option<Edit>,
}

struct Edit {
    target: Target,
    sequence: usize,
    time: Instant,
    original: String,
    replacement: String,
    prefix: String,
}

pub(super) enum Command {
    Cycle,
    To(Language),
    Case,
    Transliterate,
    Automatic(String, Language),
    Undo,
}

impl Fallback {
    pub(super) fn has_undo(&self, target: Target, sequence: usize) -> bool {
        self.undo.as_ref().is_some_and(|edit| {
            edit.target == target
                && edit.sequence == sequence
                && edit.time.elapsed() < Duration::from_secs(120)
        })
    }

    pub(super) fn run(
        &mut self,
        owner: HWND,
        editor: &WinEdit,
        command: Command,
        source: Option<Language>,
        cycle: &[Language],
        validate: impl Fn() -> io::Result<()>,
        canceled: Option<std::sync::Arc<std::sync::atomic::AtomicBool>>,
        timing: &mut diagnostics::Timing,
    ) -> io::Result<Option<Language>> {
        let target = editor.target()?;
        let sequence = editor.sequence.unwrap_or_else(input::sequence);
        let undoing = matches!(command, Command::Undo);
        let undo = self.undo.as_ref().filter(|edit| {
            edit.target == target
                && edit.sequence == sequence
                && edit.time.elapsed() < Duration::from_secs(120)
        });
        if undoing && undo.is_none() {
            timing.applied(false);
            return Ok(None);
        }
        let deadline = Instant::now() + accessible::MODIFIER_RELEASE_TIMEOUT;
        while accessible::modifiers_down() {
            validate()?;
            if Instant::now() >= deadline {
                return Err(io::ErrorKind::TimedOut.into());
            }
            thread::sleep(Duration::from_millis(4));
        }
        // Restore temporary copies before the lease releases buffered input.
        let lease;
        let mut clipboard = clipio::TemporaryText::capture(owner)?;
        timing.phase("modifiers and clipboard snapshot");
        lease = input::SelectionLease::start(sequence, target, canceled)?;
        let mut range = Range {
            target,
            valid: &validate,
            lease: &lease,
            movement: Cell::new(0),
            collapse: false,
        };
        validate()?;
        if !lease.still_quiet() || !clipboard.unchanged() {
            return Err(interrupted());
        }
        let expected = if undoing {
            undo.as_ref().map(|edit| edit.replacement.as_str())
        } else {
            editor
                .recent
                .as_deref()
                .or_else(|| undo.as_ref().map(|edit| edit.replacement.as_str()))
        };
        let original = if let Some(expected) = expected {
            let width = width(expected)?;
            range.select_left(width)?;
            let copied = copy(&mut clipboard, &range)?;
            if !equivalent(&copied, expected) {
                return Err(interrupted());
            }
            copied
        } else {
            capture_selection(&mut range, &mut clipboard)?
        };
        let (replacement, language) = match command {
            Command::Undo => (undo.as_ref().expect("verified undo").original.clone(), None),
            Command::To(target) => (layout::convert_to(&original, target, source), Some(target)),
            Command::Cycle => {
                let from = layout::detect(&original, cycle, source).or(source);
                let Some(target) = layout::next_language(cycle, from) else {
                    return Ok(None);
                };
                (layout::convert_to(&original, target, source), Some(target))
            }
            Command::Case => (text_tools::cycle_case(&original), None),
            Command::Transliterate => (text_tools::transliterate(&original), None),
            Command::Automatic(mut replacement, target) => {
                let trailing = original
                    .chars()
                    .last()
                    .filter(|ch| matches!(ch, ' ' | '\u{a0}'))
                    .ok_or_else(interrupted)?;
                replacement.push(trailing);
                (replacement, Some(target))
            }
        };
        timing.phase("copy and conversion");
        if original == replacement {
            timing.applied(false);
            return Ok(None);
        }
        let replacement_width = width(&replacement)?;
        range.select_left(CONTEXT)?;
        let extended = copy(&mut clipboard, &range)?;
        let prefix = extended
            .strip_suffix(&original)
            .ok_or_else(interrupted)?
            .to_owned();
        if undoing && undo.as_ref().is_some_and(|edit| edit.prefix != prefix) {
            return Err(interrupted());
        }
        if !prefix.is_empty() {
            range.select_right(prefix.graphemes(true).count())?;
            if copy(&mut clipboard, &range)? != original {
                return Err(interrupted());
            }
        }
        if append_ambiguous(&prefix, &original) {
            return Err(interrupted());
        }
        validate()?;
        if !lease.still_quiet() || !clipboard.still_owns() || accessible::modifiers_down() {
            return Err(interrupted());
        }
        let payload = replacement.replace('\n', "\r\n");
        clipboard.publish(&payload.encode_utf16().collect::<Vec<_>>())?;
        lease.arm_focus();
        validate()?;
        if !clipboard.still_owns() || !lease.still_quiet() {
            return Err(interrupted());
        }
        accessible::send_paste(input::paste_tag())?;
        if !lease.wait_for_paste() {
            return Err(interrupted());
        }
        range.collapse = false;
        timing.phase("selection check and paste");
        // Recopy the replacement together with preceding context. This
        // detects editors that inserted the payload beside the old range.
        thread::sleep(Duration::from_millis(15));
        if !lease.still_quiet() || !clipboard.still_owns() {
            return Err(interrupted());
        }
        range.select_left(replacement_width + CONTEXT)?;
        let written = copy(&mut clipboard, &range)?;
        if written.strip_prefix(&prefix) != Some(replacement.as_str()) {
            return Err(io::Error::other("Editor did not confirm the replacement"));
        }
        range.finish()?;
        timing.phase("result verification");
        timing.applied(true);
        if undoing {
            self.undo = None;
        } else {
            self.undo = Some(Edit {
                target,
                sequence,
                time: Instant::now(),
                original,
                replacement,
                prefix,
            });
        }
        if let Err(error) = clipboard.restore() {
            eprintln!("Typlo Rust: restoring the clipboard after a fallback edit: {error}");
        }
        timing.phase("clipboard restoration");
        Ok(language)
    }
}

struct Range<'a> {
    target: Target,
    valid: &'a dyn Fn() -> io::Result<()>,
    lease: &'a input::SelectionLease,
    movement: Cell<usize>,
    collapse: bool,
}

impl Range<'_> {
    fn select_left(&mut self, count: usize) -> io::Result<()> {
        self.collapse = true;
        self.select(VK_LEFT, count)
    }

    fn select_right(&self, count: usize) -> io::Result<()> {
        self.select(VK_RIGHT, count)
    }

    fn select(&self, direction: u16, mut count: usize) -> io::Result<()> {
        let deadline = Instant::now() + Duration::from_secs(5);
        self.movement.set(self.movement.get().saturating_add(count));
        while count > 0 {
            if !self.lease.still_quiet() || Instant::now() >= deadline {
                return Err(interrupted());
            }
            let batch = count.min(KEY_BATCH);
            self.keys(Some(VK_SHIFT), direction, batch)?;
            count -= batch;
        }
        Ok(())
    }

    fn finish(&mut self) -> io::Result<()> {
        (self.valid)()?;
        input::send_selection_keys(None, VK_RIGHT, 1, true)?;
        self.collapse = false;
        Ok(())
    }

    fn keys(&self, modifier: Option<u16>, key: u16, count: usize) -> io::Result<()> {
        (self.valid)()?;
        input::send_selection_keys(modifier, key, count, false)
    }
}

fn capture_selection(
    range: &mut Range<'_>,
    clipboard: &mut clipio::TemporaryText,
) -> io::Result<String> {
    let selected =
        try_copy(clipboard, range, Duration::from_millis(400))?.ok_or_else(interrupted)?;
    let count = width(&selected)?;
    // A copy alone is insufficient: editors such as VS Code copy the current
    // line at a collapsed caret. Verify an actual movable selection endpoint.
    range.select_left(1)?;
    let left = try_copy(clipboard, range, Duration::from_millis(130))?;
    let mut direction = left
        .as_deref()
        .and_then(|text| endpoint(&selected, text, true));
    if left.as_deref() != Some(selected.as_str()) {
        range.select_right(1)?;
        if copy(clipboard, range)? != selected {
            return Err(interrupted());
        }
    }
    if direction.is_none() {
        range.select_right(1)?;
        let right = try_copy(clipboard, range, Duration::from_millis(130))?;
        direction = right
            .as_deref()
            .and_then(|text| endpoint(&selected, text, false));
        if right.as_deref() != Some(selected.as_str()) {
            range.select_left(1)?;
            if copy(clipboard, range)? != selected {
                return Err(interrupted());
            }
        }
    }
    match direction {
        Some(Direction::Forward) => {
            range.finish()?;
            range.select_left(count)?;
            if copy(clipboard, range)? != selected {
                return Err(interrupted());
            }
        }
        Some(Direction::Backward) => {}
        None => return Err(interrupted()),
    }
    Ok(selected)
}

#[derive(Debug, PartialEq)]
enum Direction {
    Forward,
    Backward,
}

fn endpoint(selected: &str, observed: &str, left: bool) -> Option<Direction> {
    let one = |text: &str| text.graphemes(true).count() == 1;
    // A two-grapheme implicit line could coincidentally match a one-grapheme
    // probe. Only use a shortened range as proof if it still has two graphemes.
    let shortened = || observed.graphemes(true).take(2).count() == 2;
    if left {
        if observed.strip_suffix(selected).is_some_and(one) {
            return Some(Direction::Backward);
        }
        if shortened() && selected.strip_prefix(observed).is_some_and(one) {
            return Some(Direction::Forward);
        }
    } else {
        if observed.strip_prefix(selected).is_some_and(one) {
            return Some(Direction::Forward);
        }
        if shortened() && selected.strip_suffix(observed).is_some_and(one) {
            return Some(Direction::Backward);
        }
    }
    None
}

fn append_ambiguous(prefix: &str, original: &str) -> bool {
    if prefix.graphemes(true).count() != CONTEXT {
        return false;
    }
    let tail = original
        .grapheme_indices(true)
        .rev()
        .nth(CONTEXT - 1)
        .map_or(original, |(start, _)| &original[start..]);
    prefix.ends_with(tail)
}

impl Drop for Range<'_> {
    fn drop(&mut self) {
        if self.collapse {
            let _ = self.finish();
        }
    }
}

fn copy(clipboard: &mut clipio::TemporaryText, range: &Range<'_>) -> io::Result<String> {
    let duration = if range.movement.get() > 1000 {
        Duration::from_secs(5)
    } else {
        Duration::from_millis(130)
    };
    try_copy(clipboard, range, duration)?.ok_or_else(interrupted)
}

fn try_copy(
    clipboard: &mut clipio::TemporaryText,
    range: &Range<'_>,
    duration: Duration,
) -> io::Result<Option<String>> {
    (range.valid)()?;
    if !range.lease.still_quiet() || !clipboard.unchanged() {
        return Err(interrupted());
    }
    range.movement.set(0);
    let before = clipio::sequence();
    range.keys(Some(VK_CONTROL), u16::from(b'C'), 1)?;
    let deadline = Instant::now() + duration;
    loop {
        (range.valid)()?;
        if !range.lease.still_quiet() {
            return Err(interrupted());
        }
        match clipboard.copied_text(before, |owner| copy_from_target(owner, range.target)) {
            Ok(Some(text)) => return Ok(Some(text)),
            Ok(None) => {}
            Err(error) if error.raw_os_error() == Some(5) => {}
            Err(error) => return Err(error),
        }
        if Instant::now() >= deadline {
            return Ok(None);
        }
        thread::sleep(Duration::from_millis(4));
    }
}

fn copy_from_target(owner: HWND, target: Target) -> bool {
    let mut source = 0;
    let mut edit = 0;
    unsafe {
        GetWindowThreadProcessId(owner, &mut source);
        GetWindowThreadProcessId(target.edit, &mut edit);
        source != 0
            && (source == target.pid
                || source == edit
                || GetAncestor(owner, GA_ROOT) == target.foreground)
    }
}

fn width(text: &str) -> io::Result<usize> {
    let units = text.encode_utf16().take(MAX_SELECTION + 1).count();
    if (1..=MAX_SELECTION).contains(&units) && !text.contains('\0') {
        Ok(text.graphemes(true).count())
    } else {
        Err(interrupted())
    }
}

fn equivalent(actual: &str, expected: &str) -> bool {
    actual
        .chars()
        .map(|c| if c == '\u{a0}' { ' ' } else { c })
        .eq(expected
            .chars()
            .map(|c| if c == '\u{a0}' { ' ' } else { c }))
}

fn interrupted() -> io::Error {
    io::ErrorKind::Interrupted.into()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn fallback_requires_the_exact_typed_range_including_space() {
        assert!(equivalent("ghbdtn\u{a0}", "ghbdtn "));
        assert!(!equivalent("ghbdtn", "ghbdtn "));
        assert!(!equivalent("keep ghbdtn ", "ghbdtn "));
        assert!(width("ghbdtn ").is_ok());
        assert_eq!(width("first\nsecond").unwrap(), 12);
        assert!(width(&"x".repeat(MAX_SELECTION + 1)).is_err());
    }

    #[test]
    fn long_multiline_ranges_count_graphemes_and_normalized_line_breaks() {
        let line = "hello 🧑‍💻 e\u{301}\n";
        assert_eq!(width(line).unwrap(), 10);
        assert_eq!(width(&line.repeat(200)).unwrap(), 2_000);
        assert!(width(&"🧑‍💻".repeat(15_000)).is_err());
    }

    #[test]
    fn endpoint_requires_selection_evidence_and_can_prove_document_boundaries() {
        assert_eq!(endpoint("ab\nc", "ab\n", true), Some(Direction::Forward));
        assert_eq!(endpoint("ab\nc", "b\nc", false), Some(Direction::Backward));
        assert_eq!(endpoint("🧑‍💻\na", "x🧑‍💻\na", true), Some(Direction::Backward));
        assert_eq!(endpoint("ab", "a", true), None);
        assert_eq!(endpoint("line copied at caret", "l", false), None);
    }

    #[test]
    fn identical_lookbehind_cannot_confirm_that_the_original_was_removed() {
        assert!(append_ambiguous("123456789012", "original 123456789012"));
        assert!(append_ambiguous("123456789012", "9012"));
        assert!(!append_ambiguous("123456789012", "ghbdtn"));
        assert!(!append_ambiguous("9012", "9012"));
    }
}
