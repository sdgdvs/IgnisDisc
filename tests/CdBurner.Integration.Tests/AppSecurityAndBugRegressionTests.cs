using CdBurner.App.ViewModels;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Integration.Tests;

/// <summary>
/// Regression tests verifying WPF ViewModel fixes (CRIT-02 physical drive list reload on DeviceChangeType.Added,
/// DvdVideoViewModel & ImageStudioViewModel CancelCommand and SelectedWriteSpeed propagation).
/// </summary>
public sealed class AppSecurityAndBugRegressionTests
{
    private sealed class DynamicFakeDeviceService : IDiscDeviceService
    {
        private readonly List<IDiscRecorderBackend> _recorders = [];

        public event EventHandler<DeviceChangeEventArgs>? DeviceChanged;

        public void AddRecorder(IDiscRecorderBackend recorder, string eventUniqueId = "")
        {
            _recorders.Add(recorder);
            DeviceChanged?.Invoke(this, new DeviceChangeEventArgs(eventUniqueId, DeviceChangeType.Added));
        }

        public IReadOnlyList<IDiscRecorderBackend> GetRecorders(bool includeVirtual = true)
            => _recorders.ToList();

        public IDiscRecorderBackend? GetRecorderById(string uniqueId)
            => _recorders.FirstOrDefault(r => string.Equals(r.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MainViewModel_OnDeviceChanged_WhenAddedWithEmptyUniqueId_ReloadsAvailableRecorders()
    {
        var fakeService = new DynamicFakeDeviceService();
        using var virtualDrive = new VirtualDiscRecorder("VDRV-01", @"V:\");
        fakeService.AddRecorder(virtualDrive);

        using var mainVm = new MainViewModel(fakeService, new VirtualBurnEngine());
        Assert.Single(mainVm.AvailableRecorders);

        // Simulate asynchronous physical drive discovery firing DeviceChangeType.Added with empty UniqueId
        using var secondDrive = new VirtualDiscRecorder("PHYS-01", @"E:\");
        fakeService.AddRecorder(secondDrive, eventUniqueId: string.Empty);

        Assert.Equal(2, mainVm.AvailableRecorders.Count);
        Assert.Contains(mainVm.AvailableRecorders, r => r.UniqueId == "PHYS-01");
    }

    [Fact]
    public void MainViewModel_SelectedWriteSpeed_PropagatesToDvdVideoAndImageStudioAndExposesCancelCommands()
    {
        var fakeService = new DynamicFakeDeviceService();
        using var virtualDrive = new VirtualDiscRecorder("VDRV-01", @"V:\");
        fakeService.AddRecorder(virtualDrive);

        using var mainVm = new MainViewModel(fakeService, new VirtualBurnEngine());
        mainVm.MountVirtualBlankDvdRCommand.Execute(null);

        // Instantiate workspaces
        var dvdVm = mainVm.DvdVideo;
        var imageVm = mainVm.ImageStudio;

        Assert.NotNull(dvdVm.CancelCommand);
        Assert.NotNull(imageVm.CancelCommand);

        var speed8x = WriteSpeedDescriptor.FromMultiplier(8.0, DiscMediaType.DvdMinusR);
        mainVm.SelectedWriteSpeed = speed8x;

        Assert.Equal(speed8x, dvdVm.SelectedWriteSpeed);
        Assert.Equal(speed8x, imageVm.SelectedWriteSpeed);
    }
}
