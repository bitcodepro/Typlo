use crate::layout::{self, Language};
use serde::{Deserialize, Serialize};
use std::{
    collections::{HashMap, HashSet, hash_map::Entry},
    io,
};

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub struct Snippet {
    pub trigger: String,
    pub text: String,
    #[serde(default, skip_serializing_if = "String::is_empty")]
    pub name: String,
    #[serde(default, skip_serializing_if = "std::ops::Not::not")]
    pub parameters: bool,
}

impl Snippet {
    pub fn parameter_names(&self) -> io::Result<Vec<String>> {
        let mut names = Vec::new();
        if self.parameters {
            Template::parse_with(&self.text, true, |name| {
                if !names.iter().any(|found| found == name) {
                    if names.len() == 6 {
                        return Err(invalid("A snippet supports up to six distinct parameters"));
                    }
                    names.push(name.to_owned());
                }
                Ok(name.to_owned())
            })?;
        }
        Ok(names)
    }

    pub fn fill(&self, values: &[(String, String)]) -> io::Result<Template> {
        Template::parse_with(&self.text, self.parameters, |name| {
            values
                .iter()
                .find(|(key, _)| key == name)
                .map(|(_, value)| value.clone())
                .ok_or_else(|| invalid("A snippet parameter is missing"))
        })
    }
}

#[derive(Clone)]
pub struct Template {
    pub text: String,
    pub caret: Option<usize>,
}

impl Template {
    #[cfg(any(target_os = "linux", target_os = "macos"))]
    pub(crate) fn with_lf(&self) -> Self {
        let caret = self.caret.map(|offset| {
            let mut previous = 0;
            self.text
                .encode_utf16()
                .take(offset)
                .filter(|&unit| {
                    let crlf = previous == 13 && unit == 10;
                    previous = unit;
                    !crlf
                })
                .count()
        });
        Self {
            text: self.text.replace("\r\n", "\n"),
            caret,
        }
    }

    pub fn parse(template: &str) -> io::Result<Self> {
        Self::parse_with(template, false, |_| unreachable!())
    }

    fn parse_with(
        template: &str,
        parameters: bool,
        mut value: impl FnMut(&str) -> io::Result<String>,
    ) -> io::Result<Self> {
        let mut output = String::with_capacity(template.len());
        let mut caret = None;
        let mut remaining = template;
        while !remaining.is_empty() {
            if let Some(rest) = remaining.strip_prefix("{{cursor}}") {
                output.push_str("{cursor}");
                remaining = rest;
            } else if let Some(rest) = remaining.strip_prefix("{cursor}") {
                if caret.replace(output.encode_utf16().count()).is_some() {
                    return Err(invalid("Only one cursor marker is allowed"));
                }
                remaining = rest;
            } else if parameters && let Some((name, rest, escaped)) = parameter(remaining) {
                if escaped {
                    output.push('{');
                    output.push_str(name);
                    output.push('}');
                } else {
                    output.push_str(
                        &value(name)?
                            .replace("\r\n", "\n")
                            .replace('\r', "\n")
                            .replace('\n', "\r\n"),
                    );
                }
                remaining = rest;
            } else if let Some(rest) = remaining.strip_prefix("\r\n") {
                output.push_str("\r\n");
                remaining = rest;
            } else {
                let character = remaining.chars().next().expect("nonempty template");
                if matches!(character, '\r' | '\n') {
                    output.push_str("\r\n");
                } else {
                    output.push(character);
                }
                remaining = &remaining[character.len_utf8()..];
            }
        }
        if output.is_empty() || output.contains('\0') {
            return Err(invalid("A snippet needs nonempty text without NUL"));
        }
        Ok(Self {
            text: output,
            caret,
        })
    }
}

fn parameter(text: &str) -> Option<(&str, &str, bool)> {
    let escaped = text.starts_with("{{");
    let inside = text.strip_prefix(if escaped { "{{" } else { "{" })?;
    let (name, rest) = inside.split_once(if escaped { "}}" } else { "}" })?;
    if name.len() > 32
        || !name.as_bytes().first().is_some_and(u8::is_ascii_alphabetic)
        || !name.bytes().all(|b| b.is_ascii_alphanumeric() || b == b'_')
    {
        return None;
    }
    Some((name, rest, escaped))
}

#[derive(Default)]
pub struct Snippets {
    entries: HashMap<String, Option<Template>>,
    lengths: Vec<usize>,
}

impl Snippets {
    pub fn compile(snippets: &[Snippet]) -> io::Result<Self> {
        Self::compile_for(snippets, &Language::ALL)
    }

    pub fn compile_for(snippets: &[Snippet], layouts: &[Language]) -> io::Result<Self> {
        let mut compiled = Self::default();
        let mut exact = HashSet::new();
        let mut translated = HashMap::new();
        for snippet in snippets {
            let trigger = snippet.trigger.trim();
            if !(2..=64).contains(&trigger.len())
                || trigger
                    .chars()
                    .any(|character| character.is_whitespace() || character.is_control())
            {
                return Err(invalid(
                    "A snippet trigger needs 2–64 bytes and no whitespace",
                ));
            }
            let source = layout::detect(trigger, layouts, None).unwrap_or(Language::En);
            let mut aliases: Vec<_> = layouts
                .iter()
                .copied()
                .filter(|language| language.convertible())
                .map(|target| layout::convert(trigger, source, target, true).to_lowercase())
                .collect();
            aliases.sort_unstable();
            aliases.dedup();
            let template = if snippet.parameter_names()?.is_empty() {
                Some(snippet.fill(&[])?)
            } else {
                None
            };
            let literal = trigger.to_lowercase();
            if !exact.insert(literal.clone()) {
                return Err(invalid("Duplicate snippet trigger"));
            }
            compiled.entries.insert(literal.clone(), template.clone());
            for alias in aliases.into_iter().filter(|alias| *alias != literal) {
                match translated.entry(alias) {
                    Entry::Vacant(entry) => {
                        entry.insert(template.clone());
                    }
                    Entry::Occupied(mut entry) => {
                        entry.insert(None);
                    }
                }
            }
        }
        // Explicit triggers win. An ambiguous generated alias cannot choose a snippet.
        for (alias, template) in translated {
            compiled.entries.entry(alias).or_insert(template);
        }
        compiled
            .lengths
            .extend(compiled.entries.keys().map(String::len));
        compiled.lengths.sort_unstable_by(|a, b| b.cmp(a));
        compiled.lengths.dedup();
        Ok(compiled)
    }

    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }

    pub fn matches<'a>(&self, typed: &'a str) -> Option<(&'a str, &Template)> {
        let lower = typed.to_lowercase();
        if lower.len() != typed.len() {
            return None;
        }
        for &length in &self.lengths {
            let Some(start) = lower.len().checked_sub(length) else {
                continue;
            };
            if let Some(template) = lower.get(start..).and_then(|key| self.entries.get(key)) {
                return Some((typed.get(start..)?, template.as_ref()?));
            }
        }
        None
    }
}

fn invalid(message: &str) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidInput, message)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parameters_are_opt_in_and_keep_the_cursor_after_literal_values() {
        let mut snippet = Snippet {
            trigger: ";ticket".into(),
            text: "Hi {name}, {name}! {{ticket}} {cursor}{ticket}".into(),
            ..Default::default()
        };
        assert!(snippet.parameter_names().unwrap().is_empty());
        assert!(snippet.fill(&[]).unwrap().text.contains("{name}"));
        snippet.parameters = true;
        assert_eq!(snippet.parameter_names().unwrap(), ["name", "ticket"]);
        assert!(snippet.fill(&[]).is_err());
        let result = snippet
            .fill(&[
                ("name".into(), "🙂{cursor}".into()),
                ("ticket".into(), "42".into()),
            ])
            .unwrap();
        let prefix = "Hi 🙂{cursor}, 🙂{cursor}! {ticket} ";
        assert_eq!(result.text, format!("{prefix}42"));
        assert_eq!(result.caret, Some(prefix.encode_utf16().count()));
        assert!(
            Snippets::compile(&[snippet])
                .unwrap()
                .matches(";ticket")
                .is_none()
        );
    }

    #[test]
    fn supports_escaped_marker_multiline_text_and_utf16_caret() {
        let template = Template::parse("🙂\n{cursor} {{cursor}}").unwrap();
        assert_eq!(template.text, "🙂\r\n {cursor}");
        assert_eq!(template.caret, Some(4));
        assert!(Template::parse("{cursor}x{cursor}").is_err());
    }

    #[test]
    fn matches_the_longest_typed_trigger_across_layouts() {
        let compiled = Snippets::compile(&[
            Snippet {
                trigger: "ad".into(),
                text: "short".into(),
                ..Default::default()
            },
            Snippet {
                trigger: ";ad".into(),
                text: "address".into(),
                ..Default::default()
            },
        ])
        .unwrap();
        let (trigger, template) = compiled.matches("go ЖФВ").unwrap();
        assert_eq!(trigger, "ЖФВ");
        assert_eq!(template.text, "address");
    }

    #[test]
    fn explicit_triggers_win_over_layout_aliases() {
        let snippets = [
            Snippet {
                trigger: "ad".into(),
                text: "Latin".into(),
                ..Default::default()
            },
            Snippet {
                trigger: "фв".into(),
                text: "Cyrillic".into(),
                ..Default::default()
            },
        ];
        for mut ordered in [snippets.clone(), snippets] {
            let compiled = Snippets::compile(&ordered).unwrap();
            assert_eq!(compiled.matches("ad").unwrap().1.text, "Latin");
            assert_eq!(compiled.matches("фв").unwrap().1.text, "Cyrillic");
            ordered[1].trigger = "AD".into();
            assert!(Snippets::compile(&ordered).is_err());
        }
    }
}
