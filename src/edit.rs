use crate::layout::{self, Language};
use crate::{
    snippets::Template,
    typing::{verified_auto_word, verified_suffix},
};
use std::{io, ops::Range};

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Snapshot<T> {
    pub target: T,
    pub document: Vec<u16>,
    pub selection: Range<usize>,
}

pub trait Editor {
    type Target: Clone + Eq;

    fn begin_operation(&mut self) {}

    /// The last verified write, including a write with a post-write caret error.
    fn take_confirmed_edit(&mut self) -> Option<Snapshot<Self::Target>> {
        None
    }

    fn snapshot(&mut self) -> io::Result<Snapshot<Self::Target>>;
    fn recent_range(&self, _: &Snapshot<Self::Target>) -> Option<Range<usize>> {
        None
    }
    fn move_caret(&mut self, _: &Snapshot<Self::Target>, _: usize) -> io::Result<()> {
        Err(io::Error::new(
            io::ErrorKind::Unsupported,
            "Editor cannot move the caret",
        ))
    }
    fn replace(
        &mut self,
        expected: &Snapshot<Self::Target>,
        range: Range<usize>,
        replacement: &[u16],
    ) -> io::Result<()>;
}

struct Undo<T> {
    after: Snapshot<T>,
    result: Range<usize>,
    original: Vec<u16>,
    result_layout: Option<Language>,
    automatic: bool,
}

pub struct Session<T> {
    undo: Option<Undo<T>>,
    suppressed: Option<Snapshot<T>>,
}

impl<T: Clone + Eq> Default for Session<T> {
    fn default() -> Self {
        Self {
            undo: None,
            suppressed: None,
        }
    }
}

impl<T: Clone + Eq> Session<T> {
    pub fn convert(
        &mut self,
        editor: &mut impl Editor<Target = T>,
        cycle: &[Language],
        current: Option<Language>,
    ) -> io::Result<Option<Language>> {
        self.transform(editor, |selected, previous| {
            let hint = previous.or(current);
            let source = layout::detect(selected, cycle, hint)
                .or_else(|| layout::detect(selected, &Language::ALL, hint))
                .or(hint)
                .or_else(|| cycle.first().copied())?;
            let target = match cycle {
                [target] if *target != source => Some(*target),
                _ => layout::next_language(cycle, Some(source)),
            }?;
            Some((layout::convert_to(selected, target, hint), Some(target)))
        })
    }

    pub fn convert_to(
        &mut self,
        editor: &mut impl Editor<Target = T>,
        target: Language,
        current: Option<Language>,
    ) -> io::Result<Option<Language>> {
        self.transform(editor, |text, previous| {
            Some((
                layout::convert_to(text, target, previous.or(current)),
                Some(target),
            ))
        })
    }

    pub fn transform(
        &mut self,
        editor: &mut impl Editor<Target = T>,
        transform: impl FnOnce(&str, Option<Language>) -> Option<(String, Option<Language>)>,
    ) -> io::Result<Option<Language>> {
        editor.begin_operation();
        let before = editor.snapshot()?;
        let (range, previous) = if !before.selection.is_empty() {
            (before.selection.clone(), None)
        } else if let Some(range) = editor.recent_range(&before) {
            (range, None)
        } else {
            let Some(last) = self.undo.as_ref().filter(|last| last.after == before) else {
                self.undo = None;
                return Ok(None);
            };
            (last.result.clone(), last.result_layout)
        };
        let original = before
            .document
            .get(range.clone())
            .ok_or_else(invalid_range)?;
        let selected = String::from_utf16(original).map_err(io::Error::other)?;
        let Some((result, result_layout)) = transform(&selected, previous) else {
            return Ok(None);
        };
        if result == selected {
            if let Some(last) = self.undo.as_mut().filter(|last| last.after == before) {
                last.result_layout = result_layout;
            }
            return Ok(None);
        }
        self.apply(editor, before, range, &result, result_layout, None, false)?;
        Ok(result_layout)
    }

    pub fn expand_snippet(
        &mut self,
        editor: &mut impl Editor<Target = T>,
        trigger: &str,
        template: &Template,
    ) -> io::Result<bool> {
        editor.begin_operation();
        let before = editor.snapshot()?;
        if !before.selection.is_empty() {
            return Ok(false);
        }
        let Some(range) = verified_suffix(&before.document, before.selection.end, trigger) else {
            return Ok(false);
        };
        self.apply(
            editor,
            before,
            range,
            &template.text,
            None,
            template.caret,
            false,
        )?;
        Ok(true)
    }

    #[cfg(windows)]
    pub(crate) fn insert_snippet(
        &mut self,
        editor: &mut impl Editor<Target = T>,
        expected: Snapshot<T>,
        template: &Template,
    ) -> io::Result<bool> {
        editor.begin_operation();
        if editor.snapshot()? != expected {
            return Ok(false);
        }
        let range = expected.selection.clone();
        self.apply(
            editor,
            expected,
            range,
            &template.text,
            None,
            template.caret,
            false,
        )?;
        Ok(true)
    }

    pub fn correct_word(
        &mut self,
        editor: &mut impl Editor<Target = T>,
        word: &str,
        suggest: impl FnOnce(&str) -> io::Result<Option<(String, Language)>>,
    ) -> io::Result<Option<Language>> {
        let Some(token) = word.strip_suffix(' ') else {
            return Ok(None);
        };
        if token.is_empty() || token.chars().any(char::is_whitespace) {
            return Ok(None);
        }
        editor.begin_operation();
        let before = editor.snapshot()?;
        if !before.selection.is_empty() || self.suppressed.as_ref() == Some(&before) {
            return Ok(None);
        }
        let Some((range, separator)) =
            verified_auto_word(&before.document, before.selection.end, token)
        else {
            return Ok(None);
        };
        if !boundary(
            range
                .start
                .checked_sub(1)
                .and_then(|i| before.document.get(i))
                .copied(),
        ) || !boundary(before.document.get(range.end).copied())
        {
            return Ok(None);
        }
        let Some((mut result, target)) = suggest(token)? else {
            return Ok(None);
        };
        result.push(separator);
        self.apply(editor, before, range, &result, Some(target), None, true)?;
        Ok(Some(target))
    }

    pub fn replace_selection(
        &mut self,
        editor: &mut impl Editor<Target = T>,
        expected: Snapshot<T>,
        text: &str,
    ) -> io::Result<bool> {
        let range = expected.selection.clone();
        self.replace_verified_range(editor, expected, range, text, None)
    }

    pub(crate) fn replace_verified_range(
        &mut self,
        editor: &mut impl Editor<Target = T>,
        expected: Snapshot<T>,
        range: Range<usize>,
        text: &str,
        layout: Option<Language>,
    ) -> io::Result<bool> {
        editor.begin_operation();
        if range.is_empty() || editor.snapshot()? != expected {
            return Ok(false);
        }
        if expected
            .document
            .get(range.clone())
            .ok_or_else(invalid_range)?
            .iter()
            .copied()
            .eq(text.encode_utf16())
        {
            return Ok(false);
        }
        self.apply(editor, expected, range, text, layout, None, false)?;
        Ok(true)
    }

    #[expect(
        clippy::too_many_arguments,
        reason = "Keep the existing edit transaction inputs explicit"
    )]
    fn apply(
        &mut self,
        editor: &mut impl Editor<Target = T>,
        before: Snapshot<T>,
        range: Range<usize>,
        result: &str,
        result_layout: Option<Language>,
        caret_offset: Option<usize>,
        automatic: bool,
    ) -> io::Result<()> {
        let original = before
            .document
            .get(range.clone())
            .ok_or_else(invalid_range)?
            .to_vec();
        let replacement: Vec<_> = result.encode_utf16().collect();
        if caret_offset.is_some_and(|offset| offset > replacement.len()) {
            return Err(invalid_range());
        }
        let mut expected_document = before.document.clone();
        expected_document.splice(range.clone(), replacement.iter().copied());
        let result_end = range.start + replacement.len();

        let (after, caret_error) = match editor.replace(&before, range.clone(), &replacement) {
            Ok(()) => (
                match editor.take_confirmed_edit() {
                    Some(after) => after,
                    None => editor.snapshot()?,
                },
                None,
            ),
            Err(error) => {
                let Some(snapshot) = editor.take_confirmed_edit() else {
                    return Err(error);
                };
                (snapshot, Some(error))
            }
        };
        if after.target != before.target || after.document != expected_document {
            return Err(io::Error::other("Editor did not confirm the replacement"));
        }
        self.undo = Some(Undo {
            after,
            result: range.start..result_end,
            original,
            result_layout,
            automatic,
        });
        if let Some(error) = caret_error {
            return Err(error);
        }
        if self
            .undo
            .as_ref()
            .is_some_and(|undo| undo.after.selection != (result_end..result_end))
        {
            return Err(io::Error::other(
                "Editor did not confirm the replacement caret",
            ));
        }
        if let Some(caret) = caret_offset.map(|offset| range.start + offset)
            && caret != result_end
        {
            let undo = self.undo.as_mut().expect("confirmed replacement");
            editor.move_caret(&undo.after, caret)?;
            let after = editor.snapshot()?;
            if after.target != before.target
                || after.document != expected_document
                || after.selection != (caret..caret)
            {
                return Err(io::Error::other("Editor did not confirm the snippet caret"));
            }
            undo.after = after;
        }
        Ok(())
    }

    pub fn undo(&mut self, editor: &mut impl Editor<Target = T>) -> io::Result<bool> {
        let Some(last) = self.undo.as_ref() else {
            return Ok(false);
        };
        editor.begin_operation();
        if editor.snapshot()? != last.after {
            return Ok(false);
        }
        let mut document = last.after.document.clone();
        document.splice(last.result.clone(), last.original.iter().copied());
        let after = match editor.replace(&last.after, last.result.clone(), &last.original) {
            Ok(()) => match editor.take_confirmed_edit() {
                Some(after) => after,
                None => editor.snapshot()?,
            },
            Err(error) => editor.take_confirmed_edit().ok_or(error)?,
        };
        if after.target != last.after.target || after.document != document {
            return Err(io::Error::other("Editor did not confirm Undo"));
        }
        if last.automatic {
            self.suppressed = Some(after);
        }
        self.undo = None;
        Ok(true)
    }
}

fn boundary(unit: Option<u16>) -> bool {
    unit.is_none_or(|unit| {
        char::from_u32(unit.into())
            .is_some_and(|c| c.is_whitespace() || "(［[{\"«»”’.,!?;:".contains(c))
    })
}

fn invalid_range() -> io::Error {
    io::Error::new(
        io::ErrorKind::InvalidData,
        "Editor returned an invalid UTF-16 selection",
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn confirmed_autocorrection_can_be_undone_after_a_caret_failure() {
        struct CaretFailure {
            model: Model,
            fail: bool,
            confirmed: Option<Snapshot<u8>>,
        }
        impl Editor for CaretFailure {
            type Target = u8;
            fn snapshot(&mut self) -> io::Result<Snapshot<u8>> {
                self.model.snapshot()
            }
            fn take_confirmed_edit(&mut self) -> Option<Snapshot<u8>> {
                self.confirmed.take()
            }
            fn replace(
                &mut self,
                before: &Snapshot<u8>,
                range: Range<usize>,
                replacement: &[u16],
            ) -> io::Result<()> {
                self.model.replace(before, range, replacement)?;
                if std::mem::take(&mut self.fail) {
                    self.model.state.selection = 6..6;
                    self.confirmed = Some(self.model.state.clone());
                    return Err(io::Error::other("caret rejected"));
                }
                Ok(())
            }
        }
        let mut editor = CaretFailure {
            model: Model::new(),
            fail: true,
            confirmed: None,
        };
        editor.model.state.document = "ghbdtn ".encode_utf16().collect();
        editor.model.state.selection = 7..7;
        let mut session = Session::default();
        assert!(
            session
                .correct_word(&mut editor, "ghbdtn ", |_| Ok(Some((
                    "привет".into(),
                    Language::Ru
                ))))
                .is_err()
        );
        assert_eq!(editor.model.text(), "привет ");
        assert!(session.undo(&mut editor).unwrap());
        assert_eq!(editor.model.text(), "ghbdtn ");
        assert!(
            session
                .correct_word(&mut editor, "ghbdtn ", |_| panic!("Undo suppresses retry"))
                .unwrap()
                .is_none()
        );
    }

    #[cfg(windows)]
    #[test]
    fn picked_snippet_preserves_the_surroundings_and_refuses_a_moved_caret() {
        let mut editor = Model::new();
        let mut session = Session::default();
        let original = "before after";
        editor.state.document = original.encode_utf16().collect();
        editor.state.selection = 7..7;
        let captured = editor.state.clone();
        let template = Template::parse("🙂{cursor}\nbody ").unwrap();
        editor.state.selection = 0..0;
        assert!(
            !session
                .insert_snippet(&mut editor, captured.clone(), &template)
                .unwrap()
        );
        assert_eq!(editor.writes, 0);
        editor.state.selection = 7..7;
        assert!(
            session
                .insert_snippet(&mut editor, captured, &template)
                .unwrap()
        );
        assert_eq!(editor.text(), "before 🙂\r\nbody after");
        assert_eq!(editor.state.selection, 9..9);
        assert!(session.undo(&mut editor).unwrap());
        assert_eq!(editor.text(), original);
    }

    #[test]
    fn autocorrection_preserves_the_space_inserted_by_the_editor_and_undo() {
        for separator in [' ', '\u{a0}'] {
            let mut editor = Model::new();
            let mut session = Session::default();
            let original = format!("🙂 keep ghbdtn{separator} tail");
            let caret = format!("🙂 keep ghbdtn{separator}").encode_utf16().count();
            editor.state.document = original.encode_utf16().collect();
            editor.state.selection = caret..caret;
            let suggest = |token: &str| {
                assert_eq!(token, "ghbdtn");
                Ok(Some(("привет".into(), Language::Ru)))
            };
            assert_eq!(
                session
                    .correct_word(&mut editor, "ghbdtn ", suggest)
                    .unwrap(),
                Some(Language::Ru)
            );
            assert_eq!(editor.text(), format!("🙂 keep привет{separator} tail"));
            assert_eq!(editor.state.selection, caret..caret);
            assert!(session.undo(&mut editor).unwrap());
            assert_eq!(editor.text(), original);
            assert_eq!(
                session
                    .correct_word(&mut editor, "ghbdtn ", |_| panic!(
                        "Undo must suppress a repeat"
                    ))
                    .unwrap(),
                None
            );
        }
    }

    #[test]
    fn autocorrection_still_requires_the_exact_word_and_a_typed_space() {
        for text in [
            "ghbdtx\u{a0}",
            "xghbdtn\u{a0}",
            "ghbdtn\t",
            "ghbdtn\u{202f}",
        ] {
            let mut editor = Model::new();
            let mut session = Session::default();
            editor.state.document = text.encode_utf16().collect();
            let caret = editor.state.document.len();
            editor.state.selection = caret..caret;
            assert_eq!(
                session
                    .correct_word(&mut editor, "ghbdtn ", |_| panic!(
                        "The target was not verified"
                    ))
                    .unwrap(),
                None
            );
            assert_eq!(editor.writes, 0);
        }
    }

    #[test]
    fn repeat_advances_the_layout_when_russian_and_ukrainian_text_is_identical() {
        let mut session = Session::default();
        let mut editor = Model::new();
        editor.state.document = "🙂ghbTAIL".encode_utf16().collect();
        editor.state.selection = 2..5;
        let current = Some(Language::En);
        assert_eq!(
            session
                .convert(&mut editor, &Language::ALL, current)
                .unwrap(),
            Some(Language::Ru)
        );
        assert_eq!(
            session
                .convert(&mut editor, &Language::ALL, current)
                .unwrap(),
            None
        );
        assert_eq!(editor.writes, 1);
        assert_eq!(
            session
                .convert(&mut editor, &Language::ALL, current)
                .unwrap(),
            Some(Language::En)
        );
        assert_eq!(editor.text(), "🙂ghbTAIL");
        assert!(session.undo(&mut editor).unwrap());
        assert_eq!(editor.text(), "🙂приTAIL");
    }

    #[test]
    fn explicit_layout_preserves_words_already_written_in_that_layout() {
        let mut session = Session::default();
        let mut editor = Model::new();
        editor.state.document = "ghbdtn Привет".encode_utf16().collect();
        editor.state.selection = 0..editor.state.document.len();
        assert_eq!(
            session
                .convert_to(&mut editor, Language::Ru, Some(Language::En))
                .unwrap(),
            Some(Language::Ru)
        );
        assert_eq!(editor.text(), "привет Привет");
    }

    struct Model {
        state: Snapshot<u8>,
        append: bool,
        reject_write: bool,
        writes: usize,
    }

    impl Model {
        fn new() -> Self {
            Self {
                state: Snapshot {
                    target: 1,
                    document: "🙂ghbdtnTAIL".encode_utf16().collect(),
                    selection: 2..8,
                },
                append: false,
                reject_write: false,
                writes: 0,
            }
        }

        fn text(&self) -> String {
            String::from_utf16(&self.state.document).unwrap()
        }
    }

    impl Editor for Model {
        type Target = u8;
        fn snapshot(&mut self) -> io::Result<Snapshot<u8>> {
            Ok(self.state.clone())
        }
        fn replace(
            &mut self,
            expected: &Snapshot<u8>,
            mut range: Range<usize>,
            replacement: &[u16],
        ) -> io::Result<()> {
            assert_eq!(expected, &self.state);
            if self.reject_write {
                return Err(io::Error::other("fixture refused the write"));
            }
            if self.append {
                range.start = range.end;
            }
            let caret = range.start + replacement.len();
            self.state
                .document
                .splice(range, replacement.iter().copied());
            self.state.selection = caret..caret;
            self.writes += 1;
            Ok(())
        }

        fn move_caret(&mut self, expected: &Snapshot<u8>, caret: usize) -> io::Result<()> {
            assert_eq!(expected, &self.state);
            assert!(caret <= self.state.document.len());
            self.state.selection = caret..caret;
            Ok(())
        }
    }

    #[test]
    fn undo_survives_failed_replacement_and_can_be_retried() {
        let mut session = Session::default();
        let mut editor = Model::new();
        session
            .convert_to(&mut editor, Language::Ru, Some(Language::En))
            .unwrap();
        assert_eq!(editor.text(), "🙂приветTAIL");
        editor.reject_write = true;
        assert!(
            session
                .convert_to(&mut editor, Language::En, Some(Language::Ru))
                .is_err()
        );
        assert!(session.undo(&mut editor).is_err());
        assert_eq!(editor.text(), "🙂приветTAIL");
        editor.reject_write = false;
        assert!(session.undo(&mut editor).unwrap());
        assert_eq!(editor.text(), "🙂ghbdtnTAIL");
    }

    #[test]
    fn converts_only_the_selection_and_undo_restores_exactly() {
        let mut session = Session::default();
        let mut editor = Model::new();
        assert_eq!(
            session
                .convert(&mut editor, &Language::ALL, Some(Language::En))
                .unwrap(),
            Some(Language::Ru)
        );
        assert_eq!(editor.text(), "🙂приветTAIL");
        assert!(session.undo(&mut editor).unwrap());
        assert_eq!(editor.text(), "🙂ghbdtnTAIL");
    }

    #[test]
    fn does_not_record_appended_text_or_retry_the_write() {
        let mut session = Session::default();
        let mut editor = Model::new();
        editor.append = true;
        assert!(session.convert(&mut editor, &Language::ALL, None).is_err());
        assert_eq!(editor.writes, 1);
        assert!(!session.undo(&mut editor).unwrap());
    }

    #[test]
    fn moved_focus_or_caret_invalidates_undo() {
        for moved_focus in [false, true] {
            let mut session = Session::default();
            let mut editor = Model::new();
            session.convert(&mut editor, &Language::ALL, None).unwrap();
            if moved_focus {
                editor.state.target = 2;
            } else {
                editor.state.selection = 0..0;
            }
            assert!(!session.undo(&mut editor).unwrap());
            assert_eq!(editor.writes, 1);
        }
    }
}
