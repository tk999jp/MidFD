using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Forms;
using MidFD.Helpers;

namespace MidFD.Services
{
    public enum OverwriteConfirmResult
    {
        Yes,
        No,
        Cancel
    }

    public readonly record struct OutlookAttachmentDropResult(
        int AttachmentCount,
        int ProcessedCount,
        int SuccessCount,
        int FailureCount,
        int SkippedCount,
        bool WasCanceled,
        IReadOnlyList<string>? SavedFileNames = null)
    {
        public bool AnySucceeded => SuccessCount > 0;

        public IReadOnlyList<string> SuccessfulFileNames => SavedFileNames ?? Array.Empty<string>();

        public bool HadFailure => FailureCount > 0;

        public bool AllSucceeded => AttachmentCount > 0 && SuccessCount == AttachmentCount;

        public string Classification =>
            WasCanceled
                ? HadFailure
                    ? AnySucceeded ? "partial-canceled-with-failure" : "canceled-with-failure"
                    : AnySucceeded ? "partial-canceled" : "canceled"
                : HadFailure
                    ? AnySucceeded ? "partial-failure" : "failed"
                    : AttachmentCount == 0
                        ? "no-op"
                        : AllSucceeded
                            ? "all-succeeded"
                            : AnySucceeded ? "partial-skipped" : "skipped";
    }

    public static class OutlookAttachmentDropService
    {
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int RegisterClipboardFormat(string format);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FILEDESCRIPTORW
        {
            public uint dwFlags;
            public Guid clsid;
            public int sizel_cx;
            public int sizel_cy;
            public int pointl_x;
            public int pointl_y;
            public uint dwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string cFileName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private struct FILEDESCRIPTORA
        {
            public uint dwFlags;
            public Guid clsid;
            public int sizel_cx;
            public int sizel_cy;
            public int pointl_x;
            public int pointl_y;
            public uint dwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string cFileName;
        }

        private static readonly int FileGroupDescriptorWFormat = RegisterClipboardFormat("FileGroupDescriptorW");
        private static readonly int FileGroupDescriptorFormat = RegisterClipboardFormat("FileGroupDescriptor");
        private static readonly int FileContentsFormat = RegisterClipboardFormat("FileContents");

        /// <summary>
        /// IDataObjectがOutlookの仮想ファイル添付ドラッグ＆ドロップであるかを判定します。
        /// </summary>
        public static bool IsOutlookAttachmentDrop(System.Windows.Forms.IDataObject? data)
        {
            if (data == null) return false;

            // Filesystem FileDrop takes precedence even if virtual attachment formats coexist.
            if (data.GetDataPresent(DataFormats.FileDrop)) return false;

            bool hasDescriptor = data.GetDataPresent("FileGroupDescriptorW") || data.GetDataPresent("FileGroupDescriptor");
            bool hasContents = data.GetDataPresent("FileContents");

            return hasDescriptor && hasContents;
        }

        /// <summary>
        /// ドラッグされているファイル名の一覧を取得します。
        /// </summary>
        public static List<string> GetAttachmentNames(System.Windows.Forms.IDataObject data)
        {
            var names = new List<string>();

            // FileGroupDescriptorW を優先して読み込む
            if (data.GetDataPresent("FileGroupDescriptorW"))
            {
                var descriptorStream = data.GetData("FileGroupDescriptorW") as MemoryStream;
                if (descriptorStream != null)
                {
                    byte[] buffer = descriptorStream.ToArray();
                    if (buffer.Length >= 4)
                    {
                        int fileCount = BitConverter.ToInt32(buffer, 0);
                        int structSize = Marshal.SizeOf(typeof(FILEDESCRIPTORW));
                        int expectedMinSize = 4 + fileCount * structSize;

                        if (buffer.Length >= expectedMinSize)
                        {
                            IntPtr ptr = Marshal.AllocHGlobal(structSize);
                            try
                            {
                                for (int i = 0; i < fileCount; i++)
                                {
                                    int offset = 4 + i * structSize;
                                    Marshal.Copy(buffer, offset, ptr, structSize);
                                    var desc = Marshal.PtrToStructure<FILEDESCRIPTORW>(ptr);
                                    names.Add(SanitizeFileName(desc.cFileName, i));
                                }
                            }
                            finally
                            {
                                Marshal.FreeHGlobal(ptr);
                            }
                        }
                    }
                }
            }
            // FileGroupDescriptor (ANSI fallback)
            else if (data.GetDataPresent("FileGroupDescriptor"))
            {
                var descriptorStream = data.GetData("FileGroupDescriptor") as MemoryStream;
                if (descriptorStream != null)
                {
                    byte[] buffer = descriptorStream.ToArray();
                    if (buffer.Length >= 4)
                    {
                        int fileCount = BitConverter.ToInt32(buffer, 0);
                        int structSize = Marshal.SizeOf(typeof(FILEDESCRIPTORA));
                        int expectedMinSize = 4 + fileCount * structSize;

                        if (buffer.Length >= expectedMinSize)
                        {
                            IntPtr ptr = Marshal.AllocHGlobal(structSize);
                            try
                            {
                                for (int i = 0; i < fileCount; i++)
                                {
                                    int offset = 4 + i * structSize;
                                    Marshal.Copy(buffer, offset, ptr, structSize);
                                    var desc = Marshal.PtrToStructure<FILEDESCRIPTORA>(ptr);
                                    names.Add(SanitizeFileName(desc.cFileName, i));
                                }
                            }
                            finally
                            {
                                Marshal.FreeHGlobal(ptr);
                            }
                        }
                    }
                }
            }

            return names;
        }

        /// <summary>
        /// 仮想ファイルをターゲットディレクトリへ保存します。
        /// </summary>
        public static OutlookAttachmentDropResult ProcessDrop(
            System.Windows.Forms.IDataObject data,
            string targetDir,
            Func<string, OverwriteConfirmResult> confirmOverwrite,
            Action<string>? showTypeMismatch = null,
            Action<string, Exception?>? showCopyFailure = null)
            => ProcessDrop(
                data,
                targetDir,
                confirmOverwrite,
                CancellationToken.None,
                showTypeMismatch,
                showCopyFailure);

        public static OutlookAttachmentDropResult ProcessDrop(
            System.Windows.Forms.IDataObject data,
            string targetDir,
            Func<string, OverwriteConfirmResult> confirmOverwrite,
            CancellationToken cancellationToken,
            Action<string>? showTypeMismatch = null,
            Action<string, Exception?>? showCopyFailure = null,
            IReadOnlyList<string>? attachmentNames = null,
            Action<Action>? invokeComOwnerThread = null)
        {
            showTypeMismatch ??= destinationPath => MessageBox.Show(
                $"型が異なるため上書きできません。\n宛先: {destinationPath}",
                "上書きエラー",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            showCopyFailure ??= (fileName, exception) => MessageBox.Show(
                $"コピー失敗: {fileName}\n{exception?.Message ?? "添付データを保存できませんでした。"}",
                "エラー",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);

            var comDataObject = SynchronousThreadDispatcher.Invoke(
                invokeComOwnerThread,
                () => data as System.Runtime.InteropServices.ComTypes.IDataObject);
            if (comDataObject == null)
            {
                LogService.Warn("[OutlookDrop] DataObject cannot be cast to ComTypes.IDataObject.");
                return LogResult(new OutlookAttachmentDropResult(0, 0, 0, 1, 0, false));
            }

            List<string> fileNames = attachmentNames?.ToList()
                ?? SynchronousThreadDispatcher.Invoke(invokeComOwnerThread, () => GetAttachmentNames(data));
            if (fileNames.Count == 0)
            {
                LogService.Warn("[OutlookDrop] No attachment names resolved.");
                return LogResult(new OutlookAttachmentDropResult(0, 0, 0, 0, 0, false));
            }

            return ProcessAttachmentFiles(
                fileNames,
                targetDir,
                confirmOverwrite,
                (index, temporaryPath) => SaveAttachmentFile(
                    comDataObject,
                    index,
                    temporaryPath,
                    cancellationToken,
                    invokeComOwnerThread),
                showTypeMismatch,
                showCopyFailure,
                cancellationToken);
        }

        internal static OutlookAttachmentDropResult ProcessAttachmentFiles(
            IReadOnlyList<string> fileNames,
            string targetDir,
            Func<string, OverwriteConfirmResult> confirmOverwrite,
            Func<int, string, bool> saveAttachmentFile,
            Action<string> showTypeMismatch,
            Action<string, Exception?> showCopyFailure)
            => ProcessAttachmentFiles(
                fileNames,
                targetDir,
                confirmOverwrite,
                saveAttachmentFile,
                showTypeMismatch,
                showCopyFailure,
                CancellationToken.None);

        internal static OutlookAttachmentDropResult ProcessAttachmentFiles(
            IReadOnlyList<string> fileNames,
            string targetDir,
            Func<string, OverwriteConfirmResult> confirmOverwrite,
            Func<int, string, bool> saveAttachmentFile,
            Action<string> showTypeMismatch,
            Action<string, Exception?> showCopyFailure,
            CancellationToken cancellationToken)
        {
            int processedCount = 0;
            int successCount = 0;
            int failureCount = 0;
            int skippedCount = 0;
            bool wasCanceled = false;
            var savedFileNames = new List<string>();
            for (int i = 0; i < fileNames.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    wasCanceled = true;
                    break;
                }

                processedCount++;
                string fileName = fileNames[i];
                string destPath = Path.Combine(targetDir, fileName);

                bool destExists = File.Exists(destPath) || Directory.Exists(destPath);
                if (destExists)
                {
                    if (Directory.Exists(destPath))
                    {
                        failureCount++;
                        showTypeMismatch(destPath);
                        continue;
                    }

                    var confirm = confirmOverwrite(fileName);
                    if (confirm == OverwriteConfirmResult.Cancel)
                    {
                        wasCanceled = true;
                        LogService.Info("[OutlookDrop] Copy canceled by user.");
                        break;
                    }
                    if (confirm == OverwriteConfirmResult.No)
                    {
                        skippedCount++;
                        LogService.Info($"[OutlookDrop] Skipped file: {fileName}");
                        continue;
                    }
                }

                string temporaryPath = Path.Combine(
                    targetDir,
                    $".midfd-outlook-attachment-{Guid.NewGuid():N}.tmp");
                bool ownsTemporaryFile = false;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using (var temporaryFile = new FileStream(
                        temporaryPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None))
                    {
                        ownsTemporaryFile = true;
                    }

                    if (!saveAttachmentFile(i, temporaryPath))
                    {
                        failureCount++;
                        wasCanceled |= cancellationToken.IsCancellationRequested;
                        LogService.Error($"[OutlookDrop] Failed to save virtual file index {i}: {fileName}; SaveAttachmentFile returned false.");
                        showCopyFailure(fileName, null);
                        if (wasCanceled)
                        {
                            break;
                        }
                        continue;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    PromoteTemporaryAttachment(temporaryPath, destPath, destExists);
                    ownsTemporaryFile = false;
                    successCount++;
                    savedFileNames.Add(fileName);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    wasCanceled = true;
                    LogService.Info($"[OutlookDrop] Attachment save canceled: {fileName}");
                    break;
                }
                catch (Exception ex)
                {
                    failureCount++;
                    wasCanceled |= cancellationToken.IsCancellationRequested;
                    LogService.Error($"[OutlookDrop] Failed to save virtual file index {i}: {fileName}", ex);
                    showCopyFailure(fileName, ex);
                    break;
                }
                finally
                {
                    if (ownsTemporaryFile)
                    {
                        TryDeleteTemporaryAttachment(temporaryPath);
                    }
                }
            }

            return LogResult(new OutlookAttachmentDropResult(
                fileNames.Count,
                processedCount,
                successCount,
                failureCount,
                skippedCount,
                wasCanceled,
                savedFileNames));
        }

        private static OutlookAttachmentDropResult LogResult(OutlookAttachmentDropResult result)
        {
            LogService.Info(
                $"[OutlookDrop] result=processed, attachmentCount={result.AttachmentCount}, " +
                $"processedCount={result.ProcessedCount}, successCount={result.SuccessCount}, " +
                $"failureCount={result.FailureCount}, skippedCount={result.SkippedCount}, " +
                $"canceled={result.WasCanceled}, anySucceeded={result.AnySucceeded}, " +
                $"allSucceeded={result.AllSucceeded}, classification={result.Classification}");
            return result;
        }

        private static void PromoteTemporaryAttachment(
            string temporaryPath,
            string destinationPath,
            bool overwriteConfirmed)
        {
            if (Directory.Exists(destinationPath))
            {
                throw new IOException($"保存先にディレクトリが存在します: {destinationPath}");
            }

            if (!File.Exists(destinationPath))
            {
                // File.Move is deliberately non-overwriting: a new collision after the
                // initial existence check must not replace data without confirmation.
                File.Move(temporaryPath, destinationPath);
                return;
            }

            if (!overwriteConfirmed)
            {
                throw new IOException($"確認後に同名ファイルが作成されたため上書きしません: {destinationPath}");
            }

            FileAttributes attributes = File.GetAttributes(destinationPath);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"再解析ポイントの既存ファイルは安全な置換方式を保証できません: {destinationPath}");
            }

            File.Replace(temporaryPath, destinationPath, destinationBackupFileName: null);
        }

        private static void TryDeleteTemporaryAttachment(string temporaryPath)
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception ex)
            {
                LogService.Error($"[OutlookDrop] Failed to remove owned temporary file: {temporaryPath}", ex);
            }
        }

        private static bool SaveAttachmentFile(
            System.Runtime.InteropServices.ComTypes.IDataObject comDataObject,
            int index,
            string temporaryPath,
            CancellationToken cancellationToken,
            Action<Action>? invokeComOwnerThread)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FORMATETC formatetc = new FORMATETC
            {
                cfFormat = (short)FileContentsFormat,
                dwAspect = DVASPECT.DVASPECT_CONTENT,
                lindex = index,
                tymed = TYMED.TYMED_ISTREAM
            };

            STGMEDIUM medium = default;
            bool mediumAcquired = false;
            IStream? comStream = null;
            try
            {
                medium = SynchronousThreadDispatcher.Invoke(invokeComOwnerThread, () =>
                {
                    FORMATETC request = formatetc;
                    comDataObject.GetData(ref request, out STGMEDIUM acquiredMedium);
                    return acquiredMedium;
                });
                mediumAcquired = true;
            }
            catch (Exception ex)
            {
                LogService.Error($"[OutlookDrop] IDataObject.GetData for index {index} failed.", ex);
                return false;
            }

            try
            {
                if (medium.tymed != TYMED.TYMED_ISTREAM || medium.unionmember == IntPtr.Zero)
                {
                    return false;
                }

                // GetData、IStream操作、COM解放はすべて IDataObject の所有STAで行う。
                comStream = SynchronousThreadDispatcher.Invoke(
                    invokeComOwnerThread,
                    () => Marshal.GetObjectForIUnknown(medium.unionmember) as IStream);
                if (comStream == null)
                {
                    return false;
                }

                using var destFileStream = new FileStream(
                    temporaryPath,
                    FileMode.Truncate,
                    FileAccess.Write,
                    FileShare.None);
                byte[] buffer = new byte[8192];
                while (true)
                {
                    // A blocking COM Read cannot be interrupted; cancellation is
                    // accepted at the boundaries around each read/write iteration.
                    cancellationToken.ThrowIfCancellationRequested();
                    int bytesRead = SynchronousThreadDispatcher.Invoke(invokeComOwnerThread, () =>
                    {
                        IntPtr pBytesRead = Marshal.AllocHGlobal(sizeof(int));
                        try
                        {
                            comStream.Read(buffer, buffer.Length, pBytesRead);
                            return Marshal.ReadInt32(pBytesRead);
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(pBytesRead);
                        }
                    });

                    cancellationToken.ThrowIfCancellationRequested();
                    if (bytesRead == 0)
                    {
                        break;
                    }
                    if (bytesRead < 0 || bytesRead > buffer.Length)
                    {
                        throw new IOException($"添付ストリームが不正な読み取り長を返しました: {bytesRead}");
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    destFileStream.Write(buffer, 0, bytesRead);
                }

                cancellationToken.ThrowIfCancellationRequested();
                return true;
            }
            finally
            {
                Action? releaseComStream = null;
                if (comStream != null)
                {
                    IStream streamToRelease = comStream;
                    releaseComStream = () => SynchronousThreadDispatcher.Invoke(invokeComOwnerThread, () =>
                    {
                        if (Marshal.IsComObject(streamToRelease))
                        {
                            Marshal.ReleaseComObject(streamToRelease);
                        }
                    });
                }

                Action? releaseStorageMedium = null;
                if (mediumAcquired)
                {
                    releaseStorageMedium = () => SynchronousThreadDispatcher.Invoke(invokeComOwnerThread, () =>
                    {
                        STGMEDIUM releaseMedium = medium;
                        ReleaseStgMedium(ref releaseMedium);
                    });
                }

                ReleaseAttachmentResources(releaseComStream, releaseStorageMedium);
            }
        }

        internal static void ReleaseAttachmentResources(Action? releaseComObject, Action? releaseStgMedium)
        {
            Exception? comReleaseFailure = null;
            Exception? mediumReleaseFailure = null;

            try
            {
                releaseComObject?.Invoke();
            }
            catch (Exception ex)
            {
                comReleaseFailure = ex;
            }

            try
            {
                releaseStgMedium?.Invoke();
            }
            catch (Exception ex)
            {
                mediumReleaseFailure = ex;
            }

            if (comReleaseFailure != null && mediumReleaseFailure != null)
            {
                throw new AggregateException(
                    "Both Outlook COM stream and storage medium cleanup failed.",
                    comReleaseFailure,
                    mediumReleaseFailure);
            }

            if (comReleaseFailure != null)
            {
                ExceptionDispatchInfo.Capture(comReleaseFailure).Throw();
            }

            if (mediumReleaseFailure != null)
            {
                ExceptionDispatchInfo.Capture(mediumReleaseFailure).Throw();
            }
        }

        [DllImport("ole32.dll")]
        private static extern void ReleaseStgMedium(ref STGMEDIUM pmedium);

        public static string SanitizeFileName(string original, int index)
        {
            if (string.IsNullOrWhiteSpace(original))
            {
                return $"attachment_{index}.tmp";
            }

            // Path Traversal 対策: ディレクトリセパレータ等を除去して純粋なファイル名のみにする
            string clean = Path.GetFileName(original);

            // OSのファイル名禁止文字を置換
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                clean = clean.Replace(c, '_');
            }

            if (string.IsNullOrWhiteSpace(clean))
            {
                return $"attachment_{index}.tmp";
            }

            return clean;
        }
    }
}
