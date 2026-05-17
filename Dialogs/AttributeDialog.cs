using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace MidFD.Dialogs;

public sealed record AttributeDialogRequest(
    string TargetLabel,
    FileAttributes InitialAttributes,
    DateTime InitialLastWriteTime,
    DateTime InitialCreationTime,
    DateTime InitialLastAccessTime);

public sealed record AttributeDialogResult(
    bool ReadOnly,
    bool Hidden,
    bool System,
    bool Archive,
    bool ChangeLastWriteTime,
    DateTime LastWriteTime,
    bool ChangeCreationTime,
    DateTime CreationTime,
    bool ChangeLastAccessTime,
    DateTime LastAccessTime,
    bool IncludeSubdirectories);

public static class AttributeDialog
{
    public static AttributeDialogResult? Show(AttributeDialogRequest request)
    {
        const int sideMargin = 16;
        const int topMargin = 16;

        using Form form = new()
        {
            ClientSize = new Size(560, 410),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            Text = "属性 / 日時変更",
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            BackColor = Color.FromArgb(240, 240, 240),
            AutoScaleMode = AutoScaleMode.Font
        };

        int contentWidth = form.ClientSize.Width - (sideMargin * 2);
        int currentTop = topMargin;

        Label lblTarget = new()
        {
            Left = sideMargin,
            Top = currentTop,
            Width = contentWidth,
            Text = $"Target: {request.TargetLabel}",
            Font = new Font("Meiryo UI", 9F, FontStyle.Bold),
            AutoEllipsis = true
        };
        form.Controls.Add(lblTarget);
        currentTop = lblTarget.Bottom + 12;

        GroupBox grpAttribute = new()
        {
            Left = sideMargin,
            Top = currentTop,
            Width = contentWidth,
            Height = 88,
            Text = "属性"
        };

        CheckBox chkReadOnly = new() { Text = "ReadOnly", Left = 14, Top = 28, AutoSize = true };
        CheckBox chkHidden = new() { Text = "Hidden", Left = 150, Top = 28, AutoSize = true };
        CheckBox chkSystem = new() { Text = "System", Left = 286, Top = 28, AutoSize = true };
        CheckBox chkArchive = new() { Text = "Archive", Left = 422, Top = 28, AutoSize = true };
        grpAttribute.Controls.AddRange(new Control[] { chkReadOnly, chkHidden, chkSystem, chkArchive });
        form.Controls.Add(grpAttribute);
        currentTop = grpAttribute.Bottom + 10;

        chkReadOnly.Checked = request.InitialAttributes.HasFlag(FileAttributes.ReadOnly);
        chkHidden.Checked = request.InitialAttributes.HasFlag(FileAttributes.Hidden);
        chkSystem.Checked = request.InitialAttributes.HasFlag(FileAttributes.System);
        chkArchive.Checked = request.InitialAttributes.HasFlag(FileAttributes.Archive);

        GroupBox grpTimestamp = new()
        {
            Left = sideMargin,
            Top = currentTop,
            Width = contentWidth,
            Height = 184,
            Text = "日時"
        };

        CheckBox chkWrite = new() { Text = "更新日時を変更する", Left = 14, Top = 30, AutoSize = true };
        DateTimePicker dtpWrite = CreateDateTimePicker(220, 26, request.InitialLastWriteTime);

        CheckBox chkCreate = new() { Text = "作成日時を変更する", Left = 14, Top = 78, AutoSize = true };
        DateTimePicker dtpCreate = CreateDateTimePicker(220, 74, request.InitialCreationTime);

        CheckBox chkAccess = new() { Text = "最終アクセス日時を変更する", Left = 14, Top = 126, AutoSize = true };
        DateTimePicker dtpAccess = CreateDateTimePicker(220, 122, request.InitialLastAccessTime);

        void SyncDateTimeEnabled()
        {
            dtpWrite.Enabled = chkWrite.Checked;
            dtpCreate.Enabled = chkCreate.Checked;
            dtpAccess.Enabled = chkAccess.Checked;
        }

        chkWrite.CheckedChanged += (_, _) => SyncDateTimeEnabled();
        chkCreate.CheckedChanged += (_, _) => SyncDateTimeEnabled();
        chkAccess.CheckedChanged += (_, _) => SyncDateTimeEnabled();
        SyncDateTimeEnabled();

        grpTimestamp.Controls.AddRange(new Control[]
        {
            chkWrite, dtpWrite,
            chkCreate, dtpCreate,
            chkAccess, dtpAccess
        });
        form.Controls.Add(grpTimestamp);
        currentTop = grpTimestamp.Bottom + 8;

        CheckBox chkRecursive = new()
        {
            Left = sideMargin + 2,
            Top = currentTop,
            Width = contentWidth - 4,
            Text = "サブディレクトリ以下も処理する",
            AutoSize = true
        };
        form.Controls.Add(chkRecursive);
        currentTop = chkRecursive.Bottom + 12;

        Button btnOk = new()
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            MinimumSize = new Size(80, 30)
        };

        Button btnCancel = new()
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            MinimumSize = new Size(80, 30)
        };

        form.Controls.Add(btnOk);
        form.Controls.Add(btnCancel);

        FileOperationDialogLayoutHelper.ApplyModernBottomActionRow(
            form,
            new[] { btnOk, btnCancel },
            currentTop,
            buttonGap: 10,
            contentGap: 14);

        form.AcceptButton = btnOk;
        form.CancelButton = btnCancel;

        if (form.ShowDialog() != DialogResult.OK)
        {
            return null;
        }

        return new AttributeDialogResult(
            chkReadOnly.Checked,
            chkHidden.Checked,
            chkSystem.Checked,
            chkArchive.Checked,
            chkWrite.Checked,
            dtpWrite.Value,
            chkCreate.Checked,
            dtpCreate.Value,
            chkAccess.Checked,
            dtpAccess.Value,
            chkRecursive.Checked);
    }

    private static DateTimePicker CreateDateTimePicker(int left, int top, DateTime value)
    {
        return new DateTimePicker
        {
            Left = left,
            Top = top,
            Width = 300,
            Format = DateTimePickerFormat.Custom,
            CustomFormat = "yyyy/MM/dd HH:mm:ss",
            ShowUpDown = true,
            Value = value
        };
    }
}
