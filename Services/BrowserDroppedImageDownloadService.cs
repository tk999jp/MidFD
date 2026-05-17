using System.Net.Http;
using System.Net.Http.Headers;

namespace MidFD.Services;

public static class BrowserDroppedImageDownloadService
{
    private static readonly HttpClient Client = CreateClient();

    public static string DownloadToDirectory(Uri imageUri, string directoryPath, string? suggestedFileName = null, DateTime? now = null)
    {
        using HttpResponseMessage response = Client.GetAsync(imageUri, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();

        EnsureImageContent(response.Content.Headers.ContentType, imageUri);

        string extension = ResolveExtension(response.Content.Headers.ContentType, imageUri, suggestedFileName);
        if (!IsSupportedImageExtension(extension))
        {
            throw new InvalidOperationException("画像URLを特定できませんでした。");
        }

        string fileName = ResolveFileName(imageUri, suggestedFileName, extension, now ?? DateTime.Now);
        string fullPath = Path.Combine(directoryPath, fileName);
        string uniquePath = File.Exists(fullPath) || Directory.Exists(fullPath)
            ? FileOperationService.GetUniquePathStartingAtOne(fullPath)
            : fullPath;

        Directory.CreateDirectory(directoryPath);
        using Stream input = response.Content.ReadAsStream();
        using FileStream output = new(uniquePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);

        return uniquePath;
    }

    public static bool IsSupportedImageExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".gif", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".svg", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveExtension(MediaTypeHeaderValue? contentType, Uri imageUri, string? suggestedFileName)
    {
        string ext = Path.GetExtension(suggestedFileName ?? string.Empty);
        if (IsSupportedImageExtension(ext))
        {
            return ext;
        }

        ext = Path.GetExtension(imageUri.AbsolutePath);
        if (IsSupportedImageExtension(ext))
        {
            return ext;
        }

        string mediaType = contentType?.MediaType ?? string.Empty;
        return mediaType.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/bmp" => ".bmp",
            "image/webp" => ".webp",
            "image/svg+xml" => ".svg",
            _ => string.Empty,
        };
    }

    private static void EnsureImageContent(MediaTypeHeaderValue? contentType, Uri imageUri)
    {
        string mediaType = contentType?.MediaType ?? string.Empty;
        if (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)
            && IsSupportedImageExtension(Path.GetExtension(imageUri.AbsolutePath)))
        {
            return;
        }

        throw new InvalidOperationException($"画像レスポンスではありません: {mediaType}");
    }

    private static string ResolveFileName(Uri imageUri, string? suggestedFileName, string extension, DateTime now)
    {
        string candidate = suggestedFileName ?? Path.GetFileName(imageUri.AbsolutePath);
        candidate = SanitizeFileName(candidate);

        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = $"DroppedUrl_{now:yyyyMMdd_HHmmss}{extension}";
        }

        string candidateExt = Path.GetExtension(candidate);
        if (!IsSupportedImageExtension(candidateExt))
        {
            candidate = Path.GetFileNameWithoutExtension(candidate) + extension;
        }

        if (string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(candidate)))
        {
            candidate = $"DroppedUrl_{now:yyyyMMdd_HHmmss}{extension}";
        }

        return candidate;
    }

    private static string SanitizeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        char[] invalidChars = Path.GetInvalidFileNameChars();
        return new string(fileName
            .Trim()
            .Select(ch => invalidChars.Contains(ch) ? '_' : ch)
            .ToArray());
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MidFD/1.0");
        return client;
    }
}
