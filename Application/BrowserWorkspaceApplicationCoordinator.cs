using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using MidFD.Configuration;
using MidFD.Helpers;
using MidFD.Models;
using MidFD.Presentation;
using MidFD.Services;
using MidFD.Services.Workspace;

namespace MidFD.Runtime;

/// <summary>
/// Browser tab/categoryのlogical state transitionを保持する。
/// Control selectionやdirectory listのmaterializationは受け持たない。
/// </summary>
internal sealed class BrowserWorkspaceApplicationCoordinator
{
    private readonly BrowserSessionState _session;

    public BrowserWorkspaceApplicationCoordinator(BrowserSessionState session)
    {
        _session = session;
    }

    private string ResolveTabTitle(string? path) =>
        BrowserTabPresentationHelper.BuildTabTitle(
            path,
            normalizedPath => QuickAccessService.FindAliasDisplayName(
                _session.Workspace.QuickAccess,
                normalizedPath));

    private BrowserTabViewState Tabs => _session.Tabs;
    private BrowserCategoryViewState Categories => _session.Categories;
    private BrowserTabState? ActiveTab => Tabs.ActiveTab;
    public int TabCount => Tabs.Count;
    public int ActiveTabIndex => Tabs.ActiveTabIndex;
    public int ContextTabIndex => Tabs.ContextTabIndex;
    public IReadOnlyList<BrowserTabState> TabStates => Tabs.Tabs.Select(static tab => tab.Clone()).ToList();
    public BrowserTabState? ActiveTabSnapshot => Tabs.ActiveTab?.Clone();
    public BrowserTabState? GetTabSnapshot(int index) => Tabs.IsValidIndex(index) ? Tabs.Tabs[index].Clone() : null;

    public int RemoveMarksFromTab(
        SessionSettings session,
        string categoryId,
        Guid tabId,
        IReadOnlyCollection<string> paths)
    {
        if (tabId == Guid.Empty || paths.Count == 0)
        {
            return 0;
        }

        var pathSet = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        string resolvedCategoryId = ResolveCategoryId(categoryId);
        if (string.Equals(resolvedCategoryId, ActiveCategoryId, StringComparison.OrdinalIgnoreCase))
        {
            BrowserTabState? activeCategoryTab = Tabs.Tabs.FirstOrDefault(tab => tab.Id == tabId);
            return activeCategoryTab == null
                ? 0
                : RemoveMarks(activeCategoryTab.MarkedPaths, pathSet);
        }

        BrowserTabRestoreCategoryState? categoryState = FindRestoreCategoryState(session, resolvedCategoryId);
        BrowserTabSessionState? storedTab = categoryState?.OpenTabs.FirstOrDefault(tab => tab.TabId == tabId);
        return storedTab == null ? 0 : RemoveMarks(storedTab.MarkedPaths, pathSet);
    }

    private static int RemoveMarks(List<string> markedPaths, HashSet<string> paths)
    {
        int before = markedPaths.Count;
        markedPaths.RemoveAll(path => paths.Contains(path));
        return before - markedPaths.Count;
    }

    public void SyncActiveFilterState(string pattern, bool useRegex)
    {
        if (!Tabs.IsValidIndex(Tabs.ActiveTabIndex))
        {
            return;
        }

        BrowserTabState activeState = Tabs.Tabs[Tabs.ActiveTabIndex];
        activeState.FilterPattern = pattern ?? string.Empty;
        activeState.FilterUseRegex = useRegex;
    }

    public void SetTabFilterState(
        int tabIndex,
        string? pattern,
        bool useRegex,
        TabFilterLockState? filterLock)
    {
        if (!Tabs.IsValidIndex(tabIndex))
        {
            return;
        }

        BrowserTabState state = Tabs.Tabs[tabIndex];
        state.FilterPattern = pattern ?? string.Empty;
        state.FilterUseRegex = useRegex;
        state.FilterLock = filterLock?.Clone() ?? new TabFilterLockState();
    }

    public int CategoryCount => Categories.Count;
    public string? ContextCategoryId => Categories.ContextCategoryId;
    public IReadOnlyList<BrowserTabCategoryDefinition> CategoryStates =>
        Categories.Categories.Select(static category => category.Clone()).ToList();
    public BrowserTabCategoryDefinition? FindCategorySnapshot(string? categoryId) =>
        Categories.Categories.FirstOrDefault(category =>
            string.Equals(category.Id, categoryId, StringComparison.OrdinalIgnoreCase))?.Clone();

    public bool TryGetCategoryTabCount(
        SessionSettings session,
        string categoryId,
        out int tabCount)
    {
        tabCount = 0;
        if (FindCategory(categoryId) == null)
        {
            return false;
        }

        if (string.Equals(ActiveCategoryId, categoryId, StringComparison.OrdinalIgnoreCase))
        {
            tabCount = Tabs.Count;
            return true;
        }

        BrowserTabRestoreCategoryState? categoryState = FindRestoreCategoryState(session, categoryId);
        tabCount = categoryState?.OpenTabs?.Count ?? 0;
        return true;
    }
    public int FindCategoryIndex(string? categoryId)
    {
        for (int index = 0; index < Categories.Categories.Count; index++)
        {
            if (string.Equals(Categories.Categories[index].Id, categoryId, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }
        return -1;
    }
    public QuickAccessStore QuickAccessSnapshot => _session.Workspace.QuickAccess.Clone();
    public MarkSlotStore MarkSlotsSnapshot => _session.Workspace.MarkSlots.Clone();
    public IWorkspaceStateStore? StateStore => _session.Workspace.StateStore;
    public bool HasSnapshotStorage => _session.Workspace.SnapshotStorage != null;
    public int SnapshotCount => _session.Workspace.SnapshotStorage?.LoadEntries().Count ?? 0;
    public bool RestoredBrowserTabsFromStore => _session.Workspace.RestoredBrowserTabsFromStore;

    public void SetQuickAccess(QuickAccessStore value) => _session.Workspace.QuickAccess = value.Clone();
    public void SetMarkSlots(MarkSlotStore value) => _session.Workspace.MarkSlots = value.Clone();

    public MarkSlotEntry GetMarkSlotSnapshot(int slotNumber)
    {
        return _session.Workspace.MarkSlots.Slots
            .FirstOrDefault(slot => slot.SlotNumber == slotNumber)?.Clone()
            ?? new MarkSlotEntry
            {
                SlotNumber = slotNumber,
                DisplayName = $"スロット {slotNumber}"
            };
    }

    public void UpdateMarkSlot(MarkSlotEntry slot)
    {
        MarkSlotEntry? current = _session.Workspace.MarkSlots.Slots
            .FirstOrDefault(candidate => candidate.SlotNumber == slot.SlotNumber);
        if (current == null)
        {
            _session.Workspace.MarkSlots.Slots.Add(slot.Clone());
            return;
        }

        int index = _session.Workspace.MarkSlots.Slots.IndexOf(current);
        _session.Workspace.MarkSlots.Slots[index] = slot.Clone();
    }
    public void SetStateStore(IWorkspaceStateStore? value) => _session.Workspace.StateStore = value;
    public void SetSnapshotStorage(WorkspaceSnapshotStorage? value) => _session.Workspace.SnapshotStorage = value;
    public void SetRestoredBrowserTabsFromStore(bool value) => _session.Workspace.RestoredBrowserTabsFromStore = value;

    public IReadOnlyList<WorkspaceSnapshotEntry> LoadSnapshotEntries()
    {
        return _session.Workspace.SnapshotStorage?.LoadEntries() ?? Array.Empty<WorkspaceSnapshotEntry>();
    }

    public WorkspaceState CaptureWorkspaceState(SessionSettings session)
    {
        return new WorkspaceState
        {
            RestoreSnapshot = CaptureRuntimeSnapshot(session).RestoreSnapshot.Clone(),
            SavedAtUtc = DateTime.UtcNow
        };
    }

    public bool SnapshotExistsByName(string name)
    {
        return _session.Workspace.SnapshotStorage?.ExistsByName(name) ?? false;
    }

    public bool TrySaveSnapshot(string name, WorkspaceState state, out string errorMessage)
    {
        if (_session.Workspace.SnapshotStorage == null)
        {
            errorMessage = "Workspace スナップショットの保存先を初期化できません。";
            return false;
        }

        return _session.Workspace.SnapshotStorage.TrySaveSnapshot(name, state, out errorMessage);
    }

    public bool TryLoadSnapshotState(string snapshotId, out WorkspaceState? state, out string errorMessage)
    {
        if (_session.Workspace.SnapshotStorage == null)
        {
            state = null;
            errorMessage = "Workspace スナップショットの保存先を初期化できません。";
            return false;
        }

        return _session.Workspace.SnapshotStorage.TryLoadSnapshotState(snapshotId, out state, out errorMessage);
    }

    public bool TryRenameSnapshot(string snapshotId, string name, out string errorMessage)
    {
        if (_session.Workspace.SnapshotStorage == null)
        {
            errorMessage = "Workspace スナップショットの保存先を初期化できません。";
            return false;
        }

        return _session.Workspace.SnapshotStorage.TryRenameSnapshot(snapshotId, name, out errorMessage);
    }

    public bool DeleteSnapshot(string snapshotId)
    {
        return _session.Workspace.SnapshotStorage?.DeleteSnapshot(snapshotId) == true;
    }

    public bool TryGetSnapshotPayload(string snapshotId, out string? payloadJson, out string errorMessage)
    {
        if (_session.Workspace.SnapshotStorage == null)
        {
            payloadJson = null;
            errorMessage = "Workspace スナップショットの保存先を初期化できません。";
            return false;
        }

        return _session.Workspace.SnapshotStorage.TryGetSnapshotPayload(snapshotId, out payloadJson, out errorMessage);
    }

    public bool TryExportSnapshot(string snapshotId, WorkspaceSnapshotMetadata metadata, string destinationPath, out string errorMessage)
    {
        errorMessage = string.Empty;
        if (!TryGetSnapshotPayload(snapshotId, out string? payloadJson, out errorMessage) || payloadJson == null)
        {
            return false;
        }

        try
        {
            var exportFile = new WorkspaceSnapshotExportFile
            {
                Metadata = metadata,
                Payload = JsonSerializer.Deserialize<WorkspaceState>(payloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            };
            string json = JsonSerializer.Serialize(exportFile, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
            File.WriteAllText(destinationPath, json);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    public bool TryImportSnapshot(string sourcePath, string fallbackName, out string importedName, out string errorMessage)
    {
        importedName = string.Empty;
        errorMessage = string.Empty;
        if (_session.Workspace.SnapshotStorage == null)
        {
            errorMessage = "Workspace スナップショットの保存先を初期化できません。";
            return false;
        }

        try
        {
            string json = File.ReadAllText(sourcePath);
            var importFile = JsonSerializer.Deserialize<WorkspaceSnapshotExportFile>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (importFile?.Kind != "MidFD.WorkspaceSnapshot" || importFile.Payload == null)
            {
                errorMessage = "無効なスナップショットファイルです。";
                return false;
            }

            string name = importFile.Metadata?.Name ?? fallbackName;
            if (!TrySaveImportedSnapshot(name, importFile.Payload, "imported", out importedName, out errorMessage))
            {
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    public IReadOnlyList<(WorkspaceSnapshotEntry Entry, string PayloadJson)> LoadAllSnapshotsWithPayload()
    {
        return _session.Workspace.SnapshotStorage?.LoadAllSnapshotsWithPayload()
            ?? Array.Empty<(WorkspaceSnapshotEntry Entry, string PayloadJson)>();
    }

    public bool TryExportAllSnapshots(string destinationPath, out int exportedCount, out string errorMessage)
    {
        exportedCount = 0;
        errorMessage = string.Empty;
        IReadOnlyList<(WorkspaceSnapshotEntry Entry, string PayloadJson)> all = LoadAllSnapshotsWithPayload();
        if (all.Count == 0)
        {
            errorMessage = "エクスポートするスナップショットがありません。";
            return false;
        }

        try
        {
            var backupSet = new WorkspaceSnapshotBackupSetFile();
            foreach (var (entry, payloadJson) in all)
            {
                backupSet.Snapshots.Add(new WorkspaceSnapshotExportFile
                {
                    Metadata = new WorkspaceSnapshotMetadata
                    {
                        Name = entry.Name,
                        CreatedAtUtc = entry.CreatedAtUtc,
                        UpdatedAtUtc = entry.UpdatedAtUtc
                    },
                    Payload = JsonSerializer.Deserialize<WorkspaceState>(payloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                });
            }

            string json = JsonSerializer.Serialize(backupSet, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
            File.WriteAllText(destinationPath, json);
            exportedCount = all.Count;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    public bool TryImportAllSnapshots(string sourcePath, out int importedCount, out string errorMessage)
    {
        importedCount = 0;
        errorMessage = string.Empty;
        if (_session.Workspace.SnapshotStorage == null)
        {
            errorMessage = "Workspace スナップショットの保存先を初期化できません。";
            return false;
        }

        try
        {
            string json = File.ReadAllText(sourcePath);
            var backupSet = JsonSerializer.Deserialize<WorkspaceSnapshotBackupSetFile>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (backupSet?.Kind != "MidFD.WorkspaceSnapshotBackupSet" || backupSet.Snapshots == null)
            {
                errorMessage = "無効なバックアップセットファイルです。";
                return false;
            }
            if (backupSet.Snapshots.Count == 0)
            {
                errorMessage = "インポートするスナップショットが含まれていません。";
                return false;
            }

            foreach (WorkspaceSnapshotExportFile snapshotFile in backupSet.Snapshots)
            {
                if (snapshotFile.Payload == null)
                {
                    continue;
                }
                string name = snapshotFile.Metadata?.Name ?? "Imported Snapshot";
                if (TrySaveImportedSnapshot(name, snapshotFile.Payload, "backup", out _, out _))
                {
                    importedCount++;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private string CreateUniqueImportedSnapshotName(string requestedName, string sourceLabel)
    {
        string baseName = string.IsNullOrWhiteSpace(requestedName) ? "Imported Snapshot" : requestedName.Trim();
        if (!SnapshotExistsByName(baseName)) return baseName;

        string stampedName = $"{baseName} ({sourceLabel} {DateTime.Now:yyyy-MM-dd HH-mm-ss-fff})";
        string candidate = stampedName;
        int suffix = 2;
        while (SnapshotExistsByName(candidate))
        {
            candidate = $"{stampedName} ({suffix++})";
        }
        return candidate;
    }

    private bool TrySaveImportedSnapshot(
        string candidateName,
        WorkspaceState state,
        string sourceLabel,
        out string savedName,
        out string errorMessage)
    {
        savedName = string.Empty;
        errorMessage = string.Empty;
        WorkspaceSnapshotStorage? storage = _session.Workspace.SnapshotStorage;
        if (storage == null)
        {
            errorMessage = "Workspace スナップショットの保存先を初期化できません。";
            return false;
        }

        string requestedName = candidateName;
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (storage.TrySaveSnapshotIfNameAvailable(candidateName, state, out bool nameAlreadyExists, out errorMessage))
            {
                savedName = candidateName;
                return true;
            }
            if (!nameAlreadyExists) return false;
            candidateName = CreateUniqueImportedSnapshotName(requestedName, sourceLabel);
        }

        errorMessage = "一意なスナップショット名を確保できませんでした。";
        return false;
    }

    public BrowserTabSessionSerializationResult StoreActiveCategorySessionState(
        BrowserTabSettings settings,
        SessionSettings session,
        int maxTabCount,
        bool updateCompatibilityMirror) =>
        StoreActiveCategorySessionState(
            settings,
            session,
            Tabs.Tabs,
            Tabs.ActiveTabIndex,
            maxTabCount,
            updateCompatibilityMirror);

    public string ActiveCategoryId => ResolveCategoryId(Categories.ActiveCategoryId);

    public void PushClosedTabSnapshot(int tabIndex, int limit)
    {
        if (!Tabs.IsValidIndex(tabIndex) || limit <= 0)
        {
            return;
        }

        _session.Workspace.ClosedBrowserTabs.Add(new ClosedBrowserTabSnapshot
        {
            CategoryId = Categories.ActiveCategoryId ?? string.Empty,
            TabState = Tabs.Tabs[tabIndex].Clone()
        });
        while (_session.Workspace.ClosedBrowserTabs.Count > limit)
        {
            _session.Workspace.ClosedBrowserTabs.RemoveAt(0);
        }
    }

    public BrowserClosedTabRestoreTransition RestoreLastClosedTab(int maxTabCount)
    {
        List<ClosedBrowserTabSnapshot> closedTabs = _session.Workspace.ClosedBrowserTabs;
        if (closedTabs.Count == 0)
        {
            return BrowserClosedTabRestoreTransition.None;
        }

        ClosedBrowserTabSnapshot snapshot = closedTabs[^1];
        string targetCategoryId = Categories.Categories.Any(category =>
                string.Equals(category.Id, snapshot.CategoryId, StringComparison.OrdinalIgnoreCase))
            ? snapshot.CategoryId
            : ActiveCategoryId;
        if (!string.Equals(targetCategoryId, ActiveCategoryId, StringComparison.OrdinalIgnoreCase))
        {
            return new BrowserClosedTabRestoreTransition(
                BrowserClosedTabRestoreKind.RequiresCategorySwitch,
                targetCategoryId,
                null);
        }
        if (Tabs.Count >= maxTabCount)
        {
            return new BrowserClosedTabRestoreTransition(
                BrowserClosedTabRestoreKind.LimitReached,
                targetCategoryId,
                null);
        }

        closedTabs.RemoveAt(closedTabs.Count - 1);
        Tabs.Add(snapshot.TabState.Clone());
        return new BrowserClosedTabRestoreTransition(
            BrowserClosedTabRestoreKind.Restored,
            targetCategoryId,
            Tabs.Tabs[^1]);
    }

    public void EnsureCategoryConfiguration(BrowserTabSettings settings, SessionSettings session)
    {
        Categories.Clear();
        var normalizedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BrowserTabCategoryDefinition category in settings.Categories ?? Enumerable.Empty<BrowserTabCategoryDefinition>())
        {
            string normalizedId = NormalizeCategoryId(category.Id);
            if (!normalizedIds.Add(normalizedId))
            {
                continue;
            }

            Categories.Add(new BrowserTabCategoryDefinition
            {
                Id = normalizedId,
                DisplayName = string.IsNullOrWhiteSpace(category.DisplayName) ? "既定" : category.DisplayName.Trim()
            });
        }

        if (Categories.Count == 0)
        {
            Categories.Add(new BrowserTabCategoryDefinition
            {
                Id = BrowserTabSettings.DefaultCategoryId,
                DisplayName = "既定"
            });
        }

        settings.Categories = Categories.Categories
            .Select(static category => category.Clone())
            .ToList();
        session.ActiveBrowserTabCategoryId = ResolveCategoryId(session.ActiveBrowserTabCategoryId);
    }

    public string SyncActiveCategoryFromSession(SessionSettings session)
    {
        string sessionCategoryId = session.BrowserTabRestoreSnapshot?.ActiveCategoryId
            ?? session.ActiveBrowserTabCategoryId;
        string resolvedCategoryId = ResolveCategoryId(sessionCategoryId);
        Categories.ActiveCategoryId = resolvedCategoryId;
        return resolvedCategoryId;
    }

    public void SyncCategoriesToSettings(BrowserTabSettings settings)
    {
        settings.Categories = Categories.Categories
            .Select(static category => category.Clone())
            .ToList();
    }

    public string GetNextCategoryDisplayName()
    {
        for (int index = 1; ; index++)
        {
            string candidate = $"カテゴリ{index}";
            if (!Categories.Categories.Any(category =>
                    string.Equals(category.DisplayName, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }
    }

    public bool TryAddCategory(
        string displayName,
        BrowserTabSettings settings,
        SessionSettings session,
        out BrowserTabCategoryDefinition? category)
    {
        category = null;
        string trimmedName = displayName.Trim();
        if (string.IsNullOrWhiteSpace(trimmedName) || Categories.Categories.Any(existing =>
                string.Equals(existing.DisplayName, trimmedName, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        category = new BrowserTabCategoryDefinition
        {
            Id = CreateUniqueCategoryId(trimmedName),
            DisplayName = trimmedName
        };
        Categories.Add(category);
        settings.Categories = Categories.Categories
            .Select(static item => item.Clone())
            .ToList();
        EnsureRestoreSnapshot(session);
        return true;
    }

    public bool TryRenameCategory(
        string categoryId,
        string displayName,
        BrowserTabSettings settings,
        out bool duplicateName)
    {
        duplicateName = false;
        BrowserTabCategoryDefinition? category = FindCategory(categoryId);
        string trimmedName = displayName.Trim();
        if (category == null || string.IsNullOrWhiteSpace(trimmedName))
        {
            return false;
        }
        if (Categories.Categories.Any(existing =>
                !string.Equals(existing.Id, category.Id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.DisplayName, trimmedName, StringComparison.OrdinalIgnoreCase)))
        {
            duplicateName = true;
            return false;
        }

        category.DisplayName = trimmedName;
        settings.Categories = Categories.Categories
            .Select(static item => item.Clone())
            .ToList();
        return true;
    }

    public BrowserCategoryRemovalTransition RemoveCategoriesAndResolveFallback(
        IEnumerable<string> categoryIds,
        BrowserTabSettings settings,
        SessionSettings session)
    {
        HashSet<string> targetIds = categoryIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string? activeCategoryId = Categories.ActiveCategoryId;
        int activeCategoryIndex = GetActiveCategoryIndex();
        int removedCount = Categories.RemoveAll(category => targetIds.Contains(category.Id));
        BrowserTabCategoryDefinition? recoveredCategory = null;
        if (Categories.Count == 0)
        {
            recoveredCategory = EnsureAtLeastOneCategory();
        }

        SyncCategoriesToSettings(settings);
        EnsureRestoreSnapshot(session);
        bool needsFallback = recoveredCategory != null ||
            (activeCategoryId != null && targetIds.Contains(activeCategoryId));
        if (!needsFallback)
        {
            return new BrowserCategoryRemovalTransition(removedCount, false, null, -1);
        }

        int fallbackIndex = recoveredCategory != null
            ? 0
            : Math.Clamp(activeCategoryIndex, 0, Categories.Count - 1);
        string fallbackCategoryId = recoveredCategory?.Id ?? Categories.Categories[fallbackIndex].Id;
        return new BrowserCategoryRemovalTransition(removedCount, true, fallbackCategoryId, fallbackIndex);
    }

    public BrowserTabCategoryDefinition EnsureAtLeastOneCategory()
    {
        if (Categories.Count > 0)
        {
            return Categories.Categories[0];
        }

        string displayName = GetNextCategoryDisplayName();
        var category = new BrowserTabCategoryDefinition
        {
            Id = CreateUniqueCategoryId(displayName),
            DisplayName = displayName
        };
        Categories.Add(category);
        return category;
    }

    public BrowserWorkspaceRuntimeStateSnapshot CaptureRuntimeSnapshot(SessionSettings session)
    {
        return new BrowserWorkspaceRuntimeStateSnapshot(
            Categories.Categories.Select(static category => category.Clone()).ToList(),
            EnsureRestoreSnapshot(session).Clone(),
            ActiveCategoryId);
    }

    public static BrowserWorkspaceRuntimeStateSnapshot CreateRuntimeSnapshot(WorkspaceState workspaceState)
    {
        BrowserTabRestoreSnapshot snapshot = workspaceState.RestoreSnapshot.Clone();
        return new BrowserWorkspaceRuntimeStateSnapshot(
            BuildCategoryDefinitionsFromSnapshot(snapshot),
            snapshot,
            string.IsNullOrWhiteSpace(snapshot.ActiveCategoryId)
                ? BrowserTabSettings.DefaultCategoryId
                : snapshot.ActiveCategoryId);
    }

    public void ApplyRestoreSnapshotToSettings(
        BrowserWorkspaceRuntimeStateSnapshot runtimeState,
        BrowserTabSettings settings,
        SessionSettings session)
    {
        settings.Categories = runtimeState.CategoryDefinitions
            .Select(static category => category.Clone())
            .ToList();
        session.BrowserTabRestoreSnapshot = runtimeState.RestoreSnapshot.Clone();
        EnsureCategoryConfiguration(settings, session);
        string activeCategoryId = ResolveCategoryId(runtimeState.ActiveCategoryId);
        Categories.ActiveCategoryId = activeCategoryId;
        session.ActiveBrowserTabCategoryId = activeCategoryId;
        session.BrowserTabCategories = BuildCategorySessionStatesFromSnapshot(session.BrowserTabRestoreSnapshot);
        BrowserTabRestoreCategoryState? activeCategoryState = FindRestoreCategoryState(session, activeCategoryId);
        session.OpenTabs = activeCategoryState?.OpenTabs.Select(static tab => tab.Clone()).ToList()
            ?? new List<BrowserTabSessionState>();
        session.ActiveTabIndex = activeCategoryState?.ActiveTabIndex ?? 0;
    }

    public BrowserTabRestoreSnapshot EnsureRestoreSnapshot(SessionSettings session)
    {
        BrowserTabRestoreSnapshot source = (session.BrowserTabRestoreSnapshot ?? new BrowserTabRestoreSnapshot()).Clone();
        var existingStates = source.Categories
            .Where(static category => category != null)
            .GroupBy(category => NormalizeCategoryId(category.Id), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                static group => group.Key,
                static group => group.First().Clone(),
                StringComparer.OrdinalIgnoreCase);
        var normalized = new BrowserTabRestoreSnapshot
        {
            ActiveCategoryId = ResolveCategoryId(source.ActiveCategoryId),
            UserTabGroups = (source.UserTabGroups ?? new List<BrowserTabGroupRestoreState>())
                .Where(static group => group != null)
                .Select(static group => group.Clone())
                .ToList()
        };
        foreach (BrowserTabCategoryDefinition category in Categories.Categories)
        {
            string categoryId = NormalizeCategoryId(category.Id);
            existingStates.TryGetValue(categoryId, out BrowserTabRestoreCategoryState? existingState);
            normalized.Categories.Add(new BrowserTabRestoreCategoryState
            {
                Id = categoryId,
                DisplayName = string.IsNullOrWhiteSpace(category.DisplayName) ? "既定" : category.DisplayName.Trim(),
                ActiveTabIndex = existingState?.ActiveTabIndex ?? 0,
                OpenTabs = existingState?.OpenTabs.Select(static tab => tab.Clone()).ToList() ?? new List<BrowserTabSessionState>()
            });
        }
        if (normalized.Categories.Count == 0)
        {
            normalized.Categories.Add(new BrowserTabRestoreCategoryState
            {
                Id = BrowserTabSettings.DefaultCategoryId,
                DisplayName = "既定"
            });
        }
        normalized.ActiveCategoryId = ResolveCategoryId(normalized.ActiveCategoryId);
        session.BrowserTabRestoreSnapshot = normalized;
        return normalized;
    }

    public BrowserTabRestoreCategoryState? FindRestoreCategoryState(SessionSettings session, string categoryId)
    {
        BrowserTabRestoreSnapshot snapshot = EnsureRestoreSnapshot(session);
        string resolvedCategoryId = ResolveCategoryId(categoryId);
        return snapshot.Categories.FirstOrDefault(category =>
            string.Equals(category.Id, resolvedCategoryId, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<BrowserTabSessionState> GetSessionTabsForRestore(
        SessionSettings session,
        string requestedCategoryId,
        out string resolvedCategoryId)
    {
        BrowserTabRestoreSnapshot snapshot = EnsureRestoreSnapshot(session);
        BrowserTabRestoreCategoryState? activeCategoryState = snapshot.Categories
            .Where(static state => state != null && !string.IsNullOrWhiteSpace(state.Id))
            .FirstOrDefault(state => string.Equals(state.Id, requestedCategoryId, StringComparison.OrdinalIgnoreCase))
            ?? snapshot.Categories.FirstOrDefault(state => state.OpenTabs.Count > 0)
            ?? snapshot.Categories.FirstOrDefault();
        if (activeCategoryState != null && activeCategoryState.OpenTabs.Count > 0)
        {
            resolvedCategoryId = ResolveCategoryId(activeCategoryState.Id);
            return activeCategoryState.OpenTabs.Select(static tab => tab.Clone()).ToList();
        }

        resolvedCategoryId = BrowserTabSettings.DefaultCategoryId;
        return Array.Empty<BrowserTabSessionState>();
    }

    public int ResolveActiveTabIndex(SessionSettings session, string categoryId, int restoredTabCount)
    {
        if (restoredTabCount <= 0)
        {
            return 0;
        }
        BrowserTabRestoreCategoryState? categoryState = FindRestoreCategoryState(session, categoryId);
        return categoryState != null && categoryState.OpenTabs.Count > 0
            ? Math.Clamp(categoryState.ActiveTabIndex, 0, restoredTabCount - 1)
            : 0;
    }

    public IReadOnlyList<BrowserTabListCategorySnapshot> BuildTabListSnapshot(
        SessionSettings session)
    {
        var result = new List<BrowserTabListCategorySnapshot>(Categories.Count);
        string activeCategoryId = ActiveCategoryId;
        foreach (BrowserTabCategoryDefinition category in Categories.Categories)
        {
            bool isActive = string.Equals(category.Id, activeCategoryId, StringComparison.OrdinalIgnoreCase);
            IReadOnlyList<BrowserTabListTabSnapshot> tabs;
            if (isActive)
            {
                tabs = Tabs.Tabs
                    .Select((tab, index) => new BrowserTabListTabSnapshot(
                        index,
                        tab.Title,
                        tab.CurrentPath,
                        tab.IsLocked,
                        tab.IsReadOnly,
                        index == Tabs.ActiveTabIndex))
                    .ToList();
            }
            else
            {
                BrowserTabRestoreCategoryState? categoryState = FindRestoreCategoryState(session, category.Id);
                tabs = (categoryState?.OpenTabs ?? Enumerable.Empty<BrowserTabSessionState>())
                    .Select((tab, index) => new BrowserTabListTabSnapshot(
                        index,
                        ResolveTabTitle(tab.CurrentPath),
                        tab.CurrentPath,
                        tab.IsLocked,
                        tab.IsReadOnly,
                        false))
                    .ToList();
            }

            result.Add(new BrowserTabListCategorySnapshot(
                category.Id,
                string.IsNullOrWhiteSpace(category.DisplayName) ? "既定" : category.DisplayName,
                isActive,
                tabs));
        }
        return result;
    }

    public int GetActiveCategoryIndex()
    {
        if (Categories.Count == 0)
        {
            return -1;
        }
        int index = Categories.FindIndex(category =>
            string.Equals(category.Id, Categories.ActiveCategoryId, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : 0;
    }

    internal BrowserCategorySwitchTransition PrepareCategorySwitch(string categoryId, int? requestedTabIndex)
    {
        string targetCategoryId = ResolveCategoryId(categoryId);
        if (!string.Equals(targetCategoryId, ActiveCategoryId, StringComparison.OrdinalIgnoreCase))
        {
            return new BrowserCategorySwitchTransition(targetCategoryId, false, -1);
        }

        int tabIndex = requestedTabIndex.HasValue && Tabs.IsValidIndex(requestedTabIndex.Value)
            ? requestedTabIndex.Value
            : -1;
        return new BrowserCategorySwitchTransition(
            targetCategoryId,
            tabIndex >= 0 && tabIndex != Tabs.ActiveTabIndex,
            tabIndex);
    }

    public string? ResolveAdjacentCategoryId(int delta)
    {
        if (Categories.Count <= 1)
        {
            return null;
        }

        int currentIndex = GetActiveCategoryIndex();
        int nextIndex = currentIndex + delta;
        return nextIndex >= 0 && nextIndex < Categories.Count
            ? Categories.Categories[nextIndex].Id
            : null;
    }

    public BrowserTabRestoreResult RestoreTab(
        BrowserTabSessionState sessionTab)
    {
        if (sessionTab == null || !TryResolveRestorePath(sessionTab, out string restorePath, out string? statusMessage))
        {
            return BrowserTabRestoreResult.NotApplied;
        }

        List<string> backHistory = (sessionTab.BackHistory ?? new List<string>())
            .Where(static path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .ToList();
        List<string> forwardHistory = (sessionTab.ForwardHistory ?? new List<string>())
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

        List<MarkPathKind> validatedMarkedPaths = CreateValidatedMarkedPaths(sessionTab.MarkedPaths, out int skippedMarkCount);
        List<string> markedPaths = validatedMarkedPaths.Select(static entry => entry.Path).ToList();
        var state = new BrowserTabState
        {
            Id = sessionTab.TabId == Guid.Empty ? Guid.NewGuid() : sessionTab.TabId,
            Title = ResolveTabTitle(restorePath),
            CurrentPath = restorePath,
            IsLocked = sessionTab.IsLocked,
            StartupPath = sessionTab.StartupPath ?? string.Empty,
            IsReadOnly = sessionTab.IsReadOnly,
            FilterLock = sessionTab.FilterLock?.Clone() ?? new TabFilterLockState(),
            FilterPattern = sessionTab.FilterPattern,
            FilterUseRegex = sessionTab.FilterUseRegex,
            MarkedPaths = markedPaths,
            ValidatedMarkedPaths = validatedMarkedPaths,
            PendingMarkRestoreValidation = new List<MarkPathKind>(validatedMarkedPaths),
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
        return new BrowserTabRestoreResult(state, skippedMarkCount, statusMessage);
    }

    private static List<MarkPathKind> CreateValidatedMarkedPaths(
        IEnumerable<string>? paths,
        out int skippedCount) =>
        CreateValidatedMarkedPaths(
            paths,
            validationContext: null,
            out skippedCount,
            out _,
            out _,
            out _,
            out _,
            out _,
            out _,
            out _,
            out _,
            out _);

    private static List<MarkPathKind> CreateValidatedMarkedPaths(
        IEnumerable<string>? paths,
        BrowserMarkRestoreValidationContext? validationContext,
        out int skippedCount,
        out int snapshotHitCount,
        out int fallbackValidationCount,
        out int fallbackFileProbeCount,
        out int fallbackDirectoryProbeCount,
        out int parentBatchGroupCount,
        out int parentBatchPathCount,
        out int parentBatchHitCount,
        out int parentBatchMissCount,
        out int parentBatchFailureCount)
    {
        int skipped = 0;
        int snapshotHits = 0;
        var result = new List<MarkPathKind>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingPaths = new List<string>();

        void AddValidatedPath(string path, bool isDirectory, bool exists)
        {
            if (!exists)
            {
                skipped++;
                return;
            }

            if (seen.Add(path))
            {
                result.Add(new MarkPathKind(path, isDirectory));
            }
        }

        foreach (string? path in paths ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                skipped++;
                continue;
            }

            bool isDirectory;
            if (validationContext?.SnapshotPathKindsAreFresh == true &&
                validationContext.SnapshotPathKinds.TryGetValue(path, out isDirectory))
            {
                snapshotHits++;
                AddValidatedPath(path, isDirectory, exists: true);
                continue;
            }

            pendingPaths.Add(path);
        }

        MarkPathValidationResult validation = MarkPathValidationService.Validate(
            pendingPaths,
            validationContext?.ParentEnumeration);
        foreach (MarkPathKind markKind in validation.ExistingPaths)
        {
            AddValidatedPath(markKind.Path, markKind.IsDirectory, exists: true);
        }

        skippedCount = skipped + validation.SkippedCount;
        snapshotHitCount = snapshotHits;
        fallbackValidationCount = validation.Metrics.IndividualFallbackValidations;
        fallbackFileProbeCount = validation.Metrics.IndividualFileProbes;
        fallbackDirectoryProbeCount = validation.Metrics.IndividualDirectoryProbes;
        parentBatchGroupCount = validation.Metrics.ParentBatchGroups;
        parentBatchPathCount = validation.Metrics.ParentBatchPaths;
        parentBatchHitCount = validation.Metrics.ParentBatchHits;
        parentBatchMissCount = validation.Metrics.ParentBatchMisses;
        parentBatchFailureCount = validation.Metrics.ParentBatchFailures;
        return result;
    }

    private static bool TryGetPathKind(string path, out bool isDirectory)
    {
        if (File.Exists(path))
        {
            isDirectory = false;
            return true;
        }

        if (Directory.Exists(path))
        {
            isDirectory = true;
            return true;
        }

        isDirectory = false;
        return false;
    }

    public BrowserTabState CreateInitialTab(
        string categoryId,
        string? currentPath,
        int columnCount,
        SortKind sortKind,
        bool sortAscending)
    {
        string initialPath = currentPath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(initialPath) || !Directory.Exists(initialPath))
        {
            initialPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        if (string.IsNullOrWhiteSpace(initialPath) || !Directory.Exists(initialPath))
        {
            initialPath = AppContext.BaseDirectory;
        }

        return new BrowserTabState
        {
            Title = ResolveTabTitle(initialPath),
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
            ColumnCount = Math.Clamp(columnCount, 1, 9),
            SortKind = sortKind,
            SortAscending = sortAscending
        };
    }

    public BrowserTabCreationTransition CreateNewTab(
        BrowserTabState source,
        string? initialPath,
        BrowserTabNewPosition position,
        bool useConfiguredInsertion,
        int maxTabCount)
    {
        if (Tabs.Count >= Math.Max(1, maxTabCount))
        {
            return BrowserTabCreationTransition.LimitReached;
        }

        BrowserTabState newState = source.CloneWithNewId();
        newState.IsLocked = false;
        newState.StartupPath = string.Empty;
        newState.IsReadOnly = false;
        newState.FilterPattern = string.Empty;
        newState.FilterUseRegex = false;
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
            newState.Title = ResolveTabTitle(initialPath);
        }

        int newIndex = useConfiguredInsertion
            ? ResolveNewTabInsertIndex(Tabs.Count, Tabs.ActiveTabIndex, position)
            : Tabs.Count;
        Tabs.Insert(newIndex, newState);
        return new BrowserTabCreationTransition(true, newIndex, newState);
    }

    public BrowserTabSessionSerializationResult SerializeTabs(
        IReadOnlyList<BrowserTabState> sourceTabs,
        int activeTabIndex,
        int maxTabCount)
    {
        bool truncated = sourceTabs.Count > maxTabCount;
        IReadOnlyList<BrowserTabState> limitedTabs = truncated
            ? sourceTabs.Take(maxTabCount).ToList()
            : sourceTabs;
        List<BrowserTabSessionState> serializedTabs = limitedTabs
            .Where(static tab => !string.IsNullOrWhiteSpace(tab.CurrentPath))
            .Select(CreateSessionState)
            .ToList();
        int serializedActiveIndex = serializedTabs.Count == 0
            ? 0
            : Math.Clamp(activeTabIndex, 0, serializedTabs.Count - 1);
        return new BrowserTabSessionSerializationResult(serializedTabs, serializedActiveIndex, truncated);
    }

    public BrowserTabSessionSerializationResult StoreActiveCategorySessionState(
        BrowserTabSettings settings,
        SessionSettings session,
        IReadOnlyList<BrowserTabState> sourceTabs,
        int activeTabIndex,
        int maxTabCount,
        bool updateCompatibilityMirror)
    {
        EnsureCategoryConfiguration(settings, session);
        BrowserTabSessionSerializationResult result = SerializeTabs(sourceTabs, activeTabIndex, maxTabCount);
        string activeCategoryId = ActiveCategoryId;
        BrowserTabRestoreSnapshot snapshot = EnsureRestoreSnapshot(session);
        snapshot.ActiveCategoryId = activeCategoryId;
        BrowserTabRestoreCategoryState? categoryState = snapshot.Categories.FirstOrDefault(category =>
            string.Equals(category.Id, activeCategoryId, StringComparison.OrdinalIgnoreCase));
        if (categoryState == null)
        {
            categoryState = new BrowserTabRestoreCategoryState
            {
                Id = activeCategoryId,
                DisplayName = Categories.Categories.FirstOrDefault(category =>
                    string.Equals(category.Id, activeCategoryId, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? activeCategoryId
            };
            snapshot.Categories.Add(categoryState);
        }
        categoryState.DisplayName = Categories.Categories.FirstOrDefault(category =>
            string.Equals(category.Id, activeCategoryId, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? categoryState.DisplayName;
        categoryState.ActiveTabIndex = result.ActiveTabIndex;
        categoryState.OpenTabs = result.Tabs.ToList();
        session.BrowserTabRestoreSnapshot = snapshot;
        if (updateCompatibilityMirror)
        {
            session.ActiveBrowserTabCategoryId = activeCategoryId;
            session.BrowserTabCategories = UpsertBrowserTabCategorySessionState(
                session.BrowserTabCategories,
                new BrowserTabCategorySessionState
                {
                    CategoryId = activeCategoryId,
                    OpenTabs = result.Tabs.Select(static tab => tab.Clone()).ToList(),
                    ActiveTabIndex = result.ActiveTabIndex
                });
            session.OpenTabs = result.Tabs.Select(static tab => tab.Clone()).ToList();
            session.ActiveTabIndex = result.ActiveTabIndex;
        }
        return result;
    }

    public string NormalizeCategoryId(string? categoryId)
    {
        string trimmed = string.IsNullOrWhiteSpace(categoryId)
            ? BrowserTabSettings.DefaultCategoryId
            : categoryId.Trim();
        return string.Equals(trimmed, BrowserTabSettings.DefaultCategoryId, StringComparison.OrdinalIgnoreCase)
            ? BrowserTabSettings.DefaultCategoryId
            : trimmed;
    }

    public string ResolveCategoryId(string? categoryId)
    {
        string normalizedId = NormalizeCategoryId(categoryId);
        return Categories.Categories.Any(category =>
                string.Equals(category.Id, normalizedId, StringComparison.OrdinalIgnoreCase))
            ? normalizedId
            : Categories.FirstOrDefault()?.Id ?? BrowserTabSettings.DefaultCategoryId;
    }

    public BrowserTabCategoryDefinition? FindCategory(string? categoryId)
    {
        if (string.IsNullOrWhiteSpace(categoryId))
        {
            return null;
        }

        string resolvedId = NormalizeCategoryId(categoryId);
        return Categories.Categories.FirstOrDefault(category =>
            string.Equals(category.Id, resolvedId, StringComparison.OrdinalIgnoreCase));
    }

    public bool RenameCategory(string categoryId, string displayName, BrowserTabSettings settings)
    {
        BrowserTabCategoryDefinition? category = FindCategory(categoryId);
        if (category == null || string.IsNullOrWhiteSpace(displayName))
        {
            return false;
        }

        category.DisplayName = displayName.Trim();
        settings.Categories = Categories.Categories
            .Select(static item => item.Clone())
            .ToList();
        return true;
    }

    public string CreateUniqueCategoryId(string displayName)
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
        while (Categories.Categories.Any(category =>
            string.Equals(category.Id, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{baseId}-{suffix}";
            suffix++;
        }
        return candidate;
    }

    public static int ResolveNewTabInsertIndex(int tabCount, int activeTabIndex, BrowserTabNewPosition position)
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

    public static int GetMaxTabCount(BrowserTabSettings? settings)
    {
        int configuredMax = settings?.MaxTabsPerCategory ?? BrowserTabSettings.DefaultMaxTabsPerCategory;
        return Math.Clamp(configuredMax, 1, BrowserTabSettings.SafetyMaxTabsPerCategory);
    }

    public void PrepareTabActivation() => Tabs.ActiveTabIndex = -1;
    public void SetContextTabIndex(int index) => Tabs.ContextTabIndex = index;
    public int IndexOfTab(BrowserTabState tab) => Tabs.IndexOf(tab);

    public bool ReorderTab(int fromIndex, int toIndex)
    {
        if (!Tabs.IsValidIndex(fromIndex) || !Tabs.IsValidIndex(toIndex) || fromIndex == toIndex)
        {
            return false;
        }

        BrowserTabState? activeTab = Tabs.ActiveTab;
        BrowserTabState? contextTab = Tabs.ContextTabIndex >= 0 && Tabs.IsValidIndex(Tabs.ContextTabIndex)
            ? Tabs.Tabs[Tabs.ContextTabIndex]
            : null;
        BrowserTabState movedTab = Tabs.Tabs[fromIndex];
        Tabs.RemoveAt(fromIndex);
        Tabs.Insert(toIndex, movedTab);
        Tabs.ActiveTabIndex = activeTab == null ? Math.Clamp(toIndex, 0, Tabs.Count - 1) : Tabs.IndexOf(activeTab);
        Tabs.ContextTabIndex = contextTab == null ? -1 : Tabs.IndexOf(contextTab);
        return true;
    }

    public bool ReorderTabById(string categoryId, Guid tabId, Guid anchorTabId, bool insertAfter, SessionSettings? session)
    {
        string resolvedCategoryId = ResolveCategoryId(categoryId);
        if (tabId == Guid.Empty
            || anchorTabId == Guid.Empty
            || tabId == anchorTabId
            || string.IsNullOrWhiteSpace(resolvedCategoryId))
        {
            return false;
        }

        if (string.Equals(resolvedCategoryId, ActiveCategoryId, StringComparison.OrdinalIgnoreCase))
        {
            int fromIndex = FindTabIndexById(Tabs.Tabs, tabId);
            int anchorIndex = FindTabIndexById(Tabs.Tabs, anchorTabId);
            if (fromIndex < 0 || anchorIndex < 0) return false;

            int insertionIndex = ResolveTabInsertionIndex(fromIndex, anchorIndex, insertAfter);
            if (insertionIndex == fromIndex) return false;

            BrowserTabState? activeTab = Tabs.ActiveTab;
            BrowserTabState? contextTab = Tabs.ContextTabIndex >= 0 && Tabs.IsValidIndex(Tabs.ContextTabIndex)
                ? Tabs.Tabs[Tabs.ContextTabIndex]
                : null;
            BrowserTabState movedTab = Tabs.Tabs[fromIndex];
            Tabs.RemoveAt(fromIndex);
            Tabs.Insert(insertionIndex, movedTab);
            Tabs.ActiveTabIndex = activeTab == null ? Math.Clamp(insertionIndex, 0, Tabs.Count - 1) : Tabs.IndexOf(activeTab);
            Tabs.ContextTabIndex = contextTab == null ? -1 : Tabs.IndexOf(contextTab);
            return true;
        }

        if (session == null) return false;
        BrowserTabRestoreCategoryState? categoryState = FindRestoreCategoryState(session, resolvedCategoryId);
        List<BrowserTabSessionState>? storedTabs = categoryState?.OpenTabs;
        if (categoryState == null || storedTabs == null) return false;

        int storedFromIndex = storedTabs.FindIndex(tab => tab.TabId == tabId);
        int storedAnchorIndex = storedTabs.FindIndex(tab => tab.TabId == anchorTabId);
        if (storedFromIndex < 0 || storedAnchorIndex < 0) return false;

        int storedInsertionIndex = ResolveTabInsertionIndex(storedFromIndex, storedAnchorIndex, insertAfter);
        if (storedInsertionIndex == storedFromIndex) return false;

        Guid? activeTabId = categoryState.ActiveTabIndex >= 0 && categoryState.ActiveTabIndex < storedTabs.Count
            ? storedTabs[categoryState.ActiveTabIndex].TabId
            : null;
        BrowserTabSessionState movedStoredTab = storedTabs[storedFromIndex];
        storedTabs.RemoveAt(storedFromIndex);
        storedTabs.Insert(storedInsertionIndex, movedStoredTab);
        categoryState.OpenTabs = storedTabs;
        categoryState.ActiveTabIndex = activeTabId is { } activeId
            ? storedTabs.FindIndex(tab => tab.TabId == activeId)
            : Math.Clamp(categoryState.ActiveTabIndex, 0, Math.Max(0, storedTabs.Count - 1));
        UpdateCategorySessionState(session, categoryState);
        return true;
    }

    private static int ResolveTabInsertionIndex(int fromIndex, int anchorIndex, bool insertAfter)
    {
        int insertionIndex = anchorIndex + (insertAfter ? 1 : 0);
        if (fromIndex < insertionIndex) insertionIndex--;
        return insertionIndex;
    }

    private static int FindTabIndexById(IReadOnlyList<BrowserTabState> tabs, Guid tabId)
    {
        for (int index = 0; index < tabs.Count; index++)
        {
            if (tabs[index].Id == tabId) return index;
        }

        return -1;
    }

    private void SetTabMarkedPaths(BrowserTabState tab, IEnumerable<string> paths)
    {
        tab.MarkedPaths = paths.ToList();
        tab.ValidatedMarkedPaths = _session.Selection
            .BuildPassivePathKinds(null, tab.MarkedPaths)
            .Select(static pair => new MarkPathKind(pair.Key, pair.Value))
            .ToList();
        tab.PendingMarkRestoreValidation.Clear();
    }

    public void SetTabMarkedPaths(int tabIndex, IEnumerable<string> paths)
    {
        if (Tabs.IsValidIndex(tabIndex))
        {
            SetTabMarkedPaths(Tabs.Tabs[tabIndex], paths);
        }
    }

    private void SetTabMarksDirty(BrowserTabState tab, bool value) => tab.MarksDirty = value;

    public void SetTabMarksDirty(int tabIndex, bool value)
    {
        if (Tabs.IsValidIndex(tabIndex))
        {
            SetTabMarksDirty(Tabs.Tabs[tabIndex], value);
        }
    }

    private void SetTabTitle(BrowserTabState tab, string title) => tab.Title = title;

    public bool SyncActiveTabMarks(IEnumerable<string> paths)
    {
        BrowserTabState? activeTab = Tabs.ActiveTab;
        if (activeTab == null)
        {
            return false;
        }

        SetTabMarkedPaths(activeTab, paths);
        activeTab.MarksDirty = true;
        return true;
    }

    private void SetTabFilterLock(BrowserTabState tab, TabFilterLockState filterLock)
    {
        tab.FilterLock = filterLock.Clone();
    }

    public void SetTabFilterLock(int tabIndex, TabFilterLockState filterLock)
    {
        if (Tabs.IsValidIndex(tabIndex))
        {
            SetTabFilterLock(Tabs.Tabs[tabIndex], filterLock);
        }
    }

    public void SetTabTitle(int tabIndex, string title)
    {
        if (Tabs.IsValidIndex(tabIndex))
        {
            Tabs.Tabs[tabIndex].Title = title;
        }
    }

    private void ApplyCapturedState(
        BrowserTabState currentState,
        BrowserTabState latestState,
        bool captureMarks,
        bool shouldValidateMarks,
        bool markValidationSucceeded)
    {
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
        if (!currentState.IsLocked || string.IsNullOrWhiteSpace(currentState.StartupPath))
        {
            currentState.StartupPath = latestState.StartupPath;
        }
        currentState.IsReadOnly = latestState.IsReadOnly;
        currentState.FilterLock = latestState.FilterLock.Clone();
        if (captureMarks)
        {
            List<string> latestMarkedPaths = latestState.MarkedPaths.ToList();
            bool canReuseValidatedKinds = currentState.MarkedPaths
                .SequenceEqual(latestMarkedPaths, StringComparer.OrdinalIgnoreCase) &&
                currentState.ValidatedMarkedPaths
                    .Select(static entry => entry.Path)
                    .SequenceEqual(latestMarkedPaths, StringComparer.OrdinalIgnoreCase);
            currentState.MarkedPaths = latestMarkedPaths;
            if (!canReuseValidatedKinds)
            {
                currentState.ValidatedMarkedPaths = _session.Selection
                    .BuildPassivePathKinds(null, currentState.MarkedPaths)
                    .Select(static pair => new MarkPathKind(pair.Key, pair.Value))
                    .ToList();
            }
            currentState.PendingMarkRestoreValidation.Clear();
            if (shouldValidateMarks || markValidationSucceeded)
            {
                currentState.MarksDirty = false;
            }
        }
    }

    public void ApplyCapturedState(
        int tabIndex,
        BrowserTabState latestState,
        bool captureMarks,
        bool shouldValidateMarks,
        bool markValidationSucceeded)
    {
        if (Tabs.IsValidIndex(tabIndex))
        {
            ApplyCapturedState(
                Tabs.Tabs[tabIndex],
                latestState,
                captureMarks,
                shouldValidateMarks,
                markValidationSucceeded);
        }
    }

    public BrowserTabActivationTransition ActivateTabState(
        int tabIndex,
        bool allowPendingMarkRestoreValidation = true,
        BrowserMarkRestoreValidationContext? validationContext = null)
    {
        if (!Tabs.IsValidIndex(tabIndex))
        {
            return BrowserTabActivationTransition.NotApplied;
        }
        BrowserTabState state = Tabs.Tabs[tabIndex];
        Tabs.ActiveTabIndex = tabIndex;
        _session.NavigationState.Navigation.RestoreState(state.Navigation);
        _session.NavigationState.CurrentSort = state.SortKind;
        _session.NavigationState.SortAscending = state.SortAscending;
        _session.NavigationState.FilterPattern = state.FilterPattern;
        _session.NavigationState.FilterUseRegex = state.FilterUseRegex;
        _session.NavigationState.CursorIndex = state.CursorIndex;
        List<MarkPathKind> restoredMarkKinds;
        int skippedMarkCount;
        int snapshotHitCount = 0;
        int fallbackValidationCount = 0;
        int fallbackFileProbeCount = 0;
        int fallbackDirectoryProbeCount = 0;
        int parentBatchGroupCount = 0;
        int parentBatchPathCount = 0;
        int parentBatchHitCount = 0;
        int parentBatchMissCount = 0;
        int parentBatchFailureCount = 0;
        if (validationContext?.SnapshotPathKindsAreFresh == true)
        {
            restoredMarkKinds = CreateValidatedMarkedPaths(
                state.MarkedPaths,
                validationContext,
                out skippedMarkCount,
                out snapshotHitCount,
                out fallbackValidationCount,
                out fallbackFileProbeCount,
                out fallbackDirectoryProbeCount,
                out parentBatchGroupCount,
                out parentBatchPathCount,
                out parentBatchHitCount,
                out parentBatchMissCount,
                out parentBatchFailureCount);
        }
        else if (allowPendingMarkRestoreValidation && HasPendingMarkRestoreValidation(state))
        {
            restoredMarkKinds = state.PendingMarkRestoreValidation.ToList();
            skippedMarkCount = 0;
        }
        else
        {
            restoredMarkKinds = CreateValidatedMarkedPaths(
                state.MarkedPaths,
                validationContext,
                out skippedMarkCount,
                out snapshotHitCount,
                out fallbackValidationCount,
                out fallbackFileProbeCount,
                out fallbackDirectoryProbeCount,
                out parentBatchGroupCount,
                out parentBatchPathCount,
                out parentBatchHitCount,
                out parentBatchMissCount,
                out parentBatchFailureCount);
        }
        List<string> restoredMarks = restoredMarkKinds.Select(static entry => entry.Path).ToList();
        _session.Selection.RestoreSelectionWithKnownKinds(restoredMarkKinds);
        state.MarkedPaths = restoredMarks;
        state.ValidatedMarkedPaths = restoredMarkKinds;
        state.PendingMarkRestoreValidation.Clear();
        return new BrowserTabActivationTransition(
            true,
            state.ColumnCount,
            state.SortKind,
            state.SortAscending,
            state.Navigation,
            state,
            restoredMarkKinds,
            skippedMarkCount)
        {
            SnapshotMarkHitCount = snapshotHitCount,
            FallbackValidationCount = fallbackValidationCount,
            FallbackFileProbeCount = fallbackFileProbeCount,
            FallbackDirectoryProbeCount = fallbackDirectoryProbeCount,
            ParentBatchGroupCount = parentBatchGroupCount,
            ParentBatchPathCount = parentBatchPathCount,
            ParentBatchHitCount = parentBatchHitCount,
            ParentBatchMissCount = parentBatchMissCount,
            ParentBatchFailureCount = parentBatchFailureCount,
            SnapshotPathKindsAreFresh = validationContext?.SnapshotPathKindsAreFresh == true
        };
    }

    private static bool HasPendingMarkRestoreValidation(BrowserTabState state) =>
        state.PendingMarkRestoreValidation.Count == state.MarkedPaths.Count &&
        state.PendingMarkRestoreValidation
            .Select(static entry => entry.Path)
            .SequenceEqual(state.MarkedPaths, StringComparer.OrdinalIgnoreCase);

    public void SetContextCategoryId(string? categoryId) => Categories.ContextCategoryId = categoryId;

    public bool ReorderCategory(
        int fromIndex,
        int toIndex,
        BrowserTabSettings settings,
        SessionSettings? session)
    {
        if (fromIndex < 0 || fromIndex >= Categories.Count || toIndex < 0 || toIndex >= Categories.Count || fromIndex == toIndex)
        {
            return false;
        }

        BrowserTabCategoryDefinition moved = Categories.Categories[fromIndex];
        Categories.RemoveAt(fromIndex);
        Categories.Insert(toIndex, moved);
        settings.Categories = Categories.Categories
            .Select(static category => category.Clone())
            .ToList();
        ReorderCategoryState(session?.BrowserTabCategories, moved.Id, toIndex);
        ReorderCategoryState(session?.BrowserTabRestoreSnapshot?.Categories, moved.Id, toIndex);
        return true;
    }

    public BrowserCategoryReorderTransition ReorderCategoryByDelta(
        string categoryId,
        int delta,
        BrowserTabSettings settings,
        SessionSettings session)
    {
        if (delta == 0)
        {
            return BrowserCategoryReorderTransition.NotApplied;
        }

        int currentIndex = Categories.FindIndex(category =>
            string.Equals(category.Id, categoryId, StringComparison.OrdinalIgnoreCase));
        int targetIndex = currentIndex + delta;
        if (currentIndex < 0 || targetIndex < 0 || targetIndex >= Categories.Count)
        {
            return BrowserCategoryReorderTransition.NotApplied;
        }

        string movedCategoryId = Categories.Categories[currentIndex].Id;
        string displayName = Categories.Categories[currentIndex].DisplayName;
        return ReorderCategory(currentIndex, targetIndex, settings, session)
            ? new BrowserCategoryReorderTransition(true, movedCategoryId, displayName)
            : BrowserCategoryReorderTransition.NotApplied;
    }

    public BrowserMarksClearTransition ClearCategoryMarks(string categoryId, SessionSettings session)
    {
        int clearedCount = 0;
        bool changed = false;
        foreach (BrowserTabState tab in Tabs.Tabs)
        {
            clearedCount += ClearMarkPaths(tab.MarkedPaths, ref changed);
        }

        clearedCount += ClearCurrentSelection(ref changed);
        if (session.BrowserTabRestoreSnapshot != null)
        {
            foreach (BrowserTabRestoreCategoryState category in session.BrowserTabRestoreSnapshot.Categories
                .Where(category => string.Equals(category.Id, categoryId, StringComparison.OrdinalIgnoreCase)))
            {
                foreach (BrowserTabSessionState tab in category.OpenTabs)
                {
                    clearedCount += ClearMarkPaths(tab.MarkedPaths, ref changed);
                }
            }
        }

        foreach (BrowserTabCategorySessionState category in session.BrowserTabCategories
            .Where(category => string.Equals(category.CategoryId, categoryId, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (BrowserTabSessionState tab in category.OpenTabs)
            {
                clearedCount += ClearMarkPaths(tab.MarkedPaths, ref changed);
            }
        }

        return new BrowserMarksClearTransition(changed, clearedCount);
    }

    public BrowserMarksClearTransition ClearAllMarks(SessionSettings session)
    {
        int clearedCount = 0;
        bool changed = false;
        foreach (BrowserTabState tab in Tabs.Tabs)
        {
            clearedCount += ClearMarkPaths(tab.MarkedPaths, ref changed);
        }

        clearedCount += ClearCurrentSelection(ref changed);
        if (session.BrowserTabRestoreSnapshot != null)
        {
            foreach (BrowserTabRestoreCategoryState category in session.BrowserTabRestoreSnapshot.Categories)
            {
                foreach (BrowserTabSessionState tab in category.OpenTabs)
                {
                    clearedCount += ClearMarkPaths(tab.MarkedPaths, ref changed);
                }
            }
        }

        foreach (BrowserTabCategorySessionState category in session.BrowserTabCategories)
        {
            foreach (BrowserTabSessionState tab in category.OpenTabs)
            {
                clearedCount += ClearMarkPaths(tab.MarkedPaths, ref changed);
            }
        }

        foreach (BrowserTabSessionState tab in session.OpenTabs)
        {
            clearedCount += ClearMarkPaths(tab.MarkedPaths, ref changed);
        }

        return new BrowserMarksClearTransition(changed, clearedCount);
    }

    public void ReplaceActiveTabs(string categoryId, IEnumerable<BrowserTabState> tabs, int activeIndex)
    {
        Tabs.Clear();
        Tabs.AddRange(tabs);
        Categories.ActiveCategoryId = categoryId;
        Tabs.ContextTabIndex = -1;
        Tabs.ActiveTabIndex = activeIndex;
    }

    public BrowserTabLockTransition ToggleLock(int tabIndex, string? currentPath)
    {
        if (!Tabs.IsValidIndex(tabIndex))
        {
            return BrowserTabLockTransition.NotApplied;
        }

        BrowserTabState state = Tabs.Tabs[tabIndex];
        state.IsLocked = !state.IsLocked;
        if (state.IsLocked)
        {
            if (string.IsNullOrWhiteSpace(state.StartupPath))
            {
                string rawPath = !string.IsNullOrWhiteSpace(currentPath) && Directory.Exists(currentPath)
                    ? currentPath
                    : state.CurrentPath;
                state.StartupPath = NormalizeDestinationDirectory(rawPath);
            }
        }
        else
        {
            state.StartupPath = string.Empty;
        }
        return new BrowserTabLockTransition(true, tabIndex, state.IsLocked, state.StartupPath);
    }

    public BrowserTabReadOnlyTransition ToggleReadOnly(int tabIndex)
    {
        if (!Tabs.IsValidIndex(tabIndex))
        {
            return BrowserTabReadOnlyTransition.NotApplied;
        }

        BrowserTabState state = Tabs.Tabs[tabIndex];
        state.IsReadOnly = !state.IsReadOnly;
        return new BrowserTabReadOnlyTransition(true, tabIndex, state.IsReadOnly);
    }

    public BrowserLocationChangeTransition PrepareLocationChange(string? targetPath, string currentPath, int maxTabCount)
    {
        BrowserTabState? state = ActiveTab;
        if (state == null || !state.IsLocked)
        {
            return BrowserLocationChangeTransition.Allow;
        }
        if (!string.IsNullOrWhiteSpace(targetPath) && QuickAccessService.PathsEqual(targetPath, currentPath))
        {
            return BrowserLocationChangeTransition.Allow;
        }
        if (!string.IsNullOrWhiteSpace(targetPath) && IsPathUnderStartupPathCore(targetPath, state))
        {
            return BrowserLocationChangeTransition.Allow;
        }
        if (Tabs.Count >= Math.Max(1, maxTabCount))
        {
            return BrowserLocationChangeTransition.Denied;
        }
        return BrowserLocationChangeTransition.RequiresDerivedTab;
    }

    public int ResolveAdjacentTabIndex(int delta, bool wrap)
    {
        if (Tabs.Count <= 1)
        {
            return -1;
        }
        int next = Tabs.ActiveTabIndex + delta;
        if (wrap)
        {
            return (next + Tabs.Count) % Tabs.Count;
        }
        return next >= 0 && next < Tabs.Count ? next : -1;
    }

    public int CountClosableTabs(BrowserTabCloseScope scope)
    {
        int count = 0;
        for (int index = 0; index < Tabs.Count; index++)
        {
            bool included = scope switch
            {
                BrowserTabCloseScope.Left => index < Tabs.ActiveTabIndex,
                BrowserTabCloseScope.Right => index > Tabs.ActiveTabIndex,
                BrowserTabCloseScope.Other => index != Tabs.ActiveTabIndex,
                _ => true
            };
            if (included && !Tabs.Tabs[index].IsLocked)
            {
                count++;
            }
        }
        return count;
    }

    public BrowserTabCloseDecision EvaluateTabClose(int tabIndex)
    {
        if (!Tabs.IsValidIndex(tabIndex))
        {
            return BrowserTabCloseDecision.Invalid;
        }
        if (Tabs.Tabs[tabIndex].IsLocked)
        {
            return BrowserTabCloseDecision.Locked;
        }
        if (Tabs.Count > 1)
        {
            return BrowserTabCloseDecision.Allowed;
        }

        return string.Equals(ActiveCategoryId, BrowserTabSettings.DefaultCategoryId, StringComparison.OrdinalIgnoreCase)
            ? BrowserTabCloseDecision.LastTab
            : new BrowserTabCloseDecision(
                BrowserTabCloseDecisionKind.RequiresCategoryRemoval,
                ActiveCategoryId);
    }

    public bool IsClosableTab(int tabIndex)
    {
        return Tabs.IsValidIndex(tabIndex) && !Tabs.Tabs[tabIndex].IsLocked;
    }

    public IReadOnlyList<int> GetClosableTabIndices(IEnumerable<int> candidates)
    {
        return candidates
            .Distinct()
            .Where(IsClosableTab)
            .OrderByDescending(static index => index)
            .ToList();
    }

    public BrowserTabCloseTransition RemoveTab(int tabIndex)
    {
        if (!IsClosableTab(tabIndex))
        {
            return BrowserTabCloseTransition.NotApplied;
        }

        Tabs.RemoveAt(tabIndex);
        return new BrowserTabCloseTransition(
            true,
            Math.Clamp(tabIndex - 1, 0, Tabs.Count - 1));
    }

    public BrowserTabRangeCloseTransition RemoveTabs(
        IReadOnlyList<int> candidateIndices,
        BrowserTabState preferredTab,
        int preferredTabIndex)
    {
        IReadOnlyList<int> closableIndices = GetClosableTabIndices(candidateIndices);
        if (closableIndices.Count == 0)
        {
            return BrowserTabRangeCloseTransition.NotApplied;
        }

        foreach (int index in closableIndices)
        {
            Tabs.RemoveAt(index);
        }

        int targetIndex = Tabs.IndexOf(preferredTab);
        if (targetIndex < 0)
        {
            targetIndex = Math.Clamp(preferredTabIndex, 0, Tabs.Count - 1);
        }
        return new BrowserTabRangeCloseTransition(true, targetIndex, closableIndices);
    }

    public BrowserTabRangeCloseTransition RemoveTabs(
        IReadOnlyList<int> candidateIndices,
        int preferredTabIndex)
    {
        return RemoveTabs(candidateIndices, Tabs.IsValidIndex(preferredTabIndex)
            ? Tabs.Tabs[preferredTabIndex]
            : new BrowserTabState(), preferredTabIndex);
    }

    public BrowserTabBatchIdRemovalTransition RemoveTabsByIds(IReadOnlyCollection<Guid> tabIds)
    {
        if (tabIds.Count == 0)
        {
            return BrowserTabBatchIdRemovalTransition.NotApplied;
        }

        List<int> indices = Tabs.Tabs
            .Select((tab, index) => (tab, index))
            .Where(item => tabIds.Contains(item.tab.Id))
            .Select(item => item.index)
            .OrderByDescending(static index => index)
            .ToList();
        if (indices.Any(index => Tabs.Tabs[index].IsLocked))
        {
            return BrowserTabBatchIdRemovalTransition.Locked;
        }
        if (indices.Count == 0)
        {
            return BrowserTabBatchIdRemovalTransition.NotApplied;
        }

        BrowserTabState? activeTab = Tabs.ActiveTab;
        foreach (int index in indices)
        {
            Tabs.RemoveAt(index);
        }

        int targetIndex = activeTab != null
            ? Tabs.IndexOf(activeTab)
            : -1;
        if (targetIndex < 0)
        {
            targetIndex = Math.Clamp(indices[^1] - 1, 0, Math.Max(0, Tabs.Count - 1));
        }
        Tabs.ActiveTabIndex = targetIndex;
        return new BrowserTabBatchIdRemovalTransition(true, indices.Count, targetIndex);
    }

    public BrowserTabBatchIdRemovalTransition RemoveTabsByIds(
        string categoryId,
        SessionSettings session,
        IReadOnlyCollection<Guid> tabIds)
    {
        if (FindCategory(categoryId) == null)
        {
            return BrowserTabBatchIdRemovalTransition.MissingCategory;
        }

        if (string.Equals(ActiveCategoryId, categoryId, StringComparison.OrdinalIgnoreCase))
        {
            return RemoveTabsByIds(tabIds);
        }

        BrowserTabRestoreCategoryState? categoryState = FindRestoreCategoryState(session, categoryId);
        if (categoryState == null)
        {
            return BrowserTabBatchIdRemovalTransition.MissingCategory;
        }

        List<BrowserTabSessionState> tabs = categoryState.OpenTabs ?? new List<BrowserTabSessionState>();
        List<int> indices = tabs
            .Select((tab, index) => (tab, index))
            .Where(item => tabIds.Contains(item.tab.TabId))
            .Select(item => item.index)
            .OrderByDescending(static index => index)
            .ToList();
        if (indices.Any(index => tabs[index].IsLocked))
        {
            return BrowserTabBatchIdRemovalTransition.Locked;
        }
        if (indices.Count == 0)
        {
            return BrowserTabBatchIdRemovalTransition.NotApplied;
        }

        int previousActiveIndex = categoryState.ActiveTabIndex;
        Guid? previousActiveTabId = previousActiveIndex >= 0 && previousActiveIndex < tabs.Count
            ? tabs[previousActiveIndex].TabId
            : null;
        foreach (int index in indices)
        {
            tabs.RemoveAt(index);
        }

        int targetIndex = previousActiveTabId is Guid activeTabId
            ? tabs.FindIndex(tab => tab.TabId == activeTabId)
            : -1;
        if (targetIndex < 0)
        {
            targetIndex = Math.Clamp(indices[^1] - 1, 0, Math.Max(0, tabs.Count - 1));
        }
        categoryState.OpenTabs = tabs;
        categoryState.ActiveTabIndex = tabs.Count == 0 ? 0 : targetIndex;
        UpdateCategorySessionState(session, categoryState);
        return new BrowserTabBatchIdRemovalTransition(true, indices.Count, categoryState.ActiveTabIndex);
    }

    public BrowserTabCategoryBatchCreationTransition CreateTabsForCategory(
        string categoryId,
        SessionSettings session,
        IReadOnlyList<BrowserTabState> sourceTabs,
        IReadOnlyList<int> insertionIndices,
        Guid? beforeActiveTabId,
        int beforeActiveTabIndex,
        int maxTabCount)
    {
        if (FindCategory(categoryId) == null)
        {
            return BrowserTabCategoryBatchCreationTransition.MissingCategory;
        }

        if (string.Equals(ActiveCategoryId, categoryId, StringComparison.OrdinalIgnoreCase))
        {
            return BrowserTabCategoryBatchCreationTransition.NotApplied;
        }

        BrowserTabRestoreCategoryState? categoryState = FindRestoreCategoryState(session, categoryId);
        if (categoryState == null)
        {
            return BrowserTabCategoryBatchCreationTransition.MissingCategory;
        }

        List<BrowserTabSessionState> tabs = categoryState.OpenTabs ?? new List<BrowserTabSessionState>();
        if (sourceTabs.Count == 0 || sourceTabs.Count != insertionIndices.Count ||
            tabs.Count + sourceTabs.Count > Math.Max(1, maxTabCount))
        {
            return BrowserTabCategoryBatchCreationTransition.CapacityLimitReached;
        }

        var createdTabs = sourceTabs.Select(static tab => tab.CloneWithNewId()).ToList();
        var actualInsertionIndices = new List<int>(createdTabs.Count);
        for (int index = 0; index < createdTabs.Count; index++)
        {
            int insertionIndex = Math.Clamp(insertionIndices[index], 0, tabs.Count);
            tabs.Insert(insertionIndex, CreateSessionState(createdTabs[index]));
            actualInsertionIndices.Add(insertionIndex);
        }

        int activeIndex = beforeActiveTabId is Guid activeTabId
            ? tabs.FindIndex(tab => tab.TabId == activeTabId)
            : -1;
        if (activeIndex < 0)
        {
            activeIndex = Math.Clamp(beforeActiveTabIndex, 0, Math.Max(0, tabs.Count - 1));
        }
        categoryState.OpenTabs = tabs;
        categoryState.ActiveTabIndex = tabs.Count == 0 ? 0 : activeIndex;
        UpdateCategorySessionState(session, categoryState);
        return new BrowserTabCategoryBatchCreationTransition(
            true,
            createdTabs,
            actualInsertionIndices);
    }

    public bool HasLockedTab(int tabIndex) => Tabs.IsValidIndex(tabIndex) && Tabs.Tabs[tabIndex].IsLocked;
    public bool HasReadOnlyTab(int tabIndex) => Tabs.IsValidIndex(tabIndex) && Tabs.Tabs[tabIndex].IsReadOnly;
    public bool IsPathUnderStartupPath(string targetPath, BrowserTabState state) => IsPathUnderStartupPathCore(targetPath, state);

    public BrowserLockedRootResolution ResolveActiveLockedRoot()
    {
        BrowserTabState? state = Tabs.ActiveTab;
        if (state == null || !state.IsLocked || string.IsNullOrWhiteSpace(state.StartupPath))
        {
            return BrowserLockedRootResolution.NotLocked;
        }

        string lockRootPath = state.StartupPath;
        if (Directory.Exists(lockRootPath))
        {
            return new BrowserLockedRootResolution(BrowserLockedRootResolutionKind.Resolved, lockRootPath);
        }

        string normalized = _session.NavigationState.Navigation.NormalizeDestinationDirectory(lockRootPath);
        if (Directory.Exists(normalized))
        {
            state.StartupPath = normalized;
            return new BrowserLockedRootResolution(BrowserLockedRootResolutionKind.Resolved, normalized);
        }

        return BrowserLockedRootResolution.Missing;
    }

    private static string NormalizeDestinationDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }
        try
        {
            string fullPath = Path.GetFullPath(path);
            return fullPath.Length <= 3 && fullPath.EndsWith(Path.DirectorySeparatorChar)
                ? fullPath
                : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path;
        }
    }

    private static bool IsPathUnderStartupPathCore(string targetPath, BrowserTabState state)
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
            string normalizedStartup = Path.GetFullPath(startupPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
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

    private static bool TryResolveRestorePath(
        BrowserTabSessionState sessionTab,
        out string restorePath,
        out string? statusMessage)
    {
        restorePath = string.Empty;
        statusMessage = null;
        string currentPath = sessionTab.CurrentPath ?? string.Empty;
        string startupPath = sessionTab.StartupPath ?? string.Empty;
        if (sessionTab.IsLocked && !string.IsNullOrWhiteSpace(startupPath) && Directory.Exists(startupPath))
        {
            if (!string.IsNullOrWhiteSpace(currentPath) && Directory.Exists(currentPath) &&
                IsPathUnderStartupPathCore(currentPath, new BrowserTabState { StartupPath = startupPath }))
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
                statusMessage = "固定タブの起動元が見つからないため、最後の場所を開きました。";
            }
            return true;
        }

        if (sessionTab.IsLocked && TryFindExistingParentDirectory(startupPath, out string parentPath))
        {
            restorePath = parentPath;
            statusMessage = "固定タブの起動元が見つからないため、親フォルダを開きました。";
            return true;
        }

        if (sessionTab.IsLocked)
        {
            restorePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(restorePath) || !Directory.Exists(restorePath))
            {
                restorePath = AppContext.BaseDirectory;
            }
            statusMessage = "固定タブの起動元が見つからないため、代替フォルダを開きました。";
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

    private static void ReorderCategoryState<T>(IList<T>? states, string categoryId, int targetIndex)
        where T : class
    {
        if (states == null)
        {
            return;
        }

        int sourceIndex = -1;
        for (int index = 0; index < states.Count; index++)
        {
            string? stateCategoryId = states[index] switch
            {
                BrowserTabCategorySessionState sessionState => sessionState.CategoryId,
                BrowserTabRestoreCategoryState restoreState => restoreState.Id,
                _ => null
            };
            if (string.Equals(stateCategoryId, categoryId, StringComparison.OrdinalIgnoreCase))
            {
                sourceIndex = index;
                break;
            }
        }

        if (sourceIndex < 0)
        {
            return;
        }

        T moved = states[sourceIndex];
        states.RemoveAt(sourceIndex);
        states.Insert(Math.Min(targetIndex, states.Count), moved);
    }

    private int ClearCurrentSelection(ref bool changed)
    {
        int count = _session.Selection.Count;
        if (count > 0)
        {
            _session.Selection.Clear();
            changed = true;
        }
        return count;
    }

    private static int ClearMarkPaths(List<string>? paths, ref bool changed)
    {
        if (paths == null || paths.Count == 0)
        {
            return 0;
        }

        int count = paths.Count;
        paths.Clear();
        changed = true;
        return count;
    }

    private static List<BrowserTabCategorySessionState> BuildCategorySessionStatesFromSnapshot(
        BrowserTabRestoreSnapshot snapshot)
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

    private static List<BrowserTabCategoryDefinition> BuildCategoryDefinitionsFromSnapshot(
        BrowserTabRestoreSnapshot snapshot)
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

    private static void UpdateCategorySessionState(
        SessionSettings session,
        BrowserTabRestoreCategoryState categoryState)
    {
        session.BrowserTabCategories = UpsertBrowserTabCategorySessionState(
            session.BrowserTabCategories,
            new BrowserTabCategorySessionState
            {
                CategoryId = categoryState.Id,
                OpenTabs = (categoryState.OpenTabs ?? new List<BrowserTabSessionState>())
                    .Select(static tab => tab.Clone())
                    .ToList(),
                ActiveTabIndex = categoryState.ActiveTabIndex
            });
    }

    private static BrowserTabSessionState CreateSessionState(BrowserTabState tabState)
    {
        NavigationService.NavigationSnapshot navigation = tabState.Navigation ?? new NavigationService.NavigationSnapshot();
        List<string> persistedMarks = tabState.MarksDirty
            ? CreatePersistableMarkedPaths(tabState.MarkedPaths, out _)
            : tabState.MarkedPaths.ToList();
        if (tabState.MarksDirty)
        {
            tabState.MarkedPaths = persistedMarks;
            tabState.MarksDirty = false;
        }

        return new BrowserTabSessionState
        {
            TabId = tabState.Id == Guid.Empty ? Guid.NewGuid() : tabState.Id,
            CurrentPath = tabState.CurrentPath,
            IsLocked = tabState.IsLocked,
            StartupPath = tabState.StartupPath,
            IsReadOnly = tabState.IsReadOnly,
            FilterLock = tabState.FilterLock?.Clone() ?? new TabFilterLockState(),
            FilterPattern = tabState.FilterPattern,
            FilterUseRegex = tabState.FilterUseRegex,
            MarkedPaths = persistedMarks,
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

    private static List<string> CreatePersistableMarkedPaths(
        IEnumerable<string>? paths,
        out int skippedCount)
    {
        skippedCount = 0;
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? path in paths ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
            {
                skippedCount++;
                continue;
            }
            if (seen.Add(path))
            {
                result.Add(path);
            }
        }
        return result;
    }
}

internal enum BrowserTabCloseScope
{
    All,
    Left,
    Right,
    Other
}

internal readonly record struct BrowserTabLockTransition(
    bool Applied,
    int TabIndex,
    bool IsLocked,
    string StartupPath)
{
    public static BrowserTabLockTransition NotApplied => new(false, -1, false, string.Empty);
}

internal readonly record struct BrowserTabReadOnlyTransition(
    bool Applied,
    int TabIndex,
    bool IsReadOnly)
{
    public static BrowserTabReadOnlyTransition NotApplied => new(false, -1, false);
}

internal readonly record struct BrowserMarksClearTransition(bool Changed, int ClearedCount);

internal sealed record BrowserWorkspaceRuntimeStateSnapshot(
    IReadOnlyList<BrowserTabCategoryDefinition> CategoryDefinitions,
    BrowserTabRestoreSnapshot RestoreSnapshot,
    string ActiveCategoryId);

internal readonly record struct BrowserTabSessionSerializationResult(
    IReadOnlyList<BrowserTabSessionState> Tabs,
    int ActiveTabIndex,
    bool WasTruncated);

internal readonly record struct BrowserTabRestoreResult(
    BrowserTabState? State,
    int SkippedMarkCount,
    string? StatusMessage)
{
    public bool Applied => State != null;

    public static BrowserTabRestoreResult NotApplied => new(null, 0, null);
}

internal readonly record struct BrowserTabListCategorySnapshot(
    string CategoryId,
    string DisplayName,
    bool IsActive,
    IReadOnlyList<BrowserTabListTabSnapshot> Tabs);

internal readonly record struct BrowserTabListTabSnapshot(
    int Index,
    string Title,
    string CurrentPath,
    bool IsLocked,
    bool IsReadOnly,
    bool IsSelected);

internal readonly record struct BrowserTabCreationTransition(
    bool Created,
    int Index,
    BrowserTabState? State)
{
    public static BrowserTabCreationTransition LimitReached => new(false, -1, null);
}

internal readonly record struct BrowserCategoryRemovalTransition(
    int RemovedCount,
    bool RequiresFallback,
    string? FallbackCategoryId,
    int FallbackCategoryIndex);

internal readonly record struct BrowserCategoryReorderTransition(
    bool Applied,
    string CategoryId,
    string DisplayName)
{
    public static BrowserCategoryReorderTransition NotApplied => new(false, string.Empty, string.Empty);
}

internal readonly record struct BrowserCategorySwitchTransition(
    string TargetCategoryId,
    bool SwitchTabOnly,
    int RequestedTabIndex);

internal readonly record struct BrowserTabCloseTransition(
    bool Applied,
    int TargetTabIndex)
{
    public static BrowserTabCloseTransition NotApplied => new(false, -1);
}

internal enum BrowserTabCloseDecisionKind
{
    Invalid,
    Locked,
    LastTab,
    RequiresCategoryRemoval,
    Allowed
}

internal readonly record struct BrowserTabCloseDecision(
    BrowserTabCloseDecisionKind Kind,
    string? CategoryId)
{
    public static BrowserTabCloseDecision Invalid => new(BrowserTabCloseDecisionKind.Invalid, null);
    public static BrowserTabCloseDecision Locked => new(BrowserTabCloseDecisionKind.Locked, null);
    public static BrowserTabCloseDecision LastTab => new(BrowserTabCloseDecisionKind.LastTab, null);
    public static BrowserTabCloseDecision Allowed => new(BrowserTabCloseDecisionKind.Allowed, null);
}

internal readonly record struct BrowserTabRangeCloseTransition(
    bool Applied,
    int TargetTabIndex,
    IReadOnlyList<int> RemovedIndices)
{
    public static BrowserTabRangeCloseTransition NotApplied =>
        new(false, -1, Array.Empty<int>());
}

internal readonly record struct BrowserTabBatchIdRemovalTransition(
    bool Applied,
    int RemovedCount,
    int TargetTabIndex,
    bool BlockedByLock = false,
    bool TargetCategoryMissing = false)
{
    public static BrowserTabBatchIdRemovalTransition NotApplied => new(false, 0, -1);
    public static BrowserTabBatchIdRemovalTransition Locked => new(false, 0, -1, true);
    public static BrowserTabBatchIdRemovalTransition MissingCategory => new(false, 0, -1, false, true);
}

internal readonly record struct BrowserTabCategoryBatchCreationTransition(
    bool Applied,
    IReadOnlyList<BrowserTabState> Items,
    IReadOnlyList<int> InsertionIndices,
    bool TargetCategoryMissing = false,
    bool CapacityExceeded = false)
{
    public static BrowserTabCategoryBatchCreationTransition NotApplied =>
        new(false, Array.Empty<BrowserTabState>(), Array.Empty<int>());

    public static BrowserTabCategoryBatchCreationTransition MissingCategory =>
        new(false, Array.Empty<BrowserTabState>(), Array.Empty<int>(), TargetCategoryMissing: true);

    public static BrowserTabCategoryBatchCreationTransition CapacityLimitReached =>
        new(false, Array.Empty<BrowserTabState>(), Array.Empty<int>(), CapacityExceeded: true);
}

internal enum BrowserLockedRootResolutionKind
{
    NotLocked,
    Resolved,
    Missing
}

internal readonly record struct BrowserLockedRootResolution(
    BrowserLockedRootResolutionKind Kind,
    string? Path)
{
    public static BrowserLockedRootResolution NotLocked =>
        new(BrowserLockedRootResolutionKind.NotLocked, null);

    public static BrowserLockedRootResolution Missing =>
        new(BrowserLockedRootResolutionKind.Missing, null);
}

internal enum BrowserClosedTabRestoreKind
{
    None,
    RequiresCategorySwitch,
    LimitReached,
    Restored
}

internal readonly record struct BrowserClosedTabRestoreTransition(
    BrowserClosedTabRestoreKind Kind,
    string? TargetCategoryId,
    BrowserTabState? RestoredTab)
{
    public static BrowserClosedTabRestoreTransition None =>
        new(BrowserClosedTabRestoreKind.None, null, null);
}

internal enum BrowserLocationChangeTransition
{
    Allow,
    RequiresDerivedTab,
    Denied
}

internal readonly record struct BrowserTabActivationTransition(
    bool Applied,
    int ColumnCount,
    SortKind SortKind,
    bool SortAscending,
    NavigationService.NavigationSnapshot Navigation,
    BrowserTabState State,
    IReadOnlyList<MarkPathKind> RestoredMarkKinds,
    int SkippedMarkCount)
{
    public int SnapshotMarkHitCount { get; init; }
    public int FallbackValidationCount { get; init; }
    public int FallbackFileProbeCount { get; init; }
    public int FallbackDirectoryProbeCount { get; init; }
    public int ParentBatchGroupCount { get; init; }
    public int ParentBatchPathCount { get; init; }
    public int ParentBatchHitCount { get; init; }
    public int ParentBatchMissCount { get; init; }
    public int ParentBatchFailureCount { get; init; }
    public bool SnapshotPathKindsAreFresh { get; init; }

    public static BrowserTabActivationTransition NotApplied =>
        new(
            false,
            1,
            SortKind.Name,
            true,
            new NavigationService.NavigationSnapshot(),
            new BrowserTabState(),
            Array.Empty<MarkPathKind>(),
            0);
}

internal sealed record BrowserMarkRestoreValidationContext(
    IReadOnlyDictionary<string, bool> SnapshotPathKinds,
    bool SnapshotPathKindsAreFresh,
    IMarkParentEnumerationProvider? ParentEnumeration = null);
