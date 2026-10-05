using MidFD.Configuration;

namespace MidFD.Services;

internal static class NewFileExtensionHelper
{
    public static bool TryNormalize(string? value, out string normalized, out string errorMessage)
    {
        normalized = string.Empty;
        errorMessage = string.Empty;
        string candidate = value?.Trim() ?? string.Empty;
        if (candidate.Length == 0)
        {
            errorMessage = "拡張子を入力してください。";
            return false;
        }

        if (!candidate.StartsWith(".", StringComparison.Ordinal))
        {
            candidate = "." + candidate;
        }

        if (candidate is "." or "..")
        {
            errorMessage = "「.」または「..」は拡張子として使用できません。";
            return false;
        }

        if (candidate.Contains(Path.DirectorySeparatorChar) ||
            candidate.Contains(Path.AltDirectorySeparatorChar) ||
            candidate.Any(Path.GetInvalidFileNameChars().Contains))
        {
            errorMessage = "拡張子にパス区切りやファイル名として使用できない文字は含められません。";
            return false;
        }

        normalized = candidate;
        return true;
    }

    public static string ApplyToFileName(string fileName, string? extension)
    {
        if (extension == null)
        {
            return fileName;
        }

        if (!TryNormalize(extension, out string normalized, out _))
        {
            return fileName;
        }

        return fileName.EndsWith(normalized, StringComparison.OrdinalIgnoreCase)
            ? fileName
            : fileName + normalized;
    }

    public static IReadOnlyList<string> ResolveConfiguredExtensions(IEnumerable<string>? configured)
    {
        if (configured == null)
        {
            return FileOperationsSettings.DefaultNewFileExtensions;
        }

        var result = new List<string>();
        foreach (string? value in configured)
        {
            if (!TryNormalize(value, out string normalized, out _) ||
                result.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            result.Add(normalized);
        }

        return result;
    }

    public static List<string> CreateDefaultExtensions()
        => new(FileOperationsSettings.DefaultNewFileExtensions);
}
