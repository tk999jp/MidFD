using System;
using MidFD.Models;
using MidFD.Services;

namespace MidFD.Helpers;

/// <summary>
/// file operation 完了後に、どの UI 適用が必要かを判断する最小 orchestration を担当する。
/// WinForms 側の最終適用そのものは MainForm に残し、ここでは判断結果だけを返す。
/// </summary>
public sealed class FileOperationPostOperationCoordinator
{
    public readonly record struct PostOperationPlan(
        bool ShouldFinalizeBusy,
        bool ShouldClearPreview,
        bool ShouldReloadCurrentDirectory,
        bool ShouldRefreshMarks,
        bool ShouldClearMarks,
        string? NextFocusTarget,
        string StatusMessage);

    public FileOperationResult CreateCopyResult(
        FileOpExitStatus exitStatus,
        int successCount,
        int totalCount,
        string? nextFocusTarget,
        string destinationDirectory,
        int skipCount,
        int failCount)
    {
        return new FileOperationResult(
            "Copy",
            exitStatus,
            successCount,
            totalCount,
            nextFocusTarget,
            destinationDirectory,
            skipCount: skipCount,
            failCount: failCount);
    }

    public FileOperationResult CreateMoveResult(
        FileOpExitStatus exitStatus,
        int successCount,
        int totalCount,
        string? nextFocusTarget,
        string destinationDirectory,
        bool shouldClearMarks,
        string? customMessage,
        int skipCount,
        int failCount)
    {
        return new FileOperationResult(
            "Move",
            exitStatus,
            successCount,
            totalCount,
            nextFocusTarget,
            destinationDirectory,
            shouldClearMarks: shouldClearMarks,
            customMessage: customMessage,
            skipCount: skipCount,
            failCount: failCount);
    }

    public FileOperationResult CreateDeleteResult(
        FileOpExitStatus exitStatus,
        int successCount,
        int totalCount,
        string? nextFocusTarget,
        bool usePermanentDelete,
        bool recordedRecycleBinUndo,
        int failCount)
    {
        var baseResult = new FileOperationResult(
            "Delete",
            exitStatus,
            successCount,
            totalCount,
            nextFocusTarget,
            shouldClearPreview: true,
            shouldClearMarks: false,
            failCount: failCount);
        string resultMessage = FileOperationPresentationHelper.GetDeleteResultStatusMessage(
            baseResult,
            usePermanentDelete,
            recordedRecycleBinUndo);

        return new FileOperationResult(
            "Delete",
            exitStatus,
            successCount,
            totalCount,
            nextFocusTarget,
            shouldClearPreview: true,
            shouldClearMarks: false,
            customMessage: resultMessage,
            failCount: failCount);
    }

    public FileOperationResult CreateRenameResult(FileOperationResult renameResult)
    {
        string statusMessage = FileOperationPresentationHelper.GetRenameResultStatusMessage(renameResult);
        return new FileOperationResult(
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
            renameResult.FailCount);
    }

    public PostOperationPlan CreatePlan(
        FileOperationResult result,
        bool reloadAfterFileOperation,
        string currentPath)
    {
        bool shouldReload = ShouldReloadCurrentDirectoryAfterOperation(
            result,
            reloadAfterFileOperation,
            currentPath);

        bool shouldClearMarks = IsSuccessfulEnoughToClearMarks(result) && result.ShouldClearMarks;
        string statusMessage = !string.IsNullOrEmpty(result.CustomMessage)
            ? result.CustomMessage
            : FileOperationPresentationHelper.GetOperationResultStatusMessage(result);

        return new PostOperationPlan(
            ShouldFinalizeBusy: true,
            ShouldClearPreview: result.ShouldClearPreview,
            ShouldReloadCurrentDirectory: shouldReload,
            ShouldRefreshMarks: !shouldReload,
            ShouldClearMarks: shouldClearMarks,
            NextFocusTarget: result.NextFocusTarget,
            StatusMessage: statusMessage);
    }

    private static bool IsSuccessfulEnoughToClearMarks(FileOperationResult result)
    {
        return result.ExitStatus == FileOpExitStatus.Success
            || result.ExitStatus == FileOpExitStatus.PartialSuccess
            || result.ExitStatus == FileOpExitStatus.Skipped;
    }

    private static bool ShouldReloadCurrentDirectoryAfterOperation(
        FileOperationResult result,
        bool reloadAfterFileOperation,
        string currentPath)
    {
        if (reloadAfterFileOperation)
        {
            return true;
        }

        return result.OperationName switch
        {
            "Delete" => true,
            "Move" => true,
            "Paste" => true,
            "Rename" => true,
            "Copy" => string.Equals(
                NavigationService.NormalizeDirectoryForCompare(result.DestinationDir ?? string.Empty),
                NavigationService.NormalizeDirectoryForCompare(currentPath),
                StringComparison.OrdinalIgnoreCase),
            _ => true
        };
    }
}
