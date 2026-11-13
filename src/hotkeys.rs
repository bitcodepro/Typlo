use serde::{Deserialize, Serialize};
use std::io;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum DrawingButton {
    Off,
    Left,
    Right,
    Middle,
}

impl DrawingButton {
    pub fn label(self) -> &'static str {
        use crate::locale::text as tr;
        match self {
            Self::Off => tr("hotkey.off", "Off"),
            Self::Left => tr("rust.phantom.leftMouse", "Left mouse button"),
            Self::Right => tr("rust.phantom.rightMouse", "Right mouse button"),
            Self::Middle => tr("rust.phantom.middleMouse", "Middle mouse button"),
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct DrawingShortcut {
    // Modifier bits match Chord and Win32's MOD_* constants.
    pub modifiers: u32,
    pub button: DrawingButton,
}

impl Default for DrawingShortcut {
    fn default() -> Self {
        Self {
            modifiers: 1,
            button: DrawingButton::Left,
        }
    }
}

impl DrawingShortcut {
    pub const MODIFIER_OPTIONS: [u32; 15] = [2, 1, 4, 8, 3, 6, 10, 5, 9, 12, 7, 11, 14, 13, 15];

    pub fn label() -> &'static str {
        crate::locale::text("rust.phantom.draw", "Draw on screen")
    }

    pub fn modifier_label(modifiers: u32) -> String {
        [(2, "Ctrl"), (1, "Alt"), (4, "Shift"), (8, "Win")]
            .into_iter()
            .filter_map(|(bit, name)| (modifiers & bit != 0).then_some(name))
            .collect::<Vec<_>>()
            .join("+")
    }

    pub(crate) fn validate(self) -> io::Result<()> {
        if !Self::MODIFIER_OPTIONS.contains(&self.modifiers) {
            return Err(io::Error::new(io::ErrorKind::InvalidInput, Self::label()));
        }
        Ok(())
    }

    #[cfg(windows)]
    pub(crate) fn matches(self, button: DrawingButton, modifiers: u32) -> bool {
        self.button != DrawingButton::Off && self.button == button && self.modifiers == modifiers
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(i32)]
pub enum Action {
    Convert = 1,
    Undo = 2,
    Switch = 3,
    Case = 4,
    Transliterate = 5,
    English = 6,
    Russian = 7,
    Ukrainian = 8,
    Spelling = 9,
    SpellingReview = 12,
    CaptureCopy = 13,
    CaptureFile = 14,
    CaptureEdit = 15,
    History = 16,
    AddWord = 18,
    Choices = 19,
    Tools = 20,
    AcceptChoice = 21,
    CancelChoice = 22,
    SnippetMenu = 23,
    ClearDrawing = 24,
}

impl Action {
    pub fn label(self) -> &'static str {
        use crate::locale::text as tr;
        match self {
            Self::Convert => tr("shortcuts.toggle", "Convert text layout"),
            Self::Undo => tr("shortcuts.undo", "Undo conversion"),
            Self::Switch => tr("hk.inputCycle", "Next input layout"),
            Self::Case => tr("shortcuts.case", "Change case"),
            Self::Transliterate => tr("shortcuts.translit", "Transliterate"),
            Self::English => tr("layout.en", "English"),
            Self::Russian => tr("layout.ru", "Russian"),
            Self::Ukrainian => tr("layout.uk", "Ukrainian"),
            Self::Spelling => tr("spell.quick", "Fix spelling"),
            Self::SpellingReview => tr("spell.title", "Review spelling"),
            Self::CaptureCopy => tr("capture.copy", "Copy screenshot"),
            Self::CaptureFile => tr("capture.save", "Save screenshot"),
            Self::CaptureEdit => tr("paint.openLatest", "Edit last screenshot"),
            Self::History => tr("settings.openHistory", "Open clipboard history"),
            Self::AddWord => tr("dictionary.title", "Add to dictionary"),
            Self::Choices => tr("tools.choices", "Layout choices"),
            Self::AcceptChoice => tr("tools.accept", "Apply layout choice"),
            Self::CancelChoice => tr("tools.cancel", "Cancel layout choices"),
            Self::Tools => tr("tools.title", "Typlo tools"),
            Self::SnippetMenu => tr("rust.snippet.menu", "Open snippet menu"),
            Self::ClearDrawing => tr("rust.phantom.clear", "Clear screen drawing"),
        }
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(default)]
pub struct Hotkeys {
    pub convert: String,
    pub undo: String,
    pub switch: String,
    pub case: String,
    pub transliterate: String,
    pub english: String,
    pub russian: String,
    pub ukrainian: String,
    pub spelling: String,
    pub spelling_review: String,
    pub capture_copy: String,
    pub capture_file: String,
    pub capture_edit: String,
    pub history: String,
    pub add_word: String,
    pub choices: String,
    pub accept_choice: String,
    pub cancel_choice: String,
    pub tools: String,
    #[serde(default)]
    pub snippet_menu: String,
    #[serde(default)]
    pub clear_drawing: String,
}

impl Default for Hotkeys {
    fn default() -> Self {
        Self {
            case: "Ctrl+Alt+Shift+C".into(),
            transliterate: "Ctrl+Alt+Shift+T".into(),
            switch: "Ctrl+Shift".into(),
            convert: "Ctrl+Alt+Shift+Space".into(),
            undo: "Ctrl+Alt+Shift+Z".into(),
            english: String::new(),
            russian: String::new(),
            ukrainian: String::new(),
            spelling: "Ctrl+Shift+L".into(),
            spelling_review: "Ctrl+Shift+P".into(),
            capture_copy: "Win+Shift+S".into(),
            capture_file: "Ctrl+Alt+Shift+F".into(),
            capture_edit: "Ctrl+Shift+[".into(),
            history: "Ctrl+Shift+V".into(),
            add_word: "Ctrl+Shift+K".into(),
            choices: "Ctrl+Alt+Shift+L".into(),
            accept_choice: "Ctrl+Alt+Shift+Enter".into(),
            cancel_choice: "Ctrl+Alt+Shift+Escape".into(),
            tools: "Ctrl+Alt+Shift+D".into(),
            snippet_menu: if cfg!(windows) {
                "Ctrl+Alt+Shift+S".into()
            } else {
                String::new()
            },
            clear_drawing: if cfg!(windows) {
                "Ctrl+Alt+Shift+X".into()
            } else {
                String::new()
            },
        }
    }
}

impl Hotkeys {
    pub fn entries_mut(&mut self) -> [(Action, &mut String); 21] {
        [
            (Action::Switch, &mut self.switch),
            (Action::Convert, &mut self.convert),
            (Action::Undo, &mut self.undo),
            (Action::Case, &mut self.case),
            (Action::Transliterate, &mut self.transliterate),
            (Action::English, &mut self.english),
            (Action::Russian, &mut self.russian),
            (Action::Ukrainian, &mut self.ukrainian),
            (Action::Spelling, &mut self.spelling),
            (Action::SpellingReview, &mut self.spelling_review),
            (Action::CaptureCopy, &mut self.capture_copy),
            (Action::CaptureFile, &mut self.capture_file),
            (Action::CaptureEdit, &mut self.capture_edit),
            (Action::History, &mut self.history),
            (Action::AddWord, &mut self.add_word),
            (Action::Choices, &mut self.choices),
            (Action::AcceptChoice, &mut self.accept_choice),
            (Action::CancelChoice, &mut self.cancel_choice),
            (Action::Tools, &mut self.tools),
            (Action::SnippetMenu, &mut self.snippet_menu),
            (Action::ClearDrawing, &mut self.clear_drawing),
        ]
    }

    pub fn bindings(&self) -> io::Result<Vec<(Action, Chord)>> {
        let mut output = Vec::new();
        for (action, shortcut) in [
            (Action::Convert, &self.convert),
            (Action::Undo, &self.undo),
            (Action::Switch, &self.switch),
            (Action::Case, &self.case),
            (Action::Transliterate, &self.transliterate),
            (Action::English, &self.english),
            (Action::Russian, &self.russian),
            (Action::Ukrainian, &self.ukrainian),
            (Action::Spelling, &self.spelling),
            (Action::SpellingReview, &self.spelling_review),
            (Action::CaptureCopy, &self.capture_copy),
            (Action::CaptureFile, &self.capture_file),
            (Action::CaptureEdit, &self.capture_edit),
            (Action::History, &self.history),
            (Action::AddWord, &self.add_word),
            (Action::Choices, &self.choices),
            (Action::AcceptChoice, &self.accept_choice),
            (Action::CancelChoice, &self.cancel_choice),
            (Action::Tools, &self.tools),
            (Action::SnippetMenu, &self.snippet_menu),
            (Action::ClearDrawing, &self.clear_drawing),
        ] {
            if shortcut.trim().is_empty() {
                continue;
            }
            let chord = Chord::parse(shortcut)
                .filter(|chord| chord.key != 0 || action == Action::Switch)
                .filter(|chord| !chord.screen_capture() || action == Action::CaptureCopy)
                .ok_or_else(|| {
                    io::Error::new(
                        io::ErrorKind::InvalidInput,
                        format!("{}: {shortcut}", action.label()),
                    )
                })?;
            if output.iter().any(|(_, existing)| *existing == chord) {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidInput,
                    format!("Сочетание используется дважды: {shortcut}"),
                ));
            }
            output.push((action, chord));
        }
        Ok(output)
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Chord {
    // These bits match RegisterHotKey; keeping parsing independent of Win32 also
    // lets the settings window validate saved profiles on another platform.
    pub modifiers: u32,
    pub key: u32,
}

impl Chord {
    pub fn screen_capture(self) -> bool {
        self.modifiers == 12 && self.key == u32::from(b'S')
    }

    pub fn intercepted(self) -> bool {
        self.key == 0 || self.screen_capture()
    }

    pub fn parse(input: &str) -> Option<Self> {
        let mut modifiers = 0;
        let mut key = None;
        for part in input
            .split('+')
            .map(|part| part.trim().to_ascii_uppercase())
        {
            let bit = match part.as_str() {
                "ALT" => 1,
                "CTRL" | "CONTROL" => 2,
                "SHIFT" => 4,
                "WIN" => 8,
                _ => {
                    if key.is_some() {
                        return None;
                    }
                    key = virtual_key(&part);
                    key?;
                    continue;
                }
            };
            if modifiers & bit != 0 {
                return None;
            }
            modifiers |= bit;
        }
        let key = key.or_else(|| (modifiers == 6).then_some(0))?;
        if modifiers == 0 && !(0x70..=0x87).contains(&key) {
            return None;
        }
        Some(Self { modifiers, key })
    }
}

fn virtual_key(name: &str) -> Option<u32> {
    if let [letter] = name.as_bytes()
        && letter.is_ascii_alphanumeric()
    {
        return Some(u32::from(*letter));
    }
    if let Some(index) = name
        .strip_prefix('F')
        .and_then(|value| value.parse::<u32>().ok())
        && (1..=24).contains(&index)
    {
        return Some(0x6f + index);
    }
    Some(match name {
        "SPACE" => 0x20,
        "TAB" => 0x09,
        "ENTER" => 0x0d,
        "ESC" | "ESCAPE" => 0x1b,
        "LEFT" => 0x25,
        "UP" => 0x26,
        "RIGHT" => 0x27,
        "DOWN" => 0x28,
        "HOME" => 0x24,
        "END" => 0x23,
        "INSERT" => 0x2d,
        "DELETE" => 0x2e,
        "[" => 0xdb,
        "]" => 0xdd,
        "," => 0xbc,
        "." => 0xbe,
        _ => return None,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn drawing_shortcut_requires_a_modifier_and_an_exact_match() {
        let drawing = DrawingShortcut::default();
        assert!(drawing.validate().is_ok());
        assert_eq!(DrawingShortcut::modifier_label(drawing.modifiers), "Alt");
        let invalid = DrawingShortcut {
            modifiers: 0,
            ..drawing
        };
        assert!(invalid.validate().is_err());
        #[cfg(windows)]
        {
            assert!(drawing.matches(DrawingButton::Left, 1));
            assert!(!drawing.matches(DrawingButton::Left, 0));
            assert!(!drawing.matches(DrawingButton::Left, 3));
            assert!(!drawing.matches(DrawingButton::Right, 1));
            let alternative = DrawingShortcut {
                modifiers: 1,
                button: DrawingButton::Right,
            };
            assert!(alternative.matches(DrawingButton::Right, 1));
            let disabled = DrawingShortcut {
                button: DrawingButton::Off,
                ..drawing
            };
            assert!(!disabled.matches(DrawingButton::Left, 1));
        }
    }

    #[test]
    fn canonicalizes_modifier_order_and_rejects_ambiguous_chords() {
        assert_eq!(
            Chord::parse("Ctrl + Alt+f9"),
            Chord::parse("ALT+CONTROL+F9")
        );
        for invalid in ["Ctrl+Ctrl+A", "Ctrl+A+B", "Ctrl+A+", "F25", "A", "Ctrl+Alt"] {
            assert!(Chord::parse(invalid).is_none(), "{invalid}");
        }
        assert_eq!(
            Chord::parse("Ctrl+Shift"),
            Some(Chord {
                modifiers: 6,
                key: 0
            })
        );
    }

    #[test]
    fn permits_disabling_a_command_but_rejects_duplicate_chords() {
        let mut settings = Hotkeys {
            case: String::new(),
            ..Default::default()
        };
        let bindings = settings.bindings().unwrap();
        assert!(!bindings.iter().any(|(action, _)| *action == Action::Case));
        for action in [Action::AcceptChoice, Action::CancelChoice] {
            assert!(bindings.iter().any(|(bound, _)| *bound == action));
        }
        settings.case = "SHIFT+ALT+CTRL+SPACE".into();
        assert!(settings.bindings().is_err());
    }
}
