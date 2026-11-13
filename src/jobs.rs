use crate::spelling::{self, Issue, Request, Review};
use serde::{Serialize, de::DeserializeOwned};
use std::{
    io::{self, Read, Write},
    path::Path,
    process::{Command, Stdio},
    sync::atomic::{AtomicBool, Ordering},
    thread,
    time::{Duration, Instant},
};

const WORK_LIMIT: Duration = Duration::from_secs(30);
const RESPONSE_LIMIT: u64 = 2 * 1024 * 1024;

#[cfg(unix)]
pub(crate) struct ChoicePreview {
    choices: Vec<crate::lexicon::Candidate>,
    index: usize,
    started: Instant,
    updates: std::sync::mpsc::Sender<usize>,
    task: Task<()>,
}

#[cfg(unix)]
impl ChoicePreview {
    pub fn start(
        profile: &Path,
        choices: Vec<crate::lexicon::Candidate>,
    ) -> io::Result<Option<Self>> {
        if choices.is_empty() {
            return Ok(None);
        }
        let (updates, receiver) = std::sync::mpsc::channel();
        let display = choices.clone();
        let profile = profile.to_owned();
        let task = Task::start(move |canceled| {
            exchange_inner(
                &gui()?,
                "--layout-choice-preview",
                &profile,
                &display,
                None,
                canceled,
                Some(receiver),
            )
        })?;
        Ok(Some(Self {
            choices,
            index: 0,
            started: Instant::now(),
            updates,
            task,
        }))
    }

    pub fn alive(&self) -> bool {
        self.started.elapsed() < Duration::from_secs(30) && self.task.poll().is_none()
    }

    pub fn advance(&mut self) {
        self.index = (self.index + 1) % self.choices.len();
        let _ = self.updates.send(self.index);
    }

    pub fn selected(&self) -> &crate::lexicon::Candidate {
        &self.choices[self.index]
    }
}

#[cfg(unix)]
pub(crate) struct Task<T> {
    canceled: std::sync::Arc<AtomicBool>,
    reply: std::sync::mpsc::Receiver<io::Result<T>>,
}

#[cfg(unix)]
impl<T: Send + 'static> Task<T> {
    pub fn start(
        operation: impl FnOnce(&AtomicBool) -> io::Result<T> + Send + 'static,
    ) -> io::Result<Self> {
        let canceled = std::sync::Arc::new(AtomicBool::new(false));
        let flag = std::sync::Arc::clone(&canceled);
        let (sender, reply) = std::sync::mpsc::sync_channel(1);
        thread::Builder::new()
            .name("typlo-utility".into())
            .spawn(move || {
                let result = operation(&flag);
                let _ = sender.send(result);
            })?;
        Ok(Self { canceled, reply })
    }

    pub fn poll(&self) -> Option<io::Result<T>> {
        match self.reply.try_recv() {
            Ok(result) => Some(result),
            Err(std::sync::mpsc::TryRecvError::Empty) => None,
            Err(std::sync::mpsc::TryRecvError::Disconnected) => {
                Some(Err(io::Error::other("Typlo utility disconnected")))
            }
        }
    }
}

#[cfg(unix)]
impl<T> Drop for Task<T> {
    fn drop(&mut self) {
        self.canceled.store(true, Ordering::Release);
    }
}

pub(crate) fn gui() -> io::Result<std::path::PathBuf> {
    Ok(std::env::current_exe()?.with_file_name(if cfg!(windows) {
        "typlo-settings.exe"
    } else {
        "typlo-settings"
    }))
}

pub(crate) fn correct(
    profile: &Path,
    text: String,
    origin: usize,
    review: bool,
    canceled: &AtomicBool,
) -> io::Result<Option<String>> {
    let executable = std::env::current_exe()?;
    let request = Request {
        text,
        language: None,
        quick: !review,
    };
    let issues: Vec<Issue> = if request.text.is_empty() {
        Vec::new()
    } else {
        exchange(
            &executable,
            "--spelling-worker",
            profile,
            &request,
            Some(WORK_LIMIT),
            canceled,
        )?
    };
    if review {
        let request = Review {
            text: request.text,
            issues,
            origin,
        };
        let gui = gui()?;
        let choices: Option<Vec<Option<usize>>> =
            exchange(&gui, "--spelling-review", profile, &request, None, canceled)?;
        return choices
            .map(|choices| spelling::apply(&request.text, &request.issues, &choices))
            .transpose();
    }
    let choices: Vec<_> = issues
        .iter()
        .map(|issue| (!issue.suggestions.is_empty()).then_some(0))
        .collect();
    spelling::apply(&request.text, &issues, &choices).map(Some)
}

pub(crate) fn exchange<T: Serialize, U: DeserializeOwned>(
    executable: &Path,
    mode: &str,
    profile: &Path,
    request: &T,
    limit: Option<Duration>,
    canceled: &AtomicBool,
) -> io::Result<U> {
    exchange_inner(executable, mode, profile, request, limit, canceled, None)
}

fn exchange_inner<T: Serialize, U: DeserializeOwned>(
    executable: &Path,
    mode: &str,
    profile: &Path,
    request: &T,
    limit: Option<Duration>,
    canceled: &AtomicBool,
    updates: Option<std::sync::mpsc::Receiver<usize>>,
) -> io::Result<U> {
    if canceled.load(Ordering::Acquire) {
        return Err(io::ErrorKind::Interrupted.into());
    }
    let mut command = Command::new(executable);
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        command.creation_flags(windows_sys::Win32::System::Threading::CREATE_NO_WINDOW);
    }
    #[cfg(target_os = "linux")]
    crate::unix_desktop::parent_lifetime(&mut command);
    let mut child = command
        .arg(mode)
        .arg(profile)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .spawn()?;
    let output = child.stdout.take().expect("worker stdout is piped");
    let reader = thread::spawn(move || {
        let mut bytes = Vec::new();
        output.take(RESPONSE_LIMIT + 1).read_to_end(&mut bytes)?;
        Ok::<_, io::Error>(bytes)
    });
    let mut input = child.stdin.take().expect("worker stdin is piped");
    let result = (|| {
        serde_json::to_writer(&mut input, request).map_err(io::Error::other)?;
        let mut input = if updates.is_some() {
            input.write_all(b"\n")?;
            Some(input)
        } else {
            drop(input);
            None
        };
        let deadline = limit.map(|limit| Instant::now() + limit);
        loop {
            if canceled.load(Ordering::Acquire) {
                return Err(io::ErrorKind::Interrupted.into());
            }
            if deadline.is_some_and(|deadline| Instant::now() >= deadline) {
                return Err(io::ErrorKind::TimedOut.into());
            }
            if let Some(status) = child.try_wait()? {
                return if status.success() {
                    Ok(())
                } else {
                    Err(io::Error::other(format!(
                        "Spelling worker exited with {status}"
                    )))
                };
            }
            if let (Some(updates), Some(input)) = (&updates, &mut input)
                && let Some(index) = updates.try_iter().last()
            {
                let index = u8::try_from(index).map_err(io::Error::other)?;
                input.write_all(&[index])?;
            }
            thread::sleep(if limit.is_some() || updates.is_some() {
                Duration::from_millis(15)
            } else {
                Duration::from_millis(100)
            });
        }
    })();
    if result.is_err() {
        let _ = child.kill();
        let _ = child.wait();
    }
    let bytes = reader
        .join()
        .map_err(|_| io::Error::other("Spelling pipe reader failed"))??;
    result?;
    if bytes.len() as u64 > RESPONSE_LIMIT {
        return Err(io::Error::other("Spelling response exceeds limit"));
    }
    if bytes.is_empty() && limit.is_none() {
        return serde_json::from_slice(b"null").map_err(io::Error::other);
    }
    serde_json::from_slice(&bytes).map_err(io::Error::other)
}
