using Typlo.App.Platform.Shared;
using Typlo.App.Services;

namespace Typlo.Tests;

public sealed partial class AppControllerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MouseClickAfterUndoDoesNotWaitForFocusContextAndStartsANewCorrectionScope(bool native)
    {
        EnableAutomaticCorrection(native);
        _platform.Type("ghb "); await _controller.AutoCorrectionIdleAsync();
        await _controller.UndoLastTransformAsync(); Assert.Equal("ghb ", _platform.Document.Text);
        var contextEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var foregroundReads = 0;
        _platform.OnForegroundRead = () => Interlocked.Increment(ref foregroundReads);
        _platform.OnForegroundVersionRead = () =>
        {
            contextEntered.TrySetResult();
            release.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        };
        var click = Task.Run(() => _platform.UserMoveCaret(0));
        try
        {
            // A dependency barrier, not a small wall-clock threshold: the click must finish
            // while the simulated Windows message thread remains unavailable.
            await Task.WhenAny(click, contextEntered.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(click.IsCompleted, "Mouse callback waited for the foreground context service");
            await click;
            Assert.False(contextEntered.Task.IsCompleted);
            Assert.Equal(0, foregroundReads);
        }
        finally
        {
            release.TrySetResult(); await click;
            _platform.OnForegroundRead = null; _platform.OnForegroundVersionRead = null;
        }
        _platform.ActiveLayoutId = "en";
        _platform.Type("ghb "); await _controller.AutoCorrectionIdleAsync();
        Assert.Equal("при ghb ", _platform.Document.Text);
        Assert.Equal(AutoCorrectionReason.Corrected, (await _controller.GetAutomaticStatusAsync()).Recent[0].Reason);
    }

    [Fact]
    public async Task MouseClickDuringAutomaticCopyCancelsPasteAndPreservesTheClickedCaret()
    {
        EnableAutomaticCorrection();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var copySent = false;
        _platform.AfterCopy = () => { _platform.AfterCopy = null; copySent = true; };
        _platform.OnSequenceRead = () =>
        {
            if (!copySent) return;
            copySent = false; entered.TrySetResult();
            release.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        };
        _platform.Type("ghb ");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            _platform.UserMoveCaret(0);
            release.TrySetResult(); await _controller.AutoCorrectionIdleAsync();
            Assert.Equal("ghb ", _platform.Document.Text); Assert.Equal(0, _platform.PasteCount);
            Assert.Equal(0, _platform.Document.Caret); Assert.Null(_platform.Document.Anchor);
        }
        finally { release.TrySetResult(); await _controller.AutoCorrectionIdleAsync(); }
    }

    [Fact]
    public async Task MouseClickDuringUndoCancelsTheOldReplacementWithoutPublishingSuppression()
    {
        EnableAutomaticCorrection();
        _platform.Type("ghb "); await _controller.AutoCorrectionIdleAsync();
        Assert.Equal("при ", _platform.Document.Text);
        var pastes = _platform.PasteCount;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _platform.AfterCopy = () =>
        {
            _platform.AfterCopy = null; entered.TrySetResult();
            release.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        };
        var undo = Task.Run(() => _controller.UndoLastTransformAsync());
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            _platform.UserMoveCaret(0);
            release.TrySetResult(); await undo;
            Assert.Equal("при ", _platform.Document.Text); Assert.Equal(pastes, _platform.PasteCount);
            Assert.Equal(0, _platform.Document.Caret); Assert.Null(_platform.Document.Anchor);
            _platform.ActiveLayoutId = "en";
            _platform.Type("ghb "); await _controller.AutoCorrectionIdleAsync();
            Assert.Equal("при при ", _platform.Document.Text);
            Assert.Equal(AutoCorrectionReason.Corrected, (await _controller.GetAutomaticStatusAsync()).Recent[0].Reason);
        }
        finally { release.TrySetResult(); await undo; }
    }
}
