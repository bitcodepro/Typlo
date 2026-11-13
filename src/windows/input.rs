use super::{Target, WinEdit};
use crate::{
    hotkeys::{Chord, DrawingButton, DrawingShortcut},
    typing::Input,
};
use std::{
    cell::RefCell,
    collections::HashSet,
    io,
    ptr::null_mut,
    sync::{
        Arc, Condvar, Mutex, RwLock,
        atomic::{AtomicBool, AtomicUsize, Ordering},
        mpsc::{Receiver, SyncSender, sync_channel},
    },
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};
use windows_sys::Win32::{
    Foundation::{HWND, LPARAM, LRESULT, POINT, WPARAM},
    System::{LibraryLoader::GetModuleHandleW, Threading::GetCurrentThreadId},
    UI::{
        Accessibility::{HWINEVENTHOOK, SetWinEventHook, UnhookWinEvent},
        Input::KeyboardAndMouse::*,
        WindowsAndMessaging::*,
    },
};

pub const READY: u32 = WM_APP + 4;
pub const CYCLE: u32 = WM_APP + 6;
pub const PHYSICAL_HOTKEY: u32 = WM_APP + 7;
const REPLAY: usize = 0x7459_504c;
const DRAWING_CLICK: usize = 0x7459_504d;
pub const OWN_EDIT: usize = 0x7459_5045;
static SEQUENCE: AtomicUsize = AtomicUsize::new(0);
static FOCUS_EPOCH: AtomicUsize = AtomicUsize::new(0);
static EDIT: Mutex<Option<BufferedEdit>> = Mutex::new(None);
static PASTE_RESULT: Condvar = Condvar::new();
static EDIT_ACTIVE: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);

struct BufferedEdit {
    sequence: usize,
    foreground: usize,
    control: usize,
    focus_epoch: usize,
    keys: Vec<INPUT>,
    committed: Option<bool>,
    canceled: Option<Arc<AtomicBool>>,
}

impl BufferedEdit {
    fn can_commit(
        &self,
        current_sequence: usize,
        foreground: usize,
        control: usize,
        focus_epoch: usize,
    ) -> bool {
        self.keys.is_empty()
            && self.sequence == current_sequence
            && self.foreground == foreground
            && self.control == control
            && self.focus_epoch == focus_epoch
            && !self.is_canceled()
    }

    fn can_commit_now(&self) -> bool {
        let (foreground, control) = focused_windows();
        self.can_commit(
            sequence(),
            foreground,
            control,
            FOCUS_EPOCH.load(Ordering::Acquire),
        )
    }

    fn is_canceled(&self) -> bool {
        self.canceled
            .as_ref()
            .is_some_and(|flag| flag.load(Ordering::Acquire))
    }
}

// Only held while a programmatic selection could receive the user's next key.
// Never wait for UI Automation or SendInput with the buffer mutex held.
pub(super) struct SelectionLease;
static KEYBOARD_HOOK_LIVE: AtomicBool = AtomicBool::new(false);

impl SelectionLease {
    pub fn start(
        sequence: usize,
        target: Target,
        canceled: Option<Arc<AtomicBool>>,
    ) -> io::Result<Self> {
        let mut edit = EDIT.lock().unwrap_or_else(|e| e.into_inner());
        if !KEYBOARD_HOOK_LIVE.load(Ordering::Acquire)
            || edit.is_some()
            || sequence != self::sequence()
            || canceled
                .as_ref()
                .is_some_and(|flag| flag.load(Ordering::Acquire))
        {
            return Err(io::ErrorKind::Interrupted.into());
        }
        *edit = Some(BufferedEdit {
            sequence,
            foreground: target.foreground as usize,
            control: target.edit as usize,
            focus_epoch: FOCUS_EPOCH.load(Ordering::Acquire),
            keys: Vec::new(),
            committed: None,
            canceled,
        });
        EDIT_ACTIVE.store(true, Ordering::Release);
        Ok(Self)
    }

    pub fn begin_native_commit(&self) -> bool {
        let mut state = EDIT.lock().unwrap_or_else(|error| error.into_inner());
        state.as_mut().is_some_and(|edit| {
            let allowed = edit.can_commit_now();
            *edit.committed.get_or_insert(allowed)
        })
    }

    pub fn arm_focus(&self) {
        if let Some(edit) = EDIT
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .as_mut()
        {
            edit.focus_epoch = FOCUS_EPOCH.load(Ordering::Acquire);
        }
    }

    pub fn still_quiet(&self) -> bool {
        EDIT.lock()
            .unwrap_or_else(|e| e.into_inner())
            .as_ref()
            .is_some_and(|edit| {
                edit.keys.is_empty()
                    && edit.sequence == sequence()
                    && (edit.committed == Some(true) || !edit.is_canceled())
            })
    }

    pub fn wait_for_paste(&self) -> bool {
        wait_for_paste(&EDIT, &PASTE_RESULT, Duration::from_millis(35))
    }

    pub fn cancel_pending_paste(&self) -> bool {
        EDIT.lock()
            .unwrap_or_else(|e| e.into_inner())
            .as_mut()
            .is_some_and(|edit| *edit.committed.get_or_insert(false))
    }
}

fn focused_windows() -> (usize, usize) {
    let mut info = GUITHREADINFO {
        cbSize: size_of::<GUITHREADINFO>() as u32,
        ..Default::default()
    };
    let foreground = unsafe { GetForegroundWindow() };
    let thread = unsafe { GetWindowThreadProcessId(foreground, null_mut()) };
    let control = if thread != 0 && unsafe { GetGUIThreadInfo(thread, &mut info) } != 0 {
        info.hwndFocus as usize
    } else {
        0
    };
    (foreground as usize, control)
}

unsafe extern "system" fn focus_event(
    _: HWINEVENTHOOK,
    _: u32,
    window: HWND,
    _: i32,
    _: i32,
    _: u32,
    _: u32,
) {
    if !window.is_null() && unsafe { GetAncestor(window, GA_ROOT) == GetForegroundWindow() } {
        FOCUS_EPOCH.fetch_add(1, Ordering::AcqRel);
    }
}

fn wait_for_paste(edit: &Mutex<Option<BufferedEdit>>, ready: &Condvar, timeout: Duration) -> bool {
    let guard = edit.lock().unwrap_or_else(|error| error.into_inner());
    let (mut guard, _) = ready
        .wait_timeout_while(guard, timeout, |edit| {
            edit.as_ref().is_some_and(|edit| edit.committed.is_none())
        })
        .unwrap_or_else(|error| error.into_inner());
    // Seal a timed-out paste before its delayed hook events can reach the editor.
    guard
        .as_mut()
        .is_some_and(|edit| *edit.committed.get_or_insert(false))
}

impl Drop for SelectionLease {
    fn drop(&mut self) {
        let barrier = keyboard_input(0xe8, 0, 0, edit_tag(0));
        if !KEYBOARD_HOOK_LIVE.load(Ordering::Acquire) || inject(&[barrier]) != 1 {
            let keys = take_edit_keys();
            inject(&keys);
        }
    }
}

fn take_edit_keys() -> Vec<INPUT> {
    let mut state = EDIT.lock().unwrap_or_else(|e| e.into_inner());
    EDIT_ACTIVE.store(false, Ordering::Release);
    let keys = state.take().map(|edit| edit.keys).unwrap_or_default();
    drop(state);
    PASTE_RESULT.notify_one();
    keys
}

fn edit_tag(kind: usize) -> usize {
    (std::process::id() as usize).rotate_left(16) ^ (0x5445_5900 + kind)
}

pub(super) fn paste_tag() -> usize {
    edit_tag(1)
}

fn keyboard_input(key: u16, scan: u16, flags: u32, tag: usize) -> INPUT {
    INPUT {
        r#type: INPUT_KEYBOARD,
        Anonymous: INPUT_0 {
            ki: KEYBDINPUT {
                wVk: key,
                wScan: scan,
                dwFlags: flags,
                dwExtraInfo: tag,
                ..Default::default()
            },
        },
    }
}

fn inject(events: &[INPUT]) -> u32 {
    if events.is_empty() {
        return 0;
    }
    unsafe {
        SendInput(
            events.len() as u32,
            events.as_ptr(),
            size_of::<INPUT>() as i32,
        )
    }
}

pub(super) fn send_edit_keys(modifier: Option<u16>, key: u16, count: usize) -> io::Result<()> {
    send_keys(modifier, key, count, OWN_EDIT)
}

pub(super) fn send_selection_keys(
    modifier: Option<u16>,
    key: u16,
    count: usize,
    cleanup: bool,
) -> io::Result<()> {
    send_keys(modifier, key, count, edit_tag(if cleanup { 4 } else { 3 }))
}

fn send_keys(modifier: Option<u16>, key: u16, count: usize, tag: usize) -> io::Result<()> {
    let event = |key, flags| keyboard_input(key, 0, flags, tag);
    let mut keys = Vec::with_capacity(count * 2 + 2);
    keys.extend(modifier.map(|key| event(key, 0)));
    let flags = if matches!(key, VK_LEFT | VK_RIGHT) {
        KEYEVENTF_EXTENDEDKEY
    } else {
        0
    };
    for _ in 0..count {
        keys.push(event(key, flags));
        keys.push(event(key, flags | KEYEVENTF_KEYUP));
    }
    keys.extend(modifier.map(|key| event(key, KEYEVENTF_KEYUP)));
    if inject(&keys) as usize != keys.len() {
        if let Some(modifier) = modifier {
            inject(&[keyboard_input(modifier, 0, KEYEVENTF_KEYUP, OWN_EDIT)]);
        }
        return Err(io::Error::other(
            "Windows did not accept the complete edit command",
        ));
    }
    Ok(())
}

fn edit_key(key: KBDLLHOOKSTRUCT, down: bool) -> Option<bool> {
    let injected = key.flags & LLKHF_INJECTED != 0;
    if !injected && !EDIT_ACTIVE.load(Ordering::Acquire) {
        return None;
    }
    if injected && key.dwExtraInfo == edit_tag(2) {
        let suppress = HOOK.with(|slot| {
            let Ok(mut slot) = slot.try_borrow_mut() else {
                return false;
            };
            slot.as_mut()
                .is_some_and(|hook| hook.physical_shortcut(&key, down, system_modifiers()))
        });
        return Some(suppress);
    }
    if injected && [edit_tag(3), edit_tag(4)].contains(&key.dwExtraInfo) {
        if !down {
            return Some(false); // A focus change must not leave a modifier held.
        }
        let (foreground, control) = focused_windows();
        let state = EDIT.lock().unwrap_or_else(|error| error.into_inner());
        let allowed = state.as_ref().is_some_and(|edit| {
            edit.foreground == foreground
                && edit.control == control
                && edit.sequence == sequence()
                && (key.dwExtraInfo == edit_tag(4) || edit.keys.is_empty())
        });
        return Some(!allowed);
    }
    if injected && key.dwExtraInfo == edit_tag(0) {
        let mut keys = {
            let mut state = EDIT.lock().unwrap_or_else(|e| e.into_inner());
            let Some(edit) = state.as_mut() else {
                return Some(true);
            };
            if edit.keys.is_empty() {
                *state = None;
                EDIT_ACTIVE.store(false, Ordering::Release);
                drop(state);
                PASTE_RESULT.notify_one();
                return Some(true);
            }
            std::mem::take(&mut edit.keys)
        };
        HOOK.with_borrow_mut(|state| {
            if let Some(hook) = state.as_mut() {
                hook.gesture.pending = None;
                hook.send(None, Key::Reset);
            }
        });
        keys.push(keyboard_input(0xe8, 0, 0, edit_tag(0)));
        let sent = inject(&keys) as usize;
        if sent != keys.len() {
            let mut pending = keys[sent..keys.len() - 1].to_vec();
            pending.extend(take_edit_keys());
            inject(&pending);
        }
        return Some(true);
    }
    if injected && key.dwExtraInfo == OWN_EDIT {
        return None;
    }
    let mut state = EDIT.lock().unwrap_or_else(|e| e.into_inner());
    if injected && key.dwExtraInfo == paste_tag() {
        let Some(edit) = state.as_mut() else {
            return Some(true);
        };
        if edit.committed.is_none() {
            edit.committed = Some(edit.can_commit_now());
            let suppress = edit.committed != Some(true);
            drop(state);
            PASTE_RESULT.notify_one();
            return Some(suppress);
        }
        return Some(edit.committed != Some(true));
    }
    let edit = state.as_mut()?;
    let mut vk = key.vkCode as u16;
    let flags = if vk == VK_PACKET {
        vk = 0;
        KEYEVENTF_UNICODE
    } else if key.flags & LLKHF_EXTENDED != 0 {
        KEYEVENTF_EXTENDEDKEY
    } else {
        0
    } | if down { 0 } else { KEYEVENTF_KEYUP };
    edit.keys
        .push(keyboard_input(vk, key.scanCode as u16, flags, edit_tag(2)));
    drop(state);
    if !injected {
        HOOK.with_borrow_mut(|state| {
            if let Some(hook) = state.as_mut() {
                if down {
                    hook.gesture.down.insert(vk);
                } else {
                    hook.gesture.down.remove(&vk);
                }
                hook.gesture.used = !hook.gesture.down.is_empty();
                if vk == VK_CAPITAL {
                    if down && !hook.caps_down {
                        hook.caps = !hook.caps;
                    }
                    hook.caps_down = down;
                }
            }
        });
    }
    Some(true)
}

pub fn sequence() -> usize {
    SEQUENCE.load(Ordering::Acquire)
}

pub(super) fn selection_active() -> bool {
    EDIT_ACTIVE.load(Ordering::Acquire)
}

pub(super) struct Event {
    pub sequence: usize,
    pub time: Instant,
    target: Option<(usize, usize, u32, u32)>,
    kind: Key,
}

impl Event {
    pub fn target(&self) -> Option<Target> {
        let (foreground, edit, thread, pid) = self.target?;
        Some(Target {
            foreground: foreground as HWND,
            edit: edit as HWND,
            thread,
            pid,
        })
    }

    pub fn input(&self) -> Input {
        match self.kind {
            Key::Reset => Input::Reset,
            Key::Backspace => Input::Backspace,
            Key::Printable {
                key,
                scan,
                layout,
                shift,
                caps,
            } => {
                let mut state = [0; 256];
                state[VK_SHIFT as usize] = if shift { 0x80 } else { 0 };
                state[VK_CAPITAL as usize] = u8::from(caps);
                let mut output = [0u16; 4];
                // Bit 2 prevents changes to the keyboard's dead-key state.
                let count = unsafe {
                    ToUnicodeEx(
                        key,
                        scan,
                        state.as_ptr(),
                        output.as_mut_ptr(),
                        4,
                        4,
                        layout as HKL,
                    )
                };
                if count == 1
                    && let Some(character) = char::from_u32(output[0].into())
                {
                    return Input::Typed(character);
                }
                Input::Reset
            }
        }
    }
}

enum Key {
    Reset,
    Backspace,
    Printable {
        key: u32,
        scan: u32,
        layout: usize,
        shift: bool,
        caps: bool,
    },
}

struct HookState {
    host: usize,
    events: SyncSender<Event>,
    bindings: Arc<RwLock<Bindings>>,
    caps: bool,
    caps_down: bool,
    gesture: Gesture,
    physical_down: u64,
    pressed_scans: u64,
    drawing_button: Option<DrawingShortcut>,
    drawing_start: Option<(POINT, usize, usize)>,
    drawing_cursor: Option<HCURSOR>,
    brush_keys: BrushKeys,
    last_draw_move: Option<Instant>,
}

#[derive(Default)]
struct BrushKeys {
    down: u32,
    captured: u32,
}

impl BrushKeys {
    fn key(&mut self, vk: u16, down: bool, drawing: bool) -> (bool, Option<i16>) {
        let (index, digit) = match vk {
            0x31..=0x39 => (vk - 0x31, vk - 0x30),
            VK_NUMPAD1..=VK_NUMPAD9 => (vk - VK_NUMPAD1 + 9, vk - VK_NUMPAD0),
            _ => return (false, None),
        };
        let mask = 1 << index;
        let captured = self.captured & mask != 0;
        if !down {
            self.down &= !mask;
            self.captured &= !mask;
            return (captured, None);
        }
        if self.down & mask != 0 {
            return (captured, None);
        }
        self.down |= mask;
        if drawing {
            self.captured |= mask;
            (true, Some(digit as i16))
        } else {
            (false, None)
        }
    }
}

pub(super) struct Bindings {
    pub chords: Vec<Chord>,
    pub physical: Vec<(usize, Chord)>,
    pub drawing: Option<DrawingShortcut>,
}

// Shortcut labels describe US QWERTY positions even with a different active HKL.
pub(super) fn physical_scan(key: u32) -> Option<u32> {
    const LETTERS: [u32; 26] = [
        0x1e, 0x30, 0x2e, 0x20, 0x12, 0x21, 0x22, 0x23, 0x17, 0x24, 0x25, 0x26, 0x32, 0x31, 0x18,
        0x19, 0x10, 0x13, 0x1f, 0x14, 0x16, 0x2f, 0x11, 0x2d, 0x15, 0x2c,
    ];
    Some(match key {
        0x41..=0x5a => LETTERS[(key - 0x41) as usize],
        0x31..=0x39 => key - 0x31 + 0x02,
        0x30 => 0x0b,
        0xdb => 0x1a,
        0xdd => 0x1b,
        0xbc => 0x33,
        0xbe => 0x34,
        _ => return None,
    })
}

fn physical_binding(
    bindings: &Bindings,
    key: &KBDLLHOOKSTRUCT,
    modifiers: u32,
) -> Option<(usize, Chord)> {
    if key.flags & LLKHF_EXTENDED != 0 || key.scanCode == 0 {
        return None;
    }
    bindings.physical.iter().copied().find(|(_, chord)| {
        chord.modifiers == modifiers && physical_scan(chord.key) == Some(key.scanCode)
    })
}

impl HookState {
    fn send(&self, target: Option<Target>, kind: Key) {
        let sequence = SEQUENCE.fetch_add(1, Ordering::AcqRel).wrapping_add(1);
        let event = Event {
            sequence,
            time: Instant::now(),
            target: target.map(|t| (t.foreground as usize, t.edit as usize, t.thread, t.pid)),
            kind,
        };
        if self.events.try_send(event).is_ok() {
            unsafe { PostMessageW(self.host as HWND, READY, 0, 0) };
        }
    }

    fn key(&mut self, message: u32, key: &KBDLLHOOKSTRUCT) -> Decision {
        let vk = key.vkCode as u16;
        let down = matches!(message, WM_KEYDOWN | WM_SYSKEYDOWN);
        if down && vk == VK_ESCAPE {
            super::snippet_menu::escape_outside();
        }
        if key.flags & LLKHF_INJECTED != 0 && !fixture_printable(key) {
            if down
                && modifier(vk) == 0
                && !matches!(key.dwExtraInfo, REPLAY | OWN_EDIT)
                && !self.is_registered_hotkey(key.vkCode, system_modifiers())
            {
                self.send(None, Key::Reset);
            }
            return Decision::default();
        }
        let cycle = self
            .bindings
            .try_read()
            .is_ok_and(|bindings| bindings.chords.iter().any(|chord| chord.key == 0));
        let origin = if is_cycle_modifier(vk) {
            (unsafe { GetForegroundWindow() }) as usize
        } else {
            0
        };
        let mut decision = self.gesture.handle(*key, down, origin, cycle);
        if decision.cycle {
            unsafe {
                PostMessageW(self.host as HWND, CYCLE, origin, sequence() as LPARAM);
            }
        }
        if key.flags & LLKHF_INJECTED == 0
            && self.physical_shortcut(key, down, self.gesture.modifiers())
        {
            decision.suppress = true;
            return decision;
        }
        if vk == VK_CAPITAL {
            if down && !self.caps_down {
                self.caps = !self.caps;
            }
            self.caps_down = down;
        }
        if !down || modifier(vk) != 0 {
            return decision;
        }
        let modifiers = self.gesture.modifiers();
        let shift = modifiers & MOD_SHIFT != 0;
        if self.is_registered_hotkey(key.vkCode, modifiers) {
            return decision;
        }
        let Some(target) = WinEdit::default().target().ok() else {
            self.send(None, Key::Reset);
            return decision;
        };
        let kind = if modifiers & (MOD_ALT | MOD_CONTROL | MOD_WIN) != 0 {
            Key::Reset
        } else if vk == VK_BACK {
            Key::Backspace
        } else if vk == VK_SPACE || matches!(vk, 0x30..=0x39 | 0x41..=0x5a | 0xba..=0xe2) {
            let layout = unsafe { GetKeyboardLayout(target.thread) } as usize;
            Key::Printable {
                key: key.vkCode,
                scan: key.scanCode,
                layout,
                shift,
                caps: self.caps,
            }
        } else {
            Key::Reset
        };
        self.send(Some(target), kind);
        decision
    }

    fn physical_shortcut(&mut self, key: &KBDLLHOOKSTRUCT, down: bool, modifiers: u32) -> bool {
        if key.flags & LLKHF_EXTENDED == 0 && key.scanCode < 64 {
            let bit = 1_u64 << key.scanCode;
            let fresh = self.pressed_scans & bit == 0;
            if down {
                self.pressed_scans |= bit;
            } else {
                self.pressed_scans &= !bit;
            }
            if self.physical_down & bit != 0 {
                if !down {
                    self.physical_down &= !bit;
                }
                return true;
            }
            if down
                && fresh
                && let Some((id, chord)) = self
                    .bindings
                    .try_read()
                    .ok()
                    .and_then(|bindings| physical_binding(&bindings, key, modifiers))
                && !super::settings_window_focused()
                && unsafe {
                    PostMessageW(
                        self.host as HWND,
                        PHYSICAL_HOTKEY,
                        id,
                        (chord.modifiers | (chord.key << 16)) as LPARAM,
                    )
                } != 0
            {
                self.physical_down |= bit;
                return true;
            }
        }
        false
    }

    fn finish_drawing(&mut self, point: POINT) {
        if let Some(cursor) = self.drawing_cursor.take() {
            unsafe {
                SetCursor(cursor);
            }
            super::phantom::post(self.host as HWND, super::phantom::FINISH, point, 0);
        }
    }

    fn mouse(&mut self, message: u32, data: &MSLLHOOKSTRUCT) -> bool {
        use super::phantom;
        if data.dwExtraInfo == DRAWING_CLICK {
            return false;
        }
        let host = self.host as HWND;
        if let Some((button, pressed)) = drawing_button(message) {
            if !pressed
                && self
                    .drawing_button
                    .is_some_and(|drawing| drawing.button == button)
            {
                if let Some((_, foreground, hit)) = self.drawing_start.take()
                    && foreground == unsafe { GetForegroundWindow() } as usize
                    && hit == unsafe { WindowFromPoint(data.pt) } as usize
                    && self
                        .drawing_button
                        .is_some_and(|drawing| drawing.matches(button, self.mouse_modifiers()))
                {
                    replay_click(button);
                }
                self.drawing_button = None;
                self.finish_drawing(data.pt);
                return true;
            }
            if pressed
                && self.drawing_button.is_none()
                && let Some(drawing) = self
                    .bindings
                    .try_read()
                    .ok()
                    .and_then(|bindings| bindings.drawing)
                && drawing.matches(button, self.mouse_modifiers())
                && !super::settings_window_focused()
            {
                self.drawing_button = Some(drawing);
                self.drawing_start = Some((
                    data.pt,
                    unsafe { GetForegroundWindow() } as usize,
                    unsafe { WindowFromPoint(data.pt) } as usize,
                ));
                self.gesture.pending = None;
                self.gesture.used = true;
                self.send(None, Key::Reset);
                return true;
            }
        }
        if message == WM_MOUSEMOVE
            && let Some((start, foreground, _)) = self.drawing_start
            && moved_for_drawing(start, data.pt)
        {
            self.drawing_start = None;
            if foreground == unsafe { GetForegroundWindow() } as usize
                && self
                    .drawing_button
                    .is_some_and(|drawing| drawing.matches(drawing.button, self.mouse_modifiers()))
                && phantom::post(host, phantom::START, start, 0)
            {
                self.drawing_cursor = Some(unsafe { SetCursor(null_mut()) });
                self.last_draw_move = None;
            }
        }
        match message {
            WM_MOUSEMOVE if self.drawing_cursor.is_some() => {
                if self
                    .last_draw_move
                    .is_none_or(|time| time.elapsed().as_millis() >= 8)
                {
                    self.last_draw_move = Some(Instant::now());
                    phantom::post(host, phantom::MOVE, data.pt, 0);
                    unsafe {
                        SetCursor(null_mut());
                    }
                }
                return false;
            }
            WM_MOUSEWHEEL if self.drawing_cursor.is_some() => {
                phantom::post(
                    host,
                    phantom::RESIZE,
                    data.pt,
                    (data.mouseData >> 16) as i16,
                );
                return true;
            }
            _ => {}
        }
        if matches!(message, WM_MOUSEWHEEL | WM_MOUSEHWHEEL) && phantom::has_ink() {
            phantom::post(host, phantom::CLEAR, data.pt, 0);
        }
        if matches!(
            message,
            WM_LBUTTONDOWN
                | WM_RBUTTONDOWN
                | WM_MBUTTONDOWN
                | WM_XBUTTONDOWN
                | WM_MOUSEWHEEL
                | WM_MOUSEHWHEEL
        ) {
            self.gesture.pending = None;
            self.send(None, Key::Reset);
        }
        false
    }

    fn is_registered_hotkey(&self, key: u32, modifiers: u32) -> bool {
        self.bindings.try_read().is_ok_and(|bindings| {
            bindings.chords.iter().any(|chord| {
                physical_scan(chord.key).is_none()
                    && chord.key == key
                    && chord.modifiers == modifiers
            })
        })
    }

    fn mouse_modifiers(&self) -> u32 {
        // The second key of Ctrl+Shift may be held back by the layout gesture.
        system_modifiers()
            | self
                .gesture
                .deferred
                .map_or(0, |key| modifier(key.vkCode as u16))
    }

    fn release_drawing_modifier(&mut self, vk: u16) {
        if !self
            .drawing_button
            .is_some_and(|drawing| drawing.modifiers & modifier(vk) != 0)
            || opposite_modifier(vk).is_some_and(|key| unsafe { GetAsyncKeyState(key.into()) } < 0)
        {
            return;
        }
        self.drawing_start = None;
        if self.drawing_cursor.is_none() {
            return;
        }
        let mut point = POINT::default();
        unsafe { GetPhysicalCursorPos(&mut point) };
        self.finish_drawing(point);
    }
}

fn moved_for_drawing(start: POINT, current: POINT) -> bool {
    start.x.abs_diff(current.x).max(start.y.abs_diff(current.y)) >= 5
}

fn replay_click(button: DrawingButton) {
    let (down, up) = match button {
        DrawingButton::Left => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
        DrawingButton::Right => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
        DrawingButton::Middle => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
        DrawingButton::Off => return,
    };
    let events = [down, up].map(|flags| INPUT {
        r#type: INPUT_MOUSE,
        Anonymous: INPUT_0 {
            mi: MOUSEINPUT {
                dwFlags: flags,
                dwExtraInfo: DRAWING_CLICK,
                ..Default::default()
            },
        },
    });
    if inject(&events) == 1 {
        inject(&events[1..]);
    }
}

fn drawing_button(message: u32) -> Option<(DrawingButton, bool)> {
    Some(match message {
        WM_LBUTTONDOWN => (DrawingButton::Left, true),
        WM_LBUTTONUP => (DrawingButton::Left, false),
        WM_RBUTTONDOWN => (DrawingButton::Right, true),
        WM_RBUTTONUP => (DrawingButton::Right, false),
        WM_MBUTTONDOWN => (DrawingButton::Middle, true),
        WM_MBUTTONUP => (DrawingButton::Middle, false),
        _ => return None,
    })
}

fn opposite_modifier(vk: u16) -> Option<u16> {
    Some(match vk {
        VK_LCONTROL => VK_RCONTROL,
        VK_RCONTROL => VK_LCONTROL,
        VK_LSHIFT => VK_RSHIFT,
        VK_RSHIFT => VK_LSHIFT,
        VK_LMENU => VK_RMENU,
        VK_RMENU => VK_LMENU,
        VK_LWIN => VK_RWIN,
        VK_RWIN => VK_LWIN,
        _ => return None,
    })
}

#[cfg(debug_assertions)]
fn fixture_printable(key: &KBDLLHOOKSTRUCT) -> bool {
    // Debug-only opt-in for Windows integration tests using synthetic key events.
    static WINDOW: std::sync::OnceLock<Option<usize>> = std::sync::OnceLock::new();
    let target =
        WINDOW.get_or_init(|| std::env::var("TYPLO_TEST_EDITOR_WINDOW").ok()?.parse().ok());
    target.is_some_and(|hwnd| hwnd != 0 && hwnd == unsafe { GetForegroundWindow() } as usize)
        && key.dwExtraInfo == 0
        && matches!(key.vkCode, 0x20 | 0x41..=0x5a)
        && system_modifiers() == 0
}

#[cfg(not(debug_assertions))]
fn fixture_printable(_: &KBDLLHOOKSTRUCT) -> bool {
    false
}

fn system_modifiers() -> u32 {
    [VK_SHIFT, VK_CONTROL, VK_MENU, VK_LWIN, VK_RWIN]
        .into_iter()
        .filter(|&key| (unsafe { GetAsyncKeyState(key.into()) }) < 0)
        .fold(0, |modifiers, key| modifiers | modifier(key))
}

#[derive(Default)]
struct Gesture {
    down: HashSet<u16>,
    pending: Option<usize>,
    deferred: Option<KBDLLHOOKSTRUCT>,
    used: bool,
}

#[derive(Default)]
struct Decision {
    suppress: bool,
    cycle: bool,
    replay: Option<KBDLLHOOKSTRUCT>,
}

impl Gesture {
    fn modifiers(&self) -> u32 {
        self.down.iter().fold(0, |mods, &key| mods | modifier(key))
    }

    fn handle(
        &mut self,
        key: KBDLLHOOKSTRUCT,
        down: bool,
        origin: usize,
        enabled: bool,
    ) -> Decision {
        let vk = key.vkCode as u16;
        let suppressed = self
            .deferred
            .is_some_and(|deferred| deferred.vkCode == key.vkCode);
        if !down {
            self.down.remove(&vk);
            if suppressed {
                self.deferred = None;
            }
            let mut decision = Decision {
                suppress: suppressed,
                ..Default::default()
            };
            if !self.down.iter().copied().any(is_cycle_modifier) {
                decision.cycle = self.pending.take() == Some(origin) && enabled && origin != 0;
            }
            if self.down.is_empty() {
                self.used = false;
            }
            return decision;
        }
        if !self.down.insert(vk) {
            return Decision {
                suppress: suppressed,
                ..Default::default()
            };
        }
        if self.pending.take().is_some() || self.deferred.is_some() {
            self.used = true;
            return Decision {
                replay: self.deferred.take(),
                ..Default::default()
            };
        }
        if enabled
            && !self.used
            && origin != 0
            && is_cycle_modifier(vk)
            && self.down.len() == 2
            && self.modifiers() == MOD_CONTROL | MOD_SHIFT
        {
            self.pending = Some(origin);
            self.deferred = Some(key);
            return Decision {
                suppress: true,
                ..Default::default()
            };
        }
        if !is_cycle_modifier(vk) {
            self.used = true;
        }
        Decision::default()
    }
}

fn modifier(vk: u16) -> u32 {
    match vk {
        VK_SHIFT | VK_LSHIFT | VK_RSHIFT => MOD_SHIFT,
        VK_CONTROL | VK_LCONTROL | VK_RCONTROL => MOD_CONTROL,
        VK_MENU | VK_LMENU | VK_RMENU => MOD_ALT,
        VK_LWIN | VK_RWIN => MOD_WIN,
        _ => 0,
    }
}

fn is_cycle_modifier(vk: u16) -> bool {
    matches!(modifier(vk), MOD_CONTROL | MOD_SHIFT)
}

fn replay(key: KBDLLHOOKSTRUCT) {
    let event = INPUT {
        r#type: INPUT_KEYBOARD,
        Anonymous: INPUT_0 {
            ki: KEYBDINPUT {
                wVk: key.vkCode as u16,
                wScan: key.scanCode as u16,
                dwFlags: if key.flags & LLKHF_EXTENDED != 0 {
                    KEYEVENTF_EXTENDEDKEY
                } else {
                    0
                },
                time: 0,
                dwExtraInfo: REPLAY,
            },
        },
    };
    unsafe {
        SendInput(1, &event, size_of::<INPUT>() as i32);
    }
}

thread_local! {
    static HOOK: RefCell<Option<HookState>> = const { RefCell::new(None) };
}

unsafe extern "system" fn keyboard(code: i32, message: WPARAM, data: LPARAM) -> LRESULT {
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        keyboard_action(code, message, data)
    }));
    match result {
        Ok(true) => 1,
        Ok(false) => unsafe { CallNextHookEx(null_mut(), code, message, data) },
        Err(_) => {
            inject(&take_edit_keys());
            unsafe { CallNextHookEx(null_mut(), code, message, data) }
        }
    }
}

fn keyboard_action(code: i32, message: WPARAM, data: LPARAM) -> bool {
    if code == HC_ACTION as i32 && data != 0 {
        let key = unsafe { *(data as *const KBDLLHOOKSTRUCT) };
        let down = matches!(message as u32, WM_KEYDOWN | WM_SYSKEYDOWN);
        let vk = key.vkCode as u16;
        if key.flags & LLKHF_INJECTED == 0
            && matches!(vk, 0x31..=0x39 | VK_NUMPAD1..=VK_NUMPAD9)
            && HOOK.with(|slot| {
                let Ok(mut slot) = slot.try_borrow_mut() else {
                    return false;
                };
                let Some(hook) = slot.as_mut() else {
                    return false;
                };
                let (suppress, preset) =
                    hook.brush_keys.key(vk, down, hook.drawing_cursor.is_some());
                if let Some(digit) = preset {
                    super::phantom::post(
                        hook.host as HWND,
                        super::phantom::PRESET,
                        POINT::default(),
                        digit,
                    );
                }
                suppress
            })
        {
            return true;
        }
        if !down && key.flags & LLKHF_INJECTED == 0 && modifier(vk) != 0 {
            HOOK.with(|hook| {
                if let Ok(mut hook) = hook.try_borrow_mut()
                    && let Some(hook) = hook.as_mut()
                {
                    hook.release_drawing_modifier(vk);
                }
            });
        }
        if let Some(suppress) = edit_key(key, down) {
            return suppress;
        }
        let decision = HOOK.with(|hook| {
            let Ok(mut hook) = hook.try_borrow_mut() else {
                return Decision::default();
            };
            hook.as_mut()
                .map(|hook| {
                    hook.key(message as u32, unsafe {
                        &*(data as *const KBDLLHOOKSTRUCT)
                    })
                })
                .unwrap_or_default()
        });
        if let Some(key) = decision.replay {
            replay(key);
        }
        if decision.suppress {
            return true;
        }
    }
    false
}

unsafe extern "system" fn mouse(code: i32, message: WPARAM, data: LPARAM) -> LRESULT {
    let handled = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
        code == HC_ACTION as i32
            && data != 0
            && HOOK.with(|hook| {
                let Ok(mut hook) = hook.try_borrow_mut() else {
                    return false;
                };
                hook.as_mut().is_some_and(|hook| {
                    hook.mouse(message as u32, unsafe { &*(data as *const MSLLHOOKSTRUCT) })
                })
            })
    }))
    .unwrap_or(false);
    if handled {
        return 1;
    }
    unsafe { CallNextHookEx(null_mut(), code, message, data) }
}

pub(super) struct Hooks {
    pub events: Receiver<Event>,
    bindings: Arc<RwLock<Bindings>>,
    thread: u32,
    worker: Option<JoinHandle<()>>,
}

impl Hooks {
    pub fn start(window: HWND, bindings: Bindings) -> io::Result<Self> {
        let (sender, events) = sync_channel(256);
        let (ready, startup) = sync_channel(1);
        let bindings = Arc::new(RwLock::new(bindings));
        let worker_bindings = Arc::clone(&bindings);
        let host = window as usize;
        let caps = unsafe { GetKeyState(VK_CAPITAL as i32) & 1 != 0 };
        let worker = thread::Builder::new()
            .name("typlo-input".into())
            .spawn(move || unsafe {
                let mut msg = MSG::default();
                PeekMessageW(&mut msg, null_mut(), 0, 0, PM_NOREMOVE);
                HOOK.replace(Some(HookState {
                    host,
                    events: sender,
                    bindings: worker_bindings,
                    caps,
                    caps_down: false,
                    gesture: Gesture::default(),
                    physical_down: 0,
                    pressed_scans: 0,
                    drawing_button: None,
                    drawing_start: None,
                    drawing_cursor: None,
                    brush_keys: BrushKeys::default(),
                    last_draw_move: None,
                }));
                let instance = GetModuleHandleW(null_mut());
                let keys = SetWindowsHookExW(WH_KEYBOARD_LL, Some(keyboard), instance, 0);
                if keys.is_null() {
                    let _ = ready.send(Err(io::Error::last_os_error()));
                    return;
                }
                let clicks = SetWindowsHookExW(WH_MOUSE_LL, Some(mouse), instance, 0);
                let focus = SetWinEventHook(
                    EVENT_OBJECT_FOCUS,
                    EVENT_OBJECT_FOCUS,
                    null_mut(),
                    Some(focus_event),
                    0,
                    0,
                    WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS,
                );
                if clicks.is_null() {
                    eprintln!("Typlo Rust mouse hook: {}", io::Error::last_os_error());
                }
                KEYBOARD_HOOK_LIVE.store(true, Ordering::Release);
                if ready.send(Ok(GetCurrentThreadId())).is_ok() {
                    while GetMessageW(&mut msg, null_mut(), 0, 0) > 0 {
                        DispatchMessageW(&msg);
                    }
                }
                KEYBOARD_HOOK_LIVE.store(false, Ordering::Release);
                if !clicks.is_null() {
                    UnhookWindowsHookEx(clicks);
                }
                if !focus.is_null() {
                    UnhookWinEvent(focus);
                }
                if let Some(hook) = HOOK.take() {
                    if let Some(cursor) = hook.drawing_cursor {
                        SetCursor(cursor);
                    }
                    if let Some(key) = hook.gesture.deferred {
                        replay(key);
                    }
                }
                if !keys.is_null() {
                    UnhookWindowsHookEx(keys);
                }
                inject(&take_edit_keys());
            })?;
        let thread = startup.recv().map_err(io::Error::other)??;
        Ok(Self {
            events,
            bindings,
            thread,
            worker: Some(worker),
        })
    }

    pub fn update(&self, bindings: Bindings) {
        if let Ok(mut active) = self.bindings.write() {
            *active = bindings;
        }
    }

    pub fn is_finished(&self) -> bool {
        self.worker.as_ref().is_none_or(JoinHandle::is_finished)
    }
}

impl Drop for Hooks {
    fn drop(&mut self) {
        if unsafe { PostThreadMessageW(self.thread, WM_QUIT, 0, 0) } != 0
            && let Some(worker) = self.worker.take()
        {
            let _ = worker.join();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn physical_shortcuts_follow_scan_codes_not_layout_virtual_keys() {
        let ctrl_n = Chord::parse("Ctrl+N").unwrap();
        let ctrl_a = Chord::parse("Ctrl+A").unwrap();
        let capture = Chord::parse("Win+Shift+S").unwrap();
        let bindings = Bindings {
            chords: vec![ctrl_n, ctrl_a, capture],
            physical: vec![(3, ctrl_n), (512, ctrl_a), (13, capture)],
            drawing: None,
        };
        let matches = |vk, scan, flags, modifiers| {
            physical_binding(
                &bindings,
                &KBDLLHOOKSTRUCT {
                    vkCode: vk,
                    scanCode: scan,
                    flags,
                    ..Default::default()
                },
                modifiers,
            )
        };
        assert_eq!(
            matches(b'N'.into(), 0x31, 0, MOD_CONTROL),
            Some((3, ctrl_n))
        );
        assert_eq!(matches(0xe7, 0x31, 0, MOD_CONTROL), Some((3, ctrl_n)));
        assert_eq!(matches(b'N'.into(), 0x30, 0, MOD_CONTROL), None);
        assert_eq!(
            matches(b'Q'.into(), 0x1e, 0, MOD_CONTROL),
            Some((512, ctrl_a))
        );
        assert_eq!(
            matches(b'S'.into(), 0x1f, 0, MOD_WIN | MOD_SHIFT),
            Some((13, capture))
        );
        assert_eq!(matches(b'N'.into(), 0x31, 0, MOD_CONTROL | MOD_SHIFT), None);
        assert_eq!(
            matches(b'N'.into(), 0x31, LLKHF_EXTENDED, MOD_CONTROL),
            None
        );
        assert_eq!(matches(b'N'.into(), 0, 0, MOD_CONTROL), None);
    }

    #[test]
    fn brush_digits_are_captured_only_for_a_stroke_and_do_not_repeat() {
        let mut keys = BrushKeys::default();
        for digit in 1..=9 {
            for vk in [0x30 + digit, VK_NUMPAD0 + digit] {
                assert_eq!(keys.key(vk, true, false), (false, None));
                assert_eq!(keys.key(vk, true, true), (false, None));
                assert_eq!(keys.key(vk, false, true), (false, None));
                assert_eq!(keys.key(vk, true, true), (true, Some(digit as i16)));
                assert_eq!(keys.key(vk, true, true), (true, None));
                assert_eq!(keys.key(vk, false, false), (true, None));
                assert_eq!(keys.key(vk, true, false), (false, None));
                assert_eq!(keys.key(vk, false, false), (false, None));
            }
        }
        assert_eq!(keys.key(0x30, true, true), (false, None));
    }

    #[test]
    fn paste_acknowledgment_handles_notifications_and_seals_timeouts() {
        let state = || BufferedEdit {
            sequence: sequence(),
            foreground: 1,
            control: 2,
            focus_epoch: 3,
            keys: Vec::new(),
            committed: None,
            canceled: None,
        };
        for attempt in 0..128 {
            let edit = Mutex::new(Some(state()));
            let ready = Condvar::new();
            match attempt % 4 {
                0 => {
                    assert!(!wait_for_paste(&edit, &ready, Duration::ZERO));
                    let mut lock = edit.lock().unwrap();
                    assert!(!*lock.as_mut().unwrap().committed.get_or_insert(true));
                }
                1 => {
                    edit.lock().unwrap().as_mut().unwrap().committed = Some(true);
                    assert!(wait_for_paste(&edit, &ready, Duration::ZERO));
                }
                _ => {
                    let accepted = attempt % 4 == 2;
                    thread::scope(|scope| {
                        scope.spawn(|| {
                            let mut lock = edit.lock().unwrap();
                            lock.as_mut().unwrap().committed = Some(accepted);
                            drop(lock);
                            ready.notify_one();
                        });
                        assert_eq!(
                            wait_for_paste(&edit, &ready, Duration::from_secs(2)),
                            accepted
                        );
                    });
                }
            }
        }
    }

    #[test]
    fn canceled_autocorrection_cannot_commit_a_pending_paste() {
        let flag = Arc::new(AtomicBool::new(false));
        let edit = BufferedEdit {
            sequence: sequence(),
            foreground: 1,
            control: 2,
            focus_epoch: 3,
            keys: Vec::new(),
            committed: None,
            canceled: Some(Arc::clone(&flag)),
        };
        flag.store(true, Ordering::Release);
        assert!(!edit.can_commit(
            edit.sequence,
            edit.foreground,
            edit.control,
            edit.focus_epoch
        ));
    }

    #[test]
    fn pending_paste_rejects_later_input_changed_focus_and_manual_cancellation() {
        for attempt in 0..512 {
            let canceled = Arc::new(AtomicBool::new(attempt & 1 != 0));
            let edit = BufferedEdit {
                sequence: 10,
                foreground: 1,
                control: 20,
                focus_epoch: 30,
                keys: if attempt & 2 == 0 {
                    Vec::new()
                } else {
                    vec![INPUT::default()]
                },
                committed: None,
                canceled: Some(canceled),
            };
            assert_eq!(
                edit.can_commit(
                    10 + (attempt >> 2 & 1),
                    1 + (attempt >> 3 & 1),
                    20 + (attempt >> 4 & 1),
                    30 + (attempt >> 5 & 1),
                ),
                attempt & 63 == 0
            );
        }
    }

    #[test]
    fn short_click_and_drag_have_distinct_movement() {
        let start = POINT { x: -10, y: 100 };
        assert!(!moved_for_drawing(start, POINT { x: -14, y: 104 }));
        assert!(moved_for_drawing(start, POINT { x: -15, y: 100 }));
        assert!(moved_for_drawing(start, POINT { x: -10, y: 105 }));
    }

    fn key(vk: u16) -> KBDLLHOOKSTRUCT {
        KBDLLHOOKSTRUCT {
            vkCode: vk.into(),
            ..Default::default()
        }
    }

    #[test]
    fn drawing_releases_capture_only_for_the_mouse_button_that_started_it() {
        let (events, _) = sync_channel(8);
        let mut hook = HookState {
            host: 0,
            events,
            bindings: Arc::new(RwLock::new(Bindings {
                chords: Vec::new(),
                physical: Vec::new(),
                drawing: None,
            })),
            caps: false,
            caps_down: false,
            gesture: Gesture::default(),
            physical_down: 0,
            pressed_scans: 0,
            drawing_button: Some(DrawingShortcut::default()),
            drawing_start: None,
            drawing_cursor: None,
            brush_keys: BrushKeys::default(),
            last_draw_move: None,
        };
        // The modifier was released, and the user changed the configured shortcut.
        // The original button-up still belongs to the captured stroke.
        let data = MSLLHOOKSTRUCT::default();
        assert!(!hook.mouse(WM_RBUTTONUP, &data));
        assert!(hook.drawing_button.is_some());
        assert!(hook.mouse(WM_LBUTTONUP, &data));
        assert!(hook.drawing_button.is_none());
        assert!(!hook.mouse(WM_LBUTTONUP, &data));
        assert!(!hook.mouse(WM_LBUTTONDOWN, &data));
    }

    #[test]
    fn modifier_pair_fires_once_on_release_and_replays_on_longer_shortcuts() {
        let mut gesture = Gesture::default();
        assert!(!gesture.handle(key(VK_LCONTROL), true, 1, true).suppress);
        assert!(gesture.handle(key(VK_LSHIFT), true, 1, true).suppress);
        assert!(gesture.handle(key(VK_LSHIFT), true, 1, true).suppress);
        assert!(gesture.handle(key(VK_LSHIFT), false, 1, true).suppress);
        assert!(gesture.handle(key(VK_LCONTROL), false, 1, true).cycle);
        assert!(!gesture.handle(key(VK_LCONTROL), false, 1, true).cycle);
        gesture.handle(key(VK_LCONTROL), true, 1, true);
        assert!(gesture.handle(key(VK_RSHIFT), true, 1, true).suppress);
        let next = gesture.handle(key(b'L'.into()), true, 0, true);
        assert!(!next.suppress);
        assert_eq!(next.replay.unwrap().vkCode, u32::from(VK_RSHIFT));
        assert!(!gesture.handle(key(b'L'.into()), false, 0, true).cycle);
        assert!(!gesture.handle(key(VK_RSHIFT), false, 1, true).suppress);
        assert!(!gesture.handle(key(VK_LCONTROL), false, 1, true).cycle);
    }

    #[test]
    fn injected_modifier_and_key_releases_do_not_reset_the_edit() {
        let (events, receiver) = sync_channel(8);
        let mut hook = HookState {
            host: 0,
            events,
            bindings: Arc::new(RwLock::new(Bindings {
                chords: Vec::new(),
                physical: Vec::new(),
                drawing: None,
            })),
            caps: false,
            caps_down: false,
            gesture: Gesture::default(),
            physical_down: 0,
            pressed_scans: 0,
            drawing_button: None,
            drawing_start: None,
            drawing_cursor: None,
            brush_keys: BrushKeys::default(),
            last_draw_move: None,
        };
        let injected = |vk| KBDLLHOOKSTRUCT {
            flags: LLKHF_INJECTED,
            ..key(vk)
        };
        hook.key(WM_KEYDOWN, &injected(VK_LCONTROL));
        hook.key(WM_KEYUP, &injected(VK_LCONTROL));
        hook.key(WM_KEYUP, &injected(b'A'.into()));
        assert!(receiver.try_recv().is_err());
        hook.key(WM_KEYDOWN, &injected(b'A'.into()));
        assert!(matches!(receiver.try_recv().unwrap().kind, Key::Reset));
    }
}
