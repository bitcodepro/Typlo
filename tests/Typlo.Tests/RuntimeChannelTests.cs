using System.Buffers.Binary;
using System.IO.Pipes;
using Typlo.App.Platform;
using Typlo.App.Services;

namespace Typlo.Tests;

public sealed class RuntimeChannelTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(RuntimeChannel.MaxMessageBytes + 1)]
    public async Task InvalidLengthIsRejectedBeforeAllocatingPayload(int length)
    {
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, length);
        await Assert.ThrowsAsync<InvalidDataException>(() => RuntimeChannel.ReadFrameAsync(new MemoryStream(header), CancellationToken.None));
    }

    [Fact]
    public async Task DisconnectedAndMalformedClientsDoNotStopTheNextCommand()
    {
        var name = "Typlo.Test." + Guid.NewGuid().ToString("N");
        using var server = new RuntimeChannel(name, request => Task.FromResult(new RuntimeReply(request.Command == "state")));
        await using (var broken = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await broken.ConnectAsync(limit.Token);
            await broken.WriteAsync(new byte[] { 255, 255, 255, 127 }, limit.Token);
        }
        var client = new RuntimeClient(name);
        await Assert.ThrowsAsync<IOException>(() => client.CallAsync(new("unknown")));
        Assert.True((await client.CallAsync(new("state"))).Ok);
        Assert.False(client.IsBusy);
    }

    [Fact]
    public async Task ImageBurstsKeepOnlyTheActiveAndNewestPendingImage()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var processed = new List<byte>();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var worker = new LatestImageWorker((raw, _) =>
        {
            var value = raw.Image![0]; processed.Add(value);
            if (value == 1) { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); }
            if (value == 100) finished.TrySetResult();
        }))
        {
            worker.Post(new RawClip(null, [1], true, "fixture"));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            for (byte value = 2; value <= 100; value++) worker.Post(new RawClip(null, [value], true, "fixture"));
            release.Set();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        Assert.Equal(new byte[] { 1, 100 }, processed);
    }

    [Fact]
    public async Task SynchronousUiCommandsDoNotRequireTheBlockedUiSynchronizationContext()
    {
        var name = "Typlo.Test." + Guid.NewGuid().ToString("N");
        using var server = new RuntimeChannel(name, _ => Task.FromResult(new RuntimeReply(true)));
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new BlockedUiContext();
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            try { new RuntimeClient(name).Call(new("state")); done.SetResult(); }
            catch (Exception e) { done.SetException(e); }
        })
        { IsBackground = true };
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, context.Posts);
    }

    private sealed class BlockedUiContext : SynchronizationContext
    {
        internal int Posts;
        public override void Post(SendOrPostCallback d, object? state) => Interlocked.Increment(ref Posts);
    }
}
