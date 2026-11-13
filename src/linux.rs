mod accessible;
mod gnome;
mod keyboard;
mod tray;

use crate::{
    config,
    edit::{Editor, Snapshot},
    hotkeys::{Action, Chord},
    layout::{self, Language},
    snippets::Snippets,
    typing::{self, Input, RecentInput},
};
use keyboard::Keyboard;
use std::{
    collections::{HashMap, HashSet},
    io,
    os::fd::{AsRawFd, RawFd},
    path::Path,
    process::{Child, Command, Stdio},
    rc::Rc,
    sync::{LazyLock, Mutex},
    time::{Duration, Instant},
};
use x11rb::{
    connection::Connection,
    protocol::{
        Event,
        res::{self, ConnectionExt as _},
        xinput::{self, ConnectionExt as _},
        xkb::{self, ConnectionExt as _},
        xproto::{AtomEnum, ConnectionExt as _, EventMask, GrabMode, InputFocus, ModMask, Window},
    },
    xcb_ffi::XCBConnection,
};

pub(crate) fn err(error: impl Into<Box<dyn std::error::Error + Send + Sync>>) -> io::Error {
    io::Error::other(error)
}

fn connect() -> io::Result<(XCBConnection, Window)> {
    if std::env::var_os("DISPLAY").is_none() || crate::unix_clipboard::wayland() {
        return Err(io::Error::new(
            io::ErrorKind::Unsupported,
            "An X11 session is required",
        ));
    }
    let (conn, screen) = XCBConnection::connect(None).map_err(err)?;
    let root = conn.setup().roots[screen].root;
    Ok((conn, root))
}

type CachedLayouts = Mutex<Option<(Instant, Vec<Language>)>>;

pub fn available_layouts() -> io::Result<Vec<Language>> {
    static CACHE: LazyLock<CachedLayouts> = LazyLock::new(Mutex::default);
    let mut cached = CACHE
        .lock()
        .map_err(|_| io::Error::other("XKB cache unavailable"))?;
    if let Some((time, groups)) = &*cached
        && time.elapsed() < Duration::from_secs(2)
    {
        return Ok(groups.clone());
    }
    let groups = if crate::unix_clipboard::wayland() && gnome::session() {
        keyboard::configured()?
            .into_iter()
            .map(|(_, language)| language)
            .collect()
    } else {
        let (conn, root) = connect()?;
        Keyboard::load(&conn, root)?.catalog
    };
    if groups.is_empty() {
        return Err(io::Error::other("No configured XKB layouts"));
    }
    *cached = Some((Instant::now(), groups.clone()));
    Ok(groups)
}

pub fn active_layout() -> io::Result<Option<Language>> {
    let (conn, root) = connect()?;
    Keyboard::load(&conn, root)?.current(&conn)
}

#[cfg(feature = "gui")]
pub(crate) fn focus_window(window: Window) -> io::Result<()> {
    let (conn, _) = connect()?;
    conn.set_input_focus(InputFocus::PARENT, window, x11rb::CURRENT_TIME)
        .map_err(err)?
        .check()
        .map_err(err)
}

#[cfg(feature = "gui")]
pub(crate) fn paste_history(window: Window, pid: u32) -> io::Result<()> {
    use x11rb::protocol::{
        xproto::{KEY_PRESS_EVENT, KEY_RELEASE_EVENT},
        xtest::ConnectionExt as _,
    };

    let desktop = Desktop::open()?;
    let conn = &desktop.conn;
    let control = desktop
        .keyboard
        .symbol(0xffe3)
        .ok_or_else(accessible::unsupported)?;
    let paste = desktop
        .keyboard
        .key(u32::from(b'V'))
        .ok_or_else(accessible::unsupported)?;
    conn.xtest_get_version(2, 2)
        .map_err(err)?
        .reply()
        .map_err(err)?;
    let atom = conn
        .intern_atom(false, b"CLIPBOARD")
        .map_err(err)?
        .reply()
        .map_err(err)?
        .atom;
    conn.grab_server().map_err(err)?.check().map_err(err)?;
    let operation = (|| {
        let own_pid = Some(std::process::id());
        let focus = desktop.focus()?.ok_or_else(accessible::unsupported)?;
        let owner = conn
            .get_selection_owner(atom)
            .map_err(err)?
            .reply()
            .map_err(err)?
            .owner;
        if desktop.pid(focus)? != own_pid
            || desktop.pid(window)? != Some(pid)
            || owner == 0
            || desktop.pid(owner)? != own_pid
            || conn.query_keymap().map_err(err)?.reply().map_err(err)?.keys != [0; 32]
        {
            return Err(io::Error::other(
                "Focus, keys or clipboard changed before paste",
            ));
        }
        conn.set_input_focus(InputFocus::PARENT, window, x11rb::CURRENT_TIME)
            .map_err(err)?
            .check()
            .map_err(err)?;
        if desktop.focus()? != Some(window) {
            return Err(io::Error::other(
                "The original window did not receive focus",
            ));
        }
        // Queue both releases before checking the requests so keys cannot remain pressed.
        let requests = [
            conn.xtest_fake_input(KEY_PRESS_EVENT, control, 0, desktop.root, 0, 0, 0),
            conn.xtest_fake_input(KEY_PRESS_EVENT, paste, 0, desktop.root, 0, 0, 0),
            conn.xtest_fake_input(KEY_RELEASE_EVENT, paste, 0, desktop.root, 0, 0, 0),
            conn.xtest_fake_input(KEY_RELEASE_EVENT, control, 0, desktop.root, 0, 0, 0),
        ];
        for request in requests {
            request.map_err(err)?.check().map_err(err)?;
        }
        Ok(())
    })();
    let release = conn
        .ungrab_server()
        .map_err(err)
        .and_then(|request| request.check().map_err(err));
    operation.and(release)
}

pub fn set_layout(target: &str) -> io::Result<()> {
    let (conn, root) = connect()?;
    let keyboard = Keyboard::load(&conn, root)?;
    let target = match target {
        "en" => keyboard
            .catalog
            .iter()
            .copied()
            .find(|language| language.dictionary_index() == Some(0)),
        "ru" => keyboard
            .catalog
            .iter()
            .copied()
            .find(|language| language.dictionary_index() == Some(1)),
        "uk" | "ua" => keyboard
            .catalog
            .iter()
            .copied()
            .find(|language| language.dictionary_index() == Some(2)),
        "toggle" => layout::next_language(&keyboard.catalog, keyboard.current(&conn)?),
        _ => None,
    }
    .ok_or_else(|| io::Error::other("Requested layout is unavailable"))?;
    if !keyboard.select(&conn, target)? {
        return Err(io::Error::other("XKB did not change the layout"));
    }
    Ok(())
}

#[derive(Clone, Copy)]
enum Binding {
    Action(Action),
    Direct(Language),
}

type Key = (u8, u16);

struct Shortcuts {
    grabs: HashMap<Key, Binding>,
    modifier_cycle: bool,
}

impl Shortcuts {
    fn empty() -> Self {
        Self {
            grabs: HashMap::new(),
            modifier_cycle: false,
        }
    }

    fn apply(&mut self, desktop: &Desktop, settings: &config::Config) -> io::Result<()> {
        let bindings = settings.hotkeys.bindings()?;
        let direct = settings.direct_bindings(&bindings)?;
        let ignored = desktop.modifiers.mask(0xffe5) | desktop.modifiers.mask(0xff7f);
        let modifier_cycle = bindings
            .iter()
            .any(|&(action, chord)| action == Action::Switch && chord.key == 0);
        let mut proposed = HashMap::new();
        let all = bindings
            .into_iter()
            .filter_map(|(action, chord)| {
                matches!(
                    action,
                    Action::Switch
                        | Action::Tools
                        | Action::Convert
                        | Action::Case
                        | Action::Transliterate
                        | Action::History
                        | Action::Spelling
                        | Action::SpellingReview
                        | Action::AddWord
                        | Action::Choices
                        | Action::AcceptChoice
                        | Action::CancelChoice
                        | Action::Undo
                        | Action::English
                        | Action::Russian
                        | Action::Ukrainian
                )
                .then_some((chord, Binding::Action(action)))
            })
            .chain(
                direct
                    .into_iter()
                    .map(|(language, chord)| (chord, Binding::Direct(language))),
            );
        for (chord, command) in all {
            if chord.key == 0 {
                continue;
            }
            let Some(key) = desktop.keyboard.key(chord.key) else {
                continue;
            };
            let mask = desktop.modifiers.for_chord(chord)?;
            let mut locks = ignored;
            loop {
                if proposed.insert((key, mask | locks), command).is_some() {
                    return Err(io::Error::other("Two commands use the same X11 shortcut"));
                }
                if locks == 0 {
                    break;
                }
                locks = (locks - 1) & ignored;
            }
        }
        let mut added = Vec::new();
        for &(key, mask) in proposed.keys().filter(|key| !self.grabs.contains_key(key)) {
            let grabbed = desktop
                .conn
                .grab_key(
                    false,
                    desktop.root,
                    mask.into(),
                    key,
                    GrabMode::ASYNC,
                    GrabMode::ASYNC,
                )
                .map_err(err)
                .and_then(|cookie| cookie.check().map_err(err));
            if let Err(error) = grabbed {
                for (key, mask) in added {
                    let _ = desktop
                        .conn
                        .ungrab_key(key, desktop.root, ModMask::from(mask));
                }
                let _ = desktop.conn.flush();
                return Err(io::Error::other(format!(
                    "Could not grab X11 shortcut key {key}, modifiers {mask}: {error}"
                )));
            }
            added.push((key, mask));
        }
        for &(key, mask) in self.grabs.keys().filter(|key| !proposed.contains_key(key)) {
            desktop
                .conn
                .ungrab_key(key, desktop.root, mask.into())
                .map_err(err)?
                .check()
                .map_err(err)?;
        }
        self.grabs = proposed;
        self.modifier_cycle = modifier_cycle;
        Ok(())
    }

    fn find(&self, code: u8, state: u16) -> Option<Binding> {
        self.grabs.get(&(code, state & 0xff)).copied()
    }
}

struct Modifiers {
    keys: HashMap<u8, u16>,
    symbols: HashMap<u32, u8>,
}

impl Modifiers {
    fn read(conn: &XCBConnection, keyboard: &Keyboard) -> io::Result<Self> {
        let reply = conn
            .get_modifier_mapping()
            .map_err(err)?
            .reply()
            .map_err(err)?;
        let keys = reply
            .keycodes
            .chunks(usize::from(reply.keycodes_per_modifier().max(1)))
            .enumerate()
            .flat_map(|(index, keys)| {
                keys.iter()
                    .copied()
                    .filter(|&key| key != 0)
                    .map(move |key| (key, 1u16 << index))
            })
            .collect();
        let symbols = [
            0xffe1, 0xffe2, 0xffe3, 0xffe4, 0xffe5, 0xffe9, 0xffea, 0xffeb, 0xffec, 0xff7f,
        ]
        .into_iter()
        .filter_map(|symbol| keyboard.symbol(symbol).map(|key| (symbol, key)))
        .collect();
        Ok(Self { keys, symbols })
    }

    fn mask(&self, symbol: u32) -> u16 {
        self.symbols
            .get(&symbol)
            .and_then(|key| self.keys.get(key))
            .copied()
            .unwrap_or_default()
    }

    fn for_chord(&self, chord: Chord) -> io::Result<u16> {
        let mut masks = 0;
        for (bit, symbol) in [(1, 0xffe9), (2, 0xffe3), (4, 0xffe1), (8, 0xffeb)] {
            if chord.modifiers & bit != 0 {
                let mask = self.mask(symbol);
                if mask == 0 {
                    return Err(io::Error::other(format!(
                        "No modifier for X11 keysym {symbol:x}"
                    )));
                }
                masks |= mask;
            }
        }
        Ok(masks)
    }
}

#[derive(Default)]
struct Gesture {
    down: HashSet<u8>,
    armed: bool,
    consumed: bool,
}

impl Gesture {
    fn event(&mut self, code: u8, pressed: bool, modifiers: &Modifiers) -> bool {
        let shift = modifiers.mask(0xffe1);
        let ctrl = modifiers.mask(0xffe3);
        let current = modifiers.keys.get(&code).copied().unwrap_or_default();
        if pressed {
            self.down.insert(code);
            if current != shift && current != ctrl {
                self.consumed = true;
            }
            let mask = self.down.iter().fold(0, |mask, key| {
                mask | modifiers.keys.get(key).copied().unwrap_or_default()
            });
            self.armed |= !self.consumed && shift != 0 && ctrl != 0 && mask == shift | ctrl;
        } else {
            self.down.remove(&code);
            if self.down.is_empty() {
                let run = self.armed && !self.consumed;
                self.armed = false;
                self.consumed = false;
                return run;
            }
        }
        false
    }
}

struct Desktop {
    conn: XCBConnection,
    root: Window,
    keyboard: Keyboard,
    modifiers: Modifiers,
}

impl Desktop {
    fn open() -> io::Result<Self> {
        let (conn, root) = connect()?;
        let keyboard = Keyboard::load(&conn, root)?;
        let modifiers = Modifiers::read(&conn, &keyboard)?;
        Ok(Self {
            conn,
            root,
            keyboard,
            modifiers,
        })
    }

    fn focus(&self) -> io::Result<Option<Window>> {
        let focus = self
            .conn
            .get_input_focus()
            .map_err(err)?
            .reply()
            .map_err(err)?
            .focus;
        Ok(
            (focus > u32::from(u8::from(InputFocus::POINTER_ROOT)) && focus != self.root)
                .then_some(focus),
        )
    }

    fn pid(&self, window: Window) -> io::Result<Option<u32>> {
        let reply = self
            .conn
            .res_query_client_ids(&[res::ClientIdSpec {
                client: window,
                mask: res::ClientIdMask::LOCAL_CLIENT_PID,
            }])
            .map_err(err)?
            .reply()
            .map_err(err)?;
        Ok(reply
            .ids
            .into_iter()
            .find_map(|id| id.value.first().copied().filter(|pid| *pid > 0)))
    }

    fn process(&self, focus: Window) -> io::Result<String> {
        let mut window = focus;
        for _ in 0..24 {
            if window == self.root || window == 0 {
                break;
            }
            let value = self
                .conn
                .get_property(false, window, AtomEnum::WM_CLASS, AtomEnum::STRING, 0, 128)
                .map_err(err)?
                .reply()
                .map_err(err)?
                .value;
            let mut fields = value.split(|&byte| byte == 0);
            let instance = fields.next().unwrap_or_default();
            let class = fields
                .next()
                .filter(|field| !field.is_empty())
                .unwrap_or(instance);
            if !class.is_empty() {
                return Ok(String::from_utf8_lossy(class).into_owned());
            }
            window = self
                .conn
                .query_tree(window)
                .map_err(err)?
                .reply()
                .map_err(err)?
                .parent;
        }
        Ok(String::new())
    }

    fn shortcuts(&self) -> io::Result<()> {
        self.conn
            .xinput_xi_query_version(2, 2)
            .map_err(err)?
            .reply()
            .map_err(err)?;
        self.conn
            .xinput_xi_select_events(
                self.root,
                &[xinput::EventMask {
                    deviceid: xinput::Device::ALL_MASTER.into(),
                    mask: vec![
                        xinput::XIEventMask::RAW_KEY_PRESS
                            | xinput::XIEventMask::RAW_KEY_RELEASE
                            | xinput::XIEventMask::RAW_BUTTON_PRESS,
                    ],
                }],
            )
            .map_err(err)?
            .check()
            .map_err(err)?;
        self.conn
            .xkb_select_events(
                xkb::ID::USE_CORE_KBD.into(),
                xkb::EventType::default(),
                xkb::EventType::STATE_NOTIFY,
                xkb::MapPart::default(),
                xkb::MapPart::default(),
                &xkb::SelectEventsAux::default(),
            )
            .map_err(err)?
            .check()
            .map_err(err)?;
        self.conn
            .change_window_attributes(
                self.root,
                &x11rb::protocol::xproto::ChangeWindowAttributesAux::new()
                    .event_mask(EventMask::PROPERTY_CHANGE),
            )
            .map_err(err)?
            .check()
            .map_err(err)?;
        Ok(())
    }
}

pub fn run(profile: &Path) -> io::Result<()> {
    run_managed(profile, None)
}

pub fn run_managed(profile: &Path, ready: Option<&Path>) -> io::Result<()> {
    let instance = crate::unix_desktop::instance(profile)?;
    let mut tray = tray::Tray::new(profile);
    if crate::unix_clipboard::wayland() {
        return crate::unix_clipboard::watch_wayland(profile, ready, || {
            if instance.signal.requested() {
                return Ok(true);
            }
            match tray.poll() {
                Ok(stop) => Ok(stop),
                Err(error) => {
                    eprintln!("Typlo tray: {error}");
                    Ok(false)
                }
            }
        });
    }
    let mut desktop = Desktop::open()?;
    desktop.shortcuts()?;
    let mut settings = config::load(profile)?;
    let mut inhibit = crate::unix_desktop::Inhibit::default();
    if let Err(error) = inhibit.apply(settings.keep_screen_on) {
        eprintln!("Typlo desktop: {error}");
    }
    let dictionary = crate::dictionary::Dictionary::new(profile);
    let mut settings_blob = dictionary.read_settings()?;
    let mut shortcuts = Shortcuts::empty();
    shortcuts.apply(&desktop, &settings)?;
    crate::unix_clipboard::start(profile)?;
    let mut gesture = Gesture::default();
    let mut focus = None;
    let mut check_settings = Instant::now();
    let mut check_focus = Instant::now();
    let mut history: Option<Child> = None;
    let mut pending = None;
    let mut editor = EditSession::default();
    let mut typing = Typing::new(&desktop, &settings)?;

    if let Some(ready) = ready {
        std::fs::write(ready, b"ready")?;
    }
    loop {
        if instance.signal.requested() {
            return Ok(());
        }
        match tray.poll() {
            Ok(true) => return Ok(()),
            Err(error) => eprintln!("Typlo tray: {error}"),
            Ok(false) => {}
        }
        while let Some(event) = desktop.conn.poll_for_event().map_err(err)? {
            let command = match event {
                Event::KeyPress(key) => {
                    let shortcut = shortcuts.find(key.detail, u16::from(key.state));
                    if shortcut.is_some() {
                        gesture = Gesture::default();
                        typing.pending = None;
                    }
                    shortcut
                }
                Event::XinputRawKeyPress(key) => {
                    editor.cancel_quick_spelling();
                    let key = key.detail as u8;
                    if !desktop.modifiers.keys.contains_key(&key)
                        && shortcuts.find(key, typing.modifiers).is_none()
                    {
                        editor.cancel_choices();
                    }
                    gesture.event(key, true, &desktop.modifiers);
                    typing.key(&desktop, &shortcuts, &settings, key)?;
                    None
                }
                Event::XinputRawButtonPress(_) => {
                    editor.cancel_quick_spelling();
                    editor.cancel_choices();
                    typing.reset();
                    pending = None;
                    None
                }
                Event::XkbStateNotify(state) => {
                    typing.modifiers = state.mods.into();
                    let group = u8::from(state.group);
                    if group != typing.group {
                        editor.cancel_choices();
                        typing.group = group;
                        typing.reset();
                    }
                    None
                }
                Event::XinputRawKeyRelease(key)
                    if gesture.event(key.detail as u8, false, &desktop.modifiers)
                        && shortcuts.modifier_cycle =>
                {
                    Some(Binding::Action(Action::Switch))
                }
                Event::MappingNotify(_) => {
                    if let Ok(keyboard) = Keyboard::load(&desktop.conn, desktop.root)
                        && let Ok(modifiers) = Modifiers::read(&desktop.conn, &keyboard)
                    {
                        desktop.keyboard = keyboard;
                        desktop.modifiers = modifiers;
                        gesture = Gesture::default();
                        typing = Typing::new(&desktop, &settings)?;
                        if let Err(error) = inhibit.apply(settings.keep_screen_on) {
                            eprintln!("Typlo desktop: {error}");
                        }
                        if let Err(error) = shortcuts.apply(&desktop, &settings) {
                            eprintln!("Typlo shortcuts: {error}");
                        }
                    }
                    None
                }
                _ => None,
            };
            if let Some(command) = command
                && let Some(window) = desktop.focus()?
            {
                pending = Some((command, window, Instant::now()));
            }
        }
        if let Some((command, window, since)) = pending {
            if since.elapsed() >= Duration::from_millis(300) || desktop.focus()? != Some(window) {
                pending = None;
            } else if desktop
                .conn
                .query_keymap()
                .map_err(err)?
                .reply()
                .map_err(err)?
                .keys
                == [0; 32]
            {
                pending = None;
                let choice = match command {
                    Binding::Action(
                        action @ (Action::Choices | Action::AcceptChoice | Action::CancelChoice),
                    ) => editor.choice_command(&desktop, window, action, settings.switch_layout),
                    _ => {
                        editor.cancel_choices();
                        Ok(false)
                    }
                };
                let operation = if !matches!(choice, Ok(false)) {
                    choice.map(|_| ())
                } else if let Binding::Action(
                    action @ (Action::Tools | Action::AddWord | Action::Choices),
                ) = command
                {
                    editor.start_tools(
                        &desktop,
                        profile,
                        &settings,
                        window,
                        action,
                        typing.text(window),
                    )
                } else if matches!(command, Binding::Action(Action::History)) {
                    let origin = desktop.pid(window)?.map(|pid| [window, pid]);
                    crate::unix_clipboard::history_action_with_origin(profile, &mut history, origin)
                } else if matches!(
                    command,
                    Binding::Action(Action::Spelling | Action::SpellingReview)
                ) {
                    editor.start_spelling(
                        &desktop,
                        profile,
                        window,
                        matches!(command, Binding::Action(Action::SpellingReview)),
                    )
                } else {
                    let recent = settings
                        .convert_last_word
                        .then(|| typing.text(window).and_then(typing::last_word))
                        .flatten();
                    let result = execute(&desktop, &mut editor, &settings, window, command, recent);
                    typing.reset();
                    result
                };
                if let Err(error) = operation {
                    eprintln!("Typlo X11 command: {error}");
                }
            }
        }
        if let Some((window, deadline)) = typing.pending
            && Instant::now() >= deadline
        {
            typing.pending = None;
            let start = Instant::now();
            let result = apply_typing(
                &desktop,
                &mut editor,
                &settings,
                &dictionary,
                &typing,
                window,
            );
            match result {
                Ok(true) => {
                    editor.last_automatic_micros = Some(start.elapsed().as_micros() as u64);
                    typing.reset();
                }
                Err(error)
                    if !matches!(
                        error.kind(),
                        io::ErrorKind::Interrupted | io::ErrorKind::Unsupported
                    ) =>
                {
                    eprintln!("Typlo X11 automatic edit: {error}");
                }
                _ => {}
            }
        }
        if let Err(error) = editor.apply_spelling(&desktop)
            && !matches!(
                error.kind(),
                io::ErrorKind::Interrupted | io::ErrorKind::Unsupported
            )
        {
            eprintln!("Typlo X11 spelling: {error}");
        }
        if let Err(error) = editor.poll_tools()
            && !matches!(
                error.kind(),
                io::ErrorKind::Interrupted | io::ErrorKind::Unsupported
            )
        {
            eprintln!("Typlo X11 selection tools: {error}");
        }
        if check_focus.elapsed() >= Duration::from_millis(80) {
            check_focus = Instant::now();
            let current = desktop.focus()?;
            if focus != current {
                focus = current;
                if editor.choice_window().is_some() && editor.choice_window() != current {
                    editor.cancel_choices();
                }
                let process = focus
                    .and_then(|window| desktop.process(window).ok())
                    .unwrap_or_default();
                if let Some(target) =
                    settings.layout_on_entry(&process, desktop.keyboard.current(&desktop.conn)?)
                    && desktop.focus()? == focus
                {
                    desktop.keyboard.select(&desktop.conn, target)?;
                }
            }
        }
        if check_settings.elapsed() >= Duration::from_secs(2) {
            check_settings = Instant::now();
            let next = dictionary.read_settings()?;
            if next != settings_blob {
                let result = config::load(profile).and_then(|loaded| {
                    shortcuts.apply(&desktop, &loaded)?;
                    Ok(loaded)
                });
                match result {
                    Ok(loaded) => {
                        editor.cancel_choices();
                        settings = loaded;
                        focus = None;
                        typing = Typing::new(&desktop, &settings)?;
                    }
                    Err(error) => eprintln!("Typlo settings: {error}"),
                }
                settings_blob = next;
            }
        }
        poll(
            desktop.conn.as_raw_fd(),
            if pending.is_some() || typing.pending.is_some() || editor.spelling.is_some() {
                12
            } else {
                80
            },
        )?;
    }
}

pub fn convert(profile: &Path, target: &str) -> io::Result<()> {
    if crate::unix_clipboard::wayland() {
        return crate::unix_clipboard::convert_clipboard(profile, target);
    }
    let desktop = Desktop::open()?;
    let command = match target {
        "toggle" => Action::Convert,
        "en" => Action::English,
        "ru" => Action::Russian,
        "uk" | "ua" => Action::Ukrainian,
        _ => {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "Use en, ru, uk or toggle",
            ));
        }
    };
    let focus = desktop.focus()?.ok_or_else(accessible::unsupported)?;
    execute(
        &desktop,
        &mut EditSession::default(),
        &config::load(profile)?,
        focus,
        Binding::Action(command),
        None,
    )
}

#[derive(Default)]
struct EditSession {
    atspi: Option<Rc<accessible::Api>>,
    state: crate::edit::Session<accessible::Identity>,
    retained: Option<accessible::Retained>,
    vocabulary: Option<crate::lexicon::Vocabulary>,
    spelling: Option<SpellingJob>,
    tools: Option<ToolJob>,
    choices: Option<ToolChoices>,
    last_automatic_micros: Option<u64>,
    last_automatic_reason: Option<crate::lexicon::Decision>,
}

struct SpellingJob {
    job: crate::jobs::Task<Option<String>>,
    original: Option<(Window, Snapshot<accessible::Identity>, accessible::Retained)>,
    review: bool,
}

type ToolJob = crate::jobs::Task<()>;

struct ToolChoices {
    window: Window,
    snapshot: Snapshot<accessible::Identity>,
    _identity: accessible::Retained,
    range: std::ops::Range<usize>,
    preview: crate::jobs::ChoicePreview,
    suffix: String,
}

impl EditSession {
    fn api(&mut self) -> io::Result<Rc<accessible::Api>> {
        if self.atspi.is_none() {
            self.atspi = Some(Rc::new(accessible::Api::open()?));
        }
        Ok(Rc::clone(self.atspi.as_ref().expect("initialized AT-SPI")))
    }

    fn cancel_quick_spelling(&mut self) {
        if self
            .spelling
            .as_ref()
            .is_some_and(|pending| !pending.review)
        {
            self.spelling = None;
        }
    }

    fn start_tools(
        &mut self,
        desktop: &Desktop,
        profile: &Path,
        settings: &config::Config,
        window: Window,
        action: Action,
        typed: Option<&str>,
    ) -> io::Result<()> {
        use crate::selection_tools::Request;
        self.tools = None;
        let field = self.api().and_then(|api| {
            let mut field = accessible::Field::open(&api, desktop, window, None)?;
            Ok((field.snapshot()?, field.retain(Rc::clone(&api))))
        });
        let request = if action == Action::Tools {
            let method = match &field {
                Ok(_) => "Доступна прямая замена AT-SPI. Буфер обмена не используется.",
                Err(_) => "Поле не предоставило проверяемый диапазон текста.",
            };
            Request::Field {
                method: method.into(),
                last_automatic_micros: self.last_automatic_micros,
                reason: self.last_automatic_reason,
            }
        } else {
            let (snapshot, identity) = field?;
            let range = if action == Action::Choices && snapshot.selection.is_empty() {
                typed
                    .and_then(typing::last_word)
                    .and_then(|word| {
                        typing::verified_suffix(&snapshot.document, snapshot.selection.end, word)
                    })
                    .unwrap_or_else(|| snapshot.selection.clone())
            } else {
                snapshot.selection.clone()
            };
            if range.is_empty() || range.len() > 128 {
                return Ok(());
            }
            let text = String::from_utf16(&snapshot.document[range.clone()]).map_err(err)?;
            let word = text.trim_end_matches(' ');
            if word.is_empty() || word.chars().any(char::is_whitespace) {
                return Ok(());
            }
            let active = desktop.keyboard.current(&desktop.conn)?;
            if action == Action::AddWord {
                let language = layout::detect(word, &desktop.keyboard.catalog, active)
                    .and_then(Language::dictionary_index)
                    .unwrap_or_default();
                Request::Add {
                    word: word.to_owned(),
                    language,
                }
            } else {
                let Some(source) = active else {
                    return Ok(());
                };
                if self.vocabulary.is_none() {
                    self.vocabulary = Some(crate::lexicon::Vocabulary::installed()?);
                }
                let process = desktop.process(window).unwrap_or_default();
                let choices = self
                    .vocabulary
                    .as_mut()
                    .expect("initialized vocabulary")
                    .options(
                        &crate::dictionary::Dictionary::new(profile),
                        word,
                        source,
                        settings.cycle_for(&process),
                    )?;
                if desktop.focus()? != Some(window) {
                    return Ok(());
                }
                self.choices =
                    crate::jobs::ChoicePreview::start(profile, choices)?.map(|preview| {
                        ToolChoices {
                            window,
                            snapshot,
                            _identity: identity,
                            range,
                            preview,
                            suffix: text[word.len()..].to_owned(),
                        }
                    });
                return Ok(());
            }
        };
        if desktop.focus()? != Some(window) {
            return Ok(());
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
        self.tools = Some(job);
        Ok(())
    }

    fn cancel_choices(&mut self) {
        self.choices = None;
    }

    fn choice_window(&self) -> Option<Window> {
        self.choices.as_ref().map(|choice| choice.window)
    }

    fn choice_command(
        &mut self,
        desktop: &Desktop,
        window: Window,
        action: Action,
        switch: bool,
    ) -> io::Result<bool> {
        if self
            .choices
            .as_ref()
            .is_some_and(|choice| choice.window != window || !choice.preview.alive())
        {
            self.cancel_choices();
        }
        if action == Action::Choices {
            if let Some(choice) = &mut self.choices {
                choice.preview.advance();
                return Ok(true);
            }
            return Ok(false);
        }
        if let Some(apply) = self.choices.take()
            && action == Action::AcceptChoice
        {
            let candidate = apply.preview.selected();
            let text = format!("{}{}", candidate.text, apply.suffix);
            let api = self.api()?;
            let mut field = accessible::Field::open(&api, desktop, apply.window, None)?;
            if self.state.replace_verified_range(
                &mut field,
                apply.snapshot,
                apply.range,
                &text,
                Some(candidate.language),
            )? {
                self.retained = Some(field.retain(Rc::clone(&api)));
                if switch && desktop.focus()? == Some(apply.window) {
                    desktop.keyboard.select(&desktop.conn, candidate.language)?;
                }
            }
        }
        Ok(true)
    }

    fn poll_tools(&mut self) -> io::Result<()> {
        if self
            .choices
            .as_ref()
            .is_some_and(|choice| !choice.preview.alive())
        {
            self.cancel_choices();
        }
        if let Some(result) = self.tools.as_ref().and_then(|job| job.poll()) {
            self.tools = None;
            result?;
        }
        Ok(())
    }

    fn start_spelling(
        &mut self,
        desktop: &Desktop,
        profile: &Path,
        window: Window,
        review: bool,
    ) -> io::Result<()> {
        self.spelling = None;
        let original = match self.api().and_then(|api| {
            let mut field = accessible::Field::open(&api, desktop, window, None)?;
            let snapshot = field.snapshot()?;
            Ok((!snapshot.selection.is_empty()
                && snapshot.selection.len() <= crate::spelling::MAX_CHARACTERS)
                .then(|| (window, snapshot, field.retain(Rc::clone(&api)))))
        }) {
            Ok(original) => original,
            Err(error) if review && error.kind() == io::ErrorKind::Unsupported => None,
            Err(error) => return Err(error),
        };
        if original.is_none() && !review {
            return Ok(());
        }
        let text = original
            .as_ref()
            .map(|(_, snapshot, _)| {
                String::from_utf16(&snapshot.document[snapshot.selection.clone()])
            })
            .transpose()
            .map_err(err)?
            .unwrap_or_default();
        let origin = original
            .as_ref()
            .map_or(0, |(window, _, _)| *window as usize);
        let profile = profile.to_owned();
        let job = crate::jobs::Task::start(move |canceled| {
            crate::jobs::correct(&profile, text, origin, review, canceled)
        })?;
        self.spelling = Some(SpellingJob {
            job,
            original,
            review,
        });
        Ok(())
    }

    fn apply_spelling(&mut self, desktop: &Desktop) -> io::Result<()> {
        let Some(result) = self.spelling.as_ref().and_then(|job| job.job.poll()) else {
            return Ok(());
        };
        let pending = self.spelling.take().expect("spelling result has an owner");
        if let (Some(result), Some((window, snapshot, _identity))) = (result?, pending.original) {
            let api = self.api()?;
            let mut field = accessible::Field::open(&api, desktop, window, None)?;
            if self
                .state
                .replace_selection(&mut field, snapshot, &result)?
            {
                self.retained = Some(field.retain(Rc::clone(&api)));
            }
        }
        Ok(())
    }
}

struct Typing {
    group: u8,
    modifiers: u16,
    sequence: usize,
    recent: RecentInput<Window>,
    snippets: Snippets,
    pending: Option<(Window, Instant)>,
}

impl Typing {
    fn new(desktop: &Desktop, settings: &config::Config) -> io::Result<Self> {
        let (group, modifiers) = Keyboard::state(&desktop.conn)?;
        Ok(Self {
            group,
            modifiers,
            sequence: 0,
            recent: RecentInput::default(),
            snippets: Snippets::compile_for(&settings.snippets, &desktop.keyboard.catalog)?,
            pending: None,
        })
    }

    fn key(
        &mut self,
        desktop: &Desktop,
        shortcuts: &Shortcuts,
        settings: &config::Config,
        key: u8,
    ) -> io::Result<()> {
        if desktop.modifiers.keys.contains_key(&key) {
            return Ok(());
        }
        self.pending = None;
        if shortcuts.find(key, self.modifiers).is_some() {
            return Ok(());
        }
        let shift = desktop.modifiers.mask(0xffe1);
        let caps = desktop.modifiers.mask(0xffe5);
        let num = desktop.modifiers.mask(0xff7f);
        if self.modifiers & !(shift | caps | num) != 0 {
            self.reset();
            return Ok(());
        }
        let input = if desktop.keyboard.symbol(0xff08) == Some(key) {
            Input::Backspace
        } else {
            desktop
                .keyboard
                .character(
                    self.group,
                    key,
                    self.modifiers & shift != 0,
                    self.modifiers & caps != 0,
                )
                .map_or(Input::Reset, Input::Typed)
        };
        let focus = desktop.focus()?;
        let now = Instant::now();
        self.sequence = self.sequence.wrapping_add(1);
        self.recent.push(focus, self.sequence, input, now);
        if let Some(focus) = focus
            && let Some(text) = self.text(focus)
        {
            let word = matches!(input, Input::Typed(' '))
                && (settings.auto_correct
                    || settings
                        .applications
                        .iter()
                        .any(|rule| rule.auto_correct == Some(true)));
            if word || (!self.snippets.is_empty() && self.snippets.matches(text).is_some()) {
                self.pending = Some((focus, now + Duration::from_millis(30)));
            }
        }
        Ok(())
    }

    fn text(&self, window: Window) -> Option<&str> {
        self.recent.text(&window, self.sequence, Instant::now())
    }

    fn reset(&mut self) {
        self.recent.reset();
        self.pending = None;
    }
}

fn apply_typing(
    desktop: &Desktop,
    session: &mut EditSession,
    settings: &config::Config,
    dictionary: &crate::dictionary::Dictionary,
    typing: &Typing,
    window: Window,
) -> io::Result<bool> {
    if desktop.focus()? != Some(window) {
        return Ok(false);
    }
    let process = desktop.process(window).unwrap_or_default();
    let Some(text) = typing.text(window) else {
        return Ok(false);
    };
    let snippet = settings
        .snippets_for(&process)
        .then(|| typing.snippets.matches(text))
        .flatten();
    let word = settings
        .auto_correct_for(&process)
        .then(|| typing::last_word(text))
        .flatten()
        .filter(|word| word.ends_with(' '));
    if snippet.is_none() && word.is_none() {
        return Ok(false);
    }
    if session.atspi.is_none() {
        session.atspi = Some(Rc::new(accessible::Api::open()?));
    }
    let api = session.atspi.as_ref().expect("initialized AT-SPI");
    let mut field = accessible::Field::open(api, desktop, window, None)?;
    if let Some((trigger, template)) = snippet
        && session
            .state
            .expand_snippet(&mut field, trigger, &template.with_lf())?
    {
        session.retained = Some(field.retain(Rc::clone(api)));
        return Ok(true);
    }
    let (Some(word), Some(source)) = (word, desktop.keyboard.current(&desktop.conn)?) else {
        return Ok(false);
    };
    if session.vocabulary.is_none() {
        session.vocabulary = Some(crate::lexicon::Vocabulary::installed()?);
    }
    let vocabulary = session.vocabulary.as_mut().expect("initialized vocabulary");
    let started = Instant::now();
    let mut reason = crate::lexicon::Decision::Unverified;
    let target = session.state.correct_word(&mut field, word, |token| {
        let (suggestion, decision) = vocabulary.decision(
            dictionary,
            token,
            source,
            settings.cycle_for(&process),
            settings.protect_technical,
        )?;
        reason = decision;
        Ok(suggestion)
    });
    session.last_automatic_micros = Some(started.elapsed().as_micros() as u64);
    session.last_automatic_reason = Some(match &target {
        Ok(Some(_)) => crate::lexicon::Decision::Applied,
        Ok(None) => reason,
        Err(_) => crate::lexicon::Decision::Failed,
    });
    let target = target?;
    if let Some(target) = target {
        session.retained = Some(field.retain(Rc::clone(api)));
        if settings.switch_layout && desktop.focus()? == Some(window) {
            desktop.keyboard.select(&desktop.conn, target)?;
        }
        return Ok(true);
    }
    Ok(false)
}

fn execute(
    desktop: &Desktop,
    session: &mut EditSession,
    settings: &config::Config,
    window: Window,
    command: Binding,
    recent: Option<&str>,
) -> io::Result<()> {
    let process = desktop.process(window).unwrap_or_default();
    let active = desktop.keyboard.current(&desktop.conn)?;
    let layouts: Vec<_> = settings
        .cycle_for(&process)
        .iter()
        .copied()
        .filter(|language| desktop.keyboard.catalog.contains(language))
        .collect();
    if matches!(command, Binding::Action(Action::Switch)) {
        if let Some(target) = layout::next_language(&layouts, active)
            && desktop.focus()? == Some(window)
        {
            desktop.keyboard.select(&desktop.conn, target)?;
        }
        return Ok(());
    }
    let target = match command {
        Binding::Direct(language) => layouts.contains(&language).then_some(language),
        Binding::Action(Action::English | Action::Russian | Action::Ukrainian) => {
            let index = match command {
                Binding::Action(Action::English) => 0,
                Binding::Action(Action::Russian) => 1,
                _ => 2,
            };
            layouts
                .iter()
                .copied()
                .find(|layout| layout.dictionary_index() == Some(index))
        }
        _ => None,
    };
    if session.atspi.is_none() {
        session.atspi = Some(Rc::new(accessible::Api::open()?));
    }
    let api = session.atspi.as_ref().expect("initialized AT-SPI");
    let mut field = accessible::Field::open(api, desktop, window, recent)?;
    let language = match command {
        Binding::Action(Action::Case) => session.state.transform(&mut field, |text, _| {
            Some((crate::text_tools::cycle_case(text), None))
        })?,
        Binding::Action(Action::Transliterate) => {
            session.state.transform(&mut field, |text, _| {
                Some((crate::text_tools::transliterate(text), None))
            })?
        }
        Binding::Action(Action::Undo) => {
            session.state.undo(&mut field)?;
            None
        }
        Binding::Action(Action::Convert) => session.state.convert(&mut field, &layouts, active)?,
        _ => match target {
            Some(target) => session.state.convert_to(&mut field, target, active)?,
            None => None,
        },
    };
    session.retained = Some(field.retain(Rc::clone(api)));
    if settings.switch_layout
        && desktop.focus()? == Some(window)
        && let Some(language) = language
    {
        desktop.keyboard.select(&desktop.conn, language)?;
    }
    Ok(())
}

fn poll(fd: RawFd, timeout: i32) -> io::Result<()> {
    let mut descriptor = libc::pollfd {
        fd,
        events: libc::POLLIN,
        revents: 0,
    };
    if unsafe { libc::poll(&mut descriptor, 1, timeout) } < 0 {
        let error = io::Error::last_os_error();
        if error.kind() != io::ErrorKind::Interrupted {
            return Err(error);
        }
    }
    Ok(())
}

pub fn open_settings(profile: &Path) -> io::Result<Child> {
    Command::new(std::env::current_exe()?.with_file_name("typlo-settings"))
        .arg("--profile")
        .arg(profile)
        .stdin(Stdio::null())
        .spawn()
}
