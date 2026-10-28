using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Typlo.App.Services;

namespace Typlo.Tests;

public sealed partial class RuntimeChannelTests
{
    // Deliberately no Task.Yield/delay/asynchronous handler: synchronous completion can close the
    // final Unix server instance while the next client is already queued on its shared socket.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task RapidSequentialRequestsKeepListenerAliveWithSynchronousHandler(int offset)
    {
        var name = "Typlo.Test." + Guid.NewGuid().ToString("N");
        var diagnostics = new ConcurrentQueue<RuntimeTransportDiagnostic>();
        var received = new ConcurrentDictionary<long, int>();
        using var server = new RuntimeChannel(name, request =>
        {
            received.AddOrUpdate(request.Id, 1, (_, count) => count + 1);
            return Task.FromResult(new RuntimeReply(request.Command != "capture-begin" || request.Id % 7 != 0, Window: request.Id));
        }, diagnostics.Enqueue);
        var client = new RuntimeClient(name, diagnostic: diagnostics.Enqueue);
        for (var i = 1; i <= 1000; i++)
        {
            var command = (i + offset) % 3 == 0 ? "capture-begin" : "capture-end";
            var reply = await client.CallAsync(new(command, Id: i), allowRejected: true);
            Assert.Equal((long)i, reply.Window);
            Assert.Equal(command != "capture-begin" || i % 7 != 0, reply.Ok);
        }
        Assert.Equal(1000, received.Count);
        Assert.All(received.Values, count => Assert.Equal(1, count));
        Assert.False(client.IsBusy);
        var failures = diagnostics.Where(d => d.Issue != RuntimeTransportIssue.Accepted).ToArray();
        Assert.True(failures.Length == 0, string.Join(Environment.NewLine, failures.Take(5).Select(d => d.Summary)));
    }

    [Fact]
    public async Task UnixQueuedNextClientSurvivesSynchronousPreviousReply()
    {
        // Unix pipes share a listening socket. Windows has per-instance connection semantics and
        // is covered by the sequential/burst tests; blocking a second Connect here would deadlock.
        if (OperatingSystem.IsWindows()) return;
        var name = "Typlo.Test." + Guid.NewGuid().ToString("N");
        using var firstFrameReady = new ManualResetEventSlim();
        var accepted = 0;
        var barrierTimedOut = 0;
        var received = new ConcurrentQueue<long>();
        using var next = ClientPipe(name);
        using var server = new RuntimeChannel(name, request =>
        {
            received.Enqueue(request.Id);
            if (request.Id == 1)
            {
                // Queue an entire second request on the existing Unix listening socket before
                // the first, fully buffered request returns its synchronous reply and disposes.
                next.Connect(5000);
                next.Write(Frame(new("capture-end", Id: 2)));
            }
            return Task.FromResult(new RuntimeReply(true, Window: request.Id));
        }, entry =>
        {
            if (entry.Issue == RuntimeTransportIssue.Accepted && Interlocked.Increment(ref accepted) == 1 &&
                !firstFrameReady.Wait(TimeSpan.FromSeconds(5))) Interlocked.Exchange(ref barrierTimedOut, 1);
        });
        using var first = ClientPipe(name);
        try
        {
            first.Connect(5000);
            first.Write(Frame(new("capture-begin", Id: 1)));
        }
        finally { firstFrameReady.Set(); }
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reply1 = JsonSerializer.Deserialize(await RuntimeChannel.ReadFrameAsync(first, limit.Token), RuntimeJson.Default.RuntimeReply);
        var reply2 = JsonSerializer.Deserialize(await RuntimeChannel.ReadFrameAsync(next, limit.Token), RuntimeJson.Default.RuntimeReply);
        Assert.Equal(1L, reply1!.Window);
        Assert.Equal(2L, reply2!.Window);
        Assert.Equal(0, barrierTimedOut);
        Assert.Equal(new long[] { 1, 2 }, received.ToArray());
    }

    private static byte[] Frame(RuntimeRequest request)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(request, RuntimeJson.Default.RuntimeRequest);
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        return frame;
    }

    [Fact]
    public async Task TruncatedRequestReportsPhaseAndByteCountsAndNextClientStillWorks()
    {
        var name = "Typlo.Test." + Guid.NewGuid().ToString("N");
        var observed = new TaskCompletionSource<RuntimeTransportDiagnostic>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new RuntimeChannel(name, _ => Task.FromResult(new RuntimeReply(true)), d =>
        {
            if (d.Issue == RuntimeTransportIssue.Failure) observed.TrySetResult(d);
        });
        await using (var pipe = ClientPipe(name))
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await pipe.ConnectAsync(limit.Token);
            await pipe.WriteAsync(new byte[] { 42, 43 }, limit.Token);
        }
        var diagnostic = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RuntimeTransportPhase.RequestHeader, diagnostic.Phase);
        Assert.Equal(2, diagnostic.CompletedBytes);
        Assert.Equal(4, diagnostic.ExpectedBytes);
        Assert.Equal(typeof(EndOfStreamException).FullName, diagnostic.ExceptionType);
        Assert.True(diagnostic.Server);
        Assert.True(diagnostic.Connection > 0);
        Assert.True((await new RuntimeClient(name).CallAsync(new("state"))).Ok);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DiagnosticsNeverContainPayloadUnknownCommandOrExceptionMessage(bool malformed)
    {
        const string secret = "private-clipboard-marker-DO-NOT-LOG";
        var name = "Typlo.Test." + Guid.NewGuid().ToString("N");
        var observed = new TaskCompletionSource<RuntimeTransportDiagnostic>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new RuntimeChannel(name, _ => throw new IOException(secret), d =>
        {
            if (d.Issue == RuntimeTransportIssue.Failure) observed.TrySetResult(d);
        });
        if (malformed)
        {
            await using var pipe = ClientPipe(name);
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await pipe.ConnectAsync(limit.Token);
            await RuntimeChannel.WriteFrameAsync(pipe, Encoding.UTF8.GetBytes("{\"Text\":\"" + secret + "\""), limit.Token);
            _ = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        else Assert.False((await new RuntimeClient(name).CallAsync(new(secret, Text: secret), allowRejected: true)).Ok);
        var diagnostic = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(malformed ? RuntimeTransportPhase.RequestJson : RuntimeTransportPhase.Handler, diagnostic.Phase);
        Assert.Equal("unknown", diagnostic.Command);
        Assert.DoesNotContain(secret, diagnostic.Summary);
        Assert.DoesNotContain(secret, diagnostic.ToString());
    }

    [Fact]
    public async Task LostReplyReportsPartialBodyAndDoesNotRetryTheCommand()
    {
        var name = "Typlo.Test." + Guid.NewGuid().ToString("N");
        var diagnostics = new ConcurrentQueue<RuntimeTransportDiagnostic>();
        var calls = 0;
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var client = new RuntimeClient(name, diagnostic: diagnostics.Enqueue);
        var call = client.CallAsync(new("capture-end", Text: "private clipboard text"));
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await server.WaitForConnectionAsync(limit.Token);
        _ = await RuntimeChannel.ReadFrameAsync(server, limit.Token);
        Interlocked.Increment(ref calls); // The command took effect, but its reply is lost.
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, 10);
        await server.WriteAsync(header, limit.Token);
        await server.WriteAsync(new byte[] { 1, 2 }, limit.Token);
        server.Dispose();
        await Assert.ThrowsAsync<EndOfStreamException>(() => call);
        Assert.Equal(1, calls);
        var diagnostic = Assert.Single(diagnostics);
        Assert.False(diagnostic.Server);
        Assert.Equal("capture-end", diagnostic.Command);
        Assert.True(diagnostic.Trace > 0);
        Assert.Equal(RuntimeTransportPhase.ReplyBody, diagnostic.Phase);
        Assert.Equal(2, diagnostic.CompletedBytes);
        Assert.Equal(10, diagnostic.ExpectedBytes);
        Assert.DoesNotContain("private clipboard text", diagnostic.Summary);
        Assert.False(client.IsBusy);
    }

    private static NamedPipeClientStream ClientPipe(string name) => new(".", name, PipeDirection.InOut,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
}
