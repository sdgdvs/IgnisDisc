using System.IO;
using CdBurner.App.ViewModels;
using CdBurner.Core.Archival;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// Integration tests for <see cref="ArchiveViewModel"/>: planning, staging, burning,
/// verification against SHA-256 manifests, and PAR2 recovery repair.
/// </summary>
public sealed class ArchiveViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DiscDeviceManager _deviceManager;
    private readonly VirtualBurnEngine _burnEngine;

    public ArchiveViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CdBurner_Archive_Tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _deviceManager = new DiscDeviceManager(enablePhysicalEnumeration: false);
        _burnEngine = new VirtualBurnEngine();
    }

    public void Dispose()
    {
        _deviceManager.Dispose();

        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    private string CreateSampleFolder(string name = "SourceData", int fileCount = 3)
    {
        string folder = Path.Combine(_tempDir, name);
        Directory.CreateDirectory(folder);
        for (int i = 1; i <= fileCount; i++)
        {
            File.WriteAllBytes(Path.Combine(folder, $"file{i:D2}.dat"), new byte[1024 * i]);
        }
        return folder;
    }

    [Fact]
    public async Task Plan_FromFolder_FillsDiscsAndSummary()
    {
        string source = CreateSampleFolder("PlanSource", 3);
        var vm = new ArchiveViewModel(_deviceManager, _burnEngine)
        {
            SourceFolder = source,
            SelectedMedia = new ArchiveMediaOption("CD-R (700 MB)", DiscMediaType.CdR),
            RedundancyPercent = 0
        };

        var plan = await vm.PlanAsync();

        Assert.NotNull(plan);
        Assert.Same(plan, vm.CurrentPlan);
        Assert.Single(vm.Discs);
        Assert.Equal(1, vm.Discs[0].DiscNumber);
        Assert.Equal(3, vm.Discs[0].FileCount);
        Assert.Equal("Planned", vm.Discs[0].Status);
        Assert.False(string.IsNullOrWhiteSpace(vm.PlanSummary));
        Assert.Contains("1 disc", vm.PlanSummary);
        Assert.Contains("0% PAR2", vm.PlanSummary);
    }

    [Fact]
    public async Task Stage_WithZeroRedundancy_StagesAndMarksRows()
    {
        string source = CreateSampleFolder("StageSource", 3);
        string staging = Path.Combine(_tempDir, "Staging");
        var vm = new ArchiveViewModel(_deviceManager, _burnEngine)
        {
            SourceFolder = source,
            StagingFolder = staging,
            SelectedMedia = new ArchiveMediaOption("CD-R (700 MB)", DiscMediaType.CdR),
            RedundancyPercent = 0
        };

        bool ok = await vm.StageAsync();

        Assert.True(ok);
        Assert.Single(vm.Discs);
        var disc = vm.Discs[0];
        Assert.Equal("Staged", disc.Status);
        Assert.NotNull(disc.StagedPath);
        Assert.True(Directory.Exists(disc.StagedPath));
        Assert.True(File.Exists(Path.Combine(disc.StagedPath, ArchiveManifest.ManifestFileName)));
        Assert.True(File.Exists(Path.Combine(disc.StagedPath, ArchiveManifest.IndexFileName)));
    }

    [Fact]
    public async Task Verify_StagedDisc_IsIntact_ThenDetectsCorruption()
    {
        string source = CreateSampleFolder("VerifySource", 2);
        string staging = Path.Combine(_tempDir, "StagingVerify");
        var vm = new ArchiveViewModel(_deviceManager, _burnEngine)
        {
            SourceFolder = source,
            StagingFolder = staging,
            SelectedMedia = new ArchiveMediaOption("CD-R (700 MB)", DiscMediaType.CdR),
            RedundancyPercent = 0
        };

        bool ok = await vm.StageAsync();
        Assert.True(ok);
        string staged = vm.Discs[0].StagedPath!;

        var intactResult = await vm.VerifyAsync(staged);
        Assert.NotNull(intactResult);
        Assert.True(intactResult.IsIntact);
        Assert.Empty(vm.VerificationProblems);
        Assert.Contains("Intact", vm.VerificationSummary);

        // Corrupt one data file
        string targetFile = Path.Combine(staged, "file01.dat");
        byte[] bytes = File.ReadAllBytes(targetFile);
        bytes[0] ^= 0xFF;
        File.WriteAllBytes(targetFile, bytes);

        var corruptResult = await vm.VerifyAsync(staged);
        Assert.NotNull(corruptResult);
        Assert.False(corruptResult.IsIntact);
        Assert.NotEmpty(corruptResult.Corrupt);
        Assert.Contains(vm.VerificationProblems, p => p.Contains("CORRUPT"));
    }

    [Fact]
    public async Task Burn_StagedDisc_ToVirtualRecorder_Succeeds()
    {
        string source = CreateSampleFolder("BurnSource", 2);
        string staging = Path.Combine(_tempDir, "StagingBurn");
        var vm = new ArchiveViewModel(_deviceManager, _burnEngine)
        {
            SourceFolder = source,
            StagingFolder = staging,
            SelectedMedia = new ArchiveMediaOption("CD-R (700 MB)", DiscMediaType.CdR),
            RedundancyPercent = 0
        };

        bool ok = await vm.StageAsync();
        Assert.True(ok);

        var vrec = vm.AvailableRecorders.OfType<VirtualDiscRecorder>().First(r => r.UniqueId == "VDRV-01");
        vrec.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());
        vm.SelectedRecorder = vrec;

        var burnResult = await vm.BurnDiscAsync(vm.Discs[0]);

        Assert.True(burnResult.Success, $"Burn failed: {burnResult.ErrorMessage}");
        Assert.StartsWith("Burned", vm.Discs[0].Status);
        Assert.False(vm.UseDirectDriveCommands); // off unless chosen
        Assert.Null(vm.LastDirectBurnEngine);
        Assert.DoesNotContain("drive commands", vm.StatusMessage);
    }

    private async Task<(ArchiveViewModel Vm, VirtualDiscRecorder Recorder)> StageOneDiscWithDirectWritingAsync(string name)
    {
        var vm = new ArchiveViewModel(_deviceManager, _burnEngine)
        {
            SourceFolder = CreateSampleFolder(name + "Source", 2),
            StagingFolder = Path.Combine(_tempDir, name + "Staging"),
            SelectedMedia = new ArchiveMediaOption("CD-R (700 MB)", DiscMediaType.CdR),
            RedundancyPercent = 0,
            VerifyAfterBurn = false,
            DirectBurnLogDirectory = Path.Combine(_tempDir, name + "Logs")
        };
        Assert.True(await vm.StageAsync());
        vm.UseDirectDriveCommands = true;
        var vrec = vm.AvailableRecorders.OfType<VirtualDiscRecorder>().First(r => r.UniqueId == "VDRV-01");
        vm.SelectedRecorder = vrec;
        return (vm, vrec);
    }

    [Fact]
    public async Task Burn_WithDirectDriveCommands_WritesTheBlankCdDirectlyAndReadsItBack()
    {
        var (vm, vrec) = await StageOneDiscWithDirectWritingAsync("Direct");
        var medium = VirtualDiscMedium.CreateBlankCdR700();
        vrec.InsertMedia(medium);

        var result = await vm.BurnDiscAsync(vm.Discs[0]);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.Verified); // read back although "Verify after burning" is off, as on the Data Disc screen
        Assert.Contains("Written with direct drive commands", vm.StatusMessage);
        Assert.Null(vm.LastDirectBurnEngine!.LastFallbackReason);
        Assert.True(vm.LastDirectBurnEngine.LastResult!.DiscRecorded);
        Assert.True(File.Exists(vm.LastDirectBurnEngine.LastLogPath));
        Assert.True(medium.IsFinalized);
        Assert.False(medium.IsBlank);
    }

    [Fact]
    public async Task Burn_WithDirectDriveCommands_HandsADualLayerBdRToTheWindowsWriterAndSaysWhy()
    {
        var (vm, vrec) = await StageOneDiscWithDirectWritingAsync("Fallback");
        var medium = new VirtualDiscMedium(DiscMediaType.BdRDualLayer, OpticalConstants.BdR50Sectors);
        vrec.InsertMedia(medium);

        var result = await vm.BurnDiscAsync(vm.Discs[0]);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.Verified);
        string? reason = vm.LastDirectBurnEngine!.LastFallbackReason;
        Assert.NotNull(reason);
        Assert.StartsWith("Written with Windows' writer:", reason);
        Assert.Contains("single-layer Blu-ray", reason);
        Assert.Contains(reason, vm.StatusMessage);
        Assert.Null(vm.LastDirectBurnEngine.LastResult); // the direct writer sent no command to the drive
        Assert.Null(vm.LastDirectBurnEngine.LastLogPath);
        Assert.False(medium.IsBlank); // the Windows writer burned it instead
    }

    [Fact]
    public async Task Repair_RestoresCorruptedFile_WhenPar2Available()
    {
        if (Par2Runner.FindPar2Executable() == null)
        {
            return;
        }

        string source = CreateSampleFolder("RepairSource", 2);
        string staging = Path.Combine(_tempDir, "StagingRepair");
        var vm = new ArchiveViewModel(_deviceManager, _burnEngine)
        {
            SourceFolder = source,
            StagingFolder = staging,
            SelectedMedia = new ArchiveMediaOption("CD-R (700 MB)", DiscMediaType.CdR),
            RedundancyPercent = 10
        };

        bool ok = await vm.StageAsync();
        Assert.True(ok);
        string staged = vm.Discs[0].StagedPath!;

        // Corrupt one data file
        string targetFile = Path.Combine(staged, "file01.dat");
        byte[] bytes = File.ReadAllBytes(targetFile);
        bytes[0] ^= 0x5A;
        File.WriteAllBytes(targetFile, bytes);

        var vRes = await vm.VerifyAsync(staged);
        Assert.NotNull(vRes);
        Assert.False(vRes.IsIntact);
        Assert.True(vm.CanRepair);

        bool repaired = await vm.RepairAsync();
        Assert.True(repaired);
        Assert.NotNull(vm.LastVerification);
        Assert.True(vm.LastVerification.IsIntact);
        Assert.Empty(Directory.EnumerateFiles(staged, "*.1", SearchOption.AllDirectories));
    }

    [Fact]
    public void Redundancy_IsClamped_AndForcedToZeroWithoutPar2()
    {
        var vm = new ArchiveViewModel(_deviceManager, _burnEngine);

        if (vm.Par2Available)
        {
            vm.RedundancyPercent = 150;
            Assert.Equal(100, vm.RedundancyPercent);

            vm.RedundancyPercent = -20;
            Assert.Equal(0, vm.RedundancyPercent);

            vm.RedundancyPercent = 25;
            Assert.Equal(25, vm.RedundancyPercent);
        }
        else
        {
            Assert.Equal(0, vm.RedundancyPercent);

            vm.RedundancyPercent = 50;
            Assert.Equal(0, vm.RedundancyPercent);

            vm.RedundancyPercent = -10;
            Assert.Equal(0, vm.RedundancyPercent);
        }
    }
}
