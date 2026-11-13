use super::{
    Target, WinEdit, active_language, clipio::TemporaryText, diagnostics, input, switch_layout,
};
use crate::{
    dictionary::Dictionary,
    edit::{Editor, Session, Snapshot},
    hotkeys::Action,
    layout::Language,
    lexicon::{Decision, Vocabulary},
    snippets::Template,
    text_tools, typing,
};
use std::{
    io,
    ops::Range,
    path::PathBuf,
    ptr::null_mut,
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
        mpsc::{self, SyncSender},
    },
    thread,
    time::{Duration, Instant},
};
use windows::Win32::{
    System::{
        Com::{
            CLSCTX_INPROC_SERVER, COINIT_MULTITHREADED, CoCreateInstance, CoInitializeEx,
            CoUninitialize, SAFEARRAY,
        },
        Ole::{
            SafeArrayAccessData, SafeArrayDestroy, SafeArrayGetDim, SafeArrayGetElemsize,
            SafeArrayGetLBound, SafeArrayGetUBound, SafeArrayUnaccessData,
        },
        Variant::{VT_BOOL, VariantClear},
    },
    UI::Accessibility::*,
};
use windows_sys::Win32::{Foundation::HWND, UI::Input::KeyboardAndMouse::*};

pub(super) const MAX_UNITS: usize = 60_000;

pub(super) enum Command {
    Convert,
    To(Language),
    Case,
    Transliterate,
    Undo,
    Spelling {
        review: bool,
    },
    ApplySpelling {
        capture: Captured,
        result: String,
        review: bool,
    },
    Tools(Action),
    PrepareSnippetMenu {
        snippets: Vec<crate::snippets::Snippet>,
        can_insert: bool,
    },
    InsertMenuSnippet {
        capture: Captured,
        template: Template,
    },
    InsertScintillaSnippet {
        capture: super::scintilla::SnippetSelection,
        template: Template,
    },
    ApplyChoice {
        capture: Captured,
        range: Range<usize>,
        result: String,
        language: Language,
    },
    Typing {
        snippet: Option<(String, Template)>,
        word: Option<String>,
        protect: bool,
    },
}

pub(super) struct Request {
    pub target: Target,
    pub sequence: usize,
    pub command: Command,
    pub cycle: Vec<Language>,
    pub switch_layout: bool,
    pub recent: Option<String>,
}

struct Job {
    queued: Instant,
    foreground: usize,
    edit: usize,
    thread: u32,
    pid: u32,
    sequence: usize,
    command: Command,
    cycle: Vec<Language>,
    switch_layout: bool,
    recent: Option<String>,
    canceled: Option<Arc<AtomicBool>>,
    reply: Option<SyncSender<Job>>,
}

pub(super) struct Worker(
    SyncSender<Job>,
    Option<Arc<AtomicBool>>,
    thread::JoinHandle<()>,
    Option<Arc<AtomicBool>>,
);

impl Command {
    pub(super) fn diagnostic_name(&self) -> &'static str {
        match self {
            Self::Typing { .. } => "Auto-correction",
            Self::Convert | Self::To(_) => "Manual conversion",
            Self::Undo => "Undo",
            _ => "Other edits",
        }
    }
}

pub(super) struct Captured {
    runtime: Vec<i32>,
    provider: i32,
    document: Vec<u16>,
    selection: Range<usize>,
}

impl Worker {
    pub fn start(owner: HWND, profile: PathBuf, report: diagnostics::Shared) -> io::Result<Self> {
        let owner = owner as usize;
        let (sender, jobs) = mpsc::sync_channel::<Job>(8);
        let handle = thread::Builder::new()
            .name("typlo-accessibility".into())
            .spawn(move || {
                let apartment = ComApartment::initialize().ok();
                let mut automation = None;
                let mut session = Session::default();
                let mut fallback = super::clipboard_edit::Fallback::default();
                let mut scintilla = super::scintilla::Edits::default();
                let mut vocabulary = Vocabulary::installed().ok();
                let dictionary = Dictionary::new(&profile);
                while let Ok(job) = jobs.recv() {
                    if (job.sequence != input::sequence()
                        || job
                            .canceled
                            .as_ref()
                            .is_some_and(|flag| flag.load(Ordering::Acquire)))
                        && !matches!(job.command, Command::ApplySpelling { review: true, .. })
                    {
                        let mut timing =
                            diagnostics::Timing::new(&report, job.command.diagnostic_name());
                        timing.field("Queue");
                        timing.finish(&Err::<(), _>(changed()), |_| false);
                        continue;
                    }
                    let processed = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
                        let target = Target {
                            foreground: job.foreground as HWND,
                            edit: job.edit as HWND,
                            thread: job.thread,
                            pid: job.pid,
                        };
                        if super::scintilla::supports(target, &job.command) {
                            let operation = super::scintilla::Operation {
                                queued: job.queued,
                                owner: owner as HWND,
                                target,
                                sequence: job.sequence,
                                command: &job.command,
                                canceled: job.canceled.clone(),
                                cycle: &job.cycle,
                                switch_layout: job.switch_layout,
                                recent: job.recent.as_deref(),
                            };
                            if let Err(error) =
                                scintilla.run(operation, vocabulary.as_mut(), &dictionary, &report)
                                && !matches!(
                                    error.kind(),
                                    io::ErrorKind::Interrupted | io::ErrorKind::Unsupported
                                )
                            {
                                crate::runtime_error!("Typlo Rust Notepad++: {error}");
                            }
                            return;
                        }
                        if apartment.is_none() {
                            return;
                        }
                        if automation.is_none() {
                            automation = new_automation().ok();
                        }
                        let Some(automation) = &automation else {
                            return;
                        };
                        if matches!(job.command, Command::Spelling { .. }) {
                            if let Err(error) =
                                start_spelling(automation, owner as HWND, job, profile.clone())
                                && !matches!(
                                    error.kind(),
                                    io::ErrorKind::Interrupted | io::ErrorKind::Unsupported
                                )
                            {
                                crate::runtime_error!("Typlo Rust spelling: {error}");
                            }
                            return;
                        }
                        if matches!(job.command, Command::Tools(_)) {
                            if let Err(error) = start_tools(
                                automation,
                                owner as HWND,
                                job,
                                profile.clone(),
                                vocabulary.as_mut(),
                                &dictionary,
                            ) && !matches!(
                                error.kind(),
                                io::ErrorKind::Interrupted | io::ErrorKind::Unsupported
                            ) {
                                crate::runtime_error!("Typlo Rust selection tools: {error}");
                            }
                            return;
                        }
                        if let Err(error) = run(
                            automation,
                            owner as HWND,
                            job,
                            &mut session,
                            &mut fallback,
                            vocabulary.as_mut(),
                            &dictionary,
                            &report,
                        ) && !matches!(
                            error.kind(),
                            io::ErrorKind::Unsupported | io::ErrorKind::Interrupted
                        ) {
                            crate::runtime_error!("Typlo Rust accessible editor: {error}");
                        }
                    }));
                    if processed.is_err() {
                        automation = None;
                        session = Session::default();
                        fallback = super::clipboard_edit::Fallback::default();
                        scintilla = super::scintilla::Edits::default();
                        vocabulary = Vocabulary::installed().ok();
                        crate::runtime_error!(
                            "Typlo Rust editor worker recovered after a failed command"
                        );
                    }
                }
                drop(automation);
                drop(apartment);
            })?;
        Ok(Self(sender, None, handle, None))
    }

    pub fn is_finished(&self) -> bool {
        self.2.is_finished()
    }

    pub fn enqueue(&mut self, request: Request) -> io::Result<()> {
        let interactive = matches!(
            request.command,
            Command::Spelling { .. } | Command::Tools(_)
        );
        if !matches!(request.command, Command::Typing { .. })
            && let Some(current) = self.1.take()
        {
            current.store(true, Ordering::Release);
        }
        let typing = matches!(request.command, Command::Typing { .. });
        let canceled = (interactive || typing).then(|| Arc::new(AtomicBool::new(false)));
        if typing {
            self.cancel_typing();
            self.3 = canceled.clone();
        } else {
            self.1 = canceled.clone().or(self.1.take());
        }
        self.0
            .try_send(Job {
                queued: Instant::now(),
                foreground: request.target.foreground as usize,
                edit: request.target.edit as usize,
                thread: request.target.thread,
                pid: request.target.pid,
                sequence: request.sequence,
                command: request.command,
                cycle: request.cycle,
                switch_layout: request.switch_layout,
                recent: request.recent,
                canceled,
                reply: interactive.then(|| self.0.clone()),
            })
            .map_err(|error| match error {
                mpsc::TrySendError::Full(_) => {
                    io::Error::new(io::ErrorKind::WouldBlock, "Editor queue is busy")
                }
                mpsc::TrySendError::Disconnected(_) => {
                    io::Error::new(io::ErrorKind::BrokenPipe, "Editor worker stopped")
                }
            })
    }

    pub fn cancel_typing(&mut self) {
        if let Some(flag) = self.3.take() {
            flag.store(true, Ordering::Release);
        }
    }
}

impl Drop for Worker {
    fn drop(&mut self) {
        self.cancel_typing();
        if let Some(canceled) = self.1.take() {
            canceled.store(true, Ordering::Release);
        }
    }
}

struct ComApartment;

impl ComApartment {
    fn initialize() -> windows::core::Result<Self> {
        unsafe {
            CoInitializeEx(None, COINIT_MULTITHREADED).ok()?;
        }
        Ok(Self)
    }
}

impl Drop for ComApartment {
    fn drop(&mut self) {
        unsafe {
            CoUninitialize();
        }
    }
}

fn new_automation() -> windows::core::Result<IUIAutomation2> {
    unsafe {
        let automation: IUIAutomation2 =
            CoCreateInstance(&CUIAutomation8, None, CLSCTX_INPROC_SERVER)?;
        automation.SetConnectionTimeout(180)?;
        automation.SetTransactionTimeout(180)?;
        Ok(automation)
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
struct Element {
    window: Target,
    runtime: Vec<i32>,
    provider: i32,
}

struct Accessible<'a> {
    automation: &'a IUIAutomation2,
    native: WinEdit,
    owner: HWND,
    lease: Option<input::SelectionLease>,
    first_snapshot: Option<Snapshot<Element>>,
    confirmed_edit: Option<Snapshot<Element>>,
    canceled: Option<Arc<AtomicBool>>,
    keyboard_word_selection: bool,
}

fn start_spelling(
    automation: &IUIAutomation2,
    owner: HWND,
    mut job: Job,
    profile: PathBuf,
) -> io::Result<()> {
    let Command::Spelling { review } = job.command else {
        unreachable!("only spelling requests start a spelling worker");
    };
    let canceled = job
        .canceled
        .clone()
        .expect("spelling requests are cancellable");
    if canceled.load(Ordering::Acquire) {
        return Ok(());
    }
    let target = Target {
        foreground: job.foreground as HWND,
        edit: job.edit as HWND,
        thread: job.thread,
        pid: job.pid,
    };
    let mut editor = Accessible {
        automation,
        owner,
        lease: None,
        first_snapshot: None,
        confirmed_edit: None,
        canceled: None,
        keyboard_word_selection: false,
        native: WinEdit {
            expected: Some(target),
            sequence: Some(job.sequence),
            ..Default::default()
        },
    };
    let snapshot = match editor.snapshot() {
        Ok(snapshot)
            if !snapshot.selection.is_empty()
                && snapshot.selection.len() <= crate::spelling::MAX_CHARACTERS =>
        {
            Some(snapshot)
        }
        Ok(_) => None,
        Err(error) if review && error.kind() == io::ErrorKind::Unsupported => None,
        Err(error) => return Err(error),
    };
    if !review && snapshot.is_none() {
        return Ok(());
    }
    let (capture, text) = if let Some(snapshot) = snapshot {
        let text = String::from_utf16(&snapshot.document[snapshot.selection.clone()])
            .map_err(io::Error::other)?;
        (
            Some(Captured {
                runtime: snapshot.target.runtime,
                provider: snapshot.target.provider,
                document: snapshot.document,
                selection: snapshot.selection,
            }),
            text,
        )
    } else {
        (None, String::new())
    };
    let origin = if capture.is_some() { job.foreground } else { 0 };
    let sender = job
        .reply
        .take()
        .expect("spelling requests include a return channel");
    thread::Builder::new()
        .name("typlo-accessible-spelling".into())
        .spawn(
            move || match crate::jobs::correct(&profile, text, origin, review, &canceled) {
                Ok(Some(result)) if !canceled.load(Ordering::Acquire) => {
                    if let Some(capture) = capture {
                        job.command = Command::ApplySpelling {
                            capture,
                            result,
                            review,
                        };
                        let _ = sender.try_send(job);
                    }
                }
                Err(error) if error.kind() != io::ErrorKind::Interrupted => {
                    crate::runtime_error!("Typlo Rust spelling: {error}")
                }
                _ => {}
            },
        )?;
    Ok(())
}

fn start_tools(
    automation: &IUIAutomation2,
    owner: HWND,
    mut job: Job,
    profile: PathBuf,
    vocabulary: Option<&mut Vocabulary>,
    dictionary: &Dictionary,
) -> io::Result<()> {
    use crate::selection_tools::Request;
    let Command::Tools(action) = job.command else {
        unreachable!("expected a tool request");
    };
    let canceled = job.canceled.clone().expect("tool requests are cancellable");
    if canceled.load(Ordering::Acquire) {
        return Ok(());
    }
    let target = Target {
        foreground: job.foreground as HWND,
        edit: job.edit as HWND,
        thread: job.thread,
        pid: job.pid,
    };
    let mut editor = Accessible {
        automation,
        owner,
        lease: None,
        first_snapshot: None,
        confirmed_edit: None,
        canceled: None,
        keyboard_word_selection: false,
        native: WinEdit {
            expected: Some(target),
            sequence: Some(job.sequence),
            recent: job.recent.take(),
            ..Default::default()
        },
    };
    let snapshot = editor.snapshot()?;
    let range = if action == Action::Choices && snapshot.selection.is_empty() {
        editor
            .recent_range(&snapshot)
            .unwrap_or_else(|| snapshot.selection.clone())
    } else {
        snapshot.selection.clone()
    };
    if range.is_empty() || range.len() > 128 {
        return Ok(());
    }
    let text = String::from_utf16(&snapshot.document[range.clone()]).map_err(io::Error::other)?;
    let word = text.trim_end_matches(' ');
    if word.is_empty() || word.chars().any(char::is_whitespace) {
        return Ok(());
    }
    let source = active_language(target);
    let mut choices = Vec::new();
    let request = if action == Action::AddWord {
        let language = crate::layout::detect(word, &job.cycle, source)
            .and_then(Language::dictionary_index)
            .unwrap_or_default();
        Request::Add {
            word: word.to_owned(),
            language,
        }
    } else {
        let (Some(source), Some(vocabulary)) = (source, vocabulary) else {
            return Ok(());
        };
        choices = vocabulary.options(dictionary, word, source, &job.cycle)?;
        Request::Choices {
            origin: job.foreground,
            choices: choices.clone(),
        }
    };
    if editor.snapshot()? != snapshot {
        return Ok(());
    }
    let capture = Captured {
        runtime: snapshot.target.runtime,
        provider: snapshot.target.provider,
        document: snapshot.document,
        selection: snapshot.selection,
    };
    let suffix = text[word.len()..].to_owned();
    let sender = job
        .reply
        .take()
        .expect("tool requests include a return channel");
    let host = owner as usize;
    thread::Builder::new()
        .name("typlo-accessible-tools".into())
        .spawn(move || {
            let result = if matches!(request, Request::Choices { .. }) {
                super::choice::wait(
                    host,
                    job.foreground,
                    job.sequence,
                    choices.clone(),
                    &canceled,
                )
            } else {
                crate::jobs::gui().and_then(|gui| {
                    crate::jobs::exchange::<_, Option<usize>>(
                        &gui,
                        "--selection-tools",
                        &profile,
                        &request,
                        None,
                        &canceled,
                    )
                })
            };
            match result {
                Ok(Some(index)) if !canceled.load(Ordering::Acquire) => {
                    if let Some(candidate) = choices.get(index) {
                        job.command = Command::ApplyChoice {
                            capture,
                            range,
                            result: format!("{}{}", candidate.text, suffix),
                            language: candidate.language,
                        };
                        let _ = sender.try_send(job);
                    }
                }
                Err(error) if error.kind() != io::ErrorKind::Interrupted => {
                    crate::runtime_error!("Typlo Rust selection tools: {error}")
                }
                _ => {}
            }
        })?;
    Ok(())
}

#[expect(
    clippy::too_many_arguments,
    reason = "Windows worker passes its independently borrowed services explicitly"
)]
fn run(
    automation: &IUIAutomation2,
    owner: HWND,
    job: Job,
    session: &mut Session<Element>,
    fallback: &mut super::clipboard_edit::Fallback,
    vocabulary: Option<&mut Vocabulary>,
    dictionary: &Dictionary,
    report: &diagnostics::Shared,
) -> io::Result<()> {
    let mut timing = diagnostics::Timing::since(report, job.command.diagnostic_name(), job.queued);
    timing.field("UI Automation");
    timing.phase("worker queue");
    let result = run_edit(
        automation,
        owner,
        job,
        session,
        fallback,
        vocabulary,
        dictionary,
        report,
        &mut timing,
    );
    if result.is_err() {
        timing.finish(&result, |_| false);
    }
    result
}

#[expect(
    clippy::too_many_arguments,
    reason = "Windows worker passes its independently borrowed services explicitly"
)]
fn run_edit(
    automation: &IUIAutomation2,
    owner: HWND,
    job: Job,
    session: &mut Session<Element>,
    fallback: &mut super::clipboard_edit::Fallback,
    vocabulary: Option<&mut Vocabulary>,
    dictionary: &Dictionary,
    report: &diagnostics::Shared,
    timing: &mut diagnostics::Timing,
) -> io::Result<()> {
    let target = Target {
        foreground: job.foreground as HWND,
        edit: job.edit as HWND,
        thread: job.thread,
        pid: job.pid,
    };
    let untracked = matches!(job.command, Command::ApplySpelling { review: true, .. });
    let mut editor = Accessible {
        automation,
        owner,
        lease: None,
        first_snapshot: None,
        confirmed_edit: None,
        canceled: job.canceled.clone(),
        keyboard_word_selection: false,
        native: WinEdit {
            expected: Some(target),
            sequence: Some(if untracked {
                input::sequence()
            } else {
                job.sequence
            }),
            recent: job.recent,
            ..Default::default()
        },
    };
    let source = active_language(editor.native.target()?);
    let fallback_operation = matches!(
        &job.command,
        Command::Convert
            | Command::To(_)
            | Command::Case
            | Command::Transliterate
            | Command::Undo
            | Command::Typing {
                snippet: None,
                word: Some(_),
                ..
            }
    );
    if fallback_operation {
        let (identity, element) = editor.focused_element()?;
        let control = unsafe { element.CurrentControlType() }.map_err(io::Error::other)?;
        let process = super::process_name(target.pid);
        let host = process
            .as_deref()
            .and_then(|path| path.rsplit(['/', '\\']).next())
            .and_then(ClipboardHost::from_executable);
        let chromium = is_chromium_editor(target);
        let partial_selection =
            chromium || host.is_some_and(ClipboardHost::reports_partial_selection);
        let document_wrapper = control == UIA_DocumentControlTypeId && partial_selection;
        // Chromium's text provider can report the caret while failing to apply
        // a UIA selection in contenteditable. Verify a short keyboard selection
        // against that same provider before issuing any paste.
        let keyboard_selection = partial_selection
            && matches!(
                job.command,
                Command::Typing { word: Some(_), .. } | Command::Undo
            )
            && (control == UIA_DocumentControlTypeId
                || unsafe { element.CurrentClassName() }.is_ok_and(|class| {
                    class
                        .to_string()
                        .split_whitespace()
                        .any(|name| name == "ProseMirror")
                }));
        let capture = if is_text_control(control) {
            read_text_pattern(&element)
        } else if is_editor_wrapper(control) && (chromium || has_writable_value(&element)?) {
            Err(unsupported())
        } else {
            return Err(unsupported());
        };
        let incomplete_selection = partial_selection
            && is_text_control(control)
            && match &capture {
                Err(error) => document_wrapper && error.kind() == io::ErrorKind::PermissionDenied,
                Ok((_, _)) if matches!(job.command, Command::Undo) => {
                    fallback.has_undo(target, job.sequence)
                }
                Ok((document, selection)) if selection.is_empty() => match &job.command {
                    Command::Typing {
                        word: Some(word), ..
                    } => word.strip_suffix(' ').is_some_and(|word| {
                        typing::verified_auto_word(document, selection.end, word).is_none()
                    }),
                    _ => editor.native.recent.as_deref().is_none_or(|recent| {
                        typing::verified_suffix(document, selection.end, recent).is_none()
                    }),
                },
                _ => false,
            };
        if incomplete_selection
            || capture
                .as_ref()
                .is_err_and(|error| error.kind() == io::ErrorKind::Unsupported)
        {
            if !document_wrapper
                && let Ok(value) = unsafe {
                    element.GetCurrentPatternAs::<IUIAutomationValuePattern>(UIA_ValuePatternId)
                }
                && unsafe { value.CurrentIsReadOnly() }
                    .map_err(io::Error::other)?
                    .as_bool()
            {
                return Err(unsupported());
            }
            let validate = || {
                if editor.focused_element()?.0 != identity {
                    return Err(changed());
                }
                Ok(())
            };
            use super::clipboard_edit::Command as CopyCommand;
            let started = Instant::now();
            let mut automatic = false;
            let command = match &job.command {
                Command::Convert => CopyCommand::Cycle,
                Command::To(language) => CopyCommand::To(*language),
                Command::Case => CopyCommand::Case,
                Command::Transliterate => CopyCommand::Transliterate,
                Command::Undo => CopyCommand::Undo,
                Command::Typing {
                    word: Some(word),
                    protect,
                    ..
                } => {
                    let (Some(vocabulary), Some(source)) = (vocabulary, source) else {
                        return Ok(());
                    };
                    let Some(typed) = word.strip_suffix(' ') else {
                        return Ok(());
                    };
                    let (suggestion, reason) =
                        vocabulary.decision(dictionary, typed, source, &job.cycle, *protect)?;
                    timing.phase("dictionary");
                    timing.dictionary(reason);
                    let Some((replacement, language)) = suggestion else {
                        timing.applied(false);
                        diagnostics::automatic(report, started, reason, Some(source), None);
                        return Ok(());
                    };
                    automatic = true;
                    CopyCommand::Automatic(replacement, language)
                }
                _ => unreachable!(),
            };
            let mut native = WinEdit {
                expected: Some(target),
                sequence: Some(job.sequence),
                recent: editor.native.recent.clone(),
                ..Default::default()
            };
            if let Command::Typing { word, .. } = &job.command {
                native.recent.clone_from(word);
            }
            timing.field("Verified clipboard");
            timing.phase("clipboard fallback");
            let result = fallback.run(
                owner,
                &native,
                command,
                source,
                &job.cycle,
                validate,
                job.canceled.clone(),
                timing,
            );
            if automatic {
                let target_language = result.as_ref().ok().copied().flatten();
                diagnostics::automatic(
                    report,
                    started,
                    if target_language.is_some() {
                        Decision::Applied
                    } else {
                        Decision::Failed
                    },
                    source,
                    target_language,
                );
            }
            let language = result?;
            if job.switch_layout
                && let Some(language) = language
                && editor.native.target()? == target
            {
                switch_layout(target, language);
            }
            return Ok(());
        }
        let (document, selection) = capture?;
        editor.keyboard_word_selection = keyboard_selection && selection.is_empty();
        timing.phase(if selection.is_empty() {
            "UI Automation returned a caret"
        } else {
            "UI Automation returned a selection"
        });
        if editor.focused()?.0 != identity {
            return Err(changed());
        }
        editor.first_snapshot = Some(Snapshot {
            target: identity,
            document,
            selection,
        });
    }
    timing.phase("field and selection");
    let language = match job.command {
        Command::Convert => session.convert(&mut editor, &job.cycle, source)?,
        Command::To(target) => session.convert_to(&mut editor, target, source)?,
        Command::Case => session.transform(&mut editor, |text, _| {
            Some((text_tools::cycle_case(text), None))
        })?,
        Command::Transliterate => session.transform(&mut editor, |text, _| {
            Some((text_tools::transliterate(text), None))
        })?,
        Command::Undo => {
            let undone = session.undo(&mut editor)?;
            timing.phase("verified Undo");
            timing.applied(undone);
            return Ok(());
        }
        Command::Spelling { .. } | Command::Tools(_) => {
            unreachable!("dialogs are started by the worker")
        }
        Command::ApplySpelling {
            capture, result, ..
        } => {
            let snapshot = Snapshot {
                target: Element {
                    window: target,
                    runtime: capture.runtime,
                    provider: capture.provider,
                },
                document: capture.document,
                selection: capture.selection,
            };
            if !job
                .canceled
                .as_ref()
                .is_some_and(|flag| flag.load(Ordering::Acquire))
            {
                session.replace_selection(&mut editor, snapshot, &result)?;
            }
            None
        }
        Command::ApplyChoice {
            capture,
            range,
            result,
            language,
        } => {
            let original = Snapshot {
                target: Element {
                    window: target,
                    runtime: capture.runtime,
                    provider: capture.provider,
                },
                document: capture.document,
                selection: capture.selection,
            };
            if !job
                .canceled
                .as_ref()
                .is_some_and(|flag| flag.load(Ordering::Acquire))
                && session.replace_verified_range(
                    &mut editor,
                    original,
                    range,
                    &result,
                    Some(language),
                )?
            {
                Some(language)
            } else {
                None
            }
        }
        Command::PrepareSnippetMenu {
            snippets,
            can_insert,
        } => {
            use super::snippet_menu::{self, Origin, Prepared, Selection};
            let native_anchor = snippet_menu::caret_anchor(target);
            let (selection, anchor) = if super::scintilla::is_editor(target) {
                super::scintilla::capture_snippet(owner, target, job.sequence)
                    .map(|(capture, anchor)| {
                        (
                            Some(Selection::Scintilla(capture)),
                            anchor.or(native_anchor),
                        )
                    })
                    .unwrap_or((None, native_anchor))
            } else {
                match editor.native.snapshot() {
                    Ok(snapshot) => (
                        Some(Selection::Native {
                            document: snapshot.document,
                            selection: snapshot.selection,
                        }),
                        native_anchor,
                    ),
                    Err(_) => {
                        let mut anchor = native_anchor;
                        let selection = editor.focused().ok().and_then(|(identity, element)| {
                            anchor = snippet_caret(&element).or(anchor);
                            let (document, selection) = read_text_pattern(&element).ok()?;
                            if editor.focused().ok()?.0 != identity {
                                return None;
                            }
                            Some(Selection::Accessible(Captured {
                                runtime: identity.runtime,
                                provider: identity.provider,
                                document,
                                selection,
                            }))
                        });
                        (selection, anchor)
                    }
                }
            };
            editor.native.target()?;
            snippet_menu::publish(
                owner,
                Prepared {
                    origin: Origin::new(target),
                    selection: selection.filter(|_| can_insert),
                    insertion_disabled: !can_insert,
                    sequence: job.sequence,
                    anchor: anchor.unwrap_or_else(snippet_menu::pointer_anchor),
                    snippets,
                },
            );
            None
        }
        Command::InsertMenuSnippet { capture, template } => {
            let expected = Snapshot {
                target: Element {
                    window: target,
                    runtime: capture.runtime,
                    provider: capture.provider,
                },
                document: capture.document,
                selection: capture.selection,
            };
            session.insert_snippet(&mut editor, expected, &web_template(template)?)?;
            None
        }
        Command::InsertScintillaSnippet { capture, template } => {
            super::scintilla::insert_snippet(owner, target, job.sequence, capture, &template)?;
            None
        }
        Command::Typing {
            snippet,
            word,
            protect,
        } => {
            if let Some((trigger, template)) = snippet
                && session.expand_snippet(&mut editor, &trigger, &web_template(template)?)?
            {
                return Ok(());
            }
            match (word, source, vocabulary) {
                (Some(word), Some(source), Some(vocabulary)) => {
                    let start = Instant::now();
                    let mut reason = Decision::Unverified;
                    let result = session.correct_word(&mut editor, &word, |token| {
                        timing.phase("range verification");
                        let (suggestion, decision) =
                            vocabulary.decision(dictionary, token, source, &job.cycle, protect)?;
                        timing.phase("dictionary");
                        reason = decision;
                        Ok(suggestion)
                    });
                    let decision = match &result {
                        Ok(Some(_)) => Decision::Applied,
                        Ok(None) => reason,
                        Err(_) => Decision::Failed,
                    };
                    timing.dictionary(decision);
                    diagnostics::automatic(
                        report,
                        start,
                        decision,
                        Some(source),
                        result.as_ref().ok().copied().flatten(),
                    );
                    result?
                }
                _ => None,
            }
        }
    };
    timing.applied(language.is_some());
    timing.phase("conversion and verified replacement");
    if job.switch_layout
        && let Some(language) = language
        && editor
            .lease
            .as_ref()
            .is_none_or(input::SelectionLease::still_quiet)
        && editor.native.target()? == target
    {
        switch_layout(target, language);
    }
    Ok(())
}

impl Accessible<'_> {
    fn focused(&self) -> io::Result<(Element, IUIAutomationElement)> {
        let (identity, element) = self.focused_element()?;
        if !is_text_control(unsafe { element.CurrentControlType() }.map_err(io::Error::other)?) {
            return Err(unsupported());
        }
        Ok((identity, element))
    }

    fn focused_element(&self) -> io::Result<(Element, IUIAutomationElement)> {
        let window = self.native.target()?;
        unsafe {
            let element = self
                .automation
                .GetFocusedElement()
                .map_err(|_| unsupported())?;
            if element
                .CurrentIsPassword()
                .map_err(io::Error::other)?
                .as_bool()
                || !element
                    .CurrentIsEnabled()
                    .map_err(io::Error::other)?
                    .as_bool()
            {
                return Err(unsupported());
            }
            let identity = Element {
                window,
                provider: element.CurrentProcessId().map_err(io::Error::other)?,
                runtime: runtime_id(&element)?,
            };
            Ok((identity, element))
        }
    }
}

fn is_text_control(control: UIA_CONTROLTYPE_ID) -> bool {
    control == UIA_EditControlTypeId || control == UIA_DocumentControlTypeId
}

fn is_editor_wrapper(control: UIA_CONTROLTYPE_ID) -> bool {
    [
        UIA_GroupControlTypeId,
        UIA_PaneControlTypeId,
        UIA_CustomControlTypeId,
    ]
    .contains(&control)
}

fn has_writable_value(element: &IUIAutomationElement) -> io::Result<bool> {
    let Ok(value) =
        (unsafe { element.GetCurrentPatternAs::<IUIAutomationValuePattern>(UIA_ValuePatternId) })
    else {
        return Ok(false);
    };
    unsafe { value.CurrentIsReadOnly() }
        .map(|flag| !flag.as_bool())
        .map_err(io::Error::other)
}

#[derive(Clone, Copy)]
enum ClipboardHost {
    Code,
    ChatGpt,
    Teams,
}

impl ClipboardHost {
    fn from_executable(name: &str) -> Option<Self> {
        [
            ("code.exe", Self::Code),
            ("code - insiders.exe", Self::Code),
            ("chatgpt.exe", Self::ChatGpt),
            ("ms-teams.exe", Self::Teams),
            ("teams.exe", Self::Teams),
        ]
        .into_iter()
        .find_map(|(exe, host)| name.eq_ignore_ascii_case(exe).then_some(host))
    }

    fn reports_partial_selection(self) -> bool {
        matches!(self, Self::Code | Self::ChatGpt | Self::Teams)
    }
}

fn is_chromium_editor(target: Target) -> bool {
    use windows_sys::Win32::UI::WindowsAndMessaging::GetClassNameW;
    let mut class = [0u16; 80];
    let size = unsafe { GetClassNameW(target.edit, class.as_mut_ptr(), class.len() as i32) };
    let class = String::from_utf16_lossy(&class[..size.max(0) as usize]);
    matches!(
        class.as_str(),
        "Chrome_RenderWidgetHostHWND" | "Chrome_WidgetWin_0" | "Chrome_WidgetWin_1"
    )
}

impl Editor for Accessible<'_> {
    type Target = Element;

    fn take_confirmed_edit(&mut self) -> Option<Snapshot<Element>> {
        self.confirmed_edit.take()
    }

    fn snapshot(&mut self) -> io::Result<Snapshot<Element>> {
        if let Some(snapshot) = self.first_snapshot.take() {
            return Ok(snapshot);
        }
        let (identity, element) = self.focused()?;
        let (document, selection) = read_text_pattern(&element)?;
        if self.focused()?.0 != identity {
            return Err(changed());
        }
        Ok(Snapshot {
            target: identity,
            document,
            selection,
        })
    }

    fn recent_range(&self, snapshot: &Snapshot<Element>) -> Option<Range<usize>> {
        typing::verified_suffix(
            &snapshot.document,
            snapshot.selection.end,
            self.native.recent.as_deref()?,
        )
    }

    fn move_caret(&mut self, expected: &Snapshot<Element>, caret: usize) -> io::Result<()> {
        let (identity, element) = self.focused()?;
        if identity != expected.target || self.snapshot()? != *expected {
            return Err(changed());
        }
        let range = verified_range(&element, expected, caret..caret)?;
        if self.focused()?.0 != expected.target {
            return Err(changed());
        }
        unsafe { range.Select() }.map_err(io::Error::other)?;
        self.confirm_selection(expected, caret..caret)
    }

    fn replace(
        &mut self,
        expected: &Snapshot<Element>,
        mut range: Range<usize>,
        mut replacement: &[u16],
    ) -> io::Result<()> {
        self.confirmed_edit = None;
        let preserve_separator = trailing_separator(expected, &range, replacement);
        let final_caret = preserve_separator.then_some(range.start + replacement.len());
        if preserve_separator {
            range.end -= 1;
            replacement = &replacement[..replacement.len() - 1];
        }
        let release_deadline = Instant::now() + MODIFIER_RELEASE_TIMEOUT;
        while modifiers_down() {
            self.native.target()?;
            if Instant::now() >= release_deadline {
                return Err(changed());
            }
            thread::sleep(Duration::from_millis(8));
        }
        let keyboard_selection = self
            .keyboard_word_selection
            .then(|| keyboard_word_selection(expected, &range))
            .flatten();
        let proposed = if range != expected.selection && keyboard_selection.is_none() {
            let (identity, element) = self.focused()?;
            if identity != expected.target {
                return Err(changed());
            }
            Some(verified_range(&element, expected, range.clone())?)
        } else {
            None
        };
        if self.snapshot()? != *expected || modifiers_down() {
            return Err(changed());
        }
        let lease;
        let mut clipboard = TemporaryText::capture(self.owner)?;
        clipboard.publish(replacement)?;
        if !clipboard.still_owns() || modifiers_down() {
            return Err(changed());
        }
        lease = input::SelectionLease::start(
            self.native.sequence.unwrap_or_else(input::sequence),
            expected.target.window,
            self.canceled.clone(),
        )?;
        let operation = (|| {
            if self.focused()?.0 != expected.target {
                return Err(changed());
            }
            if let Some(selection) = &proposed {
                unsafe { selection.Select() }.map_err(io::Error::other)?;
            } else if let Some((skip, width)) = keyboard_selection {
                use windows_sys::Win32::UI::Input::KeyboardAndMouse::{VK_LEFT, VK_SHIFT};
                if skip > 0 {
                    input::send_selection_keys(None, VK_LEFT, skip, false)?;
                }
                input::send_selection_keys(Some(VK_SHIFT), VK_LEFT, width, false)?;
            }
            self.confirm_selection(expected, range.clone())?;
            lease.arm_focus();
            if !lease.still_quiet()
                || !clipboard.still_owns()
                || self.focused()?.0 != expected.target
            {
                return Err(changed());
            }
            send_paste(input::paste_tag())?;
            if lease.wait_for_paste() {
                Ok(())
            } else {
                Err(changed())
            }
        })();
        if let Err(error) = operation {
            if !lease.cancel_pending_paste() {
                if keyboard_selection.is_some() {
                    let _ = self.restore_keyboard_word_caret(expected, range);
                } else if proposed.is_some() {
                    let _ = self.restore_selection(expected, range);
                }
            }
            return Err(error);
        }
        self.lease = Some(lease);
        let mut result = expected.document.clone();
        result.splice(range.clone(), replacement.iter().copied());
        let paste_end = range.start + replacement.len();
        let verification = self.wait_for_replacement(expected, &result, paste_end, &clipboard);
        if verification.is_err() {
            if keyboard_selection.is_some() {
                let _ = self.restore_keyboard_word_caret(expected, range);
            } else if proposed.is_some() {
                let _ = self.restore_selection(expected, range);
            }
        }
        let mut current = verification?;
        if let Some(caret) = final_caret {
            let moved = if keyboard_selection.is_some() {
                use windows_sys::Win32::UI::Input::KeyboardAndMouse::VK_RIGHT;
                self.native
                    .target()
                    .and_then(|_| input::send_selection_keys(None, VK_RIGHT, 1, false))
                    .and_then(|()| self.confirm_selection(&current, caret..caret))
            } else {
                self.move_caret(&current, caret)
            };
            if let Err(error) = moved {
                let verified = self
                    .snapshot()
                    .ok()
                    .filter(|after| {
                        after.target == current.target && after.document == current.document
                    })
                    .unwrap_or(current);
                self.confirmed_edit = Some(verified);
                return Err(error);
            }
            current.selection = caret..caret;
        }
        self.confirmed_edit = Some(current);
        if let Err(error) = clipboard.restore() {
            // The document already changed. Let the clipboard guard retry on drop
            // without losing the confirmed edit or its Undo record.
            crate::runtime_error!("Typlo Rust: restoring the clipboard after an edit: {error}");
        }
        Ok(())
    }
}

fn keyboard_word_selection<T>(
    expected: &Snapshot<T>,
    word: &Range<usize>,
) -> Option<(usize, usize)> {
    use unicode_segmentation::UnicodeSegmentation;
    let skip = expected.selection.end.checked_sub(word.end)?;
    if !expected.selection.is_empty() || skip > 1 || !(1..=128).contains(&word.len()) {
        return None;
    }
    let text = String::from_utf16(expected.document.get(word.clone())?).ok()?;
    if text.chars().any(char::is_whitespace) {
        return None;
    }
    Some((skip, text.graphemes(true).count()))
}

// Address bars can trim pasted text. Keep a verified existing separator in
// the field, then move the caret past it after the word has been replaced.
fn trailing_separator<T>(
    expected: &Snapshot<T>,
    range: &Range<usize>,
    replacement: &[u16],
) -> bool {
    range.len() > 1
        && replacement.len() > 1
        && (expected.selection == (range.end..range.end)
            || expected.selection == (range.end - 1..range.end - 1)
            || expected.selection == *range)
        && matches!(replacement.last(), Some(0x20 | 0xa0))
        && expected.document.get(range.end - 1) == replacement.last()
}

impl Accessible<'_> {
    fn restore_keyboard_word_caret(
        &mut self,
        expected: &Snapshot<Element>,
        word: Range<usize>,
    ) -> io::Result<()> {
        use windows_sys::Win32::UI::Input::KeyboardAndMouse::VK_RIGHT;
        let current = self.snapshot()?;
        if current.target != expected.target || current.document != expected.document {
            return Err(changed());
        }
        let skip = expected
            .selection
            .end
            .checked_sub(word.end)
            .ok_or_else(changed)?;
        let count = if current.selection == word {
            1 + skip
        } else if current.selection == (word.end..word.end) {
            skip
        } else {
            return Ok(());
        };
        if count > 0 {
            input::send_selection_keys(None, VK_RIGHT, count, true)?;
        }
        Ok(())
    }

    fn wait_for_replacement(
        &mut self,
        expected: &Snapshot<Element>,
        result: &[u16],
        caret: usize,
        clipboard: &TemporaryText,
    ) -> io::Result<Snapshot<Element>> {
        wait_for_edit(expected, result, caret, || {
            if !clipboard.still_owns() {
                return Err(changed());
            }
            self.snapshot()
        })
    }

    fn restore_selection(
        &mut self,
        previous: &Snapshot<Element>,
        selected: Range<usize>,
    ) -> io::Result<()> {
        let current = self.snapshot()?;
        if current.target != previous.target
            || current.document != previous.document
            || current.selection != selected
        {
            return Err(changed());
        }
        let (identity, element) = self.focused()?;
        if identity != previous.target {
            return Err(changed());
        }
        let original = verified_range(&element, &current, previous.selection.clone())?;
        if self.snapshot()? != current {
            return Err(changed());
        }
        unsafe { original.Select() }.map_err(io::Error::other)?;
        self.confirm_selection(previous, previous.selection.clone())
    }

    fn confirm_selection(
        &mut self,
        expected: &Snapshot<Element>,
        range: Range<usize>,
    ) -> io::Result<()> {
        wait_for_selection(expected, range, || self.snapshot())
    }
}

fn wait_for_edit<T: Eq>(
    expected: &Snapshot<T>,
    result: &[u16],
    caret: usize,
    mut read: impl FnMut() -> io::Result<Snapshot<T>>,
) -> io::Result<Snapshot<T>> {
    let deadline = Instant::now() + Duration::from_millis(240);
    let mut interval = Duration::from_millis(4);
    loop {
        let current = read()?;
        if current.target != expected.target {
            return Err(changed());
        }
        if current.document == result && current.selection == (caret..caret) {
            return Ok(current);
        }
        if Instant::now() >= deadline
            || (current.document != expected.document && current.document != result)
        {
            return Err(io::Error::other(
                "The accessible editor did not confirm replacement",
            ));
        }
        thread::sleep(interval);
        interval = (interval * 2).min(Duration::from_millis(12));
    }
}

fn wait_for_selection<T: Eq>(
    expected: &Snapshot<T>,
    range: Range<usize>,
    mut read: impl FnMut() -> io::Result<Snapshot<T>>,
) -> io::Result<()> {
    let deadline = Instant::now() + Duration::from_millis(45);
    loop {
        let current = read()?;
        if current.target != expected.target || current.document != expected.document {
            return Err(changed());
        }
        if current.selection == range {
            return Ok(());
        }
        if Instant::now() >= deadline {
            return Err(changed());
        }
        thread::sleep(Duration::from_millis(4));
    }
}

fn web_template(template: Template) -> io::Result<Template> {
    let text: Vec<_> = template.text.encode_utf16().collect();
    let caret = template
        .caret
        .map(|offset| normalize_newlines(&text[..offset]).len());
    Ok(Template {
        text: String::from_utf16(&normalize_newlines(&text)).map_err(io::Error::other)?,
        caret,
    })
}

fn normalize_newlines(text: &[u16]) -> Vec<u16> {
    text.iter()
        .copied()
        .enumerate()
        .filter_map(|(index, unit)| {
            if unit == 13 && text.get(index + 1) == Some(&10) {
                None
            } else {
                Some(unit)
            }
        })
        .collect()
}

fn read_text_pattern(element: &IUIAutomationElement) -> io::Result<(Vec<u16>, Range<usize>)> {
    unsafe {
        if element
            .CurrentIsPassword()
            .map_err(io::Error::other)?
            .as_bool()
        {
            return Err(unsupported());
        }
        let text: IUIAutomationTextPattern = element
            .GetCurrentPatternAs(UIA_TextPatternId)
            .map_err(|_| unsupported())?;
        let document = text.DocumentRange().map_err(io::Error::other)?;
        let selection = text.GetSelection().map_err(|_| unsupported())?;
        if selection.Length().map_err(io::Error::other)? != 1 {
            return Err(unsupported());
        }
        let selected = selection.GetElement(0).map_err(io::Error::other)?;
        let read_only = if let Ok(value) =
            element.GetCurrentPatternAs::<IUIAutomationValuePattern>(UIA_ValuePatternId)
        {
            value
                .CurrentIsReadOnly()
                .map_err(io::Error::other)?
                .as_bool()
        } else {
            let mut attribute = selected
                .GetAttributeValue(UIA_IsReadOnlyAttributeId)
                .map_err(|_| unsupported())?;
            let variant = &attribute.Anonymous.Anonymous;
            let read_only = (variant.vt == VT_BOOL).then(|| variant.Anonymous.boolVal.0 != 0);
            VariantClear(&mut attribute).map_err(io::Error::other)?;
            read_only.ok_or_else(unsupported)?
        };
        if read_only {
            return Err(io::ErrorKind::PermissionDenied.into());
        }
        let all = range_text(&document)?;
        let selection = measure_range(&document, &selected, &all)?;
        Ok((all, selection))
    }
}

fn range_text(range: &IUIAutomationTextRange) -> io::Result<Vec<u16>> {
    let text = unsafe { range.GetText((MAX_UNITS + 1) as i32) }.map_err(io::Error::other)?;
    if text.len() > MAX_UNITS {
        return Err(unsupported());
    }
    Ok(normalize_newlines(&text))
}

fn measure_range(
    document: &IUIAutomationTextRange,
    selection: &IUIAutomationTextRange,
    text: &[u16],
) -> io::Result<Range<usize>> {
    let prefix = unsafe { document.Clone() }.map_err(io::Error::other)?;
    unsafe {
        prefix.MoveEndpointByRange(
            TextPatternRangeEndpoint_End,
            selection,
            TextPatternRangeEndpoint_Start,
        )
    }
    .map_err(io::Error::other)?;
    let prefix = range_text(&prefix)?;
    let part = range_text(selection)?;
    let result = prefix.len()..prefix.len() + part.len();
    if !text.starts_with(&prefix) || text.get(result.clone()) != Some(&part[..]) {
        return Err(changed());
    }
    Ok(result)
}

fn verified_range(
    element: &IUIAutomationElement,
    expected: &Snapshot<Element>,
    desired: Range<usize>,
) -> io::Result<IUIAutomationTextRange> {
    let text: IUIAutomationTextPattern =
        unsafe { element.GetCurrentPatternAs(UIA_TextPatternId) }.map_err(|_| unsupported())?;
    let document = unsafe { text.DocumentRange() }.map_err(io::Error::other)?;
    let ranges = unsafe { text.GetSelection() }.map_err(io::Error::other)?;
    if unsafe { ranges.Length() }.map_err(io::Error::other)? != 1
        || range_text(&document)? != expected.document
    {
        return Err(changed());
    }
    let original = unsafe { ranges.GetElement(0) }.map_err(io::Error::other)?;
    if measure_range(&document, &original, &expected.document)? != expected.selection
        || expected.document.get(desired.clone()).is_none()
    {
        return Err(changed());
    }
    let start = range_point(&document, &original, expected, desired.start)?;
    let end = if desired.is_empty() {
        None
    } else {
        Some(range_point(&document, &original, expected, desired.end)?)
    };
    if let Some(end) = end {
        unsafe {
            start.MoveEndpointByRange(
                TextPatternRangeEndpoint_End,
                &end,
                TextPatternRangeEndpoint_Start,
            )
        }
        .map_err(io::Error::other)?;
    }
    if measure_range(&document, &start, &expected.document)? != desired {
        return Err(changed());
    }
    Ok(start)
}

fn range_point(
    document: &IUIAutomationTextRange,
    selection: &IUIAutomationTextRange,
    expected: &Snapshot<Element>,
    offset: usize,
) -> io::Result<IUIAutomationTextRange> {
    let anchor = expected.selection.end;
    let distance = offset.min(anchor)..offset.max(anchor);
    let delta = expected.document.get(distance).ok_or_else(changed)?;
    let chars = char::decode_utf16(delta.iter().copied())
        .try_fold(0, |count, character| character.map(|_| count + 1))
        .map_err(io::Error::other)?;
    let sign = if offset < anchor { -1 } else { 1 };
    let alternatives = [chars, delta.len()];
    for (index, steps) in alternatives.into_iter().enumerate() {
        if index == 1 && steps == chars {
            break;
        }
        let point = unsafe { selection.Clone() }.map_err(io::Error::other)?;
        unsafe {
            point
                .MoveEndpointByRange(
                    TextPatternRangeEndpoint_Start,
                    selection,
                    TextPatternRangeEndpoint_End,
                )
                .map_err(io::Error::other)?;
            if steps != 0 {
                let endpoint = if sign < 0 {
                    TextPatternRangeEndpoint_Start
                } else {
                    TextPatternRangeEndpoint_End
                };
                point
                    .MoveEndpointByUnit(endpoint, TextUnit_Character, sign * steps as i32)
                    .map_err(io::Error::other)?;
                let opposite = if sign < 0 {
                    TextPatternRangeEndpoint_End
                } else {
                    TextPatternRangeEndpoint_Start
                };
                point
                    .MoveEndpointByRange(opposite, &point, endpoint)
                    .map_err(io::Error::other)?;
            }
        }
        if measure_range(document, &point, &expected.document)? == (offset..offset) {
            return Ok(point);
        }
    }
    Err(unsupported())
}

pub(super) fn check_focused(native: WinEdit) -> io::Result<bool> {
    let _apartment = ComApartment::initialize().map_err(io::Error::other)?;
    let automation = new_automation().map_err(io::Error::other)?;
    let mut editor = Accessible {
        automation: &automation,
        native,
        owner: null_mut(),
        lease: None,
        first_snapshot: None,
        confirmed_edit: None,
        canceled: None,
        keyboard_word_selection: false,
    };
    Ok(!editor.snapshot()?.selection.is_empty())
}

pub(super) fn probe_snapshot(window: usize, label: &str, expected: &str) -> io::Result<()> {
    probe_element(window, label, |element| {
        let (document, selection) = read_text_pattern(element)?;
        if !document[selection.clone()]
            .iter()
            .copied()
            .eq(expected.encode_utf16())
        {
            return Err(io::Error::other(format!(
                "Unexpected selection {selection:?} in fixture"
            )));
        }
        Ok(())
    })
}

pub(super) fn probe_range(window: usize, label: &str, desired: Range<usize>) -> io::Result<()> {
    probe_select_range(window, label, |_| Ok(desired))
}

pub(super) fn probe_preserve_separator(
    window: usize,
    label: &str,
    original: &str,
    replacement: &str,
) -> io::Result<()> {
    let original: Vec<_> = original.encode_utf16().collect();
    let replacement: Vec<_> = replacement.encode_utf16().collect();
    probe_select_range(window, label, |snapshot| {
        let mut range = snapshot.selection.clone();
        if range.is_empty() {
            range.start = range
                .end
                .checked_sub(original.len())
                .ok_or_else(unsupported)?;
        }
        if snapshot.document.get(range.clone()) != Some(original.as_slice())
            || !trailing_separator(snapshot, &range, &replacement)
        {
            return Err(io::Error::other(
                "The fixture does not contain the expected word and separator",
            ));
        }
        range.end -= 1;
        Ok(range)
    })
}

pub(super) fn probe_correction_range(
    window: usize,
    label: &str,
    typed: &str,
    expected: &str,
) -> io::Result<()> {
    probe_select_range(window, label, |snapshot| {
        let token = typed
            .strip_suffix(' ')
            .filter(|_| snapshot.selection.is_empty())
            .ok_or_else(|| {
                io::Error::other(format!(
                    "Expected a caret in the typing fixture, got {:?}",
                    snapshot.selection
                ))
            })?;
        let (range, space) =
            typing::verified_auto_word(&snapshot.document, snapshot.selection.end, token)
                .ok_or_else(|| io::Error::other("Could not verify the typed word and its space"))?;
        let profile = std::env::temp_dir().join(format!("typlo-fixture-{}", std::process::id()));
        let mut vocabulary = Vocabulary::installed()?;
        let (mut replacement, target) = vocabulary
            .suggest(
                &Dictionary::new(&profile),
                token,
                Language::En,
                &[Language::En, Language::Ru],
                true,
            )?
            .ok_or_else(|| io::Error::other("No dictionary match for the fixture"))?;
        replacement.push(space);
        if target != Language::Ru || replacement != expected {
            return Err(io::Error::other(
                "Unexpected dictionary correction for the fixture",
            ));
        }
        Ok(range)
    })
}

fn probe_select_range(
    window: usize,
    label: &str,
    desired: impl FnOnce(&Snapshot<Element>) -> io::Result<Range<usize>>,
) -> io::Result<()> {
    probe_element(window, label, |element| {
        let (document, selection) = read_text_pattern(element)
            .map_err(|error| io::Error::other(format!("Read initial fixture: {error}")))?;
        let snapshot = Snapshot {
            target: Element {
                window: Target {
                    foreground: window as HWND,
                    edit: window as HWND,
                    thread: 0,
                    pid: 0,
                },
                runtime: runtime_id(element)?,
                provider: unsafe { element.CurrentProcessId() }.map_err(io::Error::other)?,
            },
            document,
            selection,
        };
        let desired = desired(&snapshot)?;
        let proposed = verified_range(element, &snapshot, desired.clone()).map_err(|error| {
            io::Error::other(format!(
                "Prepare fixture range {desired:?} at {:?}: {error}",
                snapshot.selection
            ))
        })?;
        unsafe { proposed.Select() }.map_err(io::Error::other)?;
        wait_for_selection(&snapshot, desired.clone(), || {
            let (document, selection) = read_text_pattern(element)?;
            Ok(Snapshot {
                document,
                selection,
                target: snapshot.target.clone(),
            })
        })
        .map_err(|error| io::Error::other(format!("Confirm fixture range {desired:?}: {error}")))
    })
}

fn probe_element(
    window: usize,
    label: &str,
    check: impl FnOnce(&IUIAutomationElement) -> io::Result<()>,
) -> io::Result<()> {
    let _apartment = ComApartment::initialize().map_err(io::Error::other)?;
    let automation = new_automation().map_err(io::Error::other)?;
    let (root, walker) = unsafe {
        (
            automation
                .ElementFromHandle(windows::Win32::Foundation::HWND(window as HWND))
                .map_err(io::Error::other)?,
            automation.RawViewWalker().map_err(io::Error::other)?,
        )
    };
    let mut nodes = vec![root];
    let mut visited = 0;
    while let Some(element) = nodes.pop() {
        visited += 1;
        if visited > 2000 {
            break;
        }
        unsafe {
            if element.CurrentName().is_ok_and(|name| name == label) {
                return check(&element);
            }
            if let Ok(child) = walker.GetFirstChildElement(&element) {
                nodes.push(child);
            }
            if visited > 1
                && let Ok(sibling) = walker.GetNextSiblingElement(&element)
            {
                nodes.push(sibling);
            }
        }
    }
    Err(io::Error::other(format!(
        "WebView fixture control missing after {visited} UI Automation nodes"
    )))
}

fn runtime_id(element: &IUIAutomationElement) -> io::Result<Vec<i32>> {
    let array = SafeArray(unsafe { element.GetRuntimeId() }.map_err(io::Error::other)?);
    if array.0.is_null()
        || unsafe { SafeArrayGetDim(array.0) } != 1
        || unsafe { SafeArrayGetElemsize(array.0) } != size_of::<i32>() as u32
    {
        return Err(unsupported());
    }
    unsafe {
        let low = SafeArrayGetLBound(array.0, 1).map_err(io::Error::other)?;
        let high = SafeArrayGetUBound(array.0, 1).map_err(io::Error::other)?;
        let len = i64::from(high) - i64::from(low) + 1;
        if !(1..=128).contains(&len) {
            return Err(unsupported());
        }
        let mut data = null_mut();
        SafeArrayAccessData(array.0, &mut data).map_err(io::Error::other)?;
        let result = (!data.is_null())
            .then(|| std::slice::from_raw_parts(data.cast::<i32>(), len as usize).to_vec());
        SafeArrayUnaccessData(array.0).map_err(io::Error::other)?;
        result.ok_or_else(unsupported)
    }
}

fn snippet_caret(element: &IUIAutomationElement) -> Option<windows_sys::Win32::Foundation::RECT> {
    use windows_sys::Win32::Foundation::RECT;
    unsafe {
        let range = if let Ok(pattern) =
            element.GetCurrentPatternAs::<IUIAutomationTextPattern2>(UIA_TextPattern2Id)
        {
            let mut active = windows::core::BOOL::default();
            pattern
                .GetCaretRange(&mut active)
                .ok()
                .filter(|_| active.as_bool())
        } else {
            None
        }
        .or_else(|| {
            let pattern = element
                .GetCurrentPatternAs::<IUIAutomationTextPattern>(UIA_TextPatternId)
                .ok()?;
            let range = pattern.GetSelection().ok()?.GetElement(0).ok()?;
            range
                .MoveEndpointByRange(
                    TextPatternRangeEndpoint_Start,
                    &range,
                    TextPatternRangeEndpoint_End,
                )
                .ok()?;
            Some(range)
        })?;
        let right_edge = range
            .MoveEndpointByUnit(TextPatternRangeEndpoint_End, TextUnit_Character, 1)
            .ok()?
            == 0;
        if right_edge {
            range
                .MoveEndpointByUnit(TextPatternRangeEndpoint_Start, TextUnit_Character, -1)
                .ok()?;
        }
        let array = SafeArray(range.GetBoundingRectangles().ok()?);
        if array.0.is_null()
            || SafeArrayGetDim(array.0) != 1
            || SafeArrayGetElemsize(array.0) != size_of::<f64>() as u32
            || i64::from(SafeArrayGetUBound(array.0, 1).ok()?)
                - i64::from(SafeArrayGetLBound(array.0, 1).ok()?)
                < 3
        {
            return None;
        }
        let mut data = null_mut();
        SafeArrayAccessData(array.0, &mut data).ok()?;
        let rect = (!data.is_null()).then(|| *(data.cast::<[f64; 4]>()));
        SafeArrayUnaccessData(array.0).ok()?;
        let [x, y, w, h] = rect?;
        if ![x, y, w, h].iter().all(|value| value.is_finite()) || h < 1. {
            return None;
        }
        let x = if right_edge { x + w } else { x } as i32;
        Some(RECT {
            left: x,
            right: x + 1,
            top: y as i32,
            bottom: (y + h) as i32,
        })
    }
}

struct SafeArray(*mut SAFEARRAY);
impl Drop for SafeArray {
    fn drop(&mut self) {
        if !self.0.is_null() {
            let _ = unsafe { SafeArrayDestroy(self.0) };
        }
    }
}

pub(super) const MODIFIER_RELEASE_TIMEOUT: Duration = Duration::from_millis(500);

pub(super) fn modifiers_down() -> bool {
    [VK_SHIFT, VK_CONTROL, VK_MENU, VK_LWIN, VK_RWIN]
        .into_iter()
        .any(|key| unsafe { GetAsyncKeyState(key.into()) } < 0)
}

pub(super) fn send_paste(tag: usize) -> io::Result<()> {
    let key_with_tag = |key, flags, tag| INPUT {
        r#type: INPUT_KEYBOARD,
        Anonymous: INPUT_0 {
            ki: KEYBDINPUT {
                wVk: key,
                dwFlags: flags,
                dwExtraInfo: tag,
                ..Default::default()
            },
        },
    };
    let key = |key, flags| key_with_tag(key, flags, tag);
    let inputs = [
        key(VK_CONTROL, 0),
        key(u16::from(b'V'), 0),
        key(u16::from(b'V'), KEYEVENTF_KEYUP),
        key(VK_CONTROL, KEYEVENTF_KEYUP),
    ];
    if unsafe {
        SendInput(
            inputs.len() as u32,
            inputs.as_ptr(),
            size_of::<INPUT>() as i32,
        )
    } != inputs.len() as u32
    {
        let release = key_with_tag(VK_CONTROL, KEYEVENTF_KEYUP, input::OWN_EDIT);
        unsafe {
            SendInput(1, &release, size_of::<INPUT>() as i32);
        }
        return Err(io::Error::other(
            "Windows did not accept the complete paste command",
        ));
    }
    Ok(())
}

fn unsupported() -> io::Error {
    io::Error::new(io::ErrorKind::Unsupported, "No verified accessible editor")
}
fn changed() -> io::Error {
    io::Error::new(io::ErrorKind::Interrupted, "Accessible editor changed")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn waits_for_caret_after_text_changes_and_rejects_the_wrong_field() {
        let before = Snapshot {
            target: 1,
            document: vec![1, 2],
            selection: 0..1,
        };
        let changed_text = Snapshot {
            target: 1,
            document: vec![3, 2],
            selection: 0..1,
        };
        let confirmed = Snapshot {
            selection: 1..1,
            ..changed_text.clone()
        };
        let mut snapshots = [before.clone(), changed_text, confirmed.clone()].into_iter();
        assert!(
            wait_for_edit(&before, &confirmed.document, 1, || Ok(snapshots
                .next()
                .unwrap()))
            .is_ok()
        );
        let other_field = Snapshot {
            target: 2,
            ..confirmed.clone()
        };
        let error =
            wait_for_edit(&before, &confirmed.document, 1, || Ok(other_field.clone())).unwrap_err();
        assert_eq!(error.kind(), io::ErrorKind::Interrupted);
    }

    #[test]
    fn keeps_the_existing_separator_for_caret_selection_and_undo() {
        for separator in [' ', '\u{a0}'] {
            let mut before = Snapshot {
                target: (),
                document: format!("ghbdtn{separator}").encode_utf16().collect(),
                selection: 7..7,
            };
            let replacement = format!("привет{separator}")
                .encode_utf16()
                .collect::<Vec<_>>();
            assert!(trailing_separator(&before, &(0..7), &replacement));
            before.selection = 0..7;
            assert!(trailing_separator(&before, &(0..7), &replacement));
            before.selection = 1..7;
            assert!(!trailing_separator(&before, &(0..7), &replacement));
            before.selection = 7..7;
            assert!(!trailing_separator(&before, &(0..7), &replacement[..6]));
        }
    }

    #[test]
    fn keyboard_undo_selects_the_whole_word_with_or_without_a_separator() {
        for (text, word, selection, movement) in [
            ("🙂 привет ", 3..9, 10..10, Some((1, 6))),
            ("🙂 привет", 3..9, 9..9, Some((0, 6))),
            ("🙂 привіт\u{a0}", 3..9, 10..10, Some((1, 6))),
            ("🙂 e\u{301}", 3..5, 5..5, Some((0, 1))),
            ("🙂 привет xy", 3..9, 12..12, None),
            ("🙂 при вет", 3..10, 10..10, None),
            ("🙂 привет", 3..9, 3..9, None),
        ] {
            let snapshot = Snapshot {
                target: (),
                document: text.encode_utf16().collect(),
                selection,
            };
            assert_eq!(
                keyboard_word_selection(&snapshot, &word),
                movement,
                "{text}"
            );
        }
    }

    #[test]
    fn codex_accepts_verified_autocorrection_for_an_editor_wrapper() {
        let autocorrect = Command::Typing {
            snippet: None,
            word: Some("ghbdtn ".into()),
            protect: true,
        };
        let host = ClipboardHost::from_executable("ChatGPT.exe").unwrap();
        assert!(host.reports_partial_selection());
        assert!(matches!(autocorrect, Command::Typing { word: Some(_), .. }));
        assert!(host.reports_partial_selection());
        assert!(is_editor_wrapper(UIA_PaneControlTypeId));
        assert!(!is_editor_wrapper(UIA_ButtonControlTypeId));
        assert!(
            ClipboardHost::from_executable("ms-teams.exe")
                .unwrap()
                .reports_partial_selection()
        );
        assert!(ClipboardHost::from_executable("unrelated.exe").is_none());
    }

    #[test]
    fn waits_for_the_selected_range_but_rejects_changes_to_the_document() {
        let before = Snapshot {
            target: 1,
            document: "keep ghbdtn ".encode_utf16().collect(),
            selection: 12..12,
        };
        let mut selected = before.clone();
        selected.selection = 5..12;
        let mut snapshots = [before.clone(), selected.clone()].into_iter();
        wait_for_selection(&before, 5..12, || Ok(snapshots.next().unwrap())).unwrap();

        selected.document[5] = u16::from(b'x');
        let error = wait_for_selection(&before, 5..12, || Ok(selected.clone())).unwrap_err();
        assert_eq!(error.kind(), io::ErrorKind::Interrupted);
    }
}
