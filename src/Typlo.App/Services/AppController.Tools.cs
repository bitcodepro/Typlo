using System.Diagnostics;
using Typlo.Core.Languages;
using Typlo.Core.Layouts;

namespace Typlo.App.Services;

public sealed record PerformanceSample(DateTimeOffset At, string Operation, long Milliseconds, string[] Phases);
public sealed record ToolsStatus(string? Field, string[] Choices, int ChoiceIndex, PerformanceSample[] Recent, long WorkingSet, long PrivateBytes, long ManagedBytes);

public sealed partial class AppController
{
    private readonly Lock _toolsLock = new();
    private readonly Queue<PerformanceSample> _performance = new();
    private string? _fieldReport;
    private sealed record ChoiceSession(RecentTyping.Run Run, long Version, long? Focus, nint Element, KeyboardLayout Source, LayoutOption[] Options, long Started, long Generation)
    { internal int Index; }
    private ChoiceSession? _choices;
    private readonly SemaphoreSlim _choiceCommands = new(1, 1);
    private long _choiceGeneration;
    public event Action<bool>? ToolsRequested;

    private void RecordPerformance(PerformanceSample sample)
    { lock (_toolsLock) { if (_performance.Count == 50) _performance.Dequeue(); _performance.Enqueue(sample); } }
    private static long ReadMemoryMetric(Func<long> read)
    {
        try { var value = read(); return value > 0 ? value : -1; }
        catch (Exception e) when (e is NotSupportedException or System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return -1; }
    }

    public async Task<ToolsStatus> GetToolsStatusAsync()
    {
        if (_remote is not null) return (await _remote.CallAsync(new("tools-state"))).Tools ?? throw new IOException("Missing tools status");
        return await Task.Run(() =>
        {
            using var process = Process.GetCurrentProcess();
            var workingSet = ReadMemoryMetric(() => process.WorkingSet64);
            var privateBytes = ReadMemoryMetric(() => process.PrivateMemorySize64);
            var managedBytes = GC.GetTotalMemory(false);
            var choice = Volatile.Read(ref _choices);
            if (choice is not null && !ChoiceCurrent(choice)) Interlocked.CompareExchange(ref _choices, null, choice);
            lock (_toolsLock) return new ToolsStatus(_fieldReport,
                ReferenceEquals(_choices, choice) && choice is not null ? choice.Options.Select(o => $"{FindLayout(o.LayoutId)?.Name ?? o.LayoutId}: {o.Text}").ToArray() : [],
                choice is null ? 0 : Volatile.Read(ref choice.Index), _performance.Reverse().ToArray(), workingSet, privateBytes, managedBytes);
        });
    }

    internal async Task ProbeFieldAsync()
    {
        await StopPendingAutomaticWords();
        var window = _platform.ForegroundWindow; var version = Interlocked.Read(ref _typingVersion);
        var focus = _platform.ForegroundChangeVersion; var element = _platform.FocusedInputElement;
        bool Valid() => window != 0 && IsTargetActive(window) && version == Interlocked.Read(ref _typingVersion) && !ForegroundChanged(focus) && _platform.FocusedInputElement == element;
        if (!await EnterManualTextOperationAsync(window)) return;
        var timer = Stopwatch.StartNew();
        try
        {
            await _platform.WaitForModifiersReleasedAsync(ModifierReleaseGrace);
            if (!Valid()) return;
            var direct = _platform.SupportsDirectTextEditing && _platform.CanRetainDirectInputTarget(window, element)
                ? await _platform.CaptureDirectTextAsync(window, element, Valid) : Typlo.App.Platform.DirectTextCapture.Unsupported;
            if (!Valid() || direct.Status == Typlo.App.Platform.DirectCaptureStatus.Cancelled) return;
            var process = _platform.GetApplicationProcessName(window) ?? "?";
            var report = Loc.T("tools.field", process,
                Loc.T(direct.Status == Typlo.App.Platform.DirectCaptureStatus.Ready ? "tools.directReady" : "tools.directNo"),
                Loc.T(_platform.Capabilities.SimulateInput ? "tools.selectionPossible" : "tools.selectionNo"),
                Loc.T(_platform.SupportsUnicodeTextReplacement && NeedsPasteVerification(window) ? "tools.unicode" : "tools.clipboard"));
            lock (_toolsLock) _fieldReport = report;
            RecordPerformance(new(DateTimeOffset.UtcNow, "field-probe", timer.ElapsedMilliseconds, []));
            ToolsRequested?.Invoke(false);
        }
        catch (Exception e) { Log.Error("Field capability probe failed", e); }
        finally { ExitManualTextOperation(); }
    }

    private bool ChoiceCurrent(ChoiceSession choice) => Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _updating) == 0 && !_editingSettings && !_capturingScreen &&
        choice.Generation == Interlocked.Read(ref _choiceGeneration) && IsTargetActive(choice.Run.Window) && choice.Version == Interlocked.Read(ref _typingVersion) && !ForegroundChanged(choice.Focus) &&
        _platform.FocusedInputElement == choice.Element && Stopwatch.GetElapsedTime(choice.Started) < TimeSpan.FromSeconds(30);

    internal async Task CycleChoicesAsync()
    {
        var generation = Interlocked.Read(ref _choiceGeneration); var version = Interlocked.Read(ref _typingVersion); var window = _platform.ForegroundWindow;
        await _choiceCommands.WaitAsync();
        try
        {
            if (generation == Interlocked.Read(ref _choiceGeneration) && version == Interlocked.Read(ref _typingVersion) && IsTargetActive(window))
                await CycleChoicesCoreAsync(generation);
        }
        catch (Exception e)
        {
            Log.Error("Manual dictionary choices unavailable", e);
            if (Volatile.Read(ref _disposed) == 0 && generation == Interlocked.Read(ref _choiceGeneration))
            { lock (_toolsLock) _fieldReport = Loc.T("manage.fileError"); ToolsRequested?.Invoke(false); }
        }
        finally { _choiceCommands.Release(); }
    }
    private async Task CycleChoicesCoreAsync(long generation)
    {
        await StopPendingAutomaticWords();
        if (generation != Interlocked.Read(ref _choiceGeneration) || _convertGate.CurrentCount == 0) return;
        if (_choices is { } previous && ChoiceCurrent(previous))
        {
            int old, next;
            do { old = Volatile.Read(ref previous.Index); next = (old + 1) % previous.Options.Length; }
            while (Interlocked.CompareExchange(ref previous.Index, next, old) != old);
            ToolsRequested?.Invoke(true); return;
        }
        Interlocked.Exchange(ref _choices, null);
        var window = _platform.ForegroundWindow; var run = _recentTyping.Capture(window);
        var version = Interlocked.Read(ref _typingVersion); var focus = _platform.ForegroundChangeVersion;
        var element = _platform.FocusedInputElement; var source = FindLayout(_platform.GetCurrentLayoutId(window));
        if (run is null || source is null) { lock (_toolsLock) _fieldReport = Loc.T("tools.noWord"); ToolsRequested?.Invoke(false); return; }
        var timer = Stopwatch.StartNew();
        var options = await Task.Run(() => new LayoutLanguageDetector(_lexicon, _personal).Options(run.Keys.TrimEnd(' '),
            ApplicationCycleFor(window)?.Layouts ?? LayoutCatalog.Cycle(Settings).ToArray()));
        RecordPerformance(new(DateTimeOffset.UtcNow, "manual-candidates", timer.ElapsedMilliseconds, []));
        if (generation != Interlocked.Read(ref _choiceGeneration) || version != Interlocked.Read(ref _typingVersion) || !IsTargetActive(window)) return;
        if (options.Length == 0) { lock (_toolsLock) _fieldReport = Loc.T("tools.noOptions"); ToolsRequested?.Invoke(false); return; }
        var choice = new ChoiceSession(run, version, focus, element, source, options, Stopwatch.GetTimestamp(), generation);
        if (!ChoiceCurrent(choice) || _convertGate.CurrentCount == 0) return;
        Interlocked.Exchange(ref _choices, choice); ToolsRequested?.Invoke(true);
    }

    internal void CancelChoices() { Interlocked.Increment(ref _choiceGeneration); Interlocked.Exchange(ref _choices, null); }
    internal async Task ApplyChoiceAsync()
    {
        var generation = Interlocked.Read(ref _choiceGeneration);
        await _choiceCommands.WaitAsync();
        var consuming = false;
        try
        {
            if (generation == Interlocked.Read(ref _choiceGeneration))
            {
                consuming = _choices is { } pending && ChoiceCurrent(pending);
                await ApplyChoiceCoreAsync();
            }
        }
        finally
        {
            if (consuming) Interlocked.CompareExchange(ref _choiceGeneration, unchecked(generation + 1), generation);
            _choiceCommands.Release();
        }
    }
    private async Task ApplyChoiceCoreAsync()
    {
        var choice = Interlocked.Exchange(ref _choices, null);
        if (choice is null || !ChoiceCurrent(choice)) return;
        var target = FindLayout(choice.Options[Volatile.Read(ref choice.Index)].LayoutId);
        if (target is null) return;
        await StopPendingAutomaticWords();
        if (!ChoiceCurrent(choice) || !await EnterManualTextOperationAsync(choice.Run.Window)) return;
        var started = Stopwatch.GetTimestamp(); var strategy = "selection";
        Platform.ClipboardSnapshot? snapshot = null;
        try
        {
            await _platform.WaitForModifiersReleasedAsync(ModifierReleaseGrace);
            if (!ChoiceCurrent(choice)) return;
            if (await TryDirectTransformAsync(choice.Run.Window, choice.Run.Element, choice.Version, () => ChoiceCurrent(choice),
                (text, _) => choice.Run.MatchSource(text, [choice.Source]) is not null
                    ? (LayoutConverter.Convert(text, choice.Source, target, forceSource: true), target) : (text, null), null, choice.Run))
            { strategy = "direct"; return; }
            using var suspended = _platform.SuspendCapture(); snapshot = CaptureClipboard();
            using var guard = NeedsPasteVerification(choice.Run.Window) && _platform.SupportsProtectedCopy ? ProtectOwnedSelection(choice.Run.Window, () => ChoiceCurrent(choice)) : null;
            var text = await SelectRecentInputAsync(choice.Run, () => ChoiceCurrent(choice), choice.Source);
            if (text is null || choice.Run.MatchSource(text, [choice.Source]) is null || !ChoiceCurrent(choice)) return;
            var result = LayoutConverter.Convert(text, choice.Source, target, forceSource: true);
            if (!await PasteTextAsync(result, choice.Run.Window, () => ChoiceCurrent(choice), text)) return;
            if (NeedsPasteVerification(choice.Run.Window) && await VerifyPastedRangeAsync(choice.Run.Window, text, result, () => ChoiceCurrent(choice),
                probeRemainingSelection: !PreferUnicodeReplacement(choice.Run.Window, result)) != ClipboardPasteState.Confirmed) return;
            if (!ChoiceCurrent(choice)) return;
            LastTransform = new(choice.Run.Window, text, result, DateTime.UtcNow, target);
            _recentTyping.Consume(choice.Version);
            if (Settings.SwitchSystemLayout) _platform.SwitchKeyboardLayout(choice.Run.Window, target);
        }
        catch (Exception e) { Log.Error("Manual candidate application stopped", e); }
        finally
        {
            try { if (snapshot is not null) RestoreIfUnchanged(snapshot); }
            catch (Exception e) { Log.Error("Manual choice clipboard restoration failed", e); }
            finally
            {
                ExitManualTextOperation();
                RecordPerformance(new(DateTimeOffset.UtcNow, "manual-choice", (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, [strategy]));
            }
        }
    }
}
