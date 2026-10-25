using SharpHook;
using SharpHook.Data;
using Typlo.App.Platform.Shared;
using Typlo.Core.Hotkeys;

namespace Typlo.Tests;

/// <summary>Runs key sequences through the real hook translator (no native hook is started).</summary>
public class TypingEventsTests
{
    private sealed class FakeHook() : GlobalHookBase(SharpHook.Providers.UioHookProvider.Instance)
    {
        public void Press(KeyCode code, ushort raw = 0) => Raise(EventType.KeyPressed, code, raw);
        public void Release(KeyCode code) => Raise(EventType.KeyReleased, code, 0);

        private void Raise(EventType type, KeyCode code, ushort raw)
        {
            var e = new UioHookEvent { Type = type, Mask = EventMask.None };
            e.Keyboard.KeyCode = code;
            e.Keyboard.RawCode = raw;
            DispatchEvent(ref e);
        }

        protected override void HandleHookEvent(ref UioHookEvent e) => DispatchEvent(ref e);
    }

    private readonly FakeHook _hook = new();
    private readonly List<TypedKey> _raised = [];
    private readonly OwnInputTracker _own = new();

    public TypingEventsTests() =>
        TypingEvents.Attach(_hook, _raised.Add, (code, raw) => _own.Consume(code, raw, Environment.TickCount64));

    [Fact]
    public void NavigationKeyHotkey_IsReportedAsThatShortcut_NotAsABareReset()
    {
        // Review P3: Backspace/navigation keys were handled before shortcut recognition, so an Undo hotkey such as
        // Ctrl+Alt+Shift+End reached the controller as a plain reset and invalidated the conversion.
        _hook.Press(KeyCode.VcLeftControl);
        _hook.Press(KeyCode.VcLeftAlt);
        _hook.Press(KeyCode.VcLeftShift);
        _hook.Press(KeyCode.VcEnd);
        var key = Assert.Single(_raised);
        Assert.Equal(Hotkey.TryParse("Ctrl+Alt+Shift+End"), key.Shortcut);
        Assert.True(key.Reset); // still not typing
    }

    [Fact]
    public void BareNavigationAndBackspace_StayResetAndBackspace()
    {
        _hook.Press(KeyCode.VcEnd);
        _hook.Release(KeyCode.VcEnd);
        _hook.Press(KeyCode.VcBackspace);
        Assert.Equal([TypedKey.ResetKey, TypedKey.BackspaceKey], _raised);
    }

    [Fact]
    public void RealNavigationBeforeInjectedText_IsUserInput_AndTheInjectedCharacterIsNot()
    {
        // Review P2: a real Left during libuiohook's pre-injection wait must invalidate, not be taken for our text.
        _own.TextKey(250, Environment.TickCount64);
        _hook.Press(KeyCode.VcLeft, 113);
        _hook.Press(KeyCode.VcUndefined, 250);
        _hook.Press(KeyCode.VcSemicolon, 47);
        Assert.Equal([TypedKey.ResetKey, TypedKey.ForChar(';')], _raised);
    }
}
