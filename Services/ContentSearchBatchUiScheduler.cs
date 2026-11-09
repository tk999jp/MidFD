using System.Windows.Forms;

namespace MidFD.Services;

/// <summary>Posts work to a control and creates one-shot delayed drains on its UI thread.</summary>
internal sealed class ContentSearchBatchUiScheduler : IDisposable
{
    private readonly Control _owner;
    private readonly object _gate = new();
    private System.Windows.Forms.Timer? _timer;
    private EventHandler? _timerTick;
    private Action? _scheduledCallback;
    private Action<Exception>? _scheduledFailure;
    private long _scheduleId;
    private bool _disposed;

    internal ContentSearchBatchUiScheduler(Control owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _owner.Disposed += OnOwnerDisposed;
    }

    internal void Schedule(Action callback, TimeSpan? delay, Action<Exception> onFailure)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentNullException.ThrowIfNull(onFailure);
        long scheduleId;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_owner.IsDisposed || !_owner.IsHandleCreated)
                throw new InvalidOperationException("The search batch UI owner is not available.");
            if (_scheduledCallback != null)
                throw new InvalidOperationException("A search batch drain is already scheduled.");
            _scheduledCallback = callback;
            _scheduledFailure = onFailure;
            scheduleId = ++_scheduleId;
        }

        try
        {
            if (delay is not { } interval || interval <= TimeSpan.Zero)
            {
                PostToOwner(() => RunScheduledCallback(scheduleId));
                return;
            }

            if (_owner.InvokeRequired)
                PostToOwner(() => StartDelayedCallbackSafely(scheduleId, interval));
            else
                StartDelayedCallback(scheduleId, interval);
        }
        catch
        {
            ClearReservation(scheduleId);
            throw;
        }
    }

    public void Dispose()
    {
        if (_owner.InvokeRequired)
            throw new InvalidOperationException("The search batch UI scheduler must be disposed on its UI thread.");
        DisposeCore(failPendingReservation: false);
    }

    private void PostToOwner(Action callback)
    {
        if (_owner.IsDisposed || !_owner.IsHandleCreated)
            throw new InvalidOperationException("The search batch UI owner is not available.");
        _owner.BeginInvoke((MethodInvoker)(() =>
        {
            lock (_gate)
            {
                if (_disposed) return;
            }
            callback();
        }));
    }

    private void StartDelayedCallback(long scheduleId, TimeSpan delay)
    {
        if (_owner.InvokeRequired)
            throw new InvalidOperationException("A delayed search batch drain must be created on the UI thread.");
        if (_owner.IsDisposed || !_owner.IsHandleCreated)
        {
            RunScheduledCallback(scheduleId);
            return;
        }

        int intervalMilliseconds = (int)Math.Clamp(Math.Ceiling(delay.TotalMilliseconds), 1, int.MaxValue);
        var timer = new System.Windows.Forms.Timer { Interval = intervalMilliseconds };
        EventHandler? tick = null;
        tick = (_, _) =>
        {
            timer.Stop();
            timer.Tick -= tick;
            lock (_gate)
            {
                if (ReferenceEquals(_timer, timer))
                {
                    _timer = null;
                    _timerTick = null;
                }
            }
            timer.Dispose();
            RunScheduledCallback(scheduleId);
        };

        lock (_gate)
        {
            if (_disposed || scheduleId != _scheduleId || _scheduledCallback == null)
            {
                timer.Dispose();
                return;
            }
            if (_timer != null)
            {
                timer.Dispose();
                throw new InvalidOperationException("A delayed search batch drain is already scheduled.");
            }
            _timer = timer;
            _timerTick = tick;
            timer.Tick += tick;
        }

        try { timer.Start(); }
        catch
        {
            timer.Tick -= tick;
            lock (_gate)
            {
                if (ReferenceEquals(_timer, timer))
                {
                    _timer = null;
                    _timerTick = null;
                }
            }
            timer.Dispose();
            throw;
        }
    }

    private void StartDelayedCallbackSafely(long scheduleId, TimeSpan delay)
    {
        try { StartDelayedCallback(scheduleId, delay); }
        catch (Exception ex) { FailScheduledCallback(scheduleId, ex); }
    }

    private void RunScheduledCallback(long scheduleId)
    {
        Action? callback;
        lock (_gate)
        {
            if (_disposed || scheduleId != _scheduleId) return;
            callback = _scheduledCallback;
            _scheduledCallback = null;
            _scheduledFailure = null;
        }
        callback?.Invoke();
    }

    private void FailScheduledCallback(long scheduleId, Exception exception)
    {
        Action<Exception>? failure;
        System.Windows.Forms.Timer? timer;
        EventHandler? tick;
        lock (_gate)
        {
            if (_disposed || scheduleId != _scheduleId) return;
            failure = _scheduledFailure;
            _scheduledCallback = null;
            _scheduledFailure = null;
            timer = _timer;
            _timer = null;
            tick = _timerTick;
            _timerTick = null;
            _scheduleId++;
        }
        if (timer != null)
        {
            timer.Stop();
            if (tick != null) timer.Tick -= tick;
            timer.Dispose();
        }
        failure?.Invoke(exception);
    }

    private void ClearReservation(long scheduleId)
    {
        lock (_gate)
        {
            if (scheduleId == _scheduleId)
            {
                _scheduledCallback = null;
                _scheduledFailure = null;
            }
        }
    }

    private void OnOwnerDisposed(object? sender, EventArgs e)
    {
        _owner.Disposed -= OnOwnerDisposed;
        DisposeCore(failPendingReservation: true);
    }

    private void DisposeCore(bool failPendingReservation)
    {
        Action<Exception>? failure;
        System.Windows.Forms.Timer? timer;
        EventHandler? tick;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _scheduleId++;
            failure = failPendingReservation ? _scheduledFailure : null;
            _scheduledCallback = null;
            _scheduledFailure = null;
            timer = _timer;
            _timer = null;
            tick = _timerTick;
            _timerTick = null;
        }

        _owner.Disposed -= OnOwnerDisposed;
        if (timer != null)
        {
            timer.Stop();
            if (tick != null) timer.Tick -= tick;
            timer.Dispose();
        }
        failure?.Invoke(new ObjectDisposedException(nameof(ContentSearchBatchUiScheduler)));
    }
}
