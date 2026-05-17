using MidFD.Services;

namespace MidFD.Dialogs;

public enum RenameEntryMode
{
    Cancel,
    SingleStep,
    Bulk
}

public sealed class RenameEntryDialogResult
{
    public bool Confirmed { get; init; }
    public RenameEntryMode Mode { get; init; }
    public string SingleStepInitialName { get; init; } = string.Empty;
}

public sealed class RenameEntryDialog : Form
{
    private readonly string _firstSourcePath;
    private readonly string _firstSourceName;
    private readonly RadioButton _singleStepRadioButton;
    private readonly RadioButton _bulkRadioButton;
    private readonly TextBox _singleNameTextBox;
    private readonly Label _hintLabel;

    public RenameEntryDialogResult Result { get; private set; } = new();

    public RenameEntryDialog(IReadOnlyList<string> sourcePaths)
    {
        _firstSourcePath = sourcePaths.First();
        _firstSourceName = Path.GetFileName(_firstSourcePath);

        Text = "Rename";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(484, 238);

        var targetSummaryLabel = new Label
        {
            Left = 16,
            Top = 16,
            Width = 452,
            Height = 42,
            Text = $"{sourcePaths.Count} 件の対象が選択されています。\r\n先頭項目: {_firstSourceName}"
        };

        var singleNameLabel = new Label
        {
            Left = 16,
            Top = 64,
            Width = 120,
            Text = "先頭項目の新しい名前"
        };

        _singleNameTextBox = new TextBox
        {
            Left = 144,
            Top = 60,
            Width = 324,
            Text = _firstSourceName,
            TabIndex = 0
        };

        var modeGroup = new GroupBox
        {
            Left = 16,
            Top = 96,
            Width = 452,
            Height = 70,
            Text = "モード"
        };

        _singleStepRadioButton = new RadioButton
        {
            Left = 16,
            Top = 28,
            Width = 180,
            Text = "1ファイルずつ名前を変更",
            Checked = true,
            TabIndex = 1
        };
        _bulkRadioButton = new RadioButton
        {
            Left = 220,
            Top = 28,
            Width = 120,
            Text = "一括置換",
            TabIndex = 2
        };

        modeGroup.Controls.Add(_singleStepRadioButton);
        modeGroup.Controls.Add(_bulkRadioButton);

        _hintLabel = new Label
        {
            Left = 16,
            Top = 180,
            Width = 452,
            Height = 30,
            Text = string.Empty
        };

        var okButton = new Button
        {
            Left = 298,
            Top = 204,
            Width = 80,
            Height = 28,
            Text = "OK",
            DialogResult = DialogResult.OK,
            TabIndex = 3
        };

        var cancelButton = new Button
        {
            Left = 388,
            Top = 204,
            Width = 80,
            Height = 28,
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            TabIndex = 4
        };

        Controls.Add(targetSummaryLabel);
        Controls.Add(modeGroup);
        Controls.Add(singleNameLabel);
        Controls.Add(_singleNameTextBox);
        Controls.Add(_hintLabel);
        Controls.Add(okButton);
        Controls.Add(cancelButton);

        targetSummaryLabel.Height = FileOperationDialogLayoutHelper.MeasureLabelHeight(targetSummaryLabel, targetSummaryLabel.Width, 42);
        singleNameLabel.Top = targetSummaryLabel.Bottom + 10;
        _singleNameTextBox.Top = singleNameLabel.Top - 4;
        modeGroup.Top = _singleNameTextBox.Bottom + 14;
        _hintLabel.Top = modeGroup.Bottom + 12;
        _hintLabel.Height = FileOperationDialogLayoutHelper.MeasureLabelHeight(_hintLabel, _hintLabel.Width, 30);
        FileOperationDialogLayoutHelper.EnsureBottomButtonRow(
            this,
            new[] { okButton, cancelButton },
            _hintLabel.Bottom,
            buttonGap: 10,
            contentGap: 12);
        int contentWidth = ClientSize.Width - 32;
        targetSummaryLabel.Width = contentWidth;
        modeGroup.Width = contentWidth;
        _hintLabel.Width = contentWidth;

        AcceptButton = okButton;
        CancelButton = cancelButton;

        _singleStepRadioButton.CheckedChanged += (_, _) => UpdateModeUi();
        _bulkRadioButton.CheckedChanged += (_, _) => UpdateModeUi();

        Shown += (_, _) =>
        {
            UpdateModeUi();
            _singleNameTextBox.Focus();
            _singleNameTextBox.SelectAll();
        };

        FormClosing += OnFormClosing;
    }

    public static RenameEntryDialogResult Show(IWin32Window owner, IReadOnlyList<string> sourcePaths)
    {
        using var dialog = new RenameEntryDialog(sourcePaths);
        return dialog.ShowDialog(owner) == DialogResult.OK
            ? dialog.Result
            : new RenameEntryDialogResult { Confirmed = false, Mode = RenameEntryMode.Cancel };
    }

    private void UpdateModeUi()
    {
        bool singleStep = _singleStepRadioButton.Checked;
        _singleNameTextBox.Enabled = singleStep;

        if (singleStep)
        {
            _hintLabel.Text = "先頭項目から順に、小さな入力ダイアログで 1 件ずつ確認しながら変更します。";
        }
        else
        {
            _hintLabel.Text = "次の画面でテンプレートと連番を指定し、preview を確認してから一括置換します。";
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (DialogResult != DialogResult.OK)
        {
            return;
        }

        var mode = _singleStepRadioButton.Checked ? RenameEntryMode.SingleStep : RenameEntryMode.Bulk;
        string singleStepName = _singleNameTextBox.Text;

        if (mode == RenameEntryMode.SingleStep)
        {
            var previewItem = RenamePreviewService.BuildSingleItemPreview(_firstSourcePath, singleStepName);
            if (previewItem.HasError)
            {
                MessageBox.Show($"先頭項目をリネームできません: {previewItem.Status}", "Rename", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                e.Cancel = true;
                return;
            }
        }

        Result = new RenameEntryDialogResult
        {
            Confirmed = true,
            Mode = mode,
            SingleStepInitialName = singleStepName
        };
    }
}
