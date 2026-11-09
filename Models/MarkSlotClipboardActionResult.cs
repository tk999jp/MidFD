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
    IReadOnlyList<string>? UnresolvedPaths = null,
    bool IsNoOp = false,
    int SyntaxInvalidEntryCount = 0,
    int OutsideRepositoryPathCount = 0,
    IReadOnlyList<MarkSlotClipboardEntryDiagnostic>? EntryDiagnostics = null,
    int? DeclaredCount = null,
    int ListedEntryCount = 0)
{
    public int RegisteredCount => Paths.Count;

    public int RecoveredEntryCount => EntryDiagnostics?.Count(static entry =>
        entry.Kind == MarkSlotClipboardEntryDiagnosticKind.RecoveredDescription) ?? 0;

    public int AmbiguousEntryCount => EntryDiagnostics?.Count(static entry =>
        entry.Kind == MarkSlotClipboardEntryDiagnosticKind.Ambiguous) ?? 0;

    public bool CountMismatch => DeclaredCount.HasValue && DeclaredCount.Value != ListedEntryCount;
}
