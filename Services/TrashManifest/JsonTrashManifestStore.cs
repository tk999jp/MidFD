using System.Text.Json;

namespace MidFD.Services.TrashManifestStore;

internal sealed class JsonTrashManifestStore : ITrashManifestStore
{
    private readonly string _manifestPath;
    private readonly JsonSerializerOptions _jsonOptions;

    public JsonTrashManifestStore(string manifestPath, JsonSerializerOptions jsonOptions)
    {
        _manifestPath = manifestPath;
        _jsonOptions = jsonOptions;
    }

    public TrashManifest Load()
    {
        try
        {
            if (!File.Exists(_manifestPath))
            {
                return new TrashManifest();
            }

            string json = File.ReadAllText(_manifestPath);
            return JsonSerializer.Deserialize<TrashManifest>(json, _jsonOptions) ?? new TrashManifest();
        }
        catch (Exception ex)
        {
            LogService.Warn($"[MidFdTrash] Failed to load manifest; using empty manifest. error={ex.Message}");
            return new TrashManifest();
        }
    }

    public void Save(TrashManifest manifest)
    {
        string? directory = Path.GetDirectoryName(_manifestPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(manifest, _jsonOptions);
        File.WriteAllText(_manifestPath, json);
    }

    public void RegisterNewRecord(TrashManifest manifest, TrashManifestRecord record)
    {
        manifest.Records.Add(record);
    }

    public void RegisterNewRecords(TrashManifest manifest, IEnumerable<TrashManifestRecord> records)
    {
        manifest.Records.AddRange(records);
    }

    public int UpsertRecord(TrashManifest manifest, TrashManifestRecord record)
    {
        int scanCount = manifest.Records.Count;
        manifest.Records.RemoveAll(existing =>
            string.Equals(existing.TrashPath, record.TrashPath, StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(existing.BatchId, record.BatchId, StringComparison.OrdinalIgnoreCase) &&
             string.Equals(existing.ItemId, record.ItemId, StringComparison.OrdinalIgnoreCase)));
        manifest.Records.Add(record);
        return scanCount;
    }

    public bool UpdateRecordStatus(TrashManifest manifest, string trashPath, TrashRecordStatus status)
    {
        TrashManifestRecord? record = manifest.Records.FirstOrDefault(existing =>
            string.Equals(existing.TrashPath, trashPath, StringComparison.OrdinalIgnoreCase));
        if (record == null)
        {
            return false;
        }

        record.Status = status;
        return true;
    }

    public int UpdateRecordStatuses(TrashManifest manifest, IEnumerable<string> trashPaths, TrashRecordStatus status)
    {
        var paths = new HashSet<string>(trashPaths, StringComparer.OrdinalIgnoreCase);
        int updated = 0;
        foreach (var record in manifest.Records)
        {
            if (paths.Contains(record.TrashPath))
            {
                record.Status = status;
                updated++;
            }
        }
        return updated;
    }

    public int RemoveRecordsByTrashPaths(TrashManifest manifest, IEnumerable<string> trashPaths)
    {
        var paths = new HashSet<string>(trashPaths, StringComparer.OrdinalIgnoreCase);
        if (paths.Count == 0)
        {
            return 0;
        }

        int removed = manifest.Records.RemoveAll(record => paths.Contains(record.TrashPath));
        return removed;
    }

    public bool TryGetRecordByOriginalPath(TrashManifest manifest, string originalPath, out TrashManifestRecord? record)
    {
        record = manifest.Records
            .LastOrDefault(existing => string.Equals(existing.OriginalPath, originalPath, StringComparison.OrdinalIgnoreCase));
        return record != null;
    }

    public (int inserted, int updated, int skipped) UpsertRecords(TrashManifest manifest, IEnumerable<TrashManifestRecord> records)
    {
        int inserted = 0;
        int updated = 0;
        int skipped = 0;

        foreach (var record in records)
        {
            var existing = manifest.Records.FirstOrDefault(r =>
                string.Equals(r.BatchId, record.BatchId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.ItemId, record.ItemId, StringComparison.OrdinalIgnoreCase));

            if (existing == null)
            {
                manifest.Records.Add(record);
                inserted++;
            }
            else
            {
                if (existing.Status != record.Status || existing.TrashPath != record.TrashPath)
                {
                    UpsertRecord(manifest, record);
                    updated++;
                }
                else
                {
                    skipped++;
                }
            }
        }

        return (inserted, updated, skipped);
    }
}
