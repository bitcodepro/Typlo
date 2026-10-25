using SharpHook.Data;
using Typlo.App.Platform.Shared;

namespace Typlo.Tests;

public class OwnInputTrackerTests
{
    private const ushort TextRaw = 250; // the spare X11 key code text entry goes through

    [Fact]
    public void OwnPresses_AreConsumedInOrder_RealPressesAreNot()
    {
        var t = new OwnInputTracker();
        t.Press(KeyCode.VcBackspace, 0);
        t.Press(KeyCode.VcBackspace, 0);
        Assert.False(t.Consume(KeyCode.VcA, 38, 5)); // a real key while our Backspaces are pending
        Assert.True(t.Consume(KeyCode.VcBackspace, 22, 5));
        Assert.True(t.Consume(KeyCode.VcBackspace, 22, 6));
        Assert.False(t.Consume(KeyCode.VcBackspace, 22, 7)); // the user's own Backspace
    }

    [Fact]
    public void InjectedText_MatchesOnlyItsOwnKeyCode_SoARealKeyBeforeItIsNotSwallowed()
    {
        // Review P2: libuiohook waits before injecting each character; a real Left in that interval is user input.
        var t = new OwnInputTracker();
        t.TextKey(TextRaw, 0);
        Assert.False(t.Consume(KeyCode.VcLeft, 113, 10)); // real navigation: not ours
        Assert.True(t.Consume(KeyCode.VcUndefined, TextRaw, 50)); // the injected character
        Assert.False(t.Consume(KeyCode.VcSemicolon, 47, 60)); // the user starts the next trigger at once
    }

    [Fact]
    public void InjectedText_IsConsumedPerKeyEvent_ThenTheNextTriggerIsSeen()
    {
        var t = new OwnInputTracker();
        for (var i = OwnInputTracker.X11KeyEvents("Hi!"); i > 0; i--) t.TextKey(TextRaw, 0);
        Assert.True(t.Consume(KeyCode.VcUndefined, TextRaw, 1));
        Assert.True(t.Consume(KeyCode.VcUndefined, TextRaw, 2));
        Assert.True(t.Consume(KeyCode.VcUndefined, TextRaw, 3));
        Assert.False(t.Consume(KeyCode.VcSemicolon, 47, 4));
    }

    [Theory]
    [InlineData("Hi", 2)]
    [InlineData("\U0001F600", 1)] // a surrogate pair is one character, one key event
    [InlineData("e\u0301", 2)] // a combining mark is a character of its own
    [InlineData("\U0001F44D\U0001F3FD", 2)] // emoji + skin tone modifier: one text element, two scalars
    [InlineData("a\u0000b", 2)] // no key symbol for NUL: no event
    public void X11KeyEvents_CountsTheEventsTextEntrySends(string text, int events)
        => Assert.Equal(events, OwnInputTracker.X11KeyEvents(text));

    [Fact]
    public void AnEmoji_LeavesNoExtraPendingEvent_ThatCouldSwallowTheNextRealKey()
    {
        // Review P2: two UTF-16 units used to queue two entries for one key event.
        var t = new OwnInputTracker();
        for (var i = OwnInputTracker.X11KeyEvents("\U0001F600"); i > 0; i--) t.TextKey(TextRaw, 0);
        Assert.True(t.Consume(KeyCode.VcUndefined, TextRaw, 1));
        Assert.False(t.Consume(KeyCode.VcSemicolon, 47, 2));
    }

    [Fact]
    public void Unreported_OwnEvents_Expire()
    {
        var t = new OwnInputTracker();
        t.Press(KeyCode.VcLeft, 0);
        Assert.False(t.Consume(KeyCode.VcLeft, 113, 5000));
    }
}
