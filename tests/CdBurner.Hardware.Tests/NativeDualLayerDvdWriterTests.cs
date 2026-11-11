using System.Buffers.Binary;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Burning;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// Dual-layer DVD+R with the direct writer: the layer break, from growisofs' plus_r_dl_split. READ DISC STRUCTURE 20h
/// gives layer 0's capacity; an image that fits leaves it alone; a larger one gets half, rounded up to 16 sectors, sent
/// with SEND DISC STRUCTURE 20h and read back before the first WRITE. The simulated disc is scaled down (2,000-sector
/// layers) so the images stay small.
/// </summary>
public sealed class NativeDualLayerDvdWriterTests : IDisposable
{
    private const int LayerSectors = 2_000;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cdburner-test-" + Guid.NewGuid().ToString("N"));

    public NativeDualLayerDvdWriterTests() => Directory.CreateDirectory(_dir);

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

    private static byte[] Image(int sectors, int seed = 41)
    {
        byte[] data = new byte[sectors * 2048];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static VirtualMmcDrive BlankDvdPlusRDl() => new()
    {
        Profile = NativeTaoDataWriter.ProfileDvdPlusRDualLayer,
        CapacitySectors = 2 * LayerSectors
    };

    private static NativeDataWriteResult Write(VirtualMmcDrive drive, byte[] image, int? layerBreak = null)
    {
        var writer = new NativeTaoDataWriter(drive, sleep: _ => { });
        return writer.Write(new NativeDataWriteRequest(new MemoryStream(image), 0, image.Length, TestWrite: false, LayerBreakSectors: layerBreak));
    }

    [Fact]
    public void ImageLargerThanLayer0_GetsTheBreakAtHalf_RoundedUpTo16_ThenIsWrittenAndFinalized()
    {
        var drive = BlankDvdPlusRDl();
        byte[] image = Image(3_001); // padded to 3,008 sectors

        var result = Write(drive, image);

        Assert.True(result.Success, result.Message);
        Assert.Equal("sequential (DVD+R DL)", result.WriteMode);
        Assert.Equal(1_504, drive.LayerBreakSent); // growisofs: (3,008 / 16 + 1) / 2 * 16
        Assert.Equal(1_504, result.Layer0Sectors);
        Assert.True(drive.DataDiscImage!.AsSpan(0, image.Length).SequenceEqual(image));
        Assert.Equal(NativeWriteCommands.CloseFunctionFinalizeDvdPlusR, drive.LastCloseFunction);
    }

    [Theory]
    [InlineData(3_008, 1_504)]
    [InlineData(3_024, 1_520)] // 189 blocks: layer 0 gets the extra block
    [InlineData(3_952, 1_984)]
    public void AutomaticBreak_NeverLeavesLayer1LargerThanLayer0(int imageSectors, int expectedLayer0)
    {
        var drive = BlankDvdPlusRDl();

        var result = Write(drive, Image(imageSectors));

        Assert.True(result.Success, result.Message);
        Assert.Equal(expectedLayer0, drive.LayerBreakSent);
        Assert.True(expectedLayer0 >= imageSectors - expectedLayer0);
        Assert.Equal(0, expectedLayer0 % 16);
    }

    [Fact]
    public void ImageThatFitsOnLayer0_LeavesTheBreakAlone()
    {
        var drive = BlankDvdPlusRDl();

        var result = Write(drive, Image(1_200));

        Assert.True(result.Success, result.Message);
        Assert.Null(drive.LayerBreakSent);
        Assert.DoesNotContain(NativeWriteCommands.OpCodeSendDiscStructure, drive.CommandsReceived);
        Assert.Equal(LayerSectors, result.Layer0Sectors);
    }

    [Fact]
    public void RequestedBreak_IsSentAsAsked()
    {
        var drive = BlankDvdPlusRDl();

        var result = Write(drive, Image(3_008), layerBreak: 1_920);

        Assert.True(result.Success, result.Message);
        Assert.Equal(1_920, drive.LayerBreakSent);
    }

    [Theory]
    [InlineData(1_500)] // not a multiple of 16
    [InlineData(1_488)] // layer 1 would be larger than layer 0
    [InlineData(2_016)] // past the end of layer 0
    public void InvalidRequestedBreak_StopsBeforeAnythingIsSentOrWritten(int layerBreak)
    {
        var drive = BlankDvdPlusRDl();

        var result = Write(drive, Image(3_008), layerBreak);

        Assert.False(result.Success);
        Assert.True(result.StoppedBeforeWriting);
        Assert.Null(drive.LayerBreakSent);
        Assert.Equal(0, drive.WriteCommandCount);
    }

    [Fact]
    public void DriveThatRefusesTheBreak_StopsBeforeWriting()
    {
        var drive = BlankDvdPlusRDl();
        drive.RejectsLayerBreak = true;

        var result = Write(drive, Image(3_008));

        Assert.False(result.Success);
        Assert.True(result.StoppedBeforeWriting);
        Assert.Contains("did not set the layer break", result.Message);
        Assert.Equal(0, drive.WriteCommandCount);
    }

    [Fact]
    public void DriveThatAnswersWithAnErrorButSetsTheBreak_GoesOn()
    {
        var drive = BlankDvdPlusRDl();
        drive.RejectsLayerBreak = true;
        drive.SetsLayerBreakDespiteError = true;

        var result = Write(drive, Image(3_008));

        Assert.True(result.Success, result.Message);
        Assert.Equal(1_504, drive.LayerBreakSent);
    }

    [Fact]
    public void BreakFixedElsewhere_StopsWhenItWouldNeedToMove()
    {
        var drive = BlankDvdPlusRDl();
        drive.Layer0CapacitySectors = 1_600;
        drive.LayerBreakFixed = true;

        var result = Write(drive, Image(3_008));

        Assert.False(result.Success);
        Assert.True(result.StoppedBeforeWriting);
        Assert.Contains("fixed at", result.Message);
        Assert.Equal(0, drive.WriteCommandCount);
    }

    [Fact]
    public void LayerBoundary_BuildersAndParserFollowGrowisofs()
    {
        byte[] cdb = NativeWriteCommands.BuildSendDiscStructure(NativeWriteCommands.FormatLayerBoundary, 12);
        Assert.Equal(new byte[] { 0xBF, 0, 0, 0, 0, 0, 0, 0x20, 0, 12, 0, 0 }, cdb);

        byte[] list = NativeWriteCommands.BuildLayerBoundaryParameters(0x001FD800);
        Assert.Equal(new byte[] { 0, 10, 0, 0, 0, 0, 0, 0, 0x00, 0x1F, 0xD8, 0x00 }, list);

        byte[] reply = { 0, 10, 0, 0, 0x80, 0, 0, 0, 0x00, 0x1F, 0xD8, 0x00 };
        Assert.Equal((true, 0x001FD800), NativeWriteCommands.ParseLayerBoundary(reply));
        Assert.Null(NativeWriteCommands.ParseLayerBoundary(new byte[] { 0, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }));
    }

    [Fact]
    public async Task DualLayerDvdPlusR_IsWrittenDirectlyThroughTheBurnService_WithTheBreakItWasGiven()
    {
        var engine = new NativeDataBurnEngine(logDirectory: _dir)
        {
            ConfigureVirtualDrive = d => d.Layer0CapacitySectors = LayerSectors // the scaled-down layer
        };
        using var medium = new VirtualDiscMedium(DiscMediaType.DvdPlusRDualLayer, 2 * LayerSectors);
        var recorder = new VirtualDiscRecorder();
        recorder.InsertMedia(medium);
        byte[] image = Image(3_008, seed: 42);

        var result = await new DataBurnService(engine).BurnStreamAsync(recorder, new MemoryStream(image),
            new DataBurnOptions { VerifyAfterBurn = true, LayerBreakSectors = 1_600 });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.Verified);
        Assert.Null(engine.LastFallbackReason);
        Assert.Equal(1_600, engine.LastResult!.Layer0Sectors);
        byte[] onDisc = new byte[image.Length];
        medium.ReadSectors(0, 3_008, onDisc, 2048);
        Assert.True(onDisc.SequenceEqual(image));
    }
}
