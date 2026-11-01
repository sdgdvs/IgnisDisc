using System.Runtime.InteropServices;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Interop;

namespace CdBurner.Hardware.Devices;

/// <summary>
/// Physical optical disc recorder backend wrapping Windows IMAPIv2 <see cref="IDiscRecorder2"/> COM interface.
/// </summary>
public class ImapiDiscRecorder : IDiscRecorderBackend, IDiscTocProvider
{
    private IDiscRecorder2? _recorder;
    private readonly string _uniqueId;
    private string? _exclusiveClient;
    private bool _disposed;

    /// <summary>
    /// Gets the underlying native IMAPIv2 COM recorder instance.
    /// </summary>
    public IDiscRecorder2? Recorder => _recorder;

    public string UniqueId => _uniqueId;
    public bool IsVirtual => false;

    public string VendorId
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_recorder == null) return string.Empty;
            try { return _recorder.VendorId?.Trim() ?? string.Empty; }
            catch { return string.Empty; }
        }
    }

    public string ProductId
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_recorder == null) return string.Empty;
            try { return _recorder.ProductId?.Trim() ?? string.Empty; }
            catch { return string.Empty; }
        }
    }

    public string ProductRevision
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_recorder == null) return string.Empty;
            try { return _recorder.ProductRevision?.Trim() ?? string.Empty; }
            catch { return string.Empty; }
        }
    }

    public string VolumePath
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_recorder == null) return string.Empty;
            try
            {
                var paths = _recorder.VolumePathNames;
                if (paths != null && paths.Length > 0)
                {
                    var val = paths.GetValue(0)?.ToString();
                    if (!string.IsNullOrEmpty(val))
                    {
                        return val;
                    }
                }
            }
            catch
            {
            }
            return string.Empty;
        }
    }

    /// <summary>
    /// Gets all volume mount paths (e.g. ["E:\\"]) associated with this recorder.
    /// </summary>
    public IReadOnlyList<string> VolumePathNames
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_recorder == null) return Array.Empty<string>();
            try
            {
                var paths = _recorder.VolumePathNames;
                if (paths == null || paths.Length == 0) return Array.Empty<string>();
                var result = new List<string>(paths.Length);
                for (int i = 0; i < paths.Length; i++)
                {
                    var val = paths.GetValue(i)?.ToString();
                    if (!string.IsNullOrEmpty(val))
                    {
                        result.Add(val);
                    }
                }
                return result;
            }
            catch
            {
                return Array.Empty<string>();
            }
        }
    }

    /// <summary>
    /// Gets the name of the client currently holding an exclusive lock on this recorder.
    /// </summary>
    public string? ExclusiveClientName
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!string.IsNullOrEmpty(_exclusiveClient)) return _exclusiveClient;
            if (_recorder == null) return null;
            try { return _recorder.ExclusiveAccessOwner; }
            catch { return null; }
        }
    }

    /// <summary>
    /// Initializes a new physical IMAPIv2 recorder instance for the specified recorder unique identifier.
    /// </summary>
    public ImapiDiscRecorder(string uniqueId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uniqueId);
        _uniqueId = uniqueId;

        if (OperatingSystem.IsWindows())
        {
            var recorder = Imapi2ComFactory.CreateDiscRecorder2()
                ?? throw new InvalidOperationException($"Failed to activate MsftDiscRecorder2 COM CoClass for unique ID '{uniqueId}'.");
            recorder.InitializeDiscRecorder(uniqueId);
            _recorder = recorder;
        }
        else
        {
            throw new PlatformNotSupportedException("IMAPIv2 optical recorder is only supported on Windows.");
        }
    }

    /// <summary>
    /// Initializes a new physical IMAPIv2 recorder backend wrapping an existing COM instance.
    /// </summary>
    public ImapiDiscRecorder(IDiscRecorder2 recorder, string? uniqueId = null)
    {
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        if (!string.IsNullOrWhiteSpace(uniqueId))
        {
            _uniqueId = uniqueId;
        }
        else
        {
            try
            {
                _uniqueId = recorder.ActiveDiscRecorder ?? string.Empty;
            }
            catch
            {
                _uniqueId = string.Empty;
            }
        }
    }

    /// <summary>
    /// Queries the MMC profiles supported by this recorder.
    /// </summary>
    public IReadOnlyList<int> GetSupportedProfiles()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_recorder == null) return Array.Empty<int>();

        try
        {
            var profiles = _recorder.SupportedProfiles;
            if (profiles == null || profiles.Length == 0) return Array.Empty<int>();

            var result = new List<int>(profiles.Length);
            for (int i = 0; i < profiles.Length; i++)
            {
                var val = profiles.GetValue(i);
                if (val != null)
                {
                    result.Add(Convert.ToInt32(val));
                }
            }
            return result;
        }
        catch
        {
            return Array.Empty<int>();
        }
    }

    /// <summary>
    /// Returns drive hardware writing capabilities, buffer size, and supported speeds.
    /// </summary>
    public DriveCapabilitiesDto GetCapabilities()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var profiles = GetSupportedProfiles();

        // MMC Profiles
        bool canWriteCdR = profiles.Contains(0x0009);
        bool canWriteCdRw = profiles.Contains(0x000A);
        bool canWriteDvdR = profiles.Contains(0x0011);
        bool canWriteDvdRw = profiles.Contains(0x0013) || profiles.Contains(0x0014);
        bool canWriteDvdPlusR = profiles.Contains(0x001B);
        bool canWriteDvdPlusRw = profiles.Contains(0x001A);
        bool canWriteDvdPlusRDL = profiles.Contains(0x002B);
        bool canWriteDvdMinusRDL = profiles.Contains(0x0015) || profiles.Contains(0x0016);
        bool canWriteDvdRam = profiles.Contains(0x0012);

        var mediaTypes = new HashSet<DiscMediaType>();
        if (canWriteCdR) mediaTypes.Add(DiscMediaType.CdR);
        if (canWriteCdRw) mediaTypes.Add(DiscMediaType.CdRw);
        if (canWriteDvdR) mediaTypes.Add(DiscMediaType.DvdMinusR);
        if (canWriteDvdRw) mediaTypes.Add(DiscMediaType.DvdMinusRw);
        if (canWriteDvdPlusR) mediaTypes.Add(DiscMediaType.DvdPlusR);
        if (canWriteDvdPlusRw) mediaTypes.Add(DiscMediaType.DvdPlusRw);
        if (canWriteDvdPlusRDL) mediaTypes.Add(DiscMediaType.DvdPlusRDualLayer);
        if (canWriteDvdMinusRDL) mediaTypes.Add(DiscMediaType.DvdMinusRDualLayer);
        if (canWriteDvdRam) mediaTypes.Add(DiscMediaType.DvdRam);

        var writeSpeeds = new List<WriteSpeedDescriptor>();

        IDiscFormat2Data? formatData = null;
        try
        {
            if (_recorder != null && OperatingSystem.IsWindows())
            {
                formatData = Imapi2ComFactory.CreateDiscFormat2Data();
                if (formatData != null)
                {
                    try
                    {
                        formatData.put_Recorder(_recorder);

                        var supportedArray = formatData.SupportedMediaTypes;
                        if (supportedArray != null)
                        {
                            for (int i = 0; i < supportedArray.Length; i++)
                            {
                                var item = supportedArray.GetValue(i);
                                if (item != null)
                                {
                                    var phys = (ImapiMediaPhysicalType)Convert.ToInt32(item);
                                    var mapped = MapPhysicalTypeToDiscMediaType(phys);
                                    if (mapped != DiscMediaType.Unknown && mapped != DiscMediaType.NoMedia)
                                    {
                                        mediaTypes.Add(mapped);
                                    }
                                }
                            }
                        }

                        if (formatData.IsCurrentMediaSupported(_recorder))
                        {
                            var speedsArray = formatData.SupportedWriteSpeeds;
                            if (speedsArray != null)
                            {
                                var physType = formatData.CurrentPhysicalMediaType;
                                var currentMediaType = MapPhysicalTypeToDiscMediaType(physType);
                                for (int i = 0; i < speedsArray.Length; i++)
                                {
                                    var item = speedsArray.GetValue(i);
                                    if (item != null)
                                    {
                                        int sectorsPerSec = Convert.ToInt32(item);
                                        int bytesPerSec = sectorsPerSec * OpticalConstants.DataSectorSize;
                                        writeSpeeds.Add(new WriteSpeedDescriptor(bytesPerSec, currentMediaType));
                                    }
                                }
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }
        catch
        {
        }
        finally
        {
            Imapi2ComFactory.FinalRelease(ref formatData);
        }

        if (mediaTypes.Contains(DiscMediaType.CdR)) canWriteCdR = true;
        if (mediaTypes.Contains(DiscMediaType.CdRw)) canWriteCdRw = true;
        if (mediaTypes.Contains(DiscMediaType.DvdMinusR)) canWriteDvdR = true;
        if (mediaTypes.Contains(DiscMediaType.DvdMinusRw)) canWriteDvdRw = true;
        if (mediaTypes.Contains(DiscMediaType.DvdPlusR)) canWriteDvdPlusR = true;
        if (mediaTypes.Contains(DiscMediaType.DvdPlusRw)) canWriteDvdPlusRw = true;
        if (mediaTypes.Contains(DiscMediaType.DvdPlusRDualLayer)) canWriteDvdPlusRDL = true;
        if (mediaTypes.Contains(DiscMediaType.DvdMinusRDualLayer)) canWriteDvdMinusRDL = true;
        if (mediaTypes.Contains(DiscMediaType.DvdRam)) canWriteDvdRam = true;

        if (writeSpeeds.Count == 0)
        {
            if (canWriteDvdR || canWriteDvdPlusR)
            {
                writeSpeeds.Add(WriteSpeedDescriptor.FromMultiplier(4.0, DiscMediaType.DvdMinusR));
                writeSpeeds.Add(WriteSpeedDescriptor.FromMultiplier(8.0, DiscMediaType.DvdMinusR));
                writeSpeeds.Add(WriteSpeedDescriptor.FromMultiplier(16.0, DiscMediaType.DvdMinusR));
            }
            else if (canWriteCdR)
            {
                writeSpeeds.Add(WriteSpeedDescriptor.FromMultiplier(16.0, DiscMediaType.CdR));
                writeSpeeds.Add(WriteSpeedDescriptor.FromMultiplier(24.0, DiscMediaType.CdR));
                writeSpeeds.Add(WriteSpeedDescriptor.FromMultiplier(48.0, DiscMediaType.CdR));
            }
        }

        return new DriveCapabilitiesDto
        {
            CanWriteCdR = canWriteCdR,
            CanWriteCdRw = canWriteCdRw,
            CanWriteDvdR = canWriteDvdR,
            CanWriteDvdRw = canWriteDvdRw,
            CanWriteDvdPlusR = canWriteDvdPlusR,
            CanWriteDvdPlusRw = canWriteDvdPlusRw,
            CanWriteDvdPlusRDL = canWriteDvdPlusRDL,
            CanWriteDvdMinusRDL = canWriteDvdMinusRDL,
            CanWriteDvdRam = canWriteDvdRam,
            SupportsTestWrite = true,
            SupportsBufferUnderrunProtection = true,
            InternalBufferSize = 8 * 1024 * 1024,
            SupportedMediaTypes = mediaTypes.OrderBy(m => (int)m).ToList(),
            SupportedWriteSpeeds = writeSpeeds.OrderBy(s => s.BytesPerSecond).ToList()
        };
    }

    /// <summary>
    /// Queries the presence, status, and geometry of media currently in the recorder.
    /// </summary>
    public MediaStatusDto QueryMediaStatus()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_recorder == null || !OperatingSystem.IsWindows())
        {
            return EmptyMediaStatus();
        }

        IDiscFormat2Data? dataFormat = null;
        IDiscFormat2Erase? eraseFormat = null;

        try
        {
            dataFormat = Imapi2ComFactory.CreateDiscFormat2Data();
            if (dataFormat != null)
            {
                bool isRecorderAttached = false;
                try
                {
                    dataFormat.put_Recorder(_recorder);
                    isRecorderAttached = true;
                }
                catch
                {
                    isRecorderAttached = false;
                }

                if (isRecorderAttached)
                {
                    bool isMediaSupported = false;
                    try
                    {
                        isMediaSupported = dataFormat.IsCurrentMediaSupported(_recorder);
                    }
                    catch
                    {
                        isMediaSupported = false;
                    }

                    if (isMediaSupported)
                    {
                        var physicalType = dataFormat.CurrentPhysicalMediaType;
                        var mediaType = MapPhysicalTypeToDiscMediaType(physicalType);
                        var mediaState = dataFormat.CurrentMediaStatus;

                        bool isBlank = (mediaState & ImapiFormat2DataMediaState.Blank) != 0 ||
                                       dataFormat.MediaPhysicallyBlank ||
                                       dataFormat.MediaHeuristicallyBlank;

                        bool isRewritable = mediaType.IsRewritable() ||
                                            (mediaState & ImapiFormat2DataMediaState.OverwriteOnly) != 0;

                        bool isFinalized = (mediaState & ImapiFormat2DataMediaState.Finalized) != 0;
                        bool isAppendable = (mediaState & ImapiFormat2DataMediaState.Appendable) != 0;

                        int totalSectors = 0;
                        int freeSectors = 0;
                        int nextWritable = -1;

                        try { totalSectors = dataFormat.TotalSectorsOnMedia; } catch { }
                        try { freeSectors = dataFormat.FreeSectorsOnMedia; } catch { }
                        try { nextWritable = dataFormat.NextWritableAddress; } catch { }

                        if (totalSectors == 0)
                        {
                            totalSectors = mediaType.GetNominalCapacitySectors();
                        }

                        int usedSectors = Math.Max(0, totalSectors - freeSectors);

                        // Overwrite-only media (DVD+RW, DVD-RAM, BD-RE, restricted-overwrite DVD-RW) are rewritten from
                        // the start by IDiscFormat2Data.Write, so never lay out an "appended" session on them.
                        bool overwriteOnly = (mediaState & ImapiFormat2DataMediaState.OverwriteOnly) != 0;
                        if (overwriteOnly)
                        {
                            nextWritable = 0;
                        }

                        // Size of the recorded area as reported by the drive itself (more accurate than total - free,
                        // and needed for reading back finalized or rewritable discs).
                        if (!isBlank)
                        {
                            int? readable = Scsi.PhysicalSectorReader.ReadCapacity(VolumePath);
                            if (readable is > 0)
                            {
                                usedSectors = readable.Value;
                            }
                        }
                        long totalBytes = (long)totalSectors * OpticalConstants.DataSectorSize;
                        long freeBytes = (long)freeSectors * OpticalConstants.DataSectorSize;
                        int lastRecordedLba = isBlank ? -1 : usedSectors - 1;

                        int sessionCount = 0;
                        Array? multisession = null;
                        try
                        {
                            // MultisessionInterfaces lists the multisession *write interfaces* the format supports
                            // (e.g. 2 on a blank CD-R), not the sessions on the disc; it is only probed here so the
                            // COM objects are released below. The real count comes from the disc's TOC.
                            multisession = dataFormat.MultisessionInterfaces;
                            sessionCount = isBlank ? 0 : (Scsi.PhysicalSectorReader.ReadSessionCount(VolumePath) ?? 1);
                        }
                        catch
                        {
                            sessionCount = isBlank ? 0 : 1;
                        }
                        finally
                        {
                            if (multisession != null)
                            {
                                for (int i = 0; i < multisession.Length; i++)
                                {
                                    var item = multisession.GetValue(i);
                                    if (item != null && Marshal.IsComObject(item))
                                    {
                                        try { Marshal.ReleaseComObject(item); } catch { }
                                    }
                                }
                            }
                        }

                        return new MediaStatusDto
                        {
                            MediaType = mediaType,
                            MediaPresent = true,
                            IsBlank = isBlank,
                            IsRewritable = isRewritable,
                            IsAppendable = !overwriteOnly && (isAppendable || (!isFinalized && freeSectors > 0)),
                            TotalSectors = totalSectors,
                            FreeSectors = freeSectors,
                            UsedSectors = usedSectors,
                            TotalCapacityBytes = totalBytes,
                            FreeCapacityBytes = freeBytes,
                            SessionCount = sessionCount,
                            MediaManufacturerId = null,
                            LastRecordedLba = lastRecordedLba,
                            NextWritableLba = nextWritable
                        };
                    }
                }
            }

            eraseFormat = Imapi2ComFactory.CreateDiscFormat2Erase();
            if (eraseFormat != null)
            {
                bool isEraseAttached = false;
                try
                {
                    eraseFormat.put_Recorder(_recorder);
                    isEraseAttached = true;
                }
                catch
                {
                    isEraseAttached = false;
                }

                if (isEraseAttached)
                {
                    bool isEraseSupported = false;
                    try
                    {
                        isEraseSupported = eraseFormat.IsCurrentMediaSupported(_recorder);
                    }
                    catch
                    {
                        isEraseSupported = false;
                    }

                    if (isEraseSupported)
                    {
                        var physicalType = eraseFormat.CurrentPhysicalMediaType;
                        var mediaType = MapPhysicalTypeToDiscMediaType(physicalType);
                        bool isBlank = eraseFormat.MediaPhysicallyBlank || eraseFormat.MediaHeuristicallyBlank;
                        int nominalSectors = mediaType.GetNominalCapacitySectors();
                        int recordedSectors = isBlank ? 0 : (Scsi.PhysicalSectorReader.ReadCapacity(VolumePath) ?? nominalSectors);
                        long nominalBytes = mediaType.GetNominalCapacityBytes();

                        return new MediaStatusDto
                        {
                            MediaType = mediaType,
                            MediaPresent = true,
                            IsBlank = isBlank,
                            IsRewritable = true,
                            IsAppendable = false,
                            TotalSectors = nominalSectors,
                            FreeSectors = isBlank ? nominalSectors : 0,
                            UsedSectors = recordedSectors,
                            TotalCapacityBytes = nominalBytes,
                            FreeCapacityBytes = isBlank ? nominalBytes : 0,
                            SessionCount = isBlank ? 0 : 1,
                            MediaManufacturerId = null,
                            LastRecordedLba = isBlank ? -1 : recordedSectors - 1
                        };
                    }
                }
            }
        }
        catch
        {
        }
        finally
        {
            Imapi2ComFactory.FinalRelease(ref dataFormat);
            Imapi2ComFactory.FinalRelease(ref eraseFormat);
        }

        // Pressed / read-only discs (CD-ROM, DVD-ROM, BD-ROM) are not writable formats for IMAPI, but they can
        // still be read, ripped, verified and scanned.
        try
        {
            int? readOnlySectors = Scsi.PhysicalSectorReader.ReadCapacity(VolumePath);
            if (readOnlySectors is > 0)
            {
                bool isCd = readOnlySectors.Value <= 450_000; // ~ 880 MB: larger than any CD
                long bytes = (long)readOnlySectors.Value * OpticalConstants.DataSectorSize;
                return new MediaStatusDto
                {
                    MediaType = isCd ? DiscMediaType.CdRom : DiscMediaType.DvdRom,
                    MediaPresent = true,
                    IsBlank = false,
                    IsRewritable = false,
                    IsAppendable = false,
                    TotalSectors = readOnlySectors.Value,
                    FreeSectors = 0,
                    UsedSectors = readOnlySectors.Value,
                    TotalCapacityBytes = bytes,
                    FreeCapacityBytes = 0,
                    SessionCount = 1,
                    MediaManufacturerId = null,
                    LastRecordedLba = readOnlySectors.Value - 1
                };
            }
        }
        catch
        {
        }

        // Pressed audio CDs: READ CAPACITY needs a read/write handle (often refused without elevation) and the disk
        // length IOCTL fails without a data track, but the TOC is readable through a read-only handle.
        try
        {
            var toc = Scsi.PhysicalSectorReader.ReadTableOfContents(VolumePath);
            if (toc.Count > 0)
            {
                int sectors = toc.Max(t => t.StartLba + t.SectorCount);
                return new MediaStatusDto
                {
                    MediaType = DiscMediaType.CdRom,
                    MediaPresent = true,
                    IsBlank = false,
                    IsRewritable = false,
                    IsAppendable = false,
                    TotalSectors = sectors,
                    FreeSectors = 0,
                    UsedSectors = sectors,
                    TotalCapacityBytes = (long)sectors * OpticalConstants.DataSectorSize,
                    FreeCapacityBytes = 0,
                    SessionCount = 1,
                    MediaManufacturerId = null,
                    LastRecordedLba = sectors - 1
                };
            }
        }
        catch
        {
        }

        return EmptyMediaStatus();
    }

    /// <summary>
    /// Ejects the recorder tray.
    /// </summary>
    public void EjectMedia()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _recorder?.EjectMedia();
    }

    /// <summary>
    /// Closes the recorder tray.
    /// </summary>
    public void CloseTray()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _recorder?.CloseTray();
    }

    /// <summary>
    /// Acquires exclusive read/write access to this recorder.
    /// </summary>
    public bool AcquireExclusiveAccess(string clientName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientName);
        if (_recorder == null) return false;

        try
        {
            _recorder.AcquireExclusiveAccess(false, clientName);
            _exclusiveClient = clientName;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Releases previously acquired exclusive access to this recorder.
    /// </summary>
    public void ReleaseExclusiveAccess()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_recorder == null) return;

        try
        {
            _recorder.ReleaseExclusiveAccess();
            _exclusiveClient = null;
        }
        catch
        {
        }
    }

    /// <summary>
    /// Maps IMAPIv2 <see cref="ImapiMediaPhysicalType"/> to domain <see cref="DiscMediaType"/>.
    /// </summary>
    public static DiscMediaType MapPhysicalTypeToDiscMediaType(ImapiMediaPhysicalType physicalType) => physicalType switch
    {
        ImapiMediaPhysicalType.CdRom => DiscMediaType.CdRom,
        ImapiMediaPhysicalType.CdR => DiscMediaType.CdR,
        ImapiMediaPhysicalType.CdRw => DiscMediaType.CdRw,
        ImapiMediaPhysicalType.DvdRom => DiscMediaType.DvdRom,
        ImapiMediaPhysicalType.DvdRam => DiscMediaType.DvdRam,
        ImapiMediaPhysicalType.DvdPlusR => DiscMediaType.DvdPlusR,
        ImapiMediaPhysicalType.DvdPlusRw => DiscMediaType.DvdPlusRw,
        ImapiMediaPhysicalType.DvdPlusRDoubleLayer or ImapiMediaPhysicalType.DvdPlusRwDoubleLayer => DiscMediaType.DvdPlusRDualLayer,
        ImapiMediaPhysicalType.DvdDashR => DiscMediaType.DvdMinusR,
        ImapiMediaPhysicalType.DvdDashRw => DiscMediaType.DvdMinusRw,
        ImapiMediaPhysicalType.DvdDashRDoubleLayer => DiscMediaType.DvdMinusRDualLayer,
        _ => DiscMediaType.Unknown
    };

    private static MediaStatusDto EmptyMediaStatus() => new()
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

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (_recorder != null)
            {
                if (disposing && _exclusiveClient != null)
                {
                    try
                    {
                        _recorder.ReleaseExclusiveAccess();
                    }
                    catch
                    {
                    }
                    _exclusiveClient = null;
                }

                Imapi2ComFactory.FinalRelease(ref _recorder);
            }

            _disposed = true;
        }
    }

    ~ImapiDiscRecorder()
    {
        Dispose(false);
    }

    /// <inheritdoc/>
    public IReadOnlyList<DiscTocTrack> ReadTableOfContents()
    {
        try
        {
            return Scsi.PhysicalSectorReader.ReadTableOfContents(VolumePath);
        }
        catch
        {
            return Array.Empty<DiscTocTrack>();
        }
    }

    /// <inheritdoc/>
    public byte[]? ReadCdTextPacks()
    {
        try
        {
            return Scsi.PhysicalSectorReader.ReadCdTextPacks(VolumePath);
        }
        catch
        {
            return null;
        }
    }

    public override string ToString()
    {
        string vp = VolumePath;
        string name = $"{VendorId} {ProductId}".Trim();
        return string.IsNullOrEmpty(vp) ? name : $"{vp} {name}".Trim();
    }
}
