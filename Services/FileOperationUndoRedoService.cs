using MidFD.Models;

namespace MidFD.Services;

/// <summary>
/// current session 限定の file operation Undo / Redo practical subset を扱う。
/// 今回対象は Rename + Move + recycle-bin delete のみ。
/// </summary>
public sealed class FileOperationUndoRedoService
{
    private const int MaxBatchCount = 10;
    private readonly Stack<FileOperationUndoRedoBatch> _undoStack = new();
    private readonly Stack<FileOperationUndoRedoBatch> _redoStack = new();

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public void RecordBatch(FileOperationUndoRedoOperation operation, IEnumerable<FileOperationUndoRedoItem> items, bool isPartialCancellation = false)
    {
        var normalizedItems = NormalizeItems(operation, items);
        if (normalizedItems.Count == 0)
        {
            return;
        }

        _undoStack.Push(new FileOperationUndoRedoBatch
        {
            Operation = operation,
            Items = normalizedItems,
            IsPartialCancellation = isPartialCancellation
        });
        TrimStackToMax(_undoStack, MaxBatchCount);
        _redoStack.Clear();
    }

    public bool TryPeekUndo(out FileOperationUndoRedoBatch batch)
    {
        if (_undoStack.Count > 0)
        {
            batch = _undoStack.Peek();
            return true;
        }

        batch = new FileOperationUndoRedoBatch();
        return false;
    }

    public bool TryPeekRedo(out FileOperationUndoRedoBatch batch)
    {
        if (_redoStack.Count > 0)
        {
            batch = _redoStack.Peek();
            return true;
        }

        batch = new FileOperationUndoRedoBatch();
        return false;
    }

    public void CommitUndo()
    {
        if (_undoStack.Count == 0)
        {
            return;
        }

        FileOperationUndoRedoBatch popped = _undoStack.Pop();
        if (popped.Operation == FileOperationUndoRedoOperation.CreateFromPaste)
        {
            // 作成Undoは安全性優先でRedo対象にしない。
            return;
        }

        _redoStack.Push(popped);
        TrimStackToMax(_redoStack, MaxBatchCount);
    }

    public void CommitRedo()
    {
        if (_redoStack.Count == 0)
        {
            return;
        }

        _undoStack.Push(_redoStack.Pop());
        TrimStackToMax(_undoStack, MaxBatchCount);
    }

    public void Reset()
    {
        _undoStack.Clear();
        _redoStack.Clear();
    }

    public void ClearTrashDeleteBatches()
    {
        RemoveMatching(_undoStack, static batch => IsTrashDeleteOperation(batch.Operation));
        RemoveMatching(_redoStack, static batch => IsTrashDeleteOperation(batch.Operation));
    }

    public void PruneTrashDeleteItemsByRecycleBinPaths(IEnumerable<string> recycleBinPaths)
    {
        var pathSet = new HashSet<string>(
            recycleBinPaths.Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
        if (pathSet.Count == 0)
        {
            return;
        }

        PruneTrashDeleteItems(_undoStack, pathSet);
        PruneTrashDeleteItems(_redoStack, pathSet);
    }

    public static IReadOnlyList<FileOperationUndoRedoItem> CreateRenameBatch(IEnumerable<RenamePreviewItem> items)
    {
        return items
            .Where(item => item.WillRename)
            .Select(item => new FileOperationUndoRedoItem
            {
                BeforePath = item.SourcePath,
                AfterPath = item.DestinationPath,
                BeforeName = item.SourceName,
                AfterName = item.DestinationName
            })
            .ToList();
    }

    public static IReadOnlyList<FileOperationUndoRedoItem> CreateMoveBatch(IEnumerable<(string SourcePath, string DestinationPath)> items)
    {
        return items
            .Select(item => new FileOperationUndoRedoItem
            {
                BeforePath = item.SourcePath,
                AfterPath = item.DestinationPath,
                BeforeName = Path.GetFileName(item.SourcePath),
                AfterName = Path.GetFileName(item.DestinationPath)
            })
            .ToList();
    }

    public static IReadOnlyList<FileOperationUndoRedoItem> CreateDeleteToTrashBatch(IEnumerable<FileOperationUndoRedoItem> items)
    {
        return NormalizeItems(FileOperationUndoRedoOperation.DeleteToMidFdTrash, items);
    }

    public static IReadOnlyList<FileOperationUndoRedoItem> CreateCreatedFilesBatch(IEnumerable<string> paths)
    {
        var result = new List<FileOperationUndoRedoItem>();
        foreach (string? path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                continue;
            }

            try
            {
                var info = new FileInfo(path);
                result.Add(new FileOperationUndoRedoItem
                {
                    BeforePath = path,
                    BeforeName = Path.GetFileName(path),
                    CreatedFileLength = info.Length,
                    CreatedFileLastWriteTimeUtcTicks = info.LastWriteTimeUtc.Ticks
                });
            }
            catch
            {
                // 安全側: 状態が取れない項目はUndo記録しない。
            }
        }

        return NormalizeItems(FileOperationUndoRedoOperation.CreateFromPaste, result);
    }

    private static IReadOnlyList<FileOperationUndoRedoItem> NormalizeItems(IEnumerable<FileOperationUndoRedoItem> items)
    {
        return NormalizeItems(FileOperationUndoRedoOperation.Rename, items);
    }

    private static IReadOnlyList<FileOperationUndoRedoItem> NormalizeItems(
        FileOperationUndoRedoOperation operation,
        IEnumerable<FileOperationUndoRedoItem> items)
    {
        return items
            .Where(item => IsValidUndoRedoItem(operation, item))
            .Select(item => new FileOperationUndoRedoItem
            {
                BeforePath = item.BeforePath,
                AfterPath = item.AfterPath,
                BeforeName = item.BeforeName,
                AfterName = item.AfterName,
                RecycleBinPath = item.RecycleBinPath,
                RecycleBinDeletedAtUtc = item.RecycleBinDeletedAtUtc,
                CreatedFileLength = item.CreatedFileLength,
                CreatedFileLastWriteTimeUtcTicks = item.CreatedFileLastWriteTimeUtcTicks
            })
            .ToList();
    }

    private static bool IsValidUndoRedoItem(FileOperationUndoRedoOperation operation, FileOperationUndoRedoItem item)
    {
        if (string.IsNullOrWhiteSpace(item.BeforePath))
        {
            return false;
        }

        if (operation == FileOperationUndoRedoOperation.DeleteToMidFdTrash)
        {
            return !string.IsNullOrWhiteSpace(item.RecycleBinPath);
        }

        if (operation == FileOperationUndoRedoOperation.CreateFromPaste)
        {
            return !string.IsNullOrWhiteSpace(item.BeforePath) &&
                   item.CreatedFileLength >= 0 &&
                   item.CreatedFileLastWriteTimeUtcTicks > 0;
        }

        return
            !string.IsNullOrWhiteSpace(item.AfterPath) &&
            !string.Equals(item.BeforePath, item.AfterPath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTrashDeleteOperation(FileOperationUndoRedoOperation operation)
    {
        return operation == FileOperationUndoRedoOperation.DeleteToMidFdTrash;
    }

    private static void RemoveMatching(
        Stack<FileOperationUndoRedoBatch> stack,
        Func<FileOperationUndoRedoBatch, bool> predicate)
    {
        var preserved = stack
            .Where(batch => !predicate(batch))
            .Reverse()
            .ToList();
        stack.Clear();
        foreach (FileOperationUndoRedoBatch batch in preserved)
        {
            stack.Push(batch);
        }
    }

    private static void PruneTrashDeleteItems(Stack<FileOperationUndoRedoBatch> stack, HashSet<string> recycleBinPaths)
    {
        var preserved = new List<FileOperationUndoRedoBatch>();
        foreach (FileOperationUndoRedoBatch batch in stack.Reverse())
        {
            if (batch.Operation != FileOperationUndoRedoOperation.DeleteToMidFdTrash)
            {
                preserved.Add(batch);
                continue;
            }

            var filteredItems = batch.Items
                .Where(item => string.IsNullOrWhiteSpace(item.RecycleBinPath) || !recycleBinPaths.Contains(item.RecycleBinPath))
                .ToList();

            if (filteredItems.Count == 0)
            {
                continue;
            }

            preserved.Add(new FileOperationUndoRedoBatch
            {
                Operation = batch.Operation,
                Items = filteredItems,
                IsPartialCancellation = batch.IsPartialCancellation
            });
        }

        stack.Clear();
        foreach (FileOperationUndoRedoBatch batch in preserved)
        {
            stack.Push(batch);
        }
    }

    private static void TrimStackToMax(Stack<FileOperationUndoRedoBatch> stack, int maxCount)
    {
        if (maxCount <= 0 || stack.Count <= maxCount)
        {
            return;
        }

        var preserved = stack
            .Reverse()
            .Skip(stack.Count - maxCount)
            .ToList();
        stack.Clear();
        foreach (FileOperationUndoRedoBatch batch in preserved)
        {
            stack.Push(batch);
        }
    }
}
