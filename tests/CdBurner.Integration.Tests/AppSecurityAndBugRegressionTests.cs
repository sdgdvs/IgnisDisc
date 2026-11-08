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

        // If another test left a live WPF UI thread behind, the refresh is marshalled to it; give it a moment.
        for (int i = 0; i < 50 && mainVm.AvailableRecorders.Count < 2; i++)
        {
            Thread.Sleep(20);
        }

        Assert.Equal(2, mainVm.AvailableRecorders.Count);
        Assert.Contains(mainVm.AvailableRecorders, r => r.UniqueId == "PHYS-01");
    }

    [Fact]
    public void MainViewModel_OnDeviceChanged_WithApplicationWhoseUiThreadHasExited_StillReloadsRecorders()
    {
        // Reproduce the state another test leaves behind: a WPF Application created on an STA thread that has exited.
        // Its dispatcher never runs again, so marshalling the refresh there would drop it forever.
        if (System.Windows.Application.Current == null)
        {
            var sta = new Thread(() =>
            {
                if (System.Windows.Application.Current == null)
                {
                    // Same setup as the WPF acceptance test (one Application per process, with the app's resources).
                    var app = new CdBurner.App.App();
                    app.InitializeComponent();
                }
            });
            sta.SetApartmentState(ApartmentState.STA);
            sta.Start();
            sta.Join();
        }

        var dispatcher = System.Windows.Application.Current!.Dispatcher;
        if (dispatcher.Thread.IsAlive)
        {
            return; // a live UI thread exists in this test run; the dead-thread case cannot be staged here
        }

        var fakeService = new DynamicFakeDeviceService();
        using var virtualDrive = new VirtualDiscRecorder("VDRV-01", @"V:\");
        fakeService.AddRecorder(virtualDrive);
        using var mainVm = new MainViewModel(fakeService, new VirtualBurnEngine());

        using var secondDrive = new VirtualDiscRecorder("PHYS-02", @"F:\");
        fakeService.AddRecorder(secondDrive, eventUniqueId: string.Empty);

        Assert.Equal(2, mainVm.AvailableRecorders.Count); // refreshed immediately, not queued to a dead thread
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
