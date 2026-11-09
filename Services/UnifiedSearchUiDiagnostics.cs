using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using MidFD.Models;

namespace MidFD.Services;

internal enum UnifiedSearchUiDiagnosticMetric
{
    HandleResultKey,
    SelectResult,
    RefreshHit,
    AppendBatch,
    UpdateFunctionBar,
    ApplyProgress,
    HeartbeatLag,
    QueueProgress,
    NameProgressCallbackWait,
    ContentProgressTimerLag,
    QueueProgressExchange,
    ApplyProgressExchange,
    UpdateProgress,
    ProgressText,
    CurrentPathText,
    ProgressRing,
    ProgressReschedule,
    AppendHitProcessing,
    AppendFilesSnapshot,
    AppendVirtualListSize,
    AppendInitialSelection,
    AppendChangedRowsRedraw,
    AppendHeaderUpdate,
    AppendPositionUpdate,
    RetrieveVirtualItemSample,
    DrawSubItemSample
}

internal enum UnifiedSearchUiDiagnosticCounter
{
    AppendAddedHits,
    AppendAddedFiles,
    AppendChangedRows,
    RetrieveVirtualItemCalls,
    DrawSubItemCalls
}

internal enum UnifiedSearchUiMilestone
{
    MainFormSearchStarted,
    SearchServiceEntered,
    EngineResolved,
    SearchEngineInvoked,
    BackendSelected,
    BackendFallback,
    FirstHitGenerated,
    FirstBatchNotified,
    FirstBatchAccepted,
    FirstUiApplyStarted,
    FirstUiApplyFinished,
    FirstAppendStarted,
    FirstRowsApplied,
    FirstVisibleRowDrawn,
    Terminal
}

/// <summary>Explicitly opt-in timing for locating content-search UI stalls.</summary>
internal sealed class UnifiedSearchUiDiagnostics : IDisposable
{
    internal const string EnableEnvironmentVariable = "MIDFD_UNIFIED_SEARCH_UI_DIAGNOSTICS";
    private static readonly TimeSpan ReportInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMilliseconds(250);
    private const int PaintSampleRate = 32;
    private static readonly long SlowSpanThreshold = Stopwatch.Frequency / 64;
    private static readonly AsyncLocal<UnifiedSearchUiDiagnostics?> ActiveContentSearch = new();

    private readonly Guid _sessionId;
    private readonly Action<string> _log;
    private readonly object _metricsGate = new();
    private readonly MetricBucket[] _metrics = new MetricBucket[Enum.GetValues<UnifiedSearchUiDiagnosticMetric>().Length];
    private readonly long[] _counters = new long[Enum.GetValues<UnifiedSearchUiDiagnosticCounter>().Length];
    private readonly long[] _paintCallCounters = new long[2];
    private readonly long[] _milestoneTimestamps = new long[Enum.GetValues<UnifiedSearchUiMilestone>().Length];
    private readonly object _milestoneGate = new();
    private long _startedAt = Stopwatch.GetTimestamp();
    private long _windowStartedAt;
    private long _lastHeartbeatAt;
    private long _batchCount;
    private long _sliceMaxTicks;
    private long _applyMaxTicks;
    private long _queueWaitMaxTicks;
    private long _publicationIntervalCount;
    private long _publicationIntervalTotalTicks;
    private long _publicationIntervalMaxTicks;
    private int _pendingHitMax;
    private string? _firstSlow;
    private System.Windows.Forms.Timer? _heartbeatTimer;
    private int _stopped;
    private int _started;
    private int _selectedBackend = -1;
    private int _fallbackBackend = -1;
    private string _terminal = "unknown";

    internal UnifiedSearchUiDiagnostics(Guid sessionId, Action<string>? log = null)
    {
        _sessionId = sessionId;
        _log = log ?? (message => LogService.Detail(message));
        _windowStartedAt = _startedAt;
        SafeLog($"[UnifiedSearchUI] enabled session={_sessionId} intervalMs={(int)HeartbeatInterval.TotalMilliseconds}");
    }

    internal static UnifiedSearchUiDiagnostics? TryCreate(Guid sessionId, Action<string>? log = null)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(EnableEnvironmentVariable), "1", StringComparison.Ordinal)) return null;
        return new(sessionId, log);
    }

    internal static UnifiedSearchUiDiagnostics? CurrentContentSearch => ActiveContentSearch.Value;

    internal IDisposable ActivateContentSearch()
    {
        UnifiedSearchUiDiagnostics? previous = ActiveContentSearch.Value;
        ActiveContentSearch.Value = this;
        return new ContentSearchScope(previous);
    }

    internal void StartSession()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0 || Volatile.Read(ref _stopped) != 0) return;
        long now = Stopwatch.GetTimestamp();
        _startedAt = now;
        _windowStartedAt = now;
        _lastHeartbeatAt = now;
        MarkMilestone(UnifiedSearchUiMilestone.MainFormSearchStarted, now);
    }

    internal long BeginSpan() => Stopwatch.GetTimestamp();

    internal long EndSpan(long startedAt)
        => startedAt == 0 ? 0 : Math.Max(0, Stopwatch.GetTimestamp() - startedAt);

    internal double ElapsedMilliseconds => Stopwatch.GetElapsedTime(Volatile.Read(ref _startedAt)).TotalMilliseconds;

    internal void Start(Control owner)
    {
        if (Volatile.Read(ref _stopped) != 0 || owner.IsDisposed || !owner.IsHandleCreated || owner.InvokeRequired) return;
        try
        {
            _lastHeartbeatAt = Stopwatch.GetTimestamp();
            _heartbeatTimer = new System.Windows.Forms.Timer { Interval = (int)HeartbeatInterval.TotalMilliseconds };
            _heartbeatTimer.Tick += OnHeartbeat;
            _heartbeatTimer.Start();
        }
        catch
        {
            _heartbeatTimer?.Dispose();
            _heartbeatTimer = null;
        }
    }

    internal void MarkMilestone(UnifiedSearchUiMilestone milestone)
        => MarkMilestone(milestone, Stopwatch.GetTimestamp());

    private bool MarkMilestone(UnifiedSearchUiMilestone milestone, long timestamp)
    {
        lock (_milestoneGate)
        {
            if (_stopped != 0) return false;
            int index = (int)milestone;
            if (_milestoneTimestamps[index] != 0) return false;
            _milestoneTimestamps[index] = timestamp;
            return true;
        }
    }

    internal void RecordBackendSelection(ContentSearchBackendKind backend, bool isFallback)
    {
        if (isFallback)
        {
            Interlocked.CompareExchange(ref _fallbackBackend, (int)backend, -1);
            MarkMilestone(UnifiedSearchUiMilestone.BackendFallback);
            return;
        }

        Interlocked.CompareExchange(ref _selectedBackend, (int)backend, -1);
        MarkMilestone(UnifiedSearchUiMilestone.BackendSelected);
    }

    internal void RecordSpan(UnifiedSearchUiDiagnosticMetric metric, long startedAt)
    {
        if (Volatile.Read(ref _started) == 0 || Volatile.Read(ref _stopped) != 0 || startedAt == 0) return;
        long now = Stopwatch.GetTimestamp();
        RecordElapsed(metric, startedAt, now);
    }

    internal void RecordElapsed(UnifiedSearchUiDiagnosticMetric metric, long startedAt, long endedAt)
    {
        if (Volatile.Read(ref _started) == 0 || Volatile.Read(ref _stopped) != 0 || startedAt == 0 || endedAt == 0) return;
        Record(metric, Math.Max(0, endedAt - startedAt), endedAt);
    }

    internal void RecordBatch(ContentSearchBatchSliceMetrics metrics)
    {
        long now = Stopwatch.GetTimestamp();
        string? report;
        lock (_metricsGate)
        {
            if (Volatile.Read(ref _stopped) != 0) return;
            _batchCount++;
            _sliceMaxTicks = Math.Max(_sliceMaxTicks, ToTicks(metrics.SliceDuration));
            _applyMaxTicks = Math.Max(_applyMaxTicks, ToTicks(metrics.ApplyDuration));
            _queueWaitMaxTicks = Math.Max(_queueWaitMaxTicks, ToTicks(metrics.OldestHitWait));
            _pendingHitMax = Math.Max(_pendingHitMax, metrics.PendingHitCount);
            if (metrics.PublicationInterval is { } publicationInterval)
            {
                long intervalTicks = ToTicks(publicationInterval);
                _publicationIntervalCount++;
                _publicationIntervalTotalTicks += intervalTicks;
                _publicationIntervalMaxTicks = Math.Max(_publicationIntervalMaxTicks, intervalTicks);
            }
            report = TakeReportIfDue(now);
        }
        if (report != null) SafeLog(report);
    }

    internal void RecordAppendMeasurements(
        long appendTicks,
        long? hitProcessingTicks,
        long? filesSnapshotTicks,
        long? virtualListSizeTicks,
        long? initialSelectionTicks,
        long? changedRowsRedrawTicks,
        long? headerUpdateTicks,
        long? positionUpdateTicks,
        int addedHits,
        int addedFiles,
        int changedRows)
    {
        if (Volatile.Read(ref _started) == 0 || Volatile.Read(ref _stopped) != 0) return;
        long now = Stopwatch.GetTimestamp();
        string? report;
        lock (_metricsGate)
        {
            if (Volatile.Read(ref _stopped) != 0) return;
            AddMetric(UnifiedSearchUiDiagnosticMetric.AppendBatch, appendTicks);
            if (hitProcessingTicks is { } hitTicks) AddMetric(UnifiedSearchUiDiagnosticMetric.AppendHitProcessing, hitTicks);
            if (filesSnapshotTicks is { } filesTicks) AddMetric(UnifiedSearchUiDiagnosticMetric.AppendFilesSnapshot, filesTicks);
            if (virtualListSizeTicks is { } listSizeTicks) AddMetric(UnifiedSearchUiDiagnosticMetric.AppendVirtualListSize, listSizeTicks);
            if (initialSelectionTicks is { } selectionTicks) AddMetric(UnifiedSearchUiDiagnosticMetric.AppendInitialSelection, selectionTicks);
            if (changedRowsRedrawTicks is { } redrawTicks) AddMetric(UnifiedSearchUiDiagnosticMetric.AppendChangedRowsRedraw, redrawTicks);
            if (headerUpdateTicks is { } headerTicks) AddMetric(UnifiedSearchUiDiagnosticMetric.AppendHeaderUpdate, headerTicks);
            if (positionUpdateTicks is { } positionTicks) AddMetric(UnifiedSearchUiDiagnosticMetric.AppendPositionUpdate, positionTicks);
            _counters[(int)UnifiedSearchUiDiagnosticCounter.AppendAddedHits] += addedHits;
            _counters[(int)UnifiedSearchUiDiagnosticCounter.AppendAddedFiles] += addedFiles;
            _counters[(int)UnifiedSearchUiDiagnosticCounter.AppendChangedRows] += changedRows;
            report = TakeReportIfDue(now);
        }
        if (report != null) SafeLog(report);
    }

    internal long BeginPaintSample(UnifiedSearchUiDiagnosticCounter counter)
    {
        if (Volatile.Read(ref _started) == 0 || Volatile.Read(ref _stopped) != 0) return 0;
        int index = counter switch
        {
            UnifiedSearchUiDiagnosticCounter.RetrieveVirtualItemCalls => 0,
            UnifiedSearchUiDiagnosticCounter.DrawSubItemCalls => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(counter))
        };
        long call = Interlocked.Increment(ref _paintCallCounters[index]);
        return call % PaintSampleRate == 0 ? BeginSpan() : 0;
    }

    internal void Stop(string terminal = "unknown")
    {
        lock (_milestoneGate)
        {
            if (_stopped != 0) return;
            _terminal = terminal;
            int terminalIndex = (int)UnifiedSearchUiMilestone.Terminal;
            if (_milestoneTimestamps[terminalIndex] == 0)
                _milestoneTimestamps[terminalIndex] = Stopwatch.GetTimestamp();
            _stopped = 1;
        }

        System.Windows.Forms.Timer? timer = _heartbeatTimer;
        _heartbeatTimer = null;
        if (timer != null)
        {
            timer.Stop();
            timer.Tick -= OnHeartbeat;
            timer.Dispose();
        }
        Report(Stopwatch.GetTimestamp());
        SafeLog(FormatMilestoneSummary());
    }

    public void Dispose() => Stop();

    private void OnHeartbeat(object? sender, EventArgs e)
    {
        long now = Stopwatch.GetTimestamp();
        long elapsed = Math.Max(0, now - _lastHeartbeatAt);
        _lastHeartbeatAt = now;
        Record(UnifiedSearchUiDiagnosticMetric.HeartbeatLag,
            Math.Max(0, elapsed - ToTicks(HeartbeatInterval)), now);
    }

    private void Record(UnifiedSearchUiDiagnosticMetric metric, long elapsedTicks, long now)
    {
        string? report;
        lock (_metricsGate)
        {
            if (Volatile.Read(ref _started) == 0 || Volatile.Read(ref _stopped) != 0) return;
            AddMetric(metric, elapsedTicks);
            report = TakeReportIfDue(now);
        }
        if (report != null) SafeLog(report);
    }

    private string? TakeReportIfDue(long now)
    {
        if (Stopwatch.GetElapsedTime(_windowStartedAt, now) < ReportInterval || !HasMeasurements()) return null;
        return TakeReport(now);
    }

    private void Report(long now)
    {
        string? report;
        lock (_metricsGate) report = HasMeasurements() ? TakeReport(now) : null;
        if (report != null) SafeLog(report);
    }

    private string TakeReport(long now)
    {
        var message = new StringBuilder(360)
            .Append("[UnifiedSearchUI] session=").Append(_sessionId)
            .Append(" atMs=").Append(Stopwatch.GetElapsedTime(Volatile.Read(ref _startedAt), now).TotalMilliseconds.ToString("F0"))
            .Append(" key=").Append(Format(UnifiedSearchUiDiagnosticMetric.HandleResultKey))
            .Append(" select=").Append(Format(UnifiedSearchUiDiagnosticMetric.SelectResult))
            .Append(" refresh=").Append(Format(UnifiedSearchUiDiagnosticMetric.RefreshHit))
            .Append(" append=").Append(Format(UnifiedSearchUiDiagnosticMetric.AppendBatch))
            .Append(" functionBar=").Append(Format(UnifiedSearchUiDiagnosticMetric.UpdateFunctionBar))
            .Append(" progress=").Append(Format(UnifiedSearchUiDiagnosticMetric.ApplyProgress))
            .Append(" queueProgress=").Append(Format(UnifiedSearchUiDiagnosticMetric.QueueProgress))
            .Append(" nameProgressCallbackWait=").Append(Format(UnifiedSearchUiDiagnosticMetric.NameProgressCallbackWait))
            .Append(" progressTimerLag=").Append(Format(UnifiedSearchUiDiagnosticMetric.ContentProgressTimerLag))
            .Append(" queueProgressExchange=").Append(Format(UnifiedSearchUiDiagnosticMetric.QueueProgressExchange))
            .Append(" applyProgressExchange=").Append(Format(UnifiedSearchUiDiagnosticMetric.ApplyProgressExchange))
            .Append(" updateProgress=").Append(Format(UnifiedSearchUiDiagnosticMetric.UpdateProgress))
            .Append(" progressText=").Append(Format(UnifiedSearchUiDiagnosticMetric.ProgressText))
            .Append(" currentPathText=").Append(Format(UnifiedSearchUiDiagnosticMetric.CurrentPathText))
            .Append(" progressRing=").Append(Format(UnifiedSearchUiDiagnosticMetric.ProgressRing))
            .Append(" progressReschedule=").Append(Format(UnifiedSearchUiDiagnosticMetric.ProgressReschedule))
            .Append(" appendHitProcess=").Append(Format(UnifiedSearchUiDiagnosticMetric.AppendHitProcessing))
            .Append(" appendFilesSnapshot=").Append(Format(UnifiedSearchUiDiagnosticMetric.AppendFilesSnapshot))
            .Append(" appendListSize=").Append(Format(UnifiedSearchUiDiagnosticMetric.AppendVirtualListSize))
            .Append(" appendInitialSelect=").Append(Format(UnifiedSearchUiDiagnosticMetric.AppendInitialSelection))
            .Append(" appendRedrawItems=").Append(Format(UnifiedSearchUiDiagnosticMetric.AppendChangedRowsRedraw))
            .Append(" appendHeader=").Append(Format(UnifiedSearchUiDiagnosticMetric.AppendHeaderUpdate))
            .Append(" appendPosition=").Append(Format(UnifiedSearchUiDiagnosticMetric.AppendPositionUpdate))
            .Append(" retrieveVirtualItem=").Append(Format(UnifiedSearchUiDiagnosticMetric.RetrieveVirtualItemSample))
            .Append(" retrieveCalls=").Append(Interlocked.Exchange(ref _paintCallCounters[0], 0))
            .Append(" drawSubItem=").Append(Format(UnifiedSearchUiDiagnosticMetric.DrawSubItemSample))
            .Append(" drawCalls=").Append(Interlocked.Exchange(ref _paintCallCounters[1], 0))
            .Append(" addedHits=").Append(TakeCounter(UnifiedSearchUiDiagnosticCounter.AppendAddedHits))
            .Append(" addedFiles=").Append(TakeCounter(UnifiedSearchUiDiagnosticCounter.AppendAddedFiles))
            .Append(" changedRows=").Append(TakeCounter(UnifiedSearchUiDiagnosticCounter.AppendChangedRows))
            .Append(" heartbeatLag=").Append(Format(UnifiedSearchUiDiagnosticMetric.HeartbeatLag))
            .Append(" batch=").Append(_batchCount)
            .Append(" publishInterval=").Append(FormatPublicationInterval())
            .Append(" sliceMaxMs=").Append(ToMilliseconds(_sliceMaxTicks).ToString("F1"))
            .Append(" applyMaxMs=").Append(ToMilliseconds(_applyMaxTicks).ToString("F1"))
            .Append(" queueWaitMaxMs=").Append(ToMilliseconds(_queueWaitMaxTicks).ToString("F1"))
            .Append(" pendingHitsMax=").Append(_pendingHitMax);
        if (_firstSlow != null) message.Append(" firstSlow=").Append(_firstSlow);
        Array.Clear(_metrics);
        _batchCount = 0;
        _sliceMaxTicks = 0;
        _applyMaxTicks = 0;
        _queueWaitMaxTicks = 0;
        _pendingHitMax = 0;
        _publicationIntervalCount = 0;
        _publicationIntervalTotalTicks = 0;
        _publicationIntervalMaxTicks = 0;
        _firstSlow = null;
        _windowStartedAt = now;
        return message.ToString();
    }

    private string FormatMilestoneSummary()
    {
        long startedAt = Volatile.Read(ref _startedAt);
        var entries = new List<(long Timestamp, string Value)>();
        for (int index = 0; index < _milestoneTimestamps.Length; index++)
        {
            long timestamp = Volatile.Read(ref _milestoneTimestamps[index]);
            if (timestamp == 0) continue;
            string name = ((UnifiedSearchUiMilestone)index).ToString();
            entries.Add((timestamp, $"{name}:{Stopwatch.GetElapsedTime(startedAt, timestamp).TotalMilliseconds:F1}"));
        }

        entries.Sort((left, right) => left.Timestamp.CompareTo(right.Timestamp));
        long latest = entries.Count == 0 ? 0 : entries[^1].Timestamp;
        string lastStage = entries.Count == 0 ? "none" : entries[^1].Value;
        var summary = new StringBuilder(320)
            .Append("[UnifiedSearchFirst] session=").Append(_sessionId)
            .Append(" terminal=").Append(_terminal)
            .Append(" backend=").Append(FormatBackend(Volatile.Read(ref _selectedBackend)))
            .Append(" fallback=").Append(FormatBackend(Volatile.Read(ref _fallbackBackend)))
            .Append(" lastStage=").Append(lastStage)
            .Append(" lastAtMs=").Append(latest == 0 ? "none" : Stopwatch.GetElapsedTime(startedAt, latest).TotalMilliseconds.ToString("F1"))
            .Append(" milestones=").Append(entries.Count == 0 ? "none" : string.Join(',', entries.Select(entry => entry.Value)));
        return summary.ToString();
    }

    private string Format(UnifiedSearchUiDiagnosticMetric metric)
    {
        MetricBucket bucket = _metrics[(int)metric];
        if (bucket.Count == 0) return "0/0.0/0.0";
        return $"{bucket.Count}/{ToMilliseconds(bucket.TotalTicks / bucket.Count):F1}/{ToMilliseconds(bucket.MaxTicks):F1}";
    }

    private string FormatPublicationInterval()
    {
        if (_publicationIntervalCount == 0) return "0/0.0/0.0";
        return $"{_publicationIntervalCount}/{ToMilliseconds(_publicationIntervalTotalTicks / _publicationIntervalCount):F1}/{ToMilliseconds(_publicationIntervalMaxTicks):F1}";
    }

    private bool HasMeasurements()
    {
        if (_batchCount > 0 || _metrics.Any(bucket => bucket.Count > 0) || _counters.Any(count => count > 0)) return true;
        for (int index = 0; index < _paintCallCounters.Length; index++)
        {
            if (Interlocked.Read(ref _paintCallCounters[index]) > 0) return true;
        }
        return false;
    }

    private long TakeCounter(UnifiedSearchUiDiagnosticCounter counter)
    {
        int index = (int)counter;
        long value = _counters[index];
        _counters[index] = 0;
        return value;
    }

    private void AddMetric(UnifiedSearchUiDiagnosticMetric metric, long elapsedTicks)
    {
        int index = (int)metric;
        MetricBucket bucket = _metrics[index];
        bucket.Count++;
        bucket.TotalTicks += elapsedTicks;
        bucket.MaxTicks = Math.Max(bucket.MaxTicks, elapsedTicks);
        _metrics[index] = bucket;
        if (_firstSlow == null && elapsedTicks >= SlowSpanThreshold)
            _firstSlow = $"{metric}={ToMilliseconds(elapsedTicks):F1}ms";
    }

    private void SafeLog(string message)
    {
        try { _log(message); }
        catch { /* Opt-in diagnostics must not affect search or UI behavior. */ }
    }

    private static string FormatBackend(int value)
        => value < 0 ? "none" : ((ContentSearchBackendKind)value).ToString();

    private static long ToTicks(TimeSpan duration)
        => (long)Math.Max(0, duration.TotalSeconds * Stopwatch.Frequency);

    private static double ToMilliseconds(long ticks)
        => Stopwatch.GetElapsedTime(0, ticks).TotalMilliseconds;

    private struct MetricBucket
    {
        internal long Count;
        internal long TotalTicks;
        internal long MaxTicks;
    }

    private sealed class ContentSearchScope(UnifiedSearchUiDiagnostics? previous) : IDisposable
    {
        public void Dispose() => ActiveContentSearch.Value = previous;
    }
}
