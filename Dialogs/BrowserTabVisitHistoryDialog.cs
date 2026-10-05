using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MidFD.Models;

namespace MidFD.Dialogs;

public sealed class BrowserTabVisitHistoryDialog : Form
{
    private readonly ListView _listView;
    private BrowserTabVisitHistoryDialogResult? _result;

    private BrowserTabVisitHistoryDialog(IReadOnlyList<BrowserTabVisitHistoryListItem> items)
    {
        Text = "タブ移動履歴";
        StartPosition = FormStartPosition.CenterParent;
        Width = 920;
        Height = 520;
        MinimumSize = new Size(720, 360);
        Font = new Font("Meiryo UI", 9F);
        KeyPreview = true;

        _listView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            MultiSelect = false,
            HideSelection = false,
            TabIndex = 0
        };
        _listView.Columns.Add("状態", 110);
        _listView.Columns.Add("カテゴリ", 150);
        _listView.Columns.Add("タブ", 180);
        _listView.Columns.Add("現在パス", 440);
        foreach (BrowserTabVisitHistoryListItem item in items)
        {
            ListViewItem row = new(
            [
                FormatDirection(item),
                item.CategoryDisplayName,
                item.TabDisplayName,
                item.CurrentPath
            ])
            {
                Tag = item,
                ForeColor = item.IsSelectable ? SystemColors.WindowText : SystemColors.GrayText
            };
            _listView.Items.Add(row);
        }

        Button moveButton = new()
        {
            Text = "移動",
            AutoSize = true,
            MinimumSize = new Size(96, 30)
        };
        moveButton.Click += (_, _) => ActivateSelectedItem();

        Button closeButton = new()
        {
            Text = "閉じる",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            MinimumSize = new Size(96, 30)
        };

        FlowLayoutPanel buttons = new()
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(8)
        };
        buttons.Controls.Add(closeButton);
        buttons.Controls.Add(moveButton);

        Controls.Add(_listView);
        Controls.Add(buttons);
        AcceptButton = moveButton;
        CancelButton = closeButton;

        _listView.DoubleClick += (_, _) => ActivateSelectedItem();
        _listView.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                ActivateSelectedItem();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                Close();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        };
        Shown += (_, _) => _listView.Focus();
    }

    public static BrowserTabVisitHistoryDialogResult? Show(
        IWin32Window owner,
        IReadOnlyList<BrowserTabVisitHistoryListItem> items)
    {
        using var dialog = new BrowserTabVisitHistoryDialog(items);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog._result : null;
    }

    private void ActivateSelectedItem()
    {
        BrowserTabVisitHistoryListItem? item = _listView.SelectedItems.Count == 0
            ? null
            : _listView.SelectedItems[0].Tag as BrowserTabVisitHistoryListItem;
        if (item == null || !item.IsSelectable)
        {
            return;
        }

        _result = new BrowserTabVisitHistoryDialogResult(item.Direction, item.Depth);
        DialogResult = DialogResult.OK;
        Close();
    }

    private static string FormatDirection(BrowserTabVisitHistoryListItem item) =>
        item.Direction switch
        {
            BrowserTabVisitHistoryListDirection.Back => $"← 戻る ({item.Depth})",
            BrowserTabVisitHistoryListDirection.Forward => $"→ 進む ({item.Depth})",
            _ => "● 現在"
        };
}
