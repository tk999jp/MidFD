using System.Drawing;

namespace MidFD.Helpers;

public static class CommandHintOverlayLayout
{
    public readonly record struct Metrics(
        int Padding,
        int TitleHeight,
        int TitleGap,
        int ExplanationHeight,
        int ContextLineHeight,
        int ContextLineSpacing,
        int ContextGap,
        int HeaderHeight,
        int RowHeight,
        int FooterHeight,
        int MinimumVisibleRows)
    {
        public int RowsTopOffset =>
            Padding +
            TitleHeight +
            TitleGap +
            ExplanationHeight +
            ContextGap +
            (ContextLineHeight * 2) +
            ContextLineSpacing +
            HeaderHeight +
            4;
    }

    public static Metrics DefaultMetrics { get; } = new(
        Padding: 14,
        TitleHeight: 20,
        TitleGap: 4,
        ExplanationHeight: 24,
        ContextLineHeight: 18,
        ContextLineSpacing: 2,
        ContextGap: 6,
        HeaderHeight: 18,
        RowHeight: 22,
        FooterHeight: 20,
        MinimumVisibleRows: 2);

    public static Rectangle GetBounds(Size panelSize, int rowCount, Metrics metrics)
    {
        int width = Math.Min(860, Math.Max(620, panelSize.Width - 72));
        int availableHeight = Math.Max(0, panelSize.Height - 32);
        int desiredRows = Math.Max(metrics.MinimumVisibleRows, Math.Min(8, Math.Max(1, rowCount)));
        int desiredHeight = metrics.RowsTopOffset + (desiredRows * metrics.RowHeight) + metrics.Padding;
        if (rowCount > desiredRows)
        {
            desiredHeight += metrics.FooterHeight;
        }
        int minimumHeight = metrics.RowsTopOffset + (metrics.MinimumVisibleRows * metrics.RowHeight) + metrics.Padding;
        int height = Math.Min(440, Math.Max(minimumHeight, Math.Min(availableHeight, desiredHeight)));
        int left = Math.Max(12, panelSize.Width - width - 12);
        return new Rectangle(left, 12, width, height);
    }

    public static int GetVisibleRowCount(Rectangle overlayRect, Metrics metrics)
    {
        int rowTop =
            overlayRect.Top +
            metrics.Padding -
            2 +
            metrics.TitleHeight +
            metrics.TitleGap +
            metrics.ExplanationHeight +
            2 +
            metrics.ContextLineHeight +
            metrics.ContextLineSpacing +
            metrics.ContextLineHeight +
            metrics.ContextGap +
            metrics.HeaderHeight +
            4;
        return Math.Max(1, (overlayRect.Bottom - metrics.Padding - rowTop) / metrics.RowHeight);
    }
}
