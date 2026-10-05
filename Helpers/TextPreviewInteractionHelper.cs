using System.Diagnostics;
using System.Windows.Forms;
using MidFD.Services;

namespace MidFD.Helpers;

internal static class TextPreviewInteractionHelper
{
    private static readonly char[] TrimUrlPunctuation =
    {
        '(', ')', '[', ']', '{', '}', '<', '>', '"', '\'', '`', '.', ',', ';', ':', '!', '?',
        '（', '）', '［', '］', '｛', '｝', '〈', '〉', '《', '》', '「', '」', '『', '』', '【', '】',
        '〔', '〕', '。', '、', '，', '．', '；', '：', '！', '？'
    };

    public static void Attach(
        TextBoxBase textBox,
        Action<string>? showStatusMessage = null,
        IWin32Window? dialogOwner = null,
        bool showErrorDialog = false,
        Func<string?, string?>? resolveClickedUrl = null,
        Action<string>? launchExternalUrl = null)
    {
        if (textBox == null) throw new ArgumentNullException(nameof(textBox));

        textBox.ReadOnly = true;
        textBox.ShortcutsEnabled = true;

        if (textBox is RichTextBox richTextBox)
        {
            richTextBox.DetectUrls = true;
            richTextBox.LinkClicked += (_, e) =>
            {
                string? url = resolveClickedUrl?.Invoke(e.LinkText) ?? e.LinkText;
                OpenWebLink(url, showStatusMessage, dialogOwner, showErrorDialog, launchExternalUrl);
            };
        }
        else if (textBox is TextBox plainTextBox)
        {
            string urlHitTestText = plainTextBox.Text;
            plainTextBox.TextChanged += (_, _) => urlHitTestText = plainTextBox.Text;

            plainTextBox.MouseClick += (_, e) =>
            {
                if (e.Button != MouseButtons.Left)
                {
                    return;
                }

                int charIndex = plainTextBox.GetCharIndexFromPosition(e.Location);
                if (!IsCharacterAtPoint(plainTextBox, urlHitTestText, charIndex, e.Location))
                {
                    return;
                }

                string? linkText = FindWebUrlAt(urlHitTestText, charIndex);
                if (linkText == null)
                {
                    return;
                }

                string? url = resolveClickedUrl?.Invoke(linkText) ?? linkText;
                OpenWebLink(url, showStatusMessage, dialogOwner, showErrorDialog, launchExternalUrl);
            };
            plainTextBox.MouseMove += (_, e) =>
            {
                int charIndex = plainTextBox.GetCharIndexFromPosition(e.Location);
                plainTextBox.Cursor = FindWebUrlAt(
                    IsCharacterAtPoint(plainTextBox, urlHitTestText, charIndex, e.Location)
                        ? urlHitTestText
                        : null,
                    charIndex) == null
                    ? Cursors.IBeam
                    : Cursors.Hand;
            };
            plainTextBox.MouseLeave += (_, _) => plainTextBox.Cursor = Cursors.IBeam;
        }

        textBox.KeyDown += (_, e) =>
        {
            if (!e.Control) return;

            if (e.KeyCode == Keys.A)
            {
                textBox.SelectAll();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.C && textBox.SelectionLength > 0)
            {
                textBox.Copy();
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        };

        var menu = new ContextMenuStrip();
        var copyItem = new ToolStripMenuItem("コピー (&C)");
        copyItem.Click += (_, _) =>
        {
            if (textBox.SelectionLength <= 0) return;
            textBox.Copy();
            showStatusMessage?.Invoke("選択範囲をコピーしました。");
        };

        var selectAllItem = new ToolStripMenuItem("すべて選択 (&A)");
        selectAllItem.Click += (_, _) => textBox.SelectAll();

        menu.Items.Add(copyItem);
        menu.Items.Add(selectAllItem);
        menu.Opening += (_, _) =>
        {
            copyItem.Enabled = textBox.SelectionLength > 0;
        };

        textBox.ContextMenuStrip = menu;
    }

    internal static string? FindWebUrlAt(string? text, int index)
    {
        if (string.IsNullOrEmpty(text) || index < 0 || index >= text.Length || char.IsWhiteSpace(text[index]))
        {
            return null;
        }

        int start = FindSchemeStart(text, index);
        if (start < 0)
        {
            return null;
        }

        int end = FindCandidateEnd(text, start);
        if (index < start || index >= end)
        {
            return null;
        }

        string rawCandidate = text[start..end];
        string candidate = rawCandidate.Trim(TrimUrlPunctuation);
        int trimmedStart = start + rawCandidate.Length - rawCandidate.TrimStart(TrimUrlPunctuation).Length;
        int trimmedEnd = trimmedStart + candidate.Length;
        return index >= trimmedStart && index < trimmedEnd && UrlValidationHelper.IsValidWebUrl(candidate)
            ? candidate
            : null;
    }

    private static int FindSchemeStart(string text, int index)
    {
        for (int candidate = index; candidate >= 0; candidate--)
        {
            bool isHttp = candidate + 7 <= text.Length
                && text.AsSpan(candidate, 7).Equals("http://", StringComparison.OrdinalIgnoreCase);
            bool isHttps = candidate + 8 <= text.Length
                && text.AsSpan(candidate, 8).Equals("https://", StringComparison.OrdinalIgnoreCase);
            if (!isHttp && !isHttps)
            {
                continue;
            }

            if (candidate == 0 || !IsSchemeContinuation(text[candidate - 1]))
            {
                return candidate;
            }
        }

        return -1;
    }

    private static int FindCandidateEnd(string text, int start)
    {
        bool schemeIsAttachedToText = start > 0 && !char.IsWhiteSpace(text[start - 1])
            && !TrimUrlPunctuation.Contains(text[start - 1]);

        for (int index = start; index < text.Length; index++)
        {
            char character = text[index];
            if (char.IsWhiteSpace(character) || IsUrlDelimiter(character))
            {
                return index;
            }

            if (schemeIsAttachedToText && IsJapaneseProseCharacter(character))
            {
                return index;
            }
        }

        return text.Length;
    }

    private static bool IsSchemeContinuation(char character)
    {
        return char.IsAsciiLetterOrDigit(character) || character is '+' or '-' or '.';
    }

    private static bool IsUrlDelimiter(char character)
    {
        return character is '（' or '）' or '［' or '］' or '｛' or '｝'
            or '〈' or '〉' or '《' or '》' or '「' or '」' or '『' or '』'
            or '【' or '】' or '〔' or '〕' or '。' or '、' or '，' or '．'
            or '；' or '：' or '！' or '？';
    }

    private static bool IsJapaneseProseCharacter(char character)
    {
        return character is >= '\u3040' and <= '\u30ff'
            or >= '\u3400' and <= '\u4dbf'
            or >= '\u4e00' and <= '\u9fff'
            or >= '\uf900' and <= '\ufaff';
    }

    private static bool IsCharacterAtPoint(TextBox textBox, string text, int index, Point location)
    {
        if (index < 0 || index >= text.Length || char.IsWhiteSpace(text[index]))
        {
            return false;
        }

        Point start = textBox.GetPositionFromCharIndex(index);
        if (start.X < 0 || start.Y < 0 || location.Y < start.Y || location.Y >= start.Y + Math.Max(1, textBox.Font.Height))
        {
            return false;
        }

        int nextIndex = Math.Min(index + 1, text.Length);
        Point end = textBox.GetPositionFromCharIndex(nextIndex);
        int right = end.Y == start.Y && end.X > start.X
            ? end.X
            : start.X + Math.Max(1, TextRenderer.MeasureText(
                text[index].ToString(),
                textBox.Font,
                Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width);
        return location.X >= start.X && location.X < right;
    }

    internal static bool CanOpenWebLink(string? url)
    {
        return UrlValidationHelper.IsValidWebUrl(url);
    }

    private static void OpenWebLink(
        string? url,
        Action<string>? showStatusMessage,
        IWin32Window? dialogOwner,
        bool showErrorDialog,
        Action<string>? launchExternalUrl)
    {
        if (!CanOpenWebLink(url))
        {
            showStatusMessage?.Invoke("無効または安全ではないスキームのリンクのため起動をブロックしました。");
            return;
        }

        try
        {
            (launchExternalUrl ?? LaunchExternalUrl)(url!);
        }
        catch (Exception ex)
        {
            LogService.Error($"Failed to open URL '{url}': {ex.Message}");
            if (showErrorDialog)
            {
                MessageBox.Show(dialogOwner, $"リンクを開けませんでした: {ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private static void LaunchExternalUrl(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
