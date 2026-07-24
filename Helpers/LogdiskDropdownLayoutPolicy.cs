namespace MidFD.Helpers;

internal static class LogdiskDropdownLayoutPolicy
{
    public const int MaxVisibleRows = 10;

    public static bool IsNativeHistoryShortcut(Keys keyData)
    {
        Keys keyCode = keyData & Keys.KeyCode;
        return keyCode == Keys.Down && (keyData & (Keys.Control | Keys.Alt)) != Keys.None;
    }

    public static int CalculateDropDownHeight(int itemHeight, int workingAreaBottom, int comboBottomScreenY)
    {
        int safeItemHeight = Math.Max(1, itemHeight);
        int desired = safeItemHeight * MaxVisibleRows + 2;
        int available = Math.Max(safeItemHeight, workingAreaBottom - comboBottomScreenY - 2);
        return Math.Min(desired, available);
    }
}
