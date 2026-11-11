using System.Buffers.Binary;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Burning;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Scsi;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// Direct writing of data images to DVD-R, DVD-RW and DVD+R on the simulated drive. The expected command sequences come
/// from libburn's cookbook (Disc-At-Once with RESERVE TRACK for a closed DVD-R; incremental packets and CLOSE TRACK /
/// SESSION for an open one; no mode page, CLOSE TRACK and finalize for DVD+R), and the simulator refuses anything else.
/// </summary>
public sealed class NativeDvdDataWriterTests : IDisposable
{
    private const int DvdCapacity = OpticalConstants.DvdMinusR5Sectors;
    private const int Speed8x = 11_080; // kB/s, as DVD drives list 8x
    private const int Speed4x = 5_540;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cdburner-test-" + Guid.NewGuid().ToString("N"));

    public NativeDvdDataWriterTests() => Directory.CreateDirectory(_dir);

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

    private static byte[] Image(int sectors, int seed = 11)
    {
        byte[] data = new byte[sectors * 2048];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>A GET PERFORMANCE type 03h reply listing these write speeds (kB/s).</summary>
    private static byte[] Speeds(params int[] kiloBytesPerSecond)
    {
        byte[] r = new byte[8 + 16 * kiloBytesPerSecond.Length];
        BinaryPrimitives.WriteUInt32BigEndian(r, (uint)(r.Length - 4));
        for (int i = 0; i < kiloBytesPerSecond.Length; i++)
        {
            var d = r.AsSpan(8 + 16 * i, 16);
            BinaryPrimitives.WriteUInt32BigEndian(d[4..], DvdCapacity - 1);
            BinaryPrimitives.WriteUInt32BigEndian(d[8..], (uint)kiloBytesPerSecond[i]);
            BinaryPrimitives.WriteUInt32BigEndian(d[12..], (uint)kiloBytesPerSecond[i]);
        }
        return r;
    }

    private static VirtualMmcDrive BlankDvd(ushort profile = NativeTaoDataWriter.ProfileDvdMinusR) => new()
    {
        Profile = profile,
        CapacitySectors = DvdCapacity,
        WriteSpeedPerformanceResponse = Speeds(Speed8x, Speed4x)
    };

    private static NativeDataWriteResult Write(VirtualMmcDrive drive, byte[] image, bool testWrite = false, bool closeDisc = true, int? speed = null)
    {
        var writer = new NativeTaoDataWriter(drive, sleep: _ => { });
        return writer.Write(new NativeDataWriteRequest(new MemoryStream(image), 0, image.Length, testWrite, closeDisc, speed));
    }

    private static void AssertRecorded(VirtualMmcDrive drive, byte[] image, int paddedSectors)
    {
        Assert.NotNull(drive.DataDiscImage);
        Assert.Equal(paddedSectors * 2048, drive.DataDiscImage!.Length);
        Assert.True(drive.DataDiscImage.AsSpan(0, image.Length).SequenceEqual(image));
        Assert.True(drive.DataDiscImage.AsSpan(image.Length).IndexOfAnyExcept((byte)0) < 0, "padding must be zeros");
    }

    // ---- DVD-R / DVD-RW ----

    [Theory]
    [InlineData(NativeTaoDataWriter.ProfileDvdMinusR)]
    [InlineData(NativeTaoDataWriter.ProfileDvdMinusRwSequential)]
    public void ClosedDvdMinusR_IsWrittenDiscAtOnce_WithAReservationAndNoCloseCommand(ushort profile)
    {
        var drive = BlankDvd(profile);
        byte[] image = Image(1000); // not a whole number of 16-sector blocks

        var result = Write(drive, image);

        Assert.True(result.Success, result.Message);
        Assert.True(result.DiscRecorded);
        Assert.True(result.IsDvd);
        Assert.StartsWith("disc-at-once", result.WriteMode);
        Assert.Equal(1000, result.DataSectorsWritten);
        Assert.Equal(8, result.PaddingSectors);
        AssertRecorded(drive, image, 1008);
        Assert.Equal(2, drive.DiscStatus);
        Assert.Contains(NativeWriteCommands.OpCodeReserveTrack, drive.CommandsReceived);
        Assert.DoesNotContain(NativeWriteCommands.OpCodeCloseTrackSession, drive.CommandsReceived);

        var page = WriteParametersPage.FromPageBytes(drive.CurrentModePage05);
        Assert.Equal(WriteParametersPage.WriteTypeSessionAtOnce, page.WriteType);
        Assert.Equal(WriteParametersPage.TrackModeDvd, page.TrackMode);
        Assert.Equal(WriteParametersPage.DataBlockTypeMode1, page.DataBlockType);
        Assert.Equal(0, page.MultiSession);
        Assert.False(page.FixedPackets);
    }

    [Fact]
    public void Speed_IsTheFastestListed_SetWithSetStreamingOverTheWholeDisc()
    {
        var drive = BlankDvd();

        var result = Write(drive, Image(320));

        Assert.Equal(Speed8x, result.SpeedSetKiloBytesPerSecond);
        Assert.Equal(Speed8x, drive.WriteSpeedSet);
        Assert.Equal(new[] { Speed8x, Speed4x }, result.DriveSpeedsKiloBytesPerSecond);
        Assert.DoesNotContain(NativeWriteCommands.OpCodeSetCdSpeed, drive.CommandsReceived);
        var d = drive.LastPerformanceDescriptor!;
        Assert.Equal(0, BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(4)));            // start LBA
        Assert.Equal(DvdCapacity - 1, BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(8))); // end LBA
        Assert.Equal(Speed8x, BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(20)));      // write size (kB)
        Assert.Equal(1000, BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(24)));         // write time (ms)
        Assert.Equal(1385.0, result.OneXKiloBytesPerSecond);
    }

    [Fact]
    public void ChosenSpeed_IsRoundedDownToOneTheDriveLists()
    {
        var drive = BlankDvd();
        var result = Write(drive, Image(320), speed: 8000);

        Assert.True(result.Success, result.Message);
        Assert.Equal(Speed4x, drive.WriteSpeedSet);
    }

    [Fact]
    public void SetStreamingRefused_FallsBackToSetCdSpeed()
    {
        var drive = BlankDvd();
        drive.RejectsSetStreaming = true;

        var result = Write(drive, Image(320));

        Assert.True(result.Success, result.Message);
        Assert.Contains(NativeWriteCommands.OpCodeSetCdSpeed, drive.CommandsReceived);
        Assert.Equal(Speed8x, drive.WriteSpeedSet);
    }

    [Fact]
    public void OpenDvdMinusR_IsWrittenIncrementally_ThenTheTrackAndSessionAreClosed()
    {
        var drive = BlankDvd();
        byte[] image = Image(512);

        var result = Write(drive, image, closeDisc: false);

        Assert.True(result.Success, result.Message);
        Assert.StartsWith("incremental", result.WriteMode);
        AssertRecorded(drive, image, 512);
        Assert.Equal(1, drive.DiscStatus); // still appendable
        Assert.DoesNotContain(NativeWriteCommands.OpCodeReserveTrack, drive.CommandsReceived);
        Assert.Equal(NativeWriteCommands.CloseFunctionSession, drive.LastCloseFunction);

        var page = WriteParametersPage.FromPageBytes(drive.CurrentModePage05);
        Assert.Equal(WriteParametersPage.WriteTypeIncremental, page.WriteType);
        Assert.True(page.FixedPackets);
        Assert.Equal(16, page.PacketSize);
        Assert.Equal(3, page.MultiSession);
    }

    [Fact]
    public void OpenDvdMinusR_OnADriveWithoutIncrementalWriting_StopsBeforeWriting()
    {
        var drive = BlankDvd();
        drive.OffersIncremental = false;

        var result = Write(drive, Image(320), closeDisc: false);

        Assert.False(result.Success);
        Assert.True(result.StoppedBeforeWriting);
        Assert.Contains("incremental", result.Message);
        Assert.Equal(0, drive.WriteCommandCount);
    }

    [Fact]
    public void TestWrite_SendsEverySectorButRecordsNothing()
    {
        var drive = BlankDvd();

        var result = Write(drive, Image(640), testWrite: true);

        Assert.True(result.Success, result.Message);
        Assert.True(result.DiscStillBlankAfterTestWrite);
        Assert.False(result.DiscRecorded);
        Assert.Equal(20, drive.WriteCommandCount); // 640 sectors, 32 per WRITE
        Assert.Null(drive.DataDiscImage);
        Assert.True(WriteParametersPage.FromPageBytes(drive.CurrentModePage05).TestWrite);
        Assert.Equal(2, drive.DiscStatus); // the simulated closed disc, until a reload
        Assert.Contains("reload", result.Message);
    }

    [Fact]
    public void TestWrite_OnADriveThatForgetsIt_LeavesTheDiscBlank()
    {
        var drive = BlankDvd();
        drive.ShowsSimulatedSessionAfterTestWrite = false;

        var result = Write(drive, Image(320), testWrite: true);

        Assert.True(result.Success, result.Message);
        Assert.Equal(0, drive.DiscStatus);
        Assert.DoesNotContain("reload", result.Message);
    }

    [Fact]
    public void TestWrite_OnADriveThatRecordsAnyway_IsReported()
    {
        var drive = BlankDvd();
        drive.RecordsDuringTestWrite = true;

        var result = Write(drive, Image(320), testWrite: true);

        Assert.False(result.Success);
        Assert.Equal(false, result.DiscStillBlankAfterTestWrite);
        Assert.Contains("did not honour", result.Message);
    }

    [Fact]
    public void AdapterThatRefuses64KiB_GetsOneBlockPerWrite()
    {
        var drive = BlankDvd(NativeTaoDataWriter.ProfileDvdPlusR);
        drive.MaxTransferBytesOverride = 32 * 1024;
        byte[] image = Image(640);

        var result = Write(drive, image);

        Assert.True(result.Success, result.Message);
        Assert.Equal(40, drive.WriteCommandCount);
        AssertRecorded(drive, image, 640);
    }

    [Fact]
    public void TestWriteOnADriveWithoutOne_IsReportedAsNotOffered()
    {
        var drive = BlankDvd();
        drive.OffersTestWrite = false;

        var result = Write(drive, Image(320), testWrite: true);

        Assert.True(result.TestWriteNotOffered);
        Assert.True(result.StoppedBeforeWriting);
        Assert.Equal(0, drive.WriteCommandCount);
    }

    [Fact]
    public void ReservationRefused_StopsBeforeWriting()
    {
        var drive = BlankDvd();
        drive.RejectsReserveTrack = true;

        var result = Write(drive, Image(320));

        Assert.False(result.Success);
        Assert.True(result.StoppedBeforeWriting);
        Assert.Contains("reserve", result.Message);
        Assert.Equal(0, drive.WriteCommandCount);
    }

    [Fact]
    public void WriteErrorAfterTheReservation_IsNotReportedAsUntouched()
    {
        var drive = BlankDvd();
        drive.WriteErrorAtLba = 100;

        var result = Write(drive, Image(320));

        Assert.False(result.Success);
        Assert.False(result.StoppedBeforeWriting);
    }

    [Fact]
    public void ImageTooLargeForTheDisc_StopsBeforeWriting()
    {
        var drive = BlankDvd();
        drive.CapacitySectors = 500;

        var result = Write(drive, Image(600));

        Assert.True(result.StoppedBeforeWriting);
        Assert.Contains("does not fit", result.Message);
        Assert.Equal(0, drive.WriteCommandCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void DiscThatIsNotBlank_IsNotTouched(int status)
    {
        var drive = BlankDvd();
        drive.DiscStatus = status;

        var result = Write(drive, Image(320));

        Assert.True(result.StoppedBeforeWriting);
        Assert.DoesNotContain(NativeWriteCommands.OpCodeModeSelect10, drive.CommandsReceived);
        Assert.Equal(0, drive.WriteCommandCount);
    }

    // ---- DVD+R ----

    [Fact]
    public void ClosedDvdPlusR_GetsNoModePage_ThenTheTrackIsClosedAndTheDiscFinalized()
    {
        var drive = BlankDvd(NativeTaoDataWriter.ProfileDvdPlusR);
        byte[] image = Image(777);

        var result = Write(drive, image);

        Assert.True(result.Success, result.Message);
        Assert.Equal("sequential (DVD+R)", result.WriteMode);
        AssertRecorded(drive, image, 784);
        Assert.DoesNotContain(NativeWriteCommands.OpCodeModeSelect10, drive.CommandsReceived);
        Assert.DoesNotContain(NativeWriteCommands.OpCodeReserveTrack, drive.CommandsReceived);
        Assert.Equal(NativeWriteCommands.CloseFunctionFinalizeDvdPlusR, drive.LastCloseFunction);
        Assert.Equal(2, drive.DiscStatus);
    }

    [Fact]
    public void OpenDvdPlusR_HasItsSessionClosedAndStaysAppendable()
    {
        var drive = BlankDvd(NativeTaoDataWriter.ProfileDvdPlusR);

        var result = Write(drive, Image(320), closeDisc: false);

        Assert.True(result.Success, result.Message);
        Assert.Equal(NativeWriteCommands.CloseFunctionSession, drive.LastCloseFunction);
        Assert.Equal(1, drive.DiscStatus);
    }

    [Fact]
    public void FinalizeRefused_ClosesTheSessionInstead_AndSaysSo()
    {
        var drive = BlankDvd(NativeTaoDataWriter.ProfileDvdPlusR);
        drive.RejectsFinalize = true;

        var result = Write(drive, Image(320));

        Assert.True(result.Success, result.Message);
        Assert.Contains("would not finalize", result.Message);
        Assert.Equal(NativeWriteCommands.CloseFunctionSession, drive.LastCloseFunction);
        Assert.Equal(1, drive.DiscStatus);
        Assert.NotNull(drive.DataDiscImage);
    }

    [Fact]
    public void BusyAtCloseSession_IsWaitedOut()
    {
        var drive = BlankDvd(NativeTaoDataWriter.ProfileDvdPlusR);
        drive.CloseSessionBusyResponses = 2;

        var result = Write(drive, Image(320), closeDisc: false);

        Assert.True(result.Success, result.Message);
        Assert.Equal(1, drive.DiscStatus);
    }

    [Fact]
    public void DvdPlusR_HasNoTestWrite()
    {
        var drive = BlankDvd(NativeTaoDataWriter.ProfileDvdPlusR);

        var result = Write(drive, Image(320), testWrite: true);

        Assert.True(result.TestWriteNotOffered);
        Assert.True(result.StoppedBeforeWriting);
        Assert.Equal(0, drive.WriteCommandCount);
    }

    // ---- Discs not covered ----

    [Theory]
    [InlineData(NativeTaoDataWriter.ProfileDvdPlusRDualLayer)]
    [InlineData(NativeTaoDataWriter.ProfileDvdMinusRDualLayer)]
    [InlineData(NativeTaoDataWriter.ProfileDvdMinusRDualLayerJump)]
    public void DualLayerDisc_IsReportedAsNotCovered(ushort profile)
    {
        var drive = BlankDvd(profile);

        var result = Write(drive, Image(320));

        Assert.True(result.UnsupportedDisc);
        Assert.True(result.StoppedBeforeWriting);
        Assert.Contains("dual-layer", result.Message);
        Assert.Equal(0, drive.WriteCommandCount);
    }

    // ---- Command builders ----

    [Fact]
    public void ReserveTrack_PutsTheSizeInBytes5To8()
    {
        byte[] cdb = NativeWriteCommands.BuildReserveTrack(0x01020304);
        Assert.Equal(new byte[] { 0x53, 0, 0, 0, 0, 0x01, 0x02, 0x03, 0x04, 0 }, cdb);
    }

    [Fact]
    public void SetStreaming_CarriesTheDescriptorLengthInBytes9And10()
    {
        byte[] cdb = NativeWriteCommands.BuildSetStreaming();
        Assert.Equal(12, cdb.Length);
        Assert.Equal(0xB6, cdb[0]);
        Assert.Equal(0, cdb[8]); // type 0: performance descriptor
        Assert.Equal(28, BinaryPrimitives.ReadUInt16BigEndian(cdb.AsSpan(9)));
    }

    // ---- Engine, through DataBurnService with its read-back check ----

    private (NativeDataBurnEngine Engine, Func<VirtualMmcDrive?> Drive) Engine(Action<VirtualMmcDrive>? configure = null)
    {
        VirtualMmcDrive? seen = null;
        var engine = new NativeDataBurnEngine(logDirectory: _dir)
        {
            ConfigureVirtualDrive = d =>
            {
                seen = d;
                d.WriteSpeedPerformanceResponse = Speeds(Speed8x, Speed4x);
                configure?.Invoke(d);
            }
        };
        return (engine, () => seen);
    }

    private static VirtualDiscRecorder Recorder(VirtualDiscMedium medium)
    {
        var recorder = new VirtualDiscRecorder();
        recorder.InsertMedia(medium);
        return recorder;
    }

    public static TheoryData<DiscMediaType> CoveredDvds => new() { DiscMediaType.DvdMinusR, DiscMediaType.DvdMinusRw, DiscMediaType.DvdPlusR };

    [Theory]
    [MemberData(nameof(CoveredDvds))]
    public async Task BurnedImage_IsWrittenDirectlyAndReadBackCorrectly(DiscMediaType type)
    {
        var (engine, drive) = Engine();
        using var medium = new VirtualDiscMedium(type, OpticalConstants.DvdPlusR5Sectors);
        var recorder = Recorder(medium);
        byte[] image = Image(2000);

        var result = await new DataBurnService(engine).BurnStreamAsync(recorder, new MemoryStream(image), new DataBurnOptions { VerifyAfterBurn = true });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.Verified);
        Assert.Null(engine.LastFallbackReason);
        Assert.NotNull(drive());
        Assert.True(engine.LastResult!.IsDvd);
        Assert.Contains("at 8x", engine.LastSummary);
        byte[] onDisc = new byte[image.Length];
        medium.ReadSectors(0, 2000, onDisc, 2048);
        Assert.True(onDisc.SequenceEqual(image));
        Assert.True(medium.IsFinalized);
    }

    [Fact]
    public async Task ChosenDvdSpeed_IsConvertedWithTheDvdBaseRate()
    {
        var (engine, drive) = Engine();
        using var medium = VirtualDiscMedium.CreateBlankDvdR47();
        var recorder = Recorder(medium);

        var options = new DataBurnOptions { VerifyAfterBurn = true, Speed = WriteSpeedDescriptor.FromMultiplier(4, DiscMediaType.DvdMinusR) };
        var result = await new DataBurnService(engine).BurnStreamAsync(recorder, new MemoryStream(Image(320)), options);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(Speed4x, drive()!.WriteSpeedSet);
    }

    [Fact]
    public async Task DualLayerDvd_IsHandedToTheWindowsWriter()
    {
        var (engine, drive) = Engine();
        using var medium = VirtualDiscMedium.CreateBlankDvdPlusRDL85();
        var recorder = Recorder(medium);

        var result = await new DataBurnService(engine).BurnStreamAsync(recorder, new MemoryStream(Image(320)), new DataBurnOptions { VerifyAfterBurn = true });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.Verified);
        Assert.Contains("dual-layer", engine.LastFallbackReason);
        Assert.Null(drive());
    }
}
