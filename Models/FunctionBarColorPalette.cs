using System.Drawing;

namespace MidFD.Models;

public sealed class FunctionBarColorPalette
{
    public required Color BackColor { get; init; }
    public required Color BorderColor { get; init; }
    public required Color EnabledBackColor { get; init; }
    public required Color EnabledTextColor { get; init; }
    public required Color DisabledBackColor { get; init; }
    public required Color DisabledTextColor { get; init; }
    public required Color DisabledBorderColor { get; init; }
    public required Color HotKeyBackColor { get; init; }
    public required Color HotKeyTextColor { get; init; }
    public required Color HoverBackColor { get; init; }
    public required Color PressedBackColor { get; init; }
}
