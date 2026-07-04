namespace MidFD.Models;

public readonly record struct BrowserTabCategoryContextMenuState(
    bool HasTargetCategory,
    bool CanMoveLeft,
    bool CanMoveRight);
