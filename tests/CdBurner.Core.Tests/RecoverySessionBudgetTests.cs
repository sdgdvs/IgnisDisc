using CdBurner.Core.Archival;
using Xunit;

namespace CdBurner.Core.Tests;

/// <summary>Space split of a CD-Extra disc: audio, fixed session costs, FLAC vault and PAR2 parity.</summary>
public class RecoverySessionBudgetTests
{
    private const long FileSystem = 300;

    private static RecoverySessionPlan PlanFor(int minutes, long metadata = 0) =>
        RecoverySessionBudget.Plan(minutes * 4_500L, RecoverySessionBudget.MeasuredCapacitySectors, FileSystem, metadata);

    [Fact]
    public void FixedCost_IsLeadOutLeadInAndPreGap()
    {
        Assert.Equal(11_400, RecoverySessionBudget.FixedSessionSectors);
    }

    [Theory]
    // free = 359,844 - minutes x 4,500 - (11,400 + 300); vault = minutes x 3,100
    [InlineData(30, 213_144, 93_000, true)]
    [InlineData(45, 145_644, 139_500, true)]   // 202,500 + 139,500 + 11,700 = 353,700 <= 359,844
    [InlineData(46, 141_144, 142_600, false)]  // 207,000 + 142,600 + 11,700 = 361,300 > 359,844
    [InlineData(74, 15_144, 229_400, false)]
    public void Sizing_AtThirtyFortyFiveFortySixAndSeventyFourMinutes(int minutes, long free, long vault, bool vaultFits)
    {
        var plan = PlanFor(minutes);

        Assert.Equal(free, plan.FreeSectors);
        Assert.Equal(vault, plan.VaultSectors);
        Assert.Equal(vaultFits, plan.VaultFits);
        Assert.Equal(vaultFits ? RecoveryTier.B : RecoveryTier.A, plan.Recommended);
        Assert.Equal(vaultFits, plan.TierB is not null);
        Assert.NotNull(plan.TierA);
        Assert.True(plan.TierA!.Layout!.RecoverySectors <= free);
    }

    [Fact]
    public void VaultStopsFittingBetween45And46Minutes()
    {
        // (359,844 - 11,400) / (4,500 + 3,100) = 45.84 minutes with no file system.
        long Sectors(double minutes) => (long)(minutes * 4_500);
        Assert.True(RecoverySessionBudget.Plan(Sectors(45.8), 359_844, 0).VaultFits);
        Assert.False(RecoverySessionBudget.Plan(Sectors(45.9), 359_844, 0).VaultFits);
    }

    [Fact]
    public void LongerAudio_GivesLessRepairThanShorterAudio()
    {
        // Same disc, 46 vs 74 minutes: the free space shrinks from 141,144 to 15,144 sectors.
        Assert.True(PlanFor(46).TierA!.ToleranceSeconds > PlanFor(74).TierA!.ToleranceSeconds);
        Assert.True(PlanFor(74).TierA!.ToleranceSeconds > 0);
    }

    [Fact]
    public void Tolerance_IsWorkedFromBlocksAndBlockSize()
    {
        // 175 blocks of 20,480 bytes: (175 - 1) x 20,480 = 3,563,520 bytes / 176,400 bytes a second = 20.2 s.
        var layout = new Par2RecoveryLayout(20_480, 175, 1, 0, 1);
        Assert.Equal(20, RecoverySessionBudget.ToleranceSeconds(layout));
        Assert.StartsWith("Repairs up to 20 seconds of damaged audio", RecoverySessionBudget.DescribeTolerance(layout));
        Assert.Contains("1 second ", RecoverySessionBudget.DescribeTolerance(new Par2RecoveryLayout(176_400 + 4_096, 2, 1, 0, 1)));
    }

    [Fact]
    public void TierC_KeepsParityInWhatIsLeftAfterTheMetadata()
    {
        var plan = PlanFor(74, metadata: 1_000);

        Assert.NotNull(plan.TierC);
        Assert.True(plan.TierC!.Layout!.RecoverySectors <= 15_144 - 1_000);
        Assert.True(plan.TierC.ToleranceSeconds <= plan.TierA!.ToleranceSeconds);
    }

    [Fact]
    public void TierC_IsAbsentWhenMetadataTakesAllTheRoom_OrNoneWasAsked()
    {
        Assert.Null(PlanFor(74, metadata: 15_144).TierC);
        Assert.Null(PlanFor(74).TierC);
    }

    [Fact]
    public void AudioThatFillsTheDisc_HasNoRecoveryRoom()
    {
        var plan = RecoverySessionBudget.Plan(350_000, RecoverySessionBudget.MeasuredCapacitySectors, FileSystem);

        Assert.Equal(0, plan.FreeSectors);
        Assert.Null(plan.TierA);
        Assert.Equal(RecoveryTier.None, plan.Recommended);
    }

    [Fact]
    public void NoAudio_GivesNoPlan_AndNegativeInputsAreRefused()
    {
        var plan = RecoverySessionBudget.Plan(0, 359_844, 300);
        Assert.Equal(RecoveryTier.None, plan.Recommended);
        Assert.Throws<ArgumentOutOfRangeException>(() => RecoverySessionBudget.Plan(-1, 359_844, 300));
    }
}
