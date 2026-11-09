using MidFD.Configuration;

namespace MidFD;

public sealed record SusiePluginInspectionResult(
    string DisplayName,
    string VersionInfo,
    string[] Extensions,
    string[] FormatNames);

public static class SusiePluginInspectionApi
{
    public static async Task<SusiePluginInspectionResult> InspectAsync(
        string pluginPath,
        CancellationToken cancellationToken = default)
        => await InspectAsync(pluginPath, cancellationToken, settings => new SusieDecoderService(settings)).ConfigureAwait(false);

    internal static async Task<SusiePluginInspectionResult> InspectAsync(
        string pluginPath,
        CancellationToken cancellationToken,
        Func<SusiePluginSettings, SusieDecoderService> createDecoder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginPath);
        ArgumentNullException.ThrowIfNull(createDecoder);
        string fullPath = Path.GetFullPath(pluginPath);
        using var decoder = createDecoder(new SusiePluginSettings());
        SusiePluginRuntimeMetadata? metadata = await decoder
            .InspectPluginAsync(fullPath, cancellationToken)
            .ConfigureAwait(false);
        if (metadata == null)
        {
            string hostName = Path.GetExtension(fullPath).Equals(".spi", StringComparison.OrdinalIgnoreCase)
                ? "SusieHost32"
                : "SusieHost64";
            string failure = decoder.GetHostFailure(fullPath) ?? "Host failure detail was not recorded.";
            Services.LogService.Warn($"[Susie Plugin Inspection] host='{hostName}' path='{fullPath}' detail='{failure}'");
            string summary = GetFailureSummary(failure);
            throw new InvalidDataException($"{hostName}によるPlugin検査に失敗しました。段階・原因: {summary}");
        }
        return new SusiePluginInspectionResult(
            metadata.DisplayName,
            metadata.VersionInfo,
            metadata.Extensions.ToArray(),
            metadata.FormatNames.ToArray());
    }

    private static string GetFailureSummary(string failure)
    {
        string summary = failure
            .Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim() ?? "Host failure detail was not recorded.";
        const int maxLength = 360;
        return summary.Length <= maxLength ? summary : summary[..maxLength] + "…";
    }
}
