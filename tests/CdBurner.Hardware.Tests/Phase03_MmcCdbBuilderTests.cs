using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Scsi;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// Unit tests for Phase 03: Win32 SCSI Pass-Through Direct, Sense Key Decoder, and MMC-5 CDB Builders & Parsers.
/// </summary>
public class Phase03_MmcCdbBuilderTests
{
    #region ScsiPassThroughNative Constants & Struct Layout Tests

    [Fact]
    public void ScsiPassThroughConstants_MatchNtddscsiAndNtddcdrmDefinitions()
    {
        Assert.Equal(0x0004D014u, ScsiPassThroughNative.IOCTL_SCSI_PASS_THROUGH_DIRECT);
        Assert.Equal(0x00024054u, ScsiPassThroughNative.IOCTL_CDROM_READ_TOC_EX);
        Assert.Equal(0x0002403Eu, ScsiPassThroughNative.IOCTL_CDROM_RAW_READ);

        Assert.Equal(0, ScsiPassThroughNative.SCSI_IOCTL_DATA_OUT);
        Assert.Equal(1, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN);
        Assert.Equal(2, ScsiPassThroughNative.SCSI_IOCTL_DATA_UNSPECIFIED);
    }

    [Fact]
    public void ScsiPassThroughDirect_StructLayout_MatchesWindowsDdkSize()
    {
        int sptdSize = Marshal.SizeOf<ScsiPassThroughDirect>();
        int sptdWithSenseSize = Marshal.SizeOf<ScsiPassThroughDirectWithSense>();

        // On 64-bit systems, sizeof(ScsiPassThroughDirect) is 56 bytes due to 8-byte pointer alignment
        // On 32-bit systems, it is 48 bytes
        if (nint.Size == 8)
        {
            Assert.Equal(56, sptdSize);
            Assert.Equal(88, sptdWithSenseSize);
        }
        else
        {
            Assert.Equal(48, sptdSize);
            Assert.Equal(80, sptdWithSenseSize);
        }

        // SenseData offset must match exactly sizeof(ScsiPassThroughDirect)
        int senseOffset = (int)Marshal.OffsetOf<ScsiPassThroughDirectWithSense>(nameof(ScsiPassThroughDirectWithSense.SenseData));
        Assert.Equal(sptdSize, senseOffset);
    }

    [Fact]
    public void ScsiExecutionResult_FactoryMethods_ProduceExpectedState()
    {
        var success = ScsiExecutionResult.Success(2048);
        Assert.True(success.IsSuccess);
        Assert.Equal(0, success.ScsiStatus);
        Assert.Equal(2048u, success.DataTransferLength);
        Assert.Null(success.ErrorMessage);

        byte[] sense = new byte[32];
        sense[0] = 0x70;
        sense[2] = 0x02; // Not ready
        sense[12] = 0x3A; // Medium not present
        sense[13] = 0x00;

        var checkCond = ScsiExecutionResult.CheckCondition(0x02, 0, sense);
        Assert.False(checkCond.IsSuccess);
        Assert.Equal(0x02, checkCond.ScsiStatus);
        Assert.NotNull(checkCond.SenseInfo);
        Assert.Equal("Medium Not Present", checkCond.SenseInfo.Description);

        var failed = ScsiExecutionResult.Failed(5, "Access Denied");
        Assert.False(failed.IsSuccess);
        Assert.Equal(5, failed.Win32Error);
        Assert.Equal("Access Denied", failed.ErrorMessage);
    }

    #endregion

    #region MmcCommandBuilder Tests

    [Fact]
    public void BuildReadCdCdb_EncodesLbaAndTransferLengthInBigEndian_WithF8AtByte9()
    {
        const int testLba = 0x01020304;       // LBA = 16909060
        const int testTransferBlocks = 0x050607; // 329223 blocks
        const byte testSectorType = 1;         // CD-DA (bits 2..4: 1 << 2 = 0x04)
        const byte testSubChannel = 2;         // bits 0..2: 0x02

        byte[] cdb = MmcCommandBuilder.BuildReadCdCdb(
            testLba,
            testTransferBlocks,
            expectedSectorType: testSectorType,
            mainChannelSelectionBits: 0xF8,
            subChannelSelection: testSubChannel);

        Assert.Equal(12, cdb.Length);

        // Byte 0: OpCode 0xBE (READ CD)
        Assert.Equal(0xBE, cdb[0]);

        // Byte 1: Expected Sector Type in bits 2..4 -> 1 << 2 = 0x04
        Assert.Equal(0x04, cdb[1]);

        // Bytes 2..5: 32-bit big-endian LBA (0x01, 0x02, 0x03, 0x04)
        Assert.Equal(0x01, cdb[2]);
        Assert.Equal(0x02, cdb[3]);
        Assert.Equal(0x03, cdb[4]);
        Assert.Equal(0x04, cdb[5]);

        // Bytes 6..8: 24-bit big-endian transfer length (0x05, 0x06, 0x07)
        Assert.Equal(0x05, cdb[6]);
        Assert.Equal(0x06, cdb[7]);
        Assert.Equal(0x07, cdb[8]);

        // Byte 9: Main channel selection bits (0xF8: Sync + Header + SubHeader + User Data + EDC/ECC)
        Assert.Equal(0xF8, cdb[9]);

        // Byte 10: Sub-channel selection bits (0x02)
        Assert.Equal(0x02, cdb[10]);

        // Byte 11: Control byte (0x00)
        Assert.Equal(0x00, cdb[11]);
    }

    [Fact]
    public void BuildReadTocPmaAtipCdb_Format05_SetsOpCode43FormatBitsAndBigEndianLength()
    {
        const byte formatCdText = 0x05;
        const byte trackSession = 1;
        const ushort allocLength = 2048; // 0x0800

        byte[] cdb = MmcCommandBuilder.BuildReadTocPmaAtipCdb(
            format: formatCdText,
            trackSessionNumber: trackSession,
            allocationLength: allocLength,
            msf: false);

        Assert.Equal(10, cdb.Length);

        // Byte 0: OpCode 0x43 (READ TOC/PMA/ATIP)
        Assert.Equal(0x43, cdb[0]);

        // Byte 1: MSF bit = 0 (LBA mode)
        Assert.Equal(0x00, cdb[1]);

        // Byte 2: Format bits 0..3 = 0x05
        Assert.Equal(0x05, cdb[2] & 0x0F);

        // Byte 6: Track/Session number = 1
        Assert.Equal(trackSession, cdb[6]);

        // Bytes 7..8: Allocation length big-endian (2048 = 0x0800)
        Assert.Equal(0x08, cdb[7]);
        Assert.Equal(0x00, cdb[8]);

        // Byte 9: Control = 0x00
        Assert.Equal(0x00, cdb[9]);
    }

    [Fact]
    public void BuildReadTocPmaAtipCdb_MsfFlag_SetsBit1InByte1()
    {
        byte[] cdb = MmcCommandBuilder.BuildReadTocPmaAtipCdb(
            format: MmcCommandBuilder.TocFormatFormattedToc,
            trackSessionNumber: 0,
            allocationLength: 1024,
            msf: true);

        Assert.Equal(0x43, cdb[0]);
        Assert.Equal(0x02, cdb[1] & 0x02); // Bit 1 set for MSF
    }

    [Fact]
    public void BuildInquiryCdb_BuildsStandardSixByteCdb()
    {
        byte[] cdb = MmcCommandBuilder.BuildInquiryCdb(allocationLength: 96);

        Assert.Equal(6, cdb.Length);
        Assert.Equal(0x12, cdb[0]); // OpCode INQUIRY
        Assert.Equal(0x00, cdb[1]);
        Assert.Equal(0x00, cdb[2]);
        Assert.Equal(0x00, cdb[3]); // 96 = 0x0060
        Assert.Equal(0x60, cdb[4]);
        Assert.Equal(0x00, cdb[5]);
    }

    [Fact]
    public void BuildGetConfigurationCdb_BuildsTenByteCdbWithBigEndianFields()
    {
        byte[] cdb = MmcCommandBuilder.BuildGetConfigurationCdb(startingFeature: 0x0020, allocationLength: 4096);

        Assert.Equal(10, cdb.Length);
        Assert.Equal(0x46, cdb[0]); // OpCode GET CONFIGURATION
        Assert.Equal(0x00, cdb[1]);
        Assert.Equal(0x00, cdb[2]); // Starting feature 0x0020
        Assert.Equal(0x20, cdb[3]);
        Assert.Equal(0x10, cdb[7]); // Allocation length 4096 = 0x1000
        Assert.Equal(0x00, cdb[8]);
        Assert.Equal(0x00, cdb[9]);
    }

    [Fact]
    public void BuildReadDiscInformationCdb_BuildsTenByteCdb()
    {
        byte[] cdb = MmcCommandBuilder.BuildReadDiscInformationCdb(allocationLength: 64);

        Assert.Equal(10, cdb.Length);
        Assert.Equal(0x51, cdb[0]); // OpCode READ DISC INFORMATION
        Assert.Equal(0x00, cdb[7]); // Allocation length 64 = 0x0040
        Assert.Equal(0x40, cdb[8]);
        Assert.Equal(0x00, cdb[9]);
    }

    [Fact]
    public void BuildSynchronizeCacheCdb_BuildsTenByteCdbWithImmediateFlag()
    {
        byte[] cdbSync = MmcCommandBuilder.BuildSynchronizeCacheCdb(lba: 100, blockCount: 16, immediate: true);

        Assert.Equal(10, cdbSync.Length);
        Assert.Equal(0x35, cdbSync[0]); // OpCode SYNCHRONIZE CACHE
        Assert.Equal(0x02, cdbSync[1]); // Immediate flag (bit 1)
        Assert.Equal(0x00, cdbSync[2]);
        Assert.Equal(0x00, cdbSync[3]);
        Assert.Equal(0x00, cdbSync[4]);
        Assert.Equal(100, cdbSync[5]);  // LBA 100
        Assert.Equal(0x00, cdbSync[7]);
        Assert.Equal(16, cdbSync[8]);   // Block count 16
    }

    [Fact]
    public void BuildBlankCdb_BuildsTwelveByteCdbWithBlankingTypeAndAddress()
    {
        byte[] cdbBlank = MmcCommandBuilder.BuildBlankCdb(
            blankingType: MmcCommandBuilder.BlankTypeMinimal,
            startAddress: 0,
            immediate: true);

        Assert.Equal(12, cdbBlank.Length);
        Assert.Equal(0xA1, cdbBlank[0]); // OpCode BLANK
        Assert.Equal(0x11, cdbBlank[1]); // Minimal blank (0x01) | Immediate (0x10) = 0x11
        Assert.Equal(0x00, cdbBlank[2]);
        Assert.Equal(0x00, cdbBlank[3]);
        Assert.Equal(0x00, cdbBlank[4]);
        Assert.Equal(0x00, cdbBlank[5]);
    }

    [Fact]
    public void BuildSendDiscStructureCdb_BuildsTwelveByteCdb()
    {
        byte[] cdbSend = MmcCommandBuilder.BuildSendDiscStructureCdb(formatCode: 0x00, structureDataLength: 16);

        Assert.Equal(12, cdbSend.Length);
        Assert.Equal(0xBF, cdbSend[0]); // OpCode SEND DISC STRUCTURE
        Assert.Equal(0x00, cdbSend[7]); // Format code 0x00
        Assert.Equal(0x00, cdbSend[8]); // Length 16 = 0x0010
        Assert.Equal(0x10, cdbSend[9]);
    }

    [Fact]
    public void UtilityCdbBuilders_BuildExpectedByteSequences()
    {
        byte[] tur = MmcCommandBuilder.BuildTestUnitReadyCdb();
        Assert.Equal(6, tur.Length);
        Assert.Equal(0x00, tur[0]);

        byte[] eject = MmcCommandBuilder.BuildStartStopUnitCdb(start: false, loadEject: true, immediate: true);
        Assert.Equal(6, eject.Length);
        Assert.Equal(0x1B, eject[0]);
        Assert.Equal(0x01, eject[1]); // Immediate bit 0
        Assert.Equal(0x02, eject[4]); // Eject bit 1

        byte[] cue = MmcCommandBuilder.BuildSendCueSheetCdb(cueSheetLength: 0x00010203);
        Assert.Equal(10, cue.Length);
        Assert.Equal(0x5D, cue[0]);
        Assert.Equal(0x01, cue[6]);
        Assert.Equal(0x02, cue[7]);
        Assert.Equal(0x03, cue[8]);
    }

    #endregion

    #region ScsiSenseDecoder Tests

    [Fact]
    public void ScsiSenseDecoder_DecodesFixedSenseBuffer_ForMediumNotPresent()
    {
        byte[] buffer = new byte[32];
        buffer[0] = 0x70; // Current error, fixed format
        buffer[2] = 0x02; // Sense Key: Not Ready (0x02)
        buffer[7] = 10;   // Additional length
        buffer[12] = 0x3A; // ASC: Medium Not Present
        buffer[13] = 0x00; // ASCQ: 00

        ScsiSenseInfo? info = ScsiSenseDecoder.Decode(buffer);

        Assert.NotNull(info);
        Assert.Equal(0x02, info.SenseKey);
        Assert.Equal(0x3A, info.Asc);
        Assert.Equal(0x00, info.Ascq);
        Assert.Equal("Not Ready", info.SenseKeyName);
        Assert.Equal("02/3A/00", info.AscAscqHex);
        Assert.Equal("Medium Not Present", info.Description);
        Assert.Equal("02/3A/00 Medium Not Present", info.ToString());
    }

    [Fact]
    public void ScsiSenseDecoder_DecodesFixedSenseBuffer_ForUnrecoveredReadError()
    {
        byte[] buffer = new byte[32];
        buffer[0] = 0x70; // Current error, fixed format
        buffer[2] = 0x03; // Sense Key: Medium Error (0x03)
        buffer[7] = 10;
        buffer[12] = 0x11; // ASC: Unrecovered Read Error
        buffer[13] = 0x00; // ASCQ: 00

        ScsiSenseInfo? info = ScsiSenseDecoder.Decode(buffer);

        Assert.NotNull(info);
        Assert.Equal(0x03, info.SenseKey);
        Assert.Equal(0x11, info.Asc);
        Assert.Equal(0x00, info.Ascq);
        Assert.Equal("Medium Error", info.SenseKeyName);
        Assert.Equal("03/11/00", info.AscAscqHex);
        Assert.Equal("Unrecovered Read Error", info.Description);
        Assert.Equal("03/11/00 Unrecovered Read Error", info.ToString());
    }

    [Theory]
    [InlineData(0x02, 0x04, 0x01, "Logical Unit Is in Process of Becoming Ready")]
    [InlineData(0x03, 0x02, 0x00, "No Seek Complete")]
    [InlineData(0x05, 0x21, 0x00, "Logical Block Address Out of Range")]
    [InlineData(0x05, 0x30, 0x05, "Cannot Write Medium - Incompatible Format")]
    [InlineData(0x02, 0x3A, 0x01, "Medium Not Present - Tray Closed")]
    [InlineData(0x02, 0x3A, 0x02, "Medium Not Present - Tray Open")]
    [InlineData(0x06, 0x28, 0x00, "Not Ready to Ready Change, Medium May Have Changed")]
    [InlineData(0x07, 0x27, 0x00, "Write Protected")]
    [InlineData(0x03, 0x73, 0x03, "Power Calibration Area Error")]
    public void ScsiSenseDecoder_DecodesCommonOpticalSenseCodes(
        byte senseKey,
        byte asc,
        byte ascq,
        string expectedDescription)
    {
        byte[] buffer = new byte[32];
        buffer[0] = 0x70;
        buffer[2] = senseKey;
        buffer[12] = asc;
        buffer[13] = ascq;

        ScsiSenseInfo? info = ScsiSenseDecoder.Decode(buffer);

        Assert.NotNull(info);
        Assert.Equal(senseKey, info.SenseKey);
        Assert.Equal(asc, info.Asc);
        Assert.Equal(ascq, info.Ascq);
        Assert.Equal(expectedDescription, info.Description);
    }

    [Fact]
    public void ScsiSenseDecoder_DecodesDescriptorFormatSenseData_Correctly()
    {
        // Response Code 0x72 = Descriptor Format
        byte[] buffer = new byte[16];
        buffer[0] = 0x72;
        buffer[1] = 0x02; // Sense Key: Not Ready
        buffer[2] = 0x3A; // ASC: Medium Not Present
        buffer[3] = 0x00; // ASCQ

        ScsiSenseInfo? info = ScsiSenseDecoder.Decode(buffer);

        Assert.NotNull(info);
        Assert.Equal(0x02, info.SenseKey);
        Assert.Equal(0x3A, info.Asc);
        Assert.Equal(0x00, info.Ascq);
        Assert.Equal("Medium Not Present", info.Description);
    }

    [Fact]
    public void ScsiSenseDecoder_HandlesAllZeroAndEmptyBuffers()
    {
        Assert.Null(ScsiSenseDecoder.Decode(ReadOnlySpan<byte>.Empty));

        byte[] allZeros = new byte[32];
        ScsiSenseInfo? zeroInfo = ScsiSenseDecoder.Decode(allZeros);
        Assert.NotNull(zeroInfo);
        Assert.Equal(0, zeroInfo.SenseKey);
        Assert.Equal(0, zeroInfo.Asc);
        Assert.Equal(0, zeroInfo.Ascq);
        Assert.Equal("No Additional Sense Information", zeroInfo.Description);
    }

    #endregion

    #region MmcResponseParser Tests

    [Fact]
    public void ParseInquiry_ExtractsExactStringsAndDeviceType_FromSyntheticBuffer()
    {
        byte[] buffer = new byte[36];
        buffer[0] = 0x05; // Peripheral Device Type = 0x05 (CD/DVD device)
        buffer[1] = 0x80; // RMB = 1 (Removable Media)
        buffer[2] = 0x05; // SPC-3 compliant
        buffer[3] = 0x02;
        buffer[4] = 31;   // Additional length

        // Vendor ID: bytes 8..15 (8 bytes, ASCII, space padded)
        byte[] vendorBytes = Encoding.ASCII.GetBytes("PLEXTOR ");
        vendorBytes.CopyTo(buffer, 8);

        // Product ID: bytes 16..31 (16 bytes, ASCII, space padded)
        byte[] productBytes = Encoding.ASCII.GetBytes("PX-891SAF       ");
        productBytes.CopyTo(buffer, 16);

        // Revision: bytes 32..35 (4 bytes, ASCII)
        byte[] revBytes = Encoding.ASCII.GetBytes("1.00");
        revBytes.CopyTo(buffer, 32);

        MmcInquiryResponse response = MmcResponseParser.ParseInquiry(buffer);

        Assert.Equal(0x05, response.PeripheralDeviceType);
        Assert.True(response.IsRemovable);
        Assert.True(response.IsCdDvdDevice);
        Assert.Equal("PLEXTOR", response.VendorId);
        Assert.Equal("PX-891SAF", response.ProductId);
        Assert.Equal("1.00", response.ProductRevision);
        Assert.Equal("PLEXTOR PX-891SAF (Rev 1.00)", response.ToString());
    }

    [Fact]
    public void ParseInquiry_ThrowsArgumentException_WhenBufferIsLessThan36Bytes()
    {
        byte[] shortBuf = new byte[35];
        Assert.Throws<ArgumentException>(() => MmcResponseParser.ParseInquiry(shortBuf));
    }

    [Fact]
    public void ParseToc_ExtractsExactLbaAddressesAndTrackAttributes_FromSyntheticBuffer()
    {
        // 4-byte header + 3 track descriptors (8 bytes each) = 28 bytes
        byte[] buffer = new byte[28];

        // Header: TOC Data Length = 26 (0x001A), First Track = 1, Last Track = 2
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(0, 2), 26);
        buffer[2] = 1; // First track
        buffer[3] = 2; // Last track

        // Track 1: Data track (AdrControl = 0x14), Track = 1, StartLba = 0
        buffer[4] = 0;
        buffer[5] = 0x14; // ADR = 1, Control = 4 (Data track)
        buffer[6] = 1;    // Track 1
        buffer[7] = 0;
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(8, 4), 0); // LBA 0

        // Track 2: Audio track (AdrControl = 0x10), Track = 2, StartLba = 15000
        buffer[12] = 0;
        buffer[13] = 0x10; // ADR = 1, Control = 0 (Audio track)
        buffer[14] = 2;    // Track 2
        buffer[15] = 0;
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(16, 4), 15000); // LBA 15000

        // Lead-Out: Track 0xAA, StartLba = 30000
        buffer[20] = 0;
        buffer[21] = 0x10;
        buffer[22] = 0xAA; // Track 0xAA (Lead-Out)
        buffer[23] = 0;
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(24, 4), 30000); // LBA 30000

        IReadOnlyList<TocTrackDescriptor> tracks = MmcResponseParser.ParseToc(buffer, isMsf: false);

        Assert.Equal(3, tracks.Count);

        // Track 1
        Assert.Equal(1, tracks[0].TrackNumber);
        Assert.Equal(0x14, tracks[0].AdrControl);
        Assert.Equal(1, tracks[0].Adr);
        Assert.Equal(4, tracks[0].Control);
        Assert.True(tracks[0].IsDataTrack);
        Assert.False(tracks[0].IsAudioTrack);
        Assert.False(tracks[0].IsLeadOut);
        Assert.Equal(0, tracks[0].StartLba);

        // Track 2
        Assert.Equal(2, tracks[1].TrackNumber);
        Assert.Equal(0x10, tracks[1].AdrControl);
        Assert.Equal(1, tracks[1].Adr);
        Assert.Equal(0, tracks[1].Control);
        Assert.False(tracks[1].IsDataTrack);
        Assert.True(tracks[1].IsAudioTrack);
        Assert.False(tracks[1].IsLeadOut);
        Assert.Equal(15000, tracks[1].StartLba);

        // Lead-Out
        Assert.Equal(0xAA, tracks[2].TrackNumber);
        Assert.True(tracks[2].IsLeadOut);
        Assert.Equal(30000, tracks[2].StartLba);

        // Parse full response object
        TocResponse fullResponse = MmcResponseParser.ParseTocResponse(buffer, isMsf: false);
        Assert.Equal(1, fullResponse.FirstTrack);
        Assert.Equal(2, fullResponse.LastTrack);
        Assert.Equal(3, fullResponse.Tracks.Count);
    }

    [Fact]
    public void ParseToc_DecodesMsfAddressesCorrectly()
    {
        // 4-byte header + 2 track descriptors (8 bytes each) = 20 bytes
        byte[] buffer = new byte[20];
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(0, 2), 18);
        buffer[2] = 1;
        buffer[3] = 1;

        // Track 1: MSF 00:02:00 -> LBA 0 (150 frames lead-in offset)
        buffer[4] = 0;
        buffer[5] = 0x10;
        buffer[6] = 1;
        buffer[7] = 0;
        buffer[8] = 0;  // reserved
        buffer[9] = 0;  // Minutes: 0
        buffer[10] = 2; // Seconds: 2
        buffer[11] = 0; // Frames: 0

        // Track 0xAA (Lead-Out): MSF 05:22:00
        // Frames = (5 * 60 + 22) * 75 + 0 = 322 * 75 = 24150. LBA = 24150 - 150 = 24000.
        buffer[12] = 0;
        buffer[13] = 0x10;
        buffer[14] = 0xAA;
        buffer[15] = 0;
        buffer[16] = 0;  // reserved
        buffer[17] = 5;  // Minutes: 5
        buffer[18] = 22; // Seconds: 22
        buffer[19] = 0;  // Frames: 0

        IReadOnlyList<TocTrackDescriptor> tracks = MmcResponseParser.ParseToc(buffer, isMsf: true);

        Assert.Equal(2, tracks.Count);
        Assert.Equal(0, tracks[0].StartLba);
        Assert.Equal(24000, tracks[1].StartLba);
    }

    #endregion
}
