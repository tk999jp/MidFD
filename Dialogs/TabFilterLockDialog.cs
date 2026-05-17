using System;
using System.Drawing;
using System.Windows.Forms;
using MidFD.Models;
using MidFD.Services;

namespace MidFD.Dialogs;

public sealed class TabFilterLockDialog : Form
{
    private readonly CheckBox _enabledCheckBox = new();
    private readonly TextBox _extensionTextBox = new();

    private readonly GroupBox _modifiedGroupBox = new();
    // From
    private readonly CheckBox _fromCheckBox = new();
    private readonly DateTimePicker _fromDatePicker = new();
    private readonly DateTimePicker _fromTimePicker = new();

    // To
    private readonly CheckBox _toCheckBox = new();
    private readonly DateTimePicker _toDatePicker = new();
    private readonly DateTimePicker _toTimePicker = new();

    private readonly GroupBox _gitGroupBox = new();
    private readonly CheckBox _gitUnignoredOnlyCheckBox = new();

    private readonly Button _okButton = new();
    private readonly Button _cancelButton = new();
    private readonly Button _clearButton = new();

    public TabFilterLockState ResultState { get; private set; }

    public TabFilterLockDialog(TabFilterLockState? currentState)
    {
        ResultState = currentState?.Clone() ?? new TabFilterLockState();
        InitializeComponent();
        LoadState(ResultState);
    }

    private void InitializeComponent()
    {
        Text = "現在タブのフィルタロック";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(560, 420);

        _enabledCheckBox.Text = "フィルタロックを有効にする";
        _enabledCheckBox.SetBounds(16, 16, 300, 24);

        var extensionLabel = new Label
        {
            Text = "対象拡張子",
            AutoSize = true,
            Location = new Point(16, 54)
        };
        _extensionTextBox.SetBounds(16, 76, 520, 24);
        var extensionHelpLabel = new Label
        {
            Text = "例: .cs;.md;.json / 空欄なら拡張子では絞り込みません",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Location = new Point(16, 104)
        };

        _modifiedGroupBox.Text = "更新日時";
        _modifiedGroupBox.SetBounds(16, 134, 520, 150);

        // From
        _fromCheckBox.Text = "開始日時を指定する";
        _fromCheckBox.SetBounds(16, 24, 200, 24);
        _fromDatePicker.Format = DateTimePickerFormat.Custom;
        _fromDatePicker.CustomFormat = "yyyy/MM/dd";
        _fromDatePicker.SetBounds(36, 50, 130, 24);
        _fromTimePicker.Format = DateTimePickerFormat.Custom;
        _fromTimePicker.CustomFormat = "HH:mm";
        _fromTimePicker.ShowUpDown = true;
        _fromTimePicker.SetBounds(174, 50, 80, 24);
        var fromLabel = new Label { Text = "以降のファイル", AutoSize = true, Location = new Point(262, 54) };

        // To
        _toCheckBox.Text = "終了日時を指定する";
        _toCheckBox.SetBounds(16, 84, 200, 24);
        _toDatePicker.Format = DateTimePickerFormat.Custom;
        _toDatePicker.CustomFormat = "yyyy/MM/dd";
        _toDatePicker.SetBounds(36, 110, 130, 24);
        _toTimePicker.Format = DateTimePickerFormat.Custom;
        _toTimePicker.CustomFormat = "HH:mm";
        _toTimePicker.ShowUpDown = true;
        _toTimePicker.SetBounds(174, 110, 80, 24);
        var toLabel = new Label { Text = "以前のファイル", AutoSize = true, Location = new Point(262, 114) };

        _modifiedGroupBox.Controls.AddRange(new Control[]
        {
            _fromCheckBox, _fromDatePicker, _fromTimePicker, fromLabel,
            _toCheckBox, _toDatePicker, _toTimePicker, toLabel
        });

        _gitGroupBox.Text = "Git";
        _gitGroupBox.SetBounds(16, 294, 520, 74);
        _gitUnignoredOnlyCheckBox.Text = "Gitで無視されていない項目のみ表示";
        _gitUnignoredOnlyCheckBox.SetBounds(16, 22, 320, 24);
        var gitHelpLabel = new Label
        {
            Text = "Git管理下のフォルダでのみ有効です。",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Location = new Point(36, 48)
        };
        _gitGroupBox.Controls.AddRange(new Control[]
        {
            _gitUnignoredOnlyCheckBox, gitHelpLabel
        });

        _okButton.Text = "OK";
        _okButton.SetBounds(258, 380, 80, 30);
        _okButton.Click += (_, _) => Accept();

        _cancelButton.Text = "キャンセル";
        _cancelButton.SetBounds(344, 380, 80, 30);
        _cancelButton.DialogResult = DialogResult.Cancel;

        _clearButton.Text = "条件をクリア";
        _clearButton.SetBounds(430, 380, 106, 30);
        _clearButton.Click += (_, _) =>
        {
            _extensionTextBox.Text = string.Empty;
            _fromCheckBox.Checked = false;
            _toCheckBox.Checked = false;
            _gitUnignoredOnlyCheckBox.Checked = false;
        };

        _fromCheckBox.CheckedChanged += (s, e) =>
        {
            _fromDatePicker.Enabled = _fromCheckBox.Checked;
            _fromTimePicker.Enabled = _fromCheckBox.Checked;
        };
        _toCheckBox.CheckedChanged += (s, e) =>
        {
            _toDatePicker.Enabled = _toCheckBox.Checked;
            _toTimePicker.Enabled = _toCheckBox.Checked;
        };

        Controls.AddRange(new Control[]
        {
            _enabledCheckBox,
            extensionLabel,
            _extensionTextBox,
            extensionHelpLabel,
            _modifiedGroupBox,
            _gitGroupBox,
            _okButton,
            _cancelButton,
            _clearButton
        });

        AcceptButton = _okButton;
        CancelButton = _cancelButton;
    }

    private void LoadState(TabFilterLockState state)
    {
        _enabledCheckBox.Checked = state.Enabled;
        _extensionTextBox.Text = string.IsNullOrWhiteSpace(state.ExtensionText)
            ? string.Join(";", state.IncludeExtensions)
            : state.ExtensionText;

        _fromCheckBox.Checked = state.ModifiedFromLocal.HasValue;
        _fromDatePicker.Value = state.ModifiedFromLocal ?? TabFilterLockService.TrimToMinute(DateTime.Now);
        _fromTimePicker.Value = _fromDatePicker.Value;
        _fromDatePicker.Enabled = _fromCheckBox.Checked;
        _fromTimePicker.Enabled = _fromCheckBox.Checked;

        _toCheckBox.Checked = state.ModifiedToLocal.HasValue;
        _toDatePicker.Value = state.ModifiedToLocal ?? TabFilterLockService.TrimToMinute(DateTime.Now);
        _toTimePicker.Value = _toDatePicker.Value;
        _toDatePicker.Enabled = _toCheckBox.Checked;
        _toTimePicker.Enabled = _toCheckBox.Checked;

        _gitUnignoredOnlyCheckBox.Checked = state.GitUnignoredOnly;
    }

    private void Accept()
    {
        string extensionText = _extensionTextBox.Text.Trim();
        if (extensionText.Contains('*') || extensionText.Contains('?'))
        {
            MessageBox.Show(this, "ワイルドカードや正規表現は今回のフィルタロックでは使えません。", "フィルタロック", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        DateTime? from = null;
        if (_fromCheckBox.Checked)
        {
            var d = _fromDatePicker.Value;
            var t = _fromTimePicker.Value;
            from = new DateTime(d.Year, d.Month, d.Day, t.Hour, t.Minute, 0);
        }

        DateTime? to = null;
        if (_toCheckBox.Checked)
        {
            var d = _toDatePicker.Value;
            var t = _toTimePicker.Value;
            to = new DateTime(d.Year, d.Month, d.Day, t.Hour, t.Minute, 0);
        }

        if (from.HasValue && to.HasValue && from.Value > to.Value)
        {
            MessageBox.Show(this, "更新日時の「以降」が「以前」より後になっています。", "フィルタロック", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        ResultState = new TabFilterLockState
        {
            Enabled = _enabledCheckBox.Checked,
            ExtensionText = extensionText,
            IncludeExtensions = TabFilterLockState.NormalizeExtensions(extensionText),
            ModifiedFromLocal = from,
            ModifiedToLocal = to,
            GitUnignoredOnly = _gitUnignoredOnlyCheckBox.Checked
        };

        DialogResult = DialogResult.OK;
        Close();
    }
}
