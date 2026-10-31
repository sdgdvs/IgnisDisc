using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Compilation;
using CdBurner.Core.Images;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// Unit tests for Phase 12: Disc Ripper & Post-Burn Sector/Checksum Verifier.
/// Verifies DiscRipperService (ripping Data discs to .iso and Raw/Mixed/Audio to .bin + .cue with bad-sector retry and defect map logging)
/// and DiscVerificationService (comparing physical/virtual disc sector-by-sector against .iso/.bin image with CRC32, MD5, SHA-256 and LBA file mapping).
/// </summary>
public class Phase12_RipAndVerifyTests
{
    private sealed class ThreadSafeListProgress<T> : IProgress<T>
    {
        private readonly object _lock = new();
        public List<T> Items { get; } = new();

        public void Report(T value)
        {
            lock (_lock)
            {
                Items.Add(value);
            }
        }
    }

    private sealed class TrackingBurnEngine : IBurnEngineBackend
    {
        private readonly IBurnEngineBackend _inner;
        private readonly ConcurrentDictionary<int, int> _readAttemptsByLba = new();

        public TrackingBurnEngine(IBurnEngineBackend inner)
        {
            _inner = inner;
        }

        public int GetReadAttemptsForLba(int lba) => _readAttemptsByLba.TryGetValue(lba, out var count) ? count : 0;

        public Task<BurnOperationResult> BurnDataStreamAsync(IDiscRecorderBackend recorder, Stream imageStream, DataBurnOptions options, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken)
            => _inner.BurnDataStreamAsync(recorder, imageStream, options, progress, cancellationToken);

        public Task<BurnOperationResult> BurnAudioTracksAsync(IDiscRecorderBackend recorder, IReadOnlyList<AudioTrackSource> tracks, AudioBurnOptions options, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken)
            => _inner.BurnAudioTracksAsync(recorder, tracks, options, progress, cancellationToken);

        public Task<BurnOperationResult> EraseMediaAsync(IDiscRecorderBackend recorder, bool fullErase, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken)
            => _inner.EraseMediaAsync(recorder, fullErase, progress, cancellationToken);

        public async Task<RipOperationResult> ReadSectorsAsync(IDiscRecorderBackend recorder, int startLba, int sectorCount, bool raw2352Mode, Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken)
        {
            // Count an attempt for every sector the engine actually reached: each delivered sector plus the one it
            // stopped on. Counting the whole requested range would over-count sectors behind a bad LBA in a batch.
            int delivered = 0;
            void Count(int lba) => _readAttemptsByLba.AddOrUpdate(lba, 1, (_, current) => current + 1);

            RipOperationResult result;
            try
            {
                result = await _inner.ReadSectorsAsync(recorder, startLba, sectorCount, raw2352Mode, async (lba, data) =>
                {
                    Count(lba);
                    delivered++;
                    await sectorConsumer(lba, data);
                }, progress, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Count(startLba + delivered);
                throw;
            }

            if (!result.Success && delivered < sectorCount)
            {
                Count(startLba + delivered);
            }

            return result;
        }
    }

    private static (CompilationDirectory Root, byte[] AppBinBytes, byte[] ReadmeBytes) CreateStandardMultiFileCompilation()
    {
        var root = CompilationDirectory.CreateRoot();

        // /README.TXT
        byte[] readmeBytes = Encoding.UTF8.GetBytes("CdBurner Autonomous Engine Phase 12 - Master Verification Readme.\r\nLine 2 for validation.");
        root.AddFile(new CompilationFile("README.TXT", () => new MemoryStream(readmeBytes)));

        // /Docs/Manual.txt
        byte[] manualBytes = Encoding.UTF8.GetBytes("CdBurner Optical Architecture & Post-Burn Sector Verifier Manual.");
        var docsDir = root.AddDirectory("Docs");
        docsDir.AddFile(new CompilationFile("Manual.txt", () => new MemoryStream(manualBytes)));

        // /Bin/app.bin (4096 bytes = exactly 2 full Mode 1 sectors)
        byte[] appBinBytes = Enumerable.Range(0, 4096).Select(i => (byte)((i * 37 + 11) % 256)).ToArray();
        var binDir = root.AddDirectory("Bin");
        binDir.AddFile(new CompilationFile("app.bin", () => new MemoryStream(appBinBytes)));

        return (root, appBinBytes, readmeBytes);
    }

    #region Test 1: RipToIsoAsync - Multi-file Burn and Rip Roundtrip

    [Fact]
    public async Task RipToIsoAsync_BurnMultiFileIsoToVirtualRecorder_RipsIdenticalByteLengthAndSha256Digest()
    {
        // 1. Build multi-file ISO image in memory
        var (root, _, _) = CreateStandardMultiFileCompilation();
        var isoBuildOptions = new IsoBuildOptions(
            VolumeLabel: "PHASE12_ISO",
            IncludeIso9660: true,
            IncludeJoliet: true,
            IncludeUdf: true);

        using var originalIsoStream = IsoUdfImageBuilder.BuildToMemoryStream(root, isoBuildOptions);
        byte[] originalIsoBytes = originalIsoStream.ToArray();
        string expectedSha256 = Convert.ToHexString(SHA256.HashData(originalIsoBytes));
        Assert.True(originalIsoBytes.Length > 0);
        Assert.Equal(0, originalIsoBytes.Length % OpticalConstants.DataSectorSize);

        // 2. Burn ISO image to VirtualDiscRecorder
        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        var engine = new VirtualBurnEngine();
        originalIsoStream.Position = 0;
        var burnResult = await engine.BurnDataStreamAsync(
            recorder,
            originalIsoStream,
            new DataBurnOptions { VolumeLabel = "PHASE12_ISO", CloseDisc = true },
            progress: null,
            CancellationToken.None);

        Assert.True(burnResult.Success, $"Burn failed: {burnResult.ErrorMessage}");

        // 3. Rip disc back via DiscRipperService.RipToIsoAsync to MemoryStream
        var ripper = new DiscRipperService();
        using var rippedIsoStream = new MemoryStream();
        var ripProgress = new ThreadSafeListProgress<BurnProgressSnapshot>();

        var ripResult = await ripper.RipToIsoAsync(
            recorder,
            engine,
            rippedIsoStream,
            options: new RipOptions { MaxRetriesPerSector = 3, ContinueOnUnreadableSector = false },
            progress: ripProgress,
            ct: CancellationToken.None);

        // 4. Verify rip operation succeeded
        Assert.True(ripResult.Success, $"Rip failed: {ripResult.ErrorMessage}");
        Assert.Equal(0, ripResult.BadSectorsCount);
        Assert.Empty(ripResult.BadSectorLbas);

        // 5. Verify identical byte length and SHA-256 digest
        byte[] rippedBytes = rippedIsoStream.ToArray();
        Assert.Equal(originalIsoBytes.Length, rippedBytes.Length);

        string actualSha256 = Convert.ToHexString(SHA256.HashData(rippedBytes));
        Assert.Equal(expectedSha256, actualSha256);

        // 6. Verify progress snapshots were emitted
        Assert.NotEmpty(ripProgress.Items);
        Assert.Equal(100.0, ripProgress.Items.Last().ProgressPercentage);

        // 7. Verify also ripping to a temporary file on disk produces identical bytes
        string tempIsoFile = Path.Combine(Path.GetTempPath(), $"cdburner_phase12_{Guid.NewGuid():N}.iso");
        try
        {
            var fileRipResult = await ripper.RipToIsoAsync(
                recorder,
                engine,
                tempIsoFile,
                options: new RipOptions(),
                progress: null,
                ct: CancellationToken.None);

            Assert.True(fileRipResult.Success);
            byte[] fileRippedBytes = await File.ReadAllBytesAsync(tempIsoFile);
            Assert.Equal(originalIsoBytes.Length, fileRippedBytes.Length);
            Assert.Equal(expectedSha256, Convert.ToHexString(SHA256.HashData(fileRippedBytes)));
        }
        finally
        {
            if (File.Exists(tempIsoFile))
            {
                File.Delete(tempIsoFile);
            }
        }
    }

    #endregion

    #region Test 2: DiscVerificationService - Intact and Corrupted Sector in /Bin/app.bin

    [Fact]
    public async Task DiscVerificationService_PassesOnIntactDisc_AndReportsCorruptedLbaAndFileOnBadSector()
    {
        // 1. Build multi-file ISO image
        var (root, _, _) = CreateStandardMultiFileCompilation();
        var buildOptions = new IsoBuildOptions(
            VolumeLabel: "PHASE12_VERIFY",
            IncludeIso9660: true,
            IncludeJoliet: true,
            IncludeUdf: true);

        using var isoStream = IsoUdfImageBuilder.BuildToMemoryStream(root, buildOptions);
        byte[] isoBytes = isoStream.ToArray();

        // 2. Parse file extents from ISO stream using IsoImageReader
        isoStream.Position = 0;
        using var reader = IsoImageReader.FromStream(isoStream, leaveOpen: true);
        var appBinEntry = reader.FindFile("/Bin/app.bin");
        Assert.NotNull(appBinEntry);
        int appBinStartLba = appBinEntry.StartLba;
        int appBinSectorCount = OpticalConstants.BytesToSectors(appBinEntry.SizeBytes, OpticalConstants.DataSectorSize);
        Assert.Equal(2, appBinSectorCount); // 4096 bytes = 2 sectors

        // Build file extent map
        var fileMap = LbaFileExtentMap.FromIsoReader(reader);
        Assert.Contains(fileMap, f => f.FilePath.Equals("/Bin/app.bin", StringComparison.OrdinalIgnoreCase));

        // 3. Burn to VirtualDiscRecorder
        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        var engine = new VirtualBurnEngine();
        isoStream.Position = 0;
        var burnResult = await engine.BurnDataStreamAsync(recorder, isoStream, new DataBurnOptions { VolumeLabel = "PHASE12_VERIFY", CloseDisc = true }, null, CancellationToken.None);
        Assert.True(burnResult.Success);

        var verifier = new DiscVerificationService();

        // ----------------------------------------------------
        // Part A: Intact disc verification must succeed (IsMatch == true)
        // ----------------------------------------------------
        isoStream.Position = 0;
        var intactProgress = new ThreadSafeListProgress<BurnProgressSnapshot>();
        var intactResult = await verifier.VerifyDiscAgainstStreamAsync(
            recorder,
            engine,
            isoStream,
            raw2352Mode: false,
            fileMap: fileMap,
            progress: intactProgress,
            ct: CancellationToken.None);

        Assert.True(intactResult.IsMatch, $"Intact disc verification failed: {intactResult.ErrorMessage}");
        Assert.True(intactResult.Success);
        Assert.Empty(intactResult.MismatchedLbas);
        Assert.Empty(intactResult.CorruptedFiles);
        Assert.Equal(intactResult.ExpectedSha256, intactResult.ActualSha256);
        Assert.Equal(intactResult.ExpectedMd5, intactResult.ActualMd5);
        Assert.Equal(intactResult.ExpectedCrc32, intactResult.ActualCrc32);
        Assert.NotEmpty(intactProgress.Items);
        Assert.Equal(100.0, intactProgress.Items.Last().ProgressPercentage);

        // ----------------------------------------------------
        // Part B: Simulated bad LBA inside /Bin/app.bin
        // ----------------------------------------------------
        int targetCorruptLba = appBinStartLba + 1; // Second sector of /Bin/app.bin
        medium.SimulatedBadLbas.Add(targetCorruptLba);

        isoStream.Position = 0;
        var badSectorResult = await verifier.VerifyDiscAgainstStreamAsync(
            recorder,
            engine,
            isoStream,
            raw2352Mode: false,
            fileMap: fileMap,
            progress: null,
            ct: CancellationToken.None);

        Assert.False(badSectorResult.IsMatch, "Verification must report IsMatch == false when SimulatedBadLbas is hit.");
        Assert.False(badSectorResult.Success);
        Assert.Contains(targetCorruptLba, badSectorResult.MismatchedLbas);
        Assert.Contains("/Bin/app.bin", badSectorResult.CorruptedFiles);
        Assert.NotEqual(badSectorResult.ExpectedSha256, badSectorResult.ActualSha256);

        // ----------------------------------------------------
        // Part C: Corrupted byte data on disc without SimulatedBadLbas
        // ----------------------------------------------------
        medium.SimulatedBadLbas.Clear();
        byte[] corruptedSectorBytes = new byte[OpticalConstants.DataSectorSize];
        Array.Fill<byte>(corruptedSectorBytes, 0xAA);
        medium.WriteSector(targetCorruptLba, corruptedSectorBytes);

        isoStream.Position = 0;
        var byteCorruptedResult = await verifier.VerifyDiscAgainstStreamAsync(
            recorder,
            engine,
            isoStream,
            raw2352Mode: false,
            fileMap: fileMap,
            progress: null,
            ct: CancellationToken.None);

        Assert.False(byteCorruptedResult.IsMatch, "Verification must report IsMatch == false when sector bytes are corrupted.");
        Assert.Contains(targetCorruptLba, byteCorruptedResult.MismatchedLbas);
        Assert.Contains("/Bin/app.bin", byteCorruptedResult.CorruptedFiles);
        Assert.NotEqual(byteCorruptedResult.ExpectedSha256, byteCorruptedResult.ActualSha256);
        Assert.NotEqual(byteCorruptedResult.ExpectedCrc32, byteCorruptedResult.ActualCrc32);
    }

    [Fact]
    public async Task DiscVerificationService_AutoDetectsIsoFileMap_WhenFileMapArgIsNull()
    {
        // When fileMap is omitted (null), DiscVerificationService should automatically inspect
        // the ISO stream and map corrupted LBAs to their corresponding files.
        var (root, _, _) = CreateStandardMultiFileCompilation();
        using var isoStream = IsoUdfImageBuilder.BuildToMemoryStream(root, new IsoBuildOptions(VolumeLabel: "AUTO_MAP"));

        using var reader = IsoImageReader.FromStream(isoStream, leaveOpen: true);
        var appBin = reader.FindFile("/Bin/app.bin");
        Assert.NotNull(appBin);
        int targetLba = appBin.StartLba;

        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        var engine = new VirtualBurnEngine();
        isoStream.Position = 0;
        await engine.BurnDataStreamAsync(recorder, isoStream, new DataBurnOptions(), null, CancellationToken.None);

        medium.SimulatedBadLbas.Add(targetLba);

        var verifier = new DiscVerificationService();
        isoStream.Position = 0;
        var result = await verifier.VerifyDiscAgainstStreamAsync(
            recorder,
            engine,
            isoStream,
            raw2352Mode: false,
            fileMap: null, // Test auto-detection
            progress: null,
            ct: CancellationToken.None);

        Assert.False(result.IsMatch);
        Assert.Contains(targetLba, result.MismatchedLbas);
        Assert.Contains("/Bin/app.bin", result.CorruptedFiles);
    }

    #endregion

    #region Test 3: DiscRipperService - Bad Sector Retries and Defect Map Logging

    [Fact]
    public async Task DiscRipperService_ContinueOnUnreadableSector_RetriesMaxRetriesAndZeroFillsDefectiveSector()
    {
        // 1. Setup virtual medium with 16 data sectors
        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        byte[] patternSector = new byte[OpticalConstants.DataSectorSize];
        for (int i = 0; i < 16; i++)
        {
            Array.Fill<byte>(patternSector, (byte)(i + 1));
            medium.WriteSector(i, patternSector);
        }

        // 2. Mark LBA 7 as a simulated bad sector
        const int badLba = 7;
        medium.SimulatedBadLbas.Add(badLba);

        // 3. Wrap engine in TrackingBurnEngine to verify exact read attempts on the bad sector
        var baseEngine = new VirtualBurnEngine();
        var trackingEngine = new TrackingBurnEngine(baseEngine);

        var ripper = new DiscRipperService();
        using var outputStream = new MemoryStream();

        const int maxRetries = 3;
        var ripOptions = new RipOptions
        {
            StartLba = 0,
            SectorCount = 16,
            MaxRetriesPerSector = maxRetries,
            ContinueOnUnreadableSector = true
        };

        // 4. Rip sectors
        var ripResult = await ripper.RipToIsoAsync(recorder, trackingEngine, outputStream, ripOptions, null, CancellationToken.None);

        // 5. Verify rip operation succeeded with defect logged
        Assert.True(ripResult.Success);
        Assert.Equal(16, ripResult.SectorsRead);
        Assert.Equal(1, ripResult.BadSectorsCount);
        Assert.Single(ripResult.BadSectorLbas);
        Assert.Contains(badLba, ripResult.BadSectorLbas);

        // 6. Verify exact retry count: 1 initial attempt + 3 retries = 4 total read attempts
        int attemptsOnBadLba = trackingEngine.GetReadAttemptsForLba(badLba);
        Assert.Equal(1 + maxRetries, attemptsOnBadLba);

        // Non-bad sectors should each have been read exactly once
        Assert.Equal(1, trackingEngine.GetReadAttemptsForLba(badLba - 1));
        Assert.Equal(1, trackingEngine.GetReadAttemptsForLba(badLba + 1));

        // 7. Verify zero-fill in destination stream for the bad LBA
        byte[] rippedData = outputStream.ToArray();
        Assert.Equal(16 * OpticalConstants.DataSectorSize, rippedData.Length);

        // Check bad LBA bytes are all 0x00
        byte[] badSectorData = rippedData.AsSpan(badLba * OpticalConstants.DataSectorSize, OpticalConstants.DataSectorSize).ToArray();
        Assert.All(badSectorData, b => Assert.Equal(0, b));

        // Check preceding LBA (6) and succeeding LBA (8) contain valid expected pattern
        byte[] prevSectorData = rippedData.AsSpan((badLba - 1) * OpticalConstants.DataSectorSize, OpticalConstants.DataSectorSize).ToArray();
        Assert.All(prevSectorData, b => Assert.Equal(badLba, b)); // Pattern for LBA 6 was filled with 7 (i+1)

        byte[] nextSectorData = rippedData.AsSpan((badLba + 1) * OpticalConstants.DataSectorSize, OpticalConstants.DataSectorSize).ToArray();
        Assert.All(nextSectorData, b => Assert.Equal(badLba + 2, b)); // Pattern for LBA 8 was filled with 9
    }

    [Fact]
    public async Task DiscRipperService_ContinueOnUnreadableSectorFalse_AbortsOnBadSector()
    {
        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        byte[] pattern = new byte[OpticalConstants.DataSectorSize];
        for (int i = 0; i < 10; i++)
        {
            Array.Fill<byte>(pattern, 0x55);
            medium.WriteSector(i, pattern);
        }

        const int badLba = 4;
        medium.SimulatedBadLbas.Add(badLba);

        var engine = new VirtualBurnEngine();
        var ripper = new DiscRipperService();
        using var outputStream = new MemoryStream();

        var ripOptions = new RipOptions
        {
            StartLba = 0,
            SectorCount = 10,
            MaxRetriesPerSector = 2,
            ContinueOnUnreadableSector = false // Must abort
        };

        var result = await ripper.RipToIsoAsync(recorder, engine, outputStream, ripOptions, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains(badLba, result.BadSectorLbas);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("Unrecovered read error", result.ErrorMessage);
    }

    #endregion

    #region Test 4: RipToBinCueAsync - Companion CUE Sheet & Raw 2352 Extraction

    [Fact]
    public async Task RipToBinCueAsync_ExtractsRawBinAndGeneratesCompanionCueSheet()
    {
        // 1. Prepare Virtual Recorder with an audio track and data track
        using var recorder = new VirtualDiscRecorder();
        using var medium = new VirtualDiscMedium(DiscMediaType.CdRom, 1000, isBlank: false, raw2352Mode: true);
        recorder.InsertMedia(medium);

        // Track 1: Mode1 data (10 sectors = 23,520 bytes)
        // Track 2: Audio track (15 sectors = 35,280 bytes)
        const int track1Sectors = 10;
        const int track2Sectors = 15;
        const int totalSectors = track1Sectors + track2Sectors;

        byte[] rawSector = new byte[OpticalConstants.RawSectorSize];
        for (int i = 0; i < totalSectors; i++)
        {
            Array.Fill<byte>(rawSector, (byte)(i % 128));
            medium.WriteSector(i, rawSector, OpticalConstants.RawSectorSize);
        }

        var session = new VirtualSession { SessionNumber = 1, StartLba = 0, SectorCount = totalSectors };
        session.Tracks.Add(new VirtualTrack
        {
            TrackNumber = 1,
            StartLba = 0,
            SectorCount = track1Sectors,
            IsAudio = false,
            Title = "Game Data"
        });
        session.Tracks.Add(new VirtualTrack
        {
            TrackNumber = 2,
            StartLba = track1Sectors,
            SectorCount = track2Sectors,
            IsAudio = true,
            Title = "Stage 1 Music",
            Performer = "Awesome Band",
            PreGapFrames = 150
        });
        medium.Sessions.Add(session);
        medium.UsedSectors = totalSectors;

        // 2. Perform RipToBinCueAsync to temporary files
        string tempDir = Path.Combine(Path.GetTempPath(), $"cdburner_bincue_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string tempBinPath = Path.Combine(tempDir, "game_disc.bin");
        string tempCuePath = Path.Combine(tempDir, "game_disc.cue");

        try
        {
            var engine = new VirtualBurnEngine();
            var ripper = new DiscRipperService();

            var ripResult = await ripper.RipToBinCueAsync(
                recorder,
                engine,
                tempBinPath,
                tempCuePath,
                options: new RipOptions { SectorCount = totalSectors },
                progress: null,
                ct: CancellationToken.None);

            // 3. Verify rip succeeded
            Assert.True(ripResult.Success);
            Assert.Equal(totalSectors, ripResult.SectorsRead);
            Assert.Empty(ripResult.BadSectorLbas);

            // 4. Verify .bin file size matches 25 * 2352 bytes
            Assert.True(File.Exists(tempBinPath));
            var binInfo = new FileInfo(tempBinPath);
            Assert.Equal((long)totalSectors * OpticalConstants.RawSectorSize, binInfo.Length);

            // 5. Verify .cue file exists and can be parsed by CueSheetParser
            Assert.True(File.Exists(tempCuePath));
            string cueText = await File.ReadAllTextAsync(tempCuePath);
            var cueSheet = CueSheetParser.Parse(cueText);

            Assert.Single(cueSheet.Files);
            var fileEntry = cueSheet.Files[0];
            Assert.Equal("game_disc.bin", fileEntry.FileName);
            Assert.Equal("BINARY", fileEntry.FileType);

            // Verify Track 1
            Assert.Equal(2, fileEntry.Tracks.Count);
            var t1 = fileEntry.Tracks[0];
            Assert.Equal(1, t1.TrackNumber);
            Assert.Equal(CueTrackDataType.Mode1_2352, t1.DataType);
            Assert.Equal("Game Data", t1.Title);
            Assert.Single(t1.Indices);
            // CUE INDEX positions are frame offsets within the BIN file: track 1 starts at 00:00:00, not at the
            // absolute disc address 00:02:00 (which other tools would read as a 2-second shift).
            Assert.Equal(new MsfAddress(0, 0, 0), t1.Indices[0].Position);

            // Verify Track 2
            var t2 = fileEntry.Tracks[1];
            Assert.Equal(2, t2.TrackNumber);
            Assert.Equal(CueTrackDataType.Audio, t2.DataType);
            Assert.Equal("Stage 1 Music", t2.Title);
            Assert.Equal("Awesome Band", t2.Performer);
            Assert.NotNull(t2.PreGap);
            Assert.Equal(new MsfAddress(0, 2, 0), t2.PreGap.Value); // 150 frames = 00:02:00
            Assert.Single(t2.Indices);
            Assert.Equal(MsfAddress.FromFrameCount(track1Sectors), t2.Indices[0].Position);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    #endregion
}
