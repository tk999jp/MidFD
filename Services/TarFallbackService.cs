using System.Diagnostics;
using System.Text;
using MidFD.Models;

namespace MidFD.Services;

/// <summary>
/// Windows 標準の tar.exe (bsdtar) を使用したアーカイブ操作のフォールバックを提供します。
/// Windows 11 24H2 以降の環境を主な対象とし、7z/TAR の作成・展開、RAR の展開に対応します。
/// </summary>
public static class TarFallbackService
{
    private const string TarExe = "tar.exe";
    internal const int MaxCapturedChars = 16 * 1024 * 1024;
    internal const int MaxOutputLineChars = 256 * 1024;

    /// <summary>
    /// tar.exe が実行可能か確認します。
    /// </summary>
    public static bool IsAvailable()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var result = RunTar(["--version"], timeout.Token);
        return result.ExitCode == 0 && result.Output.Contains("bsdtar", StringComparison.Ordinal);
    }

    /// <summary>
    /// アーカイブの内容一覧を取得します。
    /// </summary>
    public static (int ExitCode, string Output, string Error) List(
        string archivePath,
        CancellationToken token = default)
    {
        // -tf: 内容一覧
        var args = new List<string> { "-tf", archivePath };
        return RunTar(args, token);
    }

    /// <summary>
    /// アーカイブを解凍します。
    /// </summary>
    public static (int ExitCode, string Output, string Error) Unpack(
        string archivePath,
        string destinationDirectory,
        IEnumerable<string>? selectedEntries = null,
        CancellationToken token = default,
        Action<string>? onOutputLine = null)
    {
        try
        {
            Directory.CreateDirectory(destinationDirectory);
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, $"展開先フォルダ '{destinationDirectory}' を作成できませんでした: {ex.Message}");
        }

        // -xvf: 展開, 詳細出力 (展開時は -a (auto-detect) が一部の tar で警告・失敗の原因となるため -x を優先)
        // -C: 出力先指定
        var args = new List<string> { "-xvf", archivePath, "-C", destinationDirectory };
        if (selectedEntries != null)
        {
            args.AddRange(selectedEntries);
        }

        var res = RunTar(args, token, onOutputLine);

        // 特定のエラーメッセージに対する調整
        if (res.ExitCode != 0 && !string.IsNullOrEmpty(res.Error))
        {
            if (res.Error.Contains("could not chdir"))
            {
                return (res.ExitCode, res.Output, $"展開先フォルダ '{destinationDirectory}' へ移動できませんでした。パスが長すぎるか、アクセス権限がありません。\n\n[詳細]\n{res.Error}");
            }
        }

        return res;
    }

    /// <summary>
    /// アーカイブを作成します。
    /// </summary>
    public static (int ExitCode, string Output, string Error) Pack(
        string outputPath,
        string baseDirectory,
        IEnumerable<string> relativePaths,
        CancellationToken token = default,
        Action<string>? onOutputLine = null)
    {
        // -acvf: 自動判別作成, 詳細出力
        // -C: 基準ディレクトリ指定
        var args = new List<string> { "-acvf", outputPath, "-C", baseDirectory };
        args.AddRange(relativePaths);

        return RunTar(args, token, onOutputLine);
    }

    private static (int ExitCode, string Output, string Error) RunTar(
        IEnumerable<string> args,
        CancellationToken token,
        Action<string>? onOutputLine = null)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        // Windows bsdtar emits redirected names in the active system code page.
        Encoding nativeEncoding = Encoding.GetEncoding(0);
        var start = new ProcessStartInfo(TarExe)
        {
            StandardOutputEncoding = nativeEncoding,
            StandardErrorEncoding = nativeEncoding
        };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        return RunProcess(start, token, onOutputLine);
    }

    internal static (int ExitCode, string Output, string Error) RunProcess(
        ProcessStartInfo start, CancellationToken token, Action<string>? onOutputLine = null)
    {
        if (token.IsCancellationRequested) return (-1, string.Empty, "キャンセルされました。");
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.StandardOutputEncoding ??= Encoding.UTF8;
        start.StandardErrorEncoding ??= Encoding.UTF8;
        var output = new StringBuilder();
        var error = new StringBuilder();
        var callbackLock = new object();
        Exception? callbackError = null;
        using var process = new Process { StartInfo = start };
        Task stdout = Task.CompletedTask, stderr = Task.CompletedTask;
        bool started = false;
        int code = -1;
        string failure = string.Empty;
        int captured = 0;
        void OutputLimit() => Interlocked.CompareExchange(ref callbackError,
            new IOException("tar出力が資源上限に達したため中断しました。outputは部分結果です。"), null);
        async Task Drain(StreamReader reader, StringBuilder buffer)
        {
            char[] chunk = new char[4096];
            var line = new StringBuilder();
            bool omitLine = false;
            void Publish()
            {
                if (line.Length > 0 && line[^1] == '\r') line.Length--;
                if (!omitLine && Volatile.Read(ref callbackError) == null)
                {
                    if (Interlocked.Add(ref captured, line.Length + Environment.NewLine.Length) > MaxCapturedChars) OutputLimit();
                    else
                    {
                        string text = line.ToString();
                        buffer.AppendLine(text);
                        lock (callbackLock)
                        {
                            if (callbackError == null)
                            {
                                try { onOutputLine?.Invoke(text); }
                                catch (Exception ex) { Interlocked.CompareExchange(ref callbackError, ex, null); }
                            }
                        }
                    }
                }
                line.Clear(); omitLine = false;
            }
            int count;
            while ((count = await reader.ReadAsync(chunk).ConfigureAwait(false)) > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    if (chunk[i] == '\n') { Publish(); continue; }
                    if (omitLine || Volatile.Read(ref callbackError) != null) continue;
                    if (line.Length >= MaxOutputLineChars) { OutputLimit(); line.Clear(); omitLine = true; }
                    else line.Append(chunk[i]);
                }
            }
            if (line.Length > 0) Publish();
        }

        try
        {
            started = process.Start();
            stdout = Task.Run(() => Drain(process.StandardOutput, output));
            stderr = Task.Run(() => Drain(process.StandardError, error));
            while (!process.WaitForExit(50))
            {
                token.ThrowIfCancellationRequested();
                if (Volatile.Read(ref callbackError) is { } notificationError)
                    throw new IOException("tar出力通知に失敗しました。", notificationError);
                if (stdout.IsFaulted) stdout.GetAwaiter().GetResult();
                if (stderr.IsFaulted) stderr.GetAwaiter().GetResult();
            }
            code = process.ExitCode;
        }
        catch (OperationCanceledException) { failure = "キャンセルされました。"; }
        catch (Exception ex) { failure = $"tar.exe 実行エラー: {ex.Message}"; }
        finally
        {
            if (started)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    // Cancellation never returns while the child may still be writing files.
                    process.WaitForExit();
                }
                catch (Exception ex) { failure += $" process終了確認失敗: {ex.Message}"; }
                try { Task.WhenAll(stdout, stderr).GetAwaiter().GetResult(); }
                catch (Exception ex) { failure += $" 出力読込失敗: {ex.Message}"; }
            }
        }
        if (token.IsCancellationRequested) failure = "キャンセルされました。" + failure;
        if (callbackError != null) failure += $" 出力通知失敗: {callbackError.Message}";
        return (failure.Length == 0 ? code : -1, output.ToString(), failure.Length == 0 ? error.ToString() : failure + error);
    }
}
