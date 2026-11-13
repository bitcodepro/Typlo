# Typlo2.0

Typlo2.0 is the Rust version of Typlo. [Original C# project](https://github.com/bitcodepro/Typlo)

On Windows, run `typlo-rust.exe` and open Settings from the notification area. Keep `typlo-settings.exe` and the `resources` folder beside the executable. The profile lives in `%LOCALAPPDATA%\Typlo-Rust`. New profiles use English. Change it under **Settings → Interface language**.

| Windows shortcut | Action |
|---|---|
| Ctrl+Shift | Switch layouts using the current app's rule |
| Ctrl+Alt+Shift+Space | Convert the selection or recently typed characters |
| Ctrl+Alt+Shift+Z | Undo the last confirmed conversion |
| Ctrl+Alt+Shift+L | Choose a dictionary-backed layout correction |
| Ctrl+Shift+K | Add the selected word to the dictionary |
| Ctrl+Shift+L | Fix spelling in the selection |
| Ctrl+Shift+P | Review spelling and manage personal correction pairs |
| Ctrl+Shift+V | Open clipboard history. Press it again to paste the previous item |
| Win+Shift+S | Copy a screenshot of an area to the clipboard |
| Ctrl+Alt+Shift+F | Capture an area and open the editor to save it as JPEG |
| Ctrl+Shift+[ | Open the latest screenshot in the editor |
| Alt + left mouse drag | Draw on the screen |

While drawing, `1`–`9` select widths from 2 to 40 pixels. The mouse wheel adjusts the width by one pixel per step. Scrolling outside a stroke or pressing `Ctrl+Alt+Shift+X` clears the drawing. **Draw on screen** lets you change or disable the gesture.

Configure autocorrection after Space, snippets, and layout cycling per app. Change shortcuts in Settings. If you run the C# and Rust versions together, they may compete for the same shortcuts.

On Windows, letters, top-row digits, and supported punctuation in shortcuts refer to physical US QWERTY positions. For example, an assigned `Ctrl+N` uses the same key after you switch layouts.

Clipboard history opens near the cursor. Its item menu supports viewing, pasting, pinning, and deleting items. You can paste formatted content as plain text and extract text from images. `Ctrl+O` opens the viewer, `Ctrl+T` runs OCR, and `Ctrl+1`–`Ctrl+9` paste items by position.

Select the pencil to edit a text item or add a caption to an image or file selection. Typlo copies and shares the attachment and caption as one entry. When pasting, the destination app chooses which Windows clipboard formats it can use. Update Typlo2.0 on both computers to share entries with captions.

## Share clipboard items between Windows computers

1. On both computers, open **Clipboard sharing with another computer** at the top of **Settings**. Copy each computer's code into the other computer and select **Pair computer**.
2. Open history with `Ctrl+Shift+V` and select ↗ beside the item. Typlo2.0 can send text, images, files, and folders. The receiving computer adds the item to its history. Use **Open received files** to find saved files.

The computers discover each other on the local network. If discovery fails, enter a private IPv4 address manually. Allow Typlo2.0 through Windows Firewall on private networks to receive items. Transfers use TCP 49237 and discovery uses UDP 49237. You can pair with one other computer. The file transfer limit is 512 MiB. Received files stay on disk until deleted.

**Check connection** reports discovery, connectivity, and certificate verification. The **Received files** history filter shows received files and folders, their stored size, and a button to open the folder.

Starting with Preview 27, transfers show progress. **Cancel** stops the transfer and **Retry** checks the transfer ID on the receiving computer before resending. Both computers need Preview 27 or later for this behavior. A received file's menu offers **Show in File Explorer** and **Delete entry and files…**. Deleting only the history entry leaves the files on disk.

Open the snippet menu with `Ctrl+Alt+Shift+S` and type a name or trigger. Set the name in the snippet settings. Select the star or press `Ctrl+P` to pin the selected snippet at the top.

For a template such as `Hello, {name}! Ticket {ticket} is ready.{cursor}`, enable **Ask for parameters** in the snippet settings. Select the snippet from the menu, fill in the values, and press Enter in the last field. Write `{{name}}` to insert literal `{name}`.

Use **Settings → Import settings** to load a C# settings JSON or a Typlo2.0 export. **Export settings** saves settings, snippets, and spelling correction pairs as JSON. Use **Backup** in **Typlo tools** to also include personal dictionary words and autocorrection exceptions.

## Linux and macOS

Run `./typlo-rust` and open Settings from the tray menu or with `./typlo-rust --settings`. Use `./typlo-rust --history` to open history. Keep `typlo-settings` and `resources` beside the main binary.

- On Linux/X11, text replacement requires `libxkbcommon-x11` and AT-SPI. Clipboard history requires `xclip`. `Ctrl+Shift+V` opens history. Press it again to paste the previous item in the original window. For GNOME layout switching, install the extension in `integrations/gnome`.
- On Wayland, install `wl-clipboard` and assign commands in your compositor settings. Copy text before running `./typlo-rust --convert ru` to convert it in the clipboard.
- On macOS, grant Accessibility and Input Monitoring permissions.

## Import Windows clipboard history

Pass `--import-history` the original Typlo folder containing `history.db` and `history.key`. In a portable installation this is usually the `data` folder beside the original executable.

```powershell
.\typlo-rust.exe --import-history "C:\path\to\Typlo\data"
```

## Windows releases

### Manual migration from v0.1.0 to v0.1.1

1. Download **`typlo-win-x64.zip`** from the published **v0.1.1** release when it is available. The old client cannot discover this renamed asset automatically.
2. Extract the entire ZIP into a **separate new writable folder**. Keep `typlo-rust.exe`, `typlo-settings.exe` and `resources` together; do not copy the old `rust-current.txt` or `versions` directory into it.
3. Exit the old Typlo process using its tray **Exit** command.
4. Run `typlo-rust.exe` from the new folder. It uses the same profile at `%LOCALAPPDATA%\Typlo-Rust`; your settings and history stay there. Update any startup shortcut to the new executable path.

The published v0.1.0 ZIPs, tag and assets are retained. This is a manual handoff, not an automatic v0.1.0 upgrade. Once on v0.1.1, future update checks select `typlo-win-x64.zip`.

### Installation and update contract

Download **`typlo-win-x64.zip`** from a published release. For a fresh/manual installation, extract it into a new writable folder and run `typlo-rust.exe`. Both EXEs, `resources`, README and licenses are directly in the ZIP root. From v0.1.1 onward, this is also the exact ZIP used by the in-app updater. New releases have one Windows ZIP; all historical v0.1.0 assets remain unchanged. The `.sha256` file and `release-manifest.json` are verification metadata outside the ZIP.

Windows checks for updates in the background about 20 seconds after startup and shows an available version in the tray menu. Settings → **Check for updates** uses the same feed and exact asset name. **Install now** downloads and verifies the ZIP, probes the new executable, switches the active version and rolls back if startup fails. Startup checks do not install without confirmation. For v0.1.1 and later, subsequent in-app updates use `typlo-win-x64.zip`. Installed v0.1.0 only looks for `typlo-rust-win-x64.zip` and cannot discover the renamed asset.

The Windows workflow tests and lints every PR, push to `main`, and manual run, then builds both x64 executables with a static CRT. Packaging checks PE runtime dependencies, includes resources and dependency licenses, runs `tools/windows-ui-smoke.ps1`, and verifies the updater ZIP with the release executable. A failed gate prevents artifact upload and publication. Hosted CI runs UI smoke in a temporary copy with a SHA256-pinned Mesa llvmpipe driver (`tools/windows-software-gl-smoke.ps1`); this test driver is excluded from release archives. Artifacts are retained for seven days; PRs receive artifacts only.

After merge, a `main` push publishes a new Cargo version through a draft release after checking uploaded SHA256 digests. Set the same plain `X.Y.Z` version in `Cargo.toml` and the application's `Cargo.lock` entry before the next release. This change prepares `0.1.1` / `v0.1.1`; published `v0.1.0` and its tags/assets remain immutable. Preview suffixes are unsupported. Both binaries use `CARGO_PKG_VERSION`; v0.1.1 and future Windows releases use exactly `typlo-win-x64.zip`.

Only the serialized publication job has repository write permission. A new release uses the exact tested commit and never overwrites assets. A rerun of that SHA verifies the original release and exits. A later main commit with the same Cargo version is a separate no-op: it verifies the original tag SHA, exact release target, original manifest commit/version, every original asset hash and GitHub digest, then exits without writes. Historical v0.1.0 assets, including its portable ZIP, remain intact. A draft from another SHA is always rejected. A partial draft can resume only if every existing asset matches the current verified build. No tag-triggered workflow or protection changes are required. Manual runs validate/package only.

New manifests declare `schema_version: 1` and `platform: windows-x64`, and contain hashes for one ZIP and its checksum file. Windows build, package and compatibility gates are platform-specific. Linux/Ubuntu and macOS source/updater code remains available, but this workflow produces no tested release packages for them; future platforms require their own build/runtime/package gates before their manifests are accepted.

Verification levels: Rust tests cover feed selection, version/digest rules, staging and rollback; offline mocks cover draft publication, retry, no-overwrite and both no-op cases. Packaging runs the current executable's verifier and `tools/test-windows-package-compat.ps1` runs the SHA256-pinned published v0.1.0 verifier against the internal ZIP format and extracts it into an isolated manual-install folder. This is not proof of old-client discovery: v0.1.0 will not find the new asset name. UI smoke checks release binaries. These checks do not establish a live end-to-end upgrade to a newer published version; no live upgrade or publication is performed while preparing this PR.

For local release validation use PowerShell 7, Visual Studio C++ tools, and the MSVC Rust toolchain pinned in `rust-toolchain.toml` with `clippy` and `rust-docs` components. Set `CARGO_BUILD_TARGET=x86_64-pc-windows-msvc` and `RUSTFLAGS=-C target-feature=+crt-static`, run the commands below, then `./tools/test-release-tools.ps1` and `./tools/prepare-windows-release.ps1` from a clean checkout. The latter requires absent `package/Typlo-X.Y.Z` and `dist` directories. Publication requires GitHub CLI authentication and is reserved for CI on `main`.

## Build

```powershell
cargo test --locked --all-features
cargo clippy --locked --all-targets --all-features -- -D warnings
cargo build --release --locked --features gui --bins
```

For a Windows package, put both executables in the same folder with the resources. `tools/package-windows.ps1` creates the single installation/update archive `typlo-win-x64.zip` and its SHA256 checksum, then verifies the archive with the application itself. `tools/measure-full-windows.ps1` measures the C# and Rust background processes with history enabled and includes a separate run with Rust clipboard sharing paired. Run `typlo-rust.exe --measure-native 100` for confirmed replacement and Undo timings in native Edit and RichEdit fields.

[Migration status and known differences](docs/parity.md) · [Early benchmark results](docs/comparison.md)
