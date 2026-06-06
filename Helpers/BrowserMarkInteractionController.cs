namespace MidFD.Helpers;

internal enum BrowserMarkClickKind
{
    None,
    NormalClick,
    ToggleSingle,
    AddRange
}
internal readonly record struct BrowserMarkClickDecision(
    BrowserMarkClickKind Kind,
    int AnchorIndex);

internal sealed class BrowserMarkInteractionController
{
    private int _anchorIndex = -1;

    public BrowserMarkClickDecision ResolveLeftClick(
        int clickedIndex,
        int currentCursorIndex,
        int itemCount,
        bool ctrlPressed,
        bool shiftPressed)
    {
        if (clickedIndex < 0 || clickedIndex >= itemCount)
        {
            return new BrowserMarkClickDecision(BrowserMarkClickKind.None, _anchorIndex);
        }

        if (shiftPressed)
        {
            int effectiveAnchor = _anchorIndex >= 0 && _anchorIndex < itemCount
                ? _anchorIndex
                : (currentCursorIndex >= 0 && currentCursorIndex < itemCount
                    ? currentCursorIndex
                    : clickedIndex);

            _anchorIndex = effectiveAnchor;
            return new BrowserMarkClickDecision(BrowserMarkClickKind.AddRange, effectiveAnchor);
        }

        _anchorIndex = clickedIndex;
        if (ctrlPressed)
        {
            return new BrowserMarkClickDecision(BrowserMarkClickKind.ToggleSingle, clickedIndex);
        }

        return new BrowserMarkClickDecision(BrowserMarkClickKind.NormalClick, clickedIndex);
    }
}
