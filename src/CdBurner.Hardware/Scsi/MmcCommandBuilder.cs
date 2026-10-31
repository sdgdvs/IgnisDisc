namespace CdBurner.Hardware.Scsi;

/// <summary>
/// Pure, byte-exact SCSI / MMC-5 Command Descriptor Block (CDB) builders with big-endian multi-byte fields.
/// </summary>
public static class MmcCommandBuilder
{
    // MMC-5 Operation Codes
    public const byte OpCodeTestUnitReady        = 0x00;
    public const byte OpCodeInquiry              = 0x12;
    public const byte OpCodeStartStopUnit        = 0x1B;
    public const byte OpCodeSynchronizeCache10   = 0x35;
    public const byte OpCodeReadTocPmaAtip       = 0x43;
    public const byte OpCodeGetConfiguration     = 0x46;
    public const byte OpCodeReadDiscInformation  = 0x51;
    public const byte OpCodeModeSense10          = 0x5A;
    public const byte OpCodeSendCueSheet         = 0x5D;
    public const byte OpCodeBlank                = 0xA1;
    public const byte OpCodeReadDiscStructure    = 0xAD;
    public const byte OpCodeReadCd               = 0xBE;
    public const byte OpCodeSendDiscStructure    = 0xBF;

    // READ TOC Formats
    public const byte TocFormatFormattedToc      = 0x00;
    public const byte TocFormatMultiSession      = 0x01;
    public const byte TocFormatRawToc            = 0x02;
    public const byte TocFormatPma               = 0x03;
    public const byte TocFormatAtip              = 0x04;
    public const byte TocFormatCdText            = 0x05;

    // Blanking Types
    public const byte BlankTypeFull              = 0x00;
    public const byte BlankTypeMinimal           = 0x01;
    public const byte BlankTypeTrack             = 0x02;
    public const byte BlankTypeUnreserveTrack    = 0x03;
    public const byte BlankTypeTrackTail         = 0x04;
    public const byte BlankTypeUncloseLastSession= 0x05;
    public const byte BlankTypeSession           = 0x06;

    /// <summary>
    /// Builds a standard 6-byte SCSI INQUIRY CDB (OpCode 0x12).
    /// </summary>
    /// <param name="allocationLength">Number of bytes allocated for response (default 96).</param>
    public static byte[] BuildInquiryCdb(ushort allocationLength = 96)
    {
        byte[] cdb = new byte[6];
        cdb[0] = OpCodeInquiry;
        cdb[1] = 0x00; // EVPD = 0
        cdb[2] = 0x00; // Page Code = 0
        cdb[3] = (byte)(allocationLength >> 8);
        cdb[4] = (byte)(allocationLength & 0xFF);
        cdb[5] = 0x00; // Control
        return cdb;
    }

    /// <summary>
    /// Builds a 10-byte MMC GET CONFIGURATION CDB (OpCode 0x46) to query supported profiles and features.
    /// </summary>
    /// <param name="startingFeature">Starting feature number (big-endian).</param>
    /// <param name="allocationLength">Buffer allocation length (default 4096).</param>
    public static byte[] BuildGetConfigurationCdb(ushort startingFeature = 0, ushort allocationLength = 4096)
    {
        byte[] cdb = new byte[10];
        cdb[0] = OpCodeGetConfiguration;
        cdb[1] = 0x00; // RT = 0 (all features starting from startingFeature)
        cdb[2] = (byte)(startingFeature >> 8);
        cdb[3] = (byte)(startingFeature & 0xFF);
        cdb[4] = 0x00;
        cdb[5] = 0x00;
        cdb[6] = 0x00;
        cdb[7] = (byte)(allocationLength >> 8);
        cdb[8] = (byte)(allocationLength & 0xFF);
        cdb[9] = 0x00; // Control
        return cdb;
    }

    /// <summary>
    /// Builds a 10-byte MMC READ DISC INFORMATION CDB (OpCode 0x51).
    /// </summary>
    /// <param name="allocationLength">Buffer allocation length (default 64).</param>
    public static byte[] BuildReadDiscInformationCdb(ushort allocationLength = 64)
    {
        byte[] cdb = new byte[10];
        cdb[0] = OpCodeReadDiscInformation;
        cdb[1] = 0x00;
        cdb[2] = 0x00;
        cdb[3] = 0x00;
        cdb[4] = 0x00;
        cdb[5] = 0x00;
        cdb[6] = 0x00;
        cdb[7] = (byte)(allocationLength >> 8);
        cdb[8] = (byte)(allocationLength & 0xFF);
        cdb[9] = 0x00; // Control
        return cdb;
    }

    /// <summary>
    /// Builds a 10-byte MMC READ TOC/PMA/ATIP CDB (OpCode 0x43).
    /// </summary>
    /// <param name="format">Format field: 0x00 (Formatted TOC), 0x01 (Multi-session), 0x02 (Raw TOC), 0x04 (ATIP), 0x05 (CD-Text).</param>
    /// <param name="trackSessionNumber">Starting track or session number.</param>
    /// <param name="allocationLength">Buffer allocation length (big-endian).</param>
    /// <param name="msf">Whether track addresses should be returned in MSF timecode format (true) or LBA format (false).</param>
    public static byte[] BuildReadTocPmaAtipCdb(byte format, byte trackSessionNumber, ushort allocationLength, bool msf = false)
    {
        byte[] cdb = new byte[10];
        cdb[0] = OpCodeReadTocPmaAtip;
        cdb[1] = (byte)(msf ? 0x02 : 0x00); // Bit 1 = MSF
        cdb[2] = (byte)(format & 0x0F);     // Bits 0..3 = Format
        cdb[3] = 0x00;
        cdb[4] = 0x00;
        cdb[5] = 0x00;
        cdb[6] = trackSessionNumber;
        cdb[7] = (byte)(allocationLength >> 8);
        cdb[8] = (byte)(allocationLength & 0xFF);
        cdb[9] = 0x00; // Control
        return cdb;
    }

    /// <summary>
    /// Builds a 12-byte MMC READ CD CDB (OpCode 0xBE) for extracting raw sectors, headers, and subchannel data.
    /// </summary>
    /// <param name="startLba">Starting Logical Block Address (32-bit big-endian).</param>
    /// <param name="transferBlocks">Number of blocks to read (24-bit big-endian).</param>
    /// <param name="expectedSectorType">Expected Sector Type in bits 2..4 (0 = any, 1 = CD-DA, 2 = Mode 1, etc.).</param>
    /// <param name="mainChannelSelectionBits">Main channel flags (0xF8 = Sync + Header + SubHeader + User Data + EDC/ECC).</param>
    /// <param name="subChannelSelection">Sub-channel selection bits in bits 0..2.</param>
    public static byte[] BuildReadCdCdb(
        int startLba,
        int transferBlocks,
        byte expectedSectorType = 0,
        byte mainChannelSelectionBits = 0xF8,
        byte subChannelSelection = 0)
    {
        if (transferBlocks is < 0 or > 0xFFFFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(transferBlocks), "Transfer blocks must fit in a 24-bit unsigned integer (0..0xFFFFFF).");
        }

        byte[] cdb = new byte[12];
        cdb[0] = OpCodeReadCd;
        cdb[1] = (byte)((expectedSectorType & 0x07) << 2);
        cdb[2] = (byte)((startLba >> 24) & 0xFF);
        cdb[3] = (byte)((startLba >> 16) & 0xFF);
        cdb[4] = (byte)((startLba >> 8) & 0xFF);
        cdb[5] = (byte)(startLba & 0xFF);
        cdb[6] = (byte)((transferBlocks >> 16) & 0xFF);
        cdb[7] = (byte)((transferBlocks >> 8) & 0xFF);
        cdb[8] = (byte)(transferBlocks & 0xFF);
        cdb[9] = mainChannelSelectionBits;
        cdb[10] = (byte)(subChannelSelection & 0x07);
        cdb[11] = 0x00; // Control
        return cdb;
    }

    /// <summary>
    /// Builds a 10-byte MMC SYNCHRONIZE CACHE (10) CDB (OpCode 0x35) to flush drive write buffers to media.
    /// </summary>
    /// <param name="lba">Starting Logical Block Address.</param>
    /// <param name="blockCount">Number of logical blocks.</param>
    /// <param name="immediate">Return immediately while flush runs in background.</param>
    public static byte[] BuildSynchronizeCacheCdb(int lba = 0, ushort blockCount = 0, bool immediate = false)
    {
        byte[] cdb = new byte[10];
        cdb[0] = OpCodeSynchronizeCache10;
        cdb[1] = (byte)(immediate ? 0x02 : 0x00); // Bit 1 = Immed
        cdb[2] = (byte)((lba >> 24) & 0xFF);
        cdb[3] = (byte)((lba >> 16) & 0xFF);
        cdb[4] = (byte)((lba >> 8) & 0xFF);
        cdb[5] = (byte)(lba & 0xFF);
        cdb[6] = 0x00;
        cdb[7] = (byte)(blockCount >> 8);
        cdb[8] = (byte)(blockCount & 0xFF);
        cdb[9] = 0x00; // Control
        return cdb;
    }

    /// <summary>
    /// Builds a 12-byte MMC BLANK CDB (OpCode 0xA1) for blanking/erasing CD-RW / DVD-RW media.
    /// </summary>
    /// <param name="blankingType">Blanking type (0 = Full, 1 = Minimal, etc.).</param>
    /// <param name="startAddress">Start LBA or track number.</param>
    /// <param name="immediate">Return immediately while blanking runs in background.</param>
    public static byte[] BuildBlankCdb(byte blankingType, int startAddress = 0, bool immediate = false)
    {
        byte[] cdb = new byte[12];
        cdb[0] = OpCodeBlank;
        cdb[1] = (byte)((blankingType & 0x07) | (immediate ? 0x10 : 0x00));
        cdb[2] = (byte)((startAddress >> 24) & 0xFF);
        cdb[3] = (byte)((startAddress >> 16) & 0xFF);
        cdb[4] = (byte)((startAddress >> 8) & 0xFF);
        cdb[5] = (byte)(startAddress & 0xFF);
        cdb[6] = 0x00;
        cdb[7] = 0x00;
        cdb[8] = 0x00;
        cdb[9] = 0x00;
        cdb[10] = 0x00;
        cdb[11] = 0x00; // Control
        return cdb;
    }

    /// <summary>
    /// Builds a 12-byte MMC SEND DISC STRUCTURE CDB (OpCode 0xBF) (e.g. for DVD+R Book Type bitsetting).
    /// </summary>
    /// <param name="formatCode">Structure format code.</param>
    /// <param name="structureDataLength">Length of structure data parameter list (big-endian).</param>
    public static byte[] BuildSendDiscStructureCdb(byte formatCode, ushort structureDataLength)
    {
        byte[] cdb = new byte[12];
        cdb[0] = OpCodeSendDiscStructure;
        cdb[1] = 0x00;
        cdb[2] = 0x00;
        cdb[3] = 0x00;
        cdb[4] = 0x00;
        cdb[5] = 0x00;
        cdb[6] = 0x00;
        cdb[7] = formatCode;
        cdb[8] = (byte)(structureDataLength >> 8);
        cdb[9] = (byte)(structureDataLength & 0xFF);
        cdb[10] = 0x00;
        cdb[11] = 0x00; // Control
        return cdb;
    }

    /// <summary>
    /// Builds a 12-byte MMC READ DISC STRUCTURE CDB (OpCode 0xAD).
    /// </summary>
    public static byte[] BuildReadDiscStructureCdb(
        byte formatCode,
        ushort allocationLength,
        byte mediaType = 0,
        byte layerNumber = 0,
        int address = 0)
    {
        byte[] cdb = new byte[12];
        cdb[0] = OpCodeReadDiscStructure;
        cdb[1] = mediaType;
        cdb[2] = (byte)((address >> 24) & 0xFF);
        cdb[3] = (byte)((address >> 16) & 0xFF);
        cdb[4] = (byte)((address >> 8) & 0xFF);
        cdb[5] = (byte)(address & 0xFF);
        cdb[6] = layerNumber;
        cdb[7] = formatCode;
        cdb[8] = (byte)(allocationLength >> 8);
        cdb[9] = (byte)(allocationLength & 0xFF);
        cdb[10] = 0x00;
        cdb[11] = 0x00;
        return cdb;
    }

    /// <summary>
    /// Builds a 6-byte TEST UNIT READY CDB (OpCode 0x00).
    /// </summary>
    public static byte[] BuildTestUnitReadyCdb() => new byte[6]; // OpCode 0x00, all zeroes

    /// <summary>
    /// Builds a 6-byte START STOP UNIT CDB (OpCode 0x1B) for ejecting or loading the tray.
    /// </summary>
    public static byte[] BuildStartStopUnitCdb(bool start, bool loadEject, bool immediate = false)
    {
        byte[] cdb = new byte[6];
        cdb[0] = OpCodeStartStopUnit;
        cdb[1] = (byte)(immediate ? 0x01 : 0x00);
        cdb[4] = (byte)((loadEject ? 0x02 : 0x00) | (start ? 0x01 : 0x00));
        return cdb;
    }

    /// <summary>
    /// Builds a 10-byte SEND CUE SHEET CDB (OpCode 0x5D).
    /// </summary>
    public static byte[] BuildSendCueSheetCdb(uint cueSheetLength)
    {
        if (cueSheetLength > 0xFFFFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(cueSheetLength), "CUE sheet length must fit in a 24-bit unsigned integer (0..0xFFFFFF).");
        }

        byte[] cdb = new byte[10];
        cdb[0] = OpCodeSendCueSheet;
        cdb[6] = (byte)((cueSheetLength >> 16) & 0xFF);
        cdb[7] = (byte)((cueSheetLength >> 8) & 0xFF);
        cdb[8] = (byte)(cueSheetLength & 0xFF);
        return cdb;
    }
}
