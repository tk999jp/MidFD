using System.Drawing;
using System.Drawing.Drawing2D;
using System.ComponentModel;
using System.Windows.Forms;
using MidFD.Models;
using MidFD.Presentation;

namespace MidFD.Helpers;

/// <summary>横型BrowserTabStripと同じstateを左側へ表示するnavigation view。</summary>
public sealed class BrowserTabNavigation : UserControl
{
    private readonly BrowserTabNavigationTreeView _tree = new();
    private readonly Panel _treeHost = new();
    private readonly Panel _splitter = new();
    private readonly System.Windows.Forms.Timer _splitterWidthThrottleTimer = new() { Interval = 16 };
    private Color _splitterBorderColor = MidFDColors.BorderLine;
    internal Color DropIndicatorColor => _tree.ForeColor;
    private IReadOnlyList<BrowserTabNavigationCategoryItem> _categories = Array.Empty<BrowserTabNavigationCategoryItem>();
    private int _selectedIndex = -1;
    private int _selectedCategoryIndex = -1;
    private Guid? _selectedSearchSessionId;
    private bool _syncing;
    private bool _draggingSplitter;
    private int _splitterStartX;
    private int _splitterStartWidth;
    private int? _pendingSplitterWidth;
    private bool _splitterWidthUpdateScheduled;
    private bool _treePresentationRefreshPending;
    private bool _treePresentationRefreshScheduled;
    private int _treePresentationRefreshRequestCount;
    private int _treePresentationRefreshCount;
    private int _treeInvalidationCount;
    private int _splitterWidthApplyCount;
    private int _treeSizeChangedRefreshRequestCount;
    private int _expandedCategoryRefreshRequestCount;
    private bool _applyingSplitterWidth;
    private readonly Dictionary<(int CategoryIndex, int TabIndex), PathPresentation> _pathPresentations = new();
    private readonly HashSet<string> _collapsedCategoryIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> _collapsedSourceTabIds = new();
    private TreeNode? _dropIndicatorNode;
    private BrowserTabTreeDropPlacement? _dropIndicatorPlacement;
    private Guid? _dropIndicatorSourceTabId;
    private BrowserTabTreeDropIntent? _dropIndicatorIntent;

    internal int TreePresentationRefreshRequestCount => _treePresentationRefreshRequestCount;
    internal int TreePresentationRefreshCount => _treePresentationRefreshCount;
    internal int TreeInvalidationCount => _treeInvalidationCount;
    internal int SplitterWidthApplyCount => _splitterWidthApplyCount;
    internal int TreeSizeChangedRefreshRequestCount => _treeSizeChangedRefreshRequestCount;
    internal int ExpandedCategoryRefreshRequestCount => _expandedCategoryRefreshRequestCount;
    internal bool HasPendingTreePresentationRefresh => _treePresentationRefreshPending;

    internal void ResetPresentationDiagnostics()
    {
        _treePresentationRefreshRequestCount = 0;
        _treePresentationRefreshCount = 0;
        _treeInvalidationCount = 0;
        _splitterWidthApplyCount = 0;
        _treeSizeChangedRefreshRequestCount = 0;
        _expandedCategoryRefreshRequestCount = 0;
    }

    public event EventHandler<BrowserTabStripCategoryEventArgs>? AddTabForCategoryClicked;
    public event EventHandler? NavigationWidthChanged;
    public event EventHandler? SelectedIndexChanged;
    public event EventHandler<BrowserTabStripCategoryEventArgs>? CategoryClicked;
    public event EventHandler<BrowserTabStripCategoryEventArgs>? CategoryContextMenuRequested;
    public event EventHandler<BrowserTabGroupNavigationEventArgs>? GroupContextMenuRequested;
    public event EventHandler<BrowserTabGroupExpandedEventArgs>? GroupExpandedChanged;
    public event EventHandler<BrowserTabTreeDropEventArgs>? BrowserTabTreeDropRequested;
    public event EventHandler<BrowserTabStripMouseEventArgs>? TabDoubleClicked;
    public event EventHandler<BrowserTabStripMouseEventArgs>? SelectedTabReclicked;
    public event EventHandler<BrowserTabStripMouseEventArgs>? TabRightClicked;

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
            _selectedSearchSessionId = null;
            if (!_syncing) SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            SelectCurrentNode();
        }
    }

    public int SelectedCategoryIndex => _selectedCategoryIndex;
    public Guid? SelectedSearchSessionId => _selectedSearchSessionId;

    internal BrowserTabStripItem? GetTabItem(int categoryIndex, int tabIndex)
    {
        if (categoryIndex < 0 || categoryIndex >= _categories.Count || tabIndex < 0 || tabIndex >= _categories[categoryIndex].Tabs.Count) return null;
        return _categories[categoryIndex].Tabs[tabIndex];
    }

    internal BrowserTabStripItem? GetTabItem(string categoryId, int tabIndex)
    {
        int categoryIndex = -1;
        for (int index = 0; index < _categories.Count; index++)
        {
            if (!string.Equals(_categories[index].CategoryId, categoryId, StringComparison.OrdinalIgnoreCase)) continue;
            categoryIndex = index;
            break;
        }
        return GetTabItem(categoryIndex, tabIndex);
    }

    internal Guid? GetSearchChildParentTabId(Guid sessionId)
    {
        TreeNode? child = EnumerateNodes(_tree.Nodes).FirstOrDefault(node =>
            node.Tag is SearchChildTag tag && tag.SessionId == sessionId);
        return child?.Parent?.Tag is TabTag parentTag
            ? GetTabItem(parentTag.CategoryIndex, parentTag.Index)?.BrowserTabId
            : null;
    }

    internal bool IsSearchChildParentExpanded(Guid sessionId)
    {
        TreeNode? child = EnumerateNodes(_tree.Nodes).FirstOrDefault(node =>
            node.Tag is SearchChildTag tag && tag.SessionId == sessionId);
        return child?.Parent is { } parent && parent.IsExpanded
            && parent.Parent is { } category && category.IsExpanded;
    }

    internal Guid? GetTreeParentTabId(Guid childTabId)
    {
        TreeNode? node = EnumerateNodes(_tree.Nodes).FirstOrDefault(candidate =>
            candidate.Tag is TabTag tag && GetTabItem(tag.CategoryIndex, tag.Index)?.BrowserTabId == childTabId);
        return node?.Parent?.Tag is TabTag parentTag
            ? GetTabItem(parentTag.CategoryIndex, parentTag.Index)?.BrowserTabId
            : null;
    }

    internal IReadOnlyList<Guid> GetTreeChildTabIds(Guid sourceTabId)
    {
        TreeNode? node = EnumerateNodes(_tree.Nodes).FirstOrDefault(candidate =>
            candidate.Tag is TabTag tag && GetTabItem(tag.CategoryIndex, tag.Index)?.BrowserTabId == sourceTabId);
        return node?.Nodes.Cast<TreeNode>()
            .Select(child => child.Tag is TabTag tag ? GetTabItem(tag.CategoryIndex, tag.Index)?.BrowserTabId : null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToArray() ?? Array.Empty<Guid>();
    }

    internal IReadOnlyList<Guid> GetTreeRootTabIds(int categoryIndex)
    {
        if (categoryIndex < 0 || categoryIndex >= _tree.Nodes.Count) return Array.Empty<Guid>();
        return _tree.Nodes[categoryIndex].Nodes.Cast<TreeNode>()
            .Select(node => node.Tag is TabTag tag ? GetTabItem(tag.CategoryIndex, tag.Index)?.BrowserTabId : null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToArray();
    }

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
        _tree.AfterNativePaint += Tree_AfterNativePaint;
        _tree.NodeMouseClick += Tree_NodeMouseClick;
        _tree.NodeMouseDoubleClick += Tree_NodeMouseDoubleClick;
        _tree.ItemDrag += Tree_ItemDrag;
        _tree.DragEnter += Tree_DragEnter;
        _tree.DragOver += Tree_DragOver;
        _tree.DragLeave += Tree_DragLeave;
        _tree.DragDrop += Tree_DragDrop;
        _tree.DrawNode += Tree_DrawNode;
        _tree.AfterExpand += Tree_AfterExpand;
        _tree.BeforeCollapse += Tree_BeforeCollapse;
        _tree.AfterCollapse += Tree_AfterCollapse;
        _tree.KeyDown += Tree_KeyDown;
        _splitter.Dock = DockStyle.Right;
        _splitter.Width = 5;
        _splitter.Cursor = Cursors.SizeWE;
        _splitter.BackColor = BackColor;
        _splitter.Paint += Splitter_Paint;
        _splitter.MouseDown += Splitter_MouseDown;
        _splitter.MouseMove += Splitter_MouseMove;
        _splitter.MouseUp += Splitter_MouseUp;
        _splitterWidthThrottleTimer.Tick += (_, _) => ApplyPendingSplitterWidth();
        _tree.SizeChanged += (_, _) =>
        {
            _treeSizeChangedRefreshRequestCount++;
            RequestTreeNodeTextPresentationRefresh();
        };
        Paint += BrowserTabNavigation_Paint;
        _treeHost.Controls.Add(_tree);
        Controls.Add(_treeHost);
        Controls.Add(_splitter);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ClearTreeDropIndicator();
            _splitterWidthThrottleTimer.Stop();
            _splitterWidthThrottleTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    public void SetCategories(
        IReadOnlyList<BrowserTabNavigationCategoryItem> categories,
        int selectedCategoryIndex,
        int selectedTabIndex = -1,
        Guid? selectedSearchSessionId = null)
    {
        bool sameStructure = _categories.Count == categories.Count
            && _categories.Zip(categories).All(pair => pair.First.CategoryId == pair.Second.CategoryId
                && pair.First.Text == pair.Second.Text
                && pair.First.Kind == pair.Second.Kind
                && pair.First.Tabs.SequenceEqual(pair.Second.Tabs)
                && pair.First.SearchChild == pair.Second.SearchChild
                && (pair.First.SearchDerivedTabs ?? Array.Empty<BrowserTabNavigationSearchDerivedTab>())
                    .SequenceEqual(pair.Second.SearchDerivedTabs ?? Array.Empty<BrowserTabNavigationSearchDerivedTab>())
                && UserGroupsEqual(pair.First.UserTabGroups, pair.Second.UserTabGroups));
        _categories = categories;
        _selectedCategoryIndex = selectedCategoryIndex;
        _selectedSearchSessionId = selectedSearchSessionId;
        if (sameStructure) UpdateSelection(selectedCategoryIndex, selectedTabIndex >= 0 ? selectedTabIndex : _selectedIndex, selectedSearchSessionId);
        else
        {
            int tabCount = selectedCategoryIndex >= 0 && selectedCategoryIndex < categories.Count
                ? categories[selectedCategoryIndex].Tabs.Count
                : 0;
            _selectedIndex = selectedSearchSessionId == null && selectedTabIndex >= 0 && selectedTabIndex < tabCount
                ? selectedTabIndex
                : -1;
            RebuildTree();
        }
    }

    public void UpdateSelection(int selectedCategoryIndex, int selectedIndex, Guid? selectedSearchSessionId = null)
    {
        _selectedCategoryIndex = selectedCategoryIndex;
        _selectedSearchSessionId = null;
        if (selectedSearchSessionId is { } sessionId
            && selectedCategoryIndex >= 0
            && selectedCategoryIndex < _categories.Count
            && _categories[selectedCategoryIndex].SearchChild?.SessionId == sessionId)
        {
            _selectedSearchSessionId = sessionId;
            _selectedIndex = -1;
            SelectCurrentNode();
            return;
        }

        _selectedIndex = -1;
        int tabCount = selectedCategoryIndex >= 0 && selectedCategoryIndex < _categories.Count
            ? _categories[selectedCategoryIndex].Tabs.Count
            : 0;
        if (selectedIndex >= 0 && selectedIndex < tabCount) _selectedIndex = selectedIndex;
        SelectCurrentNode();
    }

    internal IReadOnlyList<Guid> GetTreeGroupMemberTabIds(Guid groupId)
    {
        TreeNode? group = EnumerateNodes(_tree.Nodes).FirstOrDefault(node =>
            node.Tag is GroupTag tag && tag.GroupId == groupId);
        return group?.Nodes.Cast<TreeNode>()
            .Select(node => node.Tag is TabTag tag ? GetTabItem(tag.CategoryIndex, tag.Index)?.BrowserTabId : null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToArray() ?? Array.Empty<Guid>();
    }

    internal bool IsTreeGroupExpanded(Guid groupId)
        => EnumerateNodes(_tree.Nodes).FirstOrDefault(node => node.Tag is GroupTag tag && tag.GroupId == groupId)?.IsExpanded == true;

    public void ApplyThemeColors(Color borderColor, Color backColor, Color foreColor)
    {
        BackColor = backColor;
        ForeColor = foreColor;
        ActiveTabTextColor = MidFDColors.ListSelectedFore;
        _treeHost.BackColor = borderColor;
        _splitter.BackColor = backColor;
        _splitterBorderColor = borderColor;
        _splitter.Invalidate();
        _tree.BackColor = backColor;
        _tree.ForeColor = foreColor;
        Invalidate();
        InvalidateTree();
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

    private bool ApplyTabPathPresentation(int categoryIndex, int tabIndex, string? canonicalPath, string fallbackText, string toolTipText, string prefix, bool select, string? baseTitle, string? relativeSuffix, bool invalidate = true)
    {
        TreeNode? node = FindTabNode(categoryIndex, tabIndex);
        if (node == null) return false;

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
            if (!string.Equals(node.Text, text, StringComparison.Ordinal)) node.Text = text;
            if (!string.Equals(node.ToolTipText, toolTipText, StringComparison.Ordinal)) node.ToolTipText = toolTipText;
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
        if (invalidate && (changed || selectionChanged))
        {
            InvalidateTree();
        }
        return changed || selectionChanged;
    }

    private void RebuildTree()
    {
        if (_tree.IsDisposed) return;
        ClearTreeDropIndicator();
        CaptureCollapsedCategories();
        _syncing = true;
        try
        {
            _tree.BeginUpdate();
            _tree.Nodes.Clear();
            for (int categoryIndex = 0; categoryIndex < _categories.Count; categoryIndex++)
            {
                BrowserTabNavigationCategoryItem category = _categories[categoryIndex];
                TreeNode categoryNode = new(CreateCategoryNodeText(category)) { Tag = new CategoryTag(categoryIndex, category.CategoryId) };
                categoryNode.ToolTipText = category.ToolTipText ?? string.Empty;
                if (category.Kind == BrowserTabStripCategoryItemKind.ManageEntry)
                {
                    _tree.Nodes.Add(categoryNode);
                    continue;
                }
                if (_selectedCategoryIndex == categoryIndex) categoryNode.BackColor = Color.FromArgb(48, 48, 48);
                var tabNodesById = new Dictionary<Guid, TreeNode>();
                TreeNode[] tabNodesByIndex = new TreeNode[category.Tabs.Count];
                HashSet<Guid> nestedTabIds = (category.SearchDerivedTabs ?? Array.Empty<BrowserTabNavigationSearchDerivedTab>())
                    .Select(item => item.ResultTabId)
                    .ToHashSet();
                for (int i = 0; i < category.Tabs.Count; i++)
                {
                    BrowserTabStripItem item = category.Tabs[i];
                    TreeNode tabNode = new(item.Text) { Tag = new TabTag(categoryIndex, i, category.CategoryId, item.BrowserTabId) };
                    tabNode.ToolTipText = category.Tabs[i].ToolTipText ?? string.Empty;
                    tabNodesByIndex[i] = tabNode;
                    Guid? itemId = item.BrowserTabId;
                    if (itemId is { } id) tabNodesById[id] = tabNode;
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
                foreach (BrowserTabStripItem item in category.Tabs)
                {
                    if (item.BrowserTabId is not { } sourceTabId
                        || !tabNodesById.TryGetValue(sourceTabId, out TreeNode? sourceTabNode)) continue;
                    bool hasChildren = false;
                    if (category.SearchChild is { } searchChild
                        && searchChild.SourceTabId == sourceTabId
                        && searchChild.SourceCategoryId.Equals(category.CategoryId, StringComparison.OrdinalIgnoreCase))
                    {
                        var childNode = new TreeNode(searchChild.Text)
                        {
                            Tag = new SearchChildTag(categoryIndex, searchChild.SessionId, searchChild.SourceTabId),
                            ToolTipText = searchChild.ToolTipText ?? string.Empty
                        };
                        sourceTabNode.Nodes.Add(childNode);
                        hasChildren = true;
                    }
                    foreach (BrowserTabNavigationSearchDerivedTab derived in category.SearchDerivedTabs ?? Array.Empty<BrowserTabNavigationSearchDerivedTab>())
                    {
                        if (derived.SourceTabId != sourceTabId
                            || !tabNodesById.TryGetValue(derived.ResultTabId, out TreeNode? resultNode)) continue;
                        sourceTabNode.Nodes.Add(resultNode);
                        hasChildren = true;
                    }
                    if (hasChildren && !_collapsedSourceTabIds.Contains(sourceTabId)) sourceTabNode.Expand();
                }
                var groupByMember = (category.UserTabGroups ?? Array.Empty<BrowserTabGroup>())
                    .SelectMany(group => group.MemberTabIds.Select(memberId => (memberId, group)))
                    .ToDictionary(pair => pair.memberId, pair => pair.group);
                var addedGroupIds = new HashSet<Guid>();
                for (int tabIndex = 0; tabIndex < category.Tabs.Count; tabIndex++)
                {
                    BrowserTabStripItem item = category.Tabs[tabIndex];
                    if (item.BrowserTabId is not { } tabId)
                    {
                        categoryNode.Nodes.Add(tabNodesByIndex[tabIndex]);
                        continue;
                    }
                    if (nestedTabIds.Contains(tabId) || !tabNodesById.TryGetValue(tabId, out TreeNode? tabNode)) continue;
                    if (!groupByMember.TryGetValue(tabId, out BrowserTabGroup? group))
                    {
                        categoryNode.Nodes.Add(tabNode);
                        continue;
                    }

                    if (!addedGroupIds.Add(group.Id)) continue;
                    TreeNode groupNode = new(group.DisplayName)
                    {
                        Tag = new GroupTag(categoryIndex, category.CategoryId, group.Id),
                        ToolTipText = $"Tab Group · {group.MemberTabIds.Count} tabs"
                    };
                    categoryNode.Nodes.Add(groupNode);
                    for (int memberIndex = 0; memberIndex < category.Tabs.Count; memberIndex++)
                    {
                        BrowserTabStripItem member = category.Tabs[memberIndex];
                        if (member.BrowserTabId is not { } memberId
                            || !group.MemberTabIds.Contains(memberId)
                            || nestedTabIds.Contains(memberId)
                            || !tabNodesById.ContainsKey(memberId)) continue;
                        TreeNode memberNode = tabNodesByIndex[memberIndex];
                        groupNode.Nodes.Add(memberNode);
                    }
                    if (group.Expanded) groupNode.Expand();
                }
                if (!_collapsedCategoryIds.Contains(category.CategoryId)
                    || category.SearchChild != null
                    || category.SearchDerivedTabs?.Count > 0) categoryNode.Expand();
                _tree.Nodes.Add(categoryNode);
            }
            SelectCurrentNode();
        }
        finally
        {
            _tree.EndUpdate();
            _syncing = false;
        }
        if (_treePresentationRefreshPending)
        {
            FlushPendingTreeNodeTextPresentationRefresh();
        }
        else
        {
            RefreshTreeNodeTextPresentationsNow();
        }
    }

    private void SelectCurrentNode()
    {
        if (_selectedSearchSessionId is { } sessionId)
        {
            foreach (TreeNode category in _tree.Nodes)
            {
                TreeNode? child = EnumerateNodes(category.Nodes).FirstOrDefault(node =>
                    node.Tag is SearchChildTag tag && tag.SessionId == sessionId);
                if (child != null)
                {
                    ExpandAncestors(child);
                    _tree.SelectedNode = child;
                    return;
                }
            }
            _selectedSearchSessionId = null;
        }

        foreach (TreeNode category in _tree.Nodes)
        {
            if (category.Tag is CategoryTag categoryTag
                && categoryTag.Index == _selectedCategoryIndex
                && !category.IsExpanded)
            {
                category.Expand();
            }
            foreach (TreeNode tab in EnumerateNodes(category.Nodes))
            {
                if (tab.Tag is TabTag tag && tag.CategoryIndex == _selectedCategoryIndex && tag.Index == _selectedIndex)
                {
                    TreeNode? groupAncestor = tab.Parent;
                    while (groupAncestor != null && groupAncestor.Tag is not GroupTag) groupAncestor = groupAncestor.Parent;
                    if (groupAncestor is { IsExpanded: false })
                    {
                        _tree.SelectedNode = groupAncestor;
                        return;
                    }
                    ExpandAncestors(tab);
                    _tree.SelectedNode = tab;
                    return;
                }
            }
        }
    }

    private static void ExpandAncestors(TreeNode node)
    {
        for (TreeNode? ancestor = node.Parent; ancestor != null; ancestor = ancestor.Parent)
        {
            if (!ancestor.IsExpanded) ancestor.Expand();
        }
    }

    private void RequestTreeNodeTextPresentationRefresh(bool defer = true)
    {
        _treePresentationRefreshRequestCount++;
        _treePresentationRefreshPending = true;
        if (!defer)
        {
            FlushPendingTreeNodeTextPresentationRefresh();
            return;
        }
        if (_treePresentationRefreshScheduled) return;
        if (_applyingSplitterWidth) return;
        if (IsDisposed || !IsHandleCreated) return;

        _treePresentationRefreshScheduled = true;
        try
        {
            BeginInvoke((MethodInvoker)ProcessScheduledTreeNodeTextPresentationRefresh);
        }
        catch (InvalidOperationException)
        {
            _treePresentationRefreshScheduled = false;
        }
    }

    private void ProcessScheduledTreeNodeTextPresentationRefresh()
    {
        _treePresentationRefreshScheduled = false;
        FlushPendingTreeNodeTextPresentationRefresh();
    }

    private void FlushPendingTreeNodeTextPresentationRefresh()
    {
        if (!_treePresentationRefreshPending) return;
        _treePresentationRefreshPending = false;
        RefreshTreeNodeTextPresentationsNow();
    }

    private void RefreshTreeNodeTextPresentationsNow()
    {
        if (_tree.IsDisposed || !_tree.IsHandleCreated)
        {
            return;
        }

        _treePresentationRefreshCount++;
        _tree.BeginUpdate();
        try
        {
            foreach (TreeNode categoryNode in _tree.Nodes)
            {
                if (categoryNode.Tag is not CategoryTag categoryTag
                    || categoryTag.Index < 0
                    || categoryTag.Index >= _categories.Count)
                {
                    continue;
                }

                BrowserTabNavigationCategoryItem category = _categories[categoryTag.Index];
                string categoryText = FitCategoryNodeText(categoryNode, CreateCategoryNodeText(category));
                if (!string.Equals(categoryNode.Text, categoryText, StringComparison.Ordinal))
                {
                    categoryNode.Text = categoryText;
                }
                foreach (TreeNode tabNode in EnumerateNodes(categoryNode.Nodes))
                {
                    if (tabNode.Tag is not TabTag tabTag
                        || tabTag.Index < 0
                        || tabTag.Index >= category.Tabs.Count)
                    {
                        continue;
                    }

                    if (_pathPresentations.TryGetValue((tabTag.CategoryIndex, tabTag.Index), out PathPresentation? presentation))
                    {
                        ApplyTabPathPresentation(
                            tabTag.CategoryIndex,
                            tabTag.Index,
                            presentation.CanonicalPath,
                            presentation.FallbackText,
                            presentation.ToolTipText,
                            presentation.Prefix,
                            select: false,
                            presentation.BaseTitle,
                            presentation.RelativeSuffix,
                            invalidate: false);
                    }
                    else
                    {
                        string text = FitNodeText(tabNode, category.Tabs[tabTag.Index].Text);
                        if (!string.Equals(tabNode.Text, text, StringComparison.Ordinal))
                        {
                            tabNode.Text = text;
                        }
                    }
                }
            }
        }
        finally
        {
            _tree.EndUpdate();
        }
    }

    private void InvalidateTree()
    {
        _treeInvalidationCount++;
        _tree.Invalidate();
    }

    private string FitCategoryNodeText(TreeNode node, string text)
    {
        int availableWidth = GetNodeTextAvailableWidth(node);
        Func<string, int> measure = value => TextRenderer.MeasureText(
            value,
            _tree.Font,
            Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
        string suffix = "\u00A0\u00A0\u00A0\u00A0";
        int suffixWidth = measure(suffix);
        if (suffixWidth >= availableWidth)
        {
            return BrowserTabNavigationPathPresentationHelper.MiddleEllipsize(text, availableWidth, measure);
        }

        string label = text.EndsWith(suffix, StringComparison.Ordinal)
            ? text[..^suffix.Length]
            : text;
        return BrowserTabNavigationPathPresentationHelper.MiddleEllipsize(
            label,
            availableWidth - suffixWidth,
            measure) + suffix;
    }

    private string FitNodeText(TreeNode node, string text)
    {
        int availableWidth = GetNodeTextAvailableWidth(node);
        Func<string, int> measure = value => TextRenderer.MeasureText(
            value,
            _tree.Font,
            Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
        return BrowserTabNavigationPathPresentationHelper.MiddleEllipsize(text, availableWidth, measure);
    }

    private bool HasNodeTextPresentationOverflow(TreeNode node)
    {
        int textWidth = TextRenderer.MeasureText(
            node.Text,
            _tree.Font,
            Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
        return textWidth > GetNodeTextAvailableWidth(node);
    }

    private int GetNodeTextAvailableWidth(TreeNode node) => Math.Max(
        1,
        _tree.ClientSize.Width - node.Bounds.Left - SystemInformation.VerticalScrollBarWidth - 2);

    private TreeNode? FindTabNode(int categoryIndex, int tabIndex)
    {
        if (categoryIndex < 0 || categoryIndex >= _tree.Nodes.Count) return null;
        foreach (TreeNode node in EnumerateNodes(_tree.Nodes[categoryIndex].Nodes))
        {
            if (node.Tag is TabTag tag && tag.CategoryIndex == categoryIndex && tag.Index == tabIndex) return node;
        }
        return null;
    }

    private string BuildPathPresentation(TreeNode node, string? canonicalPath, string fallbackText, string prefix, string? baseTitle, string? relativeSuffix)
    {
        if (baseTitle == null && string.IsNullOrWhiteSpace(canonicalPath)) return fallbackText;
        int availableWidth = GetNodeTextAvailableWidth(node);
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
        if (node.Tag is GroupTag group)
        {
            if (e.Button == MouseButtons.Right)
            {
                GroupContextMenuRequested?.Invoke(this, new BrowserTabGroupNavigationEventArgs(group.GroupId, e.Location));
            }
            return;
        }
        if (node.Tag is SearchChildTag searchChild)
        {
            if (e.Button == MouseButtons.Left) ActivateSearchChild(searchChild);
            return;
        }
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
                if (e.Button == MouseButtons.Right)
                {
                    BrowserTabStripItem? item = GetTabItem(tab.CategoryIndex, tab.Index);
                    TabRightClicked?.Invoke(this, new BrowserTabStripMouseEventArgs(
                        tab.Index,
                        e.Button,
                        e.Location,
                        _categories[tab.CategoryIndex].CategoryId,
                        item?.BrowserTabId));
                    return;
                }
                CategoryClicked?.Invoke(this, new BrowserTabStripCategoryEventArgs(tab.CategoryIndex, _categories[tab.CategoryIndex].CategoryId, _categories[tab.CategoryIndex].Kind, e.Button, e.Location, tab.Index));
                return;
            }
            if (e.Button == MouseButtons.Right)
            {
                BrowserTabStripItem? item = GetTabItem(tab.CategoryIndex, tab.Index);
                TabRightClicked?.Invoke(this, new BrowserTabStripMouseEventArgs(
                    tab.Index,
                    e.Button,
                    e.Location,
                    _categories[tab.CategoryIndex].CategoryId,
                    item?.BrowserTabId));
                return;
            }
            bool wasSelectedTabLeftClick = e.Button == MouseButtons.Left && tab.Index == _selectedIndex;
            SelectedIndex = tab.Index;
            if (wasSelectedTabLeftClick)
            {
                NotifySelectedTabReclicked(tab.Index, e.Button, e.Location);
            }
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
        if (e.KeyCode is Keys.Enter or Keys.Space)
        {
            if (ActivateSelectedNode())
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            return;
        }
        if (!(e.KeyCode == Keys.Apps || (e.KeyCode == Keys.F10 && e.Shift))) return;
        TreeNode? node = _tree.SelectedNode;
        if (node?.Tag is GroupTag groupTag)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            GroupContextMenuRequested?.Invoke(this, new BrowserTabGroupNavigationEventArgs(groupTag.GroupId, node.Bounds.Location));
            return;
        }
        if (node?.Tag is TabTag tabTag
            && tabTag.CategoryIndex >= 0
            && tabTag.CategoryIndex < _categories.Count
            && tabTag.Index >= 0
            && tabTag.Index < _categories[tabTag.CategoryIndex].Tabs.Count)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            BrowserTabStripItem? tab = GetTabItem(tabTag.CategoryIndex, tabTag.Index);
            TabRightClicked?.Invoke(this, new BrowserTabStripMouseEventArgs(
                tabTag.Index,
                MouseButtons.Right,
                node.Bounds.Location,
                _categories[tabTag.CategoryIndex].CategoryId,
                tab?.BrowserTabId));
            return;
        }
        TreeNode? categoryNode = node;
        while (categoryNode != null && categoryNode.Tag is not CategoryTag) categoryNode = categoryNode.Parent;
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

    internal bool ActivateSelectedNode()
    {
        if (_tree.SelectedNode?.Tag is GroupTag)
        {
            if (_tree.SelectedNode.IsExpanded) _tree.SelectedNode.Collapse();
            else _tree.SelectedNode.Expand();
            return true;
        }
        if (_tree.SelectedNode?.Tag is SearchChildTag searchChild)
        {
            ActivateSearchChild(searchChild);
            return true;
        }
        if (_tree.SelectedNode?.Tag is not TabTag tab
            || tab.CategoryIndex < 0 || tab.CategoryIndex >= _categories.Count
            || tab.Index < 0 || tab.Index >= _categories[tab.CategoryIndex].Tabs.Count)
        {
            return false;
        }

        if (tab.CategoryIndex != _selectedCategoryIndex)
        {
            BrowserTabNavigationCategoryItem category = _categories[tab.CategoryIndex];
            CategoryClicked?.Invoke(this, new BrowserTabStripCategoryEventArgs(
                tab.CategoryIndex, category.CategoryId, category.Kind, MouseButtons.Left, _tree.SelectedNode.Bounds.Location, tab.Index));
            return true;
        }

        bool alreadySelected = _selectedIndex == tab.Index && _selectedSearchSessionId == null;
        SelectedIndex = tab.Index;
        if (alreadySelected) NotifySelectedTabReclicked(tab.Index, MouseButtons.Left, _tree.SelectedNode.Bounds.Location);
        return true;
    }

    private void ActivateSearchChild(SearchChildTag tag)
    {
        if (tag.CategoryIndex < 0 || tag.CategoryIndex >= _categories.Count
            || _categories[tag.CategoryIndex].SearchChild is not { } child
            || child.SessionId != tag.SessionId
            || child.SourceTabId != tag.SourceTabId)
        {
            return;
        }

        _selectedCategoryIndex = tag.CategoryIndex;
        _selectedIndex = -1;
        _selectedSearchSessionId = tag.SessionId;
        if (!_syncing) SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Tree_DrawNode(object? sender, DrawTreeNodeEventArgs e)
    {
        bool activeTab = e.Node?.Tag is TabTag tab
                && tab.CategoryIndex == _selectedCategoryIndex
                && tab.Index == _selectedIndex
            || e.Node?.Tag is SearchChildTag searchChild
                && searchChild.CategoryIndex == _selectedCategoryIndex
                && searchChild.SessionId == _selectedSearchSessionId;
        Color backColor = activeTab ? MidFDColors.ListSelectedBack : _tree.BackColor;
        Color foreColor = activeTab ? ActiveTabTextColor : ForeColor;
        using var brush = new SolidBrush(backColor);
        e.Graphics.FillRectangle(brush, e.Bounds);
        if (e.Node?.Tag is CategoryTag category && category.Index >= 0 && category.Index < _categories.Count)
        {
            string categoryText = (e.Node.Text ?? string.Empty).TrimEnd('\u00A0');
            TextRenderer.DrawText(e.Graphics, categoryText, _tree.Font, e.Bounds, foreColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            if (_categories[category.Index].Kind == BrowserTabStripCategoryItemKind.Category)
            {
                TextRenderer.DrawText(e.Graphics, "＋", _tree.Font, GetCategoryAddTabBounds(e.Node), foreColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            DrawAppendDropHighlight(e);
            return;
        }

        TextRenderer.DrawText(e.Graphics, e.Node?.Text ?? string.Empty, _tree.Font, e.Bounds, foreColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        DrawAppendDropHighlight(e);
    }

    private void DrawAppendDropHighlight(DrawTreeNodeEventArgs e)
    {
        if (e.Node == null
            || !ReferenceEquals(_dropIndicatorNode, e.Node)
            || _dropIndicatorPlacement is not (BrowserTabTreeDropPlacement.AppendToGroup or BrowserTabTreeDropPlacement.AppendToCategory))
        {
            return;
        }

        Rectangle bounds = e.Bounds;
        bounds.Inflate(-1, -1);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        using var pen = CreateDropIndicatorPen(DropIndicatorColor);
        e.Graphics.DrawRectangle(pen, bounds);
    }

    private void Tree_AfterNativePaint(Graphics graphics)
    {
        if (_dropIndicatorNode is not { } node
            || _dropIndicatorPlacement is not (BrowserTabTreeDropPlacement.Before or BrowserTabTreeDropPlacement.After)
            || node.TreeView != _tree)
        {
            return;
        }

        DrawDropIndicatorLine(graphics, _tree.ClientRectangle, node.Bounds, _dropIndicatorPlacement.Value, DropIndicatorColor);
    }

    internal static bool DrawDropIndicatorLine(
        Graphics graphics,
        Rectangle clientBounds,
        Rectangle nodeBounds,
        BrowserTabTreeDropPlacement placement,
        Color foregroundColor)
    {
        if (placement is not (BrowserTabTreeDropPlacement.Before or BrowserTabTreeDropPlacement.After)
            || clientBounds.Width <= 0
            || clientBounds.Height <= 0
            || nodeBounds.Width <= 0
            || nodeBounds.Height <= 0)
        {
            return false;
        }

        int lineY = placement == BrowserTabTreeDropPlacement.Before ? nodeBounds.Top : nodeBounds.Bottom;
        if (lineY < clientBounds.Top || lineY > clientBounds.Bottom) return false;
        if (lineY == clientBounds.Bottom) lineY--;
        if (lineY < clientBounds.Top) return false;

        GraphicsState graphicsState = graphics.Save();
        try
        {
            graphics.SetClip(clientBounds, CombineMode.Intersect);
            using Pen pen = CreateDropIndicatorPen(foregroundColor);
            graphics.DrawLine(pen, clientBounds.Left, lineY + 0.5f, clientBounds.Right - 1, lineY + 0.5f);
        }
        finally
        {
            graphics.Restore(graphicsState);
        }

        return true;
    }

    internal static Pen CreateDropIndicatorPen(Color foregroundColor)
        => new(foregroundColor, 1f) { DashStyle = DashStyle.Dot };

    private void Splitter_Paint(object? sender, PaintEventArgs e)
    {
        if (_splitter.ClientSize.Width <= 0 || _splitter.ClientSize.Height <= 0) return;
        using var pen = new Pen(_splitterBorderColor, 1);
        int x = _splitter.ClientSize.Width - 1;
        e.Graphics.DrawLine(pen, x, 0, x, _splitter.ClientSize.Height - 1);
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
        if (e.Node == null) return;

        SetCategoryCollapsed(e.Node, collapsed: false);
        SetSourceTabCollapsed(e.Node, collapsed: false);
        if (!_syncing && e.Node.Tag is GroupTag group)
        {
            GroupExpandedChanged?.Invoke(this, new BrowserTabGroupExpandedEventArgs(group.GroupId, expanded: true));
        }
        if (!_syncing && e.Node.Tag is CategoryTag)
        {
            RequestExpandedCategoryTextPresentationRefresh(e.Node);
        }
    }

    private void RequestExpandedCategoryTextPresentationRefresh(TreeNode categoryNode)
    {
        if (IsDisposed || !IsHandleCreated) return;

        try
        {
            BeginInvoke((MethodInvoker)(() =>
            {
                if (IsDisposed
                    || categoryNode.TreeView != _tree
                    || !categoryNode.IsExpanded
                    || !EnumerateNodes(categoryNode.Nodes).Any(HasNodeTextPresentationOverflow))
                {
                    return;
                }

                _expandedCategoryRefreshRequestCount++;
                RequestTreeNodeTextPresentationRefresh(defer: false);
            }));
        }
        catch (InvalidOperationException)
        {
        }
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
        if (e.Node == null) return;
        SetCategoryCollapsed(e.Node, collapsed: true);
        SetSourceTabCollapsed(e.Node, collapsed: true);
        if (e.Node.Tag is GroupTag group)
        {
            if (!_syncing) GroupExpandedChanged?.Invoke(this, new BrowserTabGroupExpandedEventArgs(group.GroupId, expanded: false));
            if (IsDescendantOf(_tree.SelectedNode, e.Node)) _tree.SelectedNode = e.Node;
        }
    }

    private void CaptureCollapsedCategories()
    {
        foreach (TreeNode node in _tree.Nodes)
        {
            SetCategoryCollapsed(node, !node.IsExpanded);
            foreach (TreeNode child in EnumerateNodes(node.Nodes))
            {
                SetSourceTabCollapsed(child, !child.IsExpanded);
            }
        }
    }

    private void SetCategoryCollapsed(TreeNode node, bool collapsed)
    {
        if (_syncing || node.Tag is not CategoryTag category || category.Index < 0 || category.Index >= _categories.Count) return;
        string categoryId = _categories[category.Index].CategoryId;
        if (collapsed) _collapsedCategoryIds.Add(categoryId);
        else _collapsedCategoryIds.Remove(categoryId);
    }

    private void SetSourceTabCollapsed(TreeNode node, bool collapsed)
    {
        if (_syncing || node.Tag is not TabTag tabTag || tabTag.CategoryIndex < 0 || tabTag.CategoryIndex >= _categories.Count) return;
        BrowserTabStripItem? item = GetTabItem(tabTag.CategoryIndex, tabTag.Index);
        if (item?.BrowserTabId is not { } sourceTabId
            || !(_categories[tabTag.CategoryIndex].SearchChild?.SourceTabId == sourceTabId
                || (_categories[tabTag.CategoryIndex].SearchDerivedTabs?.Any(derived => derived.SourceTabId == sourceTabId) ?? false))) return;
        if (collapsed) _collapsedSourceTabIds.Add(sourceTabId);
        else _collapsedSourceTabIds.Remove(sourceTabId);
    }

    private static IEnumerable<TreeNode> EnumerateNodes(TreeNodeCollection nodes)
    {
        foreach (TreeNode node in nodes)
        {
            yield return node;
            foreach (TreeNode descendant in EnumerateNodes(node.Nodes)) yield return descendant;
        }
    }

    private static bool IsDescendantOf(TreeNode? node, TreeNode ancestor)
    {
        for (TreeNode? current = node; current != null; current = current.Parent)
        {
            if (ReferenceEquals(current, ancestor)) return true;
        }
        return false;
    }

    private static bool UserGroupsEqual(
        IReadOnlyList<BrowserTabGroup>? left,
        IReadOnlyList<BrowserTabGroup>? right)
    {
        BrowserTabGroup[] leftGroups = (left ?? Array.Empty<BrowserTabGroup>()).ToArray();
        BrowserTabGroup[] rightGroups = (right ?? Array.Empty<BrowserTabGroup>()).ToArray();
        return leftGroups.Length == rightGroups.Length
            && leftGroups.Zip(rightGroups).All(pair => pair.First.Id == pair.Second.Id
                && string.Equals(pair.First.CategoryId, pair.Second.CategoryId, StringComparison.OrdinalIgnoreCase)
                && pair.First.DisplayName == pair.Second.DisplayName
                && pair.First.Expanded == pair.Second.Expanded
                && pair.First.CreationOrder == pair.Second.CreationOrder
                && pair.First.MemberTabIds.SequenceEqual(pair.Second.MemberTabIds));
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
        RequestSplitterWidth(newWidth);
    }

    internal void RequestSplitterWidth(int requestedWidth)
    {
        int normalizedWidth = Math.Clamp(requestedWidth, 120, 600);
        if (normalizedWidth == Width)
        {
            _pendingSplitterWidth = null;
            return;
        }

        _pendingSplitterWidth = normalizedWidth;
        if (_draggingSplitter)
        {
            _splitterWidthThrottleTimer.Start();
            return;
        }
        if (_splitterWidthUpdateScheduled) return;
        if (IsDisposed || !IsHandleCreated)
        {
            ApplyPendingSplitterWidth();
            return;
        }

        _splitterWidthUpdateScheduled = true;
        try
        {
            BeginInvoke((MethodInvoker)ApplyPendingSplitterWidth);
        }
        catch (InvalidOperationException)
        {
            _splitterWidthUpdateScheduled = false;
            ApplyPendingSplitterWidth();
        }
    }

    private void ApplyPendingSplitterWidth()
    {
        _splitterWidthUpdateScheduled = false;
        if (_pendingSplitterWidth.HasValue)
        {
            int requestedWidth = _pendingSplitterWidth.Value;
            _pendingSplitterWidth = null;
            if (requestedWidth != Width)
            {
                _splitterWidthApplyCount++;
                _applyingSplitterWidth = true;
                try
                {
                    Width = requestedWidth;
                }
                finally
                {
                    _applyingSplitterWidth = false;
                }
            }
        }
        FlushPendingTreeNodeTextPresentationRefresh();
        if (!_pendingSplitterWidth.HasValue) _splitterWidthThrottleTimer.Stop();
    }

    private void Splitter_MouseUp(object? sender, MouseEventArgs e)
    {
        if (!_draggingSplitter) return;
        int finalWidth = Math.Clamp(_splitterStartWidth + Control.MousePosition.X - _splitterStartX, 120, 600);
        RequestSplitterWidth(finalWidth);
        _splitterWidthThrottleTimer.Stop();
        ApplyPendingSplitterWidth();
        _draggingSplitter = false;
        NavigationWidthChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Tree_NodeMouseDoubleClick(object? sender, TreeNodeMouseClickEventArgs e)
    {
        if (e.Node?.Tag is TabTag tab && tab.CategoryIndex == _selectedCategoryIndex) TabDoubleClicked?.Invoke(this, new BrowserTabStripMouseEventArgs(tab.Index, e.Button, e.Location));
    }

    private void Tree_ItemDrag(object? sender, ItemDragEventArgs e)
    {
        if (e.Item is not TreeNode source || !IsValidTreeDragSource(source)) return;
        try
        {
            _tree.DoDragDrop(source, DragDropEffects.Move);
        }
        finally
        {
            ClearTreeDropIndicator();
        }
    }

    private void Tree_DragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = e.Data?.GetData(typeof(TreeNode)) is TreeNode source && IsValidTreeDragSource(source)
            ? DragDropEffects.Move
            : DragDropEffects.None;
    }

    private void Tree_DragOver(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(typeof(TreeNode)) is not TreeNode source
            || !IsValidTreeDragSource(source))
        {
            e.Effect = DragDropEffects.None;
            ClearTreeDropIndicator();
            return;
        }

        Point clientPoint = _tree.PointToClient(new Point(e.X, e.Y));
        TreeNode? target = _tree.GetNodeAt(clientPoint);
        if (target != null && TryCreateTreeDropIntent(source, target, clientPoint.Y, out BrowserTabTreeDropIntent? intent))
        {
            e.Effect = DragDropEffects.Move;
            SetTreeDropIndicator(target, source, intent!);
            return;
        }

        e.Effect = DragDropEffects.None;
        ClearTreeDropIndicator();
    }

    private void Tree_DragLeave(object? sender, EventArgs e) => ClearTreeDropIndicator();

    private void Tree_DragDrop(object? sender, DragEventArgs e)
    {
        try
        {
            if (e.Effect != DragDropEffects.Move
                || e.Data?.GetData(typeof(TreeNode)) is not TreeNode source
                || !IsValidTreeDragSource(source)) return;

            Point clientPoint = _tree.PointToClient(new Point(e.X, e.Y));
            TreeNode? target = _tree.GetNodeAt(clientPoint);
            BrowserTabTreeDropIntent? intent = null;
            if (target != null)
            {
                if (!TryCreateTreeDropIntent(source, target, clientPoint.Y, out intent)) return;
            }
            else if (_dropIndicatorNode?.TreeView == _tree
                && _dropIndicatorSourceTabId == (source.Tag as TabTag)?.BrowserTabId)
            {
                intent = _dropIndicatorIntent;
            }

            if (intent == null) return;

            BrowserTabTreeDropRequested?.Invoke(this, new BrowserTabTreeDropEventArgs(intent));
        }
        finally
        {
            ClearTreeDropIndicator();
        }
    }

    private bool IsValidTreeDragSource(TreeNode source)
    {
        if (source.Tag is not TabTag { BrowserTabId: { } tabId } tag
            || tabId == Guid.Empty
            || !TryFindCategory(tag.CategoryId, out BrowserTabNavigationCategoryItem? category)
            || category!.Kind != BrowserTabStripCategoryItemKind.Category
            || !TryFindTab(category, tabId, out BrowserTabStripItem? tab)
            || tab!.Kind != BrowserTabStripItemKind.Browser
            || IsSearchDerivedTab(category, tabId))
        {
            return false;
        }

        BrowserTabGroup? sourceGroup = FindMemberGroup(category, tabId, out bool ambiguousMembership);
        if (ambiguousMembership) return false;
        if (sourceGroup != null)
        {
            return source.Parent?.Tag is GroupTag parentGroup
                && parentGroup.GroupId == sourceGroup.Id
                && string.Equals(parentGroup.CategoryId, category.CategoryId, StringComparison.OrdinalIgnoreCase);
        }

        return source.Parent?.Tag is CategoryTag parentCategory
            && string.Equals(parentCategory.CategoryId, category.CategoryId, StringComparison.OrdinalIgnoreCase);
    }

    private bool TryCreateTreeDropIntent(
        TreeNode source,
        TreeNode target,
        int pointerY,
        out BrowserTabTreeDropIntent? intent)
    {
        intent = null;
        if (!IsValidTreeDragSource(source)
            || source.Tag is not TabTag { BrowserTabId: { } sourceTabId } sourceTag
            || !TryFindCategory(sourceTag.CategoryId, out BrowserTabNavigationCategoryItem? category))
        {
            return false;
        }

        if (!TryGetDropDestination(category!, target, pointerY, sourceTabId, out Guid? destinationGroupId, out Guid? anchorTabId, out BrowserTabTreeDropPlacement placement))
        {
            return false;
        }

        BrowserTabGroup? sourceGroup = FindMemberGroup(category!, sourceTabId, out bool ambiguousSourceMembership);
        if (ambiguousSourceMembership) return false;
        bool requiresMembershipChange = sourceGroup?.Id != destinationGroupId;
        IReadOnlyList<Guid> orderedTabIds = GetOrderedBrowserTabIds(category!);
        if (!TryResolveDropAnchor(category!, orderedTabIds, sourceTabId, destinationGroupId, anchorTabId, placement, out Guid? resolvedAnchorTabId, out bool insertAfter))
        {
            return false;
        }

        bool requiresReorder = TryBuildOrderedTabIds(orderedTabIds, sourceTabId, resolvedAnchorTabId, insertAfter, out IReadOnlyList<Guid> reorderedTabIds)
            && !orderedTabIds.SequenceEqual(reorderedTabIds);
        intent = new BrowserTabTreeDropIntent(
            category!.CategoryId,
            sourceTabId,
            category.CategoryId,
            destinationGroupId,
            placement is BrowserTabTreeDropPlacement.Before or BrowserTabTreeDropPlacement.After ? anchorTabId : resolvedAnchorTabId,
            placement,
            requiresMembershipChange,
            requiresReorder);
        return true;
    }

    private bool TryGetDropDestination(
        BrowserTabNavigationCategoryItem category,
        TreeNode target,
        int pointerY,
        Guid sourceTabId,
        out Guid? destinationGroupId,
        out Guid? anchorTabId,
        out BrowserTabTreeDropPlacement placement)
    {
        destinationGroupId = null;
        anchorTabId = null;
        placement = default;
        if (target.Tag is GroupTag targetGroup)
        {
            if (!string.Equals(targetGroup.CategoryId, category.CategoryId, StringComparison.OrdinalIgnoreCase)
                || FindGroup(category, targetGroup.GroupId) == null) return false;
            destinationGroupId = targetGroup.GroupId;
            placement = BrowserTabTreeDropPlacement.AppendToGroup;
            return true;
        }

        if (target.Tag is CategoryTag targetCategory)
        {
            if (!string.Equals(targetCategory.CategoryId, category.CategoryId, StringComparison.OrdinalIgnoreCase)
                || target.Parent != null
                || category.Kind != BrowserTabStripCategoryItemKind.Category) return false;
            placement = BrowserTabTreeDropPlacement.AppendToCategory;
            return true;
        }

        if (target.Tag is not TabTag { BrowserTabId: { } targetTabId } targetTag
            || targetTabId == Guid.Empty
            || targetTabId == sourceTabId
            || !string.Equals(targetTag.CategoryId, category.CategoryId, StringComparison.OrdinalIgnoreCase)
            || !TryFindTab(category, targetTabId, out BrowserTabStripItem? targetTab)
            || targetTab!.Kind != BrowserTabStripItemKind.Browser
            || IsSearchDerivedTab(category, targetTabId))
        {
            return false;
        }

        BrowserTabGroup? targetTabGroup = FindMemberGroup(category, targetTabId, out bool ambiguousTargetMembership);
        if (ambiguousTargetMembership) return false;
        if (target.Parent?.Tag is GroupTag parentGroup)
        {
            if (targetTabGroup?.Id != parentGroup.GroupId
                || !string.Equals(parentGroup.CategoryId, category.CategoryId, StringComparison.OrdinalIgnoreCase)) return false;
            destinationGroupId = parentGroup.GroupId;
        }
        else if (target.Parent?.Tag is CategoryTag parentCategory)
        {
            if (targetTabGroup != null
                || !string.Equals(parentCategory.CategoryId, category.CategoryId, StringComparison.OrdinalIgnoreCase)) return false;
        }
        else
        {
            return false;
        }

        anchorTabId = targetTabId;
        placement = BrowserTabTreeDropGeometry.ResolveRowPlacement(pointerY, target.Bounds.Top, target.Bounds.Height);
        return true;
    }

    private static bool TryResolveDropAnchor(
        BrowserTabNavigationCategoryItem category,
        IReadOnlyList<Guid> orderedTabIds,
        Guid sourceTabId,
        Guid? destinationGroupId,
        Guid? requestedAnchorTabId,
        BrowserTabTreeDropPlacement placement,
        out Guid? anchorTabId,
        out bool insertAfter)
    {
        anchorTabId = requestedAnchorTabId;
        insertAfter = placement is BrowserTabTreeDropPlacement.After
            or BrowserTabTreeDropPlacement.AppendToGroup
            or BrowserTabTreeDropPlacement.AppendToCategory;
        if (placement == BrowserTabTreeDropPlacement.AppendToGroup)
        {
            if (destinationGroupId is not { } groupId) return false;
            BrowserTabGroup? group = FindGroup(category, groupId);
            if (group == null) return false;
            anchorTabId = orderedTabIds.LastOrDefault(id => id != sourceTabId && group.MemberTabIds.Contains(id));
            if (anchorTabId == Guid.Empty) anchorTabId = null;
        }
        else if (placement == BrowserTabTreeDropPlacement.AppendToCategory)
        {
            anchorTabId = orderedTabIds.LastOrDefault(id => id != sourceTabId && !IsSearchDerivedTab(category, id));
            if (anchorTabId == Guid.Empty) anchorTabId = null;
        }

        if (anchorTabId is { } anchor && (anchor == sourceTabId || !orderedTabIds.Contains(anchor))) return false;
        return placement is BrowserTabTreeDropPlacement.Before
            or BrowserTabTreeDropPlacement.After
            or BrowserTabTreeDropPlacement.AppendToGroup
            or BrowserTabTreeDropPlacement.AppendToCategory;
    }

    private static bool TryBuildOrderedTabIds(
        IReadOnlyList<Guid> currentTabIds,
        Guid sourceTabId,
        Guid? anchorTabId,
        bool insertAfter,
        out IReadOnlyList<Guid> reorderedTabIds)
    {
        var result = currentTabIds.Where(id => id != sourceTabId).ToList();
        int insertionIndex = 0;
        if (anchorTabId is { } anchor)
        {
            int anchorIndex = result.IndexOf(anchor);
            if (anchorIndex < 0)
            {
                reorderedTabIds = currentTabIds;
                return false;
            }

            insertionIndex = anchorIndex + (insertAfter ? 1 : 0);
        }

        if (!currentTabIds.Contains(sourceTabId))
        {
            reorderedTabIds = currentTabIds;
            return false;
        }

        result.Insert(Math.Clamp(insertionIndex, 0, result.Count), sourceTabId);
        reorderedTabIds = result;
        return true;
    }

    private static IReadOnlyList<Guid> GetOrderedBrowserTabIds(BrowserTabNavigationCategoryItem category)
        => category.Tabs
            .Where(item => item.Kind == BrowserTabStripItemKind.Browser && item.BrowserTabId.HasValue)
            .Select(item => item.BrowserTabId!.Value)
            .ToArray();

    private bool TryFindCategory(string categoryId, out BrowserTabNavigationCategoryItem? category)
    {
        category = null;
        if (string.IsNullOrWhiteSpace(categoryId)) return false;
        category = _categories.FirstOrDefault(item => string.Equals(item.CategoryId, categoryId, StringComparison.OrdinalIgnoreCase));
        return category != null;
    }

    private static bool TryFindTab(BrowserTabNavigationCategoryItem category, Guid tabId, out BrowserTabStripItem? tab)
    {
        BrowserTabStripItem[] matches = category.Tabs.Where(item => item.BrowserTabId == tabId).Take(2).ToArray();
        tab = matches.FirstOrDefault();
        return matches.Length == 1;
    }

    private static BrowserTabGroup? FindGroup(BrowserTabNavigationCategoryItem category, Guid groupId)
        => (category.UserTabGroups ?? Array.Empty<BrowserTabGroup>()).FirstOrDefault(group => group.Id == groupId);

    private static BrowserTabGroup? FindMemberGroup(BrowserTabNavigationCategoryItem category, Guid tabId, out bool ambiguous)
    {
        BrowserTabGroup[] memberships = (category.UserTabGroups ?? Array.Empty<BrowserTabGroup>())
            .Where(group => group.MemberTabIds.Contains(tabId))
            .Take(2)
            .ToArray();
        ambiguous = memberships.Length > 1;
        return memberships.FirstOrDefault();
    }

    private static bool IsSearchDerivedTab(BrowserTabNavigationCategoryItem category, Guid tabId)
        => (category.SearchDerivedTabs ?? Array.Empty<BrowserTabNavigationSearchDerivedTab>())
            .Any(item => item.ResultTabId == tabId);

    private void SetTreeDropIndicator(TreeNode target, TreeNode source, BrowserTabTreeDropIntent intent)
    {
        if (ReferenceEquals(_dropIndicatorNode, target)
            && _dropIndicatorPlacement == intent.Placement
            && _dropIndicatorSourceTabId == (source.Tag as TabTag)?.BrowserTabId)
        {
            _dropIndicatorIntent = intent;
            return;
        }

        _dropIndicatorNode = target;
        _dropIndicatorPlacement = intent.Placement;
        _dropIndicatorSourceTabId = (source.Tag as TabTag)?.BrowserTabId;
        _dropIndicatorIntent = intent;
        if (!_tree.IsDisposed) _tree.Invalidate();
    }

    private void ClearTreeDropIndicator()
    {
        bool changed = _dropIndicatorNode != null || _dropIndicatorPlacement.HasValue || _dropIndicatorIntent != null;
        _dropIndicatorNode = null;
        _dropIndicatorPlacement = null;
        _dropIndicatorSourceTabId = null;
        _dropIndicatorIntent = null;
        if (changed && !_tree.IsDisposed) _tree.Invalidate();
    }

    private sealed class BrowserTabNavigationTreeView : TreeView
    {
        private const int TreeViewStyleNoHorizontalScroll = 0x00008000; // CommCtrl.h: TVS_NOHSCROLL
        private const int WmPaint = 0x000F;

        public event Action<Graphics>? AfterNativePaint;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.Style |= TreeViewStyleNoHorizontalScroll;
                return parameters;
            }
        }

        protected override void WndProc(ref Message m)
        {
            int message = m.Msg;
            base.WndProc(ref m);
            if (message != WmPaint || IsDisposed || !IsHandleCreated || AfterNativePaint == null) return;

            using Graphics graphics = CreateGraphics();
            AfterNativePaint.Invoke(graphics);
        }
    }

    private sealed record TabTag(int CategoryIndex, int Index, string CategoryId, Guid? BrowserTabId);
    private sealed record CategoryTag(int Index, string CategoryId);
    private sealed record GroupTag(int CategoryIndex, string CategoryId, Guid GroupId);
    private sealed record SearchChildTag(int CategoryIndex, Guid SessionId, Guid SourceTabId);
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
    BrowserTabStripCategoryItemKind Kind = BrowserTabStripCategoryItemKind.Category,
    BrowserTabNavigationSearchChild? SearchChild = null,
    IReadOnlyList<BrowserTabNavigationSearchDerivedTab>? SearchDerivedTabs = null,
    IReadOnlyList<BrowserTabGroup>? UserTabGroups = null);

public sealed class BrowserTabGroupNavigationEventArgs(Guid groupId, Point location) : EventArgs
{
    public Guid GroupId { get; } = groupId;
    public Point Location { get; } = location;
}

public sealed class BrowserTabGroupExpandedEventArgs(Guid groupId, bool expanded) : EventArgs
{
    public Guid GroupId { get; } = groupId;
    public bool Expanded { get; } = expanded;
}

public enum BrowserTabTreeDropPlacement
{
    Before,
    After,
    AppendToGroup,
    AppendToCategory
}

public static class BrowserTabTreeDropGeometry
{
    public static BrowserTabTreeDropPlacement ResolveRowPlacement(int pointerY, int rowTop, int rowHeight)
    {
        if (rowHeight <= 0) throw new ArgumentOutOfRangeException(nameof(rowHeight));
        return pointerY < rowTop + rowHeight / 2
            ? BrowserTabTreeDropPlacement.Before
            : BrowserTabTreeDropPlacement.After;
    }
}

public sealed record BrowserTabTreeDropIntent(
    string SourceCategoryId,
    Guid SourceTabId,
    string DestinationCategoryId,
    Guid? DestinationGroupId,
    Guid? AnchorTabId,
    BrowserTabTreeDropPlacement Placement,
    bool RequiresMembershipChange,
    bool RequiresReorder);

public sealed class BrowserTabTreeDropEventArgs(BrowserTabTreeDropIntent intent) : EventArgs
{
    public BrowserTabTreeDropIntent Intent { get; } = intent;
}

public sealed record BrowserTabNavigationSearchChild(
    string SourceCategoryId,
    Guid SessionId,
    Guid SourceTabId,
    string Text,
    string? ToolTipText);

public sealed record BrowserTabNavigationSearchDerivedTab(
    Guid ResultTabId,
    string SourceCategoryId,
    Guid SourceTabId,
    long CreationOrder);
