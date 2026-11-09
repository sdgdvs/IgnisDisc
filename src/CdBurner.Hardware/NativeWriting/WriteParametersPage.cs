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
