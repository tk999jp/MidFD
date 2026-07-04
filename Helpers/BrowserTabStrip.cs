using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using MidFD.Models;
using MidFD.Services;

using System.ComponentModel;

namespace MidFD.Helpers;

public sealed class BrowserTabStrip : Control
{
    public const string ManageCategoriesEntryId = "__manage_categories__";
    private const int MinimumPartialTabWidth = 1;

    private readonly List<BrowserTabStripCategoryItem> _categories = new();
    private readonly List<Rectangle> _categoryBounds = new();
    private readonly List<BrowserTabStripItem> _tabs = new();
    private readonly List<Rectangle> _tabBounds = new();
    private readonly List<int> _tabBoundIndexes = new();
    private readonly ToolTip _toolTip = new();
    private readonly System.Windows.Forms.Timer _attentionTimer = new();
    private int _selectedCategoryIndex = -1;
    private int _selectedIndex = -1;
    private string? _currentTooltipText;
    private bool _isAttentionActive;
    private int _hoverCategoryIndex = -1;
    private bool _isHoverAddTabEntry;
    private int _dragStartIndex = -1;
    private int _dragHoverInsertionIndex = -1;
    private Point _dragMouseDownPoint = Point.Empty;
    private Point _dragCurrentMousePoint = Point.Empty;
    private bool _isReorderDragActive;
    private int _lastLoggedPaintTabCount = int.MinValue;
    private int _lastLoggedPaintCategoryRowHeight = int.MinValue;
    private int _lastLoggedPaintTabRowTop = int.MinValue;
    private int _lastLoggedPaintTabRowHeight = int.MinValue;
    private bool? _lastLoggedShowCategoryRow;
    private Rectangle _addTabBounds = Rectangle.Empty;
    private Rectangle _scrollLeftBounds = Rectangle.Empty;
    private Rectangle _scrollRightBounds = Rectangle.Empty;
    private Rectangle _tabListBounds = Rectangle.Empty;
    private Rectangle _tabViewportBounds = Rectangle.Empty;
    private int _firstVisibleTabIndex;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [Browsable(false)]
    public bool ShowCategoryRow { get; set; } = true;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [Browsable(false)]
    public Color ActiveTabBackColor { get; set; } = MidFDColors.ListSelectedBack;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [Browsable(false)]
    public Color InactiveTabBackColor { get; set; } = MidFDColors.ListNormalBack;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [Browsable(false)]
    public Color TabBorderColor { get; set; } = MidFDColors.BorderLine;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [Browsable(false)]
    public Color ActiveTabTextColor { get; set; } = Color.Yellow;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [Browsable(false)]
    public Color InactiveTabTextColor { get; set; } = MidFDColors.ListNormalFore;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [Browsable(false)]
    public Color AttentionBorderColor { get; set; } = Color.Yellow;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [Browsable(false)]
    public int PreferredTabWidth { get; set; } = 140;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [Browsable(false)]
    public int PreferredCategoryWidth { get; set; } = 110;

    public int TabCount => _tabs.Count;
    public int CategoryCount => _categories.Count;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [Browsable(false)]
    public int SelectedCategoryIndex
    {
        get => _selectedCategoryIndex;
        set
        {
            int normalized = value >= 0 && value < _categories.Count ? value : (_categories.Count == 0 ? -1 : 0);
            if (_selectedCategoryIndex == normalized)
            {
                return;
            }

            _selectedCategoryIndex = normalized;
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [Browsable(false)]
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            int normalized = value >= 0 && value < _tabs.Count ? value : (_tabs.Count == 0 ? -1 : 0);
            if (_selectedIndex == normalized)
            {
                return;
            }

            _selectedIndex = normalized;
            EnsureSelectedTabVisible();
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private int _categoryDragStartIndex = -1;
    private int _categoryDragHoverInsertionIndex = -1;
    private bool _isCategoryDragActive;

    public event EventHandler? SelectedIndexChanged;
    public event EventHandler<BrowserTabStripCategoryEventArgs>? CategoryClicked;
    public event EventHandler? AddTabClicked;
    public event EventHandler<BrowserTabStripMouseEventArgs>? TabDoubleClicked;
    public event EventHandler<BrowserTabStripMouseEventArgs>? TabRightClicked;
    public event EventHandler<BrowserTabStripMouseEventArgs>? TabMiddleClicked;
    public event EventHandler<BrowserTabStripReorderEventArgs>? TabReordered;
    public event EventHandler<BrowserTabStripReorderEventArgs>? CategoryReordered;
    public event EventHandler<Point>? TabListDropDownOpening;

    public BrowserTabStrip()
    {
        SetStyle(
            ControlStyles.UserPaint |
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);

        BackColor = MidFDColors.ListNormalBack;
        ForeColor = MidFDColors.ListNormalFore;
        Height = 56;
        TabStop = false;

        _attentionTimer.Interval = 850;
        _attentionTimer.Tick += (s, e) =>
        {
            _attentionTimer.Stop();
            if (!_isAttentionActive)
            {
                return;
            }

            _isAttentionActive = false;
            Invalidate();
        };
    }

    public void SetCategories(IReadOnlyList<BrowserTabStripCategoryItem> categories, int selectedCategoryIndex)
    {
        int normalized = categories.Count == 0
            ? -1
            : (selectedCategoryIndex >= 0 && selectedCategoryIndex < categories.Count ? selectedCategoryIndex : 0);
        if (_selectedCategoryIndex == normalized && _categories.SequenceEqual(categories))
        {
            return;
        }

        _categories.Clear();
        _categories.AddRange(categories);
        SelectedCategoryIndex = selectedCategoryIndex;
        if (_hoverCategoryIndex >= _categories.Count)
        {
            _hoverCategoryIndex = -1;
        }

        Invalidate();
    }

    public void SetTabs(IReadOnlyList<BrowserTabStripItem> tabs)
    {
        if (_tabs.SequenceEqual(tabs))
        {
            return;
        }

        _tabs.Clear();
        _tabs.AddRange(tabs);
        if (_tabs.Count == 0)
        {
            _selectedIndex = -1;
        }
        else if (_selectedIndex < 0 || _selectedIndex >= _tabs.Count)
        {
            _selectedIndex = 0;
        }

        if (_dragStartIndex >= _tabs.Count)
        {
            ResetDragReorderState();
        }

        ClampFirstVisibleTabIndex();
        EnsureSelectedTabVisible();
        LogService.Info($"[BrowserTabStrip] SetTabs Count={_tabs.Count} SelectedIndex={_selectedIndex} ShowCategoryRow={ShowCategoryRow}");
        Invalidate();
    }

    public void FlashLimitReached()
    {
        _isAttentionActive = true;
        _attentionTimer.Stop();
        _attentionTimer.Start();
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        pevent.Graphics.Clear(BackColor);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        _categoryBounds.Clear();
        _tabBounds.Clear();
        _tabBoundIndexes.Clear();
        _addTabBounds = Rectangle.Empty;
        _scrollLeftBounds = Rectangle.Empty;
        _scrollRightBounds = Rectangle.Empty;
        _tabListBounds = Rectangle.Empty;
        _tabViewportBounds = Rectangle.Empty;

        int categoryRowHeight = GetCategoryRowHeight();
        int tabRowTop = categoryRowHeight;
        int tabRowHeight = Math.Max(1, Height - categoryRowHeight);
        int baselineY = Height - 1;
        Color baselineColor = _isAttentionActive ? AttentionBorderColor : TabBorderColor;
        LogPaintStateIfChanged(categoryRowHeight, tabRowTop, tabRowHeight);

        if (ShowCategoryRow && _categories.Count > 0)
        {
            DrawCategoryRow(e.Graphics, categoryRowHeight);
        }

        Rectangle lowerRowBounds = new(0, tabRowTop, Math.Max(1, Width), tabRowHeight);

        // content region の導入 (outer border の内側 1px。inner border はタブ境界と共有)
        int contentLeft = lowerRowBounds.Left + 1;
        int contentRight = lowerRowBounds.Right - 1;

        int addTabWidth = MeasureAddTabEntryWidth(e.Graphics);
        _addTabBounds = new Rectangle(
            Math.Max(contentLeft, contentRight - addTabWidth),
            tabRowTop,
            Math.Min(addTabWidth, Math.Max(0, contentRight - contentLeft)),
            tabRowHeight);

        if (_tabs.Count == 0)
        {
            DrawLowerTabRowFrame(e.Graphics, lowerRowBounds, baselineY, baselineColor);
            DrawEmptyTabRow(e.Graphics, lowerRowBounds, baselineY);
            DrawAddTabEntry(e.Graphics, _addTabBounds, baselineY, _isHoverAddTabEntry);
            return;
        }

        int tabWidth = PreferredTabWidth;
        int navButtonWidth = GetTabNavigationButtonWidth();
        int contentWidth = Math.Max(0, contentRight - contentLeft);
        bool isOverflow = _tabs.Count * tabWidth > Math.Max(0, contentWidth - addTabWidth);
        int tabListWidth = isOverflow ? navButtonWidth : 0;
        int visibleCapacity = CalculateVisibleCapacity(contentWidth, tabWidth, addTabWidth + tabListWidth, navButtonWidth, out bool showScrollLeft, out bool showScrollRight);
        int x = contentLeft;
        if (showScrollLeft)
        {
            _scrollLeftBounds = new Rectangle(x, tabRowTop, Math.Min(navButtonWidth, Math.Max(0, contentRight - x)), tabRowHeight);
            x = _scrollLeftBounds.Right;
        }

        int addLeft = Math.Max(x, contentRight - addTabWidth);
        _addTabBounds = new Rectangle(addLeft, tabRowTop, Math.Max(0, contentRight - addLeft), tabRowHeight);
        int rightReservedLeft = _addTabBounds.Left;
        if (isOverflow)
        {
            int tabListLeft = Math.Max(x, rightReservedLeft - tabListWidth);
            _tabListBounds = new Rectangle(tabListLeft, tabRowTop, Math.Max(0, rightReservedLeft - tabListLeft), tabRowHeight);
            rightReservedLeft = _tabListBounds.Left;
        }

        if (showScrollRight)
        {
            int scrollRightLeft = Math.Max(x, rightReservedLeft - navButtonWidth);
            _scrollRightBounds = new Rectangle(scrollRightLeft, tabRowTop, Math.Max(0, rightReservedLeft - scrollRightLeft), tabRowHeight);
            rightReservedLeft = _scrollRightBounds.Left;
        }

        _tabViewportBounds = Rectangle.FromLTRB(x, tabRowTop, Math.Max(x, rightReservedLeft), tabRowTop + tabRowHeight);

        // 1. 表示中タブの背景、セパレータ、テキストを先に描画
        for (int visibleOffset = 0; visibleOffset < visibleCapacity; visibleOffset++)
        {
            int tabIndex = _firstVisibleTabIndex + visibleOffset;
            if (tabIndex < 0 || tabIndex >= _tabs.Count)
            {
                break;
            }

            var bounds = new Rectangle(_tabViewportBounds.Left + visibleOffset * tabWidth, tabRowTop, tabWidth, tabRowHeight);
            if (bounds.Left >= _tabViewportBounds.Right)
            {
                break;
            }

            if (bounds.Right > _tabViewportBounds.Right)
            {
                bounds.Width = Math.Max(0, _tabViewportBounds.Right - bounds.Left);
            }

            if (bounds.Width < MinimumPartialTabWidth)
            {
                break;
            }

            _tabBounds.Add(bounds);
            _tabBoundIndexes.Add(tabIndex);
            
            bool isSelected = (tabIndex == _selectedIndex);
            bool isDragSource = _isReorderDragActive && tabIndex == _dragStartIndex;
            DrawTabBackgroundAndSeparator(e.Graphics, bounds, isSelected, isDragSource);
            DrawTabText(e.Graphics, bounds, _tabs[tabIndex], isSelected);
        }

        if (!isOverflow && _tabBounds.Count > 0)
        {
            Rectangle lastTabBounds = _tabBounds[^1];
            if (lastTabBounds.Right + addTabWidth <= contentRight)
            {
                _addTabBounds = new Rectangle(lastTabBounds.Right, tabRowTop, addTabWidth, tabRowHeight);
            }
        }

        // 2. 外枠 (outer frame) を描画。連続した実線として強調。
        DrawLowerTabRowFrame(e.Graphics, lowerRowBounds, baselineY, baselineColor);

        // 3. 選択中のタブの境界線を描画。外枠に接続させる。
        int selectedVisibleIndex = _tabBoundIndexes.IndexOf(_selectedIndex);
        if (selectedVisibleIndex >= 0 && selectedVisibleIndex < _tabBounds.Count)
        {
            // 先頭タブが active の場合は left border を x=0 から開始して row 左端との隙間を埋める
            bool isFirstActiveTab = (_selectedIndex == 0);
            DrawTabBorders(e.Graphics, _tabBounds[selectedVisibleIndex], _tabs[_selectedIndex], baselineY, isFirstActiveTab);
        }

        // 4. overflow navigation と AddTab を配置・描画
        DrawNavigationButton(e.Graphics, _scrollLeftBounds, "<", baselineY);
        DrawNavigationButton(e.Graphics, _scrollRightBounds, ">", baselineY);
        DrawNavigationButton(e.Graphics, _tabListBounds, "∨", baselineY);
        DrawAddTabEntry(e.Graphics, _addTabBounds, baselineY, _isHoverAddTabEntry);

        DrawDragInsertionIndicator(e.Graphics, tabRowTop, baselineY);
        DrawDragGhost(e.Graphics, baselineY);

        DrawCategoryDragInsertionIndicator(e.Graphics, categoryRowHeight);
        DrawCategoryDragGhost(e.Graphics, categoryRowHeight);
    }

    private void DrawEmptyTabRow(Graphics graphics, Rectangle rowBounds, int baselineY)
    {
        if (rowBounds.Height <= 0 || rowBounds.Width <= 0)
        {
            return;
        }

        Color emptyRowBackColor = BlendColor(InactiveTabBackColor, BackColor, 0.26);
        Rectangle fillBounds = Rectangle.FromLTRB(
            rowBounds.Left + 1,
            rowBounds.Top + 1,
            Math.Max(rowBounds.Left + 1, rowBounds.Right - 1),
            Math.Max(rowBounds.Top + 1, baselineY - 1));

        using (var backgroundBrush = new SolidBrush(emptyRowBackColor))
        {
            graphics.FillRectangle(backgroundBrush, fillBounds);
        }

        using Pen innerPen = new(Color.FromArgb(72, TabBorderColor));
        int innerY = Math.Max(rowBounds.Top + 1, baselineY - 3);
        graphics.DrawLine(innerPen, rowBounds.Left + 1, innerY, rowBounds.Right - 2, innerY);
    }

    private void DrawLowerTabRowFrame(Graphics graphics, Rectangle rowBounds, int baselineY, Color baselineColor)
    {
        if (rowBounds.Height <= 0 || rowBounds.Width <= 0)
        {
            return;
        }

        int right = rowBounds.Right - 1;
        int left = rowBounds.Left;
        int sideBottom = Math.Max(left, baselineY - 2);

        // 上辺は描かない。アクティブタブが自分の上辺に責任を持つ。
        // 下辺 (baseline) のみ row frame が描く。
        using (var baselinePen = new Pen(baselineColor))
        {
            graphics.DrawLine(baselinePen, left, baselineY, right, baselineY);
        }

        using (var baselineInnerPen = new Pen(Color.FromArgb(96, baselineColor)))
        {
            graphics.DrawLine(baselineInnerPen, left, Math.Max(0, baselineY - 1), right, Math.Max(0, baselineY - 1));
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (IsAddTabEntryHit(e.Location))
        {
            ResetDragReorderState();
            return;
        }

        if (IsTabNavigationHit(e.Location))
        {
            ResetDragReorderState();
            return;
        }

        int categoryIndex = GetCategoryIndexAt(e.Location);
        if (categoryIndex >= 0)
        {
            ResetDragReorderState();
            if (e.Button == MouseButtons.Left && _categories[categoryIndex].Kind != BrowserTabStripCategoryItemKind.ManageEntry)
            {
                _categoryDragStartIndex = categoryIndex;
                _dragMouseDownPoint = e.Location;
                _dragCurrentMousePoint = e.Location;
                _categoryDragHoverInsertionIndex = -1;
                _isCategoryDragActive = false;
            }
            return;
        }

        int tabIndex = GetTabIndexAt(e.Location);
        if (tabIndex >= 0)
        {
            SelectedIndex = tabIndex;
            if (e.Button == MouseButtons.Left)
            {
                _dragStartIndex = tabIndex;
                _dragMouseDownPoint = e.Location;
                _dragCurrentMousePoint = e.Location;
                _dragHoverInsertionIndex = -1;
                _isReorderDragActive = false;
            }
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);

        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        int tabIndex = GetTabIndexAt(e.Location);
        if (tabIndex < 0)
        {
            return;
        }

        TabDoubleClicked?.Invoke(this, new BrowserTabStripMouseEventArgs(tabIndex, e.Button, e.Location));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (e.Button == MouseButtons.Left && _isCategoryDragActive)
        {
            int dragStartIndex = _categoryDragStartIndex;
            int targetIndex = ResolveDropTargetIndex(dragStartIndex, _categoryDragHoverInsertionIndex);
            ResetDragReorderState();
            if (dragStartIndex >= 0 && targetIndex >= 0 && dragStartIndex != targetIndex)
            {
                CategoryReordered?.Invoke(this, new BrowserTabStripReorderEventArgs(dragStartIndex, targetIndex));
                return;
            }
        }

        if (e.Button == MouseButtons.Left && _isReorderDragActive)
        {
            int dragStartIndex = _dragStartIndex;
            int targetIndex = ResolveDropTargetIndex(dragStartIndex, _dragHoverInsertionIndex);
            ResetDragReorderState();
            if (dragStartIndex >= 0 && targetIndex >= 0 && dragStartIndex != targetIndex)
            {
                TabReordered?.Invoke(this, new BrowserTabStripReorderEventArgs(dragStartIndex, targetIndex));
                return;
            }
        }

        if (e.Button == MouseButtons.Left || e.Button == MouseButtons.Right)
        {
            bool wasCategoryDrag = _categoryDragStartIndex >= 0;
            ResetDragReorderState();
            if (e.Button == MouseButtons.Left && TryHandleTabNavigationClick(e.Location))
            {
                return;
            }

            if (e.Button == MouseButtons.Left && IsAddTabEntryHit(e.Location))
            {
                AddTabClicked?.Invoke(this, EventArgs.Empty);
                return;
            }

            int categoryIndex = GetCategoryIndexAt(e.Location);
            if (categoryIndex >= 0 && categoryIndex < _categories.Count)
            {
                // 左クリックは通常のカテゴリ切り替え / 追加ボタン、右クリックはコンテキストメニューとして扱う。
                // ドラッグ完了直後のマウスアップでは click を重複発火させない。
                if (e.Button == MouseButtons.Right || (e.Button == MouseButtons.Left && !_isCategoryDragActive))
                {
                    BrowserTabStripCategoryItem category = _categories[categoryIndex];
                    CategoryClicked?.Invoke(this, new BrowserTabStripCategoryEventArgs(categoryIndex, category.CategoryId, category.Kind, e.Button, e.Location));
                    return;
                }
            }
        }

        int tabIndex = GetTabIndexAt(e.Location);
        if (tabIndex < 0)
        {
            return;
        }

        var eventArgs = new BrowserTabStripMouseEventArgs(tabIndex, e.Button, e.Location);
        if (e.Button == MouseButtons.Right)
        {
            TabRightClicked?.Invoke(this, eventArgs);
        }
        else if (e.Button == MouseButtons.Middle)
        {
            TabMiddleClicked?.Invoke(this, eventArgs);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (TryHandleDragReorderMove(e))
        {
            return;
        }

        int hoverCategoryIndex = GetCategoryIndexAt(e.Location);
        if (_hoverCategoryIndex != hoverCategoryIndex)
        {
            _hoverCategoryIndex = hoverCategoryIndex;
            Invalidate();
        }

        bool isHoverAddTabEntry = IsAddTabEntryHit(e.Location);
        if (_isHoverAddTabEntry != isHoverAddTabEntry)
        {
            _isHoverAddTabEntry = isHoverAddTabEntry;
            Invalidate();
        }

        string? tooltipText = null;
        if (hoverCategoryIndex >= 0 && hoverCategoryIndex < _categories.Count)
        {
            tooltipText = _categories[hoverCategoryIndex].ToolTipText;
        }
        else if (isHoverAddTabEntry)
        {
            tooltipText = "現在のカテゴリに新しいタブを追加します。";
        }
        else if (_scrollLeftBounds.Contains(e.Location))
        {
            tooltipText = "左のタブを表示します。";
        }
        else if (_scrollRightBounds.Contains(e.Location))
        {
            tooltipText = "右のタブを表示します。";
        }
        else if (_tabListBounds.Contains(e.Location))
        {
            tooltipText = "タブ一覧を表示します。";
        }
        else
        {
            int tabIndex = GetTabIndexAt(e.Location);
            if (tabIndex >= 0 && tabIndex < _tabs.Count)
            {
                tooltipText = _tabs[tabIndex].ToolTipText;
            }
        }

        if (string.Equals(_currentTooltipText, tooltipText, StringComparison.Ordinal))
        {
            return;
        }

        _currentTooltipText = tooltipText;
        _toolTip.SetToolTip(this, tooltipText ?? string.Empty);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);

        bool shouldInvalidate = _hoverCategoryIndex >= 0;
        _hoverCategoryIndex = -1;
        shouldInvalidate |= _isHoverAddTabEntry;
        _isHoverAddTabEntry = false;
        if (!string.IsNullOrEmpty(_currentTooltipText))
        {
            _currentTooltipText = null;
            _toolTip.SetToolTip(this, string.Empty);
        }

        if (shouldInvalidate)
        {
            Invalidate();
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (e.Location.Y < GetCategoryRowHeight() || e.Location.Y >= Height)
        {
            base.OnMouseWheel(e);
            return;
        }

        MarkWheelHandledIfPossible(e);
        if (_tabs.Count <= 1 || _selectedIndex < 0)
        {
            return;
        }

        int nextIndex = e.Delta > 0
            ? _selectedIndex - 1
            : e.Delta < 0
                ? _selectedIndex + 1
                : _selectedIndex;
        if (nextIndex < 0 || nextIndex >= _tabs.Count || nextIndex == _selectedIndex)
        {
            return;
        }

        SelectedIndex = nextIndex;
    }

    private static void MarkWheelHandledIfPossible(MouseEventArgs e)
    {
        if (e is HandledMouseEventArgs handled)
        {
            handled.Handled = true;
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ClampFirstVisibleTabIndex();
        EnsureSelectedTabVisible();
    }

    private void DrawCategoryRow(Graphics graphics, int categoryRowHeight)
    {
        if (_categories.Count == 0 || categoryRowHeight <= 0)
        {
            return;
        }

        using Pen rowSeparatorPen = new(Color.FromArgb(84, TabBorderColor));
        graphics.DrawLine(rowSeparatorPen, 0, categoryRowHeight - 1, Width - 1, categoryRowHeight - 1);

        int x = 0;
        for (int i = 0; i < _categories.Count; i++)
        {
            BrowserTabStripCategoryItem category = _categories[i];
            int width = MeasureCategoryWidth(graphics, category.Text);
            Rectangle bounds = new(x, 0, width, categoryRowHeight);
            _categoryBounds.Add(bounds);
            DrawCategory(graphics, bounds, category, i == _selectedCategoryIndex, i == _hoverCategoryIndex);
            x += width;
        }
    }

    private void DrawCategory(Graphics graphics, Rectangle bounds, BrowserTabStripCategoryItem category, bool isSelected, bool isHovered)
    {
        // カテゴリはボタンとして描画する。下段との結線は行わない。
        Rectangle buttonBounds = Rectangle.Inflate(bounds, -2, -3);
        if (buttonBounds.Width <= 0 || buttonBounds.Height <= 0) return;

        bool isManageEntry = category.Kind == BrowserTabStripCategoryItemKind.ManageEntry;
        Color backgroundColor = Color.Transparent;
        Color textColor = InactiveTabTextColor;
        Color borderColor = Color.Transparent;

        if (isSelected)
        {
            backgroundColor = BlendColor(ActiveTabBackColor, BackColor, 0.35);
            textColor = ActiveTabTextColor;
            borderColor = BlendColor(TabBorderColor, ActiveTabTextColor, 0.2);
        }
        else if (isHovered)
        {
            backgroundColor = Color.FromArgb(48, ActiveTabBackColor);
            borderColor = Color.FromArgb(64, TabBorderColor);
        }
        else if (isManageEntry)
        {
            textColor = BlendColor(InactiveTabTextColor, BackColor, 0.4);
        }

        if (backgroundColor != Color.Transparent)
        {
            using var brush = new SolidBrush(backgroundColor);
            graphics.FillRectangle(brush, buttonBounds);
        }

        if (borderColor != Color.Transparent)
        {
            using var pen = new Pen(borderColor);
            graphics.DrawRectangle(pen, buttonBounds.X, buttonBounds.Y, buttonBounds.Width - 1, buttonBounds.Height - 1);
        }

        Rectangle textBounds = Rectangle.Inflate(buttonBounds, -8, 0);
        TextRenderer.DrawText(
            graphics,
            category.Text,
            Font,
            textBounds,
            textColor,
            Color.Transparent,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    private void DrawTabBackgroundAndSeparator(Graphics graphics, Rectangle bounds, bool isSelected, bool isDragSource)
    {
        int baselineY = Height - 1;
        Rectangle fillBounds = new(bounds.X, bounds.Y, bounds.Width, Math.Max(1, baselineY - bounds.Y));

        // アクティブタブのみ背景を塗る。非アクティブは透明にする。
        Color backgroundColor = isSelected ? ActiveTabBackColor : Color.Transparent;

        if (isDragSource)
        {
            backgroundColor = BlendColor(isSelected ? ActiveTabBackColor : BackColor, BackColor, 0.45);
        }

        if (backgroundColor != Color.Transparent)
        {
            using var backBrush = new SolidBrush(backgroundColor);
            graphics.FillRectangle(backBrush, fillBounds);
        }

        // 非アクティブタブ間の区切りは極めて淡くする（ほぼ消えるレベル）
        if (!isSelected)
        {
            Color separatorColor = Color.FromArgb(20, _isAttentionActive ? AttentionBorderColor : TabBorderColor);
            using Pen separatorPen = new(separatorColor);
            graphics.DrawLine(separatorPen, fillBounds.Right - 1, fillBounds.Top + 8, fillBounds.Right - 1, baselineY - 8);
        }
    }

    private void DrawTabBorders(Graphics graphics, Rectangle bounds, BrowserTabStripItem tab, int baselineY, bool isFirstTab = false)
    {
        // アクティブタブだけが上辺・左右辺の枠を持つ。
        // row frame は上辺を持たないため、ここで上辺を描くことで自分だけが「箱」に見える。
        Rectangle fillBounds = new(bounds.X, bounds.Y, bounds.Width, Math.Max(1, baselineY - bounds.Y));
        Color borderColor = _isAttentionActive ? AttentionBorderColor : TabBorderColor;
        Color backgroundColor = ActiveTabBackColor;

        // 先頭タブが active の場合、contentLeft(1) ではなく x=0 から left border を描く
        // → row 左端との 1px 隙間を解消するため
        int leftX = isFirstTab ? 0 : fillBounds.Left;

        using Pen borderPen = new(borderColor);
        // 上端
        graphics.DrawLine(borderPen, leftX, fillBounds.Top, fillBounds.Right - 1, fillBounds.Top);
        // 左端
        graphics.DrawLine(borderPen, leftX, fillBounds.Top, leftX, baselineY - 1);
        // 右端
        graphics.DrawLine(borderPen, fillBounds.Right - 1, fillBounds.Top, fillBounds.Right - 1, baselineY - 1);

        // 外枠との接続（下端の baseline 線を背景色で消し込む→アクティブタブがコンテンツと繋がって見える）
        using Pen coverPen = new(backgroundColor);
        graphics.DrawLine(coverPen, leftX + 1, baselineY, fillBounds.Right - 2, baselineY);
    }

    private void DrawTabText(Graphics graphics, Rectangle bounds, BrowserTabStripItem tab, bool isSelected)
    {
        int baselineY = Height - 1;
        Rectangle fillBounds = new(bounds.X, bounds.Y, bounds.Width, Math.Max(1, baselineY - bounds.Y));
        Color textColor = isSelected ? ActiveTabTextColor : InactiveTabTextColor;
        Rectangle textBounds = Rectangle.Inflate(fillBounds, -10, -2);
        string displayText = FitTextWithMiddleEllipsis(graphics, tab.Text, textBounds.Width);
        TextRenderer.DrawText(
            graphics,
            displayText,
            Font,
            textBounds,
            textColor,
            Color.Transparent,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    private void DrawAddTabEntry(Graphics graphics, Rectangle bounds, int baselineY, bool isHovered)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            _addTabBounds = Rectangle.Empty;
            return;
        }

        _addTabBounds = bounds;
        Rectangle fillBounds = new(bounds.X, bounds.Y, bounds.Width, Math.Max(1, baselineY - bounds.Y));

        // 非アクティブタブに近い、軽いボタンとして描画。枠線は持たない。
        Color backgroundColor = isHovered ? Color.FromArgb(40, ActiveTabBackColor) : Color.Transparent;
        Color textColor = isHovered
            ? BlendColor(InactiveTabTextColor, ActiveTabTextColor, 0.3)
            : BlendColor(InactiveTabTextColor, BackColor, 0.1);

        if (backgroundColor != Color.Transparent)
        {
            using var backBrush = new SolidBrush(backgroundColor);
            graphics.FillRectangle(backBrush, fillBounds);
        }

        // 左辺に極薄のセパレータだけ置く
        Color separatorColor = Color.FromArgb(36, TabBorderColor);
        using (var separatorPen = new Pen(separatorColor))
        {
            graphics.DrawLine(separatorPen, fillBounds.Left, fillBounds.Top + 8, fillBounds.Left, baselineY - 8);
        }

        Rectangle textBounds = Rectangle.Inflate(fillBounds, -10, -2);
        TextRenderer.DrawText(
            graphics,
            "+",
            Font,
            textBounds,
            textColor,
            Color.Transparent,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    private void DrawNavigationButton(Graphics graphics, Rectangle bounds, string text, int baselineY)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        Rectangle fillBounds = new(bounds.X, bounds.Y, bounds.Width, Math.Max(1, baselineY - bounds.Y));
        using (var backBrush = new SolidBrush(BackColor))
        {
            graphics.FillRectangle(backBrush, fillBounds);
        }

        using (var separatorPen = new Pen(Color.FromArgb(36, TabBorderColor)))
        {
            graphics.DrawLine(separatorPen, fillBounds.Left, fillBounds.Top + 8, fillBounds.Left, baselineY - 8);
        }

        TextRenderer.DrawText(
            graphics,
            text,
            Font,
            fillBounds,
            BlendColor(InactiveTabTextColor, ActiveTabTextColor, 0.18),
            Color.Transparent,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    private bool TryHandleDragReorderMove(MouseEventArgs e)
    {
        if ((e.Button & MouseButtons.Left) == 0)
        {
            return false;
        }

        // カテゴリのドラッグ処理
        if (_categoryDragStartIndex >= 0 && _categoryDragStartIndex < _categories.Count)
        {
            _dragCurrentMousePoint = e.Location;
            if (!_isCategoryDragActive)
            {
                Size dragSize = SystemInformation.DragSize;
                Rectangle dragRect = new(
                    _dragMouseDownPoint.X - dragSize.Width / 2,
                    _dragMouseDownPoint.Y - dragSize.Height / 2,
                    dragSize.Width,
                    dragSize.Height);
                if (dragRect.Contains(e.Location))
                {
                    return false;
                }
                _isCategoryDragActive = true;
            }

            int insertionIndex = GetCategoryInsertionIndexAt(e.Location);
            if (_categoryDragHoverInsertionIndex != insertionIndex)
            {
                _categoryDragHoverInsertionIndex = insertionIndex;
                Invalidate();
                return true;
            }

            Invalidate();
            return true;
        }

        // タブのドラッグ処理
        if (_dragStartIndex >= 0 && _dragStartIndex < _tabs.Count)
        {
            if (ShowCategoryRow && _categories.Count > 0 && e.Location.Y < GetCategoryRowHeight())
            {
                _dragCurrentMousePoint = e.Location;
                if (_isReorderDragActive)
                {
                    _dragHoverInsertionIndex = GetInsertionIndexAt(e.Location);
                    Invalidate();
                    return true;
                }

                return false;
            }

            _dragCurrentMousePoint = e.Location;

            if (!_isReorderDragActive)
            {
                Size dragSize = SystemInformation.DragSize;
                Rectangle dragRect = new(
                    _dragMouseDownPoint.X - dragSize.Width / 2,
                    _dragMouseDownPoint.Y - dragSize.Height / 2,
                    dragSize.Width,
                    dragSize.Height);
                if (dragRect.Contains(e.Location))
                {
                    return false;
                }

                _isReorderDragActive = true;
            }

            int insertionIndex = GetInsertionIndexAt(e.Location);
            if (_dragHoverInsertionIndex == insertionIndex)
            {
                Invalidate();
                return true;
            }

            _dragHoverInsertionIndex = insertionIndex;
            Invalidate();
            return true;
        }

        return false;
    }

    private void DrawDragInsertionIndicator(Graphics graphics, int tabRowTop, int baselineY)
    {
        if (!_isReorderDragActive || _dragHoverInsertionIndex < 0 || _tabBounds.Count == 0 || _tabBoundIndexes.Count == 0)
        {
            return;
        }

        int firstVisibleTabIndex = _tabBoundIndexes[0];
        int lastVisibleTabIndex = _tabBoundIndexes[^1];
        int indicatorX = _dragHoverInsertionIndex <= firstVisibleTabIndex
            ? _tabBounds[0].Left
            : _dragHoverInsertionIndex >= lastVisibleTabIndex + 1
                ? _tabBounds[^1].Right - 1
                : _tabBounds[Math.Max(0, _tabBoundIndexes.FindIndex(index => index >= _dragHoverInsertionIndex))].Left;

        using Pen indicatorPen = new(AttentionBorderColor, 2);
        graphics.DrawLine(indicatorPen, indicatorX, tabRowTop + 3, indicatorX, Math.Max(tabRowTop + 3, baselineY - 3));
    }

    private void DrawDragGhost(Graphics graphics, int baselineY)
    {
        if (!_isReorderDragActive || _dragStartIndex < 0 || _dragStartIndex >= _tabs.Count || !_tabBoundIndexes.Contains(_dragStartIndex))
        {
            return;
        }

        Rectangle ghostBounds = GetDragGhostBounds();
        if (ghostBounds.Width <= 0 || ghostBounds.Height <= 0)
        {
            return;
        }

        Rectangle shadowBounds = ghostBounds;
        shadowBounds.Offset(3, 3);
        using (SolidBrush shadowBrush = new(Color.FromArgb(48, Color.Black)))
        {
            graphics.FillRectangle(shadowBrush, shadowBounds);
        }

        bool isSelected = (_dragStartIndex == _selectedIndex);
        DrawTabBackgroundAndSeparator(graphics, ghostBounds, isSelected, false);
        DrawTabText(graphics, ghostBounds, _tabs[_dragStartIndex], isSelected);
        if (isSelected)
        {
            DrawTabBorders(graphics, ghostBounds, _tabs[_dragStartIndex], baselineY);
        }
    }

    private Rectangle GetDragGhostBounds()
    {
        int visibleIndex = _tabBoundIndexes.IndexOf(_dragStartIndex);
        if (visibleIndex < 0 || visibleIndex >= _tabBounds.Count)
        {
            return Rectangle.Empty;
        }

        Rectangle sourceBounds = _tabBounds[visibleIndex];
        int offsetX = _dragCurrentMousePoint.X - _dragMouseDownPoint.X;
        int offsetY = _dragCurrentMousePoint.Y - _dragMouseDownPoint.Y - 4;
        int minY = GetCategoryRowHeight();
        int maxY = Math.Max(minY, Height - sourceBounds.Height);
        int ghostY = Math.Clamp(sourceBounds.Y + offsetY, minY, maxY);
        return new Rectangle(sourceBounds.X + offsetX, ghostY, sourceBounds.Width, sourceBounds.Height);
    }

    private int MeasureCategoryWidth(Graphics graphics, string text)
    {
        int textWidth = MeasureTabTextWidth(graphics, text);
        return Math.Max(72, Math.Min(Math.Max(PreferredCategoryWidth, textWidth + 24), 220));
    }

    private int MeasureAddTabEntryWidth(Graphics graphics)
    {
        int textWidth = MeasureTabTextWidth(graphics, "+");
        return Math.Max(30, Math.Min(Math.Max(30, textWidth + 18), 44));
    }

    private static int GetTabNavigationButtonWidth()
    {
        return 30;
    }

    private int CalculateVisibleCapacity(int contentWidth, int tabWidth, int addTabWidth, int navButtonWidth, out bool showScrollLeft, out bool showScrollRight)
    {
        if (_tabs.Count == 0 || tabWidth <= 0)
        {
            showScrollLeft = false;
            showScrollRight = false;
            _firstVisibleTabIndex = 0;
            return 0;
        }

        _firstVisibleTabIndex = Math.Clamp(_firstVisibleTabIndex, 0, _tabs.Count - 1);
        for (int i = 0; i < 4; i++)
        {
            showScrollLeft = _firstVisibleTabIndex > 0;
            int widthWithoutRight = Math.Max(0, contentWidth - addTabWidth - (showScrollLeft ? navButtonWidth : 0));
            int capacityWithoutRight = CountDrawableTabs(widthWithoutRight, tabWidth);
            showScrollRight = _firstVisibleTabIndex + capacityWithoutRight < _tabs.Count;
            int width = Math.Max(0, widthWithoutRight - (showScrollRight ? navButtonWidth : 0));
            int capacity = CountDrawableTabs(width, tabWidth);
            int maxFirstVisibleTabIndex = Math.Max(0, _tabs.Count - capacity);
            if (_firstVisibleTabIndex <= maxFirstVisibleTabIndex)
            {
                return capacity;
            }

            _firstVisibleTabIndex = maxFirstVisibleTabIndex;
        }

        showScrollLeft = _firstVisibleTabIndex > 0;
        int finalWidthWithoutRight = Math.Max(0, contentWidth - addTabWidth - (showScrollLeft ? navButtonWidth : 0));
        int finalCapacityWithoutRight = CountDrawableTabs(finalWidthWithoutRight, tabWidth);
        showScrollRight = _firstVisibleTabIndex + finalCapacityWithoutRight < _tabs.Count;
        int finalWidth = Math.Max(0, finalWidthWithoutRight - (showScrollRight ? navButtonWidth : 0));
        return CountDrawableTabs(finalWidth, tabWidth);
    }

    private static int CountDrawableTabs(int availableWidth, int tabWidth)
    {
        if (availableWidth < MinimumPartialTabWidth || tabWidth <= 0)
        {
            return 0;
        }

        int fullTabCount = availableWidth / tabWidth;
        int remainingWidth = availableWidth - fullTabCount * tabWidth;
        return fullTabCount + (remainingWidth >= MinimumPartialTabWidth ? 1 : 0);
    }

    private int EstimateVisibleCapacity()
    {
        int tabWidth = PreferredTabWidth;
        int contentWidth = Math.Max(0, Width - 2);
        int addTabWidth = MeasureFallbackAddTabEntryWidth();
        int navButtonWidth = GetTabNavigationButtonWidth();
        bool isOverflow = _tabs.Count * tabWidth > Math.Max(0, contentWidth - addTabWidth);
        int fixedRightWidth = addTabWidth + (isOverflow ? navButtonWidth : 0);
        return CalculateVisibleCapacity(contentWidth, tabWidth, fixedRightWidth, navButtonWidth, out _, out _);
    }

    private static int MeasureFallbackAddTabEntryWidth()
    {
        return 30;
    }

    private void ClampFirstVisibleTabIndex()
    {
        if (_tabs.Count == 0)
        {
            _firstVisibleTabIndex = 0;
            return;
        }

        int visibleCapacity = EstimateVisibleCapacity();
        int maxFirstVisibleTabIndex = Math.Max(0, _tabs.Count - Math.Max(1, visibleCapacity));
        _firstVisibleTabIndex = Math.Clamp(_firstVisibleTabIndex, 0, maxFirstVisibleTabIndex);
    }

    private void EnsureSelectedTabVisible()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _tabs.Count)
        {
            ClampFirstVisibleTabIndex();
            return;
        }

        int visibleCapacity = Math.Max(1, EstimateVisibleCapacity());
        if (_selectedIndex < _firstVisibleTabIndex)
        {
            _firstVisibleTabIndex = _selectedIndex;
        }
        else if (_selectedIndex >= _firstVisibleTabIndex + visibleCapacity)
        {
            _firstVisibleTabIndex = _selectedIndex - visibleCapacity + 1;
        }

        ClampFirstVisibleTabIndex();
    }

    private int GetCategoryRowHeight()
    {
        return !ShowCategoryRow || _categories.Count == 0 ? 0 : Math.Max(24, Math.Min(28, Height / 2));
    }

    private void LogPaintStateIfChanged(int categoryRowHeight, int tabRowTop, int tabRowHeight)
    {
        if (_lastLoggedPaintTabCount == _tabs.Count &&
            _lastLoggedPaintCategoryRowHeight == categoryRowHeight &&
            _lastLoggedPaintTabRowTop == tabRowTop &&
            _lastLoggedPaintTabRowHeight == tabRowHeight &&
            _lastLoggedShowCategoryRow == ShowCategoryRow)
        {
            return;
        }

        _lastLoggedPaintTabCount = _tabs.Count;
        _lastLoggedPaintCategoryRowHeight = categoryRowHeight;
        _lastLoggedPaintTabRowTop = tabRowTop;
        _lastLoggedPaintTabRowHeight = tabRowHeight;
        _lastLoggedShowCategoryRow = ShowCategoryRow;
        LogService.Info(
            $"[BrowserTabStrip] Paint Tabs={_tabs.Count} Categories={_categories.Count} ShowCategoryRow={ShowCategoryRow} " +
            $"CategoryRowHeight={categoryRowHeight} TabRowTop={tabRowTop} TabRowHeight={tabRowHeight} Height={Height}");
    }

    private int GetInsertionIndexAt(Point location)
    {
        if (_tabBounds.Count == 0)
        {
            return -1;
        }

        for (int i = 0; i < _tabBounds.Count; i++)
        {
            Rectangle bounds = _tabBounds[i];
            int tabIndex = i < _tabBoundIndexes.Count ? _tabBoundIndexes[i] : i;
            if (location.X < bounds.Left + bounds.Width / 2)
            {
                return tabIndex;
            }

            if (location.X < bounds.Right)
            {
                return tabIndex + 1;
            }
        }

        return _tabBoundIndexes.Count == 0 ? _tabBounds.Count : _tabBoundIndexes[^1] + 1;
    }

    private static int ResolveDropTargetIndex(int dragStartIndex, int insertionIndex)
    {
        if (dragStartIndex < 0 || insertionIndex < 0)
        {
            return -1;
        }

        return insertionIndex > dragStartIndex
            ? insertionIndex - 1
            : insertionIndex;
    }

    private int GetCategoryIndexAt(Point location)
    {
        for (int i = 0; i < _categoryBounds.Count; i++)
        {
            if (_categoryBounds[i].Contains(location))
            {
                return i;
            }
        }

        return -1;
    }

    private int GetTabIndexAt(Point location)
    {
        for (int i = 0; i < _tabBounds.Count; i++)
        {
            if (_tabBounds[i].Contains(location))
            {
                return i < _tabBoundIndexes.Count ? _tabBoundIndexes[i] : i;
            }
        }

        return -1;
    }

    private bool IsAddTabEntryHit(Point location)
    {
        return !_addTabBounds.IsEmpty && _addTabBounds.Contains(location);
    }

    private bool IsTabNavigationHit(Point location)
    {
        return (!_scrollLeftBounds.IsEmpty && _scrollLeftBounds.Contains(location)) ||
            (!_scrollRightBounds.IsEmpty && _scrollRightBounds.Contains(location)) ||
            (!_tabListBounds.IsEmpty && _tabListBounds.Contains(location));
    }

    private bool TryHandleTabNavigationClick(Point location)
    {
        if (!_scrollLeftBounds.IsEmpty && _scrollLeftBounds.Contains(location))
        {
            MoveFirstVisibleTab(-1);
            return true;
        }

        if (!_scrollRightBounds.IsEmpty && _scrollRightBounds.Contains(location))
        {
            MoveFirstVisibleTab(1);
            return true;
        }

        if (!_tabListBounds.IsEmpty && _tabListBounds.Contains(location))
        {
            ShowTabListMenu();
            return true;
        }

        return false;
    }

    private void ShowTabListMenu()
    {
        if (TabListDropDownOpening != null)
        {
            TabListDropDownOpening.Invoke(this, new Point(_tabListBounds.Left, _tabListBounds.Bottom));
        }
        else
        {
            if (_tabs.Count == 0)
            {
                return;
            }

            ContextMenuStrip menu = new();
            for (int i = 0; i < _tabs.Count; i++)
            {
                int tabIndex = i;
                ToolStripMenuItem item = new($"{i + 1}: {_tabs[i].Text}")
                {
                    Checked = i == _selectedIndex,
                    CheckOnClick = false,
                    ToolTipText = _tabs[i].ToolTipText ?? string.Empty
                };
                item.Click += (_, _) => SelectedIndex = tabIndex;
                menu.Items.Add(item);
            }

            menu.Show(this, _tabListBounds.Left, _tabListBounds.Bottom);
        }
    }

    private void MoveFirstVisibleTab(int delta)
    {
        if (_tabs.Count == 0 || delta == 0)
        {
            return;
        }

        int previous = _firstVisibleTabIndex;
        _firstVisibleTabIndex += delta;
        ClampFirstVisibleTabIndex();
        if (_firstVisibleTabIndex != previous)
        {
            Invalidate();
        }
    }

    private void ResetDragReorderState()
    {
        if (!_isReorderDragActive && _dragStartIndex < 0 && _dragHoverInsertionIndex < 0 &&
            !_isCategoryDragActive && _categoryDragStartIndex < 0 && _categoryDragHoverInsertionIndex < 0)
        {
            return;
        }

        _dragStartIndex = -1;
        _dragHoverInsertionIndex = -1;
        _dragMouseDownPoint = Point.Empty;
        _dragCurrentMousePoint = Point.Empty;
        _isReorderDragActive = false;

        _categoryDragStartIndex = -1;
        _categoryDragHoverInsertionIndex = -1;
        _isCategoryDragActive = false;

        Invalidate();
    }

    private int GetCategoryInsertionIndexAt(Point location)
    {
        if (_categoryBounds.Count == 0)
        {
            return 0;
        }

        int validCategoryCount = _categories.Count;
        if (validCategoryCount > 0 && _categories[^1].Kind == BrowserTabStripCategoryItemKind.ManageEntry)
        {
            validCategoryCount--;
        }

        if (validCategoryCount == 0)
        {
            return 0;
        }

        for (int i = 0; i < validCategoryCount; i++)
        {
            Rectangle bounds = _categoryBounds[i];
            int midX = bounds.Left + bounds.Width / 2;
            if (location.X < midX)
            {
                return i;
            }
        }

        return validCategoryCount;
    }

    private void DrawCategoryDragInsertionIndicator(Graphics graphics, int categoryRowHeight)
    {
        if (!_isCategoryDragActive || _categoryDragHoverInsertionIndex < 0 || _categoryBounds.Count == 0)
        {
            return;
        }

        int indicatorX;
        if (_categoryDragHoverInsertionIndex < _categoryBounds.Count)
        {
            indicatorX = _categoryBounds[_categoryDragHoverInsertionIndex].Left;
        }
        else
        {
            int validCategoryCount = _categories.Count;
            if (validCategoryCount > 0 && _categories[^1].Kind == BrowserTabStripCategoryItemKind.ManageEntry)
            {
                validCategoryCount--;
            }
            indicatorX = validCategoryCount > 0 ? _categoryBounds[validCategoryCount - 1].Right : 0;
        }

        using Pen indicatorPen = new(AttentionBorderColor, 2);
        graphics.DrawLine(indicatorPen, indicatorX, 2, indicatorX, categoryRowHeight - 4);
    }

    private void DrawCategoryDragGhost(Graphics graphics, int categoryRowHeight)
    {
        if (!_isCategoryDragActive || _categoryDragStartIndex < 0 || _categoryDragStartIndex >= _categories.Count || _categoryDragStartIndex >= _categoryBounds.Count)
        {
            return;
        }

        Rectangle sourceBounds = _categoryBounds[_categoryDragStartIndex];
        int offsetX = _dragCurrentMousePoint.X - _dragMouseDownPoint.X;
        Rectangle ghostBounds = new(sourceBounds.X + offsetX, sourceBounds.Y, sourceBounds.Width, sourceBounds.Height);

        Rectangle shadowBounds = ghostBounds;
        shadowBounds.Offset(2, 2);
        using (SolidBrush shadowBrush = new(Color.FromArgb(48, Color.Black)))
        {
            graphics.FillRectangle(shadowBrush, shadowBounds);
        }

        DrawCategory(graphics, ghostBounds, _categories[_categoryDragStartIndex], _categoryDragStartIndex == _selectedCategoryIndex, false);
    }

    private static Color BlendColor(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0d, 1d);
        int r = (int)Math.Round(from.R + ((to.R - from.R) * amount));
        int g = (int)Math.Round(from.G + ((to.G - from.G) * amount));
        int b = (int)Math.Round(from.B + ((to.B - from.B) * amount));
        return Color.FromArgb(r, g, b);
    }

    private string FitTextWithMiddleEllipsis(Graphics graphics, string text, int availableWidth)
    {
        if (availableWidth <= 0 || string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (MeasureTabTextWidth(graphics, text) <= availableWidth)
        {
            return text;
        }

        const string ellipsis = "…";
        if (MeasureTabTextWidth(graphics, ellipsis) > availableWidth)
        {
            return string.Empty;
        }

        int low = 0;
        int high = Math.Max(0, text.Length - 1);
        while (low < high)
        {
            int mid = (low + high + 1) / 2;
            string candidate = BuildMiddleEllipsisCandidate(text, mid, ellipsis);
            if (MeasureTabTextWidth(graphics, candidate) <= availableWidth)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return low <= 0
            ? ellipsis
            : BuildMiddleEllipsisCandidate(text, low, ellipsis);
    }

    private int MeasureTabTextWidth(Graphics graphics, string text)
    {
        return TextRenderer.MeasureText(
            graphics,
            text,
            Font,
            new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width;
    }

    private static string BuildMiddleEllipsisCandidate(string text, int visibleCount, string ellipsis)
    {
        if (string.IsNullOrEmpty(text) || visibleCount <= 0)
        {
            return ellipsis;
        }

        if (visibleCount >= text.Length)
        {
            return text;
        }

        int headCount = (visibleCount + 1) / 2;
        int tailCount = visibleCount / 2;
        if (headCount + tailCount >= text.Length)
        {
            return text;
        }

        return text[..headCount] + ellipsis + text[(text.Length - tailCount)..];
    }
}

public enum BrowserTabStripCategoryItemKind
{
    Category,
    ManageEntry
}

public sealed record BrowserTabStripCategoryItem(
    string CategoryId,
    string Text,
    string? ToolTipText,
    BrowserTabStripCategoryItemKind Kind = BrowserTabStripCategoryItemKind.Category);

public sealed record BrowserTabStripItem(string Text, string? ToolTipText);

public sealed class BrowserTabStripCategoryEventArgs : EventArgs
{
    public BrowserTabStripCategoryEventArgs(int categoryIndex, string categoryId, BrowserTabStripCategoryItemKind kind, MouseButtons button, Point location)
    {
        CategoryIndex = categoryIndex;
        CategoryId = categoryId;
        Kind = kind;
        Button = button;
        Location = location;
    }

    public int CategoryIndex { get; }
    public string CategoryId { get; }
    public BrowserTabStripCategoryItemKind Kind { get; }
    public MouseButtons Button { get; }
    public Point Location { get; }
}

public sealed class BrowserTabStripMouseEventArgs : EventArgs
{
    public BrowserTabStripMouseEventArgs(int tabIndex, MouseButtons button, Point location)
    {
        TabIndex = tabIndex;
        Button = button;
        Location = location;
    }

    public int TabIndex { get; }
    public MouseButtons Button { get; }
    public Point Location { get; }
}

public sealed class BrowserTabStripReorderEventArgs : EventArgs
{
    public BrowserTabStripReorderEventArgs(int fromIndex, int toIndex)
    {
        FromIndex = fromIndex;
        ToIndex = toIndex;
    }

    public int FromIndex { get; }
    public int ToIndex { get; }
}
