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

    /// <summary>
    /// tar.exe が実行可能か確認します。
    /// </summary>
    public static bool IsAvailable()
    {
        try
        {
            using var process = new Process();
            process.StartInfo.FileName = TarExe;
            process.StartInfo.Arguments = "--version";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.RedirectStandardOutput = true;
            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            // libarchive ベースの bsdtar であることを確認（通常 Windows 標準はこれ）
            return process.ExitCode == 0 && output.Contains("bsdtar");
        }
        catch
        {
            return false;
        }
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
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        using var process = new Process();
        process.StartInfo.FileName = TarExe;
        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.StandardOutputEncoding = Encoding.UTF8; // bsdtar on Windows 11 usually uses UTF-8 for output
        process.StartInfo.StandardErrorEncoding = Encoding.UTF8;

        process.OutputDataReceived += (s, e) =>
        {
            if (e.Data != null)
            {
                outputBuilder.AppendLine(e.Data);
                onOutputLine?.Invoke(e.Data);
            }
        };
        process.ErrorDataReceived += (s, e) =>
        {
            if (e.Data != null)
            {
                errorBuilder.AppendLine(e.Data);
                onOutputLine?.Invoke(e.Data);
            }
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            while (!process.WaitForExit(100))
            {
                if (token.IsCancellationRequested)
                {
                    try { process.Kill(true); } catch { }
                    return (-1, outputBuilder.ToString(), "キャンセルされました。");
                }
            }

            return (process.ExitCode, outputBuilder.ToString(), errorBuilder.ToString());
        }
        catch (Exception ex)
        {
            return (-1, outputBuilder.ToString(), $"tar.exe 実行エラー: {ex.Message}");
        }
    }
}
