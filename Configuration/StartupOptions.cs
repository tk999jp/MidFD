namespace MidFD.Configuration;

public enum StartupMode { Normal, StorageDiagnostics, ShellDeleteProbe }

public sealed record StartupOptions(
    string? StorageProfileValue,
    string? ProfileValue,
    AuthorToolsCommand AuthorToolsCommand,
    StartupMode Mode,
    string? DiagnosticsFilePath,
    int ProbeCancelAfter,
    int ProbeCount,
    string? InitialDirectoryCandidate);
