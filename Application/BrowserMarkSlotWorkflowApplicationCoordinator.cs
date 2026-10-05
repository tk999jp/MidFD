using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MidFD.Configuration;
using MidFD.Helpers;
using MidFD.Models;
using MidFD.Services;

namespace MidFD.Runtime;

/// <summary>
/// Browser mark-slot の業務意味・集計・永続化順序を所有する。
/// UI は表示名や確認結果などの plain input を渡し、結果だけを表示する。
/// </summary>
internal sealed class BrowserMarkSlotWorkflowApplicationCoordinator
{
    private readonly BrowserApplicationCoordinator _browser;
    private readonly BrowserTabWorkflowApplicationCoordinator _tabs;
    private readonly BrowserWorkspacePersistenceApplicationCoordinator _persistence;

    public BrowserMarkSlotWorkflowApplicationCoordinator(
        BrowserApplicationCoordinator browser,
        BrowserTabWorkflowApplicationCoordinator tabs,
        BrowserWorkspacePersistenceApplicationCoordinator persistence)
    {
        _browser = browser;
        _tabs = tabs;
        _persistence = persistence;
    }

    public MarkSlotEntry GetSlot(int slotNumber) => _persistence.GetMarkSlot(slotNumber);

    public MarkSlotStore GetSlots() => _persistence.GetMarkSlots();

    public string BuildPersistenceSummaryText()
    {
        _persistence.EnsureNormalized();
        int currentCount = _browser.Selection.Count;
        int savedCount = _persistence.GetPersistedMarkedPathCount();
        return _persistence.PersistMarksAcrossRestart
            ? $"再起動復元: ON / 現在 {currentCount} 件 / 保存済み {savedCount} 件{Environment.NewLine}終了時に保存し、次回起動時は存在する path だけ復元します"
            : $"再起動復元: OFF / 保存済み {savedCount} 件を保持中{Environment.NewLine}ON に戻すまで自動復元しません。";
    }

    public BrowserMarkSlotGlobalSummary BuildGlobalSummary(BrowserTabState activeState)
    {
        _tabs.SyncActiveStateAndPersist(activeState);
        BrowserTabRestoreSnapshot snapshot = _persistence.EnsureRestoreSnapshot();
        Guid? activeTabId = _browser.Workspace.ActiveTabSnapshot?.Id;
        string activeCategoryId = _browser.Workspace.ResolveCategoryId(_browser.Workspace.ActiveCategoryId);
        string currentCategoryName = "既定";
        int activeTabMarkCount = _browser.Selection.Count;
        int currentCategoryMarkCount = 0;
        int currentCategoryTabCount = 0;
        int globalMarkCount = 0;
        int globalTabCount = 0;

        foreach (BrowserTabRestoreCategoryState category in snapshot.Categories)
        {
            bool isCurrentCategory = string.Equals(category.Id, activeCategoryId, StringComparison.OrdinalIgnoreCase);
            if (isCurrentCategory)
            {
                currentCategoryName = string.IsNullOrWhiteSpace(category.DisplayName)
                    ? currentCategoryName
                    : category.DisplayName;
            }

            foreach (BrowserTabSessionState tab in category.OpenTabs ?? new List<BrowserTabSessionState>())
            {
                globalTabCount++;
                int markCount = isCurrentCategory && tab.TabId == activeTabId
                    ? activeTabMarkCount
                    : tab.MarkedPaths?.Count ?? 0;
                globalMarkCount += markCount;
                if (isCurrentCategory)
                {
                    currentCategoryTabCount++;
                    currentCategoryMarkCount += markCount;
                }
            }
        }

        if (snapshot.Categories.Count == 0)
        {
            currentCategoryTabCount = 1;
            globalTabCount = 1;
            currentCategoryMarkCount = activeTabMarkCount;
            globalMarkCount = activeTabMarkCount;
        }

        return new BrowserMarkSlotGlobalSummary(
            activeTabMarkCount,
            currentCategoryMarkCount,
            currentCategoryTabCount,
            currentCategoryName,
            globalMarkCount,
            snapshot.Categories.Count == 0 ? 1 : snapshot.Categories.Count,
            globalTabCount);
    }

    public BrowserMarkSlotSaveAggregation BuildCurrentCategoryAggregation(BrowserTabState activeState)
    {
        _tabs.SyncActiveStateAndPersist(activeState);
        BrowserTabRestoreSnapshot snapshot = _persistence.EnsureRestoreSnapshot();
        string categoryId = _browser.Workspace.ResolveCategoryId(_browser.Workspace.ActiveCategoryId);
        BrowserTabRestoreCategoryState? category = snapshot.Categories.FirstOrDefault(
            item => string.Equals(item.Id, categoryId, StringComparison.OrdinalIgnoreCase));
        if (category == null)
        {
            return new BrowserMarkSlotSaveAggregation(
                MarkSlotSourceScopes.CurrentCategory,
                "現在カテゴリ",
                categoryId,
                "既定",
                1,
                0,
                0,
                Array.Empty<string>());
        }

        return BuildAggregation(
            MarkSlotSourceScopes.CurrentCategory,
            "現在カテゴリ",
            categoryId,
            string.IsNullOrWhiteSpace(category.DisplayName) ? "既定" : category.DisplayName,
            1,
            category.OpenTabs);
    }

    public BrowserMarkSlotSaveAggregation BuildWorkspaceAggregation(BrowserTabState activeState)
    {
        _tabs.SyncActiveStateAndPersist(activeState);
        BrowserTabRestoreSnapshot snapshot = _persistence.EnsureRestoreSnapshot();
        return BuildAggregation(
            MarkSlotSourceScopes.Workspace,
            "全Workspace",
            null,
            null,
            snapshot.Categories.Count,
            snapshot.Categories.SelectMany(static category => category.OpenTabs ?? new List<BrowserTabSessionState>()));
    }

    public MarkSlotActionResult SaveCurrentTab(
        int slotNumber,
        string? displayName,
        IReadOnlyList<string> paths,
        BrowserTabState activeState,
        string? categoryName,
        string? tabDisplayName,
        int slotCount)
    {
        MarkSlotEntry slot = GetSlot(slotNumber);
        slot.DisplayName = NormalizeDisplayName(displayName, slotNumber);
        slot.SavedAtUtc = DateTime.UtcNow;
        slot.Paths = DistinctPaths(paths);
        slot.SourceScope = MarkSlotSourceScopes.CurrentTab;
        slot.SourceCategoryId = _browser.Workspace.ResolveCategoryId(_browser.Workspace.ActiveCategoryId);
        slot.SourceCategoryName = categoryName;
        slot.SourceTabId = activeState.Id;
        slot.SourceTabDisplayName = tabDisplayName;
        if (!_persistence.SaveMarkSlots(slot, slotCount))
        {
            return MarkSlotSaveFailed(slotNumber);
        }
        return new MarkSlotActionResult(true, $"マークスロット {slotNumber} に保存しました ({slot.Paths.Count}件)");
    }

    public MarkSlotActionResult SaveAggregation(
        int slotNumber,
        string? displayName,
        BrowserMarkSlotSaveAggregation aggregation,
        int slotCount)
    {
        MarkSlotEntry slot = GetSlot(slotNumber);
        string defaultName = BuildDefaultDisplayName(slot, aggregation.SourceScope, aggregation.SourceCategoryName);
        slot.DisplayName = string.IsNullOrWhiteSpace(displayName) ? defaultName : displayName.Trim();
        slot.SavedAtUtc = DateTime.UtcNow;
        slot.Paths = aggregation.Paths.ToList();
        slot.SourceScope = aggregation.SourceScope;
        slot.SourceCategoryId = aggregation.SourceCategoryId;
        slot.SourceCategoryName = aggregation.SourceCategoryName;
        slot.SourceTabId = null;
        slot.SourceTabDisplayName = null;
        if (!_persistence.SaveMarkSlots(slot, slotCount))
        {
            return MarkSlotSaveFailed(slotNumber);
        }
        string scopeText = string.Equals(aggregation.SourceScope, MarkSlotSourceScopes.CurrentCategory, StringComparison.Ordinal)
            ? "現在カテゴリの全マーク"
            : "Workspace全体の全マーク";
        return new MarkSlotActionResult(
            true,
            $"マークスロット {slotNumber} に{scopeText}を保存しました (raw {aggregation.RawMarkCount}件 / 保存 {aggregation.UniquePathCount}件)");
    }

    public MarkSlotActionResult RestoreToCurrentTab(int slotNumber, BrowserTabState currentState)
    {
        MarkSlotEntry slot = GetSlot(slotNumber);
        List<string> slotPaths = DistinctPaths(slot.Paths);
        if (slotPaths.Count == 0)
        {
            return new MarkSlotActionResult(false, $"マークスロット {slotNumber} は空です。");
        }

        List<MarkPathKind> restoredMarkKinds = ValidateExistingPaths(slotPaths, out int missingCount);
        List<string> restoredPaths = restoredMarkKinds.Select(static entry => entry.Path).ToList();
        _tabs.ClearMarksAndSync();
        _tabs.RestoreMarksAndSync(restoredMarkKinds);
        BrowserTabState persistedState = currentState.Clone();
        persistedState.MarkedPaths = _browser.Selection.Snapshot().ToList();
        _tabs.PersistAfterMarkMutation(persistedState);
        string message = missingCount > 0
            ? $"マークスロット {slotNumber} を復元しました ({restoredPaths.Count}件 / {missingCount}件見つからず)"
            : $"マークスロット {slotNumber} を復元しました ({restoredPaths.Count}件)";
        return new MarkSlotActionResult(true, message);
    }

    public MarkSlotActionResult Rename(int slotNumber, string? displayName, int slotCount)
    {
        MarkSlotEntry slot = GetSlot(slotNumber);
        slot.DisplayName = NormalizeDisplayName(displayName, slotNumber);
        if (!_persistence.SaveMarkSlots(slot, slotCount))
        {
            return MarkSlotSaveFailed(slotNumber);
        }
        return new MarkSlotActionResult(true, $"マークスロット {slotNumber} の名前を更新しました");
    }

    public MarkSlotActionResult Delete(int slotNumber, int slotCount)
    {
        MarkSlotEntry slot = GetSlot(slotNumber);
        ResetSlot(slot);
        if (!_persistence.SaveMarkSlots(slot, slotCount))
        {
            return MarkSlotSaveFailed(slotNumber);
        }
        return new MarkSlotActionResult(true, $"マークスロット {slotNumber} を削除しました");
    }

    public MarkSlotActionResult RemoveItems(int slotNumber, IReadOnlyCollection<string> paths, int slotCount)
    {
        if (paths == null || paths.Count == 0)
        {
            return new MarkSlotActionResult(false, "削除対象のパスが指定されていません。");
        }

        MarkSlotEntry slot = GetSlot(slotNumber);
        HashSet<string> targetSet = new(paths, StringComparer.OrdinalIgnoreCase);
        int initialCount = slot.Paths.Count;
        slot.Paths.RemoveAll(path => targetSet.Contains(path));
        int removedCount = initialCount - slot.Paths.Count;
        if (removedCount == 0)
        {
            return new MarkSlotActionResult(false, "削除対象のパスがスロット内に見つかりません。");
        }

        slot.SavedAtUtc = DateTime.UtcNow;
        if (!_persistence.SaveMarkSlots(slot, slotCount))
        {
            return MarkSlotSaveFailed(slotNumber);
        }
        return new MarkSlotActionResult(true, $"マークスロット {slotNumber} から {removedCount} 件の項目を削除しました。");
    }

    public MarkSlotSetOperationPreviewResult BuildSetOperationPreview(
        int slotANumber,
        int slotBNumber,
        string operationKind,
        string currentPath)
    {
        MarkSlotEntry slotA = GetSlot(slotANumber);
        MarkSlotEntry slotB = GetSlot(slotBNumber);
        List<string> slotAPaths = DistinctPaths(slotA.Paths, removeBlank: true);
        List<string> slotBPaths = DistinctPaths(slotB.Paths, removeBlank: true);
        HashSet<string> aSet = new(slotAPaths, StringComparer.OrdinalIgnoreCase);
        HashSet<string> bSet = new(slotBPaths, StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> result = operationKind switch
        {
            MarkSlotSetOperations.And => slotAPaths.Where(bSet.Contains),
            MarkSlotSetOperations.AMinusB => slotAPaths.Where(path => !bSet.Contains(path)),
            MarkSlotSetOperations.BMinusA => slotBPaths.Where(path => !aSet.Contains(path)),
            MarkSlotSetOperations.Xor => slotAPaths.Where(path => !bSet.Contains(path)).Concat(slotBPaths.Where(path => !aSet.Contains(path))),
            _ => slotAPaths.Concat(slotBPaths.Where(path => !aSet.Contains(path)))
        };
        List<string> resultPaths = result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string currentDir = NavigationService.NormalizeDirectoryForCompare(currentPath);
        List<MarkSlotSetOperationPreviewItem> previewItems = resultPaths
            .Select(path =>
            {
                string? parentDir = Path.GetDirectoryName(path);
                bool inCurrentDirectory = string.Equals(
                    NavigationService.NormalizeDirectoryForCompare(parentDir ?? string.Empty),
                    currentDir,
                    StringComparison.OrdinalIgnoreCase);
                string name = Path.GetFileName(path);
                return new MarkSlotSetOperationPreviewItem(
                    string.IsNullOrWhiteSpace(name) ? path : name,
                    path,
                    inCurrentDirectory,
                    PathExists(path));
            })
            .OrderByDescending(static item => item.IsInCurrentDirectory)
            .ThenByDescending(static item => item.Exists)
            .ThenBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        int currentDirectoryCount = previewItems.Count(item => item.IsInCurrentDirectory);
        return new MarkSlotSetOperationPreviewResult(
            slotANumber,
            GetDisplayName(slotA),
            slotAPaths.Count,
            slotBNumber,
            GetDisplayName(slotB),
            slotBPaths.Count,
            operationKind,
            GetOperationLabel(operationKind),
            resultPaths,
            previewItems,
            currentDirectoryCount,
            previewItems.Count - currentDirectoryCount,
            previewItems.Count(item => !item.Exists));
    }

    public MarkSlotActionResult SaveSetOperation(
        MarkSlotSetOperationSaveRequest request,
        string? displayName,
        int slotCount)
    {
        List<string> paths = DistinctPaths(request.ResultPaths, removeBlank: true);
        if (paths.Count == 0)
        {
            return new MarkSlotActionResult(false, "0件の演算結果は保存できません。");
        }

        MarkSlotEntry slot = GetSlot(request.TargetSlotNumber);
        slot.DisplayName = string.IsNullOrWhiteSpace(displayName)
            ? BuildSetOperationDefaultName(request.SlotANumber, request.SlotBNumber, request.OperationKind)
            : displayName.Trim();
        slot.SavedAtUtc = DateTime.UtcNow;
        slot.Paths = paths;
        slot.SourceScope = MarkSlotSourceScopes.SlotSetOperation;
        slot.SourceCategoryId = null;
        slot.SourceCategoryName = null;
        slot.SourceTabId = null;
        slot.SourceTabDisplayName = null;
        if (!_persistence.SaveMarkSlots(slot, slotCount))
        {
            return MarkSlotSaveFailed(request.TargetSlotNumber);
        }
        return new MarkSlotActionResult(true, $"演算結果をマークスロット {request.TargetSlotNumber} に保存しました ({paths.Count}件)");
    }

    public bool TryExportSlot(string filePath, int slotNumber, out string errorMessage) =>
        MarkSlotStorage.TryExportSlot(filePath, GetSlot(slotNumber), out errorMessage);

    public bool TryImportSlot(string filePath, out MarkSlotEntry? slot, out string errorMessage, out string? warningMessage) =>
        MarkSlotStorage.TryImportSlot(filePath, out slot, out errorMessage, out warningMessage);

    public MarkSlotActionResult ImportSlot(int slotNumber, MarkSlotEntry importedSlot, int slotCount)
    {
        MarkSlotEntry target = GetSlot(slotNumber);
        target.DisplayName = GetDisplayName(importedSlot);
        target.SavedAtUtc = importedSlot.SavedAtUtc;
        target.Paths = DistinctPaths(importedSlot.Paths);
        target.SourceScope = importedSlot.SourceScope;
        target.SourceCategoryId = importedSlot.SourceCategoryId;
        target.SourceCategoryName = importedSlot.SourceCategoryName;
        target.SourceTabId = importedSlot.SourceTabId;
        target.SourceTabDisplayName = importedSlot.SourceTabDisplayName;
        if (!_persistence.SaveMarkSlots(target, slotCount))
        {
            return MarkSlotSaveFailed(slotNumber);
        }
        return new MarkSlotActionResult(true, $"マークスロット {slotNumber} にインポートしました ({target.Paths.Count}件)");
    }

    public bool TryExportAllSlots(string filePath, int slotCount, out string errorMessage) =>
        MarkSlotStorage.TryExportAllSlots(filePath, GetSlots(), slotCount, out errorMessage);

    public bool TryImportAllSlots(string filePath, int slotCount, out MarkSlotStore? store, out string errorMessage, out string? warningMessage) =>
        MarkSlotStorage.TryImportAllSlots(filePath, slotCount, out store, out errorMessage, out warningMessage);

    public bool ReplaceSlots(MarkSlotStore store, int slotCount) => _persistence.ReplaceMarkSlots(store, slotCount);

    public MarkSlotActionResult ApplySetOperationToCurrentTab(
        MarkSlotSetOperationPreviewResult preview,
        BrowserTabState currentState)
    {
        List<MarkPathKind> restoredMarkKinds = ValidateExistingPaths(preview.ResultPaths, out int missingCount);
        List<string> restoredPaths = restoredMarkKinds.Select(static entry => entry.Path).ToList();
        _tabs.ClearMarksAndSync();
        _tabs.RestoreMarksAndSync(restoredMarkKinds);
        BrowserTabState persistedState = currentState.Clone();
        persistedState.MarkedPaths = _browser.Selection.Snapshot().ToList();
        _tabs.PersistAfterMarkMutation(persistedState);
        string message = missingCount > 0
            ? $"演算結果を現在タブへ適用しました ({restoredPaths.Count}件 / {missingCount}件見つからず)"
            : $"演算結果を現在タブへ適用しました ({restoredPaths.Count}件)";
        return new MarkSlotActionResult(true, message);
    }

    public MarkSlotClipboardActionResult ApplyClipboardReplacement(
        MarkSlotClipboardImportResult importResult,
        string? repositoryRoot,
        BrowserTabState currentState)
    {
        MarkSlotClipboardActionResult result = BuildClipboardReplacementResult(importResult, repositoryRoot);
        if (!result.Success)
        {
            return result;
        }

        _tabs.ReplaceMarksAndSync(result.Paths.Select(static path => new MarkPathKind(path, IsDirectory: false)));
        BrowserTabState persistedState = currentState.Clone();
        persistedState.MarkedPaths = _browser.Selection.Snapshot().ToList();
        _tabs.PersistAfterMarkMutation(persistedState);
        return result;
    }

    internal static MarkSlotClipboardActionResult BuildClipboardReplacementResult(
        MarkSlotClipboardImportResult importResult,
        string? repositoryRoot)
    {
        if (importResult.FailureReason == MarkSlotClipboardImportFailureReason.NoChangesDeclared)
        {
            return new MarkSlotClipboardActionResult(
                false,
                "このKDSL_RESULTには取り込み対象の変更ファイルがありません。現在MarkとMarkSlotは変更していません。",
                Array.Empty<string>(),
                repositoryRoot,
                importResult.MissingFileCount,
                importResult.DirectoryPathCount,
                importResult.DuplicatePathCount,
                importResult.IgnoredEarlierResultCount,
                importResult.UnresolvedPaths,
                IsNoOp: true);
        }

        IReadOnlyList<string> replacementPaths = importResult.Paths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!importResult.IsSuccess || replacementPaths.Count == 0)
        {
            return BuildClipboardImportFailure(importResult, repositoryRoot);
        }

        int unresolvedCount = importResult.UnresolvedPaths?.Count ?? 0;
        string unresolvedInfo = unresolvedCount > 0 ? $"（未解決{unresolvedCount}件）" : string.Empty;
        return new MarkSlotClipboardActionResult(
            true,
            $"RESULTのpathで現在Markを置換しました（取込後{replacementPaths.Count}件）{unresolvedInfo}。MarkSlotは変更していません。",
            replacementPaths,
            repositoryRoot,
            importResult.MissingFileCount,
            importResult.DirectoryPathCount,
            importResult.DuplicatePathCount,
            importResult.IgnoredEarlierResultCount,
            importResult.UnresolvedPaths);
    }

    internal static MarkSlotClipboardActionResult BuildClipboardImportFailure(
        MarkSlotClipboardImportResult importResult,
        string? repositoryRoot)
    {
        string message = importResult.FailureReason switch
        {
            MarkSlotClipboardImportFailureReason.KdslResultNotFound => "KDSL_RESULTが見つかりません。",
            MarkSlotClipboardImportFailureReason.KdslResultFenceUnclosed => "KDSL_RESULTのfenceが閉じられていません。",
            MarkSlotClipboardImportFailureReason.MalformedChangeSection => "KDSL_RESULTの「変更:」セクション形式を認識できません。「変更:」は独立した行で出力してください。",
            MarkSlotClipboardImportFailureReason.NoExplicitFiles => "「変更:」に変更ファイルが列挙されていません。",
            MarkSlotClipboardImportFailureReason.NoValidExistingFiles => $"Mark可能な既存fileがありません（削除・不存在{importResult.MissingFileCount}件、directory{importResult.DirectoryPathCount}件）。",
            MarkSlotClipboardImportFailureReason.InvalidEntriesDetected => "変更entryに構文不正またはrepo外pathがあります。",
            _ => "RESULTをMarkへ適用できませんでした。"
        };

        return new MarkSlotClipboardActionResult(
            false,
            $"{message}現在MarkとMarkSlotは変更していません。",
            Array.Empty<string>(),
            repositoryRoot,
            importResult.MissingFileCount,
            importResult.DirectoryPathCount,
            importResult.DuplicatePathCount,
            importResult.IgnoredEarlierResultCount,
            importResult.UnresolvedPaths);
    }

    internal static MarkSlotClipboardActionResult BuildClipboardImportFailure(
        string message,
        string? repositoryRoot) =>
        new(false, message, Array.Empty<string>(), repositoryRoot, 0, 0, 0, 0);

    public static string BuildSetOperationDefaultName(int slotANumber, int slotBNumber, string operationKind)
    {
        string operationText = operationKind switch
        {
            MarkSlotSetOperations.And => "AND",
            MarkSlotSetOperations.AMinusB => "-",
            MarkSlotSetOperations.BMinusA => "逆差",
            MarkSlotSetOperations.Xor => "XOR",
            _ => "OR"
        };
        return operationKind switch
        {
            MarkSlotSetOperations.BMinusA => $"Slot{slotBNumber} - Slot{slotANumber}",
            _ when operationText == "-" => $"Slot{slotANumber} - Slot{slotBNumber}",
            _ => $"Slot{slotANumber} {operationText} Slot{slotBNumber}"
        };
    }

    public static string GetOperationLabel(string operationKind) => operationKind switch
    {
        MarkSlotSetOperations.And => "AND",
        MarkSlotSetOperations.AMinusB => "A-B",
        MarkSlotSetOperations.BMinusA => "B-A",
        MarkSlotSetOperations.Xor => "XOR",
        _ => "OR"
    };

    internal static List<string> CreatePersistableMarkedPaths(
        IEnumerable<string>? paths,
        out int skippedCount)
    {
        skippedCount = 0;
        List<string> result = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string? path in paths ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(path) || !PathExists(path))
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

    private BrowserMarkSlotSaveAggregation BuildAggregation(
        string sourceScope,
        string sourceScopeLabel,
        string? sourceCategoryId,
        string? sourceCategoryName,
        int categoryCount,
        IEnumerable<BrowserTabSessionState>? tabs)
    {
        int rawMarkCount = 0;
        int tabCount = 0;
        List<string> uniquePaths = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (BrowserTabSessionState tab in tabs ?? Array.Empty<BrowserTabSessionState>())
        {
            tabCount++;
            foreach (string path in tab.MarkedPaths ?? new List<string>())
            {
                if (string.IsNullOrWhiteSpace(path) || !PathExists(path))
                {
                    continue;
                }

                rawMarkCount++;
                if (seen.Add(path))
                {
                    uniquePaths.Add(path);
                }
            }
        }

        return new BrowserMarkSlotSaveAggregation(
            sourceScope,
            sourceScopeLabel,
            sourceCategoryId,
            sourceCategoryName,
            categoryCount,
            tabCount,
            rawMarkCount,
            uniquePaths);
    }

    private static MarkSlotActionResult MarkSlotSaveFailed(int slotNumber) =>
        new(false, $"マークスロット {slotNumber} を保存できませんでした。ストレージを確認してください。");

    private static List<string> DistinctPaths(IEnumerable<string>? paths, bool removeBlank = false) =>
        (paths ?? Array.Empty<string>())
            .Where(path => !removeBlank || !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string NormalizeDisplayName(string? displayName, int slotNumber) =>
        string.IsNullOrWhiteSpace(displayName) ? $"スロット {slotNumber}" : displayName.Trim();

    private static string GetDisplayName(MarkSlotEntry slot) => NormalizeDisplayName(slot.DisplayName, slot.SlotNumber);

    internal static string BuildDefaultDisplayName(MarkSlotEntry slot, string sourceScope, string? categoryName)
    {
        if (!string.Equals(GetDisplayName(slot), $"スロット {slot.SlotNumber}", StringComparison.CurrentCulture))
        {
            return GetDisplayName(slot);
        }

        return sourceScope switch
        {
            MarkSlotSourceScopes.CurrentCategory => $"{(string.IsNullOrWhiteSpace(categoryName) ? "既定" : categoryName)} 全マーク",
            MarkSlotSourceScopes.Workspace => "Workspace全マーク",
            _ => $"スロット {slot.SlotNumber}"
        };
    }

    internal static bool HasSavedState(MarkSlotEntry slot) =>
        slot.Paths.Count > 0 ||
        slot.SavedAtUtc.HasValue ||
        !string.Equals(GetDisplayName(slot), $"スロット {slot.SlotNumber}", StringComparison.CurrentCulture);

    private static void ResetSlot(MarkSlotEntry slot)
    {
        slot.DisplayName = $"スロット {slot.SlotNumber}";
        slot.SavedAtUtc = null;
        slot.Paths = [];
        slot.SourceScope = null;
        slot.SourceCategoryId = null;
        slot.SourceCategoryName = null;
        slot.SourceTabId = null;
        slot.SourceTabDisplayName = null;
    }

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    private static List<MarkPathKind> ValidateExistingPaths(
        IEnumerable<string> paths,
        out int missingCount)
    {
        missingCount = 0;
        var result = new List<MarkPathKind>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !TryGetPathKind(path, out bool isDirectory))
            {
                missingCount++;
                continue;
            }

            if (seen.Add(path))
            {
                result.Add(new MarkPathKind(path, isDirectory));
            }
        }
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
}

internal sealed record BrowserMarkSlotSaveAggregation(
    string SourceScope,
    string SourceScopeLabel,
    string? SourceCategoryId,
    string? SourceCategoryName,
    int CategoryCount,
    int TabCount,
    int RawMarkCount,
    IReadOnlyList<string> Paths)
{
    public int UniquePathCount => Paths.Count;
}

internal readonly record struct BrowserMarkSlotGlobalSummary(
    int ActiveTabMarkCount,
    int CurrentCategoryMarkCount,
    int CurrentCategoryTabCount,
    string CurrentCategoryName,
    int GlobalMarkCount,
    int GlobalCategoryCount,
    int GlobalTabCount);
