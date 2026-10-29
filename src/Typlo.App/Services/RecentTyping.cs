using System.Text;
using Typlo.Core.Layouts;

namespace Typlo.App.Services;

/// <summary>Only the physical keys in the current input run; never persisted or logged.</summary>
internal sealed class RecentTyping
{
    internal const int PauseMilliseconds = 2000;
    private const int MaxKeys = 512;
    private readonly Lock _gate = new();
    private readonly StringBuilder _keys = new();
    private nint _window, _element;
    private long _lastAt, _version;
    private bool _overflow;

    public nint Window { get { lock (_gate) return _window; } }

    public void Record(char key, nint window, long now, long version, nint element = 0)
    {
        lock (_gate)
        {
            if (window == 0 || char.IsControl(key) || (key != ' ' && !KeyboardLayouts.English.Contains(key)))
            {
                Clear();
                _version = version;
                return;
            }
            if (window != _window || element != _element || now - _lastAt >= PauseMilliseconds ||
                (_keys.Length > 0 && char.IsWhiteSpace(_keys[^1]) && !char.IsWhiteSpace(key))) Clear();
            _window = window;
            _element = element;
            _lastAt = now;
            _version = version;
            if (_overflow)
            {
                if (char.IsWhiteSpace(key)) Clear();
                return;
            }
            if (_keys.Length == 0 && char.IsWhiteSpace(key)) return;
            if (_keys.Length >= MaxKeys)
            {
                _keys.Clear();
                _overflow = true;
                return;
            }
            _keys.Append(key);
        }
    }

    public void Backspace(nint window, long now, long version, nint element = 0)
    {
        lock (_gate)
        {
            if (window != _window || element != _element || now - _lastAt >= PauseMilliseconds || _overflow) Clear();
            else if (_keys.Length > 0) _keys.Length--;
            _lastAt = now;
            _version = version;
        }
    }

    public void Reset(long version)
    {
        lock (_gate) { Clear(); _version = version; }
    }

    public void Consume(long version)
    {
        lock (_gate) if (_version <= version) Clear();
    }

    public Run? Capture(nint window)
    {
        lock (_gate)
        {
            if (window == 0 || window != _window) { Clear(); return null; }
            return _keys.Length == 0 || _overflow ? null : new Run(window, _keys.ToString(), _element);
        }
    }

    private void Clear()
    {
        _keys.Clear();
        _window = 0;
        _element = 0;
        _overflow = false;
    }

    internal sealed record Run(nint Window, string Keys, nint Element = 0)
    {
        // Match against what the app actually copied. Dead keys, IME, or ignored keystrokes must never
        // make Typlo replace older text just because the number of physical key presses happens to fit.
        public KeyboardLayout? MatchSource(string text, IEnumerable<KeyboardLayout> layouts)
        {
            if (text.Length != Keys.Length) return null;
            foreach (var layout in layouts)
            {
                var matches = true;
                for (var i = 0; i < Keys.Length; i++)
                {
                    var code = KeyboardLayouts.English.CodeOf(Keys[i]);
                    var expected = Keys[i] == ' ' ? ' ' : code < 0 ? '\0' : layout.CharAt(code);
                    // Contenteditable editors use NBSP to retain a typed trailing space.
                    if (expected == ' ' && text[i] is '\u00a0' or '\u202f') continue;
                    if (char.ToLowerInvariant(text[i]) == char.ToLowerInvariant(expected)) continue;
                    matches = false;
                    break;
                }
                if (matches) return layout;
            }
            return null;
        }
    }
}
