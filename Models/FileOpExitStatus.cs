namespace MidFD.Models
{
    /// <summary>
    /// ファイル操作の終了状態を示す列挙型。
    /// </summary>
    public enum FileOpExitStatus
    {
        Success,
        PartialSuccess,
        Skipped,
        Canceled,
        Error
    }
}
