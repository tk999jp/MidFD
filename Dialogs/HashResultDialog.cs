using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using MidFD.Models;

namespace MidFD.Dialogs;

public sealed class HashResultDialog : Form
{
    private readonly string _targetSummary;
    private readonly SevenZipHashAlgorithm _algorithm;
    private readonly string _output;

    private TextBox _outputTextBox = null!;

    public HashResultDialog(string targetSummary, SevenZipHashAlgorithm algorithm, string output)
    {
        _targetSummary = targetSummary;
        _algorithm = algorithm;
        _output = output;

        InitializeComponent();
    }

    private void InitializeComponent()
    {
        this.Text = "CRC/SHA 計算結果";
        this.FormBorderStyle = FormBorderStyle.Sizable;
        this.StartPosition = FormStartPosition.CenterParent;
        this.MinimizeBox = false;
        this.MaximizeBox = true;
        this.ClientSize = new Size(600, 480);
        this.MinimumSize = new Size(400, 300);
        this.AutoScaleMode = AutoScaleMode.Font;

        int currentTop = 16;
        int contentWidth = this.ClientSize.Width - 32;

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
        this.Controls.Add(targetLabel);
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
        this.Controls.Add(algoLabel);
        currentTop = algoLabel.Bottom + 8;

        _outputTextBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            Font = new Font("Consolas", 9f) ?? new Font(FontFamily.GenericMonospace, 9f),
            Left = 16,
            Top = currentTop,
            Width = contentWidth,
            Height = this.ClientSize.Height - currentTop - 70,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
            Text = _output.Replace("\r\n", "\n").Replace("\n", Environment.NewLine)
        };
        this.Controls.Add(_outputTextBox);

        var copyHashButton = new Button
        {
            Text = "ハッシュ値をコピー",
            Width = 140,
            Height = 30
        };
        copyHashButton.Click += (s, e) =>
        {
            string? hashOnly = TryExtractHashes();
            if (!string.IsNullOrEmpty(hashOnly))
            {
                Clipboard.SetText(hashOnly);
            }
            else
            {
                MessageBox.Show(this, "ハッシュ値を抽出できませんでした。結果全体コピーを使用してください。", "CRC/SHA 計算結果", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };

        var copyAllButton = new Button
        {
            Text = "結果をコピー(&C)",
            Width = 120,
            Height = 30
        };
        copyAllButton.Click += (s, e) =>
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

        this.Controls.Add(copyHashButton);
        this.Controls.Add(copyAllButton);
        this.Controls.Add(closeButton);

        FileOperationDialogLayoutHelper.ApplyModernBottomActionRow(
            this,
            new[] { copyHashButton, copyAllButton, closeButton },
            _outputTextBox.Bottom + 12,
            buttonGap: 10,
            contentGap: 16);

        this.AcceptButton = closeButton;
        this.CancelButton = closeButton;

        this.Shown += (s, e) =>
        {
            _outputTextBox.SelectionStart = 0;
            _outputTextBox.SelectionLength = 0;
            closeButton.Focus();
        };
    }

    private static string GetAlgorithmLabel(SevenZipHashAlgorithm algorithm)
    {
        return algorithm switch
        {
            SevenZipHashAlgorithm.Crc32 => "CRC-32",
            SevenZipHashAlgorithm.Crc64 => "CRC-64",
            SevenZipHashAlgorithm.Sha1 => "SHA-1",
            SevenZipHashAlgorithm.Sha256 => "SHA-256",
            SevenZipHashAlgorithm.All => "すべて (*)",
            _ => algorithm.ToString()
        };
    }

    private string? TryExtractHashes()
    {
        try
        {
            var lines = _output.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            int startLine = -1;
            int endLine = -1;

            // 7-Zip の出力テーブル（ハイフン行で囲まれた部分）を特定
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("---------"))
                {
                    if (startLine == -1) startLine = i + 1;
                    else { endLine = i; break; }
                }
            }

            if (startLine == -1 || endLine == -1 || startLine >= endLine) return null;

            // カラム位置を特定（ハイフン行の空白位置から推測）
            var dashedLine = lines[startLine - 1];
            var headerLine = lines[startLine - 2];
            var columns = ParseColumns(headerLine, dashedLine);

            var rows = new List<RowData>();
            for (int i = startLine; i < endLine; i++)
            {
                var row = ExtractRowData(lines[i], columns);
                if (row != null) rows.Add(row);
            }

            if (rows.Count == 0) return null;

            // 整形してコピー
            if (_algorithm == SevenZipHashAlgorithm.All)
            {
                // すべて(*)の場合は「Algo <TAB> Hash <TAB> Name」
                var result = new List<string>();
                foreach (var row in rows)
                {
                    foreach (var h in row.Hashes)
                    {
                        result.Add($"{h.Key}\t{h.Value}\t{row.FileName}");
                    }
                }
                return string.Join(Environment.NewLine, result);
            }
            else if (rows.Count == 1)
            {
                // 単一ファイルかつ特定アルゴリズムの場合は「Hash」のみ
                return rows[0].Hashes.Values.FirstOrDefault() ?? "";
            }
            else
            {
                // 複数ファイルの場合は「Hash <TAB> Name」
                var result = new List<string>();
                foreach (var row in rows)
                {
                    result.Add($"{row.Hashes.Values.FirstOrDefault() ?? ""}\t{row.FileName}");
                }
                return string.Join(Environment.NewLine, result);
            }
        }
        catch
        {
            return null;
        }
    }

    private record ColumnInfo(string Name, int Start, int Length);
    private record RowData(string FileName, Dictionary<string, string> Hashes);

    private List<ColumnInfo> ParseColumns(string headerLine, string dashedLine)
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
                // 空白が連続する場合をスキップ
                while (currentStart < dashedLine.Length && dashedLine[currentStart] == ' ') currentStart++;
                i = currentStart - 1;
            }
        }
        if (currentStart < headerLine.Length)
        {
            columns.Add(new ColumnInfo(headerLine.Substring(currentStart).Trim(), currentStart, -1));
        }
        return columns;
    }

    private RowData? ExtractRowData(string line, List<ColumnInfo> columns)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string fileName = "";

        foreach (var col in columns)
        {
            if (col.Start >= line.Length) continue;

            string value;
            if (col.Length == -1) value = line.Substring(col.Start).Trim();
            else value = line.Substring(col.Start, Math.Min(col.Length, line.Length - col.Start)).Trim();

            if (col.Name.Equals("Name", StringComparison.OrdinalIgnoreCase)) fileName = value;
            else if (!col.Name.Equals("Size", StringComparison.OrdinalIgnoreCase))
            {
                // カラム名が SHA256, CRC32 などであればハッシュとして扱う
                hashes[col.Name] = value;
            }
        }

        if (string.IsNullOrEmpty(fileName) || hashes.Count == 0) return null;
        return new RowData(fileName, hashes);
    }
}
