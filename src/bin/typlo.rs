#![cfg_attr(windows, windows_subsystem = "windows")]

#[path = "../resident_cli.rs"]
mod resident_cli;

fn main() {
    resident_cli::main();
}
