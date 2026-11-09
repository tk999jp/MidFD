using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MidFD.MediaFoundation;

namespace MidFD;

internal sealed class VideoBackendCapabilityProbe : IVideoBackendCapabilityProbe
{
    private readonly SemaphoreSlim _nativeGate = new(1, 1);
    private readonly Dictionary<string, (DateTimeOffset At, VideoBackendCapabilityResult Value)> _cache = new();
    private readonly Func<VideoCodecInfo, VideoBackendCapabilityResult> _nativeProbe;
    private readonly Func<DateTimeOffset> _clock;
    internal int NativeQueryCount { get; private set; }

    internal VideoBackendCapabilityProbe(Func<VideoCodecInfo, VideoBackendCapabilityResult>? nativeProbe = null, Func<DateTimeOffset>? clock = null)
    { _nativeProbe = nativeProbe ?? ProbeWindows; _clock = clock ?? (() => DateTimeOffset.UtcNow); }

    public async Task<(VideoBackendCapabilityResult LibVlc, VideoBackendCapabilityResult Mf)> ProbeAsync(VideoCodecInfo info, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        // LibVLC 3 exposes no legitimate per-codec decoder preflight. Keep that
        // capability Unknown, but still query MF for a positive OS capability.
        var lib = VideoBackendCapabilityResult.Unknown("LibVLC公開APIに個別decoder可否の事前照会なし。Parse/codec名はdecoder存在の証明ではありません");
        var mf = await ProbeMediaFoundationAsync(info, cancel);
        return (lib, mf);
    }

    // Used by the production Auto route and scoped native capability diagnostics.
    internal async Task<VideoBackendCapabilityResult> ProbeMediaFoundationAsync(VideoCodecInfo info, CancellationToken cancel)
    {
        var timer = Stopwatch.StartNew();
        cancel.ThrowIfCancellationRequested();
        if (!info.Complete)
        {
            LogCapabilityTiming(info, false, "unknown", timer.Elapsed.TotalMilliseconds);
            return VideoBackendCapabilityResult.Unknown(info.Failure ?? "stream情報を取得できず判定不能");
        }
        string key = JsonSerializer.Serialize(new { info.MimeType, info.MimeWithCodecs, info.Tracks });
        lock (_cache)
            if (_cache.TryGetValue(key, out var entry) && _clock() - entry.At < TimeSpan.FromSeconds(15))
            {
                LogCapabilityTiming(info, true, entry.Value.State.ToString(), timer.Elapsed.TotalMilliseconds);
                return entry.Value with { FromCache = true };
            }
        if (!_nativeGate.Wait(0))
        {
            LogCapabilityTiming(info, false, "busy-unknown", timer.Elapsed.TotalMilliseconds);
            return VideoBackendCapabilityResult.Unknown("MF能力照会中のため判定不能");
        }
        var pending = Task.Run(() =>
        {
            try
            {
                if (cancel.IsCancellationRequested) return VideoBackendCapabilityResult.Unknown("能力照会取消");
                NativeQueryCount++;
                return _nativeProbe(info);
            }
            catch (Exception error) { return VideoBackendCapabilityResult.Unknown($"MF能力照会失敗: {error.GetType().Name}: {error.Message}"); }
            finally { _nativeGate.Release(); }
        });
        try
        {
            var result = await pending.WaitAsync(TimeSpan.FromMilliseconds(1200), cancel);
            cancel.ThrowIfCancellationRequested();
            lock (_cache)
            {
                if (_cache.Count >= 32) _cache.Remove(_cache.Keys.First());
                _cache[key] = (_clock(), result);
            }
            LogCapabilityTiming(info, false, result.State.ToString(), timer.Elapsed.TotalMilliseconds);
            return result;
        }
        catch (TimeoutException)
        {
            // Native COM enumeration has no cancellation API. Only one bounded
            // worker may remain; its finally still releases all native objects.
            return VideoBackendCapabilityResult.Unknown("MF能力照会は時間上限。非対応とは判定しません");
        }
    }

    private static VideoBackendCapabilityResult ProbeWindows(VideoCodecInfo info)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 2)) return VideoBackendCapabilityResult.Unknown("MF能力照会APIを利用できません");
        var total = Stopwatch.StartNew();
        double startupMs = 0, mftEnumerationMs = 0, engineCreateMs = 0, canPlayTypeMs = 0, cleanupMs = 0, shutdownMs = 0;
        string? mime = info.MimeWithCodecs ?? info.MimeType;
        int? answer = null;
        VideoBackendCapabilityResult? result = null;
        var stage = Stopwatch.StartNew();
        MfNative.Check(MfNative.MFStartup(0x20070, 0), "capability MFStartup");
        startupMs = stage.Elapsed.TotalMilliseconds;
        IMFAttributes? attributes = null; IMFMediaEngineClassFactory? factory = null; IMFMediaEngine? engine = null;
        var notify = new CapabilityNotify();
        try
        {
            var enumerated = new Dictionary<(VideoCodecTrackKind, Guid), (int? Sw, int? Hw, string? Failure)>();
            var decoders = info.Tracks.Select(track =>
            {
                Guid? subtype = Subtype(track);
                if (subtype == null) return new VideoDecoderCandidate(track, null, null, null, "実codecのMF subtype対応付けは判定不能");
                var key = (track.Kind, subtype.Value);
                if (!enumerated.TryGetValue(key, out var counts))
                {
                    stage.Restart();
                    try
                    {
                        // SYNC|ASYNC|LOCAL|SORTANDFILTER vs HARDWARE|SORTANDFILTER.
                        // Counts are candidates, not proof of decoding/profile support.
                        counts = (Count(track.Kind, subtype.Value, 0x53), Count(track.Kind, subtype.Value, 0x44), null);
                    }
                    catch (Exception error) { counts = (null, null, $"MFTEnumEx失敗: {error.Message}"); }
                    mftEnumerationMs += stage.Elapsed.TotalMilliseconds;
                    enumerated[key] = counts;
                }
                return new VideoDecoderCandidate(track, subtype, counts.Sw, counts.Hw, counts.Failure);
            }).ToArray();
            if (mime != null)
            {
                stage.Restart();
                MfNative.Check(MfNative.MFCreateAttributes(out attributes, 1), "capability attributes");
                Guid callback = MfNative.CallbackKey;
                MfNative.Check(attributes.SetUnknown(ref callback, notify), "capability callback");
                factory = (IMFMediaEngineClassFactory)Activator.CreateInstance(Type.GetTypeFromCLSID(MfNative.FactoryClsid, true)!)!;
                MfNative.Check(factory.CreateInstance(0, attributes, out engine), "capability CreateInstance");
                engineCreateMs = stage.Elapsed.TotalMilliseconds;
                // No SetSource/Load/Play, HWND or render surface in capability probing.
                stage.Restart();
                MfNative.Check(engine.CanPlayType(mime, out int value), "CanPlayType"); answer = value;
                canPlayTypeMs = stage.Elapsed.TotalMilliseconds;
            }
            result = Evaluate(info, decoders, answer, mime);
            return result;
        }
        finally
        {
            stage.Restart();
            try { if (engine != null) engine.Shutdown(); }
            finally
            {
                try { MfNative.Release(engine); MfNative.Release(attributes); MfNative.Release(factory); }
                finally
                {
                    cleanupMs = stage.Elapsed.TotalMilliseconds;
                    GC.KeepAlive(notify);
                    stage.Restart();
                    MfNative.Check(MfNative.MFShutdown(), "capability MFShutdown");
                    shutdownMs = stage.Elapsed.TotalMilliseconds;
                    string mediaId = MediaId(info.Path);
                    Services.LogService.Info($"[MidPlayerTiming] stage=mf-capability-native processId={Environment.ProcessId} mediaId={mediaId} outcome={result?.State.ToString() ?? "error"} mimeKnown={mime != null} mfStartupMs={startupMs:0.0} mftEnumerationMs={mftEnumerationMs:0.0} mediaEngineCreateMs={engineCreateMs:0.0} canPlayTypeMs={canPlayTypeMs:0.0} cleanupMs={cleanupMs:0.0} mfShutdownMs={shutdownMs:0.0} totalMs={total.Elapsed.TotalMilliseconds:0.0}");
                }
            }
        }
    }

    private static void LogCapabilityTiming(VideoCodecInfo info, bool cacheHit, string outcome, double elapsedMs)
        => Services.LogService.Info($"[MidPlayerTiming] stage=mf-capability-query processId={Environment.ProcessId} mediaId={MediaId(info.Path)} cacheHit={cacheHit} outcome={outcome} elapsedMs={elapsedMs:0.0}");

    private static string MediaId(string path)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path))))[..12];

    internal static VideoBackendCapabilityResult Evaluate(VideoCodecInfo info, IReadOnlyList<VideoDecoderCandidate> decoders, int? answer, string? mime)
    {
        string counts = string.Join("; ", decoders.Select(item => $"{item.Track.Kind}/{item.Track.CodecName}: SW={item.SoftwareCount?.ToString() ?? "?"}, HW={item.HardwareCount?.ToString() ?? "?"}"));
        var state = VideoBackendCapability.Unknown;
        string why = "候補数だけでは再生可否を確定できません";
        if (info.Complete && answer == 0 && mime != null)
        { state = VideoBackendCapability.Unsupported; why = "CanPlayType=not supported（指定container／codec条件）"; }
        else if (info.Complete && decoders.Count == info.Tracks.Count && decoders.Count > 0)
        {
            bool none = decoders.Any(item => item.Subtype != null && item.SoftwareCount == 0 && item.HardwareCount == 0);
            bool all = decoders.All(item => item.Subtype != null && item.SoftwareCount.HasValue && item.HardwareCount.HasValue && item.SoftwareCount + item.HardwareCount > 0);
            if (none && answer != 2) { state = VideoBackendCapability.Unsupported; why = "実codecに一致する利用可能なMF decoder候補なし"; }
            else if (all && answer == 2 && info.MimeWithCodecs != null && mime == info.MimeWithCodecs)
            { state = VideoBackendCapability.Supported; why = "実codec条件のCanPlayType=probably＋全映像／音声trackのMFT候補あり（再生保証なし）"; }
            else if (answer == 1) why = "CanPlayType=maybe（判定不能）";
            else if (none && answer == 2) why = "MFT候補なしとCanPlayType=probablyが不一致のため判定不能";
        }
        return new(state, $"{why}。{counts}", decoders, answer, mime);
    }

    private static int Count(VideoCodecTrackKind kind, Guid subtype, uint flags)
    {
        IntPtr values = IntPtr.Zero; uint count = 0;
        try
        {
            var input = new MftRegisterTypeInfo { MajorType = kind == VideoCodecTrackKind.Video ? MfNative.VideoMajorType : MfNative.AudioMajorType, Subtype = subtype };
            MfNative.Check(MfNative.MFTEnumEx(kind == VideoCodecTrackKind.Video ? MfNative.VideoDecoderCategory : MfNative.AudioDecoderCategory,
                flags, ref input, IntPtr.Zero, out values, out count), "MFTEnumEx");
            return checked((int)count);
        }
        finally
        {
            if (values != IntPtr.Zero)
            {
                for (uint i = 0; i < count; i++)
                {
                    IntPtr item = Marshal.ReadIntPtr(values, checked((int)i * IntPtr.Size));
                    if (item != IntPtr.Zero) Marshal.Release(item);
                }
                Marshal.FreeCoTaskMem(values);
            }
        }
    }

    internal static Guid? Subtype(VideoCodecTrack track)
    {
        uint? value = track.Kind == VideoCodecTrackKind.Video ? track.CodecName switch
        {
            "h264" or "avc1" or "avc3" => 0x34363248,
            "hevc" or "hvc1" or "hev1" => 0x43564548,
            "vp9" or "vp90" => 0x30395056,
            "av01" => 0x31305641,
            _ => null
        } : track.CodecName switch
        {
            "mp4a" => 0x1610,
            "mp3" => 0x55,
            "flac" => 0xf1ac,
            "opus" => 0x704f,
            _ => null
        };
        return value.HasValue ? new Guid(unchecked((int)value.Value), 0, 0x10, 0x80, 0, 0, 0xaa, 0, 0x38, 0x9b, 0x71) : null;
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class CapabilityNotify : IMFMediaEngineNotify
    {
        public int EventNotify(uint eventId, nuint parameter1, uint parameter2) => 0;
    }
}
