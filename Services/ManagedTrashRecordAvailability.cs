using MidFD.Services.TrashManifestStore;

namespace MidFD.Services;

internal enum ManagedTrashRecordAvailability
{
    Available,
    Missing,
    InvalidPath
}

internal sealed record ManagedTrashRecordView(
    TrashManifestRecord Record,
    ManagedTrashRecordAvailability Availability,
    string AvailabilityText)
{
    public bool CanRestore => Availability == ManagedTrashRecordAvailability.Available;
    public bool CanDeletePhysicalItem => Availability == ManagedTrashRecordAvailability.Available;
}

internal static class ManagedTrashRecordAvailabilityService
{
    public static ManagedTrashRecordView Evaluate(
        TrashManifestRecord record,
        ManagedTrashPathValidator pathValidator,
        Func<string, bool>? pathExists = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(pathValidator);
        pathExists ??= static path => File.Exists(path) || Directory.Exists(path);

        if (record.Status != TrashRecordStatus.InTrash)
        {
            return new ManagedTrashRecordView(record, ManagedTrashRecordAvailability.InvalidPath, "パス不正");
        }

        try
        {
            string path = pathValidator.ValidateRecord(record);
            return pathExists(path)
                ? new ManagedTrashRecordView(record, ManagedTrashRecordAvailability.Available, "利用可能")
                : new ManagedTrashRecordView(record, ManagedTrashRecordAvailability.Missing, "実体なし");
        }
        catch
        {
            return new ManagedTrashRecordView(record, ManagedTrashRecordAvailability.InvalidPath, "パス不正");
        }
    }
}
