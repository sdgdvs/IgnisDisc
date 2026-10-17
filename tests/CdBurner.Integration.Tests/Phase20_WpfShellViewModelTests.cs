using System.Windows;
using System.Windows.Media;
using CdBurner.App.Converters;
using CdBurner.App.Mvvm;
using CdBurner.App.ViewModels;
using CdBurner.Core.Compilation;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// Unit and integration tests for Phase 20: WPF Windows 11 Fluent Shell, Navigation & Drive Selector Bar.
/// Validates MainViewModel, MVVM RelayCommands, virtual media mounting, module tab navigation,
/// capacity metrics assessment, and WPF value converters.
/// </summary>
public sealed class Phase20_WpfShellViewModelTests : IDisposable
{
    private readonly DiscDeviceManager _deviceManager;
    private readonly VirtualBurnEngine _burnEngine;
    private readonly MainViewModel _viewModel;

    public Phase20_WpfShellViewModelTests()
    {
        _deviceManager = new DiscDeviceManager(enablePhysicalEnumeration: false);
        _burnEngine = new VirtualBurnEngine();
        _viewModel = new MainViewModel(_deviceManager, _burnEngine);
    }

    public void Dispose()
    {
        _viewModel.Dispose();
        _deviceManager.Dispose();
    }

    [Fact]
    public void RefreshDrivesCommand_PopulatesAvailableRecorders_And_SelectsVdrv01()
    {
        // Act: Execute RefreshDrivesCommand
        _viewModel.RefreshDrivesCommand.Execute(null);

        // Assert: AvailableRecorders is populated and VDRV-01 is selected
        Assert.NotEmpty(_viewModel.AvailableRecorders);
        Assert.Contains(_viewModel.AvailableRecorders, r => string.Equals(r.UniqueId, "VDRV-01", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(_viewModel.SelectedRecorder);
        Assert.Equal("VDRV-01", _viewModel.SelectedRecorder.UniqueId);
        Assert.True(_viewModel.IsDriveVirtual);
        Assert.Contains("IGNIS", _viewModel.DriveStatusSummary);
    }

    [Fact]
    public void MountVirtualBlankCdRCommand_UpdatesCurrentMediaStatus_To_CdR_360000FreeSectors()
    {
        // Arrange
        _viewModel.RefreshDrivesCommand.Execute(null);

        // Act: Mount blank CD-R 700 MB
        _viewModel.MountVirtualBlankCdRCommand.Execute(null);

        // Assert
        Assert.NotNull(_viewModel.CurrentMediaStatus);
        Assert.True(_viewModel.CurrentMediaStatus.MediaPresent);
        Assert.True(_viewModel.CurrentMediaStatus.IsBlank);
        Assert.Equal(DiscMediaType.CdR, _viewModel.CurrentMediaStatus.MediaType);
        Assert.Equal(OpticalConstants.Cd700MbSectors, _viewModel.CurrentMediaStatus.FreeSectors);
        Assert.Equal(360_000, _viewModel.CurrentMediaStatus.FreeSectors);
        Assert.Contains("CD-R", _viewModel.MediaBadgeText);

        // Verify Capacity Assessment is aligned
        Assert.NotNull(_viewModel.CurrentCapacity);
        Assert.Equal(OpticalConstants.Cd700MbSectors, _viewModel.CurrentCapacity.MediaMaxSectors);
        Assert.Equal(0, _viewModel.CurrentCapacity.TotalSectors);
        Assert.Equal(CapacityStatus.FitsComfortably, _viewModel.CurrentCapacity.Status);
    }

    [Fact]
    public void MountVirtualBlankDvdRCommand_UpdatesCurrentMediaStatus_To_DvdMinusR()
    {
        // Arrange
        _viewModel.RefreshDrivesCommand.Execute(null);

        // Act: Mount blank DVD-R 4.7 GB
        _viewModel.MountVirtualBlankDvdRCommand.Execute(null);

        // Assert
        Assert.NotNull(_viewModel.CurrentMediaStatus);
        Assert.True(_viewModel.CurrentMediaStatus.MediaPresent);
        Assert.True(_viewModel.CurrentMediaStatus.IsBlank);
        Assert.Equal(DiscMediaType.DvdMinusR, _viewModel.CurrentMediaStatus.MediaType);
        Assert.Equal(OpticalConstants.DvdMinusR5Sectors, _viewModel.CurrentMediaStatus.FreeSectors);
        Assert.Equal(2_298_496, _viewModel.CurrentMediaStatus.FreeSectors);
        Assert.Contains("DVD-R", _viewModel.MediaBadgeText);

        // Verify Capacity Assessment is aligned
        Assert.NotNull(_viewModel.CurrentCapacity);
        Assert.Equal(OpticalConstants.DvdMinusR5Sectors, _viewModel.CurrentCapacity.MediaMaxSectors);
    }

    [Fact]
    public void MountVirtualBlankCdRwCommand_UpdatesCurrentMediaStatus_To_CdRw()
    {
        // Arrange
        _viewModel.RefreshDrivesCommand.Execute(null);

        // Act: Mount blank CD-RW
        _viewModel.MountVirtualBlankCdRwCommand.Execute(null);

        // Assert
        Assert.NotNull(_viewModel.CurrentMediaStatus);
        Assert.True(_viewModel.CurrentMediaStatus.MediaPresent);
        Assert.True(_viewModel.CurrentMediaStatus.IsRewritable);
        Assert.Equal(DiscMediaType.CdRw, _viewModel.CurrentMediaStatus.MediaType);
        Assert.Equal(360_000, _viewModel.CurrentMediaStatus.FreeSectors);
    }

    [Theory]
    [InlineData(NavigationTab.DataDisc)]
    [InlineData(NavigationTab.AudioCd)]
    [InlineData(NavigationTab.DvdVideo)]
    [InlineData(NavigationTab.ImageStudio)]
    [InlineData(NavigationTab.DriveUtilities)]
    [InlineData(NavigationTab.VirtualDrive)]
    [InlineData(NavigationTab.Settings)]
    public void NavigateCommand_SwitchesSelectedNavigationTab_AcrossAll7TabsCleanly(NavigationTab targetTab)
    {
        // Act: Execute NavigateCommand with target tab
        _viewModel.NavigateCommand.Execute(targetTab);

        // Assert: Navigation property updated and corresponding workspace model activated
        Assert.Equal(targetTab, _viewModel.SelectedNavigationTab);
        Assert.NotNull(_viewModel.CurrentWorkspaceViewModel);

        var workspace = Assert.IsType<WorkspaceViewModel>(_viewModel.CurrentWorkspaceViewModel);
        Assert.Equal(targetTab, workspace.Tab);
        Assert.False(string.IsNullOrWhiteSpace(workspace.Title));
        Assert.False(string.IsNullOrWhiteSpace(workspace.Description));
    }

    [Fact]
    public void NavigateCommand_AcceptsStringAndIntegerParameters()
    {
        // Test string parameter
        _viewModel.NavigateCommand.Execute("AudioCd");
        Assert.Equal(NavigationTab.AudioCd, _viewModel.SelectedNavigationTab);

        _viewModel.NavigateCommand.Execute("ImageStudio");
        Assert.Equal(NavigationTab.ImageStudio, _viewModel.SelectedNavigationTab);

        // Test int parameter
        _viewModel.NavigateCommand.Execute((int)NavigationTab.VirtualDrive);
        Assert.Equal(NavigationTab.VirtualDrive, _viewModel.SelectedNavigationTab);
    }

    [Fact]
    public void EjectTrayCommand_And_CloseTrayCommand_ToggleMediaPresence()
    {
        // Arrange: Mount medium
        _viewModel.RefreshDrivesCommand.Execute(null);
        _viewModel.MountVirtualBlankCdRCommand.Execute(null);
        Assert.True(_viewModel.CurrentMediaStatus.MediaPresent);

        // Act: Eject
        _viewModel.EjectTrayCommand.Execute(null);

        // Assert: Tray open, media inaccessible
        Assert.False(_viewModel.CurrentMediaStatus.MediaPresent);
        Assert.Equal("Empty Tray", _viewModel.MediaBadgeText);

        // Act: Close tray
        _viewModel.CloseTrayCommand.Execute(null);

        // Assert: Media restored
        Assert.True(_viewModel.CurrentMediaStatus.MediaPresent);
        Assert.Equal(DiscMediaType.CdR, _viewModel.CurrentMediaStatus.MediaType);
    }

    [Fact]
    public void ObservableObject_RaisesPropertyChanged_Correctly()
    {
        var testObj = new TestObservable();
        var raisedProps = new List<string?>();
        testObj.PropertyChanged += (s, e) => raisedProps.Add(e.PropertyName);

        testObj.TextValue = "New Value";
        Assert.Contains(nameof(testObj.TextValue), raisedProps);

        // Setting same value should not raise event
        raisedProps.Clear();
        testObj.TextValue = "New Value";
        Assert.Empty(raisedProps);
    }

    [Fact]
    public async Task RelayCommand_And_AsyncRelayCommand_Execute_And_TrackState()
    {
        // Synchronous RelayCommand
        bool syncExecuted = false;
        var syncCmd = new RelayCommand(() => syncExecuted = true, () => true);
        Assert.True(syncCmd.CanExecute(null));
        syncCmd.Execute(null);
        Assert.True(syncExecuted);

        // Parameterized RelayCommand<T>
        int receivedVal = 0;
        var paramCmd = new RelayCommand<int>(val => receivedVal = val, val => val > 0);
        Assert.False(paramCmd.CanExecute(0));
        Assert.True(paramCmd.CanExecute(42));
        paramCmd.Execute(42);
        Assert.Equal(42, receivedVal);

        // Asynchronous RelayCommand
        bool asyncRan = false;
        var asyncCmd = new AsyncRelayCommand(async () =>
        {
            await Task.Delay(10);
            asyncRan = true;
        });

        Assert.False(asyncCmd.IsRunning);
        var execTask = asyncCmd.ExecuteAsync(null);
        Assert.True(asyncCmd.IsRunning);
        await execTask;
        Assert.False(asyncCmd.IsRunning);
        Assert.True(asyncRan);
    }

    [Fact]
    public void Converters_ConvertCorrectly()
    {
        // NavigationTabToBooleanConverter
        var navConverter = NavigationTabToBooleanConverter.Instance;
        Assert.True((bool)navConverter.Convert(NavigationTab.AudioCd, typeof(bool), NavigationTab.AudioCd, null!));
        Assert.False((bool)navConverter.Convert(NavigationTab.AudioCd, typeof(bool), NavigationTab.DataDisc, null!));
        Assert.True((bool)navConverter.Convert(NavigationTab.AudioCd, typeof(bool), "AudioCd", null!));

        // CapacityStatusToBrushConverter
        var capConverter = CapacityStatusToBrushConverter.Instance;
        var safeBrush = capConverter.Convert(CapacityStatus.FitsComfortably, typeof(Brush), null, null!);
        Assert.NotNull(safeBrush);

        var warnBrush = capConverter.Convert(CapacityStatus.NearCapacityWarning, typeof(Brush), null, null!);
        Assert.NotNull(warnBrush);

        var overburnBrush = capConverter.Convert(CapacityStatus.OverburnRequired, typeof(Brush), null, null!);
        Assert.NotNull(overburnBrush);

        // MediaPresenceToBrushConverter
        var mediaConverter = MediaPresenceToBrushConverter.Instance;
        var presentBrush = mediaConverter.Convert(true, typeof(Brush), null, null!);
        Assert.NotNull(presentBrush);
        var emptyBrush = mediaConverter.Convert(false, typeof(Brush), null, null!);
        Assert.NotNull(emptyBrush);

        // BooleanToVisibilityConverter
        var boolConverter = BooleanToVisibilityConverter.Instance;
        Assert.Equal(Visibility.Visible, boolConverter.Convert(true, typeof(Visibility), null, null!));
        Assert.Equal(Visibility.Collapsed, boolConverter.Convert(false, typeof(Visibility), null, null!));
    }

    private class TestObservable : ObservableObject
    {
        private string _textValue = string.Empty;

        public string TextValue
        {
            get => _textValue;
            set => SetProperty(ref _textValue, value);
        }
    }
}
