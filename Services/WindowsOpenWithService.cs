using System;
using System.IO;
using System.Runtime.InteropServices;

namespace MidFD.Services;

internal static class WindowsOpenWithService
{
    private const uint OpenAsInfoExecute = 0x00000004;

    public static string? ShowOpenWithDialog(IntPtr ownerHandle, string? filePath)
    {
        if (!TryNormalizeExistingFile(filePath, out string normalizedPath, out string validationError))
        {
            LogService.Warn($"WindowsOpenWithService target rejected: {validationError}");
            return validationError;
        }

        try
        {
            var info = new OpenAsInfo
            {
                FilePath = normalizedPath,
                FileClass = null,
                Flags = OpenAsInfoExecute
            };
            int hresult = SHOpenWithDialog(ownerHandle, ref info);
            if (hresult < 0)
            {
                string error = $"プログラムから開くの起動に失敗しました (HRESULT: 0x{unchecked((uint)hresult):X8})";
                LogService.Error($"WindowsOpenWithService: {error}");
                return error;
            }

            return null;
        }
        catch (Exception ex)
        {
            LogService.Error($"WindowsOpenWithService failed. Path: {normalizedPath}", ex);
            return $"プログラムから開くの起動に失敗しました: {ex.Message}";
        }
    }

    internal static bool TryNormalizeExistingFile(
        string? filePath,
        out string normalizedPath,
        out string error)
    {
        normalizedPath = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            error = "対象ファイルのパスが空です。";
            return false;
        }

        try
        {
            normalizedPath = Path.GetFullPath(filePath);
        }
        catch (Exception)
        {
            error = "対象ファイルのパスが不正です。";
            return false;
        }

        if (Directory.Exists(normalizedPath))
        {
            error = "ディレクトリはプログラムから開くの対象外です。";
            return false;
        }

        if (!File.Exists(normalizedPath))
        {
            error = $"対象ファイルが見つかりません: {normalizedPath}";
            return false;
        }

        return true;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = true)]
    private static extern int SHOpenWithDialog(IntPtr ownerHandle, ref OpenAsInfo info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenAsInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string FilePath;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? FileClass;

        public uint Flags;
    }
}
