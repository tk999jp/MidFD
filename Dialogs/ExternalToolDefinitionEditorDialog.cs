using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MidFD.Models;
using MidFD.Services;

namespace MidFD.Dialogs;

public class ExternalToolDefinitionEditorDialog : Form
{
    private readonly ListView _listView;
    private readonly List<ExternalToolCommandDefinition> _workingTools;
    private readonly ExternalToolCommandStore _store;

    public ExternalToolDefinitionEditorDialog()
    {
        _store = ExternalToolCommandStorage.Load();
        _workingTools = _store.Tools.Select(t => CloneDefinition(t)).ToList();

        Text = "外部ツール管理";
        Size = new Size(850, 500);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        ShowInTaskbar = false;

        var mainPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
        Controls.Add(mainPanel);

        _listView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            CheckBoxes = true
        };
        _listView.Columns.Add("有効", 40);
        _listView.Columns.Add("ID", 100);
        _listView.Columns.Add("表示名", 150);
        _listView.Columns.Add("エイリアス", 80);
        _listView.Columns.Add("Alt", 40);
        _listView.Columns.Add("実行ファイル", 250);
        _listView.Columns.Add("引数", 150);

        _listView.ItemChecked += ListView_ItemChecked;

        mainPanel.Controls.Add(_listView);

        var rightPanel = new Panel { Dock = DockStyle.Right, Width = 120, Padding = new Padding(6, 0, 0, 0) };
        mainPanel.Controls.Add(rightPanel);

        int btnY = 0;
        int btnSpacing = 36;

        var btnAdd = CreateButton("追加...", 0, btnY, rightPanel);
        btnAdd.Click += BtnAdd_Click;
        btnY += btnSpacing;

        var btnEdit = CreateButton("編集...", 0, btnY, rightPanel);
        btnEdit.Click += BtnEdit_Click;
        btnY += btnSpacing;

        var btnDelete = CreateButton("削除", 0, btnY, rightPanel);
        btnDelete.Click += BtnDelete_Click;
        btnY += btnSpacing + 10;

        var btnUp = CreateButton("上へ", 0, btnY, rightPanel);
        btnUp.Click += BtnUp_Click;
        btnY += btnSpacing;

        var btnDown = CreateButton("下へ", 0, btnY, rightPanel);
        btnDown.Click += BtnDown_Click;

        var bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(0, 10, 0, 0) };
        Controls.Add(bottomPanel);

        var btnOk = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(630, 10), Width = 90, Height = 30, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };
        btnOk.Click += BtnOk_Click;
        var btnCancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, Location = new Point(730, 10), Width = 90, Height = 30, Anchor = AnchorStyles.Bottom | AnchorStyles.Right };

        bottomPanel.Controls.Add(btnOk);
        bottomPanel.Controls.Add(btnCancel);

        AcceptButton = btnOk;
        CancelButton = btnCancel;

        RefreshList();
    }

    private Button CreateButton(string text, int x, int y, Control parent)
    {
        var btn = new Button { Text = text, Location = new Point(x, y), Width = 110, Height = 30 };
        parent.Controls.Add(btn);
        return btn;
    }

    private void RefreshList()
    {
        _listView.ItemChecked -= ListView_ItemChecked;
        _listView.BeginUpdate();
        _listView.Items.Clear();

        foreach (var tool in _workingTools)
        {
            var item = new ListViewItem("");
            item.Checked = tool.Enabled;
            item.SubItems.Add(tool.Id);
            item.SubItems.Add(tool.DisplayName);
            item.SubItems.Add(tool.Alias ?? "");
            item.SubItems.Add(tool.AltSlot ?? "");
            item.SubItems.Add(tool.ExecutablePath);
            item.SubItems.Add(tool.Arguments);
            item.Tag = tool;
            
            if (!tool.Enabled) item.ForeColor = Color.Gray;

            _listView.Items.Add(item);
        }

        _listView.EndUpdate();
        _listView.ItemChecked += ListView_ItemChecked;
    }

    private void ListView_ItemChecked(object? sender, ItemCheckedEventArgs e)
    {
        if (e.Item.Tag is ExternalToolCommandDefinition tool)
        {
            tool.Enabled = e.Item.Checked;
            e.Item.ForeColor = tool.Enabled ? Color.Empty : Color.Gray;
        }
    }

    private void BtnAdd_Click(object? sender, EventArgs e)
    {
        var newId = GenerateNextExternalToolId(_workingTools);
        var newTool = new ExternalToolCommandDefinition { Id = newId, Enabled = true };
        
        using var dlg = new ExternalToolEntryEditDialog(newTool, true, _workingTools);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            // ID重複は自動生成により基本起きないが、一応最終防衛
            if (_workingTools.Any(t => string.Equals(t.Id, dlg.UpdatedDefinition.Id, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(this, "既に存在するIDです。", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            _workingTools.Add(dlg.UpdatedDefinition);
            RefreshList();
        }
    }

    private void BtnEdit_Click(object? sender, EventArgs e)
    {
        if (_listView.SelectedItems.Count == 0) return;
        if (_listView.SelectedItems[0].Tag is not ExternalToolCommandDefinition tool) return;

        using var dlg = new ExternalToolEntryEditDialog(tool, false, _workingTools);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            int index = _workingTools.IndexOf(tool);
            if (index >= 0)
            {
                _workingTools[index] = dlg.UpdatedDefinition;
                RefreshList();
                _listView.Items[index].Selected = true;
            }
        }
    }

    private void BtnDelete_Click(object? sender, EventArgs e)
    {
        if (_listView.SelectedItems.Count == 0) return;
        if (_listView.SelectedItems[0].Tag is not ExternalToolCommandDefinition tool) return;

        if (MessageBox.Show(this, $"外部ツール '{tool.DisplayName}' を削除しますか？", "削除確認", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
        {
            _workingTools.Remove(tool);
            RefreshList();
        }
    }

    private void BtnUp_Click(object? sender, EventArgs e)
    {
        if (_listView.SelectedItems.Count == 0) return;
        int index = _listView.SelectedIndices[0];
        if (index > 0)
        {
            var tool = _workingTools[index];
            _workingTools.RemoveAt(index);
            _workingTools.Insert(index - 1, tool);
            RefreshList();
            _listView.Items[index - 1].Selected = true;
            _listView.Items[index - 1].EnsureVisible();
        }
    }

    private void BtnDown_Click(object? sender, EventArgs e)
    {
        if (_listView.SelectedItems.Count == 0) return;
        int index = _listView.SelectedIndices[0];
        if (index < _workingTools.Count - 1)
        {
            var tool = _workingTools[index];
            _workingTools.RemoveAt(index);
            _workingTools.Insert(index + 1, tool);
            RefreshList();
            _listView.Items[index + 1].Selected = true;
            _listView.Items[index + 1].EnsureVisible();
        }
    }

    private void BtnOk_Click(object? sender, EventArgs e)
    {
        // 全体の整合性チェック (AltSlot / Id 重複)
        var slots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tool in _workingTools)
        {
            if (string.IsNullOrWhiteSpace(tool.Id))
            {
                MessageBox.Show(this, "IDが空の項目があります。", "保存エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }
            if (!ids.Add(tool.Id))
            {
                MessageBox.Show(this, $"ID '{tool.Id}' が重複しています。", "保存エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }

            if (!string.IsNullOrEmpty(tool.AltSlot))
            {
                if (!slots.Add(tool.AltSlot))
                {
                    MessageBox.Show(this, $"Altスロット '{tool.AltSlot}' が重複しています。\n同じ Altスロットは複数の外部ツールに割り当てできません。", "保存エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    DialogResult = DialogResult.None;
                    return;
                }
            }
        }

        try
        {
            _store.Tools = _workingTools;
            ExternalToolCommandStorage.Save(_store);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"保存に失敗しました。\n{ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            DialogResult = DialogResult.None;
        }
    }

    private ExternalToolCommandDefinition CloneDefinition(ExternalToolCommandDefinition src)
    {
        return new ExternalToolCommandDefinition
        {
            Id = src.Id,
            DisplayName = src.DisplayName,
            Description = src.Description,
            Alias = src.Alias,
            AltSlot = src.AltSlot,
            ExecutablePath = src.ExecutablePath,
            Arguments = src.Arguments,
            WorkingDirectory = src.WorkingDirectory,
            Enabled = src.Enabled
        };
    }

    private static string GenerateNextExternalToolId(IEnumerable<ExternalToolCommandDefinition> tools)
    {
        var used = new HashSet<string>(
            tools.Select(t => t.Id).Where(id => !string.IsNullOrWhiteSpace(id)),
            StringComparer.OrdinalIgnoreCase);

        for (var i = 1; i <= 9999; i++)
        {
            var candidate = $"external-tool-{i:000}";
            if (!used.Contains(candidate))
            {
                return candidate;
            }
        }

        return $"external-tool-{Guid.NewGuid():N}";
    }
}
