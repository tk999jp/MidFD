using System.IO;
using System.IO.Compression;
using System.Text;

namespace MidFD.Services;

public sealed class ArchiveEntryPreviewResult
{
    public bool IsSupported { get; init; }
    public bool IsTruncated { get; init; }
    public string Text { get; init; } = string.Empty;
}

public static class ArchiveEntryPreviewService
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".csv", ".log", ".json", ".xml", ".ini",
        ".yaml", ".yml", ".cs", ".csproj", ".config", ".bat", ".ps1"
    };

    public static bool IsTextFile(string entryPath)
    {
        string ext = Path.GetExtension(entryPath);
        return TextExtensions.Contains(ext);
    }

    public static ArchiveEntryPreviewResult GetZipEntryTextPreview(string archivePath, string entryPath, int maxBytes = 64 * 1024)
    {
        if (!File.Exists(archivePath))
        {
            return new ArchiveEntryPreviewResult { IsSupported = false, Text = "[アーカイブファイルが見つかりません。]" };
        }

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Encoding sjis = Encoding.GetEncoding("shift_jis");
            using ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Read, sjis);
            string targetNormalized = NormalizeEntryName(entryPath);
            ZipArchiveEntry? targetEntry = null;

            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (string.Equals(NormalizeEntryName(entry.FullName), targetNormalized, StringComparison.OrdinalIgnoreCase))
                {
                    targetEntry = entry;
                    break;
                }
            }

            if (targetEntry == null)
            {
                return new ArchiveEntryPreviewResult { IsSupported = false, Text = "[指定されたファイルがアーカイブ内に見つかりません。]" };
            }

            if (targetEntry.Length == 0)
            {
                return new ArchiveEntryPreviewResult { IsSupported = true, Text = string.Empty };
            }

            using Stream stream = targetEntry.Open();
            byte[] buffer = new byte[maxBytes];
            int bytesRead = ReadExactOrEnd(stream, buffer, maxBytes);
            bool isTruncated = targetEntry.Length > bytesRead;

            string text = DetectEncodingAndGetString(buffer, bytesRead);

            if (isTruncated)
            {
                text += "\r\n\r\n--- preview truncated ---";
            }

            return new ArchiveEntryPreviewResult
            {
                IsSupported = true,
                IsTruncated = isTruncated,
                Text = text
            };
        }
        catch (Exception ex)
        {
            return new ArchiveEntryPreviewResult { IsSupported = false, Text = $"[プレビューの読み込みに失敗しました: {ex.Message}]" };
        }
    }

    private static string NormalizeEntryName(string entryName)
    {
        return (entryName ?? string.Empty)
            .Replace('\\', '/')
            .TrimStart('/')
            .TrimEnd('/');
    }

    private static int ReadExactOrEnd(Stream stream, byte[] buffer, int maxBytes)
    {
        int totalRead = 0;
        while (totalRead < maxBytes)
        {
            int read = stream.Read(buffer, totalRead, maxBytes - totalRead);
            if (read <= 0)
            {
                break;
            }
            totalRead += read;
        }
        return totalRead;
    }

    private static string DetectEncodingAndGetString(byte[] buffer, int length)
    {
        if (length == 0) return string.Empty;

        // BOMの判定
        if (length >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(buffer, 3, length - 3);
        }
        if (length >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(buffer, 2, length - 2);
        }
        if (length >= 2 && buffer[0] == 0xFE && buffer[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(buffer, 2, length - 2);
        }

        try
        {
            var utf8Throw = new UTF8Encoding(false, true);
            return utf8Throw.GetString(buffer, 0, length);
        }
        catch
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                Encoding sjis = Encoding.GetEncoding("shift_jis");
                return sjis.GetString(buffer, 0, length);
            }
            catch
            {
                return Encoding.UTF8.GetString(buffer, 0, length);
            }
        }
    }
}
