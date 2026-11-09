using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using MidFD.Services;
using MidFD.ViewerIpc;

namespace MidFD;

internal sealed class ImageViewerIpcClient : IAsyncDisposable
{
    internal readonly record struct ShowActivationResult(
        bool? SetForegroundWindowAccepted,
        bool ViewerIsForeground,
        string? RejectedReason);

    private readonly string _pipeName;
    private readonly Guid _viewerId;
    private ImageViewerForm? _form;
    private readonly Channel<ViewerIpcMessage> _outgoing = Channel.CreateUnbounded<ViewerIpcMessage>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _stop = new();
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private bool _connected;
    private long? _lastOpenRequestId;
    private long _visibilityGeneration;
    private Task? _writeTask;
    private readonly Func<IntPtr, bool>? _setForegroundWindowOverride;

    public ImageViewerIpcClient(string pipeName, Guid viewerId, Func<IntPtr, bool>? setForegroundWindowOverride = null)
    {
        _pipeName = pipeName;
        _viewerId = viewerId;
        _setForegroundWindowOverride = setForegroundWindowOverride;
    }

    public void AttachForm(ImageViewerForm form)
    {
        _form = form;
        form.ConfigureHostedLifetime();
        LogService.TimingRecorded += ForwardTiming;
        form.Opacity = 0;
        form.ShowInTaskbar = false;
        _ = RunAsync();
        form.Move += (_, _) => SendBounds();
        form.ResizeEnd += (_, _) => SendBounds();
        form.BrowserNavigationRequested += keyData => TrySend(new ViewerIpcMessage
        {
            Type = "navigation",
            ViewerId = _viewerId,
            KeyData = (int)keyData
        });
        form.MarkToggleRequested += () => TrySend(new ViewerIpcMessage
        {
            Type = "mark",
            ViewerId = _viewerId
        });
        form.PlaybackRateCommandRequested += rate => TrySendCommand(
            ImageViewerCommandIds.ImageViewerVideoSetPlaybackRate,
            seekSeconds: null,
            playbackRate: rate);
        form.MediaLoadCompleted += (path, requestId, success, cancelled) => TrySend(new ViewerIpcMessage
        {
            Type = "load-result",
            ViewerId = _viewerId,
            Path = path,
            RequestId = requestId,
            Success = success,
            Cancelled = cancelled,
            ViewerWindowHandle = TryGetViewerWindowHandle(form)
        });
        form.PresentationModeApplied += (mode, generation, accepted) => TrySend(new ViewerIpcMessage
        {
            Type = ViewerIpcMessage.TypePresentationModeApplied,
            ViewerId = _viewerId,
            PresentationMode = mode,
            PresentationGeneration = generation,
            PresentationModeAccepted = accepted
        });
        form.VideoPlaybackStateChanged += (requestId, state) => TrySend(new ViewerIpcMessage
        {
            Type = ViewerIpcMessage.TypeVideoPlaybackStateChanged,
            ViewerId = _viewerId,
            RequestId = requestId,
            VideoPlaybackState = state
        });
        form.FormClosed += (_, _) => TrySend(new ViewerIpcMessage
        {
            Type = "closed",
            ViewerId = _viewerId,
            Visible = false
        });
        form.HiddenForReuse += () =>
        {
            SendBounds();
            TrySend(new ViewerIpcMessage
            {
                Type = ViewerIpcMessage.TypeHidden,
                ViewerId = _viewerId,
                RequestId = _lastOpenRequestId,
                VisibilityGeneration = _visibilityGeneration,
                Visible = false
            });
            LogService.Info("[MidPlayerTiming] stage=viewer-hidden visible=False");
        };
    }

    public bool TrySendCommand(string commandId, int? seekSeconds)
        => TrySendCommand(commandId, seekSeconds, null);

    public bool TrySendCommand(string commandId, int? seekSeconds, double? playbackRate)
    {
        if (!_connected)
        {
            return false;
        }

        return TrySend(new ViewerIpcMessage
        {
            Type = "command",
            ViewerId = _viewerId,
            CommandId = commandId,
            SeekSeconds = seekSeconds,
            PlaybackRate = playbackRate
        });
    }

    public async Task FlushAndStopAsync()
    {
        if (_connected)
        {
            TrySend(new ViewerIpcMessage { Type = "closed", ViewerId = _viewerId, Visible = false });
        }

        _outgoing.Writer.TryComplete();
        if (_writeTask != null)
        {
            try
            {
                await _writeTask.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        _stop.Cancel();
        _pipe?.Dispose();
    }

    private bool TrySend(ViewerIpcMessage message)
        => _connected && _outgoing.Writer.TryWrite(message);

    private void ForwardTiming(string message)
        => TrySend(new ViewerIpcMessage
        {
            Type = "diagnostic",
            ViewerId = _viewerId,
            DiagnosticMessage = message
        });

    private void SendBounds()
    {
        ImageViewerForm? form = _form;
        if (form == null || form.IsDisposed || !form.IsHandleCreated)
        {
            return;
        }

        Rectangle bounds = form.WindowState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;
        TrySend(new ViewerIpcMessage
        {
            Type = "bounds",
            ViewerId = _viewerId,
            X = bounds.X,
            Y = bounds.Y,
            Width = bounds.Width,
            Height = bounds.Height,
            WindowState = (int)form.WindowState
        });
    }

    private static long? TryGetViewerWindowHandle(ImageViewerForm form)
    {
        if (form.IsDisposed || form.Disposing || !form.IsHandleCreated || form.InvokeRequired)
        {
            return null;
        }

        try
        {
            IntPtr handle = form.Handle;
            return handle == IntPtr.Zero ? null : handle.ToInt64();
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private async Task RunAsync()
    {
        if (_pipe != null)
        {
            return;
        }

        try
        {
            if (_form == null)
            {
                return;
            }

            _pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            await _pipe.ConnectAsync(connectTimeout.Token).ConfigureAwait(false);

            using var reader = new StreamReader(_pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            _connected = true;
            await ViewerIpcJson.WriteAsync(_writer, new ViewerIpcMessage
            {
                Type = "ready",
                ViewerId = _viewerId,
                Visible = false
            }, _stop.Token).ConfigureAwait(false);

            _writeTask = WriteLoopAsync(_stop.Token);
            await ReadLoopAsync(reader, _stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Services.LogService.Warn($"[MidPicViewer IPC] {ex.Message}");
        }
        finally
        {
            _connected = false;
            _outgoing.Writer.TryComplete();
            _pipe?.Dispose();
            CloseFormAfterHostDisconnect();
        }
    }

    private async Task WriteLoopAsync(CancellationToken cancellationToken)
    {
        if (_writer == null)
        {
            return;
        }

        await foreach (ViewerIpcMessage message in _outgoing.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await ViewerIpcJson.WriteAsync(_writer, message, cancellationToken).ConfigureAwait(false);
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
            if (message.ViewerId != _viewerId) continue;

            ImageViewerForm? form = _form;
            if (form == null || form.IsDisposed || !form.IsHandleCreated)
            {
                continue;
            }

            try
            {
                var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                form.BeginInvoke((Action)(async () =>
                {
                    try
                    {
                        // Preserve pipe FIFO across asynchronous native media teardown.
                        if (message.Type is "open" or "show") await form.ReuseCleanupTask;
                        if (!_stop.IsCancellationRequested) ApplyHostMessage(message);
                        applied.TrySetResult();
                    }
                    catch (Exception ex) { applied.TrySetException(ex); }
                }));
                await applied.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                return;
            }
        }
    }

    private void ApplyHostMessage(ViewerIpcMessage message)
    {
        ImageViewerForm? form = _form;
        if (form == null || form.IsDisposed)
        {
            return;
        }

        switch (message.Type)
        {
            case "open" when !string.IsNullOrWhiteSpace(message.Path):
                _lastOpenRequestId = message.RequestId;
                if (message.PresentationMode is { } requestedPresentationMode
                    && message.PresentationGeneration is { } requestedPresentationGeneration)
                {
                    if (!ApplyPresentationMode(form, requestedPresentationMode, requestedPresentationGeneration))
                    {
                        form.RejectMediaLoad(message.Path, message.RequestId);
                        break;
                    }
                }
                bool isSelectionFollow = message.IsSelectionFollow == true;
                bool isVideo = string.Equals(message.MediaKind, "Video", StringComparison.OrdinalIgnoreCase);
                bool loadMethodAcceptsSelectionFollow = !isVideo
                    || string.Equals(message.VideoLoadMode, ViewerIpcMessage.VideoLoadModePlayback, StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(message.VideoLoadMode)
                    || string.Equals(message.VideoLoadMode, ViewerIpcMessage.VideoLoadModeStill, StringComparison.OrdinalIgnoreCase);
                if (isSelectionFollow
                    && !loadMethodAcceptsSelectionFollow
                    && !form.TryAcceptSelectionFollow(message.SelectionGeneration, message.PresentationGeneration))
                {
                    form.RejectMediaLoad(message.Path, message.RequestId);
                    break;
                }
                if (isVideo)
                {
                    if (string.Equals(message.VideoLoadMode, ViewerIpcMessage.VideoLoadModePlayback, StringComparison.OrdinalIgnoreCase))
                    {
                        if (message.VideoPlaybackBackend != null) form.SetVideoPlaybackBackend(message.VideoPlaybackBackend);
                        form.LoadVideoPlayback(
                            message.Path,
                            message.VideoSeconds ?? 0,
                            message.VolumePercent ?? 100,
                            message.RequestId,
                            isSelectionFollow,
                            message.SelectionGeneration,
                            message.PresentationGeneration);
                    }
                    else if (string.IsNullOrWhiteSpace(message.VideoLoadMode)
                        || string.Equals(message.VideoLoadMode, ViewerIpcMessage.VideoLoadModeStill, StringComparison.OrdinalIgnoreCase))
                    {
                        // Older callers used the still-preview mode. Keep the
                        // wire value readable, but route it to normal playback.
                        form.LoadVideoPlayback(
                            message.Path,
                            message.VideoSeconds ?? 0,
                            message.VolumePercent ?? 100,
                            message.RequestId,
                            isSelectionFollow,
                            message.SelectionGeneration,
                            message.PresentationGeneration);
                    }
                    else
                    {
                        form.FailVideoLoadRequest(message.Path, message.RequestId, $"動画要求の種類を認識できません: {message.VideoLoadMode}");
                    }
                }
                else
                {
                    form.LoadMedia(
                        message.Path,
                        ImageViewerMediaKind.Image,
                        message.ShowErrorMessage,
                        message.RequestId,
                        isSelectionFollow,
                        message.SelectionGeneration,
                        message.PresentationGeneration);
                }
                break;
            case "presentation-mode" when message.PresentationMode is { } mode
                && message.PresentationGeneration is { } generation:
                ApplyPresentationMode(form, mode, generation);
                break;
            case "execute" when !string.IsNullOrWhiteSpace(message.Action):
                form.ExecuteRegisteredCommand(message.Action, message.SeekSeconds, message.PlaybackRate);
                break;
            case "show":
            {
                if (!TryAcceptVisibilityCommand(message)) break;
                form.Opacity = 1;
                form.ShowInTaskbar = true;
                ShowActivationResult activation = ShowAndActivate(form, restoreMinimized: true);
                LogService.Info(
                    $"[MidPlayerTiming] stage=viewer-visible visible={form.Visible} " +
                    $"setForegroundWindowAccepted={activation.SetForegroundWindowAccepted?.ToString() ?? "not-called"} " +
                    $"viewerForeground={activation.ViewerIsForeground} rejected={activation.RejectedReason ?? "none"}");
                if (activation.SetForegroundWindowAccepted != true)
                {
                    LogService.Warn($"[MidPlayer] SetForegroundWindow failed for viewer {_viewerId}; continuing with visible window.");
                }
                break;
            }
            case "close":
                form.Shutdown();
                break;
            case ViewerIpcMessage.TypeHide:
                if (!TryAcceptVisibilityCommand(message)) break;
                form.HideForReuse();
                break;
            case ViewerIpcMessage.TypeSetReusePolicy when message.ReuseOnUserClose.HasValue:
                form.SetReuseOnUserClose(message.ReuseOnUserClose.Value);
                break;
            case "set-bounds" when message.X.HasValue && message.Y.HasValue && message.Width is > 0 && message.Height is > 0:
                form.StartPosition = FormStartPosition.Manual;
                form.SetBounds(message.X.Value, message.Y.Value, message.Width.Value, message.Height.Value);
                break;
            case "set-window-state" when message.WindowState.HasValue:
                form.WindowState = (FormWindowState)message.WindowState.Value;
                break;
        }
    }

    private ShowActivationResult ShowAndActivate(
        ImageViewerForm form,
        bool restoreMinimized)
    {
        if (!form.Visible || restoreMinimized)
        {
            form.Show();
        }
        if (restoreMinimized && form.WindowState == FormWindowState.Minimized)
        {
            form.WindowState = FormWindowState.Normal;
        }

        form.BringToFront();
        form.Activate();

        bool? setForegroundWindowAccepted = TrySetForegroundWindow(form.Handle);
        IntPtr foregroundWindow = GetForegroundWindow();
        bool viewerIsForeground = foregroundWindow == form.Handle;
        string? rejectedReason = viewerIsForeground ? null : "viewer-not-foreground-after-activation";
        LogService.Info(
            $"[MidPlayerTiming] stage=viewer-show-activation viewer={_viewerId} " +
            $"foreground=0x{foregroundWindow.ToInt64():X} viewerHwnd=0x{form.Handle.ToInt64():X} " +
            $"setForegroundWindowAccepted={setForegroundWindowAccepted?.ToString() ?? "not-called"} " +
            $"viewerForeground={viewerIsForeground} rejected={rejectedReason ?? "none"}");
        return new ShowActivationResult(setForegroundWindowAccepted, viewerIsForeground, rejectedReason);
    }

    private static bool ApplyPresentationMode(ImageViewerForm form, ViewerPresentationMode mode, int generation)
        => form.ApplyPresentationModeFromHost(mode, generation);

    private bool TryAcceptVisibilityCommand(ViewerIpcMessage message)
    {
        if (message.VisibilityGeneration is not { } generation) return true;
        if (generation < _visibilityGeneration) return false;
        _visibilityGeneration = generation;
        return true;
    }

    private bool TrySetForegroundWindow(IntPtr handle)
    {
        try
        {
            return _setForegroundWindowOverride?.Invoke(handle) ?? SetForegroundWindow(handle);
        }
        catch (Exception ex)
        {
            LogService.Warn($"[MidPlayer] SetForegroundWindow threw for viewer {_viewerId}: {ex.Message}");
            return false;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private void CloseFormAfterHostDisconnect()
    {
        ImageViewerForm? form = _form;
        if (form == null || form.IsDisposed || !form.IsHandleCreated)
        {
            return;
        }

        try
        {
            form.BeginInvoke((Action)(() =>
            {
                if (!form.IsDisposed && !form.Disposing)
                {
                    form.Shutdown();
                }
            }));
        }
        catch (InvalidOperationException)
        {
        }
    }

    public ValueTask DisposeAsync()
    {
        LogService.TimingRecorded -= ForwardTiming;
        _stop.Cancel();
        _pipe?.Dispose();
        _stop.Dispose();
        return ValueTask.CompletedTask;
    }
}
