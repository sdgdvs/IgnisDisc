using CdBurner.Core.Primitives;
using CdBurner.Core.Utilities;

namespace CdBurner.Core.Abstractions;

/// <summary>
/// Service providing enumeration, discovery, and notification for physical and virtual optical disc drives.
/// </summary>
public interface IDiscDeviceService
{
    /// <summary>
    /// Gets all available optical recorders on the system, optionally including virtual simulator drives.
    /// </summary>
    IReadOnlyList<IDiscRecorderBackend> GetRecorders(bool includeVirtual = true);

    /// <summary>
    /// Retrieves a recorder backend instance by its unique identifier.
    /// </summary>
    IDiscRecorderBackend? GetRecorderById(string uniqueId);

    /// <summary>
    /// Event raised when optical drives are attached, removed, or their media state changes.
    /// </summary>
    event EventHandler<DeviceChangeEventArgs>? DeviceChanged;
}

/// <summary>
/// Hardware or virtual optical drive recorder abstraction.
/// </summary>
public interface IDiscRecorderBackend : IDisposable
{
    string UniqueId { get; }
    string VendorId { get; }
    string ProductId { get; }
    string ProductRevision { get; }
    string VolumePath { get; } // e.g. "E:\" or "V:\Virtual0"
    bool IsVirtual { get; }

    DriveCapabilitiesDto GetCapabilities();
    MediaStatusDto QueryMediaStatus();
    void EjectMedia();
    void CloseTray();
    bool AcquireExclusiveAccess(string clientName);
    void ReleaseExclusiveAccess();
}

/// <summary>
/// Optical mastering, burning, ripping, and media erase engine abstraction.
/// </summary>
public interface IBurnEngineBackend
{
    Task<BurnOperationResult> BurnDataStreamAsync(
        IDiscRecorderBackend recorder,
        Stream imageStream,
        DataBurnOptions options,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken cancellationToken);

    Task<BurnOperationResult> BurnAudioTracksAsync(
        IDiscRecorderBackend recorder,
        IReadOnlyList<AudioTrackSource> tracks,
        AudioBurnOptions options,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken cancellationToken);

    Task<BurnOperationResult> EraseMediaAsync(
        IDiscRecorderBackend recorder,
        bool fullErase,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken cancellationToken);

    Task<RipOperationResult> ReadSectorsAsync(
        IDiscRecorderBackend recorder,
        int startLba,
        int sectorCount,
        bool raw2352Mode,
        Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// Type of device change notification.
/// </summary>
public enum DeviceChangeType
{
    Added,
    Removed,
    MediaInserted,
    MediaEjected
}

/// <summary>
/// Event arguments for optical device changes.
/// </summary>
public class DeviceChangeEventArgs : EventArgs
{
    public string UniqueId { get; }
    public DeviceChangeType ChangeType { get; }

    public DeviceChangeEventArgs(string uniqueId, DeviceChangeType changeType)
    {
        UniqueId = uniqueId;
        ChangeType = changeType;
    }
}

/// <summary>
/// Drive optical burning capabilities and feature profile information.
/// </summary>
public class DriveCapabilitiesDto
{
    public bool CanWriteCdR { get; init; }
    public bool CanWriteCdRw { get; init; }
    public bool CanWriteDvdR { get; init; }
    public bool CanWriteDvdRw { get; init; }
    public bool CanWriteDvdPlusR { get; init; }
    public bool CanWriteDvdPlusRw { get; init; }
    public bool CanWriteDvdPlusRDL { get; init; }
    public bool CanWriteDvdMinusRDL { get; init; }
    public bool CanWriteDvdRam { get; init; }
    public bool SupportsTestWrite { get; init; }
    public bool SupportsBufferUnderrunProtection { get; init; }
    public int InternalBufferSize { get; init; }
    public IReadOnlyList<WriteSpeedDescriptor> SupportedWriteSpeeds { get; init; } = Array.Empty<WriteSpeedDescriptor>();
    public IReadOnlyList<DiscMediaType> SupportedMediaTypes { get; init; } = Array.Empty<DiscMediaType>();
}

/// <summary>
/// Status and geometry of the media currently inserted in a drive.
/// </summary>
public class MediaStatusDto
{
    public DiscMediaType MediaType { get; init; } = DiscMediaType.Unknown;
    public bool MediaPresent { get; init; }
    public bool IsBlank { get; init; }
    public bool IsRewritable { get; init; }
    public bool IsAppendable { get; init; }
    public int TotalSectors { get; init; }
    public int FreeSectors { get; init; }
    public int UsedSectors { get; init; }
    public long TotalCapacityBytes { get; init; }
    public long FreeCapacityBytes { get; init; }
    public int SessionCount { get; init; }
    public int Sessions => SessionCount;
    public string? MediaManufacturerId { get; init; }
    public int LastRecordedLba { get; init; }
    public int NextWritableLba { get; init; } = -1;
    public DvdBookType? BookType { get; init; }
}

/// <summary>
/// Real-time progress snapshot emitted during burning, ripping, or blanking operations.
/// </summary>
public class BurnProgressSnapshot
{
    public TimeSpan ElapsedTime { get; init; }
    public TimeSpan RemainingTime { get; init; }
    public int CurrentLba { get; init; }
    public int TotalLba { get; init; }
    public double BufferLevelPercent { get; init; }
    public double CurrentSpeedMultiplier { get; init; }
    public long CurrentBytesPerSecond { get; init; }
    public string CurrentActionDescription { get; init; } = string.Empty;
    public double ProgressPercentage => TotalLba > 0 ? Math.Clamp((double)CurrentLba / TotalLba * 100.0, 0.0, 100.0) : 0.0;
}

/// <summary>
/// Execution status of an optical media burn or erase operation.
/// </summary>
public enum BurnStatus
{
    NotStarted,
    InProgress,
    Succeeded,
    Failed,
    Cancelled
}

/// <summary>
/// Final result of a burn or erase operation.
/// </summary>
public class BurnOperationResult
{
    public bool Success { get; init; }
    public BurnStatus Status { get; init; } = BurnStatus.NotStarted;
    public string? ErrorMessage { get; init; }
    public int SectorsWritten { get; init; }
    public TimeSpan TotalTime { get; init; }
    public double AverageSpeedMultiplier { get; init; }
    public Exception? Exception { get; init; }

    public static BurnOperationResult Succeeded(int sectorsWritten, TimeSpan duration, double avgSpeed = 0.0) => new()
    {
        Success = true,
        Status = BurnStatus.Succeeded,
        SectorsWritten = sectorsWritten,
        TotalTime = duration,
        AverageSpeedMultiplier = avgSpeed
    };

    public static BurnOperationResult Failed(string error, Exception? ex = null) => new()
    {
        Success = false,
        Status = ex is OperationCanceledException ? BurnStatus.Cancelled : BurnStatus.Failed,
        ErrorMessage = error,
        Exception = ex
    };

    public static BurnOperationResult CancelledResult(string error = "Operation was cancelled.") => new()
    {
        Success = false,
        Status = BurnStatus.Cancelled,
        ErrorMessage = error
    };
}

/// <summary>
/// Final result of a disc ripping operation.
/// </summary>
public class RipOperationResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public int SectorsRead { get; init; }
    public int BadSectorsCount { get; init; }
    public TimeSpan TotalTime { get; init; }
    public IReadOnlyList<int> BadSectorsList { get; init; } = Array.Empty<int>();
    public IReadOnlyList<int> BadSectorLbas
    {
        get => BadSectorsList;
        init => BadSectorsList = value;
    }
    public int TotalRetriesPerformed { get; init; }
    public Exception? Exception { get; init; }

    public static RipOperationResult Succeeded(int sectorsRead, TimeSpan duration, IReadOnlyList<int>? badSectors = null) => new()
    {
        Success = true,
        SectorsRead = sectorsRead,
        TotalTime = duration,
        BadSectorsCount = badSectors?.Count ?? 0,
        BadSectorsList = badSectors ?? Array.Empty<int>()
    };

    public static RipOperationResult Failed(string error, Exception? ex = null) => new()
    {
        Success = false,
        ErrorMessage = error,
        Exception = ex
    };
}

/// <summary>
/// Configuration options for data disc burning.
/// </summary>
public class DataBurnOptions
{
    public WriteSpeedDescriptor? Speed { get; init; }
    public bool Simulate { get; init; }
    public bool EjectWhenDone { get; init; }
    public bool EjectAfterBurn { get => EjectWhenDone; init => EjectWhenDone = value; }
    public bool VerifyAfterBurn { get; init; }
    public bool CloseDisc { get; init; } = true;
    public string? VolumeLabel { get; init; }
}

/// <summary>
/// Configuration options for Red Book Audio CD burning.
/// </summary>
public class AudioBurnOptions
{
    public WriteSpeedDescriptor? Speed { get; init; }
    public bool Simulate { get; init; }
    public bool EjectWhenDone { get; init; }
    public bool PerformNormalization { get; init; }
    public bool WriteCdText { get; init; }
    public bool UseDiscAtOnce { get; init; } = true;
    public int PreGapFrames { get; init; } = OpticalConstants.CdLeadInFrames; // default 150 frames = 2.0s
    public byte[]? CdTextBytes { get; init; }
}

/// <summary>
/// Metadata and file reference for an audio track in an Audio CD compilation.
/// </summary>
public class AudioTrackSource
{
    public int TrackNumber { get; init; }
    public string FilePath { get; init; } = string.Empty;
    public string? Title { get; init; }
    public string? Performer { get; init; }
    public string? Songwriter { get; init; }
    public string? Composer { get; init; }
    public string? Isrc { get; init; }
    public int PreGapFrames { get; init; }
    public TimeSpan Duration { get; init; }
    public long TotalPcmBytes { get; init; }
    public Stream? PcmStream { get; init; }
    public Func<Stream>? PcmStreamFactory { get; init; }
}
