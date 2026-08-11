using MidFD.Controls;

namespace MidFD.Presentation;

/// <summary>上部breadcrumbと同じsegment文法で、縦型navigation用に幅適応表示を生成する。</summary>
public static class BrowserTabNavigationPathPresentationHelper
{
    private const string Ellipsis = "…";
    private const string Separator = ">";

    public static bool IsUncPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && path.StartsWith(@"\\", StringComparison.Ordinal);

    public static string FormatForWidth(string path, int availableWidth, Func<string, int> measure)
    {
        ArgumentNullException.ThrowIfNull(measure);
        if (string.IsNullOrWhiteSpace(path) || availableWidth <= 0) return path;

        string[] segments = BreadcrumbPathModel.Parse(path)
            .Select(BreadcrumbPathModel.GetDisplayText)
            .ToArray();
        if (segments.Length == 0) return path;

        string full = Compose(segments);
        if (Fits(full, availableWidth, measure)) return full;

        bool unc = IsUncPath(path);
        foreach (string[] candidate in CreateCollapsedCandidates(segments, unc))
        {
            string text = Compose(candidate);
            if (Fits(text, availableWidth, measure)) return text;
        }

        return ShrinkRootAndCurrent(segments[0], segments[^1], availableWidth, measure, unc, segments.Length > 2);
    }

    public static string FormatUncForWidth(string path, int availableWidth, Func<string, int> measure) =>
        FormatForWidth(path, availableWidth, measure);

    public static string FormatBaseAndRelativeForWidth(
        string baseTitle,
        string? relativeSuffix,
        int availableWidth,
        Func<string, int> measure)
    {
        ArgumentNullException.ThrowIfNull(measure);
        if (availableWidth <= 0) return Ellipsis;
        string suffix = relativeSuffix?.Trim('>') ?? string.Empty;
        if (string.IsNullOrEmpty(suffix)) return MiddleEllipsize(baseTitle, availableWidth, measure);

        string full = $"{baseTitle} >{suffix}";
        if (Fits(full, availableWidth, measure)) return full;

        string tail = suffix.Split('>', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? suffix;
        string collapsed = $"{baseTitle} >{Ellipsis}>{tail}";
        if (Fits(collapsed, availableWidth, measure)) return collapsed;

        int baseWidth = measure(baseTitle);
        if (baseWidth <= availableWidth)
        {
            return baseTitle + " >" + MiddleEllipsize(suffix, Math.Max(1, availableWidth - baseWidth - measure(" >")), measure);
        }

        return MiddleEllipsize(baseTitle, availableWidth, measure);
    }

    private static IEnumerable<string[]> CreateCollapsedCandidates(string[] segments, bool unc)
    {
        if (segments.Length <= 2) yield break;

        if (unc && segments.Length >= 4)
        {
            yield return [segments[0], segments[1], Ellipsis, segments[^1]];
        }
        else if (!unc && segments.Length >= 4)
        {
            yield return [segments[0], segments[1], Ellipsis, segments[^1]];
        }

        yield return [segments[0], Ellipsis, segments[^1]];
    }

    private static string ShrinkRootAndCurrent(string root, string current, int availableWidth, Func<string, int> measure, bool rootIsUnc, bool omittedMiddle)
    {
        string originalRoot = root;
        string originalCurrent = current;
        int rootWidth = measure(root);
        int currentWidth = measure(current);
        string text = omittedMiddle ? Compose([root, Ellipsis, current]) : Compose([root, current]);
        while (!Fits(text, availableWidth, measure))
        {
            if (rootWidth > measure(Ellipsis)) rootWidth--;
            else if (currentWidth > measure(Ellipsis)) currentWidth--;
            else break;
            root = rootIsUnc
                ? EndEllipsize(originalRoot, rootWidth, measure)
                : MiddleEllipsize(originalRoot, rootWidth, measure);
            current = MiddleEllipsize(originalCurrent, currentWidth, measure);
            text = omittedMiddle ? Compose([root, Ellipsis, current]) : Compose([root, current]);
        }
        return text;
    }

    public static string MiddleEllipsize(string value, int availableWidth, Func<string, int> measure)
    {
        ArgumentNullException.ThrowIfNull(measure);
        if (string.IsNullOrEmpty(value) || measure(value) <= availableWidth) return value;
        if (availableWidth <= measure(Ellipsis)) return Ellipsis;

        for (int preserved = value.Length; preserved >= 2; preserved--)
        {
            int prefixLength = (preserved + 1) / 2;
            int suffixLength = preserved / 2;
            string candidate = value[..prefixLength] + Ellipsis + value[^suffixLength..];
            if (measure(candidate) <= availableWidth) return candidate;
        }
        return Ellipsis;
    }

    /// <summary>UNC hostは接続先の識別に必要な先頭側を優先して残す。</summary>
    public static string EndEllipsize(string value, int availableWidth, Func<string, int> measure)
    {
        ArgumentNullException.ThrowIfNull(measure);
        if (string.IsNullOrEmpty(value) || measure(value) <= availableWidth) return value;
        if (availableWidth <= measure(Ellipsis)) return Ellipsis;

        string[] octets = value.Split('.');
        if (octets.Length == 4 && octets.All(static octet => octet.Length > 0 && octet.All(char.IsAsciiDigit)))
        {
            for (int count = octets.Length - 1; count >= 1; count--)
            {
                string candidate = string.Join('.', octets.Take(count)) + "." + Ellipsis;
                if (measure(candidate) <= availableWidth) return candidate;
            }
        }

        for (int length = value.Length; length >= 1; length--)
        {
            string candidate = value[..length] + Ellipsis;
            if (measure(candidate) <= availableWidth) return candidate;
        }
        return Ellipsis;
    }

    private static string Compose(IEnumerable<string> segments) => string.Join(Separator, segments);
    private static bool Fits(string value, int availableWidth, Func<string, int> measure) => measure(value) <= availableWidth;
}
