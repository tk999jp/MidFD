namespace MidFD.Commands;

[Flags]
public enum CommandInputSurface
{
    None = 0,
    Keyboard = 1,
    FunctionBar = 2,
    MouseGesture = 4
}

public sealed class CommandDefinition
{
    public string Id { get; init; } = string.Empty;
    public CommandScope Scope { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool IsCustomizable { get; init; }
    public bool IsDangerous { get; init; }
    public CommandInputSurface InputSurfaces { get; init; }
}
