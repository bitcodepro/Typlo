using System.Diagnostics;

namespace Typlo.App.Services;

public sealed partial class AppController
{
    private readonly Lock _quickSpellingLock = new();
    private readonly CancellationTokenSource _quickSpellingShutdown = new();
    private Task _quickSpellingTask = Task.CompletedTask;
    private long _quickSpellingGeneration;
    internal Task QuickSpellingIdleAsync() { lock (_quickSpellingLock) return _quickSpellingTask; }
    internal Func<SpellingRequest, CancellationToken, Task<SpellingResult>> QuickSpellingCheck { get; set; } =
        (request, token) => SpellingWorker.CheckAsync(request, token);

    internal Task StopQuickSpellingAsync()
    {
        lock (_quickSpellingLock) { Interlocked.Increment(ref _quickSpellingGeneration); return _quickSpellingTask; }
    }
    internal Task CorrectSpellingAsync()
    {
        var window = _platform.ForegroundWindow; var input = Interlocked.Read(ref _typingVersion);
        var focus = _platform.ForegroundChangeVersion; var element = _platform.FocusedInputElement;
        lock (_quickSpellingLock)
        {
            var generation = Interlocked.Increment(ref _quickSpellingGeneration); var previous = _quickSpellingTask;
            bool ContextValid() => !_quickSpellingShutdown.IsCancellationRequested &&
                Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _updating) == 0 && !_editingSettings && !_capturingScreen &&
                window != 0 && IsTargetActive(window) && input == Interlocked.Read(ref _typingVersion) && !ForegroundChanged(focus) && _platform.FocusedInputElement == element;
            bool Valid() => generation == Interlocked.Read(ref _quickSpellingGeneration) && ContextValid();
            return _quickSpellingTask = Task.Run(async () =>
            {
                SpellingSession? session = null;
                var watch = Stopwatch.StartNew();
                var outcome = "canceled";
                try
                {
                    await previous.ConfigureAwait(false);
                    // No preview, focus activation or clipboard edit on an unsupported target.
                    if (!Valid()) return;
                    if (!CanApplySpelling(element)) { outcome = "unsupported-target"; return; }
                    session = await CaptureSpellingCoreAsync(preview: false, Valid).ConfigureAwait(false);
                    if (session is null || !Valid()) return;
                    if (session.Selection.Notice is not null) { outcome = session.Selection.Notice; return; }
                    var clipboard = _platform.ClipboardSequence;
                    bool Current() => Valid() && _platform.ClipboardSequence == clipboard && Volatile.Read(ref session.Canceled) == 0 && ReferenceEquals(Volatile.Read(ref _spelling), session);
                    using var stop = CancellationTokenSource.CreateLinkedTokenSource(_quickSpellingShutdown.Token);
                    var worker = QuickSpellingCheck(new(session.Selection.Text, Ignored: session.Selection.Ignored,
                        Corrections: PublishedSettings.SpellingCorrections.ToArray(), Excluded: session.Selection.Excluded), stop.Token);
                    try
                    {
                        while (!worker.IsCompleted && Current()) await Task.WhenAny(worker, Task.Delay(25)).ConfigureAwait(false);
                        if (!Current()) { await stop.CancelAsync().ConfigureAwait(false); return; }
                        var result = await worker.ConfigureAwait(false);
                        if (!Current()) return;
                        var replacement = SpellingText.Apply(session.Selection.Text, result.Issues,
                            result.Issues.Select(issue => issue.Suggestions.FirstOrDefault()).ToArray());
                        var applied = await ApplySpellingCoreAsync(session.Selection.Id, replacement, Valid, ContextValid, clipboard).ConfigureAwait(false);
                        outcome = applied ? replacement == session.Selection.Text ? "unchanged" : "corrected" : "unconfirmed";
                    }
                    finally
                    {
                        await stop.CancelAsync().ConfigureAwait(false);
                        try { await worker.ConfigureAwait(false); } catch (OperationCanceledException) { }
                    }
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                { outcome = e is OperationCanceledException ? "canceled" : "failed"; Log.Info("Quick spelling stopped: " + e.GetType().Name); }
                finally
                {
                    if (session is not null) Interlocked.CompareExchange(ref _spelling, null, session);
                    RecordPerformance(new(DateTimeOffset.UtcNow, "quick-spelling", watch.ElapsedMilliseconds, [outcome]));
                }
            });
        }
    }
}
