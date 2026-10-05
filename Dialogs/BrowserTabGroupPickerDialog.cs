using MidFD.Models;

namespace MidFD.Dialogs;

public static class BrowserTabGroupPickerDialog
{
    public static Guid? Show(IWin32Window? owner, string title, IReadOnlyList<BrowserTabGroup> groups)
    {
        if (groups.Count == 0) return null;

        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(360, 260),
            AutoScaleMode = AutoScaleMode.Font
        };
        var prompt = new Label { Left = 12, Top = 12, Width = 336, Height = 32, Text = "対象のタブグループを選択してください。" };
        var list = new ListBox { Left = 12, Top = 48, Width = 336, Height = 164, IntegralHeight = false, DisplayMember = nameof(GroupChoice.Label) };
        for (int index = 0; index < groups.Count; index++)
        {
            BrowserTabGroup group = groups[index];
            list.Items.Add(new GroupChoice(group, $"{index + 1}. {group.DisplayName} ({group.MemberTabIds.Count} tabs)"));
        }
        list.SelectedIndex = 0;
        var accept = new Button { Text = "OK", Left = 180, Top = 222, Width = 78, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 270, Top = 222, Width = 78, DialogResult = DialogResult.Cancel };
        form.Controls.AddRange([prompt, list, accept, cancel]);
        form.AcceptButton = accept;
        form.CancelButton = cancel;
        list.DoubleClick += (_, _) => form.DialogResult = DialogResult.OK;
        return form.ShowDialog(owner) == DialogResult.OK && list.SelectedItem is GroupChoice choice
            ? choice.Group.Id
            : null;
    }

    private sealed record GroupChoice(BrowserTabGroup Group, string Label);
}
