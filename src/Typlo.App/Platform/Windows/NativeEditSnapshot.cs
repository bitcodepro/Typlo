using System.Diagnostics;
using System.Runtime.Versioning;
using static Typlo.App.Platform.Windows.Native;

namespace Typlo.App.Platform.Windows;

/// <summary>
/// Opt-in native text controls only. All pointer-bearing messages are below WM_USER (Windows
/// marshals them across processes). No UIA tree walks, COM, activation, clipboard, or key events.
/// Reads are capped at 32K UTF-16 units, also making packed EM_GETSEL offsets unambiguous.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class NativeEditSnapshot : DirectTextSnapshot
{
    internal const int MaxText = 32760;
    internal static bool CanRetain(nint window, nint element, Func<uint, bool> excluded)
        => Identify(window, element, excluded) is not null;
    private const uint GetLength = 0x000e, GetText = 0x000d, GetSel = 0x00b0, SetSel = 0x00b1,
        ReplaceSel = 0x00c2, GetTextMode = 0x0400 + 90;
    private readonly Identity _identity;
    private readonly Func<uint, bool> _excluded;

    private NativeEditSnapshot(Identity identity, string text, int start, int end, Func<uint, bool> excluded)
        : base(identity.Window, identity.Element, text, start, end) => (_identity, _excluded) = (identity, excluded);

    internal static DirectTextCapture Capture(nint window, nint element, Func<bool> valid, Func<uint, bool> excluded)
    {
        if (!valid()) return DirectTextCapture.Cancelled;
        var identity = Identify(window, element, excluded);
        if (identity is null) return DirectTextCapture.Unsupported;
        var deadline = new Deadline();
        if (!deadline.Send(element, GetLength, 0, 0, out var size)) return DirectTextCapture.Cancelled;
        if (size > MaxText) return DirectTextCapture.Unsupported;
        if (identity.Rich)
        {
            // Scalar-only message above WM_USER: no remote pointer. Rich formatting is not flattened.
            if (!deadline.Send(element, GetTextMode, 0, 0, out var mode)) return DirectTextCapture.Cancelled;
            if ((mode & 1) == 0) return DirectTextCapture.Unsupported;
        }
        var state = Read(identity, excluded, valid, deadline);
        if (state is null) return DirectTextCapture.Cancelled;
        // RichEdit uses internal CR offsets whereas WM_GETTEXT can return CRLF. Until an adapter
        // explicitly maps them, decline multiline content rather than selecting the wrong characters.
        if (identity.Rich && state.Text.IndexOfAny(['\r', '\n']) >= 0) return DirectTextCapture.Unsupported;
        return new(DirectCaptureStatus.Ready, state);
    }

    public override Task<DirectTextSnapshot?> RefreshAsync(Func<bool> stillValid)
        => Task.Run<DirectTextSnapshot?>(() => Read(_identity, _excluded, stillValid, new Deadline()));

    public override Task<DirectTextEditResult> ReplaceAsync(int start, int length, string replacement, Func<bool> stillValid)
        => Task.Run(() => Replace(start, length, replacement, stillValid));

    private unsafe DirectTextEditResult Replace(int start, int length, string replacement, Func<bool> valid)
    {
        if (start < 0 || length < 0 || start > Text.Length - length || replacement.Contains('\0') ||
            Text.Length - length + replacement.Length > MaxText || !Boundary(Text, start) || !Boundary(Text, start + length) ||
            (_identity.Rich && replacement.IndexOfAny(['\r', '\n']) >= 0)) return new(DirectEditStatus.Unsupported);
        var deadline = new Deadline();
        var current = Read(_identity, _excluded, valid, deadline);
        if (current is null || !SameContext(current) || !valid()) return new(DirectEditStatus.Cancelled);
        // After the first mutating message, all failures are indeterminate; never keyboard-fallback.
        if (!deadline.Send(Element, SetSel, (nuint)start, start + length, out _)) return new(DirectEditStatus.Uncertain);
        current = Read(_identity, _excluded, valid, deadline);
        if (current is null || current.Text != Text || current.SelectionStart != start ||
            current.SelectionEnd != start + length || !valid()) return new(DirectEditStatus.Uncertain);
        fixed (char* chars = replacement)
            if (!deadline.Send(Element, ReplaceSel, 1, (nint)chars, out _)) return new(DirectEditStatus.Uncertain);
        var after = Read(_identity, _excluded, valid, deadline);
        var expected = Text[..start] + replacement + Text[(start + length)..];
        return after is not null && after.Text == expected && after.SelectionStart == start + replacement.Length &&
            after.SelectionEnd == after.SelectionStart && valid()
            ? new(DirectEditStatus.Applied, after) : new(DirectEditStatus.Uncertain);
    }

    private static bool Boundary(string text, int at) => at == 0 || at == text.Length ||
        !(char.IsHighSurrogate(text[at - 1]) && char.IsLowSurrogate(text[at])) && !(text[at - 1] == '\r' && text[at] == '\n');

    private static unsafe NativeEditSnapshot? Read(Identity identity, Func<uint, bool> excluded, Func<bool> valid, Deadline deadline)
    {
        if (!valid() || Identify(identity.Window, identity.Element, excluded) != identity) return null;
        if (identity.Rich && (!deadline.Send(identity.Element, GetTextMode, 0, 0, out var mode) || (mode & 1) == 0)) return null;
        if (!deadline.Send(identity.Element, GetLength, 0, 0, out var length) || length > MaxText) return null;
        var buffer = new char[(int)length + 1];
        nuint copied;
        fixed (char* chars = buffer)
            if (!deadline.Send(identity.Element, GetText, (nuint)buffer.Length, (nint)chars, out copied)) return null;
        if (copied != length || !deadline.Send(identity.Element, GetSel, 0, 0, out var selection) ||
            !deadline.Send(identity.Element, GetLength, 0, 0, out var afterLength) || afterLength != length) return null;
        var start = (int)(selection & 0xffff);
        var end = (int)((selection >> 16) & 0xffff);
        if (start > end || end > (int)length || !valid() || Identify(identity.Window, identity.Element, excluded) != identity) return null;
        return new(identity, new string(buffer, 0, (int)copied), start, end, excluded);
    }

    private sealed record Identity(nint Window, nint Element, uint RootThread, uint RootPid, uint Thread, uint Pid, string Class, bool Rich);

    private static unsafe Identity? Identify(nint window, nint element, Func<uint, bool> excluded)
    {
        if (window == 0 || element == 0 || !IsWindow(window) || !IsWindow(element) || GetAncestor(element, 2) != window ||
            !IsWindowEnabled(element) || !IsWindowUnicode(element) || (GetWindowStyle(element, -16) & (0x20 | 0x800)) != 0) return null;
        var thread = GetWindowThreadProcessId(element, out var pid);
        var rootThread = GetWindowThreadProcessId(window, out var rootPid);
        if (thread == 0 || rootThread == 0 || excluded(pid) || excluded(rootPid)) return null;
        char* name = stackalloc char[256];
        var count = GetClassNameW(element, name, 256);
        if (count <= 0) return null;
        var cls = new string(name, 0, count);
        var edit = cls.Equals("Edit", StringComparison.OrdinalIgnoreCase) || cls.StartsWith("WindowsForms10.EDIT.", StringComparison.OrdinalIgnoreCase);
        var rich = cls.Equals("RICHEDIT50W", StringComparison.OrdinalIgnoreCase) || cls.Equals("RichEdit20W", StringComparison.OrdinalIgnoreCase) ||
            cls.StartsWith("WindowsForms10.RICHEDIT50W.", StringComparison.OrdinalIgnoreCase) ||
            cls.StartsWith("WindowsForms10.RichEdit20W.", StringComparison.OrdinalIgnoreCase);
        return edit || rich ? new(window, element, rootThread, rootPid, thread, pid, cls, rich) : null;
    }

    private sealed class Deadline
    {
        private readonly long _start = Stopwatch.GetTimestamp();
        internal bool Send(nint window, uint message, nuint wParam, nint lParam, out nuint result)
        {
            result = 0;
            var remaining = 250 - (int)Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
            return remaining > 0 && SendMessageTimeoutW(window, message, wParam, lParam, 0x1 | 0x2 | 0x20,
                (uint)Math.Min(40, remaining), out result) != 0;
        }
    }
}
