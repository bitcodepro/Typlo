# Typlo2.0 migration status

The baseline is Typlo C# v0.5.14 at commit `a0b9a89269cc61f89ac86144e8a497decd634d4d`. The review covered the reference guide, Avalonia windows, clipboard handling, platform adapters, and settings. [Original reference guide](https://github.com/bitcodepro/Typlo/blob/a0b9a89269cc61f89ac86144e8a497decd634d4d/docs/reference.md)

| Area | Status |
| --- | --- |
| Windows clipboard | History accepts plain and formatted text, files, and images. It respects pauses, exclusions, limits, pins, and sort preferences. PNGs retain their original pixels. DIBs are converted to PNG before applying the storage limit. Thumbnails are encrypted separately. The original is loaded for full-size viewing, pasting, or OCR. Windows checks covered capture from the Typlo editor and Paint, thumbnails, full-size viewing, and pasting into Paint. |
| Linux and macOS clipboard | History includes thumbnails, full text and file views, full-content search, item menus, and shortcuts. Settings include history size and clearing while keeping pinned items. Linux OCR uses the installed Tesseract executable. Linux/X11 checks covered image storage, encryption, thumbnails, and pasting the previous item into GTK. OCR still needs a live Tesseract test. |
| Layouts, autocorrection, and snippets | Supports system layouts, cycle order, per-process rules, dictionaries for eight languages, personal words and exclusions, multiline snippets with `{cursor}`, dictionary-backed choices, and Undo. Windows checks covered manual conversion and Undo in VS Code Copilot and its file editor. Linux/X11 checks covered profiles, autocorrection, multiline fields and formatting, layout choices, and Undo. Teams, fast physical typing in Copilot, and interruptions during focus changes need further live checks. |
| Spelling and tools | Supports automatic spelling fixes and a review window, Hunspell dictionaries for English, Russian, and Ukrainian, personal correction pairs, and dictionary management. Windows includes field compatibility checks, autocorrection diagnostics, and timing by phase. Linux/X11 checks covered silent correction of a selection. Reporting and field checks vary by platform. |
| Screenshots and editor | Windows supports rectangular and free-form capture, separate clipboard and JPEG actions, reopening the latest screenshot, and drawing tools. JPEGs use 100% quality, and capture displays a bright outline without dimming the screen. Multi-monitor capture still needs a live test. |
| Settings and updates | Settings follow the original order and default to English and the system theme. Settings JSON supports the C# format in both directions, and full Rust backups can import C# archives. Windows history import can read the old encrypted database in the same user's context. End-to-end GitHub updates require a published Rust package. |

## Known differences

- macOS arm64 is cross-compiled. Startup, pasting into the original window, and menu behavior still need verification on a Mac.
- The Unix history window follows the original dimensions and structure. Cursor placement, work-area handling, and menu appearance need further checks on Linux and macOS. Global shortcuts and pasting on Wayland depend on the compositor.
- The review covered the functions and scenarios above. A pixel-by-pixel comparison of all Avalonia and egui screens is still pending.

[Early benchmark results](comparison.md)

## Windows replacement checks before release

Use a blank unsaved document or draft. Keep test text in the Teams or Copilot draft. For manual conversion, use the shortcut that converts to Russian. For Undo, use the Typlo2.0 shortcut.

| Field | Checks |
|---|---|
| Notepad++ in UTF-8 | Run two autocorrections in a row. Convert the last word and a multiline selection manually, then verify Undo. After an expected refusal in ANSI mode, retry in UTF-8. |
| VS Code file editor and GitHub Copilot input | In both fields, type `ghbdtn` followed by a space. Repeat with manual conversion and Undo. Verify that the original word is replaced and the space and surrounding text remain intact. |
| Teams | Run the same commands in a blank draft. Include typing the next word quickly and switching fields immediately after issuing the command. |
| Chrome address bar and on-page search field | Verify autocorrection, manual conversion, and Undo separately in both fields before submitting a search. |

Run manual conversion immediately after pressing Space. If autocorrection has not yet inserted the result, the manual command should cancel it. When Typlo2.0 declines an edit before pasting, the next command should still work during normal typing. Use the Tools window to inspect recent attempts and P50/P95 timings for confirmed operations in each field type. The WebView2 fixture verifies ranges and spaces in a controlled field.

### Automated checks before manual testing

- `typlo-rust.exe --native-self-test` checks native fields, protected fields, and Undo.
- `typlo-rust.exe --measure-native 200` emits JSON with the confirmed replacement count and P50/P95 timings. It checks the complete text and caret, including autocorrection with ordinary and non-breaking spaces.
- `tools/WebViewProbe` exercises autocorrection, Undo, selection with a trailing space, and a second Undo in two textarea variants and a contenteditable field. It uses Typlo2.0's UI Automation ranges and reproduces space trimming inside the test field.
- `cargo test --locked --features gui --lib measure_large_clipboard_search -- --ignored --nocapture` compares paginated scans, cold searches, repeat searches, and searches after copying a new item across 1,500 encrypted entries. Timings exclude interface latency. The history window keeps a 2 MiB cache and reuses unchanged data while refreshing the order after database changes.
- `cargo test --locked --features gui --lib measure_drawing_bounds -- --ignored --nocapture` measures stroke bounds and repainting the changed region in a GDI bitmap. Test the physical gesture and scaling separately on two monitors.
- For an endurance run on temporary profiles, set `$env:TYPLO_SOAK_SECONDS = '7200'` and run `cargo test --locked --features gui --lib paired_history_and_screenshot_soak -- --ignored --nocapture`. The test uses local TLS transfers to send text, images, and files in turn, and saves and reloads screenshots. Sample the test process's working set and private bytes at regular intervals to measure memory.
