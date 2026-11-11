using System.Buffers.Binary;

namespace CdBurner.Hardware.NativeWriting;

/// <summary>
/// The Write Parameters mode page (0x05, MMC-5 7.5.4) as returned by MODE SENSE (10), and the MODE SELECT (10)
/// parameter list that switches it to Session-at-Once.
/// </summary>
public sealed class WriteParametersPage
{
    public const byte PageCode = 0x05;
    public const byte WriteTypeSessionAtOnce = 0x02;
    public const byte DataBlockTypeRaw = 0x00;          // 2,352 bytes
    public const byte DataBlockTypeRawWithRawPw = 0x03; // 2,448 bytes; MMC-3 asks for it when a CD-Text lead-in is written

    private const int ModeHeaderLength = 8;

    private readonly byte[] _page;

    /// <summary>The page bytes, starting with the page code byte.</summary>
    public ReadOnlySpan<byte> Bytes => _page;

    public byte WriteType => (byte)(_page[2] & 0x0F);
    public bool TestWrite => (_page[2] & 0x10) != 0;
    public bool BufferUnderrunFree => (_page[2] & 0x40) != 0;
    public byte MultiSession => (byte)(_page[3] >> 6);
    public byte DataBlockType => (byte)(_page[4] & 0x0F);

    private WriteParametersPage(byte[] page) => _page = page;

    /// <summary>Reads the page out of a MODE SENSE (10) response, or returns null if it is not there.</summary>
    public static WriteParametersPage? FromModeSense10(ReadOnlySpan<byte> response)
    {
        if (response.Length < ModeHeaderLength + 2)
        {
            return null;
        }

        int modeDataLength = BinaryPrimitives.ReadUInt16BigEndian(response) + 2;
        int blockDescriptorLength = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(6, 2));
        int offset = ModeHeaderLength + blockDescriptorLength;
        int available = Math.Min(response.Length, modeDataLength);
        if (offset + 2 > available || (response[offset] & 0x3F) != PageCode)
        {
            return null;
        }

        int pageLength = response[offset + 1] + 2;
        if (pageLength < 16 || offset + pageLength > available)
        {
            return null;
        }

        return new WriteParametersPage(response.Slice(offset, pageLength).ToArray());
    }

    /// <summary>
    /// A copy of this page set up for a Session-at-Once audio disc that is closed afterwards. Everything else the
    /// drive reported (link size, packet size, MCN/ISRC fields, …) is kept as it was.
    /// </summary>
    public WriteParametersPage ForSessionAtOnce(bool testWrite, bool bufferUnderrunFree, bool cdTextLeadIn)
    {
        byte[] p = (byte[])_page.Clone();
        p[0] &= 0x3F;                                   // clear PS (and the reserved bit) for MODE SELECT
        p[2] = (byte)((p[2] & 0xA0) | WriteTypeSessionAtOnce); // keep LS_V and the reserved bit 7
        if (testWrite) p[2] |= 0x10;
        if (bufferUnderrunFree) p[2] |= 0x40;
        p[3] &= 0x3F;                                   // multi-session 00: no next session, disc closed
        p[4] = (byte)((p[4] & 0xF0) | (cdTextLeadIn ? DataBlockTypeRawWithRawPw : DataBlockTypeRaw));
        p[8] = 0x00;                                    // session format: CD-DA / CD-ROM
        return new WriteParametersPage(p);
    }

    public const byte WriteTypeTrackAtOnce = 0x01;
    public const byte DataBlockTypeMode1 = 0x08;        // 2,048 bytes of user data; the drive adds sync, header and ECC
    public const byte TrackModeData = 0x04;             // data track, recorded uninterrupted

    public byte TrackMode => (byte)(_page[3] & 0x0F);

    /// <summary>
    /// A copy of this page set up for one Mode 1 data track written Track-at-Once. <paramref name="closeDisc"/> false
    /// leaves room for another session (multi-session 11b); true closes the disc.
    /// </summary>
    public WriteParametersPage ForTrackAtOnceData(bool testWrite, bool bufferUnderrunFree, bool closeDisc)
    {
        byte[] p = (byte[])_page.Clone();
        p[0] &= 0x3F;
        p[2] = (byte)((p[2] & 0xA0) | WriteTypeTrackAtOnce);
        if (testWrite) p[2] |= 0x10;
        if (bufferUnderrunFree) p[2] |= 0x40;
        p[3] = (byte)((closeDisc ? 0x00 : 0xC0) | TrackModeData); // FP and Copy off: not packet writing
        p[4] = (byte)((p[4] & 0xF0) | DataBlockTypeMode1);
        p[8] = 0x00;                                    // session format: CD-ROM
        p[10] = p[11] = p[12] = p[13] = 0;              // packet size: unused
        return new WriteParametersPage(p);
    }

    public const byte WriteTypeIncremental = 0x00;
    public const byte TrackModeDvd = 0x05;              // libburn cookbook: track mode 5 for DVD-R/-RW
    public const int DvdPacketBlocks = 16;              // one 32 KiB ECC block

    public bool FixedPackets => (_page[3] & 0x20) != 0;
    public int PacketSize => BinaryPrimitives.ReadInt32BigEndian(_page.AsSpan(10, 4));

    /// <summary>
    /// A copy set up for a DVD-R/-RW written Disc-At-Once (write type 2): LS_V 0, link size 0, multi-session 00 (the
    /// drive closes the disc), FP 0, track mode 5, data block type 8, packet size 0. Values from libburn's cookbook.
    /// </summary>
    public WriteParametersPage ForDvdDiscAtOnce(bool testWrite, bool bufferUnderrunFree)
    {
        byte[] p = (byte[])_page.Clone();
        p[0] &= 0x3F;
        p[2] = (byte)((p[2] & 0x80) | WriteTypeSessionAtOnce); // LS_V off
        if (testWrite) p[2] |= 0x10;
        if (bufferUnderrunFree) p[2] |= 0x40;
        p[3] = TrackModeDvd;                            // multi-session 00, FP 0, copy 0
        p[4] = (byte)((p[4] & 0xF0) | DataBlockTypeMode1);
        p[5] = 0;                                       // link size
        p[8] = 0x00;
        p[10] = p[11] = p[12] = p[13] = 0;
        return new WriteParametersPage(p);
    }

    /// <summary>
    /// A copy set up for a DVD-R/-RW written incrementally (write type 0) in fixed packets of 16 blocks: LS_V 1 with
    /// link size 16, FP 1, packet size 16, data block type 8. <paramref name="closeDisc"/> false keeps the disc
    /// appendable (multi-session 11b). <paramref name="trackMode"/> comes from READ TRACK INFORMATION, normally 5.
    /// </summary>
    public WriteParametersPage ForDvdIncremental(bool testWrite, bool bufferUnderrunFree, bool closeDisc, byte trackMode)
    {
        byte[] p = (byte[])_page.Clone();
        p[0] &= 0x3F;
        p[2] = (byte)((p[2] & 0x80) | 0x20 | WriteTypeIncremental); // LS_V on
        if (testWrite) p[2] |= 0x10;
        if (bufferUnderrunFree) p[2] |= 0x40;
        p[3] = (byte)((closeDisc ? 0x00 : 0xC0) | 0x20 | (trackMode & 0x0F)); // FP on
        p[4] = (byte)((p[4] & 0xF0) | DataBlockTypeMode1);
        p[5] = DvdPacketBlocks;                         // link size
        p[8] = 0x00;
        BinaryPrimitives.WriteInt32BigEndian(p.AsSpan(10, 4), DvdPacketBlocks);
        return new WriteParametersPage(p);
    }

    /// <summary>The same settings with data block type 0, for drives that reject block type 3 (cdrdao's fallback).</summary>
    public WriteParametersPage WithRawDataBlockType()
    {
        byte[] p = (byte[])_page.Clone();
        p[4] = (byte)((p[4] & 0xF0) | DataBlockTypeRaw);
        return new WriteParametersPage(p);
    }

    /// <summary>MODE SELECT (10) parameter list: an 8-byte header (lengths zero, no block descriptor) plus the page.</summary>
    public byte[] ToModeSelect10ParameterList()
    {
        byte[] list = new byte[ModeHeaderLength + _page.Length];
        _page.CopyTo(list, ModeHeaderLength);
        return list;
    }

    /// <summary>Builds a page from raw bytes (for the virtual drive and tests).</summary>
    public static WriteParametersPage FromPageBytes(ReadOnlySpan<byte> page)
    {
        if (page.Length < 16 || (page[0] & 0x3F) != PageCode)
        {
            throw new ArgumentException("Not a write parameters page.", nameof(page));
        }

        return new WriteParametersPage(page.ToArray());
    }
}
