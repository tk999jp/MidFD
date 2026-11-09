namespace MidFD.Models;

public enum MarkSlotClipboardEntryDiagnosticKind
{
    RecoveredDescription,
    MissingFile,
    Directory,
    Duplicate,
    SyntaxInvalid,
    OutsideRepository,
    Ambiguous
}

public sealed record MarkSlotClipboardEntryDiagnostic(
    string RawEntry,
    MarkSlotClipboardEntryDiagnosticKind Kind,
    string? CandidatePath = null);
