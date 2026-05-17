using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace MidFD.Services
{
    /// <summary>
    /// 起動時や未処理例外のログを記録するための最小限のヘルパー。
    /// 既存の LogService が利用できない、または初期化前に発生したエラーを確実に記録する。
    /// </summary>
    public static class StartupExceptionLogger
    {
        private const string LogFileName = "startup_error.log";

        public static string Write(string source, Exception? ex)
        {
            try
            {
                string logDirectory = GetLogDirectory();
                if (!Directory.Exists(logDirectory))
                {
                    Directory.CreateDirectory(logDirectory);
                }

                string logPath = Path.Combine(logDirectory, LogFileName);
                string content = BuildLogContent(source, ex);

                // 追記
                File.AppendAllText(logPath, content, new UTF8Encoding(false));

                return logPath;
            }
            catch (Exception writeEx)
            {
                // ログ書き込み自体が失敗した場合は、デバッグ出力のみ行う
                Debug.WriteLine($"Failed to write startup log: {writeEx.Message}");
                return string.Empty;
            }
        }

        private static string GetLogDirectory()
        {
            // 優先順位 1: AppContext.BaseDirectory/logs (既存の LogService と同じ)
            string baseDir = AppContext.BaseDirectory;
            string logsDir = Path.Combine(baseDir, "logs");

            try
            {
                // 書き込み権限確認
                if (!Directory.Exists(logsDir)) Directory.CreateDirectory(logsDir);
                return logsDir;
            }
            catch
            {
                // 優先順位 2: %LOCALAPPDATA%/MidFD/Logs
                try
                {
                    string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    string midfdDir = Path.Combine(localAppData, "MidFD", "Logs");
                    if (!Directory.Exists(midfdDir)) Directory.CreateDirectory(midfdDir);
                    return midfdDir;
                }
                catch
                {
                    // 最終 fallback: カレントディレクトリ
                    return Directory.GetCurrentDirectory();
                }
            }
        }

        private static string BuildLogContent(string source, Exception? ex)
        {
            var sb = new StringBuilder();
            sb.AppendLine("================================================================================");
            sb.AppendLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            sb.AppendLine($"Source: {source}");
            sb.AppendLine($"App: MidFD");
            sb.AppendLine($"Version: {Assembly.GetExecutingAssembly().GetName().Version}");
            sb.AppendLine($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
            sb.AppendLine($"Process: {Process.GetCurrentProcess().ProcessName} (PID: {Process.GetCurrentProcess().Id})");
            sb.AppendLine($"CurrentDirectory: {Environment.CurrentDirectory}");
            sb.AppendLine($"BaseDirectory: {AppContext.BaseDirectory}");

            if (ex != null)
            {
                sb.AppendLine("--------------------------------------------------------------------------------");
                WriteExceptionRecursive(sb, ex, 0);
            }
            else
            {
                sb.AppendLine("Exception: (null)");
            }

            sb.AppendLine("================================================================================");
            sb.AppendLine();
            return sb.ToString();
        }

        private static void WriteExceptionRecursive(StringBuilder sb, Exception ex, int depth)
        {
            string indent = new string(' ', depth * 2);
            string prefix = depth == 0 ? "Exception" : "InnerException";

            sb.AppendLine($"{indent}{prefix}: {ex.GetType().FullName}");
            sb.AppendLine($"{indent}Message: {ex.Message}");
            sb.AppendLine($"{indent}StackTrace:");
            if (!string.IsNullOrEmpty(ex.StackTrace))
            {
                foreach (var line in ex.StackTrace.Split('\n'))
                {
                    sb.AppendLine($"{indent}  {line.TrimEnd()}");
                }
            }
            else
            {
                sb.AppendLine($"{indent}  (no stack trace available)");
            }

            if (ex is AggregateException aggEx)
            {
                foreach (var inner in aggEx.InnerExceptions)
                {
                    WriteExceptionRecursive(sb, inner, depth + 1);
                }
            }
            else if (ex.InnerException != null)
            {
                WriteExceptionRecursive(sb, ex.InnerException, depth + 1);
            }
        }
    }
}
