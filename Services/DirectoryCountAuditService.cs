using System.IO;

namespace MidFD.Services;

public static class DirectoryCountAuditService
{
    public static int CountVisibleEntries(string directoryPath, bool showHiddenFiles, CancellationToken cancellationToken = default)
        => CountVisibleEntriesDetailed(directoryPath, showHiddenFiles, cancellationToken).VisibleEntryCount;

    public static DirectoryCountAuditResult CountVisibleEntriesDetailed(
        string directoryPath,
        bool showHiddenFiles,
        CancellationToken cancellationToken = default)
        => CountVisibleEntriesDetailed(
            Directory.EnumerateFileSystemEntries(directoryPath),
            showHiddenFiles,
            File.GetAttributes,
            cancellationToken);

    internal static DirectoryCountAuditResult CountVisibleEntriesDetailed(
        IEnumerable<string> entries,
        bool showHiddenFiles,
        Func<string, FileAttributes> getAttributes,
        CancellationToken cancellationToken = default)
    {
        int count = 0;
        int enumerated = 0;
        int attributeReads = 0;
        foreach (string entryPath in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            enumerated++;
            if (!showHiddenFiles)
            {
                try
                {
                    attributeReads++;
                    if (getAttributes(entryPath).HasFlag(FileAttributes.Hidden))
                    {
                        continue;
                    }
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }
            }
            count++;
        }
        return new DirectoryCountAuditResult(count, enumerated, attributeReads);
    }

    public static bool IsNetworkPath(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            if (fullPath.StartsWith(@"\\", StringComparison.Ordinal)) return true;
            string? root = Path.GetPathRoot(fullPath);
            return !string.IsNullOrWhiteSpace(root) && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch { return false; }
    }
}

public sealed record DirectoryCountAuditResult(int VisibleEntryCount, int EnumeratedEntryCount, int AttributeReadCount);
