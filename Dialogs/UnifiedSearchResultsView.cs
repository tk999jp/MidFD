using MidFD.Models;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MidFD.Dialogs;

public sealed class UnifiedSearchResultsView : UserControl
{
    private readonly record struct VisibleContentFile(
        UnifiedSearchResultFile Source,
        int FirstFilteredHit,
        int FilteredHitCount,
        bool UsesOriginalMatches)
    {
        public int HitCount => UsesOriginalMatches ? Source.Matches.Count : FilteredHitCount;
    }

    private sealed record SelectionIdentity(string FullPath, UnifiedSearchMatch? Hit);

    private readonly string _rootPath;
    private readonly UnifiedFilterFindMode _mode;
    private readonly Func<string, bool, UnifiedSearchMatch?, string?> _activate;
    private readonly Func<string, bool, UnifiedSearchMatch?, string?>? _activateInNewTab;
    private readonly Func<UnifiedFilterFindSearchResult, string?> _export;
    private readonly Func<UnifiedSearchResultAppearance>? _appearanceFactory;
    private readonly string _queryDescription;
    private readonly Label _header = new() { Dock = DockStyle.Top, Height = 48, AutoEllipsis = true, Padding = new Padding(8, 5, 8, 2) };
    private readonly Label _progressText = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 4, 0) };
    private readonly Label _currentPathText = new() { Dock = DockStyle.Fill, Height = 18, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 4, 0) };
    private readonly Panel _progressCountsRow = new() { Dock = DockStyle.Top, Height = 22 };
    private readonly Panel _progressPanel = new() { Dock = DockStyle.Top, Height = 42, Padding = new Padding(0, 0, 8, 0) };
    private readonly IndeterminateProgressRing _progressRing = new() { Dock = DockStyle.Right, Width = 20, Visible = false };
    private readonly Panel _filterPanel = new() { Dock = DockStyle.Top, Height = 30, Padding = new Padding(8, 2, 8, 2), Visible = false };
    private readonly Label _filterLabel = new() { Dock = DockStyle.Left, Text = "絞込:", TextAlign = ContentAlignment.MiddleLeft };
    private readonly TextBox _filterInput = new() { Dock = DockStyle.Fill, PlaceholderText = "結果を絞り込み", Enabled = false };
    private readonly Label _filterCount = new() { Dock = DockStyle.Right, Width = 280, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleRight, Visible = false };
    private readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, VirtualMode = true, MultiSelect = false, FullRowSelect = true, HideSelection = false };
    private readonly Panel _bottomPanel = new() { Dock = DockStyle.Bottom, Height = 22 };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Padding = new Padding(8, 0, 8, 0), Visible = false };
    private readonly Label _position = new() { Dock = DockStyle.Right, Width = 230, AutoEllipsis = true, Padding = new Padding(4, 0, 8, 0), TextAlign = ContentAlignment.MiddleRight, Visible = false };
    private UnifiedFilterFindSearchResult? _snapshot;
    private IReadOnlyList<UnifiedSearchNameResult> _visibleNames = [];
    private VisibleContentFile[] _visibleFiles = [];
    private UnifiedSearchMatch[] _filteredMatches = [];
    private SelectionIdentity? _filterClearSelection;
    private string _lastFilter = string.Empty;
    private bool _completed;
    private string _backendDescription = string.Empty;
    internal void UpdateBackend(ContentSearchBackendInfo info)
    {
        if (IsDisposed || !IsContent) return;
        if (_backendDescription.Length > 0) _header.Text = _header.Text.Replace(_backendDescription, string.Empty, StringComparison.Ordinal).TrimEnd();
        _backendDescription = $"　エンジン: {info.DisplayName}{(info.Version == null ? string.Empty : " " + info.Version)}";
        _header.Text += _backendDescription;
    }
    private bool _updatingProjection;
    private bool _updatingSelection;
    private int _visibleMatchCount;
    private int _selected = -1;
    private int _hitIndex;
    private readonly Dictionary<string, List<UnifiedSearchMatch>> _streamedMatches = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<VisibleContentFile> _streamedFiles = [];
    private readonly HashSet<UnifiedSearchMatch> _streamedIdentities = [];

    internal ListView ResultList => _list;
    internal TextBox ResultFilterInput => _filterInput;
    internal int SelectedResultIndex => _selected;
    internal int CurrentHitIndex => _hitIndex;
    internal string HitPosition => IsContent && _selected >= 0 ? $"{_hitIndex + 1} / {_visibleFiles[_selected].HitCount}" : string.Empty;
    internal int VisibleResultCount => IsContent ? _visibleFiles.Length : _visibleNames.Count;
    internal int VisibleMatchCount => _visibleMatchCount;
    internal bool HasResults => _snapshot?.ResultCount > 0 || (!_completed && VisibleResultCount > 0);
    internal bool CanExport => _snapshot?.ResultCount > 0;
    internal string ActionStatus => _status.Text;
    internal string StatusText => _status.Text;
    internal string HeaderText => _header.Text;
    internal string ProgressText => _progressText.Text;
    internal string CurrentPathText => _currentPathText.Text;
    internal string FilterCountText => _filterCount.Text;
    internal string PositionText => _position.Text;
    internal string ResultFilterText => _filterInput.Text;
    internal bool ResultFilterEnabled => _filterInput.Enabled;
    internal bool ResultFilterFocused => _filterInput.Focused;
    internal bool ProgressIndicatorVisible => _progressRing.Visible && _progressRing.IsRunning && _progressRing.SurfaceActive;
    internal bool ProgressAnimationEnabled => _progressRing.AnimationEnabled;
    internal int ProgressAnimationTickCount => _progressRing.AnimationTickCount;
    internal int ProgressRingInvalidationCount => _progressRing.InvalidationCount;
    internal int ProgressRingWidth => _progressRing.Width;
    internal bool CurrentPathUsesEllipsis => _currentPathText.AutoEllipsis;
    internal bool ProgressShowsPercentage => _progressText.Text.Contains("%", StringComparison.Ordinal);
    internal int ProjectionVisitedFileCount { get; private set; }
    internal int ProjectionVisitedMatchCount { get; private set; }
    internal int MaterializedRowCount { get; private set; }
    internal int ProgressUpdateCount { get; private set; }
    internal IReadOnlyList<int> ColumnWidths => _list.Columns.Cast<ColumnHeader>().Select(column => column.Width).ToArray();
    public event EventHandler? CloseRequested;

    public UnifiedSearchResultsView(
        string rootPath,
        UnifiedFilterFindMode mode,
        UnifiedFilterFindCriteria criteria,
        Func<string, bool, UnifiedSearchMatch?, string?> activate,
        Func<UnifiedFilterFindSearchResult, string?> export,
        Func<UnifiedSearchResultAppearance>? appearanceFactory = null,
        Func<string, bool, UnifiedSearchMatch?, string?>? activateInNewTab = null)
    {
        _rootPath = rootPath;
        _mode = mode;
        _activate = activate;
        _activateInNewTab = activateInNewTab;
        _export = export;
        _appearanceFactory = appearanceFactory;
        AutoScaleMode = AutoScaleMode.Font;
        Dock = DockStyle.Fill;
        Name = "unifiedSearchResultsView";
        string query = string.IsNullOrWhiteSpace(criteria.ContentPattern) ? criteria.NamePattern : criteria.ContentPattern;
        _queryDescription = $"{(IsContent ? "内容検索" : "名前検索")}: {query}";
        _header.Text = $"対象: {rootPath}\n{_queryDescription}　検索中...";
        _progressCountsRow.Controls.Add(_progressText);
        _progressCountsRow.Controls.Add(_progressRing);
        _progressPanel.Controls.Add(_currentPathText);
        _progressPanel.Controls.Add(_progressCountsRow);
        _filterPanel.Controls.Add(_filterInput);
        _filterPanel.Controls.Add(_filterCount);
        _filterPanel.Controls.Add(_filterLabel);
        _bottomPanel.Controls.Add(_status);
        _bottomPanel.Controls.Add(_position);
        _list.OwnerDraw = true;
        _list.GridLines = false;
        _list.Columns.Add("ファイル", 400);
        if (IsContent)
        {
            _list.Columns.Add("ヒット数", 58);
            _list.Columns.Add("行", 64);
            _list.Columns.Add("列", 64);
            _list.Columns.Add("内容", 160);
        }
        else _list.Columns.Add("種類", 72);
        _list.ClientSizeChanged += (_, _) => LayoutColumnsToClientWidth();
        FontChanged += (_, _) => UpdateFontAwareLayout();
        _list.RetrieveVirtualItem += (_, e) => e.Item = CreateRow(e.ItemIndex);
        _list.DrawColumnHeader += DrawColumnHeader;
        _list.DrawItem += (_, e) => e.DrawDefault = false;
        _list.DrawSubItem += DrawSubItem;
        _filterInput.TextChanged += (_, _) => ApplyResultFilterFromInput();
        _list.SelectedIndexChanged += (_, _) =>
        {
            if (_updatingSelection || _updatingProjection) return;
            if (_list.SelectedIndices.Count > 0) SelectResult(_list.SelectedIndices[0], userInitiated: true);
            else { _selected = -1; _hitIndex = 0; RefreshHit(); UpdatePosition(); }
        };
        _list.DoubleClick += (_, _) => ActivateSelected();
        Controls.Add(_list);
        Controls.Add(_bottomPanel);
        Controls.Add(_filterPanel);
        Controls.Add(_progressPanel);
        Controls.Add(_header);
        ApplyCurrentAppearance();
        _progressRing.SetRunning(true);
        _progressRing.SetSurfaceActive(Visible);
        UpdateProgress(new UnifiedSearchProgress(0, 0, 0, 0, 0, rootPath));
        VisibleChanged += (_, _) =>
        {
            _progressRing.SetSurfaceActive(Visible);
            if (Visible && _list.CanFocus) BeginInvoke(new Action(() => { if (Visible && _list.CanFocus) _list.Focus(); }));
        };
    }

    private bool IsContent => _mode == UnifiedFilterFindMode.SearchContentsRecursively;

    internal void UpdateProgress(UnifiedSearchProgress progress)
    {
        if (IsDisposed || _completed) return;
        ProgressUpdateCount++;
        _progressText.Text = IsContent
            ? $"ファイル: {progress.ScannedFileCount:N0}件　ヒット: {progress.HitCount:N0}件　スキップ: {progress.SkippedCount:N0}件"
            : $"走査: {progress.ScannedEntryCount:N0}件　一致: {progress.MatchedEntryCount:N0}件　スキップ: {progress.SkippedCount:N0}件";
        _currentPathText.Text = string.IsNullOrWhiteSpace(progress.CurrentPath) ? string.Empty : $"現在: {progress.CurrentPath}";
        _progressRing.SetRunning(true);
    }

    internal void AppendBatch(ContentSearchBatch batch)
    {
        if (IsDisposed || _completed || !IsContent) return;
        bool addedFile = false;
        foreach (UnifiedSearchMatch hit in batch.Matches)
        {
            if (!_streamedIdentities.Add(hit)) continue;
            if (!_streamedMatches.TryGetValue(hit.FullPath, out var matches))
            {
                _streamedMatches[hit.FullPath] = matches = [];
                _streamedFiles.Add(new(new UnifiedSearchResultFile(hit.FullPath, matches, live: true), 0, 0, true));
                addedFile = true;
            }
            matches.Add(hit);
            _visibleMatchCount++;
        }
        if (addedFile) _visibleFiles = _streamedFiles.ToArray();
        _updatingProjection = true;
        try { _list.VirtualListSize = VisibleResultCount; }
        finally { _updatingProjection = false; }
        if (_selected < 0 && VisibleResultCount > 0) SelectResult(0);
        _position.Visible = VisibleResultCount > 0;
        _header.Text = $"対象: {_rootPath}\n{_queryDescription}　検索中: {VisibleResultCount:N0}ファイル / {_visibleMatchCount:N0}ヒット（絞込・出力は完了後）" + _backendDescription;
        UpdatePosition();
        _list.Invalidate();
    }

    internal void Complete(UnifiedFilterFindSearchResult snapshot)
    {
        if (snapshot.Mode != _mode) throw new ArgumentException("Search result mode does not match this view.", nameof(snapshot));
        SelectionIdentity? selection = CaptureSelection();
        _snapshot = snapshot;
        _completed = true;
        _filterClearSelection = null;
        _lastFilter = string.Empty;
        _visibleNames = snapshot.NameResults;
        _filteredMatches = [];
        _visibleMatchCount = snapshot.MatchCount;
        _visibleFiles = IsContent
            ? snapshot.Files.Select(file => new VisibleContentFile(file, 0, 0, UsesOriginalMatches: true)).ToArray()
            : [];
        _filterInput.Enabled = true;
        _filterPanel.Visible = true;
        _filterCount.Text = string.Empty;
        _filterCount.Visible = false;
        _header.Text = $"対象: {_rootPath}\n{_queryDescription}　" + (IsContent
            ? $"ファイル: {snapshot.FileCount:N0}件　ヒット: {snapshot.MatchCount:N0}件　スキップ: {snapshot.SkippedCount:N0}件"
            : $"結果: {snapshot.ResultCount:N0}件　スキップ: {snapshot.SkippedCount:N0}件") + _backendDescription;
        _status.Text = snapshot.ResultCount == 0
            ? IsContent ? "該当する内容はありません。" : "該当する項目はありません。"
            : string.Empty;
        _status.Visible = snapshot.ResultCount == 0;
        _progressText.Text = "検索が完了しました。";
        _currentPathText.Text = string.Empty;
        _progressRing.SetRunning(false);
        _list.VirtualListSize = VisibleResultCount;
        _selected = -1;
        _hitIndex = 0;
        _position.Visible = true;
        int preserved = FindVisibleSelection(selection, out int preservedHit);
        if (VisibleResultCount > 0) SelectResult(preserved >= 0 ? preserved : 0, false, preservedHit);
        _streamedMatches.Clear();
        _streamedFiles.Clear();
        _streamedIdentities.Clear();
        UpdateFilterCount();
        UpdatePosition();
        LayoutColumnsToClientWidth();
        ApplyCurrentAppearance();
    }

    internal void CompleteCanceled()
    {
        _completed = true;
        _progressText.Text = "検索をキャンセルしました。";
        _currentPathText.Text = string.Empty;
        _progressRing.SetRunning(false);
    }

    internal void CompleteFailed(string message)
    {
        _completed = true;
        _progressText.Text = $"検索に失敗しました: {message}";
        _currentPathText.Text = string.Empty;
        _progressRing.SetRunning(false);
    }

    internal ListViewItem CreateRow(int index)
    {
        if (index < 0 || index >= VisibleResultCount) return new ListViewItem(string.Empty);
        MaterializedRowCount++;
        string path = IsContent ? _visibleFiles[index].Source.FullPath : _visibleNames[index].FullPath;
        string display = Path.GetRelativePath(_rootPath, path);
        if (!IsContent) return new ListViewItem([display, _visibleNames[index].IsDirectory ? "フォルダ" : "ファイル"]);
        VisibleContentFile file = _visibleFiles[index];
        UnifiedSearchMatch match = GetVisibleHit(index, index == _selected ? _hitIndex : 0);
        string text = match.LineText.Length > 512 ? match.LineText[..512] + "…" : match.LineText;
        return new ListViewItem([display, file.HitCount.ToString(), match.LineNumber.ToString(), match.ColumnNumber.ToString(), text]);
    }

    internal void SelectResult(int index)
        => SelectResult(index, userInitiated: false);

    private void SelectResult(int index, bool userInitiated, int requestedHitIndex = 0)
    {
        if (index < 0 || index >= VisibleResultCount) return;
        if (_selected != index)
        {
            _selected = index;
            _hitIndex = IsContent ? Math.Clamp(requestedHitIndex, 0, _visibleFiles[index].HitCount - 1) : 0;
        }
        _updatingSelection = true;
        try
        {
            if (!_list.SelectedIndices.Contains(index))
            {
                _list.SelectedIndices.Clear();
                _list.Items[index].Selected = true;
            }
            _list.Items[index].Focused = true;
            _list.EnsureVisible(index);
        }
        finally { _updatingSelection = false; }
        RefreshHit();
        if (userInitiated && _lastFilter.Length > 0) _filterClearSelection = CaptureSelection();
        UpdatePosition();
    }

    internal void ActivateSelected(bool inNewTab = false)
    {
        if ((!IsContent && (!_completed || _snapshot == null)) || (IsContent && _completed && _snapshot == null)
            || _selected < 0 || _selected >= VisibleResultCount) return;
        string path = IsContent ? _visibleFiles[_selected].Source.FullPath : _visibleNames[_selected].FullPath;
        bool directory = !IsContent && _visibleNames[_selected].IsDirectory;
        UnifiedSearchMatch? hit = IsContent ? GetVisibleHit(_selected, _hitIndex) : null;
        Func<string, bool, UnifiedSearchMatch?, string?> activate = inNewTab && _activateInNewTab != null
            ? _activateInNewTab
            : _activate;
        RunAction(() => activate(path, directory, hit));
    }

    internal void ExportResults()
    {
        if (_snapshot == null || _snapshot.ResultCount == 0) return;
        RunAction(() => _export(_snapshot));
    }

    internal bool HandleResultKey(Keys key)
    {
        if (_filterInput.Focused)
        {
            if (key == Keys.Escape)
            {
                if (_filterInput.Text.Length > 0) _filterInput.Clear();
                FocusResultList();
                return true;
            }
            if (key == Keys.Enter)
            {
                FocusResultList();
                if (_selected < 0 && VisibleResultCount > 0) SelectResult(0);
                return true;
            }
            return false;
        }
        if (key == Keys.Escape) { CloseRequested?.Invoke(this, EventArgs.Empty); return true; }
        if (key == Keys.Enter) { ActivateSelected(); return true; }
        if (key == Keys.E) { ExportResults(); return true; }
        if (VisibleResultCount == 0) return key is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown;
        if (IsContent && _selected >= 0 && key is Keys.Left or Keys.Right)
        {
            _hitIndex = Math.Clamp(_hitIndex + (key == Keys.Left ? -1 : 1), 0, _visibleFiles[_selected].HitCount - 1);
            if (_lastFilter.Length > 0) _filterClearSelection = CaptureSelection();
            RefreshHit();
            UpdatePosition();
            return true;
        }
        int page = Math.Max(1, (_list.ClientSize.Height - 25) / Math.Max(1, _list.Font.Height + 4));
        int index = key switch
        {
            Keys.Up => _selected - 1, Keys.Down => _selected + 1,
            Keys.PageUp => _selected - page, Keys.PageDown => _selected + page,
            Keys.Home => 0, Keys.End => VisibleResultCount - 1, _ => int.MinValue
        };
        if (index == int.MinValue) return false;
        if (VisibleResultCount > 0) SelectResult(Math.Clamp(index, 0, VisibleResultCount - 1), userInitiated: true);
        return true;
    }

    internal bool FocusResultFilter()
    {
        if (!_completed || !_filterInput.Enabled || !_filterInput.CanFocus) return false;
        return _filterInput.Focus();
    }

    internal void FocusResultList()
    {
        if (_list.CanFocus) _list.Focus();
    }

    internal void SetResultFilter(string value)
    {
        if (_filterInput.Text != value) _filterInput.Text = value;
    }

    private void ApplyResultFilterFromInput()
    {
        if (!_completed || _snapshot == null || string.Equals(_lastFilter, _filterInput.Text, StringComparison.Ordinal)) return;
        string filter = _filterInput.Text;
        SelectionIdentity? currentSelection = CaptureSelection();
        bool clearing = filter.Length == 0 && _lastFilter.Length > 0;
        if (_lastFilter.Length == 0 && filter.Length > 0) _filterClearSelection = currentSelection;
        SelectionIdentity? desiredSelection = clearing ? _filterClearSelection ?? currentSelection : currentSelection;
        _lastFilter = filter;
        RebuildVisibleProjection(filter);

        _updatingProjection = true;
        try
        {
            _list.SelectedIndices.Clear();
            _selected = -1;
            _hitIndex = 0;
            _list.VirtualListSize = VisibleResultCount;
            int selected = FindVisibleSelection(desiredSelection, out int hitIndex);
            if (selected >= 0) SelectResult(selected, userInitiated: false, requestedHitIndex: hitIndex);
            else if (VisibleResultCount > 0) SelectResult(0);
        }
        finally { _updatingProjection = false; }

        if (clearing) _filterClearSelection = null;
        UpdateFilterCount();
        UpdatePosition();
        _list.Invalidate();
    }

    private void RebuildVisibleProjection(string filter)
    {
        ProjectionVisitedFileCount = 0;
        ProjectionVisitedMatchCount = 0;
        _visibleMatchCount = 0;
        _filteredMatches = [];
        if (!IsContent)
        {
            if (filter.Length == 0)
            {
                _visibleNames = _snapshot!.NameResults;
                return;
            }
            var visibleNames = new List<UnifiedSearchNameResult>();
            foreach (UnifiedSearchNameResult item in _snapshot!.NameResults)
            {
                ProjectionVisitedFileCount++;
                if (PathMatchesFilter(item.FullPath, filter)) visibleNames.Add(item);
            }
            _visibleNames = visibleNames.ToArray();
            return;
        }

        if (filter.Length == 0)
        {
            _visibleFiles = _snapshot!.Files
                .Select(file => new VisibleContentFile(file, 0, 0, UsesOriginalMatches: true))
                .ToArray();
            _visibleMatchCount = _snapshot.MatchCount;
            return;
        }

        var visibleFiles = new List<VisibleContentFile>();
        var filteredMatches = new List<UnifiedSearchMatch>();
        foreach (UnifiedSearchResultFile file in _snapshot!.Files)
        {
            ProjectionVisitedFileCount++;
            if (PathMatchesFilter(file.FullPath, filter))
            {
                visibleFiles.Add(new VisibleContentFile(file, 0, 0, UsesOriginalMatches: true));
                _visibleMatchCount += file.Matches.Count;
                continue;
            }

            int firstFilteredHit = filteredMatches.Count;
            foreach (UnifiedSearchMatch match in file.Matches)
            {
                ProjectionVisitedMatchCount++;
                if (match.LineText.Contains(filter, StringComparison.OrdinalIgnoreCase)) filteredMatches.Add(match);
            }
            int filteredHitCount = filteredMatches.Count - firstFilteredHit;
            if (filteredHitCount == 0) continue;
            visibleFiles.Add(new VisibleContentFile(file, firstFilteredHit, filteredHitCount, UsesOriginalMatches: false));
            _visibleMatchCount += filteredHitCount;
        }
        _visibleFiles = visibleFiles.ToArray();
        _filteredMatches = filteredMatches.ToArray();
    }

    private bool PathMatchesFilter(string fullPath, string filter)
    {
        string relativePath = Path.GetRelativePath(_rootPath, fullPath);
        return relativePath.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(fullPath).Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private UnifiedSearchMatch GetVisibleHit(int fileIndex, int hitIndex)
    {
        VisibleContentFile file = _visibleFiles[fileIndex];
        return file.UsesOriginalMatches
            ? file.Source.Matches[hitIndex]
            : _filteredMatches[file.FirstFilteredHit + hitIndex];
    }

    private SelectionIdentity? CaptureSelection()
    {
        if (_selected < 0 || _selected >= VisibleResultCount) return null;
        return IsContent
            ? new SelectionIdentity(_visibleFiles[_selected].Source.FullPath, GetVisibleHit(_selected, _hitIndex))
            : new SelectionIdentity(_visibleNames[_selected].FullPath, null);
    }

    private int FindVisibleSelection(SelectionIdentity? identity, out int hitIndex)
    {
        hitIndex = 0;
        if (identity == null) return -1;
        if (!IsContent)
        {
            for (int index = 0; index < _visibleNames.Count; index++)
                if (string.Equals(_visibleNames[index].FullPath, identity.FullPath, StringComparison.OrdinalIgnoreCase)) return index;
            return -1;
        }

        for (int index = 0; index < _visibleFiles.Length; index++)
        {
            VisibleContentFile file = _visibleFiles[index];
            if (!string.Equals(file.Source.FullPath, identity.FullPath, StringComparison.OrdinalIgnoreCase)) continue;
            if (identity.Hit != null)
            {
                for (int currentHit = 0; currentHit < file.HitCount; currentHit++)
                {
                    if (GetVisibleHit(index, currentHit).Equals(identity.Hit))
                    {
                        hitIndex = currentHit;
                        return index;
                    }
                }
            }
            return index;
        }
        return -1;
    }

    private void UpdateFilterCount()
    {
        bool filtering = _lastFilter.Length > 0 && _snapshot != null;
        _filterCount.Visible = filtering;
        if (!filtering)
        {
            _filterCount.Text = string.Empty;
            return;
        }
        _filterCount.Text = IsContent
            ? $"表示: {_visibleFiles.Length:N0} / {_snapshot!.FileCount:N0}ファイル　{_visibleMatchCount:N0} / {_snapshot.MatchCount:N0}ヒット"
            : $"表示: {_visibleNames.Count:N0} / {_snapshot!.ResultCount:N0}件";
    }

    private void UpdatePosition()
    {
        if (VisibleResultCount == 0 && _snapshot == null) return;
        string position = $"{(_selected >= 0 ? _selected + 1 : 0):N0} / {VisibleResultCount:N0}";
        if (IsContent && _selected >= 0)
            position += $" Hit {_hitIndex + 1:N0} / {_visibleFiles[_selected].HitCount:N0}";
        _position.Text = position;
    }

    internal void SetActionStatus(string message)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(new Action<string>(SetActionStatus), message); return; }
        _status.Text = message;
        _status.Visible = !string.IsNullOrWhiteSpace(message);
    }

    private void RefreshHit()
    {
        _list.Invalidate();
        if (_selected >= 0 && _list.VirtualListSize > _selected) _list.RedrawItems(_selected, _selected, false);
    }

    private void LayoutColumnsToClientWidth()
    {
        if (_list.Columns.Count == 0 || _list.ClientSize.Width <= 0) return;
        int available = _list.ClientSize.Width;
        if (!IsContent)
        {
            const int kindWidth = 72;
            _list.Columns[0].Width = Math.Max(1, available - kindWidth);
            _list.Columns[1].Width = Math.Min(kindWidth, Math.Max(1, available - 1));
            return;
        }
        if (available < _list.Columns.Count)
        {
            for (int i = 0; i < _list.Columns.Count; i++) _list.Columns[i].Width = i < available ? 1 : 0;
            return;
        }
        int requiredHitWidth = Math.Max(58,
            TextRenderer.MeasureText(_list.Columns[1].Text, Font,
                new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width + 12);
        int hitWidth = Math.Min(requiredHitWidth, available - 4);
        int lineWidth = Math.Min(64, available - hitWidth - 3);
        int columnWidth = Math.Min(64, available - hitWidth - lineWidth - 2);
        int flexible = available - hitWidth - lineWidth - columnWidth;
        int fileWidth = flexible >= 236 ? Math.Clamp((int)Math.Round(flexible * 0.43), 136, flexible - 100) : flexible / 2;
        _list.Columns[0].Width = fileWidth;
        _list.Columns[1].Width = hitWidth;
        _list.Columns[2].Width = lineWidth;
        _list.Columns[3].Width = columnWidth;
        _list.Columns[4].Width = flexible - fileWidth;
    }

    private void UpdateFontAwareLayout()
    {
        Size labelText = TextRenderer.MeasureText(_filterLabel.Text, _filterLabel.Font,
            new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        _filterLabel.Width = labelText.Width + _filterLabel.Padding.Horizontal;
        LayoutColumnsToClientWidth();
    }

    private void ApplyCurrentAppearance()
    {
        if (_appearanceFactory == null) return;
        UnifiedSearchResultAppearance appearance = _appearanceFactory();
        BackColor = appearance.Background;
        ForeColor = appearance.Foreground;
        Font = appearance.Font ?? SystemFonts.MessageBoxFont;
        _header.BackColor = appearance.HeaderBackground;
        _header.ForeColor = appearance.HeaderForeground;
        _progressPanel.BackColor = appearance.Background;
        _progressCountsRow.BackColor = appearance.Background;
        _progressText.BackColor = appearance.Background;
        _progressText.ForeColor = appearance.Foreground;
        _currentPathText.BackColor = appearance.Background;
        _currentPathText.ForeColor = appearance.Foreground;
        _progressRing.BackColor = appearance.Background;
        _progressRing.ForeColor = appearance.Foreground;
        _filterPanel.BackColor = appearance.Background;
        _filterLabel.BackColor = appearance.Background;
        _filterLabel.ForeColor = appearance.Foreground;
        _filterInput.BackColor = appearance.Background;
        _filterInput.ForeColor = appearance.Foreground;
        _filterCount.BackColor = appearance.Background;
        _filterCount.ForeColor = appearance.Foreground;
        _bottomPanel.BackColor = appearance.Background;
        _status.BackColor = appearance.Background;
        _status.ForeColor = appearance.Foreground;
        _position.BackColor = appearance.Background;
        _position.ForeColor = appearance.Foreground;
        _progressRing.Invalidate();
        _list.BackColor = appearance.Background;
        _list.ForeColor = appearance.Foreground;
        _list.Tag = appearance;
        UpdateFontAwareLayout();
        _list.Invalidate();
    }

    private void DrawColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        UnifiedSearchResultAppearance appearance = _list.Tag as UnifiedSearchResultAppearance ?? UnifiedSearchResultAppearance.Default;
        using var background = new SolidBrush(appearance.HeaderBackground);
        using var foreground = new SolidBrush(appearance.HeaderForeground);
        using var border = new Pen(appearance.Border);
        e.Graphics.FillRectangle(background, e.Bounds);
        e.Graphics.DrawString(e.Header?.Text ?? string.Empty, Font, foreground, Rectangle.Inflate(e.Bounds, -5, -2), StringFormat.GenericDefault);
        e.Graphics.DrawRectangle(border, e.Bounds.Left, e.Bounds.Top, Math.Max(0, e.Bounds.Width - 1), Math.Max(0, e.Bounds.Height - 1));
    }

    private void DrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        UnifiedSearchResultAppearance appearance = _list.Tag as UnifiedSearchResultAppearance ?? UnifiedSearchResultAppearance.Default;
        bool selected = e.Item?.Selected ?? false;
        Color background = selected ? appearance.SelectedBackground : appearance.Background;
        Color foreground = selected ? appearance.SelectedForeground :
            _snapshot != null && !IsContent && e.ItemIndex < _visibleNames.Count && _visibleNames[e.ItemIndex].IsDirectory
                ? appearance.DirectoryForeground : appearance.Foreground;
        using var backBrush = new SolidBrush(background);
        e.Graphics.FillRectangle(backBrush, e.Bounds);
        TextRenderer.DrawText(e.Graphics, e.SubItem?.Text ?? string.Empty, Font, Rectangle.Inflate(e.Bounds, -5, -1), foreground,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (e.ColumnIndex == _list.Columns.Count - 1 && selected && _list.Focused)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(e.Bounds, -1, -1), foreground, background);

        if (e.Bounds.Width > 0 && e.Bounds.Height > 0)
        {
            using var border = new Pen(appearance.Border);
            int right = e.Bounds.Right - 1;
            int bottom = e.Bounds.Bottom - 1;
            e.Graphics.DrawLine(border, e.Bounds.Left, bottom, right, bottom);
            e.Graphics.DrawLine(border, right, e.Bounds.Top, right, bottom);
        }
    }

    private void RunAction(Func<string?> action)
    {
        try { string? message = action(); if (!string.IsNullOrWhiteSpace(message)) SetActionStatus(message); }
        catch (Exception ex) { SetActionStatus($"失敗しました: {ex.Message}"); }
    }

    private sealed class IndeterminateProgressRing : Control
    {
        private const int SpokeCount = 8;
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 90 };
        private bool _isRunning;
        private bool _surfaceActive;
        private bool _timerDisposed;
        private int _phase;
        private int _animationTickCount;

        internal bool IsRunning => _isRunning;
        internal bool SurfaceActive => _surfaceActive;
        internal bool AnimationEnabled => !_timerDisposed && _timer.Enabled;
        internal int InvalidationCount { get; private set; }
        internal int AnimationTickCount => _animationTickCount;

        internal IndeterminateProgressRing()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.Opaque, true);
            Size = new Size(20, 20);
            Margin = new Padding(2, 1, 2, 1);
            TabStop = false;
            _timer.Tick += Timer_Tick;
        }

        internal void SetRunning(bool value)
        {
            _isRunning = value;
            Visible = value;
            if (!value) _phase = 0;
            UpdateTimerState();
            Invalidate();
        }

        internal void SetSurfaceActive(bool value)
        {
            _surfaceActive = value;
            UpdateTimerState();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UpdateTimerState();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            UpdateTimerState();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            UpdateTimerState();
        }

        protected override void OnInvalidated(InvalidateEventArgs e)
        {
            InvalidationCount++;
            base.OnInvalidated(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using var background = new SolidBrush(BackColor);
            e.Graphics.FillRectangle(background, ClientRectangle);
            if (!_isRunning) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float centerX = ClientSize.Width / 2f;
            float centerY = ClientSize.Height / 2f;
            for (int spoke = 0; spoke < SpokeCount; spoke++)
            {
                int age = (spoke - _phase + SpokeCount) % SpokeCount;
                int alpha = 52 + (SpokeCount - age) * 24;
                double angle = (spoke * 360.0 / SpokeCount - 90) * Math.PI / 180.0;
                float inner = Math.Min(ClientSize.Width, ClientSize.Height) * 0.20f;
                float outer = Math.Min(ClientSize.Width, ClientSize.Height) * 0.40f;
                var color = Color.FromArgb(Math.Min(255, alpha), ForeColor.R, ForeColor.G, ForeColor.B);
                using var pen = new Pen(color, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                e.Graphics.DrawLine(pen,
                    centerX + (float)Math.Cos(angle) * inner,
                    centerY + (float)Math.Sin(angle) * inner,
                    centerX + (float)Math.Cos(angle) * outer,
                    centerY + (float)Math.Sin(angle) * outer);
            }
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (!_isRunning || !_surfaceActive || IsDisposed) return;
            _phase = (_phase + 1) % SpokeCount;
            _animationTickCount++;
            Invalidate();
        }

        private void UpdateTimerState()
        {
            if (_timerDisposed || IsDisposed) return;
            bool shouldRun = _isRunning && _surfaceActive && Visible && Enabled && IsHandleCreated;
            if (_timer.Enabled != shouldRun) _timer.Enabled = shouldRun;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_timerDisposed)
            {
                _timerDisposed = true;
                _isRunning = false;
                _surfaceActive = false;
                _timer.Tick -= Timer_Tick;
                _timer.Stop();
                _timer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

public sealed record UnifiedSearchResultAppearance(
    Color Background, Color Foreground, Color DirectoryForeground, Color SelectedBackground,
    Color SelectedForeground, Color HeaderBackground, Color HeaderForeground, Color Border, Font? Font)
{
    public static UnifiedSearchResultAppearance Default { get; } = new(
        SystemColors.Window, SystemColors.WindowText, SystemColors.WindowText,
        SystemColors.Highlight, SystemColors.HighlightText, SystemColors.Control,
        SystemColors.ControlText, SystemColors.ActiveBorder, SystemFonts.MessageBoxFont);
}
