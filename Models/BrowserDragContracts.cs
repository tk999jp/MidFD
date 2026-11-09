using System;

namespace MidFD.Models;

[Flags]
public enum BrowserDragModifiers
{
    None = 0,
    Shift = 1,
    Control = 2,
    Alt = 4
}

public enum BrowserDragMouseButton
{
    None,
    Left,
    Right
}

[Flags]
public enum BrowserDragEffect
{
    None = 0,
    Copy = 1,
    Move = 2,
    Link = 4
}
