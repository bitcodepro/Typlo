use super::{
    Target, WinEdit, accessible::Command, active_language, clipio, diagnostics, input,
    snippet_menu, switch_layout,
};
use crate::{
    dictionary::Dictionary,
    edit::Editor,
    layout::{self, Language},
    lexicon::{Decision, Vocabulary},
};
use std::{
    io,
    ops::Range,
    time::{Duration, Instant},
};
use windows_sys::Win32::{
    Foundation::HWND,
    UI::WindowsAndMessaging::{GetClassNameW, GetWindowThreadProcessId},
};

const GET_LENGTH: u32 = 2006;
const GET_CHAR: u32 = 2007;
const GET_CARET: u32 = 2008;
const GET_ANCHOR: u32 = 2009;
const GET_EOL_MODE: u32 = 2030;
const GET_CODE_PAGE: u32 = 2137;
const GET_READ_ONLY: u32 = 2140;
const SET_SELECTION: u32 = 2160;
const PASTE: u32 = 2179;
const GET_DOCUMENT: u32 = 2357;
const POSITION_BEFORE: u32 = 2417;
const POSITION_AFTER: u32 = 2418;
const COPY_RANGE: u32 = 2419;
const GET_SELECTION_MODE: u32 = 2423;
const GET_PASTE_CONVERT_ENDINGS: u32 = 2468;
const GET_SELECTIONS: u32 = 2570;
const GET_CARET_VIRTUAL_SPACE: u32 = 2581;
const GET_ANCHOR_VIRTUAL_SPACE: u32 = 2583;
const POINT_X_FROM_POSITION: u32 = 2164;
const POINT_Y_FROM_POSITION: u32 = 2165;
const TEXT_HEIGHT: u32 = 2279;
const MAX_RANGE: usize = 60_000;

pub(super) fn supports(target: Target, command: &Command) -> bool {
    if !matches!(
        command,
        Command::Convert
            | Command::To(_)
            | Command::Undo
            | Command::Typing {
                snippet: None,
                word: Some(_),
                ..
            }
    ) {
        return false;
    }
    is_editor(target)
}

pub(super) fn is_editor(target: Target) -> bool {
    let mut class = [0u16; 16];
    let len = unsafe { GetClassNameW(target.edit, class.as_mut_ptr(), class.len() as i32) };
    len == 9 && class[..9].iter().copied().eq("Scintilla".encode_utf16())
}

pub(super) struct SnippetSelection {
    state: State,
    surrounding: String,
}

fn snippet_view(target: Target, sequence: usize) -> View {
    let mut editor = WinEdit {
        expected: Some(target),
        sequence: Some(sequence),
        ..Default::default()
    };
    editor.begin_operation();
    View { editor, target }
}

pub(super) fn capture_snippet(
    owner: HWND,
    target: Target,
    sequence: usize,
) -> io::Result<(
    SnippetSelection,
    Option<windows_sys::Win32::Foundation::RECT>,
)> {
    let view = snippet_view(target, sequence);
    let state = view.state()?;
    let mut clipboard = Clipboard { owner, saved: None };
    let surrounding = view.snippet_context(&state, &mut clipboard)?;
    let anchor = view.snippet_anchor(&state);
    if view.state()? != state {
        return Err(interrupted());
    }
    Ok((SnippetSelection { state, surrounding }, anchor))
}

pub(super) fn insert_snippet(
    owner: HWND,
    target: Target,
    sequence: usize,
    captured: SnippetSelection,
    template: &crate::snippets::Template,
) -> io::Result<()> {
    let view = snippet_view(target, sequence);
    let before = view.state()?;
    if before != captured.state {
        return Err(interrupted());
    }
    let lease = input::SelectionLease::start(sequence, target, None)?;
    let mut clipboard = Clipboard { owner, saved: None };
    if view.snippet_context(&before, &mut clipboard)? != captured.surrounding {
        return Err(interrupted());
    }
    let range = before.range();
    let original = view.read(&mut clipboard, range.clone(), &before)?;
    let mode = view.get(GET_EOL_MODE)?;
    let replacement = snippet_endings(&template.text, mode);
    let caret = template
        .caret
        .map(|offset| {
            String::from_utf16(
                &template
                    .text
                    .encode_utf16()
                    .take(offset)
                    .collect::<Vec<_>>(),
            )
            .map(|prefix| range.start + snippet_endings(&prefix, mode).len())
            .map_err(io::Error::other)
        })
        .transpose()?;
    let after = view.replace(
        &mut clipboard,
        &lease,
        &before,
        range.clone(),
        &original,
        &replacement,
    )?;
    if let Some(caret) = caret {
        if view.state()? != after {
            return Err(interrupted());
        }
        view.message(SET_SELECTION, caret, caret)?;
    }
    Ok(())
}

fn snippet_endings(text: &str, mode: usize) -> String {
    let newline = match mode {
        1 => "\r",
        2 => "\n",
        _ => "\r\n",
    };
    text.replace("\r\n", "\n")
        .replace('\r', "\n")
        .replace('\n', newline)
}

#[derive(Clone, Debug, PartialEq, Eq)]
struct State {
    document: usize,
    length: usize,
    anchor: usize,
    caret: usize,
}

impl State {
    fn range(&self) -> Range<usize> {
        self.anchor.min(self.caret)..self.anchor.max(self.caret)
    }
}

#[derive(Default)]
pub(super) struct Edits {
    undo: Option<Undo>,
}

struct Undo {
    target: Target,
    after: State,
    range: Range<usize>,
    original: String,
    replacement: String,
    layout: Option<Language>,
    time: Instant,
}

pub(super) struct Operation<'a> {
    pub queued: Instant,
    pub owner: HWND,
    pub target: Target,
    pub sequence: usize,
    pub command: &'a Command,
    pub canceled: Option<std::sync::Arc<std::sync::atomic::AtomicBool>>,
    pub cycle: &'a [Language],
    pub switch_layout: bool,
    pub recent: Option<&'a str>,
}

impl Edits {
    pub fn run(
        &mut self,
        job: Operation<'_>,
        vocabulary: Option<&mut Vocabulary>,
        dictionary: &Dictionary,
        report: &diagnostics::Shared,
    ) -> io::Result<()> {
        let mut timing =
            diagnostics::Timing::since(report, job.command.diagnostic_name(), job.queued);
        timing.field("Scintilla");
        timing.phase("worker queue");
        let had_undo = self.undo.is_some();
        let start = Instant::now();
        let source = active_language(job.target);
        let automatic = matches!(job.command, Command::Typing { .. });
        let mut decision = Decision::Unverified;
        let result = self.edit(
            &job,
            source,
            vocabulary,
            dictionary,
            &mut decision,
            &mut timing,
        );
        timing.finish(&result, |layout| {
            layout.is_some() || had_undo && self.undo.is_none()
        });
        timing.dictionary(decision);
        if automatic {
            diagnostics::automatic(
                report,
                start,
                match &result {
                    Ok(Some(_)) => Decision::Applied,
                    Ok(None) => decision,
                    Err(_) => Decision::Failed,
                },
                source,
                result.as_ref().ok().copied().flatten(),
            );
        }
        if let Some(layout) = result?
            && job.switch_layout
            && (WinEdit {
                expected: Some(job.target),
                sequence: Some(job.sequence),
                ..Default::default()
            })
            .target()
            .is_ok()
        {
            switch_layout(job.target, layout);
        }
        Ok(())
    }

    fn edit(
        &mut self,
        job: &Operation<'_>,
        source: Option<Language>,
        vocabulary: Option<&mut Vocabulary>,
        dictionary: &Dictionary,
        decision: &mut Decision,
        timing: &mut diagnostics::Timing,
    ) -> io::Result<Option<Language>> {
        let mut editor = WinEdit {
            expected: Some(job.target),
            sequence: Some(job.sequence),
            ..Default::default()
        };
        editor.begin_operation();
        let view = View {
            editor,
            target: job.target,
        };
        let before = view.state()?;
        timing.phase("verified Scintilla document and selection");
        // Keep the clipboard guard alive until verification and restore it
        // before replaying any input buffered during the write.
        let lease;
        let mut clipboard = Clipboard {
            owner: job.owner,
            saved: None,
        };
        let last = self.undo.as_ref().filter(|edit| {
            edit.target == job.target
                && edit.after == before
                && edit.time.elapsed() < Duration::from_secs(120)
        });
        let (range, original, replacement, language) = match job.command {
            Command::Undo => {
                let Some(last) = last else { return Ok(None) };
                (
                    last.range.clone(),
                    last.replacement.clone(),
                    last.original.clone(),
                    None,
                )
            }
            Command::Typing {
                word: Some(word),
                protect,
                ..
            } => {
                let (Some(source), Some(vocabulary)) = (source, vocabulary) else {
                    return Ok(None);
                };
                if !before.range().is_empty() {
                    return Ok(None);
                }
                let Some(range) = suffix_range(before.caret, word) else {
                    return Ok(None);
                };
                if view.read(&mut clipboard, range.clone(), &before)? != *word
                    || !view.word_boundaries(&mut clipboard, &range, &before)?
                {
                    return Ok(None);
                }
                let Some(token) = word.strip_suffix(' ') else {
                    return Ok(None);
                };
                let (suggestion, reason) =
                    vocabulary.decision(dictionary, token, source, job.cycle, *protect)?;
                timing.phase("dictionary");
                *decision = reason;
                let Some((mut corrected, language)) = suggestion else {
                    return Ok(None);
                };
                corrected.push(' ');
                (range, word.clone(), corrected, Some(language))
            }
            Command::Convert | Command::To(_) => {
                let mut range = before.range();
                let mut hint = source;
                if range.is_empty() {
                    if let Some(recent) = job.recent {
                        let Some(found) = suffix_range(before.caret, recent) else {
                            return Ok(None);
                        };
                        if view.read(&mut clipboard, found.clone(), &before)? != recent {
                            return Ok(None);
                        }
                        range = found;
                    } else if let Some(last) = last {
                        range = last.range.clone();
                        hint = last.layout.or(hint);
                    } else {
                        return Ok(None);
                    }
                }
                let original = view.read(&mut clipboard, range.clone(), &before)?;
                if before.range().is_empty()
                    && job.recent.is_none()
                    && last.is_some_and(|edit| edit.replacement != original)
                {
                    return Err(interrupted());
                }
                let language = match job.command {
                    Command::To(target) => *target,
                    _ => {
                        let from = layout::detect(&original, job.cycle, hint)
                            .or_else(|| layout::detect(&original, &Language::ALL, hint))
                            .or(hint);
                        let target = match job.cycle {
                            [target] if Some(*target) != from => Some(*target),
                            _ => layout::next_language(job.cycle, from),
                        };
                        let Some(target) = target else {
                            return Ok(None);
                        };
                        target
                    }
                };
                let replacement = layout::convert_to(&original, language, hint);
                (range, original, replacement, Some(language))
            }
            _ => return Ok(None),
        };
        timing.phase("range capture and conversion");
        if original == replacement || replacement.is_empty() {
            return Ok(None);
        }
        lease = input::SelectionLease::start(job.sequence, job.target, job.canceled.clone())?;
        let after = view.replace(
            &mut clipboard,
            &lease,
            &before,
            range.clone(),
            &original,
            &replacement,
        )?;
        if matches!(job.command, Command::Undo) {
            self.undo = None;
        } else {
            self.undo = Some(Undo {
                target: job.target,
                range: range.start..after.caret,
                after,
                original,
                replacement,
                layout: language,
                time: Instant::now(),
            });
        }
        timing.phase("verified Scintilla replacement");
        Ok(language)
    }
}

struct Clipboard {
    owner: HWND,
    saved: Option<clipio::TemporaryText>,
}

impl Clipboard {
    fn access(&mut self) -> io::Result<&mut clipio::TemporaryText> {
        if self.saved.is_none() {
            self.saved = Some(clipio::TemporaryText::capture(self.owner)?);
        }
        Ok(self.saved.as_mut().expect("clipboard snapshot initialized"))
    }
}

struct View {
    editor: WinEdit,
    target: Target,
}

impl View {
    fn snippet_context(&self, state: &State, clipboard: &mut Clipboard) -> io::Result<String> {
        let range = state.range();
        let start = self.message(POSITION_BEFORE, range.start, 0)?;
        let end = self.message(POSITION_AFTER, range.end, 0)?;
        self.read(clipboard, start..end, state)
    }

    fn snippet_anchor(&self, state: &State) -> Option<windows_sys::Win32::Foundation::RECT> {
        use windows_sys::Win32::{
            Foundation::{POINT, RECT},
            Graphics::Gdi::ClientToScreen,
        };
        let _dpi = snippet_menu::DpiScope::new();
        let mut point = POINT {
            x: self.message(POINT_X_FROM_POSITION, 0, state.caret).ok()? as i32,
            y: self.message(POINT_Y_FROM_POSITION, 0, state.caret).ok()? as i32,
        };
        let height = self.get(TEXT_HEIGHT).ok()? as i32;
        if point.x < 0
            || point.y < 0
            || height <= 0
            || unsafe { ClientToScreen(self.target.edit, &mut point) } == 0
        {
            return None;
        }
        Some(RECT {
            left: point.x,
            right: point.x + 1,
            top: point.y,
            bottom: point.y + height,
        })
    }
    fn message(&self, code: u32, first: usize, second: usize) -> io::Result<usize> {
        self.editor
            .send(self.target.edit, code, first, second as isize)
    }

    fn get(&self, code: u32) -> io::Result<usize> {
        self.message(code, 0, 0)
    }

    fn state(&self) -> io::Result<State> {
        self.editor.target()?;
        if self.get(GET_READ_ONLY)? != 0
            || self.get(GET_CODE_PAGE)? != 65001
            || self.get(GET_SELECTIONS)? != 1
            || self.get(GET_SELECTION_MODE)? != 0
            || self.get(GET_CARET_VIRTUAL_SPACE)? != 0
            || self.get(GET_ANCHOR_VIRTUAL_SPACE)? != 0
        {
            return Err(io::Error::new(
                io::ErrorKind::Unsupported,
                "Notepad++ needs one writable selection in a UTF-8 document",
            ));
        }
        let state = State {
            document: self.get(GET_DOCUMENT)?,
            length: self.get(GET_LENGTH)?,
            anchor: self.get(GET_ANCHOR)?,
            caret: self.get(GET_CARET)?,
        };
        if state.document == 0 || state.range().end > state.length {
            return Err(interrupted());
        }
        self.editor.target()?;
        Ok(state)
    }

    fn read(
        &self,
        clipboard: &mut Clipboard,
        range: Range<usize>,
        state: &State,
    ) -> io::Result<String> {
        if range.start > range.end || range.end > state.length || range.len() > MAX_RANGE {
            return Err(io::Error::new(
                io::ErrorKind::Unsupported,
                "Notepad++ selection exceeds the supported size",
            ));
        }
        if range.len() <= 128 {
            let bytes = range
                .map(|offset| self.message(GET_CHAR, offset, 0).map(|byte| byte as u8))
                .collect::<io::Result<Vec<_>>>()?;
            return String::from_utf8(bytes).map_err(io::Error::other);
        }
        let saved = clipboard.access()?;
        if !saved.unchanged() || self.state()? != *state {
            return Err(interrupted());
        }
        let sequence = clipio::sequence();
        self.message(COPY_RANGE, range.start, range.end)?;
        let text = saved
            .copied_text_raw(sequence, |owner| {
                let mut pid = 0;
                unsafe {
                    GetWindowThreadProcessId(owner, &mut pid);
                }
                pid != 0 && pid == self.target.pid
            })?
            .ok_or_else(interrupted)?;
        if text.len() != range.len() || self.state()? != *state {
            return Err(interrupted());
        }
        Ok(text)
    }

    fn word_boundaries(
        &self,
        clipboard: &mut Clipboard,
        range: &Range<usize>,
        state: &State,
    ) -> io::Result<bool> {
        let start = self.message(POSITION_BEFORE, range.start, 0)?;
        let end = self.message(POSITION_AFTER, range.end, 0)?;
        let left = self.read(clipboard, start..range.start, state)?;
        let right = self.read(clipboard, range.end..end, state)?;
        Ok(left.chars().all(boundary) && right.chars().all(boundary))
    }

    fn replace(
        &self,
        clipboard: &mut Clipboard,
        lease: &input::SelectionLease,
        before: &State,
        range: Range<usize>,
        original: &str,
        replacement: &str,
    ) -> io::Result<State> {
        if replacement.contains('\0')
            || replacement.len() > MAX_RANGE
            || self.get(GET_PASTE_CONVERT_ENDINGS)? != 0
                && !preserves_endings(replacement, self.get(GET_EOL_MODE)?)
        {
            return Err(io::Error::new(
                io::ErrorKind::Unsupported,
                "Replacement would change the document's line endings or exceed its size limit",
            ));
        }
        if self.state()? != *before
            || self.read(clipboard, range.clone(), before)? != original
            || !lease.still_quiet()
        {
            return Err(interrupted());
        }
        let end = range.start + replacement.len();
        let expected = State {
            document: before.document,
            length: before.length - range.len() + replacement.len(),
            caret: end,
            anchor: end,
        };
        let selected = if before.range() == range {
            before.clone()
        } else {
            State {
                anchor: range.start,
                caret: range.end,
                ..before.clone()
            }
        };
        let saved = clipboard.access()?;
        if !saved.unchanged() || !lease.still_quiet() || self.state()? != *before {
            return Err(interrupted());
        }
        saved.publish(&replacement.encode_utf16().collect::<Vec<_>>())?;
        if selected != *before {
            self.message(SET_SELECTION, selected.anchor, selected.caret)?;
        }
        lease.arm_focus();
        let ready = self.state().is_ok_and(|state| state == selected)
            && saved.still_owns()
            && lease.begin_native_commit();
        if !ready {
            if selected != *before && self.state().is_ok_and(|state| state == selected) {
                let _ = self.message(SET_SELECTION, before.anchor, before.caret);
            }
            return Err(interrupted());
        }
        self.message(PASTE, 0, 0)?;
        if self.state()? != expected
            || self.read(clipboard, range.start..end, &expected)? != replacement
        {
            return Err(io::Error::other(
                "Notepad++ did not confirm the replacement",
            ));
        }
        Ok(expected)
    }
}

fn interrupted() -> io::Error {
    io::Error::new(
        io::ErrorKind::Interrupted,
        "Notepad++ text or focus changed",
    )
}

fn boundary(value: char) -> bool {
    value.is_whitespace() || "(［[{\"«»”’.,!?;:".contains(value)
}

fn suffix_range(caret: usize, text: &str) -> Option<Range<usize>> {
    (!text.is_empty()).then_some(caret.checked_sub(text.len())?..caret)
}

fn preserves_endings(text: &str, mode: usize) -> bool {
    let mut bytes = text.bytes().peekable();
    while let Some(byte) = bytes.next() {
        match byte {
            b'\r' if bytes.peek() == Some(&b'\n') => {
                bytes.next();
                if mode != 0 {
                    return false;
                }
            }
            b'\r' if mode != 1 => return false,
            b'\n' if mode != 2 => return false,
            _ => {}
        }
    }
    true
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn scintilla_offsets_are_utf8_and_eol_policy_preserves_text() {
        assert_eq!(suffix_range("🙂 ghbdtn ".len(), "ghbdtn "), Some(5..12));
        assert!(suffix_range(2, "при").is_none());
        assert!(preserves_endings("hi\r\nthere", 0));
        assert!(preserves_endings("hi\nthere", 2));
        assert!(!preserves_endings("hi\r\nthere\n", 0));
    }
}
