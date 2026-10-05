using MidFD.Models;
using MidFD.Runtime;
using MidFD.Presentation;
using MidFD.Services;

namespace MidFD;

public partial class MainForm
{
    private void RefreshBrowserStatusSummary(string? dragStatusText = null)
    {
        if (_notificationService == null || _viewerApplicationCoordinator.Mode != ViewerApplicationMode.Browser)
        {
            return;
        }

        ShellClipboardService.TryGetStatus(out var clipboardStatus, out _);

        bool canPaste = !IsActiveBrowserTabReadOnly()
            && !IsCurrentDirectoryBusy()
            && !_fileOperationApplicationCoordinator.IsClipboardBusy
            && !string.IsNullOrWhiteSpace(_browserApplicationCoordinator.CurrentPath)
            && (clipboardStatus.HasFileDrop
                || clipboardStatus.HasImage
                || ((_settingsCoordinator.Value.FileOperations?.ClipboardPasteTextAsFileEnabled ?? false) && clipboardStatus.HasText));

        BrowserClipboardStatusMode clipboardMode = BrowserClipboardStatusMode.None;
        int clipboardCount = 0;
        if (clipboardStatus.HasFileDrop && clipboardStatus.FileDropCount > 0)
        {
            clipboardMode = clipboardStatus.IsCut ? BrowserClipboardStatusMode.Cut : BrowserClipboardStatusMode.Copy;
            clipboardCount = clipboardStatus.FileDropCount;
            canPaste = true;
        }

        SelectionResult selection = ResolveSelection();
        BrowserStatusSummaryState state = BrowserStatusSummaryProjection.Build(
            _browserApplicationCoordinator.Selection.Count,
            selection,
            clipboardMode,
            clipboardCount,
            canPaste,
            dragStatusText);

        _notificationService.SetDefaultMessage(
            BrowserStatusSummaryFormatter.Format(state),
            StatusKind.Normal,
            applyToVisibleMessage: !_notificationService.IsTemporaryMessageActive);
    }
}
