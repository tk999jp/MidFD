namespace MidFD.Models;

public sealed class FunctionKeyDefinition
{
    public FunctionKeyProfile Profile { get; init; }
    public int KeyNumber { get; init; }
    public bool IsShift { get; init; }
    public bool IsCtrl { get; init; }
    public bool IsAlt { get; init; }
    public string? CommandId { get; init; }
}
