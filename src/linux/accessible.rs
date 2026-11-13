use super::{Desktop, err};
use crate::edit::{Editor, Snapshot};
use std::{
    ffi::{CStr, CString, c_char, c_void},
    io,
    ops::Range,
    ptr::{self, NonNull},
    rc::Rc,
    time::{Duration, Instant},
};
use x11rb::{
    CURRENT_TIME,
    protocol::xproto::{ConnectionExt as _, GrabMode, GrabStatus, Window},
};

type Object = *mut c_void;
type ErrorSlot = *mut Object;
const EDITABLE: i32 = 7;
const FOCUSED: i32 = 12;
const MULTI_LINE: i32 = 17;
const SENSITIVE: i32 = 24;
const SINGLE_LINE: i32 = 26;
const TEXT: i32 = 61;
const ENTRY: i32 = 79;
const LIMIT: usize = 60_000;

#[repr(C)]
struct TextRange {
    start: i32,
    end: i32,
}

macro_rules! atspi {
    ($($field:ident: $ty:ty = $symbol:literal,)*) => {
        pub(super) struct Api {
            $($field: $ty,)*
            _library: libloading::Library,
        }
        impl Api {
            pub fn open() -> io::Result<Self> {
                let library = unsafe { libloading::Library::new("libatspi.so.0") }.map_err(err)?;
                let api = Self {
                    $($field: *unsafe { library.get($symbol) }.map_err(err)?,)*
                    _library: library,
                };
                unsafe {
                    (api.timeout)(100, 150);
                    if (api.init)() > 1 {
                        return Err(io::Error::other("AT-SPI is unavailable"));
                    }
                }
                Ok(api)
            }
        }
    };
}

atspi! {
    init: unsafe extern "C" fn() -> i32 = b"atspi_init",
    timeout: unsafe extern "C" fn(i32, i32) = b"atspi_set_timeout",
    desktop: unsafe extern "C" fn(i32) -> Object = b"atspi_get_desktop",
    unref: unsafe extern "C" fn(Object) = b"g_object_unref",
    retain: unsafe extern "C" fn(Object) -> Object = b"g_object_ref",
    free: unsafe extern "C" fn(Object) = b"g_free",
    free_error: unsafe extern "C" fn(Object) = b"g_error_free",
    children: unsafe extern "C" fn(Object, ErrorSlot) -> i32 = b"atspi_accessible_get_child_count",
    child: unsafe extern "C" fn(Object, i32, ErrorSlot) -> Object = b"atspi_accessible_get_child_at_index",
    pid: unsafe extern "C" fn(Object, ErrorSlot) -> u32 = b"atspi_accessible_get_process_id",
    role: unsafe extern "C" fn(Object, ErrorSlot) -> i32 = b"atspi_accessible_get_role",
    toolkit: unsafe extern "C" fn(Object, ErrorSlot) -> *mut c_char = b"atspi_accessible_get_toolkit_name",
    states: unsafe extern "C" fn(Object) -> Object = b"atspi_accessible_get_state_set",
    has_state: unsafe extern "C" fn(Object, i32) -> i32 = b"atspi_state_set_contains",
    clear_cache: unsafe extern "C" fn(Object) = b"atspi_accessible_clear_cache",
    text: unsafe extern "C" fn(Object) -> Object = b"atspi_accessible_get_text_iface",
    editable: unsafe extern "C" fn(Object) -> Object = b"atspi_accessible_get_editable_text_iface",
    count: unsafe extern "C" fn(Object, ErrorSlot) -> i32 = b"atspi_text_get_character_count",
    read: unsafe extern "C" fn(Object, i32, i32, ErrorSlot) -> *mut c_char = b"atspi_text_get_text",
    caret: unsafe extern "C" fn(Object, ErrorSlot) -> i32 = b"atspi_text_get_caret_offset",
    selections: unsafe extern "C" fn(Object, ErrorSlot) -> i32 = b"atspi_text_get_n_selections",
    selection: unsafe extern "C" fn(Object, i32, ErrorSlot) -> *mut TextRange = b"atspi_text_get_selection",
    set_text: unsafe extern "C" fn(Object, *const c_char, ErrorSlot) -> i32 = b"atspi_editable_text_set_text_contents",
    set_caret: unsafe extern "C" fn(Object, i32, ErrorSlot) -> i32 = b"atspi_text_set_caret_offset",
    add_selection: unsafe extern "C" fn(Object, i32, i32, ErrorSlot) -> i32 = b"atspi_text_add_selection",
    set_selection: unsafe extern "C" fn(Object, i32, i32, i32, ErrorSlot) -> i32 = b"atspi_text_set_selection",
    insert: unsafe extern "C" fn(Object, i32, *const c_char, i32, ErrorSlot) -> i32 = b"atspi_editable_text_insert_text",
    delete: unsafe extern "C" fn(Object, i32, i32, ErrorSlot) -> i32 = b"atspi_editable_text_delete_text",
}

struct Ref<'a> {
    api: &'a Api,
    pointer: NonNull<c_void>,
}

impl<'a> Ref<'a> {
    fn new(api: &'a Api, object: Object) -> Option<Self> {
        NonNull::new(object).map(|pointer| Self { api, pointer })
    }

    fn ptr(&self) -> Object {
        self.pointer.as_ptr()
    }
}

impl Drop for Ref<'_> {
    fn drop(&mut self) {
        unsafe { (self.api.unref)(self.ptr()) };
    }
}

impl Api {
    fn call<T>(&self, f: impl FnOnce(ErrorSlot) -> T) -> io::Result<T> {
        let mut error = ptr::null_mut();
        let result = f(&mut error);
        if error.is_null() {
            Ok(result)
        } else {
            unsafe { (self.free_error)(error) };
            Err(io::Error::other("AT-SPI rejected the operation"))
        }
    }

    fn lookup(&self, pid: u32) -> io::Result<(Ref<'_>, bool)> {
        let desktop = Ref::new(self, unsafe { (self.desktop)(0) }).ok_or_else(unsupported)?;
        let mut stack = Vec::new();
        let mut gtk = false;
        for child in self.child_nodes(&desktop, 128)? {
            if self
                .call(|error| unsafe { (self.pid)(child.ptr(), error) })
                .ok()
                == Some(pid)
            {
                if let Ok(name) = self.call(|error| unsafe { (self.toolkit)(child.ptr(), error) })
                    && let Some(name) = NonNull::new(name)
                {
                    gtk = unsafe { CStr::from_ptr(name.as_ptr()) }
                        .to_bytes()
                        .eq_ignore_ascii_case(b"gtk");
                    unsafe { (self.free)(name.as_ptr().cast()) };
                }
                unsafe { (self.clear_cache)(child.ptr()) };
                stack.push(child);
                break;
            }
        }
        let mut found = None;
        let deadline = Instant::now() + Duration::from_millis(100);
        let mut inspected = 0;
        while let Some(node) = stack.pop() {
            inspected += 1;
            if inspected > 192 || Instant::now() >= deadline {
                return Err(unsupported());
            }
            let states =
                Ref::new(self, unsafe { (self.states)(node.ptr()) }).ok_or_else(unsupported)?;
            if [EDITABLE, FOCUSED, SENSITIVE]
                .into_iter()
                .all(|state| unsafe { (self.has_state)(states.ptr(), state) != 0 })
            {
                let role = self.call(|error| unsafe { (self.role)(node.ptr(), error) })?;
                let single = unsafe { (self.has_state)(states.ptr(), SINGLE_LINE) != 0 };
                let multi = gtk
                    && role == TEXT
                    && unsafe { (self.has_state)(states.ptr(), MULTI_LINE) != 0 };
                if multi || (single && (role == ENTRY || (gtk && role == TEXT))) {
                    if found.replace((node, multi)).is_some() {
                        return Err(unsupported());
                    }
                    continue;
                }
            }
            stack.extend(self.child_nodes(&node, 96)?);
        }
        found.ok_or_else(unsupported)
    }

    fn child_nodes<'a>(&'a self, parent: &Ref<'a>, limit: i32) -> io::Result<Vec<Ref<'a>>> {
        let count = self.call(|error| unsafe { (self.children)(parent.ptr(), error) })?;
        if !(0..=limit).contains(&count) {
            return Err(unsupported());
        }
        let mut children = Vec::with_capacity(count as usize);
        for index in 0..count {
            let pointer = self.call(|error| unsafe { (self.child)(parent.ptr(), index, error) })?;
            if let Some(child) = Ref::new(self, pointer) {
                children.push(child);
            }
        }
        Ok(children)
    }
}

pub(super) struct Field<'a> {
    desktop: &'a Desktop,
    element: Ref<'a>,
    text: Ref<'a>,
    editable: Ref<'a>,
    window: Window,
    pid: u32,
    recent: Option<&'a str>,
    keyboard: Option<KeyboardGrab<'a>>,
    multiline: bool,
}

struct KeyboardGrab<'a>(&'a Desktop);

impl<'a> KeyboardGrab<'a> {
    fn acquire(desktop: &'a Desktop, window: Window) -> io::Result<Self> {
        let grab = desktop
            .conn
            .grab_keyboard(false, window, CURRENT_TIME, GrabMode::ASYNC, GrabMode::SYNC)
            .map_err(err)?
            .reply()
            .map_err(err)?;
        if grab.status != GrabStatus::SUCCESS {
            return Err(io::ErrorKind::Interrupted.into());
        }
        let guard = Self(desktop);
        let pressed = desktop
            .conn
            .query_keymap()
            .map_err(err)?
            .reply()
            .map_err(err)?
            .keys;
        if pressed != [0; 32] {
            return Err(io::ErrorKind::Interrupted.into());
        }
        Ok(guard)
    }
}

impl Drop for KeyboardGrab<'_> {
    fn drop(&mut self) {
        if let Ok(release) = self.0.conn.ungrab_keyboard(CURRENT_TIME) {
            let _ = release.check();
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub(super) struct Identity {
    window: Window,
    pid: u32,
    object: usize,
}

pub(super) struct Retained {
    api: Rc<Api>,
    pointer: NonNull<c_void>,
}

impl Drop for Retained {
    fn drop(&mut self) {
        unsafe { (self.api.unref)(self.pointer.as_ptr()) };
    }
}

impl<'a> Field<'a> {
    pub fn open(
        api: &'a Api,
        desktop: &'a Desktop,
        window: Window,
        recent: Option<&'a str>,
    ) -> io::Result<Self> {
        let pid = desktop.pid(window)?.ok_or_else(unsupported)?;
        let (element, multiline) = api.lookup(pid)?;
        let text = Ref::new(api, unsafe { (api.text)(element.ptr()) }).ok_or_else(unsupported)?;
        let editable =
            Ref::new(api, unsafe { (api.editable)(element.ptr()) }).ok_or_else(unsupported)?;
        Ok(Self {
            desktop,
            element,
            text,
            editable,
            window,
            pid,
            recent,
            keyboard: None,
            multiline,
        })
    }

    pub fn retain(&self, api: Rc<Api>) -> Retained {
        debug_assert!(std::ptr::eq(self.element.api, api.as_ref()));
        unsafe { (api.retain)(self.element.ptr()) };
        Retained {
            api,
            pointer: self.element.pointer,
        }
    }

    fn target(&self) -> io::Result<Identity> {
        if self.desktop.focus()? != Some(self.window) {
            return Err(io::ErrorKind::Interrupted.into());
        }
        let api = self.element.api;
        // Force the provider to refresh its state even when a toolkit emits no focus events.
        unsafe { (api.clear_cache)(self.element.ptr()) };
        let state =
            Ref::new(api, unsafe { (api.states)(self.element.ptr()) }).ok_or_else(unsupported)?;
        let lines = if self.multiline {
            MULTI_LINE
        } else {
            SINGLE_LINE
        };
        if ![EDITABLE, FOCUSED, SENSITIVE, lines]
            .into_iter()
            .all(|state_bit| unsafe { (api.has_state)(state.ptr(), state_bit) != 0 })
        {
            return Err(io::ErrorKind::Interrupted.into());
        }
        Ok(Identity {
            window: self.window,
            pid: self.pid,
            object: self.element.ptr() as usize,
        })
    }

    fn set_caret(&self, offset: i32) -> io::Result<()> {
        let api = self.element.api;
        if api.call(|error| unsafe { (api.set_caret)(self.text.ptr(), offset, error) })? == 0 {
            return Err(io::Error::other("AT-SPI editor refused to move the caret"));
        }
        Ok(())
    }

    fn restore_selection(&self, document: &[u16], range: Range<usize>) -> io::Result<()> {
        let start = chars_at(document, range.start)?;
        let end = chars_at(document, range.end)?;
        if start == end {
            return self.set_caret(end);
        }
        let api = self.element.api;
        let has_selection =
            api.call(|error| unsafe { (api.selections)(self.text.ptr(), error) })? != 0;
        let applied = api.call(|error| unsafe {
            if has_selection {
                (api.set_selection)(self.text.ptr(), 0, start, end, error)
            } else {
                (api.add_selection)(self.text.ptr(), start, end, error)
            }
        })?;
        if applied == 0 {
            return Err(io::Error::other(
                "AT-SPI editor refused to restore selection",
            ));
        }
        Ok(())
    }

    fn replace_multiline(
        &mut self,
        expected: &Snapshot<Identity>,
        range: Range<usize>,
        replacement: &[u16],
    ) -> io::Result<()> {
        let api = self.element.api;
        let start = chars_at(&expected.document, range.start)?;
        let end = chars_at(&expected.document, range.end)?;
        let text = String::from_utf16(replacement).map_err(err)?;
        let caret = start + text.chars().count() as i32;
        let mut extended = expected.document.clone();
        let mut result = expected.document.clone();
        result.splice(range.clone(), replacement.iter().copied());
        if !replacement.is_empty() {
            let text = CString::new(text).map_err(err)?;
            let length = i32::try_from(text.as_bytes().len()).map_err(err)?;
            let inserted = api.call(|error| unsafe {
                (api.insert)(self.editable.ptr(), end, text.as_ptr(), length, error)
            });
            extended.splice(range.end..range.end, replacement.iter().copied());
            let after = self.snapshot()?;
            if after.target != expected.target || after.document != extended {
                return inserted.and(Err(io::Error::other(
                    "AT-SPI did not confirm text insertion",
                )));
            }
        }
        if !range.is_empty() {
            let deleted =
                api.call(|error| unsafe { (api.delete)(self.editable.ptr(), start, end, error) });
            let after = self.snapshot()?;
            if after.target != expected.target || after.document != result {
                if after.target == expected.target
                    && after.document == extended
                    && !replacement.is_empty()
                {
                    let inserted_end = chars_at(&extended, range.end + replacement.len())?;
                    api.call(|error| unsafe {
                        (api.delete)(self.editable.ptr(), end, inserted_end, error)
                    })?;
                    if self.snapshot()?.document == expected.document {
                        self.restore_selection(&expected.document, expected.selection.clone())?;
                    }
                }
                return deleted.and(Err(io::Error::other(
                    "AT-SPI did not confirm text replacement",
                )));
            }
        }
        self.set_caret(caret)
    }
}

impl Editor for Field<'_> {
    type Target = Identity;

    fn snapshot(&mut self) -> io::Result<Snapshot<Identity>> {
        let target = self.target()?;
        let api = self.element.api;
        let count = api.call(|error| unsafe { (api.count)(self.text.ptr(), error) })?;
        if !(0..=LIMIT as i32).contains(&count) {
            return Err(unsupported());
        }
        let raw = api.call(|error| unsafe { (api.read)(self.text.ptr(), 0, count, error) })?;
        let raw = NonNull::new(raw).ok_or_else(unsupported)?;
        let text = unsafe { CStr::from_ptr(raw.as_ptr()) }
            .to_str()
            .map(str::to_owned);
        unsafe { (api.free)(raw.as_ptr().cast()) };
        let text = text.map_err(err)?;
        let range = match api.call(|error| unsafe { (api.selections)(self.text.ptr(), error) })? {
            0 => {
                let caret = api.call(|error| unsafe { (api.caret)(self.text.ptr(), error) })?;
                caret..caret
            }
            1 => {
                let raw =
                    api.call(|error| unsafe { (api.selection)(self.text.ptr(), 0, error) })?;
                let range = unsafe { raw.as_ref() }.ok_or_else(unsupported)?;
                let selection = range.start..range.end;
                unsafe { (api.free)(raw.cast()) };
                selection
            }
            _ => return Err(unsupported()),
        };
        if range.start < 0
            || range.end > count
            || range.start > range.end
            || text.chars().count() != count as usize
        {
            return Err(unsupported());
        }
        let to_utf16 = |index| {
            text.chars()
                .take(index as usize)
                .map(char::len_utf16)
                .sum::<usize>()
        };
        Ok(Snapshot {
            target,
            selection: to_utf16(range.start)..to_utf16(range.end),
            document: text.encode_utf16().collect(),
        })
    }

    fn recent_range(&self, expected: &Snapshot<Self::Target>) -> Option<Range<usize>> {
        crate::typing::verified_suffix(&expected.document, expected.selection.end, self.recent?)
    }

    fn replace(
        &mut self,
        expected: &Snapshot<Identity>,
        range: Range<usize>,
        replacement: &[u16],
    ) -> io::Result<()> {
        self.keyboard = Some(KeyboardGrab::acquire(self.desktop, self.window)?);
        if self.snapshot()? != *expected {
            return Err(io::ErrorKind::Interrupted.into());
        }
        if self.multiline {
            return self.replace_multiline(expected, range, replacement);
        }
        let api = self.element.api;
        let end = range.start + replacement.len();
        let mut next = expected.document.clone();
        next.splice(range, replacement.iter().copied());
        let caret = String::from_utf16(&next[..end])
            .map_err(err)?
            .chars()
            .count() as i32;
        let text = String::from_utf16(&next).map_err(err)?;
        if text.contains(['\r', '\n']) {
            return Err(unsupported());
        }
        let text = CString::new(text).map_err(err)?;
        if self.target()? != expected.target {
            return Err(io::ErrorKind::Interrupted.into());
        }
        if api.call(|error| unsafe { (api.set_text)(self.editable.ptr(), text.as_ptr(), error) })?
            == 0
        {
            return Err(io::Error::other("AT-SPI editor refused replacement"));
        }
        if self.target()? != expected.target {
            return Err(io::ErrorKind::Interrupted.into());
        }
        self.set_caret(caret)
    }

    fn move_caret(&mut self, expected: &Snapshot<Identity>, caret: usize) -> io::Result<()> {
        if self.snapshot()? != *expected {
            return Err(io::ErrorKind::Interrupted.into());
        }
        let prefix = String::from_utf16(expected.document.get(..caret).ok_or_else(unsupported)?)
            .map_err(err)?;
        self.set_caret(prefix.chars().count() as i32)
    }
}

fn chars_at(document: &[u16], offset: usize) -> io::Result<i32> {
    let prefix =
        String::from_utf16(document.get(..offset).ok_or_else(unsupported)?).map_err(err)?;
    Ok(prefix.chars().count() as i32)
}

pub(super) fn unsupported() -> io::Error {
    io::Error::new(
        io::ErrorKind::Unsupported,
        "Focused field does not expose safe AT-SPI editing",
    )
}
