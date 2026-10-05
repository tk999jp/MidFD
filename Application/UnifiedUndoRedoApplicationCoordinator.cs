using System;
using MidFD.Models;
using MidFD.Services;

namespace MidFD.Runtime;

internal enum UnifiedUndoRedoDomain
{
    File,
    Tab
}

internal readonly record struct UnifiedUndoRedoEntry(
    UnifiedUndoRedoDomain Domain,
    Guid OperationId);

/// <summary>
/// File操作と複数タブOpenのsession-localな時系列を統合する。
/// 各domainの安全判定・実処理は既存coordinatorへ委譲する。
/// </summary>
internal sealed class UnifiedUndoRedoApplicationCoordinator
{
    private readonly FileOperationUndoRedoService _fileUndoRedo;
    private readonly BrowserTabWorkflowApplicationCoordinator _tabs;
    private readonly Stack<UnifiedUndoRedoEntry> _undo = new();
    private readonly Stack<UnifiedUndoRedoEntry> _redo = new();

    public UnifiedUndoRedoApplicationCoordinator(
        FileOperationUndoRedoService fileUndoRedo,
        BrowserTabWorkflowApplicationCoordinator tabs)
    {
        _fileUndoRedo = fileUndoRedo;
        _tabs = tabs;
        _fileUndoRedo.BatchRecorded += OnFileBatchRecorded;
        _fileUndoRedo.UndoRedoCommitted += OnFileUndoRedoCommitted;
        _fileUndoRedo.HistoryChanged += OnFileHistoryChanged;
    }

    public bool CanUndo => TryPeek(_undo, undo: true, out _, out _);
    public bool CanRedo => TryPeek(_redo, undo: false, out _, out _);

    public bool TryPeekUndo(out UnifiedUndoRedoEntry entry, out string? reason) =>
        TryPeek(_undo, undo: true, out entry, out reason);

    public bool TryPeekRedo(out UnifiedUndoRedoEntry entry, out string? reason) =>
        TryPeek(_redo, undo: false, out entry, out reason);

    public void RecordTab(Guid operationId)
    {
        if (operationId == Guid.Empty)
        {
            return;
        }

        _undo.Push(new UnifiedUndoRedoEntry(UnifiedUndoRedoDomain.Tab, operationId));
        _redo.Clear();
    }

    public bool CommitTabUndo(Guid operationId)
    {
        if (!TryCommitTop(_undo, UnifiedUndoRedoDomain.Tab, operationId))
        {
            return false;
        }

        _redo.Push(new UnifiedUndoRedoEntry(UnifiedUndoRedoDomain.Tab, operationId));
        return true;
    }

    public bool CommitTabRedo(Guid previousOperationId, Guid newOperationId)
    {
        if (!TryCommitTop(_redo, UnifiedUndoRedoDomain.Tab, previousOperationId) ||
            newOperationId == Guid.Empty)
        {
            return false;
        }

        _undo.Push(new UnifiedUndoRedoEntry(UnifiedUndoRedoDomain.Tab, newOperationId));
        return true;
    }

    private void OnFileBatchRecorded(FileOperationUndoRedoBatch batch)
    {
        if (batch.OperationId == Guid.Empty)
        {
            return;
        }

        _undo.Push(new UnifiedUndoRedoEntry(UnifiedUndoRedoDomain.File, batch.OperationId));
        _redo.Clear();
    }

    private void OnFileUndoRedoCommitted(FileOperationUndoRedoBatch batch, bool undo)
    {
        UnifiedUndoRedoDomain domain = UnifiedUndoRedoDomain.File;
        Stack<UnifiedUndoRedoEntry> source = undo ? _undo : _redo;
        if (!TryCommitTop(source, domain, batch.OperationId))
        {
            return;
        }

        if (undo)
        {
            if (batch.Operation != FileOperationUndoRedoOperation.CreateFromPaste)
            {
                _redo.Push(new UnifiedUndoRedoEntry(domain, batch.OperationId));
            }
        }
        else
        {
            _undo.Push(new UnifiedUndoRedoEntry(domain, batch.OperationId));
        }
    }

    private void OnFileHistoryChanged()
    {
        RemoveInvalidFileEntries(_undo);
        RemoveInvalidFileEntries(_redo);
    }

    private void RemoveInvalidFileEntries(Stack<UnifiedUndoRedoEntry> stack)
    {
        UnifiedUndoRedoEntry[] preserved = stack
            .Where(entry => entry.Domain != UnifiedUndoRedoDomain.File ||
                            _fileUndoRedo.ContainsOperation(entry.OperationId))
            .Reverse()
            .ToArray();
        stack.Clear();
        foreach (UnifiedUndoRedoEntry entry in preserved)
        {
            stack.Push(entry);
        }
    }

    private bool TryPeek(
        Stack<UnifiedUndoRedoEntry> stack,
        bool undo,
        out UnifiedUndoRedoEntry entry,
        out string? reason)
    {
        if (stack.Count == 0)
        {
            entry = default;
            reason = undo ? "元に戻せる操作がありません。" : "やり直せる操作がありません。";
            return false;
        }

        entry = stack.Peek();
        bool valid = entry.Domain switch
        {
            UnifiedUndoRedoDomain.File => (undo
                ? _fileUndoRedo.TryPeekUndo(out FileOperationUndoRedoBatch fileBatch)
                : _fileUndoRedo.TryPeekRedo(out fileBatch)) &&
                fileBatch.OperationId == entry.OperationId,
            UnifiedUndoRedoDomain.Tab => undo
                ? _tabs.HasTabBatchUndo(entry.OperationId)
                : _tabs.HasTabBatchRedo(entry.OperationId),
            _ => false
        };
        if (valid)
        {
            reason = null;
            return true;
        }

        reason = undo
            ? "直前の操作を元に戻せない状態です。"
            : "直前の操作をやり直せない状態です。";
        return false;
    }

    private static bool TryCommitTop(
        Stack<UnifiedUndoRedoEntry> stack,
        UnifiedUndoRedoDomain domain,
        Guid operationId)
    {
        if (stack.Count == 0 ||
            stack.Peek().Domain != domain ||
            stack.Peek().OperationId != operationId)
        {
            return false;
        }

        stack.Pop();
        return true;
    }
}
