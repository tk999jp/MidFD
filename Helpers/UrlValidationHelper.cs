using System;

namespace MidFD.Helpers;

public static class UrlValidationHelper
{
    public static bool IsValidWebUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
        }
        return false;
    }
}
