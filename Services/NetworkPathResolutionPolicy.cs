using System;
using System.IO;
using System.Runtime.InteropServices;

namespace MidFD.Services;

internal static class NetworkPathResolutionPolicy
{
    private const string VerbatimUncPrefix = @"\\?\UNC\";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetDriveTypeW(string lpRootPathName);

    public static bool IsUncPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        return path.StartsWith(@"\\", StringComparison.Ordinal) ||
               path.StartsWith(VerbatimUncPrefix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAuxiliaryResolutionDeferred(string? path) => IsUncPath(path);

    public static string GetPathKind(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "Unknown";
        }

        if (IsUncPath(path))
        {
            return "UNC";
        }

        try
        {
            string? root = Path.GetPathRoot(path);
            if (!string.IsNullOrWhiteSpace(root) && root.Length >= 2 && char.IsLetter(root[0]) && root[1] == ':')
            {
                return "DriveLetter";
            }
        }
        catch
        {
            // Unknown
        }

        return "Unknown";
    }

    public static string GetPathRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "<empty>";
        }

        if (TryGetUncRoot(path, out string uncRoot))
        {
            return uncRoot;
        }

        try
        {
            string? root = Path.GetPathRoot(path);
            return string.IsNullOrWhiteSpace(root) ? "<none>" : root;
        }
        catch
        {
            return "<invalid>";
        }
    }

    public static bool TryGetDriveType(string? rootPath, out DriveType driveType)
    {
        driveType = DriveType.Unknown;
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return false;
        }

        try
        {
            driveType = (DriveType)GetDriveTypeW(rootPath);
            return true;
        }
        catch
        {
            driveType = DriveType.Unknown;
            return false;
        }
    }

    public static void LogDecision(
        string eventName,
        string scope,
        string caller,
        string? path,
        bool usedCached,
        bool resolvedSync,
        string reason)
    {
        LogService.Detail(
            $"[{eventName}] scope={scope} caller={caller} pathKind={GetPathKind(path)} pathRoot={GetPathRoot(path)} " +
            $"usedCached={usedCached} resolvedSync={resolvedSync} reason={reason}");
    }

    public static bool TryGetUncRoot(string? path, out string uncRoot)
    {
        uncRoot = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string candidate = path;
        if (candidate.StartsWith(VerbatimUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            candidate = @"\\" + candidate[VerbatimUncPrefix.Length..];
        }

        if (!candidate.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        string trimmed = candidate.TrimStart('\\');
        string[] segments = trimmed.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
        {
            uncRoot = @"\\" + trimmed;
            return true;
        }

        uncRoot = $@"\\{segments[0]}\{segments[1]}";
        return true;
    }
}
