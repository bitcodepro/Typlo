using Typlo.App.Platform;
using Typlo.Core.Storage;

namespace Typlo.App.Services;

/// <summary>At most one image is being encoded and one newer image is waiting; a burst cannot grow a task queue.</summary>
internal sealed class LatestImageWorker(Action<RawClip, ClipCaptureOrder> process) : IDisposable
{
    private readonly Lock _lock = new();
    private (RawClip Clip, ClipCaptureOrder Order)? _pending;
    private Task? _worker;
    private bool _disposed;
    internal bool IsBusy { get { lock (_lock) return _worker is not null; } }
    internal void Post(RawClip image, ClipCaptureOrder order)
    {
        lock (_lock)
        {
            if (_disposed) return;
            _pending = (image, order);
            _worker ??= Task.Run(Drain);
        }
    }
    private void Drain()
    {
        while (true)
        {
            (RawClip Clip, ClipCaptureOrder Order) image;
            lock (_lock)
            {
                if (_disposed || _pending is null) { _worker = null; return; }
                image = _pending.Value; _pending = null;
            }
            try { process(image.Clip, image.Order); }
            catch (Exception e) { Log.Error("Image processing failed", e); }
        }
    }
    public void Dispose()
    {
        Task? worker;
        lock (_lock) { _disposed = true; _pending = null; worker = _worker; }
        worker?.GetAwaiter().GetResult();
    }
}
