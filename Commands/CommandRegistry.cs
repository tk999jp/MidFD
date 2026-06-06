using System.Collections.ObjectModel;

namespace MidFD.Commands;

public sealed class CommandRegistry
{
    private readonly ReadOnlyCollection<CommandDefinition> _definitions;
    private readonly Dictionary<string, CommandDefinition> _map;

    public CommandRegistry()
    {
        var definitions = new List<CommandDefinition>
        {
            Create(CommandIds.BrowserNavigateParent, CommandScope.Browser, "親ディレクトリへ移動", "現在のディレクトリの親へ移動します。", true),
            Create(CommandIds.BrowserNavigateBack, CommandScope.Browser, "履歴: 戻る", "ディレクトリ履歴を1つ戻ります。", true),
            Create(CommandIds.BrowserNavigateForward, CommandScope.Browser, "履歴: 進む", "ディレクトリ履歴を1つ進みます。", true),
            Create(CommandIds.BrowserReload, CommandScope.Browser, "再読込", "現在ディレクトリを再読込します。", true),
            Create(CommandIds.BrowserMarkAllFiles, CommandScope.Browser, "ファイルのみ全選択", "ファイルのみを全選択/全解除します。", true),
            Create(CommandIds.BrowserMarkAllItems, CommandScope.Browser, "ディレクトリを含めて全選択", "ディレクトリを含めて全選択/全解除します。", true),
            Create(CommandIds.BrowserCursorTop, CommandScope.Browser, "先頭へ移動", "一覧の先頭へ移動します。", true),
            Create(CommandIds.BrowserCursorBottom, CommandScope.Browser, "末尾へ移動", "一覧の末尾へ移動します。", true),
            Create(CommandIds.BrowserOpenExplorer, CommandScope.Browser, "Explorerで開く", "現在ディレクトリをExplorerで開きます。", true),
            Create(CommandIds.BrowserOpenShell, CommandScope.Browser, "PowerShellをここで開く", "現在ディレクトリでPowerShellを開きます。", true),
            Create(CommandIds.BrowserOpenExternalEditor, CommandScope.Browser, "外部エディタで開く", "選択ファイルを外部エディタで開きます。", true),
            Create(CommandIds.BrowserSort, CommandScope.Browser, "ソート", "ソート設定を開きます。", true),
            Create(CommandIds.BrowserFilter, CommandScope.Browser, "フィルタ", "フィルタ設定を開きます。", true),
            Create(CommandIds.BrowserTree, CommandScope.Browser, "ツリー", "ツリーダイアログを開きます。", true),
            Create(CommandIds.BrowserQuickAccess, CommandScope.Browser, "QuickAccess", "QuickAccessを開きます。", true),
            Create(CommandIds.BrowserLogdisk, CommandScope.Browser, "Logdisk", "Logdiskを実行します。", true),
            Create(CommandIds.ArchiveUnpack, CommandScope.Browser, "解凍", "選択ファイルを解凍します。", true),
            Create(CommandIds.BrowserCopyFullPath, CommandScope.Browser, "フルパスコピー", "選択またはマーク項目のフルパスをコピーします。", true),
            Create(CommandIds.BrowserShowHelp, CommandScope.Browser, "ヘルプ", "ヘルプ表示を開きます。", true),
            Create(CommandIds.BrowserOpenMarkSlot, CommandScope.Browser, "マークスロット", "マークスロット画面を開きます。", true),
            Create(CommandIds.BrowserTabNew, CommandScope.Browser, "新しいタブを作る", "新しいBrowserタブを作成します。", true),
            Create(CommandIds.BrowserTabNext, CommandScope.Browser, "次のタブへ移動", "次のBrowserタブへ移動します。", true),
            Create(CommandIds.BrowserTabPrevious, CommandScope.Browser, "前のタブへ移動", "前のBrowserタブへ移動します。", true),
            Create(CommandIds.BrowserTabCategoryNext, CommandScope.Browser, "次のカテゴリへ移動", "次のBrowserタブカテゴリへ移動します。", true),
            Create(CommandIds.BrowserTabCategoryPrevious, CommandScope.Browser, "前のカテゴリへ移動", "前のBrowserタブカテゴリへ移動します。", true),
            Create(CommandIds.BrowserTabClose, CommandScope.Browser, "現在タブを閉じる", "現在のBrowserタブを閉じます。", true),
            Create(CommandIds.BrowserTabRestoreClosed, CommandScope.Browser, "閉じたタブを復元", "直前に閉じたBrowserタブを復元します。", true),
            Create(CommandIds.ClipboardPaste, CommandScope.Browser, "貼り付け", "クリップボード内容を貼り付けます。", true),
            Create("file.copy", CommandScope.Browser, "コピー", "選択項目をコピーします。", true, false),
            Create("file.move", CommandScope.Browser, "移動", "選択項目を移動します。", true, false),
            Create("file.rename", CommandScope.Browser, "名前変更", "選択項目を名前変更します。", true, false),
            Create("file.delete", CommandScope.Browser, "削除", "選択項目を削除します。", true, true),
            Create(CommandIds.EditUndo, CommandScope.Browser, "元に戻す", "直前の対象操作を元に戻します。", false),
            Create(CommandIds.EditRedo, CommandScope.Browser, "やり直し", "元に戻した操作をやり直します。", false),
            Create(CommandIds.AppOpenSystemInformation, CommandScope.Browser, "情報", "ドライブ、メモリ、システム情報を表示します。", true),
            Create(CommandIds.AppOpenNewInstance, CommandScope.Global, "MidFDをもう1枚立ち上げ", "現在パスで新しいMidFDウィンドウを起動します。", true),
            Create(CommandIds.AppOpenControlPanel, CommandScope.Global, "コントロールパネルを開く", "Windowsのコントロールパネルを開きます。", true),
            Create(CommandIds.AppOpenSettings, CommandScope.Global, "設定を開く", "設定ダイアログを開きます。", true),
            Create(CommandIds.AppOpenCommandLauncher, CommandScope.Global, "コマンドランチャーを開く", "コマンドランチャーを開きます。", true),
            Create(CommandIds.AppOpenCommandList, CommandScope.Global, "コマンド一覧", "コマンド一覧を開きます。", true)
        };

        _definitions = new ReadOnlyCollection<CommandDefinition>(definitions);
        _map = definitions.ToDictionary(static d => d.Id, StringComparer.Ordinal);
    }

    public IReadOnlyList<CommandDefinition> GetAll() => _definitions;

    public IReadOnlyList<CommandDefinition> GetMouseGestureAssignableCommands()
    {
        return _definitions
            .Where(static d =>
                (d.Scope == CommandScope.Browser || d.Scope == CommandScope.Global) &&
                d.IsCustomizable &&
                !d.IsDangerous)
            .ToArray();
    }

    public CommandDefinition? Find(string commandId)
    {
        if (string.IsNullOrWhiteSpace(commandId))
        {
            return null;
        }

        return _map.TryGetValue(commandId, out CommandDefinition? definition) ? definition : null;
    }

    private static CommandDefinition Create(
        string id,
        CommandScope scope,
        string displayName,
        string description,
        bool isCustomizable,
        bool isDangerous = false)
    {
        return new CommandDefinition
        {
            Id = id,
            Scope = scope,
            DisplayName = displayName,
            Description = description,
            IsCustomizable = isCustomizable,
            IsDangerous = isDangerous
        };
    }
}
