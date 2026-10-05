using System.Text.RegularExpressions;
using MidFD.Models;

namespace MidFD.Dialogs;

internal sealed record HashResultItem(string Target, string Algorithm, string HashValue);

internal static class HashResultParser
{
    public static IReadOnlyList<HashResultItem> Parse(string output, SevenZipHashAlgorithm requestedAlgorithm)
    {
        if (!TryParseRows(output, out List<RowData> rows) || rows.Count == 0)
        {
            return Array.Empty<HashResultItem>();
        }

        string? expectedHashName = GetHashName(requestedAlgorithm);
        if (requestedAlgorithm != SevenZipHashAlgorithm.All && expectedHashName == null)
        {
            return Array.Empty<HashResultItem>();
        }

        var items = new List<HashResultItem>();
        foreach (RowData row in rows)
        {
            if (requestedAlgorithm == SevenZipHashAlgorithm.All)
            {
                if (row.Hashes.Count == 0 || row.Hashes.Values.Any(value => !IsHexHash(value)))
                {
                    return Array.Empty<HashResultItem>();
                }

                foreach ((string algorithm, string value) in row.Hashes)
                {
                    items.Add(new HashResultItem(row.FileName, NormalizeHashLabel(algorithm), value));
                }

                continue;
            }

            if (!row.Hashes.TryGetValue(expectedHashName!, out string? hashValue)
                || !IsHashForAlgorithm(hashValue, requestedAlgorithm))
            {
                return Array.Empty<HashResultItem>();
            }

            items.Add(new HashResultItem(row.FileName, NormalizeHashLabel(expectedHashName!), hashValue));
        }

        return items;
    }

    private static bool TryParseRows(string output, out List<RowData> rows)
    {
        rows = new List<RowData>();
        string[] lines = output.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        int firstSeparator = -1;

        for (int i = 0; i < lines.Length; i++)
        {
            if (!IsTableSeparator(lines[i]))
            {
                continue;
            }

            if (firstSeparator == -1)
            {
                firstSeparator = i;
                continue;
            }

            int headerIndex = firstSeparator - 1;
            if (headerIndex >= 0)
            {
                List<ColumnInfo> columns = ParseColumns(lines[headerIndex], lines[firstSeparator]);
                bool hasName = columns.Any(column => column.Name.Equals("Name", StringComparison.OrdinalIgnoreCase));
                bool hasSize = columns.Any(column => column.Name.Equals("Size", StringComparison.OrdinalIgnoreCase));
                bool hasHash = columns.Any(column => !column.Name.Equals("Name", StringComparison.OrdinalIgnoreCase)
                    && !column.Name.Equals("Size", StringComparison.OrdinalIgnoreCase));

                if (hasName && hasSize && hasHash)
                {
                    for (int rowIndex = firstSeparator + 1; rowIndex < i; rowIndex++)
                    {
                        RowData? row = ExtractRowData(lines[rowIndex], columns);
                        if (row != null)
                        {
                            rows.Add(row);
                        }
                    }

                    if (rows.Count > 0)
                    {
                        return true;
                    }
                }
            }

            firstSeparator = i;
        }

        return false;
    }

    private static bool IsTableSeparator(string line)
    {
        return line.Length > 0
            && line.Contains('-')
            && line.All(character => character is '-' or ' ');
    }

    private static List<ColumnInfo> ParseColumns(string headerLine, string separatorLine)
    {
        var columns = new List<ColumnInfo>();
        MatchCollection groups = Regex.Matches(separatorLine, "-+");
        for (int i = 0; i < groups.Count; i++)
        {
            Match group = groups[i];
            if (group.Index >= headerLine.Length)
            {
                continue;
            }

            int availableLength = Math.Min(group.Length, headerLine.Length - group.Index);
            string name = headerLine.Substring(group.Index, availableLength).Trim();
            if (name.Length == 0)
            {
                continue;
            }

            int length = name.Equals("Name", StringComparison.OrdinalIgnoreCase) ? -1 : group.Length;
            columns.Add(new ColumnInfo(name, group.Index, length));
        }

        return columns;
    }

    private static RowData? ExtractRowData(string line, List<ColumnInfo> columns)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? fileName = null;

        foreach (ColumnInfo column in columns)
        {
            if (column.Start >= line.Length)
            {
                continue;
            }

            string value = column.Length == -1
                ? line.Substring(column.Start).Trim()
                : line.Substring(column.Start, Math.Min(column.Length, line.Length - column.Start)).Trim();

            if (column.Name.Equals("Name", StringComparison.OrdinalIgnoreCase))
            {
                fileName = value;
            }
            else if (!column.Name.Equals("Size", StringComparison.OrdinalIgnoreCase))
            {
                hashes[column.Name] = value;
            }
        }

        return string.IsNullOrWhiteSpace(fileName) || hashes.Count == 0
            ? null
            : new RowData(fileName, hashes);
    }

    private static string? GetHashName(SevenZipHashAlgorithm algorithm)
    {
        return algorithm switch
        {
            SevenZipHashAlgorithm.Crc32 => "CRC32",
            SevenZipHashAlgorithm.Crc64 => "CRC64",
            SevenZipHashAlgorithm.Sha1 => "SHA1",
            SevenZipHashAlgorithm.Sha256 => "SHA256",
            _ => null
        };
    }

    private static bool IsHashForAlgorithm(string value, SevenZipHashAlgorithm algorithm)
    {
        int expectedLength = algorithm switch
        {
            SevenZipHashAlgorithm.Crc32 => 8,
            SevenZipHashAlgorithm.Crc64 => 16,
            SevenZipHashAlgorithm.Sha1 => 40,
            SevenZipHashAlgorithm.Sha256 => 64,
            _ => 0
        };

        return value.Length == expectedLength && IsHexHash(value);
    }

    private static bool IsHexHash(string value)
    {
        return value.Length > 0 && value.All(Uri.IsHexDigit);
    }

    private static string NormalizeHashLabel(string value)
    {
        return value.ToUpperInvariant() switch
        {
            "CRC32" => "CRC-32",
            "CRC64" => "CRC-64",
            "SHA1" => "SHA-1",
            "SHA256" => "SHA-256",
            _ => value
        };
    }

    private sealed record ColumnInfo(string Name, int Start, int Length);
    private sealed record RowData(string FileName, Dictionary<string, string> Hashes);
}
