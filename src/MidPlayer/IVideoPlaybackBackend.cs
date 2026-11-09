namespace MidFD;

internal enum VideoPlaybackState { Empty, Opening, Playing, Paused, Stopped, Ended, Error }
internal sealed record VideoPlaybackMetadata(string Video = "-", string Audio = "-", string VideoToolTip = "-", string AudioToolTip = "-", string? Notice = null);
internal sealed record VideoFrameCaptureResult(bool Succeeded, string? Error = null);
internal static class VideoPlaybackRateContract
{
    internal static IReadOnlyList<double> UserRates { get; } = Array.AsReadOnly<double>([0.5d, 0.75d, 1d, 1.25d, 1.5d, 2d]);
    internal static IReadOnlyList<double> NormalOnly { get; } = Array.AsReadOnly<double>([1d]);
}

internal interface IVideoPlaybackBackend : IAsyncDisposable
{
    string BackendKind { get; }
    Control RenderSurface { get; }
    bool HasMedia { get; }
    bool IsPlaying { get; }
    bool IsSeekable { get; }
    long Position { get; }
    long Duration { get; }
    Size VideoDisplaySize { get; }
    VideoPlaybackState State { get; }
    VideoPlaybackMetadata Metadata { get; }
    string? LastError { get; }
    IReadOnlyList<double> SupportedPlaybackRates => VideoPlaybackRateContract.NormalOnly;
    double PlaybackRate => 1d;
    string? PlaybackRateRestrictionReason => null;
    bool TrySetPlaybackRate(double rate, out string? failureReason)
    {
        failureReason = Math.Abs(rate - 1d) < 0.001d ? null : "この再生エンジンでは1.0倍速以外を利用できません。";
        return failureReason == null;
    }
    Task<bool> LoadAsync(string path, int start, int volume, bool autoplay, CancellationToken cancel);
    Task<bool> PlayAsync();
    Task<bool> PauseAsync();
    Task<bool> StopAsync();
    Task<bool> SeekAsync(long milliseconds);
    Task<VideoFrameCaptureResult> CaptureFrameAsync(string outputPath, CancellationToken cancellationToken);
    void SetVolume(int volume);
    Task StopForReuseAsync();
    event Action? PlaybackStateChanged;
    event Action<string>? PlaybackFailed;
    event Action<Size>? VideoDisplaySizeAvailable;
}
