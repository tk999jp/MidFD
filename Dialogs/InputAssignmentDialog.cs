using MidFD.Commands;
using System.Globalization;
using MidFD.Configuration;
using MidFD.Helpers;
using MidFD.Models;
using MidFD.Services;

namespace MidFD.Dialogs;

public sealed class InputAssignmentDialog : Form
{
    private const int FunctionBarLabelMaxDisplayCells = 6;
    private const int FunctionBarLabelTextBoxMaxLength = 12;
    private const string ReservedFunctionSlotCommandId = "__reserved_altf4__";
    private enum FunctionLayer
    {
        Normal,
        Shift,
        Ctrl,
        Alt
    }

    private sealed record ProfileOption(string DisplayName, string Value)
    {
        public override string ToString() => DisplayName;
    }

    private sealed record CommandOption(string DisplayText, string CommandId)
    {
        public override string ToString() => DisplayText;
    }

    private readonly InputSettings _settingsDraft;
    private readonly CommandRegistry _registry;
    private readonly ComboBox _profileCombo;
    private readonly TabControl _tabs;
    private readonly DataGridView _featureGrid;
    private readonly Label _featureDescriptionValueLabel;
    private readonly TabControl _functionLayerTabs;
    private readonly DataGridView _functionGrid;
    private readonly DataGridView _gestureGrid;
    private readonly Dictionary<string, CommandDefinition> _commandById;
    private bool _refreshing;
    private bool _openingFunctionDropdown;
    private bool _openingGestureDropdown;

    public InputSettings ResultSettings => _settingsDraft.Clone();

    public string SelectedProfileValue
    {
        get => ResolveProfileValue();
        set
        {
            int index = string.Equals(value, InputSettings.FdCompatibleProfileValue, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            if (_profileCombo.SelectedIndex != index)
            {
                _profileCombo.SelectedIndex = index;
            }
        }
    }

    public InputAssignmentDialog(InputSettings currentSettings, CommandRegistry registry)
    {
        _settingsDraft = currentSettings.Clone();
        InputSettings.NormalizeAndMigrateFunctionKeyChords(_settingsDraft);
        _registry = registry;
        _commandById = _registry.GetAll().ToDictionary(static x => x.Id, StringComparer.OrdinalIgnoreCase);

        Text = "入力割り当て";
        TopLevel = false;
        FormBorderStyle = FormBorderStyle.None;
        Dock = DockStyle.Fill;

        var profilePanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 36,
            Padding = new Padding(8, 6, 8, 4),
            FlowDirection = FlowDirection.LeftToRight
        };
        profilePanel.Controls.Add(new Label
        {
            Text = "対象操作プロファイル:",
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 6, 8, 0)
        });
        _profileCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 180
        };
        _profileCombo.Items.Add(new ProfileOption("MidFD標準", InputSettings.StandardProfileValue));
        _profileCombo.Items.Add(new ProfileOption("FD/WinFD互換", InputSettings.FdCompatibleProfileValue));
        _profileCombo.SelectedIndex = string.Equals(_settingsDraft.FunctionKeyProfile, InputSettings.FdCompatibleProfileValue, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        _profileCombo.SelectedIndexChanged += (_, _) =>
        {
            _settingsDraft.FunctionKeyProfile = ResolveProfileValue();
            RefreshAllViews();
        };
        profilePanel.Controls.Add(_profileCombo);

        _tabs = new TabControl { Dock = DockStyle.Fill };
        var featureTab = new TabPage("機能別");
        var functionTab = new TabPage("ファンクションキー/バー");
        var gestureTab = new TabPage("マウスジェスチャー");
        _tabs.TabPages.AddRange(new[] { featureTab, functionTab, gestureTab });
        ConfigureTabControlStyle(_tabs);

        _featureGrid = CreateReadOnlyGrid();
        _featureGrid.Margin = Padding.Empty;
        _featureGrid.BorderStyle = BorderStyle.FixedSingle;
        _featureGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Category", HeaderText = "分類", Width = 88, ReadOnly = true });
        _featureGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Name", HeaderText = "機能名", Width = 158, ReadOnly = true });
        _featureGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Shortcut", HeaderText = "通常キー", Width = 158, ReadOnly = true });
        _featureGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Function", HeaderText = "Fキー割り当て", Width = 135, ReadOnly = true });
        _featureGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Gesture", HeaderText = "ジェスチャー", Width = 88, ReadOnly = true });
        _featureGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Description", HeaderText = "説明", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 260, ReadOnly = true });
        ApplyReadOnlyColumnStyle(_featureGrid.Columns["Category"]);
        ApplyReadOnlyColumnStyle(_featureGrid.Columns["Name"]);
        ApplyReadOnlyColumnStyle(_featureGrid.Columns["Description"]);
        _featureGrid.CellDoubleClick += FeatureGrid_CellDoubleClick;
        _featureGrid.KeyDown += FeatureGrid_KeyDown;
        _featureGrid.SelectionChanged += FeatureGrid_SelectionChanged;

        var featureDescriptionPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            Padding = new Padding(8, 6, 8, 6),
            Margin = Padding.Empty
        };
        var featureDescriptionLabel = new Label
        {
            Text = "説明:",
            AutoSize = true,
            Location = new Point(0, 10)
        };
        _featureDescriptionValueLabel = new Label
        {
            AutoEllipsis = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Location = new Point(38, 10),
            Size = new Size(920, 20),
            Text = "(項目を選択すると説明を表示します)"
        };
        featureDescriptionPanel.Controls.Add(featureDescriptionLabel);
        featureDescriptionPanel.Controls.Add(_featureDescriptionValueLabel);

        var assignmentButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 36,
            Padding = new Padding(8, 4, 8, 4),
            FlowDirection = FlowDirection.LeftToRight
        };
        var resetSelected = new Button { Text = "選択項目を既定に戻す", Width = 180, Height = 28 };
        resetSelected.Click += (_, _) => ResetSelectedAssignment();
        var resetAll = new Button { Text = "現在の全割り当てを既定に戻す", Width = 220, Height = 28 };
        resetAll.Click += (_, _) => ResetAllAssignments();
        assignmentButtons.Controls.Add(resetSelected);
        assignmentButtons.Controls.Add(resetAll);

        featureTab.Padding = Padding.Empty;
        featureTab.Controls.Add(_featureGrid);
        featureTab.Controls.Add(featureDescriptionPanel);

        _functionLayerTabs = new TabControl { Dock = DockStyle.Top, Height = 52 };
        _functionLayerTabs.TabPages.Add("通常 F1〜F12");
        _functionLayerTabs.TabPages.Add("Shift+F1〜F12");
        _functionLayerTabs.TabPages.Add("Ctrl+F1〜F12");
        _functionLayerTabs.TabPages.Add("Alt+F1〜F12");
        _functionLayerTabs.SelectedIndexChanged += (_, _) => RefreshFunctionGrid();
        ConfigureTabControlStyle(_functionLayerTabs);

        _functionGrid = CreateReadOnlyGrid();
        _functionGrid.ReadOnly = false;
        _functionGrid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _functionGrid.EditMode = DataGridViewEditMode.EditOnEnter;
        _functionGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Slot", HeaderText = "キー", Width = 90, ReadOnly = true });
        var labelColumn = new DataGridViewTextBoxColumn
        {
            Name = "Label",
            HeaderText = "表示名",
            Width = 130,
            ReadOnly = false,
            ToolTipText = "セル上で直接編集します。空欄で既定に戻します。"
        };
        labelColumn.CellTemplate.Style.BackColor = Color.White;
        labelColumn.CellTemplate.Style.ForeColor = Color.Black;
        _functionGrid.Columns.Add(labelColumn);
        _functionGrid.Columns.Add(CreateCommandComboColumn("Command", "機能名", 290, includeDefault: true, includeUnassigned: true));
        _functionGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "NormalKey",
            HeaderText = "通常キー",
            Width = 158,
            ReadOnly = true,
            ToolTipText = "通常キーはこの機能に対する共通ショートカットです。編集は機能別ビューで行います。"
        });
        _functionGrid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Description",
            HeaderText = "説明",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 260,
            ReadOnly = true
        });
        ApplyReadOnlyColumnStyle(_functionGrid.Columns["Slot"]);
        ApplyReadOnlyColumnStyle(_functionGrid.Columns["NormalKey"]);
        ApplyReadOnlyColumnStyle(_functionGrid.Columns["Description"]);
        _functionGrid.CellClick += FunctionGrid_CellClick;
        _functionGrid.CellBeginEdit += FunctionGrid_CellBeginEdit;
        _functionGrid.CellDoubleClick += FunctionGrid_CellDoubleClick;
        _functionGrid.CellEndEdit += FunctionGrid_CellEndEdit;
        _functionGrid.CellValidating += FunctionGrid_CellValidating;
        _functionGrid.CellValueChanged += FunctionGrid_CellValueChanged;
        _functionGrid.CurrentCellDirtyStateChanged += FunctionGrid_CurrentCellDirtyStateChanged;
        _functionGrid.DataError += (_, e) => e.ThrowException = false;
        _functionGrid.EditingControlShowing += FunctionGrid_EditingControlShowing;
        _functionGrid.KeyDown += FunctionGrid_KeyDown;
        functionTab.Controls.Add(_functionGrid);
        functionTab.Controls.Add(_functionLayerTabs);

        _gestureGrid = CreateReadOnlyGrid();
        _gestureGrid.ReadOnly = false;
        _gestureGrid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _gestureGrid.EditMode = DataGridViewEditMode.EditOnEnter;
        _gestureGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Gesture", HeaderText = "ジェスチャー", Width = 130, ReadOnly = true });
        _gestureGrid.Columns.Add(CreateCommandComboColumn(
            "Command",
            "機能名",
            220,
            includeDefault: false,
            includeUnassigned: true,
            commands: GetMouseGestureAssignableCommandsForDialog()));
        _gestureGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Description", HeaderText = "説明", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, ReadOnly = true });
        ApplyReadOnlyColumnStyle(_gestureGrid.Columns["Gesture"]);
        ApplyReadOnlyColumnStyle(_gestureGrid.Columns["Description"]);
        _gestureGrid.CellClick += GestureGrid_CellClick;
        _gestureGrid.CellValueChanged += GestureGrid_CellValueChanged;
        _gestureGrid.CurrentCellDirtyStateChanged += GestureGrid_CurrentCellDirtyStateChanged;
        _gestureGrid.DataError += (_, e) => e.ThrowException = false;
        _gestureGrid.KeyDown += GestureGrid_KeyDown;
        var gestureHintLabel = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 34,
            Padding = new Padding(8, 6, 8, 4),
            Text = "誤操作防止のため、削除などの注意操作（dangerous）は候補に表示しません。",
            ForeColor = Color.DimGray,
            TextAlign = ContentAlignment.MiddleLeft
        };
        gestureTab.Controls.Add(gestureHintLabel);
        gestureTab.Controls.Add(_gestureGrid);

        Controls.Add(_tabs);
        Controls.Add(assignmentButtons);
        Controls.Add(profilePanel);
        RefreshAllViews();
    }

    public void FocusShortcutTab() => _tabs.SelectedIndex = 0;
    public void FocusFunctionBarTab() => _tabs.SelectedIndex = 1;
    public void FocusMouseGestureTab() => _tabs.SelectedIndex = 2;

    private static DataGridView CreateReadOnlyGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AutoGenerateColumns = false,
            BackgroundColor = SystemColors.Window
        };
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(216, 232, 245);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.Black;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(176, 208, 232);
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Color.Black;
        return grid;
    }

    private static void ApplyReadOnlyColumnStyle(DataGridViewColumn? column)
    {
        if (column == null)
        {
            return;
        }

        column.DefaultCellStyle.BackColor = Color.FromArgb(240, 244, 248);
        column.DefaultCellStyle.ForeColor = Color.Black;
    }

    private static void ConfigureTabControlStyle(TabControl tabs)
    {
        tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
        tabs.DrawItem += (_, e) =>
        {
            if (e.Index < 0 || e.Index >= tabs.TabPages.Count)
            {
                return;
            }

            bool isActive = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            Color backColor = isActive ? Color.FromArgb(172, 205, 232) : Color.FromArgb(235, 239, 243);
            using var background = new SolidBrush(backColor);
            e.Graphics.FillRectangle(background, e.Bounds);
            TextRenderer.DrawText(
                e.Graphics,
                tabs.TabPages[e.Index].Text,
                tabs.Font,
                e.Bounds,
                Color.Black,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };
    }

    private DataGridViewComboBoxColumn CreateCommandComboColumn(
        string name,
        string headerText,
        int width,
        bool includeDefault,
        bool includeUnassigned,
        IReadOnlyList<CommandDefinition>? commands = null)
    {
        return new DataGridViewComboBoxColumn
        {
            Name = name,
            HeaderText = headerText,
            Width = width,
            ReadOnly = false,
            DisplayMember = nameof(CommandOption.DisplayText),
            ValueMember = nameof(CommandOption.CommandId),
            DataSource = CreateCommandOptions(includeDefault, includeUnassigned, commands),
            DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
        };
    }

    private CommandOption[] CreateCommandOptions(bool includeDefault, bool includeUnassigned, IReadOnlyList<CommandDefinition>? commands = null)
    {
        var options = new List<CommandOption>();
        if (includeDefault)
        {
            options.Add(new CommandOption("(既定)", "__default__"));
        }
        if (includeDefault)
        {
            options.Add(new CommandOption("予約済み / 設定不可", ReservedFunctionSlotCommandId));
        }
        if (includeUnassigned)
        {
            options.Add(new CommandOption("(無効)", InputSettings.MouseGestureUnassignedCommandId));
        }

        IReadOnlyList<CommandDefinition> sourceCommands = commands ?? GetAssignableCommands();
        options.AddRange(sourceCommands
            .OrderBy(static x => x.DisplayName, StringComparer.Ordinal)
            .Select(static x => new CommandOption(FunctionKeyProfileService.ResolveCommandDisplayText(x), x.Id)));
        return options.ToArray();
    }

    private string ResolveProfileValue()
    {
        return _profileCombo.SelectedItem is ProfileOption option
            ? option.Value
            : InputSettings.StandardProfileValue;
    }

    private void RefreshAllViews()
    {
        _refreshing = true;
        RefreshFeatureGrid();
        RefreshFunctionGrid();
        RefreshGestureGrid();
        _refreshing = false;
    }

    private void RefreshFeatureGrid()
    {
        _featureGrid.Rows.Clear();
        IEnumerable<CommandDefinition> sortedCommands = GetFeatureCommands()
            .OrderBy(static x => GetCommandCategoryOrder(GetCommandCategoryForDisplay(x)))
            .ThenBy(static x => GetCommandDisplayOrder(x))
            .ThenBy(static x => x.DisplayName, StringComparer.Ordinal);
        foreach (CommandDefinition command in sortedCommands)
        {
            string category = GetCommandCategoryForDisplay(command);
            string shortcuts = string.Join(", ", GetEffectiveShortcutKeys(command.Id));
            string functionSlots = string.Join(", ", GetFunctionSlotsForCommand(command.Id));
            string gestures = string.Join(", ", GetGesturesForCommand(command.Id));
            int row = _featureGrid.Rows.Add(category, command.DisplayName, shortcuts, functionSlots, gestures, command.Description);
            _featureGrid.Rows[row].Tag = command.Id;
            if (!IsEditableCommand(command.Id))
            {
                for (int col = 2; col <= 4; col++)
                {
                    DataGridViewCell cell = _featureGrid.Rows[row].Cells[col];
                    cell.ReadOnly = true;
                    cell.Style.BackColor = Color.FromArgb(240, 244, 248);
                    cell.Style.ForeColor = Color.DimGray;
                }
            }
            else if (!IsMouseGestureAssignableCommand(command.Id))
            {
                DataGridViewCell gestureCell = _featureGrid.Rows[row].Cells[4];
                gestureCell.ReadOnly = true;
                gestureCell.Style.BackColor = Color.FromArgb(240, 244, 248);
                gestureCell.Style.ForeColor = Color.DimGray;
            }
        }

        if (_featureGrid.Rows.Count > 0)
        {
            _featureGrid.ClearSelection();
            _featureGrid.Rows[0].Selected = true;
            _featureGrid.CurrentCell = _featureGrid.Rows[0].Cells[0];
            UpdateFeatureDescriptionLabel(_featureGrid.Rows[0]);
            return;
        }

        _featureDescriptionValueLabel.Text = "(項目を選択すると説明を表示します)";
    }

    private void FeatureGrid_SelectionChanged(object? sender, EventArgs e)
    {
        if (_featureGrid.SelectedRows.Count > 0)
        {
            UpdateFeatureDescriptionLabel(_featureGrid.SelectedRows[0]);
            return;
        }

        if (_featureGrid.CurrentRow != null)
        {
            UpdateFeatureDescriptionLabel(_featureGrid.CurrentRow);
            return;
        }

        _featureDescriptionValueLabel.Text = "(項目を選択すると説明を表示します)";
    }

    private void UpdateFeatureDescriptionLabel(DataGridViewRow row)
    {
        string text = Convert.ToString(row.Cells["Description"].Value) ?? string.Empty;
        _featureDescriptionValueLabel.Text = string.IsNullOrWhiteSpace(text)
            ? "(説明なし)"
            : text;
    }

    private static string GetCommandCategoryForDisplay(CommandDefinition command)
    {
        string id = command.Id;
        if (id.Equals("file.copy", StringComparison.OrdinalIgnoreCase) ||
            id.Equals("file.move", StringComparison.OrdinalIgnoreCase) ||
            id.Equals("file.rename", StringComparison.OrdinalIgnoreCase) ||
            id.Equals("file.delete", StringComparison.OrdinalIgnoreCase) ||
            id.Equals(CommandIds.BrowserExecute, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(CommandIds.BrowserChangeAttributes, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(CommandIds.BrowserCreateDirectory, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(CommandIds.BrowserCopyFullPath, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(CommandIds.ClipboardPaste, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(CommandIds.EditUndo, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(CommandIds.EditRedo, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(CommandIds.ArchivePack, StringComparison.OrdinalIgnoreCase))
        {
            return "ファイル操作";
        }

        if (id.Equals(CommandIds.BrowserMarkAllFiles, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(CommandIds.BrowserMarkAllItems, StringComparison.OrdinalIgnoreCase))
        {
            return "マーク";
        }

        if (id.Equals(CommandIds.BrowserNavigateParent, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(CommandIds.BrowserNavigateBack, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(CommandIds.BrowserNavigateForward, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(CommandIds.BrowserCursorTop, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(CommandIds.BrowserCursorBottom, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(CommandIds.BrowserTabNext, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(CommandIds.BrowserTabPrevious, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(CommandIds.BrowserTabCategoryNext, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(CommandIds.BrowserTabCategoryPrevious, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(CommandIds.BrowserTabClose, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(CommandIds.BrowserTabRestoreClosed, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(CommandIds.BrowserTabNew, StringComparison.OrdinalIgnoreCase))
        {
            return "移動 / 履歴";
        }

        if (id.Equals(CommandIds.BrowserReload, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(CommandIds.BrowserSort, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(CommandIds.BrowserFilter, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(CommandIds.BrowserTree, StringComparison.OrdinalIgnoreCase))
        {
            return "表示 / 一覧";
        }

        if (id.Equals(CommandIds.BrowserOpenMarkSlot, StringComparison.OrdinalIgnoreCase))
        {
            return "マークスロット";
        }

        if (id.Equals(CommandIds.BrowserOpenExplorer, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(CommandIds.BrowserOpenShell, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(CommandIds.BrowserOpenExternalEditor, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(CommandIds.BrowserQuickAccess, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(CommandIds.BrowserLogdisk, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(CommandIds.BrowserPreview, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(CommandIds.ArchiveUnpack, StringComparison.OrdinalIgnoreCase))
        {
            return "外部連携";
        }

        if (id.Equals(CommandIds.BrowserShowHelp, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(CommandIds.AppOpenSystemInformation, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(CommandIds.AppOpenNewInstance, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(CommandIds.AppOpenControlPanel, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(CommandIds.AppOpenSettings, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(CommandIds.AppOpenCommandLauncher, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(CommandIds.AppOpenCommandList, StringComparison.OrdinalIgnoreCase))
        {
            return "アプリ / ヘルプ";
        }

        return "その他";
    }

    private static int GetCommandCategoryOrder(string category)
    {
        return category switch
        {
            "ファイル操作" => 0,
            "マーク" => 1,
            "移動 / 履歴" => 2,
            "表示 / 一覧" => 3,
            "マークスロット" => 4,
            "外部連携" => 5,
            "アプリ / ヘルプ" => 6,
            _ => 9
        };
    }

    private static int GetCommandDisplayOrder(CommandDefinition command)
    {
        string id = command.Id;
        if (id.Equals("file.copy", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (id.Equals(CommandIds.BrowserExecute, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (id.Equals("file.move", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (id.Equals("file.rename", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (id.Equals("file.delete", StringComparison.OrdinalIgnoreCase))
        {
            return 4;
        }

        if (id.Equals(CommandIds.BrowserChangeAttributes, StringComparison.OrdinalIgnoreCase))
        {
            return 5;
        }

        if (id.Equals(CommandIds.ClipboardPaste, StringComparison.OrdinalIgnoreCase))
        {
            return 20;
        }

        if (id.Equals(CommandIds.BrowserCopyFullPath, StringComparison.OrdinalIgnoreCase))
        {
            return 30;
        }

        if (id.Equals(CommandIds.BrowserMarkAllFiles, StringComparison.OrdinalIgnoreCase))
        {
            return 40;
        }

        if (id.Equals(CommandIds.BrowserMarkAllItems, StringComparison.OrdinalIgnoreCase))
        {
            return 41;
        }

        if (id.Equals(CommandIds.EditUndo, StringComparison.OrdinalIgnoreCase))
        {
            return 50;
        }

        if (id.Equals(CommandIds.EditRedo, StringComparison.OrdinalIgnoreCase))
        {
            return 51;
        }

        if (id.Equals(CommandIds.BrowserCreateDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return 60;
        }

        if (id.Equals(CommandIds.BrowserPreview, StringComparison.OrdinalIgnoreCase))
        {
            return 61;
        }

        if (id.Equals(CommandIds.ArchivePack, StringComparison.OrdinalIgnoreCase))
        {
            return 62;
        }

        return 100;
    }

    private IReadOnlyList<CommandDefinition> GetAssignableCommands()
    {
        return GetFeatureCommands()
            .Where(static c => c.IsCustomizable)
            .ToArray();
    }

    private IReadOnlyList<CommandDefinition> GetMouseGestureAssignableCommandsForDialog()
    {
        return _registry.GetMouseGestureAssignableCommands();
    }

    private IReadOnlyList<CommandDefinition> GetFeatureCommands()
    {
        var baseCommands = _registry.GetAll()
            .Where(static c =>
                c.Scope == CommandScope.Browser || c.Scope == CommandScope.Global)
            .ToDictionary(static c => c.Id, StringComparer.OrdinalIgnoreCase);

        foreach (string commandId in _settingsDraft.BrowserKeyCommandOverrides.Keys)
        {
            if (baseCommands.ContainsKey(commandId))
            {
                continue;
            }

            CommandDefinition? found = _registry.Find(commandId);
            if (found != null && !found.IsDangerous)
            {
                baseCommands[commandId] = found;
            }
        }

        foreach (string commandId in InputSettings.DefaultMouseGestureCommandMap.Values)
        {
            if (string.Equals(commandId, InputSettings.MouseGestureUnassignedCommandId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (baseCommands.ContainsKey(commandId))
            {
                continue;
            }

            CommandDefinition? found = _registry.Find(commandId);
            if (found != null && !found.IsDangerous)
            {
                baseCommands[commandId] = found;
            }
        }

        foreach (string? commandId in GetAllFunctionAssignments())
        {
            if (string.IsNullOrWhiteSpace(commandId))
            {
                continue;
            }

            if (baseCommands.ContainsKey(commandId))
            {
                continue;
            }

            CommandDefinition? found = _registry.Find(commandId);
            if (found != null && !found.IsDangerous)
            {
                baseCommands[commandId] = found;
            }
        }

        return baseCommands.Values.ToArray();
    }

    private IEnumerable<string?> GetAllFunctionAssignments()
    {
        foreach (string? commandId in _settingsDraft.FunctionBarCommandOverridesStandard.Values) yield return commandId;
        foreach (string? commandId in _settingsDraft.FunctionBarCommandOverridesFdCompatible.Values) yield return commandId;
        foreach (string? commandId in _settingsDraft.FunctionBarCommandOverridesShiftStandard.Values) yield return commandId;
        foreach (string? commandId in _settingsDraft.FunctionBarCommandOverridesShiftFdCompatible.Values) yield return commandId;
        foreach (string? commandId in _settingsDraft.FunctionBarCommandOverridesCtrlStandard.Values) yield return commandId;
        foreach (string? commandId in _settingsDraft.FunctionBarCommandOverridesCtrlFdCompatible.Values) yield return commandId;
        foreach (string? commandId in _settingsDraft.FunctionBarCommandOverridesAltStandard.Values) yield return commandId;
        foreach (string? commandId in _settingsDraft.FunctionBarCommandOverridesAltFdCompatible.Values) yield return commandId;
    }

    private void RefreshFunctionGrid()
    {
        RefreshFunctionGridPreservingSelection(preserveSelection: true);
    }

    private void RefreshFunctionGridPreservingSelection(bool preserveSelection)
    {
        int? selectedSlot = null;
        int selectedColumnIndex = 0;
        if (preserveSelection && _functionGrid.CurrentRow?.Tag is ValueTuple<int, FunctionLayer, bool, bool> currentTag)
        {
            selectedSlot = currentTag.Item1;
            selectedColumnIndex = _functionGrid.CurrentCell?.ColumnIndex ?? 0;
        }

        _functionGrid.Rows.Clear();
        bool isFdCompatible = string.Equals(ResolveProfileValue(), InputSettings.FdCompatibleProfileValue, StringComparison.OrdinalIgnoreCase);
        FunctionKeyProfile profile = isFdCompatible ? FunctionKeyProfile.FDCompatible : FunctionKeyProfile.Standard;
        FunctionLayer layer = GetSelectedFunctionLayer();
        Dictionary<string, FunctionBarLabelOverride> labelOverrides = GetFunctionBarLabelOverrideMap(layer, isFdCompatible);
        bool supportsLabelOverride = true;
        if (_functionGrid.Columns["Command"] is DataGridViewComboBoxColumn comboColumn)
        {
            comboColumn.ReadOnly = false;
        }

        for (int slot = 1; slot <= 12; slot++)
        {
            string slotKey = $"F{slot}";
            string slotDisplay = layer switch
            {
                FunctionLayer.Shift => $"Shift+F{slot}",
                FunctionLayer.Ctrl => $"Ctrl+F{slot}",
                FunctionLayer.Alt => $"Alt+F{slot}",
                _ => $"F{slot}"
            };
            string? effectiveCommandId = ResolveFunctionCommandId(profile, slot, layer);
            Dictionary<string, string?> layerMap = GetOverrideMap(layer, isFdCompatible);
            bool hasOverride = layerMap.ContainsKey(slotKey);
            string? commandValue;
            if (!hasOverride && !string.IsNullOrWhiteSpace(effectiveCommandId))
            {
                commandValue = effectiveCommandId;
            }
            else if (hasOverride && layerMap.TryGetValue(slotKey, out string? overridden))
            {
                commandValue = string.IsNullOrWhiteSpace(overridden) ? InputSettings.MouseGestureUnassignedCommandId : overridden;
            }
            else
            {
                commandValue = InputSettings.MouseGestureUnassignedCommandId;
            }

            bool isReserved = layer == FunctionLayer.Alt && slot == 4;
            string? activeCommandId = isReserved
                ? ReservedFunctionSlotCommandId
                : ResolveFunctionGridActiveCommandId(commandValue, effectiveCommandId);
            string baseDisplayLabel = isReserved
                ? "Windows標準: 閉じる"
                : ResolveFunctionBarBaseDisplayLabel(profile, slot, layer);
            string displayLabel = ResolveFunctionBarDisplayLabel(profile, slot, layer, activeCommandId, labelOverrides);
            string normalKeyText = isReserved ? "(なし)" : ResolveFunctionBarNormalKeyText(activeCommandId);
            string descriptionText = isReserved
                ? "Windows標準の閉じる操作です。"
                : ResolveFunctionBarDescriptionText(activeCommandId);
            bool hasLabelOverride = !isReserved && HasFunctionBarLabelOverride(slotKey, activeCommandId, baseDisplayLabel, labelOverrides);
            int row = _functionGrid.Rows.Add(
                slotDisplay,
                displayLabel,
                isReserved ? ReservedFunctionSlotCommandId : commandValue,
                normalKeyText,
                descriptionText);
            _functionGrid.Rows[row].Tag = (slot, layer, hasOverride, isReserved);
            DataGridViewCell labelCell = _functionGrid.Rows[row].Cells["Label"];
            labelCell.ReadOnly = !supportsLabelOverride || isReserved;
            if (!supportsLabelOverride || isReserved)
            {
                labelCell.Style.BackColor = Color.FromArgb(240, 244, 248);
                labelCell.Style.ForeColor = Color.DimGray;
            }
            else if (hasLabelOverride)
            {
                labelCell.Style.ForeColor = SystemColors.HotTrack;
            }
            else
            {
                labelCell.Style.ForeColor = SystemColors.WindowText;
            }
            if (isReserved)
            {
                DataGridViewCell commandCell = _functionGrid.Rows[row].Cells[2];
                commandCell.ReadOnly = true;
                commandCell.Style.BackColor = Color.FromArgb(240, 244, 248);
                commandCell.Style.ForeColor = Color.DimGray;
            }
            else
            {
                _functionGrid.Rows[row].DefaultCellStyle.ForeColor = SystemColors.WindowText;
            }
            _functionGrid.Rows[row].Cells["Command"].Style.ForeColor = hasOverride ? SystemColors.HotTrack : SystemColors.WindowText;
        }

        if (selectedSlot.HasValue)
        {
            RestoreFunctionGridSelection(selectedSlot.Value, selectedColumnIndex);
        }
        else if (_functionGrid.Rows.Count > 0 && _functionGrid.CurrentCell == null)
        {
            _functionGrid.CurrentCell = _functionGrid.Rows[0].Cells[0];
        }
    }

    private void RestoreFunctionGridSelection(int slot, int columnIndex)
    {
        foreach (DataGridViewRow row in _functionGrid.Rows)
        {
            if (row.Tag is not ValueTuple<int, FunctionLayer, bool, bool> rowTag || rowTag.Item1 != slot)
            {
                continue;
            }

            int safeColumnIndex = Math.Max(0, Math.Min(columnIndex, row.Cells.Count - 1));
            _functionGrid.CurrentCell = row.Cells[safeColumnIndex];
            row.Selected = true;
            return;
        }
    }

    private string ResolveFunctionCommandDisplayLabel(string? commandId)
    {
        if (string.IsNullOrWhiteSpace(commandId) || string.Equals(commandId, InputSettings.MouseGestureUnassignedCommandId, StringComparison.OrdinalIgnoreCase))
        {
            return "(未割り当て)";
        }

        if (_commandById.ContainsKey(commandId))
        {
            return FunctionKeyProfileService.ResolveFunctionBarShortLabel(commandId);
        }

        return commandId;
    }

    private string? GetFunctionGridActiveCommandIdForRow(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= _functionGrid.Rows.Count)
        {
            return null;
        }

        if (_functionGrid.Rows[rowIndex].Tag is not ValueTuple<int, FunctionLayer, bool, bool> rowTag)
        {
            return null;
        }

        int slot = rowTag.Item1;
        FunctionLayer layer = rowTag.Item2;
        bool isFdCompatible = string.Equals(ResolveProfileValue(), InputSettings.FdCompatibleProfileValue, StringComparison.OrdinalIgnoreCase);
        string? currentCommandValue = _functionGrid.Rows[rowIndex].Cells["Command"].Value?.ToString();
        string? defaultCommandId = ResolveFunctionCommandId(
            isFdCompatible ? FunctionKeyProfile.FDCompatible : FunctionKeyProfile.Standard,
            slot,
            layer);
        return ResolveFunctionGridActiveCommandId(currentCommandValue, defaultCommandId);
    }

    private bool CanEditFunctionGridLabel(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= _functionGrid.Rows.Count)
        {
            return false;
        }

        if (_functionGrid.Rows[rowIndex].Tag is not ValueTuple<int, FunctionLayer, bool, bool> rowTag || rowTag.Item4)
        {
            return false;
        }

        string? activeCommandId = GetFunctionGridActiveCommandIdForRow(rowIndex);
        if (string.IsNullOrWhiteSpace(activeCommandId) ||
            string.Equals(activeCommandId, InputSettings.MouseGestureUnassignedCommandId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private void CommitFunctionGridLabelEdit(int rowIndex)
    {
        if (!CanEditFunctionGridLabel(rowIndex) ||
            _functionGrid.Rows[rowIndex].Tag is not ValueTuple<int, FunctionLayer, bool, bool> rowTag)
        {
            return;
        }

        bool isFdCompatible = string.Equals(ResolveProfileValue(), InputSettings.FdCompatibleProfileValue, StringComparison.OrdinalIgnoreCase);
        string slotKey = $"F{rowTag.Item1}";
        string? activeCommandId = GetFunctionGridActiveCommandIdForRow(rowIndex);
        FunctionLayer layer = rowTag.Item2;
        FunctionKeyProfile profile = isFdCompatible ? FunctionKeyProfile.FDCompatible : FunctionKeyProfile.Standard;
        Dictionary<string, FunctionBarLabelOverride> labelOverrides = GetFunctionBarLabelOverrideMap(layer, isFdCompatible);
        string input = _functionGrid.Rows[rowIndex].Cells["Label"].Value?.ToString() ?? string.Empty;

        if (string.Equals(input, _functionLabelEditingOriginalText, StringComparison.Ordinal))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(input) && !ValidateFunctionBarLabel(input, out _))
        {
            RestoreFunctionGridLabelCell(rowIndex, rowTag.Item1, rowTag.Item2, rowTag.Item4, activeCommandId, isFdCompatible, labelOverrides);
            return;
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            labelOverrides.Remove(slotKey);
            SetFunctionBarLabelOverrideMap(layer, isFdCompatible, labelOverrides);
            RestoreFunctionGridLabelCell(rowIndex, rowTag.Item1, rowTag.Item2, rowTag.Item4, activeCommandId, isFdCompatible, labelOverrides);
            return;
        }

        string normalized = InputSettings.NormalizeFunctionBarLabelText(input);
        string defaultShortLabel = ResolveFunctionBarBaseDisplayLabel(profile, rowTag.Item1, layer);
        if (string.Equals(normalized, defaultShortLabel, StringComparison.OrdinalIgnoreCase))
        {
            labelOverrides.Remove(slotKey);
        }
        else
        {
            labelOverrides[slotKey] = new FunctionBarLabelOverride
            {
                CommandId = activeCommandId!,
                Label = normalized
            };
        }

        SetFunctionBarLabelOverrideMap(layer, isFdCompatible, labelOverrides);
        RestoreFunctionGridLabelCell(rowIndex, rowTag.Item1, rowTag.Item2, rowTag.Item4, activeCommandId, isFdCompatible, labelOverrides);
    }

    private void RestoreFunctionGridLabelCell(
        int rowIndex,
        int slot,
        FunctionLayer layer,
        bool isReserved,
        string? activeCommandId,
        bool isFdCompatible,
        Dictionary<string, FunctionBarLabelOverride> labelOverrides)
    {
        if (rowIndex < 0 || rowIndex >= _functionGrid.Rows.Count)
        {
            return;
        }

        string slotKey = $"F{slot}";
        DataGridViewRow row = _functionGrid.Rows[rowIndex];
        DataGridViewCell labelCell = row.Cells["Label"];
        bool supportsLabelOverride = true;
        bool hasCommandOverride = GetOverrideMap(layer, isFdCompatible).ContainsKey(slotKey);
        FunctionKeyProfile profile = string.Equals(ResolveProfileValue(), InputSettings.FdCompatibleProfileValue, StringComparison.OrdinalIgnoreCase)
            ? FunctionKeyProfile.FDCompatible
            : FunctionKeyProfile.Standard;
        string baseDisplayLabel = isReserved
            ? "Windows標準: 閉じる"
            : ResolveFunctionBarBaseDisplayLabel(profile, slot, layer);
        bool hasLabelOverride = !isReserved && HasFunctionBarLabelOverride(slotKey, activeCommandId, baseDisplayLabel, labelOverrides);

        labelCell.Value = ResolveFunctionBarDisplayLabel(profile, slot, layer, activeCommandId, labelOverrides);
        labelCell.ReadOnly = !supportsLabelOverride || isReserved;
        labelCell.ErrorText = string.Empty;

        if (!supportsLabelOverride || isReserved)
        {
            labelCell.Style.BackColor = Color.FromArgb(240, 244, 248);
            labelCell.Style.ForeColor = Color.DimGray;
        }
        else if (hasLabelOverride)
        {
            labelCell.Style.BackColor = Color.White;
            labelCell.Style.ForeColor = SystemColors.HotTrack;
        }
        else
        {
            labelCell.Style.BackColor = Color.White;
            labelCell.Style.ForeColor = SystemColors.WindowText;
        }

        row.Tag = (slot, layer, hasCommandOverride, isReserved);
        row.DefaultCellStyle.ForeColor = hasLabelOverride
            ? SystemColors.HotTrack
            : SystemColors.WindowText;
        row.Cells["Command"].Style.ForeColor = hasCommandOverride ? SystemColors.HotTrack : SystemColors.WindowText;
    }

    internal static bool ValidateFunctionBarLabel(string input, out string errorMessage)
    {
        errorMessage = string.Empty;
        string normalized = InputSettings.NormalizeFunctionBarLabelText(input);

        if (normalized.Contains(":") || normalized.Contains("："))
        {
            errorMessage = "\":\" (コロン) は含められません。";
            return false;
        }

        if (normalized.Contains("\n") || normalized.Contains("\r") || normalized.Contains("\t"))
        {
            errorMessage = "改行やタブは含められません。";
            return false;
        }

        if (GetFunctionBarLabelDisplayCellCount(normalized) > FunctionBarLabelMaxDisplayCells)
        {
            errorMessage = "表示名は半角6セル、全角3文字相当まで入力できます。";
            return false;
        }

        if (System.Text.RegularExpressions.Regex.IsMatch(normalized, @"^(F\d+|Shift\+F\d+)\s*:?", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            errorMessage = "キー表記 (F1: 等) の混入は禁止されています。";
            return false;
        }

        return true;
    }

    private static int GetFunctionBarLabelDisplayCellCount(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        int cells = 0;
        TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext())
        {
            cells += IsFunctionBarLabelSingleCell(enumerator.GetTextElement()) ? 1 : 2;
        }

        return cells;
    }

    private static bool IsFunctionBarLabelSingleCell(string textElement)
    {
        if (string.IsNullOrEmpty(textElement))
        {
            return true;
        }

        if (!System.Text.Rune.TryGetRuneAt(textElement, 0, out System.Text.Rune rune))
        {
            return false;
        }

        int scalar = rune.Value;
        return (scalar >= 0x0020 && scalar <= 0x007E) || (scalar >= 0xFF61 && scalar <= 0xFF9F);
    }

    private Dictionary<string, FunctionBarLabelOverride> GetFunctionBarLabelOverrideMap(FunctionLayer layer, bool isFdCompatible)
    {
        Dictionary<string, FunctionBarLabelOverride> source = layer switch
        {
            FunctionLayer.Shift => isFdCompatible ? _settingsDraft.FunctionBarLabelOverridesShiftFdCompatible : _settingsDraft.FunctionBarLabelOverridesShiftStandard,
            FunctionLayer.Ctrl => isFdCompatible ? _settingsDraft.FunctionBarLabelOverridesCtrlFdCompatible : _settingsDraft.FunctionBarLabelOverridesCtrlStandard,
            FunctionLayer.Alt => isFdCompatible ? _settingsDraft.FunctionBarLabelOverridesAltFdCompatible : _settingsDraft.FunctionBarLabelOverridesAltStandard,
            _ => isFdCompatible ? _settingsDraft.FunctionBarLabelOverridesFdCompatible : _settingsDraft.FunctionBarLabelOverridesStandard
        };
        return new Dictionary<string, FunctionBarLabelOverride>(source ?? new Dictionary<string, FunctionBarLabelOverride>(), StringComparer.OrdinalIgnoreCase);
    }

    private void SetFunctionBarLabelOverrideMap(FunctionLayer layer, bool isFdCompatible, Dictionary<string, FunctionBarLabelOverride> map)
    {
        if (layer == FunctionLayer.Shift)
        {
            if (isFdCompatible) _settingsDraft.FunctionBarLabelOverridesShiftFdCompatible = map;
            else _settingsDraft.FunctionBarLabelOverridesShiftStandard = map;
        }
        else if (layer == FunctionLayer.Ctrl)
        {
            if (isFdCompatible) _settingsDraft.FunctionBarLabelOverridesCtrlFdCompatible = map;
            else _settingsDraft.FunctionBarLabelOverridesCtrlStandard = map;
        }
        else if (layer == FunctionLayer.Alt)
        {
            if (isFdCompatible) _settingsDraft.FunctionBarLabelOverridesAltFdCompatible = map;
            else _settingsDraft.FunctionBarLabelOverridesAltStandard = map;
        }
        else
        {
            if (isFdCompatible) _settingsDraft.FunctionBarLabelOverridesFdCompatible = map;
            else _settingsDraft.FunctionBarLabelOverridesStandard = map;
        }
    }

    private string ResolveFunctionBarDisplayLabel(
        FunctionKeyProfile profile,
        int slot,
        FunctionLayer layer,
        string? commandId,
        Dictionary<string, FunctionBarLabelOverride> labelOverrides)
    {
        if (string.IsNullOrWhiteSpace(commandId) ||
            string.Equals(commandId, InputSettings.MouseGestureUnassignedCommandId, StringComparison.OrdinalIgnoreCase))
        {
            return "(未割り当て)";
        }

        if (string.Equals(commandId, ReservedFunctionSlotCommandId, StringComparison.OrdinalIgnoreCase))
        {
            return "Windows標準: 閉じる";
        }

        return FunctionKeyProfileService.ResolveFunctionBarDisplayLabel(
            profile,
            slot,
            layer == FunctionLayer.Shift,
            layer == FunctionLayer.Ctrl,
            layer == FunctionLayer.Alt,
            commandId,
            labelOverrides);
    }

    private string ResolveFunctionBarBaseDisplayLabel(FunctionKeyProfile profile, int slot, FunctionLayer layer)
    {
        return FunctionKeyProfileService.ResolveFunctionBarDefaultDisplayLabel(
            profile,
            slot,
            layer == FunctionLayer.Shift,
            layer == FunctionLayer.Ctrl,
            layer == FunctionLayer.Alt);
    }

    private static bool HasFunctionBarLabelOverride(string slotKey, string? commandId, string baseDisplayLabel, Dictionary<string, FunctionBarLabelOverride> labelOverrides)
    {
        return !string.IsNullOrWhiteSpace(commandId) &&
               !string.Equals(commandId, InputSettings.MouseGestureUnassignedCommandId, StringComparison.OrdinalIgnoreCase) &&
               labelOverrides.TryGetValue(slotKey, out FunctionBarLabelOverride? labelOverride) &&
               labelOverride != null &&
               string.Equals(labelOverride.CommandId, commandId, StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(labelOverride.Label) &&
               !string.Equals(InputSettings.NormalizeFunctionBarLabelText(labelOverride.Label), InputSettings.NormalizeFunctionBarLabelText(baseDisplayLabel), StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveFunctionBarNormalKeyText(string? commandId)
    {
        if (string.IsNullOrWhiteSpace(commandId) || string.Equals(commandId, InputSettings.MouseGestureUnassignedCommandId, StringComparison.OrdinalIgnoreCase))
        {
            return "(なし)";
        }

        List<string> shortcuts = GetEffectiveShortcutKeys(commandId);
        return shortcuts.Count == 0 ? "(なし)" : string.Join(", ", shortcuts);
    }

    private string ResolveFunctionBarDescriptionText(string? commandId)
    {
        if (string.IsNullOrWhiteSpace(commandId) || string.Equals(commandId, InputSettings.MouseGestureUnassignedCommandId, StringComparison.OrdinalIgnoreCase))
        {
            return "このスロットには機能が割り当てられていません。";
        }

        if (string.Equals(commandId, ReservedFunctionSlotCommandId, StringComparison.OrdinalIgnoreCase))
        {
            return "Windows標準の閉じる操作です。";
        }

        if (_commandById.TryGetValue(commandId, out CommandDefinition? command))
        {
            return command.Description;
        }

        return $"未登録のコマンドID: {commandId}";
    }

    private string? ResolveFunctionGridActiveCommandId(string? commandValue, string? defaultCommandId)
    {
        if (string.IsNullOrWhiteSpace(commandValue) ||
            string.Equals(commandValue, "__default__", StringComparison.OrdinalIgnoreCase))
        {
            return defaultCommandId;
        }

        return commandValue;
    }

    private FunctionLayer GetSelectedFunctionLayer()
    {
        return _functionLayerTabs.SelectedIndex switch
        {
            1 => FunctionLayer.Shift,
            2 => FunctionLayer.Ctrl,
            3 => FunctionLayer.Alt,
            _ => FunctionLayer.Normal
        };
    }

    private string? ResolveFunctionCommandId(FunctionKeyProfile profile, int slot, FunctionLayer layer)
    {
        return FunctionKeyProfileService.ResolveFunctionBarCommandId(
            profile,
            slot,
            _settingsDraft.FunctionBarCommandOverridesStandard,
            _settingsDraft.FunctionBarCommandOverridesFdCompatible,
            _settingsDraft.FunctionBarCommandOverridesShiftStandard,
            _settingsDraft.FunctionBarCommandOverridesShiftFdCompatible,
            layer == FunctionLayer.Shift,
            _settingsDraft.FunctionBarCommandOverridesCtrlStandard,
            _settingsDraft.FunctionBarCommandOverridesCtrlFdCompatible,
            _settingsDraft.FunctionBarCommandOverridesAltStandard,
            _settingsDraft.FunctionBarCommandOverridesAltFdCompatible,
            layer == FunctionLayer.Ctrl,
            layer == FunctionLayer.Alt);
    }

    private void RefreshGestureGrid()
    {
        _gestureGrid.Rows.Clear();
        var map = GetEffectiveMouseGestureCommandMap();
        foreach (string gesture in GetGestureDisplayOrder())
        {
            string commandId = map.TryGetValue(gesture, out string? mapped)
                ? mapped
                : (InputSettings.DefaultMouseGestureCommandMap.TryGetValue(gesture, out string? defaultCommandId)
                    ? defaultCommandId
                    : InputSettings.MouseGestureUnassignedCommandId);
            _commandById.TryGetValue(commandId, out CommandDefinition? command);
            bool isUnassigned = string.Equals(commandId, InputSettings.MouseGestureUnassignedCommandId, StringComparison.OrdinalIgnoreCase);
            string gestureDisplay = ToGestureDirectionLabel(gesture);
            string description = isUnassigned
                ? "ジェスチャーを無効にします。"
                : (command?.Description ?? string.Empty);
            int row = _gestureGrid.Rows.Add(
                gestureDisplay,
                isUnassigned ? InputSettings.MouseGestureUnassignedCommandId : commandId,
                description);
            _gestureGrid.Rows[row].Tag = (gesture, commandId);
        }
    }

    private List<string> GetEffectiveShortcutKeys(string commandId)
    {
        var overrides = InputSettings.NormalizeBrowserKeyCommandOverrides(_settingsDraft.BrowserKeyCommandOverrides);
        if (overrides.TryGetValue(commandId, out List<string>? overrideKeys))
        {
            List<string> normalized = InputSettings.NormalizeBrowserKeyGestures(overrideKeys)
                .Where(static g => !InputSettings.IsFunctionKeyChordGesture(g))
                .ToList();
            return normalized.Count == 0 ? new List<string> { "(未割り当て)" } : normalized;
        }

        IReadOnlyDictionary<string, IReadOnlyList<string>> defaults = InputSettings.GetDefaultBrowserKeyCommandMap(ResolveProfileValue());
        if (defaults.TryGetValue(commandId, out IReadOnlyList<string>? defaultKeys))
        {
            List<string> normalizedDefaults = InputSettings.NormalizeBrowserKeyGestures(defaultKeys)
                .Where(static g => !InputSettings.IsFunctionKeyChordGesture(g))
                .ToList();
            return normalizedDefaults.Count == 0 ? new List<string> { "(未割り当て)" } : normalizedDefaults;
        }

        return new List<string> { "(なし)" };
    }

    private List<string> GetFunctionSlotsForCommand(string commandId)
    {
        bool isFdCompatible = string.Equals(ResolveProfileValue(), InputSettings.FdCompatibleProfileValue, StringComparison.OrdinalIgnoreCase);
        FunctionKeyProfile profile = isFdCompatible ? FunctionKeyProfile.FDCompatible : FunctionKeyProfile.Standard;
        var slots = new List<string>();
        for (int slot = 1; slot <= 12; slot++)
        {
            string? normalId = ResolveFunctionCommandId(profile, slot, FunctionLayer.Normal);
            if (string.Equals(normalId, commandId, StringComparison.OrdinalIgnoreCase))
            {
                slots.Add($"F{slot}");
            }

            string? shiftId = ResolveFunctionCommandId(profile, slot, FunctionLayer.Shift);
            if (string.Equals(shiftId, commandId, StringComparison.OrdinalIgnoreCase))
            {
                slots.Add($"Shift+F{slot}");
            }
            string? ctrlId = ResolveFunctionCommandId(profile, slot, FunctionLayer.Ctrl);
            if (string.Equals(ctrlId, commandId, StringComparison.OrdinalIgnoreCase))
            {
                slots.Add($"Ctrl+F{slot}");
            }
            string? altId = ResolveFunctionCommandId(profile, slot, FunctionLayer.Alt);
            if (string.Equals(altId, commandId, StringComparison.OrdinalIgnoreCase))
            {
                slots.Add($"Alt+F{slot}");
            }
        }

        return slots.Count == 0 ? new List<string> { "(なし)" } : slots;
    }

    private List<string> GetGesturesForCommand(string commandId)
    {
        var map = GetEffectiveMouseGestureCommandMap();
        List<string> gestures = map
            .Where(kv => string.Equals(kv.Value, commandId, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .OrderBy(static x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return gestures.Count == 0 ? new List<string> { "(なし)" } : gestures;
    }

    private Dictionary<string, string> GetEffectiveMouseGestureCommandMap()
    {
        var map = new Dictionary<string, string>(InputSettings.DefaultMouseGestureCommandMap, StringComparer.OrdinalIgnoreCase);
        foreach ((string gesture, string commandId) in InputSettings.NormalizeMouseGestureCommandMap(_settingsDraft.MouseGestureCommandMap))
        {
            map[gesture] = commandId;
        }

        return map;
    }

    private static string ToGestureDirectionLabel(string gesture)
    {
        return gesture.Replace("L", "左").Replace("R", "右").Replace("U", "上").Replace("D", "下");
    }

    private static IReadOnlyList<string> GetGestureDisplayOrder()
    {
        return new[]
        {
            "U", "UR", "UL", "UD",
            "R", "RU", "RL", "RD",
            "D", "DU", "DL", "DR",
            "L", "LU", "LR", "LD"
        };
    }

    private void FeatureGrid_KeyDown(object? sender, KeyEventArgs e)
    {
        if (_featureGrid.CurrentRow?.Tag is not string commandId || _featureGrid.CurrentCell == null)
        {
            return;
        }

        if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.F2)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            OpenFeatureEditorByColumn(commandId, _featureGrid.CurrentCell.ColumnIndex);
        }
        else if (e.KeyCode == Keys.Delete)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            DeleteFeatureAssignment(commandId, _featureGrid.CurrentCell.ColumnIndex);
        }
    }

    private void FeatureGrid_CellDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || _featureGrid.Rows[e.RowIndex].Tag is not string commandId)
        {
            return;
        }

        OpenFeatureEditorByColumn(commandId, e.ColumnIndex);
    }

    private void OpenFeatureEditorByColumn(string commandId, int col)
    {
        if (!IsEditableCommand(commandId))
        {
            MessageBox.Show(this, "この機能は安全性のため入力割り当てを変更できません。", "編集不可", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (col == 2)
        {
            OpenShortcutEditor(commandId);
        }
        else if (col == 3)
        {
            OpenFunctionEditor(commandId);
        }
        else if (col == 4)
        {
            if (!IsMouseGestureAssignableCommand(commandId))
            {
                MessageBox.Show(this, "この機能は誤操作防止のためマウスジェスチャーには割り当てできません。", "割り当て不可", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            OpenGestureEditor(commandId);
        }
    }

    private void DeleteFeatureAssignment(string commandId, int col)
    {
        if (!IsEditableCommand(commandId))
        {
            return;
        }

        if (col == 2)
        {
            var overrides = InputSettings.NormalizeBrowserKeyCommandOverrides(_settingsDraft.BrowserKeyCommandOverrides);
            overrides.TryGetValue(commandId, out List<string>? existing);
            List<string> working = InputSettings.NormalizeBrowserKeyGestures(existing ?? new List<string>());
            IReadOnlyDictionary<string, IReadOnlyList<string>> defaults = InputSettings.GetDefaultBrowserKeyCommandMap(ResolveProfileValue());
            List<string> current = working.Count > 0
                ? working
                : (defaults.TryGetValue(commandId, out IReadOnlyList<string>? defaultForCurrent)
                    ? InputSettings.NormalizeBrowserKeyGestures(defaultForCurrent)
                    : new List<string>());

            if (!_commandById.TryGetValue(commandId, out CommandDefinition? command))
            {
                return;
            }

            if (current.Count > 1)
            {
                if (!TryDeleteShortcutGesture(command.DisplayName, current, out List<string> remainingAfterDelete))
                {
                    return;
                }
                overrides[commandId] = remainingAfterDelete;
                _settingsDraft.BrowserKeyCommandOverrides = InputSettings.NormalizeBrowserKeyCommandOverrides(overrides);
            }
            else
            {
                if (MessageBox.Show(this, "選択機能のショートカットキー割り当てを解除しますか？", "確認", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                {
                    return;
                }
                _settingsDraft.BrowserKeyCommandOverrides[commandId] = new List<string>();
            }
        }
        else if (col == 3)
        {
            if (MessageBox.Show(this, "選択機能のFunctionバー割り当てを解除しますか？", "確認", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                return;
            }
            ResetFunctionAssignmentForCommand(commandId);
        }
        else if (col == 4)
        {
            if (!IsMouseGestureAssignableCommand(commandId))
            {
                return;
            }
            if (MessageBox.Show(this, "選択機能のジェスチャー割り当てを解除しますか？", "確認", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                return;
            }
            ResetGestureAssignmentForCommand(commandId);
        }
        else
        {
            return;
        }

        RefreshAllViews();
    }

    private void OpenShortcutEditor(string commandId)
    {
        var overrides = InputSettings.NormalizeBrowserKeyCommandOverrides(_settingsDraft.BrowserKeyCommandOverrides);
        overrides.TryGetValue(commandId, out List<string>? existing);
        List<string> working = InputSettings.NormalizeBrowserKeyGestures(existing ?? new List<string>());
        IReadOnlyDictionary<string, IReadOnlyList<string>> defaults = InputSettings.GetDefaultBrowserKeyCommandMap(ResolveProfileValue());
        List<string> current = working.Count > 0
            ? working
            : (defaults.TryGetValue(commandId, out IReadOnlyList<string>? defaultForCurrent)
                ? InputSettings.NormalizeBrowserKeyGestures(defaultForCurrent)
                : new List<string>());
        string currentText = current.Count == 0 ? "(未割り当て)" : string.Join(", ", current);

        if (!_commandById.TryGetValue(commandId, out CommandDefinition? command))
        {
            return;
        }

        using var dialog = new KeyCaptureDialog(command.DisplayName, currentText);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        if (dialog.IsDeleted)
        {
            List<string> deleteSource = current;
            if (!TryDeleteShortcutGesture(command.DisplayName, deleteSource, out List<string> remainingAfterDelete))
            {
                return;
            }
            overrides[commandId] = remainingAfterDelete;
            _settingsDraft.BrowserKeyCommandOverrides = InputSettings.NormalizeBrowserKeyCommandOverrides(overrides);
            RefreshAllViews();
            return;
        }

        string capturedGesture = InputSettings.ToKeyGestureText(dialog.CapturedKeyData);
        string normalizedGesture = InputSettings.NormalizeKeyGestureText(capturedGesture);
        if (string.IsNullOrWhiteSpace(normalizedGesture))
        {
            return;
        }
        if (InputSettings.IsFunctionKeyChordGesture(normalizedGesture))
        {
            MessageBox.Show(this, "F1〜F12（Shift/Ctrl/Alt含む）はファンクションキー/バー側で設定してください。", "入力種別", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (InputSettings.IsBrowserStructuralReservedGesture(normalizedGesture))
        {
            MessageBox.Show(this, "このキーはBrowserの表示/列操作に予約されています。", "予約キー", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        bool hasExisting = current.Any();
        string mode = hasExisting ? ChooseShortcutInsertMode(command.DisplayName, current, normalizedGesture) : "add";
        if (mode == "cancel")
        {
            return;
        }

        if (!AssignShortcutGestureToCommand(commandId, normalizedGesture, "キー競合", mode == "replace"))
        {
            return;
        }
        RefreshAllViews();
    }

    private bool AssignShortcutGestureToCommand(string targetCommandId, string gesture, string confirmTitle, bool replaceExistingForTarget = false)
    {
        if (InputSettings.IsBrowserStructuralReservedGesture(gesture))
        {
            MessageBox.Show(this, "このキーはBrowserの表示/列操作に予約されています。", "予約キー", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        var overrides = InputSettings.NormalizeBrowserKeyCommandOverrides(_settingsDraft.BrowserKeyCommandOverrides);
        IReadOnlyDictionary<string, IReadOnlyList<string>> defaults = InputSettings.GetDefaultBrowserKeyCommandMap(ResolveProfileValue());
        var effective = BrowserCommandBindingResolver.ResolveEffectiveKeyCommandMap(
            ResolveProfileValue(),
            overrides,
            _registry);

        if (effective.TryGetValue(gesture, out string? existingCommandId) &&
            !string.IsNullOrWhiteSpace(existingCommandId) &&
            !string.Equals(existingCommandId, targetCommandId, StringComparison.OrdinalIgnoreCase))
        {
            if (!IsEditableCommand(existingCommandId))
            {
                MessageBox.Show(this, "このキーは安全性のため変更できない機能に割り当てられています。", "編集不可", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            string existingName = _commandById.TryGetValue(existingCommandId, out CommandDefinition? existing)
                ? existing.DisplayName
                : existingCommandId;
            string existingDescription = _commandById.TryGetValue(existingCommandId, out CommandDefinition? existingDef)
                ? existingDef.Description
                : string.Empty;
            using var conflict = new AssignmentConflictDialog(
                confirmTitle,
                $"入力 '{gesture}' は既に他機能へ割り当て済みです。",
                existingName,
                existingDescription);
            if (conflict.ShowDialog(this) != DialogResult.Yes)
            {
                return false;
            }

            List<string> existingEffective = overrides.TryGetValue(existingCommandId, out List<string>? existingOverride)
                ? InputSettings.NormalizeBrowserKeyGestures(existingOverride)
                : (defaults.TryGetValue(existingCommandId, out IReadOnlyList<string>? existingDefault)
                    ? InputSettings.NormalizeBrowserKeyGestures(existingDefault)
                    : new List<string>());
            existingEffective = existingEffective
                .Where(k => !string.Equals(k, gesture, StringComparison.OrdinalIgnoreCase))
                .ToList();
            List<string> existingDefaultsList = defaults.TryGetValue(existingCommandId, out IReadOnlyList<string>? defaultsForExisting)
                ? InputSettings.NormalizeBrowserKeyGestures(defaultsForExisting)
                : new List<string>();
            if (existingEffective.SequenceEqual(existingDefaultsList, StringComparer.OrdinalIgnoreCase))
            {
                overrides.Remove(existingCommandId);
            }
            else
            {
                overrides[existingCommandId] = existingEffective;
            }
        }

        List<string> targetEffective = overrides.TryGetValue(targetCommandId, out List<string>? targetOverride)
            ? InputSettings.NormalizeBrowserKeyGestures(targetOverride)
            : (defaults.TryGetValue(targetCommandId, out IReadOnlyList<string>? defaultForTarget)
                ? InputSettings.NormalizeBrowserKeyGestures(defaultForTarget)
                : new List<string>());
        if (replaceExistingForTarget)
        {
            targetEffective.Clear();
        }
        if (!targetEffective.Contains(gesture, StringComparer.OrdinalIgnoreCase))
        {
            targetEffective.Add(gesture);
        }

        List<string> targetDefaultsList = defaults.TryGetValue(targetCommandId, out IReadOnlyList<string>? defaultsForTarget)
            ? InputSettings.NormalizeBrowserKeyGestures(defaultsForTarget)
            : new List<string>();
        List<string> normalizedTarget = InputSettings.NormalizeBrowserKeyGestures(targetEffective);
        if (normalizedTarget.SequenceEqual(targetDefaultsList, StringComparer.OrdinalIgnoreCase))
        {
            overrides.Remove(targetCommandId);
        }
        else
        {
            overrides[targetCommandId] = normalizedTarget;
        }

        _settingsDraft.BrowserKeyCommandOverrides = InputSettings.NormalizeBrowserKeyCommandOverrides(overrides);
        return true;
    }

    private bool ClearShortcutGesture(string gesture, string confirmTitle)
    {
        if (InputSettings.IsBrowserStructuralReservedGesture(gesture))
        {
            MessageBox.Show(this, "このキーはBrowserの表示/列操作に予約されています。", "予約キー", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        var overrides = InputSettings.NormalizeBrowserKeyCommandOverrides(_settingsDraft.BrowserKeyCommandOverrides);
        IReadOnlyDictionary<string, IReadOnlyList<string>> defaults = InputSettings.GetDefaultBrowserKeyCommandMap(ResolveProfileValue());
        var effective = BrowserCommandBindingResolver.ResolveEffectiveKeyCommandMap(
            ResolveProfileValue(),
            overrides,
            _registry);
        if (!effective.TryGetValue(gesture, out string? existingCommandId) || string.IsNullOrWhiteSpace(existingCommandId))
        {
            return false;
        }
        if (!IsEditableCommand(existingCommandId))
        {
            MessageBox.Show(this, "このキーは安全性のため変更できない機能に割り当てられています。", "編集不可", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        string existingName = _commandById.TryGetValue(existingCommandId, out CommandDefinition? existing)
            ? existing.DisplayName
            : existingCommandId;
        string existingDescription = _commandById.TryGetValue(existingCommandId, out CommandDefinition? existingDef)
            ? existingDef.Description
            : string.Empty;
        using var conflict = new AssignmentConflictDialog(
            confirmTitle,
            $"入力 '{gesture}' の割り当てを解除します。",
            existingName,
            existingDescription,
            showOverwrite: false,
            overwriteText: "解除する");
        if (conflict.ShowDialog(this) != DialogResult.Yes)
        {
            return false;
        }

        List<string> existingEffective = overrides.TryGetValue(existingCommandId, out List<string>? existingOverride)
            ? InputSettings.NormalizeBrowserKeyGestures(existingOverride)
            : (defaults.TryGetValue(existingCommandId, out IReadOnlyList<string>? existingDefault)
                ? InputSettings.NormalizeBrowserKeyGestures(existingDefault)
                : new List<string>());
        existingEffective = existingEffective
            .Where(k => !string.Equals(k, gesture, StringComparison.OrdinalIgnoreCase))
            .ToList();
        List<string> existingDefaultsList = defaults.TryGetValue(existingCommandId, out IReadOnlyList<string>? defaultsForExisting)
            ? InputSettings.NormalizeBrowserKeyGestures(defaultsForExisting)
            : new List<string>();
        if (existingEffective.SequenceEqual(existingDefaultsList, StringComparer.OrdinalIgnoreCase))
        {
            overrides.Remove(existingCommandId);
        }
        else
        {
            overrides[existingCommandId] = existingEffective;
        }

        _settingsDraft.BrowserKeyCommandOverrides = InputSettings.NormalizeBrowserKeyCommandOverrides(overrides);
        return true;
    }

    private string ChooseShortcutInsertMode(string commandDisplayName, IReadOnlyList<string> currentKeys, string capturedGesture)
    {
        using var dialog = new ShortcutInsertModeDialog(commandDisplayName, currentKeys, capturedGesture);
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedMode : "cancel";
    }

    private bool TryDeleteShortcutGesture(string commandDisplayName, IReadOnlyList<string> currentKeys, out List<string> remaining)
    {
        remaining = new List<string>(currentKeys);
        if (currentKeys.Count == 0)
        {
            remaining.Clear();
            return true;
        }

        using var dialog = new ShortcutDeleteDialog(commandDisplayName, currentKeys);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return false;
        }

        if (dialog.DeleteAll)
        {
            remaining.Clear();
            return true;
        }

        if (!string.IsNullOrWhiteSpace(dialog.SelectedGesture))
        {
            remaining = currentKeys
                .Where(k => !string.Equals(k, dialog.SelectedGesture, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return true;
    }

    private void OpenFunctionEditor(string commandId)
    {
        if (!_commandById.TryGetValue(commandId, out CommandDefinition? command))
        {
            return;
        }

        bool isFdCompatible = string.Equals(ResolveProfileValue(), InputSettings.FdCompatibleProfileValue, StringComparison.OrdinalIgnoreCase);
        Dictionary<string, string?> normal = GetOverrideMap(FunctionLayer.Normal, isFdCompatible);
        Dictionary<string, string?> shift = GetOverrideMap(FunctionLayer.Shift, isFdCompatible);
        Dictionary<string, string?> ctrl = GetOverrideMap(FunctionLayer.Ctrl, isFdCompatible);
        Dictionary<string, string?> alt = GetOverrideMap(FunctionLayer.Alt, isFdCompatible);
        List<string> currentAssignments = GetFunctionSlotsForCommand(commandId).Where(static x => x != "(なし)").ToList();
        string currentAssignmentsText = string.Join(", ", currentAssignments);
        if (string.IsNullOrWhiteSpace(currentAssignmentsText))
        {
            currentAssignmentsText = "なし";
        }

        using var dialog = new FunctionKeyCaptureDialog(command.DisplayName, currentAssignmentsText);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        if (dialog.IsDeleted)
        {
            foreach (Dictionary<string, string?> map in new[] { normal, shift, ctrl, alt })
            {
                foreach (string key in map.Where(x => string.Equals(x.Value, commandId, StringComparison.OrdinalIgnoreCase)).Select(x => x.Key).ToArray())
                {
                    map.Remove(key);
                }
            }
        }
        else
        {
            string slotKey = $"F{(dialog.CapturedFKey & Keys.KeyCode) - Keys.F1 + 1}";
            Keys modifier = dialog.CapturedFKey & Keys.Modifiers;
            FunctionLayer selectedLayer = modifier switch
            {
                Keys.Shift => FunctionLayer.Shift,
                Keys.Control => FunctionLayer.Ctrl,
                Keys.Alt => FunctionLayer.Alt,
                _ => FunctionLayer.Normal
            };
            Dictionary<string, string?> selectedMap = selectedLayer switch
            {
                FunctionLayer.Shift => shift,
                FunctionLayer.Ctrl => ctrl,
                FunctionLayer.Alt => alt,
                _ => normal
            };
            string? existingOnSlot = selectedMap.TryGetValue(slotKey, out string? currentLayerValue)
                ? currentLayerValue
                : ResolveFunctionCommandId(
                    isFdCompatible ? FunctionKeyProfile.FDCompatible : FunctionKeyProfile.Standard,
                    (dialog.CapturedFKey & Keys.KeyCode) - Keys.F1 + 1,
                    selectedLayer);
            if (!string.IsNullOrWhiteSpace(existingOnSlot) &&
                !string.Equals(existingOnSlot, commandId, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(existingOnSlot, "none", StringComparison.OrdinalIgnoreCase))
            {
                string existingName = _commandById.TryGetValue(existingOnSlot, out CommandDefinition? existing)
                    ? existing.DisplayName
                    : existingOnSlot;
                string existingDescription = _commandById.TryGetValue(existingOnSlot, out CommandDefinition? existingDef)
                    ? existingDef.Description
                    : string.Empty;
                string conflictKey = selectedLayer switch
                {
                    FunctionLayer.Shift => $"Shift+{slotKey}",
                    FunctionLayer.Ctrl => $"Ctrl+{slotKey}",
                    FunctionLayer.Alt => $"Alt+{slotKey}",
                    _ => slotKey
                };
                using var conflict = new AssignmentConflictDialog(
                    "キー競合",
                    $"キー '{conflictKey}' は既に他機能へ割り当て済みです。",
                    existingName,
                    existingDescription);
                if (conflict.ShowDialog(this) != DialogResult.Yes)
                {
                    return;
                }
            }
            string mode = "add";
            bool commandAlreadyAssigned = currentAssignments.Any();
            string newSlotDisplay = selectedLayer switch
            {
                FunctionLayer.Shift => $"Shift+{slotKey}",
                FunctionLayer.Ctrl => $"Ctrl+{slotKey}",
                FunctionLayer.Alt => $"Alt+{slotKey}",
                _ => slotKey
            };
            bool sameSlotAlreadyAssignedToTarget = selectedMap.Any(x =>
                string.Equals(x.Value, commandId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Key, slotKey, StringComparison.OrdinalIgnoreCase));
            if (commandAlreadyAssigned && !sameSlotAlreadyAssignedToTarget)
            {
                using var modeDialog = new FunctionAssignModeDialog(command.DisplayName, currentAssignments, newSlotDisplay);
                if (modeDialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }
                mode = modeDialog.SelectedMode;
            }

            if (mode == "replace")
            {
                foreach (Dictionary<string, string?> map in new[] { normal, shift, ctrl, alt })
                {
                    foreach (string key in map.Where(x => string.Equals(x.Value, commandId, StringComparison.OrdinalIgnoreCase)).Select(x => x.Key).ToArray())
                    {
                        map.Remove(key);
                    }
                }
            }

            selectedMap[slotKey] = commandId;
        }

        SetOverrideMap(FunctionLayer.Normal, isFdCompatible, normal);
        SetOverrideMap(FunctionLayer.Shift, isFdCompatible, shift);
        SetOverrideMap(FunctionLayer.Ctrl, isFdCompatible, ctrl);
        SetOverrideMap(FunctionLayer.Alt, isFdCompatible, alt);
        RefreshAllViews();
    }

    private void OpenGestureEditor(string commandId)
    {
        if (!IsMouseGestureAssignableCommand(commandId))
        {
            MessageBox.Show(this, "この機能は誤操作防止のためマウスジェスチャーには割り当てできません。", "割り当て不可", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Dictionary<string, string> map = InputSettings.NormalizeMouseGestureCommandMap(_settingsDraft.MouseGestureCommandMap);
        string commandName = _commandById.TryGetValue(commandId, out CommandDefinition? command)
            ? command.DisplayName
            : commandId;
        using var dialog = new GestureCaptureOverlay(commandName);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        if (dialog.IsDeleted)
        {
            foreach (string gesture in map.Where(x => string.Equals(x.Value, commandId, StringComparison.OrdinalIgnoreCase)).Select(x => x.Key).ToArray())
            {
                map[gesture] = InputSettings.MouseGestureUnassignedCommandId;
            }
            _settingsDraft.MouseGestureCommandMap = InputSettings.NormalizeMouseGestureCommandMap(map);
            RefreshAllViews();
            return;
        }

        if (!string.IsNullOrWhiteSpace(dialog.ResultGesture))
        {
            if (map.TryGetValue(dialog.ResultGesture, out string? existingCommandId) &&
                !string.Equals(existingCommandId, commandId, StringComparison.OrdinalIgnoreCase))
            {
                string existingName = _commandById.TryGetValue(existingCommandId, out CommandDefinition? existing)
                    ? existing.DisplayName
                    : existingCommandId;
                string existingDescription = _commandById.TryGetValue(existingCommandId, out CommandDefinition? existingDef)
                    ? existingDef.Description
                    : string.Empty;
                using var conflict = new AssignmentConflictDialog(
                    "ジェスチャー競合",
                    $"ジェスチャー '{dialog.ResultGesture}' は既に他機能へ割り当て済みです。",
                    existingName,
                    existingDescription);
                if (conflict.ShowDialog(this) != DialogResult.Yes)
                {
                    return;
                }
            }
            map[dialog.ResultGesture] = commandId;
        }
        _settingsDraft.MouseGestureCommandMap = InputSettings.NormalizeMouseGestureCommandMap(map);
        RefreshAllViews();
    }

    private Dictionary<string, string?> GetOverrideMap(FunctionLayer layer, bool isFdCompatible)
    {
        Dictionary<string, string?> source = layer switch
        {
            FunctionLayer.Shift => isFdCompatible ? _settingsDraft.FunctionBarCommandOverridesShiftFdCompatible : _settingsDraft.FunctionBarCommandOverridesShiftStandard,
            FunctionLayer.Ctrl => isFdCompatible ? _settingsDraft.FunctionBarCommandOverridesCtrlFdCompatible : _settingsDraft.FunctionBarCommandOverridesCtrlStandard,
            FunctionLayer.Alt => isFdCompatible ? _settingsDraft.FunctionBarCommandOverridesAltFdCompatible : _settingsDraft.FunctionBarCommandOverridesAltStandard,
            _ => isFdCompatible ? _settingsDraft.FunctionBarCommandOverridesFdCompatible : _settingsDraft.FunctionBarCommandOverridesStandard
        };
        return new Dictionary<string, string?>(source ?? new Dictionary<string, string?>(), StringComparer.OrdinalIgnoreCase);
    }

    private bool IsEditableCommand(string commandId)
    {
        if (!_commandById.TryGetValue(commandId, out CommandDefinition? command))
        {
            return false;
        }

        return command.IsCustomizable;
    }

    private bool IsMouseGestureAssignableCommand(string commandId)
    {
        return _commandById.TryGetValue(commandId, out CommandDefinition? command)
               && command.IsCustomizable
               && !command.IsDangerous;
    }

    private void SetOverrideMap(FunctionLayer layer, bool isFdCompatible, Dictionary<string, string?> map)
    {
        if (layer == FunctionLayer.Shift)
        {
            if (isFdCompatible)
            {
                _settingsDraft.FunctionBarCommandOverridesShiftFdCompatible = map;
            }
            else
            {
                _settingsDraft.FunctionBarCommandOverridesShiftStandard = map;
            }
        }
        else if (layer == FunctionLayer.Ctrl)
        {
            if (isFdCompatible)
            {
                _settingsDraft.FunctionBarCommandOverridesCtrlFdCompatible = map;
            }
            else
            {
                _settingsDraft.FunctionBarCommandOverridesCtrlStandard = map;
            }
        }
        else if (layer == FunctionLayer.Alt)
        {
            if (isFdCompatible)
            {
                _settingsDraft.FunctionBarCommandOverridesAltFdCompatible = map;
            }
            else
            {
                _settingsDraft.FunctionBarCommandOverridesAltStandard = map;
            }
        }
        else
        {
            if (isFdCompatible)
            {
                _settingsDraft.FunctionBarCommandOverridesFdCompatible = map;
            }
            else
            {
                _settingsDraft.FunctionBarCommandOverridesStandard = map;
            }
        }
    }

    private void FunctionGrid_CellClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex >= 0 && _functionGrid.Rows[e.RowIndex].Tag is ValueTuple<int, FunctionLayer, bool, bool> rowTag && rowTag.Item4)
        {
            return;
        }
        if (_refreshing || e.RowIndex < 0 || _openingFunctionDropdown)
        {
            return;
        }
        if (e.ColumnIndex == 2)
        {
            _openingFunctionDropdown = true;
            _functionGrid.CurrentCell = _functionGrid.Rows[e.RowIndex].Cells[e.ColumnIndex];
            _functionGrid.BeginEdit(true);
            if (_functionGrid.EditingControl is ComboBox combo)
            {
                combo.DroppedDown = true;
            }
            _openingFunctionDropdown = false;
        }
    }

    private void FunctionGrid_CellDoubleClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (_refreshing || e.RowIndex < 0)
        {
            return;
        }

        if (_functionGrid.Rows[e.RowIndex].Tag is ValueTuple<int, FunctionLayer, bool, bool> rowTag && rowTag.Item4)
        {
            return;
        }

        if (e.ColumnIndex == 1)
        {
            if (CanEditFunctionGridLabel(e.RowIndex))
            {
                _functionGrid.CurrentCell = _functionGrid.Rows[e.RowIndex].Cells[e.ColumnIndex];
                _functionGrid.BeginEdit(true);
            }
        }
        else if (e.ColumnIndex == 3)
        {
            string? commandId = GetFunctionGridActiveCommandIdForRow(e.RowIndex);
            if (!string.IsNullOrWhiteSpace(commandId) &&
                !string.Equals(commandId, InputSettings.MouseGestureUnassignedCommandId, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(commandId, ReservedFunctionSlotCommandId, StringComparison.OrdinalIgnoreCase))
            {
                OpenShortcutEditor(commandId);
            }
        }
    }

    private void FunctionGrid_CurrentCellDirtyStateChanged(object? sender, EventArgs e)
    {
        if (_functionGrid.IsCurrentCellDirty && _functionGrid.CurrentCell?.ColumnIndex == 2)
        {
            _functionGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        }
    }

    private void FunctionGrid_CellBeginEdit(object? sender, DataGridViewCellCancelEventArgs e)
    {
        if (_refreshing || e.RowIndex < 0 || e.ColumnIndex != 1)
        {
            return;
        }

        if (!CanEditFunctionGridLabel(e.RowIndex))
        {
            e.Cancel = true;
            ClearFunctionLabelEditingState();
            return;
        }

        BeginFunctionLabelEditingState(e.RowIndex, e.ColumnIndex);
    }

    private int _functionLabelEditingRowIndex = -1;
    private int _functionLabelEditingColumnIndex = -1;
    private string _functionLabelEditingOriginalText = string.Empty;
    private int _functionLabelEditingOriginalSelectionStart = 0;
    private bool _functionLabelEditingInitializing = false;
    private bool _functionLabelEditingSuppressTextChanged = false;

    private void BeginFunctionLabelEditingState(int rowIndex, int columnIndex)
    {
        _functionLabelEditingRowIndex = rowIndex;
        _functionLabelEditingColumnIndex = columnIndex;
        _functionLabelEditingOriginalText = _functionGrid.Rows[rowIndex].Cells[columnIndex].Value?.ToString() ?? string.Empty;
        _functionLabelEditingOriginalSelectionStart = _functionLabelEditingOriginalText.Length;
        _functionLabelEditingInitializing = true;
        _functionLabelEditingSuppressTextChanged = false;
    }

    private void ClearFunctionLabelEditingState()
    {
        _functionLabelEditingRowIndex = -1;
        _functionLabelEditingColumnIndex = -1;
        _functionLabelEditingOriginalText = string.Empty;
        _functionLabelEditingOriginalSelectionStart = 0;
        _functionLabelEditingInitializing = false;
        _functionLabelEditingSuppressTextChanged = false;
    }

    private void FunctionGrid_EditingControlShowing(object? sender, DataGridViewEditingControlShowingEventArgs e)
    {
        if (_functionGrid.CurrentCell?.ColumnIndex != 1 || e.Control is not TextBox textBox)
        {
            return;
        }

        textBox.MaxLength = FunctionBarLabelTextBoxMaxLength;
        textBox.BorderStyle = BorderStyle.FixedSingle;

        textBox.KeyPress -= FunctionGridLabelTextBox_KeyPress;
        textBox.TextChanged -= FunctionGridLabelTextBox_TextChanged;

        if (_functionGrid.CurrentCell.RowIndex == _functionLabelEditingRowIndex &&
            _functionGrid.CurrentCell.ColumnIndex == _functionLabelEditingColumnIndex)
        {
            _functionLabelEditingSuppressTextChanged = true;
            textBox.Text = _functionLabelEditingOriginalText;
            textBox.SelectionStart = Math.Min(_functionLabelEditingOriginalSelectionStart, textBox.Text.Length);
            textBox.SelectionLength = 0;
            _functionLabelEditingSuppressTextChanged = false;
        }

        textBox.KeyPress += FunctionGridLabelTextBox_KeyPress;
        textBox.TextChanged += FunctionGridLabelTextBox_TextChanged;
        _functionLabelEditingInitializing = false;
    }

    private void FunctionGridLabelTextBox_KeyPress(object? sender, KeyPressEventArgs e)
    {
        if (sender is not TextBox tb)
        {
            return;
        }

        if (char.IsControl(e.KeyChar))
        {
            return;
        }

        if (e.KeyChar == '\r' || e.KeyChar == '\n' || e.KeyChar == '\t' || e.KeyChar == ':' || e.KeyChar == '：')
        {
            e.Handled = true;
            return;
        }

        string currentText = tb.Text;
        int selStart = tb.SelectionStart;
        int selLength = tb.SelectionLength;

        string newText = currentText.Remove(selStart, selLength).Insert(selStart, e.KeyChar.ToString());
        if (!ValidateFunctionBarLabel(newText, out _))
        {
            e.Handled = true;
        }
    }

    private void FunctionGridLabelTextBox_TextChanged(object? sender, EventArgs e)
    {
        if (_functionLabelEditingSuppressTextChanged || _functionLabelEditingInitializing)
        {
            return;
        }

        if (sender is not TextBox tb ||
            _functionGrid.CurrentCell == null ||
            _functionGrid.CurrentCell.RowIndex != _functionLabelEditingRowIndex ||
            _functionGrid.CurrentCell.ColumnIndex != _functionLabelEditingColumnIndex)
        {
            return;
        }

        string text = tb.Text;
        bool invalid = false;

        if (text.Contains("\n") || text.Contains("\r") || text.Contains("\t") || text.Contains(":") || text.Contains("："))
        {
            invalid = true;
        }

        if (!ValidateFunctionBarLabel(text, out _))
        {
            invalid = true;
        }

        if (invalid)
        {
            _functionLabelEditingSuppressTextChanged = true;
            tb.Text = _functionLabelEditingOriginalText;
            tb.SelectionStart = Math.Min(_functionLabelEditingOriginalSelectionStart, tb.Text.Length);
            tb.SelectionLength = 0;
            _functionLabelEditingSuppressTextChanged = false;
        }
    }

    private void FunctionGrid_CellValidating(object? sender, DataGridViewCellValidatingEventArgs e)
    {
        if (_refreshing || e.RowIndex < 0 || e.ColumnIndex != 1)
        {
            return;
        }

        string input = e.FormattedValue?.ToString() ?? string.Empty;
        DataGridViewCell cell = _functionGrid.Rows[e.RowIndex].Cells[e.ColumnIndex];
        cell.ErrorText = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            return;
        }

        if (string.Equals(input, _functionLabelEditingOriginalText, StringComparison.Ordinal))
        {
            return;
        }

        if (CanEditFunctionGridLabel(e.RowIndex) && !ValidateFunctionBarLabel(input, out string errorMessage))
        {
            cell.ErrorText = errorMessage;
        }
    }

    private void FunctionGrid_CellEndEdit(object? sender, DataGridViewCellEventArgs e)
    {
        if (_refreshing || e.RowIndex < 0 || e.ColumnIndex != 1)
        {
            return;
        }

        _functionGrid.Rows[e.RowIndex].Cells[e.ColumnIndex].ErrorText = string.Empty;
        CommitFunctionGridLabelEdit(e.RowIndex);
        ClearFunctionLabelEditingState();
    }

    private void FunctionGrid_CellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_refreshing || e.RowIndex < 0)
        {
            return;
        }
        if (_functionGrid.Rows[e.RowIndex].Tag is ValueTuple<int, FunctionLayer, bool, bool> rowTag && rowTag.Item4)
        {
            return;
        }

        FunctionLayer layer = GetSelectedFunctionLayer();
        bool isFdCompatible = string.Equals(ResolveProfileValue(), InputSettings.FdCompatibleProfileValue, StringComparison.OrdinalIgnoreCase);
        if (e.ColumnIndex != 2)
        {
            return;
        }

        int slot = e.RowIndex + 1;
        string? selected = _functionGrid.Rows[e.RowIndex].Cells[2].Value?.ToString();
        if (string.IsNullOrWhiteSpace(selected))
        {
            return;
        }

        string slotKey = $"F{slot}";
        Dictionary<string, string?> map = GetOverrideMap(layer, isFdCompatible);

        if (string.Equals(selected, "__default__", StringComparison.OrdinalIgnoreCase))
        {
            map.Remove(slotKey);
        }
        else if (string.Equals(selected, ReservedFunctionSlotCommandId, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "予約済み値は Alt+F4 以外に設定できません。", "入力不可", MessageBoxButtons.OK, MessageBoxIcon.Information);
            RefreshFunctionGrid();
            return;
        }
        else if (string.Equals(selected, InputSettings.MouseGestureUnassignedCommandId, StringComparison.OrdinalIgnoreCase))
        {
            map[slotKey] = "none";
        }
        else
        {
            map[slotKey] = selected;
        }

        SetOverrideMap(layer, isFdCompatible, map);
        RefreshAllViews();
    }

    private void FunctionGrid_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Delete || _functionGrid.CurrentCell == null || _functionGrid.CurrentRow == null)
        {
            if ((e.KeyCode != Keys.Enter && e.KeyCode != Keys.F2) || _functionGrid.CurrentCell == null || _functionGrid.CurrentRow == null)
            {
                return;
            }
        }
        if (_functionGrid.CurrentRow.Tag is ValueTuple<int, FunctionLayer, bool, bool> rowTag && rowTag.Item4)
        {
            return;
        }

        int slotNumber = _functionGrid.CurrentRow.Index + 1;
        int columnIndex = _functionGrid.CurrentCell.ColumnIndex;

        if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.F2)
        {
            if (columnIndex == 1)
            {
                if (!_functionGrid.IsCurrentCellInEditMode && CanEditFunctionGridLabel(_functionGrid.CurrentRow.Index))
                {
                    _functionGrid.BeginEdit(true);
                }
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            if (columnIndex == 3)
            {
                string? commandId = GetFunctionGridActiveCommandIdForRow(_functionGrid.CurrentRow.Index);
                if (!string.IsNullOrWhiteSpace(commandId) &&
                    !string.Equals(commandId, InputSettings.MouseGestureUnassignedCommandId, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(commandId, ReservedFunctionSlotCommandId, StringComparison.OrdinalIgnoreCase))
                {
                    OpenShortcutEditor(commandId);
                }
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
        }

        if (e.KeyCode != Keys.Delete)
        {
            return;
        }

        if (columnIndex == 1)
        {
            FunctionLayer layer = GetSelectedFunctionLayer();
            if (layer == FunctionLayer.Ctrl || layer == FunctionLayer.Alt)
            {
                return;
            }

            bool isFdCompatible = string.Equals(ResolveProfileValue(), InputSettings.FdCompatibleProfileValue, StringComparison.OrdinalIgnoreCase);
            Dictionary<string, FunctionBarLabelOverride> labelOverrides = GetFunctionBarLabelOverrideMap(layer, isFdCompatible);
            labelOverrides.Remove($"F{slotNumber}");
            SetFunctionBarLabelOverrideMap(layer, isFdCompatible, labelOverrides);
            RefreshFunctionGrid();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (_functionGrid.CurrentCell.ColumnIndex != 2)
        {
            return;
        }

        string commandSlotKey = $"F{slotNumber}";
        FunctionLayer commandLayer = GetSelectedFunctionLayer();
        string targetLabel = commandLayer switch
        {
            FunctionLayer.Shift => $"Shift+{commandSlotKey}",
            FunctionLayer.Ctrl => $"Ctrl+{commandSlotKey}",
            FunctionLayer.Alt => $"Alt+{commandSlotKey}",
            _ => commandSlotKey
        };
        if (MessageBox.Show(this, $"{targetLabel} の割り当てを解除しますか？", "確認", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }

        bool commandIsFdCompatible = string.Equals(ResolveProfileValue(), InputSettings.FdCompatibleProfileValue, StringComparison.OrdinalIgnoreCase);
        Dictionary<string, string?> map = GetOverrideMap(commandLayer, commandIsFdCompatible);
        map[commandSlotKey] = "none";
        SetOverrideMap(commandLayer, commandIsFdCompatible, map);
        RefreshAllViews();
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private void GestureGrid_CellClick(object? sender, DataGridViewCellEventArgs e)
    {
        if (_refreshing || e.RowIndex < 0 || e.ColumnIndex != 1 || _openingGestureDropdown)
        {
            return;
        }
        _openingGestureDropdown = true;
        _gestureGrid.CurrentCell = _gestureGrid.Rows[e.RowIndex].Cells[e.ColumnIndex];
        _gestureGrid.BeginEdit(true);
        if (_gestureGrid.EditingControl is ComboBox combo)
        {
            combo.DroppedDown = true;
        }
        _openingGestureDropdown = false;
    }

    private void GestureGrid_CurrentCellDirtyStateChanged(object? sender, EventArgs e)
    {
        if (_gestureGrid.IsCurrentCellDirty)
        {
            _gestureGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        }
    }

    private void GestureGrid_CellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_refreshing || e.RowIndex < 0 || e.ColumnIndex != 1)
        {
            return;
        }

        if (_gestureGrid.Rows[e.RowIndex].Tag is not ValueTuple<string, string> rowTag)
        {
            return;
        }

        string gesture = rowTag.Item1;
        string? selected = _gestureGrid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value?.ToString();
        if (string.IsNullOrWhiteSpace(selected))
        {
            return;
        }
        if (!string.Equals(selected, InputSettings.MouseGestureUnassignedCommandId, StringComparison.OrdinalIgnoreCase))
        {
            CommandDefinition? definition = _registry.Find(selected);
            if (definition == null || !definition.IsCustomizable || definition.IsDangerous)
            {
                MessageBox.Show(this, "この機能はマウスジェスチャーには割り当てできません。", "割り当て不可", MessageBoxButtons.OK, MessageBoxIcon.Information);
                RefreshGestureGrid();
                return;
            }
        }

        Dictionary<string, string> map = InputSettings.NormalizeMouseGestureCommandMap(_settingsDraft.MouseGestureCommandMap);
        map[gesture] = string.Equals(selected, InputSettings.MouseGestureUnassignedCommandId, StringComparison.OrdinalIgnoreCase)
            ? InputSettings.MouseGestureUnassignedCommandId
            : selected;
        _settingsDraft.MouseGestureCommandMap = InputSettings.NormalizeMouseGestureCommandMap(map);
        RefreshAllViews();
    }

    private void GestureGrid_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Delete || _gestureGrid.CurrentRow == null)
        {
            return;
        }

        if (_gestureGrid.CurrentRow.Tag is not ValueTuple<string, string> rowTag)
        {
            return;
        }

        string gesture = rowTag.Item1;
        string gestureLabel = ToGestureDirectionLabel(gesture);
        if (MessageBox.Show(this, $"{gestureLabel} の割り当てを解除しますか？", "確認", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }

        Dictionary<string, string> map = InputSettings.NormalizeMouseGestureCommandMap(_settingsDraft.MouseGestureCommandMap);
        map[gesture] = InputSettings.MouseGestureUnassignedCommandId;
        _settingsDraft.MouseGestureCommandMap = InputSettings.NormalizeMouseGestureCommandMap(map);
        RefreshAllViews();
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private CommandDefinition? SelectCommandForAssignment()
    {
        using var dialog = new Form
        {
            Text = "機能名選択",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false,
            MaximizeBox = false,
            ClientSize = new Size(520, 460)
        };
        var list = new ListBox { Dock = DockStyle.Top, Height = 380 };
        var commands = GetAssignableCommands().OrderBy(static x => x.DisplayName, StringComparer.Ordinal).ToArray();
        foreach (CommandDefinition command in commands)
        {
            list.Items.Add(FunctionKeyProfileService.ResolveCommandDisplayText(command));
        }

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 340, Top = 420, Width = 80 };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, Left = 428, Top = 420, Width = 80 };
        dialog.Controls.Add(list);
        dialog.Controls.Add(ok);
        dialog.Controls.Add(cancel);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;

        if (dialog.ShowDialog(this) != DialogResult.OK || list.SelectedIndex < 0)
        {
            return null;
        }
        return commands[list.SelectedIndex];
    }

    private void ResetSelectedAssignment()
    {
        if (_tabs.SelectedIndex == 0)
        {
            ResetSelectedFeature();
            return;
        }

        if (_tabs.SelectedIndex == 1)
        {
            ResetSelectedFunctionSlot();
            return;
        }

        ResetSelectedGesture();
    }

    private void ResetSelectedFeature()
    {
        if (_featureGrid.CurrentRow?.Tag is not string commandId)
        {
            return;
        }
        if (!IsEditableCommand(commandId))
        {
            MessageBox.Show(this, "この機能は安全性のため入力割り当てを変更できません。", "編集不可", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _settingsDraft.BrowserKeyCommandOverrides.Remove(commandId);
        ResetFunctionAssignmentForCommand(commandId);
        ResetFunctionLabelOverridesForCommand(commandId);
        ResetGestureAssignmentForCommand(commandId);
        RefreshAllViews();
    }

    private void ResetSelectedFunctionSlot()
    {
        if (_functionGrid.CurrentRow == null)
        {
            return;
        }
        if (_functionGrid.CurrentRow.Tag is not ValueTuple<int, FunctionLayer, bool, bool> rowTag || rowTag.Item4)
        {
            return;
        }

        FunctionLayer layer = rowTag.Item2;
        bool isFdCompatible = string.Equals(ResolveProfileValue(), InputSettings.FdCompatibleProfileValue, StringComparison.OrdinalIgnoreCase);
        int slot = rowTag.Item1;
        string slotKey = $"F{slot}";
        Dictionary<string, string?> commandMap = GetOverrideMap(layer, isFdCompatible);
        commandMap.Remove(slotKey);
        SetOverrideMap(layer, isFdCompatible, commandMap);
        Dictionary<string, FunctionBarLabelOverride> labelMap = GetFunctionBarLabelOverrideMap(layer, isFdCompatible);
        labelMap.Remove(slotKey);
        SetFunctionBarLabelOverrideMap(layer, isFdCompatible, labelMap);
        RefreshAllViews();
    }

    private void ResetSelectedGesture()
    {
        if (_gestureGrid.CurrentRow?.Tag is not ValueTuple<string, string> rowTag)
        {
            return;
        }

        string gesture = rowTag.Item1;
        Dictionary<string, string> map = InputSettings.NormalizeMouseGestureCommandMap(_settingsDraft.MouseGestureCommandMap);
        map[gesture] = InputSettings.DefaultMouseGestureCommandMap.TryGetValue(gesture, out string? defaultCommandId)
            ? defaultCommandId
            : InputSettings.MouseGestureUnassignedCommandId;
        _settingsDraft.MouseGestureCommandMap = InputSettings.NormalizeMouseGestureCommandMap(map);
        RefreshAllViews();
    }

    private void ResetAllAssignments()
    {
        if (MessageBox.Show(this, "現在のタブに対応する割り当てを既定に戻しますか？", "確認", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
        {
            return;
        }

        if (_tabs.SelectedIndex == 1)
        {
            _settingsDraft.FunctionBarCommandOverridesStandard.Clear();
            _settingsDraft.FunctionBarCommandOverridesFdCompatible.Clear();
            _settingsDraft.FunctionBarCommandOverridesShiftStandard.Clear();
            _settingsDraft.FunctionBarCommandOverridesShiftFdCompatible.Clear();
            _settingsDraft.FunctionBarCommandOverridesCtrlStandard.Clear();
            _settingsDraft.FunctionBarCommandOverridesCtrlFdCompatible.Clear();
            _settingsDraft.FunctionBarCommandOverridesAltStandard.Clear();
            _settingsDraft.FunctionBarCommandOverridesAltFdCompatible.Clear();
            _settingsDraft.FunctionBarLabelOverridesStandard.Clear();
            _settingsDraft.FunctionBarLabelOverridesFdCompatible.Clear();
            _settingsDraft.FunctionBarLabelOverridesShiftStandard.Clear();
            _settingsDraft.FunctionBarLabelOverridesShiftFdCompatible.Clear();
            _settingsDraft.FunctionBarLabelOverridesCtrlStandard.Clear();
            _settingsDraft.FunctionBarLabelOverridesCtrlFdCompatible.Clear();
            _settingsDraft.FunctionBarLabelOverridesAltStandard.Clear();
            _settingsDraft.FunctionBarLabelOverridesAltFdCompatible.Clear();
            RefreshAllViews();
            return;
        }

        if (_tabs.SelectedIndex == 2)
        {
            _settingsDraft.MouseGestureCommandMap = new Dictionary<string, string>(InputSettings.DefaultMouseGestureCommandMap, StringComparer.OrdinalIgnoreCase);
            RefreshAllViews();
            return;
        }

        _settingsDraft.BrowserKeyCommandOverrides.Clear();
        _settingsDraft.FunctionBarCommandOverridesStandard.Clear();
        _settingsDraft.FunctionBarCommandOverridesFdCompatible.Clear();
        _settingsDraft.FunctionBarCommandOverridesShiftStandard.Clear();
        _settingsDraft.FunctionBarCommandOverridesShiftFdCompatible.Clear();
        _settingsDraft.FunctionBarCommandOverridesCtrlStandard.Clear();
        _settingsDraft.FunctionBarCommandOverridesCtrlFdCompatible.Clear();
        _settingsDraft.FunctionBarCommandOverridesAltStandard.Clear();
        _settingsDraft.FunctionBarCommandOverridesAltFdCompatible.Clear();
        _settingsDraft.FunctionBarLabelOverridesStandard.Clear();
        _settingsDraft.FunctionBarLabelOverridesFdCompatible.Clear();
        _settingsDraft.FunctionBarLabelOverridesShiftStandard.Clear();
        _settingsDraft.FunctionBarLabelOverridesShiftFdCompatible.Clear();
        _settingsDraft.FunctionBarLabelOverridesCtrlStandard.Clear();
        _settingsDraft.FunctionBarLabelOverridesCtrlFdCompatible.Clear();
        _settingsDraft.FunctionBarLabelOverridesAltStandard.Clear();
        _settingsDraft.FunctionBarLabelOverridesAltFdCompatible.Clear();
        _settingsDraft.MouseGestureCommandMap = new Dictionary<string, string>(InputSettings.DefaultMouseGestureCommandMap, StringComparer.OrdinalIgnoreCase);
        RefreshAllViews();
    }

    private void ResetFunctionAssignmentForCommand(string commandId)
    {
        foreach (Dictionary<string, string?> map in new[]
                 {
                     _settingsDraft.FunctionBarCommandOverridesStandard,
                     _settingsDraft.FunctionBarCommandOverridesFdCompatible,
                     _settingsDraft.FunctionBarCommandOverridesShiftStandard,
                     _settingsDraft.FunctionBarCommandOverridesShiftFdCompatible,
                     _settingsDraft.FunctionBarCommandOverridesCtrlStandard,
                     _settingsDraft.FunctionBarCommandOverridesCtrlFdCompatible,
                     _settingsDraft.FunctionBarCommandOverridesAltStandard,
                     _settingsDraft.FunctionBarCommandOverridesAltFdCompatible
                 })
        {
            foreach (string key in map.Where(x => string.Equals(x.Value, commandId, StringComparison.OrdinalIgnoreCase)).Select(x => x.Key).ToArray())
            {
                map.Remove(key);
            }
        }
    }

    private void ResetFunctionLabelOverridesForCommand(string commandId)
    {
        foreach (Dictionary<string, FunctionBarLabelOverride> map in new[]
                 {
                     _settingsDraft.FunctionBarLabelOverridesStandard,
                     _settingsDraft.FunctionBarLabelOverridesFdCompatible,
                     _settingsDraft.FunctionBarLabelOverridesShiftStandard,
                     _settingsDraft.FunctionBarLabelOverridesShiftFdCompatible,
                     _settingsDraft.FunctionBarLabelOverridesCtrlStandard,
                     _settingsDraft.FunctionBarLabelOverridesCtrlFdCompatible,
                     _settingsDraft.FunctionBarLabelOverridesAltStandard,
                     _settingsDraft.FunctionBarLabelOverridesAltFdCompatible
                 })
        {
            foreach (string key in map.Where(x => string.Equals(x.Value.CommandId, commandId, StringComparison.OrdinalIgnoreCase)).Select(x => x.Key).ToArray())
            {
                map.Remove(key);
            }
        }
    }

    private void ResetGestureAssignmentForCommand(string commandId)
    {
        Dictionary<string, string> map = InputSettings.NormalizeMouseGestureCommandMap(_settingsDraft.MouseGestureCommandMap);
        foreach (string gesture in map.Where(x => string.Equals(x.Value, commandId, StringComparison.OrdinalIgnoreCase)).Select(x => x.Key).ToArray())
        {
            if (InputSettings.DefaultMouseGestureCommandMap.TryGetValue(gesture, out string? defaultCommand))
            {
                map[gesture] = defaultCommand;
            }
            else
            {
                map[gesture] = InputSettings.MouseGestureUnassignedCommandId;
            }
        }
        _settingsDraft.MouseGestureCommandMap = InputSettings.NormalizeMouseGestureCommandMap(map);
    }
}

public sealed class KeyCaptureDialog : Form
{
    public Keys CapturedKeyData { get; private set; } = Keys.None;
    public bool IsDeleted { get; private set; }

    public KeyCaptureDialog(string commandDisplayName, string currentKeyText)
    {
        Text = "キー割り当ての変更";
        Size = new Size(580, 320);
        MinimumSize = new Size(560, 300);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        KeyPreview = true;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(24)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 44F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 34F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 22F));
        Controls.Add(layout);

        var promptLabel = new Label
        {
            Text = $"「{commandDisplayName}」に割り当てるキーを押してください。\r\n\r\nEsc: キャンセル / Delete・Backspace: 解除",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(Font.SystemFontName, 10.5F)
        };
        layout.Controls.Add(promptLabel, 0, 0);

        var currentKeyLabel = new Label
        {
            Text = string.IsNullOrEmpty(currentKeyText) ? "(未割り当て)" : currentKeyText,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(Font.SystemFontName, 17F, FontStyle.Bold),
            ForeColor = Color.DarkBlue
        };
        layout.Controls.Add(currentKeyLabel, 0, 1);

        var helpLabel = new Label
        {
            Text = "Ctrl, Shift, Alt などの修飾キーを組み合わせることができます。",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(Font.SystemFontName, 9.5F),
            ForeColor = Color.Gray
        };
        layout.Controls.Add(helpLabel, 0, 2);

        KeyDown += (_, e) =>
        {
            e.Handled = true;
            e.SuppressKeyPress = true;

            Keys keyCode = e.KeyCode;
            Keys modifiers = e.Modifiers;
            if (keyCode is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin)
            {
                return;
            }

            if (keyCode == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                Close();
                return;
            }

            if (keyCode == Keys.Delete || keyCode == Keys.Back)
            {
                IsDeleted = true;
                DialogResult = DialogResult.OK;
                Close();
                return;
            }

            if (IsForbiddenShortcutKey(keyCode, modifiers))
            {
                return;
            }

            CapturedKeyData = keyCode | modifiers;
            DialogResult = DialogResult.OK;
            Close();
        };
    }

    private static bool IsForbiddenShortcutKey(Keys keyCode, Keys modifiers)
    {
        bool hasModifier = modifiers != Keys.None;

        if (keyCode == Keys.Enter || keyCode == Keys.Tab)
        {
            return true;
        }

        return keyCode == Keys.Escape
            || keyCode == Keys.CapsLock
            || keyCode == Keys.PrintScreen
            || keyCode == Keys.Pause;
    }
}

public sealed class FunctionKeyCaptureDialog : Form
{
    public Keys CapturedFKey { get; private set; } = Keys.None;
    public bool IsDeleted { get; private set; }

    public FunctionKeyCaptureDialog(string commandDisplayName, string currentAssignmentsText)
    {
        Text = "ファンクションキー/バー割り当て";
        Size = new Size(700, 400);
        MinimumSize = new Size(680, 380);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        KeyPreview = true;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(24)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 48F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 24F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 14F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 14F));
        Controls.Add(layout);

        var promptLabel = new Label
        {
            Text = $"ファンクションバーの入力\r\n\r\n対象: {commandDisplayName}\r\n現在: {currentAssignmentsText}\r\n\r\nF1〜F12 / Shift+F1〜F12 / Ctrl+F1〜F12 / Alt+F1〜F12: 登録",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(Font.SystemFontName, 10.5F)
        };
        layout.Controls.Add(promptLabel, 0, 0);

        var captureLabel = new Label
        {
            Text = "(キー入力を待っています)",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(Font.SystemFontName, 17F, FontStyle.Bold),
            ForeColor = Color.DarkBlue
        };
        layout.Controls.Add(captureLabel, 0, 1);

        var helpLabel = new Label
        {
            Text = "Ctrl/Shift/Alt + F1〜F12: 登録、Delete / Backspace: 解除、Esc: キャンセル",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(Font.SystemFontName, 9.5F),
            ForeColor = Color.Gray
        };
        layout.Controls.Add(helpLabel, 0, 2);

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = false
        };
        buttonPanel.Padding = new Padding(0);
        buttonPanel.Margin = new Padding(0);
        buttonPanel.Anchor = AnchorStyles.None;
        var cancelButton = new Button { Text = "キャンセル", Width = 126, Height = 34 };
        cancelButton.Margin = new Padding(8, 0, 8, 0);
        cancelButton.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        buttonPanel.Controls.Add(cancelButton);
        layout.Controls.Add(buttonPanel, 0, 3);

        KeyDown += (_, e) =>
        {
            e.Handled = true;
            e.SuppressKeyPress = true;

            if (e.KeyCode == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                Close();
                return;
            }

            if (e.KeyCode == Keys.Delete || e.KeyCode == Keys.Back)
            {
                IsDeleted = true;
                DialogResult = DialogResult.OK;
                Close();
                return;
            }

            if (e.KeyCode < Keys.F1 || e.KeyCode > Keys.F12)
            {
                return;
            }

            Keys modifier = e.Modifiers & (Keys.Control | Keys.Shift | Keys.Alt);
            if (modifier != Keys.None && modifier != Keys.Control && modifier != Keys.Shift && modifier != Keys.Alt)
            {
                return;
            }
            if (modifier == Keys.Alt && e.KeyCode == Keys.F4)
            {
                MessageBox.Show(this, "Alt+F4 は Windows 標準の閉じる操作のため、MidFD の割り当てには使用できません。", "入力不可", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            CapturedFKey = e.KeyCode | modifier;
            string prefix = modifier switch
            {
                Keys.Control => "Ctrl+",
                Keys.Shift => "Shift+",
                Keys.Alt => "Alt+",
                _ => string.Empty
            };
            captureLabel.Text = $"選択中: {prefix}{e.KeyCode}";
            captureLabel.ForeColor = Color.Crimson;
            DialogResult = DialogResult.OK;
            Close();
        };
    }
}

public sealed class FunctionAssignModeDialog : Form
{
    public string SelectedMode { get; private set; } = "add";

    public FunctionAssignModeDialog(string commandDisplayName, IReadOnlyList<string> currentSlots, string newSlot)
    {
        Text = "ファンクションキー登録方法";
        Size = new Size(560, 280);
        MinimumSize = new Size(540, 260);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(16)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 58F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 18F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 24F));
        Controls.Add(layout);

        layout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font.SystemFontName, 10F),
            Text = $"対象: {commandDisplayName}\r\n現在: {string.Join(", ", currentSlots)}\r\n新規: {newSlot}\r\n\r\n既存スロットを残して追加するか、置き換えるかを選択してください。"
        }, 0, 0);

        layout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
            Text = "追加: 既存スロットを維持 / 置換: 新規スロットのみ残す"
        }, 0, 1);

        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var cancel = new Button { Text = "キャンセル", Width = 112, Height = 32 };
        var replace = new Button { Text = "置き換える", Width = 112, Height = 32 };
        var add = new Button { Text = "追加する", Width = 112, Height = 32 };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        replace.Click += (_, _) => { SelectedMode = "replace"; DialogResult = DialogResult.OK; Close(); };
        add.Click += (_, _) => { SelectedMode = "add"; DialogResult = DialogResult.OK; Close(); };
        panel.Controls.Add(cancel);
        panel.Controls.Add(replace);
        panel.Controls.Add(add);
        layout.Controls.Add(panel, 0, 2);
    }
}

public sealed class AssignmentConflictDialog : Form
{
    public AssignmentConflictDialog(
        string title,
        string message,
        string assignedFeatureName,
        string assignedFeatureDescription,
        bool showOverwrite = true,
        string overwriteText = "上書きする")
    {
        Text = title;
        Size = new Size(560, 300);
        MinimumSize = new Size(540, 280);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(16)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 56F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 24F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 20F));
        Controls.Add(layout);

        var messageLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font.SystemFontName, 10F),
            Text = $"{message}\r\n\r\n現在の割り当て: {assignedFeatureName}\r\n説明: {assignedFeatureDescription}"
        };
        layout.Controls.Add(messageLabel, 0, 0);

        var noteLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
            Font = new Font(Font.SystemFontName, 9.5F),
            Text = showOverwrite ? "このまま適用すると現在の割り当ては置き換わります。" : "解除を実行すると現在の割り当ては外れます。"
        };
        layout.Controls.Add(noteLabel, 0, 1);

        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        var cancel = new Button { Text = "キャンセル", DialogResult = DialogResult.Cancel, Width = 112, Height = 32 };
        buttonPanel.Controls.Add(cancel);
        if (showOverwrite)
        {
            var overwrite = new Button { Text = overwriteText, DialogResult = DialogResult.Yes, Width = 112, Height = 32 };
            buttonPanel.Controls.Add(overwrite);
            AcceptButton = overwrite;
        }
        else
        {
            var apply = new Button { Text = overwriteText, DialogResult = DialogResult.Yes, Width = 112, Height = 32 };
            buttonPanel.Controls.Add(apply);
            AcceptButton = apply;
        }
        CancelButton = cancel;
        layout.Controls.Add(buttonPanel, 0, 2);
    }
}

public sealed class ShortcutInsertModeDialog : Form
{
    public string SelectedMode { get; private set; } = "cancel";

    public ShortcutInsertModeDialog(string commandDisplayName, IReadOnlyList<string> currentKeys, string newGesture)
    {
        Text = "ショートカット登録方法";
        Size = new Size(560, 280);
        MinimumSize = new Size(540, 260);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(16)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 58F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 20F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 22F));
        Controls.Add(layout);

        var text = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font.SystemFontName, 10F),
            Text = $"対象: {commandDisplayName}\r\n現在: {string.Join(", ", currentKeys)}\r\n新規: {newGesture}\r\n\r\n既存キーを残して追加するか、上書きするか選択してください。"
        };
        layout.Controls.Add(text, 0, 0);

        layout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
            Text = "追加: 既存キーを維持 / 上書き: 新規キーのみ残す"
        }, 0, 1);

        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var cancel = new Button { Text = "キャンセル", Width = 112, Height = 32 };
        var replace = new Button { Text = "上書きする", Width = 112, Height = 32 };
        var add = new Button { Text = "追加する", Width = 112, Height = 32 };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        replace.Click += (_, _) => { SelectedMode = "replace"; DialogResult = DialogResult.OK; Close(); };
        add.Click += (_, _) => { SelectedMode = "add"; DialogResult = DialogResult.OK; Close(); };
        panel.Controls.Add(cancel);
        panel.Controls.Add(replace);
        panel.Controls.Add(add);
        layout.Controls.Add(panel, 0, 2);
    }
}

public sealed class ShortcutDeleteDialog : Form
{
    public bool DeleteAll { get; private set; }
    public string? SelectedGesture { get; private set; }

    public ShortcutDeleteDialog(string commandDisplayName, IReadOnlyList<string> currentKeys)
    {
        Text = "ショートカット削除";
        Size = new Size(500, 340);
        MinimumSize = new Size(480, 320);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(16)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52F));
        Controls.Add(layout);

        layout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Text = $"対象: {commandDisplayName}\r\n削除するキーを選択するか、全削除を選んでください。"
        }, 0, 0);

        var list = new ListBox { Dock = DockStyle.Fill };
        foreach (string key in currentKeys)
        {
            list.Items.Add(key);
        }
        if (list.Items.Count > 0) list.SelectedIndex = 0;
        layout.Controls.Add(list, 0, 1);

        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var cancel = new Button { Text = "キャンセル", Width = 112, Height = 32 };
        var removeAll = new Button { Text = "全削除", Width = 112, Height = 32 };
        var removeOne = new Button { Text = "選択を削除", Width = 112, Height = 32 };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        removeAll.Click += (_, _) => { DeleteAll = true; DialogResult = DialogResult.OK; Close(); };
        removeOne.Click += (_, _) =>
        {
            if (list.SelectedItem is string gesture)
            {
                SelectedGesture = gesture;
                DialogResult = DialogResult.OK;
                Close();
            }
        };
        panel.Controls.Add(cancel);
        panel.Controls.Add(removeAll);
        panel.Controls.Add(removeOne);
        layout.Controls.Add(panel, 0, 2);
    }
}

public sealed class GestureCaptureOverlay : Form
{
    private readonly MouseGestureRecognizer _recognizer = new();
    private bool _isRightDragging;
    private readonly Label _gestureLabel;

    public string? ResultGesture { get; private set; }
    public bool IsDeleted { get; private set; }

    public GestureCaptureOverlay(string commandName)
    {
        Text = "マウスジェスチャーの入力";
        Size = new Size(740, 420);
        MinimumSize = new Size(700, 390);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(245, 245, 245);
        KeyPreview = true;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(24)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 48F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 28F));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 24F));
        Controls.Add(layout);

        var statusLabel = new Label
        {
            Text = $"マウスジェスチャーの入力\r\n\r\n対象: {commandName}\r\n\r\n右ボタンを押したままドラッグしてください。\r\n例: 左 / 右 / 上 / 下 / 上下",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(Font.SystemFontName, 11F),
            AutoEllipsis = false
        };
        layout.Controls.Add(statusLabel, 0, 0);

        _gestureLabel = new Label
        {
            Text = "(右ドラッグを開始してください)",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(Font.SystemFontName, 17F, FontStyle.Bold),
            ForeColor = Color.DarkBlue
        };
        layout.Controls.Add(_gestureLabel, 0, 1);

        var helpLabel = new Label
        {
            Text = "Esc: キャンセル、Delete / Backspace: 割り当て解除",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font(Font.SystemFontName, 9.5F),
            ForeColor = Color.Gray
        };
        layout.Controls.Add(helpLabel, 0, 2);

        MouseDown += Overlay_MouseDown;
        MouseMove += Overlay_MouseMove;
        MouseUp += Overlay_MouseUp;
        foreach (Control c in layout.Controls)
        {
            c.MouseDown += Overlay_MouseDown;
            c.MouseMove += Overlay_MouseMove;
            c.MouseUp += Overlay_MouseUp;
        }

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                Close();
            }
            else if (e.KeyCode == Keys.Delete || e.KeyCode == Keys.Back)
            {
                IsDeleted = true;
                DialogResult = DialogResult.OK;
                Close();
            }
        };
    }

    private void Overlay_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right)
        {
            return;
        }

        _recognizer.Begin(PointToClient(Cursor.Position));
        _isRightDragging = true;
        _gestureLabel.Text = "右ドラッグ中...";
        _gestureLabel.ForeColor = Color.Crimson;
    }

    private void Overlay_MouseMove(object? sender, MouseEventArgs e)
    {
        if (!_isRightDragging)
        {
            return;
        }

        _recognizer.Update(PointToClient(Cursor.Position));
        string text = _recognizer.GestureText;
        if (!string.IsNullOrEmpty(text))
        {
            _gestureLabel.Text = $"入力: {text}";
        }
    }

    private void Overlay_MouseUp(object? sender, MouseEventArgs e)
    {
        if (!_isRightDragging || e.Button != MouseButtons.Right)
        {
            return;
        }

        _isRightDragging = false;
        string gestureId = _recognizer.End(PointToClient(Cursor.Position));
        if (!string.IsNullOrEmpty(gestureId))
        {
            ResultGesture = gestureId;
            DialogResult = DialogResult.OK;
            Close();
            return;
        }

        _gestureLabel.Text = "(右ドラッグを開始してください)";
        _gestureLabel.ForeColor = Color.DarkBlue;
    }
}
