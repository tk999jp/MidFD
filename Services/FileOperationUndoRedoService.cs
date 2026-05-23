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

        _redoStack.Push(_undoStack.Pop());
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
                RecycleBinDeletedAtUtc = item.RecycleBinDeletedAtUtc
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
