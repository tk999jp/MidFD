using MidFD.Models;

namespace MidFD.Services;

public readonly record struct PackDialogRouteDecision(PackDialogRoute Route, string? ErrorMessage = null)
{
    public bool IsNative => Route == PackDialogRoute.Native;
}

public static class PackDialogRoutingService
{
    private const int NativeCommandLineLimit = 28_000;

    public static PackDialogRouteDecision Resolve(
        PackDialogMode mode,
        string? guiExecutablePath,
        IReadOnlyList<string> targetPaths,
        string archivePath)
    {
        bool nativeAvailable = !string.IsNullOrWhiteSpace(guiExecutablePath) && File.Exists(guiExecutablePath);
        bool nativeSupported = nativeAvailable && CanUseNativeDialog(guiExecutablePath!, targetPaths, archivePath);

        if (mode == PackDialogMode.MidFd)
        {
            return new(PackDialogRoute.MidFd);
        }

        if (nativeSupported)
        {
            return new(PackDialogRoute.Native);
        }

        if (mode == PackDialogMode.SevenZipNative)
        {
            return new(
                PackDialogRoute.Error,
                nativeAvailable
                    ? "7-Zip標準Dialogは、この圧縮対象には対応していません。"
                    : "7zG.exeが見つからないため、7-Zip標準Dialogを起動できません。設定の「通常の圧縮画面」を「自動」または「MidFD簡易Dialog」に変更してください。");
        }

        return new(PackDialogRoute.MidFd);
    }

    public static bool CanUseNativeDialog(string guiExecutablePath, IReadOnlyList<string> targetPaths, string archivePath)
    {
        if (string.IsNullOrWhiteSpace(guiExecutablePath) || string.IsNullOrWhiteSpace(archivePath) || targetPaths.Count == 0)
        {
            return false;
        }

        if (targetPaths.Any(path => string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path))))
        {
            return false;
        }

        int argumentLength = guiExecutablePath.Length + archivePath.Length + 16;
        foreach (string path in targetPaths)
        {
            argumentLength += path.Length + 3;
        }

        return argumentLength <= NativeCommandLineLimit;
    }
}
