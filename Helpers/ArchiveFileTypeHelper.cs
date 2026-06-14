using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MidFD.Helpers;

/// <summary>
/// アーカイブ（圧縮ファイル）形式の判定および拡張子カタログを管理するヘルパー。
/// </summary>
internal static class ArchiveFileTypeHelper
{
    private static readonly HashSet<string> _supportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".cab", ".lzh", ".wim"
    };

    /// <summary>
    /// サポートされているアーカイブ拡張子の読み取り専用セット。
    /// </summary>
    public static IReadOnlySet<string> SupportedExtensions => _supportedExtensions;

    /// <summary>
    /// 指定されたパスがアーカイブ形式かどうかを判定します。
    /// </summary>
    /// <param name="path">ファイルパスまたはファイル名</param>
    /// <returns>アーカイブ形式の場合は true</returns>
    public static bool IsArchive(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string ext = Path.GetExtension(path);
        return _supportedExtensions.Contains(ext);
    }

    /// <summary>
    /// 指定されたパスがアーカイブ形式かどうかを判定します。ファイルの実在確認も含めることができます。
    /// </summary>
    /// <param name="path">ファイルパス</param>
    /// <param name="checkFileExists">ファイルの実在を確認するかどうか</param>
    /// <returns>条件を満たす場合は true</returns>
    public static bool IsArchive(string? path, bool checkFileExists)
    {
        if (!IsArchive(path)) return false;
        if (checkFileExists && !File.Exists(path)) return false;
        return true;
    }

    public static bool CanUseTarFallbackForUnpack(string? path)
    {
        if (!IsArchive(path)) return false;

        string? ext = Path.GetExtension(path);
        return !string.Equals(ext, ".zip", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(ext, ".wim", StringComparison.OrdinalIgnoreCase);
    }
}
