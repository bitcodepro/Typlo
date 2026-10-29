using Typlo.Core.Layouts;
using static Typlo.Core.Layouts.KeyboardLayouts;

namespace Typlo.Tests;

public class LayoutConverterTests
{
    private readonly LayoutConverter _conv = new([English, Russian]);

    [Theory]
    [InlineData("ghbdtn", "привет")]
    [InlineData("ghb", "при")]
    [InlineData("htdm.dftim", "ревьюваешь")]
    [InlineData("Ghbdtn? rfr ltkf&", "Привет, как дела?")]
    [InlineData("z vtyz", "я меня")]
    [InlineData("`", "ё")]
    [InlineData("~", "Ё")]
    [InlineData("tot hfp/", "еще раз.")]
    [InlineData("ПРИВЕТ", "ПРИВЕТ")] // already Russian
    public void ToRussian(string input, string expected) =>
        Assert.Equal(expected, _conv.ConvertTo(input, Russian).Text);

    [Theory]
    [InlineData("руддщ", "hello")]
    [InlineData("Руддщ Цщкдв!", "Hello World!")]
    [InlineData("ыуд.", "sel/")]
    [InlineData("hello", "hello")] // already English
    public void ToEnglish(string input, string expected) =>
        Assert.Equal(expected, _conv.ConvertTo(input, English).Text);

    [Fact]
    public void MixedText_KeepsWordsAlreadyInTarget()
    {
        var r = _conv.ConvertTo("Привет vbh", Russian);
        Assert.Equal("Привет мир", r.Text);
    }

    [Fact]
    public void PunctuationUsesTheSourceKnownFromTypedKeys()
    {
        Assert.Equal(",", _conv.Toggle("?", English).Text);
        Assert.Equal("/", _conv.Toggle(".", Russian).Text);
        Assert.Equal("?", _conv.ConvertTo("?", English, English).Text);
    }

    [Fact]
    public void AlreadyInTarget_ReportsUnchanged()
    {
        var r = _conv.ConvertTo("hello world", English);
        Assert.False(r.Changed);
        Assert.Equal("hello world", r.Text);
    }

    [Fact]
    public void PreservesWhitespaceAndNewlines()
    {
        Assert.Equal("привет\r\n\tмир  ", _conv.ConvertTo("ghbdtn\r\n\tvbh  ", Russian).Text);
    }

    [Fact]
    public void Toggle_GoesBothWays()
    {
        var there = _conv.Toggle("ghbdtn");
        Assert.Equal("привет", there.Text);
        Assert.Same(Russian, there.Target);
        Assert.Equal("ghbdtn", _conv.Toggle(there.Text).Text);
    }

    [Fact]
    public void RoundTrip_AllKeys()
    {
        var all = English.Normal + English.Shifted;
        var ru = LayoutConverter.Convert(all, English, Russian);
        Assert.Equal(Russian.Normal + Russian.Shifted, ru);
    }

    [Fact]
    public void Ukrainian_Works()
    {
        var conv = new LayoutConverter([English, Ukrainian]);
        Assert.Equal("їжак", conv.ConvertTo("];fr", Ukrainian).Text);
        Assert.Equal("ghbdsn", conv.ConvertTo("привіт", English).Text);
    }

    [Fact]
    public void AllLayouts_HaveSameKeyCount()
    {
        foreach (var l in All) Assert.Equal(English.Normal.Length, l.Normal.Length);
    }
}
