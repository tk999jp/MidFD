namespace MidFD.Configuration.Storage;

public sealed record StorageProfileActivation(
    StorageProfileKind Kind,
    StorageProfileActivationSource Source,
    string? RawValue,
    string? BootstrapFilePath,
    bool UsedFallback,
    string DiagnosticMessage)
{
    public bool IsInstalled => Kind == StorageProfileKind.Installed;
}
