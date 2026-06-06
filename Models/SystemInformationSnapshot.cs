namespace MidFD.Models;

public sealed record SystemInformationSnapshot(
    IReadOnlyList<DriveInformationSnapshot> Drives,
    MemoryInformationSnapshot Memory,
    SystemDetailsSnapshot System,
    HardwareSummarySnapshot Hardware);

public sealed record DriveInformationSnapshot(
    string RootPath,
    string DisplayName,
    string VolumeLabel,
    string DriveType,
    string MediaKind,
    string SerialNumber,
    string FileSystem,
    long? TotalBytes,
    long? UsedBytes,
    long? FreeBytes,
    uint? BytesPerSector,
    uint? BytesPerCluster,
    StorageHealthSnapshot StorageHealth);

public sealed record MemoryInformationSnapshot(
    ulong? TotalPhysicalBytes,
    ulong? AvailablePhysicalBytes,
    uint? MemoryLoadPercent);

public sealed record SystemDetailsSnapshot(
    string ComputerName,
    string UserName,
    string CpuName,
    string WindowsVersion,
    string Uptime,
    string OsBitness,
    string DotNetVersion);

public sealed record HardwareSummarySnapshot(
    CpuSummarySnapshot Cpu,
    GpuSummarySnapshot Gpu);

public sealed record CpuSummarySnapshot(
    string Name,
    string PhysicalCoreCount,
    string LogicalProcessorCount,
    string ClockSummary);

public sealed record GpuSummarySnapshot(
    string Name,
    string Memory,
    string DriverVersion,
    string RetrievalStatus);

public sealed record StorageHealthSnapshot(
    string PhysicalDiskName,
    string MediaType,
    string HealthStatus,
    string Temperature,
    string RetrievalStatus);
