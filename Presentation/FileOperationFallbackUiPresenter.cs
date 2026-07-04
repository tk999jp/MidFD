using System;
using System.Drawing;
using System.Windows.Forms;
using MidFD.Dialogs;
using MidFD.Models;
using MidFD.Helpers;

namespace MidFD.Presentation;

public static class FileOperationFallbackUiPresenter
{
    public static FileOperationProgressFallbackForm ShowProgressFallback(
        Form owner,
        string operationName,
        int totalCount,
        Action? requestCancel,
        bool canCancel,
        Action<FileOperationProgressFallbackForm>? closedCallback = null)
    {
        var form = new FileOperationProgressFallbackForm(operationName, totalCount, requestCancel, canCancel);
        if (closedCallback != null)
        {
            form.FormClosed += (_, _) => closedCallback(form);
        }
        PositionProgressFallbackForm(owner, form);
        form.Show(owner);
        return form;
    }

    public static FileOperationProgressFallbackForm ShowReadyProgressFallback(
        Form owner,
        string operationName,
        int totalCount,
        Action? requestCancel,
        bool canCancel,
        bool indeterminate,
        Action<FileOperationProgressFallbackForm>? closedCallback = null)
    {
        var form = ShowProgressFallback(owner, operationName, totalCount, requestCancel, canCancel, closedCallback);
        form.UpdateState($"{operationName}中", "準備中...", indeterminate, cancelRequested: false);
        return form;
    }

    public static FileOperationProgressFallbackForm? ShowShellDeleteProgressFallback(
        Form owner,
        int totalCount,
        Action cancelAction,
        Action closedCallback)
    {
        var form = new FileOperationProgressFallbackForm("削除", totalCount, cancelAction);
        form.FormClosed += (_, _) => closedCallback();
        PositionProgressFallbackForm(owner, form);
        form.Show(owner);
        form.UpdateProgress(0, totalCount, "準備中...", false);
        return form;
    }

    public static void UpdateShellDeleteProgressFallbackIfCurrent(
        FileOperationProgressFallbackForm? form,
        int processedCount,
        int totalCount,
        string currentFileName,
        bool isCancellationRequested)
    {
        form?.UpdateProgress(processedCount, totalCount, currentFileName, isCancellationRequested);
    }

    public static void UpdateShellDeleteProgressFallbackStateIfCurrent(
        FileOperationProgressFallbackForm? form,
        string title,
        string detail,
        bool indeterminate,
        bool isCancellationRequested)
    {
        form?.UpdateState(title, detail, indeterminate, isCancellationRequested);
    }

    public static void CompleteShellDeleteProgressFallbackIfCurrent(
        FileOperationProgressFallbackForm? form,
        FileOpExitStatus exitStatus,
        int successCount,
        int totalCount,
        int failCount)
    {
        if (form == null) return;

        string message = exitStatus switch
        {
            FileOpExitStatus.Success when successCount == totalCount && failCount == 0 => $"削除完了: {successCount}/{totalCount} 件",
            FileOpExitStatus.Canceled => $"削除を中断しました: {successCount}/{totalCount} 件",
            FileOpExitStatus.PartialSuccess => $"削除は一部完了: {successCount}/{totalCount} 件",
            _ => $"削除失敗または未完了: {successCount}/{totalCount} 件"
        };
        form.Complete(message);
    }

    public static void CompleteProgressFallback(FileOperationProgressFallbackForm? form, string message)
    {
        form?.Complete(message);
    }

    public static void CloseProgressFallback(ref FileOperationProgressFallbackForm? form)
    {
        var f = form;
        form = null;
        if (f != null && !f.IsDisposed)
        {
            f.Close();
        }
    }

    public static void PositionProgressFallbackForm(Form owner, Form form)
    {
        form.Location = new Point(
            owner.Left + Math.Max(0, (owner.Width - form.Width) / 2),
            owner.Top + Math.Max(0, (owner.Height - form.Height) / 2));
    }
}
