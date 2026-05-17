namespace MidFD.Models;

public sealed class DirectoryMergeDecision
{
    public DirectoryMergePolicy Policy { get; init; } = DirectoryMergePolicy.Cancel;
    public bool ApplyToAll { get; init; }
}
