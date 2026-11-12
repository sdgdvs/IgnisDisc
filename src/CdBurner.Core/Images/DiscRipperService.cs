using System.Diagnostics;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Images;

/// <summary>
/// Configuration options for ripping optical discs to ISO or BIN/CUE images.
/// </summary>
public class RipOptions
{
    /// <summary>
    /// Maximum number of read retry attempts on an unreadable sector before failing or zero-filling.
    /// Default is 3 retries.
    /// </summary>
    public int MaxRetriesPerSector { get; set; } = 3;

    /// <summary>
    /// When true, unreadable sectors that fail after all retries are zero-filled and logged in
    /// <see cref="RipOperationResult.BadSectorLbas"/> instead of aborting the operation.
    /// </summary>
    public bool ContinueOnUnreadableSector { get; set; } = false;

    /// <summary>
    /// Starting LBA to rip from (default is 0).
    /// </summary>
    public int StartLba { get; set; } = 0;

    /// <summary>
    /// Optional total number of sectors to rip. If null, rips all recorded sectors on media.
    /// </summary>
    public int? SectorCount { get; set; }

    /// <summary>
    /// When true, reads raw 2352-byte sectors (for Red Book Audio, Mode 1/2 raw images);
    /// when false, reads standard 2048-byte user data sectors (for ISO images).
    /// </summary>
    public bool Raw2352Mode { get; set; } = false;

    /// <summary>
    /// Optional speed descriptor request for the rip operation.
    /// </summary>
    public WriteSpeedDescriptor? Speed { get; set; }

    /// <summary>
    /// Optional custom track descriptors for CUE sheet generation.
    /// </summary>
    public IReadOnlyList<CueTrack>? CustomTracks { get; set; }
}

/// <summary>
/// High-level orchestration service for ripping optical discs to .iso images or .bin + .cue raw images,
/// supporting bad sector retries, unreadable sector zero-fill logging, and CUE sheet layout generation.
/// </summary>
public class DiscRipperService
{
    /// <summary>
    /// Rips a data disc to a standard 2048-byte Mode 1 .iso disc image file on disk.
    /// </summary>
    public async Task<RipOperationResult> RipToIsoAsync(
        IDiscRecorderBackend recorder,
        IBurnEngineBackend engine,
        string outputIsoPath,
        RipOptions? options = null,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputIsoPath);

        string? dir = Path.GetDirectoryName(outputIsoPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        options ??= new RipOptions();
        RipOperationResult result;
        try
        {
            await using var fs = new FileStream(outputIsoPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
            result = await RipToIsoAsync(recorder, engine, fs, options, progress, ct).ConfigureAwait(false);
        }
        catch
        {
            TryDeleteIncompleteFile(outputIsoPath);
            throw;
        }

        if (!result.Success && !options.ContinueOnUnreadableSector)
        {
            TryDeleteIncompleteFile(outputIsoPath);
        }

        return result;
    }

    /// <summary>
    /// Rips a data disc to a destination stream containing 2048-byte Mode 1 .iso sectors.
    /// </summary>
    public async Task<RipOperationResult> RipToIsoAsync(
        IDiscRecorderBackend recorder,
        IBurnEngineBackend engine,
        Stream outputStream,
        RipOptions? options = null,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(outputStream);

        options ??= new RipOptions();
        options.Raw2352Mode = false;

        return await RipCoreAsync(recorder, engine, outputStream, options, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Rips an audio, mixed-mode, or raw data disc to a 2352-byte raw .bin image and generates
    /// a companion .cue sheet describing the session and track layout.
    /// </summary>
    public async Task<RipOperationResult> RipToBinCueAsync(
        IDiscRecorderBackend recorder,
        IBurnEngineBackend engine,
        string outputBinPath,
        string outputCuePath,
        RipOptions? options = null,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputBinPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputCuePath);

        string? binDir = Path.GetDirectoryName(outputBinPath);
        if (!string.IsNullOrEmpty(binDir)) Directory.CreateDirectory(binDir);
        string? cueDir = Path.GetDirectoryName(outputCuePath);
        if (!string.IsNullOrEmpty(cueDir)) Directory.CreateDirectory(cueDir);

        options ??= new RipOptions();
        options.Raw2352Mode = true;

        RipOperationResult ripResult;
        try
        {
            await using var binStream = new FileStream(outputBinPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
            ripResult = await RipCoreAsync(recorder, engine, binStream, options, progress, ct).ConfigureAwait(false);
        }
        catch
        {
            TryDeleteIncompleteFile(outputBinPath);
            throw;
        }

        if (!ripResult.Success && !options.ContinueOnUnreadableSector)
        {
            TryDeleteIncompleteFile(outputBinPath);
            return ripResult;
        }

        // Generate companion CUE sheet from disc's session/track layout
        var cueSheet = GenerateCueSheetForDisc(recorder, outputBinPath, options.StartLba, options.CustomTracks);
        await CueSheetWriter.WriteToFileAsync(cueSheet, outputCuePath, ct).ConfigureAwait(false);

        return ripResult;
    }

    private static void TryDeleteIncompleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    private async Task<RipOperationResult> RipCoreAsync(
        IDiscRecorderBackend recorder,
        IBurnEngineBackend engine,
        Stream outputStream,
        RipOptions options,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var mediaStatus = recorder.QueryMediaStatus();
        if (!mediaStatus.MediaPresent)
        {
            return RipOperationResult.Failed("No optical media present in recorder.");
        }

        int startLba = Math.Max(0, options.StartLba);
        int sectorSize = options.Raw2352Mode ? OpticalConstants.RawSectorSize : OpticalConstants.DataSectorSize;
        int sectorCount;

        if (options.SectorCount.HasValue && options.SectorCount.Value > 0)
        {
            sectorCount = options.SectorCount.Value;
        }
        else if (mediaStatus.UsedSectors > 0)
        {
            sectorCount = Math.Max(0, mediaStatus.UsedSectors - startLba);
        }
        else if (mediaStatus.LastRecordedLba >= 0)
        {
            sectorCount = Math.Max(0, (mediaStatus.LastRecordedLba + 1) - startLba);
        }
        else
        {
            // Inspect recorder via reflection if virtual medium
            int inspectedSectors = InspectVirtualMediumUsedSectors(recorder);
            if (inspectedSectors > 0)
            {
                sectorCount = Math.Max(0, inspectedSectors - startLba);
            }
            else
            {
                return RipOperationResult.Failed("No recorded sectors found on media to rip.");
            }
        }

        // A raw rip of a disc with audio runs to the lead-out in the table of contents. The used size Windows reports
        // can stop short of it (2 sectors on a Session-at-Once CD, measured 2026-11-11), which would cut the end of the
        // last track.
        if (options.Raw2352Mode && !options.SectorCount.HasValue && recorder is IDiscTocProvider tocProvider)
        {
            try
            {
                var toc = tocProvider.ReadTableOfContents();
                if (toc.Count > 0 && toc.Any(t => t.IsAudio))
                {
                    int tocEnd = toc.Max(t => t.StartLba + t.SectorCount);
                    sectorCount = Math.Max(sectorCount, tocEnd - startLba);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                // Without a table of contents the used size stands.
            }
        }

        if (sectorCount <= 0)
        {
            return RipOperationResult.Failed($"Invalid sector count ({sectorCount}) for rip operation.");
        }

        var sw = Stopwatch.StartNew();
        var badSectorLbas = new List<int>();
        int sectorsRead = 0;
        int totalRetries = 0;
        byte[] zeroBuffer = new byte[sectorSize];
        double speedMultiplier = options.Speed?.Multiplier > 0 ? options.Speed.Value.Multiplier : 24.0;
        long bytesPerSec = (long)(speedMultiplier * (options.Raw2352Mode ? OpticalConstants.CdAudio1XBytesPerSec : OpticalConstants.CdData1XBytesPerSec));

        void ReportProgress(int currentSector, string action)
        {
            if (progress == null) return;
            var elapsed = sw.Elapsed;
            var remaining = currentSector > 0 && sectorCount > currentSector
                ? TimeSpan.FromSeconds(elapsed.TotalSeconds / currentSector * (sectorCount - currentSector))
                : TimeSpan.Zero;

            progress.Report(new BurnProgressSnapshot
            {
                ElapsedTime = elapsed,
                RemainingTime = remaining,
                CurrentLba = currentSector,
                TotalLba = sectorCount,
                BufferLevelPercent = 100.0,
                CurrentSpeedMultiplier = speedMultiplier,
                CurrentBytesPerSecond = bytesPerSec,
                CurrentActionDescription = action
            });
        }

        ReportProgress(0, "Initializing");

        var outcome = await BatchedSectorReader.ReadRangeAsync(
            engine,
            recorder,
            startLba,
            sectorCount,
            options.Raw2352Mode,
            options.MaxRetriesPerSector,
            onSector: async (lba, data) =>
            {
                await outputStream.WriteAsync(data, ct).ConfigureAwait(false);
                sectorsRead++;
            },
            onUnreadable: async lba =>
            {
                badSectorLbas.Add(lba);
                if (!options.ContinueOnUnreadableSector)
                {
                    return false;
                }

                // Zero-fill unreadable sector
                await outputStream.WriteAsync(zeroBuffer, ct).ConfigureAwait(false);
                sectorsRead++;
                return true;
            },
            onProgress: done => ReportProgress(done, "RippingSectors"),
            cancellationToken: ct).ConfigureAwait(false);

        totalRetries = outcome.TotalRetries;

        if (!outcome.Completed)
        {
            return new RipOperationResult
            {
                Success = false,
                ErrorMessage = $"Unrecovered read error at LBA {outcome.StoppedAtLba} after {options.MaxRetriesPerSector} retries.",
                SectorsRead = sectorsRead,
                BadSectorsCount = badSectorLbas.Count,
                BadSectorsList = badSectorLbas,
                TotalTime = sw.Elapsed,
                TotalRetriesPerformed = totalRetries
            };
        }

        await outputStream.FlushAsync(ct).ConfigureAwait(false);
        ReportProgress(sectorCount, "Completed");

        return new RipOperationResult
        {
            Success = true,
            SectorsRead = sectorsRead,
            BadSectorsCount = badSectorLbas.Count,
            BadSectorsList = badSectorLbas,
            TotalTime = sw.Elapsed,
            TotalRetriesPerformed = totalRetries
        };
    }

    private static CueSheet GenerateCueSheetForDisc(
        IDiscRecorderBackend recorder,
        string outputBinPath,
        int startLba,
        IReadOnlyList<CueTrack>? customTracks)
    {
        string binFileName = Path.GetFileName(outputBinPath);
        var cueTracks = new List<CueTrack>();
        string? albumTitle = null;
        string? albumPerformer = null;

        if (customTracks != null && customTracks.Count > 0)
        {
            cueTracks.AddRange(customTracks);
        }
        else if (recorder is IDiscTocProvider tocProvider && tocProvider.ReadTableOfContents() is { Count: > 0 } toc)
        {
            // Physical drives: real track boundaries and types from the TOC, names from CD-Text when present.
            CdTextAlbumMetadata? cdText = null;
            if (tocProvider.ReadCdTextPacks() is { Length: > 0 } packs)
            {
                try { cdText = CdTextPackParser.ParsePacks(packs); }
                catch (Exception ex) when (ex is ArgumentException or InvalidDataException) { }
            }
            albumTitle = string.IsNullOrEmpty(cdText?.AlbumTitle) ? null : cdText.AlbumTitle;
            albumPerformer = string.IsNullOrEmpty(cdText?.AlbumPerformer) ? null : cdText.AlbumPerformer;

            foreach (var t in toc.Where(t => t.StartLba >= startLba).OrderBy(t => t.TrackNumber))
            {
                var text = cdText?.Tracks?.FirstOrDefault(x => x.TrackNumber == t.TrackNumber);
                cueTracks.Add(new CueTrack(
                    TrackNumber: t.TrackNumber,
                    DataType: t.IsAudio ? CueTrackDataType.Audio : CueTrackDataType.Mode1_2352,
                    Title: string.IsNullOrEmpty(text?.Title) ? null : text.Title,
                    Performer: string.IsNullOrEmpty(text?.Performer) ? null : text.Performer,
                    Isrc: string.IsNullOrEmpty(text?.Isrc) ? null : text.Isrc,
                    Indices: new[] { new CueIndex(1, MsfAddress.FromFrameCount(t.StartLba - startLba)) }));
            }
        }
        else
        {
            // Inspect recorder via reflection for VirtualDiscMedium session/track layout
            var mediumProp = recorder.GetType().GetProperty("CurrentMedium");
            if (mediumProp?.GetValue(recorder) is object mediumObj)
            {
                var rawModeProp = mediumObj.GetType().GetProperty("Raw2352Mode");
                bool isRaw = rawModeProp?.GetValue(mediumObj) is bool b && b;

                var sessionsProp = mediumObj.GetType().GetProperty("Sessions");
                if (sessionsProp?.GetValue(mediumObj) is System.Collections.IEnumerable sessions)
                {
                    int defaultTrackNum = 1;
                    foreach (var sessionItem in sessions)
                    {
                        var tracksProp = sessionItem.GetType().GetProperty("Tracks");
                        if (tracksProp?.GetValue(sessionItem) is System.Collections.IEnumerable tracks)
                        {
                            foreach (var trk in tracks)
                            {
                                var tType = trk.GetType();
                                int trackNum = tType.GetProperty("TrackNumber")?.GetValue(trk) is int tn ? tn : defaultTrackNum;
                                int trkStartLba = tType.GetProperty("StartLba")?.GetValue(trk) is int sl ? sl : 0;
                                bool isAudio = tType.GetProperty("IsAudio")?.GetValue(trk) is bool ia && ia;
                                int preGap = tType.GetProperty("PreGapFrames")?.GetValue(trk) is int pg ? pg : 0;
                                string? title = tType.GetProperty("Title")?.GetValue(trk) as string;
                                string? performer = tType.GetProperty("Performer")?.GetValue(trk) as string;
                                string? songwriter = tType.GetProperty("Songwriter")?.GetValue(trk) as string;
                                string? isrc = tType.GetProperty("Isrc")?.GetValue(trk) as string;

                                int relLba = Math.Max(0, trkStartLba - startLba);
                                var dt = isAudio
                                    ? CueTrackDataType.Audio
                                    : (isRaw ? CueTrackDataType.Mode1_2352 : CueTrackDataType.Mode1_2048);

                                cueTracks.Add(new CueTrack(
                                    TrackNumber: trackNum,
                                    DataType: dt,
                                    Title: title,
                                    Performer: performer,
                                    Songwriter: songwriter,
                                    Isrc: isrc,
                                    PreGap: preGap > 0 ? MsfAddress.FromFrameCount(preGap) : null,
                                    // CUE INDEX positions are frame offsets within the FILE, without the 150-frame lead-in.
                                    Indices: new[] { new CueIndex(1, MsfAddress.FromFrameCount(relLba)) }
                                ));
                                defaultTrackNum = trackNum + 1;
                            }
                        }
                    }
                }
            }
        }

        if (cueTracks.Count == 0)
        {
            cueTracks.Add(new CueTrack(
                TrackNumber: 1,
                DataType: CueTrackDataType.Mode1_2352,
                Indices: new[] { new CueIndex(1, new MsfAddress(0, 0, 0)) }
            ));
        }

        var fileEntry = new CueFileEntry(binFileName, "BINARY", cueTracks);
        return new CueSheet(Title: albumTitle, Performer: albumPerformer, Files: new[] { fileEntry });
    }

    private static int InspectVirtualMediumUsedSectors(IDiscRecorderBackend recorder)
    {
        try
        {
            var mediumProp = recorder.GetType().GetProperty("CurrentMedium");
            if (mediumProp?.GetValue(recorder) is object mediumObj)
            {
                var usedSectorsProp = mediumObj.GetType().GetProperty("UsedSectors");
                if (usedSectorsProp?.GetValue(mediumObj) is int us && us > 0)
                {
                    return us;
                }
            }
        }
        catch
        {
            // Ignore reflection errors on physical recorder backends
        }

        return 0;
    }
}
