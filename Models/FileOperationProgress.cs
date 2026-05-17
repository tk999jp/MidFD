namespace MidFD.Models;

/// <summary>
/// 非同期ファイル操作の進捗状況を保持する。
/// </summary>
/// <param name="ProcessedCount">処理済みの件数</param>
/// <param name="TotalCount">全対象件数</param>
/// <param name="CurrentFileName">現在処理中のファイル名</param>
public record FileOperationProgress(int ProcessedCount, int TotalCount, string CurrentFileName);
