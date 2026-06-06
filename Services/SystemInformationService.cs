using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using MidFD.Models;

namespace MidFD.Services;

public sealed class SystemInformationService
{
    public SystemInformationSnapshot CreateSnapshot()
    {
        IReadOnlyList<DriveInformationSnapshot> drives = DriveInfo
            .GetDrives()
            .OrderBy(static d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(BuildDriveSnapshot)
            .ToArray();

        MemoryInformationSnapshot memory = BuildMemorySnapshot();
        SystemDetailsSnapshot system = BuildSystemDetailsSnapshot();

        return new SystemInformationSnapshot(
            drives,
            memory,
            system,
            BuildHardwareSummary(memory, system.CpuName));
    }

    public string? ResolveInitialDriveRoot(string? currentPath, IReadOnlyList<DriveInformationSnapshot> drives)
    {
        if (drives.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            try
            {
                string? root = Path.GetPathRoot(currentPath);
                if (!string.IsNullOrWhiteSpace(root))
                {
                    DriveInformationSnapshot? matched = drives.FirstOrDefault(d =>
                        string.Equals(d.RootPath, root, StringComparison.OrdinalIgnoreCase));
                    if (matched != null)
                    {
                        return matched.RootPath;
                    }
                }
            }
            catch
            {
                // 現在パス解決失敗時は先頭ドライブへフォールバックする。
            }
        }

        return drives[0].RootPath;
    }

    private static DriveInformationSnapshot BuildDriveSnapshot(DriveInfo drive)
    {
        string rootPath = SafeGet(() => drive.Name, "-");
        bool isReady = SafeGet(() => drive.IsReady, false);

        string volumeLabel = isReady ? SafeGet(() => drive.VolumeLabel, "-") : "未準備";
        string fileSystem = isReady ? SafeGet(() => drive.DriveFormat, "-") : "取得不可";
        string driveType = SafeGet(() => ToDriveTypeDisplay(drive.DriveType), "不明");
        string mediaKind = DetectMediaKind(rootPath, driveType);

        long? totalBytes = isReady ? SafeGetNullable(() => drive.TotalSize) : null;
        long? freeBytes = isReady ? SafeGetNullable(() => drive.AvailableFreeSpace) : null;
        long? usedBytes = totalBytes.HasValue && freeBytes.HasValue ? Math.Max(0L, totalBytes.Value - freeBytes.Value) : null;

        string serialNumber = TryGetVolumeInformation(rootPath, out VolumeInformation volumeInfo)
            ? volumeInfo.SerialNumber
            : "取得不可";

        fileSystem = string.Equals(fileSystem, "取得不可", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(volumeInfo.FileSystem)
            ? volumeInfo.FileSystem
            : fileSystem;
        if (string.Equals(volumeLabel, "-", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(volumeInfo.VolumeLabel))
        {
            volumeLabel = volumeInfo.VolumeLabel;
        }

        uint? bytesPerSector = null;
        uint? bytesPerCluster = null;
        if (TryGetDiskGeometry(rootPath, out DiskGeometrySnapshot geometry))
        {
            bytesPerSector = geometry.BytesPerSector;
            bytesPerCluster = checked(geometry.BytesPerSector * geometry.SectorsPerCluster);
        }

        return new DriveInformationSnapshot(
            rootPath,
            BuildDriveDisplayName(rootPath, volumeLabel),
            string.IsNullOrWhiteSpace(volumeLabel) ? "-" : volumeLabel,
            driveType,
            mediaKind,
            serialNumber,
            fileSystem,
            totalBytes,
            usedBytes,
            freeBytes,
            bytesPerSector,
            bytesPerCluster,
            BuildStorageHealthSnapshot(driveType, mediaKind));
    }

    private static StorageHealthSnapshot BuildStorageHealthSnapshot(string driveType, string mediaKind)
    {
        string mediaType = mediaKind is "SSD" or "HDD"
            ? mediaKind
            : driveType switch
        {
            "固定" => "固定ストレージ",
            "リムーバブル" => "リムーバブル",
            "ネットワーク" => "ネットワークストレージ",
            "光学" => "光学メディア",
            _ => driveType
        };

        string retrievalStatus = driveType switch
        {
            "固定" => "簡易表示のみ（詳細S.M.A.R.T.は未対応）",
            "ネットワーク" => "ネットワークドライブの健康情報は対象外",
            "光学" => "光学メディアの健康情報は対象外",
            _ => "この環境では詳細健康情報を取得できません"
        };

        string healthStatus = driveType switch
        {
            "固定" => "詳細未対応",
            "ネットワーク" => "対象外",
            "光学" => "対象外",
            _ => "-"
        };

        return new StorageHealthSnapshot(
            "-",
            mediaType,
            healthStatus,
            "-",
            retrievalStatus);
    }

    private static string DetectMediaKind(string rootPath, string driveType)
    {
        if (!string.Equals(driveType, "固定", StringComparison.Ordinal) &&
            !string.Equals(driveType, "リムーバブル", StringComparison.Ordinal))
        {
            return "対象外";
        }

        if (string.IsNullOrWhiteSpace(rootPath) || rootPath.Length < 1 || !char.IsLetter(rootPath[0]))
        {
            return "取得不可";
        }

        string volumePath = $@"\\.\{char.ToUpperInvariant(rootPath[0])}:";
        IntPtr handle = CreateFile(
            volumePath,
            0,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);
        if (handle == InvalidHandleValue)
        {
            return "取得不可";
        }

        try
        {
            var query = new StoragePropertyQuery
            {
                PropertyId = StorageDeviceSeekPenaltyProperty,
                QueryType = PropertyStandardQuery,
                AdditionalParameters = 0
            };
            var descriptor = new DeviceSeekPenaltyDescriptor();

            bool ok = DeviceIoControl(
                handle,
                IoctlStorageQueryProperty,
                ref query,
                Marshal.SizeOf<StoragePropertyQuery>(),
                ref descriptor,
                Marshal.SizeOf<DeviceSeekPenaltyDescriptor>(),
                out _,
                IntPtr.Zero);
            if (!ok || descriptor.Version == 0)
            {
                return "取得不可";
            }

            return descriptor.IncursSeekPenalty ? "HDD" : "SSD";
        }
        catch
        {
            return "取得不可";
        }
        finally
        {
            _ = CloseHandle(handle);
        }
    }

    private static MemoryInformationSnapshot BuildMemorySnapshot()
    {
        var status = new MemoryStatusEx();
        status.Length = (uint)Marshal.SizeOf<MemoryStatusEx>();

        if (!GlobalMemoryStatusEx(ref status))
        {
            return new MemoryInformationSnapshot(null, null, null);
        }

        return new MemoryInformationSnapshot(
            status.TotalPhys,
            status.AvailPhys,
            status.MemoryLoad);
    }

    private static SystemDetailsSnapshot BuildSystemDetailsSnapshot()
    {
        string rawArch = RuntimeInformation.OSArchitecture.ToString();
        string osBitness = rawArch.ToUpperInvariant() switch
        {
            "X64" => "64bit",
            "X86" => "32bit",
            "ARM64" => "ARM64",
            _ => rawArch
        };

        return new SystemDetailsSnapshot(
            Environment.MachineName,
            Environment.UserName,
            GetCpuName(),
            GetWindowsVersion(),
            FormatUptime(Environment.TickCount64),
            osBitness,
            Environment.Version.ToString());
    }

    private static HardwareSummarySnapshot BuildHardwareSummary(MemoryInformationSnapshot memory, string cpuName)
    {
        return new HardwareSummarySnapshot(
            BuildCpuSummary(cpuName),
            BuildGpuSummary());
    }

    private static CpuSummarySnapshot BuildCpuSummary(string cpuName)
    {
        string logicalProcessorCount = $"{Environment.ProcessorCount:#,0}";
        string physicalCoreCount = GetPhysicalCoreCountText();
        string clockSummary = GetCpuClockSummary();

        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key?.GetValue("~MHz") is int mhz && mhz > 0)
            {
                clockSummary = $"{mhz:#,0} MHz";
            }
        }
        catch
        {
        }

        return new CpuSummarySnapshot(
            cpuName,
            physicalCoreCount,
            logicalProcessorCount,
            clockSummary);
    }

    private static GpuSummarySnapshot BuildGpuSummary()
    {
        try
        {
            foreach (DisplayDeviceSnapshot device in EnumerateDisplayDevices())
            {
                if (string.IsNullOrWhiteSpace(device.DeviceName))
                {
                    continue;
                }

                string driverVersion = "取得不可";
                string memory = "取得不可";

                if (TryGetGpuRegistryInfo(device.DeviceKey, out GpuRegistryInfo gpuInfo))
                {
                    if (!string.IsNullOrWhiteSpace(gpuInfo.DriverVersion))
                    {
                        driverVersion = gpuInfo.DriverVersion;
                    }

                    if (gpuInfo.MemoryBytes.HasValue)
                    {
                        memory = $"{gpuInfo.MemoryBytes.Value / (1024d * 1024d):N0} MB";
                    }
                }

                return new GpuSummarySnapshot(
                    device.DeviceName,
                    memory,
                    driverVersion,
                    string.IsNullOrWhiteSpace(device.DeviceKey) ? "表示名のみ取得" : "簡易表示のみ");
            }
        }
        catch
        {
        }

        return new GpuSummarySnapshot(
            "取得不可",
            "取得不可",
            "取得不可",
            "この環境ではGPU詳細を取得できません");
    }

    private static IEnumerable<DisplayDeviceSnapshot> EnumerateDisplayDevices()
    {
        uint index = 0;
        while (true)
        {
            var displayDevice = new DisplayDevice();
            displayDevice.cb = Marshal.SizeOf<DisplayDevice>();
            if (!EnumDisplayDevices(null, index, ref displayDevice, 0))
            {
                yield break;
            }

            index++;
            if (string.IsNullOrWhiteSpace(displayDevice.DeviceString))
            {
                continue;
            }

            const int AttachedToDesktop = 0x00000001;
            if ((displayDevice.StateFlags & AttachedToDesktop) == 0)
            {
                continue;
            }

            string deviceName = displayDevice.DeviceString ?? string.Empty;
            string deviceKey = displayDevice.DeviceKey ?? string.Empty;
            yield return new DisplayDeviceSnapshot(
                deviceName.Trim(),
                deviceKey.Trim());
        }
    }

    private static bool TryGetGpuRegistryInfo(string rawDeviceKey, out GpuRegistryInfo info)
    {
        info = default;
        string? subKeyPath = ConvertDisplayDeviceKeyToRegistryPath(rawDeviceKey);
        if (string.IsNullOrWhiteSpace(subKeyPath))
        {
            return false;
        }

        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(subKeyPath);
            if (key == null)
            {
                return false;
            }

            string? driverVersion = key.GetValue("DriverVersion") as string
                ?? key.GetValue("UserModeDriverVersion") as string
                ?? key.GetValue("DriverDesc") as string;

            ulong? memoryBytes = null;
            object? memoryValue = key.GetValue("HardwareInformation.qwMemorySize");
            if (memoryValue is byte[] memoryBytesRaw && memoryBytesRaw.Length >= sizeof(ulong))
            {
                memoryBytes = BitConverter.ToUInt64(memoryBytesRaw, 0);
            }

            info = new GpuRegistryInfo(driverVersion?.Trim() ?? string.Empty, memoryBytes);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? ConvertDisplayDeviceKeyToRegistryPath(string rawDeviceKey)
    {
        if (string.IsNullOrWhiteSpace(rawDeviceKey))
        {
            return null;
        }

        const string machinePrefix = @"\Registry\Machine\";
        if (rawDeviceKey.StartsWith(machinePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return rawDeviceKey[machinePrefix.Length..];
        }

        return null;
    }

    private static string GetCpuClockSummary()
    {
        return "-";
    }

    private static string BuildDriveDisplayName(string rootPath, string volumeLabel)
    {
        if (string.IsNullOrWhiteSpace(volumeLabel) || string.Equals(volumeLabel, "-", StringComparison.Ordinal) || string.Equals(volumeLabel, "未準備", StringComparison.Ordinal))
        {
            return rootPath;
        }

        return $"{rootPath} ({volumeLabel})";
    }

    private static string ToDriveTypeDisplay(DriveType driveType)
    {
        return driveType switch
        {
            DriveType.Fixed => "固定",
            DriveType.Network => "ネットワーク",
            DriveType.CDRom => "光学",
            DriveType.Ram => "RAM",
            DriveType.Removable => "リムーバブル",
            DriveType.NoRootDirectory => "ルートなし",
            _ => "不明"
        };
    }

    private static string GetCpuName()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            string? cpuName = key?.GetValue("ProcessorNameString") as string;
            if (!string.IsNullOrWhiteSpace(cpuName))
            {
                return cpuName.Trim();
            }
        }
        catch
        {
        }

        return $"論理プロセッサ数: {Environment.ProcessorCount}";
    }

    private static string GetWindowsVersion()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string? productName = key?.GetValue("ProductName") as string;
            string? editionId = key?.GetValue("EditionID") as string;
            string? displayVersion = key?.GetValue("DisplayVersion") as string;
            string? buildNumber = key?.GetValue("CurrentBuildNumber") as string;
            object? ubrValue = key?.GetValue("UBR");
            int? build = int.TryParse(buildNumber, out int parsedBuild) ? parsedBuild : null;

            string normalizedProductName = NormalizeWindowsProductName(productName, editionId, build);

            string buildSuffix = string.IsNullOrWhiteSpace(buildNumber)
                ? string.Empty
                : ubrValue is int ubr
                    ? $" (Build {buildNumber}.{ubr})"
                    : $" (Build {buildNumber})";

            if (!string.IsNullOrWhiteSpace(normalizedProductName))
            {
                if (!string.IsNullOrWhiteSpace(displayVersion))
                {
                    return $"{normalizedProductName} {displayVersion}{buildSuffix}";
                }

                return $"{normalizedProductName}{buildSuffix}";
            }
        }
        catch
        {
        }

        return Environment.OSVersion.VersionString;
    }

    private static string NormalizeWindowsProductName(string? productName, string? editionId, int? buildNumber)
    {
        string normalizedProductName = productName?.Trim() ?? string.Empty;
        string edition = ExtractWindowsEdition(normalizedProductName, editionId);

        if (buildNumber.HasValue && buildNumber.Value >= 22000)
        {
            return string.IsNullOrWhiteSpace(edition)
                ? "Windows 11"
                : $"Windows 11 {edition}";
        }

        return string.IsNullOrWhiteSpace(normalizedProductName) ? "Windows" : normalizedProductName;
    }

    private static string ExtractWindowsEdition(string productName, string? editionId)
    {
        if (productName.Contains("Enterprise", StringComparison.OrdinalIgnoreCase))
        {
            return "Enterprise";
        }

        if (productName.Contains("Education", StringComparison.OrdinalIgnoreCase))
        {
            return "Education";
        }

        if (productName.Contains("Home", StringComparison.OrdinalIgnoreCase))
        {
            return "Home";
        }

        if (productName.Contains("Pro", StringComparison.OrdinalIgnoreCase))
        {
            return "Pro";
        }

        if (string.IsNullOrWhiteSpace(editionId))
        {
            return string.Empty;
        }

        return editionId.Trim() switch
        {
            "Professional" => "Pro",
            "Core" => "Home",
            "Enterprise" => "Enterprise",
            "Education" => "Education",
            var other when other.StartsWith("Professional", StringComparison.OrdinalIgnoreCase) => "Pro",
            var other when other.StartsWith("Enterprise", StringComparison.OrdinalIgnoreCase) => "Enterprise",
            var other when other.StartsWith("Education", StringComparison.OrdinalIgnoreCase) => "Education",
            var other when other.StartsWith("Core", StringComparison.OrdinalIgnoreCase) => "Home",
            _ => string.Empty
        };
    }

    private static string FormatUptime(long tickCount64)
    {
        if (tickCount64 < 0)
        {
            return "取得不可";
        }

        TimeSpan uptime = TimeSpan.FromMilliseconds(tickCount64);
        if (uptime.TotalDays >= 1)
        {
            return $"{(int)uptime.TotalDays}日 {uptime.Hours}時間 {uptime.Minutes}分";
        }

        return $"{uptime.Hours}時間 {uptime.Minutes}分";
    }

    private static bool TryGetVolumeInformation(string rootPath, out VolumeInformation volumeInformation)
    {
        var volumeNameBuilder = new StringBuilder(260);
        var fileSystemBuilder = new StringBuilder(260);
        volumeInformation = default;

        if (!GetVolumeInformation(
                rootPath,
                volumeNameBuilder,
                volumeNameBuilder.Capacity,
                out uint serialNumber,
                out _,
                out _,
                fileSystemBuilder,
                fileSystemBuilder.Capacity))
        {
            return false;
        }

        volumeInformation = new VolumeInformation(
            volumeNameBuilder.ToString(),
            fileSystemBuilder.ToString(),
            serialNumber.ToString("X8"));
        return true;
    }

    private static bool TryGetDiskGeometry(string rootPath, out DiskGeometrySnapshot geometry)
    {
        geometry = default;
        if (!GetDiskFreeSpace(
                rootPath,
                out uint sectorsPerCluster,
                out uint bytesPerSector,
                out _,
                out _))
        {
            return false;
        }

        geometry = new DiskGeometrySnapshot(sectorsPerCluster, bytesPerSector);
        return true;
    }

    private static T SafeGet<T>(Func<T> getter, T fallback)
    {
        try
        {
            return getter();
        }
        catch
        {
            return fallback;
        }
    }

    private static T? SafeGetNullable<T>(Func<T> getter) where T : struct
    {
        try
        {
            return getter();
        }
        catch
        {
            return null;
        }
    }

    private readonly record struct VolumeInformation(string VolumeLabel, string FileSystem, string SerialNumber);
    private readonly record struct DiskGeometrySnapshot(uint SectorsPerCluster, uint BytesPerSector);
    private readonly record struct DisplayDeviceSnapshot(string DeviceName, string DeviceKey);
    private readonly record struct GpuRegistryInfo(string DriverVersion, ulong? MemoryBytes);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct DisplayDevice
    {
        public int cb;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;

        public int StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpace(
        string lpRootPathName,
        out uint lpSectorsPerCluster,
        out uint lpBytesPerSector,
        out uint lpNumberOfFreeClusters,
        out uint lpTotalNumberOfClusters);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformation(
        string lpRootPathName,
        StringBuilder lpVolumeNameBuffer,
        int nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        StringBuilder lpFileSystemNameBuffer,
        int nFileSystemNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool EnumDisplayDevices(
        string? lpDevice,
        uint iDevNum,
        ref DisplayDevice lpDisplayDevice,
        uint dwFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        ref StoragePropertyQuery lpInBuffer,
        int nInBufferSize,
        ref DeviceSeekPenaltyDescriptor lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery
    {
        public int PropertyId;
        public int QueryType;
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceSeekPenaltyDescriptor
    {
        public uint Version;
        public uint Size;
        [MarshalAs(UnmanagedType.U1)]
        public bool IncursSeekPenalty;
    }

    private const uint IoctlStorageQueryProperty = 0x2D1400;
    private const int StorageDeviceSeekPenaltyProperty = 7;
    private const int PropertyStandardQuery = 0;
    private const uint OpenExisting = 3;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    private enum LOGICAL_PROCESSOR_RELATIONSHIP
    {
        RelationProcessorCore = 0,
        RelationNumaNode = 1,
        RelationCache = 2,
        RelationProcessorPackage = 3,
        RelationGroup = 4,
        RelationAll = 0xffff
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(
        LOGICAL_PROCESSOR_RELATIONSHIP relationshipType,
        IntPtr buffer,
        ref int returnedLength);

    private static string GetPhysicalCoreCountText()
    {
        int length = 0;
        try
        {
            if (!GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore, IntPtr.Zero, ref length))
            {
                int error = Marshal.GetLastWin32Error();
                if (error != 122) // ERROR_INSUFFICIENT_BUFFER
                {
                    return "取得不可";
                }
            }

            if (length == 0)
            {
                return "取得不可";
            }

            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (GetLogicalProcessorInformationEx(LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore, buffer, ref length))
                {
                    int coreCount = 0;
                    int offset = 0;
                    while (offset < length)
                    {
                        IntPtr currentPtr = IntPtr.Add(buffer, offset);
                        int relationshipVal = Marshal.ReadInt32(currentPtr, 0);
                        int size = Marshal.ReadInt32(currentPtr, 4);

                        if (size <= 0)
                        {
                            break; // 無限ループ防止
                        }

                        if (relationshipVal == (int)LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore)
                        {
                            coreCount++;
                        }

                        offset += size;
                    }

                    return coreCount > 0 ? coreCount.ToString() : "取得不可";
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            // 例外発生時は安全に取得不可を返す
        }

        return "取得不可";
    }
}
