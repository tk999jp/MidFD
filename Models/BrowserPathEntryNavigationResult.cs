namespace MidFD.Models;

internal enum BrowserPathEntryTargetKind
{
    None,
    Directory,
    File
}

internal sealed class BrowserPathEntryNavigationResult
{
    public BrowserPathEntryTargetKind TargetKind { get; init; }
    public string ResolvedPath { get; init; } = string.Empty;
    public string StatusMessage { get; init; } = string.Empty;
}
