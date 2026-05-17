using System;
using System.IO;

namespace MidFD.Helpers;

/// <summary>
/// Browser 文脈の「どこへ移動するか」という request と、
/// その request を MainForm 側の既存 LoadDirectory 導線へ渡す前後の最小 orchestration を担当する。
/// </summary>
public sealed class BrowserNavigationCoordinator
{
    public sealed class DirectoryNavigationRequest
    {
        public required string TargetPath { get; init; }
        public string? FocusTargetName { get; init; }
        public bool IsHistoryNavigation { get; init; }
        public bool SuppressRecent { get; init; }
    }

    public sealed class ExecutionContext
    {
        public required Func<string, bool> PrepareUnlockedTabForLocationChange { get; init; }
        public required Func<string, string?, bool, bool, bool> LoadDirectory { get; init; }
        public Action? OnNavigationSucceeded { get; init; }
        public Action<string>? OnDirectoryMissing { get; init; }
    }

    public DirectoryNavigationRequest? CreateParentNavigationRequest(string currentPath)
    {
        var parent = Directory.GetParent(currentPath);
        if (parent == null)
        {
            return null;
        }

        string currentDirectoryName = new DirectoryInfo(currentPath).Name;
        return new DirectoryNavigationRequest
        {
            TargetPath = parent.FullName,
            FocusTargetName = currentDirectoryName,
            IsHistoryNavigation = false,
            SuppressRecent = false
        };
    }

    public DirectoryNavigationRequest CreateDirectoryNavigationRequest(
        string targetPath,
        string? focusTargetName = null,
        bool isHistoryNavigation = false,
        bool suppressRecent = false)
    {
        return new DirectoryNavigationRequest
        {
            TargetPath = targetPath,
            FocusTargetName = focusTargetName,
            IsHistoryNavigation = isHistoryNavigation,
            SuppressRecent = suppressRecent
        };
    }

    public bool Execute(DirectoryNavigationRequest? request, ExecutionContext context)
    {
        if (request == null)
        {
            return false;
        }

        if (!Directory.Exists(request.TargetPath))
        {
            context.OnDirectoryMissing?.Invoke(request.TargetPath);
            return false;
        }

        if (!context.PrepareUnlockedTabForLocationChange(request.TargetPath))
        {
            return true;
        }

        bool loaded = context.LoadDirectory(
            request.TargetPath,
            request.FocusTargetName,
            request.IsHistoryNavigation,
            request.SuppressRecent);

        if (loaded)
        {
            context.OnNavigationSucceeded?.Invoke();
        }

        return loaded;
    }
}
