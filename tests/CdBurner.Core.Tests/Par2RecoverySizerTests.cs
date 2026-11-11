using CdBurner.Core.Archival;
using CdBurner.Core.Primitives;
using Xunit;

namespace CdBurner.Core.Tests;

/// <summary>M2.1: sizing PAR2 recovery data to fill a disc, from the PAR2 2.0 packet layout.</summary>
public class Par2RecoverySizerTests
{
    // The three files of a par2cmdline 1.4.0 experiment (2026-11-10): with -s4096 -n1, the index file was 3,008
    // bytes and the recovery volume 436,832 bytes for 100 blocks and 286,928 for 64.
    private static readonly Par2SourceFile[] Measured =
    [
        new("a.bin", 300_000),
        new("sub/b.dat", 123_457),
        new("sub/long name c.txt", 5_000)
    ];

    [Fact]
    public void CriticalPackets_MatchTheMeasuredPar2Output()
    {
        // index (3,008) = critical packets + creator packet (104 bytes for par2cmdline 1.4.0)
        Assert.Equal(3_008 - 104, Par2RecoverySizer.CriticalPacketBytes(Measured, 4096));
    }

    [Theory]
    [InlineData(1, 7_172)]
    [InlineData(4, 25_472)]
    [InlineData(64, 286_928)]
    [InlineData(100, 436_832)]
    public void SectorEstimate_CoversTheMeasuredFilesExactly(int blocks, int measuredVolumeBytes)
    {
        int expected = (3_008 + 2047) / 2048 + (measuredVolumeBytes + 2047) / 2048;
        Assert.Equal(expected, Par2RecoverySizer.EstimateSectors(Measured, 4096, blocks));
    }

    [Fact]
    public void Plan_FillsTheBudgetToWithinOneBlock()
    {
        var files = Enumerable.Range(0, 40).Select(i => new Par2SourceFile($"photos/img{i:D3}.jpg", 3_000_000 + i * 7919)).ToArray();
        const long budget = 30_000; // 60 MB

        var plan = Par2RecoverySizer.Plan(files, budget)!;

        Assert.True(plan.RecoverySectors <= budget);
        Assert.True(Par2RecoverySizer.EstimateSectors(files, plan.BlockSize, plan.RecoveryBlocks + 1) > budget);
        long leftover = budget - plan.RecoverySectors;
        Assert.True(leftover * 2048 <= plan.BlockSize + 2 * 2048, $"{leftover} sectors left over");
        Assert.Equal(0, plan.BlockSize % 2048);
        Assert.InRange(plan.SourceBlocks, 1, Par2RecoverySizer.TargetSourceBlocks);
        Assert.True(plan.RedundancyPercent > 40);
    }

    [Fact]
    public void FullDvd_IsCappedByWork_WithBiggerBlocksInstead()
    {
        // 4.0 GB of data and 440 MB of room: ~3,300 blocks at the finest size would cost too much par2 time.
        var files = Enumerable.Range(0, 400).Select(i => new Par2SourceFile($"v/{i}.mkv", 10_000_000)).ToArray();
        long budget = 440_000_000 / 2048;

        var plan = Par2RecoverySizer.Plan(files, budget)!;

        long workCap = Par2RecoverySizer.DefaultMaxWork / 4_000_000_000L;   // 750
        Assert.InRange(plan.RecoveryBlocks, (int)workCap, (int)workCap * 105 / 100);
        Assert.True(plan.RecoverySectors <= budget);
        Assert.True(budget - plan.RecoverySectors <= plan.BlockSize / 2048 + 2);
        // Today's par2 default (-r10, about 2,000 source blocks) gives ~200 recovery blocks.
        Assert.True(plan.RecoveryBlocks >= 3 * 200);
    }

    [Fact]
    public void NearlyEmptyDisc_StaysWithinPar2sBlockLimit_AndStillFills()
    {
        var files = new[] { new Par2SourceFile("note.txt", 20_000_000) };
        long budget = 300_000; // 600 MB of room for 20 MB of data

        var plan = Par2RecoverySizer.Plan(files, budget)!;

        Assert.Equal(Par2RecoverySizer.MaxRecoveryBlocks, plan.RecoveryBlocks);
        Assert.True(plan.RecoverySectors <= budget);
        // At par2's block limit the block size moves in 2 KB steps, so up to one sector per block can stay empty.
        Assert.True(budget - plan.RecoverySectors <= plan.RecoveryBlocks + 2, $"{budget - plan.RecoverySectors} sectors left");
        Assert.True(plan.RecoverySectors > budget * 9 / 10);
    }

    [Fact]
    public void EmptyFiles_CountOnlyTheirDescription()
    {
        var withEmpty = Measured.Append(new Par2SourceFile("empty.txt", 0)).ToArray();
        long extra = Par2RecoverySizer.CriticalPacketBytes(withEmpty, 4096) - Par2RecoverySizer.CriticalPacketBytes(Measured, 4096);
        Assert.Equal(16 + 64 + 56 + 12, extra); // main-packet entry + description with "empty.txt" padded to 12
    }

    [Fact]
    public void NothingToProtectOrNoRoom_GivesNoPlan()
    {
        Assert.Null(Par2RecoverySizer.Plan([], 10_000));
        Assert.Null(Par2RecoverySizer.Plan([new Par2SourceFile("a", 0)], 10_000));
        Assert.Null(Par2RecoverySizer.Plan(Measured, 0));
        Assert.Null(Par2RecoverySizer.Plan(Measured, 3)); // index + one volume need more than 3 sectors
    }

    [Fact]
    public void Ceiling_IsCapacityMinusTheReserve_AndUsableLeavesTheMinimum()
    {
        int ceiling = ArchiveSpanPlanner.CalculateCeilingSectors(DiscMediaType.DvdPlusR);
        Assert.Equal(OpticalConstants.DvdPlusR5Sectors - OpticalConstants.DvdPlusR5Sectors / 50, ceiling);
        Assert.Equal((int)(ceiling / 1.1), ArchiveSpanPlanner.CalculateUsableSectors(DiscMediaType.DvdPlusR, 10));
    }

    [Fact]
    public void Plan_CarriesTheCeilingAndFillChoice()
    {
        var files = new[] { new ArchiveSourceFile("C:\\x\\a.bin", "a.bin", 1_000_000) };
        var plan = ArchiveSpanPlanner.Plan(files, DiscMediaType.CdR, 10, "set", fillFreeSpace: false, ceilingSectorsOverride: 5_000);

        Assert.Equal(5_000, plan.EffectiveCeilingSectors);
        Assert.False(plan.FillFreeSpace);
        Assert.Equal((int)(5_000 / 1.1), plan.UsableSectorsPerDisc);
        Assert.True(ArchiveSpanPlanner.Plan(files, DiscMediaType.CdR, 10).FillFreeSpace);
    }

    [Fact]
    public void Arguments_AskForOneVolumeWithTheChosenBlocks()
    {
        var layout = new Par2RecoveryLayout(65_536, 1_234, 500, 0, 1);
        var args = Par2Runner.BuildCreateArguments("C:\\s\\recovery.par2", "C:\\disc", layout);
        Assert.Equal(new[] { "create", "-q", "-s65536", "-c1234", "-n1", "-R", "-BC:\\disc", "C:\\s\\recovery.par2", "*" }, args);
    }
}
