using Typlo.Core.Layouts;

namespace Typlo.App.Services;

public sealed partial class AppController
{
    /// <summary>Changes only the input source; never reads or replaces editor text.</summary>
    internal async Task CycleInputLayoutAsync()
    {
        var window = _platform.ForegroundWindow;
        var version = Interlocked.Read(ref _typingVersion);
        var focus = _platform.SupportsForegroundNotifications ? _platform.ForegroundChangeVersion : null;
        await StopPendingAutomaticWords();
        // Distinct input-only gestures may queue behind a switch whose native request is
        // already visible. Text replacement commands retain their existing no-queue behavior.
        if (!await EnterManualTextOperationAsync(window, waitForGate: true)) return;
        try
        {
            bool Current() => Volatile.Read(ref _disposed) == 0 && _platform.ForegroundWindow == window &&
                Interlocked.Read(ref _typingVersion) == version && !ForegroundChanged(focus);
            await _platform.WaitForModifiersReleasedAsync(ModifierReleaseGrace);
            if (!Current()) return;
            var cycle = ApplicationCycleFor(window)?.Layouts ?? LayoutCatalog.Cycle(Settings).ToArray();
            if (cycle.Length == 0) return;
            var current = FindLayout(_platform.GetCurrentLayoutId(window));
            var at = Array.FindIndex(cycle, layout => layout.Id == current?.Id);
            var target = cycle[(at + 1) % cycle.Length];
            if (!Current()) return;
            var accepted = _platform.SwitchKeyboardLayout(window, target);
            Log.Info($"Input cycle: count={cycle.Length}, from={current?.Id}, to={target.Id}, accepted={accepted}");
            if (accepted)
            {
                LastTransform = null;
                _recentTyping.Consume(version);
                // Windows posts the request asynchronously. Keep the gate until the source
                // is observed, so a rapid next gesture reads the applied source, not its predecessor.
                var started = Environment.TickCount64;
                while (Current() && FindLayout(_platform.GetCurrentLayoutId(window))?.Id != target.Id)
                {
                    if (Environment.TickCount64 - started >= 250)
                    {
                        Log.Info("Input cycle: applied layout was not confirmed; command will not be retried");
                        break;
                    }
                    await Task.Delay(5);
                }
            }
        }
        catch (Exception e) { Log.Error("Input layout cycle failed", e); }
        finally { ExitManualTextOperation(); }
    }
}
