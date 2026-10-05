namespace MidFD.Models;

public enum ContentSearchEncodingMode { Auto, Utf8, ShiftJis, Utf16Le, Utf16Be }

public enum ContentSearchBackendKind { Ripgrep, Internal }
public sealed record ContentSearchBackendInfo(ContentSearchBackendKind Kind, string DisplayName, string? Version = null)
{
    public static ContentSearchBackendInfo Internal { get; } = new(ContentSearchBackendKind.Internal, "内蔵検索");
}

public sealed record ContentSearchRequest(
    string RootPath,
    UnifiedFilterFindCriteria Criteria,
    ContentSearchEncodingMode Encoding = ContentSearchEncodingMode.Auto)
{
    public Action<ContentSearchBackendInfo>? BackendSelected { get; init; }
}

public sealed record ContentSearchBatch(IReadOnlyList<UnifiedSearchMatch> Matches);
