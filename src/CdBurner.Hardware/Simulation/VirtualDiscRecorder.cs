using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;
using CdBurner.Core.Utilities;

namespace CdBurner.Hardware.Simulation;

/// <summary>
/// Virtual optical drive recorder implementation of <see cref="IDiscRecorderBackend"/>
/// simulating an Ignis virtual DVD/CD-RW optical drive.
/// </summary>
public class VirtualDiscRecorder : IDiscRecorderBackend, IDriveMediaReportProvider
{
    private readonly object _lock = new();

    /// <summary>
    /// What the drive answers to READ FORMAT CAPACITIES / GET PERFORMANCE; empty unless a test sets it (for example
    /// from <see cref="Scsi.MmcMediaReport.Query"/> over a <see cref="VirtualMmcDrive"/>).
    /// </summary>
    public DriveMediaReport SimulatedDriveMediaReport { get; set; } = DriveMediaReport.Empty;

    public DriveMediaReport ReadDriveMediaReport() => SimulatedDriveMediaReport;
    private string? _exclusiveClientName;
    private VirtualDiscMedium? _trayMedium;
    private bool _disposed;

    /// <summary>
    /// Event raised when the medium or tray status changes.
    /// </summary>
    public event EventHandler? MediaChanged;

    public string UniqueId { get; init; } = "VDRV-01";
    public string VendorId { get; init; } = "IGNIS";
    public string ProductId { get; init; } = "VIRTUAL DVD/CD-RW";
    public string ProductRevision { get; init; } = "1.00";
    public string VolumePath { get; init; } = @"V:\";
    public bool IsVirtual => true;

    /// <summary>
    /// Indicates whether the virtual drive tray is currently open/ejected.
    /// </summary>
    public bool TrayOpen { get; private set; }

    /// <summary>
    /// Currently loaded virtual disc medium inside the drive, or null if empty/ejected.
    /// </summary>
    public VirtualDiscMedium? CurrentMedium { get; private set; }

    /// <summary>
    /// Name of client holding exclusive lock, or null if unrestricted.
    /// </summary>
    public string? ExclusiveClientName => _exclusiveClientName;

    public VirtualDiscRecorder(
        string uniqueId = "VDRV-01",
        string vendorId = "IGNIS",
        string productId = "VIRTUAL DVD/CD-RW",
        string volumePath = @"V:\")
    {
        UniqueId = uniqueId;
        VendorId = vendorId;
        ProductId = productId;
        VolumePath = volumePath;
    }

    /// <summary>
    /// Inserts a virtual disc medium into the drive and closes the tray.
    /// </summary>
    public void InsertMedia(VirtualDiscMedium medium)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(medium);

        lock (_lock)
        {
            CurrentMedium = medium;
            _trayMedium = medium;
            TrayOpen = false;
        }

        MediaChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Ejects the drive tray, making media inaccessible until tray is closed.
    /// </summary>
    public void EjectMedia()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            TrayOpen = true;
            _trayMedium = CurrentMedium ?? _trayMedium;
            CurrentMedium = null;
        }

        MediaChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Closes the drive tray, reloading previously inserted media if available.
    /// </summary>
    public void CloseTray()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            TrayOpen = false;
            if (CurrentMedium == null && _trayMedium != null)
            {
                CurrentMedium = _trayMedium;
            }
        }

        MediaChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Acquires exclusive read/write access to this recorder.
    /// </summary>
    public bool AcquireExclusiveAccess(string clientName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientName);

        lock (_lock)
        {
            if (_exclusiveClientName == null || _exclusiveClientName == clientName)
            {
                _exclusiveClientName = clientName;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Releases exclusive access lock.
    /// </summary>
    public void ReleaseExclusiveAccess()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            _exclusiveClientName = null;
        }
    }

    /// <summary>
    /// Returns drive hardware writing capabilities, buffer size, and supported speeds.
    /// </summary>
    public DriveCapabilitiesDto GetCapabilities()
    {
        return new DriveCapabilitiesDto
        {
            CanWriteCdR = true,
            CanWriteCdRw = true,
            CanWriteDvdR = true,
            CanWriteDvdRw = true,
            CanWriteDvdPlusR = true,
            CanWriteDvdPlusRw = true,
            CanWriteDvdPlusRDL = true,
            CanWriteDvdMinusRDL = true,
            CanWriteDvdRam = true,
            SupportsTestWrite = true,
            SupportsBufferUnderrunProtection = true,
            SupportsBufferUnderrunFree = true,
            SupportsNativeTao = true,
            SupportsNativeSao = true,
            SupportsNativeCdText = true,
            SupportsNativeRawDao = true,
            SupportsLayerJump = true,
            MmcFeatureSummary = new[]
            {
                "0x002D TAO",
                "0x002E SAO/Raw",
                "0x002F DVD Write",
                "0x0033 Layer Jump",
                "0x0041 BD Write",
                "ModePage 0x05 BUFE"
            },
            InternalBufferSize = 8 * 1024 * 1024,
            SupportedMediaTypes = new[]
            {
                DiscMediaType.CdR,
                DiscMediaType.CdRw,
                DiscMediaType.DvdMinusR,
                DiscMediaType.DvdMinusRw,
                DiscMediaType.DvdPlusR,
                DiscMediaType.DvdPlusRw,
                DiscMediaType.DvdMinusRDualLayer,
                DiscMediaType.DvdPlusRDualLayer,
                DiscMediaType.DvdRam,
                DiscMediaType.BdR,
                DiscMediaType.BdRe,
                DiscMediaType.BdRDualLayer,
                DiscMediaType.BdReDualLayer,
                DiscMediaType.BdxlRTripleLayer,
                DiscMediaType.BdxlRQuadLayer,
                DiscMediaType.BdxlReTripleLayer
            },
            SupportedWriteSpeeds = new[]
            {
                WriteSpeedDescriptor.FromMultiplier(8.0, DiscMediaType.CdR),
                WriteSpeedDescriptor.FromMultiplier(16.0, DiscMediaType.CdR),
                WriteSpeedDescriptor.FromMultiplier(24.0, DiscMediaType.CdR),
                WriteSpeedDescriptor.FromMultiplier(32.0, DiscMediaType.CdR),
                WriteSpeedDescriptor.FromMultiplier(40.0, DiscMediaType.CdR),
                WriteSpeedDescriptor.FromMultiplier(48.0, DiscMediaType.CdR),
                WriteSpeedDescriptor.FromMultiplier(2.0, DiscMediaType.DvdMinusR),
                WriteSpeedDescriptor.FromMultiplier(4.0, DiscMediaType.DvdMinusR),
                WriteSpeedDescriptor.FromMultiplier(8.0, DiscMediaType.DvdMinusR),
                WriteSpeedDescriptor.FromMultiplier(16.0, DiscMediaType.DvdMinusR)
            }
        };
    }

    /// <summary>
    /// Queries the presence, status, and geometry of media currently in the drive.
    /// </summary>
    public MediaStatusDto QueryMediaStatus()
    {
        lock (_lock)
        {
            if (TrayOpen || CurrentMedium == null)
            {
                return new MediaStatusDto
                {
                    MediaType = DiscMediaType.NoMedia,
                    MediaPresent = false,
                    IsBlank = false,
                    IsRewritable = false,
                    IsAppendable = false,
                    TotalSectors = 0,
                    FreeSectors = 0,
                    UsedSectors = 0,
                    TotalCapacityBytes = 0,
                    FreeCapacityBytes = 0,
                    SessionCount = 0,
                    MediaManufacturerId = null,
                    LastRecordedLba = -1
                };
            }

            var m = CurrentMedium;
            int sectorSize = m.SectorSize;
            long totalBytes = (long)m.TotalCapacitySectors * sectorSize;
            long freeBytes = (long)m.FreeSectors * sectorSize;

            return new MediaStatusDto
            {
                MediaType = m.MediaType,
                MediaPresent = true,
                IsBlank = m.IsBlank,
                IsRewritable = m.IsRewritable,
                // DVD+RW and DVD-RAM are rewritten in place (no erase), as the IMAPI recorder reports them.
                IsOverwriteOnly = m.MediaType is DiscMediaType.DvdPlusRw or DiscMediaType.DvdRam or DiscMediaType.BdRe,
                IsAppendable = !m.IsFinalized && m.FreeSectors > 0,
                TotalSectors = m.TotalCapacitySectors,
                FreeSectors = m.FreeSectors,
                UsedSectors = m.UsedSectors,
                TotalCapacityBytes = totalBytes,
                FreeCapacityBytes = freeBytes,
                SessionCount = m.Sessions.Count,
                MediaManufacturerId = "VIRTUAL-OPTICAL-01",
                LastRecordedLba = m.LastRecordedLba,
                NextWritableLba = m.NextWritableLba,
                BookType = m.BookType
            };
        }
    }

    /// <summary>
    /// Gets or sets the DVD Book Type for the currently loaded virtual medium.
    /// </summary>
    public DvdBookType BookType
    {
        get => CurrentMedium?.BookType ?? DvdBookType.DvdRom;
        set
        {
            if (CurrentMedium != null)
            {
                CurrentMedium.BookType = value;
            }
        }
    }

    /// <summary>
    /// Opens a readable, seekable stream over the currently inserted virtual disc medium, or null if tray open/empty.
    /// </summary>
    public Stream? CreateReadStream() => CurrentMedium?.CreateReadStream();

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
                    CurrentMedium?.Dispose();
                    CurrentMedium = null;
                    _trayMedium?.Dispose();
                    _trayMedium = null;
                    _exclusiveClientName = null;
                }
            }

            _disposed = true;
        }
    }

    public override string ToString()
    {
        return $"{VolumePath} {ProductId} (Virtual Simulator)".Trim();
    }
}
