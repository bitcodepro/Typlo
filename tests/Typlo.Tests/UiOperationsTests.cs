using Typlo.App.Services;

namespace Typlo.Tests;

public sealed class UiOperationsTests
{
    [Fact]
    public async Task ShutdownCancelsButWaitsForPublicationAndCleanup()
    {
        var operations = new UiOperations();
        var first = operations.Begin()!;
        var second = operations.Begin()!;
        Assert.True(operations.IsBusy);
        var stopping = operations.StopAsync();
        Assert.True(first.Token.IsCancellationRequested);
        Assert.False(stopping.IsCompleted);
        Assert.Null(operations.Begin());
        first.Dispose(); first.Dispose();
        Assert.True(operations.IsBusy);
        Assert.False(stopping.IsCompleted);
        second.Dispose();
        await stopping.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(operations.IsBusy);
    }

    [Fact]
    public async Task CompletionReleasesLeaseEvenAfterFailure()
    {
        var operations = new UiOperations();
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            using var work = operations.Begin();
            await Task.Yield();
            throw new IOException("fixture");
        });
        Assert.False(operations.IsBusy);
        await operations.StopAsync();
    }
}
