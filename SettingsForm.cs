using System.Drawing.Text;
using MidFD.Configuration;
using MidFD.Services;
using MidFD.Dialogs;
using MidFD.Commands;
using MidFD.Models;

namespace MidFD;

/// <summary>
/// 設定フォーム。既存設定を壊さず、OK 押下時のみ AppSettings を保存する。
/// </summary>
public class SettingsForm : Form
{
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
    private readonly CheckBox _showBrowserToolbarCheckBox;
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
    private readonly CheckBox _clipboardPasteTextAsFileCheckBox;
    private CheckBox _enableDragArchiveHandoffCheckBox = null!;
    private CheckBox _includeDragZipManifestCheckBox = null!;
    private readonly CheckBox _restoreLastPathCheckBox;
    private readonly CheckBox _restoreTabsOnStartupCheckBox;
    private readonly CheckBox _restoreWindowBoundsCheckBox;
    private readonly CheckBox _restoreColumnCountCheckBox;
    private readonly CheckBox _restoreSortCheckBox;
    private readonly CheckBox _enableMouseGesturesCheckBox;
    private readonly CheckBox _enableWorkspaceSnapshotCheckBox;
    private readonly InputAssignmentDialog _embeddedInputAssignmentView;
    private readonly CheckBox _enableLogCheckBox;
    private readonly CheckBox _enableDetailedLogCheckBox;
    private readonly ToolTip _statusToolTip = new();
    private readonly CommandRegistry _commandRegistry = new();
    private Dictionary<string, string> _mouseGestureCommandMapDraft;
    private Dictionary<string, List<string>> _browserKeyCommandOverridesDraft;
    private Dictionary<string, string?> _functionBarCommandOverridesStandardDraft;
    private Dictionary<string, string?> _functionBarCommandOverridesFdCompatibleDraft;
    private Dictionary<string, string?> _functionBarCommandOverridesShiftStandardDraft = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string?> _functionBarCommandOverridesShiftFdCompatibleDraft = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string?> _functionBarCommandOverridesCtrlStandardDraft = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string?> _functionBarCommandOverridesCtrlFdCompatibleDraft = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string?> _functionBarCommandOverridesAltStandardDraft = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string?> _functionBarCommandOverridesAltFdCompatibleDraft = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, FunctionBarLabelOverride> _functionBarLabelOverridesStandardDraft;
    private Dictionary<string, FunctionBarLabelOverride> _functionBarLabelOverridesFdCompatibleDraft;
    private Dictionary<string, FunctionBarLabelOverride> _functionBarLabelOverridesShiftStandardDraft;
    private Dictionary<string, FunctionBarLabelOverride> _functionBarLabelOverridesShiftFdCompatibleDraft;
    private Dictionary<string, FunctionBarLabelOverride> _functionBarLabelOverridesCtrlStandardDraft = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, FunctionBarLabelOverride> _functionBarLabelOverridesCtrlFdCompatibleDraft = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, FunctionBarLabelOverride> _functionBarLabelOverridesAltStandardDraft = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, FunctionBarLabelOverride> _functionBarLabelOverridesAltFdCompatibleDraft = new(StringComparer.OrdinalIgnoreCase);
    private CheckBox? _showFunctionBarTooltipsCheckBox;

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
        Display = 0,
        Color = 1,
        Operation = 2,
        InputAssignment = 3,
        External = 4,
        StartupAndLog = 5
    }

    public event EventHandler? SettingsApplied;

    public SettingsForm(AppSettings settings, FeatureProfile effectiveProfile, InitialTab initialTab = InitialTab.Display)
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
        _settings.Input.MouseGestureCommandMap ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _settings.Input.BrowserKeyCommandOverrides ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        InputSettings.NormalizeAndMigrateFunctionKeyChords(_settings.Input);
        _settings.Input.FunctionBarCommandOverridesStandard ??= new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarCommandOverridesFdCompatible ??= new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarCommandOverridesShiftStandard ??= new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarCommandOverridesShiftFdCompatible ??= new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarCommandOverridesCtrlStandard ??= new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarCommandOverridesCtrlFdCompatible ??= new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarCommandOverridesAltStandard ??= new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarCommandOverridesAltFdCompatible ??= new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarLabelOverridesStandard ??= new Dictionary<string, FunctionBarLabelOverride>(StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarLabelOverridesFdCompatible ??= new Dictionary<string, FunctionBarLabelOverride>(StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarLabelOverridesShiftStandard ??= new Dictionary<string, FunctionBarLabelOverride>(StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarLabelOverridesShiftFdCompatible ??= new Dictionary<string, FunctionBarLabelOverride>(StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarLabelOverridesCtrlStandard ??= new Dictionary<string, FunctionBarLabelOverride>(StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarLabelOverridesCtrlFdCompatible ??= new Dictionary<string, FunctionBarLabelOverride>(StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarLabelOverridesAltStandard ??= new Dictionary<string, FunctionBarLabelOverride>(StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarLabelOverridesAltFdCompatible ??= new Dictionary<string, FunctionBarLabelOverride>(StringComparer.OrdinalIgnoreCase);

        _mouseGestureCommandMapDraft = InputSettings.NormalizeMouseGestureCommandMap(_settings.Input.MouseGestureCommandMap);
        _browserKeyCommandOverridesDraft = InputSettings.NormalizeBrowserKeyCommandOverrides(_settings.Input.BrowserKeyCommandOverrides);
        _functionBarCommandOverridesStandardDraft = new Dictionary<string, string?>(_settings.Input.FunctionBarCommandOverridesStandard, StringComparer.OrdinalIgnoreCase);
        _functionBarCommandOverridesFdCompatibleDraft = new Dictionary<string, string?>(_settings.Input.FunctionBarCommandOverridesFdCompatible, StringComparer.OrdinalIgnoreCase);
        _functionBarLabelOverridesStandardDraft = _settings.Input.FunctionBarLabelOverridesStandard.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        _functionBarLabelOverridesFdCompatibleDraft = _settings.Input.FunctionBarLabelOverridesFdCompatible.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        _functionBarLabelOverridesShiftStandardDraft = _settings.Input.FunctionBarLabelOverridesShiftStandard.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        _functionBarLabelOverridesShiftFdCompatibleDraft = _settings.Input.FunctionBarLabelOverridesShiftFdCompatible.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        _functionBarLabelOverridesCtrlStandardDraft = _settings.Input.FunctionBarLabelOverridesCtrlStandard.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        _functionBarLabelOverridesCtrlFdCompatibleDraft = _settings.Input.FunctionBarLabelOverridesCtrlFdCompatible.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        _functionBarLabelOverridesAltStandardDraft = _settings.Input.FunctionBarLabelOverridesAltStandard.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        _functionBarLabelOverridesAltFdCompatibleDraft = _settings.Input.FunctionBarLabelOverridesAltFdCompatible.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);
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
        ClientSize = new Size(1040, 720);

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill
        };

        var tabDisplay = CreateTab("表示");
        var tabColor = CreateTab("配色");
        var tabOperation = CreateTab("操作");
        var tabInputAssignment = CreateTab("入力割り当て");
        var tabExternal = CreateTab("外部連携");
        var tabStartupAndLog = CreateTab("起動・ログ");

        tabs.TabPages.AddRange(new[]
        {
            tabDisplay,
            tabColor,
            tabOperation,
            tabInputAssignment,
            tabExternal,
            tabStartupAndLog
        });

        var bottomPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 52
        };

        Controls.Add(tabs);
        Controls.Add(bottomPanel);
        tabs.SelectedIndex = Math.Clamp((int)initialTab, 0, tabs.TabPages.Count - 1);
        Shown += (_, _) =>
        {
            tabs.SelectedIndex = Math.Clamp((int)initialTab, 0, tabs.TabPages.Count - 1);
        };

        string[] fonts = GetInstalledFontNames();
        string[] dateFormats = { "yyyy-MM-dd HH:mm", "yyyy/MM/dd HH:mm:ss", "yyyy-MM-dd(ddd) HH:mm" };
        string[] sizeFormats = { "HumanReadable", "Bytes", "KB/MB" };

        tabDisplay.AutoScroll = false;
        tabColor.AutoScroll = false;

        (_filerFontCombo, _filerFontSizeBox, _showBrowserTabCategoryRowCheckBox, _showExtensionsCheckBox, _showDirectoryMarkerCheckBox, _showHiddenFilesCheckBox, _showItemIconsCheckBox, _useUnderlineCursorCheckBox, _showBrowserToolbarCheckBox, _fileDisplayModeCombo, _dateFormatCombo, _sizeFormatCombo,
         _viewerFontCombo, _viewerFontSizeBox, _viewerWordWrapCheckBox, _reuseImageViewerCheckBox, _closeImageViewerOnNonImageCheckBox, _rememberImageViewerBoundsCheckBox)
            = BuildDisplayAndPreviewTab(tabDisplay, fonts, dateFormats, sizeFormats);

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

        (_confirmDeleteCheckBox, _confirmPermanentDeleteCheckBox, _useMidFdManagedTrashCheckBox, _reloadAfterFileOperationCheckBox, _selectCreatedItemCheckBox, _clipboardPasteTextAsFileCheckBox,
         _enableMouseGesturesCheckBox, _enableWorkspaceSnapshotCheckBox, _restoreLastPathCheckBox)
            = BuildOperationAndInputTab(tabOperation);

        _embeddedInputAssignmentView = BuildInputAssignmentTab(tabInputAssignment);

        tabStartupAndLog.AutoScroll = false;

        (_restoreTabsOnStartupCheckBox, _restoreWindowBoundsCheckBox, _restoreColumnCountCheckBox, _restoreSortCheckBox)
            = BuildLaunchAndRestoreTab(tabStartupAndLog);

        (_sevenZipPathBox, _diffPathBox, _editorPathBox, _videoPlaybackVolumeCombo, _videoStillPreviewFfmpegPathBox, _videoEnterPlaysExternalCheckBox, _sevenZipStatusLabel, _diffStatusLabel, _editorStatusLabel, _videoStillPreviewFfmpegStatusLabel, _videoStillPreviewEnabledCheckBox, _videoSkipSecondsCombo)
            = BuildExternalTab(tabExternal);

        (_enableLogCheckBox, _enableDetailedLogCheckBox) = BuildLogTab(tabStartupAndLog);

        InitializeColorTabState();
        _suppressColorUiEvents = false;

        _videoStillPreviewFfmpegPathBox.TextChanged += (_, _) => RefreshExternalStatus();
        RefreshExternalStatus();

        var btnOk = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Size = new Size(80, 32),
            Location = new Point(bottomPanel.Width - 176, 10),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        btnOk.Click += BtnOk_Click;

        var btnCancel = new Button
        {
            Text = "キャンセル",
            DialogResult = DialogResult.Cancel,
            Size = new Size(80, 32),
            Location = new Point(bottomPanel.Width - 88, 10),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };

        var btnApply = new Button
        {
            Text = "適用",
            Size = new Size(80, 32),
            Location = new Point(bottomPanel.Width - 264, 10),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        btnApply.Click += BtnApply_Click;

        bottomPanel.Controls.Add(btnOk);
        bottomPanel.Controls.Add(btnCancel);
        bottomPanel.Controls.Add(btnApply);

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

    private (ComboBox filerFont, NumericUpDown filerSize, CheckBox showBrowserTabCategoryRow, CheckBox showExtensions, CheckBox showDirectoryMarker, CheckBox showHiddenFiles, CheckBox showItemIcons, CheckBox useUnderlineCursor, CheckBox showBrowserToolbar, ComboBox fileDisplayMode, ComboBox dateFormat, ComboBox sizeFormat,
             ComboBox viewerFont, NumericUpDown viewerSize, CheckBox viewerWordWrap, CheckBox reuseImageViewer, CheckBox closeOnNonImage, CheckBox rememberBounds)
        BuildDisplayAndPreviewTab(TabPage tab, string[] fonts, string[] dateFormats, string[] sizeFormats)
    {
        // Layout Constants
        int lblW = 100;
        int inpX = 110;
        int sizeX = 308;
        int checkX = 32;
        int rowH = 28;
        int topY = 22;

        // --- Left: List Display ---
        var groupList = new GroupBox { Text = "一覧表示", Location = new Point(8, 6), Size = new Size(500, 414) };
        tab.Controls.Add(groupList);

        int top = topY;

        AddLabel(groupList, "フォント:", top, lblW);
        var filerFont = AddComboBox(groupList, inpX, top, 190, fonts, _settings.Fonts.FileListFontFamily);
        var filerSize = AddNumericUpDown(groupList, sizeX, top, 60, (decimal)_settings.Fonts.FileListFontSize);
        top += rowH + 8;

        // チェックボックス群（1列配置）
        int checkY = top;
        var showBrowserTabCategoryRow = AddCheckBox(groupList, "上段 of category tab to display", 16, checkY, _settings.Appearance.ShowBrowserTabCategoryRow);
        showBrowserTabCategoryRow.Text = "上段のカテゴリタブを表示する";
        checkY += 24;
        var showHiddenFiles = AddCheckBox(groupList, "隠しファイルを表示する", 16, checkY, _settings.Appearance.ShowHiddenFiles);
        checkY += 24;
        var showExtensions = AddCheckBox(groupList, "拡張子を表示する", 16, checkY, _settings.Appearance.ShowExtensions);
        checkY += 24;
        var showItemIcons = AddCheckBox(groupList, "一覧にアイコンを表示する", 16, checkY, _settings.Appearance.ShowItemIcons);
        checkY += 24;
        var showDirectoryMarker = AddCheckBox(groupList, "ディレクトリに <DIR> を表示", 16, checkY, _settings.Appearance.ShowDirectoryMarker);
        checkY += 24;
        var useUnderlineCursor = AddCheckBox(groupList, "カーソル行をアンダーライン表示", 16, checkY, _settings.Appearance.UseUnderlineCursor);
        checkY += 24;
        var showBrowserToolbar = AddCheckBox(groupList, "ナビゲーションボタンを表示する", 16, checkY, _settings.Appearance.ShowBrowserToolbar);
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

        AddHintLabel(groupList, 16, top, 460, "※ 配色は「配色」タブで設定します。");

        // --- Right Top: Viewer ---
        var groupViewer = new GroupBox { Text = "ビューア", Location = new Point(520, 6), Size = new Size(500, 180) };
        tab.Controls.Add(groupViewer);

        top = topY;
        AddLabel(groupViewer, "Viewer フォント:", top, 110);
        var viewerFont = AddComboBox(groupViewer, 120, top, 178, fonts, _settings.Fonts.ViewerFontFamily);
        var viewerSize = AddNumericUpDown(groupViewer, sizeX, top, 60, (decimal)_settings.Fonts.ViewerFontSize);

        var viewerFontSample = new TextBox
        {
            Text = "貴社の記者が汽車で帰社した。\r\nAaあぁアァ亜宇 0123456789 ()[]{}<>\r\nYesterday all my troubles seemed so far away.",
            Location = new Point(16, top + 40),
            Size = new Size(460,90),
            Multiline = true,
            ScrollBars = ScrollBars.Vertical
        };
        groupViewer.Controls.Add(viewerFontSample);

        viewerFont.DrawMode = DrawMode.OwnerDrawFixed;
        viewerFont.ItemHeight = 20;
        viewerFont.DrawItem += (s, e) =>
        {
            if (e.Index < 0) return;
            e.DrawBackground();
            string fontName = viewerFont.Items[e.Index]?.ToString() ?? "";
            Font fontToDraw;
            try
            {
                fontToDraw = new Font(fontName, 10);
            }
            catch
            {
                fontToDraw = e.Font ?? viewerFont.Font;
            }

            using (fontToDraw)
            {
                using var brush = new SolidBrush(e.ForeColor);
                var textBounds = new Rectangle(e.Bounds.X + 2, e.Bounds.Y, e.Bounds.Width - 2, e.Bounds.Height);
                e.Graphics.DrawString(fontName, fontToDraw, brush, textBounds);
            }
            e.DrawFocusRectangle();
        };

        var updateSample = new EventHandler((_, _) =>
        {
            try { viewerFontSample.Font = new Font(viewerFont.Text, (float)viewerSize.Value); } catch { }
        });
        viewerFont.SelectedIndexChanged += updateSample;
        viewerFont.TextChanged += updateSample;
        viewerSize.ValueChanged += updateSample;
        updateSample(null!, EventArgs.Empty);

        top += 140;

        var viewerWordWrap = AddCheckBox(groupViewer, "折り返しを既定で ON にする", checkX, top, _settings.Preview.ViewerWordWrap);
        top += rowH;
        var reuseImageViewer = AddCheckBox(groupViewer, "画像ビューアを再利用する", checkX, top, _settings.Preview.ReuseImageViewer);
        top += rowH;
        var closeOnNonImage = AddCheckBox(groupViewer, "非画像時にビューアを閉じる", checkX, top, _settings.Preview.CloseImageViewerOnNonImageSelection);
        top += rowH;
        var rememberBounds = AddCheckBox(groupViewer, "ビューアの位置/サイズを記憶する", checkX, top, _settings.Preview.RememberImageViewerBounds);
        groupViewer.Height = rememberBounds.Bottom + 16;

        return (filerFont, filerSize, showBrowserTabCategoryRow, showExtensions, showDirectoryMarker, showHiddenFiles, showItemIcons, useUnderlineCursor, showBrowserToolbar, fileDisplayMode, dateFormat, sizeFormat,
                viewerFont, viewerSize, viewerWordWrap, reuseImageViewer, closeOnNonImage, rememberBounds);
    }

    private (CheckBox confirmDelete, CheckBox confirmPermanentDelete, CheckBox useMidFdManagedTrash, CheckBox reloadAfterFileOperation, CheckBox selectCreatedItem, CheckBox clipboardPasteTextAsFile,
             CheckBox enableMouseGestures, CheckBox enableWorkspaceSnapshot, CheckBox restoreLastPath)
        BuildOperationAndInputTab(TabPage tab)
    {
        int rowH = 28;

        // --- Left: File Operation ---
        var groupFile = new GroupBox { Text = "ファイル操作", Location = new Point(8, 6), Size = new Size(490, 480) };
        tab.Controls.Add(groupFile);

        int top = 28;
        var confirmDelete = AddCheckBox(groupFile, "削除前に確認する", 16, top, _settings.FileOperations.ConfirmDelete);
        top += rowH;
        var confirmPermanentDelete = AddCheckBox(groupFile, "Shift+Delete 前に確認する", 16, top, _settings.FileOperations.ConfirmPermanentDelete);
        top += rowH;
        var useMidFdManagedTrash = AddCheckBox(groupFile, "削除時に MidFD管理ゴミ箱を使う", 16, top, _settings.FileOperations.UseMidFdManagedTrash);
        top += rowH;
        Label managedTrashHint = AddWrappedHintLabel(groupFile, 32, top, 430, "ON: Ctrl+Z による復元が可能になります。\n環境に応じ SQLite / JSON を自動選択します。");
        top = managedTrashHint.Bottom + 10;

        var reloadAfterFileOperation = AddCheckBox(groupFile, "操作後に一覧を再読込する", 16, top, _settings.FileOperations.ReloadAfterFileOperation);
        top += rowH;
        var selectCreatedItem = AddCheckBox(groupFile, "新規作成後に自動選択する", 16, top, _settings.FileOperations.SelectCreatedItemAfterCreate);
        top += rowH;
        var clipboardPasteTextAsFile = AddCheckBox(groupFile, "テキストクリップボードをファイルとして貼り付ける", 16, top, _settings.FileOperations.ClipboardPasteTextAsFileEnabled);
        top += rowH - 4;
        Label clipboardHint = AddWrappedHintLabel(groupFile, 32, top, 430, "Ctrl+Vで .txt ファイルを作成します。\n誤作成防止のため通常はOFF推奨です。");
        top = clipboardHint.Bottom + 18;

        var sectionDragZip = new Label
        {
            Text = "Drag ZIP",
            Location = new Point(16, top),
            Size = new Size(220, 20),
            Font = new Font(Font, FontStyle.Bold)
        };
        groupFile.Controls.Add(sectionDragZip);
        top += 24;

        _enableDragArchiveHandoffCheckBox = AddCheckBox(groupFile, "Shift/Ctrl+ドラッグでマーク対象をZIP化して渡す", 16, top, _settings.FileOperations.EnableDragArchiveHandoff);
        _enableDragArchiveHandoffCheckBox.CheckedChanged += (_, _) => UpdateDragArchiveManifestCheckboxEnabledState();
        top += rowH - 4;
        Label dragHint = AddWrappedHintLabel(
            groupFile,
            32,
            top,
            430,
            "ChatGPT等へ複数ソースをまとめて渡す場合に便利です。通常のドラッグ操作と挙動が変わります。");
        top = dragHint.Bottom + 8;

        _includeDragZipManifestCheckBox = AddCheckBox(groupFile, "ZIPに内容一覧manifestを同梱する", 32, top, _settings.FileOperations.IncludeDragZipManifest);
        top += rowH - 4;
        AddWrappedHintLabel(
            groupFile,
            48,
            top,
            414,
            "対象ファイル一覧を確認しやすくします。\nローカルパス情報を含む場合があります。");
        UpdateDragArchiveManifestCheckboxEnabledState();

        // --- Right: Operation/Input Information Architecture ---
        int rightX = 506;
        int rightW = 490;
        int rightTop = 6;

        var groupAdvanced = new GroupBox { Text = "高度な使い方 / 詳細オプション", Location = new Point(rightX, rightTop), Size = new Size(rightW, 260) };
        tab.Controls.Add(groupAdvanced);
        int advancedTop = 24;
        var enableMouseGestures = AddCheckBox(groupAdvanced, "マウスジェスチャーを使う", 16, advancedTop, _settings.Input.EnableMouseGestures);
        var mouseHint = AddWrappedHintLabel(groupAdvanced, 32, advancedTop + 24, 444, "右ドラッグで戻る/進む等の操作を行います。");
        advancedTop = mouseHint.Bottom + 8;

        _showFunctionBarTooltipsCheckBox = AddCheckBox(groupAdvanced, "Functionバーの詳細説明を表示する", 16, advancedTop, _settings.Input.ShowFunctionBarTooltips);
        var tooltipHint = AddWrappedHintLabel(groupAdvanced, 32, advancedTop + 24, 444, "Functionバーのマウスオーバー時に説明とキーヒントを表示します。");
        advancedTop = tooltipHint.Bottom + 8;

        var restoreLastPath = AddCheckBox(groupAdvanced, "前回フォルダを復元する", 16, advancedTop, _settings.Session.RestoreLastPath);
        var restoreLastPathHint = AddWrappedHintLabel(groupAdvanced, 32, advancedTop + 24, 444, "起動時に前回見ていたフォルダへ戻ります。");
        advancedTop = restoreLastPathHint.Bottom + 8;

        var enableWorkspaceSnapshot = AddCheckBox(groupAdvanced, "Workspace Snapshot / 作業状態復元を使う", 16, advancedTop, IsAdvancedWorkspaceFeaturesEnabled(_settings.Profile));
        var workspaceHint = AddWrappedHintLabel(groupAdvanced, 32, advancedTop + 24, 444, "作業状態の復元と拡張管理導線を有効にします。\n復元内容は「起動・ログ」で調整します。");
        advancedTop = workspaceHint.Bottom + 8;

        return (confirmDelete, confirmPermanentDelete, useMidFdManagedTrash, reloadAfterFileOperation, selectCreatedItem, clipboardPasteTextAsFile, enableMouseGestures, enableWorkspaceSnapshot, restoreLastPath);
    }

    private (CheckBox restoreTabsOnStartup, CheckBox restoreWindowBounds, CheckBox restoreColumnCount, CheckBox restoreSort)
        BuildLaunchAndRestoreTab(TabPage tab)
    {
        int rowH = 32;

        // --- Left: Startup ---
        var groupStartup = new GroupBox { Text = "起動時の復元", Location = new Point(8, 6), Size = new Size(490, 320) };
        tab.Controls.Add(groupStartup);

        int top = 28;
        var hint1 = AddWrappedHintLabel(groupStartup, 16, top, 460, "Workspace Snapshot / 作業状態復元の利用有無は「操作」タブの高度な使い方から切り替えます。\nここでは、起動時にどこまで復元するかを設定します。");
        top = hint1.Bottom + 8;
        var restoreTabsOnStartup = AddCheckBox(groupStartup, "前回の作業状態(タブ等)を復元する", 16, top, _settings.Session.RestoreTabsOnStartup);
        top += rowH + 8;

        var hint2 = AddWrappedHintLabel(groupStartup, 16, top, 460, "作業状態復元が ON の場合、カテゴリ、タブ構成、タブごとのマーク、固定状態等を復元します。");
        top = hint2.Bottom + 16;
        var btnOpenFirstSetup = new Button
        {
            Text = "初回セットアップを開く...",
            Location = new Point(16, top),
            Size = new Size(180, 32)
        };
        btnOpenFirstSetup.Click += (_, _) => OpenFirstLaunchSetupDialog();
        groupStartup.Controls.Add(btnOpenFirstSetup);
        top += rowH + 4;
        var hint3 = AddWrappedHintLabel(groupStartup, 16, top, 460, "初回オプション、Fキー配置、動画Enter動作、外部連携の基本設定を再設定できます。\n初期化ではありません。");
        groupStartup.Height = hint3.Bottom + 16;

        // --- Left Bottom: Display State ---
        var groupDisplay = new GroupBox { Text = "表示状態の復元", Location = new Point(8, groupStartup.Bottom + 12), Size = new Size(490, 150) };
        tab.Controls.Add(groupDisplay);

        top = 28;
        var restoreWindowBounds = AddCheckBox(groupDisplay, "ウィンドウ位置/サイズを復元する", 16, top, _settings.Session.RestoreWindowBounds);
        top += rowH;
        var restoreColumnCount = AddCheckBox(groupDisplay, "前回の列数を復元する", 16, top, _settings.Session.RestoreColumnCount);
        top += rowH;
        var restoreSort = AddCheckBox(groupDisplay, "前回のソートを復元する", 16, top, _settings.Session.RestoreSort);

        return (restoreTabsOnStartup, restoreWindowBounds, restoreColumnCount, restoreSort);
    }

    private InputAssignmentDialog BuildInputAssignmentTab(TabPage tab)
    {
        var embedded = new InputAssignmentDialog(_settings.Input, _commandRegistry)
        {
            TopLevel = false,
            FormBorderStyle = FormBorderStyle.None,
            Dock = DockStyle.Fill
        };

        tab.Controls.Add(embedded);
        embedded.Show();
        return embedded;
    }

    private void SyncInputAssignmentDraftFromEmbeddedView()
    {
        if (_embeddedInputAssignmentView == null)
        {
            return;
        }

        InputSettings result = _embeddedInputAssignmentView.ResultSettings;
        _settings.Input.BrowserKeyCommandOverrides = InputSettings.NormalizeBrowserKeyCommandOverrides(result.BrowserKeyCommandOverrides);
        _settings.Input.MouseGestureCommandMap = InputSettings.NormalizeMouseGestureCommandMap(result.MouseGestureCommandMap);
        _settings.Input.FunctionBarCommandOverridesStandard = new Dictionary<string, string?>(result.FunctionBarCommandOverridesStandard, StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarCommandOverridesFdCompatible = new Dictionary<string, string?>(result.FunctionBarCommandOverridesFdCompatible, StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarCommandOverridesShiftStandard = new Dictionary<string, string?>(result.FunctionBarCommandOverridesShiftStandard, StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarCommandOverridesShiftFdCompatible = new Dictionary<string, string?>(result.FunctionBarCommandOverridesShiftFdCompatible, StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarCommandOverridesCtrlStandard = new Dictionary<string, string?>(result.FunctionBarCommandOverridesCtrlStandard, StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarCommandOverridesCtrlFdCompatible = new Dictionary<string, string?>(result.FunctionBarCommandOverridesCtrlFdCompatible, StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarCommandOverridesAltStandard = new Dictionary<string, string?>(result.FunctionBarCommandOverridesAltStandard, StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarCommandOverridesAltFdCompatible = new Dictionary<string, string?>(result.FunctionBarCommandOverridesAltFdCompatible, StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarLabelOverridesStandard = result.FunctionBarLabelOverridesStandard.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarLabelOverridesFdCompatible = result.FunctionBarLabelOverridesFdCompatible.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarLabelOverridesShiftStandard = result.FunctionBarLabelOverridesShiftStandard.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarLabelOverridesShiftFdCompatible = result.FunctionBarLabelOverridesShiftFdCompatible.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarLabelOverridesCtrlStandard = result.FunctionBarLabelOverridesCtrlStandard.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarLabelOverridesCtrlFdCompatible = result.FunctionBarLabelOverridesCtrlFdCompatible.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarLabelOverridesAltStandard = result.FunctionBarLabelOverridesAltStandard.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarLabelOverridesAltFdCompatible = result.FunctionBarLabelOverridesAltFdCompatible.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);

        InputSettings.NormalizeAndMigrateFunctionKeyChords(_settings.Input);

        _browserKeyCommandOverridesDraft = InputSettings.NormalizeBrowserKeyCommandOverrides(_settings.Input.BrowserKeyCommandOverrides);
        _mouseGestureCommandMapDraft = InputSettings.NormalizeMouseGestureCommandMap(_settings.Input.MouseGestureCommandMap);
        _functionBarCommandOverridesStandardDraft = new Dictionary<string, string?>(_settings.Input.FunctionBarCommandOverridesStandard, StringComparer.OrdinalIgnoreCase);
        _functionBarCommandOverridesFdCompatibleDraft = new Dictionary<string, string?>(_settings.Input.FunctionBarCommandOverridesFdCompatible, StringComparer.OrdinalIgnoreCase);
        _functionBarCommandOverridesShiftStandardDraft = new Dictionary<string, string?>(_settings.Input.FunctionBarCommandOverridesShiftStandard, StringComparer.OrdinalIgnoreCase);
        _functionBarCommandOverridesShiftFdCompatibleDraft = new Dictionary<string, string?>(_settings.Input.FunctionBarCommandOverridesShiftFdCompatible, StringComparer.OrdinalIgnoreCase);
        _functionBarCommandOverridesCtrlStandardDraft = new Dictionary<string, string?>(_settings.Input.FunctionBarCommandOverridesCtrlStandard, StringComparer.OrdinalIgnoreCase);
        _functionBarCommandOverridesCtrlFdCompatibleDraft = new Dictionary<string, string?>(_settings.Input.FunctionBarCommandOverridesCtrlFdCompatible, StringComparer.OrdinalIgnoreCase);
        _functionBarCommandOverridesAltStandardDraft = new Dictionary<string, string?>(_settings.Input.FunctionBarCommandOverridesAltStandard, StringComparer.OrdinalIgnoreCase);
        _functionBarCommandOverridesAltFdCompatibleDraft = new Dictionary<string, string?>(_settings.Input.FunctionBarCommandOverridesAltFdCompatible, StringComparer.OrdinalIgnoreCase);

        _functionBarLabelOverridesStandardDraft = _settings.Input.FunctionBarLabelOverridesStandard.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        _functionBarLabelOverridesFdCompatibleDraft = _settings.Input.FunctionBarLabelOverridesFdCompatible.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        _functionBarLabelOverridesShiftStandardDraft = _settings.Input.FunctionBarLabelOverridesShiftStandard.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        _functionBarLabelOverridesShiftFdCompatibleDraft = _settings.Input.FunctionBarLabelOverridesShiftFdCompatible.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        _functionBarLabelOverridesCtrlStandardDraft = _settings.Input.FunctionBarLabelOverridesCtrlStandard.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        _functionBarLabelOverridesCtrlFdCompatibleDraft = _settings.Input.FunctionBarLabelOverridesCtrlFdCompatible.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        _functionBarLabelOverridesAltStandardDraft = _settings.Input.FunctionBarLabelOverridesAltStandard.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        _functionBarLabelOverridesAltFdCompatibleDraft = _settings.Input.FunctionBarLabelOverridesAltFdCompatible.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);
    }

    private (TextBox sevenZip, TextBox diff, TextBox editor, ComboBox videoPlaybackVolume, TextBox videoStillPreviewFfmpegPath, CheckBox videoEnterPlaysExternal, Label sevenZipStatus, Label diffStatus, Label editorStatus, Label videoStillPreviewFfmpegStatus, CheckBox videoStillPreviewEnabled, ComboBox videoSkipSeconds)
        BuildExternalTab(TabPage tab)
    {
        int labelWidth = 124;
        int baseX = labelWidth + 12;
        int rowH = 64;
        int textBoxWidth = 280;
        int browseButtonWidth = 56;
        int browseButtonX = baseX + textBoxWidth + 8;

        // --- Left: Archive / Diff / Editor ---
        var groupArchive = new GroupBox { Text = "外部アプリケーション", Location = new Point(8, 6), Size = new Size(500, 396) };
        tab.Controls.Add(groupArchive);

        int top = 28;
        AddLabel(groupArchive, "7-Zip パス:", top, labelWidth);
        var sevenZip = AddTextBox(groupArchive, baseX, top, textBoxWidth, _settings.SevenZip.ExePath ?? "");
        AddBrowseButton(groupArchive, browseButtonX, top - 1, browseButtonWidth, sevenZip);
        var sevenZipStatus = AddStatusLabel(groupArchive, baseX, top + 26, 360);
        top += rowH;

        AddLabel(groupArchive, "外部 Diff:", top, labelWidth);
        var diff = AddTextBox(groupArchive, baseX, top, textBoxWidth, _settings.ExternalTools.ExternalDiffPath ?? "");
        AddBrowseButton(groupArchive, browseButtonX, top - 1, browseButtonWidth, diff);
        var diffStatus = AddStatusLabel(groupArchive, baseX, top + 26, 360);
        top += rowH;

        AddLabel(groupArchive, "外部エディタ:", top, labelWidth);
        var editor = AddTextBox(groupArchive, baseX, top, textBoxWidth, _settings.ExternalTools.ExternalEditorPath ?? "");
        AddBrowseButton(groupArchive, browseButtonX, top - 1, browseButtonWidth, editor);
        var editorStatus = AddStatusLabel(groupArchive, baseX, top + 26, 360);
        top += rowH + 8;

        AddWrappedHintLabel(groupArchive, 16, top, 460, "E キーで選択ファイルをこのエディタで開きます。\n未設定時は notepad.exe を使用します。");

        // --- Right: External Tools / Video ---
        var groupTools = new GroupBox { Text = "外部ツール管理", Location = new Point(516, 6), Size = new Size(500, 188) };
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

        AddWrappedHintLabel(groupTools, 16, top, 460, "コマンドパレット (Ctrl+Shift+P) や Alt+英数字の外部ツール namespace で起動するツールを管理します。Alt+F1〜F12 の Function layer とは別です。");

        var groupVideoTools = new GroupBox { Text = "動画静止画プレビュー / 外部ツール", Location = new Point(516, 200), Size = new Size(500, 276) };
        tab.Controls.Add(groupVideoTools);
        top = 28;

        var videoStillPreviewEnabled = AddCheckBox(groupVideoTools, "静止画プレビューを有効にする", 16, top, _settings.Preview.VideoStillPreviewEnabled);
        top += 28;
        AddLabel(groupVideoTools, "初期位置(秒):", top, labelWidth);
        var videoSkipSeconds = AddEditableComboBox(groupVideoTools, baseX, top, 100, new[] { "0", "5", "10", "30", "60" }, _settings.Preview.VideoSkipSeconds.ToString());
        top += 34;

        AddLabel(groupVideoTools, "動画ツールフォルダ:", top, labelWidth);
        var videoStillPreviewFfmpegPath = AddTextBox(groupVideoTools, baseX, top, textBoxWidth, _settings.Preview.VideoToolDirectory ?? "");
        AddBrowseFolderButton(groupVideoTools, browseButtonX, top - 1, browseButtonWidth, videoStillPreviewFfmpegPath);

        var videoStillPreviewFfmpegStatus = new Label
        {
            Location = new Point(16, top + 32),
            Size = new Size(460, 40),
            ForeColor = SystemColors.GrayText,
            TextAlign = ContentAlignment.TopLeft
        };
        groupVideoTools.Controls.Add(videoStillPreviewFfmpegStatus);

        top += 70;
        AddLabel(groupVideoTools, "ffplay音量(%):", top, labelWidth);
        var videoPlaybackVolume = AddEditableComboBox(groupVideoTools, baseX, top, 100, new[] { "0", "30", "50", "70", "100" }, _settings.Preview.VideoPlaybackVolumePercent.ToString());
        top += 30;
        var videoEnterPlaysExternal = AddCheckBox(groupVideoTools, "動画 Enter で外部再生する (Ctrl+Enterでプレビュー)", 16, top, _settings.Preview.VideoEnterPlaysExternal);
        top += 26;
        AddHintLabel(groupVideoTools, 16, top, 460, "※ 静止画: ffmpeg / 再生: ffplay / 長さ: ffprobe", 54);

        sevenZip.TextChanged += (_, _) => RefreshExternalStatus();
        diff.TextChanged += (_, _) => RefreshExternalStatus();
        editor.TextChanged += (_, _) => RefreshExternalStatus();
        videoStillPreviewFfmpegPath.TextChanged += (_, _) => RefreshExternalStatus();

        return (sevenZip, diff, editor, videoPlaybackVolume, videoStillPreviewFfmpegPath, videoEnterPlaysExternal, sevenZipStatus, diffStatus, editorStatus, videoStillPreviewFfmpegStatus, videoStillPreviewEnabled, videoSkipSeconds);
    }

    private (CheckBox enableLog, CheckBox enableDetail) BuildLogTab(TabPage tab)
    {
        // --- Right: Log settings ---
        var groupLog = new GroupBox { Text = "ログ設定", Location = new Point(506, 6), Size = new Size(490, 160) };
        tab.Controls.Add(groupLog);

        int top = 28;
        int rowH = 32;
        var enableLog = AddCheckBox(groupLog, "ログ出力を有効化", 16, top, _settings.Logging.IsEnabled);
        top += rowH;
        var enableDetail = AddCheckBox(groupLog, "詳細ログを有効化（調査用）", 16, top, _settings.Logging.IsDetailedEnabled);
        top += rowH + 8;

        var hint = AddWrappedHintLabel(groupLog, 16, top, 460, "通常はOFF推奨です。問題調査時のみONにしてください。\nログはサイズローテーションされ、古いログは自動削除されます。");
        groupLog.Height = hint.Bottom + 16;

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

    private Label AddWrappedHintLabel(Control parent, int x, int y, int width, string text)
    {
        var label = new Label
        {
            Text = text,
            Location = new Point(x, y),
            MaximumSize = new Size(width, 0),
            AutoSize = true,
            ForeColor = SystemColors.GrayText
        };
        parent.Controls.Add(label);
        return label;
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

    private sealed record MouseGestureItem(string GestureId, string DisplayName);

    private static readonly MouseGestureItem[] MouseGestureItems =
    {
        new("L", "左"),
        new("R", "右"),
        new("U", "上"),
        new("D", "下"),
        new("LR", "左右"),
        new("LU", "左上"),
        new("LD", "左下"),
        new("RL", "右左"),
        new("RU", "右上"),
        new("RD", "右下"),
        new("UL", "上左"),
        new("UR", "上右"),
        new("UD", "上下"),
        new("DL", "下左"),
        new("DR", "下右"),
        new("DU", "下上")
    };

    private void OpenMouseGestureSettingsDialog()
    {
        var workingMap = new Dictionary<string, string>(_mouseGestureCommandMapDraft, StringComparer.OrdinalIgnoreCase);
        var assignableCommands = GetOrderedMouseGestureAssignableCommands();
        var comboBindings = new List<(string GestureId, ComboBox Combo)>();

        using var dialog = new Form
        {
            Text = "マウスジェスチャー割り当て",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ClientSize = new Size(560, 500)
        };

        var panel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 420,
            AutoScroll = true
        };
        dialog.Controls.Add(panel);

        int top = 16;
        foreach (MouseGestureItem item in MouseGestureItems)
        {
            string gestureId = item.GestureId;
            var label = new Label
            {
                Text = item.DisplayName,
                Location = new Point(20, top + 4),
                AutoSize = true
            };
            panel.Controls.Add(label);

            var combo = new ComboBox
            {
                Location = new Point(120, top),
                Size = new Size(400, 24),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Tag = item.GestureId
            };
            combo.Items.Add(new CommandOption(InputSettings.MouseGestureUnassignedCommandId, "無効"));
            foreach (CommandDefinition definition in assignableCommands)
            {
                combo.Items.Add(new CommandOption(definition.Id, definition.DisplayName));
            }
            string selectedCommandId = ResolveGestureCommandIdForSettings(gestureId, workingMap);
            SelectCommandOption(combo, selectedCommandId);
            int selectedIndexBeforeDropDown = combo.SelectedIndex;
            combo.DropDown += (_, _) =>
            {
                selectedIndexBeforeDropDown = combo.SelectedIndex;
            };
            combo.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Escape && combo.DroppedDown)
                {
                    if (selectedIndexBeforeDropDown >= 0 &&
                        selectedIndexBeforeDropDown < combo.Items.Count)
                    {
                        combo.SelectedIndex = selectedIndexBeforeDropDown;
                    }

                    combo.DroppedDown = false;
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };
            combo.MouseWheel += (_, e) =>
            {
                if (!combo.DroppedDown && e is HandledMouseEventArgs handled)
                {
                    handled.Handled = true;
                }
            };
            panel.Controls.Add(combo);
            comboBindings.Add((gestureId, combo));

            top += 34;
        }

        var resetButton = new Button
        {
            Text = "既定に戻す",
            Location = new Point(12, 454),
            Size = new Size(120, 32)
        };
        resetButton.Click += (_, _) =>
        {
            foreach (MouseGestureItem item in MouseGestureItems)
            {
                string defaultCommandId = InputSettings.DefaultMouseGestureCommandMap.TryGetValue(item.GestureId, out string? commandId)
                    ? commandId
                    : InputSettings.MouseGestureUnassignedCommandId;
                workingMap[item.GestureId] = defaultCommandId;
            }

            foreach ((string gestureId, ComboBox combo) in comboBindings)
            {
                string selectedCommandId = ResolveGestureCommandIdForSettings(gestureId, workingMap);
                SelectCommandOption(combo, selectedCommandId);
            }
        };
        dialog.Controls.Add(resetButton);

        var okButton = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(376, 454),
            Size = new Size(80, 32)
        };
        dialog.Controls.Add(okButton);

        var cancelButton = new Button
        {
            Text = "キャンセル",
            DialogResult = DialogResult.Cancel,
            Location = new Point(466, 454),
            Size = new Size(80, 32)
        };
        dialog.Controls.Add(cancelButton);

        dialog.AcceptButton = okButton;
        dialog.CancelButton = cancelButton;

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            foreach ((string gestureId, ComboBox combo) in comboBindings)
            {
                if (combo.SelectedItem is CommandOption option)
                {
                    workingMap[gestureId] = option.Id;
                }
            }

            _mouseGestureCommandMapDraft = new Dictionary<string, string>(workingMap, StringComparer.OrdinalIgnoreCase);
        }
    }

    private void OpenBrowserKeyBindingSettingsDialog()
    {
        string profileValue = _embeddedInputAssignmentView.SelectedProfileValue;
        IReadOnlyDictionary<string, IReadOnlyList<string>> defaults = InputSettings.GetDefaultBrowserKeyCommandMap(profileValue);
        var commands = GetOrderedBrowserKeyBindingCommands();
        var workingOverrides = InputSettings.NormalizeBrowserKeyCommandOverrides(_browserKeyCommandOverridesDraft);
        var gestureOptions = CreateBrowserKeyGestureOptions(defaults, workingOverrides);
        var comboBindings = new List<(CommandDefinition Command, ComboBox Combo)>();

        using var dialog = new Form
        {
            Text = "キーバインド割り当て",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ClientSize = new Size(740, 560)
        };

        var panel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 470,
            AutoScroll = true
        };
        dialog.Controls.Add(panel);

        int top = 16;
        foreach (CommandDefinition command in commands)
        {
            var label = new Label
            {
                Text = command.DisplayName,
                Location = new Point(20, top + 4),
                Size = new Size(280, 24)
            };
            panel.Controls.Add(label);

            var combo = new ComboBox
            {
                Location = new Point(320, top),
                Size = new Size(390, 24),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            foreach (string gesture in gestureOptions)
            {
                combo.Items.Add(new KeyGestureOption(gesture));
            }

            string selectedGesture = ResolveCommandGestureForSettings(command.Id, defaults, workingOverrides);
            SelectKeyGestureOption(combo, selectedGesture);
            panel.Controls.Add(combo);
            comboBindings.Add((command, combo));
            top += 34;
        }

        var resetAllButton = new Button
        {
            Text = "すべて既定に戻す",
            Location = new Point(12, 514),
            Size = new Size(150, 32)
        };
        resetAllButton.Click += (_, _) =>
        {
            workingOverrides.Clear();
            foreach ((CommandDefinition command, ComboBox combo) in comboBindings)
            {
                string selectedGesture = ResolveCommandGestureForSettings(command.Id, defaults, workingOverrides);
                SelectKeyGestureOption(combo, selectedGesture);
            }
        };
        dialog.Controls.Add(resetAllButton);

        var okButton = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(556, 514),
            Size = new Size(80, 32)
        };
        dialog.Controls.Add(okButton);

        var cancelButton = new Button
        {
            Text = "キャンセル",
            DialogResult = DialogResult.Cancel,
            Location = new Point(646, 514),
            Size = new Size(80, 32)
        };
        dialog.Controls.Add(cancelButton);

        dialog.AcceptButton = okButton;
        dialog.CancelButton = cancelButton;

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var nextOverrides = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var keyToCommand = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var conflicts = new List<string>();

        foreach ((CommandDefinition command, ComboBox combo) in comboBindings)
        {
            if (combo.SelectedItem is not KeyGestureOption selected)
            {
                continue;
            }

            string selectedGesture = InputSettings.NormalizeKeyGestureText(selected.Gesture);
            if (string.IsNullOrWhiteSpace(selectedGesture))
            {
                continue;
            }

            string defaultGesture = ResolvePrimaryGesture(defaults.TryGetValue(command.Id, out IReadOnlyList<string>? values) ? values : Array.Empty<string>());
            if (!string.Equals(selectedGesture, InputSettings.MouseGestureUnassignedCommandId, StringComparison.OrdinalIgnoreCase))
            {
                if (keyToCommand.TryGetValue(selectedGesture, out string? conflictCommandId))
                {
                    string conflictName = _commandRegistry.Find(conflictCommandId)?.DisplayName ?? conflictCommandId;
                    conflicts.Add($"{selectedGesture}: {conflictName} / {command.DisplayName}");
                }
                else
                {
                    keyToCommand[selectedGesture] = command.Id;
                }
            }

            List<string> existingGestures = workingOverrides.TryGetValue(command.Id, out List<string>? existingRaw)
                ? InputSettings.NormalizeBrowserKeyGestures(existingRaw)
                : new List<string>();

            if (string.Equals(selectedGesture, defaultGesture, StringComparison.OrdinalIgnoreCase) && existingGestures.Count == 0)
            {
                continue;
            }

            if (string.Equals(selectedGesture, InputSettings.MouseGestureUnassignedCommandId, StringComparison.OrdinalIgnoreCase))
            {
                nextOverrides[command.Id] = new List<string>();
                continue;
            }

            var merged = new List<string> { selectedGesture };
            foreach (string gesture in existingGestures)
            {
                if (!merged.Contains(gesture, StringComparer.OrdinalIgnoreCase) &&
                    !string.Equals(gesture, InputSettings.MouseGestureUnassignedCommandId, StringComparison.OrdinalIgnoreCase))
                {
                    merged.Add(gesture);
                }
            }

            nextOverrides[command.Id] = merged;
        }

        if (conflicts.Count > 0)
        {
            MessageBox.Show(
                this,
                "キー割り当てが競合しています。修正してください。\r\n\r\n" + string.Join("\r\n", conflicts),
                "キーバインド競合",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        _browserKeyCommandOverridesDraft = nextOverrides;
    }

    private IReadOnlyList<CommandDefinition> GetOrderedBrowserKeyBindingCommands()
    {
        string profileValue = _embeddedInputAssignmentView.SelectedProfileValue;
        IReadOnlyDictionary<string, IReadOnlyList<string>> defaults = InputSettings.GetDefaultBrowserKeyCommandMap(profileValue);
        var overrides = InputSettings.NormalizeBrowserKeyCommandOverrides(_settings.Input?.BrowserKeyCommandOverrides);
        var commands = _commandRegistry
            .GetAll()
            .Where(static c =>
                (c.Scope == CommandScope.Browser || c.Scope == CommandScope.Global) &&
                c.IsCustomizable &&
                !c.IsDangerous)
            .Where(c => defaults.ContainsKey(c.Id) || overrides.ContainsKey(c.Id))
            .ToArray();

        var preferredOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [CommandIds.BrowserNavigateParent] = 10,
            [CommandIds.BrowserNavigateBack] = 11,
            [CommandIds.BrowserNavigateForward] = 12,
            [CommandIds.BrowserReload] = 20,
            [CommandIds.BrowserOpenExplorer] = 30,
            [CommandIds.BrowserOpenShell] = 31,
            [CommandIds.BrowserOpenExternalEditor] = 32,
            [CommandIds.BrowserCopyFullPath] = 40,
            [CommandIds.ClipboardPaste] = 41,
            [CommandIds.BrowserTabNew] = 50,
            [CommandIds.BrowserTabClose] = 51,
            [CommandIds.BrowserTabRestoreClosed] = 52,
            [CommandIds.BrowserTabCategoryPrevious] = 53,
            [CommandIds.BrowserTabCategoryNext] = 54,
            [CommandIds.BrowserQuickAccess] = 60,
            [CommandIds.BrowserTree] = 61,
            [CommandIds.BrowserFilter] = 62,
            [CommandIds.BrowserSort] = 63,
            [CommandIds.BrowserFilter] = 64,
            [CommandIds.BrowserLogdisk] = 65,
            [CommandIds.ArchiveUnpack] = 66,
            [CommandIds.BrowserMarkAllFiles] = 67,
            [CommandIds.BrowserMarkAllItems] = 68,
            [CommandIds.BrowserOpenMarkSlot] = 69,
            [CommandIds.AppOpenCommandLauncher] = 70,
            [CommandIds.AppOpenCommandList] = 71,
            [CommandIds.BrowserShowHelp] = 72,
            [CommandIds.AppOpenSettings] = 80
        };

        return commands
            .OrderBy(c => preferredOrder.TryGetValue(c.Id, out int order) ? order : 999)
            .ThenBy(c => c.DisplayName, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> CreateBrowserKeyGestureOptions(
        IReadOnlyDictionary<string, IReadOnlyList<string>> defaults,
        IReadOnlyDictionary<string, List<string>> overrides)
    {
        var gestures = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            InputSettings.MouseGestureUnassignedCommandId
        };

        foreach (IReadOnlyList<string> values in defaults.Values)
        {
            foreach (string value in values)
            {
                string normalized = InputSettings.NormalizeKeyGestureText(value);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    gestures.Add(normalized);
                }
            }
        }

        foreach (List<string> values in overrides.Values)
        {
            foreach (string value in values)
            {
                string normalized = InputSettings.NormalizeKeyGestureText(value);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    gestures.Add(normalized);
                }
            }
        }

        string[] extras =
        {
            "Ctrl+R", "Ctrl+F", "Ctrl+T", "Ctrl+W",
            "Alt+Left", "Alt+Right", "Alt+F2",
            "Ctrl+Shift+C", "Ctrl+V", "Back",
            "Shift+F6", "Alt+F5", "Ctrl+Shift+Left", "Ctrl+Shift+Right",
            "Ctrl+Shift+P", "Ctrl+Shift+L", "Ctrl+H", "Ctrl+M", "E", "S", "F", "T", "H", "Q", "L", "U"
        };

        foreach (string extra in extras)
        {
            gestures.Add(InputSettings.NormalizeKeyGestureText(extra));
        }

        return gestures
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .OrderBy(static x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ResolveCommandGestureForSettings(
        string commandId,
        IReadOnlyDictionary<string, IReadOnlyList<string>> defaults,
        IReadOnlyDictionary<string, List<string>> overrides)
    {
        if (overrides.TryGetValue(commandId, out List<string>? configured))
        {
            List<string> normalizedConfigured = InputSettings.NormalizeBrowserKeyGestures(configured);
            if (normalizedConfigured.Count == 0)
            {
                return InputSettings.MouseGestureUnassignedCommandId;
            }

            return ResolvePrimaryGesture(normalizedConfigured);
        }

        if (defaults.TryGetValue(commandId, out IReadOnlyList<string>? defaultGestures))
        {
            return ResolvePrimaryGesture(defaultGestures);
        }

        return InputSettings.MouseGestureUnassignedCommandId;
    }

    private static string ResolvePrimaryGesture(IEnumerable<string> gestures)
    {
        foreach (string gesture in gestures)
        {
            string normalized = InputSettings.NormalizeKeyGestureText(gesture);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }
        }

        return InputSettings.MouseGestureUnassignedCommandId;
    }

    private static void SelectKeyGestureOption(ComboBox combo, string gesture)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is KeyGestureOption option &&
                string.Equals(option.Gesture, gesture, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }

        combo.SelectedIndex = 0;
    }

    private static void SelectCommandOption(ComboBox combo, string commandId)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is CommandOption option && string.Equals(option.Id, commandId, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedIndex = i;
                return;
            }
        }

        combo.SelectedIndex = 0;
    }

    private string ResolveGestureCommandIdForSettings(string gestureId, IReadOnlyDictionary<string, string> map)
    {
        if (map.TryGetValue(gestureId, out string? configured) && !string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        if (InputSettings.DefaultMouseGestureCommandMap.TryGetValue(gestureId, out string? defaultCommandId))
        {
            return defaultCommandId;
        }

        return InputSettings.MouseGestureUnassignedCommandId;
    }

    private IReadOnlyList<CommandDefinition> GetOrderedMouseGestureAssignableCommands()
    {
        var commands = _commandRegistry.GetMouseGestureAssignableCommands();
        var preferredOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [CommandIds.BrowserNavigateParent] = 10,
            [CommandIds.BrowserNavigateBack] = 11,
            [CommandIds.BrowserNavigateForward] = 12,
            [CommandIds.BrowserReload] = 20,
            [CommandIds.BrowserOpenExplorer] = 30,
            [CommandIds.BrowserOpenShell] = 31,
            [CommandIds.BrowserCopyFullPath] = 40,
            [CommandIds.BrowserTabNew] = 50,
            [CommandIds.BrowserTabNext] = 51,
            [CommandIds.BrowserTabPrevious] = 52,
            [CommandIds.BrowserTabClose] = 53,
            [CommandIds.BrowserTabRestoreClosed] = 54,
            [CommandIds.BrowserTabCategoryNext] = 55,
            [CommandIds.BrowserTabCategoryPrevious] = 56,
            [CommandIds.BrowserQuickAccess] = 60,
            [CommandIds.BrowserTree] = 61,
            [CommandIds.BrowserFilter] = 62,
            [CommandIds.BrowserSort] = 63,
            [CommandIds.BrowserFilter] = 64,
            [CommandIds.BrowserLogdisk] = 65,
            [CommandIds.ArchiveUnpack] = 66,
            [CommandIds.BrowserMarkAllFiles] = 67,
            [CommandIds.BrowserMarkAllItems] = 68,
            [CommandIds.BrowserOpenMarkSlot] = 69,
            [CommandIds.BrowserOpenExternalEditor] = 70,
            [CommandIds.ClipboardPaste] = 71,
            [CommandIds.AppOpenSettings] = 80,
            [CommandIds.AppOpenCommandLauncher] = 81
        };

        return commands
            .OrderBy(c => preferredOrder.TryGetValue(c.Id, out int order) ? order : 999)
            .ThenBy(c => c.DisplayName, StringComparer.Ordinal)
            .ToArray();
    }

    private sealed class CommandOption
    {
        public string Id { get; }
        public string DisplayName { get; }

        public CommandOption(string id, string displayName)
        {
            Id = id;
            DisplayName = displayName;
        }

        public override string ToString() => DisplayName;
    }

    private sealed class KeyGestureOption
    {
        public string Gesture { get; }

        public KeyGestureOption(string gesture)
        {
            Gesture = gesture;
        }

        public override string ToString()
        {
            return string.Equals(Gesture, InputSettings.MouseGestureUnassignedCommandId, StringComparison.OrdinalIgnoreCase)
                ? "無効"
                : Gesture;
        }
    }

    private void SaveCurrentSettings()
    {
        SyncInputAssignmentDraftFromEmbeddedView();

        _settings.Profile = ToFeatureProfileSettingValue(_enableWorkspaceSnapshotCheckBox.Checked);
        _settings.Input.FunctionKeyProfile = _embeddedInputAssignmentView.SelectedProfileValue;
        _settings.Input.EnableMouseGestures = _enableMouseGesturesCheckBox.Checked;
        _settings.Input.MouseGestureCommandMap = InputSettings.NormalizeMouseGestureCommandMap(_mouseGestureCommandMapDraft);
        _settings.Input.BrowserKeyCommandOverrides = InputSettings.NormalizeBrowserKeyCommandOverrides(_browserKeyCommandOverridesDraft);
        _settings.Input.FunctionBarCommandOverridesStandard = new Dictionary<string, string?>(_functionBarCommandOverridesStandardDraft, StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarCommandOverridesFdCompatible = new Dictionary<string, string?>(_functionBarCommandOverridesFdCompatibleDraft, StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarCommandOverridesShiftStandard = new Dictionary<string, string?>(_functionBarCommandOverridesShiftStandardDraft, StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarCommandOverridesShiftFdCompatible = new Dictionary<string, string?>(_functionBarCommandOverridesShiftFdCompatibleDraft, StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarCommandOverridesCtrlStandard = new Dictionary<string, string?>(_functionBarCommandOverridesCtrlStandardDraft, StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarCommandOverridesCtrlFdCompatible = new Dictionary<string, string?>(_functionBarCommandOverridesCtrlFdCompatibleDraft, StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarCommandOverridesAltStandard = new Dictionary<string, string?>(_functionBarCommandOverridesAltStandardDraft, StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarCommandOverridesAltFdCompatible = new Dictionary<string, string?>(_functionBarCommandOverridesAltFdCompatibleDraft, StringComparer.OrdinalIgnoreCase);
        InputSettings.NormalizeAndMigrateFunctionKeyChords(_settings.Input);
        _settings.Input.FunctionBarLabelOverridesStandard = _functionBarLabelOverridesStandardDraft.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarLabelOverridesFdCompatible = _functionBarLabelOverridesFdCompatibleDraft.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarLabelOverridesShiftStandard = _functionBarLabelOverridesShiftStandardDraft.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarLabelOverridesShiftFdCompatible = _functionBarLabelOverridesShiftFdCompatibleDraft.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarLabelOverridesCtrlStandard = _functionBarLabelOverridesCtrlStandardDraft.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarLabelOverridesCtrlFdCompatible = _functionBarLabelOverridesCtrlFdCompatibleDraft.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarLabelOverridesAltStandard = _functionBarLabelOverridesAltStandardDraft.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        _settings.Input.FunctionBarLabelOverridesAltFdCompatible = _functionBarLabelOverridesAltFdCompatibleDraft.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        _settings.Input.ShowFunctionBarTooltips = _showFunctionBarTooltipsCheckBox?.Checked ?? _settings.Input.ShowFunctionBarTooltips;
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
        _settings.Appearance.ShowBrowserToolbar = _showBrowserToolbarCheckBox.Checked;
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
        _settings.FileOperations.ClipboardPasteTextAsFileEnabled = _clipboardPasteTextAsFileCheckBox.Checked;
        _settings.FileOperations.EnableDragArchiveHandoff = _enableDragArchiveHandoffCheckBox.Checked;
        _settings.FileOperations.IncludeDragZipManifest = _includeDragZipManifestCheckBox.Checked;

        _settings.Session.RestoreLastPath = _restoreLastPathCheckBox.Checked;
        _settings.Session.RestoreTabsOnStartup = _restoreTabsOnStartupCheckBox.Checked;
        _settings.Session.RestoreWindowBounds = _restoreWindowBoundsCheckBox.Checked;
        _settings.Session.RestoreColumnCount = _restoreColumnCountCheckBox.Checked;
        _settings.Session.RestoreSort = _restoreSortCheckBox.Checked;

        _settings.Logging.IsEnabled = _enableLogCheckBox.Checked;
        _settings.Logging.IsDetailedEnabled = _enableDetailedLogCheckBox.Checked;

        SettingsManager.Save(_settings);
    }

    private void UpdateDragArchiveManifestCheckboxEnabledState()
    {
        if (_includeDragZipManifestCheckBox != null)
        {
            _includeDragZipManifestCheckBox.Enabled = _enableDragArchiveHandoffCheckBox?.Checked ?? false;
        }
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
        setupSettings.Profile = ToFeatureProfileSettingValue(_enableWorkspaceSnapshotCheckBox.Checked);
        setupSettings.Input.FunctionKeyProfile = _embeddedInputAssignmentView.SelectedProfileValue;
        setupSettings.Preview.VideoEnterPlaysExternal = _videoEnterPlaysExternalCheckBox.Checked;
        setupSettings.SevenZip.ExePath = NullIfEmpty(_sevenZipPathBox.Text);
        setupSettings.Preview.VideoToolDirectory = NullIfEmpty(_videoStillPreviewFfmpegPathBox.Text);
        setupSettings.ExternalTools.ExternalEditorPath = NullIfEmpty(_editorPathBox.Text);

        using var dialog = new FeatureProfileSelectionDialog(setupSettings);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _enableWorkspaceSnapshotCheckBox.Checked = dialog.EnableWorkspaceSnapshotFeatures;
        _embeddedInputAssignmentView.SelectedProfileValue = dialog.UseFdCompatibleFunctionKeys ? InputSettings.FdCompatibleProfileValue : InputSettings.StandardProfileValue;
        _videoEnterPlaysExternalCheckBox.Checked = dialog.VideoEnterPlaysExternal;
        _enableMouseGesturesCheckBox.Checked = dialog.EnableMouseGestures;
        _showFunctionBarTooltipsCheckBox!.Checked = dialog.ShowFunctionBarTooltips;
        _enableDragArchiveHandoffCheckBox.Checked = dialog.EnableDragArchiveHandoff;
        _includeDragZipManifestCheckBox.Checked = dialog.IncludeDragZipManifest;
        _restoreLastPathCheckBox.Checked = dialog.RestoreLastPath;
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



    private static (string Text, Color Color, string ToolTipText) GetVideoToolStatus(string pathText)
    {
        string? path = NullIfEmpty(pathText);
        VideoToolResolutionResult resolution = VideoToolResolutionService.Resolve(path);

        string ffmpegSummary = resolution.FfmpegFound ? "ffmpeg OK" : "ffmpeg 未検出";
        string ffplaySummary = resolution.FfplayFound ? "ffplay OK" : "ffplay 未検出";
        string ffprobeSummary = resolution.FfprobeFound ? "ffprobe OK" : "ffprobe 未検出";
        string text;
        Color color;

        text = $"状態: {ffmpegSummary} {ffplaySummary} {ffprobeSummary}";
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

    private static bool IsAdvancedWorkspaceFeaturesEnabled(string? settingValue)
    {
        return FeatureProfileService.TryResolveProfile(settingValue, out FeatureProfile parsed) && parsed == FeatureProfile.Full;
    }

    private static string ToFeatureProfileSettingValue(bool enabled)
    {
        return enabled
            ? FeatureProfile.Full.ToString()
            : FeatureProfile.PracticalStable.ToString();
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
        var groupCustom = new GroupBox { Text = "一覧配色カスタマイズ", Location = new Point(8, 6), Size = new Size(500, 330) };
        var groupPreview = new GroupBox { Text = "プレビュー", Location = new Point(520, 6), Size = new Size(500, 330) };
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
            Size = new Size(1002, 150)
        };
        tab.Controls.Add(groupUiColors);

        int ux = 12;
        int uy = 20;

        var syncModeNote = new Label
        {
            Text = "通常は表示色に合わせます。個別に変えたい場合だけ手動指定してください。",
            Location = new Point(ux, uy + 2),
            Size = new Size(970, 20),
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
            Size = new Size(970, 32),
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
            Location = new Point(10, 20),
            Size = new Size(480, 298),
            View = View.Details,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.None,
            OwnerDraw = true,
            MultiSelect = false
        };
        fileListColorPreviewPanel.Columns.Add("Name", 476);

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
