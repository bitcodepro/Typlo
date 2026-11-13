use super::{Peer, fingerprint, invalid, send};
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
    if job.phase == Phase::Sending && lock(&directory)?.try_lock().is_ok() {
        job.phase = Phase::Failed;
        job.error = "The sender stopped before it received confirmation".into();
    }
    Ok(Some(job))
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
