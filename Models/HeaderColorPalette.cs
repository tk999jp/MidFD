using System.Drawing;

namespace MidFD.Models;

public sealed class HeaderColorPalette
{
    public required Color HeaderTitleFore { get; init; }
    public required Color HeaderClockFore { get; init; }
    public required Color HeaderRow2Fore { get; init; }
    public required Color HeaderRow2Value { get; init; }
    public required Color HeaderPathFore { get; init; }
    public required Color HeaderMetaFore { get; init; }
    public required Color HeaderNameFore { get; init; }
}
