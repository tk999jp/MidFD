using System.Drawing.Text;
using MidFD.Configuration;
using MidFD.Services;
using MidFD.Dialogs;
using MidFD.Models;

namespace MidFD;

/// <summary>
/// 設定フォーム。既存設定を壊さず、OK 押下時のみ AppSettings を保存する。
/// </summary>
public class SettingsForm : Form
{
    private sealed record FeatureProfileOption(string DisplayName, string SettingValue);
    private static readonly FeatureProfileOption[] FeatureProfileOptions =
    {
        new("実用安定版（推奨）", FeatureProfile.PracticalStable.ToString()),
        new("高度機能α版", FeatureProfile.Full.ToString())
    };

    private readonly AppSettings _settings;

    private readonly TextBox _sevenZipPathBox;
    private readonly TextBox _diffPathBox;
    private readonly TextBox _editorPathBox;
    private readonly Label _sevenZipStatusLabel;
    private readonly Label _diffStatusLabel;
    private readonly Label _editorStatusLabel;
    private readonly ComboBox _filerFontCombo;
    private readonly NumericUpDown _filerFontSizeBox;
    private readonly ComboBox _viewerFontCombo;
    private readonly NumericUpDown _viewerFontSizeBox;
    private readonly ComboBox _colorThemeCombo;
    private readonly CheckBox _showExtensionsCheckBox;
    private readonly CheckBox _showBrowserTabCategoryRowCheckBox;
    private readonly CheckBox _showDirectoryMarkerCheckBox;
    private readonly CheckBox _showHiddenFilesCheckBox;
    private readonly CheckBox _showItemIconsCheckBox;
    private readonly ComboBox _dateFormatCombo;
    private readonly ComboBox _sizeFormatCombo;
    private readonly CheckBox _viewerWordWrapCheckBox;
    private readonly CheckBox _reuseImageViewerCheckBox;
    private readonly CheckBox _closeImageViewerOnNonImageCheckBox;
    private readonly CheckBox _rememberImageViewerBoundsCheckBox;
    private readonly CheckBox _videoStillPreviewEnabledCheckBox;
    private readonly CheckBox _videoEnterPlaysExternalCheckBox;
    private readonly ComboBox _videoSkipSecondsCombo;
    private readonly ComboBox _videoPlaybackVolumeCombo;
    private readonly TextBox _videoStillPreviewFfmpegPathBox;
    private readonly Label _videoStillPreviewFfmpegStatusLabel;
    private readonly CheckBox _confirmDeleteCheckBox;
    private readonly CheckBox _confirmPermanentDeleteCheckBox;
    private readonly CheckBox _useMidFdManagedTrashCheckBox;
    private readonly CheckBox _reloadAfterFileOperationCheckBox;
    private readonly CheckBox _selectCreatedItemCheckBox;
    private readonly CheckBox _restoreLastPathCheckBox;
    private readonly CheckBox _restoreTabsOnStartupCheckBox;
    private readonly CheckBox _restoreWindowBoundsCheckBox;
    private readonly CheckBox _restoreColumnCountCheckBox;
    private readonly CheckBox _restoreSortCheckBox;
    private readonly ComboBox _featureProfileCombo;
    private readonly ComboBox _functionKeyProfileCombo;
    private readonly ComboBox _commandLauncherShortcutCombo;
    private readonly CheckBox _enableMouseGesturesCheckBox;
    private readonly CheckBox _enableLogCheckBox;
    private readonly CheckBox _enableDetailedLogCheckBox;
    private readonly ToolTip _statusToolTip = new();

    public SettingsForm(AppSettings settings, FeatureProfile effectiveProfile)
    {
        _settings = settings.Clone();
        _settings.Profile = FeatureProfileService.ToSettingValue(effectiveProfile);
        _settings.Appearance ??= new AppearanceSettings();
        _settings.Logging ??= new LoggingSettings();
        _settings.Preview ??= new PreviewSettings();
        _settings.Session ??= new SessionSettings();
        _settings.Input ??= new InputSettings();
        _settings.SevenZip ??= new SevenZipSettings();
        _settings.ExternalTools ??= new ExternalToolsSettings();
        _settings.FileOperations ??= new FileOperationsSettings();
        _settings.Fonts ??= new FontSettings();

        Text = "設定";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Padding = new Padding(12);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(800, 520);

        var tabs = new TabControl
        {
            Dock = DockStyle.Top,
            Height = 440
        };

        var tabDisplayAndPreview = CreateTab("表示 / ビューア");
        var tabOpAndInput = CreateTab("操作 / 入力");
        var tabLaunchAndRestore = CreateTab("起動 / 復元");
        var tabExternal = CreateTab("外部連携");
        var tabLog = CreateTab("ログ / 詳細");

        tabs.TabPages.AddRange(new[]
        {
            tabDisplayAndPreview,
            tabOpAndInput,
            tabLaunchAndRestore,
            tabExternal,
            tabLog
        });

        Controls.Add(tabs);

        string[] fonts = GetInstalledFontNames();
        string[] colorThemes = { "ClassicCyan", "Green", "Amber", "Light" };
        string[] dateFormats = { "yyyy-MM-dd HH:mm", "yyyy/MM/dd HH:mm:ss", "yyyy-MM-dd(ddd) HH:mm" };
        string[] sizeFormats = { "HumanReadable", "Bytes", "KB/MB" };

        (_filerFontCombo, _filerFontSizeBox, _colorThemeCombo, _showBrowserTabCategoryRowCheckBox, _showExtensionsCheckBox, _showDirectoryMarkerCheckBox, _showHiddenFilesCheckBox, _showItemIconsCheckBox, _dateFormatCombo, _sizeFormatCombo,
         _viewerFontCombo, _viewerFontSizeBox, _viewerWordWrapCheckBox, _reuseImageViewerCheckBox, _closeImageViewerOnNonImageCheckBox, _rememberImageViewerBoundsCheckBox, _videoStillPreviewEnabledCheckBox, _videoSkipSecondsCombo)
            = BuildDisplayAndPreviewTab(tabDisplayAndPreview, fonts, colorThemes, dateFormats, sizeFormats);

        (_confirmDeleteCheckBox, _confirmPermanentDeleteCheckBox, _useMidFdManagedTrashCheckBox, _reloadAfterFileOperationCheckBox, _selectCreatedItemCheckBox,
         _functionKeyProfileCombo, _commandLauncherShortcutCombo, _enableMouseGesturesCheckBox)
            = BuildOperationAndInputTab(tabOpAndInput);

        (_featureProfileCombo, _restoreLastPathCheckBox, _restoreTabsOnStartupCheckBox, _restoreWindowBoundsCheckBox, _restoreColumnCountCheckBox, _restoreSortCheckBox)
            = BuildLaunchAndRestoreTab(tabLaunchAndRestore);

        (_sevenZipPathBox, _diffPathBox, _editorPathBox, _videoPlaybackVolumeCombo, _videoStillPreviewFfmpegPathBox, _videoEnterPlaysExternalCheckBox, _sevenZipStatusLabel, _diffStatusLabel, _editorStatusLabel, _videoStillPreviewFfmpegStatusLabel)
            = BuildExternalTab(tabExternal);

        (_enableLogCheckBox, _enableDetailedLogCheckBox) = BuildLogTab(tabLog);

        _videoStillPreviewFfmpegPathBox.TextChanged += (_, _) => RefreshExternalStatus();
        RefreshExternalStatus();

        var btnOk = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Size = new Size(80, 32),
            Location = new Point(ClientSize.Width - 180, ClientSize.Height - 44),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };
        btnOk.Click += BtnOk_Click;

        var btnCancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Size = new Size(80, 32),
            Location = new Point(ClientSize.Width - 92, ClientSize.Height - 44),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };

        Controls.Add(btnOk);
        Controls.Add(btnCancel);

        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }

    private TabPage CreateTab(string title)
    {
        return new TabPage(title)
        {
            Padding = new Padding(12),
            BackColor = SystemColors.Control
        };
    }

    private (ComboBox filerFont, NumericUpDown filerSize, ComboBox colorTheme, CheckBox showBrowserTabCategoryRow, CheckBox showExtensions, CheckBox showDirectoryMarker, CheckBox showHiddenFiles, CheckBox showItemIcons, ComboBox dateFormat, ComboBox sizeFormat,
             ComboBox viewerFont, NumericUpDown viewerSize, CheckBox viewerWordWrap, CheckBox reuseImageViewer, CheckBox closeOnNonImage, CheckBox rememberBounds, CheckBox videoStillPreviewEnabled, ComboBox videoSkipSeconds)
        BuildDisplayAndPreviewTab(TabPage tab, string[] fonts, string[] colorThemes, string[] dateFormats, string[] sizeFormats)
    {
        // Layout Constants
        int lblW = 120;
        int inpX = 140;
        int comboW = 160;
        int sizeX = 308;
        int checkX = 32;
        int rowH = 32;
        int topY = 28;

        // --- Left: List Display ---
        var groupList = new GroupBox { Text = "一覧表示", Location = new Point(8, 6), Size = new Size(376, 400) };
        tab.Controls.Add(groupList);

        int top = topY;

        AddLabel(groupList, "フォント:", top, lblW);
        var filerFont = AddComboBox(groupList, inpX, top, comboW, fonts, _settings.Fonts.FileListFontFamily);
        var filerSize = AddNumericUpDown(groupList, sizeX, top, 60, (decimal)_settings.Fonts.FileListFontSize);
        top += rowH;

        AddLabel(groupList, "表示色:", top, lblW);
        var colorTheme = AddComboBox(groupList, inpX, top, comboW, colorThemes, _settings.Appearance.ColorTheme);
        top += rowH;

        var showBrowserTabCategoryRow = AddCheckBox(groupList, "上段のカテゴリタブを表示する", checkX, top, _settings.Appearance.ShowBrowserTabCategoryRow);
        top += rowH;
        var showExtensions = AddCheckBox(groupList, "拡張子を表示する", checkX, top, _settings.Appearance.ShowExtensions);
        top += rowH;
        var showDirectoryMarker = AddCheckBox(groupList, "ディレクトリに <DIR> を表示する", checkX, top, _settings.Appearance.ShowDirectoryMarker);
        top += rowH;
        var showHiddenFiles = AddCheckBox(groupList, "隠しファイルを表示する", checkX, top, _settings.Appearance.ShowHiddenFiles);
        top += rowH;
        var showItemIcons = AddCheckBox(groupList, "一覧に小さなアイコンを表示する", checkX, top, _settings.Appearance.ShowItemIcons);
        top += rowH;

        AddLabel(groupList, "日付形式:", top, lblW);
        var dateFormat = AddComboBox(groupList, inpX, top, comboW, dateFormats, _settings.Appearance.DateFormat);
        top += rowH;

        AddLabel(groupList, "サイズ形式:", top, lblW);
        var sizeFormat = AddComboBox(groupList, inpX, top, comboW, sizeFormats, _settings.Appearance.SizeFormat);

        // --- Right Top: Viewer ---
        var groupViewer = new GroupBox { Text = "ビューア", Location = new Point(392, 6), Size = new Size(376, 188) };
        tab.Controls.Add(groupViewer);

        top = topY;
        AddLabel(groupViewer, "Viewer フォント:", top, lblW);
        var viewerFont = AddComboBox(groupViewer, inpX, top, comboW, fonts, _settings.Fonts.ViewerFontFamily);
        var viewerSize = AddNumericUpDown(groupViewer, sizeX, top, 60, (decimal)_settings.Fonts.ViewerFontSize);
        top += rowH;

        var viewerWordWrap = AddCheckBox(groupViewer, "折り返しを既定で ON にする", checkX, top, _settings.Preview.ViewerWordWrap);
        top += rowH;
        var reuseImageViewer = AddCheckBox(groupViewer, "画像ビューアを再利用する", checkX, top, _settings.Preview.ReuseImageViewer);
        top += rowH;
        var closeOnNonImage = AddCheckBox(groupViewer, "非画像時にビューアを閉じる", checkX, top, _settings.Preview.CloseImageViewerOnNonImageSelection);
        top += rowH;
        var rememberBounds = AddCheckBox(groupViewer, "ビューアの位置/サイズを記憶する", checkX, top, _settings.Preview.RememberImageViewerBounds);

        // --- Right Bottom: Video Preview ---
        var groupVideoPreview = new GroupBox { Text = "動画静止画プレビュー", Location = new Point(392, 202), Size = new Size(376, 204) };
        tab.Controls.Add(groupVideoPreview);

        top = 24;
        var videoStillPreviewEnabled = AddCheckBox(groupVideoPreview, "有効にする", 16, top, _settings.Preview.VideoStillPreviewEnabled);
        top += rowH + 8;
        AddLabel(groupVideoPreview, "初期位置(秒):", top, lblW);
        var videoSkipSeconds = AddEditableComboBox(groupVideoPreview, inpX, top, 100, new[] { "0", "5", "10", "30", "60" }, _settings.Preview.VideoSkipSeconds.ToString());
        top += rowH + 8;
        AddHintLabel(groupVideoPreview, 16, top, 330, "※ ffmpeg設定は「外部連携」タブ");

        return (filerFont, filerSize, colorTheme, showBrowserTabCategoryRow, showExtensions, showDirectoryMarker, showHiddenFiles, showItemIcons, dateFormat, sizeFormat,
                viewerFont, viewerSize, viewerWordWrap, reuseImageViewer, closeOnNonImage, rememberBounds, videoStillPreviewEnabled, videoSkipSeconds);
    }

    private (CheckBox confirmDelete, CheckBox confirmPermanentDelete, CheckBox useMidFdManagedTrash, CheckBox reloadAfterFileOperation, CheckBox selectCreatedItem,
             ComboBox functionKeyProfile, ComboBox commandLauncherShortcut, CheckBox enableMouseGestures)
        BuildOperationAndInputTab(TabPage tab)
    {
        int labelWidth = 140;
        int baseX = labelWidth + 12;
        int rowH = 32;

        // --- Left: File Operation ---
        var groupFile = new GroupBox { Text = "ファイル操作", Location = new Point(8, 6), Size = new Size(376, 360) };
        tab.Controls.Add(groupFile);

        int top = 28;
        var confirmDelete = AddCheckBox(groupFile, "削除前に確認する", 16, top, _settings.FileOperations.ConfirmDelete);
        top += rowH;
        var confirmPermanentDelete = AddCheckBox(groupFile, "Shift+Delete 前に確認する", 16, top, _settings.FileOperations.ConfirmPermanentDelete);
        top += rowH;
        var useMidFdManagedTrash = AddCheckBox(groupFile, "削除時に MidFD管理ゴミ箱を使う", 16, top, _settings.FileOperations.UseMidFdManagedTrash);
        top += rowH;
        
        AddHintLabel(groupFile, 32, top, 330, "ON: Ctrl+Z による復元が可能になります。\n環境に応じ SQLite / JSON を自動選択します。");
        top += rowH + 20;

        var reloadAfterFileOperation = AddCheckBox(groupFile, "操作後に一覧を再読込する", 16, top, _settings.FileOperations.ReloadAfterFileOperation);
        top += rowH;
        var selectCreatedItem = AddCheckBox(groupFile, "新規作成後に自動選択する", 16, top, _settings.FileOperations.SelectCreatedItemAfterCreate);

        // --- Right: Input / Shortcut ---
        var groupInput = new GroupBox { Text = "キー操作 / ショートカット", Location = new Point(392, 6), Size = new Size(376, 360) };
        tab.Controls.Add(groupInput);

        top = 28;
        AddLabel(groupInput, "Fキー割り当て:", top, labelWidth);
        var functionKeyProfile = AddComboBox(groupInput, baseX, top, 160, new[] { "標準", "FD互換" }, ToFunctionKeyProfileDisplayValue(_settings.Input.FunctionKeyProfile));
        top += rowH + 8;

        AddLabel(groupInput, "ランチャー起動:", top, labelWidth);
        var commandLauncherShortcut = AddComboBox(groupInput, baseX, top, 160, new[] { "Ctrl+Shift+P", "Ctrl+Space", "None" }, _settings.Input.CommandLauncherShortcut);
        top += rowH + 8;

        AddHintLabel(groupInput, 16, top, 340, "コマンドランチャー（コマンドパレット）を起動するショートカットキーを選択します。");
        top += rowH + 18;

        var enableMouseGestures = AddCheckBox(groupInput, "マウスジェスチャーを有効にする", 16, top, _settings.Input.EnableMouseGestures);
        top += rowH;
        AddHintLabel(groupInput, 32, top, 320, "右ドラッグで戻る/進む、親へ移動、再読込、タブ/カテゴリ移動を行います。");

        return (confirmDelete, confirmPermanentDelete, useMidFdManagedTrash, reloadAfterFileOperation, selectCreatedItem, functionKeyProfile, commandLauncherShortcut, enableMouseGestures);
    }

    private (ComboBox profile, CheckBox restoreLastPath, CheckBox restoreTabsOnStartup, CheckBox restoreWindowBounds, CheckBox restoreColumnCount, CheckBox restoreSort)
        BuildLaunchAndRestoreTab(TabPage tab)
    {
        int rowH = 32;

        // --- Left: Startup ---
        var groupStartup = new GroupBox { Text = "起動時の復元", Location = new Point(8, 6), Size = new Size(376, 360) };
        tab.Controls.Add(groupStartup);

        int top = 28;
        AddLabel(groupStartup, "機能プロファイル:", top, 140);
        var profile = CreateFeatureProfileCombo(groupStartup, 160, top, 180, _settings.Profile);
        top += rowH;
        AddHintLabel(groupStartup, 16, top, 340, "高度機能α版は開発中機能を含みます。通常利用は実用安定版（推奨）を選択してください。");
        top += rowH + 8;
        var restoreLastPath = AddCheckBox(groupStartup, "前回フォルダを復元する", 16, top, _settings.Session.RestoreLastPath);
        top += rowH;
        var restoreTabsOnStartup = AddCheckBox(groupStartup, "前回の作業状態(タブ等)を復元する", 16, top, _settings.Session.RestoreTabsOnStartup);
        top += rowH + 8;

        AddHintLabel(groupStartup, 16, top, 340, "作業状態復元が ON の場合、カテゴリ、タブ構成、タブごとのマーク、固定状態等を復元します。");
        top += rowH + 24;
        var btnOpenFirstSetup = new Button
        {
            Text = "初回セットアップを開く...",
            Location = new Point(16, top),
            Size = new Size(180, 32)
        };
        btnOpenFirstSetup.Click += (_, _) => OpenFirstLaunchSetupDialog();
        groupStartup.Controls.Add(btnOpenFirstSetup);
        top += rowH + 4;
        AddHintLabel(groupStartup, 16, top, 340, "利用モード、Fキー配置、動画Enter動作、外部連携の基本設定を再設定できます。\n初期化ではありません。", 52);

        // --- Right: Display State ---
        var groupDisplay = new GroupBox { Text = "表示状態の復元", Location = new Point(392, 6), Size = new Size(376, 360) };
        tab.Controls.Add(groupDisplay);

        top = 28;
        var restoreWindowBounds = AddCheckBox(groupDisplay, "ウィンドウ位置/サイズを復元する", 16, top, _settings.Session.RestoreWindowBounds);
        top += rowH;
        var restoreColumnCount = AddCheckBox(groupDisplay, "前回の列数を復元する", 16, top, _settings.Session.RestoreColumnCount);
        top += rowH;
        var restoreSort = AddCheckBox(groupDisplay, "前回のソートを復元する", 16, top, _settings.Session.RestoreSort);

        return (profile, restoreLastPath, restoreTabsOnStartup, restoreWindowBounds, restoreColumnCount, restoreSort);
    }

    private (TextBox sevenZip, TextBox diff, TextBox editor, ComboBox videoPlaybackVolume, TextBox videoStillPreviewFfmpegPath, CheckBox videoEnterPlaysExternal, Label sevenZipStatus, Label diffStatus, Label editorStatus, Label videoStillPreviewFfmpegStatus)
        BuildExternalTab(TabPage tab)
    {
        int labelWidth = 120;
        int baseX = labelWidth + 12;
        int rowH = 64;

        // --- Left: Archive / Diff / Editor ---
        var groupArchive = new GroupBox { Text = "外部アプリケーション", Location = new Point(8, 6), Size = new Size(376, 360) };
        tab.Controls.Add(groupArchive);

        int top = 28;
        AddLabel(groupArchive, "7-Zip パス:", top, labelWidth);
        var sevenZip = AddTextBox(groupArchive, baseX, top, 160, _settings.SevenZip.ExePath ?? "");
        AddBrowseButton(groupArchive, baseX + 168, top - 1, 60, sevenZip);
        var sevenZipStatus = AddStatusLabel(groupArchive, baseX, top + 26, 240);
        top += rowH;

        AddLabel(groupArchive, "外部 Diff:", top, labelWidth);
        var diff = AddTextBox(groupArchive, baseX, top, 160, _settings.ExternalTools.ExternalDiffPath ?? "");
        AddBrowseButton(groupArchive, baseX + 168, top - 1, 60, diff);
        var diffStatus = AddStatusLabel(groupArchive, baseX, top + 26, 240);
        top += rowH;

        AddLabel(groupArchive, "外部エディタ:", top, labelWidth);
        var editor = AddTextBox(groupArchive, baseX, top, 160, _settings.ExternalTools.ExternalEditorPath ?? "");
        AddBrowseButton(groupArchive, baseX + 168, top - 1, 60, editor);
        var editorStatus = AddStatusLabel(groupArchive, baseX, top + 26, 240);
        top += rowH + 8;

        AddHintLabel(groupArchive, 16, top, 340, "E キーで選択ファイルをこのエディタで開きます。\n未設定時は notepad.exe を使用します。");

        // --- Right: External Tools / Video ---
        var groupTools = new GroupBox { Text = "外部ツール管理", Location = new Point(392, 6), Size = new Size(376, 176) };
        tab.Controls.Add(groupTools);

        top = 28;
        var btnManageTools = new Button
        {
            Text = "外部ツール定義の編集...",
            Location = new Point(16, top),
            Size = new Size(180, 32)
        };
        btnManageTools.Click += (_, _) =>
        {
            using var dlg = new ExternalToolDefinitionEditorDialog();
            dlg.ShowDialog(this);
        };
        groupTools.Controls.Add(btnManageTools);
        top += 48;

        AddHintLabel(groupTools, 16, top, 340, "コマンドパレット (Ctrl+Shift+P) や Alt+スロットで起動する外部ツールを管理します。");

        var groupVideoTools = new GroupBox { Text = "動画ツール", Location = new Point(392, 188), Size = new Size(376, 218) };
        tab.Controls.Add(groupVideoTools);
        top = 28;
        AddLabel(groupVideoTools, "動画ツールフォルダ:", top, labelWidth);
        var videoStillPreviewFfmpegPath = AddTextBox(groupVideoTools, baseX, top, 160, _settings.Preview.VideoToolDirectory ?? "");
        AddBrowseFolderButton(groupVideoTools, baseX + 168, top - 1, 60, videoStillPreviewFfmpegPath);
        var videoStillPreviewFfmpegStatus = AddStatusLabel(groupVideoTools, 16, top + 22, 340);
        top += 46;
        AddLabel(groupVideoTools, "ffplay音量(%):", top, labelWidth);
        var videoPlaybackVolume = AddEditableComboBox(groupVideoTools, baseX, top, 100, new[] { "0", "30", "50", "70", "100" }, _settings.Preview.VideoPlaybackVolumePercent.ToString());
        top += 30;
        var videoEnterPlaysExternal = AddCheckBox(groupVideoTools, "動画 Enter で外部再生する (Ctrl+Enterでプレビュー)", 16, top, _settings.Preview.VideoEnterPlaysExternal);
        top += 26;
        AddHintLabel(groupVideoTools, 16, top, 340, "※ 静止画: ffmpeg / 再生: ffplay / 長さ: ffprobe");

        sevenZip.TextChanged += (_, _) => RefreshExternalStatus();
        diff.TextChanged += (_, _) => RefreshExternalStatus();
        editor.TextChanged += (_, _) => RefreshExternalStatus();
        videoStillPreviewFfmpegPath.TextChanged += (_, _) => RefreshExternalStatus();

        return (sevenZip, diff, editor, videoPlaybackVolume, videoStillPreviewFfmpegPath, videoEnterPlaysExternal, sevenZipStatus, diffStatus, editorStatus, videoStillPreviewFfmpegStatus);
    }

    private (CheckBox enableLog, CheckBox enableDetail) BuildLogTab(TabPage tab)
    {
        // --- Left: Log settings ---
        var groupLog = new GroupBox { Text = "ログ設定", Location = new Point(8, 6), Size = new Size(376, 360) };
        tab.Controls.Add(groupLog);

        int top = 28;
        int rowH = 32;
        var enableLog = AddCheckBox(groupLog, "ログ出力を有効化", 16, top, _settings.Logging.IsEnabled);
        top += rowH;
        var enableDetail = AddCheckBox(groupLog, "詳細ログを有効化（調査用）", 16, top, _settings.Logging.IsDetailedEnabled);
        top += rowH + 8;

        AddHintLabel(groupLog, 16, top, 340, "通常ログは障害追跡用、詳細ログは drag/drop などの切り分け時だけ使います。");

        return (enableLog, enableDetail);
    }

    private void RefreshExternalStatus()
    {
        ApplyStatus(_sevenZipStatusLabel, GetSevenZipStatus(_sevenZipPathBox.Text));
        ApplyStatus(_diffStatusLabel, GetToolStatus(_diffPathBox.Text, fallbackToShell: false));
        ApplyStatus(_editorStatusLabel, GetToolStatus(_editorPathBox.Text, fallbackToShell: false));
        var videoToolStatus = GetVideoToolStatus(_videoStillPreviewFfmpegPathBox.Text);
        ApplyStatus(_videoStillPreviewFfmpegStatusLabel, (videoToolStatus.Text, videoToolStatus.Color));
        _statusToolTip.SetToolTip(_videoStillPreviewFfmpegStatusLabel, videoToolStatus.ToolTipText);
        _statusToolTip.SetToolTip(_videoStillPreviewFfmpegPathBox, videoToolStatus.ToolTipText);
    }

    private static void ApplyStatus(Label label, (string Text, Color Color) status)
    {
        label.Text = status.Text;
        label.ForeColor = status.Color;
    }

    private static (string Text, Color Color) GetSevenZipStatus(string pathText)
    {
        string? path = NullIfEmpty(pathText);
        if (!string.IsNullOrEmpty(path))
        {
            if (!File.Exists(path))
            {
                return ("状態: NG（指定パスが見つからない）", Color.Firebrick);
            }

            string? cliPath = SevenZipService.ResolveCliExecutable(path);
            if (string.IsNullOrWhiteSpace(cliPath) || !File.Exists(cliPath))
            {
                return ("状態: NG（7z.exe が見つからない）", Color.Firebrick);
            }

            string? guiPath = SevenZipService.ResolveGuiExecutable(cliPath);
            return string.IsNullOrWhiteSpace(guiPath)
                ? ("状態: OK（7z.exeあり / 7zG.exeなし）", Color.DarkOrange)
                : ("状態: OK（7z.exe / 7zG.exe あり）", Color.DarkGreen);
        }

        string? autoFound = SevenZipService.FindSevenZip();
        return string.IsNullOrEmpty(autoFound)
            ? ("状態: 未設定（自動探索でも見つからない）", Color.DarkOrange)
            : ("状態: 未設定（自動探索で見つかった）", Color.DarkGreen);
    }

    private static (string Text, Color Color) GetToolStatus(string pathText, bool fallbackToShell)
    {
        string? path = NullIfEmpty(pathText);
        if (!string.IsNullOrEmpty(path))
        {
            return File.Exists(path)
                ? ("状態: OK（指定パスあり）", Color.DarkGreen)
                : ("状態: NG（指定パスが見つからない）", Color.Firebrick);
        }

        return fallbackToShell
            ? ("状態: 未設定（関連付けへフォールバック）", Color.DarkOrange)
            : ("状態: 未設定（フォールバックなし）", Color.Gray);
    }

    private Label AddStatusLabel(Control parent, int x, int y, int width)
    {
        var label = new Label
        {
            Location = new Point(x, y),
            Size = new Size(width, 18),
            ForeColor = SystemColors.GrayText
        };
        parent.Controls.Add(label);
        return label;
    }

    private void AddHintLabel(Control parent, int x, int y, int width, string text, int height = 36)
    {
        parent.Controls.Add(new Label
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(width, height),
            ForeColor = SystemColors.GrayText
        });
    }

    private void AddLabel(Control parent, string text, int top, int width)
    {
        parent.Controls.Add(new Label
        {
            Text = text,
            Location = new Point(8, top + 4),
            Size = new Size(width, 20),
            TextAlign = ContentAlignment.MiddleRight,
        });
    }

    private TextBox AddTextBox(Control parent, int x, int top, int width, string initial)
    {
        var tb = new TextBox
        {
            Location = new Point(x, top),
            Size = new Size(width, 24),
            Text = initial,
        };
        parent.Controls.Add(tb);
        return tb;
    }

    private void AddBrowseButton(Control parent, int x, int top, int width, TextBox target)
    {
        var btn = new Button
        {
            Text = "参照",
            Location = new Point(x, top),
            Size = new Size(width, 26),
        };
        btn.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog
            {
                Title = "実行ファイルを選択",
                Filter = "実行ファイル (*.exe)|*.exe|すべてのファイル (*.*)|*.*",
            };

            string cur = target.Text.Trim();
            if (File.Exists(cur))
            {
                dlg.InitialDirectory = Path.GetDirectoryName(cur) ?? "";
            }

            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                target.Text = dlg.FileName;
            }
        };
        parent.Controls.Add(btn);
    }

    private void AddBrowseFolderButton(Control parent, int x, int top, int width, TextBox target)
    {
        var btn = new Button
        {
            Text = "参照",
            Location = new Point(x, top),
            Size = new Size(width, 26),
        };
        btn.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "動画ツールフォルダを選択 (ffmpeg.exe などの配置先)",
                UseDescriptionForTitle = true,
                SelectedPath = Directory.Exists(target.Text.Trim()) ? target.Text.Trim() : string.Empty
            };

            if (dlg.ShowDialog(this) == DialogResult.OK)
            {
                target.Text = dlg.SelectedPath;
            }
        };
        parent.Controls.Add(btn);
    }

    private ComboBox AddComboBox(Control parent, int x, int top, int width, string[] items, string current)
    {
        var cb = new ComboBox
        {
            Location = new Point(x, top),
            Size = new Size(width, 24),
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        cb.Items.AddRange(items);
        int idx = cb.FindStringExact(current);
        if (idx >= 0) cb.SelectedIndex = idx;
        else if (cb.Items.Count > 0) cb.SelectedIndex = 0;
        parent.Controls.Add(cb);
        return cb;
    }

    private ComboBox AddEditableComboBox(Control parent, int x, int top, int width, string[] items, string current)
    {
        var cb = new ComboBox
        {
            Location = new Point(x, top),
            Size = new Size(width, 24),
            DropDownStyle = ComboBoxStyle.DropDown
        };
        cb.Items.AddRange(items);
        cb.Text = current;
        parent.Controls.Add(cb);
        return cb;
    }

    private NumericUpDown AddNumericUpDown(Control parent, int x, int top, int width, decimal current)
    {
        var n = new NumericUpDown
        {
            Location = new Point(x, top),
            Size = new Size(width, 24),
            Minimum = 6,
            Maximum = 72,
            Value = current,
            DecimalPlaces = 1
        };
        parent.Controls.Add(n);
        return n;
    }

    private CheckBox AddCheckBox(Control parent, string text, int x, int y, bool isChecked)
    {
        var checkBox = new CheckBox
        {
            Text = text,
            Checked = isChecked,
            AutoSize = true,
            Location = new Point(x, y)
        };
        parent.Controls.Add(checkBox);
        return checkBox;
    }

    private void BtnOk_Click(object? sender, EventArgs e)
    {
        _settings.Profile = GetSelectedFeatureProfileSettingValue(_featureProfileCombo);
        _settings.Input.FunctionKeyProfile = ToFunctionKeyProfileValue(_functionKeyProfileCombo.Text);
        _settings.Input.CommandLauncherShortcut = _commandLauncherShortcutCombo.Text;
        _settings.Input.EnableMouseGestures = _enableMouseGesturesCheckBox.Checked;
        _settings.SevenZip.ExePath = NullIfEmpty(_sevenZipPathBox.Text);
        _settings.ExternalTools.ExternalDiffPath = NullIfEmpty(_diffPathBox.Text);
        _settings.ExternalTools.ExternalEditorPath = NullIfEmpty(_editorPathBox.Text);

        string? cliPath = SevenZipService.ResolveCliExecutable(_settings.SevenZip.ExePath);
        if (!string.IsNullOrWhiteSpace(cliPath) && File.Exists(cliPath))
        {
            string? guiPath = SevenZipService.ResolveGuiExecutable(cliPath);
            if (string.IsNullOrWhiteSpace(guiPath))
            {
                MessageBox.Show(
                    this,
                    "7z.exe は確認できましたが、7zG.exe が見つかりません。\n圧縮・解凍は従来どおり実行できますが、7-ZipのGUI進捗表示は使用できません。",
                    "7-Zip 設定",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        _settings.Fonts.FileListFontFamily = _filerFontCombo.Text;
        _settings.Fonts.FileListFontSize = (float)_filerFontSizeBox.Value;
        _settings.Fonts.ViewerFontFamily = _viewerFontCombo.Text;
        _settings.Fonts.ViewerFontSize = (float)_viewerFontSizeBox.Value;

        _settings.Appearance.ColorTheme = _colorThemeCombo.Text;
        _settings.Appearance.ShowBrowserTabCategoryRow = _showBrowserTabCategoryRowCheckBox.Checked;
        _settings.Appearance.ShowExtensions = _showExtensionsCheckBox.Checked;
        _settings.Appearance.ShowDirectoryMarker = _showDirectoryMarkerCheckBox.Checked;
        _settings.Appearance.ShowHiddenFiles = _showHiddenFilesCheckBox.Checked;
        _settings.Appearance.ShowItemIcons = _showItemIconsCheckBox.Checked;
        _settings.Appearance.DateFormat = _dateFormatCombo.Text;
        _settings.Appearance.SizeFormat = _sizeFormatCombo.Text;

        _settings.Preview.ViewerWordWrap = _viewerWordWrapCheckBox.Checked;
        _settings.Preview.ReuseImageViewer = _reuseImageViewerCheckBox.Checked;
        _settings.Preview.CloseImageViewerOnNonImageSelection = _closeImageViewerOnNonImageCheckBox.Checked;
        _settings.Preview.RememberImageViewerBounds = _rememberImageViewerBoundsCheckBox.Checked;
        _settings.Preview.VideoStillPreviewEnabled = _videoStillPreviewEnabledCheckBox.Checked;
        _settings.Preview.VideoToolDirectory = NullIfEmpty(_videoStillPreviewFfmpegPathBox.Text);
        _settings.Preview.VideoStillPreviewFfmpegPath = null;
        if (!int.TryParse(_videoSkipSecondsCombo.Text, out int videoSkipSeconds))
        {
            videoSkipSeconds = 10;
        }
        _settings.Preview.VideoSkipSeconds = Math.Clamp(videoSkipSeconds, 0, 600);
        if (!int.TryParse(_videoPlaybackVolumeCombo.Text, out int videoPlaybackVolume))
        {
            videoPlaybackVolume = 100;
        }
        _settings.Preview.VideoPlaybackVolumePercent = Math.Clamp(videoPlaybackVolume, 0, 100);
        _settings.Preview.VideoEnterPlaysExternal = _videoEnterPlaysExternalCheckBox.Checked;

        _settings.FileOperations.ConfirmDelete = _confirmDeleteCheckBox.Checked;
        _settings.FileOperations.ConfirmPermanentDelete = _confirmPermanentDeleteCheckBox.Checked;
        _settings.FileOperations.UseMidFdManagedTrash = _useMidFdManagedTrashCheckBox.Checked;
        // _settings.FileOperations.ManagedTrashStoreMode は Initialize 時に自動決定するためUIからは変更しない

        _settings.FileOperations.ReloadAfterFileOperation = _reloadAfterFileOperationCheckBox.Checked;
        _settings.FileOperations.SelectCreatedItemAfterCreate = _selectCreatedItemCheckBox.Checked;

        _settings.Session.RestoreLastPath = _restoreLastPathCheckBox.Checked;
        _settings.Session.RestoreTabsOnStartup = _restoreTabsOnStartupCheckBox.Checked;
        _settings.Session.RestoreWindowBounds = _restoreWindowBoundsCheckBox.Checked;
        _settings.Session.RestoreColumnCount = _restoreColumnCountCheckBox.Checked;
        _settings.Session.RestoreSort = _restoreSortCheckBox.Checked;

        _settings.Logging.IsEnabled = _enableLogCheckBox.Checked;
        _settings.Logging.IsDetailedEnabled = _enableDetailedLogCheckBox.Checked;

        SettingsManager.Save(_settings);
    }

    private void OpenFirstLaunchSetupDialog()
    {
        var setupSettings = _settings.Clone();
        setupSettings.Profile = GetSelectedFeatureProfileSettingValue(_featureProfileCombo);
        setupSettings.Input.FunctionKeyProfile = ToFunctionKeyProfileValue(_functionKeyProfileCombo.Text);
        setupSettings.Preview.VideoEnterPlaysExternal = _videoEnterPlaysExternalCheckBox.Checked;
        setupSettings.SevenZip.ExePath = NullIfEmpty(_sevenZipPathBox.Text);
        setupSettings.Preview.VideoToolDirectory = NullIfEmpty(_videoStillPreviewFfmpegPathBox.Text);
        setupSettings.ExternalTools.ExternalEditorPath = NullIfEmpty(_editorPathBox.Text);

        using var dialog = new FeatureProfileSelectionDialog(setupSettings);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        SetFeatureProfileComboValue(dialog.SelectedProfile);
        _functionKeyProfileCombo.Text = dialog.UseFdCompatibleFunctionKeys ? "FD互換" : "標準";
        _videoEnterPlaysExternalCheckBox.Checked = dialog.VideoEnterPlaysExternal;
        _sevenZipPathBox.Text = dialog.SevenZipPath ?? string.Empty;
        _videoStillPreviewFfmpegPathBox.Text = dialog.VideoToolDirectory ?? string.Empty;
        _editorPathBox.Text = dialog.ExternalEditorPath ?? string.Empty;
        RefreshExternalStatus();
    }

    private string[] GetInstalledFontNames()
    {
        using var installedFonts = new InstalledFontCollection();
        return installedFonts.Families.Select(f => f.Name).ToArray();
    }

    private static string? NullIfEmpty(string s)
    {
        var t = s.Trim();
        return string.IsNullOrEmpty(t) ? null : t;
    }

    private static string ToFunctionKeyProfileDisplayValue(string? value)
    {
        return string.Equals(value, InputSettings.FdCompatibleProfileValue, StringComparison.OrdinalIgnoreCase)
            ? "FD互換"
            : "標準";
    }

    private static string ToFunctionKeyProfileValue(string? displayValue)
    {
        return string.Equals(displayValue, "FD互換", StringComparison.Ordinal)
            ? InputSettings.FdCompatibleProfileValue
            : InputSettings.StandardProfileValue;
    }

    private static (string Text, Color Color, string ToolTipText) GetVideoToolStatus(string pathText)
    {
        string? path = NullIfEmpty(pathText);
        VideoToolResolutionResult resolution = VideoToolResolutionService.Resolve(path);

        string ffmpegSummary = resolution.FfmpegFound ? "ffmpeg OK" : "ffmpeg 未検出";
        string ffplaySummary = resolution.FfplayFound ? "ffplay OK" : "ffplay 未検出";
        string ffprobeSummary = resolution.FfprobeFound ? "ffprobe OK" : "ffprobe 未検出";
        string text;
        Color color;

        text = $"状態: {ffmpegSummary} / {ffplaySummary} / {ffprobeSummary}";
        if (resolution.FfmpegFound && resolution.FfplayFound && resolution.FfprobeFound)
        {
            color = Color.DarkGreen;
        }
        else if (resolution.FfmpegFound)
        {
            color = Color.DarkOrange;
        }
        else
        {
            color = Color.Firebrick;
        }

        string tooltipText = BuildVideoToolStatusToolTip(path, resolution);
        return (text, color, tooltipText);
    }

    private static string BuildVideoToolStatusToolTip(string? configuredPath, VideoToolResolutionResult resolution)
    {
        var lines = new List<string>();
        lines.Add($"設定値: {(string.IsNullOrWhiteSpace(configuredPath) ? "(未設定)" : configuredPath)}");
        lines.Add($"ffmpeg: {(resolution.FfmpegFound ? resolution.FfmpegPath : "(未検出)")}");
        lines.Add($"ffplay: {(resolution.FfplayFound ? resolution.FfplayPath : "(未検出)")}");
        lines.Add($"ffprobe: {(resolution.FfprobeFound ? resolution.FfprobePath : "(未検出)")}");
        if (!string.IsNullOrWhiteSpace(resolution.FfmpegSource))
        {
            lines.Add($"ffmpeg解決元: {resolution.FfmpegSource}");
        }

        if (!string.IsNullOrWhiteSpace(resolution.FfplaySource))
        {
            lines.Add($"ffplay解決元: {resolution.FfplaySource}");
        }
        if (!string.IsNullOrWhiteSpace(resolution.FfprobeSource))
        {
            lines.Add($"ffprobe解決元: {resolution.FfprobeSource}");
        }

        if (!resolution.FfplayFound && resolution.FfplayCandidates.Count > 0)
        {
            lines.Add("ffplay探索候補:");
            foreach (string candidate in resolution.FfplayCandidates.Take(4))
            {
                lines.Add($"- {candidate}");
            }
        }
        if (!resolution.FfprobeFound && resolution.FfprobeCandidates.Count > 0)
        {
            lines.Add("ffprobe探索候補:");
            foreach (string candidate in resolution.FfprobeCandidates.Take(4))
            {
                lines.Add($"- {candidate}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private ComboBox CreateFeatureProfileCombo(Control parent, int x, int top, int width, string currentSettingValue)
    {
        var combo = new ComboBox
        {
            Location = new Point(x, top),
            Size = new Size(width, 24),
            DropDownStyle = ComboBoxStyle.DropDownList
        };

        combo.Items.AddRange(FeatureProfileOptions.Select(option => option.DisplayName).Cast<object>().ToArray());

        string initialSetting = FeatureProfileService.TryResolveProfile(currentSettingValue, out FeatureProfile parsed)
            ? FeatureProfileService.ToSettingValue(parsed)
            : FeatureProfile.PracticalStable.ToString();
        int selectedIndex = Array.FindIndex(FeatureProfileOptions, option => string.Equals(option.SettingValue, initialSetting, StringComparison.OrdinalIgnoreCase));
        combo.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;

        parent.Controls.Add(combo);
        return combo;
    }

    private static string GetSelectedFeatureProfileSettingValue(ComboBox combo)
    {
        int selectedIndex = combo.SelectedIndex;
        if (selectedIndex < 0 || selectedIndex >= FeatureProfileOptions.Length)
        {
            return FeatureProfile.PracticalStable.ToString();
        }

        return FeatureProfileOptions[selectedIndex].SettingValue;
    }

    private void SetFeatureProfileComboValue(FeatureProfile profile)
    {
        string settingValue = FeatureProfileService.ToSettingValue(profile);
        int selectedIndex = Array.FindIndex(FeatureProfileOptions, option => string.Equals(option.SettingValue, settingValue, StringComparison.OrdinalIgnoreCase));
        _featureProfileCombo.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
    }
}
