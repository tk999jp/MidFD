using System.IO;
using System.Drawing;
using System.Windows.Forms;
using MidFD.Models;

namespace MidFD.Services;

/// <summary>
/// 探索の結果得られたファイルシステム情報から ListViewItem を生成するファクトリクラス。
/// </summary>
public static class FileSystemItemFactory
{
    public static ListViewItem CreateDirectoryItem(DirectoryInfo d, string? dateFormat, bool showDirectoryMarker)
    {
        return CreateItem(BrowserItemDataFactory.CreateDirectory(d, dateFormat, showDirectoryMarker));
    }

    public static ListViewItem CreateFileItem(FileInfo f, string? dateFormat, string? sizeFormat)
    {
        return CreateItem(BrowserItemDataFactory.CreateFile(f, dateFormat, sizeFormat));
    }

    public static ListViewItem CreateItem(BrowserItemData data)
    {
        var item = new ListViewItem(data.DisplayName);
        item.SubItems.Add(data.TypeText);
        item.SubItems.Add(data.SizeText);
        item.SubItems.Add(data.DateText);
        item.SubItems.Add(data.AttributesText);
        item.Tag = data.FullPath;

        if (!data.IsParent)
        {
            item.ForeColor = ResolveAttributeColor(data.Attributes, data.IsDirectory);
        }

        return item;
    }

    public static string FormatDisplayDate(DateTime dateTime, string? dateFormat)
    {
        return BrowserItemDataFactory.FormatDisplayDate(dateTime, dateFormat);
    }

    public static string FormatDisplaySize(long length, string? sizeFormat)
    {
        return BrowserItemDataFactory.FormatDisplaySize(length, sizeFormat);
    }

    private static Color ResolveAttributeColor(FileAttributes attr, bool isDirectory)
    {
        if (attr.HasFlag(FileAttributes.System))
            return MidFDColors.ListSystemFore;
        if (attr.HasFlag(FileAttributes.Hidden))
            return MidFDColors.ListHiddenFore;
        if (attr.HasFlag(FileAttributes.ReadOnly))
            return MidFDColors.ListReadOnlyFore;

        return isDirectory ? MidFDColors.ListDirectoryFore : MidFDColors.ListFileFore;
    }
}
