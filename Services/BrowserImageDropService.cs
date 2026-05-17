using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Windows.Forms;
using WinFormsDataObject = System.Windows.Forms.IDataObject;

namespace MidFD.Services;

public static class BrowserImageDropService
{
    private static readonly string[] DirectImageFormats =
    {
        DataFormats.Bitmap,
        "PNG",
        "image/png",
        "image/x-png",
        DataFormats.Dib,
        "DeviceIndependentBitmap",
    };

    public static bool HasImageData(WinFormsDataObject? data)
    {
        if (data == null)
        {
            return false;
        }

        if (HasDirectImageData(data))
        {
            return true;
        }

        return HasVirtualImageFile(data);
    }

    public static bool TryGetImage(WinFormsDataObject? data, out Image? image)
    {
        image = null;
        if (data == null)
        {
            return false;
        }

        try
        {
            if (TryGetDirectImage(data, out image))
            {
                return true;
            }

            if (TryGetVirtualImageFile(data, out image))
            {
                return true;
            }

            LogService.Warn($"BrowserImageDropService: unsupported drag formats [{string.Join(", ", data.GetFormats())}]");
            return false;
        }
        catch (Exception ex)
        {
            LogService.Error("TryGetImage(drop) failed", ex);
            image = null;
            return false;
        }
    }

    public static string SavePngToDirectory(Image image, string directoryPath, DateTime? now = null)
    {
        Directory.CreateDirectory(directoryPath);

        string fileName = BuildDefaultFileName(now ?? DateTime.Now);
        string fullPath = Path.Combine(directoryPath, fileName);
        string uniquePath = File.Exists(fullPath) || Directory.Exists(fullPath)
            ? FileOperationService.GetUniquePathStartingAtOne(fullPath)
            : fullPath;

        using var bitmap = new Bitmap(image);
        bitmap.Save(uniquePath, ImageFormat.Png);
        return uniquePath;
    }

    public static string BuildDefaultFileName(DateTime now)
    {
        return $"Dropped_{now:yyyyMMdd_HHmmss}.png";
    }

    public static string DescribeDataObject(WinFormsDataObject? data)
    {
        if (data == null)
        {
            return "<null>";
        }

        var parts = new List<string>();

        if (TryGetVirtualFileName(data, out string? fileName) && !string.IsNullOrWhiteSpace(fileName))
        {
            parts.Add($"VirtualFile={fileName}");
        }

        foreach (string format in data.GetFormats())
        {
            string typeName;
            try
            {
                object? raw = data.GetData(format);
                typeName = raw?.GetType().FullName ?? "<null>";
            }
            catch (Exception ex)
            {
                typeName = $"<error:{ex.GetType().Name}>";
            }

            parts.Add($"{format}={typeName}");
        }

        return string.Join(", ", parts);
    }

    private static bool HasDirectImageData(WinFormsDataObject data)
    {
        foreach (string format in DirectImageFormats)
        {
            if (data.GetDataPresent(format))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetDirectImage(WinFormsDataObject data, out Image? image)
    {
        foreach (string format in DirectImageFormats)
        {
            if (!data.GetDataPresent(format))
            {
                continue;
            }

            object? raw = data.GetData(format);
            if (TryConvertToImage(raw, out image))
            {
                return true;
            }
        }

        image = null;
        return false;
    }

    private static bool HasVirtualImageFile(WinFormsDataObject data)
    {
        return TryGetVirtualFileName(data, out string? fileName)
            && IsSupportedImageExtension(fileName);
    }

    private static bool TryGetVirtualImageFile(WinFormsDataObject data, out Image? image)
    {
        image = null;
        if (!TryGetVirtualFileName(data, out string? fileName) || !IsSupportedImageExtension(fileName))
        {
            return false;
        }

        object? raw = data.GetData("FileContents");
        if (TryConvertToImage(raw, out image))
        {
            return true;
        }

        string rawType = raw?.GetType().FullName ?? "<null>";
        LogService.Warn($"BrowserImageDropService: FileContents could not be decoded as image ({fileName}, Type={rawType})");
        return false;
    }

    public static bool TryGetVirtualFileName(WinFormsDataObject? data, out string? fileName)
    {
        fileName = null;
        if (data == null)
        {
            return false;
        }

        if (TryReadFileDescriptor(data, "FileGroupDescriptorW", true, out fileName))
        {
            return true;
        }

        if (TryReadFileDescriptor(data, "FileGroupDescriptor", false, out fileName))
        {
            return true;
        }

        return false;
    }

    private static bool TryReadFileDescriptor(WinFormsDataObject data, string format, bool unicode, out string? fileName)
    {
        fileName = null;
        if (!data.GetDataPresent(format))
        {
            return false;
        }

        byte[]? bytes = TryGetBytes(data.GetData(format));
        if (bytes == null || bytes.Length < 84)
        {
            return false;
        }

        int count = BitConverter.ToInt32(bytes, 0);
        if (count <= 0)
        {
            return false;
        }

        const int fileNameOffset = 4 + 72;
        int fileNameLength = unicode ? 520 : 260;
        if (bytes.Length < fileNameOffset + fileNameLength)
        {
            return false;
        }

        fileName = unicode
            ? ReadNullTerminatedUnicode(bytes, fileNameOffset, fileNameLength)
            : ReadNullTerminatedAnsi(bytes, fileNameOffset, fileNameLength);

        return !string.IsNullOrWhiteSpace(fileName);
    }

    private static string ReadNullTerminatedUnicode(byte[] bytes, int offset, int length)
    {
        string raw = Encoding.Unicode.GetString(bytes, offset, length);
        int nullIndex = raw.IndexOf('\0');
        return nullIndex >= 0 ? raw[..nullIndex] : raw;
    }

    private static string ReadNullTerminatedAnsi(byte[] bytes, int offset, int length)
    {
        string raw = Encoding.Default.GetString(bytes, offset, length);
        int nullIndex = raw.IndexOf('\0');
        return nullIndex >= 0 ? raw[..nullIndex] : raw;
    }

    private static bool TryConvertToImage(object? raw, out Image? image)
    {
        image = null;
        switch (raw)
        {
            case null:
                return false;
            case Image srcImage:
                image = new Bitmap(srcImage);
                return true;
            case byte[] bytes:
                return TryLoadImageFromBytes(bytes, out image);
            case MemoryStream ms:
                return TryLoadImageFromBytes(ms.ToArray(), out image);
            case Stream stream:
                return TryLoadImageFromStream(stream, out image);
            case object[] items:
                foreach (object? item in items)
                {
                    if (TryConvertToImage(item, out image))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }

    private static bool TryLoadImageFromStream(Stream stream, out Image? image)
    {
        using var copy = new MemoryStream();
        long originalPosition = 0;
        if (stream.CanSeek)
        {
            originalPosition = stream.Position;
            stream.Position = 0;
        }

        stream.CopyTo(copy);

        if (stream.CanSeek)
        {
            stream.Position = originalPosition;
        }

        return TryLoadImageFromBytes(copy.ToArray(), out image);
    }

    private static bool TryLoadImageFromBytes(byte[] bytes, out Image? image)
    {
        image = null;
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            using var loaded = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: true);
            image = new Bitmap(loaded);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte[]? TryGetBytes(object? raw)
    {
        return raw switch
        {
            byte[] bytes => bytes,
            MemoryStream ms => ms.ToArray(),
            Stream stream => ReadAllBytes(stream),
            _ => null,
        };
    }

    private static byte[] ReadAllBytes(Stream stream)
    {
        using var copy = new MemoryStream();
        long originalPosition = 0;
        if (stream.CanSeek)
        {
            originalPosition = stream.Position;
            stream.Position = 0;
        }

        stream.CopyTo(copy);

        if (stream.CanSeek)
        {
            stream.Position = originalPosition;
        }

        return copy.ToArray();
    }

    private static bool IsSupportedImageExtension(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        string ext = Path.GetExtension(fileName);
        return ext.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".gif", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".webp", StringComparison.OrdinalIgnoreCase);
    }
}
