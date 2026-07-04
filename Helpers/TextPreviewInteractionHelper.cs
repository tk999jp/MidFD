using System.Diagnostics;
using MidFD.Services;

namespace MidFD.Helpers;

internal static class TextPreviewInteractionHelper
{
    public static void Attach(
        RichTextBox textBox,
        Action<string>? showStatusMessage = null,
        IWin32Window? dialogOwner = null,
        bool showErrorDialog = false,
        Func<string?, string?>? resolveClickedUrl = null)
    {
        if (textBox == null) throw new ArgumentNullException(nameof(textBox));

        textBox.ReadOnly = true;
        textBox.DetectUrls = true;
        textBox.ShortcutsEnabled = true;

        textBox.LinkClicked += (_, e) =>
        {
            string? url = resolveClickedUrl?.Invoke(e.LinkText) ?? e.LinkText;
            OpenWebLink(url, showStatusMessage, dialogOwner, showErrorDialog);
        };
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

    internal static bool CanOpenWebLink(string? url)
    {
        return UrlValidationHelper.IsValidWebUrl(url);
    }

    private static void OpenWebLink(
        string? url,
        Action<string>? showStatusMessage,
        IWin32Window? dialogOwner,
        bool showErrorDialog)
    {
        if (!CanOpenWebLink(url))
        {
            showStatusMessage?.Invoke("無効または安全ではないスキームのリンクのため起動をブロックしました。");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url!) { UseShellExecute = true });
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
}
