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
/// Dual-layer DVD-R with the direct writer: Layer Jump Recording. Expected values come from these sources, not from the
/// writer: write type 4 = layer jump (Pioneer BDR-203/R11 command references, Write Type table); format 23h = Manual
/// Layer Jump Address, 4 reserved bytes then the address (Windows SDK ntddcdvd.h DVD_DUAL_LAYER_MANUAL_LAYER_JUMP); the
/// address is the last layer 0 LBA (a Plextor PX-760A reported 1,864,223 after 1,864,224 sectors on layer 0); and the
/// fixed address space where layer 1 runs from the outer edge in, so a jump after N layer 0 sectors lands at 2C - N
/// (on 100-sector layers, a jump after 70 lands at 130; both from Doom9 forum thread 113868, 2006). The simulated disc
/// is scaled down to 2,000-sector layers so the images stay small.
/// </summary>
public sealed class NativeDvdMinusRDualLayerWriterTests : IDisposable
{
    private const int LayerSectors = 2_000;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cdburner-test-" + Guid.NewGuid().ToString("N"));

    public NativeDvdMinusRDualLayerWriterTests() => Directory.CreateDirectory(_dir);

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

    private static byte[] Image(int sectors, int seed = 43)
    {
        byte[] data = new byte[sectors * 2048];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static VirtualMmcDrive BlankDvdMinusRDl(ushort profile = NativeTaoDataWriter.ProfileDvdMinusRDualLayer) => new()
    {
        Profile = profile,
        CapacitySectors = 2 * LayerSectors
    };

    private static NativeDataWriteResult Write(VirtualMmcDrive drive, byte[] image, int? layerBreak = null, bool testWrite = false, bool closeDisc = true)
    {
        var writer = new NativeTaoDataWriter(drive, sleep: _ => { });
        return writer.Write(new NativeDataWriteRequest(new MemoryStream(image), 0, image.Length, TestWrite: testWrite, CloseDisc: closeDisc,
            LayerBreakSectors: layerBreak));
    }

    /// <summary>READ (10) through the drive, as a reader of the closed disc would.</summary>
    private static byte[] ReadBack(VirtualMmcDrive drive, int sectors)
    {
        byte[] all = new byte[sectors * 2048];
        for (int lba = 0; lba < sectors; lba += 16)
        {
            int n = Math.Min(16, sectors - lba);
            var r = drive.ExecuteCommand(MmcCommandBuilder.BuildRead10Cdb(lba, (ushort)n, forceUnitAccess: true), all.AsSpan(lba * 2048, n * 2048),
                ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, TimeSpan.FromSeconds(5));
            Assert.True(r.IsSuccess, $"READ (10) at {lba} failed");
        }
        return all;
    }

    private static int Layer0CapacityReported(VirtualMmcDrive drive)
    {
        byte[] reply = new byte[12];
        var r = drive.ExecuteCommand(MmcCommandBuilder.BuildReadDiscStructureCdb(0x20, 12), reply, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, TimeSpan.FromSeconds(5));
        Assert.True(r.IsSuccess);
        return BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(8));
    }

    [Theory]
    [InlineData(NativeTaoDataWriter.ProfileDvdMinusRDualLayer)]
    [InlineData(NativeTaoDataWriter.ProfileDvdMinusRDualLayerJump)]
    public void ImageLargerThanLayer0_JumpsAtHalf_ThenWritesLayer1AtTheSameRadius_AndReadsBackWhole(ushort profile)
    {
        var drive = BlankDvdMinusRDl(profile);
        byte[] image = Image(3_001); // padded to 3,008 sectors

        var result = Write(drive, image);

        Assert.True(result.Success, result.Message);
        Assert.Equal("layer jump (DVD-R DL)", result.WriteMode);
        Assert.Equal(4, drive.CurrentModePage05[2] & 0x0F); // write type 4, layer jump
        // Half of 3,008 rounded up to 16 is 1,504 on layer 0, so the last layer 0 LBA is 1,503.
        Assert.Equal(1_503, drive.LayerJumpAddressSent);
        Assert.Equal(1_504, result.Layer0Sectors);
        // Layer 0 ends with a WRITE ending at 1,503; the next one starts at 2 x 2,000 - 1,504 = 2,496 on layer 1.
        int jumpIndex = drive.DvdWrites.FindIndex(w => w.Lba + w.Sectors == 1_504);
        Assert.True(jumpIndex >= 0);
        Assert.Equal(2_496, drive.DvdWrites[jumpIndex + 1].Lba);
        Assert.All(drive.DvdWrites.Skip(jumpIndex + 1), w => Assert.InRange(w.Lba, 2_496, 3_999));
        Assert.Equal(4_000, drive.DvdWrites[^1].Lba + drive.DvdWrites[^1].Sectors); // 1,504 on layer 1 ends at the inner edge
        Assert.Equal(NativeWriteCommands.CloseFunctionSession, drive.LastCloseFunction);
        Assert.Equal(2, drive.DiscStatus);
        // Closed: the disc reads in one run from LBA 0 and reports the shorter layer 0.
        Assert.True(ReadBack(drive, 3_001).SequenceEqual(image));
        Assert.Equal(1_504, Layer0CapacityReported(drive));
    }

    [Fact]
    public void RequestedBreak_SetsTheJumpAtItsLastLayer0Sector()
    {
        var drive = BlankDvdMinusRDl();
        byte[] image = Image(3_008);

        var result = Write(drive, image, layerBreak: 1_600);

        Assert.True(result.Success, result.Message);
        Assert.Equal(1_599, drive.LayerJumpAddressSent);
        Assert.Contains(drive.DvdWrites, w => w.Lba == 2_400); // 2 x 2,000 - 1,600
        Assert.True(ReadBack(drive, 3_008).SequenceEqual(image));
        Assert.Equal(1_600, Layer0CapacityReported(drive));
    }

    [Fact]
    public void ImageThatFitsOnLayer0_SetsNoJump_AndStaysOnLayer0()
    {
        var drive = BlankDvdMinusRDl();
        byte[] image = Image(1_200);

        var result = Write(drive, image);

        Assert.True(result.Success, result.Message);
        Assert.Null(drive.LayerJumpAddressSent);
        Assert.All(drive.DvdWrites, w => Assert.True(w.Lba + w.Sectors <= 1_200));
        Assert.True(ReadBack(drive, 1_200).SequenceEqual(image));
    }

    [Fact]
    public void DriveWithoutLayerJumpRecording_StopsBeforeTouchingTheDrive_AsNotCovered()
    {
        var drive = BlankDvdMinusRDl();
        drive.OffersLayerJump = false;

        var result = Write(drive, Image(3_008));

        Assert.False(result.Success);
        Assert.True(result.UnsupportedDisc);
        Assert.True(result.StoppedBeforeWriting);
        Assert.Contains("feature 0x0033", result.Message);
        Assert.DoesNotContain(NativeWriteCommands.OpCodeModeSelect10, drive.CommandsReceived);
        Assert.Equal(0, drive.WriteCommandCount);
    }

    [Fact]
    public void DiscToBeLeftOpen_IsLeftToTheWindowsWriter()
    {
        var drive = BlankDvdMinusRDl();

        var result = Write(drive, Image(3_008), closeDisc: false);

        Assert.True(result.UnsupportedDisc);
        Assert.True(result.StoppedBeforeWriting);
        Assert.Equal(0, drive.WriteCommandCount);
    }

    [Fact]
    public void DriveThatRefusesTheJumpAddress_StopsBeforeWriting()
    {
        var drive = BlankDvdMinusRDl();
        drive.RejectsLayerJump = true;

        var result = Write(drive, Image(3_008));

        Assert.False(result.Success);
        Assert.True(result.StoppedBeforeWriting);
        Assert.Contains("did not set the layer jump", result.Message);
        Assert.Equal(0, drive.WriteCommandCount);
    }

    [Theory]
    [InlineData(1_500)] // not a multiple of 16
    [InlineData(1_488)] // layer 1 would be larger than layer 0
    [InlineData(2_016)] // past the end of layer 0
    public void InvalidRequestedBreak_StopsBeforeAnythingIsSentOrWritten(int layerBreak)
    {
        var drive = BlankDvdMinusRDl();

        var result = Write(drive, Image(3_008), layerBreak);

        Assert.False(result.Success);
        Assert.True(result.StoppedBeforeWriting);
        Assert.Null(drive.LayerJumpAddressSent);
        Assert.Equal(0, drive.WriteCommandCount);
    }

    [Fact]
    public void TestWrite_RunsThroughTheJump_AndLeavesTheDiscBlank()
    {
        var drive = BlankDvdMinusRDl();

        var result = Write(drive, Image(3_008), testWrite: true);

        Assert.True(result.Success, result.Message);
        Assert.True(result.DiscStillBlankAfterTestWrite);
        Assert.Contains(drive.DvdWrites, w => w.Lba == 2_496);
        Assert.Null(drive.DataDiscImage);
    }

    [Fact]
    public void Simulator_RefusesAWriteThatCarriesOnPastTheJumpOnLayer0()
    {
        // A host that ignored the jump and went on at LBA 1,504 must be refused, as the drive's address space requires.
        var drive = BlankDvdMinusRDl();
        var page = WriteParametersPage.FromModeSense10(ModeSense(drive))!.ForDvdLayerJump(testWrite: false, bufferUnderrunFree: true, WriteParametersPage.TrackModeDvd);
        Assert.True(Send(drive, new byte[] { NativeWriteCommands.OpCodeModeSelect10, 0x10, 0, 0, 0, 0, 0, 0, (byte)page.ToModeSelect10ParameterList().Length, 0 },
            page.ToModeSelect10ParameterList()).IsSuccess);
        Assert.True(Send(drive, NativeWriteCommands.BuildSendDiscStructure(0x23, 12), NativeWriteCommands.BuildManualLayerJumpParameters(1_503)).IsSuccess);

        for (int lba = 0; lba < 1_504; lba += 16)
        {
            Assert.True(Send(drive, NativeWriteCommands.BuildWrite10(lba, 16), new byte[16 * 2048]).IsSuccess);
        }
        var past = Send(drive, NativeWriteCommands.BuildWrite10(1_504, 16), new byte[16 * 2048]);
        var onLayer1 = Send(drive, NativeWriteCommands.BuildWrite10(2_496, 16), new byte[16 * 2048]);

        Assert.Equal((byte)0x05, past.SenseInfo?.SenseKey);
        Assert.Equal((byte)0x21, past.SenseInfo?.Asc);
        Assert.Equal((byte)0x02, past.SenseInfo?.Ascq);
        Assert.True(onLayer1.IsSuccess);
    }

    [Fact]
    public void Simulator_RefusesFormat20hOnADvdMinusRDl()
    {
        // Pioneer R11 reference: "The Format Code 20h does not work for DVD-R DL disc."
        var drive = BlankDvdMinusRDl();

        var r = Send(drive, NativeWriteCommands.BuildSendDiscStructure(0x20, 12), NativeWriteCommands.BuildLayerBoundaryParameters(1_504));

        Assert.False(r.IsSuccess);
    }

    [Fact]
    public void ManualLayerJump_BuilderAndParserFollowTheWindowsSdkLayout()
    {
        // 1,864,223 = 0x001C721F, the address the Plextor reported.
        byte[] cdb = NativeWriteCommands.BuildSendDiscStructure(0x23, 12);
        Assert.Equal(new byte[] { 0xBF, 0, 0, 0, 0, 0, 0, 0x23, 0, 12, 0, 0 }, cdb);

        byte[] list = NativeWriteCommands.BuildManualLayerJumpParameters(1_864_223);
        Assert.Equal(new byte[] { 0, 10, 0, 0, 0, 0, 0, 0, 0x00, 0x1C, 0x72, 0x1F }, list);

        Assert.Equal(1_864_223u, NativeWriteCommands.ParseManualLayerJump(list));
        Assert.Null(NativeWriteCommands.ParseManualLayerJump(new byte[] { 0, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }));
    }

    [Fact]
    public void LayerJumpWriteType_IsFour()
    {
        Assert.Equal(4, WriteParametersPage.WriteTypeLayerJump);
    }

    [Fact]
    public async Task DualLayerDvdMinusR_IsWrittenDirectlyThroughTheBurnService_AndVerified()
    {
        var engine = new NativeDataBurnEngine(logDirectory: _dir)
        {
            ConfigureVirtualDrive = d => d.Layer0CapacitySectors = LayerSectors // the scaled-down layer
        };
        using var medium = new VirtualDiscMedium(DiscMediaType.DvdMinusRDualLayer, 2 * LayerSectors);
        var recorder = new VirtualDiscRecorder();
        recorder.InsertMedia(medium);
        byte[] image = Image(3_008, seed: 44);

        var result = await new DataBurnService(engine).BurnStreamAsync(recorder, new MemoryStream(image),
            new DataBurnOptions { VerifyAfterBurn = true, LayerBreakSectors = 1_600 });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.Verified);
        Assert.Null(engine.LastFallbackReason);
        Assert.Equal(1_600, engine.LastResult!.Layer0Sectors);
        Assert.Equal("layer jump (DVD-R DL)", engine.LastResult.WriteMode);
        byte[] onDisc = new byte[image.Length];
        medium.ReadSectors(0, 3_008, onDisc, 2048);
        Assert.True(onDisc.SequenceEqual(image));
        Assert.True(medium.IsFinalized);
    }

    private static byte[] ModeSense(VirtualMmcDrive drive)
    {
        byte[] buffer = new byte[256];
        byte[] cdb = { 0x5A, 0, WriteParametersPage.PageCode, 0, 0, 0, 0, 1, 0, 0 };
        Assert.True(drive.ExecuteCommand(cdb, buffer, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, TimeSpan.FromSeconds(5)).IsSuccess);
        return buffer;
    }

    private static ScsiExecutionResult Send(VirtualMmcDrive drive, byte[] cdb, byte[] data) =>
        drive.ExecuteCommand(cdb, data, ScsiPassThroughNative.SCSI_IOCTL_DATA_OUT, TimeSpan.FromSeconds(5));
}
