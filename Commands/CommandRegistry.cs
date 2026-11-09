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
            Create(CommandIds.BrowserNavigateDriveRoot, CommandScope.Browser, "ルートへ移動", "現在タブの固定ルート、または現在ドライブのルートへ移動します。", false),
            Create(CommandIds.BrowserNavigateBack, CommandScope.Browser, "履歴: 戻る", "ディレクトリ履歴を1つ戻ります。", true),
            Create(CommandIds.BrowserNavigateForward, CommandScope.Browser, "履歴: 進む", "ディレクトリ履歴を1つ進みます。", true),
            Create(CommandIds.BrowserNavigateToPath, CommandScope.Browser, "指定パスへ移動", "指定されたBrowser directoryへ移動します。", false),
            Create(CommandIds.BrowserReload, CommandScope.Browser, "再読込", "現在ディレクトリを再読込します。", true),
            Create(CommandIds.BrowserExecute, CommandScope.Browser, "開く", "選択項目をMidFDの対象別動作で開きます。", true),
            Create(CommandIds.BrowserDefaultOpen, CommandScope.Browser, "既定アプリで開く", "ファイルをWindows関連付けで開き、ディレクトリをExplorerで開きます。", true),
            Create(CommandIds.BrowserOpenPlaybackLocked, CommandScope.Browser, "再生モードで開く", "画像はPlaybackLockedで開きます。動画は保存済みmodeや明示要求にかかわらずPreviewFollowで開き、Playing／Paused中は自動固定、Stopped／Ended／ErrorではPreviewFollowになります。", false),
            Create(CommandIds.BrowserDefaultOpenMarked, CommandScope.Browser, "マークしたファイルを既定アプリで開く", "マークした複数ファイルをWindowsの関連付けでまとめて開きます。", true),
            Create(CommandIds.BrowserOpenWith, CommandScope.Browser, "プログラムから開く", "Windows標準のアプリ選択画面で単一ファイルを開きます。", false),
            Create(CommandIds.BrowserSendTo, CommandScope.Browser, "送る", "選択項目をWindowsの送る先へ渡します。", false),
            Create(CommandIds.BrowserDragOut, CommandScope.Browser, "外部へドラッグ", "Browserの項目またはDrag ZIPを外部へ渡します。", false),
            Create(CommandIds.BrowserAudioCtrlEnter, CommandScope.Browser, "音声を外部再生", "選択した音声を既存の外部再生導線で開きます。", false),
            Create(CommandIds.BrowserOpenWithDiff, CommandScope.Browser, "外部Diffで比較", "選択した2ファイルを設定済みDiffツールで比較します。", false),
            Create(CommandIds.BrowserLaunchConfiguredExternalTool, CommandScope.Browser, "設定済み外部ツールを起動", "Alt割当またはツール一覧から選択した外部ツールを起動します。", false),
            Create(CommandIds.BrowserOpenCommandDialog, CommandScope.Browser, "コマンド実行", "コマンド、引数、作業フォルダを指定して実行します。", true),
            Create(CommandIds.BrowserMarkAllFiles, CommandScope.Browser, "ファイルのみ全選択", "ファイルのみを全選択/全解除します。", true),
            Create(CommandIds.BrowserMarkAllItems, CommandScope.Browser, "ディレクトリを含めて全選択", "ディレクトリを含めて全選択/全解除します。", true),
            Create(CommandIds.BrowserCursorTop, CommandScope.Browser, "先頭へ移動", "一覧の先頭へ移動します。", true),
            Create(CommandIds.BrowserCursorBottom, CommandScope.Browser, "末尾へ移動", "一覧の末尾へ移動します。", true),
            Create(CommandIds.BrowserNamePrefixJump, CommandScope.Browser, "頭文字ジャンプ", "@の後に入力した1文字で始まる項目へ移動します。複数文字検索は名前検索を使います。", false),
            Create(CommandIds.BrowserChangeAttributes, CommandScope.Browser, "属性/日時変更", "属性4種と日時3種を変更します。", true),
            Create(CommandIds.BrowserOpenExplorer, CommandScope.Browser, "Explorerで開く", "現在ディレクトリをExplorerで開きます。", true),
            Create(CommandIds.BrowserRevealInExplorer, CommandScope.Browser, "Explorerで表示", "ContextMenuの単一項目をExplorerで表示します。", false),
            Create(CommandIds.BrowserOpenShell, CommandScope.Browser, "PowerShellをここで開く", "現在ディレクトリでPowerShellを開きます。", true),
            Create(CommandIds.BrowserOpenExternalEditor, CommandScope.Browser, "外部エディタで開く", "選択ファイルを外部エディタで開きます。", true),
            Create(CommandIds.BrowserOpenCommandPrompt, CommandScope.Browser, "コマンドプロンプトをここで開く", "現在ディレクトリでコマンドプロンプトを開きます。", true),
            Create(CommandIds.BrowserCreateDirectory, CommandScope.Browser, "新規フォルダ", "現在ディレクトリに新しいフォルダを作成します。", true),
            Create(CommandIds.BrowserCreateFile, CommandScope.Browser, "新規ファイル", "現在ディレクトリに新しいファイルを作成します。", true),
            Create(CommandIds.BrowserPreview, CommandScope.Browser, "プレビュー", "選択項目をプレビュー表示します。", true),
            Create(CommandIds.BrowserSort, CommandScope.Browser, "ソート", "ソート設定を開きます。", true),
            Create(CommandIds.BrowserFilter, CommandScope.Browser, "フィルタ...", "現在の一覧の名前・詳細フィルタを開きます。", true),
            Create(CommandIds.BrowserSearch, CommandScope.Browser, "検索...", "現在のBrowserタブを起点に再帰的な名前・内容検索を開きます。", true),
            Create(CommandIds.BrowserSearchCloseResults, CommandScope.Browser, "検索結果を閉じる", "現在の再帰検索結果を閉じてBrowserへ戻ります。", false),
            Create(CommandIds.BrowserSearchExportResults, CommandScope.Browser, "検索結果を出力", "現在の再帰検索結果を一覧出力して設定済みEditorで開きます。", false),
            Create(CommandIds.BrowserSearchResultsFilter, CommandScope.Browser, "検索結果を絞り込む", "現在の再帰検索結果の表示をsession内filterで絞り込みます。", false),
            Create(CommandIds.BrowserSearchResultsOpenHitInEditor, CommandScope.Browser, "検索hitを外部エディタへ渡す", "内容検索の選択hitを開き、設定した引数テンプレートでfile・行・列を外部Editorへ渡します。位置移動の成否はEditor側に依存します。", false),
            Create(CommandIds.BrowserSearchResultsActivate, CommandScope.Browser, "検索結果を確認", "検索結果を確認用のBrowserタブで開きます。", false),
            Create(CommandIds.BrowserSearchResultsActivateInNewTab, CommandScope.Browser, "検索結果を新しいタブで開く", "検索結果を新しいBrowserタブで開きます。", false),
            Create(CommandIds.BrowserFilterClear, CommandScope.Browser, "現在のフィルタを解除", "現在タブの名前・詳細フィルタを解除して一覧を再表示します。", false),
            Create(CommandIds.BrowserTree, CommandScope.Browser, "ツリー", "ツリーダイアログを開きます。", true),
            Create(CommandIds.BrowserQuickAccess, CommandScope.Browser, "QuickAccess", "QuickAccessを開きます。", true),
            Create(CommandIds.BrowserQuickAccessActivate, CommandScope.Browser, "QuickAccess項目へ移動", "Favoritesに登録した項目へ移動します。", false),
            Create(CommandIds.BrowserQuickAccessRegister, CommandScope.Browser, "現在項目をQuickAccessへ登録", "現在地または選択項目をQuickAccessへ登録します。", false),
            Create(CommandIds.BrowserQuickAccessEntryAdd, CommandScope.Browser, "QuickAccess登録を追加", "BookmarksまたはAliasesへ登録を追加します。", false),
            Create(CommandIds.BrowserQuickAccessEntryUpdate, CommandScope.Browser, "QuickAccess登録を編集", "BookmarksまたはAliasesの登録内容を更新します。", false),
            Create(CommandIds.BrowserQuickAccessEntryDelete, CommandScope.Browser, "QuickAccess登録を削除", "BookmarksまたはAliasesの登録を削除します。", false),
            Create(CommandIds.BrowserQuickAccessReorder, CommandScope.Browser, "QuickAccess登録順を変更", "登録先の項目またはカテゴリの順序を変更します。", false),
            Create(CommandIds.BrowserLogdisk, CommandScope.Browser, "Logdisk", "Logdiskを実行します。", true),
            Create(CommandIds.ArchivePack, CommandScope.Browser, "圧縮", "選択項目を圧縮します。", true),
            Create(CommandIds.ArchiveUnpack, CommandScope.Browser, "解凍", "選択ファイルを解凍します。", true),
            Create(CommandIds.ArchiveHash, CommandScope.Browser, "CRC/SHA計算", "選択項目のCRC/SHAを計算します。", false),
            Create(CommandIds.ArchiveHashResultCopy, CommandScope.Global, "CRC/SHA結果をコピー", "結果Dialogの選択hash、CSVまたは詳細ログをコピーします。", false),
            Create(CommandIds.ArchiveContentsOpen, CommandScope.Browser, "archive一覧", "指定されたarchiveの内容を一覧表示します。", false),
            Create(CommandIds.ArchiveContentsNavigate, CommandScope.Browser, "archive内を移動", "archive一覧内のディレクトリへ移動するか親へ戻ります。", false),
            Create(CommandIds.ArchiveContentsPreviewText, CommandScope.ArchiveContents, "archive内テキストをプレビュー", "archive一覧で選択したテキストファイルをプレビューします。", false),
            Create(CommandIds.ArchiveOpenInSevenZipFileManager, CommandScope.Browser, "7-Zip File Managerで開く", "指定されたarchiveを7-Zip File Managerで開きます。", false),
            Create(CommandIds.BrowserCopyFullPath, CommandScope.Browser, "フルパスコピー", "選択またはマーク項目のフルパスをコピーします。", true),
            Create(CommandIds.BrowserCopyCurrentPath, CommandScope.Browser, "現在のパスをコピー", "現在ディレクトリの完全パスをコピーします。", true),
            Create(CommandIds.BrowserCopyItemName, CommandScope.Browser, "現在項目名をコピー", "カーソル位置の項目名をコピーします。", false),
            Create(CommandIds.BrowserClipboardCopy, CommandScope.Browser, "OSファイルクリップボードへコピー", "選択またはマーク項目をOS file clipboardへコピーします。", false),
            Create(CommandIds.BrowserPathEntryOpen, CommandScope.Browser, "パス入力", "現在パスの入力欄を開きます。", true),
            Create(CommandIds.BrowserViewDisplayModeSet, CommandScope.Browser, "一覧表示モードを変更", "Browser一覧の表示モードを変更します。", false),
            Create(CommandIds.BrowserShowHelp, CommandScope.Browser, "ヘルプ", "ヘルプ表示を開きます。", true),
            Create(CommandIds.BrowserOpenMarkSlot, CommandScope.Browser, "マークスロット", "マークスロット画面を開きます。", true),
            Create(CommandIds.BrowserMarkSlotRestore, CommandScope.Browser, "マークスロットを復元", "指定されたマークスロットを現在タブへ適用します。", false),
            Create(CommandIds.BrowserMarkSlotSave, CommandScope.Browser, "マークスロットへ保存", "現在タブ・カテゴリ・Workspaceのマークを指定スロットへ保存します。", false),
            Create(CommandIds.BrowserMarkSlotRename, CommandScope.Browser, "マークスロット名を変更", "指定スロットの表示名を変更します。", false),
            Create(CommandIds.BrowserMarkSlotDelete, CommandScope.Browser, "マークスロットを削除", "指定スロットの保存内容を削除します。", false),
            Create(CommandIds.BrowserMarkSlotRemoveItems, CommandScope.Browser, "マークスロット項目を削除", "指定した項目をマークスロットから削除します。", false),
            Create(CommandIds.BrowserMarkSlotExport, CommandScope.Browser, "マークスロットをエクスポート", "指定スロットをJSONファイルへ出力します。", false),
            Create(CommandIds.BrowserMarkSlotImport, CommandScope.Browser, "マークスロットをインポート", "JSONファイルの内容を指定スロットへ読み込みます。", false),
            Create(CommandIds.BrowserMarkSlotsExportAll, CommandScope.Browser, "全マークスロットをエクスポート", "全マークスロットをJSONバックアップへ出力します。", false),
            Create(CommandIds.BrowserMarkSlotsImportAll, CommandScope.Browser, "全マークスロットをインポート", "JSONバックアップで全マークスロットを置き換えます。", false),
            Create(CommandIds.BrowserMarkSlotClipboardImport, CommandScope.Browser, "Clipboardの変更ファイルを現在Markへ取り込む", "Clipboard内のKDSL_RESULT変更fileを現在タブのMarkへ反映します。", false),
            Create(CommandIds.BrowserMarkSlotSetOperationPreview, CommandScope.Browser, "マークスロット集合演算をプレビュー", "2つのスロットに対する集合演算結果を表示します。", false),
            Create(CommandIds.BrowserMarkSlotSetOperationApply, CommandScope.Browser, "集合演算結果を現在タブへ適用", "集合演算結果で現在タブのMarkを置き換えます。", false),
            Create(CommandIds.BrowserMarkSlotSetOperationSave, CommandScope.Browser, "集合演算結果をマークスロットへ保存", "集合演算結果を指定スロットへ保存します。", false),
            Create(CommandIds.BrowserMarkClear, CommandScope.Browser, "マークを解除", "現在タブ・カテゴリ・Workspaceの指定範囲からマークを解除します。", false),
            Create(CommandIds.BrowserTabNew, CommandScope.Browser, "新しいタブを作る", "新しいBrowserタブを作成します。", true),
            Create(CommandIds.BrowserOpenInNewTab, CommandScope.Browser, "新しいタブで開く", "選択したディレクトリを新しいBrowserタブで開きます。", false),
            Create(CommandIds.BrowserTabNext, CommandScope.Browser, "次のタブへ移動", "次のBrowserタブへ移動します。", true),
            Create(CommandIds.BrowserTabPrevious, CommandScope.Browser, "前のタブへ移動", "前のBrowserタブへ移動します。", true),
            Create(CommandIds.BrowserTabActivate, CommandScope.Browser, "指定タブへ移動", "指定されたBrowserタブへ移動します。", false),
            Create(CommandIds.BrowserTabReorder, CommandScope.Browser, "タブを並べ替え", "Browserタブを指定位置へ移動します。", false),
            Create(CommandIds.BrowserTabTreeDrop, CommandScope.Browser, "タブナビゲーションのドロップを適用", "Browserタブナビゲーションの所属・順序変更を一つの操作として適用します。", false),
            Create(CommandIds.BrowserTabHistoryBack, CommandScope.Browser, "タブ履歴: 戻る", "以前表示していたBrowserタブへ戻ります。", true),
            Create(CommandIds.BrowserTabHistoryForward, CommandScope.Browser, "タブ履歴: 進む", "戻ったBrowserタブ履歴を進みます。", true),
            Create(CommandIds.BrowserTabHistoryShow, CommandScope.Browser, "タブ移動履歴...", "Browserタブの戻る／進む履歴を一覧表示します。", true),
            Create(CommandIds.BrowserTabLayoutToggle, CommandScope.Browser, "タブ表示位置を切り替える", "タブ表示を横型と縦側の間で切り替えます。", true),
            Create(CommandIds.BrowserTabLayoutSet, CommandScope.Browser, "タブ表示位置を指定", "Browserタブの表示位置を横型または縦型に設定します。", false),
            Create(CommandIds.BrowserTabNavigationWidthSet, CommandScope.Browser, "タブナビゲーション幅を変更", "ドラッグで指定されたBrowserタブナビゲーション領域の幅を保存します。", false),
            Create(CommandIds.BrowserTabReadOnlyToggle, CommandScope.Browser, "現在タブのReadOnly切替", "現在のBrowserタブをReadOnlyと通常の間で切り替えます。", true),
            Create(CommandIds.BrowserTabGroupCreate, CommandScope.Browser, "タブグループを作成", "現在または指定したBrowserタブを新しいグループに入れます。", true),
            Create(CommandIds.BrowserTabGroupAdd, CommandScope.Browser, "タブをグループへ移動", "現在または指定したBrowserタブを同じカテゴリのタブグループへ移動します。", true),
            Create(CommandIds.BrowserTabGroupRemove, CommandScope.Browser, "タブをグループから外す", "現在または指定したBrowserタブをグループから外します。", true),
            Create(CommandIds.BrowserTabGroupRename, CommandScope.Browser, "タブグループ名を変更", "タブグループの表示名を変更します。", true),
            Create(CommandIds.BrowserTabGroupExpandedSet, CommandScope.Browser, "タブグループの展開状態を設定", "指定したBrowserタブグループの展開状態を保存します。", false),
            Create(CommandIds.BrowserTabCategoryAdd, CommandScope.Browser, "カテゴリ追加", "Browserタブカテゴリを追加します。", true),
            Create(CommandIds.BrowserTabCategoryRename, CommandScope.Browser, "カテゴリ名変更", "現在のBrowserタブカテゴリ名を変更します。", true),
            Create(CommandIds.BrowserTabCategoryDelete, CommandScope.Browser, "カテゴリ削除", "現在のBrowserタブカテゴリを削除します。", true, true),
            Create(CommandIds.BrowserTabCategoryMoveLeft, CommandScope.Browser, "カテゴリを左へ移動", "現在のBrowserタブカテゴリを左へ移動します。", true),
            Create(CommandIds.BrowserTabCategoryMoveRight, CommandScope.Browser, "カテゴリを右へ移動", "現在のBrowserタブカテゴリを右へ移動します。", true),
            Create(CommandIds.BrowserTabCategoryNext, CommandScope.Browser, "次のカテゴリへ移動", "次のBrowserタブカテゴリへ移動します。", true),
            Create(CommandIds.BrowserTabCategoryPrevious, CommandScope.Browser, "前のカテゴリへ移動", "前のBrowserタブカテゴリへ移動します。", true),
            Create(CommandIds.BrowserTabCategoryActivate, CommandScope.Browser, "指定カテゴリへ移動", "指定されたBrowserタブカテゴリへ移動します。", false),
            Create(CommandIds.BrowserTabCategoryReorder, CommandScope.Browser, "カテゴリを並べ替え", "Browserタブカテゴリを指定位置へ移動します。", false),
            Create(CommandIds.BrowserTabCategoryManage, CommandScope.Browser, "カテゴリ管理", "Browserタブカテゴリの管理画面を開きます。", true),
            Create(CommandIds.BrowserTabClose, CommandScope.Browser, "現在タブを閉じる", "現在のBrowserタブを閉じます。", true),
            Create(CommandIds.BrowserTabRestoreClosed, CommandScope.Browser, "閉じたタブを復元", "直前に閉じたBrowserタブを復元します。", true),
            Create(CommandIds.ClipboardPaste, CommandScope.Browser, "貼り付け", "クリップボード内容を貼り付けます。", true),
            Create(CommandIds.ClipboardCut, CommandScope.Browser, "切り取り", "選択項目をクリップボードへ切り取ります。", true),
            Create(CommandIds.BrowserProperties, CommandScope.Browser, "プロパティ", "選択中の1項目のWindowsプロパティを表示します。", true),
            Create(CommandIds.FileCopy, CommandScope.Browser, "コピー", "選択項目をコピーします。", true, false),
            Create(CommandIds.FileMove, CommandScope.Browser, "移動", "選択項目を移動します。", true, false),
            Create(CommandIds.FileRename, CommandScope.Browser, "名前変更", "選択項目を名前変更します。", true, false),
            Create(CommandIds.FileDelete, CommandScope.Browser, "削除", "選択項目を削除します。", true, true),
            CreateKeyboardOnly(CommandIds.FileDeletePermanent, "完全削除", "選択項目をゴミ箱を経由せず完全に削除します。", isDangerous: true),
            Create(CommandIds.EditUndo, CommandScope.Browser, "元に戻す", "直前の対象操作を元に戻します。", true, false, false),
            Create(CommandIds.EditRedo, CommandScope.Browser, "やり直し", "元に戻した操作をやり直します。", true, false, false),
            Create(CommandIds.AppOpenSystemInformation, CommandScope.Browser, "情報", "ドライブ、メモリ、システム情報を表示します。", true),
            Create(CommandIds.AppOpenNewInstance, CommandScope.Global, "MidFDをもう1枚立ち上げ", "現在パスで新しいMidFDウィンドウを起動します。", true),
            Create(CommandIds.AppOpenControlPanel, CommandScope.Global, "コントロールパネルを開く", "Windowsのコントロールパネルを開きます。", true),
            Create(CommandIds.AppOpenExternalUrl, CommandScope.Global, "外部URLを開く", "検証済みのHTTP(S) URLを標準ブラウザで開きます。", false),
            Create(CommandIds.AppCommandPaletteFavoriteToggle, CommandScope.Global, "コマンドのお気に入りを切り替え", "Command Paletteで選択したコマンドのお気に入り登録を切り替えます。", false),
            Create(CommandIds.AppExternalToolDefinitionsSave, CommandScope.Global, "外部ツール定義を保存", "外部ツール管理画面で編集した定義一式を保存します。", false),
            Create(CommandIds.AppOpenSettings, CommandScope.Global, "設定を開く", "設定ダイアログを開きます。", true),
            Create(CommandIds.AppSettingsSave, CommandScope.Global, "設定を保存", "設定画面で編集した設定を保存します。", false),
            Create(CommandIds.AppHideBrowserToolbar, CommandScope.Global, "Browser操作ボタンを非表示", "戻る・進む・上へ・更新ボタンを非表示にします。", false),
            Create(CommandIds.FileOperationCancel, CommandScope.Global, "実行中のファイル操作を中断", "実行中のファイル操作へ中断を要求します。", false),
            Create(CommandIds.AppSettingsImport, CommandScope.Global, "設定をインポート", "設定ファイルを読み込み、確認後に現在の設定へ適用します。", false),
            Create(CommandIds.AppSettingsExport, CommandScope.Global, "設定をエクスポート", "現在の設定をファイルへ出力します。", false),
            Create(CommandIds.AppExit, CommandScope.Global, "終了", "MidFDを終了します。", false),
            Create(CommandIds.AppOpenCommandLauncher, CommandScope.Global, "コマンドランチャーを開く", "コマンドランチャーを開きます。", true),
            Create(CommandIds.BrowserTabLock, CommandScope.Browser, "現在タブの固定/解除", "現在のタブの固定状態を切り替えます。", true),
            Create(CommandIds.BrowserTabCloseRight, CommandScope.Browser, "右側のタブを閉じる", "指定したBrowserタブより右側のタブを閉じます。", false),
            Create(CommandIds.BrowserTabCloseLeft, CommandScope.Browser, "左側のタブを閉じる", "指定したBrowserタブより左側のタブを閉じます。", false),
            Create(CommandIds.BrowserTabCloseOther, CommandScope.Browser, "他のタブを閉じる", "指定したBrowserタブ以外のタブを閉じます。", false),
            Create(CommandIds.AppOpenCommandList, CommandScope.Global, "コマンド一覧", "コマンド一覧を開きます。", true),
            Create(CommandIds.AppEmptyManagedTrash, CommandScope.Global, "MidFD管理ゴミ箱を空にする", "MidFD管理ゴミ箱を空にし、削除Undo/Redoを消去します。", false),
            Create(CommandIds.AppRestoreManagedTrash, CommandScope.Global, "MidFD管理ゴミ箱から復元", "選択したMidFD管理ゴミ箱項目を元の場所へ復元します。", false),
            Create(CommandIds.AppDeleteManagedTrashForever, CommandScope.Global, "MidFD管理ゴミ箱から完全削除", "選択したMidFD管理ゴミ箱項目を物理削除します。", false),
            Create(CommandIds.AppCleanMissingManagedTrash, CommandScope.Global, "MidFD管理ゴミ箱の欠損記録を掃除", "欠損項目の記録を整理します。", false),
            Create(CommandIds.AppOpenWorkspaceSnapshots, CommandScope.Browser, "Workspaceスナップショット管理", "Workspaceスナップショットを管理します。", false),
            Create(CommandIds.WorkspaceSnapshotSave, CommandScope.Browser, "Workspaceをスナップショット保存", "現在のWorkspace状態をスナップショットとして保存します。", false),
            Create(CommandIds.WorkspaceSnapshotRestore, CommandScope.Browser, "Workspaceスナップショットを復元", "選択したWorkspaceスナップショットを復元します。", false),
            Create(CommandIds.WorkspaceSnapshotRename, CommandScope.Browser, "Workspaceスナップショット名を変更", "選択したWorkspaceスナップショットの名前を変更します。", false),
            Create(CommandIds.WorkspaceSnapshotDelete, CommandScope.Browser, "Workspaceスナップショットを削除", "選択したWorkspaceスナップショットを削除します。", false),
            Create(CommandIds.WorkspaceSnapshotExport, CommandScope.Browser, "Workspaceスナップショットをエクスポート", "選択したWorkspaceスナップショットをエクスポートします。", false),
            Create(CommandIds.WorkspaceSnapshotImport, CommandScope.Browser, "Workspaceスナップショットをインポート", "Workspaceスナップショットをインポートします。", false),
            Create(CommandIds.WorkspaceSnapshotExportAll, CommandScope.Browser, "Workspaceスナップショットを一括エクスポート", "全Workspaceスナップショットをエクスポートします。", false),
            Create(CommandIds.WorkspaceSnapshotImportAll, CommandScope.Browser, "Workspaceスナップショットを一括インポート", "Workspaceスナップショットを一括インポートします。", false),
            Create(CommandIds.BrowserCursorMove, CommandScope.Browser, "Browserカーソル移動", "指定された一覧項目へカーソルを移動します。", false),
            Create(CommandIds.BrowserMarkToggle, CommandScope.Browser, "Browser項目のマーク切替", "現在項目のマークを切り替えます。", false),
            Create(CommandIds.BrowserMarkRangeAdd, CommandScope.Browser, "範囲をマーク", "入力面から指定されたBrowser項目を追加でマークします。", false),
            Create(CommandIds.BrowserMarkInvert, CommandScope.Browser, "Browserマーク反転", "指定範囲のマーク状態を反転します。", false),
            Create(CommandIds.FileImportExternalDrop, CommandScope.Browser, "ドロップ内容を取り込む", "Browserへ外部ドロップされたファイル内容を取り込みます。", false),
            Create(CommandIds.TextViewerReturnToBrowser, CommandScope.TextViewer, "Viewerを閉じてBrowserへ戻る", "現在のViewerを終了してBrowserへ戻ります。", false),
            Create(CommandIds.TextViewerEncodingCycle, CommandScope.TextViewer, "Viewer文字コード切替", "文字コードをAuto、UTF-8、Shift-JISの順に切り替えます。", false),
            Create(CommandIds.TextViewerWordWrapToggle, CommandScope.TextViewer, "Viewer折り返し切替", "Viewerの折り返し表示を切り替えます。", false),
            Create(CommandIds.TextViewerSearch, CommandScope.TextViewer, "Viewer内検索", "Viewer内の文字列を検索します。", false),
            Create(CommandIds.TextViewerSearchNext, CommandScope.TextViewer, "Viewer内を次検索", "現在の検索語を次方向へ検索します。", false),
            Create(CommandIds.TextViewerSearchPrevious, CommandScope.TextViewer, "Viewer内を前検索", "現在の検索語を前方向へ検索します。", false),
            Create(CommandIds.TextViewerCopy, CommandScope.TextViewer, "Viewer表示内容をコピー", "Viewerの選択範囲または表示中の内容をコピーします。", false),
            Create(CommandIds.TextViewerOpenExternalEditor, CommandScope.TextViewer, "Viewerのsourceを外部エディタで開く", "現在表示中のテキストsource fileを外部Editorで開きます。", false),
            Create(CommandIds.TextViewerNavigate, CommandScope.TextViewer, "Viewer文書内移動", "指定された文書内位置へ移動します。", false),
            Create(CommandIds.TextViewerMarkdownModeSet, CommandScope.TextViewer, "Markdown表示方式変更", "MarkdownをRenderedまたはRawで表示します。", false),
            Create(CommandIds.ImageViewerOpen, CommandScope.ImageViewer, "メディアビューア: 開く", "画像または動画をビューアで開きます。", false),
            Create(CommandIds.ImageViewerTogglePresentationMode, CommandScope.ImageViewer, "ビューア: 選択追従/再生モード切替", "画像ViewerのBrowser選択への追従と表示媒体の固定を切り替えます。動画はPlaying／Paused中に自動固定し、Stopped／Ended／ErrorではPreviewFollowになります。動画の手動切替は次の再生状態遷移まで維持します。", false),
            Create(CommandIds.ImageViewerSaveAs, CommandScope.ImageViewer, "画像ビューア: 名前をつけて保存", "表示中の画像を指定形式で保存します。", false),
            Create(CommandIds.ImageViewerClose, CommandScope.ImageViewer, "画像ビューア: 閉じる", "画像ビューアを閉じます。", false),
            Create(CommandIds.ImageViewerCopy, CommandScope.ImageViewer, "画像ビューア: コピー", "表示中または選択中の画像をコピーします。", false),
            Create(CommandIds.ImageViewerPaste, CommandScope.ImageViewer, "画像ビューア: 貼り付け", "クリップボード画像をビューアへ貼り付けます。", false),
            Create(CommandIds.ImageViewerPasteSelectionStretch, CommandScope.ImageViewer, "画像ビューア: 選択範囲へ縦横比を維持せず貼り付け", "クリップボード画像を選択範囲全体へ縦横比を維持せず貼り付けます。", false),
            Create(CommandIds.ImageViewerPasteSelectionOriginal, CommandScope.ImageViewer, "画像ビューア: 選択範囲へ元サイズで貼り付け", "クリップボード画像を元ピクセルサイズのまま選択範囲の左上へ1:1配置し、範囲を超える部分をクリップします。余白と範囲外の画素は変更しません。", false),
            Create(CommandIds.ImageViewerCopySvg, CommandScope.ImageViewer, "画像ビューア: SVGコピー", "SVGをクリップボードへコピーします。", false),
            Create(CommandIds.ImageViewerQuantize, CommandScope.ImageViewer, "画像ビューア: 減色", "表示画像へ減色を適用します。", false),
            Create(CommandIds.ImageViewerReset, CommandScope.ImageViewer, "画像ビューア: 元画像へ戻す", "画像変換を元画像へ戻します。", false),
            Create(CommandIds.ImageViewerRotateRight, CommandScope.ImageViewer, "画像ビューア: 右90度回転", "表示画像を右へ90度回転します。", false),
            Create(CommandIds.ImageViewerRotateLeft, CommandScope.ImageViewer, "画像ビューア: 左90度回転", "表示画像を左へ90度回転します。", false),
            Create(CommandIds.ImageViewerFlipHorizontal, CommandScope.ImageViewer, "画像ビューア: 左右反転", "表示画像を左右反転します。", false),
            Create(CommandIds.ImageViewerFlipVertical, CommandScope.ImageViewer, "画像ビューア: 上下反転", "表示画像を上下反転します。", false),
            Create(CommandIds.ImageViewerShowInfo, CommandScope.ImageViewer, "画像ビューア: 画像情報", "画像情報を表示します。", false),
            Create(CommandIds.ImageViewerUndo, CommandScope.ImageViewer, "画像ビューア: 元に戻す", "直前のビューア内画像操作を元に戻します。", false),
            Create(CommandIds.ImageViewerRedo, CommandScope.ImageViewer, "画像ビューア: やり直す", "元に戻したビューア内画像操作を再適用します。", false),
            Create(CommandIds.ImageViewerZoomIn, CommandScope.ImageViewer, "画像ビューア: 拡大", "表示画像を拡大します。", false),
            Create(CommandIds.ImageViewerZoomOut, CommandScope.ImageViewer, "画像ビューア: 縮小", "表示画像を縮小します。", false),
            Create(CommandIds.ImageViewerZoomReset, CommandScope.ImageViewer, "画像ビューア: 倍率を100%へ戻す", "表示倍率を100%へ戻します。", false),
            Create(CommandIds.ImageViewerFullscreenToggle, CommandScope.ImageViewer, "画像ビューア: 全画面切替", "全画面表示を切り替えます。", false),
            Create(CommandIds.ImageViewerFullscreenExit, CommandScope.ImageViewer, "画像ビューア: 全画面解除", "全画面表示を解除します。", false),
            Create(CommandIds.ImageViewerVideoSeekToStart, CommandScope.ImageViewer, "動画プレビュー: 先頭へ移動", "動画プレビュー位置を先頭へ移動します。", false),
            Create(CommandIds.ImageViewerVideoSeekBackward, CommandScope.ImageViewer, "動画プレビュー: 前へ移動", "動画プレビュー位置を前へ移動します。", false),
            Create(CommandIds.ImageViewerVideoSeekForward, CommandScope.ImageViewer, "動画プレビュー: 次へ移動", "動画プレビュー位置を次へ移動します。", false),
            Create(CommandIds.ImageViewerVideoSeekToPosition, CommandScope.ImageViewer, "動画プレビュー: 指定位置へ移動", "指定された動画プレビュー位置へ移動します。", false),
            Create(CommandIds.ImageViewerVideoExternalPlay, CommandScope.ImageViewer, "動画ビューア: 再生/一時停止", "表示中の動画を内蔵プレイヤーで再生または一時停止します。", false),
            Create(CommandIds.ImageViewerVideoSaveFrame, CommandScope.ImageViewer, "動画ビューア: 現在フレームをPNG保存", "再生中または一時停止中の動画フレームをPNG画像として保存します。", false),
            Create(CommandIds.ImageViewerVideoSetPlaybackRate, CommandScope.ImageViewer, "動画ビューア: 再生速度を変更", "動画ビューアの再生速度を変更します。", false),
            Create(CommandIds.ImageViewerResize, CommandScope.ImageViewer, "画像ビューア: リサイズ", "表示画像を指定寸法へリサイズします。", false),
            Create(CommandIds.ImageViewerMosaic, CommandScope.ImageViewer, "画像ビューア: 矩形モザイク", "選択範囲へモザイクを適用します。", false),
            Create(CommandIds.ImageViewerBlur, CommandScope.ImageViewer, "画像ビューア: 矩形ぼかし", "選択範囲へぼかしを適用します。", false),
            Create(CommandIds.ImageViewerTone, CommandScope.ImageViewer, "画像ビューア: 色調補正", "明るさ・コントラスト・彩度・ガンマを調整します。", false),
            Create(CommandIds.AppOpenManagedTrash, CommandScope.Global, "MidFD管理ゴミ箱を開く", "MidFD管理ゴミ箱の確認・管理画面を開きます。", true)
        };

        for (int drive = 1; drive <= 26; drive++)
        {
            char letter = (char)('A' + drive - 1);
            definitions.Insert(definitions.Count - 2, CreateKeyboardOnly(
                CommandIds.GetBrowserDriveCommandId(drive),
                $"Drive {letter}へ移動",
                $"Drive {letter}: の最終訪問directory、またはdrive rootへ移動します。"));
        }

        for (int columnCount = 1; columnCount <= 9; columnCount++)
        {
            definitions.Insert(definitions.Count - 2, CreateKeyboardOnly(
                CommandIds.GetBrowserColumnCountCommandId(columnCount),
                $"表示列数を{columnCount}列に変更",
                $"Browser一覧の表示列数を{columnCount}列に変更します。"));
        }

        _definitions = new ReadOnlyCollection<CommandDefinition>(definitions);
        _map = definitions.ToDictionary(static d => d.Id, StringComparer.Ordinal);
    }

    public IReadOnlyList<CommandDefinition> GetAll() => _definitions;

    public IReadOnlyList<CommandDefinition> GetMouseGestureAssignableCommands()
    {
        return _definitions
            .Where(static d =>
                (d.Scope == CommandScope.Browser || d.Scope == CommandScope.Global) &&
                (d.InputSurfaces & CommandInputSurface.MouseGesture) != 0)
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
        bool isDangerous = false,
        bool allowMouseGesture = true)
    {
        return new CommandDefinition
        {
            Id = id,
            Scope = scope,
            DisplayName = displayName,
            Description = description,
            Category = ResolveCategory(id),
            IsCustomizable = isCustomizable,
            IsDangerous = isDangerous,
            InputSurfaces = isCustomizable
                ? CommandInputSurface.Keyboard |
                  CommandInputSurface.FunctionBar |
                  (!isDangerous && allowMouseGesture ? CommandInputSurface.MouseGesture : CommandInputSurface.None)
                : CommandInputSurface.None
        };
    }

    private static CommandDefinition CreateKeyboardOnly(
        string id,
        string displayName,
        string description,
        bool isDangerous = false)
    {
        return new CommandDefinition
        {
            Id = id,
            Scope = CommandScope.Browser,
            DisplayName = displayName,
            Description = description,
            Category = ResolveCategory(id),
            IsCustomizable = true,
            IsDangerous = isDangerous,
            InputSurfaces = CommandInputSurface.Keyboard
        };
    }

    private static string ResolveCategory(string commandId)
    {
        if (commandId.StartsWith("browser.navigate.", StringComparison.Ordinal) ||
            commandId.StartsWith("browser.path_entry.", StringComparison.Ordinal) ||
            commandId.StartsWith("browser.drive.", StringComparison.Ordinal))
        {
            return "ナビゲーション";
        }
        if (commandId.StartsWith("browser.mark.", StringComparison.Ordinal) ||
            commandId.StartsWith("browser.mark_slot.", StringComparison.Ordinal) ||
            commandId.StartsWith("browser.mark_slots.", StringComparison.Ordinal) ||
            commandId.StartsWith("browser.cursor.", StringComparison.Ordinal))
        {
            return "選択・マーク";
        }
        if (commandId.StartsWith("browser.view.", StringComparison.Ordinal) ||
            commandId.StartsWith("browser.tab.", StringComparison.Ordinal) ||
            commandId.StartsWith("browser.tab_category.", StringComparison.Ordinal) ||
            commandId.StartsWith("browser.tab_group.", StringComparison.Ordinal))
        {
            return "表示・タブ";
        }
        if (commandId.StartsWith("file.", StringComparison.Ordinal) ||
            commandId.StartsWith("browser.create_", StringComparison.Ordinal) ||
            commandId.StartsWith("browser.change.", StringComparison.Ordinal))
        {
            return "ファイル操作";
        }
        if (commandId.StartsWith("archive.", StringComparison.Ordinal))
        {
            return "アーカイブ";
        }
        if (commandId.StartsWith("workspace_snapshot.", StringComparison.Ordinal))
        {
            return "Workspace";
        }
        if (commandId.StartsWith("image_viewer.", StringComparison.Ordinal))
        {
            return "画像ビューア";
        }
        if (commandId.StartsWith("text_viewer.", StringComparison.Ordinal))
        {
            return "テキストビューア";
        }
        if (commandId.StartsWith("clipboard.", StringComparison.Ordinal) ||
            commandId.StartsWith("browser.clipboard.", StringComparison.Ordinal) ||
            commandId.StartsWith("browser.copy.", StringComparison.Ordinal))
        {
            return "クリップボード";
        }
        if (commandId.StartsWith("app.", StringComparison.Ordinal) ||
            commandId.StartsWith("edit.", StringComparison.Ordinal))
        {
            return "アプリ・編集";
        }
        if (commandId.StartsWith("browser.search", StringComparison.Ordinal) ||
            commandId.StartsWith("browser.filter", StringComparison.Ordinal))
        {
            return "検索・フィルタ";
        }

        return "Browser操作";
    }
}
