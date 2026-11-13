using System.Text.Json.Nodes;
using CdBurner.Core.Persistence;
using CdBurner.Core.Primitives;
using Xunit;

namespace CdBurner.Core.Tests;

/// <summary>
/// The default write speed and the settings upgrade that moved people to it.
/// The speed list below is what a real slim USB writer reported for a blank CD-R (10x, 16x, 24x, some twice,
/// one as 23.99x), so the rules are checked against the kind of list drives actually return.
/// </summary>
public class WriteSpeedPolicyTests
{
    private static WriteSpeedDescriptor Cd(double x) => WriteSpeedDescriptor.FromMultiplier(x, DiscMediaType.CdR);
    private static WriteSpeedDescriptor Dvd(double x) => WriteSpeedDescriptor.FromMultiplier(x, DiscMediaType.DvdPlusR);

    private static readonly WriteSpeedDescriptor[] RealCdRList = [Cd(10), Cd(15.99), Cd(23.99), Cd(24), Cd(16), Cd(10)];

    // ATIP lead-in start times: 97m26s0x is FORNET (measured weak at the rim), 97m24s01f is Sony.
    private const string FornetId = "97m26s03f (FORNET INTERNATIONAL PTE LTD. - Long Strategy (Cyanine / AZO))";
    private const string SonyId = "97m24s11f (SONY Corporation - Long Strategy (Cyanine / AZO))";

    [Fact]
    public void CappedMedia_HoldsTheDefaultSpeedDownAndGivesAReason()
    {
        var (speed, reason) = WriteSpeedPolicy.ChooseForMedia(RealCdRList, WriteSpeedPolicy.Recommended, FornetId, DiscMediaType.CdR);
        Assert.Equal(10, speed!.Value.Multiplier, 1);
        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.Contains("FORNET", reason);
    }

    [Fact]
    public void CappedMedia_AlsoHoldsDownMaximumButNeverRaisesSlowest()
    {
        Assert.Equal(10, WriteSpeedPolicy.ChooseForMedia(RealCdRList, WriteSpeedPolicy.Maximum, FornetId, DiscMediaType.CdR).Speed!.Value.Multiplier, 1);
        Assert.Equal(10, WriteSpeedPolicy.ChooseForMedia(RealCdRList, WriteSpeedPolicy.Slowest, FornetId, DiscMediaType.CdR).Speed!.Value.Multiplier, 1);
    }

    [Fact]
    public void CapWithNothingUnderIt_FallsBackToTheSlowestOffered()
    {
        var offered = new[] { Cd(16), Cd(24) };
        Assert.Equal(16, WriteSpeedPolicy.ChooseForMedia(offered, WriteSpeedPolicy.Recommended, FornetId, DiscMediaType.CdR).Speed!.Value.Multiplier, 1);
    }

    [Fact]
    public void UncappedMedia_KeepsThePolicyChoiceAndHasNoReason()
    {
        var (speed, reason) = WriteSpeedPolicy.ChooseForMedia(RealCdRList, WriteSpeedPolicy.Recommended, SonyId, DiscMediaType.CdR);
        Assert.Equal(WriteSpeedPolicy.Choose(RealCdRList, WriteSpeedPolicy.Recommended)!.Value.Multiplier, speed!.Value.Multiplier, 1);
        Assert.Equal(16, speed.Value.Multiplier, 1);
        Assert.Null(reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("VIRTUAL-OPTICAL-01")]
    public void NoKnownId_MeansNoCap(string? id)
    {
        Assert.Null(MediaSpeedCaps.Find(id, DiscMediaType.CdR));
    }

    [Fact]
    public void AnAtipCodeOnADvd_IsNotACdCap()
    {
        Assert.Null(MediaSpeedCaps.Find(FornetId, DiscMediaType.DvdPlusR));
    }

    [Fact]
    public void Slowest_IsTheSlowestSpeedOffered()
    {
        Assert.Equal(10, WriteSpeedPolicy.Choose(RealCdRList, WriteSpeedPolicy.Slowest)!.Value.Multiplier, 1);
    }

    [Fact]
    public void Maximum_IsTheFastestSpeedOffered()
    {
        Assert.Equal(24, WriteSpeedPolicy.Choose(RealCdRList, WriteSpeedPolicy.Maximum)!.Value.Multiplier, 1);
    }

    [Theory]
    [InlineData(16, 16)]  // offered
    [InlineData(12, 16)]  // not offered: the nearest above
    [InlineData(48, 24)]  // above everything: the nearest below
    [InlineData(4, 10)]   // below everything: the nearest above
    public void AChosenMultiplier_BecomesAnOfferedSpeed(int preference, double expected)
    {
        Assert.Equal(expected, WriteSpeedPolicy.Choose(RealCdRList, preference)!.Value.Multiplier, 0);
    }

    [Fact]
    public void NoSpeedsOffered_LeavesTheDriveItsOwnSpeed()
    {
        Assert.Null(WriteSpeedPolicy.Choose([], WriteSpeedPolicy.Slowest));
        Assert.Null(WriteSpeedPolicy.Choose(null, WriteSpeedPolicy.Maximum));
        Assert.Null(WriteSpeedPolicy.Choose([new WriteSpeedDescriptor(0, DiscMediaType.CdR)], WriteSpeedPolicy.Slowest));
    }

    [Fact]
    public void ForDisc_NeverOffersADvdSpeedForACd()
    {
        // A list that mixes kinds: the slowest by bytes per second is CD 8x, the fastest is DVD 16x.
        WriteSpeedDescriptor[] mixed = [Cd(8), Cd(48), Dvd(2), Dvd(16)];

        var forCdRw = WriteSpeedPolicy.ForDisc(mixed, DiscMediaType.CdRw); // no CD-RW entries: CD family
        Assert.All(forCdRw, s => Assert.Equal(DiscMediaType.CdR, s.MediaType));
        Assert.Equal(48, WriteSpeedPolicy.Choose(forCdRw, WriteSpeedPolicy.Maximum)!.Value.Multiplier, 0);

        var forDvd = WriteSpeedPolicy.ForDisc(mixed, DiscMediaType.DvdPlusR);
        Assert.Equal(2, WriteSpeedPolicy.Choose(forDvd, WriteSpeedPolicy.Slowest)!.Value.Multiplier, 0);
    }

    // ---- Recommended: from what the drive reports ----

    private static WriteSpeedDescriptor Clv(double x, DiscMediaType t = DiscMediaType.CdR) =>
        WriteSpeedDescriptor.FromMultiplier(x, t) with { Rotation = WriteRotation.Clv };

    private static WriteSpeedDescriptor Cav(double x, DiscMediaType t = DiscMediaType.CdR) =>
        WriteSpeedDescriptor.FromMultiplier(x, t) with { Rotation = WriteRotation.Cav };

    private static double Recommended(params WriteSpeedDescriptor[] speeds) =>
        WriteSpeedPolicy.Choose(speeds, WriteSpeedPolicy.Recommended)!.Value.Multiplier;

    [Fact]
    public void Recommended_SlimDrive_TakesItsOnlyClvSpeed()
    {
        // LG's published modes for a slim drive of this family: CD-R 10x CLV, 16x partial CAV, 24x CAV.
        Assert.Equal(10, Recommended(Clv(10), Cav(16), Cav(24)), 1);
    }

    [Fact]
    public void Recommended_DesktopDrive_TakesItsFastestClvSpeedUpTo16x()
    {
        // A typical half-height writer: CLV up to 16x, faster speeds CAV.
        Assert.Equal(16, Recommended(Clv(4), Clv(8), Clv(16), Cav(24), Cav(32), Cav(48)), 1);
    }

    [Fact]
    public void Recommended_IsNotTheSlowest_WhenFasterClvSpeedsExist()
    {
        Assert.NotEqual(Recommended(Clv(4), Clv(8), Clv(16), Cav(48)),
            WriteSpeedPolicy.Choose([Clv(4), Clv(8), Clv(16), Cav(48)], WriteSpeedPolicy.Slowest)!.Value.Multiplier);
    }

    [Fact]
    public void Recommended_WithoutRotationInformation_UsesTheCeiling()
    {
        Assert.Equal(16, Recommended(RealCdRList), 1);              // Windows' list: no rotation
        Assert.Equal(16, Recommended(Clv(10), Clv(16), Clv(24)), 1); // all one way: says nothing, ceiling applies
    }

    [Fact]
    public void Recommended_NothingAtOrBelowTheCeiling_TakesTheSlowest()
    {
        Assert.Equal(24, Recommended(Cd(24), Cd(48)), 1);
    }

    [Fact]
    public void Recommended_Dvd_TakesTheFastestClvSpeedUpTo8x()
    {
        const DiscMediaType d = DiscMediaType.DvdPlusR;
        Assert.Equal(8, Recommended(Clv(2.4, d), Clv(4, d), Clv(6, d), Clv(8, d), Cav(12, d), Cav(16, d)), 1);
        Assert.Equal(8, Recommended(Dvd(4), Dvd(8), Dvd(16)), 1);
        Assert.Equal(4, Recommended(Clv(4, d), Cav(8, d), Cav(16, d)), 1);
    }

    [Fact]
    public void Recommended_BluRay_HasNoCeilingButStaysClv()
    {
        const DiscMediaType b = DiscMediaType.BdR;
        Assert.Equal(6, Recommended(Clv(2, b), Clv(4, b), Clv(6, b), Cav(8, b), Cav(12, b)), 1);
        Assert.Equal(12, Recommended(WriteSpeedDescriptor.FromMultiplier(6, b), WriteSpeedDescriptor.FromMultiplier(12, b)), 1);
    }

    [Fact]
    public void AChosenSpeedOfferedBothWays_PrefersClv()
    {
        Assert.Equal(WriteRotation.Clv, WriteSpeedPolicy.Choose([Cav(16), Clv(16)], 16)!.Value.Rotation);
    }

    [Fact]
    public void WindowsList_IsMarkedWithTheDrivesRotation()
    {
        // Windows reports 10/16/24x with no rotation; the drive says 10x CLV, 16x and 24x CAV.
        var marked = WriteSpeedPolicy.WithRotationFrom([Cd(10), Cd(15.99), Cd(24)], [Clv(10), Cav(16), Cav(24)]);

        Assert.Equal([WriteRotation.Clv, WriteRotation.Cav, WriteRotation.Cav], marked.Select(s => s.Rotation));
        Assert.Equal(10, Recommended([.. marked]), 1);

        // A drive that reports nothing leaves Windows' list as it was, and the ceiling decides.
        var unmarked = WriteSpeedPolicy.WithRotationFrom([Cd(10), Cd(16), Cd(24)], []);
        Assert.All(unmarked, s => Assert.Equal(WriteRotation.Unknown, s.Rotation));
        Assert.Equal(16, Recommended([.. unmarked]), 1);
    }

    [Fact]
    public void ExplainRecommended_SaysWhatItUsed()
    {
        Assert.Equal("10x: the fastest of the speeds the drive writes at a constant linear velocity up to 16x.",
            WriteSpeedPolicy.ExplainRecommended([Clv(10), Cav(16), Cav(24)]));
        Assert.Equal("The drive lists no write speeds for this disc, so it keeps its own.", WriteSpeedPolicy.ExplainRecommended([]));
    }

    [Fact]
    public void NewSettings_AreTheSafeOnes()
    {
        var s = new AppSettings();
        Assert.Equal(WriteSpeedPolicy.Recommended, s.DefaultWriteSpeedMultiplier);
        Assert.True(s.VerifyAfterBurnByDefault);
        Assert.True(s.EnableBurnFreeByDefault);
        Assert.True(s.SecureAudioRipByDefault);
        Assert.False(s.EnableVirtualDrive);
        Assert.False(s.EjectAfterBurnByDefault);
        Assert.False(s.CheckForUpdatesOnStartup);
    }

    // ---- settings files written before the safe defaults ----

    private static string WriteOldSettingsFile(string dir, AppSettings values)
    {
        // An older build wrote every value and no version: serialize, then drop the version field.
        var node = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(values, ProjectSerializer.JsonOptions))!.AsObject();
        foreach (var key in node.Select(p => p.Key).Where(k => k.Equals(nameof(AppSettings.SettingsVersion), StringComparison.OrdinalIgnoreCase)).ToList())
        {
            node.Remove(key);
        }

        string path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, node.ToJsonString());
        return path;
    }

    [Fact]
    public void OldFile_WithTheOldDefaults_MovesToTheSafeOnes()
    {
        string dir = Directory.CreateTempSubdirectory("ignis-settings-").FullName;
        try
        {
            string path = WriteOldSettingsFile(dir, new AppSettings
            {
                DefaultWriteSpeedMultiplier = 16, EnableVirtualDrive = true, SecureAudioRipByDefault = false, StrictVerify = true
            });

            var loaded = AppSettingsService.LoadFromFile(path);

            Assert.Equal(WriteSpeedPolicy.Recommended, loaded.DefaultWriteSpeedMultiplier);
            Assert.False(loaded.EnableVirtualDrive);
            Assert.True(loaded.SecureAudioRipByDefault);
            Assert.True(loaded.StrictVerify); // not one of the changed defaults: kept
            Assert.Equal(AppSettings.CurrentVersion, loaded.SettingsVersion);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void OldFile_WithAnotherSpeedChosen_KeepsIt()
    {
        string dir = Directory.CreateTempSubdirectory("ignis-settings-").FullName;
        try
        {
            string path = WriteOldSettingsFile(dir, new AppSettings { DefaultWriteSpeedMultiplier = 24 });

            Assert.Equal(24, AppSettingsService.LoadFromFile(path).DefaultWriteSpeedMultiplier);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task FileSavedByThisBuild_IsNeverUpgradedAgain()
    {
        // Someone on this build who picks 16x on purpose must keep it after a restart.
        string dir = Directory.CreateTempSubdirectory("ignis-settings-").FullName;
        try
        {
            string path = Path.Combine(dir, "settings.json");
            var chosen = new AppSettings { DefaultWriteSpeedMultiplier = 16, EnableVirtualDrive = true, SecureAudioRipByDefault = false };

            AppSettingsService.SaveToFile(chosen.Clone(), path);
            var loaded = AppSettingsService.LoadFromFile(path);
            Assert.Equal(16, loaded.DefaultWriteSpeedMultiplier);
            Assert.True(loaded.EnableVirtualDrive);
            Assert.False(loaded.SecureAudioRipByDefault);

            await AppSettingsService.SaveToFileAsync(chosen.Clone(), path);
            var loadedAsync = await AppSettingsService.LoadFromFileAsync(path);
            Assert.Equal(16, loadedAsync.DefaultWriteSpeedMultiplier);
            Assert.True(loadedAsync.EnableVirtualDrive);
        }
        finally { Directory.Delete(dir, true); }
    }
}
