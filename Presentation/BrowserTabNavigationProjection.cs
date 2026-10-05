using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MidFD.Configuration;
using MidFD.Helpers;
using MidFD.Models;

namespace MidFD.Presentation;

internal static class BrowserTabNavigationProjection
{
    public static string BuildStructureKey(
        IReadOnlyList<BrowserTabCategoryDefinition> categories,
        string activeCategoryId,
        IReadOnlyList<BrowserTabState> activeTabs,
        BrowserTabRestoreSnapshot snapshot,
        IReadOnlyList<BrowserTabNavigationSearchDerivedTab>? searchDerivedTabs = null,
        IReadOnlyList<BrowserTabGroup>? userTabGroups = null)
    {
        var key = new StringBuilder();
        foreach (BrowserTabCategoryDefinition category in categories)
        {
            key.Append(category.Id).Append('|').Append(category.DisplayName).Append('|');
            if (string.Equals(category.Id, activeCategoryId, StringComparison.OrdinalIgnoreCase))
            {
                foreach (BrowserTabState tab in activeTabs)
                {
                    key.Append(tab.Id).Append(';');
                }
            }
            else
            {
                BrowserTabRestoreCategoryState? stored = snapshot.Categories.FirstOrDefault(item =>
                    string.Equals(item.Id, category.Id, StringComparison.OrdinalIgnoreCase));
                foreach (BrowserTabSessionState tab in stored?.OpenTabs ?? Enumerable.Empty<BrowserTabSessionState>())
                {
                    key.Append(tab.TabId).Append('|').Append(tab.CurrentPath).Append(';');
                }
            }
            key.Append('#');
        }
        foreach (BrowserTabNavigationSearchDerivedTab derivedTab in (searchDerivedTabs ?? Array.Empty<BrowserTabNavigationSearchDerivedTab>())
                     .OrderBy(item => item.SourceCategoryId, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.SourceTabId)
                     .ThenBy(item => item.CreationOrder))
        {
            key.Append("|SearchDerived:")
                .Append(derivedTab.ResultTabId.ToString("N"))
                .Append(':').Append(derivedTab.SourceCategoryId)
                .Append(':').Append(derivedTab.SourceTabId.ToString("N"))
                .Append(':').Append(derivedTab.CreationOrder);
        }
        foreach (BrowserTabGroup group in (userTabGroups ?? Array.Empty<BrowserTabGroup>())
                     .OrderBy(item => item.CategoryId, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item.CreationOrder))
        {
            key.Append("|UserGroup:")
                .Append(group.Id.ToString("N"))
                .Append(':').Append(group.CategoryId)
                .Append(':').Append(group.DisplayName)
                .Append(':').Append(group.Expanded ? '1' : '0');
            foreach (Guid memberId in group.MemberTabIds) key.Append(':').Append(memberId.ToString("N"));
        }
        return key.ToString();
    }

    public static List<BrowserTabNavigationCategoryItem> BuildCategories(
        IReadOnlyList<BrowserTabCategoryDefinition> categories,
        string activeCategoryId,
        IReadOnlyList<BrowserTabState> activeTabs,
        IReadOnlyDictionary<string, IReadOnlyList<BrowserTabState>> storedTabs,
        bool showCategoryRow,
        Func<BrowserTabState, BrowserTabPresentationSnapshot> presentationResolver,
        BrowserTabNavigationSearchChild? searchChild = null,
        IReadOnlyList<BrowserTabNavigationSearchDerivedTab>? searchDerivedTabs = null,
        IReadOnlyList<BrowserTabGroup>? userTabGroups = null)
    {
        var result = new List<BrowserTabNavigationCategoryItem>();
        foreach (BrowserTabCategoryDefinition category in categories)
        {
            IReadOnlyList<BrowserTabState> tabs = string.Equals(
                    category.Id,
                    activeCategoryId,
                    StringComparison.OrdinalIgnoreCase)
                ? activeTabs
                : storedTabs.TryGetValue(category.Id, out IReadOnlyList<BrowserTabState>? cachedTabs)
                    ? cachedTabs
                    : Array.Empty<BrowserTabState>();
            IReadOnlyList<BrowserTabStripItem> projectedTabs = tabs.Select(state =>
            {
                BrowserTabPresentationSnapshot presentation = presentationResolver(state);
                return new BrowserTabStripItem(
                    presentation.HeaderText,
                    presentation.ToolTipText,
                    presentation.CanonicalPath,
                    presentation.PrefixText,
                    presentation.BaseTitle,
                    presentation.RelativeSuffix,
                    BrowserTabId: state.Id);
            }).ToList();
            HashSet<Guid> tabIds = projectedTabs
                .Where(item => item.BrowserTabId.HasValue)
                .Select(item => item.BrowserTabId!.Value)
                .ToHashSet();
            BrowserTabNavigationSearchChild? categorySearchChild = searchChild != null
                && string.Equals(category.Id, searchChild.SourceCategoryId, StringComparison.OrdinalIgnoreCase)
                && tabIds.Contains(searchChild.SourceTabId)
                    ? searchChild
                    : null;
            BrowserTabNavigationSearchDerivedTab[] categoryDerivedTabs = (searchDerivedTabs ?? Array.Empty<BrowserTabNavigationSearchDerivedTab>())
                .Where(item => string.Equals(category.Id, item.SourceCategoryId, StringComparison.OrdinalIgnoreCase)
                    && tabIds.Contains(item.SourceTabId)
                    && tabIds.Contains(item.ResultTabId))
                .OrderBy(item => item.CreationOrder)
                .ToArray();
            HashSet<Guid> nestedTabIds = categoryDerivedTabs.Select(item => item.ResultTabId).ToHashSet();
            BrowserTabGroup[] categoryGroups = (userTabGroups ?? Array.Empty<BrowserTabGroup>())
                .Where(item => string.Equals(category.Id, item.CategoryId, StringComparison.OrdinalIgnoreCase))
                .Select(item => item with
                {
                    MemberTabIds = item.MemberTabIds
                        .Where(memberId => tabIds.Contains(memberId) && !nestedTabIds.Contains(memberId))
                        .ToArray()
                })
                .Where(item => item.MemberTabIds.Count > 0)
                .OrderBy(item => item.CreationOrder)
                .ToArray();
            result.Add(new BrowserTabNavigationCategoryItem(
                category.Id,
                string.IsNullOrWhiteSpace(category.DisplayName) ? "既定" : category.DisplayName,
                BrowserTabPresentationHelper.BuildCategoryToolTip(category),
                projectedTabs,
                SearchChild: categorySearchChild,
                SearchDerivedTabs: categoryDerivedTabs,
                UserTabGroups: categoryGroups));
        }

        if (showCategoryRow)
        {
            result.Add(new BrowserTabNavigationCategoryItem(
                BrowserTabStrip.ManageCategoriesEntryId,
                "＋カテゴリ",
                "新しいカテゴリを追加します。",
                Array.Empty<BrowserTabStripItem>(),
                BrowserTabStripCategoryItemKind.ManageEntry));
        }
        return result;
    }

    public static List<BrowserTabStripItem> BuildHorizontalTabs(
        IReadOnlyList<BrowserTabStripItem> tabs,
        string categoryId,
        BrowserTabNavigationSearchChild? searchChild,
        IReadOnlyList<BrowserTabNavigationSearchDerivedTab>? searchDerivedTabs,
        IReadOnlyList<BrowserTabGroup>? userTabGroups = null)
    {
        var tabsById = tabs
            .Where(item => item.BrowserTabId.HasValue)
            .ToDictionary(item => item.BrowserTabId!.Value);
        var derivedBySource = (searchDerivedTabs ?? Array.Empty<BrowserTabNavigationSearchDerivedTab>())
            .Where(item => string.Equals(item.SourceCategoryId, categoryId, StringComparison.OrdinalIgnoreCase)
                && tabsById.ContainsKey(item.SourceTabId)
                && tabsById.ContainsKey(item.ResultTabId))
            .GroupBy(item => item.SourceTabId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(item => item.CreationOrder).ToArray());
        var derivedTabIds = derivedBySource.Values.SelectMany(items => items).Select(item => item.ResultTabId).ToHashSet();
        BrowserTabGroup[] groups = (userTabGroups ?? Array.Empty<BrowserTabGroup>())
            .Where(group => string.Equals(group.CategoryId, categoryId, StringComparison.OrdinalIgnoreCase))
            .Select(group => group with
            {
                MemberTabIds = group.MemberTabIds
                    .Where(id => tabsById.ContainsKey(id) && !derivedTabIds.Contains(id))
                    .ToArray()
            })
            .Where(group => group.MemberTabIds.Count > 0)
            .OrderBy(group => group.CreationOrder)
            .ToArray();
        var groupByMember = groups
            .SelectMany(group => group.MemberTabIds.Select(memberId => (memberId, group)))
            .ToDictionary(pair => pair.memberId, pair => pair.group);
        var result = new List<BrowserTabStripItem>(tabs.Count + (searchChild == null ? 0 : 1));
        var appendedTabIds = new HashSet<Guid>();
        var appendedGroupIds = new HashSet<Guid>();

        void AppendTabAndChildren(BrowserTabStripItem tab, BrowserTabGroup? group = null)
        {
            if (tab.BrowserTabId is { } tabId && !appendedTabIds.Add(tabId)) return;
            if (group != null)
            {
                string groupPrefix = $"[{group.DisplayName}] ";
                string tooltip = string.IsNullOrWhiteSpace(tab.ToolTipText)
                    ? $"Tab Group: {group.DisplayName}"
                    : $"{tab.ToolTipText}\nTab Group: {group.DisplayName}";
                tab = tab with { Prefix = $"{groupPrefix}{tab.Prefix}", ToolTipText = tooltip };
            }
            result.Add(tab);
            if (tab.BrowserTabId is not { } sourceTabId) return;
            if (searchChild is { } child
                && string.Equals(child.SourceCategoryId, categoryId, StringComparison.OrdinalIgnoreCase)
                && child.SourceTabId == sourceTabId)
            {
                result.Add(new BrowserTabStripItem(
                    child.Text,
                    child.ToolTipText,
                    Kind: BrowserTabStripItemKind.UnifiedSearch,
                    RuntimeIdentity: child.SessionId.ToString("N"),
                    SourceTabId: child.SourceTabId));
            }
            if (!derivedBySource.TryGetValue(sourceTabId, out BrowserTabNavigationSearchDerivedTab[]? children)) return;
            foreach (BrowserTabNavigationSearchDerivedTab derived in children)
            {
                if (tabsById.TryGetValue(derived.ResultTabId, out BrowserTabStripItem? childTab)) AppendTabAndChildren(childTab);
            }
        }

        foreach (BrowserTabStripItem tab in tabs)
        {
            Guid? tabId = tab.BrowserTabId;
            if (tabId is { } derivedId && derivedTabIds.Contains(derivedId)) continue;
            if (tabId is { } memberId && groupByMember.TryGetValue(memberId, out BrowserTabGroup? group))
            {
                if (!appendedGroupIds.Add(group.Id)) continue;
                foreach (BrowserTabStripItem member in tabs.Where(item =>
                             item.BrowserTabId is { } id && group.MemberTabIds.Contains(id) && !derivedTabIds.Contains(id)))
                {
                    AppendTabAndChildren(member, group);
                }
                continue;
            }
            AppendTabAndChildren(tab);
        }
        return result;
    }

    public static IReadOnlyList<BrowserTabState> BuildStoredTabStates(
        BrowserTabRestoreCategoryState? category,
        Func<string, string> titleResolver)
    {
        var tabs = new List<BrowserTabState>();
        foreach (BrowserTabSessionState sessionTab in category?.OpenTabs ?? Enumerable.Empty<BrowserTabSessionState>())
        {
            string path = sessionTab.CurrentPath ?? string.Empty;
            tabs.Add(new BrowserTabState
            {
                Id = sessionTab.TabId == Guid.Empty ? Guid.NewGuid() : sessionTab.TabId,
                Title = titleResolver(path),
                CurrentPath = path,
                IsLocked = sessionTab.IsLocked,
                StartupPath = sessionTab.StartupPath ?? string.Empty,
                IsReadOnly = sessionTab.IsReadOnly,
                FocusTargetName = sessionTab.FocusTargetName,
                CursorIndex = Math.Max(0, sessionTab.CursorIndex),
                ColumnCount = Math.Clamp(sessionTab.ColumnCount, 1, 9),
                SortKind = sessionTab.SortKind,
                SortAscending = sessionTab.SortAscending
            });
        }
        return tabs;
    }
}
