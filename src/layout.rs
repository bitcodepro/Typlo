use serde::{Deserialize, Serialize};
use std::{
    collections::HashMap,
    hash::{Hash, Hasher},
    sync::{Arc, LazyLock, RwLock},
};

#[derive(Clone, Copy, Debug, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Language {
    En,
    Ru,
    Uk,
    Windows(u32),
    Xkb { id: u64, locale: u16 },
    Mac { id: u64, locale: u16 },
}

impl PartialEq for Language {
    fn eq(&self, other: &Self) -> bool {
        self.identity() == other.identity()
    }
}

impl Eq for Language {}

impl Hash for Language {
    fn hash<H: Hasher>(&self, state: &mut H) {
        self.identity().hash(state);
    }
}

impl Language {
    pub const ALL: [Self; 3] = [Self::En, Self::Ru, Self::Uk];

    fn identity(self) -> (u8, u64) {
        match self {
            Self::En => (0, 0),
            Self::Ru => (0, 1),
            Self::Uk => (0, 2),
            Self::Windows(id) => (1, id.into()),
            Self::Xkb { id, .. } => (2, id),
            Self::Mac { id, .. } => (3, id),
        }
    }

    pub(crate) fn xkb(source: &[u8], variant: &[u8], name: &str) -> Self {
        let locale = match source {
            b"us" | b"gb" | b"au" | b"nz" => 0x409,
            b"ru" => 0x419,
            b"ua" => 0x422,
            b"fr" => 0x40c,
            b"es" | b"latam" => 0x40a,
            b"it" => 0x410,
            b"de" => 0x407,
            b"br" | b"pt" => 0x416,
            _ => match name.split([' ', '(']).next().unwrap_or_default() {
                "English" => 0x409,
                "Russian" => 0x419,
                "Ukrainian" => 0x422,
                "French" => 0x40c,
                "Spanish" => 0x40a,
                "Italian" => 0x410,
                "German" => 0x407,
                "Portuguese" => 0x416,
                _ => 0,
            },
        };
        let identity = if source.is_empty() {
            name.as_bytes()
        } else {
            source
        };
        let id = native_id(
            identity
                .iter()
                .copied()
                .chain([0xff])
                .chain(variant.iter().copied()),
        );
        Self::Xkb { id, locale }
    }

    pub(crate) fn mac(source: &str, locale: u16) -> Self {
        Self::Mac {
            id: native_id(source.bytes()),
            locale,
        }
    }

    pub fn name(self) -> String {
        match self {
            Self::En => "English".into(),
            Self::Ru => "Русский".into(),
            Self::Uk => "Українська".into(),
            Self::Windows(id) => NATIVE_MAPS
                .read()
                .ok()
                .and_then(|maps| maps.get(&id).map(|layout| layout.name.clone()))
                .unwrap_or_else(|| format!("Windows {id:08x}")),
            Self::Xkb { id, .. } => XKB_MAPS
                .read()
                .ok()
                .and_then(|maps| maps.get(&id).map(|layout| layout.name.clone()))
                .unwrap_or_else(|| format!("XKB {id:016x}")),
            Self::Mac { id, .. } => MAC_MAPS
                .read()
                .ok()
                .and_then(|maps| maps.get(&id).map(|layout| layout.name.clone()))
                .unwrap_or_else(|| format!("macOS {id:016x}")),
        }
    }

    pub fn lang_id(self) -> u16 {
        match self {
            Self::En => 0x0409,
            Self::Ru => 0x0419,
            Self::Uk => 0x0422,
            Self::Windows(id) => id as u16,
            Self::Xkb { locale, .. } | Self::Mac { locale, .. } => locale,
        }
    }

    pub(crate) fn settings_id(self) -> Option<String> {
        match self {
            Self::En => Some("en".into()),
            Self::Ru => Some("ru".into()),
            Self::Uk => Some("uk".into()),
            Self::Windows(id) => Some(format!("win:{id:08x}")),
            Self::Xkb { id, .. } => XKB_MAPS.read().ok()?.get(&id)?.settings_id.clone(),
            Self::Mac { id, .. } => MAC_MAPS.read().ok()?.get(&id)?.settings_id.clone(),
        }
    }

    pub fn dictionary_index(self) -> Option<usize> {
        match self.lang_id() & 0x3ff {
            9 => Some(0),
            25 => Some(1),
            34 => Some(2),
            12 => Some(3),
            10 => Some(4),
            16 => Some(5),
            7 => Some(6),
            22 => Some(7),
            _ => None,
        }
    }

    pub fn convertible(self) -> bool {
        self.map().is_some()
    }

    fn map(self) -> Option<Arc<KeyMap>> {
        match self {
            Self::En => Some(Arc::clone(&KEY_MAPS[0])),
            Self::Ru => Some(Arc::clone(&KEY_MAPS[1])),
            Self::Uk => Some(Arc::clone(&KEY_MAPS[2])),
            Self::Windows(id) => NATIVE_MAPS.read().ok()?.get(&id)?.keys.clone(),
            Self::Xkb { id, .. } => XKB_MAPS.read().ok()?.get(&id)?.keys.clone(),
            Self::Mac { id, .. } => MAC_MAPS.read().ok()?.get(&id)?.keys.clone(),
        }
    }
}

fn native_id(bytes: impl IntoIterator<Item = u8>) -> u64 {
    bytes.into_iter().fold(0xcbf29ce484222325, |hash, byte| {
        (hash ^ u64::from(byte)).wrapping_mul(0x100000001b3)
    })
}

struct NativeMap {
    name: String,
    keys: Option<Arc<KeyMap>>,
    settings_id: Option<String>,
}

static NATIVE_MAPS: LazyLock<RwLock<HashMap<u32, NativeMap>>> = LazyLock::new(RwLock::default);

static XKB_MAPS: LazyLock<RwLock<HashMap<u64, NativeMap>>> = LazyLock::new(RwLock::default);
static MAC_MAPS: LazyLock<RwLock<HashMap<u64, NativeMap>>> = LazyLock::new(RwLock::default);

#[cfg(target_os = "macos")]
pub(crate) fn register_mac_layout(
    id: u64,
    source: &str,
    name: String,
    mapping: Option<(&str, &str)>,
) {
    if let Ok(mut maps) = MAC_MAPS.write() {
        maps.insert(
            id,
            NativeMap {
                name,
                keys: mapping.map(|(normal, shifted)| Arc::new(KeyMap::new(normal, shifted))),
                settings_id: Some(format!("mac:{source}")),
            },
        );
    }
}

#[cfg(target_os = "linux")]
pub(crate) fn register_xkb_layout(
    id: u64,
    settings_id: Option<String>,
    name: String,
    normal: &str,
    shifted: &str,
) {
    if let Ok(mut maps) = XKB_MAPS.write() {
        maps.insert(
            id,
            NativeMap {
                name,
                keys: Some(Arc::new(KeyMap::new(normal, shifted))),
                settings_id,
            },
        );
    }
}

#[cfg(windows)]
pub(crate) fn registered_windows_layout(id: u32) -> bool {
    NATIVE_MAPS.read().is_ok_and(|maps| maps.contains_key(&id))
}

#[cfg(windows)]
pub(crate) fn register_windows_layout(id: u32, name: String, mapping: Option<(&str, &str)>) {
    if let Ok(mut maps) = NATIVE_MAPS.write() {
        maps.entry(id).or_insert_with(|| NativeMap {
            name,
            keys: mapping.map(|(normal, shifted)| Arc::new(KeyMap::new(normal, shifted))),
            settings_id: None,
        });
    }
}

const INDEX_LEN: usize = 0x500;
const MISSING: u8 = u8::MAX;

struct KeyMap {
    keys: Vec<char>,
    index: [u8; INDEX_LEN],
    extras: HashMap<char, u8>,
}

impl KeyMap {
    fn new(normal: &str, shifted: &str) -> Self {
        let count = normal.chars().count();
        assert_eq!(count, shifted.chars().count());
        let keys: Vec<_> = normal.chars().chain(shifted.chars()).collect();
        let mut index = [MISSING; INDEX_LEN];
        let mut extras = HashMap::new();
        for (position, key) in keys.iter().copied().enumerate().rev() {
            if key == '\0' {
                continue;
            }
            if let Some(slot) = index.get_mut(key as usize) {
                *slot = position as u8;
            } else {
                extras.insert(key, position as u8);
            }
        }
        Self {
            keys,
            index,
            extras,
        }
    }

    fn code(&self, key: char) -> u8 {
        self.index
            .get(key as usize)
            .or_else(|| self.extras.get(&key))
            .copied()
            .unwrap_or(MISSING)
    }
}

static KEY_MAPS: LazyLock<[Arc<KeyMap>; 3]> = LazyLock::new(|| {
    [
        KeyMap::new(
            concat!(r#"`1234567890-=qwertyuiop[]asdfghjkl;'zxcvbnm,./\"#, "\0"),
            concat!(r#"~!@#$%^&*()_+QWERTYUIOP{}ASDFGHJKL:"ZXCVBNM<>?|"#, "\0"),
        ),
        KeyMap::new(
            concat!(r#"ё1234567890-=йцукенгшщзхъфывапролджэячсмитьбю.\"#, "\0"),
            concat!(r#"Ё!"№;%:?*()_+ЙЦУКЕНГШЩЗХЪФЫВАПРОЛДЖЭЯЧСМИТЬБЮ,/"#, "\0"),
        ),
        KeyMap::new(
            "'1234567890-=йцукенгшщзхїфівапролджєячсмитьбю.ґ\0",
            concat!(r#"₴!"№;%:?*()_+ЙЦУКЕНГШЩЗХЇФІВАПРОЛДЖЄЯЧСМИТЬБЮ,Ґ"#, "\0"),
        ),
    ]
    .map(Arc::new)
});

pub fn convert(text: &str, source: Language, target: Language, force_source: bool) -> String {
    if source == target {
        return text.to_owned();
    }
    let (Some(from), Some(to)) = (source.map(), target.map()) else {
        return text.to_owned();
    };
    let mut output = String::with_capacity(text.len());
    if force_source {
        append_mapped(&mut output, text, &from, &to);
        return output;
    }
    let mut start = 0;
    for (position, character) in text.char_indices() {
        if character.is_whitespace() {
            append_token(&mut output, &text[start..position], &from, &to);
            output.push(character);
            start = position + character.len_utf8();
        }
    }
    append_token(&mut output, &text[start..], &from, &to);
    output
}

fn append_token(output: &mut String, token: &str, from: &KeyMap, to: &KeyMap) {
    let mut source_letters = false;
    let mut other_letters = false;
    for character in token.chars().filter(|character| character.is_alphabetic()) {
        if from.code(character) != MISSING && to.code(character) == MISSING {
            source_letters = true;
        } else {
            other_letters = true;
        }
    }
    if !source_letters && other_letters {
        output.push_str(token);
        return;
    }
    append_mapped(output, token, from, to);
}

fn append_mapped(output: &mut String, token: &str, from: &KeyMap, to: &KeyMap) {
    output.extend(token.chars().map(|character| {
        to.keys
            .get(from.code(character) as usize)
            .copied()
            .filter(|&mapped| mapped != '\0')
            .unwrap_or(character)
    }));
}

pub fn next_language(cycle: &[Language], current: Option<Language>) -> Option<Language> {
    if cycle.len() < 2 {
        return None;
    }
    let position = current.and_then(|language| cycle.iter().position(|entry| *entry == language));
    Some(cycle[position.map_or(0, |index| (index + 1) % cycle.len())])
}

pub fn convert_to(text: &str, target: Language, hint: Option<Language>) -> String {
    let Some(target_map) = target.map() else {
        return text.to_owned();
    };
    if let Some(hint) = hint
        && let Some(hint_map) = hint.map()
        && text
            .chars()
            .filter(|c| c.is_alphabetic())
            .all(|c| hint_map.code(c) != MISSING)
    {
        return if hint == target {
            text.to_owned()
        } else {
            convert(text, hint, target, true)
        };
    }
    let mut source = None;
    let mut best = 0;
    for candidate in Language::ALL
        .into_iter()
        .filter(|candidate| *candidate != target)
    {
        let Some(candidate_map) = candidate.map() else {
            continue;
        };
        let score = text
            .chars()
            .filter(|&character| {
                character.is_alphabetic()
                    && candidate_map.code(character) != MISSING
                    && target_map.code(character) == MISSING
            })
            .count();
        if score > best {
            source = Some(candidate);
            best = score;
        }
    }
    if source.is_none() && detect(text, &Language::ALL, None).is_none() {
        source = hint.or_else(|| {
            Language::ALL
                .into_iter()
                .find(|candidate| *candidate != target)
        });
    }
    source.map_or_else(
        || text.to_owned(),
        |source| convert(text, source, target, false),
    )
}

pub fn detect(
    text: &str,
    candidates: &[Language],
    preferred: Option<Language>,
) -> Option<Language> {
    let mut best = None;
    let mut best_score = 0;
    for language in preferred.into_iter().chain(candidates.iter().copied()) {
        let Some(map) = language.map() else {
            continue;
        };
        let score = text
            .chars()
            .filter(|character| character.is_alphabetic() && map.code(*character) != MISSING)
            .count();
        if score > best_score {
            best = Some(language);
            best_score = score;
        }
    }
    best
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn maps_physical_keys_and_preserves_unmapped_characters() {
        assert_eq!(
            convert("Ghbdtn,\r\n🙂", Language::En, Language::Ru, true),
            "Приветб\r\n🙂"
        );
        assert_eq!(convert("ыйд", Language::Ru, Language::En, true), "sql");
        assert_eq!(
            convert("ghbdsn", Language::En, Language::Uk, true),
            "привіт"
        );
    }

    #[test]
    fn keeps_existing_target_tokens_and_resolves_shared_letters_with_hint() {
        assert_eq!(
            convert("ghbdtn привет", Language::En, Language::Ru, false),
            "привет привет"
        );
        assert_eq!(
            detect("при", &Language::ALL, Some(Language::Uk)),
            Some(Language::Uk)
        );
        assert_eq!(
            next_language(&[Language::En, Language::Ru], Some(Language::Ru)),
            Some(Language::En)
        );
    }
}
