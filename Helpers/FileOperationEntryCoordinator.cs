using MidFD.Models;
using MidFD.Services;

namespace MidFD.Helpers;

/// <summary>
/// file operation 実行前の入口判定だけを担当する。
/// WinForms 側の dialog 表示や busy の実際の開始は MainForm に残し、
/// ここでは selection / destination / clipboard 前提の判定結果だけを返す。
/// </summary>
public sealed class FileOperationEntryCoordinator
{
    public readonly record struct SelectionEntryRequest(
        bool IsClipboardBusy,
        string? ActiveOperationName,
        bool CanCancel,
        string AttemptedOperationName,
        SelectionResult Selection,
        string EmptySelectionMessage,
        string? BusyOperationName = null,
        bool IsCancelRequested = false);

    public readonly record struct SelectionEntryPlan(
        bool CanProceed,
        string? StatusMessage,
        SelectionResult Selection,
        string? BusyOperationName);

    public readonly record struct DestinationEntryPlan(
        bool CanProceed,
        string? StatusMessage,
        string? ValidationErrorMessage,
        string NormalizedDestinationDirectory,
        bool NeedsCreateDirectory);

    public readonly record struct ClipboardPasteEntryPlan(
        bool CanProceed,
        string? StatusMessage,
        bool HasFileDrop,
        bool HasImage,
        bool HasText,
        string CurrentPath);

    public readonly record struct SelectionDialogContext(
        string SummaryText,
        string? OutsideCurrentDirectoryWarning);

    public SelectionEntryPlan CreateSelectionEntryPlan(SelectionEntryRequest request)
    {
        return CreateSelectionEntryPlan(
            request.IsClipboardBusy,
            request.ActiveOperationName,
            request.CanCancel,
            request.AttemptedOperationName,
            request.Selection,
            request.EmptySelectionMessage,
            request.BusyOperationName,
            request.IsCancelRequested);
    }

    public SelectionEntryPlan CreateSelectionEntryPlan(
        bool isClipboardBusy,
        string? activeOperationName,
        bool canCancel,
        string attemptedOperationName,
        SelectionResult selection,
        string emptySelectionMessage,
        string? busyOperationName = null,
        bool isCancelRequested = false)
    {
        if (isClipboardBusy)
        {
            return new SelectionEntryPlan(
                false,
                FileOperationPresentationHelper.GetBusyBlockedMessage(
                    string.IsNullOrWhiteSpace(activeOperationName) ? attemptedOperationName : activeOperationName,
                    canCancel,
                    isCancelRequested),
                SelectionResult.Empty,
                null);
        }

        if (selection.Count == 0)
        {
            return new SelectionEntryPlan(false, emptySelectionMessage, SelectionResult.Empty, null);
        }

        return new SelectionEntryPlan(true, null, selection, busyOperationName);
    }

    public SelectionDialogContext CreateSelectionDialogContext(SelectionResult selection, string? currentPath)
    {
        string firstName = selection.FirstFileName ?? "(不明)";
        string summaryText = $"{selection.Count} 件の対象が選択されています。{Environment.NewLine}先頭項目: {firstName}";
        string? outsideWarning = FileOperationPresentationHelper.GetSelectionOutsideCurrentDirectoryWarning(selection, currentPath);
        return new SelectionDialogContext(summaryText, outsideWarning);
    }

    public DestinationEntryPlan CreateDestinationEntryPlan(
        string? input,
        string currentPath,
        string normalizedDestinationDirectory,
        string operationDisplayName,
        string canceledMessage)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return new DestinationEntryPlan(false, canceledMessage, null, string.Empty, false);
        }

        string? validationError = FileOperationPresentationHelper.GetDestinationPathErrorMessage(
            input,
            currentPath,
            normalizedDestinationDirectory,
            operationDisplayName);

        if (validationError != null)
        {
            return new DestinationEntryPlan(false, null, validationError, normalizedDestinationDirectory, false);
        }

        return new DestinationEntryPlan(
            true,
            null,
            null,
            normalizedDestinationDirectory,
            !Directory.Exists(normalizedDestinationDirectory));
    }

    public ClipboardPasteEntryPlan CreateClipboardPasteEntryPlan(
        bool isBrowserMode,
        bool isClipboardBusy,
        bool canCancel,
        bool isCancelRequested,
        bool hasFileDrop,
        bool hasImage,
        bool hasText,
        string? currentPath)
    {
        if (!isBrowserMode)
        {
            return new ClipboardPasteEntryPlan(false, "この画面では貼り付けできません", hasFileDrop, hasImage, hasText, string.Empty);
        }

        if (isClipboardBusy)
        {
            return new ClipboardPasteEntryPlan(
                false,
                FileOperationPresentationHelper.GetBusyBlockedMessage("貼り付け", canCancel, isCancelRequested),
                hasFileDrop,
                hasImage,
                hasText,
                string.Empty);
        }

        if (!hasFileDrop && !hasImage && !hasText)
        {
            return new ClipboardPasteEntryPlan(false, "貼り付けできる項目がありません", hasFileDrop, hasImage, hasText, string.Empty);
        }

        if (string.IsNullOrWhiteSpace(currentPath))
        {
            return new ClipboardPasteEntryPlan(false, null, hasFileDrop, hasImage, hasText, string.Empty);
        }

        return new ClipboardPasteEntryPlan(true, null, hasFileDrop, hasImage, hasText, currentPath);
    }
}
