namespace MidFD.Models;

public sealed class DirectoryCopyPlanEntry
{
    public string SourcePath { get; init; } = string.Empty;
    public string DestinationPath { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
}
