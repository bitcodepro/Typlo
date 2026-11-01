# Typlo

Fix text typed in the wrong keyboard layout (`ghbdtn` → `привет`), switch languages, keep clipboard history and take screenshots.

[Download the latest release](https://github.com/bitcodepro/Typlo/releases/latest)

## Start on Windows

1. Download the **win-x64 ZIP** and extract the entire archive, including `language-data/`.
2. Run **Typlo.exe**. Right-click its icon near the Windows clock → **Settings**.
3. Choose your installed languages and their cycle order.

## Windows shortcuts

These are the defaults. **Settings** lets you change them; **Keyboard shortcuts** in the tray shows your current bindings.

| Shortcut | Action |
|---|---|
| **Ctrl+Shift** | Switch input language. Existing text stays unchanged. |
| **Ctrl+Alt+Shift+Space** | Convert selected text to the next layout. With no selection, convert only recently typed text. |
| **Ctrl+Alt+Shift+1 / 2 / 3** | Convert to a specific language. Settings shows which language each number uses. |
| **Ctrl+Alt+Shift+C** | Cycle text case: lower → Title → UPPER. |
| **Ctrl+Alt+Shift+T** | Transliterate: `Привет` ↔ `Privet`. |
| **Ctrl+Alt+Shift+Z** | Undo the last Typlo text change. |
| **Ctrl+Alt+Shift+D** | Check the current field and open timings/memory tools. |
| **Ctrl+Alt+Shift+L** | Show/cycle dictionary spellings; Ctrl+Alt+Shift+Enter applies, Ctrl+Alt+Shift+Escape cancels. |
| **Ctrl+Shift+K** | Add the selected, correctly spelled word to your personal dictionary; confirm its language. |
| **Ctrl+Shift+V** | Open clipboard history. Enter pastes; Shift+Enter pastes plain text. |
| **Win+Shift+S** | Capture a rectangle or freehand area to the clipboard; open the drawing editor. |
| **Ctrl+Alt+Shift+F** | Capture, annotate and choose where to save a JPEG. |

While Settings is open, Typlo pauses its shortcuts so you can edit them. Ctrl+Shift then follows Windows settings.

## Languages per application

In **Settings → Layouts for applications**, choose a process and its languages:

- **Teams: English + Ukrainian** — Ctrl+Shift cycles between those two.
- **PowerShell: English only** — English activates when you enter the app.
- **Empty list** — use the global cycle.

On entry, an allowed language stays active. Otherwise Typlo chooses English if it is in the list, or the first language in the list.
For PowerShell inside Windows Terminal, choose the **WindowsTerminal** process.

## Also available

- **Automatic correction after Space**, using offline dictionaries; switch it on or off in Settings.
- **Full backup** in Tools: settings, shortcuts, profiles, snippets, personal words and exceptions in one `.typlo` file.
- **Personal words** survive updates. Save the correct spelling (`sql` + English, rather than `ыйд`). Existing words are skipped; add forms such as `писать`, `напиши`, `написал` separately.
- **Dictionaries and diagnostics** in Settings: search/edit personal words, import/export JSON, check dictionary health and see why auto-correction skipped a word.
- **Multiline snippets** with a `{cursor}` marker for the insertion point.
- **Screenshot drawing tools** and encrypted clipboard history for text, images and files.

Windows, macOS and Linux are supported; screenshots are Windows-only and Wayland has input restrictions.
See the [feature and platform reference](docs/reference.md) for details and build instructions.
