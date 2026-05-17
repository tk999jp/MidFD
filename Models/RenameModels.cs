namespace MidFD.Models;

public enum RenameDialogMode
{
    Template,
    Regex
}

public sealed class RenameTemplateOptions
{
    public string Template { get; set; } = "$F$E";
    public int StartNumber { get; set; } = 1;
    public int NumberWidth { get; set; } = 1;
}

public sealed class RenameRegexOptions
{
    public string Pattern { get; set; } = string.Empty;
    public string Replacement { get; set; } = string.Empty;
    public bool IgnoreCase { get; set; }
    public bool Multiline { get; set; }
    public bool Global { get; set; } = true;
}

public sealed class RenamePreviewItem
{
    public string SourcePath { get; init; } = string.Empty;
    public string SourceName { get; init; } = string.Empty;
    public string DestinationName { get; init; } = string.Empty;
    public string DestinationPath { get; init; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool HasError { get; set; }
    public bool WillRename { get; set; }
}

public sealed class RenamePreviewResult
{
    public IReadOnlyList<RenamePreviewItem> Items { get; init; } = Array.Empty<RenamePreviewItem>();
    public bool HasErrors { get; init; }
    public bool HasRenames { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
}

public sealed class RenameDialogResult
{
    public bool Confirmed { get; init; }
    public RenameDialogMode Mode { get; init; } = RenameDialogMode.Template;
    public RenameTemplateOptions TemplateOptions { get; init; } = new();
    public RenameRegexOptions RegexOptions { get; init; } = new();
    public RenamePreviewResult Preview { get; init; } = new();
    public bool RememberTemplate { get; init; }
    public string LastTemplateCandidate { get; init; } = string.Empty;
}
