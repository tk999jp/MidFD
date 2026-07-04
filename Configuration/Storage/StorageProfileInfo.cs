namespace MidFD.Configuration.Storage;

public sealed record StorageProfileInfo(
    StorageProfileKind Kind,
    string ProfileRoot,
    string DataRoot,
    string SettingsDbPath,
    string LogsRoot,
    string TempRoot,
    string ArchivePreviewTempRoot,
    string DragArchiveTempRoot,
    string SettingsJsonPath,
    string BackupDirectory);
