using System.Collections.Concurrent;
using MidFD.Models;

namespace MidFD.Services;

/// <summary>UI-thread delivery guarded by the owning search session, including queued callbacks.</summary>
internal sealed class ContentSearchBatchDispatcher(Func<bool> isCurrent, Action<Action> schedule, Action<ContentSearchBatch> apply)
{
    private readonly ConcurrentQueue<ContentSearchBatch> _queue = new();
    private int _scheduled;

    public void Queue(ContentSearchBatch batch)
    {
        if (!isCurrent()) return;
        _queue.Enqueue(batch);
        if (Interlocked.Exchange(ref _scheduled, 1) != 0) return;
        try { schedule(Drain); }
        catch (InvalidOperationException) { _queue.Clear(); Interlocked.Exchange(ref _scheduled, 0); }
    }

    public void Drain()
    {
        if (!isCurrent()) { _queue.Clear(); Interlocked.Exchange(ref _scheduled, 0); return; }
        var hits = new List<UnifiedSearchMatch>();
        while (_queue.TryDequeue(out var batch)) hits.AddRange(batch.Matches);
        Interlocked.Exchange(ref _scheduled, 0);
        if (hits.Count > 0 && isCurrent()) apply(new(hits));
        if (!_queue.IsEmpty && isCurrent() && Interlocked.Exchange(ref _scheduled, 1) == 0) schedule(Drain);
    }
}
