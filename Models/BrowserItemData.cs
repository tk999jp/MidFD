using System;
using System.IO;

namespace MidFD.Models;

/// <summary>
/// Directory loadが返すUI非依存のbrowser item projection。
/// ListViewItemやその他のWinForms型は保持しない。
/// </summary>
public sealed record BrowserItemData(
    string DisplayName,
    string? FullPath,
    bool IsDirectory,
    bool IsParent,
    string TypeText,
    string SizeText,
    string DateText,
    string AttributesText,
    FileAttributes Attributes,
    long Length,
    DateTime LastWriteTime);
