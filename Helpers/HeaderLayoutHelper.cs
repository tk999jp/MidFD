using System;
using System.Drawing;
using System.Windows.Forms;

namespace MidFD.Helpers
{
    /// <summary>
    /// MainForm のヘッダおよび情報帯のレイアウト計算を担当するヘルパークラス。
    /// 数値計算と座標算出に特化し、UI 適用は MainForm 側で行う。
    /// </summary>
    public static class HeaderLayoutHelper
    {
        public class ZoneWidths
        {
            public int Zone1 { get; set; }
            public int Zone2 { get; set; }
            public int Zone3 { get; set; }
            public int Zone4 { get; set; }
            public int MinimumFormWidth { get; set; }
        }

        public class HeaderMetrics
        {
            public int LineHeight { get; set; }
            public int TitleHeaderHeight { get; set; }
            public int RowHeight { get; set; }
            public int TopPanelHeight { get; set; }
        }

        /// <summary>
        /// フォントに基づき各種パネルの高さを計算する。
        /// </summary>
        public static HeaderMetrics CalculateMetrics(Font font, int padding = 4)
        {
            int lineHeight = GetMeasuredLineHeight(font, padding);

            // 3段目〜4段目 (topPanel): Path, Name の2行
            int topPanelRowCount = 2;
            int topPanelSeparatorCount = 1; // sepAfterRow4 (一覧領域との境界) のみ維持
            int topPanelHeight = (lineHeight * topPanelRowCount) + topPanelSeparatorCount;

            return new HeaderMetrics
            {
                LineHeight = lineHeight,
                TitleHeaderHeight = 0, // 0 にして非表示化
                RowHeight = lineHeight,
                TopPanelHeight = topPanelHeight
            };
        }

        /// <summary>
        /// Row 2 の各ゾーン (Page, Total, Used, Free) の幅を動的に配分する。
        /// </summary>
        public static ZoneWidths CalculateZoneWidths(int availableWidth, Font font, string pageText, string totalText, string usedText, string freeText, int currentMinWidth)
        {
            // 内容に依存せず、常に最大級の文字列で計測することで配置を固定する（安定化）
            const string pageTemplate = "Page: 88/88";
            const string totalTemplate = "Total: 8888 Items";
            const string usedTemplate = "Used: 888.88GB";
            const string freeTemplate = "Free: 888.88GB";

            int p1 = TextRenderer.MeasureText(pageTemplate, font).Width + 10;
            int p2 = TextRenderer.MeasureText(totalTemplate, font).Width + 10;
            int p3 = TextRenderer.MeasureText(usedTemplate, font).Width + 10;
            int p4 = TextRenderer.MeasureText(freeTemplate, font).Width + 10;

            int totalMin = p1 + p2 + p3 + p4;
            var result = new ZoneWidths();

            // 余剰幅がある場合は重みに基づいて分配
            if (availableWidth > totalMin)
            {
                int extra = availableWidth - totalMin;
                // 重み設定: 長い数値が入る項目を優先する (Page:1.0, Total:1.6, Used:1.6, Free:1.8)
                double w1 = 1.0, w2 = 1.6, w3 = 1.6, w4 = 1.8;
                double totalWeight = w1 + w2 + w3 + w4;

                result.Zone1 = p1 + (int)(extra * w1 / totalWeight);
                result.Zone2 = p2 + (int)(extra * w2 / totalWeight);
                result.Zone3 = p3 + (int)(extra * w3 / totalWeight);
                result.Zone4 = p4 + (int)(extra * w4 / totalWeight);
            }
            else
            {
                // 幅が足りない場合は最小値を維持
                result.Zone1 = p1;
                result.Zone2 = p2;
                result.Zone3 = p3;
                result.Zone4 = p4;
            }

            // フォーム全体の最小幅の目安を計算
            // Phase 40-audit: currentMinWidth をそのまま Max に入れないようにし、内容ベースの最小幅を尊重する。
            int requiredMinFormWidth = totalMin + 40; // 余裕を持たせた最小幅
            result.MinimumFormWidth = Math.Clamp(requiredMinFormWidth, 400, 1200);

            return result;
        }

        /// <summary>
        /// フォントとパディングに基づき、実測値としての行高を算出する。
        /// </summary>
        public static int GetMeasuredLineHeight(Font font, int extraPadding = 4)
        {
            // 基準文字列で計測。CJK や記号の高さも反映させる。
            Size size = TextRenderer.MeasureText("AgjQy|漢/", font);
            return size.Height + extraPadding;
        }
    }
}
