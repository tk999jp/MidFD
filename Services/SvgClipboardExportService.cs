using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Forms;

namespace MidFD.Services
{
    /// <summary>
    /// SVG形式をクリップボードへエクスポートするサービス。
    /// Officeアプリ等でのベクター貼り付け互換性を重視する。
    /// </summary>
    public static class SvgClipboardExportService
    {
        private const string SvgFormat = "image/svg+xml";

        /// <summary>
        /// 指定されたパスのSVGファイルを読み込み、クリップボードに複数の形式で格納する。
        /// </summary>
        /// <param name="path">SVG/SVGZファイルのパス</param>
        /// <param name="fallbackImage">表示中のBitmap等のフォールバック画像（任意）</param>
        /// <returns>成功した場合はtrue</returns>
        public static bool CopyToClipboard(string path, Image? fallbackImage = null)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return false;
            }

            try
            {
                string svgXml = LoadSvgXml(path);
                if (string.IsNullOrEmpty(svgXml)) return false;

                var dataObject = new DataObject();

                // 1. Primary: image/svg+xml (UTF-8 MemoryStream)
                // Microsoft 365 や最近のブラウザが解釈する形式
                byte[] svgBytes = Encoding.UTF8.GetBytes(svgXml);
                MemoryStream svgStream = new MemoryStream(svgBytes);
                dataObject.SetData(SvgFormat, false, svgStream);

                // 2. Fallback: PNG
                // Office や多くのアプリが画像として受け取れる形式
                if (fallbackImage != null)
                {
                    MemoryStream pngStream = new MemoryStream();
                    fallbackImage.Save(pngStream, ImageFormat.Png);
                    dataObject.SetData("PNG", false, pngStream);

                    // 3. Fallback: Bitmap
                    // クラシックなアプリ向けの標準形式
                    dataObject.SetData(DataFormats.Bitmap, true, fallbackImage);
                }

                // クリップボードへの設定（リトライ付き）
                return SetDataObjectWithRetry(dataObject);
            }
            catch (Exception ex)
            {
                LogService.Error($"SVGのクリップボードコピーに失敗しました: {path}", ex);
                return false;
            }
        }

        private static string LoadSvgXml(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".svgz")
            {
                using (var fs = File.OpenRead(path))
                using (var gzip = new GZipStream(fs, CompressionMode.Decompress))
                using (var reader = new StreamReader(gzip, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
            else
            {
                return File.ReadAllText(path, Encoding.UTF8);
            }
        }

        private static bool SetDataObjectWithRetry(DataObject dataObject)
        {
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    // true を指定してアプリ終了後もデータを保持するようにする
                    Clipboard.SetDataObject(dataObject, true);
                    return true;
                }
                catch (System.Runtime.InteropServices.ExternalException)
                {
                    if (i == 2) throw;
                    System.Threading.Thread.Sleep(100);
                }
            }
            return false;
        }
    }
}
