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
        var item = new ListViewItem(d.Name);
        item.SubItems.Add(showDirectoryMarker ? "<DIR>" : "");
        item.SubItems.Add("");
        item.SubItems.Add(FormatDisplayDate(d.LastWriteTime, dateFormat));
        item.SubItems.Add(FormatAttributes(d.Attributes));
        item.Tag = d.FullName;

        item.ForeColor = ResolveAttributeColor(d.Attributes, isDirectory: true);

        return item;
    }

    public static ListViewItem CreateFileItem(FileInfo f, string? dateFormat, string? sizeFormat)
    {
        // WinFD風: 拡張子を分離
        string nameOnly = Path.GetFileNameWithoutExtension(f.Name);
        string extOnly = f.Extension.TrimStart('.');

        var item = new ListViewItem(nameOnly);
        item.SubItems.Add(extOnly);
        item.SubItems.Add(FormatDisplaySize(f.Length, sizeFormat));
        item.SubItems.Add(FormatDisplayDate(f.LastWriteTime, dateFormat));
        item.SubItems.Add(FormatAttributes(f.Attributes));
        item.Tag = f.FullName;

        item.ForeColor = ResolveAttributeColor(f.Attributes, isDirectory: false);

        return item;
    }

    public static string FormatDisplayDate(DateTime dateTime, string? dateFormat)
    {
        string format = dateFormat switch
        {
            "yyyy/MM/dd HH:mm:ss" => "yyyy/MM/dd HH:mm:ss",
            "yyyy-MM-dd(ddd) HH:mm" => "yyyy-MM-dd(ddd) HH:mm",
            _ => "yyyy-MM-dd HH:mm"
        };

        return dateTime.ToString(format);
    }

    public static string FormatDisplaySize(long length, string? sizeFormat)
    {
        return sizeFormat switch
        {
            "Bytes" => $"{length:#,0} B",
            "KB/MB" => FormatCompactSize(length),
            _ => FileOperationService.FormatSize(length)
        };
    }

    private static string FormatCompactSize(long length)
    {
        const double kb = 1024d;
        const double mb = kb * 1024d;
        const double gb = mb * 1024d;

        if (length >= gb) return $"{length / gb:0.0} GB";
        if (length >= mb) return $"{length / mb:0.0} MB";
        if (length >= kb) return $"{length / kb:0.0} KB";
        return $"{length:#,0} B";
    }

    private static string FormatAttributes(FileAttributes attr)
    {
        return $"{(attr.HasFlag(FileAttributes.ReadOnly) ? "R" : "-")}{(attr.HasFlag(FileAttributes.Hidden) ? "H" : "-")}{(attr.HasFlag(FileAttributes.System) ? "S" : "-")}{(attr.HasFlag(FileAttributes.Archive) ? "A" : "-")}";
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
