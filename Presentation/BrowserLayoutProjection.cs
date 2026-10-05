using System;
using System.Drawing;
using MidFD.Configuration;
using MidFD.Helpers;
using MidFD.Models;

namespace MidFD.Presentation;

internal readonly record struct BrowserLayoutMetrics(
    int ItemHeight,
    int RowsPerColumn,
    int EffectiveColumnCount,
    int ItemsPerPage,
    int ColumnWidth,
    int LeadingPresentationSlotCount);

internal readonly record struct BrowserLayoutProjectionInput(
    int PanelWidth,
    int PanelHeight,
    int ItemHeight,
    BrowserFileDisplayMode DisplayMode,
    int LeadingPresentationSlotCount = 0)
{
    public int GetItemsPerPage(int desiredColumnCount) =>
        BrowserLayoutProjection.Calculate(
            PanelWidth,
            PanelHeight,
            ItemHeight,
            desiredColumnCount,
            DisplayMode,
            LeadingPresentationSlotCount).ItemsPerPage;
}

internal static class BrowserLayoutProjection
{
    internal const int ItemTopInset = 5;

    public static BrowserLayoutMetrics Calculate(
        int panelWidth,
        int panelHeight,
        int itemHeight,
        int desiredColumnCount,
        BrowserFileDisplayMode displayMode,
        int leadingPresentationSlotCount = 0)
    {
        int safeItemHeight = Math.Max(1, itemHeight);
        int rowsPerColumn = Math.Max(1, (panelHeight - 10) / safeItemHeight);
        int minimumColumnWidth = GetMinimumColumnWidth(displayMode);
        int maxColumnsByWidth = Math.Max(1, panelWidth / Math.Max(1, minimumColumnWidth));
        int effectiveColumnCount = Math.Max(
            1,
            Math.Min(Math.Max(1, desiredColumnCount), maxColumnsByWidth));
        int columnWidth = Math.Max(1, panelWidth / effectiveColumnCount);
        int visualSlotCount = effectiveColumnCount * rowsPerColumn;
        int safeLeadingPresentationSlotCount = Math.Clamp(
            leadingPresentationSlotCount,
            0,
            Math.Max(0, visualSlotCount - 1));
        return new BrowserLayoutMetrics(
            safeItemHeight,
            rowsPerColumn,
            effectiveColumnCount,
            Math.Max(1, visualSlotCount - safeLeadingPresentationSlotCount),
            columnWidth,
            safeLeadingPresentationSlotCount);
    }

    public static int GetLeadingPresentationSlotCount(bool filterIndicatorVisible) =>
        filterIndicatorVisible ? 1 : 0;

    public static int GetVisualIndexForItem(int pageLocalItemIndex, BrowserLayoutMetrics layout) =>
        pageLocalItemIndex + layout.LeadingPresentationSlotCount;

    public static int GetItemPageIndexFromVisualIndex(int visualIndex, BrowserLayoutMetrics layout)
    {
        int pageLocalItemIndex = visualIndex - layout.LeadingPresentationSlotCount;
        return pageLocalItemIndex >= 0 ? pageLocalItemIndex : -1;
    }

    public static Rectangle GetVisualSlotBounds(int visualIndex, BrowserLayoutMetrics layout)
    {
        if (visualIndex < 0 || layout.RowsPerColumn <= 0 || layout.EffectiveColumnCount <= 0)
        {
            return Rectangle.Empty;
        }

        int column = visualIndex / layout.RowsPerColumn;
        int row = visualIndex % layout.RowsPerColumn;
        if (column >= layout.EffectiveColumnCount)
        {
            return Rectangle.Empty;
        }

        return new Rectangle(
            column * layout.ColumnWidth + 5,
            row * layout.ItemHeight + ItemTopInset,
            layout.ColumnWidth - 10,
            layout.ItemHeight);
    }

    public static int GetMinimumColumnWidth(BrowserFileDisplayMode displayMode) => displayMode switch
    {
        BrowserFileDisplayMode.NameSize => 220,
        BrowserFileDisplayMode.NameSizeDate => 340,
        BrowserFileDisplayMode.NameExtensionAligned => 140,
        _ => 140
    };

    public static int GetGlobalIndexFromPoint(
        int x,
        int y,
        int pageStartIndex,
        int materializedItemCount,
        BrowserLayoutMetrics layout)
    {
        if (materializedItemCount <= 0)
        {
            return -1;
        }

        if (y < ItemTopInset)
        {
            return -1;
        }

        int targetColumn = x / layout.ColumnWidth;
        int targetRow = (y - ItemTopInset) / layout.ItemHeight;
        if (targetColumn < 0 || targetColumn >= layout.EffectiveColumnCount ||
            targetRow < 0 || targetRow >= layout.RowsPerColumn)
        {
            return -1;
        }

        int visualIndex = targetColumn * layout.RowsPerColumn + targetRow;
        int pageLocalItemIndex = GetItemPageIndexFromVisualIndex(visualIndex, layout);
        return BrowserPageIndex.ToGlobal(pageLocalItemIndex, pageStartIndex, materializedItemCount);
    }
}
