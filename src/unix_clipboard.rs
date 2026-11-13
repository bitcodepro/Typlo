use crate::{
    config,
    unix_history::{Clip, History},
};
use std::{io, path::Path};

fn paths(bytes: &[u8]) -> Option<Clip> {
    use std::os::unix::ffi::OsStringExt;
    let data = std::str::from_utf8(bytes).ok()?;
    let mut result = Vec::new();
    for line in data
        .lines()
        .filter(|line| !line.is_empty() && !line.starts_with('#'))
        .take(256)
    {
        let path = line.strip_prefix("file://")?;
        let path = path.trim_prefix("localhost");
        if !path.starts_with('/') || path.contains(['#', '?']) {
            return None;
        }
        let path: Vec<u8> = percent_encoding::percent_decode_str(path).collect();
        if path.contains(&0) {
            return None;
        }
        result.push(std::path::PathBuf::from(std::ffi::OsString::from_vec(path)));
    }
    (!result.is_empty()).then_some(Clip::Files(result))
}

#[cfg(target_os = "linux")]
mod platform {
    use super::*;
    use std::{
        ffi::OsStr,
        io::{Read, Write},
        process::{Command, Stdio},
        thread,
        time::{Duration, Instant},
    };
    use x11rb::{
        connection::Connection,
        protocol::{
            Event,
            xfixes::{self, ConnectionExt as _},
            xproto::ConnectionExt as _,
        },
        xcb_ffi::XCBConnection,
    };

    fn owner_process(connection: &XCBConnection, owner: u32) -> Option<String> {
        use x11rb::protocol::res::{ClientIdMask, ClientIdSpec, ConnectionExt as _};
        let reply = connection
            .res_query_client_ids(&[ClientIdSpec {
                client: owner,
                mask: ClientIdMask::LOCAL_CLIENT_PID,
            }])
            .ok()?
            .reply()
            .ok()?;
        let pid = *reply.ids.first()?.value.first()?;
        let path = std::fs::read_link(format!("/proc/{pid}/exe")).ok()?;
        Some(path.file_name()?.to_string_lossy().into_owned())
    }

    pub fn wayland() -> bool {
        std::env::var_os("WAYLAND_DISPLAY").is_some()
            || std::env::var("XDG_SESSION_TYPE")
                .is_ok_and(|session| session.eq_ignore_ascii_case("wayland"))
    }

    pub fn start(profile: &Path) -> io::Result<()> {
        let profile = profile.to_owned();
        thread::Builder::new()
            .name("typlo-clipboard".into())
            .spawn(move || {
                if let Err(error) = watch_x11(&profile) {
                    eprintln!("Typlo clipboard: {error}");
                }
            })?;
        Ok(())
    }

    pub fn watch_x11(profile: &Path) -> io::Result<()> {
        let (conn, screen) = XCBConnection::connect(None).map_err(io::Error::other)?;
        let root = conn.setup().roots[screen].root;
        conn.xfixes_query_version(5, 0)
            .map_err(io::Error::other)?
            .reply()
            .map_err(io::Error::other)?;
        let atom = conn
            .intern_atom(false, b"CLIPBOARD")
            .map_err(io::Error::other)?
            .reply()
            .map_err(io::Error::other)?
            .atom;
        conn.xfixes_select_selection_input(
            root,
            atom,
            xfixes::SelectionEventMask::SET_SELECTION_OWNER,
        )
        .map_err(io::Error::other)?
        .check()
        .map_err(io::Error::other)?;
        let mut store = None;
        loop {
            let Event::XfixesSelectionNotify(event) =
                conn.wait_for_event().map_err(io::Error::other)?
            else {
                continue;
            };
            if event.owner == 0 || event.selection != atom {
                continue;
            }
            let settings = config::load(profile)?;
            let Some(process) = owner_process(&conn, event.owner) else {
                continue;
            };
            if !settings.history_allowed(&process) || crate::unix_history::paused(profile) {
                continue;
            }
            let result = capture_x11(&settings);
            let now = conn
                .get_selection_owner(atom)
                .map_err(io::Error::other)?
                .reply()
                .map_err(io::Error::other)?
                .owner;
            if event.owner != now {
                continue;
            }
            if let Ok(Some(clip)) = result {
                if store.is_none() {
                    store = Some(History::open(profile)?);
                }
                store
                    .as_mut()
                    .expect("history opened")
                    .store(&clip, &settings)?;
            }
        }
    }

    fn capture_x11(settings: &config::Config) -> io::Result<Option<Clip>> {
        let formats = xclip("TARGETS", 4096)?;
        let formats = std::str::from_utf8(&formats).map_err(io::Error::other)?;
        let has = |name| formats.lines().any(|target| target.trim() == name);
        if has("x-kde-passwordManagerHint") || has("application/x-keepassxc") {
            return Ok(None);
        }
        let text_limit = settings.history_text_kib as usize * 1024;
        if has("text/uri-list") {
            return Ok(paths(&xclip("text/uri-list", text_limit)?));
        }
        if has("image/png") {
            let data = xclip(
                "image/png",
                settings.history_image_mib as usize * 1024 * 1024,
            )?;
            return Ok((!data.is_empty()).then_some(Clip::Image(data)));
        }
        if has("UTF8_STRING") {
            let text =
                String::from_utf8(xclip("UTF8_STRING", text_limit)?).map_err(io::Error::other)?;
            let html = has("text/html")
                .then(|| xclip("text/html", text_limit).ok())
                .flatten()
                .and_then(|data| String::from_utf8(data).ok());
            return Ok((!text.is_empty()).then_some(Clip::Text { text, html }));
        }
        Ok(None)
    }

    fn xclip(format: &str, limit: usize) -> io::Result<Vec<u8>> {
        bounded(
            "xclip",
            &["-selection", "clipboard", "-target", format, "-out"],
            limit,
        )
    }

    pub fn watch_wayland(
        profile: &Path,
        ready: Option<&Path>,
        mut stop: impl FnMut() -> io::Result<bool>,
    ) -> io::Result<()> {
        let executable = std::env::current_exe()?;
        let mut watchers = Vec::new();
        let mut inhibit = crate::unix_desktop::Inhibit::default();
        for format in ["text", "image/png"] {
            let mut command = Command::new("wl-paste");
            command
                .args(["--type", format, "--watch"])
                .arg(&executable)
                .arg("--clipboard-event")
                .arg(profile)
                .arg(format)
                .stdin(Stdio::null());
            crate::unix_desktop::parent_lifetime(&mut command);
            let child = command.spawn();
            match child {
                Ok(child) => watchers.push(child),
                Err(error) => {
                    for mut child in watchers {
                        let _ = child.kill();
                        let _ = child.wait();
                    }
                    return Err(error);
                }
            }
        }
        let mut ready = ready;
        if ready.is_some() {
            thread::sleep(Duration::from_millis(160));
        }
        loop {
            if stop()? {
                for mut child in watchers {
                    let _ = child.kill();
                    let _ = child.wait();
                }
                return Ok(());
            }
            if let Err(error) =
                config::load(profile).and_then(|settings| inhibit.apply(settings.keep_screen_on))
            {
                eprintln!("Typlo desktop: {error}");
            }
            if watchers
                .iter_mut()
                .any(|watcher| watcher.try_wait().is_ok_and(|status| status.is_some()))
            {
                for mut child in watchers {
                    let _ = child.kill();
                    let _ = child.wait();
                }
                return Err(io::Error::other(
                    "The Wayland compositor ended clipboard monitoring",
                ));
            }
            if let Some(report) = ready.take() {
                std::fs::write(report, b"ready")?;
            }
            thread::sleep(Duration::from_millis(750));
        }
    }

    pub fn record(profile: &Path, kind: &str) -> io::Result<()> {
        let settings = config::load(profile)?;
        if !settings.history_enabled
            || crate::unix_history::paused(profile)
            || std::env::var("CLIPBOARD_STATE").is_ok_and(|state| !matches!(state.as_str(), "data"))
        {
            return Ok(());
        }
        let mut data = Vec::new();
        let limit = match kind {
            "text" => settings.history_text_kib as usize * 1024,
            "image/png" => settings.history_image_mib as usize * 1024 * 1024,
            _ => return Ok(()),
        };
        io::stdin()
            .lock()
            .take(limit as u64 + 1)
            .read_to_end(&mut data)?;
        if data.is_empty() || data.len() > limit {
            return Ok(());
        }
        let clip = if kind == "image/png" {
            if !data.starts_with(b"\x89PNG\r\n\x1a\n") {
                return Ok(());
            }
            Clip::Image(data)
        } else {
            let offers = bounded("wl-paste", &["--list-types"], 4096).unwrap_or_default();
            if String::from_utf8_lossy(&offers)
                .lines()
                .any(|mime| mime == "text/uri-list")
                && let Ok(uris) = bounded(
                    "wl-paste",
                    &["--type", "text/uri-list", "--no-newline"],
                    limit,
                )
                && let Some(files) = paths(&uris)
            {
                files
            } else {
                Clip::Text {
                    text: String::from_utf8(data).map_err(io::Error::other)?,
                    html: None,
                }
            }
        };
        History::open(profile)?.store(&clip, &settings)
    }

    pub fn convert_clipboard(profile: &Path, value: &str) -> io::Result<()> {
        use crate::layout::{self, Language};
        let settings = config::load(profile)?;
        let read = || {
            bounded(
                "wl-paste",
                &["--type", "text", "--no-newline"],
                settings.history_text_kib as usize * 1024,
            )
        };
        let original = read()?;
        let text = std::str::from_utf8(&original).map_err(io::Error::other)?;
        let target = match value {
            "en" => Language::En,
            "ru" => Language::Ru,
            "ua" | "uk" => Language::Uk,
            "toggle" => {
                layout::next_language(&settings.cycle, layout::detect(text, &settings.cycle, None))
                    .ok_or_else(|| io::Error::other("Choose at least two layouts"))?
            }
            _ => {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidInput,
                    "Use en, ru, uk or toggle",
                ));
            }
        };
        let result = layout::convert_to(text, target, None);
        if result.as_str() != text {
            if read()? != original {
                return Err(io::ErrorKind::Interrupted.into());
            }
            write(
                "wl-copy",
                &["--type", "text/plain;charset=utf-8"],
                result.as_bytes(),
            )?;
        }
        Ok(())
    }

    fn bounded(program: impl AsRef<OsStr>, args: &[&str], limit: usize) -> io::Result<Vec<u8>> {
        let mut child = Command::new(program)
            .args(args)
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .stdin(Stdio::null())
            .spawn()?;
        let output = child.stdout.take().expect("clipboard pipe");
        let reader = thread::spawn(move || {
            let mut bytes = Vec::new();
            output.take(limit as u64 + 1).read_to_end(&mut bytes)?;
            Ok::<_, io::Error>(bytes)
        });
        let deadline = Instant::now() + Duration::from_millis(650);
        let result = loop {
            match child.try_wait() {
                Ok(Some(status)) if status.success() => break Ok(()),
                Ok(Some(_)) => break Err(io::Error::other("Clipboard transfer failed")),
                Ok(None) if Instant::now() < deadline => thread::sleep(Duration::from_millis(3)),
                Ok(None) => break Err(io::ErrorKind::TimedOut.into()),
                Err(error) => break Err(error),
            }
        };
        if result.is_err() {
            let _ = child.kill();
            let _ = child.wait();
        }
        let bytes = reader
            .join()
            .map_err(|_| io::Error::other("Clipboard read failed"))??;
        result?;
        if bytes.len() > limit {
            return Err(io::Error::other(
                "Clipboard item exceeds the configured limit",
            ));
        }
        Ok(bytes)
    }

    fn write(program: &str, args: &[&str], bytes: &[u8]) -> io::Result<()> {
        let mut child = Command::new(program)
            .args(args)
            .stdin(Stdio::piped())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()?;
        child
            .stdin
            .take()
            .expect("clipboard pipe")
            .write_all(bytes)?;
        if !child.wait()?.success() {
            return Err(io::Error::other("Clipboard write failed"));
        }
        Ok(())
    }
}

#[cfg(target_os = "macos")]
mod platform {
    use super::*;
    use objc2::rc::autoreleasepool;
    use objc2_app_kit::{
        NSPasteboard, NSPasteboardTypeFileURL, NSPasteboardTypeHTML, NSPasteboardTypePNG,
        NSPasteboardTypeString, NSPasteboardTypeTIFF,
    };
    use objc2_foundation::NSData;
    use std::{path::PathBuf, ptr::NonNull, thread, time::Duration};

    pub fn start(profile: &Path) -> io::Result<()> {
        let profile = profile.to_owned();
        thread::Builder::new()
            .name("typlo-clipboard".into())
            .spawn(move || {
                if let Err(error) = watch(profile) {
                    eprintln!("Typlo clipboard: {error}");
                }
            })?;
        Ok(())
    }

    fn data(data: &NSData, limit: usize) -> Option<Vec<u8>> {
        let size = data.length();
        if size == 0 || size > limit {
            return None;
        }
        let mut bytes = vec![0u8; size];
        let pointer = NonNull::new(bytes.as_mut_ptr()).expect("nonempty data");
        unsafe { data.getBytes_length(pointer.cast(), size) };
        Some(bytes)
    }

    fn watch(profile: PathBuf) -> io::Result<()> {
        let mut version = autoreleasepool(|_| NSPasteboard::generalPasteboard().changeCount());
        let mut store = None;
        loop {
            thread::sleep(Duration::from_millis(250));
            autoreleasepool(|_| -> io::Result<()> {
                let board = NSPasteboard::generalPasteboard();
                let change = board.changeCount();
                if version == change {
                    return Ok(());
                }
                version = change;
                let settings = config::load(&profile)?;
                if !settings.history_enabled || crate::unix_history::paused(&profile) {
                    return Ok(());
                }
                let process = crate::macos::frontmost_process().unwrap_or_default();
                if !settings.history_allowed(&process) {
                    return Ok(());
                }
                let Some(clip) = capture(&board, &settings) else {
                    return Ok(());
                };
                if board.changeCount() != change {
                    return Ok(());
                }
                if store.is_none() {
                    store = Some(History::open(&profile)?);
                }
                store
                    .as_mut()
                    .expect("history opened")
                    .store(&clip, &settings)
            })?;
        }
    }

    fn capture(board: &NSPasteboard, settings: &config::Config) -> Option<Clip> {
        let types = board.types()?;
        let concealed = (0..types.count()).any(|index| {
            matches!(
                types.objectAtIndex(index).to_string().as_str(),
                "org.nspasteboard.ConcealedType" | "org.nspasteboard.TransientType"
            )
        });
        if concealed {
            return None;
        }
        let text_limit = settings.history_text_kib as usize * 1024;
        let image_limit = settings.history_image_mib as usize * 1024 * 1024;
        unsafe {
            if let Some(items) = board.pasteboardItems() {
                let mut found = Vec::new();
                for index in 0..items.count().min(256) {
                    if let Some(uri) = items
                        .objectAtIndex(index)
                        .dataForType(NSPasteboardTypeFileURL)
                        && let Some(bytes) = data(&uri, text_limit)
                        && let Some(Clip::Files(mut parsed)) = paths(&bytes)
                    {
                        found.append(&mut parsed);
                    }
                }
                if !found.is_empty() {
                    return Some(Clip::Files(found));
                }
            }
            for kind in [NSPasteboardTypePNG, NSPasteboardTypeTIFF] {
                if let Some(image) = board
                    .dataForType(kind)
                    .and_then(|item| data(&item, image_limit))
                {
                    return Some(Clip::Image(image));
                }
            }
            let text = board
                .dataForType(NSPasteboardTypeString)
                .and_then(|item| data(&item, text_limit))?;
            let text = String::from_utf8(text).ok()?;
            let html = board
                .dataForType(NSPasteboardTypeHTML)
                .and_then(|item| data(&item, text_limit))
                .and_then(|data| String::from_utf8(data).ok());
            Some(Clip::Text { text, html })
        }
    }
}

pub use platform::start;
#[cfg(target_os = "linux")]
pub use platform::{convert_clipboard, record, watch_wayland, wayland};
pub fn open_history(profile: &Path) -> io::Result<std::process::Child> {
    open_history_at(profile, None)
}

fn open_history_at(profile: &Path, origin: Option<[u32; 2]>) -> io::Result<std::process::Child> {
    std::process::Command::new(std::env::current_exe()?.with_file_name("typlo-settings"))
        .arg("--history")
        .arg(profile)
        .args(origin.unwrap_or_default().map(|value| value.to_string()))
        .stdin(std::process::Stdio::piped())
        .spawn()
}

pub fn history_action(profile: &Path, child: &mut Option<std::process::Child>) -> io::Result<()> {
    history_action_with_origin(profile, child, None)
}

pub(crate) fn history_action_with_origin(
    profile: &Path,
    child: &mut Option<std::process::Child>,
    origin: Option<[u32; 2]>,
) -> io::Result<()> {
    use std::io::Write;
    if let Some(existing) = child.as_mut()
        && existing.try_wait()?.is_none()
        && let Some(stdin) = existing.stdin.as_mut()
    {
        let [window, pid] = origin.unwrap_or_default().map(u32::to_le_bytes);
        let mut message = [0; 8];
        message[..4].copy_from_slice(&window);
        message[4..].copy_from_slice(&pid);
        match stdin.write_all(&message) {
            Ok(()) => return Ok(()),
            Err(error) if error.kind() == io::ErrorKind::BrokenPipe => {}
            Err(error) => return Err(error),
        }
    }
    *child = Some(open_history_at(profile, origin)?);
    Ok(())
}
