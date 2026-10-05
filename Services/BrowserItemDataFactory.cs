using System;
using System.Globalization;
using System.IO;
using MidFD.Models;

namespace MidFD.Services;

/// <summary>
/// FileSystem metadataをUI非依存のbrowser item dataへ変換する。
/// </summary>
public static class BrowserItemDataFactory
{
    public static BrowserItemData CreateParent()
    {
        return new BrowserItemData(
            DisplayName: "..",
            FullPath: null,
            IsDirectory: true,
            IsParent: true,
            TypeText: "<DIR>",
            SizeText: string.Empty,
            DateText: string.Empty,
            AttributesText: string.Empty,
            Attributes: 0,
            Length: 0,
            LastWriteTime: default);
    }

    public static BrowserItemData CreateDirectory(DirectoryInfo directory, string? dateFormat, bool showDirectoryMarker)
    {
        return new BrowserItemData(
            DisplayName: directory.Name,
            FullPath: directory.FullName,
            IsDirectory: true,
            IsParent: false,
            TypeText: showDirectoryMarker ? "<DIR>" : string.Empty,
            SizeText: string.Empty,
            DateText: FormatDisplayDate(directory.LastWriteTime, dateFormat),
            AttributesText: FormatAttributes(directory.Attributes),
            Attributes: directory.Attributes,
            Length: 0,
            LastWriteTime: directory.LastWriteTime);
    }

    public static BrowserItemData CreateFile(FileInfo file, string? dateFormat, string? sizeFormat)
    {
        return new BrowserItemData(
            DisplayName: Path.GetFileNameWithoutExtension(file.Name),
            FullPath: file.FullName,
            IsDirectory: false,
            IsParent: false,
            TypeText: file.Extension.TrimStart('.'),
            SizeText: FormatDisplaySize(file.Length, sizeFormat),
            DateText: FormatDisplayDate(file.LastWriteTime, dateFormat),
            AttributesText: FormatAttributes(file.Attributes),
            Attributes: file.Attributes,
            Length: file.Length,
            LastWriteTime: file.LastWriteTime);
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
            "Bytes" => $"{length.ToString("#,0", CultureInfo.InvariantCulture)} B",
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

    private static string FormatAttributes(FileAttributes attributes)
    {
        return $"{(attributes.HasFlag(FileAttributes.ReadOnly) ? "R" : "-")}{(attributes.HasFlag(FileAttributes.Hidden) ? "H" : "-")}{(attributes.HasFlag(FileAttributes.System) ? "S" : "-")}{(attributes.HasFlag(FileAttributes.Archive) ? "A" : "-")}";
    }
}
