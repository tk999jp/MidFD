using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MidFD.FileOperationHelper;

public static class ElevatedLinkCopyCore
{
    public static bool TryCreateDirectoryPlaceholder(string path)
    {
        if (CreateDirectory(path, IntPtr.Zero)) return true;
        int error = Marshal.GetLastWin32Error();
        if (error == 183) return false;
        throw new Win32Exception(error, "junction placeholder creation failed");
    }

    public static void DeleteOwnedEmptyDirectory(string path)
    {
        Directory.Delete(path, false);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string path, IntPtr securityAttributes);
}
