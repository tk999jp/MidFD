using System.Windows.Forms;
using MidFD.Models;

namespace MidFD.Services;

internal static class BrowserIncomingDragResolver
{
    private const int LeftMouseButtonMask = 1;
    private const int RightMouseButtonMask = 2;
    private const int ShiftMask = 4;
    private const int ControlMask = 8;

    public static BrowserIncomingDragDecision Resolve(
        bool isBrowserMode,
        bool isReadOnly,
        bool isClipboardBusy,
        bool hasInternalDragArchiveMarker,
        bool hasFileDrop,
        bool hasImageData,
        bool hasPotentialUrlData,
        bool hasOutlookAttachmentDrop,
        int keyState)
    {
        if (!isBrowserMode)
        {
            return Blocked("uiModeNotBrowser");
        }

        if (isReadOnly)
        {
            return Blocked("readOnlyBlocked");
        }

        if (hasInternalDragArchiveMarker)
        {
            return Blocked("internalMarkerBlocked");
        }

        if (isClipboardBusy)
        {
            return Blocked("clipboardBusyBlocked");
        }

        bool isFileLikeDrop = hasFileDrop || hasOutlookAttachmentDrop;
        if (!isFileLikeDrop && !hasImageData && !hasPotentialUrlData)
        {
            return Blocked("unsupportedFormat");
        }

        if (!isFileLikeDrop)
        {
            return new BrowserIncomingDragDecision
            {
                Effect = DragDropEffects.Copy,
                Intent = BrowserDragDropIntent.Copy,
                Reason = hasImageData ? "imageAccepted" : "imageUrlAccepted",
                StatusText = "Drag: Copy"
            };
        }

        bool isRightDrag = (keyState & RightMouseButtonMask) != 0;
        if (isRightDrag)
        {
            return new BrowserIncomingDragDecision
            {
                Effect = DragDropEffects.Copy | DragDropEffects.Move,
                Intent = BrowserDragDropIntent.Prompt,
                Reason = "rightDragPrompt",
                StatusText = "Drag: 操作を選択",
                IsFileDrop = true
            };
        }

        if ((keyState & ShiftMask) != 0 && (keyState & ControlMask) == 0 && !hasOutlookAttachmentDrop)
        {
            return new BrowserIncomingDragDecision
            {
                Effect = DragDropEffects.Move,
                Intent = BrowserDragDropIntent.Move,
                Reason = "shiftMove",
                StatusText = "Drag: Move",
                IsFileDrop = true
            };
        }

        return new BrowserIncomingDragDecision
        {
            Effect = DragDropEffects.Copy,
            Intent = BrowserDragDropIntent.Copy,
            Reason = (keyState & ControlMask) != 0 ? "ctrlCopy" : "defaultCopy",
            StatusText = "Drag: Copy",
            IsFileDrop = true
        };
    }

    private static BrowserIncomingDragDecision Blocked(string reason)
    {
        return new BrowserIncomingDragDecision
        {
            Effect = DragDropEffects.None,
            Intent = BrowserDragDropIntent.None,
            Reason = reason,
            StatusText = "Drag: Drop不可"
        };
    }
}
