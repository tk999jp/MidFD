namespace MidFD.Models;

public readonly record struct BrowserTabContextMenuState(
    bool IsLocked,
    bool IsReadOnly,
    bool CanClearFilterLock,
    bool CanCloseRight,
    bool CanCloseLeft,
    bool CanCloseOther);
