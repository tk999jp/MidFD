using MidFD.Models;

namespace MidFD.Dialogs;

public sealed class MarkSlotImportResultDialog : Form
{
    private readonly ListView _paths;
    private readonly Label _summary;
    private readonly Button _okButton;
    private int _registeredPathCount;

    public MarkSlotImportResultDialog(MarkSlotClipboardActionResult result)
    {
        Text = "KDSL_RESULT→現在Mark";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        ClientSize = new Size(760, 480);
        MinimumSize = new Size(620, 360);
        AutoScaleMode = AutoScaleMode.Font;
        KeyPreview = true;

        _summary = new Label
        {
            Dock = DockStyle.Top,
            Height = 70,
            Padding = new Padding(10, 8, 10, 4),
            AutoEllipsis = true,
            Text = BuildSummary(result)
        };
        _paths = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = false,
            ShowItemToolTips = true,
            BackColor = MidFDColors.ListNormalBack,
            ForeColor = MidFDColors.ListNormalFore
        };
        _paths.Columns.Add("種別", 62);
        _paths.Columns.Add("repo相対path", 300);
        _paths.Columns.Add("場所", 300);
        _paths.Columns.Add("状態", 62);
        AddPaths(result);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(0, 6, 10, 6) };
        _okButton = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, Dock = DockStyle.Right };
        footer.Controls.Add(_okButton);
        Controls.Add(_paths);
        Controls.Add(_summary);
        Controls.Add(footer);
        AcceptButton = _okButton;
        CancelButton = _okButton;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode is Keys.Enter or Keys.Escape)
            {
                DialogResult = DialogResult.OK;
                Close();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        };
        Resize += (_, _) => ResizeColumns();
        ResizeColumns();
    }

    private void AddPaths(MarkSlotClipboardActionResult result)
    {
        foreach (string fullPath in result.Paths)
        {
            string relativePath = GetRelativePath(result.RepositoryRoot, fullPath);
            var row = new ListViewItem(GetPathType(fullPath));
            row.SubItems.Add(relativePath);
            row.SubItems.Add(fullPath);
            row.SubItems.Add("存在");
            row.Tag = fullPath;
            row.ToolTipText = fullPath;
            _paths.Items.Add(row);
        }
        _registeredPathCount = result.Paths.Count;
        foreach (string unresolvedPath in result.UnresolvedPaths ?? Array.Empty<string>())
        {
            var row = new ListViewItem("-");
            row.SubItems.Add(unresolvedPath);
            row.SubItems.Add(unresolvedPath);
            row.SubItems.Add("未解決");
            row.ForeColor = MidFDColors.ListArchiveFore;
            row.Tag = unresolvedPath;
            row.ToolTipText = unresolvedPath;
            _paths.Items.Add(row);
        }
    }

    private static string BuildSummary(MarkSlotClipboardActionResult result)
    {
        string ignored = result.IgnoredEarlierResultCount > 0
            ? $"\n末尾RESULT採用（過去{result.IgnoredEarlierResultCount}件を無視）"
            : string.Empty;
        int unresolvedCount = result.UnresolvedPaths?.Count ?? 0;
        return $"現在Markへ登録: {result.RegisteredCount}件\n未解決: {unresolvedCount}件（削除・不存在: {result.MissingFileCount}件 / directory: {result.DirectoryPathCount}件） / 重複: {result.DuplicatePathCount}件{ignored}";
    }

    private static string GetRelativePath(string? repositoryRoot, string fullPath)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot)) return Path.GetFileName(fullPath) + $" ({fullPath})";
        try
        {
            string root = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(fullPath);
            if (candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return Path.GetRelativePath(root, candidate);
        }
        catch (Exception)
        {
            // Full path remains available in the 場所 column.
        }
        return Path.GetFileName(fullPath) + $" ({fullPath})";
    }

    private static string GetPathType(string fullPath) => Directory.Exists(fullPath) ? "DIR" : "FILE";

    private void ResizeColumns()
    {
        if (_paths.Columns.Count != 4) return;
        int width = Math.Max(280, _paths.ClientSize.Width - 4);
        _paths.Columns[0].Width = 62;
        _paths.Columns[1].Width = Math.Max(120, (int)(width * 0.40));
        _paths.Columns[2].Width = Math.Max(90, width - _paths.Columns[0].Width - _paths.Columns[1].Width - 62);
        _paths.Columns[3].Width = 62;
    }

    internal int RegisteredPathCountForTest => _registeredPathCount;
    internal IReadOnlyList<string> RelativePathsForTest => _paths.Items.Cast<ListViewItem>().Select(item => item.SubItems[1].Text).ToList();
    internal IReadOnlyList<string> FullPathsForTest => _paths.Items.Cast<ListViewItem>().Select(item => item.Tag as string ?? string.Empty).ToList();
    internal string SummaryTextForTest => _summary.Text;
    internal void HandleKeyForTest(Keys key) => OnKeyDown(new KeyEventArgs(key));
}
