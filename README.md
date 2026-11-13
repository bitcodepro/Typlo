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

## Share clipboard items between Windows computers

1. On both computers, open **Settings → Clipboard history → Clipboard sharing on the local network**. Copy each computer's code into the other computer and select **Pair computer**.
2. Open history with `Ctrl+Shift+V` and select ↗ beside the item. Typlo2.0 can send text, images, files, and folders. The receiving computer adds the item to its history. Use **Open received files** to find saved files.

The computers discover each other on the local network. If discovery fails, enter a private IPv4 address manually. Allow Typlo2.0 through Windows Firewall on private networks to receive items. Transfers use TCP 49237 and discovery uses UDP 49237. You can pair with one other computer. The file transfer limit is 512 MiB. Received files stay on disk until deleted.

**Check connection** reports discovery, connectivity, and certificate verification. The **Received files** history filter shows received files and folders, their stored size, and a button to open the folder.

Starting with Preview 27, transfers show progress. **Cancel** stops the transfer and **Retry** checks the transfer ID on the receiving computer before resending. Both computers need Preview 27 or later for this behavior. A received file's menu offers **Show in File Explorer** and **Delete entry and files…**. Deleting only the history entry leaves the files on disk.

Open the snippet menu with `Ctrl+Alt+Shift+S` and type a name or trigger. Set the name in the snippet settings. Select the star or press `Ctrl+P` to pin the selected snippet at the top.

For a template such as `Hello, {name}! Ticket {ticket} is ready.{cursor}`, enable **Ask for parameters** in the snippet settings. Select the snippet from the menu, fill in the values, and press Enter in the last field. Write `{{name}}` to insert literal `{name}`.

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

The Windows workflow tests and lints every PR, push to `main`, and manual run, then builds both x64 executables with a static CRT. Packaging checks PE runtime dependencies, includes resources and dependency licenses, runs `tools/windows-ui-smoke.ps1`, and verifies the updater ZIP with the release executable. A failed gate prevents artifact upload and publication. Artifacts are retained for seven days; PRs receive artifacts only.

After merge, a `main` push publishes a new Cargo version through a draft release after checking uploaded SHA256 digests. Set the same plain `X.Y.Z` version in `Cargo.toml` and the application's `Cargo.lock` entry before the next release. The first version is `0.1.0`, with tag `v0.1.0`; preview suffixes are unsupported. The portable binary uses `CARGO_PKG_VERSION`, and the updater asset is exactly `typlo-rust-win-x64.zip`.

Only the serialized publication job has repository write permission. It releases the exact tested commit, refuses existing tags/releases belonging to another SHA, and never overwrites assets. A rerun of an already published version verifies its original assets and exits. A partial draft can resume only if every existing asset matches the current verified build; otherwise investigate the draft manually. No tag-triggered workflow or repository protection changes are required. Manual runs validate/package only.

For local release validation use PowerShell 7, Visual Studio C++ tools, and the stable MSVC Rust toolchain with `clippy` and `rust-docs` components. Set `CARGO_BUILD_TARGET=x86_64-pc-windows-msvc` and `RUSTFLAGS=-C target-feature=+crt-static`, run the commands below, then `./tools/test-release-tools.ps1` and `./tools/prepare-windows-release.ps1` from a clean checkout. The latter requires absent `package/Typlo-X.Y.Z` and `dist` directories. Publication requires GitHub CLI authentication and is reserved for CI on `main`.

## Build

```powershell
cargo test --locked --all-features
cargo clippy --locked --all-targets --all-features -- -D warnings
cargo build --release --locked --features gui --bins
```

For a Windows package, put both executables in the same folder with the resources. `tools/package-windows.ps1` creates the portable ZIP and the updater archive `typlo-rust-win-x64.zip`, then verifies the updater archive with the application itself. `tools/measure-full-windows.ps1` measures the C# and Rust background processes with history enabled and includes a separate run with Rust clipboard sharing paired. Run `typlo-rust.exe --measure-native 100` for confirmed replacement and Undo timings in native Edit and RichEdit fields.

[Migration status and known differences](docs/parity.md) · [Early benchmark results](docs/comparison.md)
