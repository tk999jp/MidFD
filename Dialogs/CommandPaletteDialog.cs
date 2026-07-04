using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using MidFD.Models;
using MidFD.Helpers;
using MidFD.Services;

namespace MidFD.Dialogs;

/// <summary>
/// コマンドパレット（組み込みコマンドランチャー）ダイアログ。
/// </summary>
public sealed class CommandPaletteDialog : Form
{
    private sealed class PaletteListItem
    {
        public required bool IsHeader { get; init; }
        public bool IsSectionHeader { get; init; }
        public bool IsMoreRow { get; init; }
        public required string HeaderText { get; init; }
        public string? NumberText { get; init; }
        public string? SectionTitle { get; init; }
        public CommandLauncherCommand? Command { get; init; }
        public bool IsExpanded { get; init; }
        public int VisibleCount { get; init; }
        public int TotalCount { get; init; }
    }

    private sealed record UniversalSearchNavigationState(
        string Breadcrumb,
        string? ScopePrefix,
        string? ScopeLabel,
        string SearchText);

    private static readonly string[] CategoryOrder = { "App", "Browser", "Mark", "External" };
    private const int CollapsedVisibleCount = 3;
    private const int RecentVisibleCount = 7;
    private const int StandardItemHeight = 24;
    private const int LayerItemHeight = 34;
    private const int SectionHeaderItemHeight = 32;
    private const int MixedItemHeight = 34;
    private readonly HashSet<string> _expandedCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Favorite", "Recent", "App", "Browser", "Mark"
    };

    private readonly Func<string, IReadOnlySet<string>?, CommandPalettePresentation> _presentationProvider;
    private readonly CommandPaletteUsageState _usageState;
    private readonly Action<CommandPaletteUsageState> _usageStateChanged;
    private readonly TextBox _searchBox;
    private readonly ListBox _commandListBox;
    private readonly HashSet<string> _expandedSections = new(StringComparer.OrdinalIgnoreCase);
    private readonly TableLayoutPanel _contentLayout;
    private readonly Panel _detailPanel;
    private readonly TableLayoutPanel _detailStack;
    private readonly Label _detailLayerLabel;
    private readonly Label _detailTypeLabel;
    private readonly Label _detailActionLabel;
    private readonly Label _detailTargetLabel;
    private readonly Label _detailInputLabel;
    private readonly Label _detailAttentionLabel;
    private readonly Label _detailDescriptionBox;
    private readonly FlowLayoutPanel _detailKeyContainer;
    private readonly FlowLayoutPanel _detailActionContainer;
    // Universal Sectioned 右ペイン専用固定ラベル
    private readonly Label _universalKindCaptionLabel;
    private readonly Label _universalNameCaptionLabel;
    private readonly Label _universalEnterValueLabel;
    private readonly TextBox _detailExampleBox;
    private readonly FlowLayoutPanel _navigationBar;
    private readonly LinkLabel _backLink;
    private readonly LinkLabel _forwardLink;
    private readonly LinkLabel _upLink;
    private readonly Label _breadcrumbLabel;
    private readonly ToolTip _toolTip;
    private readonly ContextMenuStrip _commandContextMenu;
    private readonly Label _statusLabel;
    private CommandPalettePresentation _currentPresentation = CommandPalettePresentation.Standard(Array.Empty<CommandLauncherCommand>());
    private string _currentFilter = string.Empty;
    private string _currentFilterRaw = string.Empty;
    private string? _selectedUniversalSearchScopePrefix;
    private string? _selectedUniversalSearchScopeLabel;
    private readonly Stack<UniversalSearchNavigationState> _navigationBackStack = new();
    private readonly Stack<UniversalSearchNavigationState> _navigationForwardStack = new();

    private int _lastValidSelectedIndex = -1;
    private int? _pendingMoreRowRestoreIndex;
    private int? _pendingMoreRowRestoreTopIndex;
    public CommandLauncherCommand? SelectedCommand { get; private set; }

    public CommandPaletteDialog(
        Func<string, IReadOnlySet<string>?, CommandPalettePresentation> presentationProvider,
        CommandPaletteUsageState usageState,
        Action<CommandPaletteUsageState> usageStateChanged)
    {
        _presentationProvider = presentationProvider;
        _usageState = usageState;
        _usageStateChanged = usageStateChanged;

        Text = "Command Palette";
        Size = new Size(1080, 760);
        MinimumSize = new Size(900, 640);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;

        var mainPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(8)
        };

        _searchBox = new TextBox
        {
            Dock = DockStyle.Top,
            PlaceholderText = "コマンドを検索...",
            Font = new Font(FontFamily.GenericSansSerif, 12)
        };
        _searchBox.TextChanged += (s, e) => FilterCommands();
        _searchBox.KeyDown += SearchBox_KeyDown;

        _commandListBox = new ListBox
        {
            Dock = DockStyle.Fill,
            Font = new Font(FontFamily.GenericSansSerif, 11),
            ItemHeight = StandardItemHeight,
            ScrollAlwaysVisible = true
        };
        _commandListBox.DoubleClick += (s, e) => ExecuteSelected();
        _commandListBox.MouseDown += CommandListBox_MouseDown;
        _commandListBox.SelectedIndexChanged += (s, e) =>
        {
            int idx = _commandListBox.SelectedIndex;
            if (idx >= 0 && idx < _commandListBox.Items.Count)
            {
                if (_commandListBox.Items[idx] is PaletteListItem item && (item.IsSectionHeader || item.IsHeader))
                {
                    _commandListBox.SelectedIndex = _lastValidSelectedIndex;
                    return;
                }
            }
            _lastValidSelectedIndex = idx;

            // リスト選択後も入力フォーカスは検索欄へ戻す
            // コンストラクタ実行中（ハンドル未作成）の例外を避けるため IsHandleCreated を確認
            if (IsHandleCreated && Visible && !_searchBox.Focused)
            {
                BeginInvoke(new Action(() => _searchBox.Focus()));
            }
            UpdateStatusLabel();
            UpdateDetailPane();
        };
        _commandListBox.DrawMode = DrawMode.OwnerDrawFixed;
        _commandListBox.DrawItem += CommandListBox_DrawItem;
        _commandListBox.MouseMove += CommandListBox_MouseMove;
        _toolTip = new ToolTip
        {
            AutomaticDelay = 150,
            AutoPopDelay = 8000,
            InitialDelay = 150,
            ReshowDelay = 100,
            ShowAlways = true
        };
        _commandListBox.MouseLeave += (_, _) => _toolTip.Hide(_commandListBox);
        _commandListBox.PreviewKeyDown += (s, e) =>
        {
            if (e.KeyCode is Keys.Enter or Keys.Left or Keys.Right)
            {
                e.IsInputKey = true;
            }
        };
        _commandListBox.KeyDown += (s, e) =>
        {
            if (e.Control && e.KeyCode == Keys.D)
            {
                ToggleSelectedFavorite();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            if (TryHandlePaletteActionKey(e.KeyCode, fromSearchBox: false))
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        };

        _commandContextMenu = new ContextMenuStrip();
        _commandContextMenu.Opening += (_, e) =>
        {
            if (_currentPresentation.IsLayered || _currentPresentation.HasSections)
            {
                e.Cancel = true;
                return;
            }

            if (GetSelectedCommand() == null)
            {
                e.Cancel = true;
                return;
            }

            _commandContextMenu.Items.Clear();
            CommandLauncherCommand selected = GetSelectedCommand()!;
            bool isFavorite = IsFavorite(selected.Id);
            _commandContextMenu.Items.Add(
                isFavorite ? "お気に入りから削除" : "お気に入りに追加",
                null,
                (_, _) => ToggleSelectedFavorite());
        };

        _statusLabel = new Label
        {
            Dock = DockStyle.Bottom,
            AutoEllipsis = true,
            AutoSize = false,
            Height = 26,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(2, 0, 2, 0)
        };

        _backLink = CreateNavigationLink("←戻る", (_, _) => NavigateBack());
        _forwardLink = CreateNavigationLink("→進む", (_, _) => NavigateForward());
        _upLink = CreateNavigationLink("↑上へ", (_, _) => NavigateUp());
        _breadcrumbLabel = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Padding = new Padding(8, 4, 0, 0),
            Font = new Font(FontFamily.GenericSansSerif, 10, FontStyle.Bold),
            Text = "カテゴリ選択ホーム"
        };
        _navigationBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 28,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
        _navigationBar.Controls.Add(_backLink);
        _navigationBar.Controls.Add(_forwardLink);
        _navigationBar.Controls.Add(_upLink);
        _navigationBar.Controls.Add(_breadcrumbLabel);
        _toolTip.SetToolTip(_backLink, "検索階層の履歴を戻ります。");
        _toolTip.SetToolTip(_forwardLink, "戻った階層へ進みます。");
        _toolTip.SetToolTip(_upLink, "現在カテゴリの上階層へ戻ります。");
        _toolTip.SetToolTip(_breadcrumbLabel, "現在地を表示します。");

        _detailLayerLabel = CreateDetailLabel();
        _detailLayerLabel.Font = new Font(FontFamily.GenericSansSerif, 9.5F, FontStyle.Regular);
        _detailLayerLabel.BackColor = Color.FromArgb(235, 240, 246);
        _detailLayerLabel.ForeColor = Color.FromArgb(80, 100, 125);
        _detailLayerLabel.Padding = new Padding(6, 2, 6, 2);
        _detailLayerLabel.Margin = new Padding(0, 4, 0, 16);

        _detailTypeLabel = CreateDetailLabel();
        _detailTypeLabel.Font = new Font(FontFamily.GenericSansSerif, 15F, FontStyle.Bold);
        _detailTypeLabel.ForeColor = Color.FromArgb(20, 20, 20);
        _detailTypeLabel.Margin = new Padding(0, 0, 0, 2);

        _detailActionLabel = CreateDetailLabel();
        _detailActionLabel.Font = new Font(FontFamily.GenericSansSerif, 10F, FontStyle.Bold);
        _detailActionLabel.ForeColor = Color.FromArgb(120, 120, 120);
        _detailActionLabel.Margin = new Padding(0, 16, 0, 6);

        _detailTargetLabel = CreateDetailLabel();
        _detailTargetLabel.Font = new Font(FontFamily.GenericSansSerif, 10F, FontStyle.Bold);
        _detailTargetLabel.ForeColor = Color.FromArgb(120, 120, 120);
        _detailTargetLabel.Margin = new Padding(0, 16, 0, 6);

        _detailInputLabel = CreateDetailLabel();

        _detailAttentionLabel = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 30,
            Padding = new Padding(8, 5, 8, 5),
            Margin = new Padding(0, 6, 0, 6),
            BackColor = Color.FromArgb(255, 243, 205),
            ForeColor = Color.FromArgb(146, 111, 0),
            Font = new Font(FontFamily.GenericSansSerif, 10, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Visible = false
        };

        _detailDescriptionBox = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ForeColor = Color.FromArgb(70, 70, 70),
            Font = new Font(FontFamily.GenericSansSerif, 10),
            Margin = new Padding(0, 4, 0, 12)
        };

        _detailKeyContainer = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 4, 0, 8),
            Padding = new Padding(0)
        };

        _detailActionContainer = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = true,
            FlowDirection = FlowDirection.TopDown,
            Margin = new Padding(0, 4, 0, 8),
            Padding = new Padding(0)
        };

        _detailExampleBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            BorderStyle = BorderStyle.FixedSingle,
            ScrollBars = FontHeight > 0 ? ScrollBars.None : ScrollBars.Vertical, // ダミー
            BackColor = Color.FromArgb(250, 250, 250),
            Font = new Font(FontFamily.GenericSansSerif, 10),
            TabStop = false
        };

        _detailPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 16, 16, 16),
            BackColor = Color.FromArgb(248, 250, 252),
            BorderStyle = BorderStyle.FixedSingle
        };

        // Universal Sectioned 右ペイン専用固定captionラベル初期化
        _universalKindCaptionLabel = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Font = new Font(FontFamily.GenericSansSerif, 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(30, 60, 90),
            BackColor = Color.FromArgb(232, 240, 250),
            Padding = new Padding(6, 3, 6, 3),
            Margin = new Padding(0, 4, 0, 2),
            Text = "種類",
            Visible = false
        };
        _universalNameCaptionLabel = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Font = new Font(FontFamily.GenericSansSerif, 9F, FontStyle.Bold),
            ForeColor = Color.FromArgb(30, 60, 90),
            BackColor = Color.FromArgb(232, 240, 250),
            Padding = new Padding(6, 3, 6, 3),
            Margin = new Padding(0, 8, 0, 2),
            Text = "名前",
            Visible = false
        };
        _universalEnterValueLabel = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Font = new Font(FontFamily.GenericSansSerif, 10F, FontStyle.Regular),
            ForeColor = Color.FromArgb(50, 50, 50),
            BackColor = Color.Transparent,
            Padding = new Padding(4, 2, 4, 2),
            Margin = new Padding(0, 2, 0, 4),
            Text = string.Empty,
            Visible = false
        };

        // _detailStack: RowCount=15 に拡張（Universal Sectioned専用行を追加）
        // Row配置:
        //  0: 「詳細」ヘッダ
        //  1: attention
        //  2: [種類] caption (_universalKindCaptionLabel) / Standard: _detailTypeLabel
        //  3: 種類 値 (_detailLayerLabel) / Standard: そのまま
        //  4: [名前] caption (_universalNameCaptionLabel) / Standard: _detailDescriptionBox
        //  5: 名前 値 (_detailTypeLabel) / Standard: _detailTargetLabel
        //  6: 説明文 (_detailDescriptionBox) → Universal時のみ使用 / Standard: _detailKeyContainer
        //  7: [キー] caption (_detailTargetLabel) / Standard: _detailActionLabel
        //  8: キーbadge (_detailKeyContainer) / Standard: _detailActionContainer
        //  9: [Enter] caption (_detailActionLabel) / Standard: _detailInputLabel
        // 10: Enter値 (_universalEnterValueLabel) / Standard: (unused)
        // 11: _detailActionContainer (Universal: unused, Standard: moved here)
        // 12: _detailInputLabel
        // 13: _detailExampleBox
        _detailStack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 14,
            BackColor = Color.Transparent
        };
        for (int i = 0; i < 13; i++)
        {
            _detailStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }
        _detailStack.RowStyles.Add(new RowStyle(SizeType.Absolute, 124));

        _detailStack.Controls.Add(new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 30,
            Font = new Font(FontFamily.GenericSansSerif, 12F, FontStyle.Bold),
            BackColor = Color.FromArgb(44, 74, 122),
            ForeColor = Color.White,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 10, 0),
            Margin = new Padding(0, 0, 0, 8),
            Text = "詳細"
        }, 0, 0);
        _detailStack.Controls.Add(_detailAttentionLabel, 0, 1);
        // Standard/Layered 既定配置
        _detailStack.Controls.Add(_detailTypeLabel, 0, 2);
        _detailStack.Controls.Add(_detailLayerLabel, 0, 3);
        _detailStack.Controls.Add(_detailDescriptionBox, 0, 4);
        _detailStack.Controls.Add(_detailTargetLabel, 0, 5);
        _detailStack.Controls.Add(_detailKeyContainer, 0, 6);
        _detailStack.Controls.Add(_detailActionLabel, 0, 7);
        _detailStack.Controls.Add(_detailActionContainer, 0, 8);
        _detailStack.Controls.Add(_detailInputLabel, 0, 9);
        _detailStack.Controls.Add(_detailExampleBox, 0, 13);
        // Universal Sectioned 専用固定ラベル（初期は非表示）
        _detailStack.Controls.Add(_universalKindCaptionLabel, 0, 10);
        _detailStack.Controls.Add(_universalNameCaptionLabel, 0, 11);
        _detailStack.Controls.Add(_universalEnterValueLabel, 0, 12);

        _detailLayerLabel.Text = "-";
        _detailTypeLabel.Text = "-";
        _detailTargetLabel.Text = string.Empty;
        _detailActionLabel.Text = "-";
        _detailAttentionLabel.Visible = false;
        _detailTargetLabel.Visible = false;
        _detailActionLabel.Visible = false;
        _detailInputLabel.Visible = false;
        _detailDescriptionBox.Visible = false;
        _detailExampleBox.Visible = false;
        _detailStack.RowStyles[1] = new RowStyle(SizeType.Absolute, 0);
        _detailStack.RowStyles[4] = new RowStyle(SizeType.Absolute, 0);
        _detailStack.RowStyles[5] = new RowStyle(SizeType.Absolute, 0);
        _detailStack.RowStyles[6] = new RowStyle(SizeType.Absolute, 0);
        _detailStack.RowStyles[7] = new RowStyle(SizeType.Absolute, 0);
        _detailStack.RowStyles[8] = new RowStyle(SizeType.Absolute, 0);
        _detailStack.RowStyles[9] = new RowStyle(SizeType.Absolute, 0);
        _detailStack.RowStyles[10] = new RowStyle(SizeType.Absolute, 0);
        _detailStack.RowStyles[11] = new RowStyle(SizeType.Absolute, 0);
        _detailStack.RowStyles[12] = new RowStyle(SizeType.Absolute, 0);
        _detailStack.RowStyles[13] = new RowStyle(SizeType.Absolute, 0);
        _detailPanel.Controls.Add(_detailStack);

        _contentLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(0, 8, 0, 8)
        };
        _contentLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        _contentLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 360F));
        _contentLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        var leftPanel = new Panel
        {
            Dock = DockStyle.Fill
        };
        leftPanel.Controls.Add(_commandListBox);
        leftPanel.Controls.Add(_searchBox);
        leftPanel.Controls.Add(_navigationBar);
        leftPanel.Controls.Remove(_navigationBar);

        _contentLayout.Controls.Add(leftPanel, 0, 0);
        _contentLayout.Controls.Add(_detailPanel, 1, 0);

        mainPanel.Controls.Add(_statusLabel);
        mainPanel.Controls.Add(_contentLayout);
        Controls.Add(mainPanel);

        FilterCommands();

        Shown += (_, _) => _searchBox.Focus();
    }

    private void FilterCommands()
    {
        string rawFilter = _searchBox.Text;
        if (rawFilter != _currentFilterRaw)
        {
            _expandedSections.Clear();
        }
        _currentFilterRaw = rawFilter;
        _currentFilter = rawFilter.Trim();
        _currentPresentation = _presentationProvider(rawFilter, _expandedSections);
        bool hasSections = _currentPresentation.HasSections;
        _commandListBox.ItemHeight = hasSections
            ? MixedItemHeight
            : StandardItemHeight;
        _detailPanel.Visible = true;
        _contentLayout.ColumnStyles[1].Width = 360F;

        _commandListBox.BeginUpdate();
        var previousSelection = _commandListBox.SelectedItem as PaletteListItem;
        int? pendingMoreRowRestoreIndex = _pendingMoreRowRestoreIndex;
        int? pendingMoreRowRestoreTopIndex = _pendingMoreRowRestoreTopIndex;
        _pendingMoreRowRestoreIndex = null;
        _pendingMoreRowRestoreTopIndex = null;

        _commandListBox.Items.Clear();
        if (hasSections)
        {
            BuildSectionedItems();
        }
        else
        {
            foreach (CommandLauncherCommand cmd in _currentPresentation.Commands)
            {
                _commandListBox.Items.Add(new PaletteListItem
                {
                    IsHeader = false,
                    HeaderText = string.Empty,
                    Command = cmd
                });
            }
        }

        if (pendingMoreRowRestoreIndex.HasValue)
        {
            RestoreSelectionAfterMoreRowExpand(pendingMoreRowRestoreIndex.Value, pendingMoreRowRestoreTopIndex ?? 0);
        }
        else
        {
            RestoreSelection(previousSelection);
        }
        UpdateStatusLabel();
        UpdateDetailPane();

        _commandListBox.EndUpdate();
    }

    private string BuildEffectiveUniversalSearchQuery(string filter)
    {
        if (string.IsNullOrWhiteSpace(_selectedUniversalSearchScopePrefix))
        {
            return filter;
        }

        if (string.IsNullOrWhiteSpace(filter))
        {
            return _selectedUniversalSearchScopePrefix;
        }

        if (CommandPaletteUniversalSearchService.TryParseScope(filter, out _))
        {
            return filter;
        }

        return $"{_selectedUniversalSearchScopePrefix} {filter}";
    }

    private void RestoreSelection(PaletteListItem? previous)
    {
        if (previous == null)
        {
            SelectFirstExecutableItem();
            return;
        }

        if (previous.IsSectionHeader)
        {
            SelectFirstExecutableItem();
            return;
        }

        // 基本方針: 同じ ID のコマンド、または同じカテゴリ見出しを探す
        for (int i = 0; i < _commandListBox.Items.Count; i++)
        {
            var item = (PaletteListItem)_commandListBox.Items[i];
            if (previous.IsHeader && item.IsHeader && previous.HeaderText == item.HeaderText)
            {
                _commandListBox.SelectedIndex = i;
                return;
            }
            if (!previous.IsHeader && !previous.IsSectionHeader && !item.IsHeader && !item.IsSectionHeader && previous.Command?.Id == item.Command?.Id)
            {
                _commandListBox.SelectedIndex = i;
                return;
            }
        }

        SelectFirstExecutableItem();
    }

    private void RestoreSelectionAfterMoreRowExpand(int previousIndex, int previousTopIndex)
    {
        if (_commandListBox.Items.Count == 0)
        {
            _commandListBox.SelectedIndex = -1;
            return;
        }

        int restoreIndex = Math.Min(Math.Max(previousIndex, 0), _commandListBox.Items.Count - 1);
        _commandListBox.SelectedIndex = restoreIndex;

        try
        {
            int restoreTopIndex = Math.Min(Math.Max(previousTopIndex, 0), _commandListBox.Items.Count - 1);
            _commandListBox.TopIndex = restoreTopIndex;
        }
        catch
        {
            // TopIndex が使えない状況では選択だけ復元する。
        }
    }

    private bool IsMatch(CommandLauncherCommand cmd, string filter)
    {
        if (string.IsNullOrEmpty(filter)) return true;

        if (_currentPresentation.IsSectioned)
        {
            return GetUniversalSearchScore(cmd, filter) >= 0;
        }

        CommandPaletteLayerQuery query = CommandPaletteLayerQueryParser.Parse(filter);
        string[] tokens = query.Tokens.ToArray();
        if (tokens.Length == 0)
        {
            return true;
        }

        string searchTarget = BuildSearchTarget(cmd);
        return tokens.All(token => searchTarget.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private List<CommandLauncherCommand> BuildFilteredCommands(
        IReadOnlyList<CommandLauncherCommand> commands,
        string filter,
        bool useAccordion)
    {
        IEnumerable<CommandLauncherCommand> matched = commands.Where(c => IsMatch(c, filter));
        if (_currentPresentation.IsSectioned)
        {
            return matched
                .Select((command, index) => new { command, index, score = GetUniversalSearchScore(command, filter) })
                .Where(item => item.score >= 0)
                .OrderByDescending(item => item.score)
                .ThenBy(item => item.index)
                .Select(item => item.command)
                .ToList();
        }

        if (_currentPresentation.IsLayered)
        {
            CommandPaletteLayerQuery layerQuery = CommandPaletteLayerQueryParser.Parse(filter);
            return matched
                .Select((command, index) => new { command, index, rank = GetLayerDisplayRank(command, layerQuery) })
                .OrderByDescending(item => item.rank)
                .ThenBy(item => item.index)
                .Select(item => item.command)
                .ToList();
        }

        if (!useAccordion)
        {
            return matched
                .OrderByDescending(GetSearchBoost)
                .ThenBy(c => GetCategoryOrder(c.Category))
                .ThenBy(c => c.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return matched
            .OrderBy(c => GetCategoryOrder(c.Category))
            .ThenBy(c => c.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void BuildSectionedItems()
    {
        foreach (CommandPaletteSection section in _currentPresentation.Sections ?? Array.Empty<CommandPaletteSection>())
        {
            _commandListBox.Items.Add(new PaletteListItem
            {
                IsHeader = false,
                IsSectionHeader = true,
                HeaderText = section.Title,
                SectionTitle = section.Title,
                VisibleCount = section.Commands.Count,
                TotalCount = section.TotalCount
            });

            foreach (CommandLauncherCommand command in section.Commands)
            {
                _commandListBox.Items.Add(new PaletteListItem
                {
                    IsHeader = false,
                    IsSectionHeader = false,
                    HeaderText = string.Empty,
                    SectionTitle = section.Title,
                    NumberText = null,
                    Command = command
                });
            }

            if (section.Commands.Count < section.TotalCount)
            {
                int remaining = section.TotalCount - section.Commands.Count;
                _commandListBox.Items.Add(new PaletteListItem
                {
                    IsHeader = false,
                    IsSectionHeader = false,
                    IsMoreRow = true,
                    HeaderText = $"残り{remaining}件を表示",
                    SectionTitle = section.Title,
                    NumberText = null,
                    Command = null
                });
            }
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.D))
        {
            ToggleSelectedFavorite();
            return true;
        }

        var key = keyData & Keys.KeyCode;

        if (TryHandleUniversalSearchHomeShortcut(keyData))
        {
            return true;
        }

        if (TryHandlePaletteActionKey(key, fromSearchBox: _searchBox.Focused))
        {
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private bool TryHandlePaletteActionKey(Keys key, bool fromSearchBox)
    {
        // Escape: そのまま cancel/close する
        if (key == Keys.Escape)
        {
            DialogResult = DialogResult.Cancel;
            Close();
            return true;
        }

        // Enter: 見出しなら展開/折りたたみ、コマンドなら実行
        if (key == Keys.Enter)
        {
            ExecuteSelected();
            return true;
        }

        // 上下キー: リスト移動
        if (key == Keys.Up)
        {
            MoveSelection(-1);
            return true;
        }
        if (key == Keys.Down)
        {
            MoveSelection(1);
            return true;
        }

        // Left / Right: カテゴリ見出し操作
        // ただし TextBox フォーカス時はカーソル移動を優先する
        if (key == Keys.Right && !fromSearchBox)
        {
            if (_commandListBox.SelectedItem is PaletteListItem { IsHeader: true, IsExpanded: false })
            {
                ToggleHeaderExpansion();
                return true;
            }
        }

        if (key == Keys.Left && !fromSearchBox)
        {
            if (_commandListBox.SelectedItem is PaletteListItem { IsHeader: true, IsExpanded: true })
            {
                ToggleHeaderExpansion();
                return true;
            }
        }

        return false;
    }

    private bool TryHandleUniversalSearchHomeShortcut(Keys keyData)
    {
        _ = keyData;
        return false;
    }

    private void AddUsageGroups()
    {
        Dictionary<string, CommandLauncherCommand> commandById = _currentPresentation.Commands
            .GroupBy(static command => command.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);

        List<CommandLauncherCommand> favorites = GetFavoriteCommands(commandById);
        AddCommandGroup("Favorite", "★ Favorite", favorites, favorites.Count);

        HashSet<string> favoriteIds = favorites.Select(static command => command.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<CommandLauncherCommand> recent = _usageState.RecentCommands
            .OrderByDescending(static item => item.LastUsedUtc)
            .Select(item => commandById.TryGetValue(item.CommandId, out CommandLauncherCommand? command) ? command : null)
            .Where(command => command != null && !favoriteIds.Contains(command.Id))
            .Cast<CommandLauncherCommand>()
            .Take(RecentVisibleCount)
            .ToList();
        AddCommandGroup("Recent", "最近使ったコマンド", recent, recent.Count);
    }

    private void AddCommandGroup(string key, string label, IReadOnlyList<CommandLauncherCommand> commands, int totalCount)
    {
        if (commands.Count == 0)
        {
            return;
        }

        bool isExpanded = _expandedCategories.Contains(key);
        int visibleCount = isExpanded ? commands.Count : Math.Min(commands.Count, CollapsedVisibleCount);
        _commandListBox.Items.Add(new PaletteListItem
        {
            IsHeader = true,
            HeaderText = key,
            IsExpanded = isExpanded,
            VisibleCount = visibleCount,
            TotalCount = totalCount
        });

        for (int i = 0; i < visibleCount; i++)
        {
            _commandListBox.Items.Add(new PaletteListItem
            {
                IsHeader = false,
                HeaderText = label,
                Command = commands[i]
            });
        }
    }

    private List<CommandLauncherCommand> GetFavoriteCommands(IReadOnlyDictionary<string, CommandLauncherCommand> commandById)
    {
        Dictionary<string, DateTime> recentById = _usageState.RecentCommands
            .GroupBy(static item => item.CommandId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Max(item => item.LastUsedUtc), StringComparer.OrdinalIgnoreCase);

        return _usageState.FavoriteCommandIds
            .Select((id, index) => new { Id = id, Index = index })
            .Where(item => commandById.ContainsKey(item.Id))
            .OrderByDescending(item => recentById.TryGetValue(item.Id, out DateTime lastUsedUtc) ? lastUsedUtc : DateTime.MinValue)
            .ThenBy(item => item.Index)
            .Select(item => commandById[item.Id])
            .ToList();
    }

    private int GetSearchBoost(CommandLauncherCommand command)
    {
        int boost = 0;
        if (IsFavorite(command.Id))
        {
            boost += 2;
        }

        if (_usageState.RecentCommands.Any(item => string.Equals(item.CommandId, command.Id, StringComparison.OrdinalIgnoreCase)))
        {
            boost += 1;
        }

        return boost;
    }

    private static string BuildSearchTarget(CommandLauncherCommand cmd)
    {
        return string.Join(" ", new[]
        {
            cmd.DisplayName,
            cmd.LayerBadge ?? string.Empty,
            cmd.Id,
            cmd.Category,
            cmd.Description ?? string.Empty,
            cmd.SearchText ?? string.Empty,
            cmd.SecondaryText ?? string.Empty
        });
    }

    private static int GetUniversalSearchScore(CommandLauncherCommand command, string filter)
    {
        string normalizedFilter = NormalizeUniversalSearchText(filter);
        if (string.IsNullOrWhiteSpace(normalizedFilter))
        {
            return 0;
        }

        string[] tokens = normalizedFilter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return 0;
        }

        var fields = new (string? Value, int Weight)[]
        {
            (command.DisplayName, 5),
            (command.SearchText, 5),
            (command.SecondaryText, 3),
            (command.Description, 2),
            (command.Category, 3),
            (command.LayerKind, 3),
            (command.LayerBadge, 2),
            (command.Id, 2)
        };

        int total = 0;
        foreach (string token in tokens)
        {
            int best = -1;
            foreach ((string? value, int weight) in fields)
            {
                int score = ScoreUniversalSearchField(value, token, weight);
                if (score > best)
                {
                    best = score;
                }
            }

            if (best < 0)
            {
                return -1;
            }

            total += best;
        }

        return total;
    }

    private static int ScoreUniversalSearchField(string? value, string token, int weight)
    {
        string normalizedValue = NormalizeUniversalSearchText(value);
        if (string.IsNullOrWhiteSpace(normalizedValue))
        {
            return -1;
        }

        if (string.Equals(normalizedValue, token, StringComparison.OrdinalIgnoreCase))
        {
            return 100 * weight;
        }

        if (normalizedValue.StartsWith(token, StringComparison.OrdinalIgnoreCase))
        {
            return 60 * weight;
        }

        if (normalizedValue.Contains(token, StringComparison.OrdinalIgnoreCase))
        {
            return 20 * weight;
        }

        return -1;
    }

    private static string NormalizeUniversalSearchText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string normalized = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var builder = new System.Text.StringBuilder(normalized.Length);
        bool lastWasSpace = false;
        foreach (char ch in normalized)
        {
            char mapped = ch switch
            {
                '\\' or '/' or '-' or '_' or '.' or ':' or ';' or ',' or '|' or '(' or ')' or '[' or ']' or '{' or '}' or '+' or '=' => ' ',
                _ when char.IsWhiteSpace(ch) => ' ',
                _ => ch
            };

            if (mapped == ' ')
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }
            }
            else
            {
                builder.Append(mapped);
                lastWasSpace = false;
            }
        }

        return builder.ToString().Trim();
    }

    private CommandLauncherCommand? GetSelectedCommand()
    {
        return _commandListBox.SelectedItem is PaletteListItem { IsHeader: false, Command: { } command }
            ? command
            : null;
    }

    private bool IsFavorite(string commandId)
    {
        return _usageState.FavoriteCommandIds.Any(id => string.Equals(id, commandId, StringComparison.OrdinalIgnoreCase));
    }

    private void ToggleSelectedFavorite()
    {
        if (_currentPresentation.IsLayered || _currentPresentation.HasSections)
        {
            return;
        }

        CommandLauncherCommand? command = GetSelectedCommand();
        if (command == null)
        {
            return;
        }

        if (IsFavorite(command.Id))
        {
            _usageState.FavoriteCommandIds = _usageState.FavoriteCommandIds
                .Where(id => !string.Equals(id, command.Id, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        else
        {
            _usageState.FavoriteCommandIds.Add(command.Id);
            _usageState.FavoriteCommandIds = _usageState.FavoriteCommandIds
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        _usageStateChanged(_usageState);
        FilterCommands();
        BeginInvoke(new Action(() => _searchBox.Focus()));
    }

    private void UpdateStatusLabel()
    {
        if (_currentPresentation.HasSections)
        {
            _statusLabel.Text = _currentPresentation.IsSectioned
                ? BuildUniversalSectionedFooterText()
                : BuildSectionedFooterText();
            return;
        }

        if (_currentPresentation.IsLayered)
        {
            _statusLabel.Text = BuildLayerFooterText();
            return;
        }

        string statusText = _currentPresentation.StatusText ?? string.Empty;
        if (_commandListBox.SelectedItem is PaletteListItem { IsHeader: false, Command: { } command })
        {
            string selectionText = BuildStatusText(command);
            if (!string.IsNullOrWhiteSpace(selectionText))
            {
                statusText = string.IsNullOrWhiteSpace(statusText)
                    ? selectionText
                    : $"{statusText} / {selectionText}";
            }
        }

        _statusLabel.Text = string.IsNullOrWhiteSpace(statusText) ? " " : statusText;
    }

    private string BuildLayerFooterText()
    {
        string rootHelp = _currentPresentation.StatusText ?? "Layer";

        if (_commandListBox.SelectedItem is not PaletteListItem { IsHeader: false, Command: { } command })
        {
            return string.IsNullOrWhiteSpace(rootHelp) ? " " : rootHelp;
        }

        string action = BuildLayerActionText(command);
        string shortRoot = BuildLayerRootLabel(command);
        string selection = BuildLayerSelectionText(command);
        string footer = string.Join(" / ", new[]
        {
            string.IsNullOrWhiteSpace(selection) ? null : $"選択中: {selection}",
            string.IsNullOrWhiteSpace(action) ? null : $"Enter: {action}",
            "Esc: 閉じる",
            string.IsNullOrWhiteSpace(shortRoot) ? rootHelp : shortRoot
        }.Where(text => !string.IsNullOrWhiteSpace(text)));

        return string.IsNullOrWhiteSpace(footer) ? " " : footer;
    }

    private string BuildSectionedFooterText()
    {
        string rootHelp = _currentPresentation.StatusText ?? "Layer";

        if (_commandListBox.SelectedItem is not PaletteListItem selected)
        {
            return string.IsNullOrWhiteSpace(rootHelp) ? " " : rootHelp;
        }

        if (selected.IsSectionHeader)
        {
            string sectionSummary = selected.HeaderText switch
            {
                "Layer候補" => "Layer候補: layer候補のみを表示",
                "通常検索" => "通常検索: 通常コマンドを表示",
                _ => selected.HeaderText
            };

            string footer = string.Join(" / ", new[]
            {
                sectionSummary,
                "Enter: 実行しません",
                "Esc: 閉じる",
                rootHelp
            }.Where(text => !string.IsNullOrWhiteSpace(text)));

            return string.IsNullOrWhiteSpace(footer) ? " " : footer;
        }

        if (selected.Command is not { } command)
        {
            return string.IsNullOrWhiteSpace(rootHelp) ? " " : rootHelp;
        }

        string sectionPrefix = string.IsNullOrWhiteSpace(selected.SectionTitle)
            ? string.Empty
            : $"[{selected.SectionTitle}] ";
        string selection = selected.SectionTitle == "Layer候補"
            ? BuildLayerSelectionText(command)
            : BuildStatusText(command);
        string action = selected.SectionTitle == "Layer候補"
            ? BuildLayerActionText(command)
            : "Enter: 選択コマンドを実行";
        string footerText = string.Join(" / ", new[]
        {
            string.IsNullOrWhiteSpace(selection) ? null : $"{sectionPrefix}{selection}",
            string.IsNullOrWhiteSpace(action) ? null : action,
            "Esc: 閉じる",
            rootHelp
        }.Where(text => !string.IsNullOrWhiteSpace(text)));

        return string.IsNullOrWhiteSpace(footerText) ? " " : footerText;
    }

    private string BuildUniversalSectionedFooterText()
    {
        if (_commandListBox.SelectedItem is PaletteListItem { IsMoreRow: true })
        {
            return "クリックまたはEnterで残りの候補を表示します";
        }

        string baseText = _currentPresentation.StatusText ?? "機能 / 設定 を検索できます。";
        string selectionText = string.Empty;

        if (_commandListBox.SelectedItem is PaletteListItem selected)
        {
            selectionText = selected.IsSectionHeader
                ? $"選択: {selected.HeaderText}"
                : selected.Command is { } command
                    ? $"{command.Group} / {command.Title}"
                    : string.Empty;
        }

        string footer = string.Join(" / ", new[]
        {
            baseText,
            selectionText,
            "Enter: 実行",
            "Esc: 閉じる"
        }.Where(text => !string.IsNullOrWhiteSpace(text)));

        return string.IsNullOrWhiteSpace(footer) ? " " : footer;
    }

    private static string BuildStatusText(CommandLauncherCommand command)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(command.Description))
        {
            parts.Add(command.Description);
        }

        if (!string.IsNullOrWhiteSpace(command.SecondaryText))
        {
            parts.Add(command.SecondaryText);
        }

        return string.Join(" / ", parts);
    }

    private static string BuildUniversalSelectionText(CommandLauncherCommand command)
    {
        if (command.ClearsSearchText)
        {
            return command.DisplayName;
        }

        if (!string.IsNullOrWhiteSpace(command.QueryInsertText))
        {
            return command.DisplayName;
        }

        string kind = BuildUniversalKindLabel(command);
        string title = command.DisplayName ?? string.Empty;
        return string.Join(" ", new[] { kind, title }.Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private static string BuildUniversalActionText(CommandLauncherCommand command)
    {
        if (command.SafetyLevel == CommandPaletteSafetyLevel.Confirm)
        {
            return "Enter: 確認して実行";
        }

        if (command.SafetyLevel == CommandPaletteSafetyLevel.Unsupported)
        {
            string reason = command.SafetyInfo.ReasonText ?? command.NonExecutableMessage ?? "未対応";
            return $"Enter: 実行できません / {reason}";
        }

        if (command.SafetyLevel == CommandPaletteSafetyLevel.Deferred)
        {
            string reason = command.SafetyInfo.ReasonText ?? command.NonExecutableMessage ?? "後続フェーズ対象";
            return $"Enter: 実行できません / {reason}";
        }

        if (!string.IsNullOrWhiteSpace(command.NonExecutableMessage))
        {
            return $"Enter: 実行できません / {command.NonExecutableMessage}";
        }

        return command.ActionKind switch
        {
            CommandPaletteActionKind.OpenSettings => "Enter: 設定を開く",
            CommandPaletteActionKind.OpenDialog => "Enter: 画面を開く",
            CommandPaletteActionKind.Copy => "Enter: クリップボードへコピー",
            CommandPaletteActionKind.Navigate => "Enter: 移動する",
            CommandPaletteActionKind.InsertQuery => "Enter: 検索語を入力する",
            _ => "Enter: 実行"
        };
    }

    private static string BuildUniversalTargetText(CommandLauncherCommand command)
    {
        if (command.ClearsSearchText)
        {
            return "対象: カテゴリ一覧";
        }

        if (!string.IsNullOrWhiteSpace(command.QueryInsertText))
        {
            return string.IsNullOrWhiteSpace(command.SecondaryText)
                ? BuildUniversalScopeDisplayLabel(command)
                : $"{BuildUniversalScopeDisplayLabel(command)} {command.SecondaryText}";
        }

        return command.Category switch
        {
            "Tab" => string.IsNullOrWhiteSpace(command.SecondaryText) ? "開いているタブ" : command.SecondaryText,
            "QuickAccess" => string.IsNullOrWhiteSpace(command.SecondaryText) ? "移動先" : command.SecondaryText,
            "Command" => string.IsNullOrWhiteSpace(command.SecondaryText) ? command.Description ?? "機能" : command.SecondaryText,
            "Setting" => string.IsNullOrWhiteSpace(command.SecondaryText) ? command.Description ?? "設定" : command.SecondaryText,
            _ => command.SecondaryText ?? command.Description ?? string.Empty
        };
    }

    private static string BuildUniversalSectionedDescriptionText(CommandLauncherCommand command)
    {
        var lines = new List<string>();
        string description = command.Description ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(description))
        {
            lines.Add(description);
        }

        if (!string.IsNullOrWhiteSpace(command.SearchText) &&
            !command.ClearsSearchText &&
            string.IsNullOrWhiteSpace(command.QueryInsertText) &&
            !string.Equals(command.Category, "Category", StringComparison.OrdinalIgnoreCase))
        {
            lines.Add($"検索語: {TruncateLayerDetail(command.SearchText, 48)}");
        }

        if (!string.IsNullOrWhiteSpace(command.NonExecutableMessage))
        {
            lines.Add(BuildAttentionText(command));
        }

        string safetyDetail = CommandPaletteSafetyTextHelper.BuildDetailText(command);
        if (!string.IsNullOrWhiteSpace(safetyDetail))
        {
            lines.Add(safetyDetail);
        }

        if (command.ClearsSearchText)
        {
            lines.Add("カテゴリ一覧へ戻ります。");
            lines.Add("クリックでも戻れます。");
        }
        else if (!string.IsNullOrWhiteSpace(command.QueryInsertText))
        {
            lines.Add($"カテゴリ: {BuildUniversalScopeDisplayLabel(command)}");
            if (!string.IsNullOrWhiteSpace(command.SecondaryText))
            {
                lines.Add($"件数: {command.SecondaryText}");
            }
            if (!string.IsNullOrWhiteSpace(command.Description))
            {
                lines.Add(command.Description);
            }
            lines.Add("Enter: このカテゴリを開く");
            lines.Add("追加ワードで絞り込めます。");
        }
        else if (string.Equals(command.Category, "Category", StringComparison.OrdinalIgnoreCase))
        {
            lines.Add("番号キーまたはEnterでカテゴリを開けます。");
        }

        return string.Join(Environment.NewLine, lines.Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private string BuildUniversalSearchHintText()
    {
        string filter = _currentFilter.Trim();
        if (CommandPaletteUniversalSearchService.TryParseScope(filter, out CommandPaletteUniversalSearchService.UniversalSearchScopeResult? explicitScope) &&
            explicitScope is not null)
        {
            string explicitLabel = explicitScope.Scope switch
            {
                CommandPaletteUniversalSearchService.UniversalSearchScope.Tabs => "開いているタブ",
                CommandPaletteUniversalSearchService.UniversalSearchScope.Destinations => "移動先",
                CommandPaletteUniversalSearchService.UniversalSearchScope.Functions => "機能",
                CommandPaletteUniversalSearchService.UniversalSearchScope.Settings => "設定",
                _ => "カテゴリ"
            };

            if (string.IsNullOrWhiteSpace(explicitScope.NormalizedTail))
            {
                return string.Join(Environment.NewLine, new[]
                {
                    $"カテゴリ: {explicitLabel} >",
                    "追加ワードで絞り込めます。",
                    "戻る: カテゴリ一覧へ戻る"
                });
            }

            return string.Join(Environment.NewLine, new[]
            {
                $"{explicitLabel} > {explicitScope.NormalizedTail}",
                "Enter: 選択候補を実行",
                "戻る: カテゴリ一覧へ戻る"
            });
        }

        if (string.IsNullOrWhiteSpace(_selectedUniversalSearchScopePrefix))
        {
            return string.Join(Environment.NewLine, new[]
            {
                "カテゴリ選択ホーム",
                "1. タブを探す / 2. 移動先を探す / 3. 機能を探す / 4. 設定を探す",
                "Enter / 1-4 / クリック: カテゴリを開く",
                "追加ワードだけ入力して絞り込めます。",
                "例: png / MainForm / コピー / 設定"
            });
        }

        string scopeLabel = _selectedUniversalSearchScopeLabel ?? "カテゴリ";
        if (string.IsNullOrWhiteSpace(filter))
        {
            return string.Join(Environment.NewLine, new[]
            {
                $"カテゴリ: {scopeLabel} >",
                "追加ワードで絞り込めます。",
                "戻る: カテゴリ一覧へ戻る"
            });
        }

        return string.Join(Environment.NewLine, new[]
        {
            $"{scopeLabel} > {filter}",
            "Enter: 選択候補を実行",
            "戻る: カテゴリ一覧へ戻る"
        });
    }

    private static string BuildUniversalKindLabel(CommandLauncherCommand command)
    {
        if (command.ClearsSearchText)
        {
            return "戻る";
        }

        if (!string.IsNullOrWhiteSpace(command.LayerKind))
        {
            return command.LayerKind;
        }

        if (!string.IsNullOrWhiteSpace(command.QueryInsertText))
        {
            return "カテゴリ";
        }

        return command.Category switch
        {
            "Tab" => "Tab",
            "QuickAccess" => "QuickAccess",
            "Command" => "Command",
            "Setting" => "Setting",
            _ => command.Category
        };
    }

    private static string BuildUniversalScopeDisplayLabel(CommandLauncherCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.QueryInsertText))
        {
            return command.DisplayName;
        }

        return command.QueryInsertText.Trim() switch
        {
            "tab" => "開いているタブ",
            "q" => "移動先",
            "c" => "機能",
            "s" => "設定",
            _ => command.DisplayName
        };
    }

    private static string BuildLayerRootLabel(CommandLauncherCommand command)
    {
        return command.Category switch
        {
            "QuickAccess" => "Q: QuickAccess",
            "Mark" => "M: MarkSlot",
            "Archive" => "A: Archive",
            _ => string.Empty
        };
    }

    private static string BuildLayerCategoryLabel(string category)
    {
        return category switch
        {
            "QuickAccess" => "QuickAccess",
            "Mark" => "MarkSlot",
            "Archive" => "Archive",
            _ => category
        };
    }

    private static string BuildLayerEntryTypeLabel(CommandLauncherCommand command)
    {
        if (!string.IsNullOrWhiteSpace(command.NonExecutableMessage))
        {
            return command.NonExecutableMessage;
        }

        if (!string.IsNullOrWhiteSpace(command.LayerKind))
        {
            return command.LayerKind;
        }

        return command.Category switch
        {
            "QuickAccess" => BuildQuickAccessTypeLabel(command),
            "Mark" => "スロット復元",
            "Archive" when string.Equals(command.Id, "layer.archive.list", StringComparison.OrdinalIgnoreCase) => "一覧表示",
            "Archive" when string.Equals(command.Id, "layer.archive.test", StringComparison.OrdinalIgnoreCase) => "後続Phase対象",
            "Archive" => $"ハッシュ確認 / {BuildArchiveAlgorithmLabel(command)}",
            _ => "実行"
        };
    }

    private static string BuildQuickAccessTypeLabel(CommandLauncherCommand command)
    {
        string text = command.SecondaryText ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(text))
        {
            return text.EndsWith("\\", StringComparison.Ordinal) || text.EndsWith("/", StringComparison.Ordinal)
                ? "Folder"
                : "Path";
        }

        return "Bookmark / Alias / Recent / History";
    }

    private static string BuildArchiveAlgorithmLabel(CommandLauncherCommand command)
    {
        if (command.LayerBadge is null)
        {
            return "SHA256";
        }

        if (command.LayerBadge.Contains("SHA256", StringComparison.OrdinalIgnoreCase)) return "SHA256";
        if (command.LayerBadge.Contains("CRC32", StringComparison.OrdinalIgnoreCase)) return "CRC32";
        if (command.LayerBadge.Contains("SHA1", StringComparison.OrdinalIgnoreCase)) return "SHA1";
        if (command.LayerBadge.Contains("ALL", StringComparison.OrdinalIgnoreCase)) return "ALL";
        return "SHA256";
    }

    private static string BuildLayerTargetText(CommandLauncherCommand command)
    {
        if (command.Category == "Archive" &&
            string.Equals(command.Id, "layer.archive.test", StringComparison.OrdinalIgnoreCase))
        {
            return "後続Phase対象";
        }

        return command.Category switch
        {
            "QuickAccess" => command.SecondaryText ?? "未設定",
            "Mark" => command.SecondaryText ?? "未設定",
            "Archive" when string.Equals(command.Id, "layer.archive.list", StringComparison.OrdinalIgnoreCase) => command.SecondaryText ?? "対象なし",
            "Archive" => command.SecondaryText ?? "対象なし",
            _ => command.SecondaryText ?? command.Description ?? string.Empty
        };
    }

    private static string BuildLayerDescriptionText(CommandLauncherCommand command)
    {
        string inputText = BuildLayerSelectionText(command);
        string actionLine = !string.IsNullOrWhiteSpace(command.NonExecutableMessage)
            ? $"Enter: {command.NonExecutableMessage}"
            : command.Category switch
        {
            "QuickAccess" => string.IsNullOrWhiteSpace(inputText)
                ? "Enter: 選択した候補へ移動"
                : $"Enter: {inputText} を実行して移動",
            "Mark" => "Enter: 選択したスロットを復元",
            "Archive" when string.Equals(command.Id, "layer.archive.list", StringComparison.OrdinalIgnoreCase) => "Enter: アーカイブ一覧を開く",
            "Archive" when string.Equals(command.Id, "layer.archive.test", StringComparison.OrdinalIgnoreCase) => "Enter: 後続Phase対象",
            "Archive" => "Enter: 選択ファイルのハッシュを確認",
            _ => "Enter: 実行"
        };

        string notes = command.Category switch
        {
            "QuickAccess" => "Esc: 閉じる / Q 1 で候補を確定",
            "Mark" => "Esc: 閉じる / 保存・削除・export/import は deferred",
            "Archive" => "Esc: 閉じる / extract・compress・delete は deferred",
            _ => string.Empty
        };

        return string.Join(Environment.NewLine, new[] { actionLine, notes }.Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private string BuildExampleText(CommandLauncherCommand? command, string filter)
    {
        string[] lines = BuildExampleLines(command, filter);
        return string.Join(Environment.NewLine, lines.Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private string[] BuildExampleLines(CommandLauncherCommand? command, string filter)
    {
        CommandPaletteLayerQuery query = CommandPaletteLayerQueryParser.Parse(filter);
        if (CommandPaletteUniversalSearchService.TryParseScope(filter, out CommandPaletteUniversalSearchService.UniversalSearchScopeResult? explicitScope) &&
            explicitScope is not null)
        {
            string explicitLabel = explicitScope.Scope switch
            {
                CommandPaletteUniversalSearchService.UniversalSearchScope.Tabs => "開いているタブ",
                CommandPaletteUniversalSearchService.UniversalSearchScope.Destinations => "移動先",
                CommandPaletteUniversalSearchService.UniversalSearchScope.Functions => "機能",
                CommandPaletteUniversalSearchService.UniversalSearchScope.Settings => "設定",
                _ => "カテゴリ"
            };

            if (string.IsNullOrWhiteSpace(explicitScope.NormalizedTail))
            {
                return new[]
                {
                    "入力例",
                    $"{explicitLabel} > 追加ワード",
                    "png / MainForm / コピー / 設定"
                };
            }

            return new[]
            {
                "入力例",
                $"{explicitLabel} > {explicitScope.NormalizedTail}",
                "Enter: 選択候補を実行",
                "戻る: カテゴリ一覧へ戻る"
            };
        }

        string root = command?.Category switch
        {
            "QuickAccess" => "Q",
            "Mark" => "M",
            "Archive" => "A",
            _ => query.RootToken
        };

        if (command?.QueryInsertText is not null)
        {
            string scopeLabel = BuildUniversalScopeDisplayLabel(command);
            string[] categoryLines =
            {
                "入力例",
                $"{scopeLabel}",
                "追加ワードだけ入力して絞り込みます。",
                command.Description ?? string.Empty,
                "1-4 / Enter / クリックでカテゴリを開く"
            };

            return categoryLines;
        }

        if (command?.ClearsSearchText == true)
        {
            return new[]
            {
                "入力例",
                "Enter / クリック: カテゴリ一覧へ戻る"
            };
        }

        if (!string.IsNullOrWhiteSpace(_selectedUniversalSearchScopeLabel))
        {
            if (string.IsNullOrWhiteSpace(filter))
            {
                return new[]
                {
                    "入力例",
                    $"{_selectedUniversalSearchScopeLabel} > 追加ワード",
                    "png / MainForm / コピー / 設定"
                };
            }

            return new[]
            {
                "入力例",
                $"{_selectedUniversalSearchScopeLabel} > {filter}",
                "Enter: 選択候補を実行",
                "戻る: カテゴリ一覧へ戻る"
            };
        }

        if (string.IsNullOrWhiteSpace(root))
        {
            return new[]
            {
                "入力例",
                "Q : QuickAccess",
                "M : MarkSlot",
                "A : Archive",
                "1-4 : ホームのカテゴリを開く",
                "tab png / t MainForm",
                "q mid / quick ymm4",
                "c コピー / command 設定",
                "s / setting / 設定"
            };
        }

        return root switch
        {
            "Q" => new[]
            {
                "入力例",
                "Q : QuickAccess",
                "Q 1 / Q1   : 1番候補へ移動",
                "Q R / QR   : 最近へ移動",
                "Q H / QH   : 履歴へ移動"
            },
            "M" => new[]
            {
                "入力例",
                "M : MarkSlot",
                "M 1 / M1   : Slot1を復元",
                "M R 1 / MR1: スロット1を復元",
                "M S 1 / MS1: 保存系候補"
            },
            "A" => new[]
            {
                "入力例",
                "A : Archive",
                "A L / AL   : 一覧を表示",
                "A S / AS   : SHA候補を表示",
                "A H SHA256 / AHSHA256: SHA256を表示",
                "A H CRC32 / AHCRC32: CRC32を表示",
                "A T / AT   : 後続Phase対象"
            },
            _ => new[]
            {
                "入力例",
                "Q : QuickAccess",
                "M : MarkSlot",
                "A : Archive",
                "tab png / t MainForm",
                "q mid / quick ymm4",
                "c コピー / command 設定",
                "s / setting / 設定"
            }
        };
    }

    private static string BuildLayerShortDetailText(CommandLauncherCommand command)
    {
        string target = BuildLayerTargetText(command);
        return command.Category switch
        {
            "QuickAccess" => $"移動先: {TruncateLayerPath(target, 36)}",
            "Mark" => $"復元元: {TruncateLayerPath(target, 28)}",
            "Archive" when string.Equals(command.Id, "layer.archive.list", StringComparison.OrdinalIgnoreCase) => $"対象: {TruncateLayerPath(target, 28)}",
            "Archive" when string.Equals(command.Id, "layer.archive.test", StringComparison.OrdinalIgnoreCase) => "後続Phase対象",
            "Archive" => $"対象: {TruncateLayerPath(target, 28)}",
            _ => target
        };
    }

    private static string BuildUniversalSectionedDetailText(PaletteListItem item, CommandLauncherCommand command)
    {
        if (!string.IsNullOrWhiteSpace(command.NonExecutableMessage))
        {
            return BuildAttentionText(command);
        }

        return command.Category switch
        {
            "Tab" => string.IsNullOrWhiteSpace(command.SecondaryText)
                ? "切り替え"
                : $"Path: {TruncateLayerPath(command.SecondaryText, 34)}",
            "QuickAccess" => string.IsNullOrWhiteSpace(command.SecondaryText)
                ? "現在タブで開く"
                : $"Path: {TruncateLayerPath(command.SecondaryText, 34)}",
            "Command" => string.IsNullOrWhiteSpace(command.Description)
                ? "機能"
                : TruncateLayerDetail(command.Description, 36),
            "Setting" => string.IsNullOrWhiteSpace(command.Description)
                ? "設定"
                : TruncateLayerDetail(command.Description, 36),
            _ => item.SectionTitle ?? string.Empty
        };
    }

    private static string BuildLayerActionText(CommandLauncherCommand command)
    {
        if (!string.IsNullOrWhiteSpace(command.NonExecutableMessage))
        {
            return BuildAttentionText(command);
        }

        return command.Category switch
        {
            "QuickAccess" => "移動",
            "Mark" => "復元",
            "Archive" when string.Equals(command.Id, "layer.archive.list", StringComparison.OrdinalIgnoreCase) => "一覧",
            "Archive" when string.Equals(command.Id, "layer.archive.test", StringComparison.OrdinalIgnoreCase) => "後続Phase対象",
            "Archive" => "計算",
            _ => "実行"
        };
    }

    private static string BuildLayerDetailText(CommandLauncherCommand command)
    {
        if (command.Category == "QuickAccess")
        {
            if (!string.IsNullOrWhiteSpace(command.SecondaryText))
            {
                return $"移動先: {command.SecondaryText}";
            }

            return "移動先: 未設定";
        }

        if (command.Category == "Mark")
        {
            return string.IsNullOrWhiteSpace(command.SecondaryText)
                ? "復元元: 未設定"
                : $"復元元: {command.SecondaryText}";
        }

        if (command.Category == "Archive")
        {
            if (string.Equals(command.Id, "layer.archive.list", StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrWhiteSpace(command.SecondaryText)
                    ? "対象: 対象なし"
                    : $"対象: {command.SecondaryText}";
            }

            if (string.Equals(command.Id, "layer.archive.test", StringComparison.OrdinalIgnoreCase))
            {
                return "後続Phase対象";
            }

            return string.IsNullOrWhiteSpace(command.SecondaryText)
                ? "対象: 対象なし"
                : $"対象: {command.SecondaryText}";
        }

        if (!string.IsNullOrWhiteSpace(command.Description) &&
            !string.Equals(command.Description, BuildLayerActionText(command), StringComparison.OrdinalIgnoreCase))
        {
            return command.Description;
        }

        return string.Empty;
    }

    private static string BuildNonExecutableFeedback(CommandLauncherCommand command)
    {
        return BuildAttentionText(command);
    }

    private static string BuildNonExecutableDetailText(CommandLauncherCommand command)
    {
        bool isMarkSlotSave = command.Category == "Mark" &&
            command.Id.StartsWith("layer.markslot.save.", StringComparison.OrdinalIgnoreCase);
        bool isArchiveTest = command.Category == "Archive" &&
            string.Equals(command.Id, "layer.archive.test", StringComparison.OrdinalIgnoreCase);
        bool isArchiveList = command.Category == "Archive" &&
            string.Equals(command.Id, "layer.archive.list", StringComparison.OrdinalIgnoreCase);
        bool isArchiveHash = command.Category == "Archive" &&
            command.Id.StartsWith("layer.archive.hash.", StringComparison.OrdinalIgnoreCase);

        string title = isArchiveTest ? "⚠ 現在は実行できません" : "⚠ 実行できません";
        string reason = !string.IsNullOrWhiteSpace(command.NonExecutableMessage)
            ? command.NonExecutableMessage
            : "現在は実行できません。";
        string action = isArchiveTest
            ? "対処: 今回のPhaseでは実行できません。"
            : isMarkSlotSave
                ? "対処: 今回のPhaseでは保存できません。"
            : isArchiveList
                ? "対処: ZIPなどのアーカイブファイルを選択してください。"
                : isArchiveHash
                    ? $"対処: ファイルを選択してから {command.LayerBadge ?? "A H"} を実行してください。"
                    : "対処: 選択内容を確認してください。";

        return string.Join(Environment.NewLine, new[]
        {
            title,
            $"理由: {reason}",
            action,
            $"入力: {BuildLayerSelectionText(command)}"
        });
    }

    private void ShowNonExecutableFeedback(CommandLauncherCommand command)
    {
        string message = command.NonExecutableMessage ?? "現在は実行できません。";
        string title = command.Category == "Archive" && string.Equals(command.Id, "layer.archive.test", StringComparison.OrdinalIgnoreCase)
            ? "現在は実行できません"
            : "実行できません";

        string attention = BuildNonExecutableFeedback(command);
        _detailAttentionLabel.Visible = true;
        _detailAttentionLabel.Text = attention;
        _detailActionLabel.Text = $"実行: {attention}";
        _statusLabel.Text = attention;

        MessageBox.Show(this, message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        if (IsHandleCreated && !_searchBox.IsDisposed)
        {
            BeginInvoke(new Action(() => _searchBox.Focus()));
        }
    }

    private bool ShowCommandPaletteConfirm(CommandLauncherCommand command)
    {
        string title = command.DisplayName ?? command.Title ?? "確認";
        string body = CommandPaletteSafetyTextHelper.BuildDetailText(command, command.Description ?? command.Subtitle);
        return CommandPaletteConfirmDialog.Show(this, title, body, command.SafetyInfo.IsDestructive);
    }

    private static string BuildAttentionText(CommandLauncherCommand command)
    {
        string message = !string.IsNullOrWhiteSpace(command.NonExecutableMessage)
            ? command.NonExecutableMessage
            : "現在は実行できません。";

        return command.Category == "Archive" && string.Equals(command.Id, "layer.archive.test", StringComparison.OrdinalIgnoreCase)
            ? $"⚠ 現在は実行できません: {message}"
            : $"⚠ 実行できません: {message}";
    }

    private static string BuildLayerSelectionText(CommandLauncherCommand command)
    {
        string badge = command.LayerBadge ?? string.Empty;
        string title = command.DisplayName ?? string.Empty;
        return string.Join(" ", new[] { badge, title }.Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private void UpdateDetailPane()
    {
        if (!_currentPresentation.IsLayered)
        {
            if (_currentPresentation.HasSections)
            {
                if (_currentPresentation.IsSectioned)
                {
                    UpdateUniversalSectionedDetailPane();
                }
                else
                {
                    UpdateMixedDetailPane();
                }
                return;
            }

            UpdateStandardHelpPane();
            return;
        }

        if (_commandListBox.SelectedItem is not PaletteListItem { IsHeader: false, Command: { } command })
        {
            SetLayerDetailExpanded(true);
            _detailAttentionLabel.Visible = false;
            _detailAttentionLabel.Text = string.Empty;
            _detailLayerLabel.Text = "Layer: -";
            _detailTypeLabel.Text = "種別: -";
            _detailActionLabel.Text = "実行: -";
            _detailTargetLabel.Text = "対象: -";
            _detailInputLabel.Text = "入力: -";
            _detailDescriptionBox.Text = string.Empty;
            _detailExampleBox.Text = BuildExampleText(null, _currentFilter);
            return;
        }

        if (!string.IsNullOrWhiteSpace(command.NonExecutableMessage))
        {
            UpdateNonExecutableDetailPane(command);
            return;
        }

        SetLayerDetailExpanded(true);
        _detailLayerLabel.Text = $"Layer: {BuildLayerCategoryLabel(command.Category)}";
        _detailTypeLabel.Text = $"種別: {BuildLayerEntryTypeLabel(command)}";
        _detailActionLabel.Text = $"実行: {BuildLayerActionText(command)}";
        _detailTargetLabel.Text = $"対象: {BuildLayerTargetText(command)}";
        _detailInputLabel.Text = $"入力: {BuildLayerSelectionText(command)}";
        _detailDescriptionBox.Text = BuildLayerDescriptionText(command);
        _detailExampleBox.Text = BuildExampleText(command, _currentFilter);
        _detailAttentionLabel.Visible = false;
        _detailAttentionLabel.Text = string.Empty;
    }

    private void UpdateMixedDetailPane()
    {
        if (_commandListBox.SelectedItem is not PaletteListItem selected)
        {
            SetLayerDetailExpanded(true);
            _detailAttentionLabel.Visible = false;
            _detailAttentionLabel.Text = string.Empty;
            _detailLayerLabel.Text = "Section: Layer候補 / 通常検索";
            _detailTypeLabel.Text = "種別: mixed mode";
            _detailActionLabel.Text = "実行: Enterで選択コマンドを実行";
            _detailTargetLabel.Text = "対象: layer候補と通常検索候補";
            _detailInputLabel.Text = $"入力: {_currentFilter}";
            _detailDescriptionBox.Text = "Layer候補と通常検索候補を同じ一覧で分けて表示します。";
            _detailExampleBox.Text = BuildExampleText(null, _currentFilterRaw);
            return;
        }

        if (selected.IsSectionHeader)
        {
            SetLayerDetailExpanded(true);
            _detailAttentionLabel.Visible = false;
            _detailAttentionLabel.Text = string.Empty;
            _detailLayerLabel.Text = $"Section: {selected.HeaderText}";
            _detailTypeLabel.Text = selected.HeaderText == "Layer候補"
                ? "種別: layer候補の区切り"
                : "種別: 通常検索の区切り";
            _detailActionLabel.Text = "実行: Enterで実行されません";
            _detailTargetLabel.Text = selected.HeaderText == "Layer候補"
                ? "対象: layer候補"
                : "対象: 通常検索候補";
            _detailInputLabel.Text = $"入力: {_currentFilter}";
            _detailDescriptionBox.Text = selected.HeaderText == "Layer候補"
                ? "layer候補をまとめて表示しています。"
                : "通常検索候補をまとめて表示しています。";
            _detailExampleBox.Text = BuildExampleText(null, _currentFilterRaw);
            return;
        }

        if (selected.Command is not { } command)
        {
            SetLayerDetailExpanded(true);
            _detailAttentionLabel.Visible = false;
            _detailAttentionLabel.Text = string.Empty;
            _detailLayerLabel.Text = "Section: -";
            _detailTypeLabel.Text = "種別: -";
            _detailActionLabel.Text = "実行: -";
            _detailTargetLabel.Text = "対象: -";
            _detailInputLabel.Text = $"入力: {_currentFilter}";
            _detailDescriptionBox.Text = string.Empty;
            _detailExampleBox.Text = BuildExampleText(null, _currentFilterRaw);
            return;
        }

        SetLayerDetailExpanded(true);
        _detailLayerLabel.Text = $"Section: {selected.SectionTitle ?? "通常検索"}";
        if (selected.SectionTitle == "Layer候補")
        {
            _detailTypeLabel.Text = $"種別: {BuildLayerEntryTypeLabel(command)}";
            _detailActionLabel.Text = $"実行: {BuildLayerActionText(command)}";
            _detailTargetLabel.Text = $"対象: {BuildLayerTargetText(command)}";
            _detailInputLabel.Text = $"入力: {BuildLayerSelectionText(command)}";
            _detailDescriptionBox.Text = BuildLayerDescriptionText(command);
            _detailExampleBox.Text = BuildExampleText(command, _currentFilterRaw);
        }
        else
        {
            _detailTypeLabel.Text = "種別: 通常検索";
            _detailActionLabel.Text = "実行: Enterで選択コマンドを実行";
            _detailTargetLabel.Text = $"対象: {command.Category}";
            _detailInputLabel.Text = $"入力: {_currentFilter}";
            _detailDescriptionBox.Text = BuildStandardCommandDetailText(command);
            _detailExampleBox.Text = BuildExampleText(null, _currentFilterRaw);
        }

        _detailAttentionLabel.Visible = false;
        _detailAttentionLabel.Text = string.Empty;
    }

    private static string NormalizeUniversalKind(CommandLauncherCommand command)
    {
        // command.Kind の内部値を日本語表示に正規化する
        if (!string.IsNullOrWhiteSpace(command.Kind))
        {
            return command.Kind switch
            {
                "Function" or "Command" or "機能" => "機能",
                "Setting" or "設定" => "設定",
                "Management" or "Admin" or "管理" => "管理",
                _ => command.Kind
            };
        }

        // Kind が未設定の場合は Category から推定
        return command.Category switch
        {
            "Command" => "機能",
            "Setting" => "設定",
            "Tab" => "タブ",
            "QuickAccess" => "移動先",
            "Category" => "カテゴリ",
            _ => command.Category ?? "機能"
        };
    }

    private void UpdateUniversalSectionedDetailPane()
    {
        // no selection / section header / more row のケースは専用パネルを隠す簡易表示
        if (_commandListBox.SelectedItem is not PaletteListItem selected)
        {
            SetUniversalSectionedDetailVisibility(showFullDetail: false);
            _detailTypeLabel.Text = "-";
            _detailLayerLabel.Text = "-";
            return;
        }

        if (selected.IsSectionHeader)
        {
            SetUniversalSectionedDetailVisibility(showFullDetail: false);
            _detailTypeLabel.Text = selected.HeaderText;
            _detailLayerLabel.Text = "グループ";
            return;
        }

        if (selected.IsMoreRow)
        {
            SetUniversalSectionedDetailVisibility(showFullDetail: false);
            _detailTypeLabel.Text = (selected.HeaderText ?? string.Empty).Replace("残り", "さらに");
            _detailLayerLabel.Text = "表示";
            _detailDescriptionBox.Text = "クリックまたはEnterで残りのすべての候補を展開します。";
            _detailDescriptionBox.Visible = true;
            _detailStack.RowStyles[4] = new RowStyle(SizeType.AutoSize);
            return;
        }

        if (selected.Command is not { } command)
        {
            SetUniversalSectionedDetailVisibility(showFullDetail: false);
            _detailTypeLabel.Text = "-";
            _detailLayerLabel.Text = "-";
            return;
        }

        // 通常候補: 4-block 詳細表示
        SetUniversalSectionedDetailVisibility(showFullDetail: true);

        // [種類] caption は _universalKindCaptionLabel（固定テキスト"種類"）
        // 種類 値: _detailLayerLabel
        _detailLayerLabel.Text = NormalizeUniversalKind(command);
        _detailLayerLabel.Font = new Font(FontFamily.GenericSansSerif, 10F, FontStyle.Regular);
        _detailLayerLabel.BackColor = Color.Transparent;
        _detailLayerLabel.ForeColor = Color.FromArgb(50, 50, 50);
        _detailLayerLabel.Padding = new Padding(4, 2, 4, 2);
        _detailLayerLabel.Margin = new Padding(0, 2, 0, 4);

        // [名前] caption は _universalNameCaptionLabel（固定テキスト"名前"）
        // 名前 値: _detailTypeLabel
        _detailTypeLabel.Text = command.Title ?? command.DisplayName;
        _detailTypeLabel.Font = new Font(FontFamily.GenericSansSerif, 11F, FontStyle.Bold);
        _detailTypeLabel.ForeColor = Color.FromArgb(20, 20, 20);
        _detailTypeLabel.BackColor = Color.Transparent;
        _detailTypeLabel.Padding = new Padding(4, 2, 4, 2);
        _detailTypeLabel.Margin = new Padding(0, 2, 0, 2);

        string inlineStatusText = CommandPaletteSafetyTextHelper.BuildInlineStatusText(command);
        _detailAttentionLabel.Text = inlineStatusText;
        _detailAttentionLabel.Visible = !string.IsNullOrWhiteSpace(inlineStatusText) &&
            command.SafetyLevel != CommandPaletteSafetyLevel.Safe;

        // 説明文（名前の下）
        string? desc = !string.IsNullOrWhiteSpace(command.Description)
            ? command.Description
            : command.Subtitle;
        if (!string.IsNullOrWhiteSpace(desc))
        {
            _detailDescriptionBox.Text = desc;
            _detailDescriptionBox.Visible = true;
            _detailDescriptionBox.Margin = new Padding(4, 0, 4, 4);
            _detailStack.RowStyles[6] = new RowStyle(SizeType.AutoSize);
        }
        else
        {
            _detailDescriptionBox.Text = string.Empty;
            _detailDescriptionBox.Visible = false;
            _detailStack.RowStyles[6] = new RowStyle(SizeType.Absolute, 0);
        }

        string safetyDetailText = CommandPaletteSafetyTextHelper.BuildDetailText(command);
        if (!string.IsNullOrWhiteSpace(safetyDetailText))
        {
            if (_detailDescriptionBox.Visible && !string.IsNullOrWhiteSpace(_detailDescriptionBox.Text))
            {
                _detailDescriptionBox.Text += Environment.NewLine + Environment.NewLine + safetyDetailText;
            }
            else
            {
                _detailDescriptionBox.Text = safetyDetailText;
                _detailDescriptionBox.Visible = true;
                _detailStack.RowStyles[6] = new RowStyle(SizeType.AutoSize);
            }
        }

        // [キー] caption は _detailTargetLabel
        // キーbadge: _detailKeyContainer
        PopulateKeyBadges(command.KeyBindingText);

        // [Enter] caption は _detailActionLabel
        // Enter値: _universalEnterValueLabel（PopulateActionGuides不使用）
        string actionText = BuildUniversalActionText(command);
        if (actionText.StartsWith("Enter: ", StringComparison.OrdinalIgnoreCase))
        {
            actionText = actionText["Enter: ".Length..];
        }
        _universalEnterValueLabel.Text = actionText;

        if (command.SafetyLevel == CommandPaletteSafetyLevel.Safe)
        {
            _detailAttentionLabel.Visible = false;
        }
    }

    private void UpdateNonExecutableDetailPane(CommandLauncherCommand command)
    {
        SetLayerDetailExpanded(false);
        _detailAttentionLabel.Visible = true;
        _detailAttentionLabel.Text = BuildAttentionText(command);
        _detailDescriptionBox.Text = BuildNonExecutableDetailText(command);
        _detailExampleBox.Text = string.Empty;
    }

    private void UpdateStandardHelpPane()
    {
        if (_commandListBox.SelectedItem is PaletteListItem { IsHeader: false, Command: { } command })
        {
            SetLayerDetailExpanded(true);
            _detailAttentionLabel.Visible = false;
            _detailAttentionLabel.Text = string.Empty;
            _detailLayerLabel.Text = "Layer: Command Palette";
            _detailTypeLabel.Text = "種別: 通常検索 / Layer入口";
            _detailActionLabel.Text = "実行: Enterで選択コマンドを実行";
            _detailTargetLabel.Text = "対象: コマンド検索";
            _detailInputLabel.Text = "入力: Q / M / A で layer";
            _detailDescriptionBox.Text = BuildStandardCommandDetailText(command);
        }
        else
        {
            SetLayerDetailExpanded(true);
            _detailAttentionLabel.Visible = false;
            _detailAttentionLabel.Text = string.Empty;
            _detailLayerLabel.Text = "Layer: Command Palette";
            _detailTypeLabel.Text = "種別: 使い方";
            _detailActionLabel.Text = "実行: Enterで選択コマンドを実行";
            _detailTargetLabel.Text = "対象: コマンド検索";
            _detailInputLabel.Text = "入力: Q / M / A で layer";
            _detailDescriptionBox.Text = BuildStandardHelpText(_currentFilter);
        }

        _detailExampleBox.Text = BuildExampleText(null, _currentFilter);
    }

    private void SetLayerDetailExpanded(bool visible)
    {
        // Standard/Layered モード用: Universal Sectioned で変更した行配置を元に戻す
        _detailStack.SetRow(_detailTypeLabel, 2);
        _detailStack.SetRow(_detailLayerLabel, 3);
        _detailStack.SetRow(_detailDescriptionBox, 4);
        _detailStack.SetRow(_detailTargetLabel, 5);
        _detailStack.SetRow(_detailKeyContainer, 6);
        _detailStack.SetRow(_detailActionLabel, 7);
        _detailStack.SetRow(_detailActionContainer, 8);
        _detailStack.SetRow(_detailInputLabel, 9);
        _detailStack.SetRow(_detailExampleBox, 13);

        // Universal Sectioned 専用ラベルは非表示に戻す
        _universalKindCaptionLabel.Visible = false;
        _universalNameCaptionLabel.Visible = false;
        _universalEnterValueLabel.Visible = false;

        _detailLayerLabel.Visible = visible;
        _detailTypeLabel.Visible = visible;
        _detailActionLabel.Visible = visible;
        _detailTargetLabel.Visible = visible;
        _detailInputLabel.Visible = visible;
        _detailDescriptionBox.Visible = visible;
        _detailExampleBox.Visible = visible;
        _detailKeyContainer.Visible = visible;
        _detailActionContainer.Visible = visible;

        if (_detailStack != null)
        {
            _detailStack.RowStyles[1] = _detailAttentionLabel.Visible ? new RowStyle(SizeType.AutoSize) : new RowStyle(SizeType.Absolute, 0);
            _detailStack.RowStyles[2] = visible ? new RowStyle(SizeType.AutoSize) : new RowStyle(SizeType.Absolute, 0);
            _detailStack.RowStyles[3] = visible ? new RowStyle(SizeType.AutoSize) : new RowStyle(SizeType.Absolute, 0);
            _detailStack.RowStyles[4] = visible ? new RowStyle(SizeType.AutoSize) : new RowStyle(SizeType.Absolute, 0);
            _detailStack.RowStyles[5] = visible ? new RowStyle(SizeType.AutoSize) : new RowStyle(SizeType.Absolute, 0);
            _detailStack.RowStyles[6] = visible ? new RowStyle(SizeType.AutoSize) : new RowStyle(SizeType.Absolute, 0);
            _detailStack.RowStyles[7] = visible ? new RowStyle(SizeType.AutoSize) : new RowStyle(SizeType.Absolute, 0);
            _detailStack.RowStyles[8] = visible ? new RowStyle(SizeType.AutoSize) : new RowStyle(SizeType.Absolute, 0);
            _detailStack.RowStyles[9] = visible ? new RowStyle(SizeType.AutoSize) : new RowStyle(SizeType.Absolute, 0);
            // row10-12 は Universal Sectioned 専用ラベル用（Standard時は非表示）
            _detailStack.RowStyles[10] = new RowStyle(SizeType.Absolute, 0);
            _detailStack.RowStyles[11] = new RowStyle(SizeType.Absolute, 0);
            _detailStack.RowStyles[12] = new RowStyle(SizeType.Absolute, 0);
            // row13: _detailExampleBox（Standard/Layeredでは124固定）
            _detailStack.RowStyles[13] = visible ? new RowStyle(SizeType.Absolute, 124) : new RowStyle(SizeType.Absolute, 0);
        }
    }

    private void SetUniversalSectionedDetailVisibility(bool showFullDetail)
    {
        // Universal Sectioned 専用: Standard/Layered で使うラベルを Universal 用に再配置
        // 行配置（Universal Sectioned モード）:
        //  row2: _universalKindCaptionLabel [種類 caption]
        //  row3: _detailLayerLabel          [種類 値]
        //  row4: _universalNameCaptionLabel  [名前 caption]
        //  row5: _detailTypeLabel            [名前 値]
        //  row6: _detailDescriptionBox       [説明文（任意）]
        //  row7: _detailTargetLabel          [キー caption]
        //  row8: _detailKeyContainer         [キーbadge]
        //  row9: _detailActionLabel          [Enter caption]
        //  row10: _universalEnterValueLabel  [Enter値]
        //  row11-13: 非表示

        // 専用固定captionラベルを正しい行に配置
        _detailStack.SetRow(_universalKindCaptionLabel, 2);
        _detailStack.SetRow(_detailLayerLabel, 3);
        _detailStack.SetRow(_universalNameCaptionLabel, 4);
        _detailStack.SetRow(_detailTypeLabel, 5);
        _detailStack.SetRow(_detailDescriptionBox, 6);
        _detailStack.SetRow(_detailTargetLabel, 7);
        _detailStack.SetRow(_detailKeyContainer, 8);
        _detailStack.SetRow(_detailActionLabel, 9);
        _detailStack.SetRow(_universalEnterValueLabel, 10);
        _detailStack.SetRow(_detailActionContainer, 11);
        _detailStack.SetRow(_detailInputLabel, 12);
        _detailStack.SetRow(_detailExampleBox, 13);

        // caption スタイルを専用ラベルに適用
        _detailTargetLabel.Text = "キー";
        _detailTargetLabel.Font = new Font(FontFamily.GenericSansSerif, 9F, FontStyle.Bold);
        _detailTargetLabel.ForeColor = Color.FromArgb(30, 60, 90);
        _detailTargetLabel.BackColor = Color.FromArgb(232, 240, 250);
        _detailTargetLabel.Padding = new Padding(6, 3, 6, 3);
        _detailTargetLabel.Margin = new Padding(0, 8, 0, 2);

        _detailActionLabel.Text = "Enter";
        _detailActionLabel.Font = new Font(FontFamily.GenericSansSerif, 9F, FontStyle.Bold);
        _detailActionLabel.ForeColor = Color.FromArgb(30, 60, 90);
        _detailActionLabel.BackColor = Color.FromArgb(232, 240, 250);
        _detailActionLabel.Padding = new Padding(6, 3, 6, 3);
        _detailActionLabel.Margin = new Padding(0, 8, 0, 2);

        // 全detail非表示のケース（no selection / section header / more row 等）
        bool fullOn = showFullDetail;
        _universalKindCaptionLabel.Visible = fullOn;
        _detailLayerLabel.Visible = true;   // 種類値は常時表示（section header時も使う）
        _universalNameCaptionLabel.Visible = fullOn;
        _detailTypeLabel.Visible = true;    // 名前値も常時表示
        _detailDescriptionBox.Visible = false;  // 説明文は UpdateUniversalSectionedDetailPane で制御
        _detailTargetLabel.Visible = fullOn;
        _detailKeyContainer.Visible = fullOn;
        _detailActionLabel.Visible = fullOn;
        _universalEnterValueLabel.Visible = fullOn;
        _detailActionContainer.Visible = false; // Universal Sectioned では使わない
        _detailInputLabel.Visible = false;
        _detailExampleBox.Visible = false;
        _detailAttentionLabel.Visible = false;

        _detailStack.RowStyles[1] = new RowStyle(SizeType.Absolute, 0);  // attention
        _detailStack.RowStyles[2] = fullOn ? new RowStyle(SizeType.AutoSize) : new RowStyle(SizeType.Absolute, 0);  // 種類 caption
        _detailStack.RowStyles[3] = new RowStyle(SizeType.AutoSize);     // 種類 値（常時）
        _detailStack.RowStyles[4] = fullOn ? new RowStyle(SizeType.AutoSize) : new RowStyle(SizeType.Absolute, 0);  // 名前 caption
        _detailStack.RowStyles[5] = new RowStyle(SizeType.AutoSize);     // 名前 値（常時）
        _detailStack.RowStyles[6] = new RowStyle(SizeType.Absolute, 0);  // 説明文（後で制御）
        _detailStack.RowStyles[7] = fullOn ? new RowStyle(SizeType.AutoSize) : new RowStyle(SizeType.Absolute, 0);  // キー caption
        _detailStack.RowStyles[8] = fullOn ? new RowStyle(SizeType.AutoSize) : new RowStyle(SizeType.Absolute, 0);  // キーbadge
        _detailStack.RowStyles[9] = fullOn ? new RowStyle(SizeType.AutoSize) : new RowStyle(SizeType.Absolute, 0);  // Enter caption
        _detailStack.RowStyles[10] = fullOn ? new RowStyle(SizeType.AutoSize) : new RowStyle(SizeType.Absolute, 0); // Enter値
        _detailStack.RowStyles[11] = new RowStyle(SizeType.Absolute, 0); // actionContainer（不使用）
        _detailStack.RowStyles[12] = new RowStyle(SizeType.Absolute, 0); // inputLabel
        _detailStack.RowStyles[13] = new RowStyle(SizeType.Absolute, 0); // exampleBox
    }

    private static Label CreateKeyCapLabel(string text, bool isHighlight)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            Font = new Font(FontFamily.GenericSansSerif, 9F, FontStyle.Bold),
            BackColor = isHighlight ? Color.FromArgb(235, 240, 246) : Color.Transparent,
            ForeColor = isHighlight ? Color.FromArgb(50, 75, 110) : Color.FromArgb(140, 140, 140),
            BorderStyle = isHighlight ? BorderStyle.FixedSingle : BorderStyle.None,
            Padding = new Padding(5, 2, 5, 2),
            Margin = new Padding(0, 0, 4, 4),
            TextAlign = ContentAlignment.MiddleCenter
        };
    }

    private void PopulateKeyBadges(string? keyBindingText)
    {
        _detailKeyContainer.Controls.Clear();
        if (string.IsNullOrWhiteSpace(keyBindingText))
        {
            _detailKeyContainer.Controls.Add(CreateKeyCapLabel("未割り当て", false));
            return;
        }

        var combos = keyBindingText.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < combos.Length; i++)
        {
            if (i > 0)
            {
                _detailKeyContainer.Controls.Add(new Label
                {
                    Text = " / ",
                    AutoSize = true,
                    Font = new Font(FontFamily.GenericSansSerif, 9.5F, FontStyle.Regular),
                    ForeColor = Color.FromArgb(140, 140, 140),
                    Margin = new Padding(2, 2, 4, 4)
                });
            }

            var keys = combos[i].Trim().Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
            for (int j = 0; j < keys.Length; j++)
            {
                if (j > 0)
                {
                    _detailKeyContainer.Controls.Add(new Label
                    {
                        Text = "+",
                        AutoSize = true,
                        Font = new Font(FontFamily.GenericSansSerif, 9F, FontStyle.Bold),
                        ForeColor = Color.FromArgb(160, 160, 160),
                        Margin = new Padding(2, 2, 4, 4)
                    });
                }
                _detailKeyContainer.Controls.Add(CreateKeyCapLabel(keys[j].Trim(), true));
            }
        }
    }

    private void PopulateActionGuides(string actionDescription)
    {
        _detailActionContainer.Controls.Clear();

        // 1. Enter 行
        var enterPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 0, 0, 4),
            Padding = new Padding(0)
        };
        enterPanel.Controls.Add(CreateKeyCapLabel("Enter", true));
        enterPanel.Controls.Add(new Label
        {
            Text = actionDescription,
            AutoSize = true,
            Font = new Font(FontFamily.GenericSansSerif, 9.5F, FontStyle.Regular),
            ForeColor = Color.FromArgb(70, 70, 70),
            Margin = new Padding(4, 2, 0, 0)
        });
        _detailActionContainer.Controls.Add(enterPanel);

        // 2. Esc 行
        var escPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 0, 0, 4),
            Padding = new Padding(0)
        };
        escPanel.Controls.Add(CreateKeyCapLabel("Esc", true));
        escPanel.Controls.Add(new Label
        {
            Text = "閉じる",
            AutoSize = true,
            Font = new Font(FontFamily.GenericSansSerif, 9.5F, FontStyle.Regular),
            ForeColor = Color.FromArgb(70, 70, 70),
            Margin = new Padding(4, 2, 0, 0)
        });
        _detailActionContainer.Controls.Add(escPanel);
    }

    private static string BuildStandardHelpText(string filter)
    {
        CommandPaletteLayerQuery query = CommandPaletteLayerQueryParser.Parse(filter);
        string[] lines = query.IsLayered
            ? new[]
            {
                "使い方",
                "文字入力 : コマンド検索",
                "Enter : 選択コマンドを実行"
            }
            : new[]
            {
                "使い方",
                "文字入力 : コマンド検索",
                "Enter : 選択コマンドを実行",
                "Q / M / A : layer 入口"
            };

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildStandardCommandDetailText(CommandLauncherCommand command)
    {
        var lines = new List<string>();
        string status = BuildStatusText(command);
        if (!string.IsNullOrWhiteSpace(status))
        {
            lines.Add(status);
        }

        lines.Add("Enter: 選択コマンドを実行");
        lines.Add("Q / M / A で layer");
        return string.Join(Environment.NewLine, lines);
    }

    private static Label CreateDetailLabel()
    {
        return new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6),
            Font = new Font(FontFamily.GenericSansSerif, 10),
            Text = string.Empty
        };
    }

    private static int GetLayerDisplayRank(CommandLauncherCommand command, CommandPaletteLayerQuery query)
    {
        if (!query.IsLayered)
        {
            return 0;
        }

        int rank = 0;
        string badge = command.LayerBadge ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(badge) &&
            string.Equals(badge, string.Join(" ", query.Tokens), StringComparison.OrdinalIgnoreCase))
        {
            rank += 100;
        }

        if (!string.IsNullOrWhiteSpace(badge) &&
            query.Tokens.Count > 1 &&
            badge.StartsWith(query.RootToken, StringComparison.OrdinalIgnoreCase))
        {
            rank += 20;
        }

        if (!string.IsNullOrWhiteSpace(command.DisplayName) &&
            query.Tokens.Count > 1 &&
            command.DisplayName.StartsWith(query.Tokens.Last(), StringComparison.OrdinalIgnoreCase))
        {
            rank += 5;
        }

        return rank;
    }

    private void SelectBestLayeredItem(string filter)
    {
        CommandPaletteLayerQuery query = CommandPaletteLayerQueryParser.Parse(filter);
        if (!query.IsLayered || query.Tokens.Count == 0)
        {
            return;
        }

        string badgeText = string.Join(" ", query.Tokens);
        for (int i = 0; i < _commandListBox.Items.Count; i++)
        {
            if (_commandListBox.Items[i] is not PaletteListItem { IsHeader: false, Command: { } command })
            {
                continue;
            }

            if (string.Equals(command.LayerBadge, badgeText, StringComparison.OrdinalIgnoreCase))
            {
                _commandListBox.SelectedIndex = i;
                return;
            }
        }
    }

    private static string TruncateEnd(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length <= maxLength)
        {
            return text;
        }

        return maxLength <= 1
            ? text[..1]
            : $"{text[..(maxLength - 1)]}…";
    }

    private static string TruncateLayerDetail(string text, int width)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        if (text.Contains('\\') || text.Contains('/'))
        {
            return TruncateMiddle(text, Math.Max(12, width / 7));
        }

        return TruncateEnd(text, Math.Max(12, width / 7));
    }

    private static string TruncateLayerPath(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length <= maxLength)
        {
            return text;
        }

        return text.Contains('\\') || text.Contains('/')
            ? TruncateMiddle(text, maxLength)
            : TruncateEnd(text, maxLength);
    }

    private static string TruncateMiddle(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length <= maxLength)
        {
            return text;
        }

        if (maxLength <= 2)
        {
            return text[..Math.Min(text.Length, maxLength)];
        }

        int head = (maxLength - 1) / 2;
        int tail = maxLength - 1 - head;
        return $"{text[..head]}…{text[^tail..]}";
    }

    private void CommandListBox_MouseDown(object? sender, MouseEventArgs e)
    {
        int index = _commandListBox.IndexFromPoint(e.Location);
        if (index >= 0)
        {
            _commandListBox.SelectedIndex = index;
            if (e.Button == MouseButtons.Left)
            {
                if (_commandListBox.Items[index] is PaletteListItem item && item.IsMoreRow)
                {
                    ExecuteSelected();
                }
            }
        }

        if (e.Button == MouseButtons.Right)
        {
            _commandContextMenu.Show(_commandListBox, e.Location);
        }
    }

    private void CommandListBox_MouseMove(object? sender, MouseEventArgs e)
    {
        int index = _commandListBox.IndexFromPoint(e.Location);
        if (index < 0 || index >= _commandListBox.Items.Count)
        {
            _toolTip.Hide(_commandListBox);
            return;
        }

        if (_commandListBox.Items[index] is not PaletteListItem item)
        {
            _toolTip.Hide(_commandListBox);
            return;
        }

        string tooltip = BuildPaletteItemTooltip(item);
        if (string.IsNullOrWhiteSpace(tooltip))
        {
            _toolTip.Hide(_commandListBox);
            return;
        }

        _toolTip.SetToolTip(_commandListBox, tooltip);
    }

    private string BuildPaletteItemTooltip(PaletteListItem item)
    {
        if (item.IsSectionHeader)
        {
            return item.HeaderText switch
            {
                "カテゴリ選択ホーム" => "カテゴリ選択ホーム。番号キー、クリック、Enter でカテゴリを開けます。",
                "開いているタブ" => "現在開いているタブの一覧です。",
                "移動先" => "QuickAccess の登録先一覧です。",
                "機能" => "安全な機能一覧です。",
                "設定" => "設定画面や関連入口の一覧です。",
                _ => item.HeaderText
            };
        }

        if (item.Command is not { } command)
        {
            return string.Empty;
        }

        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.NumberText))
        {
            lines.Add($"No: {item.NumberText}");
        }

        lines.Add(command.DisplayName);

        string detail = BuildUniversalSectionedDescriptionText(command);
        if (!string.IsNullOrWhiteSpace(detail))
        {
            lines.Add(detail.Replace(Environment.NewLine, " / "));
        }

        if (!string.IsNullOrWhiteSpace(command.SecondaryText))
        {
            lines.Add($"詳細: {command.SecondaryText}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private void ExecuteSelected()
    {
        if (_commandListBox.SelectedItem is not PaletteListItem item) return;

        if (item.IsHeader)
        {
            ToggleHeaderExpansion();
            return;
        }

        if (item.IsSectionHeader)
        {
            return;
        }

        if (item.IsMoreRow)
        {
            if (!string.IsNullOrEmpty(item.SectionTitle))
            {
                _pendingMoreRowRestoreIndex = _commandListBox.SelectedIndex;
                _pendingMoreRowRestoreTopIndex = _commandListBox.TopIndex;
                _expandedSections.Add(item.SectionTitle);
                FilterCommands();
            }
            return;
        }

        if (item.Command is { } cmd)
        {
            if (cmd.ClearsSearchText)
            {
                PushCurrentNavigationState();
                ClearUniversalSearchScope();
                ApplySearchText(string.Empty);
                FilterCommands();
                return;
            }

            if (!string.IsNullOrWhiteSpace(cmd.QueryInsertText))
            {
                PushCurrentNavigationState();
                SetUniversalSearchScope(cmd.QueryInsertText, BuildUniversalScopeDisplayLabel(cmd));
                _navigationForwardStack.Clear();
                ApplySearchText(string.Empty);
                FilterCommands();
                return;
            }

            if ((cmd.CanExecute != null && !cmd.CanExecute()) || !string.IsNullOrWhiteSpace(cmd.NonExecutableMessage))
            {
                ShowNonExecutableFeedback(cmd);
                return;
            }

            if (cmd.SafetyLevel == CommandPaletteSafetyLevel.Unsupported ||
                cmd.SafetyLevel == CommandPaletteSafetyLevel.Deferred)
            {
                ShowNonExecutableFeedback(cmd);
                return;
            }

            if (cmd.SafetyLevel == CommandPaletteSafetyLevel.Confirm &&
                !ShowCommandPaletteConfirm(cmd))
            {
                return;
            }

            SelectedCommand = cmd;
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    private void ApplySearchText(string queryText)
    {
        _searchBox.Text = queryText;
        _searchBox.SelectionStart = _searchBox.TextLength;
        _searchBox.SelectionLength = 0;
        if (IsHandleCreated && Visible && !_searchBox.Focused)
        {
            BeginInvoke(new Action(() => _searchBox.Focus()));
        }
    }

    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Back or Keys.Delete &&
            string.IsNullOrWhiteSpace(_searchBox.Text) &&
            !string.IsNullOrWhiteSpace(_selectedUniversalSearchScopePrefix) &&
            _currentPresentation.HasSections)
        {
            NavigateUp();
            e.Handled = true;
            e.SuppressKeyPress = true;
            return;
        }

        if (!string.IsNullOrWhiteSpace(_searchBox.Text))
        {
            return;
        }

        if (TryHandleUniversalSearchNumberKey(e.KeyData))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private bool TryHandleUniversalSearchNumberKey(Keys keyData)
    {
        int? number = keyData switch
        {
            Keys.D0 or Keys.NumPad0 => 0,
            Keys.D1 or Keys.NumPad1 => 1,
            Keys.D2 or Keys.NumPad2 => 2,
            Keys.D3 or Keys.NumPad3 => 3,
            Keys.D4 or Keys.NumPad4 => 4,
            Keys.D5 or Keys.NumPad5 => 5,
            Keys.D6 or Keys.NumPad6 => 6,
            Keys.D7 or Keys.NumPad7 => 7,
            Keys.D8 or Keys.NumPad8 => 8,
            Keys.D9 or Keys.NumPad9 => 9,
            _ => null
        };

        if (number is null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(_selectedUniversalSearchScopePrefix))
        {
            if (number is >= 1 and <= 4 && IsUniversalSearchHomePresentation())
            {
                int index = number.Value - 1;
                return SelectUniversalSearchVisibleItem(index, executeImmediately: true);
            }

            return false;
        }

        if (number == 0)
        {
            NavigateUp();
            return true;
        }

        return SelectUniversalSearchVisibleItem(number.Value, executeImmediately: false);
    }

    private bool SelectUniversalSearchVisibleItem(int visibleIndex, bool executeImmediately)
    {
        if (visibleIndex < 0)
        {
            return false;
        }

        int seen = -1;
        for (int i = 0; i < _commandListBox.Items.Count; i++)
        {
            if (_commandListBox.Items[i] is not PaletteListItem { IsHeader: false, IsSectionHeader: false })
            {
                continue;
            }

            seen++;
            if (seen != visibleIndex)
            {
                continue;
            }

            _commandListBox.SelectedIndex = i;
            if (executeImmediately)
            {
                ExecuteSelected();
            }
            return true;
        }

        return false;
    }

    private void NavigateBack()
    {
        if (_navigationBackStack.Count == 0)
        {
            return;
        }

        UniversalSearchNavigationState previous = _navigationBackStack.Pop();
        _navigationForwardStack.Push(CaptureCurrentNavigationState());
        ApplyNavigationState(previous);
    }

    private void NavigateForward()
    {
        if (_navigationForwardStack.Count == 0)
        {
            return;
        }

        UniversalSearchNavigationState next = _navigationForwardStack.Pop();
        _navigationBackStack.Push(CaptureCurrentNavigationState());
        ApplyNavigationState(next);
    }

    private void NavigateUp()
    {
        if (string.IsNullOrWhiteSpace(_selectedUniversalSearchScopePrefix))
        {
            return;
        }

        PushCurrentNavigationState();
        _navigationForwardStack.Clear();
        ClearUniversalSearchScope();
        ApplySearchText(string.Empty);
        FilterCommands();
    }

    private void ApplyNavigationState(UniversalSearchNavigationState state)
    {
        ClearUniversalSearchScope();
        if (!string.IsNullOrWhiteSpace(state.ScopePrefix))
        {
            SetUniversalSearchScope(state.ScopePrefix, state.ScopeLabel);
        }

        ApplySearchText(state.SearchText);
        FilterCommands();
    }

    private UniversalSearchNavigationState CaptureCurrentNavigationState()
    {
        return new UniversalSearchNavigationState(
            BuildNavigationBreadcrumb(),
            _selectedUniversalSearchScopePrefix,
            _selectedUniversalSearchScopeLabel,
            _searchBox.Text);
    }

    private void PushCurrentNavigationState()
    {
        _navigationBackStack.Push(CaptureCurrentNavigationState());
    }

    private void SyncNavigationStateFromPresentation(string filter, string effectiveQuery)
    {
        _ = effectiveQuery;

        if (CommandPaletteUniversalSearchService.TryParseScope(filter, out CommandPaletteUniversalSearchService.UniversalSearchScopeResult? explicitScope) &&
            explicitScope is not null)
        {
            string prefix = BuildScopePrefix(explicitScope.Scope);
            string label = BuildScopeLabel(explicitScope.Scope);
            if (!string.Equals(_selectedUniversalSearchScopePrefix, prefix, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(_selectedUniversalSearchScopeLabel, label, StringComparison.OrdinalIgnoreCase))
            {
                SetUniversalSearchScope(prefix, label);
            }
        }
    }

    private void UpdateNavigationUi()
    {
        bool hasScope = !string.IsNullOrWhiteSpace(_selectedUniversalSearchScopePrefix);
        _backLink.Enabled = _navigationBackStack.Count > 0;
        _forwardLink.Enabled = _navigationForwardStack.Count > 0;
        _upLink.Enabled = hasScope;
        _breadcrumbLabel.Text = BuildNavigationBreadcrumb();
    }

    private string BuildNavigationBreadcrumb()
    {
        if (string.IsNullOrWhiteSpace(_selectedUniversalSearchScopeLabel))
        {
            return "カテゴリ選択ホーム";
        }

        return $"カテゴリ選択ホーム > {_selectedUniversalSearchScopeLabel}";
    }

    private static string BuildScopePrefix(CommandPaletteUniversalSearchService.UniversalSearchScope scope)
    {
        return scope switch
        {
            CommandPaletteUniversalSearchService.UniversalSearchScope.Tabs => "tab",
            CommandPaletteUniversalSearchService.UniversalSearchScope.Destinations => "q",
            CommandPaletteUniversalSearchService.UniversalSearchScope.Functions => "c",
            CommandPaletteUniversalSearchService.UniversalSearchScope.Settings => "s",
            _ => string.Empty
        };
    }

    private static string BuildScopeLabel(CommandPaletteUniversalSearchService.UniversalSearchScope scope)
    {
        return scope switch
        {
            CommandPaletteUniversalSearchService.UniversalSearchScope.Tabs => "開いているタブ",
            CommandPaletteUniversalSearchService.UniversalSearchScope.Destinations => "移動先",
            CommandPaletteUniversalSearchService.UniversalSearchScope.Functions => "機能",
            CommandPaletteUniversalSearchService.UniversalSearchScope.Settings => "設定",
            _ => "カテゴリ"
        };
    }

    private static LinkLabel CreateNavigationLink(string text, LinkLabelLinkClickedEventHandler handler)
    {
        var link = new LinkLabel
        {
            AutoSize = true,
            Text = text,
            Margin = new Padding(0, 4, 10, 0),
            Padding = new Padding(0),
            LinkBehavior = LinkBehavior.HoverUnderline
        };
        link.LinkClicked += handler;
        return link;
    }

    private void SetUniversalSearchScope(string? scopePrefix, string? scopeLabel)
    {
        _selectedUniversalSearchScopePrefix = string.IsNullOrWhiteSpace(scopePrefix) ? null : scopePrefix.Trim();
        _selectedUniversalSearchScopeLabel = string.IsNullOrWhiteSpace(scopeLabel) ? null : scopeLabel.Trim();
    }

    private void ClearUniversalSearchScope()
    {
        _selectedUniversalSearchScopePrefix = null;
        _selectedUniversalSearchScopeLabel = null;
    }

    private bool IsUniversalSearchHomePresentation()
    {
        return _currentPresentation.Sections is { Count: > 0 } sections &&
               sections.Count == 1 &&
               string.Equals(sections[0].Title, "カテゴリ選択ホーム", StringComparison.OrdinalIgnoreCase);
    }

    private int GetUniversalSearchHomeCommandItemIndex(int shortcutIndex)
    {
        int seen = -1;
        for (int i = 0; i < _commandListBox.Items.Count; i++)
        {
            if (_commandListBox.Items[i] is not PaletteListItem { IsHeader: false, IsSectionHeader: false })
            {
                continue;
            }

            seen++;
            if (seen == shortcutIndex)
            {
                return i;
            }
        }

        return -1;
    }

    private void ToggleHeaderExpansion()
    {
        if (_commandListBox.SelectedItem is not PaletteListItem { IsHeader: true } item) return;

        if (item.IsExpanded)
        {
            _expandedCategories.Remove(item.HeaderText);
        }
        else
        {
            _expandedCategories.Add(item.HeaderText);
        }

        FilterCommands();
    }

    private void CommandListBox_DrawItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;

        if (_commandListBox.Items[e.Index] is not PaletteListItem item)
        {
            return;
        }

        bool isSelected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
        e.DrawBackground();

        // ScrollAlwaysVisible = true により e.Bounds.Right は常にスクロールバーの内側になる
        int rightEdge = e.Bounds.Right - 4;

        if (_currentPresentation.IsLayered)
        {
            DrawLayeredCommandItem(e, item, isSelected, rightEdge);
            return;
        }

        if (item.IsSectionHeader)
        {
            DrawSectionHeaderItem(e, item, isSelected, rightEdge);
            return;
        }

        if (item.IsMoreRow)
        {
            DrawMoreItem(e, item, isSelected, rightEdge);
            return;
        }

        if (_currentPresentation.HasSections)
        {
            DrawSectionedCommandItem(e, item, isSelected, rightEdge);
            return;
        }

        if (item.IsHeader)
        {
            using var headerBrush = new SolidBrush(isSelected ? e.ForeColor : Color.FromArgb(60, 110, 170));
            string indicator = item.IsExpanded ? "▼" : "▶";
            string headerText = item.HeaderText switch
            {
                "Favorite" => "★ Favorite",
                "Recent" => "最近使ったコマンド",
                _ => item.HeaderText
            };
            string text = $"{indicator} {headerText}";
            
            TextRenderer.DrawText(
                e.Graphics,
                text,
                e.Font!,
                new Rectangle(e.Bounds.X + 2, e.Bounds.Y + 2, rightEdge - e.Bounds.X - 4, e.Bounds.Height),
                headerBrush.Color,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            // 件数表示 (右寄せ)
            string countText = $"{item.VisibleCount} / {item.TotalCount}";
            var countSize = TextRenderer.MeasureText(countText, e.Font!);
            TextRenderer.DrawText(
                e.Graphics,
                countText,
                e.Font!,
                new Rectangle(rightEdge - countSize.Width, e.Bounds.Y + 2, countSize.Width, e.Bounds.Height),
                headerBrush.Color,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

            return;
        }

        CommandLauncherCommand cmd = item.Command!;
        using var categoryBrush = new SolidBrush(isSelected ? e.ForeColor : Color.Gray);
        using var secondaryBrush = new SolidBrush(isSelected ? e.ForeColor : Color.DimGray);

        // Category (Right aligned)
        string category = cmd.Category;
        var categorySize = TextRenderer.MeasureText(category, e.Font!);
        int categoryX = rightEdge - categorySize.Width;
        e.Graphics.DrawString(category, e.Font!, categoryBrush, categoryX, e.Bounds.Y + 2);

        // DisplayName (Truncated if overlaps with category)
        string displayName = IsFavorite(cmd.Id) ? $"★ {cmd.DisplayName}" : cmd.DisplayName;
        string secondary = cmd.SecondaryText ?? string.Empty;
        int secondaryWidth = string.IsNullOrWhiteSpace(secondary)
            ? 0
            : TextRenderer.MeasureText(secondary, e.Font!).Width + 10;
        int maxNameWidth = categoryX - e.Bounds.X - 8 - secondaryWidth;
        if (maxNameWidth > 10)
        {
            TextRenderer.DrawText(e.Graphics, displayName, e.Font!,
                new Rectangle(e.Bounds.X + 2, e.Bounds.Y + 2, maxNameWidth, e.Bounds.Height),
                isSelected ? e.ForeColor : _commandListBox.ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        if (!string.IsNullOrWhiteSpace(secondary))
        {
            int secondaryX = Math.Max(e.Bounds.X + 2, categoryX - secondaryWidth);
            TextRenderer.DrawText(
                e.Graphics,
                secondary,
                e.Font!,
                new Rectangle(secondaryX, e.Bounds.Y + 2, secondaryWidth, e.Bounds.Height),
                secondaryBrush.Color,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        e.DrawFocusRectangle();
    }

    private void DrawLayeredCommandItem(DrawItemEventArgs e, PaletteListItem item, bool isSelected, int rightEdge)
    {
        if (item.IsHeader)
        {
            using var headerBrush = new SolidBrush(isSelected ? e.ForeColor : Color.FromArgb(60, 110, 170));
            string indicator = item.IsExpanded ? "▼" : "▶";
            string headerText = item.HeaderText switch
            {
                "Favorite" => "★ Favorite",
                "Recent" => "最近使ったコマンド",
                _ => item.HeaderText
            };
            string text = $"{indicator} {headerText}";

            TextRenderer.DrawText(
                e.Graphics,
                text,
                e.Font!,
                new Rectangle(e.Bounds.X + 2, e.Bounds.Y + 2, rightEdge - e.Bounds.X - 4, e.Bounds.Height),
                headerBrush.Color,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            string countText = $"{item.VisibleCount} / {item.TotalCount}";
            var countSize = TextRenderer.MeasureText(countText, e.Font!);
            TextRenderer.DrawText(
                e.Graphics,
                countText,
                e.Font!,
                new Rectangle(rightEdge - countSize.Width, e.Bounds.Y + 2, countSize.Width, e.Bounds.Height),
                headerBrush.Color,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            return;
        }

        if (item.Command is not { } cmd)
        {
            return;
        }

        Rectangle badgeRect = new Rectangle(e.Bounds.X + 8, e.Bounds.Y + 5, 128, e.Bounds.Height - 10);
        Rectangle titleRect = new Rectangle(badgeRect.Right + 8, e.Bounds.Y + 5, Math.Max(120, rightEdge - badgeRect.Right - 240), e.Bounds.Height - 10);
        Rectangle detailRect = new Rectangle(titleRect.Right + 8, e.Bounds.Y + 5, Math.Max(120, rightEdge - titleRect.Right - 8), e.Bounds.Height - 10);

        string badge = cmd.LayerBadge ?? cmd.Category;
        string title = cmd.DisplayName;
        string detail = BuildLayerShortDetailText(cmd);

        using var badgeBackgroundBrush = new SolidBrush(isSelected ? Color.FromArgb(70, 105, 150) : Color.FromArgb(230, 238, 247));
        using var badgeBrush = new SolidBrush(isSelected ? Color.FromArgb(220, 235, 245) : Color.FromArgb(95, 140, 190));
        using var detailBrush = new SolidBrush(isSelected ? e.ForeColor : Color.DimGray);

        e.Graphics.FillRectangle(badgeBackgroundBrush, badgeRect);

        TextRenderer.DrawText(
            e.Graphics,
            TruncateEnd(badge, 14),
            e.Font!,
            badgeRect,
            badgeBrush.Color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        TextRenderer.DrawText(
            e.Graphics,
            title,
            e.Font!,
            titleRect,
            e.ForeColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        if (!string.IsNullOrWhiteSpace(detail))
        {
            TextRenderer.DrawText(
                e.Graphics,
                TruncateLayerDetail(detail, detailRect.Width),
                e.Font!,
                detailRect,
                detailBrush.Color,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        e.DrawFocusRectangle();
    }

    private void DrawSectionedCommandItem(DrawItemEventArgs e, PaletteListItem item, bool isSelected, int rightEdge)
    {
        if (item.Command is not { } cmd)
        {
            return;
        }

        Rectangle titleRect = new Rectangle(e.Bounds.X + 8, e.Bounds.Y + 5, Math.Max(140, rightEdge - e.Bounds.X - 8 - 240), e.Bounds.Height - 10);
        Rectangle detailRect = new Rectangle(titleRect.Right + 8, e.Bounds.Y + 5, Math.Max(120, rightEdge - titleRect.Right - 8), e.Bounds.Height - 10);

        string title = string.IsNullOrWhiteSpace(cmd.Title) ? cmd.DisplayName : cmd.Title;
        string detail = !string.IsNullOrWhiteSpace(cmd.Subtitle)
            ? cmd.Subtitle
            : !string.IsNullOrWhiteSpace(cmd.Description)
                ? cmd.Description
                : cmd.SecondaryText ?? string.Empty;

        using var detailBrush = new SolidBrush(isSelected ? e.ForeColor : Color.DimGray);
        using var titleFont = new Font(e.Font!, isSelected ? FontStyle.Bold : FontStyle.Regular);

        TextRenderer.DrawText(
            e.Graphics,
            title,
            titleFont,
            titleRect,
            e.ForeColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        if (!string.IsNullOrWhiteSpace(detail))
        {
            TextRenderer.DrawText(
                e.Graphics,
                TruncateLayerDetail(detail, detailRect.Width),
                e.Font!,
                detailRect,
                detailBrush.Color,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        e.DrawFocusRectangle();
    }

    private void DrawMoreItem(DrawItemEventArgs e, PaletteListItem item, bool isSelected, int rightEdge)
    {
        if (!isSelected)
        {
            using var bgBrush = new SolidBrush(Color.FromArgb(235, 243, 253));
            e.Graphics.FillRectangle(bgBrush, e.Bounds);
            using var borderPen = new Pen(Color.FromArgb(200, 220, 245));
            e.Graphics.DrawLine(borderPen, e.Bounds.X, e.Bounds.Y, rightEdge, e.Bounds.Y);
            e.Graphics.DrawLine(borderPen, e.Bounds.X, e.Bounds.Bottom - 1, rightEdge, e.Bounds.Bottom - 1);
        }

        Rectangle textRect = new Rectangle(e.Bounds.X + 24, e.Bounds.Y + 5, rightEdge - e.Bounds.X - 32, e.Bounds.Height - 10);
        using var textBrush = new SolidBrush(isSelected ? e.ForeColor : Color.FromArgb(20, 90, 180));
        using var boldFont = new Font(e.Font!, FontStyle.Bold);

        string text = (item.HeaderText ?? string.Empty).Replace("残り", "さらに");

        TextRenderer.DrawText(
            e.Graphics,
            text,
            boldFont,
            textRect,
            textBrush.Color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        e.DrawFocusRectangle();
    }

    private void DrawSectionHeaderItem(DrawItemEventArgs e, PaletteListItem item, bool isSelected, int rightEdge)
    {
        using var backgroundBrush = new SolidBrush(Color.FromArgb(24, 48, 88));
        using var labelBrush = new SolidBrush(Color.White);
        using var separatorPen = new Pen(Color.FromArgb(20, 40, 75));

        e.Graphics.FillRectangle(backgroundBrush, e.Bounds);
        e.Graphics.DrawLine(separatorPen, e.Bounds.X, e.Bounds.Bottom - 1, rightEdge, e.Bounds.Bottom - 1);

        using var boldFont = new Font(e.Font!.FontFamily, 10F, FontStyle.Bold);

        string label = item.HeaderText;
        TextRenderer.DrawText(
            e.Graphics,
            label,
            boldFont,
            new Rectangle(e.Bounds.X + 8, e.Bounds.Y + 4, Math.Max(40, rightEdge - e.Bounds.X - 120), e.Bounds.Height - 8),
            labelBrush.Color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        string countText = $"{item.VisibleCount} / {item.TotalCount}";
        var countSize = TextRenderer.MeasureText(countText, boldFont);
        TextRenderer.DrawText(
            e.Graphics,
            countText,
            boldFont,
            new Rectangle(rightEdge - countSize.Width - 12, e.Bounds.Y + 4, countSize.Width, e.Bounds.Height - 8),
            labelBrush.Color,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter);

        e.DrawFocusRectangle();
    }

    private void SelectFirstExecutableItem()
    {
        if (_commandListBox.Items.Count == 0)
        {
            _commandListBox.SelectedIndex = -1;
            return;
        }

        // 基本は最初のコマンドを探すが、見つからなければ0番目(ヘッダー)
        for (int i = 0; i < _commandListBox.Items.Count; i++)
        {
            if (_commandListBox.Items[i] is PaletteListItem { IsHeader: false, IsSectionHeader: false })
            {
                _commandListBox.SelectedIndex = i;
                return;
            }
        }

        _commandListBox.SelectedIndex = -1;
    }

    private void MoveSelection(int direction)
    {
        if (_commandListBox.Items.Count == 0) return;

        int next = _commandListBox.SelectedIndex + direction;
        while (next >= 0 && next < _commandListBox.Items.Count)
        {
            if (_commandListBox.Items[next] is PaletteListItem { IsHeader: false, IsSectionHeader: false })
            {
                _commandListBox.SelectedIndex = next;
                return;
            }

            next += direction;
        }
    }

    private static int GetCategoryOrder(string category)
    {
        for (int i = 0; i < CategoryOrder.Length; i++)
        {
            if (string.Equals(CategoryOrder[i], category, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return CategoryOrder.Length;
    }
}
