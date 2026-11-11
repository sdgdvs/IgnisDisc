using System.Buffers.Binary;
using System.Text;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Burning;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Scsi;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// Direct writing to Blu-ray on the simulated drive, from libburn's cookbook: BD-R written like DVD+R (no mode page, no
/// test write, CLOSE TRACK, then CLOSE SESSION or finalize with close function 110b); BD-RE formatted with FORMAT UNIT
/// type 00h when new, then written from sector 0 with no close; both only on a drive whose BD Write feature is current.
/// </summary>
public sealed class NativeBluRayWriterTests : IDisposable
{
    private const int Capacity = 12_219_392; // single-layer 25 GB BD
    private const int Speed6x = 26_973;      // kB/s, 6x Blu-ray as drives list it
    private const int Speed4x = 17_982;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cdburner-test-" + Guid.NewGuid().ToString("N"));

    public NativeBluRayWriterTests() => Directory.CreateDirectory(_dir);

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

    private static byte[] Image(int sectors, int seed = 31)
    {
        byte[] data = new byte[sectors * 2048];
        new Random(seed).NextBytes(data);
        return data;
    }

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

    private static VirtualMmcDrive BlankBdR() => new()
    {
        Profile = NativeTaoDataWriter.ProfileBdR,
        CapacitySectors = Capacity,
        WriteSpeedPerformanceResponse = Speeds(Speed6x, Speed4x)
    };

    private static VirtualMmcDrive BdRe(bool formatted) => new()
    {
        Profile = NativeTaoDataWriter.ProfileBdRe,
        CapacitySectors = Capacity,
        BdReFormatted = formatted,
        FormatCapacitiesResponse = VirtualMmcDrive.BuildFormatCapacities(Capacity, formatted, (NativeWriteCommands.FormatTypeDefault, Capacity)),
        WriteSpeedPerformanceResponse = Speeds(Speed6x)
    };

    private static NativeDataWriteResult Write(VirtualMmcDrive drive, byte[] image, bool closeDisc = true, bool testWrite = false, bool overwrite = false)
    {
        var writer = new NativeTaoDataWriter(drive, sleep: _ => { });
        return writer.Write(new NativeDataWriteRequest(new MemoryStream(image), 0, image.Length, testWrite, closeDisc, OverwriteExistingData: overwrite));
    }

    // ---- BD-R ----

    [Fact]
    public void BdR_IsWrittenLikeDvdPlusR_PaddedTo64KiBClusters_AndFinalizedWith110b()
    {
        var drive = BlankBdR();
        byte[] image = Image(1000);

        var result = Write(drive, image);

        Assert.True(result.Success, result.Message);
        Assert.Equal("sequential (BD-R)", result.WriteMode);
        Assert.True(result.IsBluRay);
        Assert.False(result.IsDvd);
        Assert.Equal(24, result.PaddingSectors); // 1,000 sectors up to 1,024, a whole number of 32-sector clusters
        Assert.True(drive.DataDiscImage!.AsSpan(0, image.Length).SequenceEqual(image));
        Assert.Equal(NativeWriteCommands.CloseFunctionFinalizeBdR, drive.LastCloseFunction);
        Assert.Equal(2, drive.DiscStatus);
        Assert.DoesNotContain(NativeWriteCommands.OpCodeModeSelect10, drive.CommandsReceived);
        Assert.DoesNotContain(NativeWriteCommands.OpCodeReserveTrack, drive.CommandsReceived);
    }

    [Fact]
    public void Speed_UsesBluRayUnits()
    {
        var drive = BlankBdR();

        var result = Write(drive, Image(320));

        Assert.Equal(Speed6x, drive.WriteSpeedSet);
        Assert.Equal(4495.5, result.OneXKiloBytesPerSecond);
        Assert.Equal(6.0, Math.Round(Speed6x / result.OneXKiloBytesPerSecond, 1));
    }

    [Fact]
    public void OpenBdR_HasItsSessionClosedAndStaysAppendable()
    {
        var drive = BlankBdR();

        var result = Write(drive, Image(320), closeDisc: false);

        Assert.True(result.Success, result.Message);
        Assert.Equal(NativeWriteCommands.CloseFunctionSession, drive.LastCloseFunction);
        Assert.Equal(1, drive.DiscStatus);
    }

    [Fact]
    public void BdRFinalizeRefused_ClosesTheSessionInstead_AndSaysSo()
    {
        var drive = BlankBdR();
        drive.RejectsFinalize = true;

        var result = Write(drive, Image(320));

        Assert.True(result.Success, result.Message);
        Assert.Contains("would not finalize", result.Message);
        Assert.Equal(1, drive.DiscStatus);
    }

    [Fact]
    public void BdR_HasNoTestWrite()
    {
        var drive = BlankBdR();

        var result = Write(drive, Image(320), testWrite: true);

        Assert.True(result.TestWriteNotOffered);
        Assert.True(result.StoppedBeforeWriting);
        Assert.Equal(0, drive.WriteCommandCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void BdRThatIsNotBlank_IsNotTouched(int status)
    {
        var drive = BlankBdR();
        drive.DiscStatus = status;

        var result = Write(drive, Image(320));

        Assert.True(result.StoppedBeforeWriting);
        Assert.Equal(0, drive.WriteCommandCount);
    }

    [Fact]
    public void DriveThatOnlyReadsBluRay_IsReportedAsNotCovered()
    {
        var drive = BlankBdR();
        drive.OffersBdWrite = false;

        var result = Write(drive, Image(320));

        Assert.True(result.UnsupportedDisc);
        Assert.True(result.StoppedBeforeWriting);
        Assert.Contains("0x0041", result.Message);
        Assert.Equal(0, drive.WriteCommandCount);
    }

    [Theory]
    [InlineData(NativeTaoDataWriter.ProfileBdRRandom)]
    [InlineData(NativeTaoDataWriter.ProfileBdRom)]
    public void RandomRecordingBdRAndBdRom_AreNotCovered(ushort profile)
    {
        var drive = BlankBdR();
        drive.Profile = profile;

        var result = Write(drive, Image(320));

        Assert.True(result.UnsupportedDisc);
        Assert.Equal(0, drive.WriteCommandCount);
    }

    // ---- BD-RE ----

    [Fact]
    public void NewBdRe_IsFormattedWithType00h_ThenWrittenFrom0_WithNothingToClose()
    {
        var drive = BdRe(formatted: false);
        byte[] image = Image(777);

        var result = Write(drive, image);

        Assert.True(result.Success, result.Message);
        Assert.Equal("overwrite (BD-RE)", result.WriteMode);
        Assert.True(result.IsBluRay);
        Assert.Contains("formatted", result.Message);
        byte[] expected = new byte[12];
        expected[1] = 0x82; expected[3] = 8; expected[10] = 0x08;
        BinaryPrimitives.WriteInt32BigEndian(expected.AsSpan(4), Capacity);
        Assert.Equal(expected, drive.LastFormatParameters);
        Assert.Equal(image, drive.DataDiscImage);
        Assert.DoesNotContain(NativeWriteCommands.OpCodeCloseTrackSession, drive.CommandsReceived);
    }

    [Fact]
    public void FormattedBdRe_IsWrittenWithoutFormatting()
    {
        var drive = BdRe(formatted: true);
        byte[] image = Image(640);

        var result = Write(drive, image);

        Assert.True(result.Success, result.Message);
        Assert.Equal(0, drive.FormatUnitCount);
        Assert.Equal(image, drive.DataDiscImage);
    }

    [Fact]
    public void BdReWithAFileSystem_IsLeftAlone_WithoutLeaveToReplaceIt()
    {
        var drive = BdRe(formatted: true);
        byte[] old = IsoLikeImage(400, seed: 2);
        drive.DataDiscImage = (byte[])old.Clone();

        var result = Write(drive, Image(320));

        Assert.True(result.StoppedBeforeWriting);
        Assert.Contains("BD-RE holds data", result.Message);
        Assert.Equal(old, drive.DataDiscImage);
    }

    [Fact]
    public void UnformattedBdRe_WithNoDefaultFormatOffered_IsRefused()
    {
        var drive = BdRe(formatted: false);
        drive.FormatCapacitiesResponse = VirtualMmcDrive.BuildFormatCapacities(Capacity, formatted: false);

        var result = Write(drive, Image(320));

        Assert.True(result.StoppedBeforeWriting);
        Assert.Contains("no default format", result.Message);
        Assert.Equal(0, drive.FormatUnitCount);
    }

    [Fact]
    public void BdReFormatParameters_FollowLibburnsLayout()
    {
        byte[] list = NativeWriteCommands.BuildBdReDefaultFormatParameters(0x00BA7400);
        Assert.Equal(new byte[] { 0x00, 0x82, 0x00, 0x08, 0x00, 0xBA, 0x74, 0x00, 0x00, 0x00, 0x08, 0x00 }, list);
    }

    // ---- Engine, through DataBurnService with its read-back check ----

    private NativeDataBurnEngine Engine() => new(logDirectory: _dir)
    {
        ConfigureVirtualDrive = d => d.WriteSpeedPerformanceResponse = Speeds(Speed6x)
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

    [Theory]
    [InlineData(DiscMediaType.BdR)]
    [InlineData(DiscMediaType.BdRe)]
    public async Task BluRay_IsWrittenDirectlyAndReadBackCorrectly(DiscMediaType type)
    {
        var engine = Engine();
        using var medium = VirtualDiscMedium.CreateBlankBluRay(type);
        var recorder = Recorder(medium);
        byte[] image = IsoLikeImage(2000, seed: 5);

        var result = await new DataBurnService(engine).BurnStreamAsync(recorder, new MemoryStream(image), new DataBurnOptions { VerifyAfterBurn = true });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.Verified);
        Assert.Null(engine.LastFallbackReason);
        Assert.True(engine.LastResult!.IsBluRay);
        Assert.Contains("at 6x", engine.LastSummary);
        Assert.True(OnDisc(medium, 2000).SequenceEqual(image));
    }

    [Fact]
    public async Task UsedBdRe_IsWrittenOverWithLeave()
    {
        var engine = Engine();
        using var medium = VirtualDiscMedium.CreateBlankBluRay(DiscMediaType.BdRe);
        var recorder = Recorder(medium);
        var service = new DataBurnService(engine);
        Assert.True((await service.BurnStreamAsync(recorder, new MemoryStream(IsoLikeImage(900, seed: 6)), new DataBurnOptions())).Success);

        byte[] second = IsoLikeImage(500, seed: 7);
        var result = await service.BurnStreamAsync(recorder, new MemoryStream(second), new DataBurnOptions { VerifyAfterBurn = true, OverwriteExistingData = true });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.Verified);
        Assert.Null(engine.LastFallbackReason);
        Assert.True(OnDisc(medium, 500).SequenceEqual(second));
    }

    [Fact]
    public async Task MultiLayerBluRay_IsHandedToTheWindowsWriter_WithTheReason()
    {
        var engine = Engine();
        using var medium = VirtualDiscMedium.CreateBlankBluRay(DiscMediaType.BdRDualLayer);
        var recorder = Recorder(medium);

        var result = await new DataBurnService(engine).BurnStreamAsync(recorder, new MemoryStream(Image(320)), new DataBurnOptions());

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Contains("single-layer Blu-ray", engine.LastFallbackReason);
    }
}
