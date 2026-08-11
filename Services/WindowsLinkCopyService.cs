using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

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

    public static void CopyJunction(string sourcePath, string destinationPath)
    {
        byte[] rawData = ReadReparseData(sourcePath);
        if (Directory.Exists(destinationPath))
        {
            throw new IOException($"junction destination already exists: {destinationPath}");
        }

        Directory.CreateDirectory(destinationPath);
        try
        {
            using SafeFileHandle handle = OpenReparsePoint(destinationPath, 0x40000000u);
            if (!DeviceIoControl(handle, FsctlSetReparsePoint, rawData, rawData.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"junction creation failed: {destinationPath}");
            }
        }
        catch
        {
            try { Directory.Delete(destinationPath, false); } catch { }
            throw;
        }
    }

    private const uint FsctlGetReparsePoint = 0x000900A8;
    private const uint FsctlSetReparsePoint = 0x000900A4;

    private static byte[] ReadReparseData(string path)
    {
        using SafeFileHandle handle = OpenReparsePoint(path, 0x80000000u);
        byte[] buffer = new byte[16 * 1024];
        if (!DeviceIoControl(handle, FsctlGetReparsePoint, IntPtr.Zero, 0, buffer, buffer.Length, out uint returned, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"junction metadata read failed: {path}");
        }
        Array.Resize(ref buffer, checked((int)returned));
        return buffer;
    }

    private static SafeFileHandle OpenReparsePoint(string path, uint access)
    {
        SafeFileHandle handle = CreateFile(path, access, 0x1u | 0x2u, IntPtr.Zero, 3u, 0x02200000u, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"reparse handle open failed: {path}");
        }
        return handle;
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        byte[]? input,
        int inputSize,
        IntPtr output,
        int outputSize,
        out uint bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        IntPtr input,
        int inputSize,
        byte[] output,
        int outputSize,
        out uint bytesReturned,
        IntPtr overlapped);
}
