using MidFD.Commands;
using MidFD.Dialogs;
using MidFD.Helpers;
using MidFD.Models;

namespace MidFD;

public partial class MainForm
{
    private void BrowserTabNavigation_GroupContextMenuRequested(object? sender, BrowserTabGroupNavigationEventArgs e)
    {
        BrowserTabGroup? group = _browserTabGroupRuntimeState.Find(e.GroupId);
        if (group == null) return;

        _browserTabGroupContextId = group.Id;
        EnsureBrowserTabGroupContextMenu();
        if (_renameBrowserTabGroupContextMenuItem != null)
        {
            _renameBrowserTabGroupContextMenuItem.Text = $"「{group.DisplayName}」の名前を変更...";
        }
        if (sender is Control owner) _browserTabGroupContextMenu?.Show(owner, e.Location);
    }

    private void ApplyBrowserTabGroupExpandedChanged(object? sender, BrowserTabGroupExpandedEventArgs e)
    {
        if (_browserTabGroupRuntimeState.SetExpanded(e.GroupId, e.Expanded))
        {
            _browserWorkspacePersistenceApplicationCoordinator.CaptureBrowserTabGroupsToRestoreSnapshot();
        }
    }

    private void BrowserTabNavigation_TreeDropRequested(object? sender, BrowserTabTreeDropEventArgs e)
    {
        BrowserTabTreeDropIntent intent = e.Intent;
        if (string.IsNullOrWhiteSpace(intent.SourceCategoryId)
            || !string.Equals(intent.SourceCategoryId, intent.DestinationCategoryId, StringComparison.OrdinalIgnoreCase)
            || intent.SourceTabId == Guid.Empty)
        {
            return;
        }

        if (intent.RequiresMembershipChange)
        {
            string commandId = intent.DestinationGroupId.HasValue
                ? CommandIds.BrowserTabGroupAdd
                : CommandIds.BrowserTabGroupRemove;
            if (!ExecuteBrowserTabGroupCommandFromUi(
                    commandId,
                    "BrowserTabNavigation.TreeDrop",
                    intent.SourceCategoryId,
                    intent.SourceTabId,
                    intent.DestinationGroupId))
            {
                return;
            }
        }

        if (_browserTabGroupRuntimeState.FindByMember(intent.SourceCategoryId, intent.SourceTabId)?.Id != intent.DestinationGroupId)
        {
            return;
        }

        if (!intent.RequiresReorder || intent.AnchorTabId is not { } anchorTabId) return;

        bool activeCategory = string.Equals(
            _browserApplicationCoordinator.Workspace.ResolveCategoryId(intent.SourceCategoryId),
            _browserApplicationCoordinator.Workspace.ActiveCategoryId,
            StringComparison.OrdinalIgnoreCase);
        BrowserTabState? currentState = activeCategory ? BuildBrowserTabStateFromCurrentUi() : null;
        bool insertAfter = intent.Placement is BrowserTabTreeDropPlacement.After
            or BrowserTabTreeDropPlacement.AppendToGroup
            or BrowserTabTreeDropPlacement.AppendToCategory;
        if (_browserTabWorkflowApplicationCoordinator.ReorderTabById(
                intent.SourceCategoryId,
                intent.SourceTabId,
                anchorTabId,
                insertAfter,
                currentState))
        {
            RefreshBrowserTabHeaders();
        }
    }

    private bool ExecuteBrowserTabGroupContextCommand(string commandId, string source, Guid? groupId = null)
    {
        if (_browserTabContextTabId is not { } tabId || string.IsNullOrWhiteSpace(_browserTabContextCategoryId)) return false;
        return ExecuteBrowserTabGroupCommandFromUi(
            commandId,
            source,
            _browserTabContextCategoryId,
            tabId,
            groupId);
    }

    private void EnsureBrowserTabGroupContextMenu()
    {
        if (_browserTabGroupContextMenu != null) return;
        _browserTabGroupContextMenu = new ContextMenuStrip();
        _renameBrowserTabGroupContextMenuItem = new ToolStripMenuItem("グループ名を変更...");
        _renameBrowserTabGroupContextMenuItem.Click += (_, _) =>
        {
            if (_browserTabGroupContextId is not { } groupId) return;
            ExecuteCommandFromUiWithRuntimeTarget(
                CommandIds.BrowserTabGroupRename,
                CommandScope.Browser,
                "BrowserTabGroupContextMenu.Rename",
                groupId);
        };
        _browserTabGroupContextMenu.Items.Add(_renameBrowserTabGroupContextMenuItem);
    }

    private void UpdateBrowserTabGroupContextMenuItems(string categoryId, BrowserTabState tab)
    {
        if (_createBrowserTabGroupContextMenuItem == null
            || _addBrowserTabGroupContextMenuItem == null
            || _removeBrowserTabGroupContextMenuItem == null) return;

        IReadOnlyList<BrowserTabGroup> groups = _browserTabGroupRuntimeState.GetGroups(categoryId);
        BrowserTabGroup? currentGroup = _browserTabGroupRuntimeState.FindByMember(categoryId, tab.Id);
        bool searchLineageBlocked = IsBrowserTabGroupBlockedByActiveSearch(categoryId, tab.Id);
        _createBrowserTabGroupContextMenuItem.Visible = currentGroup == null;
        _createBrowserTabGroupContextMenuItem.Enabled = !searchLineageBlocked;
        _createBrowserTabGroupContextMenuItem.ToolTipText = searchLineageBlocked
            ? "検索結果を表示中はグループへ移動できません。検索を閉じると利用できます。"
            : string.Empty;
        _removeBrowserTabGroupContextMenuItem.Visible = currentGroup != null;
        _addBrowserTabGroupContextMenuItem.DropDownItems.Clear();
        foreach (BrowserTabGroup group in groups.Where(item => item.Id != currentGroup?.Id))
        {
            var item = new ToolStripMenuItem(group.DisplayName) { Tag = group.Id };
            item.Click += (_, _) =>
            {
                ExecuteBrowserTabGroupCommandFromUi(
                    CommandIds.BrowserTabGroupAdd,
                    "BrowserTabContextMenu.GroupAdd",
                    categoryId,
                    tab.Id,
                    group.Id);
            };
            _addBrowserTabGroupContextMenuItem.DropDownItems.Add(item);
        }
        _addBrowserTabGroupContextMenuItem.Text = "グループへ移動";
        _addBrowserTabGroupContextMenuItem.Enabled = !searchLineageBlocked
            && _addBrowserTabGroupContextMenuItem.DropDownItems.Count > 0;
        _addBrowserTabGroupContextMenuItem.ToolTipText = searchLineageBlocked
            ? "検索結果を表示中はグループへ移動できません。検索を閉じると利用できます。"
            : string.Empty;
    }

    private bool ApplyCreateBrowserTabGroupCommand(int tabIndex, string? targetCategoryId = null, Guid? targetTabId = null)
    {
        if (!TryResolveBrowserGroupCommandTab(tabIndex, targetCategoryId, targetTabId, out BrowserTabState tab, out string categoryId)) return true;
        if (IsBrowserTabGroupBlockedByActiveSearch(categoryId, tab.Id))
        {
            ShowStatusMessage("検索結果を表示中はタブをグループへ移動できません。検索を閉じてから操作してください。");
            return true;
        }

        string? name = SimpleInputDialog.ShowNullable("グループ名を入力してください。", "タブグループを作成");
        if (name == null) return true;
        BrowserTabGroup? created = _browserTabGroupRuntimeState.Create(
            categoryId,
            name,
            tab.Id,
            GetBlockedSearchResultTabIds(categoryId));
        if (created == null)
        {
            ShowStatusMessage("空でないグループ名を入力してください。");
            return true;
        }

        _searchDerivedBrowserTabLineages.RemoveAll(item => item.ResultTabId == tab.Id);
        _browserWorkspacePersistenceApplicationCoordinator.CaptureBrowserTabGroupsToRestoreSnapshot();
        RefreshBrowserTabHeaders();
        ShowStatusMessage($"タブグループを作成しました: {created.DisplayName}");
        return true;
    }

    private bool ApplyAddBrowserTabGroupMemberCommand(
        int tabIndex,
        string? groupIdText,
        string? targetCategoryId = null,
        Guid? targetTabId = null)
    {
        if (!TryResolveBrowserGroupCommandTab(tabIndex, targetCategoryId, targetTabId, out BrowserTabState tab, out string categoryId)) return true;
        if (IsBrowserTabGroupBlockedByActiveSearch(categoryId, tab.Id))
        {
            ShowStatusMessage("検索結果を表示中はタブをグループへ移動できません。検索を閉じてから操作してください。");
            return true;
        }

        Guid? groupId = Guid.TryParse(groupIdText, out Guid parsed) ? parsed : null;
        if (groupId == null)
        {
            IReadOnlyList<BrowserTabGroup> groups = _browserTabGroupRuntimeState.GetGroups(categoryId);
            groupId = BrowserTabGroupPickerDialog.Show(this, "タブをグループへ移動", groups);
        }
        if (groupId == null) return true;

        bool added = _browserTabGroupRuntimeState.AddMember(
            groupId.Value,
            categoryId,
            tab.Id,
            GetBlockedSearchResultTabIds(categoryId));
        if (!added)
        {
            ShowStatusMessage("同じカテゴリのタブグループを選択してください。");
            return true;
        }

        _searchDerivedBrowserTabLineages.RemoveAll(item => item.ResultTabId == tab.Id);
        _browserWorkspacePersistenceApplicationCoordinator.CaptureBrowserTabGroupsToRestoreSnapshot();
        RefreshBrowserTabHeaders();
        ShowStatusMessage("タブをグループへ移動しました。");
        return true;
    }

    private bool ApplyRemoveBrowserTabGroupMemberCommand(int tabIndex, string? targetCategoryId = null, Guid? targetTabId = null)
    {
        if (!TryResolveBrowserGroupCommandTab(tabIndex, targetCategoryId, targetTabId, out BrowserTabState tab, out string categoryId)) return true;
        if (_browserTabGroupRuntimeState.RemoveMember(categoryId, tab.Id))
        {
            _browserWorkspacePersistenceApplicationCoordinator.CaptureBrowserTabGroupsToRestoreSnapshot();
            RefreshBrowserTabHeaders();
            ShowStatusMessage("タブをグループから外しました。");
        }
        return true;
    }

    private bool ApplyRenameBrowserTabGroupCommand(int tabIndex, string? groupIdText)
    {
        BrowserTabGroup? group = Guid.TryParse(groupIdText, out Guid groupId)
            ? _browserTabGroupRuntimeState.Find(groupId)
            : tabIndex >= 0 && tabIndex < _browserApplicationCoordinator.Workspace.TabStates.Count
                ? _browserTabGroupRuntimeState.FindByMember(
                    _browserApplicationCoordinator.Workspace.ActiveCategoryId,
                    _browserApplicationCoordinator.Workspace.TabStates[tabIndex].Id)
                : null;
        if (group == null)
        {
            IReadOnlyList<BrowserTabGroup> groups = _browserTabGroupRuntimeState.GetGroups(
                _browserApplicationCoordinator.Workspace.ActiveCategoryId);
            Guid? selectedGroupId = BrowserTabGroupPickerDialog.Show(this, "名前を変更するグループを選択", groups);
            if (selectedGroupId == null) return true;
            group = _browserTabGroupRuntimeState.Find(selectedGroupId.Value);
        }
        if (group == null) return true;

        string? name = SimpleInputDialog.ShowNullable("新しいグループ名を入力してください。", "タブグループ名変更", group.DisplayName);
        if (name == null) return true;
        if (!_browserTabGroupRuntimeState.Rename(group.Id, name))
        {
            ShowStatusMessage("空でないグループ名を入力してください。");
            return true;
        }

        _browserWorkspacePersistenceApplicationCoordinator.CaptureBrowserTabGroupsToRestoreSnapshot();
        RefreshBrowserTabHeaders();
        ShowStatusMessage($"タブグループ名を変更しました: {name.Trim()}");
        return true;
    }

    private bool TryResolveBrowserGroupCommandTab(
        int tabIndex,
        string? targetCategoryId,
        Guid? targetTabId,
        out BrowserTabState tab,
        out string categoryId)
    {
        if (targetTabId.HasValue && !string.IsNullOrWhiteSpace(targetCategoryId))
        {
            categoryId = _browserApplicationCoordinator.Workspace.ResolveCategoryId(targetCategoryId);
            IReadOnlyList<BrowserTabState> targetTabs = GetBrowserTabContextStates(categoryId);
            BrowserTabState? target = targetTabs.FirstOrDefault(item => item.Id == targetTabId.Value);
            if (target != null)
            {
                tab = target;
                return true;
            }

            tab = null!;
            ShowStatusMessage("対象のBrowserタブが見つかりません。");
            return false;
        }

        categoryId = _browserApplicationCoordinator.Workspace.ActiveCategoryId;
        IReadOnlyList<BrowserTabState> tabs = _browserApplicationCoordinator.Workspace.TabStates;
        if (tabIndex < 0 || tabIndex >= tabs.Count)
        {
            tab = null!;
            ShowStatusMessage("対象のBrowserタブが見つかりません。");
            return false;
        }
        tab = tabs[tabIndex];
        return true;
    }

    private bool IsBrowserTabGroupBlockedByActiveSearch(string categoryId, Guid tabId)
        => _unifiedSearchSession is { IsActive: true } session
            && string.Equals(session.SourceCategoryId, categoryId, StringComparison.OrdinalIgnoreCase)
            && (session.ReusableInspectionTabId == tabId || session.AdditionalResultTabIds.Contains(tabId));

    private IReadOnlySet<Guid> GetBlockedSearchResultTabIds(string categoryId)
        => _unifiedSearchSession is { IsActive: true } session
            && string.Equals(session.SourceCategoryId, categoryId, StringComparison.OrdinalIgnoreCase)
                ? session.AdditionalResultTabIds
                    .Append(session.ReusableInspectionTabId ?? Guid.Empty)
                    .Where(id => id != Guid.Empty)
                    .ToHashSet()
                : new HashSet<Guid>();
}
