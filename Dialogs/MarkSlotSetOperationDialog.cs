using MidFD.Models;

namespace MidFD.Dialogs;

public sealed class MarkSlotSetOperationDialog : Form
{
    private readonly Func<IReadOnlyList<MarkSlotDialog.MarkSlotSummaryViewItem>> _slotSummaryProvider;
    private readonly Func<int, int, string, MarkSlotSetOperationPreviewResult> _previewProvider;
    private readonly Func<MarkSlotSetOperationSaveRequest, string> _saveResultAction;
    private readonly Func<MarkSlotSetOperationPreviewResult, string> _applyToCurrentTabAction;
    private readonly int _preferredSlotNumber;
    private readonly ComboBox _slotAComboBox;
    private readonly ComboBox _slotBComboBox;
    private readonly ComboBox _operationComboBox;
    private readonly ComboBox _targetSlotComboBox;
    private readonly Label _hintLabel;
    private readonly Label _summaryLabel;
    private readonly Label _targetSlotLabel;
    private readonly ListView _previewListView;
    private readonly Button _saveButton;
    private readonly Button _applyButton;
    private readonly Button _closeButton;
    private MarkSlotSetOperationPreviewResult? _currentPreview;

    private sealed record SlotComboItem(int SlotNumber, string DisplayText)
    {
        public override string ToString() => DisplayText;
    }

    private sealed record OperationComboItem(string Kind, string Label)
    {
        public override string ToString() => Label;
    }

    public MarkSlotSetOperationDialog(
        Func<IReadOnlyList<MarkSlotDialog.MarkSlotSummaryViewItem>> slotSummaryProvider,
        Func<int, int, string, MarkSlotSetOperationPreviewResult> previewProvider,
        Func<MarkSlotSetOperationSaveRequest, string> saveResultAction,
        Func<MarkSlotSetOperationPreviewResult, string> applyToCurrentTabAction,
        int preferredSlotNumber)
    {
        _slotSummaryProvider = slotSummaryProvider;
        _previewProvider = previewProvider;
        _saveResultAction = saveResultAction;
        _applyToCurrentTabAction = applyToCurrentTabAction;
        _preferredSlotNumber = preferredSlotNumber;

        Text = "スロット演算";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = true;
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(980, 620);
        MinimumSize = new Size(820, 520);

        int left = 16;
        int top = 16;
        int comboWidth = 220;
        int gap = 10;
        int rowHeight = 27;

        var slotALabel = new Label { Left = left, Top = top, Width = 70, Text = "Slot A" };
        _slotAComboBox = new ComboBox
        {
            Left = slotALabel.Right + 6,
            Top = top - 2,
            Width = comboWidth,
            DropDownStyle = ComboBoxStyle.DropDownList
        };

        var operationLabel = new Label { Left = _slotAComboBox.Right + gap, Top = top, Width = 70, Text = "演算" };
        _operationComboBox = new ComboBox
        {
            Left = operationLabel.Right + 6,
            Top = top - 2,
            Width = 140,
            DropDownStyle = ComboBoxStyle.DropDownList
        };

        var slotBLabel = new Label { Left = _operationComboBox.Right + gap, Top = top, Width = 70, Text = "Slot B" };
        _slotBComboBox = new ComboBox
        {
            Left = slotBLabel.Right + 6,
            Top = top - 2,
            Width = comboWidth,
            DropDownStyle = ComboBoxStyle.DropDownList
        };

        top += rowHeight + 12;
        _hintLabel = new Label
        {
            Left = left,
            Top = top,
            Width = ClientSize.Width - 32,
            Height = 42,
            Text = "演算結果はプレビューだけでは反映されません。現在タブへ反映する場合は「現在タブへ適用...」、スロットへ保存する場合は「Save-as-Slot...」を使ってください。"
        };

        top = _hintLabel.Bottom + 8;
        _summaryLabel = new Label
        {
            Left = left,
            Top = top,
            Width = ClientSize.Width - 32,
            Height = 40,
            ForeColor = Color.LightSkyBlue
        };

        top = _summaryLabel.Bottom + 8;
        _previewListView = new ListView
        {
            Left = left,
            Top = top,
            Width = ClientSize.Width - 32,
            Height = ClientSize.Height - 178,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = false,
            BackColor = Color.Black,
            ForeColor = Color.Cyan
        };
        _previewListView.Columns.Add("種別", 56);
        _previewListView.Columns.Add("名前", 220);
        _previewListView.Columns.Add("場所", 430);
        _previewListView.Columns.Add("範囲", 90);
        _previewListView.Columns.Add("状態", 80);
        _previewListView.ShowItemToolTips = true;

        _targetSlotLabel = new Label { Left = left, Top = ClientSize.Height - 78, Width = 92, Text = "保存先スロット" };
        _targetSlotComboBox = new ComboBox
        {
            Left = _targetSlotLabel.Right + 6,
            Top = _targetSlotLabel.Top - 2,
            Width = 220,
            DropDownStyle = ComboBoxStyle.DropDownList
        };

        _saveButton = new Button
        {
            Text = "Save-as-Slot...",
            Width = 140,
            Height = 30
        };
        _applyButton = new Button
        {
            Text = "現在タブへ適用...",
            Width = 140,
            Height = 30
        };
        _closeButton = new Button
        {
            Text = "閉じる",
            Width = 100,
            Height = 30,
            DialogResult = DialogResult.OK
        };

        Controls.Add(slotALabel);
        Controls.Add(_slotAComboBox);
        Controls.Add(operationLabel);
        Controls.Add(_operationComboBox);
        Controls.Add(slotBLabel);
        Controls.Add(_slotBComboBox);
        Controls.Add(_hintLabel);
        Controls.Add(_summaryLabel);
        Controls.Add(_previewListView);
        Controls.Add(_targetSlotLabel);
        Controls.Add(_targetSlotComboBox);
        Controls.Add(_saveButton);
        Controls.Add(_applyButton);
        Controls.Add(_closeButton);

        AcceptButton = _saveButton;
        CancelButton = _closeButton;

        _slotAComboBox.SelectedIndexChanged += (_, _) => RefreshPreview();
        _slotBComboBox.SelectedIndexChanged += (_, _) => RefreshPreview();
        _operationComboBox.SelectedIndexChanged += (_, _) => RefreshPreview();
        _saveButton.Click += (_, _) => SaveResultToSlot();
        _applyButton.Click += (_, _) => ApplyResultToCurrentTab();
        SizeChanged += (_, _) => LayoutControls();

        LoadSlotChoices();
        LoadOperationChoices();
        LayoutControls();
        RefreshPreview();
    }

    private void LoadSlotChoices()
    {
        IReadOnlyList<MarkSlotDialog.MarkSlotSummaryViewItem> summaries = _slotSummaryProvider();
        List<SlotComboItem> items = summaries
            .OrderBy(static summary => summary.SlotNumber)
            .Select(summary => new SlotComboItem(
                summary.SlotNumber,
                $"Slot {summary.SlotNumber}: {summary.DisplayName} ({summary.Count}件 / {summary.SourceScopeLabel})"))
            .ToList();

        _slotAComboBox.DataSource = new List<SlotComboItem>(items);
        _slotBComboBox.DataSource = new List<SlotComboItem>(items.Select(item => new SlotComboItem(item.SlotNumber, item.DisplayText)).ToList());
        _targetSlotComboBox.DataSource = new List<SlotComboItem>(items.Select(item => new SlotComboItem(item.SlotNumber, item.DisplayText)).ToList());

        SelectSlotInCombo(_slotAComboBox, _preferredSlotNumber);
        SelectSlotInCombo(_slotBComboBox, items.FirstOrDefault(item => item.SlotNumber != _preferredSlotNumber)?.SlotNumber ?? _preferredSlotNumber);
        SelectSlotInCombo(_targetSlotComboBox, items.FirstOrDefault(item => item.SlotNumber != _preferredSlotNumber)?.SlotNumber ?? _preferredSlotNumber);
    }

    private static void SelectSlotInCombo(ComboBox comboBox, int slotNumber)
    {
        for (int i = 0; i < comboBox.Items.Count; i++)
        {
            if (comboBox.Items[i] is SlotComboItem item && item.SlotNumber == slotNumber)
            {
                comboBox.SelectedIndex = i;
                return;
            }
        }

        if (comboBox.Items.Count > 0)
        {
            comboBox.SelectedIndex = 0;
        }
    }

    private void LoadOperationChoices()
    {
        _operationComboBox.DataSource = new List<OperationComboItem>
        {
            new(MarkSlotSetOperations.Or, "OR"),
            new(MarkSlotSetOperations.And, "AND"),
            new(MarkSlotSetOperations.AMinusB, "A-B"),
            new(MarkSlotSetOperations.BMinusA, "B-A"),
            new(MarkSlotSetOperations.Xor, "XOR")
        };
        _operationComboBox.SelectedIndex = 0;
    }

    private void LayoutControls()
    {
        int contentWidth = ClientSize.Width - 32;
        _hintLabel.Width = contentWidth;
        _hintLabel.Height = FileOperationDialogLayoutHelper.MeasureLabelHeight(_hintLabel, contentWidth, 48);

        _summaryLabel.Top = _hintLabel.Bottom + 8;
        _summaryLabel.Width = contentWidth;
        _summaryLabel.Height = FileOperationDialogLayoutHelper.MeasureLabelHeight(_summaryLabel, contentWidth, 52);

        _previewListView.Top = _summaryLabel.Bottom + 8;
        _previewListView.Width = contentWidth;

        int bottomTop = ClientSize.Height - 54;
        int bottomAreaTop = bottomTop - 8;
        _previewListView.Height = Math.Max(220, bottomAreaTop - _previewListView.Top);

        _targetSlotLabel.Top = bottomTop;
        _targetSlotComboBox.Top = bottomTop - 2;
        _targetSlotComboBox.Left = 114;

        _saveButton.Top = bottomTop - 3;
        _applyButton.Top = bottomTop - 3;
        _closeButton.Top = bottomTop - 3;
        _closeButton.Left = ClientSize.Width - 16 - _closeButton.Width;
        _saveButton.Left = _closeButton.Left - 10 - _saveButton.Width;
        _applyButton.Left = _saveButton.Left - 10 - _applyButton.Width;
    }

    private void RefreshPreview()
    {
        if (_slotAComboBox.SelectedItem is not SlotComboItem slotA ||
            _slotBComboBox.SelectedItem is not SlotComboItem slotB ||
            _operationComboBox.SelectedItem is not OperationComboItem operation)
        {
            _currentPreview = null;
            _previewListView.Items.Clear();
            _summaryLabel.Text = "Slot A / Slot B / 演算を選択してください。";
            UpdateButtonState();
            return;
        }

        _currentPreview = _previewProvider(slotA.SlotNumber, slotB.SlotNumber, operation.Kind);
        _previewListView.BeginUpdate();
        _previewListView.Items.Clear();
        foreach (MarkSlotSetOperationPreviewItem item in _currentPreview.PreviewItems)
        {
            var row = new ListViewItem(GetMarkItemTypeText(item.FullPath));
            row.SubItems.Add(item.Name);
            row.SubItems.Add(GetDisplayLocation(item.FullPath));
            row.SubItems.Add(item.IsInCurrentDirectory ? "現在DIR内" : "外");
            row.SubItems.Add(item.Exists ? "存在" : "不在");
            row.ToolTipText = $"名前: {item.Name}\n種別: {GetMarkItemTypeText(item.FullPath)}\n範囲: {(item.IsInCurrentDirectory ? "現在DIR内" : "外")}\n状態: {(item.Exists ? "存在" : "不在")}\nパス: {item.FullPath}";
            if (!item.Exists)
            {
                row.ForeColor = Color.Yellow;
            }
            _previewListView.Items.Add(row);
        }
        _previewListView.EndUpdate();

        _summaryLabel.Text =
            $"Slot A: {_currentPreview.SlotADisplayName} ({_currentPreview.SlotACount}件) / " +
            $"Slot B: {_currentPreview.SlotBDisplayName} ({_currentPreview.SlotBCount}件) / " +
            $"{_currentPreview.OperationLabel} 結果: {_currentPreview.ResultCount}件 / 現在DIR内 {_currentPreview.CurrentDirectoryCount} / 外 {_currentPreview.OutsideCount} / 不在 {_currentPreview.MissingCount}";

        UpdateButtonState();
    }

    private void UpdateButtonState()
    {
        _saveButton.Enabled = _currentPreview != null &&
            _currentPreview.ResultCount > 0 &&
            _targetSlotComboBox.SelectedItem is SlotComboItem;
        _applyButton.Enabled = _currentPreview != null && _currentPreview.ResultCount > 0;
    }

    private void SaveResultToSlot()
    {
        if (_currentPreview == null || _targetSlotComboBox.SelectedItem is not SlotComboItem targetSlot)
        {
            return;
        }

        if (_currentPreview.ResultCount == 0)
        {
            MessageBox.Show(this, "0件の演算結果は保存できません。", "スロット演算", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _saveResultAction(new MarkSlotSetOperationSaveRequest(
            targetSlot.SlotNumber,
            _currentPreview.SlotANumber,
            _currentPreview.SlotBNumber,
            _currentPreview.OperationKind,
            _currentPreview.ResultPaths));
    }

    private void ApplyResultToCurrentTab()
    {
        if (_currentPreview == null || _currentPreview.ResultCount == 0)
        {
            return;
        }

        _applyToCurrentTabAction(_currentPreview);
    }

    private static string GetDisplayLocation(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return "-";
        }

        string? parentDirectory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(parentDirectory))
        {
            return parentDirectory;
        }

        string root = Path.GetPathRoot(fullPath) ?? string.Empty;
        return string.IsNullOrWhiteSpace(root) ? fullPath : root;
    }

    private static string GetMarkItemTypeText(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return "-";
        }

        if (Directory.Exists(fullPath))
        {
            return "DIR";
        }

        if (File.Exists(fullPath))
        {
            return "FILE";
        }

        return "不明";
    }
}
