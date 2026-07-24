using MidFD.Models;

namespace MidFD.Services;

internal static class BrowserPathEntryCandidateService
{
    public static IReadOnlyList<string> BuildCandidates(
        NavigationService navigationService,
        QuickAccessStore? quickAccessStore,
        IEnumerable<string>? directoryMoveHistory = null)
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddCandidate(candidates, seen, navigationService.CurrentPath);

        foreach (string path in directoryMoveHistory ?? Enumerable.Empty<string>())
        {
            AddCandidate(candidates, seen, path);
        }

        foreach (QuickAccessEntry entry in QuickAccessService.GetRegisteredEntries(quickAccessStore ?? new QuickAccessStore()))
        {
            AddCandidate(candidates, seen, entry.Path);
        }

        foreach (QuickAccessEntry entry in QuickAccessService.GetRecentEntries(quickAccessStore ?? new QuickAccessStore()))
        {
            AddCandidate(candidates, seen, entry.Path);
        }

        IReadOnlyList<QuickAccessEntry> historyEntries = QuickAccessService.BuildHistoryEntries(
            navigationService.GetBackHistorySnapshot(),
            navigationService.GetForwardHistorySnapshot());
        foreach (QuickAccessEntry entry in historyEntries)
        {
            AddCandidate(candidates, seen, entry.Path);
        }

        foreach (string path in navigationService.CaptureState().LastVisitedPathByDrive.Values)
        {
            AddCandidate(candidates, seen, path);
        }

        foreach (DriveInfo drive in DriveInfo.GetDrives().Where(static drive => drive.IsReady))
        {
            AddCandidate(candidates, seen, drive.RootDirectory.FullName);
        }

        return candidates;
    }

    private static void AddCandidate(List<string> candidates, HashSet<string> seen, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string normalized = NormalizeCandidate(path);
        if (!seen.Add(normalized))
        {
            return;
        }

        candidates.Add(path);
    }

    private static string NormalizeCandidate(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string trimmed = path.Trim().Trim('"');
        try
        {
            return Path.GetFullPath(trimmed).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
