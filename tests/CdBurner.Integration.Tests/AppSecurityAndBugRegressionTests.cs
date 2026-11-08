using CdBurner.App.Mvvm;
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
[Collection(WpfApplicationCollection.Name)]
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
    public void UiDispatch_NeverMarshalsToADispatcherWhoseThreadHasExited()
    {
        // A dispatcher created on a thread that has since exited (what a WPF test leaves behind) never runs again:
        // marshalling a drive-list refresh to it would drop the refresh forever.
        System.Windows.Threading.Dispatcher? deadDispatcher = null;
        var shortLived = new Thread(() => deadDispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher);
        shortLived.Start();
        shortLived.Join();
        Assert.False(UiDispatch.ShouldMarshalTo(deadDispatcher));

        // No dispatcher, or already on the dispatcher's own thread: run inline.
        Assert.False(UiDispatch.ShouldMarshalTo(null));
        Assert.False(UiDispatch.ShouldMarshalTo(System.Windows.Threading.Dispatcher.CurrentDispatcher));

        // A live UI thread on another thread: marshal.
        System.Windows.Threading.Dispatcher? liveDispatcher = null;
        using var ready = new ManualResetEventSlim();
        var uiThread = new Thread(() =>
        {
            liveDispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            ready.Set();
            System.Windows.Threading.Dispatcher.Run();
        });
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();
        ready.Wait();
        try
        {
            Assert.True(UiDispatch.ShouldMarshalTo(liveDispatcher));
        }
        finally
        {
            liveDispatcher!.InvokeShutdown();
            uiThread.Join();
        }
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
