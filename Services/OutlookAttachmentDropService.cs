using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
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

            // 通常のFileDropがある場合は、仮想ファイルとして扱わない
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
        public static bool ProcessDrop(
            System.Windows.Forms.IDataObject data,
            string targetDir,
            Func<string, OverwriteConfirmResult> confirmOverwrite)
        {
            var comDataObject = data as System.Runtime.InteropServices.ComTypes.IDataObject;
            if (comDataObject == null)
            {
                LogService.Warn("[OutlookDrop] DataObject cannot be cast to ComTypes.IDataObject.");
                return false;
            }

            var fileNames = GetAttachmentNames(data);
            if (fileNames.Count == 0)
            {
                LogService.Warn("[OutlookDrop] No attachment names resolved.");
                return false;
            }

            int successCount = 0;
            for (int i = 0; i < fileNames.Count; i++)
            {
                string fileName = fileNames[i];
                string destPath = Path.Combine(targetDir, fileName);

                bool destExists = File.Exists(destPath) || Directory.Exists(destPath);
                if (destExists)
                {
                    if (Directory.Exists(destPath))
                    {
                        MessageBox.Show($"型が異なるため上書きできません。\n宛先: {destPath}", "上書きエラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        continue;
                    }

                    var confirm = confirmOverwrite(fileName);
                    if (confirm == OverwriteConfirmResult.Cancel)
                    {
                        LogService.Info("[OutlookDrop] Copy canceled by user.");
                        break;
                    }
                    if (confirm == OverwriteConfirmResult.No)
                    {
                        LogService.Info($"[OutlookDrop] Skipped file: {fileName}");
                        continue;
                    }
                }

                try
                {
                    if (SaveAttachmentFile(comDataObject, i, destPath))
                    {
                        successCount++;
                    }
                }
                catch (Exception ex)
                {
                    LogService.Error($"[OutlookDrop] Failed to save virtual file index {i}: {fileName}", ex);
                    MessageBox.Show($"コピー失敗: {fileName}\n{ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    break;
                }
            }

            return successCount > 0;
        }

        private static bool SaveAttachmentFile(System.Runtime.InteropServices.ComTypes.IDataObject comDataObject, int index, string destPath)
        {
            FORMATETC formatetc = new FORMATETC
            {
                cfFormat = (short)FileContentsFormat,
                dwAspect = DVASPECT.DVASPECT_CONTENT,
                lindex = index,
                tymed = TYMED.TYMED_ISTREAM
            };

            STGMEDIUM medium;
            try
            {
                comDataObject.GetData(ref formatetc, out medium);
            }
            catch (Exception ex)
            {
                LogService.Error($"[OutlookDrop] IDataObject.GetData for index {index} failed.", ex);
                return false;
            }

            if (medium.tymed == TYMED.TYMED_ISTREAM && medium.unionmember != IntPtr.Zero)
            {
                // COM IStream を .NET IStream にラップして読み込む
                var comStream = Marshal.GetObjectForIUnknown(medium.unionmember) as IStream;
                if (comStream != null)
                {
                    try
                    {
                        using (var destFileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            byte[] buffer = new byte[8192];
                            int bytesRead;
                            do
                            {
                                IntPtr pBytesRead = Marshal.AllocHGlobal(sizeof(int));
                                try
                                {
                                    comStream.Read(buffer, buffer.Length, pBytesRead);
                                    bytesRead = Marshal.ReadInt32(pBytesRead);
                                }
                                finally
                                {
                                    Marshal.FreeHGlobal(pBytesRead);
                                }

                                if (bytesRead > 0)
                                {
                                    destFileStream.Write(buffer, 0, bytesRead);
                                }
                            } while (bytesRead > 0);
                        }
                        return true;
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(comStream);
                        // STGMEDIUM を解放
                        ReleaseStgMedium(ref medium);
                    }
                }
            }

            ReleaseStgMedium(ref medium);
            return false;
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
