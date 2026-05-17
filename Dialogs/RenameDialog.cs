using MidFD.Models;
using MidFD.Services;

namespace MidFD.Dialogs;

public sealed class RenameDialog : Form
{
    private readonly IReadOnlyList<string> _sourcePaths;
    private readonly RadioButton _templateModeRadioButton;
    private readonly RadioButton _regexModeRadioButton;
    private readonly Label _modeLabel;
    private readonly Label _templateLabel;
    private readonly TextBox _templateTextBox;
    private readonly Label _startNumberLabel;
    private readonly NumericUpDown _startNumberUpDown;
    private readonly Label _numberWidthLabel;
    private readonly NumericUpDown _numberWidthUpDown;
    private readonly Label _templateHintLabel;
    private readonly Label _regexPatternLabel;
    private readonly TextBox _regexPatternTextBox;
    private readonly Label _regexReplacementLabel;
    private readonly TextBox _regexReplacementTextBox;
    private readonly CheckBox _ignoreCaseCheckBox;
    private readonly CheckBox _multilineCheckBox;
    private readonly CheckBox _globalCheckBox;
    private readonly Label _regexHintLabel;
    private readonly CheckBox _rememberTemplateCheckBox;
    private readonly ListView _previewListView;
    private readonly Label _summaryLabel;
    private readonly Label _detailLabel;
    private readonly Button _okButton;
    private readonly Button _cancelButton;
    private Control? _bottomActionPanel;

    private RenamePreviewResult _latestPreview = new();

    public RenameDialogResult Result { get; private set; } = new();

    public RenameDialog(IReadOnlyList<string> sourcePaths, string initialTemplate, bool rememberTemplate)
    {
        const int sideMargin = 16;
        const int topMargin = 16;
        _sourcePaths = sourcePaths;

        Text = "Rename Preview";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(820, 620);
        KeyPreview = true;

        int contentWidth = ClientSize.Width - (sideMargin * 2);
        int currentTop = topMargin;

        _modeLabel = new Label
        {
            Left = sideMargin,
            Top = currentTop + 2,
            Width = 110,
            Height = 20,
            Text = "モード"
        };

        _templateModeRadioButton = new RadioButton
        {
            Left = _modeLabel.Right + 6,
            Top = currentTop,
            Width = 120,
            Height = 24,
            Text = "テンプレート",
            Checked = true,
            AutoSize = true
        };

        _regexModeRadioButton = new RadioButton
        {
            Left = _templateModeRadioButton.Right + 12,
            Top = currentTop,
            Width = 140,
            Height = 24,
            Text = "正規表現置換",
            AutoSize = true
        };
        Controls.Add(_modeLabel);
        Controls.Add(_templateModeRadioButton);
        Controls.Add(_regexModeRadioButton);

        currentTop = Math.Max(_templateModeRadioButton.Bottom, _regexModeRadioButton.Bottom) + 12;
        int inputControlLeft = _modeLabel.Right + 6;
        int inputControlWidth = ClientSize.Width - sideMargin - inputControlLeft;

        // Template Mode Controls
        _templateLabel = new Label
        {
            Left = sideMargin,
            Top = currentTop + 4,
            Width = 110,
            Height = 20,
            Text = "テンプレート"
        };

        _templateTextBox = new TextBox
        {
            Left = inputControlLeft,
            Top = currentTop,
            Width = inputControlWidth,
            Height = 24,
            Text = string.IsNullOrWhiteSpace(initialTemplate) ? "$F$E" : initialTemplate
        };
        Controls.Add(_templateLabel);
        Controls.Add(_templateTextBox);

        int templateNextTop = _templateTextBox.Bottom + 12;

        _startNumberLabel = new Label
        {
            Left = sideMargin,
            Top = templateNextTop + 4,
            Width = 110,
            Height = 20,
            Text = "開始番号"
        };

        _startNumberUpDown = new NumericUpDown
        {
            Left = inputControlLeft,
            Top = templateNextTop,
            Width = 100,
            Height = 24,
            Minimum = 0,
            Maximum = 999999,
            Value = 1
        };

        _numberWidthLabel = new Label
        {
            Left = _startNumberUpDown.Right + 20,
            Top = templateNextTop + 4,
            Width = 60,
            Height = 20,
            Text = "桁数",
            AutoSize = true
        };

        _numberWidthUpDown = new NumericUpDown
        {
            Left = _numberWidthLabel.Right + 8,
            Top = templateNextTop,
            Width = 80,
            Height = 24,
            Minimum = 1,
            Maximum = 10,
            Value = 1
        };

        _templateHintLabel = new Label
        {
            Left = _numberWidthUpDown.Right + 12,
            Top = templateNextTop + 4,
            Width = ClientSize.Width - sideMargin - (_numberWidthUpDown.Right + 12),
            Height = 20,
            Text = "使用可能: $F / $E / $D / $N / $mN(桁指定)",
            TextAlign = ContentAlignment.MiddleRight,
            AutoEllipsis = true
        };
        Controls.Add(_startNumberLabel);
        Controls.Add(_startNumberUpDown);
        Controls.Add(_numberWidthLabel);
        Controls.Add(_numberWidthUpDown);
        Controls.Add(_templateHintLabel);

        // Regex Mode Controls
        _regexPatternLabel = new Label
        {
            Left = sideMargin,
            Top = currentTop + 4,
            Width = 110,
            Height = 20,
            Text = "検索パターン",
            Visible = false
        };

        _regexPatternTextBox = new TextBox
        {
            Left = inputControlLeft,
            Top = currentTop,
            Width = inputControlWidth,
            Height = 24,
            Visible = false
        };

        int regexNextTop = _regexPatternTextBox.Bottom + 12;

        _regexReplacementLabel = new Label
        {
            Left = sideMargin,
            Top = regexNextTop + 4,
            Width = 110,
            Height = 20,
            Text = "置換パターン",
            Visible = false
        };

        _regexReplacementTextBox = new TextBox
        {
            Left = inputControlLeft,
            Top = regexNextTop,
            Width = inputControlWidth,
            Height = 24,
            Visible = false
        };

        int regexOptionsTop = _regexReplacementTextBox.Bottom + 12;

        _ignoreCaseCheckBox = new CheckBox
        {
            Left = inputControlLeft,
            Top = regexOptionsTop,
            Width = 110,
            Height = 24,
            Text = "IgnoreCase",
            Visible = false,
            AutoSize = true
        };

        _multilineCheckBox = new CheckBox
        {
            Left = _ignoreCaseCheckBox.Right + 12,
            Top = regexOptionsTop,
            Width = 100,
            Height = 24,
            Text = "Multiline",
            Visible = false,
            AutoSize = true
        };

        _globalCheckBox = new CheckBox
        {
            Left = _multilineCheckBox.Right + 12,
            Top = regexOptionsTop,
            Width = 170,
            Height = 24,
            Text = "Global(全一致置換)",
            Checked = true,
            Visible = false,
            AutoSize = true
        };

        _regexHintLabel = new Label
        {
            Left = _globalCheckBox.Right + 8,
            Top = regexOptionsTop + 2,
            Width = ClientSize.Width - sideMargin - (_globalCheckBox.Right + 8),
            Height = 20,
            Text = "対象: ファイル名全体",
            TextAlign = ContentAlignment.MiddleRight,
            Visible = false,
            AutoEllipsis = true
        };
        Controls.Add(_regexPatternLabel);
        Controls.Add(_regexPatternTextBox);
        Controls.Add(_regexReplacementLabel);
        Controls.Add(_regexReplacementTextBox);
        Controls.Add(_ignoreCaseCheckBox);
        Controls.Add(_multilineCheckBox);
        Controls.Add(_globalCheckBox);
        Controls.Add(_regexHintLabel);

        int commonAreaTop = Math.Max(templateNextTop + _startNumberUpDown.Height, regexOptionsTop + _globalCheckBox.Height) + 12;

        _rememberTemplateCheckBox = new CheckBox
        {
            Left = sideMargin,
            Top = commonAreaTop,
            Width = contentWidth,
            Height = 24,
            Text = "前回使ったテンプレートを記録する",
            Checked = rememberTemplate,
            AutoSize = true
        };
        Controls.Add(_rememberTemplateCheckBox);

        _previewListView = new ListView
        {
            Left = sideMargin,
            Top = _rememberTemplateCheckBox.Bottom + 12,
            Width = contentWidth,
            Height = 320,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            HideSelection = false
        };
        _previewListView.Columns.Add("変更前", 250);
        _previewListView.Columns.Add("変更後", 280);
        _previewListView.Columns.Add("状態", 220);
        Controls.Add(_previewListView);

        _summaryLabel = new Label
        {
            Left = sideMargin,
            Width = contentWidth,
            Height = 24,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
        Controls.Add(_summaryLabel);

        _detailLabel = new Label
        {
            Left = sideMargin,
            Width = contentWidth,
            Height = 36,
            TextAlign = ContentAlignment.TopLeft,
            AutoEllipsis = true
        };
        Controls.Add(_detailLabel);

        _okButton = new Button
        {
            Text = "OK",
            Width = 80,
            Height = 30,
            DialogResult = DialogResult.OK,
            MinimumSize = new Size(80, 30)
        };

        _cancelButton = new Button
        {
            Text = "Cancel",
            Width = 80,
            Height = 30,
            DialogResult = DialogResult.Cancel,
            MinimumSize = new Size(80, 30)
        };
        // ボタンは ApplyModernBottomActionRow 内で追加されるため、ここでは Controls.Add しない

        AcceptButton = _okButton;
        CancelButton = _cancelButton;

        _templateModeRadioButton.TabIndex = 0;
        _regexModeRadioButton.TabIndex = 1;
        _templateTextBox.TabIndex = 2;
        _startNumberUpDown.TabIndex = 3;
        _numberWidthUpDown.TabIndex = 4;
        _regexPatternTextBox.TabIndex = 2;
        _regexReplacementTextBox.TabIndex = 3;
        _ignoreCaseCheckBox.TabIndex = 4;
        _multilineCheckBox.TabIndex = 5;
        _globalCheckBox.TabIndex = 6;
        _rememberTemplateCheckBox.TabIndex = 7;
        _previewListView.TabIndex = 8;
        _okButton.TabIndex = 9;
        _cancelButton.TabIndex = 10;

        _templateModeRadioButton.CheckedChanged += (_, _) => OnModeChanged();
        _regexModeRadioButton.CheckedChanged += (_, _) => OnModeChanged();
        _templateTextBox.TextChanged += (_, _) => RefreshPreview();
        _startNumberUpDown.ValueChanged += (_, _) => RefreshPreview();
        _numberWidthUpDown.ValueChanged += (_, _) => RefreshPreview();
        _regexPatternTextBox.TextChanged += (_, _) => RefreshPreview();
        _regexReplacementTextBox.TextChanged += (_, _) => RefreshPreview();
        _ignoreCaseCheckBox.CheckedChanged += (_, _) => RefreshPreview();
        _multilineCheckBox.CheckedChanged += (_, _) => RefreshPreview();
        _globalCheckBox.CheckedChanged += (_, _) => RefreshPreview();

        // 表示前にレイアウトを確定させる
        UpdateModeUi();
        LayoutBottomArea();
        RefreshPreview();

        Shown += (_, _) =>
        {
            if (_templateTextBox.Visible)
            {
                _templateTextBox.Focus();
                _templateTextBox.SelectAll();
            }
            else
            {
                _regexPatternTextBox.Focus();
                _regexPatternTextBox.SelectAll();
            }
        };

        FormClosing += OnFormClosing;
    }

    public static RenameDialogResult Show(IWin32Window owner, IReadOnlyList<string> sourcePaths, string initialTemplate, bool rememberTemplate)
    {
        using var dialog = new RenameDialog(sourcePaths, initialTemplate, rememberTemplate);
        return dialog.ShowDialog(owner) == DialogResult.OK
            ? dialog.Result
            : new RenameDialogResult { Confirmed = false };
    }

    private void RefreshPreview()
    {
        _latestPreview = IsRegexMode
            ? RenamePreviewService.BuildRegexPreview(_sourcePaths, BuildRegexOptions())
            : RenamePreviewService.BuildPreview(_sourcePaths, BuildTemplateOptions());

        _previewListView.BeginUpdate();
        _previewListView.Items.Clear();
        foreach (var item in _latestPreview.Items)
        {
            var listItem = new ListViewItem(item.SourceName);
            listItem.SubItems.Add(item.DestinationName);
            listItem.SubItems.Add(item.Status);

            if (item.HasError)
            {
                listItem.ForeColor = Color.IndianRed;
            }
            else if (!item.WillRename)
            {
                listItem.ForeColor = Color.DarkGray;
            }
            else
            {
                listItem.ForeColor = Color.DarkGreen;
            }

            _previewListView.Items.Add(listItem);
        }
        _previewListView.EndUpdate();

        _summaryLabel.Text = _latestPreview.Summary;
        _summaryLabel.ForeColor = _latestPreview.HasErrors
            ? Color.IndianRed
            : (_latestPreview.HasRenames ? Color.DarkGreen : SystemColors.ControlText);
        _detailLabel.Text = BuildDetailMessage();
        _detailLabel.ForeColor = _latestPreview.HasErrors ? Color.IndianRed : Color.DimGray;
        LayoutBottomArea();
        _okButton.Enabled = _latestPreview.Items.Count > 0 && !_latestPreview.HasErrors;
    }

    private RenameTemplateOptions BuildTemplateOptions()
    {
        return new RenameTemplateOptions
        {
            Template = _templateTextBox.Text,
            StartNumber = Decimal.ToInt32(_startNumberUpDown.Value),
            NumberWidth = Decimal.ToInt32(_numberWidthUpDown.Value)
        };
    }

    private RenameRegexOptions BuildRegexOptions()
    {
        return new RenameRegexOptions
        {
            Pattern = _regexPatternTextBox.Text,
            Replacement = _regexReplacementTextBox.Text,
            IgnoreCase = _ignoreCaseCheckBox.Checked,
            Multiline = _multilineCheckBox.Checked,
            Global = _globalCheckBox.Checked
        };
    }

    private bool IsRegexMode => _regexModeRadioButton.Checked;

    private void OnModeChanged()
    {
        UpdateModeUi();
        RefreshPreview();
    }

    private void UpdateModeUi()
    {
        bool isRegex = IsRegexMode;

        _templateLabel.Visible = !isRegex;
        _templateTextBox.Visible = !isRegex;
        _startNumberLabel.Visible = !isRegex;
        _startNumberUpDown.Visible = !isRegex;
        _numberWidthLabel.Visible = !isRegex;
        _numberWidthUpDown.Visible = !isRegex;
        _templateHintLabel.Visible = !isRegex;

        _regexPatternLabel.Visible = isRegex;
        _regexPatternTextBox.Visible = isRegex;
        _regexReplacementLabel.Visible = isRegex;
        _regexReplacementTextBox.Visible = isRegex;
        _ignoreCaseCheckBox.Visible = isRegex;
        _multilineCheckBox.Visible = isRegex;
        _globalCheckBox.Visible = isRegex;
        _regexHintLabel.Visible = isRegex;

        _rememberTemplateCheckBox.Enabled = !isRegex;
        if (isRegex)
        {
            _rememberTemplateCheckBox.Checked = false;
            _regexPatternTextBox.Focus();
        }
        else
        {
            _templateTextBox.Focus();
        }
    }

    private string BuildDetailMessage()
    {
        if (_latestPreview.HasErrors)
        {
            return $"{_latestPreview.Detail} / 問題のある行があるため OK は無効です。";
        }

        if (IsRegexMode)
        {
            string globalText = _globalCheckBox.Checked
                ? "Global ON: すべての一致を置換します。"
                : "Global OFF: 最初の 1 件だけ置換します。";
            return $"{_latestPreview.Detail} 対象はファイル名全体（拡張子込み）です。{globalText}";
        }

        return _latestPreview.Detail;
    }

    private void LayoutBottomArea()
    {
        const int sideMargin = 16;
        const int bottomMargin = 16;
        const int buttonGap = 10;
        const int sectionGap = 10;
        const int minimumPreviewHeight = 180;

        SuspendLayout();
        try
        {
            int contentWidth = ClientSize.Width - (sideMargin * 2);
            _summaryLabel.Width = contentWidth;
            _summaryLabel.Height = FileOperationDialogLayoutHelper.MeasureLabelHeight(_summaryLabel, contentWidth, 24);
            _detailLabel.Width = contentWidth;
            _detailLabel.Height = FileOperationDialogLayoutHelper.MeasureLabelHeight(_detailLabel, contentWidth, 36);

            // アクション行を未生成なら生成、生成済みなら位置のみ更新
            if (_bottomActionPanel == null)
            {
                // 初回はフォームが縮まないよう、現在のクライアント領域から推定ボタン行高さを引いた値を contentBottom とする
                int initialContentBottom = Math.Max(0, ClientSize.Height - 60);

                _bottomActionPanel = FileOperationDialogLayoutHelper.ApplyModernBottomActionRow(
                    this,
                    new[] { _okButton, _cancelButton },
                    initialContentBottom,
                    buttonGap: buttonGap,
                    contentGap: sectionGap);
            }

            if (_bottomActionPanel == null) return;

            // ボタン行の上端を基準にラベルを配置
            _detailLabel.Top = _bottomActionPanel.Top - sectionGap - _detailLabel.Height;
            _summaryLabel.Top = _detailLabel.Top - 4 - _summaryLabel.Height;

            _previewListView.Width = contentWidth;
            _previewListView.Height = Math.Max(minimumPreviewHeight, _summaryLabel.Top - sectionGap - _previewListView.Top);

            // フォーム自体の最小高さを保証
            int minimumHeight = _previewListView.Top
                + minimumPreviewHeight
                + sectionGap
                + _summaryLabel.Height
                + 4
                + _detailLabel.Height
                + sectionGap
                + _bottomActionPanel.Height
                + bottomMargin;

            if (ClientSize.Height < minimumHeight)
            {
                ClientSize = new Size(ClientSize.Width, minimumHeight);
                // ClientSize 変更によって Layout イベントが走り、再度ここへ来る可能性があるため return
                return;
            }
        }
        finally
        {
            ResumeLayout();
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (DialogResult != DialogResult.OK)
        {
            return;
        }

        if (_latestPreview.HasErrors)
        {
            MessageBox.Show("問題のある行があるため実行できません。", "Rename", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            e.Cancel = true;
            return;
        }

        Result = new RenameDialogResult
        {
            Confirmed = true,
            Mode = IsRegexMode ? RenameDialogMode.Regex : RenameDialogMode.Template,
            TemplateOptions = BuildTemplateOptions(),
            RegexOptions = BuildRegexOptions(),
            Preview = _latestPreview,
            RememberTemplate = !IsRegexMode && _rememberTemplateCheckBox.Checked,
            LastTemplateCandidate = !IsRegexMode ? _templateTextBox.Text : string.Empty
        };
    }
}
