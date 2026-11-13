use crate::{layout::Language, lexicon::Decision};
use serde::{Deserialize, Serialize};
use std::{
    collections::VecDeque,
    io::{self, Write},
    path::Path,
    sync::{Arc, Mutex},
    time::{Instant, SystemTime, UNIX_EPOCH},
};

pub(super) type Shared = Arc<Mutex<Report>>;

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub struct Report {
    pub automatic: VecDeque<Automatic>,
    pub operations: VecDeque<Operation>,
    pub dictionary_healthy: Option<bool>,
}

#[derive(Clone, Copy, Debug, Serialize, Deserialize)]
pub struct Automatic {
    pub timestamp_ms: u64,
    pub elapsed_us: u64,
    pub reason: Decision,
    pub source: Option<usize>,
    pub target: Option<usize>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Operation {
    pub timestamp_ms: u64,
    pub operation: String,
    pub elapsed_us: u64,
    pub phases: Vec<Phase>,
    #[serde(default)]
    pub field: String,
    #[serde(default)]
    pub outcome: Outcome,
    #[serde(default)]
    pub dictionary_reason: Option<Decision>,
    #[serde(default)]
    pub confirmed_us: Option<u64>,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
pub enum Outcome {
    #[default]
    Observed,
    Applied,
    Unchanged,
    TargetChanged,
    UnsupportedField,
    ClipboardOrEditorBusy,
    Unconfirmed,
}

impl Outcome {
    pub fn label(self) -> &'static str {
        use crate::locale::text as tr;
        match self {
            Self::Observed => tr("rust.edit.observed", "Attempt finished"),
            Self::Applied => tr("rust.edit.applied", "Replacement confirmed"),
            Self::Unchanged => tr("rust.edit.unchanged", "No change or valid Undo"),
            Self::TargetChanged => tr("rust.edit.changed", "Input or target changed"),
            Self::UnsupportedField => tr(
                "rust.edit.unsupported",
                "Field does not support verified editing",
            ),
            Self::ClipboardOrEditorBusy => tr(
                "rust.edit.busy",
                "Editor or clipboard did not respond in time",
            ),
            Self::Unconfirmed => tr(
                "rust.edit.unconfirmed",
                "Replacement could not be confirmed",
            ),
        }
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Phase {
    pub name: String,
    pub elapsed_us: u64,
}

pub(super) fn automatic(
    shared: &Shared,
    start: Instant,
    reason: Decision,
    source: Option<Language>,
    target: Option<Language>,
) {
    if let Ok(mut report) = shared.lock() {
        if report.automatic.len() == 20 {
            report.automatic.pop_back();
        }
        report.automatic.push_front(Automatic {
            timestamp_ms: timestamp(),
            elapsed_us: elapsed(start),
            reason,
            source: source.and_then(Language::dictionary_index),
            target: target.and_then(Language::dictionary_index),
        });
    }
}

pub(super) struct Timing {
    shared: Shared,
    operation: &'static str,
    start: Instant,
    previous: Instant,
    phases: Vec<Phase>,
    field: &'static str,
    outcome: Outcome,
    dictionary_reason: Option<Decision>,
    confirmed_us: Option<u64>,
    skip: bool,
}

impl Timing {
    pub(super) fn new(shared: &Shared, operation: &'static str) -> Self {
        Self::since(shared, operation, Instant::now())
    }

    pub(super) fn since(shared: &Shared, operation: &'static str, start: Instant) -> Self {
        Self {
            shared: shared.clone(),
            operation,
            start,
            previous: start,
            phases: Vec::new(),
            field: "Windows",
            outcome: Outcome::Observed,
            dictionary_reason: None,
            confirmed_us: None,
            skip: false,
        }
    }
    pub(super) fn phase(&mut self, name: &'static str) {
        let elapsed_us = elapsed(self.previous);
        self.previous = Instant::now();
        self.phases.push(Phase {
            name: name.into(),
            elapsed_us,
        });
    }

    pub(super) fn field(&mut self, field: &'static str) {
        self.field = field;
    }

    pub(super) fn applied(&mut self, changed: bool) {
        self.confirmed_us = changed.then(|| elapsed(self.start));
        self.outcome = if changed {
            Outcome::Applied
        } else {
            Outcome::Unchanged
        };
    }

    pub(super) fn finish<T>(&mut self, result: &io::Result<T>, confirmed: impl FnOnce(&T) -> bool) {
        if let Ok(value) = result {
            self.applied(confirmed(value));
            return;
        }
        self.outcome = match result {
            Ok(_) => unreachable!(),
            Err(error) => match error.kind() {
                io::ErrorKind::Interrupted => Outcome::TargetChanged,
                io::ErrorKind::Unsupported | io::ErrorKind::PermissionDenied => {
                    Outcome::UnsupportedField
                }
                io::ErrorKind::TimedOut | io::ErrorKind::WouldBlock => {
                    Outcome::ClipboardOrEditorBusy
                }
                _ => Outcome::Unconfirmed,
            },
        };
    }

    pub(super) fn dictionary(&mut self, decision: Decision) {
        self.dictionary_reason = Some(decision);
    }

    pub(super) fn skip(&mut self) {
        self.skip = true;
    }
}

impl Drop for Timing {
    fn drop(&mut self) {
        if !self.skip
            && let Ok(mut report) = self.shared.lock()
        {
            if report.operations.len() == 120 {
                report.operations.pop_back();
            }
            report.operations.push_front(Operation {
                timestamp_ms: timestamp(),
                operation: self.operation.into(),
                elapsed_us: elapsed(self.start),
                phases: std::mem::take(&mut self.phases),
                field: self.field.into(),
                outcome: self.outcome,
                dictionary_reason: self.dictionary_reason,
                confirmed_us: self.confirmed_us,
            });
        }
    }
}

impl Report {
    pub fn latest_summary(&self) -> Option<String> {
        let op = self
            .operations
            .iter()
            .find(|op| op.operation != "Other edits")?;
        let reason = if op.operation == "Auto-correction" && op.outcome == Outcome::Unchanged {
            op.dictionary_reason
                .map_or_else(|| op.outcome.label(), Decision::message)
        } else {
            op.outcome.label()
        };
        Some(format!("{} · {}", operation_label(&op.operation), reason))
    }

    pub fn latency(&self) -> Vec<((&str, &str), usize, u64, u64)> {
        let mut groups = std::collections::BTreeMap::<_, Vec<_>>::new();
        for op in &self.operations {
            if op.outcome == Outcome::Applied {
                groups
                    .entry((op.operation.as_str(), op.field.as_str()))
                    .or_default()
                    .push(op.confirmed_us.unwrap_or(op.elapsed_us));
            }
        }
        groups
            .into_iter()
            .map(|(key, mut times)| {
                times.sort_unstable();
                let n = times.len();
                (
                    key,
                    n,
                    times[(n - 1) / 2].midpoint(times[n / 2]),
                    times[(n * 95).div_ceil(100) - 1],
                )
            })
            .collect()
    }

    pub(super) fn publish(&self, path: &Path) -> io::Result<()> {
        let profile = path.parent().ok_or(io::ErrorKind::InvalidInput)?;
        let mut file = tempfile::NamedTempFile::new_in(profile)?;
        serde_json::to_writer(file.as_file_mut(), self).map_err(io::Error::other)?;
        file.as_file_mut().flush()?;
        file.persist(path).map_err(|error| error.error)?;
        Ok(())
    }
}

pub fn operation_label(name: &str) -> &str {
    use crate::locale::text as tr;
    match name {
        "Auto-correction" => tr("rust.edit.auto", "Auto-correction"),
        "Manual conversion" => tr("rust.edit.manual", "Manual conversion"),
        "Undo" => tr("rust.edit.undo", "Undo"),
        _ => name,
    }
}

fn timestamp() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis() as u64
}
fn elapsed(start: Instant) -> u64 {
    start.elapsed().as_micros() as u64
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn splits_confirmed_latency_and_uses_the_reason_of_the_actual_attempt() {
        let shared = Shared::default();
        for (name, field) in [
            ("Manual conversion", "Scintilla"),
            ("Manual conversion", "UI Automation"),
            ("Undo", "Scintilla"),
        ] {
            let mut timing = Timing::new(&shared, name);
            timing.field(field);
            timing.applied(true);
        }
        let mut timing = Timing::new(&shared, "Auto-correction");
        timing.dictionary(Decision::AlreadyRecognized);
        timing.applied(false);
        drop(timing);
        let report = shared.lock().unwrap();
        let rows = report.latency();
        assert_eq!(rows.len(), 3);
        assert!(rows.iter().all(|row| row.1 == 1));
        assert!(
            report
                .latest_summary()
                .unwrap()
                .ends_with(Decision::AlreadyRecognized.message())
        );
    }
}
