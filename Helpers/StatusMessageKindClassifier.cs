using MidFD.Services;

namespace MidFD.Helpers;

public static class StatusMessageKindClassifier
{
    public static StatusKind Classify(string message)
    {
        if (message.Contains("失敗", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("エラー", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("キャンセル", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("無効", StringComparison.OrdinalIgnoreCase))
        {
            return StatusKind.Error;
        }

        if (message.Contains("保存", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("作成", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("解除", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("起動しました", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("切り替えました", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("コピーしました", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("追加しました", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("移動しました", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("更新しました", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("完了", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("復元しました", StringComparison.OrdinalIgnoreCase))
        {
            return StatusKind.Result;
        }

        return StatusKind.Normal;
    }
}
