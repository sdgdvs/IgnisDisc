using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Archival;
using CdBurner.Core.Compilation;
using CdBurner.Core.Images;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Audio;

/// <summary>
/// Metadata describing a single audio track stored in session 1 and protected by session 2's PAR2 volume.
/// </summary>
public sealed record AudioRecoveryTrackEntry(
    int TrackNumber,
    long ByteOffset,
    long ByteLength,
    int SectorCount,
    string Sha256Hex,
    uint Crc32,
    string? Title,
    string? Performer,
    string? Isrc,
    int PreGapFrames);

/// <summary>
/// JSON manifest written alongside the PAR2 volume in session 2 so the ripper can verify every track's PCM slice
/// and rebuild damaged samples without guessing track boundaries.
/// </summary>
public sealed record AudioRecoveryManifest
{
    public const int CurrentFormatVersion = 1;
    public const string PcmImageFileName = "audio.pcm";
    public const string ManifestFileName = "audio-recovery.json";
    public const string Par2IndexFileName = "recovery.par2";
    public const string VolumeLabel = "IGNIS_REC";

    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public string PcmFileName { get; init; } = PcmImageFileName;
    public long TotalPcmBytes { get; init; }
    public long TotalPcmSectors { get; init; }
    public int Par2BlockSizeBytes { get; init; }
    public int Par2RecoveryBlocks { get; init; }
    public string ToleranceSummary { get; init; } = string.Empty;
    public string? AlbumTitle { get; init; }
    public string? AlbumPerformer { get; init; }
    public IReadOnlyList<AudioRecoveryTrackEntry> Tracks { get; init; } = Array.Empty<AudioRecoveryTrackEntry>();
}

/// <summary>
/// Location and extent of the second (data recovery) session on an Enhanced CD.
/// </summary>
public readonly record struct RecoverySessionLocation(int StartLba, int SectorCount);

/// <summary>
/// Outcome of checking and optionally repairing ripped audio tracks against session 2's PAR2 recovery volume.
/// </summary>
public sealed record AudioRecoveryRepairResult
{
    public bool RecoverySessionFound { get; init; }
    public bool RepairAttempted { get; init; }
    public bool AllTracksVerified { get; init; }
    public int TracksChecked { get; init; }
    public IReadOnlyList<int> DamagedTracks { get; init; } = Array.Empty<int>();
    public IReadOnlyList<int> RepairedTracks { get; init; } = Array.Empty<int>();
    public IReadOnlyList<int> FailedTracks { get; init; } = Array.Empty<int>();
    public string Summary { get; init; } = string.Empty;

    [JsonIgnore]
    public Exception? Error { get; init; }
}

/// <summary>
/// Capacity and tolerance estimate shown on the Audio CD screen before burning a recovery session.
/// </summary>
public sealed record AudioRecoveryEstimate(
    bool Fits,
    long AudioSectors,
    long OverheadSectors,
    long BudgetSectors,
    Par2RecoveryLayout? Layout,
    string ToleranceSummary,
    string WarningText);

/// <summary>
/// Staged session-2 ISO stream together with its temporary working directory (disposed after session 2 burns).
/// </summary>
public sealed class StagedRecoverySessionIso : IDisposable
{
    private readonly string _stagingDirectory;
    private bool _disposed;

    internal StagedRecoverySessionIso(
        MemoryStream isoStream,
        AudioRecoveryManifest manifest,
        string stagingDirectory,
        int isoSectors)
    {
        IsoStream = isoStream;
        Manifest = manifest;
        _stagingDirectory = stagingDirectory;
        IsoSectors = isoSectors;
    }

    public MemoryStream IsoStream { get; }
    public AudioRecoveryManifest Manifest { get; }
    public int IsoSectors { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        IsoStream.Dispose();
        try
        {
            if (Directory.Exists(_stagingDirectory))
            {
                Directory.Delete(_stagingDirectory, recursive: true);
            }
        }
        catch
        {
        }
    }
}

/// <summary>
/// Plans, builds, reads, and repairs Enhanced CD (CD-Extra) session-2 PAR2 recovery data protecting session-1 PCM audio.
/// </summary>
public static class AudioRecoverySessionService
{
    /// <summary>Reserved sectors for ISO 9660 + Joliet tables and <see cref="AudioRecoveryManifest.ManifestFileName"/> in session 2.</summary>
    public const long FileSystemReserveSectors = 96;

    /// <summary>Short warning displayed anywhere Enhanced CD recovery is offered.</summary>
    public const string CarHeadUnitWarning =
        "Some car head units reject two-session Enhanced CDs (for example, 2019 Subaru/Harman systems); keep off if car playback is required.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Evaluates whether an Audio CD of <paramref name="audioSectors"/> frames leaves enough space after session 1
    /// and the ~2.5-minute (11,400-sector) multi-session gap to hold PAR2 recovery data in session 2.
    /// </summary>
    public static AudioRecoveryEstimate EstimateCapacity(long audioSectors, long discCapacitySectors = RecoverySessionBudget.MeasuredCapacitySectors)
    {
        long normalizedAudio = Math.Max(0, audioSectors);
        long capacity = discCapacitySectors > 0 ? discCapacitySectors : RecoverySessionBudget.MeasuredCapacitySectors;

        if (normalizedAudio == 0)
        {
            return new AudioRecoveryEstimate(
                Fits: false,
                AudioSectors: 0,
                OverheadSectors: RecoverySessionBudget.FixedSessionSectors + FileSystemReserveSectors,
                BudgetSectors: 0,
                Layout: null,
                ToleranceSummary: "Add audio tracks to calculate recovery session capacity.",
                WarningText: CarHeadUnitWarning);
        }

        long rawFree = Math.Max(0, capacity - normalizedAudio - RecoverySessionBudget.FixedSessionSectors - FileSystemReserveSectors);
        long cappedBudget = CapHelpfulPar2Budget(normalizedAudio * OpticalConstants.RawSectorSize, rawFree);
        if (cappedBudget <= 0)
        {
            return new AudioRecoveryEstimate(
                Fits: false,
                AudioSectors: normalizedAudio,
                OverheadSectors: RecoverySessionBudget.FixedSessionSectors + FileSystemReserveSectors,
                BudgetSectors: 0,
                Layout: null,
                ToleranceSummary: "Disc is too full: session 2 needs ~2.5 min (11,400 sectors) of multi-session overhead plus recovery data.",
                WarningText: CarHeadUnitWarning);
        }

        var pcmFile = new[] { new Par2SourceFile(AudioRecoveryManifest.PcmImageFileName, normalizedAudio * OpticalConstants.RawSectorSize) };
        var layout = Par2RecoverySizer.Plan(pcmFile, cappedBudget);
        if (layout == null)
        {
            return new AudioRecoveryEstimate(
                Fits: false,
                AudioSectors: normalizedAudio,
                OverheadSectors: RecoverySessionBudget.FixedSessionSectors + FileSystemReserveSectors,
                BudgetSectors: cappedBudget,
                Layout: null,
                ToleranceSummary: "Not enough room remaining after session 2 overhead (11,400 sectors) for a single PAR2 recovery block.",
                WarningText: CarHeadUnitWarning);
        }

        string tolerance = rawFree > cappedBudget
            ? "Short compilation: room left for over 100% PAR2 recovery coverage (full rebuild of damaged audio)."
            : RecoverySessionBudget.DescribeTolerance(layout);

        return new AudioRecoveryEstimate(
            Fits: true,
            AudioSectors: normalizedAudio,
            OverheadSectors: RecoverySessionBudget.FixedSessionSectors + FileSystemReserveSectors,
            BudgetSectors: cappedBudget,
            Layout: layout,
            ToleranceSummary: tolerance,
            WarningText: CarHeadUnitWarning);
    }

    /// <summary>
    /// Creates <c>audio.pcm</c>, runs <c>par2 create</c>, writes <c>audio-recovery.json</c>, and packages session 2's
    /// <c>RECOVERY/</c> tree (without <c>audio.pcm</c>) into an ISO 9660 + Joliet image laid out from
    /// <paramref name="sessionStartLba"/>.
    /// </summary>
    public static async Task<StagedRecoverySessionIso> BuildRecoverySessionIsoAsync(
        IReadOnlyList<AudioTrackSource> tracks,
        AudioCdCompilation compilation,
        int sessionStartLba,
        long discCapacitySectors,
        string par2Path,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentException.ThrowIfNullOrWhiteSpace(par2Path);
        if (tracks.Count == 0)
        {
            throw new ArgumentException("At least one audio track is required.", nameof(tracks));
        }

        string stageRoot = Path.Combine(Path.GetTempPath(), $"ignis_rec_stage_{Guid.NewGuid():N}");
        Directory.CreateDirectory(stageRoot);

        try
        {
            string pcmPath = Path.Combine(stageRoot, AudioRecoveryManifest.PcmImageFileName);
            var trackEntries = new List<AudioRecoveryTrackEntry>(tracks.Count);
            long offset = 0;

            progress?.Report("Preparing PCM image and checksums for recovery session...");
            await using (var pcmOut = new FileStream(pcmPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: false))
            {
                byte[] copyBuf = new byte[65536];
                foreach (var track in tracks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    bool ownsStream = track.PcmStreamFactory != null;
                    Stream src = track.PcmStreamFactory != null
                        ? track.PcmStreamFactory()
                        : track.PcmStream ?? throw new InvalidOperationException($"Track {track.TrackNumber} has no readable PCM stream.");
                    if (!ownsStream && src.CanSeek)
                    {
                        src.Position = 0;
                    }
                    try
                    {
                        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        var crc = new Crc32Algorithm();
                        long trackBytes = 0;

                        int read;
                        while ((read = src.Read(copyBuf, 0, copyBuf.Length)) > 0)
                        {
                            pcmOut.Write(copyBuf, 0, read);
                            sha.AppendData(copyBuf.AsSpan(0, read));
                            crc.Append(copyBuf.AsSpan(0, read));
                            trackBytes += read;
                        }

                        // Keep each track slice sector-aligned (2,352-byte Red Book frames) to match burned TOC frames.
                        int remainder = (int)(trackBytes % OpticalConstants.RawSectorSize);
                        if (remainder != 0)
                        {
                            int pad = OpticalConstants.RawSectorSize - remainder;
                            byte[] padBuf = new byte[pad];
                            pcmOut.Write(padBuf, 0, pad);
                            sha.AppendData(padBuf);
                            crc.Append(padBuf);
                            trackBytes += pad;
                        }

                        int sectors = (int)(trackBytes / OpticalConstants.RawSectorSize);
                        string shaHex = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();

                        trackEntries.Add(new AudioRecoveryTrackEntry(
                            TrackNumber: track.TrackNumber,
                            ByteOffset: offset,
                            ByteLength: trackBytes,
                            SectorCount: sectors,
                            Sha256Hex: shaHex,
                            Crc32: crc.GetCurrentHashAsUInt32(),
                            Title: track.Title,
                            Performer: track.Performer,
                            Isrc: track.Isrc,
                            PreGapFrames: track.PreGapFrames));
                        offset += trackBytes;
                    }
                    finally
                    {
                        if (ownsStream)
                        {
                            src.Dispose();
                        }
                    }
                }
            }

            long capacity = discCapacitySectors > 0 ? discCapacitySectors : RecoverySessionBudget.MeasuredCapacitySectors;
            long remainingFromNwa = Math.Max(0, capacity - sessionStartLba);
            long rawDataBudget = Math.Max(0, remainingFromNwa - FileSystemReserveSectors);
            long budgetSectors = CapHelpfulPar2Budget(offset, rawDataBudget);

            var pcmSource = new[] { new Par2SourceFile(AudioRecoveryManifest.PcmImageFileName, offset) };
            var layout = Par2RecoverySizer.Plan(pcmSource, budgetSectors);
            if (layout == null)
            {
                throw new InvalidOperationException(
                    $"Insufficient capacity from LBA {sessionStartLba:N0} (remaining {remainingFromNwa:N0} sectors) to store PAR2 recovery data.");
            }

            string scratchDir = Path.Combine(stageRoot, "_par2_scratch");
            progress?.Report($"Generating PAR2 recovery data ({layout.RecoveryBlocks:N0} recovery blocks, {layout.BlockSize / 1024} KiB block size)...");
            await Par2Runner.CreateAsync(par2Path, stageRoot, layout, scratchDir, cancellationToken).ConfigureAwait(false);
            if (Directory.Exists(scratchDir))
            {
                Directory.Delete(scratchDir, recursive: true);
            }

            string recoveryDir = Path.Combine(stageRoot, ArchiveManifest.RecoveryFolderName);
            string toleranceSummary = RecoverySessionBudget.DescribeTolerance(layout);
            var manifest = new AudioRecoveryManifest
            {
                FormatVersion = AudioRecoveryManifest.CurrentFormatVersion,
                PcmFileName = AudioRecoveryManifest.PcmImageFileName,
                TotalPcmBytes = offset,
                TotalPcmSectors = offset / OpticalConstants.RawSectorSize,
                Par2BlockSizeBytes = layout.BlockSize,
                Par2RecoveryBlocks = layout.RecoveryBlocks,
                ToleranceSummary = toleranceSummary,
                AlbumTitle = compilation.AlbumTitle,
                AlbumPerformer = compilation.AlbumPerformer,
                Tracks = trackEntries
            };

            string manifestPath = Path.Combine(recoveryDir, AudioRecoveryManifest.ManifestFileName);
            await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions), Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);

            // Build ISO containing ONLY the RECOVERY directory (never audio.pcm itself).
            var isoRoot = new CompilationDirectory(AudioRecoveryManifest.VolumeLabel);
            var isoRecoveryDir = isoRoot.AddDirectory(ArchiveManifest.RecoveryFolderName);
            foreach (string filePath in Directory.EnumerateFiles(recoveryDir, "*", SearchOption.TopDirectoryOnly).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var fi = new FileInfo(filePath);
                isoRecoveryDir.AddFile(new CompilationFile(
                    name: fi.Name,
                    sourceFilePath: fi.FullName,
                    sizeBytes: fi.Length,
                    streamFactory: () => File.OpenRead(fi.FullName),
                    parent: isoRecoveryDir));
            }

            progress?.Report("Building session-2 ISO 9660/Joliet recovery image...");
            MemoryStream isoStream = IsoUdfImageBuilder.BuildToMemoryStream(
                isoRoot,
                new IsoBuildOptions(VolumeLabel: AudioRecoveryManifest.VolumeLabel, IncludeUdf: false),
                sessionStartLba: sessionStartLba);

            int isoSectors = (int)((isoStream.Length + OpticalConstants.DataSectorSize - 1) / OpticalConstants.DataSectorSize);
            int paddedSectors = Math.Max(300, isoSectors);
            if (paddedSectors > remainingFromNwa)
            {
                isoStream.Dispose();
                throw new InvalidOperationException(
                    $"Session 2 recovery image requires {paddedSectors:N0} sectors, exceeding remaining disc space ({remainingFromNwa:N0} sectors).");
            }

            return new StagedRecoverySessionIso(isoStream, manifest, stageRoot, isoSectors);
        }
        catch
        {
            try
            {
                if (Directory.Exists(stageRoot))
                {
                    Directory.Delete(stageRoot, recursive: true);
                }
            }
            catch
            {
            }
            throw;
        }
    }

    /// <summary>
    /// Locates the data session (session 2+) on an inserted Enhanced CD via TOC or virtual session metadata.
    /// </summary>
    public static RecoverySessionLocation? FindRecoverySessionLocation(IDiscRecorderBackend recorder)
    {
        ArgumentNullException.ThrowIfNull(recorder);

        if (recorder is IDiscTocProvider tocProvider)
        {
            var toc = tocProvider.ReadTableOfContents();
            bool hasAudio = toc.Any(t => t.IsAudio);
            var dataTrack = toc.LastOrDefault(t => !t.IsAudio);
            if (hasAudio && dataTrack != null)
            {
                return new RecoverySessionLocation(dataTrack.StartLba, Math.Max(17, dataTrack.SectorCount));
            }
        }

        // VirtualDiscRecorder fallback via reflection (CdBurner.Core does not reference CdBurner.Hardware).
        var medium = recorder.GetType().GetProperty("CurrentMedium")?.GetValue(recorder);
        if (medium?.GetType().GetProperty("Sessions")?.GetValue(medium) is System.Collections.IEnumerable sessions)
        {
            bool sawAudioSession = false;
            RecoverySessionLocation? found = null;
            foreach (var session in sessions)
            {
                int startLba = session?.GetType().GetProperty("StartLba")?.GetValue(session) is int s ? s : 0;
                int sectorCount = session?.GetType().GetProperty("SectorCount")?.GetValue(session) is int c ? c : 0;
                bool isAudioSession = false;
                bool hasDataTrack = false;
                if (session?.GetType().GetProperty("Tracks")?.GetValue(session) is System.Collections.IEnumerable tracks)
                {
                    foreach (var t in tracks)
                    {
                        bool? isAudio = t?.GetType().GetProperty("IsAudio")?.GetValue(t) as bool?;
                        if (isAudio == true) isAudioSession = true;
                        else if (isAudio == false) hasDataTrack = true;
                    }
                }
                if (isAudioSession)
                {
                    sawAudioSession = true;
                }
                else if (sawAudioSession && hasDataTrack)
                {
                    found = new RecoverySessionLocation(startLba, Math.Max(17, sectorCount));
                }
            }
            return found;
        }

        return null;
    }

    /// <summary>
    /// Reads session 2 from the optical disc, checks each ripped track's PCM against <c>audio-recovery.json</c>,
    /// rebuilds damaged samples using PAR2, and updates damaged WAV files in place.
    /// </summary>
    public static async Task<AudioRecoveryRepairResult> RepairRippedTracksFromDiscAsync(
        IDiscRecorderBackend recorder,
        IBurnEngineBackend engine,
        IReadOnlyList<AudioDiscoveredTrack> allDiscAudioTracks,
        IList<AudioTrackRipInfo> rippedTrackInfos,
        AudioRipOptions ripOptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(allDiscAudioTracks);
        ArgumentNullException.ThrowIfNull(rippedTrackInfos);
        ArgumentNullException.ThrowIfNull(ripOptions);

        RecoverySessionLocation? location = FindRecoverySessionLocation(recorder);
        if (location == null)
        {
            return new AudioRecoveryRepairResult
            {
                RecoverySessionFound = false,
                Summary = "No recovery session (session 2) found on this disc."
            };
        }

        string tempWork = Path.Combine(Path.GetTempPath(), $"ignis_rec_rip_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempWork);

        try
        {
            string recoveryDir = Path.Combine(tempWork, ArchiveManifest.RecoveryFolderName);
            Directory.CreateDirectory(recoveryDir);

            await using var discStream = new DiscDataSessionStream(
                recorder,
                engine,
                location.Value.StartLba,
                location.Value.SectorCount,
                cancellationToken);

            AudioRecoveryManifest? manifest = null;
            using (var reader = IsoImageReader.FromStream(discStream, leaveOpen: true, sessionStartLba: location.Value.StartLba))
            {
                foreach (var isoFile in reader.AllFiles)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string norm = isoFile.VirtualPath.Replace('\\', '/').TrimStart('/');
                    if (!norm.StartsWith(ArchiveManifest.RecoveryFolderName + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string fileName = Path.GetFileName(norm);
                    if (string.IsNullOrWhiteSpace(fileName))
                    {
                        continue;
                    }

                    string destPath = Path.Combine(recoveryDir, fileName);
                    await using var src = reader.OpenFileStream(isoFile);
                    await using var dst = File.Create(destPath);
                    await src.CopyToAsync(dst, cancellationToken).ConfigureAwait(false);
                }
            }

            string manifestPath = Path.Combine(recoveryDir, AudioRecoveryManifest.ManifestFileName);
            string par2IndexPath = Path.Combine(recoveryDir, AudioRecoveryManifest.Par2IndexFileName);
            if (!File.Exists(manifestPath) || !File.Exists(par2IndexPath))
            {
                return new AudioRecoveryRepairResult
                {
                    RecoverySessionFound = true,
                    Summary = "Session 2 does not contain IgnisDisc audio PAR2 recovery files."
                };
            }

            string manifestJson = await File.ReadAllTextAsync(manifestPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            manifest = JsonSerializer.Deserialize<AudioRecoveryManifest>(manifestJson, JsonOptions);
            if (manifest == null || manifest.Tracks.Count == 0)
            {
                return new AudioRecoveryRepairResult
                {
                    RecoverySessionFound = true,
                    Summary = "Session 2 recovery manifest could not be parsed or lists no tracks."
                };
            }

            string? par2Path = Par2Runner.FindPar2Executable();
            if (string.IsNullOrEmpty(par2Path))
            {
                return new AudioRecoveryRepairResult
                {
                    RecoverySessionFound = true,
                    TracksChecked = rippedTrackInfos.Count,
                    Summary = "par2.exe was not found, so session 2 recovery repair could not run.",
                    Error = new FileNotFoundException("par2.exe executable not found.")
                };
            }

            return await VerifyAndRepairCoreAsync(
                manifest,
                recoveryDir,
                tempWork,
                par2Path,
                rippedTrackInfos,
                missingTrackLoader: (entry, ct) => ReadDiscTrackPcmAsync(recorder, engine, allDiscAudioTracks, entry.TrackNumber, ripOptions.ReadOffsetSamples, ct),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            return new AudioRecoveryRepairResult
            {
                RecoverySessionFound = true,
                Summary = $"Recovery session check failed: {ex.Message}",
                Error = ex
            };
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempWork))
                {
                    Directory.Delete(tempWork, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Verifies and repairs already-extracted track WAV files against an extracted <paramref name="recoveryDirectory"/>
    /// containing <c>audio-recovery.json</c> and <c>recovery*.par2</c>. Useful directly in tests and offline workflows.
    /// </summary>
    public static async Task<AudioRecoveryRepairResult> RepairRippedTracksFromFolderAsync(
        string recoveryDirectory,
        IList<AudioTrackRipInfo> rippedTrackInfos,
        string par2Path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveryDirectory);
        ArgumentNullException.ThrowIfNull(rippedTrackInfos);
        ArgumentException.ThrowIfNullOrWhiteSpace(par2Path);

        string manifestPath = Path.Combine(recoveryDirectory, AudioRecoveryManifest.ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return new AudioRecoveryRepairResult
            {
                RecoverySessionFound = false,
                Summary = $"Recovery manifest '{AudioRecoveryManifest.ManifestFileName}' was not found."
            };
        }

        string json = await File.ReadAllTextAsync(manifestPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize<AudioRecoveryManifest>(json, JsonOptions)
            ?? throw new InvalidDataException("Invalid audio recovery manifest JSON.");

        string tempWork = Path.Combine(Path.GetTempPath(), $"ignis_rec_fix_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempWork);
        try
        {
            string stageRecoveryDir = Path.Combine(tempWork, ArchiveManifest.RecoveryFolderName);
            Directory.CreateDirectory(stageRecoveryDir);
            foreach (string file in Directory.EnumerateFiles(recoveryDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                File.Copy(file, Path.Combine(stageRecoveryDir, Path.GetFileName(file)), overwrite: true);
            }

            return await VerifyAndRepairCoreAsync(
                manifest,
                stageRecoveryDir,
                tempWork,
                par2Path,
                rippedTrackInfos,
                missingTrackLoader: null,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempWork))
                {
                    Directory.Delete(tempWork, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private static async Task<AudioRecoveryRepairResult> VerifyAndRepairCoreAsync(
        AudioRecoveryManifest manifest,
        string stageRecoveryDir,
        string stageRoot,
        string par2Path,
        IList<AudioTrackRipInfo> rippedTrackInfos,
        Func<AudioRecoveryTrackEntry, CancellationToken, Task<byte[]?>>? missingTrackLoader,
        CancellationToken cancellationToken)
    {
        var rippedByTrack = rippedTrackInfos.ToDictionary(t => t.TrackNumber);
        string pcmPath = Path.Combine(stageRoot, manifest.PcmFileName);
        var damagedTracks = new List<int>();
        int tracksChecked = 0;

        // 1. Assemble audio.pcm from the ripped WAVs (reading un-ripped tracks from disc if partial rip) and verify SHA-256.
        await using (var pcmStream = new FileStream(pcmPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 65536, useAsync: false))
        {
            pcmStream.SetLength(manifest.TotalPcmBytes);
            foreach (var entry in manifest.Tracks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[]? trackPcm = null;
                bool isRippedTrack = rippedByTrack.TryGetValue(entry.TrackNumber, out var ripInfo);
                if (isRippedTrack && ripInfo != null && File.Exists(ripInfo.FilePath))
                {
                    trackPcm = ReadWavPcmSlice(ripInfo.FilePath, entry.ByteLength);
                    tracksChecked++;
                }
                else if (missingTrackLoader != null)
                {
                    trackPcm = await missingTrackLoader(entry, cancellationToken).ConfigureAwait(false);
                }

                trackPcm ??= new byte[entry.ByteLength];
                if (trackPcm.Length != entry.ByteLength)
                {
                    Array.Resize(ref trackPcm, (int)entry.ByteLength);
                }

                pcmStream.Position = entry.ByteOffset;
                pcmStream.Write(trackPcm, 0, trackPcm.Length);

                string actualHash = ComputeSha256Hex(trackPcm);
                if (isRippedTrack && (!string.Equals(actualHash, entry.Sha256Hex, StringComparison.OrdinalIgnoreCase)
                    || ripInfo!.UnreadableSectors > 0))
                {
                    damagedTracks.Add(entry.TrackNumber);
                }
            }
        }

        if (damagedTracks.Count == 0)
        {
            return new AudioRecoveryRepairResult
            {
                RecoverySessionFound = true,
                RepairAttempted = false,
                AllTracksVerified = true,
                TracksChecked = tracksChecked,
                DamagedTracks = Array.Empty<int>(),
                RepairedTracks = Array.Empty<int>(),
                FailedTracks = Array.Empty<int>(),
                Summary = $"Recovery check passed: all {tracksChecked} track(s) match session 2 SHA-256 checksums."
            };
        }

        // 2. Run PAR2 repair over audio.pcm inside stageRoot (Par2Runner reads stageRoot/RECOVERY/recovery.par2).
        var par2Result = await Par2Runner.RepairAsync(par2Path, stageRoot, cancellationToken).ConfigureAwait(false);
        foreach (string backup in par2Result.BackupFiles)
        {
            try { File.Delete(backup); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        // 3. Re-verify every damaged track slice and write repaired PCM back into its WAV file.
        var repairedTracks = new List<int>();
        var failedTracks = new List<int>();
        int totalDiscTracks = manifest.Tracks.Count;

        await using (var repairedPcm = new FileStream(pcmPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            foreach (int trackNumber in damagedTracks)
            {
                var entry = manifest.Tracks.First(t => t.TrackNumber == trackNumber);
                byte[] fixedPcm = new byte[entry.ByteLength];
                repairedPcm.Position = entry.ByteOffset;
                int readTotal = 0;
                while (readTotal < fixedPcm.Length)
                {
                    int n = repairedPcm.Read(fixedPcm, readTotal, fixedPcm.Length - readTotal);
                    if (n == 0) break;
                    readTotal += n;
                }

                string postHash = ComputeSha256Hex(fixedPcm);
                if (!string.Equals(postHash, entry.Sha256Hex, StringComparison.OrdinalIgnoreCase))
                {
                    failedTracks.Add(trackNumber);
                    continue;
                }

                if (rippedByTrack.TryGetValue(trackNumber, out var ripInfo))
                {
                    OverwriteWavPcmSlice(ripInfo.FilePath, fixedPcm);
                    UpdateRipInfoAfterRepair(rippedTrackInfos, ripInfo, fixedPcm, totalDiscTracks);
                }
                repairedTracks.Add(trackNumber);
            }
        }

        bool allVerified = failedTracks.Count == 0;
        string summary = allVerified
            ? $"Repaired {repairedTracks.Count} damaged track(s) from session 2 PAR2 recovery data ({string.Join(", ", repairedTracks.Select(n => $"Track {n:D2}"))})."
            : $"PAR2 repair recovered {repairedTracks.Count} track(s) but {failedTracks.Count} track(s) still failed verification (exit {par2Result.ExitCode}): {string.Join(", ", failedTracks.Select(n => $"Track {n:D2}"))}";

        return new AudioRecoveryRepairResult
        {
            RecoverySessionFound = true,
            RepairAttempted = true,
            AllTracksVerified = allVerified,
            TracksChecked = tracksChecked,
            DamagedTracks = damagedTracks,
            RepairedTracks = repairedTracks,
            FailedTracks = failedTracks,
            Summary = summary
        };
    }

    private static long CapHelpfulPar2Budget(long totalPcmBytes, long rawFreeSectors)
    {
        if (rawFreeSectors <= 0 || totalPcmBytes <= 0)
        {
            return 0;
        }

        // At most ~150% of the audio byte size (+ small overhead for short tracks) is needed for full recovery even
        // when session 2 itself absorbs moderate damage; full CDs hit rawFreeSectors first and fill the whole disc.
        long helpfulMaxSectors = Math.Max(600, (long)Math.Ceiling(totalPcmBytes * 1.5 / OpticalConstants.DataSectorSize) + 256);
        return Math.Min(rawFreeSectors, helpfulMaxSectors);
    }

    private static byte[] ReadWavPcmSlice(string wavPath, long expectedPcmBytes)
    {
        using var fs = new FileStream(wavPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (fs.Length <= 44)
        {
            return new byte[expectedPcmBytes];
        }

        fs.Position = 44;
        long available = fs.Length - 44;
        int toRead = (int)Math.Min(expectedPcmBytes, available);
        byte[] buffer = new byte[expectedPcmBytes];
        int total = 0;
        while (total < toRead)
        {
            int n = fs.Read(buffer, total, toRead - total);
            if (n == 0) break;
            total += n;
        }
        return buffer;
    }

    private static void OverwriteWavPcmSlice(string wavPath, byte[] pcmData)
    {
        using var fs = new FileStream(wavPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        long targetPcmLen = Math.Max(pcmData.Length, Math.Max(0, fs.Length - 44));
        byte[] header = WavFileCodec.CreateWavHeader(
            (int)targetPcmLen,
            OpticalConstants.CdAudioSampleRate,
            OpticalConstants.CdAudioChannels,
            OpticalConstants.CdAudioBitsPerSample);
        fs.Position = 0;
        fs.Write(header, 0, header.Length);
        fs.Write(pcmData, 0, pcmData.Length);
    }

    private static void UpdateRipInfoAfterRepair(
        IList<AudioTrackRipInfo> rippedTrackInfos,
        AudioTrackRipInfo existing,
        byte[] fixedPcm,
        int totalDiscTracks)
    {
        var crc = new Crc32Algorithm();
        crc.Append(fixedPcm);

        var ar = new AccurateRipChecksum(
            fixedPcm.Length / 4,
            isFirstTrack: existing.AudioTrackIndex == 0,
            isLastTrack: existing.AudioTrackIndex == totalDiscTracks - 1);
        ar.Append(fixedPcm);

        double? rgGain = existing.ReplayGainTrackGain;
        double? rgPeak = existing.ReplayGainTrackPeak;
        if (File.Exists(existing.FilePath))
        {
            try
            {
                using var s = new FileStream(existing.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var rg = ReplayGainCalculator.CalculateTrackReplayGain(s);
                rgGain = rg.GainDb;
                rgPeak = rg.PeakAmplitude;
            }
            catch
            {
            }
        }

        int idx = rippedTrackInfos.IndexOf(existing);
        if (idx >= 0)
        {
            rippedTrackInfos[idx] = existing with
            {
                Crc32 = crc.GetCurrentHashAsUInt32(),
                AccurateRipV1 = ar.V1,
                AccurateRipV2 = ar.V2,
                UnreadableSectors = 0,
                SuspectSectors = 0,
                ReplayGainTrackGain = rgGain,
                ReplayGainTrackPeak = rgPeak
            };
        }
    }

    private static async Task<byte[]?> ReadDiscTrackPcmAsync(
        IDiscRecorderBackend recorder,
        IBurnEngineBackend engine,
        IReadOnlyList<AudioDiscoveredTrack> allDiscAudioTracks,
        int trackNumber,
        int readOffsetSamples,
        CancellationToken cancellationToken)
    {
        var track = allDiscAudioTracks.FirstOrDefault(t => t.TrackNumber == trackNumber);
        if (track == null || track.SectorCount <= 0)
        {
            return null;
        }

        int audioEndLba = allDiscAudioTracks.Max(t => t.StartLba + t.SectorCount);
        long firstByte = (long)track.StartLba * OpticalConstants.RawSectorSize + (long)readOffsetSamples * 4;
        long totalBytes = (long)track.SectorCount * OpticalConstants.RawSectorSize;
        int firstSector = (int)Math.Floor(firstByte / (double)OpticalConstants.RawSectorSize);
        int endSector = (int)Math.Ceiling((firstByte + totalBytes) / (double)OpticalConstants.RawSectorSize);
        int readStart = Math.Max(firstSector, 0);
        int readEnd = Math.Min(endSector, audioEndLba);
        int bytesToSkip = (int)(firstByte - (long)firstSector * OpticalConstants.RawSectorSize);

        using var ms = new MemoryStream((int)totalBytes);
        byte[] silence = new byte[OpticalConstants.RawSectorSize];

        void Emit(ReadOnlySpan<byte> sector)
        {
            if (bytesToSkip > 0)
            {
                int skip = Math.Min(bytesToSkip, sector.Length);
                sector = sector[skip..];
                bytesToSkip -= skip;
            }
            long rem = totalBytes - ms.Length;
            if (rem <= 0 || sector.IsEmpty) return;
            if (sector.Length > rem) sector = sector[..(int)rem];
            ms.Write(sector);
        }

        for (int s = firstSector; s < Math.Min(readStart, endSector); s++)
        {
            Emit(silence);
        }

        if (readEnd > readStart)
        {
            await BatchedSectorReader.ReadRangeAsync(
                engine,
                recorder,
                readStart,
                readEnd - readStart,
                raw2352Mode: true,
                maxRetriesPerSector: 1,
                onSector: (_, data) => { Emit(data.Span); return ValueTask.CompletedTask; },
                onUnreadable: _ => { Emit(silence); return ValueTask.FromResult(true); },
                onProgress: null,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        for (int s = Math.Max(readEnd, readStart); s < endSector; s++)
        {
            Emit(silence);
        }

        return ms.ToArray();
    }

    private static string ComputeSha256Hex(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>
    /// Seekable read-only stream backed by cached 2,048-byte sector reads via <see cref="IBurnEngineBackend"/>.
    /// Zero-fills unreadable sectors so ISO parsing and PAR2 can still read surviving blocks.
    /// </summary>
    private sealed class DiscDataSessionStream : Stream
    {
        private const int ChunkSectors = 32;
        private readonly IDiscRecorderBackend _recorder;
        private readonly IBurnEngineBackend _engine;
        private readonly int _sessionStartLba;
        private readonly int _sessionEndLba;
        private readonly CancellationToken _cancellationToken;
        private readonly Dictionary<int, byte[]> _sectorCache = new();
        private long _length;
        private long _position;

        public DiscDataSessionStream(
            IDiscRecorderBackend recorder,
            IBurnEngineBackend engine,
            int sessionStartLba,
            int sessionSectorCount,
            CancellationToken cancellationToken)
        {
            _recorder = recorder;
            _engine = engine;
            _sessionStartLba = sessionStartLba;
            _sessionEndLba = sessionStartLba + Math.Max(17, sessionSectorCount);
            _cancellationToken = cancellationToken;
            _length = (long)_sessionEndLba * OpticalConstants.DataSectorSize;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set
            {
                ArgumentOutOfRangeException.ThrowIfNegative(value);
                _position = value;
            }
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty || _position >= _length)
            {
                return 0;
            }

            int totalRead = 0;
            while (totalRead < buffer.Length && _position < _length)
            {
                int lba = (int)(_position / OpticalConstants.DataSectorSize);
                int inSector = (int)(_position % OpticalConstants.DataSectorSize);
                byte[] sector = GetOrReadSector(lba);
                int take = (int)Math.Min(Math.Min(buffer.Length - totalRead, OpticalConstants.DataSectorSize - inSector), _length - _position);
                sector.AsSpan(inSector, take).CopyTo(buffer.Slice(totalRead, take));
                _position += take;
                totalRead += take;
            }
            return totalRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long next = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            ArgumentOutOfRangeException.ThrowIfNegative(next);
            _position = next;
            return _position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private byte[] GetOrReadSector(int lba)
        {
            if (_sectorCache.TryGetValue(lba, out var cached))
            {
                return cached;
            }

            // Read a chunk from sessionStartLba onward so IsoImageReader doesn't re-hit the drive sector by sector.
            int batchStart = Math.Max(_sessionStartLba, lba);
            int batchCount = batchStart < _sessionEndLba ? Math.Min(ChunkSectors, _sessionEndLba - batchStart) : 1;
            try
            {
                int maxReadLba = batchStart;
                _engine.ReadSectorsAsync(
                        _recorder,
                        batchStart,
                        batchCount,
                        raw2352Mode: false,
                        sectorConsumer: (sectorLba, data) =>
                        {
                            byte[] copy = data.Length == OpticalConstants.DataSectorSize
                                ? data.ToArray()
                                : data.Span.Slice(0, Math.Min(data.Length, OpticalConstants.DataSectorSize)).ToArray();
                            if (copy.Length < OpticalConstants.DataSectorSize)
                            {
                                Array.Resize(ref copy, OpticalConstants.DataSectorSize);
                            }
                            _sectorCache[sectorLba] = copy;
                            if (sectorLba >= maxReadLba) maxReadLba = sectorLba + 1;
                            return ValueTask.CompletedTask;
                        },
                        progress: null,
                        cancellationToken: _cancellationToken)
                    .ConfigureAwait(false)
                    .GetAwaiter()
                    .GetResult();

                // Extend virtual stream length if PVD gives larger extent than initial estimate, or as we read forward.
                long endBytes = (long)maxReadLba * OpticalConstants.DataSectorSize;
                if (endBytes > _length)
                {
                    _length = endBytes;
                }

                if (_sectorCache.TryGetValue(lba, out var hit))
                {
                    return hit;
                }
            }
            catch
            {
                // Chunk failed (bad sector in session 2 or past end of track): read the requested single sector alone.
            }

            try
            {
                byte[]? single = null;
                _engine.ReadSectorsAsync(
                        _recorder,
                        lba,
                        1,
                        raw2352Mode: false,
                        sectorConsumer: (_, data) =>
                        {
                            single = data.ToArray();
                            return ValueTask.CompletedTask;
                        },
                        progress: null,
                        cancellationToken: _cancellationToken)
                    .ConfigureAwait(false)
                    .GetAwaiter()
                    .GetResult();
                if (single != null && single.Length >= OpticalConstants.DataSectorSize)
                {
                    var trimmed = single.Length == OpticalConstants.DataSectorSize
                        ? single
                        : single.AsSpan(0, OpticalConstants.DataSectorSize).ToArray();
                    _sectorCache[lba] = trimmed;
                    return trimmed;
                }
            }
            catch
            {
            }

            byte[] zeros = new byte[OpticalConstants.DataSectorSize];
            _sectorCache[lba] = zeros;
            return zeros;
        }
    }
}
