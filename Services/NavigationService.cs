using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MidFD.Helpers;

namespace MidFD.Services;

/// <summary>
/// ナビゲーションの状態（現在地、履歴、ドライブ別記憶）を管理するサービス。
/// UI 更新自体の責務は持たず、状態遷移の判断とパス解決に特化する。
/// </summary>
public class NavigationService
{
    public sealed class NavigationSnapshot
    {
        public string CurrentPath { get; init; } = string.Empty;
        public IReadOnlyList<string> BackHistory { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> ForwardHistory { get; init; } = Array.Empty<string>();
        public IReadOnlyDictionary<char, string> LastVisitedPathByDrive { get; init; } = new Dictionary<char, string>();
    }

    private string _currentPath = string.Empty;
    private readonly Stack<string> _backHistory = new();
    private readonly Stack<string> _forwardHistory = new();
    private readonly Dictionary<char, string> _lastVisitedPathByDrive = new();
    private bool _isNavigatingHistory = false;

    public string CurrentPath => _currentPath;

    public bool CanGoBack => _backHistory.Count > 0;
    public bool CanGoForward => _forwardHistory.Count > 0;

    /// <summary>
    /// 現在地を更新し、必要に応じて履歴を管理する。
    /// </summary>
    /// <param name="path">新しいパス</param>
    /// <param name="isHistoryNavigation">履歴移動（戻る/進む）による更新かどうか</param>
    public void SetCurrentPath(string path, bool isHistoryNavigation = false)
    {
        if (string.IsNullOrEmpty(path)) return;

        // 履歴移動中でなく、かつパスが実際に変わる場合のみ履歴に積む
        if (!isHistoryNavigation && !_isNavigatingHistory && !string.IsNullOrEmpty(_currentPath) &&
            !string.Equals(_currentPath, path, StringComparison.OrdinalIgnoreCase))
        {
            PushBack(_currentPath);
            _forwardHistory.Clear();
        }

        _currentPath = path;
        RememberDrivePath(_currentPath);
    }

    public string? PeekBack() => CanGoBack ? _backHistory.Peek() : null;
    public string? PeekForward() => CanGoForward ? _forwardHistory.Peek() : null;
    public IReadOnlyList<string> GetBackHistorySnapshot() => _backHistory.ToList();
    public IReadOnlyList<string> GetForwardHistorySnapshot() => _forwardHistory.ToList();

    private void PopBack() => _backHistory.Pop();
    private void PopForward() => _forwardHistory.Pop();

    private void PushBack(string path) => PushWithCap(_backHistory, path);
    private void PushForward(string path) => PushWithCap(_forwardHistory, path);

    private void PushWithCap(Stack<string> stack, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        // 連続する重複を避ける
        if (stack.Count > 0 && string.Equals(stack.Peek(), path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        stack.Push(path);

        // 上限を超えた場合は古いものを切り捨てる
        if (stack.Count > HistoryHelper.MaxHistoryCount)
        {
            var normalized = HistoryHelper.Normalize(stack);
            stack.Clear();
            foreach (var item in normalized.AsEnumerable().Reverse())
            {
                stack.Push(item);
            }
        }
    }

    /// <summary>
    /// 戻る操作を確定する。
    /// </summary>
    /// <param name="previousPath">移動前のパス（進む履歴に積む用）</param>
    public void CommitBack(string previousPath)
    {
        if (!CanGoBack) return;
        _backHistory.Pop();
        PushForward(previousPath);
    }

    /// <summary>
    /// 進む操作を確定する。
    /// </summary>
    /// <param name="previousPath">移動前のパス（戻る履歴に積む用）</param>
    public void CommitForward(string previousPath)
    {
        if (!CanGoForward) return;
        _forwardHistory.Pop();
        PushBack(previousPath);
    }

    /// <summary>
    /// 履歴移動モードに入る（旧 MainForm 互換用。現在は SetCurrentPath の引数利用を推奨）。
    /// </summary>
    public void EnterHistoryNavigation() => _isNavigatingHistory = true;

    /// <summary>
    /// 履歴移動モードを抜ける。
    /// </summary>
    public void ExitHistoryNavigation() => _isNavigatingHistory = false;

    public NavigationSnapshot CaptureState()
    {
        return new NavigationSnapshot
        {
            CurrentPath = _currentPath,
            BackHistory = _backHistory.ToList(),
            ForwardHistory = _forwardHistory.ToList(),
            LastVisitedPathByDrive = new Dictionary<char, string>(_lastVisitedPathByDrive)
        };
    }

    public void RestoreState(NavigationSnapshot? snapshot)
    {
        _currentPath = snapshot?.CurrentPath ?? string.Empty;

        _backHistory.Clear();
        if (snapshot?.BackHistory != null)
        {
            foreach (string path in snapshot.BackHistory.Reverse())
            {
                _backHistory.Push(path);
            }
        }

        _forwardHistory.Clear();
        if (snapshot?.ForwardHistory != null)
        {
            foreach (string path in snapshot.ForwardHistory.Reverse())
            {
                _forwardHistory.Push(path);
            }
        }

        _lastVisitedPathByDrive.Clear();
        if (snapshot?.LastVisitedPathByDrive != null)
        {
            foreach (var pair in snapshot.LastVisitedPathByDrive)
            {
                _lastVisitedPathByDrive[pair.Key] = pair.Value;
            }
        }
    }

    /// <summary>
    /// ドライブごとの最終訪問ディレクトリを記憶する。
    /// </summary>
    public void RememberDrivePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        string? root = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(root)) return;

        char drive = char.ToLowerInvariant(root[0]);
        if (!IsAsciiLetter(drive)) return;

        _lastVisitedPathByDrive[drive] = path;
    }

    /// <summary>
    /// "c" や "c:" などの入力を、そのドライブの最終訪問先またはルートパスとして解決する。
    /// </summary>
    public string ResolveDriveShortcutOrPath(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        string trimmed = input.Trim();

        // "c" または "c:" 形式をドライブショートカットとみなす
        if ((trimmed.Length == 1 && IsAsciiLetter(trimmed[0])) ||
            (trimmed.Length == 2 && IsAsciiLetter(trimmed[0]) && trimmed[1] == ':'))
        {
            char drive = char.ToLowerInvariant(trimmed[0]);

            if (_lastVisitedPathByDrive.TryGetValue(drive, out var rememberedPath) &&
                !string.IsNullOrWhiteSpace(rememberedPath) &&
                Directory.Exists(rememberedPath))
            {
                return rememberedPath;
            }

            return $"{char.ToUpperInvariant(drive)}:\\";
        }

        return trimmed;
    }

    /// <summary>
    /// パス比較用に末尾の区切り文字を除去するなどの正規化を行う。
    /// </summary>
    public static string NormalizeDirectoryForCompare(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path;
        }
    }

    /// <summary>
    /// 指定されたパスを、現在地やドライブショートカットを考慮して正規化する。
    /// </summary>
    public string NormalizeDestinationDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

        string resolved = ResolveDriveShortcutOrPath(path);

        try
        {
            // 相対パスは _currentPath をベースに解決、絶対パスはそのまま正規化
            string combined = Path.IsPathRooted(resolved) ? resolved : Path.Combine(_currentPath, resolved);
            string fullPath = Path.GetFullPath(combined);

            // ドライブルートの場合は末尾の \ を削らない
            if (fullPath.Length <= 3 && fullPath.EndsWith(":\\"))
            {
                return fullPath;
            }

            // それ以外の通常パスは、文字列比較や結合時のズレを防ぐため、末尾の区切り文字を削って統一
            return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return resolved; // 異常時は解決済みの値を返す
        }
    }

    private static bool IsAsciiLetter(char c)
    {
        return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
    }
}
