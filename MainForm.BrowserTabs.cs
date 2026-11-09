using System.Drawing;
using System.Diagnostics;
using System.Media;
using System.Windows.Forms;
using MidFD.Commands;
using MidFD.Configuration;
using MidFD.Dialogs;
using MidFD.Helpers;
using MidFD.Models;
using MidFD.Presentation;
using MidFD.Runtime;
using MidFD.Services;
using MidFD.Services.Workspace;

namespace MidFD;

public partial class MainForm
{
    private string? _lastBrowserTabNavigationStructureKey;
    private bool _browserFileListFocusPending;

private void InitializeBrowserTabControl()
{
    _browserTabHostPanel = new Panel
    {
        Dock = DockStyle.Top,
        Height = GetBrowserTabStripHostHeight(),
        BackColor = MidFDColors.ListNormalBack,
        Margin = Padding.Empty,
        Name = "browserTabHostPanel",
        Padding = Padding.Empty,
        Visible = false
    };
    _browserTabHostPanel.Resize += (s, e) => LayoutBrowserTabControlWithinHost();
    _browserTabStrip = new BrowserTabStrip
    {
        Height = GetBrowserTabStripHostHeight(),
        Font = CreateBrowserTabFont(),
        Name = "browserTabStrip",
        BackColor = MidFDColors.ListNormalBack,
        ForeColor = MidFDColors.ListNormalFore,
        TabStop = false,
        PreferredTabWidth = GetBrowserTabWidth(),
        ActiveTabBackColor = MidFDColors.ListSelectedBack,
        InactiveTabBackColor = MidFDColors.ListNormalBack,
        TabBorderColor = MidFDColors.BorderLine,
        ActiveTabTextColor = Color.White,
        InactiveTabTextColor = MidFDColors.ListNormalFore,
        ShowCategoryRow = ShouldShowBrowserTabCategoryRow()
    };
    _browserTabStrip.Anchor = AnchorStyles.Top | AnchorStyles.Left;
    _browserTabStrip.CategoryClicked += BrowserTabStrip_CategoryClicked;
    _browserTabStrip.AddTabClicked += BrowserTabStrip_AddTabClicked;
    _browserTabStrip.SelectedIndexChanged += BrowserTabStrip_SelectedIndexChanged;
    _browserTabStrip.TabReordered += BrowserTabStrip_TabReordered;
    _browserTabStrip.CategoryReordered += BrowserTabStrip_CategoryReordered;
    _browserTabStrip.TabDoubleClicked += BrowserTabStrip_TabDoubleClicked;
    _browserTabStrip.SelectedTabReclicked += BrowserTabStrip_SelectedTabReclicked;
    _browserTabStrip.TabRightClicked += BrowserTabStrip_TabRightClicked;
    _browserTabStrip.TabListDropDownOpening += BrowserTabStrip_TabListDropDownOpening;
    _browserTabUiCoordinator.Bind(_browserTabStrip);
    _browserTabHostPanel.Controls.Add(_browserTabStrip);
    outerHostPanel.Controls.Add(_browserTabHostPanel);
    _browserTabNavigation = new BrowserTabNavigation
    {
        Name = "browserTabNavigation",
        Font = CreateBrowserTabFont(),
        BackColor = MidFDColors.ListNormalBack,
        ForeColor = MidFDColors.ListNormalFore,
        Width = GetBrowserTabNavigationWidth(),
        Visible = false
    };
    _browserTabNavigation.SelectedIndexChanged += BrowserTabStrip_SelectedIndexChanged;
    _browserTabNavigation.CategoryClicked += BrowserTabStrip_CategoryClicked;
    _browserTabNavigation.CategoryContextMenuRequested += BrowserTabStrip_CategoryClicked;
    _browserTabNavigation.AddTabForCategoryClicked += BrowserTabNavigation_AddTabForCategoryClicked;
    _browserTabNavigation.NavigationWidthChanged += BrowserTabNavigation_NavigationWidthChanged;
    _browserTabNavigation.TabDoubleClicked += BrowserTabStrip_TabDoubleClicked;
    _browserTabNavigation.SelectedTabReclicked += BrowserTabStrip_SelectedTabReclicked;
    _browserTabNavigation.TabRightClicked += BrowserTabStrip_TabRightClicked;
    _browserTabNavigation.GroupContextMenuRequested += BrowserTabNavigation_GroupContextMenuRequested;
    _browserTabNavigation.GroupExpandedChanged += ApplyBrowserTabGroupExpandedChanged;
    _browserTabNavigation.BrowserTabTreeDropRequested += BrowserTabNavigation_TreeDropRequested;
    outerHostPanel.Controls.Add(_browserTabNavigation);
    outerHostPanel.Controls.SetChildIndex(_browserTabHostPanel, 1);
    LayoutBrowserTabControlWithinHost();
}

    private bool ShouldShowBrowserTabCategoryRow()
    {
        return _settingsCoordinator.Value.Appearance?.ShowBrowserTabCategoryRow ?? true;
    }
    private bool IsVerticalBrowserTabLayout() => _settingsCoordinator.Value.BrowserTabs?.LayoutMode == BrowserTabLayoutMode.Vertical;
    private void SetBrowserTabLayoutFromMenu(BrowserTabLayoutMode mode)
    {
        _ = ExecuteCommandFromUi(CommandIds.BrowserTabLayoutSet, new CommandExecutionContext
        {
            Scope = CommandScope.Browser,
            Source = "Menu.TabLayout.Set",
            BrowserTabLayout = mode
        });
    }
    private int GetBrowserTabNavigationWidth() => Math.Clamp(_settingsCoordinator.Value.BrowserTabs?.NavigationWidth ?? BrowserTabSettings.DefaultNavigationWidth, 120, 600);
    private float GetBrowserTabFontSize()
    {
        _settingsCoordinator.EnsureNormalized();
        return _settingsCoordinator.Value.BrowserTabs.TabFontSize;
    }
    private Font CreateBrowserTabFont()
    {
        string familyName = _settingsCoordinator.Value?.Fonts?.FileListFontFamily ?? "Consolas";
        return MidFD.Helpers.FontResolver.CreateFont(familyName, GetBrowserTabFontSize(), FontStyle.Regular);
    }
    private int GetBrowserTabWidth()
    {
        _settingsCoordinator.EnsureNormalized();
        return _settingsCoordinator.Value.BrowserTabs.TabWidth;
    }
    private int GetBrowserTabStripHostHeight()
    {
        return ShouldShowBrowserTabCategoryRow()
            ? BrowserTabStripMultiRowHeight
            : BrowserTabStripSingleRowHeight;
    }
    private void ApplyBrowserTabStripDisplaySettings()
    {
        bool vertical = IsVerticalBrowserTabLayout();
        bool wasVertical = _browserTabNavigation?.Visible == true;
        if (wasVertical != vertical)
        {
            _lastBrowserTabHeaderSnapshotKey = null;
            _lastBrowserTabNavigationStructureKey = null;
        }
        int targetHeight = GetBrowserTabStripHostHeight();
        Control? layoutHost = _browserTabHostPanel?.Parent ?? _browserTabNavigation?.Parent;
        layoutHost?.SuspendLayout();
        try
        {
        if (_browserTabHostPanel != null)
        {
            _browserTabHostPanel.Height = targetHeight;
        }
        if (_browserTabStrip != null)
        {
            _browserTabStrip.Visible = !vertical;
            _browserTabStrip.ShowCategoryRow = ShouldShowBrowserTabCategoryRow();
            _browserTabStrip.PreferredTabWidth = GetBrowserTabWidth();
            _browserTabStrip.Height = targetHeight;
        }
        if (_browserTabHostPanel != null)
        {
            _browserTabHostPanel.Visible = !vertical;
            _browserTabHostPanel.Dock = DockStyle.Top;
        }
        if (_browserTabNavigation != null)
        {
            _browserTabNavigation.Visible = vertical;
            _browserTabNavigation.Dock = DockStyle.Left;
            _browserTabNavigation.Width = GetBrowserTabNavigationWidth();
            _browserTabNavigation.Font = CreateBrowserTabFont();
        }
        LayoutBrowserTabControlWithinHost();
        }
        finally
        {
            layoutHost?.ResumeLayout(performLayout: true);
            layoutHost?.PerformLayout();
            layoutHost?.Invalidate();
        }
    }
    private BrowserWorkspaceRuntimeStateSnapshot CaptureBrowserTabRuntimeStateSnapshot()
    {
        return _browserWorkspaceSnapshotApplicationCoordinator.CaptureRuntimeStateAndPersist(
            BuildBrowserTabStateFromCurrentUi());
    }
    private void ApplyBrowserTabRuntimeRestore(
        BrowserWorkspaceRuntimeRestoreExecution execution)
    {
        RefreshBrowserTabHeaders();
        if (execution.Switch is { } switchResult)
        {
            ApplyBrowserTabSwitchResult(switchResult, execution.Plan.TargetTabIndex);
        }
        if (_browserApplicationCoordinator.Workspace.TabCount == 0)
        {
            RefreshBrowserTabHeaders();
        }
        _browserTabStrip?.Invalidate();
        _browserTabHostPanel?.Invalidate();
    }

    private void ApplySettingsAppliedBrowserRuntimeState(
        BrowserWorkspaceRuntimeStateSnapshot runtimeState)
    {
        BrowserWorkspaceSettingsApplyExecution execution = _browserWorkspaceSnapshotApplicationCoordinator.ExecuteSettingsAppliedRestoreAndReload(
            runtimeState,
            BuildBrowserTabStateFromCurrentUi(),
            CreateDirectoryLoadOptions(),
            _browserApplicationCoordinator.ColumnCount,
            CaptureBrowserRefreshShellState());
        RefreshBrowserTabHeaders();
        if (execution.Restore.Switch is { } switchResult)
        {
            ApplyBrowserTabSwitchResult(switchResult, requestedIndex: execution.Restore.Plan.TargetTabIndex);
        }
        if (execution.Reload is { Load: { Succeeded: true } load } reload)
        {
            ApplyDirectoryLoadUi(load);
            ApplyDirectoryPostLoadEffects(reload.PostLoadEffects);
        }
        _browserTabStrip?.Invalidate();
        _browserTabHostPanel?.Invalidate();
    }
    private bool SwitchBrowserTabCategory(
        string categoryId,
        int? requestedTabIndex = null)
    {
        if (!string.Equals(_browserApplicationCoordinator.Workspace.ActiveCategoryId, categoryId, StringComparison.OrdinalIgnoreCase))
            ClearNameSearchInspectionReturnContext();
        if (_unifiedSearchSession is { IsActive: true } activeSearch
            && !string.Equals(activeSearch.SourceCategoryId, categoryId, StringComparison.OrdinalIgnoreCase))
        {
            DeactivateUnifiedSearchSurface();
        }
        int previousActiveTabIndex = _browserApplicationCoordinator.Workspace.ActiveTabIndex;
        string source = requestedTabIndex.HasValue
            ? "BrowserTab.CategorySwitch.TabTarget"
            : "BrowserTab.CategorySwitch";
        bool switched = requestedTabIndex is { } tabIndex
            ? ExecuteBrowserTabActivation(categoryId, tabIndex, source)
            : ExecuteCommandFromUi(CommandIds.BrowserTabCategoryActivate, new CommandExecutionContext
            {
                Scope = CommandScope.Browser,
                Source = source,
                CategoryId = categoryId
            });
        if (!switched)
        {
            RestoreBrowserTabSelectionAfterFailedSwitch(previousActiveTabIndex);
            RefreshBrowserTabHeaders();
        }
        return switched;
    }

    private bool ExecuteBrowserTabActivation(string categoryId, int tabIndex, string source)
    {
        IReadOnlyList<BrowserTabState> targetTabs = GetBrowserTabContextStates(categoryId);
        if (tabIndex < 0 || tabIndex >= targetTabs.Count)
        {
            return false;
        }

        return ExecuteCommandFromUi(CommandIds.BrowserTabActivate, new CommandExecutionContext
        {
            Scope = CommandScope.Browser,
            Source = source,
            CategoryId = categoryId,
            ContextTabIndex = tabIndex,
            ContextBrowserTabId = targetTabs[tabIndex].Id
        });
    }

    private void ApplyBrowserTabCategoryPresentation(int targetIndex)
    {
        bool vertical = IsVerticalBrowserTabLayout();
        if (!vertical)
        {
            RefreshBrowserTabHeaders();
            return;
        }

        if (_browserTabNavigation == null)
        {
            return;
        }

        List<BrowserTabNavigationCategoryItem> categories = BuildBrowserTabNavigationPresentationSnapshot();
        _lastBrowserTabNavigationStructureKey = BuildBrowserTabNavigationStructureKey();
        _browserTabNavigation.SetCategories(categories, _browserApplicationCoordinator.Workspace.GetActiveCategoryIndex(), targetIndex);
    }

    private List<BrowserTabNavigationCategoryItem> BuildBrowserTabNavigationPresentationSnapshot()
    {
        UpdateBrowserTabGroupsForLiveTabs();
        string activeCategoryId = _browserApplicationCoordinator.Workspace.ResolveCategoryId(_browserApplicationCoordinator.Workspace.ActiveCategoryId);
        var storedTabs = new Dictionary<string, IReadOnlyList<BrowserTabState>>(StringComparer.OrdinalIgnoreCase);
        foreach (BrowserTabCategoryDefinition category in _browserApplicationCoordinator.Workspace.CategoryStates)
        {
            if (!string.Equals(category.Id, activeCategoryId, StringComparison.OrdinalIgnoreCase))
            {
                storedTabs[category.Id] = BuildStoredBrowserTabPresentationStates(category.Id);
            }
        }
        List<BrowserTabNavigationCategoryItem> projected = BrowserTabNavigationProjection.BuildCategories(
            _browserApplicationCoordinator.Workspace.CategoryStates,
            activeCategoryId,
            _browserApplicationCoordinator.Workspace.TabStates,
            storedTabs,
            ShouldShowBrowserTabCategoryRow(),
            BuildBrowserTabPresentation,
            CreateUnifiedSearchNavigationChildItem(),
            _commandApplicationCoordinator.GetUnifiedSearchDerivedTabs(),
            _browserTabGroupRuntimeState.Groups);
        return projected;
    }

    private IReadOnlyList<BrowserTabState> BuildStoredBrowserTabPresentationStates(string categoryId)
    {
        BrowserTabRestoreCategoryState? categoryState = _browserWorkspacePersistenceApplicationCoordinator.FindStoredCategory(categoryId);
        return BrowserTabNavigationProjection.BuildStoredTabStates(categoryState, GetBrowserTabTitle);
    }
    private void SelectAdjacentBrowserTabCategory(int delta)
    {
        if (GuardClipboardBusy())
        {
            return;
        }
        if (!ShouldShowBrowserTabCategoryRow())
        {
            return;
        }
        _ = ExecuteCommandFromUi(
            delta < 0 ? CommandIds.BrowserTabCategoryPrevious : CommandIds.BrowserTabCategoryNext,
            CommandScope.Browser,
            delta < 0 ? "BrowserTab.CategoryPrevious" : "BrowserTab.CategoryNext");
    }
    private void BrowserTabStrip_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_suppressBrowserTabSelectionChanged || (_browserTabStrip == null && _browserTabNavigation == null))
        {
            return;
        }
        int newIndex = sender is BrowserTabNavigation navigation
            ? navigation.SelectedIndex
            : _browserTabStrip?.SelectedIndex ?? -1;
        BrowserTabStripItem? selectedItem = sender is BrowserTabNavigation selectedNavigation
            ? selectedNavigation.GetTabItem(selectedNavigation.SelectedCategoryIndex, newIndex)
            : _browserTabStrip?.GetTabItem(newIndex);
        Guid? searchSessionId = sender is BrowserTabNavigation searchNavigation
            ? searchNavigation.SelectedSearchSessionId
            : selectedItem is { Kind: BrowserTabStripItemKind.UnifiedSearch, RuntimeIdentity: { } runtimeIdentity }
                && Guid.TryParseExact(runtimeIdentity, "N", out Guid horizontalSessionId)
                    ? horizontalSessionId
                    : null;
        if (searchSessionId is { } selectedSearchSessionId)
        {
            OpenUnifiedSearchTab(selectedSearchSessionId);
            return;
        }
        if (selectedItem is { Kind: BrowserTabStripItemKind.UnifiedSearch, RuntimeIdentity: { } identity }
            && Guid.TryParseExact(identity, "N", out Guid sessionId))
        {
            OpenUnifiedSearchTab(sessionId);
            return;
        }
        if (_unifiedSearchSession is { IsActive: true }) DeactivateUnifiedSearchSurface();
        Guid? tabId = selectedItem?.BrowserTabId;
        if (tabId is { } browserTabId)
        {
            HideBrowserFileNameToolTip();
            int previousActiveTabIndex = _browserApplicationCoordinator.Workspace.ActiveTabIndex;
            if (sender is BrowserTabNavigation tabNavigation)
            {
                string categoryId = tabNavigation.SelectedCategoryIndex >= 0
                    && tabNavigation.SelectedCategoryIndex < _browserApplicationCoordinator.Workspace.CategoryStates.Count
                        ? _browserApplicationCoordinator.Workspace.CategoryStates[tabNavigation.SelectedCategoryIndex].Id
                        : _browserApplicationCoordinator.Workspace.ActiveCategoryId;
                IReadOnlyList<BrowserTabState> categoryTabs = GetBrowserTabContextStates(categoryId);
                int categoryTabIndex = GetBrowserTabIndexById(categoryTabs, browserTabId);
                if (categoryTabIndex >= 0
                    && !string.Equals(categoryId, _browserApplicationCoordinator.Workspace.ActiveCategoryId, StringComparison.OrdinalIgnoreCase))
                {
                    ClearNameSearchInspectionReturnContext();
                    bool activated = ExecuteCommandFromUi(CommandIds.BrowserTabActivate, new CommandExecutionContext
                    {
                        Scope = CommandScope.Browser,
                        Source = "BrowserTab.SelectedIndexChanged.CategoryTab",
                        CategoryId = categoryId,
                        ContextTabIndex = categoryTabIndex,
                        ContextBrowserTabId = browserTabId
                    });
                    if (!activated)
                    {
                        RestoreBrowserTabSelectionAfterFailedSwitch(previousActiveTabIndex);
                    }
                    return;
                }
            }
            int activeTabIndex = GetBrowserTabIndexById(_browserApplicationCoordinator.Workspace.TabStates, browserTabId);
            if (activeTabIndex >= 0)
            {
                ClearNameSearchInspectionReturnContextUnlessSource(browserTabId, _browserApplicationCoordinator.Workspace.ActiveCategoryId);
                bool activated = ExecuteCommandFromUi(CommandIds.BrowserTabActivate, new CommandExecutionContext
                {
                    Scope = CommandScope.Browser,
                    Source = "BrowserTab.SelectedIndexChanged",
                    ContextTabIndex = activeTabIndex,
                    ContextBrowserTabId = browserTabId,
                    CategoryId = _browserApplicationCoordinator.Workspace.ActiveCategoryId
                });
                if (!activated)
                {
                    RestoreBrowserTabSelectionAfterFailedSwitch(previousActiveTabIndex);
                }
            }
        }
    }
    private void BrowserTabStrip_CategoryClicked(object? sender, BrowserTabStripCategoryEventArgs e)
    {
        if (e.TabIndex >= 0)
        {
            BrowserTabStripItem? clickedItem = sender is BrowserTabNavigation navigation
                ? navigation.GetTabItem(e.CategoryIndex, e.TabIndex)
                : null;
            if (clickedItem is { Kind: BrowserTabStripItemKind.UnifiedSearch, RuntimeIdentity: { } runtimeId }
                && Guid.TryParseExact(runtimeId, "N", out Guid sessionId))
            {
                if (e.Button == MouseButtons.Left) OpenUnifiedSearchTab(sessionId);
                return;
            }
            if (e.Button == MouseButtons.Right)
            {
                BrowserTabStrip_TabRightClicked(
                    sender,
                    new BrowserTabStripMouseEventArgs(e.TabIndex, e.Button, e.Location),
                    e.CategoryId);
                return;
            }
            IReadOnlyList<BrowserTabState> targetTabs = GetBrowserTabContextStates(e.CategoryId);
            int targetIndex = clickedItem?.BrowserTabId is { } clickedTabId
                ? GetBrowserTabIndexById(targetTabs, clickedTabId)
                : e.TabIndex;
            if (targetIndex >= 0)
            {
                ClearNameSearchInspectionReturnContext();
                if (_unifiedSearchSession is { IsActive: true } activeSearch &&
                    !string.Equals(activeSearch.SourceCategoryId, e.CategoryId, StringComparison.OrdinalIgnoreCase))
                {
                    DeactivateUnifiedSearchSurface();
                }
                int previousActiveTabIndex = _browserApplicationCoordinator.Workspace.ActiveTabIndex;
                bool activated = ExecuteCommandFromUi(CommandIds.BrowserTabActivate, new CommandExecutionContext
                {
                    Scope = CommandScope.Browser,
                    Source = "BrowserTab.CategoryClicked.Tab",
                    CategoryId = e.CategoryId,
                    ContextTabIndex = targetIndex,
                    ContextBrowserTabId = clickedItem?.BrowserTabId
                });
                if (!activated)
                {
                    RestoreBrowserTabSelectionAfterFailedSwitch(previousActiveTabIndex);
                    RefreshBrowserTabHeaders();
                }
            }
            return;
        }
        if (e.Button == MouseButtons.Right)
        {
            ShowBrowserTabCategoryContextMenu(sender as Control ?? _browserTabStrip, e);
            return;
        }
        if (e.Button != MouseButtons.Left)
        {
            return;
        }
        if (e.Kind == BrowserTabStripCategoryItemKind.ManageEntry)
        {
            _ = ExecuteCommandFromUi(CommandIds.BrowserTabCategoryAdd, CommandScope.Browser, "BrowserTab.CategoryManageEntry");
            return;
        }
        bool categoryActivated = ExecuteCommandFromUi(CommandIds.BrowserTabCategoryActivate, new CommandExecutionContext
        {
            Scope = CommandScope.Browser,
            Source = "BrowserTab.CategoryClicked",
            CategoryId = e.CategoryId
        });
        if (!categoryActivated)
        {
            RefreshBrowserTabHeaders();
            UpdateMenuStripState();
        }
    }
    private void BrowserTabStrip_AddTabClicked(object? sender, EventArgs e)
    {
        _ = ExecuteCommandFromUi(CommandIds.BrowserTabNew, CommandScope.Browser, "BrowserTab.Plus");
    }
    private void BrowserTabNavigation_AddTabForCategoryClicked(object? sender, BrowserTabStripCategoryEventArgs e)
    {
        TryRunAfterSuccessfulBrowserTabCategorySwitch(
            () => ExecuteCommandFromUi(CommandIds.BrowserTabCategoryActivate, new CommandExecutionContext
            {
                Scope = CommandScope.Browser,
                Source = "BrowserTab.CategoryPlus.Activate",
                CategoryId = e.CategoryId
            }),
            () => _ = ExecuteCommandFromUi(CommandIds.BrowserTabNew, CommandScope.Browser, "BrowserTab.CategoryPlus"));
    }

    internal static bool TryRunAfterSuccessfulBrowserTabCategorySwitch(Func<bool> switchCategory, Action addTab)
    {
        ArgumentNullException.ThrowIfNull(switchCategory);
        ArgumentNullException.ThrowIfNull(addTab);
        if (!switchCategory()) return false;
        addTab();
        return true;
    }
    private void BrowserTabNavigation_NavigationWidthChanged(object? sender, EventArgs e)
    {
        int actualWidth = sender is BrowserTabNavigation navigation ? navigation.Width : GetBrowserTabNavigationWidth();
        _ = ExecuteCommandFromUi(CommandIds.BrowserTabNavigationWidthSet, new CommandExecutionContext
        {
            Scope = CommandScope.Browser,
            Source = "BrowserTab.NavigationWidthChanged",
            BrowserTabNavigationWidth = new BrowserTabNavigationWidthCommandContext(actualWidth)
        });
    }
    private IReadOnlyList<BrowserTabCategoryDefinition> GetBrowserTabCategoryDefinitionsForDialog()
    {
        return _browserApplicationCoordinator.Workspace.CategoryStates
            .Select(static category => category.Clone())
            .ToList();
    }
    private void OpenBrowserTabCategoryManager()
    {
        _browserCategoryWorkflowApplicationCoordinator.EnsureConfiguration();
        using var dialog = new CategoryManageDialog(
            GetBrowserTabCategoryDefinitionsForDialog,
            PromptAndAddBrowserTabCategory,
            RenameBrowserTabCategory,
            DeleteBrowserTabCategory,
            DeleteBrowserTabCategories);
        dialog.ShowDialog(this);
        RefreshBrowserTabHeaders();
        FocusBrowserFileList();
    }
    private string? AddGeneratedBrowserTabCategory()
    {
        return AddBrowserTabCategoryCore(_browserApplicationCoordinator.Workspace.GetNextCategoryDisplayName());
    }
    private string? PromptAndAddBrowserTabCategory()
    {
        string? displayName = SimpleInputDialog.ShowNullable("新しいカテゴリ名を入力してください。", "カテゴリ追加", "");
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }
        return AddBrowserTabCategoryCore(displayName);
    }
    private string? AddBrowserTabCategoryCore(string displayName)
    {
        string trimmedName = displayName.Trim();
        bool added = ExecuteCommandFromUi(CommandIds.BrowserTabCategoryAdd, new CommandExecutionContext
        {
            Scope = CommandScope.Browser,
            Source = "BrowserTab.CategoryAdd",
            CategoryDisplayName = trimmedName
        });
        return added ? trimmedName : null;
    }
    private BrowserTabCategoryDefinition? FindBrowserTabCategoryDefinition(string? categoryId)
        => string.IsNullOrWhiteSpace(categoryId)
            ? null
            : _browserApplicationCoordinator.Workspace.FindCategorySnapshot(categoryId);
    private string? MoveBrowserTabCategory(string categoryId, int delta)
    {
        bool moved = ExecuteCommandFromUi(
            delta < 0 ? CommandIds.BrowserTabCategoryMoveLeft : CommandIds.BrowserTabCategoryMoveRight,
            new CommandExecutionContext
            {
                Scope = CommandScope.Browser,
                Source = "BrowserTab.CategoryReorder",
                CategoryId = categoryId
            });
        if (!moved)
        {
            return null;
        }
        return FindBrowserTabCategoryDefinition(categoryId)?.DisplayName;
    }
    private bool MoveActiveBrowserTabCategory(int delta)
    {
        _browserCategoryWorkflowApplicationCoordinator.EnsureConfiguration();
        string? activeCategoryId = _browserApplicationCoordinator.Workspace.ActiveCategoryId;
        if (string.IsNullOrWhiteSpace(activeCategoryId))
        {
            return false;
        }
        return MoveBrowserTabCategory(activeCategoryId, delta) != null;
    }
    private string? RenameBrowserTabCategory(BrowserTabCategoryDefinition category)
    {
        BrowserTabCategoryDefinition? target = _browserApplicationCoordinator.Workspace.CategoryStates.FirstOrDefault(
            existing => string.Equals(existing.Id, category.Id, StringComparison.OrdinalIgnoreCase));
        if (target == null)
        {
            return null;
        }
        bool renamed = ExecuteCommandFromUi(CommandIds.BrowserTabCategoryRename, new CommandExecutionContext
        {
            Scope = CommandScope.Browser,
            Source = "BrowserTab.CategoryRename",
            CategoryId = target.Id
        });
        return renamed ? FindBrowserTabCategoryDefinition(target.Id)?.DisplayName : null;
    }
    private bool RenameActiveBrowserTabCategory()
    {
        _browserCategoryWorkflowApplicationCoordinator.EnsureConfiguration();
        BrowserTabCategoryDefinition? target = FindBrowserTabCategoryDefinition(_browserApplicationCoordinator.Workspace.ActiveCategoryId);
        return target != null && RenameBrowserTabCategory(target) != null;
    }
    private string? DeleteBrowserTabCategory(BrowserTabCategoryDefinition category)
    {
        BrowserTabCategoryDefinition? target = _browserApplicationCoordinator.Workspace.CategoryStates.FirstOrDefault(
            existing => string.Equals(existing.Id, category.Id, StringComparison.OrdinalIgnoreCase));
        if (target == null)
        {
            return null;
        }
        bool deleted = ExecuteCommandFromUi(CommandIds.BrowserTabCategoryDelete, new CommandExecutionContext
        {
            Scope = CommandScope.Browser,
            Source = "BrowserTab.CategoryDelete",
            CategoryId = target.Id
        });
        return deleted ? target.DisplayName : null;
    }
    private bool DeleteActiveBrowserTabCategory()
    {
        _browserCategoryWorkflowApplicationCoordinator.EnsureConfiguration();
        BrowserTabCategoryDefinition? target = FindBrowserTabCategoryDefinition(_browserApplicationCoordinator.Workspace.ActiveCategoryId);
        return target != null && DeleteBrowserTabCategory(target) != null;
    }
    private string? DeleteBrowserTabCategories(IReadOnlyList<BrowserTabCategoryDefinition> categories)
    {
        List<BrowserTabCategoryDefinition> targets = categories
            .Where(category => category != null)
            .Select(category =>
                _browserApplicationCoordinator.Workspace.CategoryStates.FirstOrDefault(existing => string.Equals(existing.Id, category.Id, StringComparison.OrdinalIgnoreCase)))
            .Where(static category => category != null)
            .Select(static category => category!)
            .GroupBy(category => category.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToList();
        if (targets.Count == 0)
        {
            return null;
        }
        if (targets.Count == 1)
        {
            return DeleteBrowserTabCategory(targets[0]);
        }
        string summary = string.Join("、", targets.Select(target => target.DisplayName));
        DialogResult confirm = MessageBox.Show(
            $"マークした {targets.Count} 件のカテゴリを削除します。よろしいですか？{Environment.NewLine}{summary}",
            "カテゴリ一括削除",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.OK)
        {
            return null;
        }
        return DeleteBrowserTabCategoriesCore(targets, $"カテゴリを削除しました: {targets.Count} 件");
    }
    private string? DeleteBrowserTabCategoriesCore(IReadOnlyList<BrowserTabCategoryDefinition> targets, string successMessage)
    {
        bool deleted = ExecuteCommandFromUi(CommandIds.BrowserTabCategoryDelete, new CommandExecutionContext
        {
            Scope = CommandScope.Browser,
            Source = "BrowserTab.CategoryDelete.Batch",
            CategoryIds = targets.Select(static target => target.Id).ToArray(),
            CategoryDeleteConfirmed = true,
            CategoryResultMessage = successMessage
        });
        return deleted ? successMessage : null;
    }
    private void LayoutBrowserTabControlWithinHost()
    {
        if (_browserTabStrip == null || _browserTabHostPanel == null)
        {
            return;
        }
        int hostWidth = Math.Max(0, _browserTabHostPanel.ClientSize.Width);
        _browserTabStrip.Bounds = new Rectangle(
            0,
            0,
            Math.Max(1, hostWidth),
            Math.Max(1, _browserTabHostPanel.ClientSize.Height));
    }
    private BrowserTabState BuildBrowserTabStateFromCurrentUi(
        bool validateMarks = false,
        IReadOnlyList<string>? markSourceOverride = null)
    {
        string currentPath = _browserApplicationCoordinator.CurrentPath;
        BrowserTabState? activeState = _browserApplicationCoordinator.Workspace.ActiveTabIndex >= 0 && _browserApplicationCoordinator.Workspace.ActiveTabIndex < _browserApplicationCoordinator.Workspace.TabCount
            ? _browserApplicationCoordinator.Workspace.TabStates[_browserApplicationCoordinator.Workspace.ActiveTabIndex]
            : null;
        bool isLocked = activeState?.IsLocked ?? false;
        return new BrowserTabState
        {
            Id = activeState?.Id ?? Guid.NewGuid(),
            Title = GetBrowserTabTitle(currentPath),
            CurrentPath = currentPath,
            IsLocked = isLocked,
            StartupPath = activeState?.StartupPath ?? string.Empty,
            IsReadOnly = activeState?.IsReadOnly ?? false,
            FilterLock = activeState?.FilterLock?.Clone() ?? new TabFilterLockState(),
            FilterPattern = _browserApplicationCoordinator.FilterPattern,
            FilterUseRegex = _browserApplicationCoordinator.FilterUseRegex,
            MarkedPaths = validateMarks && (activeState?.MarksDirty ?? false)
                ? BrowserMarkSlotWorkflowApplicationCoordinator.CreatePersistableMarkedPaths(
                    markSourceOverride ?? _browserApplicationCoordinator.Selection.Snapshot(),
                    out _)
                : (markSourceOverride ?? _browserApplicationCoordinator.Selection.Snapshot()).ToList(),
            Navigation = _browserApplicationCoordinator.NavigationSnapshot,
            FocusTargetName = GetCurrentBrowserItem() is ListViewItem item ? GetItemFullName(item) : null,
            CursorIndex = _browserApplicationCoordinator.CursorIndex,
            ColumnCount = _browserApplicationCoordinator.ColumnCount,
            SortKind = _browserApplicationCoordinator.CurrentSort,
            SortAscending = _browserApplicationCoordinator.SortAscending
        };
    }
    private void CaptureActiveBrowserTabState(
        bool captureMarks = true,
        bool validateMarks = false,
        IReadOnlyList<string>? markSourceOverride = null,
        bool markValidationSucceeded = false)
    {
        if (_browserApplicationCoordinator.Workspace.ActiveTabIndex < 0 || _browserApplicationCoordinator.Workspace.ActiveTabIndex >= _browserApplicationCoordinator.Workspace.TabCount)
        {
            return;
        }
        BrowserTabState currentState = _browserApplicationCoordinator.Workspace.GetTabSnapshot(_browserApplicationCoordinator.Workspace.ActiveTabIndex)!;
        bool shouldValidateMarks = validateMarks && captureMarks && currentState.MarksDirty;
        BrowserTabState latestState = BuildBrowserTabStateFromCurrentUi(shouldValidateMarks, markSourceOverride);
        _browserTabWorkflowApplicationCoordinator.ApplyCapturedState(
            _browserApplicationCoordinator.Workspace.ActiveTabIndex,
            latestState,
            captureMarks,
            shouldValidateMarks,
            markValidationSucceeded);
    }
    private void ApplyCompletedBrowserTabMarkState(BrowserTabState state, int skippedMarkCount)
    {
        if (skippedMarkCount > 0)
        {
            LogService.Info($"[BrowserTabs] Pruned stale per-tab marks. TabId={state.Id} Missing={skippedMarkCount}");
        }
        InvalidateMarkSummaryCache();
        InvalidateRecentMultiMarkIntent();
        ClearPendingEscExitMarkPersistence();
        _browserMarkInteractionController.SyncMarkState(
            hasMarks: _browserApplicationCoordinator.Selection.Count > 0);
        RefreshMarkUi();
    }
    private void RefreshBrowserTabHeaders()
    {
        if (_unifiedSearchSession is { } existingSearch && !HasUnifiedSearchSource(existingSearch))
        {
            DetachUnifiedSearchSession(existingSearch);
            ShowStatusMessage("検索元のBrowserタブが閉じられたため、検索結果を閉じました。");
            UpdateFunctionBar();
        }
        UpdateBrowserTabGroupsForLiveTabs();
        bool vertical = IsVerticalBrowserTabLayout();
        _suppressBrowserTabSelectionChanged = true;
        try
        {
            if (!vertical)
            {
                _browserTabUiCoordinator.RefreshHeaders(
                    _browserApplicationCoordinator.Workspace.TabStates,
                    _browserApplicationCoordinator.Workspace.ActiveTabIndex,
                    _browserApplicationCoordinator.Workspace.CategoryStates,
                    _browserApplicationCoordinator.Workspace.GetActiveCategoryIndex(),
                ShouldShowBrowserTabCategoryRow(),
                ref _lastBrowserTabHeaderSnapshotKey,
                BuildBrowserTabPresentation);
                if (_browserTabStrip != null)
                {
                    List<BrowserTabStripItem> items = _browserApplicationCoordinator.Workspace.TabStates
                        .Select(state =>
                        {
                            BrowserTabPresentationSnapshot presentation = BuildBrowserTabPresentation(state);
                            return new BrowserTabStripItem(presentation.HeaderText, presentation.ToolTipText,
                                presentation.CanonicalPath, presentation.PrefixText, presentation.BaseTitle, presentation.RelativeSuffix,
                                BrowserTabId: state.Id);
                        })
                        .ToList();
                    items = BrowserTabNavigationProjection.BuildHorizontalTabs(
                        items,
                        _browserApplicationCoordinator.Workspace.ActiveCategoryId,
                        CreateUnifiedSearchNavigationChildItem(),
                        _commandApplicationCoordinator.GetUnifiedSearchDerivedTabs(),
                        _browserTabGroupRuntimeState.Groups);
                    Guid? activeTabId = _browserApplicationCoordinator.Workspace.ActiveTabSnapshot?.Id;
                    int selectedIndex = _unifiedSearchSession is { IsActive: true } activeSearch
                        ? items.FindIndex(item => item.Kind == BrowserTabStripItemKind.UnifiedSearch
                            && item.RuntimeIdentity == activeSearch.Id.ToString("N"))
                        : items.FindIndex(item => item.BrowserTabId == activeTabId);
                    _browserTabStrip.SetTabs(items, selectedIndex);
                }
            }

            if (vertical && _browserTabNavigation != null)
            {
                string structureKey = BuildBrowserTabNavigationStructureKey();
                if (!string.Equals(_lastBrowserTabNavigationStructureKey, structureKey, StringComparison.Ordinal))
                {
                    List<BrowserTabNavigationCategoryItem> categories = BuildBrowserTabNavigationPresentationSnapshot();
                    _lastBrowserTabNavigationStructureKey = structureKey;
                    Guid? selectedSearchSessionId = _unifiedSearchSession is { IsActive: true } activeSearch
                        && string.Equals(activeSearch.SourceCategoryId, _browserApplicationCoordinator.Workspace.ActiveCategoryId, StringComparison.OrdinalIgnoreCase)
                            ? activeSearch.Id
                            : null;
                    _browserTabNavigation.SetCategories(
                        categories,
                        _browserApplicationCoordinator.Workspace.GetActiveCategoryIndex(),
                        _browserApplicationCoordinator.Workspace.ActiveTabIndex,
                        selectedSearchSessionId);
                }
                int selectedCategoryIndex = _browserApplicationCoordinator.Workspace.GetActiveCategoryIndex();
                Guid? selectedChildId = _unifiedSearchSession is { IsActive: true } currentSearch
                    && string.Equals(currentSearch.SourceCategoryId, _browserApplicationCoordinator.Workspace.ActiveCategoryId, StringComparison.OrdinalIgnoreCase)
                        ? currentSearch.Id
                        : null;
                _browserTabNavigation.UpdateSelection(
                    selectedCategoryIndex,
                    _browserApplicationCoordinator.Workspace.ActiveTabIndex,
                    selectedChildId);
                RefreshBrowserTabNavigationPathPresentations();
            }

            LayoutBrowserTabControlWithinHost();
        }
        finally
        {
            _suppressBrowserTabSelectionChanged = false;
        }
    }
    private string BuildBrowserTabNavigationStructureKey()
    {
        UpdateBrowserTabGroupsForLiveTabs();
        BrowserTabRestoreSnapshot snapshot = _settingsCoordinator.Value.Session?.BrowserTabRestoreSnapshot ?? new BrowserTabRestoreSnapshot();
        string activeCategoryId = _browserApplicationCoordinator.Workspace.ResolveCategoryId(_browserApplicationCoordinator.Workspace.ActiveCategoryId);
        string key = BrowserTabNavigationProjection.BuildStructureKey(
            _browserApplicationCoordinator.Workspace.CategoryStates,
            activeCategoryId,
            _browserApplicationCoordinator.Workspace.TabStates,
            snapshot,
            _commandApplicationCoordinator.GetUnifiedSearchDerivedTabs(),
            _browserTabGroupRuntimeState.Groups);
        if (_unifiedSearchSession is { } search)
        {
            key += $"|UnifiedSearch:{search.SourceCategoryId}:{search.SourceTabId:N}:{search.Id:N}:{(search.Result == null ? "running" : "complete")}";
        }
        return key;
    }

    private void UpdateBrowserTabGroupsForLiveTabs()
    {
        var liveTabCategories = new Dictionary<Guid, string>();
        string activeCategoryId = _browserApplicationCoordinator.Workspace.ResolveCategoryId(
            _browserApplicationCoordinator.Workspace.ActiveCategoryId);
        foreach (BrowserTabCategoryDefinition category in _browserApplicationCoordinator.Workspace.CategoryStates)
        {
            IReadOnlyList<BrowserTabState> tabs = string.Equals(category.Id, activeCategoryId, StringComparison.OrdinalIgnoreCase)
                ? _browserApplicationCoordinator.Workspace.TabStates
                : BuildStoredBrowserTabPresentationStates(category.Id);
            foreach (BrowserTabState tab in tabs) liveTabCategories[tab.Id] = category.Id;
        }
        _browserWorkspacePersistenceApplicationCoordinator.PruneBrowserTabGroupsToLiveTabs(liveTabCategories);
    }

    private string GetBrowserTabTitle(string? path)
    {
        return BrowserTabPresentationHelper.BuildTabTitle(
            path,
            normalizedPath => QuickAccessService.FindAliasDisplayName(_browserApplicationCoordinator.Workspace.QuickAccessSnapshot, normalizedPath));
    }

    private BrowserTabPresentationSnapshot BuildBrowserTabPresentation(BrowserTabState state)
    {
        return BrowserTabPresentationHelper.BuildPresentation(
            state,
            0,
            normalizedPath => QuickAccessService.FindAliasDisplayName(_browserApplicationCoordinator.Workspace.QuickAccessSnapshot, normalizedPath));
    }
    private void RefreshActiveBrowserTabNavigationPathPresentation()
    {
        if (_browserTabNavigation == null || !IsVerticalBrowserTabLayout()) return;
        int categoryIndex = _browserApplicationCoordinator.Workspace.GetActiveCategoryIndex();
        int tabIndex = _browserApplicationCoordinator.Workspace.ActiveTabIndex;
        if (categoryIndex < 0 || tabIndex < 0 || tabIndex >= _browserApplicationCoordinator.Workspace.TabStates.Count) return;

        BrowserTabState state = _browserApplicationCoordinator.Workspace.TabStates[tabIndex];
        BrowserTabPresentationSnapshot presentation = BuildBrowserTabPresentation(state);
        _browserTabNavigation.UpdateTabPathPresentation(
            categoryIndex,
            tabIndex,
            presentation.CanonicalPath,
            presentation.HeaderText,
            presentation.ToolTipText,
            presentation.PrefixText,
            baseTitle: presentation.BaseTitle,
            relativeSuffix: presentation.RelativeSuffix);
    }

    private void ApplyActiveBrowserTabPresentation(bool synchronizeSelection)
    {
        int tabIndex = _browserApplicationCoordinator.Workspace.ActiveTabIndex;
        if (tabIndex < 0 || tabIndex >= _browserApplicationCoordinator.Workspace.TabStates.Count)
        {
            return;
        }

        BrowserTabState state = _browserApplicationCoordinator.Workspace.TabStates[tabIndex];
        bool vertical = IsVerticalBrowserTabLayout();
        if (!vertical)
        {
            RefreshBrowserTabHeaders();
            return;
        }

        if (_browserTabNavigation == null)
        {
            return;
        }

        int categoryIndex = _browserApplicationCoordinator.Workspace.GetActiveCategoryIndex();
        if (categoryIndex < 0)
        {
            return;
        }

        BrowserTabPresentationSnapshot presentation = BuildBrowserTabPresentation(state);
        _browserTabNavigation.UpdateTabPathPresentation(
            categoryIndex,
            tabIndex,
            presentation.CanonicalPath,
            presentation.HeaderText,
            presentation.ToolTipText,
            presentation.PrefixText,
            synchronizeSelection,
            presentation.BaseTitle,
            presentation.RelativeSuffix);
    }

    private void ApplyVisibleBrowserTabSelection(int tabIndex)
    {
        _suppressBrowserTabSelectionChanged = true;
        try
        {
            if (IsVerticalBrowserTabLayout())
            {
                _browserTabNavigation?.UpdateSelection(_browserApplicationCoordinator.Workspace.GetActiveCategoryIndex(), tabIndex);
                return;
            }

            if (tabIndex >= 0 && tabIndex < _browserApplicationCoordinator.Workspace.TabCount)
            {
                RefreshBrowserTabHeaders();
            }
        }
        finally
        {
            _suppressBrowserTabSelectionChanged = false;
        }
    }

    private void RefreshBrowserTabNavigationPathPresentations()
    {
        if (_browserTabNavigation == null) return;
        string activeCategoryId = _browserApplicationCoordinator.Workspace.ResolveCategoryId(_browserApplicationCoordinator.Workspace.ActiveCategoryId);
        for (int categoryIndex = 0; categoryIndex < _browserApplicationCoordinator.Workspace.CategoryStates.Count; categoryIndex++)
        {
            BrowserTabCategoryDefinition category = _browserApplicationCoordinator.Workspace.CategoryStates[categoryIndex];
            IReadOnlyList<BrowserTabState> tabs = string.Equals(category.Id, activeCategoryId, StringComparison.OrdinalIgnoreCase)
                ? _browserApplicationCoordinator.Workspace.TabStates
                : BuildStoredBrowserTabPresentationStates(category.Id);
            for (int tabIndex = 0; tabIndex < tabs.Count; tabIndex++)
            {
                BrowserTabState state = tabs[tabIndex];
                BrowserTabPresentationSnapshot presentation = BuildBrowserTabPresentation(state);
                _browserTabNavigation.UpdateTabPathPresentation(
                    categoryIndex,
                    tabIndex,
                    presentation.CanonicalPath,
                    presentation.HeaderText,
                    presentation.ToolTipText,
                    presentation.PrefixText,
                    baseTitle: presentation.BaseTitle,
                    relativeSuffix: presentation.RelativeSuffix);
            }
        }
    }
    private void FocusBrowserFileList(bool force = false)
    {
        if ((!force && _browserFileListFocusPending) || IsDisposed || !IsHandleCreated)
        {
            return;
        }
        _browserFileListFocusPending = true;
        BeginInvoke(new Action(() =>
        {
            _browserFileListFocusPending = false;
            if (!IsDisposed && fileListView.CanFocus)
            {
                fileListView.Select();
                fileListView.Focus();
            }
            else if (!IsDisposed)
            {
                browserPanel.Focus();
            }
        }));
    }
    private void RefreshAllBrowserTabTitles()
    {
        IReadOnlyList<BrowserTabState> states = _browserApplicationCoordinator.Workspace.TabStates;
        for (int tabIndex = 0; tabIndex < states.Count; tabIndex++)
        {
            BrowserTabState state = states[tabIndex];
            BrowserTabPresentationSnapshot presentation = BuildBrowserTabPresentation(state);
            _browserTabWorkflowApplicationCoordinator.SetTitle(
                tabIndex,
                presentation.AliasTitle ?? presentation.DisplayCore);
        }
        RefreshBrowserTabHeaders();
    }
    private bool IsActiveBrowserTabLocked()
    {
        return _browserApplicationCoordinator.Workspace.HasLockedTab(_browserApplicationCoordinator.Workspace.ActiveTabIndex);
    }
    private bool IsActiveBrowserTabReadOnly()
    {
        return _browserApplicationCoordinator.Workspace.HasReadOnlyTab(_browserApplicationCoordinator.Workspace.ActiveTabIndex);
    }
    private bool GuardReadOnlyBrowserTab(string? operationName = null)
    {
        if (!IsActiveBrowserTabReadOnly())
        {
            return false;
        }
        string message = string.IsNullOrWhiteSpace(operationName)
            ? ReadOnlyBrowserTabBlockedMessage
            : $"このタブは ReadOnly のため、{operationName}は実行できません。";
        ShowStatusMessage(message, 2000);
        return true;
    }
    private TabFilterLockState GetActiveTabFilterLock()
    {
        if (_browserApplicationCoordinator.Workspace.ActiveTabIndex < 0 || _browserApplicationCoordinator.Workspace.ActiveTabIndex >= _browserApplicationCoordinator.Workspace.TabCount)
        {
            return TabFilterLockState.Disabled();
        }
        return _browserApplicationCoordinator.Workspace.TabStates[_browserApplicationCoordinator.Workspace.ActiveTabIndex].FilterLock;
    }
    private void TryPlayBrowserTabLimitBeep()
    {
        DateTime nowUtc = DateTime.UtcNow;
        if ((nowUtc - _lastBrowserTabLimitBeepUtc).TotalMilliseconds < 1200)
        {
            return;
        }
        _lastBrowserTabLimitBeepUtc = nowUtc;
        try
        {
            SystemSounds.Beep.Play();
        }
        catch
        {
            // 既定音が使えない環境では無音で続行する
        }
    }
    private bool TryCloseBrowserTab(int tabIndex)
    {
        if (GuardClipboardBusy())
        {
            return false;
        }
        if (tabIndex < 0 || tabIndex >= _browserApplicationCoordinator.Workspace.TabCount)
        {
            return false;
        }
        BrowserTabState target = _browserApplicationCoordinator.Workspace.TabStates[tabIndex];
        bool closed = ExecuteCommandFromUi(CommandIds.BrowserTabClose, new CommandExecutionContext
        {
            Scope = CommandScope.Browser,
            Source = "BrowserTab.Close",
            ContextTabIndex = tabIndex,
            ContextBrowserTabId = target.Id
        });
        return closed;
    }
    private void CloseCurrentBrowserTab()
    {
        TryCloseBrowserTab(_browserApplicationCoordinator.Workspace.ActiveTabIndex);
    }
    private bool CloseBrowserTabRange(string commandId, int preferredTabIndex, string nothingToCloseMessage)
    {
        if (GuardClipboardBusy())
        {
            return false;
        }
        if (preferredTabIndex < 0 || preferredTabIndex >= _browserApplicationCoordinator.Workspace.TabCount)
        {
            return false;
        }
        BrowserTabState target = _browserApplicationCoordinator.Workspace.TabStates[preferredTabIndex];
        bool closed = ExecuteCommandFromUi(commandId, new CommandExecutionContext
        {
            Scope = CommandScope.Browser,
            Source = "BrowserTab.CloseRange",
            ContextTabIndex = preferredTabIndex,
            ContextBrowserTabId = target.Id
        });
        if (!closed)
        {
            ShowStatusMessage(nothingToCloseMessage);
        }
        return closed;
    }
    private void CloseBrowserTabsToRight(int tabIndex)
    {
        CloseBrowserTabRange(CommandIds.BrowserTabCloseRight, tabIndex, "閉じられる右側タブはありません。");
    }
    private void CloseBrowserTabsToLeft(int tabIndex)
    {
        CloseBrowserTabRange(CommandIds.BrowserTabCloseLeft, tabIndex, "閉じられる左側タブはありません。");
    }
    private void CloseOtherBrowserTabs(int tabIndex)
    {
        CloseBrowserTabRange(CommandIds.BrowserTabCloseOther, tabIndex, "閉じられる他のタブはありません。");
    }
    private int CountClosableBrowserTabs(BrowserTabCloseScope scope)
    {
        return _browserTabWorkflowApplicationCoordinator.CountClosableTabs(scope);
    }
    private void BrowserTabStrip_TabDoubleClicked(object? sender, BrowserTabStripMouseEventArgs e)
    {
        BrowserTabStripItem? item = sender is BrowserTabNavigation navigation
            ? navigation.GetTabItem(navigation.SelectedCategoryIndex, e.TabIndex)
            : _browserTabStrip?.GetTabItem(e.TabIndex);
        if (item?.Kind == BrowserTabStripItemKind.UnifiedSearch) return;
        int tabIndex = item?.BrowserTabId is { } tabId
            ? GetBrowserTabIndexById(_browserApplicationCoordinator.Workspace.TabStates, tabId)
            : e.TabIndex;
        if (tabIndex < 0) return;
        bool executed = ExecuteCommandFromUi(
            CommandIds.BrowserTabLock,
            CommandScope.Browser,
            "BrowserTab.DoubleClick",
            contextTabIndex: tabIndex);
        if (executed)
        {
            FocusBrowserFileList(force: true);
        }
    }
    private void BrowserTabStrip_TabRightClicked(object? sender, BrowserTabStripMouseEventArgs e) =>
        BrowserTabStrip_TabRightClicked(sender, e, null);

    private void BrowserTabStrip_TabRightClicked(
        object? sender,
        BrowserTabStripMouseEventArgs e,
        string? categoryId)
    {
        BrowserTabStripItem? clickedItem = sender is BrowserTabNavigation nav
            ? (e.CategoryId is { } treeCategoryId
                ? nav.GetTabItem(treeCategoryId, e.TabIndex)
                : nav.GetTabItem(nav.SelectedCategoryIndex, e.TabIndex))
            : _browserTabStrip?.GetTabItem(e.TabIndex);
        if (clickedItem?.Kind == BrowserTabStripItemKind.UnifiedSearch) return;
        string targetCategoryId = !string.IsNullOrWhiteSpace(e.CategoryId)
            ? e.CategoryId
            : !string.IsNullOrWhiteSpace(categoryId)
                ? categoryId
                : _browserApplicationCoordinator.Workspace.ActiveCategoryId;
        IReadOnlyList<BrowserTabState> targetTabs = GetBrowserTabContextStates(targetCategoryId);
        Guid? targetTabId = e.BrowserTabId ?? clickedItem?.BrowserTabId;
        int targetTabIndex = targetTabId is { } clickedTabId
            ? GetBrowserTabIndexById(targetTabs, clickedTabId)
            : e.TabIndex;
        if (targetTabIndex < 0 || targetTabIndex >= targetTabs.Count)
        {
            return;
        }
        ClearBrowserTabContextState();
        _browserTabWorkflowApplicationCoordinator.SetContextTabIndex(targetTabIndex);
        _browserTabContextCategoryId = targetCategoryId;
        _browserTabContextTabId = targetTabs[targetTabIndex].Id;
        EnsureBrowserTabContextMenu();
        UpdateBrowserTabContextMenuItems(targetTabIndex);
        Control owner = sender as Control ?? _browserTabStrip ?? (Control)this;
        _browserTabContextMenu?.Show(owner, e.Location);
    }
    private bool SwitchBrowserTab(int newIndex)
    {
        if (_unifiedSearchSession is { IsActive: true }) DeactivateUnifiedSearchSurface();
        HideBrowserFileNameToolTip();
        if (_isSwitchingBrowserTab || newIndex < 0 || newIndex >= _browserApplicationCoordinator.Workspace.TabCount)
        {
            return false;
        }
        Guid targetTabId = _browserApplicationCoordinator.Workspace.TabStates[newIndex].Id;
        ClearNameSearchInspectionReturnContextUnlessSource(targetTabId, _browserApplicationCoordinator.Workspace.ActiveCategoryId);
        if (newIndex == _browserApplicationCoordinator.Workspace.ActiveTabIndex)
        {
            FocusBrowserFileList();
            return true;
        }
        int previousActiveTabIndex = _browserApplicationCoordinator.Workspace.ActiveTabIndex;
        bool switched = ExecuteBrowserTabActivation(
            _browserApplicationCoordinator.Workspace.ActiveCategoryId,
            newIndex,
            "BrowserTab.Switch");
        if (!switched)
        {
            RestoreBrowserTabSelectionAfterFailedSwitch(previousActiveTabIndex);
        }
        return switched;
    }

    private bool ApplyBrowserTabSwitchResult(
        BrowserTabSwitchWorkflowResult completion,
        int previousActiveTabIndex = -1,
        int? requestedIndex = null)
    {
        if (completion.Kind == BrowserTabSwitchWorkflowResultKind.NotAvailable)
        {
            if (previousActiveTabIndex >= 0)
            {
                RestoreBrowserTabSelectionAfterFailedSwitch(previousActiveTabIndex);
            }
            return false;
        }
        BrowserDirectoryLoadApplicationResult? directoryLoad = completion.DirectoryLoad;
        if (directoryLoad is not { Succeeded: true, Result: not null })
        {
            if (directoryLoad?.Error != null)
            {
                NotifyDirectoryLoadFailure(directoryLoad.Value.Error);
            }
            if (previousActiveTabIndex >= 0)
            {
                RestoreBrowserTabSelectionAfterFailedSwitch(previousActiveTabIndex);
            }
            return false;
        }

        if (!completion.Committed)
        {
            if (previousActiveTabIndex >= 0)
            {
                RestoreBrowserTabSelectionAfterFailedSwitch(previousActiveTabIndex);
            }
            return false;
        }

        ClearBrowserNamePrefixJump();
        int targetIndex = requestedIndex ?? completion.TargetTabIndex;
        Stopwatch totalStopwatch = Stopwatch.StartNew();
        Stopwatch markStateStopwatch = Stopwatch.StartNew();
        Stopwatch directoryApplyStopwatch = Stopwatch.StartNew();
        _isSwitchingBrowserTab = true;
        try
        {
            ApplyCompletedBrowserTabMarkState(completion.State!, completion.SkippedMarkCount);
            markStateStopwatch.Stop();
            directoryApplyStopwatch.Restart();
            ApplyPreparedBrowserTabSwitchDirectoryLoad(
                directoryLoad.Value,
                () =>
                {
                    ApplyVisibleBrowserTabSelection(targetIndex);
                    FocusBrowserFileList();
                });
            directoryApplyStopwatch.Stop();
        }
        finally
        {
            _isSwitchingBrowserTab = false;
        }
        Stopwatch postLoadStopwatch = Stopwatch.StartNew();
        ApplyDirectoryPostLoadEffects(completion.PostLoadEffects);
        postLoadStopwatch.Stop();
        totalStopwatch.Stop();
        LogService.Info(
            $"[BrowserTabSwitchPerf] shell totalMs={totalStopwatch.ElapsedMilliseconds} " +
            $"markStateMs={markStateStopwatch.ElapsedMilliseconds} directoryApplyMs={directoryApplyStopwatch.ElapsedMilliseconds} " +
            $"postLoadMs={postLoadStopwatch.ElapsedMilliseconds} menuInfo=insideDirectoryApply " +
            $"restoredCount={completion.RestoredMarks.Count} skippedCount={completion.SkippedMarkCount}");
        return true;
    }

    private void BrowserTabStrip_SelectedTabReclicked(object? sender, BrowserTabStripMouseEventArgs e)
    {
        BrowserTabStripItem? item = sender is BrowserTabNavigation navigation
            ? navigation.GetTabItem(navigation.SelectedCategoryIndex, e.TabIndex)
            : _browserTabStrip?.GetTabItem(e.TabIndex);
        if (item?.Kind == BrowserTabStripItemKind.UnifiedSearch
            && item.RuntimeIdentity is { } runtimeId
            && Guid.TryParseExact(runtimeId, "N", out Guid sessionId))
        {
            OpenUnifiedSearchTab(sessionId);
            return;
        }
        int activeIndex = item?.BrowserTabId is { } tabId
            ? GetBrowserTabIndexById(_browserApplicationCoordinator.Workspace.TabStates, tabId)
            : e.TabIndex;
        if (e.Button == MouseButtons.Left && activeIndex == _browserApplicationCoordinator.Workspace.ActiveTabIndex)
        {
            FocusBrowserFileList();
        }
    }

    private void RestoreBrowserTabSelectionAfterFailedSwitch(int activeTabIndex)
    {
        _suppressBrowserTabSelectionChanged = true;
        try
        {
            if (IsVerticalBrowserTabLayout())
            {
                _browserTabNavigation?.UpdateSelection(_browserApplicationCoordinator.Workspace.GetActiveCategoryIndex(), activeTabIndex);
            }
            else if (_browserTabStrip != null)
            {
                Guid activeTabId = activeTabIndex >= 0 && activeTabIndex < _browserApplicationCoordinator.Workspace.TabCount
                    ? _browserApplicationCoordinator.Workspace.TabStates[activeTabIndex].Id
                    : Guid.Empty;
                int projectionIndex = activeTabId == Guid.Empty
                    ? -1
                    : _browserTabStrip.FindBrowserTabProjectionIndex(activeTabId);
                if (projectionIndex >= 0) _browserTabStrip.SelectedIndex = projectionIndex;
            }
        }
        finally
        {
            _suppressBrowserTabSelectionChanged = false;
        }
    }
}
