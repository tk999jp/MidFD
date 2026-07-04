using System;
using System.Linq;
using System.Windows.Forms;
using MidFD.Services;

namespace MidFD.Helpers
{
    public static class DragDropDataObjectDiagnosticHelper
    {
        public static string GetDiagnosticLog(
            string phase,
            string uiMode,
            bool isReadOnly,
            bool isClipboardBusy,
            bool internalMarkerPresent,
            IDataObject? data,
            DragDropEffects finalEffect,
            string? reason = null)
        {
            try
            {
                if (data == null)
                {
                    return $"[DragDataObject] {phase} - null data, uiMode={uiMode}, isReadOnly={isReadOnly}, isClipboardBusy={isClipboardBusy}, finalEffect={finalEffect}, reason={reason ?? "none"}";
                }

                bool fileDropPresent = false;
                int fileDropCount = 0;
                try
                {
                    fileDropPresent = data.GetDataPresent(DataFormats.FileDrop);
                    if (fileDropPresent)
                    {
                        fileDropCount = data.GetData(DataFormats.FileDrop) is string[] files ? files.Length : 0;
                    }
                }
                catch (Exception)
                {
                    fileDropPresent = false;
                    fileDropCount = -1;
                }

                bool fileGroupDescriptorPresent = false;
                try { fileGroupDescriptorPresent = data.GetDataPresent("FileGroupDescriptor"); } catch {}

                bool fileGroupDescriptorWPresent = false;
                try { fileGroupDescriptorWPresent = data.GetDataPresent("FileGroupDescriptorW"); } catch {}

                bool fileContentsPresent = false;
                try { fileContentsPresent = data.GetDataPresent("FileContents"); } catch {}

                bool hasImageData = false;
                try { hasImageData = BrowserImageDropService.HasImageData(data); } catch {}

                bool hasPotentialUrlData = false;
                try { hasPotentialUrlData = BrowserDropUrlResolverService.HasPotentialUrlData(data); } catch {}

                bool isOutlookAttachmentDrop = false;
                try { isOutlookAttachmentDrop = OutlookAttachmentDropService.IsOutlookAttachmentDrop(data); } catch {}

                string formatsFalseStr = "[]";
                try
                {
                    var formats = data.GetFormats(false);
                    formatsFalseStr = formats != null ? "[" + string.Join(", ", formats) + "]" : "[]";
                }
                catch (Exception ex)
                {
                    formatsFalseStr = $"[Error: {ex.Message}]";
                }

                string formatsTrueStr = "[]";
                try
                {
                    var formats = data.GetFormats(true);
                    formatsTrueStr = formats != null ? "[" + string.Join(", ", formats) + "]" : "[]";
                }
                catch (Exception ex)
                {
                    formatsTrueStr = $"[Error: {ex.Message}]";
                }

                var baseLog = $"[DragDataObject] {phase} - " +
                              $"uiMode={uiMode}, " +
                              $"isReadOnly={isReadOnly}, " +
                              $"clipboardBusy={isClipboardBusy}, " +
                              $"internalMarkerPresent={internalMarkerPresent}, " +
                              $"fileDropPresent={fileDropPresent}, " +
                              $"fileDropCount={fileDropCount}, " +
                              $"fileGroupDescriptorPresent={fileGroupDescriptorPresent}, " +
                              $"fileGroupDescriptorWPresent={fileGroupDescriptorWPresent}, " +
                              $"fileContentsPresent={fileContentsPresent}, " +
                              $"hasImageData={hasImageData}, " +
                              $"hasPotentialUrlData={hasPotentialUrlData}, " +
                              $"isOutlookAttachmentDrop={isOutlookAttachmentDrop}, " +
                              $"finalEffect={finalEffect}, " +
                              $"reason={reason ?? "none"}, " +
                              $"formatsFalse={formatsFalseStr}, " +
                              $"formatsTrue={formatsTrueStr}";

                const int maxLen = 1000;
                if (baseLog.Length > maxLen)
                {
                    return baseLog.Substring(0, maxLen - 20) + "... [truncated=true]";
                }
                return baseLog;
            }
            catch (Exception ex)
            {
                return $"[DragDataObject] {phase} - diagnosticError={ex.GetType().Name}: {ex.Message}";
            }
        }
    }
}
