using Avalonia.Controls;
using Avalonia.Input;
using Typlo.Core.Hotkeys;
using AvKeyModifiers = Avalonia.Input.KeyModifiers;
using HkModifiers = Typlo.Core.Hotkeys.KeyModifiers;

namespace Typlo.App.Views;

/// <summary>A read-only text box that records a key combination when focused. Backspace/Delete clears it.</summary>
public sealed class HotkeyBox : TextBox
{
    protected override Type StyleKeyOverride => typeof(TextBox);

    public HotkeyBox()
    {
        IsReadOnly = true;
        PlaceholderText = Services.Loc.T("hotkey.placeholder");
        Cursor = new Cursor(StandardCursorType.Hand);
        LostFocus += (_, _) => { _modifiers.Clear(); _modifierCandidate = false; };
    }

    public event Action? HotkeyChanged;
    private bool _modifierCandidate;
    private readonly HashSet<Key> _modifiers = [];

    public string HotkeyText
    {
        get => Text ?? "";
        set => Text = value;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift)
        {
            _modifiers.Add(e.Key);
            if (_modifiers.Any(k => k is Key.LeftCtrl or Key.RightCtrl) && _modifiers.Any(k => k is Key.LeftShift or Key.RightShift) &&
                (e.KeyModifiers & (AvKeyModifiers.Alt | AvKeyModifiers.Meta)) == 0) _modifierCandidate = true;
            return;
        }
        _modifierCandidate = false;
        if (e.KeyModifiers == AvKeyModifiers.None && e.Key is Key.Back or Key.Delete or Key.Escape)
        {
            if (e.Key != Key.Escape) { Text = ""; HotkeyChanged?.Invoke(); }
            return;
        }
        if (e.Key == Key.Tab) { e.Handled = false; base.OnKeyDown(e); return; }

        var name = KeyName(e.Key);
        if (name is null) return; // a lone modifier: wait for the real key

        var mods = HkModifiers.None;
        if (e.KeyModifiers.HasFlag(AvKeyModifiers.Control)) mods |= HkModifiers.Ctrl;
        if (e.KeyModifiers.HasFlag(AvKeyModifiers.Alt)) mods |= HkModifiers.Alt;
        if (e.KeyModifiers.HasFlag(AvKeyModifiers.Shift)) mods |= HkModifiers.Shift;
        if (e.KeyModifiers.HasFlag(AvKeyModifiers.Meta)) mods |= HkModifiers.Win;
        Text = new Hotkey(mods, name).ToString();
        HotkeyChanged?.Invoke();
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        _modifiers.Remove(e.Key);
        if (_modifierCandidate && _modifiers.Count == 0)
        {
            _modifierCandidate = false; Text = "Ctrl+Shift"; HotkeyChanged?.Invoke(); e.Handled = true;
        }
        base.OnKeyUp(e);
    }

    private static string? KeyName(Key key) => key switch
    {
        >= Key.A and <= Key.Z => ((char)('A' + (key - Key.A))).ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.F1 and <= Key.F24 => "F" + (key - Key.F1 + 1),
        Key.Enter => "Enter",
        Key.Escape => "Escape",
        Key.Space => "Space",
        Key.Insert => "Insert",
        Key.Delete => "Delete",
        Key.Home => "Home",
        Key.End => "End",
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        Key.Up => "Up",
        Key.Down => "Down",
        Key.Left => "Left",
        Key.Right => "Right",
        Key.Pause => "Pause",
        Key.OemTilde => "Tilde",
        Key.OemMinus => "Minus",
        Key.OemPlus => "Plus",
        Key.OemComma => "Comma",
        Key.OemPeriod => "Period",
        Key.OemQuestion => "Slash",
        Key.OemPipe or Key.OemBackslash => "Backslash",
        Key.OemSemicolon => "Semicolon",
        Key.OemQuotes => "Quote",
        Key.OemOpenBrackets => "OpenBracket",
        Key.OemCloseBrackets => "CloseBracket",
        _ => null,
    };
}
