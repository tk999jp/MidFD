using System;
using System.Collections.Generic;

namespace MidFD.Models;

public static class MarkSlotSetOperations
{
    public const string Or = "Or";
    public const string And = "And";
    public const string AMinusB = "AMinusB";
    public const string BMinusA = "BMinusA";
    public const string Xor = "Xor";
}

public sealed record MarkSlotSetOperationPreviewItem(
    string Name,
    string FullPath,
    bool IsInCurrentDirectory,
    bool Exists);

public sealed record MarkSlotSetOperationPreviewResult(
    int SlotANumber,
    string SlotADisplayName,
    int SlotACount,
    int SlotBNumber,
    string SlotBDisplayName,
    int SlotBCount,
    string OperationKind,
    string OperationLabel,
    IReadOnlyList<string> ResultPaths,
    IReadOnlyList<MarkSlotSetOperationPreviewItem> PreviewItems,
    int CurrentDirectoryCount,
    int OutsideCount,
    int MissingCount)
{
    public int ResultCount => ResultPaths.Count;
}

public sealed record MarkSlotSetOperationSaveRequest(
    int TargetSlotNumber,
    int SlotANumber,
    int SlotBNumber,
    string OperationKind,
    IReadOnlyList<string> ResultPaths);
