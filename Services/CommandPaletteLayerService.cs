using System;
using System.Collections.Generic;
using System.Linq;
using MidFD.Helpers;
using MidFD.Models;

namespace MidFD.Services;

internal static class CommandPaletteLayerService
{
    private static readonly ICommandPaletteLayerProvider[] Providers =
    {
        new QuickAccessLayerProvider(),
        new MarkSlotLayerProvider(),
        new ArchiveLayerProvider()
    };

    public static bool TryBuild(
        ICommandPaletteLayerHost host,
        FeatureGateService featureGate,
        CommandPaletteUsageState usageState,
        CommandPaletteLayerQuery query,
        out CommandPalettePresentation? presentation)
    {
        presentation = null;
        ICommandPaletteLayerProvider? provider = Providers.FirstOrDefault(item =>
            string.Equals(item.RootToken, query.RootToken, StringComparison.OrdinalIgnoreCase));

        if (provider == null)
        {
            return false;
        }

        return provider.TryBuild(host, featureGate, usageState, query, out presentation);
    }
}

internal interface ICommandPaletteLayerHost
{
    string GetCurrentBrowserPath();
    QuickAccessStore GetQuickAccessStoreClone();
    IReadOnlyList<string> GetBackHistorySnapshot();
    IReadOnlyList<string> GetForwardHistorySnapshot();
    MarkSlotStore GetMarkSlotStoreClone();
    SelectionResult ResolveSelection();
    void NavigateToPath(string path);
    void RestoreMarksFromSlot(int slotNumber);
    void ShowArchiveContents(string archivePath);
    Task ExecuteArchiveHashAsync(SevenZipHashAlgorithm algorithm);
}

internal interface ICommandPaletteLayerProvider
{
    string RootToken { get; }

    bool TryBuild(
        ICommandPaletteLayerHost host,
        FeatureGateService featureGate,
        CommandPaletteUsageState usageState,
        CommandPaletteLayerQuery query,
        out CommandPalettePresentation? presentation);
}

internal static class CommandPaletteLayerQueryParser
{
    private static readonly HashSet<string> RootTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "Q",
        "M",
        "A"
    };

    public static CommandPaletteLayerQuery Parse(string? rawText)
    {
        string raw = rawText ?? string.Empty;
        bool hasTrailingWhitespace = raw.Length > 0 && char.IsWhiteSpace(raw[^1]);
        string normalized = raw.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new CommandPaletteLayerQuery(string.Empty, string.Empty, Array.Empty<string>(), hasTrailingWhitespace);
        }

        string[] tokens = normalized
            .Split(new[] { ' ', '\t', '\u3000' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0 || !RootTokens.Contains(tokens[0]))
        {
            if (TryExpandCompactLayerTokens(normalized, out string[]? compactTokens))
            {
                return new CommandPaletteLayerQuery(normalized, compactTokens[0], compactTokens, hasTrailingWhitespace);
            }

            return new CommandPaletteLayerQuery(normalized, string.Empty, tokens, hasTrailingWhitespace);
        }

        return new CommandPaletteLayerQuery(normalized, tokens[0], tokens, hasTrailingWhitespace);
    }

    private static bool TryExpandCompactLayerTokens(string token, out string[] expandedTokens)
    {
        expandedTokens = Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(token) || token.Length < 2)
        {
            return false;
        }

        char root = char.ToUpperInvariant(token[0]);
        string suffix = token[1..].Trim();
        if (string.IsNullOrWhiteSpace(suffix))
        {
            return false;
        }

        string normalizedSuffix = suffix.ToUpperInvariant();
        if (root == 'Q')
        {
            expandedTokens = normalizedSuffix switch
            {
                "1" => new[] { "Q", "1" },
                "R" => new[] { "Q", "R" },
                "H" => new[] { "Q", "H" },
                _ => Array.Empty<string>()
            };
        }
        else if (root == 'M')
        {
            expandedTokens = TryExpandMarkSlotTokens(normalizedSuffix);
        }
        else if (root == 'A')
        {
            expandedTokens = normalizedSuffix switch
            {
                "L" => new[] { "A", "L" },
                "T" => new[] { "A", "T" },
                "HCRC32" => new[] { "A", "H", "CRC32" },
                "HCRC64" => new[] { "A", "H", "CRC64" },
                "HSHA1" => new[] { "A", "H", "SHA1" },
                "HSHA256" => new[] { "A", "H", "SHA256" },
                "HALL" => new[] { "A", "H", "ALL" },
                _ => TryExpandArchiveHashAlias(normalizedSuffix)
            };
        }

        return expandedTokens.Length > 0;
    }

    private static string[] TryExpandMarkSlotTokens(string normalizedSuffix)
    {
        if (normalizedSuffix.All(char.IsDigit))
        {
            return new[] { "M", "R", normalizedSuffix };
        }

        if (normalizedSuffix.Length == 1)
        {
            return normalizedSuffix[0] switch
            {
                'R' => new[] { "M", "R" },
                'S' => new[] { "M", "S" },
                _ => Array.Empty<string>()
            };
        }

        if (normalizedSuffix[0] == 'R' && normalizedSuffix[1..].All(char.IsDigit))
        {
            return new[] { "M", "R", normalizedSuffix[1..] };
        }

        if (normalizedSuffix[0] == 'S' && normalizedSuffix[1..].All(char.IsDigit))
        {
            return new[] { "M", "S", normalizedSuffix[1..] };
        }

        return Array.Empty<string>();
    }

    private static string[] TryExpandArchiveHashAlias(string normalizedSuffix)
    {
        string suffix = normalizedSuffix;
        if (suffix.StartsWith("H", StringComparison.OrdinalIgnoreCase))
        {
            suffix = suffix[1..];
        }

        if (suffix.StartsWith("SHA", StringComparison.OrdinalIgnoreCase))
        {
            suffix = suffix[3..];
        }
        else if (string.Equals(suffix, "S", StringComparison.OrdinalIgnoreCase))
        {
            return new[] { "A", "H", "SHA" };
        }
        else if (suffix.StartsWith("S", StringComparison.OrdinalIgnoreCase))
        {
            suffix = suffix[1..];
        }

        if (string.IsNullOrWhiteSpace(suffix))
        {
            return new[] { "A", "H", "SHA" };
        }

        if (suffix == "1")
        {
            return new[] { "A", "H", "SHA1" };
        }

        if (suffix == "256")
        {
            return new[] { "A", "H", "SHA256" };
        }

        if (suffix == "ALL")
        {
            return new[] { "A", "H", "ALL" };
        }

        return Array.Empty<string>();
    }
}

internal sealed class QuickAccessLayerProvider : ICommandPaletteLayerProvider
{
    public string RootToken => "Q";

    public bool TryBuild(
        ICommandPaletteLayerHost host,
        FeatureGateService featureGate,
        CommandPaletteUsageState usageState,
        CommandPaletteLayerQuery query,
        out CommandPalettePresentation? presentation)
    {
        QuickAccessStore store = host.GetQuickAccessStoreClone();
        string currentPath = host.GetCurrentBrowserPath();
        IReadOnlyList<string> backHistory = host.GetBackHistorySnapshot();
        IReadOnlyList<string> forwardHistory = host.GetForwardHistorySnapshot();

        var commands = new List<CommandLauncherCommand>();
        AddQuickAccessEntries(commands, host, store.Bookmarks, "登録先", "B", currentPath);
        AddQuickAccessEntries(commands, host, store.Aliases, "登録先(タブ表示)", "A", currentPath);
        AddQuickAccessEntries(commands, host, store.Recents, "最近", "R", currentPath);
        AddQuickAccessEntries(
            commands,
            host,
            QuickAccessService.BuildHistoryEntries(backHistory, forwardHistory),
            "履歴",
            "H",
            currentPath);

        string status = commands.Count == 0
            ? "QuickAccess の候補がありません。"
            : "Q: QuickAccess";

        presentation = CommandPalettePresentation.Layered(commands, status);
        return true;
    }

    private static void AddQuickAccessEntries(
        ICollection<CommandLauncherCommand> commands,
        ICommandPaletteLayerHost host,
        IReadOnlyList<QuickAccessEntry> entries,
        string scopeLabel,
        string scopeToken,
        string currentPath)
    {
        int index = 0;
        foreach (QuickAccessEntry entry in entries)
        {
            index++;
            string status = QuickAccessService.GetEntryStatusLabel(entry, currentPath);
            bool canExecute = !string.IsNullOrWhiteSpace(entry.Path) &&
                !string.Equals(status, "見つからない", StringComparison.OrdinalIgnoreCase);
            string displayName = string.IsNullOrWhiteSpace(entry.DisplayName)
                ? QuickAccessService.CreateDisplayName(entry.Path)
                : entry.DisplayName;

            string searchText = string.Join(" ", new[]
            {
                "Q",
                scopeToken,
                index.ToString(),
                scopeLabel,
                entry.DisplayName,
                entry.Path,
                status
            }.Where(text => !string.IsNullOrWhiteSpace(text)));

            commands.Add(new CommandLauncherCommand
            {
                Id = $"layer.quickaccess.{scopeToken.ToLowerInvariant()}.{index}",
                DisplayName = displayName,
                Description = string.IsNullOrWhiteSpace(status)
                    ? "移動"
                    : status,
                SearchText = searchText,
                SecondaryText = entry.Path,
                LayerBadge = $"Q {index}",
                LayerKind = entry.Kind switch
                {
                    QuickAccessEntryKind.Bookmark => "Bookmark",
                    QuickAccessEntryKind.Alias => "Alias",
                    QuickAccessEntryKind.Recent => "Recent",
                    QuickAccessEntryKind.History => "History",
                    _ => "QuickAccess"
                },
                Category = "QuickAccess",
                CanExecute = canExecute ? null : () => false,
                NonExecutableMessage = canExecute
                    ? null
                    : string.IsNullOrWhiteSpace(entry.Path)
                        ? "移動先が未設定です。"
                        : "移動先が見つかりません。",
                Execute = () => host.NavigateToPath(entry.Path)
            });
        }
    }
}

internal sealed class MarkSlotLayerProvider : ICommandPaletteLayerProvider
{
    public string RootToken => "M";

    public bool TryBuild(
        ICommandPaletteLayerHost host,
        FeatureGateService featureGate,
        CommandPaletteUsageState usageState,
        CommandPaletteLayerQuery query,
        out CommandPalettePresentation? presentation)
    {
        MarkSlotStore store = host.GetMarkSlotStoreClone();
        var commands = new List<CommandLauncherCommand>();
        bool showSaveCandidates = query.TailTokens.Count > 0 &&
            string.Equals(query.TailTokens[0], "S", StringComparison.OrdinalIgnoreCase);

        foreach (MarkSlotEntry slot in store.Slots.OrderBy(static slot => slot.SlotNumber))
        {
            if (showSaveCandidates)
            {
                commands.Add(BuildSaveCandidate(slot));
                continue;
            }

            string status = BuildSlotStatus(slot);
            string searchText = string.Join(" ", new[]
            {
                "M",
                "R",
                slot.SlotNumber.ToString(),
                slot.DisplayName,
                status,
                slot.SourceScope,
                slot.SourceCategoryName,
                slot.SourceTabDisplayName
            }.Where(text => !string.IsNullOrWhiteSpace(text)));

            commands.Add(new CommandLauncherCommand
            {
                Id = $"layer.markslot.restore.{slot.SlotNumber}",
                DisplayName = slot.DisplayName,
                Description = "復元",
                SearchText = searchText,
                SecondaryText = status,
                LayerBadge = $"M R {slot.SlotNumber}",
                LayerKind = $"Slot {slot.SlotNumber}",
                Category = "Mark",
                CanExecute = slot.SlotNumber > 0 ? null : () => false,
                NonExecutableMessage = slot.SlotNumber > 0 ? null : "このスロットは実行できません。",
                Execute = () => host.RestoreMarksFromSlot(slot.SlotNumber)
            });
        }

        presentation = CommandPalettePresentation.Layered(
            commands,
            commands.Count == 0 ? "MarkSlot の候補がありません。" : "M: MarkSlot");
        return true;
    }

    private static CommandLauncherCommand BuildSaveCandidate(MarkSlotEntry slot)
    {
        return new CommandLauncherCommand
        {
            Id = $"layer.markslot.save.{slot.SlotNumber}",
            DisplayName = $"Slot {slot.SlotNumber}",
            Description = "保存",
            SearchText = string.Join(" ", new[]
            {
                "M",
                "S",
                slot.SlotNumber.ToString(),
                "保存",
                "現在タブ",
                slot.DisplayName,
                slot.SourceScope,
                slot.SourceCategoryName,
                slot.SourceTabDisplayName
            }.Where(text => !string.IsNullOrWhiteSpace(text))),
            SecondaryText = BuildSaveSlotStatus(slot),
            LayerBadge = $"M S {slot.SlotNumber}",
            LayerKind = $"Slot {slot.SlotNumber}",
            Category = "Mark",
            CanExecute = () => false,
            NonExecutableMessage = "保存は後続Phase対象です。",
            Execute = () => { }
        };
    }

    private static string BuildSaveSlotStatus(MarkSlotEntry slot)
    {
        string scopeText = string.IsNullOrWhiteSpace(slot.SourceScope)
            ? "保存候補"
            : slot.SourceScope switch
            {
                MarkSlotSourceScopes.CurrentTab => "現在タブから保存",
                MarkSlotSourceScopes.CurrentCategory => "現在カテゴリから保存",
                MarkSlotSourceScopes.Workspace => "Workspaceから保存",
                MarkSlotSourceScopes.SlotSetOperation => "スロット演算から保存",
                _ => slot.SourceScope
            };

        return $"{scopeText} / Slot {slot.SlotNumber}";
    }

    private static string BuildSlotStatus(MarkSlotEntry slot)
    {
        string countText = $"{slot.Paths.Count}件";
        string scopeText = string.IsNullOrWhiteSpace(slot.SourceScope)
            ? "未保存"
            : slot.SourceScope switch
            {
                MarkSlotSourceScopes.CurrentTab => "現在タブ",
                MarkSlotSourceScopes.CurrentCategory => "現在カテゴリ",
                MarkSlotSourceScopes.Workspace => "Workspace",
                MarkSlotSourceScopes.SlotSetOperation => "スロット演算",
                _ => slot.SourceScope
            };

        return $"{countText} / {scopeText}";
    }
}

internal sealed class ArchiveLayerProvider : ICommandPaletteLayerProvider
{
    public string RootToken => "A";

    public bool TryBuild(
        ICommandPaletteLayerHost host,
        FeatureGateService featureGate,
        CommandPaletteUsageState usageState,
        CommandPaletteLayerQuery query,
        out CommandPalettePresentation? presentation)
    {
        SelectionResult selection = host.ResolveSelection();
        var commands = new List<CommandLauncherCommand>();
        bool hasArchiveSelection = selection.Count > 0 &&
            selection.FullPaths.All(path => File.Exists(path) && ArchiveFileTypeHelper.IsArchive(path));
        bool hasHashableSelection = selection.Count > 0 && selection.FullPaths.All(File.Exists) && !selection.FullPaths.Any(Directory.Exists);

        commands.Add(BuildListCommand(host, hasArchiveSelection, selection));
        commands.Add(BuildHashCommand(host, SevenZipHashAlgorithm.Sha256, hasHashableSelection, selection));
        commands.Add(BuildHashCommand(host, SevenZipHashAlgorithm.Crc32, hasHashableSelection, selection));
        commands.Add(BuildHashCommand(host, SevenZipHashAlgorithm.Sha1, hasHashableSelection, selection));
        commands.Add(BuildHashCommand(host, SevenZipHashAlgorithm.All, hasHashableSelection, selection));
        commands.Add(BuildDeferredTestCommand());

        presentation = CommandPalettePresentation.Layered(
            commands,
            commands.Count == 0
                ? "Archive の候補がありません。"
                : "A: Archive");
        return true;
    }

    private static CommandLauncherCommand BuildListCommand(ICommandPaletteLayerHost host, bool canExecute, SelectionResult selection)
    {
        string path = selection.FirstPath ?? string.Empty;
        string status = canExecute ? "一覧表示可" : BuildListUnavailableMessage(selection);
        return new CommandLauncherCommand
        {
            Id = "layer.archive.list",
            DisplayName = "archive 一覧",
            Description = "一覧",
            SearchText = string.Join(" ", new[] { "A", "L", "list", "archive", status, path }.Where(text => !string.IsNullOrWhiteSpace(text))),
            SecondaryText = path,
            LayerBadge = "A L",
            LayerKind = "List",
            Category = "Archive",
            CanExecute = canExecute ? null : () => false,
            NonExecutableMessage = canExecute ? null : status,
            Execute = () => host.ShowArchiveContents(path)
        };
    }

    private static CommandLauncherCommand BuildHashCommand(
        ICommandPaletteLayerHost host,
        SevenZipHashAlgorithm algorithm,
        bool canExecute,
        SelectionResult selection)
    {
        string algorithmName = algorithm switch
        {
            SevenZipHashAlgorithm.Crc32 => "CRC32",
            SevenZipHashAlgorithm.Crc64 => "CRC64",
            SevenZipHashAlgorithm.Sha1 => "SHA1",
            SevenZipHashAlgorithm.Sha256 => "SHA256",
            SevenZipHashAlgorithm.All => "ALL",
            _ => "SHA256"
        };

        string status = canExecute
            ? $"{algorithmName} 計算可"
            : BuildHashUnavailableMessage(selection);
        return new CommandLauncherCommand
        {
            Id = $"layer.archive.hash.{algorithmName.ToLowerInvariant()}",
            DisplayName = algorithmName,
            Description = "計算",
            SearchText = string.Join(" ", new[] { "A", "H", algorithmName, "hash", status }.Where(text => !string.IsNullOrWhiteSpace(text))),
            SecondaryText = selection.Count > 0 ? $"{selection.Count}件" : string.Empty,
            LayerBadge = $"A H {algorithmName}",
            LayerKind = algorithmName,
            Category = "Archive",
            CanExecute = canExecute ? null : () => false,
            NonExecutableMessage = canExecute ? null : status,
            Execute = () => _ = host.ExecuteArchiveHashAsync(algorithm)
        };
    }

    private static CommandLauncherCommand BuildDeferredTestCommand()
    {
        return new CommandLauncherCommand
        {
            Id = "layer.archive.test",
            DisplayName = "archive test",
            Description = "後続Phase対象",
            SearchText = "A T test archive deferred",
            LayerBadge = "A T",
            LayerKind = "Test",
            Category = "Archive",
            CanExecute = () => false,
            NonExecutableMessage = "archive test は後続Phase対象です。",
            Execute = () => { }
        };
    }

    private static string BuildListUnavailableMessage(SelectionResult selection)
    {
        if (selection.Count == 0)
        {
            return "アーカイブ一覧を開くには、ZIPなどのアーカイブファイルを選択してください。";
        }

        if (selection.FullPaths.Any(path => !File.Exists(path)))
        {
            return "アーカイブ一覧を開くには、ZIPなどのアーカイブファイルを選択してください。";
        }

        return "アーカイブ一覧を開くには、ZIPなどのアーカイブファイルを選択してください。";
    }

    private static string BuildHashUnavailableMessage(SelectionResult selection)
    {
        if (selection.Count == 0)
        {
            return "ハッシュを計算するファイルが選択されていません。";
        }

        if (selection.FullPaths.Any(Directory.Exists))
        {
            return "ハッシュを計算するファイルが選択されていません。";
        }

        if (selection.FullPaths.Any(path => !File.Exists(path)))
        {
            return "ハッシュを計算するファイルが選択されていません。";
        }

        return "ハッシュを計算するファイルが選択されていません。";
    }
}
