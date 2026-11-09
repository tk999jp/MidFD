using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;

namespace MidFD.Services;

internal static class MarkdownInlineImageResolver
{
    internal const int MaxMarkdownInlinePreviewWidth = 800;
    internal const int MaxInlineImageBytes = 8 * 1024 * 1024;

    internal sealed class PreviewBudget(int maxImages = 512, int maxDataUriChars = 16 * 1024 * 1024)
    {
        private int _images = maxImages, _chars = maxDataUriChars;
        private readonly System.Diagnostics.Stopwatch _elapsed = System.Diagnostics.Stopwatch.StartNew();
        internal bool WasLimited { get; private set; }
        internal bool TryStartImage()
        {
            if (_images-- <= 0 || _elapsed.Elapsed > TimeSpan.FromSeconds(2)) { WasLimited = true; return false; }
            return true;
        }
        internal bool TryReserve(int chars)
        {
            if (chars > _chars) { WasLimited = true; return false; }
            _chars -= chars; return true;
        }
    }

    private static readonly IReadOnlyDictionary<string, string> SupportedMimeTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif"
        };

    public static bool TryCreateDataUri(string? markdownPath, string? resourceReference, out string? dataUri)
    {
        return TryCreateDataUri(markdownPath, resourceReference, out dataUri, out _, out _);
    }

    internal static bool TryCreateDataUri(
        string? markdownPath,
        string? resourceReference,
        out string? dataUri,
        out int displayWidth,
        out int displayHeight,
        PreviewBudget? budget = null)
    {
        dataUri = null;
        displayWidth = 0;
        displayHeight = 0;
        if (budget != null && !budget.TryStartImage()) return false;
        if (string.IsNullOrWhiteSpace(markdownPath)
            || string.IsNullOrWhiteSpace(resourceReference)
            || Path.IsPathRooted(resourceReference)
            || Uri.TryCreate(resourceReference, UriKind.Absolute, out _))
        {
            return false;
        }

        string? root = Path.GetDirectoryName(Path.GetFullPath(markdownPath));
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.Combine(root, resourceReference));
        }
        catch (Exception)
        {
            return false;
        }

        string relative = Path.GetRelativePath(root, candidate);
        if (relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
            || !SupportedMimeTypes.TryGetValue(Path.GetExtension(candidate), out string? mimeType)
            || !File.Exists(candidate)
            || ContainsReparsePoint(root, candidate))
        {
            return false;
        }

        try
        {
            using var input = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read);
            ImagePreviewService.ValidateRasterStream(input, MaxInlineImageBytes, 16L * 1024 * 1024);
            byte[] bytes = new byte[checked((int)input.Length)];
            input.ReadExactly(bytes);
            byte[] previewBytes = CreatePreviewBytes(bytes, mimeType, out displayWidth, out displayHeight);
            if (previewBytes.Length > MaxInlineImageBytes)
            {
                displayWidth = displayHeight = 0;
                return false;
            }
            int uriChars = 4 * ((previewBytes.Length + 2) / 3) + mimeType.Length + 13;
            if (budget != null && !budget.TryReserve(uriChars)) { displayWidth = displayHeight = 0; return false; }
            dataUri = $"data:{mimeType};base64,{Convert.ToBase64String(previewBytes)}";
            return true;
        }
        catch (Exception)
        {
            dataUri = null;
            displayWidth = 0;
            displayHeight = 0;
            return false;
        }
    }

    private static byte[] CreatePreviewBytes(byte[] sourceBytes, string mimeType, out int displayWidth, out int displayHeight)
    {
        displayWidth = 0;
        displayHeight = 0;

        try
        {
            using var input = new MemoryStream(sourceBytes, writable: false);
            using var source = Image.FromStream(input);
            if (source.Width <= MaxMarkdownInlinePreviewWidth || source.Height <= 0)
            {
                return sourceBytes;
            }

            int targetHeight = Math.Max(1, (int)Math.Round(source.Height * (MaxMarkdownInlinePreviewWidth / (double)source.Width)));
            displayWidth = MaxMarkdownInlinePreviewWidth;
            displayHeight = targetHeight;
            if (mimeType is not ("image/png" or "image/jpeg"))
            {
                return sourceBytes;
            }

            PixelFormat pixelFormat = mimeType == "image/png" ? PixelFormat.Format32bppArgb : PixelFormat.Format24bppRgb;
            using var preview = new Bitmap(MaxMarkdownInlinePreviewWidth, targetHeight, pixelFormat);
            using (Graphics graphics = Graphics.FromImage(preview))
            {
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.DrawImage(source, new Rectangle(0, 0, preview.Width, preview.Height));
            }

            using var output = new MemoryStream();
            if (mimeType == "image/png")
            {
                preview.Save(output, ImageFormat.Png);
            }
            else
            {
                ImageCodecInfo? encoder = ImageCodecInfo.GetImageEncoders()
                    .FirstOrDefault(candidate => string.Equals(candidate.MimeType, "image/jpeg", StringComparison.OrdinalIgnoreCase));
                if (encoder == null)
                {
                    return sourceBytes;
                }

                using var parameters = new EncoderParameters(1);
                parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 90L);
                preview.Save(output, encoder, parameters);
            }

            return output.ToArray();
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("画像プレビューを生成できません。", ex);
        }
    }

    private static bool ContainsReparsePoint(string root, string candidate)
    {
        if (ReparsePointHelper.IsReparsePoint(root))
        {
            return true;
        }

        string current = root;
        string relative = Path.GetRelativePath(root, candidate);
        foreach (string segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if (ReparsePointHelper.IsReparsePoint(current))
            {
                return true;
            }
        }
        return false;
    }
}
