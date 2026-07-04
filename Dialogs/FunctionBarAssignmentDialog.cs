using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MidFD.Commands;
using MidFD.Configuration;
using MidFD.Models;
using MidFD.Services;

namespace MidFD.Dialogs;

public sealed class FunctionBarAssignmentDialog : Form
{
    private readonly InputSettings _settingsDraft;
    private readonly CommandRegistry _registry;

    private readonly ComboBox _profileCombo;
    private readonly TabControl _layerTabs;
    private readonly DataGridView _assignmentGrid;
    private readonly TextBox _detailsBox;
    private readonly Button _resetRowButton;
    private readonly Button _resetLabelButton;
    private readonly Button _resetAllButton;

    public InputSettings ResultSettings => _settingsDraft;

    public FunctionBarAssignmentDialog(InputSettings currentSettings, CommandRegistry registry)
    {
        _settingsDraft = currentSettings.Clone();
        _registry = registry;

        Text = "Functionバー割り当て";
        Size = new Size(950, 710);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;

        var rootLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(12)
        };
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60F)); // 説明テキスト
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F)); // タブ高さ用
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 65F));  // グリッドエリア
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 35F));  // 詳細ペインエリア
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45F)); // 下部ボタン
        Controls.Add(rootLayout);

        // 1. 上部プロファイル選択パネル
        var topPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0)
        };
        rootLayout.Controls.Add(topPanel, 0, 0);

        var profileLabel = new Label
        {
            Text = "対象プロファイル:",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft
        };
        topPanel.Controls.Add(profileLabel);

        _profileCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 180,
            Height = 28
        };
        _profileCombo.Items.Add("MidFD標準");
        _profileCombo.Items.Add("FD/WinFD互換");
        _profileCombo.SelectedIndex = _settingsDraft.FunctionKeyProfile == InputSettings.FdCompatibleProfileValue ? 1 : 0;
        _profileCombo.SelectedIndexChanged += ProfileCombo_SelectedIndexChanged;
        topPanel.Controls.Add(_profileCombo);

        // 2. 説明ラベル
        var descLabel = new Label
        {
            Text = "※ 表示名はFunctionバー本体の短縮ラベルです。\r\n※ 機能名はこのslotで実行する機能、通常キーはその機能の共通ショートカットです。\r\n※ ShiftタブはShift押下中のFunctionバー表示/実行導線に使用します。",
            ForeColor = Color.DimGray,
            AutoSize = true,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 0, 0, 4)
        };
        rootLayout.Controls.Add(descLabel, 0, 1);

        // 3. TabControlによるレイヤー切り替え
        _layerTabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0)
        };
        _layerTabs.TabPages.Add("通常 F1〜F12");
        _layerTabs.TabPages.Add("Shift+F1〜F12");
        _layerTabs.SelectedIndexChanged += LayerTabs_SelectedIndexChanged;
        rootLayout.Controls.Add(_layerTabs, 0, 2);

        // 4. 中央 DataGridView
        _assignmentGrid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.Fixed3D,
            EditMode = DataGridViewEditMode.EditOnEnter,
            MultiSelect = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.CellSelect,
            Margin = new Padding(0, 4, 0, 4)
        };

        var keyCol = new DataGridViewTextBoxColumn { Name = "Key", HeaderText = "キー", ReadOnly = true, Width = 80 };
        var labelCol = new DataGridViewTextBoxColumn { Name = "Label", HeaderText = "表示名", ReadOnly = false, Width = 120 };
        var normalKeyCol = new DataGridViewTextBoxColumn
        {
            Name = "NormalKey",
            HeaderText = "通常キー",
            ReadOnly = true,
            Width = 140,
            ToolTipText = "通常キーはこの機能に対する共通ショートカットです。変更は「入力割り当て」で行います。"
        };
        if (normalKeyCol.CellTemplate is not null)
        {
            normalKeyCol.CellTemplate.Style.BackColor = SystemColors.Control;
            normalKeyCol.CellTemplate.Style.ForeColor = SystemColors.GrayText;
        }

        var cmdComboCol = new DataGridViewComboBoxColumn
        {
            Name = "Command",
            HeaderText = "機能名",
            ReadOnly = false,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 290,
            DisplayStyle = DataGridViewComboBoxDisplayStyle.ComboBox,
            DropDownWidth = 720
        };

        var descriptionCol = new DataGridViewTextBoxColumn
        {
            Name = "Description",
            HeaderText = "説明",
            ReadOnly = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 240
        };
        if (descriptionCol.CellTemplate is not null)
        {
            descriptionCol.CellTemplate.Style.BackColor = SystemColors.Control;
            descriptionCol.CellTemplate.Style.ForeColor = SystemColors.GrayText;
        }

        _assignmentGrid.Columns.Add(keyCol);
        _assignmentGrid.Columns.Add(labelCol);
        _assignmentGrid.Columns.Add(cmdComboCol);
        _assignmentGrid.Columns.Add(normalKeyCol);
        _assignmentGrid.Columns.Add(descriptionCol);

        if (_assignmentGrid.Columns["Key"] is DataGridViewColumn keyColumn)
        {
            keyColumn.DisplayIndex = 0;
        }

        if (_assignmentGrid.Columns["Label"] is DataGridViewColumn labelColumnRef)
        {
            labelColumnRef.DisplayIndex = 1;
        }

        if (_assignmentGrid.Columns["Command"] is DataGridViewColumn commandColumn)
        {
            commandColumn.DisplayIndex = 2;
        }

        if (_assignmentGrid.Columns["NormalKey"] is DataGridViewColumn normalKeyColumnRef)
        {
            normalKeyColumnRef.DisplayIndex = 3;
        }

        if (_assignmentGrid.Columns["Description"] is DataGridViewColumn descriptionColumn)
        {
            descriptionColumn.DisplayIndex = 4;
        }

        _assignmentGrid.CellValueChanged += AssignmentGrid_CellValueChanged;
        _assignmentGrid.CellContextMenuStripNeeded += AssignmentGrid_CellContextMenuStripNeeded;
        _assignmentGrid.CurrentCellDirtyStateChanged += AssignmentGrid_CurrentCellDirtyStateChanged;
        _assignmentGrid.DataError += (s, e) => { e.ThrowException = false; };
        _assignmentGrid.SelectionChanged += (s, e) => UpdateDetailsPane();

        rootLayout.Controls.Add(_assignmentGrid, 0, 3);

        // 5. 詳細ペイン
        var detailsGroup = new GroupBox
        {
            Text = "選択中スロットの詳細情報",
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 4, 0, 4),
            Padding = new Padding(8)
        };
        _detailsBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = SystemColors.Control,
            Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font(Font.FontFamily, 9.5F)
        };
        detailsGroup.Controls.Add(_detailsBox);
        rootLayout.Controls.Add(detailsGroup, 0, 4);

        // 6. 下部ボタン操作領域
        var bottomPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0)
        };
        rootLayout.Controls.Add(bottomPanel, 0, 5);

        _resetRowButton = new Button
        {
            Text = "選択行の機能名を既定に戻す",
            Width = 190,
            Height = 32,
            Location = new Point(0, 8)
        };
        _resetRowButton.Click += ResetRowButton_Click;
        bottomPanel.Controls.Add(_resetRowButton);

        _resetLabelButton = new Button
        {
            Text = "選択行の表示名を既定に戻す",
            Width = 190,
            Height = 32,
            Location = new Point(198, 8)
        };
        _resetLabelButton.Click += ResetLabelButton_Click;
        bottomPanel.Controls.Add(_resetLabelButton);

        _resetAllButton = new Button
        {
            Text = "全スロットを既定に戻す",
            Width = 150,
            Height = 32,
            Location = new Point(396, 8)
        };
        _resetAllButton.Click += ResetAllButton_Click;
        bottomPanel.Controls.Add(_resetAllButton);

        var okButton = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Width = 90,
            Height = 32,
            Location = new Point(Size.Width - 215, 8),
            Anchor = AnchorStyles.Right
        };
        bottomPanel.Controls.Add(okButton);

        var cancelButton = new Button
        {
            Text = "キャンセル",
            DialogResult = DialogResult.Cancel,
            Width = 90,
            Height = 32,
            Location = new Point(Size.Width - 115, 8),
            Anchor = AnchorStyles.Right
        };
        bottomPanel.Controls.Add(cancelButton);

        AcceptButton = okButton;
        CancelButton = cancelButton;

        // 初期ロード
        RefreshListView();
    }

    private bool IsShiftLayerSelected()
    {
        return _layerTabs.SelectedIndex == 1;
    }

    private void ProfileCombo_SelectedIndexChanged(object? sender, EventArgs e)
    {
        _settingsDraft.FunctionKeyProfile = _profileCombo.SelectedIndex == 1
            ? InputSettings.FdCompatibleProfileValue
            : InputSettings.StandardProfileValue;

        RefreshListView();
    }

    private void LayerTabs_SelectedIndexChanged(object? sender, EventArgs e)
    {
        RefreshListView();
    }

    private bool _isRefreshing = false;

    private void RefreshListView()
    {
        if (_isRefreshing) return;
        _isRefreshing = true;

        try
        {
            var currentCell = _assignmentGrid.CurrentCell;
            int savedRow = currentCell?.RowIndex ?? -1;
            int savedCol = currentCell?.ColumnIndex ?? -1;

            if (_assignmentGrid.Rows.Count != 12)
            {
                _assignmentGrid.Rows.Clear();
                for (int i = 0; i < 12; i++)
                {
                    _assignmentGrid.Rows.Add();
                }
            }

            bool isFdCompatible = _profileCombo.SelectedIndex == 1;
            bool isShift = IsShiftLayerSelected();

            Dictionary<string, string?>? overrides = isShift
                ? (isFdCompatible ? _settingsDraft.FunctionBarCommandOverridesShiftFdCompatible : _settingsDraft.FunctionBarCommandOverridesShiftStandard)
                : (isFdCompatible ? _settingsDraft.FunctionBarCommandOverridesFdCompatible : _settingsDraft.FunctionBarCommandOverridesStandard);

            // コマンド候補 DataSource の構築
            var assignableCommands = _registry.GetMouseGestureAssignableCommands()
                .OrderBy(d => d.DisplayName)
                .ToList();

            var comboItems = new List<ComboBoxItem>
            {
                new ComboBoxItem { Value = null, Display = "(既定の割り当て)" },
                new ComboBoxItem { Value = "none", Display = "(未割り当て)" }
            };

            foreach (var cmd in assignableCommands)
            {
                comboItems.Add(new ComboBoxItem { Value = cmd.Id, Display = FunctionKeyProfileService.ResolveCommandDisplayText(cmd) });
            }

            // 現在設定されているコマンドで Registry にないもの（不明なコマンド）のハンドリング
            for (int slot = 1; slot <= 12; slot++)
            {
                string slotKey = $"F{slot}";
                if (overrides != null && overrides.TryGetValue(slotKey, out string? assignedCmdId) && !string.IsNullOrEmpty(assignedCmdId))
                {
                    if (!string.Equals(assignedCmdId, "none", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!comboItems.Any(item => string.Equals(item.Value, assignedCmdId, StringComparison.OrdinalIgnoreCase)))
                        {
                            comboItems.Add(new ComboBoxItem { Value = assignedCmdId, Display = $"不明な機能 ({assignedCmdId})" });
                        }
                    }
                }
            }

            if (_assignmentGrid.Columns["Command"] is DataGridViewComboBoxColumn cmdComboCol)
            {
                cmdComboCol.DataSource = comboItems;
                cmdComboCol.ValueMember = "Value";
                cmdComboCol.DisplayMember = "Display";
            }

            for (int slot = 1; slot <= 12; slot++)
            {
                string slotKey = $"F{slot}";
                string displaySlotKey = isShift ? $"Shift+F{slot}" : $"F{slot}";
                string? assignedCmdId = null;

                if (overrides != null && overrides.TryGetValue(slotKey, out string? value))
                {
                    assignedCmdId = value;
                }

                // 既定のアクション情報を取得
                var defaultAction = FunctionKeyProfileService.ResolveAction(
                    isFdCompatible ? InputSettings.FdCompatibleProfileValue : InputSettings.StandardProfileValue,
                    slot);

                string? defaultCmdId = FunctionKeyProfileService.ResolveFunctionBarCommandId(
                    isFdCompatible ? FunctionKeyProfile.FDCompatible : FunctionKeyProfile.Standard,
                    slot,
                    null,
                    null,
                    null,
                    null,
                    isShift);

                string defaultActionLabel = !string.IsNullOrEmpty(defaultCmdId)
                    ? (isFdCompatible
                        ? FunctionKeyProfileService.ResolveFdCompatibleFunctionBarShortLabel(slot, isShift, false, false)
                        : FunctionKeyProfileService.ResolveFunctionBarShortLabel(defaultCmdId))
                    : (isFdCompatible ? "(未割り当て)" : ResolveStandardDefaultActionLabel(slot, defaultCmdId));

                string displayName;
                string normalKeyText = string.Empty;
                string functionName = "(未割り当て)";
                string descriptionText = "このスロットには機能が割り当てられていません。";
                string defaultDisplayName = GetDefaultDisplayNameForSlot(slot, isFdCompatible, isShift, defaultCmdId);
                string baseDisplayName;

                if (string.IsNullOrEmpty(assignedCmdId))
                {
                    displayName = defaultDisplayName;
                    normalKeyText = FunctionKeyProfileService.ResolveFunctionBarKeyHint(
                        defaultCmdId,
                        _settingsDraft.BrowserKeyCommandOverrides,
                        isFdCompatible ? InputSettings.FdCompatibleProfileValue : InputSettings.StandardProfileValue);
                    if (string.IsNullOrEmpty(normalKeyText))
                    {
                        normalKeyText = "(なし)";
                    }
                }
                else if (string.Equals(assignedCmdId, "none", StringComparison.OrdinalIgnoreCase))
                {
                    displayName = "(未割り当て)";
                    normalKeyText = "(なし)";
                }
                else
                {
                    normalKeyText = FunctionKeyProfileService.ResolveFunctionBarKeyHint(
                        assignedCmdId,
                        _settingsDraft.BrowserKeyCommandOverrides,
                        isFdCompatible ? InputSettings.FdCompatibleProfileValue : InputSettings.StandardProfileValue);
                    var def = _registry.Find(assignedCmdId);
                    if (def != null)
                    {
                        displayName = defaultDisplayName;
                        functionName = def.DisplayName;
                        descriptionText = def.Description;
                    }
                    else
                    {
                        displayName = "不明";
                        functionName = "不明な機能";
                        descriptionText = $"未登録のコマンドID: {assignedCmdId}";
                    }
                }

                baseDisplayName = displayName;

                // Custom ShortLabel Override の適用
                string? activeCmdId = assignedCmdId;
                if (string.IsNullOrEmpty(activeCmdId))
                {
                    activeCmdId = defaultCmdId;
                }

                if (!string.IsNullOrEmpty(activeCmdId) && !string.Equals(activeCmdId, "none", StringComparison.OrdinalIgnoreCase))
                {
                    var labelOverrides = isShift
                        ? (isFdCompatible ? _settingsDraft.FunctionBarLabelOverridesShiftFdCompatible : _settingsDraft.FunctionBarLabelOverridesShiftStandard)
                        : (isFdCompatible ? _settingsDraft.FunctionBarLabelOverridesFdCompatible : _settingsDraft.FunctionBarLabelOverridesStandard);

                    if (labelOverrides != null && labelOverrides.TryGetValue(slotKey, out var labelOverride) && labelOverride != null)
                    {
                        if (string.Equals(labelOverride.CommandId, activeCmdId, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(labelOverride.Label))
                        {
                            string normalizedLabel = InputSettings.NormalizeFunctionBarLabelText(labelOverride.Label);
                            string normalizedBaseDisplayName = InputSettings.NormalizeFunctionBarLabelText(baseDisplayName);
                            if (string.Equals(normalizedLabel, normalizedBaseDisplayName, StringComparison.OrdinalIgnoreCase))
                            {
                                RemoveLabelOverride(slot);
                            }
                            else
                            {
                                displayName = normalizedLabel;
                            }
                        }
                    }

                    if (string.IsNullOrWhiteSpace(functionName) || functionName == "(未割り当て)")
                    {
                        var activeDef = _registry.Find(activeCmdId);
                        if (activeDef != null)
                        {
                            functionName = activeDef.DisplayName;
                            descriptionText = activeDef.Description;
                        }
                        else
                        {
                            functionName = "不明な機能";
                            descriptionText = $"未登録のコマンドID: {activeCmdId}";
                        }
                    }
                }

                var row = _assignmentGrid.Rows[slot - 1];
                row.Cells["Key"].Value = displaySlotKey;
                row.Cells["Label"].Value = displayName;
                row.Cells["Command"].Value = assignedCmdId;
                row.Cells["NormalKey"].Value = normalKeyText;
                row.Cells["Description"].Value = descriptionText;

                var labelCell = row.Cells["Label"];
                labelCell.Style.ForeColor = string.Equals(displayName, baseDisplayName, StringComparison.OrdinalIgnoreCase)
                    ? SystemColors.WindowText
                    : SystemColors.HotTrack;
            }
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private string? GetAssignedCommandIdForSlot(int slot)
    {
        bool isFdCompatible = _profileCombo.SelectedIndex == 1;
        bool isShift = IsShiftLayerSelected();
        string slotKey = $"F{slot}";

        Dictionary<string, string?>? overrides = isShift
            ? (isFdCompatible ? _settingsDraft.FunctionBarCommandOverridesShiftFdCompatible : _settingsDraft.FunctionBarCommandOverridesShiftStandard)
            : (isFdCompatible ? _settingsDraft.FunctionBarCommandOverridesFdCompatible : _settingsDraft.FunctionBarCommandOverridesStandard);

        if (overrides != null && overrides.TryGetValue(slotKey, out string? val))
        {
            return val;
        }
        return null;
    }

    private string? GetDefaultCommandIdForSlot(int slot)
    {
        bool isFdCompatible = _profileCombo.SelectedIndex == 1;
        bool isShift = IsShiftLayerSelected();

        return FunctionKeyProfileService.ResolveFunctionBarCommandId(
            isFdCompatible ? FunctionKeyProfile.FDCompatible : FunctionKeyProfile.Standard,
            slot,
            null,
            null,
            null,
            null,
            isShift);
    }

    private void SetCommandOverride(int slot, string? commandId)
    {
        bool isFdCompatible = _profileCombo.SelectedIndex == 1;
        bool isShift = IsShiftLayerSelected();
        string slotKey = $"F{slot}";

        var overrides = isShift
            ? (isFdCompatible ? _settingsDraft.FunctionBarCommandOverridesShiftFdCompatible : _settingsDraft.FunctionBarCommandOverridesShiftStandard)
            : (isFdCompatible ? _settingsDraft.FunctionBarCommandOverridesFdCompatible : _settingsDraft.FunctionBarCommandOverridesStandard);

        if (overrides == null)
        {
            overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (isShift)
            {
                if (isFdCompatible) _settingsDraft.FunctionBarCommandOverridesShiftFdCompatible = overrides;
                else _settingsDraft.FunctionBarCommandOverridesShiftStandard = overrides;
            }
            else
            {
                if (isFdCompatible) _settingsDraft.FunctionBarCommandOverridesFdCompatible = overrides;
                else _settingsDraft.FunctionBarCommandOverridesStandard = overrides;
            }
        }

        if (commandId == null)
        {
            overrides.Remove(slotKey);
        }
        else
        {
            overrides[slotKey] = commandId;
        }
    }

    private void SetLabelOverride(int slot, string commandId, string label)
    {
        bool isFdCompatible = _profileCombo.SelectedIndex == 1;
        bool isShift = IsShiftLayerSelected();
        string slotKey = $"F{slot}";
        var labelOverrides = isShift
            ? (isFdCompatible ? _settingsDraft.FunctionBarLabelOverridesShiftFdCompatible : _settingsDraft.FunctionBarLabelOverridesShiftStandard)
            : (isFdCompatible ? _settingsDraft.FunctionBarLabelOverridesFdCompatible : _settingsDraft.FunctionBarLabelOverridesStandard);

        if (labelOverrides == null)
        {
            labelOverrides = new Dictionary<string, FunctionBarLabelOverride>(StringComparer.OrdinalIgnoreCase);
            if (isShift)
            {
                if (isFdCompatible) _settingsDraft.FunctionBarLabelOverridesShiftFdCompatible = labelOverrides;
                else _settingsDraft.FunctionBarLabelOverridesShiftStandard = labelOverrides;
            }
            else
            {
                if (isFdCompatible) _settingsDraft.FunctionBarLabelOverridesFdCompatible = labelOverrides;
                else _settingsDraft.FunctionBarLabelOverridesStandard = labelOverrides;
            }
        }

        labelOverrides[slotKey] = new FunctionBarLabelOverride
        {
            CommandId = commandId,
            Label = InputSettings.NormalizeFunctionBarLabelText(label)
        };
    }

    private void RemoveLabelOverride(int slot)
    {
        bool isFdCompatible = _profileCombo.SelectedIndex == 1;
        bool isShift = IsShiftLayerSelected();
        string slotKey = $"F{slot}";

        var labelOverrides = isShift
            ? (isFdCompatible ? _settingsDraft.FunctionBarLabelOverridesShiftFdCompatible : _settingsDraft.FunctionBarLabelOverridesShiftStandard)
            : (isFdCompatible ? _settingsDraft.FunctionBarLabelOverridesFdCompatible : _settingsDraft.FunctionBarLabelOverridesStandard);

        if (labelOverrides != null)
        {
            labelOverrides.Remove(slotKey);
        }
    }

    private void AssignmentGrid_CellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_isRefreshing) return;
        if (e.RowIndex < 0 || e.ColumnIndex < 0) return;

        int slot = e.RowIndex + 1;

        if (e.ColumnIndex == 1) // 表示名列
        {
            var cell = _assignmentGrid.Rows[e.RowIndex].Cells[e.ColumnIndex];
            string input = (cell.Value?.ToString() ?? string.Empty).Trim();

            string? assignedCmdId = GetAssignedCommandIdForSlot(slot);
            string? defaultCmdId = GetDefaultCommandIdForSlot(slot);
            string? activeCmdId = string.IsNullOrEmpty(assignedCmdId) ? defaultCmdId : assignedCmdId;

            if (string.IsNullOrEmpty(activeCmdId) || string.Equals(activeCmdId, "none", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(this, "未割り当てのキーには表示名を設定できません。", "注意", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                RefreshListView();
                return;
            }

            if (string.IsNullOrEmpty(input))
            {
                // 空文字の場合は「既定に戻す」として扱う
                RemoveLabelOverride(slot);
                RefreshListView();
                return;
            }

            if (!ValidateCustomLabel(input, out string errorMsg))
            {
                MessageBox.Show(this, errorMsg, "入力エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                RefreshListView();
                return;
            }

            string baseDisplayName = ResolveBaseDisplayNameForSlot(slot, assignedCmdId, defaultCmdId);
            if (string.Equals(InputSettings.NormalizeFunctionBarLabelText(input), InputSettings.NormalizeFunctionBarLabelText(baseDisplayName), StringComparison.OrdinalIgnoreCase))
            {
                RemoveLabelOverride(slot);
            }
            else
            {
                SetLabelOverride(slot, activeCmdId, input);
            }
            RefreshListView();
        }
        else if (e.ColumnIndex == 2) // 機能名列
        {
            var cell = _assignmentGrid.Rows[e.RowIndex].Cells[e.ColumnIndex];
            string? selectedCmdId = cell.Value as string; // null or "none" or commandId

            SetCommandOverride(slot, selectedCmdId);
            RefreshListView();
        }
    }

    private void AssignmentGrid_CurrentCellDirtyStateChanged(object? sender, EventArgs e)
    {
        if (_assignmentGrid.IsCurrentCellDirty)
        {
            _assignmentGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        }
    }

    private void AssignmentGrid_CellContextMenuStripNeeded(object? sender, DataGridViewCellContextMenuStripNeededEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0) return;

        int slot = e.RowIndex + 1;
        var menu = new ContextMenuStrip();

        var resetLabelItem = new ToolStripMenuItem("表示名を既定に戻す");
        resetLabelItem.Click += (s, ev) =>
        {
            RemoveLabelOverride(slot);
            RefreshListView();
        };
        menu.Items.Add(resetLabelItem);

        var resetCmdItem = new ToolStripMenuItem("機能名を既定に戻す");
        resetCmdItem.Click += (s, ev) =>
        {
            SetCommandOverride(slot, null);
            RefreshListView();
        };
        menu.Items.Add(resetCmdItem);

        menu.Items.Add(new ToolStripSeparator());

        var resetBothItem = new ToolStripMenuItem("表示名と機能名の両方を既定に戻す");
        resetBothItem.Click += (s, ev) =>
        {
            RemoveLabelOverride(slot);
            SetCommandOverride(slot, null);
            RefreshListView();
        };
        menu.Items.Add(resetBothItem);

        e.ContextMenuStrip = menu;
    }

    private bool ValidateCustomLabel(string input, out string errorMsg)
    {
        errorMsg = string.Empty;

        if (input.Contains(":") || input.Contains("："))
        {
            errorMsg = "\":\" (コロン) は含められません。";
            return false;
        }
        if (input.Contains("\n") || input.Contains("\r") || input.Contains("\t"))
        {
            errorMsg = "改行やタブは含められません。";
            return false;
        }

        int length = GetHalfWidthLength(input);
        if (length > 6)
        {
            errorMsg = "表示名は半角換算で最大6文字までです（全角は3文字まで）。";
            return false;
        }

        // F1: などのキー指定プレフィックスの簡易チェック
        if (System.Text.RegularExpressions.Regex.IsMatch(input, @"^(F\d+|Shift\+F\d+)\s*:?", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            errorMsg = "キー表記 (F1: 等) の混入は禁止されています。";
            return false;
        }

        return true;
    }

    private int GetHalfWidthLength(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        int len = 0;
        foreach (char c in s)
        {
            if (c > 0x7F)
            {
                len += 2;
            }
            else
            {
                len += 1;
            }
        }
        return len;
    }

    private void ResetRowButton_Click(object? sender, EventArgs e)
    {
        if (_assignmentGrid.CurrentCell == null) return;
        int slot = _assignmentGrid.CurrentCell.RowIndex + 1;

        SetCommandOverride(slot, null);
        RefreshListView();
    }

    private void ResetLabelButton_Click(object? sender, EventArgs e)
    {
        if (_assignmentGrid.CurrentCell == null) return;
        int slot = _assignmentGrid.CurrentCell.RowIndex + 1;

        RemoveLabelOverride(slot);
        RefreshListView();
    }

    private void ResetAllButton_Click(object? sender, EventArgs e)
    {
        bool isFdCompatible = _profileCombo.SelectedIndex == 1;
        bool isShift = IsShiftLayerSelected();

        var overrides = isShift
            ? (isFdCompatible ? _settingsDraft.FunctionBarCommandOverridesShiftFdCompatible : _settingsDraft.FunctionBarCommandOverridesShiftStandard)
            : (isFdCompatible ? _settingsDraft.FunctionBarCommandOverridesFdCompatible : _settingsDraft.FunctionBarCommandOverridesStandard);

        var labelOverrides = isShift
            ? (isFdCompatible ? _settingsDraft.FunctionBarLabelOverridesShiftFdCompatible : _settingsDraft.FunctionBarLabelOverridesShiftStandard)
            : (isFdCompatible ? _settingsDraft.FunctionBarLabelOverridesFdCompatible : _settingsDraft.FunctionBarLabelOverridesStandard);

        if (overrides != null)
        {
            for (int slot = 1; slot <= 12; slot++)
            {
                overrides[$"F{slot}"] = null;
            }
        }

        if (labelOverrides != null)
        {
            for (int slot = 1; slot <= 12; slot++)
            {
                labelOverrides.Remove($"F{slot}");
            }
        }

        RefreshListView();
    }

    private string GetActionShortLabel(FunctionKeyAction action)
    {
        return action switch
        {
            FunctionKeyAction.Help => "help",
            FunctionKeyAction.Execute => "exec",
            FunctionKeyAction.Copy => "copy",
            FunctionKeyAction.Edit => "edit",
            FunctionKeyAction.Rename => "ren",
            FunctionKeyAction.Sort => "sort",
            FunctionKeyAction.Filter => "find",
            FunctionKeyAction.Tree => "tree",
            FunctionKeyAction.Logdisk => "logd",
            FunctionKeyAction.Unpack => "unpk",
            FunctionKeyAction.Top => "top",
            FunctionKeyAction.Bottom => "btm",
            FunctionKeyAction.Menu => "menu",
            _ => "none"
        };
    }

    private string GetDefaultDisplayNameForSlot(int slot, bool isFdCompatible, bool isShift, string? defaultCmdId)
    {
        return FunctionKeyProfileService.ResolveFunctionBarDefaultDisplayLabel(
            isFdCompatible ? FunctionKeyProfile.FDCompatible : FunctionKeyProfile.Standard,
            slot,
            isShift);
    }

    private string ResolveBaseDisplayNameForSlot(int slot, string? assignedCmdId, string? defaultCmdId)
    {
        bool isFdCompatible = _profileCombo.SelectedIndex == 1;
        bool isShift = IsShiftLayerSelected();

        if (string.IsNullOrEmpty(assignedCmdId))
        {
            return GetDefaultDisplayNameForSlot(slot, isFdCompatible, isShift, defaultCmdId);
        }

        if (string.Equals(assignedCmdId, "none", StringComparison.OrdinalIgnoreCase))
        {
            return "(未割り当て)";
        }

        var def = _registry.Find(assignedCmdId);
        if (def != null)
        {
            return GetDefaultDisplayNameForSlot(slot, isFdCompatible, isShift, defaultCmdId);
        }

        return "不明";
    }

    private string GetActionJapaneseDescription(FunctionKeyAction action)
    {
        return action switch
        {
            FunctionKeyAction.Help => "ヘルプを表示します。",
            FunctionKeyAction.Execute => "選択中の項目を実行します。",
            FunctionKeyAction.Copy => "選択中の項目をコピーします。",
            FunctionKeyAction.Edit => "選択中のファイルを編集します。",
            FunctionKeyAction.Rename => "選択中の項目をリネームします。",
            FunctionKeyAction.Sort => "表示順序のソートを行います。",
            FunctionKeyAction.Filter => "表示項目のフィルタを設定します。",
            FunctionKeyAction.Tree => "フォルダツリーを表示します。",
            FunctionKeyAction.Logdisk => "Logdiskを実行します。",
            FunctionKeyAction.Unpack => "選択アーカイブを解凍します。",
            FunctionKeyAction.Top => "一覧の先頭にスクロール移動します。",
            FunctionKeyAction.Bottom => "一覧の末尾にスクロール移動します。",
            FunctionKeyAction.Menu => "メニューを表示します。",
            _ => "何もしません。"
        };
    }

    private string GetStandardActionLabel(int slot)
    {
        return slot switch
        {
            1 => "Help",
            2 => "Ren",
            3 => "Copy",
            4 => "Edit",
            5 => "Rld",
            6 => "Sort",
            7 => "Filt",
            8 => "QAcc",
            9 => "Logd",
            10 => "Cmd",
            11 => "Mark",
            12 => "Cmds",
            _ => "none"
        };
    }

    private string GetStandardActionJapaneseDescription(int slot)
    {
        return slot switch
        {
            1 => "ヘルプを表示します。",
            2 => "ファイルやフォルダの名前を変更します。",
            3 => "選択中の項目をコピーします。",
            4 => "ファイルを編集します。",
            5 => "表示内容を最新の情報に更新します。",
            6 => "項目の並び替えダイアログを表示します。",
            7 => "表示フィルタの適用を設定します。",
            8 => "クイックアクセスフォルダ一覧を開きます。",
            9 => "Logdiskを実行します。",
            10 => "コマンドランチャーを開きます。",
            11 => "マークスロットを開きます。",
            12 => "コマンド一覧を開きます。",
            _ => "割り当てはありません。"
        };
    }

    private string ResolveStandardDefaultActionLabel(int slot, string? defaultCmdId)
    {
        if (!string.IsNullOrEmpty(defaultCmdId))
        {
            return FunctionKeyProfileService.ResolveFunctionBarShortLabel(defaultCmdId);
        }

        return GetStandardActionLabel(slot);
    }

    private string ResolveStandardDefaultActionDescription(int slot, string? defaultCmdId)
    {
        if (!string.IsNullOrEmpty(defaultCmdId))
        {
            var def = _registry.Find(defaultCmdId);
            if (def != null)
            {
                return def.Description;
            }
        }

        return GetStandardActionJapaneseDescription(slot);
    }

    public class ComboBoxItem
    {
        public string? Value { get; set; }
        public string Display { get; set; } = string.Empty;
    }

    private void UpdateDetailsPane()
    {
        if (_assignmentGrid == null || _detailsBox == null || _assignmentGrid.CurrentRow == null)
        {
            if (_detailsBox != null) _detailsBox.Text = string.Empty;
            return;
        }

        int slot = _assignmentGrid.CurrentRow.Index + 1;
        bool isFdCompatible = _profileCombo.SelectedIndex == 1;
        bool isShift = IsShiftLayerSelected();
        string slotKey = $"F{slot}";
        string displaySlotKey = isShift ? $"Shift+F{slot}" : $"F{slot}";

        string? assignedCmdId = GetAssignedCommandIdForSlot(slot);
        string? defaultCmdId = GetDefaultCommandIdForSlot(slot);
        string? activeCmdId = string.IsNullOrEmpty(assignedCmdId) ? defaultCmdId : assignedCmdId;

        // 既定のアクション情報を取得
        var defaultAction = FunctionKeyProfileService.ResolveAction(
            isFdCompatible ? InputSettings.FdCompatibleProfileValue : InputSettings.StandardProfileValue,
            slot);

        string defaultActionLabel = GetDefaultDisplayNameForSlot(slot, isFdCompatible, isShift, defaultCmdId);

        string? labelOverrideVal = null;
        if (!string.IsNullOrEmpty(activeCmdId) && !string.Equals(activeCmdId, "none", StringComparison.OrdinalIgnoreCase))
        {
            var labelOverrides = isShift
                ? (isFdCompatible ? _settingsDraft.FunctionBarLabelOverridesShiftFdCompatible : _settingsDraft.FunctionBarLabelOverridesShiftStandard)
                : (isFdCompatible ? _settingsDraft.FunctionBarLabelOverridesFdCompatible : _settingsDraft.FunctionBarLabelOverridesStandard);
            if (labelOverrides != null && labelOverrides.TryGetValue(slotKey, out var labelOverride) && labelOverride != null)
            {
                if (string.Equals(labelOverride.CommandId, activeCmdId, StringComparison.OrdinalIgnoreCase))
                {
                    labelOverrideVal = InputSettings.NormalizeFunctionBarLabelText(labelOverride.Label);
                }
            }
        }

        string cmdDisplayName = "なし";
        string cmdDesc = "何も実行しません。";
        if (string.IsNullOrEmpty(assignedCmdId))
        {
            cmdDisplayName = string.IsNullOrEmpty(defaultCmdId)
                ? (isFdCompatible ? "(未割り当て)" : ResolveStandardDefaultActionLabel(slot, defaultCmdId))
                : defaultActionLabel;
            var defaultCmdDef = !string.IsNullOrEmpty(defaultCmdId) ? _registry.Find(defaultCmdId) : null;
            if (defaultCmdDef != null)
            {
                cmdDisplayName = defaultCmdDef.DisplayName;
            }
            cmdDesc = defaultCmdDef != null
                ? defaultCmdDef.Description
                : (isFdCompatible ? GetActionJapaneseDescription(defaultAction) : ResolveStandardDefaultActionDescription(slot, defaultCmdId));
        }
        else if (string.Equals(assignedCmdId, "none", StringComparison.OrdinalIgnoreCase))
        {
            cmdDisplayName = "(未割り当て)";
            cmdDesc = "何も実行しません。";
        }
        else
        {
            var def = _registry.Find(assignedCmdId);
            if (def != null)
            {
                cmdDisplayName = def.DisplayName;
                cmdDesc = def.Description;
                if (def.IsDangerous)
                {
                    cmdDesc += " (※実行前に確認ダイアログが表示されます)";
                }
            }
            else
            {
                cmdDisplayName = "不明";
                cmdDesc = "設定されたコマンドは存在しません。";
            }
        }

        string defaultCmdName = "なし";
        if (!string.IsNullOrEmpty(defaultCmdId))
        {
            var def = _registry.Find(defaultCmdId);
            if (def != null) defaultCmdName = def.DisplayName;
        }
        else
        {
            defaultCmdName = isFdCompatible ? "(未割り当て)" : GetStandardActionJapaneseDescription(slot);
        }

        string normalKeyText = string.Empty;
        if (!string.IsNullOrEmpty(activeCmdId) && !string.Equals(activeCmdId, "none", StringComparison.OrdinalIgnoreCase))
        {
            normalKeyText = FunctionKeyProfileService.ResolveFunctionBarKeyHint(
                activeCmdId,
                _settingsDraft.BrowserKeyCommandOverrides,
                isFdCompatible ? InputSettings.FdCompatibleProfileValue : InputSettings.StandardProfileValue);
        }
        if (string.IsNullOrEmpty(normalKeyText))
        {
            normalKeyText = "未設定";
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"【対象キー】 {displaySlotKey} ({(isFdCompatible ? "FD/WinFD互換" : "MidFD標準")} / {(isShift ? "Shiftレイヤー" : "通常レイヤー")})");
        sb.AppendLine($"【表示名】 {(labelOverrideVal ?? displayNameOrFallback(activeCmdId, defaultActionLabel, slot, isShift, isFdCompatible))}   [既定: {defaultActionLabel}]");
        sb.AppendLine($"【機能名】 {cmdDisplayName}   [既定: {defaultCmdName}]");
        sb.AppendLine($"【通常キー】 {normalKeyText}");
        if (!string.IsNullOrEmpty(activeCmdId) && !string.Equals(activeCmdId, "none", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine($"【コマンドID】 {activeCmdId}");
        }
        sb.AppendLine($"【説明】 {cmdDesc}");
        sb.AppendLine();
        sb.AppendLine($"※ 表示名はFunctionバー本体に表示する短縮ラベルです。");
        sb.AppendLine($"※ 通常キーはこの機能に対する共通ショートカットです。変更は「入力割り当て」画面で行います。");

        _detailsBox.Text = sb.ToString();
    }

    private string displayNameOrFallback(string? activeCmdId, string fallback, int slot, bool isShift, bool isFdCompatible)
    {
        if (string.IsNullOrEmpty(activeCmdId) || string.Equals(activeCmdId, "none", StringComparison.OrdinalIgnoreCase))
        {
            return fallback;
        }
        return fallback;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_assignmentGrid != null && _assignmentGrid.IsCurrentCellInEditMode)
        {
            var key = keyData & Keys.KeyCode;
            if (key == Keys.Enter || key == Keys.Escape)
            {
                return false;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
