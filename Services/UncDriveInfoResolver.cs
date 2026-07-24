using System.Runtime.InteropServices;

namespace MidFD.Services;

public sealed class UncDriveInfoResolver : IDisposable
{
    public readonly record struct Result(long Used, long Free);

    private readonly object _gate = new();
    private readonly Dictionary<string, Result> _cache = new(StringComparer.OrdinalIgnoreCase);
    private System.Threading.Timer? _timer;
    private string? _pendingRoot;
    private long _pendingGeneration;
    private Action<string, long, Result, bool>? _pendingCallback;
    private long _requestVersion;
    private bool _inFlight;
    private bool _disposed;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceExW(
        string? lpDirectoryName,
        out ulong lpFreeBytesAvailable,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);

    public bool TryGetCached(string root, out Result result)
    {
        lock (_gate)
        {
            return _cache.TryGetValue(root, out result);
        }
    }

    public void Schedule(string root, long generation, Action<string, long, Result, bool> callback)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed || _cache.ContainsKey(root))
            {
                return;
            }

            _pendingRoot = root;
            _pendingGeneration = generation;
            _pendingCallback = callback;
            _requestVersion++;
            if (_inFlight)
            {
                return;
            }

            _timer ??= new System.Threading.Timer(StartProbe, null, Timeout.Infinite, Timeout.Infinite);
            _timer.Change(1000, Timeout.Infinite);
        }
    }

    public void CancelPending()
    {
        lock (_gate)
        {
            _pendingRoot = null;
            _pendingCallback = null;
            _requestVersion++;
            if (!_inFlight)
            {
                _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }
    }

    private void StartProbe(object? state)
    {
        string root;
        long generation;
        Action<string, long, Result, bool>? callback;
        long requestVersion;
        lock (_gate)
        {
            if (_disposed || _inFlight || _pendingRoot == null || _pendingCallback == null)
            {
                return;
            }

            root = _pendingRoot;
            generation = _pendingGeneration;
            callback = _pendingCallback;
            requestVersion = _requestVersion;
            _pendingRoot = null;
            _pendingCallback = null;
            _inFlight = true;
        }

        _ = Task.Run(() => Probe(root)).ContinueWith(task =>
        {
            Result result = default;
            bool succeeded = false;
            if (!task.IsCanceled && !task.IsFaulted)
            {
                result = task.Result;
                succeeded = true;
            }

            Action<string, long, Result, bool>? completionCallback = null;
            lock (_gate)
            {
                _inFlight = false;
                if (succeeded)
                {
                    _cache[root] = result;
                }

                if (_pendingRoot != null && _pendingCallback != null)
                {
                    if (!string.Equals(_pendingRoot, root, StringComparison.OrdinalIgnoreCase))
                    {
                        _timer ??= new System.Threading.Timer(StartProbe, null, Timeout.Infinite, Timeout.Infinite);
                        _timer.Change(1000, Timeout.Infinite);
                    }
                    else
                    {
                        completionCallback = _pendingCallback;
                        generation = _pendingGeneration;
                        _pendingRoot = null;
                        _pendingCallback = null;
                    }
                }
                else if (succeeded && _requestVersion == requestVersion)
                {
                    completionCallback = callback;
                }
            }

            completionCallback?.Invoke(root, generation, result, succeeded);
        }, TaskScheduler.Default);
    }

    private static Result Probe(string root)
    {
        if (!GetDiskFreeSpaceExW(root, out _, out ulong total, out ulong free))
        {
            throw new IOException($"GetDiskFreeSpaceExW failed: {Marshal.GetLastWin32Error()}");
        }

        ulong used = total >= free ? total - free : 0;
        return new Result(checked((long)used), checked((long)free));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pendingRoot = null;
            _pendingCallback = null;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
