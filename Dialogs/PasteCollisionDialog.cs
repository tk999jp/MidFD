using MidFD.Models;

namespace MidFD.Dialogs;

public sealed class PasteCollisionDialog : Form
{
    private PasteCollisionAction _result = PasteCollisionAction.Cancel;
    private readonly List<Button> _buttonOrder = new();
    public PasteCollisionAction Result => _result;
    public bool ApplyToAll { get; private set; }

    public PasteCollisionDialog(string fileName, string? renamePreviewName = null, bool allowRename = true, bool isCut = false)
    {
        const int sideMargin = 16;
        const int topMargin = 16;
        Text = isCut ? "貼り付け(移動)時の同名衝突" : "貼り付け時の同名衝突";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Font;
        bool showRenameHint = allowRename && !string.IsNullOrWhiteSpace(renamePreviewName);
        ClientSize = new Size(560, showRenameHint ? 190 : 164);
        int contentWidth = ClientSize.Width - (sideMargin * 2);
        int currentTop = topMargin;

        var titleLabel = new Label
        {
            Left = sideMargin,
            Top = currentTop,
            Width = contentWidth,
            Height = 0,
            Text = isCut
                ? $"移動先に '{fileName}' が既に存在します。どうしますか？"
                : $"'{fileName}' は既に存在します。どうしますか？",
            TextAlign = ContentAlignment.MiddleLeft
        };
        titleLabel.Height = FileOperationDialogLayoutHelper.MeasureLabelHeight(titleLabel, titleLabel.Width, 28);
        currentTop = titleLabel.Bottom + 8;

        var applyToAllCheckBox = new CheckBox
        {
            Left = sideMargin,
            Top = currentTop,
            Width = contentWidth,
            Height = 24,
            Text = isCut ? "以降の同名ファイルにも適用" : "以降すべてに適用",
            TabIndex = 0
        };
        currentTop = applyToAllCheckBox.Bottom + 4;

        var renameHintLabel = new Label
        {
            Left = sideMargin,
            Top = currentTop,
            Width = contentWidth,
            Height = 0,
            Text = showRenameHint
                ? $"同名ファイルがあるため、別名保存では '{renamePreviewName}' で貼り付けます。"
                : string.Empty,
            ForeColor = SystemColors.GrayText,
            Visible = showRenameHint
        };
        if (renameHintLabel.Visible)
        {
            renameHintLabel.Height = FileOperationDialogLayoutHelper.MeasureLabelHeight(renameHintLabel, renameHintLabel.Width, 24);
            currentTop = renameHintLabel.Bottom;
        }
        else
        {
            currentTop = applyToAllCheckBox.Bottom;
        }

        var btnNewerOnly = new Button { Text = "新しい方のみ(&N)", UseMnemonic = true, TabIndex = 1, MinimumSize = new Size(110, 30) };
        var btnOverwrite = new Button { Text = "上書き(&O)", UseMnemonic = true, TabIndex = 2, MinimumSize = new Size(88, 30) };
        var btnSkip = new Button { Text = "スキップ(&S)", UseMnemonic = true, TabIndex = 3, MinimumSize = new Size(88, 30) };
        var btnRename = new Button { Text = "別名保存(&R)", UseMnemonic = true, TabIndex = 4, MinimumSize = new Size(96, 30) };
        var btnCancel = new Button { Text = "キャンセル(&C)", UseMnemonic = true, DialogResult = DialogResult.Cancel, TabIndex = 5, MinimumSize = new Size(104, 30) };

        btnRename.Enabled = allowRename;
        btnRename.Visible = allowRename;
        if (!allowRename)
        {
            btnCancel.TabIndex = 4;
        }

        btnNewerOnly.Click += (_, _) => Commit(PasteCollisionAction.NewerOnly, applyToAllCheckBox.Checked);
        btnOverwrite.Click += (_, _) => Commit(PasteCollisionAction.Overwrite, applyToAllCheckBox.Checked);
        btnSkip.Click += (_, _) => Commit(PasteCollisionAction.Skip, applyToAllCheckBox.Checked);
        btnRename.Click += (_, _) => Commit(PasteCollisionAction.RenameCopy, applyToAllCheckBox.Checked);

        Controls.Add(titleLabel);
        Controls.Add(applyToAllCheckBox);
        Controls.Add(renameHintLabel);
        Controls.Add(btnNewerOnly);
        Controls.Add(btnOverwrite);
        Controls.Add(btnSkip);
        Controls.Add(btnRename);
        Controls.Add(btnCancel);
        FileOperationDialogLayoutHelper.ApplyModernBottomActionRow(
            this,
            allowRename
                ? new[] { btnNewerOnly, btnOverwrite, btnSkip, btnRename, btnCancel }
                : new[] { btnNewerOnly, btnOverwrite, btnSkip, btnCancel },
            currentTop,
            buttonGap: 8,
            contentGap: 16);

        _buttonOrder.Add(btnNewerOnly);
        _buttonOrder.Add(btnOverwrite);
        _buttonOrder.Add(btnSkip);
        if (allowRename)
        {
            _buttonOrder.Add(btnRename);
        }
        _buttonOrder.Add(btnCancel);

        AcceptButton = allowRename ? btnRename : btnOverwrite;
        CancelButton = btnCancel;
        Shown += (_, _) =>
        {
            ActiveControl = allowRename ? btnRename : btnOverwrite;
            (ActiveControl as Button)?.Select();
        };
    }

    public static PasteCollisionDialogResult Show(IWin32Window owner, string fileName, string? renamePreviewName = null, bool allowRename = true, bool isCut = false)
    {
        using var dialog = new PasteCollisionDialog(fileName, renamePreviewName, allowRename, isCut);
        if (dialog.ShowDialog(owner) == DialogResult.OK)
        {
            return new PasteCollisionDialogResult
            {
                Action = dialog.Result,
                ApplyToAll = dialog.ApplyToAll
            };
        }

        return new PasteCollisionDialogResult
        {
            Action = PasteCollisionAction.Cancel,
            ApplyToAll = false
        };
    }

    private void Commit(PasteCollisionAction action, bool applyToAll)
    {
        _result = action;
        ApplyToAll = applyToAll;
        DialogResult = DialogResult.OK;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData is not (Keys.Left or Keys.Right))
        {
            return base.ProcessCmdKey(ref msg, keyData);
        }

        if (ActiveControl is not Button activeButton)
        {
            return base.ProcessCmdKey(ref msg, keyData);
        }

        int currentIndex = _buttonOrder.IndexOf(activeButton);
        if (currentIndex < 0)
        {
            return base.ProcessCmdKey(ref msg, keyData);
        }

        int nextIndex = keyData == Keys.Left
            ? (currentIndex == 0 ? _buttonOrder.Count - 1 : currentIndex - 1)
            : (currentIndex == _buttonOrder.Count - 1 ? 0 : currentIndex + 1);

        _buttonOrder[nextIndex].Select();
        return true;
    }
}
