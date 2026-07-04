using System;
using System.Collections.Generic;
using System.Linq;
using MidFD;
using MidFD.Models;
using MidFD.Presentation;
using MidFD.Configuration;
using MidFD.Helpers;

namespace MidFD.Coordinators;

public class BrowserTabUiCoordinator
{
    private BrowserTabStrip? _browserTabStrip;

    public void Bind(BrowserTabStrip strip)
    {
        _browserTabStrip = strip;
    }

    public void RefreshHeaders(
        IReadOnlyList<BrowserTabState> browserTabs,
        int activeBrowserTabIndex,
        IReadOnlyList<BrowserTabCategoryDefinition> browserTabCategories,
        int activeCategoryIndex,
        bool showCategoryRow,
        ref string? lastSnapshotKey)
    {
        if (_browserTabStrip == null)
            return;

        var stripCategories = browserTabCategories
            .Select(category => new BrowserTabStripCategoryItem(
                category.Id,
                string.IsNullOrWhiteSpace(category.DisplayName) ? "既定" : category.DisplayName,
                BrowserTabPresentationHelper.BuildCategoryToolTip(category)))
            .ToList();

        if (showCategoryRow)
        {
            stripCategories.Add(new BrowserTabStripCategoryItem(
                BrowserTabStrip.ManageCategoriesEntryId,
                "+",
                "新しいカテゴリを追加します。",
                BrowserTabStripCategoryItemKind.ManageEntry));
        }

        var stripTabs = browserTabs
            .Select((state, i) => new BrowserTabStripItem(
                BrowserTabPresentationHelper.BuildHeaderText(state, i),
                BrowserTabPresentationHelper.BuildToolTip(state)))
            .ToList();

        string snapshotKey = BrowserTabPresentationHelper.BuildHeaderSnapshotKey(
            showCategoryRow,
            activeCategoryIndex,
            activeBrowserTabIndex,
            stripCategories,
            stripTabs);

        if (string.Equals(lastSnapshotKey, snapshotKey, StringComparison.Ordinal))
        {
            return;
        }

        lastSnapshotKey = snapshotKey;

        _browserTabStrip.SetCategories(stripCategories, activeCategoryIndex);
        _browserTabStrip.SetTabs(stripTabs);

        if (activeBrowserTabIndex >= 0 && activeBrowserTabIndex < browserTabs.Count)
        {
            _browserTabStrip.SelectedIndex = activeBrowserTabIndex;
        }
    }
}
