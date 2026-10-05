using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MidFD.Services
{
    public static class ShellClipboardService
    {
        private const string PreferredDropEffectFormat = "Preferred DropEffect";
        private const uint ClipboardFormatHDrop = 15;
        private const uint DragQueryFileAllFiles = 0xFFFFFFFF;
        private static readonly IClipboardStatusProbe DefaultClipboardStatusProbe = new NativeClipboardStatusProbe();

        internal readonly struct ClipboardStatusSnapshot
        {
            public bool HasFileDrop { get; }
            public int FileDropCount { get; }
            public bool IsCut { get; }
            public bool HasImage { get; }
            public bool HasText { get; }

            public ClipboardStatusSnapshot(
                bool hasFileDrop,
                int fileDropCount,
                bool isCut,
                bool hasImage,
                bool hasText)
            {
                HasFileDrop = hasFileDrop;
                FileDropCount = fileDropCount;
                IsCut = isCut;
                HasImage = hasImage;
                HasText = hasText;
            }
        }

        internal interface IClipboardStatusProbe
        {
            bool TryRead(out ClipboardStatusSnapshot snapshot, out string? errorMessage);
        }

        internal static bool TryGetStatus(out ClipboardStatusSnapshot snapshot, out string? errorMessage)
            => TryGetStatus(DefaultClipboardStatusProbe, out snapshot, out errorMessage);

        internal static bool TryGetStatus(
            IClipboardStatusProbe probe,
            out ClipboardStatusSnapshot snapshot,
            out string? errorMessage)
        {
            return probe.TryRead(out snapshot, out errorMessage);
        }

        public static bool HasFileDrop()
        {
            try
            {
                return Clipboard.ContainsFileDropList();
            }
            catch (Exception ex)
            {
                LogService.Error("HasFileDrop failed", ex);
                return false;
            }
        }

        public static bool HasImage()
        {
            try
            {
                return Clipboard.ContainsImage();
            }
            catch (Exception ex)
            {
                LogService.Error("HasImage failed", ex);
                return false;
            }
        }

        public static bool HasText()
        {
            try
            {
                return Clipboard.ContainsText(TextDataFormat.UnicodeText);
            }
            catch (Exception ex)
            {
                LogService.Error("HasText failed", ex);
                return false;
            }
        }

        public static bool TryHasFileDrop(out bool hasFileDrop, out string? errorMessage)
        {
            hasFileDrop = false;
            errorMessage = null;
            try
            {
                hasFileDrop = Clipboard.ContainsFileDropList();
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                LogService.Error("TryHasFileDrop failed", ex);
                return false;
            }
        }

        public static bool TryHasImage(out bool hasImage, out string? errorMessage)
        {
            hasImage = false;
            errorMessage = null;
            try
            {
                hasImage = Clipboard.ContainsImage();
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                LogService.Error("TryHasImage failed", ex);
                return false;
            }
        }

        public static bool TryHasText(out bool hasText, out string? errorMessage)
        {
            hasText = false;
            errorMessage = null;
            try
            {
                hasText = Clipboard.ContainsText(TextDataFormat.UnicodeText);
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                LogService.Error("TryHasText failed", ex);
                return false;
            }
        }

        public static bool TryGetImage(out Image? image, out string? errorMessage)
        {
            image = null;
            errorMessage = null;
            try
            {
                if (!Clipboard.ContainsImage())
                {
                    return false;
                }

                image = Clipboard.GetImage();
                return image != null;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                LogService.Error("TryGetImage failed", ex);
                return false;
            }
        }

        public static bool TryGetText(out string? text, out string? errorMessage)
        {
            text = null;
            errorMessage = null;
            try
            {
                if (!Clipboard.ContainsText(TextDataFormat.UnicodeText))
                {
                    return false;
                }

                text = Clipboard.GetText(TextDataFormat.UnicodeText);
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                LogService.Error("TryGetText failed", ex);
                return false;
            }
        }

        private sealed class NativeClipboardStatusProbe : IClipboardStatusProbe
        {
            public bool TryRead(out ClipboardStatusSnapshot snapshot, out string? errorMessage)
            {
                bool hasImage = false;
                bool hasText = false;
                bool hasFileDrop = false;
                bool isCut = false;
                int fileDropCount = 0;
                errorMessage = null;

                try
                {
                    hasImage = Clipboard.ContainsImage();
                }
                catch (Exception ex)
                {
                    errorMessage = ex.Message;
                    LogService.Error("Clipboard status image probe failed", ex);
                }

                try
                {
                    hasText = Clipboard.ContainsText(TextDataFormat.UnicodeText);
                }
                catch (Exception ex)
                {
                    errorMessage ??= ex.Message;
                    LogService.Error("Clipboard status text probe failed", ex);
                }

                bool clipboardOpened = false;
                try
                {
                    clipboardOpened = OpenClipboard(IntPtr.Zero);
                    if (!clipboardOpened)
                    {
                        throw new ExternalException("OpenClipboard failed.", Marshal.GetLastWin32Error());
                    }

                    IntPtr hDrop = GetClipboardData(ClipboardFormatHDrop);
                    hasFileDrop = hDrop != IntPtr.Zero;
                    if (hasFileDrop)
                    {
                        uint count = DragQueryFile(hDrop, DragQueryFileAllFiles, IntPtr.Zero, 0);
                        fileDropCount = count > int.MaxValue ? int.MaxValue : (int)count;

                        uint dropEffectFormat = RegisterClipboardFormat(PreferredDropEffectFormat);
                        if (dropEffectFormat != 0)
                        {
                            isCut = ReadIsCut(GetClipboardData(dropEffectFormat));
                        }
                    }
                }
                catch (Exception ex)
                {
                    errorMessage ??= ex.Message;
                    LogService.Error("Clipboard status file-drop probe failed", ex);
                    hasFileDrop = false;
                    fileDropCount = 0;
                    isCut = false;
                }
                finally
                {
                    if (clipboardOpened)
                    {
                        CloseClipboard();
                    }
                }

                snapshot = new ClipboardStatusSnapshot(
                    hasFileDrop,
                    fileDropCount,
                    isCut,
                    hasImage,
                    hasText);
                return true;
            }

            private static bool ReadIsCut(IntPtr dropEffectHandle)
            {
                if (dropEffectHandle == IntPtr.Zero)
                {
                    return false;
                }

                IntPtr data = GlobalLock(dropEffectHandle);
                if (data == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    return Marshal.ReadInt32(data) == 2;
                }
                finally
                {
                    GlobalUnlock(dropEffectHandle);
                }
            }

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool OpenClipboard(IntPtr hWndNewOwner);

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool CloseClipboard();

            [DllImport("user32.dll", SetLastError = true)]
            private static extern IntPtr GetClipboardData(uint uFormat);

            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern uint RegisterClipboardFormat(string lpszFormat);

            [DllImport("shell32.dll", EntryPoint = "DragQueryFileW", SetLastError = true)]
            private static extern uint DragQueryFile(
                IntPtr hDrop,
                uint iFile,
                IntPtr lpszFile,
                uint cch);

            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern IntPtr GlobalLock(IntPtr hMem);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool GlobalUnlock(IntPtr hMem);
        }

        internal sealed class ClipboardFileDropSnapshot
        {
            public List<string> Paths { get; }
            public bool IsCut { get; }

            public ClipboardFileDropSnapshot(IEnumerable<string> paths, bool isCut)
            {
                Paths = paths.ToList();
                IsCut = isCut;
            }
        }

        internal static bool TryGetSnapshot(out ClipboardFileDropSnapshot? snapshot, out string? errorMessage)
        {
            snapshot = null;
            errorMessage = null;

            try
            {
                if (!Clipboard.ContainsFileDropList()) return true;

                var data = Clipboard.GetDataObject();
                if (data == null) return true;

                var pathsObj = data.GetData(DataFormats.FileDrop) as string[];
                if (pathsObj == null || pathsObj.Length == 0) return true;

                bool isCut = false;
                var dropEffect = data.GetData("Preferred DropEffect") as MemoryStream;
                if (dropEffect != null && dropEffect.Length >= 4)
                {
                    byte[] bytes = new byte[4];
                    dropEffect.Read(bytes, 0, 4);
                    int effect = BitConverter.ToInt32(bytes, 0);
                    isCut = (effect == 2); // 2 = MOVE (Cut)
                }

                snapshot = new ClipboardFileDropSnapshot(pathsObj, isCut);
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                LogService.Error("TryGetSnapshot failed", ex);
                return false;
            }
        }

        internal static bool IsSameCutSnapshot(ClipboardFileDropSnapshot? a, ClipboardFileDropSnapshot? b)
        {
            if (a == null || b == null) return a == b;
            if (a.IsCut != b.IsCut) return false;
            if (a.Paths.Count != b.Paths.Count) return false;

            return a.Paths.SequenceEqual(b.Paths);
        }

        public static bool TryClear(out string? errorMessage)
        {
            errorMessage = null;
            try
            {
                Clipboard.Clear();
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                LogService.Error("TryClear failed", ex);
                return false;
            }
        }

        public static void SetFileDrop(IEnumerable<string> paths, bool isCut)
        {
            try
            {
                var validPaths = paths.Where(p => !string.IsNullOrEmpty(p) && p != ".." && (File.Exists(p) || Directory.Exists(p))).ToArray();
                if (validPaths.Length == 0) return;

                var dataObject = new DataObject();
                dataObject.SetData(DataFormats.FileDrop, true, validPaths);

                // 1: Copy, 2: Move(Cut)
                byte[] dropEffect = new byte[] { (byte)(isCut ? 2 : 1), 0, 0, 0 };
                using (var stream = new MemoryStream(dropEffect))
                {
                    dataObject.SetData(PreferredDropEffectFormat, stream);
                    Clipboard.SetDataObject(dataObject, true);
                }
            }
            catch (Exception ex)
            {
                LogService.Error("SetFileDrop failed", ex);
            }
        }

        public static bool TryGetFileDrop(out List<string> validPaths, out bool isCut)
        {
            validPaths = new List<string>();
            isCut = false;

            try
            {
                if (!Clipboard.ContainsFileDropList())
                    return false;

                var paths = Clipboard.GetFileDropList();
                foreach (string? p in paths)
                {
                    if (!string.IsNullOrEmpty(p) && p != ".." && (File.Exists(p) || Directory.Exists(p)))
                    {
                        validPaths.Add(p);
                    }
                }

                if (validPaths.Count == 0) return false;

                // Preferred DropEffect の読み取り (Cut か Copy か)
                var dataObject = Clipboard.GetDataObject();
                if (dataObject != null && dataObject.GetDataPresent(PreferredDropEffectFormat))
                {
                    if (dataObject.GetData(PreferredDropEffectFormat) is MemoryStream stream)
                    {
                        byte[] dropEffect = stream.ToArray();
                        if (dropEffect.Length >= 1)
                        {
                            // 2 = Move(Cut)
                            isCut = (dropEffect[0] == 2);
                        }
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                LogService.Error("TryGetFileDrop failed", ex);
                return false;
            }
        }
    }
}
