using MidFD.Models;

namespace MidFD.Commands;

public sealed class CommandExecutionContext
{
    public CommandScope Scope { get; init; }
    public string Source { get; init; } = string.Empty;
    public SelectionResult? SelectionSnapshot { get; init; }
}
