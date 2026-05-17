using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using MidFD.Models;
using MidFD.Services;

namespace MidFD.Helpers
{
    /// <summary>
    /// ファイル操作（コピー、移動、削除、リネーム等）における表示文言や入力バリデーションの組み立てを担当するヘルパー。
    /// UI コントロール（MessageBox 等）の表示自体は行わず、表示に必要な情報を生成する。
    /// </summary>
    public static class FileOperationPresentationHelper
    {
        public record DialogInfo(string Message, string Title, MessageBoxButtons Buttons, MessageBoxIcon Icon, MessageBoxDefaultButton DefaultButton);

        private static string GetOperationLabelOrDefault(string? operationNameOrLabel)
        {
            if (string.IsNullOrWhiteSpace(operationNameOrLabel))
            {
                return "処理";
            }

            return operationNameOrLabel switch
            {
                "Copy" => "コピー",
                "Move" => "移動",
                "Paste" => "貼り付け",
                "Delete" => "削除",
                "Rename" => "リネーム",
                _ => operationNameOrLabel
            };
        }

        public static string GetOperationDisplayName(string operationName)
        {
            return operationName switch
            {
                "Copy" => "コピー",
                "Move" => "移動",
                "Paste" => "貼り付け",
                "Delete" => "削除",
                "Rename" => "リネーム",
                _ => "処理"
            };
        }

        public static string GetBusyBlockedMessage(string? operationLabel = null, bool canCancel = false, bool isCancelRequested = false)
        {
            if (isCancelRequested)
            {
                return string.IsNullOrWhiteSpace(operationLabel)
                    ? "中断処理中です。完了までお待ちください。"
                    : $"{GetOperationLabelOrDefault(operationLabel)}を中断中です。完了までお待ちください。";
            }

            string message = string.IsNullOrWhiteSpace(operationLabel)
                ? "処理中のため操作できません。"
                : $"処理中のため {GetOperationLabelOrDefault(operationLabel)} できません。";

            if (canCancel)
            {
                message += " Esc で中断できます。";
            }

            return message;
        }

        public static string GetCancelRequestedMessage(string operationLabel = "処理")
        {
            return $"{GetOperationLabelOrDefault(operationLabel)}の中断を要求しました。完了までお待ちください。";
        }

        public static string GetOperationStartingMessage(string operationName, int totalCount, string? destinationDirectory = null)
        {
            string action = GetOperationLabelOrDefault(operationName);
            string countText = totalCount > 0 ? $"{totalCount} 件の" : string.Empty;
            string destinationText = string.IsNullOrWhiteSpace(destinationDirectory)
                ? string.Empty
                : $" / 先: {destinationDirectory}";
            return $"{countText}{action}を開始しています...{destinationText}".Trim();
        }

        public static string GetOperationProgressMessage(string operationName, int processedCount, int totalCount, string currentFileName)
        {
            string action = GetOperationLabelOrDefault(operationName);
            return $"{processedCount}/{totalCount} 件 {action}中: {currentFileName}";
        }

        public static string GetPasteProgressMessage(bool isCut, int processedCount, int totalCount, string currentFileName)
        {
            string action = isCut ? "貼り付け(移動)" : "貼り付け(コピー)";
            return $"{processedCount}/{totalCount} 件 {action}中: {currentFileName}";
        }

        public static string GetConflictConfirmationMessage(string operationName, string targetName)
        {
            string action = GetOperationLabelOrDefault(operationName);
            return $"{action}の確認中: {targetName}";
        }

        public static string GetSameDirectoryAliasCopyConfirmationMessage(string fileName, string suggestedName)
        {
            return $"別名コピー方法を確認中: {fileName} -> {suggestedName}";
        }

        public static FileOpExitStatus NormalizeExitStatus(
            FileOpExitStatus rawStatus,
            int successCount,
            int totalCount,
            int skipCount = 0,
            int failCount = 0)
        {
            if (rawStatus == FileOpExitStatus.Canceled)
            {
                return FileOpExitStatus.Canceled;
            }

            if (rawStatus == FileOpExitStatus.Error && successCount == 0 && skipCount == 0)
            {
                return FileOpExitStatus.Error;
            }

            if (successCount == 0 && skipCount > 0 && failCount == 0)
            {
                return FileOpExitStatus.Skipped;
            }

            if (failCount > 0 || skipCount > 0 || successCount < totalCount)
            {
                return successCount > 0 || skipCount > 0
                    ? FileOpExitStatus.PartialSuccess
                    : FileOpExitStatus.Error;
            }

            return rawStatus;
        }

        /// <summary>削除確認ダイアログの情報を生成する。</summary>
        public static DialogInfo GetDeleteConfirmation(SelectionResult selection, bool permanent)
        {
            string msg;
            if (!selection.IsMultiple)
            {
                string path = selection.FirstPath ?? "";
                string name = selection.FirstFileName ?? "";
                string typeStr = Directory.Exists(path) ? "[DIR] " : (File.Exists(path) ? "[FILE] " : "");

                msg = permanent
                    ? $"【警告】 {typeStr}{name} を完全に削除しますか？\n(この操作は元に戻せません)"
                    : $"{typeStr}{name} をゴミ箱へ移動しますか？";
            }
            else
            {
                string contextStr = selection.HasMarkedSelection ? "マーク済み項目 " : "";
                msg = permanent
                    ? $"【警告】 {contextStr}{selection.Count} 件を完全に削除しますか？\n(この操作は元に戻せません)"
                    : $"{contextStr}{selection.Count} 件をゴミ箱へ移動しますか？";
            }

            return new DialogInfo(
                msg,
                permanent ? "完全削除" : "削除",
                MessageBoxButtons.YesNo,
                permanent ? MessageBoxIcon.Warning : MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2
            );
        }

        public static string? GetSelectionSummaryText(SelectionResult selection)
        {
            if (selection.Count <= 1)
            {
                return null;
            }

            string firstName = selection.FirstFileName ?? "(不明)";
            return $"{selection.Count} 件の対象が選択されています。{Environment.NewLine}先頭項目: {firstName}";
        }

        public static string? GetSelectionOutsideCurrentDirectoryWarning(SelectionResult selection, string? currentPath)
        {
            if (selection.Count == 0 || string.IsNullOrWhiteSpace(currentPath))
            {
                return null;
            }

            string currentDir = NavigationService.NormalizeDirectoryForCompare(currentPath);
            int outsideCount = selection.FullPaths.Count(path =>
                !string.Equals(
                    NavigationService.NormalizeDirectoryForCompare(Path.GetDirectoryName(path) ?? string.Empty),
                    currentDir,
                    StringComparison.OrdinalIgnoreCase));

            if (outsideCount <= 0)
            {
                return null;
            }

            return $"警告: 現在のディレクトリ外の項目を {outsideCount} 件含みます。";
        }

        /// <summary>リネーム入力プロンプトのメッセージを生成する。</summary>
        public static string GetRenamePrompt(SelectionResult selection, string defaultName, int currentSuccess)
        {
            if (selection.IsMultiple)
            {
                return $"新しい名前を入力してください ({currentSuccess + 1}/{selection.Count}):\n元の名前: {defaultName}";
            }
            return "新しい名前を入力してください:";
        }

        /// <summary>コピー/移動先パスのバリデーションメッセージを生成する。</summary>
        /// <returns>エラーがある場合はメッセージ、問題なければ null。</returns>
        public static string? GetDestinationPathErrorMessage(string input, string currentPath, string destDirNormalized, string operationName)
        {
            if (string.IsNullOrWhiteSpace(input)) return null; // キャンセル扱い用

            // NormalizeDestinationDirectory が異常時に元のパスを返すため、ここで改めてチェック
            // _currentPathは常に絶対パスなので、相対パスが返ってきたら不正
            if (!Path.IsPathRooted(destDirNormalized) && destDirNormalized != currentPath)
            {
                return $"{operationName}先パスが不正です: {destDirNormalized}";
            }

            if (operationName == "Move" && string.Equals(
                NavigationService.NormalizeDirectoryForCompare(destDirNormalized),
                NavigationService.NormalizeDirectoryForCompare(currentPath),
                StringComparison.OrdinalIgnoreCase))
            {
                return "同一ディレクトリへの移動は意味がありません。";
            }

            return null;
        }

        /// <summary>上書き確認メッセージを生成する。</summary>
        public static string GetOverwriteConfirmationMessage(string fileName)
        {
            return $"{fileName} は既に存在します。上書きしますか？";
        }

        /// <summary>ディレクトリ作成確認メッセージを生成する。</summary>
        public static string GetCreateDirectoryConfirmationMessage(string dirPath)
        {
            return $"ディレクトリ '{dirPath}' が存在いません。作成しますか？";
        }
        
        /// <summary>ファイル操作終了時のステータスメッセージを生成する。</summary>
        public static string GetOperationResultStatusMessage(string operationName, int successCount, string? destDir = null)
        {
            string action = GetOperationDisplayName(operationName);
            string destInfo = !string.IsNullOrEmpty(destDir) ? $" {action}先: {destDir}" : "";
            return $"{successCount} 件の項目を{action}しました。{destInfo}";
        }

        public static string GetOperationResultStatusMessage(FileOperationResult result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));

            string action = GetOperationDisplayName(result.OperationName);
            string countBreakdown = BuildCountBreakdown(result);

            switch (result.ExitStatus)
            {
                case FileOpExitStatus.Success:
                    {
                        string suffix = string.IsNullOrWhiteSpace(result.DestinationDir)
                            ? string.Empty
                            : $" / 先: {result.DestinationDir}";
                        return $"{result.SuccessCount} 件の{action}が完了しました。{countBreakdown}{suffix}".Trim();
                    }

                case FileOpExitStatus.PartialSuccess:
                    {
                        string message = $"{result.SuccessCount}/{result.TotalCount} 件の{action}が完了しました。{countBreakdown}";
                        if (result.OperationName == "Move" && result.SuccessCount < result.TotalCount)
                        {
                            message += " / 未完了のため一部項目は移動元に残りました。";
                        }
                        return message.Trim();
                    }

                case FileOpExitStatus.Skipped:
                    return $"{action}対象はすべてスキップしました。{countBreakdown}".Trim();

                case FileOpExitStatus.Canceled:
                    {
                        string message = $"{action}を中断しました。";
                        if (result.ProcessedCount > 0)
                        {
                            message += countBreakdown;
                        }
                        if (result.OperationName == "Move" && result.SuccessCount < result.TotalCount)
                        {
                            message += " / 未完了のため一部項目は移動元に残りました。";
                        }
                        return message.Trim();
                    }

                default:
                    return result.SuccessCount > 0
                        ? $"{result.SuccessCount}/{result.TotalCount} 件の{action}後にエラーで停止しました。{countBreakdown}".Trim()
                        : $"{action}に失敗しました。{countBreakdown}".Trim();
            }
        }

        /// <summary>貼り付け操作（コピー/移動）の詳細な結果メッセージを生成する。</summary>
        public static string GetPasteResultStatusMessage(int successCount, int skipCount, int failCount, bool isCut)
        {
            string opMode = isCut ? "貼り付け(移動)" : "貼り付け(コピー)";
            string resultMsg = $"{successCount} 件の{opMode}完了";
            if (skipCount > 0 || failCount > 0)
            {
                resultMsg += $" ({skipCount} 件スキップ, {failCount} 件失敗)";
            }
            return resultMsg;
        }

        /// <summary>
        /// Paste 統合フェーズ向けに、FileOperationResult から貼り付け結果メッセージを生成する。
        /// 今回は MainForm の実行フローへは未統合で、文言生成の受け皿のみ提供する。
        /// </summary>
        public static string GetPasteResultStatusMessage(FileOperationResult result, bool isCut)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));
            return GetPasteResultStatusMessage(result.SuccessCount, result.SkipCount, result.FailCount, isCut);
        }

        public static string GetPasteResultStatusMessage(
            FileOperationResult result,
            bool isCut,
            int renamedCount,
            string? firstRenamedName,
            bool preserveClipboardOnIncomplete)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));

            string opMode = isCut ? "貼り付け(移動)" : "貼り付け(コピー)";
            string detail = BuildCountBreakdown(result);
            string message = result.ExitStatus switch
            {
                FileOpExitStatus.Success => $"{result.SuccessCount} 件の{opMode}が完了しました。{detail}".Trim(),
                FileOpExitStatus.PartialSuccess => $"{result.SuccessCount}/{result.TotalCount} 件の{opMode}が完了しました。{detail}".Trim(),
                FileOpExitStatus.Skipped => $"{opMode}対象はすべてスキップしました。{detail}".Trim(),
                FileOpExitStatus.Canceled => $"{opMode}を中断しました。{detail}".Trim(),
                _ => result.SuccessCount > 0
                    ? $"{result.SuccessCount}/{result.TotalCount} 件の{opMode}後にエラーで停止しました。{detail}".Trim()
                    : $"{opMode}に失敗しました。{detail}".Trim()
            };

            if (renamedCount > 0)
            {
                message += renamedCount == 1 && !string.IsNullOrWhiteSpace(firstRenamedName)
                    ? $" / 同名ファイルがあったため別名保存: {firstRenamedName}"
                    : $" / 同名ファイルがあったため別名保存 {renamedCount} 件";
            }

            if (isCut && preserveClipboardOnIncomplete &&
                (result.ExitStatus == FileOpExitStatus.Canceled || result.ExitStatus == FileOpExitStatus.PartialSuccess))
            {
                message += " / 未完了のため切り取り情報は保持しました";
            }

            return message.Trim();
        }

        public static string GetDeleteResultStatusMessage(FileOperationResult result, bool permanent, bool canUndo = false)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));

            string action = permanent ? "完全削除" : "削除";
            string completed = permanent ? "完全に削除" : "削除";
            string detail = BuildCountBreakdown(result);
            string undoHint = !permanent && canUndo && result.SuccessCount > 0
                ? result.SuccessCount == result.TotalCount
                    ? " Ctrl+Z で元に戻せます。"
                    : " Ctrl+Z で成功分を元に戻せます。"
                : string.Empty;

            return result.ExitStatus switch
            {
                FileOpExitStatus.Success => $"{result.SuccessCount} 件を{completed}しました。{undoHint}".Trim(),
                FileOpExitStatus.PartialSuccess => result.SuccessCount > 0
                    ? $"{result.SuccessCount}/{result.TotalCount} 件を{completed}したところで停止しました。{detail}{undoHint}".Trim()
                    : $"{action}に失敗しました。{detail}".Trim(),
                FileOpExitStatus.Canceled => result.ProcessedCount > 0
                    ? $"{result.SuccessCount} 件を{completed}したところで中断しました。{detail}{undoHint}".Trim()
                    : $"{action}はキャンセルされました。",
                FileOpExitStatus.Skipped => $"{action}対象はすべてスキップしました。{detail}".Trim(),
                _ => result.SuccessCount > 0
                    ? $"{result.SuccessCount}/{result.TotalCount} 件の{action}後にエラーで停止しました。{detail}{undoHint}".Trim()
                    : $"{action}に失敗しました。{detail}".Trim()
            };
        }

        public static string GetRenameResultStatusMessage(FileOperationResult result)
        {
            if (result == null) throw new ArgumentNullException(nameof(result));

            string detail = BuildCountBreakdown(result, skipLabel: "変更なし");
            string undoHint = BuildRenameUndoHint(result);

            return result.ExitStatus switch
            {
                FileOpExitStatus.Success => $"{result.SuccessCount} 件リネームしました。{undoHint}".Trim(),
                FileOpExitStatus.PartialSuccess => result.FailCount > 0
                    ? $"{result.SuccessCount}/{result.TotalCount} 件リネームしたところで停止しました。{detail} {undoHint}".Trim()
                    : $"{result.SuccessCount}/{result.TotalCount} 件リネームしました。{detail} {undoHint}".Trim(),
                FileOpExitStatus.Canceled => result.SuccessCount > 0
                    ? $"{result.SuccessCount} 件リネームしたところで中断しました。{detail} {undoHint}".Trim()
                    : "リネームはキャンセルされました。",
                FileOpExitStatus.Skipped => "変更はありません。",
                _ => result.SuccessCount > 0
                    ? $"{result.SuccessCount}/{result.TotalCount} 件リネームしたところでエラーが発生しました。{detail} {undoHint}".Trim()
                    : "リネームに失敗しました。"
            };
        }

        private static string BuildRenameUndoHint(FileOperationResult result)
        {
            if (result.SuccessCount <= 0)
            {
                return string.Empty;
            }

            return result.SuccessCount == result.TotalCount
                ? "Ctrl+Z で元に戻せます。"
                : "Ctrl+Z で成功分を元に戻せます。";
        }

        private static string BuildCountBreakdown(FileOperationResult result, string skipLabel = "スキップ")
        {
            var parts = new[]
            {
                result.SuccessCount > 0 ? $"{result.SuccessCount} 件完了" : null,
                result.SkipCount > 0 ? $"{result.SkipCount} 件{skipLabel}" : null,
                result.FailCount > 0 ? $"{result.FailCount} 件失敗" : null
            }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();

            if (parts.Length == 0)
            {
                return string.Empty;
            }

            return $"({string.Join(", ", parts)})";
        }
    }
}
