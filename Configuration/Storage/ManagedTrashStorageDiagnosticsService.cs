namespace MidFD.Configuration.Storage;

public static class ManagedTrashStorageDiagnosticsService
{
    private const string TrashDirectoryName = ".midfd-trash";

    public static ManagedTrashStorageDiagnosticsReport CreateReport(AppStoragePaths activePaths)
    {
        AppStoragePaths portablePaths = StorageProfileProviderFactory.CreatePortable().GetPaths();
        AppStoragePaths installedPaths = StorageProfileProviderFactory.CreateInstalledCandidate().GetPaths();
        string physicalRootCandidate = BuildSameVolumeTrashRootCandidate(activePaths.ProfileRoot);
        string legacyLocalManifestPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MidFD",
            "Trash",
            "manifest.json");

        var riskFlags = new List<string>
        {
            "diagnostics_only",
            "manifest_and_physical_roots_may_differ",
            "trash_restore_requires_existing_manifest_paths"
        };

        if (activePaths.ProfileKind == StorageProfileKind.Installed)
        {
            riskFlags.Add("installed_profile_does_not_migrate_managed_trash");
        }

        return new ManagedTrashStorageDiagnosticsReport(
            ProfileKind: activePaths.ProfileKind,
            ManifestPath: activePaths.TrashManifestDbPath,
            PortableManifestPath: portablePaths.TrashManifestDbPath,
            InstalledManifestCandidatePath: installedPaths.TrashManifestDbPath,
            LegacyLocalJsonManifestPath: legacyLocalManifestPath,
            PhysicalTrashRootCandidate: physicalRootCandidate,
            ManifestExists: File.Exists(activePaths.TrashManifestDbPath),
            LegacyLocalJsonManifestExists: File.Exists(legacyLocalManifestPath),
            PhysicalTrashRootExists: Directory.Exists(physicalRootCandidate),
            RelocationRiskFlags: riskFlags,
            MigrationAllowed: false,
            Reason: "Managed Trash migration is diagnostics-only in this phase; manifest and physical trash roots are not moved.");
    }

    private static string BuildSameVolumeTrashRootCandidate(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            string? root = Path.GetPathRoot(fullPath);
            return string.IsNullOrWhiteSpace(root)
                ? Path.Combine(fullPath, TrashDirectoryName)
                : Path.Combine(root, TrashDirectoryName);
        }
        catch
        {
            return Path.Combine(path, TrashDirectoryName);
        }
    }
}

public sealed record ManagedTrashStorageDiagnosticsReport(
    StorageProfileKind ProfileKind,
    string ManifestPath,
    string PortableManifestPath,
    string InstalledManifestCandidatePath,
    string LegacyLocalJsonManifestPath,
    string PhysicalTrashRootCandidate,
    bool ManifestExists,
    bool LegacyLocalJsonManifestExists,
    bool PhysicalTrashRootExists,
    IReadOnlyList<string> RelocationRiskFlags,
    bool MigrationAllowed,
    string Reason);
