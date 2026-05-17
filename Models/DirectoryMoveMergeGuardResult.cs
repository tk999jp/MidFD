namespace MidFD.Models;

public sealed class DirectoryMoveMergeGuardResult
{
    public bool CanMerge { get; init; }
    public DirectoryMoveMergeAbortReason AbortReason { get; init; } = DirectoryMoveMergeAbortReason.None;
    public string? BlockingPath { get; init; }
    public string Message { get; init; } = string.Empty;
}
