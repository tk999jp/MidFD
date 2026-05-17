using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace MidFD.Dialogs;

public static class SortDialog
{
    private enum SortKey
    {
        Name,
        Ext,
        Size,
        Date
    }

    public record SortResult(string Kind, bool Ascending);

    public static SortResult? Show(string currentKind, bool currentAscending)
    {
        using Form form = new()
        {
            ClientSize = new Size(420, 290),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            Text = "Sort",
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            KeyPreview = true,
            AutoScaleMode = AutoScaleMode.Font
        };

        Label lblHeader = new()
        {
            Text = "ソート条件を選択して下さい",
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Top,
            Height = 40,
            Font = new Font("Meiryo UI", 9F, FontStyle.Bold)
        };
        Panel pnlLine = new() { Dock = DockStyle.Top, Height = 1, BackColor = Color.LightGray };

        int outerMargin = 12;
        GroupBox grpKind = new()
        {
            Text = "条件",
            Left = outerMargin,
            Top = lblHeader.Bottom + 12,
            Width = 240,
            Height = 100,
            Font = new Font("Meiryo UI", 9F)
        };

        RadioButton rbName = new() { Text = "名前(&N)", Left = 12, Top = 24, Width = 100, AutoSize = true };
        RadioButton rbExt = new() { Text = "拡張子(&E)", Left = 120, Top = 24, Width = 100, AutoSize = true };
        RadioButton rbSize = new() { Text = "サイズ(&S)", Left = 12, Top = 56, Width = 100, AutoSize = true };
        RadioButton rbDate = new() { Text = "日時(&T)", Left = 120, Top = 56, Width = 100, AutoSize = true };
        grpKind.Controls.AddRange(new Control[] { rbName, rbExt, rbSize, rbDate });

        GroupBox grpOrder = new()
        {
            Text = "順番",
            Left = grpKind.Right + 12,
            Top = grpKind.Top,
            Width = 144,
            Height = grpKind.Height,
            Font = new Font("Meiryo UI", 9F)
        };

        RadioButton rbAsc = new() { Text = "昇り順(&U)", Left = 12, Top = 24, Width = 120, AutoSize = true };
        RadioButton rbDesc = new() { Text = "降り順(&D)", Left = 12, Top = 56, Width = 120, AutoSize = true };
        grpOrder.Controls.AddRange(new Control[] { rbAsc, rbDesc });

        GroupBox grpDateKind = new()
        {
            Text = "日時種別",
            Left = outerMargin,
            Top = grpKind.Bottom + 10,
            Width = form.ClientSize.Width - (outerMargin * 2),
            Height = 68,
            Font = new Font("Meiryo UI", 9F)
        };

        ComboBox cmbDateKind = new()
        {
            Left = 14,
            Top = 28,
            Width = grpDateKind.Width - 28,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        cmbDateKind.Items.AddRange(new object[]
        {
            "更新日時",
            "作成日時",
            "最終アクセス日時"
        });
        grpDateKind.Controls.Add(cmbDateKind);

        Button btnOk = new() { Text = "OK(&O)", DialogResult = DialogResult.OK };
        Button btnCancel = new() { Text = "Cancel(&C)", DialogResult = DialogResult.Cancel };

        form.Controls.AddRange(new Control[] { grpKind, grpOrder, grpDateKind, btnOk, btnCancel, pnlLine, lblHeader });

        FileOperationDialogLayoutHelper.ApplyModernBottomActionRow(
            form,
            new[] { btnOk, btnCancel },
            grpDateKind.Bottom,
            buttonGap: 8,
            contentGap: 16);

        form.AcceptButton = btnOk;
        form.CancelButton = btnCancel;

        bool updatingSelection = false;

        SortKey currentKey = currentKind switch
        {
            "Ext" => SortKey.Ext,
            "Size" => SortKey.Size,
            "Date" or "DateCreated" or "DateAccessed" => SortKey.Date,
            _ => SortKey.Name
        };

        cmbDateKind.SelectedIndex = currentKind switch
        {
            "DateCreated" => 1,
            "DateAccessed" => 2,
            _ => 0
        };

        void UpdateDateKindEnabled()
        {
            cmbDateKind.Enabled = rbDate.Checked;
        }

        void SelectSortKey(SortKey key)
        {
            if (updatingSelection)
                return;

            try
            {
                updatingSelection = true;
                rbName.Checked = key == SortKey.Name;
                rbExt.Checked = key == SortKey.Ext;
                rbSize.Checked = key == SortKey.Size;
                rbDate.Checked = key == SortKey.Date;
                UpdateDateKindEnabled();
            }
            finally
            {
                updatingSelection = false;
            }
        }

        void SelectOrder(bool ascending)
        {
            if (updatingSelection)
                return;

            try
            {
                updatingSelection = true;
                rbAsc.Checked = ascending;
                rbDesc.Checked = !ascending;
            }
            finally
            {
                updatingSelection = false;
            }
        }

        string GetSelectedSortKind()
        {
            if (rbExt.Checked)
                return "Ext";
            if (rbSize.Checked)
                return "Size";
            if (rbDate.Checked)
            {
                return cmbDateKind.SelectedIndex switch
                {
                    1 => "DateCreated",
                    2 => "DateAccessed",
                    _ => "Date"
                };
            }

            return "Name";
        }

        void SortKeyRadio_CheckedChanged(object? sender, EventArgs e)
        {
            if (updatingSelection)
                return;

            if (sender is not RadioButton radio || !radio.Checked)
                return;

            if (radio == rbExt)
                SelectSortKey(SortKey.Ext);
            else if (radio == rbSize)
                SelectSortKey(SortKey.Size);
            else if (radio == rbDate)
                SelectSortKey(SortKey.Date);
            else
                SelectSortKey(SortKey.Name);
        }

        void OrderRadio_CheckedChanged(object? sender, EventArgs e)
        {
            if (updatingSelection)
                return;

            if (sender is not RadioButton radio || !radio.Checked)
                return;

            SelectOrder(radio == rbAsc);
        }

        rbName.CheckedChanged += SortKeyRadio_CheckedChanged;
        rbExt.CheckedChanged += SortKeyRadio_CheckedChanged;
        rbSize.CheckedChanged += SortKeyRadio_CheckedChanged;
        rbDate.CheckedChanged += SortKeyRadio_CheckedChanged;
        rbAsc.CheckedChanged += OrderRadio_CheckedChanged;
        rbDesc.CheckedChanged += OrderRadio_CheckedChanged;

        SelectSortKey(currentKey);
        SelectOrder(currentAscending);
        UpdateDateKindEnabled();

        form.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                btnOk.PerformClick();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                btnCancel.PerformClick();
                e.Handled = true;
            }
        };

        form.Shown += (s, e) =>
        {
            RadioButton? focused = new[] { rbName, rbExt, rbSize, rbDate }.FirstOrDefault(r => r.Checked) ?? rbName;
            focused.Focus();
        };

        if (form.ShowDialog() == DialogResult.OK)
        {
            return new SortResult(GetSelectedSortKind(), rbAsc.Checked);
        }

        return null;
    }
}
