using System.Text.Json;
using System.Diagnostics;
using MidFD.Models;

namespace MidFD.Services.TrashManifestStore;

public sealed class TrashManifestMigrationOptions
{
    public string JsonManifestPath { get; set; } = string.Empty;
    public string SqliteDbPath { get; set; } = string.Empty;
    public bool DryRun { get; set; }
}

public sealed class TrashManifestMigrationResult
{
    public int JsonRecordCount { get; set; }
    public int InsertedCount { get; set; }
    public int UpdatedCount { get; set; }
    public int SkippedCount { get; set; }
    public int ErrorCount { get; set; }
    public int SqliteRecordCountAfter { get; set; }
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    
    // Performance metrics
    public long JsonLoadMs { get; set; }
    public long ExistingSqliteLoadMs { get; set; }
    public long WriteMs { get; set; }
    public long ValidationMs { get; set; }
    public long TotalElapsedMs { get; set; }
}

internal static class TrashManifestMigrationService
{
    public static TrashManifestMigrationResult Migrate(TrashManifestMigrationOptions options)
    {
        var result = new TrashManifestMigrationResult();
        var totalSw = Stopwatch.StartNew();
        try
        {
            if (string.IsNullOrWhiteSpace(options.JsonManifestPath))
            {
                throw new ArgumentException("JSON manifest path is required.");
            }
            if (string.IsNullOrWhiteSpace(options.SqliteDbPath))
            {
                throw new ArgumentException("SQLite DB path is required.");
            }

            // 1. Load JSON manifest
            var jsonLoadSw = Stopwatch.StartNew();
            var jsonStore = TrashManifestStoreFactory.CreateJsonStore(options.JsonManifestPath, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            var jsonManifest = jsonStore.Load();
            jsonLoadSw.Stop();
            result.JsonLoadMs = jsonLoadSw.ElapsedMilliseconds;
            result.JsonRecordCount = jsonManifest.Records.Count;

            if (options.DryRun)
            {
                // Dry run validation
                int duplicates = 0;
                var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in jsonManifest.Records)
                {
                    string key = $"{r.BatchId}:{r.ItemId}";
                    if (!keys.Add(key)) duplicates++;
                }

                result.Success = true;
                result.Message = $"Dry run: Found {result.JsonRecordCount} JSON records. Potential duplicates: {duplicates}.";
                LogService.Info($"[TrashManifestMigration] {result.Message}");
                totalSw.Stop();
                result.TotalElapsedMs = totalSw.ElapsedMilliseconds;
                return result;
            }

            // 2. Initialize SQLite store and load existing
            var sqliteLoadSw = Stopwatch.StartNew();
            var sqliteStore = TrashManifestStoreFactory.CreateSqliteStore(options.SqliteDbPath);
            var sqliteManifest = sqliteStore.Load(); 
            sqliteLoadSw.Stop();
            result.ExistingSqliteLoadMs = sqliteLoadSw.ElapsedMilliseconds;

            // 3. Migrate records in batch
            var writeSw = Stopwatch.StartNew();
            var counts = sqliteStore.UpsertRecords(sqliteManifest, jsonManifest.Records);
            writeSw.Stop();
            result.WriteMs = writeSw.ElapsedMilliseconds;
            
            result.InsertedCount = counts.inserted;
            result.UpdatedCount = counts.updated;
            result.SkippedCount = counts.skipped;

            // 4. Final Validation
            var valSw = Stopwatch.StartNew();
            var finalSqliteManifest = sqliteStore.Load();
            valSw.Stop();
            result.ValidationMs = valSw.ElapsedMilliseconds;
            
            result.SqliteRecordCountAfter = finalSqliteManifest.Records.Count;
            result.Success = result.ErrorCount == 0;
            
            result.Message = $"Completed json={result.JsonRecordCount} sqlite={result.SqliteRecordCountAfter} inserted={result.InsertedCount} updated={result.UpdatedCount} skipped={result.SkippedCount} errors={result.ErrorCount} " +
                             $"jsonLoadMs={result.JsonLoadMs} existingLoadMs={result.ExistingSqliteLoadMs} writeMs={result.WriteMs} validationMs={result.ValidationMs}";
            
            LogService.Info($"[TrashManifestMigration] {result.Message}");
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = $"Migration failed: {ex.Message}";
            LogService.Error("[TrashManifestMigration] Migration aborted.", ex);
        }
        finally
        {
            totalSw.Stop();
            result.TotalElapsedMs = totalSw.ElapsedMilliseconds;
            if (result.Success)
            {
                LogService.Info($"[TrashManifestMigration] TotalElapsedMs={result.TotalElapsedMs}");
            }
        }
        return result;
    }
}
