using MidFD.Helpers;
using MidFD.Models;
using MidFD.Presentation;
using MidFD.Services;

namespace MidFD;

public partial class MainForm
{
    internal enum BrowserDropItemClassification
    {
        Success,
        Skip,
        Fail,
        Cancel,
        NoOp
    }

    internal readonly record struct BrowserDropItemResult(
        BrowserDropItemClassification Classification,
        bool Partial,
        int NestedSkipCount,
        int NestedFailCount);

    internal readonly record struct BrowserDropCounters(
        int SuccessCount = 0,
        int SkipCount = 0,
        int FailCount = 0,
        int CancelCount = 0,
        int NoOpCount = 0,
        int PartialSkipCount = 0,
        int PartialCancelCount = 0,
        int PartialFailCount = 0,
        int NestedSkipCount = 0,
        int NestedFailCount = 0)
    {
        public BrowserDropCounters Add(BrowserDropItemResult item)
        {
            return item.Classification switch
            {
                BrowserDropItemClassification.Success => this with { SuccessCount = SuccessCount + 1 },
                BrowserDropItemClassification.Skip => this with
                {
                    SkipCount = SkipCount + 1,
                    PartialSkipCount = PartialSkipCount + (item.Partial ? 1 : 0)
                },
                BrowserDropItemClassification.Fail => this with
                {
                    FailCount = FailCount + 1,
                    PartialFailCount = PartialFailCount + (item.Partial ? 1 : 0)
                },
                BrowserDropItemClassification.Cancel => this with
                {
                    CancelCount = CancelCount + 1,
                    PartialCancelCount = PartialCancelCount + (item.Partial ? 1 : 0)
                },
                BrowserDropItemClassification.NoOp => this with { NoOpCount = NoOpCount + 1 },
                _ => this
            } with
            {
                NestedSkipCount = NestedSkipCount + item.NestedSkipCount,
                NestedFailCount = NestedFailCount + item.NestedFailCount
            };
        }
    }

    internal static BrowserDropItemResult ClassifyDirectoryMergeResult(
        int successCount,
        int skipCount,
        int failCount,
        bool canceled)
    {
        if (canceled)
        {
            return new BrowserDropItemResult(
                BrowserDropItemClassification.Cancel,
                successCount > 0,
                skipCount,
                failCount);
        }

        if (failCount > 0)
        {
            return new BrowserDropItemResult(
                BrowserDropItemClassification.Fail,
                successCount > 0,
                skipCount,
                failCount);
        }

        if (successCount > 0 && skipCount > 0)
        {
            return new BrowserDropItemResult(
                BrowserDropItemClassification.Skip,
                true,
                skipCount,
                0);
        }

        if (skipCount > 0)
        {
            return new BrowserDropItemResult(
                BrowserDropItemClassification.Skip,
                false,
                skipCount,
                0);
        }

        return new BrowserDropItemResult(
            BrowserDropItemClassification.Success,
            false,
            0,
            0);
    }

    internal static BrowserDropItemClassification ClassifyBrowserDropTypeMismatch(bool sourceIsDirectory, bool destinationIsDirectory)
    {
        return sourceIsDirectory != destinationIsDirectory
            ? BrowserDropItemClassification.Fail
            : BrowserDropItemClassification.Success;
    }

    internal static string FormatBrowserDropResult(
        string operationLabel,
        BrowserDropCounters counters)
    {
        string result = $"ドロップ{operationLabel}: {counters.SuccessCount} 件成功、{counters.SkipCount} 件スキップ、{counters.FailCount} 件失敗、{counters.CancelCount} 件キャンセル、{counters.NoOpCount} 件不要";
        if (counters.PartialSkipCount > 0 || counters.PartialCancelCount > 0 || counters.PartialFailCount > 0)
        {
            result += $"（partial: skip {counters.PartialSkipCount}、cancel {counters.PartialCancelCount}、fail {counters.PartialFailCount}）";
        }
        if (counters.NestedSkipCount > 0 || counters.NestedFailCount > 0)
        {
            result += $"（merge内訳: skip {counters.NestedSkipCount}、fail {counters.NestedFailCount}）";
        }
        return result;
    }

    internal static StatusKind ResolveBrowserDropStatusKind(BrowserDropCounters counters)
    {
        if (counters.FailCount > 0 || counters.CancelCount > 0)
        {
            return StatusKind.Error;
        }

        return counters.SuccessCount > 0 && counters.SkipCount == 0 && counters.NoOpCount == 0
            ? StatusKind.Result
            : StatusKind.Normal;
    }

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

        BrowserDropCounters counters = new();
        var successfulCreatedFilePaths = new List<string>();
        var successfulMoveUndoItems = new List<(string SourcePath, string DestinationPath)>();

        foreach (var sourcePath in files)
        {
            string fileName = Path.GetFileName(sourcePath);
            string destPath = Path.Combine(_navigationService.CurrentPath, fileName);
            if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destPath), StringComparison.OrdinalIgnoreCase))
            {
                ShowStatusMessage("同一場所への移動・コピーは不要です。");
                counters = counters.Add(new BrowserDropItemResult(BrowserDropItemClassification.NoOp, false, 0, 0));
                continue;
            }
            bool sourceIsDir = Directory.Exists(sourcePath);
            bool destExists = File.Exists(destPath) || Directory.Exists(destPath);
            if (destExists)
            {
                bool destIsDir = Directory.Exists(destPath);
                if (ClassifyBrowserDropTypeMismatch(sourceIsDir, destIsDir) == BrowserDropItemClassification.Fail)
                {
                    MessageBox.Show($"型が異なるため上書きできません。\n宛先: {destPath}", "上書きエラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    counters = counters.Add(new BrowserDropItemResult(BrowserDropItemClassification.Fail, false, 0, 0));
                    continue;
                }

                if (sourceIsDir)
                {
                    CopyCollisionDecision? mergeFileDecision = null;
                    DirectoryMergeDecision? directoryDecision = null;
                    bool canMerge = action == BrowserDropAction.Move
                        ? TryResolveMoveDirectoryMerge(sourcePath, destPath, ref directoryDecision, out bool shouldSkip, out bool shouldCancel)
                        : TryResolveCopyDirectoryMerge(sourcePath, destPath, ref directoryDecision, out shouldSkip, out shouldCancel);
                    if (shouldCancel)
                    {
                        counters = counters.Add(new BrowserDropItemResult(BrowserDropItemClassification.Cancel, false, 0, 0));
                        break;
                    }
                    if (shouldSkip)
                    {
                        counters = counters.Add(new BrowserDropItemResult(BrowserDropItemClassification.Skip, false, 0, 0));
                        continue;
                    }
                    if (!canMerge)
                    {
                        counters = counters.Add(new BrowserDropItemResult(BrowserDropItemClassification.Fail, false, 0, 0));
                        continue;
                    }

                    var mergeState = new DirectoryMergeExecutionState();
                    BrowserDropItemResult mergeResult;
                    try
                    {
                        if (action == BrowserDropAction.Move)
                        {
                            PasteMoveDirectoryIntoExisting(sourcePath, destPath, ref mergeFileDecision, out bool directoryShouldCancel, out _, out _, mergeState);
                            if (directoryShouldCancel)
                            {
                                mergeResult = ClassifyDirectoryMergeResult(mergeState.SuccessCount, mergeState.SkipCount, mergeState.FailCount, true);
                                counters = counters.Add(mergeResult);
                                break;
                            }
                        }
                        else
                        {
                            PasteCopyDirectoryIntoExisting(sourcePath, destPath, ref mergeFileDecision, out bool directoryShouldCancel, mergeState);
                            if (directoryShouldCancel)
                            {
                                mergeResult = ClassifyDirectoryMergeResult(mergeState.SuccessCount, mergeState.SkipCount, mergeState.FailCount, true);
                                counters = counters.Add(mergeResult);
                                break;
                            }
                        }
                        mergeResult = ClassifyDirectoryMergeResult(mergeState.SuccessCount, mergeState.SkipCount, mergeState.FailCount, false);
                        counters = counters.Add(mergeResult);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"{operationLabel}失敗: {fileName}\n{ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        counters = counters.Add(new BrowserDropItemResult(BrowserDropItemClassification.Fail, mergeState.SuccessCount > 0, mergeState.SkipCount, mergeState.FailCount));
                        break;
                    }
                    continue;
                }

                var overwriteMsg = FileOperationPresentationHelper.GetOverwriteConfirmationMessage(fileName);
                var overwriteResult = MessageBox.Show(overwriteMsg, "確認", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                if (overwriteResult == DialogResult.Cancel)
                {
                    counters = counters.Add(new BrowserDropItemResult(BrowserDropItemClassification.Cancel, false, 0, 0));
                    break;
                }

                if (overwriteResult == DialogResult.No)
                {
                    counters = counters.Add(new BrowserDropItemResult(BrowserDropItemClassification.Skip, false, 0, 0));
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

                counters = counters.Add(new BrowserDropItemResult(BrowserDropItemClassification.Success, false, 0, 0));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"{operationLabel}失敗: {fileName}\n{ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                counters = counters.Add(new BrowserDropItemResult(BrowserDropItemClassification.Fail, false, 0, 0));
                break;
            }
        }

        // Record Undo/Redo history for successful items
        if (counters.SuccessCount > 0)
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

        if (counters.SuccessCount > 0 ||
            counters.PartialSkipCount > 0 ||
            counters.PartialCancelCount > 0 ||
            counters.PartialFailCount > 0)
        {
            RearmCurrentDirectoryWatcherAfterInternalMutation(_navigationService.CurrentPath);
        }

        LoadDirectory(_navigationService.CurrentPath);
        ShowStatusMessage(
            FormatBrowserDropResult(operationLabel, counters),
            0,
            ResolveBrowserDropStatusKind(counters));
        RefreshBrowserStatusSummary();
        return true;
    }
}
