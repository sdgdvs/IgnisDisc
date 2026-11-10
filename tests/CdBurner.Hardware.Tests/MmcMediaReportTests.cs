using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Scsi;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>M1.6: READ FORMAT CAPACITIES and GET PERFORMANCE (write speed), parsed from captured replies.</summary>
public class MmcMediaReportTests
{
    // Captured 2026-11-10 from an HL-DT-ST GP60NB50 (RF02) with a Mitsubishi CD-RW holding a 20 MB data session.
    private static readonly byte[] Gp60CdRwFormatCapacities =
        Convert.FromHexString("00000018000027520200080000002752000008000000275240000000");
    private static readonly byte[] Gp60CdRwWriteSpeeds =
        Convert.FromHexString("00000014000000000000000000057DA9000006E4000006E4");

    [Fact]
    public void ReadFormatCapacitiesCdb_IsOpcode23WithTheAllocationLength()
    {
        byte[] cdb = MmcMediaReport.BuildReadFormatCapacitiesCdb(0x01FC);
        Assert.Equal(10, cdb.Length);
        Assert.Equal(0x23, cdb[0]);
        Assert.Equal(0x01, cdb[7]);
        Assert.Equal(0xFC, cdb[8]);
        Assert.All(new[] { 1, 2, 3, 4, 5, 6, 9 }, i => Assert.Equal(0, cdb[i]));
    }

    [Fact]
    public void GetPerformanceCdb_AsksForWriteSpeedDescriptors()
    {
        byte[] cdb = MmcMediaReport.BuildGetPerformanceWriteSpeedCdb(32);
        Assert.Equal(12, cdb.Length);
        Assert.Equal(0xAC, cdb[0]);
        Assert.Equal(0, cdb[1]);   // data type: only used by type 00h
        Assert.Equal(0, cdb[8]);
        Assert.Equal(32, cdb[9]);
        Assert.Equal(0x03, cdb[10]);
        Assert.Equal(8 + 16 * 32, MmcMediaReport.WriteSpeedBufferLength(32));
    }

    [Fact]
    public void CapturedCdRwReply_ParsesBlocksStateAndFormats()
    {
        var info = MmcMediaReport.ParseFormatCapacities(Gp60CdRwFormatCapacities)!;

        Assert.Equal(10_066, info.Blocks);
        Assert.Equal(2048, info.BlockLength);
        Assert.Equal(FormattedCapacityState.Formatted, info.State);
        Assert.Equal(10_066L * 2048, info.Bytes);
        Assert.Equal(2, info.Formats.Count);
        Assert.Equal((byte)0x00, info.Formats[0].FormatType);
        Assert.Equal(2048, info.Formats[0].TypeParameter);
        Assert.Equal((byte)0x10, info.Formats[1].FormatType); // byte 4 = 0x40: format type sits in bits 7-2
    }

    [Fact]
    public void CapturedCdRwReply_GivesOneWriteSpeedOf10x()
    {
        var speeds = MmcMediaReport.ParseWriteSpeeds(Gp60CdRwWriteSpeeds);

        var speed = Assert.Single(speeds);
        Assert.Equal(1764, speed.KilobytesPerSecond);
        Assert.Equal(1764, speed.ReadKilobytesPerSecond);
        Assert.Equal(359_849, speed.EndLba);
        Assert.False(speed.Exact);
        Assert.Equal(10.0, speed.Multiplier(DiscMediaType.CdRw));
    }

    [Fact]
    public void FormattedDvdPlusRw_ReportsItsCurrentSize()
    {
        // Built per MMC-6 §6.24: formatted DVD+RW, 2,295,104 blocks, then the DVD+RW full format (type 0x26).
        byte[] reply = Reply(Capacity(2_295_104, 0x02, 2048), Capacity(2_295_104, 0x26 << 2, 0));
        var info = MmcMediaReport.ParseFormatCapacities(reply)!;

        Assert.Equal(FormattedCapacityState.Formatted, info.State);
        Assert.Equal("formatted", info.StateName);
        Assert.Equal(OpticalConstants.DvdPlusR5Bytes, info.Bytes);
        Assert.Equal((byte)0x26, Assert.Single(info.Formats).FormatType);
    }

    [Fact]
    public void BlankBdRe_ReportsTheMaximumAndTheFormatsOnOffer()
    {
        // Unformatted BD-RE: maximum size, then format 0x30 with the default spare area and 0x30 without.
        byte[] reply = Reply(
            Capacity(12_219_392, 0x01, 2048),
            Capacity(11_826_176, 0x30 << 2, 0x003000),
            Capacity(12_219_392, 0x30 << 2, 0x000000));
        var info = MmcMediaReport.ParseFormatCapacities(reply)!;

        Assert.Equal(FormattedCapacityState.Unformatted, info.State);
        Assert.Equal("unformatted", info.StateName);
        Assert.Equal(OpticalConstants.BdR25Sectors, info.Blocks);
        Assert.Equal(2, info.Formats.Count);
        Assert.Equal(OpticalConstants.BdRe25Sectors, info.Formats[0].Blocks);
        Assert.Equal(0x003000, info.Formats[0].TypeParameter);
    }

    [Fact]
    public void NoMediaOrShortReplies_GiveNothing()
    {
        Assert.Null(MmcMediaReport.ParseFormatCapacities(Reply(Capacity(0, 0x03, 2048))));
        Assert.Null(MmcMediaReport.ParseFormatCapacities(new byte[8]));
        Assert.Null(MmcMediaReport.ParseFormatCapacities(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }));
        Assert.Empty(MmcMediaReport.ParseWriteSpeeds(new byte[4]));
        Assert.Empty(MmcMediaReport.ParseWriteSpeeds(new byte[8]));
    }

    [Fact]
    public void CapacityListLongerThanTheBuffer_StopsAtTheBuffer()
    {
        byte[] reply = Reply(Capacity(100, 0x02, 2048), Capacity(100, 0, 2048));
        reply[3] = 0xF8; // claims 31 descriptors
        var info = MmcMediaReport.ParseFormatCapacities(reply)!;
        Assert.Single(info.Formats);
    }

    [Fact]
    public void SeveralDvdSpeeds_AreDeduplicatedAndSortedFastestFirst()
    {
        byte[] reply = Speeds(
            Speed(5_540, exact: false),
            Speed(22_160, exact: true),
            Speed(11_080, exact: false),
            Speed(22_160, exact: false), // duplicate
            Speed(0, exact: false));     // empty slot
        var speeds = MmcMediaReport.ParseWriteSpeeds(reply);

        Assert.Equal(new[] { 22_160, 11_080, 5_540 }, speeds.Select(s => s.KilobytesPerSecond));
        Assert.True(speeds[0].Exact);
        Assert.Equal(new[] { 16.0, 8.0, 4.0 }, speeds.Select(s => s.Multiplier(DiscMediaType.DvdPlusR)));
        Assert.Equal(22_160_000, speeds[0].BytesPerSecond);
    }

    [Fact]
    public void BluRaySpeeds_UseTheBluRay1xRate()
    {
        var speed = Assert.Single(MmcMediaReport.ParseWriteSpeeds(Speeds(Speed(35_964, exact: false))));
        Assert.Equal(8.0, speed.Multiplier(DiscMediaType.BdR));
    }

    [Fact]
    public void DataLengthShorterThanTheBuffer_IgnoresTrailingBytes()
    {
        byte[] reply = Speeds(Speed(1764, false), Speed(3528, false));
        reply[3] = 4 + 16; // the drive says only one descriptor follows
        Assert.Single(MmcMediaReport.ParseWriteSpeeds(reply));
    }

    [Fact]
    public void Query_OverTheSimulator_SendsOnlyTheTwoReadCommands()
    {
        var drive = new VirtualMmcDrive
        {
            FormatCapacitiesResponse = Gp60CdRwFormatCapacities,
            WriteSpeedPerformanceResponse = Gp60CdRwWriteSpeeds
        };

        var report = MmcMediaReport.Query(drive);

        Assert.Equal(10_066, report.FormattedCapacity!.Blocks);
        Assert.Equal(1764, Assert.Single(report.WriteSpeeds).KilobytesPerSecond);
        Assert.Equal(new byte[] { 0x23, 0xAC }, drive.CommandsReceived);
    }

    [Fact]
    public void Query_WhenTheDriveRefusesBoth_ReturnsAnEmptyReport()
    {
        var drive = new VirtualMmcDrive(); // no canned replies: both commands get 5/20/00
        var report = MmcMediaReport.Query(drive);
        Assert.Null(report.FormattedCapacity);
        Assert.Empty(report.WriteSpeeds);
    }

    [Fact]
    public void Query_WithNoDisc_ReturnsAnEmptyReport()
    {
        var drive = new VirtualMmcDrive
        {
            MediaPresent = false,
            FormatCapacitiesResponse = Gp60CdRwFormatCapacities,
            WriteSpeedPerformanceResponse = Gp60CdRwWriteSpeeds
        };
        var report = MmcMediaReport.Query(drive);
        Assert.Null(report.FormattedCapacity);
        Assert.Empty(report.WriteSpeeds);
    }

    [Fact]
    public void CapturedCdRwSpeed_BecomesA10xPickerEntryInWindowsUnits()
    {
        var report = new DriveMediaReport(null, MmcMediaReport.ParseWriteSpeeds(Gp60CdRwWriteSpeeds));

        var entry = Assert.Single(report.ToWriteSpeedDescriptors(DiscMediaType.CdRw));

        Assert.Equal(10.0, entry.Multiplier, 1);
        Assert.Equal(DiscMediaType.CdRw, entry.MediaType);
        // Windows' writer takes 2048-byte sectors a second; CD 1x = 75 of them.
        Assert.Equal(750, entry.BytesPerSecond / OpticalConstants.DataSectorSize);
    }

    [Fact]
    public void DvdSpeeds_BecomePickerEntriesSlowestFirst()
    {
        var report = new DriveMediaReport(null, MmcMediaReport.ParseWriteSpeeds(
            Speeds(Speed(22_160, false), Speed(5_540, false), Speed(11_080, false))));

        var entries = report.ToWriteSpeedDescriptors(DiscMediaType.DvdPlusR);

        Assert.Equal(new[] { 4.0, 8.0, 16.0 }, entries.Select(e => Math.Round(e.Multiplier, 1)));
        Assert.Equal(22_160_000, entries[^1].BytesPerSecond);
    }

    [Fact]
    public void NoDiscOrNoSpeeds_GiveNoPickerEntries()
    {
        var report = new DriveMediaReport(null, MmcMediaReport.ParseWriteSpeeds(Gp60CdRwWriteSpeeds));
        Assert.Empty(report.ToWriteSpeedDescriptors(DiscMediaType.NoMedia));
        Assert.Empty(report.ToWriteSpeedDescriptors(DiscMediaType.Unknown));
        Assert.Empty(DriveMediaReport.Empty.ToWriteSpeedDescriptors(DiscMediaType.CdR));
    }

    [Theory]
    [InlineData(DiscMediaType.DvdPlusRw, true)]
    [InlineData(DiscMediaType.DvdMinusRw, true)]
    [InlineData(DiscMediaType.DvdRam, true)]
    [InlineData(DiscMediaType.BdRe, true)]
    [InlineData(DiscMediaType.BdR, true)]
    [InlineData(DiscMediaType.CdRw, false)]
    [InlineData(DiscMediaType.CdR, false)]
    [InlineData(DiscMediaType.DvdPlusR, false)]
    [InlineData(DiscMediaType.DvdMinusRDualLayer, false)]
    public void FormattedCapacity_OnlyMattersForFormattableMedia(DiscMediaType type, bool formattable)
    {
        Assert.Equal(formattable, type.IsFormattable());
    }

    private static byte[] Capacity(uint blocks, int byte4, int parameter) =>
    [
        (byte)(blocks >> 24), (byte)(blocks >> 16), (byte)(blocks >> 8), (byte)blocks,
        (byte)byte4, (byte)(parameter >> 16), (byte)(parameter >> 8), (byte)parameter
    ];

    private static byte[] Reply(params byte[][] descriptors)
    {
        var bytes = new List<byte> { 0, 0, 0, (byte)(descriptors.Length * 8) };
        foreach (var d in descriptors) bytes.AddRange(d);
        return bytes.ToArray();
    }

    private static byte[] Speed(int writeKb, bool exact)
    {
        byte[] d = new byte[16];
        d[0] = (byte)(exact ? 0x02 : 0x00);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(4), 2_295_103);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(8), writeKb);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(12), writeKb);
        return d;
    }

    private static byte[] Speeds(params byte[][] descriptors)
    {
        var bytes = new List<byte>();
        int length = 4 + descriptors.Length * 16;
        bytes.AddRange(new[] { (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length, (byte)0x02, (byte)0, (byte)0, (byte)0 });
        foreach (var d in descriptors) bytes.AddRange(d);
        return bytes.ToArray();
    }
}
