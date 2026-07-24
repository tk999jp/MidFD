using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MidFD.FileOperationHelper;

internal enum HelperReparseKind
{
    FileSymbolicLink,
    DirectorySymbolicLink,
    Junction,
    Unsupported
}

internal sealed record HelperReparseInfo(HelperReparseKind Kind, uint Tag, string RawTarget, byte[]? RawData = null);

internal static class HelperReparsePointService
{
    private const uint IoReparseTagSymLink = 0xA000000C;
    private const uint IoReparseTagMountPoint = 0xA0000003;
    private const uint FsctlGetReparsePoint = 0x000900A8;
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    public static HelperReparseInfo Read(string path)
    {
        WIN32_FIND_DATA data;
        IntPtr findHandle = FindFirstFile(path, out data);
        if (findHandle == InvalidHandleValue) throw new IOException($"source link metadata unavailable: {path}");
        try
        {
            if ((data.FileAttributes & FileAttributes.ReparsePoint) == 0) throw new IOException("source is not a reparse point");
            bool isDirectory = (data.FileAttributes & FileAttributes.Directory) != 0;
            HelperReparseKind kind = data.Reserved0 switch
            {
                IoReparseTagSymLink when isDirectory => HelperReparseKind.DirectorySymbolicLink,
                IoReparseTagSymLink => HelperReparseKind.FileSymbolicLink,
                IoReparseTagMountPoint when isDirectory => HelperReparseKind.Junction,
                _ => HelperReparseKind.Unsupported
            };
            if (kind == HelperReparseKind.Junction)
            {
                byte[] rawData = ReadReparseData(path);
                return new HelperReparseInfo(kind, data.Reserved0, ReadMountPointTarget(rawData), rawData);
            }
            FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
            return new HelperReparseInfo(kind, data.Reserved0, info.LinkTarget ?? throw new IOException("raw link target unavailable"));
        }
        finally { FindClose(findHandle); }
    }

    public static void CreateJunction(string destination, byte[] rawData)
    {
        if (!ElevatedLinkCopyCore.TryCreateDirectoryPlaceholder(destination))
            throw new IOException("junction destination already exists");

        try
        {
            using SafeFileHandle handle = OpenReparsePoint(destination, 0x40000000u);
            if (!DeviceIoControl(handle, FsctlSetReparsePoint, rawData, rawData.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
                throw new IOException($"junction creation failed: {Marshal.GetLastWin32Error()}");
        }
        catch
        {
            try { ElevatedLinkCopyCore.DeleteOwnedEmptyDirectory(destination); } catch { }
            throw;
        }
    }

    private static byte[] ReadReparseData(string path)
    {
        using SafeFileHandle handle = OpenReparsePoint(path, 0x80000000u);
        byte[] buffer = new byte[16 * 1024];
        if (!DeviceIoControl(handle, FsctlGetReparsePoint, IntPtr.Zero, 0, buffer, buffer.Length, out uint returned, IntPtr.Zero))
            throw new IOException($"junction metadata unavailable: {Marshal.GetLastWin32Error()}");
        Array.Resize(ref buffer, checked((int)returned));
        return buffer;
    }

    internal static string ReadMountPointTarget(byte[] rawData)
    {
        if (rawData.Length < 16) throw new IOException("invalid junction reparse data");
        ushort substituteOffset = BitConverter.ToUInt16(rawData, 8);
        ushort substituteLength = BitConverter.ToUInt16(rawData, 10);
        ushort printOffset = BitConverter.ToUInt16(rawData, 12);
        ushort printLength = BitConverter.ToUInt16(rawData, 14);
        int pathBufferLength = rawData.Length - 16;
        ValidateMountPointComponent(substituteOffset, substituteLength, pathBufferLength, "substitute");
        ValidateMountPointComponent(printOffset, printLength, pathBufferLength, "print");
        string printName = ReadMountPointComponent(rawData, printOffset, printLength);
        if (!string.IsNullOrEmpty(printName)) return printName;

        string substituteName = ReadMountPointComponent(rawData, substituteOffset, substituteLength);
        if (string.IsNullOrEmpty(substituteName))
            throw new IOException("invalid junction target");
        return NormalizeSubstituteName(substituteName);
    }

    private static void ValidateMountPointComponent(ushort offset, ushort length, int pathBufferLength, string componentName)
    {
        if ((offset % 2) != 0 || (length % 2) != 0 || offset > pathBufferLength || length > pathBufferLength - offset)
            throw new IOException($"invalid junction {componentName} name");
    }

    private static string ReadMountPointComponent(byte[] rawData, ushort offset, ushort length)
    {
        return System.Text.Encoding.Unicode.GetString(rawData, 16 + offset, length);
    }

    private static string NormalizeSubstituteName(string substituteName)
    {
        if (substituteName.StartsWith(@"\??\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\" + substituteName[7..];
        if (substituteName.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase))
            return substituteName[4..];
        if (substituteName.StartsWith(@"\DosDevices\", StringComparison.OrdinalIgnoreCase))
            return substituteName[12..];
        return substituteName;
    }

    private static SafeFileHandle OpenReparsePoint(string path, uint access)
    {
        SafeFileHandle handle = CreateFile(path, access, 0x1u | 0x2u, IntPtr.Zero, 3u, 0x02200000u, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException($"reparse handle open failed: {Marshal.GetLastWin32Error()}");
        return handle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstFile(string fileName, out WIN32_FIND_DATA data);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FindClose(IntPtr findFile);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
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
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string FileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string AlternateFileName;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode, byte[]? input, int inputSize,
        IntPtr output, int outputSize, out uint bytesReturned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode, IntPtr input, int inputSize,
        byte[] output, int outputSize, out uint bytesReturned, IntPtr overlapped);
}
