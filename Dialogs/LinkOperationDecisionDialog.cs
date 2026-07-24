using MidFD.Services;

namespace MidFD.Dialogs;

internal enum LinkOperationDecision
{
    Preserve,
    Skip,
    Cancel
}

internal static class LinkOperationDecisionDialog
{
    public static LinkOperationDecision Show(IWin32Window owner, LinkOperationPlan plan)
    {
        string message =
            $"リンクを含む操作です。\r\n\r\n" +
            $"ファイルシンボリックリンク: {plan.FileSymbolicLinkCount}\r\n" +
            $"ディレクトリシンボリックリンク: {plan.DirectorySymbolicLinkCount}\r\n" +
            $"junction: {plan.JunctionCount}\r\n" +
            $"unsupported: {plan.UnsupportedCount}\r\n\r\n" +
            "保持を選ぶと、MidFD本体ではなく専用helperだけが一時的に管理者権限を使用します。\r\n" +
            "リンク先を通常fileへ変換することはありません。\r\n\r\n" +
            "[はい] リンクを保持して続行\r\n[いいえ] リンクをスキップして続行\r\n[キャンセル] 操作を中止";
        return MessageBox.Show(owner, message, "リンクを含む操作", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning) switch
        {
            DialogResult.Yes => LinkOperationDecision.Preserve,
            DialogResult.No => LinkOperationDecision.Skip,
            _ => LinkOperationDecision.Cancel
        };
    }
}
