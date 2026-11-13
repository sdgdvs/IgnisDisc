using System.Buffers.Binary;
using System.Text;

namespace CdBurner.Core.Audio;

public enum GaplessCodec
{
    Mp3,
    Opus,
    Vorbis
}

/// <summary>
/// What a lossy file says about its own exact length. A decoder returns the encoder delay and padding as extra
/// samples; the numbers here say how many belong to the music, so consecutive tracks can be joined without added
/// samples.
/// </summary>
/// <param name="Codec">Stream type.</param>
/// <param name="SampleRate">Rate the decoder produces, in Hz (Opus always decodes at 48,000).</param>
/// <param name="Channels">Channel count of the stream.</param>
/// <param name="TrimmedSamples">Samples per channel that belong to the audio, or 0 when the file does not say.</param>
/// <param name="UntrimmedSamples">Samples per channel a decoder returns when it does no trimming at all.</param>
/// <param name="LeadingSkipWhenUntrimmed">Samples to drop from the start of an untrimmed decode.</param>
/// <param name="Comments">Vorbis comments (upper-case keys) for Opus and Vorbis files.</param>
public sealed record GaplessStreamInfo(
    GaplessCodec Codec,
    int SampleRate,
    int Channels,
    long TrimmedSamples,
    long UntrimmedSamples,
    long LeadingSkipWhenUntrimmed,
    IReadOnlyDictionary<string, string>? Comments = null)
{
    /// <summary>True when the file states an exact length.</summary>
    public bool HasExactLength => TrimmedSamples > 0;

    /// <summary>
    /// Reads the header of an MP3, Ogg Opus or Ogg Vorbis file. Returns null for other files and for files whose
    /// headers cannot be read (a plain MP3 without a Xing/Info frame has a codec and rate but no exact length, which is
    /// reported with <see cref="TrimmedSamples"/> = 0).
    /// </summary>
    public static GaplessStreamInfo? TryRead(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> probe = stackalloc byte[4];
            int n = fs.Read(probe);
            fs.Position = 0;
            if (n >= 4 && probe.SequenceEqual("OggS"u8))
            {
                return OggReader.Read(fs);
            }
            return Mp3Reader.Read(fs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException)
        {
            return null;
        }
    }

    /// <summary>
    /// Works out which part of a decoded buffer is the music. <paramref name="decodedSamples"/> is what the decoder
    /// returned. A decoder that already trimmed (the normal case for current ffmpeg) is left alone; one that returned
    /// the whole untrimmed stream loses its leading delay; extra samples at the end are always cut.
    /// </summary>
    public static (long Skip, long Take) PlanTrim(GaplessStreamInfo info, long decodedSamples)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (!info.HasExactLength || decodedSamples <= 0)
        {
            return (0, Math.Max(0, decodedSamples));
        }

        long skip = 0;
        if (decodedSamples == info.UntrimmedSamples && info.UntrimmedSamples > info.TrimmedSamples)
        {
            skip = info.LeadingSkipWhenUntrimmed;
        }

        long available = decodedSamples - skip;
        return (skip, Math.Clamp(available, 0, info.TrimmedSamples));
    }

    private static class Mp3Reader
    {
        // MPEG-1 Layer III bitrate-independent tables (ISO/IEC 11172-3 table 2.4.2.3).
        private static readonly int[] RatesMpeg1 = [44100, 48000, 32000];
        private static readonly int[] RatesMpeg2 = [22050, 24000, 16000];
        private static readonly int[] RatesMpeg25 = [11025, 12000, 8000];

        // The decoder delay of the common MP3 decoders (528 + 1) is added to the encoder delay in the LAME tag.
        private const int DecoderDelay = 529;

        public static GaplessStreamInfo? Read(FileStream fs)
        {
            long pos = SkipId3v2(fs);
            // Look for the first frame header within the first 64 KiB after the tag.
            byte[] buf = new byte[65536];
            fs.Position = pos;
            int got = fs.Read(buf, 0, buf.Length);
            for (int i = 0; i + 4 <= got; i++)
            {
                if (buf[i] != 0xFF || (buf[i + 1] & 0xE0) != 0xE0)
                {
                    continue;
                }
                int versionBits = (buf[i + 1] >> 3) & 3;
                int layerBits = (buf[i + 1] >> 1) & 3;
                int rateIndex = (buf[i + 2] >> 2) & 3;
                int bitrateIndex = (buf[i + 2] >> 4) & 0xF;
                if (versionBits == 1 || layerBits != 1 || rateIndex == 3 || bitrateIndex is 0 or 15)
                {
                    continue; // reserved version, not Layer III, reserved rate, free-format or invalid bitrate
                }

                bool mpeg1 = versionBits == 3;
                int rate = (versionBits switch { 3 => RatesMpeg1, 2 => RatesMpeg2, _ => RatesMpeg25 })[rateIndex];
                bool mono = ((buf[i + 3] >> 6) & 3) == 3;
                int samplesPerFrame = mpeg1 ? 1152 : 576;
                bool crc = (buf[i + 1] & 1) == 0;
                int sideInfo = mpeg1 ? (mono ? 17 : 32) : (mono ? 9 : 17);
                int tagAt = i + 4 + (crc ? 2 : 0) + sideInfo;

                long frames = 0, delay = 0, padding = 0;
                if (tagAt + 8 <= got &&
                    (buf.AsSpan(tagAt, 4).SequenceEqual("Xing"u8) || buf.AsSpan(tagAt, 4).SequenceEqual("Info"u8)))
                {
                    uint flags = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(tagAt + 4, 4));
                    int p = tagAt + 8;
                    if ((flags & 1) != 0 && p + 4 <= got) { frames = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(p, 4)); p += 4; }
                    if ((flags & 2) != 0) p += 4;   // byte count
                    if ((flags & 4) != 0) p += 100; // table of contents
                    if ((flags & 8) != 0) p += 4;   // quality indicator
                    // LAME extension: 9-byte encoder string, 12 bytes of other fields, then delay and padding (12 bits each).
                    if (frames > 0 && p + 24 <= got && (buf[p] == 'L' || buf[p] == 'l'))
                    {
                        int d = p + 21;
                        delay = (buf[d] << 4) | (buf[d + 1] >> 4);
                        padding = ((buf[d + 1] & 0xF) << 8) | buf[d + 2];
                    }
                }

                int channels = mono ? 1 : 2;
                long untrimmed = frames * samplesPerFrame;
                long trimmed = untrimmed - delay - padding;
                if (frames == 0 || delay + padding >= untrimmed || trimmed <= 0)
                {
                    return new GaplessStreamInfo(GaplessCodec.Mp3, rate, channels, 0, 0, 0);
                }
                return new GaplessStreamInfo(GaplessCodec.Mp3, rate, channels, trimmed, untrimmed, delay + DecoderDelay);
            }
            return null;
        }

        private static long SkipId3v2(FileStream fs)
        {
            Span<byte> h = stackalloc byte[10];
            fs.Position = 0;
            if (fs.Read(h) == 10 && h[0] == 'I' && h[1] == 'D' && h[2] == '3')
            {
                long size = (h[6] << 21) | (h[7] << 14) | (h[8] << 7) | h[9]; // synchsafe
                long total = 10 + size + ((h[5] & 0x10) != 0 ? 10 : 0);
                return Math.Min(total, fs.Length);
            }
            return 0;
        }
    }

    private static class OggReader
    {
        private const int MaxCommentPacket = 4 * 1024 * 1024;

        private sealed record Page(byte Flags, long Granule, uint Serial, byte[] SegmentTable, long DataOffset, int DataLength);

        public static GaplessStreamInfo? Read(FileStream fs)
        {
            var first = ReadPage(fs, 0);
            if (first is null || (first.Flags & 0x02) == 0)
            {
                return null;
            }

            byte[] head = ReadBytes(fs, first.DataOffset, Math.Min(first.DataLength, 64));
            GaplessCodec codec;
            int channels, rate;
            long preSkip = 0;
            if (head.Length >= 19 && head.AsSpan(0, 8).SequenceEqual("OpusHead"u8))
            {
                // RFC 7845 section 5.1: version, channels, pre-skip (LE16), input rate (LE32), gain, mapping family.
                codec = GaplessCodec.Opus;
                channels = head[9];
                preSkip = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(10, 2));
                rate = 48000; // an Opus decoder always produces 48 kHz; the stored rate is informational
            }
            else if (head.Length >= 16 && head[0] == 1 && head.AsSpan(1, 6).SequenceEqual("vorbis"u8))
            {
                // Vorbis I section 4.2.2: type, "vorbis", version (LE32), channels, rate (LE32).
                codec = GaplessCodec.Vorbis;
                channels = head[11];
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(12, 4));
            }
            else
            {
                return null; // Ogg FLAC, Speex or similar
            }

            // Walk the page headers: the last granule position of this stream is its length in samples.
            long lastGranule = 0;
            long pos = first.DataOffset + first.DataLength;
            Page? second = null;
            while (pos < fs.Length)
            {
                var page = ReadPage(fs, pos);
                if (page is null)
                {
                    break;
                }
                if ((page.Flags & 0x02) != 0 && page.Serial != first.Serial)
                {
                    return null; // multiplexed or chained streams: let the decoder deal with them untrimmed
                }
                if (page.Serial == first.Serial)
                {
                    second ??= page;
                    if (page.Granule >= 0)
                    {
                        lastGranule = page.Granule;
                    }
                }
                pos = page.DataOffset + page.DataLength;
            }

            long trimmed = codec == GaplessCodec.Opus ? lastGranule - preSkip : lastGranule;
            IReadOnlyDictionary<string, string>? comments = second is null ? null : ReadComments(fs, second, codec);
            if (trimmed <= 0)
            {
                return new GaplessStreamInfo(codec, rate, channels, 0, 0, 0, comments);
            }
            return new GaplessStreamInfo(codec, rate, channels, trimmed, lastGranule, preSkip, comments);
        }

        private static Page? ReadPage(FileStream fs, long offset)
        {
            if (offset + 27 > fs.Length)
            {
                return null;
            }
            byte[] h = ReadBytes(fs, offset, 27);
            if (!h.AsSpan(0, 4).SequenceEqual("OggS"u8) || h[4] != 0)
            {
                return null;
            }
            int segments = h[26];
            byte[] table = ReadBytes(fs, offset + 27, segments);
            if (table.Length != segments)
            {
                return null;
            }
            int dataLen = 0;
            foreach (byte b in table) dataLen += b;
            long dataOffset = offset + 27 + segments;
            if (dataOffset + dataLen > fs.Length)
            {
                return null; // truncated final page
            }
            return new Page(h[5], BinaryPrimitives.ReadInt64LittleEndian(h.AsSpan(6, 8)),
                BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(14, 4)), table, dataOffset, dataLen);
        }

        private static byte[] ReadBytes(FileStream fs, long offset, int count)
        {
            byte[] b = new byte[count];
            fs.Position = offset;
            int total = 0;
            while (total < count)
            {
                int r = fs.Read(b, total, count - total);
                if (r == 0) break;
                total += r;
            }
            return total == count ? b : b[..total];
        }

        private static IReadOnlyDictionary<string, string>? ReadComments(FileStream fs, Page page, GaplessCodec codec)
        {
            // The comment header is the first packet of the second page; take it only if it ends on this page.
            using var packet = new MemoryStream();
            int p = 0;
            bool complete = false;
            foreach (byte seg in page.SegmentTable)
            {
                packet.Write(ReadBytes(fs, page.DataOffset + p, seg));
                p += seg;
                if (seg < 255) { complete = true; break; }
            }
            if (!complete || packet.Length > MaxCommentPacket)
            {
                return null;
            }

            byte[] data = packet.ToArray();
            int pos = codec == GaplessCodec.Opus ? 8 : 7; // "OpusTags" or 0x03 + "vorbis"
            if (data.Length < pos + 8 ||
                (codec == GaplessCodec.Opus && !data.AsSpan(0, 8).SequenceEqual("OpusTags"u8)) ||
                (codec == GaplessCodec.Vorbis && !(data[0] == 3 && data.AsSpan(1, 6).SequenceEqual("vorbis"u8))))
            {
                return null;
            }

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            uint vendor = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4));
            pos += 4;
            if (vendor > data.Length - pos - 4) return null;
            pos += (int)vendor;
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4));
            pos += 4;
            for (uint i = 0; i < count && pos + 4 <= data.Length; i++)
            {
                uint len = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4));
                pos += 4;
                if (len > data.Length - pos) break;
                string entry = Encoding.UTF8.GetString(data, pos, (int)len);
                pos += (int)len;
                int eq = entry.IndexOf('=');
                if (eq > 0)
                {
                    result.TryAdd(entry[..eq].ToUpperInvariant(), entry[(eq + 1)..]);
                }
            }
            return result;
        }
    }
}
