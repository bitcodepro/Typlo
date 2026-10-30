using Typlo.Core.Layouts;

namespace Typlo.App.Services;

public sealed partial class AppController
{
    /// <summary>Changes only the input source; never reads or replaces editor text.</summary>
    internal async Task CycleInputLayoutAsync()
    {
        var window = _platform.ForegroundWindow;
        var version = Interlocked.Read(ref _typingVersion);
        await StopPendingAutomaticWords();
        if (!await EnterManualTextOperationAsync(window)) return;
        try
        {
            bool Current() => Volatile.Read(ref _disposed) == 0 && _platform.ForegroundWindow == window &&
                Interlocked.Read(ref _typingVersion) == version;
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
            }
        }
        catch (Exception e) { Log.Error("Input layout cycle failed", e); }
        finally { ExitManualTextOperation(); }
    }
}
