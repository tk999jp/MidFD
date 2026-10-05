using MidFD.Configuration;
using MidFD.Models;

namespace MidFD.Runtime;

internal static class BrowserTabGroupPersistenceMapper
{
    public static List<BrowserTabGroupRestoreState> Capture(IEnumerable<BrowserTabGroup>? groups)
    {
        return (groups ?? Array.Empty<BrowserTabGroup>())
            .Where(static group => group.Id != Guid.Empty
                && !string.IsNullOrWhiteSpace(group.CategoryId)
                && !string.IsNullOrWhiteSpace(group.DisplayName))
            .Select(static group => new BrowserTabGroupRestoreState
            {
                GroupId = group.Id.ToString("D"),
                CategoryId = group.CategoryId,
                DisplayName = group.DisplayName,
                Expanded = group.Expanded,
                MemberTabIds = group.MemberTabIds
                    .Where(static id => id != Guid.Empty)
                    .Distinct()
                    .Select(static id => id.ToString("D"))
                    .ToList()
            })
            .Where(static group => group.MemberTabIds.Count > 0)
            .OrderBy(static group => group.GroupId, StringComparer.Ordinal)
            .ToList();
    }

    public static IReadOnlyList<BrowserTabGroup> NormalizeForRestore(BrowserTabRestoreSnapshot? snapshot)
    {
        if (snapshot == null) return Array.Empty<BrowserTabGroup>();

        var categoryOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var locations = new Dictionary<Guid, TabLocation>();
        List<BrowserTabRestoreCategoryState> categories = snapshot.Categories ?? new List<BrowserTabRestoreCategoryState>();
        for (int categoryIndex = 0; categoryIndex < categories.Count; categoryIndex++)
        {
            BrowserTabRestoreCategoryState? category = categories[categoryIndex];
            if (category == null || string.IsNullOrWhiteSpace(category.Id)) continue;
            if (!categoryOrder.TryAdd(category.Id, categoryIndex)) continue;

            List<BrowserTabSessionState> openTabs = category.OpenTabs ?? new List<BrowserTabSessionState>();
            for (int tabIndex = 0; tabIndex < openTabs.Count; tabIndex++)
            {
                BrowserTabSessionState? tab = openTabs[tabIndex];
                if (tab == null || tab.TabId == Guid.Empty) continue;
                locations.TryAdd(tab.TabId, new TabLocation(category.Id, categoryIndex, tabIndex));
            }
        }

        var candidates = new List<GroupCandidate>();
        IReadOnlyList<BrowserTabGroupRestoreState> persistedGroups = snapshot.UserTabGroups ?? new();
        for (int index = 0; index < persistedGroups.Count; index++)
        {
            BrowserTabGroupRestoreState? persisted = persistedGroups[index];
            if (persisted == null
                || !Guid.TryParse(persisted.GroupId, out Guid groupId)
                || groupId == Guid.Empty
                || string.IsNullOrWhiteSpace(persisted.CategoryId)
                || !categoryOrder.ContainsKey(persisted.CategoryId)
                || string.IsNullOrWhiteSpace(persisted.DisplayName))
            {
                continue;
            }

            candidates.Add(new GroupCandidate(groupId, persisted, index));
        }

        var acceptedGroupIds = new HashSet<Guid>();
        var claimedTabIds = new HashSet<Guid>();
        var restored = new List<RestoredGroup>();
        foreach (GroupCandidate candidate in candidates
                     .OrderBy(static item => item.SourceIndex))
        {
            if (acceptedGroupIds.Contains(candidate.GroupId)) continue;

            var members = new List<(Guid TabId, TabLocation Location)>();
            foreach (string memberText in candidate.Persisted.MemberTabIds ?? new List<string>())
            {
                if (!Guid.TryParse(memberText, out Guid tabId)
                    || tabId == Guid.Empty
                    || !locations.TryGetValue(tabId, out TabLocation location)
                    || !string.Equals(location.CategoryId, candidate.Persisted.CategoryId, StringComparison.OrdinalIgnoreCase)
                    || claimedTabIds.Contains(tabId))
                {
                    continue;
                }

                claimedTabIds.Add(tabId);
                members.Add((tabId, location));
            }

            if (members.Count == 0) continue;

            acceptedGroupIds.Add(candidate.GroupId);
            members.Sort(static (left, right) => left.Location.TabIndex.CompareTo(right.Location.TabIndex));
            TabLocation firstMember = members[0].Location;
            restored.Add(new RestoredGroup(
                new BrowserTabGroup(
                    candidate.GroupId,
                    firstMember.CategoryId,
                    candidate.Persisted.DisplayName.Trim(),
                    members.Select(static member => member.TabId).ToArray(),
                    candidate.Persisted.Expanded,
                    0),
                firstMember.CategoryIndex,
                firstMember.TabIndex));
        }

        return restored
            .OrderBy(static group => group.CategoryIndex)
            .ThenBy(static group => group.FirstMemberTabIndex)
            .ThenBy(static group => group.Group.Id)
            .Select((group, creationOrder) => group.Group with { CreationOrder = creationOrder })
            .ToArray();
    }

    private sealed record GroupCandidate(Guid GroupId, BrowserTabGroupRestoreState Persisted, int SourceIndex);
    private sealed record RestoredGroup(BrowserTabGroup Group, int CategoryIndex, int FirstMemberTabIndex);
    private readonly record struct TabLocation(string CategoryId, int CategoryIndex, int TabIndex);
}
