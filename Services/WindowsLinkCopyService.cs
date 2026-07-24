using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MidFD.Services;

internal static class WindowsLinkCopyService
{
    private const uint CopyFileCopySymlink = 0x00000800;
    private const uint SymbolicLinkFlagDirectory = 0x1;
    private const uint SymbolicLinkFlagAllowUnprivilegedCreate = 0x2;

    public static void CopyFileSymbolicLink(string sourcePath, string destinationPath)
    {
        if (!CopyFileEx(
                sourcePath,
                destinationPath,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                CopyFileCopySymlink))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"file symbolic link copy failed: {sourcePath}");
        }
    }

    public static void CreateDirectorySymbolicLink(string destinationPath, string targetPath)
    {
        if (CreateSymbolicLink(
                destinationPath,
                targetPath,
                SymbolicLinkFlagDirectory | SymbolicLinkFlagAllowUnprivilegedCreate) == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"directory symbolic link creation failed: {destinationPath}");
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CopyFileEx(
        string existingFileName,
        string newFileName,
        IntPtr progressRoutine,
        IntPtr data,
        IntPtr cancel,
        uint copyFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern byte CreateSymbolicLink(
        string symbolicLinkFileName,
        string targetFileName,
        uint flags);
}
