using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MidFD.Models;
using MidFD.Services;

namespace MidFD.Runtime;

/// <summary>
/// Preview の非 UI コンテンツ取得を担当する application boundary。
/// WinForms control は参照せず、表示反映は shell 側の adapter に返す。
/// </summary>
internal sealed class PreviewApplicationCoordinator
{
    public Task<TextPreviewContent> LoadTextAsync(
        string path,
        int maxBytes,
        TextPreviewEncodingOverride encodingOverride,
        CancellationToken token)
    {
        return TextPreviewContentService.LoadAsync(path, maxBytes, encodingOverride, token);
    }

    public Task<string> LoadMarkdownAsync(string path, int maxBytes, CancellationToken token)
    {
        return MarkdownPreviewService.GetPreviewAsync(path, maxBytes, token);
    }

    public Task<DelimitedTextTable> LoadDelimitedAsync(string path, int maxBytes, CancellationToken token)
    {
        return DelimitedTextPreviewService.ReadAsync(path, maxBytes, token);
    }

    public Task<string> LoadSqliteAsync(string path, CancellationToken token)
    {
        return SqlitePreviewService.GetPreviewAsync(path, token);
    }

    public Task<string> LoadBinaryDumpAsync(string path, CancellationToken token)
    {
        return Task.Run(() => BuildBinaryDump(path, token), token);
    }

    private static string BuildBinaryDump(string path, CancellationToken token)
    {
        try
        {
            const int hexDumpMaxLength = 4096;
            using var stream = File.OpenRead(path);
            token.ThrowIfCancellationRequested();
            int length = (int)Math.Min(stream.Length, hexDumpMaxLength);
            byte[] buffer = new byte[length];
            int read = stream.Read(buffer, 0, length);
            var builder = new StringBuilder();
            builder.AppendLine($"[Binary Dump: {Path.GetFileName(path)} - {(stream.Length > hexDumpMaxLength ? "First 4KB" : $"{read} Bytes")}]\n");
            for (int offset = 0; offset < read; offset += 16)
            {
                if (offset % 512 == 0)
                {
                    token.ThrowIfCancellationRequested();
                }

                builder.Append($"{offset:X8}  ");
                for (int column = 0; column < 16; column++)
                {
                    if (offset + column < read)
                    {
                        builder.Append($"{buffer[offset + column]:X2} ");
                    }
                    else
                    {
                        builder.Append("   ");
                    }

                    if (column == 7)
                    {
                        builder.Append(' ');
                    }
                }

                builder.Append(" |");
                for (int column = 0; column < 16 && offset + column < read; column++)
                {
                    byte value = buffer[offset + column];
                    builder.Append(value is >= 32 and <= 126 ? (char)value : '.');
                }
                builder.AppendLine("|");
            }

            return builder.ToString();
        }
        catch (IOException)
        {
            return "[プレビュー不可: 使用中またはロックされています]";
        }
        catch (UnauthorizedAccessException)
        {
            return "[プレビュー不可: アクセス権限がありません]";
        }
    }
}
