using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
#if MIDFD_WITH_SKIA
using SkiaSharp;
#endif
using Svg;

namespace MidFD.Services;

public static class ImagePreviewService
{
    private static readonly string[] _imageExtensions =
    [
        ".jpg", ".jpeg", ".png", ".bmp", ".gif",
#if MIDFD_WITH_SKIA
        ".webp",
#endif
        ".tif", ".tiff", ".svg"
    ];
    private static readonly IReadOnlyList<string> _imageExtensionView = Array.AsReadOnly(_imageExtensions);

    public static IReadOnlyList<string> SupportedExtensions => _imageExtensionView;
    private static readonly object _svgCacheLock = new();
    private static readonly Dictionary<string, (DateTime LastWriteUtc, long Length, Bitmap Image)> _svgCache = new();
    private const int SvgCacheMaxEntries = 6;
    internal const long MaxSourceBytes = 128L * 1024 * 1024;
    internal const long MaxPixels = 64L * 1024 * 1024;
    private const long SvgCachePixels = 16L * 1024 * 1024;

    internal static void ValidateDimensions(int width, int height, long maxPixels = MaxPixels)
    {
        if (width <= 0 || height <= 0 || width > 32768 || height > 32768 || (long)width * height > maxPixels)
            throw new InvalidDataException("画像の寸法・総画素数がプレビュー資源上限を超えています。");
    }

    internal static void ValidateRasterStream(Stream stream, long maxBytes = MaxSourceBytes, long maxPixels = MaxPixels)
    {
        if (!stream.CanRead || !stream.CanSeek)
            throw new InvalidDataException("画像streamを安全に検証できません。");
        if (stream.Length > maxBytes) throw new InvalidDataException("画像ファイルがプレビュー容量上限を超えています。");
        stream.Position = 0;
        try
        {
            Span<byte> prefix = stackalloc byte[12];
            int prefixLength = ReadAtMost(stream, prefix);
            (int Width, int Height) dimensions;
            if (prefixLength >= 8 && prefix[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            {
                dimensions = ReadPngDimensions(stream);
            }
            else if (prefixLength >= 6 && (prefix[..6].SequenceEqual("GIF87a"u8) || prefix[..6].SequenceEqual("GIF89a"u8)))
            {
                dimensions = ReadGifDimensions(stream);
            }
            else if (prefixLength >= 2 && prefix[0] == (byte)'B' && prefix[1] == (byte)'M')
            {
                dimensions = ReadBmpDimensions(stream);
            }
            else if (prefixLength >= 2 && prefix[0] == 0xFF && prefix[1] == 0xD8)
            {
                dimensions = ReadJpegDimensions(stream);
            }
            else if (prefixLength >= 4 && ((prefix[0] == 'I' && prefix[1] == 'I' && prefix[2] == 42 && prefix[3] == 0)
                || (prefix[0] == 'M' && prefix[1] == 'M' && prefix[2] == 0 && prefix[3] == 42)))
            {
                dimensions = ReadTiffDimensions(stream);
            }
#if MIDFD_WITH_SKIA
            else if (prefixLength >= 12 && prefix[..4].SequenceEqual("RIFF"u8) && prefix[8..12].SequenceEqual("WEBP"u8))
            {
                stream.Position = 0;
                using var managed = new SKManagedStream(stream, false);
                using var codec = SKCodec.Create(managed);
                if (codec == null) throw new InvalidDataException("WebP画像ヘッダを検証できません。");
                dimensions = (codec.Info.Width, codec.Info.Height);
            }
#endif
            else
            {
                throw new InvalidDataException("画像形式または寸法ヘッダを検証できません。");
            }

            ValidateDimensions(dimensions.Width, dimensions.Height, maxPixels);
        }
        catch (EndOfStreamException error)
        {
            throw new InvalidDataException("画像寸法ヘッダが途中で切れています。", error);
        }
        catch (OverflowException error)
        {
            throw new InvalidDataException("画像寸法ヘッダの値が対応範囲を超えています。", error);
        }
        finally { stream.Position = 0; }
    }

    private static int ReadAtMost(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer[total..]);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    private static (int Width, int Height) ReadPngDimensions(Stream stream)
    {
        if (stream.Length < 33) throw new InvalidDataException("PNG IHDRが途中で切れています。");
        Span<byte> header = stackalloc byte[24];
        stream.Position = 0;
        stream.ReadExactly(header);
        if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header[8..12]) != 13
            || !header[12..16].SequenceEqual("IHDR"u8))
            throw new InvalidDataException("PNG IHDRが不正です。");
        return (
            checked((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header[16..20])),
            checked((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header[20..24])));
    }

    private static (int Width, int Height) ReadGifDimensions(Stream stream)
    {
        if (stream.Length < 10) throw new InvalidDataException("GIF logical screen headerが途中で切れています。");
        Span<byte> dimensions = stackalloc byte[4];
        stream.Position = 6;
        stream.ReadExactly(dimensions);
        return (
            System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(dimensions[..2]),
            System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(dimensions[2..]));
    }

    private static (int Width, int Height) ReadBmpDimensions(Stream stream)
    {
        if (stream.Length < 26) throw new InvalidDataException("BMP DIB headerが途中で切れています。");
        Span<byte> header = stackalloc byte[26];
        stream.Position = 0;
        stream.ReadExactly(header);
        uint dibSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header[14..18]);
        if (dibSize == 12)
        {
            return (
                System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header[18..20]),
                System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header[20..22]));
        }
        if (dibSize is < 40 or > 124 || stream.Length < 14L + dibSize)
            throw new InvalidDataException("BMP DIB header size is unsupported or truncated.");
        int width = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header[18..22]);
        int signedHeight = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header[22..26]);
        long absoluteHeight = Math.Abs((long)signedHeight);
        if (absoluteHeight > int.MaxValue) throw new InvalidDataException("BMP dimensions are invalid.");
        return (width, (int)absoluteHeight);
    }

    private static (int Width, int Height) ReadJpegDimensions(Stream stream)
    {
        const long maxHeaderBytes = 1024 * 1024;
        const int maxSegments = 4096;
        stream.Position = 2;
        Span<byte> lengthBytes = stackalloc byte[2];
        Span<byte> dimensions = stackalloc byte[5];
        for (int segment = 0; segment < maxSegments && stream.Position <= Math.Min(stream.Length, maxHeaderBytes); segment++)
        {
            int prefix;
            do
            {
                prefix = stream.ReadByte();
                if (prefix < 0) throw new InvalidDataException("JPEG frame header is missing.");
            } while (prefix != 0xFF);

            int marker;
            do
            {
                marker = stream.ReadByte();
                if (marker < 0) throw new InvalidDataException("JPEG marker is truncated.");
            } while (marker == 0xFF);

            if (marker == 0x00) throw new InvalidDataException("JPEG marker stuffing is invalid before scan data.");
            if (marker == 0xD9 || marker == 0xDA) break;
            if (marker is 0xD8 or 0x01 or >= 0xD0 and <= 0xD7) continue;
            if (stream.Position > maxHeaderBytes) throw new InvalidDataException("JPEG header scan exceeds its resource limit.");

            stream.ReadExactly(lengthBytes);
            int segmentLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(lengthBytes);
            if (segmentLength < 2 || stream.Length - stream.Position < segmentLength - 2)
                throw new InvalidDataException("JPEG segment length is invalid or truncated.");
            if (stream.Position > maxHeaderBytes) throw new InvalidDataException("JPEG header scan exceeds its resource limit.");

            if (IsJpegStartOfFrameMarker(marker))
            {
                if (segmentLength < 8) throw new InvalidDataException("JPEG SOF dimensions are truncated.");
                stream.ReadExactly(dimensions);
                int height = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(dimensions[1..3]);
                int width = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(dimensions[3..5]);
                return (width, height);
            }

            stream.Position += segmentLength - 2;
            if (stream.Position > maxHeaderBytes)
                throw new InvalidDataException("JPEG header scan exceeds its resource limit.");
        }
        throw new InvalidDataException("JPEG frame dimensions were not found within the header scan limit.");
    }

    private static bool IsJpegStartOfFrameMarker(int marker)
        => marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7
            or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF;

    private static (int Width, int Height) ReadTiffDimensions(Stream stream)
    {
        Span<byte> header = stackalloc byte[8];
        stream.Position = 0;
        stream.ReadExactly(header);
        bool little = header[0] == 'I' && header[1] == 'I';
        if (!little && !(header[0] == 'M' && header[1] == 'M'))
            throw new InvalidDataException("画像ヘッダを検証できません。");
        static ushort U16(ReadOnlySpan<byte> data, bool little) => little
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(data)
            : System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(data);
        static uint U32(ReadOnlySpan<byte> data, bool little) => little
            ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data)
            : System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data);
        if (U16(header[2..], little) != 42) throw new InvalidDataException("TIFFヘッダが未対応です。");
        long offset = U32(header[4..], little);
        if (offset < 8 || offset > stream.Length - 2) throw new InvalidDataException("TIFF IFDが不正です。");
        stream.Position = offset;
        Span<byte> count = stackalloc byte[2]; stream.ReadExactly(count);
        int entries = U16(count, little);
        if (entries > 4096 || stream.Length - stream.Position < entries * 12L)
            throw new InvalidDataException("TIFF IFDの資源上限を超えています。");
        int width = 0, height = 0;
        Span<byte> entry = stackalloc byte[12];
        for (int i = 0; i < entries; i++)
        {
            stream.ReadExactly(entry);
            int tag = U16(entry, little), type = U16(entry[2..], little);
            if (tag is not (256 or 257)) continue;
            if (U32(entry[4..], little) != 1 || type is not (3 or 4))
                throw new InvalidDataException("TIFF寸法fieldが不正です。");
            int value = checked((int)(type == 3 ? U16(entry[8..], little) : U32(entry[8..], little)));
            if (tag == 256) width = value; else height = value;
        }
        return (width, height);
    }

    public static bool IsSupportedExtension(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return Array.IndexOf(_imageExtensions, ext) >= 0;
    }

    public static (Bitmap? Image, string ErrorMessage) GetPreviewImage(string path)
    {
        if (!IsSupportedExtension(path))
        {
            return (null, "プレビュー対象外");
        }

        if (string.Equals(Path.GetExtension(path), ".svg", StringComparison.OrdinalIgnoreCase))
        {
            return TryLoadSvg(path);
        }

#if MIDFD_WITH_SKIA
        if (string.Equals(Path.GetExtension(path), ".webp", StringComparison.OrdinalIgnoreCase))
        {
            var webpResult = TryLoadWebp(path);
            if (webpResult.Image != null)
            {
                return webpResult;
            }
        }
#endif

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            ValidateRasterStream(fs);
            using var tempImg = Image.FromStream(fs);
            
            // MemoryStream等への参照を断ち切る完全なクローンを作成
            var bmp = new Bitmap(tempImg);

            return (bmp, "");
        }
        catch (InvalidDataException ex) { return (null, $"プレビュー省略: {ex.Message}"); }
        catch (Exception ex)
        {
            var wicResult = TryLoadWithWic(path);
            if (wicResult.Image != null)
            {
                return wicResult;
            }

            return (null, $"プレビュー失敗: {ex.Message}");
        }
    }

#if MIDFD_WITH_SKIA
    private static (Bitmap? Image, string ErrorMessage) TryLoadWebp(string path)
    {
        try
        {
            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fileStream.Length > MaxSourceBytes) throw new InvalidDataException("画像ファイルがプレビュー容量上限を超えています。");
            using SKCodec? codec = SKCodec.Create(fileStream);
            if (codec == null)
            {
                return (null, "プレビュー失敗: WebP codec を利用できません");
            }

            SKImageInfo info = codec.Info;
            ValidateDimensions(info.Width, info.Height);

            using SKBitmap decoded = SKBitmap.Decode(codec);
            using var source = new Bitmap(decoded.Width, decoded.Height, decoded.RowBytes,
                System.Drawing.Imaging.PixelFormat.Format32bppPArgb, decoded.GetPixels());
            return (new Bitmap(source), string.Empty);
        }
        catch (Exception ex)
        {
            return (null, $"WebP読み込み失敗: {ex.Message}");
        }
    }
#endif

    private static (Bitmap? Image, string ErrorMessage) TryLoadSvg(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            lock (_svgCacheLock)
            {
                if (_svgCache.TryGetValue(path, out var cached)
                    && cached.LastWriteUtc == fi.LastWriteTimeUtc
                    && cached.Length == fi.Length)
                {
                    return (new Bitmap(cached.Image), string.Empty);
                }
            }

            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > 4 * 1024 * 1024) throw new InvalidDataException("SVGがプレビュー容量上限を超えています。");
            using var xmlReader = System.Xml.XmlReader.Create(input, new System.Xml.XmlReaderSettings
            {
                DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = 4 * 1024 * 1024
            });
            var xml = new System.Xml.XmlDocument { XmlResolver = null };
            xml.Load(xmlReader);
            if (xml.SelectNodes("//*")!.Count > 10000)
                throw new InvalidDataException("SVG要素数がプレビュー上限を超えています。");
            // External image decode bypasses this service's byte/pixel guards.
            // Omit such previews rather than fetching unbounded nested resources.
            foreach (System.Xml.XmlNode image in xml.SelectNodes("//*[local-name()='image']")!)
            {
                string href = image.Attributes?["href"]?.Value ?? image.Attributes?["href", "http://www.w3.org/1999/xlink"]?.Value ?? "";
                if (string.IsNullOrEmpty(href)) continue;
                int comma = href.IndexOf(',');
                if (!href.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) || comma < 0 ||
                    !href[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("外部画像を参照するSVGのプレビューは省略します。");
                using var embedded = new MemoryStream(Convert.FromBase64String(href[(comma + 1)..]));
                ValidateRasterStream(embedded, 4 * 1024 * 1024, 16L * 1024 * 1024);
            }
            foreach (System.Xml.XmlNode node in xml.SelectNodes("//*[@href or @xlink:href]", CreateSvgNamespaceManager(xml))!)
            {
                if (node.LocalName == "image") continue;
                string href = node.Attributes?["href"]?.Value ?? node.Attributes?["href", "http://www.w3.org/1999/xlink"]?.Value ?? "";
                if (href.Length > 0 && !href.StartsWith('#'))
                    throw new InvalidDataException("外部要素を参照するSVGのプレビューは省略します。");
            }
            SvgDocument? svgDocument = SvgDocument.Open(xml);
            if (svgDocument == null)
            {
                return (null, "プレビュー失敗: SVGの読み込みに失敗しました");
            }

            SizeF dimensions = svgDocument.GetDimensions();
            if (!float.IsFinite(dimensions.Width) || !float.IsFinite(dimensions.Height))
                throw new InvalidDataException("SVGの寸法が不正です。");
            ValidateDimensions(checked((int)Math.Ceiling(dimensions.Width)), checked((int)Math.Ceiling(dimensions.Height)));
            Bitmap bitmap = svgDocument.Draw();
            try
            {
                lock (_svgCacheLock)
                {
                    if (_svgCache.Remove(path, out var previous)) previous.Image.Dispose();
                    long pixels = (long)bitmap.Width * bitmap.Height;
                    if (pixels <= SvgCachePixels)
                    {
                        while (_svgCache.Count >= SvgCacheMaxEntries || _svgCache.Values.Sum(x => (long)x.Image.Width * x.Image.Height) + pixels > SvgCachePixels)
                        {
                            string removeKey = _svgCache.Keys.First();
                            _svgCache[removeKey].Image.Dispose();
                            _svgCache.Remove(removeKey);
                        }
                        _svgCache[path] = (fi.LastWriteTimeUtc, fi.Length, new Bitmap(bitmap));
                    }
                }
                return (bitmap, string.Empty);
            }
            catch { bitmap.Dispose(); throw; }
        }
        catch (Exception ex)
        {
            return (null, $"プレビュー失敗: {ex.Message}");
        }
    }

    private static System.Xml.XmlNamespaceManager CreateSvgNamespaceManager(System.Xml.XmlDocument document)
    {
        var namespaces = new System.Xml.XmlNamespaceManager(document.NameTable);
        namespaces.AddNamespace("xlink", "http://www.w3.org/1999/xlink");
        return namespaces;
    }

    private static (Bitmap? Image, string ErrorMessage) TryLoadWithWic(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            ValidateRasterStream(stream);
            Assembly presentationCore = Assembly.Load("PresentationCore");
            Type? bitmapDecoderType = presentationCore.GetType("System.Windows.Media.Imaging.BitmapDecoder");
            Type? bitmapCreateOptionsType = presentationCore.GetType("System.Windows.Media.Imaging.BitmapCreateOptions");
            Type? bitmapCacheOptionType = presentationCore.GetType("System.Windows.Media.Imaging.BitmapCacheOption");
            Type? pngBitmapEncoderType = presentationCore.GetType("System.Windows.Media.Imaging.PngBitmapEncoder");
            if (bitmapDecoderType == null || bitmapCreateOptionsType == null || bitmapCacheOptionType == null || pngBitmapEncoderType == null)
            {
                return (null, "プレビュー失敗: WIC decoder を利用できません");
            }

            object preservePixelFormat = Enum.Parse(bitmapCreateOptionsType, "DelayCreation");
            object onLoad = Enum.Parse(bitmapCacheOptionType, "None");
            MethodInfo? createMethod = bitmapDecoderType.GetMethod("Create", new[] { typeof(Stream), bitmapCreateOptionsType, bitmapCacheOptionType });
            if (createMethod == null)
            {
                return (null, "プレビュー失敗: WIC decoder の初期化に失敗しました");
            }

            object? decoder = createMethod.Invoke(null, new[] { stream, preservePixelFormat, onLoad });
            if (decoder == null)
            {
                return (null, "プレビュー失敗: WIC decoder を生成できませんでした");
            }

            object? frames = bitmapDecoderType.GetProperty("Frames")?.GetValue(decoder);
            if (frames == null)
            {
                return (null, "プレビュー失敗: WIC フレームを取得できませんでした");
            }

            int frameCount = (int?)frames.GetType().GetProperty("Count")?.GetValue(frames) ?? 0;
            if (frameCount == 0)
            {
                return (null, "プレビュー失敗: フレームがありません");
            }

            object? frame = frames.GetType().GetProperty("Item")?.GetValue(frames, new object[] { 0 });
            if (frame == null)
            {
                return (null, "プレビュー失敗: 先頭フレームを取得できませんでした");
            }

            object? encoder = Activator.CreateInstance(pngBitmapEncoderType);
            if (encoder == null)
            {
                return (null, "プレビュー失敗: PNG encoder を生成できませんでした");
            }

            object? encoderFrames = pngBitmapEncoderType.GetProperty("Frames")?.GetValue(encoder);
            MethodInfo? addMethod = encoderFrames?.GetType().GetMethod("Add");
            MethodInfo? saveMethod = pngBitmapEncoderType.GetMethod("Save", new[] { typeof(Stream) });
            if (encoderFrames == null || addMethod == null || saveMethod == null)
            {
                return (null, "プレビュー失敗: WIC encoder の初期化に失敗しました");
            }

            addMethod.Invoke(encoderFrames, new[] { frame });

            using var ms = new MemoryStream();
            saveMethod.Invoke(encoder, new object[] { ms });
            ms.Position = 0;

            using var tempImg = Image.FromStream(ms);
            var bmp = new Bitmap(tempImg);
            return (bmp, "");
        }
        catch (Exception ex)
        {
            return (null, $"プレビュー失敗: {ex.Message}");
        }
    }
}
