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
