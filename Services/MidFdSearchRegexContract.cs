using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MidFD.Services;

/// <summary>Portable rg-default subset evaluated on Unicode scalar values, independent of .NET's dialect.</summary>
internal sealed class MidFdSearchRegexContract
{
    private readonly string _shape;
    private readonly List<Func<Rune, bool>> _atoms;
    private string? _lastAlphabet;
    private Regex? _lastRegex;

    private MidFdSearchRegexContract(string pattern, bool caseSensitive)
    {
        var parser = new Parser(pattern, caseSensitive);
        _shape = parser.Parse();
        _atoms = parser.Atoms;
        _ = BuildRegex([]);
    }

    internal static MidFdSearchRegexContract Create(string pattern, bool caseSensitive = false) => new(pattern, caseSensitive);
    internal static bool TryValidate(string pattern, out string? error)
    {
        try { _ = Create(pattern); error = null; return true; }
        catch (ArgumentException ex) { error = ex.Message; return false; }
    }
    internal bool IsMatch(string text, CancellationToken token = default) => FindMatches(text, token).Any();

    internal static IEnumerable<(int Index, int Length)> FindLiteralMatches(string text, string pattern,
        bool caseSensitive, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string haystack = caseSensitive ? text : string.Concat(text.EnumerateRunes().Select(r => Fold(r).ToString()));
        string needle = caseSensitive ? pattern : string.Concat(pattern.EnumerateRunes().Select(r => Fold(r).ToString()));
        if (needle.Length == 0) yield break;
        int next = 0;
        while (next <= haystack.Length - needle.Length)
        {
            token.ThrowIfCancellationRequested();
            int index = haystack.IndexOf(needle, next, StringComparison.Ordinal);
            if (index < 0) break;
            yield return (index, needle.Length);
            next = index + needle.Length;
        }
    }

    private static Rune Fold(Rune r) => r.Value is 0x130 or 0x131 ? r : Rune.ToLowerInvariant(Rune.ToUpperInvariant(r));

    internal static string ToRipgrepPattern(string pattern)
    {
        // Search operates on decoded logical lines. Accept CRLF's trailing CR at native $,
        // then the common matcher projects matches onto the logical line without its terminator.
        var result = new StringBuilder();
        bool inClass = false;
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '\\') { result.Append(c).Append(pattern[++i]); continue; }
            if (c == '[') inClass = true;
            if (c == ']') inClass = false;
            result.Append(c == '$' && !inClass ? "(?:\\r?$)" : c.ToString());
        }
        return result.ToString();
    }

    internal IEnumerable<(int Index, int Length)> FindMatches(string text, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Rune[] runes = text.EnumerateRunes().ToArray();
        Rune[] alphabet = runes.Distinct().OrderBy(r => r.Value).ToArray();
        if (alphabet.Length >= char.MaxValue) throw new ArgumentException("この行の文字種類数は共通正規表現契約で未対応です。");
        var codes = alphabet.Select((r, i) => (r, code: (char)(i + 1))).ToDictionary(x => x.r, x => x.code);
        string projected = new(runes.Select(r => codes[r]).ToArray());
        string key = string.Concat(alphabet.Select(r => r.ToString()));
        Regex regex = _lastAlphabet == key && _lastRegex != null ? _lastRegex : BuildRegex(alphabet);
        _lastAlphabet = key;
        _lastRegex = regex;
        int[] offsets = new int[runes.Length + 1];
        for (int i = 0; i < runes.Length; i++) offsets[i + 1] = offsets[i] + runes[i].Utf16SequenceLength;
        int lastEnd = -1;
        foreach (Match match in regex.Matches(projected))
        {
            token.ThrowIfCancellationRequested();
            // Rust find_iter suppresses an empty match adjacent to the preceding match.
            if (match.Length == 0 && match.Index == lastEnd) continue;
            lastEnd = match.Index + match.Length;
            yield return (offsets[match.Index], offsets[lastEnd] - offsets[match.Index]);
        }
    }

    private Regex BuildRegex(Rune[] alphabet)
    {
        string expression = Regex.Replace(_shape, "@([0-9]+)@", match =>
        {
            var predicate = _atoms[int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)];
            var set = new StringBuilder();
            for (int i = 0; i < alphabet.Length; i++)
                if (predicate(alphabet[i])) set.Append("\\u").Append((i + 1).ToString("X4", CultureInfo.InvariantCulture));
            return set.Length == 0 ? "(?!)" : "[" + set + "]";
        });
        return new Regex(expression, RegexOptions.CultureInvariant, NamePatternMatcher.MatchTimeout);
    }

    private sealed class Parser(string pattern, bool caseSensitive)
    {
        private int _position, _depth;
        internal List<Func<Rune, bool>> Atoms { get; } = [];
        internal string Parse()
        {
            string result = Expression();
            if (_position != pattern.Length) throw Unsupported();
            return result;
        }
        private string Expression()
        {
            var result = new StringBuilder();
            while (_position < pattern.Length && pattern[_position] != ')')
            {
                char c = pattern[_position++];
                if (c == '|') { result.Append('|'); continue; }
                string atom;
                switch (c)
                {
                    case '(':
                        if (++_depth > 64) throw Unsupported();
                        if (_position < pattern.Length && pattern[_position] == '?')
                        {
                            if (!pattern.AsSpan(_position).StartsWith("?:")) throw Unsupported();
                            _position += 2;
                        }
                        atom = "(?:" + Expression() + ")";
                        if (_position >= pattern.Length || pattern[_position++] != ')') throw Unsupported();
                        _depth--;
                        break;
                    case '[': atom = Add(CharacterClass()); break;
                    case '.': atom = Add(r => r.Value != '\n'); break;
                    case '^': atom = "\\A"; break;
                    case '$': atom = "\\z"; break;
                    case '\\': atom = Add(Escape().Predicate); break;
                    case '*' or '+' or '?' or '{' or '}' or ']': throw Unsupported();
                    default: _position--; atom = Add(Equal(ReadRune())); break;
                }
                bool quantified = false;
                if (_position < pattern.Length && pattern[_position] is '*' or '+' or '?')
                { atom = "(?:" + atom + ")" + pattern[_position++]; quantified = true; }
                else if (_position < pattern.Length && pattern[_position] == '{')
                {
                    int start = _position++;
                    while (_position < pattern.Length && (char.IsAsciiDigit(pattern[_position]) || pattern[_position] == ',')) _position++;
                    if (_position >= pattern.Length || pattern[_position++] != '}') throw Unsupported();
                    string count = pattern[start.._position];
                    if (!Regex.IsMatch(count, @"^\{[0-9]+(,[0-9]*)?\}$") || count.Trim('{', '}').Split(',').Any(n =>
                        n.Length > 0 && (!int.TryParse(n, out int value) || value > 10000))) throw Unsupported();
                    atom = "(?:" + atom + ")" + count;
                    quantified = true;
                }
                if (quantified && _position < pattern.Length && pattern[_position] == '?') atom += pattern[_position++];
                result.Append(atom);
            }
            return result.ToString();
        }
        private string Add(Func<Rune, bool> predicate)
        {
            int index = Atoms.Count;
            Atoms.Add(predicate);
            return "@" + index + "@";
        }
        private Func<Rune, bool> CharacterClass()
        {
            bool negate = _position < pattern.Length && pattern[_position] == '^';
            if (negate) _position++;
            var predicates = new List<Func<Rune, bool>>();
            while (_position < pattern.Length && pattern[_position] != ']')
            {
                if (pattern[_position] == '[' || pattern.AsSpan(_position).StartsWith("&&") ||
                    pattern.AsSpan(_position).StartsWith("--") || pattern.AsSpan(_position).StartsWith("~~")) throw Unsupported();
                var first = ClassAtom();
                if (_position + 1 < pattern.Length && pattern[_position] == '-' && pattern[_position + 1] != ']')
                {
                    _position++;
                    var last = ClassAtom();
                    if (first.Literal == null || last.Literal == null || first.Literal.Value.Value > last.Literal.Value.Value) throw Unsupported();
                    int lo = first.Literal.Value.Value, hi = last.Literal.Value.Value;
                    predicates.Add(r => InRange(r, lo, hi));
                }
                else predicates.Add(first.Predicate);
            }
            if (predicates.Count == 0 || _position >= pattern.Length || pattern[_position++] != ']') throw Unsupported();
            return r => predicates.Any(p => p(r)) != negate;
        }
        private (Func<Rune, bool> Predicate, Rune? Literal) ClassAtom()
        {
            if (pattern[_position] == '\\') { _position++; return Escape(); }
            Rune r = ReadRune();
            return (Equal(r), r);
        }
        private (Func<Rune, bool> Predicate, Rune? Literal) Escape()
        {
            if (_position >= pattern.Length) throw Unsupported();
            char c = pattern[_position++];
            if (c is 'd' or 'D') return (r => (Rune.GetUnicodeCategory(r) == UnicodeCategory.DecimalDigitNumber) == (c == 'd'), null);
            if (c is 's' or 'S') return (r => Rune.IsWhiteSpace(r) == (c == 's'), null);
            int value;
            if (c is 'x' or 'u')
            {
                int start = _position;
                bool braced = _position < pattern.Length && pattern[_position] == '{';
                if (braced)
                {
                    start = ++_position;
                    while (_position < pattern.Length && char.IsAsciiHexDigit(pattern[_position])) _position++;
                    if (_position >= pattern.Length || pattern[_position++] != '}') throw Unsupported();
                }
                else
                {
                    if (c != 'x' || _position + 2 > pattern.Length) throw Unsupported();
                    _position += 2;
                }
                string hex = pattern[start..(braced ? _position - 1 : _position)];
                if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value) || !Rune.IsValid(value)) throw Unsupported();
            }
            else value = c switch
            {
                'r' => '\r', 't' => '\t', 'f' => '\f', 'a' => '\a',
                _ when char.IsAsciiLetterOrDigit(c) => throw Unsupported(), _ => c
            };
            Rune rune = new(value);
            if (rune.Value == '\n') throw Unsupported();
            return (Equal(rune), rune);
        }
        private Rune ReadRune()
        {
            if (!Rune.TryGetRuneAt(pattern, _position, out Rune rune)) throw Unsupported();
            if (rune.Value == '\n') throw Unsupported();
            _position += rune.Utf16SequenceLength;
            return rune;
        }
        private Func<Rune, bool> Equal(Rune expected) => r => caseSensitive ? r == expected : Fold(r) == Fold(expected);
        private bool InRange(Rune r, int lo, int hi)
        {
            if (r.Value >= lo && r.Value <= hi) return true;
            if (caseSensitive) return false;
            Rune folded = Fold(r);
            for (int i = lo; i <= hi; i++) if (Rune.IsValid(i) && Fold(new Rune(i)) == folded) return true;
            return false;
        }
        private static ArgumentException Unsupported() => new("この構文はMidFD共通正規表現契約で未対応です。look-around、後方参照、特殊group、Unicode property/word class、inline flag、集合演算は使用できません。");
    }
}
