using System.Collections.Concurrent;
using SharpHook.Data;

namespace Typlo.App.Platform.Shared;

/// <summary>
/// Remembers the key presses Typlo injects on platforms whose hook does not flag them (X11, macOS), so the typing
/// hook can drop exactly those events. Real presses in between are not swallowed: only a press matching the oldest
/// pending own event is consumed, and pending events expire if the hook never reports them.
/// </summary>
internal sealed class OwnInputTracker
{
    private const long ExpiryMs = 1000;
    // Code null: one character of injected text, whose key code the hook reports unpredictably.
    private readonly ConcurrentQueue<(KeyCode? Code, long At)> _pending = new();

    public void Press(KeyCode code, long now) => _pending.Enqueue((code, now));

    public void TextCharacter(long now) => _pending.Enqueue((null, now));

    /// <summary>True (and forgotten) if this press is the next own event; false for a real key press.</summary>
    public bool Consume(KeyCode code, long now)
    {
        while (_pending.TryPeek(out var head))
        {
            if (now - head.At > ExpiryMs) { _pending.TryDequeue(out _); continue; }
            if (head.Code is { } expected && expected != code) return false;
            _pending.TryDequeue(out _);
            return true;
        }
        return false;
    }
}
