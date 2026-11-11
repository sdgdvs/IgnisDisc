using System.IO;
using CdBurner.App.ViewModels;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Persistence;
using CdBurner.Core.Primitives;
using CdBurner.Core.Utilities;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// Unit and integration tests for WPF Burn Progress Modal, Erase &amp; Surface Scanner UI.
/// Validates BurnProgressViewModel telemetry and log drawer, DriveUtilitiesViewModel rewritable blanking,
/// bitsetting and surface benchmarking, VirtualDriveViewModel presets and fault injection, and SettingsViewModel persistence.
/// </summary>
public sealed class ProgressAndUtilitiesViewModelTests : IDisposable
{
    private readonly DiscDeviceManager _deviceManager;
    private readonly VirtualBurnEngine _burnEngine;

    public ProgressAndUtilitiesViewModelTests()
    {
        _deviceManager = new DiscDeviceManager(enablePhysicalEnumeration: false);
        _burnEngine = new VirtualBurnEngine();
    }

    public void Dispose()
    {
        _deviceManager.Dispose();
    }

    #region 1. BurnProgressViewModel Tests

    [Fact]
    public void BurnProgressViewModel_ApplySnapshot_UpdatesPercentage_LbaText_SpeedText_BufferPercentage_And_AppendsActionTransitionsToLiveLogLines()
    {
        // Arrange
        var vm = new BurnProgressViewModel();
        vm.Reset("ISO 9660 Data Mastering");

        Assert.Equal(0.0, vm.PercentComplete);
        Assert.Equal("0 / 0", vm.LbaText);
        Assert.Equal("0.0x", vm.CurrentSpeedText);
        Assert.Equal(0.0, vm.BufferFillPercent);
        Assert.Single(vm.LiveLogLines); // initial job started line

        // Act 1: Apply first telemetry snapshot
        var snapshot1 = new BurnProgressSnapshot
        {
            ElapsedTime = TimeSpan.FromSeconds(25),
            RemainingTime = TimeSpan.FromSeconds(75),
            CurrentLba = 50_000,
            TotalLba = 200_000,
            BufferLevelPercent = 94.5,
            CurrentSpeedMultiplier = 8.0,
            CurrentBytesPerSecond = 8 * 176_400,
            CurrentActionDescription = "Writing Track 1: ISO Data Payload"
        };
        vm.ApplySnapshot(snapshot1);

        // Assert 1: Validates percentage, LBA text, speed text, buffer percentage, and live logs
        Assert.Equal(25.0, vm.PercentComplete, 1);
        Assert.Equal(50_000, vm.CurrentLba);
        Assert.Equal(200_000, vm.TotalSectors);
        Assert.Contains("50,000", vm.LbaText);
        Assert.Contains("200,000", vm.LbaText);
        Assert.Contains("8.0x", vm.CurrentSpeedText);
        Assert.Equal(94.5, vm.BufferFillPercent, 1);
        Assert.Equal("00:25", vm.ElapsedTimeText);
        Assert.Equal("01:15", vm.RemainingTimeText);
        Assert.Equal("Writing Track 1: ISO Data Payload", vm.CurrentActionText);
        Assert.Contains("Writing Track 1: ISO Data Payload", vm.LiveLogLines);

        // Act 2: Apply second telemetry snapshot with a new action description transition
        var snapshot2 = new BurnProgressSnapshot
        {
            ElapsedTime = TimeSpan.FromSeconds(50),
            RemainingTime = TimeSpan.FromSeconds(50),
            CurrentLba = 100_000,
            TotalLba = 200_000,
            BufferLevelPercent = 88.0,
            CurrentSpeedMultiplier = 12.0,
            CurrentBytesPerSecond = 12 * 176_400,
            CurrentActionDescription = "Flushing Hardware Buffer & Cache"
        };
        vm.ApplySnapshot(snapshot2);

        // Assert 2
        Assert.Equal(50.0, vm.PercentComplete, 1);
        Assert.Equal(100_000, vm.CurrentLba);
        Assert.Contains("100,000", vm.LbaText);
        Assert.Contains("12.0x", vm.CurrentSpeedText);
        Assert.Equal(88.0, vm.BufferFillPercent, 1);
        Assert.Equal("Flushing Hardware Buffer & Cache", vm.CurrentActionText);
        Assert.Contains("Flushing Hardware Buffer & Cache", vm.LiveLogLines);

        // Act 3: Apply third snapshot with identical action description (should NOT duplicate log line)
        int logCountBefore = vm.LiveLogLines.Count;
        var snapshot3 = new BurnProgressSnapshot
        {
            ElapsedTime = TimeSpan.FromSeconds(55),
            RemainingTime = TimeSpan.FromSeconds(45),
            CurrentLba = 110_000,
            TotalLba = 200_000,
            BufferLevelPercent = 85.0,
            CurrentSpeedMultiplier = 12.0,
            CurrentBytesPerSecond = 12 * 176_400,
            CurrentActionDescription = "Flushing Hardware Buffer & Cache" // same description
        };
        vm.ApplySnapshot(snapshot3);

        // Assert 3
        Assert.Equal(logCountBefore, vm.LiveLogLines.Count);
    }

    [Fact]
    public void BurnProgressViewModel_CancelBurnCommand_TriggersCancellationTokenAndUpdatesStatus()
    {
        // Arrange
        var vm = new BurnProgressViewModel();
        using var cts = new CancellationTokenSource();
        vm.Reset("Test Mastering", cts);

        Assert.True(vm.CanCancel);
        Assert.True(vm.IsRunning);
        Assert.False(cts.IsCancellationRequested);

        // Act: Execute CancelBurnCommand
        vm.CancelBurnCommand.Execute(null);

        // Assert
        Assert.True(cts.IsCancellationRequested);
        Assert.False(vm.CanCancel);
        Assert.Contains("Cancellation requested", vm.CurrentActionText);
        Assert.Contains(vm.LiveLogLines, line => line.Contains("cancellation", StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region 2. DriveUtilitiesViewModel Tests

    [Fact]
    public async Task DriveUtilitiesViewModel_Executes_EraseDiscCommand_On_VDRV01()
    {
        // Arrange
        var vrec = _deviceManager.GetRecorderById("VDRV-01") as VirtualDiscRecorder;
        Assert.NotNull(vrec);

        // Insert a written CD-RW rewritable medium
        using var medium = VirtualDiscMedium.CreateBlankCdRw700();
        byte[] dummy = new byte[OpticalConstants.DataSectorSize];
        medium.WriteSector(0, dummy);
        medium.UsedSectors = 64;
        medium.IsBlank = false;
        vrec.InsertMedia(medium);

        var vm = new DriveUtilitiesViewModel(_deviceManager, _burnEngine);
        vm.SelectedRecorder = vrec;
        vm.IsFullEraseSelected = false; // Quick Erase

        // Verify initial state
        Assert.NotNull(vm.MediaDetails);
        Assert.False(vm.MediaDetails.IsBlank);
        Assert.True(vm.MediaDetails.IsRewritable);

        // Act: Execute EraseDiscCommand
        await vm.EraseDiscCommand.ExecuteAsync(null);

        // Assert
        Assert.NotNull(vrec.CurrentMedium);
        Assert.True(vrec.CurrentMedium.IsBlank);
        Assert.Equal(0, vrec.CurrentMedium.UsedSectors);
        Assert.NotNull(vm.MediaDetails);
        Assert.True(vm.MediaDetails.IsBlank);
        Assert.Contains("completed successfully", vm.StatusMessage);
    }

    [Fact]
    public async Task DriveUtilitiesViewModel_Executes_ApplyBookTypeCommand_On_VDRV01()
    {
        // Arrange
        var vrec = _deviceManager.GetRecorderById("VDRV-01") as VirtualDiscRecorder;
        Assert.NotNull(vrec);

        // Insert a DVD+R DL medium that supports bitsetting
        using var medium = VirtualDiscMedium.CreateBlankDvdPlusRDL85();
        vrec.InsertMedia(medium);

        var vm = new DriveUtilitiesViewModel(_deviceManager, _burnEngine);
        vm.SelectedRecorder = vrec;
        vm.SelectedBookType = DvdBookType.DvdRom;

        // Act: Execute ApplyBookTypeCommand
        await vm.ApplyBookTypeCommand.ExecuteAsync(null);

        // Assert
        Assert.NotNull(vrec.CurrentMedium);
        Assert.Equal(DvdBookType.DvdRom, vrec.CurrentMedium.BookType);
        Assert.Contains("DvdRom", vm.StatusMessage);
    }

    [Fact]
    public async Task DriveUtilitiesViewModel_Executes_StartSurfaceScanCommand_On_VDRV01_And_Populates_ScanBlocks_And_LatestScanReport()
    {
        // Arrange
        var vrec = _deviceManager.GetRecorderById("VDRV-01") as VirtualDiscRecorder;
        Assert.NotNull(vrec);

        // Insert a virtual medium with 512 sectors and an injected bad sector at LBA 300
        using var medium = new VirtualDiscMedium(DiscMediaType.CdR, 512, isBlank: false)
        {
            UsedSectors = 512
        };
        medium.SimulatedBadLbas.Add(300);
        vrec.InsertMedia(medium);

        var vm = new DriveUtilitiesViewModel(_deviceManager, _burnEngine);
        vm.SelectedRecorder = vrec;
        vm.ScanBlockSizeSectors = 128; // 512 / 128 = 4 blocks

        Assert.Empty(vm.ScanBlocks);
        Assert.Null(vm.LatestScanReport);

        // Act: Execute StartSurfaceScanCommand
        await vm.StartSurfaceScanCommand.ExecuteAsync(null);

        // Assert: ScanBlocks and LatestScanReport are populated
        Assert.NotEmpty(vm.ScanBlocks);
        Assert.Equal(4, vm.ScanBlocks.Count);

        Assert.NotNull(vm.LatestScanReport);
        Assert.Equal(512, vm.LatestScanReport.TotalSectorsScanned);
        Assert.Equal(3, vm.LatestScanReport.GoodBlocks);
        Assert.Equal(1, vm.LatestScanReport.DamagedBlocks);

        // Block index 2 covers LBAs 256..383, containing bad sector 300
        Assert.Equal(SectorHealthStatus.Good, vm.ScanBlocks[0].Status);
        Assert.Equal(SectorHealthStatus.Good, vm.ScanBlocks[1].Status);
        Assert.Equal(SectorHealthStatus.Damaged, vm.ScanBlocks[2].Status);
        Assert.Equal(SectorHealthStatus.Good, vm.ScanBlocks[3].Status);

        Assert.Contains("Surface scan finished", vm.StatusMessage);
    }

    #endregion

    #region 3. VirtualDriveViewModel Tests

    [Fact]
    public void VirtualDriveViewModel_Can_InjectSimulatedBadLBAs_And_ClearThem()
    {
        // Arrange
        var vm = new VirtualDriveViewModel(_deviceManager);
        vm.MountCdRCommand.Execute(null);

        Assert.True(vm.HasMedia);
        Assert.NotNull(vm.CurrentMedium);
        Assert.Empty(vm.SimulatedBadLbas);

        // Act 1: Inject bad LBA 450
        vm.NewBadLba = 450;
        vm.InjectBadLbaCommand.Execute(null);

        // Assert 1
        Assert.Contains(450, vm.SimulatedBadLbas);
        Assert.Contains(450, vm.CurrentMedium!.SimulatedBadLbas);

        // Act 2: Inject bad LBA 1024 directly
        vm.InjectBadLba(1024);

        // Assert 2
        Assert.Equal(2, vm.SimulatedBadLbas.Count);
        Assert.Contains(1024, vm.SimulatedBadLbas);

        // Act 3: Remove bad LBA 450
        vm.RemoveBadLba(450);

        // Assert 3
        Assert.DoesNotContain(450, vm.SimulatedBadLbas);
        Assert.Contains(1024, vm.SimulatedBadLbas);

        // Act 4: Clear all bad LBAs
        vm.ClearBadLbasCommand.Execute(null);

        // Assert 4
        Assert.Empty(vm.SimulatedBadLbas);
        Assert.Empty(vm.CurrentMedium.SimulatedBadLbas);
    }

    [Fact]
    public void VirtualDriveViewModel_Can_MountVirtualMediaPresets()
    {
        // Arrange
        var vm = new VirtualDriveViewModel(_deviceManager);

        // Act 1: Mount blank CD-R 700 MB
        vm.MountPreset("CD-R");
        Assert.True(vm.HasMedia);
        Assert.Equal(DiscMediaType.CdR, vm.MediaType);
        Assert.Equal(OpticalConstants.Cd700MbSectors, vm.TotalCapacitySectors);

        // Act 2: Mount blank DVD-R 4.7 GB
        vm.MountPreset("DVD-R");
        Assert.True(vm.HasMedia);
        Assert.Equal(DiscMediaType.DvdMinusR, vm.MediaType);
        Assert.Equal(OpticalConstants.DvdMinusR5Sectors, vm.TotalCapacitySectors);

        // Act 3: Mount blank DVD+R DL 8.5 GB
        vm.MountPreset("DVD+RDL");
        Assert.True(vm.HasMedia);
        Assert.Equal(DiscMediaType.DvdPlusRDualLayer, vm.MediaType);
        Assert.Equal(OpticalConstants.DvdPlusR9Sectors, vm.TotalCapacitySectors);

        // Act 4: Eject and Close Tray
        vm.EjectMediaCommand.Execute(null);
        Assert.False(vm.HasMedia);
        Assert.True(vm.IsTrayOpen);

        vm.CloseTrayCommand.Execute(null);
        Assert.True(vm.HasMedia);
        Assert.False(vm.IsTrayOpen);
    }

    #endregion

    #region 4. SettingsViewModel & MainViewModel Integration Tests

    [Fact]
    public void SettingsViewModel_SavesAndResetsSettings()
    {
        // Arrange: Custom settings file path
        string tempSettingsFile = Path.Combine(Path.GetTempPath(), $"cdburner_test_settings_{Guid.NewGuid():N}.json");
        try
        {
            var service = new AppSettingsService(tempSettingsFile);
            var vm = new SettingsViewModel(service);

            // Act 1: Modify settings and save
            vm.DefaultWriteSpeedMultiplier = 24;
            vm.VerifyAfterBurnByDefault = false;
            Assert.False(vm.StrictVerify);
            vm.StrictVerify = true;
            vm.ThemeMode = ThemeMode.Dark;
            vm.SaveSettingsCommand.Execute(null);

            // Assert 1: Persistent settings updated
            var reloadedService = new AppSettingsService(tempSettingsFile);
            reloadedService.Load();
            Assert.Equal(24, reloadedService.CurrentSettings.DefaultWriteSpeedMultiplier);
            Assert.False(reloadedService.CurrentSettings.VerifyAfterBurnByDefault);
            Assert.True(reloadedService.CurrentSettings.StrictVerify);
            Assert.Equal(ThemeMode.Dark, reloadedService.CurrentSettings.ThemeMode);

            // Act 2: Reset to defaults
            vm.ResetSettingsCommand.Execute(null);

            // Assert 2: Reset back to defaults
            Assert.Equal(CdBurner.Core.Primitives.WriteSpeedPolicy.Safest, vm.DefaultWriteSpeedMultiplier);
            Assert.True(vm.VerifyAfterBurnByDefault);
            Assert.False(vm.StrictVerify);
            Assert.Equal(ThemeMode.System, vm.ThemeMode);
        }
        finally
        {
            if (File.Exists(tempSettingsFile))
            {
                File.Delete(tempSettingsFile);
            }
        }
    }

    [Fact]
    public void MainViewModel_WiresAllSevenWorkspaces_ProperlyConfigured()
    {
        // Arrange
        using var mainVm = new MainViewModel(_deviceManager, _burnEngine);

        // Assert all 7 workspace ViewModels are exposed
        Assert.NotNull(mainVm.DataBurn);
        Assert.NotNull(mainVm.AudioBurn);
        Assert.NotNull(mainVm.DvdVideo);
        Assert.NotNull(mainVm.ImageStudio);
        Assert.NotNull(mainVm.DriveUtilities);
        Assert.NotNull(mainVm.VirtualDrive);
        Assert.NotNull(mainVm.Settings);

        // Act & Assert navigation to DriveUtilities
        mainVm.NavigateCommand.Execute(NavigationTab.DriveUtilities);
        Assert.Equal(NavigationTab.DriveUtilities, mainVm.SelectedNavigationTab);
        Assert.NotNull(mainVm.CurrentWorkspaceViewModel);
        var driveWorkspace = Assert.IsType<WorkspaceViewModel>(mainVm.CurrentWorkspaceViewModel);
        Assert.Equal(mainVm.DriveUtilities, driveWorkspace.Content);

        // Act & Assert navigation to VirtualDrive
        mainVm.NavigateCommand.Execute(NavigationTab.VirtualDrive);
        Assert.Equal(NavigationTab.VirtualDrive, mainVm.SelectedNavigationTab);
        var vdrvWorkspace = Assert.IsType<WorkspaceViewModel>(mainVm.CurrentWorkspaceViewModel);
        Assert.Equal(mainVm.VirtualDrive, vdrvWorkspace.Content);

        // Act & Assert navigation to Settings
        mainVm.NavigateCommand.Execute(NavigationTab.Settings);
        Assert.Equal(NavigationTab.Settings, mainVm.SelectedNavigationTab);
        var settingsWorkspace = Assert.IsType<WorkspaceViewModel>(mainVm.CurrentWorkspaceViewModel);
        Assert.Equal(mainVm.Settings, settingsWorkspace.Content);
    }

    [Fact]
    public void ThemeManager_And_SettingsViewModel_ThemeSwitching_UpdatesModeAndProperties()
    {
        // Arrange
        var vm = new SettingsViewModel();
        Assert.Equal(ThemeMode.System, vm.ThemeMode);
        Assert.Contains(ThemeMode.Light, vm.AvailableThemes);
        Assert.Contains(ThemeMode.Dark, vm.AvailableThemes);
        Assert.Contains(ThemeMode.System, vm.AvailableThemes);

        // Act 1: Switch to Light mode
        vm.ThemeMode = ThemeMode.Light;
        Assert.Equal(ThemeMode.Light, vm.ThemeMode);
        Assert.Equal(ThemeMode.Light, CdBurner.App.Themes.ThemeManager.CurrentMode);
        Assert.True(CdBurner.App.Themes.ThemeManager.CurrentIsLight);

        // Act 2: Switch to Dark mode
        vm.ThemeMode = ThemeMode.Dark;
        Assert.Equal(ThemeMode.Dark, vm.ThemeMode);
        Assert.Equal(ThemeMode.Dark, CdBurner.App.Themes.ThemeManager.CurrentMode);
        Assert.False(CdBurner.App.Themes.ThemeManager.CurrentIsLight);

        // Act 3: Switch to System mode
        vm.ThemeMode = ThemeMode.System;
        Assert.Equal(ThemeMode.System, vm.ThemeMode);
        Assert.Equal(ThemeMode.System, CdBurner.App.Themes.ThemeManager.CurrentMode);
    }

    [Fact]
    public void MainViewModel_DefaultWriteSpeed_IsTheSlowestOffered_AndAChosenSpeedIsKept()
    {
        using var mainVm = new MainViewModel(_deviceManager, _burnEngine);

        var virtualCd = mainVm.AvailableRecorders.FirstOrDefault(r => r.UniqueId == "VDRV-01");
        Assert.NotNull(virtualCd);
        mainVm.SelectedRecorder = virtualCd;

        // Default: the slowest CD speed the virtual drive offers (8x of 8x..48x), so a first burn takes the cautious choice.
        Assert.NotEmpty(mainVm.AvailableWriteSpeeds);
        Assert.NotNull(mainVm.SelectedWriteSpeed);
        Assert.Equal(8.0, mainVm.SelectedWriteSpeed.Value.Multiplier);

        // A speed chosen in Settings is used instead.
        mainVm.Settings.DefaultWriteSpeedMultiplier = 16;
        mainVm.SelectDefaultWriteSpeed();
        Assert.Equal(16.0, mainVm.SelectedWriteSpeed!.Value.Multiplier);

        mainVm.Settings.DefaultWriteSpeedMultiplier = CdBurner.Core.Primitives.WriteSpeedPolicy.Maximum;
        mainVm.SelectDefaultWriteSpeed();
        Assert.Equal(48.0, mainVm.SelectedWriteSpeed!.Value.Multiplier);
    }

    #endregion
}
