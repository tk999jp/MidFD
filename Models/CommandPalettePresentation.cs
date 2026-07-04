using System.Collections.Generic;
using System.Linq;

namespace MidFD.Models;

public enum CommandPalettePresentationMode
{
    Standard,
    Layered,
    Mixed,
    Sectioned
}

public sealed record CommandPaletteSection(
    string Title,
    IReadOnlyList<CommandLauncherCommand> Commands,
    int TotalCount);

internal sealed record CommandPaletteLayerQuery(
    string RawText,
    string RootToken,
    IReadOnlyList<string> Tokens,
    bool HasTrailingWhitespace)
{
    public bool IsLayered => !string.IsNullOrWhiteSpace(RootToken);

    public bool IsExplicitLayerQuery => IsLayered && (Tokens.Count > 1 || HasTrailingWhitespace);

    public bool IsLayerRootOnly => IsLayered && !IsExplicitLayerQuery;

    public IReadOnlyList<string> TailTokens =>
        Tokens.Count <= 1
            ? Array.Empty<string>()
            : Tokens.Skip(1).ToArray();
}

public sealed record CommandPalettePresentation(
    IReadOnlyList<CommandLauncherCommand> Commands,
    bool IsLayered,
    bool UseAccordion,
    string? StatusText,
    CommandPalettePresentationMode Mode = CommandPalettePresentationMode.Standard,
    IReadOnlyList<CommandPaletteSection>? Sections = null)
{
    public static CommandPalettePresentation Standard(IReadOnlyList<CommandLauncherCommand> commands)
        => new(commands, IsLayered: false, UseAccordion: true, StatusText: null);

    public static CommandPalettePresentation Layered(IReadOnlyList<CommandLauncherCommand> commands, string? statusText)
        => new(commands, IsLayered: true, UseAccordion: false, StatusText: statusText, Mode: CommandPalettePresentationMode.Layered);

    public static CommandPalettePresentation Mixed(
        IReadOnlyList<CommandLauncherCommand> layerCommands,
        IReadOnlyList<CommandLauncherCommand> standardCommands,
        string? statusText)
    {
        var sections = new[]
        {
            new CommandPaletteSection("Layer候補", layerCommands, layerCommands.Count),
            new CommandPaletteSection("通常検索", standardCommands, standardCommands.Count)
        };

        IReadOnlyList<CommandLauncherCommand> commands = sections
            .SelectMany(section => section.Commands)
            .ToList();

        return new CommandPalettePresentation(
            commands,
            IsLayered: false,
            UseAccordion: false,
            StatusText: statusText,
            Mode: CommandPalettePresentationMode.Mixed,
            Sections: sections);
    }

    public static CommandPalettePresentation Sectioned(
        IReadOnlyList<CommandPaletteSection> sections,
        string? statusText)
    {
        IReadOnlyList<CommandLauncherCommand> commands = sections
            .SelectMany(section => section.Commands)
            .ToList();

        return new CommandPalettePresentation(
            commands,
            IsLayered: false,
            UseAccordion: false,
            StatusText: statusText,
            Mode: CommandPalettePresentationMode.Sectioned,
            Sections: sections);
    }

    public bool HasSections => Sections is { Count: > 0 };

    public bool IsMixed => Mode == CommandPalettePresentationMode.Mixed;

    public bool IsSectioned => Mode == CommandPalettePresentationMode.Sectioned;
}
