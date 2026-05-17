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
        string fullArchivePath = Path.GetFullPath(archivePath);
        string fullExtractPath = Path.GetFullPath(extractToDirectory);
        Directory.CreateDirectory(fullExtractPath);
        ZipFile.ExtractToDirectory(fullArchivePath, fullExtractPath, overwriteFiles: true);
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
