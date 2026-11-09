using System.IO;
using MidFD.Commands;
using MidFD.Models;
using MidFD.Services;

namespace MidFD.Runtime;

internal sealed class BrowserDragOutApplicationCoordinator
{
    private readonly string _dragArchiveTempDirectory;

    public BrowserDragOutApplicationCoordinator(string? dragArchiveTempDirectory = null)
    {
        _dragArchiveTempDirectory = string.IsNullOrWhiteSpace(dragArchiveTempDirectory)
            ? DragArchiveService.GetDragArchiveTempDirectory()
            : dragArchiveTempDirectory;
    }

    public BrowserDragOutPreparation? Prepare(BrowserDragOutCommandContext? request, bool isClipboardBusy)
    {
        if (request is null)
        {
            return null;
        }

        IReadOnlyList<string> selection = request.SelectionSnapshot.FullPaths;
        bool isArchiveRequested = ((request.MouseDownModifiers | request.CurrentModifiers)
            & (BrowserDragModifiers.Shift | BrowserDragModifiers.Control)) != 0;

        if (request.IsBlankDrag && (selection.Count == 0 || !isArchiveRequested))
        {
            return null;
        }

        if (isClipboardBusy)
        {
            return null;
        }

        if (request.IsBlankDrag && !request.EnableDragArchiveHandoff)
        {
            return null;
        }

        if (request.EnableDragArchiveHandoff && isArchiveRequested)
        {
            BrowserOutgoingDragDecision archiveDecision = BrowserOutgoingDragResolver.Resolve(
                request.MouseButton == BrowserDragMouseButton.Right,
                request.CurrentModifiers,
                isDragArchive: true);
            return new BrowserDragOutPreparation(
                Array.Empty<string>(),
                IsArchive: true,
                ArchiveBaseDirectory: null,
                ArchiveItemCount: 0,
                ArchiveSizeBytes: 0,
                archiveDecision.AllowedEffects,
                archiveDecision.PreferredEffect,
                archiveDecision.HasPreferredEffect,
                archiveDecision.StatusText,
                ArchiveRequest: new BrowserDragOutArchiveRequest(
                    _dragArchiveTempDirectory,
                    selection.ToArray(),
                    request.CandidatePath,
                    request.IncludeDragZipManifest));
        }

        IReadOnlyList<string> dragPaths = ResolveFileDropPaths(selection, request.CandidatePath);
        if (dragPaths.Count == 0)
        {
            return null;
        }

        LogService.Info(
            $"[DragArchive] dragPaths.Count={dragPaths.Count}, selection.Count={selection.Count}, " +
            $"enableDragArchiveHandoff={request.EnableDragArchiveHandoff}, includeManifest={request.IncludeDragZipManifest}, " +
            $"mouseDownModifier={request.MouseDownModifiers}, currentModifier={request.CurrentModifiers}, " +
            $"archiveDragRequested={isArchiveRequested}, candidatePath='{request.CandidatePath}'");

        BrowserOutgoingDragDecision decision = BrowserOutgoingDragResolver.Resolve(
            request.MouseButton == BrowserDragMouseButton.Right,
            request.CurrentModifiers);
        return new BrowserDragOutPreparation(
            dragPaths,
            IsArchive: false,
            null,
            0,
            0,
            decision.AllowedEffects,
            decision.PreferredEffect,
            decision.HasPreferredEffect,
            decision.StatusText);
    }

    internal static IReadOnlyList<string> ResolveFileDropPaths(
        IReadOnlyList<string> selection,
        string? candidatePath)
    {
        if (!string.IsNullOrWhiteSpace(candidatePath)
            && selection.Count > 1
            && !selection.Contains(candidatePath, StringComparer.OrdinalIgnoreCase))
        {
            return IsFileSystemItem(candidatePath) ? new[] { candidatePath } : Array.Empty<string>();
        }

        if (selection.Count > 0)
        {
            return selection.Where(IsFileSystemItem).ToArray();
        }

        return !string.IsNullOrWhiteSpace(candidatePath) && IsFileSystemItem(candidatePath)
            ? new[] { candidatePath }
            : Array.Empty<string>();
    }

    private static bool IsFileSystemItem(string path) => File.Exists(path) || Directory.Exists(path);

    internal static BrowserDragOutArchiveBuildResult BuildArchive(BrowserDragOutArchiveRequest request)
    {
        IReadOnlyList<string> archivePaths = DragTargetResolver.Resolve(
            request.Selection,
            request.CandidatePath);
        if (archivePaths.Count == 0)
        {
            return BrowserDragOutArchiveBuildResult.NoOp;
        }

        LogService.Info(
            $"[DragArchive] dragPaths.Count={archivePaths.Count}, selection.Count={request.Selection.Count}, " +
            $"includeManifest={request.IncludeManifest}, archiveDragRequested=true, " +
            $"candidatePath='{request.CandidatePath}'");

        DragArchiveService.CleanupDragArchivesBeforeCreation(request.TempDirectory);
        DragArchiveService.DragArchiveInfo archiveInfo = DragArchiveService.GetOrCreateInfoZip(
            request.TempDirectory,
            archivePaths,
            request.IncludeManifest);
        long archiveSizeBytes = new FileInfo(archiveInfo.ArchivePath).Length;
        LogService.Info(
            $"[DragArchive] Prepared archive: baseDirectory='{archiveInfo.BaseDirectory}', " +
            $"archivePath='{archiveInfo.ArchivePath}', itemCount={archiveInfo.ItemCount}, " +
            $"sizeBytes={archiveSizeBytes}");
        return new BrowserDragOutArchiveBuildResult(
            archiveInfo.ArchivePath,
            archiveInfo.BaseDirectory,
            archiveInfo.ItemCount,
            archiveSizeBytes);
    }

    internal static BrowserDragOutPreparation CompleteArchivePreparation(
        BrowserDragOutPreparation pending,
        BrowserDragOutArchiveBuildResult result)
    {
        if (!pending.IsArchive || pending.ArchiveRequest is null)
        {
            throw new InvalidOperationException("ZIP作成待ちのドラッグ準備ではありません。");
        }

        return pending with
        {
            FileDropPaths = result.ArchivePath is { Length: > 0 } archivePath
                ? new[] { archivePath }
                : Array.Empty<string>(),
            ArchiveBaseDirectory = result.BaseDirectory,
            ArchiveItemCount = result.ItemCount,
            ArchiveSizeBytes = result.SizeBytes,
            CompletionStatusText = result.ArchivePath is { Length: > 0 }
                ? $"ドラッグ用ZIPを作成しました。{result.ItemCount}件 / {FileOperationService.FormatSize(result.SizeBytes)}"
                : null,
            ErrorMessage = result.ErrorMessage,
            ArchiveRequest = null
        };
    }
}

internal sealed record BrowserDragOutPreparation(
    IReadOnlyList<string> FileDropPaths,
    bool IsArchive,
    string? ArchiveBaseDirectory,
    int ArchiveItemCount,
    long ArchiveSizeBytes,
    BrowserDragEffect AllowedEffects,
    BrowserDragEffect PreferredEffect,
    bool HasPreferredEffect,
    string StatusText,
    string? CompletionStatusText = null,
    string? ErrorMessage = null,
    BrowserDragOutArchiveRequest? ArchiveRequest = null);

internal sealed record BrowserDragOutArchiveRequest(
    string TempDirectory,
    IReadOnlyList<string> Selection,
    string? CandidatePath,
    bool IncludeManifest) : IBrowserDragOutArchiveBuildOperation
{
    public BrowserDragOutArchiveBuildResult Build() =>
        BrowserDragOutApplicationCoordinator.BuildArchive(this);
}

internal sealed record BrowserDragOutArchiveBuildResult(
    string? ArchivePath,
    string? BaseDirectory,
    int ItemCount,
    long SizeBytes,
    string? ErrorMessage = null)
{
    public static BrowserDragOutArchiveBuildResult NoOp { get; } = new(null, null, 0, 0);

    public static BrowserDragOutArchiveBuildResult Failed(Exception exception) =>
        new(null, null, 0, 0, $"ドラッグ用ZIPの作成に失敗しました:\n{exception.Message}");
}

internal interface IBrowserDragOutArchiveBuildOperation
{
    BrowserDragOutArchiveBuildResult Build();
}

internal static class BrowserDragOutArchiveWorker
{
    internal static async Task<BrowserDragOutArchiveBuildResult> RunAsync(
        IBrowserDragOutArchiveBuildOperation buildOperation,
        BrowserDragOutDiagnosticSession? diagnostics = null)
    {
        try
        {
            return await Task.Run(() =>
            {
                bool? buttonPressed = diagnostics?.SampleInitiatingButtonPressed();
                diagnostics?.MarkWorkerStarted(buttonPressed);
                diagnostics?.Record("archive_build_started", buttonPressed);
                diagnostics?.Flush();

                BrowserDragOutArchiveBuildResult result;
                try
                {
                    result = buildOperation.Build();
                }
                catch (Exception exception)
                {
                    try
                    {
                        LogService.Error("ドラッグ用ZIP作成失敗", exception);
                    }
                    catch
                    {
                        // Logging must not turn an expected archive failure into an async-void exception.
                    }

                    result = BrowserDragOutArchiveBuildResult.Failed(exception);
                }

                diagnostics?.MarkWorkerCompleted(
                    diagnostics.SampleInitiatingButtonPressed(),
                    result.SizeBytes,
                    result.ErrorMessage is null ? "success" : "failure");
                diagnostics?.Flush();
                return result;
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            try
            {
                LogService.Error("ドラッグ用ZIP作成失敗", exception);
            }
            catch
            {
                // Logging must not turn an expected archive failure into an async-void exception.
            }

            BrowserDragOutArchiveBuildResult result = BrowserDragOutArchiveBuildResult.Failed(exception);
            diagnostics?.MarkWorkerCompleted(
                diagnostics.SampleInitiatingButtonPressed(),
                result.SizeBytes,
                "worker_failure");
            diagnostics?.Flush();
            return result;
        }
    }
}
