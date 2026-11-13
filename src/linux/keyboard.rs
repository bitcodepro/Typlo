use crate::layout::{self, Language};
use std::{
    ffi::{CStr, CString, c_char},
    io, ptr,
};
use x11rb::{
    connection::Connection,
    protocol::{
        xkb::{self, ConnectionExt as _},
        xproto::{AtomEnum, ConnectionExt as _, ModMask, Window},
    },
    xcb_ffi::XCBConnection,
};
use xkbcommon_dl::{
    x11::xkbcommon_x11_option, xkb_context_flags, xkb_keymap, xkb_keymap_compile_flags,
    xkb_rule_names, xkbcommon_option,
};

const PHYSICAL: [&CStr; 48] = [
    c"TLDE", c"AE01", c"AE02", c"AE03", c"AE04", c"AE05", c"AE06", c"AE07", c"AE08", c"AE09",
    c"AE10", c"AE11", c"AE12", c"AD01", c"AD02", c"AD03", c"AD04", c"AD05", c"AD06", c"AD07",
    c"AD08", c"AD09", c"AD10", c"AD11", c"AD12", c"AC01", c"AC02", c"AC03", c"AC04", c"AC05",
    c"AC06", c"AC07", c"AC08", c"AC09", c"AC10", c"AC11", c"AB01", c"AB02", c"AB03", c"AB04",
    c"AB05", c"AB06", c"AB07", c"AB08", c"AB09", c"AB10", c"BKSL", c"LSGT",
];

pub(super) struct Keyboard {
    pub groups: Vec<Language>,
    pub catalog: Vec<Language>,
    shell_sources: Vec<(String, Language)>,
    gnome: bool,
    physical: [u8; 48],
    symbols: Vec<(u8, u32)>,
    text: Vec<[[char; 2]; 256]>,
}

struct Keymap(*mut xkb_keymap);

impl Drop for Keymap {
    fn drop(&mut self) {
        unsafe { (xkbcommon_dl::xkbcommon_handle().xkb_keymap_unref)(self.0) };
    }
}

impl Keyboard {
    pub fn load(conn: &XCBConnection, root: Window) -> io::Result<Self> {
        let api = xkbcommon_option().ok_or_else(|| io::Error::other("libxkbcommon is required"))?;
        let x11 = xkbcommon_x11_option()
            .ok_or_else(|| io::Error::other("libxkbcommon-x11 is required"))?;
        if !conn
            .xkb_use_extension(1, 0)
            .map_err(super::err)?
            .reply()
            .map_err(super::err)?
            .supported
        {
            return Err(io::Error::other("XKB is unavailable"));
        }
        let raw = conn.get_raw_xcb_connection();
        let device = unsafe { (x11.xkb_x11_get_core_keyboard_device_id)(raw) };
        if device < 0 {
            return Err(io::Error::other("No XKB core keyboard"));
        }
        let context = unsafe { (api.xkb_context_new)(xkb_context_flags::XKB_CONTEXT_NO_FLAGS) };
        if context.is_null() {
            return Err(io::Error::other("Cannot initialize XKB"));
        }
        let map = unsafe {
            let map = (x11.xkb_x11_keymap_new_from_device)(
                context,
                raw,
                device,
                xkb_keymap_compile_flags::XKB_KEYMAP_COMPILE_NO_FLAGS,
            );
            (api.xkb_context_unref)(context);
            map
        };
        if map.is_null() {
            return Err(io::Error::other("Cannot read the XKB keymap"));
        }
        let keymap = Keymap(map);
        let library =
            unsafe { libloading::Library::new("libxkbcommon.so.0") }.map_err(super::err)?;
        // xkbcommon-dl does not expose these two getters.
        let by_name: libloading::Symbol<
            unsafe extern "C" fn(*mut xkb_keymap, *const c_char) -> u32,
        > = unsafe { library.get(b"xkb_keymap_key_by_name") }.map_err(super::err)?;
        let name_at: libloading::Symbol<
            unsafe extern "C" fn(*mut xkb_keymap, u32) -> *const c_char,
        > = unsafe { library.get(b"xkb_keymap_layout_get_name") }.map_err(super::err)?;
        let physical = PHYSICAL.map(|name| {
            u8::try_from(unsafe { by_name(keymap.0, name.as_ptr()) }).unwrap_or_default()
        });
        let rules = rules(conn, root).unwrap_or_default();
        let mut fields = rules.split(|&byte| byte == 0).skip(2);
        let mut codes = fields
            .next()
            .unwrap_or_default()
            .split(|&byte| byte == b',');
        let mut variants = fields
            .next()
            .unwrap_or_default()
            .split(|&byte| byte == b',');
        let symbol_at = |key: u8, group: u32, level: u32| {
            let mut syms = ptr::null();
            let count = unsafe {
                (api.xkb_keymap_key_get_syms_by_level)(
                    keymap.0,
                    u32::from(key),
                    group,
                    level,
                    &mut syms,
                )
            };
            (count == 1 && !syms.is_null()).then(|| unsafe { *syms })
        };
        let mut groups = Vec::new();
        let mut text = Vec::new();
        let count = unsafe { (api.xkb_keymap_num_layouts)(keymap.0) }.min(4);
        for group in 0..count {
            let name = unsafe { name_at(keymap.0, group) };
            let name = if name.is_null() {
                format!("XKB group {}", group + 1)
            } else {
                unsafe { CStr::from_ptr(name) }
                    .to_string_lossy()
                    .into_owned()
            };
            let code = codes.next().unwrap_or_default();
            let variant = variants.next().unwrap_or_default();
            let keys = [0, 1].map(|level| {
                physical
                    .iter()
                    .map(|&key| {
                        symbol_at(key, group, level)
                            .and_then(|symbol| {
                                char::from_u32(unsafe { (api.xkb_keysym_to_utf32)(symbol) })
                            })
                            .unwrap_or('\0')
                    })
                    .collect::<String>()
            });
            let mut language = Language::xkb(code, variant, &name);
            let mut settings_id = std::str::from_utf8(code)
                .ok()
                .filter(|code| !code.is_empty())
                .zip(std::str::from_utf8(variant).ok())
                .map(|(code, variant)| format!("xkb:{code}:{variant}"));
            if groups.contains(&language) {
                language = Language::xkb(&[], variant, &format!("{name} {group}"));
                settings_id = None;
            }
            let Language::Xkb { id, .. } = language else {
                unreachable!()
            };
            layout::register_xkb_layout(id, settings_id, name, &keys[0], &keys[1]);
            groups.push(language);
            let mut row = [['\0'; 2]; 256];
            for key in conn.setup().min_keycode..=conn.setup().max_keycode {
                for level in 0..2 {
                    row[key as usize][level] = symbol_at(key, group, level as u32)
                        .and_then(|symbol| {
                            char::from_u32(unsafe { (api.xkb_keysym_to_utf32)(symbol) })
                        })
                        .filter(|character| !character.is_control())
                        .unwrap_or_default();
                }
            }
            text.push(row);
        }
        if groups.is_empty() {
            return Err(io::Error::other("XKB reported no layouts"));
        }
        let symbols = (conn.setup().min_keycode..=conn.setup().max_keycode)
            .filter_map(|key| symbol_at(key, 0, 0).map(|symbol| (key, symbol)))
            .collect();
        let gnome = super::gnome::session();
        let shell_sources = if gnome { configured()? } else { Vec::new() };
        let catalog = if shell_sources.is_empty() {
            groups.clone()
        } else {
            shell_sources.iter().map(|(_, layout)| *layout).collect()
        };
        Ok(Self {
            groups,
            catalog,
            shell_sources,
            gnome,
            physical,
            symbols,
            text,
        })
    }

    pub fn key(&self, virtual_key: u32) -> Option<u8> {
        let physical = match virtual_key {
            0x41..=0x5a => Some(virtual_key as u8 + 32),
            0x30..=0x39 => Some(virtual_key as u8),
            0xdb => Some(b'['),
            0xdd => Some(b']'),
            0xbc => Some(b','),
            0xbe => Some(b'.'),
            _ => None,
        };
        if let Some(physical) = physical {
            let us = [96u8].into_iter().chain(
                b"1234567890-=qwertyuiop[]asdfghjkl;'zxcvbnm,./\\"
                    .iter()
                    .copied(),
            );
            let index = us
                .enumerate()
                .find_map(|(index, key)| (key == physical).then_some(index))?;
            return (self.physical[index] != 0).then_some(self.physical[index]);
        }
        self.symbol(match virtual_key {
            0x20 => 0x20,
            0x09 => 0xff09,
            0x0d => 0xff0d,
            0x1b => 0xff1b,
            0x25..=0x28 => [0xff51, 0xff52, 0xff53, 0xff54][(virtual_key - 0x25) as usize],
            0x24 => 0xff50,
            0x23 => 0xff57,
            0x2d => 0xff63,
            0x2e => 0xffff,
            0x70..=0x87 => 0xffbe + virtual_key - 0x70,
            _ => return None,
        })
    }

    pub fn symbol(&self, symbol: u32) -> Option<u8> {
        self.symbols
            .iter()
            .find_map(|&(key, value)| (value == symbol).then_some(key))
    }

    pub fn current(&self, conn: &XCBConnection) -> io::Result<Option<Language>> {
        if self.gnome
            && let Some(current) = super::gnome::current()
            && let Some((_, layout)) = self.shell_sources.iter().find(|(id, _)| *id == current)
        {
            return Ok(Some(*layout));
        }
        let (group, _) = Self::state(conn)?;
        Ok(self.groups.get(group as usize).copied())
    }

    pub fn state(conn: &XCBConnection) -> io::Result<(u8, u16)> {
        let state = conn
            .xkb_get_state(xkb::ID::USE_CORE_KBD.into())
            .map_err(super::err)?
            .reply()
            .map_err(super::err)?;
        Ok((state.group.into(), state.mods.into()))
    }

    pub fn character(&self, group: u8, key: u8, shift: bool, caps: bool) -> Option<char> {
        let row = self.text.get(group as usize)?[key as usize];
        let level = usize::from(shift ^ (caps && row[0].is_alphabetic()));
        (row[level] != '\0').then_some(row[level])
    }

    pub fn select(&self, conn: &XCBConnection, language: Language) -> io::Result<bool> {
        if self.gnome {
            let Some((id, _)) = self
                .shell_sources
                .iter()
                .find(|(_, layout)| *layout == language)
            else {
                return Ok(false);
            };
            return Ok(super::gnome::activate(id));
        }
        let Some(group) = self.groups.iter().position(|&layout| layout == language) else {
            return Ok(false);
        };
        conn.xkb_latch_lock_state(
            xkb::ID::USE_CORE_KBD.into(),
            ModMask::default(),
            ModMask::default(),
            true,
            (group as u8).into(),
            ModMask::default(),
            false,
            0,
        )
        .map_err(super::err)?
        .check()
        .map_err(super::err)?;
        Ok(self.current(conn)? == Some(language))
    }
}

pub(super) fn configured() -> io::Result<Vec<(String, Language)>> {
    let api = xkbcommon_option().ok_or_else(|| io::Error::other("libxkbcommon is required"))?;
    let library = unsafe { libloading::Library::new("libxkbcommon.so.0") }.map_err(super::err)?;
    let by_name: libloading::Symbol<unsafe extern "C" fn(*mut xkb_keymap, *const c_char) -> u32> =
        unsafe { library.get(b"xkb_keymap_key_by_name") }.map_err(super::err)?;
    let name_at: libloading::Symbol<unsafe extern "C" fn(*mut xkb_keymap, u32) -> *const c_char> =
        unsafe { library.get(b"xkb_keymap_layout_get_name") }.map_err(super::err)?;
    let context = unsafe { (api.xkb_context_new)(xkb_context_flags::XKB_CONTEXT_NO_FLAGS) };
    if context.is_null() {
        return Err(io::Error::other("Cannot initialize XKB"));
    }
    let mut result = Vec::new();
    for (code, variant) in super::gnome::configured() {
        let layout = CString::new(code.as_str()).expect("GNOME layout contains validated ASCII");
        let variant_name =
            CString::new(variant.as_str()).expect("GNOME variant contains validated ASCII");
        let names = xkb_rule_names {
            rules: ptr::null(),
            model: ptr::null(),
            layout: layout.as_ptr(),
            variant: variant_name.as_ptr(),
            options: ptr::null(),
        };
        let map = unsafe {
            (api.xkb_keymap_new_from_names)(
                context,
                &names,
                xkb_keymap_compile_flags::XKB_KEYMAP_COMPILE_NO_FLAGS,
            )
        };
        if map.is_null() {
            continue;
        }
        let map = Keymap(map);
        let name = unsafe { name_at(map.0, 0) };
        let name = if name.is_null() {
            code.clone()
        } else {
            unsafe { CStr::from_ptr(name) }
                .to_string_lossy()
                .into_owned()
        };
        let keys = [0, 1].map(|level| {
            PHYSICAL
                .iter()
                .map(|key| {
                    let key = unsafe { by_name(map.0, key.as_ptr()) };
                    let mut symbol = ptr::null();
                    let count = unsafe {
                        (api.xkb_keymap_key_get_syms_by_level)(map.0, key, 0, level, &mut symbol)
                    };
                    if count != 1 || symbol.is_null() {
                        return '\0';
                    }
                    char::from_u32(unsafe { (api.xkb_keysym_to_utf32)(*symbol) })
                        .unwrap_or_default()
                })
                .collect::<String>()
        });
        let language = Language::xkb(code.as_bytes(), variant.as_bytes(), &name);
        let Language::Xkb { id, .. } = language else {
            unreachable!()
        };
        layout::register_xkb_layout(
            id,
            Some(format!("xkb:{code}:{variant}")),
            name,
            &keys[0],
            &keys[1],
        );
        let source = if variant.is_empty() {
            code
        } else {
            format!("{code}+{variant}")
        };
        result.push((source, language));
    }
    unsafe { (api.xkb_context_unref)(context) };
    Ok(result)
}

fn rules(conn: &XCBConnection, root: Window) -> io::Result<Vec<u8>> {
    let atom = conn
        .intern_atom(true, b"_XKB_RULES_NAMES")
        .map_err(super::err)?
        .reply()
        .map_err(super::err)?
        .atom;
    if atom == 0 {
        return Ok(Vec::new());
    }
    Ok(conn
        .get_property(false, root, atom, AtomEnum::STRING, 0, 256)
        .map_err(super::err)?
        .reply()
        .map_err(super::err)?
        .value)
}
