namespace Typlo.App.Services;

/// <summary>Local work keeps an on-demand UI alive through result publication and cleanup.</summary>
internal sealed class UiOperations
{
    internal static UiOperations Shared { get; } = new();
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private TaskCompletionSource _drained = Completed();
    private int _count;
    private bool _stopping;
    internal bool IsBusy { get { lock (_gate) return _count != 0; } }
    internal Lease? Begin()
    {
        lock (_gate)
        {
            if (_stopping) return null;
            if (_count++ == 0) _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new(this, _stop.Token);
        }
    }
    internal async Task StopAsync()
    {
        Task drained;
        lock (_gate) { _stopping = true; drained = _drained.Task; }
        await _stop.CancelAsync().ConfigureAwait(false);
        await drained.ConfigureAwait(false);
    }
    private static TaskCompletionSource Completed()
    { var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); source.SetResult(); return source; }
    internal sealed class Lease(UiOperations owner, CancellationToken token) : IDisposable
    {
        private UiOperations? _owner = owner;
        internal CancellationToken Token => token;
        public void Dispose()
        {
            var value = Interlocked.Exchange(ref _owner, null);
            if (value is null) return;
            lock (value._gate) if (--value._count == 0) value._drained.TrySetResult();
        }
    }
}
