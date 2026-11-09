namespace MidFD.Configuration;

public static class StartupArgumentParser
{
    /// <summary>Program.Main arguments, without the executable name.</summary>
    public static StartupOptions Parse(IEnumerable<string> args) => ParseCore(args.ToArray(), 0);

    /// <summary>Environment.GetCommandLineArgs arguments, including the executable name.</summary>
    public static StartupOptions ParseCommandLine(IReadOnlyList<string> commandLine) => ParseCore(commandLine, 1);

    private static StartupOptions ParseCore(IReadOnlyList<string> args, int offset)
    {
        string? storage = null, profile = null, diagnosticsPath = null, directory = null;
        bool enable = false, disable = false, diagnostics = false, probe = false;
        int cancelAfter = 0, count = 10;
        var seen = new HashSet<StartupArgumentKind>();
        foreach (StartupArgumentDefinition definition in StartupArgumentCatalog.All)
        {
            if (definition.Form == StartupArgumentForm.FirstPosition)
            {
                directory = args.Count > offset ? args[offset] : null;
                continue;
            }
            for (int i = offset; i < args.Count; i++)
            {
                string token = args[i];
                // Historical diagnostics-file prefix detection also accepts malformed suffixes.
                if (definition.RequestsDiagnosticsByPrefix && token.StartsWith(definition.Name, StringComparison.OrdinalIgnoreCase))
                    diagnostics = true;
                bool exact = string.Equals(token, definition.Name, StringComparison.OrdinalIgnoreCase);
                string? value = null;
                if (definition.Form == StartupArgumentForm.Flag)
                {
                    if (!exact) continue;
                }
                else if (token.StartsWith(definition.Name + "=", StringComparison.OrdinalIgnoreCase))
                    value = token[(definition.Name.Length + 1)..];
                else if (exact && definition.Form == StartupArgumentForm.SeparateOrEquals)
                    value = i + 1 < args.Count ? args[i + 1] : definition.MissingValue;
                else
                    continue;

                if ((definition.Kind is StartupArgumentKind.ProbeCancelAfter or StartupArgumentKind.ProbeCount) &&
                    !int.TryParse(value, out _)) continue;
                if (!seen.Add(definition.Kind)) continue;
                switch (definition.Kind)
                {
                    case StartupArgumentKind.StorageProfile: storage = value; break;
                    case StartupArgumentKind.Profile: profile = value; break;
                    case StartupArgumentKind.EnableAuthorTools: enable = true; break;
                    case StartupArgumentKind.DisableAuthorTools: disable = true; break;
                    case StartupArgumentKind.StorageDiagnostics: diagnostics = true; break;
                    case StartupArgumentKind.StorageDiagnosticsFile: diagnosticsPath = value; break;
                    case StartupArgumentKind.ShellDeleteProbe: probe = true; break;
                    case StartupArgumentKind.ProbeCancelAfter: cancelAfter = Math.Max(0, int.Parse(value!)); break;
                    case StartupArgumentKind.ProbeCount: count = Math.Clamp(int.Parse(value!), 1, 200); break;
                }
                // Do not consume a value token: legacy scanners each saw the complete argument list.
            }
        }
        AuthorToolsCommand author = (enable, disable) switch
        {
            (true, true) => AuthorToolsCommand.Conflict,
            (true, false) => AuthorToolsCommand.Enable,
            (false, true) => AuthorToolsCommand.Disable,
            _ => AuthorToolsCommand.None
        };
        return new StartupOptions(storage, profile, author,
            diagnostics ? StartupMode.StorageDiagnostics : probe ? StartupMode.ShellDeleteProbe : StartupMode.Normal,
            diagnosticsPath, cancelAfter, count, directory);
    }
}
