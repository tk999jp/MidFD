namespace MidFD.Models;

internal enum BrowserClipboardStatusMode
{
    None,
    Copy,
    Cut
}

internal sealed class BrowserStatusSummaryState
{
    public int MarkCount { get; init; }
    public int SelectionCount { get; init; }
    public string TargetText { get; init; } = string.Empty;
    public BrowserClipboardStatusMode ClipboardMode { get; init; }
    public int ClipboardCount { get; init; }
    public bool CanPaste { get; init; }
    public string? DragStatusText { get; init; }
}
