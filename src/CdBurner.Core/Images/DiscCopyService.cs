using System.Diagnostics;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.Burning;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Images;

/// <summary>
/// Options for 1-click disc-to-disc copying and multi-copy batch mastering.
/// </summary>
public sealed class DiscCopyOptions
{
    /// <summary>
    /// Number of destination copies to burn sequentially (1..99). Default is 1.
    /// </summary>
    public int CopyCount { get; set; } = 1;

    /// <summary>
    /// When true, reads and writes raw 2,352-byte sectors (.bin + .cue) to preserve Audio CD tracks,
    /// CD-Text, or mixed-mode layouts. When false, auto-detects Audio CDs and uses 2,048-byte ISO mode for data discs.
    /// </summary>
    public bool RawCloneMode { get; set; }

    /// <summary>
    /// When true, performs a bit-for-bit sector verification pass after each burned copy.
    /// </summary>
    public bool VerifyEachCopy { get; set; } = true;

    /// <summary>
    /// When true, retains the temporary staged disc image file on disk after copying completes.
    /// </summary>
    public bool KeepStagedImage { get; set; }

    /// <summary>
    /// Optional explicit file path for the staged disc image (.iso or .cue).
    /// If omitted, a temporary staging path is created automatically.
    /// </summary>
    public string? CustomStagingImagePath { get; set; }

    /// <summary>
    /// Optional write speed for burning target copies.
    /// </summary>
    public WriteSpeedDescriptor? WriteSpeed { get; set; }

    /// <summary>
    /// Optional callback invoked before each target copy (1-based copy index) to allow
    /// single-drive media swapping or inserting the next blank disc in a batch run.
    /// Return false to cancel the copy batch.
    /// </summary>
    public Func<int, CancellationToken, Task<bool>>? OnPrepareTargetDiscAsync { get; set; }
}

/// <summary>
/// Result of a disc-to-disc copy or multi-copy batch operation.
/// </summary>
public sealed record DiscCopyResult
{
    public bool Success { get; init; }
    public int CopiesRequested { get; init; }
    public int CopiesCompleted { get; init; }
    public int SectorsPerDisc { get; init; }
    public int TotalSectorsWritten { get; init; }
    public bool WasAudioCd { get; init; }
    public string? StagedImagePath { get; init; }
    public TimeSpan TotalTime { get; init; }
    public string? ErrorMessage { get; init; }

    public static DiscCopyResult Failed(string errorMessage, int copiesRequested = 1, int copiesCompleted = 0, string? stagedPath = null) =>
        new()
        {
            Success = false,
            CopiesRequested = copiesRequested,
            CopiesCompleted = copiesCompleted,
            StagedImagePath = stagedPath,
            ErrorMessage = errorMessage
        };
}

/// <summary>
/// Orchestrates 1-click Disc-to-Disc copying (drive-to-drive or single-drive swap)
/// and multi-copy batch burning for Data CDs/DVDs and Red Book Audio CDs.
/// </summary>
public sealed class DiscCopyService
{
    private readonly IBurnEngineBackend _burnEngine;
    private readonly DiscRipperService _ripperService;
    private readonly DataBurnService _dataBurnService;
    private readonly AudioCdBurnService _audioBurnService;
    private readonly AudioCdRipperService _audioRipperService;
    private readonly DiscVerificationService _verificationService;

    public DiscCopyService(
        IBurnEngineBackend burnEngine,
        IAudioDecoder? audioDecoder = null)
    {
        _burnEngine = burnEngine ?? throw new ArgumentNullException(nameof(burnEngine));
        _ripperService = new DiscRipperService();
        _dataBurnService = new DataBurnService(_burnEngine);
        _audioBurnService = new AudioCdBurnService(_burnEngine, audioDecoder);
        _audioRipperService = new AudioCdRipperService();
        _verificationService = new DiscVerificationService();
    }

    /// <summary>
    /// Copies a source optical disc from <paramref name="sourceRecorder"/> to <paramref name="targetRecorder"/>
    /// for <see cref="DiscCopyOptions.CopyCount"/> copies.
    /// </summary>
    public async Task<DiscCopyResult> CopyDiscAsync(
        IDiscRecorderBackend sourceRecorder,
        IDiscRecorderBackend targetRecorder,
        DiscCopyOptions? options = null,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sourceRecorder);
        ArgumentNullException.ThrowIfNull(targetRecorder);

        options ??= new DiscCopyOptions();
        int copyCount = Math.Clamp(options.CopyCount, 1, 99);
        var sw = Stopwatch.StartNew();

        var sourceStatus = sourceRecorder.QueryMediaStatus();
        if (!sourceStatus.MediaPresent || sourceStatus.IsBlank || sourceStatus.UsedSectors <= 0)
        {
            return DiscCopyResult.Failed("Source drive does not contain a recorded disc to copy.", copyCount);
        }

        // Without a media-swap callback there is no way to get a blank disc in front of the laser: a single drive still
        // holds the source, and a second copy would land on the first. Refuse up front instead of failing mid-batch
        // (or, on rewritable media, silently overwriting the previous copy).
        if (options.OnPrepareTargetDiscAsync == null)
        {
            if (IsSameDrive(sourceRecorder, targetRecorder))
            {
                return DiscCopyResult.Failed(
                    "Single-drive copy needs a disc-swap prompt (OnPrepareTargetDiscAsync) so the source can be ejected and a blank disc inserted.",
                    copyCount);
            }

            if (copyCount > 1)
            {
                return DiscCopyResult.Failed(
                    "Multi-copy batches need a disc-swap prompt (OnPrepareTargetDiscAsync) to insert a new blank disc before each copy.",
                    copyCount);
            }
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "IgnisDisc_Copy_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        string? stagedPath = null;
        try
        {
            // Detect whether source disc is an Audio CD via reflection/session inspection or AudioCdRipperService
            bool isAudioDisc = IsSourceAudioCd(sourceRecorder);

            if (isAudioDisc)
            {
                string audioRipDir = !string.IsNullOrWhiteSpace(options.CustomStagingImagePath)
                    ? (Path.GetDirectoryName(options.CustomStagingImagePath) ?? tempDir)
                    : Path.Combine(tempDir, "audio_stage");
                Directory.CreateDirectory(audioRipDir);

                var audioRip = await _audioRipperService.RipAudioCdAsync(
                    sourceRecorder,
                    _burnEngine,
                    audioRipDir,
                    new AudioRipOptions { GenerateCueSheet = true },
                    progress,
                    ct).ConfigureAwait(false);

                if (!audioRip.Success || string.IsNullOrWhiteSpace(audioRip.CueSheetPath) || !File.Exists(audioRip.CueSheetPath))
                {
                    return DiscCopyResult.Failed(audioRip.ErrorMessage ?? "Failed to extract source Audio CD.", copyCount);
                }

                stagedPath = audioRip.CueSheetPath;
                var compilation = new AudioCdCompilation
                {
                    RecordingMode = AudioRecordingMode.DiscAtOnceGapless,
                    IncludeCdText = true
                };
                compilation.ImportCueSheet(stagedPath, _audioBurnService.AudioDecoder);

                int completed = 0;
                int totalSectors = 0;
                int sectorsPerDisc = audioRip.TotalSectorsRead;

                for (int copyIndex = 1; copyIndex <= copyCount; copyIndex++)
                {
                    ct.ThrowIfCancellationRequested();

                    if (options.OnPrepareTargetDiscAsync != null)
                    {
                        bool proceed = await options.OnPrepareTargetDiscAsync(copyIndex, ct).ConfigureAwait(false);
                        if (!proceed)
                        {
                            return DiscCopyResult.Failed("Disc copy cancelled while waiting for target blank media.", copyCount, completed, stagedPath);
                        }
                    }

                    var burnRes = await _audioBurnService.BurnAsync(
                        targetRecorder,
                        compilation,
                        new AudioBurnOptions
                        {
                            UseDiscAtOnce = true,
                            WriteCdText = true,
                            Speed = options.WriteSpeed
                        },
                        progress,
                        ct).ConfigureAwait(false);

                    if (!burnRes.Success)
                    {
                        return DiscCopyResult.Failed(
                            $"Copy {copyIndex} of {copyCount} failed: {burnRes.ErrorMessage}",
                            copyCount,
                            completed,
                            stagedPath);
                    }

                    completed++;
                    totalSectors += burnRes.SectorsWritten;
                    sectorsPerDisc = burnRes.SectorsWritten;
                }

                sw.Stop();
                return new DiscCopyResult
                {
                    Success = true,
                    CopiesRequested = copyCount,
                    CopiesCompleted = completed,
                    SectorsPerDisc = sectorsPerDisc,
                    TotalSectorsWritten = totalSectors,
                    WasAudioCd = true,
                    StagedImagePath = options.KeepStagedImage ? stagedPath : null,
                    TotalTime = sw.Elapsed
                };
            }
            else
            {
                // Data / Video CD or DVD copy
                bool rawMode = options.RawCloneMode;
                stagedPath = !string.IsNullOrWhiteSpace(options.CustomStagingImagePath)
                    ? options.CustomStagingImagePath
                    : Path.Combine(tempDir, rawMode ? "staged_disc.bin" : "staged_disc.iso");

                RipOperationResult ripResult;
                if (rawMode)
                {
                    string cuePath = Path.ChangeExtension(stagedPath, ".cue");
                    ripResult = await _ripperService.RipToBinCueAsync(
                        sourceRecorder,
                        _burnEngine,
                        stagedPath,
                        cuePath,
                        progress: progress,
                        ct: ct).ConfigureAwait(false);
                }
                else
                {
                    ripResult = await _ripperService.RipToIsoAsync(
                        sourceRecorder,
                        _burnEngine,
                        stagedPath,
                        progress: progress,
                        ct: ct).ConfigureAwait(false);
                }

                if (!ripResult.Success)
                {
                    return DiscCopyResult.Failed(ripResult.ErrorMessage ?? "Failed to read source disc.", copyCount);
                }

                int completed = 0;
                int totalSectors = 0;
                int sectorsPerDisc = ripResult.SectorsRead;

                for (int copyIndex = 1; copyIndex <= copyCount; copyIndex++)
                {
                    ct.ThrowIfCancellationRequested();

                    if (options.OnPrepareTargetDiscAsync != null)
                    {
                        bool proceed = await options.OnPrepareTargetDiscAsync(copyIndex, ct).ConfigureAwait(false);
                        if (!proceed)
                        {
                            return DiscCopyResult.Failed("Disc copy cancelled while waiting for target blank media.", copyCount, completed, stagedPath);
                        }
                    }

                    BurnOperationResult burnRes;
                    if (rawMode)
                    {
                        await using var fs = new FileStream(stagedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
                        burnRes = await _burnEngine.BurnDataStreamAsync(
                            targetRecorder,
                            fs,
                            new DataBurnOptions
                            {
                                CloseDisc = true,
                                Speed = options.WriteSpeed,
                                OverwriteExistingData = true
                            },
                            progress,
                            ct).ConfigureAwait(false);
                    }
                    else
                    {
                        var dataRes = await _dataBurnService.BurnIsoFileAsync(
                            targetRecorder,
                            stagedPath,
                            new DataBurnOptions
                            {
                                VolumeLabel = "DISC_COPY",
                                CloseDisc = true,
                                Speed = options.WriteSpeed,
                                OverwriteExistingData = true
                            },
                            progress,
                            ct).ConfigureAwait(false);

                        burnRes = dataRes.Success
                            ? BurnOperationResult.Succeeded(dataRes.SectorsWritten, dataRes.TotalTime, dataRes.AverageSpeedMultiplier)
                            : BurnOperationResult.Failed(dataRes.ErrorMessage ?? "Burn failed.");
                    }

                    if (!burnRes.Success)
                    {
                        return DiscCopyResult.Failed(
                            $"Copy {copyIndex} of {copyCount} failed: {burnRes.ErrorMessage}",
                            copyCount,
                            completed,
                            stagedPath);
                    }

                    if (options.VerifyEachCopy && !rawMode)
                    {
                        var verifyRes = await _verificationService.VerifyDiscAgainstIsoFileAsync(
                            targetRecorder,
                            _burnEngine,
                            stagedPath,
                            progress: progress,
                            ct: ct).ConfigureAwait(false);

                        if (!verifyRes.IsMatch)
                        {
                            return DiscCopyResult.Failed(
                                $"Verification failed on copy {copyIndex} of {copyCount}: {verifyRes.ErrorMessage}",
                                copyCount,
                                completed,
                                stagedPath);
                        }
                    }

                    completed++;
                    totalSectors += burnRes.SectorsWritten;
                }

                sw.Stop();
                return new DiscCopyResult
                {
                    Success = true,
                    CopiesRequested = copyCount,
                    CopiesCompleted = completed,
                    SectorsPerDisc = sectorsPerDisc,
                    TotalSectorsWritten = totalSectors,
                    WasAudioCd = false,
                    StagedImagePath = options.KeepStagedImage ? stagedPath : null,
                    TotalTime = sw.Elapsed
                };
            }
        }
        finally
        {
            if (!options.KeepStagedImage)
            {
                try
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, recursive: true);
                    }
                }
                catch
                {
                    // Ignore temp cleanup errors
                }
            }
        }
    }

    /// <summary>
    /// True when two recorder handles refer to the same physical (or virtual) drive.
    /// </summary>
    public static bool IsSameDrive(IDiscRecorderBackend a, IDiscRecorderBackend b) =>
        ReferenceEquals(a, b) ||
        (!string.IsNullOrWhiteSpace(a.UniqueId) && string.Equals(a.UniqueId, b.UniqueId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Detects a Red Book Audio CD in <paramref name="recorder"/>. Physical drives (<see cref="IDiscTocProvider"/>, e.g.
    /// the IMAPI recorder) are classified from the real Table of Contents; virtual drives from their mounted medium.
    /// </summary>
    public static bool IsSourceAudioCd(IDiscRecorderBackend recorder)
    {
        ArgumentNullException.ThrowIfNull(recorder);

        // Physical drives: the TOC's per-track control bits are authoritative.
        if (recorder is IDiscTocProvider tocProvider)
        {
            try
            {
                var toc = tocProvider.ReadTableOfContents();
                if (toc.Count > 0)
                {
                    return toc.Any(t => t.IsAudio);
                }
            }
            catch
            {
                // Fall through to the medium inspection below.
            }
        }

        try
        {
            // Check if VirtualDiscRecorder has CurrentMedium with audio tracks
            var prop = recorder.GetType().GetProperty("CurrentMedium");
            if (prop?.GetValue(recorder) is object medium)
            {
                var sessionsProp = medium.GetType().GetProperty("Sessions");
                if (sessionsProp?.GetValue(medium) is System.Collections.IEnumerable sessions)
                {
                    foreach (object session in sessions)
                    {
                        var tracksProp = session.GetType().GetProperty("Tracks");
                        if (tracksProp?.GetValue(session) is System.Collections.IEnumerable tracks)
                        {
                            foreach (object track in tracks)
                            {
                                var isAudioProp = track.GetType().GetProperty("IsAudio");
                                if (isAudioProp?.GetValue(track) is bool isAudio && isAudio)
                                {
                                    return true;
                                }
                            }
                        }
                    }
                }
            }
        }
        catch
        {
        }

        return false;
    }
}
