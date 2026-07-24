using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using MidFD.Services;

namespace MidFD.Controls;

public sealed record BreadcrumbPathSegment(string DisplayText, string FullPath, bool IsRoot, bool IsCurrent);

public static class BreadcrumbPathModel
{
    public static string GetDisplayText(BreadcrumbPathSegment segment)
    {
        if (!segment.IsRoot || segment.DisplayText.Length != 2 || segment.DisplayText[1] != ':')
        {
            return segment.DisplayText;
        }

        return char.ToUpperInvariant(segment.DisplayText[0]) + ":";
    }

    public static IReadOnlyList<BreadcrumbPathSegment> Parse(string path)
    {
        string value = (path ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<BreadcrumbPathSegment>();
        }

        if (value.StartsWith(@"\\", StringComparison.Ordinal))
        {
            string[] parts = value[2..].TrimEnd('\\', '/').Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                return [new BreadcrumbPathSegment(value, value, true, true)];
            }

            string shareRoot = $@"\\{parts[0]}\{parts[1]}";
            var segments = new List<BreadcrumbPathSegment>
            {
                new(shareRoot, shareRoot, true, parts.Length == 2)
            };
            string current = shareRoot;
            for (int i = 2; i < parts.Length; i++)
            {
                current = $@"{current}\{parts[i]}";
                segments.Add(new BreadcrumbPathSegment(parts[i], current, false, i == parts.Length - 1));
            }
            return segments;
        }

        string? root = Path.GetPathRoot(value);
        if (!string.IsNullOrEmpty(root))
        {
            string rootDisplay = root.TrimEnd('\\', '/');
            var segments = new List<BreadcrumbPathSegment>
            {
                new(rootDisplay, root, true, false)
            };
            string[] parts = value[root.Length..].TrimEnd('\\', '/').Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            string current = root;
            if (parts.Length == 0)
            {
                segments[0] = segments[0] with { IsCurrent = true };
                return segments;
            }

            for (int i = 0; i < parts.Length; i++)
            {
                current = Path.Combine(current, parts[i]);
                segments.Add(new BreadcrumbPathSegment(parts[i], current, false, i == parts.Length - 1));
            }
            return segments;
        }

        string[] relativeParts = value.TrimEnd('\\', '/').Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        var relative = new List<BreadcrumbPathSegment>();
        string relativeCurrent = string.Empty;
        for (int i = 0; i < relativeParts.Length; i++)
        {
            relativeCurrent = string.IsNullOrEmpty(relativeCurrent)
                ? relativeParts[i]
                : Path.Combine(relativeCurrent, relativeParts[i]);
            relative.Add(new BreadcrumbPathSegment(relativeParts[i], relativeCurrent, i == 0, i == relativeParts.Length - 1));
        }
        return relative;
    }
}

public static class BreadcrumbPathLayout
{
    public static IReadOnlyList<int> SelectVisibleIndices(
        IReadOnlyList<string> labels,
        int availableWidth,
        Func<string, int> measureText,
        int separatorWidth,
        int ellipsisWidth)
    {
        if (labels.Count == 0 || availableWidth <= 0)
        {
            return Array.Empty<int>();
        }

        int Width(int index) => measureText(labels[index]);
        int FullWidth(IEnumerable<int> indices)
        {
            int[] values = indices.ToArray();
            return values.Sum(Width) + Math.Max(0, values.Length - 1) * separatorWidth;
        }

        if (FullWidth(Enumerable.Range(0, labels.Count)) <= availableWidth)
        {
            return Enumerable.Range(0, labels.Count).ToArray();
        }

        var selected = new List<int> { 0 };
        if (labels.Count > 1)
        {
            selected.Add(labels.Count - 1);
        }
        int tailStart = labels.Count - 2;
        while (tailStart > 0)
        {
            var candidate = new List<int>(selected) { tailStart };
            int withEllipsis = FullWidth(candidate) + separatorWidth + ellipsisWidth;
            if (withEllipsis > availableWidth)
            {
                break;
            }
            selected.Add(tailStart);
            tailStart--;
        }

        selected.Sort();
        return selected.Distinct().ToArray();
    }
}

public readonly record struct BreadcrumbSeparatorMetrics(
    int RegionWidth,
    int ChevronWidth,
    int ChevronHeight,
    float LineWidth)
{
    public static BreadcrumbSeparatorMetrics Create(float dpiScale, int rowHeight)
    {
        int regionWidth = Math.Max(1, (int)Math.Round(14 * dpiScale));
        int chevronWidth = Math.Max(1, (int)Math.Round(5 * dpiScale));
        int chevronHeight = Math.Clamp((int)Math.Round(rowHeight * 0.38f), 1, Math.Max(1, rowHeight / 2));
        float lineWidth = Math.Max(1f, 1.1f * dpiScale);
        return new BreadcrumbSeparatorMetrics(regionWidth, chevronWidth, chevronHeight, lineWidth);
    }

    public static BreadcrumbSeparatorMetrics FromGraphics(Graphics graphics, int rowHeight)
        => Create(graphics.DpiX / 96f, rowHeight);
}

public sealed class BreadcrumbPathControl : Control
{
    private const int SegmentHorizontalPadding = 4;
    private const int SegmentVerticalPadding = 1;
    private readonly List<(Rectangle Bounds, BreadcrumbPathSegment? Segment)> _hitAreas = new();
    private IReadOnlyList<BreadcrumbPathSegment> _segments = Array.Empty<BreadcrumbPathSegment>();
    private IReadOnlyList<BreadcrumbPathSegment> _hiddenSegments = Array.Empty<BreadcrumbPathSegment>();
    private int _hoveredArea = -1;
    private Color _normalTextColor = Color.FromArgb(130, 220, 220);
    private Color _currentBackgroundColor = Color.FromArgb(38, 100, 105);
    private Color _currentTextColor = Color.White;
    private Color _hoverBackgroundColor = Color.FromArgb(35, 75, 80);
    private Color _hoverTextColor = Color.FromArgb(205, 250, 250);
    private Color _ellipsisBackgroundColor = Color.FromArgb(48, 82, 86);
    private Color _ellipsisTextColor = Color.FromArgb(190, 235, 235);
    private Color _separatorColor = Color.FromArgb(105, 170, 175);

    public event EventHandler<string>? PathSelected;
    public event EventHandler? BackgroundSelected;

    public BreadcrumbPathControl()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.SupportsTransparentBackColor,
            true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        TabStop = false;
    }

    public void SetPath(string path)
    {
        _segments = BreadcrumbPathModel.Parse(path);
        Invalidate();
    }

    public void ApplyThemeColors(Color backgroundColor, Color normalTextColor, Color accentColor)
    {
        _normalTextColor = EnsureContrast(normalTextColor, backgroundColor);
        _currentBackgroundColor = BlendColor(backgroundColor, accentColor, 0.24);
        _hoverBackgroundColor = BlendColor(backgroundColor, accentColor, 0.14);
        _ellipsisBackgroundColor = BlendColor(backgroundColor, accentColor, 0.18);
        _currentTextColor = EnsureContrast(_normalTextColor, _currentBackgroundColor);
        _hoverTextColor = EnsureContrast(_normalTextColor, _hoverBackgroundColor);
        _ellipsisTextColor = EnsureContrast(_normalTextColor, _ellipsisBackgroundColor);
        _separatorColor = EnsureContrast(accentColor, backgroundColor);
        Invalidate();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int area = FindHitArea(e.Location);
        if (area != _hoveredArea)
        {
            _hoveredArea = area;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoveredArea != -1)
        {
            _hoveredArea = -1;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        _hitAreas.Clear();
        _hiddenSegments = Array.Empty<BreadcrumbPathSegment>();
        if (_segments.Count == 0)
        {
            return;
        }

        int height = ClientSize.Height;
        BreadcrumbSeparatorMetrics separator = BreadcrumbSeparatorMetrics.FromGraphics(e.Graphics, height);
        string[] labels = _segments.Select(BreadcrumbPathModel.GetDisplayText).ToArray();
        int[] visible = BreadcrumbPathLayout.SelectVisibleIndices(
            labels,
            ClientSize.Width,
            text => TextRenderer.MeasureText(e.Graphics, text, Font).Width + SegmentHorizontalPadding * 2,
            separator.RegionWidth,
            TextRenderer.MeasureText(e.Graphics, "…", Font).Width).ToArray();
        HashSet<int> visibleSet = visible.ToHashSet();
        _hiddenSegments = _segments
            .Where((_, index) => !visibleSet.Contains(index))
            .ToArray();
        var drawItems = new List<(string Text, BreadcrumbPathSegment? Segment)>();
        for (int visibleIndex = 0; visibleIndex < visible.Length; visibleIndex++)
        {
            int i = visible[visibleIndex];
            if (visibleIndex > 0 && i > visible[visibleIndex - 1] + 1)
            {
                drawItems.Add(("…", null));
            }
            drawItems.Add((labels[i], _segments[i]));
        }

        int x = 2;
        for (int i = 0; i < drawItems.Count; i++)
        {
            if (i > 0)
            {
                DrawSeparator(e.Graphics, new Rectangle(x, 0, separator.RegionWidth, height), separator);
                x += separator.RegionWidth;
            }

            var item = drawItems[i];
            int textWidth = TextRenderer.MeasureText(e.Graphics, item.Text, Font).Width;
            int width = textWidth + SegmentHorizontalPadding * 2;
            var bounds = new Rectangle(x, SegmentVerticalPadding, width, Math.Max(1, height - SegmentVerticalPadding * 2));
            bool hovered = _hoveredArea == _hitAreas.Count;
            bool isCurrent = item.Segment?.IsCurrent == true;
            Color background = item.Segment == null
                ? _ellipsisBackgroundColor
                : isCurrent
                    ? _currentBackgroundColor
                    : hovered
                        ? _hoverBackgroundColor
                        : Color.Transparent;
            if (background != Color.Transparent)
            {
                using var brush = new SolidBrush(background);
                e.Graphics.FillRectangle(brush, bounds);
            }
            Color color = item.Segment == null
                ? _ellipsisTextColor
                : isCurrent
                    ? _currentTextColor
                    : hovered
                        ? _hoverTextColor
                        : _normalTextColor;
            var textBounds = new Rectangle(bounds.Left + SegmentHorizontalPadding, bounds.Top, textWidth, bounds.Height);
            TextRenderer.DrawText(e.Graphics, item.Text, Font, textBounds, color, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            _hitAreas.Add((bounds, item.Segment));
            x += width;
        }
    }

    private void DrawSeparator(Graphics graphics, Rectangle bounds, BreadcrumbSeparatorMetrics metrics)
    {
        int centerX = bounds.Left + bounds.Width / 2;
        int top = bounds.Top + (bounds.Height - metrics.ChevronHeight) / 2;
        int halfWidth = Math.Max(1, metrics.ChevronWidth / 2);
        int centerY = top + metrics.ChevronHeight / 2;
        int bottom = top + metrics.ChevronHeight;
        using var pen = new Pen(_separatorColor, metrics.LineWidth)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        SmoothingMode previous = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.DrawLine(pen, centerX - halfWidth, top, centerX + halfWidth, centerY);
        graphics.DrawLine(pen, centerX + halfWidth, centerY, centerX - halfWidth, bottom);
        graphics.SmoothingMode = previous;
    }

    private static Color BlendColor(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        int r = (int)Math.Round(from.R + ((to.R - from.R) * amount));
        int g = (int)Math.Round(from.G + ((to.G - from.G) * amount));
        int b = (int)Math.Round(from.B + ((to.B - from.B) * amount));
        return Color.FromArgb(255, r, g, b);
    }

    private static Color EnsureContrast(Color foreground, Color background)
    {
        double difference = Math.Abs(
            FileListColorResolver.GetRelativeLuminance(foreground) -
            FileListColorResolver.GetRelativeLuminance(background));
        if (difference >= 0.28)
        {
            return foreground;
        }

        return FileListColorResolver.GetRelativeLuminance(background) > 0.5
            ? Color.FromArgb(24, 24, 24)
            : Color.FromArgb(240, 240, 240);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        foreach ((Rectangle bounds, BreadcrumbPathSegment? segment) in _hitAreas)
        {
            if (!bounds.Contains(e.Location))
            {
                continue;
            }

            if (segment != null)
            {
                PathSelected?.Invoke(this, segment.FullPath);
            }
            else
            {
                ShowHiddenAncestorMenu();
            }
            return;
        }

        BackgroundSelected?.Invoke(this, EventArgs.Empty);
    }

    private int FindHitArea(Point point)
    {
        for (int i = 0; i < _hitAreas.Count; i++)
        {
            if (_hitAreas[i].Bounds.Contains(point))
            {
                return i;
            }
        }
        return -1;
    }

    private void ShowHiddenAncestorMenu()
    {
        var menu = new ContextMenuStrip();
        foreach (BreadcrumbPathSegment segment in _hiddenSegments)
        {
            string path = segment.FullPath;
            menu.Items.Add(new ToolStripMenuItem(segment.DisplayText) { Tag = path });
        }
        foreach (ToolStripMenuItem item in menu.Items.OfType<ToolStripMenuItem>())
        {
            item.Click += (_, _) => PathSelected?.Invoke(this, (string)item.Tag!);
        }
        if (menu.Items.Count > 0)
        {
            menu.Closed += (_, _) => menu.Dispose();
            menu.Show(this, new Point(2, Height));
        }
        else
        {
            menu.Dispose();
        }
    }
}
