namespace MidFD.Helpers;

internal enum BrowserMarkClickKind
{
    None,
    NormalClick,
    ToggleSingle,
    AddRange,
    PromotePendingAndToggleSingle
}
internal readonly record struct BrowserMarkClickDecision(
    BrowserMarkClickKind Kind,
    int AnchorIndex,
    int PendingPromotionIndex = -1);

internal sealed class BrowserMarkInteractionController
{
    private int _anchorIndex = -1;
    private int _pendingPromotionIndex = -1;

    public BrowserMarkClickDecision ResolveLeftClick(
        int clickedIndex,
        int currentCursorIndex,
        int itemCount,
        bool ctrlPressed,
        bool shiftPressed,
        bool hasExistingMarks)
    {
        if (clickedIndex < 0 || clickedIndex >= itemCount)
        {
            return new BrowserMarkClickDecision(BrowserMarkClickKind.None, _anchorIndex);
        }

        if (shiftPressed)
        {
            _pendingPromotionIndex = -1;
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
            if (!hasExistingMarks
                && _pendingPromotionIndex >= 0
                && _pendingPromotionIndex < itemCount
                && _pendingPromotionIndex != clickedIndex)
            {
                int promotionIndex = _pendingPromotionIndex;
                _pendingPromotionIndex = -1;
                return new BrowserMarkClickDecision(BrowserMarkClickKind.PromotePendingAndToggleSingle, clickedIndex, promotionIndex);
            }

            _pendingPromotionIndex = -1;
            return new BrowserMarkClickDecision(BrowserMarkClickKind.ToggleSingle, clickedIndex);
        }

        _pendingPromotionIndex = clickedIndex;
        return new BrowserMarkClickDecision(BrowserMarkClickKind.NormalClick, clickedIndex);
    }

    public void ClearPendingPromotionCandidate()
    {
        _pendingPromotionIndex = -1;
    }

    public void SyncMarkState(bool hasMarks)
    {
        if (hasMarks)
        {
            _pendingPromotionIndex = -1;
        }
    }
}
