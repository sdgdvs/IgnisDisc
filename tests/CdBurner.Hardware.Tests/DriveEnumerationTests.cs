using System.Runtime.InteropServices;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Interop;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// Unit tests for Drive Enumeration, Capabilities & PnP Device Monitor.
/// Verifies physical IMAPIv2 and virtual drive enumeration, capability discovery,
/// PnP device arrival and removal events, and Win32 WM_DEVICECHANGE bitmask decoding.
/// </summary>
public class DriveEnumerationTests
{
    #region DiscDeviceManager Enumeration & Virtual Drive Registry Tests

    [Fact]
    public void GetRecorders_IncludeVirtualTrue_AlwaysReturnsDefaultVirtualRecorder()
    {
        // 1. Verify DiscDeviceManager.GetRecorders(includeVirtual: true) always returns
        // at least the default "VDRV-01" virtual recorder even without physical drives.
        using var manager = new DiscDeviceManager(enablePhysicalEnumeration: false);

        var recorders = manager.GetRecorders(includeVirtual: true);

        Assert.NotEmpty(recorders);
        var defaultVirtual = Assert.Single(recorders);
        Assert.Equal("VDRV-01", defaultVirtual.UniqueId);
        Assert.Equal("IGNIS", defaultVirtual.VendorId);
        Assert.Equal("VIRTUAL DVD/CD-RW", defaultVirtual.ProductId);
        Assert.Equal(@"V:\", defaultVirtual.VolumePath);
        Assert.True(defaultVirtual.IsVirtual);

        // includeVirtual: false should return no virtual drives
        var physicalOnly = manager.GetRecorders(includeVirtual: false);
        Assert.Empty(physicalOnly);
    }

    [Fact]
    public void DefaultPhysicalEnumeration_AlwaysIncludesVirtualRecorder()
    {
        // On systems with or without physical drives, includeVirtual: true still returns VDRV-01
        using var manager = new DiscDeviceManager(enablePhysicalEnumeration: true);

        var recorders = manager.GetRecorders(includeVirtual: true);

        Assert.NotEmpty(recorders);
        Assert.Contains(recorders, r => r.UniqueId == "VDRV-01" && r.IsVirtual);

        // Verify VDRV-01 can be queried by ID
        var vdrv = manager.GetRecorderById("VDRV-01");
        Assert.NotNull(vdrv);
        Assert.Equal("VDRV-01", vdrv.UniqueId);
        Assert.True(vdrv.IsVirtual);
    }

    [Fact]
    public void AddAndRemoveVirtualRecorder_RaisesDeviceChangedAndUpdatesLookup()
    {
        // 2. Verify adding and removing a secondary VirtualDiscRecorder ("VDRV-02")
        // raises DeviceChanged and updates GetRecorderById("VDRV-02").
        using var manager = new DiscDeviceManager(enablePhysicalEnumeration: false);

        var events = new List<DeviceChangeEventArgs>();
        manager.DeviceChanged += (s, e) => events.Add(e);

        using var secondary = new VirtualDiscRecorder(
            uniqueId: "VDRV-02",
            vendorId: "IGNIS",
            productId: "VIRTUAL BD-RE",
            volumePath: @"W:\"
        );

        // Add secondary virtual recorder
        manager.AddVirtualRecorder(secondary);

        Assert.Single(events);
        Assert.Equal("VDRV-02", events[0].UniqueId);
        Assert.Equal(DeviceChangeType.Added, events[0].ChangeType);

        // Verify lookup by ID
        var found = manager.GetRecorderById("VDRV-02");
        Assert.NotNull(found);
        Assert.Equal("VDRV-02", found.UniqueId);
        Assert.Equal(@"W:\", found.VolumePath);
        Assert.Same(secondary, found);

        // Verify lookup by VolumePath
        var foundByPath = manager.GetRecorderById(@"W:\");
        Assert.NotNull(foundByPath);
        Assert.Equal("VDRV-02", foundByPath.UniqueId);

        // Verify total recorders count
        var allRecorders = manager.GetRecorders(includeVirtual: true);
        Assert.Equal(2, allRecorders.Count);
        Assert.Contains(allRecorders, r => r.UniqueId == "VDRV-01");
        Assert.Contains(allRecorders, r => r.UniqueId == "VDRV-02");

        // Remove secondary virtual recorder
        bool removed = manager.RemoveVirtualRecorder("VDRV-02");
        Assert.True(removed);

        Assert.Equal(2, events.Count);
        Assert.Equal("VDRV-02", events[1].UniqueId);
        Assert.Equal(DeviceChangeType.Removed, events[1].ChangeType);

        // Verify it is no longer found
        Assert.Null(manager.GetRecorderById("VDRV-02"));
        Assert.Single(manager.GetRecorders(includeVirtual: true));

        // Removing non-existent recorder should return false and not fire event
        bool removedAgain = manager.RemoveVirtualRecorder("VDRV-02");
        Assert.False(removedAgain);
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public void VirtualRecorder_MediaInsertionAndEjection_RaisesDeviceChangedEvent()
    {
        using var manager = new DiscDeviceManager(enablePhysicalEnumeration: false);

        var events = new List<DeviceChangeEventArgs>();
        manager.DeviceChanged += (s, e) => events.Add(e);

        var recorder = manager.GetRecorderById("VDRV-01") as VirtualDiscRecorder;
        Assert.NotNull(recorder);

        // Insert media
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        Assert.Single(events);
        Assert.Equal("VDRV-01", events[0].UniqueId);
        Assert.Equal(DeviceChangeType.MediaInserted, events[0].ChangeType);

        // Eject media
        recorder.EjectMedia();

        Assert.Equal(2, events.Count);
        Assert.Equal("VDRV-01", events[1].UniqueId);
        Assert.Equal(DeviceChangeType.MediaEjected, events[1].ChangeType);
    }

    #endregion

    #region DeviceChangeMonitor & WM_DEVICECHANGE Decoding Tests

    [Theory]
    [InlineData(0x00000010u, new[] { 'E' })]
    [InlineData(0x00000028u, new[] { 'D', 'F' })]
    [InlineData(0x00000001u, new[] { 'A' })]
    [InlineData(0x00000004u, new[] { 'C' })]
    [InlineData(1u << 25, new[] { 'Z' })]
    public void DecodeDriveMask_DecodesExpectedDriveLetters(uint unitMask, char[] expectedLetters)
    {
        // 3. Verify DeviceChangeMonitor.DecodeDriveMask decodes bitmasks
        // (0x00000010 -> ['E'], 0x00000028 -> ['D', 'F'])
        char[] letters = DeviceChangeMonitor.DecodeDriveMask(unitMask);
        Assert.Equal(expectedLetters, letters);
    }

    [Fact]
    public void DecodeDriveMask_ZeroBitmask_ReturnsEmptyArray()
    {
        char[] letters = DeviceChangeMonitor.DecodeDriveMask(0);
        Assert.Empty(letters);
    }

    [Theory]
    [InlineData(0x00000010u, new[] { @"E:\" })]
    [InlineData(0x00000028u, new[] { @"D:\", @"F:\" })]
    public void DecodeDrivePaths_FormatsPathsCorrectly(uint unitMask, string[] expectedPaths)
    {
        string[] paths = DeviceChangeMonitor.DecodeDrivePaths(unitMask);
        Assert.Equal(expectedPaths, paths);
    }

    [Fact]
    public void SimulateDeviceChange_ArrivalAndRemoval_FiresExpectedEvents()
    {
        // Simulates WM_DEVICECHANGE arrival/removal events.
        var monitor = new DeviceChangeMonitor();
        var events = new List<DeviceChangeEventArgs>();
        monitor.DeviceChanged += (s, e) => events.Add(e);

        // 1. Optical Drive Arrival (isMedia = false)
        monitor.SimulateDeviceChange(DeviceChangeMonitor.DBT_DEVICEARRIVAL, 0x00000010u, isMedia: false);
        Assert.Single(events);
        Assert.Equal(@"E:\", events[0].UniqueId);
        Assert.Equal(DeviceChangeType.Added, events[0].ChangeType);

        // 2. Optical Disc Media Insertion (isMedia = true)
        monitor.SimulateDeviceChange(DeviceChangeMonitor.DBT_DEVICEARRIVAL, 0x00000010u, isMedia: true);
        Assert.Equal(2, events.Count);
        Assert.Equal(@"E:\", events[1].UniqueId);
        Assert.Equal(DeviceChangeType.MediaInserted, events[1].ChangeType);

        // 3. Optical Disc Media Ejection (isMedia = true)
        monitor.SimulateDeviceChange(DeviceChangeMonitor.DBT_DEVICEREMOVECOMPLETE, 0x00000010u, isMedia: true);
        Assert.Equal(3, events.Count);
        Assert.Equal(@"E:\", events[2].UniqueId);
        Assert.Equal(DeviceChangeType.MediaEjected, events[2].ChangeType);

        // 4. Optical Drive Removal (isMedia = false)
        monitor.SimulateDeviceChange(DeviceChangeMonitor.DBT_DEVICEREMOVECOMPLETE, 0x00000010u, isMedia: false);
        Assert.Equal(4, events.Count);
        Assert.Equal(@"E:\", events[3].UniqueId);
        Assert.Equal(DeviceChangeType.Removed, events[3].ChangeType);
    }

    [Fact]
    public void ProcessMessage_WithNativeDevBroadcastVolumeBuffer_HandlesCorrectly()
    {
        var monitor = new DeviceChangeMonitor();
        var events = new List<DeviceChangeEventArgs>();
        monitor.DeviceChanged += (s, e) => events.Add(e);

        // Non-device change message should be ignored
        bool handledNonDev = monitor.ProcessMessage(0x0001, 0, 0);
        Assert.False(handledNonDev);
        Assert.Empty(events);

        // Create native DEV_BROADCAST_VOLUME buffer for 'E' with DBTF_MEDIA (media arrival)
        nint bufPtr = DeviceChangeMonitor.AllocateVolumeBroadcast(0x00000010u, DeviceChangeMonitor.DBTF_MEDIA);
        try
        {
            bool handled = monitor.ProcessMessage(
                DeviceChangeMonitor.WM_DEVICECHANGE,
                (nint)DeviceChangeMonitor.DBT_DEVICEARRIVAL,
                bufPtr);

            Assert.True(handled);
            Assert.Single(events);
            Assert.Equal(@"E:\", events[0].UniqueId);
            Assert.Equal(DeviceChangeType.MediaInserted, events[0].ChangeType);
        }
        finally
        {
            DeviceChangeMonitor.FreeVolumeBroadcast(bufPtr);
        }
    }

    [Fact]
    public void ProcessVolumeChange_ManagedStruct_HandlesCorrectly()
    {
        var monitor = new DeviceChangeMonitor();
        var events = new List<DeviceChangeEventArgs>();
        monitor.DeviceChanged += (s, e) => events.Add(e);

        var vol = new DEV_BROADCAST_VOLUME
        {
            dbcv_size = (uint)Marshal.SizeOf<DEV_BROADCAST_VOLUME>(),
            dbcv_devicetype = DeviceChangeMonitor.DBT_DEVTYP_VOLUME,
            dbcv_unitmask = 0x00000028u, // 'D' and 'F'
            dbcv_flags = 0 // Drive arrival, not media
        };

        bool handled = monitor.ProcessVolumeChange(DeviceChangeMonitor.DBT_DEVICEARRIVAL, in vol);
        Assert.True(handled);
        Assert.Equal(2, events.Count);
        Assert.Equal(@"D:\", events[0].UniqueId);
        Assert.Equal(DeviceChangeType.Added, events[0].ChangeType);
        Assert.Equal(@"F:\", events[1].UniqueId);
        Assert.Equal(DeviceChangeType.Added, events[1].ChangeType);
    }

    #endregion

    #region ImapiDiscRecorder & Capabilities Tests

    [Fact]
    public void MapPhysicalTypeToDiscMediaType_MapsAllTypesAccurately()
    {
        Assert.Equal(DiscMediaType.CdRom, ImapiDiscRecorder.MapPhysicalTypeToDiscMediaType(ImapiMediaPhysicalType.CdRom));
        Assert.Equal(DiscMediaType.CdR, ImapiDiscRecorder.MapPhysicalTypeToDiscMediaType(ImapiMediaPhysicalType.CdR));
        Assert.Equal(DiscMediaType.CdRw, ImapiDiscRecorder.MapPhysicalTypeToDiscMediaType(ImapiMediaPhysicalType.CdRw));
        Assert.Equal(DiscMediaType.DvdRom, ImapiDiscRecorder.MapPhysicalTypeToDiscMediaType(ImapiMediaPhysicalType.DvdRom));
        Assert.Equal(DiscMediaType.DvdMinusR, ImapiDiscRecorder.MapPhysicalTypeToDiscMediaType(ImapiMediaPhysicalType.DvdDashR));
        Assert.Equal(DiscMediaType.DvdMinusRw, ImapiDiscRecorder.MapPhysicalTypeToDiscMediaType(ImapiMediaPhysicalType.DvdDashRw));
        Assert.Equal(DiscMediaType.DvdPlusR, ImapiDiscRecorder.MapPhysicalTypeToDiscMediaType(ImapiMediaPhysicalType.DvdPlusR));
        Assert.Equal(DiscMediaType.DvdPlusRw, ImapiDiscRecorder.MapPhysicalTypeToDiscMediaType(ImapiMediaPhysicalType.DvdPlusRw));
        Assert.Equal(DiscMediaType.DvdPlusRDualLayer, ImapiDiscRecorder.MapPhysicalTypeToDiscMediaType(ImapiMediaPhysicalType.DvdPlusRDoubleLayer));
        Assert.Equal(DiscMediaType.DvdPlusRDualLayer, ImapiDiscRecorder.MapPhysicalTypeToDiscMediaType(ImapiMediaPhysicalType.DvdPlusRwDoubleLayer));
        Assert.Equal(DiscMediaType.DvdMinusRDualLayer, ImapiDiscRecorder.MapPhysicalTypeToDiscMediaType(ImapiMediaPhysicalType.DvdDashRDoubleLayer));
        Assert.Equal(DiscMediaType.DvdRam, ImapiDiscRecorder.MapPhysicalTypeToDiscMediaType(ImapiMediaPhysicalType.DvdRam));
        Assert.Equal(DiscMediaType.Unknown, ImapiDiscRecorder.MapPhysicalTypeToDiscMediaType(ImapiMediaPhysicalType.Unknown));
    }

    [Fact]
    public void PhysicalDriveEnumeration_IfPhysicalDrivePresent_QueriesPropertiesSafely()
    {
        // Tests real physical drive enumeration if optical drive hardware is present
        using var manager = new DiscDeviceManager(enablePhysicalEnumeration: true);
        var physicalRecorders = manager.GetRecorders(includeVirtual: false);

        if (physicalRecorders.Count > 0)
        {
            var recorder = physicalRecorders[0];
            Assert.False(recorder.IsVirtual);
            Assert.False(string.IsNullOrWhiteSpace(recorder.UniqueId));
            Assert.False(string.IsNullOrWhiteSpace(recorder.VendorId));
            Assert.False(string.IsNullOrWhiteSpace(recorder.ProductId));

            // Query capabilities safely without throwing
            var caps = recorder.GetCapabilities();
            Assert.NotNull(caps);
            Assert.NotNull(caps.SupportedMediaTypes);
            Assert.NotNull(caps.SupportedWriteSpeeds);

            // Query media status safely without throwing
            var status = recorder.QueryMediaStatus();
            Assert.NotNull(status);

            // Query supported profiles safely
            if (recorder is ImapiDiscRecorder imapiRec)
            {
                var profiles = imapiRec.GetSupportedProfiles();
                Assert.NotNull(profiles);
                Assert.NotNull(imapiRec.VolumePathNames);
            }
        }
    }

    [Fact]
    public void DiscFormat2Data_AttachPhysicalRecorder_IsRecorderSupported()
    {
        var master = ImapiComActivation.CreateDiscMaster2();
        if (master != null && master.Count > 0)
        {
            string id = master[0];
            var rec = ImapiComActivation.CreateDiscRecorder2();
            Assert.NotNull(rec);
            rec.InitializeDiscRecorder(id);
            string v = rec.VendorId;
            Assert.NotNull(v);

            var data = ImapiComActivation.CreateDiscFormat2Data();
            Assert.NotNull(data);
            try
            {
                if (data.IsRecorderSupported(rec))
                {
                    data.put_Recorder(rec);
                }
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Device may be read-only optical drive or does not support IDiscFormat2Data
            }
        }
    }

    [Fact]
    public void ImapiDiscRecorder_DisposedInstance_ThrowsObjectDisposedException()
    {
        using var manager = new DiscDeviceManager(enablePhysicalEnumeration: true);
        var physical = manager.GetRecorders(includeVirtual: false);

        if (physical.Count > 0 && physical[0] is ImapiDiscRecorder imapiRec)
        {
            // Create a separate instance to dispose
            var clone = new ImapiDiscRecorder(imapiRec.UniqueId);
            clone.Dispose();

            Assert.Throws<ObjectDisposedException>(() => _ = clone.VendorId);
            Assert.Throws<ObjectDisposedException>(() => _ = clone.ProductId);
            Assert.Throws<ObjectDisposedException>(() => _ = clone.VolumePath);
            Assert.Throws<ObjectDisposedException>(() => clone.GetCapabilities());
            Assert.Throws<ObjectDisposedException>(() => clone.QueryMediaStatus());
            Assert.Throws<ObjectDisposedException>(() => clone.EjectMedia());
            Assert.Throws<ObjectDisposedException>(() => clone.CloseTray());
            Assert.Throws<ObjectDisposedException>(() => clone.AcquireExclusiveAccess("test"));
            Assert.Throws<ObjectDisposedException>(() => clone.ReleaseExclusiveAccess());
        }
    }

    #endregion
}
