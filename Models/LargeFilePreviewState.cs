using System;
using System.Collections.Generic;
using System.Text;

namespace MidFD.Models;

public sealed class LargeFilePreviewState
{
    public string FilePath { get; set; } = string.Empty;
    public long TotalBytes { get; set; }
    
    /// <summary>
    /// 各行の開始位置（バイトオフセット）のリスト。
    /// インデックス 0 は 1行目の開始位置（通常 0）。
    /// </summary>
    public List<long> LineOffsets { get; } = new List<long>();
    
    public int TotalLines => LineOffsets.Count;
    
    /// <summary>
    /// 現在の表示開始行（0-based）。
    /// </summary>
    public int FirstVisibleLine { get; set; }
    
    /// <summary>
    /// 1画面に表示可能な行数。
    /// </summary>
    public int VisibleLineCount { get; set; } = 20;

    /// <summary>
    /// 行インデックスの構築中かどうか。
    /// </summary>
    public bool IsIndexing { get; set; }

    /// <summary>
    /// インデックス作成中に末尾への移動リクエストがあったかどうか。
    /// </summary>
    public bool PendingEndAfterIndex { get; set; }

    /// <summary>
    /// LargeText 用の直近検索キーワード。
    /// </summary>
    public string LastSearchText { get; set; } = string.Empty;

    /// <summary>
    /// 直近検索の方向。true=後方検索。
    /// </summary>
    public bool LastSearchBackward { get; set; }

    /// <summary>
    /// 現在有効な検索ヒット行（0-based）。
    /// </summary>
    public int? ActiveSearchHitLine { get; set; }

    /// <summary>
    /// 現在有効な検索ヒット列（0-based）。
    /// </summary>
    public int ActiveSearchHitColumn { get; set; }

    /// <summary>
    /// 現在有効な検索ヒット長。
    /// </summary>
    public int ActiveSearchHitLength { get; set; }

    /// <summary>
    /// 古い非同期検索結果の反映を防ぐための request id。
    /// </summary>
    public int SearchRequestId { get; set; }

    /// <summary>
    /// LargeText 用に確定した文字コード。
    /// </summary>
    public Encoding DetectedEncoding { get; set; } = Encoding.UTF8;

    /// <summary>
    /// ステータス表示用の文字コード名。
    /// </summary>
    public string DetectedEncodingLabel { get; set; } = "UTF-8";

    /// <summary>
    /// BOM 検出の有無。
    /// </summary>
    public bool HasBom { get; set; }

    /// <summary>
    /// Binary-like 判定結果。
    /// </summary>
    public bool IsBinaryLike { get; set; }

    /// <summary>
    /// UTF-16 など、現行 LargeText 実装で未対応のため安全停止すべきか。
    /// </summary>
    public bool IsEncodingUnsupportedForLargeText { get; set; }
    public bool IsLongLineDetected { get; set; }

    public void ReplaceLineOffsets(IReadOnlyList<long> offsets, long totalBytes)
    {
        LineOffsets.Clear();

        if (offsets.Count == 0)
        {
            LineOffsets.Add(0);
        }
        else
        {
            LineOffsets.AddRange(offsets);
        }

        TotalBytes = totalBytes;

        int maxFirstVisibleLine = Math.Max(0, TotalLines - 1);
        if (FirstVisibleLine > maxFirstVisibleLine)
        {
            FirstVisibleLine = maxFirstVisibleLine;
        }
    }

    public void Clear()
    {
        FilePath = string.Empty;
        TotalBytes = 0;
        LineOffsets.Clear();
        FirstVisibleLine = 0;
        LastSearchText = string.Empty;
        LastSearchBackward = false;
        ActiveSearchHitLine = null;
        ActiveSearchHitColumn = 0;
        ActiveSearchHitLength = 0;
        SearchRequestId = 0;
        DetectedEncoding = Encoding.UTF8;
        DetectedEncodingLabel = "UTF-8";
        HasBom = false;
        IsBinaryLike = false;
        IsEncodingUnsupportedForLargeText = false;
        IsLongLineDetected = false;
    }
}
