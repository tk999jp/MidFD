namespace MidFD.Models;

public sealed record NavigationRefreshBatch(
    string TargetPath,
    long WatcherGeneration,
    IReadOnlyCollection<string> Reasons,
    int EventCount,
    string? ExceptionType,
    string? ExceptionMessage);
