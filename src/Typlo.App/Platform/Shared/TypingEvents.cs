using SharpHook;
using SharpHook.Data;

namespace Typlo.App.Platform.Shared;

/// <summary>One typed character, or a signal that the typed text so far no longer leads up to the caret.</summary>
public readonly record struct TypedKey(char Char, bool Reset, bool Backspace)
{
    public static TypedKey ForChar(char c) => new(c, false, false);
    public static readonly TypedKey ResetKey = new('\0', true, false);
    public static readonly TypedKey BackspaceKey = new('\0', false, true);
}

/// <summary>
/// Turns SharpHook key presses into <see cref="TypedKey"/>s. Each key is reported as the character it has in the
/// US layout: snippet triggers are matched by physical key anyway, and this avoids per-layout character
/// translation (which libuiohook does not do for every event). Synthetic events are ignored.
/// </summary>
internal static class TypingEvents
{
    /// <summary>Self-test only: lets synthetic key presses count as typing.</summary>
    internal static bool AcceptSimulated { get; set; }

    private static readonly HashSet<KeyCode> ResetKeys =
    [
        KeyCode.VcEnter, KeyCode.VcEscape, KeyCode.VcTab, KeyCode.VcDelete, KeyCode.VcLeft, KeyCode.VcRight,
        KeyCode.VcUp, KeyCode.VcDown, KeyCode.VcHome, KeyCode.VcEnd, KeyCode.VcPageUp, KeyCode.VcPageDown,
    ];

    private static readonly Dictionary<KeyCode, (char Normal, char Shifted)> Punctuation = new()
    {
        [KeyCode.VcSpace] = (' ', ' '), [KeyCode.VcSemicolon] = (';', ':'), [KeyCode.VcQuote] = ('\'', '"'),
        [KeyCode.VcComma] = (',', '<'), [KeyCode.VcPeriod] = ('.', '>'), [KeyCode.VcSlash] = ('/', '?'),
        [KeyCode.VcMinus] = ('-', '_'), [KeyCode.VcEquals] = ('=', '+'), [KeyCode.VcOpenBracket] = ('[', '{'),
        [KeyCode.VcCloseBracket] = (']', '}'), [KeyCode.VcBackslash] = ('\\', '|'), [KeyCode.VcBackQuote] = ('`', '~'),
    };

    private const string ShiftedDigits = ")!@#$%^&*(";

    public static void Attach(IGlobalHook hook, Action<TypedKey> raise)
    {
        var down = new HashSet<KeyCode>();
        hook.KeyReleased += (_, e) => { lock (down) down.Remove(e.Data.KeyCode); };
        hook.KeyPressed += (_, e) =>
        {
            var code = e.Data.KeyCode;
            bool shift, shortcut;
            lock (down)
            {
                down.Add(code);
                shift = down.Contains(KeyCode.VcLeftShift) || down.Contains(KeyCode.VcRightShift);
                shortcut = down.Overlaps([KeyCode.VcLeftControl, KeyCode.VcRightControl, KeyCode.VcLeftMeta, KeyCode.VcRightMeta,
                    KeyCode.VcLeftAlt, KeyCode.VcRightAlt]);
            }
            if (e.IsEventSimulated && !AcceptSimulated) return;
            if (code == KeyCode.VcBackspace) { raise(TypedKey.BackspaceKey); return; }
            if (ResetKeys.Contains(code)) { raise(TypedKey.ResetKey); return; }
            if (ToChar(code, shift) is not { } c) return;
            // A shortcut (Ctrl+C, Alt+Tab…) is not typing, and the caret may have moved.
            raise(shortcut ? TypedKey.ResetKey : TypedKey.ForChar(c));
        };
        hook.MousePressed += (_, e) =>
        {
            if (!e.IsEventSimulated) raise(TypedKey.ResetKey); // the caret may have moved
        };
    }

    /// <summary>The character of a physical key in the US layout, or null for non-character keys.</summary>
    internal static char? ToChar(KeyCode code, bool shift)
    {
        var name = code.ToString();
        if (name.Length == 3 && name.StartsWith("Vc", StringComparison.Ordinal))
        {
            var c = name[2];
            if (char.IsAsciiLetterUpper(c)) return shift ? c : char.ToLowerInvariant(c);
            if (char.IsAsciiDigit(c)) return shift ? ShiftedDigits[c - '0'] : c;
        }
        return Punctuation.TryGetValue(code, out var p) ? (shift ? p.Shifted : p.Normal) : null;
    }
}
