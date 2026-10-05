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
using MidFD.Runtime;
using MidFD.Services;
using MidFD.Services.TrashManifestStore;

namespace MidFD;

public partial class MainForm
{
    private static bool PathExists(string path)
        => File.Exists(path) || Directory.Exists(path);

    private bool CanRestoreBrowserFocusAfterFileOperation()
        => _viewerApplicationCoordinator.Mode == ViewerApplicationMode.Browser && !IsDisposed && !Disposing && fileListView.IsHandleCreated;

    FileOperationSelectionContext IFileOperationUiPort.ReadSelectionContext()
    {
        ListViewItem? currentItem = GetCurrentBrowserItem();
        return new FileOperationSelectionContext(
            currentItem?.Tag as string,
            currentItem == null || currentItem.Text == "..",
            currentItem?.Text);
    }

    FileOperationMultiMarkSelectionAction IFileOperationUiPort.ChooseMultiMarkSelection(
        string operationName,
        string currentName,
        int markedCount)
        => ShowMultiMarkGuardActionDialog(operationName, currentName, markedCount) switch
        {
            MultiMarkGuardAction.CurrentOnly => FileOperationMultiMarkSelectionAction.CurrentOnly,
            MultiMarkGuardAction.MarkedAll => FileOperationMultiMarkSelectionAction.MarkedAll,
            _ => FileOperationMultiMarkSelectionAction.Cancel
        };

    BrowserPostOperationReloadContext IFileOperationUiPort.CapturePostOperationReloadContext()
        => new(
            CreateDirectoryLoadOptions(),
            _browserApplicationCoordinator.ColumnCount,
            CaptureBrowserRefreshShellState());

    string? IFileOperationUiPort.RequestDestinationDirectory(FileOperationDestinationDialogOptions options)
        => _fileOperationDialogCoordinator.RequestDestinationDirectory(
            this,
            options.OperationName,
            options.CurrentPath,
            options.SummaryText,
            options.WarningText,
            options.DirectoryHistory);

    void IFileOperationUiPort.ShowDestinationPathError(string message)
        => MessageBox.Show(this, message, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Information);

    bool IFileOperationUiPort.ConfirmCreateDirectory(string destinationDirectory)
        => _fileOperationDialogCoordinator.ConfirmCreateDirectory(this, destinationDirectory);

    bool IFileOperationUiPort.ConfirmDelete(SelectionResult selection, bool permanentDelete)
        => _fileOperationDialogCoordinator.ConfirmDelete(this, selection, permanentDelete, _browserApplicationCoordinator.CurrentPath, ShowStatusMessage);

    bool IFileOperationUiPort.ConfirmBulkPasteMove(IReadOnlyList<string> sourcePaths, string destinationDirectory)
        => MessageBox.Show(
            this,
            $"{sourcePaths.Count} 件を次のフォルダへ移動します。\n{destinationDirectory}\n\n実行しますか？",
            "切り取り貼り付けの確認",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    LinkOperationDecision IFileOperationUiPort.ChooseLinkOperation(LinkOperationPlan plan)
        => LinkOperationDecisionDialog.Show(this, plan);

    bool IFileOperationUiPort.ConfirmEmptyManagedTrash()
        => MessageBox.Show(
            "MidFD管理ゴミ箱を空にします。この操作後、MidFDの削除Undo/Redoはできなくなります。よろしいですか？",
            "MidFD管理ゴミ箱を空にする",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    FileOperationRenameEntryResult IFileOperationUiPort.RequestRenameEntry(IReadOnlyList<string> sourcePaths)
    {
        RenameEntryDialogResult result = _renameDialogCoordinator.ShowEntryDialog(this, sourcePaths);
        return new FileOperationRenameEntryResult(
            result.Confirmed,
            result.Mode switch
            {
                RenameEntryMode.SingleStep => FileOperationRenameEntryMode.SingleStep,
                RenameEntryMode.Bulk => FileOperationRenameEntryMode.Bulk,
                _ => FileOperationRenameEntryMode.Cancel
            },
            result.SingleStepInitialName);
    }

    FileOperationRenameSingleResult IFileOperationUiPort.RequestRenameSingle(
        string sourcePath,
        string? initialValue,
        bool skipInitialPrompt,
        bool showValidationMessage)
    {
        RenameDialogCoordinator.SingleRenameDialogResult result = _renameDialogCoordinator.ShowSingleRenameDialog(
            this,
            sourcePath,
            initialValue,
            skipInitialPrompt,
            showValidationMessage);
        return new FileOperationRenameSingleResult(result.WasCanceled, result.WillRename, result.PreviewItem);
    }

    FileOperationRenameBatchResult IFileOperationUiPort.RequestRenameBatch(
        IReadOnlyList<string> sourcePaths,
        string initialTemplate,
        bool rememberTemplate)
    {
        RenameDialogResult result = _renameDialogCoordinator.ShowBatchDialog(
            this,
            sourcePaths,
            initialTemplate,
            rememberTemplate);
        return new FileOperationRenameBatchResult(
            result.Confirmed,
            result.Preview.Items,
            result.RememberTemplate,
            result.LastTemplateCandidate);
    }

    FileOperationPackDialogResult? IFileOperationUiPort.RequestPackDialog(FileOperationPackDialogOptions options)
    {
        FileOperationPackExistingArchiveAction existingArchiveAction = FileOperationPackExistingArchiveAction.Add;
        PackRequest? request = PackDialog.Show(
            this,
            options.InitialDirectory,
            options.DefaultArchiveName,
            options.TargetSummary,
            options.CanPackEachFolderIndividually,
            options.DefaultPackEachFolderIndividually,
            options.AvailableFormats,
            options.HintText,
            (owner, archivePath) =>
            {
                PackExistingArchiveAction action = ShowPackExistingArchiveActionDialog(owner, archivePath);
                existingArchiveAction = action switch
                {
                    PackExistingArchiveAction.Overwrite => FileOperationPackExistingArchiveAction.Overwrite,
                    _ => FileOperationPackExistingArchiveAction.Add
                };
                return action;
            });
        return request == null
            ? null
            : new FileOperationPackDialogResult(
                request.OutputArchivePath,
                request.Format,
                request.CompressionLevel,
                request.SplitSize,
                request.PackEachFolderIndividually,
                existingArchiveAction);
    }

    FileOperationPackIndividualTargetAnswer IFileOperationUiPort.ChoosePackIndividualTargets(
        int folderCount,
        int fileCount)
    {
        DialogResult result = MessageBox.Show(
            this,
            "フォルダとファイルが混在しています。\nファイルも個別に圧縮しますか？\n\n「はい」：ファイルも個別に圧縮します\n「いいえ」：フォルダのみを個別に圧縮します（ファイルは除外）",
            "個別圧縮の確認",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question);
        return result switch
        {
            DialogResult.Yes => FileOperationPackIndividualTargetAnswer.IncludeFiles,
            DialogResult.No => FileOperationPackIndividualTargetAnswer.FoldersOnly,
            _ => FileOperationPackIndividualTargetAnswer.Cancel
        };
    }

    FileOperationPackExistingArchiveAnswer IFileOperationUiPort.ChoosePackExistingArchive(string archivePath)
        => ShowPackExistingArchiveActionDialog(this, archivePath) switch
        {
            PackExistingArchiveAction.Overwrite => FileOperationPackExistingArchiveAnswer.Overwrite,
            PackExistingArchiveAction.Add => FileOperationPackExistingArchiveAnswer.Add,
            _ => FileOperationPackExistingArchiveAnswer.Cancel
        };

    ArchiveExtractDestinationOptions? IFileOperationUiPort.RequestUnpackDestination(
        string currentPath,
        string archiveDisplayName)
    {
        return ArchiveExtractDestinationDialog.Show(
            this,
            currentPath,
            archiveDisplayName);
    }

    string? IFileOperationUiPort.RequestNewItemName(bool directory, string? fileExtension)
    {
        string prompt = directory || string.IsNullOrWhiteSpace(fileExtension)
            ? directory ? "作成するフォルダ名を入力してください:" : "作成するファイル名を入力してください:"
            : $"作成するファイル名を入力してください:\n拡張子 {fileExtension} は自動で付加されます。";
        string title = directory
            ? "フォルダ作成 (K)"
            : string.IsNullOrWhiteSpace(fileExtension) ? "新規ファイル作成 (N)" : $"新規ファイル作成 ({fileExtension})";
        return SimpleInputDialog.Show(prompt, title);
    }

    FileOperationAttributeDialogResult? IFileOperationUiPort.RequestAttributeChangeDialog(
        FileOperationAttributeDialogOptions options)
    {
        AttributeDialogResult? result = AttributeDialog.Show(new AttributeDialogRequest(
            options.TargetLabel,
            options.ReadOnlyState switch
            {
                FileOperationAttributeAggregateState.AllSet => AttributeAggregateState.AllSet,
                FileOperationAttributeAggregateState.Mixed => AttributeAggregateState.Mixed,
                _ => AttributeAggregateState.AllClear
            },
            options.HiddenState switch
            {
                FileOperationAttributeAggregateState.AllSet => AttributeAggregateState.AllSet,
                FileOperationAttributeAggregateState.Mixed => AttributeAggregateState.Mixed,
                _ => AttributeAggregateState.AllClear
            },
            options.SystemState switch
            {
                FileOperationAttributeAggregateState.AllSet => AttributeAggregateState.AllSet,
                FileOperationAttributeAggregateState.Mixed => AttributeAggregateState.Mixed,
                _ => AttributeAggregateState.AllClear
            },
            options.ArchiveState switch
            {
                FileOperationAttributeAggregateState.AllSet => AttributeAggregateState.AllSet,
                FileOperationAttributeAggregateState.Mixed => AttributeAggregateState.Mixed,
                _ => AttributeAggregateState.AllClear
            },
            options.InitialLastWriteTime,
            options.InitialCreationTime,
            options.InitialLastAccessTime));
        return result is null
            ? null
            : new FileOperationAttributeDialogResult(
                MapAttributeChange(result.ReadOnlyAction),
                MapAttributeChange(result.HiddenAction),
                MapAttributeChange(result.SystemAction),
                MapAttributeChange(result.ArchiveAction),
                result.ChangeLastWriteTime,
                result.LastWriteTime,
                result.ChangeCreationTime,
                result.CreationTime,
                result.ChangeLastAccessTime,
                result.LastAccessTime,
                result.IncludeSubdirectories);
    }

    private static FileOperationAttributeChangeAction MapAttributeChange(AttributeChangeAction action)
        => action switch
        {
            AttributeChangeAction.Set => FileOperationAttributeChangeAction.Set,
            AttributeChangeAction.Clear => FileOperationAttributeChangeAction.Clear,
            _ => FileOperationAttributeChangeAction.Preserve
        };

    FileOperationCollisionAnswer IFileOperationUiPort.ChooseCollision(
        string sourcePath,
        string destinationPath,
        bool allowRename,
        bool isMove,
        ref CopyCollisionDecision? applyToAllDecision)
    {
        if (_integrationSeam?.DialogOverride is { } dialogOverride &&
            dialogOverride.TryChooseCollision(
                sourcePath,
                destinationPath,
                allowRename,
                isMove,
                out FileOperationCollisionAnswer overrideAnswer))
        {
            return overrideAnswer;
        }

        CopyCollisionDecision decision;
        if (applyToAllDecision != null)
        {
            decision = applyToAllDecision;
        }
        else
        {
            decision = _fileOperationDialogCoordinator.ShowCopyCollision(this, sourcePath, destinationPath);
            if (decision.ApplyToAll && decision.Policy != CopyCollisionPolicy.Cancel)
            {
                applyToAllDecision = new CopyCollisionDecision
                {
                    Policy = decision.Policy,
                    ApplyToAll = true
                };
            }
        }

        return decision.Policy switch
        {
            CopyCollisionPolicy.Skip => FileOperationCollisionAnswer.Skip,
            CopyCollisionPolicy.Overwrite => FileOperationCollisionAnswer.Overwrite,
            CopyCollisionPolicy.RenameCopy when allowRename => FileOperationCollisionAnswer.RenameCopy,
            CopyCollisionPolicy.NewerOnly => FileOperationCollisionAnswer.NewerOnly,
            _ => FileOperationCollisionAnswer.Cancel
        };
    }

    FileOperationDirectoryMergeAnswer IFileOperationUiPort.ChooseDirectoryMerge(
        string sourcePath,
        string destinationPath,
        bool isMove,
        ref DirectoryMergeDecision? applyToAllDecision)
    {
        if (_integrationSeam?.DialogOverride is { } dialogOverride &&
            dialogOverride.TryChooseDirectoryMerge(
                sourcePath,
                destinationPath,
                isMove,
                out FileOperationDirectoryMergeAnswer overrideAnswer))
        {
            return overrideAnswer;
        }

        DirectoryMergeDecision decision;
        if (applyToAllDecision != null)
        {
            decision = applyToAllDecision;
        }
        else
        {
            decision = isMove
                ? _fileOperationDialogCoordinator.ShowMoveDirectoryMerge(this, sourcePath, destinationPath)
                : _fileOperationDialogCoordinator.ShowCopyDirectoryMerge(this, sourcePath, destinationPath);
            if (decision.ApplyToAll && decision.Policy != DirectoryMergePolicy.Cancel)
            {
                applyToAllDecision = new DirectoryMergeDecision
                {
                    Policy = decision.Policy,
                    ApplyToAll = true
                };
            }
        }

        return decision.Policy switch
        {
            DirectoryMergePolicy.Merge => FileOperationDirectoryMergeAnswer.Merge,
            DirectoryMergePolicy.Skip => FileOperationDirectoryMergeAnswer.Skip,
            _ => FileOperationDirectoryMergeAnswer.Cancel
        };
    }

    FileOperationPasteCollisionAnswer IFileOperationUiPort.ChoosePasteCollision(
        string sourcePath,
        string destinationPath,
        bool allowRename,
        bool isCut,
        ref CopyCollisionDecision? applyToAllDecision)
    {
        PasteCollisionResolution resolution = _fileOperationDialogCoordinator.ResolvePasteCollision(
            this, sourcePath, destinationPath, allowRename, isCut, ref applyToAllDecision);

        if (resolution.ShouldCancel) return new FileOperationPasteCollisionAnswer(FileOperationCollisionAnswer.Cancel, false);
        if (resolution.ShouldSkip) return new FileOperationPasteCollisionAnswer(FileOperationCollisionAnswer.Skip, false);
        if (resolution.UsedRenameCopy) return new FileOperationPasteCollisionAnswer(FileOperationCollisionAnswer.RenameCopy, false);
        if (resolution.OverwriteExisting) return new FileOperationPasteCollisionAnswer(FileOperationCollisionAnswer.Overwrite, false);
        // NewerOnly は PasteCollisionDialog の PasteCollisionAction.NewerOnly に対応
        return new FileOperationPasteCollisionAnswer(FileOperationCollisionAnswer.NewerOnly, false);
    }

    FileOperationPasteDirectoryMergeAnswer IFileOperationUiPort.ChoosePasteDirectoryMerge(
        string sourcePath,
        string destinationPath,
        bool isCut,
        ref DirectoryMergeDecision? applyToAllDecision)
    {
        DirectoryMergeDecision decision = _fileOperationDialogCoordinator.ShowPasteDirectoryMerge(
            this, sourcePath, destinationPath, isCut);

        if (decision.ApplyToAll)
        {
            applyToAllDecision = decision;
        }

        return decision.Policy switch
        {
            DirectoryMergePolicy.Merge => new FileOperationPasteDirectoryMergeAnswer(FileOperationDirectoryMergeAnswer.Merge, decision.ApplyToAll),
            DirectoryMergePolicy.Skip => new FileOperationPasteDirectoryMergeAnswer(FileOperationDirectoryMergeAnswer.Skip, decision.ApplyToAll),
            _ => new FileOperationPasteDirectoryMergeAnswer(FileOperationDirectoryMergeAnswer.Cancel, false)
        };
    }

    FileOperationSameDirectoryCopyAnswer IFileOperationUiPort.ChooseSameDirectoryCopy(
        string sourcePath,
        string suggestedDestinationPath,
        bool showApplyToAll)
        => _fileOperationDialogCoordinator.ConfirmPasteSameDirectory(
            this,
            Path.GetFileName(sourcePath),
            Path.GetFileName(suggestedDestinationPath),
            showApplyToAll) switch
        {
            PasteSameDirectoryConfirmAction.Yes => FileOperationSameDirectoryCopyAnswer.CopyWithUniqueName,
            PasteSameDirectoryConfirmAction.All => FileOperationSameDirectoryCopyAnswer.CopyWithUniqueNameForAll,
            PasteSameDirectoryConfirmAction.No => FileOperationSameDirectoryCopyAnswer.Skip,
            _ => FileOperationSameDirectoryCopyAnswer.Cancel
        };

    FileOperationClipboardTransfer? IFileOperationUiPort.ReadClipboardFileTransfer()
    {
        return ShellClipboardService.TryGetFileDrop(out List<string> paths, out bool isCut)
            ? new FileOperationClipboardTransfer(paths, isCut)
            : null;
    }

    void IFileOperationUiPort.SetClipboardFileTransfer(IReadOnlyList<string> paths, bool isCut)
        => ShellClipboardService.SetFileDrop(paths, isCut);

    bool IFileOperationUiPort.TryGetClipboardImage(out System.Drawing.Image? image)
        => ShellClipboardService.TryGetImage(out image, out _);

    bool IFileOperationUiPort.TryGetClipboardText(out string? text)
        => ShellClipboardService.TryGetText(out text, out _);

    ClipboardPasteChoice IFileOperationUiPort.ChooseClipboardPasteMode()
        => _fileOperationDialogCoordinator.ChooseClipboardPasteMode(this);

    DeleteCancelResolution IFileOperationUiPort.ShowDeleteCancelResolution(int successCount, int pendingCount, int failedCount)
        => _fileOperationDialogCoordinator.ShowDeleteCancelResolution(this, successCount, pendingCount, failedCount);

    void IFileOperationUiPort.ShowTypeMismatchConflict(string conflictPath)
        => _fileOperationDialogCoordinator.ShowTypeMismatchConflict(this, conflictPath);

    void IFileOperationUiPort.ShowStatus(string message)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        ShowStatusMessage(message);
    }

    void IFileOperationUiPort.ShowError(string operationName, string targetName, string detail)
        => InvokeFileOperationUi(() =>
        {
            _integrationSeam?.Observer?.OnOperationError(operationName, targetName, detail);
            if (_integrationSeam?.DialogOverride?.TryHandleOperationError(operationName, targetName, detail) == true)
            {
                return;
            }

            _fileOperationDialogCoordinator.ShowOperationError(this, operationName, targetName, detail);
        });

    void IFileOperationUiPort.ShowUnexpectedError(string operationName, Exception exception)
        => InvokeFileOperationUi(() =>
        {
            _integrationSeam?.Observer?.OnUnexpectedOperationError(operationName, exception);
            if (_integrationSeam?.DialogOverride?.TryHandleUnexpectedOperationError(operationName, exception) == true)
            {
                return;
            }

            _fileOperationDialogCoordinator.ShowUnexpectedOperationError(this, operationName, exception);
        });

    void IFileOperationUiPort.ShowHashResult(string targetSummary, SevenZipHashAlgorithm algorithm, string output)
        => InvokeFileOperationUi(() =>
        {
            using var dialog = new Dialogs.HashResultDialog(targetSummary, algorithm, output);
            dialog.ShowDialog(this);
        });

    void IFileOperationUiPort.BeginOperation(string operationName, int totalCount)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        ShowStatusMessage(FileOperationPresentationHelper.GetOperationStartingMessage(operationName, totalCount));
        StartFileOperationProgressIndicator(operationName, totalCount);
        _integrationSeam?.Observer?.OnOperationStarted(operationName, totalCount);
    }

    void IFileOperationUiPort.ReportProgress(FileOperationProgress progress)
        => InvokeFileOperationUi(() =>
        {
            _integrationSeam?.Observer?.OnProgress(progress);
            UpdateFileOperationProgressIndicatorIfCurrent(
                _fileOperationApplicationCoordinator.StatusVersion,
                _fileOperationItemProgressState?.OperationKind,
                _fileOperationApplicationCoordinator.ActiveOperationName ?? "FileOperation",
                progress.ProcessedCount,
                progress.TotalCount);
        });

    void IFileOperationUiPort.CompleteOperation(string message)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        CompleteFileOperationProgressIndicator();
        _integrationSeam?.Observer?.OnOperationCompleted(message);
    }

    void IFileOperationUiPort.ClearOperationProgress()
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        CompleteFileOperationProgressIndicator();
    }

    void IFileOperationUiPort.ApplyPostOperation(FileOperationPostOperationApplicationResult result)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        FileOperationPostOperationCoordinator.PostOperationPlan plan = result.Plan;
        if (plan.ShouldClearPreview)
        {
            ClearPreview();
        }
        if (plan.ShouldReloadCurrentDirectory)
        {
            if (result.BrowserReload is { } reload)
            {
                if (reload.Load is { Succeeded: true } load)
                {
                    ApplyDirectoryLoadUi(load);
                    ApplyDirectoryPostLoadEffects(reload.PostLoadEffects);
                }
                else if (reload.Error != null)
                {
                    NotifyDirectoryLoadFailure(reload.Error);
                }
            }
        }
        else if (plan.ShouldRefreshMarks || plan.ShouldClearMarks)
        {
            RefreshMarkUi();
        }
        if (!string.IsNullOrWhiteSpace(plan.StatusMessage))
        {
            ShowStatusMessage(plan.StatusMessage);
        }

        _integrationSeam?.Observer?.OnPostOperationApplied();
    }

    void IFileOperationUiPort.RefreshOperationUi()
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        UpdateMenuStripState();
        RefreshBrowserStatusSummary();
        UpdateFunctionBar();
        _integrationSeam?.Observer?.OnOperationUiRefreshed();
    }

    private void InvokeFileOperationUi(Action action)
    {
        if (IsDisposed || Disposing || !IsHandleCreated)
        {
            return;
        }
        if (InvokeRequired)
        {
            Invoke(action);
            return;
        }
        action();
    }

    private string? GetCreatedItemFocusTarget(string? fileName)
    {
        if (!(_settingsCoordinator.Value.FileOperations?.SelectCreatedItemAfterCreate ?? true))
        {
            return null;
        }
        return string.IsNullOrWhiteSpace(fileName) ? null : fileName;
    }
    private bool IsCurrentFileOperationStatusVersion(int statusVersion)
    {
        return _fileOperationApplicationCoordinator.IsClipboardBusy && statusVersion == _fileOperationApplicationCoordinator.StatusVersion;
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
        FileOperationItemProgressKind? operationKind,
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
            operationKind ?? FileOperationPresentationHelper.ResolveItemProgressKind(operationDisplayName),
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
        UpdateFileOperationProgressIndicatorIfCurrent(statusVersion, null, operationDisplayName, processedCount, totalCount);
        string message = (_fileOperationApplicationCoordinator.CancellationTokenSource?.IsCancellationRequested ?? false)
            ? FileOperationPresentationHelper.GetCancelRequestedMessage(_fileOperationApplicationCoordinator.ActiveOperationName ?? operationDisplayName)
            : usePasteProgress
                ? FileOperationPresentationHelper.GetPasteProgressMessage(isCut, processedCount, totalCount, currentFileName)
                : FileOperationPresentationHelper.GetOperationProgressMessage(operationDisplayName, processedCount, totalCount, currentFileName);
        ShowFileOperationStatusIfCurrent(statusVersion, message);
    }
    /// <summary>
    /// Phase 5-viewer-ux1: Viewer の現在状態（エンコーディング・折り返し）をまとめた statusLabel 用の文字列を生成する。
    /// </summary>
}
