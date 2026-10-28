using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using Typlo.Core.Settings;

namespace Typlo.App.Services;

internal sealed record RuntimeRequest(string Command, long Id = 0, long Window = 0, bool Flag = false, string? Text = null, AppSettings? Settings = null, string[]? Args = null, int UiOwner = 0, long Trace = 0);
internal sealed record RuntimeReply(bool Ok, string? Error = null, string[]? FailedHotkeys = null, DateTimeOffset? PausedUntil = null, int UiProcessId = 0, long StoredItems = 0, string? AvailableVersion = null, long Window = 0);

[JsonSerializable(typeof(RuntimeRequest))]
[JsonSerializable(typeof(RuntimeReply))]
[JsonSerializable(typeof(UpdateProbeResult))]
internal partial class RuntimeJson : JsonSerializerContext;

/// <summary>Small, per-user command channel. Image/file payloads stay in the encrypted database.</summary>
internal sealed class RuntimeChannel : IDisposable
{
    internal const int MaxMessageBytes = 8 * 1024 * 1024;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _listener;
    private readonly SemaphoreSlim _clients = new(4);
    private readonly Func<RuntimeRequest, Task<RuntimeReply>> _handle;
    private readonly Action<RuntimeTransportDiagnostic>? _diagnostic;
    private static long _nextChannel;
    private readonly long _channel = Interlocked.Increment(ref _nextChannel);
    private long _nextConnection;
    private int _instances;
    internal string Name { get; }
    internal RuntimeChannel(string name, Func<RuntimeRequest, Task<RuntimeReply>> handle, Action<RuntimeTransportDiagnostic>? diagnostic = null)
    {
        Name = name; _handle = handle; _diagnostic = diagnostic;
        _listener = ListenAsync();
    }
    private async Task ListenAsync()
    {
        var progress = new RuntimeTransportProgress(true, _channel);
        NamedPipeServerStream? pending = null;
        NamedPipeServerStream? accepted = null;
        try
        {
            pending = await CreateStreamAsync().ConfigureAwait(false);
            while (!_stop.IsCancellationRequested)
            {
                progress.Begin(RuntimeTransportPhase.Accept);
                await pending.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                accepted = pending;
                pending = null;
                progress.Begin(RuntimeTransportPhase.Create);
                // On Unix, disposing the last NamedPipeServerStream unlinks/closes its shared
                // listening socket, including clients already queued there. Establish a successor
                // BEFORE a fully synchronous ServeAsync can dispose this accepted connection.
                // Both streams retain capacity permits; no fifth Windows instance is created.
                pending = await CreateStreamAsync().ConfigureAwait(false);
                _ = ServeAsync(accepted);
                accepted = null; // ServeAsync now owns this stream and its capacity permit.
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (!_stop.IsCancellationRequested) progress.Report(_diagnostic, RuntimeTransportIssue.Failure, e, false, false, Volatile.Read(ref _instances)); }
        finally
        {
            if (accepted is not null) await ReleaseStreamAsync(accepted, progress).ConfigureAwait(false);
            if (pending is not null) await ReleaseStreamAsync(pending, progress).ConfigureAwait(false);
        }
    }
    private async Task<NamedPipeServerStream> CreateStreamAsync()
    {
        await _clients.WaitAsync(_stop.Token).ConfigureAwait(false);
        try
        {
            var stream = new NamedPipeServerStream(Name, PipeDirection.InOut, 4, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            Interlocked.Increment(ref _instances);
            return stream;
        }
        catch { _clients.Release(); throw; }
    }
    private async ValueTask ReleaseStreamAsync(NamedPipeServerStream stream, RuntimeTransportProgress progress)
    {
        progress.Begin(RuntimeTransportPhase.Dispose);
        try { await stream.DisposeAsync().ConfigureAwait(false); }
        catch (Exception e)
        { if (!_stop.IsCancellationRequested) progress.Report(_diagnostic, RuntimeTransportIssue.Failure, e, false, false, Volatile.Read(ref _instances)); }
        finally
        {
            var remaining = Interlocked.Decrement(ref _instances);
            if (remaining == 0 && !_stop.IsCancellationRequested)
                progress.Report(_diagnostic, RuntimeTransportIssue.ListenerGap, null, false, false, remaining);
            // Preserve disposal-before-release: the permit covers the actual OS instance.
            _clients.Release();
        }
    }
    private async Task ServeAsync(NamedPipeServerStream stream)
    {
        var progress = new RuntimeTransportProgress(true, _channel, Interlocked.Increment(ref _nextConnection));
        progress.Begin(RuntimeTransportPhase.Accept);
        if (_diagnostic is not null)
            progress.Report(_diagnostic, RuntimeTransportIssue.Accepted, null, false, false, Volatile.Read(ref _instances));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var data = await ReadFrameAsync(stream, timeout.Token, progress).ConfigureAwait(false);
            progress.Begin(RuntimeTransportPhase.RequestJson);
            var request = JsonSerializer.Deserialize(data, RuntimeJson.Default.RuntimeRequest) ?? throw new InvalidDataException("Missing command");
            progress.Trace = request.Trace;
            progress.SetCommand(request.Command);
            progress.Begin(RuntimeTransportPhase.Handler);
            RuntimeReply reply;
            try { reply = await _handle(request).ConfigureAwait(false); }
            catch (Exception e)
            {
                progress.Report(_diagnostic, RuntimeTransportIssue.Failure, e, _stop.IsCancellationRequested, timeout.IsCancellationRequested, Volatile.Read(ref _instances));
                reply = new(false, "Operation failed");
            }
            await WriteFrameAsync(stream, JsonSerializer.SerializeToUtf8Bytes(reply, RuntimeJson.Default.RuntimeReply), timeout.Token, progress).ConfigureAwait(false);
        }
        catch (Exception e) { if (!_stop.IsCancellationRequested) progress.Report(_diagnostic, RuntimeTransportIssue.Failure, e, false, timeout.IsCancellationRequested, Volatile.Read(ref _instances)); }
        finally { await ReleaseStreamAsync(stream, progress).ConfigureAwait(false); }
    }
    internal static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken token, RuntimeTransportProgress? progress = null)
    {
        progress?.Begin(progress.ServerSide() ? RuntimeTransportPhase.RequestHeader : RuntimeTransportPhase.ReplyHeader, 4);
        var header = new byte[4]; await ReadPartAsync(stream, header, token, progress).ConfigureAwait(false);
        var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > MaxMessageBytes) throw new InvalidDataException("Invalid runtime message size");
        progress?.Begin(progress.ServerSide() ? RuntimeTransportPhase.RequestBody : RuntimeTransportPhase.ReplyBody, length);
        var data = new byte[length]; await ReadPartAsync(stream, data, token, progress).ConfigureAwait(false); return data;
    }
    private static async Task ReadPartAsync(Stream stream, Memory<byte> buffer, CancellationToken token, RuntimeTransportProgress? progress)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[read..], token).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("Incomplete IPC frame");
            read += count;
            if (progress is not null) progress.CompletedBytes = read;
        }
    }
    internal static async Task WriteFrameAsync(Stream stream, byte[] data, CancellationToken token, RuntimeTransportProgress? progress = null)
    {
        if (data.Length is < 1 or > MaxMessageBytes) throw new InvalidDataException("Runtime message too large");
        var header = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
        progress?.Begin(progress.ServerSide() ? RuntimeTransportPhase.ReplyHeader : RuntimeTransportPhase.RequestHeader, 4);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        if (progress is not null) progress.CompletedBytes = 4;
        progress?.Begin(progress.ServerSide() ? RuntimeTransportPhase.ReplyBody : RuntimeTransportPhase.RequestBody, data.Length);
        await stream.WriteAsync(data, token).ConfigureAwait(false);
        if (progress is not null) progress.CompletedBytes = data.Length;
        progress?.Begin(progress.ServerSide() ? RuntimeTransportPhase.ReplyFlush : RuntimeTransportPhase.RequestFlush);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
    public void Dispose() => _stop.Cancel();
}

internal sealed class RuntimeClient(string channel, int uiOwner = 0, Action<RuntimeTransportDiagnostic>? diagnostic = null)
{
    private static long _nextTrace;
    private int _pending;
    private int _disconnected;
    internal bool IsBusy => Volatile.Read(ref _pending) > 0;
    internal void Disconnect() => Interlocked.Exchange(ref _disconnected, 1);
    internal async Task<RuntimeReply> CallAsync(RuntimeRequest request, int timeoutMilliseconds = 12000, bool allowRejected = false)
    {
        if (Volatile.Read(ref _disconnected) != 0) throw new IOException("Background process exited");
        request = request with { UiOwner = uiOwner, Trace = ((long)Environment.ProcessId << 32) | (uint)Interlocked.Increment(ref _nextTrace) };
        var progress = new RuntimeTransportProgress(false) { Trace = request.Trace };
        progress.SetCommand(request.Command);
        Interlocked.Increment(ref _pending);
        RuntimeReply reply;
        try
        {
            using var timeout = new CancellationTokenSource(timeoutMilliseconds);
            await using var pipe = new NamedPipeClientStream(".", channel, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            progress.Begin(RuntimeTransportPhase.Accept);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            await RuntimeChannel.WriteFrameAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(request, RuntimeJson.Default.RuntimeRequest), timeout.Token, progress).ConfigureAwait(false);
            var data = await RuntimeChannel.ReadFrameAsync(pipe, timeout.Token, progress).ConfigureAwait(false);
            progress.Begin(RuntimeTransportPhase.ReplyJson);
            reply = JsonSerializer.Deserialize(data, RuntimeJson.Default.RuntimeReply)
                ?? throw new IOException("Missing runtime reply");
            progress.Begin(RuntimeTransportPhase.Dispose);
        }
        catch (Exception e)
        {
            progress.Report(diagnostic, RuntimeTransportIssue.Failure, e, Volatile.Read(ref _disconnected) != 0, e is OperationCanceledException, 0);
            throw;
        }
        finally { Interlocked.Decrement(ref _pending); }
        if (!reply.Ok && !allowRejected) throw new IOException(reply.Error ?? "Background process refused the command");
        return reply;
    }
    internal RuntimeReply Call(RuntimeRequest request, int timeoutMilliseconds = 12000) => CallAsync(request, timeoutMilliseconds).GetAwaiter().GetResult();
}
