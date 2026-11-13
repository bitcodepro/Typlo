use super::{Target, WinEdit};
use crate::{
    edit::{Editor, Session, Snapshot},
    jobs::correct,
    spelling,
};
use std::{
    io,
    path::PathBuf,
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

pub const READY: u32 = WM_APP + 5;

pub struct Job {
    canceled: Arc<AtomicBool>,
    reply: Receiver<io::Result<Option<String>>>,
    snapshot: Option<Snapshot<Target>>,
    sequence: usize,
    pub review: bool,
}

impl Job {
    pub fn start(
        window: HWND,
        profile: PathBuf,
        mut editor: WinEdit,
        sequence: usize,
        review: bool,
    ) -> io::Result<Option<Self>> {
        editor.begin_operation();
        let snapshot = match editor.snapshot() {
            Ok(snapshot)
                if !snapshot.selection.is_empty()
                    && snapshot.selection.len() <= spelling::MAX_CHARACTERS =>
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
        let origin = snapshot
            .as_ref()
            .map_or(0, |snapshot| snapshot.target.foreground as usize);
        let text = snapshot
            .as_ref()
            .map(|snapshot| String::from_utf16(&snapshot.document[snapshot.selection.clone()]))
            .transpose()
            .map_err(io::Error::other)?
            .unwrap_or_default();
        let canceled = Arc::new(AtomicBool::new(false));
        let cancel_worker = Arc::clone(&canceled);
        let (sender, reply) = mpsc::sync_channel(1);
        let host = window as usize;
        thread::Builder::new()
            .name("typlo-spelling".into())
            .spawn(move || {
                let result = correct(&profile, text, origin, review, &cancel_worker);
                if sender.send(result).is_ok() {
                    unsafe { PostMessageW(host as HWND, READY, 0, 0) };
                }
            })?;
        Ok(Some(Self {
            canceled,
            reply,
            snapshot,
            sequence,
            review,
        }))
    }

    pub fn apply(&self, session: &mut Session<Target>) -> io::Result<bool> {
        let result = match self.reply.try_recv() {
            Ok(result) => result?,
            Err(TryRecvError::Empty) => return Ok(false),
            Err(TryRecvError::Disconnected) => {
                return Err(io::Error::other("Spelling worker disconnected"));
            }
        };
        let (Some(snapshot), Some(output)) = (&self.snapshot, result) else {
            return Ok(true);
        };
        let mut editor = WinEdit {
            expected: Some(snapshot.target),
            sequence: (!self.review).then_some(self.sequence),
            ..Default::default()
        };
        session.replace_selection(&mut editor, snapshot.clone(), &output)?;
        Ok(true)
    }
}

impl Drop for Job {
    fn drop(&mut self) {
        self.canceled.store(true, Ordering::Release);
    }
}
