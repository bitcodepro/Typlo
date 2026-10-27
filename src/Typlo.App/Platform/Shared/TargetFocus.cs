using System.Diagnostics;

namespace Typlo.App.Platform.Shared;

internal static class TargetFocus
{
    // Wait for transient shell/notification activation to pass. Never activate a window ourselves,
    // and never send input while another window owns the foreground. Real user input cancels the wait.
    internal static async Task<bool> WaitAsync(nint target, Func<nint> foreground, Func<bool> stillValid, TimeSpan timeout, Action? onResumed = null)
    {
        if (target == 0) return false;
        var timer = Stopwatch.StartNew();
        var interrupted = false;
        while (stillValid())
        {
            if (foreground() == target)
            {
                if (interrupted) onResumed?.Invoke();
                return stillValid();
            }
            interrupted = true;
            if (timer.Elapsed >= timeout) return false;
            await Task.Delay(15);
        }
        return false;
    }
}
