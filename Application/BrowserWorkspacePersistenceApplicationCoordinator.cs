using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MidFD.Configuration;
using MidFD.Helpers;
using MidFD.Models;
using MidFD.Services;
using MidFD.Services.Workspace;

namespace MidFD.Runtime;

/// <summary>
/// Browser/Workspace の設定・session・mark・state-store persistence を所有する。
/// UI は保存前のplain stateを渡し、保存結果だけを受け取る。
/// </summary>
internal sealed class BrowserWorkspacePersistenceApplicationCoordinator
{
    private readonly BrowserApplicationCoordinator _browser;
    private readonly SettingsApplicationCoordinator _settings;
    private readonly BrowserTabGroupRuntimeState? _tabGroups;
    private readonly MarkPersistenceBoundaryCoordinator _markPersistence = new();

    public BrowserWorkspacePersistenceApplicationCoordinator(
        BrowserApplicationCoordinator browser,
        SettingsApplicationCoordinator settings,
        BrowserTabGroupRuntimeState? tabGroups = null)
    {
        _browser = browser;
        _settings = settings;
        _tabGroups = tabGroups;
    }

    public void InitializeStores()
    {
        _browser.Workspace.SetStateStore(WorkspaceStateStoreFactory.CreateDefault());
        _browser.Workspace.SetSnapshotStorage(
            new WorkspaceSnapshotStorage(WorkspaceStateStoreFactory.GetDefaultDbPath()));
        _settings.InitializeManagedTrash();
    }

    public void InitializeWorkspaceSettings()
    {
        EnsureConfiguration();
        SyncActiveCategory();
        _browser.Workspace.SetQuickAccess(QuickAccessService.LoadOrMigrate(_settings.Value.QuickAccess));
    }

    public void SaveSettings() => _settings.Save();

    public bool SetBrowserTabLayout(BrowserTabLayoutMode mode)
    {
        EnsureNormalized();
        if (_settings.Value.BrowserTabs.LayoutMode == mode)
        {
            return false;
        }

        _settings.SetBrowserTabLayoutMode(mode);
        SaveSettings();
        return true;
    }

    public int SetBrowserTabNavigationWidth(int width)
    {
        int normalizedWidth = Math.Clamp(width, 120, 600);
        EnsureNormalized();
        _settings.SetBrowserTabNavigationWidth(normalizedWidth);
        SaveSettings();
        return normalizedWidth;
    }

    public bool ShouldRestoreWorkspace =>
        SessionRestorePolicy.ShouldRestoreStartupWorkspace(_settings.Value.Session);

    public bool ShouldPreservePendingMarks =>
        _settings.Value.Session.PersistMarksAcrossRestart || ShouldRestoreWorkspace;

    public bool PersistMarksAcrossRestart => _settings.Value.Session.PersistMarksAcrossRestart;

    public int GetPersistedMarkedPathCount() => _settings.Value.Session.PersistedMarkedPaths?
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count() ?? 0;

    public void EnsureConfiguration() =>
        _settings.EnsureBrowserWorkspaceCategoryConfiguration(_browser.Workspace);

    public void EnsureNormalized() => _settings.EnsureNormalized();

    public void SyncActiveCategory() =>
        _settings.SyncActiveBrowserWorkspaceCategory(_browser.Workspace);

    public BrowserTabRestoreSnapshot EnsureRestoreSnapshot() =>
        _settings.EnsureBrowserWorkspaceRestoreSnapshot(_browser.Workspace);

    public void CaptureBrowserTabGroupsToRestoreSnapshot()
    {
        if (_tabGroups == null) return;
        BrowserTabRestoreSnapshot snapshot = EnsureRestoreSnapshot();
        snapshot.UserTabGroups = BrowserTabGroupPersistenceMapper.Capture(_tabGroups.Groups);
    }

    public WorkspaceState CaptureCurrentWorkspaceState()
    {
        CaptureBrowserTabGroupsToRestoreSnapshot();
        return _browser.Workspace.CaptureWorkspaceState(_settings.Value.Session);
    }

    public void RestoreBrowserTabGroups(BrowserTabRestoreSnapshot snapshot)
    {
        _tabGroups?.Restore(BrowserTabGroupPersistenceMapper.NormalizeForRestore(snapshot));
    }

    public BrowserTabRestoreCategoryState? FindStoredCategory(string categoryId) =>
        _settings.FindBrowserWorkspaceRestoreCategoryState(_browser.Workspace, categoryId);

    public IReadOnlyList<BrowserTabSessionState> GetSessionTabsForRestore(
        string categoryId,
        out string resolvedCategoryId) =>
        _settings.GetBrowserWorkspaceSessionTabsForRestore(
            _browser.Workspace,
            categoryId,
            out resolvedCategoryId);

    public int ResolveActiveTabIndex(string categoryId, int restoredTabCount) =>
        _settings.ResolveBrowserWorkspaceActiveTabIndex(
            _browser.Workspace,
            categoryId,
            restoredTabCount);

    public void InitializeMarkSlots(int slotCount) =>
        _browser.Workspace.SetMarkSlots(MarkSlotStorage.Load(slotCount));

    public MarkSlotEntry GetMarkSlot(int slotNumber) =>
        _browser.Workspace.GetMarkSlotSnapshot(slotNumber);

    public MarkSlotStore GetMarkSlots() =>
        _browser.Workspace.MarkSlotsSnapshot;

    public bool ReplaceMarkSlots(MarkSlotStore store, int slotCount)
    {
        _browser.Workspace.SetMarkSlots(store);
        return MarkSlotStorage.Save(_browser.Workspace.MarkSlotsSnapshot, slotCount);
    }

    public bool SaveMarkSlots(MarkSlotEntry? changedSlot, int slotCount)
    {
        if (changedSlot != null)
        {
            _browser.Workspace.UpdateMarkSlot(changedSlot);
        }

        return MarkSlotStorage.Save(_browser.Workspace.MarkSlotsSnapshot, slotCount);
    }

    public BrowserMarkPersistenceTransition ClearCategoryMarks(string categoryId)
    {
        BrowserMarksClearTransition clear = _settings.ClearBrowserCategoryMarks(_browser.Workspace, categoryId);
        return clear.Changed
            ? new BrowserMarkPersistenceTransition(clear, SaveMarkMutationState())
            : new BrowserMarkPersistenceTransition(clear, true);
    }

    public BrowserMarkPersistenceTransition ClearAllMarks()
    {
        BrowserMarksClearTransition clear = _settings.ClearAllBrowserMarks(_browser.Workspace);
        return clear.Changed
            ? new BrowserMarkPersistenceTransition(clear, SaveMarkMutationState())
            : new BrowserMarkPersistenceTransition(clear, true);
    }

    public BrowserMarkPersistenceTransition PersistAfterMarkMutation()
    {
        return new BrowserMarkPersistenceTransition(
            new BrowserMarksClearTransition(false, 0),
            SaveMarkMutationState());
    }

    public BrowserWorkspaceMarkPersistencePlan PrepareMarkPersistence(
        bool activeMarksDirty,
        IReadOnlyList<string> runtimeMarks,
        IReadOnlyList<string>? pendingEscMarks)
    {
        MarkPersistencePreparation preparation = _markPersistence.Prepare(
            activeMarksDirty,
            runtimeMarks,
            pendingEscMarks);
        return new(
            preparation,
            activeMarksDirty || preparation.UsedPendingEscSnapshot);
    }

    public BrowserStartupLegacyMarkRestoreResult ApplyStartupLegacyMarks(
        bool restoreEnabled,
        bool restoredTabs,
        bool workspaceStoreLoaded,
        IReadOnlyList<BrowserTabState> restoredTabStates)
    {
        if (restoreEnabled && restoredTabs &&
            (workspaceStoreLoaded || restoredTabStates.Any(tab => tab.MarkedPaths.Count > 0)))
        {
            return BrowserStartupLegacyMarkRestoreResult.SkippedByWorkspaceAuthority;
        }

        EnsureNormalized();
        if (!_settings.Value.Session.PersistMarksAcrossRestart)
        {
            return new BrowserStartupLegacyMarkRestoreResult(
                BrowserStartupLegacyMarkRestoreKind.SkippedDisabled,
                Array.Empty<string>(),
                0);
        }

        var savedPaths = _settings.Value.Session.PersistedMarkedPaths ?? new List<string>();
        if (savedPaths.Count == 0)
        {
            return new BrowserStartupLegacyMarkRestoreResult(
                BrowserStartupLegacyMarkRestoreKind.SkippedEmpty,
                Array.Empty<string>(),
                0);
        }

        var restoredMarkKinds = new List<MarkPathKind>();
        int skippedCount = 0;
        foreach (string path in savedPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (TryGetPathKind(path, out MarkPathKind markKind))
            {
                restoredMarkKinds.Add(markKind);
            }
            else
            {
                skippedCount++;
            }
        }

        List<string> restoredPaths = restoredMarkKinds
            .Select(static markKind => markKind.Path)
            .ToList();
        if (restoredPaths.Count == 0)
        {
            return new BrowserStartupLegacyMarkRestoreResult(
                BrowserStartupLegacyMarkRestoreKind.SkippedMissing,
                Array.Empty<string>(),
                skippedCount);
        }

        _browser.RestoreMarksWithKnownKinds(restoredMarkKinds);
        return new BrowserStartupLegacyMarkRestoreResult(
            BrowserStartupLegacyMarkRestoreKind.Restored,
            restoredPaths,
            skippedCount);
    }

    public BrowserWorkspaceSessionSaveResult SaveSession(
        string currentPath,
        int columnCount,
        SortKind sortKind,
        bool sortAscending,
        BrowserTabState? activeState,
        IReadOnlyList<string>? pendingEscMarks)
    {
        int? activeTabIndex = _browser.Workspace.ActiveTabIndex >= 0
            ? _browser.Workspace.ActiveTabIndex
            : null;
        IReadOnlyList<int> dirtyTabIndicesBeforeSave = _browser.Workspace.TabStates
            .Select((tab, index) => (tab, index))
            .Where(static item => item.tab.MarksDirty)
            .Select(static item => item.index)
            .ToList();
        BrowserTabState? currentActiveState = activeTabIndex.HasValue && activeState != null
            ? _browser.Workspace.GetTabSnapshot(activeTabIndex.Value)
            : null;
        BrowserWorkspaceMarkPersistencePlan markPlan = PrepareMarkPersistence(
            currentActiveState?.MarksDirty ?? false,
            _browser.Selection.Snapshot(),
            pendingEscMarks);
        if (activeTabIndex.HasValue && activeState != null && currentActiveState != null)
        {
            BrowserTabState stateForPersistence = activeState.Clone();
            stateForPersistence.MarkedPaths = markPlan.Preparation.MarkedPaths.ToList();
            _browser.Workspace.ApplyCapturedState(
                activeTabIndex.Value,
                stateForPersistence,
                captureMarks: true,
                shouldValidateMarks: false,
                markValidationSucceeded: markPlan.Preparation.ValidationCount == 1);
        }

        if (!ShouldRestoreWorkspace)
        {
            SavePersistedMarksToSettings(
                markPlan.Preparation.MarkedPaths,
                markPlan.Preparation.UsedPendingEscSnapshot);
        }

        _settings.SetLastPath(currentPath);
        SaveBrowserTabsToSettings();
        bool workspaceSaveSucceeded = SaveWorkspaceStateStore();
        _settings.SetSessionListState(columnCount, sortKind, sortAscending);
        SettingsSqliteStore.SettingsSaveResult settingsSaveResult = _settings.TrySave();
        bool markPersistenceSucceeded = workspaceSaveSucceeded && settingsSaveResult.Succeeded;

        if (activeTabIndex.HasValue)
        {
            _browser.Workspace.SetTabMarksDirty(
                activeTabIndex.Value,
                _markPersistence.ShouldRemainDirty(
                    markPlan.ActiveMarksWereDirty,
                    markPlan.Preparation.ValidationCount,
                    markPersistenceSucceeded));
        }

        if (!markPersistenceSucceeded)
        {
            foreach (int dirtyTabIndex in dirtyTabIndicesBeforeSave)
            {
                _browser.Workspace.SetTabMarksDirty(dirtyTabIndex, true);
            }
        }

        return new BrowserWorkspaceSessionSaveResult(
            workspaceSaveSucceeded,
            settingsSaveResult,
            markPersistenceSucceeded,
            markPlan.Preparation,
            markPlan.ActiveMarksWereDirty);
    }

    public void StoreActiveCategorySessionState(bool updateCompatibilityMirror)
    {
        BrowserTabSessionSerializationResult result = _settings.StoreActiveBrowserWorkspaceCategorySessionState(
            _browser.Workspace,
            updateCompatibilityMirror);
        LogService.Info(
            $"[BrowserTabCategory] Store Category={_browser.Workspace.ActiveCategoryId} " +
            $"Tabs={result.Tabs.Count} ActiveIndex={result.ActiveTabIndex} MirrorUpdated={updateCompatibilityMirror}");
    }

    public void SaveBrowserTabsToSettings()
    {
        EnsureConfiguration();
        EnsureNormalized();
        if (!_settings.Value.Session.RestoreTabsOnStartup)
        {
            _settings.ClearBrowserTabRestoreState();
            LogService.Info("[BrowserTabs] Save cleared because tab restore is disabled.");
            return;
        }

        StoreActiveCategorySessionState(updateCompatibilityMirror: false);
        CaptureBrowserTabGroupsToRestoreSnapshot();
        string activeCategoryId = _browser.Workspace.ResolveCategoryId(_browser.Workspace.ActiveCategoryId);
        BrowserTabRestoreCategoryState? activeCategoryState = FindStoredCategory(activeCategoryId);
        LogService.Info(
            $"[BrowserTabs] Saved Category={activeCategoryId} " +
            $"Tabs={activeCategoryState?.OpenTabs.Count ?? 0} ActiveIndex={activeCategoryState?.ActiveTabIndex ?? 0}");
    }

    public bool SaveWorkspaceStateStore()
    {
        IWorkspaceStateStore? stateStore = _browser.Workspace.StateStore;
        if (stateStore == null)
        {
            return false;
        }

        if (stateStore is NoOpWorkspaceStateStore)
        {
            LogService.Warn("[WorkspaceStore] Save unavailable; NoOp store did not persist workspace state.");
            return false;
        }

        try
        {
            if (!_settings.Value.Session.RestoreTabsOnStartup)
            {
                stateStore.Clear();
                LogService.Info("[WorkspaceStore] Cleared because workspace restore is disabled.");
                return true;
            }

            CaptureBrowserTabGroupsToRestoreSnapshot();
            BrowserTabRestoreSnapshot snapshot = EnsureRestoreSnapshot().Clone();
            stateStore.Save(WorkspaceStateMigrationService.FromSessionSnapshot(snapshot));
            LogService.Info($"[WorkspaceStore] Saved categories={snapshot.Categories.Count} active={snapshot.ActiveCategoryId}");
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("Workspace state save failed. Session snapshot fallback remains available.", ex);
            return false;
        }
    }

    public BrowserWorkspaceRuntimeStateSnapshot CaptureRuntimeState()
    {
        CaptureBrowserTabGroupsToRestoreSnapshot();
        return _settings.CaptureBrowserWorkspaceRuntimeState(_browser.Workspace);
    }

    public void ApplyRuntimeState(BrowserWorkspaceRuntimeStateSnapshot runtimeState)
    {
        _settings.ApplyBrowserWorkspaceRestoreState(_browser.Workspace, runtimeState);
        RestoreBrowserTabGroups(runtimeState.RestoreSnapshot);
    }

    public bool TryLoadWorkspaceStateStore(out BrowserTabRestoreSnapshot? snapshot)
    {
        snapshot = null;
        IWorkspaceStateStore? stateStore = _browser.Workspace.StateStore;
        if (stateStore == null)
        {
            return false;
        }

        try
        {
            WorkspaceState? workspaceState = stateStore.Load();
            if (workspaceState?.RestoreSnapshot?.Categories is not { Count: > 0 })
            {
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

    public void ApplyWorkspaceRestoreSnapshot(BrowserTabRestoreSnapshot snapshot) =>
        ApplyRuntimeState(BrowserWorkspaceApplicationCoordinator.CreateRuntimeSnapshot(new WorkspaceState
        {
            RestoreSnapshot = snapshot.Clone(),
            SavedAtUtc = DateTime.UtcNow
        }));

    private void SavePersistedMarksToSettings(
        IReadOnlyList<string> persistedPaths,
        bool usedPendingEscSnapshot)
    {
        EnsureNormalized();
        if (!_settings.Value.Session.PersistMarksAcrossRestart)
        {
            LogService.Info("[MarkPersistence] Save skipped because persistence is disabled.");
            return;
        }

        _settings.SetPersistedMarkedPaths(persistedPaths);
        string saveMode = usedPendingEscSnapshot ? "EscExitSnapshot" : "CurrentMarks";
        LogService.Info($"[MarkPersistence] Saved={persistedPaths.Count} Mode={saveMode}");
    }

    private bool SaveMarkMutationState()
    {
        StoreActiveCategorySessionState(updateCompatibilityMirror: false);
        return SaveWorkspaceStateStore();
    }

    private static bool TryGetPathKind(string path, out MarkPathKind markKind)
    {
        if (File.Exists(path))
        {
            markKind = new MarkPathKind(path, IsDirectory: false);
            return true;
        }

        if (Directory.Exists(path))
        {
            markKind = new MarkPathKind(path, IsDirectory: true);
            return true;
        }

        markKind = default;
        return false;
    }
}

internal readonly record struct BrowserWorkspaceMarkPersistencePlan(
    MarkPersistencePreparation Preparation,
    bool ActiveMarksWereDirty);

internal readonly record struct BrowserWorkspaceSessionSaveResult(
    bool WorkspaceSaveSucceeded,
    SettingsSqliteStore.SettingsSaveResult SettingsSaveResult,
    bool MarkPersistenceSucceeded,
    MarkPersistencePreparation MarkPreparation,
    bool ActiveMarksWereDirty);

internal readonly record struct BrowserMarkPersistenceTransition(
    BrowserMarksClearTransition Clear,
    bool PersistenceSucceeded);

internal enum BrowserStartupLegacyMarkRestoreKind
{
    Restored,
    SkippedByWorkspaceAuthority,
    SkippedDisabled,
    SkippedEmpty,
    SkippedMissing
}

internal readonly record struct BrowserStartupLegacyMarkRestoreResult(
    BrowserStartupLegacyMarkRestoreKind Kind,
    IReadOnlyList<string> RestoredPaths,
    int SkippedCount)
{
    public static BrowserStartupLegacyMarkRestoreResult SkippedByWorkspaceAuthority => new(
        BrowserStartupLegacyMarkRestoreKind.SkippedByWorkspaceAuthority,
        Array.Empty<string>(),
        0);
}
