using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace MidFD.Configuration.Storage;

public static class StorageProfileDiagnosticsService
{
    public static string RunToFile(StorageProfileActivation activation, string? explicitPath = null)
    {
        StorageProfileDiagnosticsReport report = CreateReport(activation);
        string reportText = FormatText(report);
        string path = WriteReportFile(reportText, explicitPath);

        if (string.IsNullOrWhiteSpace(explicitPath) && Environment.UserInteractive)
        {
            try
            {
                MessageBox.Show(
                    $"Storage profile diagnostics を保存しました。\n\n{path}",
                    "MidFD Storage Diagnostics",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch
            {
                // diagnostics path の通知は失敗しても本体処理は継続しない。
            }
        }

        return path;
    }

    public static StorageProfileDiagnosticsReport CreateReport(StorageProfileActivation activation)
    {
        AppStoragePaths portablePaths = StorageProfileProviderFactory.CreatePortable().GetPaths();
        AppStoragePaths activePaths = StorageProfileProviderFactory.CreateForActivation(activation).GetPaths();
        string markerPath = GetInstalledMigrationMarkerPath(activePaths);
        string? migrationResult = TryReadMigrationResult(markerPath);

        return new StorageProfileDiagnosticsReport(
            SelectedProfile: activation.Kind,
            ActivationSource: activation.Source,
            UsedFallback: activation.UsedFallback,
            RawValue: activation.RawValue,
            DiagnosticMessage: activation.DiagnosticMessage,
            ProfileRoot: activePaths.ProfileRoot,
            SettingsDbPath: activePaths.SettingsDbPath,
            SettingsJsonPath: activePaths.SettingsJsonPath,
            InstalledMigrationMarkerPath: markerPath,
            InstalledMigrationResult: migrationResult,
            IsPortableDefault: activation.Kind == StorageProfileKind.Portable && activation.Source == StorageProfileActivationSource.Default,
            IsInstalledOptIn: activation.Kind == StorageProfileKind.Installed,
            PortableSettingsDbPath: portablePaths.SettingsDbPath,
            ManagedTrashDiagnostics: ManagedTrashStorageDiagnosticsService.CreateReport(activePaths));
    }

    public static string FormatText(StorageProfileDiagnosticsReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Storage Profile Diagnostics");
        builder.AppendLine($"SelectedProfile: {report.SelectedProfile}");
        builder.AppendLine($"ActivationSource: {report.ActivationSource}");
        builder.AppendLine($"UsedFallback: {report.UsedFallback}");
        builder.AppendLine($"RawValue: {report.RawValue ?? "(none)"}");
        builder.AppendLine($"ProfileRoot: {report.ProfileRoot}");
        builder.AppendLine($"SettingsDbPath: {report.SettingsDbPath}");
        builder.AppendLine($"SettingsJsonPath: {report.SettingsJsonPath}");
        builder.AppendLine($"InstalledMigrationMarkerPath: {report.InstalledMigrationMarkerPath}");
        builder.AppendLine($"InstalledMigrationResult: {report.InstalledMigrationResult ?? "(none)"}");
        builder.AppendLine($"IsPortableDefault: {report.IsPortableDefault}");
        builder.AppendLine($"IsInstalledOptIn: {report.IsInstalledOptIn}");
        builder.AppendLine($"PortableSettingsDbPath: {report.PortableSettingsDbPath}");
        builder.AppendLine($"TrashManifestPath: {report.ManagedTrashDiagnostics.ManifestPath}");
        builder.AppendLine($"TrashManifestExists: {report.ManagedTrashDiagnostics.ManifestExists}");
        builder.AppendLine($"TrashPhysicalRootCandidate: {report.ManagedTrashDiagnostics.PhysicalTrashRootCandidate}");
        builder.AppendLine($"TrashPhysicalRootExists: {report.ManagedTrashDiagnostics.PhysicalTrashRootExists}");
        builder.AppendLine($"TrashMigrationAllowed: {report.ManagedTrashDiagnostics.MigrationAllowed}");
        builder.AppendLine($"TrashMigrationReason: {report.ManagedTrashDiagnostics.Reason}");
        foreach (string flag in report.ManagedTrashDiagnostics.RelocationRiskFlags)
        {
            builder.AppendLine($"TrashRelocationRisk: {flag}");
        }

        return builder.ToString();
    }

    private static string WriteReportFile(string reportText, string? explicitPath)
    {
        var candidatePaths = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            candidatePaths.Add(explicitPath);
        }
        else
        {
            candidatePaths.Add(Path.Combine(AppContext.BaseDirectory, "diagnostics", "storage-profile-diagnostics.txt"));
        }

        candidatePaths.Add(Path.Combine(Path.GetTempPath(), "MidFD", "storage-profile-diagnostics.txt"));

        Exception? lastError = null;
        foreach (string candidate in candidatePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                string fullPath = Path.GetFullPath(candidate);
                string? directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(fullPath, reportText, Encoding.UTF8);
                return fullPath;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw new IOException("Failed to write storage profile diagnostics report.", lastError);
    }

    private static string GetInstalledMigrationMarkerPath(AppStoragePaths paths)
    {
        string settingsDirectory = Path.GetDirectoryName(paths.SettingsDbPath) ?? paths.ProfileRoot;
        return Path.Combine(settingsDirectory, InstalledSettingsMigrationService.MarkerFileName);
    }

    private static string? TryReadMigrationResult(string markerPath)
    {
        try
        {
            if (!File.Exists(markerPath))
            {
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(markerPath));
            return document.RootElement.TryGetProperty("Result", out JsonElement resultElement)
                ? resultElement.GetString()
                : null;
        }
        catch
        {
            return "unreadable";
        }
    }
}

public sealed record StorageProfileDiagnosticsReport(
    StorageProfileKind SelectedProfile,
    StorageProfileActivationSource ActivationSource,
    bool UsedFallback,
    string? RawValue,
    string DiagnosticMessage,
    string ProfileRoot,
    string SettingsDbPath,
    string SettingsJsonPath,
    string InstalledMigrationMarkerPath,
    string? InstalledMigrationResult,
    bool IsPortableDefault,
    bool IsInstalledOptIn,
    string PortableSettingsDbPath,
    ManagedTrashStorageDiagnosticsReport ManagedTrashDiagnostics);
