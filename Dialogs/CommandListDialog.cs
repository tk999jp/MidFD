using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MidFD.Commands;

namespace MidFD.Dialogs;

public sealed class CommandListDialog : Form
{
    private readonly CommandDefinition[] _commands;
    private readonly TabControl _categoryTabs;
    private readonly TextBox _searchBox;
    private readonly DataGridView _grid;

    public CommandListDialog(IReadOnlyList<CommandDefinition> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        _commands = commands.ToArray();

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
            RowCount = 5,
            Padding = new Padding(12)
        };
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48F));
        Controls.Add(rootLayout);

        var descriptionLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = "登録済みコマンドをカテゴリ別に表示します。検索は全カテゴリを対象にします。",
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0)
        };
        rootLayout.Controls.Add(descriptionLabel, 0, 0);

        var searchLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0)
        };
        searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72F));
        searchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        var searchLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = "全カテゴリ検索",
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0)
        };
        _searchBox = new TextBox
        {
            Dock = DockStyle.Fill,
            AccessibleName = "全カテゴリ検索",
            Margin = new Padding(0, 3, 0, 3)
        };
        searchLayout.Controls.Add(searchLabel, 0, 0);
        searchLayout.Controls.Add(_searchBox, 1, 0);
        rootLayout.Controls.Add(searchLayout, 0, 1);

        _categoryTabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0)
        };
        foreach (string category in _commands
                     .Select(static command => command.Category)
                     .Where(static category => !string.IsNullOrWhiteSpace(category))
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            _categoryTabs.TabPages.Add(new TabPage(category) { Tag = category });
        }
        if (_categoryTabs.TabPages.Count == 0)
        {
            _categoryTabs.TabPages.Add(new TabPage("その他") { Tag = "その他" });
        }
        rootLayout.Controls.Add(_categoryTabs, 0, 2);

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
        AddTextColumn("カテゴリ", "Category", 118);
        AddTextColumn("表示名", "DisplayName", 180);
        AddTextColumn("スコープ", "Scope", 90);
        AddTextColumn("説明", "Description", 310);
        AddTextColumn("カスタマイズ可", "Customizable", 110);
        AddTextColumn("注意操作", "Caution", 90);
        AddTextColumn("ID", "Id", 240);
        rootLayout.Controls.Add(_grid, 0, 3);

        var bottomPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 8, 0, 0)
        };
        rootLayout.Controls.Add(bottomPanel, 0, 4);

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

        _categoryTabs.SelectedIndexChanged += (_, _) => RefreshRows();
        _searchBox.TextChanged += (_, _) => RefreshRows();
        Shown += (_, _) =>
        {
            RefreshRows();
            if (_grid.Rows.Count > 0)
            {
                _grid.ClearSelection();
                _grid.Rows[0].Selected = true;
                _grid.CurrentCell = _grid.Rows[0].Cells[0];
            }

            _searchBox.Focus();
        };

        RefreshRows();
    }

    private void AddTextColumn(string header, string name, int width)
    {
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = header,
            Name = name,
            Width = width,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
    }

    private void RefreshRows()
    {
        string query = _searchBox.Text.Trim();
        string? selectedCategory = _categoryTabs.SelectedTab?.Tag as string;
        IEnumerable<CommandDefinition> filtered = _commands;
        if (query.Length == 0)
        {
            filtered = filtered.Where(command => string.Equals(command.Category, selectedCategory, StringComparison.Ordinal));
        }
        else
        {
            filtered = filtered.Where(command =>
                command.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                command.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                command.Description.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        _grid.SuspendLayout();
        try
        {
            _grid.Rows.Clear();
            foreach (CommandDefinition command in filtered.OrderBy(static command => command.DisplayName, StringComparer.Ordinal))
            {
                _grid.Rows.Add(
                    command.Category,
                    command.DisplayName,
                    command.Scope.ToString(),
                    command.Description,
                    FormatBoolean(command.IsCustomizable),
                    FormatBoolean(command.IsDangerous),
                    command.Id);
            }
        }
        finally
        {
            _grid.ResumeLayout();
        }
    }

    private static string FormatBoolean(bool value)
    {
        return value ? "はい" : "いいえ";
    }
}
