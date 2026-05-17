using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using MidFD.Services;
using MidFD.Models;

namespace MidFD.Helpers
{
    /// <summary>
    /// MainForm のヘッダおよび情報帯に表示する文字列の生成を担当するヘルパークラス。
    /// UI コントロールには直接触れず、データから表示用テキストを組み立てる。
    /// </summary>
    public static class HeaderPresentationHelper
    {
        public class DisplayStrings
        {
            public string Path { get; set; } = string.Empty;
            public string Page { get; set; } = string.Empty;
            public string Total { get; set; } = string.Empty;
            public string MarkSummary { get; set; } = string.Empty;
            public string MarkCountLine { get; set; } = string.Empty;
            public string MarkSizeLine { get; set; } = string.Empty;
            public string SortFilter { get; set; } = string.Empty;
            public string ItemAttr { get; set; } = string.Empty;
            public string FileDate { get; set; } = string.Empty;
            public string FileStats { get; set; } = string.Empty;
            public string FileName { get; set; } = string.Empty;
            public string ItemNameWithSize { get; set; } = string.Empty;
            public string ItemMetaWithoutSize { get; set; } = string.Empty;
            public string MarkSummaryCompact { get; set; } = string.Empty;
            public int MarkCount { get; set; }
            public string MarkSizeText { get; set; } = string.Empty;
            public string DriveUsed { get; set; } = string.Empty;
            public string DriveFree { get; set; } = string.Empty;
            public bool SelectedItemIsDirectory { get; set; }
            public string RawFileName { get; set; } = string.Empty;
            public string SelectedItemSizeText { get; set; } = string.Empty;
        }

        public class InputState
        {
            public string CurrentPath { get; set; } = string.Empty;
            public int CursorIndex { get; set; }
            public int ItemCount { get; set; }
            public int ItemsPerPage { get; set; }
            public int RowsPerColumn { get; set; }
            public int ColumnCount { get; set; }
            public IReadOnlyCollection<string> MarkedFiles { get; set; } = Array.Empty<string>();
            public string? CachedMarkSummary { get; set; }
            public string? CurrentItemText { get; set; }
            public string? CurrentItemPath { get; set; }
            public SortKind SortKind { get; set; }
            public bool SortAscending { get; set; }
            public string FilterPattern { get; set; } = string.Empty;
            public string FilterLockSummary { get; set; } = string.Empty;
            public bool ShowExtensions { get; set; } = true;
            public bool ShowDirectoryMarker { get; set; } = true;
            public bool ShowItemIcons { get; set; }
            public string DateFormat { get; set; } = "yyyy-MM-dd HH:mm";
            public string SizeFormat { get; set; } = "HumanReadable";
        }

        public static DisplayStrings Build(InputState state)
        {
            var result = new DisplayStrings();

            // 1. Path
            result.Path = state.CurrentPath;

            // 2. Page
            int page = state.ItemsPerPage > 0 ? (state.CursorIndex / state.ItemsPerPage) + 1 : 1;
            int totalPages = state.ItemsPerPage > 0 ? ((state.ItemCount - 1) / state.ItemsPerPage) + 1 : 1;
            result.Page = $"Page:{page,2}/{totalPages,-2}";

            // 3. Total / Mark
            BuildTotalInfo(state, result);

            // 4. Sort / Filter (Compact, Conditional & Prioritized)
            // 優先順位: 1. Mark, 2. Filter, 3. Sort
            var parts = new List<string>();

            // Filter: active 時のみ。Mark があっても残す
            if (!string.IsNullOrEmpty(state.FilterPattern) && state.FilterPattern != "None")
            {
                parts.Add($"F:{state.FilterPattern}");
            }

            if (!string.IsNullOrWhiteSpace(state.FilterLockSummary))
            {
                parts.Add(state.FilterLockSummary);
            }

            // Sort: 非デフォルト時かつ Mark がない時のみ表示（Mark 優先のため）
            bool hasMark = !string.IsNullOrEmpty(result.MarkSummary);
            bool isDefaultSort = (state.SortKind == SortKind.Name && state.SortAscending);
            if (!hasMark && !isDefaultSort)
            {
                string sortStr = state.SortKind.ToString();
                string ascStr = state.SortAscending ? "▲" : "▼";
                parts.Add($"S:{sortStr}{ascStr}");
            }

            result.SortFilter = parts.Count > 0 ? string.Join(" ", parts) : string.Empty;

            // 5. Item Info
            BuildItemInfo(state, result);

            // 6. Drive Info
            BuildDriveInfo(state, result);

            // 7. Composite Strings for UI (Corrective: inline item size & compact mark summary)
            // FileName [Size] (Directories don't get size)
            bool isDirectory = result.FileStats == "<DIR>" || result.ItemAttr.Contains("(DIR)");
            result.ItemNameWithSize = (!isDirectory && !string.IsNullOrEmpty(result.FileStats))
                ? $"{result.FileName} [{result.FileStats}]"
                : result.FileName;

            // Attr Timestamp (Exclude size and mark info from metadata strip)
            result.ItemMetaWithoutSize = $"{result.ItemAttr} {result.FileDate}".Trim();

            // Store raw components for custom ellipsis calculation
            result.SelectedItemIsDirectory = isDirectory;
            result.RawFileName = result.FileName;
            result.SelectedItemSizeText = !isDirectory ? result.FileStats : string.Empty;

            return result;
        }

        private static void BuildTotalInfo(InputState state, DisplayStrings result)
        {
            result.Total = $"Total:{state.ItemCount,3} Items";

            if (state.MarkedFiles.Count > 0)
            {
                // 2行表示用を先に生成（キャッシュの有無に関わらず必要）
                result.MarkCountLine = $"Mark:{state.MarkedFiles.Count,3}";

                if (state.CachedMarkSummary != null)
                {
                    result.MarkSummary = state.CachedMarkSummary;
                    // キャッシュからサイズ部分を抽出するのは不安定なため、再計算するか、ここでは簡易表示にする
                    // 今回は MarkedFiles があるので再計算ロジックを走らせることを優先
                }

                long totalSize = 0;
                int fileCount = 0;
                int outsideCurrentDirectoryCount = 0;
                string currentDir = NavigationService.NormalizeDirectoryForCompare(state.CurrentPath);
                foreach (var path in state.MarkedFiles)
                {
                    string? parentDir = Path.GetDirectoryName(path);
                    if (!string.Equals(
                        NavigationService.NormalizeDirectoryForCompare(parentDir ?? string.Empty),
                        currentDir,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        outsideCurrentDirectoryCount++;
                    }

                    if (File.Exists(path))
                    {
                        try
                        {
                            totalSize += new FileInfo(path).Length;
                            fileCount++;
                        }
                        catch { /* Ignore access errors */ }
                    }
                }

                string outsideInfo = outsideCurrentDirectoryCount > 0 ? $" Out:{outsideCurrentDirectoryCount}" : "";
                result.MarkSummary = $"Mark:{state.MarkedFiles.Count,3} ({fileCount} Files){outsideInfo} {FileOperationService.FormatSize(totalSize)}";

                result.MarkSizeLine = $"Size:{FileOperationService.FormatSize(totalSize)}";

                // Corrective: Path行右端用のコンパクトなマーク表示
                string sizeText = FileOperationService.FormatSize(totalSize);
                result.MarkCount = state.MarkedFiles.Count;
                result.MarkSizeText = sizeText;
                result.MarkSummaryCompact = $"Mark: {result.MarkCount} MarkSize: {sizeText}";
            }
        }

        private static void BuildItemInfo(InputState state, DisplayStrings result)
        {
            if (string.IsNullOrEmpty(state.CurrentItemPath) || state.CurrentItemText == "..")
            {
                result.ItemAttr = "----";
                result.FileDate = "";
                result.FileStats = state.CurrentItemText == ".." ? "<DIR>" : "";
                result.FileName = state.CurrentItemText ?? "";
                return;
            }

            try
            {
                if (Directory.Exists(state.CurrentItemPath))
                {
                    var di = new DirectoryInfo(state.CurrentItemPath);
                    result.ItemAttr = FormatAttributes(di.Attributes) + " (DIR)";
                    result.FileDate = FileSystemItemFactory.FormatDisplayDate(di.LastWriteTime, state.DateFormat);
                    result.FileStats = state.ShowDirectoryMarker ? "<DIR>" : "";
                    result.FileName = state.CurrentItemText ?? di.Name;
                }
                else if (File.Exists(state.CurrentItemPath))
                {
                    var fi = new FileInfo(state.CurrentItemPath);
                    result.ItemAttr = FormatAttributes(fi.Attributes);
                    result.FileDate = FileSystemItemFactory.FormatDisplayDate(fi.LastWriteTime, state.DateFormat);
                    result.FileStats = FileSystemItemFactory.FormatDisplaySize(fi.Length, state.SizeFormat);
                    result.FileName = state.ShowExtensions
                        ? Path.GetFileName(state.CurrentItemPath) ?? state.CurrentItemText ?? ""
                        : Path.GetFileNameWithoutExtension(state.CurrentItemPath) ?? state.CurrentItemText ?? "";
                }
            }
            catch
            {
                result.ItemAttr = "????";
                result.FileName = state.CurrentItemText ?? "";
            }
        }

        private static void BuildDriveInfo(InputState state, DisplayStrings result)
        {
            string driveUsedText = "Used:---";
            string driveFreeText = "Free:---";

            try
            {
                string root = Path.GetPathRoot(state.CurrentPath) ?? "C:\\";
                var drive = new DriveInfo(root);
                if (drive.IsReady)
                {
                    long total = drive.TotalSize;
                    long free = drive.AvailableFreeSpace;
                    long used = total - free;

                    driveUsedText = $"Used:{FileOperationService.FormatSize(used)}";
                    driveFreeText = $"Free:{FileOperationService.FormatSize(free)}";
                }
            }
            catch
            {
                driveUsedText = "Used:---";
                driveFreeText = "Free:---";
            }

            result.DriveUsed = driveUsedText;
            result.DriveFree = driveFreeText;
        }

        private static string FormatAttributes(FileAttributes attr)
        {
            string s = "";
            s += (attr.HasFlag(FileAttributes.ReadOnly)) ? "R" : "-";
            s += (attr.HasFlag(FileAttributes.Hidden)) ? "H" : "-";
            s += (attr.HasFlag(FileAttributes.System)) ? "S" : "-";
            s += (attr.HasFlag(FileAttributes.Archive)) ? "A" : "-";
            return s;
        }
    }
}
