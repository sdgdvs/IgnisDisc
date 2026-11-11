using System.Diagnostics;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.Primitives;

namespace CdBurner.Hardware.Simulation;

/// <summary>
/// Virtual burning, erasing, and ripping engine executing deterministically against
/// a <see cref="VirtualDiscRecorder"/> and mounted <see cref="VirtualDiscMedium"/>.
/// </summary>
public class VirtualBurnEngine : IBurnEngineBackend, ISecureAudioReader
{
    /// <summary>
    /// Optional default recorder to use if recorder argument is omitted or null.
    /// </summary>
    public VirtualDiscRecorder? DefaultRecorder { get; set; }

    /// <summary>
    /// When true, <see cref="ReadSectorsAsync"/> throws an <see cref="OpticalReadException"/> upon encountering
    /// an injected bad sector. When false (default), it returns a failed <see cref="RipOperationResult"/>.
    /// </summary>
    public bool ThrowOnReadError { get; set; }

    public VirtualBurnEngine(VirtualDiscRecorder? defaultRecorder = null)
    {
        DefaultRecorder = defaultRecorder;
    }

    /// <summary>
    /// Streams 2048-byte Mode 1 data sectors into the mounted virtual disc medium,
    /// recording session/track TOC metadata and emitting progress snapshots.
    /// </summary>
    public async Task<BurnOperationResult> BurnDataStreamAsync(
        IDiscRecorderBackend recorder,
        Stream imageStream,
        DataBurnOptions options,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(imageStream);
        options ??= new DataBurnOptions();

        var virtualRecorder = (recorder as VirtualDiscRecorder) ?? DefaultRecorder;
        if (virtualRecorder == null)
        {
            return BurnOperationResult.Failed("Recorder backend is not a VirtualDiscRecorder.");
        }

        var medium = virtualRecorder.CurrentMedium;
        if (medium == null)
        {
            return BurnOperationResult.Failed("No media present in recorder.");
        }

        bool overwriteOnly = medium.MediaType is DiscMediaType.DvdPlusRw or DiscMediaType.DvdRam or DiscMediaType.BdRe;
        if (medium.IsFinalized && !(overwriteOnly && options.OverwriteExistingData))
        {
            return BurnOperationResult.Failed("Mounted virtual medium is finalized and cannot be written.");
        }

        long streamLength = imageStream.CanSeek ? (imageStream.Length - imageStream.Position) : -1;
        int estimatedSectors = streamLength >= 0
            ? OpticalConstants.BytesToSectors(streamLength, OpticalConstants.DataSectorSize)
            : 0;

        if (estimatedSectors > medium.FreeSectors)
        {
            return BurnOperationResult.Failed(
                $"Not enough free sectors on media. Required: {estimatedSectors}, Available: {medium.FreeSectors}.");
        }

        var sw = Stopwatch.StartNew();
        double speedMultiplier = options.Speed?.Multiplier > 0 ? options.Speed.Value.Multiplier : 16.0;
        long bytesPerSec = (long)(speedMultiplier * OpticalConstants.CdData1XBytesPerSec);

        void ReportSnapshot(int currentLba, int totalLba, string action, double bufferPct = 100.0)
        {
            if (progress == null) return;
            var elapsed = sw.Elapsed;
            var remaining = currentLba > 0 && totalLba > currentLba
                ? TimeSpan.FromSeconds(elapsed.TotalSeconds / currentLba * (totalLba - currentLba))
                : TimeSpan.Zero;

            progress.Report(new BurnProgressSnapshot
            {
                ElapsedTime = elapsed,
                RemainingTime = remaining,
                CurrentLba = currentLba,
                TotalLba = totalLba,
                BufferLevelPercent = bufferPct,
                CurrentSpeedMultiplier = speedMultiplier,
                CurrentBytesPerSecond = bytesPerSec,
                CurrentActionDescription = action
            });
        }

        // 1. Initializing
        ReportSnapshot(0, estimatedSectors, "Initializing");
        cancellationToken.ThrowIfCancellationRequested();

        // 2. Calibrating laser power
        ReportSnapshot(0, estimatedSectors, "CalibratingPower");
        cancellationToken.ThrowIfCancellationRequested();

        // 3. Writing Lead-In
        ReportSnapshot(0, estimatedSectors, "WritingLeadIn");
        cancellationToken.ThrowIfCancellationRequested();

        // Like IMAPI's ForceOverwrite: replacing an overwrite-only disc's data writes from sector 0.
        bool replacesFromStart = options.OverwriteExistingData && overwriteOnly;
        int startLba = medium.IsBlank || replacesFromStart ? 0 : (medium.NextWritableLba > 0 ? medium.NextWritableLba : medium.UsedSectors);
        byte[] sectorBuffer = new byte[OpticalConstants.DataSectorSize];
        int sectorsWritten = 0;

        // 4. Writing Data
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int bytesRead = 0;
            while (bytesRead < OpticalConstants.DataSectorSize)
            {
                int read = await imageStream.ReadAsync(
                    sectorBuffer.AsMemory(bytesRead, OpticalConstants.DataSectorSize - bytesRead),
                    cancellationToken);
                if (read == 0) break;
                bytesRead += read;
            }

            if (bytesRead == 0)
            {
                break; // End of stream
            }

            if (bytesRead < OpticalConstants.DataSectorSize)
            {
                Array.Clear(sectorBuffer, bytesRead, OpticalConstants.DataSectorSize - bytesRead);
            }

            int currentLba = startLba + sectorsWritten;
            if (!options.Simulate)
            {
                medium.WriteSector(currentLba, sectorBuffer, OpticalConstants.DataSectorSize);
            }

            sectorsWritten++;
            int totalProgressLba = Math.Max(estimatedSectors, sectorsWritten);
            ReportSnapshot(sectorsWritten, totalProgressLba, "WritingData");
        }

        int finalTotalLba = Math.Max(estimatedSectors, sectorsWritten);

        // 5. Writing Lead-Out
        ReportSnapshot(finalTotalLba, finalTotalLba, "WritingLeadOut");
        cancellationToken.ThrowIfCancellationRequested();

        if (!options.Simulate && sectorsWritten > 0)
        {
            if (replacesFromStart)
            {
                medium.Sessions.Clear(); // written over from sector 0: the old volume is gone
            }
            var session = new VirtualSession
            {
                SessionNumber = medium.Sessions.Count + 1,
                StartLba = startLba,
                SectorCount = sectorsWritten
            };
            session.Tracks.Add(new VirtualTrack
            {
                TrackNumber = 1,
                StartLba = startLba,
                SectorCount = sectorsWritten,
                IsAudio = false
            });
            medium.Sessions.Add(session);

            if (options.CloseDisc && !overwriteOnly) // a DVD+RW or DVD-RAM can always be written over
            {
                medium.IsFinalized = true;
            }
            else
            {
                medium.IsFinalized = false;
            }

            medium.NextWritableLba = startLba + sectorsWritten;
            if (startLba + sectorsWritten > medium.UsedSectors)
            {
                medium.UsedSectors = startLba + sectorsWritten;
            }
        }

        // 6. Completed
        ReportSnapshot(finalTotalLba, finalTotalLba, "Completed", bufferPct: 0.0);

        if (options.EjectWhenDone)
        {
            virtualRecorder.EjectMedia();
        }

        return BurnOperationResult.Succeeded(sectorsWritten, sw.Elapsed, speedMultiplier);
    }

    /// <summary>
    /// Writes Red Book 2352-byte audio frames for each track, populating track start/length TOC entries
    /// and optional CD-Text pack bytes.
    /// </summary>
    public async Task<BurnOperationResult> BurnAudioTracksAsync(
        IDiscRecorderBackend recorder,
        IReadOnlyList<AudioTrackSource> tracks,
        AudioBurnOptions options,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        options ??= new AudioBurnOptions();

        if (tracks.Count == 0)
        {
            return BurnOperationResult.Failed("No audio tracks provided.");
        }

        var virtualRecorder = (recorder as VirtualDiscRecorder) ?? DefaultRecorder;
        if (virtualRecorder == null)
        {
            return BurnOperationResult.Failed("Recorder backend is not a VirtualDiscRecorder.");
        }

        var medium = virtualRecorder.CurrentMedium;
        if (medium == null)
        {
            return BurnOperationResult.Failed("No media present in recorder.");
        }

        if (medium.IsFinalized)
        {
            return BurnOperationResult.Failed("Mounted virtual medium is finalized.");
        }

        // Calculate total required sectors
        int totalAudioSectors = 0;
        var trackSectorCounts = new int[tracks.Count];
        for (int i = 0; i < tracks.Count; i++)
        {
            var trk = tracks[i];
            int trkSectors;
            if (trk.TotalPcmBytes > 0)
            {
                trkSectors = OpticalConstants.BytesToSectors(trk.TotalPcmBytes, OpticalConstants.RawSectorSize);
            }
            else if (trk.PcmStream != null && trk.PcmStream.CanSeek && trk.PcmStream.Length > 0)
            {
                trkSectors = OpticalConstants.BytesToSectors(trk.PcmStream.Length, OpticalConstants.RawSectorSize);
            }
            else if (!string.IsNullOrEmpty(trk.FilePath) && File.Exists(trk.FilePath))
            {
                trkSectors = OpticalConstants.BytesToSectors(new FileInfo(trk.FilePath).Length, OpticalConstants.RawSectorSize);
            }
            else if (trk.Duration > TimeSpan.Zero)
            {
                trkSectors = (int)(trk.Duration.TotalSeconds * OpticalConstants.AudioFramesPerSecond);
            }
            else
            {
                trkSectors = OpticalConstants.CdLeadInFrames; // Minimum 150 frames (2.0s)
            }

            trackSectorCounts[i] = trkSectors;
            totalAudioSectors += trkSectors;
        }

        if (totalAudioSectors > medium.FreeSectors)
        {
            return BurnOperationResult.Failed(
                $"Not enough free sectors for audio tracks. Required: {totalAudioSectors}, Available: {medium.FreeSectors}.");
        }

        var sw = Stopwatch.StartNew();
        double speedMultiplier = options.Speed?.Multiplier > 0 ? options.Speed.Value.Multiplier : 16.0;
        long bytesPerSec = (long)(speedMultiplier * OpticalConstants.CdAudio1XBytesPerSec);

        void ReportSnapshot(int currentLba, int totalLba, string action, double bufferPct = 100.0)
        {
            if (progress == null) return;
            var elapsed = sw.Elapsed;
            var remaining = currentLba > 0 && totalLba > currentLba
                ? TimeSpan.FromSeconds(elapsed.TotalSeconds / currentLba * (totalLba - currentLba))
                : TimeSpan.Zero;

            progress.Report(new BurnProgressSnapshot
            {
                ElapsedTime = elapsed,
                RemainingTime = remaining,
                CurrentLba = currentLba,
                TotalLba = totalLba,
                BufferLevelPercent = bufferPct,
                CurrentSpeedMultiplier = speedMultiplier,
                CurrentBytesPerSecond = bytesPerSec,
                CurrentActionDescription = action
            });
        }

        ReportSnapshot(0, totalAudioSectors, "Initializing");
        cancellationToken.ThrowIfCancellationRequested();

        ReportSnapshot(0, totalAudioSectors, "CalibratingPower");
        cancellationToken.ThrowIfCancellationRequested();

        ReportSnapshot(0, totalAudioSectors, "WritingLeadIn");
        cancellationToken.ThrowIfCancellationRequested();

        if (!options.Simulate)
        {
            medium.Raw2352Mode = true;
        }
        int sessionStartLba = medium.IsBlank ? 0 : medium.UsedSectors;
        int currentLba = sessionStartLba;
        int sectorsWritten = 0;

        var session = new VirtualSession
        {
            SessionNumber = medium.Sessions.Count + 1,
            StartLba = sessionStartLba
        };

        byte[] frameBuffer = new byte[OpticalConstants.RawSectorSize];

        for (int i = 0; i < tracks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var trk = tracks[i];
            int trkSectors = trackSectorCounts[i];
            int trkStartLba = currentLba;

            Stream? trackStream = null;
            bool shouldDisposeStream = true;
            if (trk.PcmStreamFactory != null)
            {
                trackStream = trk.PcmStreamFactory();
            }
            else if (trk.PcmStream != null)
            {
                trackStream = trk.PcmStream;
                shouldDisposeStream = false;
                if (trackStream.CanSeek)
                {
                    trackStream.Position = 0;
                }
            }
            else if (!string.IsNullOrEmpty(trk.FilePath) && File.Exists(trk.FilePath))
            {
                trackStream = File.OpenRead(trk.FilePath);
            }

            try
            {
                for (int s = 0; s < trkSectors; s++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (trackStream != null)
                    {
                        int bytesRead = 0;
                        while (bytesRead < OpticalConstants.RawSectorSize)
                        {
                            int read = await trackStream.ReadAsync(
                                frameBuffer.AsMemory(bytesRead, OpticalConstants.RawSectorSize - bytesRead),
                                cancellationToken);
                            if (read == 0) break;
                            bytesRead += read;
                        }
                        if (bytesRead < OpticalConstants.RawSectorSize)
                        {
                            Array.Clear(frameBuffer, bytesRead, OpticalConstants.RawSectorSize - bytesRead);
                        }
                    }
                    else
                    {
                        // Generate silence/dummy audio data
                        Array.Clear(frameBuffer, 0, OpticalConstants.RawSectorSize);
                    }

                    if (!options.Simulate)
                    {
                        medium.WriteSector(currentLba, frameBuffer, OpticalConstants.RawSectorSize);
                    }

                    currentLba++;
                    sectorsWritten++;
                    ReportSnapshot(sectorsWritten, totalAudioSectors, $"WritingAudioTrack {trk.TrackNumber}");
                }
            }
            finally
            {
                if (shouldDisposeStream && trackStream != null)
                {
                    await trackStream.DisposeAsync();
                }
            }

            session.Tracks.Add(new VirtualTrack
            {
                TrackNumber = trk.TrackNumber > 0 ? trk.TrackNumber : i + 1,
                StartLba = trkStartLba,
                SectorCount = trkSectors,
                IsAudio = true,
                PreGapFrames = trk.PreGapFrames,
                Title = trk.Title,
                Performer = trk.Performer,
                Songwriter = trk.Songwriter,
                Composer = trk.Composer,
                Isrc = trk.Isrc
            });
        }

        session.SectorCount = sectorsWritten;

        // Writing Lead-Out
        ReportSnapshot(totalAudioSectors, totalAudioSectors, "WritingLeadOut");

        if (!options.Simulate)
        {
            medium.Sessions.Add(session);

            if (options.WriteCdText)
            {
                if (options.CdTextBytes != null && options.CdTextBytes.Length > 0)
                {
                    medium.CdTextBytes = options.CdTextBytes.ToArray();
                }
                else if (medium.CdTextBytes == null)
                {
                    var albumMeta = new CdTextAlbumMetadata(
                        AlbumTitle: "Virtual Audio CD",
                        AlbumPerformer: tracks.FirstOrDefault(t => !string.IsNullOrEmpty(t.Performer))?.Performer ?? "Artist",
                        Tracks: tracks.Select(t => new CdTextTrackMetadata(
                            TrackNumber: t.TrackNumber > 0 ? t.TrackNumber : 1,
                            Title: t.Title ?? $"Track {t.TrackNumber}",
                            Performer: t.Performer ?? "")).ToList());
                    medium.CdTextBytes = CdTextPackBuilder.BuildPacks(albumMeta);
                }
            }

            if (options.UseDiscAtOnce)
            {
                medium.IsFinalized = true;
            }
        }

        // Completed
        ReportSnapshot(totalAudioSectors, totalAudioSectors, "Completed", bufferPct: 0.0);

        if (options.EjectWhenDone)
        {
            virtualRecorder.EjectMedia();
        }

        return BurnOperationResult.Succeeded(sectorsWritten, sw.Elapsed, speedMultiplier);
    }

    /// <summary>
    /// Erases rewritable media (CD-RW, DVD-RW, DVD+RW, DVD-RAM), clearing sessions and restoring blank status.
    /// </summary>
    public async Task<BurnOperationResult> EraseMediaAsync(
        IDiscRecorderBackend recorder,
        bool fullErase,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        var virtualRecorder = (recorder as VirtualDiscRecorder) ?? DefaultRecorder;
        if (virtualRecorder == null)
        {
            return BurnOperationResult.Failed("Recorder backend is not a VirtualDiscRecorder.");
        }

        var medium = virtualRecorder.CurrentMedium;
        if (medium == null)
        {
            return BurnOperationResult.Failed("No media present in recorder.");
        }

        if (!medium.IsRewritable)
        {
            return BurnOperationResult.Failed($"Media type '{medium.MediaType.GetDisplayName()}' is not rewritable.");
        }

        var sw = Stopwatch.StartNew();

        void ReportSnapshot(int current, int total, string action)
        {
            progress?.Report(new BurnProgressSnapshot
            {
                ElapsedTime = sw.Elapsed,
                RemainingTime = TimeSpan.Zero,
                CurrentLba = current,
                TotalLba = total,
                BufferLevelPercent = 100.0,
                CurrentSpeedMultiplier = 24.0,
                CurrentBytesPerSecond = 24 * OpticalConstants.CdData1XBytesPerSec,
                CurrentActionDescription = action
            });
        }

        ReportSnapshot(0, 100, "Initializing");
        cancellationToken.ThrowIfCancellationRequested();

        ReportSnapshot(50, 100, fullErase ? "FullErasing" : "QuickErasing");

        await Task.Run(() => medium.Erase(fullErase), cancellationToken);

        ReportSnapshot(100, 100, "Completed");

        return BurnOperationResult.Succeeded(0, sw.Elapsed);
    }

    /// <summary>
    /// Reads contiguous sectors from the mounted virtual disc medium, invoking <paramref name="sectorConsumer"/>
    /// for each valid sector, or reporting/throwing an <see cref="OpticalReadException"/> if an LBA is in <see cref="VirtualDiscMedium.SimulatedBadLbas"/>.
    /// </summary>
    public async Task<RipOperationResult> ReadSectorsAsync(
        IDiscRecorderBackend recorder,
        int startLba,
        int sectorCount,
        bool raw2352Mode,
        Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sectorConsumer);

        var virtualRecorder = (recorder as VirtualDiscRecorder) ?? DefaultRecorder;
        if (virtualRecorder == null)
        {
            return RipOperationResult.Failed("Recorder backend is not a VirtualDiscRecorder.");
        }

        var medium = virtualRecorder.CurrentMedium;
        if (medium == null)
        {
            return RipOperationResult.Failed("No media present in recorder.");
        }

        if (startLba < 0 || sectorCount < 0 || (long)startLba + sectorCount > medium.TotalCapacitySectors)
        {
            return RipOperationResult.Failed(
                $"Sector range [{startLba}..{(long)startLba + sectorCount - 1}] is out of bounds for medium capacity ({medium.TotalCapacitySectors}).");
        }

        int sz = raw2352Mode ? OpticalConstants.RawSectorSize : OpticalConstants.DataSectorSize;
        var sw = Stopwatch.StartNew();
        int readCount = 0;
        var badSectors = new List<int>();

        for (int i = 0; i < sectorCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int currentLba = startLba + i;

            if (medium.SimulatedBadLbas.Contains(currentLba))
            {
                badSectors.Add(currentLba);
                var badEx = new OpticalReadException(currentLba, $"Unrecovered read error at LBA {currentLba} (Sense: 03/11/00)");

                if (ThrowOnReadError)
                {
                    throw badEx;
                }

                return new RipOperationResult
                {
                    Success = false,
                    ErrorMessage = badEx.Message,
                    SectorsRead = readCount,
                    BadSectorsCount = badSectors.Count,
                    BadSectorsList = badSectors.ToArray(),
                    TotalTime = sw.Elapsed,
                    Exception = badEx
                };
            }

            byte[] sectorBuffer = new byte[sz];
            medium.ReadSector(currentLba, sectorBuffer, sz);

            await sectorConsumer(currentLba, new ReadOnlyMemory<byte>(sectorBuffer));
            readCount++;

            progress?.Report(new BurnProgressSnapshot
            {
                ElapsedTime = sw.Elapsed,
                CurrentLba = readCount,
                TotalLba = sectorCount,
                BufferLevelPercent = 100.0,
                CurrentSpeedMultiplier = 24.0,
                CurrentBytesPerSecond = 24 * OpticalConstants.CdData1XBytesPerSec,
                CurrentActionDescription = "ReadingSectors"
            });
        }

        progress?.Report(new BurnProgressSnapshot
        {
            ElapsedTime = sw.Elapsed,
            CurrentLba = sectorCount,
            TotalLba = sectorCount,
            BufferLevelPercent = 0.0,
            CurrentSpeedMultiplier = 24.0,
            CurrentBytesPerSecond = 24 * OpticalConstants.CdData1XBytesPerSec,
            CurrentActionDescription = "Completed"
        });

        return RipOperationResult.Succeeded(readCount, sw.Elapsed, badSectors);
    }

    /// <summary>Number of times <see cref="FlushReadCacheAsync"/> was called (for tests).</summary>
    public int CacheFlushCount { get; private set; }

    /// <summary>Number of bytes reported as C2-damaged for each damaged simulated read.</summary>
    public const int SimulatedC2BytesPerDamagedSector = 16;

    /// <inheritdoc/>
    public bool SupportsC2ErrorPointers(IDiscRecorderBackend recorder, int probeLba) => true;

    /// <inheritdoc/>
    /// <remarks>
    /// Sectors in <see cref="VirtualDiscMedium.SimulatedBadLbas"/> stop the read; sectors in
    /// <see cref="VirtualDiscMedium.SimulatedC2Errors"/> return data with the first
    /// <see cref="SimulatedC2BytesPerDamagedSector"/> bytes inverted and that many C2 bytes, until their count runs out.
    /// </remarks>
    public async Task<RipOperationResult> ReadAudioSectorsWithC2Async(
        IDiscRecorderBackend recorder,
        int startLba,
        int sectorCount,
        Func<int, ReadOnlyMemory<byte>, int, ValueTask> sectorConsumer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sectorConsumer);

        var medium = ((recorder as VirtualDiscRecorder) ?? DefaultRecorder)?.CurrentMedium;
        if (medium == null)
        {
            return RipOperationResult.Failed("No media present in recorder.");
        }

        if (startLba < 0 || sectorCount < 0 || (long)startLba + sectorCount > medium.TotalCapacitySectors)
        {
            return RipOperationResult.Failed($"Sector range starting at {startLba} is out of bounds.");
        }

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < sectorCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int lba = startLba + i;

            if (medium.SimulatedBadLbas.Contains(lba))
            {
                return new RipOperationResult
                {
                    Success = false,
                    ErrorMessage = $"Unrecovered read error at LBA {lba} (Sense: 03/11/00)",
                    SectorsRead = i,
                    BadSectorsCount = 1,
                    BadSectorsList = new[] { lba },
                    TotalTime = sw.Elapsed
                };
            }

            byte[] sector = new byte[OpticalConstants.RawSectorSize];
            medium.ReadSector(lba, sector, OpticalConstants.RawSectorSize);

            int c2 = 0;
            if (medium.SimulatedC2Errors.TryGetValue(lba, out int remaining) && remaining > 0)
            {
                for (int b = 0; b < SimulatedC2BytesPerDamagedSector; b++)
                {
                    sector[b] ^= 0xFF;
                }
                c2 = SimulatedC2BytesPerDamagedSector;
                if (remaining != int.MaxValue)
                {
                    medium.SimulatedC2Errors[lba] = remaining - 1;
                }
            }

            await sectorConsumer(lba, sector, c2).ConfigureAwait(false);
        }

        return RipOperationResult.Succeeded(sectorCount, sw.Elapsed);
    }

    /// <inheritdoc/>
    public Task FlushReadCacheAsync(IDiscRecorderBackend recorder, int nearLba, int audioEndLba, CancellationToken cancellationToken)
    {
        CacheFlushCount++;
        return Task.CompletedTask;
    }
}
