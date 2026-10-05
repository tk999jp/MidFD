using MidFD.Models;

namespace MidFD.Services;

public static class UnifiedSearchResultActivationService
{
    // Navigation returns after the existing Browser load/apply boundary.
    public static string? Activate(string fullPath, bool isDirectory, bool preview,
        Func<string, string?, bool> navigate, Func<string?> selectedPath, Func<bool> openPreview,
        Action<bool>? requestForeground = null)
    {
        if (!(isDirectory ? Directory.Exists(fullPath) : File.Exists(fullPath)))
            return $"対象が見つかりません: {fullPath}";

        string? parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(parent)) return "対象の親ディレクトリを取得できませんでした。";
        if (!navigate(parent, Path.GetFileName(fullPath)) ||
            !string.Equals(selectedPath(), fullPath, StringComparison.OrdinalIgnoreCase))
            return isDirectory ? "対象フォルダを選択できませんでした。" : "対象を選択できませんでした。";
        if (preview && !openPreview()) return "対象をプレビューできませんでした。";
        requestForeground?.Invoke(preview);
        return null;
    }
}
