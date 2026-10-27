using System.Globalization;
using System.Diagnostics;
using System.Text;
using SharpHook.Data;
using Typlo.App.Platform;
using Typlo.App.Platform.Shared;
using Typlo.App.Services;
using Typlo.Core;
using Typlo.Core.Hotkeys;
using Typlo.Core.Layouts;
using Typlo.Core.Storage;
using Typlo.Core.Text;

namespace Typlo.Tests;

public sealed class AppControllerTests : IDisposable
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "typlo-controller-" + Guid.NewGuid().ToString("N"));
    private readonly FakePlatform _platform = new();
    private readonly AppController _controller;

    public AppControllerTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
        _controller = new(_platform, new AppPaths(_dir), new PlainKeyProtector(), diagnostic: true);
    }

    [Fact]
    public async Task ThreeLanguageCycle_AdvancesAcrossIdenticalRuUaText_WithSystemSwitchDisabled()
    {
        _platform.Installed = [KeyboardLayouts.English, KeyboardLayouts.Ukrainian, KeyboardLayouts.Russian];
        _controller.RefreshKeyboardLayouts();
        var settings = _controller.Settings.Clone();
        settings.Layouts = ["en", "uk", "ru"];
        settings.SwitchSystemLayout = false;
        _controller.ApplySettings(settings);
        _platform.Type("ghbdtn");
        foreach (var expected in new[] { "привет", "привет", "ghbdtn", "привет" })
        {
            await _controller.ConvertSelectionAsync(null);
            Assert.Equal(expected, _platform.Document.Text);
        }
    }

    [Fact]
    public async Task ActiveSystemLayout_DisambiguatesCommonRuUaWords()
    {
        _platform.Installed = [KeyboardLayouts.English, KeyboardLayouts.Ukrainian, KeyboardLayouts.Russian];
        _platform.ActiveLayoutId = "ru";
        _controller.RefreshKeyboardLayouts();
        var settings = _controller.Settings.Clone();
        settings.Layouts = ["en", "uk", "ru"];
        _controller.ApplySettings(settings);
        _platform.TypeAs("ghbdtn", "привет");
        await _controller.ConvertSelectionAsync(null);
        Assert.Equal("ghbdtn", _platform.Document.Text); // RU is last in the cycle, so next is EN
    }

    [Fact]
    public async Task FocusChangeDuringPasteSettle_DoesNotSwitchTheOtherApplicationsLayout()
    {
        _platform.Type("ghbdtn");
        _platform.AfterPaste = () => { _platform.Window = 2; _platform.Document.Text = "other"; };
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Null(_platform.ActiveLayoutId);
        Assert.Equal("other", _platform.Document.Text);
    }

    [Fact]
    public void FiveInstalledLayouts_InitializeOnlyThreeShortcuts()
    {
        var en = KeyboardLayouts.English;
        var platform = new FakePlatform { Installed = Enumerable.Range(0, 5)
            .Select(i => new KeyboardLayout("native:" + i, "Language " + i, i, en.Normal, en.Shifted, "lang" + i)).ToArray() };
        using var controller = new AppController(platform, new AppPaths(Path.Combine(_dir, "five")), new PlainKeyProtector(), diagnostic: true);
        Assert.Equal(5, controller.AvailableLayouts.Count);
        Assert.Equal(5, controller.Settings.Layouts.Count);
        Assert.Equal(3, controller.Settings.LayoutHotkeys.Count(h => h.Hotkey.Length > 0));
    }

    [Fact]
    public async Task InputMethodHotkey_SwitchesTheSourceWithoutReadingOrReplacingText()
    {
        var ime = new KeyboardLayout("native:ime", "Input method", 0, "", "", "ja", supportsConversion: false);
        var settings = _controller.Settings.Clone();
        settings.SwitchSystemLayout = false; // this setting controls the post-conversion switch, not explicit IME selection
        _controller.ApplySettings(settings);
        _platform.Document.Text = "existing";
        await _controller.ConvertSelectionAsync(ime);
        Assert.Equal("native:ime", _platform.ActiveLayoutId);
        Assert.Equal("existing", _platform.Document.Text);
        Assert.Equal(0, _platform.PasteCount);
        Assert.Equal(1ul, _platform.ClipboardSequence);
    }

    [Theory]
    [InlineData("Hello, {cursor}!\n\nRegards,\n", "Hello, !\n\nRegards,\n", 7)]
    [InlineData("{cursor}first\r\n\tsecond\r\n", "first\r\n\tsecond\r\n", 0)]
    [InlineData("end{cursor}", "end", 3)]
    [InlineData("before{cursor}😀e\u0301\r\nx", "before😀e\u0301\r\nx", 6)]
    public async Task SnippetTemplate_PlacesCursorAndPreservesMultilineFormatting(string template, string expected, int offset)
    {
        _platform.Document.Text = "keep ;hi";
        _platform.Document.Caret = _platform.Document.Text.Length;
        await _controller.ExpandSnippetAsync(new Snippet { Trigger = ";hi", Text = template }, 1);
        Assert.Equal("keep " + expected, _platform.Document.Text);
        Assert.Equal(5 + offset, _platform.Document.Caret);
        Assert.Empty(_platform.Document.Selection);
        Assert.Equal("original clipboard", _platform.ClipboardText);
    }

    [Fact]
    public async Task SnippetTemplate_UserInputDuringPasteSettle_PreventsLateCursorMovement()
    {
        _platform.Document.Text = ";hi";
        _platform.Document.Caret = 3;
        _platform.AfterPaste = () => _platform.Type("X");
        await _controller.ExpandSnippetAsync(new Snippet { Trigger = ";hi", Text = "before{cursor}after" }, 1);
        Assert.Equal("beforeafterX", _platform.Document.Text);
        Assert.Equal(_platform.Document.Text.Length, _platform.Document.Caret);
        Assert.Empty(_platform.Document.Selection);
    }

    [Fact]
    public async Task SnippetTemplate_EditorChangedTheSuffix_DoesNotGuessTheCursorPosition()
    {
        _platform.Document.Text = ";hi";
        _platform.Document.Caret = 3;
        _platform.AfterPaste = () => _platform.Document.Type("auto");
        await _controller.ExpandSnippetAsync(new Snippet { Trigger = ";hi", Text = "before{cursor}after" }, 1);
        Assert.Equal("beforeafterauto", _platform.Document.Text);
        Assert.Equal(_platform.Document.Text.Length, _platform.Document.Caret);
        Assert.Empty(_platform.Document.Selection);
    }

    [Fact]
    public async Task SnippetTemplate_ExternalCopyBeforeCursorPlacement_IsPreserved()
    {
        _platform.Document.Text = ";hi";
        _platform.Document.Caret = 3;
        _platform.AfterPaste = () => _platform.ExternalCopy("new user copy", owner: 99);
        await _controller.ExpandSnippetAsync(new Snippet { Trigger = ";hi", Text = "before{cursor}after" }, 1);
        Assert.Equal("beforeafter", _platform.Document.Text);
        Assert.Equal("new user copy", _platform.ClipboardText);
        Assert.Equal(_platform.Document.Text.Length, _platform.Document.Caret);
    }

    [Fact]
    public async Task SnippetTemplate_MultipleMarkers_DeleteNothing()
    {
        _platform.Document.Text = ";hi";
        _platform.Document.Caret = 3;
        await _controller.ExpandSnippetAsync(new Snippet { Trigger = ";hi", Text = "x{cursor}y{cursor}" }, 1);
        Assert.Equal(";hi", _platform.Document.Text);
        Assert.Equal(0, _platform.BackspaceCount);
    }

    [Fact]
    public async Task SnippetTemplate_FocusChangeDuringCursorSelection_StopsWithoutMovingTheOtherCaret()
    {
        _platform.Document.Text = ";hi";
        _platform.Document.Caret = 3;
        _platform.OnLeft = () =>
        {
            _platform.Window = 2;
            _platform.Document.Text = "other";
            _platform.Document.Caret = 2;
        };
        await _controller.ExpandSnippetAsync(new Snippet { Trigger = ";hi", Text = "before{cursor}after" }, 1);
        Assert.Equal("other", _platform.Document.Text);
        Assert.Equal(2, _platform.Document.Caret);
    }

    [Fact]
    public async Task Snippets_DoNotExpandWhileEditingSettings()
    {
        EnableSnippet();
        _controller.SuspendHotkeys();
        _platform.Type(";hi");
        await Task.Delay(100);
        Assert.Equal(";hi", _platform.Document.Text);
        Assert.Equal(0, _platform.BackspaceCount);
        _controller.RegisterHotkeys();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecentConversion_DoesNotWaitASecondForTheHotkeyModifiers(bool modifiersHeld)
    {
        _platform.Document.Text = "existing ";
        _platform.Document.Caret = 9;
        _platform.Type("ghbdtn");
        _platform.ModifiersHeld = modifiersHeld;
        var watch = Stopwatch.StartNew();
        TimeSpan? untilPaste = null;
        _platform.AfterPaste = () => untilPaste = watch.Elapsed;

        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);

        _output.WriteLine($"Hotkey to replacement: {untilPaste?.TotalMilliseconds:F0} ms; completed: {watch.Elapsed.TotalMilliseconds:F0} ms; modifiers held: {modifiersHeld}");
        Assert.Equal("existing привет", _platform.Document.Text);
        Assert.Equal("original clipboard", _platform.ClipboardText);
        Assert.NotNull(untilPaste);
        Assert.True(untilPaste.Value < TimeSpan.FromMilliseconds(600), $"Replacement took {untilPaste.Value.TotalMilliseconds:F0} ms");
    }

    [Fact]
    public async Task HeldDefaultShortcut_ActualInjectedModifierSequence_PreservesConversionAndUndo()
    {
        using var hook = new TypingEventsTests.FakeHook();
        TypingEvents.Attach(hook, _platform.UserKey);
        _platform.CopyLineWhenUnselected = true;
        _platform.Type("ghbdtn");
        hook.Press(KeyCode.VcLeftControl);
        hook.Press(KeyCode.VcLeftAlt);
        hook.Press(KeyCode.VcLeftShift);
        hook.Press(KeyCode.VcSpace);
        hook.Release(KeyCode.VcSpace);
        _platform.BeforeModifiers = () =>
        {
            hook.Press(KeyCode.VcLeftControl, simulated: true);
            hook.Release(KeyCode.VcLeftControl, simulated: true);
            hook.Release(KeyCode.VcLeftControl, simulated: true);
            hook.Release(KeyCode.VcLeftShift, simulated: true);
            hook.Release(KeyCode.VcLeftAlt, simulated: true);
            return Task.CompletedTask;
        };
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal("привет", _platform.Document.Text);
        hook.Release(KeyCode.VcLeftControl);
        hook.Release(KeyCode.VcLeftShift);
        hook.Release(KeyCode.VcLeftAlt);
        // Repeat the same forced-release path while Undo already has a valid result to preserve.
        hook.Press(KeyCode.VcLeftControl);
        hook.Press(KeyCode.VcLeftAlt);
        hook.Press(KeyCode.VcLeftShift);
        hook.Press(KeyCode.VcZ);
        hook.Release(KeyCode.VcZ);
        await _controller.UndoLastTransformAsync();
        hook.Release(KeyCode.VcLeftControl);
        hook.Release(KeyCode.VcLeftShift);
        hook.Release(KeyCode.VcLeftAlt);
        Assert.Equal("ghbdtn", _platform.Document.Text);
        Assert.Equal("original clipboard", _platform.ClipboardText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConsecutiveCaseCommands_CycleTheSameRangeWithoutReselection(bool explicitSelection)
    {
        _platform.Document.Text = "keep ";
        _platform.Document.Caret = 5;
        if (explicitSelection)
        {
            _platform.Document.Text += "hello";
            _platform.Document.Anchor = 5;
            _platform.Document.Caret = 10;
        }
        else _platform.Type("hello");
        foreach (var expected in new[] { "keep Hello", "keep HELLO", "keep hello" })
        {
            await _controller.ChangeCaseAsync();
            Assert.Equal(expected, _platform.Document.Text);
            Assert.Empty(_platform.Document.Selection);
        }
        await _controller.UndoLastTransformAsync();
        Assert.Equal("keep HELLO", _platform.Document.Text);
        Assert.Equal("original clipboard", _platform.ClipboardText);
    }

    [Fact]
    public async Task ConsecutiveLayoutCommands_ToggleTheSameRangeWithoutReselection()
    {
        _platform.Document.Text = "keep ";
        _platform.Document.Caret = 5;
        _platform.Type("ghbdtn");
        foreach (var expected in new[] { "keep привет", "keep ghbdtn", "keep привет" })
        {
            await _controller.ConvertSelectionAsync(null);
            Assert.Equal(expected, _platform.Document.Text);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConsecutiveExplicitLayoutCommand_AfterTransientActivation_SwitchesOnlyIfTheRangeIsVerified(bool loseSelection)
    {
        _platform.WaitForTransientFocus = true;
        _platform.Type("ghbdtn");
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal("привет", _platform.Document.Text);
        Assert.Equal(KeyboardLayouts.Russian.Id, _platform.ActiveLayoutId);
        Task recovery = Task.CompletedTask;
        _platform.OnLeft = () =>
        {
            _platform.OnLeft = null;
            _platform.Window = 2;
            _platform.Document.Text = "notification";
            recovery = Task.Run(async () =>
            {
                await Task.Delay(80);
                if (loseSelection) _platform.DocumentFor(1).Anchor = null; // finish the editor transition first
                _platform.Window = 1;
            });
        };
        await _controller.ConvertSelectionAsync(KeyboardLayouts.English);
        await recovery;
        Assert.Equal(loseSelection ? "привет" : "ghbdtn", _platform.Document.Text);
        Assert.Equal(loseSelection ? KeyboardLayouts.Russian.Id : KeyboardLayouts.English.Id, _platform.ActiveLayoutId);
        Assert.Equal(loseSelection ? 1 : 2, _platform.PasteCount);
        Assert.Empty(_platform.Document.Selection);
        Assert.Equal("original clipboard", _platform.ClipboardText);
        Assert.Equal(0, _platform.Activations);
        if (loseSelection)
        {
            await _controller.UndoLastTransformAsync();
            Assert.Equal("привет", _platform.Document.Text);
            Assert.Equal(1, _platform.PasteCount); // the failed record must also be discarded
        }
        _platform.Window = 2;
        Assert.Equal("notification", _platform.Document.Text);
    }

    [Fact]
    public async Task ConsecutiveExplicitLayoutCommand_ChangedResultDoesNotSwitchLayout()
    {
        _platform.Type("ghbdtn");
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        _platform.Document.Text = "другое"; // editor changed the result without a keyboard event
        await _controller.ConvertSelectionAsync(KeyboardLayouts.English);
        await _controller.UndoLastTransformAsync();
        Assert.Equal("другое", _platform.Document.Text);
        Assert.Equal(KeyboardLayouts.Russian.Id, _platform.ActiveLayoutId);
        Assert.Equal(1, _platform.PasteCount);
        Assert.Empty(_platform.Document.Selection);
        Assert.Equal("original clipboard", _platform.ClipboardText);
    }

    [Fact]
    public async Task ExplicitLayoutCommand_WithoutATrackedRangeStillSwitchesLayout()
    {
        _platform.ActiveLayoutId = KeyboardLayouts.Russian.Id;
        _platform.Document.Text = "existing text";
        _platform.Document.Caret = _platform.Document.Text.Length;
        await _controller.ConvertSelectionAsync(KeyboardLayouts.English);
        Assert.Equal("existing text", _platform.Document.Text);
        Assert.Equal(KeyboardLayouts.English.Id, _platform.ActiveLayoutId);
        Assert.Equal(0, _platform.PasteCount);
        Assert.Equal("original clipboard", _platform.ClipboardText);
    }

    [Fact]
    public async Task ConsecutiveTransliterationCommands_UseTheChangedLengthOfTheResult()
    {
        _platform.Document.Text = "keep щука";
        _platform.Document.Anchor = 5;
        _platform.Document.Caret = _platform.Document.Text.Length;
        foreach (var expected in new[] { "keep shchuka", "keep щука", "keep shchuka" })
        {
            await _controller.TransliterateAsync();
            Assert.Equal(expected, _platform.Document.Text);
        }
    }

    [Fact]
    public async Task ConsecutivePunctuationCommands_PreserveTheKnownSourceLayout()
    {
        _platform.Document.Text = "ready";
        _platform.Document.Caret = 5;
        _platform.Type("?");
        foreach (var expected in new[] { "ready,", "ready?", "ready," })
        {
            await _controller.ConvertSelectionAsync(null);
            Assert.Equal(expected, _platform.Document.Text);
        }
    }

    [Fact]
    public async Task ConsecutiveCommand_RevalidatesTheResultBeforeReplacingIt()
    {
        _platform.Type("abc");
        await _controller.ChangeCaseAsync();
        _platform.Document.Text = "xyz"; // editor changed text without a keyboard event
        await _controller.ChangeCaseAsync();
        await _controller.UndoLastTransformAsync();
        Assert.Equal("xyz", _platform.Document.Text);
        Assert.Equal(1, _platform.PasteCount);
        Assert.Empty(_platform.Document.Selection);
    }

    [Fact]
    public async Task ConsecutiveCommand_UserSelectionDuringRevalidation_IsNotOverwritten()
    {
        _platform.Document.Text = "abc keep";
        _platform.Document.Anchor = 0;
        _platform.Document.Caret = 3;
        await _controller.ChangeCaseAsync();
        _platform.OnLeft = () =>
        {
            _platform.OnLeft = null;
            _platform.Document.Anchor = 4;
            _platform.UserMoveCaret(8, keepAnchor: true);
        };
        await _controller.ChangeCaseAsync();
        Assert.Equal("Abc keep", _platform.Document.Text);
        Assert.Equal("keep", _platform.Document.Selection);
        Assert.Equal(1, _platform.PasteCount);
    }

    [Fact]
    public async Task ConsecutiveCommand_ExternalCopyDuringRevalidation_IsKept()
    {
        _platform.Type("abc");
        await _controller.ChangeCaseAsync();
        _platform.OnLeft = () => _platform.ExternalCopy("user copy", owner: 99);
        await _controller.ChangeCaseAsync();
        Assert.Equal("Abc", _platform.Document.Text);
        Assert.Equal("user copy", _platform.ClipboardText);
        Assert.Equal(1, _platform.PasteCount);
        Assert.Empty(_platform.Document.Selection);
    }

    [Fact]
    public async Task ConsecutiveCommand_NoChangePreservesTheRangeForTheNextCommand()
    {
        _platform.Type("ghbdtn");
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian); // already in Russian
        await _controller.ConvertSelectionAsync(null);
        Assert.Equal("ghbdtn", _platform.Document.Text);
        Assert.Equal(2, _platform.PasteCount);
    }

    [Fact]
    public async Task ConsecutiveCommand_GraphemesAndLineBreaksDoNotExtendTheRange()
    {
        _platform.Document.Text = "keep 😀abc\r\ndef";
        _platform.Document.Anchor = 5;
        _platform.Document.Caret = _platform.Document.Text.Length;
        await _controller.ChangeCaseAsync();
        await _controller.ChangeCaseAsync();
        Assert.Equal("keep 😀ABC\r\nDEF", _platform.Document.Text);
        Assert.Equal("original clipboard", _platform.ClipboardText);
    }

    [Fact]
    public async Task ConsecutiveCommand_CaretMovementInvalidatesTheOldRange()
    {
        _platform.Type("abc");
        await _controller.ChangeCaseAsync();
        _platform.UserMoveCaret(3);
        _platform.CopyLineWhenUnselected = true;
        await _controller.ChangeCaseAsync();
        Assert.Equal("Abc", _platform.Document.Text);
        Assert.Equal(1, _platform.PasteCount);
    }

    [Fact]
    public async Task ConsecutiveCommand_WindowChangeInvalidatesTheOldRange()
    {
        _platform.Type("abc");
        await _controller.ChangeCaseAsync();
        _platform.Window = 2;
        _platform.Document.Text = "Abc";
        _platform.Document.Caret = 3;
        _platform.CopyLineWhenUnselected = true;
        await _controller.ChangeCaseAsync();
        _platform.Window = 1;
        await _controller.ChangeCaseAsync();
        Assert.Equal("Abc", _platform.Document.Text);
        Assert.Equal(1, _platform.PasteCount);
    }

    [Fact]
    public async Task RecentSelection_UserInputDuringSelection_StopsBeforeCopyOrPaste()
    {
        _platform.Type("ghbdtn");
        _platform.OnLeft = () => _platform.Type("X");
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal("ghbdtX", _platform.Document.Text);
        Assert.Equal(0, _platform.PasteCount);
        Assert.Equal("original clipboard", _platform.ClipboardText);
    }

    [Fact]
    public async Task RecentSelection_ExternalCopyDuringSelection_IsKeptAndStopsTheReplacement()
    {
        _platform.Type("ghbdtn");
        _platform.OnLeft = () => _platform.ExternalCopy("new user copy", owner: 99);
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal("ghbdtn", _platform.Document.Text);
        Assert.Empty(_platform.Document.Selection);
        Assert.Equal(0, _platform.PasteCount);
        Assert.Equal("new user copy", _platform.ClipboardText);
    }

    [Fact]
    public async Task RecentSelection_FocusChangeDuringSelection_LeavesTheOtherWindowAlone()
    {
        _platform.Type("ghbdtn");
        _platform.OnLeft = () =>
        {
            _platform.Window = 2;
            _platform.Document.Text = "other window";
            _platform.Document.SelectAll();
        };
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal("other window", _platform.Document.Text);
        Assert.Equal("other window", _platform.Document.Selection);
        Assert.Equal(0, _platform.PasteCount);
        Assert.Equal("original clipboard", _platform.ClipboardText);
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("copy")]
    [InlineData("paste")]
    public async Task TransientActivation_ResumesOnlyInTheOriginalWindow(string phase)
    {
        _platform.WaitForTransientFocus = true;
        _platform.Type("ghbdtn");
        Task recovery = Task.CompletedTask;
        void Interrupt()
        {
            _platform.Window = 2;
            _platform.Document.Text = "notification";
            recovery = Task.Run(async () => { await Task.Delay(80); _platform.Window = 1; });
        }
        if (phase == "selection") _platform.OnLeft = () => { _platform.OnLeft = null; Interrupt(); };
        if (phase == "copy") _platform.AfterCopy = () => { _platform.AfterCopy = null; Interrupt(); };
        if (phase == "paste") _platform.AfterWrite = text => { if (text == "привет") { _platform.AfterWrite = null; Interrupt(); } };
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        await recovery;
        Assert.Equal("привет", _platform.Document.Text);
        Assert.Equal(1, _platform.PasteCount);
        Assert.Equal(0, _platform.Activations); // Recovery must not take focus back from another application.
        _platform.Window = 2;
        Assert.Equal("notification", _platform.Document.Text);
    }

    [Fact]
    public async Task UserInputDuringTransientActivation_CancelsEvenIfTheTargetReturns()
    {
        _platform.WaitForTransientFocus = true;
        _platform.Type("ghbdtn");
        Task recovery = Task.CompletedTask;
        _platform.OnLeft = () =>
        {
            _platform.OnLeft = null;
            _platform.Window = 2;
            _platform.Document.Text = "other window";
            recovery = Task.Run(async () =>
            {
                await Task.Delay(20);
                _platform.UserKey(TypedKey.ResetKey); // A real click/key, unlike passive window activation.
                await Task.Delay(40);
                _platform.Window = 1;
            });
        };
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        await recovery;
        Assert.Equal("ghbdtn", _platform.Document.Text);
        Assert.Equal(0, _platform.PasteCount);
        Assert.Equal(0, _platform.Activations);
    }

    [Fact]
    public async Task ExternalCopyDuringTransientActivation_IsPreserved()
    {
        _platform.WaitForTransientFocus = true;
        _platform.Type("ghbdtn");
        Task recovery = Task.CompletedTask;
        _platform.OnLeft = () =>
        {
            _platform.OnLeft = null;
            _platform.Window = 2;
            recovery = Task.Run(async () =>
            {
                await Task.Delay(20);
                _platform.ExternalCopy("external copy", owner: 99);
                await Task.Delay(40);
                _platform.Window = 1;
            });
        };
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        await recovery;
        Assert.Equal("ghbdtn", _platform.Document.Text);
        Assert.Equal(0, _platform.PasteCount);
        Assert.Equal("external copy", _platform.ClipboardText);
    }

    [Fact]
    public async Task PersistentFocusChange_TimesOutWithoutTypingOrTakingFocusBack()
    {
        _platform.WaitForTransientFocus = true;
        _platform.Type("ghbdtn");
        _platform.OnLeft = () => { _platform.OnLeft = null; _platform.Window = 2; _platform.Document.Text = "other window"; };
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal((nint)2, _platform.Window);
        Assert.Equal("other window", _platform.Document.Text);
        Assert.Equal(0, _platform.PasteCount);
        Assert.Equal(0, _platform.Activations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedSelection_UnverifiedOrCollapsedRangeIsNeverExtended(bool collapsed)
    {
        _platform.WaitForTransientFocus = true;
        _platform.Type("ghbdtn");
        Task recovery = Task.CompletedTask;
        _platform.OnLeft = () =>
        {
            _platform.OnLeft = null;
            _platform.Window = 2;
            recovery = Task.Run(async () =>
            {
                await Task.Delay(40);
                var document = _platform.DocumentFor(1);
                if (collapsed) document.Anchor = null; // clear before publishing the restored foreground
                _platform.Window = 1;
            });
        };
        if (!collapsed) _platform.AfterCopy = () => _platform.ExternalCopy("not the typed suffix", owner: 1);
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        await recovery;
        Assert.Equal("ghbdtn", _platform.Document.Text);
        Assert.Equal(0, _platform.PasteCount);
        Assert.Null(_platform.ActiveLayoutId);
    }

    // The second sequence read is the clipboard settle boundary, before the next foreground
    // observation. These hooks complete the entire blur/return there; no timers race the controller.
    private void AtNextCopySettle(Action action)
    {
        _platform.AfterCopy = () =>
        {
            _platform.AfterCopy = null;
            AtSettle(action);
        };
    }

    private void AtSettle(Action action)
    {
        var reads = 0;
        _platform.OnSequenceRead = () =>
        {
            if (++reads != 2) return;
            _platform.OnSequenceRead = null;
            action();
        };
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("paste")]
    public async Task SelectionLostDuringBlur_IsRecheckedBeforeReplacement(string phase)
    {
        _platform.WaitForTransientFocus = true;
        _platform.Type("ghbdtn");
        if (phase == "copy") AtNextCopySettle(() => _platform.BlurAndReturn(loseSelection: true));
        else _platform.AfterWrite = text =>
        {
            if (text != "привет") return;
            _platform.AfterWrite = null;
            _platform.BlurAndReturn(loseSelection: true);
        };
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal("ghbdtn", _platform.Document.Text);
        Assert.Equal(0, _platform.PasteCount);
        Assert.Null(_platform.ActiveLayoutId);
        Assert.Equal("original clipboard", _platform.ClipboardText);
        Assert.All(_platform.ObservedForegroundWindows, window => Assert.Equal((nint)1, window));
    }

    [Theory]
    [InlineData("recent", false)]
    [InlineData("recent", true)]
    [InlineData("explicit", false)]
    [InlineData("explicit", true)]
    [InlineData("repeat", false)]
    [InlineData("repeat", true)]
    public async Task FullBlurBetweenForegroundReads_RechecksTheCurrentRange(string mode, bool loseSelection)
    {
        _platform.WaitForTransientFocus = true;
        _platform.ActiveLayoutId = KeyboardLayouts.English.Id;
        if (mode == "explicit") { _platform.Document.Text = "ghbdtn"; _platform.Document.SelectAll(); }
        else _platform.Type("ghbdtn");
        if (mode == "repeat") await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        var before = _platform.Document.Text;
        var beforeLayout = _platform.ActiveLayoutId;
        var beforePastes = _platform.PasteCount;
        AtNextCopySettle(() => _platform.BlurAndReturn(loseSelection));
        var target = mode == "repeat" ? KeyboardLayouts.English : KeyboardLayouts.Russian;
        await _controller.ConvertSelectionAsync(target);
        Assert.Equal(loseSelection ? before : mode == "repeat" ? "ghbdtn" : "привет", _platform.Document.Text);
        Assert.Equal(loseSelection ? beforeLayout : target.Id, _platform.ActiveLayoutId);
        Assert.Equal(beforePastes + (loseSelection ? 0 : 1), _platform.PasteCount);
        Assert.All(_platform.ObservedForegroundWindows, window => Assert.Equal((nint)1, window));
        Assert.Equal(0, _platform.Activations);
        Assert.Equal("original clipboard", _platform.ClipboardText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedBlurDuringRecopy_AbortsWithABoundedNumberOfCopies(bool loseSelection)
    {
        _platform.WaitForTransientFocus = true;
        _platform.Type("ghbdtn");
        var copies = 0;
        _platform.AfterCopy = () =>
        {
            var current = ++copies;
            AtSettle(() => _platform.BlurAndReturn(current == 2 && loseSelection));
        };
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal(2, copies);
        Assert.Equal("ghbdtn", _platform.Document.Text);
        Assert.Equal(0, _platform.PasteCount);
        Assert.Null(_platform.ActiveLayoutId);
        Assert.Equal("original clipboard", _platform.ClipboardText);
        Assert.All(_platform.ObservedForegroundWindows, window => Assert.Equal((nint)1, window));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recopy_UserInputOrExternalCopy_StopsBeforePaste(bool externalCopy)
    {
        _platform.WaitForTransientFocus = true;
        _platform.Type("ghbdtn");
        var copies = 0;
        _platform.AfterCopy = () =>
        {
            var current = ++copies;
            AtSettle(() =>
            {
                _platform.BlurAndReturn(loseSelection: false);
                if (current != 2) return;
                if (externalCopy) _platform.ExternalCopy("external copy", owner: 99);
                else _platform.UserKey(TypedKey.ResetKey);
            });
        };
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal(2, copies);
        Assert.Equal("ghbdtn", _platform.Document.Text);
        Assert.Equal(0, _platform.PasteCount);
        Assert.Null(_platform.ActiveLayoutId);
        // Real input while Copy is pending makes the new sequence ambiguous (it could be a
        // user copy from the same app). Do not claim it and restore an older clipboard over it.
        Assert.Equal(externalCopy ? "external copy" : "ghbdtn", _platform.ClipboardText);
    }

    [Fact]
    public async Task FullBlurDuringFinalClipboardCheck_StopsBeforePasteAndLayoutSwitch()
    {
        _platform.Type("ghbdtn");
        _platform.AfterWrite = text =>
        {
            if (text != "привет") return;
            _platform.AfterWrite = null;
            var reads = 0;
            _platform.OnSequenceRead = () =>
            {
                if (++reads != 5) return;
                _platform.OnSequenceRead = null;
                _platform.BlurAndReturn(loseSelection: true);
            };
        };
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal("ghbdtn", _platform.Document.Text);
        Assert.Equal(0, _platform.PasteCount);
        Assert.Null(_platform.ActiveLayoutId);
        Assert.Equal("original clipboard", _platform.ClipboardText);
    }

    [Fact]
    public async Task PlatformWithoutFocusEvents_MakesAnIndependentCopyBeforeReplacing()
    {
        _platform.TrackFocusTransitions = false;
        _platform.Type("ghbdtn");
        AtNextCopySettle(() => _platform.BlurAndReturn(loseSelection: true));
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal("ghbdtn", _platform.Document.Text);
        Assert.Equal(0, _platform.PasteCount);
        Assert.Null(_platform.ActiveLayoutId);
    }

    [Fact]
    public async Task NoOpTransform_AfterFullBlur_DoesNotSwitchLayout()
    {
        _platform.Type("ghbdtn");
        await _controller.TransformSelectionAsync(text =>
        {
            _platform.BlurAndReturn(loseSelection: true);
            return (text, KeyboardLayouts.Russian);
        });
        Assert.Equal("ghbdtn", _platform.Document.Text);
        Assert.Equal(0, _platform.PasteCount);
        Assert.Null(_platform.ActiveLayoutId);
    }

    [Fact]
    public async Task NoOpTransform_FullBlurAfterVerification_DoesNotSwitchLayout()
    {
        _platform.Type("ghbdtn");
        await _controller.TransformSelectionAsync(text =>
        {
            _platform.AfterForegroundRead = () =>
            {
                _platform.AfterForegroundRead = null;
                _platform.BlurAndReturn(loseSelection: true);
            };
            return (text, KeyboardLayouts.Russian);
        });
        Assert.Equal("ghbdtn", _platform.Document.Text);
        Assert.Equal(0, _platform.PasteCount);
        Assert.Null(_platform.ActiveLayoutId);
    }

    [Fact]
    public async Task UserInputWhileModifiersAreReleased_CancelsThePendingConversion()
    {
        _platform.Type("ghbdtn");
        _platform.BeforeModifiers = () => { _platform.UserKey(TypedKey.ResetKey); return Task.CompletedTask; };
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal("ghbdtn", _platform.Document.Text);
        Assert.Equal(0, _platform.PasteCount);
        Assert.Null(_platform.ActiveLayoutId);
    }

    [Theory]
    [InlineData("abc", "Abc")]
    [InlineData("abc\n", "Abc\n")]
    [InlineData("\u2063abc", "\u2063Abc")]
    public async Task SelectedText_ConvertsAndRestoresClipboard(string input, string expected)
    {
        _platform.Document.Text = input;
        _platform.Document.SelectAll();
        await _controller.ChangeCaseAsync();
        Assert.Equal(expected, _platform.Document.Text);
        Assert.Equal("original clipboard", _platform.ClipboardText);
    }

    [Theory]
    [InlineData("htdm.dftim", "ревьюваешь")]
    [InlineData("Привет htdm.dftim", "Привет ревьюваешь")]
    [InlineData("Привет htdm.dftim  ", "Привет ревьюваешь  ")]
    [InlineData("Привет ;bpym", "Привет жизнь")]
    [InlineData("Привет [kt,", "Привет хлеб")]
    [InlineData("first\nhtdm.dftim", "first\nревьюваешь")]
    public async Task RecentInput_IncludesPunctuationInsideTheWrongLayoutWord(string input, string expected)
    {
        var end = input.Length;
        while (end > 0 && char.IsWhiteSpace(input[end - 1])) end--;
        var start = end;
        while (start > 0 && !char.IsWhiteSpace(input[start - 1])) start--;
        _platform.Document.Text = input[..start];
        _platform.Document.Caret = start;
        _platform.CopyLineWhenUnselected = true;
        _platform.Type(input[start..]);
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal(expected, _platform.Document.Text);
        Assert.Equal("original clipboard", _platform.ClipboardText);
    }

    [Fact]
    public async Task Undo_OnlyReplacesTheUnchangedResult()
    {
        _platform.Document.Text = "😀abc";
        _platform.Document.SelectAll();
        await _controller.ChangeCaseAsync();
        Assert.Equal("😀Abc", _platform.Document.Text);
        await _controller.UndoLastTransformAsync();
        Assert.Equal("😀abc", _platform.Document.Text);
    }

    [Theory]
    [InlineData("готовое", "?", "?", "готовое,")]
    [InlineData("готовое", ",", "б", "готовое,")]
    [InlineData("готовое", "/", ".", "готовое/")]
    public async Task PunctuationOnly_LeavesTheExistingWordAlone(string existing, string keys, string inserted, string expected)
    {
        _platform.Document.Text = existing;
        _platform.Document.Caret = existing.Length;
        _platform.CopyLineWhenUnselected = true;
        _platform.TypeAs(keys, inserted);
        await _controller.ConvertSelectionAsync(null);
        Assert.Equal(expected, _platform.Document.Text);
        Assert.Equal("original clipboard", _platform.ClipboardText);
    }

    [Fact]
    public async Task PreviouslyTypedWord_AfterCaretMove_IsNotPartOfTheNewInput()
    {
        _platform.Type("ready");
        _platform.UserMoveCaret(5);
        _platform.Type("?");
        _platform.CopyLineWhenUnselected = true;
        await _controller.ConvertSelectionAsync(null);
        Assert.Equal("ready,", _platform.Document.Text);
    }

    [Fact]
    public async Task UnmarkedCopyWholeLineBehavior_CannotEnlargeTheTrackedRange()
    {
        _platform.Document.Text = "ready";
        _platform.Document.Caret = 5;
        _platform.CopyLineWhenUnselected = true;
        _platform.ReportLineCopyTag = false;
        _platform.Type("?");
        await _controller.ConvertSelectionAsync(null);
        Assert.Equal("ready,", _platform.Document.Text);
    }

    [Fact]
    public async Task ExplicitUserSelectionStillConvertsTheWholeSelection()
    {
        _platform.Type("ghbdtn vbh");
        _platform.UserKey(TypedKey.ForShortcut(Hotkey.TryParse("Ctrl+A")));
        _platform.Document.SelectAll();
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        Assert.Equal("привет мир", _platform.Document.Text);
    }

    [Fact]
    public async Task NoTrackedInput_DoesNotConvertExistingText()
    {
        _platform.Document.Text = "existingword?";
        _platform.Document.Caret = 13;
        _platform.CopyLineWhenUnselected = true;
        await _controller.ConvertSelectionAsync(null);
        Assert.Equal("existingword?", _platform.Document.Text);
        Assert.Empty(_platform.Document.Selection);
        Assert.Equal(0, _platform.PasteCount);
    }

    [Fact]
    public async Task TrackedKeysThatDidNotInsertMatchingText_DoNotReplaceOlderText()
    {
        _platform.Document.Text = "existingword";
        _platform.Document.Caret = 12;
        _platform.CopyLineWhenUnselected = true;
        _platform.UserKey(TypedKey.ForChar('?'));
        await _controller.ConvertSelectionAsync(null);
        Assert.Equal("existingword", _platform.Document.Text);
        Assert.Empty(_platform.Document.Selection);
        Assert.Equal(0, _platform.PasteCount);
    }

    [Fact]
    public async Task BackspacePastTheNewInput_DoesNotMakeTheOlderWordEligible()
    {
        _platform.Document.Text = "ready";
        _platform.Document.Caret = 5;
        _platform.Type("x");
        for (var i = 0; i < 2; i++)
        {
            _platform.UserKey(TypedKey.BackspaceKey);
            _platform.Document.Backspace();
        }
        _platform.Type("?");
        _platform.CopyLineWhenUnselected = true;
        await _controller.ConvertSelectionAsync(null);
        Assert.Equal("read,", _platform.Document.Text);
    }

    [Fact]
    public async Task UnrelatedShortcutStartsANewInputRun()
    {
        _platform.Type("ready");
        _platform.UserKey(TypedKey.ForShortcut(Hotkey.TryParse("Ctrl+V")));
        _platform.Document.Type("pasted");
        _platform.Type("?");
        _platform.CopyLineWhenUnselected = true;
        await _controller.ConvertSelectionAsync(null);
        Assert.Equal("readypasted,", _platform.Document.Text);
    }

    [Fact]
    public async Task SuccessfulConversionStartsANewInputRun()
    {
        _platform.CopyLineWhenUnselected = true;
        _platform.Type("ghbdtn");
        await _controller.ConvertSelectionAsync(KeyboardLayouts.Russian);
        _platform.Type("?");
        await _controller.ConvertSelectionAsync(null);
        Assert.Equal("привет,", _platform.Document.Text);
    }

    [Fact]
    public async Task OwnHotkeyKeepsTheTypedRunUntilTheCommandConsumesIt()
    {
        _platform.Document.Text = "ready";
        _platform.Document.Caret = 5;
        _platform.CopyLineWhenUnselected = true;
        _platform.Type("?");
        _platform.PressHotkey("Ctrl+Alt+Shift+Space");
        await WaitUntil(() => _platform.Document.Text == "ready,");
        Assert.Equal("ready,", _platform.Document.Text);
    }

    [Fact]
    public async Task WindowChangeKeepsTheOtherWindowsWordOutOfTheRun()
    {
        _platform.Type("old");
        _platform.Window = 2;
        _platform.Document.Text = "ready";
        _platform.Document.Caret = 5;
        _platform.CopyLineWhenUnselected = true;
        _platform.Type("?");
        await _controller.ConvertSelectionAsync(null);
        Assert.Equal("ready,", _platform.Document.Text);
    }

    [Fact]
    public async Task Undo_AfterMoreTypingPreservesTheNewText()
    {
        _platform.Document.Text = "abc";
        _platform.Document.SelectAll();
        await _controller.ChangeCaseAsync();
        _platform.Document.Type("XYZ");
        await _controller.UndoLastTransformAsync();
        Assert.Equal("AbcXYZ", _platform.Document.Text);
        Assert.Equal(6, _platform.Document.Caret);
        Assert.Empty(_platform.Document.Selection);
        Assert.Equal("original clipboard", _platform.ClipboardText);
    }

    [Fact]
    public async Task CopyDuringTransformation_IsPreservedAndStopsThePaste()
    {
        _platform.Document.Text = "abc";
        _platform.Document.SelectAll();
        await _controller.TransformSelectionAsync(text =>
        {
            _platform.ExternalCopy("new user copy");
            return ("Abc", null);
        });
        Assert.Equal("abc", _platform.Document.Text);
        Assert.Equal("new user copy", _platform.ClipboardText);
        Assert.Equal(0, _platform.PasteCount);
    }

    [Fact]
    public async Task CopyImmediatelyAfterWritingTheResult_IsNotMistakenForOurOwnWrite()
    {
        _platform.Document.Text = "abc";
        _platform.Document.SelectAll();
        _platform.AfterWrite = text => { if (text == "Abc") _platform.ExternalCopy("new user copy"); };
        await _controller.ChangeCaseAsync();
        Assert.Equal("abc", _platform.Document.Text);
        Assert.Equal("new user copy", _platform.ClipboardText);
        Assert.Equal(0, _platform.PasteCount);
    }

    [Fact]
    public async Task CopyJustBeforePaste_DoesNotGetPastedIntoTheDocument()
    {
        _platform.Document.Text = "abc";
        _platform.Document.SelectAll();
        _platform.OnForegroundRead = () =>
        {
            if (_platform.ClipboardText == "Abc") _platform.ExternalCopy("new user copy");
        };
        await _controller.ChangeCaseAsync();
        Assert.Equal("abc", _platform.Document.Text);
        Assert.Equal("new user copy", _platform.ClipboardText);
        Assert.Equal(0, _platform.PasteCount);
    }

    [Fact]
    public async Task SnapshotThatChangesWhileBeingRead_DoesNotReplaceTheNewCopy()
    {
        _platform.Document.Text = "abc";
        _platform.Document.SelectAll();
        _platform.AfterSnapshot = () => _platform.ExternalCopy("new user copy");
        await _controller.ChangeCaseAsync();
        Assert.Equal("abc", _platform.Document.Text);
        Assert.Equal("new user copy", _platform.ClipboardText);
    }

    [Fact]
    public async Task HistoryPaste_StopsIfTheClipboardChangesWhileModifiersAreReleased()
    {
        _platform.Document.Text = "existing text";
        _platform.Document.SelectAll();
        var id = _controller.Store.Add(ClipContent.FromText("history item"))!.Value;
        _platform.BeforeModifiers = () => { _platform.ExternalCopy("new user copy"); return Task.CompletedTask; };
        await _controller.PasteAsync(id, 1);
        Assert.Equal("existing text", _platform.Document.Text);
        Assert.Equal("new user copy", _platform.ClipboardText);
        Assert.Equal(0, _platform.PasteCount);
    }

    [Fact]
    public async Task Snippet_DoesNotTouchAnotherWindow()
    {
        _platform.Document.Text = ";hi";
        _platform.Document.Caret = 3;
        _platform.BeforeModifiers = () =>
        {
            _platform.Window = 2;
            _platform.Document.Text = "OTHER";
            _platform.Document.Caret = 5;
            return Task.CompletedTask;
        };
        await _controller.ExpandSnippetAsync(new Snippet { Trigger = ";hi", Text = "EXPANDED" }, 1);
        Assert.Equal("OTHER", _platform.Document.Text);
        Assert.Equal(0, _platform.BackspaceCount);
    }

    [Fact]
    public async Task Snippet_CapturesTheWindowOnTheTypingEventBeforeDispatch()
    {
        EnableSnippet();
        var capturedOnEvent = false;
        _platform.AfterForegroundRead = () =>
        {
            if (_platform.Document.Text != ";hi") return;
            capturedOnEvent |= _platform.InTypingEvent;
            _platform.Window = 2;
            _platform.Document.Text = "OTHER";
            _platform.Document.Caret = 5;
        };
        _platform.Type(";hi");
        Assert.True(capturedOnEvent);
        await _platform.ModifiersEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("OTHER", _platform.Document.Text);
        Assert.Equal(0, _platform.BackspaceCount);
    }

    [Fact]
    public async Task Snippet_StopsIfMoreTextIsTypedWhileItWaits()
    {
        EnableSnippet();
        _platform.Document.Text = ";hi";
        _platform.Document.Caret = 3;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _platform.BeforeModifiers = () => release.Task;
        var expansion = _controller.ExpandSnippetAsync(new Snippet { Trigger = ";hi", Text = "EXPANDED" }, 1);
        await _platform.ModifiersEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        _platform.Type("X");
        release.SetResult();
        await expansion;
        Assert.Equal(";hiX", _platform.Document.Text);
        Assert.Equal(0, _platform.BackspaceCount);
    }

    [Fact]
    public async Task Snippet_IsTypedWithoutTouchingTheClipboard()
    {
        _platform.Document.Text = "say ;hi";
        _platform.Document.Caret = 7;
        await _controller.ExpandSnippetAsync(new Snippet { Trigger = ";hi", Text = "EXPANDED" }, 1);
        Assert.Equal("say EXPANDED", _platform.Document.Text);
        Assert.Equal("original clipboard", _platform.ClipboardText);
        Assert.Equal(1UL, _platform.ClipboardSequence); // never written
        Assert.Equal(0, _platform.PasteCount);
    }

    [Fact]
    public async Task Snippet_ExternalCopyAfterDeletion_KeepsBothTheCopyAndTheText()
    {
        // Review #1: a copy by another app between deleting the trigger and inserting the text.
        _platform.Document.Text = ";hi";
        _platform.Document.Caret = 3;
        _platform.OnBackspace = () => { if (_platform.BackspaceCount == 3) _platform.ExternalCopy("user copy"); };
        await _controller.ExpandSnippetAsync(new Snippet { Trigger = ";hi", Text = "EXPANDED" }, 1);
        Assert.Equal("EXPANDED", _platform.Document.Text);
        Assert.Equal("user copy", _platform.ClipboardText);
    }

    [Fact]
    public async Task Snippet_RealTypingAfterPartialDeletion_StopsWithoutTypingAnything()
    {
        // Real input during the expansion stops it: no "XEXPANDED", and nothing is typed back at a caret that moved.
        EnableSnippet();
        _platform.Document.Text = ";hi";
        _platform.Document.Caret = 3;
        _platform.OnBackspace = () => { if (_platform.BackspaceCount == 1) _platform.Type("X"); };
        await _controller.ExpandSnippetAsync(new Snippet { Trigger = ";hi", Text = "EXPANDED" }, 1);
        // ";hi" -> ";h" (our Backspace) -> ";hX" (user); then Typlo stops.
        Assert.Equal(";hX", _platform.Document.Text);
        Assert.Equal(1, _platform.BackspaceCount);
        Assert.Equal(0, _platform.TypedTextCount);
    }

    [Fact]
    public async Task Snippet_ClickElsewhereInTheSameWindow_DuringDeletion_DoesNotOverwriteTheNewSelection()
    {
        EnableSnippet();
        _platform.Document.Text = "keep this ;hi";
        _platform.Document.Caret = 13;
        _platform.OnBackspace = () =>
        {
            if (_platform.BackspaceCount != 1) return;
            _platform.Document.Anchor = 0; // the user selects "keep" with the mouse
            _platform.UserMoveCaret(4, keepAnchor: true);
        };
        await _controller.ExpandSnippetAsync(new Snippet { Trigger = ";hi", Text = "EXPANDED" }, 1);
        Assert.Equal("keep this ;h", _platform.Document.Text);
        Assert.Equal(0, _platform.TypedTextCount);
    }

    [Fact]
    public async Task Snippet_MultiLine_IsPastedAsText_NotTypedAsEnterKeys()
    {
        _platform.Document.Text = ";hi";
        _platform.Document.Caret = 3;
        await _controller.ExpandSnippetAsync(new Snippet { Trigger = ";hi", Text = "line 1\nline 2\tend" }, 1);
        Assert.Equal("line 1\nline 2\tend", _platform.Document.Text);
        Assert.Equal(1, _platform.PasteCount);
        Assert.Equal(0, _platform.TypedTextCount); // never as Enter/Tab keystrokes
        Assert.Equal("original clipboard", _platform.ClipboardText);
    }

    [Fact]
    public async Task Snippet_MultiLine_ClipboardTakenBeforeDeletion_DeletesNothing()
    {
        _platform.Document.Text = ";hi";
        _platform.Document.Caret = 3;
        _platform.AfterWrite = text => { if (text.StartsWith("line")) _platform.ExternalCopy("user copy"); };
        await _controller.ExpandSnippetAsync(new Snippet { Trigger = ";hi", Text = "line 1\nline 2" }, 1);
        Assert.Equal(";hi", _platform.Document.Text);
        Assert.Equal(0, _platform.BackspaceCount);
        Assert.Equal("user copy", _platform.ClipboardText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Snippet_MultiLine_FailedPreparation_ReleasesTheCaptureSuspension(bool failSnapshot)
    {
        // Review: the suspension was taken before a snapshot or write that can throw, and only disposed afterwards.
        _platform.Document.Text = ";hi";
        _platform.Document.Caret = 3;
        if (failSnapshot) _platform.AfterSnapshot = () => _platform.ExternalCopy("user copy");
        else _platform.AfterWrite = text => { if (text.StartsWith("line")) _platform.ExternalCopy("user copy"); };
        await _controller.ExpandSnippetAsync(new Snippet { Trigger = ";hi", Text = "line 1\nline 2" }, 1);
        Assert.Equal(1, _platform.SuspendCalls);
        Assert.Equal(0, _platform.SuspendDepth); // capture is not left disabled until restart
        Assert.Equal(";hi", _platform.Document.Text);
        Assert.Equal("user copy", _platform.ClipboardText);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Snippet_MultiLine_ExternalCopyDuringDeletion_KeepsTheCopyAndPutsTheTriggerBack(int atBackspace)
    {
        // Review: another app copies while the trigger is deleted (3 = on the last Backspace). Its copy must not be
        // pasted or overwritten, and the deleted trigger comes back since the caret did not move.
        _platform.Document.Text = "say ;hi";
        _platform.Document.Caret = 7;
        _platform.OnBackspace = () => { if (_platform.BackspaceCount == atBackspace) _platform.ExternalCopy("user copy"); };
        await _controller.ExpandSnippetAsync(new Snippet { Trigger = ";hi", Text = "line 1\nline 2" }, 1);
        Assert.Equal("say ;hi", _platform.Document.Text);
        Assert.Equal("user copy", _platform.ClipboardText);
        Assert.Equal(0, _platform.PasteCount);
        Assert.Equal(atBackspace, _platform.BackspaceCount);
        Assert.Equal(0, _platform.SuspendDepth);
    }

    [Fact]
    public async Task Snippet_MultiLine_ExternalCopyAndUserInputDuringDeletion_TypesNothingBack()
    {
        // The user selects text while Typlo finds the clipboard taken: the trigger is not typed back over the selection.
        EnableSnippet();
        _platform.Document.Text = "keep this ;hi";
        _platform.Document.Caret = 13;
        _platform.OnBackspace = () =>
        {
            if (_platform.BackspaceCount != 3) return;
            _platform.ExternalCopy("user copy");
            var armed = true;
            _platform.OnSequenceRead = () =>
            {
                if (!armed) return;
                armed = false; // during the clipboard check right before the paste
                _platform.Document.Anchor = 0;
                _platform.UserMoveCaret(4, keepAnchor: true);
            };
        };
        await _controller.ExpandSnippetAsync(new Snippet { Trigger = ";hi", Text = "line 1\nline 2" }, 1);
        Assert.Equal("keep this ", _platform.Document.Text);
        Assert.Equal(0, _platform.TypedTextCount);
        Assert.Equal("user copy", _platform.ClipboardText);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    [InlineData(3, 2)]
    public async Task Snippet_MultiLine_SelectionChangedDuringTheClipboardCheck_StopsBeforeTheNextKey(int atBackspace, int atRead)
    {
        // Review: the clipboard check can block (xclip). The user selects "keep" during it without copying anything:
        // no further Backspace (1) and no paste (3; first or second check before Ctrl+V) at the new selection.
        EnableSnippet();
        _platform.Document.Text = "keep this ;hi";
        _platform.Document.Caret = 13;
        _platform.OnBackspace = () =>
        {
            if (_platform.BackspaceCount != atBackspace) return;
            SelectOnSequenceRead(atRead, anchor: 0, caret: 4); // reads after this Backspace are the clipboard checks
        };
        await _controller.ExpandSnippetAsync(new Snippet { Trigger = ";hi", Text = "line 1\nline 2" }, 1);
        Assert.Equal("keep this ;hi"[..(13 - atBackspace)], _platform.Document.Text);
        Assert.Equal(atBackspace, _platform.BackspaceCount);
        Assert.Equal(0, _platform.PasteCount);
        Assert.Equal(0, _platform.TypedTextCount);
    }

    /// <summary>The user selects text with the mouse while Typlo reads the clipboard sequence for the n-th time.</summary>
    private void SelectOnSequenceRead(int nth, int anchor, int caret)
    {
        var reads = 0;
        _platform.OnSequenceRead = () =>
        {
            if (++reads != nth) return;
            _platform.Document.Anchor = anchor;
            _platform.UserMoveCaret(caret, keepAnchor: true);
        };
    }

    [Fact]
    public async Task Snippet_FocusChangeMidTyping_StopsTheRest()
    {
        // Review #6: the target is checked before every character of a long snippet.
        _platform.Document.Text = ";hi";
        _platform.Document.Caret = 3;
        _platform.OnTypeChar = typed => { if (typed == 3) _platform.Window = 2; };
        await _controller.ExpandSnippetAsync(new Snippet { Trigger = ";hi", Text = "abcdefghij" }, 1);
        Assert.Equal("", _platform.Document.Text); // window 2 (now active) received nothing
        _platform.Window = 1;
        Assert.Equal("abc", _platform.Document.Text); // window 1 got only the part typed before the switch
    }

    [Fact]
    public async Task Copy_ExternalChangeWhileTheCopySettles_IsNotAdopted()
    {
        // Review #2: another app (another clipboard owner) copies after our copy was observed but before it is read.
        _platform.Document.Text = "abc";
        _platform.Document.SelectAll();
        var reads = int.MinValue; // count only the reads after the copy
        _platform.AfterCopy = () => reads = 0;
        _platform.OnSequenceRead = () =>
        {
            if (++reads == 2) _platform.ExternalCopy("external copy", owner: 99); // while our copy settles
        };
        await _controller.ChangeCaseAsync();
        Assert.Equal("abc", _platform.Document.Text);
        Assert.Equal("external copy", _platform.ClipboardText);
        Assert.Equal(0, _platform.PasteCount);
    }

    [Fact]
    public async Task Copy_ExternalChangeWithUnknownOwner_WhileTheCopySettles_IsNotAdopted()
    {
        // Review (unknown owner): on macOS/Linux the OS does not name the owner, so a second change is ambiguous.
        _platform.OwnerKnown = false;
        _platform.Document.Text = "abc";
        _platform.Document.SelectAll();
        var reads = int.MinValue;
        _platform.AfterCopy = () => reads = 0;
        _platform.OnSequenceRead = () =>
        {
            if (++reads == 2) _platform.ExternalCopy("external copy");
        };
        await _controller.ChangeCaseAsync();
        Assert.Equal("abc", _platform.Document.Text);
        Assert.Equal("external copy", _platform.ClipboardText);
        Assert.Equal(0, _platform.PasteCount);
    }

    [Fact]
    public async Task Copy_KnownOwner_FormatsFirstThenData_IsStillAdopted()
    {
        // X11 (owner known through X-Resource): the copy first shows up without readable text, then with it.
        _platform.Document.Text = "abc";
        _platform.Document.SelectAll();
        var reads = int.MinValue;
        var selection = "";
        _platform.AfterCopy = () =>
        {
            reads = 0;
            selection = _platform.ClipboardText;
            _platform.ExternalCopy("", owner: _platform.Window); // formats announced, data not readable yet
        };
        _platform.OnSequenceRead = () => { if (++reads == 2) _platform.ExternalCopy(selection, owner: _platform.Window); };
        await _controller.ChangeCaseAsync();
        Assert.Equal("Abc", _platform.Document.Text);
        Assert.Equal("original clipboard", _platform.ClipboardText);
    }

    [Fact]
    public async Task Copy_UnknownOwner_EmptyFirstStateThenOtherText_IsNotAdopted()
    {
        // Review: with no owner to check, an empty first state followed by text may be another app's copy.
        _platform.OwnerKnown = false;
        _platform.Document.Text = "abc";
        _platform.Document.SelectAll();
        var reads = int.MinValue;
        _platform.AfterCopy = () =>
        {
            reads = 0;
            _platform.ExternalCopy("");
        };
        _platform.OnSequenceRead = () => { if (++reads == 2) _platform.ExternalCopy("external copy"); };
        await _controller.ChangeCaseAsync();
        Assert.Equal("abc", _platform.Document.Text);
        Assert.Equal("external copy", _platform.ClipboardText); // neither pasted nor overwritten by the restore
        Assert.Equal(0, _platform.PasteCount);
    }

    [Fact]
    public async Task Copy_TwoStepWriteByTheTargetApp_IsStillAdopted()
    {
        // OLE apps set the clipboard and flush it a moment later: two changes, same owner.
        _platform.Document.Text = "abc";
        _platform.Document.SelectAll();
        var reads = int.MinValue; // count only the reads after the copy
        _platform.AfterCopy = () => reads = 0;
        _platform.OnSequenceRead = () =>
        {
            // The flush lands on the second poll after the copy, while the copy settles.
            if (++reads == 2) _platform.ExternalCopy(_platform.ClipboardText, owner: _platform.Window);
        };
        await _controller.ChangeCaseAsync();
        Assert.Equal("Abc", _platform.Document.Text);
        Assert.Equal("original clipboard", _platform.ClipboardText);
    }

    [Fact]
    public async Task Copy_LateFlushByTheTargetApp_AfterTheCopySettled_IsStillAccepted()
    {
        _platform.Document.Text = "abc";
        _platform.Document.SelectAll();
        // After the copy settled and was adopted, the target app flushes it (same owner, same text) before Typlo
        // writes the result: the write guard must recognise the flush instead of aborting.
        await _controller.TransformSelectionAsync(text =>
        {
            _platform.ExternalCopy(text, owner: _platform.Window);
            return ("Abc", null);
        });
        Assert.Equal("Abc", _platform.Document.Text);
        Assert.Equal("original clipboard", _platform.ClipboardText);
    }

    [Fact]
    public async Task Undo_RepeatedTextAfterCaretMove_DoesNotTouchTheOtherOccurrence()
    {
        // Review #4: convert the first "abc" of "abc Abc", move after the second, then Undo.
        _platform.Document.Text = "abc Abc";
        _platform.Document.Anchor = 0;
        _platform.Document.Caret = 3;
        await _controller.ChangeCaseAsync();
        Assert.Equal("Abc Abc", _platform.Document.Text);
        _platform.UserMoveCaret(7); // End key / click: reported by the hook
        await _controller.UndoLastTransformAsync();
        Assert.Equal("Abc Abc", _platform.Document.Text);
    }

    [Fact]
    public async Task Undo_ThroughTheRealHotkey_IsNotInvalidatedByItsOwnKeyPress()
    {
        // Review: the hook reports Ctrl+Alt+Shift+Z as a shortcut before the hotkey handler runs.
        _platform.Document.Text = "abc";
        _platform.Document.SelectAll();
        await _controller.ChangeCaseAsync();
        Assert.Equal("Abc", _platform.Document.Text);
        _platform.PressHotkey("Ctrl+Alt+Shift+Z");
        await WaitUntil(() => _platform.Document.Text == "abc");
        Assert.Equal("abc", _platform.Document.Text);
    }

    [Fact]
    public async Task Undo_UnrelatedShortcut_InvalidatesIt()
    {
        _platform.Document.Text = "abc";
        _platform.Document.SelectAll();
        await _controller.ChangeCaseAsync();
        _platform.UserKey(TypedKey.ForShortcut(Hotkey.TryParse("Ctrl+V"))); // the user pastes something
        await _controller.UndoLastTransformAsync();
        Assert.Equal("Abc", _platform.Document.Text);
    }

    [Fact]
    public async Task Undo_CaretMovedWhileUndoWaits_DoesNotTouchTheOtherOccurrence()
    {
        _platform.Document.Text = "abc Abc";
        _platform.Document.Anchor = 0;
        _platform.Document.Caret = 3;
        await _controller.ChangeCaseAsync();
        Assert.Equal("Abc Abc", _platform.Document.Text);
        _platform.BeforeModifiers = () => { _platform.UserMoveCaret(7); return Task.CompletedTask; };
        await _controller.UndoLastTransformAsync();
        Assert.Equal("Abc Abc", _platform.Document.Text);
    }

    [Fact]
    public async Task Undo_CaretMovedDuringThePasteSettle_IsNotRecorded()
    {
        _platform.Document.Text = "abc Abc";
        _platform.Document.Anchor = 0;
        _platform.Document.Caret = 3;
        _platform.AfterPaste = () => _platform.UserMoveCaret(7);
        await _controller.ChangeCaseAsync();
        _platform.AfterPaste = null;
        await _controller.UndoLastTransformAsync();
        Assert.Equal("Abc Abc", _platform.Document.Text);
    }

    [Fact]
    public async Task Undo_SelectionChangedDuringTheFinalPasteWait_DoesNotOverwriteIt()
    {
        // Review: convert the first "abc" of "abc Abc"; while Undo writes "abc" to the clipboard, the user selects
        // the second "Abc".
        _platform.Document.Text = "abc Abc";
        _platform.Document.Anchor = 0;
        _platform.Document.Caret = 3;
        await _controller.ChangeCaseAsync();
        Assert.Equal("Abc Abc", _platform.Document.Text);
        _platform.AfterWrite = text =>
        {
            if (text != "abc") return;
            _platform.Document.Anchor = 4;
            _platform.UserMoveCaret(7, keepAnchor: true);
        };
        await _controller.UndoLastTransformAsync();
        Assert.Equal("Abc Abc", _platform.Document.Text);
        Assert.Equal("original clipboard", _platform.ClipboardText);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Undo_SelectionChangedDuringTheClipboardCheckBeforeThePaste_DoesNotOverwriteIt(int atRead)
    {
        // Review: the shared paste helper's clipboard check can block; the selection changes during it.
        _platform.Document.Text = "abc Abc";
        _platform.Document.Anchor = 0;
        _platform.Document.Caret = 3;
        await _controller.ChangeCaseAsync();
        Assert.Equal("Abc Abc", _platform.Document.Text);
        // Our write of "abc" reads the sequence twice to verify itself; reads 3 and 4 are the checks before Ctrl+V.
        _platform.AfterWrite = text => { if (text == "abc") SelectOnSequenceRead(atRead, anchor: 4, caret: 7); };
        await _controller.UndoLastTransformAsync();
        Assert.Equal("Abc Abc", _platform.Document.Text);
        Assert.Equal("original clipboard", _platform.ClipboardText);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Convert_SelectionChangedDuringTheClipboardCheckBeforeThePaste_DoesNotOverwriteIt(int atRead)
    {
        _platform.Document.Text = "abc def";
        _platform.Document.Anchor = 0;
        _platform.Document.Caret = 3;
        _platform.AfterWrite = text => { if (text == "Abc") SelectOnSequenceRead(atRead, anchor: 4, caret: 7); };
        await _controller.ChangeCaseAsync();
        Assert.Equal("abc def", _platform.Document.Text);
        Assert.Equal(0, _platform.PasteCount);
        Assert.Equal("original clipboard", _platform.ClipboardText);
    }

    [Fact]
    public async Task Convert_SelectionChangedBeforeThePaste_DoesNotOverwriteIt()
    {
        _platform.Document.Text = "abc def";
        _platform.Document.Anchor = 0;
        _platform.Document.Caret = 3;
        _platform.AfterWrite = text =>
        {
            if (text != "Abc") return;
            _platform.Document.Anchor = 4;
            _platform.UserMoveCaret(7, keepAnchor: true);
        };
        await _controller.ChangeCaseAsync();
        Assert.Equal("abc def", _platform.Document.Text);
        Assert.Equal(0, _platform.PasteCount);
        Assert.Equal("original clipboard", _platform.ClipboardText);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(30);
    }

    [Fact]
    public async Task Undo_IsInvalidatedByBackspaceAndClicks_EvenWithoutSnippets()
    {
        _platform.Document.Text = "abc";
        _platform.Document.SelectAll();
        await _controller.ChangeCaseAsync();
        Assert.True(_platform.TypingWatch); // the hook runs while an undo is possible
        _platform.UserKey(TypedKey.BackspaceKey);
        _platform.Document.Backspace();
        _platform.Document.Type("c");
        await _controller.UndoLastTransformAsync();
        Assert.Equal("Abc", _platform.Document.Text);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("123")]
    [InlineData("not json")]
    public void EditorMetadata_RejectsNonObjectPayloads(string json) =>
        Assert.False(EditorClipboard.IsFromEmptySelection(json));

    private void EnableSnippet()
    {
        var settings = _controller.Settings.Clone();
        settings.Snippets = [new Snippet { Trigger = ";hi", Text = "EXPANDED" }];
        _controller.ApplySettings(settings);
    }

    public void Dispose()
    {
        _controller.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private sealed class Document
    {
        public string Text = "";
        public int Caret;
        public int? Anchor;
        public string Selection => Text[Math.Min(Anchor ?? Caret, Caret)..Math.Max(Anchor ?? Caret, Caret)];
        public void SelectAll() { Anchor = 0; Caret = Text.Length; }
        public void Type(string text)
        {
            var start = Math.Min(Anchor ?? Caret, Caret);
            var end = Math.Max(Anchor ?? Caret, Caret);
            Text = Text[..start] + text + Text[end..];
            Caret = start + text.Length;
            Anchor = null;
        }
        public void Left(bool shift, bool word)
        {
            if (!shift && Anchor is { } anchor) { Caret = Math.Min(anchor, Caret); Anchor = null; return; }
            if (shift) Anchor ??= Caret;
            else Anchor = null;
            if (Caret == 0) return;
            if (!word) { Caret = StringInfo.ParseCombiningCharacters(Text[..Caret])[^1]; return; }
            while (Caret > 0 && char.IsWhiteSpace(Text[Caret - 1])) Caret--;
            if (Caret == 0) return;
            var letters = char.IsLetterOrDigit(Text[Caret - 1]);
            while (Caret > 0 && !char.IsWhiteSpace(Text[Caret - 1]) && char.IsLetterOrDigit(Text[Caret - 1]) == letters) Caret--;
        }
        public void Right()
        {
            Caret = Anchor is { } anchor ? Math.Max(anchor, Caret) : Math.Min(Text.Length, Caret + 1);
            Anchor = null;
        }
        public void Backspace()
        {
            if (Selection.Length == 0 && Caret > 0) Anchor = Caret - 1;
            Type("");
        }
    }

    private sealed class FakePlatform : IPlatform
    {
        private readonly Dictionary<nint, Document> _documents = [];
        private readonly object _focusGate = new();
        private nint _window = 1;
        private long _foregroundVersion;
        public bool TrackFocusTransitions = true;
        public nint Window
        {
            get { lock (_focusGate) return _window; }
            set { lock (_focusGate) { if (_window != value) _foregroundVersion++; _window = value; } }
        }
        public long? ForegroundChangeVersion { get { lock (_focusGate) return TrackFocusTransitions ? _foregroundVersion : null; } }
        public Document Document => DocumentFor(Window);
        public Document DocumentFor(nint window)
        {
            lock (_focusGate) return _documents.TryGetValue(window, out var doc) ? doc : _documents[window] = new();
        }
        public void BlurAndReturn(bool loseSelection)
        {
            lock (_focusGate)
            {
                var target = Window;
                var document = Document;
                Window = 2;
                if (loseSelection) document.Anchor = null;
                Window = target;
            }
        }
        public readonly System.Collections.Concurrent.ConcurrentQueue<nint> ObservedForegroundWindows = new();
        public string ClipboardText = "original clipboard";
        public Action<string>? AfterWrite;
        public Action? OnForegroundRead, AfterForegroundRead, AfterSnapshot;
        public Func<Task>? BeforeModifiers;
        public bool CopyLineWhenUnselected, LastCopyWasLine, InTypingEvent, TypingWatch, ModifiersHeld;
        public bool ReportLineCopyTag = true;
        public Action? OnBackspace, OnLeft, AfterCopy;
        public int PasteCount, BackspaceCount;
        public TaskCompletionSource ModifiersEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name => "Fake platform (no OS access)";
        public IReadOnlyList<KeyboardLayout> Installed = [KeyboardLayouts.English, KeyboardLayouts.Russian];
        public string? ActiveLayoutId;
        public IReadOnlyList<KeyboardLayout> GetKeyboardLayouts() => Installed;
        public string? GetCurrentLayoutId(nint window) => ActiveLayoutId;
        public PlatformCapabilities Capabilities { get; } = new(true, true, true, true);
        public event Action<int>? HotkeyPressed;
        private readonly Dictionary<Hotkey, int> _hotkeys = [];
        public bool OwnerKnown = true;
        public bool WaitForTransientFocus;
        public int Activations;
        public Task<bool> WaitForTargetAsync(nint window, Func<bool> stillValid, Action? onResumed = null) => WaitForTransientFocus
            ? TargetFocus.WaitAsync(window, () => ForegroundWindow, stillValid, TimeSpan.FromMilliseconds(250), onResumed)
            : Task.FromResult(window != 0 && ForegroundWindow == window && stillValid());
        public int TypedTextCount;
        public Action<int>? OnTypeChar;
        public Action? AfterPaste;
        /// <summary>The user presses a hotkey: the hook sees the shortcut first, then the OS delivers the hotkey.</summary>
        public void PressHotkey(string text)
        {
            var hotkey = Hotkey.TryParse(text)!;
            KeyTyped?.Invoke(TypedKey.ForShortcut(hotkey));
            if (_hotkeys.TryGetValue(hotkey, out var id)) HotkeyPressed?.Invoke(id);
        }
        public event Action<RawClip>? ClipboardCaptured { add { } remove { } }
        public event Action<TypedKey>? KeyTyped;
        public bool RegisterHotkey(int id, Hotkey hotkey) { _hotkeys[hotkey] = id; return true; }
        public void UnregisterAllHotkeys() => _hotkeys.Clear();
        public int SuspendDepth, SuspendCalls;
        public IDisposable SuspendCapture()
        {
            SuspendCalls++;
            SuspendDepth++;
            return new Scope(() => SuspendDepth--);
        }
        private ulong _sequence = 1;
        public Action? OnSequenceRead;
        public ulong ClipboardSequence
        {
            get { OnSequenceRead?.Invoke(); return _sequence; }
            private set => _sequence = value;
        }
        public nint ClipboardOwner;
        public void ExternalCopy(string text, nint owner = 0) { ClipboardText = text; _sequence++; ClipboardOwner = owner; }
        public bool? IsClipboardOwnedBy(nint window) => !OwnerKnown || ClipboardOwner == 0 ? null : ClipboardOwner == window;
        public string? ReadText() => ClipboardText;
        public void SetText(string text) { ExternalCopy(text); AfterWrite?.Invoke(text); }
        public void SetRichText(string text, string? html, string? rtf) => SetText(text);
        public void SetFiles(IReadOnlyList<string> paths) => SetText(string.Join('\n', paths));
        public void SetImage(byte[] png, byte[] bgra, int width, int height) => throw new NotSupportedException();
        public ClipboardSnapshot Snapshot()
        {
            var snapshot = new ClipboardSnapshot([("text", Encoding.UTF8.GetBytes(ClipboardText))]);
            AfterSnapshot?.Invoke();
            return snapshot;
        }
        public bool IsEmptySelectionLineCopy() => LastCopyWasLine && ReportLineCopyTag;
        public void Restore(ClipboardSnapshot snapshot) => ExternalCopy(Encoding.UTF8.GetString(snapshot.Formats[0].Data));
        public async Task WaitForModifiersReleasedAsync(TimeSpan timeout)
        {
            ModifiersEntered.TrySetResult();
            if (BeforeModifiers is not null) await BeforeModifiers();
            else if (ModifiersHeld) await Task.Delay(timeout);
        }
        public void SendChord(KeyModifiers modifiers, string key)
        {
            if (key == "Left")
            {
                Document.Left(modifiers.HasFlag(KeyModifiers.Shift), (modifiers & (KeyModifiers.Ctrl | KeyModifiers.Alt)) != 0);
                OnLeft?.Invoke();
            }
            else if (key == "Right") Document.Right();
            else if (key == "Backspace")
            {
                BackspaceCount++;
                Document.Backspace();
                OnBackspace?.Invoke();
            }
        }
        public void SendCopy()
        {
            LastCopyWasLine = Document.Selection.Length == 0 && CopyLineWhenUnselected;
            if (LastCopyWasLine) SetText(Document.Text + "\n");
            else if (Document.Selection.Length > 0) SetText(Document.Selection);
            if (LastCopyWasLine || Document.Selection.Length > 0) ClipboardOwner = Window; // the target app copied
            AfterCopy?.Invoke();
        }
        public bool TypeText(string text, Func<bool>? keepGoing = null)
        {
            TypedTextCount++;
            var typed = 0;
            foreach (var c in text)
            {
                if (keepGoing?.Invoke() == false) return false;
                Document.Type(c.ToString());
                OnTypeChar?.Invoke(++typed);
            }
            return true;
        }
        public void UserKey(TypedKey key) => KeyTyped?.Invoke(key);
        public void UserMoveCaret(int caret, bool keepAnchor = false)
        {
            Document.Caret = caret;
            if (!keepAnchor) Document.Anchor = null;
            KeyTyped?.Invoke(TypedKey.ResetKey);
        }
        public void SendPaste() { PasteCount++; Document.Type(ClipboardText); AfterPaste?.Invoke(); }
        public nint ForegroundWindow
        {
            get { OnForegroundRead?.Invoke(); var window = Window; ObservedForegroundWindows.Enqueue(window); AfterForegroundRead?.Invoke(); return window; }
        }
        public void ActivateWindow(nint handle) { Activations++; Window = handle; }
        public (int X, int Y) CursorPosition => (0, 0);
        public void SetTypingWatch(bool enabled) => TypingWatch = enabled;
        public void Type(string text)
            => TypeAs(text, text);

        public void TypeAs(string physicalKeys, string text)
        {
            InTypingEvent = true;
            try
            {
                for (var i = 0; i < text.Length; i++)
                {
                    Document.Type(text[i].ToString());
                    KeyTyped?.Invoke(TypedKey.ForChar(physicalKeys[i]));
                }
            }
            finally { InTypingEvent = false; }
        }
        public bool SwitchKeyboardLayout(nint window, int langId) => true;
        public bool SwitchKeyboardLayout(nint window, KeyboardLayout layout) { ActiveLayoutId = layout.Id; return true; }
        public bool SetKeepAwake(bool enabled) => true;
        public void SignalUserActivity() { }
        public bool AutostartEnabled { get; set; }
        public void Dispose() { }
        private sealed class Scope(Action onDispose) : IDisposable
        {
            private Action? _onDispose = onDispose;
            public void Dispose() { _onDispose?.Invoke(); _onDispose = null; }
        }
    }
}
