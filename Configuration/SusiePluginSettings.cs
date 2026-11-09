namespace MidFD.Configuration;

public sealed class SusiePluginRegistration
{
    public string PluginPath { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public Dictionary<string, string> PreferredDecoderByExtension { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public SusiePluginInspectionMetadata? InspectionMetadata { get; set; }

    public SusiePluginRegistration Clone() => new()
    {
        PluginPath = PluginPath ?? string.Empty,
        IsEnabled = IsEnabled,
        PreferredDecoderByExtension = new Dictionary<string, string>(
            PreferredDecoderByExtension ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase),
        InspectionMetadata = InspectionMetadata?.Clone()
    };
}

public sealed class SusiePluginInspectionMetadata
{
    public string DisplayName { get; set; } = string.Empty;
    public string VersionInfo { get; set; } = string.Empty;
    public string[] Extensions { get; set; } = Array.Empty<string>();
    public string[] FormatNames { get; set; } = Array.Empty<string>();

    public SusiePluginInspectionMetadata Clone() => new()
    {
        DisplayName = DisplayName ?? string.Empty,
        VersionInfo = VersionInfo ?? string.Empty,
        Extensions = (Extensions ?? Array.Empty<string>()).ToArray(),
        FormatNames = (FormatNames ?? Array.Empty<string>()).ToArray()
    };
}

public sealed class SusiePluginSettings
{
    public List<SusiePluginRegistration> Plugins { get; set; } = new();
    public Dictionary<string, string> DecoderByExtension { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> DisabledStandardExtensions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public SusiePluginSettings Clone() => new()
    {
        Plugins = (Plugins ?? new List<SusiePluginRegistration>()).Select(plugin => plugin.Clone()).ToList(),
        DecoderByExtension = new Dictionary<string, string>(
            DecoderByExtension ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase),
        DisabledStandardExtensions = new HashSet<string>(
            DisabledStandardExtensions ?? new HashSet<string>(),
            StringComparer.OrdinalIgnoreCase)
    };

    public void Normalize()
    {
        Plugins ??= new List<SusiePluginRegistration>();
        Plugins = Plugins
            .Where(plugin => plugin != null && !string.IsNullOrWhiteSpace(plugin.PluginPath))
            .GroupBy(plugin => plugin.PluginPath.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToList();
        foreach (SusiePluginRegistration plugin in Plugins)
        {
            plugin.InspectionMetadata = plugin.InspectionMetadata?.Clone();
            if (plugin.InspectionMetadata is not { } metadata) continue;
            metadata.Extensions = (metadata.Extensions ?? Array.Empty<string>())
                .Select(NormalizeExtension)
                .Where(extension => extension.Length > 1)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            metadata.FormatNames = (metadata.FormatNames ?? Array.Empty<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .ToArray();
        }
        var normalizedSelections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string extension, string decoder) in DecoderByExtension ?? new Dictionary<string, string>())
        {
            string normalizedExtension = NormalizeExtension(extension);
            if (normalizedExtension.Length > 1 && !string.IsNullOrWhiteSpace(decoder))
                normalizedSelections[normalizedExtension] = decoder.Trim();
        }
        DecoderByExtension = normalizedSelections;
        DisabledStandardExtensions = (DisabledStandardExtensions ?? new HashSet<string>())
            .Select(NormalizeExtension)
            .Where(extension => extension.Length > 1)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static string NormalizeExtension(string? extension)
    {
        string value = (extension ?? string.Empty).Trim();
        if (value.Length == 0) return string.Empty;
        value = value.TrimStart('*');
        return value[0] == '.' ? value : "." + value;
    }
}
