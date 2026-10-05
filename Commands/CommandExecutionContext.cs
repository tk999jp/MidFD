using MidFD.Models;

namespace MidFD.Commands;

public sealed class CommandExecutionContext
{
    public CommandScope Scope { get; init; }
    public string Source { get; init; } = string.Empty;
    public SelectionResult? SelectionSnapshot { get; init; }
    public string? ContextTargetPath { get; init; }
    public string? CategoryId { get; init; }
    public int? ContextTabIndex { get; init; }
    public Guid? ContextBrowserTabId { get; init; }
    public Guid? RuntimeTargetId { get; init; }
    public string? FileExtension { get; init; }
    public SevenZipHashAlgorithm? HashAlgorithm { get; init; }
}
