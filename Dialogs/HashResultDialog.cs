using System.Drawing;
using System.Text;
using System.Windows.Forms;
using MidFD.Models;

namespace MidFD.Dialogs;

public sealed class HashResultDialog : Form
{
    private const int CompactClientHeight = 420;
    private const int ExpandedClientHeight = 600;
    private const int LogTextBoxHeight = 150;

    private readonly string _targetSummary;
    private readonly SevenZipHashAlgorithm _algorithm;
    private readonly string _output;
    private readonly IReadOnlyList<HashDisplayItem> _hashItems;

    private readonly ListView _hashListView;
    private readonly TextBox _outputTextBox;
    private readonly Button _toggleLogButton;
    private readonly Button _copyCsvButton;
    private readonly ContextMenuStrip _hashListContextMenu;
    private readonly ToolStripMenuItem _copyHashMenuItem;
    private readonly Control? _bottomActionRow;
    private readonly int _logTop;

    public HashResultDialog(string targetSummary, SevenZipHashAlgorithm algorithm, string output)
    {
        _targetSummary = targetSummary;
        _algorithm = algorithm;
        _output = output;
        _hashItems = ExtractHashItems();

        Text = "CRC/SHA 計算結果";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = true;
        ClientSize = new Size(720, CompactClientHeight);
        MinimumSize = new Size(520, 320);
        AutoScaleMode = AutoScaleMode.Font;

        int currentTop = 16;
        int contentWidth = ClientSize.Width - 32;

        var targetLabel = new Label
        {
            Text = $"対象: {_targetSummary}",
            Left = 16,
            Top = currentTop,
            Width = contentWidth,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            AutoSize = false
        };
        targetLabel.Height = FileOperationDialogLayoutHelper.MeasureLabelHeight(targetLabel, targetLabel.Width, 24);
        Controls.Add(targetLabel);
        currentTop = targetLabel.Bottom + 4;

        var algoLabel = new Label
        {
            Text = $"アルゴリズム: {GetAlgorithmLabel(_algorithm)}",
            Left = 16,
            Top = currentTop,
            Width = contentWidth,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            AutoSize = false,
            Height = 20
        };
        Controls.Add(algoLabel);
        currentTop = algoLabel.Bottom + 8;

        var listLabel = new Label
        {
            Text = "ハッシュ一覧:",
            Left = 16,
            Top = currentTop,
            Width = 120,
            Height = 20
        };
        Controls.Add(listLabel);
        currentTop = listLabel.Bottom + 4;

        _hashListView = new ListView
        {
            Left = 16,
            Top = currentTop,
            Width = contentWidth,
            Height = 170,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            HideSelection = false,
            MultiSelect = false
        };
        _hashListView.Columns.Add("対象", 220);
        _hashListView.Columns.Add("アルゴリズム", 100);
        _hashListView.Columns.Add("ハッシュ値", Math.Max(260, contentWidth - 324));
        _hashListView.SelectedIndexChanged += (_, _) => UpdateCopyCommandsState();
        _hashListView.DoubleClick += (_, _) => CopySelectedHashOrShowInfo();
        _hashListView.KeyDown += HashListView_KeyDown;
        _hashListView.MouseUp += HashListView_MouseUp;

        if (_hashItems.Count > 0)
        {
            foreach (var item in _hashItems)
            {
                var row = new ListViewItem(item.Target);
                row.SubItems.Add(item.Algorithm);
                row.SubItems.Add(item.HashValue);
                _hashListView.Items.Add(row);
            }

            _hashListView.Items[0].Selected = true;
            _hashListView.Items[0].Focused = true;
        }
        else
        {
            var row = new ListViewItem("ハッシュ値を抽出できませんでした");
            row.SubItems.Add("-");
            row.SubItems.Add("-");
            _hashListView.Items.Add(row);
        }

        Controls.Add(_hashListView);
        currentTop = _hashListView.Bottom + 8;

        _copyCsvButton = new Button
        {
            Text = "全件をCSVコピー",
            Width = 130,
            Height = 30,
            Left = 16,
            Top = currentTop,
            Anchor = AnchorStyles.Top | AnchorStyles.Left
        };
        _copyCsvButton.Click += (_, _) => CopyAllAsCsvOrShowInfo();
        Controls.Add(_copyCsvButton);

        _toggleLogButton = new Button
        {
            Text = "詳細ログを表示",
            Width = 120,
            Height = 30,
            Left = ClientSize.Width - 136,
            Top = currentTop,
            Anchor = AnchorStyles.Top | AnchorStyles.Right
        };
        _toggleLogButton.Click += (_, _) => ToggleLogVisibility();
        Controls.Add(_toggleLogButton);
        currentTop = _copyCsvButton.Bottom + 8;
        _logTop = currentTop;

        _outputTextBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            Font = new Font("Consolas", 9f),
            Left = 16,
            Top = _logTop,
            Width = contentWidth,
            Height = LogTextBoxHeight,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Text = _output.Replace("\r\n", "\n").Replace("\n", Environment.NewLine),
            Visible = false
        };
        Controls.Add(_outputTextBox);

        var copyAllButton = new Button
        {
            Text = "結果をコピー(&C)",
            Width = 120,
            Height = 30
        };
        copyAllButton.Click += (_, _) =>
        {
            if (!string.IsNullOrEmpty(_outputTextBox.Text))
            {
                Clipboard.SetText(_outputTextBox.Text);
            }
        };

        var closeButton = new Button
        {
            Text = "閉じる",
            Width = 100,
            Height = 30,
            DialogResult = DialogResult.OK
        };

        Controls.Add(copyAllButton);
        Controls.Add(closeButton);

        _copyHashMenuItem = new ToolStripMenuItem("ハッシュ値をコピー");
        _copyHashMenuItem.Click += (_, _) => CopySelectedHashOrShowInfo();
        _hashListContextMenu = new ContextMenuStrip();
        _hashListContextMenu.Items.Add(_copyHashMenuItem);

        _bottomActionRow = FileOperationDialogLayoutHelper.ApplyModernBottomActionRow(
            this,
            new[] { copyAllButton, closeButton },
            currentTop,
            buttonGap: 10,
            contentGap: 16);

        AcceptButton = closeButton;
        CancelButton = closeButton;

        Shown += (_, _) =>
        {
            _outputTextBox.SelectionStart = 0;
            _outputTextBox.SelectionLength = 0;
            _hashListView.Focus();
        };

        UpdateCopyCommandsState();
    }

    private void ToggleLogVisibility()
    {
        ApplyLogVisibility(!_outputTextBox.Visible);
    }

    private void ApplyLogVisibility(bool visible)
    {
        SuspendLayout();
        try
        {
            _outputTextBox.Visible = visible;
            _toggleLogButton.Text = visible ? "詳細ログを隠す" : "詳細ログを表示";

            if (_bottomActionRow != null)
            {
                int contentBottom = _logTop + (visible ? _outputTextBox.Height : 0);
                ClientSize = new Size(ClientSize.Width, visible ? ExpandedClientHeight : CompactClientHeight);
                _bottomActionRow.Height = _bottomActionRow.PreferredSize.Height;
                _bottomActionRow.Top = contentBottom;
            }
            else
            {
                ClientSize = new Size(ClientSize.Width, visible ? ExpandedClientHeight : CompactClientHeight);
            }
        }
        finally
        {
            ResumeLayout(performLayout: true);
        }
    }

    private void UpdateCopyCommandsState()
    {
        bool canCopyHash = GetSelectedHashItem() != null;
        bool canCopyCsv = _hashItems.Count > 0;
        _copyHashMenuItem.Enabled = canCopyHash;
        _copyCsvButton.Enabled = canCopyCsv;
    }

    private void HashListView_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.Shift && e.KeyCode == Keys.C)
        {
            CopyAllAsCsvOrShowInfo();
            e.SuppressKeyPress = true;
            e.Handled = true;
            return;
        }

        if (e.Control && e.KeyCode == Keys.C)
        {
            CopySelectedHashOrShowInfo();
            e.SuppressKeyPress = true;
            e.Handled = true;
        }
    }

    private void HashListView_MouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right)
        {
            return;
        }

        ListViewItem? hitItem = _hashListView.GetItemAt(e.X, e.Y);
        if (hitItem != null)
        {
            _hashListView.SelectedIndices.Clear();
            hitItem.Selected = true;
            hitItem.Focused = true;
        }

        UpdateCopyCommandsState();
        _hashListContextMenu.Show(_hashListView, e.Location);
    }

    private void CopySelectedHashOrShowInfo()
    {
        HashDisplayItem? item = GetSelectedHashItem();
        if (item != null)
        {
            Clipboard.SetText(item.HashValue);
            return;
        }

        MessageBox.Show(this, "ハッシュ値を抽出できませんでした。結果全体コピーを使用してください。", "CRC/SHA 計算結果", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void CopyAllAsCsvOrShowInfo()
    {
        if (_hashItems.Count == 0)
        {
            MessageBox.Show(this, "CSV 化できるハッシュ一覧がありません。", "CRC/SHA 計算結果", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("\"対象\",\"アルゴリズム\",\"ハッシュ値\"");
        foreach (var item in _hashItems)
        {
            sb.Append('"').Append(EscapeCsv(item.Target)).Append("\",")
                .Append('"').Append(EscapeCsv(item.Algorithm)).Append("\",")
                .Append('"').Append(EscapeCsv(item.HashValue)).AppendLine("\"");
        }

        Clipboard.SetText(sb.ToString());
    }

    private static string EscapeCsv(string value)
    {
        return (value ?? string.Empty).Replace("\"", "\"\"");
    }

    private HashDisplayItem? GetSelectedHashItem()
    {
        if (_hashListView.SelectedIndices.Count == 0)
        {
            return null;
        }

        int index = _hashListView.SelectedIndices[0];
        return index >= 0 && index < _hashItems.Count
            ? _hashItems[index]
            : null;
    }

    private static string GetAlgorithmLabel(SevenZipHashAlgorithm algorithm)
    {
        return algorithm switch
        {
            SevenZipHashAlgorithm.Crc32 => "CRC-32",
            SevenZipHashAlgorithm.Crc64 => "CRC-64",
            SevenZipHashAlgorithm.Sha1 => "SHA-1",
            SevenZipHashAlgorithm.Sha256 => "SHA-256",
            SevenZipHashAlgorithm.All => "すべて",
            _ => algorithm.ToString()
        };
    }

    private IReadOnlyList<HashDisplayItem> ExtractHashItems()
    {
        try
        {
            if (!TryParseRows(out var rows) || rows.Count == 0)
            {
                return Array.Empty<HashDisplayItem>();
            }

            var items = new List<HashDisplayItem>();
            foreach (var row in rows)
            {
                foreach (var hash in row.Hashes)
                {
                    if (string.IsNullOrWhiteSpace(hash.Value))
                    {
                        continue;
                    }

                    items.Add(new HashDisplayItem(
                        row.FileName,
                        NormalizeHashLabel(hash.Key),
                        hash.Value));
                }
            }

            return items;
        }
        catch
        {
            return Array.Empty<HashDisplayItem>();
        }
    }

    private static string NormalizeHashLabel(string value)
    {
        return value.ToUpperInvariant() switch
        {
            "CRC32" => "CRC-32",
            "CRC64" => "CRC-64",
            "SHA1" => "SHA-1",
            "SHA256" => "SHA-256",
            _ => value
        };
    }

    private bool TryParseRows(out List<RowData> rows)
    {
        rows = new List<RowData>();
        var lines = _output.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        int startLine = -1;
        int endLine = -1;

        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("---------"))
            {
                if (startLine == -1)
                {
                    startLine = i + 1;
                }
                else
                {
                    endLine = i;
                    break;
                }
            }
        }

        if (startLine == -1 || endLine == -1 || startLine >= endLine)
        {
            return false;
        }

        var dashedLine = lines[startLine - 1];
        var headerLine = lines[startLine - 2];
        var columns = ParseColumns(headerLine, dashedLine);

        for (int i = startLine; i < endLine; i++)
        {
            var row = ExtractRowData(lines[i], columns);
            if (row != null)
            {
                rows.Add(row);
            }
        }

        return rows.Count > 0;
    }

    private static List<ColumnInfo> ParseColumns(string headerLine, string dashedLine)
    {
        var columns = new List<ColumnInfo>();
        int currentStart = 0;

        for (int i = 0; i < dashedLine.Length; i++)
        {
            if (dashedLine[i] == ' ')
            {
                if (i > currentStart)
                {
                    string name = headerLine.Substring(currentStart, i - currentStart).Trim();
                    columns.Add(new ColumnInfo(name, currentStart, i - currentStart));
                }

                currentStart = i + 1;
                while (currentStart < dashedLine.Length && dashedLine[currentStart] == ' ')
                {
                    currentStart++;
                }
                i = currentStart - 1;
            }
        }

        if (currentStart < headerLine.Length)
        {
            columns.Add(new ColumnInfo(headerLine.Substring(currentStart).Trim(), currentStart, -1));
        }

        return columns;
    }

    private static RowData? ExtractRowData(string line, List<ColumnInfo> columns)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string fileName = string.Empty;

        foreach (var col in columns)
        {
            if (col.Start >= line.Length)
            {
                continue;
            }

            string value = col.Length == -1
                ? line.Substring(col.Start).Trim()
                : line.Substring(col.Start, Math.Min(col.Length, line.Length - col.Start)).Trim();

            if (col.Name.Equals("Name", StringComparison.OrdinalIgnoreCase))
            {
                fileName = value;
            }
            else if (!col.Name.Equals("Size", StringComparison.OrdinalIgnoreCase))
            {
                hashes[col.Name] = value;
            }
        }

        return string.IsNullOrEmpty(fileName) || hashes.Count == 0
            ? null
            : new RowData(fileName, hashes);
    }

    private sealed record HashDisplayItem(string Target, string Algorithm, string HashValue);
    private sealed record ColumnInfo(string Name, int Start, int Length);
    private sealed record RowData(string FileName, Dictionary<string, string> Hashes);
}
