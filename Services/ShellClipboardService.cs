using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace MidFD.Services
{
    public static class ShellClipboardService
    {
        private const string PreferredDropEffectFormat = "Preferred DropEffect";

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
