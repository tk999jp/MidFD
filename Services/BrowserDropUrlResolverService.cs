using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using WinFormsDataObject = System.Windows.Forms.IDataObject;

namespace MidFD.Services;

public static class BrowserDropUrlResolverService
{
    private static readonly string[] HtmlFormats =
    {
        "HTML Format",
        "text/html",
    };

    private static readonly string[] DirectUrlFormats =
    {
        "UniformResourceLocatorW",
        "UniformResourceLocator",
        "text/x-moz-url",
        DataFormats.UnicodeText,
        DataFormats.Text,
        DataFormats.StringFormat,
    };

    private static readonly Regex ImageSourceRegex = new(
        "<img\\b[^>]*\\bsrc\\s*=\\s*[\"'](?<url>[^\"'#>]+)[\"']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool HasPotentialUrlData(WinFormsDataObject? data)
    {
        if (data == null)
        {
            return false;
        }

        return HtmlFormats.Any(data.GetDataPresent)
            || DirectUrlFormats.Any(data.GetDataPresent);
    }

    public static bool TryResolveImageUrl(WinFormsDataObject? data, out Uri? imageUri, out string? suggestedFileName)
    {
        imageUri = null;
        suggestedFileName = null;

        if (data == null)
        {
            return false;
        }

        BrowserImageDropService.TryGetVirtualFileName(data, out suggestedFileName);

        foreach (UrlCandidate candidate in EnumerateCandidates(data))
        {
            if (!TryCreateHttpUri(candidate.Url, out Uri? resolvedUri) || resolvedUri == null)
            {
                continue;
            }

            if (!LooksLikeImageUrl(resolvedUri, suggestedFileName, candidate.Kind))
            {
                continue;
            }

            imageUri = resolvedUri;
            return true;
        }

        return false;
    }

    private static IEnumerable<UrlCandidate> EnumerateCandidates(WinFormsDataObject data)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string format in HtmlFormats)
        {
            if (!data.GetDataPresent(format))
            {
                continue;
            }

            if (!TryGetText(data.GetData(format), PreferUnicode(format), out string? text)
                || string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            foreach (string candidate in SplitCandidates(format, text))
            {
                if (!string.IsNullOrWhiteSpace(candidate) && seen.Add(candidate.Trim()))
                {
                    yield return new UrlCandidate(candidate.Trim(), UrlCandidateKind.HtmlImageSource);
                }
            }
        }

        foreach (string format in DirectUrlFormats)
        {
            if (!data.GetDataPresent(format))
            {
                continue;
            }

            if (!TryGetText(data.GetData(format), PreferUnicode(format), out string? text)
                || string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            foreach (string candidate in SplitCandidates(format, text))
            {
                if (!string.IsNullOrWhiteSpace(candidate) && seen.Add(candidate.Trim()))
                {
                    yield return new UrlCandidate(candidate.Trim(), UrlCandidateKind.DirectUrl);
                }
            }
        }
    }

    private static IEnumerable<string> SplitCandidates(string format, string text)
    {
        if (format.Equals("text/x-moz-url", StringComparison.OrdinalIgnoreCase))
        {
            using var reader = new StringReader(text);
            while (reader.ReadLine() is string line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    yield return line.Trim();
                    yield break;
                }
            }

            yield break;
        }

        if (format.Equals("HTML Format", StringComparison.OrdinalIgnoreCase)
            || format.Equals("text/html", StringComparison.OrdinalIgnoreCase))
        {
            foreach (Match match in ImageSourceRegex.Matches(text))
            {
                string candidate = System.Net.WebUtility.HtmlDecode(match.Groups["url"].Value);
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    yield return candidate;
                }
            }

            yield break;
        }

        yield return text.Trim();
    }

    private static bool TryGetText(object? raw, bool preferUnicode, out string? text)
    {
        text = raw switch
        {
            null => null,
            string s => s,
            MemoryStream ms => DecodeBytes(ms.ToArray(), preferUnicode),
            byte[] bytes => DecodeBytes(bytes, preferUnicode),
            Stream stream => DecodeBytes(ReadAllBytes(stream), preferUnicode),
            _ => null,
        };

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = text.Replace("\0", string.Empty).Trim();
        return text.Length > 0;
    }

    private static string DecodeBytes(byte[] bytes, bool preferUnicode)
    {
        if (bytes.Length >= 2)
        {
            if (bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            }

            if (bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            }
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (preferUnicode || LooksUtf16(bytes))
        {
            return Encoding.Unicode.GetString(bytes);
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static bool LooksUtf16(byte[] bytes)
    {
        if (bytes.Length < 4 || (bytes.Length % 2) != 0)
        {
            return false;
        }

        int zeroCount = 0;
        int sampleCount = Math.Min(bytes.Length / 2, 16);
        for (int i = 1; i < sampleCount * 2; i += 2)
        {
            if (bytes[i] == 0)
            {
                zeroCount++;
            }
        }

        return zeroCount >= sampleCount / 2;
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

    private static bool TryCreateHttpUri(string candidate, out Uri? uri)
    {
        uri = null;
        if (candidate.StartsWith("//", StringComparison.Ordinal))
        {
            candidate = "https:" + candidate;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out Uri? parsed))
        {
            return false;
        }

        if (!NetworkSecurityPolicyService.IsPublicHttpUri(parsed))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    private static bool LooksLikeImageUrl(Uri uri, string? suggestedFileName, UrlCandidateKind kind)
    {
        string ext = Path.GetExtension(uri.AbsolutePath);
        if (BrowserDroppedImageDownloadService.IsSupportedImageExtension(ext))
        {
            return true;
        }

        if (kind == UrlCandidateKind.HtmlImageSource)
        {
            return true;
        }

        if (kind != UrlCandidateKind.DirectUrl)
        {
            return false;
        }

        ext = Path.GetExtension(suggestedFileName ?? string.Empty);
        return BrowserDroppedImageDownloadService.IsSupportedImageExtension(ext);
    }

    private static bool PreferUnicode(string format)
    {
        return format.EndsWith("W", StringComparison.OrdinalIgnoreCase)
            || format.Equals("text/x-moz-url", StringComparison.OrdinalIgnoreCase)
            || format.Equals(DataFormats.UnicodeText, StringComparison.OrdinalIgnoreCase)
            || format.Equals(DataFormats.StringFormat, StringComparison.OrdinalIgnoreCase)
            || format.Equals("HTML Format", StringComparison.OrdinalIgnoreCase)
            || format.Equals("text/html", StringComparison.OrdinalIgnoreCase);
    }

    private readonly record struct UrlCandidate(string Url, UrlCandidateKind Kind);

    private enum UrlCandidateKind
    {
        HtmlImageSource,
        DirectUrl,
    }
}
