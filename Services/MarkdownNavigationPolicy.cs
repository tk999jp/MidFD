namespace MidFD.Services;

internal enum MarkdownNavigationDecision
{
    AllowInitialDocument,
    AllowSameDocumentAnchor,
    ConfirmExternalHttp,
    BlockRelative,
    BlockUnsafeScheme
}

internal readonly record struct MarkdownNavigationResult(
    MarkdownNavigationDecision Decision,
    Uri? TargetUri)
{
    public bool AllowsInternalNavigation => Decision is
        MarkdownNavigationDecision.AllowInitialDocument or
        MarkdownNavigationDecision.AllowSameDocumentAnchor;
}

internal static class MarkdownNavigationPolicy
{
    public static MarkdownNavigationResult Evaluate(
        string? target,
        Uri? currentDocumentUri,
        bool isInitialDocumentNavigation)
    {
        if (isInitialDocumentNavigation)
        {
            return new MarkdownNavigationResult(MarkdownNavigationDecision.AllowInitialDocument, null);
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            return new MarkdownNavigationResult(MarkdownNavigationDecision.BlockUnsafeScheme, null);
        }

        string trimmed = target.Trim();
        if (trimmed.StartsWith('#') && trimmed.Length > 1)
        {
            return new MarkdownNavigationResult(
                MarkdownNavigationDecision.AllowSameDocumentAnchor,
                Uri.TryCreate(trimmed, UriKind.Relative, out Uri? anchor) ? anchor : null);
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? targetUri))
        {
            return new MarkdownNavigationResult(MarkdownNavigationDecision.BlockRelative, null);
        }

        if (targetUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || targetUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return new MarkdownNavigationResult(MarkdownNavigationDecision.ConfirmExternalHttp, targetUri);
        }

        if (targetUri.Scheme.Equals(Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase)
            || targetUri.Scheme.Equals("javascript", StringComparison.OrdinalIgnoreCase)
            || targetUri.Scheme.Equals("vbscript", StringComparison.OrdinalIgnoreCase)
            || targetUri.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase))
        {
            return new MarkdownNavigationResult(MarkdownNavigationDecision.BlockUnsafeScheme, targetUri);
        }

        if (targetUri.Fragment.Length > 0
            && currentDocumentUri != null
            && !currentDocumentUri.Scheme.Equals(Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase)
            && SameDocumentWithoutFragment(targetUri, currentDocumentUri))
        {
            return new MarkdownNavigationResult(MarkdownNavigationDecision.AllowSameDocumentAnchor, targetUri);
        }

        return new MarkdownNavigationResult(MarkdownNavigationDecision.BlockUnsafeScheme, targetUri);
    }

    private static bool SameDocumentWithoutFragment(Uri left, Uri right)
    {
        UriBuilder leftBuilder = new(left) { Fragment = string.Empty };
        UriBuilder rightBuilder = new(right) { Fragment = string.Empty };
        return Uri.Equals(leftBuilder.Uri, rightBuilder.Uri);
    }
}

internal static class MarkdownPreviewBrowserPolicy
{
    public const bool IsStandardContextMenuEnabled = false;
    public const bool CancelNewWindow = true;
}
