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

    /// <summary>
    /// A disc filled to the planner's usable size at <paramref name="percent"/> % (200 equal files), with every
    /// sector between the data and the ceiling given to recovery data, as the default fill does.
    /// </summary>
    private static (Par2SourceFile[] Files, long Budget) FullDisc(DiscMediaType media, int percent)
    {
        int ceiling = ArchiveSpanPlanner.CalculateCeilingSectors(media);
        int usable = ArchiveSpanPlanner.CalculateUsableSectors(media, percent);
        const int count = 200;
        long perFile = (long)usable / count * 2048;
        var files = Enumerable.Range(0, count).Select(i => new Par2SourceFile($"d/{i:D4}.bin", perFile)).ToArray();
        return (files, ceiling - perFile / 2048 * count);
    }

    // Measured with the sizer as it was before the block count was scaled to the disc (flat 3e12 work cap, flat
    // 30,000 target): the cap held DVD and Blu-ray recovery blocks almost constant across 10 / 15 / 20 % and grew the
    // blocks instead (DVD+R 568 / 782 / 958 KiB, BD-RE 15 / 20 / 25 MiB).
    [Theory]
    [InlineData(DiscMediaType.DvdPlusR, new[] { 716, 748, 781 })]
    [InlineData(DiscMediaType.DvdPlusRDualLayer, new[] { 393, 411, 429 })]
    [InlineData(DiscMediaType.BdRe, new[] { 139, 145, 151 })]
    [InlineData(DiscMediaType.BdReDualLayer, new[] { 69, 72, 75 })]
    [InlineData(DiscMediaType.BdxlRTripleLayer, new[] { 33, 35, 36 })]
    public void FullDvdAndBluRay_GetMoreRecoveryBlocksThanTheFlatWorkCapGave(DiscMediaType media, int[] flatCapBlocks)
    {
        int[] percents = [10, 15, 20];
        for (int i = 0; i < percents.Length; i++)
        {
            var (files, budget) = FullDisc(media, percents[i]);
            var plan = Par2RecoverySizer.Plan(files, budget)!;
            // Each step of the slider must now buy at least 1.9x what the flat cap gave at that step.
            Assert.True(plan.RecoveryBlocks >= flatCapBlocks[i] * 19 / 10,
                $"{media} {percents[i]} %: {plan.RecoveryBlocks} recovery blocks, flat cap gave {flatCapBlocks[i]}");
        }
    }

    [Theory]
    [InlineData(DiscMediaType.CdR)]
    [InlineData(DiscMediaType.DvdPlusR)]
    [InlineData(DiscMediaType.DvdMinusR)]
    [InlineData(DiscMediaType.DvdPlusRDualLayer)]
    [InlineData(DiscMediaType.BdR)]
    [InlineData(DiscMediaType.BdRe)]
    [InlineData(DiscMediaType.BdRDualLayer)]
    [InlineData(DiscMediaType.BdxlRTripleLayer)]
    [InlineData(DiscMediaType.BdxlRQuadLayer)]
    public void FullDisc_RecoveryBlocksScaleWithThePercentage(DiscMediaType media)
    {
        var plans = new[] { 10, 15, 20 }.Select(p =>
        {
            var (files, budget) = FullDisc(media, p);
            var plan = Par2RecoverySizer.Plan(files, budget)!;
            Assert.True(plan.RecoverySectors <= budget);
            Assert.Equal(0, plan.BlockSize % 2048);
            Assert.True(plan.BlockSize >= Par2RecoverySizer.MinBlockBytes);
            return plan;
        }).ToArray();

        double r15 = (double)plans[1].RecoveryBlocks / plans[0].RecoveryBlocks;
        double r20 = (double)plans[2].RecoveryBlocks / plans[0].RecoveryBlocks;
        // 1 : 1.5 : 2, give or take the rounding of blocks to whole sectors (about 9 % on a CD's 20 KiB blocks).
        Assert.InRange(r15, 1.5 * 0.9, 1.5 * 1.1);
        Assert.InRange(r20, 2.0 * 0.9, 2.0 * 1.1);
    }

    // Captured from the sizer before this change: full CDs were never work-capped and must not move.
    [Theory]
    [InlineData(10, 22 * 1024, 2_556)]
    [InlineData(15, 22 * 1024, 3_838)]
    [InlineData(20, 20 * 1024, 5_428)]
    public void FullCd_KeepsItsLayout(int percent, int blockSize, int recoveryBlocks)
    {
        var (files, budget) = FullDisc(DiscMediaType.CdR, percent);
        var plan = Par2RecoverySizer.Plan(files, budget)!;
        Assert.Equal(blockSize, plan.BlockSize);
        Assert.Equal(recoveryBlocks, plan.RecoveryBlocks);
    }

    [Fact]
    public void HalfFullCd_KeepsTheMeasuredBlockSize()
    {
        // The 350 MiB (367,001,600-byte), 48-MP3 run timed at 152 s with par2cmdline 1.4.0 used 42 KiB blocks; the
        // sizer before this change gave 43,008 bytes and 8,191 blocks for the same span with equal-sized files.
        var files = Enumerable.Range(0, 48).Select(i => new Par2SourceFile($"m/{i:D2}.mp3", 367_001_600L / 48)).ToArray();
        long budget = ArchiveSpanPlanner.CalculateCeilingSectors(DiscMediaType.CdR) - 367_001_600L / 2048;

        var plan = Par2RecoverySizer.Plan(files, budget)!;

        Assert.Equal(42 * 1024, plan.BlockSize);
        Assert.Equal(8_191, plan.RecoveryBlocks);
        Assert.True((double)plan.DataBytes * plan.RecoveryBlocks <= 1.01 * Par2RecoverySizer.MinWorkCap);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(15)]
    [InlineData(20)]
    public void SmallArchive_SpendsAtLeast85PercentOfItsRecoverySpaceOnRecoveryData(int percent)
    {
        // 60 MiB in 20 files with --no-fill: the budget is the percentage of the data. Before the 16 KiB floor the
        // sizer chose 4 KiB blocks here and only 45 % of the space (at 10 %) held recovery data; the rest was the
        // checksum packets par2 repeats in every copy of its critical packets.
        var files = Enumerable.Range(0, 20).Select(i => new Par2SourceFile($"s/{i:D2}.bin", 3L * 1024 * 1024)).ToArray();
        long budget = 60L * 1024 * 1024 / 2048 * percent / 100;

        var plan = Par2RecoverySizer.Plan(files, budget)!;

        double recoveryShare = (double)plan.RecoveryBlocks * plan.BlockSize / (plan.RecoverySectors * 2048.0);
        Assert.True(recoveryShare >= 0.85, $"{recoveryShare:P1} of {plan.RecoverySectors} sectors, {plan.BlockSize}-byte blocks");
        Assert.Equal(Par2RecoverySizer.MinBlockBytes, plan.BlockSize);
    }

    [Theory]
    [InlineData(700L * 1024 * 1024, 30_000)]           // a CD: the full target
    [InlineData(1_200L * 1024 * 1024, 29_023)]         // past about 1.1 GiB the square-root rule takes over
    [InlineData(4_480L * 1024 * 1024, 15_021)]         // DVD-5
    [InlineData(23_100L * 1024 * 1024, 6_615)]         // BD-25
    [InlineData(47_000L * 1024 * 1024, 5_500)]         // BD-50 and up: the floor
    [InlineData(0L, 30_000)]
    public void TargetSourceBlocks_FollowTheSquareRootOfTheSpan(long spanBytes, int expected)
    {
        // 38,000 x sqrt(700 MiB / span) within 5,500..30,000, worked by hand: 38,000 x sqrt(700 / 4,480) = 15,020.8.
        Assert.Equal(expected, Par2RecoverySizer.TargetSourceBlocksFor(spanBytes));
    }

    [Fact]
    public void PartFilledDisc_PutsRoomPastTheWorkCapIntoBiggerBlocks()
    {
        // 2,000 MiB on a BD-RE 25: the span's target is about 6,600 source blocks, so the cap is a quarter of that or
        // the CD work allowance, whichever allows more blocks; par2's time stays near a full disc at 25 %.
        var files = Enumerable.Range(0, 100).Select(i => new Par2SourceFile($"v/{i:D3}.bin", 20L * 1024 * 1024)).ToArray();
        long budget = ArchiveSpanPlanner.CalculateCeilingSectors(DiscMediaType.BdRe) - 100L * 20 * 1024 * 1024 / 2048;

        var plan = Par2RecoverySizer.Plan(files, budget)!;

        int target = Par2RecoverySizer.TargetSourceBlocksFor(plan.DataBytes + budget * 2048);
        long cap = Math.Max((target + 3) / 4, Par2RecoverySizer.MinWorkCap / plan.DataBytes);
        Assert.InRange(plan.RecoveryBlocks, (int)cap, (int)(cap * 105 / 100));
        Assert.True(plan.RecoverySectors <= budget);
        Assert.True(budget - plan.RecoverySectors <= plan.BlockSize / 2048 + 2);
    }

    [Fact]
    public void TinyFile_GetsABlockNoBiggerThanItself_AndTheMinimumBudgetPlans()
    {
        var files = new[] { new Par2SourceFile("note.txt", 5_000) };
        long minimum = Par2RecoverySizer.MinimumBudgetSectors(files);

        var plan = Par2RecoverySizer.Plan(files, minimum);

        Assert.NotNull(plan);
        Assert.Equal(6_144, plan!.BlockSize);  // 5,000 bytes rounded up to whole sectors, under the 16 KiB floor
        Assert.Equal(1, plan.RecoveryBlocks);
        Assert.Null(Par2RecoverySizer.Plan(files, minimum - 1));
    }

    [Fact]
    public void MinimumBudget_PlansOnALargeSpanToo()
    {
        var files = Enumerable.Range(0, 300).Select(i => new Par2SourceFile($"v/{i:D3}.bin", 10_000_000)).ToArray();
        long minimum = Par2RecoverySizer.MinimumBudgetSectors(files);
        Assert.NotNull(Par2RecoverySizer.Plan(files, minimum));
        Assert.Equal(0, Par2RecoverySizer.MinimumBudgetSectors([new Par2SourceFile("e", 0)]));
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

    [Fact]
    public void Plan_WithMoreThan30000SmallFiles_GivesAPositiveBlockSize()
    {
        // 31,000 non-empty files: each needs its own block, so the block count can never get under 30,000 by
        // growing the block size. The sizer must still return a positive, sector-aligned size.
        var files = Enumerable.Range(0, 31_000).Select(i => new Par2SourceFile($"f/{i:D5}.txt", 100)).ToArray();

        var plan = Par2RecoverySizer.Plan(files, 200_000);

        Assert.NotNull(plan);
        Assert.True(plan!.BlockSize > 0, $"block size {plan.BlockSize}");
        Assert.Equal(0, plan.BlockSize % 2048);
        Assert.Equal(31_000, plan.SourceBlocks);
        Assert.True(plan.RecoveryBlocks > 0);
        Assert.True(plan.RecoverySectors <= 200_000);
    }

    [Fact]
    public void Plan_WithMoreThan32768NonEmptyFiles_RefusesPlainly()
    {
        var files = Enumerable.Range(0, 32_769).Select(i => new Par2SourceFile($"f/{i:D5}.txt", 10)).ToArray();
        var ex = Assert.Throws<InvalidOperationException>(() => Par2RecoverySizer.Plan(files, 1_000_000));
        Assert.Contains("32,768", ex.Message);
    }

    [Fact]
    public void Plan_EmptyFilesDoNotCountTowardsTheFileLimit()
    {
        var files = Enumerable.Range(0, 40_000).Select(i => new Par2SourceFile($"e/{i:D5}.txt", i < 10 ? 5000 : 0)).ToArray();
        var plan = Par2RecoverySizer.Plan(files, 1_000_000);
        Assert.NotNull(plan);
        Assert.True(plan!.BlockSize > 0);
    }

    [Fact]
    public void SpanPlanner_KeepsFileCountPerDiscWithinTheParLimit()
    {
        var files = Enumerable.Range(0, 70_000).Select(i => new ArchiveSourceFile($"C:/x/{i}.txt", $"d/{i:D5}.txt", 10)).ToArray();
        var plan = ArchiveSpanPlanner.Plan(files, DiscMediaType.DvdMinusR, 10, "set");

        Assert.True(plan.Discs.Count >= 3);
        Assert.All(plan.Discs, d => Assert.True(d.Files.Count(f => f.SizeBytes > 0) <= ArchiveSpanPlanner.MaxNonEmptyFilesPerDisc));
        Assert.Equal(70_000, plan.Discs.Sum(d => d.Files.Count));
    }
}
