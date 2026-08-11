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

    public static bool IsAuxiliaryResolutionDeferred(string? path) => IsNetworkPath(path);

    public static bool IsNetworkPath(string? path)
    {
        return IsNetworkPath(path, ResolveDriveType);
    }

    internal static bool IsNetworkPath(string? path, Func<string, DriveType?> driveTypeResolver)
    {
        if (IsUncPath(path))
        {
            return true;
        }

        return TryGetDriveRoot(path, out string root) &&
               driveTypeResolver(root) == DriveType.Network;
    }

    public static bool TryGetNetworkRoot(string? path, out string networkRoot)
    {
        return TryGetNetworkRoot(path, ResolveDriveType, out networkRoot);
    }

    internal static bool TryGetNetworkRoot(
        string? path,
        Func<string, DriveType?> driveTypeResolver,
        out string networkRoot)
    {
        if (TryGetUncRoot(path, out networkRoot))
        {
            return true;
        }

        if (TryGetDriveRoot(path, out string driveRoot) && driveTypeResolver(driveRoot) == DriveType.Network)
        {
            networkRoot = driveRoot;
            return true;
        }

        networkRoot = string.Empty;
        return false;
    }

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
                return ResolveDriveType(root) == DriveType.Network ? "MappedNetwork" : "DriveLetter";
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

    private static DriveType? ResolveDriveType(string root)
    {
        return TryGetDriveType(root, out DriveType driveType) ? driveType : null;
    }

    private static bool TryGetDriveRoot(string? path, out string root)
    {
        root = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            string? candidate = Path.GetPathRoot(path);
            if (!string.IsNullOrWhiteSpace(candidate) && candidate.Length >= 2 && char.IsLetter(candidate[0]) && candidate[1] == ':')
            {
                root = candidate;
                return true;
            }
        }
        catch
        {
            // Unknown
        }

        return false;
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
