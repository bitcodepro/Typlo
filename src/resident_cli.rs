use std::{env, fs, hint::black_box, io, path::Path, time::Instant};
#[cfg(target_os = "linux")]
use typlo_rust::linux as desktop;
#[cfg(target_os = "macos")]
use typlo_rust::macos as desktop;
use typlo_rust::{
    config,
    layout::{self, Language},
};

pub fn main() {
    if let Err(error) = run() {
        typlo_rust::runtime_error!("Typlo Rust: {error}");
        std::process::exit(1);
    }
}

#[cfg(windows)]
fn helper_profile(path: Option<&str>) -> io::Result<std::path::PathBuf> {
    let default = config::profile();
    let profile = path.map_or_else(|| default.clone(), std::path::PathBuf::from);
    if profile == default {
        typlo_rust::windows::prepare_portable_profile(&profile)?;
    }
    typlo_rust::runtime_log::initialize(&profile, "resident");
    Ok(profile)
}

fn run() -> Result<(), Box<dyn std::error::Error>> {
    let args: Vec<_> = env::args().skip(1).collect();
    match args.as_slice() {
        [mode, fixture, iterations] if mode == "--engine-bench" => {
            benchmark(Path::new(fixture), iterations.parse()?)?
        }
        [mode, iterations] if mode == "--dictionary-bench" => {
            benchmark_dictionary(iterations.parse()?)?
        }
        [mode, profile] if mode == "--spelling-worker" => {
            typlo_rust::spelling::worker(Path::new(profile))?
        }
        #[cfg(windows)]
        [mode] if mode == "--list-layouts" => {
            for layout in config::available_layouts() {
                println!("{}", layout.name());
            }
        }
        #[cfg(any(target_os = "linux", target_os = "macos"))]
        [mode] if mode == "--list-layouts" => {
            for layout in desktop::available_layouts()? {
                println!("{}", layout.name());
            }
        }
        #[cfg(any(target_os = "linux", target_os = "macos"))]
        [mode] if mode == "--current-layout" => {
            if let Some(layout) = desktop::active_layout()? {
                println!("{}", layout.name());
            }
        }
        #[cfg(any(target_os = "linux", target_os = "macos"))]
        [mode, layout] if mode == "--switch-layout" => desktop::set_layout(layout)?,
        #[cfg(any(target_os = "linux", target_os = "macos"))]
        [mode, layout] if mode == "--convert" => desktop::convert(&config::profile(), layout)?,
        #[cfg(any(target_os = "linux", target_os = "macos"))]
        [mode] if mode == "--history" => {
            typlo_rust::unix_clipboard::open_history(&config::profile())?.wait()?;
        }
        #[cfg(any(target_os = "linux", target_os = "macos"))]
        [mode] if mode == "--settings" => {
            std::process::Command::new(std::env::current_exe()?.with_file_name("typlo-settings"))
                .spawn()?
                .wait()?;
        }
        #[cfg(target_os = "linux")]
        [mode, profile, format] if mode == "--clipboard-event" => {
            typlo_rust::unix_clipboard::record(Path::new(profile), format)?
        }
        #[cfg(any(target_os = "linux", target_os = "macos"))]
        [mode, profile] if mode == "--profile" => desktop::run(Path::new(profile))?,
        #[cfg(any(target_os = "linux", target_os = "macos"))]
        [mode, profile, report] if mode == "--managed-resident" => {
            desktop::run_managed(Path::new(profile), Some(Path::new(report)))?;
        }
        #[cfg(any(target_os = "linux", target_os = "macos"))]
        [] => {
            let profile = config::profile();
            if !typlo_rust::unix_update::launch_installed(&profile)? {
                desktop::run(&profile)?;
            }
        }
        #[cfg(windows)]
        [mode] if mode == "--native-self-test" => {
            typlo_rust::windows::native_self_test()?;
            println!(
                "PASS cross-process Edit and plain RichEdit conversion and Undo; formatted, multiline RichEdit and password fields declined"
            );
            println!("PASS Windows shortcut conflict preserves the previous binding");
            println!(
                "PASS multiline snippet with UTF-16 caret; dictionary correction, Undo, and last word with trailing space"
            );
        }
        #[cfg(windows)]
        [command, mode] if command == "--native-test-editor" => {
            typlo_rust::windows::native_test_editor(mode)?
        }
        #[cfg(windows)]
        [mode, profile, report] if mode == "--background-probe" => {
            typlo_rust::windows::run(Path::new(profile), Some(Path::new(report)))?
        }
        #[cfg(windows)]
        [mode, iterations] if mode == "--measure-native" => {
            println!(
                "{}",
                typlo_rust::windows::measure_native(iterations.parse()?)?
            );
        }
        #[cfg(windows)]
        [mode, profile, report] if mode == "--measure-resident" => {
            typlo_rust::windows::run_measurement(Path::new(profile), Path::new(report))?;
        }
        #[cfg(windows)]
        [mode, profile, peer] if mode == "--prepare-lan-fixture" => {
            use typlo_rust::windows::lan_clipboard::{invite, pair};
            let profile = Path::new(profile);
            let peer = Path::new(peer);
            let left = invite(profile)?;
            let right = invite(peer)?;
            pair(profile, &right, "")?;
            pair(peer, &left, "")?;
        }
        #[cfg(windows)]
        [mode, archive, version, digest] if mode == "--verify-update-package" => {
            typlo_rust::windows::update::verify_package(Path::new(archive), version, digest)?
        }
        #[cfg(windows)]
        [mode, profile, entry] if mode == "--lan-send" => {
            typlo_rust::windows::lan_clipboard::jobs::run(
                &helper_profile(Some(profile))?,
                entry.parse()?,
                false,
            )?
        }
        #[cfg(windows)]
        [mode, profile, entry] if mode == "--lan-retry" => {
            typlo_rust::windows::lan_clipboard::jobs::run(
                &helper_profile(Some(profile))?,
                entry.parse()?,
                true,
            )?
        }
        #[cfg(windows)]
        [mode, version, hash] if mode == "--install-update" => {
            helper_profile(None)?;
            typlo_rust::windows::update::install_selected(version, hash)?
        }
        #[cfg(windows)]
        [mode, version, hash, status] if mode == "--install-update" => {
            helper_profile(None)?;
            typlo_rust::windows::update::install_selected_with_status(
                version,
                hash,
                Some(Path::new(status)),
            )?;
        }
        #[cfg(windows)]
        [mode] if mode == "--auto-update" => {
            helper_profile(None)?;
            typlo_rust::windows::update::auto_install()?;
        }
        #[cfg(windows)]
        [mode, report] if mode == "--updated-resident" => {
            typlo_rust::windows::run_updated(&config::profile(), Path::new(report))?
        }
        #[cfg(windows)]
        [mode] if mode == "--restart-previous" => {
            typlo_rust::windows::run_previous(&config::profile())?
        }
        #[cfg(windows)]
        [mode, source] if mode == "--import-history" => {
            let imported = typlo_rust::windows::history::History::open(&helper_profile(None)?)?
                .import_legacy(Path::new(source))?;
            println!("{imported}");
        }
        #[cfg(windows)]
        [mode] if mode == "--query-update" => {
            let version = typlo_rust::windows::update::check()?.map(|update| update.version);
            serde_json::to_writer(io::stdout().lock(), &version)?;
        }
        #[cfg(windows)]
        [mode, window, label, expected] if mode == "--uia-snapshot" => {
            typlo_rust::windows::probe_accessible_snapshot(window.parse()?, label, expected)?;
        }
        #[cfg(windows)]
        [mode, window, label, original, replacement] if mode == "--uia-preserve-separator" => {
            typlo_rust::windows::probe_accessible_separator(
                window.parse()?,
                label,
                original,
                replacement,
            )?;
        }
        #[cfg(windows)]
        [mode, window, label, start, end] if mode == "--uia-range" => {
            typlo_rust::windows::probe_accessible_range(
                window.parse()?,
                label,
                start.parse()?,
                end.parse()?,
            )?;
        }
        #[cfg(windows)]
        [mode, window, label, typed, expected] if mode == "--uia-correction-range" => {
            typlo_rust::windows::probe_accessible_correction_range(
                window.parse()?,
                label,
                typed,
                expected,
            )?;
        }
        #[cfg(windows)]
        [] => {
            if !typlo_rust::windows::update::launch_installed()? {
                typlo_rust::windows::run(&config::profile(), None)?;
            }
        }
        _ => return Err(io::Error::other("Unrecognized Typlo command").into()),
    }
    Ok(())
}

fn benchmark(fixture: &Path, iterations: usize) -> io::Result<()> {
    if iterations == 0 {
        return Err(io::Error::other("Iterations must be positive"));
    }
    let data = fs::read_to_string(fixture)?;
    let cases = data
        .lines()
        .map(|line| {
            let (pair, input) = line
                .split_once('\t')
                .ok_or_else(|| io::Error::other("Expected layout pair and text"))?;
            let (source, target) = pair
                .split_once('-')
                .ok_or_else(|| io::Error::other("Expected source-target"))?;
            let parse = |value| match value {
                "en" => Ok(Language::En),
                "ru" => Ok(Language::Ru),
                "uk" => Ok(Language::Uk),
                _ => Err(io::Error::other("Unknown layout")),
            };
            Ok((parse(source)?, parse(target)?, input))
        })
        .collect::<io::Result<Vec<_>>>()?;
    let operation = |&(source, target, input): &(Language, Language, &str)| {
        layout::convert(input, source, target, true)
    };
    let mut checksum = 0xcbf29ce484222325u64;
    for case in &cases {
        for byte in operation(case).bytes().chain(std::iter::once(b'\n')) {
            checksum = (checksum ^ u64::from(byte)).wrapping_mul(0x100000001b3);
        }
    }
    for _ in 0..500 {
        for case in &cases {
            black_box(operation(black_box(case)));
        }
    }
    let start = Instant::now();
    for _ in 0..iterations {
        for case in &cases {
            black_box(operation(black_box(case)));
        }
    }
    println!(
        "cases={} iterations={} elapsed_ms={:.3} checksum={checksum:016x}",
        cases.len(),
        iterations,
        start.elapsed().as_secs_f64() * 1000.0
    );
    Ok(())
}

fn benchmark_dictionary(iterations: usize) -> io::Result<()> {
    use typlo_rust::{dictionary::Dictionary, lexicon::Vocabulary};

    if !(1..=100_000).contains(&iterations) {
        return Err(io::Error::other("Choose 1 to 100000 iterations"));
    }
    let profile = tempfile::tempdir()?;
    let dictionary = Dictionary::new(profile.path());
    dictionary.set_correction("helo", "hello")?;
    let mut vocabulary = Vocabulary::installed()?;
    let cases = [
        ("rfr", Language::En, Language::Ru, Some("как")),
        ("xnj", Language::En, Language::Ru, Some("что")),
        ("gjlrk.xbnm", Language::En, Language::Ru, Some("подключить")),
        ("ghbdsn", Language::En, Language::Uk, Some("привіт")),
        ("ыйд", Language::Ru, Language::En, Some("sql")),
        ("“Ghbdtn”", Language::En, Language::Ru, Some("“Привет”")),
        ("https://example.org", Language::En, Language::Ru, None),
        ("hello", Language::En, Language::Ru, None),
        ("x123", Language::En, Language::Ru, None),
    ];
    let mut matched = 0;
    let mut samples = Vec::with_capacity(iterations);
    for attempt in 0..iterations {
        let (typed, source, target, expected) = cases[attempt % cases.len()];
        let start = Instant::now();
        let decision = vocabulary.suggest(
            &dictionary,
            black_box(typed),
            source,
            &[source, target],
            true,
        )?;
        samples.push(start.elapsed().as_secs_f64() * 1_000.);
        matched += usize::from(
            decision
                .as_ref()
                .map(|(text, language)| (text.as_str(), *language))
                == expected.map(|text| (text, target)),
        );
    }
    samples.sort_unstable_by(f64::total_cmp);
    println!(
        "{}",
        serde_json::json!({
            "attempts": iterations, "matched": matched,
            "p50_ms": samples[iterations.div_ceil(2) - 1], "p95_ms": samples[(iterations * 95).div_ceil(100) - 1]
        })
    );
    if matched != iterations {
        return Err(io::Error::other("Unexpected dictionary results"));
    }
    Ok(())
}
