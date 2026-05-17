using System;
using System.IO;
using System.Text;
using MidFD.Configuration;

namespace MidFD.Services
{
    /// <summary>
    /// 最小限のロギング機能を提供する静的クラス。
    /// logs/app.log に追記を行う。
    /// </summary>
    public static class LogService
    {
        private static readonly string LogDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
        private static readonly string LogFilePath = Path.Combine(LogDirectory, "app.log");
        private static readonly object LockObj = new object();
        private static bool _isEnabled = true;
        private static bool _isDetailedEnabled = false;

        public static void ApplySettings(LoggingSettings? settings)
        {
            _isEnabled = settings?.IsEnabled ?? true;
            _isDetailedEnabled = settings?.IsDetailedEnabled ?? false;
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
    }
}
