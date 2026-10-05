using System.Collections.Generic;

namespace MidFD.Models;

/// <summary>
/// 外部ツール定義の永続化用コンテナ。
/// </summary>
public sealed class ExternalToolCommandStore
{
    internal bool LoadFailed { get; set; }
    public int SchemaVersion { get; set; } = 1;
    public List<ExternalToolCommandDefinition> Tools { get; set; } = new();
}
