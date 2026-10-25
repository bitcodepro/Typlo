using System.Collections.Concurrent;
using SharpHook.Data;

namespace Typlo.App.Platform.Shared;

/// <summary>
/// Remembers the key presses Typlo injects on X11, whose hook does not flag them (XTest events look real), so the
/// typing hook can drop exactly those events. Every pending event names the key the hook will report: an injected
/// key by its key code, a character of injected text by the spare raw key code text entry goes through. A real press
/// in between never matches the head and stays user input; pending events expire if the hook never reports them.
/// </summary>
internal sealed class OwnInputTracker
{
    private const long ExpiryMs = 1000;
    private readonly ConcurrentQueue<(KeyCode? Code, ushort? Raw, long At)> _pending = new();

    public void Press(KeyCode code, long now) => _pending.Enqueue((code, null, now));

    /// <summary>One key event of injected text, reported with the raw key code it is typed through.</summary>
    public void TextKey(ushort rawCode, long now) => _pending.Enqueue((null, rawCode, now));

    /// <summary>True (and forgotten) if this press is the next own event; false for a real key press.</summary>
    public bool Consume(KeyCode code, ushort rawCode, long now)
    {
        while (_pending.TryPeek(out var head))
        {
            if (now - head.At > ExpiryMs) { _pending.TryDequeue(out _); continue; }
            if (head.Code is { } expected ? expected != code : head.Raw != rawCode) return false;
            _pending.TryDequeue(out _);
            return true;
        }
        return false;
    }

    /// <summary>
    /// How many key presses libuiohook's X11 text entry sends for <paramref name="text"/>: one per Unicode scalar
    /// (a surrogate pair is one character), except the control characters xkb has no key symbol for.
    /// </summary>
    public static int X11KeyEvents(string text)
    {
        var count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var v = rune.Value;
            var noSymbol = (v < 0x20 && v is not (0x08 or 0x09 or 0x0A or 0x0B or 0x0D or 0x1B)) || v is >= 0x80 and < 0xA0
                || v is >= 0xFDD0 and <= 0xFDEF || (v & 0xFFFE) == 0xFFFE;
            if (!noSymbol) count++;
        }
        return count;
    }
}
