#![cfg_attr(windows, windows_subsystem = "windows")]

// Preserve the existing Unix executable. On Windows this name is only a
// compatibility launcher for existing root shortcuts, never the resident.
#[cfg(not(windows))]
#[path = "../resident_cli.rs"]
mod resident_cli;

#[cfg(not(windows))]
fn main() {
    resident_cli::main();
}

#[cfg(windows)]
fn forward() -> std::io::Result<i32> {
    let current = std::env::current_exe()?;
    let target = current.with_file_name(typlo_rust::windows::update::RESIDENT_EXE);
    if target.canonicalize()? == current.canonicalize()? {
        return Err(std::io::Error::other(
            "Compatibility launcher cannot launch itself",
        ));
    }
    let args: Vec<_> = std::env::args_os().skip(1).collect();
    let mut command = std::process::Command::new(target);
    command.args(&args);
    if args.is_empty() {
        command.spawn()?;
        Ok(0)
    } else {
        Ok(command.status()?.code().unwrap_or(1))
    }
}

#[cfg(windows)]
fn main() {
    let code = forward().unwrap_or_else(|error| {
        eprintln!("Typlo launcher: {error}");
        1
    });
    std::process::exit(code);
}
