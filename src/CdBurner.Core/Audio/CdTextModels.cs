using System.Buffers.Binary;

namespace CdBurner.Core.Audio;

/// <summary>
/// Red Book Annex J / IEC 60908 CD-Text Pack Type Indicators.
/// </summary>
public enum CdTextPackType : byte
{
    /// <summary>0x80: Disc Title / Track Titles.</summary>
    Title = 0x80,

    /// <summary>0x81: Performer / Artist name(s).</summary>
    Performer = 0x81,

    /// <summary>0x82: Songwriter name(s).</summary>
    Songwriter = 0x82,

    /// <summary>0x83: Composer name(s).</summary>
    Composer = 0x83,

    /// <summary>0x84: Arranger name(s).</summary>
    Arranger = 0x84,

    /// <summary>0x85: Message / Liner notes.</summary>
    Message = 0x85,

    /// <summary>0x86: Disc Identification / Catalog Number.</summary>
    DiscId = 0x86,

    /// <summary>0x87: Genre / Genre Code.</summary>
    Genre = 0x87,

    /// <summary>0x8E: Media Catalog Number (UPC/EAN) &amp; Track ISRC.</summary>
    UpcEanIsrc = 0x8E,

    /// <summary>0x8F: Block Size &amp; Structure Information.</summary>
    SizeInfo = 0x8F
}

/// <summary>
/// CD-Text metadata for an individual audio track.
/// </summary>
public record CdTextTrackMetadata(
    int TrackNumber,
    string Title,
    string Performer = "",
    string Songwriter = "",
    string Composer = "",
    string Arranger = "",
    string Message = "",
    string Isrc = "");

/// <summary>
/// CD-Text metadata for an album (disc-level metadata plus optional list of per-track metadata).
/// </summary>
public record CdTextAlbumMetadata(
    string AlbumTitle,
    string AlbumPerformer = "",
    string Songwriter = "",
    string Composer = "",
    string Arranger = "",
    string Message = "",
    string DiscId = "",
    string UpcEan = "",
    IReadOnlyList<CdTextTrackMetadata>? Tracks = null)
{
    public virtual bool Equals(CdTextAlbumMetadata? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return string.Equals(AlbumTitle, other.AlbumTitle, StringComparison.Ordinal)
            && string.Equals(AlbumPerformer, other.AlbumPerformer, StringComparison.Ordinal)
            && string.Equals(Songwriter, other.Songwriter, StringComparison.Ordinal)
            && string.Equals(Composer, other.Composer, StringComparison.Ordinal)
            && string.Equals(Arranger, other.Arranger, StringComparison.Ordinal)
            && string.Equals(Message, other.Message, StringComparison.Ordinal)
            && string.Equals(DiscId, other.DiscId, StringComparison.Ordinal)
            && string.Equals(UpcEan, other.UpcEan, StringComparison.Ordinal)
            && TracksEqual(Tracks, other.Tracks);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(AlbumTitle);
        hash.Add(AlbumPerformer);
        hash.Add(Songwriter);
        hash.Add(Composer);
        hash.Add(Arranger);
        hash.Add(Message);
        hash.Add(DiscId);
        hash.Add(UpcEan);
        if (Tracks != null)
        {
            foreach (var t in Tracks)
            {
                hash.Add(t);
            }
        }
        return hash.ToHashCode();
    }

    private static bool TracksEqual(IReadOnlyList<CdTextTrackMetadata>? a, IReadOnlyList<CdTextTrackMetadata>? b)
    {
        if (a is null || a.Count == 0) return b is null || b.Count == 0;
        if (b is null || b.Count == 0) return false;
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!a[i].Equals(b[i])) return false;
        }
        return true;
    }
}

/// <summary>
/// Represents an 18-byte Red Book Annex J / IEC 60908 CD-Text Pack:
/// Byte 0: Pack Type Indicator (ID1).
/// Byte 1: Track Number Indicator (ID2).
/// Byte 2: Sequence Number (ID3).
/// Byte 3: Block Number &amp; Character Position Indicator (ID4).
/// Bytes 4..15: 12-byte Text/Data payload.
/// Bytes 16..17: 16-bit Big-Endian Inverted CRC-16-CCITT checksum.
/// </summary>
public readonly struct CdTextPack
{
    public const int PackSizeBytes = 18;
    public const int PayloadSizeBytes = 12;

    private readonly byte[]? _textData;

    public CdTextPackType PackType { get; }
    public byte TrackNumberIndicator { get; }
    public byte SequenceNumber { get; }
    public byte BlockCharacterIndicator { get; }
    public byte[] TextData => _textData ?? new byte[PayloadSizeBytes];
    public ushort InvertedCrc16 { get; }

    public int CharacterPosition => BlockCharacterIndicator & 0x0F;
    public int BlockNumber => (BlockCharacterIndicator >> 4) & 0x07;
    public bool IsDbcs => (BlockCharacterIndicator & 0x80) != 0;

    public CdTextPack(
        CdTextPackType packType,
        byte trackNumberIndicator,
        byte sequenceNumber,
        byte blockCharacterIndicator,
        ReadOnlySpan<byte> textData,
        ushort invertedCrc16)
    {
        PackType = packType;
        TrackNumberIndicator = trackNumberIndicator;
        SequenceNumber = sequenceNumber;
        BlockCharacterIndicator = blockCharacterIndicator;
        _textData = new byte[PayloadSizeBytes];
        textData.Slice(0, Math.Min(PayloadSizeBytes, textData.Length)).CopyTo(_textData);
        InvertedCrc16 = invertedCrc16;
    }

    public CdTextPack(ReadOnlySpan<byte> raw18Bytes)
    {
        if (raw18Bytes.Length < PackSizeBytes)
        {
            throw new ArgumentException($"CD-Text pack must be at least {PackSizeBytes} bytes.", nameof(raw18Bytes));
        }

        PackType = (CdTextPackType)raw18Bytes[0];
        TrackNumberIndicator = raw18Bytes[1];
        SequenceNumber = raw18Bytes[2];
        BlockCharacterIndicator = raw18Bytes[3];
        _textData = raw18Bytes.Slice(4, PayloadSizeBytes).ToArray();
        InvertedCrc16 = BinaryPrimitives.ReadUInt16BigEndian(raw18Bytes.Slice(16, 2));
    }

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < PackSizeBytes)
        {
            throw new ArgumentException($"Destination span must be at least {PackSizeBytes} bytes.", nameof(destination));
        }

        destination[0] = (byte)PackType;
        destination[1] = TrackNumberIndicator;
        destination[2] = SequenceNumber;
        destination[3] = BlockCharacterIndicator;
        (_textData ?? new byte[PayloadSizeBytes]).CopyTo(destination.Slice(4, PayloadSizeBytes));
        BinaryPrimitives.WriteUInt16BigEndian(destination.Slice(16, 2), InvertedCrc16);
    }

    public byte[] ToByteArray()
    {
        byte[] bytes = new byte[PackSizeBytes];
        WriteTo(bytes);
        return bytes;
    }

    public override string ToString() =>
        $"CdTextPack[{PackType} Trk={TrackNumberIndicator} Seq={SequenceNumber} CharPos={CharacterPosition} Crc=0x{InvertedCrc16:X4}]";
}
