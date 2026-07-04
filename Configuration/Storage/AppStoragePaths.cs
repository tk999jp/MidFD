namespace MidFD.Configuration.Storage;

public sealed record AppStoragePaths(
    StorageProfileKind ProfileKind,
    string AppBaseDirectory,
    string ProfileRoot,
    string DocumentationRootDirectory,
    string UserDocsDirectory,
    string SettingsJsonPath,
    string SettingsDbPath,
    string QuickAccessJsonPath,
    string MarkSlotsJsonPath,
    string ExternalToolsJsonPath,
    string CommandPaletteUsageJsonPath,
    string WorkspaceDbPath,
    string TrashManifestDbPath,
    string LogDirectory,
    string StartupLogFallbackDirectory,
    string VideoStillPreviewCacheDirectory,
    string TempRoot,
    string BackupDirectory);
