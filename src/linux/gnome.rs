use std::process::Command;

const INTERFACE: &str = "io.github.bitcodepro.Typlo.InputSources";

pub(super) fn session() -> bool {
    std::env::var("XDG_CURRENT_DESKTOP").is_ok_and(|desktop| {
        desktop
            .split(':')
            .any(|item| item.eq_ignore_ascii_case("GNOME"))
    }) || std::env::var("DESKTOP_SESSION")
        .is_ok_and(|session| session.to_ascii_lowercase().starts_with("gnome"))
}

pub(super) fn configured() -> Vec<(String, String)> {
    let Some(output) = Command::new("gsettings")
        .args(["get", "org.gnome.desktop.input-sources", "sources"])
        .output()
        .ok()
        .filter(|result| result.status.success())
    else {
        return Vec::new();
    };
    parse(&String::from_utf8_lossy(&output.stdout))
}

fn parse(output: &str) -> Vec<(String, String)> {
    output
        .split('(')
        .filter_map(|tuple| {
            let tuple = tuple.split_once(')')?.0;
            let (kind, id) = tuple.split_once(',')?;
            if kind.trim() != "'xkb'" {
                return None;
            }
            let id = id.trim().strip_prefix('\'')?.strip_suffix('\'')?;
            let (layout, variant) = source(id)?;
            Some((layout.to_owned(), variant.to_owned()))
        })
        .take(128)
        .collect()
}

fn valid(text: &str) -> bool {
    !text.is_empty()
        && text.len() <= 80
        && text
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'_'))
}

fn source(id: &str) -> Option<(&str, &str)> {
    let (layout, variant) = id.split_once('+').unwrap_or((id, ""));
    (valid(layout) && (variant.is_empty() || valid(variant))).then_some((layout, variant))
}

fn call(method: &str, value: Option<&str>) -> Option<String> {
    let mut command = Command::new("gdbus");
    command
        .args([
            "call",
            "--session",
            "--timeout",
            "1",
            "--dest",
            "org.gnome.Shell",
            "--object-path",
            "/io/github/bitcodepro/Typlo/InputSources",
            "--method",
        ])
        .arg(format!("{INTERFACE}.{method}"));
    if let Some(value) = value {
        source(value)?;
        command.arg(value);
    }
    let output = command
        .output()
        .ok()
        .filter(|output| output.status.success())?;
    String::from_utf8(output.stdout)
        .ok()
        .map(|text| text.trim().to_owned())
}

pub(super) fn current() -> Option<String> {
    let result = call("GetCurrent", None)?;
    let id = result.strip_prefix("('")?.strip_suffix("',)")?;
    source(id)?;
    Some(id.to_owned())
}

pub(super) fn activate(id: &str) -> bool {
    call("Activate", Some(id)).as_deref() == Some("(true,)")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn includes_all_configured_groups_instead_of_the_four_loaded_by_x11() {
        let groups = parse(
            "[('xkb', 'us'), ('ibus', 'mozc-jp'), ('xkb', 'ru'), ('xkb', 'ua'), ('xkb', 'fr+oss'), ('xkb', 'de')]",
        );
        assert_eq!(groups.len(), 5);
        assert_eq!(groups[3], ("fr".into(), "oss".into()));
        assert_eq!(groups[4], ("de".into(), "".into()));
        assert_eq!(source("us+bad'"), None);
    }
}
