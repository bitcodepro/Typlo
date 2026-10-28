namespace Typlo.App.Platform;

public enum DirectCaptureStatus { Unsupported, Ready, Cancelled }
public enum DirectEditStatus { Applied, Unsupported, Cancelled, Uncertain }

/// <summary>Unsupported is a read-only capability decline; Cancelled must not trigger keyboard fallback.</summary>
public sealed record DirectTextCapture(DirectCaptureStatus Status, DirectTextSnapshot? Snapshot = null)
{
    public static readonly DirectTextCapture Unsupported = new(DirectCaptureStatus.Unsupported);
    public static readonly DirectTextCapture Cancelled = new(DirectCaptureStatus.Cancelled);
}

/// <summary>Uncertain includes a timed-out write: it may still execute, so never paste again.</summary>
public sealed record DirectTextEditResult(DirectEditStatus Status, DirectTextSnapshot? After = null);

/// <summary>
/// Immutable, bounded, in-memory view of one editable control. No clipboard, activation or keystrokes.
/// Implementations verify control identity, contents and selection before writing. Unsupported is
/// permitted only before any selection/text mutation. After a mutation, any failure is terminal.
/// </summary>
public abstract class DirectTextSnapshot(nint window, nint element, string text, int selectionStart, int selectionEnd)
{
    public nint Window { get; } = window;
    public nint Element { get; } = element;
    public string Text { get; } = text;
    public int SelectionStart { get; } = selectionStart;
    public int SelectionEnd { get; } = selectionEnd;
    public abstract Task<DirectTextSnapshot?> RefreshAsync(Func<bool> stillValid);
    public abstract Task<DirectTextEditResult> ReplaceAsync(int start, int length, string replacement, Func<bool> stillValid);

    public bool SameContext(DirectTextSnapshot other) => Window == other.Window && Element == other.Element &&
        Text == other.Text && SelectionStart == other.SelectionStart && SelectionEnd == other.SelectionEnd;
}
