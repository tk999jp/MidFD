using MidFD.Models;

namespace MidFD.Services;

public static class QuickAccessService
{
    private const int MaxRecentCount = 20;
    private const string UncategorizedCategoryLabel = "未分類";
    public static int RecentLimit => MaxRecentCount;

    public static QuickAccessStore LoadOrMigrate(IEnumerable<string>? legacyPaths)
    {
        if (QuickAccessStorage.Exists())
        {
            return QuickAccessStorage.Load();
        }

        var store = new QuickAccessStore();
        bool migrated = false;

        foreach (string path in legacyPaths ?? Array.Empty<string>())
        {
            string? normalized = NormalizePath(path, null);
            if (string.IsNullOrEmpty(normalized))
            {
                continue;
            }

            if (store.Bookmarks.Any(item => PathsEqual(item.Path, normalized)))
            {
                continue;
            }

            store.Bookmarks.Add(new QuickAccessEntry
            {
                Kind = QuickAccessEntryKind.Bookmark,
                Path = normalized,
                DisplayName = CreateDisplayName(normalized)
            });
            migrated = true;
        }

        if (migrated)
        {
            QuickAccessStorage.Save(store);
        }

        return store;
    }

    public static void Save(QuickAccessStore store)
    {
        QuickAccessStorage.Save(SanitizeStore(store));
    }

    public static bool TryAddBookmark(QuickAccessStore store, string path, string? currentPath, out string message)
    {
        string? normalized = NormalizePath(path, currentPath);
        if (string.IsNullOrEmpty(normalized))
        {
            message = "登録するパスが空です。";
            return false;
        }

        if (!Directory.Exists(normalized))
        {
            message = $"登録するパスが見つかりません: {normalized}";
            return false;
        }

        if (ContainsManagedPath(store, normalized))
        {
            message = "同じパスは既にブックマーク登録されています。";
            return false;
        }

        store.Bookmarks.Add(new QuickAccessEntry
        {
            Kind = QuickAccessEntryKind.Bookmark,
            Path = normalized,
            DisplayName = CreateDisplayName(normalized)
        });
        RemoveRecentByPath(store, normalized);

        message = "ブックマークを追加しました。";
        return true;
    }

    public static bool TrySaveManagedLocationEntry(
        QuickAccessStore store,
        QuickAccessEntry? existingEntry,
        string displayName,
        string path,
        string? categoryName,
        bool useAlias,
        string? currentPath,
        out string normalizedPath,
        out string message)
    {
        normalizedPath = string.Empty;
        string? normalized = NormalizePath(path, currentPath);
        if (string.IsNullOrEmpty(normalized))
        {
            message = "登録するパスが空です。";
            return false;
        }

        if (!Directory.Exists(normalized))
        {
            message = $"登録するパスが見つかりません: {normalized}";
            return false;
        }

        string resolvedDisplayName = displayName.Trim();
        if (string.IsNullOrWhiteSpace(resolvedDisplayName))
        {
            resolvedDisplayName = CreateDisplayName(normalized);
        }

        QuickAccessEntry? target = existingEntry == null ? null : FindManagedEntry(store, existingEntry);
        if (existingEntry != null && target == null)
        {
            message = "編集対象が見つかりません。";
            return false;
        }

        if (ContainsManagedPath(store, normalized, target))
        {
            message = "同じパスは既に登録されています。";
            return false;
        }

        QuickAccessEntryKind targetKind = useAlias ? QuickAccessEntryKind.Alias : QuickAccessEntryKind.Bookmark;
        string targetDisplayName = resolvedDisplayName;
        string? targetCategoryName = NormalizeCategoryName(categoryName);

        if (target == null)
        {
            var newEntry = new QuickAccessEntry
            {
                Kind = targetKind,
                Path = normalized,
                DisplayName = targetDisplayName,
                CategoryName = targetCategoryName
            };

            if (targetKind == QuickAccessEntryKind.Alias)
            {
                store.Aliases.Add(newEntry);
                message = $"QuickAccess を登録しました。表示名: {targetDisplayName} / タブ表示にも使います。";
            }
            else
            {
                store.Bookmarks.Add(newEntry);
                message = $"QuickAccess を登録しました。表示名: {targetDisplayName}";
            }
        }
        else
        {
            if (target.Kind != targetKind)
            {
                if (target.Kind == QuickAccessEntryKind.Bookmark)
                {
                    store.Bookmarks.Remove(target);
                }
                else if (target.Kind == QuickAccessEntryKind.Alias)
                {
                    store.Aliases.Remove(target);
                }

                var replacement = new QuickAccessEntry
                {
                    Kind = targetKind,
                    Path = normalized,
                    DisplayName = targetDisplayName,
                    CategoryName = targetCategoryName
                };

                if (targetKind == QuickAccessEntryKind.Alias)
                {
                    store.Aliases.Add(replacement);
                }
                else
                {
                    store.Bookmarks.Add(replacement);
                }
            }
            else
            {
                target.Path = normalized;
                target.DisplayName = targetDisplayName;
                target.CategoryName = targetCategoryName;
            }

            message = targetKind == QuickAccessEntryKind.Alias
                ? $"QuickAccess を更新しました。表示名: {targetDisplayName} / タブ表示にも使います。"
                : $"QuickAccess を更新しました。表示名: {targetDisplayName}";
        }

        RemoveRecentByPath(store, normalized);
        normalizedPath = normalized;
        return true;
    }

    public static bool TryAddAlias(QuickAccessStore store, string displayName, string path, string? currentPath, out string normalizedPath, out string message)
    {
        normalizedPath = string.Empty;
        string? normalized = NormalizePath(path, currentPath);
        if (string.IsNullOrEmpty(normalized))
        {
            message = "登録するパスが空です。";
            return false;
        }

        string trimmedName = displayName.Trim();
        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            message = "別名が空です。";
            return false;
        }

        if (ContainsManagedPath(store, normalized))
        {
            message = "同じパスは既に登録されています。";
            return false;
        }

        store.Aliases.Add(new QuickAccessEntry
        {
            Kind = QuickAccessEntryKind.Alias,
            Path = normalized,
            DisplayName = trimmedName
        });
        RemoveRecentByPath(store, normalized);

        normalizedPath = normalized;
        message = "別名を追加しました。";
        return true;
    }

    public static bool TryAddExternalCommand(
        QuickAccessStore store,
        string displayName,
        string executablePath,
        string arguments,
        QuickAccessCommandWorkingDirectoryMode workingDirectoryMode,
        QuickAccessCommandTargetMode targetMode,
        string? currentPath,
        out string normalizedExecutablePath,
        out string message)
    {
        normalizedExecutablePath = string.Empty;
        string trimmedName = displayName.Trim();
        if (string.IsNullOrWhiteSpace(trimmedName))
        {
            message = "表示名が空です。";
            return false;
        }

        string? normalized = NormalizePath(executablePath, currentPath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            message = "実行ファイルパスが空です。";
            return false;
        }

        store.Commands.Add(new QuickAccessEntry
        {
            Kind = QuickAccessEntryKind.ExternalCommand,
            DisplayName = trimmedName,
            ExecutablePath = normalized,
            Arguments = arguments ?? string.Empty,
            WorkingDirectoryMode = workingDirectoryMode,
            TargetMode = targetMode
        });

        normalizedExecutablePath = normalized;
        message = "外部コマンドを追加しました。";
        return true;
    }

    public static bool TryUpdateEntry(QuickAccessStore store, QuickAccessEntry entry, string displayName, string path, string? currentPath, out string normalizedPath, out string message)
    {
        normalizedPath = string.Empty;

        if (entry.Kind != QuickAccessEntryKind.Bookmark &&
            entry.Kind != QuickAccessEntryKind.Alias &&
            entry.Kind != QuickAccessEntryKind.ExternalCommand)
        {
            message = "この項目は編集できません。";
            return false;
        }

        if (entry.Kind == QuickAccessEntryKind.ExternalCommand)
        {
            string trimmedName = displayName.Trim();
            if (string.IsNullOrWhiteSpace(trimmedName))
            {
                message = "表示名が空です。";
                return false;
            }

            string? normalizedExecutablePath = NormalizePath(path, currentPath);
            if (string.IsNullOrWhiteSpace(normalizedExecutablePath))
            {
                message = "実行ファイルパスが空です。";
                return false;
            }

            QuickAccessEntry? commandTarget = FindManagedEntry(store, entry);
            if (commandTarget == null)
            {
                message = "編集対象が見つかりません。";
                return false;
            }

            commandTarget.DisplayName = trimmedName;
            commandTarget.ExecutablePath = normalizedExecutablePath;
            normalizedPath = normalizedExecutablePath;
            message = "項目を更新しました。";
            return true;
        }

        return TrySaveManagedLocationEntry(
            store,
            entry,
            displayName,
            path,
            entry.CategoryName,
            entry.Kind == QuickAccessEntryKind.Alias,
            currentPath,
            out normalizedPath,
            out message);
    }

    public static bool RemoveManagedEntry(QuickAccessStore store, QuickAccessEntry entry)
    {
        QuickAccessEntry? existing = FindManagedEntry(store, entry);
        if (existing == null)
        {
            return false;
        }

        if (existing.Kind == QuickAccessEntryKind.Bookmark)
        {
            store.Bookmarks.Remove(existing);
        }
        else if (existing.Kind == QuickAccessEntryKind.Alias)
        {
            store.Aliases.Remove(existing);
        }
        else if (existing.Kind == QuickAccessEntryKind.ExternalCommand)
        {
            store.Commands.Remove(existing);
        }

        return true;
    }

    public static bool CanMoveManagedEntry(QuickAccessStore store, QuickAccessEntry entry, bool moveUp)
    {
        List<QuickAccessEntry>? list = GetManagedList(store, entry.Kind);
        if (list == null)
        {
            return false;
        }

        QuickAccessEntry? existing = FindManagedEntry(store, entry);
        if (existing == null)
        {
            return false;
        }

        int index = list.IndexOf(existing);
        if (index < 0)
        {
            return false;
        }

        return TryGetCategoryMoveTargetIndex(list, existing, index, moveUp, out _);
    }

    public static bool TryMoveManagedEntry(QuickAccessStore store, QuickAccessEntry entry, bool moveUp)
    {
        List<QuickAccessEntry>? list = GetManagedList(store, entry.Kind);
        if (list == null)
        {
            return false;
        }

        QuickAccessEntry? existing = FindManagedEntry(store, entry);
        if (existing == null)
        {
            return false;
        }

        int index = list.IndexOf(existing);
        if (index < 0)
        {
            return false;
        }

        if (!TryGetCategoryMoveTargetIndex(list, existing, index, moveUp, out int targetIndex))
        {
            return false;
        }

        (list[index], list[targetIndex]) = (list[targetIndex], list[index]);
        return true;
    }

    public static bool CanMoveRegisteredCategory(QuickAccessStore store, string categoryName, bool moveUp)
    {
        return TryMoveRegisteredCategoryCore(store, categoryName, moveUp, apply: false);
    }

    public static bool TryMoveRegisteredCategory(QuickAccessStore store, string categoryName, bool moveUp)
    {
        return TryMoveRegisteredCategoryCore(store, categoryName, moveUp, apply: true);
    }

    public static bool RecordRecent(QuickAccessStore store, string path)
    {
        string? normalized = NormalizePath(path, null);
        if (string.IsNullOrEmpty(normalized) || !Directory.Exists(normalized))
        {
            return false;
        }

        if (ShouldSuppressRecent(store, normalized))
        {
            RemoveRecentByPath(store, normalized);
            RefreshRecentDisplayNames(store);
            return false;
        }

        QuickAccessEntry? existing = store.Recents.FirstOrDefault(item => PathsEqual(item.Path, normalized));
        if (existing != null)
        {
            store.Recents.Remove(existing);
        }

        store.Recents.Insert(0, new QuickAccessEntry
        {
            Kind = QuickAccessEntryKind.Recent,
            Path = normalized,
            DisplayName = CreateDisplayName(normalized)
        });

        if (store.Recents.Count > MaxRecentCount)
        {
            store.Recents.RemoveRange(MaxRecentCount, store.Recents.Count - MaxRecentCount);
        }

        RefreshRecentDisplayNames(store);

        return true;
    }

    public static IReadOnlyList<QuickAccessEntry> GetRegisteredEntries(QuickAccessStore store)
    {
        return store.Bookmarks
            .Concat(store.Aliases)
            .Select(CloneEntry)
            .ToList();
    }

    public static IReadOnlyList<string> GetKnownCategoryNames(QuickAccessStore? store)
    {
        if (store == null)
        {
            return Array.Empty<string>();
        }

        var categories = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (QuickAccessEntry entry in store.Bookmarks.Concat(store.Aliases).Concat(store.Commands))
        {
            string? category = NormalizeCategoryName(entry.CategoryName);
            if (string.IsNullOrWhiteSpace(category))
            {
                continue;
            }

            if (seen.Add(category))
            {
                categories.Add(category);
            }
        }

        return categories;
    }

    public static IReadOnlyList<string> GetRegisteredCategoryOrder(IEnumerable<QuickAccessEntry> entries)
    {
        var ordered = new List<string>();
        bool hasUncategorized = false;
        foreach (QuickAccessEntry entry in entries)
        {
            string category = GetEntryCategoryLabel(entry);
            if (string.Equals(category, UncategorizedCategoryLabel, StringComparison.OrdinalIgnoreCase))
            {
                hasUncategorized = true;
                continue;
            }

            if (!ordered.Contains(category, StringComparer.OrdinalIgnoreCase))
            {
                ordered.Add(category);
            }
        }

        if (hasUncategorized)
        {
            ordered.Add(UncategorizedCategoryLabel);
        }

        return ordered;
    }

    public static string? FindAliasDisplayName(QuickAccessStore? store, string? path)
    {
        string? normalized = NormalizePath(path, null);
        if (store == null || string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        QuickAccessEntry? alias = store.Aliases.FirstOrDefault(item => PathsEqual(item.Path, normalized));
        if (alias == null)
        {
            return null;
        }

        string displayName = alias.DisplayName?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(displayName) ? null : displayName;
    }

    public static IReadOnlyList<QuickAccessEntry> GetRecentEntries(QuickAccessStore store)
    {
        return store.Recents
            .Where(item => !ShouldSuppressRecent(store, item.Path))
            .Select(CloneEntry)
            .ToList();
    }

    public static IReadOnlyList<QuickAccessEntry> GetHistoryEntries(IReadOnlyList<QuickAccessEntry> historyEntries)
    {
        return historyEntries
            .Where(item => item.Kind == QuickAccessEntryKind.History && !string.IsNullOrWhiteSpace(item.Path))
            .Select(CloneEntry)
            .ToList();
    }

    public static IReadOnlyList<QuickAccessEntry> FilterEntries(
        IEnumerable<QuickAccessEntry> entries,
        string query,
        QuickAccessOpenDiagnostics? diagnostics = null)
    {
        string trimmed = query.Trim();
        IReadOnlyList<QuickAccessEntry> source = entries
            .Select(CloneEntry)
            .ToList();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return source;
        }

        return source
            .Where(entry =>
                entry.DisplayName.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                GetEntryCategoryLabel(entry).Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                GetEntryValueLabel(entry).Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                GetEntryKindLabel(entry).Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                GetEntryStatusLabel(entry, (string?)null, diagnostics).Contains(trimmed, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public static string GetEntryCategoryLabel(QuickAccessEntry entry)
    {
        string? category = NormalizeCategoryName(entry.CategoryName);
        return string.IsNullOrWhiteSpace(category) ? "未分類" : category;
    }

    public static string GetEntryKindLabel(QuickAccessEntry entry)
    {
        return entry.Kind switch
        {
            QuickAccessEntryKind.Bookmark => "登録先",
            QuickAccessEntryKind.Alias => "登録先(タブ表示)",
            QuickAccessEntryKind.ExternalCommand => "コマンド",
            QuickAccessEntryKind.Recent => "最近",
            QuickAccessEntryKind.History => entry.DisplayName.StartsWith("進む:", StringComparison.Ordinal) ? "進む履歴" : "戻る履歴",
            _ => string.Empty
        };
    }

    public static string GetEntryValueLabel(QuickAccessEntry entry)
    {
        if (entry.Kind == QuickAccessEntryKind.ExternalCommand)
        {
            return string.IsNullOrWhiteSpace(entry.ExecutablePath)
                ? "(未設定)"
                : entry.ExecutablePath;
        }

        return entry.Path;
    }

    public static string GetEntryTooltipText(
        QuickAccessEntry entry,
        string? currentPath,
        QuickAccessOpenDiagnostics? diagnostics = null,
        string? precomputedStatus = null)
    {
        string statusLabel = precomputedStatus ?? GetEntryStatusLabel(entry, currentPath, diagnostics);
        if (entry.Kind == QuickAccessEntryKind.ExternalCommand)
        {
            return $"表示名: {entry.DisplayName}\r\n" +
                   $"実行ファイル: {GetEntryValueLabel(entry)}\r\n" +
                   $"区分: {GetEntryKindLabel(entry)}\r\n" +
                   $"状態: {statusLabel}\r\n" +
                   $"対象: {GetExternalCommandTargetLabel(entry.TargetMode)}\r\n" +
                   $"作業: {GetExternalCommandWorkingDirectoryLabel(entry.WorkingDirectoryMode)}\r\n" +
                   $"引数: {(string.IsNullOrWhiteSpace(entry.Arguments) ? "(なし)" : entry.Arguments)}";
        }

        return $"表示名: {entry.DisplayName}\r\n" +
               $"カテゴリ: {GetEntryCategoryLabel(entry)}\r\n" +
               $"移動先: {GetEntryValueLabel(entry)}\r\n" +
               $"区分: {GetEntryKindLabel(entry)}\r\n" +
               $"状態: {statusLabel}";
    }

    public static string GetEntryStatusLabel(QuickAccessEntry entry, QuickAccessCommandContext context)
    {
        if (entry.Kind != QuickAccessEntryKind.ExternalCommand)
        {
            return string.Empty;
        }

        return TryResolveExternalCommand(entry, context, out _, out _, out _, out string message)
            ? $"実行可 / {GetExternalCommandTargetLabel(entry.TargetMode)}"
            : message;
    }

    public static string GetEntryStatusLabel(QuickAccessEntry entry, string? currentPath, QuickAccessOpenDiagnostics? diagnostics = null)
    {
        if (entry.Kind == QuickAccessEntryKind.ExternalCommand)
        {
            string availability = GetExternalCommandAvailabilityLabel(entry, diagnostics);
            return $"{availability} / {GetExternalCommandTargetLabel(entry.TargetMode)}";
        }

        string candidatePath = entry.Path ?? string.Empty;
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(currentPath) && PathsEqual(candidatePath, currentPath))
        {
            return "現在地";
        }

        if (entry.Kind == QuickAccessEntryKind.History)
        {
            return entry.DisplayName.StartsWith("進む:", StringComparison.Ordinal)
                ? "進む候補"
                : "戻る候補";
        }

        if (NetworkPathResolutionPolicy.IsAuxiliaryResolutionDeferred(candidatePath))
        {
            NetworkPathResolutionPolicy.LogDecision(
                "NetworkPathResolutionDeferral.Skip",
                "QuickAccessStatus",
                nameof(GetEntryStatusLabel),
                candidatePath,
                usedCached: false,
                resolvedSync: false,
                reason: "unc-path");
            return string.Empty;
        }

        bool exists = diagnostics?.MeasureDirectoryExists("QuickAccess.ResolvePath", candidatePath, "entry-status")
            ?? Directory.Exists(candidatePath);
        bool usedCached = diagnostics?.LastProbeUsedCache ?? false;
        if (!exists)
        {
            NetworkPathResolutionPolicy.LogDecision(
                "NetworkPathResolutionDeferral.AllowCritical",
                "QuickAccessStatus",
                nameof(GetEntryStatusLabel),
                candidatePath,
                usedCached: usedCached,
                resolvedSync: !usedCached,
                reason: usedCached ? "cached-entry-status" : "local-or-drive-letter");
            return "見つからない";
        }

        NetworkPathResolutionPolicy.LogDecision(
            "NetworkPathResolutionDeferral.AllowCritical",
            "QuickAccessStatus",
            nameof(GetEntryStatusLabel),
            candidatePath,
            usedCached: usedCached,
            resolvedSync: !usedCached,
            reason: usedCached ? "cached-entry-status" : "local-or-drive-letter");
        return "移動可";
    }

    public static string GetExternalCommandTargetLabel(QuickAccessCommandTargetMode targetMode)
    {
        return targetMode switch
        {
            QuickAccessCommandTargetMode.None => "対象: なし",
            QuickAccessCommandTargetMode.CurrentPath => "対象: 現在地",
            QuickAccessCommandTargetMode.CurrentItem => "対象: 選択項目",
            QuickAccessCommandTargetMode.CurrentFile => "対象: 選択ファイル",
            QuickAccessCommandTargetMode.CurrentDirectory => "対象: 選択フォルダ",
            QuickAccessCommandTargetMode.MarkedItems => "対象: マーク項目",
            _ => "対象: なし"
        };
    }

    public static string GetExternalCommandWorkingDirectoryLabel(QuickAccessCommandWorkingDirectoryMode workingDirectoryMode)
    {
        return workingDirectoryMode switch
        {
            QuickAccessCommandWorkingDirectoryMode.CurrentPath => "作業: 現在地",
            QuickAccessCommandWorkingDirectoryMode.ExecutableDirectory => "作業: 実行ファイルのフォルダ",
            _ => "作業: 現在地"
        };
    }

    public static bool TryExecuteExternalCommand(QuickAccessEntry entry, QuickAccessCommandContext context, out string message)
    {
        if (!TryResolveExternalCommand(entry, context, out string executablePath, out string arguments, out string workingDirectory, out message))
        {
            return false;
        }

        string? error = ExternalToolService.ExecuteCommand(executablePath, arguments, workingDirectory);
        if (error != null)
        {
            message = error;
            return false;
        }

        message = "外部コマンドを起動しました。";
        return true;
    }

    public static bool TryResolveExternalCommand(
        QuickAccessEntry entry,
        QuickAccessCommandContext context,
        out string executablePath,
        out string arguments,
        out string workingDirectory,
        out string message)
    {
        executablePath = string.Empty;
        arguments = string.Empty;
        workingDirectory = context.CurrentPath;

        if (entry.Kind != QuickAccessEntryKind.ExternalCommand)
        {
            message = "外部コマンドではありません。";
            return false;
        }

        executablePath = NormalizePath(entry.ExecutablePath, context.CurrentPath) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            message = "外部コマンドの実行ファイルが未設定です。";
            return false;
        }

        if (!File.Exists(executablePath))
        {
            message = "外部コマンドの実行ファイルが見つかりません。";
            return false;
        }

        if (!TryResolveCommandTarget(entry.TargetMode, context, out message))
        {
            return false;
        }

        if (!TryExpandArguments(entry.Arguments ?? string.Empty, context, out arguments, out message))
        {
            return false;
        }

        workingDirectory = entry.WorkingDirectoryMode switch
        {
            QuickAccessCommandWorkingDirectoryMode.ExecutableDirectory => Path.GetDirectoryName(executablePath) ?? context.CurrentPath,
            _ => context.CurrentPath
        };

        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            workingDirectory = context.CurrentPath;
        }

        message = string.Empty;
        return true;
    }

    private static string GetExternalCommandAvailabilityLabel(QuickAccessEntry entry, QuickAccessOpenDiagnostics? diagnostics = null)
    {
        string executablePath = NormalizePath(entry.ExecutablePath, null) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return "未設定";
        }

        if (NetworkPathResolutionPolicy.IsAuxiliaryResolutionDeferred(executablePath))
        {
            NetworkPathResolutionPolicy.LogDecision(
                "NetworkPathResolutionDeferral.Skip",
                "QuickAccessStatus",
                nameof(GetExternalCommandAvailabilityLabel),
                executablePath,
                usedCached: false,
                resolvedSync: false,
                reason: "unc-executable");
            return string.Empty;
        }

        bool exists = diagnostics?.MeasureFileExists("QuickAccess.ResolveShellInfo", executablePath, "command-executable")
            ?? File.Exists(executablePath);
        bool usedCached = diagnostics?.LastProbeUsedCache ?? false;
        NetworkPathResolutionPolicy.LogDecision(
            "NetworkPathResolutionDeferral.AllowCritical",
            "QuickAccessStatus",
            nameof(GetExternalCommandAvailabilityLabel),
            executablePath,
            usedCached: usedCached,
            resolvedSync: !usedCached,
            reason: usedCached ? "cached-command-executable" : "local-or-drive-letter");
        return exists ? "実行可" : "見つからない";
    }

    public static IReadOnlyList<QuickAccessEntry> BuildHistoryEntries(IEnumerable<string> backHistory, IEnumerable<string> forwardHistory)
    {
        var entries = new List<QuickAccessEntry>();

        foreach (string path in backHistory)
        {
            string? normalized = NormalizePath(path, null);
            if (string.IsNullOrEmpty(normalized))
            {
                continue;
            }

            entries.Add(new QuickAccessEntry
            {
                Kind = QuickAccessEntryKind.History,
                Path = normalized,
                DisplayName = $"戻る: {CreateDisplayName(normalized)}"
            });
        }

        foreach (string path in forwardHistory)
        {
            string? normalized = NormalizePath(path, null);
            if (string.IsNullOrEmpty(normalized))
            {
                continue;
            }

            entries.Add(new QuickAccessEntry
            {
                Kind = QuickAccessEntryKind.History,
                Path = normalized,
                DisplayName = $"進む: {CreateDisplayName(normalized)}"
            });
        }

        return entries;
    }

    public static QuickAccessStore SanitizeStore(QuickAccessStore? store)
    {
        var result = new QuickAccessStore();
        if (store == null)
        {
            return result;
        }

        foreach (QuickAccessEntry entry in store.Bookmarks)
        {
            if (!TryNormalizeLoadedEntry(entry, QuickAccessEntryKind.Bookmark, out QuickAccessEntry? normalized))
            {
                continue;
            }

            if (!ContainsManagedPath(result, normalized.Path))
            {
                result.Bookmarks.Add(normalized);
            }
        }

        foreach (QuickAccessEntry entry in store.Aliases)
        {
            if (!TryNormalizeLoadedEntry(entry, QuickAccessEntryKind.Alias, out QuickAccessEntry? normalized))
            {
                continue;
            }

            if (!ContainsManagedPath(result, normalized.Path))
            {
                result.Aliases.Add(normalized);
            }
        }

        foreach (QuickAccessEntry entry in store.Commands)
        {
            if (!TryNormalizeLoadedEntry(entry, QuickAccessEntryKind.ExternalCommand, out QuickAccessEntry? normalized))
            {
                continue;
            }

            result.Commands.Add(normalized);
        }

        foreach (QuickAccessEntry entry in store.Recents)
        {
            if (!TryNormalizeLoadedEntry(entry, QuickAccessEntryKind.Recent, out QuickAccessEntry? normalized))
            {
                continue;
            }

            QuickAccessEntry? existing = result.Recents.FirstOrDefault(item => PathsEqual(item.Path, normalized.Path));
            if (existing != null)
            {
                result.Recents.Remove(existing);
            }

            if (ShouldSuppressRecent(result, normalized.Path))
            {
                continue;
            }

            result.Recents.Add(normalized);
            if (result.Recents.Count >= MaxRecentCount)
            {
                break;
            }
        }

        RefreshRecentDisplayNames(result);
        return result;
    }

    public static string CreateDisplayName(string path)
    {
        try
        {
            string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string name = Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(name) ? trimmed : name;
        }
        catch
        {
            return path;
        }
    }

    public static string? NormalizePath(string? path, string? currentPath)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string candidate = path.Trim();
        if (candidate.Length == 2 && candidate[1] == ':' && char.IsLetter(candidate[0]))
        {
            candidate = candidate + Path.DirectorySeparatorChar;
        }
        try
        {
            if (!Path.IsPathRooted(candidate) && !string.IsNullOrWhiteSpace(currentPath))
            {
                candidate = Path.Combine(currentPath, candidate);
            }

            return Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return candidate;
        }
    }

    public static bool PathsEqual(string? left, string? right)
    {
        return string.Equals(
            NavigationService.NormalizeDirectoryForCompare(left ?? string.Empty),
            NavigationService.NormalizeDirectoryForCompare(right ?? string.Empty),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryNormalizeLoadedEntry(QuickAccessEntry? entry, QuickAccessEntryKind expectedKind, out QuickAccessEntry normalized)
    {
        normalized = null!;
        if (entry == null)
        {
            return false;
        }

        string? path = expectedKind == QuickAccessEntryKind.ExternalCommand
            ? NormalizePath(entry.ExecutablePath, null)
            : NormalizePath(entry.Path, null);
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string displayName = string.IsNullOrWhiteSpace(entry.DisplayName)
            ? CreateDisplayName(path)
            : entry.DisplayName.Trim();

        normalized = new QuickAccessEntry
        {
            Kind = expectedKind,
            Path = path,
            DisplayName = displayName,
            CategoryName = NormalizeCategoryName(entry.CategoryName),
            ExecutablePath = expectedKind == QuickAccessEntryKind.ExternalCommand
                ? path
                : (entry.ExecutablePath ?? string.Empty),
            Arguments = entry.Arguments ?? string.Empty,
            WorkingDirectoryMode = entry.WorkingDirectoryMode,
            TargetMode = entry.TargetMode
        };

        if (expectedKind == QuickAccessEntryKind.ExternalCommand)
        {
            normalized.Path = string.Empty;
            if (string.IsNullOrWhiteSpace(displayName))
            {
                return false;
            }
        }

        return true;
    }

    private static void RefreshRecentDisplayNames(QuickAccessStore store)
    {
        var duplicatePaths = store.Recents
            .GroupBy(item => CreateDisplayName(item.Path), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (QuickAccessEntry entry in store.Recents)
        {
            string baseName = CreateDisplayName(entry.Path);
            if (!duplicatePaths.Contains(baseName))
            {
                entry.DisplayName = baseName;
                continue;
            }

            entry.DisplayName = $"{baseName} ({GetParentLabel(entry.Path)})";
        }
    }

    private static bool ShouldSuppressRecent(QuickAccessStore store, string path)
    {
        return ContainsManagedPath(store, path);
    }

    private static void RemoveRecentByPath(QuickAccessStore store, string normalizedPath)
    {
        store.Recents.RemoveAll(item => PathsEqual(item.Path, normalizedPath));
    }

    private static QuickAccessEntry CloneEntry(QuickAccessEntry entry)
    {
        return new QuickAccessEntry
        {
            Kind = entry.Kind,
            Path = entry.Path,
            DisplayName = entry.DisplayName,
            CategoryName = entry.CategoryName,
            ExecutablePath = entry.ExecutablePath,
            Arguments = entry.Arguments,
            WorkingDirectoryMode = entry.WorkingDirectoryMode,
            TargetMode = entry.TargetMode
        };
    }

    public static string? NormalizeCategoryName(string? categoryName)
    {
        if (string.IsNullOrWhiteSpace(categoryName))
        {
            return null;
        }

        string trimmed = categoryName.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static string GetParentLabel(string path)
    {
        try
        {
            string? parent = Path.GetDirectoryName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(parent))
            {
                return path;
            }

            string parentName = Path.GetFileName(parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(parentName))
            {
                return parentName;
            }

            return parent;
        }
        catch
        {
            return path;
        }
    }

    private static bool ContainsManagedPath(QuickAccessStore store, string normalizedPath, QuickAccessEntry? except = null)
    {
        return store.Bookmarks.Concat(store.Aliases).Any(item =>
            (except == null || !ReferenceEquals(item, except)) &&
            item.Kind != QuickAccessEntryKind.Recent &&
            item.Kind != QuickAccessEntryKind.History &&
            PathsEqual(item.Path, normalizedPath));
    }

    private static QuickAccessEntry? FindManagedEntry(QuickAccessStore store, QuickAccessEntry entry)
    {
        return store.Bookmarks.Concat(store.Aliases).Concat(store.Commands)
            .FirstOrDefault(item =>
                item.Kind == entry.Kind &&
                (item.Kind == QuickAccessEntryKind.ExternalCommand
                    ? PathsEqual(item.ExecutablePath, entry.ExecutablePath) &&
                      string.Equals(item.DisplayName, entry.DisplayName, StringComparison.Ordinal)
                    : PathsEqual(item.Path, entry.Path)));
    }

    private static List<QuickAccessEntry>? GetManagedList(QuickAccessStore store, QuickAccessEntryKind kind)
    {
        return kind switch
        {
            QuickAccessEntryKind.Bookmark => store.Bookmarks,
            QuickAccessEntryKind.Alias => store.Aliases,
            _ => null
        };
    }

    private static bool TryGetCategoryMoveTargetIndex(
        List<QuickAccessEntry> list,
        QuickAccessEntry entry,
        int index,
        bool moveUp,
        out int targetIndex)
    {
        string category = GetEntryCategoryLabel(entry);
        int step = moveUp ? -1 : 1;
        for (int candidate = index + step; candidate >= 0 && candidate < list.Count; candidate += step)
        {
            if (!string.Equals(GetEntryCategoryLabel(list[candidate]), category, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            targetIndex = candidate;
            return true;
        }

        targetIndex = -1;
        return false;
    }

    private static bool TryMoveRegisteredCategoryCore(QuickAccessStore store, string categoryName, bool moveUp, bool apply)
    {
        string? normalizedCategory = NormalizeCategoryName(categoryName);
        if (string.IsNullOrWhiteSpace(normalizedCategory) ||
            string.Equals(normalizedCategory, UncategorizedCategoryLabel, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        IReadOnlyList<string> categories = GetRegisteredCategoryOrder(store.Bookmarks.Concat(store.Aliases));
        int categoryIndex = -1;
        for (int index = 0; index < categories.Count; index++)
        {
            if (string.Equals(categories[index], normalizedCategory, StringComparison.OrdinalIgnoreCase))
            {
                categoryIndex = index;
                break;
            }
        }

        if (categoryIndex < 0)
        {
            return false;
        }

        int movableCount = categories.Count;
        bool appendUncategorized = movableCount > 0 &&
            string.Equals(categories[movableCount - 1], UncategorizedCategoryLabel, StringComparison.OrdinalIgnoreCase);
        if (appendUncategorized)
        {
            movableCount--;
        }

        if (categoryIndex >= movableCount)
        {
            return false;
        }

        int targetIndex = moveUp ? categoryIndex - 1 : categoryIndex + 1;
        if (targetIndex < 0 || targetIndex >= movableCount)
        {
            return false;
        }

        if (!apply)
        {
            return true;
        }

        var reorderedCategories = categories.Take(movableCount).ToList();
        (reorderedCategories[categoryIndex], reorderedCategories[targetIndex]) = (reorderedCategories[targetIndex], reorderedCategories[categoryIndex]);
        ReorderEntriesByCategoryOrder(store.Bookmarks, reorderedCategories, appendUncategorized);
        ReorderEntriesByCategoryOrder(store.Aliases, reorderedCategories, appendUncategorized);
        return true;
    }

    private static void ReorderEntriesByCategoryOrder(List<QuickAccessEntry> list, IReadOnlyList<string> categoryOrder, bool appendUncategorized)
    {
        if (list.Count == 0)
        {
            return;
        }

        var grouped = list
            .GroupBy(entry => GetEntryCategoryLabel(entry), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        var reordered = new List<QuickAccessEntry>(list.Count);
        foreach (string category in categoryOrder)
        {
            if (grouped.TryGetValue(category, out List<QuickAccessEntry>? categoryEntries))
            {
                reordered.AddRange(categoryEntries);
            }
        }

        if (appendUncategorized && grouped.TryGetValue(UncategorizedCategoryLabel, out List<QuickAccessEntry>? uncategorizedEntries))
        {
            reordered.AddRange(uncategorizedEntries);
        }

        if (reordered.Count == list.Count)
        {
            list.Clear();
            list.AddRange(reordered);
        }
    }

    private static bool TryResolveCommandTarget(QuickAccessCommandTargetMode targetMode, QuickAccessCommandContext context, out string message)
    {
        message = string.Empty;
        return targetMode switch
        {
            QuickAccessCommandTargetMode.None => true,
            QuickAccessCommandTargetMode.CurrentPath => true,
            QuickAccessCommandTargetMode.CurrentItem => EnsureCurrentItem(context, "現在項目がありません。", out message),
            QuickAccessCommandTargetMode.CurrentFile => EnsureCurrentFile(context, out message),
            QuickAccessCommandTargetMode.CurrentDirectory => EnsureCurrentDirectory(context, out message),
            QuickAccessCommandTargetMode.MarkedItems => EnsureMarkedItems(context, out message),
            _ => true
        };
    }

    private static bool TryExpandArguments(string template, QuickAccessCommandContext context, out string expandedArguments, out string message)
    {
        expandedArguments = template ?? string.Empty;
        message = string.Empty;

        if (expandedArguments.Contains("{CurrentItemPath}", StringComparison.Ordinal))
        {
            if (!EnsureCurrentItem(context, "現在項目がありません。", out message))
            {
                return false;
            }

            expandedArguments = expandedArguments.Replace("{CurrentItemPath}", context.CurrentItemPath);
        }

        if (expandedArguments.Contains("{CurrentItemName}", StringComparison.Ordinal))
        {
            if (!EnsureCurrentItem(context, "現在項目がありません。", out message) || string.IsNullOrWhiteSpace(context.CurrentItemName))
            {
                message = "現在項目がありません。";
                return false;
            }

            expandedArguments = expandedArguments.Replace("{CurrentItemName}", context.CurrentItemName);
        }

        if (expandedArguments.Contains("{CurrentFilePath}", StringComparison.Ordinal))
        {
            if (!EnsureCurrentFile(context, out message) || string.IsNullOrWhiteSpace(context.CurrentItemPath))
            {
                message = "選択ファイルがありません。";
                return false;
            }

            expandedArguments = expandedArguments.Replace("{CurrentFilePath}", QuoteArgument(context.CurrentItemPath));
        }

        if (expandedArguments.Contains("{MarkedItemPaths}", StringComparison.Ordinal))
        {
            if (!EnsureMarkedItems(context, out message))
            {
                return false;
            }

            string joinedMarkedPaths = string.Join(" ", context.MarkedPaths.Select(QuoteArgument));
            expandedArguments = expandedArguments.Replace("{MarkedItemPaths}", joinedMarkedPaths);
        }

        expandedArguments = expandedArguments.Replace("{CurrentPath}", context.CurrentPath ?? string.Empty);
        expandedArguments = expandedArguments.Replace("{CurrentDirectoryPath}", context.CurrentPath ?? string.Empty);
        return true;
    }

    private static bool EnsureCurrentItem(QuickAccessCommandContext context, string failureMessage, out string message)
    {
        if (string.IsNullOrWhiteSpace(context.CurrentItemPath))
        {
            message = failureMessage;
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static bool EnsureCurrentFile(QuickAccessCommandContext context, out string message)
    {
        if (!EnsureCurrentItem(context, "選択ファイルがありません。", out message))
        {
            return false;
        }

        if (context.CurrentItemIsDirectory)
        {
            message = "選択ファイルがありません。";
            return false;
        }

        return true;
    }

    private static bool EnsureCurrentDirectory(QuickAccessCommandContext context, out string message)
    {
        if (!EnsureCurrentItem(context, "選択フォルダがありません。", out message))
        {
            return false;
        }

        if (!context.CurrentItemIsDirectory)
        {
            message = "選択フォルダがありません。";
            return false;
        }

        return true;
    }

    private static bool EnsureMarkedItems(QuickAccessCommandContext context, out string message)
    {
        if (context.MarkedPaths.Count == 0)
        {
            message = "マーク対象がありません。";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static string QuoteArgument(string value)
    {
        string escaped = (value ?? string.Empty).Replace("\"", "\\\"");
        return $"\"{escaped}\"";
    }
}
