using System.Diagnostics;

namespace Typlo.App.Services;

/// <summary>Return large, dead clipboard buffers after a burst, once idle; never trim working-set pages.</summary>
internal sealed class IdleMemoryReclaimer(Func<bool> idle) : IDisposable
{
    private readonly Lock _lock = new();
    private Timer? _timer;
    private bool _disposed;
    private long _lastCollection = Environment.TickCount64 - 15000;
    internal void Schedule()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _timer ??= new Timer(_ => Reclaim(), null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(2000, Timeout.Infinite);
        }
    }
    private void Reclaim()
    {
        lock (_lock)
        {
            if (_disposed) return;
            var elapsed = Environment.TickCount64 - _lastCollection;
            if (!idle() || elapsed < 15000)
            {
                _timer?.Change((int)Math.Max(2000, 15000 - elapsed), Timeout.Infinite); return;
            }
            _lastCollection = Environment.TickCount64;
        }
        var timing = Stopwatch.StartNew();
        // The image job is complete and its buffers are unreachable. Aggressive asks the GC to
        // decommit empty regions, unlike EmptyWorkingSet which only hides resident pages.
        try
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            Log.Info($"Released large clipboard buffers in {timing.ElapsedMilliseconds} ms");
        }
        catch (Exception e) { Log.Error("Releasing clipboard buffers failed", e); }
    }
    public void Dispose() { lock (_lock) { _disposed = true; _timer?.Dispose(); } }
}
