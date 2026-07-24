using System.IO;

namespace MidFD.Services;

internal static class ReparsePointHelper
{
    public static bool Exists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            // 属性取得失敗時は安全側でリンク相当として扱う。
            return true;
        }
    }

    public static bool IsDirectory(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.Directory) != 0;
        }
        catch
        {
            return Directory.Exists(path);
        }
    }

    public static bool IsDirectoryContainer(string path)
    {
        return IsDirectory(path) && !IsReparsePoint(path);
    }

    public static string GetLinkTarget(string path)
    {
        FileSystemInfo info = IsDirectory(path) ? new DirectoryInfo(path) : new FileInfo(path);
        return info.LinkTarget ?? throw new IOException($"リンク先を取得できません: {path}");
    }

    public static uint GetReparseTag(string path)
    {
        WIN32_FIND_DATA data;
        IntPtr handle = FindFirstFile(path, out data);
        if (handle == new IntPtr(-1)) throw new IOException($"reparse tagを取得できません: {path}");
        try { return data.Reserved0; }
        finally { FindClose(handle); }
    }

    public static bool ShouldRecurseIntoDirectory(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            return (attrs & FileAttributes.Directory) != 0
                && (attrs & FileAttributes.ReparsePoint) == 0;
        }
        catch
        {
            return false;
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstFile(string fileName, out WIN32_FIND_DATA data);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr findFile);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct WIN32_FIND_DATA
    {
        public FileAttributes FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint Reserved0;
        public uint Reserved1;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)] public string FileName;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 14)] public string AlternateFileName;
    }
}
