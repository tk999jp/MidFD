using MidFD.Models;

namespace MidFD.Presentation;

/// <summary>
/// Browser status summaryの表示モデルを組み立てるapplication/presentation境界。
/// Clipboardなどのshell状態は呼び出し側で解決し、ここでは表示意味だけを確定する。
/// </summary>
internal static class BrowserStatusSummaryProjection
{
    public static BrowserStatusSummaryState Build(
        int markCount,
        SelectionResult selection,
        BrowserClipboardStatusMode clipboardMode,
        int clipboardCount,
        bool canPaste,
        string? dragStatusText)
    {
        string targetText = selection.Count == 0
            ? "Target: none"
            : selection.HasMarkedSelection ? "Target: mark" : "Target: select";

        return new BrowserStatusSummaryState
        {
            MarkCount = markCount,
            SelectionCount = selection.Count,
            TargetText = targetText,
            ClipboardMode = clipboardMode,
            ClipboardCount = clipboardCount,
            CanPaste = canPaste,
            DragStatusText = dragStatusText
        };
    }
}
