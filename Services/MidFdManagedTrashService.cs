using System.Text.Json;
using System.Diagnostics;
using MidFD.Models;
using MidFD.Services.TrashManifestStore;
using MidFD.Configuration.Storage;

namespace MidFD.Services;

internal readonly record struct ManagedTrashRestoreStageMetrics(
    int Count,
    long TotalMs,
    long MaxMs);

internal readonly record struct ManagedTrashRestoreDiagnosticsSnapshot(
    int RestoreCount,
    long RestoreTotalMs,
    int ManifestRecordCount,
    ManagedTrashRestoreStageMetrics MutationAcquire,
    ManagedTrashRestoreStageMetrics DestinationCheck,
    ManagedTrashRestoreStageMetrics ManifestLookup,
    ManagedTrashRestoreStageMetrics ManifestLoad,
    ManagedTrashRestoreStageMetrics ManifestScan,
    int ManifestScanRecordCount,
    ManagedTrashRestoreStageMetrics RecordValidation,
    ManagedTrashRestoreStageMetrics PathValidation,
    ManagedTrashRestoreStageMetrics DirectoryPrepare,
    ManagedTrashRestoreStageMetrics PhysicalMove,
    ManagedTrashRestoreStageMetrics StatusUpdate,
    ManagedTrashRestoreStageMetrics BatchStatusUpdate,
    int BatchStatusUpdatedRecordCount,
    long OtherMs);

internal sealed class ManagedTrashRestoreDiagnostics
{
    private readonly object _sync = new();
    private readonly Dictionary<string, ManagedTrashRestoreStageMetrics> _stages = new(StringComparer.Ordinal);
    private int _restoreCount;
    private long _restoreTotalMs;
    private int _manifestRecordCount;
    private int _manifestScanRecordCount;
    private int _batchStatusUpdatedRecordCount;

    public void RecordRestore(long elapsedMs)
    {
        lock (_sync)
        {
            _restoreCount++;
            _restoreTotalMs += Math.Max(0, elapsedMs);
        }
    }

    public void RecordStage(string name, long elapsedMs)
    {
        lock (_sync)
        {
            RecordStageLocked(name, elapsedMs);
        }
    }

    public void RecordBatchStatusUpdate(long elapsedMs, int updatedRecordCount)
    {
        lock (_sync)
        {
            RecordStageLocked("BatchStatusUpdate", elapsedMs);
            _batchStatusUpdatedRecordCount += Math.Max(0, updatedRecordCount);
        }
    }

    public void RecordStageAggregate(string name, int count, long totalMs, long maxMs)
    {
        if (count <= 0) return;

        lock (_sync)
        {
            _stages.TryGetValue(name, out ManagedTrashRestoreStageMetrics current);
            _stages[name] = new ManagedTrashRestoreStageMetrics(
                current.Count + count,
                current.TotalMs + Math.Max(0, totalMs),
                Math.Max(current.MaxMs, Math.Max(0, maxMs)));
        }
    }

    public void RecordManifestScan(long elapsedMs, int recordCount)
    {
        lock (_sync)
        {
            RecordStageLocked("ManifestScan", elapsedMs);
            _manifestScanRecordCount += Math.Max(0, recordCount);
        }
    }

    public void SetManifestRecordCount(int count)
    {
        lock (_sync)
        {
            _manifestRecordCount = Math.Max(_manifestRecordCount, count);
        }
    }

    public ManagedTrashRestoreDiagnosticsSnapshot Snapshot()
    {
        lock (_sync)
        {
            ManagedTrashRestoreStageMetrics mutationAcquire = GetStage("MutationAcquire");
            ManagedTrashRestoreStageMetrics destinationCheck = GetStage("DestinationCheck");
            ManagedTrashRestoreStageMetrics manifestLookup = GetStage("ManifestLookup");
            ManagedTrashRestoreStageMetrics manifestLoad = GetStage("ManifestLoad");
            ManagedTrashRestoreStageMetrics manifestScan = GetStage("ManifestScan");
            ManagedTrashRestoreStageMetrics recordValidation = GetStage("RecordValidation");
            ManagedTrashRestoreStageMetrics pathValidation = GetStage("PathValidation");
            ManagedTrashRestoreStageMetrics directoryPrepare = GetStage("DirectoryPrepare");
            ManagedTrashRestoreStageMetrics physicalMove = GetStage("PhysicalMove");
            ManagedTrashRestoreStageMetrics statusUpdate = GetStage("StatusUpdate");
            ManagedTrashRestoreStageMetrics batchStatusUpdate = GetStage("BatchStatusUpdate");
            long accountedMs = mutationAcquire.TotalMs +
                               destinationCheck.TotalMs +
                               manifestLookup.TotalMs +
                               pathValidation.TotalMs +
                               directoryPrepare.TotalMs +
                               physicalMove.TotalMs +
                               statusUpdate.TotalMs;
            return new ManagedTrashRestoreDiagnosticsSnapshot(
                _restoreCount,
                _restoreTotalMs,
                _manifestRecordCount,
                mutationAcquire,
                destinationCheck,
                manifestLookup,
                manifestLoad,
                manifestScan,
                _manifestScanRecordCount,
                recordValidation,
                pathValidation,
                directoryPrepare,
                physicalMove,
                statusUpdate,
                batchStatusUpdate,
                _batchStatusUpdatedRecordCount,
                Math.Max(0, _restoreTotalMs - accountedMs));
        }
    }

    public string BuildLogMessage(string batchId)
    {
        ManagedTrashRestoreDiagnosticsSnapshot snapshot = Snapshot();
        return
            $"[DeleteCancelRestorePerf] batchId={batchId}, restoreCount={snapshot.RestoreCount}, " +
            $"restoreTotalMs={snapshot.RestoreTotalMs}, manifestRecordCount={snapshot.ManifestRecordCount}, " +
            $"mutationAcquireCount={snapshot.MutationAcquire.Count}, mutationAcquireTotalMs={snapshot.MutationAcquire.TotalMs}, mutationAcquireMaxMs={snapshot.MutationAcquire.MaxMs}, " +
            $"destinationCheckCount={snapshot.DestinationCheck.Count}, destinationCheckTotalMs={snapshot.DestinationCheck.TotalMs}, destinationCheckMaxMs={snapshot.DestinationCheck.MaxMs}, " +
            $"manifestLookupCount={snapshot.ManifestLookup.Count}, manifestLookupTotalMs={snapshot.ManifestLookup.TotalMs}, manifestLookupMaxMs={snapshot.ManifestLookup.MaxMs}, " +
            $"manifestLoadCount={snapshot.ManifestLoad.Count}, manifestLoadTotalMs={snapshot.ManifestLoad.TotalMs}, manifestLoadMaxMs={snapshot.ManifestLoad.MaxMs}, " +
            $"manifestScanCount={snapshot.ManifestScan.Count}, manifestScanRecordCount={snapshot.ManifestScanRecordCount}, manifestScanTotalMs={snapshot.ManifestScan.TotalMs}, manifestScanMaxMs={snapshot.ManifestScan.MaxMs}, " +
            $"recordValidationCount={snapshot.RecordValidation.Count}, recordValidationTotalMs={snapshot.RecordValidation.TotalMs}, recordValidationMaxMs={snapshot.RecordValidation.MaxMs}, " +
            $"pathValidationCount={snapshot.PathValidation.Count}, pathValidationTotalMs={snapshot.PathValidation.TotalMs}, pathValidationMaxMs={snapshot.PathValidation.MaxMs}, " +
            $"directoryPrepareCount={snapshot.DirectoryPrepare.Count}, directoryPrepareTotalMs={snapshot.DirectoryPrepare.TotalMs}, directoryPrepareMaxMs={snapshot.DirectoryPrepare.MaxMs}, " +
            $"physicalMoveCount={snapshot.PhysicalMove.Count}, physicalMoveTotalMs={snapshot.PhysicalMove.TotalMs}, physicalMoveMaxMs={snapshot.PhysicalMove.MaxMs}, " +
            $"perItemStatusUpdateCount={snapshot.StatusUpdate.Count}, perItemStatusUpdateTotalMs={snapshot.StatusUpdate.TotalMs}, perItemStatusUpdateMaxMs={snapshot.StatusUpdate.MaxMs}, " +
            $"batchStatusUpdateCallCount={snapshot.BatchStatusUpdate.Count}, batchStatusUpdateRecordCount={snapshot.BatchStatusUpdatedRecordCount}, batchStatusUpdateTotalMs={snapshot.BatchStatusUpdate.TotalMs}, batchStatusUpdateMaxMs={snapshot.BatchStatusUpdate.MaxMs}, " +
            $"otherMs={snapshot.OtherMs}";
    }

    private void RecordStageLocked(string name, long elapsedMs)
    {
        elapsedMs = Math.Max(0, elapsedMs);
        _stages.TryGetValue(name, out ManagedTrashRestoreStageMetrics current);
        _stages[name] = new ManagedTrashRestoreStageMetrics(
            current.Count + 1,
            current.TotalMs + elapsedMs,
            Math.Max(current.MaxMs, elapsedMs));
    }

    private ManagedTrashRestoreStageMetrics GetStage(string name)
        => _stages.TryGetValue(name, out ManagedTrashRestoreStageMetrics metrics)
            ? metrics
            : default;
}

internal sealed class ManagedTrashRestoreLookup
{
    private readonly IReadOnlyDictionary<string, TrashManifestRecord> _records;
    private readonly IReadOnlySet<string> _duplicatePaths;

    public ManagedTrashRestoreLookup(
        IReadOnlyDictionary<string, TrashManifestRecord> records,
        IReadOnlySet<string> duplicatePaths)
    {
        _records = records;
        _duplicatePaths = duplicatePaths;
    }

    public TrashManifestRecord Require(string trashPath)
    {
        string normalizedPath = Path.GetFullPath(trashPath);
        if (_duplicatePaths.Contains(normalizedPath) || !_records.TryGetValue(normalizedPath, out TrashManifestRecord? record))
        {
            throw new InvalidOperationException("管理ゴミ箱manifest recordのidentityが一致しません。");
        }

        return record;
    }
}

public static class MidFdManagedTrashService
{
    private const string TrashDirectoryName = ".midfd-trash";
    private const string ItemsDirectoryName = "items";
    private const string ManifestFileName = "manifest.json";
    private const int MaxVisibleOriginalNameLength = 120;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
    private static readonly object MutationSync = new();
    private static readonly object StartupSync = new();
    private static readonly AsyncLocal<Guid?> MutationBatchContext = new();
    private static Guid? _activeMutationBatchId;
    private static string _manifestPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MidFD", "Trash", ManifestFileName);
    private static string _sqliteManifestPath = Path.Combine(AppContext.BaseDirectory, "Data", "Trash", "manifest.db");
    private static string _itemsRoot = Path.Combine(AppContext.BaseDirectory, "Data", "Trash", ItemsDirectoryName);
    private static ManagedTrashPathValidator _pathValidator = new(new[] { _itemsRoot });
    private static ITrashManifestStore? ManifestStore;
    private static string SqliteManifestPath => _sqliteManifestPath;
    internal static bool IsAvailable
    {
        get { lock (StartupSync) return ManifestStore != null; }
    }

    internal static string AvailabilityMessage =>
        "管理ゴミ箱のmanifest storageを初期化できないため、この機能だけを停止しています。通常の閲覧は継続できます。";

    public static void Initialize(Configuration.AppSettings settings)
    {
        AppStoragePaths activePaths = StorageProfileProviderFactory
            .CreateForActivation(Configuration.SettingsManager.CurrentStorageProfileActivation)
            .GetPaths();
        Initialize(settings, activePaths);
    }

    internal static IDisposable InitializeForTest(Configuration.AppSettings settings, AppStoragePaths activePaths)
    {
        lock (StartupSync)
        {
            var snapshot = new ManagedTrashInitializationSnapshot(
                ManifestStore,
                _manifestPath,
                _sqliteManifestPath,
                _itemsRoot,
                _pathValidator);
            Initialize(settings, activePaths);
            return snapshot;
        }
    }

    private static void Initialize(Configuration.AppSettings settings, AppStoragePaths activePaths)
    {
        lock (StartupSync)
        {
            ManifestStore = null;
            try
            {
                _sqliteManifestPath = Path.GetFullPath(activePaths.TrashManifestDbPath);
                string profileRoot = Path.GetFullPath(activePaths.ProfileRoot);
                string trashDirectory = Path.GetDirectoryName(_sqliteManifestPath) ?? profileRoot;
                if (!IsPathWithinOrEqual(trashDirectory, profileRoot) ||
                    !IsPathWithinOrEqual(_sqliteManifestPath, profileRoot))
                {
                    throw new InvalidOperationException("Managed trash manifest must be within the active profile root.");
                }

                _manifestPath = Path.Combine(trashDirectory, ManifestFileName);
                _itemsRoot = Path.Combine(trashDirectory, ItemsDirectoryName);
                string legacyLocalItemsRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MidFD",
                    "Trash",
                    ItemsDirectoryName);
                _pathValidator = new ManagedTrashPathValidator(new[] { _itemsRoot, legacyLocalItemsRoot });

                var mode = Configuration.ManagedTrashStoreMode.Sqlite;
                if (IsExecutableDirectoryNetworkPath())
                {
                    LogService.Warn($"[MidFdTrashStore] SQLite disabled because executable directory is network path. BaseDirectory={AppContext.BaseDirectory}");
                    mode = Configuration.ManagedTrashStoreMode.Json;
                }

                ManifestStore = TrashManifestStoreFactory.CreateStore(ref mode, ManifestPath, SqliteManifestPath, JsonOptions);
                if (settings?.FileOperations != null) settings.FileOperations.ManagedTrashStoreMode = mode;
                LogService.Info("[MidFdTrashStore] Manifest store initialized without physical item migration.");
            }
            catch (Exception ex)
            {
                ManifestStore = null;
                LogService.Error("[MidFdTrashStore] Manifest store initialization failed; managed trash is unavailable for this session.", ex);
            }
        }
    }

    private sealed class ManagedTrashInitializationSnapshot(
        ITrashManifestStore? manifestStore,
        string manifestPath,
        string sqliteManifestPath,
        string itemsRoot,
        ManagedTrashPathValidator pathValidator) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            lock (StartupSync)
            {
                ManifestStore = manifestStore;
                _manifestPath = manifestPath;
                _sqliteManifestPath = sqliteManifestPath;
                _itemsRoot = itemsRoot;
                _pathValidator = pathValidator;
                _disposed = true;
            }
        }
    }

    public static bool IsExecutableDirectoryNetworkPath()
    {
        try
        {
            string path = AppContext.BaseDirectory;
            if (path.StartsWith(@"\\")) return true;

            string? root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root)) return false;

            var driveInfo = new DriveInfo(root);
            return driveInfo.DriveType == DriveType.Network;
        }
        catch (Exception ex)
        {
            LogService.Warn($"[MidFdTrashStore] Failed to determine if executable directory is network path. path={AppContext.BaseDirectory}, error={ex.Message}");
            return true; // Safe side
        }
    }

    private static bool _suppressSuccessLogging;
    private static int _manifestRecordCountBefore;
    private static int _manifestRecordCountAfter;
    private static int _manifestAppendCount;
    private static long _manifestAppendMs;
    private static long _manifestUpsertScanCount;
    private static bool _manifestAppendMode;
    private static int _manifestRecordBatchCount;
    private static int _manifestRecordBatchFlushCount;
    private static int _manifestActiveBatchSaveCount;
    private static long _manifestRecordBatchMs;
    private static long _manifestDbConnectionOpenMs;
    private static long _manifestDbTransactionBeginMs;
    private static long _manifestDbDeleteLoopMs;
    private static long _manifestDbInsertLoopMs;
    private static long _manifestDbCommitMs;
    private static long _totalFileMoveMs;
    private static int _crossVolumeMoveCount;
    private static int _sameVolumeMoveCount;
    private static int _appDataFallbackMoveCount;
    private static readonly SemaphoreSlim RetentionCleanupGate = new(1, 1);
    private static DateTime _lastRetentionCleanupStartedUtc = DateTime.MinValue;
    private static readonly TimeSpan RetentionCleanupThrottle = TimeSpan.FromMinutes(5);
    private const int RetentionCleanupChunkSize = 16;
    private const int ForegroundMutationWaitMilliseconds = 25;
    private const int RetentionCleanupSampleLimit = 5;
    private static readonly TimeSpan ForegroundMutationWaitTimeout = TimeSpan.FromSeconds(5);
    private static int _maintenanceMutationActive;
    private static int _foregroundMutationWaiters;

    public static void RecordDbOperationTimings(long connMs, long transMs, long delMs, long insMs, long commitMs)
    {
        _manifestDbConnectionOpenMs += connMs;
        _manifestDbTransactionBeginMs += transMs;
        _manifestDbDeleteLoopMs += delMs;
        _manifestDbInsertLoopMs += insMs;
        _manifestDbCommitMs += commitMs;
    }

    private static int _suppressedSuccessCount;

    public static void SetLoggingSuppression(bool suppress)
    {
        _suppressSuccessLogging = suppress;
        if (!suppress) _suppressedSuccessCount = 0;
    }

    public static bool IsLoggingSuppressed()
    {
        return _suppressSuccessLogging;
    }

    public static int GetSuppressedSuccessCount()
    {
        return _suppressedSuccessCount;
    }

    private static long _lastManifestLookupMs;
    private static long _lastManifestFileMoveMs;
    private static long _lastManifestStatusUpdateMs;

    public static void ResetManifestOperationDiagnostics()
    {
        _manifestRecordCountBefore = 0;
        _manifestRecordCountAfter = 0;
        _manifestAppendCount = 0;
        _manifestAppendMs = 0;
        _manifestUpsertScanCount = 0;
        _manifestAppendMode = false;
        _manifestRecordBatchCount = 0;
        _manifestRecordBatchFlushCount = 0;
        _manifestActiveBatchSaveCount = 0;
        _manifestRecordBatchMs = 0;
        _manifestDbConnectionOpenMs = 0;
        _manifestDbTransactionBeginMs = 0;
        _manifestDbDeleteLoopMs = 0;
        _manifestDbInsertLoopMs = 0;
        _manifestDbCommitMs = 0;
        _totalFileMoveMs = 0;
        _crossVolumeMoveCount = 0;
        _sameVolumeMoveCount = 0;
        _appDataFallbackMoveCount = 0;
        _lastManifestLookupMs = 0;
        _lastManifestFileMoveMs = 0;
        _lastManifestStatusUpdateMs = 0;
        _suppressedSuccessCount = 0;
    }

    public static (long lookup, long fileMove, long statusUpdate, long manifestStore) GetUndoRedoMetrics()
    {
        return (_lastManifestLookupMs, _lastManifestFileMoveMs, _lastManifestStatusUpdateMs, _manifestAppendMs);
    }

    private static TrashManifest? _activeBatchManifest;

    public static void BeginManifestBatch()
    {
        using IDisposable mutation = EnterMutation();
        if (_activeMutationBatchId != null) throw new InvalidOperationException("管理ゴミ箱batchは既に開始されています。");
        Guid batchId = Guid.NewGuid();
        _activeMutationBatchId = batchId;
        MutationBatchContext.Value = batchId;
        try
        {
            _activeBatchManifest = LoadManifest();
            _manifestRecordCountBefore = _activeBatchManifest.Records.Count;
            _manifestRecordCountAfter = _activeBatchManifest.Records.Count;
            _manifestAppendCount = 0;
            _manifestAppendMs = 0;
            _manifestUpsertScanCount = 0;
            _manifestAppendMode = true;
        }
        catch
        {
            _activeBatchManifest = null;
            _activeMutationBatchId = null;
            MutationBatchContext.Value = null;
            throw;
        }
    }

    public static void FlushManifestBatch()
    {
        using IDisposable mutation = EnterMutation();
        try
        {
            if (_activeBatchManifest != null)
            {
                SaveManifest(_activeBatchManifest);
                _manifestRecordCountAfter = _activeBatchManifest.Records.Count;
            }
        }
        finally
        {
            _activeBatchManifest = null;
            _activeMutationBatchId = null;
            MutationBatchContext.Value = null;
        }
    }

    public static void SaveActiveBatch()
    {
        using IDisposable mutation = EnterMutation();
        if (_activeBatchManifest != null)
        {
            SaveManifest(_activeBatchManifest);
            _manifestActiveBatchSaveCount++;
        }
    }

    public static ManifestOperationDiagnostics GetManifestOperationDiagnostics()
    {
        int recordCountAfter = _activeBatchManifest?.Records.Count ?? _manifestRecordCountAfter;
        return new ManifestOperationDiagnostics(
            _manifestAppendCount,
            _manifestUpsertScanCount,
            _manifestAppendMs,
            _manifestRecordCountBefore,
            recordCountAfter,
            _manifestAppendMode,
            _manifestRecordBatchCount,
            _manifestRecordBatchFlushCount,
            _manifestActiveBatchSaveCount,
            _manifestRecordBatchMs,
            _manifestDbConnectionOpenMs,
            _manifestDbTransactionBeginMs,
            _manifestDbDeleteLoopMs,
            _manifestDbInsertLoopMs,
            _manifestDbCommitMs,
            _totalFileMoveMs,
            _crossVolumeMoveCount,
            _sameVolumeMoveCount,
            _appDataFallbackMoveCount);
    }

    public static FileOperationUndoRedoItem MoveToTrash(string originalPath, string batchId, int itemIndex, bool skipRegistration = false, bool suppressLogging = false)
    {
        return MoveToTrash(originalPath, batchId, itemIndex, skipRegistration, out _, out _, out _, out _, suppressLogging: suppressLogging);
    }

    internal static FileOperationUndoRedoItem MoveToTrash(string originalPath, string batchId, int itemIndex, bool skipRegistration, out TrashManifestRecord? outRecord, bool suppressLogging = false)
    {
        return MoveToTrash(originalPath, batchId, itemIndex, skipRegistration, out outRecord, out _, out _, out _, suppressLogging: suppressLogging);
    }

    public static FileOperationUndoRedoItem MoveToTrash(
        string originalPath,
        string batchId,
        int itemIndex,
        out long fileMoveMs,
        out long recordUpsertMs,
        out long logMs,
        bool suppressLogging = false)
    {
        return MoveToTrash(originalPath, batchId, itemIndex, false, out _, out fileMoveMs, out recordUpsertMs, out logMs, suppressLogging: suppressLogging);
    }

    internal static FileOperationUndoRedoItem MoveToTrash(
        string originalPath,
        string batchId,
        int itemIndex,
        bool skipRegistration,
        out TrashManifestRecord? outRecord,
        out long fileMoveMs,
        out long recordUpsertMs,
        out long logMs,
        bool suppressLogging = false)
    {
        using IDisposable mutation = EnterMutation();
        if (string.IsNullOrWhiteSpace(originalPath))
        {
            throw new ArgumentException("削除対象 path が空です。", nameof(originalPath));
        }

        if (!ReparsePointHelper.Exists(originalPath) && !File.Exists(originalPath) && !Directory.Exists(originalPath))
        {
            throw new FileNotFoundException("削除対象が見つかりません。", originalPath);
        }

        var totalSw = Stopwatch.StartNew();

        bool isDirectory = ReparsePointHelper.IsDirectory(originalPath);
        string root = ResolveTrashRoot(originalPath);
        string itemId = itemIndex.ToString("D4");
        string trashPath = BuildUniqueTrashPath(root, batchId, itemId, Path.GetFileName(originalPath));
        _pathValidator.ValidatePath(trashPath);
        Directory.CreateDirectory(Path.GetDirectoryName(trashPath) ?? root);

        var moveSw = Stopwatch.StartNew();
        FileOperationService.Move(originalPath, trashPath, suppressLogging: suppressLogging);
        moveSw.Stop();
        fileMoveMs = moveSw.ElapsedMilliseconds;
        _totalFileMoveMs += fileMoveMs;
        if (suppressLogging) _suppressedSuccessCount++;
 
        // Placement metrics for investigation
        string? originalRoot = Path.GetPathRoot(Path.GetFullPath(originalPath));
        string? trashRoot = Path.GetPathRoot(root);
        bool isSameVolume = string.Equals(originalRoot, trashRoot, StringComparison.OrdinalIgnoreCase);

        if (isSameVolume)
        {
            _sameVolumeMoveCount++;
        }
        else
        {
            _crossVolumeMoveCount++;
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (root.Contains(localAppData, StringComparison.OrdinalIgnoreCase))
        {
            _appDataFallbackMoveCount++;
        }

        // Integrity Check
        bool sourceExists = ReparsePointHelper.Exists(originalPath) || File.Exists(originalPath) || Directory.Exists(originalPath);
        bool trashExists = ReparsePointHelper.Exists(trashPath) || File.Exists(trashPath) || Directory.Exists(trashPath);

        if (sourceExists || !trashExists)
        {
            string sourceDetail = sourceExists
                ? (ReparsePointHelper.IsReparsePoint(originalPath)
                    ? "LINK"
                    : Directory.Exists(originalPath) ? "DIR" : $"FILE({new FileInfo(originalPath).Length} bytes)")
                : "NONE";
            string trashDetail = trashExists
                ? (ReparsePointHelper.IsReparsePoint(trashPath)
                    ? "LINK"
                    : Directory.Exists(trashPath) ? "DIR" : $"FILE({new FileInfo(trashPath).Length} bytes)")
                : "NONE";

            LogService.Error($"[MidFdTrashIntegrity] Move verification failed. original={originalPath}, trash={trashPath}, sourceExists={sourceExists}({sourceDetail}), trashExists={trashExists}({trashDetail})");
            throw new IOException($"MidFD管理ゴミ箱への移動検証に失敗しました。ソースが残存しているか、移動先に実体がありません。 path={originalPath}");
        }

        if (!suppressLogging)
        {
            LogService.Info($"[MidFdTrashIntegrity] AfterMove verified. original={originalPath}, trash={trashPath}");
        }

        var recordSw = Stopwatch.StartNew();
        var record = new TrashManifestRecord
        {
            BatchId = batchId,
            ItemId = itemId,
            OriginalPath = originalPath,
            TrashPath = trashPath,
            OriginalName = Path.GetFileName(originalPath),
            IsDirectory = isDirectory,
            Size = isDirectory || ReparsePointHelper.IsReparsePoint(trashPath) ? 0 : new FileInfo(trashPath).Length,
            LastWriteTimeUtc = ReparsePointHelper.IsReparsePoint(trashPath)
                ? DateTime.UtcNow
                : isDirectory
                ? Directory.GetLastWriteTimeUtc(trashPath)
                : File.GetLastWriteTimeUtc(trashPath),
            DeletedAtUtc = DateTime.UtcNow,
            Status = TrashRecordStatus.InTrash
        };

        if (!skipRegistration)
        {
            RegisterNewTrashRecord(record);
        }
        outRecord = record;
        recordSw.Stop();
        recordUpsertMs = recordSw.ElapsedMilliseconds;

        var logSw = Stopwatch.StartNew();
        if (!suppressLogging)
        {
            LogService.Info(
                $"[MidFdTrash] Moved to managed trash. batchId={batchId}, itemId={itemId}, " +
                $"original={originalPath}, trash={trashPath}, isDirectory={isDirectory}");
        }
        logSw.Stop();
        logMs = logSw.ElapsedMilliseconds;

        totalSw.Stop();
        if (totalSw.ElapsedMilliseconds > 1000)
        {
            LogService.Info($"[MidFdTrash] SlowMove operationId={batchId} index={itemId} elapsedMs={totalSw.ElapsedMilliseconds} original={originalPath} trash={trashPath}");
        }

        return new FileOperationUndoRedoItem
        {
            BeforePath = originalPath,
            BeforeName = Path.GetFileName(originalPath),
            RecycleBinPath = trashPath,
            RecycleBinDeletedAtUtc = record.DeletedAtUtc
        };
    }

    public static void RestoreFromTrash(FileOperationUndoRedoItem item, bool skipStatusUpdate = false, bool suppressLogging = false)
    {
        RestoreFromTrashCore(item, skipStatusUpdate, suppressLogging, diagnostics: null, restoreLookup: null);
    }

    internal static void RestoreFromTrashWithLookup(
        FileOperationUndoRedoItem item,
        bool skipStatusUpdate,
        bool suppressLogging,
        ManagedTrashRestoreLookup restoreLookup)
    {
        ArgumentNullException.ThrowIfNull(restoreLookup);
        RestoreFromTrashCore(item, skipStatusUpdate, suppressLogging, diagnostics: null, restoreLookup: restoreLookup);
    }

    internal static ManagedTrashRestoreLookup CreateRestoreLookup(ManagedTrashRestoreDiagnostics? diagnostics = null)
    {
        TrashManifest manifest;
        if (_activeBatchManifest != null)
        {
            manifest = _activeBatchManifest;
        }
        else
        {
            var manifestLoadSw = Stopwatch.StartNew();
            try
            {
                manifest = LoadManifest();
            }
            finally
            {
                manifestLoadSw.Stop();
                diagnostics?.RecordStage("ManifestLoad", manifestLoadSw.ElapsedMilliseconds);
            }
        }

        diagnostics?.SetManifestRecordCount(manifest.Records.Count);
        var records = new Dictionary<string, TrashManifestRecord>(StringComparer.OrdinalIgnoreCase);
        var duplicatePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int recordValidationCount = 0;
        long recordValidationTotalMs = 0;
        long recordValidationMaxMs = 0;
        var manifestScanSw = Stopwatch.StartNew();
        try
        {
            foreach (TrashManifestRecord record in manifest.Records)
            {
                var recordValidationSw = Stopwatch.StartNew();
                try
                {
                    string validatedPath;
                    try
                    {
                        validatedPath = _pathValidator.ValidateRecord(record);
                    }
                    catch
                    {
                        continue;
                    }

                    if (duplicatePaths.Contains(validatedPath))
                    {
                        continue;
                    }

                    if (!records.TryAdd(validatedPath, record))
                    {
                        records.Remove(validatedPath);
                        duplicatePaths.Add(validatedPath);
                    }
                }
                finally
                {
                    recordValidationSw.Stop();
                    recordValidationCount++;
                    long elapsedMs = recordValidationSw.ElapsedMilliseconds;
                    recordValidationTotalMs += elapsedMs;
                    recordValidationMaxMs = Math.Max(recordValidationMaxMs, elapsedMs);
                }
            }
        }
        finally
        {
            manifestScanSw.Stop();
            diagnostics?.RecordManifestScan(manifestScanSw.ElapsedMilliseconds, manifest.Records.Count);
            diagnostics?.RecordStageAggregate(
                "RecordValidation",
                recordValidationCount,
                recordValidationTotalMs,
                recordValidationMaxMs);
        }

        return new ManagedTrashRestoreLookup(records, duplicatePaths);
    }

    internal static void RestoreFromTrashWithDiagnostics(
        FileOperationUndoRedoItem item,
        bool skipStatusUpdate,
        bool suppressLogging,
        ManagedTrashRestoreDiagnostics diagnostics,
        ManagedTrashRestoreLookup? restoreLookup = null)
    {
        RestoreFromTrashCore(item, skipStatusUpdate, suppressLogging, diagnostics, restoreLookup);
    }

    private static void RestoreFromTrashCore(
        FileOperationUndoRedoItem item,
        bool skipStatusUpdate,
        bool suppressLogging,
        ManagedTrashRestoreDiagnostics? diagnostics,
        ManagedTrashRestoreLookup? restoreLookup)
    {
        var restoreSw = Stopwatch.StartNew();
        IDisposable? mutation = null;
        try
        {
            var acquireSw = Stopwatch.StartNew();
            try
            {
                mutation = EnterMutation();
            }
            finally
            {
                acquireSw.Stop();
                diagnostics?.RecordStage("MutationAcquire", acquireSw.ElapsedMilliseconds);
            }

            if (string.IsNullOrWhiteSpace(item.BeforePath) || string.IsNullOrWhiteSpace(item.RecycleBinPath))
            {
                throw new InvalidOperationException("MidFD管理ゴミ箱の復元情報が不完全です。");
            }

            var destinationSw = Stopwatch.StartNew();
            try
            {
            if (PathExists(item.BeforePath))
                {
                    throw new IOException($"復元先に同名項目があるため復元できません: {item.BeforePath}");
                }
            }
            finally
            {
                destinationSw.Stop();
                diagnostics?.RecordStage("DestinationCheck", destinationSw.ElapsedMilliseconds);
            }

            TrashManifestRecord record;
            var lookupSw = Stopwatch.StartNew();
            try
            {
                record = restoreLookup?.Require(item.RecycleBinPath)
                    ?? RequireManifestRecord(item.RecycleBinPath, diagnostics);
            }
            finally
            {
                lookupSw.Stop();
                diagnostics?.RecordStage("ManifestLookup", lookupSw.ElapsedMilliseconds);
            }

            string recycleBinPath;
            var recordValidationSw = Stopwatch.StartNew();
            try
            {
                recycleBinPath = _pathValidator.ValidateRecord(record);
            }
            finally
            {
                recordValidationSw.Stop();
                diagnostics?.RecordStage("PathValidation", recordValidationSw.ElapsedMilliseconds);
            }

            if (!PathExists(recycleBinPath))
            {
                throw new FileNotFoundException("MidFD管理ゴミ箱内の項目が見つかりません。", item.RecycleBinPath);
            }

            var directorySw = Stopwatch.StartNew();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(item.BeforePath) ?? string.Empty);
            }
            finally
            {
                directorySw.Stop();
                diagnostics?.RecordStage("DirectoryPrepare", directorySw.ElapsedMilliseconds);
            }

            _pathValidator.ValidatePath(recycleBinPath);
            var moveSw = Stopwatch.StartNew();
            try
            {
                FileOperationService.Move(recycleBinPath, item.BeforePath, suppressLogging: suppressLogging);
            }
            finally
            {
                moveSw.Stop();
                diagnostics?.RecordStage("PhysicalMove", moveSw.ElapsedMilliseconds);
            }
            long fileMoveMs = moveSw.ElapsedMilliseconds;
            _lastManifestFileMoveMs += fileMoveMs;
            _totalFileMoveMs += fileMoveMs; // Also accumulate to total investigation metric
            if (suppressLogging) _suppressedSuccessCount++;

            if (!skipStatusUpdate)
            {
                var statusSw = Stopwatch.StartNew();
                bool statusUpdated = false;
                try
                {
                    if (!UpdateRecordStatus(item.RecycleBinPath, TrashRecordStatus.Restored))
                    {
                        throw new IOException($"管理ゴミ箱recordのstatus永続化に失敗しました: {item.RecycleBinPath}");
                    }
                    statusUpdated = true;
                }
                finally
                {
                    statusSw.Stop();
                    diagnostics?.RecordStage("StatusUpdate", statusSw.ElapsedMilliseconds);
                    if (statusUpdated)
                    {
                        _lastManifestStatusUpdateMs += statusSw.ElapsedMilliseconds;
                    }
                }
            }

            if (!suppressLogging)
            {
                LogService.Info($"[MidFdTrash] Restored managed trash item. trash={item.RecycleBinPath}, original={item.BeforePath}");
            }
        }
        finally
        {
            mutation?.Dispose();
            restoreSw.Stop();
            diagnostics?.RecordRestore(restoreSw.ElapsedMilliseconds);
        }
    }

    internal static FileOperationUndoRedoItem RedoDeleteToTrash(FileOperationUndoRedoItem item, out TrashManifestRecord? outRecord, bool skipRegistration = false, bool suppressLogging = false)
    {
        using IDisposable mutation = EnterMutation();
        outRecord = null;
        if (string.IsNullOrWhiteSpace(item.BeforePath))
        {
            throw new InvalidOperationException("MidFD管理ゴミ箱の再削除情報が不完全です。");
        }

        if (!PathExists(item.BeforePath))
        {
            throw new FileNotFoundException("再削除対象が見つかりません。", item.BeforePath);
        }

        var lookupSw = Stopwatch.StartNew();
        bool hasExistingRecord = TryGetRecordByOriginalPath(item.BeforePath, out TrashManifestRecord? existing);
        lookupSw.Stop();
        _lastManifestLookupMs += lookupSw.ElapsedMilliseconds;

        string batchId = hasExistingRecord && existing != null
            ? existing.BatchId
            : CreateBatchId();
        int itemIndex = TryParseItemIndex(existing?.ItemId, out int parsedIndex) ? parsedIndex : 1;

        return MoveToTrash(item.BeforePath, batchId, itemIndex, skipRegistration, out outRecord, suppressLogging: suppressLogging);
    }

    public static Task RunRetentionCleanupAsync(
        Configuration.AppSettings? settings,
        FileOperationUndoRedoService? undoRedoService,
        string trigger)
        => RunRetentionCleanupAsyncCore(
            settings,
            undoRedoService,
            trigger,
            maintenanceMutationEnteredForTest: null,
            maintenanceMutationLockAttemptingForTest: null,
            maintenanceMutationAcquisitionFailedForTest: null);

    internal static Task RunRetentionCleanupAsyncForTest(
        Configuration.AppSettings settings,
        FileOperationUndoRedoService? undoRedoService,
        string trigger,
        Action maintenanceMutationEntered,
        Action? maintenanceMutationLockAttempting = null,
        Action? maintenanceMutationAcquisitionFailed = null)
    {
        ArgumentNullException.ThrowIfNull(maintenanceMutationEntered);
        return RunRetentionCleanupAsyncCore(
            settings,
            undoRedoService,
            trigger,
            maintenanceMutationEntered,
            maintenanceMutationLockAttempting,
            maintenanceMutationAcquisitionFailed);
    }

    internal static void ResetRetentionCleanupStateForTest()
    {
        _lastRetentionCleanupStartedUtc = DateTime.MinValue;
        Interlocked.Exchange(ref _maintenanceMutationActive, 0);
        Interlocked.Exchange(ref _foregroundMutationWaiters, 0);
    }

    internal static int GetForegroundMutationWaiterCountForTest() =>
        Volatile.Read(ref _foregroundMutationWaiters);

    internal static IDisposable EnterMutationForTest() => EnterMutation();

    private static Task RunRetentionCleanupAsyncCore(
        Configuration.AppSettings? settings,
        FileOperationUndoRedoService? undoRedoService,
        string trigger,
        Action? maintenanceMutationEnteredForTest,
        Action? maintenanceMutationLockAttemptingForTest,
        Action? maintenanceMutationAcquisitionFailedForTest)
    {
        if (settings?.FileOperations == null || !settings.FileOperations.ManagedTrashAutoHandoffEnabled)
        {
            return Task.CompletedTask;
        }

        int retentionDays = settings.FileOperations.ManagedTrashUndoRetentionDays;
        if (retentionDays <= 0)
        {
            return Task.CompletedTask;
        }

        int clampedDays = Math.Clamp(retentionDays, 1, 365);
        return Task.Run(() =>
        {
            try
            {
                RunRetentionCleanupCore(
                    clampedDays,
                    undoRedoService,
                    trigger,
                    maintenanceMutationEnteredForTest,
                    maintenanceMutationLockAttemptingForTest,
                    maintenanceMutationAcquisitionFailedForTest);
            }
            catch (Exception ex)
            {
                LogService.Warn($"[MidFdTrashCleanup] Maintenance failed without affecting foreground operation. trigger={trigger}, error={ex.Message}");
            }
        });
    }

    public static void EmptyTrash()
    {
        using IDisposable mutation = EnterMutation();
        TrashManifest manifest = _activeBatchManifest ?? LoadManifest();
        int deleted = 0;
        int cleaned = 0;
        int pruned = 0;

        foreach (TrashManifestRecord record in manifest.Records.ToList())
        {
            try
            {
                string validatedTrashPath = _pathValidator.ValidateRecord(record);
                TryGetItemsRootForTrashPath(record.TrashPath, out string? itemsRoot);
                if (PathExists(validatedTrashPath))
                {
                    FileOperationService.Delete(validatedTrashPath);
                    deleted++;
                }
                else
                {
                    cleaned++;
                    LogService.Warn($"[MidFdTrash] Missing item was preserved for explicit missing-record cleanup. trash={record.TrashPath}");
                    continue;
                }

                manifest.Records.Remove(record);
                if (!string.IsNullOrWhiteSpace(itemsRoot))
                {
                    pruned += PruneEmptyParents(record.TrashPath, itemsRoot);
                }
            }
            catch (Exception ex)
            {
                LogService.Warn($"[MidFdTrash] Failed to empty item. trash={record.TrashPath}, error={ex.Message}");
            }
        }

        if (_activeBatchManifest == null)
        {
            SaveManifest(manifest);
        }

        LogService.Info(
            $"[MidFdTrash] Empty completed. deleted={deleted}, cleaned={cleaned}, " +
            $"pruned={pruned}, remaining={manifest.Records.Count}");
    }

    private static void RunRetentionCleanupCore(
        int retentionDays,
        FileOperationUndoRedoService? undoRedoService,
        string trigger,
        Action? maintenanceMutationEnteredForTest = null,
        Action? maintenanceMutationLockAttemptingForTest = null,
        Action? maintenanceMutationAcquisitionFailedForTest = null)
    {
        if (!RetentionCleanupGate.Wait(0))
        {
            LogService.Info($"[MidFdTrashCleanup] Skipped because cleanup is already running. trigger={trigger}");
            return;
        }

        try
        {
            DateTime startedUtc = DateTime.UtcNow;
            IDisposable? initialMutation = TryEnterMaintenanceMutation(
                maintenanceMutationLockAttemptingForTest,
                maintenanceMutationAcquisitionFailedForTest);
            if (initialMutation == null)
            {
                LogService.Info($"[MidFdTrashCleanup] Skipped because a foreground mutation is running. trigger={trigger}");
                return;
            }

            using (initialMutation)
            {
                if (_lastRetentionCleanupStartedUtc != DateTime.MinValue &&
                    startedUtc - _lastRetentionCleanupStartedUtc < RetentionCleanupThrottle)
                {
                    LogService.Info($"[MidFdTrashCleanup] Skipped because throttled. trigger={trigger}");
                    return;
                }

                _lastRetentionCleanupStartedUtc = startedUtc;
                maintenanceMutationEnteredForTest?.Invoke();
            }

            int deletedCount = 0;
            int cleanedMissingCount = 0;
            int emptyPruned = 0;
            int failedCount = 0;
            int? remainingCount = null;
            bool yieldedToForeground = false;
            var examinedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var missingSamples = new List<string>(RetentionCleanupSampleLimit);
            var failedSamples = new List<string>(RetentionCleanupSampleLimit);

            while (true)
            {
                if (Volatile.Read(ref _foregroundMutationWaiters) != 0)
                {
                    yieldedToForeground = true;
                    break;
                }

                IDisposable? mutation = TryEnterMaintenanceMutation(
                    maintenanceMutationLockAttemptingForTest,
                    maintenanceMutationAcquisitionFailedForTest);
                if (mutation == null)
                {
                    yieldedToForeground = true;
                    break;
                }

                bool chunkCompleted = false;
                using (mutation)
                {
                    if (Volatile.Read(ref _foregroundMutationWaiters) != 0)
                    {
                        yieldedToForeground = true;
                    }
                    else
                    {
                        TrashManifest manifest = LoadManifest();
                        remainingCount = manifest.Records.Count;
                        List<TrashManifestRecord> candidates = manifest.Records
                            .Where(record =>
                                IsExpiredManagedTrashRecord(record, startedUtc, retentionDays) &&
                                examinedPaths.Add(GetRetentionCleanupRecordKey(record)))
                            .Take(RetentionCleanupChunkSize)
                            .ToList();

                        if (candidates.Count == 0)
                        {
                            chunkCompleted = true;
                        }
                        else
                        {
                            var pathsToRemoveFromManifest = new List<string>();
                            foreach (TrashManifestRecord record in candidates)
                            {
                                if (Volatile.Read(ref _foregroundMutationWaiters) != 0)
                                {
                                    yieldedToForeground = true;
                                    break;
                                }

                                if (!IsRecordAvailableForRestore(record))
                                {
                                    cleanedMissingCount++;
                                    AddRetentionCleanupSample(missingSamples, record.TrashPath);
                                    continue;
                                }

                                try
                                {
                                    string path = _pathValidator.ValidateRecord(record);
                                    FileOperationService.Delete(path);
                                    deletedCount++;
                                    pathsToRemoveFromManifest.Add(path);

                                    if (TryGetItemsRootForTrashPath(path, out string? itemsRoot) && !string.IsNullOrEmpty(itemsRoot))
                                    {
                                        emptyPruned += PruneEmptyParents(path, itemsRoot);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    failedCount++;
                                    AddRetentionCleanupSample(failedSamples, $"{record.TrashPath} ({ex.Message})");
                                }
                            }

                            if (pathsToRemoveFromManifest.Count > 0)
                            {
                                var pathsSet = new HashSet<string>(pathsToRemoveFromManifest, StringComparer.OrdinalIgnoreCase);
                                int removed = RequireManifestStore().RemoveRecordsByTrashPaths(manifest, pathsSet);
                                if (removed > 0)
                                {
                                    undoRedoService?.PruneTrashDeleteItemsByRecycleBinPaths(pathsSet);
                                }

                                if (_activeBatchManifest == null)
                                {
                                    SaveManifest(manifest);
                                }
                            }

                            remainingCount = manifest.Records.Count;
                        }
                    }
                }

                if (yieldedToForeground || chunkCompleted)
                {
                    break;
                }
            }

            if (cleanedMissingCount > 0)
            {
                LogService.Warn(
                    $"[MidFdTrashCleanup] Missing or invalid expired items were preserved for explicit cleanup. " +
                    $"count={cleanedMissingCount}, sampleCount={missingSamples.Count}, samples={string.Join(" | ", missingSamples)}");
            }

            if (failedCount > 0)
            {
                LogService.Warn(
                    $"[MidFdTrashCleanup] Failed to delete expired items. count={failedCount}, " +
                    $"sampleCount={failedSamples.Count}, samples={string.Join(" | ", failedSamples)}");
            }

            LogService.Info(
                $"[MidFdTrashCleanup] Completed. trigger={trigger}, retentionDays={retentionDays}, " +
                $"deleted={deletedCount}, missingOrInvalidPreserved={cleanedMissingCount}, " +
                $"emptyContainersPruned={emptyPruned}, failed={failedCount}, " +
                $"remaining={(remainingCount?.ToString() ?? "unknown")}, yieldedToForeground={yieldedToForeground}");
        }
        finally
        {
            RetentionCleanupGate.Release();
        }
    }

    private static string GetRetentionCleanupRecordKey(TrashManifestRecord record)
    {
        try
        {
            return Path.GetFullPath(record.TrashPath);
        }
        catch
        {
            return record.TrashPath ?? string.Empty;
        }
    }

    private static void AddRetentionCleanupSample(ICollection<string> samples, string? value)
    {
        if (samples.Count >= RetentionCleanupSampleLimit || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        samples.Add(value);
    }

    private static bool IsExpiredManagedTrashRecord(TrashManifestRecord record, DateTime nowUtc, int retentionDays)
    {
        if (record.Status != TrashRecordStatus.InTrash)
        {
            return false;
        }

        if (record.DeletedAtUtc == default || record.DeletedAtUtc == DateTime.MinValue)
        {
            return false;
        }

        if (record.DeletedAtUtc > nowUtc)
        {
            return false;
        }

        return nowUtc - record.DeletedAtUtc >= TimeSpan.FromDays(Math.Clamp(retentionDays, 1, 365));
    }

    internal static bool PathExists(string path)
    {
        return ReparsePointHelper.Exists(path) || File.Exists(path) || Directory.Exists(path);
    }

    internal static bool IsRecordAvailableForRestore(TrashManifestRecord record) =>
        IsRecordAvailableForRestore(record, _pathValidator);

    internal static ManagedTrashRecordView GetRecordView(TrashManifestRecord record) =>
        ManagedTrashRecordAvailabilityService.Evaluate(record, _pathValidator);

    internal static bool IsRecordAvailableForRestore(TrashManifestRecord record, ManagedTrashPathValidator pathValidator)
    {
        return ManagedTrashRecordAvailabilityService.Evaluate(record, pathValidator).CanRestore;
    }

    public static void DeleteFromTrashForever(string trashPath, FileOperationUndoRedoService? undoRedoService)
    {
        using IDisposable mutation = EnterMutation();
        if (string.IsNullOrWhiteSpace(trashPath)) return;
        TrashManifestRecord record = RequireManifestRecord(trashPath);
        trashPath = _pathValidator.ValidateRecord(record);

        if (!PathExists(trashPath))
        {
            throw new FileNotFoundException("物理itemが存在しません。欠損レコード掃除を使用してください。", trashPath);
        }

        if (PathExists(trashPath))
        {
            FileOperationService.Delete(trashPath);
        }

        TrashManifest manifest = LoadManifest();
        var pathsSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { trashPath };
        int removed = RequireManifestStore().RemoveRecordsByTrashPaths(manifest, pathsSet);
        if (removed > 0)
        {
            SaveManifest(manifest);
            undoRedoService?.PruneTrashDeleteItemsByRecycleBinPaths(pathsSet);
        }

        if (TryGetItemsRootForTrashPath(trashPath, out string? itemsRoot) && !string.IsNullOrEmpty(itemsRoot))
        {
            PruneEmptyParents(trashPath, itemsRoot);
        }
    }

    public static int CleanMissingTrashRecords(FileOperationUndoRedoService? undoRedoService)
    {
        using IDisposable mutation = EnterMutation();
        TrashManifest manifest = LoadManifest();
        var missingPaths = new List<string>();
        int prunedContainers = 0;

        foreach (var record in manifest.Records)
        {
            try
            {
                string validatedPath = _pathValidator.ValidateRecord(record);
                if (record.Status == TrashRecordStatus.InTrash && !PathExists(validatedPath))
                {
                    missingPaths.Add(validatedPath);
                }
            }
            catch (Exception ex)
            {
                LogService.Warn($"[MidFdTrash] Skipped invalid manifest record during missing-record cleanup. error={ex.Message}");
            }
        }

        if (missingPaths.Count > 0)
        {
            var pathsSet = new HashSet<string>(missingPaths, StringComparer.OrdinalIgnoreCase);
            int removed = RequireManifestStore().RemoveRecordsByTrashPaths(manifest, pathsSet);
            if (removed > 0)
            {
                SaveManifest(manifest);
                undoRedoService?.PruneTrashDeleteItemsByRecycleBinPaths(pathsSet);
            }

            foreach (string path in missingPaths)
            {
                if (TryGetItemsRootForTrashPath(path, out string? itemsRoot) && !string.IsNullOrEmpty(itemsRoot))
                {
                    prunedContainers += PruneEmptyParents(path, itemsRoot);
                }
            }
        }

        return missingPaths.Count;
    }

    public static string CreateBatchId()
    {
        return DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..8];
    }

    internal static string ResolveTrashRootPath(string originalPath)
    {
        string fullPath = Path.GetFullPath(originalPath);
        string? sourceRoot = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(sourceRoot))
        {
            throw new InvalidOperationException("削除元のvolume/share rootを解決できません。");
        }

        return Path.GetFullPath(Path.Combine(sourceRoot, TrashDirectoryName));
    }

    private static string ResolveTrashRoot(string originalPath)
    {
        string trashRoot = ResolveTrashRootPath(originalPath);
        string itemsRoot = Path.Combine(trashRoot, ItemsDirectoryName);
        _pathValidator.ValidatePath(Path.Combine(itemsRoot, ".root-validation"));
        Directory.CreateDirectory(itemsRoot);
        TryHideDirectory(trashRoot);
        return trashRoot;
    }

    private static string BuildUniqueTrashPath(string root, string batchId, string itemId, string originalName)
    {
        string safeName = SanitizeTrashVisibleName(originalName);
        string directory = Path.Combine(root, ItemsDirectoryName, batchId);
        string itemName = $"{itemId}_{safeName}";
        string candidate = Path.Combine(directory, itemName);
        int suffix = 1;
        while (PathExists(candidate))
        {
            candidate = Path.Combine(directory, $"{itemName}.{suffix}");
            suffix++;
        }

        return candidate;
    }

    private static string SanitizeTrashVisibleName(string originalName)
    {
        string safeName = string.IsNullOrWhiteSpace(originalName) ? "item" : originalName.Trim();
        foreach (char invalidChar in Path.GetInvalidFileNameChars())
        {
            safeName = safeName.Replace(invalidChar, '_');
        }

        safeName = safeName.TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = "item";
        }

        if (safeName.Length <= MaxVisibleOriginalNameLength)
        {
            return safeName;
        }

        string extension = Path.GetExtension(safeName);
        int baseLength = MaxVisibleOriginalNameLength - extension.Length;
        if (baseLength < 16)
        {
            return safeName[..MaxVisibleOriginalNameLength];
        }

        string nameWithoutExtension = Path.GetFileNameWithoutExtension(safeName);
        return nameWithoutExtension[..Math.Min(nameWithoutExtension.Length, baseLength)] + extension;
    }

    private static string ManifestPath => _manifestPath;

    private static bool TryGetItemsRootForTrashPath(string trashPath, out string? itemsRoot)
    {
        itemsRoot = null;
        if (string.IsNullOrWhiteSpace(trashPath))
        {
            return false;
        }

        return _pathValidator.TryResolveItemsRoot(trashPath, out itemsRoot) &&
               !string.IsNullOrWhiteSpace(itemsRoot) &&
               _pathValidator.IsSafeItemsRoot(itemsRoot);
    }

    private static int PruneEmptyParents(string trashPath, string itemsRoot)
    {
        if (!IsSafeItemsRoot(itemsRoot))
        {
            return 0;
        }

        int pruned = 0;
        string normalizedItemsRoot = Path.GetFullPath(itemsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? current = Path.GetDirectoryName(Path.GetFullPath(trashPath));
        while (!string.IsNullOrWhiteSpace(current) &&
               IsPathWithinOrEqual(current, normalizedItemsRoot) &&
               !string.Equals(
                   current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                   normalizedItemsRoot,
                   StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(current) || Directory.EnumerateFileSystemEntries(current).Any())
                {
                    break;
                }

                Directory.Delete(current, recursive: false);
                pruned++;
                LogService.Info($"[MidFdTrash] Pruned empty trash directory. path={current}");
            }
            catch (Exception ex)
            {
                LogService.Warn($"[MidFdTrash] Failed to prune empty trash directory. path={current}, error={ex.Message}");
                break;
            }

            current = Path.GetDirectoryName(current);
        }

        return pruned;
    }

    private static bool IsSafeItemsRoot(string itemsRoot)
    {
        return _pathValidator.IsSafeItemsRoot(itemsRoot);
    }

    private static bool IsPathWithinOrEqual(string path, string parent)
    {
        string normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(normalizedPath, normalizedParent, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(normalizedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static TrashManifestRecord RequireManifestRecord(
        string trashPath,
        ManagedTrashRestoreDiagnostics? diagnostics = null)
    {
        string normalizedPath = Path.GetFullPath(trashPath);
        TrashManifest manifest;
        if (_activeBatchManifest != null)
        {
            manifest = _activeBatchManifest;
        }
        else
        {
            var manifestLoadSw = Stopwatch.StartNew();
            try
            {
                manifest = LoadManifest();
            }
            finally
            {
                manifestLoadSw.Stop();
                diagnostics?.RecordStage("ManifestLoad", manifestLoadSw.ElapsedMilliseconds);
            }
        }
        diagnostics?.SetManifestRecordCount(manifest.Records.Count);
        var matches = new List<TrashManifestRecord>();
        var manifestScanSw = Stopwatch.StartNew();
        try
        {
            foreach (TrashManifestRecord record in manifest.Records)
            {
                var recordValidationSw = Stopwatch.StartNew();
                try
                {
                    try
                    {
                        string validatedPath = _pathValidator.ValidateRecord(record);
                        if (string.Equals(validatedPath, normalizedPath, StringComparison.OrdinalIgnoreCase)) matches.Add(record);
                    }
                    catch
                    {
                        // Invalid metadata is not a candidate for a physical operation.
                    }
                }
                finally
                {
                    recordValidationSw.Stop();
                    diagnostics?.RecordStage("RecordValidation", recordValidationSw.ElapsedMilliseconds);
                }
            }
        }
        finally
        {
            manifestScanSw.Stop();
            diagnostics?.RecordManifestScan(manifestScanSw.ElapsedMilliseconds, manifest.Records.Count);
        }
        if (matches.Count != 1)
        {
            throw new InvalidOperationException("管理ゴミ箱manifest recordのidentityが一致しません。");
        }
        return matches[0];
    }

    private static IDisposable EnterMutation()
    {
        ThrowIfManagedTrashUnavailable();
        bool waitingForMaintenance = false;
        DateTime foregroundWaitDeadlineUtc = DateTime.MinValue;
        while (true)
        {
            if (Volatile.Read(ref _maintenanceMutationActive) != 0)
            {
                if (!waitingForMaintenance)
                {
                    Interlocked.Increment(ref _foregroundMutationWaiters);
                    waitingForMaintenance = true;
                    foregroundWaitDeadlineUtc = DateTime.UtcNow + ForegroundMutationWaitTimeout;
                }

                if (Monitor.TryEnter(MutationSync, ForegroundMutationWaitMilliseconds))
                {
                    break;
                }

                if (DateTime.UtcNow >= foregroundWaitDeadlineUtc)
                {
                    Interlocked.Decrement(ref _foregroundMutationWaiters);
                    throw new InvalidOperationException("別の管理ゴミ箱操作を実行中です。完了後に再試行してください。");
                }

                Thread.Sleep(ForegroundMutationWaitMilliseconds);
                continue;
            }

            if (Monitor.TryEnter(MutationSync))
            {
                break;
            }

            if (Volatile.Read(ref _maintenanceMutationActive) != 0)
            {
                continue;
            }

            if (waitingForMaintenance)
            {
                if (DateTime.UtcNow < foregroundWaitDeadlineUtc)
                {
                    if (Monitor.TryEnter(MutationSync, ForegroundMutationWaitMilliseconds))
                    {
                        break;
                    }

                    Thread.Sleep(ForegroundMutationWaitMilliseconds);
                    continue;
                }

                Interlocked.Decrement(ref _foregroundMutationWaiters);
            }

            throw new InvalidOperationException("別の管理ゴミ箱操作を実行中です。完了後に再試行してください。");
        }

        if (waitingForMaintenance)
        {
            Interlocked.Decrement(ref _foregroundMutationWaiters);
        }

        if (_activeMutationBatchId != null && MutationBatchContext.Value != _activeMutationBatchId)
        {
            Monitor.Exit(MutationSync);
            throw new InvalidOperationException("別の管理ゴミ箱batchを実行中です。完了後に再試行してください。");
        }

        return new MutationLease(maintenance: false);
    }

    private static IDisposable? TryEnterMaintenanceMutation(
        Action? maintenanceMutationLockAttemptingForTest = null,
        Action? maintenanceMutationAcquisitionFailedForTest = null)
    {
        ThrowIfManagedTrashUnavailable();
        if (Interlocked.CompareExchange(ref _maintenanceMutationActive, 1, 0) != 0)
        {
            return null;
        }

        bool entered = false;
        try
        {
            if (Volatile.Read(ref _foregroundMutationWaiters) != 0)
            {
                return null;
            }

            maintenanceMutationLockAttemptingForTest?.Invoke();
            if (!Monitor.TryEnter(MutationSync))
            {
                return null;
            }

            entered = true;
            if (Volatile.Read(ref _foregroundMutationWaiters) != 0 || _activeMutationBatchId != null)
            {
                Monitor.Exit(MutationSync);
                entered = false;
                return null;
            }

            return new MutationLease(maintenance: true);
        }
        finally
        {
            if (!entered)
            {
                Volatile.Write(ref _maintenanceMutationActive, 0);
                maintenanceMutationAcquisitionFailedForTest?.Invoke();
            }
        }
    }

    private static void ThrowIfManagedTrashUnavailable()
    {
        if (!IsAvailable) throw new InvalidOperationException(AvailabilityMessage);
    }

    private static ITrashManifestStore RequireManifestStore()
    {
        lock (StartupSync)
        {
            return ManifestStore ?? throw new InvalidOperationException(AvailabilityMessage);
        }
    }

    private sealed class MutationLease : IDisposable
    {
        private readonly bool _maintenance;
        private bool _disposed;

        public MutationLease(bool maintenance)
        {
            _maintenance = maintenance;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Monitor.Exit(MutationSync);
            if (_maintenance)
            {
                Volatile.Write(ref _maintenanceMutationActive, 0);
            }
        }
    }

    internal static TrashManifest LoadManifest()
    {
        ThrowIfManagedTrashUnavailable();
        return RequireManifestStore().Load();
    }

    private static void SaveManifest(TrashManifest manifest)
    {
        RequireManifestStore().Save(manifest);
    }

    private static void RegisterNewTrashRecord(TrashManifestRecord record)
    {
        if (_activeBatchManifest != null)
        {
            var appendSw = Stopwatch.StartNew();
            RequireManifestStore().RegisterNewRecord(_activeBatchManifest, record);
            appendSw.Stop();

            _manifestAppendCount++;
            _manifestAppendMs += appendSw.ElapsedMilliseconds;
            _lastManifestStatusUpdateMs += appendSw.ElapsedMilliseconds; // Track as status update ms for Undo/Redo context
            _manifestRecordCountAfter = _activeBatchManifest.Records.Count;
            return;
        }

        UpsertRecord(record);
    }

    internal static void RegisterNewTrashRecordsPublic(IEnumerable<TrashManifestRecord> records)
    {
        using IDisposable mutation = EnterMutation();
        RegisterNewTrashRecords(records);
    }

    private static void RegisterNewTrashRecords(IEnumerable<TrashManifestRecord> records)
    {
        List<TrashManifestRecord> recordList = records.ToList();
        if (recordList.Count == 0)
        {
            return;
        }

        TrashManifest manifest = _activeBatchManifest ?? LoadManifest();
        var appendSw = Stopwatch.StartNew();
        RequireManifestStore().RegisterNewRecords(manifest, recordList);
        appendSw.Stop();

        _manifestAppendCount += recordList.Count;
        _manifestAppendMs += appendSw.ElapsedMilliseconds;
        _manifestRecordBatchMs += appendSw.ElapsedMilliseconds;
        _manifestRecordBatchCount += recordList.Count;
        _manifestRecordBatchFlushCount++;
        _lastManifestStatusUpdateMs += appendSw.ElapsedMilliseconds;
        _manifestRecordCountAfter = manifest.Records.Count;

        if (_activeBatchManifest == null)
        {
            // Already saved by ManifestStore in SQLite mode, but JSON needs Save.
            SaveManifest(manifest);
        }
    }

    private static void UpsertRecord(TrashManifestRecord record)
    {
        TrashManifest manifest = _activeBatchManifest ?? LoadManifest();
        var sw = Stopwatch.StartNew();
        _manifestUpsertScanCount += RequireManifestStore().UpsertRecord(manifest, record);
        sw.Stop();
        _manifestRecordCountAfter = manifest.Records.Count;
        _lastManifestStatusUpdateMs += sw.ElapsedMilliseconds;

        if (_activeBatchManifest == null)
        {
            var saveSw = Stopwatch.StartNew();
            SaveManifest(manifest);
            saveSw.Stop();
            _manifestAppendMs += saveSw.ElapsedMilliseconds;
        }

    }

    private static bool UpdateRecordStatus(string trashPath, TrashRecordStatus status)
    {
        TrashManifest manifest = _activeBatchManifest ?? LoadManifest();
        var sw = Stopwatch.StartNew();
        bool success = RequireManifestStore().UpdateRecordStatus(manifest, trashPath, status);
        sw.Stop();
        _lastManifestStatusUpdateMs += sw.ElapsedMilliseconds;

        if (!success)
        {
            return false;
        }

        if (_activeBatchManifest == null)
        {
            var saveSw = Stopwatch.StartNew();
            SaveManifest(manifest);
            saveSw.Stop();
            _manifestAppendMs += saveSw.ElapsedMilliseconds;
        }

        return true;
    }

    internal static int UpdateRecordStatuses(IEnumerable<string> trashPaths, TrashRecordStatus status)
    {
        using IDisposable mutation = EnterMutation();
        List<string> pathList = trashPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (pathList.Count == 0)
        {
            return 0;
        }

        TrashManifest manifest = _activeBatchManifest ?? LoadManifest();
        var sw = Stopwatch.StartNew();
        int updated = RequireManifestStore().UpdateRecordStatuses(manifest, pathList, status);
        sw.Stop();
        _lastManifestStatusUpdateMs += sw.ElapsedMilliseconds;

        if (updated > 0 && _activeBatchManifest == null)
        {
            var saveSw = Stopwatch.StartNew();
            SaveManifest(manifest);
            saveSw.Stop();
            _manifestAppendMs += saveSw.ElapsedMilliseconds;
        }

        return updated;
    }

    private static bool TryGetRecordByOriginalPath(string originalPath, out TrashManifestRecord? record)
    {
        TrashManifest manifest = _activeBatchManifest ?? LoadManifest();
        return RequireManifestStore().TryGetRecordByOriginalPath(manifest, originalPath, out record);
    }

    private static bool TryParseItemIndex(string? itemId, out int index)
    {
        return int.TryParse(itemId, out index) && index > 0;
    }

    private static void TryHideDirectory(string directory)
    {
        try
        {
            File.SetAttributes(directory, File.GetAttributes(directory) | FileAttributes.Hidden);
        }
        catch (Exception ex)
        {
            LogService.Warn($"[MidFdTrash] Could not hide trash directory. path={directory}, error={ex.Message}");
        }
    }

    public readonly record struct ManifestOperationDiagnostics(
        int AppendCount,
        long UpsertScanCount,
        long AppendMs,
        int RecordCountBefore,
        int RecordCountAfter,
        bool AppendMode,
        int RecordBatchCount,
        int RecordBatchFlushCount,
        int ActiveBatchSaveCount,
        long RecordBatchMs,
        long DbConnectionOpenMs,
        long DbTransactionBeginMs,
        long DbDeleteLoopMs,
        long DbInsertLoopMs,
        long DbCommitMs,
        long TotalFileMoveMs,
        int CrossVolumeMoveCount,
        int SameVolumeMoveCount,
        int AppDataFallbackMoveCount);
}
