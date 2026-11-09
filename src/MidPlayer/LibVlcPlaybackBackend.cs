using System.Globalization;
using System.Text;
using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;
using MidFD.Configuration;

namespace MidFD;

internal sealed class LibVlcPlaybackBackend : IVideoPlaybackBackend, IVideoPlaybackProbeConsumer
{
    private readonly VideoView _surface = new() { Dock = DockStyle.Fill, BackColor = Color.Black };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LibVLC? _libVlc;
    private Media? _media;
    private MediaPlayer? _player;
    private CancellationTokenSource? _metadataCts;
    private Task? _metadataTask;
    private Task _cleanup = Task.CompletedTask;
    private int _generation;
    private bool _disposed;
    private bool _stopped;
    private long _stoppedPosition;
    private VideoCodecInfo? _probeInfo;
    internal int MetadataParseCount { get; private set; }
    public void UseCodecProbe(VideoCodecInfo? info) => _probeInfo = info;
    internal bool AdoptParsedRuntime(LibVLC runtime)
    {
        if (_disposed || _libVlc != null) return false;
        _libVlc = runtime;
        return true;
    }
    public string BackendKind => VideoPlaybackBackendKind.LibVLC;
    public Control RenderSurface => _surface;
    public bool HasMedia => _media != null && _player != null;
    public bool IsPlaying => _player?.IsPlaying == true;
    public bool IsSeekable => _player?.IsSeekable == true && Duration > 0;
    public IReadOnlyList<double> SupportedPlaybackRates => VideoPlaybackRateContract.UserRates;
    public double PlaybackRate => _player?.Rate ?? 1d;
    public long Position => _stopped ? _stoppedPosition : Math.Max(0, _player?.Time ?? 0);
    public long Duration => Math.Max(0, _player?.Length ?? 0);
    public Size VideoDisplaySize { get; private set; }
    public VideoPlaybackState State => _stopped ? VideoPlaybackState.Stopped : _player?.State switch
    {
        VLCState.Playing => VideoPlaybackState.Playing,
        VLCState.Paused => VideoPlaybackState.Paused,
        VLCState.Ended => VideoPlaybackState.Ended,
        VLCState.Error => VideoPlaybackState.Error,
        VLCState.Opening or VLCState.Buffering => VideoPlaybackState.Opening,
        _ => HasMedia ? VideoPlaybackState.Stopped : VideoPlaybackState.Empty
    };
    public VideoPlaybackMetadata Metadata { get; private set; } = new();
    public string? LastError { get; private set; }
    public event Action? PlaybackStateChanged;
    public event Action<string>? PlaybackFailed;
    public event Action<Size>? VideoDisplaySizeAvailable;

    public async Task<bool> LoadAsync(string path, int start, int volume, bool autoplay, CancellationToken cancel)
    {
        var probe = _probeInfo; _probeInfo = null;
        await StopForReuseAsync();
        int generation = ++_generation;
        LastError = null;
        bool loadedSuccessfully = false;
        try
        {
            cancel.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            var resolution = probe is { Complete: true, SidecarResolution: { } parsedResolution }
                && string.Equals(probe.Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)
                ? parsedResolution
                : await SubtitleSidecarResolver.ResolveAsync(path, cancel);
            if (cancel.IsCancellationRequested || generation != _generation) return false;
            foreach (var ignored in resolution.IgnoredSidecars)
                Services.LogService.Warn($"Unsupported/unreadable sidecar subtitle ignored: {ignored.Path} ({ignored.Reason})");
            if (_libVlc == null)
            {
                Core.Initialize();
                _libVlc = new LibVLC("--no-video-title-show", "--quiet", "--audio-time-stretch");
            }
            _media = new Media(_libVlc, Path.GetFullPath(path), FromType.FromPath);
            if (!SubtitleSidecarResolver.ConfigureMedia(_media, resolution) && resolution.SupportedPath is { } subtitle)
                Services.LogService.Warn($"Sidecar subtitle registration failed; playback continues: {subtitle}");
            var player = _player = new MediaPlayer(_libVlc) { Volume = Math.Clamp(volume, 0, 100) };
            if (VideoInputTrace.Enabled)
                player.VolumeChanged += (_, e) => VideoInputTrace.Record("backend-volume-confirmed", $"backend=LibVLC actual={(int)Math.Round(e.Volume * 100)}");
            // Native input is owned by MidPlayer's physical-input bridge.
            player.EnableMouseInput = false;
            player.EnableKeyInput = false;
            _surface.MediaPlayer = player;
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            player.Playing += (_, _) => { if (generation == _generation) started.TrySetResult(true); };
            player.EncounteredError += (_, _) =>
            {
                if (generation != _generation) return;
                started.TrySetResult(false);
                Post(() => { if (generation == _generation) Fail(path, "playback", new IOException("動画を再生できません。")); });
            };
            if (!player.Play(_media)) started.TrySetResult(false);
            bool success = await started.Task.WaitAsync(TimeSpan.FromSeconds(12), cancel);
            if (!success || cancel.IsCancellationRequested || generation != _generation) return false;
            _metadataCts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            _metadataTask = PublishMetadataAsync(_media, player, generation, _metadataCts.Token);
            if (start > 0 && !await CommandAsync(p => { p.Time = (long)start * 1000; return true; })) return false;
            if (!autoplay && !await PauseAsync()) return false;
            Services.LogService.Info($"[MidPlayerTiming] stage=video-backend-ready engine=LibVLC videoOutputs={player.VoutCount}");
            PlaybackStateChanged?.Invoke();
            loadedSuccessfully = true;
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception error) { if (generation == _generation) Fail(path, "load", error); return false; }
        finally { if (!loadedSuccessfully && generation == _generation) await StopForReuseAsync(); }
    }

    public Task<bool> PlayAsync() => CommandAsync(p =>
    {
        bool result;
        if (_stopped || p.State is VLCState.Ended or VLCState.Stopped) result = _media != null && p.Play(_media);
        else { p.SetPause(false); result = true; }
        if (result) _stopped = false;
        return result;
    });
    public Task<bool> PauseAsync() => CommandAsync(p => { p.SetPause(true); return true; });
    public Task<bool> StopAsync() => CommandAsync(p => { p.Stop(); try { p.Time = 0; } catch { } _stopped = true; _stoppedPosition = 0; return true; });
    public Task<bool> SeekAsync(long milliseconds) => CommandAsync(p =>
    {
        p.Time = Math.Max(0, milliseconds);
        if (_stopped) _stoppedPosition = Math.Max(0, milliseconds);
        return true;
    });
    public void SetVolume(int volume)
    {
        if (_player is not { } player) return;
        player.Volume = Math.Clamp(volume, 0, 100);
        if (VideoInputTrace.Enabled) VideoInputTrace.Record("backend-volume", $"backend=LibVLC requested={volume} actual={player.Volume}");
    }

    public bool TrySetPlaybackRate(double rate, out string? failureReason)
    {
        failureReason = null;
        if (!VideoPlaybackRateContract.UserRates.Any(available => Math.Abs(available - rate) < 0.001d))
        {
            failureReason = "指定された再生速度はサポートされていません。";
            return false;
        }
        MediaPlayer? player = _player;
        if (player == null || player.SetRate((float)rate) != 0)
        {
            failureReason = "LibVLCが再生速度を適用できませんでした。";
            return false;
        }
        if (Math.Abs(player.Rate - rate) > 0.01d)
        {
            failureReason = $"LibVLCの要求速度が一致しません (要求={rate:0.##}x、実値={player.Rate:0.##}x)。";
            return false;
        }
        return true;
    }

    public async Task<VideoFrameCaptureResult> CaptureFrameAsync(string outputPath, CancellationToken cancellationToken)
    {
        MediaPlayer? player = _player;
        int generation = _generation;
        if (player == null || player.VoutCount <= 0 || player.State is not (VLCState.Playing or VLCState.Paused))
            return new(false, "動画の映像出力がありません。再生中または一時停止中に保存してください。");

        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<MediaPlayerSnapshotTakenEventArgs> handler = (_, args) =>
        {
            if (string.Equals(Path.GetFullPath(args.Filename), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
                completed.TrySetResult(true);
        };
        player.SnapshotTaken += handler;
        try
        {
            if (!player.TakeSnapshot(0, outputPath, 0, 0))
                return new(false, "LibVLCが現在フレームの取得を受け付けませんでした。");

            await completed.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            if (generation != _generation || !ReferenceEquals(player, _player))
                return new(false, "保存中に再生媒体が切り替わりました。");
            return File.Exists(outputPath) && new FileInfo(outputPath).Length > 0
                ? new(true)
                : new(false, "LibVLCのフレーム出力ファイルを確認できませんでした。");
        }
        catch (OperationCanceledException)
        {
            return new(false, "フレーム保存を取り消しました。");
        }
        catch (TimeoutException)
        {
            return new(false, "LibVLCのフレーム出力がtimeoutしました。");
        }
        catch (Exception error)
        {
            return new(false, $"LibVLCのフレーム保存に失敗しました: {error.Message}");
        }
        finally
        {
            player.SnapshotTaken -= handler;
        }
    }

    private async Task<bool> CommandAsync(Func<MediaPlayer, bool> command)
    {
        var player = _player;
        int generation = _generation;
        bool success = await Task.Run(async () =>
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try { return generation == _generation && player != null && ReferenceEquals(player, _player) && command(player); }
            finally { _gate.Release(); }
        });
        if (generation == _generation) PlaybackStateChanged?.Invoke();
        return success;
    }

    public Task StopForReuseAsync()
    {
        ++_generation;
        var player = _player; var media = _media;
        var cts = _metadataCts; var metadata = _metadataTask;
        _player = null; _media = null; _metadataCts = null; _metadataTask = null;
        cts?.Cancel();
        _surface.MediaPlayer = null;
        _stopped = false; _stoppedPosition = 0;
        Metadata = new(); VideoDisplaySize = Size.Empty;
        return _cleanup = CleanupAsync(_cleanup, metadata, cts, player, media);
    }

    private async Task CleanupAsync(Task previous, Task? metadata, CancellationTokenSource? cts, MediaPlayer? player, Media? media)
    {
        try { await previous.ConfigureAwait(false); } catch { }
        if (metadata != null) { try { await metadata.ConfigureAwait(false); } catch { } }
        cts?.Dispose();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() => { try { player?.Stop(); } finally { try { player?.Dispose(); } finally { media?.Dispose(); } } }).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopForReuseAsync();
        var engine = _libVlc; _libVlc = null;
        if (engine != null) await Task.Run(engine.Dispose);
        _surface.Dispose();
    }

    private async Task PublishMetadataAsync(Media media, MediaPlayer player, int generation, CancellationToken token)
    {
        MetadataParseCount++;
        try
        {
            if (await media.Parse(MediaParseOptions.ParseLocal, 5000, token).ConfigureAwait(false) != MediaParsedStatus.Done || token.IsCancellationRequested) return;
            int selectedVideoId = player.VideoTrack;
            int selectedAudioId = player.AudioTrack;
            MediaTrack? selectedVideo = media.Tracks.FirstOrDefault(track =>
                track.TrackType == TrackType.Video && track.Id == selectedVideoId);
            MediaTrack? selectedAudio = media.Tracks.FirstOrDefault(track =>
                track.TrackType == TrackType.Audio && track.Id == selectedAudioId);
            Size size = Size.Empty;
            string video = selectedVideoId < 0 ? "なし" : "不明";
            string audio = selectedAudioId < 0 ? "なし" : "不明";
            string videoTip = "-", audioTip = "-";
            if (selectedVideo is { } videoTrack)
            {
                var v = videoTrack.Data.Video;
                size = VideoPlaybackGeometry.CalculateDisplaySize(v.Width, v.Height, v.SarNum, v.SarDen,
                    v.Orientation is VideoOrientation.LeftTop or VideoOrientation.LeftBottom or VideoOrientation.RightTop or VideoOrientation.RightBottom);
                string fps = v.FrameRateNum > 0 && v.FrameRateDen > 0
                    ? (v.FrameRateNum / (double)v.FrameRateDen).ToString("0.###", CultureInfo.InvariantCulture) + " fps"
                    : string.Empty;
                video = JoinKnownValues(FormatTrackCodecDisplayName(videoTrack), size.IsEmpty ? null : $"{size.Width}×{size.Height}", fps);
                videoTip = BuildTrackToolTip(videoTrack);
            }
            if (selectedAudio is { } audioTrack)
            {
                var a = audioTrack.Data.Audio;
                audio = JoinKnownValues(FormatTrackCodecDisplayName(audioTrack),
                    a.Rate > 0 ? (a.Rate / 1000d).ToString("0.###", CultureInfo.InvariantCulture) + " kHz" : string.Empty,
                    a.Channels > 0 ? $"{a.Channels}ch" : string.Empty);
                audioTip = BuildTrackToolTip(audioTrack);
            }
            Post(() =>
            {
                if (generation != _generation || !ReferenceEquals(player, _player)) return;
                Metadata = new(video, audio, videoTip, audioTip); VideoDisplaySize = size;
                PlaybackStateChanged?.Invoke();
                if (!size.IsEmpty) VideoDisplaySizeAvailable?.Invoke(size);
            });
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch { /* Metadata is advisory; retain the actual playback result. */ }
    }
    private static string FormatTrackCodecDisplayName(MediaTrack track)
    {
        string? known = NormalizeCodecFourcc(track.Codec) ?? NormalizeCodecFourcc(track.OriginalFourcc);
        if (!string.IsNullOrWhiteSpace(known)) return known;
        return string.IsNullOrWhiteSpace(track.Description) ? "不明" : track.Description.Trim();
    }

    private static string? NormalizeCodecFourcc(uint value)
    {
        if (value == 0) return null;
        string fourcc = new string(new[]
        {
            (char)(value & 0xff),
            (char)((value >> 8) & 0xff),
            (char)((value >> 16) & 0xff),
            (char)((value >> 24) & 0xff)
        }).Trim();
        return fourcc.ToUpperInvariant() switch
        {
            "H264" or "AVC1" or "X264" => "H.264 / AVC",
            "HEVC" or "H265" or "HVC1" or "HEV1" => "HEVC / H.265",
            "AV01" => "AV1",
            "VP90" => "VP9",
            "VP80" => "VP8",
            "MP4V" or "DIVX" or "XVID" => "MPEG-4",
            "MP2V" or "MPG2" => "MPEG-2",
            "MP1V" => "MPEG-1",
            "MP4A" or "AAC " or "AACL" or "AACH" => "AAC",
            "MP3 " or "MPGA" => "MP3",
            "AC-3" or "A52 " => "AC-3",
            "EAC3" => "E-AC-3",
            "DTS " => "DTS",
            "OPUS" => "Opus",
            "VORB" => "Vorbis",
            "FLAC" => "FLAC",
            "WMA " or "WMAV" => "WMA",
            "MJPG" => "Motion JPEG",
            "THEO" => "Theora",
            _ => null
        };
    }
    private static string BuildTrackToolTip(MediaTrack track)
        => $"名称: {FormatTrackCodecDisplayName(track)}{Environment.NewLine}codec: {(track.Codec == 0 ? "-" : $"0x{track.Codec:X8}")}{Environment.NewLine}FourCC: {(track.OriginalFourcc == 0 ? "-" : $"0x{track.OriginalFourcc:X8}")}";
    private static string JoinKnownValues(params string?[] values) => string.Join(" / ", values.Where(x => !string.IsNullOrWhiteSpace(x) && x != "-")) is { Length: > 0 } text ? text : "不明";
    private void Post(Action action)
    {
        if (_surface.IsDisposed || !_surface.IsHandleCreated) return;
        try { _surface.BeginInvoke(action); } catch (InvalidOperationException) { }
    }
    private void Fail(string path, string phase, Exception error)
    {
        LastError = $"LibVLC: {error.Message}";
        Services.LogService.Error($"[MidPlayer Video] engine=LibVLC phase={phase} file={Path.GetFileName(path)} HRESULT=0x{error.HResult:X8}: {error.Message}");
        PlaybackFailed?.Invoke(LastError);
    }
}
