using System.Collections.ObjectModel;

namespace MidFD.Services;

/// <summary>Video file containers known to the packaged LibVLC demuxers.</summary>
public static class VideoMediaCatalog
{
    private static readonly string[] Extensions =
    [
        ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".mpg", ".mpeg", ".webm", ".mkv",
        ".3gp", ".3g2", ".3gpp", ".3gp2", ".asf", ".vob", ".m1v", ".m2v",
        ".mts", ".m2ts", ".m2t", ".ogv", ".ogm"
    ];

    private static readonly ReadOnlyCollection<string> DialogExtensions =
        Array.AsReadOnly(Extensions.Append(".ts").ToArray());

    private static readonly HashSet<string> ExtensionSet = new(Extensions, StringComparer.OrdinalIgnoreCase);
    private static readonly ReadOnlyCollection<string> ReadOnlyExtensions = Array.AsReadOnly(Extensions);

    public static IReadOnlyList<string> SupportedExtensions => ReadOnlyExtensions;
    public static IReadOnlyList<string> OpenDialogExtensions => DialogExtensions;

    public static bool IsSupportedExtension(string path)
        => ExtensionSet.Contains(Path.GetExtension(path));

    public static string BuildFilterPattern(bool includeAmbiguousTransportStreamCandidate = true)
    {
        IEnumerable<string> extensions = includeAmbiguousTransportStreamCandidate ? DialogExtensions : Extensions;
        return string.Join(';', extensions.Select(extension => "*" + extension));
    }

    public static bool IsMpegTransportStreamFile(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".ts", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < 376) return false;
            Span<byte> sample = stackalloc byte[377];
            int read = stream.Read(sample);
            return read >= 377
                && sample[0] == 0x47
                && sample[188] == 0x47
                && sample[376] == 0x47
                && (sample[1] & 0x80) == 0
                && (sample[3] & 0x30) != 0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
