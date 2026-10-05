using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MidFD.Services;

public enum TextPreviewEncodingOverride
{
    Auto,
    Utf8,
    ShiftJis
}

public sealed record TextPreviewContent(string Text, string EncodingLabel);

/// <summary>
/// Text Previewの読込・encoding判定をUIから分離する。
/// RichTextBox/TextBox等の表示surfaceは参照しない。
/// </summary>
public static class TextPreviewContentService
{
    public static Task<TextPreviewContent> LoadAsync(
        string path,
        int maxBytes,
        TextPreviewEncodingOverride encodingOverride,
        CancellationToken token)
    {
        return Task.Run(() => Load(path, maxBytes, encodingOverride, token), token);
    }

    private static TextPreviewContent Load(
        string path,
        int maxBytes,
        TextPreviewEncodingOverride encodingOverride,
        CancellationToken token)
    {
        using var stream = File.OpenRead(path);
        token.ThrowIfCancellationRequested();
        int bytesToRead = (int)Math.Min(stream.Length, maxBytes);
        byte[] buffer = new byte[bytesToRead];
        int readCount = stream.Read(buffer, 0, bytesToRead);
        token.ThrowIfCancellationRequested();
        bool truncated = stream.Length > maxBytes;

        if (encodingOverride == TextPreviewEncodingOverride.Utf8)
        {
            string text = NormalizeNewlines(Encoding.UTF8.GetString(buffer, 0, readCount));
            return CreateContent(text, truncated, "UTF-8 (manual)");
        }

        if (encodingOverride == TextPreviewEncodingOverride.ShiftJis)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            string text = NormalizeNewlines(Encoding.GetEncoding("shift_jis").GetString(buffer, 0, readCount));
            return CreateContent(text, truncated, "CP932 (manual)");
        }

        if (readCount >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
        {
            string text = NormalizeNewlines(Encoding.UTF8.GetString(buffer, 3, readCount - 3));
            return CreateContent(text, truncated, "UTF-8 BOM");
        }

        if (readCount >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE)
        {
            string text = NormalizeNewlines(Encoding.Unicode.GetString(buffer, 2, readCount - 2));
            return CreateContent(text, truncated, "UTF-16 LE BOM");
        }

        if (readCount >= 2 && buffer[0] == 0xFE && buffer[1] == 0xFF)
        {
            string text = NormalizeNewlines(Encoding.BigEndianUnicode.GetString(buffer, 2, readCount - 2));
            return CreateContent(text, truncated, "UTF-16 BE BOM");
        }

        try
        {
            var utf8Strict = new UTF8Encoding(false, true);
            int safeLength = GetSafeUtf8Length(buffer, readCount);
            string text = NormalizeNewlines(utf8Strict.GetString(buffer, 0, safeLength));
            return CreateContent(text, truncated, "UTF-8");
        }
        catch (ArgumentException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            string text = NormalizeNewlines(Encoding.GetEncoding("shift_jis").GetString(buffer, 0, readCount));
            return CreateContent(text, truncated, "CP932");
        }
    }

    private static TextPreviewContent CreateContent(string text, bool truncated, string encodingLabel)
    {
        if (truncated)
        {
            text += $"{Environment.NewLine}{Environment.NewLine}[... 表示節減されました ...]";
        }
        return new TextPreviewContent(text, encodingLabel);
    }

    private static string NormalizeNewlines(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }
        text = text.Replace("\r\n", "\n");
        text = text.Replace('\r', '\n');
        return text.Replace("\n", Environment.NewLine);
    }

    private static int GetSafeUtf8Length(byte[] buffer, int length)
    {
        if (length <= 0)
        {
            return 0;
        }
        for (int i = 1; i <= Math.Min(length, 3); i++)
        {
            byte b = buffer[length - i];
            if ((b & 0x80) == 0)
            {
                return length;
            }
            if ((b & 0xC0) == 0xC0)
            {
                int expected;
                if ((b & 0xE0) == 0xC0) expected = 2;
                else if ((b & 0xF0) == 0xE0) expected = 3;
                else if ((b & 0xF8) == 0xF0) expected = 4;
                else return length;
                return i < expected ? length - i : length;
            }
        }
        return length;
    }
}
