using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;

namespace MidFD.Helpers;

/// <summary>
/// Browser 一覧で使う小アイコンを軽量に取得・キャッシュする。
/// Explorer 風の大アイコン化は行わず、拡張子単位の小アイコンだけを扱う。
/// </summary>
public static class BrowserItemIconProvider
{
    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint FILE_ATTRIBUTE_FILE = 0x00000080;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, Icon> IconCache = new(StringComparer.OrdinalIgnoreCase);

    public static Icon GetSmallIcon(string? fullPath, bool isDirectory)
    {
        string cacheKey = BuildCacheKey(fullPath, isDirectory);

        lock (SyncRoot)
        {
            if (IconCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            var icon = LoadShellIcon(fullPath, isDirectory) ?? SystemIcons.Application;
            IconCache[cacheKey] = icon;
            return icon;
        }
    }

    private static string BuildCacheKey(string? fullPath, bool isDirectory)
    {
        if (isDirectory)
        {
            return "<DIR>";
        }

        string extension = Path.GetExtension(fullPath ?? string.Empty);
        if (string.IsNullOrWhiteSpace(extension))
        {
            return "<FILE>";
        }

        return extension.ToLowerInvariant();
    }

    private static Icon? LoadShellIcon(string? fullPath, bool isDirectory)
    {
        string iconSource = isDirectory
            ? "folder"
            : GetShellLookupSource(fullPath);

        uint fileAttributes = isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_FILE;
        SHFILEINFO shinfo = new();
        IntPtr result = SHGetFileInfo(
            iconSource,
            fileAttributes,
            ref shinfo,
            (uint)Marshal.SizeOf<SHFILEINFO>(),
            SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);

        if (result == IntPtr.Zero || shinfo.hIcon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            using var shellIcon = Icon.FromHandle(shinfo.hIcon);
            return (Icon)shellIcon.Clone();
        }
        finally
        {
            DestroyIcon(shinfo.hIcon);
        }
    }

    private static string GetShellLookupSource(string? fullPath)
    {
        string extension = Path.GetExtension(fullPath ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(extension))
        {
            return extension;
        }

        return "file";
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }
}
