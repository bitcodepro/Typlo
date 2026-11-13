use super::{
    clipio,
    history::{History, Limits},
};
use std::{
    io,
    path::PathBuf,
    sync::{
        Arc,
        atomic::{AtomicUsize, Ordering},
        mpsc::{self, SyncSender},
    },
    thread,
    time::{Duration, Instant},
};
use windows_sys::Win32::Foundation::HWND;

pub struct Monitor {
    events: SyncSender<Job>,
    generation: Arc<AtomicUsize>,
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

impl Monitor {
    pub fn start(profile: PathBuf) -> io::Result<Self> {
        let generation = Arc::new(AtomicUsize::new(0));
        let save_generation = Arc::clone(&generation);
        let (snapshots, captured) = mpsc::sync_channel::<Captured>(32);
        thread::Builder::new()
            .name("typlo-clipboard-store".into())
            .spawn(move || {
                let mut history = None;
                while let Ok(mut snapshot) = captured.recv() {
                    let job = &snapshot.job;
                    let current = || job.generation == save_generation.load(Ordering::Acquire);
                    if !current() {
                        continue;
                    }
                    if let Ok(Some((clip, preview))) = snapshot
                        .clip
                        .take()
                        .expect("captured clip")
                        .normalize_image(job.limits.max_image)
                        && current()
                    {
                        if history.is_none() {
                            history = History::open(&profile).ok();
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
                            eprintln!("Typlo Rust clipboard: {error}");
                        }
                    }
                }
            })?;
        let worker_generation = Arc::clone(&generation);
        let pending_bytes = Arc::new(AtomicUsize::new(0));
        let (events, receiver) = mpsc::sync_channel::<Job>(32);
        thread::Builder::new()
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
                    if let Ok(Some(clip)) = result
                        && current()
                        && reserve(&pending_bytes, clip.size(), clip.kind)
                    {
                        let _ = snapshots.try_send(Captured {
                            job,
                            bytes: clip.size(),
                            clip: Some(clip),
                            pending_bytes: pending_bytes.clone(),
                        });
                    }
                }
            })?;
        Ok(Self { events, generation })
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
            limits: Limits {
                max_items: settings.history_items,
                max_total: u64::from(settings.history_total_mib) * 1024 * 1024,
                max_image: settings.history_image_mib as usize * 1024 * 1024,
                max_text: settings.history_text_kib as usize * 1024,
            },
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
        .fetch_update(Ordering::AcqRel, Ordering::Acquire, |current| {
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
}
