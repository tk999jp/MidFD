using System.Runtime.InteropServices;

namespace MidFD.MediaFoundation;

// Windows SDK 10.0.26100.0 mfapi.h: MFTEnumEx takes category GUID by value,
// an optional MFT_REGISTER_TYPE_INFO, and a CoTaskMem array of IMFActivate*.
internal static partial class MfNative
{
    internal static readonly Guid VideoDecoderCategory = new("d6c02d4b-6833-45b4-971a-05a4b04bab91");
    internal static readonly Guid AudioDecoderCategory = new("9ea73fb4-ef7a-4559-8d5d-719d8f0426c7");
    internal static readonly Guid VideoMajorType = new("73646976-0000-0010-8000-00aa00389b71");
    internal static readonly Guid AudioMajorType = new("73647561-0000-0010-8000-00aa00389b71");
    [DllImport("mfplat.dll", ExactSpelling = true)]
    internal static extern int MFTEnumEx(Guid category, uint flags, ref MftRegisterTypeInfo input,
        IntPtr output, out IntPtr activations, out uint count);
}

[StructLayout(LayoutKind.Sequential)]
internal struct MftRegisterTypeInfo { public Guid MajorType, Subtype; }
