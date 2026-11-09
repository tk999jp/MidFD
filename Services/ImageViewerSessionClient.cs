extern alias MidPlayerLite;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using MidFD.Commands;
using MidFD.Configuration;
using MidFD.Services;
using MidFD.ViewerIpc;
using LiteImageViewerSession = MidPlayerLite::MidFD.ImageViewerSession;
using LiteImageViewerSettings = MidPlayerLite::MidFD.ImageViewerSettings;
using LiteViewerIpcMessage = MidPlayerLite::MidFD.ViewerIpc.ViewerIpcMessage;
using LiteViewerIpcJson = MidPlayerLite::MidFD.ViewerIpc.ViewerIpcJson;

namespace MidFD;

internal enum ImageViewerSessionState
{
    Starting,
    Ready,
    Closed,
    Disposed
}

internal sealed class ImageViewerSessionClient : IDisposable
{
    internal enum ViewerWindowActivationAction
    {
        Rejected,
        AlreadyForeground,
        Attempted
    }

    internal readonly record struct ViewerWindowActivationResult(
        ViewerWindowActivationAction Action,
        string? RejectedReason,
        IntPtr ViewerWindow,
        IntPtr ForegroundBefore,
        bool? SetForegroundWindowAccepted,
        int? SetForegroundWindowError,
        IntPtr ForegroundAfter);

    private readonly Control _owner;
    private readonly LiteImageViewerSession _session;
    private readonly Action<ViewerIpcMessage>? _outgoingMessageObserver;
    private readonly Func<int, bool>? _allowForegroundWindowOverride;
    private readonly ImageViewerSessionLoadRequestTracker _loadRequests;
    private long _nextRequestId;
    private long _visibilityGeneration;
    private long _susieSettingsRevision;
    private long _appliedSusieSettingsRevision;
    private long _currentLoadSusieSettingsRevision;
    private int _state = (int)ImageViewerSessionState.Ready;
    private bool _disposed;
    private Rectangle _bounds;
    private FormWindowState _windowState = FormWindowState.Normal;

    private ImageViewerSessionClient(
        Control owner,
        LiteImageViewerSession session,
        Rectangle initialBounds,
        Func<int, bool>? allowForegroundWindowOverride,
        Action<ViewerIpcMessage>? outgoingMessageObserver)
    {
        _owner = owner;
        _session = session;
        _bounds = initialBounds;
        _allowForegroundWindowOverride = allowForegroundWindowOverride;
        _outgoingMessageObserver = outgoingMessageObserver;
        InstanceId = session.InstanceId;
        _loadRequests = new ImageViewerSessionLoadRequestTracker(InstanceId);
        _session.MessageProduced += HandleMessage;
        _session.TimingProduced += HandleTimingProduced;
    }

    public Guid InstanceId { get; }
    public IntPtr WindowHandle => _session.WindowHandle;
    public ImageViewerSessionState State => (ImageViewerSessionState)Volatile.Read(ref _state);
    public string? CurrentPath => _loadRequests.CurrentPath;
    public string? CurrentMediaKind => _loadRequests.CurrentMediaKind;
    public bool CurrentIsSelectionFollow => _loadRequests.CurrentIsSelectionFollow;
    public string? DisplayedPath => _loadRequests.DisplayedPath;
    public string? DisplayedMediaKind => _loadRequests.DisplayedMediaKind;
    public bool HasDisplayedMedia => _loadRequests.HasDisplayedMedia;
    public string? CurrentVideoPlaybackBackend { get; private set; }
    public long CurrentRequestId => _loadRequests.CurrentRequestId;
    public bool HasLoadedImage => _loadRequests.HasLoadedImage && !IsDisposed;
    public bool SusieSettingsUpdateFailed { get; private set; }
    public bool SusieSettingsReloadRequired
        => Interlocked.Read(ref _appliedSusieSettingsRevision) < Interlocked.Read(ref _susieSettingsRevision);
    public bool IsProcessAlive => !IsDisposed;
    public bool IsPipeConnected => !IsDisposed;
    public bool CanReuse => !IsDisposed;
    public bool IsDisposed => _disposed || State is ImageViewerSessionState.Closed or ImageViewerSessionState.Disposed || _session.IsDisposed;
    public bool Visible => !IsDisposed && _session.Visible;
    public Rectangle Bounds => _session.Bounds;
    public Rectangle RestoreBounds => _session.Bounds;
    public FormWindowState WindowState
    {
        get => _session.WindowState;
        set
        {
            if (!Enum.IsDefined(value)) return;
            Send(new ViewerIpcMessage { Type = "set-window-state", ViewerId = InstanceId, WindowState = (int)value });
        }
    }

    public event Action<ImageViewerSessionClient, ViewerIpcMessage>? CommandRequested;
    public event Action<ImageViewerSessionClient, ViewerIpcMessage>? LoadCompleted;
    public event Action<ImageViewerSessionClient, Keys>? BrowserNavigationRequested;
    public event Action<ImageViewerSessionClient>? MarkToggleRequested;
    public event Action<ImageViewerSessionClient>? BoundsChanged;
    public event Action<ImageViewerSessionClient>? Closed;
    public event Action<ImageViewerSessionClient, ViewerIpcMessage>? PresentationModeApplied;
    public event Action<ImageViewerSessionClient, ViewerIpcMessage>? VideoPlaybackStateChanged;
    public event Action<string>? TimingReceived;

    public static ImageViewerSessionClient? Start(
        Control owner,
        Guid instanceId,
        ImageViewerSettings settings,
        bool allowQuantization,
        bool allowSvgClipboard,
        Rectangle initialBounds,
        out string errorMessage,
        Func<int, bool>? allowForegroundWindowOverride = null,
        Action<ViewerIpcMessage>? outgoingMessageObserver = null,
        Action<Guid>? sessionCreatedObserver = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        errorMessage = string.Empty;
        LiteImageViewerSession? session = null;
        try
        {
            if (owner.InvokeRequired)
            {
                throw new InvalidOperationException("Viewer session must be created on the MidFD UI thread.");
            }
            LiteImageViewerSettings liteSettings = LiteImageViewerSettings.FromJson(
                System.Text.Json.JsonSerializer.Serialize(settings, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
            session = new LiteImageViewerSession(liteSettings, allowQuantization, allowSvgClipboard, instanceId);
            var client = new ImageViewerSessionClient(owner, session, initialBounds, allowForegroundWindowOverride, outgoingMessageObserver);
            try { sessionCreatedObserver?.Invoke(instanceId); }
            catch (Exception error) { LogService.Warn($"[MidPlayer session] creation observer failed: {error.Message}"); }
            if (!initialBounds.IsEmpty) client.SetBounds(initialBounds.X, initialBounds.Y, initialBounds.Width, initialBounds.Height);
            return client;
        }
        catch (Exception error)
        {
            try { session?.Dispose(); } catch { }
            errorMessage = error.Message;
            return null;
        }
    }

    public bool ExecuteRegisteredCommand(ImageViewerCommandAction action, ImageViewerCommandContext? context)
        => Send(new ViewerIpcMessage
        {
            Type = "execute", ViewerId = InstanceId, Action = action.ToString(),
            SeekSeconds = context?.VideoStillSeekSeconds, PlaybackRate = context?.VideoPlaybackRate
        });

    public bool LoadMedia(
        string path,
        PreviewKind kind,
        bool showErrorMessage = true,
        bool isSelectionFollow = false,
        int? selectionGeneration = null,
        int? presentationGeneration = null)
    {
        long requestId = BeginLoadRequest(path, kind.ToString(), isSelectionFollow);
        bool sent = Send(new ViewerIpcMessage
        {
            Type = "open", ViewerId = InstanceId, RequestId = requestId, Path = path,
            MediaKind = kind.ToString(), ShowErrorMessage = showErrorMessage,
            IsSelectionFollow = isSelectionFollow, SelectionGeneration = selectionGeneration,
            PresentationGeneration = presentationGeneration
        });
        if (!sent) _loadRequests.InvalidateMedia(requestId);
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
        long requestId = BeginLoadRequest(path, nameof(PreviewKind.Video), isSelectionFollow);
        bool sent = Send(new ViewerIpcMessage
        {
            Type = "open", ViewerId = InstanceId, RequestId = requestId, Path = path,
            MediaKind = nameof(PreviewKind.Video), VideoLoadMode = ViewerIpcMessage.VideoLoadModePlayback,
            VideoPlaybackBackend = backend, VideoSeconds = initialSeconds, VolumePercent = volumePercent,
            IsSelectionFollow = isSelectionFollow, SelectionGeneration = selectionGeneration,
            PresentationGeneration = presentationGeneration
        });
        if (!sent) _loadRequests.InvalidateMedia(requestId);
        return sent;
    }

    public ViewerWindowActivationResult TryActivateSelectionFollowImage(ViewerIpcMessage message, IntPtr expectedForegroundWindow)
    {
        IntPtr handle = _session.WindowHandle;
        IntPtr foregroundBefore = GetForegroundWindow();
        string? rejected = null;
        if (IsDisposed) rejected = "viewer-disposed";
        else if (message.ViewerId != InstanceId) rejected = "viewer-id-mismatch";
        else if (message.RequestId != CurrentRequestId) rejected = "stale-request";
        else if (message.Success != true || message.Cancelled == true) rejected = "load-not-successful";
        else if (!Visible) rejected = "viewer-not-visible";
        else if (handle == IntPtr.Zero) rejected = "viewer-hwnd-invalid";
        else if (foregroundBefore != expectedForegroundWindow) rejected = "foreground-changed";
        else if (_owner.InvokeRequired) rejected = "not-owner-ui-thread";
        if (rejected != null)
        {
            return new(ViewerWindowActivationAction.Rejected, rejected, handle, foregroundBefore, null, null, GetForegroundWindow());
        }

        if (_allowForegroundWindowOverride is { } allowForegroundOverride && !allowForegroundOverride(Environment.ProcessId))
        {
            return new(ViewerWindowActivationAction.Rejected, "foreground-activation-not-allowed", handle, foregroundBefore, false, Marshal.GetLastWin32Error(), GetForegroundWindow());
        }

        if (foregroundBefore == handle)
        {
            return new(ViewerWindowActivationAction.AlreadyForeground, null, handle, foregroundBefore, true, 0, foregroundBefore);
        }

        bool accepted = _session.Activate();
        int error = accepted ? 0 : Marshal.GetLastWin32Error();
        return new(ViewerWindowActivationAction.Attempted, accepted ? null : "set-foreground-window-rejected", handle,
            foregroundBefore, accepted, error, GetForegroundWindow());
    }

    public bool SetPresentationMode(ViewerPresentationMode mode, int generation)
        => Send(new ViewerIpcMessage
        {
            Type = "presentation-mode", ViewerId = InstanceId,
            PresentationMode = mode, PresentationGeneration = generation
        });

    public void Show()
    {
        long generation = Interlocked.Increment(ref _visibilityGeneration);
        Send(new ViewerIpcMessage { Type = "show", ViewerId = InstanceId, VisibilityGeneration = generation });
    }

    public void BringToFront() => Show();
    public void Activate() => Show();

    public void EnsureVisibleAndActivated()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Show();
    }

    public void SetBounds(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        if (Send(new ViewerIpcMessage
        {
            Type = "set-bounds", ViewerId = InstanceId, X = x, Y = y, Width = width, Height = height
        }))
        {
            _bounds = new Rectangle(x, y, width, height);
        }
    }

    public void Close() => Send(new ViewerIpcMessage { Type = "close", ViewerId = InstanceId });

    public void Hide()
    {
        long generation = Interlocked.Increment(ref _visibilityGeneration);
        if (Send(new ViewerIpcMessage { Type = ViewerIpcMessage.TypeHide, ViewerId = InstanceId, VisibilityGeneration = generation }))
        {
            _loadRequests.InvalidateMedia();
        }
    }

    public void SetReuseOnUserClose(bool reuse)
        => Send(new ViewerIpcMessage { Type = ViewerIpcMessage.TypeSetReusePolicy, ViewerId = InstanceId, ReuseOnUserClose = reuse });

    public bool UpdateSusiePluginSettings(SusiePluginSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Interlocked.Increment(ref _susieSettingsRevision);
        string settingsJson = System.Text.Json.JsonSerializer.Serialize(
            settings,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        bool sent = Send(new ViewerIpcMessage
        {
            Type = ViewerIpcMessage.TypeUpdateSusieSettings,
            ViewerId = InstanceId,
            SettingsJson = settingsJson
        });
        if (!sent) SusieSettingsUpdateFailed = true;
        return sent;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.Exchange(ref _state, (int)ImageViewerSessionState.Disposed);
        _loadRequests.ClearLoadedState();
        _session.MessageProduced -= HandleMessage;
        _session.TimingProduced -= HandleTimingProduced;
        _session.Dispose();
    }

    private long BeginLoadRequest(string path, string mediaKind, bool isSelectionFollow)
    {
        long requestId = checked(Interlocked.Increment(ref _nextRequestId));
        Interlocked.Exchange(ref _currentLoadSusieSettingsRevision, Interlocked.Read(ref _susieSettingsRevision));
        _loadRequests.Begin(requestId, path, mediaKind, isSelectionFollow);
        return requestId;
    }

    private bool Send(ViewerIpcMessage message)
    {
        if (IsDisposed) return false;
        try { _outgoingMessageObserver?.Invoke(message); }
        catch (Exception error) { LogService.Warn($"[MidPlayer session] observer failed: {error.Message}"); }
        string payload = ViewerIpcJson.Serialize(message);
        LiteViewerIpcMessage liteMessage = LiteViewerIpcJson.Deserialize(payload)
            ?? throw new InvalidDataException("Viewer IPC payload could not be converted to the Lite contract.");
        return _session.PostHostMessage(liteMessage);
    }

    private void HandleMessage(LiteViewerIpcMessage liteMessage)
    {
        ViewerIpcMessage message = ViewerIpcJson.Deserialize(LiteViewerIpcJson.Serialize(liteMessage))
            ?? throw new InvalidDataException("Lite Viewer IPC payload could not be converted to the host contract.");
        ProcessIncomingMessage(message);
    }

    internal void ProcessIncomingMessage(ViewerIpcMessage message)
    {
        if (message.ViewerId != InstanceId) return;
        switch (message.Type)
        {
            case "bounds":
                if (message.X.HasValue && message.Y.HasValue && message.Width is > 0 && message.Height is > 0)
                    _bounds = new Rectangle(message.X.Value, message.Y.Value, message.Width.Value, message.Height.Value);
                if (message.WindowState is { } windowState && Enum.IsDefined(typeof(FormWindowState), windowState))
                    _windowState = (FormWindowState)windowState;
                BoundsChanged?.Invoke(this);
                break;
            case "load-result":
                if (_loadRequests.TryComplete(message))
                {
                    if (message.Success == true
                        && message.Cancelled != true
                        && !CurrentIsSelectionFollow
                        && string.Equals(CurrentMediaKind, nameof(PreviewKind.Image), StringComparison.OrdinalIgnoreCase))
                    {
                        long completedSettingsRevision = Interlocked.Read(ref _currentLoadSusieSettingsRevision);
                        if (completedSettingsRevision == Interlocked.Read(ref _susieSettingsRevision))
                            Interlocked.Exchange(ref _appliedSusieSettingsRevision, completedSettingsRevision);
                    }
                    LoadCompleted?.Invoke(this, message);
                }
                break;
            case ViewerIpcMessage.TypePresentationModeApplied:
                PresentationModeApplied?.Invoke(this, message);
                break;
            case ViewerIpcMessage.TypeVideoPlaybackStateChanged:
                VideoPlaybackStateChanged?.Invoke(this, message);
                break;
            case ViewerIpcMessage.TypeSusieSettingsUpdateResult:
                SusieSettingsUpdateFailed = message.Success != true;
                if (message.Success != true)
                    LogService.Warn($"[MidPlayer session] Susie settings update was rejected: {message.ErrorMessage ?? "unknown error"}");
                break;
            case "command":
                if (!string.IsNullOrWhiteSpace(message.CommandId)) CommandRequested?.Invoke(this, message);
                break;
            case "navigation":
                if (message.KeyData is { } keyData) BrowserNavigationRequested?.Invoke(this, (Keys)keyData);
                break;
            case "mark":
                MarkToggleRequested?.Invoke(this);
                break;
            case ViewerIpcMessage.TypeHidden:
                if (!message.VisibilityGeneration.HasValue || message.VisibilityGeneration.Value == Volatile.Read(ref _visibilityGeneration))
                    _loadRequests.InvalidateMedia(message.RequestId);
                break;
            case "closed":
                Interlocked.Exchange(ref _state, (int)ImageViewerSessionState.Closed);
                Closed?.Invoke(this);
                break;
        }
    }

    private void HandleTimingProduced(string message) => TimingReceived?.Invoke(message);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr handle);
}

internal sealed class ImageViewerSessionLoadRequestTracker(Guid viewerId)
{
    private readonly object _gate = new();
    private long _currentRequestId;
    private string? _currentPath;
    private string? _currentMediaKind;
    private bool _currentIsSelectionFollow;
    private string? _displayedPath;
    private string? _displayedMediaKind;
    private bool _hasLoadedImage;

    public string? CurrentPath { get { lock (_gate) return _currentPath; } }
    public long CurrentRequestId { get { lock (_gate) return _currentRequestId; } }
    public string? CurrentMediaKind { get { lock (_gate) return _currentMediaKind; } }
    public bool CurrentIsSelectionFollow { get { lock (_gate) return _currentIsSelectionFollow; } }
    public bool HasLoadedImage { get { lock (_gate) return _hasLoadedImage; } }
    public string? DisplayedPath { get { lock (_gate) return _displayedPath; } }
    public string? DisplayedMediaKind { get { lock (_gate) return _displayedMediaKind; } }
    public bool HasDisplayedMedia { get { lock (_gate) return _displayedPath != null && _displayedMediaKind != null; } }

    public void Begin(long requestId, string path, string mediaKind, bool isSelectionFollow)
    {
        lock (_gate)
        {
            _currentRequestId = requestId;
            _currentPath = path;
            _currentMediaKind = mediaKind;
            _currentIsSelectionFollow = isSelectionFollow;
            _hasLoadedImage = false;
        }
    }

    public bool TryComplete(ViewerIpcMessage message)
    {
        lock (_gate)
        {
            if (message.ViewerId != viewerId || message.RequestId != _currentRequestId
                || !string.Equals(message.Path, _currentPath, StringComparison.OrdinalIgnoreCase)) return false;
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
        lock (_gate) _hasLoadedImage = false;
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
