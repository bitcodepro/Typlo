using System.Diagnostics;
using System.Net.Sockets;

namespace Typlo.App.Services;

internal enum RuntimeTransportPhase { Create, Accept, RequestHeader, RequestBody, RequestJson, Handler, ReplyHeader, ReplyBody, ReplyFlush, RequestFlush, ReplyJson, Dispose }
internal enum RuntimeTransportIssue { Failure, ListenerGap, Accepted }

/// <summary>Only transport metadata. Never include frame bytes, arbitrary commands or exception messages.</summary>
internal sealed record RuntimeTransportDiagnostic(RuntimeTransportIssue Issue, bool Server, long Channel, long Connection,
    long Trace, RuntimeTransportPhase Phase, string Command, int CompletedBytes, int ExpectedBytes,
    long ElapsedMilliseconds, string? ExceptionType, int? HResult, SocketError? SocketError, bool Stopping, bool TimedOut,
    int ServerInstances, int PeerProcessId = 0)
{
    internal string Summary => $"IPC {Issue}: side={(Server ? "server" : "client")} pid={Environment.ProcessId} channel={Channel} " +
        $"connection={Connection} trace={Trace} phase={Phase} command={Command} bytes={CompletedBytes}/{ExpectedBytes} " +
        $"elapsed={ElapsedMilliseconds}ms exception={ExceptionType ?? "none"} hresult={HResult?.ToString("X8") ?? "none"} " +
        $"socket={SocketError?.ToString() ?? "none"} stopping={Stopping} timeout={TimedOut} instances={ServerInstances} peer={PeerProcessId}";
}

internal sealed class RuntimeTransportProgress(bool server, long channel = 0, long connection = 0)
{
    private readonly long _started = Stopwatch.GetTimestamp();
    internal long Trace;
    internal RuntimeTransportPhase Phase;
    internal string Command = "unknown";
    internal int CompletedBytes, ExpectedBytes;
    internal int PeerProcessId;
    internal bool ServerSide() => server;

    internal void Begin(RuntimeTransportPhase phase, int bytes = 0)
        => (Phase, CompletedBytes, ExpectedBytes) = (phase, 0, bytes);

    internal void SetCommand(string command) => Command = command switch
    {
        "state" or "settings" or "paste" or "capture-begin" or "capture-end" or "image-write-begin" or
        "image-write-end" or "ui" or "probe-hotkey" or "clear-probes" or "image-fixture" or
        "history-target-fixture" or "update-fixture" or "register-hotkeys" or "suspend-hotkeys" or "refresh-layouts" or
        "auto-status" or "dictionary-check" or "personal-query" or "personal-edit" or "personal-add" or "personal-import" or "personal-export" or "personal-word" or "excluded-word" => command,
        _ => "unknown",
    };

    internal void Report(Action<RuntimeTransportDiagnostic>? diagnostic, RuntimeTransportIssue issue,
        Exception? error, bool stopping, bool timedOut, int instances)
    {
        SocketError? socket = null;
        for (var inner = error; inner is not null; inner = inner.InnerException)
            if (inner is SocketException se) { socket = se.SocketErrorCode; break; }
        var entry = new RuntimeTransportDiagnostic(issue, server, channel, connection, Trace, Phase, Command,
            CompletedBytes, ExpectedBytes, (long)Stopwatch.GetElapsedTime(_started).TotalMilliseconds,
            error?.GetType().FullName, error?.HResult, socket, stopping, timedOut, instances, PeerProcessId);
        // A diagnostic sink must never change whether a command is delivered or retried.
        try
        {
            if (diagnostic is not null) diagnostic(entry);
            else Log.Info(entry.Summary);
        }
        catch { }
    }
}
