using MidFD.Helpers;
using MidFD.Models;
using MidFD.Presentation;
using MidFD.Services;

namespace MidFD;

public partial class MainForm
{
    private void HandleBrowserPanelDragEnterOrOver(DragEventArgs e, string operationName)
    {
        BrowserIncomingDragDecision decision = ResolveIncomingBrowserDragDecision(e);
        e.Effect = decision.Effect;
        if (decision.Intent != BrowserDragDropIntent.None)
        {
            _currentIncomingDragDecision = decision;
        }
        RefreshBrowserStatusSummary(decision.StatusText);
        LogService.Info(DragDropDataObjectDiagnosticHelper.GetDiagnosticLog(
            operationName,
            _uiMode.ToString(),
            IsActiveBrowserTabReadOnly(),
            _isClipboardBusy,
            HasInternalDragArchiveMarker(e.Data),
            e.Data,
            e.Effect,
            decision.Reason));
    }

    private BrowserIncomingDragDecision ResolveIncomingBrowserDragDecision(DragEventArgs e)
    {
        return BrowserIncomingDragResolver.Resolve(
            _uiMode == UIMode.Browser,
            IsActiveBrowserTabReadOnly(),
            _isClipboardBusy,
            HasInternalDragArchiveMarker(e.Data),
            e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop),
            BrowserImageDropService.HasImageData(e.Data),
            BrowserDropUrlResolverService.HasPotentialUrlData(e.Data),
            OutlookAttachmentDropService.IsOutlookAttachmentDrop(e.Data),
            e.KeyState);
    }

    private BrowserIncomingDragDecision ResolveIncomingDropDecision(DragEventArgs e)
    {
        // Drop event keyState might lose right mouse button flag (2) or modifier keys.
        // If DragOver remembered a Prompt or Move decision, prefer that.
        var eventDecision = ResolveIncomingBrowserDragDecision(e);
        if (_currentIncomingDragDecision != null)
        {
            // Only override if the current event is less specific (e.g. falls back to default Copy)
            if (eventDecision.Intent == BrowserDragDropIntent.Copy &&
                (_currentIncomingDragDecision.Intent == BrowserDragDropIntent.Prompt ||
                 _currentIncomingDragDecision.Intent == BrowserDragDropIntent.Move))
            {
                return _currentIncomingDragDecision;
            }
        }
        return eventDecision;
    }

    private void HandleBrowserPanelDragLeave()
    {
        _currentIncomingDragDecision = null;
        RefreshBrowserStatusSummary();
    }

    private BrowserDropAction ResolveBrowserDropAction(DragEventArgs e, BrowserIncomingDragDecision decision)
    {
        if (decision.Intent != BrowserDragDropIntent.Prompt)
        {
            return decision.Intent switch
            {
                BrowserDragDropIntent.Move => BrowserDropAction.Move,
                BrowserDragDropIntent.Copy => BrowserDropAction.Copy,
                _ => BrowserDropAction.Cancel
            };
        }

        return BrowserDropActionMenuPresenter.Show(this, new Point(e.X, e.Y));
    }

    private bool TryHandleBrowserFileDrop(DragEventArgs e, BrowserIncomingDragDecision decision)
    {
        if (e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return false;
        }

        string[]? files = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (files == null || files.Length == 0)
        {
            return true;
        }

        BrowserDropAction action = ResolveBrowserDropAction(e, decision);
        if (action == BrowserDropAction.Cancel)
        {
            ShowStatusMessage("ドロップ操作はキャンセルされました。");
            return true;
        }

        string operationLabel = action == BrowserDropAction.Move ? "移動" : "コピー";

        int successCount = 0;
        var successfulCreatedFilePaths = new List<string>();
        var successfulMoveUndoItems = new List<(string SourcePath, string DestinationPath)>();

        foreach (var sourcePath in files)
        {
            string fileName = Path.GetFileName(sourcePath);
            string destPath = Path.Combine(_navigationService.CurrentPath, fileName);
            if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase))
            {
                ShowStatusMessage("同一場所への移動・コピーは不要です。");
                continue;
            }
            bool sourceIsDir = Directory.Exists(sourcePath);
            bool destExists = File.Exists(destPath) || Directory.Exists(destPath);
            if (destExists)
            {
                bool destIsDir = Directory.Exists(destPath);
                if (sourceIsDir != destIsDir)
                {
                    MessageBox.Show($"型が異なるため上書きできません。\n宛先: {destPath}", "上書きエラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    continue;
                }

                if (sourceIsDir)
                {
                    MessageBox.Show($"フォルダ同士の上書き（統合）は現在未対応です。\nスキップします: {fileName}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    continue;
                }

                var overwriteMsg = FileOperationPresentationHelper.GetOverwriteConfirmationMessage(fileName);
                var overwriteResult = MessageBox.Show(overwriteMsg, "確認", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                if (overwriteResult == DialogResult.Cancel)
                {
                    break;
                }

                if (overwriteResult == DialogResult.No)
                {
                    continue;
                }
            }

            try
            {
                if (action == BrowserDropAction.Move)
                {
                    FileOperationService.Move(sourcePath, destPath, overwrite: destExists);
                    successfulMoveUndoItems.Add((sourcePath, destPath));
                }
                else
                {
                    FileOperationService.Copy(sourcePath, destPath);
                    successfulCreatedFilePaths.Add(destPath);
                }

                successCount++;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"{operationLabel}失敗: {fileName}\n{ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                break;
            }
        }

        // Record Undo/Redo history for successful items
        if (successCount > 0)
        {
            if (action == BrowserDropAction.Move && successfulMoveUndoItems.Count > 0)
            {
                var moveUndoItems = FileOperationUndoRedoService.CreateMoveBatch(successfulMoveUndoItems);
                _fileOperationUndoRedoService.RecordBatch(FileOperationUndoRedoOperation.Move, moveUndoItems);
            }
            else if (action == BrowserDropAction.Copy && successfulCreatedFilePaths.Count > 0)
            {
                var createdUndoItems = FileOperationUndoRedoService.CreateCreatedFilesBatch(successfulCreatedFilePaths);
                _fileOperationUndoRedoService.RecordBatch(FileOperationUndoRedoOperation.CreateFromPaste, createdUndoItems);
            }
        }

        LoadDirectory(_navigationService.CurrentPath);
        ShowStatusMessage($"{successCount} 件の項目をドロップ{operationLabel}しました。");
        RefreshBrowserStatusSummary();
        return true;
    }
}
