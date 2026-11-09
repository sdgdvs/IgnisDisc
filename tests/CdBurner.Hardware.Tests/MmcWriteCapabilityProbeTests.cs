using System.Buffers.Binary;
using CdBurner.Core.Abstractions;
using CdBurner.Hardware.Scsi;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// Unit tests for P1 Phase 1: SCSI MMC Write Capability Probe, Mode Page 0x05 Write Parameters,
/// and VirtualDiscRecorder MMC profile reporting.
/// </summary>
public class MmcWriteCapabilityProbeTests
{
    #region BuildModeSense10Cdb Tests

    [Fact]
    public void BuildModeSense10Cdb_WithDefaultParameters_BuildsTenByteCdbWithDbdBit()
    {
        byte[] cdb = MmcCommandBuilder.BuildModeSense10Cdb(
            pageCode: MmcCommandBuilder.ModePageWriteParameters,
            allocationLength: 128,
            disableBlockDescriptors: true);

        Assert.Equal(10, cdb.Length);
        Assert.Equal(0x5A, cdb[0]); // OpCode MODE SENSE (10)
        Assert.Equal(0x08, cdb[1]); // DBD = 1 (bit 3)
        Assert.Equal(0x05, cdb[2]); // Page Code 0x05 (PC = 00b current values)
        Assert.Equal(0x00, cdb[3]); // Subpage code = 0
        Assert.Equal(0x00, cdb[4]);
        Assert.Equal(0x00, cdb[5]);
        Assert.Equal(0x00, cdb[6]);
        Assert.Equal(0x00, cdb[7]); // Allocation Length big-endian (128 = 0x0080)
        Assert.Equal(0x80, cdb[8]);
        Assert.Equal(0x00, cdb[9]); // Control = 0
    }

    [Fact]
    public void BuildModeSense10Cdb_WithoutDbd_ClearsByteOneDbdBit()
    {
        byte[] cdb = MmcCommandBuilder.BuildModeSense10Cdb(
            pageCode: 0x05,
            allocationLength: 256,
            disableBlockDescriptors: false);

        Assert.Equal(0x00, cdb[1]); // DBD = 0
        Assert.Equal(0x01, cdb[7]); // Allocation Length (256 = 0x0100)
        Assert.Equal(0x00, cdb[8]);
    }

    [Fact]
    public void BuildModeSense10Cdb_MasksPageCodeToSixBits()
    {
        // 0x85 (bit 7 set) should be masked with 0x3F -> 0x05
        byte[] cdb = MmcCommandBuilder.BuildModeSense10Cdb(0x85, 64);
        Assert.Equal(0x05, cdb[2]);
    }

    #endregion

    #region ParseGetConfigurationFeatures Tests

    [Fact]
    public void ParseGetConfigurationFeatures_EmptyOrShortBuffer_ReturnsEmptyProbe()
    {
        var probeEmpty = MmcCommandBuilder.ParseGetConfigurationFeatures(ReadOnlySpan<byte>.Empty);
        Assert.False(probeEmpty.HasTaoFeature);
        Assert.False(probeEmpty.HasCdMasteringFeature);
        Assert.False(probeEmpty.HasDvdWriteFeature);
        Assert.Empty(probeEmpty.FeatureCodes);

        byte[] shortHeader = new byte[7];
        var probeShort = MmcCommandBuilder.ParseGetConfigurationFeatures(shortHeader);
        Assert.False(probeShort.HasTaoFeature);
        Assert.Empty(probeShort.FeatureCodes);
    }

    [Fact]
    public void ParseGetConfigurationFeatures_SyntheticBuffer_ParsesAllExpectedMmcFeatures()
    {
        // Construct synthetic GET CONFIGURATION response buffer
        // Header: 8 bytes
        // Descriptors:
        // 1. 0x002D TAO: 4 header + 4 payload = 8 bytes
        // 2. 0x002E CD Mastering: 4 header + 4 payload = 8 bytes
        // 3. 0x002F DVD Write: 4 header + 4 payload = 8 bytes
        // 4. 0x0033 Layer Jump: 4 header + 0 payload = 4 bytes
        // 5. 0x0041 BD Write: 4 header + 0 payload = 4 bytes
        // Total data = 8 + 8 + 8 + 8 + 4 + 4 = 40 bytes. DataLength = 40 - 4 = 36 (0x00000024).

        byte[] buffer = new byte[40];

        // 8-byte header
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(0, 4), 36); // Data length
        buffer[4] = 0; // Reserved
        buffer[5] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(6, 2), 0x0011); // Current profile = DVD-R

        int offset = 8;

        // 1. Feature 0x002D (TAO): BUF=1 (0x40), TestWrite=1 (0x04), CD-RW=1 (0x02) -> 0x46
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), MmcCommandBuilder.FeatureTrackAtOnce);
        buffer[offset + 2] = 0x05; // Current = 1, Version = 1
        buffer[offset + 3] = 4;    // Additional Length = 4
        buffer[offset + 4] = 0x46; // BUF | TestWrite | CD-RW
        offset += 8;

        // 2. Feature 0x002E (CD Mastering): BUF=1 (0x40), SAO=1 (0x20), Raw=1 (0x08), TestWrite=1 (0x04) -> 0x6C
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), MmcCommandBuilder.FeatureCdMastering);
        buffer[offset + 2] = 0x05;
        buffer[offset + 3] = 4;
        buffer[offset + 4] = 0x6C;
        offset += 8;

        // 3. Feature 0x002F (DVD-R/-RW Write): BUF=1 (0x40), TestWrite=1 (0x04) -> 0x44
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), MmcCommandBuilder.FeatureDvdRecordableWrite);
        buffer[offset + 2] = 0x05;
        buffer[offset + 3] = 4;
        buffer[offset + 4] = 0x44;
        offset += 8;

        // 4. Feature 0x0033 (Layer Jump Recording): Additional Length = 0
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), MmcCommandBuilder.FeatureLayerJumpRecording);
        buffer[offset + 2] = 0x05;
        buffer[offset + 3] = 0;
        offset += 4;

        // 5. Feature 0x0041 (BD Write): Additional Length = 0
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), MmcCommandBuilder.FeatureBdWrite);
        buffer[offset + 2] = 0x05;
        buffer[offset + 3] = 0;
        offset += 4;

        Assert.Equal(40, offset);

        var probe = MmcCommandBuilder.ParseGetConfigurationFeatures(buffer);

        // Feature 0x002D
        Assert.True(probe.HasTaoFeature);
        Assert.True(probe.TaoSupportsBufferUnderrunFree);
        Assert.True(probe.TaoSupportsTestWrite);

        // Feature 0x002E
        Assert.True(probe.HasCdMasteringFeature);
        Assert.True(probe.SupportsSao);
        Assert.True(probe.SupportsRawDao);
        Assert.False(probe.SupportsCdTextLeadIn); // 0x6C has no R-W bit (0x01)
        Assert.True(probe.CdMasteringSupportsTestWrite);
        Assert.True(probe.CdMasteringSupportsBufferUnderrunFree);

        // Feature 0x002F
        Assert.True(probe.HasDvdWriteFeature);
        Assert.True(probe.DvdSupportsBufferUnderrunFree);
        Assert.True(probe.DvdSupportsTestWrite);

        // Feature 0x0033 & 0x0041
        Assert.True(probe.HasLayerJumpFeature);
        Assert.True(probe.HasBdWriteFeature);

        // Aggregates
        Assert.True(probe.SupportsBufferUnderrunFree);
        Assert.True(probe.SupportsTestWrite);

        // FeatureCodes
        Assert.Equal(5, probe.FeatureCodes.Count);
        Assert.Contains(MmcCommandBuilder.FeatureTrackAtOnce, probe.FeatureCodes);
        Assert.Contains(MmcCommandBuilder.FeatureCdMastering, probe.FeatureCodes);
        Assert.Contains(MmcCommandBuilder.FeatureDvdRecordableWrite, probe.FeatureCodes);
        Assert.Contains(MmcCommandBuilder.FeatureLayerJumpRecording, probe.FeatureCodes);
        Assert.Contains(MmcCommandBuilder.FeatureBdWrite, probe.FeatureCodes);

        // Summary
        var summary = probe.ToFeatureSummary();
        Assert.Contains("0x002D TAO", summary);
        Assert.Contains("0x002E SAO/Raw", summary);
        Assert.Contains("0x002F DVD Write", summary);
        Assert.Contains("0x0033 Layer Jump", summary);
        Assert.Contains("0x0041 BD Write", summary);
    }

    [Fact]
    public void ParseGetConfigurationFeatures_FeaturesWithoutTestWriteOrBuf_FlagsRemainFalse()
    {
        byte[] buffer = new byte[24];
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(0, 4), 20);

        // Feature 0x002D with 0 payload bits set
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(8, 2), MmcCommandBuilder.FeatureTrackAtOnce);
        buffer[10] = 0x01;
        buffer[11] = 4;
        buffer[12] = 0x00; // All flags false

        // Feature 0x002E with only SAO set (0x20), BUF=0, TestWrite=0, Raw=0
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(16, 2), MmcCommandBuilder.FeatureCdMastering);
        buffer[18] = 0x01;
        buffer[19] = 4;
        buffer[20] = 0x20; // SAO only

        var probe = MmcCommandBuilder.ParseGetConfigurationFeatures(buffer);

        Assert.True(probe.HasTaoFeature);
        Assert.False(probe.TaoSupportsTestWrite);
        Assert.False(probe.TaoSupportsBufferUnderrunFree);

        Assert.True(probe.HasCdMasteringFeature);
        Assert.True(probe.SupportsSao);
        Assert.False(probe.SupportsRawDao);
        Assert.False(probe.CdMasteringSupportsTestWrite);
        Assert.False(probe.CdMasteringSupportsBufferUnderrunFree);

        Assert.False(probe.SupportsTestWrite);
        Assert.False(probe.SupportsBufferUnderrunFree);
        Assert.False(probe.SupportsCdTextLeadIn); // SAO alone does not mean the drive can write CD-Text
    }

    [Theory]
    [InlineData(0x21, true)]  // SAO + R-W
    [InlineData(0x01, true)]  // R-W only
    [InlineData(0x7E, false)] // every other CD Mastering bit, no R-W
    public void ParseGetConfigurationFeatures_CdText_FollowsCdMasteringRwBit(byte flags, bool expected)
    {
        // MMC-5 CD Mastering feature (0x002E), byte 4: BUF 0x40, SAO 0x20, Raw MS 0x10, Raw 0x08,
        // Test Write 0x04, CD-RW 0x02, R-W 0x01.
        byte[] buffer = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(0, 4), 12);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(8, 2), MmcCommandBuilder.FeatureCdMastering);
        buffer[10] = 0x01;
        buffer[11] = 4;
        buffer[12] = flags;

        var probe = MmcCommandBuilder.ParseGetConfigurationFeatures(buffer);

        Assert.Equal(expected, probe.SupportsCdTextLeadIn);
    }

    [Fact]
    public void ParseGetConfigurationFeatures_TruncatedDescriptor_DoesNotThrow()
    {
        // AdditionalLength says 16 bytes, but buffer ends abruptly after 4
        byte[] buffer = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(0, 4), 12);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(8, 2), MmcCommandBuilder.FeatureTrackAtOnce);
        buffer[10] = 0x01;
        buffer[11] = 16; // claims 16 bytes

        var probe = MmcCommandBuilder.ParseGetConfigurationFeatures(buffer);
        Assert.True(probe.HasTaoFeature);
    }

    #endregion

    #region ParseWriteParametersModePage Tests

    [Fact]
    public void ParseWriteParametersModePage_StandardModeSense10Buffer_ExtractsParameters()
    {
        // 8-byte Mode Parameter Header + Page 0x05
        byte[] buffer = new byte[8 + 12];

        // Header: ModeDataLength = 18, BlockDescriptorLength = 0
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(0, 2), 18);
        buffer[2] = 0x00; // Medium Type
        buffer[3] = 0x00; // Device-Specific
        buffer[4] = 0x00;
        buffer[5] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(6, 2), 0); // Block Descriptor Length = 0

        // Page 0x05 at offset 8:
        buffer[8] = 0x05; // Page Code 0x05
        buffer[9] = 10;   // Page Length = 10
        // Byte 2 (buffer[10]): BUFE=1 (0x40), TestWrite=1 (0x10), WriteType=0x02 (SAO) -> 0x52
        buffer[10] = 0x52;

        var probe = MmcCommandBuilder.ParseWriteParametersModePage(buffer);

        Assert.True(probe.ModePage05Present);
        Assert.True(probe.ModePage05BufferUnderrunFreeEnabled);
        Assert.True(probe.ModePage05TestWriteEnabled);
        Assert.Equal(0x02, probe.ModePage05WriteType);
        Assert.True(probe.SupportsBufferUnderrunFree);
        Assert.True(probe.SupportsTestWrite);

        var summary = probe.ToFeatureSummary();
        Assert.Contains("ModePage 0x05 BUFE", summary);
    }

    [Fact]
    public void ParseWriteParametersModePage_WithBlockDescriptors_FindsPageAtCorrectOffset()
    {
        // 8-byte Mode Header + 8-byte Block Descriptor + Page 0x05 at offset 16
        byte[] buffer = new byte[8 + 8 + 8];

        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(0, 2), 22);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(6, 2), 8); // 8-byte block descriptor

        // Page 0x05 starts at 8 + 8 = 16
        buffer[16] = 0x85; // Page Code 0x05 with PS bit set
        buffer[17] = 6;
        buffer[18] = 0x41; // BUFE=1 (0x40), TestWrite=0, WriteType=0x01 (TAO)

        var probe = MmcCommandBuilder.ParseWriteParametersModePage(buffer);

        Assert.True(probe.ModePage05Present);
        Assert.True(probe.ModePage05BufferUnderrunFreeEnabled);
        Assert.False(probe.ModePage05TestWriteEnabled);
        Assert.Equal(0x01, probe.ModePage05WriteType);
    }

    [Fact]
    public void ParseWriteParametersModePage_BarePageBuffer_ParsesSuccessfully()
    {
        // Directly passing mode page slice (starts with 0x05)
        byte[] pageSlice = new byte[6];
        pageSlice[0] = 0x05;
        pageSlice[1] = 4;
        pageSlice[2] = 0x10; // TestWrite=1, BUFE=0, WriteType=0 (Packet)

        var probe = MmcCommandBuilder.ParseWriteParametersModePage(pageSlice);

        Assert.True(probe.ModePage05Present);
        Assert.False(probe.ModePage05BufferUnderrunFreeEnabled);
        Assert.True(probe.ModePage05TestWriteEnabled);
        Assert.Equal(0x00, probe.ModePage05WriteType);
    }

    [Fact]
    public void ParseWriteParametersModePage_WrongPageCodeOrTooShort_ReturnsNotPresent()
    {
        byte[] wrongPage = new byte[12];
        wrongPage[0] = 0x2A; // Page 0x2A (Capabilities)
        wrongPage[1] = 10;
        var probeWrong = MmcCommandBuilder.ParseWriteParametersModePage(wrongPage);
        Assert.False(probeWrong.ModePage05Present);

        byte[] tooShort = new byte[3];
        var probeShort = MmcCommandBuilder.ParseWriteParametersModePage(tooShort);
        Assert.False(probeShort.ModePage05Present);
    }

    #endregion

    #region VirtualDiscRecorder Capabilities Tests

    [Fact]
    public void VirtualDiscRecorder_GetCapabilities_PopulatesAllMmcWriteFeatures()
    {
        using var recorder = new VirtualDiscRecorder("VDRV-TEST", "IGNIS", "SIM-RECORDER", @"V:\");
        DriveCapabilitiesDto caps = recorder.GetCapabilities();

        Assert.NotNull(caps);
        Assert.True(caps.SupportsNativeTao);
        Assert.True(caps.SupportsNativeSao);
        Assert.True(caps.SupportsNativeRawDao);
        Assert.True(caps.SupportsTestWrite);
        Assert.True(caps.SupportsBufferUnderrunProtection);
        Assert.True(caps.SupportsBufferUnderrunFree);
        Assert.True(caps.SupportsLayerJump);

        Assert.NotNull(caps.MmcFeatureSummary);
        Assert.Equal(6, caps.MmcFeatureSummary.Count);
        Assert.Contains("0x002D TAO", caps.MmcFeatureSummary);
        Assert.Contains("0x002E SAO/Raw", caps.MmcFeatureSummary);
        Assert.Contains("0x002F DVD Write", caps.MmcFeatureSummary);
        Assert.Contains("0x0033 Layer Jump", caps.MmcFeatureSummary);
        Assert.Contains("0x0041 BD Write", caps.MmcFeatureSummary);
        Assert.Contains("ModePage 0x05 BUFE", caps.MmcFeatureSummary);
    }

    #endregion

    #region PhysicalSectorReader ProbeWriteCapabilities Tests

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"Z:\InvalidDriveLetter_NonExistent_Probe")]
    public void PhysicalSectorReader_ProbeWriteCapabilities_NeverThrowsOnInvalidOrMissingDrive(string? volumePath)
    {
        var probe = PhysicalSectorReader.ProbeWriteCapabilities(volumePath);
        Assert.NotNull(probe);
        Assert.False(probe.HasTaoFeature);
        Assert.False(probe.HasCdMasteringFeature);
        Assert.False(probe.ModePage05Present);
        Assert.Empty(probe.FeatureCodes);
    }

    #endregion

    #region MmcMediaIdInspector (ATIP / ADIP / DVD Pre-Recorded / BD Disc Info) Tests

    [Fact]
    public void ParseCdAtip_TaiyoYudenCyanineCdR_IdentifiesManufacturerAndStrategy()
    {
        byte[] atip = new byte[16];
        BinaryPrimitives.WriteUInt16BigEndian(atip.AsSpan(0, 2), 14);
        atip[6] = 0x80; // CD-R (bit 6 = 0)
        atip[8] = 97;   // Lead-in M
        atip[9] = 24;   // Lead-in S
        atip[10] = 01;  // Lead-in F (ends in 1 -> Long Strategy Cyanine)
        atip[12] = 79;  // Lead-out M
        atip[13] = 59;  // Lead-out S
        atip[14] = 74;  // Lead-out F

        var info = MmcMediaIdInspector.ParseCdAtip(atip);
        Assert.NotNull(info);
        Assert.Equal("97m24s01f", info.RawManufacturerId);
        Assert.Equal("Taiyo Yuden Company Limited", info.ManufacturerName);
        Assert.Equal("ATIP", info.MediaCodeType);
        Assert.Contains("Long Strategy", info.DyeOrStrategy);
        Assert.Equal("79m59s74f", info.NominalLeadOutMsf);
        Assert.Contains("Taiyo Yuden", info.FormattedSummary);
    }

    [Fact]
    public void ParseCdAtip_MitsubishiChemicalCdRw_IdentifiesRewritableAndManufacturer()
    {
        byte[] atip = new byte[16];
        BinaryPrimitives.WriteUInt16BigEndian(atip.AsSpan(0, 2), 14);
        atip[6] = 0xC0; // Bit 7 = 1, Bit 6 = 1 (CD-RW)
        atip[8] = 97;
        atip[9] = 34;
        atip[10] = 23;

        var info = MmcMediaIdInspector.ParseCdAtip(atip);
        Assert.NotNull(info);
        Assert.Equal("97m34s23f", info.RawManufacturerId);
        Assert.Contains("Mitsubishi Chemical", info.ManufacturerName);
        Assert.Equal("Phase-Change Rewritable", info.DyeOrStrategy);
    }

    [Fact]
    public void ParseDvdPreRecordedLeadIn_Mcc03Rg20_ExtractsManufacturerId()
    {
        // Pre-pit fields are 8 bytes each after the 4-byte header: field 1 at 4, field 2 at 12, field 3 at 20,
        // field 4 at 28. The manufacturer ID is bytes 21..26 + 29..34 (what dvd+rw-mediainfo prints as Media ID).
        byte[] response = new byte[68];
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(0, 2), 66);
        response[4] = 0x01;
        response[5] = 0x40; // disc application code: physical data, not part of the ID
        response[12] = 0x02;
        "WSTRAT"u8.CopyTo(response.AsSpan(14)); // write strategy bytes, must not leak into the ID
        response[20] = 0x03;
        System.Text.Encoding.ASCII.GetBytes("MCC 03").CopyTo(response, 21);
        response[28] = 0x04;
        System.Text.Encoding.ASCII.GetBytes("RG20  ").CopyTo(response, 29);

        var info = MmcMediaIdInspector.ParseDvdPreRecordedLeadIn(response);
        Assert.NotNull(info);
        Assert.Equal("MCC 03RG20", info.RawManufacturerId);
        Assert.Contains("Mitsubishi Chemical", info.ManufacturerName);
        Assert.Equal("DVD-PreRecorded", info.MediaCodeType);
    }

    [Fact]
    public void ParseDvdPreRecordedLeadIn_RejectsShortOrMislabelledResponses()
    {
        byte[] tooShort = new byte[24];
        tooShort[4] = 0x01;
        tooShort[12] = 0x02;
        Assert.Null(MmcMediaIdInspector.ParseDvdPreRecordedLeadIn(tooShort));

        byte[] wrongFieldIds = new byte[68];
        System.Text.Encoding.ASCII.GetBytes("MCC 03").CopyTo(wrongFieldIds, 21);
        System.Text.Encoding.ASCII.GetBytes("RG20  ").CopyTo(wrongFieldIds, 29);
        Assert.Null(MmcMediaIdInspector.ParseDvdPreRecordedLeadIn(wrongFieldIds));
    }

    [Theory]
    [InlineData(97, 24, 0, "Taiyo Yuden Company Limited")]
    [InlineData(97, 24, 1, "SONY Corporation")]
    [InlineData(97, 15, 2, "Mitsubishi Chemical Corporation")]
    [InlineData(97, 22, 1, "Seantram Technology Inc.")]
    [InlineData(97, 26, 6, "CMC Magnetics Corporation")]
    [InlineData(97, 34, 2, "Mitsubishi Chemical Corporation")]
    [InlineData(97, 99, 9, "Unknown Manufacturer")]
    public void LookupCdAtipManufacturer_MatchesOrangeBookAssignments(int m, int s, int frameTens, string expected)
    {
        Assert.Equal(expected, MmcMediaIdInspector.LookupCdAtipManufacturer(m, s, frameTens));
    }

    [Fact]
    public void ParseDvdAdip_Yuden000T02_ExtractsVendorMediaTypeAndRevision()
    {
        byte[] response = new byte[40];
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(0, 2), 36);
        System.Text.Encoding.ASCII.GetBytes("YUDEN000").CopyTo(response, 23);
        System.Text.Encoding.ASCII.GetBytes("T02").CopyTo(response, 31);
        response[34] = 0x02;

        var info = MmcMediaIdInspector.ParseDvdAdip(response);
        Assert.NotNull(info);
        Assert.Equal("YUDEN000-T02-02", info.RawManufacturerId);
        Assert.Equal("Taiyo Yuden (JVC)", info.ManufacturerName);
        Assert.Equal("ADIP", info.MediaCodeType);
    }

    [Fact]
    public void ParseBdDiscInfo_MillenniataMDisc_ExtractsManufacturerAndType()
    {
        byte[] response = new byte[120];
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(0, 2), 116);
        response[4] = (byte)'D';
        response[5] = (byte)'I';
        System.Text.Encoding.ASCII.GetBytes("MILLEN").CopyTo(response, 104);
        System.Text.Encoding.ASCII.GetBytes("MR1").CopyTo(response, 110);

        var info = MmcMediaIdInspector.ParseBdDiscInfo(response);
        Assert.NotNull(info);
        Assert.Equal("MILLEN-MR1", info.RawManufacturerId);
        Assert.Equal("Millenniata (M-DISC)", info.ManufacturerName);
        Assert.Equal("BD-DI", info.MediaCodeType);
    }

    #endregion
}
