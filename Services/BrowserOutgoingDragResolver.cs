using System;
using System.Windows.Forms;

namespace MidFD.Services;

internal sealed class BrowserOutgoingDragDecision
{
    public DragDropEffects AllowedEffects { get; init; }
    public DragDropEffects PreferredEffect { get; init; }
    public bool HasPreferredEffect { get; init; }
    public string StatusText { get; init; } = string.Empty;
}

internal static class BrowserOutgoingDragResolver
{
    public static BrowserOutgoingDragDecision Resolve(
        bool isRightButton,
        Keys modifierKeys,
        bool isDragArchive = false)
    {
        if (isDragArchive)
        {
            return new BrowserOutgoingDragDecision
            {
                AllowedEffects = DragDropEffects.Copy,
                PreferredEffect = DragDropEffects.Copy,
                HasPreferredEffect = true,
                StatusText = "Drag: Copy"
            };
        }

        if (isRightButton)
        {
            return new BrowserOutgoingDragDecision
            {
                AllowedEffects = DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link,
                PreferredEffect = DragDropEffects.None,
                HasPreferredEffect = false,
                StatusText = "Drag: 操作を選択"
            };
        }

        if ((modifierKeys & Keys.Shift) != 0)
        {
            return new BrowserOutgoingDragDecision
            {
                AllowedEffects = DragDropEffects.Move,
                PreferredEffect = DragDropEffects.Move,
                HasPreferredEffect = true,
                StatusText = "Drag: Move"
            };
        }

        if ((modifierKeys & Keys.Control) != 0)
        {
            return new BrowserOutgoingDragDecision
            {
                AllowedEffects = DragDropEffects.Copy,
                PreferredEffect = DragDropEffects.Copy,
                HasPreferredEffect = true,
                StatusText = "Drag: Copy"
            };
        }

        return new BrowserOutgoingDragDecision
        {
            AllowedEffects = DragDropEffects.Copy | DragDropEffects.Move,
            PreferredEffect = DragDropEffects.None,
            HasPreferredEffect = false,
            StatusText = "Drag: Copy" // default left drag is copy/move but shown as default
        };
    }
}
