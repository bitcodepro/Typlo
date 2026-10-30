using SharpHook;
using SharpHook.Data;
using Typlo.App.Platform.Shared;
using Typlo.Core.Hotkeys;

namespace Typlo.Tests;

/// <summary>Runs key sequences through the real hook translator (no native hook is started).</summary>
public class TypingEventsTests
{
    internal sealed class FakeHook() : GlobalHookBase(SharpHook.Providers.UioHookProvider.Instance)
    {
        public void Press(KeyCode code, ushort raw = 0, bool simulated = false) => Raise(EventType.KeyPressed, code, raw, simulated);
        public void Release(KeyCode code, bool simulated = false) => Raise(EventType.KeyReleased, code, 0, simulated);

        private void Raise(EventType type, KeyCode code, ushort raw, bool simulated)
        {
            var e = new UioHookEvent { Type = type, Mask = simulated ? EventMask.SimulatedEvent : EventMask.None };
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

    [Theory]
    [InlineData(KeyCode.VcLeftAlt)]
    [InlineData(KeyCode.VcLeftControl)]
    public void ModifierOnlyLayoutSwitch_IsAnInputBoundary(KeyCode modifier)
    {
        _hook.Press(modifier);
        _hook.Press(KeyCode.VcLeftShift);
        _hook.Release(KeyCode.VcLeftShift);
        _hook.Release(modifier);
        Assert.Equal([TypedKey.LayoutGestureKey], _raised);
    }

    [Fact]
    public void FullTyploShortcut_DoesNotProduceAnExtraLayoutReset()
    {
        _hook.Press(KeyCode.VcLeftControl);
        _hook.Press(KeyCode.VcLeftAlt);
        _hook.Press(KeyCode.VcLeftShift);
        _hook.Press(KeyCode.VcSpace);
        _hook.Release(KeyCode.VcSpace);
        _hook.Release(KeyCode.VcLeftShift);
        _hook.Release(KeyCode.VcLeftAlt);
        _hook.Release(KeyCode.VcLeftControl);
        Assert.Equal(Hotkey.TryParse("Ctrl+Alt+Shift+Space"), Assert.Single(_raised).Shortcut);
    }

    [Fact]
    public void HeldDefaultShortcut_SyntheticCtrlTapAndForcedReleases_DoNotResetInput()
    {
        _hook.Press(KeyCode.VcLeftControl);
        _hook.Press(KeyCode.VcLeftAlt);
        _hook.Press(KeyCode.VcLeftShift);
        _hook.Press(KeyCode.VcSpace);
        _hook.Release(KeyCode.VcSpace);
        // WinPlatform.WaitForModifiersReleasedAsync taps Ctrl, then releases the held modifiers.
        _hook.Press(KeyCode.VcLeftControl, simulated: true);
        _hook.Release(KeyCode.VcLeftControl, simulated: true);
        _hook.Release(KeyCode.VcLeftControl, simulated: true);
        _hook.Release(KeyCode.VcLeftShift, simulated: true);
        _hook.Release(KeyCode.VcLeftAlt, simulated: true);
        _hook.Release(KeyCode.VcLeftControl);
        _hook.Release(KeyCode.VcLeftShift);
        _hook.Release(KeyCode.VcLeftAlt);
        Assert.Equal(Hotkey.TryParse("Ctrl+Alt+Shift+Space"), Assert.Single(_raised).Shortcut);
        _hook.Press(KeyCode.VcA);
        Assert.Equal(TypedKey.ForChar('a'), _raised[^1]); // no modifier remains logically stuck
    }

    [Fact]
    public void SyntheticReleases_DoNotCompleteAPhysicalLayoutSwitch()
    {
        _hook.Press(KeyCode.VcLeftControl);
        _hook.Press(KeyCode.VcLeftShift);
        _hook.Release(KeyCode.VcLeftShift, simulated: true);
        _hook.Release(KeyCode.VcLeftControl, simulated: true);
        Assert.Empty(_raised);
        _hook.Release(KeyCode.VcLeftShift);
        _hook.Release(KeyCode.VcLeftControl);
        Assert.Equal([TypedKey.LayoutGestureKey], _raised);
    }

    [Fact]
    public void InjectedCopy_DoesNotCancelAPhysicalLayoutSwitch()
    {
        _hook.Press(KeyCode.VcLeftControl);
        _hook.Press(KeyCode.VcLeftShift);
        _hook.Press(KeyCode.VcC, simulated: true);
        _hook.Release(KeyCode.VcC, simulated: true);
        _hook.Release(KeyCode.VcLeftShift);
        _hook.Release(KeyCode.VcLeftControl);
        Assert.Equal([TypedKey.LayoutGestureKey], _raised);
    }

    [Theory]
    [InlineData(KeyCode.VcLeftAlt)]
    [InlineData(KeyCode.VcLeftControl)]
    public void PhysicalReleaseClassificationKeepsLayoutCancellationDistinctFromCaretMovement(KeyCode modifier)
    {
        var hook = new FakeHook(); var observed = new List<TypedKey>();
        TypingEvents.Attach(hook, observed.Add, isPhysicalEvent: _ => true);
        hook.Press(modifier, simulated: true); hook.Press(KeyCode.VcLeftShift, simulated: true);
        hook.Release(KeyCode.VcLeftShift, simulated: true); hook.Release(modifier, simulated: true);
        var change = Assert.Single(observed);
        Assert.True(change.Reset); Assert.True(change.LayoutGesture);
        hook.Press(KeyCode.VcLeft); hook.Release(KeyCode.VcLeft);
        Assert.Equal(TypedKey.ResetKey, observed[^1]); Assert.False(observed[^1].LayoutGesture);
        hook.Press(KeyCode.VcBackspace);
        Assert.False(observed[^1].LayoutGesture);
    }

    [Fact]
    public void BareFunctionKeyIsReportedSoItsOwnHotkeyCanBeRecognized()
    {
        _hook.Press(KeyCode.VcF9);
        Assert.Equal(Hotkey.TryParse("F9"), Assert.Single(_raised).Shortcut);
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
