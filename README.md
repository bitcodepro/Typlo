# Typlo

Tray utility for Windows, macOS and Linux (Avalonia / .NET 10):

1. **Layout fix** – select text typed in the wrong layout and press a hotkey: `ghbdtn` → `привет`.
   - `Ctrl+Alt+Shift+Space` – next layout in your cycle: choose two, three, four or more languages and set their order.
   - Layouts and physical key maps come from the enabled input sources in Windows, macOS or XKB.
     On first setup all convertible layouts join the cycle; the first three input sources get
     `Ctrl+Alt+Shift+1/2/3`. Other direct shortcuts remain empty. Existing shortcuts and cycle order are preserved.
     Settings → layout cycle lets you enable/reorder sources and refresh after an OS input-settings change.
     Input methods without a direct character map are shown as switch-only sources.
   - **Layouts for applications** lets you pick a running process or enter its name, then select
     and reorder its own Typlo cycle. A rule with one layout activates it when you enter the app;
     it does not change existing text or continuously override a later manual layout choice.
     Manual conversion shortcuts take precedence over a pending automatic activation.
     Other apps use the global cycle, and direct language shortcuts keep their assigned targets.
     Rules match exact process names (case-insensitive, optional `.exe`); all windows of that process
     share the rule. They are included in settings import/export. If any selected layout is missing,
     the rule stays saved and the global cycle is used until all its layouts are available.
     Windows uses foreground events. macOS and X11 check focus twice a second only when an automatic
     single-layout rule is enabled; Wayland retains its existing foreground/input restrictions.
   - Nothing selected? Only the recently typed input is converted (can be turned off).
     Windows first tries direct replacement in supported native Edit and single-line plain-text RichEdit fields.
     It binds the exact control and verifies its text and caret before writing, so a notification can take focus
     without redirecting the operation. This path does not use the clipboard or simulated selection keystrokes.
     Unsupported fields use the guarded copy/paste path below; a stale or uncertain direct write never retries
     through the clipboard. Teams/WebView2 and rich-text editors currently use that fallback.
     See [direct editing](docs/direct-text-edit.md) for adapter limits and verification.
     Windows also recognizes clipboard copies from native child windows hosted by another process,
     such as WebView2 editors in Teams. Copies from unrelated processes still stop the replacement.
     A transient Windows activation pauses input for up to 750 ms instead of immediately cancelling.
     After focus returns, an interrupted selection continues only from a verified nonempty suffix.
     Typing, clicking, an external copy or an unverified/collapsed selection stops the operation;
     Typlo never activates the original window to take focus away from the user.
     Windows also records queued foreground events: a complete away/return cycle during Copy's
     clipboard-settle wait invalidates the copied range, even if polling only sees the original window.
     Typlo makes a new verification copy; a second interruption aborts instead of inserting at a collapsed caret.
     The verified focus version is checked again immediately before Paste, and an aborted verification never
     falls through to layout-only switching. Backends without a foreground-event counter use an independent
     verification copy before replacement and retain their existing strict foreground checks.
     Typing a single punctuation character beside an existing word changes only that character.
     Typing a whole wrong-layout word still includes its punctuation: `htdm.dftim` → `ревьюваешь`.
     Cursor movement, clicks, other shortcuts, window/layout changes and a 2-second pause between keystrokes start a new input run.
     To convert older text, select it explicitly.
   - The clipboard is restored afterwards; the system layout is switched too (Windows, Linux X11).
     Hotkey modifiers get an 80 ms release grace period; Windows selects recent input in one input batch.
     `data/typlo.log` records operation phase timings without text or keystrokes to help diagnose slow editors.
   - `Ctrl+Alt+Shift+C` cycles the case (lower → Title → UPPER); `Ctrl+Alt+Shift+T` transliterates (Привет ⇄ Privet).
     Consecutive text-tool commands reuse the last replaced range without reselection, verifying its contents each time.
     New typing, caret movement and other input invalidate that range; a newly typed symbol is handled on its own.
   - Snippets: typing a trigger such as `;addr` replaces it with saved text, in any configured keyboard layout.
     The multiline editor preserves line breaks, tabs, blank lines and indentation. Insert one `{cursor}` marker
     with the button to choose where typing continues; the preview shows that position as `│`.
     The marker disappears on expansion. `{{cursor}}` inserts the literal text `{cursor}`.
     Cursor placement is verified against the pasted suffix and stops if you type, move the caret or change windows.
   - `Ctrl+Alt+Shift+Z` undoes the last layout fix, case change or transliteration (within 2 minutes, same window).
2. **Clipboard history** – `Ctrl+Shift+V` opens a popup near the cursor with text and image previews.
   Arrows/Enter, `Ctrl+1…9`, mouse; `Shift+Enter` pastes plain text, `Ctrl+P` pin, `Del` delete, type to search.
   Keeps HTML/RTF formatting (Excel tables, bold text, links) and files copied in the file manager;
   on Linux formatting is stored but pasted as plain text. Pressing the history shortcut again pastes the
   previous item at once. Search covers the full text of every item; `Ctrl+O` opens an item in full. Hover shows the start of the text; right click offers paste, plain paste, pin, delete and
   "Copy text from image" (offline OCR: Windows built-in, `tesseract` on Linux). "Pause history" in the tray stops recording for
   10 minutes, 1 hour or until restart.
   Encrypted (AES-256-GCM). Limits on count, total size, image size; excluded apps.
3. **Automatic layout correction and application rules** – enabled in Settings:
   - After Space and a short pause, Typlo checks the just-typed word against offline English,
     Russian, Ukrainian, French, Spanish, Italian, German and Portuguese dictionaries, including
     Russian/Ukrainian IT forms such as `ревьюваешь`, `коммитить`, `пушить` and `комітити`.
     Only confident corrections are applied; unknown/ambiguous words, numbers, identifiers and
     addresses are left alone. Disable “Correct keyboard layout after Space” to turn it off.
   - The dictionary is shipped in `language-data/` beside the executable (inside the macOS bundle).
     Extract the **whole update**, not just the EXE. It opens lazily, uses a 128 KiB SQLite page-cache
     target with no mmap or word-list preload, and never sends text over the network.
     The vocabulary is CC BY-SA 4.0; its attribution and license ship beside the data.
   - Application rules also restrict automatic correction candidates. Teams with EN/UK cycles only
     EN/UK; PowerShell with EN activates EN on entry and its Typlo cycle stays on EN. An empty list
     (e.g. for a browser) uses the global cycle. These rules govern **Typlo's cycle**, not Win+Space
     or Alt+Shift. Explicit per-language shortcuts retain their assigned targets.
   - Rules match the process owning the window. For PowerShell hosted inside Windows Terminal,
     choose **WindowsTerminal**, whose window title is shown in the process picker.
   - If you keep typing, automatic correction cancels. Its copy/read stage collapses the selection
     before waiting for clipboard data, so further typing appends instead of replacing the word.
     Manual conversion remains available for every convertible layout and does not require a
     dictionary match. Session/input/clipboard checks still apply; unresponsive editors can be skipped.
4. **Screenshots (Windows)** – tray → Screenshots or Settings → Screenshots:
   - `Ctrl+Alt+Shift+S`: capture to the clipboard; `Ctrl+Alt+Shift+F`: capture for saving.
     Both shortcuts are configurable and can be disabled. Choose the default rectangle/freehand shape in Settings;
     `R` / `F` changes shape in the overlay before drawing.
   - The desktop freezes before the selection overlay opens. Drag with the crosshair, then release to capture.
     `Esc` or right click cancels without changing the clipboard. A tiny or empty area lets you try again.
   - Freehand contours close automatically; pixels outside the contour are transparent in the PNG.
     The editor shows pixel dimensions and offers a pen, translucent marker, arrow and frame,
     eight colors and adjustable line width. Undo/redo (`Ctrl+Z` / `Ctrl+Y`) and Clear drawings
     preserve the original screenshot; clearing can also be undone. `Esc` cancels the current stroke or closes the editor.
     Copy (`Ctrl+C`) exports the current drawing as PNG; Save JPEG (`Ctrl+S`) opens Save As.
     The clipboard shortcut copies the original immediately; after editing, Copy updates the clipboard.
     Disable “Open editor after copying a screenshot” for a quick capture without opening a window.
     The save shortcut opens the editor before Save As, so annotations can be added before saving.
     Save As produces an actual JPEG at quality 95 and the original pixel dimensions, without resizing.
     JPEG has no transparency, so the area outside a freehand contour is white in saved files.
     The Save shortcut does not modify the clipboard or add the image to history, even if the file dialog is cancelled.
     Paste the image into another app with `Ctrl+V`. Screenshots join history only when recording is enabled and unpaused.
   - Monitors use their own DPI scale, including monitors left/above the primary display. No image is uploaded;
     the full desktop frame stays in memory for the selection. Only the selected image is copied/saved.
   - Command-line launch: `Typlo --capture rectangle` / `Typlo --capture freehand`; add `--save` to open the editor for JPEG saving.
     Capture is currently Windows-only; the controls explain this on macOS/Linux.
5. **System** – interface language (English, Русский, Українська, Deutsch, Español, Português, Français, Italiano, 中文, 日本語) and theme (system/light/dark), export/import of settings, add to autostart; keep the screen from locking (like video playback). After 3 idle minutes
   it also presses F15 (a key standard keyboards do not have) so Teams does not switch to "Away";
   it never touches the mouse and sends nothing while you work.

All hotkeys are configurable in Settings (tray icon → right click); "Keyboard shortcuts…" in the tray lists every shortcut.
With nothing selected, the text tools (layout, case, transliteration) work only on the tracked recent input.

**Updates** – Typlo checks GitHub Releases once, 20 s after it starts (no periodic checks), and never
pops up a window. When a newer stable release exists, the tray menu shows “⬆ Update to vX.Y.Z”: it downloads
the archive for the current OS, verifies its SHA-256, swaps the files (keeping `data/`), restarts, and removes
the old files on the next start. "Check for updates…" (tray menu or Settings) checks right away and asks "Update now / Later". Local `0.0.0-dev` builds do not update; `TYPLO_UPDATE_FEED` overrides the feed URL for tests.

## Memory and background operation

Windows releases use Native AOT. The resident process handles input, layout conversion and the native
tray without initializing Avalonia. Settings, clipboard history and screenshot windows run in one
on-demand UI child; it exits one second after the last window is hidden or closed. Text replacement
and Undo always execute in the resident, including requests made by the UI. A UI crash restores
shortcuts and releases any screenshot/clipboard lease; stale requests cannot affect a newer UI.
The automatic update check uses a short-lived helper, so HTTP/TLS resources are released afterwards.
macOS and Linux retain their existing Avalonia lifetime and platform integration.

History payloads remain AES-GCM encrypted in SQLite on disk. Listing history loads metadata without
image thumbnails. Virtualized rows load a thumbnail only while realized and dispose it when recycled;
hiding history clears its items, and closing an image viewer releases its bitmap. File entries store
paths, not a second in-memory copy of the files. The previous-item shortcut queries one ID directly.
Image bursts are bounded to one active encoding job and one newest pending image; superseded pending
images are skipped. Capture timestamps preserve ordering relative to text copied during encoding.
After large image/clipboard buffers become unreachable, a one-shot idle collection returns unused
managed-heap regions. It waits for input/conversion/image activity to stop and is throttled to at most
one collection per 15 seconds; working-set trimming is never used. Active images and screenshots can
still temporarily require much more memory than the idle resident.

The Windows portable distribution contains `Typlo.exe` and five required native DLLs. Keep these files
together when installing manually; the updater handles the complete package. Building Windows Native
AOT requires Windows and the Visual Studio C++ build tools. `publish-all.sh` uses a Windows JIT fallback
when cross-publishing from other operating systems; CI builds the Windows Native AOT release.

## Platform support

| | Windows | macOS | Linux X11 | Linux Wayland |
|---|---|---|---|---|
| Global hotkeys | RegisterHotKey | libuiohook (needs Accessibility) | libuiohook | bind in desktop settings → `Typlo --history` / `--convert ru\|en\|toggle` |
| Copy/paste simulation | SendInput | CGEvent (Cmd+C/V) | XTest | – (history only puts the item on the clipboard) |
| Screenshots: rectangle / freehand | yes | – | – | – |
| Clipboard history | Win32 listener | NSPasteboard | `xclip` (+ `clipnotify` if present) | `wl-clipboard` |
| Key for history | DPAPI | Keychain | Secret Service (`secret-tool`), else 0600 file | same |
| Switch system layout | yes | yes (TIS) | yes (XKB; GNOME needs the bridge below) | no |
| Keep screen on | SetThreadExecutionState | `caffeinate` | `gnome-session-inhibit` / `systemd-inhibit` / `xset s reset` | same |
| Autostart | HKCU Run | LaunchAgent | `~/.config/autostart` | same |
| Verified by self-test | yes | **not yet (no Mac available)** | yes (Ubuntu 24.04, Xvfb, `tests/linux`) | no |

Linux packages: `xclip xdotool libxres1 libxkbcommon0` (X11) or `wl-clipboard` (Wayland); optional `clipnotify`, `libsecret-tools`.

GNOME 45/46: Typlo reads the full configured source list on X11 and Wayland, even when only some
sources are loaded into XKB. For system-layout switching on **GNOME X11**, install the included
Shell bridge: `sh gnome-extension/install.sh` from the unpacked Linux build
(from a source checkout: `sh integrations/gnome/install.sh`). A first installation may require
logging out/in, then `gnome-extensions enable typlo-input-sources@bitcodepro`. The settings window
shows a notice while the bridge is unavailable; text conversion still works. The bridge lets Shell
load any configured source, including the fifth and later ones, and keeps its indicator and per-window
state in sync. It exposes only source lookup/activation, refuses activation on the lock screen and
does not require unsafe `Eval` or rewrite your configured sources. Remove it using
`gnome-extensions uninstall typlo-input-sources@bitcodepro`. Other GNOME versions need a compatible
bridge version. The five-source GNOME 46/X11 regression runs in `tests/gnome` and CI; GNOME 45
has not been exercised natively. Wayland input limitations remain.

macOS: the build is unsigned. Build/package on a Mac (`scripts/package-macos.sh` ad-hoc signs it), then
right click → Open, and allow Typlo in System Settings → Privacy & Security → Accessibility.

Data folder: `data/` next to the executable; if not writable (or inside a `.app`) the per-user app data folder.

## Build / test

    dotnet test
    sh scripts/publish-all.sh                     # dist/<rid>/ for win/linux/osx, plus Typlo.app
    Typlo --selftest report.txt              # end-to-end check against the real clipboard/hotkeys/input
    Typlo --screenshots shots                # render the windows with sample data

    # Windows: real WebView2 contenteditable, recent input, formatting, Undo and clipboard races:
    pwsh -File scripts/test-windows-webview.ps1

The WebView2 check uses a local HTML editor and its own temporary settings/profile. It does not
sign in to Teams or send messages. It requires the installed WebView2 Runtime; CI may install it
with the explicit `-InstallRuntimeIfMissing` switch. WebView2 is a test dependency only.

```sh
    # Linux end-to-end in Docker (X11 via Xvfb):
    dotnet publish src/Typlo.App -c Release -r linux-x64 --self-contained -o publish-linux
    docker build -t typlo-linux-test tests/linux
    docker run --rm -v "$PWD/publish-linux:/app" -v "$PWD/out:/out" typlo-linux-test
```

## Layout

CI, release versioning, verified packages and first-release instructions:
[docs/ci-release.md](docs/ci-release.md).

- `src/Typlo.Core` – layout converter, hotkey model, settings, encrypted SQLite store (no UI, no OS calls)
- `src/Typlo.App/Platform` – `IPlatform`; `Windows/`, `Mac/`, `Linux/`; `Shared/HookPlatformBase` (SharpHook)
- `src/Typlo.App/Views` – tray, history popup, settings
- `src/Typlo.App/Services` – controller, IPC (`--history`, `--convert`), self-test
