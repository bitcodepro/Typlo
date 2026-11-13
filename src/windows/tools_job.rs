use super::{Target, WinEdit, accessible, active_language, input, process_name, switch_layout};
use crate::{
    config::Config,
    dictionary::Dictionary,
    edit::{Editor, Session, Snapshot},
    hotkeys::Action,
    jobs::exchange,
    layout,
    lexicon::Vocabulary,
    selection_tools::Request,
    typing,
};
use std::{
    io,
    ops::Range,
    path::{Path, PathBuf},
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
        mpsc::{self, Receiver, TryRecvError},
    },
    thread,
};
use windows_sys::Win32::{
    Foundation::HWND,
    UI::WindowsAndMessaging::{PostMessageW, WM_APP},
};

pub const READY: u32 = WM_APP + 8;

pub struct Job {
    canceled: Arc<AtomicBool>,
    reply: Receiver<io::Result<Option<usize>>>,
    apply: Option<Apply>,
}

struct Apply {
    snapshot: Snapshot<Target>,
    range: Range<usize>,
    choices: Vec<crate::lexicon::Candidate>,
    suffix: String,
    sequence: usize,
}

pub struct Context<'a> {
    pub profile: &'a Path,
    pub settings: &'a Config,
    pub corpus: &'a mut Vocabulary,
    pub dictionary: &'a Dictionary,
    pub recent: Option<&'a str>,
    pub last_automatic_micros: Option<u64>,
    pub last_automatic_reason: Option<crate::lexicon::Decision>,
}

impl Job {
    pub fn start(
        host: HWND,
        action: Action,
        mut editor: WinEdit,
        context: Context<'_>,
    ) -> io::Result<Option<Self>> {
        editor.begin_operation();
        let target = editor.target()?;
        let sequence = editor.sequence.unwrap_or_else(input::sequence);
        let source = active_language(target);
        if action == Action::Tools {
            let method = match editor.snapshot() {
                Ok(_) => "Прямая замена Windows Edit".to_owned(),
                Err(error) if error.kind() == io::ErrorKind::Unsupported => String::new(),
                Err(error) => return Err(error),
            };
            return Self::spawn(
                host,
                context.profile.to_owned(),
                target,
                sequence,
                Request::Field {
                    method,
                    last_automatic_micros: context.last_automatic_micros,
                    reason: context.last_automatic_reason,
                },
                None,
            )
            .map(Some);
        }
        let snapshot = editor.snapshot()?;
        let mut range = snapshot.selection.clone();
        if range.is_empty()
            && action == Action::Choices
            && let Some(recent) = context.recent.and_then(typing::last_word)
            && let Some(found) = typing::verified_suffix(&snapshot.document, range.end, recent)
        {
            range = found;
        }
        if range.is_empty() || range.len() > 128 {
            return Ok(None);
        }
        let text =
            String::from_utf16(&snapshot.document[range.clone()]).map_err(io::Error::other)?;
        let word = text.trim();
        if word.is_empty() || word.chars().any(char::is_whitespace) {
            return Ok(None);
        }
        if action == Action::AddWord {
            let language = layout::detect(word, &layout::Language::ALL, source)
                .and_then(|language| language.dictionary_index())
                .unwrap_or(0);
            if editor.snapshot()? != snapshot {
                return Ok(None);
            }
            return Self::spawn(
                host,
                context.profile.to_owned(),
                target,
                sequence,
                Request::Add {
                    word: word.to_owned(),
                    language,
                },
                None,
            )
            .map(Some);
        }
        let Some(source) = source else {
            return Ok(None);
        };
        let process = process_name(target.pid).unwrap_or_default();
        let choices = context.corpus.options(
            context.dictionary,
            word,
            source,
            context.settings.cycle_for(&process),
        )?;
        let Some(suffix) = text.strip_prefix(word).map(str::to_owned) else {
            return Ok(None);
        };
        if editor.snapshot()? != snapshot {
            return Ok(None);
        }
        let request = Request::Choices {
            origin: target.foreground as usize,
            choices: choices.clone(),
        };
        Self::spawn(
            host,
            context.profile.to_owned(),
            target,
            sequence,
            request,
            Some(Apply {
                snapshot,
                range,
                choices,
                suffix,
                sequence,
            }),
        )
        .map(Some)
    }

    fn spawn(
        host: HWND,
        profile: PathBuf,
        target: Target,
        sequence: usize,
        mut request: Request,
        apply: Option<Apply>,
    ) -> io::Result<Self> {
        let canceled = Arc::new(AtomicBool::new(false));
        let cancel_worker = Arc::clone(&canceled);
        let (sender, reply) = mpsc::sync_channel(1);
        let host = host as usize;
        let context = (
            target.foreground as usize,
            target.edit as usize,
            target.thread,
            target.pid,
        );
        thread::Builder::new()
            .name("typlo-selection-tools".into())
            .spawn(move || {
                let (foreground, edit, thread, pid) = context;
                let target = Target { foreground: foreground as HWND, edit: edit as HWND, thread, pid };
                let editor = || WinEdit { expected: Some(target), sequence: Some(sequence), ..Default::default() };
                let result = (|| {
                    if editor().target().is_err() { return Ok(None); }
                    if let Request::Field { method, .. } = &mut request && method.is_empty() {
                        *method = match accessible::check_focused(editor()) {
                            Ok(true) => "Выделение проверено через UI Automation. Доступна замена через буфер обмена.",
                            Ok(false) => "UI Automation доступен. Выделите текст для замены.",
                            Err(_) => "Поле не предоставило проверяемый диапазон текста.",
                        }.into();
                    }
                    if editor().target().is_err() { return Ok(None); }
                    if let Request::Choices { origin, choices } = request {
                        return super::choice::wait(host, origin, sequence, choices, &cancel_worker);
                    }
                    std::env::current_exe().and_then(|exe| {
                        exchange(
                            &exe.with_file_name("typlo-settings.exe"),
                            "--selection-tools",
                            &profile,
                            &request,
                            None,
                            &cancel_worker,
                        )
                    })
                })();
                if sender.send(result).is_ok() {
                    unsafe {
                        PostMessageW(host as HWND, READY, 0, 0);
                    }
                }
            })?;
        Ok(Self {
            canceled,
            reply,
            apply,
        })
    }

    pub fn finish(&self, session: &mut Session<Target>, switch: bool) -> io::Result<bool> {
        let reply = match self.reply.try_recv() {
            Ok(reply) => reply?,
            Err(TryRecvError::Empty) => return Ok(false),
            Err(TryRecvError::Disconnected) => {
                return Err(io::Error::other("Selection tools disconnected"));
            }
        };
        if let (Some(apply), Some(index)) = (&self.apply, reply) {
            let candidate = apply
                .choices
                .get(index)
                .ok_or_else(|| io::Error::other("Invalid layout choice"))?;
            let mut text = candidate.text.clone();
            text.push_str(&apply.suffix);
            let target = apply.snapshot.target;
            let mut editor = WinEdit {
                expected: Some(target),
                sequence: Some(apply.sequence),
                ..Default::default()
            };
            if session.replace_verified_range(
                &mut editor,
                apply.snapshot.clone(),
                apply.range.clone(),
                &text,
                Some(candidate.language),
            )? && switch
                && editor.target()? == target
            {
                switch_layout(target, candidate.language);
            }
        }
        Ok(true)
    }
}

impl Drop for Job {
    fn drop(&mut self) {
        self.canceled.store(true, Ordering::Release);
    }
}
