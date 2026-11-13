using System.Buffers.Binary;
using System.Text;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Burning;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// Direct writing to the overwritable DVDs (DVD+RW, DVD-RW in restricted overwrite mode, DVD-RAM) on the simulated drive. Expected behaviour from libburn's cookbook and mmc.c: FORMAT UNIT
/// type 26h (0xFFFFFFFF blocks, FOV and Immed) only on a never-formatted disc, no mode page and no test write, WRITE
/// (10) from LBA 0 on 2 KiB boundaries, SYNCHRONIZE CACHE, then CLOSE SESSION (010b). A disc holding a file system
/// is only written over with leave to replace its data.
/// </summary>
public sealed class NativeOverwritableDvdWriterTests : IDisposable
{
    private const int Capacity = OpticalConstants.DvdPlusR5Sectors;
    private const int Speed4x = 5_540;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cdburner-test-" + Guid.NewGuid().ToString("N"));

    public NativeOverwritableDvdWriterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
        }
    }

    private static byte[] Image(int sectors, int seed = 21)
    {
        byte[] data = new byte[sectors * 2048];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>An image whose sector 16 carries an ISO 9660 volume descriptor, like a real data disc.</summary>
    private static byte[] IsoLikeImage(int sectors, int seed)
    {
        byte[] data = Image(sectors, seed);
        data[16 * 2048] = 1;
        Encoding.ASCII.GetBytes("CD001").CopyTo(data, 16 * 2048 + 1);
        return data;
    }

    private static byte[] Speeds(params int[] kiloBytesPerSecond)
    {
        byte[] r = new byte[8 + 16 * kiloBytesPerSecond.Length];
        BinaryPrimitives.WriteUInt32BigEndian(r, (uint)(r.Length - 4));
        for (int i = 0; i < kiloBytesPerSecond.Length; i++)
        {
            var d = r.AsSpan(8 + 16 * i, 16);
            BinaryPrimitives.WriteUInt32BigEndian(d[4..], Capacity - 1);
            BinaryPrimitives.WriteUInt32BigEndian(d[8..], (uint)kiloBytesPerSecond[i]);
            BinaryPrimitives.WriteUInt32BigEndian(d[12..], (uint)kiloBytesPerSecond[i]);
        }
        return r;
    }

    /// <summary>A DVD+RW: never formatted (status 0), partly formatted (1) or formatted (3).</summary>
    private static VirtualMmcDrive DvdPlusRw(int backgroundFormat) => new()
    {
        Profile = NativeTaoDataWriter.ProfileDvdPlusRw,
        CapacitySectors = Capacity,
        BackgroundFormatStatus = backgroundFormat,
        FormatCapacitiesResponse = VirtualMmcDrive.BuildDvdPlusRwFormatCapacities(Capacity, formatted: backgroundFormat != 0),
        WriteSpeedPerformanceResponse = Speeds(Speed4x)
    };

    private static NativeDataWriteResult Write(VirtualMmcDrive drive, byte[] image, bool overwrite = false, bool testWrite = false)
    {
        var writer = new NativeTaoDataWriter(drive, sleep: _ => { });
        return writer.Write(new NativeDataWriteRequest(new MemoryStream(image), 0, image.Length, testWrite, OverwriteExistingData: overwrite));
    }

    // ---- Writer ----

    [Fact]
    public void NewDisc_IsFormattedInTheBackground_ThenWrittenFromSector0_AndTheSessionClosed()
    {
        var drive = DvdPlusRw(backgroundFormat: 0);
        byte[] image = Image(1000); // not a whole number of 16-sector blocks: DVD+RW needs only 2 KiB alignment

        var result = Write(drive, image);

        Assert.True(result.Success, result.Message);
        Assert.True(result.DiscRecorded);
        Assert.Equal("overwrite (DVD+RW)", result.WriteMode);
        Assert.Equal(0, result.PaddingSectors);
        Assert.Contains("formatted", result.Message);
        Assert.Equal(1, drive.FormatUnitCount);
        Assert.Equal(new byte[] { 0x00, 0x82, 0x00, 0x08, 0xFF, 0xFF, 0xFF, 0xFF, 0x98, 0x00, 0x00, 0x00 }, drive.LastFormatParameters);
        Assert.Equal(image, drive.DataDiscImage);
        Assert.Equal(NativeWriteCommands.CloseFunctionSession, drive.LastCloseFunction);
        Assert.Equal(1, drive.BackgroundFormatStatus); // the close stopped the background format
        Assert.DoesNotContain(NativeWriteCommands.OpCodeModeSelect10, drive.CommandsReceived);
        Assert.DoesNotContain(NativeWriteCommands.OpCodeReserveTrack, drive.CommandsReceived);
        Assert.Equal(Speed4x, drive.WriteSpeedSet);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void FormattedOrPartlyFormattedBlankDisc_IsWrittenWithoutFormatting(int backgroundFormat)
    {
        var drive = DvdPlusRw(backgroundFormat);
        byte[] image = Image(640);

        var result = Write(drive, image);

        Assert.True(result.Success, result.Message);
        Assert.Equal(0, drive.FormatUnitCount);
        Assert.Equal(image, drive.DataDiscImage);
    }

    [Fact]
    public void DiscWithAFileSystem_IsLeftAlone_WithoutLeaveToReplaceIt()
    {
        var drive = DvdPlusRw(backgroundFormat: 3);
        byte[] old = IsoLikeImage(400, seed: 1);
        drive.DataDiscImage = (byte[])old.Clone();

        var result = Write(drive, Image(320));

        Assert.False(result.Success);
        Assert.True(result.StoppedBeforeWriting);
        Assert.Contains("CD001", result.Message);
        Assert.Contains("--overwrite", result.Message);
        Assert.Equal(0, drive.WriteCommandCount);
        Assert.Equal(old, drive.DataDiscImage);
    }

    [Fact]
    public void DiscWithAFileSystem_IsWrittenOver_WithLeaveToReplaceIt()
    {
        var drive = DvdPlusRw(backgroundFormat: 3);
        byte[] old = IsoLikeImage(400, seed: 1);
        drive.DataDiscImage = (byte[])old.Clone();
        byte[] image = IsoLikeImage(320, seed: 2);

        var result = Write(drive, image, overwrite: true);

        Assert.True(result.Success, result.Message);
        Assert.True(drive.DataDiscImage!.AsSpan(0, image.Length).SequenceEqual(image));
        // Past the new image the old sectors remain, as on a real overwritten DVD+RW; the new file system ends earlier.
        Assert.True(drive.DataDiscImage.AsSpan(image.Length).SequenceEqual(old.AsSpan(image.Length)));
    }

    [Fact]
    public void TestWrite_IsNotOffered()
    {
        var drive = DvdPlusRw(backgroundFormat: 0);

        var result = Write(drive, Image(320), testWrite: true);

        Assert.True(result.TestWriteNotOffered);
        Assert.True(result.StoppedBeforeWriting);
        Assert.Equal(0, drive.FormatUnitCount);
        Assert.Equal(0, drive.WriteCommandCount);
    }

    [Fact]
    public void FormatRefused_StopsWithTheDiscUntouched()
    {
        var drive = DvdPlusRw(backgroundFormat: 0);
        drive.RejectsFormatUnit = true;

        var result = Write(drive, Image(320));

        Assert.False(result.Success);
        Assert.True(result.StoppedBeforeWriting);
        Assert.Contains("format", result.Message);
        Assert.Equal(0, drive.WriteCommandCount);
    }

    [Fact]
    public void ImageTooLarge_StopsBeforeFormattingOrWriting()
    {
        var drive = DvdPlusRw(backgroundFormat: 0);
        drive.FormatCapacitiesResponse = VirtualMmcDrive.BuildDvdPlusRwFormatCapacities(500, formatted: false);

        var result = Write(drive, Image(600));

        Assert.True(result.StoppedBeforeWriting);
        Assert.Contains("does not fit", result.Message);
        Assert.Equal(0, drive.FormatUnitCount);
        Assert.Equal(0, drive.WriteCommandCount);
    }

    [Fact]
    public void CloseRefused_StillReportsTheWrittenData()
    {
        var drive = DvdPlusRw(backgroundFormat: 3);
        drive.RejectsCloseSession = true;
        byte[] image = Image(320);

        var result = Write(drive, image);

        Assert.True(result.Success, result.Message);
        Assert.Contains("refused to close", result.Message);
        Assert.Equal(image, drive.DataDiscImage);
    }

    [Fact]
    public void FormatParameters_FollowLibburnsLayout()
    {
        Assert.Equal(new byte[] { 0x04, 0x11, 0, 0, 0, 0 }, NativeWriteCommands.BuildFormatUnit());
        byte[] restart = NativeWriteCommands.BuildDvdPlusRwFormatParameters(restart: true);
        Assert.Equal(1, restart[11]);
        Assert.Equal(0x26, restart[8] >> 2);
    }

    // ---- Engine, through DataBurnService with its read-back check ----

    private NativeDataBurnEngine Engine() => new(logDirectory: _dir)
    {
        ConfigureVirtualDrive = d => d.WriteSpeedPerformanceResponse = Speeds(Speed4x)
    };

    private static VirtualDiscRecorder Recorder(VirtualDiscMedium medium)
    {
        var recorder = new VirtualDiscRecorder();
        recorder.InsertMedia(medium);
        return recorder;
    }

    private static byte[] OnDisc(VirtualDiscMedium medium, int sectors)
    {
        byte[] data = new byte[sectors * 2048];
        medium.ReadSectors(0, sectors, data, 2048);
        return data;
    }

    [Fact]
    public async Task NewDvdPlusRw_IsWrittenDirectly_ThenWrittenOverWithLeave_AndRefusedWithout()
    {
        var engine = Engine();
        using var medium = VirtualDiscMedium.CreateBlankDvdPlusRw47();
        var recorder = Recorder(medium);
        var service = new DataBurnService(engine);

        byte[] first = IsoLikeImage(1200, seed: 3);
        var r1 = await service.BurnStreamAsync(recorder, new MemoryStream(first), new DataBurnOptions { VerifyAfterBurn = true });
        Assert.True(r1.Success, r1.ErrorMessage);
        Assert.True(r1.Verified);
        Assert.Null(engine.LastFallbackReason);
        Assert.Contains("formatted", engine.LastResult!.Message);
        Assert.True(OnDisc(medium, 1200).SequenceEqual(first));

        // The disc now holds data: without leave to replace it nothing is written.
        var refused = await service.BurnStreamAsync(recorder, new MemoryStream(Image(400, seed: 4)), new DataBurnOptions { VerifyAfterBurn = true });
        Assert.False(refused.Success);
        Assert.True(OnDisc(medium, 1200).SequenceEqual(first));

        byte[] second = IsoLikeImage(800, seed: 5);
        var r2 = await service.BurnStreamAsync(recorder, new MemoryStream(second),
            new DataBurnOptions { VerifyAfterBurn = true, OverwriteExistingData = true });
        Assert.True(r2.Success, r2.ErrorMessage);
        Assert.True(r2.Verified);
        Assert.Null(engine.LastFallbackReason);
        Assert.DoesNotContain("formatted and", engine.LastResult!.Message); // formatted only the first time
        Assert.True(OnDisc(medium, 800).SequenceEqual(second));
    }

    [Fact]
    public async Task WindowsWriter_ReplacingADvdPlusRw_WritesAndVerifiesFromSector0()
    {
        using var medium = VirtualDiscMedium.CreateBlankDvdPlusRw47();
        var recorder = Recorder(medium);
        var service = new DataBurnService(new CdBurner.Hardware.Burning.CompositeBurnEngine());
        Assert.True((await service.BurnStreamAsync(recorder, new MemoryStream(IsoLikeImage(1200, seed: 6)), new DataBurnOptions())).Success);

        byte[] second = IsoLikeImage(800, seed: 7);
        var result = await service.BurnStreamAsync(recorder, new MemoryStream(second),
            new DataBurnOptions { VerifyAfterBurn = true, OverwriteExistingData = true });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.Verified);
        Assert.True(OnDisc(medium, 800).SequenceEqual(second)); // not appended after the old data
    }

    // ---- DVD-RW in restricted overwrite mode ----

    private static VirtualMmcDrive RestrictedOverwrite(VirtualMmcDrive.RestrictedOverwriteState state, byte[]? formatCapacities) => new()
    {
        Profile = NativeTaoDataWriter.ProfileDvdMinusRwRestricted,
        CapacitySectors = Capacity,
        OverwriteState = state,
        FormatCapacitiesResponse = formatCapacities,
        WriteSpeedPerformanceResponse = Speeds(Speed4x)
    };

    [Fact]
    public void FormattedDvdMinusRw_IsWrittenFrom0InWhole32KiBBlocks_AndTheSessionClosed()
    {
        var drive = RestrictedOverwrite(VirtualMmcDrive.RestrictedOverwriteState.Formatted, VirtualMmcDrive.BuildFormatCapacities(Capacity, formatted: true));
        byte[] image = Image(1000);

        var result = Write(drive, image);

        Assert.True(result.Success, result.Message);
        Assert.Equal("restricted overwrite (DVD-RW)", result.WriteMode);
        Assert.Equal(8, result.PaddingSectors);
        Assert.Equal(1008 * 2048, drive.DataDiscImage!.Length);
        Assert.True(drive.DataDiscImage.AsSpan(0, image.Length).SequenceEqual(image));
        Assert.True(drive.DataDiscImage.AsSpan(image.Length).IndexOfAnyExcept((byte)0) < 0);
        Assert.Equal(0, drive.FormatUnitCount);
        Assert.Equal(NativeWriteCommands.CloseFunctionSession, drive.LastCloseFunction);
        Assert.DoesNotContain(NativeWriteCommands.OpCodeModeSelect10, drive.CommandsReceived);
    }

    [Fact]
    public void PartlyFormattedDvdMinusRw_IsExpandedWithFormat13h_ThenWrittenAndClosed()
    {
        var drive = RestrictedOverwrite(VirtualMmcDrive.RestrictedOverwriteState.PartlyFormatted,
            VirtualMmcDrive.BuildFormatCapacities(1024, formatted: true, (NativeWriteCommands.FormatTypeDvdMinusRwExpand, Capacity - 1024)));
        drive.PartlyFormattedSectors = 1024;
        byte[] image = Image(3000); // more than the formatted part

        var result = Write(drive, image);

        Assert.True(result.Success, result.Message);
        Assert.Contains("expanded", result.Message);
        Assert.Equal(new byte[] { 0x00, 0x82, 0x00, 0x08, 0, 0, 0, 0, 0x4C, 0x00, 0x00, 0x10 }, drive.LastFormatParameters);
        Assert.True(drive.DataDiscImage!.AsSpan(0, image.Length).SequenceEqual(image));
        Assert.Equal(VirtualMmcDrive.RestrictedOverwriteState.Formatted, drive.OverwriteState); // the close ended the intermediate state
    }

    [Fact]
    public void IntermediateDvdMinusRw_IsWrittenInSequence_AndTheCloseEndsTheIntermediateState()
    {
        var drive = RestrictedOverwrite(VirtualMmcDrive.RestrictedOverwriteState.Intermediate, formatCapacities: null);
        byte[] image = Image(640);

        var result = Write(drive, image);

        Assert.True(result.Success, result.Message);
        Assert.Equal(0, drive.FormatUnitCount);
        Assert.Equal(image, drive.DataDiscImage);
        Assert.Equal(VirtualMmcDrive.RestrictedOverwriteState.Formatted, drive.OverwriteState);
    }

    [Fact]
    public void DvdMinusRwWithAFileSystem_IsLeftAlone_WithoutLeaveToReplaceIt()
    {
        var drive = RestrictedOverwrite(VirtualMmcDrive.RestrictedOverwriteState.Formatted, VirtualMmcDrive.BuildFormatCapacities(Capacity, formatted: true));
        byte[] old = IsoLikeImage(400, seed: 8);
        drive.DataDiscImage = (byte[])old.Clone();

        var result = Write(drive, Image(320));

        Assert.True(result.StoppedBeforeWriting);
        Assert.Contains("DVD-RW holds data", result.Message);
        Assert.Equal(old, drive.DataDiscImage);
    }

    // ---- DVD-RAM ----

    private static VirtualMmcDrive DvdRam(bool formatted) => new()
    {
        Profile = NativeTaoDataWriter.ProfileDvdRam,
        CapacitySectors = Capacity,
        RamFormatted = formatted, // READ FORMAT CAPACITIES follows this state
        WriteSpeedPerformanceResponse = Speeds(Speed4x)
    };

    /// <summary>ECMA-330 (120 mm DVD-RAM, 4.7 GB per side): 2,295,104 user sectors of 2,048 bytes.</summary>
    private const int DvdRam47Sectors = 2_295_104;

    [Fact]
    public void DvdRamFormatParameters_MatchTheMmcLayoutForFormatType00h()
    {
        // MMC-5 6.5.3 (FORMAT UNIT parameter list): header byte 1 = FOV (bit 7) | DCRT (bit 5) | Immed (bit 1), bytes 2-3
        // format descriptor length 8; descriptor bytes 0-3 number of blocks (0x00230540 = 2,295,104), byte 4 format
        // type 00h in bits 7-2 with subtype 0, bytes 5-7 type-dependent parameter = block length 2,048 (0x000800).
        byte[] expected = { 0x00, 0xA2, 0x00, 0x08, 0x00, 0x23, 0x05, 0x40, 0x00, 0x00, 0x08, 0x00 };
        Assert.Equal(expected, NativeWriteCommands.BuildDvdRamFormatParameters(DvdRam47Sectors));
    }

    [Fact]
    public void UnformattedDvdRam_ReportsItselfAsUnformatted_WithFormat00hOffered()
    {
        var drive = DvdRam(formatted: false);
        drive.CapacitySectors = DvdRam47Sectors;

        // READ FORMAT CAPACITIES (0x23), allocation length 0xFC in bytes 7-8.
        byte[] reply = new byte[252];
        var r = drive.ExecuteCommand(new byte[] { 0x23, 0, 0, 0, 0, 0, 0, 0x00, 0xFC, 0 }, reply,
            CdBurner.Hardware.Scsi.ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, TimeSpan.FromSeconds(5));

        Assert.True(r.IsSuccess);
        // MMC-5 6.24.3: list length 16; current/maximum descriptor with descriptor type 01b (unformatted media, the
        // most it can be formatted to) and block length 2,048; one formattable descriptor of type 00h whose
        // type-dependent parameter is the block length.
        byte[] expected =
        {
            0x00, 0x00, 0x00, 0x10,
            0x00, 0x23, 0x05, 0x40, 0x01, 0x00, 0x08, 0x00,
            0x00, 0x23, 0x05, 0x40, 0x00, 0x00, 0x08, 0x00
        };
        Assert.Equal(expected, reply[..20]);
    }

    [Fact]
    public void UnformattedDvdRam_IsFormatted_ThenWrittenAndReadBack()
    {
        var drive = DvdRam(formatted: false);
        drive.CapacitySectors = DvdRam47Sectors;
        drive.RamFormatBusyPolls = 4;
        byte[] image = Image(777);
        var progress = new List<BurnProgressSnapshot>();

        var writer = new NativeTaoDataWriter(drive, sleep: _ => { });
        var result = writer.Write(new NativeDataWriteRequest(new MemoryStream(image), 0, image.Length, false, true),
            new SyncProgress(progress.Add), CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Contains("formatted", result.Message);
        Assert.Equal(1, drive.FormatUnitCount);
        Assert.Equal(NativeWriteCommands.BuildDvdRamFormatParameters(DvdRam47Sectors), drive.LastFormatParameters);
        Assert.True(drive.RamFormatted);
        // Every 2/04/04 answer was waited out before the first WRITE.
        Assert.Equal(4, drive.FormatProgressSent.Count);
        Assert.Contains(progress, p => p.CurrentActionDescription == "Formatting 75 %");
        Assert.DoesNotContain(NativeWriteCommands.OpCodeCloseTrackSession, drive.CommandsReceived);

        // Read back through READ (10), as the verify after a burn does.
        byte[] back = new byte[image.Length];
        for (int lba = 0; lba < 777; lba += 16)
        {
            int count = Math.Min(16, 777 - lba);
            var r = drive.ExecuteCommand(CdBurner.Hardware.Scsi.MmcCommandBuilder.BuildRead10Cdb(lba, count, forceUnitAccess: true),
                back.AsSpan(lba * 2048, count * 2048), CdBurner.Hardware.Scsi.ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, TimeSpan.FromSeconds(5));
            Assert.True(r.IsSuccess, r.SenseInfo?.ToString());
        }
        Assert.Equal(image, back);
    }

    [Fact]
    public void DvdRamThatRefusesTheFormat_IsLeftUnwritten()
    {
        var drive = DvdRam(formatted: false);
        drive.RejectsFormatUnit = true;

        var result = Write(drive, Image(320));

        Assert.False(result.Success);
        Assert.Contains("refused to format", result.Message);
        Assert.Equal(0, drive.WriteCommandCount);
        Assert.False(drive.RamFormatted);
    }

    [Fact]
    public void SimulatedDvdRam_RefusesAFormatWithTheWrongBlockLength()
    {
        var drive = DvdRam(formatted: false);
        byte[] list = NativeWriteCommands.BuildDvdRamFormatParameters(1000);
        list[10] = 0x02; // 512-byte blocks

        var r = drive.ExecuteCommand(NativeWriteCommands.BuildFormatUnit(), list,
            CdBurner.Hardware.Scsi.ScsiPassThroughNative.SCSI_IOCTL_DATA_OUT, TimeSpan.FromSeconds(5));

        Assert.False(r.IsSuccess);
        Assert.Equal((byte)0x26, r.SenseInfo?.Asc);
        Assert.False(drive.RamFormatted);
    }

    [Fact]
    public void FormatProgress_IsReadFromTheSenseKeySpecificBytes()
    {
        // SPC-4 4.5.2.4.4: fixed sense 70h, key 2, SKSV (byte 15 bit 7), progress in bytes 16-17 of 65,536.
        byte[] sense = new byte[18];
        sense[0] = 0x70;
        sense[2] = 0x02;
        sense[7] = 10;
        sense[12] = 0x04;
        sense[13] = 0x04;
        sense[15] = 0x80;
        sense[16] = 0x40; // 0x4000 = a quarter
        Assert.Equal(0.25, NativeTaoDataWriter.FormatProgress(sense));
        sense[15] = 0;
        Assert.Null(NativeTaoDataWriter.FormatProgress(sense));
    }

    private sealed class SyncProgress(Action<BurnProgressSnapshot> report) : IProgress<BurnProgressSnapshot>
    {
        public void Report(BurnProgressSnapshot value) => report(value);
    }

    [Fact]
    public void DvdRam_IsWrittenFrom0_WithNothingToClose()
    {
        var drive = DvdRam(formatted: true);
        byte[] image = Image(777);

        var result = Write(drive, image);

        Assert.True(result.Success, result.Message);
        Assert.Equal("overwrite (DVD-RAM)", result.WriteMode);
        Assert.Equal(0, result.PaddingSectors);
        Assert.Equal(image, drive.DataDiscImage);
        Assert.DoesNotContain(NativeWriteCommands.OpCodeCloseTrackSession, drive.CommandsReceived);
        Assert.Equal(0, drive.FormatUnitCount);
    }

    [Fact]
    public void UnformattedDvdRam_WithNoFormatOffered_IsRefusedBeforeWriting()
    {
        var drive = DvdRam(formatted: false);
        drive.FormatCapacitiesResponse = VirtualMmcDrive.BuildFormatCapacities(Capacity, formatted: false);

        var result = Write(drive, Image(320));

        Assert.True(result.StoppedBeforeWriting);
        Assert.Contains("offers no format", result.Message);
        Assert.Equal(0, drive.FormatUnitCount);
        Assert.Equal(0, drive.WriteCommandCount);
    }

    [Fact]
    public async Task NeverFormattedDvdRam_IsFormattedWrittenAndVerified_ThroughTheBurnService()
    {
        VirtualMmcDrive? used = null;
        var engine = new NativeDataBurnEngine(logDirectory: _dir)
        {
            ConfigureVirtualDrive = d =>
            {
                d.WriteSpeedPerformanceResponse = Speeds(Speed4x);
                d.RamFormatted = false;
                used = d;
            }
        };
        using var medium = VirtualDiscMedium.CreateBlankDvdRam();
        var service = new DataBurnService(engine);

        byte[] image = IsoLikeImage(900, seed: 12);
        var result = await service.BurnStreamAsync(Recorder(medium), new MemoryStream(image), new DataBurnOptions { VerifyAfterBurn = true });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.Verified);
        Assert.Null(engine.LastFallbackReason);
        Assert.Equal(1, used!.FormatUnitCount);
        Assert.True(OnDisc(medium, 900).SequenceEqual(image));
    }

    [Theory]
    [InlineData(NativeTaoDataWriter.ProfileDvdRam)]
    [InlineData(NativeTaoDataWriter.ProfileDvdMinusRwRestricted)]
    public void OverwritableDvds_HaveNoTestWrite(ushort profile)
    {
        var drive = profile == NativeTaoDataWriter.ProfileDvdRam
            ? DvdRam(formatted: true)
            : RestrictedOverwrite(VirtualMmcDrive.RestrictedOverwriteState.Formatted, VirtualMmcDrive.BuildFormatCapacities(Capacity, formatted: true));

        var result = Write(drive, Image(320), testWrite: true);

        Assert.True(result.TestWriteNotOffered);
        Assert.Equal(0, drive.WriteCommandCount);
    }

    [Fact]
    public async Task DvdRam_IsWrittenDirectly_ThenWrittenOverWithLeave()
    {
        var engine = Engine();
        using var medium = VirtualDiscMedium.CreateBlankDvdRam();
        var recorder = Recorder(medium);
        var service = new DataBurnService(engine);

        byte[] first = IsoLikeImage(900, seed: 9);
        var r1 = await service.BurnStreamAsync(recorder, new MemoryStream(first), new DataBurnOptions { VerifyAfterBurn = true });
        Assert.True(r1.Success, r1.ErrorMessage);
        Assert.True(r1.Verified);
        Assert.Null(engine.LastFallbackReason);

        byte[] second = IsoLikeImage(500, seed: 10);
        var r2 = await service.BurnStreamAsync(recorder, new MemoryStream(second),
            new DataBurnOptions { VerifyAfterBurn = true, OverwriteExistingData = true });
        Assert.True(r2.Success, r2.ErrorMessage);
        Assert.True(r2.Verified);
        Assert.Null(engine.LastFallbackReason);
        Assert.True(OnDisc(medium, 500).SequenceEqual(second));
    }
}
