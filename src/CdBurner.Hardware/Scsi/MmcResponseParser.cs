using System.Buffers.Binary;
using System.Text;
using CdBurner.Core.Primitives;

namespace CdBurner.Hardware.Scsi;

/// <summary>
/// Parsed standard SCSI INQUIRY response data.
/// </summary>
public sealed record MmcInquiryResponse(
    byte PeripheralDeviceType,
    bool IsRemovable,
    string VendorId,
    string ProductId,
    string ProductRevision,
    bool IsCdDvdDevice)
{
    public override string ToString() => $"{VendorId} {ProductId} (Rev {ProductRevision})";
}

/// <summary>
/// Descriptor representing a single track or lead-out entry within a READ TOC Format 0x00 response.
/// </summary>
public sealed record TocTrackDescriptor(byte TrackNumber, byte AdrControl, int StartLba)
{
    /// <summary>
    /// ADR nibble (bits 4..7) indicating the type of subchannel Q data.
    /// </summary>
    public byte Adr => (byte)((AdrControl >> 4) & 0x0F);

    /// <summary>
    /// Control nibble (bits 0..3) specifying track attributes (audio vs. data, pre-emphasis, copy permit).
    /// </summary>
    public byte Control => (byte)(AdrControl & 0x0F);

    /// <summary>
    /// True if bit 2 of Control is set (Data track: Mode 1, Mode 2, or DVD data).
    /// </summary>
    public bool IsDataTrack => (Control & 0x04) != 0;

    /// <summary>
    /// True if bit 2 of Control is 0 (Red Book CD-DA audio track).
    /// </summary>
    public bool IsAudioTrack => !IsDataTrack;

    /// <summary>
    /// True if this descriptor represents the Lead-Out track (TrackNumber == 0xAA).
    /// </summary>
    public bool IsLeadOut => TrackNumber == 0xAA;

    public override string ToString() =>
        $"Track {(IsLeadOut ? "AA (Lead-Out)" : TrackNumber.ToString("D2"))}: Start LBA={StartLba}, Control=0x{Control:X1} ({(IsDataTrack ? "Data" : "Audio")})";
}

/// <summary>
/// Full TOC response including track range and track descriptors.
/// </summary>
public sealed record TocResponse(
    byte FirstTrack,
    byte LastTrack,
    IReadOnlyList<TocTrackDescriptor> Tracks);

/// <summary>
/// Parsers for SCSI / MMC-5 command responses including INQUIRY and READ TOC.
/// </summary>
public static class MmcResponseParser
{
    /// <summary>
    /// Standard SCSI Peripheral Device Type code for CD/DVD optical devices (0x05).
    /// </summary>
    public const byte DeviceTypeCdDvd = 0x05;

    /// <summary>
    /// Parses standard SCSI INQUIRY response bytes.
    /// </summary>
    /// <param name="buffer">Byte buffer returned by INQUIRY command (minimum 36 bytes for full identification).</param>
    /// <returns>Structured <see cref="MmcInquiryResponse"/> containing vendor, product, and revision strings.</returns>
    public static MmcInquiryResponse ParseInquiry(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 36)
        {
            throw new ArgumentException("INQUIRY response buffer must be at least 36 bytes.", nameof(buffer));
        }

        byte devType = (byte)(buffer[0] & 0x1F);
        bool isRemovable = (buffer[1] & 0x80) != 0;

        string vendor = Encoding.ASCII.GetString(buffer.Slice(8, 8)).Trim(' ', '\0');
        string product = Encoding.ASCII.GetString(buffer.Slice(16, 16)).Trim(' ', '\0');
        string revision = Encoding.ASCII.GetString(buffer.Slice(32, 4)).Trim(' ', '\0');

        bool isCdDvd = devType == DeviceTypeCdDvd;

        return new MmcInquiryResponse(devType, isRemovable, vendor, product, revision, isCdDvd);
    }

    /// <summary>
    /// Parses READ TOC Format 0x00 response bytes into a list of <see cref="TocTrackDescriptor"/>.
    /// </summary>
    /// <param name="buffer">Byte buffer returned by READ TOC Format 0x00 command.</param>
    /// <param name="isMsf">True if addresses in buffer are encoded as MSF (MM:SS:FF) timecodes; false if 32-bit big-endian LBA.</param>
    /// <returns>List of track descriptors including regular tracks and Lead-Out (0xAA).</returns>
    public static IReadOnlyList<TocTrackDescriptor> ParseToc(ReadOnlySpan<byte> buffer, bool isMsf = false)
    {
        if (buffer.Length < 4)
        {
            throw new ArgumentException("TOC buffer must be at least 4 bytes (header length).", nameof(buffer));
        }

        ushort tocDataLength = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(0, 2));

        var tracks = new List<TocTrackDescriptor>();

        // If tocDataLength is populated (> 0), data ends at Math.Min(buffer.Length, tocDataLength + 2).
        // Otherwise, parse up to buffer.Length.
        int availableData = tocDataLength > 0 ? Math.Min(buffer.Length, tocDataLength + 2) : buffer.Length;

        // Descriptors begin at offset 4 and are 8 bytes each
        for (int offset = 4; offset + 8 <= availableData; offset += 8)
        {
            byte adrControl = buffer[offset + 1];
            byte trackNumber = buffer[offset + 2];

            int startLba;
            if (isMsf)
            {
                byte m = buffer[offset + 5];
                byte s = buffer[offset + 6];
                byte f = buffer[offset + 7];
                startLba = new MsfAddress(m, s, f).ToLba();
            }
            else
            {
                startLba = BinaryPrimitives.ReadInt32BigEndian(buffer.Slice(offset + 4, 4));
            }

            tracks.Add(new TocTrackDescriptor(trackNumber, adrControl, startLba));
        }

        return tracks;
    }

    /// <summary>
    /// Parses full READ TOC Format 0x00 response into <see cref="TocResponse"/> containing first/last track numbers and descriptors.
    /// </summary>
    public static TocResponse ParseTocResponse(ReadOnlySpan<byte> buffer, bool isMsf = false)
    {
        if (buffer.Length < 4)
        {
            throw new ArgumentException("TOC buffer must be at least 4 bytes.", nameof(buffer));
        }

        byte firstTrack = buffer[2];
        byte lastTrack = buffer[3];
        var tracks = ParseToc(buffer, isMsf);

        return new TocResponse(firstTrack, lastTrack, tracks);
    }
}
