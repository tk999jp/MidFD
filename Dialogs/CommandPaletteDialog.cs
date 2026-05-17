using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MidFD.Models;

namespace MidFD.Dialogs;

/// <summary>
/// コマンドパレット（組み込みコマンドランチャー）ダイアログ。
/// </summary>
public sealed class CommandPaletteDialog : Form
{
    private sealed class PaletteListItem
    {
        public required bool IsHeader { get; init; }
        public required string HeaderText { get; init; }
        public CommandLauncherCommand? Command { get; init; }
        public bool IsExpanded { get; init; }
        public int VisibleCount { get; init; }
        public int TotalCount { get; init; }
    }

    private static readonly string[] CategoryOrder = { "App", "Browser", "Mark", "External" };
    private const int CollapsedVisibleCount = 3;
    private const int RecentVisibleCount = 7;
    private readonly HashSet<string> _expandedCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Favorite", "Recent", "App", "Browser", "Mark"
    };

    private readonly List<CommandLauncherCommand> _allCommands;
    private readonly CommandPaletteUsageState _usageState;
    private readonly Action<CommandPaletteUsageState> _usageStateChanged;
    private readonly TextBox _searchBox;
    private readonly ListBox _commandListBox;
    private readonly ContextMenuStrip _commandContextMenu;

    public CommandLauncherCommand? SelectedCommand { get; private set; }

    public CommandPaletteDialog(
        IEnumerable<CommandLauncherCommand> commands,
        CommandPaletteUsageState usageState,
        Action<CommandPaletteUsageState> usageStateChanged)
    {
        _allCommands = commands.ToList();
        _usageState = usageState;
        _usageStateChanged = usageStateChanged;

        Text = "Command Palette";
        Size = new Size(500, 400);
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

        _commandListBox = new ListBox
        {
            Dock = DockStyle.Fill,
            Font = new Font(FontFamily.GenericSansSerif, 11),
            ItemHeight = 24,
            ScrollAlwaysVisible = true
        };
        _commandListBox.DoubleClick += (s, e) => ExecuteSelected();
        _commandListBox.MouseDown += CommandListBox_MouseDown;
        _commandListBox.SelectedIndexChanged += (s, e) =>
        {
            // リスト選択後も入力フォーカスは検索欄へ戻す
            // コンストラクタ実行中（ハンドル未作成）の例外を避けるため IsHandleCreated を確認
            if (IsHandleCreated && Visible && !_searchBox.Focused)
            {
                BeginInvoke(new Action(() => _searchBox.Focus()));
            }
        };
        _commandListBox.DrawMode = DrawMode.OwnerDrawFixed;
        _commandListBox.DrawItem += CommandListBox_DrawItem;
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

        mainPanel.Controls.Add(_commandListBox);
        mainPanel.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 8 }); // Margin
        mainPanel.Controls.Add(_searchBox);
        Controls.Add(mainPanel);

        FilterCommands();

        Shown += (_, _) => _searchBox.Focus();
    }

    private void FilterCommands()
    {
        string filter = _searchBox.Text.Trim();
        bool useAccordion = string.IsNullOrWhiteSpace(filter);
        
        _commandListBox.BeginUpdate();
        
        // 現在の選択を記憶 (展開/折りたたみ時のUX向上のため)
        var previousSelection = _commandListBox.SelectedItem as PaletteListItem;
        
        _commandListBox.Items.Clear();

        List<CommandLauncherCommand> filtered = BuildFilteredCommands(filter, useAccordion);

        if (useAccordion)
        {
            AddUsageGroups();

            var groups = filtered.GroupBy(c => c.Category).ToList();
            foreach (var group in groups)
            {
                string category = group.Key;
                bool isExpanded = _expandedCategories.Contains(category);
                var itemsInGroup = group.ToList();
                int totalCount = itemsInGroup.Count;
                int visibleCount = isExpanded ? totalCount : Math.Min(totalCount, CollapsedVisibleCount);

                _commandListBox.Items.Add(new PaletteListItem
                {
                    IsHeader = true,
                    HeaderText = category,
                    IsExpanded = isExpanded,
                    VisibleCount = visibleCount,
                    TotalCount = totalCount
                });

                for (int i = 0; i < visibleCount; i++)
                {
                    _commandListBox.Items.Add(new PaletteListItem
                    {
                        IsHeader = false,
                        HeaderText = string.Empty,
                        Command = itemsInGroup[i]
                    });
                }
            }
        }
        else
        {
            // 検索中はフラット表示
            foreach (CommandLauncherCommand cmd in filtered)
            {
                _commandListBox.Items.Add(new PaletteListItem
                {
                    IsHeader = false,
                    HeaderText = string.Empty,
                    Command = cmd
                });
            }
        }

        RestoreSelection(previousSelection);

        _commandListBox.EndUpdate();
    }

    private void RestoreSelection(PaletteListItem? previous)
    {
        if (previous == null)
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
            if (!previous.IsHeader && !item.IsHeader && previous.Command?.Id == item.Command?.Id)
            {
                _commandListBox.SelectedIndex = i;
                return;
            }
        }

        SelectFirstExecutableItem();
    }

    private bool IsMatch(CommandLauncherCommand cmd, string filter)
    {
        if (string.IsNullOrEmpty(filter)) return true;

        string[] tokens = filter
            .Split(new[] { ' ', '\t', '\u3000' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return true;
        }

        string searchTarget = BuildSearchTarget(cmd);
        return tokens.All(token => searchTarget.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private List<CommandLauncherCommand> BuildFilteredCommands(string filter, bool useAccordion)
    {
        IEnumerable<CommandLauncherCommand> matched = _allCommands.Where(c => IsMatch(c, filter));
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

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.D))
        {
            ToggleSelectedFavorite();
            return true;
        }

        var key = keyData & Keys.KeyCode;

        if (TryHandlePaletteActionKey(key, fromSearchBox: _searchBox.Focused))
        {
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private bool TryHandlePaletteActionKey(Keys key, bool fromSearchBox)
    {
        // Escape: 検索文字がある場合はクリア、空なら閉じる
        if (key == Keys.Escape)
        {
            if (!string.IsNullOrEmpty(_searchBox.Text))
            {
                _searchBox.Text = string.Empty;
                return true;
            }
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

    private void AddUsageGroups()
    {
        Dictionary<string, CommandLauncherCommand> commandById = _allCommands
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
            cmd.Id,
            cmd.Category,
            cmd.Description ?? string.Empty,
            cmd.SearchText ?? string.Empty,
            cmd.SecondaryText ?? string.Empty
        });
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

    private void CommandListBox_MouseDown(object? sender, MouseEventArgs e)
    {
        int index = _commandListBox.IndexFromPoint(e.Location);
        if (index >= 0)
        {
            _commandListBox.SelectedIndex = index;
        }

        if (e.Button == MouseButtons.Right)
        {
            _commandContextMenu.Show(_commandListBox, e.Location);
        }
    }

    private void ExecuteSelected()
    {
        if (_commandListBox.SelectedItem is not PaletteListItem item) return;

        if (item.IsHeader)
        {
            ToggleHeaderExpansion();
            return;
        }

        if (item.Command is { } cmd)
        {
            if (cmd.CanExecute != null && !cmd.CanExecute())
            {
                return;
            }

            SelectedCommand = cmd;
            DialogResult = DialogResult.OK;
            Close();
        }
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
            if (_commandListBox.Items[i] is PaletteListItem { IsHeader: false })
            {
                _commandListBox.SelectedIndex = i;
                return;
            }
        }

        _commandListBox.SelectedIndex = 0;
    }

    private void MoveSelection(int direction)
    {
        if (_commandListBox.Items.Count == 0) return;

        int next = _commandListBox.SelectedIndex + direction;
        if (next >= 0 && next < _commandListBox.Items.Count)
        {
            _commandListBox.SelectedIndex = next;
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
