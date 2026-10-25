using System.Globalization;
using System.Text;
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
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "typlo-controller-" + Guid.NewGuid().ToString("N"));
    private readonly FakePlatform _platform = new();
    private readonly AppController _controller;

    public AppControllerTests() =>
        _controller = new(_platform, new AppPaths(_dir), new PlainKeyProtector(), diagnostic: true);

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
    public async Task LastWord_IncludesPunctuationInsideTheWrongLayoutWord(string input, string expected)
    {
        _platform.Document.Text = input;
        _platform.Document.Caret = input.Length;
        _platform.CopyLineWhenUnselected = true;
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
    public async Task Copy_UnknownOwner_FormatsFirstThenData_IsStillAdopted()
    {
        // X11: the copy first shows up without readable text, then with it. Same copy, not an external one.
        _platform.OwnerKnown = false;
        _platform.Document.Text = "abc";
        _platform.Document.SelectAll();
        var reads = int.MinValue;
        var selection = "";
        _platform.AfterCopy = () =>
        {
            reads = 0;
            selection = _platform.ClipboardText;
            _platform.ExternalCopy(""); // formats announced, data not readable yet
        };
        _platform.OnSequenceRead = () => { if (++reads == 2) _platform.ExternalCopy(selection); };
        await _controller.ChangeCaseAsync();
        Assert.Equal("Abc", _platform.Document.Text);
        Assert.Equal("original clipboard", _platform.ClipboardText);
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
        public nint Window = 1;
        public Document Document => _documents.TryGetValue(Window, out var doc) ? doc : _documents[Window] = new();
        public string ClipboardText = "original clipboard";
        public Action<string>? AfterWrite;
        public Action? OnForegroundRead, AfterForegroundRead, AfterSnapshot;
        public Func<Task>? BeforeModifiers;
        public bool CopyLineWhenUnselected, LastCopyWasLine, InTypingEvent, TypingWatch;
        public Action? OnBackspace, AfterCopy;
        public int PasteCount, BackspaceCount;
        public TaskCompletionSource ModifiersEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Name => "Fake platform (no OS access)";
        public PlatformCapabilities Capabilities { get; } = new(true, true, true, true);
        public event Action<int>? HotkeyPressed;
        private readonly Dictionary<Hotkey, int> _hotkeys = [];
        public bool OwnerKnown = true;
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
        public IDisposable SuspendCapture() => new Scope();
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
        public bool IsEmptySelectionLineCopy() => LastCopyWasLine;
        public void Restore(ClipboardSnapshot snapshot) => ExternalCopy(Encoding.UTF8.GetString(snapshot.Formats[0].Data));
        public Task WaitForModifiersReleasedAsync(TimeSpan timeout)
        {
            ModifiersEntered.TrySetResult();
            return BeforeModifiers?.Invoke() ?? Task.CompletedTask;
        }
        public void SendChord(KeyModifiers modifiers, string key)
        {
            if (key == "Left") Document.Left(modifiers.HasFlag(KeyModifiers.Shift), (modifiers & (KeyModifiers.Ctrl | KeyModifiers.Alt)) != 0);
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
            get { OnForegroundRead?.Invoke(); var window = Window; AfterForegroundRead?.Invoke(); return window; }
        }
        public void ActivateWindow(nint handle) => Window = handle;
        public (int X, int Y) CursorPosition => (0, 0);
        public void SetTypingWatch(bool enabled) => TypingWatch = enabled;
        public void Type(string text)
        {
            InTypingEvent = true;
            try { foreach (var c in text) { Document.Type(c.ToString()); KeyTyped?.Invoke(TypedKey.ForChar(c)); } }
            finally { InTypingEvent = false; }
        }
        public bool SwitchKeyboardLayout(nint window, int langId) => true;
        public bool SetKeepAwake(bool enabled) => true;
        public void SignalUserActivity() { }
        public bool AutostartEnabled { get; set; }
        public void Dispose() { }
        private sealed class Scope : IDisposable { public void Dispose() { } }
    }
}
