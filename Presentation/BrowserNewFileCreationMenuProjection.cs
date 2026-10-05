using MidFD.Services;

namespace MidFD.Presentation;

internal readonly record struct BrowserNewFileCreationMenuEntry(
    string Text,
    string? Extension,
    bool IsSeparator,
    bool IsGenericFileNameEntry);

internal static class BrowserNewFileCreationMenuProjection
{
    public static IReadOnlyList<BrowserNewFileCreationMenuEntry> Build(IEnumerable<string>? configuredExtensions)
    {
        IReadOnlyList<string> extensions = NewFileExtensionHelper.ResolveConfiguredExtensions(configuredExtensions);
        var entries = new List<BrowserNewFileCreationMenuEntry>(extensions.Count + 2);
        foreach (string extension in extensions)
        {
            entries.Add(new BrowserNewFileCreationMenuEntry(extension, extension, false, false));
        }

        if (entries.Count > 0)
        {
            entries.Add(new BrowserNewFileCreationMenuEntry(string.Empty, null, true, false));
        }

        entries.Add(new BrowserNewFileCreationMenuEntry("任意のファイル名...", null, false, true));
        return entries;
    }
}
