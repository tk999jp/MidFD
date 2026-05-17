using System;
using System.Windows.Forms;

namespace MidFD.Helpers;

/// <summary>
/// Viewer 文脈の入力ルーティング順序だけを担当する。
/// 実際の挙動本体は MainForm 側の既存 Execute / TryHandle 群を再利用する。
/// </summary>
public sealed class ViewerInputRouter
{
    public sealed class CmdKeyContext
    {
        public bool IsViewerMode { get; init; }
        public required Func<Keys, bool> TryHandleCore { get; init; }
    }

    public sealed class KeyDownContext
    {
        public bool IsViewerMode { get; init; }
        public required Func<KeyEventArgs, bool> TryHandleCore { get; init; }
    }

    public bool TryHandleCmdKey(CmdKeyContext context, Keys keyData)
    {
        if (!context.IsViewerMode)
        {
            return false;
        }

        return context.TryHandleCore(keyData);
    }

    public bool TryHandleKeyDown(KeyDownContext context, KeyEventArgs e)
    {
        if (!context.IsViewerMode)
        {
            return false;
        }

        return context.TryHandleCore(e);
    }
}
