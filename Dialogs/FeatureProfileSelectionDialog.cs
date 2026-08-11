using MidFD.Configuration;
using MidFD.Models;
using MidFD.Services;

namespace MidFD.Dialogs;

public sealed class FeatureProfileSelectionDialog : Form
{
    private readonly CheckBox _restoreStartupStateCheckBox;
    private readonly CheckBox _enableMouseGesturesCheckBox;
    private readonly CheckBox _showFunctionBarTooltipsCheckBox;
    private readonly CheckBox _enableDragArchiveHandoffCheckBox;
    private readonly CheckBox _includeDragZipManifestCheckBox;
    private readonly CheckBox _videoEnterPlaysExternalCheckBox;
    private readonly CheckBox _showPathAsBreadcrumbCheckBox;
    private readonly CheckBox _useMidFdManagedTrashCheckBox;
    private readonly CheckBox _clipboardPasteTextAsFileCheckBox;
    private readonly RadioButton _standardInputRadioButton;
    private readonly RadioButton _fdCompatibleInputRadioButton;
    private readonly RadioButton _verticalTabLayoutRadioButton;
    private readonly RadioButton _horizontalTabLayoutRadioButton;
    private readonly RadioButton _basicRangeRadioButton;
    private readonly RadioButton _convenientRangeRadioButton;
    private readonly RadioButton _allRangeRadioButton;
    private readonly TextBox _sevenZipPathBox;
    private readonly TextBox _videoToolDirectoryBox;
    private readonly TextBox _externalEditorPathBox;
    private readonly ComboBox _colorThemeComboBox;
    private readonly Label _rangeStateLabel;
    private readonly Label _videoToolStatusLabel;
    private readonly Label _sevenZipStatusLabel;
    private readonly Label _externalEditorStatusLabel;
    private bool _updatingRange;
    private bool _initializingControls;

    public FeatureProfile SelectedProfile { get; private set; } = FeatureProfile.PracticalStable;
    public bool UseFdCompatibleFunctionKeys => _fdCompatibleInputRadioButton.Checked;
    public bool VideoEnterPlaysExternal => _videoEnterPlaysExternalCheckBox.Checked;
    public bool RestoreStartupState => _restoreStartupStateCheckBox.Checked;
    public bool EnableMouseGestures => _enableMouseGesturesCheckBox.Checked;
    public bool ShowFunctionBarTooltips => _showFunctionBarTooltipsCheckBox.Checked;
    public bool EnableDragArchiveHandoff => _enableDragArchiveHandoffCheckBox.Checked;
    public bool IncludeDragZipManifest => _includeDragZipManifestCheckBox.Checked;
    public bool ShowPathAsBreadcrumb => _showPathAsBreadcrumbCheckBox.Checked;
    public bool UseMidFdManagedTrash => _useMidFdManagedTrashCheckBox.Checked;
    public bool ClipboardPasteTextAsFileEnabled => _clipboardPasteTextAsFileCheckBox.Checked;
    public string ColorTheme => FileListColorResolver.GetPresetKeyFromDisplayName(_colorThemeComboBox.Text);
    public BrowserTabLayoutMode LayoutMode => _verticalTabLayoutRadioButton.Checked
        ? BrowserTabLayoutMode.Vertical
        : BrowserTabLayoutMode.Horizontal;
    public string? SevenZipPath => NullIfEmpty(_sevenZipPathBox.Text);
    public string? VideoToolDirectory => NullIfEmpty(_videoToolDirectoryBox.Text);
    public string? ExternalEditorPath => NullIfEmpty(_externalEditorPathBox.Text);

    public FeatureProfileSelectionDialog(AppSettings? settings = null, bool isFirstLaunch = true)
    {
        Text = isFirstLaunch ? "MidFD 初回セットアップ" : "MidFD 基本セットアップ";
        StartPosition = isFirstLaunch ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(780, 740);
        Padding = new Padding(8);

        SelectedProfile = ResolveInitialProfile(settings);
        InputSettings? input = settings?.Input;
        FileOperationsSettings? fileOperations = settings?.FileOperations;
        PreviewSettings? preview = settings?.Preview;
        AppearanceSettings? appearance = settings?.Appearance;
        BrowserTabSettings? browserTabs = settings?.BrowserTabs;
        SevenZipSettings? sevenZip = settings?.SevenZip;
        ExternalToolsSettings? externalTools = settings?.ExternalTools;
        var bodyPanel = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = false,
            AutoScrollMinSize = Size.Empty,
            Padding = Padding.Empty
        };

        bodyPanel.Controls.Add(new Label
        {
            Text = "使用する機能の範囲を選択してください。",
            AutoSize = false,
            Location = new Point(16, 14),
            Size = new Size(780, 24),
            Font = new Font(Font, FontStyle.Bold)
        });
        bodyPanel.Controls.Add(new Label
        {
            Text = "選択した範囲は下の各セグメントへ反映されます。個別に変更すると「個別設定」になります。",
            AutoSize = false,
            Location = new Point(16, 42),
            Size = new Size(700, 24)
        });

        var rangeGroup = new GroupBox { Text = "機能範囲", Location = new Point(16, 74), Size = new Size(700, 96) };
        _basicRangeRadioButton = AddRadio(rangeGroup, "基本機能のみ", 18, 22);
        _convenientRangeRadioButton = AddRadio(rangeGroup, "便利機能まで使う（推奨）", 18, 50);
        _allRangeRadioButton = AddRadio(rangeGroup, "すべての機能を使う", 280, 22);
        rangeGroup.Controls.Add(new Label { Text = "注意が必要な外部受け渡し機能も有効になります。", AutoSize = true, Location = new Point(298, 50) });
        bodyPanel.Controls.Add(rangeGroup);

        var basicGroup = new GroupBox { Text = "基本機能                         ON", Location = new Point(16, 182), Size = new Size(340, 100) };
        basicGroup.Controls.Add(new Label { Text = "ファイル閲覧・コピー・移動・名前変更・Mark\r\n標準のキー操作・内部Viewer", AutoSize = false, Location = new Point(18, 28), Size = new Size(340, 50) });
        bodyPanel.Controls.Add(basicGroup);

        var convenientGroup = new GroupBox { Text = "便利機能", Location = new Point(376, 182), Size = new Size(340, 224) };
        _restoreStartupStateCheckBox = AddCheckBox(convenientGroup, "前回の状態を復元", 18, 28, isFirstLaunch ? true : settings?.Session?.RestoreStartupState ?? true);
        _enableMouseGesturesCheckBox = AddCheckBox(convenientGroup, "マウスジェスチャー", 18, 58, isFirstLaunch ? true : input?.EnableMouseGestures ?? true);
        _showFunctionBarTooltipsCheckBox = AddCheckBox(convenientGroup, "Functionバーの説明", 18, 88, isFirstLaunch ? true : input?.ShowFunctionBarTooltips ?? true);
        _videoEnterPlaysExternalCheckBox = AddCheckBox(convenientGroup, "メディアファイルのEnter外部再生", 18, 118, isFirstLaunch ? true : preview?.VideoEnterPlaysExternal ?? false);
        _showPathAsBreadcrumbCheckBox = AddCheckBox(convenientGroup, "パスをパンくず形式で表示", 18, 148, isFirstLaunch ? true : appearance?.ShowPathAsBreadcrumb ?? false);
        convenientGroup.Controls.Add(new Label { Text = "外部アプリは、設定済みのパスまたは\r\n自動検出したツールを使用します。", AutoSize = false, Location = new Point(18, 176), Size = new Size(300, 42) });
        bodyPanel.Controls.Add(convenientGroup);

        var cautionGroup = new GroupBox { Text = "注意が必要な機能", Location = new Point(16, 294), Size = new Size(340, 220) };
        _useMidFdManagedTrashCheckBox = AddCheckBox(cautionGroup, "MidFD管理ゴミ箱", 18, 28, fileOperations?.UseMidFdManagedTrash ?? false);
        _enableDragArchiveHandoffCheckBox = AddCheckBox(cautionGroup, "Drag ZIP", 18, 58, fileOperations?.EnableDragArchiveHandoff ?? false);
        _includeDragZipManifestCheckBox = AddCheckBox(cautionGroup, "内容一覧manifestの同梱", 18, 88, fileOperations?.IncludeDragZipManifest ?? false);
        _clipboardPasteTextAsFileCheckBox = AddCheckBox(cautionGroup, "クリップボードのテキストをファイルとして貼り付ける", 18, 118, fileOperations?.ClipboardPasteTextAsFileEnabled ?? false);
        cautionGroup.Controls.Add(new Label { Text = "通常削除したファイルをMidFD管理ゴミ箱へ移し、\r\nCtrl+Zで復元できるようにします。\r\nテキスト貼り付けは通常OFFを推奨します。", AutoSize = false, Location = new Point(18, 146), Size = new Size(300, 64) });
        bodyPanel.Controls.Add(cautionGroup);

        var operationGroup = new GroupBox { Text = "操作方式", Location = new Point(376, 400), Size = new Size(340, 62) };
        _standardInputRadioButton = AddRadio(operationGroup, "MidFD標準", 18, 24);
        _fdCompatibleInputRadioButton = AddRadio(operationGroup, "FD／WinFD互換", 160, 24);
        _fdCompatibleInputRadioButton.Checked = string.Equals(input?.FunctionKeyProfile, InputSettings.FdCompatibleProfileValue, StringComparison.OrdinalIgnoreCase);
        _standardInputRadioButton.Checked = !_fdCompatibleInputRadioButton.Checked;
        bodyPanel.Controls.Add(operationGroup);

        var displayGroup = new GroupBox { Text = "表示", Location = new Point(376, 466), Size = new Size(340, 104) };
        displayGroup.Controls.Add(new Label { Text = "タブ表示", AutoSize = true, Location = new Point(18, 20) });
        _verticalTabLayoutRadioButton = AddRadio(displayGroup, "縦型（推奨）", 18, 44);
        _horizontalTabLayoutRadioButton = AddRadio(displayGroup, "横型", 160, 44);
        bool useVerticalTabs = isFirstLaunch
            ? true
            : browserTabs?.LayoutMode == BrowserTabLayoutMode.Vertical;
        _verticalTabLayoutRadioButton.Checked = useVerticalTabs;
        _horizontalTabLayoutRadioButton.Checked = !useVerticalTabs;
        displayGroup.Controls.Add(new Label { Text = "配色プリセット", AutoSize = true, Location = new Point(18, 78) });
        _colorThemeComboBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(108, 71), Size = new Size(210, 28) };
        foreach (string key in FileListColorResolver.BuiltInPresetKeys)
        {
            _colorThemeComboBox.Items.Add(FileListColorResolver.GetPresetDisplayName(key));
        }
        foreach (CustomFileListColorPreset preset in appearance?.CustomFileListColorPresets ?? new List<CustomFileListColorPreset>())
        {
            _colorThemeComboBox.Items.Add(preset.Name);
        }
        string currentTheme = FileListColorResolver.CanonicalizePresetKey(appearance?.ColorTheme);
        int themeIndex = _colorThemeComboBox.Items.IndexOf(FileListColorResolver.GetPresetDisplayName(currentTheme));
        _colorThemeComboBox.SelectedIndex = themeIndex >= 0 ? themeIndex : 0;
        displayGroup.Controls.Add(_colorThemeComboBox);
        bodyPanel.Controls.Add(displayGroup);

        var externalGroup = new GroupBox { Text = "外部アプリ", Location = new Point(16, 576), Size = new Size(700, 154) };
        int externalRowTop = 13;
        AddLabel(externalGroup, "7-Zip", 18, externalRowTop + 4, 120);
        _sevenZipPathBox = AddReadOnlyTextBox(externalGroup, 142, externalRowTop, 440, sevenZip?.ExePath ?? string.Empty);
        AddBrowseFileButton(externalGroup, 592, externalRowTop - 1, 90, _sevenZipPathBox, "7-Zip実行ファイルを選択");
        _sevenZipStatusLabel = AddStatus(externalGroup, string.Empty, 142, _sevenZipPathBox.Bottom + 2, 440);
        externalRowTop = _sevenZipStatusLabel.Bottom + 2;

        AddLabel(externalGroup, "動画ツール", 18, externalRowTop + 4, 120);
        _videoToolDirectoryBox = AddReadOnlyTextBox(externalGroup, 142, externalRowTop, 440, preview?.VideoToolDirectory ?? string.Empty);
        AddBrowseFolderButton(externalGroup, 592, externalRowTop - 1, 90, _videoToolDirectoryBox, "動画ツールフォルダを選択");
        _videoToolStatusLabel = AddStatus(externalGroup, string.Empty, 142, _videoToolDirectoryBox.Bottom + 2, 440);
        externalRowTop = _videoToolStatusLabel.Bottom + 2;

        AddLabel(externalGroup, "外部エディタ", 18, externalRowTop + 4, 120);
        _externalEditorPathBox = AddReadOnlyTextBox(externalGroup, 142, externalRowTop, 440, externalTools?.ExternalEditorPath ?? string.Empty);
        AddBrowseFileButton(externalGroup, 592, externalRowTop - 1, 90, _externalEditorPathBox, "外部エディタ実行ファイルを選択");
        _externalEditorStatusLabel = AddStatus(externalGroup, string.Empty, 142, _externalEditorPathBox.Bottom + 2, 440);
        externalGroup.Height = _externalEditorStatusLabel.Bottom + 16;
        ClientSize = new Size(ClientSize.Width, Math.Max(ClientSize.Height, externalGroup.Bottom + 56));
        bodyPanel.Controls.Add(externalGroup);

        var footer = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4) };
        _rangeStateLabel = new Label
        {
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = string.Empty
        };
        var footerButtons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        var cancelButton = new Button { Text = "キャンセル", Size = new Size(110, 32), DialogResult = DialogResult.Cancel };
        var okButton = new Button { Text = isFirstLaunch ? "この設定で開始" : "設定画面へ反映", Size = new Size(180, 32), DialogResult = DialogResult.OK };
        footerButtons.Controls.Add(okButton);
        footerButtons.Controls.Add(cancelButton);
        footer.Controls.Add(_rangeStateLabel);
        footer.Controls.Add(footerButtons);
        var rootLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48F));
        rootLayout.Controls.Add(bodyPanel, 0, 0);
        rootLayout.Controls.Add(footer, 0, 1);
        Controls.Add(rootLayout);
        AcceptButton = okButton;
        CancelButton = cancelButton;

        _initializingControls = true;
        _basicRangeRadioButton.CheckedChanged += (_, _) => ApplyRangeFromRadio(0);
        _convenientRangeRadioButton.CheckedChanged += (_, _) => ApplyRangeFromRadio(1);
        _allRangeRadioButton.CheckedChanged += (_, _) => ApplyRangeFromRadio(2);
        foreach (CheckBox checkBox in new[] { _restoreStartupStateCheckBox, _enableMouseGesturesCheckBox, _showFunctionBarTooltipsCheckBox, _videoEnterPlaysExternalCheckBox, _showPathAsBreadcrumbCheckBox, _useMidFdManagedTrashCheckBox, _enableDragArchiveHandoffCheckBox, _includeDragZipManifestCheckBox, _clipboardPasteTextAsFileCheckBox }) checkBox.CheckedChanged += (_, _) => RefreshRangeState();
        _standardInputRadioButton.CheckedChanged += (_, _) => { if (_standardInputRadioButton.Checked) ApplyInputModeColor(false); };
        _fdCompatibleInputRadioButton.CheckedChanged += (_, _) => { if (_fdCompatibleInputRadioButton.Checked) ApplyInputModeColor(true); };
        _enableDragArchiveHandoffCheckBox.CheckedChanged += (_, _) => _includeDragZipManifestCheckBox.Enabled = _enableDragArchiveHandoffCheckBox.Checked;
        _videoToolDirectoryBox.TextChanged += (_, _) => RefreshExternalStatuses();
        _sevenZipPathBox.TextChanged += (_, _) => RefreshExternalStatuses();
        _externalEditorPathBox.TextChanged += (_, _) => RefreshExternalStatuses();

        int rangeIndex = ResolveRangeIndex();
        _updatingRange = true;
        if (rangeIndex == 0) _basicRangeRadioButton.Checked = true;
        else if (rangeIndex == 1) _convenientRangeRadioButton.Checked = true;
        else if (rangeIndex == 2) _allRangeRadioButton.Checked = true;
        _updatingRange = false;
        _includeDragZipManifestCheckBox.Enabled = _enableDragArchiveHandoffCheckBox.Checked;
        RefreshRangeState();
        RefreshExternalStatuses();
        _initializingControls = false;
        Shown += (_, _) => { Activate(); BringToFront(); };
    }

    private int ResolveRangeIndex()
    {
        bool basic = !_restoreStartupStateCheckBox.Checked && !_enableMouseGesturesCheckBox.Checked && _showFunctionBarTooltipsCheckBox.Checked && !_videoEnterPlaysExternalCheckBox.Checked && !_showPathAsBreadcrumbCheckBox.Checked && !_useMidFdManagedTrashCheckBox.Checked && !_enableDragArchiveHandoffCheckBox.Checked && !_includeDragZipManifestCheckBox.Checked && !_clipboardPasteTextAsFileCheckBox.Checked;
        bool convenient = _restoreStartupStateCheckBox.Checked && _enableMouseGesturesCheckBox.Checked && _showFunctionBarTooltipsCheckBox.Checked && _videoEnterPlaysExternalCheckBox.Checked && _showPathAsBreadcrumbCheckBox.Checked && !_useMidFdManagedTrashCheckBox.Checked && !_enableDragArchiveHandoffCheckBox.Checked && !_includeDragZipManifestCheckBox.Checked && !_clipboardPasteTextAsFileCheckBox.Checked;
        bool all = _restoreStartupStateCheckBox.Checked && _enableMouseGesturesCheckBox.Checked && _showFunctionBarTooltipsCheckBox.Checked && _videoEnterPlaysExternalCheckBox.Checked && _showPathAsBreadcrumbCheckBox.Checked && _useMidFdManagedTrashCheckBox.Checked && _enableDragArchiveHandoffCheckBox.Checked && _includeDragZipManifestCheckBox.Checked && _clipboardPasteTextAsFileCheckBox.Checked;
        return basic ? 0 : convenient ? 1 : all ? 2 : -1;
    }

    private void ApplyRangeFromRadio(int index)
    {
        if (_updatingRange) return;
        if ((index == 0 && !_basicRangeRadioButton.Checked) || (index == 1 && !_convenientRangeRadioButton.Checked) || (index == 2 && !_allRangeRadioButton.Checked)) return;
        SyncSelectedProfile(index);
        _updatingRange = true;
        _restoreStartupStateCheckBox.Checked = index > 0;
        _enableMouseGesturesCheckBox.Checked = index > 0;
        _showFunctionBarTooltipsCheckBox.Checked = true;
        _videoEnterPlaysExternalCheckBox.Checked = index > 0;
        _showPathAsBreadcrumbCheckBox.Checked = index > 0;
        _useMidFdManagedTrashCheckBox.Checked = index == 2;
        _enableDragArchiveHandoffCheckBox.Checked = index == 2;
        _includeDragZipManifestCheckBox.Checked = index == 2;
        _clipboardPasteTextAsFileCheckBox.Checked = index == 2;
        _includeDragZipManifestCheckBox.Enabled = index == 2;
        _updatingRange = false;
        RefreshRangeState();
    }

    private void RefreshRangeState()
    {
        if (_updatingRange) return;
        int index = ResolveRangeIndex();
        _updatingRange = true;
        if (index < 0) { _basicRangeRadioButton.Checked = false; _convenientRangeRadioButton.Checked = false; _allRangeRadioButton.Checked = false; }
        else if (index == 0) _basicRangeRadioButton.Checked = true;
        else if (index == 1) _convenientRangeRadioButton.Checked = true;
        else _allRangeRadioButton.Checked = true;
        _updatingRange = false;
        if (!_initializingControls && index >= 0) SyncSelectedProfile(index);
        _rangeStateLabel.Text = index < 0 ? "現在は個別設定です。構成を選ぶと一括変更します。" : string.Empty;
    }

    private void SyncSelectedProfile(int index)
    {
        SelectedProfile = index switch
        {
            0 => FeatureProfile.MinimalCore,
            1 => FeatureProfile.PracticalStable,
            2 => FeatureProfile.Full,
            _ => SelectedProfile
        };
    }

    private void RefreshExternalStatuses()
    {
        string? sevenZipPath = SevenZipService.ResolveExecutable(NullIfEmpty(_sevenZipPathBox.Text));
        _sevenZipStatusLabel.Text = sevenZipPath == null ? "自動探索／fallback: 未検出" : $"自動検出済み: {Path.GetFileName(sevenZipPath)}";
        VideoToolResolutionResult video = VideoToolResolutionService.Resolve(NullIfEmpty(_videoToolDirectoryBox.Text));
        _videoToolStatusLabel.Text = $"ffmpeg {OnOff(video.FfmpegFound)}／ffplay {OnOff(video.FfplayFound)}";
        string? editorPath = NullIfEmpty(_externalEditorPathBox.Text);
        _externalEditorStatusLabel.Text = !string.IsNullOrEmpty(editorPath) && File.Exists(editorPath)
            ? $"検出済み: {Path.GetFileName(editorPath)}"
            : "未検出（未設定時はnotepad.exeを使用）";
    }

    private void ApplyInputModeColor(bool fdCompatible)
    {
        if (_initializingControls) return;
        string targetKey = FileListColorResolver.CanonicalizePresetKey(fdCompatible ? "WinFdCompatible" : "MidFdStandard");
        for (int index = 0; index < FileListColorResolver.BuiltInPresetKeys.Length; index++)
        {
            string builtInKey = FileListColorResolver.CanonicalizePresetKey(FileListColorResolver.BuiltInPresetKeys[index]);
            if (!string.Equals(builtInKey, targetKey, StringComparison.OrdinalIgnoreCase)) continue;
            if (_colorThemeComboBox.SelectedIndex != index) _colorThemeComboBox.SelectedIndex = index;
            return;
        }
    }

    private static string OnOff(bool value) => value ? "検出済み" : "未検出";
    private static CheckBox AddCheckBox(Control parent, string text, int x, int y, bool value) { var box = new CheckBox { Text = text, AutoSize = true, Location = new Point(x, y), Checked = value }; parent.Controls.Add(box); return box; }
    private static RadioButton AddRadio(Control parent, string text, int x, int y) { var radio = new RadioButton { Text = text, AutoSize = true, Location = new Point(x, y) }; parent.Controls.Add(radio); return radio; }
    private static Label AddStatus(Control parent, string text, int x, int y, int width)
    {
        int height = TextRenderer.MeasureText("検出済み", parent.Font).Height + 4;
        var label = new Label { Text = text, AutoSize = false, Location = new Point(x, y), Size = new Size(width, height), ForeColor = Color.DimGray };
        parent.Controls.Add(label);
        return label;
    }
    private static void AddLabel(Control parent, string text, int x, int y, int width) => parent.Controls.Add(new Label { Text = text, Location = new Point(x, y), Size = new Size(width, 23) });
    private static TextBox AddReadOnlyTextBox(Control parent, int x, int y, int width, string value)
    {
        int height = TextRenderer.MeasureText("Ag", parent.Font).Height + 8;
        var box = new TextBox { AutoSize = false, Location = new Point(x, y), Size = new Size(width, height), Text = value, ReadOnly = true, TextAlign = HorizontalAlignment.Right };
        parent.Controls.Add(box);
        return box;
    }
    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static FeatureProfile ResolveInitialProfile(AppSettings? settings) => FeatureProfileService.TryResolveProfile(settings?.Profile, out FeatureProfile profile) ? profile : FeatureProfile.PracticalStable;

    private static void AddBrowseFileButton(Control parent, int x, int y, int width, TextBox target, string title)
    {
        var button = new Button { Text = "変更...", Location = new Point(x, y), Size = new Size(width, 25) };
        button.Click += (_, _) => { using var dialog = new OpenFileDialog { Title = title, Filter = "実行ファイル|*.exe|すべてのファイル|*.*", CheckFileExists = true }; if (dialog.ShowDialog(parent.FindForm()) == DialogResult.OK) target.Text = dialog.FileName; };
        parent.Controls.Add(button);
    }

    private static void AddBrowseFolderButton(Control parent, int x, int y, int width, TextBox target, string description)
    {
        var button = new Button { Text = "変更...", Location = new Point(x, y), Size = new Size(width, 25) };
        button.Click += (_, _) => { using var dialog = new FolderBrowserDialog { Description = description, ShowNewFolderButton = false }; if (dialog.ShowDialog(parent.FindForm()) == DialogResult.OK) target.Text = dialog.SelectedPath; };
        parent.Controls.Add(button);
    }
}
