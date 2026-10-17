using CdBurner.Core.Abstractions;
using CdBurner.Hardware.Interop;
using CdBurner.Hardware.Simulation;

namespace CdBurner.Hardware.Devices;

/// <summary>
/// Manages optical disc drive discovery, enumeration, capability inspection, and device arrival/removal notifications.
/// Combines physical optical recorders (via IMAPIv2) and virtual drive simulators.
/// </summary>
public class DiscDeviceManager : IDiscDeviceService, IDisposable
{
    private readonly object _lock = new();
    private bool _enablePhysicalEnumeration;
    private readonly DeviceChangeMonitor _changeMonitor;
    private readonly Dictionary<string, VirtualDiscRecorder> _virtualRecorders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ImapiDiscRecorder> _physicalRecorders = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>
    /// Event raised when optical drives are attached, removed, or their media state changes.
    /// </summary>
    public event EventHandler<DeviceChangeEventArgs>? DeviceChanged;

    /// <summary>
    /// Gets whether physical optical drive enumeration via IMAPIv2 is enabled.
    /// </summary>
    public bool EnablePhysicalEnumeration => _enablePhysicalEnumeration;

    /// <summary>
    /// Enables or disables physical IMAPIv2 optical drive discovery and triggers a refresh.
    /// </summary>
    public void EnablePhysicalDiscovery(bool enable)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _enablePhysicalEnumeration = enable;
        if (enable && OperatingSystem.IsWindows())
        {
            int beforeCount;
            lock (_lock) { beforeCount = _physicalRecorders.Count; }
            RefreshPhysicalRecorders();
            int afterCount;
            string? firstPhysId = null;
            lock (_lock)
            {
                afterCount = _physicalRecorders.Count;
                firstPhysId = _physicalRecorders.Keys.FirstOrDefault();
            }
            if (afterCount != beforeCount && firstPhysId != null)
            {
                DeviceChanged?.Invoke(this, new DeviceChangeEventArgs(firstPhysId, DeviceChangeType.Added));
            }
        }
    }

    /// <summary>
    /// Gets the underlying <see cref="DeviceChangeMonitor"/> handling Win32 WM_DEVICECHANGE messages.
    /// </summary>
    public DeviceChangeMonitor ChangeMonitor => _changeMonitor;

    /// <summary>
    /// Initializes a new instance of <see cref="DiscDeviceManager"/>.
    /// </summary>
    /// <param name="enablePhysicalEnumeration">True to enumerate physical IMAPIv2 drives; false for virtual-only operation.</param>
    /// <param name="changeMonitor">Optional custom change monitor; defaults to new instance.</param>
    public DiscDeviceManager(bool enablePhysicalEnumeration = true, DeviceChangeMonitor? changeMonitor = null)
    {
        _enablePhysicalEnumeration = enablePhysicalEnumeration;
        _changeMonitor = changeMonitor ?? new DeviceChangeMonitor();
        _changeMonitor.DeviceChanged += OnMonitorDeviceChanged;

        // Register default virtual recorder
        var defaultVirtual = new VirtualDiscRecorder(
            uniqueId: "VDRV-01",
            vendorId: "IGNIS",
            productId: "VIRTUAL DVD/CD-RW",
            volumePath: @"V:\"
        );
        AddVirtualRecorderInternal(defaultVirtual, raiseEvent: false);

        if (_enablePhysicalEnumeration && OperatingSystem.IsWindows())
        {
            RefreshPhysicalRecorders();
        }
    }

    /// <summary>
    /// Gets all available optical recorders on the system, optionally including virtual simulator drives.
    /// </summary>
    public IReadOnlyList<IDiscRecorderBackend> GetRecorders(bool includeVirtual = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            var list = new List<IDiscRecorderBackend>(_physicalRecorders.Count + (includeVirtual ? _virtualRecorders.Count : 0));
            list.AddRange(_physicalRecorders.Values);
            if (includeVirtual)
            {
                list.AddRange(_virtualRecorders.Values);
            }
            return list.AsReadOnly();
        }
    }

    /// <summary>
    /// Retrieves a recorder backend instance by its unique identifier or volume path.
    /// </summary>
    public IDiscRecorderBackend? GetRecorderById(string uniqueId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(uniqueId);

        lock (_lock)
        {
            if (_virtualRecorders.TryGetValue(uniqueId, out var virtualRec))
            {
                return virtualRec;
            }

            if (_physicalRecorders.TryGetValue(uniqueId, out var physRec))
            {
                return physRec;
            }

            // Fallback match by VolumePath or case-insensitive UniqueId
            var vMatch = _virtualRecorders.Values.FirstOrDefault(r =>
                string.Equals(r.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(r.VolumePath, uniqueId, StringComparison.OrdinalIgnoreCase));
            if (vMatch != null) return vMatch;

            var pMatch = _physicalRecorders.Values.FirstOrDefault(r =>
                string.Equals(r.UniqueId, uniqueId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(r.VolumePath, uniqueId, StringComparison.OrdinalIgnoreCase));
            if (pMatch != null) return pMatch;

            return null;
        }
    }

    /// <summary>
    /// Registers an additional virtual recorder in the device registry.
    /// </summary>
    public void AddVirtualRecorder(VirtualDiscRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ObjectDisposedException.ThrowIf(_disposed, this);

        AddVirtualRecorderInternal(recorder, raiseEvent: true);
    }

    /// <summary>
    /// Removes a virtual recorder from the registry by its unique identifier.
    /// </summary>
    public bool RemoveVirtualRecorder(string uniqueId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uniqueId);
        ObjectDisposedException.ThrowIf(_disposed, this);

        VirtualDiscRecorder? removed = null;
        lock (_lock)
        {
            if (!_virtualRecorders.Remove(uniqueId, out removed))
            {
                return false;
            }
        }

        if (removed != null)
        {
            removed.MediaChanged -= OnVirtualRecorderMediaChanged;
            DeviceChanged?.Invoke(this, new DeviceChangeEventArgs(uniqueId, DeviceChangeType.Removed));
        }

        return true;
    }

    /// <summary>
    /// Retrieves all currently registered virtual optical recorders.
    /// </summary>
    public IReadOnlyList<VirtualDiscRecorder> GetVirtualRecorders()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lock)
        {
            return _virtualRecorders.Values.ToList().AsReadOnly();
        }
    }

    /// <summary>
    /// Forces re-enumeration of physical optical drives and updates the internal recorder cache.
    /// </summary>
    public void RefreshRecorders()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_enablePhysicalEnumeration && OperatingSystem.IsWindows())
        {
            RefreshPhysicalRecorders();
        }
    }

    private void AddVirtualRecorderInternal(VirtualDiscRecorder recorder, bool raiseEvent)
    {
        lock (_lock)
        {
            _virtualRecorders[recorder.UniqueId] = recorder;
            recorder.MediaChanged += OnVirtualRecorderMediaChanged;
        }

        if (raiseEvent)
        {
            DeviceChanged?.Invoke(this, new DeviceChangeEventArgs(recorder.UniqueId, DeviceChangeType.Added));
        }
    }

    private void OnVirtualRecorderMediaChanged(object? sender, EventArgs e)
    {
        if (sender is VirtualDiscRecorder vRec)
        {
            var changeType = vRec.CurrentMedium != null ? DeviceChangeType.MediaInserted : DeviceChangeType.MediaEjected;
            DeviceChanged?.Invoke(this, new DeviceChangeEventArgs(vRec.UniqueId, changeType));
        }
    }

    private void RefreshPhysicalRecorders()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        lock (_lock)
        {
            IDiscMaster2? master = null;
            try
            {
                master = Imapi2ComFactory.CreateDiscMaster2();
                if (master == null || !master.IsSupportedEnvironment)
                {
                    return;
                }

                int count = master.Count;
                var activeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        string uniqueId = master[i];
                        if (string.IsNullOrWhiteSpace(uniqueId)) continue;

                        activeIds.Add(uniqueId);
                        if (!_physicalRecorders.ContainsKey(uniqueId))
                        {
                            var recorder = Imapi2ComFactory.CreateDiscRecorder2();
                            if (recorder != null)
                            {
                                recorder.InitializeDiscRecorder(uniqueId);
                                var backend = new ImapiDiscRecorder(recorder, uniqueId);
                                _physicalRecorders[uniqueId] = backend;
                            }
                        }
                    }
                    catch
                    {
                    }
                }

                var toRemove = _physicalRecorders.Keys.Where(k => !activeIds.Contains(k)).ToList();
                foreach (var key in toRemove)
                {
                    if (_physicalRecorders.Remove(key, out var oldRecorder))
                    {
                        oldRecorder.Dispose();
                    }
                }
            }
            catch
            {
                // Gracefully degrade to virtual recorders if IMAPIv2 throws
            }
            finally
            {
                Imapi2ComFactory.FinalRelease(ref master);
            }
        }
    }

    private void OnMonitorDeviceChanged(object? sender, DeviceChangeEventArgs e)
    {
        if (_enablePhysicalEnumeration && OperatingSystem.IsWindows())
        {
            RefreshPhysicalRecorders();
        }

        // Try mapping volume path to physical recorder's unique ID
        string resolvedId = e.UniqueId;
        if (!string.IsNullOrWhiteSpace(e.UniqueId))
        {
            lock (_lock)
            {
                var match = _physicalRecorders.Values.FirstOrDefault(r =>
                    string.Equals(r.VolumePath, e.UniqueId, StringComparison.OrdinalIgnoreCase) ||
                    r.VolumePathNames.Any(p => string.Equals(p, e.UniqueId, StringComparison.OrdinalIgnoreCase)));
                if (match != null)
                {
                    resolvedId = match.UniqueId;
                }
            }
        }

        DeviceChanged?.Invoke(this, new DeviceChangeEventArgs(resolvedId, e.ChangeType));
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                lock (_lock)
                {
                    _changeMonitor.DeviceChanged -= OnMonitorDeviceChanged;

                    foreach (var virtualRec in _virtualRecorders.Values)
                    {
                        virtualRec.MediaChanged -= OnVirtualRecorderMediaChanged;
                        virtualRec.Dispose();
                    }
                    _virtualRecorders.Clear();

                    foreach (var physRec in _physicalRecorders.Values)
                    {
                        physRec.Dispose();
                    }
                    _physicalRecorders.Clear();
                }
            }
            _disposed = true;
        }
    }
}
