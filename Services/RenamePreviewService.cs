using System.IO;
using MidFD.Models;

namespace MidFD.Services;

public static class RenamePreviewService
{
    public static RenamePreviewItem BuildSingleItemPreview(string sourcePath, string destinationName)
    {
        string sourceName = Path.GetFileName(sourcePath);
        string destinationPath = Path.Combine(Path.GetDirectoryName(sourcePath) ?? string.Empty, destinationName ?? string.Empty);

        var item = new RenamePreviewItem
        {
            SourcePath = sourcePath,
            SourceName = sourceName,
            DestinationName = destinationName ?? string.Empty,
            DestinationPath = destinationPath
        };

        string? error = ValidateName(item, new HashSet<string>(new[] { sourcePath }, StringComparer.OrdinalIgnoreCase));
        if (error != null)
        {
            item.Status = error;
            item.HasError = true;
            item.WillRename = false;
            return item;
        }

        if (string.Equals(sourceName, item.DestinationName, StringComparison.OrdinalIgnoreCase))
        {
            item.Status = "変更なし";
            item.WillRename = false;
            return item;
        }

        if (File.Exists(item.DestinationPath) || Directory.Exists(item.DestinationPath))
        {
            if (!string.Equals(item.SourcePath, item.DestinationPath, StringComparison.OrdinalIgnoreCase))
            {
                item.Status = "既存項目と衝突";
                item.HasError = true;
                item.WillRename = false;
                return item;
            }
        }

        item.Status = "OK";
        item.WillRename = true;
        return item;
    }

    public static RenamePreviewResult BuildPreview(IReadOnlyList<string> sourcePaths, RenameTemplateOptions options)
    {
        return BuildPreviewCore(sourcePaths, (sourcePath, index) =>
        {
            string destinationName = RenameTemplateEngine.BuildName(
                sourcePath,
                options.StartNumber + index,
                options.NumberWidth,
                options.Template);
            return new RenameGeneratedNameResult(destinationName, null);
        });
    }

    public static RenamePreviewResult BuildRegexPreview(IReadOnlyList<string> sourcePaths, RenameRegexOptions options)
    {
        if (!RenameRegexEngine.TryCreateRegex(options, out var regex, out var regexError))
        {
            return BuildPreviewCore(sourcePaths, (sourcePath, _) =>
                new RenameGeneratedNameResult(string.Empty, regexError ?? "正規表現エラー"));
        }

        return BuildPreviewCore(sourcePaths, (sourcePath, _) =>
        {
            string sourceName = Path.GetFileName(sourcePath);
            string destinationName = RenameRegexEngine.Apply(sourceName, regex!, options);
            return new RenameGeneratedNameResult(destinationName, null);
        });
    }

    private static RenamePreviewResult BuildPreviewCore(
        IReadOnlyList<string> sourcePaths,
        Func<string, int, RenameGeneratedNameResult> destinationNameFactory)
    {
        if (sourcePaths == null || sourcePaths.Count == 0)
        {
            return new RenamePreviewResult
            {
                Summary = "リネーム対象がありません。"
            };
        }

        var items = new List<RenamePreviewItem>(sourcePaths.Count);
        var previewNameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var sourcePathSet = new HashSet<string>(sourcePaths, StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < sourcePaths.Count; i++)
        {
            string sourcePath = sourcePaths[i];
            string sourceName = Path.GetFileName(sourcePath);
            var generated = destinationNameFactory(sourcePath, i);
            string destinationName = generated.DestinationName;
            string destinationPath = Path.Combine(Path.GetDirectoryName(sourcePath) ?? string.Empty, destinationName);

            var item = new RenamePreviewItem
            {
                SourcePath = sourcePath,
                SourceName = sourceName,
                DestinationName = destinationName,
                DestinationPath = destinationPath
            };

            string? error = generated.ErrorMessage ?? ValidateName(item, sourcePathSet);
            if (error != null)
            {
                item.Status = error;
                item.HasError = true;
            }
            else if (string.Equals(sourceName, destinationName, StringComparison.OrdinalIgnoreCase))
            {
                item.Status = "変更なし";
                item.WillRename = false;
            }
            else
            {
                item.Status = "OK";
                item.WillRename = true;
            }

            if (!item.HasError)
            {
                previewNameCounts.TryGetValue(item.DestinationPath, out int currentCount);
                previewNameCounts[item.DestinationPath] = currentCount + 1;
            }

            items.Add(item);
        }

        foreach (var item in items.Where(x => !x.HasError))
        {
            if (previewNameCounts.TryGetValue(item.DestinationPath, out int duplicateCount) && duplicateCount > 1)
            {
                item.Status = "選択内重複";
                item.HasError = true;
                item.WillRename = false;
                continue;
            }

            if (File.Exists(item.DestinationPath) || Directory.Exists(item.DestinationPath))
            {
                if (!string.Equals(item.SourcePath, item.DestinationPath, StringComparison.OrdinalIgnoreCase))
                {
                    // 宛先がバッチ内の他項目である場合は、その項目も移動するため衝突とはみなさない。
                    // (バッチ内での最終的な重複は previewNameCounts で別途チェック済み)
                    if (!sourcePathSet.Contains(item.DestinationPath))
                    {
                        item.Status = "既存項目と衝突";
                        item.HasError = true;
                        item.WillRename = false;
                    }
                }
            }
        }

        bool hasErrors = items.Any(x => x.HasError);
        int renameCount = items.Count(x => x.WillRename);
        string firstError = items.FirstOrDefault(x => x.HasError)?.Status ?? string.Empty;
        string summary = hasErrors
            ? $"{renameCount} 件適用可能 / {items.Count(x => x.HasError)} 件エラー"
            : (renameCount > 0 ? $"{renameCount} 件をリネーム予定" : "変更はありません。");
        string detail = hasErrors
            ? $"最初のエラー: {firstError}"
            : (renameCount > 0 ? "プレビュー確認後に OK で適用します。" : "適用できる変更はありません。");

        return new RenamePreviewResult
        {
            Items = items,
            HasErrors = hasErrors,
            HasRenames = renameCount > 0,
            Summary = summary,
            Detail = detail
        };
    }

    private sealed record RenameGeneratedNameResult(string DestinationName, string? ErrorMessage);

    private static string? ValidateName(RenamePreviewItem item, HashSet<string> sourcePathSet)
    {
        if (string.IsNullOrWhiteSpace(item.DestinationName))
        {
            return "空文字";
        }

        if (item.DestinationName == "." || item.DestinationName == "..")
        {
            return "予約名";
        }

        if (item.DestinationName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return "禁止文字";
        }

        string? parentDirectory = Path.GetDirectoryName(item.SourcePath);
        if (string.IsNullOrWhiteSpace(parentDirectory))
        {
            return "親ディレクトリ不明";
        }

        if (!sourcePathSet.Contains(item.SourcePath))
        {
            return "対象不明";
        }

        return null;
    }
}
