using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using MidFD.Commands;

namespace MidFD.Dialogs;

public sealed class CommandListDialog : Form
{
    private readonly DataGridView _grid;

    public CommandListDialog(IReadOnlyList<CommandDefinition> commands)
    {
        Text = "コマンド一覧";
        Size = new Size(980, 560);
        MinimumSize = new Size(760, 420);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        KeyPreview = true;

        var rootLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(12)
        };
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48F));
        Controls.Add(rootLayout);

        var descriptionLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = "登録済みコマンドの一覧です。表示内容は読み取り専用です。",
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0)
        };
        rootLayout.Controls.Add(descriptionLabel, 0, 0);

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            ColumnHeadersVisible = true,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ColumnHeadersHeight = 28,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            AutoGenerateColumns = false,
            EditMode = DataGridViewEditMode.EditProgrammatically,
            StandardTab = true,
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.Fixed3D,
            EnableHeadersVisualStyles = false
        };
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(31, 78, 121);
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
        _grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(31, 78, 121);
        _grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Color.White;
        _grid.ColumnHeadersDefaultCellStyle.Font = new Font(_grid.Font, FontStyle.Bold);
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "表示名",
            Name = "DisplayName",
            Width = 180,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "スコープ",
            Name = "Scope",
            Width = 90,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "説明",
            Name = "Description",
            Width = 360,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "カスタマイズ可",
            Name = "Customizable",
            Width = 110,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "注意操作",
            Name = "Caution",
            Width = 90,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "ID",
            Name = "Id",
            Width = 240,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        rootLayout.Controls.Add(_grid, 0, 1);

        var bottomPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 8, 0, 0)
        };
        rootLayout.Controls.Add(bottomPanel, 0, 2);

        var closeButton = new Button
        {
            Text = "閉じる",
            DialogResult = DialogResult.Cancel,
            Width = 100,
            Height = 30,
            Dock = DockStyle.Right
        };
        bottomPanel.Controls.Add(closeButton);

        AcceptButton = closeButton;
        CancelButton = closeButton;

        foreach (CommandDefinition command in commands)
        {
            _grid.Rows.Add(
                command.DisplayName,
                command.Scope.ToString(),
                command.Description,
                FormatBoolean(command.IsCustomizable),
                FormatBoolean(command.IsDangerous),
                command.Id);
        }

        Shown += (_, _) =>
        {
            if (_grid.Rows.Count > 0)
            {
                _grid.ClearSelection();
                _grid.Rows[0].Selected = true;
                _grid.CurrentCell = _grid.Rows[0].Cells[0];
            }

            _grid.Focus();
        };
    }

    private static string FormatBoolean(bool value)
    {
        return value ? "はい" : "いいえ";
    }
}
