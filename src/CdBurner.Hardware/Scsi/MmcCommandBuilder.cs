using System.Buffers.Binary;

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
    public const byte OpCodeWrite10              = 0x2A;
    public const byte OpCodeSynchronizeCache10   = 0x35;
    public const byte OpCodeReadTocPmaAtip       = 0x43;
    public const byte OpCodeGetConfiguration     = 0x46;
    public const byte OpCodeReadDiscInformation  = 0x51;
    public const byte OpCodeReadTrackInformation = 0x52;
    public const byte OpCodeModeSelect10         = 0x55;
    public const byte OpCodeModeSense10          = 0x5A;
    public const byte OpCodeCloseTrackSession    = 0x5B;
    public const byte OpCodeReadBufferCapacity   = 0x5C;
    public const byte OpCodeSendCueSheet         = 0x5D;
    public const byte OpCodeBlank                = 0xA1;
    public const byte OpCodeReadDiscStructure    = 0xAD;
    public const byte OpCodeReadCd               = 0xBE;
    public const byte OpCodeSendDiscStructure    = 0xBF;

    // MMC-5 Feature Codes
    public const ushort FeatureTrackAtOnce        = 0x002D;
    public const ushort FeatureCdMastering        = 0x002E;
    public const ushort FeatureDvdRecordableWrite = 0x002F;
    public const ushort FeatureLayerJumpRecording = 0x0033;
    public const ushort FeatureBdWrite            = 0x0041;

    // MMC Mode Pages
    public const byte ModePageWriteParameters    = 0x05;

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
    /// Builds a 10-byte MMC MODE SENSE (10) CDB (OpCode 0x5A) to query drive mode parameters.
    /// </summary>
    /// <param name="pageCode">Mode Page Code to query (bits 0..5).</param>
    /// <param name="allocationLength">Buffer allocation length (default 128).</param>
    /// <param name="disableBlockDescriptors">True to set DBD bit (bit 3 of byte 1), omitting block descriptors from response.</param>
    public static byte[] BuildModeSense10Cdb(byte pageCode, ushort allocationLength = 128, bool disableBlockDescriptors = true)
    {
        byte[] cdb = new byte[10];
        cdb[0] = OpCodeModeSense10;
        cdb[1] = (byte)(disableBlockDescriptors ? 0x08 : 0x00);
        cdb[2] = (byte)(pageCode & 0x3F);
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
    /// Parses MMC GET CONFIGURATION response buffer into an <see cref="MmcWriteCapabilityProbe"/>.
    /// </summary>
    /// <param name="response">Raw byte buffer returned by GET CONFIGURATION (0x46).</param>
    public static MmcWriteCapabilityProbe ParseGetConfigurationFeatures(ReadOnlySpan<byte> response)
    {
        if (response.Length < 8)
        {
            return new MmcWriteCapabilityProbe();
        }

        uint dataLength = BinaryPrimitives.ReadUInt32BigEndian(response.Slice(0, 4));
        int totalLength = Math.Min(response.Length, (int)Math.Min((long)int.MaxValue, (long)dataLength + 4));

        var featureCodes = new List<ushort>();
        bool hasTao = false, taoTestWrite = false, taoBuf = false;
        bool hasCdMastering = false, sao = false, rawDao = false, cdTextLeadIn = false, cdMasteringTestWrite = false, cdMasteringBuf = false;
        bool hasDvdWrite = false, dvdTestWrite = false, dvdBuf = false;
        bool hasLayerJump = false;
        bool hasBdWrite = false;

        int offset = 8;
        while (offset + 4 <= totalLength)
        {
            ushort featureCode = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(offset, 2));
            byte additionalLength = response[offset + 3];
            featureCodes.Add(featureCode);

            int payloadLength = Math.Min((int)additionalLength, Math.Max(0, totalLength - (offset + 4)));
            var payload = response.Slice(offset + 4, payloadLength);

            if (featureCode == FeatureTrackAtOnce)
            {
                hasTao = true;
                if (payload.Length >= 1)
                {
                    taoBuf = (payload[0] & 0x40) != 0;
                    taoTestWrite = (payload[0] & 0x04) != 0;
                }
            }
            else if (featureCode == FeatureCdMastering)
            {
                hasCdMastering = true;
                if (payload.Length >= 1)
                {
                    cdMasteringBuf = (payload[0] & 0x40) != 0;
                    sao = (payload[0] & 0x20) != 0;
                    rawDao = (payload[0] & 0x08) != 0;
                    cdMasteringTestWrite = (payload[0] & 0x04) != 0;
                    // R-W bit: the drive records user-supplied R-W subcode, which carries CD-Text in the lead-in.
                    cdTextLeadIn = (payload[0] & 0x01) != 0;
                }
            }
            else if (featureCode == FeatureDvdRecordableWrite)
            {
                hasDvdWrite = true;
                if (payload.Length >= 1)
                {
                    dvdBuf = (payload[0] & 0x40) != 0;
                    dvdTestWrite = (payload[0] & 0x04) != 0;
                }
            }
            else if (featureCode == FeatureLayerJumpRecording)
            {
                hasLayerJump = true;
            }
            else if (featureCode == FeatureBdWrite)
            {
                hasBdWrite = true;
            }

            offset += 4 + additionalLength;
        }

        return new MmcWriteCapabilityProbe
        {
            HasTaoFeature = hasTao,
            TaoSupportsTestWrite = taoTestWrite,
            TaoSupportsBufferUnderrunFree = taoBuf,
            HasCdMasteringFeature = hasCdMastering,
            SupportsSao = sao,
            SupportsRawDao = rawDao,
            SupportsCdTextLeadIn = cdTextLeadIn,
            CdMasteringSupportsTestWrite = cdMasteringTestWrite,
            CdMasteringSupportsBufferUnderrunFree = cdMasteringBuf,
            HasDvdWriteFeature = hasDvdWrite,
            DvdSupportsTestWrite = dvdTestWrite,
            DvdSupportsBufferUnderrunFree = dvdBuf,
            HasLayerJumpFeature = hasLayerJump,
            HasBdWriteFeature = hasBdWrite,
            FeatureCodes = featureCodes
        };
    }

    /// <summary>
    /// Parses MMC MODE SENSE (10) Page 0x05 (Write Parameters) response buffer into an <see cref="MmcWriteCapabilityProbe"/>.
    /// </summary>
    /// <param name="modeSense10Response">Raw byte buffer returned by MODE SENSE (10) (0x5A) Page 0x05.</param>
    public static MmcWriteCapabilityProbe ParseWriteParametersModePage(ReadOnlySpan<byte> modeSense10Response)
    {
        int pageOffset;

        // Check if caller passed just the mode page (starts with Page Code 0x05, e.g. [0x05, length, ...])
        if (modeSense10Response.Length >= 4 && (modeSense10Response[0] & 0x3F) == ModePageWriteParameters)
        {
            pageOffset = 0;
        }
        else if (modeSense10Response.Length >= 8)
        {
            ushort blockDescLen = BinaryPrimitives.ReadUInt16BigEndian(modeSense10Response.Slice(6, 2));
            pageOffset = 8 + blockDescLen;
        }
        else
        {
            return new MmcWriteCapabilityProbe();
        }

        if (pageOffset + 3 > modeSense10Response.Length)
        {
            return new MmcWriteCapabilityProbe();
        }

        byte pageCode = (byte)(modeSense10Response[pageOffset] & 0x3F);
        if (pageCode != ModePageWriteParameters)
        {
            return new MmcWriteCapabilityProbe();
        }

        byte b2 = modeSense10Response[pageOffset + 2];
        bool bufe = (b2 & 0x40) != 0;
        bool testWrite = (b2 & 0x10) != 0;
        byte writeType = (byte)(b2 & 0x0F);

        return new MmcWriteCapabilityProbe
        {
            ModePage05Present = true,
            ModePage05BufferUnderrunFreeEnabled = bufe,
            ModePage05TestWriteEnabled = testWrite,
            ModePage05WriteType = writeType
        };
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
