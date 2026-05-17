using MidFD.Models;

namespace MidFD.Models
{
    /// <summary>
    /// ファイル操作（コピー、移動など）の実行結果を保持するモデル。
    /// HandlePostOperation で後処理を行うための情報を集約する。
    /// </summary>
    public class FileOperationResult
    {
        public string OperationName { get; }
        public FileOpExitStatus ExitStatus { get; }
        public int SuccessCount { get; }
        public int TotalCount { get; }
        public int SkipCount { get; }
        public int FailCount { get; }
        public int ProcessedCount => SuccessCount + SkipCount + FailCount;
        public string? NextFocusTarget { get; }
        public string? DestinationDir { get; }

        // 追加のオプションフラグ
        public bool ShouldClearPreview { get; }
        public bool ShouldClearMarks { get; }
        public string? CustomMessage { get; }

        public FileOperationResult(
            string operationName,
            FileOpExitStatus exitStatus,
            int successCount,
            int totalCount,
            string? nextFocusTarget = null,
            string? destinationDir = null,
            bool shouldClearPreview = false,
            bool shouldClearMarks = true,
            string? customMessage = null,
            int skipCount = 0,
            int failCount = 0)
        {
            OperationName = operationName;
            ExitStatus = exitStatus;
            SuccessCount = successCount;
            TotalCount = totalCount;
            NextFocusTarget = nextFocusTarget;
            DestinationDir = destinationDir;
            ShouldClearPreview = shouldClearPreview;
            ShouldClearMarks = shouldClearMarks;
            CustomMessage = customMessage;
            SkipCount = skipCount;
            FailCount = failCount;
        }
    }
}
