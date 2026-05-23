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
    private readonly CheckBox _useUnderlineCursorCheckBox;
    private readonly ComboBox _fileDisplayModeCombo;
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
    private readonly CheckBox _useCustomColorsCheckBox;
    private readonly CheckBox _enableColorAssistCheckBox;
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

    // 配色タブ用の新規追加フィールド
    private readonly Button _deleteColorPresetButton;
    private readonly ListBox _fileListColorFieldListBox;
    private readonly TextBox _fileListColorHexTextBox;
    private readonly NumericUpDown _fileListColorRedBox;
    private readonly NumericUpDown _fileListColorGreenBox;
    private readonly NumericUpDown _fileListColorBlueBox;
    private readonly Button _fileListColorPickerButton;
    private readonly Panel _fileListColorCurrentPreviewPanel;
    private readonly ListView _fileListColorPreviewPanel;
    private readonly Label _fileListColorWarningLabel;
    private bool _updatingColorFromUi;
    private bool _suppressColorUiEvents;
    private bool _fileListCustomColorsEnabledForSave;

    // UIクローム/Viewer 手動指定色コントロール
    private readonly CheckBox _customUiThemeCheckBox;
    private readonly Button _customUiThemeEditButton;
    private readonly Panel _customFilerBackPreview;
    private readonly Panel _customFilerForePreview;
    private readonly Panel _customViewerBackPreview;
    private readonly Panel _customViewerForePreview;

    public enum InitialTab
    {
        DisplayAndViewer = 0,
        Color = 1,
        OperationAndInput = 2,
        LaunchAndRestore = 3,
        External = 4,
        Log = 5
    }

    public event EventHandler? SettingsApplied;

    public SettingsForm(AppSettings settings, FeatureProfile effectiveProfile, InitialTab initialTab = InitialTab.DisplayAndViewer)
    {
        ArgumentNullException.ThrowIfNull(settings);
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
        _fileListCustomColorsEnabledForSave = _settings.Appearance.UseCustomFileListColors;
        _suppressColorUiEvents = true;

        // 不要になったカスタム色チェックボックスのダミー初期化
        _useCustomColorsCheckBox = new CheckBox { Checked = _settings.Appearance.UseCustomFileListColors };

        Text = "設定";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Padding = new Padding(12);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(800, 610);

        var tabs = new TabControl
        {
            Dock = DockStyle.Top,
            Height = 530
        };

        var tabDisplayAndPreview = CreateTab("表示 / ビューア");
        var tabColor = CreateTab("配色");
        var tabOpAndInput = CreateTab("操作 / 入力");
        var tabLaunchAndRestore = CreateTab("起動 / 復元");
        var tabExternal = CreateTab("外部連携");
        var tabLog = CreateTab("ログ / 詳細");

        tabs.TabPages.AddRange(new[]
        {
            tabDisplayAndPreview,
            tabColor,
            tabOpAndInput,
            tabLaunchAndRestore,
            tabExternal,
            tabLog
        });

        Controls.Add(tabs);
        tabs.SelectedIndex = Math.Clamp((int)initialTab, 0, tabs.TabPages.Count - 1);
        Shown += (_, _) =>
        {
            tabs.SelectedIndex = Math.Clamp((int)initialTab, 0, tabs.TabPages.Count - 1);
        };

        string[] fonts = GetInstalledFontNames();
        string[] dateFormats = { "yyyy-MM-dd HH:mm", "yyyy/MM/dd HH:mm:ss", "yyyy-MM-dd(ddd) HH:mm" };
        string[] sizeFormats = { "HumanReadable", "Bytes", "KB/MB" };

        (_filerFontCombo, _filerFontSizeBox, _showBrowserTabCategoryRowCheckBox, _showExtensionsCheckBox, _showDirectoryMarkerCheckBox, _showHiddenFilesCheckBox, _showItemIconsCheckBox, _useUnderlineCursorCheckBox, _fileDisplayModeCombo, _dateFormatCombo, _sizeFormatCombo,
         _viewerFontCombo, _viewerFontSizeBox, _viewerWordWrapCheckBox, _reuseImageViewerCheckBox, _closeImageViewerOnNonImageCheckBox, _rememberImageViewerBoundsCheckBox, _videoStillPreviewEnabledCheckBox, _videoSkipSecondsCombo)
            = BuildDisplayAndPreviewTab(tabDisplayAndPreview, fonts, dateFormats, sizeFormats);

        ColorTabResult colorTabResult = BuildColorTab(tabColor);
        _enableColorAssistCheckBox = colorTabResult.EnableColorAssistCheckBox;
        _colorThemeCombo = colorTabResult.ColorThemeCombo;
        _deleteColorPresetButton = colorTabResult.DeleteColorPresetButton;
        _fileListColorFieldListBox = colorTabResult.FileListColorFieldListBox;
        _fileListColorHexTextBox = colorTabResult.FileListColorHexTextBox;
        _fileListColorRedBox = colorTabResult.FileListColorRedBox;
        _fileListColorGreenBox = colorTabResult.FileListColorGreenBox;
        _fileListColorBlueBox = colorTabResult.FileListColorBlueBox;
        _fileListColorPickerButton = colorTabResult.FileListColorPickerButton;
        _fileListColorCurrentPreviewPanel = colorTabResult.FileListColorCurrentPreviewPanel;
        _fileListColorPreviewPanel = colorTabResult.FileListColorPreviewPanel;
        _fileListColorWarningLabel = colorTabResult.FileListColorWarningLabel;
        _customUiThemeCheckBox = colorTabResult.CustomUiThemeCheckBox;
        _customUiThemeEditButton = colorTabResult.CustomUiThemeEditButton;
        _customFilerBackPreview = colorTabResult.CustomFilerBackPreview;
        _customFilerForePreview = colorTabResult.CustomFilerForePreview;
        _customViewerBackPreview = colorTabResult.CustomViewerBackPreview;
        _customViewerForePreview = colorTabResult.CustomViewerForePreview;

        (_confirmDeleteCheckBox, _confirmPermanentDeleteCheckBox, _useMidFdManagedTrashCheckBox, _reloadAfterFileOperationCheckBox, _selectCreatedItemCheckBox,
         _functionKeyProfileCombo, _commandLauncherShortcutCombo, _enableMouseGesturesCheckBox)
            = BuildOperationAndInputTab(tabOpAndInput);

        (_featureProfileCombo, _restoreLastPathCheckBox, _restoreTabsOnStartupCheckBox, _restoreWindowBoundsCheckBox, _restoreColumnCountCheckBox, _restoreSortCheckBox)
            = BuildLaunchAndRestoreTab(tabLaunchAndRestore);

        (_sevenZipPathBox, _diffPathBox, _editorPathBox, _videoPlaybackVolumeCombo, _videoStillPreviewFfmpegPathBox, _videoEnterPlaysExternalCheckBox, _sevenZipStatusLabel, _diffStatusLabel, _editorStatusLabel, _videoStillPreviewFfmpegStatusLabel)
            = BuildExternalTab(tabExternal);

        (_enableLogCheckBox, _enableDetailedLogCheckBox) = BuildLogTab(tabLog);

        InitializeColorTabState();
        _suppressColorUiEvents = false;

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
            Text = "キャンセル",
            DialogResult = DialogResult.Cancel,
            Size = new Size(80, 32),
            Location = new Point(ClientSize.Width - 92, ClientSize.Height - 44),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };

        var btnApply = new Button
        {
            Text = "適用",
            Size = new Size(80, 32),
            Location = new Point(ClientSize.Width - 268, ClientSize.Height - 44),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };
        btnApply.Click += BtnApply_Click;

        Controls.Add(btnOk);
        Controls.Add(btnCancel);
        Controls.Add(btnApply);

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

    private (ComboBox filerFont, NumericUpDown filerSize, CheckBox showBrowserTabCategoryRow, CheckBox showExtensions, CheckBox showDirectoryMarker, CheckBox showHiddenFiles, CheckBox showItemIcons, CheckBox useUnderlineCursor, ComboBox fileDisplayMode, ComboBox dateFormat, ComboBox sizeFormat,
             ComboBox viewerFont, NumericUpDown viewerSize, CheckBox viewerWordWrap, CheckBox reuseImageViewer, CheckBox closeOnNonImage, CheckBox rememberBounds, CheckBox videoStillPreviewEnabled, ComboBox videoSkipSeconds)
        BuildDisplayAndPreviewTab(TabPage tab, string[] fonts, string[] dateFormats, string[] sizeFormats)
    {
        // Layout Constants
        int lblW = 100;
        int inpX = 110;
        int comboW = 190;
        int sizeX = 308;
        int checkX = 32;
        int rowH = 28;
        int topY = 22;

        // --- Left: List Display ---
        var groupList = new GroupBox { Text = "一覧表示", Location = new Point(8, 6), Size = new Size(376, 400) };
        tab.Controls.Add(groupList);

        int top = topY;

        AddLabel(groupList, "フォント:", top, lblW);
        var filerFont = AddComboBox(groupList, inpX, top, 190, fonts, _settings.Fonts.FileListFontFamily);
        var filerSize = AddNumericUpDown(groupList, sizeX, top, 60, (decimal)_settings.Fonts.FileListFontSize);
        top += rowH + 8;

        // チェックボックス群（2列配置）
        int checkY = top;
        var showBrowserTabCategoryRow = AddCheckBox(groupList, "上段のカテゴリタブを表示する", 16, checkY, _settings.Appearance.ShowBrowserTabCategoryRow);
        var showHiddenFiles = AddCheckBox(groupList, "隠しファイルを表示する", 200, checkY, _settings.Appearance.ShowHiddenFiles);
        checkY += 22;

        var showExtensions = AddCheckBox(groupList, "拡張子を表示する", 16, checkY, _settings.Appearance.ShowExtensions);
        var showItemIcons = AddCheckBox(groupList, "一覧にアイコンを表示する", 200, checkY, _settings.Appearance.ShowItemIcons);
        checkY += 22;

        var showDirectoryMarker = AddCheckBox(groupList, "ディレクトリに <DIR> を表示", 16, checkY, _settings.Appearance.ShowDirectoryMarker);
        var useUnderlineCursor = AddCheckBox(groupList, "カーソル行をアンダーライン表示", 200, checkY, _settings.Appearance.UseUnderlineCursor);
        top = checkY + 28;

        AddLabel(groupList, "一覧表示:", top, lblW);
        var fileDisplayMode = AddFileDisplayModeCombo(groupList, inpX, top, 248, _settings.Appearance.ResolveFileDisplayMode());
        top += rowH;

        AddLabel(groupList, "日付形式:", top, lblW);
        var dateFormat = AddComboBox(groupList, inpX, top, 248, dateFormats, _settings.Appearance.DateFormat);
        top += rowH;

        AddLabel(groupList, "サイズ形式:", top, lblW);
        var sizeFormat = AddComboBox(groupList, inpX, top, 248, sizeFormats, _settings.Appearance.SizeFormat);
        top += rowH + 16;

        AddHintLabel(groupList, 16, top, 330, "※ 配色は「配色」タブで設定します。");

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

        return (filerFont, filerSize, showBrowserTabCategoryRow, showExtensions, showDirectoryMarker, showHiddenFiles, showItemIcons, useUnderlineCursor, fileDisplayMode, dateFormat, sizeFormat,
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
        AddLabel(groupInput, "操作プリセット:", top, labelWidth);
        var functionKeyProfile = AddComboBox(groupInput, baseX, top, 160, new[] { "MidFD標準", "FD/WinFD互換" }, ToFunctionKeyProfileDisplayValue(_settings.Input.FunctionKeyProfile));
        top += rowH;
        AddHintLabel(groupInput, 16, top, 340, "FD/WinFD互換: Fキー配置・一部Shift+F・列数キー操作をWinFD寄りにします。");
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

    private ComboBox AddFileDisplayModeCombo(Control parent, int x, int top, int width, BrowserFileDisplayMode current)
    {
        var cb = new ComboBox
        {
            Location = new Point(x, top),
            Size = new Size(width, 24),
            DropDownStyle = ComboBoxStyle.DropDownList
        };

        cb.Items.Add("ファイル名のみ");
        cb.Items.Add("サイズ");
        cb.Items.Add("サイズ・更新日時");

        cb.SelectedIndex = current switch
        {
            BrowserFileDisplayMode.NameSize => 1,
            BrowserFileDisplayMode.NameSizeDate => 2,
            _ => 0
        };

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

    private void SaveCurrentSettings()
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

        PersistEditedFileListColorsAsPresetIfNeeded();
        _settings.Appearance.ColorTheme = _colorThemeCombo.Text;
        _settings.Appearance.UseCustomFileListColors = _fileListCustomColorsEnabledForSave;
        _settings.Appearance.EnableSemanticColorAssist = _enableColorAssistCheckBox.Checked;
        _settings.Appearance.CustomUiThemeColorsEnabled = _customUiThemeCheckBox.Checked;
        _settings.Appearance.CustomFilerBackColor = UiThemeResolver.ToHexString(GetPreviewColor(_customFilerBackPreview, _settings.Appearance.CustomFilerBackColor));
        _settings.Appearance.CustomFilerForeColor = UiThemeResolver.ToHexString(GetPreviewColor(_customFilerForePreview, _settings.Appearance.CustomFilerForeColor));
        _settings.Appearance.CustomViewerBackColor = UiThemeResolver.ToHexString(GetPreviewColor(_customViewerBackPreview, _settings.Appearance.CustomViewerBackColor));
        _settings.Appearance.CustomViewerForeColor = UiThemeResolver.ToHexString(GetPreviewColor(_customViewerForePreview, _settings.Appearance.CustomViewerForeColor));
        _settings.Appearance.ShowBrowserTabCategoryRow = _showBrowserTabCategoryRowCheckBox.Checked;
        _settings.Appearance.ShowExtensions = _showExtensionsCheckBox.Checked;
        _settings.Appearance.ShowDirectoryMarker = _showDirectoryMarkerCheckBox.Checked;
        _settings.Appearance.ShowHiddenFiles = _showHiddenFilesCheckBox.Checked;
        _settings.Appearance.ShowItemIcons = _showItemIconsCheckBox.Checked;
        _settings.Appearance.UseUnderlineCursor = _useUnderlineCursorCheckBox.Checked;
        _settings.Appearance.FileDisplayMode = _fileDisplayModeCombo.SelectedIndex switch
        {
            1 => BrowserFileDisplayMode.NameSize,
            2 => BrowserFileDisplayMode.NameSizeDate,
            _ => BrowserFileDisplayMode.NameOnly
        };
        _settings.Appearance.ShowFileSizeAndDateInBrowser = _settings.Appearance.FileDisplayMode == BrowserFileDisplayMode.NameSizeDate;
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
        _settings.Preview.VideoSkipSeconds = Math.Max(0, videoSkipSeconds);
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

    private void PersistEditedFileListColorsAsPresetIfNeeded()
    {
        if (!_fileListCustomColorsEnabledForSave || _colorThemeCombo == null)
        {
            return;
        }

        string currentThemeKey = _colorThemeCombo.Text;
        string targetPresetName;

        if (FileListColorResolver.TryGetUserPresetName(currentThemeKey, out string? userPresetName) &&
            !string.IsNullOrWhiteSpace(userPresetName))
        {
            targetPresetName = userPresetName;
        }
        else
        {
            targetPresetName = GenerateNextColorPresetName();
            string targetKey = FileListColorResolver.MakeUserPresetKey(targetPresetName);
            _settings.Appearance.ColorTheme = targetKey;
            _suppressColorUiEvents = true;
            try
            {
                ReloadPresetsCombo(targetKey);
            }
            finally
            {
                _suppressColorUiEvents = false;
            }
        }

        var existing = _settings.Appearance.CustomFileListColorPresets
            .FirstOrDefault(p => string.Equals(p.Name, targetPresetName, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            existing.Colors = _settings.Appearance.CustomFileListColors.Clone();
        }
        else
        {
            _settings.Appearance.CustomFileListColorPresets.Add(new CustomFileListColorPreset
            {
                Name = targetPresetName,
                Colors = _settings.Appearance.CustomFileListColors.Clone()
            });
        }

        string resolvedKey = FileListColorResolver.MakeUserPresetKey(targetPresetName);
        _settings.Appearance.ColorTheme = resolvedKey;
        _fileListCustomColorsEnabledForSave = false;
        _suppressColorUiEvents = true;
        try
        {
            ReloadPresetsCombo(resolvedKey);
        }
        finally
        {
            _suppressColorUiEvents = false;
        }
    }

    private string GenerateNextColorPresetName()
    {
        var names = _settings.Appearance.CustomFileListColorPresets
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (int i = 1; i <= 9999; i++)
        {
            string candidate = $"color {i}";
            if (!names.Contains(candidate))
            {
                return candidate;
            }
        }

        return $"color {DateTime.Now:yyyyMMddHHmmss}";
    }

    private void BtnOk_Click(object? sender, EventArgs e)
    {
        SaveCurrentSettings();
    }

    private void BtnApply_Click(object? sender, EventArgs e)
    {
        SaveCurrentSettings();
        SettingsApplied?.Invoke(this, EventArgs.Empty);
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
        _functionKeyProfileCombo.Text = dialog.UseFdCompatibleFunctionKeys ? "FD/WinFD互換" : "MidFD標準";
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
            ? "FD/WinFD互換"
            : "MidFD標準";
    }

    private static string ToFunctionKeyProfileValue(string? displayValue)
    {
        return string.Equals(displayValue, "FD/WinFD互換", StringComparison.Ordinal)
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

    private sealed class ColorTabResult
    {
        public required CheckBox EnableColorAssistCheckBox;
        public required ComboBox ColorThemeCombo;
        public required Button DeleteColorPresetButton;
        public required ListBox FileListColorFieldListBox;
        public required TextBox FileListColorHexTextBox;
        public required NumericUpDown FileListColorRedBox;
        public required NumericUpDown FileListColorGreenBox;
        public required NumericUpDown FileListColorBlueBox;
        public required Button FileListColorPickerButton;
        public required Panel FileListColorCurrentPreviewPanel;
        public required ListView FileListColorPreviewPanel;
        public required Label FileListColorWarningLabel;
        public required CheckBox CustomUiThemeCheckBox;
        public required Button CustomUiThemeEditButton;
        public required Panel CustomFilerBackPreview;
        public required Panel CustomFilerForePreview;
        public required Panel CustomViewerBackPreview;
        public required Panel CustomViewerForePreview;
    }

    private static Color GetPreviewColor(Panel preview, string? fallbackHex)
    {
        if (preview.BackColor != Color.Empty && preview.BackColor != Control.DefaultBackColor)
            return preview.BackColor;
        return UiThemeResolver.TryParseColor(fallbackHex) ?? Color.Black;
    }

    private ColorTabResult BuildColorTab(TabPage tab)
    {
        tab.AutoScroll = false;
        var groupCustom = new GroupBox { Text = "一覧配色カスタマイズ", Location = new Point(8, 6), Size = new Size(405, 330) };
        var groupPreview = new GroupBox { Text = "プレビュー", Location = new Point(420, 6), Size = new Size(352, 330) };
        tab.Controls.Add(groupCustom);
        tab.Controls.Add(groupPreview);

        int top = 22;
        var lblTheme = new Label
        {
            Text = "表示色:",
            Location = new Point(16, top + 4),
            Size = new Size(60, 20),
            TextAlign = ContentAlignment.MiddleRight
        };
        var colorThemeCombo = new ComboBox
        {
            Location = new Point(82, top),
            Size = new Size(150, 24),
            DropDownStyle = ComboBoxStyle.DropDownList
        };

        var btnRegister = new Button
        {
            Text = "登録",
            Location = new Point(248, top - 1),
            Size = new Size(65, 26)
        };

        var deleteColorPresetButton = new Button
        {
            Text = "削除",
            Location = new Point(319, top - 1),
            Size = new Size(65, 26)
        };

        groupCustom.Controls.Add(lblTheme);
        groupCustom.Controls.Add(colorThemeCombo);
        groupCustom.Controls.Add(btnRegister);
        groupCustom.Controls.Add(deleteColorPresetButton);

        top += 30;
        var btnReset = new Button
        {
            Text = "選択プリセットでリセット",
            Location = new Point(12, top),
            Size = new Size(165, 26)
        };

        var enableColorAssistCheckBox = new CheckBox
        {
            Text = "見えにくい色を自動補正",
            Location = new Point(184, top + 3),
            AutoSize = true,
            Checked = _settings.Appearance.EnableSemanticColorAssist
        };

        groupCustom.Controls.Add(btnReset);
        groupCustom.Controls.Add(enableColorAssistCheckBox);

        top += 34;
        var fileListColorFieldListBox = new ListBox
        {
            Location = new Point(12, top),
            Size = new Size(165, 210),
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 22
        };
        fileListColorFieldListBox.Items.AddRange(ColorFieldItems.Select(x => x.DisplayName).ToArray());
        groupCustom.Controls.Add(fileListColorFieldListBox);

        int adjX = 196;
        var fileListColorCurrentPreviewPanel = new Panel
        {
            Location = new Point(adjX, top),
            Size = new Size(190, 36),
            BorderStyle = BorderStyle.FixedSingle
        };

        var fileListColorPickerButton = new Button
        {
            Text = "色選択...",
            Location = new Point(adjX, top + 42),
            Size = new Size(188, 28)
        };

        var lblHex = new Label
        {
            Text = "HEX:",
            Location = new Point(adjX, top + 78),
            Size = new Size(40, 20),
            TextAlign = ContentAlignment.MiddleRight
        };
        var fileListColorHexTextBox = new TextBox
        {
            Location = new Point(adjX + 44, top + 75),
            Size = new Size(144, 24)
        };

        var lblR = new Label
        {
            Text = "R:",
            Location = new Point(adjX, top + 108),
            Size = new Size(40, 20),
            TextAlign = ContentAlignment.MiddleRight
        };
        var fileListColorRedBox = new NumericUpDown
        {
            Location = new Point(adjX + 44, top + 105),
            Size = new Size(144, 24),
            Minimum = 0,
            Maximum = 255
        };

        var lblG = new Label
        {
            Text = "G:",
            Location = new Point(adjX, top + 138),
            Size = new Size(40, 20),
            TextAlign = ContentAlignment.MiddleRight
        };
        var fileListColorGreenBox = new NumericUpDown
        {
            Location = new Point(adjX + 44, top + 135),
            Size = new Size(144, 24),
            Minimum = 0,
            Maximum = 255
        };

        var lblB = new Label
        {
            Text = "B:",
            Location = new Point(adjX, top + 168),
            Size = new Size(40, 20),
            TextAlign = ContentAlignment.MiddleRight
        };
        var fileListColorBlueBox = new NumericUpDown
        {
            Location = new Point(adjX + 44, top + 165),
            Size = new Size(144, 24),
            Minimum = 0,
            Maximum = 255
        };

        groupCustom.Controls.Add(fileListColorCurrentPreviewPanel);
        groupCustom.Controls.Add(fileListColorPickerButton);
        groupCustom.Controls.Add(lblHex);
        groupCustom.Controls.Add(fileListColorHexTextBox);
        groupCustom.Controls.Add(lblR);
        groupCustom.Controls.Add(fileListColorRedBox);
        groupCustom.Controls.Add(lblG);
        groupCustom.Controls.Add(fileListColorGreenBox);
        groupCustom.Controls.Add(lblB);
        groupCustom.Controls.Add(fileListColorBlueBox);

        top += 210;
        var warningLabelLocal = new Label
        {
            Location = new Point(12, top),
            Size = new Size(370, 24),
            Font = new Font(tab.Font, FontStyle.Bold)
        };
        groupCustom.Controls.Add(warningLabelLocal);

        // ── UI基調色グループ ──────────────────────────────────
        var groupUiColors = new GroupBox
        {
            Text = "UI基調色（メニュー/ヘッダ/ステータス/Viewer）",
            Location = new Point(8, 342),
            Size = new Size(734, 150)
        };
        tab.Controls.Add(groupUiColors);

        int ux = 12;
        int uy = 20;

        var syncModeNote = new Label
        {
            Text = "通常は表示色に合わせます。個別に変えたい場合だけ手動指定してください。",
            Location = new Point(ux, uy + 2),
            Size = new Size(704, 20),
            ForeColor = Color.DimGray
        };
        groupUiColors.Controls.Add(syncModeNote);
        uy += 24;

        var customCb = new CheckBox
        {
            Text = "UI基調色を手動指定する",
            Location = new Point(ux, uy),
            AutoSize = true,
            Checked = _settings.Appearance.CustomUiThemeColorsEnabled
        };

        var customEditButton = new Button
        {
            Text = "UI基調色を調整...",
            Location = new Point(ux + 190, uy - 2),
            Size = new Size(140, 26)
        };

        groupUiColors.Controls.AddRange(new Control[] { customCb, customEditButton });
        uy += 32;

        var lblFilerColors = new Label { Text = "ファイラー:", Location = new Point(ux, uy + 3), Size = new Size(68, 20), TextAlign = ContentAlignment.MiddleRight };
        lblFilerColors.Text = "UI:";
        var filerBackLbl = new Label { Text = "背景", Location = new Point(ux + 72, uy + 3), Size = new Size(28, 20) };
        var filerBackPreview = CreateColorPreview(ux + 104, uy, TryLoadColor(_settings.Appearance.CustomFilerBackColor, Color.FromArgb(16, 20, 28)));
        var filerForeLbl = new Label { Text = "文字", Location = new Point(ux + 148, uy + 3), Size = new Size(28, 20) };
        var filerForePreview = CreateColorPreview(ux + 180, uy, TryLoadColor(_settings.Appearance.CustomFilerForeColor, Color.Cyan));

        var lblViewerColors = new Label { Text = "ビューア:", Location = new Point(ux + 236, uy + 3), Size = new Size(68, 20), TextAlign = ContentAlignment.MiddleRight };
        var viewerBackLbl = new Label { Text = "背景", Location = new Point(ux + 308, uy + 3), Size = new Size(28, 20) };
        var viewerBackPreview = CreateColorPreview(ux + 340, uy, TryLoadColor(_settings.Appearance.CustomViewerBackColor, Color.FromArgb(0, 0, 64)));
        var viewerForeLbl = new Label { Text = "文字", Location = new Point(ux + 384, uy + 3), Size = new Size(28, 20) };
        var viewerForePreview = CreateColorPreview(ux + 416, uy, TryLoadColor(_settings.Appearance.CustomViewerForeColor, Color.FromArgb(200, 220, 255)));

        groupUiColors.Controls.AddRange(new Control[] {
            lblFilerColors, filerBackLbl, filerBackPreview, filerForeLbl, filerForePreview,
            lblViewerColors, viewerBackLbl, viewerBackPreview, viewerForeLbl, viewerForePreview
        });
        uy += 28;

        var uiColorsNoteLabel = new Label
        {
            Text = "※ ファイル一覧の背景/文字色は上の「一覧配色カスタマイズ」で変更します。",
            Location = new Point(ux, uy),
            Size = new Size(704, 32),
            ForeColor = Color.DimGray
        };
        groupUiColors.Controls.Add(uiColorsNoteLabel);

        customCb.CheckedChanged += (_, _) =>
        {
            if (_suppressColorUiEvents) return;
            SetCustomColorRowEnabled(customCb.Checked);
            UpdatePreview();
        };

        customEditButton.Click += (_, _) => ShowUiThemeColorSettingsDialog();

        filerBackPreview.Click += (_, _) => { if (customCb.Checked) ShowUiThemeColorSettingsDialog(); };
        filerForePreview.Click += (_, _) => { if (customCb.Checked) ShowUiThemeColorSettingsDialog(); };
        viewerBackPreview.Click += (_, _) => { if (customCb.Checked) ShowUiThemeColorSettingsDialog(); };
        viewerForePreview.Click += (_, _) => { if (customCb.Checked) ShowUiThemeColorSettingsDialog(); };

        // groupCustom 内の旧 syncUiThemeCheckBox と UIテーマComboBox は groupUiColors へ移設したため削除

        var fileListColorPreviewPanel = new ListView
        {
            Location = new Point(8, 20),
            Size = new Size(322, 298),
            View = View.Details,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.None,
            OwnerDraw = true,
            MultiSelect = false
        };
        fileListColorPreviewPanel.Columns.Add("Name", 296);

        var previewItems = new[]
        {
            new ListViewItem("normal.txt") { Tag = "normal" },
            new ListViewItem("folder <DIR>") { Tag = "directory" },
            new ListViewItem("readonly.txt") { Tag = "readonly" },
            new ListViewItem("hidden.txt") { Tag = "hidden" },
            new ListViewItem("system.dat") { Tag = "system" },
            new ListViewItem("marked.png") { Tag = "marked" },
            new ListViewItem("selected-file.txt") { Tag = "selected" },
            new ListViewItem("selected-folder <DIR>") { Tag = "selected-directory" }
        };
        fileListColorPreviewPanel.Items.AddRange(previewItems);
        groupPreview.Controls.Add(fileListColorPreviewPanel);

        fileListColorFieldListBox.DrawItem += FileListColorFieldListBox_DrawItem;
        fileListColorFieldListBox.SelectedIndexChanged += (s, e) => UpdateColorTabUiFromModel();

        fileListColorHexTextBox.TextChanged += FileListColorHexTextBox_TextChanged;
        fileListColorRedBox.ValueChanged += FileListColorRgbBox_ValueChanged;
        fileListColorGreenBox.ValueChanged += FileListColorRgbBox_ValueChanged;
        fileListColorBlueBox.ValueChanged += FileListColorRgbBox_ValueChanged;

        fileListColorPickerButton.Click += FileListColorPickerButton_Click;

        btnRegister.Click += RegisterPresetButton_Click;
        deleteColorPresetButton.Click += DeletePresetButton_Click;
        btnReset.Click += ResetPresetButton_Click;

        enableColorAssistCheckBox.CheckedChanged += (s, e) =>
        {
            if (_suppressColorUiEvents) return;
            _settings.Appearance.EnableSemanticColorAssist = enableColorAssistCheckBox.Checked;
            UpdatePreview();
        };

        colorThemeCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_suppressColorUiEvents) return;
            ApplySelectedColorPresetToEditor(forceRefresh: true);
        };

        colorThemeCombo.SelectionChangeCommitted += (_, _) =>
        {
            if (_suppressColorUiEvents) return;
            ApplySelectedColorPresetToEditor(forceRefresh: true);
        };
        colorThemeCombo.DropDownClosed += (_, _) =>
        {
            if (_suppressColorUiEvents) return;
            BeginInvoke(new Action(() =>
            {
                ApplySelectedColorPresetToEditor(forceRefresh: true);
            }));
        };
        fileListColorPreviewPanel.DrawSubItem += PreviewListView_DrawSubItem;
        fileListColorPreviewPanel.SelectedIndexChanged += (s, e) => fileListColorPreviewPanel.Invalidate();

        return new ColorTabResult
        {
            EnableColorAssistCheckBox = enableColorAssistCheckBox,
            ColorThemeCombo = colorThemeCombo,
            DeleteColorPresetButton = deleteColorPresetButton,
            FileListColorFieldListBox = fileListColorFieldListBox,
            FileListColorHexTextBox = fileListColorHexTextBox,
            FileListColorRedBox = fileListColorRedBox,
            FileListColorGreenBox = fileListColorGreenBox,
            FileListColorBlueBox = fileListColorBlueBox,
            FileListColorPickerButton = fileListColorPickerButton,
            FileListColorCurrentPreviewPanel = fileListColorCurrentPreviewPanel,
            FileListColorPreviewPanel = fileListColorPreviewPanel,
            FileListColorWarningLabel = warningLabelLocal,
            CustomUiThemeCheckBox = customCb,
            CustomUiThemeEditButton = customEditButton,
            CustomFilerBackPreview = filerBackPreview,
            CustomFilerForePreview = filerForePreview,
            CustomViewerBackPreview = viewerBackPreview,
            CustomViewerForePreview = viewerForePreview
        };
    }

    private static Panel CreateColorPreview(int x, int y, Color color)
    {
        return new Panel
        {
            Location = new Point(x, y),
            Size = new Size(36, 22),
            BackColor = color,
            BorderStyle = BorderStyle.FixedSingle
        };
    }

    private static Color TryLoadColor(string? hex, Color fallback)
        => UiThemeResolver.TryParseColor(hex) ?? fallback;

    private void InitializeColorTabState()
    {
        _suppressColorUiEvents = true;
        _updatingColorFromUi = true;
        try
        {
            ReloadPresetsCombo(_settings.Appearance.ColorTheme);
            _enableColorAssistCheckBox.Checked = _settings.Appearance.EnableSemanticColorAssist;
            if (_fileListColorFieldListBox.Items.Count > 0)
            {
                _fileListColorFieldListBox.SelectedIndex = 0;
            }

            // カスタムUI色の初期化
            _customUiThemeCheckBox.Checked = _settings.Appearance.CustomUiThemeColorsEnabled;
            bool customEnabled = _settings.Appearance.CustomUiThemeColorsEnabled;
            SetCustomColorRowEnabled(customEnabled);
        }
        finally
        {
            _updatingColorFromUi = false;
            _suppressColorUiEvents = false;
        }

        UpdateDeleteButtonState();
        UpdateColorTabUiFromModel();
        UpdatePreview();
    }

    private void FileListColorFieldListBox_DrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= ColorFieldItems.Length) return;
        e.DrawBackground();

        var item = ColorFieldItems[e.Index];
        Color color = GetCurrentFieldColor(item.PropertyName);

        int boxSize = e.Bounds.Height - 4;
        Rectangle colorBox = new Rectangle(e.Bounds.X + 2, e.Bounds.Y + 2, boxSize, boxSize);
        using (var brush = new SolidBrush(color))
        {
            e.Graphics.FillRectangle(brush, colorBox);
        }
        using (var pen = new Pen(Color.FromArgb(128, 128, 128)))
        {
            e.Graphics.DrawRectangle(pen, colorBox);
        }

        var font = e.Font ?? SystemFonts.DefaultFont;
        var textBrush = (e.State & DrawItemState.Selected) == DrawItemState.Selected
            ? SystemBrushes.HighlightText
            : SystemBrushes.ControlText;

        e.Graphics.DrawString(item.DisplayName, font, textBrush, e.Bounds.X + boxSize + 6, e.Bounds.Y + 2);
        e.DrawFocusRectangle();
    }

    private Color GetCurrentFieldColor(string propertyName)
    {
        string? hex = GetPropertyValue(_settings.Appearance.CustomFileListColors, propertyName);
        if (FileListColorResolver.ParseHexColor(hex) is Color c)
        {
            return c;
        }
        var defaultColors = FileListColorResolver.ResolvePresetColors(_colorThemeCombo.Text, _settings.Appearance.CustomFileListColorPresets);
        return propertyName switch
        {
            nameof(CustomFileListColorSettings.Background) => defaultColors.Background,
            nameof(CustomFileListColorSettings.NormalFile) => defaultColors.NormalFile,
            nameof(CustomFileListColorSettings.Directory) => defaultColors.Directory,
            nameof(CustomFileListColorSettings.ReadOnly) => defaultColors.ReadOnly,
            nameof(CustomFileListColorSettings.Hidden) => defaultColors.Hidden,
            nameof(CustomFileListColorSettings.System) => defaultColors.System,
            nameof(CustomFileListColorSettings.Marked) => defaultColors.Marked,
            nameof(CustomFileListColorSettings.SelectedBackground) => defaultColors.SelectedBackground,
            nameof(CustomFileListColorSettings.SelectedForeground) => defaultColors.SelectedForeground,
            _ => Color.White
        };
    }

    private string? GetPropertyValue(CustomFileListColorSettings target, string propertyName)
    {
        return target.GetType().GetProperty(propertyName)?.GetValue(target) as string;
    }

    private void SetPropertyValue(CustomFileListColorSettings target, string propertyName, string? value)
    {
        target.GetType().GetProperty(propertyName)?.SetValue(target, value);
    }

    private void ReloadPresetsCombo(string? selectPresetKey = null)
    {
        if (_colorThemeCombo == null)
        {
            return;
        }

        _updatingColorFromUi = true;
        _colorThemeCombo.Items.Clear();

        foreach (var key in FileListColorResolver.BuiltInPresetKeys)
        {
            _colorThemeCombo.Items.Add(key);
        }

        foreach (var preset in _settings.Appearance.CustomFileListColorPresets)
        {
            _colorThemeCombo.Items.Add(FileListColorResolver.MakeUserPresetKey(preset.Name));
        }

        string target = selectPresetKey ?? _settings.Appearance.ColorTheme;
        int idx = _colorThemeCombo.FindStringExact(target);
        if (idx >= 0)
        {
            _colorThemeCombo.SelectedIndex = idx;
        }
        else
        {
            _colorThemeCombo.SelectedIndex = 0;
        }
        _updatingColorFromUi = false;
        UpdateDeleteButtonState();
    }

    private void UpdateDeleteButtonState()
    {
        string current = _colorThemeCombo.Text;
        _deleteColorPresetButton.Enabled = FileListColorResolver.TryGetUserPresetName(current, out _);
    }

    private void ApplySelectedColorPresetToEditor(bool forceRefresh = true)
    {
        if (_suppressColorUiEvents || _colorThemeCombo == null) return;

        string selectedPreset = _colorThemeCombo.Text;
        _settings.Appearance.ColorTheme = selectedPreset;
        _fileListCustomColorsEnabledForSave = false;

        var resolved = FileListColorResolver.ResolvePresetColors(selectedPreset, _settings.Appearance.CustomFileListColorPresets);

        _settings.Appearance.CustomFileListColors.Background = FileListColorResolver.ToHexColor(resolved.Background);
        _settings.Appearance.CustomFileListColors.NormalFile = FileListColorResolver.ToHexColor(resolved.NormalFile);
        _settings.Appearance.CustomFileListColors.Directory = FileListColorResolver.ToHexColor(resolved.Directory);
        _settings.Appearance.CustomFileListColors.ReadOnly = FileListColorResolver.ToHexColor(resolved.ReadOnly);
        _settings.Appearance.CustomFileListColors.Hidden = FileListColorResolver.ToHexColor(resolved.Hidden);
        _settings.Appearance.CustomFileListColors.System = FileListColorResolver.ToHexColor(resolved.System);
        _settings.Appearance.CustomFileListColors.Marked = FileListColorResolver.ToHexColor(resolved.Marked);
        _settings.Appearance.CustomFileListColors.SelectedBackground = FileListColorResolver.ToHexColor(resolved.SelectedBackground);
        _settings.Appearance.CustomFileListColors.SelectedForeground = FileListColorResolver.ToHexColor(resolved.SelectedForeground);

        UpdateDeleteButtonState();
        UpdateColorTabUiFromModel();
        UpdatePreview();

        if (forceRefresh)
        {
            ForceRefreshColorTabControls();
        }
    }

    private void ForceRefreshColorTabControls()
    {
        _fileListColorFieldListBox?.Invalidate();
        _fileListColorFieldListBox?.Update();

        _fileListColorCurrentPreviewPanel?.Invalidate();
        _fileListColorCurrentPreviewPanel?.Update();

        _fileListColorPreviewPanel?.Invalidate();
        _fileListColorPreviewPanel?.Update();

        _fileListColorWarningLabel?.Invalidate();
        _fileListColorWarningLabel?.Update();
    }

    private void UpdateColorTabUiFromModel()
    {
        if (_suppressColorUiEvents || _fileListColorFieldListBox == null)
        {
            return;
        }
        if (_fileListColorFieldListBox.SelectedIndex < 0) return;
        var item = ColorFieldItems[_fileListColorFieldListBox.SelectedIndex];
        Color color = GetCurrentFieldColor(item.PropertyName);

        _updatingColorFromUi = true;
        _fileListColorHexTextBox.Text = FileListColorResolver.ToHexColor(color);
        _fileListColorRedBox.Value = color.R;
        _fileListColorGreenBox.Value = color.G;
        _fileListColorBlueBox.Value = color.B;
        _fileListColorCurrentPreviewPanel.BackColor = color;
        _updatingColorFromUi = false;
    }

    private void FileListColorHexTextBox_TextChanged(object? sender, EventArgs e)
    {
        if (_suppressColorUiEvents || _updatingColorFromUi) return;
        if (_fileListColorFieldListBox.SelectedIndex < 0) return;

        string hexText = _fileListColorHexTextBox.Text.Trim();
        Color? color = FileListColorResolver.ParseHexColor(hexText);
        if (color == null) return;

        var item = ColorFieldItems[_fileListColorFieldListBox.SelectedIndex];

        _updatingColorFromUi = true;
        _fileListColorRedBox.Value = color.Value.R;
        _fileListColorGreenBox.Value = color.Value.G;
        _fileListColorBlueBox.Value = color.Value.B;
        _fileListColorCurrentPreviewPanel.BackColor = color.Value;
        _updatingColorFromUi = false;

        SetPropertyValue(_settings.Appearance.CustomFileListColors, item.PropertyName, FileListColorResolver.ToHexColor(color.Value));
        _fileListCustomColorsEnabledForSave = true;
        _fileListColorFieldListBox.Invalidate();
        UpdatePreview();
    }

    private void FileListColorRgbBox_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressColorUiEvents || _updatingColorFromUi) return;
        if (_fileListColorFieldListBox.SelectedIndex < 0) return;

        Color color = Color.FromArgb(
            (int)_fileListColorRedBox.Value,
            (int)_fileListColorGreenBox.Value,
            (int)_fileListColorBlueBox.Value
        );

        var item = ColorFieldItems[_fileListColorFieldListBox.SelectedIndex];

        _updatingColorFromUi = true;
        _fileListColorHexTextBox.Text = FileListColorResolver.ToHexColor(color);
        _fileListColorCurrentPreviewPanel.BackColor = color;
        _updatingColorFromUi = false;

        SetPropertyValue(_settings.Appearance.CustomFileListColors, item.PropertyName, FileListColorResolver.ToHexColor(color));
        _fileListCustomColorsEnabledForSave = true;
        _fileListColorFieldListBox.Invalidate();
        UpdatePreview();
    }

    private void FileListColorPickerButton_Click(object? sender, EventArgs e)
    {
        if (_suppressColorUiEvents)
        {
            return;
        }
        if (_fileListColorFieldListBox.SelectedIndex < 0) return;
        var item = ColorFieldItems[_fileListColorFieldListBox.SelectedIndex];
        Color curColor = GetCurrentFieldColor(item.PropertyName);

        using var cd = new ColorDialog
        {
            Color = curColor,
            FullOpen = true
        };

        if (cd.ShowDialog(this) == DialogResult.OK)
        {
            _updatingColorFromUi = true;
            _fileListColorHexTextBox.Text = FileListColorResolver.ToHexColor(cd.Color);
            _fileListColorRedBox.Value = cd.Color.R;
            _fileListColorGreenBox.Value = cd.Color.G;
            _fileListColorBlueBox.Value = cd.Color.B;
            _fileListColorCurrentPreviewPanel.BackColor = cd.Color;
            _updatingColorFromUi = false;

            SetPropertyValue(_settings.Appearance.CustomFileListColors, item.PropertyName, FileListColorResolver.ToHexColor(cd.Color));
            _fileListCustomColorsEnabledForSave = true;
            _fileListColorFieldListBox.Invalidate();
            UpdatePreview();
        }
    }

    private void RegisterPresetButton_Click(object? sender, EventArgs e)
    {
        if (_suppressColorUiEvents)
        {
            return;
        }

        string currentTheme = _colorThemeCombo.Text;
        string? initialPresetName = null;
        FileListColorResolver.TryGetUserPresetName(currentTheme, out initialPresetName);

        var userPresetNames = _settings.Appearance.CustomFileListColorPresets
            .Select(p => p.Name)
            .ToList();

        using var dlg = new PresetSaveDialog(initialPresetName ?? "", userPresetNames);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        string targetName;
        bool isOverwrite = false;

        if (dlg.IsNewPreset)
        {
            targetName = dlg.NewPresetName;

            if (FileListColorResolver.BuiltInPresetKeys.Contains(targetName, StringComparer.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "組み込みプリセット名は使用できません。別名を指定してください。", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (_settings.Appearance.CustomFileListColorPresets.Any(p => string.Equals(p.Name, targetName, StringComparison.OrdinalIgnoreCase)))
            {
                isOverwrite = true;
            }
        }
        else
        {
            targetName = dlg.SelectedPresetName;
            isOverwrite = true;
        }

        string presetKey = FileListColorResolver.MakeUserPresetKey(targetName);

        if (isOverwrite)
        {
            var confirm = MessageBox.Show(
                this,
                $"既存のユーザープリセット '{targetName}' を上書きしますか？",
                "プリセット上書きの確認",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (confirm != DialogResult.Yes) return;

            var target = _settings.Appearance.CustomFileListColorPresets.FirstOrDefault(p => string.Equals(p.Name, targetName, StringComparison.OrdinalIgnoreCase));
            if (target != null)
            {
                target.Colors = _settings.Appearance.CustomFileListColors.Clone();
            }
            else
            {
                var newPreset = new CustomFileListColorPreset
                {
                    Name = targetName,
                    Colors = _settings.Appearance.CustomFileListColors.Clone()
                };
                _settings.Appearance.CustomFileListColorPresets.Add(newPreset);
            }
        }
        else
        {
            var newPreset = new CustomFileListColorPreset
            {
                Name = targetName,
                Colors = _settings.Appearance.CustomFileListColors.Clone()
            };
            _settings.Appearance.CustomFileListColorPresets.Add(newPreset);
        }

        _settings.Appearance.ColorTheme = presetKey;
        _fileListCustomColorsEnabledForSave = false;

        ReloadPresetsCombo(presetKey);
        ApplySelectedColorPresetToEditor(forceRefresh: true);
    }

    private void DeletePresetButton_Click(object? sender, EventArgs e)
    {
        if (_suppressColorUiEvents)
        {
            return;
        }
        string current = _colorThemeCombo.Text;
        if (!FileListColorResolver.TryGetUserPresetName(current, out string? userName) || userName == null) return;

        var confirm = MessageBox.Show(
            this,
            $"プリセット '{userName}' を削除しますか？",
            "プリセット削除の確認",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (confirm != DialogResult.Yes) return;

        var target = _settings.Appearance.CustomFileListColorPresets.FirstOrDefault(p => string.Equals(p.Name, userName, StringComparison.OrdinalIgnoreCase));
        if (target != null)
        {
            _settings.Appearance.CustomFileListColorPresets.Remove(target);
        }

        string fallbackKey = "ClassicCyan";
        _settings.Appearance.ColorTheme = fallbackKey;
        _fileListCustomColorsEnabledForSave = false;

        ReloadPresetsCombo(fallbackKey);

        var resolved = FileListColorResolver.ResolvePresetColors(fallbackKey, _settings.Appearance.CustomFileListColorPresets);
        _settings.Appearance.CustomFileListColors.Background = FileListColorResolver.ToHexColor(resolved.Background);
        _settings.Appearance.CustomFileListColors.NormalFile = FileListColorResolver.ToHexColor(resolved.NormalFile);
        _settings.Appearance.CustomFileListColors.Directory = FileListColorResolver.ToHexColor(resolved.Directory);
        _settings.Appearance.CustomFileListColors.ReadOnly = FileListColorResolver.ToHexColor(resolved.ReadOnly);
        _settings.Appearance.CustomFileListColors.Hidden = FileListColorResolver.ToHexColor(resolved.Hidden);
        _settings.Appearance.CustomFileListColors.System = FileListColorResolver.ToHexColor(resolved.System);
        _settings.Appearance.CustomFileListColors.Marked = FileListColorResolver.ToHexColor(resolved.Marked);
        _settings.Appearance.CustomFileListColors.SelectedBackground = FileListColorResolver.ToHexColor(resolved.SelectedBackground);
        _settings.Appearance.CustomFileListColors.SelectedForeground = FileListColorResolver.ToHexColor(resolved.SelectedForeground);

        UpdateColorTabUiFromModel();
        UpdatePreview();
    }

    private void ResetPresetButton_Click(object? sender, EventArgs e)
    {
        if (_suppressColorUiEvents)
        {
            return;
        }
        string currentTheme = _colorThemeCombo.Text;
        var resolved = FileListColorResolver.ResolvePresetColors(currentTheme, _settings.Appearance.CustomFileListColorPresets);

        _settings.Appearance.CustomFileListColors.Background = FileListColorResolver.ToHexColor(resolved.Background);
        _settings.Appearance.CustomFileListColors.NormalFile = FileListColorResolver.ToHexColor(resolved.NormalFile);
        _settings.Appearance.CustomFileListColors.Directory = FileListColorResolver.ToHexColor(resolved.Directory);
        _settings.Appearance.CustomFileListColors.ReadOnly = FileListColorResolver.ToHexColor(resolved.ReadOnly);
        _settings.Appearance.CustomFileListColors.Hidden = FileListColorResolver.ToHexColor(resolved.Hidden);
        _settings.Appearance.CustomFileListColors.System = FileListColorResolver.ToHexColor(resolved.System);
        _settings.Appearance.CustomFileListColors.Marked = FileListColorResolver.ToHexColor(resolved.Marked);
        _settings.Appearance.CustomFileListColors.SelectedBackground = FileListColorResolver.ToHexColor(resolved.SelectedBackground);
        _settings.Appearance.CustomFileListColors.SelectedForeground = FileListColorResolver.ToHexColor(resolved.SelectedForeground);
        _fileListCustomColorsEnabledForSave = false;

        UpdateColorTabUiFromModel();
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (_suppressColorUiEvents || _fileListColorPreviewPanel == null || _colorThemeCombo == null)
        {
            return;
        }

        var dummySettings = _settings.Clone();
        dummySettings.Appearance.UseCustomFileListColors = _fileListCustomColorsEnabledForSave;
        dummySettings.Appearance.EnableSemanticColorAssist = _enableColorAssistCheckBox.Checked;
        dummySettings.Appearance.ColorTheme = _colorThemeCombo.Text;

        var resolved = FileListColorResolver.ResolveColors(dummySettings);
        _fileListColorPreviewPanel.BackColor = resolved.Background;
        _fileListColorPreviewPanel.Invalidate();

        UpdateWarningLabel(resolved);
    }

    private void UpdateWarningLabel(FileListColorResolver.ResolvedColors resolved)
    {
        var warningText = "";
        var warningColor = Color.DarkGreen;

        double normalContrast = FileListColorResolver.GetContrastRatio(resolved.NormalFile, resolved.Background);
        double dirContrast = FileListColorResolver.GetContrastRatio(resolved.Directory, resolved.Background);

        if (normalContrast < 2.0)
        {
            warningText = "警告：通常文字色と背景のコントラストが極端に低く、文字が見えません。";
            warningColor = Color.Firebrick;
        }
        else if (dirContrast < 1.8)
        {
            warningText = "警告：ディレクトリ色と背景のコントラストが低く、視認性が悪いです。";
            warningColor = Color.DarkOrange;
        }
        else if (_enableColorAssistCheckBox.Checked)
        {
            warningText = "自動補正が有効です。背景と同化する文字色は自動で補正されます。";
            warningColor = Color.DarkGreen;
        }
        else
        {
            warningText = "コントラスト警告はありません。良好な視認性です。";
            warningColor = Color.DarkGreen;
        }

        if (_fileListColorWarningLabel != null)
        {
            _fileListColorWarningLabel.Text = warningText;
            _fileListColorWarningLabel.ForeColor = warningColor;
        }
    }

    private void PreviewListView_DrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        var item = e.Item;
        if (item == null) return;

        var dummySettings = _settings.Clone();
        dummySettings.Appearance.UseCustomFileListColors = _fileListCustomColorsEnabledForSave;
        dummySettings.Appearance.EnableSemanticColorAssist = _enableColorAssistCheckBox.Checked;
        dummySettings.Appearance.ColorTheme = _colorThemeCombo.Text;

        var resolved = FileListColorResolver.ResolveColors(dummySettings);

        Color backColor = resolved.Background;
        Color foreColor = resolved.NormalFile;

        string? tag = item.Tag as string;
        bool isSelected = item.Selected || tag == "selected" || tag == "selected-directory";
        bool isMarked = tag == "marked";

        if (isSelected)
        {
            backColor = resolved.SelectedBackground;
        }

        switch (tag)
        {
            case "directory": foreColor = resolved.Directory; break;
            case "readonly": foreColor = resolved.ReadOnly; break;
            case "hidden": foreColor = resolved.Hidden; break;
            case "system": foreColor = resolved.System; break;
            case "selected-directory": foreColor = resolved.Directory; break;
        }

        using (var backBrush = new SolidBrush(backColor))
        {
            e.Graphics.FillRectangle(backBrush, e.Bounds);
        }

        TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;

        if (isMarked)
        {
            const int markSlotWidth = 15;
            Rectangle markRect = new Rectangle(e.Bounds.X, e.Bounds.Y, markSlotWidth, e.Bounds.Height);
            Rectangle textBounds = new Rectangle(
                e.Bounds.X + markSlotWidth,
                e.Bounds.Y,
                Math.Max(0, e.Bounds.Width - markSlotWidth),
                e.Bounds.Height);

            Color markColor = resolved.Marked;
            if (isSelected)
            {
                double bgLuminance = FileListColorResolver.GetRelativeLuminance(backColor);
                markColor = bgLuminance > 0.5 ? Color.Black : Color.White;
            }

            TextRenderer.DrawText(e.Graphics, "*", item.Font, markRect, markColor, flags);
            TextRenderer.DrawText(e.Graphics, item.Text, item.Font, textBounds, foreColor, flags);
        }
        else
        {
            Rectangle textBounds = new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, item.Text, item.Font, textBounds, foreColor, flags);
        }
    }

    private sealed class PresetSaveDialog : Form
    {
        private readonly RadioButton _rbNew;
        private readonly RadioButton _rbOverwrite;
        private readonly TextBox _txtNewName;
        private readonly ComboBox _comboUserPresets;

        public bool IsNewPreset => _rbNew.Checked;
        public string NewPresetName => _txtNewName.Text.Trim();
        public string SelectedPresetName
        {
            get
            {
                if (_comboUserPresets.SelectedItem is string item)
                {
                    const string prefix = "ユーザー: ";
                    if (item.StartsWith(prefix))
                    {
                        return item.Substring(prefix.Length);
                    }
                    return item;
                }
                return "";
            }
        }

        public PresetSaveDialog(string initialPresetName, List<string> userPresetNames)
        {
            Text = "配色プリセットの登録";
            ClientSize = new Size(460, 200);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            var grp = new GroupBox
            {
                Text = "保存方法",
                Location = new Point(10, 10),
                Size = new Size(440, 140)
            };

            _rbNew = new RadioButton
            {
                Text = "新しいプリセットとして登録",
                Location = new Point(16, 24),
                AutoSize = true
            };

            _txtNewName = new TextBox
            {
                Location = new Point(36, 48),
                Size = new Size(380, 23)
            };

            _rbOverwrite = new RadioButton
            {
                Text = "既存ユーザープリセットを上書き",
                Location = new Point(16, 76),
                AutoSize = true
            };

            _comboUserPresets = new ComboBox
            {
                Location = new Point(36, 102),
                Size = new Size(380, 23),
                DropDownStyle = ComboBoxStyle.DropDownList
            };

            foreach (var name in userPresetNames)
            {
                _comboUserPresets.Items.Add($"ユーザー: {name}");
            }

            grp.Controls.Add(_rbNew);
            grp.Controls.Add(_txtNewName);
            grp.Controls.Add(_rbOverwrite);
            grp.Controls.Add(_comboUserPresets);

            // ここから初期状態を設定する
            bool canOverwrite = userPresetNames.Count > 0;
            bool selectOverwrite = canOverwrite && userPresetNames.Contains(initialPresetName);

            _rbOverwrite.Enabled = canOverwrite;

            if (selectOverwrite)
            {
                _rbNew.Checked = false;
                _rbOverwrite.Checked = true;
                int idx = _comboUserPresets.FindStringExact($"ユーザー: {initialPresetName}");
                if (idx >= 0)
                {
                    _comboUserPresets.SelectedIndex = idx;
                }
            }
            else
            {
                _rbOverwrite.Checked = false;
                _rbNew.Checked = true;
                if (_comboUserPresets.Items.Count > 0)
                {
                    _comboUserPresets.SelectedIndex = 0;
                }
            }

            _rbNew.CheckedChanged += (s, e) => UpdateEnabledStates();
            _rbOverwrite.CheckedChanged += (s, e) => UpdateEnabledStates();

            var btnOk = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Location = new Point(280, 160),
                Size = new Size(80, 28)
            };

            var btnCancel = new Button
            {
                Text = "キャンセル",
                DialogResult = DialogResult.Cancel,
                Location = new Point(370, 160),
                Size = new Size(80, 28)
            };

            Controls.Add(grp);
            Controls.Add(btnOk);
            Controls.Add(btnCancel);

            AcceptButton = btnOk;
            CancelButton = btnCancel;

            UpdateEnabledStates();

            btnOk.Click += (s, e) =>
            {
                if (IsNewPreset && string.IsNullOrWhiteSpace(NewPresetName))
                {
                    MessageBox.Show(this, "名前を入力してください。", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    DialogResult = DialogResult.None;
                    return;
                }
                if (!IsNewPreset && string.IsNullOrEmpty(SelectedPresetName))
                {
                    MessageBox.Show(this, "上書きするプリセットを選択してください。", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    DialogResult = DialogResult.None;
                    return;
                }
            };
        }

        private void UpdateEnabledStates()
        {
            _txtNewName.Enabled = _rbNew.Checked;
            _comboUserPresets.Enabled = _rbOverwrite.Checked;
        }
    }

    private void SetCustomColorRowEnabled(bool enabled)
    {
        _customUiThemeEditButton.Enabled = enabled;

        _customFilerBackPreview.Enabled = enabled;
        _customFilerForePreview.Enabled = enabled;
        _customViewerBackPreview.Enabled = enabled;
        _customViewerForePreview.Enabled = enabled;
    }

    private void ShowUiThemeColorSettingsDialog()
    {
        using var dialog = new UiThemeColorSettingsDialog(
            GetPreviewColor(_customFilerBackPreview, _settings.Appearance.CustomFilerBackColor),
            GetPreviewColor(_customFilerForePreview, _settings.Appearance.CustomFilerForeColor),
            GetPreviewColor(_customViewerBackPreview, _settings.Appearance.CustomViewerBackColor),
            GetPreviewColor(_customViewerForePreview, _settings.Appearance.CustomViewerForeColor));

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _customFilerBackPreview.BackColor = dialog.FilerBackColor;
        _customFilerForePreview.BackColor = dialog.FilerForeColor;
        _customViewerBackPreview.BackColor = dialog.ViewerBackColor;
        _customViewerForePreview.BackColor = dialog.ViewerForeColor;

        UpdatePreview();
    }

    private sealed class UiThemeColorSettingsDialog : Form
    {
        public Color FilerBackColor { get; private set; }
        public Color FilerForeColor { get; private set; }
        public Color ViewerBackColor { get; private set; }
        public Color ViewerForeColor { get; private set; }

        private readonly Panel _filerBackPreview;
        private readonly TextBox _filerBackHex;
        private readonly NumericUpDown _filerBackR;
        private readonly NumericUpDown _filerBackG;
        private readonly NumericUpDown _filerBackB;

        private readonly Panel _filerForePreview;
        private readonly TextBox _filerForeHex;
        private readonly NumericUpDown _filerForeR;
        private readonly NumericUpDown _filerForeG;
        private readonly NumericUpDown _filerForeB;

        private readonly Panel _viewerBackPreview;
        private readonly TextBox _viewerBackHex;
        private readonly NumericUpDown _viewerBackR;
        private readonly NumericUpDown _viewerBackG;
        private readonly NumericUpDown _viewerBackB;

        private readonly Panel _viewerForePreview;
        private readonly TextBox _viewerForeHex;
        private readonly NumericUpDown _viewerForeR;
        private readonly NumericUpDown _viewerForeG;
        private readonly NumericUpDown _viewerForeB;

        private bool _updating;

        public UiThemeColorSettingsDialog(Color filerBack, Color filerFore, Color viewerBack, Color viewerFore)
        {
            FilerBackColor = filerBack;
            FilerForeColor = filerFore;
            ViewerBackColor = viewerBack;
            ViewerForeColor = viewerFore;

            Text = "UI基調色の調整";
            Size = new Size(712, 260);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            int y = 20;
            int rowH = 34;

            CreateRow("UI背景", y, filerBack, out _filerBackPreview, out _filerBackHex, out _filerBackR, out _filerBackG, out _filerBackB);
            y += rowH;
            CreateRow("UI文字", y, filerFore, out _filerForePreview, out _filerForeHex, out _filerForeR, out _filerForeG, out _filerForeB);
            y += rowH;
            CreateRow("ビューア背景", y, viewerBack, out _viewerBackPreview, out _viewerBackHex, out _viewerBackR, out _viewerBackG, out _viewerBackB);
            y += rowH;
            CreateRow("ビューア文字", y, viewerFore, out _viewerForePreview, out _viewerForeHex, out _viewerForeR, out _viewerForeG, out _viewerForeB);
            y += rowH + 10;

            var btnOk = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Location = new Point(524, y),
                Size = new Size(84, 28)
            };
            btnOk.Click += (s, e) =>
            {
                FilerBackColor = _filerBackPreview.BackColor;
                FilerForeColor = _filerForePreview.BackColor;
                ViewerBackColor = _viewerBackPreview.BackColor;
                ViewerForeColor = _viewerForePreview.BackColor;
            };

            var btnCancel = new Button
            {
                Text = "キャンセル",
                DialogResult = DialogResult.Cancel,
                Location = new Point(616, y),
                Size = new Size(84, 28)
            };

            Controls.Add(btnOk);
            Controls.Add(btnCancel);
            AcceptButton = btnOk;
            CancelButton = btnCancel;
        }

        private void CreateRow(string labelText, int y, Color initColor,
            out Panel preview, out TextBox hexBox,
            out NumericUpDown rBox, out NumericUpDown gBox, out NumericUpDown bBox)
        {
            var lbl = new Label
            {
                Text = labelText,
                Location = new Point(12, y + 4),
                Size = new Size(108, 20),
                TextAlign = ContentAlignment.MiddleLeft
            };
            Controls.Add(lbl);

            preview = new Panel
            {
                Location = new Point(126, y),
                Size = new Size(36, 22),
                BackColor = initColor,
                BorderStyle = BorderStyle.FixedSingle
            };
            Controls.Add(preview);

            var lblHex = new Label
            {
                Text = "HEX",
                Location = new Point(170, y + 4),
                Size = new Size(44, 20),
                TextAlign = ContentAlignment.MiddleRight
            };
            Controls.Add(lblHex);

            hexBox = new TextBox
            {
                Location = new Point(218, y),
                Size = new Size(86, 23),
                Text = UiThemeResolver.ToHexString(initColor)
            };
            Controls.Add(hexBox);

            var lblR = new Label { Text = "R", Location = new Point(320, y + 4), Size = new Size(16, 20), TextAlign = ContentAlignment.MiddleRight };
            rBox = new NumericUpDown { Location = new Point(340, y), Size = new Size(52, 23), Minimum = 0, Maximum = 255, Value = initColor.R };
            Controls.Add(lblR);
            Controls.Add(rBox);

            var lblG = new Label { Text = "G", Location = new Point(402, y + 4), Size = new Size(16, 20), TextAlign = ContentAlignment.MiddleRight };
            gBox = new NumericUpDown { Location = new Point(422, y), Size = new Size(52, 23), Minimum = 0, Maximum = 255, Value = initColor.G };
            Controls.Add(lblG);
            Controls.Add(gBox);

            var lblB = new Label { Text = "B", Location = new Point(484, y + 4), Size = new Size(16, 20), TextAlign = ContentAlignment.MiddleRight };
            bBox = new NumericUpDown { Location = new Point(504, y), Size = new Size(52, 23), Minimum = 0, Maximum = 255, Value = initColor.B };
            Controls.Add(lblB);
            Controls.Add(bBox);

            var btnPick = new Button
            {
                Text = "色選択...",
                Location = new Point(590, y - 1),
                Size = new Size(94, 25)
            };
            Controls.Add(btnPick);

            var localPreview = preview;
            var localHex = hexBox;
            var localR = rBox;
            var localG = gBox;
            var localB = bBox;

            localHex.TextChanged += (s, e) =>
            {
                if (_updating) return;
                string hexText = localHex.Text;
                if (UiThemeResolver.TryParseColor(hexText) is Color color)
                {
                    _updating = true;
                    try
                    {
                        localR.Value = color.R;
                        localG.Value = color.G;
                        localB.Value = color.B;
                        localPreview.BackColor = color;
                    }
                    finally
                    {
                        _updating = false;
                    }
                }
            };

            Action rgbChanged = () =>
            {
                if (_updating) return;
                Color color = Color.FromArgb((int)localR.Value, (int)localG.Value, (int)localB.Value);
                _updating = true;
                try
                {
                    localHex.Text = UiThemeResolver.ToHexString(color);
                    localPreview.BackColor = color;
                }
                finally
                {
                    _updating = false;
                }
            };

            localR.ValueChanged += (s, e) => rgbChanged();
            localG.ValueChanged += (s, e) => rgbChanged();
            localB.ValueChanged += (s, e) => rgbChanged();

            btnPick.Click += (s, e) =>
            {
                using var dlg = new ColorDialog { Color = localPreview.BackColor, FullOpen = true };
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _updating = true;
                    try
                    {
                        Color color = dlg.Color;
                        localPreview.BackColor = color;
                        localHex.Text = UiThemeResolver.ToHexString(color);
                        localR.Value = color.R;
                        localG.Value = color.G;
                        localB.Value = color.B;
                    }
                    finally
                    {
                        _updating = false;
                    }
                }
            };
        }
    }

    private sealed record ColorFieldItem(string DisplayName, string PropertyName);
    private static readonly ColorFieldItem[] ColorFieldItems = new[]
    {
        new ColorFieldItem("背景色", nameof(CustomFileListColorSettings.Background)),
        new ColorFieldItem("通常ファイル文字色", nameof(CustomFileListColorSettings.NormalFile)),
        new ColorFieldItem("ディレクトリ文字色", nameof(CustomFileListColorSettings.Directory)),
        new ColorFieldItem("ReadOnly文字色", nameof(CustomFileListColorSettings.ReadOnly)),
        new ColorFieldItem("Hidden文字色", nameof(CustomFileListColorSettings.Hidden)),
        new ColorFieldItem("System文字色", nameof(CustomFileListColorSettings.System)),
        new ColorFieldItem("マーク記号色", nameof(CustomFileListColorSettings.Marked)),
        new ColorFieldItem("選択行背景色", nameof(CustomFileListColorSettings.SelectedBackground)),
        new ColorFieldItem("選択行文字色", nameof(CustomFileListColorSettings.SelectedForeground))
    };
}
