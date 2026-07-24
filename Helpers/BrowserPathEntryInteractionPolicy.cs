namespace MidFD.Helpers;

internal static class BrowserPathEntryInteractionPolicy
{
    public static bool ShouldDismissForBrowserClick(
        bool editorActive,
        bool clickInsideInput,
        bool clickInsideGoButton,
        bool clickInsidePopup)
    {
        return editorActive && !clickInsideInput && !clickInsideGoButton && !clickInsidePopup;
    }
}
