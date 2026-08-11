using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MidFD.Commands;
using MidFD.Configuration;
using MidFD.Dialogs;
using MidFD.Helpers;
using MidFD.Models;

namespace MidFD.Services;

public static class CommandPaletteUniversalSearchService
{
    private const int EmptyQueryLimitPerGroup = 10;
    private const int SearchQueryLimitPerGroup = 20;

    public enum UniversalSearchScope
    {
        All,
        Tabs,
        Destinations,
        Functions,
        Settings
    }

    public sealed record UniversalSearchScopeResult(
        UniversalSearchScope Scope,
        string NormalizedTail,
        bool IsCompact);

    private sealed record CommandPaletteCatalog(
        IReadOnlyList<CommandLauncherCommand> Functions,
        IReadOnlyList<CommandLauncherCommand> Settings);

    public static CommandPalettePresentation BuildPresentation(
        CommandPaletteSearchContext context,
        FeatureGateService featureGate,
        CommandPaletteUsageState usageState,
        string rawQuery,
        IReadOnlySet<string>? expandedSections = null)
    {
        _ = featureGate;
        _ = usageState;

        CommandPaletteCatalog catalog = BuildCatalog(context);
        bool isEmptyQuery = string.IsNullOrWhiteSpace(rawQuery);
        string normalizedQuery = NormalizeText(rawQuery);
        bool useSearchLimit = !isEmptyQuery;
        int limitPerGroup = useSearchLimit ? SearchQueryLimitPerGroup : EmptyQueryLimitPerGroup;

        var sections = new List<CommandPaletteSection>();
        AddSectionIfAny(
            sections,
            "機能",
            RankAndFilter(catalog.Functions, normalizedQuery, isEmptyQuery),
            limitPerGroup,
            expandedSections);
        AddSectionIfAny(
            sections,
            "設定",
            RankAndFilter(catalog.Settings.Where(x => x.Kind == "設定").ToList(), normalizedQuery, isEmptyQuery),
            limitPerGroup,
            expandedSections);
        AddSectionIfAny(
            sections,
            "管理",
            RankAndFilter(catalog.Settings.Where(x => x.Kind == "管理").ToList(), normalizedQuery, isEmptyQuery),
            limitPerGroup,
            expandedSections);

        string statusText = isEmptyQuery
            ? "機能 / 設定 / 管理 を検索できます。"
            : "機能 / 設定 / 管理 を検索中です。";

        return CommandPalettePresentation.Sectioned(sections, statusText);
    }

    internal static bool TryParseScope(string rawQuery, out UniversalSearchScopeResult? scopeResult)
    {
        scopeResult = null;
        _ = rawQuery;
        return false;
    }

    private static CommandPaletteCatalog BuildCatalog(CommandPaletteSearchContext context)
    {
        CommandRegistry registry = context.GetCommandRegistry();
        Dictionary<string, CommandDefinition> definitions = registry.GetMouseGestureAssignableCommands()
            .ToDictionary(static x => x.Id, StringComparer.OrdinalIgnoreCase);

        var functions = new List<CommandLauncherCommand>();
        var settings = new List<CommandLauncherCommand>();

        AddFunctionCandidates(context, definitions, functions);
        AddSettingsCandidates(context, definitions, settings);
        AddArchiveCandidates(context, functions);
        AddFileOperationCandidates(context, registry, definitions, functions);

        return new CommandPaletteCatalog(functions, settings);
    }

    private static void AddFunctionCandidates(
        CommandPaletteSearchContext context,
        IReadOnlyDictionary<string, CommandDefinition> definitions,
        ICollection<CommandLauncherCommand> commands)
    {
        TryAddCommand(definitions, CommandIds.BrowserExecute, definition => commands.Add(CreateFunctionCommand(
            definition, context, "開く", "選択項目をMidFDの対象別動作で開きます。", "open 開く 入る 対象別open")));
        TryAddCommand(definitions, CommandIds.BrowserReload, definition => commands.Add(CreateFunctionCommand(
            definition, context, "再読込", "現在ディレクトリを再読込します。", "reload refresh update 更新 再読込")));
        TryAddCommand(definitions, CommandIds.BrowserOpenExplorer, definition => commands.Add(CreateFunctionCommand(
            definition, context, "Explorerで開く", "現在ディレクトリをExplorerで開きます。", "explorer open folder current directory 表示 開く")));
        TryAddCommand(definitions, CommandIds.BrowserOpenShell, definition => commands.Add(CreateFunctionCommand(
            definition, context, "PowerShellをここで開く", "現在ディレクトリでPowerShellを開きます。", "powershell ps shell terminal ターミナル 開く")));
        TryAddCommand(definitions, CommandIds.BrowserOpenExternalEditor, definition => commands.Add(CreateFunctionCommand(
            definition, context, "外部エディタで開く", "選択ファイルを外部エディタで開きます。", "external editor edit エディタ 開く")));
        TryAddCommand(definitions, CommandIds.BrowserOpenCommandPrompt, definition => commands.Add(CreateFunctionCommand(
            definition, context, "コマンドプロンプトをここで開く", "現在ディレクトリでコマンドプロンプトを開きます。", "command prompt cmd shell terminal コマンドプロンプト")));
        TryAddCommand(definitions, CommandIds.BrowserPreview, definition => commands.Add(CreateFunctionCommand(
            definition, context, "プレビュー", "選択項目をプレビュー表示します。", "preview プレビュー 表示", CommandPaletteActionKind.OpenDialog)));
        TryAddCommand(definitions, CommandIds.BrowserSort, definition => commands.Add(CreateFunctionCommand(
            definition, context, "ソート", "ソート設定を開きます。", "sort ソート 順序", CommandPaletteActionKind.OpenDialog)));
        TryAddCommand(definitions, CommandIds.BrowserFilter, definition => commands.Add(CreateFunctionCommand(
            definition, context, "フィルタ", "フィルタ設定を開きます。", "filter フィルタ 絞り込み", CommandPaletteActionKind.OpenDialog)));
        TryAddCommand(definitions, CommandIds.BrowserTree, definition => commands.Add(CreateFunctionCommand(
            definition, context, "ツリー", "ツリーダイアログを開きます。", "tree ツリー", CommandPaletteActionKind.OpenDialog)));
        TryAddCommand(definitions, CommandIds.BrowserLogdisk, definition => commands.Add(CreateFunctionCommand(
            definition, context, "Logdisk", "Logdiskを実行します。", "logdisk ログディスク")));
        TryAddCommand(definitions, CommandIds.BrowserCopyFullPath, definition => commands.Add(CreateFunctionCommand(
            definition, context, "フルパスコピー", "選択またはマーク項目のフルパスをコピーします。", "copy path パス clipboard クリップボード コピー", CommandPaletteActionKind.Copy)));
        TryAddCommand(definitions, CommandIds.BrowserPathEntryOpen, definition => commands.Add(CreateFunctionCommand(
            definition, context, "パス入力", "現在パスの入力欄を開きます。", "path entry パス入力 入力欄", CommandPaletteActionKind.OpenDialog)));
        TryAddCommand(definitions, CommandIds.BrowserShowHelp, definition => commands.Add(CreateFunctionCommand(
            definition, context, "ヘルプ", "ヘルプ表示を開きます。", "help ヘルプ 説明", CommandPaletteActionKind.OpenDialog)));
    }

    private static void AddSettingsCandidates(
        CommandPaletteSearchContext context,
        IReadOnlyDictionary<string, CommandDefinition> definitions,
        ICollection<CommandLauncherCommand> commands)
    {
        TryAddCommand(definitions, CommandIds.AppOpenSettings, definition => commands.Add(CreateSettingsCommand(
            context, definition, "設定", "設定を開く", "設定ダイアログを開きます。", "setting settings config option preferences 設定", CommandPaletteActionKind.OpenSettings)));
        TryAddCommand(definitions, CommandIds.BrowserQuickAccess, definition => commands.Add(CreateSettingsCommand(
            context, definition, "管理", "QuickAccess設定を開く", "QuickAccess の管理画面を開きます。", "quick access qa 移動先 お気に入り 登録先 管理", CommandPaletteActionKind.OpenDialog)));
        TryAddCommand(definitions, CommandIds.BrowserOpenMarkSlot, definition => commands.Add(CreateSettingsCommand(
            context, definition, "管理", "Mark Slot管理を開く", "Mark Slot の管理画面を開きます。", "mark slot マーク スロット 保存枠 管理", CommandPaletteActionKind.OpenDialog)));
        TryAddCommand(definitions, CommandIds.AppOpenCommandList, definition => commands.Add(CreateSettingsCommand(
            context, definition, "管理", "コマンド一覧を開く", "コマンド一覧を開きます。", "command list コマンド一覧 list commands 管理", CommandPaletteActionKind.OpenDialog)));
        TryAddCommand(definitions, CommandIds.AppOpenSystemInformation, definition => commands.Add(CreateSettingsCommand(
            context, definition, "管理", "システム情報を開く", "ドライブ、メモリ、システム情報を表示します。", "system information 情報 drive memory system 管理", CommandPaletteActionKind.OpenDialog)));
        TryAddCommand(definitions, CommandIds.AppOpenControlPanel, definition => commands.Add(CreateSettingsCommand(
            context, definition, "管理", "コントロールパネルを開く", "Windows のコントロールパネルを開きます。", "control panel コントロールパネル windows 管理", CommandPaletteActionKind.OpenDialog)));

        CommandDefinition? settingsDefinition = FindDefinition(definitions, CommandIds.AppOpenSettings);
        if (settingsDefinition is null)
        {
            return;
        }

        commands.Add(CreateSyntheticSettingsCommand(
            settingsDefinition,
            "設定",
            "表示設定を開く",
            "設定ダイアログの表示タブを開きます。",
            "表示 display view font フォント 設定",
            CommandPaletteActionKind.OpenSettings,
            () => context.OpenSettingsForm(SettingsForm.InitialTab.Display)));
        commands.Add(CreateSyntheticSettingsCommand(
            settingsDefinition,
            "設定",
            "配色設定を開く",
            "設定ダイアログの配色タブを開きます。",
            "color theme 配色 色 テーマ 設定",
            CommandPaletteActionKind.OpenSettings,
            () => context.OpenSettingsForm(SettingsForm.InitialTab.Color)));
        commands.Add(CreateSyntheticSettingsCommand(
            settingsDefinition,
            "設定",
            "操作設定を開く",
            "設定ダイアログの操作タブを開きます。",
            "operation 操作 settings option 設定",
            CommandPaletteActionKind.OpenSettings,
            () => context.OpenSettingsForm(SettingsForm.InitialTab.Operation)));
        commands.Add(CreateSyntheticSettingsCommand(
            settingsDefinition,
            "設定",
            "キー割り当て設定を開く",
            "設定ダイアログの入力割り当てタブを開きます。",
            "input assignment keybind shortcut キー 割り当て ショートカット 設定",
            CommandPaletteActionKind.OpenSettings,
            () => context.OpenSettingsForm(SettingsForm.InitialTab.InputAssignment)));
        commands.Add(CreateSyntheticSettingsCommand(
            settingsDefinition,
            "設定",
            "外部連携設定を開く",
            "設定ダイアログの外部連携タブを開きます。",
            "external integration 外部 連携 editor sevenzip 設定",
            CommandPaletteActionKind.OpenSettings,
            () => context.OpenSettingsForm(SettingsForm.InitialTab.External)));
    }

    private static void AddArchiveCandidates(CommandPaletteSearchContext context, ICollection<CommandLauncherCommand> commands)
    {
        SelectionResult selection = context.ResolveSelection();
        bool hasArchiveSelection = selection.Count > 0 &&
            selection.FullPaths.All(path => File.Exists(path) && ArchiveFileTypeHelper.IsArchive(path));
        bool hasHashableSelection = selection.Count > 0 &&
            selection.FullPaths.All(File.Exists) &&
            !selection.FullPaths.Any(Directory.Exists);

        if (hasArchiveSelection)
        {
            string archiveTarget = BuildSelectionTargetText(selection);
            commands.Add(CreateSyntheticFunctionCommand(
                "function.archive.list",
                "アーカイブ情報を表示",
                "選択中アーカイブの内容を表示します。",
                $"7zip 7-zip zip archive アーカイブ list 一覧 情報 表示 {archiveTarget}",
                CommandPaletteActionKind.OpenDialog,
                () => context.ShowArchiveContents(selection.FirstPath!)));
        }

        if (!hasHashableSelection)
        {
            return;
        }

        string hashTarget = BuildSelectionTargetText(selection);
        commands.Add(CreateSyntheticFunctionCommand(
            "function.archive.hash.sha256",
            "SHA256を計算",
            "選択ファイルの SHA256 を計算します。",
            $"7zip 7-zip zip archive アーカイブ hash sha sha256 checksum チェックサム 検査 {hashTarget}",
            CommandPaletteActionKind.Execute,
            () => context.ExecuteArchiveHash(SevenZipHashAlgorithm.Sha256)));
        commands.Add(CreateSyntheticFunctionCommand(
            "function.archive.hash.crc32",
            "CRC32を計算",
            "選択ファイルの CRC32 を計算します。",
            $"7zip 7-zip zip archive アーカイブ hash crc crc32 checksum チェックサム 検査 {hashTarget}",
            CommandPaletteActionKind.Execute,
            () => context.ExecuteArchiveHash(SevenZipHashAlgorithm.Crc32)));
        commands.Add(CreateSyntheticFunctionCommand(
            "function.archive.hash.sha1",
            "SHA1を計算",
            "選択ファイルの SHA1 を計算します。",
            $"7zip 7-zip zip archive アーカイブ hash sha sha1 checksum チェックサム 検査 {hashTarget}",
            CommandPaletteActionKind.Execute,
            () => context.ExecuteArchiveHash(SevenZipHashAlgorithm.Sha1)));
        commands.Add(CreateSyntheticFunctionCommand(
            "function.archive.hash.all",
            "ハッシュをまとめて計算",
            "選択ファイルの主要ハッシュをまとめて計算します。",
            $"7zip 7-zip zip archive アーカイブ hash sha sha256 sha1 crc all checksum チェックサム 検査 {hashTarget}",
            CommandPaletteActionKind.Execute,
            () => context.ExecuteArchiveHash(SevenZipHashAlgorithm.All)));
    }

    private static void AddFileOperationCandidates(
        CommandPaletteSearchContext context,
        CommandRegistry registry,
        IReadOnlyDictionary<string, CommandDefinition> definitions,
        ICollection<CommandLauncherCommand> commands)
    {
        SelectionResult selection = context.ResolveSelection();
        string currentBrowserPath = context.GetCurrentBrowserPath();
        bool hasSelection = selection.Count > 0;
        bool hasSingleSelection = selection.Count == 1;
        string representativePath = selection.FirstPath ?? currentBrowserPath;
        string targetKindText = selection.HasMarkedSelection ? "マーク中" : "現在選択";
        string targetCountText = hasSelection ? $"{selection.Count}件" : "0件";
        string currentDirectoryText = string.IsNullOrWhiteSpace(currentBrowserPath) ? "現在ディレクトリ" : currentBrowserPath;
        string? noSelectionReason = hasSelection ? null : "選択項目がありません。";
        string? renameReason = hasSelection
            ? (hasSingleSelection ? null : "名前変更は単一対象のみです。")
            : "選択項目がありません。";

        AddConfirmedCommand(
            registry,
            definitions,
            commands,
            context,
            selection,
            CommandIds.FileCopy,
            "コピー",
            "選択項目をコピーします。",
            "copy clipboard クリップボード copy move rename delete",
            CommandPaletteActionKind.Copy,
            hasSelection,
            noSelectionReason,
            new CommandPaletteSafetyInfo
            {
                TargetKindText = targetKindText,
                TargetCountText = targetCountText,
                RepresentativePath = representativePath,
                ImpactText = "既存のコピー経路を実行します。",
                IsDestructive = false,
                ReasonText = noSelectionReason
            });

        AddConfirmedCommand(
            registry,
            definitions,
            commands,
            context,
            selection,
            CommandIds.FileMove,
            "移動",
            "選択項目を移動します。",
            "move 移動 clipboard クリップボード copy rename delete",
            CommandPaletteActionKind.Execute,
            hasSelection,
            noSelectionReason,
            new CommandPaletteSafetyInfo
            {
                TargetKindText = targetKindText,
                TargetCountText = targetCountText,
                RepresentativePath = representativePath,
                ImpactText = "既存の移動経路を実行します。",
                IsDestructive = true,
                ReasonText = noSelectionReason
            });

        AddConfirmedCommand(
            registry,
            definitions,
            commands,
            context,
            selection,
            CommandIds.FileRename,
            "名前変更",
            "選択項目を名前変更します。",
            "rename 名前変更 ren リネーム copy move delete",
            CommandPaletteActionKind.Execute,
            hasSingleSelection,
            renameReason,
            new CommandPaletteSafetyInfo
            {
                TargetKindText = targetKindText,
                TargetCountText = targetCountText,
                RepresentativePath = representativePath,
                ImpactText = "既存のリネーム経路を実行します。",
                IsDestructive = true,
                ReasonText = renameReason
            });

        AddConfirmedCommand(
            registry,
            definitions,
            commands,
            context,
            selection,
            CommandIds.FileDelete,
            "削除",
            "選択項目を削除します。",
            "delete 削除 remove trash recycle bin コピー 移動 名前変更",
            CommandPaletteActionKind.Execute,
            hasSelection,
            noSelectionReason,
            new CommandPaletteSafetyInfo
            {
                TargetKindText = targetKindText,
                TargetCountText = targetCountText,
                RepresentativePath = representativePath,
                ImpactText = "既存の削除経路を実行します。",
                IsDestructive = true,
                ReasonText = noSelectionReason
            });

        AddConfirmedCommand(
            registry,
            definitions,
            commands,
            context,
            selection,
            CommandIds.ClipboardPaste,
            "貼り付け",
            "クリップボード内容を貼り付けます。",
            "paste 貼り付け clipboard クリップボード current directory",
            CommandPaletteActionKind.Execute,
            true,
            null,
            new CommandPaletteSafetyInfo
            {
                TargetKindText = "現在ディレクトリ",
                TargetCountText = string.Empty,
                RepresentativePath = currentDirectoryText,
                DestinationOrOutputText = currentDirectoryText,
                ImpactText = "既存の貼り付け経路を実行します。",
                IsDestructive = true
            });

        AddConfirmedCommand(
            registry,
            definitions,
            commands,
            context,
            selection,
            CommandIds.BrowserCreateDirectory,
            "新規フォルダ",
            "現在ディレクトリに新しいフォルダを作成します。",
            "mkdir create folder 新規フォルダ current directory",
            CommandPaletteActionKind.OpenDialog,
            true,
            null,
            new CommandPaletteSafetyInfo
            {
                TargetKindText = "現在ディレクトリ",
                TargetCountText = "1件",
                RepresentativePath = currentDirectoryText,
                DestinationOrOutputText = currentDirectoryText,
                ImpactText = "既存のフォルダ作成経路を実行します。",
                IsDestructive = true
            });

        bool archiveSelection = selection.Count > 0 &&
            selection.FullPaths.All(path => File.Exists(path) && ArchiveFileTypeHelper.IsArchive(path));
        bool archiveOperands = selection.Count > 0 &&
            selection.FullPaths.All(path => File.Exists(path) || Directory.Exists(path));

        AddConfirmedCommand(
            registry,
            definitions,
            commands,
            context,
            selection,
            CommandIds.ArchivePack,
            "圧縮",
            "選択項目を圧縮します。",
            "pack compress 圧縮 archive zip sevenzip",
            CommandPaletteActionKind.Execute,
            archiveOperands,
            archiveOperands ? null : "圧縮対象がありません。",
            new CommandPaletteSafetyInfo
            {
                TargetKindText = targetKindText,
                TargetCountText = archiveOperands ? targetCountText : "0件",
                RepresentativePath = representativePath,
                ImpactText = "既存の圧縮経路を実行します。",
                IsDestructive = true,
                ReasonText = archiveOperands ? null : "圧縮対象がありません。"
            });

        AddConfirmedCommand(
            registry,
            definitions,
            commands,
            context,
            selection,
            CommandIds.ArchiveUnpack,
            "解凍",
            "選択ファイルを解凍します。",
            "unpack extract 解凍 archive zip sevenzip",
            CommandPaletteActionKind.Execute,
            archiveSelection,
            archiveSelection ? null : "解凍対象のアーカイブがありません。",
            new CommandPaletteSafetyInfo
            {
                TargetKindText = targetKindText,
                TargetCountText = archiveSelection ? targetCountText : "0件",
                RepresentativePath = representativePath,
                ImpactText = "既存の解凍経路を実行します。",
                IsDestructive = true,
                ReasonText = archiveSelection ? null : "解凍対象のアーカイブがありません。"
            });
    }

    private static CommandLauncherCommand CreateFunctionCommand(
        CommandDefinition definition,
        CommandPaletteSearchContext context,
        string title,
        string subtitle,
        string keywords,
        CommandPaletteActionKind actionKind = CommandPaletteActionKind.Execute)
    {
        return CreateCommand(
            definition,
            "機能",
            "機能",
            title,
            subtitle,
            keywords,
            actionKind,
            () => context.ExecuteCommandFromUi(definition.Id, definition.Scope, "CommandPaletteFunctionalSearch"),
            context.ResolveKeyBindingText(definition.Id));
    }

    private static CommandLauncherCommand CreateSettingsCommand(
        CommandPaletteSearchContext context,
        CommandDefinition definition,
        string kind,
        string title,
        string subtitle,
        string keywords,
        CommandPaletteActionKind actionKind)
    {
        Action execute = definition.Id switch
        {
            CommandIds.AppOpenSettings => context.OpenSettingsForm,
            CommandIds.AppOpenCommandList => context.ShowCommandList,
            CommandIds.AppOpenSystemInformation => context.ShowSystemInformationDialog,
            CommandIds.AppOpenControlPanel => context.OpenControlPanel,
            _ => () => context.ExecuteCommandFromUi(definition.Id, definition.Scope, "CommandPaletteFunctionalSearch")
        };

        return CreateCommand(
            definition,
            kind,
            kind,
            title,
            subtitle,
            keywords,
            actionKind,
            execute,
            context.ResolveKeyBindingText(definition.Id));
    }

    private static CommandLauncherCommand CreateSyntheticSettingsCommand(
        CommandDefinition definition,
        string kind,
        string title,
        string subtitle,
        string keywords,
        CommandPaletteActionKind actionKind,
        Action execute)
    {
        return CreateCommand(
            definition,
            kind,
            kind,
            title,
            subtitle,
            keywords,
            actionKind,
            execute,
            "未割り当て");
    }

    private static CommandLauncherCommand CreateSyntheticFunctionCommand(
        string id,
        string title,
        string subtitle,
        string keywords,
        CommandPaletteActionKind actionKind,
        Action execute,
        CommandPaletteSafetyLevel safetyLevel = CommandPaletteSafetyLevel.Safe,
        CommandPaletteSafetyInfo? safetyInfo = null)
    {
        return new CommandLauncherCommand
        {
            Id = id,
            DisplayName = title,
            Group = "機能",
            Kind = "機能",
            Title = title,
            Subtitle = subtitle,
            Keywords = keywords,
            Description = subtitle,
            SearchText = keywords,
            SecondaryText = subtitle,
            ActionKind = actionKind,
            SafetyLevel = safetyLevel,
            SafetyInfo = safetyInfo ?? new CommandPaletteSafetyInfo(),
            CanExecute = () => true,
            Execute = execute,
            Category = "Archive",
            LayerKind = "機能",
            LayerBadge = "機能",
            KeyBindingText = "未割り当て"
        };
    }

    private static CommandLauncherCommand CreateCommand(
        CommandDefinition definition,
        string group,
        string kind,
        string title,
        string subtitle,
        string keywords,
        CommandPaletteActionKind actionKind,
        Action execute,
        string keyBindingText,
        CommandPaletteSafetyLevel safetyLevel = CommandPaletteSafetyLevel.Safe,
        CommandPaletteSafetyInfo? safetyInfo = null,
        Func<bool>? canExecute = null,
        string? nonExecutableMessage = null)
    {
        string mergedKeywords = string.Join(" ", new[]
        {
            keywords,
            title,
            subtitle,
            definition.DisplayName,
            definition.Description,
            definition.Id,
            group,
            kind
        }.Where(text => !string.IsNullOrWhiteSpace(text)));

        return new CommandLauncherCommand
        {
            Id = definition.Id,
            DisplayName = title,
            Group = group,
            Kind = kind,
            Title = title,
            Subtitle = subtitle,
            Keywords = mergedKeywords,
            Description = definition.Description,
            SearchText = mergedKeywords,
            SecondaryText = subtitle,
            ActionKind = actionKind,
            SafetyLevel = safetyLevel,
            SafetyInfo = safetyInfo ?? new CommandPaletteSafetyInfo(),
            CanExecute = canExecute ?? (() => true),
            NonExecutableMessage = nonExecutableMessage,
            Execute = execute,
            KeyBindingText = keyBindingText,
            Category = group,
            LayerKind = kind,
            LayerBadge = kind
        };
    }

    private static void AddConfirmedCommand(
        CommandRegistry registry,
        IReadOnlyDictionary<string, CommandDefinition> definitions,
        ICollection<CommandLauncherCommand> commands,
        CommandPaletteSearchContext context,
        SelectionResult selectionSnapshot,
        string commandId,
        string title,
        string subtitle,
        string keywords,
        CommandPaletteActionKind actionKind,
        bool canExecute,
        string? nonExecutableMessage,
        CommandPaletteSafetyInfo safetyInfo)
    {
        TryAddCommand(registry, definitions, commandId, definition =>
        {
            commands.Add(CreateCommand(
                definition,
                "機能",
                "機能",
                title,
                subtitle,
                keywords,
                actionKind,
                () => context.ExecuteCommandFromUi(definition.Id, definition.Scope, "CommandPaletteFunctionalSearch", selectionSnapshot),
                context.ResolveKeyBindingText(definition.Id),
                canExecute ? CommandPaletteSafetyLevel.Confirm : CommandPaletteSafetyLevel.Unsupported,
                safetyInfo,
                canExecute ? () => true : () => false,
                nonExecutableMessage));
        });
    }

    private static IReadOnlyList<CommandLauncherCommand> RankAndFilter(
        IReadOnlyList<CommandLauncherCommand> source,
        string normalizedQuery,
        bool isEmptyQuery)
    {
        IEnumerable<CommandLauncherCommand> candidates = source
            .Where(command => command.SafetyLevel != CommandPaletteSafetyLevel.Deferred);

        if (!isEmptyQuery)
        {
            candidates = candidates
                .Select(command => (command, score: ScoreCandidate(command, normalizedQuery)))
                .Where(item => item.score >= 0)
                .OrderByDescending(item => item.score)
                .ThenBy(item => GetSafetyOrder(item.command.SafetyLevel))
                .ThenBy(item => GetGroupOrder(item.command.Group))
                .ThenBy(item => item.command.Title, StringComparer.OrdinalIgnoreCase)
                .Select(item =>
                {
                    item.command.Score = item.score;
                    return item.command;
                });
        }
        else
        {
            candidates = candidates
                .OrderByDescending(GetEmptyQueryBoost)
                .ThenBy(item => GetSafetyOrder(item.SafetyLevel))
                .ThenBy(item => GetGroupOrder(item.Group))
                .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
                .Select(command =>
                {
                    command.Score = 0;
                    return command;
                });
        }

        return candidates.ToList();
    }

    private static int ScoreCandidate(CommandLauncherCommand command, string normalizedQuery)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            return 0;
        }

        string[] tokens = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return 0;
        }

        int total = 0;
        foreach (string token in tokens)
        {
            int best = -1;
            foreach ((string? value, int weight) in GetSearchFields(command))
            {
                int score = ScoreField(value, token, weight);
                if (score > best)
                {
                    best = score;
                }
            }

            if (best < 0)
            {
                return -1;
            }

            total += best;
        }

        return total;
    }

    private static IEnumerable<(string? Value, int Weight)> GetSearchFields(CommandLauncherCommand command)
    {
        yield return (command.Title, 600);
        yield return (command.DisplayName, 600);
        yield return (command.Keywords, 500);
        yield return (command.SearchText, 500);
        yield return (command.Subtitle, 320);
        yield return (command.SecondaryText, 320);
        yield return (command.Description, 180);
        yield return (command.Group, 240);
        yield return (command.Kind, 240);
        yield return (command.Category, 220);
        yield return (command.LayerKind, 160);
        yield return (command.LayerBadge, 140);
        yield return (command.SafetyInfo.TargetKindText, 200);
        yield return (command.SafetyInfo.TargetCountText, 180);
        yield return (command.SafetyInfo.RepresentativePath, 120);
        yield return (command.SafetyInfo.DestinationOrOutputText, 180);
        yield return (command.SafetyInfo.ImpactText, 160);
        yield return (command.SafetyInfo.ReasonText, 220);
        yield return (command.Id, 80);
    }

    private static int GetSafetyOrder(CommandPaletteSafetyLevel safetyLevel)
    {
        return safetyLevel switch
        {
            CommandPaletteSafetyLevel.Safe => 0,
            CommandPaletteSafetyLevel.Confirm => 1,
            CommandPaletteSafetyLevel.Unsupported => 2,
            CommandPaletteSafetyLevel.Deferred => 3,
            _ => 4
        };
    }

    private static int ScoreField(string? value, string token, int weight)
    {
        string normalizedValue = NormalizeText(value);
        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            return -1;
        }

        if (string.Equals(normalizedValue, token, StringComparison.OrdinalIgnoreCase))
        {
            return weight * 4;
        }

        if (normalizedValue.StartsWith(token, StringComparison.OrdinalIgnoreCase))
        {
            return weight * 3;
        }

        if (normalizedValue.Contains(token, StringComparison.OrdinalIgnoreCase))
        {
            return weight;
        }

        return -1;
    }

    private static int GetEmptyQueryBoost(CommandLauncherCommand command)
    {
        int boost = 0;
        if (!string.IsNullOrWhiteSpace(command.Subtitle))
        {
            boost += 1;
        }

        if (!string.IsNullOrWhiteSpace(command.Keywords))
        {
            boost += 1;
        }

        return boost;
    }

    private static int GetGroupOrder(string? group)
    {
        return group switch
        {
            "機能" => 0,
            "設定・管理" => 1,
            _ => 2
        };
    }

    private static void AddSectionIfAny(
        List<CommandPaletteSection> sections,
        string title,
        IReadOnlyList<CommandLauncherCommand> commands,
        int visibleLimit,
        IReadOnlySet<string>? expandedSections)
    {
        if (commands.Count == 0)
        {
            return;
        }

        bool isExpanded = expandedSections != null && expandedSections.Contains(title);
        int limit = isExpanded ? commands.Count : visibleLimit;

        sections.Add(new CommandPaletteSection(title, commands.Take(limit).ToList(), commands.Count));
    }

    private static CommandDefinition? FindDefinition(IReadOnlyDictionary<string, CommandDefinition> definitions, string commandId)
    {
        return definitions.TryGetValue(commandId, out CommandDefinition? definition) ? definition : null;
    }

    private static void TryAddCommand(
        IReadOnlyDictionary<string, CommandDefinition> definitions,
        string commandId,
        Action<CommandDefinition> addAction)
    {
        if (definitions.TryGetValue(commandId, out CommandDefinition? definition))
        {
            addAction(definition);
        }
    }

    private static void TryAddCommand(
        CommandRegistry registry,
        IReadOnlyDictionary<string, CommandDefinition> definitions,
        string commandId,
        Action<CommandDefinition> addAction)
    {
        if (definitions.TryGetValue(commandId, out CommandDefinition? definition))
        {
            addAction(definition);
            return;
        }

        CommandDefinition? fallbackDefinition = registry.Find(commandId);
        if (fallbackDefinition is not null)
        {
            addAction(fallbackDefinition);
        }
    }

    private static string BuildSelectionTargetText(SelectionResult selection)
    {
        if (selection.Count <= 0)
        {
            return string.Empty;
        }

        return selection.Count == 1
            ? selection.FirstFileName ?? selection.FirstPath ?? string.Empty
            : $"{selection.Count}件";
    }

    private static string NormalizeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string normalized = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        bool lastWasSpace = false;

        foreach (char ch in normalized)
        {
            char mapped = ch switch
            {
                '\\' or '/' or '-' or '_' or '.' or ':' or ';' or ',' or '|' or '(' or ')' or '[' or ']' or '{' or '}' or '+' or '=' => ' ',
                _ when char.IsWhiteSpace(ch) => ' ',
                _ => ch
            };

            if (mapped == ' ')
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            builder.Append(mapped);
            lastWasSpace = false;
        }

        return builder.ToString().Trim();
    }
}
