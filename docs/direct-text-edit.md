# Direct text editing

Layout conversion, case conversion and transliteration qualify a native field before asking
`IPlatform.CaptureDirectTextAsync` for a bound control. A `DirectTextSnapshot` contains the root window,
field, bounded document text and selection offsets. Recent physical input carries field identity
only for eligible native controls, so two supported fields in one window cannot share a typing run.
Opaque browser/WebView focus HWNDs are not field identities: changes to those proxies must not
split or discard the window-scoped typing run used by the keyboard/clipboard strategy.
A retained native run or verified last replacement
can identify the editor when a notification has already become foreground, provided the platform
still recognizes it as a live eligible target. Unsupported or closed prior fields cannot redirect
a command away from the current window's explicit selection. Actual user input
invalidates it. There is no search for a caret in unrelated background windows.

## Capability and commit results

- `Unsupported`: the provider has not changed selection or text; the controller may use the existing
  guarded keyboard/clipboard path against the original window and, for a pinned native target,
  the original field. A programmatic focus change inside that window cancels fallback.
- `Cancelled`: stale contents, changed selection, user input, shutdown or failed capture. Stop.
- `Uncertain`: a mutating message was sent, but completion could not be confirmed. Stop, discard the
  retained range, do not paste again, do not switch the system layout and do not offer an unsafe Undo.
- `Applied`: the provider confirmed the entire expected document and collapsed caret. Only then
  retain the range for another conversion or Undo and optionally switch that window's input layout.

Every write rechecks the bound control's identity, full document, selection and input generation.
Repeat commands and Undo validate the retained post-edit document, not just a matching word.
An unchanged result is revalidated without changing the control's selection.

`AppController.Direct` owns native operations; `AppController.Selection` owns clipboard capture,
selection verification, replacement and clipboard restoration. A command holds one shared operation
gate and enters the selection strategy only after a pre-mutation capability decline. Cancellation
or uncertain completion of a native operation cannot also send a clipboard paste.

## Windows adapter

The first adapter supports Unicode standard `Edit` controls, including WinForms native subclasses,
and recognized RichEdit classes only in `TM_PLAINTEXT` mode with single-line contents. It excludes
Typlo's own processes, password/read-only/disabled controls, unknown classes and documents exceeding
32,760 UTF-16 units. Unsupported controls remain on the existing path. Multiline standard Edit is
supported; multiline RichEdit is declined because its internal CR positions can differ from
`WM_GETTEXT` CRLF positions. Rich content is declined rather than flattened.

The adapter pins root/child HWNDs, process and thread IDs, and the native class. It uses
`WM_GETTEXTLENGTH`, `WM_GETTEXT`, `EM_GETSEL`, `EM_SETSEL` and `EM_REPLACESEL` with native undo enabled.
Pointer-bearing messages are below `WM_USER`, so Windows performs cross-process marshalling.
`EM_SETSEL` sets the range directly; it does not activate the window or walk the caret with arrow
keys. Selection can briefly change internally before a cancelled/uncertain write; no speculative
restore is attempted after an interruption.

Calls run on demand on the thread pool, with a 40 ms timeout per native message and a 250 ms budget
per capture/refresh/replace operation. Same-process fields are excluded because synchronous calls
in a shared GUI queue can ignore message timeouts. Captures are bounded, never persisted or logged;
only one last document snapshot is retained, and genuine input clears it. No UI Automation service,
COM dependency, accessibility polling or Avalonia initialization is added to the resident process.

A third-party editor does not expose an atomic compare-and-replace transaction. The adapter
revalidates immediately before and after writing, but cannot prevent arbitrary concurrent changes
inside that editor. Unknown completion is terminal, including a write that completes after timeout.

Teams/WebView2, browsers, rich-text controls, macOS and Linux currently use the existing guarded
fallback. UI Automation TextPattern alone can read ranges but cannot replace one; whole-field
ValuePattern replacement would risk formatting and editor undo semantics and is not used here.
Additional platform/editor adapters can implement the same explicit result contract.

For Windows Teams, verified single-line replacements up to 4096 UTF-16 units use Unicode
SendInput instead of Ctrl+V. Automatic correction selects the tracked range and types the
replacement in one input batch; manual commands replace the independently verified selection.
The source/result verification accepts exactly one copied paragraph terminator only when
the rest matches the known input or retained result. Explicit multiline selections keep their
real line breaks and use clipboard paste. No clipboard retry follows an uncertain Unicode write.

Teams must finish exporting Copy before its selection is collapsed: moving the caret in the
same input batch can produce a partial or stale word. A lazily installed native keyboard hook
protects only the automatically selected range. Continuing physical input cancels the edit,
collapses that range, and replays the intercepted events in order, including modifier/key-up
events. Private input-stream barriers acknowledge cleanup before another operation starts;
these markers never reach an editor. Other editors keep the existing copy/collapse batch.
Reading and switching an input source resolves the focused GUI thread within the original
window: a WebView2 renderer can have a different HKL from its host.

## Verification

Regression tests reproduce loss of an entire recent word, or conversion of only its final letter,
when an unsupported editor's focus proxy changes. They also cover repeated RU/UK/EN conversions,
no native capture attempts for opaque controls, and rejection of native fallback into another field.
The WebView2 fixture exercises nine registered RU/UK/EN hotkey conversions of a newly typed suffix
beside existing formatted text, clipboard restoration and Undo. It does not sign into real Teams.

Controller tests cover target binding, suffix-only conversion, repeat/Undo, focus loss, external
clipboard changes, stale content/caret, real input, shutdown and uncertain completion. The separate
Windows fixture process exercises real native controls, cross-process Unicode/multiline offsets,
native undo, focus loss during conversion, capability declines and deliberately delayed reads/writes.
`scripts/test-windows-webview.ps1` runs these checks alongside the existing WebView2 fallback,
hook-lifecycle, Native AOT build/runtime and desktop checks. This is not a reproduction in real Teams.

The plain-text RichEdit fixture sets and verifies `EM_SETTEXTMODE` on every new HWND, including
recreation, while preserving native text and selection. WinForms recreates RichTextBox in rich
mode; configuring it only once in `Shown` leaves later handles unsupported. A forced-recreation
regression fails the old fixture (`EM_GETTEXTMODE=42`, `Unsupported`) and passes the corrected
fixture (`EM_GETTEXTMODE=41`, `Ready`), including a subsequent replacement. This is a verified
fixture failure path, not proof of the original post-merge CI failure's cause: that run omitted
both actual capture status and mode/read timing.

CI retains `direct-edit-native.log` alongside the assertion report. It records mode-set delivery,
message result/readback, handle changes, actual capture status, and native message IDs, results,
elapsed time and Win32 error codes. Read failures are distinguished from unsupported mode/identity
and invalidated snapshots. A separate delayed `WM_GETTEXT` test requires `Cancelled`, the matching
failed native read diagnostic and unchanged text/write count. The 40 ms message and 250 ms aggregate
budgets, conservative capability assertions and no-retry behavior remain unchanged.

Native diagnostics use an explicit internal test sink, buffered until completion; normal application
capture does not enable it. No document contents, window titles or pointer parameters are logged.
