using MidFD.Services;

namespace MidFD.Helpers;

public static class DialogTextBuilder
{
    public static string BuildLargeTextClipboardCopyConfirmationMessage(int lineCount, long estimatedBytes)
    {
        return $"{lineCount:N0} 行 / 約 {FileOperationService.FormatSize(estimatedBytes)} の選択範囲です。\n" +
               "クリップボードへは大きすぎるため、直接コピーしません。\n\n" +
               "選択範囲をファイルへ保存しますか？";
    }
}
