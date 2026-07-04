using System.Text.Json;

namespace MidFD.Configuration.Storage;

public static class StorageProfileActivationResolver
{
    public const string EnvironmentVariableName = "MIDFD_STORAGE_PROFILE";
    public const string BootstrapFileName = "storage-profile.json";

    public static StorageProfileActivation ResolveDefault(string[] args)
    {
        return Resolve(
            args,
            Environment.GetEnvironmentVariable(EnvironmentVariableName),
            Path.Combine(AppContext.BaseDirectory, BootstrapFileName));
    }

    internal static StorageProfileActivation Resolve(string[] args, string? environmentValue, string bootstrapFilePath)
    {
        string? commandLineValue = TryGetCommandLineValue(args);
        if (commandLineValue != null)
        {
            return FromValue(commandLineValue, StorageProfileActivationSource.CommandLine, null);
        }

        if (!string.IsNullOrWhiteSpace(environmentValue))
        {
            return FromValue(environmentValue, StorageProfileActivationSource.EnvironmentVariable, null);
        }

        StorageProfileActivation? bootstrapActivation = TryResolveBootstrapFile(bootstrapFilePath);
        return bootstrapActivation ?? DefaultActivation();
    }

    private static string? TryGetCommandLineValue(string[] args)
    {
        const string prefix = "--storage-profile=";

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return arg[prefix.Length..];
            }

            if (string.Equals(arg, "--storage-profile", StringComparison.OrdinalIgnoreCase))
            {
                return i + 1 < args.Length ? args[i + 1] : string.Empty;
            }
        }

        return null;
    }

    private static StorageProfileActivation? TryResolveBootstrapFile(string bootstrapFilePath)
    {
        try
        {
            if (!File.Exists(bootstrapFilePath))
            {
                return null;
            }

            using FileStream stream = File.OpenRead(bootstrapFilePath);
            using JsonDocument document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("profile", out JsonElement profileElement) ||
                profileElement.ValueKind != JsonValueKind.String)
            {
                return InvalidFallback(StorageProfileActivationSource.BootstrapFile, null, bootstrapFilePath);
            }

            return FromValue(profileElement.GetString(), StorageProfileActivationSource.BootstrapFile, bootstrapFilePath);
        }
        catch
        {
            return InvalidFallback(StorageProfileActivationSource.BootstrapFile, null, bootstrapFilePath);
        }
    }

    private static StorageProfileActivation FromValue(
        string? value,
        StorageProfileActivationSource source,
        string? bootstrapFilePath)
    {
        string normalized = (value ?? string.Empty).Trim();
        if (string.Equals(normalized, "installed", StringComparison.OrdinalIgnoreCase))
        {
            return new StorageProfileActivation(
                StorageProfileKind.Installed,
                source,
                value,
                bootstrapFilePath,
                UsedFallback: false,
                DiagnosticMessage: "Installed profile selected by explicit opt-in.");
        }

        if (string.Equals(normalized, "portable", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "legacyportable", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "legacy-portable", StringComparison.OrdinalIgnoreCase))
        {
            return new StorageProfileActivation(
                StorageProfileKind.Portable,
                source,
                value,
                bootstrapFilePath,
                UsedFallback: false,
                DiagnosticMessage: "Portable profile selected by explicit opt-in.");
        }

        return InvalidFallback(source, value, bootstrapFilePath);
    }

    private static StorageProfileActivation InvalidFallback(
        StorageProfileActivationSource source,
        string? rawValue,
        string? bootstrapFilePath)
    {
        return new StorageProfileActivation(
            StorageProfileKind.Portable,
            source,
            rawValue,
            bootstrapFilePath,
            UsedFallback: true,
            DiagnosticMessage: "Invalid storage profile value. Falling back to Portable.");
    }

    private static StorageProfileActivation DefaultActivation()
    {
        return new StorageProfileActivation(
            StorageProfileResolver.GetDefaultProfileKind(),
            StorageProfileActivationSource.Default,
            RawValue: null,
            BootstrapFilePath: null,
            UsedFallback: false,
            DiagnosticMessage: "Portable profile selected by default.");
    }
}
