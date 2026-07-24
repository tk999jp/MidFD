using System.IO;
using MidFD.Models;

namespace MidFD.Services;

public static class DirectoryWatcherNotifyFilterPolicy
{
    public static NotifyFilters ForSort(SortKind sortKind)
    {
        NotifyFilters filters = NotifyFilters.FileName |
            NotifyFilters.DirectoryName |
            NotifyFilters.LastWrite |
            NotifyFilters.Size |
            NotifyFilters.Attributes;

        return sortKind switch
        {
            SortKind.DateCreated => filters | NotifyFilters.CreationTime,
            SortKind.DateAccessed => filters | NotifyFilters.LastAccess,
            _ => filters
        };
    }
}
