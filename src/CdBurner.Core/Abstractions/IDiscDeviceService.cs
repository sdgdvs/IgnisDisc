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

    /// <summary>
    /// Reads 2,048-byte data sectors for verification: a real drive is told to read the disc itself (READ (10) with
    /// FUA), not a copy of the sectors left in its memory by the burn. By default that is done for the stretches
    /// that can still be in its memory (the last sectors written, and the start); with <paramref name="strict"/> for
    /// every read, which is several times slower. Backends without a drive cache read normally.
    /// </summary>
    /// <param name="writtenRange">
    /// The whole stretch being checked when this call reads only a piece of it, so "the last sectors written" is
    /// judged against the whole and not against each piece. Null: the call's own range is the whole.
    /// </param>
    Task<RipOperationResult> ReadSectorsForVerifyAsync(
        IDiscRecorderBackend recorder,
        int startLba,
        int sectorCount,
        Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken cancellationToken,
        bool strict = false,
        SectorRange? writtenRange = null) =>
        ReadSectorsAsync(recorder, startLba, sectorCount, raw2352Mode: false, sectorConsumer, progress, cancellationToken);
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
    public bool SupportsBufferUnderrunFree { get; init; }
    public bool SupportsNativeTao { get; init; }
    public bool SupportsNativeSao { get; init; }
    /// <summary>The drive records user-supplied R-W subcode in Session-at-Once (CD Mastering R-W bit), which CD-Text needs.</summary>
    public bool SupportsNativeCdText { get; init; }
    public bool SupportsNativeRawDao { get; init; }
    public bool SupportsLayerJump { get; init; }
    public IReadOnlyList<string> MmcFeatureSummary { get; init; } = Array.Empty<string>();
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

    /// <summary>
    /// True for media that are rewritten in place rather than appended to or finalized (DVD+RW, DVD-RAM,
    /// restricted-overwrite DVD-RW). A burn replaces everything on them.
    /// </summary>
    public bool IsOverwriteOnly { get; init; }

    /// <summary>
    /// True when burning to this disc would replace files already on it (a used DVD+RW or similar).
    /// </summary>
    public bool HasDataThatBurningReplaces => MediaPresent && IsOverwriteOnly && !IsBlank;

    /// <summary>
    /// True when the inserted disc is a sequential rewritable medium (such as CD-RW or sequential DVD-RW)
    /// that has been finalized or filled and must be erased before it can be written again.
    /// </summary>
    public bool RequiresEraseToWrite => MediaPresent && IsRewritable && !IsOverwriteOnly && !IsBlank && !IsAppendable;

    public int TotalSectors { get; init; }
    public int FreeSectors { get; init; }
    public int UsedSectors { get; init; }
    public long TotalCapacityBytes { get; init; }
    public long FreeCapacityBytes { get; init; }
    public int SessionCount { get; init; }
    public int Sessions => SessionCount;
    public string? MediaManufacturerId { get; set; }

    /// <summary>True when the media ID belongs to M-DISC archival media (see <see cref="MDiscIdentifier"/>).</summary>
    public bool IsMDisc => MDiscIdentifier.IsMDisc(MediaManufacturerId);
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

    /// <summary>
    /// A plain-language remark about how the sectors were read, e.g. that the drive refused to bypass its cache and
    /// the cache was flushed instead. Null when there is nothing to say.
    /// </summary>
    public string? Note { get; init; }

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
/// <summary>A run of sectors on a disc.</summary>
public readonly record struct SectorRange(int Start, int Count);

/// <summary>The user's standing choice for how a burn is checked; set once from the app's settings.</summary>
public static class VerifyPreferences
{
    /// <summary>New <see cref="DataBurnOptions"/> ask for a strict verify (every read forced from the disc).</summary>
    public static bool StrictByDefault { get; set; }
}

public class DataBurnOptions
{
    public WriteSpeedDescriptor? Speed { get; init; }
    public bool Simulate { get; init; }
    public bool EjectWhenDone { get; init; }
    public bool EjectAfterBurn { get => EjectWhenDone; init => EjectWhenDone = value; }
    public bool VerifyAfterBurn { get; init; }

    /// <summary>
    /// Verify with every read forced from the disc. Off: only the stretches that can still be in the drive's memory
    /// after the burn are forced, and the rest is read at normal speed. Starts from
    /// <see cref="VerifyPreferences.StrictByDefault"/>, so the app's setting reaches every burn path.
    /// </summary>
    public bool StrictVerify { get; init; } = VerifyPreferences.StrictByDefault;
    public bool CloseDisc { get; init; } = true;
    public string? VolumeLabel { get; init; }

    /// <summary>
    /// Allows a burn to replace the files already on a rewritable disc such as a used DVD+RW. Off by default so data
    /// is never destroyed without the user agreeing to it.
    /// </summary>
    public bool OverwriteExistingData { get; init; }
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

    /// <summary>
    /// 13-digit UPC/EAN written into the disc's sub-channel. Only the direct drive-command writer can write it;
    /// the Windows burning service ignores it.
    /// </summary>
    public string? MediaCatalogNumber { get; init; }

    /// <summary>
    /// Keep a CUE sheet's INDEX 00 marks and the audio before track 1 (see
    /// <see cref="AudioTrackSource.PreGapPcmStreamFactory"/>). Set only for the direct drive-command writer: the
    /// Windows burning service writes every pre-gap as silence and would lose that audio.
    /// </summary>
    public bool KeepIndexMarks { get; init; }
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

    /// <summary>
    /// Audio that belongs in the last <see cref="PreGapAudioFrames"/> frames of this track's pre-gap (its INDEX 00
    /// area): the count-in before a track, or hidden audio before track 1. Written only by the direct
    /// drive-command writer; other engines ignore it.
    /// </summary>
    public Func<Stream>? PreGapPcmStreamFactory { get; init; }

    /// <summary>Length of the pre-gap audio in CD frames (1/75 s).</summary>
    public int PreGapAudioFrames { get; init; }

    /// <summary>The track was mastered with pre-emphasis (CUE "FLAGS PRE").</summary>
    public bool PreEmphasis { get; init; }

    /// <summary>Digital copying of the track is permitted (CUE "FLAGS DCP").</summary>
    public bool CopyPermitted { get; init; }
}
