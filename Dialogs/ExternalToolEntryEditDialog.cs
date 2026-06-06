using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using MidFD.Models;

namespace MidFD.Dialogs;

public class ExternalToolEntryEditDialog : Form
{
    private readonly ExternalToolCommandDefinition _editingDefinition;
    private readonly CheckBox _enabledCheckBox;
    private readonly TextBox _idTextBox;
    private readonly TextBox _displayNameTextBox;
    private readonly TextBox _descriptionTextBox;
    private readonly TextBox _aliasTextBox;
    private readonly TextBox _altSlotTextBox;
    private readonly TextBox _executablePathTextBox;
    private readonly TextBox _argumentsTextBox;
    private readonly TextBox _workingDirectoryTextBox;

    private readonly IReadOnlyList<ExternalToolCommandDefinition> _existingTools;
    private readonly string? _editingId;

    public ExternalToolCommandDefinition UpdatedDefinition { get; private set; } = null!;

    public ExternalToolEntryEditDialog(ExternalToolCommandDefinition definition, bool isNew, IReadOnlyList<ExternalToolCommandDefinition> existingTools)
    {
        _editingDefinition = definition;
        _existingTools = existingTools;
        _editingId = isNew ? null : definition.Id;

        Text = isNew ? "外部ツール定義の追加" : "外部ツール定義の編集";
        Size = new Size(600, 560);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;

        int labelX = 20;
        int controlX = 140;
        int controlWidth = 420;
        int currentY = 20;
        int rowGap = 32;

        _enabledCheckBox = new CheckBox
        {
            Text = "有効",
            Location = new Point(controlX, currentY),
            Checked = definition.Enabled,
            AutoSize = true
        };
        Controls.Add(_enabledCheckBox);
        currentY += rowGap;

        AddLabel("ID:", labelX, currentY);
        _idTextBox = new TextBox 
        { 
            Location = new Point(controlX, currentY), 
            Width = controlWidth, 
            Text = definition.Id,
            ReadOnly = true,
            BackColor = SystemColors.Control
        };
        Controls.Add(_idTextBox);
        currentY += rowGap;

        AddLabel("表示名:", labelX, currentY);
        _displayNameTextBox = new TextBox { Location = new Point(controlX, currentY), Width = controlWidth, Text = definition.DisplayName };
        Controls.Add(_displayNameTextBox);
        currentY += rowGap;

        AddLabel("詳細説明:", labelX, currentY);
        _descriptionTextBox = new TextBox { Location = new Point(controlX, currentY), Width = controlWidth, Text = definition.Description };
        Controls.Add(_descriptionTextBox);
        currentY += rowGap;

        AddLabel("エイリアス:", labelX, currentY);
        _aliasTextBox = new TextBox { Location = new Point(controlX, currentY), Width = 150, Text = definition.Alias };
        Controls.Add(_aliasTextBox);
        AddHint("(検索補助キー)", controlX + 160, currentY);
        currentY += rowGap;

        AddLabel("Altスロット:", labelX, currentY);
        _altSlotTextBox = new TextBox { Location = new Point(controlX, currentY), Width = 40, MaxLength = 1, Text = definition.AltSlot };
        Controls.Add(_altSlotTextBox);
        AddHint("(Alt+英数字 で直起動。F,V,G,T,H は予約済。Alt+F1〜F12 とは別 namespace)", controlX + 50, currentY);
        currentY += rowGap;

        AddLabel("実行ファイル:", labelX, currentY);
        _executablePathTextBox = new TextBox { Location = new Point(controlX, currentY), Width = controlWidth - 80, Text = definition.ExecutablePath };
        Controls.Add(_executablePathTextBox);
        var btnBrowseExe = new Button { Text = "参照...", Location = new Point(controlX + controlWidth - 75, currentY - 2), Width = 75, Height = 26 };
        btnBrowseExe.Click += (_, _) => BrowseFile(_executablePathTextBox, "実行ファイル (*.exe;*.com;*.bat;*.cmd)|*.exe;*.com;*.bat;*.cmd|すべてのファイル (*.*)|*.*");
        Controls.Add(btnBrowseExe);
        currentY += rowGap;

        AddLabel("引数:", labelX, currentY);
        _argumentsTextBox = new TextBox { Location = new Point(controlX, currentY), Width = controlWidth, Text = definition.Arguments };
        Controls.Add(_argumentsTextBox);
        AddHint(
            "{currentDir}, {selectedPath}, {selectedName}, {markedPaths}, {markedPathsFile} が使用可能",
            controlX,
            currentY + 22);
        AddTemplateHelp(controlX, currentY + 44, controlWidth);
        currentY += rowGap + 78;

        AddLabel("作業フォルダ:", labelX, currentY);
        _workingDirectoryTextBox = new TextBox { Location = new Point(controlX, currentY), Width = controlWidth - 80, Text = definition.WorkingDirectory };
        Controls.Add(_workingDirectoryTextBox);
        var btnBrowseDir = new Button { Text = "参照...", Location = new Point(controlX + controlWidth - 75, currentY - 2), Width = 75, Height = 26 };
        btnBrowseDir.Click += (_, _) => BrowseFolder(_workingDirectoryTextBox);
        Controls.Add(btnBrowseDir);
        currentY += rowGap;

        var btnOk = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(380, 480), Width = 90, Height = 30 };
        btnOk.Click += BtnOk_Click;
        var btnCancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, Location = new Point(480, 480), Width = 90, Height = 30 };

        Controls.Add(btnOk);
        Controls.Add(btnCancel);
        AcceptButton = btnOk;
        CancelButton = btnCancel;
    }

    private void AddLabel(string text, int x, int y)
    {
        Controls.Add(new Label { Text = text, Location = new Point(x, y + 3), AutoSize = true });
    }

    private void AddHint(string text, int x, int y)
    {
        Controls.Add(new Label { Text = text, Location = new Point(x, y + 3), AutoSize = true, ForeColor = Color.Gray });
    }

    private void AddTemplateHelp(int x, int y, int width)
    {
        Controls.Add(new Label
        {
            Text = "{markedPaths}: マーク済みパスを引数へ直接展開 / {markedPathsFile}: 1行1パスの一時ファイルを渡す\r\n例: \"{selectedPath}\"    --cwd \"{currentDir}\"    --list \"{markedPathsFile}\"",
            Location = new Point(x, y),
            Size = new Size(width, 42),
            ForeColor = Color.DimGray
        });
    }

    private void BrowseFile(TextBox target, string filter)
    {
        using var dlg = new OpenFileDialog { Filter = filter };
        if (dlg.ShowDialog(this) == DialogResult.OK) target.Text = dlg.FileName;
    }

    private void BrowseFolder(TextBox target)
    {
        using var dlg = new FolderBrowserDialog();
        if (dlg.ShowDialog(this) == DialogResult.OK) target.Text = dlg.SelectedPath;
    }

    private void BtnOk_Click(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_idTextBox.Text))
        {
            MessageBox.Show(this, "IDが空です。管理画面から再度やり直してください。", "入力エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
            return;
        }

        if (string.IsNullOrWhiteSpace(_displayNameTextBox.Text))
        {
            MessageBox.Show(this, "表示名は必須です。", "入力エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
            return;
        }

        if (string.IsNullOrWhiteSpace(_executablePathTextBox.Text))
        {
            MessageBox.Show(this, "実行ファイルパスは必須です。", "入力エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
            return;
        }

        string altSlot = _altSlotTextBox.Text.Trim().ToUpperInvariant();
        if (!string.IsNullOrEmpty(altSlot))
        {
            if (altSlot.Length != 1 || !char.IsLetterOrDigit(altSlot[0]))
            {
                MessageBox.Show(this, "Altスロットは1文字の英数字で指定してください。", "入力エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }

            char slot = altSlot[0];
            if (slot == 'F' || slot == 'V' || slot == 'G' || slot == 'T' || slot == 'H')
            {
                MessageBox.Show(this, "Alt+F / Alt+V / Alt+G / Alt+T / Alt+H はメニュー用の予約キーのため使用できません。", "入力エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }

            // 重複チェック (早期検出)
            foreach (var tool in _existingTools)
            {
                if (!string.IsNullOrEmpty(_editingId) && string.Equals(tool.Id, _editingId, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // 自分自身は除外
                }

                if (string.Equals(tool.AltSlot, altSlot, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show(this, $"Altスロット '{altSlot}' が重複しています。\n同じ Altスロットは複数の外部ツールに割り当てできません。", "入力エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    DialogResult = DialogResult.None;
                    return;
                }
            }
        }

        UpdatedDefinition = new ExternalToolCommandDefinition
        {
            Id = _idTextBox.Text.Trim(),
            Enabled = _enabledCheckBox.Checked,
            DisplayName = _displayNameTextBox.Text.Trim(),
            Description = _descriptionTextBox.Text.Trim(),
            Alias = _aliasTextBox.Text.Trim(),
            AltSlot = string.IsNullOrEmpty(altSlot) ? null : altSlot,
            ExecutablePath = _executablePathTextBox.Text.Trim(),
            Arguments = _argumentsTextBox.Text.Trim(),
            WorkingDirectory = string.IsNullOrWhiteSpace(_workingDirectoryTextBox.Text) ? null : _workingDirectoryTextBox.Text.Trim()
        };
    }
}
