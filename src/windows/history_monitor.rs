use super::{
    clipio,
    history::{History, Limits},
};
use std::{
    io,
    path::PathBuf,
    sync::{
        Arc,
        atomic::{AtomicU64, AtomicUsize, Ordering},
        mpsc::{self, SyncSender},
    },
    thread::{self, JoinHandle},
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};
use windows_sys::Win32::Foundation::HWND;

pub struct Monitor {
    events: SyncSender<Job>,
    generation: Arc<AtomicUsize>,
    workers: [JoinHandle<()>; 2],
}

struct Job {
    owner: usize,
    sequence: u32,
    generation: usize,
    limits: Limits,
    source: String,
}

struct Captured {
    job: Job,
    clip: Option<clipio::Clip>,
    bytes: usize,
    pending_bytes: Arc<AtomicUsize>,
}

impl Drop for Captured {
    fn drop(&mut self) {
        self.pending_bytes.fetch_sub(self.bytes, Ordering::AcqRel);
    }
}

/// Writes one line per kind of failure at most once a minute, so a locked
/// clipboard or database cannot flood the log but is never silent either.
struct Throttled {
    last: AtomicU64,
}

impl Throttled {
    const fn new() -> Self {
        Self {
            last: AtomicU64::new(0),
        }
    }

    fn report(&self, message: std::fmt::Arguments<'_>) {
        let now = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map_or(0, |elapsed| elapsed.as_secs());
        let last = self.last.load(Ordering::Relaxed);
        if (last == 0 || now >= last + 60)
            && self
                .last
                .compare_exchange(last, now.max(1), Ordering::AcqRel, Ordering::Relaxed)
                .is_ok()
        {
            crate::runtime_error!("{message}");
        }
    }
}

static OPEN_FAILED: Throttled = Throttled::new();
static STORE_FAILED: Throttled = Throttled::new();
static CAPTURE_FAILED: Throttled = Throttled::new();

impl Monitor {
    pub fn start(profile: PathBuf) -> io::Result<Self> {
        let generation = Arc::new(AtomicUsize::new(0));
        let save_generation = Arc::clone(&generation);
        let (snapshots, captured) = mpsc::sync_channel::<Captured>(32);
        let store = thread::Builder::new()
            .name("typlo-clipboard-store".into())
            .spawn(move || {
                let mut history = None;
                while let Ok(mut snapshot) = captured.recv() {
                    let job = &snapshot.job;
                    let current = || job.generation == save_generation.load(Ordering::Acquire);
                    if !current() {
                        continue;
                    }
                    let Some(clip) = snapshot.clip.take() else {
                        continue;
                    };
                    let (clip, preview) = match clip.normalize_image(job.limits.max_image) {
                        Ok(Some(normalized)) => normalized,
                        Ok(None) => continue,
                        Err(error) => {
                            STORE_FAILED.report(format_args!(
                                "Typlo clipboard history: could not prepare an image: {error}"
                            ));
                            continue;
                        }
                    };
                    if !current() {
                        continue;
                    }
                    if history.is_none() {
                        match History::open(&profile) {
                            Ok(opened) => history = Some(opened),
                            Err(error) => {
                                OPEN_FAILED.report(format_args!(
                                    "Typlo clipboard history: cannot open the history, copied items are not saved: {error}"
                                ));
                                continue;
                            }
                        }
                    }
                    if let Some(store) = &mut history
                        && current()
                        && let Err(error) = store.store_capture(
                            &clip,
                            job.limits,
                            preview.as_ref(),
                            Some(&job.source),
                        )
                    {
                        STORE_FAILED.report(format_args!("Typlo clipboard history: {error}"));
                        // Reopen on the next item: the connection may be broken.
                        history = None;
                    }
                }
            })?;
        let worker_generation = Arc::clone(&generation);
        let pending_bytes = Arc::new(AtomicUsize::new(0));
        let (events, receiver) = mpsc::sync_channel::<Job>(32);
        let capture = thread::Builder::new()
            .name("typlo-clipboard-capture".into())
            .spawn(move || {
                while let Ok(job) = receiver.recv() {
                    let current = || job.generation == worker_generation.load(Ordering::Acquire);
                    let mut result = Ok(None);
                    let timeout = Instant::now() + Duration::from_millis(120);
                    while current() && clipio::sequence() == job.sequence {
                        result = clipio::capture(
                            job.sequence,
                            job.owner as HWND,
                            job.limits.max_text,
                            job.limits.max_image,
                        );
                        if result.is_ok() || Instant::now() >= timeout {
                            break;
                        }
                        thread::sleep(Duration::from_millis(12));
                    }
                    match result {
                        Ok(Some(clip))
                            if current() && reserve(&pending_bytes, clip.size(), clip.kind) =>
                        {
                            let _ = snapshots.try_send(Captured {
                                job,
                                bytes: clip.size(),
                                clip: Some(clip),
                                pending_bytes: pending_bytes.clone(),
                            });
                        }
                        Err(error) if current() && clipio::sequence() == job.sequence => {
                            CAPTURE_FAILED.report(format_args!(
                                "Typlo clipboard history: could not read the clipboard from {}: {error}",
                                job.source
                            ));
                        }
                        _ => {}
                    }
                }
            })?;
        Ok(Self {
            events,
            generation,
            workers: [store, capture],
        })
    }

    /// True once a worker thread has stopped, for example after a panic.
    pub fn is_finished(&self) -> bool {
        self.workers.iter().any(JoinHandle::is_finished)
    }

    pub fn enqueue(
        &self,
        owner: HWND,
        sequence: u32,
        settings: &crate::config::Config,
        source: String,
    ) {
        let _ = self.events.try_send(Job {
            owner: owner as usize,
            source,
            sequence,
            generation: self.generation.load(Ordering::Acquire),
            limits: Limits::from_config(settings),
        });
    }

    pub fn invalidate(&self) {
        self.generation.fetch_add(1, Ordering::AcqRel);
    }
}

fn reserve(pending: &AtomicUsize, bytes: usize, kind: clipio::Kind) -> bool {
    const IMAGE_BUDGET: usize = 128 * 1024 * 1024;
    const SMALL_CLIP_RESERVE: usize = 16 * 1024 * 1024;
    let budget = if kind == clipio::Kind::Image {
        IMAGE_BUDGET
    } else {
        IMAGE_BUDGET + SMALL_CLIP_RESERVE
    };
    pending
        .try_update(Ordering::AcqRel, Ordering::Acquire, |current| {
            current.checked_add(bytes).filter(|&next| next <= budget)
        })
        .is_ok()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::windows::clipio::Kind;

    #[test]
    fn image_backlog_keeps_capacity_for_the_following_text_and_file() {
        let pending = AtomicUsize::new(0);
        assert!(reserve(&pending, 128 * 1024 * 1024, Kind::Image));
        assert!(!reserve(&pending, 1, Kind::Image));
        assert!(reserve(&pending, 1024, Kind::Text));
        assert!(reserve(&pending, 1024, Kind::Files));
        assert!(!reserve(&pending, 16 * 1024 * 1024, Kind::Files));
    }

    #[test]
    fn repeated_failures_are_logged_once_a_minute() {
        let throttled = Throttled::new();
        let first = throttled.last.load(Ordering::Relaxed);
        throttled.report(format_args!("first"));
        let stamped = throttled.last.load(Ordering::Relaxed);
        assert_ne!(first, stamped);
        throttled.report(format_args!("second"));
        assert_eq!(throttled.last.load(Ordering::Relaxed), stamped);
    }
}
