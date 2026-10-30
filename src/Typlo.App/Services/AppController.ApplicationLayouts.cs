using Typlo.Core.Layouts;
using Typlo.Core.Settings;

namespace Typlo.App.Services;

public sealed partial class AppController
{
    private sealed record ApplicationCycle(KeyboardLayout[] Layouts, LayoutConverter Converter);
    private Dictionary<string, ApplicationCycle> _applicationCycles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _applicationFocusLock = new();
    private (nint Window, long Input, long Generation)? _pendingApplicationFocus;
    private long _applicationFocusGeneration;
    private bool _applicationFocusWorker;
    private Task _applicationFocusTask = Task.CompletedTask;
    private Timer? _applicationFocusPoll;
    private nint _lastPolledApplication;
    private nint _manualTextWindow;
    private sealed class AutomaticLayoutOperation
    {
        internal readonly CancellationTokenSource Stop = new();
        internal readonly TaskCompletionSource Completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private AutomaticLayoutOperation? _automaticLayoutOperation;

    private async Task<bool> EnterManualTextOperationAsync(nint window, bool waitForGate = false)
    {
        AutomaticLayoutOperation? automatic;
        lock (_applicationFocusLock)
        {
            if (Volatile.Read(ref _updating) != 0) return false;
            Interlocked.Increment(ref _applicationFocusGeneration); _pendingApplicationFocus = null;
            automatic = _automaticLayoutOperation; automatic?.Stop.Cancel();
        }
        if (automatic is not null) await automatic.Completed.Task;
        if (waitForGate)
        {
            if (!await _convertGate.WaitAsync(TimeSpan.FromSeconds(1))) return false;
            lock (_applicationFocusLock)
            {
                if (Volatile.Read(ref _disposed) != 0) { _convertGate.Release(); return false; }
                _manualTextWindow = window; return true;
            }
        }
        lock (_applicationFocusLock)
        {
            if (Volatile.Read(ref _disposed) != 0 || !_convertGate.Wait(0)) return false;
            _manualTextWindow = window; return true;
        }
    }

    private void ExitManualTextOperation()
    {
        lock (_applicationFocusLock) { _manualTextWindow = 0; _convertGate.Release(); }
    }

    private void BuildApplicationCycles()
    {
        var cycles = new Dictionary<string, ApplicationCycle>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in Settings.ApplicationLayouts.Where(r => r.Enabled))
        {
            var name = ApplicationLayoutRule.ProcessKey(rule.ProcessName);
            var layouts = rule.Layouts.Select(FindLayout).OfType<KeyboardLayout>().Where(l => l.SupportsConversion)
                .DistinctBy(l => l.Id).ToArray();
            if (name.Length == 0 || layouts.Length == 0 || layouts.Length != rule.Layouts.Count) continue;
            var candidates = layouts.Concat(_converter.Layouts).DistinctBy(l => l.Id).ToArray();
            cycles.TryAdd(name, new(layouts, new LayoutConverter(candidates, layouts)));
        }
        Volatile.Write(ref _applicationCycles, cycles);
    }

    private ApplicationCycle? ApplicationCycleFor(nint window)
    {
        var cycles = Volatile.Read(ref _applicationCycles);
        if (cycles.Count == 0 || window == 0) return null;
        var process = ApplicationLayoutRule.ProcessKey(_platform.GetApplicationProcessName(window));
        return cycles.GetValueOrDefault(process);
    }

    private void ConfigureApplicationLayouts()
    {
        _platform.ForegroundChanged -= OnApplicationForeground;
        _applicationFocusPoll?.Dispose(); _applicationFocusPoll = null;
        lock (_applicationFocusLock)
        { Interlocked.Increment(ref _applicationFocusGeneration); _pendingApplicationFocus = null; _automaticLayoutOperation?.Stop.Cancel(); }
        if (_remote is not null || Volatile.Read(ref _disposed) != 0 ||
            Volatile.Read(ref _applicationCycles).Count == 0) return;
        _platform.ForegroundChanged += OnApplicationForeground;
        _lastPolledApplication = _platform.ForegroundWindow;
        OnApplicationForeground(_lastPolledApplication);
        if (!_platform.SupportsForegroundNotifications)
            _applicationFocusPoll = new Timer(_ =>
            {
                try
                {
                    var window = _platform.ForegroundWindow;
                    lock (_applicationFocusLock)
                    {
                        if (window == _lastPolledApplication) return;
                        _lastPolledApplication = window;
                    }
                    OnApplicationForeground(window);
                }
                catch (Exception e) { Log.Error("Application layout focus lookup failed", e); }
            }, null, 500, 500);
    }

    private void OnApplicationForeground(nint window)
    {
        if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _applicationCycles).Count == 0) return;
        lock (_applicationFocusLock)
        {
            var generation = Interlocked.Increment(ref _applicationFocusGeneration);
            _automaticLayoutOperation?.Stop.Cancel();
            if (window == _manualTextWindow) { _pendingApplicationFocus = null; return; }
            _pendingApplicationFocus = (window, Interlocked.Read(ref _typingVersion), generation);
            if (_applicationFocusWorker) return;
            _applicationFocusWorker = true;
            _applicationFocusTask = Task.Run(DrainApplicationFocusAsync);
        }
    }

    internal Task ApplicationLayoutsIdleAsync() { lock (_applicationFocusLock) return _applicationFocusTask; }

    private async Task DrainApplicationFocusAsync()
    {
        while (true)
        {
            (nint Window, long Input, long Generation) work;
            lock (_applicationFocusLock)
            {
                if (_pendingApplicationFocus is not { } pending)
                { _applicationFocusWorker = false; return; }
                work = pending; _pendingApplicationFocus = null;
            }
            try
            {
                if (Volatile.Read(ref _disposed) != 0 || work.Window == 0) continue;
                var rule = ApplicationCycleFor(work.Window);
                if (rule is null) continue;
                var automatic = new AutomaticLayoutOperation();
                lock (_applicationFocusLock)
                {
                    if (work.Generation != _applicationFocusGeneration || Volatile.Read(ref _disposed) != 0)
                    { automatic.Stop.Dispose(); continue; }
                    _automaticLayoutOperation = automatic;
                }
                try
                {
                    if (!await _convertGate.WaitAsync(TimeSpan.FromSeconds(1), automatic.Stop.Token)) continue;
                    try
                    {
                        bool Valid() => Volatile.Read(ref _disposed) == 0 &&
                            Interlocked.Read(ref _applicationFocusGeneration) == work.Generation &&
                            Interlocked.Read(ref _typingVersion) == work.Input && _platform.ForegroundWindow == work.Window;
                        if (!Valid()) continue;
                        var current = FindLayout(_platform.GetCurrentLayoutId(work.Window));
                        if (rule.Layouts.Any(layout => layout.Id == current?.Id)) continue;
                        var target = rule.Layouts.FirstOrDefault(layout =>
                            layout.LanguageCode.Equals("en", StringComparison.OrdinalIgnoreCase)) ?? rule.Layouts[0];
                        if (Valid() && _platform.SwitchKeyboardLayout(work.Window, target))
                        {
                            LastTransform = null;
                            _recentTyping.Consume(work.Input);
                        }
                    }
                    finally { _convertGate.Release(); }
                }
                catch (OperationCanceledException) when (automatic.Stop.IsCancellationRequested) { }
                finally
                {
                    lock (_applicationFocusLock) _automaticLayoutOperation = null;
                    automatic.Completed.TrySetResult(); automatic.Stop.Dispose();
                }
            }
            catch (Exception e) { Log.Error("Application layout switch failed", e); }
        }
    }

    private void StopApplicationLayouts()
    {
        _platform.ForegroundChanged -= OnApplicationForeground;
        _applicationFocusPoll?.Dispose(); _applicationFocusPoll = null;
        lock (_applicationFocusLock)
        { Interlocked.Increment(ref _applicationFocusGeneration); _pendingApplicationFocus = null; _automaticLayoutOperation?.Stop.Cancel(); }
    }
}
