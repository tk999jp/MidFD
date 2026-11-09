using System.Text;
using System.Text.Json;

namespace MidFD.Services;

/// <summary>Applies bounded I/O and structural budgets to the small JSON persistence files.</summary>
internal static class JsonResourceSafety
{
    internal const int MaxJsonBytes = 64 * 1024 * 1024;
    internal const int MaxDepth = 64;
    internal const int MaxTokens = 1_000_000;
    internal const int MaxArrayItems = 100_000;
    internal const int MaxStringUtf8Bytes = 1024 * 1024;
    private const int ReadBufferSize = 16 * 1024;

    internal static string ReadValidatedJsonFile(string path, FileShare fileShare = FileShare.Read)
    {
        ArraySegment<byte> rawBytes = ReadBoundedFile(path, fileShare);
        ReadOnlySpan<byte> raw = rawBytes.AsSpan();
        bool hasUtf8Bom = raw.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF });
        bool hasOtherBom = raw.StartsWith(new byte[] { 0xFF, 0xFE })
            || raw.StartsWith(new byte[] { 0xFE, 0xFF })
            || raw.StartsWith(new byte[] { 0x00, 0x00, 0xFE, 0xFF });
        ReadOnlySpan<byte> utf8Candidate = hasUtf8Bom ? raw[3..] : raw;
        bool isValidUtf8 = !hasOtherBom && IsValidUtf8(utf8Candidate);
        if (isValidUtf8)
        {
            ValidateJson(utf8Candidate);
        }

        string text = DecodeTextWithinBudget(rawBytes);
        if (!isValidUtf8)
        {
            int utf8Length = Encoding.UTF8.GetByteCount(text);
            if (utf8Length > MaxJsonBytes)
                throw new InvalidDataException($"Decoded JSON exceeds the {MaxJsonBytes}-byte UTF-8 limit.");
            ValidateJson(Encoding.UTF8.GetBytes(text));
        }
        return text;
    }

    internal static void WriteJsonAtomically(string path, Action<Stream> serialize)
    {
        ArgumentNullException.ThrowIfNull(serialize);

        AtomicFileWriter.Write(path, stream =>
        {
            using var bounded = new BoundedWriteStream(stream, MaxJsonBytes);
            serialize(bounded);
            bounded.Flush();
            stream.Position = 0;
            ValidateJsonStream(stream);
            stream.Position = stream.Length;
        });
    }

    internal static void ValidateJson(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length > MaxJsonBytes)
            throw new InvalidDataException($"JSON exceeds the {MaxJsonBytes}-byte limit.");

        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions { MaxDepth = MaxDepth });
        var validation = new JsonStructureValidator();
        while (reader.Read()) validation.Visit(ref reader);
        validation.Complete();
    }

    private static void ValidateJsonStream(Stream stream)
    {
        long initialLength = stream.Length;
        if (initialLength > MaxJsonBytes)
            throw new InvalidDataException($"JSON exceeds the {MaxJsonBytes}-byte limit.");

        byte[] buffer = new byte[ReadBufferSize];
        int buffered = 0;
        long readBytes = 0;
        bool finalBlock = false;
        JsonReaderState state = new(new JsonReaderOptions { MaxDepth = MaxDepth });
        var validation = new JsonStructureValidator();

        while (true)
        {
            if (!finalBlock && buffered < buffer.Length)
            {
                int read = stream.Read(buffer, buffered, buffer.Length - buffered);
                if (read == 0)
                {
                    finalBlock = true;
                }
                else
                {
                    readBytes += read;
                    if (readBytes > MaxJsonBytes)
                        throw new InvalidDataException($"JSON exceeds the {MaxJsonBytes}-byte limit.");
                    buffered += read;
                }
            }

            int consumed = ProcessJsonChunk(buffer, buffered, finalBlock, state, validation, out state);
            if (consumed > 0)
            {
                Buffer.BlockCopy(buffer, consumed, buffer, 0, buffered - consumed);
                buffered -= consumed;
            }

            if (finalBlock)
            {
                if (buffered != 0)
                    throw new JsonException("JSON document is incomplete.");
                break;
            }

            if (buffered == buffer.Length && consumed == 0)
            {
                if (buffer.Length >= MaxJsonBytes)
                    throw new InvalidDataException("JSON token exceeds the available validation budget.");
                int nextSize = buffer.Length > MaxJsonBytes / 2 ? MaxJsonBytes : buffer.Length * 2;
                Array.Resize(ref buffer, nextSize);
            }
        }

        if (stream.Length != initialLength || readBytes != initialLength)
            throw new InvalidDataException("Temporary JSON changed while being validated.");
        validation.Complete();
    }

    private static int ProcessJsonChunk(
        byte[] buffer,
        int buffered,
        bool finalBlock,
        JsonReaderState state,
        JsonStructureValidator validation,
        out JsonReaderState nextState)
    {
        var reader = new Utf8JsonReader(buffer.AsSpan(0, buffered), finalBlock, state);
        while (reader.Read()) validation.Visit(ref reader);
        nextState = reader.CurrentState;
        return checked((int)reader.BytesConsumed);
    }

    private sealed class JsonStructureValidator
    {
        private readonly Stack<(int Depth, int Count)> _arrays = new();
        private int _tokenCount;
        private bool _hasRoot;

        internal void Visit(ref Utf8JsonReader reader)
        {
            _hasRoot = true;
            if (++_tokenCount > MaxTokens)
                throw new InvalidDataException($"JSON exceeds the {MaxTokens}-token limit.");

            JsonTokenType tokenType = reader.TokenType;
            if (tokenType is not (JsonTokenType.EndArray or JsonTokenType.EndObject or JsonTokenType.PropertyName)
                && _arrays.Count > 0
                && reader.CurrentDepth == _arrays.Peek().Depth + 1)
            {
                (int depth, int count) = _arrays.Pop();
                count++;
                if (count > MaxArrayItems)
                    throw new InvalidDataException($"JSON array exceeds the {MaxArrayItems}-element limit.");
                _arrays.Push((depth, count));
            }

            if (tokenType is JsonTokenType.String or JsonTokenType.PropertyName)
                ValidateString(ref reader);

            if (tokenType == JsonTokenType.StartArray)
                _arrays.Push((reader.CurrentDepth, 0));
            else if (tokenType == JsonTokenType.EndArray)
            {
                if (_arrays.Count == 0)
                    throw new JsonException("Unbalanced JSON array.");
                _arrays.Pop();
            }
        }

        internal void Complete()
        {
            if (!_hasRoot || _arrays.Count != 0)
                throw new JsonException("JSON document is empty or incomplete.");
        }
    }

    private static ArraySegment<byte> ReadBoundedFile(string path, FileShare fileShare)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, fileShare);
        long initialLength = stream.Length;
        if (initialLength > MaxJsonBytes)
            throw new InvalidDataException($"JSON exceeds the {MaxJsonBytes}-byte limit.");

        using var output = new MemoryStream(checked((int)initialLength));
        byte[] chunk = new byte[ReadBufferSize];
        while (true)
        {
            if (stream.Length != initialLength)
                throw new InvalidDataException("JSON file length changed while being read.");

            long remaining = MaxJsonBytes - output.Length;
            if (remaining == 0)
            {
                if (stream.ReadByte() >= 0 || stream.Length != initialLength)
                    throw new InvalidDataException($"JSON grew beyond the {MaxJsonBytes}-byte limit while being read.");
                break;
            }

            int read = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, remaining));
            if (read == 0) break;
            output.Write(chunk, 0, read);
        }

        if (stream.Length != initialLength || output.Length != initialLength)
            throw new InvalidDataException("JSON file length changed while being read.");
        if (!output.TryGetBuffer(out ArraySegment<byte> segment))
            throw new InvalidOperationException("Read JSON buffer is unavailable.");
        return new ArraySegment<byte>(segment.Array!, segment.Offset, checked((int)output.Length));
    }

    private static string DecodeTextWithinBudget(ArraySegment<byte> rawBytes)
    {
        int charCount = 0;
        long utf8Bytes = 0;
        char pendingHighSurrogate = '\0';
        using (var reader = OpenTextReader(rawBytes))
        {
            char[] chars = new char[ReadBufferSize];
            while (true)
            {
                int read = reader.Read(chars, 0, chars.Length);
                if (read == 0) break;
                charCount = checked(charCount + read);
                utf8Bytes += CountUtf8Bytes(chars.AsSpan(0, read), ref pendingHighSurrogate);
                if (utf8Bytes > MaxJsonBytes)
                    throw new InvalidDataException($"Decoded JSON exceeds the {MaxJsonBytes}-byte UTF-8 limit.");
            }
        }

        if (pendingHighSurrogate != '\0') utf8Bytes += 3;
        if (utf8Bytes > MaxJsonBytes)
            throw new InvalidDataException($"Decoded JSON exceeds the {MaxJsonBytes}-byte UTF-8 limit.");

        return string.Create(charCount, rawBytes, static (destination, bytes) => DecodeInto(destination, bytes));
    }

    private static StreamReader OpenTextReader(ArraySegment<byte> rawBytes)
    {
        var source = new MemoryStream(rawBytes.Array!, rawBytes.Offset, rawBytes.Count, writable: false, publiclyVisible: true);
        return new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: ReadBufferSize, leaveOpen: false);
    }

    private static void DecodeInto(Span<char> destination, ArraySegment<byte> rawBytes)
    {
        using StreamReader reader = OpenTextReader(rawBytes);
        char[] chars = new char[ReadBufferSize];
        int offset = 0;
        while (true)
        {
            int read = reader.Read(chars, 0, chars.Length);
            if (read == 0) break;
            chars.AsSpan(0, read).CopyTo(destination[offset..]);
            offset += read;
        }
        if (offset != destination.Length)
            throw new InvalidDataException("JSON text changed during decoding.");
    }

    private static bool IsValidUtf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            _ = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetCharCount(bytes);
            return true;
        }
        catch (DecoderFallbackException) { return false; }
    }

    private static long CountUtf8Bytes(ReadOnlySpan<char> chars, ref char pendingHighSurrogate)
    {
        long bytes = 0;
        int index = 0;
        if (pendingHighSurrogate != '\0')
        {
            if (chars.Length > 0 && char.IsLowSurrogate(chars[0]))
            {
                bytes += 4;
                index = 1;
            }
            else
            {
                bytes += 3;
            }
            pendingHighSurrogate = '\0';
        }

        for (; index < chars.Length; index++)
        {
            char value = chars[index];
            if (char.IsHighSurrogate(value))
            {
                if (index + 1 < chars.Length && char.IsLowSurrogate(chars[index + 1]))
                {
                    bytes += 4;
                    index++;
                }
                else if (index + 1 == chars.Length)
                {
                    pendingHighSurrogate = value;
                }
                else
                {
                    bytes += 3;
                }
            }
            else if (char.IsLowSurrogate(value))
            {
                bytes += 3;
            }
            else if (value <= 0x7f)
            {
                bytes++;
            }
            else if (value <= 0x7ff)
            {
                bytes += 2;
            }
            else
            {
                bytes += 3;
            }
        }

        return bytes;
    }

    private static void ValidateString(ref Utf8JsonReader reader)
    {
        long rawLength = reader.HasValueSequence ? reader.ValueSequence.Length : reader.ValueSpan.Length;
        if (rawLength > (long)MaxStringUtf8Bytes * 6 + 2)
            throw new InvalidDataException($"JSON string exceeds the {MaxStringUtf8Bytes}-byte decoded limit.");

        if (!reader.ValueIsEscaped)
        {
            if (rawLength > MaxStringUtf8Bytes)
                throw new InvalidDataException($"JSON string exceeds the {MaxStringUtf8Bytes}-byte limit.");
            return;
        }

        string value = reader.GetString() ?? string.Empty;
        if (Encoding.UTF8.GetByteCount(value) > MaxStringUtf8Bytes)
            throw new InvalidDataException($"JSON string exceeds the {MaxStringUtf8Bytes}-byte decoded limit.");
    }

    private sealed class BoundedWriteStream(Stream inner, long maxBytes) : Stream
    {
        private long written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => written;
        public override long Position { get => written; set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count)
            => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > maxBytes - written)
                throw new InvalidDataException($"Serialized JSON exceeds the {maxBytes}-byte limit.");
            inner.Write(buffer);
            written += buffer.Length;
        }

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { }
    }
}
