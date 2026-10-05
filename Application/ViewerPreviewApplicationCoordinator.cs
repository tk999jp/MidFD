using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MidFD.Configuration;
using MidFD.Models;
using MidFD.Services;

namespace MidFD.Runtime;

internal interface IViewerPreviewUiPort
{
    bool IsViewerMode { get; }
    int VisibleLineCount { get; }
    bool IsLatestRequest(int requestId, string path, CancellationToken token);
    TextPreviewEncodingOverride GetTextPreviewEncodingOverride();
    void ClearPreview(string message, int requestId);
    void ApplyViewerChromeState();
    void ApplyViewerStatus(string reason);
    void ShowStatusMessage(string message);
    void ReturnToBrowserForVideo();
    void ApplyImagePreview(string path);
    void ApplyVideoPreview(string path, VideoPreviewOptions options);
    void ApplyTextPreview(TextPreviewContent content);
    void ApplyMarkdownPreview(string text, string path, MarkdownViewerMode mode);
    void ApplyDelimitedPreview(DelimitedTextTable table);
    bool IsSearchHitPreviewPending(string path);
    void ApplyContentSearchPreviewCompletion(int requestId, string path, PreviewKind kind, string result);
    void ApplySqlitePreview(string text);
    void ApplyBinaryPreview(string text);
    void BeginLargeTextPreview(LargeFilePreviewState state);
    Task ApplyLargeTextInitialDisplayAsync(LargeFilePreviewState state, int requestId, CancellationToken token);
}

internal sealed record VideoPreviewOptions(
    string? ToolDirectory,
    int InitialSeconds,
    int VolumePercent);

/// <summary>
/// Text Previewのrequest lifecycleと内容分岐を担当する。
/// WinForms controlやMainFormを参照せず、表示反映は型付きUI portへ返す。
/// </summary>
internal sealed class ViewerPreviewApplicationCoordinator
{
    private readonly ViewerSessionState _state;
    private readonly PreviewApplicationCoordinator _contentCoordinator;
    private readonly ViewerWorkflowApplicationCoordinator _workflow;
    private readonly SettingsApplicationCoordinator _settings;
    private readonly PreviewDiagnosticDelayService _diagnosticDelayService = new();

    public ViewerPreviewApplicationCoordinator(
        ViewerSessionState state,
        PreviewApplicationCoordinator contentCoordinator,
        ViewerWorkflowApplicationCoordinator workflow,
        SettingsApplicationCoordinator settings)
    {
        _state = state;
        _contentCoordinator = contentCoordinator;
        _workflow = workflow;
        _settings = settings;
    }

    public bool RequestRefresh(
        string? requestPath,
        bool force,
        PreviewKind? previewKindOverride,
        IViewerPreviewUiPort ui)
    {
        if (string.IsNullOrWhiteSpace(requestPath))
        {
            _state.PreviewRequests.Cancel();
            _state.LastRequestedPath = null;
            _state.CurrentPreviewTarget = null;
            _state.AutoPreviewSuppressed = false;
            _state.LastAutoPreviewSuppressedMessage = null;
            ui.ClearPreview("選択なしのためプレビューなし", -1);
            return false;
        }

        PreviewKind shallowKind = _workflow.ResolveEffectivePreviewKind(requestPath);
        if (!force && !ViewerWorkflowApplicationCoordinator.IsBrowserAutoPreviewEligible(shallowKind))
        {
            string message = ViewerWorkflowApplicationCoordinator.GetBrowserAutoPreviewSuppressedMessage(shallowKind);
            if (!(_state.AutoPreviewSuppressed
                && string.Equals(_state.LastAutoPreviewSuppressedMessage, message, StringComparison.Ordinal)))
            {
                ui.ClearPreview(message, -1);
            }
            _state.CurrentPreviewTarget = requestPath;
            _state.AutoPreviewSuppressed = true;
            _state.LastAutoPreviewSuppressedMessage = message;
            return false;
        }

        if (!force
            && string.Equals(_state.LastRequestedPath, requestPath, StringComparison.OrdinalIgnoreCase)
            && _state.PreviewRequests.IsInFlight)
        {
            return false;
        }

        _state.AutoPreviewSuppressed = false;
        _state.LastAutoPreviewSuppressedMessage = null;
        _state.PreviewRequests.Cancel();
        CancellationToken token = _state.PreviewRequests.StartNewRequest(out int requestId);
        _state.ExchangeActiveRequestId(requestId);
        _state.LastRequestedPath = requestPath;
        LogService.Info($"[PreviewRequest] queued reqId={requestId} requestPath={requestPath} force={force}");
        _ = RunAsync(requestId, requestPath, token, previewKindOverride, ui);
        return true;
    }

    public void CancelRequest()
    {
        _state.PreviewRequests.Cancel();
    }

    public async Task RunAsync(
        int requestId,
        string requestPath,
        CancellationToken token,
        PreviewKind? previewKindOverride,
        IViewerPreviewUiPort ui)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        string result = "Completed";
        string stage = "Start";
        PreviewKind resolvedKind = PreviewKind.None;
        Exception? failure = null;

        try
        {
            if (ui.IsViewerMode)
            {
                await Task.Yield();
            }
            else
            {
                await Task.Delay(150, token).ConfigureAwait(true);
            }

            await _diagnosticDelayService.DelayAsync(
                "Preview",
                requestPath,
                _diagnosticDelayService.PreviewDelayMs,
                token).ConfigureAwait(true);

            if (!ui.IsLatestRequest(requestId, requestPath, token))
            {
                result = "Superseded";
                stage = "Debounce";
                return;
            }

            _state.LargeFileState = null;
            if (Directory.Exists(requestPath))
            {
                result = "SkippedDirectory";
                stage = "DirectoryGuard";
                ui.ClearPreview("プレビュー対象外", requestId);
                return;
            }

            if (!string.Equals(_state.CurrentPreviewTarget, requestPath, StringComparison.OrdinalIgnoreCase))
            {
                ui.ClearPreview(string.Empty, requestId);
                _state.CurrentPreviewTarget = requestPath;
            }

            await _diagnosticDelayService.DelayAsync(
                "PreviewKind",
                requestPath,
                _diagnosticDelayService.PreviewKindDelayMs,
                token).ConfigureAwait(true);

            (PreviewKind rawKind, TextPreviewProbeResult? probe) = await Task.Run(
                () =>
                {
                    PreviewKind kind = previewKindOverride.HasValue
                        ? PreviewService.GetContentPreviewKind(requestPath, out TextPreviewProbeResult? detectedProbe)
                        : PreviewService.GetPreviewKind(requestPath, out detectedProbe);
                    return (kind, detectedProbe);
                },
                token).ConfigureAwait(true);

            PreviewKind kind = _workflow.ResolveEffectivePreviewKind(
                requestPath,
                rawKind);
            resolvedKind = kind;
            if (!ui.IsLatestRequest(requestId, requestPath, token))
            {
                result = "Superseded";
                stage = "AfterKind";
                return;
            }

            if (kind == PreviewKind.None)
            {
                result = "SkippedUnsupported";
                stage = "KindNone";
                ui.ClearPreview($"プレビュー対象外\n{Path.GetExtension(requestPath)}", requestId);
                return;
            }

            ui.ClearPreview("プレビュー読み込み中...", requestId);
            await ApplyKindAsync(kind, requestPath, probe, requestId, token, ui).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            result = "Canceled";
            stage = "Canceled";
        }
        catch (Exception ex)
        {
            result = "Failed";
            stage = "Exception";
            failure = ex;
            if (ui.IsLatestRequest(requestId, requestPath, token))
            {
                ui.ClearPreview($"エラー: {ex.Message}", requestId);
            }
        }
        finally
        {
            string error = failure == null
                ? string.Empty
                : $" exceptionType='{failure.GetType().Name}' message='{failure.Message}'";
            LogService.Info(
                $"[PreviewRequest] completed reqId={requestId} result={result} stage='{stage}' " +
                $"kind={resolvedKind} elapsedMs={stopwatch.ElapsedMilliseconds} path='{requestPath}'{error}");
            ui.ApplyContentSearchPreviewCompletion(requestId, requestPath, resolvedKind, result);
            if (_state.ActiveRequestId == requestId)
            {
                _state.PreviewRequests.EndRequest(requestId);
            }
        }
    }

    private async Task ApplyKindAsync(
        PreviewKind kind,
        string path,
        TextPreviewProbeResult? probe,
        int requestId,
        CancellationToken token,
        IViewerPreviewUiPort ui)
    {
        switch (kind)
        {
            case PreviewKind.Image:
                await DelayOpenAsync(path, token).ConfigureAwait(true);
                if (!ui.IsLatestRequest(requestId, path, token)) return;
                _state.CurrentKind = PreviewKind.Image;
                ui.ApplyImagePreview(path);
                return;

            case PreviewKind.Video:
                await DelayOpenAsync(path, token).ConfigureAwait(true);
                if (ui.IsViewerMode)
                {
                    _state.Mode = ViewerApplicationMode.Browser;
                    _state.CurrentKind = PreviewKind.None;
                    ui.ReturnToBrowserForVideo();
                    ui.ShowStatusMessage("Enter/V: 画像プレビューで静止画表示 / Ctrl+Enter: 外部再生");
                }
                _state.CurrentKind = PreviewKind.Video;
                ui.ApplyVideoPreview(path, new VideoPreviewOptions(
                    _settings.Value.Preview?.VideoToolDirectory,
                    _settings.Value.Preview?.VideoSkipSeconds ?? 0,
                    _settings.Value.Preview?.VideoPlaybackVolumePercent ?? 100));
                return;

            case PreviewKind.Text:
                await DelayOpenAsync(path, token).ConfigureAwait(true);
                TextPreviewContent text = await _contentCoordinator.LoadTextAsync(
                    path,
                    PreviewService.LargeTextThresholdBytes,
                    ui.GetTextPreviewEncodingOverride(),
                    token).ConfigureAwait(true);
                if (ui.IsLatestRequest(requestId, path, token) && ui.IsViewerMode)
                {
                    _state.CurrentKind = PreviewKind.Text;
                    _state.DetectedEncodingLabel = text.EncodingLabel;
                    ui.ApplyTextPreview(text);
                }
                return;

            case PreviewKind.Markdown:
                await DelayOpenAsync(path, token).ConfigureAwait(true);
                string markdown = await _contentCoordinator.LoadMarkdownAsync(
                    path,
                    PreviewService.LargeTextThresholdBytes,
                    token).ConfigureAwait(true);
                if (ui.IsLatestRequest(requestId, path, token) && ui.IsViewerMode)
                {
                    _state.CurrentKind = PreviewKind.Markdown;
                    _state.MarkdownSource = markdown;
                    _state.DetectedEncodingLabel = "Markdown";
                    ui.ApplyMarkdownPreview(
                        markdown,
                        path,
                        _settings.Value.Preview?.MarkdownViewerMode ?? MarkdownViewerMode.Rendered);
                }
                return;

            case PreviewKind.CsvTsv:
                await DelayOpenAsync(path, token).ConfigureAwait(true);
                if (ui.IsSearchHitPreviewPending(path))
                {
                    TextPreviewContent rawText = await _contentCoordinator.LoadTextAsync(
                        path,
                        PreviewService.LargeTextThresholdBytes,
                        ui.GetTextPreviewEncodingOverride(),
                        token).ConfigureAwait(true);
                    if (ui.IsLatestRequest(requestId, path, token) && ui.IsViewerMode)
                    {
                        _state.CurrentKind = PreviewKind.Text;
                        _state.DetectedEncodingLabel = rawText.EncodingLabel;
                        ui.ApplyTextPreview(rawText);
                    }
                    return;
                }
                DelimitedTextTable table = await _contentCoordinator.LoadDelimitedAsync(
                    path,
                    PreviewService.LargeTextThresholdBytes,
                    token).ConfigureAwait(true);
                if (ui.IsLatestRequest(requestId, path, token) && ui.IsViewerMode)
                {
                    _state.CurrentKind = PreviewKind.CsvTsv;
                    _state.DetectedEncodingLabel = "CSV/TSV";
                    ui.ApplyDelimitedPreview(table);
                }
                return;

            case PreviewKind.Sqlite:
                await DelayOpenAsync(path, token).ConfigureAwait(true);
                string sqlite = await _contentCoordinator.LoadSqliteAsync(path, token).ConfigureAwait(true);
                if (ui.IsLatestRequest(requestId, path, token) && ui.IsViewerMode)
                {
                    _state.CurrentKind = PreviewKind.Sqlite;
                    _state.DetectedEncodingLabel = "SQLite";
                    ui.ApplySqlitePreview(sqlite);
                }
                return;

            case PreviewKind.LargeText:
                await ApplyLargeTextAsync(path, probe, requestId, token, ui).ConfigureAwait(true);
                return;

            case PreviewKind.Binary:
                await DelayOpenAsync(path, token).ConfigureAwait(true);
                string dump = await _contentCoordinator.LoadBinaryDumpAsync(path, token).ConfigureAwait(true);
                if (ui.IsLatestRequest(requestId, path, token))
                {
                    _state.CurrentKind = PreviewKind.Binary;
                    ui.ApplyBinaryPreview(dump);
                }
                return;
        }
    }

    private async Task ApplyLargeTextAsync(
        string path,
        TextPreviewProbeResult? probe,
        int requestId,
        CancellationToken token,
        IViewerPreviewUiPort ui)
    {
        if (!ui.IsViewerMode)
        {
            return;
        }

        var state = new LargeFilePreviewState
        {
            FilePath = path,
            IsIndexing = true
        };
        _state.LargeFileState = state;
        _state.CurrentKind = PreviewKind.LargeText;
        ui.BeginLargeTextPreview(state);

        await DelayOpenAsync(path, token).ConfigureAwait(true);
        TextPreviewProbeResult detected = probe
            ?? await Task.Run(() => PreviewService.ProbeTextPreview(path), token).ConfigureAwait(true);
        state.DetectedEncoding = detected.Encoding;
        state.DetectedEncodingLabel = detected.EncodingLabel;
        state.HasBom = detected.HasBom;
        state.IsBinaryLike = detected.IsBinaryLike;
        state.IsLongLineDetected = detected.HasLongLine;

        if (!ui.IsLatestRequest(requestId, path, token) || !ui.IsViewerMode)
        {
            return;
        }

        if (state.IsBinaryLike)
        {
            ui.ClearPreview("LargeText対象外: binary-like file", requestId);
            ui.ApplyViewerStatus("LargeText binary-like guard");
            ui.ShowStatusMessage("LargeText対象外: binary-like file を検出しました。");
            return;
        }

        if (state.IsEncodingUnsupportedForLargeText)
        {
            ui.ClearPreview($"LargeText未対応: {state.DetectedEncodingLabel}", requestId);
            ui.ApplyViewerStatus("LargeText unsupported encoding guard");
            ui.ShowStatusMessage($"LargeText未対応: {state.DetectedEncodingLabel}");
            return;
        }

        await LargeFileLineReaderService.ReadFirstLinesQuicklyAsync(
            state,
            ui.VisibleLineCount * 2,
            token).ConfigureAwait(true);

        if (ui.IsLatestRequest(requestId, path, token) && ui.IsViewerMode)
        {
            await ui.ApplyLargeTextInitialDisplayAsync(state, requestId, token).ConfigureAwait(true);
        }
    }

    private Task DelayOpenAsync(string path, CancellationToken token)
    {
        return _diagnosticDelayService.DelayAsync(
            "PreviewOpen",
            path,
            _diagnosticDelayService.PreviewOpenDelayMs,
            token);
    }
}
