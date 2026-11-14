using CdBurner.Core.Abstractions;
using CdBurner.Core.Archival;
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
    private readonly IBurnEngineBackend? _recoveryDataEngine;

    /// <summary>
    /// Gets the burning engine backend.
    /// </summary>
    public IBurnEngineBackend BurnEngine => _burnEngine;

    /// <summary>
    /// Gets the audio decoder.
    /// </summary>
    public IAudioDecoder AudioDecoder => _audioDecoder;

    /// <summary>
    /// Manifest written to session 2 on the most recent Enhanced CD recovery burn, or null if none ran.
    /// </summary>
    public AudioRecoveryManifest? LastRecoveryManifest { get; private set; }

    /// <summary>
    /// Initializes a new instance of <see cref="AudioCdBurnService"/>.
    /// </summary>
    /// <param name="burnEngine">The optical burning engine backend.</param>
    /// <param name="audioDecoder">Optional audio decoder instance. If null, a built-in RIFF WAVE decoder is used.</param>
    /// <param name="recoveryDataEngine">Optional data burn engine used to append session 2 when <see cref="AudioBurnOptions.IncludeRecoverySession"/> is enabled.</param>
    public AudioCdBurnService(
        IBurnEngineBackend burnEngine,
        IAudioDecoder? audioDecoder = null,
        IBurnEngineBackend? recoveryDataEngine = null)
    {
        _burnEngine = burnEngine ?? throw new ArgumentNullException(nameof(burnEngine));
        _audioDecoder = audioDecoder ?? new CoreWavAudioDecoder();
        _recoveryDataEngine = recoveryDataEngine;
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
    public Task<BurnOperationResult> BurnAsync(
        IDiscRecorderBackend recorder,
        AudioCdCompilation compilation,
        AudioBurnOptions? options = null,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(recorder, compilation, options, progress, checkOnly: false, cancellationToken);

    /// <summary>
    /// Checks the recorded disc in <paramref name="recorder"/> against <paramref name="compilation"/> without writing:
    /// the tracks are prepared exactly as for a burn, then the engine's read-back check compares the disc with them.
    /// Needs an engine that can check discs (<see cref="IAudioDiscVerifier"/>, the direct drive-command writer).
    /// </summary>
    public Task<BurnOperationResult> VerifyAsync(
        IDiscRecorderBackend recorder,
        AudioCdCompilation compilation,
        AudioBurnOptions? options = null,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken cancellationToken = default) =>
        RunAsync(recorder, compilation, options, progress, checkOnly: true, cancellationToken);

    private async Task<BurnOperationResult> RunAsync(
        IDiscRecorderBackend recorder,
        AudioCdCompilation compilation,
        AudioBurnOptions? options,
        IProgress<BurnProgressSnapshot>? progress,
        bool checkOnly,
        CancellationToken cancellationToken)
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

        // 1a. A mixed-mode CD (data track 1, then audio) needs the direct drive-command writer.
        if (compilation.HasDataTrack)
        {
            if (!compilation.Tracks[0].IsDataTrack || compilation.Tracks.Skip(1).Any(t => t.IsDataTrack))
            {
                return BurnOperationResult.Failed(
                    "Only track 1 of a mixed-mode CD can be a data track. Move the data track to the top, or remove it. Nothing was written to the disc.");
            }
            if (compilation.TrackCount < 2)
            {
                return BurnOperationResult.Failed("A mixed-mode CD needs audio after its data track. Nothing was written to the disc.");
            }
            if (!_burnEngine.WritesMixedModeCds && !checkOnly)
            {
                return BurnOperationResult.Failed(
                    "Track 1 is a data track (a mixed-mode CD). Only direct drive commands can write one: turn them on, or remove the data track. "
                    + "Nothing was written to the disc.");
            }
        }

        IAudioDiscVerifier? verifier = null;
        if (checkOnly && (verifier = _burnEngine as IAudioDiscVerifier) == null)
        {
            return BurnOperationResult.Failed("Checking a disc against its source needs direct drive commands.");
        }

        // 1b. Damaged FLAC sources would be burned in as silence or missing audio: stop before the drive is touched.
        if (!checkOnly && !options.AllowDamagedAudio && !options.Simulate)
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

        if (checkOnly)
        {
            // A check reads a recorded disc; none of the writing checks apply.
            if (mediaStatus.IsBlank)
            {
                return BurnOperationResult.Failed("The disc is blank: there is nothing to check.");
            }
        }
        else
        {
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
        }

        LastRecoveryManifest = null;
        string? par2Path = null;
        if (options.IncludeRecoverySession && !checkOnly)
        {
            if (_recoveryDataEngine == null)
            {
                return BurnOperationResult.Failed("Audio CD recovery sessions require direct drive commands (--native).");
            }
            if (compilation.Tracks.Any(t => t.IsDataTrack))
            {
                return BurnOperationResult.Failed("Audio CD recovery sessions cannot be combined with a track-based data track in the audio compilation.");
            }
            par2Path = Par2Runner.FindPar2Executable();
            if (string.IsNullOrEmpty(par2Path))
            {
                return BurnOperationResult.Failed("Audio CD recovery sessions require par2.exe, which was not found on PATH or next to the application.");
            }
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
            var albumMeasurements = new ReplayGainResult?[compilation.Tracks.Count];

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

                if (trackItem.IsDataTrack)
                {
                    preparedTracks[i] = Array.Empty<byte>(); // read from the image while writing; nothing to decode
                    continue;
                }

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

                    if (compilation.LoudnessNormalization == LoudnessNormalizationMode.PerTrack)
                    {
                        var measured = ReplayGainCalculator.CalculateTrackReplayGain(pcmBytes);
                        if (!double.IsNegativeInfinity(measured.IntegratedLoudnessLufs))
                        {
                            pcmBytes = RedBookPcmDspProcessor.ApplyLoudnessGain(pcmBytes, measured.GainDb);
                        }
                    }
                    else if (compilation.LoudnessNormalization == LoudnessNormalizationMode.PerAlbum)
                    {
                        albumMeasurements[i] = ReplayGainCalculator.CalculateTrackReplayGain(pcmBytes);
                    }
                    else if (compilation.UseRmsLoudnessLeveling)
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

            // Album mode: one gain from all tracks' gated blocks together (data tracks have no measurement).
            if (compilation.LoudnessNormalization == LoudnessNormalizationMode.PerAlbum)
            {
                var measured = albumMeasurements.Where(m => m != null).Select(m => m!).ToList();
                double albumGainDb = ReplayGainCalculator.CalculateAlbumReplayGain(measured).AlbumGainDb;
                if (measured.Any(m => !double.IsNegativeInfinity(m.IntegratedLoudnessLufs)))
                {
                    for (int i = 0; i < preparedTracks.Length; i++)
                    {
                        if (albumMeasurements[i] is { } own && !double.IsNegativeInfinity(own.IntegratedLoudnessLufs))
                        {
                            preparedTracks[i] = RedBookPcmDspProcessor.ApplyLoudnessGain(preparedTracks[i], albumGainDb);
                        }
                    }
                }
            }

            // Crossfade adjacent tracks by overlapping them (the next track's head is mixed into this track's tail
            // and removed from the next track), so the transition plays exactly once. Only meaningful when tracks
            // butt against each other: gapless DAO with no pre-gap on the incoming track.
            if (compilation.CrossfadeSeconds > 0 && compilation.RecordingMode == AudioRecordingMode.DiscAtOnceGapless)
            {
                for (int k = 0; k + 1 < preparedTracks.Length; k++)
                {
                    if (compilation.Tracks[k + 1].PreGapFrames > 0 || compilation.Tracks[k].IsDataTrack) continue;
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
                    if (compilation.Tracks[i - 1].IsDataTrack) continue;
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
                if (trackItem.DataTrack is { } data)
                {
                    // The data, then its two-second post-gap, which the writer adds.
                    totalRequiredFrames += data.SectorCount + AudioCdCompilation.MixedModeAudioPreGapFrames;
                    audioTrackSources.Add(new AudioTrackSource
                    {
                        TrackNumber = 1,
                        FilePath = trackItem.SourceFilePath,
                        Title = trackItem.Title,
                        Performer = !string.IsNullOrEmpty(trackItem.Performer) ? trackItem.Performer : compilation.AlbumPerformer,
                        PreGapFrames = trackItem.PreGapFrames,
                        TotalPcmBytes = data.UserDataBytes,
                        Duration = TimeSpan.FromSeconds((double)data.SectorCount / OpticalConstants.AudioFramesPerSecond),
                        PcmStreamFactory = data.OpenUserData,
                        IsDataTrack = true
                    });
                    continue;
                }

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
            if (!checkOnly && mediaStatus.FreeSectors > 0 && totalRequiredFrames > mediaStatus.FreeSectors)
            {
                return BurnOperationResult.Failed(
                    $"Compilation requires {totalRequiredFrames} frames, exceeding media free capacity of {mediaStatus.FreeSectors} frames.");
            }

            long discCapacitySectors = mediaStatus.TotalSectors > 0
                ? mediaStatus.TotalSectors
                : RecoverySessionBudget.MeasuredCapacitySectors;
            if (options.IncludeRecoverySession && !checkOnly)
            {
                long audioOnlySectors = audioTrackSources.Sum(t => (t.TotalPcmBytes + OpticalConstants.RawSectorSize - 1) / OpticalConstants.RawSectorSize);
                var recoveryEstimate = AudioRecoverySessionService.EstimateCapacity(audioOnlySectors, discCapacitySectors);
                if (!recoveryEstimate.Fits)
                {
                    return BurnOperationResult.Failed($"Cannot fit recovery session: {recoveryEstimate.ToleranceSummary}");
                }
            }

            // 5. Generate CD-Text payload if requested
            bool includeCdText = compilation.IncludeCdText || options.WriteCdText;
            byte[]? cdTextPayload = includeCdText ? compilation.BuildCdTextPacks() : null;

            bool leaveOpenForRecovery = options.IncludeRecoverySession && !checkOnly;
            var effectiveOptions = new AudioBurnOptions
            {
                Speed = options.Speed,
                Simulate = options.Simulate,
                EjectWhenDone = options.EjectWhenDone && !leaveOpenForRecovery,
                PerformNormalization = false, // Already performed
                WriteCdText = includeCdText,
                UseDiscAtOnce = compilation.RecordingMode == AudioRecordingMode.DiscAtOnceGapless,
                PreGapFrames = options.PreGapFrames > 0 ? options.PreGapFrames : compilation.DefaultPreGapFrames,
                CdTextBytes = cdTextPayload,
                MediaCatalogNumber = options.MediaCatalogNumber ?? compilation.UpcEan,
                KeepIndexMarks = options.KeepIndexMarks,
                WriteOffsetSamples = options.WriteOffsetSamples,
                LeaveSessionOpen = options.LeaveSessionOpen || leaveOpenForRecovery,
                IncludeRecoverySession = options.IncludeRecoverySession
            };

            // 6. Execute burn through backend (or, for a check, read the disc back against the prepared tracks)
            if (verifier != null)
            {
                return await verifier.VerifyAudioTracksAsync(recorder, audioTrackSources, effectiveOptions, progress, cancellationToken)
                    .ConfigureAwait(false);
            }

            var session1Result = await _burnEngine.BurnAudioTracksAsync(
                recorder,
                audioTrackSources,
                effectiveOptions,
                progress,
                cancellationToken).ConfigureAwait(false);

            if (!session1Result.Success || !leaveOpenForRecovery || options.Simulate)
            {
                return session1Result;
            }

            // 7. Build and write session 2 (PAR2 over PCM) at the next writable LBA and close the disc.
            var postAudioStatus = recorder.QueryMediaStatus();
            if (!postAudioStatus.IsAppendable || postAudioStatus.NextWritableLba <= 0)
            {
                return BurnOperationResult.Failed(
                    "Session 1 audio finished, but the drive did not leave the disc open for session 2.",
                    session1Result.Exception);
            }

            progress?.Report(new BurnProgressSnapshot
            {
                CurrentActionDescription = "Building PAR2 recovery session...",
                CurrentLba = 0,
                TotalLba = 100,
                BufferLevelPercent = 100.0
            });

            var textProgress = progress == null
                ? null
                : new Progress<string>(msg => progress.Report(new BurnProgressSnapshot
                {
                    CurrentActionDescription = msg,
                    CurrentLba = 0,
                    TotalLba = 100,
                    BufferLevelPercent = 100.0
                }));

            using var stagedIso = await AudioRecoverySessionService.BuildRecoverySessionIsoAsync(
                audioTrackSources,
                compilation,
                postAudioStatus.NextWritableLba,
                discCapacitySectors,
                par2Path!,
                textProgress,
                cancellationToken).ConfigureAwait(false);

            LastRecoveryManifest = stagedIso.Manifest;
            var session2Options = new DataBurnOptions
            {
                Speed = options.Speed,
                Simulate = false,
                CloseDisc = true,
                EjectWhenDone = options.EjectWhenDone,
                VerifyAfterBurn = false,
                VolumeLabel = AudioRecoveryManifest.VolumeLabel
            };

            var session2Result = await _recoveryDataEngine!.BurnDataStreamAsync(
                recorder,
                stagedIso.IsoStream,
                session2Options,
                progress,
                cancellationToken).ConfigureAwait(false);

            if (!session2Result.Success)
            {
                return BurnOperationResult.Failed(
                    $"Session 1 audio completed, but session 2 recovery write failed: {session2Result.ErrorMessage}",
                    session2Result.Exception);
            }

            return BurnOperationResult.Succeeded(
                session1Result.SectorsWritten + session2Result.SectorsWritten,
                session1Result.TotalTime + session2Result.TotalTime,
                session1Result.AverageSpeedMultiplier > 0 ? session1Result.AverageSpeedMultiplier : session2Result.AverageSpeedMultiplier);
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
