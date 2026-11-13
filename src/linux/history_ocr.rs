use eframe::egui;
use std::{
    io::{self, Read, Write},
    process::{Command, Stdio},
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
        mpsc::{self, Receiver},
    },
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};

const TIMEOUT: Duration = Duration::from_secs(30);
const OUTPUT_LIMIT: u64 = 1024 * 1024;

pub(super) struct Job {
    pub result: Receiver<io::Result<String>>,
    canceled: Arc<AtomicBool>,
    worker: Option<JoinHandle<()>>,
}

impl Job {
    pub fn start(
        load: impl FnOnce() -> io::Result<Vec<u8>> + Send + 'static,
        ctx: egui::Context,
    ) -> io::Result<Self> {
        let (sender, result) = mpsc::sync_channel(1);
        let canceled = Arc::new(AtomicBool::new(false));
        let signal = Arc::clone(&canceled);
        let worker = thread::Builder::new()
            .name("typlo-ocr".into())
            .spawn(move || {
                let result = load().and_then(|bytes| recognize(bytes, &signal));
                let _ = sender.send(result);
                ctx.request_repaint();
            })?;
        Ok(Self {
            result,
            canceled,
            worker: Some(worker),
        })
    }
}

impl Drop for Job {
    fn drop(&mut self) {
        self.canceled.store(true, Ordering::Release);
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}

pub(super) fn available() -> bool {
    use std::os::unix::fs::PermissionsExt;
    std::env::var_os("PATH").is_some_and(|paths| {
        std::env::split_paths(&paths).any(|path| {
            path.join("tesseract")
                .metadata()
                .is_ok_and(|file| file.is_file() && file.permissions().mode() & 0o111 != 0)
        })
    })
}

fn recognize(image: Vec<u8>, canceled: &AtomicBool) -> io::Result<String> {
    let deadline = Instant::now() + TIMEOUT;
    let installed = run(&["--list-langs"], Vec::new(), deadline, canceled)?;
    let langs = ["eng", "rus", "ukr"]
        .into_iter()
        .filter(|name| installed.lines().any(|line| line.trim() == *name))
        .collect::<Vec<_>>()
        .join("+");
    let args = if langs.is_empty() {
        &["stdin", "stdout"][..]
    } else {
        &["stdin", "stdout", "-l", &langs][..]
    };
    let text = run(args, image, deadline, canceled)?;
    Ok(text.replace("\r\n", "\n").trim().to_owned())
}

fn run(
    args: &[&str],
    bytes: Vec<u8>,
    deadline: Instant,
    canceled: &AtomicBool,
) -> io::Result<String> {
    if canceled.load(Ordering::Acquire) {
        return Err(io::ErrorKind::Interrupted.into());
    }
    let mut child = Command::new("tesseract")
        .args(args)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .spawn()?;
    let mut stdin = child.stdin.take().expect("OCR stdin");
    let stdout = child.stdout.take().expect("OCR stdout");
    thread::scope(|scope| {
        let writer = scope.spawn(move || stdin.write_all(&bytes));
        let reader = scope.spawn(move || {
            let mut bytes = Vec::new();
            stdout.take(OUTPUT_LIMIT + 1).read_to_end(&mut bytes)?;
            if bytes.len() as u64 > OUTPUT_LIMIT {
                return Err(io::Error::other("OCR output exceeds its size limit"));
            }
            String::from_utf8(bytes).map_err(io::Error::other)
        });
        let status = loop {
            if canceled.load(Ordering::Acquire) {
                break Err(io::ErrorKind::Interrupted.into());
            }
            if Instant::now() >= deadline {
                break Err(io::ErrorKind::TimedOut.into());
            }
            match child.try_wait() {
                Ok(Some(status)) => break Ok(status),
                Ok(None) => thread::sleep(Duration::from_millis(20)),
                Err(error) => break Err(error),
            }
        };
        if status.is_err() {
            let _ = child.kill();
            let _ = child.wait();
        }
        let status = status?;
        if !status.success() {
            return Err(io::Error::other(format!("Tesseract exited with {status}")));
        }
        writer
            .join()
            .map_err(|_| io::Error::other("OCR input stopped"))??;
        reader
            .join()
            .map_err(|_| io::Error::other("OCR output stopped"))?
    })
}
