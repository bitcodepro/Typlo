using Typlo.App.Services;
using Typlo.Core.Layouts;

namespace Typlo.Tests;

public sealed class RecentTypingTests
{
    [Fact]
    public void PauseStartsANewRun_ButWaitingForTheHotkeyDoesNotDiscardIt()
    {
        var input = new RecentTyping();
        input.Record('a', 1, 100, 1);
        input.Record('b', 1, 200, 2);
        Assert.Equal("ab", input.Capture(1)!.Keys);
        input.Record('?', 1, 200 + RecentTyping.PauseMilliseconds, 3);
        Assert.Equal("?", input.Capture(1)!.Keys);
        Assert.Equal("?", input.Capture(1)!.Keys); // capture has no inactivity expiry
    }

    [Fact]
    public void WordBoundaryRetainsTrailingSpacesUntilANewWordStarts()
    {
        var input = new RecentTyping();
        input.Record('a', 1, 100, 1);
        input.Record(' ', 1, 200, 2);
        Assert.Equal("a ", input.Capture(1)!.Keys);
        input.Record('?', 1, 300, 3);
        Assert.Equal("?", input.Capture(1)!.Keys);
    }

    [Fact]
    public void BackspaceNeverIncludesCharactersBeforeTheTrackedInput()
    {
        var input = new RecentTyping();
        input.Record('x', 1, 100, 1);
        input.Backspace(1, 200, 2);
        input.Backspace(1, 300, 3);
        input.Record('?', 1, 400, 4);
        Assert.Equal("?", input.Capture(1)!.Keys);
    }

    [Fact]
    public void WindowChangeAndResetDiscardThePreviousRun()
    {
        var input = new RecentTyping();
        input.Record('x', 1, 100, 1);
        input.Record('?', 2, 200, 2);
        Assert.Equal("?", input.Capture(2)!.Keys);
        Assert.Null(input.Capture(1));
        input.Record('a', 1, 300, 3);
        input.Reset(4);
        Assert.Null(input.Capture(1));
    }

    [Fact]
    public void DelayedCompletionCannotDiscardNewerInput()
    {
        var input = new RecentTyping();
        input.Record('x', 1, 100, 1);
        input.Reset(2);
        input.Record('?', 1, 200, 3);
        input.Consume(1);
        Assert.Equal("?", input.Capture(1)!.Keys);
        input.Consume(3);
        Assert.Null(input.Capture(1));
    }

    [Fact]
    public void OversizedRunsAreNotPartiallyConverted()
    {
        var input = new RecentTyping();
        for (var i = 0; i < 600; i++) input.Record('x', 1, 100 + i, i);
        Assert.Null(input.Capture(1));
        input.Record(' ', 1, 701, 601);
        input.Record('?', 1, 702, 602);
        Assert.Equal("?", input.Capture(1)!.Keys);
    }

    [Theory]
    [InlineData("?", "?", "en")]
    [InlineData("?", ",", "ru")]
    [InlineData("/", ".", "ru")]
    [InlineData(",", "б", "ru")]
    [InlineData("htdm.dftim", "ревьюваешь", "ru")]
    [InlineData("abc", "ФИС", "ru")]
    [InlineData("s", "і", "uk")]
    public void PhysicalKeysIdentifyTheLayoutOfTheCopiedInput(string keys, string text, string expected)
    {
        var run = new RecentTyping.Run(1, keys);
        Assert.Equal(expected, run.MatchSource(text, KeyboardLayouts.All)!.Id);
    }

    [Theory]
    [InlineData("?", "a")]
    [InlineData("abc", "old text")]
    [InlineData("abc", "猫猫猫")]
    public void MismatchingTextIsNeverAdopted(string keys, string text) =>
        Assert.Null(new RecentTyping.Run(1, keys).MatchSource(text, KeyboardLayouts.All));
}
