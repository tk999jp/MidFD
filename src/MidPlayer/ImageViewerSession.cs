using System.Collections.Generic;
using MidFD.ViewerIpc;

namespace MidFD;

/// <summary>In-process host for the shared Viewer form.</summary>
public sealed class ImageViewerSession : IDisposable
{
    private readonly object _queueGate = new();
    private readonly Queue<ViewerIpcMessage> _pending = new();
    private readonly ImageViewerForm _form;
    private bool _draining;
    private bool _disposed;
    private bool _closed;
    private bool _susieSettingsApplicationFailed;
    private long? _lastOpenRequestId;
    private long _visibilityGeneration;

    public Guid InstanceId { get; }
    public bool IsDisposed => _disposed || _closed || _form.IsDisposed;
    public bool Visible => !IsDisposed && _form.Visible;
    public bool HasLoadedMedia => !IsDisposed && _form.HasLoadedImage;
    public string? CurrentPath => _form.CurrentPath;
    public Rectangle Bounds => _form.WindowState == FormWindowState.Normal ? _form.Bounds : _form.RestoreBounds;
    public FormWindowState WindowState => _form.WindowState;
    public IntPtr WindowHandle => TryGetHandle(_form);
    internal Task ReuseCleanupTask => _form.ReuseCleanupTask;
    internal ImageViewerForm Form => _form;

    public event Action<ViewerIpcMessage>? MessageProduced;
    public event Action<string>? TimingProduced;

    public ImageViewerSession(
        ImageViewerSettings settings,
        bool allowQuantization,
        bool allowSvgClipboard,
        Guid instanceId)
    {
        ArgumentNullException.ThrowIfNull(settings);
        InstanceId = instanceId;
        _form = new ImageViewerForm(settings, allowQuantization, allowSvgClipboard, DispatchHostCommand, instanceId);
        _form.ConfigureHostedLifetime();
        _form.Opacity = 0;
        _form.ShowInTaskbar = false;
        _form.Move += HandleBoundsChanged;
        _form.ResizeEnd += HandleBoundsChanged;
        _form.BrowserNavigationRequested += HandleBrowserNavigationRequested;
        _form.MarkToggleRequested += HandleMarkToggleRequested;
        _form.PlaybackRateCommandRequested += HandlePlaybackRateRequested;
        _form.MediaLoadCompleted += HandleMediaLoadCompleted;
        _form.PresentationModeApplied += HandlePresentationModeApplied;
        _form.VideoPlaybackStateChanged += HandleVideoPlaybackStateChanged;
        _form.FormClosed += HandleClosed;
        _form.HiddenForReuse += HandleHiddenForReuse;
        _form.TimingDiagnosticProduced += HandleTimingDiagnosticProduced;

        // The in-process host uses the same UI-thread form lifetime as MidFD.
        _ = _form.Handle;
    }

    public bool PostHostMessage(ViewerIpcMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (IsDisposed || message.ViewerId != InstanceId) return false;
        bool schedule;
        lock (_queueGate)
        {
            _pending.Enqueue(message);
            schedule = !_draining;
            if (schedule) _draining = true;
        }
        if (schedule)
        {
            if (_form.InvokeRequired)
            {
                try { _form.BeginInvoke((Action)(async () => await DrainQueueAsync())); }
                catch (InvalidOperationException)
                {
                    lock (_queueGate)
                    {
                        _pending.Clear();
                        _draining = false;
                    }
                    return false;
                }
            }
            else
            {
                _ = DrainQueueAsync();
            }
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _form.Move -= HandleBoundsChanged;
        _form.ResizeEnd -= HandleBoundsChanged;
        _form.BrowserNavigationRequested -= HandleBrowserNavigationRequested;
        _form.MarkToggleRequested -= HandleMarkToggleRequested;
        _form.PlaybackRateCommandRequested -= HandlePlaybackRateRequested;
        _form.MediaLoadCompleted -= HandleMediaLoadCompleted;
        _form.PresentationModeApplied -= HandlePresentationModeApplied;
        _form.VideoPlaybackStateChanged -= HandleVideoPlaybackStateChanged;
        _form.HiddenForReuse -= HandleHiddenForReuse;
        _form.TimingDiagnosticProduced -= HandleTimingDiagnosticProduced;
        _form.FormClosed -= HandleClosed;
        if (!_form.IsDisposed)
        {
            try { _form.Dispose(); }
            catch (InvalidOperationException) { }
        }
        MessageProduced = null;
        TimingProduced = null;
    }

    public bool Activate()
    {
        if (IsDisposed || _form.InvokeRequired) return false;
        _form.Opacity = 1;
        _form.ShowInTaskbar = true;
        if (!_form.Visible) _form.Show();
        if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
        _form.BringToFront();
        _form.Activate();
        return _form.IsHandleCreated && SetForegroundWindow(_form.Handle);
    }

    private async Task DrainQueueAsync()
    {
        while (true)
        {
            ViewerIpcMessage message;
            lock (_queueGate)
            {
                if (_pending.Count == 0)
                {
                    _draining = false;
                    return;
                }
                message = _pending.Dequeue();
            }

            if (IsDisposed) continue;
            try
            {
                if (message.Type is "open" or "show") await _form.ReuseCleanupTask;
                if (!IsDisposed) ApplyHostMessage(message);
            }
            catch (Exception error)
            {
                Services.LogService.Error($"[ImageViewerSession] request failed type={message.Type}", error);
                if (message.Type == "open" && message.RequestId is { } requestId && message.Path is { } path)
                {
                    Emit(new ViewerIpcMessage
                    {
                        Type = "load-result", ViewerId = InstanceId, Path = path,
                        RequestId = requestId, Success = false, Cancelled = false
                    });
                }
            }
        }
    }

    private void ApplyHostMessage(ViewerIpcMessage message)
    {
        switch (message.Type)
        {
            case "open" when !string.IsNullOrWhiteSpace(message.Path):
                _lastOpenRequestId = message.RequestId;
                bool isVideoRequest = string.Equals(message.MediaKind, "Video", StringComparison.OrdinalIgnoreCase);
                if (!isVideoRequest && _susieSettingsApplicationFailed)
                {
                    _form.RejectMediaLoad(message.Path, message.RequestId);
                    return;
                }
                if (message.PresentationMode is { } requestedMode
                    && message.PresentationGeneration is { } requestedGeneration
                    && !_form.ApplyPresentationModeFromHost(requestedMode, requestedGeneration))
                {
                    _form.RejectMediaLoad(message.Path, message.RequestId);
                    return;
                }
                bool selectionFollow = message.IsSelectionFollow == true;
                bool isVideo = isVideoRequest;
                bool lowerLoadAcceptsGeneration = !isVideo
                    || string.Equals(message.VideoLoadMode, ViewerIpcMessage.VideoLoadModePlayback, StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(message.VideoLoadMode)
                    || string.Equals(message.VideoLoadMode, ViewerIpcMessage.VideoLoadModeStill, StringComparison.OrdinalIgnoreCase);
                if (selectionFollow && !lowerLoadAcceptsGeneration
                    && !_form.TryAcceptSelectionFollow(message.SelectionGeneration, message.PresentationGeneration))
                {
                    _form.RejectMediaLoad(message.Path, message.RequestId);
                    return;
                }
                if (isVideo)
                {
                    if (string.Equals(message.VideoLoadMode, ViewerIpcMessage.VideoLoadModePlayback, StringComparison.OrdinalIgnoreCase)
                        || string.IsNullOrWhiteSpace(message.VideoLoadMode)
                        || string.Equals(message.VideoLoadMode, ViewerIpcMessage.VideoLoadModeStill, StringComparison.OrdinalIgnoreCase))
                    {
                        if (message.VideoPlaybackBackend != null) _form.SetVideoPlaybackBackend(message.VideoPlaybackBackend);
                        _form.LoadVideoPlayback(message.Path, message.VideoSeconds ?? 0, message.VolumePercent ?? 100,
                            message.RequestId, selectionFollow, message.SelectionGeneration, message.PresentationGeneration);
                    }
                    else
                    {
                        _form.FailVideoLoadRequest(message.Path, message.RequestId,
                            $"動画要求の種類を認識できません: {message.VideoLoadMode}");
                    }
                }
                else
                {
                    _form.LoadMedia(message.Path, ImageViewerMediaKind.Image, message.ShowErrorMessage,
                        message.RequestId, selectionFollow, message.SelectionGeneration, message.PresentationGeneration);
                }
                break;
            case "presentation-mode" when message.PresentationMode is { } mode
                && message.PresentationGeneration is { } generation:
                _form.ApplyPresentationModeFromHost(mode, generation);
                break;
            case "execute" when !string.IsNullOrWhiteSpace(message.Action):
                _form.ExecuteRegisteredCommand(message.Action, message.SeekSeconds, message.PlaybackRate);
                break;
            case "show":
                if (!TryAcceptVisibilityCommand(message)) return;
                _form.Opacity = 1;
                _form.ShowInTaskbar = true;
                if (!_form.Visible) _form.Show();
                if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
                _form.BringToFront();
                _form.Activate();
                if (_form.IsHandleCreated) SetForegroundWindow(_form.Handle);
                break;
            case "close":
                _form.Shutdown();
                break;
            case ViewerIpcMessage.TypeHide:
                if (TryAcceptVisibilityCommand(message)) _form.HideForReuse();
                break;
            case ViewerIpcMessage.TypeSetReusePolicy when message.ReuseOnUserClose.HasValue:
                _form.SetReuseOnUserClose(message.ReuseOnUserClose.Value);
                break;
            case ViewerIpcMessage.TypeUpdateSusieSettings:
            {
                bool success = _form.TryApplySusiePluginSettings(message.SettingsJson, out string errorMessage);
                _susieSettingsApplicationFailed = !success;
                Emit(new ViewerIpcMessage
                {
                    Type = ViewerIpcMessage.TypeSusieSettingsUpdateResult,
                    ViewerId = InstanceId,
                    Success = success,
                    ErrorMessage = success ? null : errorMessage
                });
                break;
            }
            case "set-bounds" when message.X.HasValue && message.Y.HasValue && message.Width is > 0 && message.Height is > 0:
                _form.StartPosition = FormStartPosition.Manual;
                _form.SetBounds(message.X.Value, message.Y.Value, message.Width.Value, message.Height.Value);
                break;
            case "set-window-state" when message.WindowState.HasValue:
                _form.WindowState = (FormWindowState)message.WindowState.Value;
                break;
        }
    }

    private bool DispatchHostCommand(string commandId, int? seekSeconds)
        => DispatchHostCommand(commandId, seekSeconds, null);

    private bool DispatchHostCommand(string commandId, int? seekSeconds, double? playbackRate)
    {
        if (IsDisposed) return false;
        var message = new ViewerIpcMessage
        {
            Type = "command", ViewerId = InstanceId, CommandId = commandId,
            SeekSeconds = seekSeconds, PlaybackRate = playbackRate
        };
        MessageProduced?.Invoke(message);
        return true;
    }

    private void Emit(ViewerIpcMessage message)
    {
        if (!IsDisposed) MessageProduced?.Invoke(message);
    }

    private void SendBounds()
    {
        if (IsDisposed || !_form.IsHandleCreated) return;
        Rectangle bounds = _form.WindowState == FormWindowState.Normal ? _form.Bounds : _form.RestoreBounds;
        Emit(new ViewerIpcMessage
        {
            Type = "bounds", ViewerId = InstanceId, X = bounds.X, Y = bounds.Y,
            Width = bounds.Width, Height = bounds.Height, WindowState = (int)_form.WindowState
        });
    }

    private void HandleBoundsChanged(object? sender, EventArgs e) => SendBounds();
    private void HandleTimingDiagnosticProduced(string message) => TimingProduced?.Invoke(message);
    private void HandleBrowserNavigationRequested(Keys keyData) => Emit(new ViewerIpcMessage
    {
        Type = "navigation", ViewerId = InstanceId, KeyData = (int)keyData
    });
    private void HandleMarkToggleRequested() => Emit(new ViewerIpcMessage { Type = "mark", ViewerId = InstanceId });
    private void HandlePlaybackRateRequested(double rate) => DispatchHostCommand(
        ImageViewerCommandIds.ImageViewerVideoSetPlaybackRate, null, rate);
    private void HandleMediaLoadCompleted(string path, long requestId, bool success, bool cancelled) => Emit(new ViewerIpcMessage
    {
        Type = "load-result", ViewerId = InstanceId, Path = path, RequestId = requestId,
        Success = success, Cancelled = cancelled, ViewerWindowHandle = TryGetHandle(_form).ToInt64()
    });
    private void HandlePresentationModeApplied(ViewerPresentationMode mode, int generation, bool accepted) => Emit(new ViewerIpcMessage
    {
        Type = ViewerIpcMessage.TypePresentationModeApplied, ViewerId = InstanceId,
        PresentationMode = mode, PresentationGeneration = generation, PresentationModeAccepted = accepted
    });
    private void HandleVideoPlaybackStateChanged(long requestId, ViewerVideoPlaybackState state) => Emit(new ViewerIpcMessage
    {
        Type = ViewerIpcMessage.TypeVideoPlaybackStateChanged, ViewerId = InstanceId,
        RequestId = requestId, VideoPlaybackState = state
    });
    private void HandleHiddenForReuse()
    {
        SendBounds();
        Emit(new ViewerIpcMessage
        {
            Type = ViewerIpcMessage.TypeHidden, ViewerId = InstanceId,
            RequestId = _lastOpenRequestId, VisibilityGeneration = _visibilityGeneration, Visible = false
        });
    }

    private void HandleClosed(object? sender, FormClosedEventArgs e)
    {
        Emit(new ViewerIpcMessage { Type = "closed", ViewerId = InstanceId, Visible = false });
        _closed = true;
    }

    private bool TryAcceptVisibilityCommand(ViewerIpcMessage message)
    {
        if (message.VisibilityGeneration is not { } generation) return true;
        if (generation < _visibilityGeneration) return false;
        _visibilityGeneration = generation;
        return true;
    }

    private static IntPtr TryGetHandle(ImageViewerForm form)
    {
        if (form.IsDisposed || form.Disposing || !form.IsHandleCreated || form.InvokeRequired) return IntPtr.Zero;
        try { return form.Handle; }
        catch (InvalidOperationException) { return IntPtr.Zero; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr handle);
}
