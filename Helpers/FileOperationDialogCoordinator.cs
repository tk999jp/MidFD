using MidFD.Dialogs;
using MidFD.Models;
using MidFD.Services;

namespace MidFD.Helpers;

/// <summary>
/// file operation の dialog / confirm / progress 接続だけを扱う。
/// 実行可否の入口判定や service 実行本体は持たず、MainForm から UI 接続責務だけを薄く分離する。
/// </summary>
public sealed class FileOperationDialogCoordinator
{
    public bool TrySelectDestinationDirectory(
        IWin32Window owner,
        NavigationService navigationService,
        string prompt,
        string title,
        string operationDisplayName,
        string canceledMessage,
        Action<string> showStatusMessage,
        string? summaryText,
        string? warningText,
        IReadOnlyList<string>? directoryHistory,
        out string normalizedDestinationDirectory,
        out bool needsCreateDirectory)
    {
        normalizedDestinationDirectory = string.Empty;
        needsCreateDirectory = false;

        string? input;
        if (string.Equals(operationDisplayName, "移動", StringComparison.Ordinal) && directoryHistory != null)
        {
            input = MoveDestinationDialog.Show(
                prompt,
                title,
                navigationService.CurrentPath,
                directoryHistory,
                summaryText,
                warningText);
        }
        else
        {
            input = SimpleInputDialog.ShowNullable(
                prompt,
                title,
                navigationService.CurrentPath,
                new SimpleInputDialog.DisplayOptions(summaryText, warningText, EnableDirectoryCompletion: true));
        }
        if (string.IsNullOrWhiteSpace(input))
        {
            showStatusMessage(canceledMessage);
            return false;
        }

        normalizedDestinationDirectory = navigationService.NormalizeDestinationDirectory(input);
        string? validationError = FileOperationPresentationHelper.GetDestinationPathErrorMessage(
            input,
            navigationService.CurrentPath,
            normalizedDestinationDirectory,
            operationDisplayName);

        if (!string.IsNullOrEmpty(validationError))
        {
            MessageBox.Show(validationError, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Information);
            normalizedDestinationDirectory = string.Empty;
            return false;
        }

        needsCreateDirectory = !Directory.Exists(normalizedDestinationDirectory);
        return true;
    }

    public bool EnsureDestinationDirectory(
        IWin32Window owner,
        string destinationDirectory,
        bool needsCreateDirectory)
    {
        if (!needsCreateDirectory)
        {
            return true;
        }

        string message = FileOperationPresentationHelper.GetCreateDirectoryConfirmationMessage(destinationDirectory);
        DialogResult result = MessageBox.Show(message, "確認", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (result != DialogResult.Yes)
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(destinationDirectory);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"ディレクトリの作成に失敗しました: {ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    public bool ConfirmDelete(
        IWin32Window owner,
        SelectionResult selection,
        bool usePermanentDelete,
        string? currentPath,
        Action<string> showStatusMessage)
    {
        var dialog = FileOperationPresentationHelper.GetDeleteConfirmation(selection, usePermanentDelete);
        string? summaryText = FileOperationPresentationHelper.GetSelectionSummaryText(selection);
        string? warningText = FileOperationPresentationHelper.GetSelectionOutsideCurrentDirectoryWarning(selection, currentPath);

        bool requireAltYes = usePermanentDelete && selection.Count > 1;

        DialogResult result = DeleteConfirmDialog.Show(
            owner,
            dialog.Title,
            dialog.Message,
            dialog.Icon,
            summaryText,
            warningText,
            requireAltYes);
        if (result == DialogResult.Yes)
        {
            return true;
        }

        showStatusMessage("削除はキャンセルされました。");
        return false;
    }

    public DeleteCancelResolution ShowDeleteCancelResolution(
        IWin32Window owner,
        int successCount,
        int pendingCount,
        int failedCount)
    {
        return DeleteCancelResolutionDialog.Show(owner, successCount, pendingCount, failedCount);
    }

    public ClipboardPasteChoice ChooseClipboardPasteMode(IWin32Window owner)
    {
        return ClipboardPasteChoiceDialog.ShowChoice(owner);
    }

    public PasteSameDirectoryConfirmAction ConfirmPasteSameDirectory(
        IWin32Window owner,
        string fileName,
        string suggestedName,
        bool showApplyToAll)
    {
        return PasteSameDirectoryConfirmDialog.Show(owner, fileName, suggestedName, showApplyToAll);
    }

    public CopyCollisionDecision ShowCopyCollision(
        IWin32Window owner,
        string sourcePath,
        string destPath)
    {
        return CopyCollisionDialog.Show(owner, sourcePath, destPath);
    }

    public DirectoryMergeDecision ShowCopyDirectoryMerge(
        IWin32Window owner,
        string sourcePath,
        string destPath)
    {
        return FolderMergeDialog.Show(owner, sourcePath, destPath);
    }

    public DirectoryMergeDecision ShowMoveDirectoryMerge(
        IWin32Window owner,
        string sourcePath,
        string destPath)
    {
        return FolderMergeDialog.Show(owner, sourcePath, destPath, "移動");
    }

    public DirectoryMergeDecision ShowPasteDirectoryMerge(
        IWin32Window owner,
        string sourcePath,
        string destPath,
        bool isCut)
    {
        return PasteFolderMergeDialog.Show(owner, sourcePath, destPath, isCut);
    }

    public PasteCollisionResolution ResolvePasteCollision(
        IWin32Window owner,
        string sourcePath,
        string destPath,
        bool allowRename,
        bool isCut,
        ref CopyCollisionDecision? applyToAllDecision)
    {
        return PasteCollisionResolver.Resolve(owner, sourcePath, destPath, allowRename, isCut, ref applyToAllDecision);
    }

    public IProgress<FileOperationProgress> CreateOperationProgress(string operationName, Action<string> showStatusMessage)
    {
        return new Progress<FileOperationProgress>(p =>
        {
            showStatusMessage(FileOperationPresentationHelper.GetOperationProgressMessage(
                operationName,
                p.ProcessedCount,
                p.TotalCount,
                p.CurrentFileName));
        });
    }

    public IProgress<FileOperationProgress> CreatePasteProgress(bool isCut, Action<string> showStatusMessage)
    {
        return new Progress<FileOperationProgress>(p =>
        {
            showStatusMessage(FileOperationPresentationHelper.GetPasteProgressMessage(
                isCut,
                p.ProcessedCount,
                p.TotalCount,
                p.CurrentFileName));
        });
    }

    public void ShowTypeMismatchConflict(IWin32Window owner, string conflictPath)
    {
        MessageBox.Show(
            $"型が異なるため上書きできません。\n宛先: {conflictPath}",
            "上書きエラー",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    public void ShowUnsupportedDirectoryOverwrite(IWin32Window owner)
    {
        MessageBox.Show(
            "フォルダ同士の上書き（統合）は現在未対応です。",
            "エラー",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    public void ShowInformationDialog(IWin32Window owner, string message, string title)
    {
        MessageBox.Show(message, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    public void ShowOperationError(IWin32Window owner, string operationDisplayName, string targetName, string detail)
    {
        MessageBox.Show(
            $"{operationDisplayName}失敗: {targetName}\n{detail}",
            "エラー",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    public void ShowUnexpectedOperationError(IWin32Window owner, string operationDisplayName, Exception ex)
    {
        MessageBox.Show(
            $"{operationDisplayName}中に予期せぬエラーが発生しました:\n{ex.Message}",
            "エラー",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}
