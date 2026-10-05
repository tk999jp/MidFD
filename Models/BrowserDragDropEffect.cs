using System;

namespace MidFD.Models;

[Flags]
internal enum BrowserDragDropEffect
{
    None = 0,
    Copy = 1,
    Move = 2,
    Link = 4
}
