namespace MidFD.Helpers;

public static class BrowserPageIndex
{
    public static int ClampGlobalIndex(int globalIndex, int totalItemCount)
    {
        return totalItemCount <= 0 ? 0 : Math.Clamp(globalIndex, 0, totalItemCount - 1);
    }

    public static int GetPageStartForTotal(int globalIndex, int totalItemCount, int itemsPerPage)
    {
        if (totalItemCount <= 0 || itemsPerPage <= 0)
        {
            return 0;
        }
        return GetPageStart(ClampGlobalIndex(globalIndex, totalItemCount), itemsPerPage);
    }

    public static int GetPageStart(int globalIndex, int itemsPerPage)
    {
        if (globalIndex < 0 || itemsPerPage <= 0)
        {
            return 0;
        }
        return (globalIndex / itemsPerPage) * itemsPerPage;
    }

    public static int ToLocal(int globalIndex, int pageStartIndex, int pageItemCount)
    {
        int localIndex = globalIndex - pageStartIndex;
        return localIndex >= 0 && localIndex < pageItemCount ? localIndex : -1;
    }

    public static int ToGlobal(int pageLocalIndex, int pageStartIndex, int pageItemCount)
    {
        return pageLocalIndex >= 0 && pageLocalIndex < pageItemCount
            ? pageStartIndex + pageLocalIndex
            : -1;
    }
}
