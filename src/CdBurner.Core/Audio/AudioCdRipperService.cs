using System.Diagnostics;
using System.Text;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Images;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Audio;

/// <summary>
/// Configuration options for ripping Audio CD tracks to uncompressed WAV files and companion CUE sheet.
/// </summary>
public class AudioRipOptions
{
    /// <summary>
    /// Maximum read retry attempts per sector on unreadable audio sectors. Default is 3.
    /// </summary>
    public int MaxRetriesPerSector { get; set; } = 3;

    /// <summary>
    /// When true, unreadable sectors that fail after all retries are zero-filled instead of aborting the operation.
    /// </summary>
    public bool ContinueOnUnreadableSector { get; set; } = false;

    /// <summary>
    /// Whether to generate a companion .cue sheet describing the ripped audio files and track layout. Default is true.
    /// </summary>
    public bool GenerateCueSheet { get; set; } = true;

    /// <summary>
    /// Filename of the generated .cue sheet in the output directory. Default is "album.cue".
    /// </summary>
    public string CueSheetFileName { get; set; } = "album.cue";

    /// <summary>
    /// Optional subset of 1-based track numbers to rip. If null or empty, all tracks are ripped.
    /// </summary>
    public IReadOnlyList<int>? TrackNumbersToRip { get; set; }

    /// <summary>
    /// Optional metadata (e.g. from an online lookup) that takes precedence over CD-Text for file names, the CUE
    /// sheet and <see cref="AudioCdRipResult.CdText"/>. Tracks it does not cover keep their CD-Text or generic names.
    /// </summary>
    public CdTextAlbumMetadata? MetadataOverride { get; set; }
}

/// <summary>
/// Discovered audio track descriptor extracted from optical medium TOC or session metadata.
/// </summary>
public record AudioDiscoveredTrack(
    int TrackNumber,
    int StartLba,
    int SectorCount,
    int PreGapFrames,
    string Title,
    string Performer,
    string? Songwriter = null,
    string? Composer = null,
    string? Isrc = null);

/// <summary>
/// Final result of an Audio CD ripping operation.
/// </summary>
public class AudioCdRipResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public int TracksRipped { get; init; }
    public int TotalSectorsRead { get; init; }
    public TimeSpan TotalTime { get; init; }
    public IReadOnlyList<string> RippedFilePaths { get; init; } = Array.Empty<string>();
    public string? CueSheetPath { get; init; }
    public CdTextAlbumMetadata? CdText { get; init; }
    public Exception? Exception { get; init; }

    public static AudioCdRipResult Succeeded(
        IReadOnlyList<string> filePaths,
        int totalSectors,
        TimeSpan duration,
        string? cueSheetPath = null,
        CdTextAlbumMetadata? cdText = null) => new()
    {
        Success = true,
        TracksRipped = filePaths.Count,
        TotalSectorsRead = totalSectors,
        TotalTime = duration,
        RippedFilePaths = filePaths,
        CueSheetPath = cueSheetPath,
        CdText = cdText
    };

    public static AudioCdRipResult Failed(string error, Exception? ex = null) => new()
    {
        Success = false,
        ErrorMessage = error,
        Exception = ex
    };
}

/// <summary>
/// Digital Audio Extraction (DAE) ripper service extracting Red Book 2352-byte raw sectors
/// into canonical 44.1 kHz 16-bit Stereo RIFF WAVE (.wav) files with decoded CD-Text metadata and companion CUE sheet.
/// </summary>
public class AudioCdRipperService
{
    private static readonly HashSet<string> ReservedWindowsDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// Sanitizes a string for use in Windows file paths, replacing illegal characters with underscores.
    /// </summary>
    public static string SanitizeFileName(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var invalidChars = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(input.Length);
        foreach (char c in input)
        {
            sb.Append(invalidChars.Contains(c) ? '_' : c);
        }

        string result = sb.ToString().Trim().TrimEnd('.');
        if (result.Length == 0 || result is "." or "..")
        {
            return "Track";
        }

        string baseName = Path.GetFileNameWithoutExtension(result);
        if (ReservedWindowsDeviceNames.Contains(result) || ReservedWindowsDeviceNames.Contains(baseName))
        {
            result = "_" + result;
        }

        if (result.Length > 100)
        {
            result = result[..100].TrimEnd('.', ' ');
        }

        return string.IsNullOrEmpty(result) ? "Track" : result;
    }

    /// <summary>
    /// Inspects the inserted optical medium for session/track layout and decodes any recorded CD-Text packs.
    /// </summary>
    public static (IReadOnlyList<AudioDiscoveredTrack> Tracks, CdTextAlbumMetadata? CdText) QueryTracksAndCdText(
        IDiscRecorderBackend recorder)
    {
        ArgumentNullException.ThrowIfNull(recorder);

        CdTextAlbumMetadata? cdText = null;
        var tracks = new List<AudioDiscoveredTrack>();

        // Physical drives: read the real Table of Contents and any CD-Text in the lead-in
        if (recorder is IDiscTocProvider tocProvider)
        {
            var toc = tocProvider.ReadTableOfContents();
            if (toc.Any(t => t.IsAudio))
            {
                cdText = TryParseCdText(tocProvider.ReadCdTextPacks());
            }

            foreach (var t in toc.Where(t => t.IsAudio))
            {
                var (title, performer, songwriter, composer, isrc) = MergeCdText(t.TrackNumber, null, null, null, null, null, cdText);
                tracks.Add(new AudioDiscoveredTrack(
                    t.TrackNumber,
                    t.StartLba,
                    t.SectorCount,
                    t.TrackNumber == 1 ? OpticalConstants.CdLeadInFrames : 0,
                    title,
                    performer,
                    songwriter,
                    composer,
                    isrc));
            }

            if (tracks.Count > 0)
            {
                return (tracks, cdText);
            }
        }

        // Check if recorder exposes CurrentMedium (VirtualDiscRecorder or via reflection)
        object? mediumObj = null;
        var currentMediumProp = recorder.GetType().GetProperty("CurrentMedium");
        if (currentMediumProp != null)
        {
            mediumObj = currentMediumProp.GetValue(recorder);
        }

        if (mediumObj != null)
        {
            // Read optional CdTextBytes
            var cdTextBytesProp = mediumObj.GetType().GetProperty("CdTextBytes");
            cdText = TryParseCdText(cdTextBytesProp?.GetValue(mediumObj) as byte[]);

            // Read Sessions and Tracks
            var sessionsProp = mediumObj.GetType().GetProperty("Sessions");
            if (sessionsProp?.GetValue(mediumObj) is System.Collections.IEnumerable sessionsList)
            {
                foreach (var session in sessionsList)
                {
                    var tracksProp = session.GetType().GetProperty("Tracks");
                    if (tracksProp?.GetValue(session) is System.Collections.IEnumerable trkList)
                    {
                        foreach (var t in trkList)
                        {
                            var tType = t.GetType();
                            int trkNum = tType.GetProperty("TrackNumber")?.GetValue(t) is int tn ? tn : (tracks.Count + 1);
                            int startLba = tType.GetProperty("StartLba")?.GetValue(t) is int sl ? sl : 0;
                            int count = tType.GetProperty("SectorCount")?.GetValue(t) is int sc ? sc : 0;
                            int preGap = tType.GetProperty("PreGapFrames")?.GetValue(t) is int pg ? pg : 0;
                            string? title = tType.GetProperty("Title")?.GetValue(t) as string;
                            string? performer = tType.GetProperty("Performer")?.GetValue(t) as string;
                            string? songwriter = tType.GetProperty("Songwriter")?.GetValue(t) as string;
                            string? composer = tType.GetProperty("Composer")?.GetValue(t) as string;
                            string? isrc = tType.GetProperty("Isrc")?.GetValue(t) as string;

                            var merged = MergeCdText(trkNum, title, performer, songwriter, composer, isrc, cdText);
                            tracks.Add(new AudioDiscoveredTrack(
                                trkNum,
                                startLba,
                                count,
                                preGap,
                                merged.Title,
                                merged.Performer,
                                merged.Songwriter,
                                merged.Composer,
                                merged.Isrc));
                        }
                    }
                }
            }
        }

        // Fallback: If no session metadata was found but medium has recorded sectors
        if (tracks.Count == 0)
        {
            var mediaStatus = recorder.QueryMediaStatus();
            if (mediaStatus.UsedSectors > 0)
            {
                tracks.Add(new AudioDiscoveredTrack(
                    1,
                    0,
                    mediaStatus.UsedSectors,
                    OpticalConstants.CdLeadInFrames,
                    "Track 01",
                    cdText?.AlbumPerformer ?? "Unknown Artist"));
            }
        }

        return (tracks, cdText);
    }

    private static CdTextAlbumMetadata? TryParseCdText(byte[]? packs)
    {
        if (packs == null || packs.Length < CdTextPack.PackSizeBytes)
        {
            return null;
        }

        try
        {
            return CdTextPackParser.ParsePacks(packs);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException)
        {
            // Malformed or CRC-damaged CD-Text: fall back to generic track names.
            return null;
        }
    }

    /// <summary>
    /// Overlays decoded CD-Text on top of the given per-track fields and applies the generic fallbacks
    /// ("Track NN", album performer, "Unknown Artist").
    /// </summary>
    private static (string Title, string Performer, string? Songwriter, string? Composer, string? Isrc) MergeCdText(
        int trackNumber,
        string? title,
        string? performer,
        string? songwriter,
        string? composer,
        string? isrc,
        CdTextAlbumMetadata? cdText)
    {
        var cdt = cdText?.Tracks?.FirstOrDefault(x => x.TrackNumber == trackNumber);
        if (cdt != null)
        {
            if (!string.IsNullOrEmpty(cdt.Title)) title = cdt.Title;
            if (!string.IsNullOrEmpty(cdt.Performer)) performer = cdt.Performer;
            if (!string.IsNullOrEmpty(cdt.Songwriter)) songwriter = cdt.Songwriter;
            if (!string.IsNullOrEmpty(cdt.Composer)) composer = cdt.Composer;
            if (!string.IsNullOrEmpty(cdt.Isrc)) isrc = cdt.Isrc;
        }

        if (string.IsNullOrWhiteSpace(performer) && !string.IsNullOrWhiteSpace(cdText?.AlbumPerformer))
        {
            performer = cdText.AlbumPerformer;
        }

        return (
            string.IsNullOrWhiteSpace(title) ? $"Track {trackNumber:D2}" : title,
            string.IsNullOrWhiteSpace(performer) ? "Unknown Artist" : performer,
            songwriter,
            composer,
            isrc);
    }

    /// <summary>
    /// Rips audio tracks from an inserted optical disc into individual .wav files wrapped with standard 44-byte headers
    /// and optionally generates a companion .cue sheet.
    /// </summary>
    public async Task<AudioCdRipResult> RipAudioCdAsync(
        IDiscRecorderBackend recorder,
        IBurnEngineBackend engine,
        string outputDirectory,
        AudioRipOptions? options = null,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        options ??= new AudioRipOptions();

        cancellationToken.ThrowIfCancellationRequested();

        var mediaStatus = recorder.QueryMediaStatus();
        if (!mediaStatus.MediaPresent)
        {
            return AudioCdRipResult.Failed("No optical media present in recorder.");
        }

        Directory.CreateDirectory(outputDirectory);

        var (allTracks, cdText) = QueryTracksAndCdText(recorder);
        if (options.MetadataOverride is { } metadataOverride)
        {
            allTracks = allTracks.Select(t =>
            {
                // The generic placeholder must not block the override's album performer.
                string? performer = t.Performer == "Unknown Artist" ? null : t.Performer;
                var m = MergeCdText(t.TrackNumber, t.Title, performer, t.Songwriter, t.Composer, t.Isrc, metadataOverride);
                return t with { Title = m.Title, Performer = m.Performer, Songwriter = m.Songwriter, Composer = m.Composer, Isrc = m.Isrc };
            }).ToList();
            cdText = metadataOverride;
        }
        if (allTracks.Count == 0)
        {
            return AudioCdRipResult.Failed("No recorded audio tracks found on media.");
        }

        var tracksToRip = options.TrackNumbersToRip != null && options.TrackNumbersToRip.Count > 0
            ? allTracks.Where(t => options.TrackNumbersToRip.Contains(t.TrackNumber)).ToList()
            : allTracks.ToList();

        if (tracksToRip.Count == 0)
        {
            return AudioCdRipResult.Failed("None of the specified track numbers were found on the disc.");
        }

        var sw = Stopwatch.StartNew();
        var rippedFilePaths = new List<string>(tracksToRip.Count);
        int totalSectorsRead = 0;

        foreach (var track in tracksToRip)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string safePerformer = SanitizeFileName(track.Performer);
            if (string.IsNullOrEmpty(safePerformer)) safePerformer = "Unknown Artist";

            string safeTitle = SanitizeFileName(track.Title);
            if (string.IsNullOrEmpty(safeTitle)) safeTitle = $"Track {track.TrackNumber:D2}";

            string fileName = $"{track.TrackNumber:D2} - {safePerformer} - {safeTitle}.wav";
            string targetWavPath = Path.Combine(outputDirectory, fileName);

            int pcmByteLength = track.SectorCount * OpticalConstants.RawSectorSize;
            byte[] wavHeader = WavFileCodec.CreateWavHeader(
                pcmByteLength,
                OpticalConstants.CdAudioSampleRate,
                OpticalConstants.CdAudioChannels,
                OpticalConstants.CdAudioBitsPerSample);

            RipOperationResult ripResult;
            await using (var fileStream = new FileStream(targetWavPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                // Write standard 44-byte RIFF WAVE header
                await fileStream.WriteAsync(wavHeader, cancellationToken).ConfigureAwait(false);

                // Read raw 2352-byte audio sectors from disc directly into the file stream
                ripResult = await engine.ReadSectorsAsync(
                    recorder,
                    track.StartLba,
                    track.SectorCount,
                    raw2352Mode: true,
                    sectorConsumer: async (lba, memory) =>
                    {
                        await fileStream.WriteAsync(memory, cancellationToken).ConfigureAwait(false);
                    },
                    progress: progress,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            if (!ripResult.Success && !options.ContinueOnUnreadableSector)
            {
                try
                {
                    if (File.Exists(targetWavPath))
                    {
                        File.Delete(targetWavPath);
                    }
                }
                catch
                {
                    // Best-effort cleanup of incomplete WAV file
                }

                return AudioCdRipResult.Failed(
                    $"Failed to rip track {track.TrackNumber} ('{track.Title}'): {ripResult.ErrorMessage}",
                    ripResult.Exception);
            }

            rippedFilePaths.Add(targetWavPath);
            totalSectorsRead += track.SectorCount;
        }

        // Generate companion CUE sheet
        string? cueSheetPath = null;
        if (options.GenerateCueSheet && rippedFilePaths.Count > 0)
        {
            string cueBaseName = SanitizeFileName(Path.GetFileNameWithoutExtension(options.CueSheetFileName ?? "album"));
            if (string.IsNullOrEmpty(cueBaseName)) cueBaseName = "album";
            string safeCueName = cueBaseName + ".cue";
            cueSheetPath = Path.Combine(outputDirectory, safeCueName);
            var cueFiles = new List<CueFileEntry>();

            for (int i = 0; i < tracksToRip.Count; i++)
            {
                var trk = tracksToRip[i];
                string wavFileName = Path.GetFileName(rippedFilePaths[i]);

                var cueTrack = new CueTrack(
                    TrackNumber: trk.TrackNumber,
                    DataType: CueTrackDataType.Audio,
                    Title: trk.Title,
                    Performer: trk.Performer,
                    Songwriter: trk.Songwriter,
                    Isrc: trk.Isrc,
                    PreGap: trk.PreGapFrames > 0 ? MsfAddress.FromFrameCount(trk.PreGapFrames) : null,
                    Indices: new[] { new CueIndex(1, new MsfAddress(0, 0, 0)) }
                );

                cueFiles.Add(new CueFileEntry(wavFileName, "WAVE", new[] { cueTrack }));
            }

            var cueSheet = new CueSheet(
                Title: cdText?.AlbumTitle,
                Performer: cdText?.AlbumPerformer,
                Songwriter: cdText?.Songwriter,
                Files: cueFiles
            );

            await CueSheetWriter.WriteToFileAsync(cueSheet, cueSheetPath, cancellationToken).ConfigureAwait(false);
        }

        sw.Stop();
        return AudioCdRipResult.Succeeded(rippedFilePaths, totalSectorsRead, sw.Elapsed, cueSheetPath, cdText);
    }
}
