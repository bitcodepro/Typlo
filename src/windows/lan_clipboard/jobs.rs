use super::{Peer, announce, fingerprint, invalid, send};
use serde::{Deserialize, Serialize};
use std::{
    fs::{self, File},
    io::{self, Write},
    path::{Path, PathBuf},
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
    },
    thread,
    time::{Duration, Instant},
};

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub enum Phase {
    Sending,
    /// The other computer could not be reached and is expected to pick the
    /// entry up itself.
    Waiting,
    Delivered,
    Failed,
    Cancelled,
}

#[derive(Clone, Serialize, Deserialize)]
pub struct Job {
    pub entry: i64,
    pub phase: Phase,
    pub done: u64,
    pub total: u64,
    pub error: String,
    transfer: [u8; 16],
    attempt: [u8; 16],
    peer: [u8; 32],
}

fn root(profile: &Path) -> PathBuf {
    profile.join("lan-outbox")
}

fn read<T: serde::de::DeserializeOwned>(path: &Path) -> io::Result<Option<T>> {
    match fs::read(path) {
        Ok(data) => serde_json::from_slice(&data)
            .map(Some)
            .map_err(io::Error::other),
        Err(error) if error.kind() == io::ErrorKind::NotFound => Ok(None),
        Err(error) => Err(error),
    }
}

fn write(directory: &Path, name: &str, value: &impl Serialize) -> io::Result<()> {
    let mut file = tempfile::NamedTempFile::new_in(directory)?;
    serde_json::to_writer(file.as_file_mut(), value).map_err(io::Error::other)?;
    file.as_file_mut().flush()?;
    file.persist(directory.join(name))
        .map_err(|error| error.error)?;
    Ok(())
}

fn lock(directory: &Path) -> io::Result<File> {
    File::options()
        .create(true)
        .truncate(false)
        .read(true)
        .write(true)
        .open(directory.join("transfer.lock"))
}

pub fn status(profile: &Path) -> io::Result<Option<Job>> {
    let directory = root(profile);
    let Some(mut job): Option<Job> = read(&directory.join("status.json"))? else {
        return Ok(None);
    };
    if matches!(job.phase, Phase::Sending | Phase::Waiting) && lock(&directory)?.try_lock().is_ok()
    {
        job.phase = Phase::Failed;
        job.error = "The sender stopped before it received confirmation".into();
    }
    Ok(Some(job))
}

/// How long an undelivered entry waits for the other computer to pick it up.
const PULL_WAIT: Duration = Duration::from_secs(15 * 60);
/// How often a waiting sender searches for the other computer.
const ANNOUNCE_EVERY: Duration = Duration::from_secs(10);

/// An entry the paired computer may pick up with a pull request.
#[derive(Clone, Serialize, Deserialize)]
pub(super) struct Pending {
    pub entry: i64,
    pub transfer: [u8; 16],
    attempt: [u8; 16],
    peer: [u8; 32],
}

/// Progress of a pull, written by the resident and read by the sender job.
#[derive(Serialize, Deserialize)]
struct PullState {
    attempt: [u8; 16],
    done: u64,
    total: u64,
    finished: bool,
    error: String,
}

/// The entry waiting for `peer`, if its sender job is still running.
pub(super) fn pending(profile: &Path, peer: &[u8; 32]) -> io::Result<Option<Pending>> {
    let directory = root(profile);
    let Some(pending): Option<Pending> = read(&directory.join("pull.json"))? else {
        return Ok(None);
    };
    if &pending.peer != peer || lock(&directory)?.try_lock().is_ok() {
        return Ok(None);
    }
    Ok(Some(pending))
}

impl Pending {
    fn is_current(&self, profile: &Path) -> io::Result<bool> {
        Ok(read::<Self>(&root(profile).join("pull.json"))?
            .is_some_and(|saved| saved.attempt == self.attempt))
    }

    fn state(
        &self,
        profile: &Path,
        done: u64,
        total: u64,
        finished: bool,
        error: String,
    ) -> io::Result<()> {
        let state = PullState {
            attempt: self.attempt,
            done,
            total,
            finished,
            error,
        };
        write(&root(profile), "pull-state.json", &state)
    }

    /// Records progress; fails when the sender cancelled or gave up.
    pub(super) fn progress(&self, profile: &Path, done: u64, total: u64) -> io::Result<()> {
        if !self.is_current(profile)? {
            return Err(io::Error::new(
                io::ErrorKind::ConnectionAborted,
                "Transfer cancelled",
            ));
        }
        self.state(profile, done, total, false, String::new())
    }

    pub(super) fn finish(&self, profile: &Path, result: &io::Result<()>) {
        let error = result
            .as_ref()
            .err()
            .map(ToString::to_string)
            .unwrap_or_default();
        if self.is_current(profile).unwrap_or(false) {
            let _ = self.state(profile, 0, 0, true, error);
        }
    }
}

pub fn cancel(profile: &Path, job: &Job) -> io::Result<()> {
    write(&root(profile), "cancel.json", &job.attempt)
}

fn random_id() -> io::Result<[u8; 16]> {
    let mut id = [0; 16];
    rustls::crypto::ring::default_provider()
        .secure_random
        .fill(&mut id)
        .map_err(|_| io::Error::other("Could not generate a transfer ID"))?;
    Ok(id)
}

pub fn run(profile: &Path, entry: i64, retry: bool) -> io::Result<()> {
    let directory = root(profile);
    fs::create_dir_all(&directory)?;
    let lock = lock(&directory)?;
    lock.try_lock()
        .map_err(|_| io::Error::other("A transfer is already running"))?;
    let certificate =
        Peer::load(profile)?.ok_or_else(|| invalid("Pair the other computer in Settings"))?;
    let peer = fingerprint(&certificate.certificate);
    let transfer = if retry {
        let old: Job =
            read(&directory.join("status.json"))?.ok_or_else(|| invalid("No transfer to retry"))?;
        if old.entry != entry || old.peer != peer || old.phase == Phase::Delivered {
            return Err(invalid(
                "This transfer cannot be retried for the current computer",
            ));
        }
        old.transfer
    } else {
        random_id()?
    };
    let mut job = Job {
        entry,
        phase: Phase::Sending,
        done: 0,
        total: 0,
        error: String::new(),
        transfer,
        attempt: random_id()?,
        peer,
    };
    write(&directory, "status.json", &job)?;
    let cancelled = Arc::new(AtomicBool::new(false));
    let finished = Arc::new(AtomicBool::new(false));
    let watcher = {
        let path = directory.join("cancel.json");
        let cancelled = cancelled.clone();
        let finished = finished.clone();
        let attempt = job.attempt;
        thread::spawn(move || {
            while !finished.load(Ordering::Acquire) {
                if read::<[u8; 16]>(&path).is_ok_and(|value| value == Some(attempt)) {
                    cancelled.store(true, Ordering::Release);
                    return;
                }
                thread::park_timeout(Duration::from_millis(80));
            }
        })
    };
    let mut last = Instant::now();
    let result = send(
        profile,
        entry,
        &peer,
        &transfer,
        cancelled.clone(),
        |done, total| {
            let changed = job.total != total;
            job.done = done;
            job.total = total;
            if changed || last.elapsed() >= Duration::from_millis(200) {
                write(&directory, "status.json", &job)?;
                last = Instant::now();
            }
            Ok(())
        },
    );
    let result = match result {
        Err(error)
            if error.kind() == io::ErrorKind::NotConnected
                && !cancelled.load(Ordering::Acquire) =>
        {
            job.phase = Phase::Waiting;
            job.error = error.to_string();
            write(&directory, "status.json", &job)?;
            wait_for_pull(&directory, &mut job, &cancelled)
        }
        result => result,
    };
    finished.store(true, Ordering::Release);
    watcher.thread().unpark();
    let _ = watcher.join();
    match result {
        Ok(()) => {
            job.phase = Phase::Delivered;
            job.done = job.total;
        }
        Err(error) if cancelled.load(Ordering::Acquire) => {
            job.phase = Phase::Cancelled;
            job.error = error.to_string();
        }
        Err(error) => {
            job.phase = Phase::Failed;
            job.error = error.to_string();
        }
    }
    write(&directory, "status.json", &job)
}

/// Offers the entry to the paired computer until it picks it up, the user
/// cancels, or [`PULL_WAIT`] passes.
fn wait_for_pull(directory: &Path, job: &mut Job, cancelled: &AtomicBool) -> io::Result<()> {
    let pending = Pending {
        entry: job.entry,
        transfer: job.transfer,
        attempt: job.attempt,
        peer: job.peer,
    };
    let state_path = directory.join("pull-state.json");
    let _ = fs::remove_file(&state_path);
    write(directory, "pull.json", &pending)?;
    let deadline = Instant::now() + PULL_WAIT;
    let mut announced = None::<Instant>;
    let result = loop {
        // The search is what makes the other computer connect here. Nothing
        // is sent while no entry is waiting.
        if announced.is_none_or(|time| time.elapsed() >= ANNOUNCE_EVERY)
            && let Some(profile) = directory.parent()
        {
            let _ = announce(profile);
            announced = Some(Instant::now());
        }
        if cancelled.load(Ordering::Acquire) {
            break Err(io::Error::new(
                io::ErrorKind::ConnectionAborted,
                "Transfer cancelled",
            ));
        }
        if let Ok(Some(state)) = read::<PullState>(&state_path)
            && state.attempt == job.attempt
        {
            if state.finished && state.error.is_empty() {
                break Ok(());
            }
            let (phase, done, total, error) = if state.finished {
                // The pull failed; the other computer will ask again.
                (Phase::Waiting, 0, 0, state.error)
            } else {
                (Phase::Sending, state.done, state.total, String::new())
            };
            if (phase, done, total) != (job.phase, job.done, job.total)
                || error != job.error && !error.is_empty()
            {
                job.phase = phase;
                job.done = done;
                job.total = total;
                if !error.is_empty() {
                    job.error = error;
                }
                write(directory, "status.json", job)?;
            }
        }
        if Instant::now() >= deadline {
            break Err(invalid(
                "The other computer could not be reached and did not pick up the entry",
            ));
        }
        thread::park_timeout(Duration::from_millis(150));
    };
    // Remove the offer first so a pull in progress stops at its next step.
    let _ = fs::remove_file(directory.join("pull.json"));
    let _ = fs::remove_file(state_path);
    result
}

/// Offers `entry` to `peer` as an unreachable sender would, for tests.
#[cfg(test)]
pub(super) fn offer_for_test(
    profile: &Path,
    entry: i64,
    peer: [u8; 32],
    cancelled: &AtomicBool,
) -> io::Result<()> {
    let directory = root(profile);
    fs::create_dir_all(&directory)?;
    let lock = lock(&directory)?;
    lock.try_lock()
        .map_err(|_| io::Error::other("A transfer is already running"))?;
    let mut job = Job {
        entry,
        phase: Phase::Waiting,
        done: 0,
        total: 0,
        error: String::new(),
        transfer: random_id()?,
        attempt: random_id()?,
        peer,
    };
    wait_for_pull(&directory, &mut job, cancelled)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn interrupted_job_keeps_its_transfer_id_and_attempt_scoped_cancel() {
        let profile = tempfile::tempdir().unwrap();
        let directory = root(profile.path());
        fs::create_dir(&directory).unwrap();
        let active = lock(&directory).unwrap();
        active.try_lock().unwrap();
        let job = Job {
            entry: 13,
            phase: Phase::Sending,
            done: 2,
            total: 4,
            error: String::new(),
            transfer: [1; 16],
            attempt: [2; 16],
            peer: [3; 32],
        };
        write(&directory, "status.json", &job).unwrap();
        assert_eq!(
            status(profile.path()).unwrap().unwrap().phase,
            Phase::Sending
        );
        cancel(profile.path(), &job).unwrap();
        assert_eq!(
            read::<[u8; 16]>(&directory.join("cancel.json")).unwrap(),
            Some(job.attempt)
        );
        drop(active);
        let stopped = status(profile.path()).unwrap().unwrap();
        assert_eq!(stopped.phase, Phase::Failed);
        assert_eq!(stopped.transfer, job.transfer);
        assert_eq!(stopped.entry, job.entry);
    }
}
