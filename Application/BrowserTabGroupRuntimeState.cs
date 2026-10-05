using MidFD.Models;

namespace MidFD.Runtime;

/// <summary>Single runtime authority for user-created Browser tab groups.</summary>
public sealed class BrowserTabGroupRuntimeState
{
    private readonly List<GroupState> _groups = [];
    private long _nextCreationOrder;

    public IReadOnlyList<BrowserTabGroup> Groups
        => _groups.Select(ToSnapshot).ToArray();

    public IReadOnlyList<BrowserTabGroup> GetGroups(string categoryId)
        => _groups
            .Where(group => string.Equals(group.CategoryId, categoryId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(group => group.CreationOrder)
            .Select(ToSnapshot)
            .ToArray();

    public void Restore(IEnumerable<BrowserTabGroup> groups)
    {
        _groups.Clear();
        _nextCreationOrder = 0;
        var groupIds = new HashSet<Guid>();
        var memberIds = new HashSet<Guid>();
        foreach (BrowserTabGroup group in groups
                     .OrderBy(static item => item.CreationOrder)
                     .ThenBy(static item => item.Id))
        {
            if (group.Id == Guid.Empty
                || !groupIds.Add(group.Id)
                || string.IsNullOrWhiteSpace(group.CategoryId)
                || string.IsNullOrWhiteSpace(group.DisplayName))
            {
                continue;
            }

            var restored = new GroupState(
                group.Id,
                group.CategoryId,
                group.DisplayName.Trim(),
                group.Expanded,
                _nextCreationOrder);
            foreach (Guid memberId in group.MemberTabIds)
            {
                if (memberId != Guid.Empty && memberIds.Add(memberId)) restored.MemberTabIds.Add(memberId);
            }

            if (restored.MemberTabIds.Count == 0) continue;
            _groups.Add(restored);
            _nextCreationOrder++;
        }
    }

    public BrowserTabGroup? Find(Guid groupId)
        => _groups.FirstOrDefault(group => group.Id == groupId) is { } group ? ToSnapshot(group) : null;

    public BrowserTabGroup? FindByMember(string categoryId, Guid tabId)
        => _groups.FirstOrDefault(group =>
            string.Equals(group.CategoryId, categoryId, StringComparison.OrdinalIgnoreCase)
            && group.MemberTabIds.Contains(tabId)) is { } group
                ? ToSnapshot(group)
                : null;

    public BrowserTabGroup? Create(string categoryId, string displayName, Guid tabId, IReadOnlySet<Guid>? blockedTabIds = null)
    {
        string normalizedName = displayName.Trim();
        if (string.IsNullOrWhiteSpace(categoryId)
            || string.IsNullOrWhiteSpace(normalizedName)
            || tabId == Guid.Empty
            || blockedTabIds?.Contains(tabId) == true)
        {
            return null;
        }

        RemoveMembership(tabId);
        var group = new GroupState(Guid.NewGuid(), categoryId, normalizedName, expanded: true, _nextCreationOrder++);
        group.MemberTabIds.Add(tabId);
        _groups.Add(group);
        return ToSnapshot(group);
    }

    public bool AddMember(Guid groupId, string categoryId, Guid tabId, IReadOnlySet<Guid>? blockedTabIds = null)
    {
        GroupState? target = _groups.FirstOrDefault(group => group.Id == groupId);
        if (target == null
            || !string.Equals(target.CategoryId, categoryId, StringComparison.OrdinalIgnoreCase)
            || tabId == Guid.Empty
            || blockedTabIds?.Contains(tabId) == true)
        {
            return false;
        }

        GroupState? current = _groups.FirstOrDefault(group => group.MemberTabIds.Contains(tabId));
        if (current?.Id == groupId) return true;

        RemoveMembership(tabId);
        target.MemberTabIds.Add(tabId);
        return true;
    }

    public bool RemoveMember(string categoryId, Guid tabId)
    {
        GroupState? group = _groups.FirstOrDefault(candidate =>
            string.Equals(candidate.CategoryId, categoryId, StringComparison.OrdinalIgnoreCase)
            && candidate.MemberTabIds.Contains(tabId));
        if (group == null) return false;

        group.MemberTabIds.Remove(tabId);
        RemoveIfEmpty(group);
        return true;
    }

    public bool Rename(Guid groupId, string displayName)
    {
        GroupState? group = _groups.FirstOrDefault(candidate => candidate.Id == groupId);
        string normalizedName = displayName.Trim();
        if (group == null || string.IsNullOrWhiteSpace(normalizedName)) return false;

        group.DisplayName = normalizedName;
        return true;
    }

    public bool SetExpanded(Guid groupId, bool expanded)
    {
        GroupState? group = _groups.FirstOrDefault(candidate => candidate.Id == groupId);
        if (group == null) return false;

        group.Expanded = expanded;
        return true;
    }

    public bool PruneToLiveTabs(IReadOnlyDictionary<Guid, string> liveTabCategories)
    {
        bool changed = false;
        foreach (GroupState group in _groups.ToArray())
        {
            if (!liveTabCategories.Values.Contains(group.CategoryId, StringComparer.OrdinalIgnoreCase))
            {
                _groups.Remove(group);
                changed = true;
                continue;
            }

            changed |= group.MemberTabIds.RemoveAll(tabId =>
                !liveTabCategories.TryGetValue(tabId, out string? categoryId)
                || !string.Equals(categoryId, group.CategoryId, StringComparison.OrdinalIgnoreCase)) > 0;
            int beforeCount = _groups.Count;
            RemoveIfEmpty(group);
            changed |= _groups.Count != beforeCount;
        }
        return changed;
    }

    private void RemoveMembership(Guid tabId)
    {
        foreach (GroupState group in _groups.Where(candidate => candidate.MemberTabIds.Contains(tabId)).ToArray())
        {
            group.MemberTabIds.Remove(tabId);
            RemoveIfEmpty(group);
        }
    }

    private void RemoveIfEmpty(GroupState group)
    {
        if (group.MemberTabIds.Count == 0) _groups.Remove(group);
    }

    private static BrowserTabGroup ToSnapshot(GroupState group)
        => new(
            group.Id,
            group.CategoryId,
            group.DisplayName,
            group.MemberTabIds.ToArray(),
            group.Expanded,
            group.CreationOrder);

    private sealed class GroupState(Guid id, string categoryId, string displayName, bool expanded, long creationOrder)
    {
        public Guid Id { get; } = id;
        public string CategoryId { get; } = categoryId;
        public string DisplayName { get; set; } = displayName;
        public List<Guid> MemberTabIds { get; } = [];
        public bool Expanded { get; set; } = expanded;
        public long CreationOrder { get; } = creationOrder;
    }
}
