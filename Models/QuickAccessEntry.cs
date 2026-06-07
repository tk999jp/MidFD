namespace MidFD.Models;

public enum QuickAccessEntryKind
{
    Bookmark,
    Recent,
    Alias,
    History,
    ExternalCommand
}

public enum QuickAccessCommandWorkingDirectoryMode
{
    CurrentPath,
    ExecutableDirectory
}

public enum QuickAccessCommandTargetMode
{
    None,
    CurrentPath,
    CurrentItem,
    CurrentFile,
    CurrentDirectory,
    MarkedItems
}

public sealed class QuickAccessCommandContext
{
    public string CurrentPath { get; init; } = string.Empty;
    public string? CurrentItemPath { get; init; }
    public string? CurrentItemName { get; init; }
    public bool CurrentItemIsDirectory { get; init; }
    public IReadOnlyList<string> MarkedPaths { get; init; } = Array.Empty<string>();
}

public class QuickAccessEntry
{
    public string DisplayName { get; set; } = string.Empty;
    public string? CategoryName { get; set; }
    public string Path { get; set; } = string.Empty;
    public QuickAccessEntryKind Kind { get; set; }
    public string ExecutablePath { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public QuickAccessCommandWorkingDirectoryMode WorkingDirectoryMode { get; set; } = QuickAccessCommandWorkingDirectoryMode.CurrentPath;
    public QuickAccessCommandTargetMode TargetMode { get; set; } = QuickAccessCommandTargetMode.None;

    public QuickAccessEntry Clone()
    {
        return new QuickAccessEntry
        {
            DisplayName = DisplayName,
            CategoryName = CategoryName,
            Path = Path,
            Kind = Kind,
            ExecutablePath = ExecutablePath,
            Arguments = Arguments,
            WorkingDirectoryMode = WorkingDirectoryMode,
            TargetMode = TargetMode
        };
    }
}
