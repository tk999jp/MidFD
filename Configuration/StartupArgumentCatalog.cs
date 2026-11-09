namespace MidFD.Configuration;

public enum StartupArgumentKind
{
    StorageProfile, Profile, EnableAuthorTools, DisableAuthorTools,
    StorageDiagnostics, StorageDiagnosticsFile, ShellDeleteProbe, ProbeCancelAfter, ProbeCount, Directory
}

public enum StartupArgumentForm { Flag, SeparateOrEquals, EqualsOnly, FirstPosition }

public sealed record StartupArgumentDefinition(
    StartupArgumentKind Kind, string Name, StartupArgumentForm Form,
    string? MissingValue = null, bool RequestsDiagnosticsByPrefix = false);

/// <summary>Existing startup syntax only. Execution and persistence remain with their owners.</summary>
public static class StartupArgumentCatalog
{
    public const string EnableAuthorToolsName = "--enable-author-tools";
    public const string DisableAuthorToolsName = "--disable-author-tools";

    public static IReadOnlyList<StartupArgumentDefinition> All { get; } = Array.AsReadOnly<StartupArgumentDefinition>(
    [
        new(StartupArgumentKind.StorageProfile, "--storage-profile", StartupArgumentForm.SeparateOrEquals, ""),
        new(StartupArgumentKind.Profile, "--profile", StartupArgumentForm.SeparateOrEquals),
        new(StartupArgumentKind.EnableAuthorTools, EnableAuthorToolsName, StartupArgumentForm.Flag),
        new(StartupArgumentKind.DisableAuthorTools, DisableAuthorToolsName, StartupArgumentForm.Flag),
        new(StartupArgumentKind.StorageDiagnostics, "--storage-profile-diagnostics", StartupArgumentForm.Flag),
        new(StartupArgumentKind.StorageDiagnosticsFile, "--storage-profile-diagnostics-file", StartupArgumentForm.SeparateOrEquals, "", true),
        new(StartupArgumentKind.ShellDeleteProbe, "--shell-delete-probe", StartupArgumentForm.Flag),
        new(StartupArgumentKind.ProbeCancelAfter, "--shell-delete-probe-cancel-after", StartupArgumentForm.EqualsOnly),
        new(StartupArgumentKind.ProbeCount, "--shell-delete-probe-count", StartupArgumentForm.EqualsOnly),
        new(StartupArgumentKind.Directory, "directory", StartupArgumentForm.FirstPosition)
    ]);
}
