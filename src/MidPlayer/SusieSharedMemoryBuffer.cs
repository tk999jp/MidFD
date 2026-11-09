using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MidFD;

internal sealed class SusieSharedMemoryBuffer : IDisposable
{
    private const uint PageReadWrite = 0x04;
    private const uint FileMapRead = 0x0004;
    private const uint FileMapWrite = 0x0002;
    private const int MaximumLength = 1024 * 1024 * 1024;
    private IntPtr _handle;

    private SusieSharedMemoryBuffer(IntPtr handle, int length)
    {
        _handle = handle;
        Length = length;
    }

    internal int Length { get; }
    internal IntPtr Handle => _handle;

    internal static SusieSharedMemoryBuffer Create(int length)
    {
        if (length <= 0 || length > MaximumLength) throw new ArgumentOutOfRangeException(nameof(length));
        IntPtr handle = CreateFileMappingW(new IntPtr(-1), IntPtr.Zero, PageReadWrite,
            unchecked((uint)((ulong)length >> 32)), unchecked((uint)length), null);
        if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateFileMapping failed.");
        return new SusieSharedMemoryBuffer(handle, length);
    }

    internal void Write(byte[] data)
    {
        if (data.Length > Length) throw new ArgumentOutOfRangeException(nameof(data));
        IntPtr view = MapViewOfFile(_handle, FileMapWrite, 0, 0, new UIntPtr((uint)data.Length));
        if (view == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "MapViewOfFile(write) failed.");
        try { Marshal.Copy(data, 0, view, data.Length); }
        finally { UnmapViewOfFile(view); }
    }

    internal byte[] Read()
    {
        IntPtr view = MapViewOfFile(_handle, FileMapRead, 0, 0, new UIntPtr((uint)Length));
        if (view == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "MapViewOfFile(read) failed.");
        try
        {
            byte[] data = new byte[Length];
            Marshal.Copy(view, data, 0, data.Length);
            return data;
        }
        finally { UnmapViewOfFile(view); }
    }

    public void Dispose()
    {
        IntPtr handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero) CloseHandle(handle);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileMappingW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileMappingW(IntPtr file, IntPtr securityAttributes, uint protection, uint maximumSizeHigh, uint maximumSizeLow, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(IntPtr mapping, uint desiredAccess, uint offsetHigh, uint offsetLow, UIntPtr bytesToMap);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UnmapViewOfFile(IntPtr address);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
