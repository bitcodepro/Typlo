using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using Typlo.Core.Settings;

namespace Typlo.App.Services;

internal sealed record RuntimeRequest(string Command, long Id = 0, long Window = 0, bool Flag = false, string? Text = null, AppSettings? Settings = null, string[]? Args = null, int UiOwner = 0);
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
    internal string Name { get; }
    internal RuntimeChannel(string name, Func<RuntimeRequest, Task<RuntimeReply>> handle)
    {
        Name = name; _handle = handle;
        _listener = ListenAsync();
    }
    private async Task ListenAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _clients.WaitAsync(_stop.Token).ConfigureAwait(false);
                var stream = new NamedPipeServerStream(Name, PipeDirection.InOut, 4, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try { await stream.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false); }
                catch { stream.Dispose(); _clients.Release(); throw; }
                _ = ServeAsync(stream);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (!_stop.IsCancellationRequested) Log.Error("Runtime channel stopped", e); }
    }
    private async Task ServeAsync(NamedPipeServerStream stream)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await using (stream)
            {
                try
                {
                    var data = await ReadFrameAsync(stream, timeout.Token).ConfigureAwait(false);
                    var request = JsonSerializer.Deserialize(data, RuntimeJson.Default.RuntimeRequest) ?? throw new InvalidDataException("Missing command");
                    RuntimeReply reply;
                    try { reply = await _handle(request).ConfigureAwait(false); }
                    catch (Exception e) { Log.Error($"Runtime command {request.Command} failed at {e.StackTrace}", e); reply = new(false, "Operation failed"); }
                    await WriteFrameAsync(stream, JsonSerializer.SerializeToUtf8Bytes(reply, RuntimeJson.Default.RuntimeReply), timeout.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or OperationCanceledException or JsonException or InvalidDataException) { }
            }
        }
        // A permit represents an actual OS pipe instance, including its asynchronous disposal.
        // Releasing inside await using lets the listener create a fifth instance before this one
        // closes, exceeding maxNumberOfServerInstances and permanently stopping the listener.
        finally { _clients.Release(); }
    }
    internal static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > MaxMessageBytes) throw new InvalidDataException("Invalid runtime message size");
        var data = new byte[length]; await stream.ReadExactlyAsync(data, token).ConfigureAwait(false); return data;
    }
    internal static async Task WriteFrameAsync(Stream stream, byte[] data, CancellationToken token)
    {
        if (data.Length is < 1 or > MaxMessageBytes) throw new InvalidDataException("Runtime message too large");
        var header = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false); await stream.WriteAsync(data, token).ConfigureAwait(false); await stream.FlushAsync(token).ConfigureAwait(false);
    }
    public void Dispose() => _stop.Cancel();
}

internal sealed class RuntimeClient(string channel, int uiOwner = 0)
{
    private int _pending;
    private int _disconnected;
    internal bool IsBusy => Volatile.Read(ref _pending) > 0;
    internal void Disconnect() => Interlocked.Exchange(ref _disconnected, 1);
    internal async Task<RuntimeReply> CallAsync(RuntimeRequest request, int timeoutMilliseconds = 12000, bool allowRejected = false)
    {
        if (Volatile.Read(ref _disconnected) != 0) throw new IOException("Background process exited");
        request = request with { UiOwner = uiOwner };
        Interlocked.Increment(ref _pending);
        try
        {
            using var timeout = new CancellationTokenSource(timeoutMilliseconds);
            await using var pipe = new NamedPipeClientStream(".", channel, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            await RuntimeChannel.WriteFrameAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(request, RuntimeJson.Default.RuntimeRequest), timeout.Token).ConfigureAwait(false);
            var reply = JsonSerializer.Deserialize(await RuntimeChannel.ReadFrameAsync(pipe, timeout.Token).ConfigureAwait(false), RuntimeJson.Default.RuntimeReply)
                ?? throw new IOException("Missing runtime reply");
            if (!reply.Ok && !allowRejected) throw new IOException(reply.Error ?? "Background process refused the command");
            return reply;
        }
        finally { Interlocked.Decrement(ref _pending); }
    }
    internal RuntimeReply Call(RuntimeRequest request, int timeoutMilliseconds = 12000) => CallAsync(request, timeoutMilliseconds).GetAwaiter().GetResult();
}
