using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using MidFD.Models;
using MidFD.Services;

namespace MidFD.Controls;

public sealed class LargeFilePreviewControl : UserControl
{
    private const int MaxRenderableLineLength = 2048; // TextRenderer/GDI safety limit (pixel width guard)

    private readonly struct LargeTextLayout
    {
        public LargeTextLayout(
            float lineHeight,
            float gutterWidth,
            float textStartX,
            float textEndX,
            float charWidth)
        {
            LineHeight = lineHeight;
            GutterWidth = gutterWidth;
            TextStartX = textStartX;
            TextEndX = textEndX;
            CharWidth = charWidth;
        }

        public float LineHeight { get; }
        public float GutterWidth { get; }
        public float TextStartX { get; }
        public float TextEndX { get; }
        public float CharWidth { get; }
    }

    private readonly struct CharacterSelectionPoint
    {
        public CharacterSelectionPoint(int absoluteLine, int column)
        {
            AbsoluteLine = absoluteLine;
            Column = column;
        }
        public int AbsoluteLine { get; }
        public int Column { get; }

        public bool Equals(CharacterSelectionPoint other)
        {
            return AbsoluteLine == other.AbsoluteLine && Column == other.Column;
        }

        public override bool Equals(object? obj) => obj is CharacterSelectionPoint other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(AbsoluteLine, Column);
    }
    public readonly record struct CharacterSelectionRange(int StartLine, int StartColumn, int EndLine, int EndColumn);

    private readonly System.Windows.Forms.Timer _characterSelectionAutoScrollTimer;
    private Point _lastCharacterSelectionMousePoint;
    private int _autoScrollDirection; // -1: up, 0: none, 1: down

    private readonly VScrollBar _vScrollBar;
    private LargeFilePreviewState? _state;
    private List<string> _visibleLines = new List<string>();
    private List<bool> _isLineTruncated = new List<bool>();
    private int _renderedFirstLine;
    private bool _suppressScrollValueChanged;
    private bool _isSelecting;
    private bool _isCharacterSelecting;
    private int? _selectionAnchorLine;
    private int? _selectionStartLine;
    private int? _selectionEndLine;
    private int? _activeSearchHitLine;
    private int _activeSearchHitColumn;
    private int _activeSearchHitLength;
    private CharacterSelectionPoint? _characterSelectionAnchor;
    private CharacterSelectionPoint? _characterSelectionCaret;

    private Encoding _encoding = Encoding.UTF8;
    private Font _font = MidFD.Helpers.FontResolver.CreateFont(MidFD.Helpers.FontResolver.ResolveMonospaceFontFamily(), 10F);

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override Font Font
    {
        get => _font;
        set
        {
            if (value != null && _font != value)
            {
                _font = (Font)value.Clone();
                base.Font = _font;
                this.Invalidate();
            }
        }
    }
    private Color _lineNumberColor = Color.FromArgb(80, 80, 80);
    private Color _textColor = Color.FromArgb(200, 200, 200);

    public event EventHandler<int>? ScrollRequested;
    public event EventHandler<int>? CharacterSelectionAutoScrollRequested;
    public event EventHandler? SelectionChanged;
    public event EventHandler? FirstContentPainted;

    private bool _firstContentPaintReported;

    public void ResetFirstContentPaintMarker()
    {
        _firstContentPaintReported = false;
    }

    public LargeFilePreviewControl()
    {
        this.DoubleBuffered = true;
        this.BackColor = Color.FromArgb(25, 25, 25);
        this.Dock = DockStyle.Fill;
        this.TabStop = true;

        _vScrollBar = new VScrollBar
        {
            Dock = DockStyle.Right,
            Visible = true
        };
        _vScrollBar.ValueChanged += VScrollBar_ValueChanged;
        this.Controls.Add(_vScrollBar);

        _characterSelectionAutoScrollTimer = new System.Windows.Forms.Timer
        {
            Interval = 60
        };
        _characterSelectionAutoScrollTimer.Tick += CharacterSelectionAutoScrollTimer_Tick;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _characterSelectionAutoScrollTimer.Dispose();
            _font.Dispose();
        }
        base.Dispose(disposing);
    }

    public void SetState(LargeFilePreviewState state, Encoding encoding)
    {
        _state = state;
        _encoding = encoding;
        _renderedFirstLine = 0;
        _visibleLines.Clear();
        ClearSelections();
        ClearActiveSearchHit();
        UpdateScrollSettings();
        Invalidate();
    }

    public void SetVisibleLines(
        int firstLine,
        List<string> lines,
        List<bool>? truncatedFlags = null,
        bool preserveCharacterSelection = true)
    {
        int nextFirstLine = Math.Max(0, firstLine);
        bool moving = _renderedFirstLine != nextFirstLine;

        if (moving)
        {
            if (HasLineSelection)
            {
                ClearLineSelectionCore(raiseEvent: true);
            }

            if (!preserveCharacterSelection && HasCharacterSelectionCore)
            {
                ClearCharacterSelectionCore(raiseEvent: true);
            }
        }

        _renderedFirstLine = nextFirstLine;
        _visibleLines = lines ?? new List<string>();
        _isLineTruncated = truncatedFlags ?? new List<bool>();
        
        if (preserveCharacterSelection && _isCharacterSelecting && _characterSelectionAnchor.HasValue)
        {
            UpdateCharacterSelectionCaretFromMouse(_lastCharacterSelectionMousePoint);
        }
        else
        {
            _isCharacterSelecting = false;
        }

        // スクロールバーの位置をデータに合わせる (ValueChangedを抑止してループを防ぐ)
        SetScrollValueSilently(_renderedFirstLine);

        Invalidate();
        Update(); // 即時再描画を強制
    }

    /// <summary>
    /// スクロールバーの値を外部（MainForm）から同期させる。
    /// イベントを発火させないため、無限ループを回避できる。
    /// </summary>
    public void SetScrollValueSilently(int value)
    {
        int max = GetMaxFirstVisibleLine();
        if (value < 0) value = 0;
        if (value > max) value = max;

        if (_vScrollBar.Value != value)
        {
            _suppressScrollValueChanged = true;
            try
            {
                _vScrollBar.Value = value;
            }
            finally
            {
                _suppressScrollValueChanged = false;
            }
            Invalidate();
        }
    }

    public int FirstVisibleLine => _vScrollBar.Value;
    public bool HasSelectedLines => GetSelectedVisibleLineCount() > 0;
    public bool HasCharacterSelection => HasCharacterSelectionCore;
    public bool HasAnySelection => HasSelectedLines || HasCharacterSelectionCore;
    public int SelectedLineCount => GetSelectedVisibleLineCount();

    public int VisibleLineCount
    {
        get
        {
            // 完全に表示できる行数にする (文字切れ防止)
            float fontHeight = _font.Height;
            if (fontHeight <= 0) return 1;

            int drawableHeight = Math.Max(0, this.ClientSize.Height - 4);
            int count = (int)Math.Floor(drawableHeight / fontHeight);
            return Math.Max(1, count);
        }
    }

    public int GetMaxFirstVisibleLine()
    {
        if (_state == null) return 0;
        // 最終行が一番下に表示される状態 = TotalLines - 表示可能行数
        return Math.Max(0, _state.TotalLines - VisibleLineCount);
    }

    public void UpdateScrollSettings()
    {
        if (_state == null) return;

        int totalLines = _state.TotalLines;
        int visibleCount = VisibleLineCount;
        
        // WinForms VScrollBar の仕様:
        // ユーザーが操作可能な最大値は Maximum - LargeChange + 1
        // つまり、FirstVisibleLine の最大値 (max) を実現するには、
        // Maximum = max + LargeChange - 1 とする必要がある。
        
        int maxPos = GetMaxFirstVisibleLine();
        
        _suppressScrollValueChanged = true;
        try
        {
            _vScrollBar.Minimum = 0;
            _vScrollBar.LargeChange = visibleCount;
            _vScrollBar.SmallChange = 1;
            _vScrollBar.Maximum = maxPos + _vScrollBar.LargeChange - 1;
            _vScrollBar.Enabled = totalLines > visibleCount && !_state.IsIndexing;

            if (_vScrollBar.Value > maxPos)
            {
                _vScrollBar.Value = maxPos;
            }
        }
        finally
        {
            _suppressScrollValueChanged = false;
        }
    }

    private void VScrollBar_ValueChanged(object? sender, EventArgs e)
    {
        if (_suppressScrollValueChanged) return;
        // ユーザー操作による変更を MainForm へリクエストする
        ScrollRequested?.Invoke(this, _vScrollBar.Value);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateScrollSettings();
    }

    private LargeTextLayout GetLargeTextLayout(Graphics g)
    {
        float lineHeight = _font.Height;
        float gutterWidth = GetLineNumberGutterWidth(g);
        float textStartX = gutterWidth + 5;
        float textEndX = Math.Max(textStartX, ClientSize.Width - _vScrollBar.Width);

        // 等幅フォント前提で、TextRenderer のパディングを除去した「文字送り幅」を取得する。
        // 単一文字の MeasureText は両端にパディングを含むため、2文字との差分を取るのが正確。
        float w1 = TextRenderer.MeasureText(
            g,
            "M",
            _font,
            new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
        float w2 = TextRenderer.MeasureText(
            g,
            "MM",
            _font,
            new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
        float charWidth = Math.Max(1, w2 - w1);

        if (charWidth <= 0)
        {
            // fallback
            charWidth = Math.Max(1, g.MeasureString("M", _font).Width);
        }

        return new LargeTextLayout(
            lineHeight,
            gutterWidth,
            textStartX,
            textEndX,
            charWidth);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_state == null || _visibleLines == null) return;

        if (!_firstContentPaintReported && HasVisibleLinesForPaint())
        {
            _firstContentPaintReported = true;
            FirstContentPainted?.Invoke(this, EventArgs.Empty);
        }

        Graphics g = e.Graphics;
        LargeTextLayout layout = GetLargeTextLayout(g);
        int startLine = _renderedFirstLine;

        using var lineNumBrush = new SolidBrush(_lineNumberColor);
        using var textBrush = new SolidBrush(_textColor);
        using var lineNumPen = new Pen(_lineNumberColor);
        using var selectedBackBrush = new SolidBrush(SystemColors.Highlight);
        using var selectedTextBrush = new SolidBrush(SystemColors.HighlightText);
        using var searchHitBackBrush = new SolidBrush(SystemColors.Info);
        using var searchHitTextBrush = new SolidBrush(SystemColors.InfoText);

        float drawableBottom = this.ClientSize.Height - 2;
        float contentWidth = Math.Max(0, this.ClientSize.Width - _vScrollBar.Width);

        const TextFormatFlags textFlags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

        for (int i = 0; i < _visibleLines.Count; i++)
        {
            float y = i * layout.LineHeight;
            if (y + layout.LineHeight > drawableBottom) break;

            int absoluteLineIndex = startLine + i;
            int lineIdx = absoluteLineIndex + 1;
            bool hasCharacterSelectionOnLine = TryGetSelectedColumnRangeForLine(
                absoluteLineIndex,
                _visibleLines[i]?.Length ?? 0,
                out int selectedStartColumn,
                out int selectedEndColumn);
            bool isSelected = !hasCharacterSelectionOnLine && IsLineSelected(absoluteLineIndex);
            bool isSearchHit = !hasCharacterSelectionOnLine && IsActiveSearchHitLine(absoluteLineIndex);

            if (isSelected)
            {
                g.FillRectangle(selectedBackBrush, 0, y, contentWidth, layout.LineHeight);
            }
            else if (isSearchHit)
            {
                g.FillRectangle(searchHitBackBrush, 0, y, contentWidth, layout.LineHeight);
            }

            Color currentLineNumberColor = isSelected
                ? SystemColors.HighlightText
                : isSearchHit
                    ? SystemColors.InfoText
                    : _lineNumberColor;
            Color currentTextColor = isSelected
                ? SystemColors.HighlightText
                : isSearchHit
                    ? SystemColors.InfoText
                    : _textColor;

            string lineNumberText = lineIdx.ToString("N0");
            Size lineNumSize = TextRenderer.MeasureText(g, lineNumberText, _font, new Size(int.MaxValue, int.MaxValue), textFlags);
            float lineNumberX = layout.GutterWidth - 5 - lineNumSize.Width;

            // 行番号
            TextRenderer.DrawText(g, lineNumberText, _font, new Point((int)lineNumberX, (int)y), currentLineNumberColor, textFlags);
            
            // 区切り線
            if (i == 0) g.DrawLine(lineNumPen, layout.GutterWidth, 0, layout.GutterWidth, this.Height);

            // 本文背景 (文字選択がある場合)
            string lineText = _visibleLines[i] ?? string.Empty;
            if (hasCharacterSelectionOnLine && selectedStartColumn < selectedEndColumn)
            {
                // 選択外 (前)
                string preText = selectedStartColumn > 0 ? lineText.Substring(0, selectedStartColumn) : string.Empty;
                float preWidth = MeasureTextWidth(g, preText);
                
                // 選択部分
                string selectedText = lineText.Substring(selectedStartColumn, Math.Min(selectedEndColumn, lineText.Length) - selectedStartColumn);
                float selWidth = MeasureTextWidth(g, selectedText);
                
                float selX = layout.TextStartX + preWidth;
                var selectionRect = new RectangleF(selX, y, Math.Max(1F, selWidth), layout.LineHeight);
                g.FillRectangle(selectedBackBrush, selectionRect);

                // テキスト描画 (分割描画によるズレを最小化するため、各パーツの開始位置を正確に計算)
                if (preText.Length > 0)
                {
                    if (preText.Length > MaxRenderableLineLength) preText = preText.Substring(0, MaxRenderableLineLength);
                    TextRenderer.DrawText(g, preText, _font, new Point((int)layout.TextStartX, (int)y), currentTextColor, textFlags);
                }

                if (selectedText.Length > 0)
                {
                    if (selectedText.Length > MaxRenderableLineLength) selectedText = selectedText.Substring(0, MaxRenderableLineLength);
                    TextRenderer.DrawText(g, selectedText, _font, new Point((int)selX, (int)y), SystemColors.HighlightText, textFlags);
                }

                // 選択外 (後)
                if (selectedEndColumn < lineText.Length || (i < _isLineTruncated.Count && _isLineTruncated[i]))
                {
                    string postText = selectedEndColumn < lineText.Length ? lineText.Substring(selectedEndColumn) : string.Empty;
                    if (postText.Length > MaxRenderableLineLength) postText = postText.Substring(0, MaxRenderableLineLength);
                    
                    if (i < _isLineTruncated.Count && _isLineTruncated[i])
                    {
                        postText += " … [長大行のため表示を省略]";
                    }

                    float postX = selX + selWidth;
                    TextRenderer.DrawText(g, postText, _font, new Point((int)postX, (int)y), currentTextColor, textFlags);
                }
            }
            else
            {
                // 通常描画
                string drawText = lineText;
                if (drawText.Length > MaxRenderableLineLength) drawText = drawText.Substring(0, MaxRenderableLineLength);
                
                bool isTruncated = i < _isLineTruncated.Count && _isLineTruncated[i];
                if (isTruncated)
                {
                    drawText += " … [長大行のため表示を省略]";
                }

                TextRenderer.DrawText(g, drawText, _font, new Point((int)layout.TextStartX, (int)y), currentTextColor, textFlags);
            }
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        Focus();

        bool isShift = (Control.ModifierKeys & Keys.Shift) == Keys.Shift;
        if (isShift && TryBeginShiftCharacterSelection(e.Location))
        {
            return;
        }

        // 通常の MouseDown (Shiftなし、または有効な既存Anchorがない場合)
        // まず、既存のすべての選択状態（アンカー含む）を「必ず」リセットする。
        ClearSelections();
        _isCharacterSelecting = false;
        _isSelecting = false;
        _selectionAnchorLine = null;

        int? hitLine = HitTestLineFromPoint(e.Location);
        if (!hitLine.HasValue)
        {
            return;
        }

        if (IsInGutterArea(e.Location))
        {
            // 新しい行選択を開始
            _isSelecting = true;
            Capture = true;
            _selectionAnchorLine = hitLine.Value;
            SetLineSelection(hitLine.Value, hitLine.Value);
            return;
        }

        CharacterSelectionPoint? startPoint = GetCharacterSelectionPointFromMouse(e.Location);
        if (!startPoint.HasValue)
        {
            // 有効な本文文字位置を取れない場合は、文字選択を開始しない
            LogService.Info($"LargeText OnMouseDown: Rejected (mouseX={e.Location.X})");
            return;
        }

        // 通常クリック: 有効な位置からのみ新しい文字選択を開始
        _isCharacterSelecting = true;
        Capture = true;
        _characterSelectionAnchor = startPoint.Value;
        _characterSelectionCaret = startPoint.Value;
        _lastCharacterSelectionMousePoint = e.Location;
        
        using (var g = CreateGraphics())
        {
            LargeTextLayout layout = GetLargeTextLayout(g);
            LogService.Info($"LargeText OnMouseDown: Accepted (mouseX={e.Location.X}, textStartX={layout.TextStartX}, anchorColumn={startPoint.Value.Column})");
        }

        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if ((e.Button & MouseButtons.Left) != MouseButtons.Left)
        {
            return;
        }

        if (_isCharacterSelecting && _characterSelectionAnchor.HasValue)
        {
            _lastCharacterSelectionMousePoint = e.Location;
            UpdateCharacterSelectionCaretFromMouse(e.Location);
            UpdateCharacterSelectionAutoScroll(e.Location);
            
            if (_characterSelectionCaret.HasValue)
            {
                LogService.Info($"LargeText OnMouseMove: dragged (mouseX={e.Location.X}, caretColumn={_characterSelectionCaret.Value.Column})");
            }
            return;
        }

        if (!_isSelecting || !_selectionAnchorLine.HasValue)
        {
            return;
        }

        int? hitLine = HitTestLineFromPoint(e.Location) ?? ClampVisibleLineFromPoint(e.Location);
        if (!hitLine.HasValue)
        {
            return;
        }

        SetLineSelection(_selectionAnchorLine.Value, hitLine.Value);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        _isSelecting = false;
        _isCharacterSelecting = false;
        StopCharacterSelectionAutoScroll();
        Capture = false;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        int delta = e.Delta;
        int linesToScroll = -delta / 120 * 3; // 1ノッチ3行
        int targetLine = _vScrollBar.Value + linesToScroll;
        
        ScrollRequested?.Invoke(this, targetLine);
    }

    public string GetVisibleText()
    {
        if (_visibleLines == null || _visibleLines.Count == 0) return string.Empty;
        // 実際に表示されている行数分だけを結合する
        int count = Math.Min(_visibleLines.Count, VisibleLineCount);
        return string.Join(Environment.NewLine, _visibleLines.GetRange(0, count));
    }

    public string GetSelectedText()
    {
        if (!HasLineSelection || _visibleLines.Count == 0)
        {
            return string.Empty;
        }

        int visibleStart = _renderedFirstLine;
        int visibleEnd = _renderedFirstLine + _visibleLines.Count - 1;
        int selectionStart = Math.Max(_selectionStartLine!.Value, visibleStart);
        int selectionEnd = Math.Min(_selectionEndLine!.Value, visibleEnd);
        if (selectionStart > selectionEnd)
        {
            return string.Empty;
        }

        List<string> selectedLines = new List<string>();
        for (int line = selectionStart; line <= selectionEnd; line++)
        {
            int visibleIndex = line - _renderedFirstLine;
            if (visibleIndex >= 0 && visibleIndex < _visibleLines.Count)
            {
                selectedLines.Add(_visibleLines[visibleIndex]);
            }
        }

        return string.Join(Environment.NewLine, selectedLines);
    }

    public string GetSelectedCharacterText()
    {
        if (!HasCharacterSelectionCore || _visibleLines.Count == 0)
        {
            return string.Empty;
        }

        var (start, end) = NormalizeCharacterSelection(_characterSelectionAnchor!.Value, _characterSelectionCaret!.Value);
        List<string> selectedLines = new List<string>();

        for (int line = start.AbsoluteLine; line <= end.AbsoluteLine; line++)
        {
            int visibleIndex = line - _renderedFirstLine;
            if (visibleIndex < 0 || visibleIndex >= _visibleLines.Count)
            {
                continue;
            }

            string text = _visibleLines[visibleIndex] ?? string.Empty;
            int startColumn = line == start.AbsoluteLine ? start.Column : 0;
            int endColumn = line == end.AbsoluteLine ? end.Column : text.Length;
            startColumn = Math.Clamp(startColumn, 0, text.Length);
            endColumn = Math.Clamp(endColumn, 0, text.Length);
            if (endColumn < startColumn)
            {
                (startColumn, endColumn) = (endColumn, startColumn);
            }

            selectedLines.Add(text.Substring(startColumn, endColumn - startColumn));
        }

        return string.Join(Environment.NewLine, selectedLines);
    }

    public bool TryGetCharacterSelectionRange(out CharacterSelectionRange range)
    {
        range = default;

        if (!HasCharacterSelectionCore)
        {
            return false;
        }

        var (start, end) = NormalizeCharacterSelection(
            _characterSelectionAnchor!.Value,
            _characterSelectionCaret!.Value);

        range = new CharacterSelectionRange(
            start.AbsoluteLine,
            start.Column,
            end.AbsoluteLine,
            end.Column);

        LogService.Info(
            $"[LargeTextSelectionRange] " +
            $"anchor=({_characterSelectionAnchor?.AbsoluteLine}:{_characterSelectionAnchor?.Column}) " +
            $"caret=({_characterSelectionCaret?.AbsoluteLine}:{_characterSelectionCaret?.Column}) " +
            $"normalized=({range.StartLine}:{range.StartColumn})-({range.EndLine}:{range.EndColumn})");

        return true;
    }

    public void ClearLineSelection()
    {
        ClearLineSelectionCore(raiseEvent: true);
    }

    public void ClearCharacterSelection()
    {
        ClearCharacterSelectionCore(raiseEvent: true);
    }

    public void ClearSelections()
    {
        bool hadSelection = HasAnySelection || _selectionAnchorLine.HasValue || _isSelecting || _isCharacterSelecting;
        _isSelecting = false;
        _isCharacterSelecting = false;
        StopCharacterSelectionAutoScroll();
        Capture = false;
        _selectionAnchorLine = null;
        _selectionStartLine = null;
        _selectionEndLine = null;
        _characterSelectionAnchor = null;
        _characterSelectionCaret = null;
        if (!hadSelection)
        {
            return;
        }

        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectAll()
    {
        if (_state == null || _state.TotalLines <= 0)
        {
            return;
        }

        int lastLine = _state.TotalLines - 1;
        _characterSelectionAnchor = new CharacterSelectionPoint(0, 0);
        _characterSelectionCaret = new CharacterSelectionPoint(lastLine, int.MaxValue);
        _isCharacterSelecting = true;

        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetActiveSearchHit(int? line, int column, int length)
    {
        int normalizedColumn = Math.Max(0, column);
        int normalizedLength = Math.Max(0, length);
        if (_activeSearchHitLine == line
            && _activeSearchHitColumn == normalizedColumn
            && _activeSearchHitLength == normalizedLength)
        {
            return;
        }

        _activeSearchHitLine = line;
        _activeSearchHitColumn = normalizedColumn;
        _activeSearchHitLength = normalizedLength;
        Invalidate();
    }

    public void ClearActiveSearchHit()
    {
        if (!_activeSearchHitLine.HasValue && _activeSearchHitColumn == 0 && _activeSearchHitLength == 0)
        {
            return;
        }

        _activeSearchHitLine = null;
        _activeSearchHitColumn = 0;
        _activeSearchHitLength = 0;
        Invalidate();
    }

    private bool HasLineSelection => _selectionStartLine.HasValue && _selectionEndLine.HasValue;
    private bool HasCharacterSelectionCore =>
        _characterSelectionAnchor.HasValue
        && _characterSelectionCaret.HasValue
        && !_characterSelectionAnchor.Value.Equals(_characterSelectionCaret.Value);

    private bool IsLineSelected(int absoluteLineIndex)
    {
        if (!HasLineSelection)
        {
            return false;
        }

        return absoluteLineIndex >= _selectionStartLine!.Value
            && absoluteLineIndex <= _selectionEndLine!.Value;
    }

    private bool IsActiveSearchHitLine(int absoluteLineIndex)
    {
        return _activeSearchHitLine.HasValue && _activeSearchHitLine.Value == absoluteLineIndex;
    }

    private bool IsInGutterArea(Point point)
    {
        using var g = CreateGraphics();
        float gutterWidth = GetLineNumberGutterWidth(g);
        return point.X < gutterWidth;
    }

    private CharacterSelectionPoint? GetCharacterSelectionPointFromMouse(Point point, bool clampToVisible = false)
    {
        if (_visibleLines.Count == 0)
        {
            return null;
        }

        using var g = CreateGraphics();
        LargeTextLayout layout = GetLargeTextLayout(g);

        int contentWidth = Math.Max(0, this.ClientSize.Width - _vScrollBar.Width);
        if (point.X < 0 || point.X >= contentWidth)
        {
            if (!clampToVisible)
            {
                return null;
            }
        }

        int visibleIndex = (int)(point.Y / layout.LineHeight);
        if (clampToVisible)
        {
            if (point.Y < 0)
            {
                visibleIndex = 0;
            }
            else if (point.Y >= ClientSize.Height)
            {
                visibleIndex = Math.Min(_visibleLines.Count - 1, VisibleLineCount - 1);
            }
            else
            {
                visibleIndex = Math.Clamp(visibleIndex, 0, _visibleLines.Count - 1);
            }
        }
        else if (visibleIndex < 0 || visibleIndex >= _visibleLines.Count)
        {
            return null;
        }

        int absoluteLine = _renderedFirstLine + visibleIndex;
        string text = _visibleLines[visibleIndex] ?? string.Empty;
        int column = GetColumnFromX(g, layout, text, point.X, clampToVisible);
        if (column < 0) return null;
        
        return new CharacterSelectionPoint(absoluteLine, column);
    }

    private static (CharacterSelectionPoint Start, CharacterSelectionPoint End) NormalizeCharacterSelection(
        CharacterSelectionPoint a,
        CharacterSelectionPoint b)
    {
        if (a.AbsoluteLine < b.AbsoluteLine)
        {
            return (a, b);
        }

        if (a.AbsoluteLine > b.AbsoluteLine)
        {
            return (b, a);
        }

        return a.Column <= b.Column ? (a, b) : (b, a);
    }

    private bool TryGetSelectedColumnRangeForLine(int absoluteLine, int textLength, out int startColumn, out int endColumn)
    {
        startColumn = 0;
        endColumn = 0;
        if (!HasCharacterSelectionCore)
        {
            return false;
        }

        var (start, end) = NormalizeCharacterSelection(_characterSelectionAnchor!.Value, _characterSelectionCaret!.Value);
        if (absoluteLine < start.AbsoluteLine || absoluteLine > end.AbsoluteLine)
        {
            return false;
        }

        startColumn = absoluteLine == start.AbsoluteLine ? start.Column : 0;
        endColumn = absoluteLine == end.AbsoluteLine ? end.Column : textLength;
        startColumn = Math.Clamp(startColumn, 0, textLength);
        endColumn = Math.Clamp(endColumn, 0, textLength);
        if (endColumn < startColumn)
        {
            (startColumn, endColumn) = (endColumn, startColumn);
        }

        return startColumn != endColumn;
    }

    private float MeasureTextWidth(Graphics g, string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return TextRenderer.MeasureText(
            g,
            text,
            _font,
            new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
    }

    private float GetLineNumberGutterWidth(Graphics g)
    {
        string measureStr = "999,999,999";
        float width = MeasureTextWidth(g, measureStr);
        return width + 10;
    }

    private int GetColumnFromX(Graphics g, LargeTextLayout layout, string text, int mouseX, bool clampToVisible)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        int maxLength = Math.Min(text.Length, MaxRenderableLineLength);
        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

        if (mouseX < layout.TextStartX)
        {
            if (!clampToVisible) return -1; // MouseDown: 領域外は無効
            return 0; // MouseMove: ドラッグ中は0へクランプ
        }

        float relativeX = mouseX - layout.TextStartX;

        string maxSub = text.Substring(0, maxLength);
        float maxWidth = TextRenderer.MeasureText(g, maxSub, _font, new Size(int.MaxValue, int.MaxValue), flags).Width;

        if (relativeX > maxWidth)
        {
            if (!clampToVisible) return -1; // MouseDown: 文字列より右側の何もない領域は無効
            return maxLength; // MouseMove: 表示範囲の末尾へクランプ
        }

        // MeasureTextWidth ベースの二分探索により、GDI描画と完全に一致する文字境界を特定する。
        int low = 0;
        int high = maxLength;
        int bestColumn = 0;
        float bestDiff = float.MaxValue;

        while (low <= high)
        {
            int mid = low + (high - low) / 2;
            string sub = text.Substring(0, mid);
            float width = TextRenderer.MeasureText(g, sub, _font, new Size(int.MaxValue, int.MaxValue), flags).Width;
            
            float diff = Math.Abs(width - relativeX);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                bestColumn = mid;
            }

            if (width < relativeX)
            {
                low = mid + 1;
            }
            else if (width > relativeX)
            {
                high = mid - 1;
            }
            else
            {
                break; // 完全に一致
            }
        }

        return Math.Clamp(bestColumn, 0, maxLength);
    }

    private void SetLineSelection(int startLine, int endLine)
    {
        if (HasCharacterSelectionCore || _characterSelectionAnchor.HasValue || _characterSelectionCaret.HasValue)
        {
            ClearCharacterSelectionCore(raiseEvent: false);
        }

        int normalizedStart = Math.Min(startLine, endLine);
        int normalizedEnd = Math.Max(startLine, endLine);
        if (_selectionStartLine == normalizedStart && _selectionEndLine == normalizedEnd)
        {
            return;
        }

        _selectionStartLine = normalizedStart;
        _selectionEndLine = normalizedEnd;
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearLineSelectionCore(bool raiseEvent)
    {
        bool hadSelection = HasLineSelection || _selectionAnchorLine.HasValue;
        _isSelecting = false;
        _selectionAnchorLine = null;
        _selectionStartLine = null;
        _selectionEndLine = null;

        if (!hadSelection)
        {
            return;
        }

        Invalidate();
        if (raiseEvent)
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ClearCharacterSelectionCore(bool raiseEvent)
    {
        bool hadSelection = HasCharacterSelectionCore || _characterSelectionAnchor.HasValue || _characterSelectionCaret.HasValue || _isCharacterSelecting;
        _isCharacterSelecting = false;
        _characterSelectionAnchor = null;
        _characterSelectionCaret = null;
        StopCharacterSelectionAutoScroll();
        if (!hadSelection)
        {
            return;
        }

        Invalidate();
        if (raiseEvent)
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void UpdateCharacterSelectionCaretFromMouse(Point point)
    {
        if (!_characterSelectionAnchor.HasValue)
        {
            return;
        }

        CharacterSelectionPoint? selectionPoint = GetCharacterSelectionPointFromMouse(point, clampToVisible: true);
        if (!selectionPoint.HasValue)
        {
            return;
        }

        if (_characterSelectionCaret.HasValue && _characterSelectionCaret.Value.Equals(selectionPoint.Value))
        {
            return;
        }

        _characterSelectionCaret = selectionPoint.Value;
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }



    private void UpdateCharacterSelectionAutoScroll(Point point)
    {
        bool canScrollUp = _renderedFirstLine > 0;
        int margin = Math.Max(8, _font.Height / 2);

        // 上スクロール: コントロール外に出たか、スクロール可能な状態でマージン内に入った場合のみ
        if (point.Y < 0 || (canScrollUp && point.Y < margin))
        {
            if (_autoScrollDirection != -1)
            {
                LogService.Info($"LargeText AutoScroll: Start UP (Y={point.Y}, canScrollUp={canScrollUp})");
            }
            StartCharacterSelectionAutoScroll(-1);
            return;
        }

        // 下スクロール: コントロール外に出た場合のみ（安全側）
        if (point.Y > ClientSize.Height)
        {
            if (_autoScrollDirection != 1)
            {
                LogService.Info($"LargeText AutoScroll: Start DOWN (Y={point.Y})");
            }
            StartCharacterSelectionAutoScroll(1);
            return;
        }

        if (_autoScrollDirection != 0)
        {
            LogService.Info($"LargeText AutoScroll: Stop (Y={point.Y})");
        }
        StopCharacterSelectionAutoScroll();
    }

    private void StartCharacterSelectionAutoScroll(int direction)
    {
        _autoScrollDirection = Math.Sign(direction);

        if (_autoScrollDirection == 0)
        {
            StopCharacterSelectionAutoScroll();
            return;
        }

        if (!_characterSelectionAutoScrollTimer.Enabled)
        {
            _characterSelectionAutoScrollTimer.Start();
        }
    }

    private void StopCharacterSelectionAutoScroll()
    {
        _autoScrollDirection = 0;

        if (_characterSelectionAutoScrollTimer.Enabled)
        {
            _characterSelectionAutoScrollTimer.Stop();
        }
    }

    internal bool TryBeginShiftCharacterSelection(Point point)
    {
        CharacterSelectionPoint? selectionPoint = GetCharacterSelectionPointFromMouse(point, clampToVisible: true);
        if (!selectionPoint.HasValue)
        {
            return false;
        }

        CharacterSelectionPoint anchor = _characterSelectionAnchor
            ?? _characterSelectionCaret
            ?? selectionPoint.Value;

        ClearLineSelectionCore(raiseEvent: false);
        _selectionAnchorLine = null;
        _isSelecting = false;
        _isCharacterSelecting = true;
        Capture = true;
        _characterSelectionAnchor = anchor;
        _characterSelectionCaret = selectionPoint.Value;
        _lastCharacterSelectionMousePoint = point;
        StopCharacterSelectionAutoScroll();

        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void CharacterSelectionAutoScrollTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isCharacterSelecting || !_characterSelectionAnchor.HasValue)
        {
            StopCharacterSelectionAutoScroll();
            return;
        }

        if (_autoScrollDirection == 0)
        {
            StopCharacterSelectionAutoScroll();
            return;
        }

        CharacterSelectionAutoScrollRequested?.Invoke(this, _autoScrollDirection);
    }

    private int GetCharacterSelectionAutoScrollStep()
    {
        int distance;

        if (_autoScrollDirection < 0)
        {
            distance = Math.Max(0, -_lastCharacterSelectionMousePoint.Y);
        }
        else
        {
            distance = Math.Max(0, _lastCharacterSelectionMousePoint.Y - ClientSize.Height);
        }

        if (distance > _font.Height * 3)
        {
            return 5;
        }

        if (distance > _font.Height)
        {
            return 3;
        }

        return 1;
    }

    public void ExtendCharacterSelectionToVisibleEdge(int direction)
    {
        if (!_isCharacterSelecting || !_characterSelectionAnchor.HasValue || _visibleLines.Count == 0)
        {
            return;
        }

        if (direction < 0 && _renderedFirstLine == 0)
        {
            // 上方向へスクロールできない状態（先頭行表示中）では、強制的に column = 0 にしない
            LogService.Info("LargeText ExtendSelection: Guarded (Already at first line, ignoring UP direction)");
            return;
        }

        int line = direction > 0
            ? _renderedFirstLine + _visibleLines.Count - 1
            : _renderedFirstLine;

        int visibleIndex = line - _renderedFirstLine;
        string text = (visibleIndex >= 0 && visibleIndex < _visibleLines.Count)
            ? _visibleLines[visibleIndex]
            : string.Empty;

        int column = direction > 0
            ? text.Length
            : 0;

        LogService.Info($"LargeText ExtendSelection: direction={direction}, currentFirstLine={_renderedFirstLine}, targetLine={line}, column={column}");

        _characterSelectionCaret = new CharacterSelectionPoint(line, column);
        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private int? HitTestLineFromPoint(Point point)
    {
        if (_visibleLines.Count == 0)
        {
            return null;
        }

        int contentWidth = Math.Max(0, this.ClientSize.Width - _vScrollBar.Width);
        if (point.X < 0 || point.X >= contentWidth || point.Y < 0)
        {
            return null;
        }

        int visibleIndex = (int)(point.Y / _font.Height);
        if (visibleIndex < 0 || visibleIndex >= _visibleLines.Count)
        {
            return null;
        }

        return _renderedFirstLine + visibleIndex;
    }

    private int? ClampVisibleLineFromPoint(Point point)
    {
        if (_visibleLines.Count == 0)
        {
            return null;
        }

        if (point.Y < 0)
        {
            return _renderedFirstLine;
        }

        return _renderedFirstLine + _visibleLines.Count - 1;
    }

    private int GetSelectedVisibleLineCount()
    {
        if (!HasLineSelection || _visibleLines.Count == 0)
        {
            return 0;
        }

        int visibleStart = _renderedFirstLine;
        int visibleEnd = _renderedFirstLine + _visibleLines.Count - 1;
        int start = Math.Max(_selectionStartLine!.Value, visibleStart);
        int end = Math.Min(_selectionEndLine!.Value, visibleEnd);
        return start > end ? 0 : end - start + 1;
    }

    private bool HasVisibleLinesForPaint()
    {
        return _visibleLines != null && _visibleLines.Count > 0;
    }
}
