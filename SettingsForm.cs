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
    private const string FontPreviewSampleText =
        "貴社の記者が汽車で帰社した。\r\n" +
        "Aaあぁアァ亜宇 0123456789 ()[]{}<>\r\n" +
        "Yesterday all my troubles seemed so far away.";

    private readonly AppSettings _settings;

    private readonly TextBox _sevenZipPathBox;
    private readonly TextBox _diffPathBox;
    private readonly TextBox _editorPathBox;
    private readonly Label _sevenZipStatusLabel;
    private readonly Label _diffStatusLabel;
    private readonly Label _editorStatusLabel;
    private readonly ComboBox _filerFontCombo;
    private readonly NumericUpDown _filerFontSizeBox;
    private readonly NumericUpDown _browserTabFontSizeBox;
    private readonly NumericUpDown _browserTabWidthBox;
    private readonly ComboBox _viewerFontCombo;
    private readonly NumericUpDown _viewerFontSizeBox;
    private readonly ComboBox _colorThemeCombo;
    private readonly CheckBox _showExtensionsCheckBox;
    private readonly CheckBox _showBrowserTabCategoryRowCheckBox;
    private readonly CheckBox _showDirectoryMarkerCheckBox;
    private readonly CheckBox _showHiddenFilesCheckBox;
    private readonly CheckBox _showItemIconsCheckBox;
    private readonly CheckBox _useUnderlineCursorCheckBox;
    private readonly CheckBox _showFunctionBarCheckBox;
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
    private readonly CheckBox _managedTrashAutoHandoffCheckBox;
    private readonly NumericUpDown _managedTrashUndoRetentionDaysBox;
    private readonly CheckBox _reloadAfterFileOperationCheckBox;
    private readonly CheckBox _selectCreatedItemCheckBox;
    private readonly CheckBox _clipboardPasteTextAsFileCheckBox;
    private CheckBox _enableDragArchiveHandoffCheckBox = null!;
    private CheckBox _includeDragZipManifestCheckBox = null!;
    private readonly CheckBox _restoreStartupStateCheckBox;
    private readonly CheckBox _restoreTabsOnStartupCheckBox;
    private readonly CheckBox _restoreLastPathCheckBox;
    private readonly CheckBox _restoreDisplayStateCheckBox;
    private readonly CheckBox _restoreWindowBoundsCheckBox;
    private readonly CheckBox _restoreColumnCountCheckBox;
    private readonly CheckBox _restoreSortCheckBox;
    private readonly CheckBox _enableMouseGesturesCheckBox;
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
    private readonly Panel _functionBarPreviewPanel;
    private bool _updatingColorFromUi;
    private bool _suppressColorUiEvents;
    private bool _fileListCustomColorsEnabledForSave;

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
    public event EventHandler? ManualEmptyManagedTrashRequested;

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
        _settings.BrowserTabs ??= new BrowserTabSettings();
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
        ClientSize = new Size(1088, 720);

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

        (_filerFontCombo, _filerFontSizeBox, _browserTabFontSizeBox, _browserTabWidthBox, _showBrowserTabCategoryRowCheckBox, _showExtensionsCheckBox, _showDirectoryMarkerCheckBox, _showHiddenFilesCheckBox, _showItemIconsCheckBox, _useUnderlineCursorCheckBox, _showFunctionBarCheckBox, _showBrowserToolbarCheckBox, _fileDisplayModeCombo, _dateFormatCombo, _sizeFormatCombo,
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
        _functionBarPreviewPanel = colorTabResult.FunctionBarPreviewPanel;

        (_confirmDeleteCheckBox, _confirmPermanentDeleteCheckBox, _useMidFdManagedTrashCheckBox, _managedTrashAutoHandoffCheckBox, _managedTrashUndoRetentionDaysBox, _reloadAfterFileOperationCheckBox, _selectCreatedItemCheckBox, _clipboardPasteTextAsFileCheckBox,
         _enableMouseGesturesCheckBox)
            = BuildOperationAndInputTab(tabOperation);

        _embeddedInputAssignmentView = BuildInputAssignmentTab(tabInputAssignment);

        tabStartupAndLog.AutoScroll = false;

        (_restoreStartupStateCheckBox, _restoreTabsOnStartupCheckBox, _restoreLastPathCheckBox, _restoreDisplayStateCheckBox, _restoreWindowBoundsCheckBox, _restoreColumnCountCheckBox, _restoreSortCheckBox)
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

    private (ComboBox filerFont, NumericUpDown filerSize, NumericUpDown browserTabFontSize, NumericUpDown browserTabWidth, CheckBox showBrowserTabCategoryRow, CheckBox showExtensions, CheckBox showDirectoryMarker, CheckBox showHiddenFiles, CheckBox showItemIcons, CheckBox useUnderlineCursor, CheckBox showFunctionBar, CheckBox showBrowserToolbar, ComboBox fileDisplayMode, ComboBox dateFormat, ComboBox sizeFormat,
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
        var groupList = new GroupBox { Text = "一覧表示", Location = new Point(8, 6), Size = new Size(500, 520) };
        tab.Controls.Add(groupList);

        int top = topY;

        var fileListFontLabel = new Label
        {
            Text = "一覧表示フォント:",
            Location = new Point(16, top + 4),
            Size = new Size(140, 20),
            TextAlign = ContentAlignment.MiddleLeft
        };
        groupList.Controls.Add(fileListFontLabel);
        var filerFont = AddFontComboBox(groupList, 160, top, 146, fonts, _settings.Fonts.FileListFontFamily);
        var filerSize = AddNumericUpDown(groupList, 314, top, 60, (decimal)_settings.Fonts.FileListFontSize, min: 0.1m, max: 999m);
        var resetFileListFontSizeButton = new Button
        {
            Text = "初期値",
            Location = new Point(382, top - 1),
            Size = new Size(104, 26)
        };
        resetFileListFontSizeButton.Click += (_, _) =>
        {
            _filerFontSizeBox.Value = 11.0m;
        };
        groupList.Controls.Add(resetFileListFontSizeButton);
        top += rowH + 8;

        int fileListPreviewTop = top + 4;
        var fileListFontSample = CreateFontSampleTextBox(new Point(16, fileListPreviewTop), new Size(460, 104), FontPreviewSampleText);
        groupList.Controls.Add(fileListFontSample);
        top += 112;

        AddLabel(groupList, "タブ文字サイズ:", top, lblW);
        var browserTabFontSize = AddNumericUpDown(groupList, inpX, top, 72, (decimal)_settings.BrowserTabs.TabFontSize, min: 0.1m, max: 9999m, decimalPlaces: 1, increment: 0.5m);
        var resetTabFontSizeButton = new Button
        {
            Text = "初期値",
            Location = new Point(194, top - 1),
            Size = new Size(104, 26)
        };
        resetTabFontSizeButton.Click += (_, _) =>
        {
            _browserTabFontSizeBox.Value = (decimal)BrowserTabSettings.DefaultTabFontSize;
        };
        groupList.Controls.Add(resetTabFontSizeButton);
        top += rowH;

        AddLabel(groupList, "タブ幅:", top, lblW);
        var browserTabWidth = AddNumericUpDown(groupList, inpX, top, 72, _settings.BrowserTabs.TabWidth, min: 0m, max: 99999m, decimalPlaces: 0, increment: 10m);
        var resetTabWidthButton = new Button
        {
            Text = "初期値",
            Location = new Point(194, top - 1),
            Size = new Size(104, 26)
        };
        resetTabWidthButton.Click += (_, _) =>
        {
            _browserTabWidthBox.Value = BrowserTabSettings.DefaultTabWidth;
        };
        groupList.Controls.Add(resetTabWidthButton);
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
        var showBrowserToolbar = AddCheckBox(groupList, "上部の戻る・進む・上へ・更新ボタンを表示する", 16, checkY, _settings.Appearance.ShowBrowserToolbar);
        checkY += 24;
        var showFunctionBar = AddCheckBox(groupList, "下部のファンクションバーを表示する", 16, checkY, _settings.Appearance.ShowFunctionBar);
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
        var viewerFont = AddFontComboBox(groupViewer, 120, top, 178, fonts, _settings.Fonts.ViewerFontFamily);
        var viewerSize = AddNumericUpDown(groupViewer, sizeX, top, 60, (decimal)_settings.Fonts.ViewerFontSize);

        var viewerFontSample = CreateFontSampleTextBox(new Point(16, top + 40), new Size(460, 104), FontPreviewSampleText);
        groupViewer.Controls.Add(viewerFontSample);

        Font? fileListFontSampleOwnedFont = null;
        Font? viewerFontSampleOwnedFont = null;
        Disposed += (_, _) =>
        {
            fileListFontSampleOwnedFont?.Dispose();
            viewerFontSampleOwnedFont?.Dispose();
        };

        void UpdateFontSample(TextBox sample, ref Font? ownedFont, string familyName, float size)
        {
            Font? nextFont = CreatePreviewFont(familyName, size);
            if (nextFont == null)
            {
                ownedFont?.Dispose();
                ownedFont = null;
                sample.Font = sample.Parent?.Font ?? SystemFonts.DefaultFont;
                return;
            }

            ownedFont?.Dispose();
            ownedFont = nextFont;
            sample.Font = nextFont;
        }

        void UpdateFileListFontSample() => UpdateFontSample(fileListFontSample, ref fileListFontSampleOwnedFont, filerFont.Text, (float)filerSize.Value);
        void UpdateViewerFontSample() => UpdateFontSample(viewerFontSample, ref viewerFontSampleOwnedFont, viewerFont.Text, (float)viewerSize.Value);

        filerFont.SelectedIndexChanged += (_, _) => UpdateFileListFontSample();
        filerFont.TextChanged += (_, _) => UpdateFileListFontSample();
        filerSize.ValueChanged += (_, _) => UpdateFileListFontSample();
        viewerFont.SelectedIndexChanged += (_, _) => UpdateViewerFontSample();
        viewerFont.TextChanged += (_, _) => UpdateViewerFontSample();
        viewerSize.ValueChanged += (_, _) => UpdateViewerFontSample();
        UpdateFileListFontSample();
        UpdateViewerFontSample();

        top += 145;

        var viewerWordWrap = AddCheckBox(groupViewer, "折り返しを既定で ON にする", checkX, top, _settings.Preview.ViewerWordWrap);
        top += rowH;
        var reuseImageViewer = AddCheckBox(groupViewer, "画像ビューアを再利用する", checkX, top, _settings.Preview.ReuseImageViewer);
        top += rowH;
        var closeOnNonImage = AddCheckBox(groupViewer, "非画像時にビューアを閉じる", checkX, top, _settings.Preview.CloseImageViewerOnNonImageSelection);
        top += rowH;
        var rememberBounds = AddCheckBox(groupViewer, "ビューアの位置/サイズを記憶する", checkX, top, _settings.Preview.RememberImageViewerBounds);
        groupViewer.Height = rememberBounds.Bottom + 16;

        return (filerFont, filerSize, browserTabFontSize, browserTabWidth, showBrowserTabCategoryRow, showExtensions, showDirectoryMarker, showHiddenFiles, showItemIcons, useUnderlineCursor, showFunctionBar, showBrowserToolbar, fileDisplayMode, dateFormat, sizeFormat,
                viewerFont, viewerSize, viewerWordWrap, reuseImageViewer, closeOnNonImage, rememberBounds);
    }

    private (CheckBox confirmDelete, CheckBox confirmPermanentDelete, CheckBox useMidFdManagedTrash, CheckBox managedTrashAutoHandoff, NumericUpDown managedTrashUndoRetentionDays, CheckBox reloadAfterFileOperation, CheckBox selectCreatedItem, CheckBox clipboardPasteTextAsFile,
             CheckBox enableMouseGestures)
        BuildOperationAndInputTab(TabPage tab)
    {
        int rowH = 28;

        // --- Left: File Operation ---
        var groupFile = new GroupBox { Text = "ファイル操作", Location = new Point(8, 6), Size = new Size(490, 560) };
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

        var managedTrashAutoHandoff = AddCheckBox(groupFile, "期限切れ退避ファイルを自動整理する", 16, top, _settings.FileOperations.ManagedTrashAutoHandoffEnabled);
        top += rowH;
        AddLabel(groupFile, "Undo保持日数:", top, 112);
        var managedTrashUndoRetentionDays = AddNumericUpDown(groupFile, 130, top, 72, _settings.FileOperations.ManagedTrashUndoRetentionDays);
        managedTrashUndoRetentionDays.Minimum = 1;
        managedTrashUndoRetentionDays.Maximum = 365;
        managedTrashUndoRetentionDays.DecimalPlaces = 0;
        managedTrashUndoRetentionDays.Increment = 1;
        top += rowH - 2;
        var managedTrashRetentionHint = AddWrappedHintLabel(groupFile, 32, top, 430, "指定日数を過ぎた MidFD 管理ゴミ箱内の退避項目を Windows ごみ箱へ移します。");
        top = managedTrashRetentionHint.Bottom + 8;

        var btnEmptyTrash = new Button
        {
            Text = "MidFD管理ゴミ箱を今すぐ空にする...",
            Location = new Point(32, top),
            Size = new Size(240, 28)
        };
        btnEmptyTrash.Click += (s, e) =>
        {
            ManualEmptyManagedTrashRequested?.Invoke(this, EventArgs.Empty);
        };
        groupFile.Controls.Add(btnEmptyTrash);
        top += rowH + 6;

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
            "ChatGPT等へ複数ソースをまとめて渡す場合に便利です。\n通常のドラッグ操作と挙動が変わります。");
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

        var workspaceHint = AddWrappedHintLabel(groupAdvanced, 16, advancedTop, 444, "詳細な復元設定は起動・ログで調整できます。\nここでは管理導線だけを設定します。");
        advancedTop = workspaceHint.Bottom + 8;

        return (confirmDelete, confirmPermanentDelete, useMidFdManagedTrash, managedTrashAutoHandoff, managedTrashUndoRetentionDays, reloadAfterFileOperation, selectCreatedItem, clipboardPasteTextAsFile, enableMouseGestures);
    }

    private (CheckBox restoreStartupState, CheckBox restoreTabsOnStartup, CheckBox restoreLastPath, CheckBox restoreDisplayState, CheckBox restoreWindowBounds, CheckBox restoreColumnCount, CheckBox restoreSort)
        BuildLaunchAndRestoreTab(TabPage tab)
    {
        int rowH = 32;

        var groupStartup = new GroupBox { Text = "起動時に復元する内容", Location = new Point(8, 6), Size = new Size(490, 432) };
        tab.Controls.Add(groupStartup);

        int top = 28;
        var restoreStartupState = AddCheckBox(groupStartup, "起動時に前回の状態を復元する", 16, top, _settings.Session.RestoreStartupState);
        top += rowH - 4;
        var hint1 = AddWrappedHintLabel(groupStartup, 32, top, 436, "下の項目を使って、前回終了時の状態を復元します。\nOFFにすると復元は行いませんが、下のチェック状態は保持します。");
        top = hint1.Bottom + 10;

        var restoreTabsOnStartup = AddCheckBox(groupStartup, "前回のカテゴリ・タブ構成を復元する", 32, top, _settings.Session.RestoreTabsOnStartup);
        top += rowH - 4;
        var hint2 = AddWrappedHintLabel(groupStartup, 48, top, 420, "カテゴリ、タブ、タブごとの場所、固定状態などを復元します。");
        top = hint2.Bottom + 8;

        var restoreLastPath = AddCheckBox(groupStartup, "前回開いていたフォルダを初期表示する", 32, top, _settings.Session.RestoreLastPath);
        top += rowH - 4;
        var hint3 = AddWrappedHintLabel(groupStartup, 48, top, 420, "カテゴリ・タブ構成を復元しない場合に、最後に開いていたフォルダを開きます。");
        top = hint3.Bottom + 8;

        var restoreWindowBounds = AddCheckBox(groupStartup, "ウィンドウ位置/サイズを復元する", 32, top, _settings.Session.RestoreWindowBounds);
        top += rowH - 4;
        var restoreColumnCount = AddCheckBox(groupStartup, "前回の列数を復元する", 32, top, _settings.Session.RestoreColumnCount);
        top += rowH - 4;
        var restoreSort = AddCheckBox(groupStartup, "前回のソートを復元する", 32, top, _settings.Session.RestoreSort);
        top += rowH + 2;

        var btnOpenFirstSetup = new Button
        {
            Text = "初回セットアップを開く...",
            Location = new Point(16, top),
            Size = new Size(180, 32)
        };
        btnOpenFirstSetup.Click += (_, _) => OpenFirstLaunchSetupDialog();
        groupStartup.Controls.Add(btnOpenFirstSetup);
        top += rowH + 4;
        var hint4 = AddWrappedHintLabel(groupStartup, 16, top, 460, "初回オプション、Fキー配置、メディアEnter動作、外部連携の基本設定を再設定できます。\n初期化ではありません。");
        groupStartup.Height = hint4.Bottom + 16;

        void UpdateStartupRestoreControlsEnabledStateLocal()
        {
            bool enabled = restoreStartupState.Checked;
            restoreTabsOnStartup.Enabled = enabled;
            restoreLastPath.Enabled = enabled;
            restoreWindowBounds.Enabled = enabled;
            restoreColumnCount.Enabled = enabled;
            restoreSort.Enabled = enabled;
        }

        restoreStartupState.CheckedChanged += (_, _) => UpdateStartupRestoreControlsEnabledStateLocal();
        UpdateStartupRestoreControlsEnabledStateLocal();

        var restoreDisplayState = new CheckBox
        {
            Visible = false,
            Checked = _settings.Session.RestoreDisplayState
        };

        return (restoreStartupState, restoreTabsOnStartup, restoreLastPath, restoreDisplayState, restoreWindowBounds, restoreColumnCount, restoreSort);
    }

    private InputAssignmentDialog BuildInputAssignmentTab(TabPage tab)
    {
        var embedded = new InputAssignmentDialog(_settings.Input, _commandRegistry)
        {
            TopLevel = false,
            FormBorderStyle = FormBorderStyle.None,
            Dock = DockStyle.Fill
        };

        embedded.ProfileChanged += HandleInputProfileChanged;

        tab.Controls.Add(embedded);
        embedded.Show();
        return embedded;
    }

    private void HandleInputProfileChanged(object? sender, EventArgs e)
    {
        if (_embeddedInputAssignmentView == null || _colorThemeCombo == null)
        {
            return;
        }

        _suppressColorUiEvents = true;
        try
        {
            string profileValue = _embeddedInputAssignmentView.SelectedProfileValue;
            string targetPresetKey = string.Equals(profileValue, InputSettings.FdCompatibleProfileValue, StringComparison.OrdinalIgnoreCase)
                 ? "WinFdCompatible"
                 : "MidFdStandard";

            _settings.Appearance.ColorTheme = targetPresetKey;

            _settings.Appearance.CustomFunctionBarBackColor = null;
            _settings.Appearance.CustomFunctionBarForeColor = null;

            string displayPresetName = FileListColorResolver.GetPresetDisplayName(targetPresetKey);
            int idx = _colorThemeCombo.FindStringExact(displayPresetName);
            if (idx >= 0)
            {
                _colorThemeCombo.SelectedIndex = idx;
            }
        }
        finally
        {
            _suppressColorUiEvents = false;
        }

        ApplySelectedColorPresetToEditor(forceRefresh: true);
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
        var videoEnterPlaysExternal = AddCheckBox(groupVideoTools, "メディアファイル Enter で外部再生する", 16, top, _settings.Preview.VideoEnterPlaysExternal);
        top += 26;
        AddHintLabel(groupVideoTools, 16, top, 460, "※ 動画: OFF=Enterで静止画 / Ctrl+Enterで外部再生\n※ 動画: ON=Enterで外部再生 / Ctrl+Enterで静止画\n※ 音声: 設定ON/OFFに関係なく Enter / Ctrl+Enter で外部再生\n※ 静止画: ffmpeg / 再生: ffplay / 長さ: ffprobe", 54);

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

    private ComboBox AddFontComboBox(Control parent, int x, int top, int width, string[] items, string current)
    {
        var cb = new ComboBox
        {
            Location = new Point(x, top),
            Size = new Size(width, 24),
            DropDownStyle = ComboBoxStyle.DropDown,
            AutoCompleteMode = AutoCompleteMode.SuggestAppend,
            AutoCompleteSource = AutoCompleteSource.ListItems
        };
        cb.Items.AddRange(items);
        int idx = cb.FindStringExact(current);
        if (idx >= 0) cb.SelectedIndex = idx;
        else cb.Text = current;

        cb.DrawMode = DrawMode.OwnerDrawFixed;
        cb.IntegralHeight = false;
        cb.ItemHeight = Math.Max(20, cb.Font.Height + 6);
        cb.DrawItem += FontCombo_DrawItem;
        parent.Controls.Add(cb);
        return cb;
    }

    private static TextBox CreateFontSampleTextBox(Point location, Size size, string text)
    {
        return new TextBox
        {
            Text = text,
            Location = location,
            Size = size,
            Multiline = true,
            ReadOnly = false,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            AcceptsReturn = true,
            AcceptsTab = false,
            TabStop = true,
            BorderStyle = BorderStyle.FixedSingle
        };
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

    private static Font? CreatePreviewFont(string familyName, float size)
    {
        if (string.IsNullOrWhiteSpace(familyName) || !MidFD.Helpers.FontResolver.IsFontInstalled(familyName, out string normalizedName))
        {
            return null;
        }

        try
        {
            return new Font(normalizedName, size, FontStyle.Regular, GraphicsUnit.Point);
        }
        catch
        {
            return null;
        }
    }

    private void FontCombo_DrawItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ComboBox combo)
        {
            return;
        }

        e.DrawBackground();

        string fontName = e.Index >= 0
            ? combo.Items[e.Index]?.ToString() ?? string.Empty
            : combo.Text;

        if (string.IsNullOrWhiteSpace(fontName))
        {
            e.DrawFocusRectangle();
            return;
        }

        Font fallbackFont = e.Font ?? combo.Font;
        Font? drawFont = CreatePreviewFont(fontName, fallbackFont.Size);
        bool ownsFont = drawFont != null;
        drawFont ??= fallbackFont;

        try
        {
            var textBounds = new Rectangle(e.Bounds.X + 2, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 4), e.Bounds.Height);
            var textColor = (e.State & DrawItemState.Selected) == DrawItemState.Selected
                ? SystemColors.HighlightText
                : combo.ForeColor;
            TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(e.Graphics, fontName, drawFont, textBounds, textColor, flags);
        }
        finally
        {
            if (ownsFont)
            {
                drawFont.Dispose();
            }
        }

        e.DrawFocusRectangle();
    }

    private NumericUpDown AddNumericUpDown(Control parent, int x, int top, int width, decimal current, decimal min = 6m, decimal max = 72m, int decimalPlaces = 1, decimal increment = 1m)
    {
        var n = new NumericUpDown
        {
            Location = new Point(x, top),
            Size = new Size(width, 24),
            Minimum = min,
            Maximum = max,
            Value = current,
            DecimalPlaces = decimalPlaces,
            Increment = increment
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
        _settings.BrowserTabs.TabFontSize = (float)_browserTabFontSizeBox.Value;
        _settings.BrowserTabs.TabWidth = (int)_browserTabWidthBox.Value;
        _settings.Fonts.ViewerFontFamily = _viewerFontCombo.Text;
        _settings.Fonts.ViewerFontSize = (float)_viewerFontSizeBox.Value;

        PersistEditedFileListColorsAsPresetIfNeeded();
        _settings.Appearance.ColorTheme = FileListColorResolver.CanonicalizePresetKey(_colorThemeCombo.Text);
        _settings.Appearance.UseCustomFileListColors = _fileListCustomColorsEnabledForSave;
        _settings.Appearance.EnableSemanticColorAssist = _enableColorAssistCheckBox.Checked;
        _settings.Appearance.ShowBrowserTabCategoryRow = _showBrowserTabCategoryRowCheckBox.Checked;
        _settings.Appearance.ShowFunctionBar = _showFunctionBarCheckBox.Checked;
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
        _settings.FileOperations.ManagedTrashAutoHandoffEnabled = _managedTrashAutoHandoffCheckBox.Checked;
        _settings.FileOperations.ManagedTrashUndoRetentionDays = (int)_managedTrashUndoRetentionDaysBox.Value;
        // _settings.FileOperations.ManagedTrashStoreMode は Initialize 時に自動決定するためUIからは変更しない

        _settings.FileOperations.ReloadAfterFileOperation = _reloadAfterFileOperationCheckBox.Checked;
        _settings.FileOperations.SelectCreatedItemAfterCreate = _selectCreatedItemCheckBox.Checked;
        _settings.FileOperations.ClipboardPasteTextAsFileEnabled = _clipboardPasteTextAsFileCheckBox.Checked;
        _settings.FileOperations.EnableDragArchiveHandoff = _enableDragArchiveHandoffCheckBox.Checked;
        _settings.FileOperations.IncludeDragZipManifest = _includeDragZipManifestCheckBox.Checked;

        _settings.Session.RestoreStartupState = _restoreStartupStateCheckBox.Checked;
        _settings.Session.RestoreTabsOnStartup = _restoreTabsOnStartupCheckBox.Checked;
        _settings.Session.RestoreLastPath = _restoreLastPathCheckBox.Checked;
        _settings.Session.RestoreDisplayState = _restoreDisplayStateCheckBox.Checked;
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

    private void UpdateStartupRestoreControlsEnabledState()
    {
        bool enabled = _restoreStartupStateCheckBox.Checked;
        _restoreTabsOnStartupCheckBox.Enabled = enabled;
        _restoreLastPathCheckBox.Enabled = enabled;
        _restoreWindowBoundsCheckBox.Enabled = enabled;
        _restoreColumnCountCheckBox.Enabled = enabled;
        _restoreSortCheckBox.Enabled = enabled;
    }

    private void ApplyFirstLaunchRestoreStartupState(bool enabled)
    {
        _restoreStartupStateCheckBox.Checked = enabled;
        _restoreTabsOnStartupCheckBox.Checked = enabled;
        _restoreLastPathCheckBox.Checked = enabled;
        _restoreDisplayStateCheckBox.Checked = enabled;
        _restoreWindowBoundsCheckBox.Checked = enabled;
        _restoreColumnCountCheckBox.Checked = enabled;
        _restoreSortCheckBox.Checked = enabled;
        UpdateStartupRestoreControlsEnabledState();
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

    private bool ValidateFontSettings()
    {
        string filerFamily = _filerFontCombo.Text.Trim();
        if (string.IsNullOrWhiteSpace(filerFamily))
        {
            MessageBox.Show(this, "一覧表示フォント名を入力してください。", "設定エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _filerFontCombo.Focus();
            return false;
        }
        if (!MidFD.Helpers.FontResolver.IsFontInstalled(filerFamily, out string normalizedFiler))
        {
            MessageBox.Show(this, $"一覧表示フォント「{filerFamily}」はシステムにインストールされていません。\n有効なフォント名を指定してください。", "設定エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _filerFontCombo.Focus();
            return false;
        }

        string viewerFamily = _viewerFontCombo.Text.Trim();
        if (string.IsNullOrWhiteSpace(viewerFamily))
        {
            MessageBox.Show(this, "Viewerフォント名を入力してください。", "設定エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _viewerFontCombo.Focus();
            return false;
        }
        if (!MidFD.Helpers.FontResolver.IsFontInstalled(viewerFamily, out string normalizedViewer))
        {
            MessageBox.Show(this, $"Viewerフォント「{viewerFamily}」はシステムにインストールされていません。\n有効なフォント名を指定してください。", "設定エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _viewerFontCombo.Focus();
            return false;
        }

        _filerFontCombo.Text = normalizedFiler;
        _viewerFontCombo.Text = normalizedViewer;
        return true;
    }

    private void BtnOk_Click(object? sender, EventArgs e)
    {
        if (!ValidateFontSettings())
        {
            this.DialogResult = DialogResult.None;
            return;
        }
        SaveCurrentSettings();
    }

    private void BtnApply_Click(object? sender, EventArgs e)
    {
        if (!ValidateFontSettings())
        {
            return;
        }
        SaveCurrentSettings();
        SettingsApplied?.Invoke(this, EventArgs.Empty);
    }

    private void OpenFirstLaunchSetupDialog()
    {
        var setupSettings = _settings.Clone();
        setupSettings.Input.FunctionKeyProfile = _embeddedInputAssignmentView.SelectedProfileValue;
        setupSettings.Preview.VideoEnterPlaysExternal = _videoEnterPlaysExternalCheckBox.Checked;
        setupSettings.SevenZip.ExePath = NullIfEmpty(_sevenZipPathBox.Text);
        setupSettings.Preview.VideoToolDirectory = NullIfEmpty(_videoStillPreviewFfmpegPathBox.Text);
        setupSettings.ExternalTools.ExternalEditorPath = NullIfEmpty(_editorPathBox.Text);
        setupSettings.Session.RestoreStartupState = _restoreStartupStateCheckBox.Checked;
        setupSettings.Session.RestoreTabsOnStartup = _restoreTabsOnStartupCheckBox.Checked;
        setupSettings.Session.RestoreLastPath = _restoreLastPathCheckBox.Checked;
        setupSettings.Session.RestoreDisplayState = _restoreDisplayStateCheckBox.Checked;
        setupSettings.Session.RestoreWindowBounds = _restoreWindowBoundsCheckBox.Checked;
        setupSettings.Session.RestoreColumnCount = _restoreColumnCountCheckBox.Checked;
        setupSettings.Session.RestoreSort = _restoreSortCheckBox.Checked;

        using var dialog = new FeatureProfileSelectionDialog(setupSettings);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _embeddedInputAssignmentView.SelectedProfileValue = dialog.UseFdCompatibleFunctionKeys ? InputSettings.FdCompatibleProfileValue : InputSettings.StandardProfileValue;
        _videoEnterPlaysExternalCheckBox.Checked = dialog.VideoEnterPlaysExternal;
        _enableMouseGesturesCheckBox.Checked = dialog.EnableMouseGestures;
        _showFunctionBarTooltipsCheckBox!.Checked = dialog.ShowFunctionBarTooltips;
        _enableDragArchiveHandoffCheckBox.Checked = dialog.EnableDragArchiveHandoff;
        _includeDragZipManifestCheckBox.Checked = dialog.IncludeDragZipManifest;
        ApplyFirstLaunchRestoreStartupState(dialog.RestoreStartupState);
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
        public required Panel FunctionBarPreviewPanel;
    }

    private ColorTabResult BuildColorTab(TabPage tab)
    {
        var groupCustom = new GroupBox { Text = "一覧配色カスタマイズ", Location = new Point(8, 6), Size = new Size(540, 372) };
        var groupPreview = new GroupBox { Text = "プレビュー", Location = new Point(556, 6), Size = new Size(492, 372) };
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
            Text = "選択表示色へ戻す",
            Location = new Point(12, top),
            Size = new Size(152, 26)
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
            Size = new Size(255, 120),
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 22,
            IntegralHeight = false
        };
        fileListColorFieldListBox.Items.AddRange(ColorFieldItems.Select(x => x.DisplayName).ToArray());
        int fileListColorListHeight = (fileListColorFieldListBox.ItemHeight * fileListColorFieldListBox.Items.Count) + 10;
        fileListColorFieldListBox.Size = new Size(255, fileListColorListHeight);
        groupCustom.Controls.Add(fileListColorFieldListBox);

        int adjX = 278;
        var fileListColorCurrentPreviewPanel = new Panel
        {
            Location = new Point(adjX, top),
            Size = new Size(148, 36),
            BorderStyle = BorderStyle.FixedSingle
        };

        var fileListColorPickerButton = new Button
        {
            Text = "色選択...",
            Location = new Point(adjX, top + 42),
            Size = new Size(146, 28)
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
            Size = new Size(102, 24)
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
            Size = new Size(102, 24),
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
            Size = new Size(102, 24),
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
            Size = new Size(102, 24),
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

        top += fileListColorListHeight;
        var warningLabelLocal = new Label
        {
            Location = new Point(12, top),
            Size = new Size(370, 24),
            Font = new Font(tab.Font, FontStyle.Bold)
        };
        groupCustom.Controls.Add(warningLabelLocal);

        var fileListColorPreviewPanel = new ListView
        {
            Location = new Point(10, 20),
            Size = new Size(472, 220),
            View = View.Details,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.None,
            OwnerDraw = true,
            MultiSelect = false
        };
        fileListColorPreviewPanel.Columns.Add("Name", 468);

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
        int functionPreviewTop = fileListColorPreviewPanel.Bottom - 1;
        var functionPreviewPanel = new Panel
        {
            Location = new Point(10, functionPreviewTop),
            Size = new Size(472, 24),
            BorderStyle = BorderStyle.None
        };
        string[] functionSampleLabels = { "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10" };
        int functionLabelX = 8;
        FunctionPreviewPalette functionPalette = ResolveFunctionPreviewPalette();
        foreach (string sampleLabel in functionSampleLabels)
        {
            int sampleWidth = sampleLabel.Length >= 3 ? 44 : 38;
            var sample = new Label
            {
                Text = sampleLabel,
                Location = new Point(functionLabelX, 2),
                Size = new Size(sampleWidth, 20),
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                TextAlign = ContentAlignment.MiddleCenter,
                BackColor = functionPalette.ButtonBackColor,
                ForeColor = functionPalette.ButtonForeColor
            };
            sample.Paint += (_, e) =>
            {
                FunctionPreviewPalette currentPalette = ResolveFunctionPreviewPalette();
                Color borderColor = BlendColors(sample.BackColor, currentPalette.PanelBackColor, 0.22);
                using var pen = new Pen(borderColor);
                e.Graphics.DrawRectangle(pen, 0, 0, sample.Width - 1, sample.Height - 1);
            };
            functionPreviewPanel.Controls.Add(sample);
            functionLabelX += sampleWidth + 5;
        }

        groupPreview.Controls.Add(functionPreviewPanel);

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

        UpdatePreview();

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
            FunctionBarPreviewPanel = functionPreviewPanel
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

    private static Color BlendColors(Color baseColor, Color targetColor, double targetWeight)
    {
        double clampedWeight = Math.Clamp(targetWeight, 0.0, 1.0);
        double sourceWeight = 1.0 - clampedWeight;
        int r = (int)Math.Round((baseColor.R * sourceWeight) + (targetColor.R * clampedWeight));
        int g = (int)Math.Round((baseColor.G * sourceWeight) + (targetColor.G * clampedWeight));
        int b = (int)Math.Round((baseColor.B * sourceWeight) + (targetColor.B * clampedWeight));
        return Color.FromArgb(r, g, b);
    }

    private AppSettings BuildColorTabPreviewSettings()
    {
        var previewSettings = _settings.Clone();
        bool enableColorAssist = _enableColorAssistCheckBox != null
            ? _enableColorAssistCheckBox.Checked
            : _settings.Appearance.EnableSemanticColorAssist;
        previewSettings.Appearance.UseCustomFileListColors = _fileListCustomColorsEnabledForSave;
        previewSettings.Appearance.EnableSemanticColorAssist = enableColorAssist;
        previewSettings.Appearance.ColorTheme = FileListColorResolver.CanonicalizePresetKey(_colorThemeCombo?.Text ?? _settings.Appearance.ColorTheme);
        return previewSettings;
    }

    private sealed record FunctionPreviewPalette(
        Color PanelBackColor,
        Color ButtonBackColor,
        Color ButtonForeColor,
        Color ButtonBorderColor);

    private (Color ButtonBackColor, Color ButtonForeColor, Color ButtonBorderColor) ResolveDarkStandardFunctionPreviewColors(
        string presetKey,
        FileListColorResolver.ResolvedColors resolved)
    {
        presetKey = FileListColorResolver.CanonicalizePresetKey(presetKey);

        if (string.Equals(presetKey, "Slate", StringComparison.OrdinalIgnoreCase))
        {
            Color buttonBack = BlendColors(resolved.Background, resolved.Directory, 0.42);
            Color buttonFore = FileListColorResolver.GetContrastRatio(buttonBack, resolved.NormalFile)
                >= FileListColorResolver.GetContrastRatio(buttonBack, Color.White)
                ? resolved.NormalFile
                : Color.White;
            return (buttonBack, buttonFore, resolved.Directory);
        }

        if (string.Equals(presetKey, "Violet", StringComparison.OrdinalIgnoreCase))
        {
            Color buttonBack = BlendColors(resolved.Background, resolved.Directory, 0.44);
            Color buttonFore = FileListColorResolver.GetContrastRatio(buttonBack, resolved.NormalFile)
                >= FileListColorResolver.GetContrastRatio(buttonBack, Color.White)
                ? resolved.NormalFile
                : Color.White;
            return (buttonBack, buttonFore, resolved.Directory);
        }

        if (string.Equals(presetKey, "Sepia", StringComparison.OrdinalIgnoreCase))
        {
            Color buttonBack = BlendColors(resolved.Background, resolved.Directory, 0.46);
            Color buttonFore = FileListColorResolver.GetContrastRatio(buttonBack, resolved.NormalFile)
                >= FileListColorResolver.GetContrastRatio(buttonBack, Color.White)
                ? resolved.NormalFile
                : Color.White;
            return (buttonBack, buttonFore, resolved.Directory);
        }

        if (string.Equals(presetKey, "Mono Dark", StringComparison.OrdinalIgnoreCase))
        {
            Color buttonBack = BlendColors(resolved.Background, resolved.Directory, 0.40);
            Color buttonFore = FileListColorResolver.GetContrastRatio(buttonBack, resolved.NormalFile)
                >= FileListColorResolver.GetContrastRatio(buttonBack, Color.White)
                ? resolved.NormalFile
                : Color.White;
            return (buttonBack, buttonFore, resolved.Directory);
        }

        if (string.Equals(presetKey, "Cyber", StringComparison.OrdinalIgnoreCase))
        {
            Color buttonBack = BlendColors(resolved.Background, resolved.Directory, 0.52);
            Color buttonFore = FileListColorResolver.GetContrastRatio(buttonBack, resolved.NormalFile)
                >= FileListColorResolver.GetContrastRatio(buttonBack, Color.White)
                ? resolved.NormalFile
                : Color.White;
            return (buttonBack, buttonFore, resolved.Directory);
        }

        if (string.Equals(presetKey, "Green", StringComparison.OrdinalIgnoreCase))
        {
            Color buttonBack = BlendColors(resolved.Background, resolved.Directory, 0.36);
            Color buttonFore = FileListColorResolver.GetContrastRatio(buttonBack, resolved.NormalFile)
                >= FileListColorResolver.GetContrastRatio(buttonBack, Color.White)
                ? resolved.NormalFile
                : Color.White;
            return (buttonBack, buttonFore, resolved.Directory);
        }

        if (string.Equals(presetKey, "Amber", StringComparison.OrdinalIgnoreCase))
        {
            Color buttonBack = BlendColors(resolved.Background, resolved.Directory, 0.38);
            Color buttonFore = FileListColorResolver.GetContrastRatio(buttonBack, resolved.NormalFile)
                >= FileListColorResolver.GetContrastRatio(buttonBack, Color.White)
                ? resolved.NormalFile
                : Color.White;
            return (buttonBack, buttonFore, resolved.Directory);
        }

        return (
            Color.FromArgb(60, 120, 180),
            Color.FromArgb(220, 238, 255),
            Color.FromArgb(70, 100, 120));
    }

    private FunctionPreviewPalette ResolveFunctionPreviewPalette()
    {
        AppSettings previewSettings = BuildColorTabPreviewSettings();
        var resolved = FileListColorResolver.ResolveColors(previewSettings);
        string themeNormalized = FileListColorResolver.NormalizeCoreTheme(previewSettings.Appearance.ColorTheme, previewSettings);
        bool isLightTheme = themeNormalized == "Light";
        bool isWinFdCompatible = FunctionKeyProfileService.ResolveProfile(previewSettings.Input.FunctionKeyProfile) == FunctionKeyProfile.FDCompatible;

        Color accentColor = resolved.Directory;
        Color? customBackColor = UiThemeResolver.TryParseColor(previewSettings.Appearance.CustomFunctionBarBackColor);
        Color? customForeColor = UiThemeResolver.TryParseColor(previewSettings.Appearance.CustomFunctionBarForeColor);
        bool hasCustomFunctionBarColors = customBackColor.HasValue || customForeColor.HasValue;

        if (hasCustomFunctionBarColors)
        {
            FunctionPreviewPalette defaults = ResolveDefaultFunctionPreviewPalette(previewSettings);
            Color buttonBack = customBackColor ?? defaults.ButtonBackColor;
            Color buttonFore = customForeColor ?? defaults.ButtonForeColor;
            Color panelBack = defaults.PanelBackColor;
            Color borderColor = isLightTheme ? Color.FromArgb(200, 200, 200) : Color.FromArgb(70, 100, 120);
            return new FunctionPreviewPalette(panelBack, buttonBack, buttonFore, borderColor);
        }

        return ResolveDefaultFunctionPreviewPalette(previewSettings);
    }

    private FunctionPreviewPalette ResolveDefaultFunctionPreviewPalette(AppSettings previewSettings)
    {
        if (previewSettings.Appearance == null)
        {
            return new FunctionPreviewPalette(Color.Black, Color.Gray, Color.White, Color.Black);
        }
        var resolved = FileListColorResolver.ResolveColors(previewSettings);
        string themeNormalized = FileListColorResolver.NormalizeCoreTheme(previewSettings.Appearance.ColorTheme, previewSettings);
        bool isLightTheme = themeNormalized == "Light";
        bool isWinFdCompatible = FunctionKeyProfileService.ResolveProfile(previewSettings.Input.FunctionKeyProfile) == FunctionKeyProfile.FDCompatible;

        string theme = previewSettings.Appearance?.ColorTheme ?? string.Empty;
        if (string.Equals(theme, "WinFdCompatible", StringComparison.OrdinalIgnoreCase))
        {
            isWinFdCompatible = true;
        }
        else if (string.Equals(theme, "MidFdStandard", StringComparison.OrdinalIgnoreCase))
        {
            isWinFdCompatible = false;
        }

        Color accentColor = resolved.Directory;

        if (isWinFdCompatible)
        {
            if (isLightTheme)
            {
                Color panelBack = Color.FromArgb(235, 235, 235);
                Color buttonBack = BlendColors(Color.White, accentColor, 0.25);
                if (FileListColorResolver.GetRelativeLuminance(buttonBack) < 0.6)
                {
                    buttonBack = Color.FromArgb(200, 240, 240);
                }

                return new FunctionPreviewPalette(
                    panelBack,
                    buttonBack,
                    Color.Black,
                    Color.FromArgb(200, 200, 200));
            }

            Color darkPanelBack = resolved.Background;
            Color darkButtonBack = accentColor;
            if (FileListColorResolver.GetRelativeLuminance(darkButtonBack) < 0.25)
            {
                darkButtonBack = BlendColors(darkButtonBack, Color.White, 0.5);
            }

            return new FunctionPreviewPalette(
                darkPanelBack,
                darkButtonBack,
                Color.Black,
                darkPanelBack);
        }

        if (isLightTheme)
        {
            return new FunctionPreviewPalette(
                resolved.Background,
                Color.FromArgb(228, 228, 228),
                Color.FromArgb(32, 32, 32),
                Color.FromArgb(198, 198, 198));
        }

        (Color previewButtonBack, Color previewButtonFore, Color previewButtonBorder) = ResolveDarkStandardFunctionPreviewColors(previewSettings.Appearance!.ColorTheme, resolved);
        return new FunctionPreviewPalette(
            resolved.Background,
            previewButtonBack,
            previewButtonFore,
            previewButtonBorder);
    }

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
        Color color = GetCurrentFieldColor(item);

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

    private Color GetCurrentFieldColor(ColorFieldItem item)
    {
        if (item.IsFunctionColor)
        {
            return GetFunctionFieldColor(item.PropertyName);
        }

        string? hex = GetPropertyValue(_settings.Appearance.CustomFileListColors, item.PropertyName);
        if (FileListColorResolver.ParseHexColor(hex) is Color c)
        {
            return c;
        }

        var defaultColors = FileListColorResolver.ResolvePresetColors(_colorThemeCombo.Text, _settings.Appearance.CustomFileListColorPresets);
        return item.PropertyName switch
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
            nameof(CustomFileListColorSettings.StatusNormal) => defaultColors.StatusNormal,
            nameof(CustomFileListColorSettings.StatusResult) => defaultColors.StatusResult,
            nameof(CustomFileListColorSettings.StatusError) => defaultColors.StatusError,
            _ => Color.White
        };
    }

    private Color GetFunctionFieldColor(string propertyName)
    {
        FunctionPreviewPalette palette = ResolveDefaultFunctionPreviewPalette(BuildColorTabPreviewSettings());
        return propertyName switch
        {
            nameof(AppearanceSettings.CustomFunctionBarBackColor) =>
                UiThemeResolver.TryParseColor(_settings.Appearance.CustomFunctionBarBackColor) ?? palette.ButtonBackColor,
            nameof(AppearanceSettings.CustomFunctionBarForeColor) =>
                UiThemeResolver.TryParseColor(_settings.Appearance.CustomFunctionBarForeColor) ?? palette.ButtonForeColor,
            _ => palette.ButtonBackColor
        };
    }

    private static string? GetPropertyValue(CustomFileListColorSettings target, string propertyName)
    {
        return target.GetType().GetProperty(propertyName)?.GetValue(target) as string;
    }

    private static void SetPropertyValue(CustomFileListColorSettings target, string propertyName, string? value)
    {
        target.GetType().GetProperty(propertyName)?.SetValue(target, value);
    }

    private void SetCurrentFieldColor(ColorFieldItem item, Color color)
    {
        if (item.IsFunctionColor)
        {
            if (item.PropertyName == nameof(AppearanceSettings.CustomFunctionBarBackColor))
            {
                _settings.Appearance.CustomFunctionBarBackColor = UiThemeResolver.ToHexString(color);
            }
            else if (item.PropertyName == nameof(AppearanceSettings.CustomFunctionBarForeColor))
            {
                _settings.Appearance.CustomFunctionBarForeColor = UiThemeResolver.ToHexString(color);
            }
            return;
        }

        SetPropertyValue(_settings.Appearance.CustomFileListColors, item.PropertyName, FileListColorResolver.ToHexColor(color));
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
            _colorThemeCombo.Items.Add(FileListColorResolver.GetPresetDisplayName(key));
        }

        foreach (var preset in _settings.Appearance.CustomFileListColorPresets)
        {
            _colorThemeCombo.Items.Add(FileListColorResolver.MakeUserPresetKey(preset.Name));
        }

        string target = FileListColorResolver.CanonicalizePresetKey(selectPresetKey ?? _settings.Appearance.ColorTheme);
        int idx = _colorThemeCombo.FindStringExact(FileListColorResolver.GetPresetDisplayName(target));
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

    private void ApplyDefaultFunctionColorsFromCurrentTheme()
    {
        if (_settings?.Appearance == null)
        {
            return;
        }
        string theme = _settings.Appearance.ColorTheme ?? string.Empty;
        if (string.Equals(theme, "MidFdStandard", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(theme, "WinFdCompatible", StringComparison.OrdinalIgnoreCase))
        {
            _settings.Appearance.CustomFunctionBarBackColor = null;
            _settings.Appearance.CustomFunctionBarForeColor = null;
            return;
        }

        FunctionPreviewPalette palette = ResolveDefaultFunctionPreviewPalette(BuildColorTabPreviewSettings());
        _settings.Appearance.CustomFunctionBarBackColor = UiThemeResolver.ToHexString(palette.ButtonBackColor);
        _settings.Appearance.CustomFunctionBarForeColor = UiThemeResolver.ToHexString(palette.ButtonForeColor);
    }

    private void ApplySelectedColorPresetToEditor(bool forceRefresh = true)
    {
        if (_suppressColorUiEvents || _colorThemeCombo == null) return;

        string selectedPreset = _colorThemeCombo.Text;
        string presetKey = FileListColorResolver.GetPresetKeyFromDisplayName(selectedPreset);
        _settings.Appearance.ColorTheme = FileListColorResolver.CanonicalizePresetKey(presetKey);
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
        _settings.Appearance.CustomFileListColors.StatusNormal = FileListColorResolver.ToHexColor(resolved.StatusNormal);
        _settings.Appearance.CustomFileListColors.StatusResult = FileListColorResolver.ToHexColor(resolved.StatusResult);
        _settings.Appearance.CustomFileListColors.StatusError = FileListColorResolver.ToHexColor(resolved.StatusError);
        ApplyDefaultFunctionColorsFromCurrentTheme();

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
        Color color = GetCurrentFieldColor(item);

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

        SetCurrentFieldColor(item, color.Value);
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

        SetCurrentFieldColor(item, color);
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
        Color curColor = GetCurrentFieldColor(item);

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

            SetCurrentFieldColor(item, cd.Color);
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

            if (FileListColorResolver.IsBuiltInPreset(targetName))
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
            var confirm = ShowColorPresetOverwriteConfirmationDialog(targetName);

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

    private DialogResult ShowColorPresetOverwriteConfirmationDialog(string targetName)
    {
        using (var form = new Form())
        {
            form.Text = "プリセット上書きの確認";
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.MaximizeBox = false;
            form.MinimizeBox = false;
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.CenterParent;
            form.ClientSize = new Size(400, 140);

            var label = new Label
            {
                Text = $"既存のユーザープリセット '{targetName}' を上書きしますか？",
                Location = new Point(15, 20),
                Size = new Size(370, 50),
                TextAlign = ContentAlignment.MiddleLeft
            };

            var btnYes = new Button
            {
                Text = "はい(&Y)",
                DialogResult = DialogResult.Yes,
                Location = new Point(190, 90),
                Size = new Size(90, 30)
            };

            var btnNo = new Button
            {
                Text = "いいえ(&N)",
                DialogResult = DialogResult.No,
                Location = new Point(290, 90),
                Size = new Size(90, 30)
            };

            form.Controls.Add(label);
            form.Controls.Add(btnYes);
            form.Controls.Add(btnNo);

            form.AcceptButton = btnYes;
            form.CancelButton = btnNo;

            return form.ShowDialog(this);
        }
    }

    private DialogResult ShowColorPresetDeleteConfirmationDialog(string userName)
    {
        using (var form = new Form())
        {
            form.Text = "プリセット削除の確認";
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.MaximizeBox = false;
            form.MinimizeBox = false;
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.CenterParent;
            form.ClientSize = new Size(400, 140);

            var label = new Label
            {
                Text = $"プリセット '{userName}' を削除しますか？",
                Location = new Point(15, 20),
                Size = new Size(370, 50),
                TextAlign = ContentAlignment.MiddleLeft
            };

            var btnYes = new Button
            {
                Text = "はい(&Y)",
                DialogResult = DialogResult.Yes,
                Location = new Point(190, 90),
                Size = new Size(90, 30)
            };

            var btnNo = new Button
            {
                Text = "いいえ(&N)",
                DialogResult = DialogResult.No,
                Location = new Point(290, 90),
                Size = new Size(90, 30)
            };

            form.Controls.Add(label);
            form.Controls.Add(btnYes);
            form.Controls.Add(btnNo);

            form.AcceptButton = btnYes;
            form.CancelButton = btnNo;

            return form.ShowDialog(this);
        }
    }

    private void DeletePresetButton_Click(object? sender, EventArgs e)
    {
        if (_suppressColorUiEvents)
        {
            return;
        }
        string current = _colorThemeCombo.Text;
        if (!FileListColorResolver.TryGetUserPresetName(current, out string? userName) || userName == null) return;

        var confirm = ShowColorPresetDeleteConfirmationDialog(userName);

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
        _settings.Appearance.CustomFileListColors.StatusNormal = FileListColorResolver.ToHexColor(resolved.StatusNormal);
        _settings.Appearance.CustomFileListColors.StatusResult = FileListColorResolver.ToHexColor(resolved.StatusResult);
        _settings.Appearance.CustomFileListColors.StatusError = FileListColorResolver.ToHexColor(resolved.StatusError);
        ApplyDefaultFunctionColorsFromCurrentTheme();

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
        _settings.Appearance.CustomFileListColors.StatusNormal = FileListColorResolver.ToHexColor(resolved.StatusNormal);
        _settings.Appearance.CustomFileListColors.StatusResult = FileListColorResolver.ToHexColor(resolved.StatusResult);
        _settings.Appearance.CustomFileListColors.StatusError = FileListColorResolver.ToHexColor(resolved.StatusError);
        ApplyDefaultFunctionColorsFromCurrentTheme();
        _fileListCustomColorsEnabledForSave = false;

        UpdateColorTabUiFromModel();
        UpdatePreview();
        ForceRefreshColorTabControls(); // リスト色見本を即時再描画 (OwnerDraw Invalidate漏れ修正)
    }

    private void UpdatePreview()
    {
        if (_suppressColorUiEvents || _fileListColorPreviewPanel == null || _colorThemeCombo == null)
        {
            return;
        }

        AppSettings dummySettings = BuildColorTabPreviewSettings();

        var resolved = FileListColorResolver.ResolveColors(dummySettings);
        _fileListColorPreviewPanel.BackColor = resolved.Background;
        _fileListColorPreviewPanel.Invalidate();

        if (_functionBarPreviewPanel != null)
        {
            FunctionPreviewPalette palette = ResolveFunctionPreviewPalette();
            _functionBarPreviewPanel.BackColor = palette.PanelBackColor;
            foreach (Control sample in _functionBarPreviewPanel.Controls)
            {
                sample.BackColor = palette.ButtonBackColor;
                sample.ForeColor = palette.ButtonForeColor;
                if (sample is Button button)
                {
                    button.FlatAppearance.BorderColor = palette.ButtonBorderColor;
                }
                sample.Invalidate();
            }
            _functionBarPreviewPanel.Invalidate();
        }

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
        dummySettings.Appearance.ColorTheme = FileListColorResolver.CanonicalizePresetKey(_colorThemeCombo.Text);

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

    private sealed record ColorFieldItem(string DisplayName, string PropertyName, bool IsFunctionColor = false);
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
        new ColorFieldItem("選択行文字色", nameof(CustomFileListColorSettings.SelectedForeground)),
        new ColorFieldItem("ステータス通常文字色", nameof(CustomFileListColorSettings.StatusNormal)),
        new ColorFieldItem("ステータス操作結果文字色", nameof(CustomFileListColorSettings.StatusResult)),
        new ColorFieldItem("ステータス警告/エラー文字色", nameof(CustomFileListColorSettings.StatusError)),
        new ColorFieldItem("ファンクション背景色", nameof(AppearanceSettings.CustomFunctionBarBackColor), true),
        new ColorFieldItem("ファンクション文字色", nameof(AppearanceSettings.CustomFunctionBarForeColor), true)
    };
}
