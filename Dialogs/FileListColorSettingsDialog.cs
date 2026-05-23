using System;
using System.Drawing;
using System.Windows.Forms;
using MidFD.Configuration;
using MidFD.Services;

namespace MidFD.Dialogs;

public class FileListColorSettingsDialog : Form
{
    private readonly AppSettings _settings;
    private readonly CustomFileListColorSettings _editingColors;
    private readonly string _themeKey;
    private bool _enableSemanticColorAssist;

    private ListView _previewListView = null!;
    private Label _presetDescriptionLabel = null!;
    private Label _assistStatusLabel = null!;
    private CheckBox _semanticAssistCheckBox = null!;
    private Button _btnReset = null!;
    private Button _btnOk = null!;
    private Button _btnCancel = null!;

    private Button _btnBg = null!;
    private Button _btnNormal = null!;
    private Button _btnDir = null!;
    private Button _btnReadOnly = null!;
    private Button _btnHidden = null!;
    private Button _btnSystem = null!;
    private Button _btnMarked = null!;
    private Button _btnSelBg = null!;
    private Button? _btnSelFg;

    public FileListColorSettingsDialog(AppSettings settings, string themeKey, bool enableSemanticColorAssist)
    {
        _settings = settings;
        _editingColors = settings.Appearance.CustomFileListColors.Clone();
        _themeKey = themeKey;
        _enableSemanticColorAssist = enableSemanticColorAssist;

        InitializeComponent();
        LoadColorsToUi();
        UpdatePreview();
    }

    private void InitializeComponent()
    {
        this.Text = "一覧表示の色カスタム設定";
        this.Size = new Size(760, 560);
        this.FormBorderStyle = FormBorderStyle.FixedDialog;
        this.MaximizeBox = false;
        this.MinimizeBox = false;
        this.StartPosition = FormStartPosition.CenterParent;

        var mainLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            Padding = new Padding(10)
        };
        mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 92F));
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 85F));
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45F));
        this.Controls.Add(mainLayout);

        // プリセット選択パネル
        var presetPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Height = 35
        };

        var lblPreset = new Label
        {
            Text = "プリセット:",
            Location = new Point(5, 8),
            Size = new Size(70, 20),
            TextAlign = ContentAlignment.MiddleLeft
        };

        var presetCombo = new ComboBox
        {
            Location = new Point(80, 6),
            Size = new Size(170, 24),
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        presetCombo.Items.AddRange(new object[] {
            "現在の表示色",
            "WinFD Classic Dark",
            "WinFD Classic Light",
            "High Contrast Dark",
            "High Contrast Light",
            "Terminal Green",
            "Amber Contrast"
        });
        presetCombo.SelectedIndex = 0;

        var btnLoadPreset = new Button
        {
            Text = "読み込み",
            Location = new Point(260, 5),
            Size = new Size(80, 26)
        };

        _semanticAssistCheckBox = new CheckBox
        {
            Text = "背景同化時のみ最小補正する",
            Location = new Point(355, 8),
            Size = new Size(210, 20),
            Checked = _enableSemanticColorAssist
        };

        _presetDescriptionLabel = new Label
        {
            Location = new Point(80, 36),
            Size = new Size(650, 18),
            AutoEllipsis = true,
            Text = FileListColorResolver.GetPresetDescription(_themeKey)
        };

        _assistStatusLabel = new Label
        {
            Location = new Point(80, 58),
            Size = new Size(650, 18),
            ForeColor = SystemColors.GrayText
        };

        presetCombo.SelectedIndexChanged += (s, e) =>
        {
            string selectedPreset = presetCombo.Text;
            _presetDescriptionLabel.Text = FileListColorResolver.GetPresetDescription(selectedPreset == "現在の表示色" ? _themeKey : selectedPreset);
        };

        btnLoadPreset.Click += (s, e) =>
        {
            string selectedPreset = presetCombo.Text;
            string targetTheme = selectedPreset == "現在の表示色" ? _themeKey : selectedPreset;
            ApplyPreset(targetTheme);
        };

        _semanticAssistCheckBox.CheckedChanged += (s, e) =>
        {
            _enableSemanticColorAssist = _semanticAssistCheckBox.Checked;
            UpdatePreview();
        };

        presetPanel.Controls.Add(lblPreset);
        presetPanel.Controls.Add(presetCombo);
        presetPanel.Controls.Add(btnLoadPreset);
        presetPanel.Controls.Add(_semanticAssistCheckBox);
        presetPanel.Controls.Add(_presetDescriptionLabel);
        presetPanel.Controls.Add(_assistStatusLabel);

        mainLayout.Controls.Add(presetPanel, 0, 0);
        mainLayout.SetColumnSpan(presetPanel, 2);

        var colorPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true
        };
        mainLayout.Controls.Add(colorPanel, 0, 1);

        _previewListView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.None,
            OwnerDraw = true,
            MultiSelect = false
        };
        _previewListView.Columns.Add("Name", 320);

        var items = new[]
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
        _previewListView.Items.AddRange(items);
        _previewListView.DrawSubItem += PreviewListView_DrawSubItem;

        mainLayout.Controls.Add(_previewListView, 1, 1);

        _btnBg = AddColorSelector(colorPanel, "一覧背景色", () => _editingColors.Background, c => _editingColors.Background = c);
        _btnNormal = AddColorSelector(colorPanel, "通常ファイル文字色", () => _editingColors.NormalFile, c => _editingColors.NormalFile = c);
        _btnDir = AddColorSelector(colorPanel, "ディレクトリ文字色", () => _editingColors.Directory, c => _editingColors.Directory = c);
        _btnReadOnly = AddColorSelector(colorPanel, "ReadOnly文字色", () => _editingColors.ReadOnly, c => _editingColors.ReadOnly = c);
        _btnHidden = AddColorSelector(colorPanel, "Hidden文字色", () => _editingColors.Hidden, c => _editingColors.Hidden = c);
        _btnSystem = AddColorSelector(colorPanel, "System文字色", () => _editingColors.System, c => _editingColors.System = c);
        _btnMarked = AddColorSelector(colorPanel, "マーク記号色", () => _editingColors.Marked, c => _editingColors.Marked = c);
        _btnSelBg = AddColorSelector(colorPanel, "選択行背景色", () => _editingColors.SelectedBackground, c => _editingColors.SelectedBackground = c);
        // UIから「選択行文字色」を非表示にするため、生成・追加を行わない
        _btnSelFg = null;

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 5, 0, 0)
        };
        mainLayout.Controls.Add(buttonPanel, 0, 2);
        mainLayout.SetColumnSpan(buttonPanel, 2);

        _btnCancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, Size = new Size(85, 28) };
        _btnOk = new Button { Text = "OK", DialogResult = DialogResult.OK, Size = new Size(85, 28) };
        _btnReset = new Button { Text = "現在のテーマ色に戻す", Size = new Size(150, 28) };

        _btnOk.Click += (s, e) => SaveColorsFromUi();
        _btnReset.Click += (s, e) => ResetToDefault();

        buttonPanel.Controls.Add(_btnCancel);
        buttonPanel.Controls.Add(_btnOk);
        buttonPanel.Controls.Add(_btnReset);
    }

    private void ApplyPreset(string themeKey)
    {
        var resolved = FileListColorResolver.ResolvePresetColors(themeKey);

        _editingColors.Background = FileListColorResolver.ToHexColor(resolved.Background);
        _editingColors.NormalFile = FileListColorResolver.ToHexColor(resolved.NormalFile);
        _editingColors.Directory = FileListColorResolver.ToHexColor(resolved.Directory);
        _editingColors.ReadOnly = FileListColorResolver.ToHexColor(resolved.ReadOnly);
        _editingColors.Hidden = FileListColorResolver.ToHexColor(resolved.Hidden);
        _editingColors.System = FileListColorResolver.ToHexColor(resolved.System);
        _editingColors.Marked = FileListColorResolver.ToHexColor(resolved.Marked);
        _editingColors.SelectedBackground = FileListColorResolver.ToHexColor(resolved.SelectedBackground);
        _editingColors.SelectedForeground = FileListColorResolver.ToHexColor(resolved.SelectedForeground);

        LoadColorsToUi();
        UpdatePreview();
    }

    private Button AddColorSelector(FlowLayoutPanel panel, string labelText, Func<string?> getter, Action<string?> setter)
    {
        var row = new Panel { Size = new Size(280, 32) };
        var label = new Label { Text = labelText, Location = new Point(5, 8), Size = new Size(130, 20) };
        var colorButton = new Button { Location = new Point(140, 3), Size = new Size(130, 26), FlatStyle = FlatStyle.Flat };

        colorButton.Click += (s, e) =>
        {
            using (var cd = new ColorDialog())
            {
                var curHex = getter();
                if (!string.IsNullOrEmpty(curHex) && FileListColorResolver.ParseHexColor(curHex) is Color curColor)
                {
                    cd.Color = curColor;
                }
                cd.FullOpen = true;
                if (cd.ShowDialog() == DialogResult.OK)
                {
                    setter(FileListColorResolver.ToHexColor(cd.Color));
                    SetButtonColor(colorButton, FileListColorResolver.ToHexColor(cd.Color), cd.Color);
                    UpdatePreview();
                }
            }
        };

        row.Controls.Add(label);
        row.Controls.Add(colorButton);
        panel.Controls.Add(row);

        return colorButton;
    }

    private void LoadColorsToUi()
    {
        var tempSettings = _settings.Clone();
        tempSettings.Appearance.UseCustomFileListColors = false;
        tempSettings.Appearance.ColorTheme = FileListColorResolver.NormalizeCoreTheme(_themeKey, _settings);
        var defaultColors = FileListColorResolver.ResolveColors(tempSettings);

        SetButtonColor(_btnBg, _editingColors.Background, defaultColors.Background);
        SetButtonColor(_btnNormal, _editingColors.NormalFile, defaultColors.NormalFile);
        SetButtonColor(_btnDir, _editingColors.Directory, defaultColors.Directory);
        SetButtonColor(_btnReadOnly, _editingColors.ReadOnly, defaultColors.ReadOnly);
        SetButtonColor(_btnHidden, _editingColors.Hidden, defaultColors.Hidden);
        SetButtonColor(_btnSystem, _editingColors.System, defaultColors.System);
        SetButtonColor(_btnMarked, _editingColors.Marked, defaultColors.Marked);
        SetButtonColor(_btnSelBg, _editingColors.SelectedBackground, defaultColors.SelectedBackground);
        if (_btnSelFg != null)
        {
            SetButtonColor(_btnSelFg, _editingColors.SelectedForeground, defaultColors.SelectedForeground);
        }
    }

    private void SetButtonColor(Button btn, string? customHex, Color fallbackColor)
    {
        var color = FileListColorResolver.ParseHexColor(customHex) ?? fallbackColor;
        btn.BackColor = color;
        double contrastWithWhite = FileListColorResolver.GetContrastRatio(color, Color.White);
        double contrastWithBlack = FileListColorResolver.GetContrastRatio(color, Color.Black);
        btn.ForeColor = contrastWithWhite >= contrastWithBlack ? Color.White : Color.Black;
        btn.Text = FileListColorResolver.ToHexColor(color);
    }

    private void UpdatePreview()
    {
        var dummySettings = _settings.Clone();
        dummySettings.Appearance.UseCustomFileListColors = true;
        dummySettings.Appearance.EnableSemanticColorAssist = _enableSemanticColorAssist;
        dummySettings.Appearance.ColorTheme = FileListColorResolver.NormalizeCoreTheme(_themeKey, _settings);
        dummySettings.Appearance.CustomFileListColors = _editingColors;

        var resolved = FileListColorResolver.ResolveColors(dummySettings);
        _previewListView.BackColor = resolved.Background;
        _previewListView.Invalidate();

        var noAssistSettings = _settings.Clone();
        noAssistSettings.Appearance.UseCustomFileListColors = true;
        noAssistSettings.Appearance.EnableSemanticColorAssist = false;
        noAssistSettings.Appearance.ColorTheme = FileListColorResolver.NormalizeCoreTheme(_themeKey, _settings);
        noAssistSettings.Appearance.CustomFileListColors = _editingColors;
        var noAssistResolved = FileListColorResolver.ResolveColors(noAssistSettings);

        bool assistAdjusted =
            resolved.NormalFile != noAssistResolved.NormalFile ||
            resolved.Directory != noAssistResolved.Directory ||
            resolved.ReadOnly != noAssistResolved.ReadOnly ||
            resolved.Hidden != noAssistResolved.Hidden ||
            resolved.System != noAssistResolved.System ||
            resolved.Marked != noAssistResolved.Marked;

        _assistStatusLabel.Text = _enableSemanticColorAssist
            ? (assistAdjusted ? "補正あり: 背景と同化しやすい色だけ最小補正しています。" : "補正なし: 現在の配色はそのまま表示できます。")
            : "自動補正は無効です。";
    }

    private void PreviewListView_DrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        var item = e.Item;
        if (item == null) return;

        var dummySettings = _settings.Clone();
        dummySettings.Appearance.UseCustomFileListColors = true;
        dummySettings.Appearance.EnableSemanticColorAssist = _enableSemanticColorAssist;
        dummySettings.Appearance.ColorTheme = _themeKey;
        dummySettings.Appearance.CustomFileListColors = _editingColors;

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
            // marked の場合、ファイル名本体の色は normalFile の色を維持する
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

            // マーク記号 * の描画
            TextRenderer.DrawText(e.Graphics, "*", item.Font, markRect, markColor, flags);
            // ファイル名本体の描画
            TextRenderer.DrawText(e.Graphics, item.Text, item.Font, textBounds, foreColor, flags);
        }
        else
        {
            Rectangle textBounds = new Rectangle(e.Bounds.X + 4, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, item.Text, item.Font, textBounds, foreColor, flags);
        }
    }

    private void SaveColorsFromUi()
    {
        _settings.Appearance.CustomFileListColors = _editingColors.Clone();
        _settings.Appearance.EnableSemanticColorAssist = _enableSemanticColorAssist;
        _settings.Appearance.UseCustomFileListColors = true;
    }

    private void ResetToDefault()
    {
        ApplyPreset(_themeKey);
    }
}
