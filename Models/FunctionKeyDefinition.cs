namespace MidFD.Models;

public sealed class FunctionKeyDefinition
{
    public int KeyNumber { get; init; }
    public FunctionKeyAction Action { get; init; }
    public string Label { get; init; } = string.Empty;
    public string? ShortcutHint { get; init; }
    public bool VisibleOnFunctionBar { get; init; } = true;
}
