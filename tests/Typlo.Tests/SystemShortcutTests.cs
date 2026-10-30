using SharpHook.Data;
using Typlo.App.Platform.Shared;
using Typlo.App.Platform.Windows;
using Typlo.Core.Hotkeys;
using Typlo.Core.Settings;

namespace Typlo.Tests;

public sealed class SystemShortcutTests
{
    [Fact]
    public void ModifierOnlyShortcutRoundTripsWithoutAllowingBareModifiers()
    {
        var chord = Hotkey.TryParse("Shift+Control");
        Assert.NotNull(chord); Assert.True(chord.IsUsable); Assert.True(chord.IsModifierOnly);
        Assert.Equal("Ctrl+Shift", chord.ToString());
        Assert.Null(Hotkey.TryParse("Ctrl")); Assert.Null(Hotkey.TryParse("Alt+Shift")); Assert.Null(Hotkey.TryParse("Win"));
    }
    [Theory]
    [InlineData(KeyCode.VcLeftControl, KeyCode.VcLeftShift, true)]
    [InlineData(KeyCode.VcLeftShift, KeyCode.VcLeftControl, false)]
    [InlineData(KeyCode.VcRightControl, KeyCode.VcRightShift, true)]
    [InlineData(KeyCode.VcRightShift, KeyCode.VcRightControl, false)]
    public void PureCtrlShiftIsSwallowedAndFiresOnceOnlyAfterBothReleases(KeyCode first, KeyCode second, bool firstUpFirst)
    {
        var state = new WindowsShortcutInterceptor(); Assert.True(state.Register(8, Hotkey.TryParse("Ctrl+Shift")!));
        Assert.False(state.Handle(first, false, 1).Suppress);
        Assert.True(state.Handle(second, false, 1).Suppress);
        Assert.Null(state.Handle(second, false, 1).Fire); // repeat
        Assert.Null(state.Handle(firstUpFirst ? first : second, true, 1).Fire);
        var done = state.Handle(firstUpFirst ? second : first, true, 1);
        Assert.Equal(8, done.Fire);
        Assert.Null(state.Handle(first, true, 1).Fire);
    }
    [Theory]
    [InlineData(KeyCode.VcC)]
    [InlineData(KeyCode.VcLeft)]
    [InlineData(KeyCode.VcRight)]
    [InlineData(KeyCode.VcS)]
    [InlineData(KeyCode.VcLeftAlt)]
    public void LongerShortcutsReplayTheDeferredModifierAndNeverCycle(KeyCode main)
    {
        var state = new WindowsShortcutInterceptor(); state.Register(8, Hotkey.TryParse("Ctrl+Shift")!);
        state.Handle(KeyCode.VcLeftControl, false, 1); state.Handle(KeyCode.VcLeftShift, false, 1);
        var replay = state.Handle(main, false, 1);
        Assert.False(replay.Suppress); Assert.Equal(0xa0, replay.ReplayDown); Assert.Null(replay.Fire);
        state.Handle(main, true, 1);
        Assert.False(state.Handle(KeyCode.VcLeftShift, true, 1).Suppress);
        Assert.Null(state.Handle(KeyCode.VcLeftControl, true, 1).Fire);
    }
    [Fact]
    public void ScreenshotSuppressesSDownRepeatAndUpButDoesNotConsumeOtherWinShortcuts()
    {
        var state = new WindowsShortcutInterceptor(); state.Register(6, Hotkey.TryParse("Win+Shift+S")!);
        state.Handle(KeyCode.VcLeftMeta, false, 1); state.Handle(KeyCode.VcLeftShift, false, 1);
        Assert.Equal(6, state.Handle(KeyCode.VcS, false, 1).Fire);
        Assert.Null(state.Handle(KeyCode.VcS, false, 1).Fire);
        Assert.True(state.Handle(KeyCode.VcS, true, 1).Suppress);
        Assert.False(state.Handle(KeyCode.VcA, false, 1).Suppress);
    }
    [Fact]
    public void SuspensionAndWindowChangesNeverDispatchAnOldGesture()
    {
        var state = new WindowsShortcutInterceptor(); state.Register(8, Hotkey.TryParse("Ctrl+Shift")!);
        state.Handle(KeyCode.VcLeftControl, false, 1); state.Handle(KeyCode.VcLeftShift, false, 1);
        Assert.True(state.Handle(KeyCode.VcLeftShift, true, 2).Suppress);
        Assert.Null(state.Handle(KeyCode.VcLeftControl, true, 2).Fire);
        state.Handle(KeyCode.VcLeftControl, false, 1); state.Handle(KeyCode.VcLeftShift, false, 1); state.Clear();
        Assert.True(state.Handle(KeyCode.VcLeftShift, true, 1).Suppress);
        Assert.Null(state.Handle(KeyCode.VcLeftControl, true, 1).Fire);
    }
    [Fact]
    public void OwnedModifierGesturePreservesTheInputRunThroughTheRealTranslator()
    {
        var state = new WindowsShortcutInterceptor(); state.Register(8, Hotkey.TryParse("Ctrl+Shift")!);
        var hook = new TypingEventsTests.FakeHook(); var typed = new List<TypedKey>();
        TypingEvents.Attach(hook, typed.Add, ownsModifierShortcut: key => key.IsModifierOnly && state.OwnsModifierGesture(1));
        hook.KeyPressed += (_, e) => state.Handle(e.Data.KeyCode, false, 1);
        hook.KeyReleased += (_, e) => state.Handle(e.Data.KeyCode, true, 1);
        hook.Press(KeyCode.VcLeftControl); hook.Press(KeyCode.VcLeftShift);
        hook.Release(KeyCode.VcLeftShift); hook.Release(KeyCode.VcLeftControl);
        Assert.Equal(Hotkey.TryParse("Ctrl+Shift"), Assert.Single(typed).Shortcut);
    }
    [Fact]
    public void DefaultsMigrateOnceAndRetainCustomAndDisabledBindings()
    {
        var settings = new AppSettings(); settings.InitializeWindowsShortcuts();
        Assert.Equal("Ctrl+Shift", settings.LayoutSwitchHotkey); Assert.Equal("Win+Shift+S", settings.ScreenshotCopyHotkey);
        settings.LayoutSwitchHotkey = ""; settings.ScreenshotCopyHotkey = "F13"; settings.InitializeWindowsShortcuts();
        Assert.Empty(settings.LayoutSwitchHotkey); Assert.Equal("F13", settings.ScreenshotCopyHotkey);
        var custom = new AppSettings { LayoutSwitchHotkey = "F14", ScreenshotCopyHotkey = "F15" };
        custom.InitializeWindowsShortcuts(); Assert.Equal("F14", custom.LayoutSwitchHotkey); Assert.Equal("F15", custom.ScreenshotCopyHotkey);
    }
}
