using System.IO;
using System.Text.RegularExpressions;

namespace MidFD.Services;

public static class RenameTemplateEngine
{
    public static string BuildName(string sourcePath, int sequenceNumber, int numberWidth, string template)
    {
        string safeTemplate = template ?? string.Empty;
        string originalName = Path.GetFileName(sourcePath);
        string extension = Path.GetExtension(originalName);
        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(originalName);
        string? parentDirectory = Path.GetDirectoryName(sourcePath);
        string directoryName = string.IsNullOrEmpty(parentDirectory)
            ? string.Empty
            : Path.GetFileName(parentDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        string numberText = numberWidth > 0
            ? sequenceNumber.ToString($"D{numberWidth}")
            : sequenceNumber.ToString();

        string templateWithExplicitNumberWidth = Regex.Replace(
            safeTemplate,
            @"(?<!\$)\$(\d+)N",
            match =>
            {
                if (!int.TryParse(match.Groups[1].Value, out int explicitWidth) || explicitWidth <= 0)
                {
                    return match.Value;
                }

                return explicitWidth > 0
                    ? sequenceNumber.ToString($"D{explicitWidth}")
                    : sequenceNumber.ToString();
            });

        return templateWithExplicitNumberWidth
            .Replace("$F", fileNameWithoutExtension)
            .Replace("$E", extension)
            .Replace("$D", directoryName)
            .Replace("$N", numberText);
    }
}
