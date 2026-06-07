using MidFD.Commands;
using MidFD.Models;

namespace MidFD.Helpers;

internal sealed class MainMenuConstructionCoordinator
{
    internal delegate ToolStripMenuItem MenuItemFactory(
        string text,
        EventHandler onClick,
        bool browserOnly = false,
        bool requiresIdle = false,
        bool requiresSelection = false,
        bool requiresFile = false,
        bool requiresEditorTarget = false,
        bool requiresExactlyTwoSelection = false,
        bool requiresTwoFiles = false,
        string? shortcutHint = null);

    internal sealed class BuildContext
    {
        public required MenuItemFactory CreateMenuItem { get; init; }
        public required Func<FunctionKeyAction, string, string?, string> GetFunctionAwareShortcutHint { get; init; }
        public required Func<bool> IsWorkspaceSnapshotEnabled { get; init; }

        public required Action ExecuteCurrentFile { get; init; }
        public required Action ExecuteAttribute { get; init; }
        public required Action ExecuteCopy { get; init; }
        public required Action ExecuteMove { get; init; }
        public required Action ExecuteRename { get; init; }
        public required Action ExecuteDelete { get; init; }
        public required Action EmptyMidFdManagedTrash { get; init; }
        public required Action ExecuteCreateDirectory { get; init; }
        public required Action ExecuteCreateFile { get; init; }
        public required Action CloseMainForm { get; init; }
        public required Action ExecuteSort { get; init; }
        public required Action ExecuteFilter { get; init; }
        public required Action SetFileDisplayModeNameOnly { get; init; }
        public required Action SetFileDisplayModeNameSize { get; init; }
        public required Action SetFileDisplayModeNameSizeDate { get; init; }
        public required Action UpdateFileDisplayModeMenuChecks { get; init; }
        public required Action ReloadCurrentDirectory { get; init; }
        public required Action OpenActiveTabFilterLockDialog { get; init; }
        public required Action ClearActiveTabFilterLock { get; init; }
        public required Action ExecutePreviewLaunch { get; init; }
        public required Action ExecuteLogdisk { get; init; }
        public required Action OpenFileListColorSettings { get; init; }
        public required Action NavigateParent { get; init; }
        public required Action ExecuteDriveRoot { get; init; }
        public required Action OpenExplorer { get; init; }
        public required Action ExecuteTop { get; init; }
        public required Action ExecuteBottom { get; init; }
        public required Action ExecuteTreeDialog { get; init; }
        public required Action ExecuteQuickAccess { get; init; }
        public required Action NavigateBack { get; init; }
        public required Action NavigateForward { get; init; }
        public required Action CreateNewBrowserTab { get; init; }
        public required Action ToggleActiveBrowserTabLock { get; init; }
        public required Action ToggleActiveBrowserTabReadOnly { get; init; }
        public required Action SelectNextBrowserTab { get; init; }
        public required Action SelectPreviousBrowserTab { get; init; }
        public required Action CloseCurrentBrowserTab { get; init; }
        public required Action ExecutePack { get; init; }
        public required Action ExecuteUnpack { get; init; }
        public required Action ExecuteOpenWithEditor { get; init; }
        public required Action ExecuteOpenWithDiff { get; init; }
        public required Action OpenPowerShell { get; init; }
        public required Action CopyFullPath { get; init; }
        public required Action OpenMarkSlotDialog { get; init; }
        public required Action OpenWorkspaceSnapshotDialog { get; init; }
        public required Action ShowSystemInformation { get; init; }
        public required Action OpenSettings { get; init; }
        public required Action ShowMenuKeyHint { get; init; }
        public required Action ShowCommandList { get; init; }
        public required Action ShowVersionInfo { get; init; }
    }

    internal sealed class BuildResult
    {
        public required ToolStripMenuItem FileMenu { get; init; }
        public required ToolStripMenuItem ViewMenu { get; init; }
        public required ToolStripMenuItem MoveMenu { get; init; }
        public required ToolStripMenuItem ToolsMenu { get; init; }
        public required ToolStripMenuItem HelpMenu { get; init; }
        public required ToolStripMenuItem FileDisplayModeSubMenu { get; init; }
        public required ToolStripMenuItem FileDisplayModeNameOnlyMenuItem { get; init; }
        public required ToolStripMenuItem FileDisplayModeNameSizeMenuItem { get; init; }
        public required ToolStripMenuItem FileDisplayModeNameSizeDateMenuItem { get; init; }
        public required ToolStripMenuItem ReloadCurrentDirectoryMenuItem { get; init; }
        public required ToolStripMenuItem ClearTabFilterLockMenuItem { get; init; }
        public required ToolStripMenuItem ToggleBrowserTabLockMenuItem { get; init; }
        public required ToolStripMenuItem ToggleBrowserTabReadOnlyMenuItem { get; init; }
    }

    public BuildResult Build(BuildContext context)
    {
        ToolStripMenuItem fileMenu = new("ファイル(&F)");
        fileMenu.DropDownItems.Add(context.CreateMenuItem("内容確認/実行(eXecute)(&O)", (s, e) => context.ExecuteCurrentFile(), browserOnly: true, requiresIdle: true, requiresSelection: true, requiresFile: true, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: context.GetFunctionAwareShortcutHint(FunctionKeyAction.Execute, "X", null)));
        fileMenu.DropDownItems.Add(context.CreateMenuItem("属性変更(&A)", (s, e) => context.ExecuteAttribute(), browserOnly: true, requiresIdle: true, requiresSelection: true, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: context.GetFunctionAwareShortcutHint(FunctionKeyAction.None, string.Empty, "Shift+F1")));
        fileMenu.DropDownItems.Add(context.CreateMenuItem("コピー(&C)", (s, e) => context.ExecuteCopy(), browserOnly: true, requiresIdle: true, requiresSelection: true, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: context.GetFunctionAwareShortcutHint(FunctionKeyAction.Copy, "C", null)));
        fileMenu.DropDownItems.Add(context.CreateMenuItem("移動(&M)", (s, e) => context.ExecuteMove(), browserOnly: true, requiresIdle: true, requiresSelection: true, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: context.GetFunctionAwareShortcutHint(FunctionKeyAction.None, "M", "Shift+F3")));
        fileMenu.DropDownItems.Add(context.CreateMenuItem("名前変更(&R)", (s, e) => context.ExecuteRename(), browserOnly: true, requiresIdle: true, requiresSelection: true, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: context.GetFunctionAwareShortcutHint(FunctionKeyAction.Rename, "R", null)));
        fileMenu.DropDownItems.Add(context.CreateMenuItem("削除(&D)", (s, e) => context.ExecuteDelete(), browserOnly: true, requiresIdle: true, requiresSelection: true, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: "D / Delete"));
        fileMenu.DropDownItems.Add(context.CreateMenuItem("MidFD管理ゴミ箱を空にする(&T)", (s, e) => context.EmptyMidFdManagedTrash(), browserOnly: true, requiresIdle: true, requiresSelection: false, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: null));
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add(context.CreateMenuItem("新規フォルダ(&K)", (s, e) => context.ExecuteCreateDirectory(), browserOnly: true, requiresIdle: true, requiresSelection: false, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: context.GetFunctionAwareShortcutHint(FunctionKeyAction.None, "K", "Shift+F5")));
        fileMenu.DropDownItems.Add(context.CreateMenuItem("新規ファイル(&N)", (s, e) => context.ExecuteCreateFile(), browserOnly: true, requiresIdle: true, requiresSelection: false, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: "N"));
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add(context.CreateMenuItem("終了(&X)", (s, e) => context.CloseMainForm(), browserOnly: false, requiresIdle: false, requiresSelection: false, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: null));

        ToolStripMenuItem viewMenu = new("表示(&V)");
        viewMenu.DropDownItems.Add(context.CreateMenuItem("ソート(&S)", (s, e) => context.ExecuteSort(), browserOnly: true, requiresIdle: true, requiresSelection: false, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: context.GetFunctionAwareShortcutHint(FunctionKeyAction.Sort, "S", null)));
        viewMenu.DropDownItems.Add(context.CreateMenuItem("フィルタ(&F)", (s, e) => context.ExecuteFilter(), browserOnly: true, requiresIdle: true, requiresSelection: false, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: context.GetFunctionAwareShortcutHint(FunctionKeyAction.Filter, "F / Ctrl+F", null)));
        ToolStripMenuItem fileDisplayModeSubMenu = new MidFD.Controls.TightCascadeToolStripMenuItem("一覧表示");
        ToolStripMenuItem fileDisplayModeNameOnlyMenuItem = context.CreateMenuItem("ファイル名のみ", (s, e) => context.SetFileDisplayModeNameOnly(), browserOnly: true, requiresIdle: true, requiresSelection: false, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: "Ctrl+1");
        ToolStripMenuItem fileDisplayModeNameSizeMenuItem = context.CreateMenuItem("サイズ", (s, e) => context.SetFileDisplayModeNameSize(), browserOnly: true, requiresIdle: true, requiresSelection: false, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: "Ctrl+2");
        ToolStripMenuItem fileDisplayModeNameSizeDateMenuItem = context.CreateMenuItem("サイズ・更新日時", (s, e) => context.SetFileDisplayModeNameSizeDate(), browserOnly: true, requiresIdle: true, requiresSelection: false, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: "Ctrl+3");
        fileDisplayModeSubMenu.DropDownItems.Add(fileDisplayModeNameOnlyMenuItem);
        fileDisplayModeSubMenu.DropDownItems.Add(fileDisplayModeNameSizeMenuItem);
        fileDisplayModeSubMenu.DropDownItems.Add(fileDisplayModeNameSizeDateMenuItem);
        viewMenu.DropDownItems.Add(fileDisplayModeSubMenu);
        viewMenu.DropDownOpening += (s, e) => context.UpdateFileDisplayModeMenuChecks();
        fileDisplayModeSubMenu.DropDownOpening += (s, e) => context.UpdateFileDisplayModeMenuChecks();

        ToolStripMenuItem reloadCurrentDirectoryMenuItem = context.CreateMenuItem("現在ディレクトリを再読込(&R)", (s, e) => context.ReloadCurrentDirectory(), browserOnly: true, requiresIdle: false, requiresSelection: false, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: context.GetFunctionAwareShortcutHint(FunctionKeyAction.Reload, "Ctrl+R", "Shift+F7"));
        viewMenu.DropDownItems.Add(reloadCurrentDirectoryMenuItem);
        viewMenu.DropDownItems.Add(context.CreateMenuItem("現在タブのフィルタロック...(&L)", (s, e) => context.OpenActiveTabFilterLockDialog(), browserOnly: true, requiresIdle: true, requiresSelection: false, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: "Ctrl+Shift+L"));
        ToolStripMenuItem clearTabFilterLockMenuItem = context.CreateMenuItem("現在タブのフィルタロックを解除(&U)", (s, e) => context.ClearActiveTabFilterLock(), browserOnly: true, requiresIdle: true, requiresSelection: false, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: null);
        viewMenu.DropDownItems.Add(clearTabFilterLockMenuItem);
        viewMenu.DropDownItems.Add(context.CreateMenuItem("内蔵Viewer / 画像Viewer(&P)", (s, e) => context.ExecutePreviewLaunch(), browserOnly: true, requiresIdle: true, requiresSelection: true, requiresFile: true, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: context.GetFunctionAwareShortcutHint(FunctionKeyAction.None, "V / Enter", "Shift+F9")));
        viewMenu.DropDownItems.Add(context.CreateMenuItem("Logdisk(&L)", (s, e) => context.ExecuteLogdisk(), browserOnly: true, requiresIdle: true, requiresSelection: false, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: context.GetFunctionAwareShortcutHint(FunctionKeyAction.Logdisk, "L", null)));
        viewMenu.DropDownItems.Add(new ToolStripSeparator());
        viewMenu.DropDownItems.Add(context.CreateMenuItem("配色設定...(&A)", (s, e) => context.OpenFileListColorSettings(), browserOnly: false, requiresIdle: false, requiresSelection: false, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: null));

        ToolStripMenuItem moveMenu = new("移動(&G)");
        moveMenu.DropDownItems.Add(context.CreateMenuItem("親へ(&U)", (s, e) => context.NavigateParent(), browserOnly: true, requiresIdle: true, requiresSelection: false, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: "Backspace"));
        moveMenu.DropDownItems.Add(context.CreateMenuItem("ルートへ(&R)", (s, e) => context.ExecuteDriveRoot(), browserOnly: true, requiresIdle: true, requiresSelection: false, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: "\\"));
        moveMenu.DropDownItems.Add(context.CreateMenuItem("エクスプローラーで開く(&X)", (s, e) => context.OpenExplorer(), browserOnly: true, requiresIdle: true, requiresSelection: false, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: "Alt+F2"));
        moveMenu.DropDownItems.Add(context.CreateMenuItem("Top(&T)", (s, e) => context.ExecuteTop(), browserOnly: true, requiresIdle: true, requiresSelection: false, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: context.GetFunctionAwareShortcutHint(FunctionKeyAction.Top, string.Empty, null)));
        moveMenu.DropDownItems.Add(context.CreateMenuItem("Bottom(&B)", (s, e) => context.ExecuteBottom(), browserOnly: true, requiresIdle: true, requiresSelection: false, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: context.GetFunctionAwareShortcutHint(FunctionKeyAction.Bottom, string.Empty, null)));
        moveMenu.DropDownItems.Add(context.CreateMenuItem("Tree(&E)", (s, e) => context.ExecuteTreeDialog(), browserOnly: true, requiresIdle: true, requiresSelection: false, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: context.GetFunctionAwareShortcutHint(FunctionKeyAction.Tree, "T", null)));
        moveMenu.DropDownItems.Add(context.CreateMenuItem("QuickAccess(&Q)", (s, e) => context.ExecuteQuickAccess(), browserOnly: true, requiresIdle: true, requiresSelection: false, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: "Q"));
        moveMenu.DropDownItems.Add(context.CreateMenuItem("戻る(&A)", (s, e) => context.NavigateBack(), browserOnly: true, requiresIdle: true, requiresSelection: false, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: "Alt+Left"));
        moveMenu.DropDownItems.Add(context.CreateMenuItem("進む(&D)", (s, e) => context.NavigateForward(), browserOnly: true, requiresIdle: true, requiresSelection: false, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: "Alt+Right"));
        moveMenu.DropDownItems.Add(new ToolStripSeparator());
        moveMenu.DropDownItems.Add(context.CreateMenuItem("新しいタブを作る(&N)", (s, e) => context.CreateNewBrowserTab(), browserOnly: true, requiresIdle: true, requiresSelection: false, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: "Ctrl+T"));
        ToolStripMenuItem toggleBrowserTabLockMenuItem = context.CreateMenuItem("現在のタブを固定(&K)", (s, e) => context.ToggleActiveBrowserTabLock(), browserOnly: true, requiresIdle: true, requiresSelection: false, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: "Ctrl+L");
        moveMenu.DropDownItems.Add(toggleBrowserTabLockMenuItem);
        ToolStripMenuItem toggleBrowserTabReadOnlyMenuItem = context.CreateMenuItem("現在のタブを ReadOnly にする(&Y)", (s, e) => context.ToggleActiveBrowserTabReadOnly(), browserOnly: true, requiresIdle: true, requiresSelection: false, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: null);
        moveMenu.DropDownItems.Add(toggleBrowserTabReadOnlyMenuItem);
        moveMenu.DropDownItems.Add(context.CreateMenuItem("次のタブへ(&X)", (s, e) => context.SelectNextBrowserTab(), browserOnly: true, requiresIdle: true, requiresSelection: false, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: "Ctrl+Right / Ctrl+Tab"));
        moveMenu.DropDownItems.Add(context.CreateMenuItem("前のタブへ(&P)", (s, e) => context.SelectPreviousBrowserTab(), browserOnly: true, requiresIdle: true, requiresSelection: false, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: "Ctrl+Left / Ctrl+Shift+Tab"));
        moveMenu.DropDownItems.Add(context.CreateMenuItem("現在のタブを閉じる(&W)", (s, e) => context.CloseCurrentBrowserTab(), browserOnly: true, requiresIdle: true, requiresSelection: false, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: "Ctrl+W"));

        ToolStripMenuItem toolsMenu = new("ツール(&T)");
        toolsMenu.DropDownItems.Add(context.CreateMenuItem("圧縮(&P)", (s, e) => context.ExecutePack(), browserOnly: true, requiresIdle: true, requiresSelection: true, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: context.GetFunctionAwareShortcutHint(FunctionKeyAction.None, "P", "Shift+F10")));
        toolsMenu.DropDownItems.Add(context.CreateMenuItem("解凍(&U)", (s, e) => context.ExecuteUnpack(), browserOnly: true, requiresIdle: true, requiresSelection: true, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: context.GetFunctionAwareShortcutHint(FunctionKeyAction.Unpack, "U", null)));
        toolsMenu.DropDownItems.Add(context.CreateMenuItem("外部エディタで開く(&E)", (s, e) => context.ExecuteOpenWithEditor(), browserOnly: true, requiresIdle: true, requiresSelection: true, requiresFile: true, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: context.GetFunctionAwareShortcutHint(FunctionKeyAction.Edit, "E", "Shift+F8 / Shift+Enter")));
        toolsMenu.DropDownItems.Add(context.CreateMenuItem("外部 Diff (2件比較専用)(&D)", (s, e) => context.ExecuteOpenWithDiff(), browserOnly: true, requiresIdle: true, requiresSelection: true, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: true, requiresTwoFiles: true, shortcutHint: null));
        toolsMenu.DropDownItems.Add(context.CreateMenuItem("PowerShellをここで開く(&P)", (s, e) => context.OpenPowerShell(), browserOnly: true, requiresIdle: true, requiresSelection: false, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: context.GetFunctionAwareShortcutHint(FunctionKeyAction.None, "H", "Shift+F6")));
        toolsMenu.DropDownItems.Add(context.CreateMenuItem("フルパスをコピー(&Y)", (s, e) => context.CopyFullPath(), browserOnly: true, requiresIdle: true, requiresSelection: true, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: context.GetFunctionAwareShortcutHint(FunctionKeyAction.None, string.Empty, "Shift+Ctrl+C")));
        toolsMenu.DropDownItems.Add(new ToolStripSeparator());
        toolsMenu.DropDownItems.Add(context.CreateMenuItem("マーク一覧 / スロット(&M)", (s, e) => context.OpenMarkSlotDialog(), browserOnly: true, requiresIdle: true, requiresSelection: false, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: "Ctrl+M"));
        if (context.IsWorkspaceSnapshotEnabled())
        {
            toolsMenu.DropDownItems.Add(context.CreateMenuItem("Workspace スナップショット...(&W)", (s, e) => context.OpenWorkspaceSnapshotDialog(), browserOnly: true, requiresIdle: true, requiresSelection: false, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: null));
        }
        toolsMenu.DropDownItems.Add(context.CreateMenuItem("情報(&I)...", (s, e) => context.ShowSystemInformation(), browserOnly: true, requiresIdle: false, requiresSelection: false, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: null));
        toolsMenu.DropDownItems.Add(new ToolStripSeparator());
        toolsMenu.DropDownItems.Add(context.CreateMenuItem("設定(&O)", (s, e) => context.OpenSettings(), browserOnly: false, requiresIdle: false, requiresSelection: false, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: context.GetFunctionAwareShortcutHint(FunctionKeyAction.None, "O", "Alt+F5")));

        ToolStripMenuItem helpMenu = new("ヘルプ(&H)");
        helpMenu.DropDownItems.Add(context.CreateMenuItem("主なキー操作ヒント(&K)", (s, e) => context.ShowMenuKeyHint(), browserOnly: false, requiresIdle: false, requiresSelection: false, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: null));
        helpMenu.DropDownItems.Add(context.CreateMenuItem("コマンド一覧(&C)...", (s, e) => context.ShowCommandList(), browserOnly: false, requiresIdle: false, requiresSelection: false, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: null));
        helpMenu.DropDownItems.Add(context.CreateMenuItem("バージョン情報(&A)", (s, e) => context.ShowVersionInfo(), browserOnly: false, requiresIdle: false, requiresSelection: false, requiresFile: false, requiresEditorTarget: false, requiresExactlyTwoSelection: false, requiresTwoFiles: false, shortcutHint: null));

        return new BuildResult
        {
            FileMenu = fileMenu,
            ViewMenu = viewMenu,
            MoveMenu = moveMenu,
            ToolsMenu = toolsMenu,
            HelpMenu = helpMenu,
            FileDisplayModeSubMenu = fileDisplayModeSubMenu,
            FileDisplayModeNameOnlyMenuItem = fileDisplayModeNameOnlyMenuItem,
            FileDisplayModeNameSizeMenuItem = fileDisplayModeNameSizeMenuItem,
            FileDisplayModeNameSizeDateMenuItem = fileDisplayModeNameSizeDateMenuItem,
            ReloadCurrentDirectoryMenuItem = reloadCurrentDirectoryMenuItem,
            ClearTabFilterLockMenuItem = clearTabFilterLockMenuItem,
            ToggleBrowserTabLockMenuItem = toggleBrowserTabLockMenuItem,
            ToggleBrowserTabReadOnlyMenuItem = toggleBrowserTabReadOnlyMenuItem
        };
    }
}
