namespace MidFD.Models;

public sealed class PasteCollisionDialogResult
{
    public PasteCollisionAction Action { get; init; } = PasteCollisionAction.Cancel;
    public bool ApplyToAll { get; init; }
}
