using System.Text;
using System.Text.RegularExpressions;

namespace CdBurner.Core.Audio;

/// <summary>
/// Text tags read from an audio file, used for CD-Text. Every field is optional.
/// </summary>
public sealed record AudioTags
{
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public string? AlbumArtist { get; init; }
    public string? Composer { get; init; }
    public string? Songwriter { get; init; }
    public string? Isrc { get; init; }

    public bool IsEmpty =>
        Title is null && Artist is null && Album is null && AlbumArtist is null &&
        Composer is null && Songwriter is null && Isrc is null;

    /// <summary>Fills each empty field of this set from <paramref name="other"/>.</summary>
    public AudioTags FillFrom(AudioTags? other) => other is null ? this : new AudioTags
    {
        Title = Title ?? other.Title,
        Artist = Artist ?? other.Artist,
        Album = Album ?? other.Album,
        AlbumArtist = AlbumArtist ?? other.AlbumArtist,
        Composer = Composer ?? other.Composer,
        Songwriter = Songwriter ?? other.Songwriter,
        Isrc = Isrc ?? other.Isrc,
    };

    /// <summary>
    /// Returns the ISRC in its 12-character form (country, registrant, year, designation), or null when the text is
    /// not a valid ISRC. A bad ISRC tag must never stop a burn, so it is dropped rather than passed on.
    /// </summary>
    public static string? NormalizeIsrc(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string compact = text.Replace("-", "").Replace(" ", "").Trim().ToUpperInvariant();
        return Regex.IsMatch(compact, "^[A-Z]{2}[A-Z0-9]{3}[0-9]{7}$") ? compact : null;
    }
}

/// <summary>
/// Reads the text frames of ID3 tags (versions 2.2, 2.3 and 2.4 at the start of the file, version 1 in the last 128
/// bytes) as written by MP3 encoders and music libraries. Read-only; never throws for a malformed tag, it just
/// returns what it could read.
/// </summary>
/// <remarks>
/// Layouts follow the id3.org specifications: ID3v2.3.0 (whole-tag unsynchronisation, 4-byte frame IDs, plain
/// 32-bit frame sizes), ID3v2.4.0 (synchsafe frame sizes, per-frame unsynchronisation and data length indicator,
/// UTF-8 text, null-separated multiple values) and ID3v2.2 (3-byte IDs and sizes).
/// </remarks>
public static class Id3TagReader
{
    /// <summary>Text frames larger than this are ignored; a cover picture may be larger, but it is skipped by size.</summary>
    private const int MaxTagBytes = 64 * 1024 * 1024;

    public static AudioTags? TryRead(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return TryRead(stream);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Reads ID3v2 from the start of the stream and fills gaps from ID3v1 at its end.</summary>
    public static AudioTags? TryRead(Stream stream)
    {
        AudioTags? v2 = ReadV2(stream);
        AudioTags? v1 = ReadV1(stream);
        AudioTags? merged = v2 is null ? v1 : v2.FillFrom(v1);
        return merged is null || merged.IsEmpty ? null : merged;
    }

    private static AudioTags? ReadV2(Stream stream)
    {
        if (!stream.CanSeek || stream.Length < 10) return null;
        stream.Position = 0;
        var header = new byte[10];
        if (!ReadExactly(stream, header)) return null;
        if (header[0] != 'I' || header[1] != 'D' || header[2] != '3') return null;

        int major = header[3];
        if (major < 2 || major > 4 || header[4] == 0xFF) return null;
        byte flags = header[5];
        if ((header[6] | header[7] | header[8] | header[9]) >= 0x80) return null;
        int size = Synchsafe(header, 6);
        if (size <= 0 || size > MaxTagBytes) return null;

        var body = new byte[Math.Min(size, (int)Math.Max(0, stream.Length - 10))];
        if (!ReadExactly(stream, body)) return null;

        bool tagUnsync = (flags & 0x80) != 0;
        if (major == 2 && (flags & 0x40) != 0) return null; // v2.2 compression: no defined scheme, skip the tag.
        if (major == 3 && tagUnsync) body = RemoveUnsync(body);

        int pos = 0;
        if (major >= 3 && (flags & 0x40) != 0 && body.Length >= 4)
        {
            // Extended header: v2.3 size excludes its own 4 bytes; v2.4 size is synchsafe and includes them.
            pos = major == 3 ? 4 + BigEndian(body, 0) : Synchsafe(body, 0);
            if (pos < 0 || pos > body.Length) return null;
        }

        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        int idLength = major == 2 ? 3 : 4;
        int headerLength = major == 2 ? 6 : 10;
        while (pos + headerLength <= body.Length)
        {
            if (body[pos] == 0) break; // padding
            string id = Encoding.ASCII.GetString(body, pos, idLength);
            if (!IsFrameId(id)) break;

            int frameSize;
            byte formatFlags = 0;
            if (major == 2)
            {
                frameSize = (body[pos + 3] << 16) | (body[pos + 4] << 8) | body[pos + 5];
            }
            else if (major == 3)
            {
                frameSize = BigEndian(body, pos + 4);
                formatFlags = body[pos + 9];
            }
            else
            {
                frameSize = FrameSizeV24(body, pos);
                formatFlags = body[pos + 9];
            }

            int dataStart = pos + headerLength;
            if (frameSize < 0 || (long)dataStart + frameSize > body.Length) break; // long: a corrupt size must not wrap
            pos = dataStart + frameSize;

            string key = CanonicalId(id);
            if (key.Length == 0 || found.ContainsKey(key)) continue;

            byte[] data = body.AsSpan(dataStart, frameSize).ToArray();
            if (!UnwrapFrame(major, formatFlags, tagUnsync, ref data)) continue;

            string? text = DecodeTextFrame(data);
            if (!string.IsNullOrWhiteSpace(text)) found[key] = text;
        }

        if (found.Count == 0) return null;
        return new AudioTags
        {
            Title = found.GetValueOrDefault("TIT2"),
            Artist = found.GetValueOrDefault("TPE1"),
            Album = found.GetValueOrDefault("TALB"),
            AlbumArtist = found.GetValueOrDefault("TPE2"),
            Composer = found.GetValueOrDefault("TCOM"),
            Songwriter = found.GetValueOrDefault("TEXT"),
            Isrc = AudioTags.NormalizeIsrc(found.GetValueOrDefault("TSRC")),
        };
    }

    /// <summary>Strips grouping, data length and unsynchronisation from a frame body; false for compressed or encrypted frames.</summary>
    private static bool UnwrapFrame(int major, byte formatFlags, bool tagUnsync, ref byte[] data)
    {
        if (major == 3)
        {
            if ((formatFlags & 0x80) != 0 || (formatFlags & 0x40) != 0) return false; // compressed / encrypted
            if ((formatFlags & 0x20) != 0) data = Skip(data, 1);                       // group identifier
            return true;
        }

        if (major == 4)
        {
            if ((formatFlags & 0x08) != 0 || (formatFlags & 0x04) != 0) return false; // compressed / encrypted
            if ((formatFlags & 0x40) != 0) data = Skip(data, 1);                       // group identifier
            if ((formatFlags & 0x01) != 0) data = Skip(data, 4);                       // data length indicator
            if (tagUnsync || (formatFlags & 0x02) != 0) data = RemoveUnsync(data);
        }

        return true;
    }

    private static string CanonicalId(string id) => id switch
    {
        "TIT2" or "TT2" => "TIT2",
        "TPE1" or "TP1" => "TPE1",
        "TALB" or "TAL" => "TALB",
        "TPE2" or "TP2" => "TPE2",
        "TCOM" or "TCM" => "TCOM",
        "TEXT" or "TXT" => "TEXT",
        "TSRC" or "TRC" => "TSRC",
        _ => string.Empty,
    };

    /// <summary>
    /// Decodes a text frame: one encoding byte (0 ISO-8859-1, 1 UTF-16 with BOM, 2 UTF-16BE, 3 UTF-8), then one or
    /// more null-terminated strings. Several values (ID3v2.4) are joined with "; ".
    /// </summary>
    public static string? DecodeTextFrame(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2) return null;
        byte encoding = data[0];
        ReadOnlySpan<byte> text = data[1..];
        var values = new List<string>();

        if (encoding is 1 or 2)
        {
            // Strings end at a 00 00 pair on a 2-byte boundary; each may start with its own byte order mark.
            bool bigEndian = encoding == 2;
            int start = 0;
            for (int i = 0; i + 1 < text.Length; i += 2)
            {
                if (text[i] == 0 && text[i + 1] == 0)
                {
                    AddUtf16(values, text[start..i], bigEndian, encoding == 1);
                    start = i + 2;
                }
            }

            int remaining = (text.Length - start) & ~1;
            if (remaining > 0)
            {
                AddUtf16(values, text.Slice(start, remaining), bigEndian, encoding == 1);
            }
        }
        else
        {
            Encoding enc = encoding == 3 ? Encoding.UTF8 : encoding == 0 ? Encoding.Latin1 : Encoding.UTF8;
            foreach (var part in Split(text, 0))
            {
                values.Add(enc.GetString(part));
            }
        }

        var cleaned = values.Select(v => v.Trim().TrimEnd('\0')).Where(v => v.Length > 0).ToList();
        return cleaned.Count == 0 ? null : string.Join("; ", cleaned);
    }

    private static void AddUtf16(List<string> values, ReadOnlySpan<byte> part, bool bigEndian, bool hasBom)
    {
        if (part.Length >= 2 && hasBom)
        {
            if (part[0] == 0xFF && part[1] == 0xFE) { part = part[2..]; bigEndian = false; }
            else if (part[0] == 0xFE && part[1] == 0xFF) { part = part[2..]; bigEndian = true; }
        }

        values.Add((bigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode).GetString(part));
    }

    private static IEnumerable<byte[]> Split(ReadOnlySpan<byte> text, byte separator)
    {
        var parts = new List<byte[]>();
        int start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i == text.Length || text[i] == separator)
            {
                parts.Add(text[start..i].ToArray());
                start = i + 1;
            }
        }

        return parts;
    }

    /// <summary>ID3v1: "TAG", title 30, artist 30, album 30, year 4, comment 30, genre 1, all ISO-8859-1.</summary>
    private static AudioTags? ReadV1(Stream stream)
    {
        if (!stream.CanSeek || stream.Length < 128) return null;
        stream.Position = stream.Length - 128;
        var tag = new byte[128];
        if (!ReadExactly(stream, tag)) return null;
        if (tag[0] != 'T' || tag[1] != 'A' || tag[2] != 'G') return null;

        var result = new AudioTags
        {
            Title = V1Field(tag, 3, 30),
            Artist = V1Field(tag, 33, 30),
            Album = V1Field(tag, 63, 30),
        };
        return result.IsEmpty ? null : result;
    }

    private static string? V1Field(byte[] tag, int offset, int length)
    {
        int end = offset;
        while (end < offset + length && tag[end] != 0) end++;
        string value = Encoding.Latin1.GetString(tag, offset, end - offset).Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>
    /// ID3v2.4 frame sizes are synchsafe, but some writers stored plain 32-bit sizes. Use the synchsafe size unless
    /// a byte has its top bit set (impossible in synchsafe form).
    /// </summary>
    private static int FrameSizeV24(byte[] body, int pos)
    {
        bool synchsafe = (body[pos + 4] | body[pos + 5] | body[pos + 6] | body[pos + 7]) < 0x80;
        return synchsafe ? Synchsafe(body, pos + 4) : BigEndian(body, pos + 4);
    }

    private static bool IsFrameId(string id)
    {
        foreach (char c in id)
        {
            if (!(c is >= 'A' and <= 'Z' || c is >= '0' and <= '9')) return false;
        }

        return true;
    }

    private static int Synchsafe(byte[] b, int offset) =>
        (b[offset] & 0x7F) << 21 | (b[offset + 1] & 0x7F) << 14 | (b[offset + 2] & 0x7F) << 7 | (b[offset + 3] & 0x7F);

    private static int BigEndian(byte[] b, int offset) =>
        b[offset] << 24 | b[offset + 1] << 16 | b[offset + 2] << 8 | b[offset + 3];

    /// <summary>Reverses unsynchronisation: every 0xFF 0x00 pair becomes 0xFF.</summary>
    private static byte[] RemoveUnsync(byte[] data)
    {
        var output = new List<byte>(data.Length);
        for (int i = 0; i < data.Length; i++)
        {
            output.Add(data[i]);
            if (data[i] == 0xFF && i + 1 < data.Length && data[i + 1] == 0x00) i++;
        }

        return output.ToArray();
    }

    private static byte[] Skip(byte[] data, int count) => data.Length <= count ? [] : data[count..];

    private static bool ReadExactly(Stream stream, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer, read, buffer.Length - read);
            if (n <= 0) return false;
            read += n;
        }

        return true;
    }
}
