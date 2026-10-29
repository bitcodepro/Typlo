using Typlo.App.Platform.Shared;

namespace Typlo.App.Services;

public sealed partial class AppController
{
    private CopyInputObservation? _pendingCopyInput;
    private sealed record CopyInterruption(bool PlainTyping, ulong? SequenceBeforeInput);
    private sealed class CopyInputObservation : IDisposable
    {
        private readonly AppController _owner;
        private readonly CopyInputObservation? _previous;
        private CopyInterruption? _first;
        internal CopyInputObservation(AppController owner)
        { _owner = owner; _previous = Interlocked.Exchange(ref owner._pendingCopyInput, this); }
        internal void Observe(TypedKey key)
        {
            if (Volatile.Read(ref _first) is not null) return;
            var typing = !key.Reset && !key.Backspace && key.Shortcut is null && !char.IsControl(key.Char);
            var interruption = new CopyInterruption(typing, typing ? _owner._platform.ImmediateClipboardSequence : null);
            Interlocked.CompareExchange(ref _first, interruption, null);
        }
        internal bool CopyCompletedBeforeTyping(ulong sequence) =>
            Volatile.Read(ref _first) is { PlainTyping: true, SequenceBeforeInput: { } before } && before == sequence;
        public void Dispose() => Interlocked.CompareExchange(ref _owner._pendingCopyInput, _previous, this);
    }
}
