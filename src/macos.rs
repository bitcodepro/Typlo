use crate::{
    config,
    edit::{Editor, Session, Snapshot},
    layout::{self, Language},
};
use std::{
    ffi::c_void,
    io,
    ops::Range,
    path::Path,
    ptr::{self, NonNull},
    sync::{
        Arc, LazyLock, Mutex,
        atomic::{AtomicUsize, Ordering},
    },
    time::{Duration, Instant},
};

#[path = "mac_input.rs"]
mod input;
#[path = "mac_tray.rs"]
mod tray;

type Raw = *const c_void;
const NULL: Raw = ptr::null();
const MAX_UNITS: usize = 60_000;

#[repr(C)]
#[derive(Clone, Copy, Default)]
struct CfRange {
    location: isize,
    length: isize,
}

#[link(name = "CoreFoundation", kind = "framework")]
unsafe extern "C" {
    fn CFRetain(value: Raw) -> Raw;
    fn CFRelease(value: Raw);
    fn CFEqual(first: Raw, second: Raw) -> u8;
    fn CFGetTypeID(value: Raw) -> usize;
    fn CFStringGetTypeID() -> usize;
    fn CFStringGetLength(value: Raw) -> isize;
    fn CFStringGetCharacters(value: Raw, range: CfRange, buffer: *mut u16);
    fn CFStringCreateWithCharacters(allocator: Raw, characters: *const u16, length: isize) -> Raw;
    fn CFArrayGetCount(value: Raw) -> isize;
    fn CFArrayGetValueAtIndex(value: Raw, index: isize) -> Raw;
    fn CFBooleanGetValue(value: Raw) -> u8;
    fn CFDataGetBytePtr(value: Raw) -> *const u8;
}

#[link(name = "ApplicationServices", kind = "framework")]
unsafe extern "C" {
    fn AXIsProcessTrusted() -> u8;
    fn AXUIElementCreateSystemWide() -> Raw;
    fn AXUIElementGetPid(element: Raw, pid: *mut i32) -> i32;
    fn AXUIElementSetMessagingTimeout(element: Raw, timeout: f32) -> i32;
    fn AXUIElementCopyAttributeValue(element: Raw, name: Raw, result: *mut Raw) -> i32;
    fn AXUIElementIsAttributeSettable(element: Raw, name: Raw, result: *mut u8) -> i32;
    fn AXUIElementSetAttributeValue(element: Raw, name: Raw, value: Raw) -> i32;
    fn AXValueGetTypeID() -> usize;
    fn AXValueCreate(kind: i32, value: *const c_void) -> Raw;
    fn AXValueGetValue(value: Raw, kind: i32, result: *mut c_void) -> u8;
}

#[link(name = "Carbon", kind = "framework")]
unsafe extern "C" {
    fn TISCreateInputSourceList(properties: Raw, include_all: u8) -> Raw;
    fn TISCopyCurrentKeyboardInputSource() -> Raw;
    fn TISGetInputSourceProperty(source: Raw, property: Raw) -> Raw;
    fn TISSelectInputSource(source: Raw) -> i32;
    fn LMGetKbdType() -> u8;
    fn UCKeyTranslate(
        layout: *const u8,
        key: u16,
        action: u16,
        modifiers: u32,
        keyboard: u32,
        options: u32,
        dead: *mut u32,
        capacity: usize,
        length: *mut usize,
        output: *mut u16,
    ) -> i32;

    static kTISPropertyInputSourceID: Raw;
    static kTISPropertyLocalizedName: Raw;
    static kTISPropertyInputSourceLanguages: Raw;
    static kTISPropertyInputSourceIsSelectCapable: Raw;
    static kTISPropertyInputSourceCategory: Raw;
    static kTISCategoryKeyboardInputSource: Raw;
    static kTISPropertyUnicodeKeyLayoutData: Raw;
}

#[link(name = "proc")]
unsafe extern "C" {
    fn proc_name(pid: i32, output: *mut c_void, size: u32) -> i32;
}

#[derive(Debug)]
struct Cf(NonNull<c_void>);

impl Cf {
    fn take(value: Raw) -> io::Result<Self> {
        NonNull::new(value.cast_mut())
            .map(Self)
            .ok_or_else(unsupported)
    }

    fn get(&self) -> Raw {
        self.0.as_ptr()
    }

    fn string(text: &str) -> io::Result<Self> {
        let data: Vec<_> = text.encode_utf16().collect();
        Self::take(unsafe {
            CFStringCreateWithCharacters(NULL, data.as_ptr(), data.len() as isize)
        })
    }
}

impl Clone for Cf {
    fn clone(&self) -> Self {
        unsafe { CFRetain(self.get()) };
        Self(self.0)
    }
}

impl Drop for Cf {
    fn drop(&mut self) {
        unsafe { CFRelease(self.get()) };
    }
}

impl PartialEq for Cf {
    fn eq(&self, other: &Self) -> bool {
        unsafe { CFEqual(self.get(), other.get()) != 0 }
    }
}

impl Eq for Cf {}

fn units(value: Raw) -> io::Result<Vec<u16>> {
    if value.is_null() || unsafe { CFGetTypeID(value) != CFStringGetTypeID() } {
        return Err(unsupported());
    }
    let size = unsafe { CFStringGetLength(value) };
    if !(0..=MAX_UNITS as isize).contains(&size) {
        return Err(unsupported());
    }
    let mut data = vec![0; size as usize];
    if size != 0 {
        unsafe {
            CFStringGetCharacters(
                value,
                CfRange {
                    location: 0,
                    length: size,
                },
                data.as_mut_ptr(),
            )
        };
    }
    Ok(data)
}

fn string(value: Raw) -> io::Result<String> {
    String::from_utf16(&units(value)?).map_err(io::Error::other)
}

fn id(value: Raw) -> io::Result<String> {
    string(unsafe { TISGetInputSourceProperty(value, kTISPropertyInputSourceID) })
}

fn locale(source: Raw) -> u16 {
    let languages = unsafe { TISGetInputSourceProperty(source, kTISPropertyInputSourceLanguages) };
    if languages.is_null() || unsafe { CFArrayGetCount(languages) } == 0 {
        return 0;
    }
    let language = string(unsafe { CFArrayGetValueAtIndex(languages, 0) }).unwrap_or_default();
    match language.split(['-', '_']).next().unwrap_or_default() {
        "en" => 0x409,
        "ru" => 0x419,
        "uk" => 0x422,
        "fr" => 0x40c,
        "es" => 0x40a,
        "it" => 0x410,
        "de" => 0x407,
        "pt" => 0x416,
        _ => 0,
    }
}

struct Keyboard {
    sources: Cf,
    groups: Vec<(isize, String, Language)>,
}

impl Keyboard {
    fn load() -> io::Result<Self> {
        let sources = Cf::take(unsafe { TISCreateInputSourceList(NULL, 0) })?;
        let count = unsafe { CFArrayGetCount(sources.get()) }.min(256);
        let mut groups = Vec::new();
        for index in 0..count {
            let source = unsafe { CFArrayGetValueAtIndex(sources.get(), index) };
            let category =
                unsafe { TISGetInputSourceProperty(source, kTISPropertyInputSourceCategory) };
            if category.is_null()
                || unsafe { CFEqual(category, kTISCategoryKeyboardInputSource) } == 0
            {
                continue;
            }
            let select = unsafe {
                TISGetInputSourceProperty(source, kTISPropertyInputSourceIsSelectCapable)
            };
            if select.is_null() || unsafe { CFBooleanGetValue(select) } == 0 {
                continue;
            }
            let source_id = id(source)?;
            let language = Language::mac(&source_id, locale(source));
            let Language::Mac { id: key, .. } = language else {
                unreachable!()
            };
            let name =
                string(unsafe { TISGetInputSourceProperty(source, kTISPropertyLocalizedName) })
                    .unwrap_or_else(|_| source_id.clone());
            let mapping = keyboard_map(source);
            layout::register_mac_layout(
                key,
                &source_id,
                name,
                mapping
                    .as_ref()
                    .map(|[normal, shift]| (normal.as_str(), shift.as_str())),
            );
            groups.push((index, source_id, language));
        }
        Ok(Self { sources, groups })
    }

    fn current(&self) -> Option<Language> {
        let source = Cf::take(unsafe { TISCopyCurrentKeyboardInputSource() }).ok()?;
        let source = id(source.get()).ok()?;
        self.groups
            .iter()
            .find_map(|(_, name, language)| (*name == source).then_some(*language))
    }

    fn select(&self, language: Language) -> io::Result<()> {
        let (index, _, _) = self
            .groups
            .iter()
            .find(|(_, _, group)| *group == language)
            .ok_or_else(|| io::Error::other("Requested macOS input source is unavailable"))?;
        let source = unsafe { CFArrayGetValueAtIndex(self.sources.get(), *index) };
        let code = unsafe { TISSelectInputSource(source) };
        if code != 0 {
            return Err(io::Error::other(format!(
                "TISSelectInputSource returned {code}"
            )));
        }
        Ok(())
    }
}

fn keyboard_map(source: Raw) -> Option<[String; 2]> {
    const PHYSICAL: [u16; 48] = [
        50, 18, 19, 20, 21, 23, 22, 26, 28, 25, 29, 27, 24, 12, 13, 14, 15, 17, 16, 32, 34, 31, 35,
        33, 30, 0, 1, 2, 3, 5, 4, 38, 40, 37, 41, 39, 6, 7, 8, 9, 11, 45, 46, 43, 47, 44, 42, 10,
    ];
    let data = unsafe { TISGetInputSourceProperty(source, kTISPropertyUnicodeKeyLayoutData) };
    if data.is_null() {
        return None;
    }
    let data = unsafe { CFDataGetBytePtr(data) };
    if data.is_null() {
        return None;
    }
    let keyboard = u32::from(unsafe { LMGetKbdType() });
    Some([0, 2].map(|modifiers| {
        PHYSICAL
            .into_iter()
            .map(|key| {
                let mut output = [0; 4];
                let (mut dead, mut length) = (0, 0);
                let status = unsafe {
                    UCKeyTranslate(
                        data,
                        key,
                        3,
                        modifiers,
                        keyboard,
                        1,
                        &mut dead,
                        4,
                        &mut length,
                        output.as_mut_ptr(),
                    )
                };
                if status == 0 && (1..=2).contains(&length) {
                    char::decode_utf16(output[..length].iter().copied())
                        .next()
                        .and_then(Result::ok)
                        .unwrap_or('\0')
                } else {
                    '\0'
                }
            })
            .collect()
    }))
}

pub fn available_layouts() -> io::Result<Vec<Language>> {
    type Cached = Mutex<Option<(Instant, Vec<Language>)>>;
    static CACHE: LazyLock<Cached> = LazyLock::new(Mutex::default);
    let mut cached = CACHE
        .lock()
        .map_err(|_| io::Error::other("macOS layout cache unavailable"))?;
    if let Some((read, layouts)) = &*cached
        && read.elapsed() < Duration::from_secs(2)
    {
        return Ok(layouts.clone());
    }
    let layouts: Vec<_> = Keyboard::load()?
        .groups
        .into_iter()
        .map(|(_, _, language)| language)
        .collect();
    *cached = Some((Instant::now(), layouts.clone()));
    Ok(layouts)
}

pub fn active_layout() -> io::Result<Option<Language>> {
    Ok(Keyboard::load()?.current())
}

pub fn set_layout(value: &str) -> io::Result<()> {
    let keyboard = Keyboard::load()?;
    let groups: Vec<_> = keyboard
        .groups
        .iter()
        .map(|(_, _, language)| *language)
        .collect();
    let target = target(value, &groups, keyboard.current())?;
    keyboard.select(target)
}

fn target(value: &str, groups: &[Language], active: Option<Language>) -> io::Result<Language> {
    if value == "toggle" {
        return layout::next_language(groups, active).ok_or_else(unsupported);
    }
    let index = match value {
        "en" => 0,
        "ru" => 1,
        "ua" | "uk" => 2,
        _ => return Err(unsupported()),
    };
    groups
        .iter()
        .copied()
        .find(|layout| layout.dictionary_index() == Some(index))
        .ok_or_else(unsupported)
}

struct AxEditor {
    system: Cf,
    focus_attribute: Cf,
    application: Cf,
    role: Cf,
    subrole: Cf,
    value: Cf,
    range: Cf,
    selection: Cf,
    guard: Option<(Arc<AtomicUsize>, usize)>,
    recent: Option<String>,
    lease: Option<input::EditLease>,
}

impl AxEditor {
    fn new() -> io::Result<Self> {
        if unsafe { AXIsProcessTrusted() } == 0 {
            return Err(io::Error::new(
                io::ErrorKind::PermissionDenied,
                "Allow Typlo Rust in macOS Accessibility settings",
            ));
        }
        let system = Cf::take(unsafe { AXUIElementCreateSystemWide() })?;
        status(unsafe { AXUIElementSetMessagingTimeout(system.get(), 0.15) })?;
        Ok(Self {
            system,
            focus_attribute: Cf::string("AXFocusedUIElement")?,
            application: Cf::string("AXFocusedApplication")?,
            role: Cf::string("AXRole")?,
            subrole: Cf::string("AXSubrole")?,
            value: Cf::string("AXValue")?,
            range: Cf::string("AXSelectedTextRange")?,
            selection: Cf::string("AXSelectedText")?,
            guard: None,
            recent: None,
            lease: None,
        })
    }

    fn read(&self, element: &Cf, attribute: &Cf) -> io::Result<Cf> {
        let mut value = NULL;
        status(unsafe {
            AXUIElementCopyAttributeValue(element.get(), attribute.get(), &mut value)
        })?;
        Cf::take(value)
    }

    fn quiet(&self) -> bool {
        self.guard
            .as_ref()
            .is_none_or(|(current, expected)| current.load(Ordering::Acquire) == *expected)
    }

    fn set_range(&self, target: &Cf, range: Range<usize>) -> io::Result<()> {
        let location = isize::try_from(range.start).map_err(io::Error::other)?;
        let length = isize::try_from(range.len()).map_err(io::Error::other)?;
        let range = CfRange { location, length };
        let value = Cf::take(unsafe { AXValueCreate(4, (&range as *const CfRange).cast()) })?;
        status(unsafe { AXUIElementSetAttributeValue(target.get(), self.range.get(), value.get()) })
    }

    fn pid(&self) -> io::Result<i32> {
        let application = self.read(&self.system, &self.application)?;
        let mut pid = 0;
        status(unsafe { AXUIElementGetPid(application.get(), &mut pid) })?;
        Ok(pid)
    }

    fn process(&self) -> io::Result<String> {
        let pid = self.pid()?;
        let mut bytes = [0; 256];
        let written = unsafe { proc_name(pid, bytes.as_mut_ptr().cast(), bytes.len() as u32) };
        if written <= 0 {
            return Ok(String::new());
        }
        let end = bytes
            .iter()
            .position(|&byte| byte == 0)
            .unwrap_or(bytes.len());
        Ok(String::from_utf8_lossy(&bytes[..end]).into_owned())
    }
}

impl Editor for AxEditor {
    type Target = Cf;

    fn begin_operation(&mut self) {
        self.lease = None;
    }

    fn snapshot(&mut self) -> io::Result<Snapshot<Cf>> {
        if !self.quiet() {
            return Err(io::ErrorKind::Interrupted.into());
        }
        let target = self.read(&self.system, &self.focus_attribute)?;
        let role = string(self.read(&target, &self.role)?.get())?;
        let subrole = self
            .read(&target, &self.subrole)
            .and_then(|value| string(value.get()))
            .unwrap_or_default();
        if !matches!(
            role.as_str(),
            "AXTextField" | "AXTextArea" | "AXSearchField"
        ) || subrole == "AXSecureTextField"
        {
            return Err(unsupported());
        }
        let mut writable = 0;
        status(unsafe {
            AXUIElementIsAttributeSettable(target.get(), self.selection.get(), &mut writable)
        })?;
        if writable == 0 {
            return Err(unsupported());
        }
        let document = units(self.read(&target, &self.value)?.get())?;
        let range = self.read(&target, &self.range)?;
        let mut value = CfRange::default();
        if unsafe {
            CFGetTypeID(range.get()) != AXValueGetTypeID()
                || AXValueGetValue(range.get(), 4, (&mut value as *mut CfRange).cast()) == 0
        } || value.location < 0
            || value.length < 0
        {
            return Err(unsupported());
        }
        let start = value.location as usize;
        let end = start
            .checked_add(value.length as usize)
            .ok_or_else(unsupported)?;
        let selection = start..end;
        if end > document.len() || self.read(&self.system, &self.focus_attribute)? != target {
            return Err(io::ErrorKind::Interrupted.into());
        }
        Ok(Snapshot {
            target,
            document,
            selection,
        })
    }

    fn replace(
        &mut self,
        expected: &Snapshot<Cf>,
        range: Range<usize>,
        replacement: &[u16],
    ) -> io::Result<()> {
        if self.snapshot()? != *expected {
            return Err(io::ErrorKind::Interrupted.into());
        }
        let value = Cf::take(unsafe {
            CFStringCreateWithCharacters(NULL, replacement.as_ptr(), replacement.len() as isize)
        })?;
        let moved = range != expected.selection;
        if self.guard.is_some() {
            self.lease = Some(input::EditLease::acquire()?);
        } else if moved {
            return Err(unsupported());
        }
        let result = (|| {
            if self.snapshot()? != *expected
                || !self.lease.as_ref().is_none_or(input::EditLease::quiet)
            {
                return Err(io::ErrorKind::Interrupted.into());
            }
            if moved {
                self.set_range(&expected.target, range.clone())?;
            }
            let snapshot = self.snapshot()?;
            if snapshot.target != expected.target
                || snapshot.document != expected.document
                || snapshot.selection != range
                || !self.lease.as_ref().is_none_or(input::EditLease::quiet)
            {
                return Err(io::ErrorKind::Interrupted.into());
            }
            status(unsafe {
                AXUIElementSetAttributeValue(
                    expected.target.get(),
                    self.selection.get(),
                    value.get(),
                )
            })
        })();
        if result.is_err()
            && moved
            && self.snapshot().is_ok_and(|snapshot| {
                snapshot.target == expected.target
                    && snapshot.document == expected.document
                    && snapshot.selection == range
            })
        {
            let _ = self.set_range(&expected.target, expected.selection.clone());
        }
        result
    }

    fn recent_range(&self, expected: &Snapshot<Cf>) -> Option<Range<usize>> {
        crate::typing::verified_recent_word(
            &expected.document,
            expected.selection.end,
            self.recent.as_deref()?,
        )
    }

    fn move_caret(&mut self, expected: &Snapshot<Cf>, caret: usize) -> io::Result<()> {
        if self.snapshot()? != *expected || self.lease.is_none() {
            return Err(io::ErrorKind::Interrupted.into());
        }
        self.set_range(&expected.target, caret..caret)
    }
}

pub fn convert(profile: &Path, requested: &str) -> io::Result<()> {
    let keyboard = Keyboard::load()?;
    let settings = config::load(profile)?;
    let groups: Vec<_> = settings
        .cycle
        .iter()
        .copied()
        .filter(|language| {
            keyboard
                .groups
                .iter()
                .any(|(_, _, installed)| installed == language)
        })
        .collect();
    let mut editor = AxEditor::new()?;
    let mut session = Session::default();
    let current = keyboard.current();
    let language = if requested == "toggle" {
        session.convert(&mut editor, &groups, current)?
    } else {
        session.convert_to(&mut editor, target(requested, &groups, current)?, current)?
    };
    if settings.switch_layout
        && let Some(language) = language
    {
        keyboard.select(language)?;
    }
    Ok(())
}

pub fn run(profile: &Path) -> io::Result<()> {
    run_managed(profile, None)
}

pub fn run_managed(profile: &Path, ready: Option<&Path>) -> io::Result<()> {
    let instance = crate::unix_desktop::instance(profile)?;
    input::run(profile, ready, instance.signal.clone())
}

pub(crate) fn frontmost_process() -> io::Result<String> {
    AxEditor::new()?.process()
}

pub(crate) fn history_origin() -> Option<[u32; 2]> {
    let pid = u32::try_from(AxEditor::new().ok()?.pid().ok()?).ok()?;
    (pid != 0 && pid != std::process::id()).then_some([pid, pid])
}

#[cfg(feature = "gui")]
pub(crate) fn paste_history(pid: u32) -> io::Result<()> {
    crate::restore_edit_origin(pid as usize)?;
    let deadline = std::time::Instant::now() + std::time::Duration::from_millis(400);
    let pid = i32::try_from(pid).map_err(io::Error::other)?;
    let editor = AxEditor::new()?;
    loop {
        if editor.pid().ok() == Some(pid) {
            return input::paste_to(pid);
        }
        if std::time::Instant::now() >= deadline {
            return Err(io::ErrorKind::Interrupted.into());
        }
        std::thread::sleep(std::time::Duration::from_millis(8));
    }
}

fn status(value: i32) -> io::Result<()> {
    match value {
        0 => Ok(()),
        -25205 | -25206 | -25208 | -25211 | -25212 => Err(unsupported()),
        _ => Err(io::Error::other(format!(
            "macOS Accessibility returned {value}"
        ))),
    }
}

fn unsupported() -> io::Error {
    io::Error::new(
        io::ErrorKind::Unsupported,
        "The current macOS input source or field does not support this operation",
    )
}
