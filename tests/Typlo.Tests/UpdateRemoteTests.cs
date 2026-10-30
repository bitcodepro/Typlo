using Typlo.App.Services;

namespace Typlo.Tests;

public sealed class UpdateRemoteTests
{
    [Fact]
    public async Task ResidentProgressAndFailureReachTheUiWithoutRepeatingTheInstallCommand()
    {
        var starts = 0; var reads = 0;
        // macOS counts the temporary-directory prefix toward the Unix socket path limit.
        using var channel = new RuntimeChannel("TU." + Guid.NewGuid().ToString("N"), request =>
        {
            if (request.Command == "update-start") { starts++; return Task.FromResult(new RuntimeReply(true, UpdateInstalling: true, UpdateProgress: 0)); }
            Assert.Equal("update-state", request.Command);
            return Task.FromResult(++reads == 1 ? new RuntimeReply(true, UpdateInstalling: true, UpdateProgress: 42) : new RuntimeReply(true, UpdateError: "Locked destination"));
        });
        using var updates = new UpdateService(Path.GetTempPath());
        var progress = new List<int?>(); updates.Changed += () => progress.Add(updates.Progress);
        await updates.InstallRemoteAsync(new(channel.Name));
        Assert.True(starts == 1, $"Install requests={starts}; error={updates.LastError}"); Assert.Equal("Locked destination", updates.LastError);
        Assert.Contains(42, progress); Assert.Null(updates.Progress);
    }
    [Fact]
    public async Task RejectedCommandIsVisibleAndIsNotRetried()
    {
        var requests = 0;
        using var channel = new RuntimeChannel("TU." + Guid.NewGuid().ToString("N"), _ =>
        { requests++; return Task.FromResult(new RuntimeReply(false, "Fixture rejection")); });
        using var updates = new UpdateService(Path.GetTempPath());
        await updates.InstallRemoteAsync(new(channel.Name));
        Assert.True(requests == 1, $"Install requests={requests}; error={updates.LastError}"); Assert.Contains("Fixture rejection", updates.LastError);
    }
}
