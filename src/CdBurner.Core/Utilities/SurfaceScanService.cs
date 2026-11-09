using System.Diagnostics;
using System.Globalization;
using System.Text;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Utilities;

/// <summary>
/// Optical sector surface classification representing read integrity and latency.
/// </summary>
public enum SectorHealthStatus
{
    /// <summary>
    /// Sector was read successfully with normal transfer rate and zero ECC errors.
    /// </summary>
    Good,

    /// <summary>
    /// Sector was read successfully but suffered high latency or read speed below configured threshold.
    /// </summary>
    Slow,

    /// <summary>
    /// Sector failed to read due to unrecovered hardware read errors, bad block sense errors, or physical damage.
    /// </summary>
    Damaged
}

/// <summary>
/// Measured performance and health outcome for a contiguous block of scanned optical sectors.
/// </summary>
/// <param name="StartLba">Starting Logical Block Address of the block.</param>
/// <param name="SectorCount">Total number of sectors contained within the block.</param>
/// <param name="Status">Health classification for this block (Good, Slow, or Damaged).</param>
/// <param name="ReadSpeedKilobytesPerSec">Measured read throughput in KiB/s (1024 bytes/sec).</param>
/// <param name="ReadSpeedMultiplierX">Optical transfer rate multiplier relative to CD/DVD 1X reference rates.</param>
/// <param name="Elapsed">Total duration elapsed while reading this block.</param>
public sealed record SurfaceScanBlockResult(
    int StartLba,
    int SectorCount,
    SectorHealthStatus Status,
    double ReadSpeedKilobytesPerSec,
    double ReadSpeedMultiplierX,
    TimeSpan Elapsed);

/// <summary>
/// Summary report of an optical disc surface scan and transfer benchmark.
/// </summary>
/// <param name="TotalSectorsScanned">Total number of logical sectors tested across all blocks.</param>
/// <param name="GoodBlocks">Number of blocks classified as healthy (<see cref="SectorHealthStatus.Good"/>).</param>
/// <param name="SlowBlocks">Number of blocks classified as delayed (<see cref="SectorHealthStatus.Slow"/>).</param>
/// <param name="DamagedBlocks">Number of blocks with unreadable sectors (<see cref="SectorHealthStatus.Damaged"/>).</param>
/// <param name="MinSpeedKbPerSec">Minimum transfer rate achieved across valid blocks in KiB/s.</param>
/// <param name="MaxSpeedKbPerSec">Maximum transfer rate achieved across valid blocks in KiB/s.</param>
/// <param name="AverageSpeedKbPerSec">Average transfer rate achieved across valid blocks in KiB/s.</param>
/// <param name="Blocks">Individual scan results for every block on the disc surface.</param>
public sealed record SurfaceScanReport(
    int TotalSectorsScanned,
    int GoodBlocks,
    int SlowBlocks,
    int DamagedBlocks,
    double MinSpeedKbPerSec,
    double MaxSpeedKbPerSec,
    double AverageSpeedKbPerSec,
    IReadOnlyList<SurfaceScanBlockResult> Blocks)
{
    /// <summary>
    /// Serializes the block-by-block LBA transfer rate and health results to standard CSV format.
    /// </summary>
    public string ToCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine("BlockIndex,StartLba,EndLba,SectorCount,Status,ReadSpeedKiBPerSec,ReadSpeedMultiplierX,ElapsedMs");
        if (Blocks != null)
        {
            for (int i = 0; i < Blocks.Count; i++)
            {
                var b = Blocks[i];
                int endLba = b.SectorCount > 0 ? b.StartLba + b.SectorCount - 1 : b.StartLba;
                sb.Append(i.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(b.StartLba.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(endLba.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(b.SectorCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                  .Append(b.Status).Append(',')
                  .Append(b.ReadSpeedKilobytesPerSec.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
                  .Append(b.ReadSpeedMultiplierX.ToString("F2", CultureInfo.InvariantCulture)).Append(',')
                  .Append(b.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture))
                  .AppendLine();
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Writes the CSV transfer rate and sector health table to a file on disk.
    /// </summary>
    public async Task ExportCsvAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        string? dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllTextAsync(filePath, ToCsv(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken)
            .ConfigureAwait(false);
    }
}

/// <summary>
/// Configuration parameters for an optical surface scan and read benchmark.
/// </summary>
public class SurfaceScanOptions
{
    /// <summary>
    /// Number of contiguous sectors scanned per block (default 256 sectors).
    /// </summary>
    public int BlockSizeSectors { get; init; } = 256;

    /// <summary>
    /// Starting Logical Block Address on media (default 0).
    /// </summary>
    public int StartLba { get; init; } = 0;

    /// <summary>
    /// Total number of sectors to scan. If null, scans all available sectors on the media.
    /// </summary>
    public int? SectorCount { get; init; }

    /// <summary>
    /// Read throughput threshold in KiB/s below which a block is classified as Slow (0.0 = disabled).
    /// </summary>
    public double SlowSpeedThresholdKbPerSec { get; init; } = 0.0;

    /// <summary>
    /// Optical speed multiplier threshold below which a block is classified as Slow (0.0 = disabled).
    /// </summary>
    public double SlowSpeedMultiplierThreshold { get; init; } = 0.0;

    /// <summary>
    /// Whether to read in raw 2,352-byte frame mode (CD-DA / raw mode) or standard 2,048-byte Mode 1 blocks.
    /// </summary>
    public bool Raw2352Mode { get; init; }

    /// <summary>
    /// Optional real-time progress reporter for each individual block scanned.
    /// </summary>
    public IProgress<SurfaceScanBlockResult>? BlockProgress { get; init; }
}

/// <summary>
/// High-level orchestration service for scanning optical disc surfaces, measuring transfer rate curves,
/// and building block-level defect maps (Good, Slow, Damaged sectors).
/// </summary>
public class SurfaceScanService
{
    private readonly IBurnEngineBackend? _defaultEngine;

    /// <summary>
    /// Gets the default burning engine backend configured for this service instance, if any.
    /// </summary>
    public IBurnEngineBackend? BurnEngine => _defaultEngine;

    /// <summary>
    /// Initializes a new instance of <see cref="SurfaceScanService"/> with an optional burning engine backend.
    /// </summary>
    /// <param name="burnEngine">Optional default burn engine backend.</param>
    public SurfaceScanService(IBurnEngineBackend? burnEngine = null)
    {
        _defaultEngine = burnEngine;
    }

    /// <summary>
    /// Scans the surface of media in the target recorder using the constructor-injected engine backend.
    /// </summary>
    public Task<SurfaceScanReport> ScanAsync(
        IDiscRecorderBackend recorder,
        SurfaceScanOptions? options = null,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (_defaultEngine == null)
        {
            throw new InvalidOperationException("No burn engine was provided to SurfaceScanService constructor.");
        }

        return ScanAsync(recorder, _defaultEngine, options, progress, cancellationToken);
    }

    /// <summary>
    /// Scans the surface of media in the target recorder using a specified block size in sectors.
    /// </summary>
    public Task<SurfaceScanReport> ScanAsync(
        IDiscRecorderBackend recorder,
        int blockSizeSectors,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return ScanAsync(recorder, new SurfaceScanOptions { BlockSizeSectors = blockSizeSectors }, progress, cancellationToken);
    }

    /// <summary>
    /// Scans the surface of media in the target recorder using the specified engine and block size.
    /// </summary>
    public Task<SurfaceScanReport> ScanAsync(
        IDiscRecorderBackend recorder,
        IBurnEngineBackend engine,
        int blockSizeSectors,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return ScanAsync(recorder, engine, new SurfaceScanOptions { BlockSizeSectors = blockSizeSectors }, progress, cancellationToken);
    }

    /// <summary>
    /// Scans the surface of media in the target recorder using the specified burning engine backend and options.
    /// </summary>
    public async Task<SurfaceScanReport> ScanAsync(
        IDiscRecorderBackend recorder,
        IBurnEngineBackend engine,
        SurfaceScanOptions? options = null,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(engine);

        options ??= new SurfaceScanOptions();
        cancellationToken.ThrowIfCancellationRequested();

        // 1. Query media status and determine total sectors to scan
        var mediaStatus = recorder.QueryMediaStatus();
        if (!mediaStatus.MediaPresent)
        {
            throw new InvalidOperationException("No optical media present in recorder.");
        }

        int startLba = Math.Max(0, options.StartLba);
        var scanRanges = BuildScanRanges(recorder, mediaStatus, options, startLba);
        int totalSectors = scanRanges.Sum(r => r.SectorCount);

        if (totalSectors <= 0)
        {
            return new SurfaceScanReport(
                TotalSectorsScanned: 0,
                GoodBlocks: 0,
                SlowBlocks: 0,
                DamagedBlocks: 0,
                MinSpeedKbPerSec: 0.0,
                MaxSpeedKbPerSec: 0.0,
                AverageSpeedKbPerSec: 0.0,
                Blocks: Array.Empty<SurfaceScanBlockResult>());
        }

        int blockSize = options.BlockSizeSectors > 0 ? options.BlockSizeSectors : 256;

        var blocks = new List<SurfaceScanBlockResult>();
        var overallSw = Stopwatch.StartNew();
        int scannedSoFarTotal = 0;

        foreach (var range in scanRanges)
        {
            int sectorSize = range.Raw2352Mode ? OpticalConstants.RawSectorSize : OpticalConstants.DataSectorSize;
            int baseRate = WriteSpeedDescriptor.GetBaseRate(mediaStatus.MediaType, isAudio: range.Raw2352Mode);
            int currentLba = range.StartLba;
            int endLba = range.StartLba + range.SectorCount;

            while (currentLba < endLba)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int currentBlockSize = Math.Min(blockSize, endLba - currentLba);
                var blockSw = Stopwatch.StartNew();
                bool isDamaged = false;
                int sectorsReadInBlock = 0;

                try
                {
                    var ripResult = await engine.ReadSectorsAsync(
                        recorder,
                        startLba: currentLba,
                        sectorCount: currentBlockSize,
                        raw2352Mode: range.Raw2352Mode,
                        sectorConsumer: (lba, memory) =>
                        {
                            sectorsReadInBlock++;
                            return ValueTask.CompletedTask;
                        },
                        progress: null,
                        cancellationToken: cancellationToken).ConfigureAwait(false);

                    if (!ripResult.Success || ripResult.BadSectorsCount > 0 || sectorsReadInBlock < currentBlockSize)
                    {
                        isDamaged = true;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    isDamaged = true;
                }
                finally
                {
                    blockSw.Stop();
                }

                SectorHealthStatus status;
                double kbPerSec;
                double multiplier;
                TimeSpan elapsed = blockSw.Elapsed;

                if (isDamaged)
                {
                    status = SectorHealthStatus.Damaged;
                    kbPerSec = 0.0;
                    multiplier = 0.0;
                }
                else
                {
                    double seconds = Math.Max(elapsed.TotalSeconds, 0.000001);
                    long bytesRead = (long)currentBlockSize * sectorSize;
                    double bytesPerSec = bytesRead / seconds;
                    kbPerSec = bytesPerSec / 1024.0;
                    multiplier = baseRate > 0 ? bytesPerSec / baseRate : 0.0;

                    if ((options.SlowSpeedThresholdKbPerSec > 0 && kbPerSec < options.SlowSpeedThresholdKbPerSec) ||
                        (options.SlowSpeedMultiplierThreshold > 0 && multiplier < options.SlowSpeedMultiplierThreshold))
                    {
                        status = SectorHealthStatus.Slow;
                    }
                    else
                    {
                        status = SectorHealthStatus.Good;
                    }
                }

                var blockResult = new SurfaceScanBlockResult(
                    StartLba: currentLba,
                    SectorCount: currentBlockSize,
                    Status: status,
                    ReadSpeedKilobytesPerSec: kbPerSec,
                    ReadSpeedMultiplierX: multiplier,
                    Elapsed: elapsed);

                blocks.Add(blockResult);
                options.BlockProgress?.Report(blockResult);
                scannedSoFarTotal += currentBlockSize;

                if (progress != null)
                {
                    TimeSpan overallElapsed = overallSw.Elapsed;
                    TimeSpan remaining = scannedSoFarTotal > 0 && totalSectors > scannedSoFarTotal
                        ? TimeSpan.FromSeconds(overallElapsed.TotalSeconds / scannedSoFarTotal * (totalSectors - scannedSoFarTotal))
                        : TimeSpan.Zero;

                    progress.Report(new BurnProgressSnapshot
                    {
                        ElapsedTime = overallElapsed,
                        RemainingTime = remaining,
                        CurrentLba = scannedSoFarTotal,
                        TotalLba = totalSectors,
                        BufferLevelPercent = 100.0,
                        CurrentSpeedMultiplier = multiplier,
                        CurrentBytesPerSecond = (long)(kbPerSec * 1024.0),
                        CurrentActionDescription = $"Scanning LBA {currentLba}..{currentLba + currentBlockSize - 1} ({status})"
                    });
                }

                currentLba += currentBlockSize;
            }
        }

        overallSw.Stop();

        int goodBlocks = blocks.Count(b => b.Status == SectorHealthStatus.Good);
        int slowBlocks = blocks.Count(b => b.Status == SectorHealthStatus.Slow);
        int damagedBlocks = blocks.Count(b => b.Status == SectorHealthStatus.Damaged);
        int totalSectorsScanned = blocks.Sum(b => b.SectorCount);

        var validBlocks = blocks.Where(b => b.Status != SectorHealthStatus.Damaged && b.ReadSpeedKilobytesPerSec > 0).ToList();
        double minSpeed = validBlocks.Count > 0 ? validBlocks.Min(b => b.ReadSpeedKilobytesPerSec) : 0.0;
        double maxSpeed = validBlocks.Count > 0 ? validBlocks.Max(b => b.ReadSpeedKilobytesPerSec) : 0.0;
        double avgSpeed = validBlocks.Count > 0 ? validBlocks.Average(b => b.ReadSpeedKilobytesPerSec) : 0.0;

        return new SurfaceScanReport(
            TotalSectorsScanned: totalSectorsScanned,
            GoodBlocks: goodBlocks,
            SlowBlocks: slowBlocks,
            DamagedBlocks: damagedBlocks,
            MinSpeedKbPerSec: minSpeed,
            MaxSpeedKbPerSec: maxSpeed,
            AverageSpeedKbPerSec: avgSpeed,
            Blocks: blocks);
    }

    private static List<(int StartLba, int SectorCount, bool Raw2352Mode)> BuildScanRanges(
        IDiscRecorderBackend recorder,
        MediaStatusDto mediaStatus,
        SurfaceScanOptions options,
        int startLba)
    {
        // When no explicit sector sub-range was requested and the drive exposes a CD Table of Contents, scan each
        // recorded track individually so multi-session inter-session gaps (~11,400 unrecorded sectors) are skipped
        // and CD-DA audio tracks are read in 2352-byte raw mode automatically.
        if (startLba == 0 && !options.SectorCount.HasValue &&
            recorder is IDiscTocProvider tocProvider &&
            tocProvider.ReadTableOfContents() is { Count: > 0 } tocTracks)
        {
            var tocRanges = new List<(int StartLba, int SectorCount, bool Raw2352Mode)>(tocTracks.Count);
            foreach (var track in tocTracks)
            {
                int tStart = Math.Max(0, track.StartLba);
                int tCount = track.SectorCount;
                if (!track.IsAudio && mediaStatus.UsedSectors > tStart && tStart + tCount > mediaStatus.UsedSectors)
                {
                    tCount = mediaStatus.UsedSectors - tStart;
                }

                if (tCount > 0)
                {
                    tocRanges.Add((tStart, tCount, options.Raw2352Mode || track.IsAudio));
                }
            }

            if (tocRanges.Count > 0)
            {
                return tocRanges;
            }
        }

        int totalSectors;
        if (options.SectorCount.HasValue && options.SectorCount.Value > 0)
        {
            totalSectors = options.SectorCount.Value;
        }
        else if (mediaStatus.UsedSectors > 0)
        {
            totalSectors = Math.Max(0, mediaStatus.UsedSectors - startLba);
        }
        else if (mediaStatus.TotalSectors > 0)
        {
            totalSectors = Math.Max(0, mediaStatus.TotalSectors - startLba);
        }
        else if (mediaStatus.LastRecordedLba >= 0)
        {
            totalSectors = Math.Max(0, (mediaStatus.LastRecordedLba + 1) - startLba);
        }
        else
        {
            totalSectors = 0;
        }

        totalSectors = Math.Min(totalSectors, int.MaxValue - startLba);
        return totalSectors > 0
            ? new List<(int StartLba, int SectorCount, bool Raw2352Mode)> { (startLba, totalSectors, options.Raw2352Mode) }
            : new List<(int StartLba, int SectorCount, bool Raw2352Mode)>();
    }
}
