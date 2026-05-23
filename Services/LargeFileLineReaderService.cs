using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MidFD.Models;

namespace MidFD.Services;

public static class LargeFileLineReaderService
{
    /// <summary>
    /// 表示を速めるため、先頭の数行分だけ素早くインデックスを作成する。
    /// </summary>
    public static async Task ReadFirstLinesQuicklyAsync(
        LargeFilePreviewState state,
        int count,
        CancellationToken token,
        long maxInitialScanBytes = 512 * 1024)
    {
        await Task.Run(() =>
        {
            if (state.LineOffsets.Count > 1) return;
            if (state.LineOffsets.Count == 0) state.LineOffsets.Add(0);

            using var fs = new FileStream(state.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            state.TotalBytes = fs.Length;
            long contentStartOffset = GetContentStartOffset(state.FilePath, state.DetectedEncoding);
            if (state.LineOffsets.Count == 1 && state.LineOffsets[0] == 0 && contentStartOffset > 0)
            {
                state.LineOffsets[0] = contentStartOffset;
            }

            byte[] buffer = new byte[64 * 1024];
            fs.Position = contentStartOffset;
            long currentOffset = contentStartOffset;
            int bytesRead;
            int linesFound = 0;
            long scanLimit = Math.Min(Math.Max(0, maxInitialScanBytes), state.TotalBytes);
            int carry = -1;
            bool pendingCr = false;

            while (linesFound < count && currentOffset < scanLimit && (bytesRead = fs.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                if (IsUtf16Encoding(state.DetectedEncoding))
                {
                    linesFound += AddUtf16LineOffsets(
                        state.LineOffsets,
                        buffer,
                        bytesRead,
                        currentOffset,
                        state.DetectedEncoding,
                        ref carry,
                        count - linesFound);
                }
                else
                {
                    linesFound += AddSingleByteLineOffsets(
                        state.LineOffsets,
                        buffer,
                        bytesRead,
                        currentOffset,
                        ref pendingCr,
                        count - linesFound);
                }

                currentOffset += bytesRead;
            }

            if (pendingCr && linesFound < count)
            {
                state.LineOffsets.Add(currentOffset);
            }
        }, token);
    }

    public sealed record LargeFileLineIndexResult(
        IReadOnlyList<long> LineOffsets,
        long TotalBytes);

    public static async Task<LargeFileLineIndexResult> BuildLineIndexOffsetsAsync(
        string filePath,
        CancellationToken token,
        Encoding? encoding = null,
        Action<int>? progressCallback = null)
    {
        return await Task.Run(() =>
        {
            long contentStartOffset = GetContentStartOffset(filePath, encoding);
            var offsets = new List<long> { contentStartOffset };

            using var fs = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);

            long totalBytes = fs.Length;

            byte[] buffer = new byte[64 * 1024];
            fs.Position = contentStartOffset;
            long currentOffset = contentStartOffset;
            int bytesRead;
            int lastProgress = -1;
            int carry = -1;
            bool pendingCr = false;

            while ((bytesRead = fs.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                if (IsUtf16Encoding(encoding))
                {
                    AddUtf16LineOffsets(offsets, buffer, bytesRead, currentOffset, encoding!, ref carry);
                }
                else
                {
                    AddSingleByteLineOffsets(offsets, buffer, bytesRead, currentOffset, ref pendingCr);
                }

                currentOffset += bytesRead;

                if (progressCallback != null && totalBytes > 0)
                {
                    int progress = (int)Math.Min(100, (currentOffset * 100L) / totalBytes);
                    if (progress != lastProgress)
                    {
                        lastProgress = progress;
                        progressCallback(progress);
                    }
                }
            }

            if (pendingCr)
            {
                offsets.Add(currentOffset);
            }

            return new LargeFileLineIndexResult(offsets, totalBytes);
        }, token);
    }

    /// <summary>
    /// 互換用。UI表示中の LargeText Viewer 経路では直接使わないこと。
    /// UI表示中は BuildLineIndexOffsetsAsync で local build し、UIスレッドで ReplaceLineOffsets する。
    /// </summary>
    public static async Task BuildLineIndexAsync(LargeFilePreviewState state, CancellationToken token, Action<int>? progressCallback = null)
    {
        var result = await BuildLineIndexOffsetsAsync(state.FilePath, token, state.DetectedEncoding, progressCallback);
        state.ReplaceLineOffsets(result.LineOffsets, result.TotalBytes);
    }

    /// <summary>
    /// 指定された行範囲の内容を読み込む。
    /// </summary>
    public static async Task<List<string>> ReadLinesAsync(
        LargeFilePreviewState state,
        int startLine,
        int count,
        Encoding encoding,
        CancellationToken token,
        int maxLineReadBytes = int.MaxValue)
    {
        return await Task.Run(() =>
        {
            var lines = new List<string>();
            if (state.LineOffsets.Count == 0) return lines;

            int endLine = Math.Min(startLine + count, state.LineOffsets.Count);
            using var fs = new FileStream(state.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            for (int i = startLine; i < endLine; i++)
            {
                token.ThrowIfCancellationRequested();

                long startOffset = state.LineOffsets[i];
                long nextOffset = (i + 1 < state.LineOffsets.Count) ? state.LineOffsets[i + 1] : state.TotalBytes;
                int lineByteLen = (int)(nextOffset - startOffset);
                if (maxLineReadBytes > 0 && lineByteLen > maxLineReadBytes)
                {
                    lineByteLen = maxLineReadBytes;
                }

                if (lineByteLen <= 0)
                {
                    lines.Add(string.Empty);
                    continue;
                }

                byte[] lineBuf = new byte[lineByteLen];
                fs.Seek(startOffset, SeekOrigin.Begin);
                fs.Read(lineBuf, 0, lineByteLen);

                // 改行文字（CRLF, LF, CR）を除去して文字列化
                string lineText = encoding.GetString(lineBuf);
                lines.Add(lineText.TrimEnd('\r', '\n'));
            }

            return lines;
        }, token);
    }

    /// <summary>
    /// テキストを検索し、最初に見つかった行と列を返す。
    /// </summary>
    public static async Task<(int Line, int Column, int Length)?> SearchTextAsync(
        LargeFilePreviewState state,
        string query,
        int startLine,
        int startColumn,
        bool backward,
        Encoding encoding,
        CancellationToken token)
    {
        return await Task.Run<(int Line, int Column, int Length)?>(() =>
        {
            if (state.LineOffsets.Count == 0) return null;
            if (string.IsNullOrEmpty(query)) return null;

            using var fs = new FileStream(state.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            
            int current = startLine;
            if (current < 0) current = 0;
            if (current >= state.LineOffsets.Count) current = state.LineOffsets.Count - 1;

            while (current >= 0 && current < state.LineOffsets.Count)
            {
                token.ThrowIfCancellationRequested();

                long offset = state.LineOffsets[current];
                long nextOffset = (current + 1 < state.LineOffsets.Count) ? state.LineOffsets[current + 1] : state.TotalBytes;
                int len = (int)(nextOffset - offset);

                if (len > 0)
                {
                    byte[] buf = new byte[len];
                    fs.Seek(offset, SeekOrigin.Begin);
                    fs.Read(buf, 0, len);
                    
                    string lineText = encoding.GetString(buf).TrimEnd('\r', '\n');
                    int hitColumn = FindInLine(lineText, query, current == startLine ? startColumn : (backward ? int.MaxValue : 0), backward);
                    if (hitColumn >= 0)
                    {
                        return (current, hitColumn, query.Length);
                    }
                }

                if (backward) current--;
                else current++;
            }

            return null;
        }, token);
    }

    private static int FindInLine(string lineText, string query, int startColumn, bool backward)
    {
        if (string.IsNullOrEmpty(lineText) || string.IsNullOrEmpty(query))
        {
            return -1;
        }

        if (backward)
        {
            int startIndex = Math.Min(startColumn, lineText.Length - 1);
            if (startIndex < 0)
            {
                return -1;
            }

            return lineText.LastIndexOf(query, startIndex, startIndex + 1, StringComparison.OrdinalIgnoreCase);
        }

        int normalizedStart = Math.Clamp(startColumn, 0, lineText.Length);
        return lineText.IndexOf(query, normalizedStart, StringComparison.OrdinalIgnoreCase);
    }

    private static int AddSingleByteLineOffsets(
        List<long> offsets,
        byte[] buffer,
        int bytesRead,
        long currentOffset,
        ref bool pendingCr,
        int maxLinesToAdd = int.MaxValue)
    {
        int added = 0;
        for (int i = 0; i < bytesRead && added < maxLinesToAdd; i++)
        {
            byte current = buffer[i];
            if (pendingCr)
            {
                if (current == 0x0A)
                {
                    offsets.Add(currentOffset + i + 1);
                    added++;
                    pendingCr = false;
                    continue;
                }

                offsets.Add(currentOffset + i);
                added++;
                pendingCr = false;
            }

            if (current == 0x0D)
            {
                pendingCr = true;
            }
            else if (current == 0x0A)
            {
                offsets.Add(currentOffset + i + 1);
                added++;
            }
        }

        return added;
    }

    private static bool IsUtf16Encoding(Encoding? encoding)
    {
        return encoding != null && (encoding.CodePage == Encoding.Unicode.CodePage || encoding.CodePage == Encoding.BigEndianUnicode.CodePage);
    }

    private static long GetContentStartOffset(string filePath, Encoding? encoding)
    {
        if (!IsUtf16Encoding(encoding))
        {
            return 0;
        }

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 2)
            {
                return 0;
            }

            int first = fs.ReadByte();
            int second = fs.ReadByte();
            if (encoding!.CodePage == Encoding.Unicode.CodePage && first == 0xFF && second == 0xFE)
            {
                return 2;
            }

            if (encoding.CodePage == Encoding.BigEndianUnicode.CodePage && first == 0xFE && second == 0xFF)
            {
                return 2;
            }
        }
        catch
        {
            return 0;
        }

        return 0;
    }

    private static int AddUtf16LineOffsets(
        IList<long> offsets,
        byte[] buffer,
        int bytesRead,
        long currentOffset,
        Encoding encoding,
        ref int carry,
        int maxToAdd = int.MaxValue)
    {
        int added = 0;
        int i = 0;

        if (carry >= 0 && bytesRead > 0)
        {
            if (IsUtf16Lf((byte)carry, buffer[0], encoding))
            {
                offsets.Add(currentOffset + 1);
                added++;
                if (added >= maxToAdd)
                {
                    carry = -1;
                    return added;
                }
            }

            carry = -1;
            i = 1;
        }

        for (; i + 1 < bytesRead; i += 2)
        {
            if (IsUtf16Lf(buffer[i], buffer[i + 1], encoding))
            {
                offsets.Add(currentOffset + i + 2);
                added++;
                if (added >= maxToAdd)
                {
                    return added;
                }
            }
        }

        if (i < bytesRead)
        {
            carry = buffer[i];
        }

        return added;
    }

    private static bool IsUtf16Lf(byte first, byte second, Encoding encoding)
    {
        if (encoding.CodePage == Encoding.Unicode.CodePage)
        {
            return first == 0x0A && second == 0x00;
        }

        return first == 0x00 && second == 0x0A;
    }
}
