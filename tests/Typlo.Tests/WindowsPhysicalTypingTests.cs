using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpHook.Data;
using Typlo.App.Platform.Shared;
using Typlo.App.Platform.Windows;
using Typlo.App.Services;
using Typlo.Core.Layouts;

namespace Typlo.Tests;

[SupportedOSPlatform("windows")]
public sealed partial class WindowsPhysicalTypingTests
{
    [Fact]
    public void FrenchHookDefault_ProducesUsPhysicalKeysForRecentConversionAndSnippets()
    {
        if (!OperatingSystem.IsWindows()) return;
        var previouslyEnabled = Enabled();
        // Load translation tables without activating the layout or notifying Shell. No user settings change.
        var fr = LoadKeyboardLayoutW("0000040c", 0x80 /* KLF_NOTELLSHELL */);
        var en = LoadKeyboardLayoutW("00000409", 0x80);
        Assert.NotEqual(0, fr);
        Assert.NotEqual(0, en);
        try
        {
            using var hook = new TypingEventsTests.FakeHook();
            var raised = new List<TypedKey>();
            TypingEvents.Attach(hook, raised.Add, physicalCharacter:
                (code, raw, shift) => WindowsKeyboardLayouts.PhysicalCharacter(code, raw, shift, fr, fr));
            hook.Press(KeyCode.VcA, 0x41); // Physical Q -> French VK_A -> libuiohook VcA.
            hook.Release(KeyCode.VcA);
            hook.Press(KeyCode.VcQ, 0x51); // Physical A.
            hook.Release(KeyCode.VcQ);
            hook.Press(KeyCode.VcM, 0x4d); // Physical semicolon -> French M.
            hook.Release(KeyCode.VcM);
            hook.Press(KeyCode.VcLeftShift);
            hook.Press(KeyCode.VcA, 0x41);
            hook.Release(KeyCode.VcA);
            hook.Release(KeyCode.VcLeftShift);
            Assert.Equal("qa;Q", new string(raised.Select(k => k.Char).ToArray()));

            var reference = KeyboardLayouts.English;
            static string Swap(string s) => s.Replace('a', '\0').Replace('q', 'a').Replace('\0', 'q')
                .Replace('A', '\0').Replace('Q', 'A').Replace('\0', 'Q');
            var french = new KeyboardLayout("native:fr", "French", 0, Swap(reference.Normal), Swap(reference.Shifted), "fr");
            var run = new RecentTyping.Run(1, raised[0].Char.ToString());
            var source = run.MatchSource("a", [french, reference]);
            Assert.Same(french, source);
            Assert.Equal("q", new LayoutConverter([french, reference]).ConvertTo("a", reference, source).Text);
            var matcher = new Typlo.Core.Text.SnippetMatcher();
            matcher.SetSnippets([new() { Trigger = "az", Text = "done" }], [french]);
            Assert.Null(matcher.OnChar(raised[0].Char));
            Assert.Equal("done", matcher.OnChar('z')?.Text);

            // Hook is French, foreground is US: alphanumeric KeyCode still uses the hook's layout;
            // OEM RawCode instead belongs to the foreground window, and must use the US table.
            Assert.Equal('q', WindowsKeyboardLayouts.PhysicalCharacter(KeyCode.VcA, 0x51, false, fr, en));
            Assert.Equal(';', WindowsKeyboardLayouts.PhysicalCharacter(KeyCode.VcSemicolon, 0xba, false, fr, en));
            Assert.Null(WindowsKeyboardLayouts.PhysicalCharacter(KeyCode.VcUndefined, 0, false, fr, en));
        }
        finally
        {
            var restoredFrench = previouslyEnabled.Contains(fr) || UnloadKeyboardLayout(fr);
            var restoredEnglish = previouslyEnabled.Contains(en) || UnloadKeyboardLayout(en);
            Assert.True(restoredFrench && restoredEnglish, "Temporary keyboard-layout tables must be unloaded after the test.");
        }
    }

    private static HashSet<nint> Enabled()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var handles = new nint[GetKeyboardLayoutList(0, null)];
        GetKeyboardLayoutList(handles.Length, handles);
        return handles.ToHashSet();
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadKeyboardLayoutW(string id, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnloadKeyboardLayout(nint layout);
    [DllImport("user32.dll")] private static extern int GetKeyboardLayoutList(int count, [Out] nint[]? layouts);
}
