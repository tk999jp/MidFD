using System.Drawing;
using System.Drawing.Imaging;

namespace MidFD.Services;

public static class ClipboardImagePasteService
{
    public static string SavePngToDirectory(Image image, string directoryPath, DateTime? now = null)
    {
        Directory.CreateDirectory(directoryPath);

        string fileName = BuildDefaultFileName(now ?? DateTime.Now);
        string fullPath = Path.Combine(directoryPath, fileName);
        string uniquePath = File.Exists(fullPath) || Directory.Exists(fullPath)
            ? FileOperationService.GetUniquePathStartingAtOne(fullPath)
            : fullPath;

        using var bitmap = new Bitmap(image);
        bitmap.Save(uniquePath, ImageFormat.Png);
        return uniquePath;
    }

    public static string BuildDefaultFileName(DateTime now)
    {
        return $"Clipboard_{now:yyyyMMdd_HHmmss}.png";
    }
}
