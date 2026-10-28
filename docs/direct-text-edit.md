# Direct text editing

Layout conversion, case conversion and transliteration ask `IPlatform.CaptureDirectTextAsync` for a
bound control before copying anything. A `DirectTextSnapshot` contains the root window, field,
bounded document text and selection offsets. Recent physical input also carries field identity,
so two fields in one window cannot share a typing run. A retained run or verified last replacement
can identify the editor when a notification has already become foreground, provided the platform
still recognizes it as a live eligible target. Unsupported or closed prior fields cannot redirect
a command away from the current window's explicit selection. Actual user input
invalidates it. There is no search for a caret in unrelated background windows.

## Capability and commit results

- `Unsupported`: the provider has not changed selection or text; the controller may use the existing
  guarded keyboard/clipboard path against the original window.
- `Cancelled`: stale contents, changed selection, user input, shutdown or failed capture. Stop.
- `Uncertain`: a mutating message was sent, but completion could not be confirmed. Stop, discard the
  retained range, do not paste again, do not switch the system layout and do not offer an unsafe Undo.
- `Applied`: the provider confirmed the entire expected document and collapsed caret. Only then
  retain the range for another conversion or Undo and optionally switch that window's input layout.

Every write rechecks the bound control's identity, full document, selection and input generation.
Repeat commands and Undo validate the retained post-edit document, not just a matching word.
An unchanged result is revalidated without changing the control's selection.

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

## Verification

Controller tests cover target binding, suffix-only conversion, repeat/Undo, focus loss, external
clipboard changes, stale content/caret, real input, shutdown and uncertain completion. The separate
Windows fixture process exercises real native controls, cross-process Unicode/multiline offsets,
native undo, focus loss during conversion, capability declines and a deliberately delayed write.
`scripts/test-windows-webview.ps1` runs these checks alongside the existing WebView2 fallback,
hook-lifecycle, Native AOT build/runtime and desktop checks. This is not a reproduction in real Teams.
