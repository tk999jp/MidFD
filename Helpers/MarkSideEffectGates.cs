using System.Threading;
using System.Threading.Tasks;
using System.IO;
using MidFD.Services;

namespace MidFD.Helpers;

public sealed class BrowserSelectionIdentityGate
{
    private string? _lastPath;
    private long _lastContentGeneration = long.MinValue;

    public bool TryAccept(string? path, long contentGeneration = 0)
    {
        bool changed = !string.Equals(_lastPath, path, StringComparison.OrdinalIgnoreCase)
            || _lastContentGeneration != contentGeneration;
        _lastPath = path;
        _lastContentGeneration = contentGeneration;
        return changed;
    }
}

public readonly record struct MarkOperationEffectPlan(
    int MarkCommitCount,
    int ActiveTabSyncCount,
    int InfoUpdateScheduleCount,
    int PersistenceValidationCount);

/// <summary>Mark mutation effects used by the production UI flow.</summary>
public sealed class MarkOperationEffectCoordinator
{
    public MarkOperationEffectPlan CommitMutation(int changedCount)
    {
        return changedCount <= 0
            ? default
            : new MarkOperationEffectPlan(1, 1, 1, 0);
    }

    public MarkOperationEffectPlan ExecuteMutation(
        int changedCount,
        Action markCommit,
        Action activeTabSync,
        Action infoUpdateSchedule)
    {
        MarkOperationEffectPlan plan = CommitMutation(changedCount);
        for (int index = 0; index < plan.MarkCommitCount; index++) markCommit();
        for (int index = 0; index < plan.ActiveTabSyncCount; index++) activeTabSync();
        for (int index = 0; index < plan.InfoUpdateScheduleCount; index++) infoUpdateSchedule();
        return plan;
    }
}

public enum MarkSummaryCacheState
{
    Invalid,
    CountOnly,
    Complete
}

public readonly record struct MarkSummaryBulkEffectResult(
    int CountOnlyApplyCount,
    int SummaryScheduleCount,
    int PendingInvalidationCount);

/// <summary>Bulk mark後のcount-only反映とsize集計予約を1操作単位に集約する。</summary>
public sealed class MarkSummaryBulkEffectCoordinator
{
    public MarkSummaryBulkEffectResult Execute(
        int markCount,
        bool deferSizeResolution,
        Action applyCountOnly,
        Action scheduleSummary,
        Action invalidatePending)
    {
        applyCountOnly();
        if (markCount == 0 || deferSizeResolution)
        {
            invalidatePending();
            return new MarkSummaryBulkEffectResult(1, 0, 1);
        }

        scheduleSummary();
        return new MarkSummaryBulkEffectResult(1, 1, 0);
    }
}

public readonly record struct MarkPersistencePreparation(
    IReadOnlyList<string> MarkedPaths,
    bool UsedPendingEscSnapshot,
    int SourceCount,
    int ValidationCount);

/// <summary>保存境界でmark sourceを先に確定し、順序を維持したまま一度だけ検証する。</summary>
public sealed class MarkPersistenceBoundaryCoordinator
{
    public MarkPersistencePreparation Prepare(
        bool marksDirty,
        IReadOnlyList<string> runtimeMarks,
        IReadOnlyList<string>? pendingEscMarks,
        Func<string, bool> pathExists)
    {
        bool usePending = pendingEscMarks is { Count: > 0 };
        IReadOnlyList<string> source = usePending ? pendingEscMarks! : runtimeMarks;
        var ordered = new List<string>(source.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? path in source)
        {
            if (!string.IsNullOrWhiteSpace(path) && seen.Add(path))
            {
                ordered.Add(path);
            }
        }

        if (!marksDirty && !usePending)
        {
            return new MarkPersistencePreparation(ordered, false, ordered.Count, 0);
        }

        List<string> persisted = ordered.Where(pathExists).ToList();
        return new MarkPersistencePreparation(persisted, usePending, ordered.Count, 1);
    }

    public bool ShouldRemainDirty(bool wasDirty, int validationCount, bool saveSucceeded)
    {
        return wasDirty && (validationCount == 0 || !saveSucceeded);
    }
}

public readonly record struct MarkSummaryBuildResult(long TotalSize, int FileCount, int OutsideCount);

public readonly record struct MarkSummaryExactCache(
    long TotalSize,
    int FileCount,
    int OutsideCount,
    int MarkCount);

public readonly record struct MarkSummaryDelta(
    long TotalSize,
    int FileCount,
    int OutsideCount,
    int MarkCount);

public static class MarkSummaryDeltaGate
{
    public static bool TryApply(
        MarkSummaryExactCache current,
        MarkSummaryDelta delta,
        int expectedMarkCount,
        out MarkSummaryExactCache updated)
    {
        updated = default;
        try
        {
            long totalSize = checked(current.TotalSize + delta.TotalSize);
            int fileCount = checked(current.FileCount + delta.FileCount);
            int outsideCount = checked(current.OutsideCount + delta.OutsideCount);
            int markCount = checked(current.MarkCount + delta.MarkCount);
            if (totalSize < 0 || fileCount < 0 || outsideCount < 0 || markCount < 0 || markCount != expectedMarkCount)
            {
                return false;
            }

            updated = new MarkSummaryExactCache(totalSize, fileCount, outsideCount, markCount);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}

public static class MarkSummaryOutsideCountCalculator
{
    public static int Count(IReadOnlyCollection<string> paths, string currentDirectory)
    {
        int outsideCount = 0;
        foreach (string path in paths)
        {
            string parent = NavigationService.NormalizeDirectoryForCompare(Path.GetDirectoryName(path) ?? string.Empty);
            if (!string.Equals(parent, currentDirectory, StringComparison.OrdinalIgnoreCase))
            {
                outsideCount++;
            }
        }
        return outsideCount;
    }
}

public readonly record struct MarkSummaryOrchestrationMetrics(
    int ScheduleCount,
    int BuildCount,
    int CancelCount,
    int SupersededCount,
    int ApplyCount);

/// <summary>MarkSizeのbuildとUI applyをgeneration/context単位でlatest-wins制御する。</summary>
public sealed class MarkSummaryRebuildCoordinator : IDisposable
{
    private readonly Func<string, IReadOnlyList<string>, CancellationToken, Task<MarkSummaryBuildResult>> _builder;
    private readonly Func<string> _getCurrentDirectory;
    private readonly Func<bool> _isClosed;
    private readonly Action<Action> _postToUi;
    private readonly Action<string, IReadOnlyList<string>, MarkSummaryBuildResult> _apply;
    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private long _generation;
    private bool _disposed;
    private bool _hasPending;
    private int _scheduleCount;
    private int _buildCount;
    private int _cancelCount;
    private int _supersededCount;
    private int _applyCount;

    public MarkSummaryRebuildCoordinator(
        Func<string, IReadOnlyList<string>, CancellationToken, Task<MarkSummaryBuildResult>> builder,
        Func<string> getCurrentDirectory,
        Func<bool> isClosed,
        Action<Action> postToUi,
        Action<string, IReadOnlyList<string>, MarkSummaryBuildResult> apply)
    {
        _builder = builder;
        _getCurrentDirectory = getCurrentDirectory;
        _isClosed = isClosed;
        _postToUi = postToUi;
        _apply = apply;
    }

    public bool HasPending
    {
        get
        {
            lock (_sync)
            {
                return _hasPending;
            }
        }
    }

    public Task Schedule(string currentDirectory, IReadOnlyList<string> paths)
    {
        CancellationTokenSource cts;
        long generation;
        IReadOnlyList<string> immutablePaths = paths.ToArray();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CancelCurrentLocked();
            cts = new CancellationTokenSource();
            _cts = cts;
            generation = ++_generation;
            _hasPending = true;
            _scheduleCount++;
        }

        return RunAsync(generation, currentDirectory, immutablePaths, cts);
    }

    public void Invalidate()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _generation++;
            CancelCurrentLocked();
            _hasPending = false;
        }
    }

    public MarkSummaryOrchestrationMetrics GetMetrics()
    {
        return new MarkSummaryOrchestrationMetrics(
            Volatile.Read(ref _scheduleCount),
            Volatile.Read(ref _buildCount),
            Volatile.Read(ref _cancelCount),
            Volatile.Read(ref _supersededCount),
            Volatile.Read(ref _applyCount));
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _generation++;
            CancelCurrentLocked();
            _hasPending = false;
        }
    }

    private async Task RunAsync(
        long generation,
        string currentDirectory,
        IReadOnlyList<string> paths,
        CancellationTokenSource cts)
    {
        MarkSummaryBuildResult result;
        Interlocked.Increment(ref _buildCount);
        try
        {
            result = await Task.Run(
                () => _builder(currentDirectory, paths, cts.Token),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            CompleteWithoutApply(generation);
            return;
        }
        catch
        {
            CompleteWithoutApply(generation);
            return;
        }

        if (!CanApply(generation, currentDirectory))
        {
            Interlocked.Increment(ref _supersededCount);
            CompleteWithoutApply(generation);
            return;
        }

        try
        {
            _postToUi(() =>
            {
                try
                {
                    if (!CanApply(generation, currentDirectory))
                    {
                        Interlocked.Increment(ref _supersededCount);
                        CompleteWithoutApply(generation);
                        return;
                    }

                    // UI側のapplyがHeaderを再描画するため、完了状態を先に確定する。
                    // generationが変わった場合はCompleteWithoutApplyが新しいpendingを保持する。
                    CompleteWithoutApply(generation);
                    _apply(currentDirectory, paths, result);
                    Interlocked.Increment(ref _applyCount);
                }
                catch (InvalidOperationException)
                {
                }
            });
        }
        catch (InvalidOperationException)
        {
            CompleteWithoutApply(generation);
        }
    }

    private bool CanApply(long generation, string currentDirectory)
    {
        lock (_sync)
        {
            return !_disposed
                && generation == _generation
                && !_isClosed()
                && string.Equals(currentDirectory, _getCurrentDirectory(), StringComparison.OrdinalIgnoreCase);
        }
    }

    private void CompleteWithoutApply(long generation)
    {
        lock (_sync)
        {
            if (generation == _generation)
            {
                _hasPending = false;
                _cts?.Dispose();
                _cts = null;
            }
        }
    }

    private void CancelCurrentLocked()
    {
        if (_cts == null)
        {
            return;
        }
        _cts.Cancel();
        _cts.Dispose();
        _cts = null;
        _cancelCount++;
    }
}
