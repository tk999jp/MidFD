using System.Drawing;

namespace MidFD.Helpers;

public static class WindowPlacementBoundsHelper
{
    public static bool IsCollapsedWindowBounds(Rectangle bounds, int minimumWidth, int minimumHeight)
    {
        return !IsSaneNormalBounds(bounds, minimumWidth, minimumHeight);
    }

    public static bool IsSaneNormalBounds(Rectangle bounds, int minimumWidth, int minimumHeight)
    {
        return bounds.Width >= minimumWidth && bounds.Height >= minimumHeight;
    }

    public static string FormatBoundsForLog(Rectangle bounds)
    {
        return $"({bounds.Left},{bounds.Top},{bounds.Width},{bounds.Height})";
    }
}
