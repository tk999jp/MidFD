namespace MidFD.Models;

public sealed record MarkSlotClipboardActionResult(
    bool Success,
    string Message,
    IReadOnlyList<string> Paths,
    string? RepositoryRoot,
    int MissingFileCount,
    int DirectoryPathCount,
    int DuplicatePathCount,
    int IgnoredEarlierResultCount,
    IReadOnlyList<string>? UnresolvedPaths = null)
{
    public int RegisteredCount => Paths.Count;
}
