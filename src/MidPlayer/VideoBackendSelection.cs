using System.Text;
using MidFD.Configuration;

namespace MidFD;

internal enum VideoBackendCapability { Unknown, Supported, Unsupported }
internal enum VideoCodecTrackKind { Video, Audio }
internal enum VideoSubtitleRequirement { Unknown, None, LibVlcRequired }

internal sealed record VideoCodecTrack(VideoCodecTrackKind Kind, int Id, uint Codec, uint OriginalFourcc,
    int? Profile = null, int? Level = null, int? BitDepth = null, uint Width = 0, uint Height = 0,
    uint Channels = 0, uint SampleRate = 0)
{
    public string CodecName => FormatFourCc(Codec);

    private static string FormatFourCc(uint value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        return bytes.All(value => value is >= 32 and <= 126)
            ? Encoding.ASCII.GetString(bytes).TrimEnd().ToLowerInvariant()
            : $"0x{value:X8}";
    }
}

internal sealed record VideoCodecInfo(string Path, bool Complete, string Container, string? MimeType,
    string? MimeWithCodecs, IReadOnlyList<VideoCodecTrack> Tracks, string? Failure = null,
    VideoPlaybackMetadata? Metadata = null, Size DisplaySize = default, bool FromCache = false,
    VideoSubtitleRequirement SubtitleRequirement = VideoSubtitleRequirement.Unknown,
    SubtitleSidecarResolution? SidecarResolution = null, bool? EmbeddedSubtitlesPresent = null)
{
    public string VideoCodec => Summary(VideoCodecTrackKind.Video);
    public string AudioCodec => Summary(VideoCodecTrackKind.Audio);
    private string Summary(VideoCodecTrackKind kind)
        => string.Join(", ", Tracks.Where(track => track.Kind == kind).Select(track => track.CodecName)) is { Length: > 0 } value ? value : "-";
    internal static VideoCodecInfo Unknown(string path, string reason)
        => new(path, false, "判定不能", null, null, [], reason);
}

internal sealed record VideoDecoderCandidate(VideoCodecTrack Track, Guid? Subtype, int? SoftwareCount,
    int? HardwareCount, string? Failure = null);

internal sealed record VideoBackendCapabilityResult(VideoBackendCapability State, string Evidence,
    IReadOnlyList<VideoDecoderCandidate>? Decoders = null, int? CanPlayType = null, string? QueriedMime = null,
    bool FromCache = false)
{
    internal static VideoBackendCapabilityResult Unknown(string reason) => new(VideoBackendCapability.Unknown, reason);
}

internal sealed record VideoBackendSelection(string RequestedMode, string? SelectedBackend, string VideoCodec,
    string AudioCodec, VideoBackendCapabilityResult LibVlcCapability, VideoBackendCapabilityResult MfCapability,
    string Reason, VideoCodecInfo? CodecInfo = null, double ElapsedMilliseconds = 0)
{
    internal string ToolTip => $"設定: {RequestedMode}\n使用エンジン: {SelectedBackend ?? "未選択"}\n理由: {Reason}\n映像codec: {VideoCodec}\n音声codec: {AudioCodec}\ncontainer: {CodecInfo?.Container ?? "-"}\ncodec条件: {CodecInfo?.MimeWithCodecs ?? "判定不能"}\n字幕互換: {CodecInfo?.SubtitleRequirement.ToString() ?? "判定不能"}\nLibVLC: {LibVlcCapability.State} / {LibVlcCapability.Evidence}\nMF: {MfCapability.State} / {MfCapability.Evidence}";
    internal static VideoBackendSelection Manual(string mode)
        => new(mode, mode, "-", "-", VideoBackendCapabilityResult.Unknown("手動固定"),
            VideoBackendCapabilityResult.Unknown("手動固定"), "手動指定を使用（自動切替なし）");

    internal static VideoBackendSelection DirectAutoLibVlc()
        => new(VideoPlaybackBackendKind.Auto, VideoPlaybackBackendKind.LibVLC, "-", "-",
            VideoBackendCapabilityResult.Unknown("再生前のdecoder判定は行わない"),
            VideoBackendCapabilityResult.Unknown("通常OpenではMF能力を照会しない"),
            "AutoはLibVLCを直接起動します。LibVLC APIからdecoder非対応を一般的な再生失敗と確実に区別できないため、MFへ自動切替しません。");

    internal static VideoBackendSelection MediaFoundationOnly(string requestedMode)
        => new(requestedMode, VideoPlaybackBackendKind.MediaFoundation, "-", "-",
            VideoBackendCapabilityResult.Unknown("この配布ではLibVLCを含めず、decoder事前照会を行いません"),
            VideoBackendCapabilityResult.Unknown("通常OpenではMF能力を事前照会しません"),
            string.Equals(VideoPlaybackBackendKind.Normalize(requestedMode), VideoPlaybackBackendKind.LibVLC, StringComparison.OrdinalIgnoreCase)
                ? "LibVLCを含まない標準配布のため、Media Foundationを使用します。"
                : "この標準配布ではMedia Foundationを使用します。");
}

internal interface IVideoCodecProbe
{
    Task<VideoCodecInfo> ProbeAsync(string path, CancellationToken cancellationToken);
}

internal interface IVideoBackendCapabilityProbe
{
    Task<(VideoBackendCapabilityResult LibVlc, VideoBackendCapabilityResult Mf)> ProbeAsync(VideoCodecInfo info, CancellationToken cancellationToken);
}

internal interface IVideoPlaybackProbeConsumer
{
    void UseCodecProbe(VideoCodecInfo? info);
}

internal interface IVideoCodecProbeResourceOwner
{
    Task ReleaseResourcesAsync();
    Task TransferResourcesAsync(IVideoPlaybackBackend backend, CancellationToken cancellationToken);
}
