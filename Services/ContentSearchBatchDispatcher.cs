using System.Diagnostics;
using MidFD.Models;

namespace MidFD.Services;

/// <summary>Drains search hits on the UI thread in bounded slices while preserving session ownership.</summary>
internal sealed class ContentSearchBatchDispatcher
{
    internal const int DefaultMaxHitsPerSlice = 128;
    internal static readonly TimeSpan DefaultSliceWorkBudget = TimeSpan.FromMilliseconds(8);
    internal static readonly TimeSpan DefaultUiPublicationInterval = TimeSpan.FromMilliseconds(100);

    private readonly Func<bool> _canAccept;
    private readonly Func<bool> _canApply;
    private readonly Action<Action, TimeSpan?, Action<Exception>> _schedule;
    private readonly Action<ContentSearchBatch> _apply;
    private readonly Action<ContentSearchBatchSliceMetrics>? _observeSlice;
    private readonly UnifiedSearchUiDiagnostics? _uiDiagnostics;
    private readonly int _maxHitsPerSlice;
    private readonly TimeSpan _sliceWorkBudget;
    private readonly TimeSpan _minimumUiPublicationInterval;
    private readonly Func<long> _getTimestamp;
    private readonly object _gate = new();
    private readonly Queue<PendingBatch> _pending = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _pendingHitCount;
    private bool _isScheduled;
    private bool _inputCompleted;
    private bool _abandoned;
    private bool _hasPublished;
    private long _lastPublishedTimestamp;

    internal ContentSearchBatchDispatcher(
        Func<bool> canAccept,
        Func<bool> canApply,
        Action<Action, TimeSpan?, Action<Exception>> schedule,
        Action<ContentSearchBatch> apply,
        Action<ContentSearchBatchSliceMetrics>? observeSlice = null,
        int maxHitsPerSlice = DefaultMaxHitsPerSlice,
        TimeSpan? sliceWorkBudget = null,
        TimeSpan? minimumUiPublicationInterval = null,
        Func<long>? getTimestamp = null,
        UnifiedSearchUiDiagnostics? uiDiagnostics = null)
    {
        _canAccept = canAccept ?? throw new ArgumentNullException(nameof(canAccept));
        _canApply = canApply ?? throw new ArgumentNullException(nameof(canApply));
        _schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        _observeSlice = observeSlice;
        _uiDiagnostics = uiDiagnostics;
        _maxHitsPerSlice = maxHitsPerSlice > 0 ? maxHitsPerSlice : throw new ArgumentOutOfRangeException(nameof(maxHitsPerSlice));
        _sliceWorkBudget = sliceWorkBudget ?? DefaultSliceWorkBudget;
        _minimumUiPublicationInterval = minimumUiPublicationInterval ?? DefaultUiPublicationInterval;
        _getTimestamp = getTimestamp ?? Stopwatch.GetTimestamp;
        if (_sliceWorkBudget < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(sliceWorkBudget));
        if (_minimumUiPublicationInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minimumUiPublicationInterval));
    }

    internal void Queue(ContentSearchBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Matches.Count == 0 || !_canAccept()) return;

        bool schedule = false;
        TimeSpan? scheduleDelay = null;
        lock (_gate)
        {
            if (_inputCompleted || _abandoned || !_canAccept()) return;
            _pending.Enqueue(new PendingBatch(batch, _getTimestamp()));
            _pendingHitCount += batch.Matches.Count;
            if (!_isScheduled)
            {
                _isScheduled = true;
                schedule = true;
                scheduleDelay = GetPublicationDelayLocked(_getTimestamp());
            }
        }

        _uiDiagnostics?.MarkMilestone(UnifiedSearchUiMilestone.FirstBatchAccepted);
        if (schedule) ScheduleDrain(scheduleDelay);
    }

    /// <summary>Stops accepting producer batches and completes only after every accepted hit is applied.</summary>
    internal Task CompleteInputAsync()
    {
        bool schedule = false;
        TimeSpan? scheduleDelay = null;
        bool complete = false;
        lock (_gate)
        {
            _inputCompleted = true;
            if (_abandoned || (_pendingHitCount == 0 && !_isScheduled))
            {
                complete = true;
            }
            else if (_pendingHitCount > 0 && !_isScheduled)
            {
                _isScheduled = true;
                schedule = true;
                scheduleDelay = GetPublicationDelayLocked(_getTimestamp());
            }
        }

        if (complete) _completion.TrySetResult();
        if (schedule) ScheduleDrain(scheduleDelay);
        return _completion.Task;
    }

    /// <summary>Discards queued hits only when the owning session is detached or disposed.</summary>
    internal void Abandon()
    {
        lock (_gate)
        {
            _abandoned = true;
            _inputCompleted = true;
            _pending.Clear();
            _pendingHitCount = 0;
            _isScheduled = false;
        }
        _completion.TrySetResult();
    }

    private void DrainSlice()
    {
        if (!_canApply())
        {
            Abandon();
            return;
        }

        long sliceStarted = Stopwatch.GetTimestamp();
        long queueStartedAt = _getTimestamp();
        long oldestEnqueued = 0;
        var hits = new List<UnifiedSearchMatch>(_maxHitsPerSlice);
        lock (_gate)
        {
            if (_abandoned) return;
            while (_pending.Count > 0 && hits.Count < _maxHitsPerSlice)
            {
                if (hits.Count > 0 && Stopwatch.GetElapsedTime(sliceStarted) >= _sliceWorkBudget) break;
                PendingBatch current = _pending.Peek();
                if (oldestEnqueued == 0) oldestEnqueued = current.EnqueuedTimestamp;
                hits.Add(current.Batch.Matches[current.Offset++]);
                _pendingHitCount--;
                if (current.Offset >= current.Batch.Matches.Count) _pending.Dequeue();
            }
        }

        TimeSpan applyDuration = TimeSpan.Zero;
        long publishedAt = 0;
        if (hits.Count > 0)
        {
            if (!_canApply())
            {
                Abandon();
                return;
            }

            long applyStarted = Stopwatch.GetTimestamp();
            _uiDiagnostics?.MarkMilestone(UnifiedSearchUiMilestone.FirstUiApplyStarted);
            try
            {
                _apply(new ContentSearchBatch(hits));
            }
            catch (Exception ex)
            {
                Fail(ex);
                return;
            }
            finally
            {
                _uiDiagnostics?.MarkMilestone(UnifiedSearchUiMilestone.FirstUiApplyFinished);
            }
            applyDuration = Stopwatch.GetElapsedTime(applyStarted);
            publishedAt = _getTimestamp();
        }

        bool scheduleNext = false;
        bool complete = false;
        TimeSpan? scheduleDelayNext = null;
        TimeSpan? publicationInterval = null;
        int pendingHitCount;
        int pendingBatchCount;
        lock (_gate)
        {
            if (_abandoned) return;
            if (hits.Count > 0)
            {
                if (_hasPublished)
                    publicationInterval = Stopwatch.GetElapsedTime(_lastPublishedTimestamp, publishedAt);
                _lastPublishedTimestamp = publishedAt;
                _hasPublished = true;
            }
            pendingHitCount = _pendingHitCount;
            pendingBatchCount = _pending.Count;
            if (pendingHitCount > 0)
            {
                scheduleNext = true;
                scheduleDelayNext = GetPublicationDelayLocked(_getTimestamp());
            }
            else
            {
                _isScheduled = false;
                complete = _inputCompleted;
            }
        }

        if (hits.Count > 0)
        {
            var metrics = new ContentSearchBatchSliceMetrics(
                hits.Count,
                pendingHitCount,
                pendingBatchCount,
                oldestEnqueued == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(oldestEnqueued, queueStartedAt),
                applyDuration,
                Stopwatch.GetElapsedTime(sliceStarted),
                publicationInterval);
            try { _observeSlice?.Invoke(metrics); }
            catch { /* Diagnostics must not change result delivery. */ }
        }

        if (complete) _completion.TrySetResult();
        if (scheduleNext) ScheduleDrain(scheduleDelayNext);
    }

    private TimeSpan? GetPublicationDelayLocked(long now)
    {
        if (!_hasPublished) return null;
        long minimumTicks = (long)Math.Ceiling(_minimumUiPublicationInterval.TotalSeconds * Stopwatch.Frequency);
        long remainingTicks = minimumTicks - (now - _lastPublishedTimestamp);
        return remainingTicks > 0
            ? TimeSpan.FromSeconds((double)remainingTicks / Stopwatch.Frequency)
            : null;
    }

    private void ScheduleDrain(TimeSpan? delay)
    {
        try
        {
            _schedule(DrainSlice, delay, Fail);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private void Fail(Exception exception)
    {
        if (!_canApply())
        {
            Abandon();
            return;
        }

        lock (_gate)
        {
            if (_abandoned) return;
            _inputCompleted = true;
            _pending.Clear();
            _pendingHitCount = 0;
            _isScheduled = false;
        }
        _completion.TrySetException(exception);
    }

    private sealed class PendingBatch(ContentSearchBatch batch, long enqueuedTimestamp)
    {
        internal ContentSearchBatch Batch { get; } = batch;
        internal long EnqueuedTimestamp { get; } = enqueuedTimestamp;
        internal int Offset { get; set; }
    }
}

internal readonly record struct ContentSearchBatchSliceMetrics(
    int HitCount,
    int PendingHitCount,
    int PendingBatchCount,
    TimeSpan OldestHitWait,
    TimeSpan ApplyDuration,
    TimeSpan SliceDuration,
    TimeSpan? PublicationInterval);
