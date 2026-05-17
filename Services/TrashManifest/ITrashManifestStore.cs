namespace MidFD.Services.TrashManifestStore;

internal interface ITrashManifestStore
{
    TrashManifest Load();
    void Save(TrashManifest manifest);
    void RegisterNewRecord(TrashManifest manifest, TrashManifestRecord record);
    void RegisterNewRecords(TrashManifest manifest, IEnumerable<TrashManifestRecord> records);
    int UpsertRecord(TrashManifest manifest, TrashManifestRecord record);
    bool UpdateRecordStatus(TrashManifest manifest, string trashPath, TrashRecordStatus status);
    int UpdateRecordStatuses(TrashManifest manifest, IEnumerable<string> trashPaths, TrashRecordStatus status);
    bool TryGetRecordByOriginalPath(TrashManifest manifest, string originalPath, out TrashManifestRecord? record);
    (int inserted, int updated, int skipped) UpsertRecords(TrashManifest manifest, IEnumerable<TrashManifestRecord> records);
}
