namespace MidFD.Models;

public enum HashResultCopyFormat { SelectedHash, Csv, Log }

public sealed record HashResultCopyItem(string Target, string Algorithm, string HashValue);

public sealed record HashResultCopyContext(
    HashResultCopyFormat Format,
    IReadOnlyList<HashResultCopyItem> Items,
    int SelectedIndex,
    string OutputText);
