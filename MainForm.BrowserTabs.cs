using System.Drawing;
using System.Media;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using MidFD.Commands;
using MidFD.Configuration;
using MidFD.Dialogs;
using MidFD.Helpers;
using MidFD.Models;
using MidFD.Presentation;
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
    _browserTabNavigation.TabReordered += BrowserTabStrip_TabReordered;
    _browserTabNavigation.CategoryReordered += BrowserTabStrip_CategoryReordered;
    _browserTabNavigation.TabDoubleClicked += BrowserTabStrip_TabDoubleClicked;
    _browserTabNavigation.SelectedTabReclicked += BrowserTabStrip_SelectedTabReclicked;
    _browserTabNavigation.TabRightClicked += BrowserTabStrip_TabRightClicked;
    outerHostPanel.Controls.Add(_browserTabNavigation);
    outerHostPanel.Controls.SetChildIndex(_browserTabHostPanel, 1);
    LayoutBrowserTabControlWithinHost();
}

    private bool ShouldShowBrowserTabCategoryRow()
    {
        return _settings.Appearance?.ShowBrowserTabCategoryRow ?? true;
    }
    private bool IsVerticalBrowserTabLayout() => _settings.BrowserTabs?.LayoutMode == BrowserTabLayoutMode.Vertical;
    private bool SetBrowserTabLayout(BrowserTabLayoutMode mode)
    {
        _settings.BrowserTabs ??= new BrowserTabSettings();
        if (_settings.BrowserTabs.LayoutMode == mode)
        {
            return false;
        }
        _settings.BrowserTabs.LayoutMode = mode;
        SettingsManager.Save(_settings);
        ApplyBrowserTabStripDisplaySettings();
        RefreshBrowserTabHeaders();
        return true;
    }
    private void SetBrowserTabLayoutFromMenu(BrowserTabLayoutMode mode)
    {
        SetBrowserTabLayout(mode);
    }
    private bool ToggleBrowserTabLayout()
    {
        BrowserTabLayoutMode current = _settings.BrowserTabs?.LayoutMode ?? BrowserTabLayoutMode.Horizontal;
        return SetBrowserTabLayout(current == BrowserTabLayoutMode.Horizontal
            ? BrowserTabLayoutMode.Vertical
            : BrowserTabLayoutMode.Horizontal);
    }
    private int GetBrowserTabNavigationWidth() => Math.Clamp(_settings.BrowserTabs?.NavigationWidth ?? BrowserTabSettings.DefaultNavigationWidth, 120, 600);
    private float GetBrowserTabFontSize()
    {
        _settings.BrowserTabs ??= new BrowserTabSettings();
        return _settings.BrowserTabs.TabFontSize;
    }
    private Font CreateBrowserTabFont()
    {
        string familyName = _settings?.Fonts?.FileListFontFamily ?? "Consolas";
        return MidFD.Helpers.FontResolver.CreateFont(familyName, GetBrowserTabFontSize(), FontStyle.Regular);
    }
    private int GetBrowserTabWidth()
    {
        _settings.BrowserTabs ??= new BrowserTabSettings();
        return _settings.BrowserTabs.TabWidth;
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
    private void InitializeInitialBrowserTab()
    {
        var initialState = BuildBrowserTabStateFromCurrentUi();
        _browserTabViewState.Clear();
        _browserTabViewState.Add(initialState);
        _browserTabViewState.ActiveTabIndex = 0;
        // Fresh-profile startup previously updated only the runtime state and
        // SelectedIndex.  The strip/tree remained empty until a later
        // structural operation (for example, adding a tab) called the full
        // projection.  Apply the completed initial state through the same
        // startup projection used by restored tabs.
        RefreshBrowserTabHeaders();
    }
    private void EnsureBrowserTabCategoryConfiguration()
    {
        _settings.BrowserTabs ??= new BrowserTabSettings();
        _settings.Session ??= new SessionSettings();
        _categoryViewState.Clear();
        var normalizedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BrowserTabCategoryDefinition category in _settings.BrowserTabs.Categories ?? Enumerable.Empty<BrowserTabCategoryDefinition>())
        {
            string normalizedId = NormalizeBrowserTabCategoryId(category.Id);
            if (!normalizedIds.Add(normalizedId))
            {
                continue;
            }
            _categoryViewState.Add(new BrowserTabCategoryDefinition
            {
                Id = normalizedId,
                DisplayName = string.IsNullOrWhiteSpace(category.DisplayName) ? "既定" : category.DisplayName.Trim()
            });
        }
        if (_categoryViewState.Count == 0)
        {
            _categoryViewState.Add(CreateDefaultBrowserTabCategoryDefinition());
        }
        _settings.BrowserTabs.Categories = _categoryViewState.Categories
            .Select(static category => category.Clone())
            .ToList();
        _settings.Session.ActiveBrowserTabCategoryId = ResolveExistingBrowserTabCategoryId(_settings.Session.ActiveBrowserTabCategoryId);
    }
    private void SyncActiveBrowserTabCategoryFromSession()
    {
        EnsureBrowserTabCategoryConfiguration();
        string sessionCategoryId = _settings.Session.BrowserTabRestoreSnapshot?.ActiveCategoryId
            ?? _settings.Session.ActiveBrowserTabCategoryId;
        _categoryViewState.ActiveCategoryId = ResolveExistingBrowserTabCategoryId(sessionCategoryId);
    }
    private string NormalizeBrowserTabCategoryId(string? categoryId)
    {
        string trimmed = string.IsNullOrWhiteSpace(categoryId)
            ? BrowserTabSettings.DefaultCategoryId
            : categoryId.Trim();
        return string.Equals(trimmed, BrowserTabSettings.DefaultCategoryId, StringComparison.OrdinalIgnoreCase)
            ? BrowserTabSettings.DefaultCategoryId
            : trimmed;
    }
    private string ResolveExistingBrowserTabCategoryId(string? categoryId)
    {
        string normalizedId = NormalizeBrowserTabCategoryId(categoryId);
        if (_categoryViewState.Categories.Any(category => string.Equals(category.Id, normalizedId, StringComparison.OrdinalIgnoreCase)))
        {
            return normalizedId;
        }
        return _categoryViewState.FirstOrDefault()?.Id ?? BrowserTabSettings.DefaultCategoryId;
    }
    private static BrowserTabCategoryDefinition CreateDefaultBrowserTabCategoryDefinition()
    {
        return new BrowserTabCategoryDefinition
        {
            Id = BrowserTabSettings.DefaultCategoryId,
            DisplayName = "既定"
        };
    }
    private BrowserTabCategoryDefinition EnsureAtLeastOneBrowserTabCategoryAfterDeletion()
    {
        if (_categoryViewState.Count > 0)
        {
            return _categoryViewState.Categories[0];
        }
        string displayName = GenerateNextBrowserTabCategoryDisplayName();
        var generatedCategory = new BrowserTabCategoryDefinition
        {
            Id = CreateUniqueBrowserTabCategoryId(displayName),
            DisplayName = displayName
        };
        _categoryViewState.Add(generatedCategory);
        return generatedCategory;
    }
    private sealed class BrowserTabRuntimeStateSnapshot
    {
        public List<BrowserTabCategoryDefinition> CategoryDefinitions { get; init; } = new();
        public BrowserTabRestoreSnapshot RestoreSnapshot { get; init; } = new();
        public string ActiveCategoryId { get; init; } = BrowserTabSettings.DefaultCategoryId;
    }
    private void SyncBrowserTabCategoryDefinitionsToSettings()
    {
        _settings.BrowserTabs ??= new BrowserTabSettings();
        _settings.BrowserTabs.Categories = _categoryViewState.Categories
            .Select(static category => category.Clone())
            .ToList();
    }
    private BrowserTabRuntimeStateSnapshot CaptureBrowserTabRuntimeStateSnapshot()
    {
        CaptureActiveBrowserTabState();
        StoreActiveBrowserTabCategorySessionState(updateCompatibilityMirror: false);
        return new BrowserTabRuntimeStateSnapshot
        {
            CategoryDefinitions = _categoryViewState.Categories
                .Select(static category => category.Clone())
                .ToList(),
            RestoreSnapshot = EnsureBrowserTabRestoreSnapshot().Clone(),
            ActiveCategoryId = ResolveExistingBrowserTabCategoryId(_categoryViewState.ActiveCategoryId)
        };
    }
    private static List<BrowserTabCategorySessionState> BuildCategorySessionStatesFromSnapshot(BrowserTabRestoreSnapshot snapshot)
    {
        return snapshot.Categories
            .Where(static category => category != null && !string.IsNullOrWhiteSpace(category.Id))
            .Select(static category => new BrowserTabCategorySessionState
            {
                CategoryId = category.Id,
                ActiveTabIndex = category.ActiveTabIndex,
                OpenTabs = category.OpenTabs.Select(static tab => tab.Clone()).ToList()
            })
            .ToList();
    }
    private void RestoreBrowserTabRuntimeStateSnapshot(BrowserTabRuntimeStateSnapshot runtimeState)
    {
        _settings.BrowserTabs ??= new BrowserTabSettings();
        _settings.Session ??= new SessionSettings();
        _settings.BrowserTabs.Categories = runtimeState.CategoryDefinitions
            .Select(static category => category.Clone())
            .ToList();
        _settings.Session.BrowserTabRestoreSnapshot = runtimeState.RestoreSnapshot.Clone();
        EnsureBrowserTabCategoryConfiguration();
        _categoryViewState.ActiveCategoryId = ResolveExistingBrowserTabCategoryId(runtimeState.ActiveCategoryId);
        _settings.Session.ActiveBrowserTabCategoryId = _categoryViewState.ActiveCategoryId;
        _settings.Session.BrowserTabCategories = BuildCategorySessionStatesFromSnapshot(_settings.Session.BrowserTabRestoreSnapshot);
        BrowserTabRestoreCategoryState? activeCategoryState = FindBrowserTabRestoreCategoryState(_categoryViewState.ActiveCategoryId);
        _settings.Session.OpenTabs = activeCategoryState?.OpenTabs.Select(static tab => tab.Clone()).ToList()
            ?? new List<BrowserTabSessionState>();
        _settings.Session.ActiveTabIndex = activeCategoryState?.ActiveTabIndex ?? 0;
        List<BrowserTabState> targetTabs = LoadBrowserTabsForCategory(_categoryViewState.ActiveCategoryId);
        int targetIndex = Math.Clamp(
            ResolveBrowserTabCategoryActiveIndex(_categoryViewState.ActiveCategoryId, targetTabs.Count),
            0,
            Math.Max(0, targetTabs.Count - 1));
        _browserTabViewState.Clear();
        _browserTabViewState.AddRange(targetTabs);
        _browserTabViewState.ContextTabIndex = -1;
        RefreshBrowserTabHeaders();
        if (_browserTabViewState.Count > 0)
        {
            _browserTabViewState.ActiveTabIndex = -1;
            SwitchBrowserTab(targetIndex);
        }
        else
        {
            _browserTabViewState.ActiveTabIndex = -1;
        }
        if (_browserTabViewState.Count == 0)
        {
            RefreshBrowserTabHeaders();
        }
        _browserTabStrip?.Invalidate();
        _browserTabHostPanel?.Invalidate();
    }
    private static List<BrowserTabCategoryDefinition> BuildBrowserTabCategoryDefinitionsFromSnapshot(BrowserTabRestoreSnapshot snapshot)
    {
        return snapshot.Categories
            .Where(static category => category != null && !string.IsNullOrWhiteSpace(category.Id))
            .Select(static category => new BrowserTabCategoryDefinition
            {
                Id = category.Id,
                DisplayName = string.IsNullOrWhiteSpace(category.DisplayName) ? "既定" : category.DisplayName
            })
            .ToList();
    }
    private static BrowserTabRuntimeStateSnapshot CreateBrowserTabRuntimeStateSnapshot(WorkspaceState workspaceState)
    {
        BrowserTabRestoreSnapshot snapshot = workspaceState.RestoreSnapshot.Clone();
        return new BrowserTabRuntimeStateSnapshot
        {
            CategoryDefinitions = BuildBrowserTabCategoryDefinitionsFromSnapshot(snapshot),
            RestoreSnapshot = snapshot,
            ActiveCategoryId = string.IsNullOrWhiteSpace(snapshot.ActiveCategoryId)
                ? BrowserTabSettings.DefaultCategoryId
                : snapshot.ActiveCategoryId
        };
    }
    private WorkspaceState CaptureWorkspaceSnapshotState()
    {
        BrowserTabRuntimeStateSnapshot runtimeState = CaptureBrowserTabRuntimeStateSnapshot();
        return new WorkspaceState
        {
            RestoreSnapshot = runtimeState.RestoreSnapshot.Clone(),
            SavedAtUtc = DateTime.UtcNow
        };
    }
    private BrowserTabRestoreSnapshot EnsureBrowserTabRestoreSnapshot()
    {
        _settings.Session ??= new SessionSettings();
        BrowserTabRestoreSnapshot snapshot = (_settings.Session.BrowserTabRestoreSnapshot ?? new BrowserTabRestoreSnapshot()).Clone();
        var existingStates = snapshot.Categories
            .Where(static category => category != null)
            .GroupBy(category => NormalizeBrowserTabCategoryId(category.Id), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.First().Clone(),
                StringComparer.OrdinalIgnoreCase);
        var normalizedSnapshot = new BrowserTabRestoreSnapshot
        {
            ActiveCategoryId = ResolveExistingBrowserTabCategoryId(snapshot.ActiveCategoryId)
        };
        foreach (BrowserTabCategoryDefinition category in _categoryViewState.Categories)
        {
            string categoryId = NormalizeBrowserTabCategoryId(category.Id);
            existingStates.TryGetValue(categoryId, out BrowserTabRestoreCategoryState? existingState);
            normalizedSnapshot.Categories.Add(new BrowserTabRestoreCategoryState
            {
                Id = categoryId,
                DisplayName = string.IsNullOrWhiteSpace(category.DisplayName) ? "既定" : category.DisplayName.Trim(),
                ActiveTabIndex = existingState?.ActiveTabIndex ?? 0,
                OpenTabs = existingState?.OpenTabs.Select(static tab => tab.Clone()).ToList() ?? new List<BrowserTabSessionState>()
            });
        }
        if (normalizedSnapshot.Categories.Count == 0)
        {
            normalizedSnapshot.Categories.Add(new BrowserTabRestoreCategoryState
            {
                Id = BrowserTabSettings.DefaultCategoryId,
                DisplayName = "既定"
            });
        }
        normalizedSnapshot.ActiveCategoryId = ResolveExistingBrowserTabCategoryId(normalizedSnapshot.ActiveCategoryId);
        _settings.Session.BrowserTabRestoreSnapshot = normalizedSnapshot;
        return normalizedSnapshot;
    }
    private BrowserTabRestoreCategoryState? FindBrowserTabRestoreCategoryState(string categoryId)
    {
        BrowserTabRestoreSnapshot snapshot = EnsureBrowserTabRestoreSnapshot();
        string resolvedCategoryId = ResolveExistingBrowserTabCategoryId(categoryId);
        return snapshot.Categories.FirstOrDefault(
            category => string.Equals(category.Id, resolvedCategoryId, StringComparison.OrdinalIgnoreCase));
    }
    private string CreateUniqueBrowserTabCategoryId(string displayName)
    {
        string baseId = Regex.Replace(displayName.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        if (string.IsNullOrWhiteSpace(baseId))
        {
            baseId = "category";
        }
        if (string.Equals(baseId, BrowserTabSettings.DefaultCategoryId, StringComparison.OrdinalIgnoreCase))
        {
            baseId = "category";
        }
        string candidate = baseId;
        int suffix = 2;
        while (_categoryViewState.Categories.Any(category => string.Equals(category.Id, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{baseId}-{suffix}";
            suffix++;
        }
        return candidate;
    }
    private void SaveBrowserTabsToSettings()
    {
        EnsureBrowserTabCategoryConfiguration();
        _settings.Session ??= new SessionSettings();
        if (!_settings.Session.RestoreTabsOnStartup)
        {
            _settings.Session.ClearBrowserTabRestoreState();
            LogService.Info("[BrowserTabs] Save cleared because tab restore is disabled.");
            return;
        }
        StoreActiveBrowserTabCategorySessionState(updateCompatibilityMirror: false);
        string activeCategoryId = ResolveExistingBrowserTabCategoryId(_categoryViewState.ActiveCategoryId);
        BrowserTabRestoreCategoryState? activeCategoryState = FindBrowserTabRestoreCategoryState(activeCategoryId);
        int activeTabIndex = activeCategoryState?.ActiveTabIndex ?? 0;
        int tabCount = activeCategoryState?.OpenTabs?.Count ?? 0;
        LogService.Info($"[BrowserTabs] Saved Category={activeCategoryId} Tabs={tabCount} ActiveIndex={activeTabIndex}");
    }
    private bool SaveWorkspaceStateStore(bool captureActiveState = true)
    {
        if (_workspaceStateStore == null)
        {
            return true;
        }
        try
        {
            if (captureActiveState)
            {
                CaptureActiveBrowserTabState(validateMarks: true);
            }
            if (!_settings.Session.RestoreTabsOnStartup)
            {
                _workspaceStateStore.Clear();
                LogService.Info("[WorkspaceStore] Cleared because workspace restore is disabled.");
                return true;
            }
            BrowserTabRestoreSnapshot snapshot = EnsureBrowserTabRestoreSnapshot().Clone();
            _workspaceStateStore.Save(WorkspaceStateMigrationService.FromSessionSnapshot(snapshot));
            LogService.Info($"[WorkspaceStore] Saved categories={snapshot.Categories.Count} active={snapshot.ActiveCategoryId}");
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("Workspace state save failed. Session snapshot fallback remains available.", ex);
            return false;
        }
    }
    private bool TryLoadWorkspaceStateStore(out BrowserTabRestoreSnapshot? snapshot)
    {
        snapshot = null;
        if (_workspaceStateStore == null)
        {
            return false;
        }
        try
        {
            WorkspaceState? workspaceState = _workspaceStateStore.Load();
            if (workspaceState?.RestoreSnapshot?.Categories is not { Count: > 0 })
            {
                LogService.Info("[WorkspaceStore] No workspace restore state found.");
                return false;
            }
            snapshot = workspaceState.RestoreSnapshot.Clone();
            LogService.Info($"[WorkspaceStore] Loaded categories={snapshot.Categories.Count} active={snapshot.ActiveCategoryId}");
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("Workspace state load failed. Falling back to SessionSettings snapshot.", ex);
            return false;
        }
    }
    private void ApplyWorkspaceRestoreSnapshotToSettings(BrowserTabRestoreSnapshot snapshot)
    {
        _settings.BrowserTabs ??= new BrowserTabSettings();
        _settings.Session ??= new SessionSettings();
        _settings.Session.BrowserTabRestoreSnapshot = snapshot.Clone();
        _settings.BrowserTabs.Categories = snapshot.Categories
            .Select(static category => new BrowserTabCategoryDefinition
            {
                Id = string.IsNullOrWhiteSpace(category.Id) ? BrowserTabSettings.DefaultCategoryId : category.Id,
                DisplayName = string.IsNullOrWhiteSpace(category.DisplayName) ? "既定" : category.DisplayName
            })
            .ToList();
        _settings.Session.BrowserTabCategories = BuildCategorySessionStatesFromSnapshot(snapshot);
        _settings.Session.ActiveBrowserTabCategoryId = string.IsNullOrWhiteSpace(snapshot.ActiveCategoryId)
            ? BrowserTabSettings.DefaultCategoryId
            : snapshot.ActiveCategoryId;
        BrowserTabRestoreCategoryState? activeCategory = snapshot.Categories.FirstOrDefault(
            category => string.Equals(category.Id, _settings.Session.ActiveBrowserTabCategoryId, StringComparison.OrdinalIgnoreCase))
            ?? snapshot.Categories.FirstOrDefault();
        _settings.Session.OpenTabs = activeCategory?.OpenTabs.Select(static tab => tab.Clone()).ToList()
            ?? new List<BrowserTabSessionState>();
        _settings.Session.ActiveTabIndex = activeCategory?.ActiveTabIndex ?? 0;
    }
    private List<BrowserTabSessionState> SerializeBrowserTabsForSession(IReadOnlyList<BrowserTabState> sourceTabs, out int activeTabIndex)
    {
        IReadOnlyList<BrowserTabState> limitedTabs = sourceTabs;
        int maxTabCount = GetMaxBrowserTabsPerCategory();
        if (limitedTabs.Count > maxTabCount)
        {
            LogService.Warn($"[BrowserTabs] Save source exceeded max tabs. Source={limitedTabs.Count} Max={maxTabCount}");
            limitedTabs = limitedTabs.Take(maxTabCount).ToList();
            ShowStatusMessage($"タブ数が上限を超えたため、{maxTabCount} 個まで保存しました。");
        }
        List<BrowserTabSessionState> serializedTabs = limitedTabs
            .Where(static tab => !string.IsNullOrWhiteSpace(tab.CurrentPath))
            .Select(CreateBrowserTabSessionState)
            .ToList();
        activeTabIndex = serializedTabs.Count == 0
            ? 0
            : Math.Clamp(_browserTabViewState.ActiveTabIndex, 0, serializedTabs.Count - 1);
        return serializedTabs;
    }
    private void StoreActiveBrowserTabCategorySessionState(bool updateCompatibilityMirror)
    {
        EnsureBrowserTabCategoryConfiguration();
        _settings.Session ??= new SessionSettings();
        string activeCategoryId = ResolveExistingBrowserTabCategoryId(_categoryViewState.ActiveCategoryId);
        List<BrowserTabSessionState> serializedTabs = SerializeBrowserTabsForSession(_browserTabViewState.Tabs, out int activeTabIndex);
        BrowserTabRestoreSnapshot snapshot = EnsureBrowserTabRestoreSnapshot();
        snapshot.ActiveCategoryId = activeCategoryId;
        BrowserTabRestoreCategoryState? categoryState = snapshot.Categories.FirstOrDefault(
            category => string.Equals(category.Id, activeCategoryId, StringComparison.OrdinalIgnoreCase));
        if (categoryState == null)
        {
            categoryState = new BrowserTabRestoreCategoryState
            {
                Id = activeCategoryId,
                DisplayName = _categoryViewState.Categories
                    .FirstOrDefault(category => string.Equals(category.Id, activeCategoryId, StringComparison.OrdinalIgnoreCase))
                    ?.DisplayName ?? activeCategoryId
            };
            snapshot.Categories.Add(categoryState);
        }
        categoryState.DisplayName = _categoryViewState.Categories
            .FirstOrDefault(category => string.Equals(category.Id, activeCategoryId, StringComparison.OrdinalIgnoreCase))
            ?.DisplayName ?? categoryState.DisplayName;
        categoryState.ActiveTabIndex = activeTabIndex;
        categoryState.OpenTabs = serializedTabs.Select(static tab => tab.Clone()).ToList();
        _settings.Session.BrowserTabRestoreSnapshot = snapshot;
        if (updateCompatibilityMirror)
        {
            _settings.Session.ActiveBrowserTabCategoryId = activeCategoryId;
            _settings.Session.BrowserTabCategories = UpsertBrowserTabCategorySessionState(
                _settings.Session.BrowserTabCategories,
                new BrowserTabCategorySessionState
                {
                    CategoryId = activeCategoryId,
                    OpenTabs = serializedTabs.Select(static tab => tab.Clone()).ToList(),
                    ActiveTabIndex = activeTabIndex
                });
            _settings.Session.OpenTabs = serializedTabs.Select(static tab => tab.Clone()).ToList();
            _settings.Session.ActiveTabIndex = activeTabIndex;
        }
        LogService.Info(
            $"[BrowserTabCategory] Store Category={activeCategoryId} Tabs={serializedTabs.Count} ActiveIndex={activeTabIndex} " +
            $"MirrorUpdated={updateCompatibilityMirror}");
    }
    private static List<BrowserTabCategorySessionState> UpsertBrowserTabCategorySessionState(
        IEnumerable<BrowserTabCategorySessionState>? existingStates,
        BrowserTabCategorySessionState updatedState)
    {
        var mergedStates = new List<BrowserTabCategorySessionState>();
        bool replaced = false;
        foreach (BrowserTabCategorySessionState state in existingStates ?? Enumerable.Empty<BrowserTabCategorySessionState>())
        {
            if (state == null || string.IsNullOrWhiteSpace(state.CategoryId))
            {
                continue;
            }
            if (string.Equals(state.CategoryId, updatedState.CategoryId, StringComparison.OrdinalIgnoreCase))
            {
                mergedStates.Add(updatedState.Clone());
                replaced = true;
            }
            else
            {
                mergedStates.Add(state.Clone());
            }
        }
        if (!replaced)
        {
            mergedStates.Add(updatedState.Clone());
        }
        return mergedStates;
    }
    private BrowserTabSessionState CreateBrowserTabSessionState(BrowserTabState tabState)
    {
        NavigationService.NavigationSnapshot navigation = tabState.Navigation ?? new NavigationService.NavigationSnapshot();
        return new BrowserTabSessionState
        {
            TabId = tabState.Id == Guid.Empty ? Guid.NewGuid() : tabState.Id,
            CurrentPath = tabState.CurrentPath,
            IsLocked = tabState.IsLocked,
            StartupPath = tabState.StartupPath,
            IsReadOnly = tabState.IsReadOnly,
            FilterLock = tabState.FilterLock?.Clone() ?? new TabFilterLockState(),
            MarkedPaths = PersistBrowserTabMarkedPaths(tabState),
            BackHistory = navigation.BackHistory.ToList(),
            ForwardHistory = navigation.ForwardHistory.ToList(),
            LastVisitedPathByDrive = navigation.LastVisitedPathByDrive.ToDictionary(
                static pair => pair.Key.ToString(),
                static pair => pair.Value,
                StringComparer.OrdinalIgnoreCase),
            FocusTargetName = tabState.FocusTargetName,
            CursorIndex = tabState.CursorIndex,
            ColumnCount = tabState.ColumnCount,
            SortKind = tabState.SortKind,
            SortAscending = tabState.SortAscending
        };
    }
    private List<string> PersistBrowserTabMarkedPaths(BrowserTabState tabState)
    {
        if (!tabState.MarksDirty)
        {
            return tabState.MarkedPaths.ToList();
        }

        List<string> persisted = CreatePersistableMarkedPaths(tabState.MarkedPaths, out _);
        tabState.MarkedPaths = persisted;
        tabState.MarksDirty = false;
        return persisted;
    }
    private bool TryRestoreBrowserTabsOnStartup(out int restoredTabCount, out int skippedTabCount, out bool hadSavedTabs)
    {
        restoredTabCount = 0;
        skippedTabCount = 0;
        hadSavedTabs = false;
        try
        {
            EnsureBrowserTabCategoryConfiguration();
            _settings.Session ??= new SessionSettings();
            if (!_settings.Session.RestoreTabsOnStartup)
            {
                LogService.Info("[BrowserTabs] Restore skipped because tab restore is disabled.");
                _restoredBrowserTabsFromWorkspaceStore = false;
                return false;
            }
            bool workspaceStoreLoaded = TryLoadWorkspaceStateStore(out BrowserTabRestoreSnapshot? workspaceSnapshot);
            _restoredBrowserTabsFromWorkspaceStore = workspaceStoreLoaded;
            if (workspaceSnapshot != null)
            {
                ApplyWorkspaceRestoreSnapshotToSettings(workspaceSnapshot);
            }
            EnsureBrowserTabCategoryConfiguration();
            BrowserTabRestoreSnapshot snapshot = EnsureBrowserTabRestoreSnapshot();
            string restoredCategoryId = ResolveExistingBrowserTabCategoryId(snapshot.ActiveCategoryId);
            List<BrowserTabSessionState> savedTabs = GetBrowserTabSessionStatesForRestore(ref restoredCategoryId);
            hadSavedTabs = savedTabs.Count > 0;
            if (!hadSavedTabs)
            {
                LogService.Info("[BrowserTabs] Restore skipped because no saved tabs were found.");
                return false;
            }
            int maxTabCount = GetMaxBrowserTabsPerCategory();
            if (savedTabs.Count > maxTabCount)
            {
                LogService.Warn($"[BrowserTabs] Restore source exceeded max tabs. Source={savedTabs.Count} Max={maxTabCount}");
                savedTabs = savedTabs.Take(maxTabCount).ToList();
                ShowStatusMessage($"保存タブが上限を超えたため、{maxTabCount} 個まで復元しました。");
            }
            var restoredTabs = new List<BrowserTabState>();
            foreach (BrowserTabSessionState sessionTab in savedTabs)
            {
                if (!TryCreateBrowserTabStateFromSession(sessionTab, out BrowserTabState? restoredTab))
                {
                    skippedTabCount++;
                    continue;
                }
                restoredTabs.Add(restoredTab!);
            }
            if (restoredTabs.Count == 0)
            {
                LogService.Info($"[BrowserTabs] Restore skipped because all saved tabs were unavailable. Missing={skippedTabCount}");
                return false;
            }
            _browserTabViewState.Clear();
            _browserTabViewState.AddRange(restoredTabs);
            int totalRestoredTabs = 0;
            foreach (BrowserTabRestoreCategoryState category in snapshot.Categories)
            {
                if (string.Equals(category.Id, restoredCategoryId, StringComparison.OrdinalIgnoreCase))
                {
                    totalRestoredTabs += restoredTabs.Count;
                }
                else
                {
                    totalRestoredTabs += category.OpenTabs.Count;
                }
            }
            restoredTabCount = totalRestoredTabs;
            _categoryViewState.ActiveCategoryId = restoredCategoryId;
            _settings.Session.ActiveBrowserTabCategoryId = restoredCategoryId;
            int targetIndex = ResolveBrowserTabCategoryActiveIndex(restoredCategoryId, restoredTabs.Count);
            _browserTabViewState.ActiveTabIndex = targetIndex;
            RefreshBrowserTabHeaders();
            _browserTabViewState.ActiveTabIndex = -1;
            SwitchBrowserTab(targetIndex);
            LogService.Info($"[BrowserTabs] Restored Category={restoredCategoryId} Tabs={restoredTabCount} Missing={skippedTabCount} ActiveIndex={targetIndex}");
            if (!workspaceStoreLoaded)
            {
                SaveWorkspaceStateStore();
            }
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("Unexpected error during startup browser tabs restoration. Falling back to default startup.", ex);
            restoredTabCount = 0;
            skippedTabCount = 0;
            hadSavedTabs = false;
            return false;
        }
    }
    private List<BrowserTabSessionState> GetBrowserTabSessionStatesForRestore(ref string restoredCategoryId)
    {
        string requestedCategoryId = restoredCategoryId;
        BrowserTabRestoreSnapshot snapshot = EnsureBrowserTabRestoreSnapshot();
        List<BrowserTabRestoreCategoryState> categoryStates = snapshot.Categories
            .Where(static state => state != null && !string.IsNullOrWhiteSpace(state.Id))
            .Select(static state => state.Clone())
            .ToList();
        BrowserTabRestoreCategoryState? activeCategoryState = categoryStates.FirstOrDefault(
            state => string.Equals(state.Id, requestedCategoryId, StringComparison.OrdinalIgnoreCase));
        if (activeCategoryState == null)
        {
            activeCategoryState = categoryStates.FirstOrDefault(state => state.OpenTabs.Count > 0)
                ?? categoryStates.FirstOrDefault();
        }
        if (activeCategoryState != null && activeCategoryState.OpenTabs.Count > 0)
        {
            restoredCategoryId = ResolveExistingBrowserTabCategoryId(activeCategoryState.Id);
            return activeCategoryState.OpenTabs.Select(static tab => tab.Clone()).ToList();
        }
        restoredCategoryId = BrowserTabSettings.DefaultCategoryId;
        return new List<BrowserTabSessionState>();
    }
    private int ResolveBrowserTabCategoryActiveIndex(string categoryId, int restoredTabCount)
    {
        if (restoredTabCount <= 0)
        {
            return 0;
        }
        BrowserTabRestoreCategoryState? categoryState = FindBrowserTabRestoreCategoryState(categoryId);
        if (categoryState != null && categoryState.OpenTabs.Count > 0)
        {
            return Math.Clamp(categoryState.ActiveTabIndex, 0, restoredTabCount - 1);
        }
        return 0;
    }
    private int GetActiveBrowserTabCategoryIndex()
    {
        if (_categoryViewState.Count == 0)
        {
            return -1;
        }
        int categoryIndex = _categoryViewState.FindIndex(
            category => string.Equals(category.Id, _categoryViewState.ActiveCategoryId, StringComparison.OrdinalIgnoreCase));
        return categoryIndex >= 0 ? categoryIndex : 0;
    }
    private List<BrowserTabState> LoadBrowserTabsForCategory(string categoryId)
    {
        EnsureBrowserTabCategoryConfiguration();
        _settings.Session ??= new SessionSettings();
        BrowserTabRestoreCategoryState? categoryState = FindBrowserTabRestoreCategoryState(categoryId);
        var restoredTabs = new List<BrowserTabState>();
        int sessionTabCount = categoryState?.OpenTabs?.Count ?? 0;
        foreach (BrowserTabSessionState sessionTab in categoryState?.OpenTabs ?? Enumerable.Empty<BrowserTabSessionState>())
        {
            if (TryCreateBrowserTabStateFromSession(sessionTab, out BrowserTabState? restoredTab))
            {
                restoredTabs.Add(restoredTab!);
            }
        }
        bool usedFallback = false;
        string fallbackReason = "None";
        if (restoredTabs.Count == 0)
        {
            BrowserTabState fallbackState = CreateInitialBrowserTabStateForCategory(categoryId);
            restoredTabs.Add(fallbackState);
            usedFallback = true;
            fallbackReason = sessionTabCount == 0 ? "InitializeCategory" : "RestoreUnavailable";
        }
        LogService.Info(
            $"[BrowserTabCategory] Load Category={categoryId} SessionTabs={sessionTabCount} RestoredTabs={restoredTabs.Count} " +
            $"UsedFallback={usedFallback} FallbackReason={fallbackReason} CurrentUiPath={_navigationService.CurrentPath}");
        return restoredTabs;
    }
    private BrowserTabState CreateInitialBrowserTabStateForCategory(string categoryId)
    {
        string initialPath = _navigationService.CurrentPath;
        if (string.IsNullOrWhiteSpace(initialPath) || !Directory.Exists(initialPath))
        {
            initialPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        if (string.IsNullOrWhiteSpace(initialPath) || !Directory.Exists(initialPath))
        {
            initialPath = AppContext.BaseDirectory;
        }
        string resolvedCategoryId = ResolveExistingBrowserTabCategoryId(categoryId);
        LogService.Info(
            $"[BrowserTabCategory] InitializeCategory Category={resolvedCategoryId} InitialPath={initialPath} " +
            $"CurrentUiPath={_navigationService.CurrentPath}");
        return new BrowserTabState
        {
            Title = GetBrowserTabTitle(initialPath),
            CurrentPath = initialPath,
            IsLocked = false,
            Navigation = new NavigationService.NavigationSnapshot
            {
                CurrentPath = initialPath,
                BackHistory = Array.Empty<string>(),
                ForwardHistory = Array.Empty<string>(),
                LastVisitedPathByDrive = new Dictionary<char, string>()
            },
            FocusTargetName = null,
            CursorIndex = 0,
            ColumnCount = Math.Clamp(_columnCount, 1, 9),
            SortKind = _currentSort,
            SortAscending = _sortAscending
        };
    }
    private void SwitchBrowserTabCategory(string categoryId, int? requestedTabIndex = null)
    {
        EnsureBrowserModeBeforeWorkspaceNavigation();
        string targetCategoryId = ResolveExistingBrowserTabCategoryId(categoryId);
        LogService.Info(
            $"[BrowserTabCategory] Switch Requested={categoryId} Resolved={targetCategoryId} ActiveBefore={_categoryViewState.ActiveCategoryId} " +
            $"TabsBefore={_browserTabViewState.Count} ActiveIndexBefore={_browserTabViewState.ActiveTabIndex}");
        if (string.Equals(targetCategoryId, _categoryViewState.ActiveCategoryId, StringComparison.OrdinalIgnoreCase))
        {
            if (requestedTabIndex.HasValue && requestedTabIndex.Value >= 0 && requestedTabIndex.Value < _browserTabViewState.Count
                && requestedTabIndex.Value != _browserTabViewState.ActiveTabIndex)
            {
                SwitchBrowserTab(requestedTabIndex.Value);
                return;
            }
            ClearBrowserTabCategoryContextState();
            RefreshBrowserTabHeaders();
            UpdateMenuStripState();
            _browserTabStrip?.Invalidate();
            _browserTabHostPanel?.Invalidate();
            FocusBrowserFileList();
            LogService.Info($"[BrowserTabCategory] Switch skipped because target category was already active: {targetCategoryId}");
            return;
        }
        CaptureActiveBrowserTabState();
        List<BrowserTabState> targetTabs = LoadBrowserTabsForCategory(targetCategoryId);
        int targetIndex = requestedTabIndex.HasValue && requestedTabIndex.Value >= 0 && requestedTabIndex.Value < targetTabs.Count
            ? requestedTabIndex.Value
            : Math.Clamp(ResolveBrowserTabCategoryActiveIndex(targetCategoryId, targetTabs.Count), 0, Math.Max(0, targetTabs.Count - 1));
        LogService.Info($"[BrowserTabCategory] Switch loaded Category={targetCategoryId} Tabs={targetTabs.Count} TargetIndex={targetIndex}");

        BrowserTabState targetTab = targetTabs[targetIndex];
        string targetPath = string.IsNullOrWhiteSpace(targetTab.CurrentPath) || !Directory.Exists(targetTab.CurrentPath)
            ? (Directory.Exists(_navigationService.CurrentPath) ? _navigationService.CurrentPath : Environment.CurrentDirectory)
            : targetTab.CurrentPath;
        BrowserLoadCoordinator.DirectoryLoadResult? preparedLoad = PrepareBrowserTabSwitchDirectoryLoad(targetTab, targetPath);
        if (preparedLoad == null)
        {
            return;
        }

        StoreActiveBrowserTabCategorySessionState(updateCompatibilityMirror: false);
        _browserTabViewState.Clear();
        _browserTabViewState.AddRange(targetTabs);
        _categoryViewState.ActiveCategoryId = targetCategoryId;
        ClearBrowserTabContextState();
        ClearBrowserTabCategoryContextState();
        _browserTabViewState.ActiveTabIndex = targetIndex;
        _isSwitchingBrowserTab = true;
        try
        {
            _columnCount = Math.Clamp(targetTab.ColumnCount, 1, 9);
            _currentSort = targetTab.SortKind;
            _sortAscending = targetTab.SortAscending;
            _navigationService.RestoreState(targetTab.Navigation);
            RestoreMarksForBrowserTab(targetTab);
            CommitPreparedBrowserTabSwitchDirectoryLoad(
                preparedLoad,
                () =>
                {
                    ApplyBrowserTabCategoryPresentation(targetIndex);
                    FocusBrowserFileList();
                });
        }
        finally
        {
            _isSwitchingBrowserTab = false;
        }
        UpdateMenuStripState();
        StoreActiveBrowserTabCategorySessionState(updateCompatibilityMirror: false);
        LogService.Info(
            $"[BrowserTabCategory] Switch applied ActiveAfter={_categoryViewState.ActiveCategoryId} TabsAfter={_browserTabViewState.Count} " +
            $"ActiveIndexAfter={_browserTabViewState.ActiveTabIndex}");
            ShowStatusMessage($"カテゴリを切り替えました: {_categoryViewState.Categories[GetActiveBrowserTabCategoryIndex()].DisplayName}");
    }

    private void ApplyBrowserTabCategoryPresentation(int targetIndex)
    {
        bool vertical = IsVerticalBrowserTabLayout();
        if (!vertical)
        {
            _browserTabUiCoordinator.RefreshHeaders(
                _browserTabViewState.Tabs,
                targetIndex,
                _categoryViewState.Categories,
                GetActiveBrowserTabCategoryIndex(),
                ShouldShowBrowserTabCategoryRow(),
                ref _lastBrowserTabHeaderSnapshotKey,
                BuildBrowserTabPresentation);
            return;
        }

        if (_browserTabNavigation == null)
        {
            return;
        }

        List<BrowserTabNavigationCategoryItem> categories = BuildBrowserTabNavigationPresentationSnapshot();
        _lastBrowserTabNavigationStructureKey = BuildBrowserTabNavigationStructureKey();
        _browserTabNavigation.SetCategories(categories, GetActiveBrowserTabCategoryIndex(), targetIndex);
    }

    private List<BrowserTabNavigationCategoryItem> BuildBrowserTabNavigationPresentationSnapshot()
    {
        string activeCategoryId = ResolveExistingBrowserTabCategoryId(_categoryViewState.ActiveCategoryId);
        var categories = new List<BrowserTabNavigationCategoryItem>();
        foreach (BrowserTabCategoryDefinition category in _categoryViewState.Categories)
        {
            IReadOnlyList<BrowserTabState> tabs = string.Equals(category.Id, activeCategoryId, StringComparison.OrdinalIgnoreCase)
                ? _browserTabViewState.Tabs
                : BuildStoredBrowserTabPresentationStates(category.Id);
            categories.Add(new BrowserTabNavigationCategoryItem(
                category.Id,
                string.IsNullOrWhiteSpace(category.DisplayName) ? "既定" : category.DisplayName,
                BrowserTabPresentationHelper.BuildCategoryToolTip(category),
                tabs.Select((state, i) =>
                {
                    BrowserTabPresentationSnapshot presentation = BuildBrowserTabPresentation(state);
                    return new BrowserTabStripItem(
                        presentation.HeaderText,
                        presentation.ToolTipText,
                        presentation.CanonicalPath,
                        presentation.PrefixText,
                        presentation.BaseTitle,
                        presentation.RelativeSuffix);
                }).ToList()));
        }
        if (ShouldShowBrowserTabCategoryRow())
        {
            categories.Add(new BrowserTabNavigationCategoryItem(
                BrowserTabStrip.ManageCategoriesEntryId,
                "＋カテゴリ",
                "新しいカテゴリを追加します。",
                Array.Empty<BrowserTabStripItem>(),
                BrowserTabStripCategoryItemKind.ManageEntry));
        }
        return categories;
    }

    private IReadOnlyList<BrowserTabState> BuildStoredBrowserTabPresentationStates(string categoryId)
    {
        BrowserTabRestoreCategoryState? categoryState = FindBrowserTabRestoreCategoryState(categoryId);
        var tabs = new List<BrowserTabState>();
        foreach (BrowserTabSessionState sessionTab in categoryState?.OpenTabs ?? Enumerable.Empty<BrowserTabSessionState>())
        {
            string path = sessionTab.CurrentPath ?? string.Empty;
            tabs.Add(new BrowserTabState
            {
                Id = sessionTab.TabId == Guid.Empty ? Guid.NewGuid() : sessionTab.TabId,
                Title = GetBrowserTabTitle(path),
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
        EnsureBrowserTabCategoryConfiguration();
        if (_categoryViewState.Count <= 1)
        {
            return;
        }
        int currentIndex = GetActiveBrowserTabCategoryIndex();
        if (currentIndex < 0)
        {
            currentIndex = 0;
        }
        int nextIndex = currentIndex + delta;
        if (nextIndex < 0 || nextIndex >= _categoryViewState.Count)
        {
            return;
        }
        LogService.Info(
            $"[BrowserTabCategory] SelectAdjacent Delta={delta} CurrentIndex={currentIndex} NextIndex={nextIndex} " +
            $"CategoryCount={_categoryViewState.Count} ActiveCategory={_categoryViewState.ActiveCategoryId}");
        SwitchBrowserTabCategory(_categoryViewState.Categories[nextIndex].Id);
    }
    private bool TryResolveBrowserTabRestorePath(BrowserTabSessionState sessionTab, out string restorePath)
    {
        restorePath = string.Empty;
        string currentPath = sessionTab.CurrentPath ?? string.Empty;
        string startupPath = sessionTab.StartupPath ?? string.Empty;
        if (sessionTab.IsLocked && !string.IsNullOrWhiteSpace(startupPath) && Directory.Exists(startupPath))
        {
            // ロックタブでも、現在パスがロックルート配下なら現在パスを優先して復元する
            if (!string.IsNullOrWhiteSpace(currentPath) && Directory.Exists(currentPath) &&
                IsPathUnderBrowserTabStartupPath(currentPath, new BrowserTabState { StartupPath = startupPath }))
            {
                restorePath = currentPath;
            }
            else
            {
                restorePath = startupPath;
            }
            return true;
        }
        if (Directory.Exists(currentPath))
        {
            restorePath = currentPath;
            if (sessionTab.IsLocked && !string.IsNullOrWhiteSpace(startupPath))
            {
                LogService.Warn($"[BrowserTabs] Locked startup path missing. StartupPath={startupPath} Fallback={restorePath}");
                ShowStatusMessage("固定タブの起動元が見つからないため、最後の場所を開きました。");
            }
            return true;
        }
        if (sessionTab.IsLocked && TryFindExistingParentDirectory(startupPath, out string parentPath))
        {
            restorePath = parentPath;
            LogService.Warn($"[BrowserTabs] Locked startup/current path missing. StartupPath={startupPath} CurrentPath={currentPath} Fallback={restorePath}");
            ShowStatusMessage("固定タブの起動元が見つからないため、親フォルダを開きました。");
            return true;
        }
        if (sessionTab.IsLocked)
        {
            restorePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(restorePath) || !Directory.Exists(restorePath))
            {
                restorePath = AppContext.BaseDirectory;
            }
            LogService.Warn($"[BrowserTabs] Locked tab restore fallback used. StartupPath={startupPath} CurrentPath={currentPath} Fallback={restorePath}");
            ShowStatusMessage("固定タブの起動元が見つからないため、代替フォルダを開きました。");
            return Directory.Exists(restorePath);
        }
        return false;
    }
    private static bool TryFindExistingParentDirectory(string? path, out string parentPath)
    {
        parentPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        string? candidate = path;
        while (!string.IsNullOrWhiteSpace(candidate))
        {
            candidate = Path.GetDirectoryName(candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
            {
                parentPath = candidate;
                return true;
            }
        }
        return false;
    }
    private bool TryCreateBrowserTabStateFromSession(BrowserTabSessionState sessionTab, out BrowserTabState? restoredTab)
    {
        restoredTab = null;
        if (sessionTab == null || !TryResolveBrowserTabRestorePath(sessionTab, out string restorePath))
        {
            return false;
        }
        var backHistory = (sessionTab.BackHistory ?? new List<string>())
            .Where(static path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .ToList();
        var forwardHistory = (sessionTab.ForwardHistory ?? new List<string>())
            .Where(static path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .ToList();
        var lastVisitedByDrive = new Dictionary<char, string>();
        foreach ((string driveKey, string path) in sessionTab.LastVisitedPathByDrive ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(driveKey) || driveKey.Length != 1 || string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                continue;
            }
            lastVisitedByDrive[driveKey[0]] = path;
        }
        restoredTab = new BrowserTabState
        {
            Id = sessionTab.TabId == Guid.Empty ? Guid.NewGuid() : sessionTab.TabId,
            Title = GetBrowserTabTitle(restorePath),
            CurrentPath = restorePath,
            IsLocked = sessionTab.IsLocked,
            StartupPath = sessionTab.StartupPath ?? string.Empty,
            IsReadOnly = sessionTab.IsReadOnly,
            FilterLock = sessionTab.FilterLock?.Clone() ?? new TabFilterLockState(),
            MarkedPaths = CreatePersistableMarkedPaths(sessionTab.MarkedPaths, out int skippedMarkCount),
            Navigation = new NavigationService.NavigationSnapshot
            {
                CurrentPath = restorePath,
                BackHistory = backHistory,
                ForwardHistory = forwardHistory,
                LastVisitedPathByDrive = lastVisitedByDrive
            },
            FocusTargetName = sessionTab.FocusTargetName,
            CursorIndex = Math.Max(0, sessionTab.CursorIndex),
            ColumnCount = Math.Clamp(sessionTab.ColumnCount, 1, 9),
            SortKind = sessionTab.SortKind,
            SortAscending = sessionTab.SortAscending
        };
        if (skippedMarkCount > 0)
        {
            LogService.Info($"[BrowserTabs] Pruned stale restored marks. TabId={restoredTab.Id} Missing={skippedMarkCount}");
        }
        return true;
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
        if (newIndex >= 0 && newIndex < _browserTabViewState.Count)
        {
            SwitchBrowserTab(newIndex);
        }
    }
    private void BrowserTabStrip_CategoryClicked(object? sender, BrowserTabStripCategoryEventArgs e)
    {
        if (e.TabIndex >= 0)
        {
            if (e.Button == MouseButtons.Right
                && string.Equals(e.CategoryId, _categoryViewState.ActiveCategoryId, StringComparison.OrdinalIgnoreCase))
            {
                BrowserTabStrip_TabRightClicked(sender, new BrowserTabStripMouseEventArgs(e.TabIndex, e.Button, e.Location));
                return;
            }
            SwitchBrowserTabCategory(e.CategoryId, e.TabIndex);
            if (e.Button == MouseButtons.Right)
            {
                BrowserTabStrip_TabRightClicked(sender, new BrowserTabStripMouseEventArgs(e.TabIndex, e.Button, e.Location));
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
        SwitchBrowserTabCategory(e.CategoryId);
    }
    private void BrowserTabStrip_AddTabClicked(object? sender, EventArgs e)
    {
        _ = ExecuteCommandFromUi(CommandIds.BrowserTabNew, CommandScope.Browser, "BrowserTab.Plus");
    }
    private void BrowserTabNavigation_AddTabForCategoryClicked(object? sender, BrowserTabStripCategoryEventArgs e)
    {
        SwitchBrowserTabCategory(e.CategoryId);
        _ = ExecuteCommandFromUi(CommandIds.BrowserTabNew, CommandScope.Browser, "BrowserTab.CategoryPlus");
    }
    private void BrowserTabNavigation_NavigationWidthChanged(object? sender, EventArgs e)
    {
        _settings.BrowserTabs ??= new BrowserTabSettings();
        int actualWidth = sender is BrowserTabNavigation navigation ? navigation.Width : GetBrowserTabNavigationWidth();
        _settings.BrowserTabs.NavigationWidth = Math.Clamp(actualWidth, 120, 600);
        SettingsManager.Save(_settings);
    }
    private IReadOnlyList<BrowserTabCategoryDefinition> GetBrowserTabCategoryDefinitionsForDialog()
    {
        return _categoryViewState.Categories
            .Select(static category => category.Clone())
            .ToList();
    }
    private void OpenBrowserTabCategoryManager()
    {
        EnsureBrowserTabCategoryConfiguration();
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
    private string GenerateNextBrowserTabCategoryDisplayName()
    {
        for (int i = 1; ; i++)
        {
            string candidate = $"カテゴリ{i}";
            if (!_categoryViewState.Categories.Any(category => string.Equals(category.DisplayName, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }
    }
    private string? AddGeneratedBrowserTabCategory()
    {
        return AddBrowserTabCategoryCore(GenerateNextBrowserTabCategoryDisplayName());
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
        if (_categoryViewState.Categories.Any(category => string.Equals(category.DisplayName, trimmedName, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("同じ表示名のカテゴリがすでにあります。", "カテゴリ追加", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }
        string newCategoryId = CreateUniqueBrowserTabCategoryId(trimmedName);
        _categoryViewState.Add(new BrowserTabCategoryDefinition
        {
            Id = newCategoryId,
            DisplayName = trimmedName
        });
        SyncBrowserTabCategoryDefinitionsToSettings();
        EnsureBrowserTabRestoreSnapshot();
        SwitchBrowserTabCategory(newCategoryId);
        SettingsManager.Save(_settings);
        ShowStatusMessage($"カテゴリを追加しました: {trimmedName}");
        return trimmedName;
    }
    private BrowserTabCategoryDefinition? FindBrowserTabCategoryDefinition(string? categoryId)
    {
        if (string.IsNullOrWhiteSpace(categoryId))
        {
            return null;
        }
        return _categoryViewState.FirstOrDefault(
            category => string.Equals(category.Id, categoryId, StringComparison.OrdinalIgnoreCase));
    }
    private string? MoveBrowserTabCategory(string categoryId, int delta)
    {
        if (delta == 0)
        {
            return null;
        }
        int currentIndex = _categoryViewState.FindIndex(category => string.Equals(category.Id, categoryId, StringComparison.OrdinalIgnoreCase));
        if (currentIndex < 0)
        {
            return null;
        }
        int targetIndex = currentIndex + delta;
        if (targetIndex < 0 || targetIndex >= _categoryViewState.Count)
        {
            return null;
        }
        CaptureActiveBrowserTabState();
        StoreActiveBrowserTabCategorySessionState(updateCompatibilityMirror: false);
        BrowserTabCategoryDefinition movedCategory = _categoryViewState.Categories[currentIndex];
        _categoryViewState.RemoveAt(currentIndex);
        _categoryViewState.Insert(targetIndex, movedCategory);
        SyncBrowserTabCategoryDefinitionsToSettings();
        EnsureBrowserTabRestoreSnapshot();
        RefreshBrowserTabHeaders();
        StoreActiveBrowserTabCategorySessionState(updateCompatibilityMirror: false);
        SettingsManager.Save(_settings);
        string direction = delta < 0 ? "左" : "右";
        ShowStatusMessage($"カテゴリを{direction}へ移動しました: {movedCategory.DisplayName}");
        FocusBrowserFileList();
        return movedCategory.DisplayName;
    }
    private bool MoveActiveBrowserTabCategory(int delta)
    {
        EnsureBrowserTabCategoryConfiguration();
        string? activeCategoryId = _categoryViewState.ActiveCategoryId;
        if (string.IsNullOrWhiteSpace(activeCategoryId))
        {
            return false;
        }
        return MoveBrowserTabCategory(activeCategoryId, delta) != null;
    }
    private string? RenameBrowserTabCategory(BrowserTabCategoryDefinition category)
    {
        BrowserTabCategoryDefinition? target = _categoryViewState.FirstOrDefault(
            existing => string.Equals(existing.Id, category.Id, StringComparison.OrdinalIgnoreCase));
        if (target == null)
        {
            return null;
        }
        string? renamed = SimpleInputDialog.ShowNullable("カテゴリ名を入力してください。", "カテゴリ名変更", target.DisplayName);
        if (string.IsNullOrWhiteSpace(renamed))
        {
            return null;
        }
        string trimmedName = renamed.Trim();
        if (_categoryViewState.Categories.Any(existing =>
                !string.Equals(existing.Id, target.Id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.DisplayName, trimmedName, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("同じ表示名のカテゴリがすでにあります。", "カテゴリ名変更", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return null;
        }
        target.DisplayName = trimmedName;
        SyncBrowserTabCategoryDefinitionsToSettings();
        EnsureBrowserTabRestoreSnapshot();
        SettingsManager.Save(_settings);
        RefreshBrowserTabHeaders();
        ShowStatusMessage($"カテゴリ名を更新しました: {trimmedName}");
        return trimmedName;
    }
    private bool RenameActiveBrowserTabCategory()
    {
        EnsureBrowserTabCategoryConfiguration();
        BrowserTabCategoryDefinition? target = FindBrowserTabCategoryDefinition(_categoryViewState.ActiveCategoryId);
        return target != null && RenameBrowserTabCategory(target) != null;
    }
    private string? DeleteBrowserTabCategory(BrowserTabCategoryDefinition category)
    {
        BrowserTabCategoryDefinition? target = _categoryViewState.FirstOrDefault(
            existing => string.Equals(existing.Id, category.Id, StringComparison.OrdinalIgnoreCase));
        if (target == null)
        {
            return null;
        }
        if (string.Equals(target.Id, BrowserTabSettings.DefaultCategoryId, StringComparison.OrdinalIgnoreCase))
        {
            ShowStatusMessage("既定カテゴリは削除できません。");
            return null;
        }
        DialogResult confirm = MessageBox.Show(
            $"カテゴリ '{target.DisplayName}' を削除します。よろしいですか？",
            "カテゴリ削除",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (confirm != DialogResult.OK)
        {
            return null;
        }
        return DeleteBrowserTabCategoriesCore([target], $"カテゴリを削除しました: {target.DisplayName}");
    }
    private bool DeleteActiveBrowserTabCategory()
    {
        EnsureBrowserTabCategoryConfiguration();
        BrowserTabCategoryDefinition? target = FindBrowserTabCategoryDefinition(_categoryViewState.ActiveCategoryId);
        return target != null && DeleteBrowserTabCategory(target) != null;
    }
    private string? DeleteBrowserTabCategories(IReadOnlyList<BrowserTabCategoryDefinition> categories)
    {
        List<BrowserTabCategoryDefinition> targets = categories
            .Where(category => category != null)
            .Select(category =>
                _categoryViewState.FirstOrDefault(existing => string.Equals(existing.Id, category.Id, StringComparison.OrdinalIgnoreCase)))
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
        CaptureActiveBrowserTabState();
        StoreActiveBrowserTabCategorySessionState(updateCompatibilityMirror: false);
        int activeCategoryIndexBeforeRemoval = _categoryViewState.FindIndex(
            category => string.Equals(category.Id, _categoryViewState.ActiveCategoryId, StringComparison.OrdinalIgnoreCase));
        HashSet<string> targetIds = targets
            .Select(target => target.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        _categoryViewState.RemoveAll(existing => targetIds.Contains(existing.Id));
        BrowserTabCategoryDefinition? recoveredCategory = null;
        if (_categoryViewState.Count == 0)
        {
            recoveredCategory = EnsureAtLeastOneBrowserTabCategoryAfterDeletion();
        }
        SyncBrowserTabCategoryDefinitionsToSettings();
        EnsureBrowserTabRestoreSnapshot();
        if ((_categoryViewState.ActiveCategoryId != null && targetIds.Contains(_categoryViewState.ActiveCategoryId)) || recoveredCategory != null)
        {
            int fallbackCategoryIndex = recoveredCategory != null
                ? 0
                : Math.Clamp(activeCategoryIndexBeforeRemoval, 0, _categoryViewState.Count - 1);
            string fallbackCategoryId = recoveredCategory?.Id
                ?? _categoryViewState.Categories[fallbackCategoryIndex].Id;
            List<BrowserTabState> targetTabs = LoadBrowserTabsForCategory(fallbackCategoryId);
            int targetIndex = Math.Clamp(ResolveBrowserTabCategoryActiveIndex(fallbackCategoryId, targetTabs.Count), 0, Math.Max(0, targetTabs.Count - 1));
            _browserTabViewState.Clear();
            _browserTabViewState.AddRange(targetTabs);
            _categoryViewState.ActiveCategoryId = fallbackCategoryId;
            _browserTabViewState.ContextTabIndex = -1;
            RefreshBrowserTabHeaders();
            if (_browserTabViewState.Count > 0)
            {
                _browserTabViewState.ActiveTabIndex = -1;
                SwitchBrowserTab(targetIndex);
            }
            else
            {
                _browserTabViewState.ActiveTabIndex = -1;
            }
            StoreActiveBrowserTabCategorySessionState(updateCompatibilityMirror: false);
        }
        else
        {
            RefreshBrowserTabHeaders();
        }
        SettingsManager.Save(_settings);
        ShowStatusMessage(successMessage);
        return successMessage;
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
        string currentPath = _navigationService.CurrentPath;
        BrowserTabState? activeState = _browserTabViewState.ActiveTabIndex >= 0 && _browserTabViewState.ActiveTabIndex < _browserTabViewState.Count
            ? _browserTabViewState.Tabs[_browserTabViewState.ActiveTabIndex]
            : null;
        bool isLocked = activeState?.IsLocked ?? false;
        return new BrowserTabState
        {
            Title = GetBrowserTabTitle(currentPath),
            CurrentPath = currentPath,
            IsLocked = isLocked,
            StartupPath = activeState?.StartupPath ?? string.Empty,
            IsReadOnly = activeState?.IsReadOnly ?? false,
            FilterLock = activeState?.FilterLock?.Clone() ?? new TabFilterLockState(),
            MarkedPaths = validateMarks && (activeState?.MarksDirty ?? false)
                ? CreatePersistableMarkedPaths(markSourceOverride ?? _markedFiles.Snapshot(), out _)
                : (markSourceOverride ?? _markedFiles.Snapshot()).ToList(),
            Navigation = _navigationService.CaptureState(),
            FocusTargetName = GetCurrentBrowserItem() is ListViewItem item ? GetItemFullName(item) : null,
            CursorIndex = _browserCursorIndex,
            ColumnCount = _columnCount,
            SortKind = _currentSort,
            SortAscending = _sortAscending
        };
    }
    private void CaptureActiveBrowserTabState(
        bool captureMarks = true,
        bool validateMarks = false,
        IReadOnlyList<string>? markSourceOverride = null,
        bool markValidationSucceeded = false)
    {
        if (_browserTabViewState.ActiveTabIndex < 0 || _browserTabViewState.ActiveTabIndex >= _browserTabViewState.Count)
        {
            return;
        }
        BrowserTabState currentState = _browserTabViewState.Tabs[_browserTabViewState.ActiveTabIndex];
        bool shouldValidateMarks = validateMarks && captureMarks && currentState.MarksDirty;
        BrowserTabState latestState = BuildBrowserTabStateFromCurrentUi(shouldValidateMarks, markSourceOverride);
        if (!currentState.IsLocked)
        {
            currentState.Title = latestState.Title;
        }
        currentState.CurrentPath = latestState.CurrentPath;
        currentState.Navigation = latestState.Navigation;
        currentState.FocusTargetName = latestState.FocusTargetName;
        currentState.CursorIndex = latestState.CursorIndex;
        currentState.ColumnCount = latestState.ColumnCount;
        currentState.SortKind = latestState.SortKind;
        currentState.SortAscending = latestState.SortAscending;
        currentState.IsLocked = latestState.IsLocked;
        // StartupPath の不変性を保護 (ロック中の場合は既存の StartupPath を優先)
        if (!currentState.IsLocked || string.IsNullOrWhiteSpace(currentState.StartupPath))
        {
            currentState.StartupPath = latestState.StartupPath;
        }
        currentState.IsReadOnly = latestState.IsReadOnly;
        currentState.FilterLock = latestState.FilterLock.Clone();
        if (captureMarks)
        {
            currentState.MarkedPaths = latestState.MarkedPaths;
            if (shouldValidateMarks || markValidationSucceeded)
            {
                currentState.MarksDirty = false;
            }
        }
    }
    private void RestoreMarksForBrowserTab(BrowserTabState state)
    {
        List<string> restoredMarks = CreatePersistableMarkedPaths(state.MarkedPaths, out int skippedCount);
        if (skippedCount > 0)
        {
            LogService.Info($"[BrowserTabs] Pruned stale per-tab marks. TabId={state.Id} Missing={skippedCount}");
        }
        state.MarkedPaths = restoredMarks;
        RestoreMarks(restoredMarks, invalidateRedo: false);
        state.MarksDirty = false;
        RefreshMarkUi();
    }
    private void RefreshBrowserTabHeaders()
    {
        bool vertical = IsVerticalBrowserTabLayout();
        _suppressBrowserTabSelectionChanged = true;
        try
        {
            if (!vertical)
            {
                _browserTabUiCoordinator.RefreshHeaders(
                    _browserTabViewState.Tabs,
                    _browserTabViewState.ActiveTabIndex,
                    _categoryViewState.Categories,
                    GetActiveBrowserTabCategoryIndex(),
                ShouldShowBrowserTabCategoryRow(),
                ref _lastBrowserTabHeaderSnapshotKey,
                BuildBrowserTabPresentation);
            }

            if (vertical && _browserTabNavigation != null)
            {
                string structureKey = BuildBrowserTabNavigationStructureKey();
                if (!string.Equals(_lastBrowserTabNavigationStructureKey, structureKey, StringComparison.Ordinal))
                {
                    List<BrowserTabNavigationCategoryItem> categories = BuildBrowserTabNavigationPresentationSnapshot();
                    _lastBrowserTabNavigationStructureKey = structureKey;
                    _browserTabNavigation.SetCategories(categories, GetActiveBrowserTabCategoryIndex());
                }
                _browserTabNavigation.UpdateSelection(GetActiveBrowserTabCategoryIndex(), _browserTabViewState.ActiveTabIndex);
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
        BrowserTabRestoreSnapshot snapshot = _settings.Session?.BrowserTabRestoreSnapshot ?? new BrowserTabRestoreSnapshot();
        string activeCategoryId = ResolveExistingBrowserTabCategoryId(_categoryViewState.ActiveCategoryId);
        var key = new StringBuilder();
        foreach (BrowserTabCategoryDefinition category in _categoryViewState.Categories)
        {
            key.Append(category.Id).Append('|').Append(category.DisplayName).Append('|');
            IEnumerable<BrowserTabState> activeTabs = string.Equals(category.Id, activeCategoryId, StringComparison.OrdinalIgnoreCase)
                ? _browserTabViewState.Tabs
                : Array.Empty<BrowserTabState>();
            if (activeTabs.Any())
            {
                foreach (BrowserTabState tab in activeTabs) key.Append(tab.Id).Append(';');
            }
            else
            {
                BrowserTabRestoreCategoryState? stored = snapshot.Categories.FirstOrDefault(item => string.Equals(item.Id, category.Id, StringComparison.OrdinalIgnoreCase));
                foreach (BrowserTabSessionState tab in stored?.OpenTabs ?? Enumerable.Empty<BrowserTabSessionState>()) key.Append(tab.TabId).Append('|').Append(tab.CurrentPath).Append(';');
            }
            key.Append('#');
        }
        return key.ToString();
    }
    private string GetBrowserTabTitle(string? path)
    {
        return BrowserTabPresentationHelper.BuildTabTitle(
            path,
            normalizedPath => QuickAccessService.FindAliasDisplayName(_quickAccessStore, normalizedPath));
    }

    private BrowserTabPresentationSnapshot BuildBrowserTabPresentation(BrowserTabState state)
    {
        return BrowserTabPresentationHelper.BuildPresentation(
            state,
            0,
            normalizedPath => QuickAccessService.FindAliasDisplayName(_quickAccessStore, normalizedPath));
    }
    private void RefreshActiveBrowserTabNavigationPathPresentation()
    {
        if (_browserTabNavigation == null || !IsVerticalBrowserTabLayout()) return;
        int categoryIndex = GetActiveBrowserTabCategoryIndex();
        int tabIndex = _browserTabViewState.ActiveTabIndex;
        if (categoryIndex < 0 || tabIndex < 0 || tabIndex >= _browserTabViewState.Tabs.Count) return;

        BrowserTabState state = _browserTabViewState.Tabs[tabIndex];
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
        int tabIndex = _browserTabViewState.ActiveTabIndex;
        if (tabIndex < 0 || tabIndex >= _browserTabViewState.Tabs.Count)
        {
            return;
        }

        BrowserTabState state = _browserTabViewState.Tabs[tabIndex];
        bool vertical = IsVerticalBrowserTabLayout();
        if (!vertical)
        {
            _browserTabUiCoordinator.UpdateTabPresentation(
                state,
                tabIndex,
                synchronizeSelection,
                BuildBrowserTabPresentation);
            return;
        }

        if (_browserTabNavigation == null)
        {
            return;
        }

        int categoryIndex = GetActiveBrowserTabCategoryIndex();
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
                _browserTabNavigation?.UpdateSelection(GetActiveBrowserTabCategoryIndex(), tabIndex);
                return;
            }

            if (_browserTabStrip != null && tabIndex >= 0 && tabIndex < _browserTabViewState.Count)
            {
                BrowserTabState state = _browserTabViewState.Tabs[tabIndex];
                BrowserTabPresentationSnapshot presentation = BuildBrowserTabPresentation(state);
                _browserTabStrip.UpdateTabAndSelection(
                    tabIndex,
                    new BrowserTabStripItem(
                        presentation.HeaderText,
                        presentation.ToolTipText,
                        presentation.CanonicalPath,
                        presentation.PrefixText,
                        presentation.BaseTitle,
                        presentation.RelativeSuffix),
                    select: true);
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
        string activeCategoryId = ResolveExistingBrowserTabCategoryId(_categoryViewState.ActiveCategoryId);
        for (int categoryIndex = 0; categoryIndex < _categoryViewState.Categories.Count; categoryIndex++)
        {
            BrowserTabCategoryDefinition category = _categoryViewState.Categories[categoryIndex];
            IReadOnlyList<BrowserTabState> tabs = string.Equals(category.Id, activeCategoryId, StringComparison.OrdinalIgnoreCase)
                ? _browserTabViewState.Tabs
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
    private void FocusBrowserFileList()
    {
        if (_browserFileListFocusPending || IsDisposed || !IsHandleCreated)
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
    private bool CreateNewBrowserTab(string? initialPath = null, bool showStatusMessage = true, bool useConfiguredInsertion = false)
    {
        if (GuardClipboardBusy())
        {
            return false;
        }
        int maxTabCount = GetMaxBrowserTabsPerCategory();
        if (_browserTabViewState.Count >= maxTabCount)
        {
            ShowStatusMessage($"タブは最大{maxTabCount}個までです。");
            _browserTabStrip?.FlashLimitReached();
            TryPlayBrowserTabLimitBeep();
            return false;
        }
        CaptureActiveBrowserTabState();
        string categoryId = ResolveExistingBrowserTabCategoryId(_categoryViewState.ActiveCategoryId);
        BrowserTabState newState = useConfiguredInsertion
            ? CreateInitialBrowserTabStateForCategory(categoryId)
            : BuildBrowserTabStateFromCurrentUi();
        newState.IsLocked = false;
        newState.StartupPath = string.Empty;
        newState.IsReadOnly = false;
        newState.MarkedPaths = new List<string>();
        if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath))
        {
            newState.CurrentPath = initialPath;
            newState.Navigation = new NavigationService.NavigationSnapshot
            {
                CurrentPath = initialPath,
                BackHistory = Array.Empty<string>(),
                ForwardHistory = Array.Empty<string>(),
                LastVisitedPathByDrive = new Dictionary<char, string>()
            };
            newState.FocusTargetName = null;
            newState.CursorIndex = 0;
            newState.Title = GetBrowserTabTitle(initialPath);
        }
        int newIndex = useConfiguredInsertion
            ? ResolveNewBrowserTabInsertIndex(_browserTabViewState.Count, _browserTabViewState.ActiveTabIndex, _settings.BrowserTabs.NewTabPosition)
            : _browserTabViewState.Count;
        _browserTabViewState.Insert(newIndex, newState);
        RefreshBrowserTabHeaders();
        _browserTabViewState.ActiveTabIndex = -1;
        SwitchBrowserTab(newIndex);
        if (showStatusMessage)
        {
            ShowStatusMessage("新しいタブを作成しました。");
        }
        return true;
    }
    private void RefreshAllBrowserTabTitles()
    {
        foreach (BrowserTabState state in _browserTabViewState.Tabs)
        {
            BrowserTabPresentationSnapshot presentation = BuildBrowserTabPresentation(state);
            state.Title = presentation.AliasTitle ?? presentation.DisplayCore;
        }
        RefreshBrowserTabHeaders();
    }
    private bool IsActiveBrowserTabLocked()
    {
        return _browserTabViewState.ActiveTabIndex >= 0
            && _browserTabViewState.ActiveTabIndex < _browserTabViewState.Count
            && _browserTabViewState.Tabs[_browserTabViewState.ActiveTabIndex].IsLocked;
    }
    private int GetMaxBrowserTabsPerCategory()
    {
        int configuredMax = _settings.BrowserTabs?.MaxTabsPerCategory ?? BrowserTabSettings.DefaultMaxTabsPerCategory;
        return Math.Clamp(configuredMax, 1, BrowserTabSettings.SafetyMaxTabsPerCategory);
    }
    private bool IsActiveBrowserTabReadOnly()
    {
        return _browserTabViewState.ActiveTabIndex >= 0
            && _browserTabViewState.ActiveTabIndex < _browserTabViewState.Count
            && _browserTabViewState.Tabs[_browserTabViewState.ActiveTabIndex].IsReadOnly;
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
    private void ToggleActiveBrowserTabLock()
    {
        ToggleBrowserTabLock(_browserTabViewState.ActiveTabIndex);
    }
    private void ToggleActiveBrowserTabReadOnly()
    {
        ToggleBrowserTabReadOnly(_browserTabViewState.ActiveTabIndex);
    }
    private TabFilterLockState GetActiveTabFilterLock()
    {
        if (_browserTabViewState.ActiveTabIndex < 0 || _browserTabViewState.ActiveTabIndex >= _browserTabViewState.Count)
        {
            return TabFilterLockState.Disabled();
        }
        return _browserTabViewState.Tabs[_browserTabViewState.ActiveTabIndex].FilterLock;
    }
    private bool HasActiveTabFilterLock()
    {
        var lockState = GetActiveTabFilterLock();
        return lockState.Enabled && lockState.HasAnyCondition;
    }
    private void OpenActiveTabFilterLockDialog()
    {
        OpenTabFilterLockDialog(_browserTabViewState.ActiveTabIndex);
    }
    private void OpenTabFilterLockDialog(int tabIndex)
    {
        if (tabIndex < 0 || tabIndex >= _browserTabViewState.Count) return;
        var tab = _browserTabViewState.Tabs[tabIndex];
        using var dialog = new TabFilterLockDialog(tab.FilterLock);
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            tab.FilterLock = dialog.ResultState;
            if (tabIndex == _browserTabViewState.ActiveTabIndex)
            {
                ExecuteCurrentDirectoryReloadCommand();
            }
            else
            {
                _browserTabStrip?.Invalidate();
            }
        }
    }
    private void ClearActiveTabFilterLock()
    {
        ClearTabFilterLock(_browserTabViewState.ActiveTabIndex);
    }
    private void ClearTabFilterLock(int tabIndex)
    {
        if (tabIndex < 0 || tabIndex >= _browserTabViewState.Count) return;
        var tab = _browserTabViewState.Tabs[tabIndex];
        tab.FilterLock = TabFilterLockState.Disabled();
        if (tabIndex == _browserTabViewState.ActiveTabIndex)
        {
            ExecuteCurrentDirectoryReloadCommand();
        }
        else
        {
            _browserTabStrip?.Invalidate();
        }
    }
    private void ToggleBrowserTabLock(int tabIndex, bool showStatusMessage = true)
    {
        if (tabIndex < 0 || tabIndex >= _browserTabViewState.Count)
        {
            return;
        }
        if (_browserTabViewState.ActiveTabIndex != tabIndex)
        {
            SwitchBrowserTab(tabIndex);
        }
        BrowserTabState state = _browserTabViewState.Tabs[tabIndex];
        state.IsLocked = !state.IsLocked;
        if (state.IsLocked)
        {
            if (string.IsNullOrWhiteSpace(state.StartupPath))
            {
                string rawPath = Directory.Exists(_navigationService.CurrentPath)
                    ? _navigationService.CurrentPath
                    : state.CurrentPath;
                state.StartupPath = _navigationService.NormalizeDestinationDirectory(rawPath);
            }
        }
        else
        {
            state.StartupPath = string.Empty;
        }
        RefreshBrowserTabHeaders();
        if (showStatusMessage)
        {
            ShowStatusMessage(state.IsLocked
                ? "現在のタブを固定しました。"
                : "現在のタブ固定を解除しました。");
        }
    }
    private void ToggleBrowserTabReadOnly(int tabIndex, bool showStatusMessage = true)
    {
        if (tabIndex < 0 || tabIndex >= _browserTabViewState.Count)
        {
            return;
        }
        if (_browserTabViewState.ActiveTabIndex != tabIndex)
        {
            SwitchBrowserTab(tabIndex);
        }
        BrowserTabState state = _browserTabViewState.Tabs[tabIndex];
        state.IsReadOnly = !state.IsReadOnly;
        RefreshBrowserTabHeaders();
        if (showStatusMessage)
        {
            ShowStatusMessage(state.IsReadOnly
                ? "現在のタブを ReadOnly にしました。"
                : "現在のタブの ReadOnly を解除しました。");
        }
    }
    private bool PrepareUnlockedTabForLocationChange(string? targetPath = null)
    {
        if (!IsActiveBrowserTabLocked())
        {
            return true;
        }
        if (!string.IsNullOrWhiteSpace(targetPath) && QuickAccessService.PathsEqual(targetPath, _navigationService.CurrentPath))
        {
            return true;
        }
        if (!string.IsNullOrWhiteSpace(targetPath)
            && _browserTabViewState.ActiveTabIndex >= 0
            && _browserTabViewState.ActiveTabIndex < _browserTabViewState.Count
            && IsPathUnderBrowserTabStartupPath(targetPath, _browserTabViewState.Tabs[_browserTabViewState.ActiveTabIndex]))
        {
            return true;
        }
        if (!CreateNewBrowserTab(showStatusMessage: false))
        {
            return false;
        }
        ShowStatusMessage("固定タブから派生タブを作成しました。");
        return true;
    }
    private static bool IsPathUnderBrowserTabStartupPath(string targetPath, BrowserTabState state)
    {
        string startupPath = state.StartupPath;
        if (string.IsNullOrWhiteSpace(startupPath) || !Directory.Exists(startupPath))
        {
            startupPath = state.CurrentPath;
        }
        if (string.IsNullOrWhiteSpace(startupPath))
        {
            return false;
        }
        try
        {
            string normalizedStartup = BrowserTabPresentationHelper.EnsureTrailingDirectorySeparator(Path.GetFullPath(startupPath));
            string normalizedTarget = Path.GetFullPath(targetPath);
            return normalizedTarget.StartsWith(normalizedStartup, StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    normalizedTarget.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    normalizedStartup.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
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
    private bool TryCloseBrowserTab(int tabIndex, bool showStatusMessage = true)
    {
        if (GuardClipboardBusy())
        {
            return false;
        }
        if (tabIndex < 0 || tabIndex >= _browserTabViewState.Count)
        {
            return false;
        }
        if (_browserTabViewState.Tabs[tabIndex].IsLocked)
        {
            if (showStatusMessage)
            {
                ShowStatusMessage("固定タブは閉じられません。先に固定を解除してください。");
            }
            return false;
        }
        if (_browserTabViewState.Count == 1)
        {
            BrowserTabCategoryDefinition? activeCategory = FindBrowserTabCategoryDefinition(_categoryViewState.ActiveCategoryId);
            if (activeCategory != null
                && !string.Equals(activeCategory.Id, BrowserTabSettings.DefaultCategoryId, StringComparison.OrdinalIgnoreCase))
            {
                DialogResult confirm = MessageBox.Show(
                    $"このタブを閉じると、カテゴリ「{activeCategory.DisplayName}」も削除されます。\nカテゴリごと削除しますか？",
                    "タブを閉じる",
                    MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (confirm != DialogResult.OK)
                {
                    return false;
                }
                return DeleteBrowserTabCategoriesCore([activeCategory], $"カテゴリを削除しました: {activeCategory.DisplayName}") != null;
            }
            if (showStatusMessage)
            {
                ShowStatusMessage("最後のタブは閉じられません。");
            }
            return false;
        }
        if (_browserTabViewState.ActiveTabIndex != tabIndex)
        {
            SwitchBrowserTab(tabIndex);
        }
        int closingIndex = tabIndex;
        PushClosedBrowserTabSnapshot(closingIndex);
        _browserTabViewState.RemoveAt(closingIndex);
        int targetIndex = Math.Clamp(closingIndex - 1, 0, _browserTabViewState.Count - 1);
        RefreshBrowserTabHeaders();
        _browserTabViewState.ActiveTabIndex = -1;
        SwitchBrowserTab(targetIndex);
        if (showStatusMessage)
        {
            ShowStatusMessage("タブを閉じました。");
        }
        return true;
    }
    private void CloseCurrentBrowserTab()
    {
        TryCloseBrowserTab(_browserTabViewState.ActiveTabIndex);
    }
    private bool CloseBrowserTabRange(IReadOnlyList<int> tabIndices, int preferredTabIndex, string successMessage, string nothingToCloseMessage)
    {
        if (GuardClipboardBusy())
        {
            return false;
        }
        if (preferredTabIndex < 0 || preferredTabIndex >= _browserTabViewState.Count)
        {
            return false;
        }
        var closableIndices = tabIndices
            .Distinct()
            .Where(index => index >= 0 && index < _browserTabViewState.Count && !_browserTabViewState.Tabs[index].IsLocked)
            .OrderByDescending(index => index)
            .ToList();
        if (closableIndices.Count == 0)
        {
            ShowStatusMessage(nothingToCloseMessage);
            return false;
        }
        BrowserTabState preferredTab = _browserTabViewState.Tabs[preferredTabIndex];
        if (_browserTabViewState.ActiveTabIndex != preferredTabIndex)
        {
            SwitchBrowserTab(preferredTabIndex);
        }
        foreach (int index in closableIndices)
        {
            _browserTabViewState.RemoveAt(index);
        }
        RefreshBrowserTabHeaders();
        int targetIndex = _browserTabViewState.IndexOf(preferredTab);
        if (targetIndex < 0)
        {
            targetIndex = Math.Clamp(preferredTabIndex, 0, _browserTabViewState.Count - 1);
        }
        _browserTabViewState.ActiveTabIndex = -1;
        SwitchBrowserTab(targetIndex);
        ShowStatusMessage(successMessage);
        return true;
    }
    private void CloseBrowserTabsToRight(int tabIndex)
    {
        var tabIndices = Enumerable.Range(tabIndex + 1, Math.Max(0, _browserTabViewState.Count - tabIndex - 1)).ToList();
        CloseBrowserTabRange(tabIndices, tabIndex, "右側のタブを閉じました。", "閉じられる右側タブはありません。");
    }
    private void CloseBrowserTabsToLeft(int tabIndex)
    {
        var tabIndices = Enumerable.Range(0, Math.Max(0, tabIndex)).ToList();
        CloseBrowserTabRange(tabIndices, tabIndex, "左側のタブを閉じました。", "閉じられる左側タブはありません。");
    }
    private void CloseOtherBrowserTabs(int tabIndex)
    {
        var tabIndices = Enumerable.Range(0, _browserTabViewState.Count)
            .Where(index => index != tabIndex)
            .ToList();
        CloseBrowserTabRange(tabIndices, tabIndex, "このタブ以外を閉じました。", "閉じられる他タブはありません。");
    }
    private int CountClosableBrowserTabs(Func<int, bool> predicate)
    {
        int count = 0;
        for (int i = 0; i < _browserTabViewState.Count; i++)
        {
            if (predicate(i) && !_browserTabViewState.Tabs[i].IsLocked)
            {
                count++;
            }
        }
        return count;
    }
    private void BrowserTabStrip_TabDoubleClicked(object? sender, BrowserTabStripMouseEventArgs e)
    {
        ExecuteCommandFromUi(
            CommandIds.BrowserTabLock,
            CommandScope.Browser,
            "BrowserTab.DoubleClick",
            contextTabIndex: e.TabIndex);
    }
    private void BrowserTabStrip_TabRightClicked(object? sender, BrowserTabStripMouseEventArgs e)
    {
        if (_browserTabStrip == null || e.TabIndex < 0 || e.TabIndex >= _browserTabViewState.Count)
        {
            return;
        }
        ClearBrowserTabContextState();
        _browserTabViewState.ContextTabIndex = e.TabIndex;
        EnsureBrowserTabContextMenu();
        UpdateBrowserTabContextMenuItems(e.TabIndex);
        Control owner = sender as Control ?? _browserTabStrip ?? (Control)this;
        _browserTabContextMenu?.Show(owner, e.Location);
    }
    private bool SwitchBrowserTab(int newIndex)
    {
        HideBrowserFileNameToolTip();
        EnsureBrowserModeBeforeWorkspaceNavigation();
        if (_isSwitchingBrowserTab || newIndex < 0 || newIndex >= _browserTabViewState.Count)
        {
            return false;
        }
        if (newIndex == _browserTabViewState.ActiveTabIndex)
        {
            FocusBrowserFileList();
            return true;
        }
        CaptureActiveBrowserTabState(validateMarks: true);
        int previousActiveTabIndex = _browserTabViewState.ActiveTabIndex;
        BrowserTabState state = _browserTabViewState.Tabs[newIndex];
        string targetPath = state.CurrentPath;
        if (string.IsNullOrWhiteSpace(targetPath) || !Directory.Exists(targetPath))
        {
            targetPath = Directory.Exists(_navigationService.CurrentPath)
                ? _navigationService.CurrentPath
                : Environment.CurrentDirectory;
        }

        BrowserLoadCoordinator.DirectoryLoadResult? preparedLoad = PrepareBrowserTabSwitchDirectoryLoad(state, targetPath);
        if (preparedLoad == null)
        {
            RestoreBrowserTabSelectionAfterFailedSwitch(previousActiveTabIndex);
            return false;
        }

        _isSwitchingBrowserTab = true;
        try
        {
            // Commit begins only after directory enumeration, sorting and UI
            // item materialization have all succeeded.
            _browserTabViewState.ActiveTabIndex = newIndex;
            _columnCount = Math.Clamp(state.ColumnCount, 1, 9);
            _currentSort = state.SortKind;
            _sortAscending = state.SortAscending;
            _navigationService.RestoreState(state.Navigation);
            RestoreMarksForBrowserTab(state);
            CommitPreparedBrowserTabSwitchDirectoryLoad(
                preparedLoad,
                () =>
                {
                    ApplyVisibleBrowserTabSelection(newIndex);
                    FocusBrowserFileList();
                });
        }
        finally
        {
            _isSwitchingBrowserTab = false;
        }
        return true;
    }

    private void BrowserTabStrip_SelectedTabReclicked(object? sender, BrowserTabStripMouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && e.TabIndex == _browserTabViewState.ActiveTabIndex)
        {
            FocusBrowserFileList();
        }
    }

    internal static int ResolveNewBrowserTabInsertIndex(int tabCount, int activeTabIndex, BrowserTabNewPosition position)
    {
        int safeTabCount = Math.Max(0, tabCount);
        if (position == BrowserTabNewPosition.End)
        {
            return safeTabCount;
        }

        return activeTabIndex >= 0 && activeTabIndex < safeTabCount
            ? activeTabIndex + 1
            : safeTabCount;
    }

    private void RestoreBrowserTabSelectionAfterFailedSwitch(int activeTabIndex)
    {
        _suppressBrowserTabSelectionChanged = true;
        try
        {
            if (IsVerticalBrowserTabLayout())
            {
                _browserTabNavigation?.UpdateSelection(GetActiveBrowserTabCategoryIndex(), activeTabIndex);
            }
            else if (_browserTabStrip != null)
            {
                _browserTabStrip.SelectedIndex = activeTabIndex;
            }
        }
        finally
        {
            _suppressBrowserTabSelectionChanged = false;
        }
    }
    private void SelectAdjacentBrowserTab(int delta, bool wrap = true)
    {
        if (GuardClipboardBusy())
        {
            return;
        }
        if (_browserTabViewState.Count <= 1)
        {
            return;
        }
        int nextIndex = _browserTabViewState.ActiveTabIndex + delta;
        if (wrap)
        {
            nextIndex = (nextIndex + _browserTabViewState.Count) % _browserTabViewState.Count;
        }
        else if (nextIndex < 0 || nextIndex >= _browserTabViewState.Count)
        {
            return;
        }
        SwitchBrowserTab(nextIndex);
    }
    private string? GetActiveBrowserTabLockRootPath()
    {
        if (_browserTabViewState.ActiveTabIndex < 0 || _browserTabViewState.ActiveTabIndex >= _browserTabViewState.Count)
        {
            return null;
        }
        BrowserTabState state = _browserTabViewState.Tabs[_browserTabViewState.ActiveTabIndex];
        if (!state.IsLocked || string.IsNullOrWhiteSpace(state.StartupPath))
        {
            return null;
        }
        return state.StartupPath;
    }
}
