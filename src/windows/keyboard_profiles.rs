use std::{
    ptr::{null, null_mut},
    sync::{LazyLock, Mutex},
    time::{Duration, Instant},
};
use windows_sys::{
    Win32::{
        Foundation::{ERROR_SUCCESS, FreeLibrary},
        System::{
            LibraryLoader::{GetProcAddress, LOAD_LIBRARY_SEARCH_SYSTEM32, LoadLibraryExW},
            Registry::{HKEY_LOCAL_MACHINE, RRF_RT_REG_SZ, RegGetValueW},
        },
        UI::Input::KeyboardAndMouse::HKL,
    },
    core::GUID,
};

const KEYBOARD_LAYOUT: u32 = 2;
const INPUT_PROCESSOR: u32 = 1;

#[repr(C)]
#[derive(Clone)]
struct RawProfile {
    kind: u32,
    language: u16,
    clsid: GUID,
    profile: GUID,
    category: GUID,
    substitute: u32,
    flags: u32,
    id: [u16; 260],
}

type Enumerate =
    unsafe extern "system" fn(*const u16, *const u16, *const u16, *mut RawProfile, u32) -> u32;

static ENUMERATE: LazyLock<Option<Enumerate>> = LazyLock::new(|| unsafe {
    let dll = LoadLibraryExW(
        super::wide("input.dll").as_ptr(),
        null_mut(),
        LOAD_LIBRARY_SEARCH_SYSTEM32,
    );
    if dll.is_null() {
        return None;
    }
    let Some(procedure) = GetProcAddress(dll, c"EnumEnabledLayoutOrTip".as_ptr().cast()) else {
        FreeLibrary(dll);
        return None;
    };
    // Keep the system DLL loaded for the lifetime of the cached procedure.
    Some(std::mem::transmute::<
        unsafe extern "system" fn() -> isize,
        Enumerate,
    >(procedure))
});

#[derive(Clone, Copy)]
enum Profile {
    Keyboard { language: u16, device: u16 },
    InputProcessor { language: u16, substitute: u32 },
    UnmappedKeyboard(u16),
}

struct Cache {
    checked: Instant,
    profiles: Option<Vec<Profile>>,
}

static ENABLED: Mutex<Option<Cache>> = Mutex::new(None);

pub(super) fn retain_enabled(layouts: &mut Vec<HKL>) {
    let Ok(mut cache) = ENABLED.lock() else {
        return;
    };
    let cached = cache.get_or_insert_with(query);
    if cached.checked.elapsed() >= Duration::from_secs(1) {
        *cached = query();
    }
    if let Some(profiles) = &cached.profiles {
        let loaded: Vec<_> = layouts
            .iter()
            .map(|&handle| handle as usize as u32)
            .collect();
        let enabled = select_enabled(&loaded, profiles);
        if !enabled.is_empty() {
            layouts.retain(|&handle| enabled.contains(&(handle as usize as u32)));
        }
    }
}

fn query() -> Cache {
    Cache {
        checked: Instant::now(),
        profiles: read_profiles(),
    }
}

fn read_profiles() -> Option<Vec<Profile>> {
    let enumerate = (*ENUMERATE)?;
    let count = unsafe { enumerate(null(), null(), null(), null_mut(), 0) };
    if !(1..=512).contains(&count) {
        return None;
    }
    // The native buffer contains integers, GUIDs and UTF-16 code units.
    let mut raw = vec![unsafe { std::mem::zeroed::<RawProfile>() }; count as usize];
    let copied = unsafe { enumerate(null(), null(), null(), raw.as_mut_ptr(), count) };
    if !(1..=count).contains(&copied) {
        return None;
    }
    let profiles: Vec<_> = raw[..copied as usize]
        .iter()
        .filter_map(|item| {
            let language = item.language;
            match item.kind {
                KEYBOARD_LAYOUT => {
                    let id = String::from_utf16_lossy(&item.id[..nul(&item.id)]);
                    let layout = parse_keyboard_id(&id, language);
                    let device = layout.and_then(device_id);
                    Some(match device {
                        Some(device) => Profile::Keyboard { language, device },
                        None => Profile::UnmappedKeyboard(language),
                    })
                }
                INPUT_PROCESSOR => Some(Profile::InputProcessor {
                    language,
                    substitute: item.substitute,
                }),
                _ => None,
            }
        })
        .collect();
    (!profiles.is_empty()).then_some(profiles)
}

fn nul(text: &[u16]) -> usize {
    text.iter()
        .position(|&unit| unit == 0)
        .unwrap_or(text.len())
}

fn parse_keyboard_id(input: &str, language: u16) -> Option<u32> {
    let (lang, klid) = input.split_once(':')?;
    let lang = u16::from_str_radix(lang.trim_start_matches("0x"), 16).ok()?;
    (lang == language).then(|| u32::from_str_radix(klid.trim_start_matches("0x"), 16).ok())?
}

fn device_id(klid: u32) -> Option<u16> {
    if let Ok(device) = u16::try_from(klid) {
        return Some(device);
    }
    let key = super::wide(&format!(
        r"SYSTEM\CurrentControlSet\Control\Keyboard Layouts\{klid:08x}"
    ));
    let mut value = [0u16; 16];
    let mut size = size_of_val(&value) as u32;
    let result = unsafe {
        RegGetValueW(
            HKEY_LOCAL_MACHINE,
            key.as_ptr(),
            super::wide("Layout Id").as_ptr(),
            RRF_RT_REG_SZ,
            null_mut(),
            value.as_mut_ptr().cast(),
            &mut size,
        )
    };
    if result != ERROR_SUCCESS {
        return None;
    }
    let id = u16::from_str_radix(&String::from_utf16_lossy(&value[..nul(&value)]), 16).ok()?;
    (id <= 0x0fff).then_some(0xf000 | id)
}

fn select_enabled(loaded: &[u32], profiles: &[Profile]) -> Vec<u32> {
    let mut enabled = Vec::new();
    for &id in loaded {
        let language = id as u16;
        let device = (id >> 16) as u16;
        let found = profiles.iter().any(|profile| match *profile {
            Profile::Keyboard {
                language: lang,
                device: dev,
            } => language == lang && device == dev,
            Profile::UnmappedKeyboard(lang) => language == lang,
            Profile::InputProcessor {
                language: lang,
                substitute,
            } => {
                let matches_ime =
                    |handle: u32| handle == substitute || (handle >> 16) & 0xf000 == 0xe000;
                language == lang
                    && (matches_ime(id)
                        || !loaded
                            .iter()
                            .any(|&other| other as u16 == lang && matches_ime(other)))
            }
        });
        if found && !enabled.contains(&id) {
            enabled.push(id);
        }
    }
    enabled
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn excludes_loaded_but_unconfigured_english_profiles() {
        let loaded = [0x04090409, 0x04190419, 0x08090809, 0x04090809];
        let profiles = [
            Profile::Keyboard {
                language: 0x0409,
                device: 0x0409,
            },
            Profile::Keyboard {
                language: 0x0419,
                device: 0x0419,
            },
        ];
        assert_eq!(select_enabled(&loaded, &profiles), loaded[..2]);
    }

    #[test]
    fn keeps_distinct_enabled_keyboard_variants_and_ime() {
        let loaded = [0x04090409, 0xf0020409, 0xf0010409, 0x04110411, 0xe0010411];
        let profiles = [
            Profile::Keyboard {
                language: 0x0409,
                device: 0x0409,
            },
            Profile::Keyboard {
                language: 0x0409,
                device: 0xf002,
            },
            Profile::InputProcessor {
                language: 0x0411,
                substitute: 0,
            },
        ];
        assert_eq!(
            select_enabled(&loaded, &profiles),
            [loaded[0], loaded[1], loaded[4]]
        );
        assert_eq!(parse_keyboard_id("0409:00010409", 0x0409), Some(0x00010409));
        assert_eq!(parse_keyboard_id("0809:00000809", 0x0409), None);
    }

    #[test]
    fn saved_unconfigured_layouts_do_not_participate_in_the_runtime_cycle() {
        use crate::{
            config::{ApplicationRule, Config, LayoutShortcut},
            layout::Language::Windows,
        };
        let english = Windows(0x04090409);
        let russian = Windows(0x04190419);
        let extra = Windows(0x08090809);
        let mut settings = Config {
            cycle: vec![russian, extra, english],
            applications: vec![ApplicationRule {
                process: "editor".into(),
                cycle: vec![extra],
                ..Default::default()
            }],
            layout_hotkeys: vec![
                LayoutShortcut {
                    layout: english,
                    hotkey: "Ctrl+Alt+1".into(),
                },
                LayoutShortcut {
                    layout: extra,
                    hotkey: "Ctrl+Alt+2".into(),
                },
            ],
            ..Default::default()
        };
        super::super::restrict_layouts(&mut settings, &[english, russian]);
        assert_eq!(settings.cycle, [russian, english]);
        assert_eq!(settings.cycle_for("editor.exe"), [russian, english]);
        assert_eq!(settings.layout_hotkeys.len(), 1);
        assert_eq!(settings.layout_hotkeys[0].layout, english);
    }
}
