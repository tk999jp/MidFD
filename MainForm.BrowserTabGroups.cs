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
        _ = ExecuteCommandFromUi(CommandIds.BrowserTabGroupExpandedSet, new CommandExecutionContext
        {
            Scope = CommandScope.Browser,
            Source = "BrowserTabNavigation.GroupExpandedChanged",
            BrowserTabGroupExpanded = new BrowserTabGroupExpandedCommandContext(e.GroupId, e.Expanded)
        });
    }

    private void BrowserTabNavigation_TreeDropRequested(object? sender, BrowserTabTreeDropEventArgs e)
    {
        BrowserTabTreeDropIntent intent = e.Intent;
        _ = ExecuteCommandFromUi(CommandIds.BrowserTabTreeDrop, new CommandExecutionContext
        {
            Scope = CommandScope.Browser,
            Source = "BrowserTabNavigation.TreeDrop",
            BrowserTabTreeDrop = new BrowserTabTreeDropCommandContext(
                intent.SourceCategoryId,
                intent.SourceTabId,
                intent.DestinationCategoryId,
                intent.DestinationGroupId,
                intent.AnchorTabId,
                intent.Placement,
                intent.RequiresMembershipChange,
                intent.RequiresReorder)
        });
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

    private bool IsBrowserTabGroupBlockedByActiveSearch(string categoryId, Guid tabId)
        => _commandApplicationCoordinator.IsUnifiedSearchResultTabBlocked(categoryId, tabId);

    private IReadOnlySet<Guid> GetBlockedSearchResultTabIds(string categoryId)
        => _commandApplicationCoordinator.GetBlockedUnifiedSearchResultTabIds(categoryId);
}
