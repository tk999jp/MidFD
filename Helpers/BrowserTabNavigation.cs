using System.Drawing;
using System.ComponentModel;
using System.Windows.Forms;
using MidFD.Models;
using MidFD.Presentation;

namespace MidFD.Helpers;

/// <summary>横型BrowserTabStripと同じstateを左側へ表示するnavigation view。</summary>
public sealed class BrowserTabNavigation : UserControl
{
    private readonly TreeView _tree = new();
    private readonly Panel _treeHost = new();
    private readonly Panel _splitter = new();
    private IReadOnlyList<BrowserTabNavigationCategoryItem> _categories = Array.Empty<BrowserTabNavigationCategoryItem>();
    private int _selectedIndex = -1;
    private int _selectedCategoryIndex = -1;
    private bool _syncing;
    private bool _draggingSplitter;
    private int _splitterStartX;
    private int _splitterStartWidth;
    private readonly Dictionary<(int CategoryIndex, int TabIndex), PathPresentation> _pathPresentations = new();
    private readonly HashSet<string> _collapsedCategoryIds = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<BrowserTabStripCategoryEventArgs>? AddTabForCategoryClicked;
    public event EventHandler? NavigationWidthChanged;
    public event EventHandler? SelectedIndexChanged;
    public event EventHandler<BrowserTabStripCategoryEventArgs>? CategoryClicked;
    public event EventHandler<BrowserTabStripCategoryEventArgs>? CategoryContextMenuRequested;
    public event EventHandler<BrowserTabStripMouseEventArgs>? TabDoubleClicked;
    public event EventHandler<BrowserTabStripMouseEventArgs>? SelectedTabReclicked;
    public event EventHandler<BrowserTabStripMouseEventArgs>? TabRightClicked;
    public event EventHandler<BrowserTabStripReorderEventArgs>? TabReordered;
    public event EventHandler<BrowserTabStripReorderEventArgs>? CategoryReordered;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [Browsable(false)]
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            int tabCount = _selectedCategoryIndex >= 0 && _selectedCategoryIndex < _categories.Count
                ? _categories[_selectedCategoryIndex].Tabs.Count
                : 0;
            int normalized = value >= 0 && value < tabCount ? value : -1;
            if (_selectedIndex == normalized) return;
            _selectedIndex = normalized;
            if (!_syncing) SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            SelectCurrentNode();
        }
    }

    public int SelectedCategoryIndex => _selectedCategoryIndex;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [Browsable(false)]
    public Color ActiveTabTextColor { get; private set; } = MidFDColors.ListSelectedFore;

    public BrowserTabNavigation()
    {
        BackColor = MidFDColors.ListNormalBack;
        ForeColor = MidFDColors.ListNormalFore;
        MinimumSize = new Size(120, 0);
        _treeHost.Dock = DockStyle.Fill;
        _treeHost.BackColor = MidFDColors.BorderLine;
        _treeHost.Padding = new Padding(1, 1, 0, 1);
        _tree.Dock = DockStyle.Fill;
        _tree.BorderStyle = BorderStyle.None;
        _tree.HideSelection = false;
        _tree.DrawMode = TreeViewDrawMode.OwnerDrawText;
        _tree.BackColor = MidFDColors.ListNormalBack;
        _tree.ForeColor = ForeColor;
        _tree.AllowDrop = true;
        _tree.NodeMouseClick += Tree_NodeMouseClick;
        _tree.NodeMouseDoubleClick += Tree_NodeMouseDoubleClick;
        _tree.ItemDrag += Tree_ItemDrag;
        _tree.DragEnter += Tree_DragEnter;
        _tree.DragDrop += Tree_DragDrop;
        _tree.DrawNode += Tree_DrawNode;
        _tree.AfterExpand += Tree_AfterExpand;
        _tree.BeforeCollapse += Tree_BeforeCollapse;
        _tree.AfterCollapse += Tree_AfterCollapse;
        _tree.KeyDown += Tree_KeyDown;
        _splitter.Dock = DockStyle.Right;
        _splitter.Width = 5;
        _splitter.Cursor = Cursors.SizeWE;
        _splitter.BackColor = Color.FromArgb(80, 80, 80);
        _splitter.MouseDown += Splitter_MouseDown;
        _splitter.MouseMove += Splitter_MouseMove;
        _splitter.MouseUp += (_, _) =>
        {
            if (_draggingSplitter) NavigationWidthChanged?.Invoke(this, EventArgs.Empty);
            _draggingSplitter = false;
        };
        Resize += (_, _) => RefreshPathPresentations();
        Paint += BrowserTabNavigation_Paint;
        _treeHost.Controls.Add(_tree);
        Controls.Add(_treeHost);
        Controls.Add(_splitter);
    }

    public void SetCategories(
        IReadOnlyList<BrowserTabNavigationCategoryItem> categories,
        int selectedCategoryIndex,
        int selectedTabIndex = -1)
    {
        bool sameStructure = _categories.Count == categories.Count
            && _categories.Zip(categories).All(pair => pair.First.CategoryId == pair.Second.CategoryId
                && pair.First.Text == pair.Second.Text
                && pair.First.Kind == pair.Second.Kind
                && pair.First.Tabs.Count == pair.Second.Tabs.Count);
        _categories = categories;
        _selectedCategoryIndex = selectedCategoryIndex;
        if (selectedTabIndex >= 0)
        {
            _selectedIndex = selectedTabIndex;
        }
        if (sameStructure) UpdateSelection(selectedCategoryIndex, selectedTabIndex >= 0 ? selectedTabIndex : _selectedIndex);
        else RebuildTree();
    }

    public void UpdateSelection(int selectedCategoryIndex, int selectedIndex)
    {
        _selectedCategoryIndex = selectedCategoryIndex;
        _selectedIndex = -1;
        int tabCount = selectedCategoryIndex >= 0 && selectedCategoryIndex < _categories.Count
            ? _categories[selectedCategoryIndex].Tabs.Count
            : 0;
        if (selectedIndex >= 0 && selectedIndex < tabCount) _selectedIndex = selectedIndex;
        SelectCurrentNode();
    }

    public void ApplyThemeColors(Color borderColor, Color backColor, Color foreColor)
    {
        BackColor = backColor;
        ForeColor = foreColor;
        ActiveTabTextColor = MidFDColors.ListSelectedFore;
        _treeHost.BackColor = borderColor;
        _tree.BackColor = backColor;
        _tree.ForeColor = foreColor;
        Invalidate();
        _tree.Invalidate();
    }

    /// <summary>構造を再構築せず、指定tabの表示だけを現在幅へ更新する。</summary>
    public void UpdateTabPathPresentation(int categoryIndex, int tabIndex, string? canonicalPath, string fallbackText, string toolTipText, string prefix, bool select = false, string? baseTitle = null, string? relativeSuffix = null)
    {
        _pathPresentations[(categoryIndex, tabIndex)] = new PathPresentation(
            canonicalPath!,
            fallbackText,
            toolTipText,
            prefix,
            baseTitle,
            relativeSuffix,
            string.Empty);
        ApplyTabPathPresentation(categoryIndex, tabIndex, canonicalPath, fallbackText, toolTipText, prefix, select, baseTitle, relativeSuffix);
    }

    private void ApplyTabPathPresentation(int categoryIndex, int tabIndex, string? canonicalPath, string fallbackText, string toolTipText, string prefix, bool select, string? baseTitle, string? relativeSuffix)
    {
        TreeNode? node = FindTabNode(categoryIndex, tabIndex);
        if (node == null) return;

        string text = BuildPathPresentation(node, canonicalPath, fallbackText, prefix, baseTitle, relativeSuffix);
        _pathPresentations[(categoryIndex, tabIndex)] = new PathPresentation(
            canonicalPath!,
            fallbackText,
            toolTipText,
            prefix,
            baseTitle,
            relativeSuffix,
            text);
        bool changed = !string.Equals(node.Text, text, StringComparison.Ordinal)
            || !string.Equals(node.ToolTipText, toolTipText, StringComparison.Ordinal);
        bool selectionChanged = select && (_selectedCategoryIndex != categoryIndex || _selectedIndex != tabIndex);
        if (select)
        {
            _tree.BeginUpdate();
        }
        try
        {
            node.Text = text;
            node.ToolTipText = toolTipText;
            if (select)
            {
                _selectedCategoryIndex = categoryIndex;
                _selectedIndex = tabIndex;
                SelectCurrentNode();
            }
        }
        finally
        {
            if (select)
            {
                _tree.EndUpdate();
            }
        }
        if (changed || selectionChanged)
        {
            _tree.Invalidate();
        }
    }

    private void RebuildTree()
    {
        if (_tree.IsDisposed) return;
        CaptureCollapsedCategories();
        _syncing = true;
        try
        {
            _tree.BeginUpdate();
            _tree.Nodes.Clear();
            for (int categoryIndex = 0; categoryIndex < _categories.Count; categoryIndex++)
            {
                BrowserTabNavigationCategoryItem category = _categories[categoryIndex];
                TreeNode categoryNode = new(CreateCategoryNodeText(category)) { Tag = new CategoryTag(categoryIndex) };
                categoryNode.ToolTipText = category.ToolTipText ?? string.Empty;
                if (category.Kind == BrowserTabStripCategoryItemKind.ManageEntry)
                {
                    _tree.Nodes.Add(categoryNode);
                    continue;
                }
                if (_selectedCategoryIndex == categoryIndex) categoryNode.BackColor = Color.FromArgb(48, 48, 48);
                for (int i = 0; i < category.Tabs.Count; i++)
                {
                    BrowserTabStripItem item = category.Tabs[i];
                    TreeNode tabNode = new(item.Text) { Tag = new TabTag(categoryIndex, i) };
                    tabNode.ToolTipText = category.Tabs[i].ToolTipText ?? string.Empty;
                    categoryNode.Nodes.Add(tabNode);
                    if (item.BaseTitle != null || !string.IsNullOrWhiteSpace(item.CanonicalPath))
                    {
                        PathPresentation nextPresentation = new(
                            item.CanonicalPath,
                            item.Text,
                            item.ToolTipText ?? string.Empty,
                            item.Prefix,
                            item.BaseTitle,
                            item.RelativeSuffix,
                            string.Empty);
                        if (_pathPresentations.TryGetValue((categoryIndex, i), out PathPresentation? previous)
                            && string.Equals(previous.CanonicalPath, nextPresentation.CanonicalPath, StringComparison.Ordinal)
                            && string.Equals(previous.FallbackText, nextPresentation.FallbackText, StringComparison.Ordinal)
                            && string.Equals(previous.ToolTipText, nextPresentation.ToolTipText, StringComparison.Ordinal)
                            && string.Equals(previous.Prefix, nextPresentation.Prefix, StringComparison.Ordinal)
                            && string.Equals(previous.BaseTitle, nextPresentation.BaseTitle, StringComparison.Ordinal)
                            && string.Equals(previous.RelativeSuffix, nextPresentation.RelativeSuffix, StringComparison.Ordinal)
                            && !string.IsNullOrEmpty(previous.RenderedText))
                        {
                            nextPresentation = nextPresentation with { RenderedText = previous.RenderedText };
                        }
                        else
                        {
                            nextPresentation = nextPresentation with
                            {
                                RenderedText = BuildPathPresentation(tabNode, item.CanonicalPath, item.Text, item.Prefix, item.BaseTitle, item.RelativeSuffix)
                            };
                        }
                        _pathPresentations[(categoryIndex, i)] = nextPresentation;
                        tabNode.Text = nextPresentation.RenderedText;
                    }
                }
                if (!_collapsedCategoryIds.Contains(category.CategoryId)) categoryNode.Expand();
                _tree.Nodes.Add(categoryNode);
            }
            SelectCurrentNode();
        }
        finally
        {
            _tree.EndUpdate();
            _syncing = false;
        }
    }

    private void SelectCurrentNode()
    {
        foreach (TreeNode category in _tree.Nodes)
        {
            if (category.Tag is CategoryTag categoryTag
                && categoryTag.Index == _selectedCategoryIndex
                && !category.IsExpanded)
            {
                category.Expand();
            }
            foreach (TreeNode tab in category.Nodes)
            {
                if (tab.Tag is TabTag tag && tag.CategoryIndex == _selectedCategoryIndex && tag.Index == _selectedIndex)
                {
                    if (category.IsExpanded) _tree.SelectedNode = tab;
                    return;
                }
            }
        }
    }

    private void RefreshPathPresentations()
    {
        foreach (((int categoryIndex, int tabIndex), PathPresentation presentation) in _pathPresentations)
        {
            ApplyTabPathPresentation(
                categoryIndex,
                tabIndex,
                presentation.CanonicalPath,
                presentation.FallbackText,
                presentation.ToolTipText,
                presentation.Prefix,
                select: false,
                presentation.BaseTitle,
                presentation.RelativeSuffix);
        }
    }

    private TreeNode? FindTabNode(int categoryIndex, int tabIndex)
    {
        if (categoryIndex < 0 || categoryIndex >= _tree.Nodes.Count) return null;
        foreach (TreeNode node in _tree.Nodes[categoryIndex].Nodes)
        {
            if (node.Tag is TabTag tag && tag.CategoryIndex == categoryIndex && tag.Index == tabIndex) return node;
        }
        return null;
    }

    private string BuildPathPresentation(TreeNode node, string? canonicalPath, string fallbackText, string prefix, string? baseTitle, string? relativeSuffix)
    {
        if (baseTitle == null && string.IsNullOrWhiteSpace(canonicalPath)) return fallbackText;
        int availableWidth = Math.Max(1, _tree.ClientSize.Width - node.Bounds.Left - SystemInformation.VerticalScrollBarWidth - 2);
        int prefixWidth = TextRenderer.MeasureText(prefix, _tree.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
        Func<string, int> measure = text => TextRenderer.MeasureText(text, _tree.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
        if (baseTitle != null)
        {
            string relativeText = BrowserTabNavigationPathPresentationHelper.FormatBaseAndRelativeForWidth(
                baseTitle,
                relativeSuffix,
                Math.Max(1, availableWidth - prefixWidth),
                measure);
            return prefix + relativeText;
        }
        string pathText = BrowserTabNavigationPathPresentationHelper.FormatForWidth(
            canonicalPath!,
            Math.Max(1, availableWidth - prefixWidth),
            measure);
        return prefix + pathText;
    }

    private void Tree_NodeMouseClick(object? sender, TreeNodeMouseClickEventArgs e)
    {
        TreeViewHitTestInfo hit = _tree.HitTest(e.Location);
        if ((hit.Location & TreeViewHitTestLocations.PlusMinus) != 0) return;

        TreeNode node = e.Node!;
        if (node.Tag is CategoryTag category && category.Index >= 0 && category.Index < _categories.Count)
        {
            BrowserTabNavigationCategoryItem categoryItem = _categories[category.Index];
            Rectangle addTabBounds = GetCategoryAddTabBounds(node);
            if (categoryItem.Kind == BrowserTabStripCategoryItemKind.Category && e.Button == MouseButtons.Left && addTabBounds.Contains(e.Location))
            {
                AddTabForCategoryClicked?.Invoke(this, new BrowserTabStripCategoryEventArgs(category.Index, categoryItem.CategoryId, categoryItem.Kind, e.Button, e.Location));
                return;
            }
            CategoryClicked?.Invoke(this, new BrowserTabStripCategoryEventArgs(category.Index, _categories[category.Index].CategoryId, _categories[category.Index].Kind, e.Button, e.Location));
            return;
        }
        if (node.Tag is TabTag tab && tab.CategoryIndex >= 0 && tab.CategoryIndex < _categories.Count && tab.Index >= 0 && tab.Index < _categories[tab.CategoryIndex].Tabs.Count)
        {
            if (tab.CategoryIndex != _selectedCategoryIndex)
            {
                CategoryClicked?.Invoke(this, new BrowserTabStripCategoryEventArgs(tab.CategoryIndex, _categories[tab.CategoryIndex].CategoryId, _categories[tab.CategoryIndex].Kind, e.Button, e.Location, tab.Index));
                return;
            }
            bool wasSelectedTabLeftClick = e.Button == MouseButtons.Left && tab.Index == _selectedIndex;
            SelectedIndex = tab.Index;
            if (wasSelectedTabLeftClick)
            {
                NotifySelectedTabReclicked(tab.Index, e.Button, e.Location);
            }
            if (e.Button == MouseButtons.Right) TabRightClicked?.Invoke(this, new BrowserTabStripMouseEventArgs(tab.Index, e.Button, e.Location));
        }
    }

    internal void NotifySelectedTabReclicked(int tabIndex, MouseButtons button, Point location = default)
    {
        if (button == MouseButtons.Left && tabIndex == _selectedIndex)
        {
            SelectedTabReclicked?.Invoke(this, new BrowserTabStripMouseEventArgs(tabIndex, button, location));
        }
    }

    private void Tree_KeyDown(object? sender, KeyEventArgs e)
    {
        if (!(e.KeyCode == Keys.Apps || (e.KeyCode == Keys.F10 && e.Shift))) return;
        TreeNode? node = _tree.SelectedNode;
        TreeNode? categoryNode = node?.Tag is CategoryTag ? node : node?.Parent;
        if (categoryNode?.Tag is not CategoryTag category
            || category.Index < 0
            || category.Index >= _categories.Count)
        {
            return;
        }

        e.Handled = true;
        e.SuppressKeyPress = true;
        Point anchor = new(categoryNode.Bounds.Left, categoryNode.Bounds.Bottom);
        BrowserTabNavigationCategoryItem item = _categories[category.Index];
        CategoryContextMenuRequested?.Invoke(
            this,
            new BrowserTabStripCategoryEventArgs(
                category.Index,
                item.CategoryId,
                item.Kind,
                MouseButtons.Right,
                anchor));
    }

    private void Tree_DrawNode(object? sender, DrawTreeNodeEventArgs e)
    {
        bool activeTab = e.Node?.Tag is TabTag tab
            && tab.CategoryIndex == _selectedCategoryIndex
            && tab.Index == _selectedIndex;
        Color backColor = activeTab ? MidFDColors.ListSelectedBack : _tree.BackColor;
        Color foreColor = activeTab ? ActiveTabTextColor : ForeColor;
        using var brush = new SolidBrush(backColor);
        e.Graphics.FillRectangle(brush, e.Bounds);
        if (e.Node?.Tag is CategoryTag category && category.Index >= 0 && category.Index < _categories.Count)
        {
            string categoryText = _categories[category.Index].Text;
            TextRenderer.DrawText(e.Graphics, categoryText, _tree.Font, e.Bounds, foreColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            if (_categories[category.Index].Kind == BrowserTabStripCategoryItemKind.Category)
            {
                TextRenderer.DrawText(e.Graphics, "＋", _tree.Font, GetCategoryAddTabBounds(e.Node), foreColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            return;
        }

        TextRenderer.DrawText(e.Graphics, e.Node?.Text ?? string.Empty, _tree.Font, e.Bounds, foreColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    private void BrowserTabNavigation_Paint(object? sender, PaintEventArgs e)
    {
        using var pen = new Pen(MidFDColors.BorderLine);
        e.Graphics.DrawLine(pen, 0, 0, Width - 1, 0);
        e.Graphics.DrawLine(pen, 0, 0, 0, Height - 1);
    }

    private string CreateCategoryNodeText(BrowserTabNavigationCategoryItem category)
    {
        return category.Kind == BrowserTabStripCategoryItemKind.Category
            ? category.Text + "\u00A0\u00A0\u00A0\u00A0"
            : category.Text;
    }

    private Rectangle GetCategoryAddTabBounds(TreeNode node)
    {
        Size glyphSize = TextRenderer.MeasureText("＋", _tree.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        return new Rectangle(node.Bounds.Right - glyphSize.Width, node.Bounds.Top, glyphSize.Width, node.Bounds.Height);
    }

    private void Tree_AfterExpand(object? sender, TreeViewEventArgs e)
    {
        if (e.Node != null) SetCategoryCollapsed(e.Node, collapsed: false);
    }

    private void Tree_BeforeCollapse(object? sender, TreeViewCancelEventArgs e)
    {
        if (e.Node?.Tag is CategoryTag category
            && category.Index >= 0
            && category.Index < _categories.Count
            && category.Index == _selectedCategoryIndex)
        {
            e.Cancel = true;
        }
    }

    private void Tree_AfterCollapse(object? sender, TreeViewEventArgs e)
    {
        if (e.Node != null) SetCategoryCollapsed(e.Node, collapsed: true);
    }

    private void CaptureCollapsedCategories()
    {
        foreach (TreeNode node in _tree.Nodes)
        {
            SetCategoryCollapsed(node, !node.IsExpanded);
        }
    }

    private void SetCategoryCollapsed(TreeNode node, bool collapsed)
    {
        if (_syncing || node.Tag is not CategoryTag category || category.Index < 0 || category.Index >= _categories.Count) return;
        string categoryId = _categories[category.Index].CategoryId;
        if (collapsed) _collapsedCategoryIds.Add(categoryId);
        else _collapsedCategoryIds.Remove(categoryId);
    }

    private void Splitter_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _draggingSplitter = true;
        _splitterStartX = Control.MousePosition.X;
        _splitterStartWidth = Width;
    }

    private void Splitter_MouseMove(object? sender, MouseEventArgs e)
    {
        if (!_draggingSplitter) return;
        int currentX = Control.MousePosition.X;
        int newWidth = Math.Clamp(_splitterStartWidth + currentX - _splitterStartX, 120, 600);
        if (newWidth == Width) return;
        Width = newWidth;
    }

    private void Tree_NodeMouseDoubleClick(object? sender, TreeNodeMouseClickEventArgs e)
    {
        if (e.Node?.Tag is TabTag tab && tab.CategoryIndex == _selectedCategoryIndex) TabDoubleClicked?.Invoke(this, new BrowserTabStripMouseEventArgs(tab.Index, e.Button, e.Location));
    }

    private void Tree_ItemDrag(object? sender, ItemDragEventArgs e)
    {
        if (e.Item != null) _tree.DoDragDrop(e.Item, DragDropEffects.Move);
    }
    private void Tree_DragEnter(object? sender, DragEventArgs e) => e.Effect = e.Data?.GetDataPresent(typeof(TreeNode)) == true ? DragDropEffects.Move : DragDropEffects.None;
    private void Tree_DragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(typeof(TreeNode)) is not TreeNode source) return;
        TreeNode? target = _tree.GetNodeAt(_tree.PointToClient(new Point(e.X, e.Y)));
        if (target == null) return;
        if (source.Tag is TabTag sTab && target.Tag is TabTag tTab && sTab.CategoryIndex == tTab.CategoryIndex && sTab.Index != tTab.Index) TabReordered?.Invoke(this, new BrowserTabStripReorderEventArgs(sTab.Index, tTab.Index));
        if (source.Tag is CategoryTag sCat && target.Tag is CategoryTag tCat && sCat.Index != tCat.Index) CategoryReordered?.Invoke(this, new BrowserTabStripReorderEventArgs(sCat.Index, tCat.Index));
    }

    private sealed record TabTag(int CategoryIndex, int Index);
    private sealed record CategoryTag(int Index);
    private sealed record PathPresentation(
        string? CanonicalPath,
        string FallbackText,
        string ToolTipText,
        string Prefix,
        string? BaseTitle,
        string? RelativeSuffix,
        string RenderedText);
}

public sealed record BrowserTabNavigationCategoryItem(
    string CategoryId,
    string Text,
    string? ToolTipText,
    IReadOnlyList<BrowserTabStripItem> Tabs,
    BrowserTabStripCategoryItemKind Kind = BrowserTabStripCategoryItemKind.Category);
