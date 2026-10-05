using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using MidFD.Configuration;
using MidFD.Helpers;
using MidFD.Presentation;
using MidFD.Services;

namespace MidFD.Controls;

/// <summary>
/// Browserのowner-draw／多列表示に必要な描画判断をShellから分離する。
/// Control値は描画対象として受け取るが、MainFormの状態やイベントには依存しない。
/// </summary>
internal sealed class BrowserFileListRenderer
{
    internal const string FilterIndicatorText = "フィルタ適用中";

    internal readonly record struct NameExtensionAlignedFields(
        Rectangle NameBounds,
        Rectangle ExtensionBounds,
        string NameText,
        string ExtensionText,
        bool IsEllipsized);

    internal readonly record struct Options(
        FileListColorResolver.ResolvedColors Colors,
        string? ColorTheme,
        IReadOnlyCollection<string> MarkedPaths,
        bool UseUnderlineCursor,
        bool ShowItemIcons,
        bool ShowExtensions,
        bool ShowDirectoryMarker,
        BrowserFileDisplayMode DisplayMode,
        string? DateFormat,
        bool ShowFilterIndicator,
        Color FilterIndicatorColor);

    public void DrawSubItem(
        DrawListViewSubItemEventArgs e,
        Options options)
    {
        if (e.Item == null)
        {
            return;
        }

        bool selected = e.Item.Selected;
        bool isMarked = IsMarked(e.Item.Tag as string, options.MarkedPaths);
        Color background = options.Colors.Background;
        Color foreground = ResolveItemForeground(e.Item, options.Colors);
        if (selected && !options.UseUnderlineCursor)
        {
            background = options.Colors.SelectedBackground;
            foreground = FileListColorResolver.ResolveSelectedForegroundForPreset(
                options.ColorTheme,
                foreground,
                background);
        }

        using var backgroundBrush = new SolidBrush(background);
        e.Graphics.FillRectangle(backgroundBrush, e.Bounds);
        Font font = e.Item.ListView?.Font ?? SystemFonts.DefaultFont;
        Rectangle textBounds = e.Bounds;
        if (e.ColumnIndex == 0 && isMarked)
        {
            const int markSlotWidth = 15;
            Rectangle markRect = new(e.Bounds.X, e.Bounds.Y, markSlotWidth, e.Bounds.Height);
            textBounds = new Rectangle(
                e.Bounds.X + markSlotWidth,
                e.Bounds.Y,
                Math.Max(0, e.Bounds.Width - markSlotWidth),
                e.Bounds.Height);
            TextRenderer.DrawText(
                e.Graphics,
                "*",
                font,
                markRect,
                ResolveMarkGlyphColor(background),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

        TextRenderer.DrawText(
            e.Graphics,
            e.SubItem?.Text ?? string.Empty,
            font,
            textBounds,
            foreground,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        if (options.UseUnderlineCursor && e.Item.Selected && e.ColumnIndex == 0)
        {
            DrawCursorUnderline(e.Graphics, e.Item.Bounds, foreground);
        }
    }

    public void DrawPanel(
        Graphics graphics,
        ListView.ListViewItemCollection items,
        int panelWidth,
        int panelHeight,
        Font font,
        int pageStartIndex,
        int cursorIndex,
        int desiredColumnCount,
        Options options,
        Action<IReadOnlyList<string>>? markGlyphsPainted = null)
    {
        graphics.Clear(options.Colors.Background);
        List<string>? paintedMarkedPaths = markGlyphsPainted == null ? null : new List<string>();

        int itemHeight = HeaderLayoutHelper.GetMeasuredLineHeight(font, 4);
        BrowserLayoutMetrics layout = BrowserLayoutProjection.Calculate(
            panelWidth,
            panelHeight,
            itemHeight,
            desiredColumnCount,
            options.DisplayMode,
            BrowserLayoutProjection.GetLeadingPresentationSlotCount(options.ShowFilterIndicator));
        if (options.ShowFilterIndicator)
        {
            DrawFilterIndicator(graphics, font, layout, options.FilterIndicatorColor);
        }
        if (items.Count == 0)
        {
            markGlyphsPainted?.Invoke(Array.Empty<string>());
            return;
        }
        int iconSize = Math.Clamp((int)Math.Round(font.Height * 0.9), 12, 48);
        int markSlotWidthForLayout = GetMarkSlotWidth(font, options.ShowItemIcons, iconSize);
        int iconSlotWidthForLayout = options.ShowItemIcons ? iconSize + 2 : 0;
        int commonTextWidth = Math.Max(0, layout.ColumnWidth - 10 - markSlotWidthForLayout - iconSlotWidthForLayout);
        int alignedExtensionStartOffset = options.DisplayMode == BrowserFileDisplayMode.NameExtensionAligned && options.ShowExtensions
            ? GetNameExtensionStartOffset(graphics, items, commonTextWidth, font)
            : -1;
        int pageLocalCursorIndex = BrowserPageIndex.ToLocal(cursorIndex, pageStartIndex, items.Count);
        for (int index = 0; index < items.Count; index++)
        {
            int visualIndex = BrowserLayoutProjection.GetVisualIndexForItem(index, layout);
            ListViewItem item = items[index];
            bool selected = index == pageLocalCursorIndex;
            Rectangle bounds = BrowserLayoutProjection.GetVisualSlotBounds(visualIndex, layout);
            Color background = options.Colors.Background;
            Color foreground = ResolveItemForeground(item, options.Colors);
            if (selected && !options.UseUnderlineCursor)
            {
                background = options.Colors.SelectedBackground;
                foreground = FileListColorResolver.ResolveSelectedForegroundForPreset(
                    options.ColorTheme,
                    foreground,
                    background);
            }

            using (var backgroundBrush = new SolidBrush(background))
            {
                graphics.FillRectangle(backgroundBrush, bounds);
            }

            int markSlotWidth = GetMarkSlotWidth(font, options.ShowItemIcons, iconSize);
            Rectangle markRect = new(bounds.X, bounds.Y, markSlotWidth, bounds.Height);
            int iconSlotWidth = options.ShowItemIcons ? iconSize + 2 : 0;
            Rectangle iconRect = new(
                bounds.X + markSlotWidth,
                bounds.Y + Math.Max(0, (bounds.Height - iconSize) / 2),
                iconSize,
                iconSize);
            Rectangle textRect = new(
                bounds.X + markSlotWidth + iconSlotWidth,
                bounds.Y,
                options.DisplayMode == BrowserFileDisplayMode.NameExtensionAligned
                    ? Math.Max(0, bounds.Width - markSlotWidth - iconSlotWidth)
                    : bounds.Width - markSlotWidth - iconSlotWidth,
                bounds.Height);

            if (IsMarked(item.Tag as string, options.MarkedPaths))
            {
                TextRenderer.DrawText(
                    graphics,
                    "*",
                    font,
                    markRect,
                    ResolveMarkGlyphColor(background),
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                if (item.Tag is string markedPath)
                {
                    paintedMarkedPaths?.Add(markedPath);
                }
            }
            if (options.ShowItemIcons && textRect.Width > 24)
            {
                DrawItemIcon(graphics, item, iconRect);
            }

            bool detailDrawn = options.DisplayMode == BrowserFileDisplayMode.NameExtensionAligned
                ? DrawNameExtensionAligned(graphics, item, textRect, font, foreground, options, alignedExtensionStartOffset)
                : options.DisplayMode != BrowserFileDisplayMode.NameOnly &&
                    DrawItemTextWithDetails(graphics, item, textRect, font, foreground, options);
            if (!detailDrawn && options.DisplayMode == BrowserFileDisplayMode.NameSizeDate)
            {
                detailDrawn = DrawItemTextWithDetails(
                    graphics,
                    item,
                    textRect,
                    font,
                    foreground,
                    options with { DisplayMode = BrowserFileDisplayMode.NameSize });
            }
            if (!detailDrawn)
            {
                string text = BuildDisplayText(item, textRect.Width, font, graphics, options);
                TextRenderer.DrawText(
                    graphics,
                    text,
                    font,
                    textRect,
                    foreground,
                    Color.Transparent,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            if (options.UseUnderlineCursor && selected)
            {
                DrawCursorUnderline(graphics, bounds, foreground);
            }
        }

        markGlyphsPainted?.Invoke(paintedMarkedPaths!);
    }

    public bool TryGetItemLayoutBounds(
        Graphics graphics,
        int panelWidth,
        int panelHeight,
        Font font,
        ListView.ListViewItemCollection items,
        int index,
        int pageStartIndex,
        int desiredColumnCount,
        Options options,
        out Rectangle hoverBounds,
        out Rectangle nameBounds)
    {
        return TryGetItemLayoutBounds(
            graphics,
            panelWidth,
            panelHeight,
            font,
            items,
            index,
            pageStartIndex,
            desiredColumnCount,
            options,
            out hoverBounds,
            out nameBounds,
            out _);
    }

    public bool TryGetItemLayoutBounds(
        Graphics graphics,
        int panelWidth,
        int panelHeight,
        Font font,
        ListView.ListViewItemCollection items,
        int index,
        int pageStartIndex,
        int desiredColumnCount,
        Options options,
        out Rectangle hoverBounds,
        out Rectangle nameBounds,
        out bool? isAlignedNameEllipsized)
    {
        hoverBounds = Rectangle.Empty;
        nameBounds = Rectangle.Empty;
        isAlignedNameEllipsized = null;
        int pageLocalIndex = index - pageStartIndex;
        if (pageLocalIndex < 0 || pageLocalIndex >= items.Count)
        {
            return false;
        }

        int itemHeight = HeaderLayoutHelper.GetMeasuredLineHeight(font, 4);
        BrowserLayoutMetrics layout = BrowserLayoutProjection.Calculate(
            panelWidth,
            panelHeight,
            itemHeight,
            desiredColumnCount,
            options.DisplayMode,
            BrowserLayoutProjection.GetLeadingPresentationSlotCount(options.ShowFilterIndicator));
        if (layout.RowsPerColumn <= 0 || layout.EffectiveColumnCount <= 0)
        {
            return false;
        }

        int visualIndex = BrowserLayoutProjection.GetVisualIndexForItem(pageLocalIndex, layout);
        Rectangle bounds = BrowserLayoutProjection.GetVisualSlotBounds(visualIndex, layout);
        hoverBounds = bounds;

        int iconSize = Math.Clamp((int)Math.Round(font.Height * 0.9), 12, 48);
        int markSlotWidth = GetMarkSlotWidth(font, options.ShowItemIcons, iconSize);
        int iconSlotWidth = options.ShowItemIcons ? iconSize + 2 : 0;
        Rectangle textBounds = new(
            bounds.X + markSlotWidth + iconSlotWidth,
            bounds.Y,
            bounds.Width - markSlotWidth - iconSlotWidth,
            bounds.Height);
        if (textBounds.Width <= 0)
        {
            return false;
        }

        ListViewItem item = items[pageLocalIndex];
        if (options.DisplayMode == BrowserFileDisplayMode.NameExtensionAligned)
        {
            nameBounds = textBounds;
            int iconSizeForLayout = Math.Clamp((int)Math.Round(font.Height * 0.9), 12, 48);
            int markSlotWidthForLayout = GetMarkSlotWidth(font, options.ShowItemIcons, iconSizeForLayout);
            int iconSlotWidthForLayout = options.ShowItemIcons ? iconSizeForLayout + 2 : 0;
            int commonTextWidth = Math.Max(0, layout.ColumnWidth - 10 - markSlotWidthForLayout - iconSlotWidthForLayout);
            int alignedExtensionStartOffset = options.ShowExtensions
                ? GetNameExtensionStartOffset(graphics, items, commonTextWidth, font)
                : -1;

            if (IsDirectoryListItem(item, item.Tag as string) || !options.ShowExtensions)
            {
                string visibleText = BuildDisplayText(item, textBounds.Width, font, graphics, options);
                string completeText = GetUntruncatedNameDisplayText(item, options);
                isAlignedNameEllipsized = !string.Equals(visibleText, completeText, StringComparison.Ordinal);
            }
            else if (alignedExtensionStartOffset >= 0)
            {
                NameExtensionAlignedFields fields = BuildNameExtensionAlignedFields(
                    graphics,
                    item,
                    textBounds,
                    font,
                    alignedExtensionStartOffset);
                isAlignedNameEllipsized = fields.IsEllipsized;
            }
            else
            {
                string completeText = GetUntruncatedNameDisplayText(item, options);
                isAlignedNameEllipsized = MeasureTextWidth(graphics, completeText, font) > textBounds.Width;
            }
            return true;
        }

        if (options.DisplayMode == BrowserFileDisplayMode.NameOnly)
        {
            nameBounds = textBounds;
            return true;
        }

        if (!TryCalculateNameBounds(graphics, item, textBounds, font, options.DisplayMode, options, out Rectangle detailNameBounds)
            && !(options.DisplayMode == BrowserFileDisplayMode.NameSizeDate
                && TryCalculateNameBounds(graphics, item, textBounds, font, BrowserFileDisplayMode.NameSize, options, out detailNameBounds)))
        {
            nameBounds = textBounds;
            return true;
        }

        nameBounds = detailNameBounds;
        return true;
    }

    internal static int GetNameExtensionStartOffset(
        Graphics graphics,
        ListView.ListViewItemCollection items,
        int availableWidth,
        Font font)
    {
        if (availableWidth <= 0)
        {
            return -1;
        }

        int maximumBaseWidth = 0;
        int maximumExtensionWidth = 0;
        bool hasExtension = false;
        foreach (ListViewItem item in items)
        {
            if (IsDirectoryListItem(item, item.Tag as string))
            {
                continue;
            }

            maximumBaseWidth = Math.Max(maximumBaseWidth, MeasureTextWidth(graphics, item.Text, font));
            string extension = GetItemExtensionText(item);
            if (!string.IsNullOrEmpty(extension))
            {
                hasExtension = true;
                maximumExtensionWidth = Math.Max(maximumExtensionWidth, MeasureTextWidth(graphics, extension, font));
            }
        }

        if (!hasExtension)
        {
            return -1;
        }

        int gapWidth = Math.Max(2, MeasureTextWidth(graphics, " ", font));
        int minimumFieldWidth = MeasureTextWidth(graphics, "…", font);
        if (availableWidth < minimumFieldWidth * 2 + gapWidth)
        {
            return -1;
        }

        int extensionBudget = Math.Min(
            maximumExtensionWidth,
            Math.Max(minimumFieldWidth, availableWidth - minimumFieldWidth - gapWidth));
        int maximumOffset = availableWidth - extensionBudget;
        int preferredOffset = maximumBaseWidth + gapWidth;
        return Math.Clamp(preferredOffset, minimumFieldWidth + gapWidth, maximumOffset);
    }

    internal static NameExtensionAlignedFields BuildNameExtensionAlignedFields(
        Graphics graphics,
        ListViewItem item,
        Rectangle textBounds,
        Font font,
        int extensionStartOffset)
    {
        int gapWidth = Math.Max(2, MeasureTextWidth(graphics, " ", font));
        int nameWidth = Math.Clamp(extensionStartOffset - gapWidth, 0, textBounds.Width);
        int extensionX = textBounds.X + Math.Clamp(extensionStartOffset, 0, textBounds.Width);
        Rectangle nameBounds = new(textBounds.X, textBounds.Y, nameWidth, textBounds.Height);
        Rectangle extensionBounds = new(
            extensionX,
            textBounds.Y,
            Math.Max(0, textBounds.Right - extensionX),
            textBounds.Height);

        string extension = GetItemExtensionText(item);
        string nameText = FitTextWithTrailingEllipsis(item.Text, nameBounds.Width, font, graphics);
        string extensionText = FitTextWithTrailingEllipsis(extension, extensionBounds.Width, font, graphics);
        bool isEllipsized = !string.Equals(nameText, item.Text, StringComparison.Ordinal)
            || !string.Equals(extensionText, extension, StringComparison.Ordinal);
        return new NameExtensionAlignedFields(nameBounds, extensionBounds, nameText, extensionText, isEllipsized);
    }

    public bool IsItemNameEllipsized(
        Graphics graphics,
        ListViewItem item,
        Rectangle nameBounds,
        Options options,
        string fullName)
    {
        if (nameBounds.Width <= 0)
        {
            return false;
        }

        if (IsDirectoryListItem(item, item.Tag as string))
        {
            if (item.Text == "..")
            {
                return false;
            }

            if (options.DisplayMode == BrowserFileDisplayMode.NameOnly && options.ShowDirectoryMarker)
            {
                const string marker = " <DIR>";
                return !string.Equals(
                    item.Text + marker,
                    FitDirectoryTextPreservingMarker(item.Text, marker, nameBounds.Width, item.ListView?.Font ?? SystemFonts.DefaultFont, graphics),
                    StringComparison.Ordinal);
            }

            return !string.Equals(
                item.Text,
                FitTextWithTrailingEllipsis(item.Text, nameBounds.Width, item.ListView?.Font ?? SystemFonts.DefaultFont, graphics),
                StringComparison.Ordinal);
        }

        return MeasureTextWidth(graphics, fullName, item.ListView?.Font ?? SystemFonts.DefaultFont) > nameBounds.Width;
    }

    private static bool TryCalculateNameBounds(
        Graphics graphics,
        ListViewItem item,
        Rectangle textBounds,
        Font font,
        BrowserFileDisplayMode mode,
        Options options,
        out Rectangle nameBounds)
    {
        nameBounds = Rectangle.Empty;
        const string nameEllipsis = "...";
        bool includeDate = mode == BrowserFileDisplayMode.NameSizeDate;
        string dateText = item.SubItems.Count > 3 ? NormalizeDateText(item.SubItems[3].Text) : string.Empty;
        if (DateTime.TryParse(item.SubItems.Count > 3 ? item.SubItems[3].Text : string.Empty, out DateTime parsedDate))
        {
            dateText = FileSystemItemFactory.FormatDisplayDate(parsedDate, options.DateFormat);
        }

        if ((includeDate && string.IsNullOrWhiteSpace(dateText)) || string.IsNullOrWhiteSpace(GetSizeText(item)))
        {
            return false;
        }

        int gapWidth = Math.Max(2, MeasureTextWidth(graphics, " ", font));
        int dateFieldWidth = includeDate
            ? Math.Max(MeasureTextWidth(graphics, GetDateFieldSample(options.DateFormat), font), MeasureTextWidth(graphics, dateText, font))
            : 0;
        int sizeFieldWidth = Math.Max(
            MeasureTextWidth(graphics, "999.9PB", font),
            Math.Max(MeasureTextWidth(graphics, "<DIR>", font), MeasureTextWidth(graphics, GetSizeText(item), font)));
        int minimumNameWidth = MeasureTextWidth(graphics, nameEllipsis, font);
        int requiredWidth = includeDate
            ? minimumNameWidth + sizeFieldWidth + dateFieldWidth + gapWidth * 2
            : minimumNameWidth + sizeFieldWidth + gapWidth;
        if (textBounds.Width < requiredWidth)
        {
            return false;
        }

        int reservedDetailWidth = includeDate
            ? sizeFieldWidth + dateFieldWidth + gapWidth * 2
            : sizeFieldWidth + gapWidth;
        int nameFieldWidth = Math.Max(minimumNameWidth, textBounds.Width - reservedDetailWidth);
        if (nameFieldWidth < minimumNameWidth)
        {
            return false;
        }

        nameBounds = new Rectangle(textBounds.X, textBounds.Y, nameFieldWidth, textBounds.Height);
        return true;
    }

    private static string GetSizeText(ListViewItem item)
    {
        return IsDirectoryListItem(item, item.Tag as string) ? "<DIR>" : BuildFileSizeTextCompact(item);
    }

    public static void ApplyItemColors(
        ListViewItem item,
        FileListColorResolver.ResolvedColors colors)
    {
        item.ForeColor = ResolveItemForeground(item, colors);
        item.BackColor = colors.Background;
    }

    public static string BuildFileSizeTextCompact(ListViewItem item)
    {
        return item.SubItems.Count > 2 ? item.SubItems[2].Text : string.Empty;
    }

    public static bool IsDirectoryListItem(ListViewItem item, string? fullPath = null)
    {
        if (item.Text == "..")
        {
            return true;
        }
        return !string.IsNullOrEmpty(fullPath) && Directory.Exists(fullPath);
    }

    private static bool IsMarked(string? path, IReadOnlyCollection<string> markedPaths)
    {
        return !string.IsNullOrWhiteSpace(path) &&
            markedPaths.Any(markedPath => string.Equals(markedPath, path, StringComparison.OrdinalIgnoreCase));
    }

    private static Color ResolveItemForeground(
        ListViewItem item,
        FileListColorResolver.ResolvedColors colors)
    {
        if (item.Text == "..")
        {
            return colors.Directory;
        }

        bool isDirectory = IsDirectoryListItemForDisplay(item);
        if (TryGetAttributes(item, out FileAttributes attributes))
        {
            return ResolveAttributeColor(attributes, isDirectory, colors);
        }
        return isDirectory ? colors.Directory : colors.NormalFile;
    }

    private static Color ResolveAttributeColor(
        FileAttributes attributes,
        bool isDirectory,
        FileListColorResolver.ResolvedColors colors)
    {
        if (attributes.HasFlag(FileAttributes.System)) return colors.System;
        if (attributes.HasFlag(FileAttributes.Hidden)) return colors.Hidden;
        if (attributes.HasFlag(FileAttributes.ReadOnly)) return colors.ReadOnly;
        return isDirectory ? colors.Directory : colors.NormalFile;
    }

    private static bool IsDirectoryListItemForDisplay(ListViewItem item)
    {
        return item.Text == ".." ||
            (item.SubItems.Count > 2 && string.IsNullOrEmpty(item.SubItems[2].Text));
    }

    private static bool TryGetAttributes(ListViewItem item, out FileAttributes attributes)
    {
        attributes = FileAttributes.Normal;
        if (item.SubItems.Count <= 4)
        {
            return false;
        }
        string code = item.SubItems[4].Text ?? string.Empty;
        if (code.Contains('R')) attributes |= FileAttributes.ReadOnly;
        if (code.Contains('H')) attributes |= FileAttributes.Hidden;
        if (code.Contains('S')) attributes |= FileAttributes.System;
        if (code.Contains('A')) attributes |= FileAttributes.Archive;
        return true;
    }

    private static Color ResolveMarkGlyphColor(Color background)
    {
        return FileListColorResolver.GetRelativeLuminance(background) > 0.5
            ? Color.Black
            : Color.White;
    }

    private static void DrawCursorUnderline(Graphics graphics, Rectangle bounds, Color color)
    {
        int y = bounds.Bottom - 2;
        using var pen = new Pen(color, 1);
        graphics.DrawLine(pen, bounds.Left + 2, y, bounds.Right - 2, y);
    }

    private static void DrawItemIcon(Graphics graphics, ListViewItem item, Rectangle iconRect)
    {
        try
        {
            string? fullPath = item.Tag as string;
            bool isDirectory = IsDirectoryListItem(item, fullPath);
            using var icon = (Icon)BrowserItemIconProvider.GetIcon(fullPath, isDirectory, iconRect.Width).Clone();
            graphics.DrawIcon(icon, iconRect);
        }
        catch
        {
            // アイコン取得失敗時は一覧描画を優先して無視する。
        }
    }

    private static int GetMarkSlotWidth(Font font, bool showItemIcons, int iconSize)
    {
        int baseSlotWidth = showItemIcons ? Math.Clamp(iconSize / 2 + 8, 18, 32) : 15;
        int glyphWidth = TextRenderer.MeasureText(
            "*",
            font,
            Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
        return Math.Max(baseSlotWidth, glyphWidth + 8);
    }

    private static void DrawFilterIndicator(
        Graphics graphics,
        Font font,
        BrowserLayoutMetrics layout,
        Color indicatorColor)
    {
        Rectangle bounds = BrowserLayoutProjection.GetVisualSlotBounds(0, layout);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        using (var indicatorBrush = new SolidBrush(indicatorColor))
        {
            graphics.FillRectangle(indicatorBrush, bounds);
        }

        TextRenderer.DrawText(
            graphics,
            FilterIndicatorText,
            font,
            bounds,
            ColorContrastHelper.PickReadableTextColor(indicatorColor, Color.Black, Color.White),
            TextFormatFlags.Left |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPrefix |
            TextFormatFlags.EndEllipsis |
            TextFormatFlags.SingleLine);
    }

    private static string BuildDisplayText(
        ListViewItem item,
        int availableWidth,
        Font font,
        Graphics graphics,
        Options options)
    {
        bool isDirectory = IsDirectoryListItem(item, item.Tag as string);
        if (isDirectory)
        {
            if (item.Text == ".." || options.ShowDirectoryMarker)
            {
                return FitDirectoryTextPreservingMarker(item.Text, " <DIR>", availableWidth, font, graphics);
            }
            return FitTextWithTrailingEllipsis(item.Text, availableWidth, font, graphics);
        }

        string extension = options.ShowExtensions && item.SubItems.Count > 1 && !string.IsNullOrEmpty(item.SubItems[1].Text)
            ? "." + item.SubItems[1].Text
            : string.Empty;
        return string.IsNullOrEmpty(extension)
            ? FitTextWithTrailingEllipsis(item.Text, availableWidth, font, graphics)
            : FitFileNamePreservingExtension(item.Text, extension, availableWidth, font, graphics);
    }

    private static bool DrawNameExtensionAligned(
        Graphics graphics,
        ListViewItem item,
        Rectangle textBounds,
        Font font,
        Color foreground,
        Options options,
        int extensionStartOffset)
    {
        if (textBounds.Width <= 0 || extensionStartOffset < 0 ||
            !options.ShowExtensions || IsDirectoryListItem(item, item.Tag as string))
        {
            return false;
        }

        NameExtensionAlignedFields fields = BuildNameExtensionAlignedFields(
            graphics,
            item,
            textBounds,
            font,
            extensionStartOffset);
        if (fields.NameBounds.Width > 0)
        {
            TextRenderer.DrawText(
                graphics,
                fields.NameText,
                font,
                fields.NameBounds,
                foreground,
                Color.Transparent,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }
        if (fields.ExtensionBounds.Width > 0 && !string.IsNullOrEmpty(fields.ExtensionText))
        {
            TextRenderer.DrawText(
                graphics,
                fields.ExtensionText,
                font,
                fields.ExtensionBounds,
                foreground,
                Color.Transparent,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }
        return true;
    }

    private static bool DrawItemTextWithDetails(
        Graphics graphics,
        ListViewItem item,
        Rectangle textRect,
        Font font,
        Color foreground,
        Options options)
    {
        const string nameEllipsis = "...";
        bool isDirectory = IsDirectoryListItem(item, item.Tag as string);
        if (isDirectory && item.Text == "..")
        {
            return false;
        }

        string dateText = item.SubItems.Count > 3 ? NormalizeDateText(item.SubItems[3].Text) : string.Empty;
        if (DateTime.TryParse(item.SubItems.Count > 3 ? item.SubItems[3].Text : string.Empty, out DateTime parsedDate))
        {
            dateText = FileSystemItemFactory.FormatDisplayDate(parsedDate, options.DateFormat);
        }
        string sizeText = isDirectory ? "<DIR>" : BuildFileSizeTextCompact(item);
        bool includeDate = options.DisplayMode == BrowserFileDisplayMode.NameSizeDate;
        if ((includeDate && string.IsNullOrWhiteSpace(dateText)) || string.IsNullOrWhiteSpace(sizeText))
        {
            return false;
        }

        int gapWidth = Math.Max(2, MeasureTextWidth(graphics, " ", font));
        int dateFieldWidth = includeDate
            ? Math.Max(MeasureTextWidth(graphics, GetDateFieldSample(options.DateFormat), font), MeasureTextWidth(graphics, dateText, font))
            : 0;
        int sizeFieldWidth = Math.Max(
            MeasureTextWidth(graphics, "999.9PB", font),
            Math.Max(MeasureTextWidth(graphics, "<DIR>", font), MeasureTextWidth(graphics, sizeText, font)));
        int minimumNameWidth = MeasureTextWidth(graphics, nameEllipsis, font);
        int requiredWidth = includeDate
            ? minimumNameWidth + sizeFieldWidth + dateFieldWidth + gapWidth * 2
            : minimumNameWidth + sizeFieldWidth + gapWidth;
        if (textRect.Width < requiredWidth)
        {
            return false;
        }

        int reservedDetailWidth = includeDate
            ? sizeFieldWidth + dateFieldWidth + gapWidth * 2
            : sizeFieldWidth + gapWidth;
        int nameFieldWidth = Math.Max(minimumNameWidth, textRect.Width - reservedDetailWidth);
        if (nameFieldWidth < minimumNameWidth)
        {
            return false;
        }

        Rectangle nameRect = new(textRect.X, textRect.Y, nameFieldWidth, textRect.Height);
        Rectangle sizeRect = new(nameRect.Right + gapWidth, textRect.Y, sizeFieldWidth, textRect.Height);
        Rectangle dateRect = includeDate
            ? new Rectangle(sizeRect.Right + gapWidth, textRect.Y, dateFieldWidth, textRect.Height)
            : Rectangle.Empty;
        string nameText = isDirectory
            ? FitTextWithTrailingEllipsis(item.Text, nameRect.Width, font, graphics)
            : BuildFileNameText(item, options.ShowExtensions, nameRect.Width, font, graphics);
        TextRenderer.DrawText(
            graphics,
            nameText,
            font,
            nameRect,
            foreground,
            Color.Transparent,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(
            graphics,
            sizeText,
            font,
            sizeRect,
            foreground,
            Color.Transparent,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        if (includeDate)
        {
            TextRenderer.DrawText(
                graphics,
                dateText,
                font,
                dateRect,
                foreground,
                Color.Transparent,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }
        return true;
    }

    private static string BuildFileNameText(
        ListViewItem item,
        bool showExtensions,
        int width,
        Font font,
        Graphics graphics)
    {
        string extension = showExtensions && item.SubItems.Count > 1 && !string.IsNullOrEmpty(item.SubItems[1].Text)
            ? "." + item.SubItems[1].Text
            : string.Empty;
        return string.IsNullOrEmpty(extension)
            ? FitTextWithTrailingEllipsis(item.Text, width, font, graphics)
            : FitFileNamePreservingExtension(item.Text, extension, width, font, graphics);
    }

    private static string GetItemExtensionText(ListViewItem item)
    {
        return item.SubItems.Count > 1 && !string.IsNullOrEmpty(item.SubItems[1].Text)
            ? "." + item.SubItems[1].Text
            : string.Empty;
    }

    private static string GetUntruncatedNameDisplayText(ListViewItem item, Options options)
    {
        if (IsDirectoryListItem(item, item.Tag as string))
        {
            return item.Text == ".." || options.ShowDirectoryMarker
                ? item.Text + " <DIR>"
                : item.Text;
        }

        return item.Text + (options.ShowExtensions ? GetItemExtensionText(item) : string.Empty);
    }

    private static string NormalizeDateText(string raw)
    {
        return string.IsNullOrWhiteSpace(raw)
            ? string.Empty
            : DateTime.TryParse(raw, out DateTime parsed)
                ? parsed.ToString("yyyy-MM-dd HH:mm")
                : raw;
    }

    private static string GetDateFieldSample(string? dateFormat)
    {
        return FileSystemItemFactory.FormatDisplayDate(new DateTime(2099, 12, 31, 23, 59, 59), dateFormat);
    }

    private static string FitFileNamePreservingExtension(string baseName, string extension, int availableWidth, Font font, Graphics graphics)
    {
        string fullText = baseName + extension;
        if (MeasureTextWidth(graphics, fullText, font) <= availableWidth) return fullText;
        if (MeasureTextWidth(graphics, extension, font) > availableWidth) return FitTextWithTrailingEllipsis(fullText, availableWidth, font, graphics);
        const string ellipsis = "…";
        string minimumCandidate = ellipsis + extension;
        if (MeasureTextWidth(graphics, minimumCandidate, font) > availableWidth) return FitTextWithTrailingEllipsis(fullText, availableWidth, font, graphics);
        int low = 0;
        int high = baseName.Length;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            string candidate = baseName[..mid] + ellipsis + extension;
            if (MeasureTextWidth(graphics, candidate, font) <= availableWidth) low = mid;
            else high = mid - 1;
        }
        return low <= 0 ? minimumCandidate : baseName[..low] + ellipsis + extension;
    }

    private static string FitDirectoryTextPreservingMarker(string baseName, string marker, int availableWidth, Font font, Graphics graphics)
    {
        string fullText = baseName + marker;
        if (MeasureTextWidth(graphics, fullText, font) <= availableWidth) return fullText;
        if (MeasureTextWidth(graphics, marker, font) > availableWidth) return FitTextWithTrailingEllipsis(fullText, availableWidth, font, graphics);
        const string ellipsis = "…";
        string minimumCandidate = ellipsis + marker;
        if (MeasureTextWidth(graphics, minimumCandidate, font) > availableWidth) return FitTextWithTrailingEllipsis(fullText, availableWidth, font, graphics);
        int low = 0;
        int high = baseName.Length;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            string candidate = baseName[..mid] + ellipsis + marker;
            if (MeasureTextWidth(graphics, candidate, font) <= availableWidth) low = mid;
            else high = mid - 1;
        }
        return low <= 0 ? minimumCandidate : baseName[..low] + ellipsis + marker;
    }

    private static string FitTextWithTrailingEllipsis(string text, int availableWidth, Font font, Graphics graphics)
    {
        if (MeasureTextWidth(graphics, text, font) <= availableWidth) return text;
        const string ellipsis = "…";
        if (MeasureTextWidth(graphics, ellipsis, font) > availableWidth) return string.Empty;
        int low = 0;
        int high = text.Length;
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            string candidate = text[..mid] + ellipsis;
            if (MeasureTextWidth(graphics, candidate, font) <= availableWidth) low = mid;
            else high = mid - 1;
        }
        return low <= 0 ? ellipsis : text[..low] + ellipsis;
    }

    private static int MeasureTextWidth(Graphics graphics, string text, Font font)
    {
        return TextRenderer.MeasureText(
            graphics,
            text,
            font,
            new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width;
    }
}
