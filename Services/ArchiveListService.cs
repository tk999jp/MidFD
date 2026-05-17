using System.Globalization;
using MidFD.Models;

namespace MidFD.Services;

public static class ArchiveListService
{
    public static ArchiveListResult GetArchiveContents(string? configuredSevenZipPath, string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath) || !File.Exists(archivePath))
        {
            return new ArchiveListResult
            {
                Success = false,
                ErrorMessage = "archive が見つかりません。"
            };
        }

        string? sevenZipPath = SevenZipService.ResolveExecutable(configuredSevenZipPath);
        if (string.IsNullOrWhiteSpace(sevenZipPath))
        {
            LogService.Warn($"ArchiveListService: 7-Zip not found. Checking Tar fallback. Archive={archivePath}");
            if (TarFallbackService.IsAvailable())
            {
                try
                {
                    // tar -tf による簡易一覧取得
                    var res = TarFallbackService.List(archivePath);
                    if (res.ExitCode == 0)
                    {
                        return new ArchiveListResult
                        {
                            Success = true,
                            Entries = ParseTarEntries(res.Output)
                        };
                    }
                }
                catch (Exception ex)
                {
                    LogService.Error($"ArchiveListService: tar fallback list failed. Archive={archivePath}", ex);
                }
            }

            return new ArchiveListResult
            {
                Success = false,
                ErrorMessage = SevenZipService.BuildUnavailableMessage(configuredSevenZipPath, "archive 内容一覧を取得"),
                SevenZipPath = configuredSevenZipPath
            };
        }

        try
        {
            var commandResult = SevenZipService.List(sevenZipPath, archivePath);
            if (commandResult.ExitCode != 0)
            {
                LogService.Error($"7z List Failure (ExitCode: {commandResult.ExitCode}) Archive={archivePath}\nError: {commandResult.Error}");
                return new ArchiveListResult
                {
                    Success = false,
                    ErrorMessage = "archive 内容一覧の取得に失敗しました。",
                    SevenZipPath = sevenZipPath
                };
            }

            IReadOnlyList<ArchiveEntry> entries = ParseEntries(commandResult.Output);
            if (entries.Count == 0)
            {
                LogService.Info($"ArchiveListService: archive has 0 entries. Archive={archivePath}");
            }

            return new ArchiveListResult
            {
                Success = true,
                SevenZipPath = sevenZipPath,
                Entries = entries
            };
        }
        catch (Exception ex)
        {
            LogService.Error($"ArchiveListService: unexpected failure. Archive={archivePath}", ex);
            return new ArchiveListResult
            {
                Success = false,
                ErrorMessage = $"archive 内容一覧の取得に失敗しました: {ex.Message}",
                SevenZipPath = sevenZipPath
            };
        }
    }

    private static IReadOnlyList<ArchiveEntry> ParseTarEntries(string output)
    {
        var entries = new List<ArchiveEntry>();
        using var reader = new StringReader(output ?? string.Empty);
        while (true)
        {
            string? line = reader.ReadLine();
            if (line == null) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            string entryPath = line.Trim();
            bool isDirectory = entryPath.EndsWith("/") || entryPath.EndsWith("\\");

            entries.Add(new ArchiveEntry
            {
                EntryPath = entryPath,
                Name = Path.GetFileName(entryPath.TrimEnd('\\', '/')),
                IsDirectory = isDirectory,
                Size = null,
                ModifiedAt = null
            });
        }
        return entries;
    }

    private static IReadOnlyList<ArchiveEntry> ParseEntries(string output)
    {
        var entries = new List<ArchiveEntry>();
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StringReader(output ?? string.Empty);

        while (true)
        {
            string? line = reader.ReadLine();
            if (line == null)
            {
                AddEntryIfPresent(entries, fields);
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                AddEntryIfPresent(entries, fields);
                fields.Clear();
                continue;
            }

            int separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            string key = line[..separatorIndex].Trim();
            string value = line[(separatorIndex + 1)..].Trim();
            fields[key] = value;
        }

        return entries;
    }

    private static void AddEntryIfPresent(List<ArchiveEntry> entries, IReadOnlyDictionary<string, string> fields)
    {
        if (!fields.TryGetValue("Path", out string? entryPath) || string.IsNullOrWhiteSpace(entryPath))
        {
            return;
        }

        bool isDirectory = fields.TryGetValue("Folder", out string? folderValue)
            && string.Equals(folderValue, "+", StringComparison.Ordinal);
        if (!isDirectory
            && fields.TryGetValue("Attributes", out string? attributesValue)
            && attributesValue.IndexOf('D', StringComparison.OrdinalIgnoreCase) >= 0)
        {
            isDirectory = true;
        }

        long? size = null;
        if (fields.TryGetValue("Size", out string? sizeValue)
            && long.TryParse(sizeValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedSize))
        {
            size = parsedSize;
        }

        DateTime? modifiedAt = null;
        if (fields.TryGetValue("Modified", out string? modifiedValue)
            && DateTime.TryParse(modifiedValue, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsedModified))
        {
            modifiedAt = parsedModified;
        }

        entries.Add(new ArchiveEntry
        {
            EntryPath = entryPath,
            Name = Path.GetFileName(entryPath.TrimEnd('\\', '/')),
            IsDirectory = isDirectory,
            Size = size,
            ModifiedAt = modifiedAt
        });
    }
}
