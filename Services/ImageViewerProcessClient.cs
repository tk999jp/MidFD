using System.Diagnostics;
using System.Collections.Concurrent;
using System.Drawing;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using MidFD.Commands;
using MidFD.Services;
using MidFD.ViewerIpc;

namespace MidFD;

internal enum ImageViewerProcessState
{
    Starting,
    Ready,
    Failed,
    Closed,
    Disposed
}

internal sealed class ImageViewerLoadRequestTracker(Guid viewerId)
{
    private readonly object _gate = new();
    private long _nextRequestId;
    private long _currentRequestId;
    private string? _currentPath;
    private string? _currentMediaKind;
    private bool _currentIsSelectionFollow;
    private string? _displayedPath;
    private string? _displayedMediaKind;
    private bool _hasLoadedImage;

    public string? CurrentPath
    {
        get { lock (_gate) return _currentPath; }
    }

    public long CurrentRequestId
    {
        get { lock (_gate) return _currentRequestId; }
    }

    public string? CurrentMediaKind
    {
        get { lock (_gate) return _currentMediaKind; }
    }

    public bool CurrentIsSelectionFollow
    {
        get { lock (_gate) return _currentIsSelectionFollow; }
    }

    public bool HasLoadedImage
    {
        get { lock (_gate) return _hasLoadedImage; }
    }

    public string? DisplayedPath
    {
        get { lock (_gate) return _displayedPath; }
    }

    public string? DisplayedMediaKind
    {
        get { lock (_gate) return _displayedMediaKind; }
    }

    public bool HasDisplayedMedia
    {
        get { lock (_gate) return _displayedPath != null && _displayedMediaKind != null; }
    }

    public long Begin(string path, string mediaKind, bool isSelectionFollow = false)
    {
        lock (_gate)
        {
            long requestId = checked(++_nextRequestId);
            _currentRequestId = requestId;
            _currentPath = path;
            _currentMediaKind = mediaKind;
            _currentIsSelectionFollow = isSelectionFollow;
            _hasLoadedImage = false;
            return requestId;
        }
    }

    public long Begin(string path) => Begin(path, "Image");

    public bool TryComplete(ViewerIpcMessage message)
    {
        lock (_gate)
        {
            if (message.ViewerId != viewerId
                || message.RequestId != _currentRequestId
                || !string.Equals(message.Path, _currentPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            _hasLoadedImage = message.Success == true && message.Cancelled != true;
            if (_hasLoadedImage)
            {
                _displayedPath = _currentPath;
                _displayedMediaKind = _currentMediaKind;
            }
            return true;
        }
    }

    public void ClearLoadedState()
    {
        lock (_gate)
        {
            _hasLoadedImage = false;
        }
    }

    public bool InvalidateMedia(long? throughRequestId = null)
    {
        lock (_gate)
        {
            if (throughRequestId.HasValue && _currentRequestId > throughRequestId.Value) return false;
            _currentPath = null;
            _currentMediaKind = null;
            _currentIsSelectionFollow = false;
            _displayedPath = null;
            _displayedMediaKind = null;
            _hasLoadedImage = false;
            return true;
        }
    }
}

internal sealed class ImageViewerProcessClient : IDisposable
{
    internal enum ViewerWindowActivationAction
    {
        Rejected,
        AlreadyForeground,
        Attempted
    }

    internal readonly record struct ViewerWindowActivationState(
        Guid ExpectedViewerId,
        Guid MessageViewerId,
        long? RequestId,
        long CurrentRequestId,
        bool IsSelectionFollow,
        bool LoadSucceeded,
        bool LoadCancelled,
        string? MessagePath,
        string? MessageMediaKind,
        bool HasLoadedImage,
        bool IsVisible,
        bool IsMinimized,
        string? CurrentMediaKind,
        string? DisplayedMediaKind,
        string? CurrentPath,
        string? DisplayedPath,
        bool IsOwnerUiThread,
        bool IsProcessAlive,
        int ProcessId,
        long? ViewerWindowHandle,
        IntPtr ExpectedForegroundWindow);

    internal readonly record struct ViewerWindowActivationCallResult(bool Accepted, int LastError);

    internal readonly record struct ViewerWindowActivationResult(
        ViewerWindowActivationAction Action,
        string? RejectedReason,
        IntPtr ViewerWindow,
        IntPtr ForegroundBefore,
        bool? SetForegroundWindowAccepted,
        int? SetForegroundWindowError,
        IntPtr ForegroundAfter);

    internal sealed record ViewerWindowActivationApi(
        Func<IntPtr, bool> IsWindow,
        Func<IntPtr, bool> IsWindowVisible,
        Func<IntPtr, bool> IsIconic,
        Func<IntPtr, IntPtr> GetAncestor,
        Func<IntPtr, (uint ThreadId, uint ProcessId)> GetWindowThreadProcessId,
        Func<IntPtr> GetForegroundWindow,
        Func<IntPtr, ViewerWindowActivationCallResult> SetForegroundWindow);

    private const uint GaRoot = 2;

    private static readonly ViewerWindowActivationApi NativeViewerWindowActivationApi = new(
        IsWindow,
        IsWindowVisible,
        IsIconic,
        handle => GetAncestor(handle, GaRoot),
        handle =>
        {
            uint threadId = GetWindowThreadProcessId(handle, out uint processId);
            return (threadId, processId);
        },
        GetForegroundWindow,
        handle =>
        {
            bool accepted = SetForegroundWindow(handle);
            return new ViewerWindowActivationCallResult(accepted, accepted ? 0 : Marshal.GetLastWin32Error());
        });

    private readonly Control _owner;
    private readonly Process _process;
    private readonly int _processId;
    private readonly long _processStartedAt;
    private readonly ConcurrentDictionary<long, long> _loadRequestStartedAt = new();
    private readonly NamedPipeServerStream _pipe;
    private readonly ImageViewerLoadRequestTracker _loadRequests;
    private readonly Func<int, bool>? _allowForegroundWindowOverride;
    private readonly Action<ViewerIpcMessage>? _outgoingMessageObserver;
    private readonly Channel<ViewerIpcMessage> _outgoing = Channel.CreateUnbounded<ViewerIpcMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _sendLock = new();
    private StreamWriter? _writer;
    private volatile bool _connected;
    private volatile bool _disposed;
    private volatile bool _visible;
    private long _visibilityGeneration;
    private int _state = (int)ImageViewerProcessState.Starting;
    private int _terminalEventRaised;
    private int _startupFailureHandled;
    private string? _failureMessage;
    private Rectangle _bounds;
    private FormWindowState _windowState = FormWindowState.Normal;

    private ImageViewerProcessClient(
        Control owner,
        Guid instanceId,
        Process process,
        NamedPipeServerStream pipe,
        Rectangle bounds,
        TimeSpan readyTimeout,
        Func<int, bool>? allowForegroundWindowOverride,
        Action<ViewerIpcMessage>? outgoingMessageObserver,
        long processStartedAt)
    {
        _owner = owner;
        InstanceId = instanceId;
        _process = process;
        _processId = process.Id;
        _processStartedAt = processStartedAt;
        _pipe = pipe;
        _bounds = bounds;
        _allowForegroundWindowOverride = allowForegroundWindowOverride;
        _outgoingMessageObserver = outgoingMessageObserver;
        _loadRequests = new ImageViewerLoadRequestTracker(instanceId);
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) => HandleProcessExited();
        _ = RunPipeAsync(readyTimeout);
    }

    public Guid InstanceId { get; }
    public int ProcessId => _processId;
    public ImageViewerProcessState State => (ImageViewerProcessState)Volatile.Read(ref _state);
    public string? FailureMessage => _failureMessage;
    public string? CurrentPath => _loadRequests.CurrentPath;
    public string? CurrentMediaKind => _loadRequests.CurrentMediaKind;
    public bool CurrentIsSelectionFollow => _loadRequests.CurrentIsSelectionFollow;
    public string? DisplayedPath => _loadRequests.DisplayedPath;
    public string? DisplayedMediaKind => _loadRequests.DisplayedMediaKind;
    public bool HasDisplayedMedia => _loadRequests.HasDisplayedMedia;
    public string? CurrentVideoPlaybackBackend { get; private set; }
    public long CurrentRequestId => _loadRequests.CurrentRequestId;
    public bool HasLoadedImage => _loadRequests.HasLoadedImage && State == ImageViewerProcessState.Ready && IsPipeConnected && IsProcessAlive;
    public bool IsProcessAlive => !_disposed && !HasExited();
    public bool IsPipeConnected => _connected && _pipe.IsConnected;
    public bool CanReuse => IsProcessAlive
        && (State == ImageViewerProcessState.Starting
            || (State == ImageViewerProcessState.Ready && IsPipeConnected));
    public bool IsDisposed => State is ImageViewerProcessState.Failed or ImageViewerProcessState.Closed or ImageViewerProcessState.Disposed
        || !IsProcessAlive;
    public bool Visible => _visible && !IsDisposed;
    public Rectangle Bounds => _bounds;
    public Rectangle RestoreBounds => _bounds;
    public FormWindowState WindowState
    {
        get => _windowState;
        set
        {
            if (TrySend(new ViewerIpcMessage
            {
                Type = "set-window-state",
                ViewerId = InstanceId,
                WindowState = (int)value
            }))
            {
                _windowState = value;
            }
        }
    }

    public event Action<ImageViewerProcessClient, ViewerIpcMessage>? CommandRequested;
    public event Action<ImageViewerProcessClient, ViewerIpcMessage>? LoadCompleted;
    public event Action<ImageViewerProcessClient, Keys>? BrowserNavigationRequested;
    public event Action<ImageViewerProcessClient>? MarkToggleRequested;
    public event Action<ImageViewerProcessClient>? BoundsChanged;
    public event Action<ImageViewerProcessClient, string>? StartupFailed;
    public event Action<ImageViewerProcessClient>? Closed;

    public bool TryClaimStartupFailureHandling()
        => Interlocked.Exchange(ref _startupFailureHandled, 1) == 0;

    public static ImageViewerProcessClient? Start(
        Control owner,
        string executablePath,
        Guid instanceId,
        ImageViewerSettings settings,
        bool allowQuantization,
        bool allowSvgClipboard,
        Rectangle initialBounds,
        TimeSpan readyTimeout,
        out string errorMessage,
        Action<ProcessStartInfo>? processStartInfoOverride = null,
        Func<int, bool>? allowForegroundWindowOverride = null,
        Action<ViewerIpcMessage>? outgoingMessageObserver = null)
    {
        errorMessage = string.Empty;
        NamedPipeServerStream? pipe = null;
        Process? process = null;
        try
        {
            string pipeName = $"MidPlayer-{Guid.NewGuid():N}";
            pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--host-pipe");
            startInfo.ArgumentList.Add(pipeName);
            startInfo.ArgumentList.Add("--viewer-id");
            startInfo.ArgumentList.Add(instanceId.ToString("D"));
            startInfo.ArgumentList.Add("--settings-base64");
            string settingsJson = JsonSerializer.Serialize(new
            {
                settings,
                allowQuantization,
                allowSvgClipboard
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(settingsJson)));
            processStartInfoOverride?.Invoke(startInfo);

            long processStartedAt = Stopwatch.GetTimestamp();
            process = Process.Start(startInfo);
            if (process == null)
            {
                throw new InvalidOperationException("MidPlayer.exe を起動できませんでした。");
            }

            var client = new ImageViewerProcessClient(owner, instanceId, process, pipe, initialBounds, readyTimeout, allowForegroundWindowOverride, outgoingMessageObserver, processStartedAt);
            client.CurrentVideoPlaybackBackend = settings.VideoPlaybackBackend;
            pipe = null;
            process = null;
            return client;
        }
        catch (Exception ex)
        {
            try { pipe?.Dispose(); } catch { }
            try
            {
                if (process is { HasExited: false })
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch { }
            process?.Dispose();
            errorMessage = ex.Message;
            return null;
        }
    }

    public bool ExecuteRegisteredCommand(ImageViewerCommandAction action, ImageViewerCommandContext? context)
        => TrySend(new ViewerIpcMessage
        {
            Type = "execute",
            ViewerId = InstanceId,
            Action = action.ToString(),
            SeekSeconds = context?.VideoStillSeekSeconds,
            PlaybackRate = context?.VideoPlaybackRate
        });

    public bool LoadMedia(
        string path,
        PreviewKind kind,
        bool showErrorMessage = true,
        bool isSelectionFollow = false,
        int? selectionGeneration = null,
        int? presentationGeneration = null)
    {
        long requestId = BeginLoadRequest(path, kind.ToString(), "default", isSelectionFollow);
        bool sent = TrySend(new ViewerIpcMessage
        {
            Type = "open",
            ViewerId = InstanceId,
            RequestId = requestId,
            Path = path,
            MediaKind = kind.ToString(),
            ShowErrorMessage = showErrorMessage,
            IsSelectionFollow = isSelectionFollow,
            SelectionGeneration = selectionGeneration,
            PresentationGeneration = presentationGeneration
        });
        if (!sent) _loadRequestStartedAt.TryRemove(requestId, out _);
        return sent;
    }

    public bool LoadVideoPlayback(
        string path,
        int initialSeconds,
        int volumePercent,
        string? backend = null,
        bool isSelectionFollow = false,
        int? selectionGeneration = null,
        int? presentationGeneration = null)
    {
        CurrentVideoPlaybackBackend = MidFD.Configuration.VideoPlaybackBackendKind.Normalize(backend ?? CurrentVideoPlaybackBackend);
        long requestId = BeginLoadRequest(path, nameof(PreviewKind.Video), ViewerIpcMessage.VideoLoadModePlayback, isSelectionFollow);
        bool sent = TrySend(new ViewerIpcMessage
        {
            Type = "open",
            ViewerId = InstanceId,
            RequestId = requestId,
            Path = path,
            MediaKind = nameof(PreviewKind.Video),
            VideoLoadMode = ViewerIpcMessage.VideoLoadModePlayback,
            VideoPlaybackBackend = backend,
            VideoSeconds = initialSeconds,
            VolumePercent = volumePercent,
            IsSelectionFollow = isSelectionFollow,
            SelectionGeneration = selectionGeneration,
            PresentationGeneration = presentationGeneration
        });
        if (!sent) _loadRequestStartedAt.TryRemove(requestId, out _);
        return sent;
    }

    public ViewerWindowActivationResult TryActivateSelectionFollowImage(
        ViewerIpcMessage message,
        IntPtr expectedForegroundWindow)
    {
        string? currentMediaKind = CurrentMediaKind;
        ViewerWindowActivationState state = new(
            InstanceId,
            message.ViewerId,
            message.RequestId,
            CurrentRequestId,
            CurrentIsSelectionFollow,
            message.Success == true,
            message.Cancelled == true,
            message.Path,
            message.MediaKind ?? currentMediaKind,
            HasLoadedImage,
            Visible,
            WindowState == FormWindowState.Minimized,
            currentMediaKind,
            DisplayedMediaKind,
            CurrentPath,
            DisplayedPath,
            !_owner.IsDisposed && !_owner.Disposing && !_owner.InvokeRequired,
            IsProcessAlive && State == ImageViewerProcessState.Ready,
            _processId,
            message.ViewerWindowHandle,
            expectedForegroundWindow);

        ViewerWindowActivationResult result = TryActivateViewerWindow(state, NativeViewerWindowActivationApi);
        Services.LogService.Info(
            $"[MidPlayerTiming] stage=host-selection-follow-direct-activation viewer={InstanceId} " +
            $"messageViewer={message.ViewerId} request={message.RequestId?.ToString() ?? "missing"} " +
            $"latestRequest={CurrentRequestId} pid={_processId} messageHwnd={message.ViewerWindowHandle?.ToString("X") ?? "missing"} " +
            $"expectedForeground=0x{expectedForegroundWindow.ToInt64():X} " +
            $"foregroundBefore=0x{result.ForegroundBefore.ToInt64():X} " +
            $"setForegroundWindowAccepted={result.SetForegroundWindowAccepted?.ToString() ?? "not-called"} " +
            $"setForegroundWindowError={result.SetForegroundWindowError?.ToString() ?? "not-applicable"} " +
            $"foregroundAfter=0x{result.ForegroundAfter.ToInt64():X} " +
            $"viewerForegroundObserved={result.ForegroundAfter == result.ViewerWindow} " +
            $"action={result.Action} rejected={result.RejectedReason ?? "none"}");
        return result;
    }

    internal static ViewerWindowActivationResult TryActivateViewerWindow(
        ViewerWindowActivationState state,
        ViewerWindowActivationApi api)
    {
        ArgumentNullException.ThrowIfNull(api);

        string? rejectedReason = GetViewerWindowActivationRejectionReason(state);
        if (rejectedReason != null)
        {
            return Rejected(rejectedReason);
        }

        long rawHandle = state.ViewerWindowHandle!.Value;
        if (IntPtr.Size == 4 && (rawHandle < int.MinValue || rawHandle > int.MaxValue))
        {
            return Rejected("viewer-hwnd-out-of-range");
        }

        IntPtr viewerWindow = new(rawHandle);
        if (viewerWindow == IntPtr.Zero || !api.IsWindow(viewerWindow))
        {
            return Rejected("viewer-hwnd-invalid");
        }
        if (!api.IsWindowVisible(viewerWindow))
        {
            return Rejected("viewer-window-not-visible");
        }
        if (api.IsIconic(viewerWindow))
        {
            return Rejected("viewer-window-minimized");
        }
        if (api.GetAncestor(viewerWindow) != viewerWindow)
        {
            return Rejected("viewer-hwnd-not-top-level");
        }

        (uint threadId, uint processId) = api.GetWindowThreadProcessId(viewerWindow);
        if (threadId == 0 || processId != (uint)state.ProcessId)
        {
            return Rejected("viewer-hwnd-process-mismatch");
        }

        IntPtr foregroundBefore = api.GetForegroundWindow();
        if (foregroundBefore == viewerWindow)
        {
            return new ViewerWindowActivationResult(
                ViewerWindowActivationAction.AlreadyForeground,
                null,
                viewerWindow,
                foregroundBefore,
                null,
                null,
                foregroundBefore);
        }
        if (foregroundBefore != state.ExpectedForegroundWindow)
        {
            return new ViewerWindowActivationResult(
                ViewerWindowActivationAction.Rejected,
                "foreground-hwnd-mismatch",
                viewerWindow,
                foregroundBefore,
                null,
                null,
                foregroundBefore);
        }

        // This is the final foreground check before the single cross-process activation call.
        IntPtr foregroundAtCall = api.GetForegroundWindow();
        if (foregroundAtCall != state.ExpectedForegroundWindow)
        {
            return new ViewerWindowActivationResult(
                ViewerWindowActivationAction.Rejected,
                "foreground-changed-before-activation",
                viewerWindow,
                foregroundAtCall,
                null,
                null,
                foregroundAtCall);
        }

        ViewerWindowActivationCallResult call = api.SetForegroundWindow(viewerWindow);
        IntPtr foregroundAfter = api.GetForegroundWindow();
        return new ViewerWindowActivationResult(
            ViewerWindowActivationAction.Attempted,
            null,
            viewerWindow,
            foregroundAtCall,
            call.Accepted,
            call.Accepted ? null : call.LastError,
            foregroundAfter);

        ViewerWindowActivationResult Rejected(string reason)
            => new(
                ViewerWindowActivationAction.Rejected,
                reason,
                IntPtr.Zero,
                IntPtr.Zero,
                null,
                null,
                IntPtr.Zero);
    }

    internal static string? GetViewerWindowActivationRejectionReason(ViewerWindowActivationState state)
        => state.ExpectedViewerId != state.MessageViewerId ? "viewer-id-mismatch"
            : state.RequestId is not { } requestId || requestId <= 0 ? "request-id-invalid"
            : requestId != state.CurrentRequestId ? "stale-request"
            : !state.IsSelectionFollow ? "not-selection-follow"
            : !state.LoadSucceeded ? "load-not-successful"
            : state.LoadCancelled ? "load-cancelled"
            : !string.Equals(state.MessagePath, state.CurrentPath, StringComparison.OrdinalIgnoreCase) ? "message-path-mismatch"
            : !string.Equals(state.MessageMediaKind, nameof(PreviewKind.Image), StringComparison.OrdinalIgnoreCase) ? "message-media-not-image"
            : !state.HasLoadedImage ? "image-not-loaded"
            : !state.IsVisible ? "viewer-not-visible"
            : state.IsMinimized ? "viewer-minimized"
            : !string.Equals(state.CurrentMediaKind, nameof(PreviewKind.Image), StringComparison.OrdinalIgnoreCase) ? "current-media-not-image"
            : !string.Equals(state.DisplayedMediaKind, nameof(PreviewKind.Image), StringComparison.OrdinalIgnoreCase) ? "displayed-media-not-image"
            : !string.Equals(state.CurrentPath, state.DisplayedPath, StringComparison.OrdinalIgnoreCase) ? "current-display-path-mismatch"
            : !state.IsOwnerUiThread ? "not-host-ui-thread"
            : !state.IsProcessAlive || state.ProcessId <= 0 ? "viewer-process-not-alive"
            : state.ViewerWindowHandle is not { } handle || handle <= 0 ? "viewer-hwnd-invalid"
            : state.ExpectedForegroundWindow == IntPtr.Zero ? "expected-foreground-hwnd-invalid"
            : null;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr handle, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr handle);

    private long BeginLoadRequest(string path, string mediaKind, string mode, bool isSelectionFollow = false)
    {
        long requestId = _loadRequests.Begin(path, mediaKind, isSelectionFollow);
        long startedAt = Stopwatch.GetTimestamp();
        _loadRequestStartedAt[requestId] = startedAt;
        Services.LogService.Info(
            $"[MidPlayerTiming] stage=ipc-open-enqueue request={requestId} pid={_processId} " +
            $"processState={State} visible={Visible} media={mediaKind} mode={mode}");
        return requestId;
    }

    public void Show()
    {
        long generation = Interlocked.Increment(ref _visibilityGeneration);
        if (TrySend(new ViewerIpcMessage { Type = "show", ViewerId = InstanceId, VisibilityGeneration = generation }))
        {
            _visible = true;
            Services.LogService.Info(
                $"[MidPlayerTiming] stage=show-enqueue pid={_processId} processReadyMs=" +
                $"{Stopwatch.GetElapsedTime(_processStartedAt).TotalMilliseconds:0.0}");
        }
    }

    public bool SetPresentationMode(ViewerPresentationMode mode, int generation)
        => TrySend(new ViewerIpcMessage
        {
            Type = "presentation-mode",
            ViewerId = InstanceId,
            PresentationMode = mode,
            PresentationGeneration = generation
        });

    public void BringToFront() => Show();
    public void Activate() => Show();
    public void EnsureVisibleAndActivated()
    {
        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }
        Show();
    }

    private bool TryAllowSetForegroundWindow()
        => TryAllowSetForegroundWindow(_processId);

    private bool TryAllowSetForegroundWindow(int processId)
    {
        try
        {
            return _allowForegroundWindowOverride?.Invoke(processId)
                ?? AllowSetForegroundWindow((uint)processId);
        }
        catch (Exception ex)
        {
            LogService.Warn($"[MidPlayer] AllowSetForegroundWindow threw for process {processId}: {ex.Message}");
            return false;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    public void SetBounds(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (TrySend(new ViewerIpcMessage
        {
            Type = "set-bounds",
            ViewerId = InstanceId,
            X = x,
            Y = y,
            Width = width,
            Height = height
        }))
        {
            _bounds = new Rectangle(x, y, width, height);
        }
    }

    public void Close()
    {
        TrySend(new ViewerIpcMessage { Type = "close", ViewerId = InstanceId });
    }

    public void Hide()
    {
        long generation = Interlocked.Increment(ref _visibilityGeneration);
        if (TrySend(new ViewerIpcMessage { Type = ViewerIpcMessage.TypeHide, ViewerId = InstanceId, VisibilityGeneration = generation }))
        {
            _visible = false;
            _loadRequests.InvalidateMedia();
        }
    }

    public void SetReuseOnUserClose(bool reuse)
        => TrySend(new ViewerIpcMessage
        {
            Type = ViewerIpcMessage.TypeSetReusePolicy,
            ViewerId = InstanceId,
            ReuseOnUserClose = reuse
        });

    public void Dispose() => Dispose(killProcess: false);

    private void Dispose(bool killProcess)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connected = false;
        Interlocked.Exchange(ref _state, (int)ImageViewerProcessState.Disposed);
        _loadRequests.ClearLoadedState();
        try { _stop.Cancel(); } catch (ObjectDisposedException) { }
        _outgoing.Writer.TryComplete();
        try { _pipe.Dispose(); } catch { }
        try
        {
            if (killProcess && !_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch { }
        _process.Dispose();
    }

    private bool TrySend(ViewerIpcMessage message)
    {
        if (State is not (ImageViewerProcessState.Starting or ImageViewerProcessState.Ready)
            || !IsProcessAlive
            || (State == ImageViewerProcessState.Ready && !IsPipeConnected))
        {
            return false;
        }

        lock (_sendLock)
        {
            if (!_outgoing.Writer.TryWrite(message))
            {
                return false;
            }

            try
            {
                _outgoingMessageObserver?.Invoke(message);
            }
            catch (Exception ex)
            {
                Services.LogService.Warn($"[MidPlayer IPC] Outgoing message observer failed: {ex.Message}");
            }
            return true;
        }
    }

    private async Task RunPipeAsync(TimeSpan readyTimeout)
    {
        try
        {
            using var connectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            connectionTimeout.CancelAfter(readyTimeout);
            await _pipe.WaitForConnectionAsync(connectionTimeout.Token).ConfigureAwait(false);
            using var reader = new StreamReader(_pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

            Task readTask = ReadLoopAsync(reader, _stop.Token);
            Task first = await Task.WhenAny(_ready.Task, readTask).WaitAsync(connectionTimeout.Token).ConfigureAwait(false);
            if (first != _ready.Task || !await _ready.Task.ConfigureAwait(false))
            {
                if (!_stop.IsCancellationRequested)
                {
                    FailStartup("MidPlayer.exe がready応答前に通信を終了しました。");
                }
                return;
            }

            _connected = true;
            if (Interlocked.CompareExchange(
                    ref _state,
                    (int)ImageViewerProcessState.Ready,
                    (int)ImageViewerProcessState.Starting) != (int)ImageViewerProcessState.Starting)
            {
                _connected = false;
                return;
            }
            Task writeTask = WriteLoopAsync(_stop.Token);
            await Task.WhenAny(readTask, writeTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException)
        {
            FailStartup("MidPlayer.exe の起動応答がtimeoutしました。");
        }
        catch (Exception ex)
        {
            if (State == ImageViewerProcessState.Starting)
            {
                FailStartup($"MidPlayer.exe との接続に失敗しました: {ex.Message}");
            }
            else if (State == ImageViewerProcessState.Ready)
            {
                MarkClosed();
            }
            if (!_disposed)
            {
                Services.LogService.Warn($"[MidPlayer IPC] {ex.Message}");
            }
        }
        finally
        {
            _connected = false;
            _outgoing.Writer.TryComplete();
            try { _pipe.Dispose(); } catch { }
            if (State == ImageViewerProcessState.Starting && !_stop.IsCancellationRequested)
            {
                FailStartup("MidPlayer.exe がready応答を返さず終了しました。");
            }
            else if (State == ImageViewerProcessState.Ready && !_stop.IsCancellationRequested)
            {
                MarkClosed();
            }
        }
    }

    private async Task ReadLoopAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ViewerIpcMessage? message = await ViewerIpcJson.ReadAsync(reader, cancellationToken).ConfigureAwait(false);
            if (message == null)
            {
                return;
            }
            ProcessIncomingMessage(message);
        }
    }

    internal void ProcessIncomingMessage(ViewerIpcMessage message)
    {
        if (message.ViewerId != InstanceId)
        {
            if (message.Type == "load-result")
            {
                Services.LogService.Info(
                    $"[MidPlayerTiming] stage=host-ipc-load-result-discard viewer={InstanceId} " +
                    $"messageViewer={message.ViewerId} request={message.RequestId?.ToString() ?? "missing"} " +
                    $"latestRequest={CurrentRequestId} success={message.Success == true} " +
                    $"cancelled={message.Cancelled == true} rejected=viewer-id-mismatch");
            }
            return;
        }

        switch (message.Type)
        {
            case "diagnostic" when message.DiagnosticMessage is { Length: <= 2048 } diagnostic
                && diagnostic.StartsWith("[MidPlayerTiming]", StringComparison.Ordinal):
                Services.LogService.Info($"{diagnostic} hostViewer={InstanceId} childPid={_processId}");
                TimingReceived?.Invoke(diagnostic);
                break;
            case "ready":
                _ready.TrySetResult(true);
                Services.LogService.Info(
                    $"[MidPlayerTiming] stage=process-ipc-ready pid={_processId} " +
                    $"elapsedMs={Stopwatch.GetElapsedTime(_processStartedAt).TotalMilliseconds:0.0}");
                break;
            case "bounds":
                Dispatch(() =>
                {
                    if (message.X.HasValue && message.Y.HasValue && message.Width.HasValue && message.Height.HasValue)
                    {
                        _bounds = new Rectangle(message.X.Value, message.Y.Value, message.Width.Value, message.Height.Value);
                    }
                    if (message.WindowState.HasValue && Enum.IsDefined(typeof(FormWindowState), message.WindowState.Value))
                    {
                        _windowState = (FormWindowState)message.WindowState.Value;
                    }
                    BoundsChanged?.Invoke(this);
                });
                break;
            case "load-result":
                Dispatch(() =>
                {
                    long latestRequestBefore = CurrentRequestId;
                    bool completedCurrentRequest = _loadRequests.TryComplete(message);
                    string elapsed = "unknown";
                    if (message.RequestId is { } requestId
                        && _loadRequestStartedAt.TryRemove(requestId, out long startedAt))
                    {
                        elapsed = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds.ToString("0.0");
                    }
                    Services.LogService.Info(
                        $"[MidPlayerTiming] stage=ipc-load-result viewer={InstanceId} request={message.RequestId?.ToString() ?? "missing"} " +
                        $"pid={_processId} latestBefore={latestRequestBefore} latestAfter={CurrentRequestId} " +
                        $"current={completedCurrentRequest} success={message.Success == true} cancelled={message.Cancelled == true} " +
                        $"media={message.MediaKind ?? CurrentMediaKind ?? DisplayedMediaKind ?? "unknown"} " +
                        $"visible={Visible} hasImage={HasLoadedImage} elapsedMs={elapsed}");
                    if (completedCurrentRequest)
                    {
                        LoadCompleted?.Invoke(this, message);
                    }
                });
                break;
            case ViewerIpcMessage.TypePresentationModeApplied
                when message.PresentationMode is { } mode
                    && message.PresentationGeneration is { } generation:
                Dispatch(() => PresentationModeApplied?.Invoke(this, message));
                break;
            case ViewerIpcMessage.TypeVideoPlaybackStateChanged
                when message.RequestId is > 0
                    && message.VideoPlaybackState is { } playbackState
                    && Enum.IsDefined(playbackState):
                Dispatch(() => VideoPlaybackStateChanged?.Invoke(this, message));
                break;
            case "command":
                if (!string.IsNullOrWhiteSpace(message.CommandId))
                {
                    Dispatch(() => CommandRequested?.Invoke(this, message));
                }
                break;
            case "navigation":
                if (message.KeyData.HasValue)
                {
                    Dispatch(() => BrowserNavigationRequested?.Invoke(this, (Keys)message.KeyData.Value));
                }
                break;
            case "mark":
                Dispatch(() => MarkToggleRequested?.Invoke(this));
                break;
            case "closed":
                Dispatch(MarkClosed);
                break;
            case ViewerIpcMessage.TypeHidden:
                Dispatch(() =>
                {
                    bool currentVisibility = message.VisibilityGeneration.HasValue
                        ? message.VisibilityGeneration.Value == Volatile.Read(ref _visibilityGeneration)
                        : message.RequestId.HasValue && message.RequestId.Value >= CurrentRequestId;
                    if (!currentVisibility) return;
                    _visible = false;
                    if (_loadRequests.InvalidateMedia(message.RequestId))
                    {
                        _loadRequestStartedAt.Clear();
                        Services.LogService.Info($"[MidPlayerTiming] stage=hidden-received pid={_processId} state={State}");
                    }
                });
                break;
        }
    }

    internal event Action<string>? TimingReceived;
    internal event Action<ImageViewerProcessClient, ViewerIpcMessage>? PresentationModeApplied;
    internal event Action<ImageViewerProcessClient, ViewerIpcMessage>? VideoPlaybackStateChanged;

    private async Task WriteLoopAsync(CancellationToken cancellationToken)
    {
        if (_writer == null)
        {
            return;
        }

        await foreach (ViewerIpcMessage message in _outgoing.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (message.Type == "show" && !TryAllowSetForegroundWindow())
            {
                LogService.Warn($"[MidPlayer] AllowSetForegroundWindow failed for process {_processId}; continuing with show request.");
            }
            SendImmediate(message);
        }
    }

    private bool SendImmediate(ViewerIpcMessage message)
    {
        if (!_connected || _writer == null || State != ImageViewerProcessState.Ready)
        {
            return false;
        }

        try
        {
            lock (_sendLock)
            {
                _writer.WriteLine(ViewerIpcJson.Serialize(message));
                _writer.Flush();
            }
            return true;
        }
        catch
        {
            _connected = false;
            MarkClosed();
            return false;
        }
    }

    private void FailStartup(string message)
    {
        if (Interlocked.CompareExchange(
                ref _state,
                (int)ImageViewerProcessState.Failed,
                (int)ImageViewerProcessState.Starting) != (int)ImageViewerProcessState.Starting)
        {
            return;
        }

        _failureMessage = message;
        _connected = false;
        _ready.TrySetResult(false);
        _outgoing.Writer.TryComplete();
        try { _pipe.Dispose(); } catch { }
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch { }
        RaiseTerminalEvent(() => StartupFailed?.Invoke(this, message));
    }

    private void HandleProcessExited()
    {
        if (State == ImageViewerProcessState.Starting)
        {
            FailStartup("MidPlayer.exe がready応答前に終了しました。");
            return;
        }
        if (State == ImageViewerProcessState.Ready)
        {
            MarkClosed();
        }
    }

    private void MarkClosed()
    {
        ImageViewerProcessState previous;
        if (State == ImageViewerProcessState.Starting)
        {
            FailStartup("MidPlayer.exe がready応答前に通信を終了しました。");
            return;
        }
        do
        {
            previous = State;
            if (previous is ImageViewerProcessState.Closed or ImageViewerProcessState.Failed or ImageViewerProcessState.Disposed)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _state, (int)ImageViewerProcessState.Closed, (int)previous) != (int)previous);

        _connected = false;
        _loadRequests.ClearLoadedState();
        _outgoing.Writer.TryComplete();
        try { _pipe.Dispose(); } catch { }
        try { _stop.Cancel(); } catch (ObjectDisposedException) { }
        RaiseTerminalEvent(() => Closed?.Invoke(this));
    }

    private void RaiseTerminalEvent(Action action)
    {
        if (Interlocked.Exchange(ref _terminalEventRaised, 1) == 0)
        {
            Dispatch(action);
        }
    }

    private void Dispatch(Action action)
    {
        if (_owner.IsDisposed)
        {
            return;
        }

        try
        {
            if (_owner.IsHandleCreated && _owner.InvokeRequired)
            {
                _owner.BeginInvoke(action);
            }
            else
            {
                action();
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private bool HasExited()
    {
        try { return _process.HasExited; }
        catch { return true; }
    }
}
