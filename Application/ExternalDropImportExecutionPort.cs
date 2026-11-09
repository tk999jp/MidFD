using MidFD.Commands;
using MidFD.Services;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MidFD;

internal interface IExternalDropImportExecutionPort
{
    Task<OutlookAttachmentDropResult> ProcessOutlookAttachmentsAsync(
        IDataObject dataObject,
        string destinationDirectory,
        Func<string, OverwriteConfirmResult> confirmOverwrite,
        Action<string> showTypeMismatch,
        Action<string, Exception?> showCopyFailure,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? attachmentNames,
        Action<Action>? invokeComOwnerThread);

    Task<string> SaveImagePngAsync(
        IDataObject dataObject,
        string destinationDirectory,
        Action<Action>? invokeDataObjectOwnerThread);

    Task<string> DownloadImageAsync(
        Uri imageUri,
        string destinationDirectory,
        string? suggestedFileName,
        CancellationToken cancellationToken);
}

internal sealed class DefaultExternalDropImportExecutionPort : IExternalDropImportExecutionPort
{
    public Task<OutlookAttachmentDropResult> ProcessOutlookAttachmentsAsync(
        IDataObject dataObject,
        string destinationDirectory,
        Func<string, OverwriteConfirmResult> confirmOverwrite,
        Action<string> showTypeMismatch,
        Action<string, Exception?> showCopyFailure,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? attachmentNames,
        Action<Action>? invokeComOwnerThread)
        => Task.Run(() => OutlookAttachmentDropService.ProcessDrop(
            dataObject,
            destinationDirectory,
            confirmOverwrite,
            cancellationToken,
            showTypeMismatch,
            showCopyFailure,
            attachmentNames,
            invokeComOwnerThread));

    public Task<string> SaveImagePngAsync(
        IDataObject dataObject,
        string destinationDirectory,
        Action<Action>? invokeDataObjectOwnerThread)
    {
        // IDataObject is STA/COM-affine. Read it on its owning UI thread, then send only
        // the detached image object to filesystem work on the worker thread.
        Image? image = SynchronousThreadDispatcher.Invoke(invokeDataObjectOwnerThread, () =>
        {
            if (!BrowserImageDropService.TryGetImage(dataObject, out Image? extracted) || extracted == null)
            {
                throw new InvalidOperationException("このブラウザの画像ドロップ形式には未対応です。");
            }
            return extracted;
        });

        // Only the owned image and filesystem work cross into the worker thread.
        return Task.Run(() =>
        {
            using (image)
            {
                return BrowserImageDropService.SavePngToDirectory(image, destinationDirectory);
            }
        });
    }

    public Task<string> DownloadImageAsync(
        Uri imageUri,
        string destinationDirectory,
        string? suggestedFileName,
        CancellationToken cancellationToken)
        => Task.Run(() => BrowserDroppedImageDownloadService.DownloadToDirectory(
            imageUri,
            destinationDirectory,
            suggestedFileName,
            cancellationToken: cancellationToken));
}
