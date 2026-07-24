using System.Text.Json;
using System.Diagnostics;
using MidFD.Models;
using MidFD.Services.TrashManifestStore;
using MidFD.Configuration.Storage;

namespace MidFD.Services;

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
        _activeMutationBatchId = Guid.NewGuid();
        MutationBatchContext.Value = _activeMutationBatchId;
        _activeBatchManifest = LoadManifest();
        _manifestRecordCountBefore = _activeBatchManifest.Records.Count;
        _manifestRecordCountAfter = _activeBatchManifest.Records.Count;
        _manifestAppendCount = 0;
        _manifestAppendMs = 0;
        _manifestUpsertScanCount = 0;
        _manifestAppendMode = true;
    }

    public static void FlushManifestBatch()
    {
        using IDisposable mutation = EnterMutation();
        if (_activeBatchManifest != null)
        {
            SaveManifest(_activeBatchManifest);
            _manifestRecordCountAfter = _activeBatchManifest.Records.Count;
            _activeBatchManifest = null;
        }
        _activeMutationBatchId = null;
        MutationBatchContext.Value = null;
    }

    public static void SaveActiveBatch()
    {
        using IDisposable mutation = EnterMutation();
        if (_activeBatchManifest != null)
        {
            SaveManifest(_activeBatchManifest);
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

        if (!File.Exists(originalPath) && !Directory.Exists(originalPath))
        {
            throw new FileNotFoundException("削除対象が見つかりません。", originalPath);
        }

        var totalSw = Stopwatch.StartNew();

        bool isDirectory = Directory.Exists(originalPath);
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
        bool sourceExists = File.Exists(originalPath) || Directory.Exists(originalPath);
        bool trashExists = File.Exists(trashPath) || Directory.Exists(trashPath);

        if (sourceExists || !trashExists)
        {
            string sourceDetail = sourceExists
                ? (Directory.Exists(originalPath) ? "DIR" : $"FILE({new FileInfo(originalPath).Length} bytes)")
                : "NONE";
            string trashDetail = trashExists
                ? (Directory.Exists(trashPath) ? "DIR" : $"FILE({new FileInfo(trashPath).Length} bytes)")
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
            Size = isDirectory ? 0 : new FileInfo(trashPath).Length,
            LastWriteTimeUtc = isDirectory
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
        using IDisposable mutation = EnterMutation();
        if (string.IsNullOrWhiteSpace(item.BeforePath) || string.IsNullOrWhiteSpace(item.RecycleBinPath))
        {
            throw new InvalidOperationException("MidFD管理ゴミ箱の復元情報が不完全です。");
        }

        if (File.Exists(item.BeforePath) || Directory.Exists(item.BeforePath))
        {
            throw new IOException($"復元先に同名項目があるため復元できません: {item.BeforePath}");
        }

        TrashManifestRecord record = RequireManifestRecord(item.RecycleBinPath);
        string recycleBinPath = _pathValidator.ValidateRecord(record);
        if (!File.Exists(recycleBinPath) && !Directory.Exists(recycleBinPath))
        {
            throw new FileNotFoundException("MidFD管理ゴミ箱内の項目が見つかりません。", item.RecycleBinPath);
        }

        var moveSw = Stopwatch.StartNew();
        Directory.CreateDirectory(Path.GetDirectoryName(item.BeforePath) ?? string.Empty);
        _pathValidator.ValidatePath(recycleBinPath);
        FileOperationService.Move(recycleBinPath, item.BeforePath, suppressLogging: suppressLogging);
        moveSw.Stop();
        long fileMoveMs = moveSw.ElapsedMilliseconds;
        _lastManifestFileMoveMs += fileMoveMs;
        _totalFileMoveMs += fileMoveMs; // Also accumulate to total investigation metric
        if (suppressLogging) _suppressedSuccessCount++;

        if (!skipStatusUpdate)
        {
            var statusSw = Stopwatch.StartNew();
            UpdateRecordStatus(item.RecycleBinPath, TrashRecordStatus.Restored);
            statusSw.Stop();
            _lastManifestStatusUpdateMs += statusSw.ElapsedMilliseconds;
        }

        if (!suppressLogging)
        {
            LogService.Info($"[MidFdTrash] Restored managed trash item. trash={item.RecycleBinPath}, original={item.BeforePath}");
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

        if (!File.Exists(item.BeforePath) && !Directory.Exists(item.BeforePath))
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
        return Task.Run(() => RunRetentionCleanupCore(clampedDays, undoRedoService, trigger));
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
                if (Directory.Exists(validatedTrashPath))
                {
                    FileOperationService.Delete(validatedTrashPath);
                    deleted++;
                }
                else if (File.Exists(validatedTrashPath))
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

    private static void RunRetentionCleanupCore(int retentionDays, FileOperationUndoRedoService? undoRedoService, string trigger)
    {
        using IDisposable mutation = EnterMutation();
        if (!RetentionCleanupGate.Wait(0))
        {
            LogService.Info($"[MidFdTrashCleanup] Skipped because cleanup is already running. trigger={trigger}");
            return;
        }

        try
        {
            DateTime startedUtc = DateTime.UtcNow;
            if (_lastRetentionCleanupStartedUtc != DateTime.MinValue &&
                startedUtc - _lastRetentionCleanupStartedUtc < RetentionCleanupThrottle)
            {
                LogService.Info($"[MidFdTrashCleanup] Skipped because throttled. trigger={trigger}");
                return;
            }

            _lastRetentionCleanupStartedUtc = startedUtc;

            TrashManifest manifest = LoadManifest();

            // Part 1: Collect expired items tracked in manifest
            List<TrashManifestRecord> expiredRecords = manifest.Records
                .Where(record => IsExpiredManagedTrashRecord(record, startedUtc, retentionDays))
                .ToList();

            int deletedCount = 0;
            int cleanedMissingCount = 0;
            int emptyPruned = 0;
            int failedCount = 0;

            // 1. Process expiredRecords (tracked in manifest)
            var pathsToRemoveFromManifest = new List<string>();
            foreach (var record in expiredRecords)
            {
                if (!IsRecordAvailableForRestore(record))
                {
                    cleanedMissingCount++;
                    LogService.Warn($"[MidFdTrashCleanup] Missing or invalid expired item was preserved for explicit cleanup. path={record.TrashPath}");
                    continue;
                }

                string path = _pathValidator.ValidateRecord(record);
                try
                {
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
                    LogService.Warn($"[MidFdTrashCleanup] Failed to delete expired item. path={path}, error={ex.Message}");
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
            }

            if (pathsToRemoveFromManifest.Count > 0 && _activeBatchManifest == null)
            {
                SaveManifest(manifest);
            }

            LogService.Info(
                $"[MidFdTrashCleanup] Completed. trigger={trigger}, retentionDays={retentionDays}, " +
                $"deleted={deletedCount}, missingOrInvalidPreserved={cleanedMissingCount}, " +
                $"emptyContainersPruned={emptyPruned}, failed={failedCount}, remaining={manifest.Records.Count}");
        }
        finally
        {
            RetentionCleanupGate.Release();
        }
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

    private static bool PathExists(string path)
    {
        return File.Exists(path) || Directory.Exists(path);
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

        if (File.Exists(trashPath) || Directory.Exists(trashPath))
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
        while (File.Exists(candidate) || Directory.Exists(candidate))
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

    private static TrashManifestRecord RequireManifestRecord(string trashPath)
    {
        string normalizedPath = Path.GetFullPath(trashPath);
        TrashManifest manifest = _activeBatchManifest ?? LoadManifest();
        var matches = new List<TrashManifestRecord>();
        foreach (TrashManifestRecord record in manifest.Records)
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
        if (matches.Count != 1)
        {
            throw new InvalidOperationException("管理ゴミ箱manifest recordのidentityが一致しません。");
        }
        return matches[0];
    }

    private static IDisposable EnterMutation()
    {
        ThrowIfManagedTrashUnavailable();
        if (!Monitor.TryEnter(MutationSync))
        {
            throw new InvalidOperationException("別の管理ゴミ箱操作を実行中です。完了後に再試行してください。");
        }
        if (_activeMutationBatchId != null && MutationBatchContext.Value != _activeMutationBatchId)
        {
            Monitor.Exit(MutationSync);
            throw new InvalidOperationException("別の管理ゴミ箱batchを実行中です。完了後に再試行してください。");
        }
        return new MutationLease();
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
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Monitor.Exit(MutationSync);
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
        RegisterNewTrashRecords(records);
    }

    private static void RegisterNewTrashRecords(IEnumerable<TrashManifestRecord> records)
    {
        TrashManifest manifest = _activeBatchManifest ?? LoadManifest();
        var appendSw = Stopwatch.StartNew();
        RequireManifestStore().RegisterNewRecords(manifest, records);
        appendSw.Stop();

        _manifestAppendCount += records.Count();
        _manifestAppendMs += appendSw.ElapsedMilliseconds;
        _manifestRecordBatchMs += appendSw.ElapsedMilliseconds;
        _manifestRecordBatchCount += records.Count();
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

    private static void UpdateRecordStatus(string trashPath, TrashRecordStatus status)
    {
        TrashManifest manifest = _activeBatchManifest ?? LoadManifest();
        var sw = Stopwatch.StartNew();
        bool success = RequireManifestStore().UpdateRecordStatus(manifest, trashPath, status);
        sw.Stop();
        _lastManifestStatusUpdateMs += sw.ElapsedMilliseconds;

        if (!success)
        {
            return;
        }

        if (_activeBatchManifest == null)
        {
            var saveSw = Stopwatch.StartNew();
            SaveManifest(manifest);
            saveSw.Stop();
            _manifestAppendMs += saveSw.ElapsedMilliseconds;
        }
    }

    internal static void UpdateRecordStatuses(IEnumerable<string> trashPaths, TrashRecordStatus status)
    {
        TrashManifest manifest = _activeBatchManifest ?? LoadManifest();
        var sw = Stopwatch.StartNew();
        int updated = RequireManifestStore().UpdateRecordStatuses(manifest, trashPaths, status);
        sw.Stop();
        _lastManifestStatusUpdateMs += sw.ElapsedMilliseconds;

        if (updated > 0 && _activeBatchManifest == null)
        {
            var saveSw = Stopwatch.StartNew();
            SaveManifest(manifest);
            saveSw.Stop();
            _manifestAppendMs += saveSw.ElapsedMilliseconds;
        }
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
