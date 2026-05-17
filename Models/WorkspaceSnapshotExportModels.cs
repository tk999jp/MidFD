using System;
using System.Collections.Generic;
using MidFD.Configuration;
using MidFD.Services.Workspace;

namespace MidFD.Models;

/// <summary>
/// 単一スナップショットのエクスポート形式。
/// </summary>
public sealed class WorkspaceSnapshotExportFile
{
    public string Kind { get; set; } = "MidFD.WorkspaceSnapshot";
    public int SchemaVersion { get; set; } = 1;
    public DateTime ExportedAtUtc { get; set; } = DateTime.UtcNow;

    public WorkspaceSnapshotMetadata? Metadata { get; set; }
    public WorkspaceState? Payload { get; set; }
}

/// <summary>
/// スナップショット一括バックアップの形式。
/// </summary>
public sealed class WorkspaceSnapshotBackupSetFile
{
    public string Kind { get; set; } = "MidFD.WorkspaceSnapshotBackupSet";
    public int SchemaVersion { get; set; } = 1;
    public DateTime ExportedAtUtc { get; set; } = DateTime.UtcNow;

    public List<WorkspaceSnapshotExportFile> Snapshots { get; set; } = new();
}

public sealed class WorkspaceSnapshotMetadata
{
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string? Summary { get; set; }
}
