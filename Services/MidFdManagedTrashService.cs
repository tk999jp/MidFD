using System.Text.Json;
using System.Diagnostics;
using MidFD.Models;
using MidFD.Services.TrashManifestStore;

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
    private static ITrashManifestStore ManifestStore = TrashManifestStoreFactory.CreateJsonStore(ManifestPath, JsonOptions);
    private static string SqliteManifestPath => Path.Combine(AppContext.BaseDirectory, "Data", "Trash", "manifest.db");
    private static string LegacySqliteManifestPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MidFD",
        "Trash",
        "manifest.db");

    public static void Initialize(Configuration.AppSettings settings)
    {
        // 常に SQLite を第一選択とし、環境や初期化可否で自動判定（fallback）する
        var mode = Configuration.ManagedTrashStoreMode.Sqlite;
        bool isNetwork = IsExecutableDirectoryNetworkPath();

        if (isNetwork)
        {
            LogService.Warn($"[MidFdTrashStore] SQLite disabled because executable directory is network path. BaseDirectory={AppContext.BaseDirectory}");
            LogService.Info($"[MidFdTrashStore] ActiveStore=Json FallbackReason=NetworkExecutableDirectory");
            mode = Configuration.ManagedTrashStoreMode.Json;
        }

        if (mode == Configuration.ManagedTrashStoreMode.Sqlite)
        {
            EnsureSqliteDbRelocation();
        }

        ManifestStore = TrashManifestStoreFactory.CreateStore(
            mode,
            ManifestPath,
            SqliteManifestPath,
            JsonOptions);

        // Telemetry
        var manifest = LoadManifest();
        LogService.Info($"[MidFdTrashStore] Store ready. RecordCount={manifest.Records.Count} [MidFdTrashLogThrottle] RuntimeGapCorrective active");
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

    private static void EnsureSqliteDbRelocation()
    {
        try
        {
            string target = SqliteManifestPath;
            if (File.Exists(target)) return;

            string source = LegacySqliteManifestPath;
            if (File.Exists(source))
            {
                string? dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.Copy(source, target);
                LogService.Info($"[MidFdTrashStore] Copied legacy AppData SQLite DB to executable data path. Source={source} Target={target}");
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"[MidFdTrashStore] Failed to check/relocate legacy AppData SQLite DB. Error={ex.Message}");
        }
    }

    public static TrashManifestMigrationResult MigrateJsonToSqlite(bool dryRun = false)
    {
        if (IsExecutableDirectoryNetworkPath())
        {
            LogService.Warn($"[TrashManifestMigration] Blocked because executable directory is network path. TargetDb={SqliteManifestPath}");
            throw new InvalidOperationException("実行ディレクトリがネットワーク上にあるため、SQLite 管理ゴミ箱DBは利用できません。ローカル配置で実行するか、JSON形式を使用してください。");
        }

        var options = new TrashManifestMigrationOptions
        {
            JsonManifestPath = ManifestPath,
            SqliteDbPath = SqliteManifestPath,
            DryRun = dryRun
        };
        return TrashManifestMigrationService.Migrate(options);
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
        if (_activeBatchManifest != null)
        {
            SaveManifest(_activeBatchManifest);
            _manifestRecordCountAfter = _activeBatchManifest.Records.Count;
            _activeBatchManifest = null;
        }
    }

    public static void SaveActiveBatch()
    {
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
        if (string.IsNullOrWhiteSpace(item.BeforePath) || string.IsNullOrWhiteSpace(item.RecycleBinPath))
        {
            throw new InvalidOperationException("MidFD管理ゴミ箱の復元情報が不完全です。");
        }

        if (File.Exists(item.BeforePath) || Directory.Exists(item.BeforePath))
        {
            throw new IOException($"復元先に同名項目があるため復元できません: {item.BeforePath}");
        }

        if (!File.Exists(item.RecycleBinPath) && !Directory.Exists(item.RecycleBinPath))
        {
            throw new FileNotFoundException("MidFD管理ゴミ箱内の項目が見つかりません。", item.RecycleBinPath);
        }

        var moveSw = Stopwatch.StartNew();
        Directory.CreateDirectory(Path.GetDirectoryName(item.BeforePath) ?? string.Empty);
        FileOperationService.Move(item.RecycleBinPath, item.BeforePath, suppressLogging: suppressLogging);
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

    public static void EmptyTrash()
    {
        TrashManifest manifest = _activeBatchManifest ?? LoadManifest();
        int deleted = 0;
        int cleaned = 0;
        int pruned = 0;
        List<string> itemsRoots = CollectKnownItemsRoots(manifest);

        foreach (TrashManifestRecord record in manifest.Records.ToList())
        {
            try
            {
                TryGetItemsRootForTrashPath(record.TrashPath, out string? itemsRoot);
                if (Directory.Exists(record.TrashPath))
                {
                    FileOperationService.Delete(record.TrashPath);
                    deleted++;
                }
                else if (File.Exists(record.TrashPath))
                {
                    FileOperationService.Delete(record.TrashPath);
                    deleted++;
                }
                else
                {
                    cleaned++;
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

        foreach (string itemsRoot in itemsRoots)
        {
            pruned += EmptyItemsRootChildren(itemsRoot);
        }

        if (_activeBatchManifest == null)
        {
            SaveManifest(manifest);
        }

        LogService.Info(
            $"[MidFdTrash] Empty completed. deleted={deleted}, cleaned={cleaned}, " +
            $"pruned={pruned}, itemsRoots={itemsRoots.Count}, remaining={manifest.Records.Count}");
    }

    public static string CreateBatchId()
    {
        return DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..8];
    }

    private static string ResolveTrashRoot(string originalPath)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(originalPath));
            if (!string.IsNullOrWhiteSpace(root))
            {
                string sameVolumeRoot = Path.Combine(root, TrashDirectoryName);
                Directory.CreateDirectory(sameVolumeRoot);
                TryHideDirectory(sameVolumeRoot);
                return sameVolumeRoot;
            }
        }
        catch (Exception ex)
        {
            LogService.Warn($"[MidFdTrash] Same-volume trash root unavailable. original={originalPath}, error={ex.Message}");
        }

        string fallbackRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MidFD",
            "Trash");
        Directory.CreateDirectory(fallbackRoot);
        return fallbackRoot;
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

    private static string ManifestPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MidFD",
        "Trash",
        ManifestFileName);

    private static string LocalTrashRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MidFD",
        "Trash");

    private static string LocalItemsRoot => Path.Combine(LocalTrashRoot, ItemsDirectoryName);

    private static List<string> CollectKnownItemsRoots(TrashManifest manifest)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (TrashManifestRecord record in manifest.Records)
        {
            if (TryGetItemsRootForTrashPath(record.TrashPath, out string? itemsRoot) &&
                !string.IsNullOrWhiteSpace(itemsRoot))
            {
                roots.Add(itemsRoot);
            }
        }

        if (Directory.Exists(LocalItemsRoot) && IsSafeItemsRoot(LocalItemsRoot))
        {
            roots.Add(Path.GetFullPath(LocalItemsRoot));
        }

        foreach (string drive in Directory.GetLogicalDrives())
        {
            try
            {
                string driveItemsRoot = Path.Combine(drive, TrashDirectoryName, ItemsDirectoryName);
                if (Directory.Exists(driveItemsRoot) && IsSafeItemsRoot(driveItemsRoot))
                {
                    roots.Add(Path.GetFullPath(driveItemsRoot));
                }
            }
            catch (Exception ex)
            {
                LogService.Warn($"[MidFdTrash] Failed to inspect drive trash root. drive={drive}, error={ex.Message}");
            }
        }

        return roots.ToList();
    }

    private static bool TryGetItemsRootForTrashPath(string trashPath, out string? itemsRoot)
    {
        itemsRoot = null;
        if (string.IsNullOrWhiteSpace(trashPath))
        {
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(trashPath);
        }
        catch
        {
            return false;
        }

        string localItemsRoot = Path.GetFullPath(LocalItemsRoot);
        if (IsPathWithinOrEqual(fullPath, localItemsRoot) && IsSafeItemsRoot(localItemsRoot))
        {
            itemsRoot = localItemsRoot;
            return true;
        }

        string? root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        string sameVolumeItemsRoot = Path.GetFullPath(Path.Combine(root, TrashDirectoryName, ItemsDirectoryName));
        if (IsPathWithinOrEqual(fullPath, sameVolumeItemsRoot) && IsSafeItemsRoot(sameVolumeItemsRoot))
        {
            itemsRoot = sameVolumeItemsRoot;
            return true;
        }

        return false;
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

    private static int EmptyItemsRootChildren(string itemsRoot)
    {
        if (!IsSafeItemsRoot(itemsRoot) || !Directory.Exists(itemsRoot))
        {
            return 0;
        }

        int pruned = 0;
        string normalizedItemsRoot = Path.GetFullPath(itemsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (string child in Directory.EnumerateFileSystemEntries(normalizedItemsRoot).ToList())
        {
            try
            {
                string fullChild = Path.GetFullPath(child);
                string? parent = Path.GetDirectoryName(fullChild)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!string.Equals(parent, normalizedItemsRoot, StringComparison.OrdinalIgnoreCase))
                {
                    LogService.Warn($"[MidFdTrash] Skipped non-child trash pruning target. target={fullChild}, itemsRoot={normalizedItemsRoot}");
                    continue;
                }

                FileOperationService.Delete(fullChild);
                pruned++;
                LogService.Info($"[MidFdTrash] Pruned orphan trash item root child. path={fullChild}");
            }
            catch (Exception ex)
            {
                LogService.Warn($"[MidFdTrash] Failed to prune orphan trash item root child. path={child}, error={ex.Message}");
            }
        }

        return pruned;
    }

    private static bool IsSafeItemsRoot(string itemsRoot)
    {
        if (string.IsNullOrWhiteSpace(itemsRoot))
        {
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(itemsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return false;
        }

        string? pathRoot = Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(pathRoot) ||
            string.Equals(fullPath, pathRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string localItemsRoot = Path.GetFullPath(LocalItemsRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(fullPath, localItemsRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string expectedSuffix = Path.Combine(TrashDirectoryName, ItemsDirectoryName);
        return fullPath.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPathWithinOrEqual(string path, string parent)
    {
        string normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(normalizedPath, normalizedParent, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(normalizedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static TrashManifest LoadManifest()
    {
        return ManifestStore.Load();
    }

    private static void SaveManifest(TrashManifest manifest)
    {
        ManifestStore.Save(manifest);
    }

    private static void RegisterNewTrashRecord(TrashManifestRecord record)
    {
        if (_activeBatchManifest != null)
        {
            var appendSw = Stopwatch.StartNew();
            ManifestStore.RegisterNewRecord(_activeBatchManifest, record);
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
        ManifestStore.RegisterNewRecords(manifest, records);
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
        _manifestUpsertScanCount += ManifestStore.UpsertRecord(manifest, record);
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
        bool success = ManifestStore.UpdateRecordStatus(manifest, trashPath, status);
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
        int updated = ManifestStore.UpdateRecordStatuses(manifest, trashPaths, status);
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
        return ManifestStore.TryGetRecordByOriginalPath(manifest, originalPath, out record);
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
