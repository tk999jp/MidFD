namespace MidFD.Models;

/// <summary>Runtime presentation group for normal Browser tabs.</summary>
public sealed record BrowserTabGroup(
    Guid Id,
    string CategoryId,
    string DisplayName,
    IReadOnlyList<Guid> MemberTabIds,
    bool Expanded,
    long CreationOrder);
