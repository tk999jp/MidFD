using System.Runtime.InteropServices;

namespace MidFD.MediaFoundation;

// ABI source: Windows SDK 10.0.26100.0 um/mfmediaengine.h and um/mfobjects.h.
// HRESULT methods preserve their native result. Native BOOL/USHORT/double getters
// return values directly, and IMFMediaEngineEx repeats the exact inherited prefix.
internal static partial class MfNative
{
    internal static readonly Guid FactoryClsid = new("b44392da-499b-446b-a4cb-005fead0e6d5");
    internal static readonly Guid CallbackKey = new("c60381b8-83a4-41f8-a3d0-de05076849a9");
    internal static readonly Guid PlaybackHwndKey = new("d988879b-67c9-4d92-baa7-6eadd446039d");
    internal static readonly Guid MfMtMajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    internal static readonly Guid MfMtSubtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    internal static readonly Guid MfMtFrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    internal static readonly Guid MfMtFrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    internal static readonly Guid MfMtAudioSamplesPerSecond = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    internal static readonly Guid MfMtAudioNumChannels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    internal static readonly Guid MfMediaTypeAudio = new("73647561-0000-0010-8000-00aa00389b71");
    internal static readonly Guid MfMediaTypeVideo = new("73646976-0000-0010-8000-00aa00389b71");
    [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFShutdown();
    [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFCreateAttributes(out IMFAttributes attributes, uint size);
    [DllImport("ole32.dll", ExactSpelling = true)] internal static extern int PropVariantClear(ref PropVariant value);
    internal static void Check(int hr, string phase)
    {
        if (hr < 0) throw new COMException($"Media Foundation {phase}: HRESULT=0x{hr:X8}", hr);
    }
    internal static void Release(object? value) { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
}

[StructLayout(LayoutKind.Sequential)] internal struct NativeRect { public int Left, Top, Right, Bottom; }
[StructLayout(LayoutKind.Explicit, Size = 24)] internal struct PropVariant
{
    [FieldOffset(0)] public ushort Type;
    [FieldOffset(8)] public uint UInt32;
    [FieldOffset(8)] public ulong UInt64;
    [FieldOffset(8)] public double Double;
    [FieldOffset(8)] public IntPtr Pointer;
}

[ComVisible(true), Guid("fee7c112-e776-42b5-9bbf-0048524e2bd5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFMediaEngineNotify
{
    [PreserveSig] int EventNotify(uint eventId, nuint parameter1, uint parameter2);
}

[ComImport, Guid("4d645ace-26aa-4688-9be1-df3516990b93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaEngineClassFactory
{
    [PreserveSig] int CreateInstance(uint flags, IMFAttributes attributes, out IMFMediaEngine engine);
    [PreserveSig] int CreateTimeRange(out IMFMediaTimeRange range);
    [PreserveSig] int CreateError(out IMFMediaError error);
}

[ComImport, Guid("fc0e10d2-ab2a-4501-a951-06bb1075184c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaError
{
    [PreserveSig] ushort GetErrorCode();
    [PreserveSig] int GetExtendedErrorCode();
    [PreserveSig] int SetErrorCode(int code);
    [PreserveSig] int SetExtendedErrorCode(int hr);
}

[ComImport, Guid("db71a2fc-078a-414e-9df9-8c2531b0aa6c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaTimeRange
{
    [PreserveSig] uint GetLength();
    [PreserveSig] int GetStart(uint index, out double start);
    [PreserveSig] int GetEnd(uint index, out double end);
    [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool ContainsTime(double time);
    [PreserveSig] int AddRange(double start, double end);
    [PreserveSig] int Clear();
}

[ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFAttributes
{
    [PreserveSig] int GetItem(ref Guid key, IntPtr value);
    [PreserveSig] int GetItemType(ref Guid key, out int type);
    [PreserveSig] int CompareItem(ref Guid key, IntPtr value, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [PreserveSig] int Compare(IMFAttributes other, int match, [MarshalAs(UnmanagedType.Bool)] out bool result);
    [PreserveSig] int GetUINT32(ref Guid key, out uint value);
    [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
    [PreserveSig] int GetDouble(ref Guid key, out double value);
    [PreserveSig] int GetGUID(ref Guid key, out Guid value);
    [PreserveSig] int GetStringLength(ref Guid key, out uint length);
    [PreserveSig] int GetString(ref Guid key, IntPtr buffer, uint length, out uint actual);
    [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out uint length);
    [PreserveSig] int GetBlobSize(ref Guid key, out uint size);
    [PreserveSig] int GetBlob(ref Guid key, IntPtr value, uint size, out uint actual);
    [PreserveSig] int GetAllocatedBlob(ref Guid key, out IntPtr value, out uint length);
    [PreserveSig] int GetUnknown(ref Guid key, ref Guid iid, out IntPtr value);
    [PreserveSig] int SetItem(ref Guid key, IntPtr value);
    [PreserveSig] int DeleteItem(ref Guid key);
    [PreserveSig] int DeleteAllItems();
    [PreserveSig] int SetUINT32(ref Guid key, uint value);
    [PreserveSig] int SetUINT64(ref Guid key, ulong value);
    [PreserveSig] int SetDouble(ref Guid key, double value);
    [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
    [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [PreserveSig] int SetBlob(ref Guid key, IntPtr value, uint size);
    [PreserveSig] int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
    [PreserveSig] int LockStore();
    [PreserveSig] int UnlockStore();
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
    [PreserveSig] int CopyAllItems(IMFAttributes destination);
}

[ComImport, Guid("98a1b0bb-03eb-4935-ae7c-93c1fa0e1c93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaEngine
{

    [PreserveSig] int GetError(out IMFMediaError error);
    [PreserveSig] int SetErrorCode(int error);
    [PreserveSig] int SetSourceElements(IntPtr elements);
    [PreserveSig] int SetSource([MarshalAs(UnmanagedType.BStr)] string url);
    [PreserveSig] int GetCurrentSource([MarshalAs(UnmanagedType.BStr)] out string url);
    [PreserveSig] ushort GetNetworkState();
    [PreserveSig] int GetPreload();
    [PreserveSig] int SetPreload(int preload);
    [PreserveSig] int GetBuffered(out IMFMediaTimeRange ranges);
    [PreserveSig] int Load();
    [PreserveSig] int CanPlayType([MarshalAs(UnmanagedType.BStr)] string type, out int answer);
    [PreserveSig] ushort GetReadyState();
    [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool IsSeeking();
    [PreserveSig] double GetCurrentTime();
    [PreserveSig] int SetCurrentTime(double seconds);
    [PreserveSig] double GetStartTime();
    [PreserveSig] double GetDuration();
    [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool IsPaused();
    [PreserveSig] double GetDefaultPlaybackRate();
    [PreserveSig] int SetDefaultPlaybackRate(double rate);
    [PreserveSig] double GetPlaybackRate();
    [PreserveSig] int SetPlaybackRate(double rate);
    [PreserveSig] int GetPlayed(out IMFMediaTimeRange ranges);
    [PreserveSig] int GetSeekable(out IMFMediaTimeRange ranges);
    [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool IsEnded();
    [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool GetAutoPlay();
    [PreserveSig] int SetAutoPlay([MarshalAs(UnmanagedType.Bool)] bool enabled);
    [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool GetLoop();
    [PreserveSig] int SetLoop([MarshalAs(UnmanagedType.Bool)] bool enabled);
    [PreserveSig] int Play();
    [PreserveSig] int Pause();
    [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool GetMuted();
    [PreserveSig] int SetMuted([MarshalAs(UnmanagedType.Bool)] bool muted);
    [PreserveSig] double GetVolume();
    [PreserveSig] int SetVolume(double volume);
    [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool HasVideo();
    [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool HasAudio();
    [PreserveSig] int GetNativeVideoSize(out uint width, out uint height);
    [PreserveSig] int GetVideoAspectRatio(out uint width, out uint height);
    [PreserveSig] int Shutdown();
    [PreserveSig] int TransferVideoFrame(IntPtr surface, IntPtr source, IntPtr destination, IntPtr border);
    [PreserveSig] int OnVideoStreamTick(out long pts);
}

// Exact SDK vtable order through the stream metadata methods used by Media Engine playback.
[ComImport, Guid("83015ead-b1e6-40d0-a98a-37145ffe1ad1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaEngineEx
{

    [PreserveSig] int GetError(out IMFMediaError error);
    [PreserveSig] int SetErrorCode(int error);
    [PreserveSig] int SetSourceElements(IntPtr elements);
    [PreserveSig] int SetSource([MarshalAs(UnmanagedType.BStr)] string url);
    [PreserveSig] int GetCurrentSource([MarshalAs(UnmanagedType.BStr)] out string url);
    [PreserveSig] ushort GetNetworkState();
    [PreserveSig] int GetPreload();
    [PreserveSig] int SetPreload(int preload);
    [PreserveSig] int GetBuffered(out IMFMediaTimeRange ranges);
    [PreserveSig] int Load();
    [PreserveSig] int CanPlayType([MarshalAs(UnmanagedType.BStr)] string type, out int answer);
    [PreserveSig] ushort GetReadyState();
    [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool IsSeeking();
    [PreserveSig] double GetCurrentTime();
    [PreserveSig] int SetCurrentTime(double seconds);
    [PreserveSig] double GetStartTime();
    [PreserveSig] double GetDuration();
    [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool IsPaused();
    [PreserveSig] double GetDefaultPlaybackRate();
    [PreserveSig] int SetDefaultPlaybackRate(double rate);
    [PreserveSig] double GetPlaybackRate();
    [PreserveSig] int SetPlaybackRate(double rate);
    [PreserveSig] int GetPlayed(out IMFMediaTimeRange ranges);
    [PreserveSig] int GetSeekable(out IMFMediaTimeRange ranges);
    [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool IsEnded();
    [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool GetAutoPlay();
    [PreserveSig] int SetAutoPlay([MarshalAs(UnmanagedType.Bool)] bool enabled);
    [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool GetLoop();
    [PreserveSig] int SetLoop([MarshalAs(UnmanagedType.Bool)] bool enabled);
    [PreserveSig] int Play();
    [PreserveSig] int Pause();
    [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool GetMuted();
    [PreserveSig] int SetMuted([MarshalAs(UnmanagedType.Bool)] bool muted);
    [PreserveSig] double GetVolume();
    [PreserveSig] int SetVolume(double volume);
    [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool HasVideo();
    [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool HasAudio();
    [PreserveSig] int GetNativeVideoSize(out uint width, out uint height);
    [PreserveSig] int GetVideoAspectRatio(out uint width, out uint height);
    [PreserveSig] int Shutdown();
    [PreserveSig] int TransferVideoFrame(IntPtr surface, IntPtr source, IntPtr destination, IntPtr border);
    [PreserveSig] int OnVideoStreamTick(out long pts);

    [PreserveSig] int SetSourceFromByteStream(IntPtr stream, [MarshalAs(UnmanagedType.BStr)] string url);
    [PreserveSig] int GetStatistics(int statistic, out PropVariant value);
    [PreserveSig] int UpdateVideoStream(IntPtr source, ref NativeRect destination, IntPtr border);
    [PreserveSig] double GetBalance();
    [PreserveSig] int SetBalance(double balance);
    [PreserveSig] [return: MarshalAs(UnmanagedType.Bool)] bool IsPlaybackRateSupported(double rate);
    [PreserveSig] int FrameStep([MarshalAs(UnmanagedType.Bool)] bool forward);
    [PreserveSig] int GetResourceCharacteristics(out uint characteristics);
    [PreserveSig] int GetPresentationAttribute(ref Guid attribute, out PropVariant value);
    [PreserveSig] int GetNumberOfStreams(out uint streamCount);
    [PreserveSig] int GetStreamAttribute(uint streamIndex, ref Guid attribute, out PropVariant value);
    [PreserveSig] int GetStreamSelection(uint streamIndex, [MarshalAs(UnmanagedType.Bool)] out bool enabled);
}
