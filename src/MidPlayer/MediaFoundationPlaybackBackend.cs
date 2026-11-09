using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Drawing.Imaging;
using MidFD.Configuration;
using MidFD.MediaFoundation;

namespace MidFD;

internal sealed class MediaFoundationPlaybackBackend : IVideoPlaybackBackend
{
    private readonly Panel _surface = new() { Dock = DockStyle.Fill, BackColor = Color.Black };
    private IMFMediaEngine? _engine;
    private IMFMediaEngineEx? _extended;
    private Notify? _notify;
    private bool _started;
    private bool _disposed;
    private bool _loaded;
    private bool _stopped;
    private int _generation;
    private string _path = string.Empty;
    private IReadOnlyList<double> _mediaSupportedPlaybackRates = Array.Empty<double>();
    internal Action? InitializeOverrideForTests { get; set; }
    private TaskCompletionSource<bool>? _ready;
    private TaskCompletionSource<bool>? _playing;
    private TaskCompletionSource<bool>? _seeked;
    public string BackendKind => VideoPlaybackBackendKind.MediaFoundation;
    public Control RenderSurface => _surface;
    public bool HasMedia => _loaded && _engine != null;
    public bool IsPlaying => HasMedia && !_stopped && !_engine!.IsPaused() && !_engine.IsEnded();
    public bool HasAudio => _engine?.HasAudio() == true;
    public bool HasVideo => _engine?.HasVideo() == true;
    public bool IsSeekable
    {
        get
        {
            if (!HasMedia || Duration <= 0) return false;
            IMFMediaTimeRange? ranges = null;
            try { return _engine!.GetSeekable(out ranges) >= 0 && ranges.GetLength() > 0; }
            finally { MfNative.Release(ranges); }
        }
    }
    public long Position => Milliseconds(_engine?.GetCurrentTime() ?? 0);
    public long Duration => Milliseconds(_engine?.GetDuration() ?? 0);
    // Keep the six public controls visible for every video. A selected rate is
    // still checked against the current media's Media Engine capabilities.
    public IReadOnlyList<double> SupportedPlaybackRates => VideoPlaybackRateContract.UserRates;
    public double PlaybackRate => _extended?.GetPlaybackRate() ?? 1d;
    public string? PlaybackRateRestrictionReason
    {
        get
        {
            if (!_loaded) return null;
            double[] unsupported = VideoPlaybackRateContract.UserRates
                .Where(rate => !_mediaSupportedPlaybackRates.Any(value => Math.Abs(value - rate) < 0.001d))
                .ToArray();
            return unsupported.Length == 0
                ? null
                : $"この動画で利用できない再生倍率: {string.Join(", ", unsupported.Select(rate => $"{rate:0.##}x"))}";
        }
    }
    internal IReadOnlyList<double> MediaSupportedPlaybackRates => _mediaSupportedPlaybackRates;
    public Size VideoDisplaySize { get; private set; }
    public VideoPlaybackState State { get; private set; }
    public VideoPlaybackMetadata Metadata { get; private set; } = new();
    public string? LastError { get; private set; }
    internal uint RenderedFrames
    {
        get
        {
            if (_extended == null) return 0;
            var value = new PropVariant();
            try { return _extended.GetStatistics(0, out value) >= 0 ? value.Type switch { 19 => value.UInt32, 21 => (uint)Math.Min(uint.MaxValue, value.UInt64), _ => 0 } : 0; }
            finally { MfNative.PropVariantClear(ref value); }
        }
    }
    public event Action? PlaybackStateChanged;
    public event Action<string>? PlaybackFailed;
    public event Action<Size>? VideoDisplaySizeAvailable;

    public MediaFoundationPlaybackBackend()
    {
        _surface.SizeChanged += (_, _) => UpdateVideoRectangle();
        _surface.DpiChangedAfterParent += (_, _) => UpdateVideoRectangle();
        _surface.Paint += (_, _) => UpdateVideoRectangle();
        _surface.HandleDestroyed += (_, _) => { if (_engine != null) _ = StopForReuseAsync(); };
    }

    public async Task<bool> LoadAsync(string path, int start, int volume, bool autoplay, CancellationToken cancel)
    {
        var loadTimer = Stopwatch.StartNew();
        var stageTimer = Stopwatch.StartNew();
        double reuseCleanupMs = 0, startupMs = 0, engineCreateMs = 0, sourceLoadMs = 0, readyWaitMs = 0,
            initialSeekMs = 0, playMs = 0, firstFrameWaitMs = 0;
        await StopForReuseAsync();
        reuseCleanupMs = stageTimer.Elapsed.TotalMilliseconds;
        int generation = ++_generation;
        _path = path;
        LastError = null;
        State = VideoPlaybackState.Opening;
        bool loadedSuccessfully = false;
        try
        {
            cancel.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!OperatingSystem.IsWindowsVersionAtLeast(6, 2)) throw new PlatformNotSupportedException("Media Foundation Media EngineはWindows 8以降が必要です。");
            if (!_surface.Visible || !_surface.IsHandleCreated || _surface.FindForm() is not { Visible: true })
                throw new InvalidOperationException("Media Foundation描画surfaceが表示されていません。");
            InitializeOverrideForTests?.Invoke();
            stageTimer.Restart();
            MfNative.Check(MfNative.MFStartup(0x20070, 0), "MFStartup");
            startupMs = stageTimer.Elapsed.TotalMilliseconds;
            _started = true;
            IMFAttributes? attributes = null;
            IMFMediaEngineClassFactory? factory = null;
            try
            {
                stageTimer.Restart();
                MfNative.Check(MfNative.MFCreateAttributes(out attributes, 2), "MFCreateAttributes");
                _notify = new Notify(this, generation);
                Guid callback = MfNative.CallbackKey, hwnd = MfNative.PlaybackHwndKey;
                MfNative.Check(attributes.SetUnknown(ref callback, _notify), "CALLBACK");
                MfNative.Check(attributes.SetUINT64(ref hwnd, (ulong)_surface.Handle.ToInt64()), "PLAYBACK_HWND");
                factory = (IMFMediaEngineClassFactory)Activator.CreateInstance(Type.GetTypeFromCLSID(MfNative.FactoryClsid, throwOnError: true)!)!;
                MfNative.Check(factory.CreateInstance(0, attributes, out _engine), "CreateInstance");
                _extended = (IMFMediaEngineEx)_engine;
                engineCreateMs = stageTimer.Elapsed.TotalMilliseconds;
            }
            finally { MfNative.Release(attributes); MfNative.Release(factory); }
            _ready = Completion();
            _playing = Completion();
            SetVolume(volume);
            MfNative.Check(_engine.SetAutoPlay(false), "SetAutoPlay");
            // Media Engine consumes a Unicode Windows URL. Preserve Unicode path
            // characters while keeping reserved URL delimiters escaped.
            string source = new Uri(Path.GetFullPath(path)).GetComponents(UriComponents.AbsoluteUri, UriFormat.SafeUnescaped).Replace(" ", "%20");
            stageTimer.Restart();
            MfNative.Check(_engine.SetSource(source), "SetSource");
            MfNative.Check(_engine.Load(), "Load");
            sourceLoadMs = stageTimer.Elapsed.TotalMilliseconds;
            stageTimer.Restart();
            bool ready = await _ready.Task.WaitAsync(TimeSpan.FromSeconds(12), cancel);
            readyWaitMs = stageTimer.Elapsed.TotalMilliseconds;
            if (!ready || generation != _generation) return false;
            _loaded = true;
            if (!HasVideo && !HasAudio) throw new IOException("再生可能な映像/音声streamがありません。");
            _mediaSupportedPlaybackRates = MediaFoundationPlaybackRatePolicy.QuerySupportedRates(rate =>
            {
                try { return _extended!.IsPlaybackRateSupported(rate); }
                catch (Exception error)
                {
                    Services.LogService.Warn($"[MidFD Video] Media Foundation rate capability query failed rate={rate:0.##} HRESULT=0x{error.HResult:X8}: {error.Message}");
                    return false;
                }
            });
            // A new Media Engine starts at its normal 1.0x rate. Capability
            // queries are advisory and must not prevent ordinary playback.
            UpdateMetadata();
            UpdateVideoRectangle();
            if (start > 0 && IsSeekable)
            {
                stageTimer.Restart();
                if (!await SeekAsync((long)start * 1000)) return false;
                initialSeekMs = stageTimer.Elapsed.TotalMilliseconds;
            }
            if (autoplay)
            {
                stageTimer.Restart();
                if (!await PlayAsync()) return false;
                playMs = stageTimer.Elapsed.TotalMilliseconds;
                if (HasVideo)
                {
                    var wait = Stopwatch.StartNew();
                    while (generation == _generation && LastError == null && RenderedFrames == 0 && wait.Elapsed < TimeSpan.FromSeconds(5))
                        await Task.Delay(30, cancel);
                    firstFrameWaitMs = wait.Elapsed.TotalMilliseconds;
                    if (generation != _generation || LastError != null) return false;
                    if (RenderedFrames == 0) throw new IOException("映像frameの描画を確認できませんでした。");
                }
            }
            else State = VideoPlaybackState.Paused;
            Services.LogService.Info($"[MidPlayerTiming] stage=video-backend-ready engine=MediaFoundation video={HasVideo} audio={HasAudio} framesRendered={RenderedFrames} durationMs={Duration} reuseCleanupMs={reuseCleanupMs:0.0} mfStartupMs={startupMs:0.0} mediaEngineCreateMs={engineCreateMs:0.0} sourceLoadCallMs={sourceLoadMs:0.0} readyWaitMs={readyWaitMs:0.0} initialSeekMs={initialSeekMs:0.0} playMs={playMs:0.0} firstFrameWaitMs={firstFrameWaitMs:0.0} totalLoadMs={loadTimer.Elapsed.TotalMilliseconds:0.0}");
            PlaybackStateChanged?.Invoke();
            loadedSuccessfully = generation == _generation && !cancel.IsCancellationRequested;
            return loadedSuccessfully;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception error) { if (generation == _generation) Fail("load", error); return false; }
        finally { if (!loadedSuccessfully && generation == _generation) await StopForReuseAsync(); }
    }

    public async Task<bool> PlayAsync()
    {
        if (!HasMedia) return false;
        if (IsPlaying) return true;
        int generation = _generation;
        try
        {
            _playing = Completion();
            MfNative.Check(_engine!.Play(), "Play");
            bool success = await _playing.Task.WaitAsync(TimeSpan.FromSeconds(8));
            if (generation != _generation) return false;
            if (success) _stopped = false;
            return success;
        }
        catch (Exception error) { if (generation == _generation) Fail("Play", error); return false; }
    }
    public Task<bool> PauseAsync()
    {
        if (!HasMedia) return Task.FromResult(false);
        try
        {
            MfNative.Check(_engine!.Pause(), "Pause");
            State = _stopped ? VideoPlaybackState.Stopped : VideoPlaybackState.Paused;
            PlaybackStateChanged?.Invoke();
            return Task.FromResult(_engine.IsPaused());
        }
        catch (Exception error) { Fail("Pause", error); return Task.FromResult(false); }
    }
    public async Task<bool> StopAsync()
    {
        if (!await PauseAsync()) return false;
        if (IsSeekable && !await SeekAsync(0)) return false;
        _stopped = true; State = VideoPlaybackState.Stopped;
        PlaybackStateChanged?.Invoke();
        return true;
    }
    public async Task<bool> SeekAsync(long milliseconds)
    {
        if (!IsSeekable) return false;
        int generation = _generation;
        try
        {
            double seconds = Math.Clamp(milliseconds / 1000d, 0, _engine!.GetDuration());
            _seeked = Completion();
            MfNative.Check(_engine.SetCurrentTime(seconds), "SetCurrentTime");
            if (!_engine.IsSeeking() && Math.Abs(_engine.GetCurrentTime() - seconds) < .08) return true;
            return await _seeked.Task.WaitAsync(TimeSpan.FromSeconds(5)) && generation == _generation;
        }
        catch (Exception error) { if (generation == _generation) Fail("Seek", error); return false; }
    }
    public void SetVolume(int volume)
    {
        if (_engine == null) return;
        MfNative.Check(_engine.SetVolume(Math.Clamp(volume, 0, 100) / 100d), "SetVolume");
        if (VideoInputTrace.Enabled) VideoInputTrace.Record("backend-volume", $"backend=MediaFoundation requested={volume} actual={_engine.GetVolume() * 100:0}");
    }
    public bool TrySetPlaybackRate(double rate, out string? failureReason)
    {
        IMFMediaEngineEx? extended = _extended;
        if (!HasMedia || extended == null)
        {
            failureReason = "Media Foundationの再生engineが準備されていません。";
            return false;
        }
        return MediaFoundationPlaybackRatePolicy.TryApply(rate, _mediaSupportedPlaybackRates,
            extended.SetPlaybackRate, extended.GetPlaybackRate, out failureReason);
    }
    public async Task<VideoFrameCaptureResult> CaptureFrameAsync(string outputPath, CancellationToken cancellationToken)
    {
        FrameCaptureSnapshot? snapshot = null;
        string? snapshotError = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            (snapshot, snapshotError) = GetFrameCaptureSnapshot();
            if (snapshot == null) return new(false, snapshotError ?? "動画表示領域を取得できません。");

            string targetPath = Path.GetFullPath(outputPath);
            string? directory = Path.GetDirectoryName(targetPath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return new(false, "フレーム保存先ディレクトリがありません。");
            FrameCaptureSnapshot captureSnapshot = snapshot;
            string temporaryPath = Path.Combine(directory,
                $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.capture.tmp.png");

            try
            {
                await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var bitmap = new Bitmap(captureSnapshot.Bounds.Width, captureSnapshot.Bounds.Height, PixelFormat.Format32bppArgb);
                    using (Graphics graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.CopyFromScreen(captureSnapshot.Bounds.Location, Point.Empty, captureSnapshot.Bounds.Size,
                            CopyPixelOperation.SourceCopy);
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    bitmap.Save(temporaryPath, ImageFormat.Png);
                }, CancellationToken.None).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                VideoFrameCaptureResult committed = CommitFrameCapture(captureSnapshot, temporaryPath, targetPath, cancellationToken);
                return committed;
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            }
        }
        catch (OperationCanceledException)
        {
            return new(false, "フレーム保存がキャンセルされました。");
        }
        catch (Exception error)
        {
            return new(false, $"動画表示領域のキャプチャに失敗しました: {error.Message}");
        }
    }

    private (FrameCaptureSnapshot? Snapshot, string? Error) GetFrameCaptureSnapshot()
    {
        if (_surface.IsHandleCreated && _surface.InvokeRequired)
        {
            try
            {
                object? result = _surface.Invoke(
                    (Func<(FrameCaptureSnapshot? Snapshot, string? Error)>)GetFrameCaptureSnapshot);
                return (ValueTuple<FrameCaptureSnapshot?, string?>)result!;
            }
            catch (Exception invokeError)
            {
                return (null, $"動画表示領域の状態を確認できません: {invokeError.Message}");
            }
        }

        if (_disposed || _surface.IsDisposed || !_surface.IsHandleCreated)
            return (null, "動画表示領域が利用できません。");
        if (!HasMedia || !HasVideo || State is not (VideoPlaybackState.Playing or VideoPlaybackState.Paused))
            return (null, "再生中または一時停止中の動画だけ保存できます。");
        if (!_surface.Visible || _surface.FindForm() is not { Visible: true } form
            || form.WindowState == FormWindowState.Minimized)
            return (null, "動画表示領域が表示されていません。");

        Rectangle bounds = new(_surface.PointToScreen(Point.Empty), _surface.ClientSize);
        if (!MediaFoundationFrameCapturePolicy.TryValidateRegion(bounds, SystemInformation.VirtualScreen, out string? error))
            return (null, error);
        return (new FrameCaptureSnapshot(_generation, _engine!, bounds), null);
    }

    private VideoFrameCaptureResult CommitFrameCapture(FrameCaptureSnapshot snapshot, string temporaryPath,
        string targetPath, CancellationToken cancellationToken)
    {
        if (_surface.IsHandleCreated && _surface.InvokeRequired)
        {
            try
            {
                return (VideoFrameCaptureResult)_surface.Invoke(
                    (Func<VideoFrameCaptureResult>)(() => CommitFrameCapture(snapshot, temporaryPath, targetPath, cancellationToken)))!;
            }
            catch (Exception error)
            {
                return new(false, $"動画表示領域の状態を確認できません: {error.Message}");
            }
        }

        if (_disposed || _surface.IsDisposed || !_surface.IsHandleCreated
            || snapshot.Generation != _generation || !ReferenceEquals(snapshot.Engine, _engine)
            || !HasMedia || State is not (VideoPlaybackState.Playing or VideoPlaybackState.Paused))
            return new(false, "保存中に再生媒体が切り替わりました。");
        if (!_surface.Visible || _surface.FindForm() is not { Visible: true } form
            || form.WindowState == FormWindowState.Minimized)
            return new(false, "保存中に動画表示領域が非表示になりました。");
        Rectangle currentBounds = new(_surface.PointToScreen(Point.Empty), _surface.ClientSize);
        if (currentBounds != snapshot.Bounds)
            return new(false, "保存中に動画表示領域が移動しました。");
        if (!MediaFoundationFrameCapturePolicy.TryValidateRegion(currentBounds, SystemInformation.VirtualScreen, out string? boundsError))
            return new(false, boundsError);
        if (cancellationToken.IsCancellationRequested)
            return new(false, "フレーム保存がキャンセルされました。");

        try
        {
            File.Move(temporaryPath, targetPath);
            return new(true);
        }
        catch (Exception error)
        {
            return new(false, $"フレーム画像を保存できません: {error.Message}");
        }
    }

    private sealed record FrameCaptureSnapshot(int Generation, IMFMediaEngine Engine, Rectangle Bounds);

    private void HandleEvent(int generation, uint eventId, uint hr)
    {
        if (_disposed || generation != _generation || _engine == null) return;
        if (eventId == 5 || eventId == 1014)
        {
            int code = unchecked((int)hr);
            if (code == 0)
            {
                IMFMediaError? error = null;
                try { if (_engine.GetError(out error) >= 0 && error != null) code = error.GetExtendedErrorCode(); }
                finally { MfNative.Release(error); }
            }
            Fail("event-error", new COMException($"非対応mediaまたは再生エラー (HRESULT=0x{code:X8})", code));
            _ready?.TrySetResult(false); _playing?.TrySetResult(false); _seeked?.TrySetResult(false);
            return;
        }
        switch (eventId)
        {
            case 10: case 1010: UpdateMetadata(); break;
            case 14: case 15: _ready?.TrySetResult(true); break;
            case 13: _stopped = false; State = VideoPlaybackState.Playing; _playing?.TrySetResult(true); break;
            case 9: State = _stopped ? VideoPlaybackState.Stopped : VideoPlaybackState.Paused; break;
            case 19: State = VideoPlaybackState.Ended; break;
            case 17: _seeked?.TrySetResult(true); break;
        }
        PlaybackStateChanged?.Invoke();
    }
    private void UpdateMetadata()
    {
        if (_disposed || _surface.IsDisposed || !_surface.IsHandleCreated) return;
        if (_surface.InvokeRequired)
        {
            PostToSurface(UpdateMetadata);
            return;
        }

        IMFMediaEngine? engine = _engine;
        IMFMediaEngineEx? extended = _extended;
        if (engine == null || extended == null) return;
        bool hasVideo = engine.HasVideo();
        bool hasAudio = engine.HasAudio();
        if (hasVideo && engine.GetNativeVideoSize(out uint width, out uint height) >= 0 && width > 0 && height > 0)
        {
            Size size = new((int)Math.Min(width, int.MaxValue), (int)Math.Min(height, int.MaxValue));
            if (size != VideoDisplaySize) { VideoDisplaySize = size; VideoDisplaySizeAvailable?.Invoke(size); }
        }
        Metadata = ReadSelectedStreamMetadata(extended, hasVideo, hasAudio) with
        {
            Notice = MidPlayerRuntimeFeatures.HasFullVideoBackend
                ? "Media Foundation: 外部字幕は未対応。従来の字幕利用はLibVLCを選択してください。"
                : "Media Foundation: この標準配布では外部字幕を利用できません。"
        };
        PlaybackStateChanged?.Invoke();
    }

    private void PostToSurface(Action action)
    {
        if (_surface.IsDisposed || !_surface.IsHandleCreated) return;
        try { _surface.BeginInvoke(action); }
        catch (InvalidOperationException) { }
    }

    private static VideoPlaybackMetadata ReadSelectedStreamMetadata(IMFMediaEngineEx extended, bool hasVideo, bool hasAudio)
    {
        int hr = extended.GetNumberOfStreams(out uint streamCount);
        if (hr < 0 || streamCount == 0 || streamCount > 128)
            return new(hasVideo ? "不明" : "なし", hasAudio ? "不明" : "なし");

        string? video = null;
        string? audio = null;
        string? videoToolTip = null;
        string? audioToolTip = null;
        bool querySucceeded = false;
        for (uint index = 0; index < streamCount; index++)
        {
            bool selected;
            if (extended.GetStreamSelection(index, out selected) < 0) continue;
            querySucceeded = true;
            if (!selected) continue;

            Guid major = TryReadGuidAttribute(extended, index, MfNative.MfMtMajorType) ?? Guid.Empty;
            Guid? subtypeValue = TryReadGuidAttribute(extended, index, MfNative.MfMtSubtype);
            if (major == MfNative.MfMediaTypeVideo && video == null)
            {
                string codec = subtypeValue is { } subtype ? FormatCodecName(subtype, video: true) : "不明";
                string? dimensions = TryReadUInt64Attribute(extended, index, MfNative.MfMtFrameSize) is { } packedSize
                    ? $"{(uint)(packedSize >> 32)}×{(uint)packedSize}"
                    : null;
                string? frameRate = TryReadUInt64Attribute(extended, index, MfNative.MfMtFrameRate) is { } packedRate
                    ? FormatFrameRate(packedRate)
                    : null;
                video = JoinKnown(codec, dimensions, frameRate);
                videoToolTip = $"MF subtype: {subtypeValue?.ToString("D") ?? "不明"}";
            }
            else if (major == MfNative.MfMediaTypeAudio && audio == null)
            {
                string codec = subtypeValue is { } subtype ? FormatCodecName(subtype, video: false) : "不明";
                uint? sampleRate = TryReadUInt32Attribute(extended, index, MfNative.MfMtAudioSamplesPerSecond);
                uint? channels = TryReadUInt32Attribute(extended, index, MfNative.MfMtAudioNumChannels);
                audio = JoinKnown(codec,
                    sampleRate is > 0 ? $"{(sampleRate.Value / 1000d).ToString("0.###", CultureInfo.InvariantCulture)} kHz" : null,
                    channels is > 0 ? $"{channels}ch" : null);
                audioToolTip = $"MF subtype: {subtypeValue?.ToString("D") ?? "不明"}";
            }

            if (video != null && audio != null) break;
        }

        if (!querySucceeded)
            return new(hasVideo ? "不明" : "なし", hasAudio ? "不明" : "なし");
        return new(video ?? "なし", audio ?? "なし", videoToolTip ?? "-", audioToolTip ?? "-");
    }

    private static Guid? TryReadGuidAttribute(IMFMediaEngineEx extended, uint streamIndex, Guid attribute)
    {
        var value = new PropVariant();
        try
        {
            int hr = extended.GetStreamAttribute(streamIndex, ref attribute, out value);
            return hr >= 0 && value.Type == 72 && value.Pointer != IntPtr.Zero
                ? Marshal.PtrToStructure<Guid>(value.Pointer)
                : null;
        }
        finally { MfNative.PropVariantClear(ref value); }
    }

    private static ulong? TryReadUInt64Attribute(IMFMediaEngineEx extended, uint streamIndex, Guid attribute)
    {
        var value = new PropVariant();
        try
        {
            int hr = extended.GetStreamAttribute(streamIndex, ref attribute, out value);
            return hr >= 0 && value.Type == 21 ? value.UInt64 : null;
        }
        finally { MfNative.PropVariantClear(ref value); }
    }

    private static uint? TryReadUInt32Attribute(IMFMediaEngineEx extended, uint streamIndex, Guid attribute)
    {
        var value = new PropVariant();
        try
        {
            int hr = extended.GetStreamAttribute(streamIndex, ref attribute, out value);
            return hr >= 0 && value.Type == 19 ? value.UInt32 : null;
        }
        finally { MfNative.PropVariantClear(ref value); }
    }

    private static string FormatCodecName(Guid subtype, bool video)
    {
        string guidText = subtype.ToString("N");
        uint code = Convert.ToUInt32(guidText[..8], 16);
        if (video)
        {
            return code switch
            {
                0x34363248 => "H.264 / AVC",
                0x43564548 => "HEVC / H.265",
                0x31305641 => "AV1",
                0x30395056 => "VP9",
                0x30385056 => "VP8",
                0x47504a4d => "Motion JPEG",
                0x3247504d => "MPEG-2",
                0x3147504d => "MPEG-1",
                0x5634504d => "MPEG-4",
                _ => "不明"
            };
        }

        return code switch
        {
            0x00001610 or 0x000000ff => "AAC",
            0x00000055 => "MP3",
            0x00000050 => "MPEG Audio",
            0x00000001 => "PCM",
            0x00000011 => "IMA ADPCM",
            0x00002000 => "AC-3",
            0x00002001 => "DTS",
            0x7375704f => "Opus",
            _ => "不明"
        };
    }

    private static string FormatFrameRate(ulong packedRate)
    {
        uint numerator = (uint)(packedRate >> 32);
        uint denominator = (uint)packedRate;
        return numerator > 0 && denominator > 0
            ? $"{numerator / (double)denominator:0.###} fps"
            : string.Empty;
    }

    private static string JoinKnown(params string?[] values)
        => string.Join(" / ", values.Where(value => !string.IsNullOrWhiteSpace(value)));
    private void UpdateVideoRectangle()
    {
        if (_extended == null || !_surface.IsHandleCreated || _disposed || _surface.ClientSize.Width == 0 || _surface.ClientSize.Height == 0) return;
        var rectangle = new NativeRect { Right = _surface.ClientSize.Width, Bottom = _surface.ClientSize.Height };
        int hr = _extended.UpdateVideoStream(IntPtr.Zero, ref rectangle, IntPtr.Zero);
        if (hr < 0) Services.LogService.Warn($"[MidPlayer Video] engine=MediaFoundation phase=UpdateVideoStream HRESULT=0x{hr:X8}");
    }
    public Task StopForReuseAsync()
    {
        ++_generation;
        _ready?.TrySetResult(false); _playing?.TrySetResult(false); _seeked?.TrySetResult(false);
        var engine = _engine; _engine = null; _extended = null;
        try
        {
            if (engine != null)
            {
                int hr = engine.Shutdown();
                if (hr < 0) Services.LogService.Warn($"[MidPlayer Video] engine=MediaFoundation phase=Shutdown HRESULT=0x{hr:X8}");
            }
        }
        finally
        {
            MfNative.Release(engine);
            GC.KeepAlive(_notify); _notify = null;
            if (_started) { _started = false; MfNative.Check(MfNative.MFShutdown(), "MFShutdown"); }
            _loaded = false; _stopped = false; State = VideoPlaybackState.Empty;
            VideoDisplaySize = Size.Empty; Metadata = new(); _mediaSupportedPlaybackRates = Array.Empty<double>();
        }
        return Task.CompletedTask;
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopForReuseAsync();
        _surface.Dispose();
    }
    private void Fail(string phase, Exception error)
    {
        State = VideoPlaybackState.Error;
        LastError = $"Media Foundation: {error.Message}";
        Services.LogService.Error($"[MidPlayer Video] engine=MediaFoundation phase={phase} file={Path.GetFileName(_path)} HRESULT=0x{error.HResult:X8}: {error.Message}");
        PlaybackFailed?.Invoke(LastError);
    }
    private static TaskCompletionSource<bool> Completion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static long Milliseconds(double seconds) => double.IsFinite(seconds) && seconds > 0 ? (long)Math.Min(long.MaxValue, seconds * 1000) : 0;

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class Notify(MediaFoundationPlaybackBackend owner, int generation) : IMFMediaEngineNotify
    {
        public int EventNotify(uint eventId, nuint parameter1, uint parameter2)
        {
            // Never block native callbacks on UI or call back into the engine here.
            try
            {
                if (!owner._disposed && generation == owner._generation && owner._surface.IsHandleCreated)
                    owner._surface.BeginInvoke((Action)(() => owner.HandleEvent(generation, eventId, parameter2)));
            }
            catch (InvalidOperationException) { }
            return 0;
        }
    }
}

internal static class MediaFoundationPlaybackRatePolicy
{
    internal static IReadOnlyList<double> DisplayRates => VideoPlaybackRateContract.UserRates;

    internal static IReadOnlyList<double> QuerySupportedRates(Func<double, bool> isSupported)
    {
        ArgumentNullException.ThrowIfNull(isSupported);
        var supported = new List<double>();
        foreach (double rate in VideoPlaybackRateContract.UserRates)
        {
            if (isSupported(rate)) supported.Add(rate);
        }
        return supported.AsReadOnly();
    }

    internal static bool TryApply(double rate, IReadOnlyList<double> supportedRates,
        Func<double, int> setRate, Func<double> getRate, out string? failureReason)
    {
        if (!VideoPlaybackRateContract.UserRates.Any(value => Math.Abs(value - rate) < 0.001d))
        {
            failureReason = $"この再生倍率は選択できません: {rate:0.###}x";
            return false;
        }
        bool capabilityReported = supportedRates.Any(value => Math.Abs(value - rate) < 0.001d);
        bool isNormalRate = Math.Abs(rate - 1d) < 0.001d;
        if (!capabilityReported && !isNormalRate)
        {
            failureReason = $"Media Foundationはこの動画で{rate:0.##}倍速をサポートしていません。";
            return false;
        }

        int hr;
        try { hr = setRate(rate); }
        catch (Exception error)
        {
            failureReason = $"Media Foundationの{rate:0.##}倍速設定に失敗しました: {error.Message}";
            return false;
        }
        if (hr < 0)
        {
            failureReason = $"Media Foundationの{rate:0.##}倍速設定に失敗しました (HRESULT=0x{hr:X8})。";
            return false;
        }

        double actual;
        try { actual = getRate(); }
        catch (Exception error)
        {
            failureReason = $"Media Foundationの{rate:0.##}倍速を確認できませんでした: {error.Message}";
            return false;
        }
        if (!double.IsFinite(actual) || Math.Abs(actual - rate) > 0.01d)
        {
            failureReason = $"Media Foundationの再生倍率が一致しません (requested={rate:0.###}, actual={actual:0.###})。";
            return false;
        }

        failureReason = null;
        return true;
    }
}

internal static class MediaFoundationFrameCapturePolicy
{
    internal static bool TryValidateRegion(Rectangle bounds, Rectangle virtualScreen, out string? failureReason)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            failureReason = "動画表示領域のサイズが不正です。";
            return false;
        }
        if (virtualScreen.Width <= 0 || virtualScreen.Height <= 0 || !virtualScreen.Contains(bounds))
        {
            failureReason = "動画表示領域全体が画面内にありません。";
            return false;
        }
        failureReason = null;
        return true;
    }
}
