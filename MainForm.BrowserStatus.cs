using MidFD.Models;
using MidFD.Presentation;
using MidFD.Services;

namespace MidFD;

public partial class MainForm
{
    private void RefreshBrowserStatusSummary(string? dragStatusText = null)
    {
        if (_notificationService == null || _uiMode != UIMode.Browser)
        {
            return;
        }

        bool canPaste = !IsActiveBrowserTabReadOnly()
            && !IsCurrentDirectoryBusy()
            && !_isClipboardBusy
            && !string.IsNullOrWhiteSpace(_navigationService.CurrentPath)
            && (ShellClipboardService.HasFileDrop()
                || ShellClipboardService.HasImage()
                || ((_settings.FileOperations?.ClipboardPasteTextAsFileEnabled ?? false) && ShellClipboardService.HasText()));

        BrowserClipboardStatusMode clipboardMode = BrowserClipboardStatusMode.None;
        int clipboardCount = 0;
        if (ShellClipboardService.TryGetSnapshot(out var snapshot, out _)
            && snapshot != null)
        {
            clipboardMode = snapshot.IsCut ? BrowserClipboardStatusMode.Cut : BrowserClipboardStatusMode.Copy;
            clipboardCount = snapshot.Paths.Count;
            canPaste = true;
        }

        SelectionResult selection = ResolveSelection();
        string targetText = selection.Count == 0
            ? "Target: none"
            : selection.HasMarkedSelection ? "Target: mark" : "Target: select";

        var state = new BrowserStatusSummaryState
        {
            MarkCount = _markedFiles.Count,
            SelectionCount = selection.Count,
            TargetText = targetText,
            ClipboardMode = clipboardMode,
            ClipboardCount = clipboardCount,
            CanPaste = canPaste,
            DragStatusText = dragStatusText
        };

        _notificationService.SetDefaultMessage(
            BrowserStatusSummaryFormatter.Format(state),
            StatusKind.Normal,
            applyToVisibleMessage: !_notificationService.IsTemporaryMessageActive);
    }
}
