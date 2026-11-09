using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace MidFD;

// Bounded metadata reader, not a demuxer/decoder. Unknown/truncated structures do
// not become negative capability evidence. Codec strings follow RFC 6381 and are
// formed only from actual avcC / MPEG-4 DecoderSpecificInfo, never an extension.
internal sealed record VideoContainerProbe(string Name, string? Mime, IReadOnlyList<string> AvcCodecs, IReadOnlyList<string> AacCodecs)
{
    private readonly record struct IsoBoxRange(string Type, int BodyOffset, int BodyLength)
    {
        internal ReadOnlySpan<byte> Body(ReadOnlySpan<byte> source) => source.Slice(BodyOffset, BodyLength);
    }

    private static readonly HashSet<string> SupportedMp4Brands = new(StringComparer.Ordinal)
    {
        "isom", "iso2", "iso3", "iso4", "iso5", "iso6", "mp41", "mp42", "avc1", "M4V "
    };

    // Bounded fast path for the common H.264/AAC ISO BMFF shape. It returns a
    // complete codec record only when every media track and display transform
    // needed by Auto is understood; all other files use the LibVLC parser.
    internal static async Task<VideoCodecInfo?> TryReadSimpleMp4Async(string path, CancellationToken cancel)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (file.Length < 16) return null;

        byte[] header = new byte[16];
        await file.ReadExactlyAsync(header.AsMemory(0, 8), cancel);
        if (!header.AsSpan(4, 4).SequenceEqual("ftyp"u8)) return null;
        ulong ftypSize = BinaryPrimitives.ReadUInt32BigEndian(header);
        int ftypHeaderSize = 8;
        if (ftypSize == 1)
        {
            await file.ReadExactlyAsync(header.AsMemory(8, 8), cancel);
            ftypSize = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
            ftypHeaderSize = 16;
        }
        if (ftypSize < (ulong)(ftypHeaderSize + 8) || ftypSize > 4096 || ftypSize > (ulong)file.Length) return null;
        byte[] ftyp = new byte[(int)ftypSize - ftypHeaderSize];
        await file.ReadExactlyAsync(ftyp, cancel);
        if (ftyp.Length < 8 || (ftyp.Length - 8) % 4 != 0) return null;
        string majorBrand = Encoding.ASCII.GetString(ftyp, 0, 4);
        // Preserve the existing QuickTime classification even if the file also
        // advertises an ISO compatible brand; its track semantics need LibVLC.
        if (majorBrand == "qt  ") return null;
        bool compatibleBrandSupported = false;
        for (int offset = 8; offset + 4 <= ftyp.Length; offset += 4)
            compatibleBrandSupported |= SupportedMp4Brands.Contains(Encoding.ASCII.GetString(ftyp, offset, 4));
        if (!SupportedMp4Brands.Contains(majorBrand) && !compatibleBrandSupported) return null;

        byte[]? movie = null;
        for (int index = 0; index < 128 && file.Position + 8 <= file.Length; index++)
        {
            cancel.ThrowIfCancellationRequested();
            long start = file.Position;
            await file.ReadExactlyAsync(header.AsMemory(0, 8), cancel);
            ulong size = BinaryPrimitives.ReadUInt32BigEndian(header);
            string type = Encoding.ASCII.GetString(header, 4, 4);
            int prefix = 8;
            if (size == 1)
            {
                if (file.Position + 8 > file.Length) return null;
                await file.ReadExactlyAsync(header.AsMemory(8, 8), cancel);
                size = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8)); prefix = 16;
            }
            if (size == 0) size = (ulong)(file.Length - start);
            if (size < (ulong)prefix || size > (ulong)(file.Length - start)) return null;
            if (type == "moov")
            {
                if (movie != null || size - (ulong)prefix > 8 * 1024 * 1024) return null;
                movie = new byte[(int)size - prefix];
                await file.ReadExactlyAsync(movie, cancel);
            }
            file.Position = start + (long)size;
            if (size == 0) break;
        }
        if (movie == null) return null;

        int boxCount = 0;
        if (!TryReadBoxRanges(movie, 0, ref boxCount, out var movieBoxes)) return null;
        var tracks = new List<VideoCodecTrack>();
        string? videoCodec = null, audioCodec = null;
        bool embeddedSubtitles = false;
        string? videoMetadata = null, videoToolTip = null, audioMetadata = null, audioToolTip = null;
        Size displaySize = Size.Empty;
        var ids = new HashSet<int>();

        foreach (IsoBoxRange trackRange in movieBoxes.Where(box => box.Type == "trak"))
        {
            if (!TryReadBoxRanges(trackRange.Body(movie), 0, ref boxCount, out var trackBoxes)
                || !TryGetSingleBox(trackBoxes, "tkhd", out var tkhdRange)
                || !TryGetSingleBox(trackBoxes, "mdia", out var mdiaRange)
                || !TryReadTrackHeader(tkhdRange.Body(trackRange.Body(movie)), out int trackId, out bool identityMatrix)
                || !ids.Add(trackId)) return null;

            ReadOnlySpan<byte> mdia = mdiaRange.Body(trackRange.Body(movie));
            if (!TryReadBoxRanges(mdia, 0, ref boxCount, out var mdiaBoxes)
                || !TryGetSingleBox(mdiaBoxes, "hdlr", out var hdlrRange)
                || !TryGetSingleBox(mdiaBoxes, "minf", out var minfRange)) return null;
            ReadOnlySpan<byte> hdlr = hdlrRange.Body(mdia);
            if (hdlr.Length < 12 || hdlr[0] != 0) return null;
            string handler = Encoding.ASCII.GetString(hdlr.Slice(8, 4));
            if (handler is "text" or "subt" or "sbtl" or "clcp")
            {
                embeddedSubtitles = true;
                continue;
            }
            if (handler is not ("vide" or "soun")) return null;
            if (handler == "vide" && !identityMatrix) return null;

            ReadOnlySpan<byte> minf = minfRange.Body(mdia);
            if (!TryReadBoxRanges(minf, 0, ref boxCount, out var minfBoxes)
                || !TryGetSingleBox(minfBoxes, "stbl", out var stblRange)) return null;
            ReadOnlySpan<byte> stbl = stblRange.Body(minf);
            if (!TryReadBoxRanges(stbl, 0, ref boxCount, out var stblBoxes)
                || !TryGetSingleBox(stblBoxes, "stsd", out var stsdRange)) return null;
            ReadOnlySpan<byte> stsd = stsdRange.Body(stbl);
            if (stsd.Length < 8 || BinaryPrimitives.ReadUInt32BigEndian(stsd[4..]) != 1
                || !TryReadBoxRanges(stsd, 8, ref boxCount, out var sampleBoxes) || sampleBoxes.Count != 1) return null;

            IsoBoxRange sample = sampleBoxes[0];
            ReadOnlySpan<byte> entry = sample.Body(stsd);
            if (handler == "vide")
            {
                if (sample.Type is not ("avc1" or "avc3") || entry.Length < 78
                    || !TryReadBoxRanges(entry, 78, ref boxCount, out var entryBoxes)
                    || entryBoxes.Any(box => box.Type is "pasp" or "clap" or "sinf")
                    || !TryGetSingleBox(entryBoxes, "avcC", out var configRange)) return null;
                ReadOnlySpan<byte> config = configRange.Body(entry);
                uint width = BinaryPrimitives.ReadUInt16BigEndian(entry[24..]);
                uint height = BinaryPrimitives.ReadUInt16BigEndian(entry[26..]);
                if (config.Length < 4 || config[0] != 1 || width == 0 || height == 0) return null;
                int profile = config[1], compatibility = config[2], level = config[3];
                string codec = $"{sample.Type}.{profile:X2}{compatibility:X2}{level:X2}";
                tracks.Add(new(VideoCodecTrackKind.Video, trackId, FourCc("h264"), FourCc(sample.Type), profile, level, Width: width, Height: height));
                videoMetadata = $"H.264 / {width}×{height} / -";
                videoToolTip = $"codec: h264\ncontainer tag: {sample.Type}\nprofile: {profile}\nlevel: {level}";
                displaySize = new Size(checked((int)width), checked((int)height));
                videoCodec = codec;
            }
            else
            {
                if (sample.Type != "mp4a" || entry.Length < 28
                    || BinaryPrimitives.ReadUInt16BigEndian(entry[8..]) != 0
                    || !TryReadBoxRanges(entry, 28, ref boxCount, out var entryBoxes)
                    || entryBoxes.Any(box => box.Type == "sinf")
                    || !TryGetSingleBox(entryBoxes, "esds", out var esdsRange)) return null;
                ReadOnlySpan<byte> esds = esdsRange.Body(entry);
                if (esds.Length < 4 || ReadAudioDescriptor(esds[4..], 0) is not { } codec || !codec.StartsWith("mp4a.40.", StringComparison.Ordinal)) return null;
                uint channels = BinaryPrimitives.ReadUInt16BigEndian(entry[16..]);
                uint sampleRate = BinaryPrimitives.ReadUInt32BigEndian(entry[24..]) >> 16;
                if (channels == 0 || sampleRate == 0) return null;
                tracks.Add(new(VideoCodecTrackKind.Audio, trackId, FourCc("mp4a"), FourCc(sample.Type), Channels: channels, SampleRate: sampleRate));
                audioMetadata = $"AAC / {(sampleRate / 1000d).ToString("0.###", CultureInfo.InvariantCulture)} kHz / {channels}ch";
                audioToolTip = $"codec: {codec}\ncontainer tag: mp4a\nsample rate: {sampleRate} Hz\nchannels: {channels}";
                audioCodec = codec;
            }
        }

        if (tracks.Count is 0 or > 2 || tracks.Count(track => track.Kind == VideoCodecTrackKind.Video) != 1
            || tracks.Count(track => track.Kind == VideoCodecTrackKind.Audio) > 1 || videoCodec == null
            || tracks.Any(track => track.Kind == VideoCodecTrackKind.Audio) != (audioCodec != null)) return null;
        string mimeWithCodecs = $"video/mp4; codecs=\"{string.Join(",", new[] { videoCodec, audioCodec }.Where(codec => codec != null))}\"";
        var metadata = new VideoPlaybackMetadata(videoMetadata ?? "-", audioMetadata ?? "-", videoToolTip ?? "-", audioToolTip ?? "-");
        return new VideoCodecInfo(Path.GetFullPath(path), true, "ISO BMFF", "video/mp4", mimeWithCodecs, tracks,
            Metadata: metadata, DisplaySize: displaySize, EmbeddedSubtitlesPresent: embeddedSubtitles);
    }

    private static bool TryReadBoxRanges(ReadOnlySpan<byte> data, int skip, ref int boxCount, out List<IsoBoxRange> boxes)
    {
        boxes = [];
        int position = skip;
        while (position < data.Length)
        {
            if (position + 8 > data.Length || ++boxCount > 4096) return false;
            ulong size = BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
            string type = Encoding.ASCII.GetString(data.Slice(position + 4, 4));
            int prefix = 8;
            if (size == 1)
            {
                if (position + 16 > data.Length) return false;
                size = BinaryPrimitives.ReadUInt64BigEndian(data[(position + 8)..]); prefix = 16;
            }
            if (size == 0) size = (ulong)(data.Length - position);
            if (size < (ulong)prefix || size > (ulong)(data.Length - position) || size > int.MaxValue) return false;
            boxes.Add(new(type, position + prefix, (int)size - prefix));
            position += (int)size;
        }
        return position == data.Length;
    }

    private static bool TryGetSingleBox(IReadOnlyList<IsoBoxRange> boxes, string type, out IsoBoxRange result)
    {
        result = default;
        bool found = false;
        foreach (IsoBoxRange box in boxes)
        {
            if (box.Type != type) continue;
            if (found) return false;
            result = box;
            found = true;
        }
        return found;
    }

    private static bool TryReadTrackHeader(ReadOnlySpan<byte> data, out int trackId, out bool identityMatrix)
    {
        trackId = 0;
        identityMatrix = false;
        if (data.Length < 4) return false;
        byte version = data[0];
        int idOffset = version switch { 0 => 12, 1 => 20, _ => -1 };
        int matrixOffset = version switch { 0 => 40, 1 => 52, _ => -1 };
        if (idOffset < 0 || data.Length < matrixOffset + 36) return false;
        uint id = BinaryPrimitives.ReadUInt32BigEndian(data[idOffset..]);
        if (id == 0 || id > int.MaxValue) return false;
        trackId = (int)id;
        int[] identity = [0x00010000, 0, 0, 0, 0x00010000, 0, 0, 0, 0x40000000];
        for (int index = 0; index < identity.Length; index++)
        {
            if (BinaryPrimitives.ReadInt32BigEndian(data[(matrixOffset + index * 4)..]) != identity[index]) return true;
        }
        identityMatrix = true;
        return true;
    }

    private static uint FourCc(string value)
        => BitConverter.ToUInt32(Encoding.ASCII.GetBytes(value.PadRight(4)));

    internal static async Task<VideoContainerProbe> ReadAsync(string path, bool hasVideo, CancellationToken cancel)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.Asynchronous | FileOptions.RandomAccess);
        byte[] header = new byte[16];
        int read = await file.ReadAsync(header, cancel);
        if (read >= 12 && header.AsSpan(4, 4).SequenceEqual("ftyp"u8))
        {
            var avc = new List<string>(); var aac = new List<string>();
            file.Position = 0;
            for (int index = 0; index < 128 && file.Position + 8 <= file.Length; index++)
            {
                long start = file.Position;
                await file.ReadExactlyAsync(header.AsMemory(0, 8), cancel);
                ulong size = BinaryPrimitives.ReadUInt32BigEndian(header);
                string type = Encoding.ASCII.GetString(header, 4, 4);
                int prefix = 8;
                if (size == 1)
                {
                    await file.ReadExactlyAsync(header.AsMemory(8, 8), cancel);
                    size = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8)); prefix = 16;
                }
                if (size == 0) size = (ulong)(file.Length - start);
                if (size < (ulong)prefix || size > (ulong)(file.Length - start)) break;
                if (type == "moov" && size - (ulong)prefix <= 8 * 1024 * 1024)
                {
                    byte[] data = new byte[(int)size - prefix];
                    await file.ReadExactlyAsync(data, cancel);
                    int boxes = 0; ReadBoxes(data, avc, aac, 0, ref boxes);
                    break;
                }
                file.Position = start + (long)size;
            }
            // QuickTime is a different media type, even though both use boxes.
            file.Position = 8; await file.ReadExactlyAsync(header.AsMemory(0, 4), cancel);
            string brand = Encoding.ASCII.GetString(header, 0, 4);
            return new(brand == "qt  " ? "QuickTime" : "ISO BMFF", brand == "qt  " ? "video/quicktime" : hasVideo ? "video/mp4" : "audio/mp4", avc, aac);
        }
        if (read >= 12 && header.AsSpan(0, 4).SequenceEqual("RIFF"u8))
            return header.AsSpan(8, 4).SequenceEqual("WAVE"u8) ? new("WAVE", "audio/wav", [], [])
                : header.AsSpan(8, 4).SequenceEqual("AVI "u8) ? new("AVI", null, [], []) : new("RIFF", null, [], []);
        if (read >= 4 && header.AsSpan(0, 4).SequenceEqual("fLaC"u8)) return new("FLAC", "audio/flac", [], []);
        if (read >= 4 && header.AsSpan(0, 4).SequenceEqual("OggS"u8)) return new("Ogg", hasVideo ? "video/ogg" : "audio/ogg", [], []);
        if (read >= 4 && header.AsSpan(0, 4).SequenceEqual(new byte[] { 0x1a, 0x45, 0xdf, 0xa3 })) return new("EBML", null, [], []);
        return new("判定不能", null, [], []);
    }

    private static void ReadBoxes(ReadOnlySpan<byte> data, List<string> avc, List<string> aac, int depth, ref int boxes)
    {
        if (depth > 10) return;
        int position = 0;
        while (position + 8 <= data.Length && ++boxes <= 4096)
        {
            ulong size = BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
            string type = Encoding.ASCII.GetString(data.Slice(position + 4, 4));
            int prefix = 8;
            if (size == 1)
            {
                if (position + 16 > data.Length) return;
                size = BinaryPrimitives.ReadUInt64BigEndian(data[(position + 8)..]); prefix = 16;
            }
            if (size == 0) size = (ulong)(data.Length - position);
            if (size < (ulong)prefix || size > (ulong)(data.Length - position)) return;
            var body = data.Slice(position + prefix, (int)size - prefix);
            int skip = type switch
            {
                "moov" or "trak" or "mdia" or "minf" or "stbl" or "wave" => 0,
                "stsd" => 8,
                "avc1" or "avc3" => 78,
                "mp4a" when body.Length >= 10 => BinaryPrimitives.ReadUInt16BigEndian(body[8..]) switch { 0 => 28, 1 => 44, 2 => 64, _ => int.MaxValue },
                _ => int.MaxValue
            };
            if (type is "avc1" or "avc3")
                ReadSampleEntry(body, 78, type, avc);
            if (type == "mp4a" && skip <= body.Length)
                ReadSampleEntry(body, skip, type, aac);
            // Sample entry children have already been visited by ReadSampleEntry.
            if (type is not ("avc1" or "avc3" or "mp4a") && skip <= body.Length)
                ReadBoxes(body[skip..], avc, aac, depth + 1, ref boxes);
            position += (int)size;
        }
    }

    private static void ReadSampleEntry(ReadOnlySpan<byte> data, int offset, string entry, List<string> result)
    {
        int count = 0;
        while (offset + 8 <= data.Length && ++count <= 256)
        {
            uint size = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);
            if (size < 8 || size > data.Length - offset) return;
            string type = Encoding.ASCII.GetString(data.Slice(offset + 4, 4));
            var body = data.Slice(offset + 8, (int)size - 8);
            if (type == "avcC" && (entry is "avc1" or "avc3") && body.Length >= 4 && body[0] == 1)
                result.Add($"{entry}.{body[1]:X2}{body[2]:X2}{body[3]:X2}");
            else if (type == "esds" && entry == "mp4a" && body.Length >= 4)
            {
                string? codec = ReadAudioDescriptor(body[4..], 0);
                if (codec != null) result.Add(codec);
            }
            offset += (int)size;
        }
    }

    private static string? ReadAudioDescriptor(ReadOnlySpan<byte> data, int depth)
    {
        if (depth > 3) return null;
        int offset = 0;
        while (offset < data.Length)
        {
            byte tag = data[offset++]; int length = 0, octets = 0; byte value;
            do
            {
                if (offset >= data.Length || ++octets > 4) return null;
                value = data[offset++]; length = (length << 7) | (value & 0x7f);
            } while ((value & 0x80) != 0);
            if (length > data.Length - offset) return null;
            var body = data.Slice(offset, length);
            if (tag == 3 && body.Length >= 3)
            {
                int skip = 3;
                if ((body[2] & 0x80) != 0) skip += 2;
                if ((body[2] & 0x40) != 0)
                {
                    if (skip >= body.Length) return null;
                    skip += 1 + body[skip];
                }
                if ((body[2] & 0x20) != 0) skip += 2;
                if (skip <= body.Length && ReadAudioDescriptor(body[skip..], depth + 1) is { } nested) return nested;
            }
            if (tag == 4 && body.Length >= 13 && body[0] == 0x40 && (body[1] >> 2) == 5)
            {
                // Audio DecoderConfigDescriptor, MPEG-4 Audio OTI=0x40.
                var config = body[13..];
                if (config.Length >= 3 && config[0] == 5)
                {
                    int cursor = 1, n = 0, size = 0; byte b;
                    do { if (cursor >= config.Length || ++n > 4) return null; b = config[cursor++]; size = (size << 7) | (b & 0x7f); } while ((b & 0x80) != 0);
                    if (size < 2 || size > config.Length - cursor) return null;
                    int audioObjectType = config[cursor] >> 3;
                    if (audioObjectType == 31) audioObjectType = 32 + ((config[cursor] & 7) << 3) + (config[cursor + 1] >> 5);
                    if (audioObjectType > 0) return $"mp4a.40.{audioObjectType}";
                }
            }
            offset += length;
        }
        return null;
    }

    internal string? ComposeMime(IReadOnlyList<VideoCodecTrack> tracks)
    {
        if (Mime is not ("video/mp4" or "audio/mp4") || tracks.Count == 0) return null;
        var video = tracks.Where(track => track.Kind == VideoCodecTrackKind.Video).ToArray();
        var audio = tracks.Where(track => track.Kind == VideoCodecTrackKind.Audio).ToArray();
        // Do not guess the association of multiple sample descriptions/tracks.
        if (video.Length > 1 || audio.Length > 1 || AvcCodecs.Count != video.Length || AacCodecs.Count != audio.Length) return null;
        if (video.Any(track => track.CodecName is not ("h264" or "avc1" or "avc3")) || audio.Any(track => track.CodecName != "mp4a")) return null;
        string[] codecs = AvcCodecs.Concat(AacCodecs).ToArray();
        return codecs.Length == 0 ? null : $"{Mime}; codecs=\"{string.Join(",", codecs)}\"";
    }

    internal VideoCodecTrack[] AddAvcConfiguration(VideoCodecTrack[] tracks)
    {
        var video = tracks.Where(track => track.Kind == VideoCodecTrackKind.Video).ToArray();
        if (video.Length != 1 || AvcCodecs.Count != 1 || video[0].CodecName is not ("h264" or "avc1" or "avc3")) return tracks;
        string hex = AvcCodecs[0].Split('.')[1];
        int profile = Convert.ToInt32(hex[..2], 16), level = Convert.ToInt32(hex[4..], 16);
        return tracks.Select(track => ReferenceEquals(track, video[0]) ? track with { Profile = track.Profile ?? profile, Level = track.Level ?? level } : track).ToArray();
    }
}
