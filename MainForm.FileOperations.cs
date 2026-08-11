using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MidFD.Configuration;
using MidFD.Dialogs;
using MidFD.Helpers;
using MidFD.Models;
using MidFD.Services;
using MidFD.Services.TrashManifestStore;

namespace MidFD;

public partial class MainForm
{
    private sealed class DirectoryMergeExecutionState
    {
        public int SuccessCount { get; set; }
        public int SkipCount { get; set; }
        public int FailCount { get; set; }
        public bool Canceled { get; set; }
    }

    private void ExecuteRename(SelectionResult? selectionSnapshot = null)
    {
        if (GuardMutationBusy()) return;
        if (GuardReadOnlyBrowserTab())
        {
            return;
        }
        var entryPlan = _fileOperationEntryCoordinator.CreateSelectionEntryPlan(
            _isClipboardBusy,
            _fileOpUiState.ActiveOperationName,
            _fileOpUiState.Cts != null,
            "リネーム",
            ResolveSelection(selectionSnapshot),
            "リネーム対象がありません。");
        if (!entryPlan.CanProceed)
        {
            if (!string.IsNullOrEmpty(entryPlan.StatusMessage))
            {
                ShowStatusMessage(entryPlan.StatusMessage, 1000);
            }
            return;
        }
        var selection = entryPlan.Selection;
        if (!TryResolveMultiMarkSelectionAction("リネーム", "リネームをキャンセルしました。", selection, out selection))
        {
            return;
        }
        if (selection.Count == 1)
        {
            ExecuteSingleRename(selection.FirstPath);
            return;
        }
        ExecuteRenameEntry(selection);
    }
    private void ExecuteRenameEntry(SelectionResult selection)
    {
        var dialogResult = _renameDialogCoordinator.ShowEntryDialog(this, selection.FullPaths);
        if (!dialogResult.Confirmed || dialogResult.Mode == RenameEntryMode.Cancel)
        {
            ShowStatusMessage("リネームはキャンセルされました。");
            return;
        }
        if (dialogResult.Mode == RenameEntryMode.SingleStep)
        {
            ExecuteSequentialRename(selection, dialogResult.SingleStepInitialName);
            return;
        }
        ExecuteBatchRename(selection);
    }
    private void ExecuteSingleRename(string? sourcePath)
    {
        var outcome = _renameApplyCoordinator.ApplySingleRename(
            sourcePath ?? string.Empty,
            initialValue: null,
            showNoChangeStatus: true,
            showValidationMessage: true,
            (path, value, skipInitialPrompt, showValidation) =>
                _renameDialogCoordinator.ShowSingleRenameDialog(this, path, value, skipInitialPrompt, showValidation),
            GetFriendlyRenameErrorMessage,
            message => MessageBox.Show(message, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error),
            BuildRenameUndoReadyMessage);
        ApplyRenameOutcome(outcome);
    }
    private void ExecuteSequentialRename(SelectionResult selection, string? firstItemInitialName)
    {
        var outcome = _renameApplyCoordinator.ApplySequentialRename(
            selection,
            firstItemInitialName,
            (path, value, skipInitialPrompt, showValidation) =>
                _renameDialogCoordinator.ShowSingleRenameDialog(this, path, value, skipInitialPrompt, showValidation),
            GetFriendlyRenameErrorMessage,
            message => MessageBox.Show(message, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error),
            BuildRenameUndoReadyMessage);
        ApplyRenameOutcome(outcome);
    }
    private static string GetFriendlyRenameErrorMessage(Exception ex)
    {
        if (ex is IOException ioEx)
        {
            const int sharingViolationHResult = unchecked((int)0x80070020);
            if (ioEx.HResult == sharingViolationHResult ||
                ioEx.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase))
            {
                return "別のプロセスがこのファイルを使用中のため、リネームできません。";
            }
        }
        return ex.Message;
    }
    private async void ExecuteBatchRename(SelectionResult selection)
    {
        if (GuardMutationBusy()) return;
        string initialTemplate = "$F$E";
        if (_settings.Rename.RememberLastTemplate && !string.IsNullOrWhiteSpace(_settings.Rename.LastTemplate))
        {
            initialTemplate = _settings.Rename.LastTemplate;
        }
        var dialogResult = _renameDialogCoordinator.ShowBatchDialog(
            this,
            selection.FullPaths,
            initialTemplate,
            _settings.Rename.RememberLastTemplate);
        if (!dialogResult.Confirmed)
        {
            ShowStatusMessage("リネームはキャンセルされました。");
            return;
        }
        if (GuardMutationBusy()) return;
        if (dialogResult.RememberTemplate)
        {
            _settings.Rename.RememberLastTemplate = true;
            _settings.Rename.LastTemplate = dialogResult.LastTemplateCandidate;
        }
        else
        {
            _settings.Rename.RememberLastTemplate = false;
        }
        SettingsManager.Save(_settings);
        var token = PrepareFileOperation("一括リネーム");
        int renameTotal = dialogResult.Preview.Items.Count(item => item.WillRename);
        var progressForm = Presentation.FileOperationFallbackUiPresenter.ShowReadyProgressFallback(
            this,
            "一括リネーム",
            renameTotal,
            requestCancel: null,
            canCancel: false,
            indeterminate: false);
        try
        {
            var outcome = await Task.Run(() => _renameApplyCoordinator.ApplyBatchRename(
                selection,
                dialogResult.Preview,
                _navigationService.CurrentPath,
                message =>
                {
                    if (!IsDisposed && IsHandleCreated)
                    {
                        Invoke(new Action(() => MessageBox.Show(this, message, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error)));
                    }
                },
                BuildRenameUndoReadyMessage,
                (processed, total, currentName) =>
                {
                    if (!IsDisposed && IsHandleCreated)
                    {
                        BeginInvoke(new Action(() => progressForm.UpdateProgress(processed, total, currentName, cancelRequested: false)));
                    }
                }));
            if (outcome.StatusMessage == "問題のある行があるためリネームを実行できません。")
            {
                MessageBox.Show(this, outcome.StatusMessage, "Rename", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            ApplyRenameOutcome(outcome);
        }
        catch (Exception ex)
        {
            LogService.Error("ExecuteBatchRename async error", ex);
            MessageBox.Show(this, $"予期せぬエラーが発生しました。\n{ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            progressForm.Complete("一括リネーム完了");
            FinalizeFileOperation();
        }
    }
    private void ApplyRenameOutcome(RenameApplyCoordinator.RenameApplyOutcome outcome)
    {
        if (outcome.SuccessfulItems.Count > 0)
        {
            RecordRenameUndoBatch(outcome.SuccessfulItems);
        }
        if (outcome.PostOperationResult != null)
        {
            FileOperationResult renameResult = outcome.PostOperationResult;
            string statusMessage = FileOperationPresentationHelper.GetRenameResultStatusMessage(renameResult);
            HandlePostOperation(new FileOperationResult(
                renameResult.OperationName,
                renameResult.ExitStatus,
                renameResult.SuccessCount,
                renameResult.TotalCount,
                renameResult.NextFocusTarget,
                renameResult.DestinationDir,
                renameResult.ShouldClearPreview,
                renameResult.ShouldClearMarks,
                statusMessage,
                renameResult.SkipCount,
                renameResult.FailCount));
            return;
        }
        if (!string.IsNullOrWhiteSpace(outcome.StatusMessage))
        {
            ShowStatusMessage(outcome.StatusMessage);
        }
    }
    private async void ExecuteFileOperationUndo()
    {
        var stopwatch = Stopwatch.StartNew();
        LogService.Info($"[UndoRuntime] Undo requested. thread={Environment.CurrentManagedThreadId}");
        if (_isFileOperationUndoRedoBusy)
        {
            LogService.Warn($"[UndoRuntime] Undo ignored because another undo/redo is running. elapsed={stopwatch.ElapsedMilliseconds}ms");
            ShowStatusMessage("Undo/Redo 処理中です。");
            return;
        }
        if (!_fileOperationUndoRedoService.TryPeekUndo(out FileOperationUndoRedoBatch batch))
        {
            LogService.Warn($"[UndoRuntime] No undo batch. elapsed={stopwatch.ElapsedMilliseconds}ms");
            ShowStatusMessage("元に戻せるファイル操作がありません");
            return;
        }
        LogService.Info($"[UndoRuntime] Undo batch peeked. operation={batch.Operation}, items={batch.Items.Count}");
        bool showProgress = IsTrashDeleteUndoRedoOperation(batch.Operation);
        _isFileOperationUndoRedoBusy = true;
        UpdateMenuStripState();
        if (showProgress)
        {
            ShowFileOperationUndoRedoProgressFallback("元に戻す", batch.Items.Count);
        }
        try
        {
            var applyResult = await Task.Run(() =>
            {
                bool success = TryApplyFileOperationUndoRedoBatch(
                    batch,
                    undo: true,
                    out string? focusTargetName,
                    out string? errorMessage,
                    showProgress ? UpdateFileOperationUndoRedoProgressFallbackFromWorker : null);
                return new FileOperationUndoRedoApplyResult(success, focusTargetName, errorMessage);
            });
            if (!applyResult.Success)
            {
                if (showProgress)
                {
                    CompleteFileOperationUndoRedoProgressFallback("元に戻せませんでした。");
                }
                stopwatch.Stop();
                LogService.Warn(
                    $"[UndoRuntime] Undo apply failed. operation={batch.Operation}, items={batch.Items.Count}, " +
                    $"elapsed={stopwatch.ElapsedMilliseconds}ms, error={applyResult.ErrorMessage ?? "<none>"}");
                ShowStatusMessage(applyResult.ErrorMessage ?? "ファイル操作を元に戻せませんでした。");
                return;
            }
            _fileOperationUndoRedoService.CommitUndo();
            LogService.Info($"[RedoRuntime] Redo batch recorded by CommitUndo. operation={batch.Operation}, items={batch.Items.Count}");
            LoadDirectory(_navigationService.CurrentPath, applyResult.FocusTargetName);
            stopwatch.Stop();
            LogService.Info(
                $"[UndoRuntime] Undo completed. operation={batch.Operation}, items={batch.Items.Count}, " +
                $"focusTarget={applyResult.FocusTargetName ?? "<none>"}, elapsed={stopwatch.ElapsedMilliseconds}ms");
            string opLabel = GetFileOperationUndoRedoOperationLabel(batch.Operation);
            if (batch.IsPartialCancellation) opLabel += " (途中キャンセル分)";
            ShowStatusMessage($"{batch.Items.Count} 件の{opLabel}を元に戻しました");
            ScheduleBrowserFocusReturnAfterFileOperation("UndoCompleted");
            if (showProgress)
            {
                CompleteFileOperationUndoRedoProgressFallback("元に戻しました");
            }
        }
        catch (Exception ex)
        {
            if (showProgress)
            {
                CompleteFileOperationUndoRedoProgressFallback("元に戻せませんでした。");
            }
            stopwatch.Stop();
            LogService.Error(
                $"[UndoRuntime] Undo failed unexpectedly. operation={batch.Operation}, items={batch.Items.Count}, " +
                $"elapsed={stopwatch.ElapsedMilliseconds}ms",
                ex);
            ShowStatusMessage("ファイル操作を元に戻せませんでした。");
        }
        finally
        {
            _isFileOperationUndoRedoBusy = false;
            UpdateMenuStripState();
            TryProcessPendingCurrentDirectoryRefresh("UndoFinally");
        }
    }
    private async void ExecuteFileOperationRedo()
    {
        var stopwatch = Stopwatch.StartNew();
        LogService.Info($"[RedoRuntime] Redo requested. thread={Environment.CurrentManagedThreadId}");
        if (_isFileOperationUndoRedoBusy)
        {
            LogService.Warn($"[RedoRuntime] Redo ignored because another undo/redo is running. elapsed={stopwatch.ElapsedMilliseconds}ms");
            ShowStatusMessage("Undo/Redo 処理中です。");
            return;
        }
        if (!_fileOperationUndoRedoService.TryPeekRedo(out FileOperationUndoRedoBatch batch))
        {
            LogService.Warn($"[RedoRuntime] No redo batch. elapsed={stopwatch.ElapsedMilliseconds}ms");
            ShowStatusMessage("やり直せるファイル操作がありません");
            return;
        }
        LogService.Info($"[RedoRuntime] Redo batch peeked. operation={batch.Operation}, items={batch.Items.Count}");
        bool showProgress = IsTrashDeleteUndoRedoOperation(batch.Operation);
        string? precomputedFocusTargetName = IsTrashDeleteUndoRedoOperation(batch.Operation)
            ? GetNextFocusTarget(batch.Items.Select(item => item.BeforePath).ToList())
            : null;
        _isFileOperationUndoRedoBusy = true;
        UpdateMenuStripState();
        if (showProgress)
        {
            ShowFileOperationUndoRedoProgressFallback("やり直し", batch.Items.Count);
        }
        try
        {
            var applyResult = await Task.Run(() =>
            {
                bool success = TryApplyFileOperationUndoRedoBatch(
                    batch,
                    undo: false,
                    out string? focusTargetName,
                    out string? errorMessage,
                    showProgress ? UpdateFileOperationUndoRedoProgressFallbackFromWorker : null,
                    precomputedFocusTargetName);
                return new FileOperationUndoRedoApplyResult(success, focusTargetName, errorMessage);
            });
            if (!applyResult.Success)
            {
                if (showProgress)
                {
                    CompleteFileOperationUndoRedoProgressFallback("やり直せませんでした。");
                }
                stopwatch.Stop();
                LogService.Warn(
                    $"[RedoRuntime] Redo apply failed. operation={batch.Operation}, items={batch.Items.Count}, " +
                    $"elapsed={stopwatch.ElapsedMilliseconds}ms, error={applyResult.ErrorMessage ?? "<none>"}");
                ShowStatusMessage(applyResult.ErrorMessage ?? "ファイル操作をやり直せませんでした。");
                return;
            }
            _fileOperationUndoRedoService.CommitRedo();
            LogService.Info($"[UndoRuntime] Undo batch restored by CommitRedo. operation={batch.Operation}, items={batch.Items.Count}");
            LoadDirectory(_navigationService.CurrentPath, applyResult.FocusTargetName);
            stopwatch.Stop();
            LogService.Info(
                $"[RedoRuntime] Redo completed. operation={batch.Operation}, items={batch.Items.Count}, " +
                $"focusTarget={applyResult.FocusTargetName ?? "<none>"}, elapsed={stopwatch.ElapsedMilliseconds}ms");
            string opLabel = GetFileOperationUndoRedoOperationLabel(batch.Operation);
            if (batch.IsPartialCancellation) opLabel += " (途中キャンセル分)";
            ShowStatusMessage($"{batch.Items.Count} 件の{opLabel}をやり直しました");
            ScheduleBrowserFocusReturnAfterFileOperation("RedoCompleted");
            if (showProgress)
            {
                CompleteFileOperationUndoRedoProgressFallback("やり直しました");
            }
        }
        catch (Exception ex)
        {
            if (showProgress)
            {
                CompleteFileOperationUndoRedoProgressFallback("やり直せませんでした。");
            }
            stopwatch.Stop();
            LogService.Error(
                $"[RedoRuntime] Redo failed unexpectedly. operation={batch.Operation}, items={batch.Items.Count}, " +
                $"elapsed={stopwatch.ElapsedMilliseconds}ms",
                ex);
            ShowStatusMessage("ファイル操作をやり直せませんでした。");
        }
        finally
        {
            _isFileOperationUndoRedoBusy = false;
            UpdateMenuStripState();
            TryProcessPendingCurrentDirectoryRefresh("RedoFinally");
        }
    }
    private readonly record struct FileOperationUndoRedoApplyResult(
        bool Success,
        string? FocusTargetName,
        string? ErrorMessage);
    private bool TryApplyFileOperationUndoRedoBatch(
        FileOperationUndoRedoBatch batch,
        bool undo,
        out string? focusTargetName,
        out string? errorMessage,
        Action<int, int, string>? progress = null,
        string? precomputedFocusTargetName = null)
    {
        focusTargetName = null;
        errorMessage = null;
        if (batch.Items.Count == 0)
        {
            errorMessage = "Undo/Redo 履歴が空です。";
            return false;
        }
        if (batch.Operation == FileOperationUndoRedoOperation.CreateFromPaste)
        {
            if (!undo)
            {
                errorMessage = "この貼り付けUndoはやり直しに対応していません。";
                return false;
            }

            foreach (FileOperationUndoRedoItem item in batch.Items)
            {
                if (!File.Exists(item.BeforePath))
                {
                    errorMessage = $"対象が見つからないため続行できません: {item.BeforePath}";
                    return false;
                }

                var info = new FileInfo(item.BeforePath);
                if (info.Length != item.CreatedFileLength || info.LastWriteTimeUtc.Ticks != item.CreatedFileLastWriteTimeUtcTicks)
                {
                    errorMessage = $"作成後に変更されたため続行できません: {item.BeforePath}";
                    return false;
                }
            }

            try
            {
                foreach (FileOperationUndoRedoItem item in batch.Items)
                {
                    FileOperationService.Delete(item.BeforePath);
                }
            }
            catch (Exception ex)
            {
                _fileOperationUndoRedoService.Reset();
                errorMessage = $"{ex.Message} (履歴は安全側で破棄しました)";
                return false;
            }

            focusTargetName = null;
            return true;
        }
        if (IsTrashDeleteUndoRedoOperation(batch.Operation))
        {
            return TryApplyTrashDeleteUndoRedoBatch(
                batch,
                undo,
                out focusTargetName,
                out errorMessage,
                progress,
                precomputedFocusTargetName);
        }
        var operations = batch.Items
            .Select(item => undo
                ? new { CurrentPath = item.AfterPath, TargetPath = item.BeforePath, TargetName = item.BeforeName }
                : new { CurrentPath = item.BeforePath, TargetPath = item.AfterPath, TargetName = item.AfterName })
            .ToList();
        foreach (var operation in operations)
        {
            if (!PathExists(operation.CurrentPath))
            {
                errorMessage = $"対象が見つからないため続行できません: {operation.CurrentPath}";
                return false;
            }
            if (PathExists(operation.TargetPath))
            {
                errorMessage = $"同名の項目があるため続行できません: {operation.TargetPath}";
                return false;
            }
        }
        try
        {
            foreach (var operation in Enumerable.Reverse(operations))
            {
                if (batch.Operation == FileOperationUndoRedoOperation.Rename)
                {
                    FileOperationService.Rename(operation.CurrentPath, operation.TargetPath);
                    continue;
                }
                FileOperationService.Move(operation.CurrentPath, operation.TargetPath, overwrite: false);
            }
        }
        catch (Exception ex)
        {
            _fileOperationUndoRedoService.Reset();
            errorMessage = $"{ex.Message} (履歴は安全側で破棄しました)";
            return false;
        }
        focusTargetName = operations
            .Select(operation => operation.TargetPath)
            .FirstOrDefault(path =>
                string.Equals(
                    NavigationService.NormalizeDirectoryForCompare(Path.GetDirectoryName(path) ?? string.Empty),
                    NavigationService.NormalizeDirectoryForCompare(_navigationService.CurrentPath),
                    StringComparison.OrdinalIgnoreCase))
            is string focusPath
                ? Path.GetFileName(focusPath)
                : null;
        return true;
    }
    private bool TryApplyTrashDeleteUndoRedoBatch(
        FileOperationUndoRedoBatch batch,
        bool undo,
        out string? focusTargetName,
        out string? errorMessage,
        Action<int, int, string>? progress = null,
        string? precomputedFocusTargetName = null)
    {
        focusTargetName = null;
        errorMessage = null;
        try
        {
            var batchStopwatch = Stopwatch.StartNew();
            LogService.Info(
                $"[UndoRuntime] Recycle-bin batch apply start. mode={(undo ? "UndoRestore" : "RedoDelete")}, " +
                $"items={batch.Items.Count}, thread={Environment.CurrentManagedThreadId}");
            if (undo)
            {
                if (batch.Operation == FileOperationUndoRedoOperation.DeleteToMidFdTrash)
                {
                    LogService.Info($"[FileOperationUndo] Restoring MidFD managed trash batch: {batch.Items.Count} items");
                    MidFdManagedTrashService.ResetManifestOperationDiagnostics();
                    MidFdManagedTrashService.BeginManifestBatch();
                    var uiUpdateSw = new Stopwatch();
                    int managedIndex = 0;
                    long maxItemMs = 0;
                    if (batch.Items.Count > 10)
                    {
                        MidFdManagedTrashService.SetLoggingSuppression(true);
                    }
                    try
                    {
                        var trashPathsToUpdate = new List<string>();
                        foreach (FileOperationUndoRedoItem item in batch.Items)
                        {
                            var itemSw = Stopwatch.StartNew();
                            managedIndex++;
                            uiUpdateSw.Start();
                            progress?.Invoke(managedIndex - 1, batch.Items.Count, Path.GetFileName(item.BeforePath));
                            uiUpdateSw.Stop();
                            bool suppressLogging = batch.Items.Count > 10;
                            MidFdManagedTrashService.RestoreFromTrash(item, skipStatusUpdate: true, suppressLogging: suppressLogging);
                            trashPathsToUpdate.Add(item.RecycleBinPath!);
                            uiUpdateSw.Start();
                            progress?.Invoke(managedIndex, batch.Items.Count, Path.GetFileName(item.BeforePath));
                            uiUpdateSw.Stop();
                            itemSw.Stop();
                            if (itemSw.ElapsedMilliseconds > maxItemMs) maxItemMs = itemSw.ElapsedMilliseconds;
                        }
                        if (trashPathsToUpdate.Count > 0)
                        {
                            MidFdManagedTrashService.UpdateRecordStatuses(trashPathsToUpdate, TrashRecordStatus.Restored);
                        }
                    }
                    finally
                    {
                        int suppressedCount = MidFdManagedTrashService.GetSuppressedSuccessCount();
                        if (suppressedCount > 0 || batch.Items.Count > 10)
                        {
                            LogService.Info($"[MidFdTrashLogThrottle] Summary operation={(undo ? "UndoRestore" : "RedoDelete")} items={batch.Items.Count} suppressed={suppressedCount} [MidFdTrashLogThrottle] RuntimeGapCorrective active");
                        }
                        MidFdManagedTrashService.FlushManifestBatch();
                        MidFdManagedTrashService.SetLoggingSuppression(false);
                    }
                    focusTargetName = batch.Items
                        .Select(item => item.BeforePath)
                        .FirstOrDefault(path =>
                            string.Equals(
                                NavigationService.NormalizeDirectoryForCompare(Path.GetDirectoryName(path) ?? string.Empty),
                                NavigationService.NormalizeDirectoryForCompare(_navigationService.CurrentPath),
                                StringComparison.OrdinalIgnoreCase))
                        is string restoredPath
                            ? Path.GetFileName(restoredPath)
                            : null;
                    batchStopwatch.Stop();
                    var metrics = MidFdManagedTrashService.GetUndoRedoMetrics();
                    LogService.Info(
                        $"[UndoRedoPerf] Undo completed. operation={batch.Operation}, items={batch.Items.Count}, " +
                        $"totalMs={batchStopwatch.ElapsedMilliseconds}, lookupMs={metrics.lookup}, fileMoveMs={metrics.fileMove}, " +
                        $"statusUpdateMs={metrics.statusUpdate}, manifestStoreMs={metrics.manifestStore}, uiUpdateMs={uiUpdateSw.ElapsedMilliseconds}, " +
                        $"perItemAvgMs={(double)batchStopwatch.ElapsedMilliseconds / Math.Max(1, batch.Items.Count):F2}, maxItemMs={maxItemMs}");
                    return true;
                }
                errorMessage = "未対応の削除Undo操作です。";
                return false;
            }
            var refreshedItems = new List<FileOperationUndoRedoItem>();
            if (batch.Operation == FileOperationUndoRedoOperation.DeleteToMidFdTrash)
            {
                LogService.Info($"[FileOperationRedo] Re-deleting MidFD managed trash batch: {batch.Items.Count} items");
                MidFdManagedTrashService.ResetManifestOperationDiagnostics();
                MidFdManagedTrashService.BeginManifestBatch();
                var uiUpdateSw = new Stopwatch();
                int managedRedoIndex = 0;
                long maxItemMs = 0;
                var recordsToRegister = new List<TrashManifestRecord>();
                if (batch.Items.Count > 10)
                {
                    MidFdManagedTrashService.SetLoggingSuppression(true);
                }
                try
                {
                    foreach (FileOperationUndoRedoItem item in batch.Items)
                    {
                        var itemSw = Stopwatch.StartNew();
                        managedRedoIndex++;
                        if (!PathExists(item.BeforePath))
                        {
                            errorMessage = $"対象が見つからないため続行できません: {item.BeforePath}";
                            return false;
                        }
                        uiUpdateSw.Start();
                        progress?.Invoke(managedRedoIndex - 1, batch.Items.Count, Path.GetFileName(item.BeforePath));
                        uiUpdateSw.Stop();
                        bool suppressLogging = batch.Items.Count > 10;
                        refreshedItems.Add(MidFdManagedTrashService.RedoDeleteToTrash(item, out TrashManifestRecord? record, skipRegistration: true, suppressLogging: suppressLogging));
                        if (record != null) recordsToRegister.Add(record);
                        if (recordsToRegister.Count >= 1000)
                        {
                            MidFdManagedTrashService.RegisterNewTrashRecordsPublic(recordsToRegister);
                            recordsToRegister.Clear();
                        }
                        uiUpdateSw.Start();
                        progress?.Invoke(managedRedoIndex, batch.Items.Count, Path.GetFileName(item.BeforePath));
                        uiUpdateSw.Stop();
                        itemSw.Stop();
                        if (itemSw.ElapsedMilliseconds > maxItemMs) maxItemMs = itemSw.ElapsedMilliseconds;
                    }
                }
                finally
                {
                    if (recordsToRegister.Count > 0)
                    {
                        MidFdManagedTrashService.RegisterNewTrashRecordsPublic(recordsToRegister);
                        recordsToRegister.Clear();
                    }
                    int suppressedCount = MidFdManagedTrashService.GetSuppressedSuccessCount();
                    if (suppressedCount > 0 || batch.Items.Count > 10)
                    {
                        LogService.Info($"[MidFdTrashLogThrottle] Summary operation=RedoDelete items={batch.Items.Count} suppressed={suppressedCount} [MidFdTrashLogThrottle] RuntimeGapCorrective active");
                    }
                    MidFdManagedTrashService.FlushManifestBatch();
                    MidFdManagedTrashService.SetLoggingSuppression(false);
                }
                batch.Items = FileOperationUndoRedoService.CreateDeleteToTrashBatch(refreshedItems);
                focusTargetName = precomputedFocusTargetName;
                batchStopwatch.Stop();
                var metrics = MidFdManagedTrashService.GetUndoRedoMetrics();
                LogService.Info(
                    $"[UndoRedoPerf] Redo completed. operation={batch.Operation}, items={batch.Items.Count}, " +
                    $"totalMs={batchStopwatch.ElapsedMilliseconds}, lookupMs={metrics.lookup}, fileMoveMs={metrics.fileMove}, " +
                    $"statusUpdateMs={metrics.statusUpdate}, manifestStoreMs={metrics.manifestStore}, uiUpdateMs={uiUpdateSw.ElapsedMilliseconds}, " +
                    $"perItemAvgMs={(double)batchStopwatch.ElapsedMilliseconds / Math.Max(1, batch.Items.Count):F2}, maxItemMs={maxItemMs}");
                return true;
            }
            errorMessage = "未対応の削除Redo操作です。";
            return false;
        }
        catch (Exception ex)
        {
            _fileOperationUndoRedoService.Reset();
            LogService.Error("[UndoRuntime] Recycle-bin batch failed and history was reset.", ex);
            errorMessage = $"{ex.Message} (履歴は安全側で破棄しました)";
            return false;
        }
    }
    private static bool PathExists(string path)
    {
        return ReparsePointHelper.Exists(path);
    }
    private static List<string> CreatePersistableMarkedPaths(IEnumerable<string>? paths, out int skippedCount)
    {
        skippedCount = 0;
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? path in paths ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(path) || !PathExists(path))
            {
                skippedCount++;
                continue;
            }
            if (seen.Add(path))
            {
                result.Add(path);
            }
        }
        return result;
    }
    private void RecordRenameUndoBatch(IEnumerable<RenamePreviewItem> items)
    {
        _fileOperationUndoRedoService.RecordBatch(
            FileOperationUndoRedoOperation.Rename,
            FileOperationUndoRedoService.CreateRenameBatch(items));
    }
    private static string BuildRenameUndoReadyMessage(int successCount, int totalCount)
    {
        return FileOperationPresentationHelper.GetUndoReadyMessage("リネーム", successCount, totalCount);
    }
    private static string BuildMoveUndoReadyMessage(int successCount, int totalCount)
    {
        return FileOperationPresentationHelper.GetUndoReadyMessage("移動", successCount, totalCount);
    }
    private static string GetFileOperationUndoRedoOperationLabel(FileOperationUndoRedoOperation operation)
    {
        return operation switch
        {
            FileOperationUndoRedoOperation.Rename => "リネーム",
            FileOperationUndoRedoOperation.Move => "移動",
            FileOperationUndoRedoOperation.DeleteToMidFdTrash => "削除",
            FileOperationUndoRedoOperation.CreateFromPaste => "貼り付け作成",
            _ => "ファイル操作"
        };
    }
    private static bool IsTrashDeleteUndoRedoOperation(FileOperationUndoRedoOperation operation)
    {
        return operation == FileOperationUndoRedoOperation.DeleteToMidFdTrash;
    }
    private async Task ExecuteDelete(bool permanent = false, SelectionResult? selectionSnapshot = null)
    {
        if (GuardMutationBusy()) return;
        if (GuardReadOnlyBrowserTab())
        {
            return;
        }
        var entryPlan = _fileOperationEntryCoordinator.CreateSelectionEntryPlan(
            _isClipboardBusy,
            _fileOpUiState.ActiveOperationName,
            _fileOpUiState.Cts != null,
            "削除",
            ResolveSelection(selectionSnapshot),
            "削除対象がありません。");
        if (!entryPlan.CanProceed)
        {
            if (!string.IsNullOrEmpty(entryPlan.StatusMessage))
            {
                ShowStatusMessage(entryPlan.StatusMessage, 1000);
            }
            return;
        }
        var selectionSw = Stopwatch.StartNew();
        var selection = entryPlan.Selection;
        if (!TryResolveMultiMarkSelectionAction("削除", "削除をキャンセルしました。", selection, out selection))
        {
            return;
        }
        if (selection.FullPaths.Count > 1)
        {
            var filtered = Helpers.PathNormalizationHelper.FilterParentChildPaths(selection.FullPaths);
            selection = new SelectionResult(filtered, selection.HasMarkedSelection);
        }
        selectionSw.Stop();
        long selectionResolveMs = selectionSw.ElapsedMilliseconds;
        var warningSw = Stopwatch.StartNew();
        bool usePermanentDelete = permanent;
        bool useMidFdManagedTrash = !usePermanentDelete && (_settings.FileOperations?.UseMidFdManagedTrash ?? false);
        bool shouldConfirm = usePermanentDelete
            ? (_settings.FileOperations?.ConfirmPermanentDelete ?? true)
            : (_settings.FileOperations?.ConfirmDelete ?? true);
        warningSw.Stop();
        long outsideWarningMs = warningSw.ElapsedMilliseconds;
        var confirmSw = Stopwatch.StartNew();
        if (shouldConfirm && !_fileOperationDialogCoordinator.ConfirmDelete(this, selection, usePermanentDelete, _navigationService.CurrentPath, ShowStatusMessage))
        {
            return;
        }
        if (GuardMutationBusy()) return;
        confirmSw.Stop();
        long confirmDialogMs = confirmSw.ElapsedMilliseconds;
        var focusPrepSw = Stopwatch.StartNew();
        // 操作後に一気に一番上まで戻るのを防ぐため、あらかじめ次にフォーカスすべき対象を見つけておく
        string? nextTargetName = GetNextFocusTarget(selection.FullPaths.ToList());
        focusPrepSw.Stop();
        long focusTargetPrepareMs = focusPrepSw.ElapsedMilliseconds;
        int totalCount = selection.Count;
        int successCount = 0;
        int failCount = 0;
        FileOpExitStatus exitStatus = FileOpExitStatus.Success;
        var successPaths = new List<string>();
        var recycleBinDeleteUndoItems = new List<FileOperationUndoRedoItem>();
        bool canRecordRecycleBinUndo = useMidFdManagedTrash;
        bool recordedRecycleBinUndo = false;
        CancellationToken token = PrepareFileOperation(usePermanentDelete ? "完全削除" : "削除");
        int deleteStatusVersion = _fileOpUiState.StatusVersion;
        bool useShellGuardedRecycleBinDelete = !usePermanentDelete && !useMidFdManagedTrash && totalCount <= ShellGuardedRecycleBinDeleteMaxItems;
        ShowStatusMessage(FileOperationPresentationHelper.GetOperationStartingMessage("Delete", totalCount));
        StartFileOperationProgressIndicator("Delete", totalCount);
        // ShowShellDeleteProgressFallback is bypassed to avoid duplicate progress dialogs.
        // The common FileOperationProgressDialog (StartFileOperationProgressIndicator) is the canonical progress UI.
        LogService.Info($"[MidFdTrashIntegrity] ExecuteDelete started. (Build: 2026-04-26-Investigation-Correctness)");
        var deleteTotalStopwatch = Stopwatch.StartNew();
        DateTime recycleBinDeleteStartedUtc = DateTime.UtcNow;
        long deleteLoopTotalMs = 0;
        long undoRecordMs = 0;
        long postOperationMs = 0;
        long shellServiceMs = 0;
        long progressCompleteMs = 0;
        // LargeDeletePerf metrics
        long manifestOperationTotalMs = 0;
        long manifestFileMoveTotalMs = 0;
        long manifestUpsertTotalMs = 0;
        long manifestLogTotalMs = 0;
        long manifestSaveTotalMs = 0;
        long progressUiTotalMs = 0;
        long progressiveRemovalTotalMs = 0;
        long markRemovalTotalMs = 0;
        long headerMenuUpdateTotalMs = 0;
        int manifestUpsertCount = 0;
        int manifestSaveCount = 0;
        int manifestFlushCount = 0;
        int manifestLogSuppressedCount = 0;
        int manifestSuccessLogCount = 0;
        int manifestChunkSummaryCount = 0;
        int manifestSlowItemCount = 0;
        int manifestAppendCount = 0;
        long manifestUpsertScanCount = 0;
        long manifestAppendMs = 0;
        int manifestRecordCountBefore = 0;
        int manifestRecordCountAfter = 0;
        bool manifestAppendMode = false;
        int headerUpdateCount = 0;
        int menuUpdateCount = 0;
        int progressUpdateCount = 0;
        int progressiveRemovalCount = 0;
        int markRemoveCallCount = 0;
        int invalidateCount = 0;
        long uiFlushMaxMs = 0;
        string midFdTrashBatchId = MidFdManagedTrashService.CreateBatchId();
        try
        {
            var swLoop = Stopwatch.StartNew();
            if (usePermanentDelete)
            {
                var result = await Task.Run(() =>
                {
                    int currentSuccess = 0;
                    int currentFailCount = 0;
                    FileOpExitStatus currentStatus = FileOpExitStatus.Success;
                    var chunkSw = Stopwatch.StartNew();
                    int chunkStartIndex = 0;
                    long chunkMaxPerItemMs = 0;
                    var pendingUiPaths = new List<string>();
                    var uiThrottleSw = Stopwatch.StartNew();
                    const int UI_CHUNK_SIZE = 250;
                    const int UI_THROTTLE_MS = 250;
                    bool largeDelete = totalCount >= 100;
                    foreach (string path in selection.FullPaths)
                    {
                        if (token.IsCancellationRequested)
                        {
                            currentStatus = FileOpExitStatus.Canceled;
                            break;
                        }
                        var itemSw = Stopwatch.StartNew();
                        string fileName = Path.GetFileName(path);
                        bool shouldUpdateProgress = (currentSuccess + currentFailCount) % 100 == 0 || pendingUiPaths.Count >= UI_CHUNK_SIZE || uiThrottleSw.ElapsedMilliseconds >= UI_THROTTLE_MS;
                        if (shouldUpdateProgress)
                        {
                            var uiSw = Stopwatch.StartNew();
                            Invoke(new Action(() => ShowFileOperationProgressIfCurrent(
                                deleteStatusVersion,
                                "完全削除",
                                currentSuccess + currentFailCount + 1,
                                totalCount,
                                fileName)));
                            uiSw.Stop();
                            progressUiTotalMs += uiSw.ElapsedMilliseconds;
                            progressUpdateCount++;
                        }
                        try
                        {
                            FileOperationService.Delete(path);
                            currentSuccess++;
                            pendingUiPaths.Add(path);
                            string flushReason = "";
                            if (pendingUiPaths.Count >= UI_CHUNK_SIZE) flushReason = "CountThreshold";
                            else if (uiThrottleSw.ElapsedMilliseconds >= UI_THROTTLE_MS) flushReason = "TimeThreshold";
                            if (!string.IsNullOrEmpty(flushReason))
                            {
                                var removalSw = Stopwatch.StartNew();
                                var flushPaths = pendingUiPaths.ToList();
                                pendingUiPaths.Clear();
                                Invoke(new Action(() => ApplyProgressiveDeleteUiChunk(
                                    flushPaths,
                                    deleteStatusVersion,
                                    ref markRemovalTotalMs,
                                    ref markRemoveCallCount,
                                    ref headerMenuUpdateTotalMs,
                                    ref headerUpdateCount,
                                    ref menuUpdateCount,
                                    ref invalidateCount,
                                    midFdTrashBatchId,
                                    flushReason)));
                                uiThrottleSw.Restart(); // restart AFTER invoke to avoid degenerate 1-item flushes
                                removalSw.Stop();
                                progressiveRemovalTotalMs += removalSw.ElapsedMilliseconds;
                                if (removalSw.ElapsedMilliseconds > uiFlushMaxMs) uiFlushMaxMs = removalSw.ElapsedMilliseconds;
                                progressiveRemovalCount++;
                            }
                        }
                        catch (Exception ex)
                        {
                            Invoke(new Action(() =>
                                MessageBox.Show($"完全削除失敗: {path}\n{ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error)));
                            currentFailCount++;
                            currentStatus = FileOpExitStatus.Error;
                            break;
                        }
                        itemSw.Stop();
                        long itemMs = itemSw.ElapsedMilliseconds;
                        if (itemMs > chunkMaxPerItemMs) chunkMaxPerItemMs = itemMs;
                        if (itemMs > 1000)
                        {
                            LogService.Info($"[LargeDeletePerf] SlowItem operationId={midFdTrashBatchId} index={currentSuccess + currentFailCount} elapsedMs={itemMs} stage=PermanentDelete path={path}");
                        }
                        if ((currentSuccess + currentFailCount) % 100 == 0)
                        {
                            LogService.Info($"[LargeDeletePerf] DeleteChunk operationId={midFdTrashBatchId} start={chunkStartIndex} count=100 elapsedMs={chunkSw.ElapsedMilliseconds} avgPerItemMs={chunkSw.ElapsedMilliseconds / 100.0:F1} maxPerItemMs={chunkMaxPerItemMs}");
                            chunkSw.Restart();
                            chunkStartIndex = currentSuccess + currentFailCount;
                            chunkMaxPerItemMs = 0;
                        }
                    }
                    // Final flush
                    if (pendingUiPaths.Count > 0)
                    {
                        var removalSw = Stopwatch.StartNew();
                        var flushPaths = pendingUiPaths.ToList();
                        pendingUiPaths.Clear();
                        Invoke(new Action(() => ApplyProgressiveDeleteUiChunk(
                            flushPaths,
                            deleteStatusVersion,
                            ref markRemovalTotalMs,
                            ref markRemoveCallCount,
                            ref headerMenuUpdateTotalMs,
                            ref headerUpdateCount,
                            ref menuUpdateCount,
                            ref invalidateCount,
                            midFdTrashBatchId,
                            currentStatus == FileOpExitStatus.Canceled ? "CancelFinalFlush" : "FinalFlush")));
                        removalSw.Stop();
                        progressiveRemovalTotalMs += removalSw.ElapsedMilliseconds;
                        if (removalSw.ElapsedMilliseconds > uiFlushMaxMs) uiFlushMaxMs = removalSw.ElapsedMilliseconds;
                        progressiveRemovalCount++;
                    }
                    return (currentSuccess, currentFailCount, currentStatus);
                }, token);
                swLoop.Stop();
                deleteLoopTotalMs = swLoop.ElapsedMilliseconds;
                LogService.Info($"[Perf] ExecuteDelete permanent async loop: {deleteLoopTotalMs}ms for {selection.Count} items");
                successCount = result.currentSuccess;
                failCount = result.currentFailCount;
                exitStatus = result.currentStatus;
            }
            else
            {
                if (useShellGuardedRecycleBinDelete)
                {
                    var shellServiceStopwatch = Stopwatch.StartNew();
                    var shellResult = await ShellRecycleBinDeleteService.DeleteToRecycleBinAsync(
                        selection.FullPaths.ToList(),
                        IsHandleCreated ? Handle : IntPtr.Zero,
                        token,
                        progress =>
                        {
                            if (IsDisposed || !IsHandleCreated)
                            {
                                return;
                            }
                            BeginInvoke(new Action(() =>
                            {
                                var uiSw = Stopwatch.StartNew();
                                ShowFileOperationProgressIfCurrent(
                                    deleteStatusVersion,
                                    "Delete",
                                    progress.ProcessedCount,
                                    progress.TotalCount,
                                    progress.Name);
                                UpdateShellDeleteProgressFallbackStateIfCurrent(
                                    deleteStatusVersion,
                                    _fileOpUiState.Cts?.IsCancellationRequested ?? false
                                        ? "キャンセル要求中..."
                                        : "Shell 削除実行中...",
                                    progress.IsSuccess
                                        ? $"Shell 完了通知: {progress.ProcessedCount}/{progress.TotalCount} 件"
                                        : "Shell からの完了通知を待っています",
                                    indeterminate: true);
                                uiSw.Stop();
                                progressUiTotalMs += uiSw.ElapsedMilliseconds;
                                progressUpdateCount++;
                                if (progress.IsSuccess)
                                {
                                    var removalSw = Stopwatch.StartNew();
                                    // Shell guarded delete is usually small (<= MaxItems), so we use ApplyProgressiveDeleteUi directly
                                    // but if user increased the limit, it might be heavy.
                                    // For now, ShellGuardedRecycleBinDeleteMaxItems is likely small.
                                    ApplyProgressiveDeleteUi(progress.Path, deleteStatusVersion, ref markRemovalTotalMs, ref markRemoveCallCount, ref headerMenuUpdateTotalMs, ref headerUpdateCount, ref menuUpdateCount, ref invalidateCount);
                                    removalSw.Stop();
                                    progressiveRemovalTotalMs += removalSw.ElapsedMilliseconds;
                                    if (removalSw.ElapsedMilliseconds > uiFlushMaxMs) uiFlushMaxMs = removalSw.ElapsedMilliseconds;
                                    progressiveRemovalCount++;
                                }
                            }));
                        });
                    shellServiceStopwatch.Stop();
                    shellServiceMs = shellServiceStopwatch.ElapsedMilliseconds;
                    swLoop.Stop();
                    deleteLoopTotalMs = swLoop.ElapsedMilliseconds;
                    LogService.Info(
                        $"[Perf] ExecuteDelete shell recycle-bin guarded loop: {deleteLoopTotalMs}ms " +
                        $"for {selection.Count} items, success={shellResult.SuccessCount}, " +
                        $"fail={shellResult.FailCount}, canceled={shellResult.IsCanceled}, hr=0x{shellResult.HResult:X8}, " +
                        $"serviceTotal={shellResult.TotalMs}ms, queueItems={shellResult.QueueItemsMs}ms, " +
                        $"perform={shellResult.PerformOperationsMs}ms, callbackSpan={shellResult.CallbackSpanMs}ms, " +
                        $"maxCallbackGap={shellResult.MaxCallbackGapMs}ms");
                    successCount = shellResult.SuccessCount;
                    failCount = shellResult.FailCount;
                    exitStatus = shellResult.IsCanceled
                        ? FileOpExitStatus.Canceled
                        : shellResult.HResult < 0
                            ? FileOpExitStatus.Error
                            : FileOpExitStatus.Success;
                    successPaths.AddRange(shellResult.SuccessPaths);
                }
                else if (useMidFdManagedTrash)
                {
                    bool largeDelete = totalCount > 10;
                    MidFdManagedTrashService.ResetManifestOperationDiagnostics();
                    // Always use batching for Managed Trash to ensure unified SQLite batch path even for small deletions
                    MidFdManagedTrashService.BeginManifestBatch();
                    if (largeDelete)
                    {
                        MidFdManagedTrashService.SetLoggingSuppression(true);
                    }
                    try
                    {
                        var managedTrashResult = await Task.Run(() =>
                    {
                        int currentSuccess = 0;
                        int currentFailCount = 0;
                        FileOpExitStatus currentStatus = FileOpExitStatus.Success;
                        var currentUndoItems = new List<FileOperationUndoRedoItem>();
                        var pendingRecords = new List<TrashManifestRecord>();
                        try
                        {
                            var chunkSw = Stopwatch.StartNew();
                            int chunkStartIndex = 0;
                            long chunkMaxPerItemMs = 0;
                            var pendingUiPaths = new List<string>();
                            var uiThrottleSw = Stopwatch.StartNew();
                            const int UI_CHUNK_SIZE = 250;
                            const int UI_THROTTLE_MS = 250;
                            foreach (string path in selection.FullPaths)
                            {
                                if (token.IsCancellationRequested)
                                {
                                    currentStatus = FileOpExitStatus.Canceled;
                                    break;
                                }
                                var itemSw = Stopwatch.StartNew();
                                string fileName = Path.GetFileName(path);
                                int nextIndex = currentSuccess + currentFailCount + 1;
                                bool shouldUpdateProgress = (currentSuccess + currentFailCount) % 100 == 0 || pendingUiPaths.Count >= UI_CHUNK_SIZE || uiThrottleSw.ElapsedMilliseconds >= UI_THROTTLE_MS;
                                if (shouldUpdateProgress)
                                {
                                    var uiSw = Stopwatch.StartNew();
                                    Invoke(new Action(() =>
                                    {
                                        ShowFileOperationProgressIfCurrent(
                                            deleteStatusVersion,
                                            "Delete",
                                            nextIndex,
                                            totalCount,
                                            fileName);
                                        UpdateShellDeleteProgressFallbackIfCurrent(
                                            deleteStatusVersion,
                                            currentSuccess + currentFailCount,
                                            totalCount,
                                            fileName);
                                    }));
                                    uiSw.Stop();
                                    progressUiTotalMs += uiSw.ElapsedMilliseconds;
                                    progressUpdateCount++;
                                }
                                try
                                {
                                    var trashSw = Stopwatch.StartNew();
                                    FileOperationUndoRedoItem undoItem = MidFdManagedTrashService.MoveToTrash(
                                        path,
                                        midFdTrashBatchId,
                                        nextIndex,
                                        true, // Always skip individual registration, we use batch registration below
                                        out TrashManifestRecord? record,
                                        out long fMoveMs,
                                        out long rUpsertMs,
                                        out long lMs,
                                        suppressLogging: largeDelete);
                                    if (record != null) pendingRecords.Add(record);
                                    trashSw.Stop();
                                    long totalOpMs = trashSw.ElapsedMilliseconds;
                                    manifestOperationTotalMs += totalOpMs;
                                    manifestFileMoveTotalMs += fMoveMs;
                                    manifestUpsertTotalMs += rUpsertMs;
                                    manifestLogTotalMs += lMs;
                                    manifestUpsertCount++;
                                    if (MidFdManagedTrashService.IsLoggingSuppressed()) manifestLogSuppressedCount++;
                                    else manifestSuccessLogCount++;
                                    if (totalOpMs > 1000) manifestSlowItemCount++;
                                    currentUndoItems.Add(undoItem);
                                    currentSuccess++;
                                    // Manifest chunk save (Unified for all deletion counts to ensure SQLite batch path)
                                    if (pendingRecords.Count >= 1000)
                                    {
                                        var mSw = Stopwatch.StartNew();
                                        MidFdManagedTrashService.RegisterNewTrashRecordsPublic(pendingRecords);
                                        pendingRecords.Clear();
                                        MidFdManagedTrashService.SaveActiveBatch();
                                        mSw.Stop();
                                        manifestSaveTotalMs += mSw.ElapsedMilliseconds;
                                        manifestSaveCount++;
                                        manifestFlushCount++;
                                        LogService.Info($"[LargeDeletePerf] ManifestFlush operationId={midFdTrashBatchId} reason=CountThreshold items={currentSuccess} elapsedMs={mSw.ElapsedMilliseconds} saveCount={manifestSaveCount}");
                                    }
                                    pendingUiPaths.Add(path);
                                    string flushReason = "";
                                    if (pendingUiPaths.Count >= UI_CHUNK_SIZE) flushReason = "CountThreshold";
                                    else if (uiThrottleSw.ElapsedMilliseconds >= UI_THROTTLE_MS) flushReason = "TimeThreshold";
                                    if (!string.IsNullOrEmpty(flushReason))
                                    {
                                        var removalSw = Stopwatch.StartNew();
                                        var flushPaths = pendingUiPaths.ToList();
                                        pendingUiPaths.Clear();
                                        Invoke(new Action(() =>
                                        {
                                            ApplyProgressiveDeleteUiChunk(
                                                flushPaths,
                                                deleteStatusVersion,
                                                ref markRemovalTotalMs,
                                                ref markRemoveCallCount,
                                                ref headerMenuUpdateTotalMs,
                                                ref headerUpdateCount,
                                                ref menuUpdateCount,
                                                ref invalidateCount,
                                                midFdTrashBatchId,
                                                flushReason);
                                            UpdateShellDeleteProgressFallbackIfCurrent(
                                                deleteStatusVersion,
                                                currentSuccess,
                                                totalCount,
                                                fileName);
                                        }));
                                        uiThrottleSw.Restart(); // restart AFTER invoke to avoid degenerate 1-item flushes
                                        removalSw.Stop();
                                        progressiveRemovalTotalMs += removalSw.ElapsedMilliseconds;
                                        if (removalSw.ElapsedMilliseconds > uiFlushMaxMs) uiFlushMaxMs = removalSw.ElapsedMilliseconds;
                                        progressiveRemovalCount++;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Invoke(new Action(() =>
                                        MessageBox.Show($"削除失敗: {path}\n{ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error)));
                                    currentFailCount++;
                                    currentStatus = FileOpExitStatus.Error;
                                    break;
                                }
                                itemSw.Stop();
                                long itemMs = itemSw.ElapsedMilliseconds;
                                if (itemMs > chunkMaxPerItemMs) chunkMaxPerItemMs = itemMs;
                                if (itemMs > 1000)
                                {
                                    LogService.Info($"[LargeDeletePerf] SlowItem operationId={midFdTrashBatchId} index={currentSuccess + currentFailCount} elapsedMs={itemMs} stage=ManagedTrashMove path={path}");
                                }
                                if ((currentSuccess + currentFailCount) % 100 == 0)
                                {
                                    LogService.Info($"[LargeDeletePerf] DeleteChunk operationId={midFdTrashBatchId} start={chunkStartIndex} count=100 elapsedMs={chunkSw.ElapsedMilliseconds} avgPerItemMs={chunkSw.ElapsedMilliseconds / 100.0:F1} maxPerItemMs={chunkMaxPerItemMs}");
                                    if (largeDelete)
                                    {
                                        LogService.Info($"[MidFdTrash] MoveChunkSummary operationId={midFdTrashBatchId} start={chunkStartIndex} count=100 elapsedMs={chunkSw.ElapsedMilliseconds} avgPerItemMs={chunkSw.ElapsedMilliseconds / 100.0:F1} moved=100 failed=0 manifestBatchMode=true");
                                        manifestChunkSummaryCount++;
                                    }
                                    chunkSw.Restart();
                                    chunkStartIndex = currentSuccess + currentFailCount;
                                    chunkMaxPerItemMs = 0;
                                }
                            }
                            // Final flush
                            if (pendingUiPaths.Count > 0)
                            {
                                var removalSw = Stopwatch.StartNew();
                                var flushPaths = pendingUiPaths.ToList();
                                pendingUiPaths.Clear();
                                Invoke(new Action(() => ApplyProgressiveDeleteUiChunk(
                                    flushPaths,
                                    deleteStatusVersion,
                                    ref markRemovalTotalMs,
                                    ref markRemoveCallCount,
                                    ref headerMenuUpdateTotalMs,
                                    ref headerUpdateCount,
                                    ref menuUpdateCount,
                                    ref invalidateCount,
                                    midFdTrashBatchId,
                                    currentStatus == FileOpExitStatus.Canceled ? "CancelFinalFlush" : "FinalFlush")));
                                removalSw.Stop();
                                progressiveRemovalTotalMs += removalSw.ElapsedMilliseconds;
                                if (removalSw.ElapsedMilliseconds > uiFlushMaxMs) uiFlushMaxMs = removalSw.ElapsedMilliseconds;
                                progressiveRemovalCount++;
                            }
                            return (currentSuccess, currentFailCount, currentStatus, currentUndoItems);
                        }
                        finally
                        {
                            if (pendingRecords.Count > 0)
                            {
                                MidFdManagedTrashService.RegisterNewTrashRecordsPublic(pendingRecords);
                                pendingRecords.Clear();
                            }
                        }
                    }, token);
                        successCount = managedTrashResult.currentSuccess;
                        failCount = managedTrashResult.currentFailCount;
                        exitStatus = managedTrashResult.currentStatus;
                        recycleBinDeleteUndoItems.AddRange(managedTrashResult.currentUndoItems);
                    }
                    finally
                    {
                        // Manifest flush moved to outer finally to allow RestoreNow to reuse the active batch
                    }
                    var manifestDiagnostics = MidFdManagedTrashService.GetManifestOperationDiagnostics();
                    manifestAppendCount = manifestDiagnostics.AppendCount;
                    manifestUpsertScanCount = manifestDiagnostics.UpsertScanCount;
                    manifestAppendMs = manifestDiagnostics.AppendMs;
                    manifestRecordCountBefore = manifestDiagnostics.RecordCountBefore;
                    manifestRecordCountAfter = manifestDiagnostics.RecordCountAfter;
                    manifestAppendMode = manifestDiagnostics.AppendMode;
                    LogService.Info(
                        $"[LargeDeletePerf] ManifestRecordSummary operationId={midFdTrashBatchId} " +
                        $"appendMode={manifestAppendMode} appendCount={manifestAppendCount} " +
                        $"upsertScanCount={manifestUpsertScanCount} appendMs={manifestAppendMs} " +
                        $"recordCountBefore={manifestRecordCountBefore} recordCountAfter={manifestRecordCountAfter} " +
                        $"recordBatchCount={manifestDiagnostics.RecordBatchCount} recordBatchFlushCount={manifestDiagnostics.RecordBatchFlushCount} " +
                        $"recordBatchMs={manifestDiagnostics.RecordBatchMs} " +
                        $"dbConnMs={manifestDiagnostics.DbConnectionOpenMs} dbTransMs={manifestDiagnostics.DbTransactionBeginMs} " +
                        $"dbDelMs={manifestDiagnostics.DbDeleteLoopMs} dbInsMs={manifestDiagnostics.DbInsertLoopMs} dbCommitMs={manifestDiagnostics.DbCommitMs}");
                    swLoop.Stop();
                    deleteLoopTotalMs = swLoop.ElapsedMilliseconds;
                    LogService.Info(
                        $"[Perf] ExecuteDelete MidFD managed trash loop: {deleteLoopTotalMs}ms " +
                        $"for {selection.Count} items, success={successCount}, " +
                        $"fail={failCount}, canceled={exitStatus == FileOpExitStatus.Canceled}");
                }
                else
                {
                    var controlledResult = await Task.Run(() =>
                    {
                        int currentSuccess = 0;
                        int currentFailCount = 0;
                        FileOpExitStatus currentStatus = FileOpExitStatus.Success;
                        var currentSuccessPaths = new List<string>();
                        var chunkSw = Stopwatch.StartNew();
                        int chunkStartIndex = 0;
                        long chunkMaxPerItemMs = 0;
                        var pendingUiPaths = new List<string>();
                        var uiThrottleSw = Stopwatch.StartNew();
                        const int UI_CHUNK_SIZE = 250;
                        const int UI_THROTTLE_MS = 250;
                        bool useChunkedShellDelete = totalCount >= ChunkedShellRecycleBinDeleteMinItems;
                        if (useChunkedShellDelete)
                        {
                            int chunkCursor = 0;
                            while (chunkCursor < selection.FullPaths.Count)
                            {
                                if (token.IsCancellationRequested)
                                {
                                    currentStatus = FileOpExitStatus.Canceled;
                                    break;
                                }
                                int chunkCount = Math.Min(ChunkedShellRecycleBinDeleteChunkSize, selection.FullPaths.Count - chunkCursor);
                                List<string> chunkPaths = selection.FullPaths.Skip(chunkCursor).Take(chunkCount).ToList();
                                string progressFileName = Path.GetFileName(chunkPaths[^1]);
                                var uiSw = Stopwatch.StartNew();
                                Invoke(new Action(() =>
                                {
                                    ShowFileOperationProgressIfCurrent(
                                        deleteStatusVersion,
                                        "Delete",
                                        currentSuccess + currentFailCount + 1,
                                        totalCount,
                                        progressFileName);
                                    UpdateShellDeleteProgressFallbackIfCurrent(
                                        deleteStatusVersion,
                                        currentSuccess + currentFailCount,
                                        totalCount,
                                        progressFileName);
                                }));
                                uiSw.Stop();
                                progressUiTotalMs += uiSw.ElapsedMilliseconds;
                                progressUpdateCount++;
                                ShellRecycleBinDeleteService.Result chunkResult =
                                    ShellRecycleBinDeleteService.DeleteToRecycleBinAsync(
                                        chunkPaths,
                                        IntPtr.Zero,
                                        token,
                                        static _ => { })
                                    .GetAwaiter()
                                    .GetResult();
                                currentSuccess += chunkResult.SuccessCount;
                                currentFailCount += chunkResult.FailCount;
                                currentSuccessPaths.AddRange(chunkResult.SuccessPaths);
                                pendingUiPaths.AddRange(chunkResult.SuccessPaths);
                                if (pendingUiPaths.Count > 0)
                                {
                                    var removalSw = Stopwatch.StartNew();
                                    var flushPaths = pendingUiPaths.ToList();
                                    pendingUiPaths.Clear();
                                    Invoke(new Action(() =>
                                    {
                                        ApplyProgressiveDeleteUiChunk(
                                            flushPaths,
                                            deleteStatusVersion,
                                            ref markRemovalTotalMs,
                                            ref markRemoveCallCount,
                                            ref headerMenuUpdateTotalMs,
                                            ref headerUpdateCount,
                                            ref menuUpdateCount,
                                            ref invalidateCount,
                                            midFdTrashBatchId,
                                            "ShellChunk");
                                        UpdateShellDeleteProgressFallbackIfCurrent(
                                            deleteStatusVersion,
                                            currentSuccess + currentFailCount,
                                            totalCount,
                                            progressFileName);
                                    }));
                                    removalSw.Stop();
                                    progressiveRemovalTotalMs += removalSw.ElapsedMilliseconds;
                                    if (removalSw.ElapsedMilliseconds > uiFlushMaxMs) uiFlushMaxMs = removalSw.ElapsedMilliseconds;
                                    progressiveRemovalCount++;
                                }
                                LogService.Info(
                                    $"[Perf] ExecuteDelete chunked shell recycle-bin chunk: start={chunkCursor} count={chunkCount} " +
                                    $"success={chunkResult.SuccessCount} fail={chunkResult.FailCount} canceled={chunkResult.IsCanceled} " +
                                    $"serviceTotal={chunkResult.TotalMs}ms perform={chunkResult.PerformOperationsMs}ms");
                                if (chunkResult.IsCanceled)
                                {
                                    currentStatus = FileOpExitStatus.Canceled;
                                    break;
                                }
                                if (chunkResult.HResult < 0)
                                {
                                    currentStatus = FileOpExitStatus.Error;
                                    break;
                                }
                                chunkCursor += chunkCount;
                            }
                        }
                        else
                        {
                            foreach (string path in selection.FullPaths)
                            {
                                if (token.IsCancellationRequested)
                                {
                                    currentStatus = FileOpExitStatus.Canceled;
                                    break;
                                }
                                var itemSw = Stopwatch.StartNew();
                                string fileName = Path.GetFileName(path);
                                bool shouldUpdateProgress = (currentSuccess + currentFailCount) % 100 == 0 || pendingUiPaths.Count >= UI_CHUNK_SIZE || uiThrottleSw.ElapsedMilliseconds >= UI_THROTTLE_MS;
                                if (shouldUpdateProgress)
                                {
                                    var uiSw = Stopwatch.StartNew();
                                    Invoke(new Action(() =>
                                    {
                                        ShowFileOperationProgressIfCurrent(
                                            deleteStatusVersion,
                                            "Delete",
                                            currentSuccess + currentFailCount + 1,
                                            totalCount,
                                            fileName);
                                        UpdateShellDeleteProgressFallbackIfCurrent(
                                            deleteStatusVersion,
                                            currentSuccess + currentFailCount,
                                            totalCount,
                                            fileName);
                                    }));
                                    uiSw.Stop();
                                    progressUiTotalMs += uiSw.ElapsedMilliseconds;
                                    progressUpdateCount++;
                                }
                                try
                                {
                                    FileOperationService.DeleteToRecycleBin(path);
                                    currentSuccess++;
                                    currentSuccessPaths.Add(path);
                                    pendingUiPaths.Add(path);
                                    string flushReason = "";
                                    if (pendingUiPaths.Count >= UI_CHUNK_SIZE) flushReason = "CountThreshold";
                                    else if (uiThrottleSw.ElapsedMilliseconds >= UI_THROTTLE_MS) flushReason = "TimeThreshold";
                                    if (!string.IsNullOrEmpty(flushReason))
                                    {
                                        var removalSw = Stopwatch.StartNew();
                                        var flushPaths = pendingUiPaths.ToList();
                                        pendingUiPaths.Clear();
                                        Invoke(new Action(() =>
                                        {
                                            ApplyProgressiveDeleteUiChunk(
                                                flushPaths,
                                                deleteStatusVersion,
                                                ref markRemovalTotalMs,
                                                ref markRemoveCallCount,
                                                ref headerMenuUpdateTotalMs,
                                                ref headerUpdateCount,
                                                ref menuUpdateCount,
                                                ref invalidateCount,
                                                midFdTrashBatchId,
                                                flushReason);
                                            UpdateShellDeleteProgressFallbackIfCurrent(
                                                deleteStatusVersion,
                                                currentSuccess,
                                                totalCount,
                                                fileName);
                                        }));
                                        uiThrottleSw.Restart(); // restart AFTER invoke to avoid degenerate 1-item flushes
                                        removalSw.Stop();
                                        progressiveRemovalTotalMs += removalSw.ElapsedMilliseconds;
                                        if (removalSw.ElapsedMilliseconds > uiFlushMaxMs) uiFlushMaxMs = removalSw.ElapsedMilliseconds;
                                        progressiveRemovalCount++;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Invoke(new Action(() =>
                                        MessageBox.Show($"削除失敗: {path}\n{ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error)));
                                    currentFailCount++;
                                    currentStatus = FileOpExitStatus.Error;
                                    break;
                                }
                                itemSw.Stop();
                                long itemMs = itemSw.ElapsedMilliseconds;
                                if (itemMs > chunkMaxPerItemMs) chunkMaxPerItemMs = itemMs;
                                if (itemMs > 1000)
                                {
                                    LogService.Info($"[LargeDeletePerf] SlowItem operationId={midFdTrashBatchId} index={currentSuccess + currentFailCount} elapsedMs={itemMs} stage=StandardRecycleBinDelete path={path}");
                                }
                                if ((currentSuccess + currentFailCount) % 100 == 0)
                                {
                                    LogService.Info($"[LargeDeletePerf] DeleteChunk operationId={midFdTrashBatchId} start={chunkStartIndex} count=100 elapsedMs={chunkSw.ElapsedMilliseconds} avgPerItemMs={chunkSw.ElapsedMilliseconds / 100.0:F1} maxPerItemMs={chunkMaxPerItemMs}");
                                    chunkSw.Restart();
                                    chunkStartIndex = currentSuccess + currentFailCount;
                                    chunkMaxPerItemMs = 0;
                                }
                            }
                        }
                    // Final flush
                    if (pendingUiPaths.Count > 0)
                    {
                        var removalSw = Stopwatch.StartNew();
                        var flushPaths = pendingUiPaths.ToList();
                        pendingUiPaths.Clear();
                        Invoke(new Action(() => ApplyProgressiveDeleteUiChunk(
                            flushPaths,
                            deleteStatusVersion,
                            ref markRemovalTotalMs,
                            ref markRemoveCallCount,
                            ref headerMenuUpdateTotalMs,
                            ref headerUpdateCount,
                            ref menuUpdateCount,
                            ref invalidateCount,
                            midFdTrashBatchId,
                            currentStatus == FileOpExitStatus.Canceled ? "CancelFinalFlush" : "FinalFlush")));
                        removalSw.Stop();
                        progressiveRemovalTotalMs += removalSw.ElapsedMilliseconds;
                        if (removalSw.ElapsedMilliseconds > uiFlushMaxMs) uiFlushMaxMs = removalSw.ElapsedMilliseconds;
                        progressiveRemovalCount++;
                    }
                        return (currentSuccess, currentFailCount, currentStatus, currentSuccessPaths);
                    }, token);
                    swLoop.Stop();
                    deleteLoopTotalMs = swLoop.ElapsedMilliseconds;
                    LogService.Info(
                        $"[Perf] ExecuteDelete controlled recycle-bin loop: {deleteLoopTotalMs}ms " +
                        $"for {selection.Count} items, success={controlledResult.currentSuccess}, " +
                        $"fail={controlledResult.currentFailCount}, canceled={controlledResult.currentStatus == FileOpExitStatus.Canceled}");
                    successCount = controlledResult.currentSuccess;
                    failCount = controlledResult.currentFailCount;
                    exitStatus = controlledResult.currentStatus;
                    successPaths.AddRange(controlledResult.currentSuccessPaths);
                }
            }
            bool isFullSuccess = exitStatus == FileOpExitStatus.Success
                && successCount == totalCount
                && failCount == 0
                && !token.IsCancellationRequested;
            if (exitStatus == FileOpExitStatus.Canceled && useMidFdManagedTrash && successCount > 0)
            {
                int pendingCount = totalCount - successCount - failCount;
                var resolution = _fileOperationDialogCoordinator.ShowDeleteCancelResolution(this, successCount, pendingCount, failCount);
                LogService.Info($"[DeleteCancelResolution] Cancel requested success={successCount} pending={pendingCount} failed={failCount} UserChoice={resolution}");
                if (resolution == DeleteCancelResolution.RestoreNow)
                {
                    ShowStatusMessage($"{successCount} 件を復元中...");
                    LogService.Info($"[DeleteCancelRestorePerf] RestoreNow started items={recycleBinDeleteUndoItems.Count}");
                    var restoreSw = Stopwatch.StartNew();
                    long fileMoveTotalMs = 0;
                    long statusUpdateTotalMs = 0;
                    long maxItemMs = 0;
                    int slowCount = 0;
                    var restoredPaths = new List<string>();
                    var restoreResult = await Task.Run(() =>
                    {
                        try
                        {
                            MidFdManagedTrashService.ResetManifestOperationDiagnostics();
                            bool suppressLogging = recycleBinDeleteUndoItems.Count > 10;
                            if (suppressLogging)
                            {
                                MidFdManagedTrashService.SetLoggingSuppression(true); // Still set global for safety, but pass param too
                            }
                            foreach (var item in recycleBinDeleteUndoItems)
                            {
                                var itemSw = Stopwatch.StartNew();
                                try
                                {
                                    MidFdManagedTrashService.RestoreFromTrash(item, skipStatusUpdate: true, suppressLogging: suppressLogging);
                                    restoredPaths.Add(item.RecycleBinPath);
                                }
                                catch (Exception ex)
                                {
                                    LogService.Error($"[DeleteCancelRestorePerf] RestoreNow item failed path={item.BeforePath}", ex);
                                }
                                itemSw.Stop();
                                long elapsed = itemSw.ElapsedMilliseconds;
                                if (elapsed > 100)
                                {
                                    slowCount++;
                                    LogService.Info($"[DeleteCancelRestorePerf] RestoreNow slowItem path={item.BeforePath} elapsedMs={elapsed}");
                                }
                                if (elapsed > maxItemMs) maxItemMs = elapsed;
                                fileMoveTotalMs += elapsed;
                            }
                            if (restoredPaths.Count > 0)
                            {
                                var sSw = Stopwatch.StartNew();
                                MidFdManagedTrashService.UpdateRecordStatuses(restoredPaths, TrashRecordStatus.Restored);
                                sSw.Stop();
                                statusUpdateTotalMs = sSw.ElapsedMilliseconds;
                            }
                            int suppressedCount = MidFdManagedTrashService.GetSuppressedSuccessCount();
                            if (suppressedCount > 0 || recycleBinDeleteUndoItems.Count > 10)
                            {
                                LogService.Info($"[MidFdTrashLogThrottle] Summary operation=RestoreNow items={recycleBinDeleteUndoItems.Count} suppressed={suppressedCount} [MidFdTrashLogThrottle] RuntimeGapCorrective active");
                            }
                            int suppressedCountAtEnd = MidFdManagedTrashService.GetSuppressedSuccessCount();
                            return (true, suppressedCountAtEnd);
                        }
                        catch (Exception ex)
                        {
                            LogService.Error("[DeleteCancelResolution] RestoreNow fatal error", ex);
                            return (false, 0);
                        }
                        finally
                        {
                            MidFdManagedTrashService.SetLoggingSuppression(false);
                        }
                    });
                    restoreSw.Stop();
                    long totalMs = restoreSw.ElapsedMilliseconds;
                    if (restoreResult.Item1)
                    {
                        int suppressedCount = restoreResult.Item2;
                        LogService.Info($"[DeleteCancelRestorePerf] RestoreNow completed items={successCount} totalMs={totalMs} fileMoveMs={fileMoveTotalMs} statusUpdateMs={statusUpdateTotalMs} slowItemCount={slowCount} maxItemMs={maxItemMs} perItemAvgMs={(double)totalMs / Math.Max(1, successCount):F2} suppressedSuccessLogs={suppressedCount}");
                        ShowStatusMessage("中断し、削除済みのファイルを復元しました。");
                    }
                    else
                    {
                        LogService.Warn($"[DeleteCancelRestorePerf] RestoreNow completed with some failures. totalMs={totalMs}");
                        ShowStatusMessage("中断しましたが、一部のファイル復元に失敗しました。");
                    }
                    canRecordRecycleBinUndo = false;
                    recycleBinDeleteUndoItems.Clear();
                }
                else
                {
                    // KeepDeleted or Cancel -> record undo for partial success items
                    canRecordRecycleBinUndo = true;
                    LogService.Info($"[DeleteCancelResolution] PartialUndoBatch will be registered count={successCount}");
                    ShowStatusMessage($"中断しました。削除済み {successCount} 件は Ctrl+Z で復元できます。");
                }
            }
            // partial / cancel では安全側として Undo 履歴を積まない。
            if (!usePermanentDelete && canRecordRecycleBinUndo && isFullSuccess && recycleBinDeleteUndoItems.Count != totalCount)
            {
                // Windows標準ごみ箱の場合、MidFD削除 Undo/Redo を積まない契約
                canRecordRecycleBinUndo = false;
                recycleBinDeleteUndoItems.Clear();
            }
            exitStatus = FileOperationPresentationHelper.NormalizeExitStatus(exitStatus, successCount, totalCount, failCount: failCount);
            if (!usePermanentDelete &&
                canRecordRecycleBinUndo &&
                (exitStatus == FileOpExitStatus.Success || (exitStatus == FileOpExitStatus.Canceled && recycleBinDeleteUndoItems.Count > 0)) &&
                recycleBinDeleteUndoItems.Count == successCount &&
                successCount > 0)
            {
                var undoRecordStopwatch = Stopwatch.StartNew();
                bool isPartialCancel = exitStatus == FileOpExitStatus.Canceled;
                _fileOperationUndoRedoService.RecordBatch(
                    FileOperationUndoRedoOperation.DeleteToMidFdTrash,
                    FileOperationUndoRedoService.CreateDeleteToTrashBatch(recycleBinDeleteUndoItems),
                    isPartialCancel);
                undoRecordStopwatch.Stop();
                undoRecordMs = undoRecordStopwatch.ElapsedMilliseconds;
                recordedRecycleBinUndo = true;
                LogService.Info($"[ShellDeleteUndo] Recorded MidFD managed trash undo batch: {recycleBinDeleteUndoItems.Count} items in {undoRecordMs}ms");
            }
            else if (!usePermanentDelete && useMidFdManagedTrash && isFullSuccess && !recordedRecycleBinUndo)
            {
                LogService.Warn(
                    $"[ShellDeleteUndo] MidFD recycle-bin undo batch was not recorded. " +
                    $"canRecord={canRecordRecycleBinUndo}, undoItems={recycleBinDeleteUndoItems.Count}, total={totalCount}");
            }
        }
        catch (OperationCanceledException)
        {
            exitStatus = FileOpExitStatus.Canceled;
        }
        catch (Exception ex)
        {
            exitStatus = FileOpExitStatus.Error;
            LogService.Error("ExecuteDelete async error", ex);
            _fileOperationDialogCoordinator.ShowUnexpectedOperationError(this, usePermanentDelete ? "完全削除" : "削除", ex);
        }
        finally
        {
            if (useMidFdManagedTrash)
            {
                var fSw = Stopwatch.StartNew();
                int suppressedCount = MidFdManagedTrashService.GetSuppressedSuccessCount();
                if (suppressedCount > 0 || totalCount > 10)
                {
                    string opName = exitStatus == FileOpExitStatus.Canceled ? "Delete(Canceled)" : "Delete";
                    LogService.Info($"[MidFdTrashLogThrottle] Summary operation={opName} items={totalCount} processed={successCount} suppressed={suppressedCount} [MidFdTrashLogThrottle] RuntimeGapCorrective active");
                }
                MidFdManagedTrashService.FlushManifestBatch();
                MidFdManagedTrashService.SetLoggingSuppression(false);
                fSw.Stop();
                manifestSaveTotalMs += fSw.ElapsedMilliseconds;
                manifestSaveCount++;
                manifestFlushCount++;
            }
            var manifestDiagnostics = MidFdManagedTrashService.GetManifestOperationDiagnostics();
            var progressCompleteStopwatch = Stopwatch.StartNew();
            CompleteShellDeleteProgressFallbackIfCurrent(deleteStatusVersion, exitStatus, successCount, totalCount, failCount);
            progressCompleteStopwatch.Stop();
            progressCompleteMs = progressCompleteStopwatch.ElapsedMilliseconds;
            var postOperationStopwatch = Stopwatch.StartNew();
            HandlePostOperation(_fileOperationPostOperationCoordinator.CreateDeleteResult(
                exitStatus,
                successCount,
                totalCount,
                nextTargetName,
                usePermanentDelete,
                recordedRecycleBinUndo,
                failCount));
            postOperationStopwatch.Stop();
            postOperationMs = postOperationStopwatch.ElapsedMilliseconds;
            deleteTotalStopwatch.Stop();
            long cancelLatencyMs = 0;
            if (exitStatus == FileOpExitStatus.Canceled && _fileOpUiState.CancelRequestedTimestamp > 0)
            {
                cancelLatencyMs = (long)Stopwatch.GetElapsedTime(_fileOpUiState.CancelRequestedTimestamp).TotalMilliseconds;
            }
            string mode = usePermanentDelete ? "PermanentDelete" : (useMidFdManagedTrash ? "MidFdManagedTrash" : "WindowsRecycleBin");
            LogService.Info($"[LargeDeletePerf] BatchSummary operationId={midFdTrashBatchId} mode={mode} count={totalCount} success={successCount} fail={failCount} canceled={exitStatus == FileOpExitStatus.Canceled} totalMs={deleteTotalStopwatch.ElapsedMilliseconds} undoRecorded={recordedRecycleBinUndo}");
            LogService.Info($"[LargeDeletePerf] StageSummary operationId={midFdTrashBatchId} selectionResolveMs={selectionResolveMs} outsideWarningMs={outsideWarningMs} confirmDialogMs={confirmDialogMs} focusTargetPrepareMs={focusTargetPrepareMs} deleteLoopMs={deleteLoopTotalMs} manifestOperationMs={manifestOperationTotalMs} manifestFileMoveMs={manifestFileMoveTotalMs} manifestUpsertMs={manifestUpsertTotalMs} manifestLogMs={manifestLogTotalMs} manifestLogSuppressedCount={manifestLogSuppressedCount} manifestLogSuccessCount={manifestSuccessLogCount} manifestChunkSummaryCount={manifestChunkSummaryCount} manifestSlowItemCount={manifestSlowItemCount} manifestUpsertCount={manifestUpsertCount} manifestAppendMode={manifestAppendMode} manifestAppendCount={manifestAppendCount} manifestUpsertScanCount={manifestUpsertScanCount} manifestAppendMs={manifestAppendMs} manifestRecordCountBefore={manifestRecordCountBefore} manifestRecordCountAfter={manifestRecordCountAfter} manifestSaveCount={manifestSaveCount} manifestFlushCount={manifestFlushCount} manifestSaveTotalMs={manifestSaveTotalMs} dbConnMs={manifestDiagnostics.DbConnectionOpenMs} dbTransMs={manifestDiagnostics.DbTransactionBeginMs} dbDelMs={manifestDiagnostics.DbDeleteLoopMs} dbInsMs={manifestDiagnostics.DbInsertLoopMs} dbCommitMs={manifestDiagnostics.DbCommitMs} [ManagedTrashPerfInvestigation] totalFileMoveMs={manifestDiagnostics.TotalFileMoveMs} crossVolumeMoveCount={manifestDiagnostics.CrossVolumeMoveCount} sameVolumeCount={manifestDiagnostics.SameVolumeMoveCount} appDataFallbackCount={manifestDiagnostics.AppDataFallbackMoveCount} cancelLatencyMs={cancelLatencyMs} progressUiMs={progressUiTotalMs} progressCount={progressUpdateCount} progressiveRemovalMs={progressiveRemovalTotalMs} uiFlushCount={progressiveRemovalCount} uiFlushMaxMs={uiFlushMaxMs} markRemovalMs={markRemovalTotalMs} headerMenuUpdateMs={headerMenuUpdateTotalMs} postReloadMs={postOperationMs} undoRecordMs={undoRecordMs}");
            if (useMidFdManagedTrash && successCount > 0)
            {
                _ = MidFdManagedTrashService.RunRetentionCleanupAsync(_settings, _fileOperationUndoRedoService, "DeleteComplete");
            }
        }
    }
    private void ScheduleBrowserFocusReturnAfterFileOperation(string reason)
    {
        if (!CanRestoreBrowserFocusAfterFileOperation())
        {
            return;
        }
        BeginInvoke(new Action(() =>
        {
            if (!CanRestoreBrowserFocusAfterFileOperation() || _uiMode != UIMode.Browser || !browserPanel.Visible)
            {
                return;
            }
            Activate();
            browserPanel.Focus();
            LogService.Info(
                $"[FileOperationFocus] Browser focus returned. reason={reason}, " +
                $"activeControl={DescribeControl(ActiveControl)}, browserFocused={browserPanel.Focused}");
        }));
    }
    private bool CanRestoreBrowserFocusAfterFileOperation()
    {
        if (IsDisposed || !IsHandleCreated || !Visible)
        {
            return false;
        }
        Form? activeForm = Form.ActiveForm;
        if (activeForm != null && !ReferenceEquals(activeForm, this))
        {
            return false;
        }
        foreach (Form ownedForm in OwnedForms)
        {
            if (ownedForm != null && !ownedForm.IsDisposed && ownedForm.Visible)
            {
                return false;
            }
        }
        return true;
    }
    private void ShowFileOperationUndoRedoProgressFallback(string operationName, int totalCount)
    {
        CloseFileOperationUndoRedoProgressFallback();
        var form = Presentation.FileOperationFallbackUiPresenter.ShowProgressFallback(
            this,
            operationName,
            totalCount,
            requestCancel: null,
            canCancel: false,
            closedCallback: closedForm =>
            {
                if (ReferenceEquals(_undoRedoProgressFallback, closedForm))
                {
                    _undoRedoProgressFallback = null;
                }
                ScheduleBrowserFocusReturnAfterFileOperation("UndoRedoProgressFallbackClosed");
            });
        _undoRedoProgressFallback = form;
        form.UpdateProgress(0, totalCount, "準備中...", cancelRequested: false);
    }
    private void UpdateFileOperationUndoRedoProgressFallbackFromWorker(int processedCount, int totalCount, string currentFileName)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }
        BeginInvoke(new Action(() =>
        {
            _undoRedoProgressFallback?.UpdateProgress(processedCount, totalCount, currentFileName, cancelRequested: false);
        }));
    }
    private void CompleteFileOperationUndoRedoProgressFallback(string message)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }
        BeginInvoke(new Action(() =>
        {
            _undoRedoProgressFallback?.Complete(message);
        }));
    }
    private void CloseFileOperationUndoRedoProgressFallback()
    {
        Presentation.FileOperationFallbackUiPresenter.CloseProgressFallback(ref _undoRedoProgressFallback);
    }
    private void ApplyProgressiveDeleteUi(string deletedPath, int statusVersion, ref long markRemovalMs, ref int markRemoveCount, ref long headerUpdateMs, ref int headerUpdateCount, ref int menuUpdateCount, ref int invalidateCount)
    {
        if (!IsCurrentFileOperationStatusVersion(statusVersion))
        {
            return;
        }
        var markSw = Stopwatch.StartNew();
        UnmarkPath(deletedPath);
        markSw.Stop();
        markRemovalMs += markSw.ElapsedMilliseconds;
        markRemoveCount++;
        for (int i = 0; i < fileListView.Items.Count; i++)
        {
            if (fileListView.Items[i].Tag is string itemPath &&
                string.Equals(itemPath, deletedPath, StringComparison.OrdinalIgnoreCase))
            {
                fileListView.Items.RemoveAt(i);
                int pageLocalCursorIndex = GetBrowserPageLocalCursorIndex();
                if (fileListView.Items.Count == 0)
                {
                    _browserCursorIndex = 0;
                }
                else if (pageLocalCursorIndex >= fileListView.Items.Count)
                {
                    _browserCursorIndex = _browserPageStartIndex + fileListView.Items.Count - 1;
                }
                else if (i < pageLocalCursorIndex)
                {
                    _browserCursorIndex--;
                }
                break;
            }
        }
        if (string.Equals(_currentPreviewTarget, deletedPath, StringComparison.OrdinalIgnoreCase))
        {
            _currentPreviewTarget = null;
            ClearPreview();
        }
        var headerSw = Stopwatch.StartNew();
        UpdateInfoPanel();
        headerSw.Stop();
        headerUpdateMs += headerSw.ElapsedMilliseconds;
        headerUpdateCount++;
        UpdateMenuStripState();
        menuUpdateCount++;
        UpdateFunctionBar();
        browserPanel.Invalidate();
        invalidateCount++;
    }
    private void ApplyProgressiveDeleteUi(string deletedPath, int statusVersion)
    {
        long dummyMs = 0;
        int dummyCount = 0;
        ApplyProgressiveDeleteUi(deletedPath, statusVersion, ref dummyMs, ref dummyCount, ref dummyMs, ref dummyCount, ref dummyCount, ref dummyCount);
    }
    private void ApplyProgressiveDeleteUiChunk(
        List<string> deletedPaths,
        int statusVersion,
        ref long markRemovalMs,
        ref int markRemoveCount,
        ref long headerUpdateMs,
        ref int headerUpdateCount,
        ref int menuUpdateCount,
        ref int invalidateCount,
        string operationId,
        string reason)
    {
        if (!IsCurrentFileOperationStatusVersion(statusVersion) || deletedPaths.Count == 0)
        {
            return;
        }
        var swFlush = Stopwatch.StartNew();
        // 1. Bulk Unmark
        var markSw = Stopwatch.StartNew();
        int removedMarks = _markedFiles.RemoveRange(deletedPaths);
        markSw.Stop();
        markRemovalMs += markSw.ElapsedMilliseconds;
        markRemoveCount++;
        if (removedMarks > 0)
        {
            // 大量削除中の chunk flush では、mark 全件の File I/O を避けるため
            // count-only のキャッシュ更新に留める。
            SetCountOnlyMarkSummaryCache();
            InvalidateRecentMultiMarkIntent();
            ClearPendingEscExitMarkPersistence();
            LogService.Info($"[LargeDeletePerf] BulkUnmark operationId={operationId} count={removedMarks} elapsedMs={markSw.ElapsedMilliseconds} reason={reason}");
        }
        // 2. Bulk UI Removal
        var targets = new HashSet<string>(deletedPaths, StringComparer.OrdinalIgnoreCase);
        for (int i = fileListView.Items.Count - 1; i >= 0; i--)
        {
            if (fileListView.Items[i].Tag is string itemPath && targets.Contains(itemPath))
            {
                fileListView.Items.RemoveAt(i);
                int pageLocalCursorIndex = GetBrowserPageLocalCursorIndex();
                if (fileListView.Items.Count == 0)
                {
                    _browserCursorIndex = 0;
                }
                else if (pageLocalCursorIndex >= fileListView.Items.Count)
                {
                    _browserCursorIndex = _browserPageStartIndex + fileListView.Items.Count - 1;
                }
                else if (i < pageLocalCursorIndex)
                {
                    _browserCursorIndex--;
                }
            }
        }
        foreach (var path in deletedPaths)
        {
            if (string.Equals(_currentPreviewTarget, path, StringComparison.OrdinalIgnoreCase))
            {
                _currentPreviewTarget = null;
                ClearPreview();
                break;
            }
        }
        // 3. UI Global Updates
        if (reason.EndsWith("FinalFlush"))
        {
            var headerSw = Stopwatch.StartNew();
            UpdateInfoPanel();
            headerSw.Stop();
            headerUpdateMs += headerSw.ElapsedMilliseconds;
            headerUpdateCount++;
            UpdateMenuStripState();
            menuUpdateCount++;
            UpdateFunctionBar();
            browserPanel.Invalidate();
            invalidateCount++;
        }
        swFlush.Stop();
        LogService.Info($"[LargeDeletePerf] UiFlush operationId={operationId} reason={reason} items={deletedPaths.Count} elapsedMs={swFlush.ElapsedMilliseconds}");
    }
    private void ShowShellDeleteProgressFallback(int statusVersion, int totalCount)
    {
        if (!IsCurrentFileOperationStatusVersion(statusVersion))
        {
            return;
        }
        CloseShellDeleteProgressFallback();
        StartFileOperationProgressIndicator("Delete", totalCount);

        _shellDeleteProgressFallback = Presentation.FileOperationFallbackUiPresenter.ShowShellDeleteProgressFallback(
            this,
            totalCount,
            () => RequestActiveFileOperationCancel("ShellDeleteProgressFallback"),
            () =>
            {
                if (ReferenceEquals(_shellDeleteProgressFallback, null) == false)
                {
                    _shellDeleteProgressFallback = null;
                }
                ScheduleBrowserFocusReturnAfterFileOperation("ShellDeleteProgressFallbackClosed");
            });
    }
    private void PositionProgressFallbackForm(Form form)
        => Presentation.FileOperationFallbackUiPresenter.PositionProgressFallbackForm(this, form);
    private void UpdateShellDeleteProgressFallbackIfCurrent(int statusVersion, int processedCount, int totalCount, string currentFileName)
    {
        if (!IsCurrentFileOperationStatusVersion(statusVersion))
        {
            return;
        }
        UpdateFileOperationProgressIndicatorIfCurrent(statusVersion, "Delete", processedCount, totalCount);
        Presentation.FileOperationFallbackUiPresenter.UpdateShellDeleteProgressFallbackIfCurrent(
            _shellDeleteProgressFallback,
            processedCount,
            totalCount,
            currentFileName,
            _fileOpUiState.Cts?.IsCancellationRequested ?? false);
    }
    private void UpdateShellDeleteProgressFallbackStateIfCurrent(
        int statusVersion,
        string title,
        string detail,
        bool indeterminate)
    {
        if (!IsCurrentFileOperationStatusVersion(statusVersion))
        {
            return;
        }
        _shellDeleteProgressFallback?.UpdateState(
            title,
            detail,
            indeterminate,
            _fileOpUiState.Cts?.IsCancellationRequested ?? false);
    }
    private void CompleteShellDeleteProgressFallbackIfCurrent(
        int statusVersion,
        FileOpExitStatus exitStatus,
        int successCount,
        int totalCount,
        int failCount)
    {
        if (!IsCurrentFileOperationStatusVersion(statusVersion))
        {
            return;
        }
        Presentation.FileOperationFallbackUiPresenter.CompleteShellDeleteProgressFallbackIfCurrent(
            _shellDeleteProgressFallback,
            exitStatus,
            successCount,
            totalCount,
            failCount);
    }
    private void CloseShellDeleteProgressFallback()
    {
        Presentation.FileOperationFallbackUiPresenter.CloseProgressFallback(ref _shellDeleteProgressFallback);
    }
    private void ExecuteClipboardCopy()
    {
        var entryPlan = _fileOperationEntryCoordinator.CreateSelectionEntryPlan(
            _isClipboardBusy,
            null,
            _fileOpUiState.Cts != null,
            "コピー",
            ResolveSelection(),
            "コピー対象がありません。");
        if (!entryPlan.CanProceed)
        {
            if (!string.IsNullOrEmpty(entryPlan.StatusMessage))
            {
                ShowStatusMessage(entryPlan.StatusMessage, 1000);
            }
            return;
        }
        var selection = entryPlan.Selection;
        if (!TryResolveMultiMarkSelectionAction("コピー", "コピーをキャンセルしました。", selection, out selection))
        {
            return;
        }
        _isClipboardBusy = true;
        try
        {
            ShellClipboardService.SetFileDrop(selection.FullPaths, false);
            ShowStatusMessage($"{selection.Count} 件をクリップボードにコピーしました。");
        }
        finally
        {
            _isClipboardBusy = false;
            RefreshBrowserStatusSummary();
        }
    }
    private void ExecuteClipboardCut(SelectionResult? selectionSnapshot = null)
    {
        if (GuardReadOnlyBrowserTab("切り取り")) return;
        var entryPlan = _fileOperationEntryCoordinator.CreateSelectionEntryPlan(
            _isClipboardBusy,
            null,
            _fileOpUiState.Cts != null,
            "切り取り",
            ResolveSelection(selectionSnapshot),
            "切り取り対象がありません。");
        if (!entryPlan.CanProceed)
        {
            if (!string.IsNullOrEmpty(entryPlan.StatusMessage))
            {
                ShowStatusMessage(entryPlan.StatusMessage, 1000);
            }
            return;
        }
        _isClipboardBusy = true;
        try
        {
            var selection = entryPlan.Selection;
            ShellClipboardService.SetFileDrop(selection.FullPaths, true);
            ShowStatusMessage($"{selection.Count} 件をクリップボードに切り取り登録しました。");
        }
        finally
        {
            _isClipboardBusy = false;
            RefreshBrowserStatusSummary();
        }
    }
    private async void ExecuteClipboardPaste()
    {
        if (GuardMutationBusy()) return;
        if (GuardReadOnlyBrowserTab())
        {
            return;
        }
        if (!ShellClipboardService.TryHasFileDrop(out bool hasFileDrop, out string? clipboardError))
        {
            ShowStatusMessage("クリップボードの確認に失敗しました");
            return;
        }
        if (!ShellClipboardService.TryHasImage(out bool hasImage, out string? imageClipboardError))
        {
            ShowStatusMessage("クリップボードの確認に失敗しました");
            return;
        }
        if (!ShellClipboardService.TryHasText(out bool hasText, out string? textClipboardError))
        {
            ShowStatusMessage("クリップボードの確認に失敗しました");
            return;
        }
        var pasteEntryPlan = _fileOperationEntryCoordinator.CreateClipboardPasteEntryPlan(
            _uiMode == UIMode.Browser,
            _isClipboardBusy,
            _fileOpUiState.Cts != null,
            _fileOpUiState.Cts?.IsCancellationRequested ?? false,
            hasFileDrop,
            hasImage,
            hasText,
            _navigationService.CurrentPath);
        if (!pasteEntryPlan.CanProceed)
        {
            if (!string.IsNullOrEmpty(pasteEntryPlan.StatusMessage))
            {
                ShowStatusMessage(pasteEntryPlan.StatusMessage, 1000);
            }
            return;
        }
        if (hasFileDrop && hasImage)
        {
            var choice = _fileOperationDialogCoordinator.ChooseClipboardPasteMode(this);
            if (choice == ClipboardPasteChoice.Cancel)
            {
                ShowStatusMessage("貼り付けはキャンセルされました。");
                return;
            }
            if (choice == ClipboardPasteChoice.ClipboardImage)
            {
                ExecuteClipboardImagePaste();
                return;
            }
        }
        else if (!hasFileDrop && hasImage)
        {
            ExecuteClipboardImagePaste();
            return;
        }
        else if (!hasFileDrop && !hasImage && hasText)
        {
            ExecuteClipboardTextPaste();
            return;
        }
        try
        {
            ShellClipboardService.TryGetSnapshot(out var beforeSnapshot, out _);
            if (!ShellClipboardService.TryGetFileDrop(out List<string> validPaths, out bool isCut))
            {
                ShowStatusMessage("クリップボードに有効なファイルがありません。");
                return;
            }
            string destDir = pasteEntryPlan.CurrentPath;
            if (isCut && validPaths.Count >= 2 && !ConfirmBulkCutPasteMove(validPaths.Count, validPaths[0], destDir))
            {
                ShowStatusMessage("貼り付け(移動)はキャンセルされました。");
                return;
            }
            if (!TryBuildPasteFinalPlan(
                    validPaths,
                    destDir,
                    isCut,
                    out IReadOnlyList<PasteFinalAction> finalPastePlan,
                    out int plannedRenamedCount,
                    out string? plannedFirstRenamedName,
                    out bool plannedCanRecordMoveUndoBatch,
                    out bool plannedCanRecordCreatedFilesUndoBatch))
            {
                ShowStatusMessage("貼り付けはキャンセルされました。");
                return;
            }
            IReadOnlyList<LinkOperationRoot> pasteLinkRoots = finalPastePlan
                .Where(action => !action.Skip)
                .Select(action => new LinkOperationRoot(action.SourcePath, action.DestinationPath))
                .ToList();
            IReadOnlyList<LinkOperationRoot> helperPasteLinkRoots = isCut
                ? LinkOperationPreparationService.BuildCrossVolumeMoveRoots(pasteLinkRoots)
                : pasteLinkRoots;
            LinkPreparation linkPreparation = await PreparePasteLinksAsync(
                helperPasteLinkRoots,
                allowHelper: helperPasteLinkRoots.Count > 0,
                CancellationToken.None);
            if (linkPreparation.Canceled)
            {
                ShowStatusMessage("リンク処理のキャンセルにより貼り付けを中止しました。");
                return;
            }
            string pasteOperationDisplayName = isCut ? "貼り付け(移動)" : "貼り付け(コピー)";
            if (GuardMutationBusy()) return;
            CancellationToken token = PrepareFileOperation(pasteOperationDisplayName);
            int pasteStatusVersion = _fileOpUiState.StatusVersion;
            ShowStatusMessage(FileOperationPresentationHelper.GetOperationStartingMessage("Paste", validPaths.Count, destDir));
            StartFileOperationProgressIndicator(isCut ? "Move" : "Copy", validPaths.Count);
            IProgress<FileOperationProgress> progress = _fileOperationDialogCoordinator.CreatePasteProgress(
                isCut,
                message => ShowFileOperationStatusIfCurrent(
                    pasteStatusVersion,
                    (_fileOpUiState.Cts?.IsCancellationRequested ?? false)
                        ? FileOperationPresentationHelper.GetCancelRequestedMessage(_fileOpUiState.ActiveOperationName ?? pasteOperationDisplayName)
                        : message),
                p => UpdateFileOperationProgressIndicatorIfCurrent(
                    pasteStatusVersion,
                    isCut ? "Move" : "Copy",
                    p.ProcessedCount,
                    p.TotalCount));
            var result = await Task.Run(() =>
            {
                string? firstSuccessName = null;
                string? firstRenamedName = null;
                int successCount = 0;
                int skipCount = 0;
                int failCount = 0;
                int renamedCount = 0;
                int linkSuccessCount = linkPreparation.SuccessfulTopLevelSources.Count;
                int linkSkipCount = linkPreparation.SkipCount;
                int linkFailCount = linkPreparation.FailCount;
                bool wasCancelled = false;
                bool canRecordMoveUndoBatch = plannedCanRecordMoveUndoBatch;
                bool canRecordCreatedFilesUndoBatch = plannedCanRecordCreatedFilesUndoBatch;
                int plannedSkipCount = finalPastePlan.Count(action => action.Skip);
                var successfulMoveUndoItems = new List<(string SourcePath, string DestinationPath)>();
                var successfulCreatedFilePaths = new List<string>();
                foreach (PasteFinalAction action in finalPastePlan)
                {
                    if (token.IsCancellationRequested)
                    {
                        wasCancelled = true;
                        break;
                    }
                    string sourcePath = action.SourcePath;
                    string destPath = action.DestinationPath;
                    string fileName = Path.GetFileName(destPath);
                    if (linkPreparation.ExcludedSources.Contains(sourcePath))
                    {
                        if (isCut && linkPreparation.SuccessfulTopLevelSources.Contains(sourcePath))
                        {
                            FileOperationService.Delete(sourcePath);
                        }
                        continue;
                    }
                    progress.Report(new FileOperationProgress(successCount + skipCount + failCount + 1, validPaths.Count, fileName));
                    if (action.Skip)
                    {
                        skipCount++;
                        continue;
                    }
                    if (action.Merge)
                    {
                        try
                        {
                            if (isCut)
                            {
                                CopyCollisionDecision? mergeFileDecision = null;
                                PasteMoveDirectoryIntoExisting(
                                    sourcePath,
                                    destPath,
                                    ref mergeFileDecision,
                                    out bool directoryShouldCancel,
                                    out int directorySkipCount,
                                    out int directoryFailCount,
                                    linkPreparation.ExcludedSources,
                                    linkPreparation.SuccessfulSources);
                                if (directoryShouldCancel)
                                {
                                    wasCancelled = true;
                                    break;
                                }
                                skipCount += directorySkipCount;
                                failCount += directoryFailCount;
                                if (directorySkipCount > 0 || directoryFailCount > 0)
                                    canRecordMoveUndoBatch = false;
                            }
                            else
                            {
                                CopyCollisionDecision? mergeFileDecision = null;
                                PasteCopyDirectoryIntoExisting(
                                    sourcePath,
                                    destPath,
                                    ref mergeFileDecision,
                                    out bool directoryShouldCancel,
                                    linkPreparation.ExcludedSources);
                                if (directoryShouldCancel)
                                {
                                    wasCancelled = true;
                                    break;
                                }
                            }
                            firstSuccessName ??= fileName;
                            successCount++;
                        }
                        catch (OperationCanceledException)
                        {
                            wasCancelled = true;
                            break;
                        }
                        catch (Exception ex)
                        {
                            string opErrName = isCut ? "貼り付け(移動)" : "貼り付け(コピー)";
                            LogService.Error($"{opErrName}フォルダ統合失敗: {fileName}", ex);
                            failCount++;
                        }
                        continue;
                    }
                    try
                    {
                        if (isCut)
                        {
                            if (!action.OverwriteMove &&
                                FileOperationService.IsDirectoryContainerPath(sourcePath) &&
                                !FileOperationService.HaveSameStorageRoot(sourcePath, destPath) &&
                                Directory.Exists(destPath))
                            {
                                CopyCollisionDecision? mergeFileDecision = null;
                                PasteMoveDirectoryIntoExisting(
                                    sourcePath,
                                    destPath,
                                    ref mergeFileDecision,
                                    out bool directoryShouldCancel,
                                    out int directorySkipCount,
                                    out int directoryFailCount,
                                    linkPreparation.ExcludedSources,
                                    linkPreparation.SuccessfulSources);
                                if (directoryShouldCancel)
                                {
                                    wasCancelled = true;
                                    break;
                                }
                                skipCount += directorySkipCount;
                                failCount += directoryFailCount;
                                if (directorySkipCount > 0 || directoryFailCount > 0)
                                    canRecordMoveUndoBatch = false;
                            }
                            else
                            {
                                FileOperationService.Move(sourcePath, destPath, action.OverwriteMove, suppressLogging: validPaths.Count > 100);
                            }
                            if (canRecordMoveUndoBatch)
                            {
                                successfulMoveUndoItems.Add((sourcePath, destPath));
                            }
                        }
                        else
                        {
                            FileOperationService.Copy(sourcePath, destPath, linkPreparation.ExcludedSources);
                            if (canRecordCreatedFilesUndoBatch && File.Exists(destPath))
                            {
                                successfulCreatedFilePaths.Add(destPath);
                            }
                        }
                        firstSuccessName ??= fileName;
                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        string opErrName = isCut ? "切り取り(移動)" : "コピー";
                        LogService.Error($"{opErrName}失敗: {fileName}", ex);
                        failCount++;
                        canRecordMoveUndoBatch = false;
                        canRecordCreatedFilesUndoBatch = false;
                    }
                }
                renamedCount = plannedRenamedCount;
                firstRenamedName = plannedFirstRenamedName;
                IReadOnlyList<FileOperationUndoRedoItem> moveUndoItems =
                    canRecordMoveUndoBatch &&
                    !wasCancelled &&
                    failCount == 0 &&
                    skipCount == 0 &&
                    successCount + linkSuccessCount == validPaths.Count &&
                    successfulMoveUndoItems.Count == validPaths.Count - linkSuccessCount - plannedSkipCount
                        ? FileOperationUndoRedoService.CreateMoveBatch(successfulMoveUndoItems)
                        : Array.Empty<FileOperationUndoRedoItem>();
                IReadOnlyList<FileOperationUndoRedoItem> createdFilesUndoItems =
                    canRecordCreatedFilesUndoBatch &&
                    !wasCancelled &&
                    failCount == 0 &&
                    skipCount == 0 &&
                    successCount + linkSuccessCount == validPaths.Count &&
                    successfulCreatedFilePaths.Count == validPaths.Count - linkSuccessCount - plannedSkipCount
                        ? FileOperationUndoRedoService.CreateCreatedFilesBatch(successfulCreatedFilePaths)
                        : Array.Empty<FileOperationUndoRedoItem>();
                return (successCount: successCount + linkSuccessCount, skipCount: skipCount + linkSkipCount, failCount: failCount + linkFailCount,
                    wasCancelled, firstSuccessName, renamedCount, firstRenamedName, moveUndoItems, createdFilesUndoItems);
            }, token);
            if (isCut && !result.wasCancelled && result.successCount > 0 && result.failCount == 0 && result.skipCount == 0 && beforeSnapshot != null)
            {
                if (ShellClipboardService.TryGetSnapshot(out var afterSnapshot, out _) &&
                    ShellClipboardService.IsSameCutSnapshot(beforeSnapshot, afterSnapshot))
                {
                    ShellClipboardService.TryClear(out _);
                }
            }
            if (result.wasCancelled)
            {
                var canceledResult = new FileOperationResult("Paste", FileOpExitStatus.Canceled, result.successCount, validPaths.Count, result.firstSuccessName,
                    skipCount: result.skipCount, failCount: result.failCount);
                string cancelMsg = FileOperationPresentationHelper.GetPasteResultStatusMessage(
                    canceledResult,
                    isCut,
                    result.renamedCount,
                    result.firstRenamedName,
                    preserveClipboardOnIncomplete: true);
                HandlePostOperation(new FileOperationResult("Paste", FileOpExitStatus.Canceled, result.successCount, validPaths.Count, result.firstSuccessName,
                    customMessage: cancelMsg, skipCount: result.skipCount, failCount: result.failCount));
                RefreshBrowserStatusSummary();
                return;
            }
            FileOpExitStatus pasteExitStatus = FileOperationPresentationHelper.NormalizeExitStatus(
                FileOpExitStatus.Success,
                result.successCount,
                validPaths.Count,
                result.skipCount,
                result.failCount);
            var pasteResult = new FileOperationResult("Paste", pasteExitStatus, result.successCount, validPaths.Count, result.firstSuccessName,
                skipCount: result.skipCount, failCount: result.failCount);
            string resultMsg = FileOperationPresentationHelper.GetPasteResultStatusMessage(
                pasteResult,
                isCut,
                result.renamedCount,
                result.firstRenamedName,
                preserveClipboardOnIncomplete: true);
            if (result.moveUndoItems.Count > 0)
            {
                _fileOperationUndoRedoService.RecordBatch(FileOperationUndoRedoOperation.Move, result.moveUndoItems);
                resultMsg = BuildMoveUndoReadyMessage(result.successCount, validPaths.Count);
            }
            else if (result.createdFilesUndoItems.Count > 0)
            {
                _fileOperationUndoRedoService.RecordBatch(FileOperationUndoRedoOperation.CreateFromPaste, result.createdFilesUndoItems);
                resultMsg = BuildCreatedFilesUndoReadyMessage(result.successCount, validPaths.Count);
            }
            HandlePostOperation(new FileOperationResult("Paste", pasteExitStatus, result.successCount, validPaths.Count, result.firstSuccessName,
                customMessage: resultMsg, skipCount: result.skipCount, failCount: result.failCount));
            RefreshBrowserStatusSummary();
        }
        catch (OperationCanceledException)
        {
            HandlePostOperation(new FileOperationResult("Paste", FileOpExitStatus.Canceled, 0, 0, customMessage: "貼り付けを中断しました。"));
            RefreshBrowserStatusSummary();
        }
        catch (Exception ex)
        {
            LogService.Error("貼り付け処理中に致命的なエラーが発生しました", ex);
            HandlePostOperation(new FileOperationResult("Paste", FileOpExitStatus.Error, 0, 0));
            RefreshBrowserStatusSummary();
        }
    }
    private static string BuildCreatedFilesUndoReadyMessage(int successCount, int totalCount)
    {
        return FileOperationPresentationHelper.GetUndoReadyMessage("作成", successCount, totalCount);
    }
    private bool ConfirmBulkCutPasteMove(int itemCount, string firstSourcePath, string destinationDirectory)
    {
        string sourceDirectory = Path.GetDirectoryName(firstSourcePath) ?? firstSourcePath;
        string message =
            $"切り取り済みの {itemCount} 件を現在のフォルダへ移動します。{Environment.NewLine}{Environment.NewLine}" +
            $"移動元:{Environment.NewLine}{sourceDirectory}{Environment.NewLine}{Environment.NewLine}" +
            $"移動先:{Environment.NewLine}{destinationDirectory}{Environment.NewLine}{Environment.NewLine}" +
            "この操作は元の場所からファイルを移動します。実行しますか？" +
            $"{Environment.NewLine}{Environment.NewLine}※ 成功した移動は、このセッション中に限り「元に戻す」できます。";
        return MessageBox.Show(
            this,
            message,
            "複数項目の移動確認",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    }
    private void HandlePostOperation(FileOperationResult result)
    {
        CompleteFileOperationProgressIndicator();
        var totalStopwatch = Stopwatch.StartNew();
        long finalizeMs = 0;
        long clearPreviewMs = 0;
        long reloadMs = 0;
        long refreshMarksMs = 0;
        long clearMarksMs = 0;
        long statusMs = 0;
        var plan = _fileOperationPostOperationCoordinator.CreatePlan(
            result,
            _settings.FileOperations?.ReloadAfterFileOperation ?? true,
            _navigationService.CurrentPath);
        if (plan.ShouldFinalizeBusy)
        {
            var sw = Stopwatch.StartNew();
            FinalizeFileOperation();
            sw.Stop();
            finalizeMs = sw.ElapsedMilliseconds;
        }
        if (plan.ShouldClearPreview)
        {
            var sw = Stopwatch.StartNew();
            ClearPreview();
            sw.Stop();
            clearPreviewMs = sw.ElapsedMilliseconds;
        }
        if (plan.ShouldReloadCurrentDirectory)
        {
            var sw = Stopwatch.StartNew();
            LoadDirectory(_navigationService.CurrentPath, plan.NextFocusTarget);
            sw.Stop();
            reloadMs = sw.ElapsedMilliseconds;
        }
        else if (plan.ShouldRefreshMarks)
        {
            var sw = Stopwatch.StartNew();
            RefreshMarkUi();
            sw.Stop();
            refreshMarksMs = sw.ElapsedMilliseconds;
        }
        if (plan.ShouldClearMarks)
        {
            var sw = Stopwatch.StartNew();
            ClearMarks();
            sw.Stop();
            clearMarksMs = sw.ElapsedMilliseconds;
        }
        var statusStopwatch = Stopwatch.StartNew();
        ShowStatusMessage(plan.StatusMessage);
        statusStopwatch.Stop();
        statusMs = statusStopwatch.ElapsedMilliseconds;
        totalStopwatch.Stop();
        LogService.Info(
            $"[Perf] FileOperationPostOperation operation={result.OperationName} status={result.ExitStatus} " +
            $"total={totalStopwatch.ElapsedMilliseconds}ms finalize={finalizeMs}ms clearPreview={clearPreviewMs}ms " +
            $"reload={reloadMs}ms refreshMarks={refreshMarksMs}ms clearMarks={clearMarksMs}ms status={statusMs}ms " +
            $"reloadApplied={plan.ShouldReloadCurrentDirectory} focusTarget={plan.NextFocusTarget ?? "<none>"}");
    }
    private string? GetCreatedItemFocusTarget(string? fileName)
    {
        if (!(_settings.FileOperations?.SelectCreatedItemAfterCreate ?? true))
        {
            return null;
        }
        return string.IsNullOrWhiteSpace(fileName) ? null : fileName;
    }
    private bool IsCurrentFileOperationStatusVersion(int statusVersion)
    {
        return _isClipboardBusy && statusVersion == _fileOpUiState.StatusVersion;
    }
    private void ShowFileOperationStatusIfCurrent(int statusVersion, string message)
    {
        if (!IsCurrentFileOperationStatusVersion(statusVersion))
        {
            return;
        }
        // busy feedback などの一時優先メッセージが表示されている間は進捗更新をスキップする
        if (DateTime.UtcNow < _statusNoticeHoldUntilUtc)
        {
            return;
        }
        ShowStatusMessage(message);
    }
    private void UpdateFileOperationProgressIndicatorIfCurrent(
        int statusVersion,
        string operationDisplayName,
        int processedCount,
        int totalCount)
    {
        if (!IsCurrentFileOperationStatusVersion(statusVersion))
        {
            return;
        }

        bool isIndeterminate = totalCount <= 0;
        UpdateFileOperationItemProgressState(new FileOperationItemProgressState(
            FileOperationPresentationHelper.ResolveItemProgressKind(operationDisplayName),
            processedCount,
            totalCount,
            isIndeterminate,
            true));
    }
    private void StartFileOperationProgressIndicator(string operationDisplayName, int totalCount)
    {
        UpdateFileOperationItemProgressState(new FileOperationItemProgressState(
            FileOperationPresentationHelper.ResolveItemProgressKind(operationDisplayName),
            0,
            totalCount,
            totalCount <= 0,
            true));
    }
    private void CompleteFileOperationProgressIndicator()
    {
        ClearFileOperationItemProgressState();
    }
    private void ShowFileOperationProgressIfCurrent(
        int statusVersion,
        string operationDisplayName,
        int processedCount,
        int totalCount,
        string currentFileName,
        bool usePasteProgress = false,
        bool isCut = false)
    {
        UpdateFileOperationProgressIndicatorIfCurrent(statusVersion, operationDisplayName, processedCount, totalCount);
        string message = (_fileOpUiState.Cts?.IsCancellationRequested ?? false)
            ? FileOperationPresentationHelper.GetCancelRequestedMessage(_fileOpUiState.ActiveOperationName ?? operationDisplayName)
            : usePasteProgress
                ? FileOperationPresentationHelper.GetPasteProgressMessage(isCut, processedCount, totalCount, currentFileName)
                : FileOperationPresentationHelper.GetOperationProgressMessage(operationDisplayName, processedCount, totalCount, currentFileName);
        ShowFileOperationStatusIfCurrent(statusVersion, message);
    }
    private CancellationToken PrepareFileOperation(string? operationName = null)
    {
        _fileOpUiState.StatusVersion++;
        _isClipboardBusy = true;
        _fileOpUiState.ActiveOperationName = operationName;
        UpdateMenuStripState();
        _fileOpUiState.Cts?.Cancel();
        _fileOpUiState.Cts?.Dispose();
        _fileOpUiState.Cts = new CancellationTokenSource();
        _fileOpUiState.CancelRequestedTimestamp = 0;
        return _fileOpUiState.Cts.Token;
    }
    private void FinalizeFileOperation()
    {
        _fileOpUiState.StatusVersion++;
        CompleteFileOperationProgressIndicator();
        _fileOpUiState.Cts?.Dispose();
        _fileOpUiState.Cts = null;
        _isClipboardBusy = false;
        _fileOpUiState.ActiveOperationName = null;
        UpdateMenuStripState();
        TryProcessPendingCurrentDirectoryRefresh("FinalizeFileOperation");
    }
    private bool TryResolveCopyCollision(
        string sourcePath,
        ref string destPath,
        ref CopyCollisionDecision? applyToAllDecision,
        out CopyCollisionPolicy appliedPolicy,
        out bool shouldSkip,
        out bool shouldCancel)
    {
        appliedPolicy = CopyCollisionPolicy.Cancel;
        shouldSkip = false;
        shouldCancel = false;
        bool sourceIsDir = FileOperationService.IsDirectoryContainerPath(sourcePath);
        bool destIsDir = FileOperationService.IsDirectoryContainerPath(destPath);
        if (sourceIsDir != destIsDir)
        {
            string conflictPath = destPath;
            this.Invoke(() => _fileOperationDialogCoordinator.ShowTypeMismatchConflict(this, conflictPath));
            shouldSkip = true;
            return false;
        }
        var decision = applyToAllDecision;
        if (decision == null)
        {
            string dialogDestPath = destPath;
            string targetName = Path.GetFileName(dialogDestPath);
            decision = (CopyCollisionDecision)this.Invoke(() =>
            {
                ShowStatusMessage(FileOperationPresentationHelper.GetConflictConfirmationMessage("コピー", targetName));
                return _fileOperationDialogCoordinator.ShowCopyCollision(this, sourcePath, dialogDestPath);
            });
            if (decision.ApplyToAll && decision.Policy != CopyCollisionPolicy.Cancel)
            {
                applyToAllDecision = new CopyCollisionDecision
                {
                    Policy = decision.Policy,
                    ApplyToAll = true
                };
            }
        }
        switch (decision.Policy)
        {
            case CopyCollisionPolicy.NewerOnly:
                appliedPolicy = CopyCollisionPolicy.NewerOnly;
                var sourceTime = File.GetLastWriteTimeUtc(sourcePath);
                var destTime = File.GetLastWriteTimeUtc(destPath);
                shouldSkip = sourceTime <= destTime;
                return !shouldSkip;
            case CopyCollisionPolicy.RenameCopy:
                appliedPolicy = CopyCollisionPolicy.RenameCopy;
                destPath = FileOperationService.GetUniquePathStartingAtOne(destPath);
                return true;
            case CopyCollisionPolicy.Overwrite:
                appliedPolicy = CopyCollisionPolicy.Overwrite;
                return true;
            case CopyCollisionPolicy.Skip:
                appliedPolicy = CopyCollisionPolicy.Skip;
                shouldSkip = true;
                return false;
            default:
                shouldCancel = true;
                return false;
        }
    }
    private void ExecuteClipboardImagePaste()
    {
        if (GuardReadOnlyBrowserTab())
        {
            return;
        }
        if (_uiMode != UIMode.Browser)
        {
            ShowStatusMessage("この画面では貼り付けできません");
            return;
        }
        if (_isClipboardBusy)
        {
            ShowStatusMessage(FileOperationPresentationHelper.GetBusyBlockedMessage(
                "貼り付け",
                canCancel: _fileOpUiState.Cts != null,
                isCancelRequested: _fileOpUiState.Cts?.IsCancellationRequested ?? false));
            return;
        }
        if (string.IsNullOrEmpty(_navigationService.CurrentPath))
        {
            return;
        }
        _isClipboardBusy = true;
        try
        {
            if (!ShellClipboardService.TryGetImage(out var image, out string? imageError) || image == null)
            {
                LogBrowserImageImportWarn($"Source=ClipboardImageUnavailable Error={imageError ?? "<none>"}");
                ShowStatusMessage("クリップボードに画像がありません");
                return;
            }
            using (image)
            {
                string savedPath = ClipboardImagePasteService.SavePngToDirectory(image, _navigationService.CurrentPath);
                string fileName = Path.GetFileName(savedPath);
                var createdUndoItems = FileOperationUndoRedoService.CreateCreatedFilesBatch(new[] { savedPath });
                if (createdUndoItems.Count > 0)
                {
                    _fileOperationUndoRedoService.RecordBatch(FileOperationUndoRedoOperation.CreateFromPaste, createdUndoItems);
                }
                LoadDirectory(_navigationService.CurrentPath, GetCreatedItemFocusTarget(fileName));
                LogBrowserImageImportInfo($"Source=ClipboardImage Saved={savedPath}");
                ShowStatusMessage(createdUndoItems.Count > 0
                    ? "画像を PNG として貼り付けました。Ctrl+Z で元に戻せます。"
                    : $"画像を PNG として貼り付けました: {fileName}");
            }
        }
        catch (Exception ex)
        {
            LogService.Error("クリップボード画像の貼り付けに失敗しました", ex);
            ShowStatusMessage($"画像貼り付け失敗: {ex.Message}");
        }
        finally
        {
            _isClipboardBusy = false;
        }
    }
    private void ExecuteClipboardTextPaste()
    {
        if (GuardReadOnlyBrowserTab())
        {
            return;
        }
        if (_uiMode != UIMode.Browser)
        {
            ShowStatusMessage("この画面では貼り付けできません");
            return;
        }
        if (!(_settings.FileOperations?.ClipboardPasteTextAsFileEnabled ?? false))
        {
            ShowStatusMessage("テキスト貼り付けファイル化は設定でOFFです。");
            return;
        }
        if (_isClipboardBusy)
        {
            ShowStatusMessage(FileOperationPresentationHelper.GetBusyBlockedMessage(
                "貼り付け",
                canCancel: _fileOpUiState.Cts != null,
                isCancelRequested: _fileOpUiState.Cts?.IsCancellationRequested ?? false));
            return;
        }
        if (string.IsNullOrEmpty(_navigationService.CurrentPath))
        {
            return;
        }

        _isClipboardBusy = true;
        try
        {
            if (!ShellClipboardService.TryGetText(out string? text, out string? textError))
            {
                ShowStatusMessage("クリップボードにテキストがありません");
                return;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                ShowStatusMessage("空のテキストは貼り付けできません");
                return;
            }

            string targetPath = CreateClipboardTextPasteFilePath(_navigationService.CurrentPath);
            File.WriteAllText(targetPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            string fileName = Path.GetFileName(targetPath);
            var createdUndoItems = FileOperationUndoRedoService.CreateCreatedFilesBatch(new[] { targetPath });
            if (createdUndoItems.Count > 0)
            {
                _fileOperationUndoRedoService.RecordBatch(FileOperationUndoRedoOperation.CreateFromPaste, createdUndoItems);
            }
            LoadDirectory(_navigationService.CurrentPath, GetCreatedItemFocusTarget(fileName));
            ShowStatusMessage(createdUndoItems.Count > 0
                ? "テキストを貼り付けてファイル作成しました。Ctrl+Z で元に戻せます。"
                : $"テキストを貼り付けてファイル作成しました: {fileName}");
        }
        catch (Exception ex)
        {
            LogService.Error("クリップボードテキストの貼り付けに失敗しました", ex);
            ShowStatusMessage($"テキスト貼り付け失敗: {ex.Message}");
        }
        finally
        {
            _isClipboardBusy = false;
        }
    }
    private static string CreateClipboardTextPasteFilePath(string directory)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string baseName = $"clipboard_text_{stamp}";
        string path = Path.Combine(directory, $"{baseName}.txt");
        if (!PathExists(path))
        {
            return path;
        }

        for (int i = 1; i <= 999; i++)
        {
            string numbered = Path.Combine(directory, $"{baseName}_{i:000}.txt");
            if (!PathExists(numbered))
            {
                return numbered;
            }
        }

        return Path.Combine(directory, $"{baseName}_{Guid.NewGuid():N}.txt");
    }
    private bool TryResolvePasteDirectoryMerge(
        string sourcePath,
        string destPath,
        bool isCut,
        ref DirectoryMergeDecision? applyToAllDecision,
        out bool shouldSkip,
        out bool shouldCancel)
    {
        shouldSkip = false;
        shouldCancel = false;
        var guard = FileOperationService.AnalyzeDirectoryPasteMerge(sourcePath, destPath, isCut);
        if (!guard.CanMerge)
        {
            this.Invoke(() => _fileOperationDialogCoordinator.ShowInformationDialog(
                this,
                guard.Message,
                isCut ? "貼り付け(移動)エラー" : "貼り付け(コピー)エラー"));
            shouldSkip = true;
            return false;
        }
        var decision = applyToAllDecision;
        if (decision == null)
        {
            string targetName = Path.GetFileName(destPath);
            decision = (DirectoryMergeDecision)this.Invoke(() =>
            {
                ShowStatusMessage(FileOperationPresentationHelper.GetConflictConfirmationMessage(
                    isCut ? "貼り付け(移動)" : "貼り付け(コピー)",
                    targetName));
                return _fileOperationDialogCoordinator.ShowPasteDirectoryMerge(this, sourcePath, destPath, isCut);
            });
            if (decision.ApplyToAll && decision.Policy != DirectoryMergePolicy.Cancel)
            {
                applyToAllDecision = new DirectoryMergeDecision
                {
                    Policy = decision.Policy,
                    ApplyToAll = true
                };
            }
        }
        switch (decision.Policy)
        {
            case DirectoryMergePolicy.Merge:
                return true;
            case DirectoryMergePolicy.Skip:
                shouldSkip = true;
                return false;
            default:
                shouldCancel = true;
                return false;
        }
    }
    private bool TryResolveCopyDirectoryMerge(
        string sourcePath,
        string destPath,
        ref DirectoryMergeDecision? applyToAllDecision,
        out bool shouldSkip,
        out bool shouldCancel)
    {
        shouldSkip = false;
        shouldCancel = false;
        var decision = applyToAllDecision;
        if (decision == null)
        {
            string targetName = Path.GetFileName(destPath);
            decision = (DirectoryMergeDecision)this.Invoke(() =>
            {
                ShowStatusMessage(FileOperationPresentationHelper.GetConflictConfirmationMessage("コピー", targetName));
                return _fileOperationDialogCoordinator.ShowCopyDirectoryMerge(this, sourcePath, destPath);
            });
            if (decision.ApplyToAll && decision.Policy != DirectoryMergePolicy.Cancel)
            {
                applyToAllDecision = new DirectoryMergeDecision
                {
                    Policy = decision.Policy,
                    ApplyToAll = true
                };
            }
        }
        switch (decision.Policy)
        {
            case DirectoryMergePolicy.Merge:
                return true;
            case DirectoryMergePolicy.Skip:
                shouldSkip = true;
                return false;
            default:
                shouldCancel = true;
            return false;
        }
    }
    private bool TryResolveMoveDirectoryMerge(
        string sourcePath,
        string destPath,
        ref DirectoryMergeDecision? applyToAllDecision,
        out bool shouldSkip,
        out bool shouldCancel)
    {
        shouldSkip = false;
        shouldCancel = false;
        var guard = FileOperationService.AnalyzeDirectoryMoveMergePractical(sourcePath, destPath);
        if (!guard.CanMerge)
        {
            this.Invoke(() => _fileOperationDialogCoordinator.ShowInformationDialog(this, guard.Message, "移動エラー"));
            shouldSkip = true;
            return false;
        }
        var decision = applyToAllDecision;
        if (decision == null)
        {
            string targetName = Path.GetFileName(destPath);
            decision = (DirectoryMergeDecision)this.Invoke(() =>
            {
                ShowStatusMessage(FileOperationPresentationHelper.GetConflictConfirmationMessage("移動", targetName));
                return _fileOperationDialogCoordinator.ShowMoveDirectoryMerge(this, sourcePath, destPath);
            });
            if (decision.ApplyToAll && decision.Policy != DirectoryMergePolicy.Cancel)
            {
                applyToAllDecision = new DirectoryMergeDecision
                {
                    Policy = decision.Policy,
                    ApplyToAll = true
                };
            }
        }
        switch (decision.Policy)
        {
            case DirectoryMergePolicy.Merge:
                return true;
            case DirectoryMergePolicy.Skip:
                shouldSkip = true;
                return false;
            default:
                shouldCancel = true;
                return false;
        }
    }
    private void CopyDirectoryIntoExisting(
        string sourceDir,
        string destinationDir,
        ref CopyCollisionDecision? fileApplyToAllDecision,
        CancellationToken token,
        ISet<string>? excludedReparsePaths = null)
    {
        foreach (var entry in FileOperationService.BuildDirectoryCopyPlan(sourceDir, destinationDir))
        {
            token.ThrowIfCancellationRequested();
            if (excludedReparsePaths?.Contains(entry.SourcePath) == true)
            {
                continue;
            }
            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(entry.DestinationPath);
                continue;
            }
            string destinationPath = entry.DestinationPath;
            bool destExists = PathExists(destinationPath);
            if (destExists)
            {
                if (!TryResolveCopyCollision(entry.SourcePath, ref destinationPath, ref fileApplyToAllDecision, out _, out bool shouldSkip, out bool shouldCancel))
                {
                    if (shouldCancel)
                    {
                        throw new OperationCanceledException(token);
                    }
                    if (shouldSkip)
                    {
                        continue;
                    }
                }
            }
            FileOperationService.Copy(entry.SourcePath, destinationPath, excludedReparsePaths);
        }
    }
    private void PasteCopyDirectoryIntoExisting(
        string sourceDir,
        string destinationDir,
        ref CopyCollisionDecision? fileApplyToAllDecision,
        out bool shouldCancel,
        ISet<string>? excludedReparsePaths = null)
    {
        PasteCopyDirectoryIntoExisting(
            sourceDir,
            destinationDir,
            ref fileApplyToAllDecision,
            out shouldCancel,
            null,
            excludedReparsePaths);
    }

    private void PasteCopyDirectoryIntoExisting(
        string sourceDir,
        string destinationDir,
        ref CopyCollisionDecision? fileApplyToAllDecision,
        out bool shouldCancel,
        DirectoryMergeExecutionState? executionState,
        ISet<string>? excludedReparsePaths = null)
    {
        shouldCancel = false;
        foreach (var entry in FileOperationService.BuildDirectoryCopyPlan(sourceDir, destinationDir))
        {
            if (excludedReparsePaths?.Contains(entry.SourcePath) == true)
            {
                continue;
            }
            if (entry.IsDirectory)
            {
                try
                {
                    bool destinationExisted = Directory.Exists(entry.DestinationPath);
                    Directory.CreateDirectory(entry.DestinationPath);
                    if (!destinationExisted && executionState != null)
                    {
                        executionState.SuccessCount++;
                    }
                }
                catch (Exception ex) when (executionState != null)
                {
                    executionState.FailCount++;
                    LogService.Error($"フォルダ統合失敗: {entry.DestinationPath}", ex);
                }
                continue;
            }
            string destinationPath = entry.DestinationPath;
            bool destExists = PathExists(destinationPath);
            if (destExists)
            {
                var collisionResolution = _fileOperationDialogCoordinator.ResolvePasteCollision(
                    this,
                    entry.SourcePath,
                    destinationPath,
                    allowRename: true,
                    isCut: false,
                    ref fileApplyToAllDecision);
                if (collisionResolution.ShouldCancel)
                {
                    shouldCancel = true;
                    if (executionState != null)
                    {
                        executionState.Canceled = true;
                    }
                    return;
                }
                if (collisionResolution.ShouldSkip)
                {
                    if (executionState != null)
                    {
                        executionState.SkipCount++;
                    }
                    continue;
                }
                destinationPath = collisionResolution.DestinationPath;
            }
            try
            {
                FileOperationService.Copy(entry.SourcePath, destinationPath, excludedReparsePaths);
                if (executionState != null)
                {
                    executionState.SuccessCount++;
                }
            }
            catch (Exception ex) when (executionState != null)
            {
                executionState.FailCount++;
                LogService.Error($"フォルダ統合失敗: {Path.GetFileName(entry.SourcePath)}", ex);
            }
        }
    }
    private void PasteMoveDirectoryIntoExisting(
        string sourceDir,
        string destinationDir,
        ref CopyCollisionDecision? fileApplyToAllDecision,
        out bool shouldCancel,
        out int skipCount,
        out int failCount,
        ISet<string>? excludedReparsePaths = null,
        ISet<string>? successfulPreparedReparsePaths = null)
    {
        PasteMoveDirectoryIntoExisting(
            sourceDir,
            destinationDir,
            ref fileApplyToAllDecision,
            out shouldCancel,
            out skipCount,
            out failCount,
            null,
            excludedReparsePaths,
            successfulPreparedReparsePaths);
    }

    private void PasteMoveDirectoryIntoExisting(
        string sourceDir,
        string destinationDir,
        ref CopyCollisionDecision? fileApplyToAllDecision,
        out bool shouldCancel,
        out int skipCount,
        out int failCount,
        DirectoryMergeExecutionState? executionState,
        ISet<string>? excludedReparsePaths = null,
        ISet<string>? successfulPreparedReparsePaths = null)
    {
        MoveDirectoryIntoExistingWithCollisionResolution(
            sourceDir,
            destinationDir,
            ref fileApplyToAllDecision,
            "貼り付け(移動)",
            out shouldCancel,
            out skipCount,
            out failCount,
            excludedReparsePaths,
            successfulPreparedReparsePaths,
            executionState);
    }
    private void DirectMoveDirectoryIntoExisting(
        string sourceDir,
        string destinationDir,
        ref CopyCollisionDecision? fileApplyToAllDecision,
        out bool shouldCancel,
        out int skipCount,
        out int failCount,
        ISet<string>? excludedReparsePaths = null,
        ISet<string>? successfulPreparedReparsePaths = null,
        DirectoryMergeExecutionState? executionState = null)
    {
        MoveDirectoryIntoExistingWithCollisionResolution(
            sourceDir,
            destinationDir,
            ref fileApplyToAllDecision,
            "移動",
            out shouldCancel,
            out skipCount,
            out failCount,
            excludedReparsePaths,
            successfulPreparedReparsePaths,
            executionState);
    }
    private void MoveDirectoryIntoExistingWithCollisionResolution(
        string sourceDir,
        string destinationDir,
        ref CopyCollisionDecision? fileApplyToAllDecision,
        string operationLogLabel,
        out bool shouldCancel,
        out int skipCount,
        out int failCount,
        ISet<string>? excludedReparsePaths = null,
        ISet<string>? successfulPreparedReparsePaths = null,
        DirectoryMergeExecutionState? executionState = null)
    {
        shouldCancel = false;
        skipCount = 0;
        failCount = 0;
        IReadOnlyList<DirectoryCopyPlanEntry> copyPlan = FileOperationService.BuildDirectoryCopyPlan(sourceDir, destinationDir);
        bool suppressItemSuccessLogs = copyPlan.Count > 100;
        foreach (var entry in copyPlan)
        {
            if (excludedReparsePaths?.Contains(entry.SourcePath) == true)
            {
                continue;
            }
            if (entry.IsDirectory)
            {
                try
                {
                    bool destinationExisted = Directory.Exists(entry.DestinationPath);
                    Directory.CreateDirectory(entry.DestinationPath);
                    if (!destinationExisted && executionState != null)
                    {
                        executionState.SuccessCount++;
                    }
                }
                catch (Exception ex) when (executionState != null)
                {
                    executionState.FailCount++;
                    LogService.Error($"{operationLogLabel}フォルダ統合失敗: {entry.DestinationPath}", ex);
                }
                continue;
            }
            string destinationPath = entry.DestinationPath;
            bool overwriteMove = false;
            bool destExists = PathExists(destinationPath);
            if (destExists)
            {
                var collisionResolution = PasteCollisionResolver.Resolve(
                    this,
                    entry.SourcePath,
                    destinationPath,
                    allowRename: false,
                    isCut: true,
                    ref fileApplyToAllDecision);
                if (collisionResolution.ShouldCancel)
                {
                    shouldCancel = true;
                    if (executionState != null)
                    {
                        executionState.Canceled = true;
                    }
                    return;
                }
                if (collisionResolution.ShouldSkip)
                {
                    skipCount++;
                    if (executionState != null)
                    {
                        executionState.SkipCount++;
                    }
                    continue;
                }
                destinationPath = collisionResolution.DestinationPath;
                overwriteMove = collisionResolution.OverwriteExisting;
            }
            try
            {
                FileOperationService.Move(entry.SourcePath, destinationPath, overwriteMove, suppressLogging: suppressItemSuccessLogs);
                if (executionState != null)
                {
                    executionState.SuccessCount++;
                }
            }
            catch (Exception ex)
            {
                LogService.Error($"{operationLogLabel}フォルダ統合失敗: {Path.GetFileName(entry.SourcePath)}", ex);
                failCount++;
                if (executionState != null)
                {
                    executionState.FailCount++;
                }
            }
        }
        failCount += FileOperationService.DeleteSuccessfulPreparedReparsePointsUnderSource(
            sourceDir,
            excludedReparsePaths,
            successfulPreparedReparsePaths,
            operationLogLabel);
        FileOperationService.DeleteEmptyDirectoriesBottomUp(sourceDir);
    }
    private bool TryExtractSevenZipProgress(string line, out string percent)
    {
        percent = string.Empty;
        var match = System.Text.RegularExpressions.Regex.Match(line, @"(\d+)%");
        if (match.Success)
        {
            percent = match.Groups[1].Value;
            return true;
        }
        return false;
    }
    /// <summary>
    /// Phase 5-viewer-ux1: Viewer の現在状態（エンコーディング・折り返し）をまとめた statusLabel 用の文字列を生成する。
    /// </summary>
}
