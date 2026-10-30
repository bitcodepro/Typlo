using SharpHook.Data;
using Typlo.Core.Hotkeys;

namespace Typlo.App.Platform.Windows;

// Only the second modifier of a plain Ctrl+Shift gesture is withheld from Windows.
// If another key follows, replay that modifier before allowing the real key through.
// Thus Ctrl+Shift+C/Arrow etc. retain their ordinary meaning; no system preference is edited.
internal sealed class WindowsShortcutInterceptor
{
    private readonly Lock _gate = new();
    private Dictionary<Hotkey, int> _bindings = [];
    private readonly HashSet<KeyCode> _down = [];
    private readonly HashSet<KeyCode> _swallowed = [];
    private KeyCode? _deferred;
    private bool _pending, _used;
    private nint _origin;
    private static readonly Hotkey Cycle = new(KeyModifiers.Ctrl | KeyModifiers.Shift, "");
    private static readonly Hotkey Capture = new(KeyModifiers.Win | KeyModifiers.Shift, "S");
    internal readonly record struct Decision(bool Suppress, ushort ReplayDown = 0, int? Fire = null);

    internal static bool Supports(Hotkey key) => key == Cycle || key == Capture;
    internal bool Register(int id, Hotkey key)
    {
        lock (_gate)
        {
            if (!Supports(key) || _bindings.Any(b => b.Key == key && b.Value != id)) return false;
            foreach (var old in _bindings.Where(b => b.Value == id).Select(b => b.Key).ToArray()) _bindings.Remove(old);
            _bindings[key] = id; return true;
        }
    }
    internal void Clear() { lock (_gate) _bindings.Clear(); }
    internal KeyModifiers ModifiersDown { get { lock (_gate) return Modifiers(); } }
    internal bool IsRegistered(Hotkey key) { lock (_gate) return _bindings.ContainsKey(key); }
    internal bool OwnsModifierGesture(nint window)
    { lock (_gate) return _pending && _origin == window && _bindings.ContainsKey(Cycle); }

    internal Decision Handle(KeyCode code, bool up, nint window)
    {
        lock (_gate)
        {
            if (up)
            {
                _down.Remove(code);
                var suppressed = _swallowed.Remove(code);
                if (_deferred == code) _deferred = null;
                int? fire = null;
                if (_pending && !_down.Any(IsCtrlShift))
                {
                    if (_origin == window && _bindings.TryGetValue(Cycle, out var id)) fire = id;
                    _pending = false;
                }
                if (_down.Count == 0) _used = false;
                return new(suppressed, Fire: fire);
            }
            if (!_down.Add(code)) return new(_swallowed.Contains(code)); // repeat never retriggers
            var mods = Modifiers();
            if (code == KeyCode.VcS && mods == Capture.Modifiers && _bindings.TryGetValue(Capture, out var capture))
            { _used = true; _swallowed.Add(code); return new(true, Fire: capture); }
            if (_pending)
            {
                if (_deferred is null && IsCtrlShift(code) && _down.Count == 2)
                { _deferred = code; _swallowed.Add(code); return new(true); }
                var replay = _deferred is { } key ? VirtualModifier(key) : (ushort)0;
                if (_deferred is { } deferred) _swallowed.Remove(deferred);
                _deferred = null; _pending = false; _used = true;
                return new(false, ReplayDown: replay);
            }
            if (!_used && IsCtrlShift(code) && mods == Cycle.Modifiers && _down.Count == 2 && _bindings.ContainsKey(Cycle))
            {
                _pending = true; _origin = window; _deferred = code; _swallowed.Add(code);
                return new(true);
            }
            if (!IsCtrlShift(code)) _used = true;
            return new(false);
        }
    }
    internal ushort ReleaseForShutdown()
    {
        lock (_gate)
        {
            var replay = _deferred is { } key ? VirtualModifier(key) : (ushort)0;
            _bindings.Clear(); _down.Clear(); _swallowed.Clear(); _deferred = null; _pending = false;
            return replay;
        }
    }
    private KeyModifiers Modifiers()
    {
        var mods = KeyModifiers.None;
        foreach (var key in _down)
            mods |= key switch
            {
                KeyCode.VcLeftControl or KeyCode.VcRightControl => KeyModifiers.Ctrl,
                KeyCode.VcLeftShift or KeyCode.VcRightShift => KeyModifiers.Shift,
                KeyCode.VcLeftAlt or KeyCode.VcRightAlt => KeyModifiers.Alt,
                KeyCode.VcLeftMeta or KeyCode.VcRightMeta => KeyModifiers.Win,
                _ => KeyModifiers.None,
            };
        return mods;
    }
    private static bool IsCtrlShift(KeyCode key) => VirtualModifier(key) != 0;
    private static ushort VirtualModifier(KeyCode key) => key switch
    {
        KeyCode.VcLeftControl => 0xa2, KeyCode.VcRightControl => 0xa3,
        KeyCode.VcLeftShift => 0xa0, KeyCode.VcRightShift => 0xa1, _ => 0,
    };
}
