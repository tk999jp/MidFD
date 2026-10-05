using System;
using System.Collections.Generic;
using System.Linq;
using MidFD.Helpers;
using MidFD.Models;

namespace MidFD.Services;

/// <summary>
/// タブ訪問履歴の動作モード。
/// </summary>
public enum BrowserTabVisitHistoryMode
{
    /// <summary>
    /// 内部処理や復元、クローズ時フォールバックなど、履歴に積まないデフォルトモード。
    /// </summary>
    Suppress,

    /// <summary>
    /// ユーザー起点の通常タブ移動（クリック、ショートカット移動、新規タブ等）を記録するモード。
    /// </summary>
    Record,

    /// <summary>
    /// タブ履歴の戻る/進む操作実行中モード（通常の移動記録を抑止し、Commitでスタック更新）。
    /// </summary>
    Replay
}

/// <summary>
/// ディレクトリ履歴とは独立した、タブ表示履歴（戻る/進む）を管理するセッションローカルサービス。
/// カテゴリをまたいで CategoryId + TabId を記録する。
/// </summary>
public sealed class BrowserTabVisitHistoryService
{
    private readonly List<BrowserTabVisitLocation> _backStack = new();
    private readonly List<BrowserTabVisitLocation> _forwardStack = new();
    private BrowserTabVisitLocation? _current;
    private BrowserTabVisitHistoryMode _mode = BrowserTabVisitHistoryMode.Suppress;

    public BrowserTabVisitHistoryMode Mode
    {
        get => _mode;
        set => _mode = value;
    }

    public BrowserTabVisitLocation? CurrentLocation => _current;

    public bool CanGoBack => _backStack.Count > 0;
    public bool CanGoForward => _forwardStack.Count > 0;

    public int BackCount => _backStack.Count;
    public int ForwardCount => _forwardStack.Count;

    public IReadOnlyList<BrowserTabVisitLocation> GetBackHistorySnapshot() => _backStack.ToList();
    public IReadOnlyList<BrowserTabVisitLocation> GetForwardHistorySnapshot() => _forwardStack.ToList();

    /// <summary>
    /// 表示用の Back 履歴を、現在位置に近い順で返す。
    /// </summary>
    public IReadOnlyList<BrowserTabVisitLocation> GetBackSnapshot() =>
        _backStack.AsEnumerable().Reverse().ToList();

    /// <summary>
    /// 表示用の Forward 履歴を、現在位置に近い順で返す。
    /// </summary>
    public IReadOnlyList<BrowserTabVisitLocation> GetForwardSnapshot() =>
        _forwardStack.AsEnumerable().Reverse().ToList();

    /// <summary>
    /// 現在存在しないカテゴリ／タブを、表示前に両スタックから除外する。
    /// </summary>
    public void PruneInvalidEntries(Func<BrowserTabVisitLocation, bool> validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        _backStack.RemoveAll(location => !validator(location));
        _forwardStack.RemoveAll(location => !validator(location));
    }

    public IDisposable BeginModeScope(BrowserTabVisitHistoryMode mode)
    {
        var previous = _mode;
        _mode = mode;
        return new ModeScope(this, previous);
    }

    private sealed class ModeScope : IDisposable
    {
        private readonly BrowserTabVisitHistoryService _service;
        private readonly BrowserTabVisitHistoryMode _previous;
        private bool _disposed;

        public ModeScope(BrowserTabVisitHistoryService service, BrowserTabVisitHistoryMode previous)
        {
            _service = service;
            _previous = previous;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _service._mode = _previous;
                _disposed = true;
            }
        }
    }

    /// <summary>
    /// 現在のタブ位置を同期する（履歴スタックは変更しない）。
    /// </summary>
    public void SyncCurrentLocation(BrowserTabVisitLocation location)
    {
        if (location.IsEmpty) return;
        _current = location;
    }

    /// <summary>
    /// 通常のタブ移動（Recordモード）を記録する。
    /// from != to のときのみ back に push し、forward をクリアする。
    /// </summary>
    public void RecordNavigation(BrowserTabVisitLocation from, BrowserTabVisitLocation to)
    {
        if (from.IsEmpty || to.IsEmpty) return;
        if (Equals(from, to)) return;

        if (_mode == BrowserTabVisitHistoryMode.Record)
        {
            PushBack(from);
            _forwardStack.Clear();
            _current = to;
        }
        else if (_mode == BrowserTabVisitHistoryMode.Suppress)
        {
            _current = to;
        }
    }

    private void PushBack(BrowserTabVisitLocation location)
    {
        if (_backStack.Count > 0 && Equals(_backStack[^1], location))
        {
            return;
        }

        _backStack.Add(location);

        if (_backStack.Count > HistoryHelper.MaxHistoryCount)
        {
            _backStack.RemoveAt(0);
        }
    }

    private void PushForward(BrowserTabVisitLocation location)
    {
        if (_forwardStack.Count > 0 && Equals(_forwardStack[^1], location))
        {
            return;
        }

        _forwardStack.Add(location);

        if (_forwardStack.Count > HistoryHelper.MaxHistoryCount)
        {
            _forwardStack.RemoveAt(0);
        }
    }

    /// <summary>
    /// 次に移動すべき有効な Back ターゲットを peek する。
    /// 無効なエントリはスタックから遅延除去（lazy prune）される。
    /// </summary>
    public bool TryPeekBack(Func<BrowserTabVisitLocation, bool> validator, out BrowserTabVisitLocation target)
    {
        while (_backStack.Count > 0)
        {
            var candidate = _backStack[^1];
            if (validator(candidate))
            {
                target = candidate;
                return true;
            }
            _backStack.RemoveAt(_backStack.Count - 1);
        }

        target = default;
        return false;
    }

    /// <summary>
    /// 次に移動すべき有効な Forward ターゲットを peek する。
    /// 無効なエントリはスタックから遅延除去（lazy prune）される。
    /// </summary>
    public bool TryPeekForward(Func<BrowserTabVisitLocation, bool> validator, out BrowserTabVisitLocation target)
    {
        while (_forwardStack.Count > 0)
        {
            var candidate = _forwardStack[^1];
            if (validator(candidate))
            {
                target = candidate;
                return true;
            }
            _forwardStack.RemoveAt(_forwardStack.Count - 1);
        }

        target = default;
        return false;
    }

    public bool TryGetBackAt(int depth, out BrowserTabVisitLocation target)
    {
        int index = _backStack.Count - 1 - depth;
        if (depth < 0 || index < 0)
        {
            target = default;
            return false;
        }

        target = _backStack[index];
        return true;
    }

    public bool TryGetForwardAt(int depth, out BrowserTabVisitLocation target)
    {
        int index = _forwardStack.Count - 1 - depth;
        if (depth < 0 || index < 0)
        {
            target = default;
            return false;
        }

        target = _forwardStack[index];
        return true;
    }

    /// <summary>
    /// 戻る操作が成功したときにスタック変更を確定する。
    /// </summary>
    public void CommitBack(BrowserTabVisitLocation current, BrowserTabVisitLocation target) =>
        CommitBack(current, target, 0);

    /// <summary>
    /// Back の指定位置へ移動し、飛び越した履歴を Forward に保持する。
    /// </summary>
    public void CommitBack(BrowserTabVisitLocation current, BrowserTabVisitLocation target, int depth)
    {
        int targetIndex = _backStack.Count - 1 - depth;
        if (depth < 0 || targetIndex < 0)
        {
            return;
        }

        if (!current.IsEmpty)
        {
            PushForward(current);
        }

        for (int index = _backStack.Count - 1; index > targetIndex; index--)
        {
            PushForward(_backStack[index]);
        }

        _backStack.RemoveRange(targetIndex, _backStack.Count - targetIndex);
        _current = target;
    }

    /// <summary>
    /// 進む操作が成功したときにスタック変更を確定する。
    /// </summary>
    public void CommitForward(BrowserTabVisitLocation current, BrowserTabVisitLocation target) =>
        CommitForward(current, target, 0);

    /// <summary>
    /// Forward の指定位置へ移動し、飛び越した履歴を Back に保持する。
    /// </summary>
    public void CommitForward(BrowserTabVisitLocation current, BrowserTabVisitLocation target, int depth)
    {
        int targetIndex = _forwardStack.Count - 1 - depth;
        if (depth < 0 || targetIndex < 0)
        {
            return;
        }

        if (!current.IsEmpty)
        {
            PushBack(current);
        }

        for (int index = _forwardStack.Count - 1; index > targetIndex; index--)
        {
            PushBack(_forwardStack[index]);
        }

        _forwardStack.RemoveRange(targetIndex, _forwardStack.Count - targetIndex);
        _current = target;
    }

    public void Clear()
    {
        _backStack.Clear();
        _forwardStack.Clear();
        _current = null;
    }
}
