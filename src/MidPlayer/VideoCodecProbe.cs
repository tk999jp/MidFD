using System.Globalization;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using LibVLCSharp.Shared;

namespace MidFD;

internal sealed class VideoCodecProbe : IVideoCodecProbe, IVideoCodecProbeResourceOwner, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, (long Length, long Modified, VideoCodecInfo Info)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private LibVLC? _runtime;
    internal int ParseCount { get; private set; }

    public async Task<VideoCodecInfo> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(path);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // File metadata can block on network paths too; keep all probe I/O off the UI thread.
            return await Task.Run(async () =>
            {
                var total = Stopwatch.StartNew();
                cancellationToken.ThrowIfCancellationRequested();
                var file = new FileInfo(fullPath);
                long length = file.Length, modified = file.LastWriteTimeUtc.Ticks;
                if (_cache.TryGetValue(fullPath, out var cached) && cached.Length == length && cached.Modified == modified)
                {
                    var subtitleTimer = Stopwatch.StartNew();
                    VideoCodecInfo cachedInfo = ApplySubtitleResolution(cached.Info with { FromCache = true },
                        SubtitleSidecarResolver.ResolveForPlaybackProbe(fullPath, cancellationToken));
                    LogProbeTiming(fullPath, "cache", true, cachedInfo.Complete ? "complete" : "unknown",
                        subtitleTimer.Elapsed.TotalMilliseconds, total.Elapsed.TotalMilliseconds);
                    return cachedInfo;
                }
                VideoCodecInfo info;
                var fastProbeTimer = Stopwatch.StartNew();
                VideoCodecInfo? fastInfo;
                try { fastInfo = await VideoContainerProbe.TryReadSimpleMp4Async(fullPath, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                { fastInfo = null; }
                string mediaId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fullPath)))[..12];
                Services.LogService.Info($"[MidPlayerTiming] stage=video-codec-fast-probe processId={Environment.ProcessId} mediaId={mediaId} accepted={fastInfo != null} elapsedMs={fastProbeTimer.Elapsed.TotalMilliseconds:0.0}");
                if (fastInfo != null)
                {
                    var subtitleTimer = Stopwatch.StartNew();
                    info = ApplySubtitleResolution(fastInfo,
                        SubtitleSidecarResolver.ResolveForPlaybackProbe(fullPath, cancellationToken));
                    file.Refresh();
                    if (!file.Exists || file.Length != length || file.LastWriteTimeUtc.Ticks != modified)
                        return VideoCodecInfo.Unknown(fullPath, "解析中にfileが変更されました");
                    if (_cache.Count >= 16) _cache.Remove(_cache.Keys.First());
                    _cache[fullPath] = (length, modified, info);
                    LogProbeTiming(fullPath, "ISO-BMFF", false, info.Complete ? "complete" : "unknown",
                        subtitleTimer.Elapsed.TotalMilliseconds, total.Elapsed.TotalMilliseconds);
                    return info;
                }
                ParseCount++;
                info = await ParseAsync(fullPath, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                file.Refresh();
                if (!file.Exists || file.Length != length || file.LastWriteTimeUtc.Ticks != modified)
                    return VideoCodecInfo.Unknown(fullPath, "解析中にfileが変更されました");
                if (info.Complete)
                {
                    if (_cache.Count >= 16) _cache.Remove(_cache.Keys.First());
                    _cache[fullPath] = (length, modified, info);
                }
                return info;
            }, CancellationToken.None);
        }
        finally { _gate.Release(); }
    }

    private async Task<VideoCodecInfo> ParseAsync(string path, CancellationToken cancel)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        budget.CancelAfter(TimeSpan.FromMilliseconds(1200));
        var total = Stopwatch.StartNew();
        double libVlcInitializeMs = 0, mediaParseMs = 0, containerReadMs = 0, subtitleResolveMs = 0;
        VideoCodecInfo? parsed = null;
        try
        {
            var stage = Stopwatch.StartNew();
            Core.Initialize();
            _runtime ??= new LibVLC("--no-video-title-show", "--quiet");
            libVlcInitializeMs = stage.Elapsed.TotalMilliseconds;
            using var media = new Media(_runtime, path, FromType.FromPath);
            media.AddOption(":no-sub-autodetect-file");
            // Parsing only: no MediaPlayer, Play, audio output, or video HWND.
            stage.Restart();
            MediaParsedStatus status = await media.Parse(MediaParseOptions.ParseLocal, 900, budget.Token).ConfigureAwait(false);
            mediaParseMs = stage.Elapsed.TotalMilliseconds;
            if (status != MediaParsedStatus.Done)
                return parsed = VideoCodecInfo.Unknown(path, "stream解析は判定不能（parse未完了／timeout）");
            var allTracks = media.Tracks;
            var native = allTracks.Where(track => track.TrackType is TrackType.Video or TrackType.Audio).ToArray();
            if (native.Length == 0 || native.Length > 256)
                return parsed = VideoCodecInfo.Unknown(path, "映像／音声track情報を取得できませんでした");
            var tracks = native.Select(track => new VideoCodecTrack(
                track.TrackType == TrackType.Video ? VideoCodecTrackKind.Video : VideoCodecTrackKind.Audio,
                track.Id, track.Codec, track.OriginalFourcc, track.Profile >= 0 ? track.Profile : null,
                track.Level >= 0 ? track.Level : null,
                Width: track.TrackType == TrackType.Video ? track.Data.Video.Width : 0,
                Height: track.TrackType == TrackType.Video ? track.Data.Video.Height : 0,
                Channels: track.TrackType == TrackType.Audio ? track.Data.Audio.Channels : 0,
                SampleRate: track.TrackType == TrackType.Audio ? track.Data.Audio.Rate : 0)).ToArray();
            stage.Restart();
            var container = await VideoContainerProbe.ReadAsync(path, tracks.Any(track => track.Kind == VideoCodecTrackKind.Video), budget.Token);
            containerReadMs = stage.Elapsed.TotalMilliseconds;
            tracks = container.AddAvcConfiguration(tracks);
            var (metadata, size) = MetadataFromTracks(native);
            var info = new VideoCodecInfo(path, true, container.Name, container.Mime, container.ComposeMime(tracks), tracks,
                Metadata: metadata, DisplaySize: size, EmbeddedSubtitlesPresent: allTracks.Any(track => track.TrackType == TrackType.Text));
            stage.Restart();
            parsed = ApplySubtitleResolution(info, SubtitleSidecarResolver.ResolveForPlaybackProbe(path, budget.Token));
            subtitleResolveMs = stage.Elapsed.TotalMilliseconds;
            return parsed;
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        { return parsed = VideoCodecInfo.Unknown(path, "stream解析は判定不能（時間上限）"); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { return parsed = VideoCodecInfo.Unknown(path, $"stream解析は判定不能: {error.GetType().Name}: {error.Message}"); }
        finally { LogDetailedProbeTiming(path, parsed?.Complete == true ? "complete" : "unknown", libVlcInitializeMs, mediaParseMs, containerReadMs, subtitleResolveMs, total.Elapsed.TotalMilliseconds); }
    }

    private static void LogProbeTiming(string path, string parser, bool cacheHit, string outcome, double subtitleResolveMs, double totalMs)
    {
        string mediaId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path))))[..12];
        Services.LogService.Info($"[MidPlayerTiming] stage=video-codec-probe processId={Environment.ProcessId} mediaId={mediaId} parser={parser} cacheHit={cacheHit} outcome={outcome} subtitleResolveMs={subtitleResolveMs:0.0} totalMs={totalMs:0.0}");
    }

    private static void LogDetailedProbeTiming(string path, string outcome, double libVlcInitMs, double mediaParseMs,
        double containerReadMs, double subtitleResolveMs, double totalMs)
    {
        string mediaId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path))))[..12];
        Services.LogService.Info($"[MidPlayerTiming] stage=video-codec-libvlc-parse processId={Environment.ProcessId} mediaId={mediaId} cacheHit=False outcome={outcome} libVlcInitMs={libVlcInitMs:0.0} mediaParseMs={mediaParseMs:0.0} containerReadMs={containerReadMs:0.0} subtitleResolveMs={subtitleResolveMs:0.0} totalMs={totalMs:0.0}");
    }

    private static VideoCodecInfo ApplySubtitleResolution(VideoCodecInfo info, SubtitleSidecarResolution sidecars)
    {
        bool unreadable = sidecars.IgnoredSidecars.Any(item => item.Reason.StartsWith("ReadFailure:", StringComparison.Ordinal));
        VideoSubtitleRequirement requirement = info.EmbeddedSubtitlesPresent switch
        {
            true => VideoSubtitleRequirement.LibVlcRequired,
            false when sidecars.SupportedPath != null => VideoSubtitleRequirement.LibVlcRequired,
            false when unreadable => VideoSubtitleRequirement.Unknown,
            false => VideoSubtitleRequirement.None,
            _ => VideoSubtitleRequirement.Unknown
        };
        return info with { SubtitleRequirement = requirement, SidecarResolution = sidecars };
    }

    public async Task TransferResourcesAsync(IVideoPlaybackBackend backend, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_runtime != null && backend is LibVlcPlaybackBackend lib && lib.AdoptParsedRuntime(_runtime)) _runtime = null;
            else await ReleaseRuntimeCoreAsync();
        }
        finally { _gate.Release(); }
    }

    public async Task ReleaseResourcesAsync()
    {
        await _gate.WaitAsync();
        try { await ReleaseRuntimeCoreAsync(); }
        finally { _gate.Release(); }
    }

    private async Task ReleaseRuntimeCoreAsync()
    {
        var engine = _runtime; _runtime = null;
        if (engine != null) await Task.Run(engine.Dispose);
    }
    public ValueTask DisposeAsync() => new(ReleaseResourcesAsync());

    private static (VideoPlaybackMetadata, Size) MetadataFromTracks(MediaTrack[] tracks)
    {
        Size size = Size.Empty;
        string video = "-", audio = "-", videoTip = "-", audioTip = "-";
        foreach (var track in tracks)
        {
            string name = string.IsNullOrWhiteSpace(track.Description) ? "-" : track.Description.Trim();
            string tip = $"名称: {name}\ncodec: 0x{track.Codec:X8}\nFourCC: 0x{track.OriginalFourcc:X8}";
            if (track.TrackType == TrackType.Video && video == "-")
            {
                var v = track.Data.Video;
                size = VideoPlaybackGeometry.CalculateDisplaySize(v.Width, v.Height, v.SarNum, v.SarDen,
                    v.Orientation is VideoOrientation.LeftTop or VideoOrientation.LeftBottom or VideoOrientation.RightTop or VideoOrientation.RightBottom);
                string fps = v.FrameRateNum > 0 && v.FrameRateDen > 0 ? (v.FrameRateNum / (double)v.FrameRateDen).ToString("0.###", CultureInfo.InvariantCulture) + " fps" : "-";
                video = Join(name, size.IsEmpty ? "-" : $"{size.Width}×{size.Height}", fps); videoTip = tip;
            }
            else if (track.TrackType == TrackType.Audio && audio == "-")
            {
                var a = track.Data.Audio;
                audio = Join(name, a.Rate > 0 ? (a.Rate / 1000d).ToString("0.###", CultureInfo.InvariantCulture) + " kHz" : "-", a.Channels > 0 ? $"{a.Channels}ch" : "-"); audioTip = tip;
            }
        }
        return (new(video, audio, videoTip, audioTip), size);
    }
    private static string Join(params string[] values) => string.Join(" / ", values.Where(value => value != "-")) is { Length: > 0 } text ? text : "-";
    internal static string FourCc(uint value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        return bytes.All(b => b is >= 32 and <= 126) ? Encoding.ASCII.GetString(bytes).TrimEnd().ToLowerInvariant() : $"0x{value:X8}";
    }
}
