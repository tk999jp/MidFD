using System.Buffers.Binary;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace MidFD;

internal enum SusieSpiDecodeStatus
{
    Decoded,
    ArchitectureMismatch,
    PluginNotFound,
    PluginLoadFailed,
    RequiredExportMissing,
    NotImagePlugin,
    InputFileFailed,
    PathNotRepresentable,
    Unsupported,
    PluginError,
    InvalidDib,
    UnsupportedDib,
    ManagedException,
    MemoryReleaseFailed,
    ModuleReleaseFailed
}

internal sealed record SusieSpiAttemptSummary(
    SusieSpiDecodeStatus Status,
    int? PluginErrorCode,
    bool UsedUnicodePath,
    bool? OutputHandlesReleased,
    string Detail);

internal sealed record SusieSpiDecodeResult(Bitmap? Image, SusieSpiAttemptSummary Attempt);

internal sealed record SusieSpiPluginMetadata(string DisplayName, string VersionInfo, IReadOnlyList<string> Extensions, IReadOnlyList<string> FormatNames);

internal sealed record SusieSpiViewerObservation(
    bool Is32BitProcess,
    string? CurrentPath,
    bool HasLoadedImage,
    int? ImageWidth,
    int? ImageHeight,
    int? TopLeftArgb,
    SusieSpiAttemptSummary? SusieAttempt);

/// <summary>
/// Susie image Import Filter decoder. This source is compiled only into the architecture-matched host.
/// MidPlayer must not load third-party SPI/SPH modules directly.
/// </summary>
internal static class SusieSpiDecoder
{
    private const uint LoadLibrarySearchDllLoadDir = 0x00000100;
    private const uint LoadLibrarySearchDefaultDirs = 0x00001000;
    private const int HeaderBufferLength = 2048;
    private const int BitmapInfoHeaderSize = 40;
    private const int MaximumBitmapInfoBytes = 1024 * 1024;
    private const int MaximumPixelBytes = 256 * 1024 * 1024;
    private const int MaximumDimension = 32768;
    private const uint BiRgb = 0;
    private const uint BiRle8 = 1;
    private const uint BiRle4 = 2;
    private const uint BiBitfields = 3;

    private static readonly SusieProgressCallback ProgressCallback = static (_, _, _) => 0;

    internal static SusieSpiDecodeResult Decode(string pluginPath, string imagePath)
        => DecodeCore(pluginPath, imagePath, null);

    internal static SusieSpiDecodeResult DecodeMemory(string pluginPath, byte[] imageBytes, string pathHint)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        if (imageBytes.Length == 0 || imageBytes.Length > 1024 * 1024 * 1024)
        {
            return Failure(SusieSpiDecodeStatus.InputFileFailed, "Input byte length is outside the supported host limit.");
        }
        return DecodeCore(pluginPath, string.IsNullOrWhiteSpace(pathHint) ? "input.bin" : Path.GetFileName(pathHint), imageBytes);
    }

    internal static SusieSpiPluginMetadata Inspect(string pluginPath)
    {
        string extension = Path.GetExtension(pluginPath);
        if ((extension.Equals(".spi", StringComparison.OrdinalIgnoreCase) && Environment.Is64BitProcess)
            || (extension.Equals(".sph", StringComparison.OrdinalIgnoreCase) && !Environment.Is64BitProcess)
            || (!extension.Equals(".spi", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".sph", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException($"Plugin extension {extension} does not match the {IntPtr.Size * 8}-bit Susie host architecture.");
        }

        string fullPluginPath = Path.GetFullPath(pluginPath);
        IntPtr module = LoadLibraryExW(fullPluginPath, IntPtr.Zero, LoadLibrarySearchDllLoadDir | LoadLibrarySearchDefaultDirs);
        if (module == IntPtr.Zero)
        {
            throw new IOException($"LoadLibraryEx failed: {Marshal.GetLastWin32Error()}");
        }

        try
        {
            IntPtr infoAddress = GetProcAddress(module, "GetPluginInfo");
            IntPtr infoWideAddress = GetProcAddress(module, "GetPluginInfoW");
            IntPtr supportedAddress = GetProcAddress(module, "IsSupported");
            IntPtr pictureAddress = GetProcAddress(module, "GetPicture");
            if (infoAddress == IntPtr.Zero || supportedAddress == IntPtr.Zero || pictureAddress == IntPtr.Zero)
                throw new InvalidDataException("Required GetPluginInfo, IsSupported, or GetPicture export is missing.");

            var infoA = Marshal.GetDelegateForFunctionPointer<GetPluginInfoA>(infoAddress);
            if (!IsImagePlugin(infoA)) throw new InvalidDataException("GetPluginInfo(0) is not the 00IN image Import Filter type.");
            var infoW = infoWideAddress == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<GetPluginInfoW>(infoWideAddress);
            string ReadInfo(int infoNo)
            {
                if (infoW != null) return ReadPluginInfo(infoW, infoNo, Encoding.Unicode, 1024);
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return ReadPluginInfo(infoA, infoNo, Encoding.GetEncoding(checked((int)GetACP()), EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback), 512);
            }

            string name = ReadInfo(1);
            var extensions = new List<string>();
            var formats = new List<string>();
            for (int index = 1; index <= 64; index++)
            {
                string extensionList = ReadInfo(index * 2);
                if (extensionList.Length == 0) break;
                string format = ReadInfo(index * 2 + 1);
                extensions.AddRange(extensionList.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(value => value.StartsWith("*.", StringComparison.Ordinal)
                        ? value[1..]
                        : value.Length > 0 && value[0] == '.'
                            ? value
                            : Path.GetExtension(value))
                    .Where(value => value.Length > 1)
                    .Select(NormalizeExtension));
                formats.Add(format);
            }
            return new SusieSpiPluginMetadata(name, ReadInfo(1), extensions.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), formats);
        }
        finally
        {
            if (!FreeLibrary(module)) throw new IOException($"FreeLibrary failed: {Marshal.GetLastWin32Error()}");
        }
    }

    private static SusieSpiDecodeResult DecodeCore(string pluginPath, string imagePath, byte[]? memoryInput)
    {
        string extension = Path.GetExtension(pluginPath);
        if ((extension.Equals(".spi", StringComparison.OrdinalIgnoreCase) && Environment.Is64BitProcess)
            || (extension.Equals(".sph", StringComparison.OrdinalIgnoreCase) && !Environment.Is64BitProcess)
            || (!extension.Equals(".spi", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".sph", StringComparison.OrdinalIgnoreCase)))
        {
            return Failure(SusieSpiDecodeStatus.ArchitectureMismatch,
                $"Plugin extension {extension} does not match the {IntPtr.Size * 8}-bit Susie host architecture.");
        }

        string fullPluginPath;
        string fullImagePath;
        try
        {
            fullPluginPath = Path.GetFullPath(pluginPath);
            fullImagePath = memoryInput == null
                ? Path.GetFullPath(imagePath)
                : @"C:\MidFD\" + Path.GetFileName(imagePath);
        }
        catch (Exception ex)
        {
            return Failure(SusieSpiDecodeStatus.InputFileFailed, ex.Message);
        }

        if (!File.Exists(fullPluginPath))
        {
            return Failure(SusieSpiDecodeStatus.PluginNotFound, $"Explicit SPI path does not exist: {fullPluginPath}");
        }

        byte[] header;
        try
        {
            header = memoryInput == null ? ReadHeader(fullImagePath) : ReadHeader(memoryInput);
        }
        catch (Exception ex)
        {
            return Failure(SusieSpiDecodeStatus.InputFileFailed, ex.Message);
        }

        IntPtr module = LoadLibraryExW(
            fullPluginPath,
            IntPtr.Zero,
            LoadLibrarySearchDllLoadDir | LoadLibrarySearchDefaultDirs);
        if (module == IntPtr.Zero)
        {
            return Failure(SusieSpiDecodeStatus.PluginLoadFailed, $"LoadLibraryEx failed: {Marshal.GetLastWin32Error()}");
        }

        SusieSpiDecodeResult result;
        bool moduleReleased;
        try
        {
            result = DecodeWithModule(module, fullImagePath, header, memoryInput);
        }
        catch (Exception ex)
        {
            result = Failure(SusieSpiDecodeStatus.ManagedException, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            moduleReleased = FreeLibrary(module);
        }
        if (!moduleReleased)
        {
            result.Image?.Dispose();
            return new SusieSpiDecodeResult(null, result.Attempt with
            {
                Status = SusieSpiDecodeStatus.ModuleReleaseFailed,
                Detail = $"FreeLibrary failed. {result.Attempt.Detail}"
            });
        }
        return result;
    }

    private static SusieSpiDecodeResult DecodeWithModule(IntPtr module, string imagePath, byte[] header, byte[]? memoryInput = null)
    {
        IntPtr getPluginInfoAddress = GetProcAddress(module, "GetPluginInfo");
        IntPtr isSupportedAddress = GetProcAddress(module, "IsSupported");
        IntPtr getPictureAddress = GetProcAddress(module, "GetPicture");
        if (getPluginInfoAddress == IntPtr.Zero || isSupportedAddress == IntPtr.Zero || getPictureAddress == IntPtr.Zero)
        {
            return Failure(
                SusieSpiDecodeStatus.RequiredExportMissing,
                "The 32-bit required exports GetPluginInfo, IsSupported, and GetPicture must all be present.");
        }

        var getPluginInfo = Marshal.GetDelegateForFunctionPointer<GetPluginInfoA>(getPluginInfoAddress);
        var isSupported = Marshal.GetDelegateForFunctionPointer<IsSupportedA>(isSupportedAddress);
        var getPicture = Marshal.GetDelegateForFunctionPointer<GetPictureA>(getPictureAddress);
        if (!IsImagePlugin(getPluginInfo))
        {
            return Failure(SusieSpiDecodeStatus.NotImagePlugin, "GetPluginInfo(0) did not identify a 00IN image import filter.");
        }

        IntPtr isSupportedWideAddress = GetProcAddress(module, "IsSupportedW");
        IntPtr getPictureWideAddress = GetProcAddress(module, "GetPictureW");
        bool useWidePath = isSupportedWideAddress != IntPtr.Zero && getPictureWideAddress != IntPtr.Zero;
        SusieSpiDecodeResult result;
        IntPtr infoHandle = IntPtr.Zero;
        IntPtr bitsHandle = IntPtr.Zero;
        bool outputHandlesReturned = false;
        bool infoReleased = true;
        bool bitsReleased = true;
        IntPtr widePathPointer = IntPtr.Zero;
        IntPtr imageInputPointer = IntPtr.Zero;

        try
        {
            IntPtr pathPointer;
            IntPtr headerPointer = IntPtr.Zero;
            IntPtr ansiPathPointer = IntPtr.Zero;
            try
            {
                if (useWidePath)
                {
                    widePathPointer = Marshal.StringToHGlobalUni(imagePath);
                    pathPointer = widePathPointer;
                }
                else
                {
                    if (!TryEncodeActiveCodePagePath(imagePath, out byte[]? pathBytes, out string pathError))
                    {
                        result = Failure(SusieSpiDecodeStatus.PathNotRepresentable, pathError);
                        goto Done;
                    }

                    ansiPathPointer = Marshal.AllocHGlobal(pathBytes.Length + 1);
                    Marshal.Copy(pathBytes, 0, ansiPathPointer, pathBytes.Length);
                    Marshal.WriteByte(ansiPathPointer, pathBytes.Length, 0);
                    pathPointer = ansiPathPointer;
                }

                headerPointer = Marshal.AllocHGlobal(HeaderBufferLength);
                Marshal.Copy(header, 0, headerPointer, HeaderBufferLength);
                int supported;
                if (useWidePath)
                {
                    var wide = Marshal.GetDelegateForFunctionPointer<IsSupportedW>(isSupportedWideAddress);
                    supported = wide(pathPointer, headerPointer);
                }
                else
                {
                    supported = isSupported(pathPointer, headerPointer);
                }

                if (supported == 0)
                {
                    result = new SusieSpiDecodeResult(null, new SusieSpiAttemptSummary(
                        SusieSpiDecodeStatus.Unsupported, 0, useWidePath, null, "IsSupported returned 0."));
                    goto Done;
                }

                int pluginCode;
                if (useWidePath)
                {
                    var wide = Marshal.GetDelegateForFunctionPointer<GetPictureW>(getPictureWideAddress);
                    if (memoryInput == null)
                    {
                        pluginCode = wide(pathPointer, IntPtr.Zero, 0, out infoHandle, out bitsHandle, ProgressCallback, IntPtr.Zero);
                    }
                    else
                    {
                        imageInputPointer = Marshal.AllocHGlobal(memoryInput.Length);
                        Marshal.Copy(memoryInput, 0, imageInputPointer, memoryInput.Length);
                        pluginCode = wide(imageInputPointer, new IntPtr(memoryInput.Length), 1, out infoHandle, out bitsHandle, ProgressCallback, IntPtr.Zero);
                    }
                }
                else
                {
                    if (memoryInput == null)
                    {
                        pluginCode = getPicture(pathPointer, IntPtr.Zero, 0, out infoHandle, out bitsHandle, ProgressCallback, IntPtr.Zero);
                    }
                    else
                    {
                        imageInputPointer = Marshal.AllocHGlobal(memoryInput.Length);
                        Marshal.Copy(memoryInput, 0, imageInputPointer, memoryInput.Length);
                        pluginCode = getPicture(imageInputPointer, new IntPtr(memoryInput.Length), 1, out infoHandle, out bitsHandle, ProgressCallback, IntPtr.Zero);
                    }
                }

                outputHandlesReturned = infoHandle != IntPtr.Zero || bitsHandle != IntPtr.Zero;
                if (pluginCode != 0)
                {
                    result = new SusieSpiDecodeResult(null, new SusieSpiAttemptSummary(
                        SusieSpiDecodeStatus.PluginError,
                        pluginCode,
                        useWidePath,
                        null,
                        $"GetPicture returned Susie error code {pluginCode}."));
                    goto Done;
                }

                if (infoHandle == IntPtr.Zero || bitsHandle == IntPtr.Zero || infoHandle == bitsHandle)
                {
                    result = Failure(SusieSpiDecodeStatus.InvalidDib, "GetPicture returned null or aliased bitmap handles.", useWidePath);
                    goto Done;
                }

                DibConversionResult conversion = ConvertDibHandles(infoHandle, bitsHandle);
                result = new SusieSpiDecodeResult(
                    conversion.Image,
                    new SusieSpiAttemptSummary(
                        conversion.Status,
                        null,
                        useWidePath,
                        null,
                        conversion.Detail));
            }
            finally
            {
                if (headerPointer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(headerPointer);
                }
                if (ansiPathPointer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(ansiPathPointer);
                }
                if (widePathPointer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(widePathPointer);
                }
                if (imageInputPointer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(imageInputPointer);
                }
            }
        }
        finally
        {
            if (infoHandle != IntPtr.Zero)
            {
                infoReleased = LocalFree(infoHandle) == IntPtr.Zero;
            }
            if (bitsHandle != IntPtr.Zero)
            {
                if (bitsHandle == infoHandle)
                {
                    bitsReleased = infoReleased;
                }
                else
                {
                    bitsReleased = LocalFree(bitsHandle) == IntPtr.Zero;
                }
            }
        }

    Done:
        if (outputHandlesReturned)
        {
            bool released = infoReleased && bitsReleased;
            if (!released)
            {
                result.Image?.Dispose();
                result = new SusieSpiDecodeResult(null, result.Attempt with
                {
                    Status = SusieSpiDecodeStatus.MemoryReleaseFailed,
                    OutputHandlesReleased = false,
                    Detail = $"LocalFree failed after decode. {result.Attempt.Detail}"
                });
            }
            else
            {
                result = result with { Attempt = result.Attempt with { OutputHandlesReleased = true } };
            }
        }
        return result;
    }

    private static bool IsImagePlugin(GetPluginInfoA getPluginInfo)
    {
        IntPtr buffer = Marshal.AllocHGlobal(16);
        try
        {
            for (int i = 0; i < 16; i++) Marshal.WriteByte(buffer, i, 0);
            int returned = getPluginInfo(0, buffer, 16);
            if (returned < 4) return false;
            byte[] bytes = new byte[4];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            return bytes[0] == (byte)'0' && bytes[1] == (byte)'0' && bytes[2] == (byte)'I' && bytes[3] == (byte)'N';
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static byte[] ReadHeader(string path)
    {
        byte[] header = new byte[HeaderBufferLength];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        int read = 0;
        while (read < header.Length)
        {
            int count = stream.Read(header, read, header.Length - read);
            if (count == 0) break;
            read += count;
        }
        return header;
    }

    private static byte[] ReadHeader(byte[] imageBytes)
    {
        byte[] header = new byte[HeaderBufferLength];
        Buffer.BlockCopy(imageBytes, 0, header, 0, Math.Min(imageBytes.Length, header.Length));
        return header;
    }

    private static string ReadPluginInfo(GetPluginInfoA getPluginInfo, int infoNo, Encoding encoding, int bufferLength)
    {
        byte[] buffer = new byte[bufferLength];
        IntPtr pointer = Marshal.AllocHGlobal(buffer.Length);
        try
        {
            for (int index = 0; index < bufferLength; index++) Marshal.WriteByte(pointer, index, 0);
            int returned = getPluginInfo(infoNo, pointer, bufferLength);
            if (returned <= 0) return string.Empty;
            int length = Math.Min(returned, buffer.Length);
            Marshal.Copy(pointer, buffer, 0, length);
            int terminator = Array.IndexOf(buffer, (byte)0, 0, length);
            if (terminator >= 0) length = terminator;
            return encoding.GetString(buffer, 0, length).Trim();
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static string ReadPluginInfo(GetPluginInfoW getPluginInfo, int infoNo, Encoding encoding, int bufferLength)
    {
        byte[] buffer = new byte[checked(bufferLength * 2)];
        IntPtr pointer = Marshal.AllocHGlobal(buffer.Length);
        try
        {
            for (int index = 0; index < buffer.Length; index++) Marshal.WriteByte(pointer, index, 0);
            int returned = getPluginInfo(infoNo, pointer, bufferLength);
            if (returned <= 0) return string.Empty;
            int length = Math.Min(checked(returned * 2), buffer.Length);
            Marshal.Copy(pointer, buffer, 0, length);
            for (int index = 0; index + 1 < length; index += 2)
            {
                if (buffer[index] == 0 && buffer[index + 1] == 0)
                {
                    length = index;
                    break;
                }
            }
            return encoding.GetString(buffer, 0, length).Trim();
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private static string NormalizeExtension(string extension)
    {
        string value = extension.Trim();
        if (value.Length == 0) return string.Empty;
        return value[0] == '.' ? value : "." + value;
    }

    private static bool TryEncodeActiveCodePagePath(string path, out byte[] bytes, out string error)
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            uint codePage = GetACP();
            Encoding encoding = Encoding.GetEncoding(
                checked((int)codePage),
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
            bytes = encoding.GetBytes(path);
            if (!string.Equals(encoding.GetString(bytes), path, StringComparison.Ordinal))
            {
                bytes = Array.Empty<byte>();
                error = $"Image path does not round-trip through Windows ANSI code page {codePage}.";
                return false;
            }
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            bytes = Array.Empty<byte>();
            error = $"Image path cannot be encoded using the active Windows ANSI code page: {ex.Message}";
            return false;
        }
    }

    private static DibConversionResult ConvertDibHandles(IntPtr infoHandle, IntPtr bitsHandle)
    {
        uint infoSize = LocalSize(infoHandle);
        uint bitsSize = LocalSize(bitsHandle);
        if (infoSize < BitmapInfoHeaderSize || infoSize > MaximumBitmapInfoBytes || bitsSize == 0 || bitsSize > MaximumPixelBytes + 4096u)
        {
            return DibFailure(SusieSpiDecodeStatus.InvalidDib, "LocalSize returned invalid BITMAPINFO or bitmap-data allocation sizes.");
        }

        IntPtr infoPointer = LocalLock(infoHandle);
        if (infoPointer == IntPtr.Zero)
        {
            return DibFailure(SusieSpiDecodeStatus.InvalidDib, "LocalLock failed for the BITMAPINFO handle.");
        }

        IntPtr bitsPointer = IntPtr.Zero;
        try
        {
            byte[] infoBytes = new byte[checked((int)infoSize)];
            Marshal.Copy(infoPointer, infoBytes, 0, infoBytes.Length);
            DibHeader header = ReadDibHeader(infoBytes);
            DibValidation validation = ValidateDibHeader(header, infoBytes.Length, bitsSize);
            if (validation.Status != SusieSpiDecodeStatus.Decoded)
            {
                return DibFailure(validation.Status, validation.Detail);
            }

            int bytesToCopy = checked((int)(header.SizeImage == 0 ? validation.RequiredPixelBytes : header.SizeImage));
            bitsPointer = LocalLock(bitsHandle);
            if (bitsPointer == IntPtr.Zero)
            {
                return DibFailure(SusieSpiDecodeStatus.InvalidDib, "LocalLock failed for the bitmap-data handle.");
            }

            byte[] pixelBytes = new byte[bytesToCopy];
            Marshal.Copy(bitsPointer, pixelBytes, 0, bytesToCopy);
            if (!TryCreateBitmapFromDib(infoBytes, pixelBytes, out Bitmap? bitmap, out SusieSpiDecodeStatus status, out string detail))
            {
                return DibFailure(status, detail);
            }

            return new DibConversionResult(bitmap, SusieSpiDecodeStatus.Decoded, detail);
        }
        catch (Exception ex)
        {
            return DibFailure(SusieSpiDecodeStatus.InvalidDib, $"DIB conversion failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (bitsPointer != IntPtr.Zero) LocalUnlock(bitsHandle);
            LocalUnlock(infoHandle);
        }
    }

    internal static bool TryCreateBitmapFromDib(
        byte[] bitmapInfo,
        byte[] bitmapData,
        out Bitmap? bitmap,
        out SusieSpiDecodeStatus status,
        out string detail)
    {
        bitmap = null;
        if (bitmapInfo.Length < BitmapInfoHeaderSize)
        {
            status = SusieSpiDecodeStatus.InvalidDib;
            detail = "BITMAPINFO is shorter than BITMAPINFOHEADER.";
            return false;
        }

        DibHeader header = ReadDibHeader(bitmapInfo);
        DibValidation validation = ValidateDibHeader(header, bitmapInfo.Length, checked((uint)bitmapData.Length));
        if (validation.Status != SusieSpiDecodeStatus.Decoded)
        {
            status = validation.Status;
            detail = validation.Detail;
            return false;
        }

        int pixelLength = checked((int)(header.SizeImage == 0 ? validation.RequiredPixelBytes : header.SizeImage));
        if (bitmapData.Length < pixelLength)
        {
            status = SusieSpiDecodeStatus.InvalidDib;
            detail = "Bitmap data is shorter than the validated DIB image size.";
            return false;
        }

        try
        {
            int paletteBytes = header.BitsPerPixel == 8
                ? checked((int)(header.ClrUsed == 0 ? 256u : header.ClrUsed) * 4)
                : 0;
            int pixelOffset = checked(14 + BitmapInfoHeaderSize + paletteBytes);
            int totalSize = checked(pixelOffset + pixelLength);
            using var stream = new MemoryStream(totalSize);
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write((ushort)0x4D42);
                writer.Write(totalSize);
                writer.Write((ushort)0);
                writer.Write((ushort)0);
                writer.Write(checked((uint)pixelOffset));
                writer.Write(bitmapInfo, 0, BitmapInfoHeaderSize);
                if (paletteBytes != 0)
                {
                    writer.Write(bitmapInfo, BitmapInfoHeaderSize, paletteBytes);
                }
                writer.Write(bitmapData, 0, pixelLength);
            }
            stream.Position = 0;
            using Image decoded = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
            bitmap = new Bitmap(decoded);
            status = SusieSpiDecodeStatus.Decoded;
            detail = "GetPicture DIB converted to a detached Bitmap.";
            return true;
        }
        catch (Exception ex)
        {
            status = SusieSpiDecodeStatus.InvalidDib;
            detail = $"GDI+ rejected the validated DIB: {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static DibHeader ReadDibHeader(byte[] bytes)
        => new(
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)),
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4, 4)),
            BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8, 4)),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(12, 2)),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(14, 2)),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(20, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(32, 4)));

    private static DibValidation ValidateDibHeader(DibHeader header, int infoLength, uint bitsLength)
    {
        if (header.HeaderSize < BitmapInfoHeaderSize || header.HeaderSize > infoLength)
        {
            return new(SusieSpiDecodeStatus.InvalidDib, 0, "BITMAPINFOHEADER size is outside the LocalSize-backed info buffer.");
        }
        if (header.HeaderSize != BitmapInfoHeaderSize)
        {
            return new(SusieSpiDecodeStatus.UnsupportedDib, 0, $"DIB header size {header.HeaderSize} is not the 40-byte BITMAPINFOHEADER PoC format.");
        }
        if (header.Width <= 0 || header.Height == 0 || header.Height == int.MinValue || header.Planes != 1)
        {
            return new(SusieSpiDecodeStatus.InvalidDib, 0, "DIB width, height, or plane count is invalid.");
        }
        if (header.Height < 0)
        {
            return new(SusieSpiDecodeStatus.UnsupportedDib, 0, "Top-down DIBs are outside this PoC's supported subset.");
        }
        if (header.Width > MaximumDimension || header.Height > MaximumDimension)
        {
            return new(SusieSpiDecodeStatus.InvalidDib, 0, "DIB dimensions exceed the PoC safety limit.");
        }
        if (header.BitsPerPixel is not (8 or 24 or 32))
        {
            bool knownDepth = header.BitsPerPixel is 1 or 4 or 8 or 16;
            return new(knownDepth ? SusieSpiDecodeStatus.UnsupportedDib : SusieSpiDecodeStatus.InvalidDib, 0,
                $"DIB bit depth {header.BitsPerPixel} is not supported; this PoC accepts 8/24/32-bpp BI_RGB.");
        }
        if (header.Compression != BiRgb)
        {
            bool knownCompression = header.Compression is BiRle4 or BiRle8 or BiBitfields;
            return new(knownCompression ? SusieSpiDecodeStatus.UnsupportedDib : SusieSpiDecodeStatus.InvalidDib, 0,
                $"DIB compression is not supported: bitCount={header.BitsPerPixel}, compression={header.Compression}, "
                + $"width={header.Width}, height={header.Height}, biClrUsed={header.ClrUsed}, biSizeImage={header.SizeImage}.");
        }

        if (header.BitsPerPixel == 8)
        {
            if (header.ClrUsed > 256)
            {
                return new(SusieSpiDecodeStatus.InvalidDib, 0,
                    $"8-bpp BI_RGB palette count is invalid: biClrUsed={header.ClrUsed}; maximum=256.");
            }

            uint paletteColorCount = header.ClrUsed == 0 ? 256u : header.ClrUsed;
            int paletteBytes = checked((int)paletteColorCount * 4);
            int requiredInfoBytes = checked(BitmapInfoHeaderSize + paletteBytes);
            if (requiredInfoBytes > infoLength)
            {
                return new(SusieSpiDecodeStatus.InvalidDib, 0,
                    $"8-bpp BI_RGB palette is truncated: biClrUsed={header.ClrUsed}, requiredInfoBytes={requiredInfoBytes}, availableInfoBytes={infoLength}.");
            }
        }

        long rowBits = checked((long)header.Width * header.BitsPerPixel);
        long stride = ((rowBits + 31) / 32) * 4;
        long requiredPixelBytes = checked(stride * header.Height);
        if (requiredPixelBytes <= 0 || requiredPixelBytes > MaximumPixelBytes || requiredPixelBytes > int.MaxValue)
        {
            return new(SusieSpiDecodeStatus.InvalidDib, 0, "DIB stride or pixel allocation exceeds the PoC safety limit.");
        }
        if (header.SizeImage != 0 && (header.SizeImage < requiredPixelBytes || header.SizeImage > MaximumPixelBytes))
        {
            return new(SusieSpiDecodeStatus.InvalidDib, 0, "biSizeImage is inconsistent with the calculated DIB stride and height.");
        }
        uint required = checked((uint)requiredPixelBytes);
        if (bitsLength < required || (header.SizeImage != 0 && header.SizeImage > bitsLength))
        {
            return new(SusieSpiDecodeStatus.InvalidDib, 0, "Bitmap LocalSize is smaller than the calculated DIB pixel data.");
        }
        return new(SusieSpiDecodeStatus.Decoded, required, "DIB dimensions, bit depth, stride, and LocalSize are consistent.");
    }

    private static SusieSpiDecodeResult Failure(
        SusieSpiDecodeStatus status,
        string detail,
        bool usedUnicodePath = false,
        int? errorCode = null)
        => new(null, new SusieSpiAttemptSummary(status, errorCode, usedUnicodePath, null, detail));

    private static DibConversionResult DibFailure(SusieSpiDecodeStatus status, string detail)
        => new(null, status, detail);

    private sealed record DibConversionResult(Bitmap? Image, SusieSpiDecodeStatus Status, string Detail);
    private sealed record DibHeader(uint HeaderSize, int Width, int Height, ushort Planes, ushort BitsPerPixel, uint Compression, uint SizeImage, uint ClrUsed);
    private sealed record DibValidation(SusieSpiDecodeStatus Status, uint RequiredPixelBytes, string Detail);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPluginInfoA(int infoNo, IntPtr buffer, int bufferLength);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPluginInfoW(int infoNo, IntPtr buffer, int bufferLength);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int IsSupportedA(IntPtr fileName, IntPtr first2KBuffer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int IsSupportedW(IntPtr fileName, IntPtr first2KBuffer);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPictureA(IntPtr fileName, IntPtr length, uint flags, out IntPtr bitmapInfo, out IntPtr bitmapData, SusieProgressCallback progress, IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetPictureW(IntPtr fileName, IntPtr length, uint flags, out IntPtr bitmapInfo, out IntPtr bitmapData, SusieProgressCallback progress, IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SusieProgressCallback(int current, int total, IntPtr userData);

    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryExW(string fileName, IntPtr fileHandle, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, string procedureName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LocalUnlock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint LocalSize(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();
}
