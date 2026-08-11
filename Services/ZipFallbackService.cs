using System.IO.Compression;

namespace MidFD.Services;

public static class ZipFallbackService
{
    public static void Pack(string outputArchivePath, IReadOnlyList<string> sourcePaths)
    {
        string fullOutputPath = Path.GetFullPath(outputArchivePath);
        string? outputDirectory = Path.GetDirectoryName(fullOutputPath);
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
        {
            throw new DirectoryNotFoundException($"出力先フォルダが見つかりません: {outputDirectory}");
        }

        using ZipArchive archive = ZipFile.Open(fullOutputPath, ZipArchiveMode.Update);
        foreach (string sourcePath in sourcePaths)
        {
            if (Directory.Exists(sourcePath))
            {
                AddDirectoryToArchive(archive, sourcePath, Path.GetFileName(sourcePath));
                continue;
            }

            if (File.Exists(sourcePath))
            {
                string entryName = Path.GetFileName(sourcePath);
                AddFileToArchive(archive, sourcePath, entryName);
            }
        }
    }

    public static void Unpack(string archivePath, string extractToDirectory)
    {
        Unpack(archivePath, extractToDirectory, selectedEntries: null);
    }

    public static void Unpack(
        string archivePath,
        string extractToDirectory,
        System.Collections.Generic.IEnumerable<string>? selectedEntries = null,
        System.Threading.CancellationToken token = default,
        System.Action<string>? onOutputLine = null)
    {
        string fullArchivePath = Path.GetFullPath(archivePath);
        string fullExtractPath = Path.GetFullPath(extractToDirectory);

        System.Collections.Generic.HashSet<string>? selectedSet = null;
        if (selectedEntries != null)
        {
            selectedSet = selectedEntries
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(NormalizeEntryName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (selectedSet.Count == 0)
            {
                throw new InvalidOperationException("解凍対象のエントリが指定されていません。");
            }
        }

        using ZipArchive archive = ZipFile.OpenRead(fullArchivePath);
        var extractionPlan = new List<(ZipArchiveEntry Entry, string NormalizedName, string DestinationPath, bool IsDirectory)>();
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();

            string normalizedEntryName = NormalizeEntryName(entry.FullName);
            if (string.IsNullOrEmpty(normalizedEntryName))
            {
                continue;
            }

            if (selectedSet != null)
            {
                if (!selectedSet.Contains(normalizedEntryName))
                {
                    continue;
                }
            }

            bool isDirectory = normalizedEntryName.EndsWith("/");
            string destinationPath = GetSafeDestinationPath(fullExtractPath, normalizedEntryName);
            EnsureNoReparseTraversal(fullExtractPath, destinationPath, normalizedEntryName);
            extractionPlan.Add((entry, normalizedEntryName, destinationPath, isDirectory));
        }

        Directory.CreateDirectory(fullExtractPath);
        foreach ((ZipArchiveEntry entry, string normalizedEntryName, string destinationPath, bool isDirectory) in extractionPlan)
        {
            token.ThrowIfCancellationRequested();
            EnsureNoReparseTraversal(fullExtractPath, destinationPath, normalizedEntryName);

            onOutputLine?.Invoke(entry.FullName);

            if (isDirectory)
            {
                Directory.CreateDirectory(destinationPath);
            }
            else
            {
                string? parentDir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(parentDir))
                {
                    Directory.CreateDirectory(parentDir);
                }

                entry.ExtractToFile(destinationPath, overwrite: true);
            }
        }
    }

    private static string NormalizeEntryName(string entryName)
    {
        return (entryName ?? string.Empty)
            .Replace('\\', '/')
            .TrimStart('/')
            .Trim();
    }

    private static string GetSafeDestinationPath(string destinationRoot, string entryName)
    {
        string normalizedRoot = Path.GetFullPath(destinationRoot);
        string destinationPath = Path.GetFullPath(Path.Combine(normalizedRoot, entryName));

        string rootWithSeparator = normalizedRoot.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!destinationPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"ZIP entry の展開先が解凍先フォルダ外です: {entryName}");
        }

        return destinationPath;
    }

    private static void EnsureNoReparseTraversal(string destinationRoot, string destinationPath, string entryName)
    {
        if (ReparsePointHelper.Exists(destinationRoot) && ReparsePointHelper.IsReparsePoint(destinationRoot))
        {
            throw new InvalidOperationException($"ZIP entry の展開先rootがreparse pointです: {entryName}");
        }

        string relativePath = Path.GetRelativePath(destinationRoot, destinationPath);
        string currentPath = Path.GetFullPath(destinationRoot);
        string[] components = relativePath.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        foreach (string component in components)
        {
            currentPath = Path.Combine(currentPath, component);
            if (ReparsePointHelper.Exists(currentPath) && ReparsePointHelper.IsReparsePoint(currentPath))
            {
                throw new InvalidOperationException($"ZIP entry の展開先にreparse pointを経由するpathがあります: {entryName}");
            }
        }
    }

    private static void AddDirectoryToArchive(ZipArchive archive, string sourceDirectoryPath, string entryRoot)
    {
        bool hasAnyEntry = false;
        foreach (string filePath in Directory.EnumerateFiles(sourceDirectoryPath, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(sourceDirectoryPath, filePath);
            string entryName = Path.Combine(entryRoot, relativePath).Replace('\\', '/');
            AddFileToArchive(archive, filePath, entryName);
            hasAnyEntry = true;
        }

        if (!hasAnyEntry)
        {
            string directoryEntryName = entryRoot.TrimEnd('/', '\\') + "/";
            ReplaceEntry(archive, directoryEntryName);
            archive.CreateEntry(directoryEntryName);
        }
    }

    private static void AddFileToArchive(ZipArchive archive, string filePath, string entryName)
    {
        ReplaceEntry(archive, entryName);
        archive.CreateEntryFromFile(filePath, entryName, CompressionLevel.Optimal);
    }

    private static void ReplaceEntry(ZipArchive archive, string entryName)
    {
        ZipArchiveEntry? existing = archive.GetEntry(entryName);
        existing?.Delete();
    }
}
