using System.Buffers.Binary;
using CdBurner.Core.Primitives;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Burning;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// Direct Track-at-Once writing of data images on the simulated drive: the command sequence and every refusal before
/// the laser (writer level), then the whole path through <see cref="DataBurnService"/> with its read-back check, the
/// hand-over to the Windows writer for discs not covered yet, and erasing a used CD-RW first (engine level).
/// </summary>
public sealed class NativeDataBurnEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cdburner-test-" + Guid.NewGuid().ToString("N"));

    public NativeDataBurnEngineTests() => Directory.CreateDirectory(_dir);

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

    private static byte[] Image(int sectors, int seed = 7, int tailBytes = 0)
    {
        byte[] data = new byte[sectors * 2048 + tailBytes];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>A GET PERFORMANCE type 03h reply listing these write speeds (kB/s), like the GP60NB50's.</summary>
    private static byte[] Speeds(params int[] kiloBytesPerSecond)
    {
        byte[] r = new byte[8 + 16 * kiloBytesPerSecond.Length];
        BinaryPrimitives.WriteUInt32BigEndian(r, (uint)(r.Length - 4));
        for (int i = 0; i < kiloBytesPerSecond.Length; i++)
        {
            var d = r.AsSpan(8 + 16 * i, 16);
            BinaryPrimitives.WriteUInt32BigEndian(d[4..], 359_849);
            BinaryPrimitives.WriteUInt32BigEndian(d[8..], (uint)kiloBytesPerSecond[i]);
            BinaryPrimitives.WriteUInt32BigEndian(d[12..], (uint)kiloBytesPerSecond[i]);
        }
        return r;
    }

    private static VirtualMmcDrive BlankCd(ushort profile = NativeSaoAudioWriter.ProfileCdRw) => new()
    {
        Profile = profile,
        WriteSpeedPerformanceResponse = Speeds(1764, 1411, 706)
    };

    private static NativeDataWriteResult Write(VirtualMmcDrive drive, byte[] image, bool testWrite = false, bool closeDisc = true,
        int? speed = null, CancellationToken ct = default)
    {
        var writer = new NativeTaoDataWriter(drive, sleep: _ => { });
        return writer.Write(new NativeDataWriteRequest(new MemoryStream(image), 0, image.Length, testWrite, closeDisc, speed), null, ct);
    }

    // ---- Writer ----

    [Fact]
    public void RealWrite_WritesOneMode1TrackAtTheDrivesFastestSpeedAndClosesTheDisc()
    {
        var drive = BlankCd();
        byte[] image = Image(1000);

        var result = Write(drive, image);

        Assert.True(result.Success, result.Message);
        Assert.True(result.DiscRecorded);
        Assert.Equal(1000, result.DataSectorsWritten);
        Assert.Equal(0, result.PaddingSectors);
        Assert.Equal(image, drive.DataDiscImage);
        Assert.Equal((1000 + 30) / 31, drive.WriteCommandCount);

        var page = WriteParametersPage.FromPageBytes(drive.CurrentModePage05);
        Assert.Equal(WriteParametersPage.WriteTypeTrackAtOnce, page.WriteType);
        Assert.Equal(WriteParametersPage.DataBlockTypeMode1, page.DataBlockType);
        Assert.Equal(WriteParametersPage.TrackModeData, page.TrackMode);
        Assert.Equal(0, page.MultiSession);
        Assert.False(page.TestWrite);

        Assert.Equal(1764, drive.WriteSpeedSet);
        Assert.Equal(1764, result.SpeedSetKiloBytesPerSecond);
        Assert.Equal(new[] { 1764, 1411, 706 }, result.DriveSpeedsKiloBytesPerSecond);
        Assert.Equal(NativeWriteCommands.CloseFunctionSession, drive.LastCloseFunction);
        Assert.Equal(2, drive.DiscStatus);
        Assert.Contains("one data track", result.Message);
    }

    [Fact]
    public void ShortImage_IsPaddedToTheMinimumTrackLength_AndAPartialLastSectorWithZeros()
    {
        var drive = BlankCd();
        byte[] image = Image(20, tailBytes: 100);

        var result = Write(drive, image);

        Assert.True(result.Success, result.Message);
        Assert.Equal(21, result.DataSectorsWritten);
        Assert.Equal(NativeTaoDataWriter.MinimumTrackSectors - 21, result.PaddingSectors);
        Assert.Equal(NativeTaoDataWriter.MinimumTrackSectors * 2048, drive.DataDiscImage!.Length);
        Assert.True(drive.DataDiscImage.AsSpan(0, image.Length).SequenceEqual(image));
        Assert.True(drive.DataDiscImage.AsSpan(image.Length).IndexOfAnyExcept((byte)0) < 0);
    }

    [Fact]
    public void NoClose_LeavesTheDiscOpenForAnotherSession()
    {
        var drive = BlankCd();
        var result = Write(drive, Image(400), closeDisc: false);

        Assert.True(result.Success, result.Message);
        Assert.Equal(3, WriteParametersPage.FromPageBytes(drive.CurrentModePage05).MultiSession);
        Assert.Equal(1, drive.DiscStatus);
        Assert.Contains("session closed", result.Message);
    }

    [Fact]
    public void TestWrite_SendsEverySectorButRecordsNothing()
    {
        var drive = BlankCd();
        var result = Write(drive, Image(400), testWrite: true);

        Assert.True(result.Success, result.Message);
        Assert.False(result.DiscRecorded);
        Assert.True(result.DiscStillBlankAfterTestWrite);
        Assert.Null(drive.DataDiscImage);
        Assert.Null(drive.LastCloseFunction);
        Assert.True(drive.WriteCommandCount > 0);
        Assert.Contains("reload", result.Message); // the GP60NB50 shows the simulated session until a reload

        drive.Reload();
        Assert.Equal(0, drive.DiscStatus);
        Assert.True(Write(drive, Image(400)).Success);
    }

    [Fact]
    public void DriveThatRecordsDuringATestWrite_IsReported()
    {
        var drive = BlankCd();
        drive.RecordsDuringTestWrite = true;
        var result = Write(drive, Image(400), testWrite: true);

        Assert.False(result.Success);
        Assert.Contains("did not honour the test-write setting", result.Message);
    }

    [Fact]
    public void TestWriteBitNotConfirmed_StopsBeforeWriting()
    {
        var drive = BlankCd();
        drive.DropsTestWriteBit = true;
        var result = Write(drive, Image(400), testWrite: true);

        Assert.False(result.Success);
        Assert.True(result.StoppedBeforeWriting);
        Assert.Equal(0, drive.WriteCommandCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void DiscThatIsNotBlank_IsNotTouched(int status)
    {
        var drive = BlankCd();
        drive.DiscStatus = status;
        var result = Write(drive, Image(400));

        Assert.True(result.StoppedBeforeWriting);
        Assert.Contains("not blank", result.Message);
        Assert.Equal(0, drive.WriteCommandCount);
    }

    [Fact]
    public void UncoveredDiscInTheDrive_IsReportedAsNotCovered()
    {
        var drive = BlankCd(profile: 0x0042); // BD-R in random recording mode: not covered
        var result = Write(drive, Image(400));

        Assert.True(result.StoppedBeforeWriting);
        Assert.True(result.UnsupportedDisc);
        Assert.Equal(0, drive.WriteCommandCount);
    }

    [Fact]
    public void ImageTooLargeForTheDisc_StopsBeforeWriting()
    {
        var drive = BlankCd();
        drive.LastLeadOutStart = Msf.FromLba(500);
        var result = Write(drive, Image(600));

        Assert.True(result.StoppedBeforeWriting);
        Assert.Contains("does not fit", result.Message);
        Assert.Equal(0, drive.WriteCommandCount);
    }

    [Fact]
    public void FirstWritableSectorNotZero_StopsBeforeWriting()
    {
        var drive = BlankCd();
        drive.NextWritableAddressOverride = 150;
        var result = Write(drive, Image(400));

        Assert.True(result.StoppedBeforeWriting);
        Assert.Equal(0, drive.WriteCommandCount);
    }

    [Fact]
    public void NoTrackInformation_UsesTheDiscsLastLeadOut()
    {
        var drive = BlankCd();
        drive.RejectsReadTrackInformation = true;
        Assert.True(Write(drive, Image(400)).Success);

        var small = BlankCd();
        small.RejectsReadTrackInformation = true;
        small.LastLeadOutStart = Msf.FromLba(350);
        Assert.True(Write(small, Image(400)).StoppedBeforeWriting);
    }

    [Fact]
    public void WriteError_FailsAfterWritingStarted()
    {
        var drive = BlankCd();
        drive.WriteErrorAtLba = 200;
        var result = Write(drive, Image(400));

        Assert.False(result.Success);
        Assert.False(result.StoppedBeforeWriting);
        Assert.Contains("LBA 186", result.Message); // the WRITE of sectors 186..216 holds sector 200
    }

    [Fact]
    public void FullDriveBuffer_IsWaitedOut()
    {
        var drive = BlankCd();
        drive.BufferFullEveryNWrites = 3;
        byte[] image = Image(500);
        var result = Write(drive, image);

        Assert.True(result.Success, result.Message);
        Assert.Equal(image, drive.DataDiscImage);
    }

    [Fact]
    public void ChosenSpeed_IsRoundedDownToOneTheDriveLists()
    {
        var drive = BlankCd();
        var result = Write(drive, Image(400), speed: 1588); // 9x: the drive lists 10x, 8x and 4x
        Assert.Equal(1411, drive.WriteSpeedSet);
        Assert.Equal(1411, result.SpeedSetKiloBytesPerSecond);

        var slow = BlankCd();
        Write(slow, Image(400), speed: 300); // below every listed speed: the slowest listed
        Assert.Equal(706, slow.WriteSpeedSet);
    }

    [Fact]
    public void SpeedRefused_KeepsTheDrivesOwnAndStillWrites()
    {
        var drive = BlankCd();
        drive.RejectsSetSpeed = true;
        var result = Write(drive, Image(400));

        Assert.True(result.Success, result.Message);
        Assert.Null(result.SpeedSetKiloBytesPerSecond);
    }

    [Fact]
    public void CloseSessionRefused_IsReportedAsUnclosed()
    {
        var drive = BlankCd();
        drive.RejectsCloseSession = true;
        var result = Write(drive, Image(400));

        Assert.False(result.Success);
        Assert.False(result.StoppedBeforeWriting);
        Assert.Contains("could not close the session", result.Message);
    }

    [Fact]
    public void CancelledBeforeWriting_WritesNothing()
    {
        var drive = BlankCd();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = Write(drive, Image(400), ct: cts.Token);

        Assert.True(result.Cancelled);
        Assert.True(result.StoppedBeforeWriting);
        Assert.Equal(0, drive.WriteCommandCount);
    }

    // ---- Engine, through DataBurnService ----

    private (NativeDataBurnEngine Engine, Func<VirtualMmcDrive?> Drive) Engine(bool testFirst = false, Action<VirtualMmcDrive>? configure = null)
    {
        VirtualMmcDrive? seen = null;
        var engine = new NativeDataBurnEngine(logDirectory: _dir)
        {
            TestWriteFirst = testFirst,
            Wait = _ => { },
            ConfigureVirtualDrive = d =>
            {
                seen = d;
                d.WriteSpeedPerformanceResponse = Speeds(1764, 1411);
                configure?.Invoke(d);
            }
        };
        return (engine, () => seen);
    }

    private static byte[] OnDisc(VirtualDiscMedium medium, int sectors)
    {
        byte[] data = new byte[sectors * 2048];
        medium.ReadSectors(0, sectors, data, 2048);
        return data;
    }

    private static VirtualDiscRecorder Recorder(VirtualDiscMedium medium)
    {
        var recorder = new VirtualDiscRecorder();
        recorder.InsertMedia(medium);
        return recorder;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BurnedImage_IsWrittenDirectlyAndReadBackCorrectly(bool rewritable)
    {
        var (engine, drive) = Engine();
        using var medium = rewritable ? VirtualDiscMedium.CreateBlankCdRw700() : VirtualDiscMedium.CreateBlankCdR700();
        var recorder = Recorder(medium);
        byte[] image = Image(900);

        var result = await new DataBurnService(engine).BurnStreamAsync(recorder, new MemoryStream(image), new DataBurnOptions { VerifyAfterBurn = true });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.Verified);
        Assert.Null(engine.LastFallbackReason);
        Assert.Equal(900, result.SectorsWritten);
        Assert.NotNull(drive());
        Assert.Equal(1764, engine.LastResult!.SpeedSetKiloBytesPerSecond);
        Assert.True(File.Exists(engine.LastLogPath));
        Assert.Contains("native data writer", File.ReadAllText(engine.LastLogPath!));
        Assert.True(OnDisc(medium, 900).SequenceEqual(image));
        Assert.True(medium.IsFinalized);
    }

    [Fact]
    public async Task DualLayerDvd_IsHandedToTheWindowsWriter_WithTheReason()
    {
        var (engine, drive) = Engine();
        using var medium = new VirtualDiscMedium(DiscMediaType.DvdMinusRDualLayer, OpticalConstants.DvdPlusR9Sectors);
        var recorder = Recorder(medium);

        var result = await new DataBurnService(engine).BurnStreamAsync(recorder, new MemoryStream(Image(400)), new DataBurnOptions { VerifyAfterBurn = true });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.Verified);
        Assert.Contains("dual-layer", engine.LastFallbackReason);
        Assert.Null(drive()); // the direct writer never opened the drive
    }

    [Fact]
    public async Task DiscWithASession_IsAppendedByTheWindowsWriter_NeverErased()
    {
        // The image for a second session is built to sit after the first one; erasing and writing it at sector 0
        // would ruin it. So even with leave to replace data, an open disc goes to the Windows writer, untouched.
        var (engine, _) = Engine();
        using var medium = VirtualDiscMedium.CreateBlankCdRw700();
        var recorder = Recorder(medium);
        var service = new DataBurnService(engine);
        byte[] first = Image(400, seed: 1);
        Assert.True((await service.BurnStreamAsync(recorder, new MemoryStream(first), new DataBurnOptions { CloseDisc = false })).Success);
        Assert.Null(engine.LastFallbackReason); // the first session was written directly
        Assert.False(medium.IsFinalized);

        foreach (bool overwrite in new[] { false, true })
        {
            var appended = await service.BurnStreamAsync(recorder, new MemoryStream(Image(300, seed: 3)),
                new DataBurnOptions { CloseDisc = false, OverwriteExistingData = overwrite, VerifyAfterBurn = true });
            Assert.True(appended.Success, appended.ErrorMessage);
            Assert.True(appended.Verified);
            Assert.Contains("adds no sessions yet", engine.LastFallbackReason);
            Assert.True(OnDisc(medium, 400).SequenceEqual(first)); // the first session is still there
        }
    }

    [Fact]
    public async Task TestWriteFirst_OnADriveThatNeedsAReload_StopsWithTheReloadMessage()
    {
        var (engine, drive) = Engine(testFirst: true);
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        var recorder = Recorder(medium);

        var result = await new DataBurnService(engine).BurnStreamAsync(recorder, new MemoryStream(Image(400)), new DataBurnOptions { VerifyAfterBurn = true });

        Assert.False(result.Success);
        Assert.True(engine.LastTestWritePassed);
        Assert.Contains("reloaded", result.ErrorMessage);
        Assert.Null(drive()!.DataDiscImage);
        Assert.True(medium.IsBlank);
        Assert.Equal(0, drive()!.TrayEjects);
    }

    [Fact]
    public async Task TestWriteFirst_OnADriveThatClosesItsOwnTray_ReloadsAndWrites()
    {
        var (engine, drive) = Engine(testFirst: true, configure: d => d.CanLoadTray = true);
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        var recorder = Recorder(medium);

        var result = await new DataBurnService(engine).BurnStreamAsync(recorder, new MemoryStream(Image(400)), new DataBurnOptions { VerifyAfterBurn = true });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(engine.LastTestWritePassed);
        Assert.Equal(1, drive()!.TrayLoads);
        Assert.True(result.Verified);
    }

    [Fact]
    public async Task TestWriteFirst_ThenRealWrite_OnADriveThatForgetsTheTest()
    {
        var (engine, _) = Engine(testFirst: true, configure: d => d.ShowsSimulatedSessionAfterTestWrite = false);
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        var recorder = Recorder(medium);
        byte[] image = Image(400);

        var result = await new DataBurnService(engine).BurnStreamAsync(recorder, new MemoryStream(image), new DataBurnOptions { VerifyAfterBurn = true });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.Verified);
        Assert.True(engine.LastTestWritePassed);
        Assert.True(OnDisc(medium, 400).SequenceEqual(image));
    }

    [Fact]
    public async Task SimulateOption_IsATestWriteOnly()
    {
        var (engine, drive) = Engine();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        var recorder = Recorder(medium);

        var result = await new DataBurnService(engine).BurnStreamAsync(recorder, new MemoryStream(Image(400)), new DataBurnOptions { Simulate = true });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(drive()!.WriteCommandCount > 0);
        Assert.Null(drive()!.DataDiscImage);
        Assert.True(medium.IsBlank);
    }
}
