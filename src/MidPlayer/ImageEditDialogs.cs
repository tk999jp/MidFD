namespace MidFD;

internal readonly record struct ImageToneSettings(int Brightness, int Contrast, int Saturation, double Gamma);

internal static class ImageEditDialogs
{
    public static (int Width, int Height, bool PreserveAspect)? PromptResize(IWin32Window owner, int width, int height)
    {
        using var dialog = new Form
        {
            Text = "画像リサイズ",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(390, 155)
        };
        var widthBox = CreateNumber(width, 1, 32768);
        var heightBox = CreateNumber(height, 1, 32768);
        var preserve = new CheckBox { Text = "縦横比を維持", Checked = true, AutoSize = true, Left = 18, Top = 78 };
        AddLabeledControl(dialog, "幅 (px)", widthBox, 18, 18);
        AddLabeledControl(dialog, "高さ (px)", heightBox, 164, 18);
        dialog.Controls.Add(preserve);
        AddButtons(dialog, 112);
        if (dialog.ShowDialog(owner) != DialogResult.OK) return null;
        return ((int)widthBox.Value, (int)heightBox.Value, preserve.Checked);
    }

    public static int? PromptInteger(IWin32Window owner, string title, string label, int value, int minimum, int maximum)
    {
        using var dialog = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(390, 125)
        };
        var number = CreateNumber(value, minimum, maximum);
        AddLabeledControl(dialog, label, number, 18, 20);
        AddButtons(dialog, 76);
        return dialog.ShowDialog(owner) == DialogResult.OK ? (int)number.Value : null;
    }

    public static ImageToneSettings? PromptTone(
        IWin32Window owner,
        ImageToneSettings initial,
        Action<ImageToneSettings> preview)
    {
        using var dialog = new Form
        {
            Text = "色調補正",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(390, 245)
        };
        var brightness = CreateNumber(initial.Brightness, -100, 100);
        var contrast = CreateNumber(initial.Contrast, -100, 100);
        var saturation = CreateNumber(initial.Saturation, 0, 200);
        var gamma = new NumericUpDown
        {
            Minimum = 0.1m,
            Maximum = 4.0m,
            DecimalPlaces = 1,
            Increment = 0.1m,
            Value = (decimal)Math.Clamp(initial.Gamma, 0.1, 4.0),
            Left = 160,
            Width = 175,
            Top = 104
        };
        AddLabeledControl(dialog, "明るさ (-100～100)", brightness, 18, 16);
        AddLabeledControl(dialog, "コントラスト (-100～100)", contrast, 18, 58);
        AddLabeledControl(dialog, "彩度 (0～200%)", saturation, 18, 100);
        AddLabeledControl(dialog, "ガンマ (0.1～4.0)", gamma, 18, 142);

        var previewButton = new Button { Text = "プレビュー", Left = 18, Top = 198, Width = 90, Height = 28 };
        previewButton.Click += (_, _) => preview(new ImageToneSettings((int)brightness.Value, (int)contrast.Value, (int)saturation.Value, (double)gamma.Value));
        dialog.Controls.Add(previewButton);
        var ok = new Button { Text = "適用", DialogResult = DialogResult.OK, Left = 215, Top = 198, Width = 75, Height = 28 };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, Left = 296, Top = 198, Width = 78, Height = 28 };
        dialog.Controls.AddRange([ok, cancel]);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        if (dialog.ShowDialog(owner) != DialogResult.OK) return null;
        return new ImageToneSettings((int)brightness.Value, (int)contrast.Value, (int)saturation.Value, (double)gamma.Value);
    }

    private static NumericUpDown CreateNumber(int value, int minimum, int maximum) => new()
    {
        Minimum = minimum,
        Maximum = maximum,
        Value = Math.Clamp(value, minimum, maximum),
        Left = 160,
        Width = 175
    };

    private static void AddLabeledControl(Form dialog, string label, Control control, int left, int top)
    {
        dialog.Controls.Add(new Label { Text = label, Left = left, Top = top + 4, AutoSize = true });
        control.Left = 200;
        control.Top = top;
        control.Width = 170;
        dialog.Controls.Add(control);
    }

    private static void AddButtons(Form dialog, int top)
    {
        var ok = new Button { Text = "適用", DialogResult = DialogResult.OK, Left = 194, Top = top, Width = 80, Height = 28 };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, Left = 280, Top = top, Width = 80, Height = 28 };
        dialog.Controls.AddRange([ok, cancel]);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
    }
}
