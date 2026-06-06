namespace MidFD.Commands;

public sealed class CommandDefinition
{
    public string Id { get; init; } = string.Empty;
    public CommandScope Scope { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool IsCustomizable { get; init; }
    public bool IsDangerous { get; init; }
}
