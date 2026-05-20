using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using Svg;

namespace MidFD.Services;

public static class ImagePreviewService
{
    private static readonly string[] _imageExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".svg" };
    private static readonly object _svgCacheLock = new();
    private static readonly Dictionary<string, (DateTime LastWriteUtc, long Length, Bitmap Image)> _svgCache = new();
    private const int SvgCacheMaxEntries = 6;

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

        try
        {
            using var fs = File.OpenRead(path);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            ms.Position = 0;

            using var tempImg = Image.FromStream(ms);
            
            // MemoryStream等への参照を断ち切る完全なクローンを作成
            var bmp = new Bitmap(tempImg);

            return (bmp, "");
        }
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

            SvgDocument? svgDocument = SvgDocument.Open<SvgDocument>(path, new SvgOptions());
            if (svgDocument == null)
            {
                return (null, "プレビュー失敗: SVGの読み込みに失敗しました");
            }

            Bitmap bitmap = svgDocument.Draw();
            lock (_svgCacheLock)
            {
                if (_svgCache.Count >= SvgCacheMaxEntries)
                {
                    string removeKey = _svgCache.Keys.First();
                    _svgCache[removeKey].Image.Dispose();
                    _svgCache.Remove(removeKey);
                }
                _svgCache[path] = (fi.LastWriteTimeUtc, fi.Length, new Bitmap(bitmap));
            }
            return (bitmap, string.Empty);
        }
        catch (Exception ex)
        {
            return (null, $"プレビュー失敗: {ex.Message}");
        }
    }

    private static (Bitmap? Image, string ErrorMessage) TryLoadWithWic(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            Assembly presentationCore = Assembly.Load("PresentationCore");
            Type? bitmapDecoderType = presentationCore.GetType("System.Windows.Media.Imaging.BitmapDecoder");
            Type? bitmapCreateOptionsType = presentationCore.GetType("System.Windows.Media.Imaging.BitmapCreateOptions");
            Type? bitmapCacheOptionType = presentationCore.GetType("System.Windows.Media.Imaging.BitmapCacheOption");
            Type? pngBitmapEncoderType = presentationCore.GetType("System.Windows.Media.Imaging.PngBitmapEncoder");
            if (bitmapDecoderType == null || bitmapCreateOptionsType == null || bitmapCacheOptionType == null || pngBitmapEncoderType == null)
            {
                return (null, "プレビュー失敗: WIC decoder を利用できません");
            }

            object preservePixelFormat = Enum.Parse(bitmapCreateOptionsType, "PreservePixelFormat");
            object onLoad = Enum.Parse(bitmapCacheOptionType, "OnLoad");
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
