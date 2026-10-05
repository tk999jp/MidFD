using System;
using System.Text.RegularExpressions;

namespace MidFD.Services;

public static class NamePatternMatcher
{
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    public static bool IsMatch(
        string name,
        string? pattern,
        bool useRegex,
        bool caseSensitive = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(pattern))
        {
            return true;
        }

        bool matched;
        if (useRegex)
        {
            matched = MidFdSearchRegexContract.Create(pattern, caseSensitive).IsMatch(name, cancellationToken);
        }
        else if (pattern.Contains('*') || pattern.Contains('?'))
        {
            string regexPattern = "^" + Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";
            matched = new Regex(regexPattern, caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase, MatchTimeout)
                .IsMatch(name);
        }
        else matched = name.Contains(pattern, caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

        cancellationToken.ThrowIfCancellationRequested();
        return matched;
    }

    public static bool TryValidate(string? pattern, bool useRegex, out string? error)
    {
        error = null;
        if (!useRegex || string.IsNullOrEmpty(pattern))
        {
            return true;
        }

        return MidFdSearchRegexContract.TryValidate(pattern, out error);
    }
}
