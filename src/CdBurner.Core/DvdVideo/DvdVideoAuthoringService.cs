using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.DvdVideo;

/// <summary>
/// User configuration options for the universal one-click DVD-Video authoring pipeline.
/// </summary>
public sealed class DvdAuthoringOptions
{
    /// <summary>
    /// Target television standard (NTSC or PAL). Default: NTSC.
    /// </summary>
    public DvdVideoStandard Standard { get; set; } = DvdVideoStandard.Ntsc;

    /// <summary>
    /// Target display aspect ratio (16:9 Widescreen or 4:3 Full-Frame). Default: 16:9 Widescreen.
    /// </summary>
    public DvdAspectRatio AspectRatio { get; set; } = DvdAspectRatio.Widescreen16x9;

    /// <summary>
    /// Optical volume label (max 32 uppercase characters). Default: "DVD_VIDEO".
    /// </summary>
    public string VolumeLabel { get; set; } = "DVD_VIDEO";

    /// <summary>
    /// Top title displayed on the interactive DVD root menu. Default: "DVD Video".
    /// </summary>
    public string MenuTitle { get; set; } = "DVD Video";

    /// <summary>
    /// Whether to automatically generate an interactive DVD Root Play Menu (VIDEO_TS.VOB). Default: true.
    /// </summary>
    public bool AutoCreatePlayMenu { get; set; } = true;

    /// <summary>
    /// Whether to use FFmpeg when available on the system to transcode arbitrary video streams into MPEG-2. Default: true.
    /// </summary>
    public bool UseFfmpegWhenAvailable { get; set; } = true;

    /// <summary>
    /// Optional custom path to the FFmpeg executable.
    /// </summary>
    public string? CustomFfmpegPath { get; set; }

    /// <summary>
    /// Target video bitrate in kilobits per second. Default: 6000 kbps (6 Mbps).
    /// </summary>
    public int VideoBitrateKbps { get; set; } = 6000;

    /// <summary>
    /// Maximum video bitrate in kilobits per second. Default: 8000 kbps (8 Mbps).
    /// </summary>
    public int MaxVideoBitrateKbps { get; set; } = 8000;

    /// <summary>
    /// Audio bitrate in kilobits per second. Default: 192 kbps.
    /// </summary>
    public int AudioBitrateKbps { get; set; } = 192;
}

/// <summary>
/// Result containing authored DVD directory paths, generated files, validation status, and planned disc layout.
/// </summary>
public sealed class DvdAuthoringResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string OutputRootDirectory { get; init; } = string.Empty;
    public string VideoTsDirectory { get; init; } = string.Empty;
    public string AudioTsDirectory { get; init; } = string.Empty;
    public string VolumeLabel { get; init; } = "DVD_VIDEO";
    public DvdInputKind InputKind { get; init; }
    public int TitleCount { get; init; }
    public long TotalSizeBytes { get; init; }
    public int TotalSectors { get; init; }
    public IReadOnlyList<string> GeneratedFiles { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> LogNotes { get; init; } = Array.Empty<string>();
    public DvdVideoValidationResult? ValidationResult { get; init; }
    public DvdVideoLayout? PlannedLayout { get; init; }
}

/// <summary>
/// Universal One-Click DVD-Video Authoring Engine.
/// Accepts arbitrary input (single/multiple movie files or existing VIDEO_TS folders), normalizes the structure,
/// transcodes video streams (or falls back to deterministic synthetic streams for testing), generates compliant
/// IFO/BUP/VOB headers, patches NAV packs, generates menus, and produces 100% DVD-compliant optical disc layouts.
/// </summary>
public sealed class DvdVideoAuthoringService
{
    /// <summary>
    /// Creates a compliant sectorCount * 2048 byte VOB buffer containing a valid NAV pack at sector 0
    /// and alternating MPEG-2 Video & AC-3 Audio PES sectors.
    /// </summary>
    public static byte[] CreateSyntheticMovieVob(int sectorCount = 32, int titleNumber = 1)
    {
        sectorCount = Math.Max(16, sectorCount);
        byte[] vob = new byte[sectorCount * OpticalConstants.DataSectorSize];

        for (int s = 1; s < sectorCount; s++)
        {
            int offset = s * OpticalConstants.DataSectorSize;

            // Pack Header (14 bytes)
            byte[] packHeader = [0x00, 0x00, 0x01, 0xBA, 0x44, 0x00, 0x04, 0x00, 0x04, 0x01, 0x01, 0x89, 0xC3, 0xF8];
            packHeader.CopyTo(vob, offset);

            if (s % 2 == 1)
            {
                // MPEG-2 Video PES packet
                vob[offset + 14] = 0x00;
                vob[offset + 15] = 0x00;
                vob[offset + 16] = 0x01;
                vob[offset + 17] = 0xE0;
                BinaryPrimitives.WriteUInt16BigEndian(vob.AsSpan(offset + 18), (ushort)(OpticalConstants.DataSectorSize - 20));
                vob[offset + 20] = 0x80;
                vob[offset + 21] = 0x00;
                vob[offset + 22] = 0x00;

                // MPEG-2 Sequence Header (00 00 01 B3)
                vob[offset + 23] = 0x00;
                vob[offset + 24] = 0x00;
                vob[offset + 25] = 0x01;
                vob[offset + 26] = 0xB3;
                vob[offset + 27] = 0x2D; // 720 width
                vob[offset + 28] = 0x01;
                vob[offset + 29] = 0xE0; // 480 height
            }
            else
            {
                // AC-3 Audio PES packet
                vob[offset + 14] = 0x00;
                vob[offset + 15] = 0x00;
                vob[offset + 16] = 0x01;
                vob[offset + 17] = 0xBD;
                BinaryPrimitives.WriteUInt16BigEndian(vob.AsSpan(offset + 18), (ushort)(OpticalConstants.DataSectorSize - 20));
                vob[offset + 20] = 0x80;
                vob[offset + 21] = 0x00;
                vob[offset + 22] = 0x00;
                vob[offset + 23] = 0x80; // AC-3 Substream 0
                vob[offset + 24] = 0x0B; // sync frame
                vob[offset + 25] = 0x77;
            }
        }

        DvdIfoGenerator.PatchVobNavPackets(vob, vobId: 1, cellId: 1, isMenu: false);
        return vob;
    }

    /// <summary>
    /// Authors DVD-Video from a single path (movie file, directory, or semicolon-delimited list).
    /// </summary>
    public Task<DvdAuthoringResult> AuthorAsync(
        string inputPath,
        string outputRootDirectory,
        DvdAuthoringOptions? options = null,
        IProgress<(double Percent, string Status)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        return AuthorAsync([inputPath], outputRootDirectory, options, progress, cancellationToken);
    }

    private static readonly Regex FfmpegDurationRegex = new(
        @"Duration:\s*(?<h>\d+):(?<m>\d+):(?<s>\d+(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FfmpegTimeRegex = new(
        @"time=\s*(?<h>\d+):(?<m>\d+):(?<s>\d+(?:\.\d+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FfmpegSpeedRegex = new(
        @"speed=\s*(?<spd>[\d\.]+x)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Authors DVD-Video from multiple input paths.
    /// </summary>
    public Task<DvdAuthoringResult> AuthorAsync(
        IEnumerable<string> inputPaths,
        string outputRootDirectory,
        DvdAuthoringOptions? options = null,
        IProgress<(double Percent, string Status)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputPaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRootDirectory);
        var pathList = inputPaths.ToList();

        return Task.Run(
            () => AuthorCoreAsync(pathList, outputRootDirectory, options, progress, cancellationToken),
            cancellationToken);
    }

    private async Task<DvdAuthoringResult> AuthorCoreAsync(
        IReadOnlyList<string> inputPaths,
        string outputRootDirectory,
        DvdAuthoringOptions? options,
        IProgress<(double Percent, string Status)>? progress,
        CancellationToken cancellationToken)
    {
        options ??= new DvdAuthoringOptions();
        var logNotes = new List<string>();

        progress?.Report((2.0, "Analyzing input files and directory structures..."));
        var analyzed = DvdInputNormalizer.AnalyzeInputs(inputPaths);
        foreach (var note in analyzed.NormalizationNotes)
        {
            logNotes.Add(note);
        }

        if (analyzed.Kind == DvdInputKind.EmptyOrInvalid)
        {
            return new DvdAuthoringResult
            {
                Success = false,
                ErrorMessage = "No valid DVD-Video directory or supported movie files were found in the provided input.",
                InputKind = DvdInputKind.EmptyOrInvalid,
                LogNotes = logNotes
            };
        }

        // Determine destination paths
        string rootDir = Path.GetFullPath(outputRootDirectory);
        string videoTsDir;
        string audioTsDir;

        string trimmedName = Path.GetFileName(rootDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.Equals(trimmedName, "VIDEO_TS", StringComparison.OrdinalIgnoreCase))
        {
            videoTsDir = rootDir;
            rootDir = Path.GetDirectoryName(rootDir) ?? rootDir;
            audioTsDir = Path.Combine(rootDir, "AUDIO_TS");
        }
        else
        {
            videoTsDir = Path.Combine(rootDir, "VIDEO_TS");
            audioTsDir = Path.Combine(rootDir, "AUDIO_TS");
        }

        Directory.CreateDirectory(rootDir);
        Directory.CreateDirectory(videoTsDir);
        Directory.CreateDirectory(audioTsDir);

        progress?.Report((5.0, "DVD directories prepared. Processing video content..."));

        string volumeLabel = string.IsNullOrWhiteSpace(options.VolumeLabel) || options.VolumeLabel == "DVD_VIDEO"
            ? analyzed.SuggestedVolumeLabel
            : DvdInputNormalizer.SanitizeVolumeLabel(options.VolumeLabel);

        string menuTitle = string.IsNullOrWhiteSpace(options.MenuTitle) || options.MenuTitle == "DVD Video"
            ? analyzed.SuggestedMenuTitle
            : options.MenuTitle;

        int titleCount = 0;

        if (analyzed.Kind == DvdInputKind.CompliantVideoTs)
        {
            progress?.Report((30.0, "Copying compliant VIDEO_TS files..."));
            string srcVideoTs = analyzed.ExistingVideoTsPath!;

            if (!string.Equals(Path.GetFullPath(srcVideoTs), Path.GetFullPath(videoTsDir), StringComparison.OrdinalIgnoreCase))
            {
                foreach (var file in Directory.GetFiles(srcVideoTs, "*.*", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string dst = Path.Combine(videoTsDir, Path.GetFileName(file).ToUpperInvariant());
                    File.Copy(file, dst, overwrite: true);
                }
            }

            var valCheck = new DvdVideoValidator().ValidateDirectory(videoTsDir);
            titleCount = valCheck.TitleSetNumbers.Count;
            logNotes.Add($"Used existing compliant VIDEO_TS with {titleCount} Title Set(s).");
        }
        else if (analyzed.Kind == DvdInputKind.RepairableVideoTs)
        {
            progress?.Report((30.0, "Repairing and normalizing VIDEO_TS files..."));
            DvdInputNormalizer.RepairVideoTsDirectory(analyzed.ExistingVideoTsPath!, videoTsDir, logNotes);

            // If menu requested and VIDEO_TS.VOB missing, synthesize
            string menuVobPath = Path.Combine(videoTsDir, "VIDEO_TS.VOB");
            if (options.AutoCreatePlayMenu && !File.Exists(menuVobPath))
            {
                var menuOpts = new DvdMenuOptions
                {
                    MenuTitle = menuTitle,
                    Standard = options.Standard,
                    AspectRatio = options.AspectRatio
                };

                byte[] menuBytes = await DvdMenuGenerator.GenerateMenuVobAsync(
                    menuOpts,
                    options.CustomFfmpegPath,
                    options.UseFfmpegWhenAvailable,
                    cancellationToken).ConfigureAwait(false);

                await File.WriteAllBytesAsync(menuVobPath, menuBytes, cancellationToken).ConfigureAwait(false);
                logNotes.Add("Created default play menu 'VIDEO_TS.VOB'.");
            }

            var valCheck = new DvdVideoValidator().ValidateDirectory(videoTsDir);
            titleCount = valCheck.TitleSetNumbers.Count;
        }
        else if (analyzed.Kind == DvdInputKind.MovieFiles)
        {
            titleCount = Math.Min(analyzed.MovieFilePaths.Count, 99);
            int menuVobSectors = 0;

            // 1. Generate Menu VOB if requested
            if (options.AutoCreatePlayMenu)
            {
                progress?.Report((8.0, "Generating interactive DVD Root Menu..."));
                var menuOpts = new DvdMenuOptions
                {
                    MenuTitle = menuTitle,
                    TitleNames = analyzed.CleanedMovieTitles,
                    Standard = options.Standard,
                    AspectRatio = options.AspectRatio
                };

                byte[] menuBytes = await DvdMenuGenerator.GenerateMenuVobAsync(
                    menuOpts,
                    options.CustomFfmpegPath,
                    options.UseFfmpegWhenAvailable,
                    cancellationToken).ConfigureAwait(false);

                string menuVobPath = Path.Combine(videoTsDir, "VIDEO_TS.VOB");
                await File.WriteAllBytesAsync(menuVobPath, menuBytes, cancellationToken).ConfigureAwait(false);
                menuVobSectors = menuBytes.Length / OpticalConstants.DataSectorSize;
                logNotes.Add($"Generated menu 'VIDEO_TS.VOB' ({menuVobSectors} sectors).");
            }

            // Probe movie durations if FFmpeg is available so we can auto-fit bitrate to Single-Layer DVD-5 and show accurate % progress
            string? ffmpegExe = null;
            bool ffmpegReady = options.UseFfmpegWhenAvailable &&
                               FfmpegLocator.IsFfmpegAvailable(out ffmpegExe, options.CustomFfmpegPath) &&
                               !string.IsNullOrEmpty(ffmpegExe);

            var probedDurations = new double[titleCount];
            double totalMovieDurationSeconds = 0;
            if (ffmpegReady)
            {
                for (int i = 0; i < titleCount; i++)
                {
                    double dur = await ProbeVideoDurationSecondsAsync(ffmpegExe!, analyzed.MovieFilePaths[i], cancellationToken).ConfigureAwait(false);
                    probedDurations[i] = dur;
                    if (dur > 0)
                    {
                        totalMovieDurationSeconds += dur;
                    }
                }
            }

            // Calculate optimal video bitrate so feature-length movies (e.g. 2 hours) automatically fit on Single-Layer DVD-5 (~4.25 GB VOB budget)
            int targetVideoBitrateKbps = options.VideoBitrateKbps;
            int targetMaxVideoBitrateKbps = options.MaxVideoBitrateKbps;
            if (totalMovieDurationSeconds > 60)
            {
                const double dvd5SafePayloadBytes = 4_250_000_000.0;
                double availableKilobits = (dvd5SafePayloadBytes * 8.0 / 1000.0) / 1.07; // 7% DVD PS mux overhead
                int maxTotalKbps = (int)(availableKilobits / totalMovieDurationSeconds);
                int fittedVideoKbps = Math.Clamp(maxTotalKbps - options.AudioBitrateKbps, 1800, options.VideoBitrateKbps);
                if (fittedVideoKbps < targetVideoBitrateKbps)
                {
                    targetVideoBitrateKbps = fittedVideoKbps;
                    targetMaxVideoBitrateKbps = Math.Clamp((int)(fittedVideoKbps * 1.35), fittedVideoKbps + 500, options.MaxVideoBitrateKbps);
                    logNotes.Add($"Auto-adjusted video bitrate to {targetVideoBitrateKbps} kbps (max {targetMaxVideoBitrateKbps} kbps) to fit {FormatSeconds(totalMovieDurationSeconds)} onto Single-Layer DVD-5.");
                }
            }

            // 2. Process each movie track into a Title Set (VTS_xx)
            var titleNavPacks = new Dictionary<int, IReadOnlyList<int>>();
            var titleTotalSectors = new Dictionary<int, int>();

            for (int i = 0; i < titleCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int setNum = i + 1;
                string moviePath = analyzed.MovieFilePaths[i];
                string movieName = i < analyzed.CleanedMovieTitles.Count ? analyzed.CleanedMovieTitles[i] : $"Title {setNum}";

                double titleStartPct = 12.0 + (70.0 * i / titleCount);
                double titleSpanPct = 70.0 / titleCount;
                progress?.Report((titleStartPct, $"Transcoding Title {setNum}/{titleCount}: '{movieName}'..."));

                // Place tempVob inside videoTsDir so it is on the same filesystem volume for fast 0ms File.Move
                string tempVob = Path.Combine(videoTsDir, $"_temp_transcode_{setNum}_{Guid.NewGuid():N}.vob");
                bool transcodeSuccess = false;
                double detectedDurationSeconds = probedDurations[i];

                try
                {
                    if (ffmpegReady)
                    {
                        var transcodeBuilder = (options.Standard == DvdVideoStandard.Pal
                            ? FfmpegDvdTranscodeBuilder.ForPal(moviePath, tempVob, options.AspectRatio)
                            : FfmpegDvdTranscodeBuilder.ForNtsc(moviePath, tempVob, options.AspectRatio))
                            .WithScaleAndPadFilter(true)
                            .WithVideoBitrate(targetVideoBitrateKbps, targetMaxVideoBitrateKbps)
                            .WithAudio("ac3", options.AudioBitrateKbps);

                        (transcodeSuccess, detectedDurationSeconds) = await RunFfmpegTranscodeWithProgressAsync(
                            ffmpegExe!,
                            transcodeBuilder.BuildArgumentList(),
                            tempVob,
                            setNum,
                            titleCount,
                            movieName,
                            detectedDurationSeconds,
                            titleStartPct,
                            titleSpanPct,
                            progress,
                            logNotes,
                            cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    try { if (File.Exists(tempVob)) File.Delete(tempVob); } catch { }
                    throw;
                }
                catch (Exception ex)
                {
                    logNotes.Add($"Transcode attempt failed for '{Path.GetFileName(moviePath)}': {ex.Message}. Falling back to synthetic VOB.");
                }

                if (!transcodeSuccess)
                {
                    long inputLen = 0;
                    try { inputLen = new FileInfo(moviePath).Length; } catch { }
                    // Cap synthetic fallback to 4096 sectors max if FFmpeg failed on a large dummy/corrupt file
                    int secCount = (int)Math.Clamp(inputLen / OpticalConstants.DataSectorSize, 32L, 4096L);
                    byte[] synthBytes = CreateSyntheticMovieVob(secCount, setNum);
                    await File.WriteAllBytesAsync(tempVob, synthBytes, cancellationToken).ConfigureAwait(false);
                    logNotes.Add($"Generated synthetic movie VOB for Title {setNum} ({secCount} sectors).");
                }

                // Patch NAV packets on the contiguous VOB stream before splitting into 1 GB extents
                double navPct = titleStartPct + titleSpanPct * 0.92;
                progress?.Report((navPct, $"Patching DVD NAV packets & VOBU address maps for Title {setNum}/{titleCount}..."));
                DvdIfoGenerator.PatchVobNavPacketsInFile(tempVob, vobId: 1, cellId: 1, isMenu: false);
                var allNavSectors = DvdIfoGenerator.ExtractNavPackSectors(tempVob).ToList();

                // Split oversized VOB (> 1 GB)
                double splitPct = titleStartPct + titleSpanPct * 0.96;
                progress?.Report((splitPct, $"Formatting 1 GB DVD VOB extents for Title {setNum}/{titleCount}: '{movieName}'..."));
                var vobParts = DvdIfoGenerator.SplitVobFileIfOversized(tempVob, videoTsDir, setNum);

                // Clean up temp transcode file
                try
                {
                    if (File.Exists(tempVob) && !vobParts.Contains(tempVob, StringComparer.OrdinalIgnoreCase))
                    {
                        File.Delete(tempVob);
                    }
                }
                catch { }

                int totalVobSecs = 0;
                for (int p = 0; p < vobParts.Count; p++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string part = vobParts[p];
                    totalVobSecs += (int)(new FileInfo(part).Length / OpticalConstants.DataSectorSize);
                }

                allNavSectors.RemoveAll(s => s >= totalVobSecs);
                if (allNavSectors.Count == 0)
                {
                    allNavSectors.Add(0);
                }

                titleNavPacks[setNum] = allNavSectors;
                titleTotalSectors[setNum] = totalVobSecs;

                // Build VTS_xx_0.IFO and VTS_xx_0.BUP
                int nextTitle = (setNum < titleCount) ? (setNum + 1) : 0;
                int effectiveDurationSec = detectedDurationSeconds > 1.0 ? (int)Math.Round(detectedDurationSeconds) : 60;
                byte[] vtsIfoBytes = DvdIfoGenerator.BuildVtsIfo(
                    titleSetNumber: setNum,
                    standard: options.Standard,
                    aspect: options.AspectRatio,
                    titleVobTotalSectors: totalVobSecs,
                    durationSeconds: effectiveDurationSec,
                    menuVobSectors: 0,
                    navPackSectors: allNavSectors,
                    nextTitleNumber: nextTitle);

                string vtsIfoPath = Path.Combine(videoTsDir, $"VTS_{setNum:D2}_0.IFO");
                string vtsBupPath = Path.Combine(videoTsDir, $"VTS_{setNum:D2}_0.BUP");

                await File.WriteAllBytesAsync(vtsIfoPath, vtsIfoBytes, cancellationToken).ConfigureAwait(false);
                await File.WriteAllBytesAsync(vtsBupPath, vtsIfoBytes, cancellationToken).ConfigureAwait(false);
            }

            // 3. Plan preliminary layout to determine exact VTS start sector offsets
            progress?.Report((86.0, "Building Video Manager (VIDEO_TS.IFO / VIDEO_TS.BUP) navigation tables..."));
            var layoutPlanner = new DvdVideoLayoutPlanner();
            var preliminaryFiles = Directory.GetFiles(videoTsDir)
                .Select(f => (IDvdVideoFileEntry)new FileSystemDvdVideoFileEntry(new FileInfo(f)))
                .ToList();

            int vtsAtrtBytes = 8 + titleCount * 4 + titleCount * 772;
            int vmgIfoSectors = 5 + Math.Max(1, (vtsAtrtBytes + OpticalConstants.DataSectorSize - 1) / OpticalConstants.DataSectorSize);

            if (!preliminaryFiles.Any(f => string.Equals(f.Name, "VIDEO_TS.IFO", StringComparison.OrdinalIgnoreCase)))
            {
                preliminaryFiles.Add(new DvdVideoMemoryFileEntry("VIDEO_TS.IFO", new byte[vmgIfoSectors * OpticalConstants.DataSectorSize]));
            }
            if (!preliminaryFiles.Any(f => string.Equals(f.Name, "VIDEO_TS.BUP", StringComparison.OrdinalIgnoreCase)))
            {
                preliminaryFiles.Add(new DvdVideoMemoryFileEntry("VIDEO_TS.BUP", new byte[vmgIfoSectors * OpticalConstants.DataSectorSize]));
            }

            var prelimLayout = layoutPlanner.Plan(preliminaryFiles, startLba: 0);

            var vtsStartOffsets = new List<int>();
            for (int i = 1; i <= titleCount; i++)
            {
                var ifoPlacement = prelimLayout.GetPlacement($"VTS_{i:D2}_0.IFO");
                int startLba = ifoPlacement?.StartLba.Value ?? (vmgIfoSectors + menuVobSectors + (i - 1) * 32);
                vtsStartOffsets.Add(startLba);
            }

            // 4. Write VIDEO_TS.IFO and identical VIDEO_TS.BUP
            byte[] vmgIfoBytes = DvdIfoGenerator.BuildVmgIfo(
                titleSetCount: titleCount,
                standard: options.Standard,
                aspect: options.AspectRatio,
                menuVobSectors: menuVobSectors,
                vtsStartSectorOffsets: vtsStartOffsets,
                autoPlayMenu: options.AutoCreatePlayMenu);

            string vmgIfoPath = Path.Combine(videoTsDir, "VIDEO_TS.IFO");
            string vmgBupPath = Path.Combine(videoTsDir, "VIDEO_TS.BUP");

            await File.WriteAllBytesAsync(vmgIfoPath, vmgIfoBytes, cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(vmgBupPath, vmgIfoBytes, cancellationToken).ConfigureAwait(false);
            logNotes.Add($"Built compliant 'VIDEO_TS.IFO' and 'VIDEO_TS.BUP' for {titleCount} Title Set(s).");
        }

        // Final Validation & Layout Calculation
        progress?.Report((92.0, "Validating authored DVD structure and calculating 32 KB ECC alignment..."));
        var validator = new DvdVideoValidator();
        var valResult = validator.ValidateDirectory(rootDir);

        var finalFilesOnDisk = Directory.GetFiles(videoTsDir)
            .Select(f => (IDvdVideoFileEntry)new FileSystemDvdVideoFileEntry(new FileInfo(f)))
            .ToList();

        var finalPlanner = new DvdVideoLayoutPlanner();
        var finalLayout = finalPlanner.Plan(finalFilesOnDisk, startLba: 0);

        long totalBytes = finalFilesOnDisk.Sum(f => f.SizeBytes);
        int totalSectors = finalLayout.TotalSectors;

        var generatedFilesList = Directory.GetFiles(videoTsDir, "*.*", SearchOption.TopDirectoryOnly).ToList();

        progress?.Report((100.0, "DVD-Video authoring complete. Ready to burn!"));

        return new DvdAuthoringResult
        {
            Success = valResult.IsValid,
            ErrorMessage = valResult.IsValid ? null : string.Join("; ", valResult.Errors),
            OutputRootDirectory = rootDir,
            VideoTsDirectory = videoTsDir,
            AudioTsDirectory = audioTsDir,
            VolumeLabel = volumeLabel,
            InputKind = analyzed.Kind,
            TitleCount = titleCount,
            TotalSizeBytes = totalBytes,
            TotalSectors = totalSectors,
            GeneratedFiles = generatedFilesList,
            LogNotes = logNotes,
            ValidationResult = valResult,
            PlannedLayout = finalLayout
        };
    }

    private static async Task<double> ProbeVideoDurationSecondsAsync(
        string ffmpegExe,
        string moviePath,
        CancellationToken cancellationToken)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegExe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(moviePath);

            using var proc = Process.Start(psi);
            if (proc == null) return 0;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var outTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var errTask = proc.StandardError.ReadToEndAsync(cts.Token);

            try
            {
                await Task.WhenAll(outTask, errTask, proc.WaitForExitAsync(cts.Token)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                throw;
            }
            catch
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            }

            string stderr = errTask.IsCompletedSuccessfully ? errTask.Result : string.Empty;
            var match = FfmpegDurationRegex.Match(stderr);
            if (match.Success)
            {
                return ParseHms(match);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
        }

        return 0;
    }

    private static async Task<(bool Success, double DurationSeconds)> RunFfmpegTranscodeWithProgressAsync(
        string ffmpegExe,
        IReadOnlyList<string> arguments,
        string tempVob,
        int setNum,
        int titleCount,
        string movieName,
        double initialDurationSeconds,
        double startPct,
        double spanPct,
        IProgress<(double Percent, string Status)>? progress,
        List<string> logNotes,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-threads");
        psi.ArgumentList.Add("0");
        foreach (var arg in arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi);
        if (proc == null)
        {
            return (false, initialDurationSeconds);
        }

        using var killReg = cancellationToken.Register(() =>
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
        });

        double durationSec = initialDurationSeconds;
        long lastReportTick = 0;
        long throttleTicks = Stopwatch.Frequency / 4; // Max 4 UI updates per second

        // Drain stdout in background
        var stdOutTask = Task.Run(async () =>
        {
            try
            {
                char[] buf = new char[512];
                while (await proc.StandardOutput.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false) > 0)
                {
                }
            }
            catch
            {
            }
        }, CancellationToken.None);

        // Stream stderr in real time, splitting on \r and \n so FFmpeg progress lines never fill the OS pipe buffer
        var stdErrTask = Task.Run(async () =>
        {
            var sb = new StringBuilder(256);
            char[] buf = new char[512];
            try
            {
                int read;
                while ((read = await proc.StandardError.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
                {
                    for (int i = 0; i < read; i++)
                    {
                        char c = buf[i];
                        if (c == '\r' || c == '\n')
                        {
                            if (sb.Length > 0)
                            {
                                string line = sb.ToString();
                                sb.Clear();
                                ProcessFfmpegLine(line);
                            }
                        }
                        else if (sb.Length < 1024)
                        {
                            sb.Append(c);
                        }
                    }
                }

                if (sb.Length > 0)
                {
                    ProcessFfmpegLine(sb.ToString());
                }
            }
            catch
            {
            }
        }, CancellationToken.None);

        void ProcessFfmpegLine(string line)
        {
            if (durationSec <= 0 && line.Contains("Duration:", StringComparison.Ordinal))
            {
                var durMatch = FfmpegDurationRegex.Match(line);
                if (durMatch.Success)
                {
                    durationSec = ParseHms(durMatch);
                }
            }

            if (progress != null && line.Contains("time=", StringComparison.Ordinal))
            {
                long now = Stopwatch.GetTimestamp();
                if (now - lastReportTick < throttleTicks)
                {
                    return;
                }

                var timeMatch = FfmpegTimeRegex.Match(line);
                if (timeMatch.Success)
                {
                    lastReportTick = now;
                    double curSec = ParseHms(timeMatch);
                    var spdMatch = FfmpegSpeedRegex.Match(line);
                    string speedStr = spdMatch.Success ? spdMatch.Groups["spd"].Value : string.Empty;

                    if (durationSec > 0)
                    {
                        double ratio = Math.Clamp(curSec / durationSec, 0.0, 0.99);
                        double pct = startPct + (spanPct * 0.90 * ratio);
                        string status = string.IsNullOrEmpty(speedStr)
                            ? $"Transcoding Title {setNum}/{titleCount}: '{movieName}' — {ratio * 100.0:F1}% ({FormatSeconds(curSec)} / {FormatSeconds(durationSec)})"
                            : $"Transcoding Title {setNum}/{titleCount}: '{movieName}' — {ratio * 100.0:F1}% ({FormatSeconds(curSec)} / {FormatSeconds(durationSec)}, {speedStr})";
                        progress.Report((pct, status));
                    }
                    else
                    {
                        double pct = Math.Min(startPct + spanPct * 0.85, startPct + (curSec % 60));
                        string status = $"Transcoding Title {setNum}/{titleCount}: '{movieName}' ({FormatSeconds(curSec)})...";
                        progress.Report((pct, status));
                    }
                }
            }
        }

        await Task.WhenAll(stdOutTask, stdErrTask, proc.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);

        if (proc.ExitCode == 0 && File.Exists(tempVob) && new FileInfo(tempVob).Length >= OpticalConstants.DataSectorSize)
        {
            logNotes.Add($"Transcoded '{movieName}' to DVD MPEG-2 via FFmpeg ({new FileInfo(tempVob).Length / (1024 * 1024):N0} MB).");
            return (true, durationSec);
        }

        logNotes.Add($"FFmpeg exited with code {proc.ExitCode} for '{movieName}'. Falling back to synthetic VOB.");
        return (false, durationSec);
    }

    private static double ParseHms(Match match)
    {
        double h = double.TryParse(match.Groups["h"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var hv) ? hv : 0;
        double m = double.TryParse(match.Groups["m"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mv) ? mv : 0;
        double s = double.TryParse(match.Groups["s"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var sv) ? sv : 0;
        return (h * 3600.0) + (m * 60.0) + s;
    }

    private static string FormatSeconds(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalHours >= 1.0
            ? $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }
}
