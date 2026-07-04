using System.Text;
using System.Text.RegularExpressions;

namespace MidFD.Services;

public static class MarkdownPreviewService
{
    private const int MaxOutlineItems = 200;
    private const int MaxHeadingDisplayLength = 120;

    public static async Task<string> GetPreviewAsync(string path, int maxBytes, CancellationToken token)
    {
        string text = await PreviewService.GetTextPreviewAsync(path, maxBytes, token);
        token.ThrowIfCancellationRequested();

        var sb = new StringBuilder();
        sb.AppendLine($"[Markdown Preview: {Path.GetFileName(path)}]");
        sb.AppendLine("Read-only raw Markdown view");
        sb.AppendLine();

        string outline = BuildOutline(text);
        if (!string.IsNullOrWhiteSpace(outline))
        {
            sb.AppendLine("[Outline: line / level / heading]");
            sb.Append(outline);
            sb.AppendLine();
        }

        sb.AppendLine("[Raw]");
        sb.Append(text);
        return sb.ToString();
    }

    public static string? ResolveClickedUrl(string? linkText)
    {
        if (string.IsNullOrWhiteSpace(linkText))
        {
            return null;
        }

        string text = linkText.Trim();
        int markdownSeparator = text.IndexOf("](", StringComparison.Ordinal);
        if (markdownSeparator > 0)
        {
            string labelCandidate = TrimUrlBoundary(text[..markdownSeparator]);
            if (Helpers.UrlValidationHelper.IsValidWebUrl(labelCandidate))
            {
                return labelCandidate;
            }

            string destination = text[(markdownSeparator + 2)..];
            int closeParen = destination.IndexOf(')');
            if (closeParen >= 0)
            {
                destination = destination[..closeParen];
            }

            destination = TrimUrlBoundary(destination);
            return Helpers.UrlValidationHelper.IsValidWebUrl(destination) ? destination : null;
        }

        string trimmed = TrimUrlBoundary(text);
        return Helpers.UrlValidationHelper.IsValidWebUrl(trimmed) ? trimmed : null;
    }

    private static string TrimUrlBoundary(string value)
    {
        string text = value.Trim();
        while (text.Length > 0 && IsTrailingUrlPunctuation(text[^1]))
        {
            text = text[..^1];
        }

        return text;
    }

    private static bool IsTrailingUrlPunctuation(char ch)
    {
        return ch == ')' || ch == ']' || ch == '.' || ch == ',';
    }

    public static string BuildOutline(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        using var reader = new StringReader(markdown);
        string? line;
        int count = 0;
        int lineNumber = 0;
        while ((line = reader.ReadLine()) != null)
        {
            lineNumber++;
            Match match = Regex.Match(line, "^(#{1,6})\\s+(.+?)\\s*#*\\s*$");
            if (!match.Success)
            {
                continue;
            }

            int level = match.Groups[1].Value.Length;
            string title = match.Groups[2].Value.Trim();
            if (title.Length == 0)
            {
                continue;
            }

            title = CompactHeading(title);
            sb.Append("L");
            sb.Append(lineNumber.ToString("D4"));
            sb.Append("  H");
            sb.Append(level);
            sb.Append("  ");
            sb.Append(' ', Math.Max(0, level - 1) * 2);
            sb.AppendLine(title);
            count++;
            if (count >= MaxOutlineItems)
            {
                sb.AppendLine("... outline truncated ...");
                break;
            }
        }

        return sb.ToString();
    }

    private static string CompactHeading(string heading)
    {
        string text = Regex.Replace(heading.Trim(), "\\s+", " ");
        if (text.Length <= MaxHeadingDisplayLength)
        {
            return text;
        }

        return text[..MaxHeadingDisplayLength] + "...";
    }
}
