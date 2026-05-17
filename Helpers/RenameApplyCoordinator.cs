using MidFD.Models;
using MidFD.Services;

namespace MidFD.Helpers;

/// <summary>
/// Rename の preview 確定後 apply と、その結果を MainForm へ返す責務だけを扱う。
/// dialog hookup、preview / validation、本体 policy は既存の責務に残し、
/// ここでは apply 実行と post-operation へ渡す前段だけをまとめる。
/// </summary>
public sealed class RenameApplyCoordinator
{
    public readonly record struct RenameApplyOutcome(
        string? StatusMessage,
        FileOperationResult? PostOperationResult,
        IReadOnlyList<RenamePreviewItem> SuccessfulItems);

    public RenameApplyOutcome ApplySingleRename(
        string sourcePath,
        string? initialValue,
        bool showNoChangeStatus,
        bool showValidationMessage,
        Func<string, string?, bool, bool, RenameDialogCoordinator.SingleRenameDialogResult> showSingleRenameDialog,
        Func<Exception, string> getFriendlyRenameErrorMessage,
        Action<string> showRenameError,
        Func<int, int, string> buildRenameUndoReadyMessage)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return new RenameApplyOutcome("リネーム対象がありません。", null, Array.Empty<RenamePreviewItem>());
        }

        bool skipInitialPrompt = initialValue != null;

        while (true)
        {
            var dialogResult = showSingleRenameDialog(
                sourcePath,
                initialValue,
                skipInitialPrompt,
                showValidationMessage);

            if (dialogResult.WasCanceled)
            {
                return new RenameApplyOutcome("リネームはキャンセルされました。", null, Array.Empty<RenamePreviewItem>());
            }

            if (!dialogResult.WillRename || dialogResult.PreviewItem == null)
            {
                return new RenameApplyOutcome(showNoChangeStatus ? "変更はありません。" : null, null, Array.Empty<RenamePreviewItem>());
            }

            try
            {
                FileOperationService.Rename(dialogResult.PreviewItem.SourcePath, dialogResult.PreviewItem.DestinationPath);
                return new RenameApplyOutcome(
                    null,
                    new FileOperationResult(
                        "Rename",
                        FileOpExitStatus.Success,
                        1,
                        1,
                        dialogResult.PreviewItem.DestinationName,
                        customMessage: buildRenameUndoReadyMessage(1, 1)),
                    new[] { dialogResult.PreviewItem });
            }
            catch (Exception ex)
            {
                showRenameError($"リネーム失敗: {dialogResult.PreviewItem.SourceName}\n{getFriendlyRenameErrorMessage(ex)}");
                initialValue = dialogResult.PreviewItem.DestinationName;
                skipInitialPrompt = false;
            }
        }
    }

    public RenameApplyOutcome ApplySequentialRename(
        SelectionResult selection,
        string? firstItemInitialName,
        Func<string, string?, bool, bool, RenameDialogCoordinator.SingleRenameDialogResult> showSingleRenameDialog,
        Func<Exception, string> getFriendlyRenameErrorMessage,
        Action<string> showRenameError,
        Func<int, int, string> buildRenameUndoReadyMessage)
    {
        int successCount = 0;
        string? lastRenamedName = null;
        bool canceled = false;
        var successfulItems = new List<RenamePreviewItem>();

        for (int i = 0; i < selection.FullPaths.Count; i++)
        {
            string sourcePath = selection.FullPaths[i];
            string? initialValue = i == 0 ? firstItemInitialName : null;

            var itemOutcome = ApplySingleRename(
                sourcePath,
                initialValue,
                showNoChangeStatus: false,
                showValidationMessage: false,
                showSingleRenameDialog,
                getFriendlyRenameErrorMessage,
                showRenameError,
                buildRenameUndoReadyMessage);

            if (itemOutcome.PostOperationResult == null)
            {
                if (itemOutcome.StatusMessage == "リネームはキャンセルされました。")
                {
                    canceled = true;
                    break;
                }

                continue;
            }

            var renamedItem = itemOutcome.SuccessfulItems[0];
            successCount++;
            lastRenamedName = renamedItem.DestinationName;
            successfulItems.Add(renamedItem);
        }

        if (successCount > 0)
        {
            int skipCount = selection.Count - successCount;
            string? customMessage = canceled
                ? $"{successCount} 件リネームしたところで中断しました。Ctrl+Z で元に戻せます。"
                : buildRenameUndoReadyMessage(successCount, selection.Count);
            var exitStatus = canceled
                ? FileOpExitStatus.Canceled
                : FileOperationPresentationHelper.NormalizeExitStatus(FileOpExitStatus.Success, successCount, selection.Count, skipCount: skipCount);

            return new RenameApplyOutcome(
                null,
                new FileOperationResult(
                    "Rename",
                    exitStatus,
                    successCount,
                    selection.Count,
                    lastRenamedName,
                    customMessage: customMessage,
                    skipCount: skipCount),
                successfulItems);
        }

        return new RenameApplyOutcome(
            canceled ? "リネームはキャンセルされました。" : "変更はありません。",
            null,
            Array.Empty<RenamePreviewItem>());
    }

    public RenameApplyOutcome ApplyBatchRename(
        SelectionResult selection,
        RenamePreviewResult preview,
        string currentPath,
        Action<string> showRenameError,
        Func<int, int, string> buildRenameUndoReadyMessage,
        Action<int, int, string>? onProgress = null)
    {
        if (preview.Items.Count == 0)
        {
            return new RenameApplyOutcome("リネーム対象がありません。", null, Array.Empty<RenamePreviewItem>());
        }

        if (preview.HasErrors)
        {
            return new RenameApplyOutcome("問題のある行があるためリネームを実行できません。", null, Array.Empty<RenamePreviewItem>());
        }

        string? focusTargetName = preview.Items
            .FirstOrDefault(item =>
                item.WillRename &&
                string.Equals(
                    NavigationService.NormalizeDirectoryForCompare(Path.GetDirectoryName(item.DestinationPath) ?? string.Empty),
                    NavigationService.NormalizeDirectoryForCompare(currentPath),
                    StringComparison.OrdinalIgnoreCase))
            ?.DestinationName;

        string? lastRenamedName = focusTargetName;
        int successCount = 0;
        int skipCount = preview.Items.Count(item => !item.WillRename);
        FileOpExitStatus exitStatus = FileOpExitStatus.Success;
        int failCount = 0;
        var successfulItems = new List<RenamePreviewItem>();

        int totalToRename = preview.Items.Count - skipCount;
        int processedToRename = 0;

        foreach (var item in preview.Items)
        {
            if (!item.WillRename)
            {
                continue;
            }

            try
            {
                FileOperationService.Rename(item.SourcePath, item.DestinationPath);
                lastRenamedName = item.DestinationName;
                successCount++;
                successfulItems.Add(item);
            }
            catch (Exception ex)
            {
                showRenameError($"リネーム失敗: {item.SourceName}\n{ex.Message}");
                failCount++;
                exitStatus = FileOpExitStatus.Error;
                break;
            }
            finally
            {
                processedToRename++;
                // 100件ごと、または最後の1件で進捗を通知 (UIスレッドへの負荷軽減)
                if (onProgress != null && (processedToRename % 100 == 0 || processedToRename == totalToRename))
                {
                    onProgress(processedToRename, totalToRename, item.DestinationName);
                }
            }
        }

        if (successCount > 0)
        {
            string? customMessage = exitStatus == FileOpExitStatus.Success
                ? buildRenameUndoReadyMessage(successCount, selection.Count)
                : $"{successCount} 件リネーム後にエラーで停止しました。Ctrl+Z で成功分を元に戻せます。";

            return new RenameApplyOutcome(
                null,
                new FileOperationResult(
                    "Rename",
                    FileOperationPresentationHelper.NormalizeExitStatus(exitStatus, successCount, selection.Count, skipCount: skipCount, failCount: failCount),
                    successCount,
                    selection.Count,
                    lastRenamedName,
                    customMessage: customMessage,
                    skipCount: skipCount,
                    failCount: failCount),
                successfulItems);
        }

        return new RenameApplyOutcome(preview.HasRenames ? "リネームは実行されませんでした。" : "変更はありません。", null, Array.Empty<RenamePreviewItem>());
    }
}
