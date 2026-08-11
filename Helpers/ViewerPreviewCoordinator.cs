using System;
using System.IO;
using System.Threading;
using MidFD.Services;

namespace MidFD.Helpers;

/// <summary>
/// Browser からの preview / viewer 起動意図と、
/// preview 更新要求を既存導線へ渡す前後の最小 orchestration だけを担当する。
/// </summary>
public sealed class ViewerPreviewCoordinator
{
    public enum BrowserOpenRoute
    {
        ExecuteTarget,
        Archive,
        MediaViewer,
        InternalViewer
    }

    public sealed class BrowserOpenRequest
    {
        public required string FullPath { get; init; }
        public required BrowserOpenRoute Route { get; init; }
        public PreviewKind ViewerKind { get; init; } = PreviewKind.None;
    }

    public sealed class BrowserOpenExecutionContext
    {
        public required Action<string> ExecuteConfirmedFile { get; init; }
        public required Action<string> ShowArchiveContentsOrFallback { get; init; }
        public required Action<string> OpenMediaViewer { get; init; }
        public required Action<PreviewKind> EnterInternalViewer { get; init; }
    }

    public readonly record struct PreviewRefreshRequest(int RequestId, CancellationToken Token);
    public readonly record struct ViewerModeLifecyclePlan(
        PreviewKind NextViewerKind,
        bool ShouldClearPreview,
        string ClearMessage,
        bool ShouldRefreshPreview);

    public BrowserOpenRequest? CreateBrowserOpenRequest(
        string? fullPath,
        bool allowExecuteTarget,
        Func<string, bool> isExecuteTarget,
        Func<string, bool> isArchiveTarget,
        Func<string, PreviewKind> getPreviewKind)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || Directory.Exists(fullPath) || !File.Exists(fullPath))
        {
            return null;
        }

        if (allowExecuteTarget && isExecuteTarget(fullPath))
        {
            return new BrowserOpenRequest
            {
                FullPath = fullPath,
                Route = BrowserOpenRoute.ExecuteTarget
            };
        }

        if (isArchiveTarget(fullPath))
        {
            return new BrowserOpenRequest
            {
                FullPath = fullPath,
                Route = BrowserOpenRoute.Archive
            };
        }

        var kind = getPreviewKind(fullPath);
        if (kind == PreviewKind.Image || kind == PreviewKind.Video)
        {
            return new BrowserOpenRequest
            {
                FullPath = fullPath,
                Route = BrowserOpenRoute.MediaViewer,
                ViewerKind = kind
            };
        }

        return new BrowserOpenRequest
        {
            FullPath = fullPath,
            Route = BrowserOpenRoute.InternalViewer,
            ViewerKind = kind
        };
    }

    public bool ExecuteBrowserOpenRequest(BrowserOpenRequest? request, BrowserOpenExecutionContext context)
    {
        if (request == null)
        {
            return false;
        }

        switch (request.Route)
        {
            case BrowserOpenRoute.ExecuteTarget:
                context.ExecuteConfirmedFile(request.FullPath);
                return true;

            case BrowserOpenRoute.Archive:
                context.ShowArchiveContentsOrFallback(request.FullPath);
                return true;

            case BrowserOpenRoute.MediaViewer:
                context.OpenMediaViewer(request.FullPath);
                return true;

            case BrowserOpenRoute.InternalViewer:
                context.EnterInternalViewer(request.ViewerKind);
                return true;

            default:
                return false;
        }
    }

    public PreviewRefreshRequest StartPreviewRefresh(
        ref int previewRequestId,
        ref int activePreviewRequestId,
        ref CancellationTokenSource? previewCts)
    {
        int reqId = Interlocked.Increment(ref previewRequestId);
        Interlocked.Exchange(ref activePreviewRequestId, reqId);
        previewCts?.Cancel();
        previewCts?.Dispose();
        previewCts = new CancellationTokenSource();
        return new PreviewRefreshRequest(reqId, previewCts.Token);
    }

    public void ExecutePreviewRefresh(
        ref int previewRequestId,
        ref int activePreviewRequestId,
        ref CancellationTokenSource? previewCts,
        Func<int, CancellationToken, Task> runPreviewUpdate)
    {
        var request = StartPreviewRefresh(
            ref previewRequestId,
            ref activePreviewRequestId,
            ref previewCts);

        _ = runPreviewUpdate(request.RequestId, request.Token);
    }

    public ViewerModeLifecyclePlan CreateViewerModeLifecyclePlan(
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
}
