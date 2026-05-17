namespace MidFD.Models;

public sealed class CopyCollisionDecision
{
    public CopyCollisionPolicy Policy { get; init; } = CopyCollisionPolicy.Cancel;
    public bool ApplyToAll { get; init; }
    public string? ResolvedTargetPath { get; init; }
}
