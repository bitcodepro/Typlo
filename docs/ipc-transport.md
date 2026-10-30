# IPC listener lifetime and diagnostics

Fast sequential requests must not close the last server instance between connections. On Unix,
.NET shares one listening socket across the `NamedPipeServerStream` objects for a name. Disposing
the final instance unlinks and closes that socket, including a next client already queued there.
See the [.NET Unix implementation](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.IO.Pipes/src/System/IO/Pipes/NamedPipeServerStream.Unix.cs).

The listener now creates its successor before invoking the accepted connection's handler. This
matters even without concurrency: a buffered request, `Task.FromResult` handler and small reply can
all complete synchronously before the old accept loop returns to create its next instance.

Capacity remains bounded to four actual server streams, including the pending listener. A busy
loop can wait for a slot while holding an accepted stream; other running handlers release capacity
independently. Each stream keeps its permit through `DisposeAsync`, preserving the Windows instance
limit fix. No delay, `Task.Yield`, automatic retry or unlimited instance count is used as a workaround.

## Evidence and limits

The diagnostic/reproduction commit `c1d54f6` retains the previous accept algorithm. In
[its macOS CI run](https://github.com/bitcodepro/Typlo/actions/runs/40750526244/job/123035689875),
both 1,000-request synchronous sequential tests and the queued-next-client Unix test failed with
`EndOfStreamException`. The queued client test also failed with `Connection reset by peer` on
[Ubuntu](https://github.com/bitcodepro/Typlo/actions/runs/40750526244/job/123035689894).
Windows recorded `ListenerGap` events with zero server instances between requests.

The Unix test buffers the first request before serving it, then synchronously queues a second
client from the first handler. It tests an already connected client, rather than relying on a race
to reconnect after the endpoint has disappeared. The ordinary sequential tests retain a fully
synchronous handler; existing screenshot assertions and concurrent burst tests are unchanged.

The original post-merge failure in #21 had no server-side transport diagnostics, so its individual
cause cannot be established retroactively. This change fixes an independently reproduced mechanism
for the same reply-header EOF and adds evidence if another cause remains.

## Failure records

Server and client failures record the side, process/channel/connection IDs, numeric request trace,
phase, completed/expected byte counts, elapsed time, exception type, HRESULT, inner socket error,
timeout/shutdown state and live server-instance count. Only known command names are included.
Payloads, arbitrary commands, exception messages and JSON contents are excluded. Normal replies
are not written to the application log. Tests can observe accepted-connection metadata separately.

Read counts include partial headers/bodies. Write counts describe successfully completed writes;
a failed write reporting zero does **not** prove no bytes reached the peer. Unknown completion must
not trigger retries of commands with side effects. Optional numeric traces preserve framing and
allow older requests without a trace to be read.

In the app, records go to `typlo.log` / `typlo-ui.log`. Test fixtures include records in xUnit/TRX
output; CI retains the nested Windows runtime logs as well as test reports. Logging failures do not
change command delivery. Payload size limits and the existing request/response timeouts remain.

## Windows UI-close regression coverage

Investigating the Windows history-target timeout reproduced a stalled settings `Closed` handler
in Native AOT. The UI was synchronously waiting on resident hotkey restoration, so it could not
answer another UI request or finish dismissal. Settings closure now awaits restoration
asynchronously and holds a `UiOperations` lease until the reply/state update finishes. The UI
dispatcher stays responsive and the idle timer cannot tear the process down mid-operation.

The Windows integration harness also supports `--ui-target-check <app> <results> <cycles>`.
Its first cycle holds a diagnostic-only barrier before resident dispatch of `register-hotkeys`.
Dismissal and a dispatcher query must complete before that barrier is released; the process must
outlive the existing one-second idle interval. It then reopens Settings before releasing the old
restore, verifies live hotkeys remain suspended, and closes the new window to restore hotkeys/exit.
Each settings window increments a session number carried by suspend/restore requests; the resident
ignores restoration from an older session under the same lock used for UI ownership and suspension.
This check fails on the old synchronous handler, without relying on a random timeout. CI runs it
with ten repeated editor → Settings → history → close cycles on the Native AOT executable.
The reopen assertion also fails on `9d8959d`, which has asynchronous closure but no session guard.

The foreground editor fixture explicitly grants foreground permission to the spawned UI, modelling
a user action while retaining the actual foreground/target assertions. Test transport logs are
buffered until completion to avoid file I/O changing request timing. On Windows, explicit diagnostic
clients also record the server PID for the connected pipe; UI lifecycle records contain only fixed
action labels, thread IDs and traces. No payloads or window titles are recorded.

The original single CI timeout cannot be attributed retroactively beyond its available logs. These
checks verify the independently reproduced blocking path rather than treating a green retry as proof.

A later local Native AOT repetition recorded a different failure: after the harness observed
the previous UI process exit and the resident reported a new UI PID, a client connected to a
pipe whose reported server PID still belonged to the previous UI. Writing the request header
then failed with `ERROR_BROKEN_PIPE`. The logs establish the stale endpoint, not the precise
Windows handle-retention mechanism. Each UI generation now receives a fresh pipe name;
the resident publishes that name alongside the corresponding PID in its state reply. Production
commands and the harness use that generation's name. The repeated UI test rejects name reuse
across processes. No command is retried as a workaround for an uncertain write.

The runtime harness binds its client from one current-state reply after every UI launch:
Settings, screenshot capture and both OCR/history lifetimes. This keeps channel uniqueness
checks while ensuring capture/OCR commands never use the previously closed Settings channel.
