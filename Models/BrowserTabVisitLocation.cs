using System;

namespace MidFD.Models;

/// <summary>
/// タブ訪問履歴のエントリ識別子。
/// カテゴリ横断で特定のタブを一意に特定するため、CategoryId と TabId のペアで保持する。
/// インデックスや表示名には依存しない。
/// </summary>
public readonly record struct BrowserTabVisitLocation(string CategoryId, Guid TabId)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(CategoryId) || TabId == Guid.Empty;
}
