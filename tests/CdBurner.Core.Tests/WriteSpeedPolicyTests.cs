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

    [Fact]
    public void Safest_IsTheSlowestSpeedOffered()
    {
        Assert.Equal(10, WriteSpeedPolicy.Choose(RealCdRList, WriteSpeedPolicy.Safest)!.Value.Multiplier, 1);
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
        Assert.Null(WriteSpeedPolicy.Choose([], WriteSpeedPolicy.Safest));
        Assert.Null(WriteSpeedPolicy.Choose(null, WriteSpeedPolicy.Maximum));
        Assert.Null(WriteSpeedPolicy.Choose([new WriteSpeedDescriptor(0, DiscMediaType.CdR)], WriteSpeedPolicy.Safest));
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
        Assert.Equal(2, WriteSpeedPolicy.Choose(forDvd, WriteSpeedPolicy.Safest)!.Value.Multiplier, 0);
    }

    [Fact]
    public void NewSettings_AreTheSafeOnes()
    {
        var s = new AppSettings();
        Assert.Equal(WriteSpeedPolicy.Safest, s.DefaultWriteSpeedMultiplier);
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

            Assert.Equal(WriteSpeedPolicy.Safest, loaded.DefaultWriteSpeedMultiplier);
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
