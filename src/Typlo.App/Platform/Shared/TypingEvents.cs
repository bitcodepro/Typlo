using SharpHook;
using SharpHook.Data;

namespace Typlo.App.Platform.Shared;

/// <summary>One typed character, or a signal that the typed text so far no longer leads up to the caret.</summary>
public readonly record struct TypedKey(char Char, bool Reset, bool Backspace, Typlo.Core.Hotkeys.Hotkey? Shortcut = null)
{
    public static TypedKey ForChar(char c) => new(c, false, false);
    public static readonly TypedKey ResetKey = new('\0', true, false);
    public static readonly TypedKey BackspaceKey = new('\0', false, true);
    /// <summary>A key combination such as Ctrl+V or Ctrl+Alt+Shift+Z; it may move the caret (unless it is Typlo's own hotkey).</summary>
    public static TypedKey ForShortcut(Typlo.Core.Hotkeys.Hotkey? shortcut) => new('\0', true, false, shortcut);
}

/// <summary>
/// Turns SharpHook key presses into <see cref="TypedKey"/>s. Each key is reported as the character it has in the
/// US layout: snippet triggers are matched by physical key anyway, and this avoids per-layout character
/// translation (which libuiohook does not do for every event). Synthetic events are ignored.
/// </summary>
internal static class TypingEvents
{
    /// <summary>
    /// Self-test only: plain synthetic character keys count as typing (the test "types" through SendInput).
    /// Synthetic shortcuts, Backspace and navigation keys stay ignored, so Typlo's own keys never count.
    /// </summary>
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

    /// <param name="isOwnPress">
    /// Consumes a key press (key code, raw code) Typlo itself injected, on platforms whose hook does not flag them.
    /// </param>
    public static void Attach(IGlobalHook hook, Action<TypedKey> raise, Func<KeyCode, ushort, bool>? isOwnPress = null)
    {
        var down = new HashSet<KeyCode>();
        var layoutChordPending = false;
        hook.KeyReleased += (_, e) =>
        {
            bool reset;
            lock (down)
            {
                down.Remove(e.Data.KeyCode);
                reset = layoutChordPending && !down.Any(IsModifier);
                if (reset) layoutChordPending = false;
            }
            // Modifier-only Alt+Shift / Ctrl+Shift commonly changes the input layout. A full hotkey
            // clears the pending chord on its main key, so Typlo's own shortcuts preserve the input run.
            if (reset) raise(TypedKey.ResetKey);
        };
        hook.KeyPressed += (_, e) =>
        {
            var code = e.Data.KeyCode;
            bool shift, shortcut;
            var mods = Typlo.Core.Hotkeys.KeyModifiers.None;
            lock (down)
            {
                down.Add(code);
                shift = down.Contains(KeyCode.VcLeftShift) || down.Contains(KeyCode.VcRightShift);
                if (down.Contains(KeyCode.VcLeftControl) || down.Contains(KeyCode.VcRightControl)) mods |= Typlo.Core.Hotkeys.KeyModifiers.Ctrl;
                if (down.Contains(KeyCode.VcLeftAlt) || down.Contains(KeyCode.VcRightAlt)) mods |= Typlo.Core.Hotkeys.KeyModifiers.Alt;
                if (down.Contains(KeyCode.VcLeftMeta) || down.Contains(KeyCode.VcRightMeta)) mods |= Typlo.Core.Hotkeys.KeyModifiers.Win;
                shortcut = mods != Typlo.Core.Hotkeys.KeyModifiers.None;
                if (shift) mods |= Typlo.Core.Hotkeys.KeyModifiers.Shift;
                if (!IsModifier(code)) layoutChordPending = false;
                else if (shift && (mods & (Typlo.Core.Hotkeys.KeyModifiers.Ctrl | Typlo.Core.Hotkeys.KeyModifiers.Alt)) != 0)
                    layoutChordPending = true;
            }
            // Modifier presses are never typing; they are also not tracked as own input (text entry may add a Shift).
            if (IsModifier(code)) return;
            if (isOwnPress?.Invoke(code, e.Data.RawCode) == true) return;
            if (e.IsEventSimulated && !(AcceptSimulated && !shortcut && ToChar(code, shift) is not null)) return;
            if (shortcut)
            {
                // Not typing, and the caret may have moved, unless the controller recognises it as its own hotkey.
                // Checked before Backspace and navigation keys, so a hotkey such as Ctrl+Alt+Shift+End is recognised.
                var name = KeyCodes.ToName(code);
                raise(TypedKey.ForShortcut(name is null ? null : new Typlo.Core.Hotkeys.Hotkey(mods, name)));
                return;
            }
            if (code == KeyCode.VcBackspace) { raise(TypedKey.BackspaceKey); return; }
            if (ResetKeys.Contains(code)) { raise(TypedKey.ResetKey); return; }
            if (ToChar(code, shift) is not { } c)
            {
                var name = KeyCodes.ToName(code);
                raise(TypedKey.ForShortcut(name is null ? null : new Typlo.Core.Hotkeys.Hotkey(mods, name)));
                return;
            }
            raise(TypedKey.ForChar(c));
        };
        hook.MousePressed += (_, e) =>
        {
            if (!e.IsEventSimulated) raise(TypedKey.ResetKey); // the caret may have moved
        };
    }

    private static bool IsModifier(KeyCode code) => code is KeyCode.VcLeftControl or KeyCode.VcRightControl or KeyCode.VcLeftAlt
        or KeyCode.VcRightAlt or KeyCode.VcLeftShift or KeyCode.VcRightShift or KeyCode.VcLeftMeta or KeyCode.VcRightMeta;

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
