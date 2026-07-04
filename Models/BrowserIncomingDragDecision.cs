using System.Windows.Forms;

namespace MidFD.Models;

internal sealed class BrowserIncomingDragDecision
{
    public DragDropEffects Effect { get; init; } = DragDropEffects.None;
    public BrowserDragDropIntent Intent { get; init; } = BrowserDragDropIntent.None;
    public string Reason { get; init; } = string.Empty;
    public string StatusText { get; init; } = "Drag: Drop不可";
    public bool IsFileDrop { get; init; }
}
