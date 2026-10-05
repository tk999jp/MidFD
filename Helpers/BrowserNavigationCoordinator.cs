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

}
