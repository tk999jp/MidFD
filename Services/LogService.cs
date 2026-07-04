using System;
using System.IO;
using System.Text;
using MidFD.Configuration;
using MidFD.Configuration.Storage;

namespace MidFD.Services
{
    /// <summary>
    /// 最小限のロギング機能を提供する静的クラス。
    /// logs/app.log に追記を行う。
    /// </summary>
    public static class LogService
    {
        private static readonly AppStoragePaths StoragePaths = LegacyStoragePathProvider.CreateDefault().GetPaths();
        private static readonly string LogDirectory = StoragePaths.LogDirectory;
        private static readonly string LogFilePath = Path.Combine(LogDirectory, "app.log");
        private static readonly object LockObj = new object();
        private static bool _isEnabled = false;
        private static bool _isDetailedEnabled = false;
        private static long _maxFileSizeBytes = 5 * 1024 * 1024;
        private static int _retentionDays = 14;
        private static DateTime _lastPruneUtc = DateTime.MinValue;

        internal static string CurrentLogDirectory => LogDirectory;
        internal static string CurrentLogFilePath => LogFilePath;

        public static void ApplySettings(LoggingSettings? settings)
        {
            _isEnabled = settings?.IsEnabled ?? false;
            _isDetailedEnabled = settings?.IsDetailedEnabled ?? false;
            _maxFileSizeBytes = Math.Max(256 * 1024, settings?.MaxFileSizeBytes ?? 5 * 1024 * 1024);
            _retentionDays = Math.Max(1, settings?.RetentionDays ?? 14);
        }

        /// <summary>
        /// 情報ログを記録する。
        /// </summary>
        public static void Info(string message) => Write("INFO", message);

        /// <summary>
        /// 警告ログを記録する。
        /// </summary>
        public static void Warn(string message) => Write("WARN", message);

        /// <summary>
        /// 調査用の詳細ログを記録する。
        /// </summary>
        public static void Detail(string message)
        {
            if (!_isDetailedEnabled) return;
            Write("DETAIL", message);
        }

        /// <summary>
        /// エラーログを記録する。
        /// </summary>
        public static void Error(string message, Exception? ex = null)
        {
            string fullMessage = ex != null ? $"{message} (Exception: {ex.GetType().Name}: {ex.Message})\n{ex.StackTrace}" : message;
            Write("ERROR", fullMessage);
        }

        private static void Write(string level, string message)
        {
            if (!_isEnabled) return;

            try
            {
                lock (LockObj)
                {
                    if (!Directory.Exists(LogDirectory))
                    {
                        Directory.CreateDirectory(LogDirectory);
                    }

                    RotateLogIfNeeded();
                    PruneOldLogsIfNeeded();

                    string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    string logLine = $"[{timestamp}] [{level}] {message}{Environment.NewLine}";

                    // BOMなしUTF-8で追記
                    File.AppendAllText(LogFilePath, logLine, new UTF8Encoding(false));
                }
            }
            catch
            {
                // ログ自体の失敗でアプリを落とさない
#if DEBUG
                System.Diagnostics.Debug.WriteLine($"Failed to write log: {message}");
#endif
            }
        }

        private static void RotateLogIfNeeded()
        {
            if (!File.Exists(LogFilePath))
            {
                return;
            }

            long fileSize = new FileInfo(LogFilePath).Length;
            if (fileSize < _maxFileSizeBytes)
            {
                return;
            }

            string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string rotatedPath = Path.Combine(LogDirectory, $"app-{timestamp}.log");
            int sequence = 1;
            while (File.Exists(rotatedPath))
            {
                rotatedPath = Path.Combine(LogDirectory, $"app-{timestamp}-{sequence}.log");
                sequence++;
            }

            File.Move(LogFilePath, rotatedPath);
        }

        private static void PruneOldLogsIfNeeded()
        {
            DateTime nowUtc = DateTime.UtcNow;
            if ((nowUtc - _lastPruneUtc) < TimeSpan.FromDays(1))
            {
                return;
            }

            _lastPruneUtc = nowUtc;
            DateTime cutoffUtc = nowUtc.AddDays(-_retentionDays);
            foreach (string archivedLogPath in Directory.EnumerateFiles(LogDirectory, "app-*.log"))
            {
                try
                {
                    DateTime lastWriteUtc = File.GetLastWriteTimeUtc(archivedLogPath);
                    if (lastWriteUtc < cutoffUtc)
                    {
                        File.Delete(archivedLogPath);
                    }
                }
                catch
                {
                    // ログ削除失敗でもアプリを落とさない
                }
            }
        }
    }
}
