using MidFD.Dialogs;
using MidFD.Models;
using MidFD.Services;

namespace MidFD.Helpers;

/// <summary>
/// Rename 導線の dialog 接続だけを扱う。
/// 実際の rename apply や preview service の public な意味は MainForm / Service 側に残し、
/// ここではどの dialog を出してどの preview を MainForm に返すかだけをまとめる。
/// </summary>
public sealed class RenameDialogCoordinator
{
    public readonly record struct SingleRenameDialogResult(
        bool WasCanceled,
        bool WillRename,
        RenamePreviewItem? PreviewItem);

    public RenameEntryDialogResult ShowEntryDialog(IWin32Window owner, IReadOnlyList<string> sourcePaths)
    {
        return RenameEntryDialog.Show(owner, sourcePaths);
    }

    public RenameDialogResult ShowBatchDialog(IWin32Window owner, IReadOnlyList<string> sourcePaths, string initialTemplate, bool rememberTemplate)
    {
        return RenameDialog.Show(owner, sourcePaths, initialTemplate, rememberTemplate);
    }

    public SingleRenameDialogResult ShowSingleRenameDialog(
        IWin32Window owner,
        string sourcePath,
        string? initialValue,
        bool skipInitialPrompt,
        bool showValidationMessage)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return new SingleRenameDialogResult(false, false, null);
        }

        string sourceName = Path.GetFileName(sourcePath);
        string promptValue = initialValue ?? sourceName;
        bool usePrompt = !skipInitialPrompt;

        // 単体ファイルリネームのみ拡張子除外選択を行う。
        // ディレクトリ・拡張子なしファイル・先頭ドットのみのファイルは全選択（-1）。
        int selectionLength = -1;
        if (File.Exists(sourcePath))
        {
            string baseName = Path.GetFileNameWithoutExtension(sourceName);
            string ext = Path.GetExtension(sourceName);
            // ".gitignore" → baseName="", ext=".gitignore" の場合は全選択
            if (!string.IsNullOrEmpty(ext) && !string.IsNullOrEmpty(baseName))
            {
                selectionLength = baseName.Length;
            }
        }

        while (true)
        {
            string? newName = usePrompt
                ? SimpleInputDialog.ShowNullable("新しい名前を入力してください:", "Rename", promptValue, null, selectionLength)
                : promptValue;

            usePrompt = true;

            if (newName == null)
            {
                return new SingleRenameDialogResult(true, false, null);
            }

            var previewItem = RenamePreviewService.BuildSingleItemPreview(sourcePath, newName);
            if (previewItem.HasError)
            {
                if (showValidationMessage)
                {
                    MessageBox.Show(
                        $"リネームできません: {previewItem.Status}",
                        "Rename",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }

                promptValue = newName;
                selectionLength = -1; // リトライ時は全選択に戻す
                continue;
            }

            if (!previewItem.WillRename)
            {
                return new SingleRenameDialogResult(false, false, null);
            }

            return new SingleRenameDialogResult(false, true, previewItem);
        }
    }
}
