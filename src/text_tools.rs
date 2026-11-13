pub fn cycle_case(text: &str) -> String {
    let mut has_letters = false;
    let mut all_upper = true;
    let mut all_lower = true;
    for character in text.chars().filter(|character| character.is_alphabetic()) {
        has_letters = true;
        all_upper &= character.is_uppercase();
        all_lower &= character.is_lowercase();
    }
    if !has_letters {
        return text.to_owned();
    }
    if all_upper {
        return text.to_lowercase();
    }
    if !all_lower {
        return text.to_uppercase();
    }
    let mut result = String::with_capacity(text.len());
    let mut start_of_word = true;
    for character in text.chars() {
        if character.is_alphabetic() {
            if start_of_word {
                result.extend(character.to_uppercase());
            } else {
                result.extend(character.to_lowercase());
            }
            start_of_word = false;
        } else {
            result.push(character);
            start_of_word = !character.is_numeric() && !matches!(character, '\'' | '’');
        }
    }
    result
}

const RUSSIAN: [&str; 32] = [
    "a", "b", "v", "g", "d", "e", "zh", "z", "i", "y", "k", "l", "m", "n", "o", "p", "r", "s", "t",
    "u", "f", "kh", "ts", "ch", "sh", "shch", "", "y", "", "e", "yu", "ya",
];
const LATIN: [(&str, &str); 36] = [
    ("shch", "щ"),
    ("zh", "ж"),
    ("kh", "х"),
    ("ts", "ц"),
    ("ch", "ч"),
    ("sh", "ш"),
    ("yo", "ё"),
    ("yu", "ю"),
    ("ya", "я"),
    ("ye", "е"),
    ("a", "а"),
    ("b", "б"),
    ("v", "в"),
    ("w", "в"),
    ("g", "г"),
    ("d", "д"),
    ("e", "е"),
    ("z", "з"),
    ("i", "и"),
    ("y", "й"),
    ("k", "к"),
    ("q", "к"),
    ("l", "л"),
    ("m", "м"),
    ("n", "н"),
    ("o", "о"),
    ("p", "п"),
    ("r", "р"),
    ("s", "с"),
    ("t", "т"),
    ("u", "у"),
    ("f", "ф"),
    ("h", "х"),
    ("c", "к"),
    ("x", "кс"),
    ("j", "дж"),
];

pub fn transliterate(text: &str) -> String {
    if text
        .chars()
        .any(|character| ('Ѐ'..='ӿ').contains(&character))
    {
        return to_latin(text);
    }
    let mut remaining = text;
    let mut result = String::with_capacity(text.len());
    while let Some(first) = remaining.chars().next() {
        let matched = LATIN.iter().find_map(|&(latin, cyrillic)| {
            let prefix = remaining.get(..latin.len())?;
            prefix
                .eq_ignore_ascii_case(latin)
                .then_some((prefix, cyrillic))
        });
        if let Some((prefix, cyrillic)) = matched {
            remaining = &remaining[prefix.len()..];
            let all_upper = if prefix.len() > 1 {
                prefix.as_bytes().last().is_some_and(u8::is_ascii_uppercase)
            } else {
                remaining.chars().next().is_some_and(char::is_uppercase)
            };
            append_case(&mut result, cyrillic, first.is_uppercase(), all_upper);
        } else {
            result.push(first);
            remaining = &remaining[first.len_utf8()..];
        }
    }
    result
}

fn to_latin(text: &str) -> String {
    let ukrainian = text.chars().any(|character| "ґіїєҐІЇЄ".contains(character));
    let mut previous = None::<char>;
    let mut characters = text.chars().peekable();
    let mut result = String::with_capacity(text.len());
    while let Some(character) = characters.next() {
        let lower = character.to_lowercase().next().unwrap_or(character);
        let start = !previous.is_some_and(char::is_alphabetic);
        let mapped = match lower {
            'г' if ukrainian => Some("h"),
            'ґ' if ukrainian => Some("g"),
            'и' if ukrainian => Some("y"),
            'і' if ukrainian => Some("i"),
            'ї' if ukrainian => Some(if start { "yi" } else { "i" }),
            'є' if ukrainian => Some(if start { "ye" } else { "ie" }),
            '\'' | '’' if ukrainian => Some(""),
            'ё' => Some("yo"),
            'а'..='я' => Some(RUSSIAN[(lower as u32 - 'а' as u32) as usize]),
            _ => None,
        };
        if let Some(mapped) = mapped {
            let all_upper = previous.is_some_and(char::is_uppercase)
                || characters.peek().is_some_and(|next| next.is_uppercase());
            append_case(&mut result, mapped, character.is_uppercase(), all_upper);
        } else {
            result.push(character);
        }
        previous = Some(character);
    }
    result
}

fn append_case(result: &mut String, text: &str, uppercase: bool, all_uppercase: bool) {
    for (index, character) in text.chars().enumerate() {
        if uppercase && (index == 0 || all_uppercase) {
            result.extend(character.to_uppercase());
        } else {
            result.push(character);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn cycles_case_with_apostrophes_and_preserves_non_letters() {
        assert_eq!(cycle_case("l’été привет 🙂"), "L’été Привет 🙂");
        assert_eq!(cycle_case("L’été Привет 🙂"), "L’ÉTÉ ПРИВЕТ 🙂");
        assert_eq!(cycle_case("L’ÉTÉ ПРИВЕТ 🙂"), "l’été привет 🙂");
    }

    #[test]
    fn transliterates_longest_sequences_and_respects_case() {
        assert_eq!(transliterate("Щука ЩУКА"), "Shchuka SHCHUKA");
        assert_eq!(transliterate("Shchuka SHCHUKA"), "Щука ЩУКА");
        assert_eq!(transliterate("Київ 🙂"), "Kyiv 🙂");
    }
}
