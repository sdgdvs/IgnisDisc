using System.IO;
using CdBurner.App.ViewModels;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// The "Direct drive commands" switch on the Data Disc and Disc Image screens: off by default, writes a blank CD
/// directly and reads it back when on, and leaves a DVD to the Windows writer with a note saying so.
/// </summary>
public sealed class DirectDataBurnViewModelTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "CdBurner_Tests_" + Guid.NewGuid().ToString("N"));
    private readonly DiscDeviceManager _deviceManager = new(enablePhysicalEnumeration: false);
    private readonly VirtualBurnEngine _burnEngine = new();

    public DirectDataBurnViewModelTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        _deviceManager.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }

    private VirtualDiscMedium Insert(VirtualDiscMedium medium)
    {
        _deviceManager.GetRecorders().OfType<VirtualDiscRecorder>().First(r => r.UniqueId == "VDRV-01").InsertMedia(medium);
        return medium;
    }

    private DataBurnViewModel DataScreen()
    {
        var vm = new DataBurnViewModel(_deviceManager, _burnEngine) { VolumeLabel = "DIRECT", DirectBurnLogDirectory = Path.Combine(_tempDir, "logs") };
        byte[] payload = new byte[3_000_000];
        new Random(5).NextBytes(payload);
        vm.AddFile("payload.bin", payload);
        return vm;
    }

    [Fact]
    public void DirectDriveCommands_AreOffByDefault()
    {
        Assert.False(new DataBurnViewModel(_deviceManager, _burnEngine).UseDirectDriveCommands);
        Assert.False(new ImageStudioViewModel(_deviceManager, _burnEngine).UseDirectDriveCommands);
    }

    [Fact]
    public async Task DataScreen_WithDirectDriveCommands_WritesTheCdDirectlyAndReadsItBack()
    {
        var medium = Insert(VirtualDiscMedium.CreateBlankCdR700());
        var vm = DataScreen();
        vm.UseDirectDriveCommands = true;
        Assert.False(vm.VerifyAfterBurn);

        var result = await vm.StartDataBurnAsync("VDRV-01");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.Verified); // read back although "Verify After Burn" is off
        Assert.Contains("Written with direct drive commands", vm.StatusMessage);
        Assert.Null(vm.LastDirectBurnEngine!.LastFallbackReason);
        Assert.True(File.Exists(vm.LastDirectBurnEngine.LastLogPath));
        Assert.True(medium.IsFinalized);
    }

    [Fact]
    public async Task DataScreen_WithADvd_UsesTheWindowsWriterAndSaysSo()
    {
        Insert(VirtualDiscMedium.CreateBlankDvdR47());
        var vm = DataScreen();
        vm.UseDirectDriveCommands = true;

        var result = await vm.StartDataBurnAsync("VDRV-01");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Contains("Written with Windows' writer", vm.StatusMessage);
        Assert.Null(vm.LastDirectBurnEngine!.LastLogPath);
    }

    [Fact]
    public async Task DataScreen_WithoutDirectDriveCommands_IsUnchanged()
    {
        Insert(VirtualDiscMedium.CreateBlankCdR700());
        var vm = DataScreen();

        var result = await vm.StartDataBurnAsync("VDRV-01");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Null(vm.LastDirectBurnEngine);
        Assert.DoesNotContain("drive commands", vm.StatusMessage);
        Assert.False(Directory.Exists(Path.Combine(_tempDir, "logs")));
    }

    [Fact]
    public async Task ImageScreen_WithDirectDriveCommands_WritesTheIsoDirectlyAndChecksIt()
    {
        var medium = Insert(VirtualDiscMedium.CreateBlankCdRw700());
        var root = CompilationDirectory.CreateRoot();
        byte[] payload = new byte[2_000_000];
        new Random(9).NextBytes(payload);
        root.AddFile("payload.bin", payload);
        string iso = Path.Combine(_tempDir, "direct.iso");
        await using (var fs = new FileStream(iso, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await IsoUdfImageBuilder.BuildAsync(root, fs, new IsoBuildOptions(VolumeLabel: "DIRECT_ISO"));
        }

        var vm = new ImageStudioViewModel(_deviceManager, _burnEngine)
        {
            SourceImagePath = iso,
            UseDirectDriveCommands = true,
            DirectBurnLogDirectory = Path.Combine(_tempDir, "logs")
        };

        var result = await vm.BurnImageToDiscAsync("VDRV-01");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(vm.IsChecksumMatch, vm.VerificationStatusText); // checked although "Verify" is off
        Assert.Contains("Written with direct drive commands", vm.StatusMessage);
        Assert.True(medium.IsFinalized);
    }
}
