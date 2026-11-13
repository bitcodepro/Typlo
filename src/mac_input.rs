use super::{AxEditor, Cf, Keyboard, NULL, Raw, unsupported};
use crate::{
    config,
    edit::{Editor, Session, Snapshot},
    hotkeys::{Action, Chord},
    layout::{self, Language},
    lexicon::Decision,
    snippets::Snippets,
    typing::{self, Input, RecentInput},
};
use std::{
    collections::HashMap,
    ffi::c_void,
    io,
    path::{Path, PathBuf},
    sync::{
        Arc, Mutex, RwLock,
        atomic::{AtomicU8, AtomicUsize, Ordering},
        mpsc::{self, SyncSender},
    },
    time::{Duration, Instant},
};

const KEY_DOWN: u32 = 10;
const KEY_UP: u32 = 11;
const FLAGS: u32 = 12;
const MODIFIER_FLAGS: u64 = 0x001e_0000;
const PAIR: u64 = 0x0006_0000;
const EVENT_TAG: u32 = 42;
const RELEASE_TAG: i64 = 0x5459_504c_4f45_4449;
const QUIT_TAG: i64 = 0x5459_504c_5155_4954;
static EDITING: AtomicU8 = AtomicU8::new(0);

pub(super) struct EditLease;

impl EditLease {
    pub fn acquire() -> io::Result<Self> {
        EDITING
            .compare_exchange(0, 1, Ordering::AcqRel, Ordering::Acquire)
            .map_err(|_| io::Error::from(io::ErrorKind::Interrupted))?;
        Ok(Self)
    }

    pub fn quiet(&self) -> bool {
        EDITING.load(Ordering::Acquire) == 1
    }
}

impl Drop for EditLease {
    fn drop(&mut self) {
        EDITING.store(3, Ordering::Release);
        let _ = post_control(RELEASE_TAG);
    }
}

fn post_control(tag: i64) -> io::Result<()> {
    let event = Cf::take(unsafe { CGEventCreateKeyboardEvent(NULL, 0, true) })?;
    unsafe {
        CGEventSetIntegerValueField(event.get(), EVENT_TAG, tag);
        CGEventPost(1, event.get());
    }
    Ok(())
}

#[link(name = "CoreGraphics", kind = "framework")]
unsafe extern "C" {
    fn CGEventTapCreate(
        location: u32,
        placement: u32,
        options: u32,
        events: u64,
        callback: unsafe extern "C" fn(Raw, u32, Raw, *mut c_void) -> Raw,
        context: *mut c_void,
    ) -> Raw;
    fn CGEventTapEnable(tap: Raw, enable: bool);
    fn CGEventGetFlags(event: Raw) -> u64;
    fn CGEventGetIntegerValueField(event: Raw, field: u32) -> i64;
    fn CGEventSourceFlagsState(state: i32) -> u64;
    fn CGEventCreateCopy(event: Raw) -> Raw;
    fn CGEventCreateKeyboardEvent(source: Raw, key: u16, down: bool) -> Raw;
    fn CGEventSetFlags(event: Raw, flags: u64);
    fn CGEventSetIntegerValueField(event: Raw, field: u32, value: i64);
    fn CGEventPost(location: u32, event: Raw);
    fn CGEventPostToPid(pid: i32, event: Raw);
    fn CGEventTapPostEvent(proxy: Raw, event: Raw);
    fn CGEventKeyboardGetUnicodeString(
        event: Raw,
        capacity: usize,
        length: *mut usize,
        output: *mut u16,
    );
}

pub(super) fn paste_to(pid: i32) -> io::Result<()> {
    const V: u16 = 9;
    const COMMAND: u64 = 1 << 20;
    let down = Cf::take(unsafe { CGEventCreateKeyboardEvent(NULL, V, true) })?;
    let up = Cf::take(unsafe { CGEventCreateKeyboardEvent(NULL, V, false) })?;
    for event in [down, up] {
        unsafe {
            CGEventSetFlags(event.get(), COMMAND);
            CGEventPostToPid(pid, event.get());
        }
    }
    Ok(())
}

#[link(name = "CoreFoundation", kind = "framework")]
unsafe extern "C" {
    fn CFMachPortCreateRunLoopSource(allocator: Raw, port: Raw, order: isize) -> Raw;
    fn CFMachPortInvalidate(port: Raw);
    fn CFRunLoopGetCurrent() -> Raw;
    fn CFRunLoopAddSource(run_loop: Raw, source: Raw, mode: Raw);
    fn CFRunLoopRemoveSource(run_loop: Raw, source: Raw, mode: Raw);
    static kCFRunLoopCommonModes: Raw;
}

#[derive(Clone, Copy)]
enum Operation {
    Command(Action),
    To(Language),
}

#[derive(Default)]
struct Bindings {
    keys: HashMap<(u16, u64), Operation>,
    modifier_cycle: bool,
}

impl Bindings {
    fn new(settings: &config::Config) -> io::Result<Self> {
        let commands = settings.hotkeys.bindings()?;
        let direct = settings.direct_bindings(&commands)?;
        let mut output = Self::default();
        for (action, chord) in commands {
            if action == Action::Switch && chord.key == 0 {
                output.modifier_cycle = true;
            } else if matches!(
                action,
                Action::Switch
                    | Action::Convert
                    | Action::Case
                    | Action::Transliterate
                    | Action::English
                    | Action::Russian
                    | Action::Ukrainian
                    | Action::Spelling
                    | Action::SpellingReview
                    | Action::Undo
                    | Action::AddWord
                    | Action::Choices
                    | Action::AcceptChoice
                    | Action::CancelChoice
                    | Action::History
                    | Action::Tools
            ) {
                output.bind(chord, Operation::Command(action))?;
            }
        }
        for (layout, chord) in direct {
            output.bind(chord, Operation::To(layout))?;
        }
        Ok(output)
    }

    fn bind(&mut self, chord: Chord, command: Operation) -> io::Result<()> {
        let Some(key) = mac_key(chord.key) else {
            return Ok(());
        };
        let mut flags = 0;
        for (bit, flag) in [(1, 0x80000), (2, 0x40000), (4, 0x20000), (8, 0x100000)] {
            if chord.modifiers & bit != 0 {
                flags |= flag;
            }
        }
        if self.keys.insert((key, flags), command).is_some() {
            return Err(io::Error::other("Duplicate macOS shortcut"));
        }
        Ok(())
    }
}

fn mac_key(key: u32) -> Option<u16> {
    Some(match key {
        0x41..=0x5a => [
            0, 11, 8, 2, 14, 3, 5, 4, 34, 38, 40, 37, 46, 45, 31, 35, 12, 15, 1, 17, 32, 9, 13, 7,
            16, 6,
        ][(key - 0x41) as usize],
        0x30..=0x39 => [29, 18, 19, 20, 21, 23, 22, 26, 28, 25][(key - 0x30) as usize],
        0x70..=0x83 => [
            122, 120, 99, 118, 96, 97, 98, 100, 101, 109, 103, 111, 105, 107, 113, 106, 64, 79, 80,
            90,
        ][(key - 0x70) as usize],
        0x20 => 49,
        0x09 => 48,
        0x0d => 36,
        0x1b => 53,
        0x25 => 123,
        0x26 => 126,
        0x27 => 124,
        0x28 => 125,
        0x24 => 115,
        0x23 => 119,
        0x2d => 114,
        0x2e => 117,
        0xdb => 33,
        0xdd => 30,
        0xbc => 43,
        0xbe => 47,
        _ => return None,
    })
}

struct Tap {
    bindings: Arc<RwLock<Bindings>>,
    revision: Arc<AtomicUsize>,
    sender: SyncSender<Message>,
    swallowed: [bool; 128],
    pair: bool,
    other_key: bool,
    port: Raw,
    buffered: Vec<Cf>,
}

enum Message {
    Command(Operation, usize),
    Key(Input, i32, usize),
}

fn unicode(event: Raw, key: u16, modifiers: u64) -> Input {
    if modifiers & !0x20000 != 0 {
        return Input::Reset;
    }
    if key == 51 {
        return Input::Backspace;
    }
    let mut data = [0u16; 2];
    let mut length = 0;
    unsafe { CGEventKeyboardGetUnicodeString(event, data.len(), &mut length, data.as_mut_ptr()) };
    if (1..=data.len()).contains(&length) {
        let mut characters = char::decode_utf16(data[..length].iter().copied());
        if let Some(Ok(character)) = characters.next()
            && characters.next().is_none()
            && !character.is_control()
        {
            return Input::Typed(character);
        }
    }
    Input::Reset
}

fn replay(state: &mut Tap, proxy: Raw) {
    if !state.buffered.is_empty() {
        state.revision.fetch_add(1, Ordering::AcqRel);
        for event in state.buffered.drain(..) {
            unsafe { CGEventTapPostEvent(proxy, event.get()) };
        }
    }
    EDITING.store(0, Ordering::Release);
}

unsafe extern "C" fn callback(proxy: Raw, kind: u32, event: Raw, context: *mut c_void) -> Raw {
    // The run loop owns Tap for the entire lifetime of this callback.
    let state = unsafe { &mut *context.cast::<Tap>() };
    if kind >= u32::MAX - 1 {
        state.swallowed.fill(false);
        state.pair = false;
        state.other_key = false;
        state.revision.fetch_add(1, Ordering::AcqRel);
        unsafe { CGEventTapEnable(state.port, true) };
        replay(state, proxy);
        return event;
    }
    let keyboard = matches!(kind, KEY_DOWN | KEY_UP | FLAGS);
    if keyboard && unsafe { CGEventGetIntegerValueField(event, EVENT_TAG) } == QUIT_TAG {
        super::tray::request_stop();
        return NULL;
    }
    if keyboard && unsafe { CGEventGetIntegerValueField(event, EVENT_TAG) } == RELEASE_TAG {
        if EDITING.load(Ordering::Acquire) == 3 {
            replay(state, proxy);
        }
        return NULL;
    }
    if EDITING.load(Ordering::Acquire) == 3 {
        replay(state, proxy);
    }
    if keyboard
        && matches!(EDITING.load(Ordering::Acquire), 1 | 2)
        && let Ok(copy) = Cf::take(unsafe { CGEventCreateCopy(event) })
    {
        let _ = EDITING.compare_exchange(1, 2, Ordering::AcqRel, Ordering::Acquire);
        state.buffered.push(copy);
        return NULL;
    }
    let flags = unsafe { CGEventGetFlags(event) } & MODIFIER_FLAGS;
    if kind == FLAGS {
        state.pair |= flags == PAIR && !state.other_key;
        if flags & !PAIR != 0 {
            state.other_key = true;
        }
        if flags == 0 {
            if state.pair
                && !state.other_key
                && state
                    .bindings
                    .try_read()
                    .is_ok_and(|settings| settings.modifier_cycle)
            {
                let revision = state
                    .revision
                    .fetch_add(1, Ordering::AcqRel)
                    .wrapping_add(1);
                let _ = state.sender.try_send(Message::Command(
                    Operation::Command(Action::Switch),
                    revision,
                ));
            }
            state.pair = false;
            state.other_key = false;
        }
        return event;
    }
    if !matches!(kind, KEY_DOWN | KEY_UP) {
        state.other_key |= flags != 0;
        let revision = state
            .revision
            .fetch_add(1, Ordering::AcqRel)
            .wrapping_add(1);
        let _ = state
            .sender
            .try_send(Message::Key(Input::Reset, 0, revision));
        return event;
    }
    let Ok(key) = u16::try_from(unsafe { CGEventGetIntegerValueField(event, 9) }) else {
        return event;
    };
    let Some(swallowed) = state.swallowed.get_mut(key as usize) else {
        return event;
    };
    if kind == KEY_UP {
        return if std::mem::take(swallowed) {
            NULL
        } else {
            event
        };
    }
    state.other_key |= flags != 0;
    if *swallowed {
        return NULL;
    }
    let revision = state
        .revision
        .fetch_add(1, Ordering::AcqRel)
        .wrapping_add(1);
    let command = state
        .bindings
        .try_read()
        .ok()
        .and_then(|settings| settings.keys.get(&(key, flags)).copied());
    if let Some(command) = command
        && state
            .sender
            .try_send(Message::Command(command, revision))
            .is_ok()
    {
        *swallowed = true;
        return NULL;
    }
    let pid = i32::try_from(unsafe { CGEventGetIntegerValueField(event, 40) }).unwrap_or_default();
    let _ = state
        .sender
        .try_send(Message::Key(unicode(event, key, flags), pid, revision));
    event
}

pub(super) fn run(
    profile: &Path,
    ready: Option<&Path>,
    shutdown: crate::unix_desktop::Shutdown,
) -> io::Result<()> {
    AxEditor::new()?;
    let settings = config::load(profile)?;
    let bindings = Arc::new(RwLock::new(Bindings::new(&settings)?));
    let revision = Arc::new(AtomicUsize::new(0));
    let (sender, receiver) = mpsc::sync_channel(64);
    let history = Arc::new(Mutex::new(None));
    let tray = super::tray::Tray::new(profile, Arc::clone(&history))?;
    let mut context = Tap {
        bindings: Arc::clone(&bindings),
        revision: Arc::clone(&revision),
        sender,
        swallowed: [false; 128],
        pair: false,
        other_key: false,
        port: NULL,
        buffered: Vec::new(),
    };
    let events = [KEY_DOWN, KEY_UP, FLAGS, 1, 3, 25]
        .into_iter()
        .fold(0, |mask, kind| mask | (1u64 << kind));
    let port = Cf::take(unsafe {
        CGEventTapCreate(1, 0, 0, events, callback, (&mut context as *mut Tap).cast())
    })
    .map_err(|_| {
        io::Error::new(
            io::ErrorKind::PermissionDenied,
            "Allow Typlo Rust in Accessibility and Input Monitoring",
        )
    })?;
    context.port = port.get();
    let source = Cf::take(unsafe { CFMachPortCreateRunLoopSource(NULL, port.get(), 0) })?;
    crate::unix_clipboard::start(profile)?;
    let profile = profile.to_owned();
    let worker = std::thread::Builder::new()
        .name("typlo-macos-input".into())
        .spawn(move || {
            if let Err(error) = process(
                profile, settings, bindings, revision, receiver, history, shutdown,
            ) {
                eprintln!("Typlo macOS: {error}");
            }
        })?;
    let run_loop = unsafe { CFRunLoopGetCurrent() };
    unsafe {
        CFRunLoopAddSource(run_loop, source.get(), kCFRunLoopCommonModes);
        CGEventTapEnable(port.get(), true);
    }
    let ready_result = ready.map_or(Ok(()), |path| std::fs::write(path, b"ready"));
    if ready_result.is_ok() {
        tray.run();
    }
    unsafe {
        CFMachPortInvalidate(port.get());
        CFRunLoopRemoveSource(run_loop, source.get(), kCFRunLoopCommonModes);
    }
    drop(context);
    let _ = worker.join();
    ready_result
}

fn process(
    profile: PathBuf,
    mut settings: config::Config,
    bindings: Arc<RwLock<Bindings>>,
    revision: Arc<AtomicUsize>,
    receiver: mpsc::Receiver<Message>,
    history: super::tray::HistoryWindow,
    shutdown: crate::unix_desktop::Shutdown,
) -> io::Result<()> {
    let dictionary = crate::dictionary::Dictionary::new(&profile);
    let mut stored = dictionary.read_settings()?;
    let mut last_check = Instant::now();
    let mut editor = AxEditor::new()?;
    let mut keyboard = Keyboard::load()?;
    let mut inhibit = crate::unix_desktop::Inhibit::default();
    if let Err(error) = inhibit.apply(settings.keep_screen_on) {
        eprintln!("Typlo desktop: {error}");
    }
    let mut session = Session::default();
    let mut spelling: Option<SpellingJob> = None;
    let mut tools: Option<ToolJob> = None;
    let mut last_automatic = None;
    let mut foreground = String::new();
    let mut recent = RecentInput::default();
    let mut last_input = 0usize;
    let mut automatic = None;
    let groups = || {
        Keyboard::load().map(|keyboard| {
            keyboard
                .groups
                .into_iter()
                .map(|(_, _, layout)| layout)
                .collect::<Vec<_>>()
        })
    };
    let mut snippets = Snippets::compile_for(&settings.snippets, &groups()?)?;
    let mut vocabulary = None;
    loop {
        if shutdown.requested() {
            return post_control(QUIT_TAG);
        }
        let wait = if spelling.is_some() || automatic.is_some() || tools.is_some() {
            20
        } else {
            200
        };
        match receiver.recv_timeout(Duration::from_millis(wait)) {
            Ok(Message::Key(input, pid, sequence)) => {
                automatic = None;
                if matches!(tools, Some(ToolJob::Choice(_))) {
                    tools = None;
                }
                let pid = match (pid, input) {
                    (_, Input::Reset) => None,
                    (pid, _) if pid > 0 => Some(pid),
                    _ if revision.load(Ordering::Acquire) == sequence => editor.pid().ok(),
                    _ => None,
                };
                let now = Instant::now();
                recent.push(pid, sequence, input, now);
                last_input = sequence;
                if let Some(pid) = pid
                    && let Some(text) = recent.text(&pid, sequence, now)
                {
                    let word = matches!(input, Input::Typed(' '))
                        && (settings.auto_correct
                            || settings
                                .applications
                                .iter()
                                .any(|rule| rule.auto_correct == Some(true)));
                    if word || (!snippets.is_empty() && snippets.matches(text).is_some()) {
                        automatic = Some((pid, sequence, now + Duration::from_millis(30)));
                    }
                }
            }
            Ok(Message::Command(command, expected)) => {
                automatic = None;
                let deadline = Instant::now() + Duration::from_millis(300);
                while unsafe { CGEventSourceFlagsState(0) } & MODIFIER_FLAGS != 0
                    && Instant::now() < deadline
                    && revision.load(Ordering::Acquire) == expected
                {
                    std::thread::sleep(Duration::from_millis(6));
                }
                if unsafe { CGEventSourceFlagsState(0) } & MODIFIER_FLAGS != 0
                    || revision.load(Ordering::Acquire) != expected
                {
                    continue;
                }
                if !matches!(
                    command,
                    Operation::Command(
                        Action::Choices | Action::AcceptChoice | Action::CancelChoice
                    )
                ) && matches!(tools, Some(ToolJob::Choice(_)))
                {
                    tools = None;
                }
                if matches!(command, Operation::Command(Action::History)) {
                    let origin = super::history_origin();
                    if let Ok(mut history) = history.lock()
                        && let Err(error) = crate::unix_clipboard::history_action_with_origin(
                            &profile,
                            &mut history,
                            origin,
                        )
                    {
                        eprintln!("Typlo clipboard: {error}");
                    }
                    continue;
                }
                editor.guard = Some((Arc::clone(&revision), expected));
                editor.recent =
                    if settings.convert_last_word && expected == last_input.wrapping_add(1) {
                        editor
                            .pid()
                            .ok()
                            .and_then(|pid| recent.text(&pid, last_input, Instant::now()))
                            .and_then(typing::last_word)
                            .map(str::to_owned)
                    } else {
                        None
                    };
                recent.reset();
                let result = if matches!(command, Operation::Command(Action::CancelChoice)) {
                    if matches!(tools, Some(ToolJob::Choice(_))) {
                        tools = None;
                    }
                    Ok(())
                } else if matches!(command, Operation::Command(Action::AcceptChoice)) {
                    if matches!(tools, Some(ToolJob::Choice(_))) {
                        let Some(ToolJob::Choice(choice)) = tools.take() else {
                            unreachable!();
                        };
                        choice.apply(&mut editor, &mut session, &keyboard, settings.switch_layout)
                    } else {
                        Ok(())
                    }
                } else if matches!(command, Operation::Command(Action::Choices))
                    && let Some(ToolJob::Choice(choice)) = &mut tools
                    && choice.current(&editor)
                {
                    choice.preview.advance();
                    Ok(())
                } else if let Operation::Command(
                    action @ (Action::Spelling | Action::SpellingReview),
                ) = command
                {
                    spelling = None;
                    SpellingJob::start(
                        &profile,
                        &mut editor,
                        action == Action::SpellingReview,
                        expected,
                    )
                    .map(|job| spelling = job)
                } else if let Operation::Command(
                    action @ (Action::Tools | Action::AddWord | Action::Choices),
                ) = command
                {
                    tools = None;
                    ToolJob::start(
                        &profile,
                        &settings,
                        &keyboard,
                        &mut editor,
                        action,
                        last_automatic,
                    )
                    .map(|job| tools = job)
                } else {
                    execute(&settings, &keyboard, &mut editor, &mut session, command)
                };
                editor.lease = None;
                editor.recent = None;
                if let Err(error) = result
                    && !matches!(
                        error.kind(),
                        io::ErrorKind::Interrupted | io::ErrorKind::Unsupported
                    )
                {
                    eprintln!("Typlo macOS command: {error}");
                }
            }
            Err(mpsc::RecvTimeoutError::Disconnected) => return Ok(()),
            Err(mpsc::RecvTimeoutError::Timeout) if !settings.applications.is_empty() => {
                editor.guard = None;
                if let Ok(process) = editor.process()
                    && process != foreground
                {
                    if let Some(language) = settings.layout_on_entry(&process, keyboard.current())
                        && editor.process().ok().as_ref() == Some(&process)
                    {
                        keyboard.select(language)?;
                    }
                    foreground = process;
                }
            }
            Err(mpsc::RecvTimeoutError::Timeout) => {}
        }
        if let Some((pid, sequence, time)) = automatic
            && Instant::now() >= time
        {
            automatic = None;
            if revision.load(Ordering::Acquire) == sequence
                && editor.pid().ok() == Some(pid)
                && let Some(text) = recent.text(&pid, sequence, Instant::now())
            {
                editor.guard = Some((Arc::clone(&revision), sequence));
                let result = apply_typing(
                    &settings,
                    &keyboard,
                    &mut editor,
                    &mut session,
                    (&snippets, text),
                    &dictionary,
                    &mut vocabulary,
                );
                editor.lease = None;
                match result {
                    Ok((applied, report)) => {
                        if report.is_some() {
                            last_automatic = report;
                        }
                        if applied {
                            recent.reset();
                        }
                    }
                    Err(error)
                        if !matches!(
                            error.kind(),
                            io::ErrorKind::Unsupported | io::ErrorKind::Interrupted
                        ) =>
                    {
                        eprintln!("Typlo macOS automatic edit: {error}");
                    }
                    Err(_) => {}
                }
            }
        }
        if spelling
            .as_ref()
            .is_some_and(|job| !job.review && job.revision != revision.load(Ordering::Acquire))
        {
            spelling = None;
        }
        if let Some(result) = spelling.as_ref().and_then(|pending| pending.job.poll()) {
            let pending = spelling.take().expect("spelling result has an owner");
            let latest = revision.load(Ordering::Acquire);
            editor.guard = Some((Arc::clone(&revision), latest));
            let applied = result.and_then(|result| {
                if let (Some(snapshot), Some(result)) = (pending.snapshot, result) {
                    session.replace_selection(&mut editor, snapshot, &result)?;
                }
                Ok(())
            });
            editor.lease = None;
            if let Err(error) = applied
                && !matches!(
                    error.kind(),
                    io::ErrorKind::Interrupted | io::ErrorKind::Unsupported
                )
            {
                eprintln!("Typlo macOS spelling: {error}");
            }
        }
        if matches!(&tools, Some(ToolJob::Choice(choice)) if !choice.current(&editor)) {
            tools = None;
        }
        if let Some(ToolJob::Dialog(job)) = &tools
            && let Some(result) = job.poll()
        {
            tools = None;
            if let Err(error) = result
                && !matches!(
                    error.kind(),
                    io::ErrorKind::Interrupted | io::ErrorKind::Unsupported
                )
            {
                eprintln!("Typlo macOS selection tools: {error}");
            }
        }
        if last_check.elapsed() >= Duration::from_secs(2) {
            last_check = Instant::now();
            let next = dictionary.read_settings()?;
            if next != stored {
                if let Ok(next) = config::load(&profile)
                    && let Ok(shortcuts) = Bindings::new(&next)
                    && let Ok(mut current) = bindings.write()
                {
                    *current = shortcuts;
                    if matches!(tools, Some(ToolJob::Choice(_))) {
                        tools = None;
                    }
                    settings = next;
                    snippets = Snippets::compile_for(&settings.snippets, &groups()?)?;
                    if let Err(error) = inhibit.apply(settings.keep_screen_on) {
                        eprintln!("Typlo desktop: {error}");
                    }
                }
                stored = next;
            }
            keyboard = Keyboard::load().unwrap_or(keyboard);
        }
    }
}

struct SpellingJob {
    job: crate::jobs::Task<Option<String>>,
    snapshot: Option<Snapshot<Cf>>,
    revision: usize,
    review: bool,
}

type AutoReport = Option<(u64, Decision)>;

enum ToolJob {
    Dialog(crate::jobs::Task<()>),
    Choice(ToolChoices),
}

struct ToolChoices {
    pid: i32,
    original: Snapshot<Cf>,
    range: std::ops::Range<usize>,
    preview: crate::jobs::ChoicePreview,
    suffix: String,
}

impl ToolJob {
    fn start(
        profile: &Path,
        settings: &config::Config,
        keyboard: &Keyboard,
        editor: &mut AxEditor,
        action: Action,
        automatic: AutoReport,
    ) -> io::Result<Option<Self>> {
        use crate::selection_tools::Request;
        let original = editor.snapshot();
        let request = if action == Action::Tools {
            let method = if original.is_ok() {
                "Доступна прямая замена через macOS Accessibility."
            } else {
                "Поле не предоставило проверяемый диапазон текста."
            };
            Request::Field {
                method: method.into(),
                last_automatic_micros: automatic.map(|(time, _)| time),
                reason: automatic.map(|(_, decision)| decision),
            }
        } else {
            let original = original?;
            let range = if action == Action::Choices && original.selection.is_empty() {
                editor
                    .recent_range(&original)
                    .unwrap_or_else(|| original.selection.clone())
            } else {
                original.selection.clone()
            };
            if range.is_empty() || range.len() > 128 {
                return Ok(None);
            }
            let text =
                String::from_utf16(&original.document[range.clone()]).map_err(io::Error::other)?;
            let word = text.trim_end_matches(' ');
            if word.is_empty() || word.chars().any(char::is_whitespace) {
                return Ok(None);
            }
            let source = keyboard.current();
            let catalog: Vec<_> = keyboard
                .groups
                .iter()
                .map(|(_, _, layout)| *layout)
                .collect();
            if action == Action::AddWord {
                let language = layout::detect(word, &catalog, source)
                    .and_then(Language::dictionary_index)
                    .unwrap_or_default();
                Request::Add {
                    word: word.to_owned(),
                    language,
                }
            } else {
                let Some(source) = source else {
                    return Ok(None);
                };
                let dictionary = crate::dictionary::Dictionary::new(profile);
                let process = editor.process()?;
                let choices = crate::lexicon::Vocabulary::installed()?.options(
                    &dictionary,
                    word,
                    source,
                    settings.cycle_for(&process),
                )?;
                if !editor.quiet() {
                    return Ok(None);
                }
                let pid = editor.pid()?;
                return Ok(
                    crate::jobs::ChoicePreview::start(profile, choices)?.map(|preview| {
                        Self::Choice(ToolChoices {
                            pid,
                            original,
                            range,
                            preview,
                            suffix: text[word.len()..].to_owned(),
                        })
                    }),
                );
            }
        };
        if !editor.quiet() {
            return Ok(None);
        }
        let profile = profile.to_owned();
        let job = crate::jobs::Task::start(move |canceled| {
            crate::jobs::exchange(
                &crate::jobs::gui()?,
                "--selection-tools",
                &profile,
                &request,
                None,
                canceled,
            )
        })?;
        Ok(Some(Self::Dialog(job)))
    }
}

impl ToolChoices {
    fn current(&self, editor: &AxEditor) -> bool {
        self.preview.alive() && editor.pid().ok() == Some(self.pid)
    }

    fn apply(
        self,
        editor: &mut AxEditor,
        session: &mut Session<Cf>,
        keyboard: &Keyboard,
        switch: bool,
    ) -> io::Result<()> {
        if self.current(editor) {
            let candidate = self.preview.selected();
            let text = format!("{}{}", candidate.text, self.suffix);
            if session.replace_verified_range(
                editor,
                self.original,
                self.range,
                &text,
                Some(candidate.language),
            )? && switch
                && editor.quiet()
                && editor.lease.as_ref().is_none_or(EditLease::quiet)
            {
                keyboard.select(candidate.language)?;
            }
        }
        Ok(())
    }
}

fn apply_typing(
    settings: &config::Config,
    keyboard: &Keyboard,
    editor: &mut AxEditor,
    session: &mut Session<Cf>,
    input: (&Snippets, &str),
    dictionary: &crate::dictionary::Dictionary,
    vocabulary: &mut Option<crate::lexicon::Vocabulary>,
) -> io::Result<(bool, AutoReport)> {
    let (snippets, text) = input;
    let process = editor.process()?;
    if settings.snippets_for(&process)
        && let Some((trigger, template)) = snippets.matches(text)
        && session.expand_snippet(editor, trigger, &template.with_lf())?
    {
        return Ok((true, None));
    }
    let Some(word) = typing::last_word(text).filter(|word| word.ends_with(' ')) else {
        return Ok((false, None));
    };
    if !settings.auto_correct_for(&process) {
        return Ok((false, None));
    }
    let Some(source) = keyboard.current() else {
        return Ok((false, None));
    };
    if vocabulary.is_none() {
        *vocabulary = Some(crate::lexicon::Vocabulary::installed()?);
    }
    let started = Instant::now();
    let mut reason = Decision::Unverified;
    let target = session.correct_word(editor, word, |token| {
        let (suggestion, decision) = vocabulary
            .as_mut()
            .expect("initialized vocabulary")
            .decision(
                dictionary,
                token,
                source,
                settings.cycle_for(&process),
                settings.protect_technical,
            )?;
        reason = decision;
        Ok(suggestion)
    })?;
    if let Some(target) = target {
        if settings.switch_layout
            && editor.quiet()
            && editor.lease.as_ref().is_none_or(EditLease::quiet)
        {
            keyboard.select(target)?;
        }
        return Ok((
            true,
            Some((started.elapsed().as_micros() as u64, Decision::Applied)),
        ));
    }
    Ok((false, Some((started.elapsed().as_micros() as u64, reason))))
}

impl SpellingJob {
    fn start(
        profile: &Path,
        editor: &mut AxEditor,
        review: bool,
        revision: usize,
    ) -> io::Result<Option<Self>> {
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
        if snapshot.is_none() && !review {
            return Ok(None);
        }
        let text = snapshot
            .as_ref()
            .map(|snapshot| String::from_utf16(&snapshot.document[snapshot.selection.clone()]))
            .transpose()
            .map_err(io::Error::other)?
            .unwrap_or_default();
        let origin = if snapshot.is_some() {
            editor.pid()? as usize
        } else {
            0
        };
        let profile = profile.to_owned();
        let job = crate::jobs::Task::start(move |canceled| {
            crate::jobs::correct(&profile, text, origin, review, canceled)
        })?;
        Ok(Some(Self {
            job,
            snapshot,
            revision,
            review,
        }))
    }
}

fn execute(
    settings: &config::Config,
    keyboard: &Keyboard,
    editor: &mut AxEditor,
    session: &mut Session<Cf>,
    command: Operation,
) -> io::Result<()> {
    let process = editor.process()?;
    let active = keyboard.current();
    let groups: Vec<_> = settings
        .cycle_for(&process)
        .iter()
        .copied()
        .filter(|language| {
            keyboard
                .groups
                .iter()
                .any(|(_, _, installed)| installed == language)
        })
        .collect();
    if matches!(command, Operation::Command(Action::Switch)) {
        if let Some(language) = layout::next_language(&groups, active)
            && editor.quiet()
        {
            keyboard.select(language)?;
        }
        return Ok(());
    }
    let language = match command {
        Operation::Command(Action::Convert) => session.convert(editor, &groups, active)?,
        Operation::Command(Action::Undo) => {
            session.undo(editor)?;
            None
        }
        Operation::Command(Action::Case) => session.transform(editor, |text, _| {
            Some((crate::text_tools::cycle_case(text), None))
        })?,
        Operation::Command(Action::Transliterate) => session.transform(editor, |text, _| {
            Some((crate::text_tools::transliterate(text), None))
        })?,
        _ => {
            let target = match command {
                Operation::To(language) => {
                    Some(language).filter(|language| groups.contains(language))
                }
                Operation::Command(action) => {
                    let name = match action {
                        Action::English => "en",
                        Action::Russian => "ru",
                        Action::Ukrainian => "uk",
                        _ => return Err(unsupported()),
                    };
                    super::target(name, &groups, active).ok()
                }
            };
            match target {
                Some(target) => session.convert_to(editor, target, active)?,
                None => None,
            }
        }
    };
    if let Some(language) = language
        && settings.switch_layout
        && editor.quiet()
        && editor.lease.as_ref().is_none_or(EditLease::quiet)
    {
        keyboard.select(language)?;
    }
    Ok(())
}
