namespace MidFD.Models;

internal enum BrowserContextMenuKind
{
    Background,
    Item,
    MultiSelection
}

internal readonly record struct BrowserContextMenuTargetResolution(
    BrowserContextMenuKind Kind,
    int TargetIndex,
    SelectionResult Selection)
{
    public bool HasItemTarget => Kind != BrowserContextMenuKind.Background;
}
