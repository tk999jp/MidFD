namespace MidFD.Models;

public sealed class WorkspaceSnapshotEntry
{
    public string SnapshotId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public DateTime CreatedAtUtc { get; init; }
    public DateTime UpdatedAtUtc { get; init; }
    public int CategoryCount { get; init; }
    public int TabCount { get; init; }
    public int MarkedCount { get; init; }
    public string ActivePath { get; init; } = string.Empty;
}
