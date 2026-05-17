namespace MidFD.Models;

public sealed class DirectoryPasteMergeGuardResult
{
    public bool CanMerge { get; init; }
    public DirectoryPasteMergeAbortReason AbortReason { get; init; } = DirectoryPasteMergeAbortReason.None;
    public string? BlockingPath { get; init; }
    public string Message { get; init; } = string.Empty;
}
