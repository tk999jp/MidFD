using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MidFD.Configuration;
using MidFD.Helpers;
using MidFD.Models;
using MidFD.Services;

namespace MidFD.Runtime;

internal enum ViewerOpenRoute
{
    ExecuteTarget,
    Archive,
    MediaViewer,
    InternalViewer
}

internal readonly record struct ViewerOpenRequest(
    string FullPath,
    ViewerOpenRoute Route,
    PreviewKind ViewerKind = PreviewKind.None);

internal readonly record struct ViewerEnterDecision(
    ViewerOpenRequest? OpenRequest,
    bool UseExternalMediaPlayback,
    bool IsAudio);

internal readonly record struct ViewerCtrlEnterDecision(
    bool Handled,
    ViewerOpenRequest? OpenRequest,
    bool UseExternalMediaPlayback,
    bool IsAudio);

internal readonly record struct ViewerModeLifecyclePlan(
    PreviewKind NextViewerKind,
    bool ShouldClearPreview,
    string ClearMessage,
    bool ShouldRefreshPreview);

internal enum ViewerInputKey
{
    Home,
    End,
    PageUp,
    PageDown
}

internal readonly record struct ViewerNavigationDecision(
    bool Handled,
    bool PendingEndAfterIndex,
    int TargetFirstVisibleLine);

internal readonly record struct ViewerTextSearchResult(
    int Index,
    bool Wrapped)
{
    public bool Found => Index >= 0;
}

internal readonly record struct LargeFileSearchResult(
    int RequestId,
    int Line,
    int Column,
    int Length,
    bool Wrapped)
{
    public bool Found => Line >= 0;
}

/// <summary>
/// Viewer/Preview の意味判断と状態遷移を所有する。
/// WinForms control、MainForm、Dialog、Shell を参照せず、具体的な表示反映は呼び出し側へ返す。
/// </summary>
internal sealed class ViewerWorkflowApplicationCoordinator
{
    private static readonly string[] ExecuteTargetExtensions = [".exe", ".com", ".lnk"];

    private readonly ViewerSessionState _state;
    private readonly SettingsApplicationCoordinator _settings;
    private readonly ILargeFileTextSearch _largeFileTextSearch;

    public ViewerWorkflowApplicationCoordinator(
        ViewerSessionState state,
        SettingsApplicationCoordinator settings,
        ILargeFileTextSearch? largeFileTextSearch = null)
    {
        _state = state;
        _settings = settings;
        _largeFileTextSearch = largeFileTextSearch ?? new LargeFileTextSearch();
    }

    public PreviewKind ResolveEffectivePreviewKind(string path, PreviewKind rawKind)
        => PreviewRoutingService.Route(
            path,
            rawKind,
            _settings.Value.Preview?.VideoToolDirectory).EffectiveKind;

    public PreviewKind ResolveEffectivePreviewKind(string path)
        => PreviewRoutingService.Route(
            path,
            _settings.Value.Preview?.VideoToolDirectory).EffectiveKind;

    public PreviewKind ResolveSelectionPreviewKind(string? fullPath)
    {
        if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
        {
            return PreviewKind.None;
        }

        return ResolveEffectivePreviewKind(fullPath);
    }

    public ViewerModeLifecyclePlan CreateModeLifecyclePlan(
        bool isBrowserMode,
        PreviewKind currentViewerKind,
        PreviewKind currentSelectionKind)
    {
        if (isBrowserMode)
        {
            return new ViewerModeLifecyclePlan(PreviewKind.None, false, string.Empty, false);
        }

        return new ViewerModeLifecyclePlan(
            currentViewerKind == PreviewKind.None ? currentSelectionKind : currentViewerKind,
            true,
            "読み込み中...",
            true);
    }

    public ViewerEncodingPreference CycleEncodingPreference()
    {
        ViewerEncodingPreference next = _state.EncodingPreference switch
        {
            ViewerEncodingPreference.Auto => ViewerEncodingPreference.Utf8,
            ViewerEncodingPreference.Utf8 => ViewerEncodingPreference.ShiftJis,
            _ => ViewerEncodingPreference.Auto
        };
        _state.EncodingPreference = next;
        return next;
    }

    public Encoding ResolveCurrentViewerEncoding()
    {
        if (_state.CurrentKind == PreviewKind.LargeText
            && _state.LargeFileState?.DetectedEncoding != null)
        {
            return _state.LargeFileState.DetectedEncoding;
        }
        if (_state.EncodingPreference == ViewerEncodingPreference.Utf8)
        {
            return Encoding.UTF8;
        }
        if (_state.EncodingPreference == ViewerEncodingPreference.ShiftJis)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding("shift_jis");
        }
        return Encoding.UTF8;
    }

    public void ResetEncodingPreference()
        => _state.EncodingPreference = ViewerEncodingPreference.Auto;

    public void SetSearchKeyword(string query)
        => _state.SearchKeyword = query ?? string.Empty;

    public void UpdateTextLineCount(int lineCount)
        => _state.TextLineCount = Math.Max(1, lineCount);

    public void ClearPreviewState()
        => _state.DetectedEncodingLabel = string.Empty;

    public void ClearCurrentPreviewTarget()
        => _state.CurrentPreviewTarget = null;

    public void SetLargeFilePendingEndAfterIndex(bool pending)
    {
        if (_state.LargeFileState != null)
        {
            _state.LargeFileState.PendingEndAfterIndex = pending;
        }
    }

    public bool ConsumeLargeFilePendingEndAfterIndex()
    {
        if (_state.LargeFileState == null || !_state.LargeFileState.PendingEndAfterIndex)
        {
            return false;
        }

        _state.LargeFileState.PendingEndAfterIndex = false;
        return true;
    }

    public void SetLargeFileFirstVisibleLine(int line)
    {
        if (_state.LargeFileState != null)
        {
            _state.LargeFileState.FirstVisibleLine = Math.Max(0, line);
        }
    }

    public void CompleteLargeFileIndex(
        LargeFilePreviewState state,
        IReadOnlyList<long> lineOffsets,
        long totalBytes,
        int visibleLineCount)
    {
        if (!ReferenceEquals(_state.LargeFileState, state))
        {
            return;
        }

        state.ReplaceLineOffsets(lineOffsets, totalBytes);
        state.IsIndexing = false;
        int maxFirstVisibleLine = Math.Max(0, state.TotalLines - Math.Max(1, visibleLineCount));
        state.FirstVisibleLine = Math.Min(state.FirstVisibleLine, maxFirstVisibleLine);
    }

    public void PrepareLargeFileSearch(
        LargeFilePreviewState state,
        string query,
        bool backward)
    {
        string normalized = query.Trim();
        _state.SearchKeyword = normalized;
        state.LastSearchText = normalized;
        state.LastSearchBackward = backward;
    }

    public void ClearLargeFileSearchState(LargeFilePreviewState state)
    {
        state.LastSearchText = string.Empty;
        state.LastSearchBackward = false;
        state.ActiveSearchHitLine = null;
        state.ActiveSearchHitColumn = 0;
        state.ActiveSearchHitLength = 0;
    }

    public void SetLargeFileSearchHit(LargeFilePreviewState state, int line, int column, int length)
    {
        state.ActiveSearchHitLine = line;
        state.ActiveSearchHitColumn = column;
        state.ActiveSearchHitLength = length;
    }

    public void ClearLargeFileSearchHit(LargeFilePreviewState state)
    {
        state.ActiveSearchHitLine = null;
        state.ActiveSearchHitColumn = 0;
        state.ActiveSearchHitLength = 0;
    }

    public int InvalidateLargeFileSearchRequest(LargeFilePreviewState state)
        => ++state.SearchRequestId;

    public bool ToggleWordWrap(bool save = true)
    {
        bool next = !_settings.Value.Preview.ViewerWordWrap;
        _settings.SetViewerWordWrap(next);
        if (save)
        {
            _settings.Save();
        }
        return next;
    }

    public bool SetMarkdownViewerMode(MarkdownViewerMode mode, bool save)
    {
        bool changed = _settings.Value.Preview.MarkdownViewerMode != mode;
        _settings.SetMarkdownViewerMode(mode);
        if (save && changed)
        {
            _settings.Save();
        }
        return changed;
    }

    public bool IsMarkdownViewerRawMode
        => _settings.Value.Preview?.MarkdownViewerMode == MarkdownViewerMode.Raw;

    public ViewerOpenRequest? CreateBrowserOpenRequest(string? fullPath, bool allowExecuteTarget)
    {
        if (string.IsNullOrWhiteSpace(fullPath)
            || Directory.Exists(fullPath)
            || !File.Exists(fullPath))
        {
            return null;
        }

        if (allowExecuteTarget
            && Array.Exists(ExecuteTargetExtensions, extension =>
                string.Equals(extension, Path.GetExtension(fullPath), StringComparison.OrdinalIgnoreCase)))
        {
            return new ViewerOpenRequest(fullPath, ViewerOpenRoute.ExecuteTarget);
        }

        if (ArchiveFileTypeHelper.IsArchive(fullPath))
        {
            return new ViewerOpenRequest(fullPath, ViewerOpenRoute.Archive);
        }

        PreviewKind kind = ResolveEffectivePreviewKind(fullPath);
        return new ViewerOpenRequest(fullPath, kind is PreviewKind.Image or PreviewKind.Video
            ? ViewerOpenRoute.MediaViewer
            : ViewerOpenRoute.InternalViewer, kind);
    }

    public ViewerEnterDecision ResolveEnter(string fullPath)
    {
        PreviewKind rawKind = PreviewService.GetPreviewKind(fullPath);
        if (rawKind == PreviewKind.Video)
        {
            bool isAudio = PreviewService.IsSupportedAudioExtension(fullPath);
            if (isAudio || _settings.Value.Preview?.VideoEnterPlaysExternal == true)
            {
                return new ViewerEnterDecision(null, true, isAudio);
            }
        }

        return new ViewerEnterDecision(CreateBrowserOpenRequest(fullPath, allowExecuteTarget: true), false, false);
    }

    public ViewerCtrlEnterDecision ResolveCtrlEnter(string fullPath)
    {
        if (PreviewService.GetPreviewKind(fullPath) != PreviewKind.Video)
        {
            return new ViewerCtrlEnterDecision(false, null, false, false);
        }

        bool isAudio = PreviewService.IsSupportedAudioExtension(fullPath);
        if (_settings.Value.Preview?.VideoEnterPlaysExternal == true && !isAudio)
        {
            return new ViewerCtrlEnterDecision(
                true,
                CreateBrowserOpenRequest(fullPath, allowExecuteTarget: true),
                false,
                false);
        }

        return new ViewerCtrlEnterDecision(true, null, true, isAudio);
    }

    public PreviewKind ResolvePreviewLaunchKind(string fullPath)
    {
        PreviewKind rawKind = PreviewService.GetContentPreviewKind(fullPath, out _);
        return rawKind == PreviewKind.None
            ? PreviewKind.None
            : ResolveEffectivePreviewKind(fullPath, rawKind);
    }

    public ViewerNavigationDecision ResolveLargeFileNavigation(
        LargeFilePreviewState state,
        ViewerInputKey key,
        int visibleLineCount,
        int maxFirstVisibleLine)
    {
        int oldLine = state.FirstVisibleLine;
        if (key == ViewerInputKey.End && state.IsIndexing)
        {
            state.PendingEndAfterIndex = true;
            return new ViewerNavigationDecision(true, true, oldLine);
        }

        int target = key switch
        {
            ViewerInputKey.Home => 0,
            ViewerInputKey.End => maxFirstVisibleLine,
            ViewerInputKey.PageUp => oldLine - visibleLineCount,
            ViewerInputKey.PageDown => oldLine + visibleLineCount,
            _ => oldLine
        };
        target = Math.Max(0, target);
        return new ViewerNavigationDecision(
            target != oldLine || key is ViewerInputKey.Home or ViewerInputKey.End,
            false,
            target);
    }

    public ViewerTextSearchResult FindText(
        string text,
        string query,
        int start,
        bool backward,
        bool allowWrap)
    {
        if (string.IsNullOrEmpty(query))
        {
            return new ViewerTextSearchResult(-1, false);
        }

        int normalizedStart = Math.Clamp(start, 0, text.Length);
        int result = backward
            ? normalizedStart > 0
                ? text.LastIndexOf(query, normalizedStart - 1, StringComparison.CurrentCultureIgnoreCase)
                : -1
            : text.IndexOf(query, normalizedStart, StringComparison.CurrentCultureIgnoreCase);
        if (result >= 0 || !allowWrap)
        {
            return new ViewerTextSearchResult(result, false);
        }

        result = backward
            ? text.Length > 0
                ? text.LastIndexOf(query, text.Length - 1, StringComparison.CurrentCultureIgnoreCase)
                : -1
            : text.IndexOf(query, 0, StringComparison.CurrentCultureIgnoreCase);
        return new ViewerTextSearchResult(result, result >= 0);
    }

    public async Task<LargeFileSearchResult> SearchLargeFileAsync(
        LargeFilePreviewState state,
        string query,
        bool backward,
        bool isWrapAround,
        Encoding encoding,
        CancellationToken token)
    {
        string normalizedQuery = query.Trim();
        int requestId = ++state.SearchRequestId;
        state.LastSearchText = normalizedQuery;
        state.LastSearchBackward = backward;
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            return new LargeFileSearchResult(requestId, -1, 0, 0, isWrapAround);
        }

        return await SearchLargeFilePassAsync(
            state,
            normalizedQuery,
            backward,
            requestId,
            isWrapAround,
            encoding,
            token).ConfigureAwait(true);
    }

    private async Task<LargeFileSearchResult> SearchLargeFilePassAsync(
        LargeFilePreviewState state,
        string query,
        bool backward,
        int requestId,
        bool isWrapAround,
        Encoding encoding,
        CancellationToken token)
    {
        if (state.SearchRequestId != requestId)
        {
            return new LargeFileSearchResult(requestId, -1, 0, 0, isWrapAround);
        }

        (int startLine, int startColumn) = GetLargeFileSearchStartPosition(
            state,
            query,
            backward,
            isWrapAround);
        var hit = await _largeFileTextSearch.SearchAsync(
            state,
            query,
            startLine,
            startColumn,
            backward,
            encoding,
            token).ConfigureAwait(true);
        if (state.SearchRequestId != requestId)
        {
            return new LargeFileSearchResult(requestId, -1, 0, 0, isWrapAround);
        }

        if (hit.HasValue)
        {
            return new LargeFileSearchResult(
                requestId,
                hit.Value.Line,
                hit.Value.Column,
                hit.Value.Length,
                isWrapAround);
        }

        if (!isWrapAround)
        {
            return await SearchLargeFilePassAsync(
                state,
                query,
                backward,
                requestId,
                isWrapAround: true,
                encoding,
                token).ConfigureAwait(true);
        }

        return new LargeFileSearchResult(requestId, -1, 0, 0, true);
    }

    public bool IsLargeFileSearchRequestActive(LargeFilePreviewState state, int requestId)
        => ReferenceEquals(_state.LargeFileState, state)
            && state.SearchRequestId == requestId
            && _state.Mode == ViewerApplicationMode.Viewer
            && _state.CurrentKind == PreviewKind.LargeText
            && string.Equals(_state.CurrentPreviewTarget, state.FilePath, StringComparison.OrdinalIgnoreCase);

    public static bool IsPlainTextViewerKind(PreviewKind kind)
        => kind is PreviewKind.Text or PreviewKind.Markdown or PreviewKind.Sqlite or PreviewKind.Binary;

    public static bool IsBrowserAutoPreviewEligible(PreviewKind kind)
        => kind == PreviewKind.Image;

    public static string GetBrowserAutoPreviewSuppressedMessage(PreviewKind kind)
        => kind switch
        {
            PreviewKind.Text or PreviewKind.Markdown or PreviewKind.Sqlite or PreviewKind.Binary
                => "自動プレビューなし\nV / Enter で開きます。",
            PreviewKind.Video => "動画は自動プレビュー対象外です。",
            _ => "プレビュー対象外"
        };

    public static string? ResolveViewerClickedUrl(PreviewKind kind, string? linkText)
        => kind == PreviewKind.Markdown
            ? MarkdownPreviewService.ResolveClickedUrl(linkText)
            : linkText;

    private static (int StartLine, int StartColumn) GetLargeFileSearchStartPosition(
        LargeFilePreviewState state,
        string query,
        bool backward,
        bool isWrapAround)
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
            return backward
                ? (state.ActiveSearchHitLine.Value, Math.Max(-1, state.ActiveSearchHitColumn - 1))
                : (state.ActiveSearchHitLine.Value, state.ActiveSearchHitColumn + Math.Max(1, state.ActiveSearchHitLength));
        }
        return backward
            ? (Math.Max(0, state.FirstVisibleLine), int.MaxValue)
            : (Math.Max(0, state.FirstVisibleLine), 0);
    }
}
