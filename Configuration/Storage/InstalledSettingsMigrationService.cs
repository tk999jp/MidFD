using System.Text.Json;
using Microsoft.Data.Sqlite;
using MidFD.Services;

namespace MidFD.Configuration.Storage;

public static class InstalledSettingsMigrationService
{
    public const string MarkerFileName = "installed-profile-migration.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static InstalledSettingsMigrationResult EnsureInitialSettingsMigration(
        AppStoragePaths portablePaths,
        AppStoragePaths installedPaths)
    {
        string sourceDbPath = Path.GetFullPath(portablePaths.SettingsDbPath);
        string targetDbPath = Path.GetFullPath(installedPaths.SettingsDbPath);
        string markerPath = Path.Combine(Path.GetDirectoryName(targetDbPath) ?? installedPaths.ProfileRoot, MarkerFileName);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetDbPath) ?? installedPaths.ProfileRoot);
            Directory.CreateDirectory(installedPaths.BackupDirectory);

            if (File.Exists(targetDbPath))
            {
                if (File.Exists(markerPath))
                {
                    return new InstalledSettingsMigrationResult(sourceDbPath, targetDbPath, markerPath, "already_exists", "Installed settings migration marker already exists.");
                }

                return WriteMarker(markerPath, sourceDbPath, targetDbPath, "skipped", "Installed settings DB already exists.");
            }

            if (!File.Exists(sourceDbPath))
            {
                return WriteMarker(markerPath, sourceDbPath, targetDbPath, "skipped", "Portable settings DB does not exist.");
            }

            using var source = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = sourceDbPath,
                Mode = SqliteOpenMode.ReadOnly
            }.ToString());
            using var target = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = targetDbPath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString());

            source.Open();
            target.Open();
            source.BackupDatabase(target);

            return WriteMarker(markerPath, sourceDbPath, targetDbPath, "copied", "Portable settings DB copied to Installed profile.");
        }
        catch (Exception ex)
        {
            LogService.Warn($"Installed profile settings migration failed. Source={sourceDbPath} Target={targetDbPath} Error={ex.Message}");
            try
            {
                return WriteMarker(markerPath, sourceDbPath, targetDbPath, "failed", ex.Message);
            }
            catch
            {
                return new InstalledSettingsMigrationResult(sourceDbPath, targetDbPath, markerPath, "failed", ex.Message);
            }
        }
    }

    private static InstalledSettingsMigrationResult WriteMarker(
        string markerPath,
        string sourceDbPath,
        string targetDbPath,
        string result,
        string message)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath) ?? string.Empty);

        var marker = new InstalledSettingsMigrationMarker(
            SourceProfile: "portable",
            TargetProfile: "installed",
            SourceDbPath: sourceDbPath,
            TargetDbPath: targetDbPath,
            TimestampUtc: DateTime.UtcNow,
            Result: result,
            Message: message,
            Rollback: "Portable source DB is not moved or deleted; remove Installed profile DB to return to a fresh Installed profile.");

        File.WriteAllText(markerPath, JsonSerializer.Serialize(marker, JsonOptions));
        return new InstalledSettingsMigrationResult(sourceDbPath, targetDbPath, markerPath, result, message);
    }

    public sealed record InstalledSettingsMigrationResult(
        string SourceDbPath,
        string TargetDbPath,
        string MarkerPath,
        string Result,
        string Message);

    public sealed record InstalledSettingsMigrationMarker(
        string SourceProfile,
        string TargetProfile,
        string SourceDbPath,
        string TargetDbPath,
        DateTime TimestampUtc,
        string Result,
        string Message,
        string Rollback);
}
