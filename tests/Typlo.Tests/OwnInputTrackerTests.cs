using SharpHook.Data;
using Typlo.App.Platform.Shared;

namespace Typlo.Tests;

public class OwnInputTrackerTests
{
    [Fact]
    public void OwnPresses_AreConsumedInOrder_RealPressesAreNot()
    {
        var t = new OwnInputTracker();
        t.Press(KeyCode.VcBackspace, 0);
        t.Press(KeyCode.VcBackspace, 0);
        Assert.False(t.Consume(KeyCode.VcA, 5)); // a real key while our Backspaces are pending
        Assert.True(t.Consume(KeyCode.VcBackspace, 5));
        Assert.True(t.Consume(KeyCode.VcBackspace, 6));
        Assert.False(t.Consume(KeyCode.VcBackspace, 7)); // the user's own Backspace
    }

    [Fact]
    public void InjectedText_IsConsumedPerCharacter_ThenTheNextTriggerIsSeen()
    {
        // Review P3: no time window after text entry; a snippet trigger typed right after is not swallowed.
        var t = new OwnInputTracker();
        foreach (var _ in "Hi!") t.TextCharacter(0);
        Assert.True(t.Consume(KeyCode.VcUndefined, 1));
        Assert.True(t.Consume(KeyCode.VcI, 2));
        Assert.True(t.Consume(KeyCode.Vc1, 3));
        Assert.False(t.Consume(KeyCode.VcSemicolon, 4)); // the user starts the next trigger at once
        Assert.False(t.Consume(KeyCode.VcH, 5));
    }

    [Fact]
    public void ModifiersAreNotTracked_SoAShiftedTextCharacterDoesNotShiftTheQueue()
    {
        // X11 text entry of "H" sends Shift + H; the typing hook skips modifier presses before consulting the tracker.
        var t = new OwnInputTracker();
        foreach (var _ in "Hi") t.TextCharacter(0);
        Assert.True(t.Consume(KeyCode.VcH, 1));
        Assert.True(t.Consume(KeyCode.VcI, 2));
        Assert.False(t.Consume(KeyCode.VcE, 3));
    }

    [Fact]
    public void Unreported_OwnEvents_Expire()
    {
        var t = new OwnInputTracker();
        t.Press(KeyCode.VcLeft, 0);
        Assert.False(t.Consume(KeyCode.VcLeft, 5000));
    }
}
