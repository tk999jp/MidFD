namespace MidFD.Services;

public static class NavigationFallbackResolver
{
    public static bool TryResolveExistingDirectoryFallback(
        string? missingPath,
        Action<string>? logError,
        out string fallbackPath,
        out string reason)
    {
        fallbackPath = string.Empty;
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(missingPath))
        {
            return TryResolveDefaultFallback(out fallbackPath, out reason);
        }

        try
        {
            string? current = null;
            try
            {
                current = Path.GetFullPath(missingPath);
            }
            catch
            {
                current = missingPath;
            }

            while (!string.IsNullOrWhiteSpace(current))
            {
                try
                {
                    string? parent = Directory.GetParent(current)?.FullName;
                    if (string.IsNullOrWhiteSpace(parent))
                    {
                        break;
                    }
                    if (Directory.Exists(parent))
                    {
                        fallbackPath = parent;
                        reason = "親";
                        return true;
                    }
                    current = parent;
                }
                catch
                {
                    break;
                }
            }

            try
            {
                string? root = Path.GetPathRoot(missingPath);
                if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                {
                    fallbackPath = root;
                    reason = "ルート";
                    return true;
                }
            }
            catch
            {
            }

            return TryResolveDefaultFallback(out fallbackPath, out reason);
        }
        catch (Exception ex)
        {
            logError?.Invoke($"[DirectoryRefresh] Fallback resolution failed: {ex.Message}");
            return TryResolveDefaultFallback(out fallbackPath, out reason);
        }
    }

    public static bool TryResolveDefaultFallback(out string fallbackPath, out string reason)
    {
        fallbackPath = string.Empty;
        reason = string.Empty;
        try
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile) && Directory.Exists(userProfile))
            {
                fallbackPath = userProfile;
                reason = "ユーザープロファイル";
                return true;
            }

            string appDir = AppContext.BaseDirectory;
            if (!string.IsNullOrWhiteSpace(appDir) && Directory.Exists(appDir))
            {
                fallbackPath = appDir;
                reason = "アプリケーション";
                return true;
            }
        }
        catch
        {
        }

        return false;
    }
}
