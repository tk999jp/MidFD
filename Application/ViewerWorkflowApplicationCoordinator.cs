using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MidFD.Configuration;
using MidFD.Helpers;
using MidFD.Models;
using MidFD.Services;
using MidFD.ViewerIpc;

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
    PreviewKind ViewerKind = PreviewKind.None,
    ViewerPresentationMode? PresentationMode = null);

internal readonly record struct ViewerEnterDecision(
    ViewerOpenRequest? OpenRequest,
    bool UseExternalMediaPlayback,
    bool IsAudio);

internal readonly record struct ViewerAudioCtrlEnterDecision(bool Handled, bool UseExternalMediaPlayback);

internal enum BrowserCtrlEnterRoute
{
    None,
    OpenVideoPlaybackLocked,
    PlayAudioExternally
}

internal readonly record struct ViewerPresentationAcknowledgement(
    bool IsCurrent,
    bool Accepted,
    ViewerPresentationMode AppliedMode,
    int Generation,
    bool SynchronizeBrowserSelection);

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

internal readonly record struct TextViewerExternalEditorPreparation(
    string? TargetPath,
    string? ErrorMessage);

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
    private readonly Dictionary<Guid, MediaViewerPresentationState> _mediaViewerPresentation = new();

    private readonly record struct MediaViewerPresentationState(
        ViewerPresentationMode AppliedMode,
        ViewerPresentationMode DesiredMode,
        int Generation,
        int SelectionGeneration,
        bool IsPending,
        bool SynchronizeBrowserSelectionOnApply,
        long MediaRequestId,
        PreviewKind MediaKind,
        ViewerVideoPlaybackState PlaybackState,
        ViewerPresentationMode? ManualOverrideMode);

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
        => PreviewRoutingService.Route(path, rawKind).EffectiveKind;

    public PreviewKind ResolveEffectivePreviewKind(string path)
        => PreviewRoutingService.Route(path).EffectiveKind;

    /// <summary>
    /// Browserのmedia候補だけに、設定済み・検査済みSusie拡張子の分類を適用する。
    /// Vの内容判定や汎用Preview分類には適用しない。
    /// </summary>
    public PreviewKind ResolveBrowserSelectionPreviewKind(string? fullPath, PreviewKind shallowKind, bool isDirectory)
    {
        if (isDirectory
            || string.IsNullOrWhiteSpace(fullPath)
            || shallowKind != PreviewKind.None
            || IsNonMediaBrowserPath(fullPath))
        {
            return shallowKind;
        }

        return HasInspectedSusieImageCandidate(fullPath) ? PreviewKind.Image : shallowKind;
    }

    public PreviewKind ResolveSelectionPreviewKind(string? fullPath)
    {
        if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
        {
            return PreviewKind.None;
        }

        return ResolveEffectivePreviewKind(fullPath);
    }

    public TextViewerExternalEditorPreparation PrepareDisplayedTextExternalEditor(
        string? requestedPath,
        PreviewKind requestedKind)
    {
        string? displayedPath = _state.DisplayedPreviewTarget;
        PreviewKind displayedKind = _state.DisplayedPreviewKind;
        if (_state.Mode != ViewerApplicationMode.Viewer)
            return new(null, "Viewerが表示されていません。");
        if (string.IsNullOrWhiteSpace(displayedPath) || displayedKind is not
            (PreviewKind.Text or PreviewKind.LargeText or PreviewKind.Markdown or PreviewKind.CsvTsv))
            return new(null, "現在のViewer内容は外部Editorで開けるテキストsourceではありません。");

        string fullPath;
        try { fullPath = Path.GetFullPath(requestedPath ?? string.Empty); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
        { return new(null, "表示中のsource pathが不正です。"); }

        string currentFullPath;
        try { currentFullPath = Path.GetFullPath(displayedPath); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
        { return new(null, "表示中のsource pathが不正です。"); }

        if (!string.Equals(fullPath, currentFullPath, StringComparison.OrdinalIgnoreCase) || requestedKind != displayedKind)
            return new(null, "Viewer sourceが切り替わりました。現在表示中のsourceを確認してください。");
        if (Directory.Exists(fullPath) || !File.Exists(fullPath))
            return new(null, $"表示中のsource fileが見つかりません: {fullPath}");

        return new(fullPath, null);
    }

    public ViewerPresentationMode ResolveMediaViewerOpenMode(ViewerPresentationMode? explicitMode)
    {
        if (explicitMode is { } requested && Enum.IsDefined(requested))
        {
            return requested;
        }

        ViewerPresentationMode configured = _settings.Value.Preview?.DefaultMediaViewerPresentationMode
            ?? ViewerPresentationMode.PreviewFollow;
        return Enum.IsDefined(configured) ? configured : ViewerPresentationMode.PreviewFollow;
    }

    public ViewerPresentationMode RegisterMediaViewer(
        Guid viewerId,
        ViewerPresentationMode initialMode = ViewerPresentationMode.PreviewFollow)
    {
        if (viewerId == Guid.Empty) return ViewerPresentationMode.PreviewFollow;
        if (!_mediaViewerPresentation.ContainsKey(viewerId))
        {
            ViewerPresentationMode mode = Enum.IsDefined(initialMode) ? initialMode : ViewerPresentationMode.PreviewFollow;
            _mediaViewerPresentation[viewerId] = new MediaViewerPresentationState(
                mode, mode, 1, 0, false, false, 0, PreviewKind.None, ViewerVideoPlaybackState.Empty, null);
        }
        return _mediaViewerPresentation[viewerId].AppliedMode;
    }

    public ViewerPresentationMode GetMediaViewerPresentationMode(Guid viewerId)
        => _mediaViewerPresentation.TryGetValue(viewerId, out var state)
            ? state.AppliedMode
            : ViewerPresentationMode.PreviewFollow;

    public ViewerPresentationMode GetEffectiveMediaViewerPresentationMode(Guid viewerId)
    {
        if (!_mediaViewerPresentation.TryGetValue(viewerId, out MediaViewerPresentationState state))
        {
            return ViewerPresentationMode.PreviewFollow;
        }

        if (state.MediaKind == PreviewKind.Video && state.MediaRequestId > 0)
        {
            if (state.IsPending) return state.DesiredMode;
            if (state.ManualOverrideMode is { } manualMode) return manualMode;
            return GetAutomaticVideoPresentationMode(state.PlaybackState);
        }

        return state.IsPending ? state.DesiredMode : state.AppliedMode;
    }

    private static ViewerPresentationMode GetAutomaticVideoPresentationMode(ViewerVideoPlaybackState playbackState)
        => playbackState is ViewerVideoPlaybackState.Playing or ViewerVideoPlaybackState.Paused
            ? ViewerPresentationMode.PlaybackLocked
            : ViewerPresentationMode.PreviewFollow;

    public bool IsMediaViewerPlaybackAutoManaged(Guid viewerId)
        => _mediaViewerPresentation.TryGetValue(viewerId, out var state)
            && state.MediaKind == PreviewKind.Video
            && state.MediaRequestId > 0;

    public (ViewerPresentationMode Mode, int Generation)? BeginMediaViewerRequest(
        Guid viewerId,
        long requestId,
        PreviewKind mediaKind)
    {
        if (viewerId == Guid.Empty || requestId <= 0) return null;
        RegisterMediaViewer(viewerId);
        MediaViewerPresentationState state = _mediaViewerPresentation[viewerId];
        if (requestId < state.MediaRequestId) return null;
        if (requestId == state.MediaRequestId && mediaKind == state.MediaKind) return null;
        ViewerPresentationMode currentMode = state.IsPending
            ? state.DesiredMode
            : state.ManualOverrideMode ?? state.AppliedMode;

        _mediaViewerPresentation[viewerId] = state with
        {
            MediaRequestId = requestId,
            MediaKind = mediaKind,
            PlaybackState = mediaKind == PreviewKind.Video
                ? ViewerVideoPlaybackState.Opening
                : ViewerVideoPlaybackState.Empty,
            ManualOverrideMode = null
        };

        if (mediaKind == PreviewKind.Video && currentMode != ViewerPresentationMode.PreviewFollow)
        {
            return SetMediaViewerPresentationMode(viewerId, ViewerPresentationMode.PreviewFollow);
        }
        return null;
    }

    public (ViewerPresentationMode Mode, int Generation)? UpdateMediaViewerPlaybackState(
        Guid viewerId,
        long requestId,
        ViewerVideoPlaybackState playbackState)
    {
        if (!_mediaViewerPresentation.TryGetValue(viewerId, out MediaViewerPresentationState state)
            || state.MediaKind != PreviewKind.Video
            || state.MediaRequestId != requestId
            || !Enum.IsDefined(playbackState)
            || state.PlaybackState == playbackState)
        {
            return null;
        }

        state = state with
        {
            PlaybackState = playbackState,
            ManualOverrideMode = null
        };
        _mediaViewerPresentation[viewerId] = state;

        ViewerPresentationMode target = GetAutomaticVideoPresentationMode(playbackState);
        ViewerPresentationMode current = state.IsPending ? state.DesiredMode : state.AppliedMode;
        if (current == target) return null;

        bool synchronizeSelection = target == ViewerPresentationMode.PreviewFollow
            && (state.IsPending ? state.DesiredMode : state.AppliedMode) == ViewerPresentationMode.PlaybackLocked
            && playbackState is ViewerVideoPlaybackState.Stopped or ViewerVideoPlaybackState.Ended;
        return SetMediaViewerPresentationMode(viewerId, target, synchronizeSelection);
    }

    public int GetMediaViewerPresentationGeneration(Guid viewerId)
        => _mediaViewerPresentation.TryGetValue(viewerId, out var state)
            ? state.Generation
            : 0;

    public (ViewerPresentationMode Mode, int Generation) SetMediaViewerPresentationMode(
        Guid viewerId,
        ViewerPresentationMode mode,
        bool synchronizeBrowserSelectionOnApply = false)
    {
        RegisterMediaViewer(viewerId);
        var state = _mediaViewerPresentation[viewerId];
        int generation = checked(state.Generation + 1);
        ViewerPresentationMode desiredMode = Enum.IsDefined(mode) ? mode : ViewerPresentationMode.PreviewFollow;
        _mediaViewerPresentation[viewerId] = state with
        {
            DesiredMode = desiredMode,
            Generation = generation,
            IsPending = true,
            SynchronizeBrowserSelectionOnApply = synchronizeBrowserSelectionOnApply
        };
        return (desiredMode, generation);
    }

    public (ViewerPresentationMode Mode, int Generation) ToggleMediaViewerPresentationMode(Guid viewerId)
    {
        RegisterMediaViewer(viewerId);
        MediaViewerPresentationState state = _mediaViewerPresentation[viewerId];
        if (state.MediaKind == PreviewKind.Video && state.MediaRequestId > 0)
        {
            ViewerPresentationMode videoCurrent = state.IsPending
                ? state.DesiredMode
                : state.ManualOverrideMode ?? state.AppliedMode;
            ViewerPresentationMode videoNext = videoCurrent == ViewerPresentationMode.PreviewFollow
                ? ViewerPresentationMode.PlaybackLocked
                : ViewerPresentationMode.PreviewFollow;
            (ViewerPresentationMode Mode, int Generation) request = SetMediaViewerPresentationMode(
                viewerId,
                videoNext,
                synchronizeBrowserSelectionOnApply: videoNext == ViewerPresentationMode.PreviewFollow);
            state = _mediaViewerPresentation[viewerId];
            _mediaViewerPresentation[viewerId] = state with { ManualOverrideMode = videoNext };
            return request;
        }
        ViewerPresentationMode current = state.IsPending ? state.DesiredMode : state.AppliedMode;
        ViewerPresentationMode next = current == ViewerPresentationMode.PreviewFollow
            ? ViewerPresentationMode.PlaybackLocked
            : ViewerPresentationMode.PreviewFollow;
        return SetMediaViewerPresentationMode(
            viewerId,
            next,
            synchronizeBrowserSelectionOnApply: next == ViewerPresentationMode.PreviewFollow);
    }

    public ViewerPresentationAcknowledgement AcknowledgeMediaViewerPresentationMode(
        Guid viewerId,
        ViewerPresentationMode mode,
        int generation,
        bool accepted)
    {
        if (!_mediaViewerPresentation.TryGetValue(viewerId, out MediaViewerPresentationState state)
            || !state.IsPending
            || generation != state.Generation)
        {
            return new ViewerPresentationAcknowledgement(false, false, GetMediaViewerPresentationMode(viewerId), generation, false);
        }

        bool applied = accepted && mode == state.DesiredMode && Enum.IsDefined(mode);
        ViewerPresentationMode appliedMode = applied ? mode : state.AppliedMode;
        bool synchronizeBrowserSelection = applied
            ? state.SynchronizeBrowserSelectionOnApply
            : state.AppliedMode == ViewerPresentationMode.PreviewFollow
                && state.DesiredMode == ViewerPresentationMode.PlaybackLocked;
        _mediaViewerPresentation[viewerId] = state with
        {
            AppliedMode = appliedMode,
            DesiredMode = appliedMode,
            IsPending = false,
            SynchronizeBrowserSelectionOnApply = false,
            ManualOverrideMode = applied ? state.ManualOverrideMode : null
        };
        return new ViewerPresentationAcknowledgement(true, applied, appliedMode, generation, synchronizeBrowserSelection);
    }

    public void RejectMediaViewerPresentationMode(Guid viewerId, int generation)
    {
        if (!_mediaViewerPresentation.TryGetValue(viewerId, out MediaViewerPresentationState state)
            || !state.IsPending
            || generation != state.Generation)
        {
            return;
        }
        _mediaViewerPresentation[viewerId] = state with
        {
            DesiredMode = state.AppliedMode,
            IsPending = false,
            SynchronizeBrowserSelectionOnApply = false,
            ManualOverrideMode = null
        };
    }

    public int NextMediaViewerSelectionGeneration(Guid viewerId)
    {
        RegisterMediaViewer(viewerId);
        MediaViewerPresentationState state = _mediaViewerPresentation[viewerId];
        int generation = checked(state.SelectionGeneration + 1);
        _mediaViewerPresentation[viewerId] = state with { SelectionGeneration = generation };
        return generation;
    }

    public void ForgetMediaViewer(Guid viewerId)
        => _mediaViewerPresentation.Remove(viewerId);

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
        kind = ResolveBrowserOpenMediaKind(fullPath, kind);
        return new ViewerOpenRequest(fullPath, kind is PreviewKind.Image or PreviewKind.Video
            ? ViewerOpenRoute.MediaViewer
            : ViewerOpenRoute.InternalViewer, kind);
    }

    public ViewerOpenRequest? CreatePlaybackLockedOpenRequest(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)
            || Directory.Exists(fullPath)
            || !File.Exists(fullPath))
        {
            return null;
        }

        // Keep Browser media metadata classification consistent for locked opens.
        PreviewKind kind = ResolveBrowserOpenMediaKind(fullPath, PreviewService.GetPreviewKind(fullPath));
        return kind is PreviewKind.Image or PreviewKind.Video
            ? new ViewerOpenRequest(fullPath, ViewerOpenRoute.MediaViewer, kind, ViewerPresentationMode.PlaybackLocked)
            : null;
    }

    private static readonly string[] NonMediaBrowserExtensions = [".exe", ".com", ".bat", ".cmd", ".lnk", ".url"];

    private PreviewKind ResolveBrowserOpenMediaKind(string fullPath, PreviewKind rawKind)
    {
        if (rawKind is not (PreviewKind.None or PreviewKind.Binary))
        {
            return rawKind;
        }

        // Keep known Browser classifications ahead of optional Susie metadata.
        PreviewKind shallowKind = PreviewService.GetPreviewKindShallow(fullPath, isDirectory: false);
        if (shallowKind != PreviewKind.None || IsNonMediaBrowserPath(fullPath))
        {
            return rawKind;
        }

        return HasInspectedSusieImageCandidate(fullPath) ? PreviewKind.Image : rawKind;
    }

    private static bool IsNonMediaBrowserPath(string fullPath)
        => ArchiveFileTypeHelper.IsArchive(fullPath)
            || Array.Exists(NonMediaBrowserExtensions, extension =>
                string.Equals(extension, Path.GetExtension(fullPath), StringComparison.OrdinalIgnoreCase));

    private bool HasInspectedSusieImageCandidate(string fullPath)
    {
        SusiePluginSettings? susieSettings = _settings.Value.Preview?.SusiePlugins;
        if (susieSettings == null)
        {
            return false;
        }

        string extension = SusiePluginSettings.NormalizeExtension(Path.GetExtension(fullPath));
        if (extension.Length <= 1)
        {
            return false;
        }

        bool hasExplicitSelection = false;
        string? selectedDecoder = null;
        foreach ((string configuredExtension, string decoder) in susieSettings.DecoderByExtension
                     ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
        {
            if (!string.Equals(
                    SusiePluginSettings.NormalizeExtension(configuredExtension),
                    extension,
                    StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(decoder))
            {
                continue;
            }

            selectedDecoder = decoder.Trim();
            hasExplicitSelection = true;
            break;
        }

        if (hasExplicitSelection && string.Equals(selectedDecoder, "standard", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        IEnumerable<SusiePluginRegistration> registrations = susieSettings.Plugins
            ?? new List<SusiePluginRegistration>();
        if (hasExplicitSelection)
        {
            return registrations.Any(registration =>
                registration != null
                && registration.IsEnabled
                && HasInspectedExtension(registration, extension)
                && PathsReferToSamePlugin(registration.PluginPath, selectedDecoder));
        }

        return registrations.Any(registration =>
            registration != null
            && registration.IsEnabled
            && HasInspectedExtension(registration, extension));
    }

    private static bool HasInspectedExtension(SusiePluginRegistration registration, string extension)
        => !string.IsNullOrWhiteSpace(registration.PluginPath)
            && registration.InspectionMetadata?.Extensions?.Any(candidate =>
                string.Equals(
                    SusiePluginSettings.NormalizeExtension(candidate),
                    extension,
                    StringComparison.OrdinalIgnoreCase)) == true;

    private static bool PathsReferToSamePlugin(string? registeredPath, string? selectedPath)
    {
        if (string.IsNullOrWhiteSpace(registeredPath) || string.IsNullOrWhiteSpace(selectedPath))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(registeredPath),
                Path.GetFullPath(selectedPath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    public ViewerEnterDecision ResolveEnter(string fullPath)
    {
        PreviewKind rawKind = PreviewService.GetPreviewKind(fullPath);
        if (rawKind == PreviewKind.Video)
        {
            bool isAudio = PreviewService.IsSupportedAudioExtension(fullPath);
            if (isAudio)
            {
                return new ViewerEnterDecision(null, true, isAudio);
            }

            return new ViewerEnterDecision(
                new ViewerOpenRequest(fullPath, ViewerOpenRoute.MediaViewer, PreviewKind.Video),
                false,
                false);
        }

        return new ViewerEnterDecision(CreateBrowserOpenRequest(fullPath, allowExecuteTarget: true), false, false);
    }

    public BrowserCtrlEnterRoute ResolveBrowserCtrlEnter(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
        {
            return BrowserCtrlEnterRoute.None;
        }

        if (PreviewService.IsSupportedAudioExtension(fullPath))
        {
            return BrowserCtrlEnterRoute.PlayAudioExternally;
        }

        ViewerOpenRequest? request = CreatePlaybackLockedOpenRequest(fullPath);
        return request is { ViewerKind: PreviewKind.Image or PreviewKind.Video }
            ? BrowserCtrlEnterRoute.OpenVideoPlaybackLocked
            : BrowserCtrlEnterRoute.None;
    }

    public ViewerAudioCtrlEnterDecision ResolveAudioCtrlEnter(string fullPath)
        => PreviewService.IsSupportedAudioExtension(fullPath)
            ? new ViewerAudioCtrlEnterDecision(true, true)
            : new ViewerAudioCtrlEnterDecision(false, false);

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
