using System;
using System.Windows.Forms;

namespace MidFD.Helpers;

/// <summary>
/// Browser 文脈の入力ルーティング順序だけを担当する。
/// 実際の挙動本体は MainForm 側の既存 Execute / TryHandle 群を再利用する。
/// </summary>
public sealed class BrowserInputRouter
{
    public static bool IsBrowserInputFocused(Control? browserPanel)
    {
        return browserPanel != null && (browserPanel.Focused || browserPanel.ContainsFocus);
    }

    public sealed class CmdKeyContext
    {
        public bool IsBrowserMode { get; init; }
        public bool IsBrowserFocused { get; init; }
        public bool IsAuxPreviewActive { get; init; }
        public bool CanUseCommandLauncherCommands { get; init; }

        public required Func<Keys, bool> TryHandleTabs { get; init; }
        public required Func<Keys, BrowserCommandBindingResolver.Resolution> TryHandleCustomBindings { get; init; }
        public required Action OpenMenuStripFromKeyboard { get; init; }
        public required Func<Keys, bool> TryHandleNavigation { get; init; }
        public required Func<Keys, bool> TryHandleFileOperationUndoRedo { get; init; }
        public required Func<Keys, bool> TryHandleMarking { get; init; }
        public required Func<Keys, bool> TryHandleClipboard { get; init; }
        public required Func<Keys, bool> TryHandleColumnCount { get; init; }
        public required Func<Keys, bool> TryHandleAliases { get; init; }
        public required Func<Keys, bool> TryHandleLaunch { get; init; }
        public required Func<Keys, bool> TryHandleCommandLauncher { get; init; }
    }

    public sealed class KeyDownContext
    {
        public bool IsBrowserMode { get; init; }
        public required Func<KeyEventArgs, bool> TryHandleCore { get; init; }
    }

    public bool TryHandleCmdKey(CmdKeyContext context, Keys keyData)
    {
        if (!context.IsBrowserMode)
        {
            return false;
        }

        if (keyData == Keys.F10 && !context.IsBrowserFocused)
        {
            context.OpenMenuStripFromKeyboard();
            return true;
        }

        bool acceptsBrowserShortcut = context.IsBrowserFocused || (keyData & Keys.Control) == Keys.Control;

        if (acceptsBrowserShortcut)
        {
            if (context.TryHandleCustomBindings(keyData) != BrowserCommandBindingResolver.Resolution.NotMatched)
            {
                return true;
            }
        }

        if (context.TryHandleTabs(keyData))
        {
            return true;
        }

        if (acceptsBrowserShortcut || context.IsAuxPreviewActive)
        {
            if (context.TryHandleNavigation(keyData))
            {
                return true;
            }
        }

        if (acceptsBrowserShortcut)
        {
            if (context.TryHandleFileOperationUndoRedo(keyData)) return true;
            if (context.TryHandleMarking(keyData)) return true;
            if (context.TryHandleClipboard(keyData)) return true;
            if (context.TryHandleColumnCount(keyData)) return true;
            if (context.TryHandleAliases(keyData)) return true;
            if (context.TryHandleLaunch(keyData)) return true;
        }

        if (context.CanUseCommandLauncherCommands && context.TryHandleCommandLauncher(keyData))
        {
            return true;
        }

        return false;
    }

    public bool TryHandleKeyDown(KeyDownContext context, KeyEventArgs e)
    {
        if (!context.IsBrowserMode)
        {
            return false;
        }

        return context.TryHandleCore(e);
    }
}
