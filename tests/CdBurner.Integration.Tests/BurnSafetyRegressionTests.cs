using System.IO;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Burning;
using CdBurner.Core.Compilation;
using CdBurner.Core.Images;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// End-to-end checks on the virtual drive for burn paths that could otherwise waste a disc or erase one:
/// raw-clone disc copy, and the unattended burn queue.
/// </summary>
public sealed class BurnSafetyRegressionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "IgnisDisc_BurnSafety_" + Guid.NewGuid().ToString("N"));

    public BurnSafetyRegressionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static async Task<byte[]> BuildIsoAsync()
    {
        var root = CompilationDirectory.CreateRoot();
        byte[] payload = new byte[64 * 1024];
        new Random(42).NextBytes(payload);
        root.AddFile("PAYLOAD.DAT", payload);
        using var ms = new MemoryStream();
        await IsoUdfImageBuilder.BuildAsync(root, ms, new IsoBuildOptions(VolumeLabel: "SAFETY"));
        return ms.ToArray();
    }

    [Fact]
    public async Task DiscCopy_RawCloneMode_BurnsTheDataNotTheRawSectorBytes()
    {
        byte[] iso = await BuildIsoAsync();
        int sectors = iso.Length / OpticalConstants.DataSectorSize;

        using var source = new VirtualDiscRecorder("SRC-01", @"S:\");
        var sourceMedium = VirtualDiscMedium.CreateBlankCdR700();
        for (int i = 0; i < sectors; i++)
        {
            sourceMedium.WriteSector(i, iso.AsSpan(i * OpticalConstants.DataSectorSize, OpticalConstants.DataSectorSize).ToArray(), OpticalConstants.DataSectorSize);
        }
        sourceMedium.UsedSectors = sectors;
        sourceMedium.IsFinalized = true;
        source.InsertMedia(sourceMedium);

        using var target = new VirtualDiscRecorder("DST-01", @"T:\");
        target.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());

        var result = await new DiscCopyService(new VirtualBurnEngine()).CopyDiscAsync(
            source,
            target,
            new DiscCopyOptions { RawCloneMode = true, VerifyEachCopy = false });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.False(result.WasAudioCd);
        for (int i = 0; i < sectors; i++)
        {
            byte[] written = target.CurrentMedium!.ReadSector(i, OpticalConstants.DataSectorSize);
            Assert.True(
                written.AsSpan().SequenceEqual(iso.AsSpan(i * OpticalConstants.DataSectorSize, OpticalConstants.DataSectorSize)),
                $"Sector {i} of the copy differs from the source disc's data.");
        }
    }

    [Fact]
    public async Task BurnQueue_RefusesAudioBinAndCueSheets_WithoutTouchingTheDisc()
    {
        string audioBin = Path.Combine(_dir, "album.bin");
        byte[] raw = new byte[OpticalConstants.RawSectorSize * 400];
        new Random(3).NextBytes(raw);
        await File.WriteAllBytesAsync(audioBin, raw);
        string cue = Path.Combine(_dir, "album.cue");
        await File.WriteAllTextAsync(cue, "FILE \"album.bin\" BINARY\n  TRACK 01 AUDIO\n    INDEX 01 00:00:00\n");

        using var recorder = new VirtualDiscRecorder("Q-01", @"Q:\");
        var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        var result = await new BurnQueueService(new VirtualBurnEngine()).ExecuteQueueAsync(
            recorder,
            new[] { new BurnQueueItem(audioBin), new BurnQueueItem(cue) },
            promptForNextDiscAsync: (_, _, _) => Task.FromResult(true),
            continueOnError: true);

        Assert.False(result.Success);
        Assert.Equal(2, result.FailedItems);
        Assert.Contains("CUE sheet", result.Items[1].ErrorMessage);
        Assert.True(medium.IsBlank, "A refused image must not be written to the disc.");
    }

    [Fact]
    public async Task BurnQueue_NeverReplacesDataOnARewritableDisc()
    {
        string isoPath = Path.Combine(_dir, "disc.iso");
        await File.WriteAllBytesAsync(isoPath, await BuildIsoAsync());

        using var recorder = new VirtualDiscRecorder("Q-02", @"R:\");
        var rewritable = VirtualDiscMedium.CreateBlankDvdPlusRw47();
        byte[] existing = Enumerable.Repeat((byte)0xAB, OpticalConstants.DataSectorSize).ToArray();
        for (int i = 0; i < 64; i++)
        {
            rewritable.WriteSector(i, existing, OpticalConstants.DataSectorSize);
        }
        rewritable.UsedSectors = 64;
        rewritable.IsBlank = false;
        recorder.InsertMedia(rewritable);
        Assert.True(recorder.QueryMediaStatus().HasDataThatBurningReplaces);

        var result = await new BurnQueueService(new VirtualBurnEngine()).ExecuteQueueAsync(
            recorder, new[] { new BurnQueueItem(isoPath) });

        Assert.False(result.Success);
        Assert.Equal(existing, recorder.CurrentMedium!.ReadSector(0, OpticalConstants.DataSectorSize));
    }

    [Fact]
    public async Task BurnQueue_CancelledAtTheDiscSwap_DoesNotShutDown()
    {
        string isoPath = Path.Combine(_dir, "disc.iso");
        await File.WriteAllBytesAsync(isoPath, await BuildIsoAsync());

        using var recorder = new VirtualDiscRecorder("Q-03", @"U:\");
        recorder.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());
        int shutdowns = 0;

        var result = await new BurnQueueService(new VirtualBurnEngine()).ExecuteQueueAsync(
            recorder,
            new[] { new BurnQueueItem(isoPath), new BurnQueueItem(isoPath) },
            promptForNextDiscAsync: (_, _, _) => Task.FromResult(false), // user clicks Cancel at the swap prompt
            postBurnAction: PostBurnAction.ShutDown,
            shutdownInitiator: () => shutdowns++);

        Assert.Equal(1, result.CompletedItems);
        Assert.Equal(BurnQueueItemStatus.Cancelled, result.Items[1].Status);
        Assert.Equal(0, shutdowns);
    }

    [Fact]
    public async Task BurnQueue_RunToTheEnd_RunsTheAfterBurnActionOnce()
    {
        string isoPath = Path.Combine(_dir, "disc.iso");
        await File.WriteAllBytesAsync(isoPath, await BuildIsoAsync());

        using var recorder = new VirtualDiscRecorder("Q-04", @"W:\");
        recorder.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());
        int shutdowns = 0;

        var result = await new BurnQueueService(new VirtualBurnEngine()).ExecuteQueueAsync(
            recorder,
            new[] { new BurnQueueItem(isoPath), new BurnQueueItem(isoPath) },
            promptForNextDiscAsync: (_, _, _) =>
            {
                recorder.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());
                return Task.FromResult(true);
            },
            postBurnAction: PostBurnAction.ShutDown,
            shutdownInitiator: () => shutdowns++);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, shutdowns);
    }
}
