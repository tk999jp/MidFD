using System.IO;

namespace MidFD.Services;

internal static class ReparsePointHelper
{
    public static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            // 属性取得失敗時は安全側でリンク相当として扱う。
            return true;
        }
    }

    public static bool ShouldRecurseIntoDirectory(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            return (attrs & FileAttributes.Directory) != 0
                && (attrs & FileAttributes.ReparsePoint) == 0;
        }
        catch
        {
            return false;
        }
    }
}
