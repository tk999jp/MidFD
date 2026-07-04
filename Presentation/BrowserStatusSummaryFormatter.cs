using MidFD.Models;

namespace MidFD.Presentation;

internal static class BrowserStatusSummaryFormatter
{
    public static string Format(BrowserStatusSummaryState state)
    {
        var parts = new List<string>
        {
            $"Mark: {state.MarkCount}",
            $"Select: {state.SelectionCount}"
        };

        if (!string.IsNullOrWhiteSpace(state.TargetText))
        {
            parts.Add(state.TargetText);
        }

        if (state.ClipboardMode != BrowserClipboardStatusMode.None && state.ClipboardCount > 0)
        {
            string mode = state.ClipboardMode == BrowserClipboardStatusMode.Cut ? "Cut" : "Copy";
            parts.Add($"Clipboard: {mode} {state.ClipboardCount}");
        }
        else if (state.CanPaste)
        {
            parts.Add("Clipboard: Paste可");
        }

        if (!string.IsNullOrWhiteSpace(state.DragStatusText))
        {
            parts.Add(state.DragStatusText!);
        }

        return string.Join(" / ", parts);
    }
}
