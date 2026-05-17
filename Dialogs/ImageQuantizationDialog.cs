using MidFD.Services;
using MidFD.Helpers;

namespace MidFD.Dialogs;

public sealed class ImageQuantizationDialog : Form
{
    private readonly ComboBox _colorCountCombo = new();
    private readonly NumericUpDown _customColorCount = new();
    private readonly ComboBox _ditherCombo = new();
    private readonly ComboBox _mergeCombo = new();
    private readonly Label _ditherHint = new();

    public QuantizationRequest? ResultRequest { get; private set; }
    public string ResultLabel { get; private set; } = string.Empty;

    public ImageQuantizationDialog()
    {
        Text = "減色";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ClientSize = new Size(560, 250);
        AutoScaleMode = AutoScaleMode.Font;

        BuildControls();
        UpdateControlState();
    }

    private void BuildControls()
    {
        int labelX = 20;
        int inputX = 120;
        int top = 22;
        int rowH = 42;

        Controls.Add(new Label { Text = "色数:", Left = labelX, Top = top + 4, Width = 90 });
        _colorCountCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _colorCountCombo.Items.AddRange(new object[] { "65536色", "256色", "16色", "2色", "色数指定" });
        _colorCountCombo.SelectedIndex = 1;
        _colorCountCombo.Left = inputX;
        _colorCountCombo.Top = top;
        _colorCountCombo.Width = 140;
        _colorCountCombo.SelectedIndexChanged += (_, _) => UpdateControlState();
        Controls.Add(_colorCountCombo);

        _customColorCount.Left = inputX + 154;
        _customColorCount.Top = top;
        _customColorCount.Width = 90;
        _customColorCount.Minimum = 2;
        _customColorCount.Maximum = 256;
        _customColorCount.Value = 64;
        Controls.Add(_customColorCount);
        top += rowH;

        Controls.Add(new Label { Text = "ディザ:", Left = labelX, Top = top + 4, Width = 90 });
        _ditherCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _ditherCombo.Items.AddRange(new object[]
        {
            "なし",
            "自然",
            "階調優先",
            "レトロ"
        });
        _ditherCombo.SelectedIndex = 0;
        _ditherCombo.Left = inputX;
        _ditherCombo.Top = top;
        _ditherCombo.Width = 140; // 短縮
        _ditherCombo.SelectedIndexChanged += (_, _) => UpdateDitherHint();
        Controls.Add(_ditherCombo);

        _ditherHint.Left = inputX;
        _ditherHint.Top = top + 28;
        _ditherHint.Width = 400;
        _ditherHint.Height = 30;
        _ditherHint.ForeColor = SystemColors.GrayText;
        Controls.Add(_ditherHint);
        top += rowH + 18;

        Controls.Add(new Label { Text = "色の統合:", Left = labelX, Top = top + 4, Width = 90 });
        _mergeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _mergeCombo.Items.AddRange(new object[] { "なし", "弱", "中", "強" });
        _mergeCombo.SelectedIndex = 0;
        _mergeCombo.Left = inputX;
        _mergeCombo.Top = top;
        _mergeCombo.Width = 140;
        Controls.Add(_mergeCombo);

        var runButton = new Button
        {
            Text = "実行",
            DialogResult = DialogResult.OK,
            Left = ClientSize.Width - 188,
            Top = ClientSize.Height - 48,
            Width = 80,
            Height = 30,
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom
        };
        runButton.Click += (_, _) => CommitResult();

        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Left = ClientSize.Width - 98,
            Top = ClientSize.Height - 48,
            Width = 80,
            Height = 30,
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom
        };

        Controls.Add(runButton);
        Controls.Add(cancelButton);
        AcceptButton = runButton;
        CancelButton = cancelButton;
        DialogKeyboardHelper.AttachOkCancelBindings(this, runButton, cancelButton);
    }

    private void UpdateControlState()
    {
        bool isCustom = _colorCountCombo.SelectedIndex == 4;
        bool isRgb565 = _colorCountCombo.SelectedIndex == 0;
        _customColorCount.Enabled = isCustom;
        _ditherCombo.Enabled = !isRgb565;
        _mergeCombo.Enabled = !isRgb565;
        if (isRgb565)
        {
            _ditherCombo.SelectedIndex = 0;
            _mergeCombo.SelectedIndex = 0;
        }
        UpdateDitherHint();
    }

    private void UpdateDitherHint()
    {
        _ditherHint.Text = _ditherCombo.SelectedIndex switch
        {
            1 => "自然: 粒を控えめに散らします。イラスト向き。",
            2 => "階調優先: 写真やグラデーション向き。階調を高品質に保持します。",
            3 => "レトロ (Bayer): 古いPC風の網点パターンで表現します。",
            _ => "なし: ディザを行わず最も近い色へ置換します。256色ではこちらを推奨。"
        };
    }

    private void CommitResult()
    {
        bool useRgb565 = _colorCountCombo.SelectedIndex == 0;
        int colorCount = _colorCountCombo.SelectedIndex switch
        {
            0 => 65536,
            1 => 256,
            2 => 16,
            3 => 2,
            _ => (int)_customColorCount.Value
        };

        QuantizationDitherMode dither = _ditherCombo.SelectedIndex switch
        {
            1 => QuantizationDitherMode.Atkinson,               // 自然 (VoidAndCluster から Atkinson へ変更)
            2 => QuantizationDitherMode.SierraLite,             // 階調優先
            3 => QuantizationDitherMode.OrderedBayer4x4,        // レトロ
            _ => QuantizationDitherMode.None
        };

        QuantizationMergeLevel merge = _mergeCombo.SelectedIndex switch
        {
            1 => QuantizationMergeLevel.Weak,
            2 => QuantizationMergeLevel.Medium,
            3 => QuantizationMergeLevel.Strong,
            _ => QuantizationMergeLevel.None
        };

        ResultRequest = new QuantizationRequest
        {
            ColorCount = colorCount,
            UseRgb565 = useRgb565,
            Dither = useRgb565 ? QuantizationDitherMode.None : dither,
            MergeLevel = useRgb565 ? QuantizationMergeLevel.None : merge
        };
        ResultLabel = useRgb565 ? "65536色" : $"{Math.Clamp(colorCount, 2, 256)}色";
    }
}
