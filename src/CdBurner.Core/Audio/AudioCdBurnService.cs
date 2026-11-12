using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Audio;

/// <summary>
/// Service orchestrating Red Book Audio CD burning (Track-at-Once and gapless Disc-at-Once with Lead-In CD-Text),
/// decoding source audio, applying DSP normalization and 2352-byte sector padding, and executing the burn backend.
/// </summary>
public class AudioCdBurnService
{
    private readonly IBurnEngineBackend _burnEngine;
    private readonly IAudioDecoder _audioDecoder;

    /// <summary>
    /// Gets the burning engine backend.
    /// </summary>
    public IBurnEngineBackend BurnEngine => _burnEngine;

    /// <summary>
    /// Gets the audio decoder.
    /// </summary>
    public IAudioDecoder AudioDecoder => _audioDecoder;

    /// <summary>
    /// Initializes a new instance of <see cref="AudioCdBurnService"/>.
    /// </summary>
    /// <param name="burnEngine">The optical burning engine backend.</param>
    /// <param name="audioDecoder">Optional audio decoder instance. If null, a built-in RIFF WAVE decoder is used.</param>
    public AudioCdBurnService(IBurnEngineBackend burnEngine, IAudioDecoder? audioDecoder = null)
    {
        _burnEngine = burnEngine ?? throw new ArgumentNullException(nameof(burnEngine));
        _audioDecoder = audioDecoder ?? new CoreWavAudioDecoder();
    }

    /// <summary>
    /// Decodes each FLAC source once, fully, and returns the damaged ones with a plain description. Other formats are
    /// left to their decoders. A file that cannot be opened is left to the burn, which reports it in its own words.
    /// </summary>
    public static IReadOnlyList<(string Path, string Problem)> CheckFlacSources(
        AudioCdCompilation compilation, CancellationToken cancellationToken = default, IProgress<BurnProgressSnapshot>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        var damaged = new List<(string, string)>();
        var flacs = compilation.Tracks.Select(t => t.SourceFilePath)
            .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p) && FlacFileCodec.IsFlacFile(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        for (int i = 0; i < flacs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = flacs[i];
            progress?.Report(new BurnProgressSnapshot
            {
                CurrentLba = i,
                TotalLba = flacs.Count,
                CurrentActionDescription = $"Checking FLAC files for damage ({i + 1} of {flacs.Count})"
            });

            try
            {
                var report = FlacFileCodec.CheckIntegrity(path);
                if (report.IsDamaged)
                {
                    damaged.Add((path, report.Describe()));
                }
            }
            catch (InvalidDataException ex)
            {
                damaged.Add((path, ex.Message));
            }
            catch (IOException)
            {
                // Unreadable right now: the burn opens it again and reports the error itself.
            }
        }
        return damaged;
    }

    /// <summary>
    /// Burns an Audio CD compilation to the specified recorder.
    /// </summary>
    public async Task<BurnOperationResult> BurnAsync(
        IDiscRecorderBackend recorder,
        AudioCdCompilation compilation,
        AudioBurnOptions? options = null,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(compilation);
        options ??= new AudioBurnOptions();

        cancellationToken.ThrowIfCancellationRequested();

        // 1. Validate tracks
        if (compilation.TrackCount == 0)
        {
            return BurnOperationResult.Failed("Compilation contains no audio tracks.");
        }

        if (compilation.TrackCount > AudioCdCompilation.MaxTracks)
        {
            return BurnOperationResult.Failed(
                $"Compilation contains {compilation.TrackCount} tracks, which exceeds the Red Book limit of {AudioCdCompilation.MaxTracks}.");
        }

        // 1b. Damaged FLAC sources would be burned in as silence or missing audio: stop before the drive is touched.
        if (!options.AllowDamagedAudio && !options.Simulate)
        {
            var damaged = CheckFlacSources(compilation, cancellationToken, progress);
            if (damaged.Count > 0)
            {
                string list = string.Join(" ", damaged.Select(d => $"'{Path.GetFileName(d.Path)}': {d.Problem}"));
                return new BurnOperationResult
                {
                    Success = false,
                    Status = BurnStatus.Failed,
                    ErrorMessage = $"{damaged.Count} FLAC file(s) are damaged, so the disc would not hold the full audio. {list} "
                        + "Nothing was written to the disc. Replace the file(s), or burn anyway to accept the damage.",
                    DamagedAudioFiles = damaged.Select(d => d.Path).ToList()
                };
            }
        }

        // 2. Query and validate media
        var mediaStatus = recorder.QueryMediaStatus();
        if (!mediaStatus.MediaPresent)
        {
            return BurnOperationResult.Failed("No optical media present in recorder.");
        }

        if (!mediaStatus.IsBlank && !mediaStatus.IsAppendable && !mediaStatus.IsRewritable)
        {
            return BurnOperationResult.Failed("Mounted media is not writable (disc is finalized or not blank/rewritable).");
        }

        if (mediaStatus.RequiresEraseToWrite)
        {
            return BurnOperationResult.Failed(CdBurner.Core.Burning.DataBurnService.EraseRequiredMessage);
        }

        if (mediaStatus.IsOverwriteOnly || mediaStatus.MediaType.IsDvd())
        {
            return BurnOperationResult.Failed("Audio CDs require blank CD-R or CD-RW media.");
        }

        progress?.Report(new BurnProgressSnapshot
        {
            CurrentActionDescription = "PreparingAudioTracks",
            CurrentLba = 0,
            TotalLba = 100,
            BufferLevelPercent = 100.0
        });

        cancellationToken.ThrowIfCancellationRequested();

        // 3. Process, decode, normalize, and pad tracks
        var audioTrackSources = new List<AudioTrackSource>(compilation.TrackCount);

        try
        {
            int totalRequiredFrames = 0;
            var preparedTracks = new byte[compilation.Tracks.Count][];

            for (int i = 0; i < compilation.Tracks.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var trackItem = compilation.Tracks[i];

                progress?.Report(new BurnProgressSnapshot
                {
                    CurrentActionDescription = $"Preparing track {i + 1}/{compilation.Tracks.Count}: {trackItem.Title}",
                    CurrentLba = i,
                    TotalLba = Math.Max(1, compilation.Tracks.Count),
                    BufferLevelPercent = 100.0
                });

                Stream pcmStream;
                try
                {
                    if (trackItem.CustomPcmStreamFactory != null)
                    {
                        pcmStream = trackItem.CustomPcmStreamFactory();
                    }
                    else if (!string.IsNullOrEmpty(trackItem.SourceFilePath) && File.Exists(trackItem.SourceFilePath))
                    {
                        pcmStream = _audioDecoder.OpenRedBookPcmStream(trackItem.SourceFilePath);
                    }
                    else
                    {
                        return BurnOperationResult.Failed(
                            $"Audio source not found for track {trackItem.TrackNumber}: '{trackItem.SourceFilePath}'.");
                    }
                }
                catch (DrmProtectedAudioException drmEx)
                {
                    return BurnOperationResult.Failed(
                        $"Track {trackItem.TrackNumber} ('{trackItem.Title}') cannot be burned: {drmEx.Message}");
                }
                catch (Exception ex)
                {
                    return BurnOperationResult.Failed(
                        $"Failed to decode track {trackItem.TrackNumber} ('{trackItem.Title}'): {ex.Message}");
                }

                var trackStreams = new List<Stream>(3) { pcmStream };
                byte[] pcmBytes;

                try
                {
                    // Normalization
                    bool shouldNormalize = compilation.NormalizeAllTracks || trackItem.NormalizeAudio || options.PerformNormalization;
                    if (shouldNormalize)
                    {
                        try
                        {
                            var normalized = RedBookPcmDspProcessor.NormalizePcmStream(pcmStream);
                            trackStreams.Add(normalized);
                            pcmStream = normalized;
                        }
                        catch (Exception ex)
                        {
                            return BurnOperationResult.Failed(
                                $"DSP normalization failed for track {trackItem.TrackNumber} ('{trackItem.Title}'): {ex.Message}");
                        }
                    }

                    // Padding to 2352-byte boundary
                    var padded = RedBookPcmDspProcessor.PadToRedBookSectorBoundary(pcmStream);
                    trackStreams.Add(padded);
                    pcmStream = padded;

                    if (pcmStream is MemoryStream ms && ms.TryGetBuffer(out var segment) && segment.Offset == 0 && segment.Count == segment.Array?.Length)
                    {
                        pcmBytes = segment.Array;
                    }
                    else
                    {
                        using var mem = new MemoryStream();
                        await pcmStream.CopyToAsync(mem, cancellationToken).ConfigureAwait(false);
                        pcmBytes = mem.ToArray();
                    }

                    if (compilation.UseRmsLoudnessLeveling)
                    {
                        pcmBytes = RedBookPcmDspProcessor.NormalizeRmsLoudness(pcmBytes);
                    }
                }
                finally
                {
                    foreach (var s in trackStreams)
                    {
                        try { s.Dispose(); } catch { }
                    }
                }

                preparedTracks[i] = pcmBytes;
            }

            // Crossfade adjacent tracks by overlapping them (the next track's head is mixed into this track's tail
            // and removed from the next track), so the transition plays exactly once. Only meaningful when tracks
            // butt against each other: gapless DAO with no pre-gap on the incoming track.
            if (compilation.CrossfadeSeconds > 0 && compilation.RecordingMode == AudioRecordingMode.DiscAtOnceGapless)
            {
                for (int k = 0; k + 1 < preparedTracks.Length; k++)
                {
                    if (compilation.Tracks[k + 1].PreGapFrames > 0) continue;
                    (preparedTracks[k], preparedTracks[k + 1]) = RedBookPcmDspProcessor.ApplyCrossfadeBetweenTracks(
                        preparedTracks[k], preparedTracks[k + 1], compilation.CrossfadeSeconds);
                }
            }

            // INDEX 00 marks for the direct drive-command writer: the tail of the previous track's audio becomes
            // this track's pre-gap audio. The bytes on the disc stay in the same order; only the track mark moves.
            // Not done with a crossfade (track lengths have changed) or when it would leave a track under 4 seconds.
            var gapAudio = new (byte[] Source, int Offset, int Frames)?[preparedTracks.Length];
            var audibleLength = new int[preparedTracks.Length];
            for (int i = 0; i < preparedTracks.Length; i++)
            {
                audibleLength[i] = preparedTracks[i].Length;
            }
            if (options.KeepIndexMarks && compilation.CrossfadeSeconds <= 0)
            {
                const int minimumTrackFrames = 4 * OpticalConstants.AudioFramesPerSecond;
                for (int i = 1; i < preparedTracks.Length; i++)
                {
                    int lead = compilation.Tracks[i].Index00LeadFrames;
                    int previousFrames = preparedTracks[i - 1].Length / OpticalConstants.RawSectorSize;
                    if (lead > 0 && previousFrames - lead >= minimumTrackFrames)
                    {
                        audibleLength[i - 1] = (previousFrames - lead) * OpticalConstants.RawSectorSize;
                        gapAudio[i] = (preparedTracks[i - 1], audibleLength[i - 1], lead);
                    }
                }
            }

            for (int i = 0; i < preparedTracks.Length; i++)
            {
                var trackItem = compilation.Tracks[i];
                byte[] pcmBytes = preparedTracks[i];
                int pcmLength = audibleLength[i];
                int frames = pcmBytes.Length / OpticalConstants.RawSectorSize;
                trackItem.DurationFrames = frames;
                int preGap = i > 0 ? Math.Max(0, trackItem.PreGapFrames) : Math.Max(0, trackItem.PreGapFrames - OpticalConstants.CdLeadInFrames);
                totalRequiredFrames += frames + preGap;

                Func<Stream>? preGapFactory = null;
                int preGapAudioFrames = 0;
                if (gapAudio[i] is { } gap)
                {
                    preGapFactory = () => new MemoryStream(gap.Source, gap.Offset, gap.Frames * OpticalConstants.RawSectorSize, writable: false);
                    preGapAudioFrames = gap.Frames;
                }
                else if (i == 0 && options.KeepIndexMarks && trackItem is { HiddenAudioFrames: > 0, HiddenAudioPcmStreamFactory: not null })
                {
                    preGapFactory = trackItem.HiddenAudioPcmStreamFactory;
                    preGapAudioFrames = trackItem.HiddenAudioFrames;
                    totalRequiredFrames += preGapAudioFrames;
                }

                audioTrackSources.Add(new AudioTrackSource
                {
                    TrackNumber = trackItem.TrackNumber > 0 ? trackItem.TrackNumber : i + 1,
                    FilePath = trackItem.SourceFilePath,
                    Title = trackItem.Title,
                    Performer = !string.IsNullOrEmpty(trackItem.Performer) ? trackItem.Performer : compilation.AlbumPerformer,
                    Songwriter = trackItem.Songwriter ?? compilation.Songwriter,
                    Composer = trackItem.Composer ?? compilation.Composer,
                    Isrc = trackItem.Isrc,
                    PreGapFrames = trackItem.PreGapFrames,
                    TotalPcmBytes = pcmLength,
                    Duration = TimeSpan.FromSeconds((double)pcmLength / OpticalConstants.RawSectorSize / OpticalConstants.AudioFramesPerSecond),
                    PcmStream = new MemoryStream(pcmBytes, 0, pcmLength, writable: false),
                    PcmStreamFactory = () => new MemoryStream(pcmBytes, 0, pcmLength, writable: false),
                    PreGapPcmStreamFactory = preGapFactory,
                    PreGapAudioFrames = preGapAudioFrames,
                    PreEmphasis = trackItem.PreEmphasis,
                    CopyPermitted = trackItem.CopyPermitted
                });
            }

            // 4. Validate capacity against media
            if (mediaStatus.FreeSectors > 0 && totalRequiredFrames > mediaStatus.FreeSectors)
            {
                return BurnOperationResult.Failed(
                    $"Compilation requires {totalRequiredFrames} frames, exceeding media free capacity of {mediaStatus.FreeSectors} frames.");
            }

            // 5. Generate CD-Text payload if requested
            bool includeCdText = compilation.IncludeCdText || options.WriteCdText;
            byte[]? cdTextPayload = includeCdText ? compilation.BuildCdTextPacks() : null;

            var effectiveOptions = new AudioBurnOptions
            {
                Speed = options.Speed,
                Simulate = options.Simulate,
                EjectWhenDone = options.EjectWhenDone,
                PerformNormalization = false, // Already performed
                WriteCdText = includeCdText,
                UseDiscAtOnce = compilation.RecordingMode == AudioRecordingMode.DiscAtOnceGapless,
                PreGapFrames = options.PreGapFrames > 0 ? options.PreGapFrames : compilation.DefaultPreGapFrames,
                CdTextBytes = cdTextPayload,
                MediaCatalogNumber = options.MediaCatalogNumber ?? compilation.UpcEan,
                KeepIndexMarks = options.KeepIndexMarks
            };

            // 6. Execute burn through backend
            return await _burnEngine.BurnAudioTracksAsync(
                recorder,
                audioTrackSources,
                effectiveOptions,
                progress,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var trk in audioTrackSources)
            {
                try { trk.PcmStream?.Dispose(); } catch { }
            }
            compilation.ReleaseCachedAlbumBuffers();
        }
    }

    /// <summary>
    /// Overload for burning an Audio CD compilation.
    /// </summary>
    public Task<BurnOperationResult> BurnCompilationAsync(
        IDiscRecorderBackend recorder,
        AudioCdCompilation compilation,
        AudioBurnOptions? options = null,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default) =>
        BurnAsync(recorder, compilation, options, progress, cancellationToken);

    /// <summary>
    /// Built-in fallback audio decoder for standard RIFF WAVE (.wav) and lossless FLAC (.flac) files.
    /// </summary>
    private sealed class CoreWavAudioDecoder : IAudioDecoder
    {
        public AudioFormatMetadata ProbeAudioFile(string filePath) =>
            FlacFileCodec.IsFlacFile(filePath)
                ? FlacFileCodec.ProbeAudioFile(filePath)
                : WavFileCodec.ProbeAudioFile(filePath);

        public Stream OpenRedBookPcmStream(string filePath) =>
            FlacFileCodec.IsFlacFile(filePath)
                ? FlacFileCodec.OpenRedBookPcmStream(filePath)
                : WavFileCodec.OpenRedBookPcmStream(filePath);
    }
}
