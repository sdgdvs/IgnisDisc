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
                }
                finally
                {
                    foreach (var s in trackStreams)
                    {
                        try { s.Dispose(); } catch { }
                    }
                }

                int frames = pcmBytes.Length / OpticalConstants.RawSectorSize;
                trackItem.DurationFrames = frames;
                int preGap = i > 0 ? Math.Max(0, trackItem.PreGapFrames) : Math.Max(0, trackItem.PreGapFrames - OpticalConstants.CdLeadInFrames);
                totalRequiredFrames += frames + preGap;

                var source = new AudioTrackSource
                {
                    TrackNumber = trackItem.TrackNumber > 0 ? trackItem.TrackNumber : i + 1,
                    FilePath = trackItem.SourceFilePath,
                    Title = trackItem.Title,
                    Performer = !string.IsNullOrEmpty(trackItem.Performer) ? trackItem.Performer : compilation.AlbumPerformer,
                    Songwriter = trackItem.Songwriter ?? compilation.Songwriter,
                    Composer = trackItem.Composer ?? compilation.Composer,
                    Isrc = trackItem.Isrc,
                    PreGapFrames = trackItem.PreGapFrames,
                    TotalPcmBytes = pcmBytes.Length,
                    Duration = TimeSpan.FromSeconds((double)frames / OpticalConstants.AudioFramesPerSecond),
                    PcmStream = new MemoryStream(pcmBytes, writable: false),
                    PcmStreamFactory = () => new MemoryStream(pcmBytes, writable: false)
                };

                audioTrackSources.Add(source);
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
                CdTextBytes = cdTextPayload
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
    /// Built-in fallback audio decoder for standard RIFF WAVE files.
    /// </summary>
    private sealed class CoreWavAudioDecoder : IAudioDecoder
    {
        public AudioFormatMetadata ProbeAudioFile(string filePath) => WavFileCodec.ProbeAudioFile(filePath);
        public Stream OpenRedBookPcmStream(string filePath) => WavFileCodec.OpenRedBookPcmStream(filePath);
    }
}
