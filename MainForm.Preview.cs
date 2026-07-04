using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using MidFD.Configuration;
using MidFD.Dialogs;
using MidFD.Helpers;
using MidFD.Models;
using MidFD.Services;

namespace MidFD;

public partial class MainForm
{    private PreviewKind GetCurrentSelectionPreviewKind()
    {
        var item = GetCurrentBrowserItem();
        string? fullPath = item?.Tag as string;
        if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
        {
            return PreviewKind.None;
        }
        return GetEffectivePreviewKind(fullPath);
    }
    private PreviewKind GetEffectivePreviewKind(string path, PreviewKind rawKind)
    {
        var result = PreviewRoutingService.Route(path, rawKind, _settings.Preview?.VideoToolDirectory);
        return result.EffectiveKind;
    }
    private PreviewKind GetEffectivePreviewKind(string path)
    {
        var result = PreviewRoutingService.Route(path, _settings.Preview?.VideoToolDirectory);
        return result.EffectiveKind;
    }
    private void ApplyViewerChromeState()
    {
        bool compactViewer = _uiMode == UIMode.Viewer
            && (_currentViewerKind == PreviewKind.Text
                || _currentViewerKind == PreviewKind.Markdown
                || _currentViewerKind == PreviewKind.Sqlite
                || _currentViewerKind == PreviewKind.Binary
                || _currentViewerKind == PreviewKind.LargeText);
        Presentation.PreviewUiPresenter.ApplyViewerChromeState(
            compactViewer,
            _uiMode == UIMode.Viewer && _currentViewerKind == PreviewKind.LargeText,
            titleHeaderPanel,
            headerPanel,
            sepBeforeTopPanel,
            topPanel,
            _largeFileControl);
        ApplyFunctionBarVisibilityForCurrentContext();
    }
    private void ExecuteViewerFind()
    {
        if (_currentViewerKind == PreviewKind.LargeText && _largeFileState != null)
        {
            ExecuteLargeFileFind();
            return;
        }
        if (!viewerTextBox.Visible) return;
        string? query = SimpleInputDialog.ShowNullable("検索:", "Viewer 検索 (Ctrl+F)", _viewerSearchKeyword);
        if (query == null) return; // キャンセル時は現状維持
        _viewerSearchKeyword = query;
        ApplyViewerStatusLine(); // ステータスに反映
        if (string.IsNullOrWhiteSpace(query))
        {
            ShowStatusMessage("検索キーワードをクリアしました。");
            return;
        }
        // 初回検索: 現在位置の次から前方へ
        int start = viewerTextBox.SelectionStart + viewerTextBox.SelectionLength;
        _ = InnerExecuteViewerSearch(query, start, backward: false);
    }
    private void ExecuteViewerFindNext(bool backward)
    {
        if (_currentViewerKind == PreviewKind.LargeText && _largeFileState != null)
        {
            ExecuteLargeFileFindNext(backward);
            return;
        }
        if (!viewerTextBox.Visible) return;
        if (string.IsNullOrWhiteSpace(_viewerSearchKeyword))
        {
            ShowStatusMessage("検索キーワードが未設定です。新規検索ダイアログを開きます...");
            ExecuteViewerFind();
            return;
        }
        int start;
        if (backward)
        {
            // 前方向: 現在の選択開始位置より前から探す
            start = viewerTextBox.SelectionStart;
        }
        else
        {
            // 次方向: 現在の選択終了位置から探す
            start = viewerTextBox.SelectionStart + viewerTextBox.SelectionLength;
        }
        _ = InnerExecuteViewerSearch(_viewerSearchKeyword, start, backward);
    }
    private async Task InnerExecuteViewerSearch(string query, int start, bool backward, bool isWrapAround = false, int chunkCrossoverCount = 0)
    {
        if (_currentViewerKind == PreviewKind.LargeText && _largeFileState != null)
        {
            await ExecuteLargeFileSearchAsync(query, backward, isWrapAround);
            return;
        }
        RichTextBoxFinds options = backward ? RichTextBoxFinds.Reverse : RichTextBoxFinds.None;
        int result = viewerTextBox.Find(query, start, options);
        if (result < 0 && !isWrapAround)
        {
            if (backward)
            {
                result = viewerTextBox.Find(query, viewerTextBox.TextLength, options);
                if (result >= 0) ShowStatusMessage("末尾から再検索しました");
            }
            else
            {
                result = viewerTextBox.Find(query, 0, options);
                if (result >= 0) ShowStatusMessage("先頭から再検索しました");
            }
        }
        if (result >= 0)
        {
            viewerTextBox.Focus();
        }
        else
        {
            ShowStatusMessage($"一致する文字列が見つかりません: \"{query}\"");
        }
    }
    private async Task ExecuteLargeFileSearchAsync(string query, bool backward, bool isWrapAround)
    {
        if (_largeFileState == null) return;
        var state = _largeFileState;
        string normalizedQuery = query?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            ClearLargeFileSearchHit(state);
            ShowStatusMessage("検索キーワードが未設定です。");
            return;
        }
        int requestId = ++state.SearchRequestId;
        state.LastSearchText = normalizedQuery;
        state.LastSearchBackward = backward;
        _viewerSearchKeyword = normalizedQuery;
        ApplyViewerStatusLine();
        ShowStatusMessage($"検索中: {normalizedQuery}");
        var token = _previewRequestCoordinator.Token;
        var encoding = GetCurrentViewerEncoding();
        var (startLine, startColumn) = GetLargeFileSearchStartPosition(state, normalizedQuery, backward, isWrapAround);
        try
        {
            var hit = await Services.LargeFileLineReaderService.SearchTextAsync(
                state,
                normalizedQuery,
                startLine,
                startColumn,
                backward,
                encoding,
                token);
            if (!IsLargeFileSearchRequestActive(state, requestId))
            {
                return;
            }
            if (hit.HasValue)
            {
                await ApplyLargeFileSearchHitAsync(state, requestId, normalizedQuery, hit.Value.Line, hit.Value.Column, hit.Value.Length, backward, isWrapAround);
                return;
            }
            if (!isWrapAround)
            {
                ShowStatusMessage(backward ? "先頭まで検索しました。末尾から再検索します..." : "末尾まで検索しました。先頭から再検索します...");
                await ExecuteLargeFileSearchAsync(normalizedQuery, backward, true);
                return;
            }
            ClearLargeFileSearchHit(state);
            ShowStatusMessage($"一致する文字列が見つかりません: \"{normalizedQuery}\"");
        }
        catch (OperationCanceledException)
        {
        }
    }
    private void EnsureStatusBarVisible()
    {
        Presentation.PreviewUiPresenter.EnsureStatusBarVisible(statusStrip, statusLabel);
    }
    private void ExecuteLargeFileFind()
    {
        if (_largeFileState == null)
        {
            return;
        }
        string initialQuery = string.IsNullOrWhiteSpace(_largeFileState.LastSearchText)
            ? _viewerSearchKeyword
            : _largeFileState.LastSearchText;
        string? query = SimpleInputDialog.ShowNullable("検索:", "LargeText 検索 (Ctrl+F)", initialQuery);
        if (query == null)
        {
            return;
        }
        string normalizedQuery = query.Trim();
        bool continueFromActiveHit = !string.IsNullOrWhiteSpace(normalizedQuery)
            && string.Equals(_largeFileState.LastSearchText, normalizedQuery, StringComparison.OrdinalIgnoreCase)
            && _largeFileState.ActiveSearchHitLine.HasValue;
        _viewerSearchKeyword = normalizedQuery;
        _largeFileState.LastSearchText = normalizedQuery;
        ApplyViewerStatusLine();
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            ClearLargeFileSearchHit(_largeFileState);
            ShowStatusMessage("検索キーワードをクリアしました。");
            return;
        }
        if (!continueFromActiveHit)
        {
            _largeFileState.ActiveSearchHitLine = null;
            _largeFileState.ActiveSearchHitColumn = 0;
            _largeFileState.ActiveSearchHitLength = 0;
        }
        _ = ExecuteLargeFileSearchAsync(normalizedQuery, backward: false, isWrapAround: false);
    }
    private void ExecuteLargeFileFindNext(bool backward)
    {
        if (_largeFileState == null)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(_largeFileState.LastSearchText))
        {
            ShowStatusMessage("検索キーワードが未設定です。新規検索ダイアログを開きます...");
            ExecuteLargeFileFind();
            return;
        }
        _ = ExecuteLargeFileSearchAsync(_largeFileState.LastSearchText, backward, false);
    }
    private (int StartLine, int StartColumn) GetLargeFileSearchStartPosition(LargeFilePreviewState state, string query, bool backward, bool isWrapAround)
    {
        if (isWrapAround)
        {
            return backward
                ? (Math.Max(0, state.TotalLines - 1), int.MaxValue)
                : (0, 0);
        }
        if (state.ActiveSearchHitLine.HasValue
            && string.Equals(state.LastSearchText, query, StringComparison.OrdinalIgnoreCase))
        {
            if (backward)
            {
                return (
                    state.ActiveSearchHitLine.Value,
                    Math.Max(-1, state.ActiveSearchHitColumn - 1));
            }
            return (
                state.ActiveSearchHitLine.Value,
                state.ActiveSearchHitColumn + Math.Max(1, state.ActiveSearchHitLength));
        }
        return backward
            ? (Math.Max(0, state.FirstVisibleLine), int.MaxValue)
            : (Math.Max(0, state.FirstVisibleLine), 0);
    }
    private async Task ApplyLargeFileSearchHitAsync(
        LargeFilePreviewState state,
        int requestId,
        string query,
        int hitLine,
        int hitColumn,
        int hitLength,
        bool backward,
        bool isWrapAround)
    {
        if (!IsLargeFileSearchRequestActive(state, requestId))
        {
            return;
        }
        state.ActiveSearchHitLine = hitLine;
        state.ActiveSearchHitColumn = hitColumn;
        state.ActiveSearchHitLength = hitLength;
        _largeFileControl.SetActiveSearchHit(hitLine, hitColumn, hitLength);
        int targetFirstLine = Math.Max(0, hitLine - Math.Max(1, _largeFileControl.VisibleLineCount / 2));
        await NavigateLargeFilePreviewAsync(targetFirstLine, "SearchHit");
        if (!IsLargeFileSearchRequestActive(state, requestId))
        {
            return;
        }
        _largeFileControl.SetActiveSearchHit(hitLine, hitColumn, hitLength);
        ApplyViewerStatusLine();
        string wrapPrefix = isWrapAround
            ? (backward ? "末尾から再検索しました。 " : "先頭から再検索しました。 ")
            : string.Empty;
        ShowStatusMessage($"{wrapPrefix}{query}: {hitLine + 1:N0} 行目");
    }
    private bool IsLargeFileSearchRequestActive(LargeFilePreviewState state, int requestId)
    {
        return ReferenceEquals(_largeFileState, state)
            && state.SearchRequestId == requestId
            && _uiMode == UIMode.Viewer
            && _currentViewerKind == PreviewKind.LargeText
            && string.Equals(_currentPreviewTarget, state.FilePath, StringComparison.OrdinalIgnoreCase);
    }
    private void ClearLargeFileSearchHit(LargeFilePreviewState state)
    {
        state.ActiveSearchHitLine = null;
        state.ActiveSearchHitColumn = 0;
        state.ActiveSearchHitLength = 0;
        _largeFileControl.ClearActiveSearchHit();
        ApplyViewerStatusLine();
    }
    private void PositionPreviewPopup()
    {
        Presentation.PreviewUiPresenter.PositionPreviewPopup(this, _previewPopup);
    }
}
