using System.Text.RegularExpressions;
using MidFD.Models;

namespace MidFD.Services;

public static class RenameRegexEngine
{
    public static bool TryCreateRegex(RenameRegexOptions options, out Regex? regex, out string? errorMessage)
    {
        try
        {
            regex = new Regex(options.Pattern ?? string.Empty, BuildRegexOptions(options));
            errorMessage = null;
            return true;
        }
        catch (ArgumentException ex)
        {
            regex = null;
            errorMessage = $"正規表現エラー: {ex.Message}";
            return false;
        }
    }

    public static string Apply(string sourceName, Regex regex, RenameRegexOptions options)
    {
        if (options.Global)
        {
            return regex.Replace(sourceName, options.Replacement ?? string.Empty);
        }

        return regex.Replace(sourceName, options.Replacement ?? string.Empty, 1);
    }

    private static RegexOptions BuildRegexOptions(RenameRegexOptions options)
    {
        var regexOptions = RegexOptions.CultureInvariant;
        if (options.IgnoreCase)
        {
            regexOptions |= RegexOptions.IgnoreCase;
        }

        if (options.Multiline)
        {
            regexOptions |= RegexOptions.Multiline;
        }

        return regexOptions;
    }
}
