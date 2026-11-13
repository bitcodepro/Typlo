use super::{Target, WinEdit, input, wide};
use crate::{hotkeys::Action, lexicon::Candidate};
use std::{
    cell::Cell,
    io,
    ptr::null_mut,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
        mpsc,
    },
    time::{Duration, Instant},
};
use windows_sys::Win32::{
    Foundation::{HWND, POINT},
    System::LibraryLoader::GetModuleHandleW,
    UI::WindowsAndMessaging::*,
};

pub(super) const CHANGED: u32 = WM_APP + 14;
static CHOICES: Mutex<Option<Active>> = Mutex::new(None);
thread_local! { static HUD: Cell<HWND> = const { Cell::new(null_mut()) }; }

struct Active {
    session: Arc<()>,
    origin: usize,
    input: usize,
    index: usize,
    candidates: Vec<Candidate>,
    reply: mpsc::SyncSender<Option<usize>>,
}

pub(super) fn wait(
    host: usize,
    origin: usize,
    sequence: usize,
    candidates: Vec<Candidate>,
    canceled: &AtomicBool,
) -> io::Result<Option<usize>> {
    if candidates.is_empty() || input::sequence() != sequence || canceled.load(Ordering::Acquire) {
        return Ok(None);
    }
    let (reply, result) = mpsc::sync_channel(1);
    let mut state = CHOICES.lock().unwrap_or_else(|error| error.into_inner());
    if let Some(previous) = state.take() {
        let _ = previous.reply.try_send(None);
    }
    let session = Arc::new(());
    *state = Some(Active {
        session: session.clone(),
        origin,
        input: sequence,
        index: 0,
        candidates,
        reply,
    });
    drop(state);
    notify(host as HWND);

    let start = Instant::now();
    loop {
        match result.recv_timeout(Duration::from_millis(100)) {
            Ok(choice) => return Ok(choice),
            Err(mpsc::RecvTimeoutError::Disconnected) => return Ok(None),
            Err(mpsc::RecvTimeoutError::Timeout) => {}
        }
        if canceled.load(Ordering::Acquire)
            || input::sequence() != sequence
            || start.elapsed() >= Duration::from_secs(30)
            || unsafe { GetForegroundWindow() } as usize != origin
        {
            let mut state = CHOICES.lock().unwrap_or_else(|error| error.into_inner());
            // The receiver for a superseded session has already disconnected.
            if state
                .as_ref()
                .is_some_and(|choice| Arc::ptr_eq(&choice.session, &session))
            {
                state.take();
            }
            drop(state);
            notify(host as HWND);
            return Ok(None);
        }
    }
}

pub(super) fn key(host: HWND, action: Action, target: Option<Target>) -> bool {
    let mut state = CHOICES.lock().unwrap_or_else(|error| error.into_inner());
    let Some(choice) = state.as_mut() else {
        return false;
    };
    let valid = target.is_some_and(|target| target.foreground as usize == choice.origin)
        && input::sequence() == choice.input;
    if !valid || action == Action::CancelChoice {
        let _ = state.take().expect("choice is active").reply.try_send(None);
    } else if action == Action::AcceptChoice {
        let choice = state.take().expect("choice is active");
        let _ = choice.reply.try_send(Some(choice.index));
    } else if action == Action::Choices {
        choice.index = (choice.index + 1) % choice.candidates.len();
    } else {
        let _ = state.take().expect("choice is active").reply.try_send(None);
        notify(host);
        return false;
    }
    notify(host);
    valid
}

pub(super) fn cancel(host: HWND) {
    if let Some(choice) = CHOICES
        .lock()
        .unwrap_or_else(|error| error.into_inner())
        .take()
    {
        let _ = choice.reply.try_send(None);
        notify(host);
    }
}

fn notify(host: HWND) {
    unsafe {
        PostMessageW(host, CHANGED, 0, 0);
    }
}

pub(super) fn repaint() {
    let text = CHOICES
        .lock()
        .unwrap_or_else(|error| error.into_inner())
        .as_ref()
        .and_then(|choice| {
            let target = WinEdit::default().target().ok()?;
            if target.foreground as usize != choice.origin || input::sequence() != choice.input {
                return None;
            }
            let candidate = &choice.candidates[choice.index];
            Some(wide(&format!(
                "{} / {}   {} · {}",
                choice.index + 1,
                choice.candidates.len(),
                candidate.text,
                candidate.language.name()
            )))
        });
    HUD.with(|hud| unsafe {
        let mut window = hud.get();
        let Some(text) = text else {
            if !window.is_null() {
                ShowWindow(window, SW_HIDE);
            }
            return;
        };
        if window.is_null() {
            window = CreateWindowExW(
                WS_EX_NOACTIVATE | WS_EX_TOPMOST | WS_EX_TOOLWINDOW,
                wide("STATIC").as_ptr(),
                text.as_ptr(),
                WS_POPUP | WS_BORDER | 0x0200,
                0,
                0,
                1,
                1,
                null_mut(),
                null_mut(),
                GetModuleHandleW(null_mut()),
                null_mut(),
            );
            if window.is_null() {
                return;
            }
            SendMessageW(
                window,
                WM_SETFONT,
                windows_sys::Win32::Graphics::Gdi::GetStockObject(17) as usize,
                1,
            );
            hud.set(window);
        }
        SetWindowTextW(window, text.as_ptr());
        let mut point = POINT::default();
        GetCursorPos(&mut point);
        SetWindowPos(
            window,
            HWND_TOPMOST,
            point.x - 90,
            point.y - 58,
            365,
            40,
            SWP_NOACTIVATE | SWP_SHOWWINDOW,
        );
    });
}
