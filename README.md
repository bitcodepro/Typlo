# Typlo

Tray utility for Windows, macOS and Linux (Avalonia / .NET 10):

1. **Layout fix** – select text typed in the wrong layout and press a hotkey: `ghbdtn` → `привет`.
   - `Ctrl+Alt+Shift+Space` – toggle between the pair (EN ⇄ RU by default)
   - `Ctrl+Alt+Shift+E` / `Ctrl+Alt+Shift+R` – convert into English / Russian (Ukrainian available)
   - Nothing selected? The word left of the caret is converted (can be turned off).
   - The clipboard is restored afterwards; the system layout is switched too (Windows, Linux X11).
   - `Ctrl+Alt+Shift+C` cycles the case (lower → Title → UPPER); `Ctrl+Alt+Shift+T` transliterates (Привет ⇄ Privet).
   - Snippets: typing a trigger such as `;addr` replaces it with saved text, in any keyboard layout.
2. **Clipboard history** – `Ctrl+Shift+V` opens a popup near the cursor with text and image previews.
   Arrows/Enter, `Ctrl+1…9`, mouse; `Shift+Enter` pastes plain text, `Ctrl+P` pin, `Del` delete, type to search.
   Keeps HTML/RTF formatting (Excel tables, bold text, links) and files copied in the file manager;
   on Linux formatting is stored but pasted as plain text. Pressing the history shortcut again pastes the
   previous item at once. Hover shows the full text; right click offers paste, plain paste, pin, delete and
   "Copy text from image" (offline OCR: Windows built-in, `tesseract` on Linux). "Pause history" in the tray stops recording for
   10 minutes, 1 hour or until restart.
   Encrypted (AES-256-GCM). Limits on count, total size, image size; excluded apps.
3. **System** – interface language and theme (system/light/dark), export/import of settings, add to autostart; keep the screen from locking (like video playback). After 3 idle minutes
   it also presses F15 (a key standard keyboards do not have) so Teams does not switch to "Away";
   it never touches the mouse and sends nothing while you work.

All hotkeys are configurable in Settings (tray icon → right click).

**Updates** – Typlo checks GitHub Releases once, 20 s after it starts (no periodic checks), and never
pops up a window. When a newer stable release exists, the tray menu shows “⬆ Update to vX.Y.Z”: it downloads
the archive for the current OS, verifies its SHA-256, swaps the files (keeping `data/`), restarts, and removes
the old files on the next start. Local `0.0.0-dev` builds do not update; `TYPLO_UPDATE_FEED` overrides the feed URL for tests.

## Platform support

| | Windows | macOS | Linux X11 | Linux Wayland |
|---|---|---|---|---|
| Global hotkeys | RegisterHotKey | libuiohook (needs Accessibility) | libuiohook | bind in desktop settings → `Typlo --history` / `--convert ru\|en\|toggle` |
| Copy/paste simulation | SendInput | CGEvent (Cmd+C/V) | XTest | – (history only puts the item on the clipboard) |
| Clipboard history | Win32 listener | NSPasteboard | `xclip` (+ `clipnotify` if present) | `wl-clipboard` |
| Key for history | DPAPI | Keychain | Secret Service (`secret-tool`), else 0600 file | same |
| Switch system layout | yes | not yet | yes (XKB, needs `setxkbmap`) | no |
| Keep screen on | SetThreadExecutionState | `caffeinate` | `gnome-session-inhibit` / `systemd-inhibit` / `xset s reset` | same |
| Autostart | HKCU Run | LaunchAgent | `~/.config/autostart` | same |
| Verified by self-test | yes | **not yet (no Mac available)** | yes (Ubuntu 24.04, Xvfb, `tests/linux`) | no |

Linux packages: `xclip xdotool` (X11) or `wl-clipboard` (Wayland); optional `clipnotify`, `libsecret-tools`.

macOS: the build is unsigned. Build/package on a Mac (`scripts/package-macos.sh` ad-hoc signs it), then
right click → Open, and allow Typlo in System Settings → Privacy & Security → Accessibility.

Data folder: `data/` next to the executable; if not writable (or inside a `.app`) the per-user app data folder.

## Build / test

    dotnet test
    sh scripts/publish-all.sh                     # dist/<rid>/ for win/linux/osx, plus Typlo.app
    Typlo --selftest report.txt              # end-to-end check against the real clipboard/hotkeys/input
    Typlo --screenshots shots                # render the windows with sample data

    # Linux end-to-end in Docker (X11 via Xvfb):
    dotnet publish src/Typlo.App -c Release -r linux-x64 --self-contained -o publish-linux
    docker build -t typlo-linux-test tests/linux
    docker run --rm -v "$PWD/publish-linux:/app" -v "$PWD/out:/out" typlo-linux-test

## Layout

CI, release versioning, verified packages and first-release instructions:
[docs/ci-release.md](docs/ci-release.md).

- `src/Typlo.Core` – layout converter, hotkey model, settings, encrypted SQLite store (no UI, no OS calls)
- `src/Typlo.App/Platform` – `IPlatform`; `Windows/`, `Mac/`, `Linux/`; `Shared/HookPlatformBase` (SharpHook)
- `src/Typlo.App/Views` – tray, history popup, settings
- `src/Typlo.App/Services` – controller, IPC (`--history`, `--convert`), self-test
