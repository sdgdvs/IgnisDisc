using System.Buffers.Binary;

namespace CdBurner.Hardware.NativeWriting;

/// <summary>
/// CDB builders for the MMC commands that change what is on a disc or how the drive will write it. They live here,
/// apart from <see cref="Scsi.MmcCommandBuilder"/>, so that every caller of a write command is inside the opt-in
/// native writer.
/// </summary>
public static class NativeWriteCommands
{
    public const byte OpCodeWrite10 = 0x2A;
    public const byte OpCodeModeSelect10 = 0x55;
    public const byte OpCodeReadBufferCapacity = 0x5C;
    public const byte OpCodeSendCueSheet = 0x5D;
    public const byte OpCodeCloseTrackSession = 0x5B;
    public const byte OpCodeSetCdSpeed = 0xBB;
    public const byte OpCodeReserveTrack = 0x53;
    public const byte OpCodeFormatUnit = 0x04;

    /// <summary>FORMAT UNIT format type 26h: DVD+RW full format, run by the drive in the background.</summary>
    public const byte FormatTypeDvdPlusRw = 0x26;

    /// <summary>FORMAT UNIT format type 13h: expand a partly formatted DVD-RW (restricted overwrite).</summary>
    public const byte FormatTypeDvdMinusRwExpand = 0x13;

    /// <summary>
    /// FORMAT UNIT parameter list that expands a partly formatted DVD-RW: FOV | Immed, descriptor length 8, number of
    /// blocks 0 (libburn's cookbook: writing can then begin soon, the disc in the intermediate state), type 13h &lt;&lt;
    /// 2 in byte 8, and 16 in byte 11 (the packet size libburn sets for format types 10h–15h).
    /// </summary>
    public static byte[] BuildDvdMinusRwExpandParameters()
    {
        byte[] list = new byte[12];
        list[1] = 0x82;
        list[3] = 8;
        list[8] = FormatTypeDvdMinusRwExpand << 2;
        list[11] = 16;
        return list;
    }

    /// <summary>FORMAT UNIT (0x04) with FmtData = 1 and format code 001b: a parameter list of 12 bytes follows.</summary>
    public static byte[] BuildFormatUnit()
    {
        byte[] cdb = new byte[6];
        cdb[0] = OpCodeFormatUnit;
        cdb[1] = 0x11;
        return cdb;
    }

    /// <summary>
    /// FORMAT UNIT parameter list for a DVD+RW (layout from libburn's mmc_format_unit): header byte 1 = FOV | Immed
    /// (0x82), byte 3 = descriptor length 8; descriptor bytes 4–7 = number of blocks 0xFFFFFFFF (the most the disc
    /// takes, which type 26h allows), byte 8 = type 26h &lt;&lt; 2, byte 11 = the restart bit for a partly formatted
    /// disc.
    /// </summary>
    public static byte[] BuildDvdPlusRwFormatParameters(bool restart)
    {
        byte[] list = new byte[12];
        list[1] = 0x82;
        list[3] = 8;
        BinaryPrimitives.WriteUInt32BigEndian(list.AsSpan(4, 4), 0xFFFFFFFF);
        list[8] = FormatTypeDvdPlusRw << 2;
        list[11] = (byte)(restart ? 1 : 0);
        return list;
    }
    public const byte OpCodeSetStreaming = 0xB6;

    /// <summary>Length of one SET STREAMING performance descriptor (type 0).</summary>
    public const int PerformanceDescriptorLength = 28;

    /// <summary>CLOSE TRACK/SESSION close function 001b: close the track (on DVD±R, the written fragment).</summary>
    public const byte CloseFunctionTrack = 0x01;

    /// <summary>CLOSE TRACK/SESSION close function 010b: close the session (writes its lead-in and lead-out).</summary>
    public const byte CloseFunctionSession = 0x02;

    /// <summary>CLOSE TRACK/SESSION close function 101b on DVD+R: close the session and finalize the disc with the
    /// minimal radius, so nothing more can be added (libburn's cookbook; some drives refuse it).</summary>
    public const byte CloseFunctionFinalizeDvdPlusR = 0x05;

    /// <summary>
    /// RESERVE TRACK (0x53) with ARSV = 0 and RMZ = 0: reserves <paramref name="sectors"/> 2,048-byte blocks for the
    /// next track. DVD-R Disc-At-Once needs it; the size goes in CDB bytes 5–8.
    /// </summary>
    public static byte[] BuildReserveTrack(int sectors)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sectors);
        byte[] cdb = new byte[10];
        cdb[0] = OpCodeReserveTrack;
        BinaryPrimitives.WriteInt32BigEndian(cdb.AsSpan(5, 4), sectors);
        return cdb;
    }

    /// <summary>SET STREAMING (0xB6) with type 0 (one performance descriptor); the list length goes in bytes 9–10.</summary>
    public static byte[] BuildSetStreaming()
    {
        byte[] cdb = new byte[12];
        cdb[0] = OpCodeSetStreaming;
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(9, 2), PerformanceDescriptorLength);
        return cdb;
    }

    /// <summary>
    /// The 28-byte performance descriptor for SET STREAMING: start LBA 0 (bytes 4–7), <paramref name="endLba"/>
    /// (8–11), then read size and read time (12–15, 16–19) and write size and write time (20–23, 24–27). Sizes in kB
    /// over 1,000 ms give the speed in kB/s; read and write both get <paramref name="kiloBytesPerSecond"/>, a speed
    /// from the drive's own GET PERFORMANCE list.
    /// </summary>
    public static byte[] BuildPerformanceDescriptor(int endLba, int kiloBytesPerSecond)
    {
        byte[] d = new byte[PerformanceDescriptorLength];
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(8, 4), Math.Max(0, endLba));
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(12, 4), kiloBytesPerSecond);
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(16, 4), 1000);
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(20, 4), kiloBytesPerSecond);
        BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(24, 4), 1000);
        return d;
    }

    /// <summary>CLOSE TRACK/SESSION. With <paramref name="immediate"/> the drive answers at once and reports "not ready"
    /// until it has finished.</summary>
    public static byte[] BuildCloseTrackSession(byte closeFunction, ushort trackNumber = 0, bool immediate = true)
    {
        byte[] cdb = new byte[10];
        cdb[0] = OpCodeCloseTrackSession;
        cdb[1] = (byte)(immediate ? 0x01 : 0x00);
        cdb[2] = (byte)(closeFunction & 0x07);
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(4, 2), trackNumber);
        return cdb;
    }

    /// <summary>SET CD SPEED (0xBB): read speed "fastest", write speed in kB/s (1,000 bytes; 176.4 per 1x).</summary>
    public static byte[] BuildSetCdSpeed(int writeKiloBytesPerSecond)
    {
        byte[] cdb = new byte[12];
        cdb[0] = OpCodeSetCdSpeed;
        cdb[2] = 0xFF; cdb[3] = 0xFF;
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(4, 2), (ushort)Math.Clamp(writeKiloBytesPerSecond, 1, 0xFFFF));
        return cdb;
    }

    /// <summary>READ TRACK INFORMATION for a track number (address type 01b); 0xFF asks for the invisible track.</summary>
    public static byte[] BuildReadTrackInformation(int trackNumber, ushort allocationLength = 36)
    {
        byte[] cdb = new byte[10];
        cdb[0] = Scsi.MmcCommandBuilder.OpCodeReadTrackInformation;
        cdb[1] = 0x01;
        BinaryPrimitives.WriteInt32BigEndian(cdb.AsSpan(2, 4), trackNumber);
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(7, 2), allocationLength);
        return cdb;
    }

    /// <summary>WRITE (10): <paramref name="blockCount"/> blocks starting at <paramref name="lba"/> (negative in the lead-in).</summary>
    public static byte[] BuildWrite10(int lba, ushort blockCount)
    {
        byte[] cdb = new byte[10];
        cdb[0] = OpCodeWrite10;
        BinaryPrimitives.WriteInt32BigEndian(cdb.AsSpan(2, 4), lba);
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(7, 2), blockCount);
        return cdb;
    }

    /// <summary>MODE SELECT (10) with PF = 1 (the parameter list uses the MMC page format).</summary>
    public static byte[] BuildModeSelect10(ushort parameterListLength)
    {
        byte[] cdb = new byte[10];
        cdb[0] = OpCodeModeSelect10;
        cdb[1] = 0x10; // PF
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(7, 2), parameterListLength);
        return cdb;
    }

    /// <summary>READ BUFFER CAPACITY in byte units (BLOCK = 0); the response is 12 bytes.</summary>
    public static byte[] BuildReadBufferCapacity()
    {
        byte[] cdb = new byte[10];
        cdb[0] = OpCodeReadBufferCapacity;
        BinaryPrimitives.WriteUInt16BigEndian(cdb.AsSpan(7, 2), 12);
        return cdb;
    }

    /// <summary>SEND CUE SHEET with a 24-bit cue sheet length.</summary>
    public static byte[] BuildSendCueSheet(int cueSheetLength)
    {
        if (cueSheetLength <= 0 || cueSheetLength > 0xFFFFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(cueSheetLength));
        }

        byte[] cdb = new byte[10];
        cdb[0] = OpCodeSendCueSheet;
        cdb[6] = (byte)(cueSheetLength >> 16);
        cdb[7] = (byte)(cueSheetLength >> 8);
        cdb[8] = (byte)cueSheetLength;
        return cdb;
    }

    /// <summary>Parses READ BUFFER CAPACITY data: (buffer length, blank length) in bytes, or null if too short.</summary>
    public static (uint Length, uint Blank)? ParseBufferCapacity(ReadOnlySpan<byte> response)
    {
        if (response.Length < 12)
        {
            return null;
        }

        return (BinaryPrimitives.ReadUInt32BigEndian(response.Slice(4, 4)),
                BinaryPrimitives.ReadUInt32BigEndian(response.Slice(8, 4)));
    }
}

/// <summary>
/// CD addresses. An absolute MSF time counts frames from the start of the program area's pre-gap (00:00:00 is
/// LBA −150). Lead-in times (90:00:00 and later) stand for negative LBAs.
/// </summary>
public readonly record struct Msf(byte Minute, byte Second, byte Frame)
{
    public const int FramesPerSecond = 75;

    /// <summary>Converts an LBA to absolute MSF. LBA 0 is 00:02:00; lead-in LBAs map to 9x:xx:xx.</summary>
    public static Msf FromLba(int lba)
    {
        int frames = lba >= -150 ? lba + 150 : lba + 450_150;
        if (frames < 0 || frames >= 100 * 60 * FramesPerSecond)
        {
            throw new ArgumentOutOfRangeException(nameof(lba), lba, "Address outside the CD MSF range.");
        }

        return new Msf((byte)(frames / (60 * FramesPerSecond)), (byte)(frames / FramesPerSecond % 60), (byte)(frames % FramesPerSecond));
    }

    /// <summary>Converts back to an LBA, treating 90:00:00 and later as lead-in (negative) addresses.</summary>
    public int ToLba()
    {
        int frames = (Minute * 60 + Second) * FramesPerSecond + Frame;
        return Minute >= 90 ? frames - 450_150 : frames - 150;
    }

    public override string ToString() => $"{Minute:D2}:{Second:D2}:{Frame:D2}";
}
