using System.Security.Cryptography;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// Unit tests for Phase 04: Virtual Optical Drive Simulator (VirtualDiscBackend).
/// Verifies deterministic drive and medium simulation, sector read/write persistence,
/// artificial bad sector injection, rewritable blanking, and audio/data burning pipelines.
/// </summary>
public class Phase04_VirtualDriveSimulatorTests
{
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly object _lock = new();
        public List<T> Snapshots { get; } = new();

        public void Report(T value)
        {
            lock (_lock)
            {
                Snapshots.Add(value);
            }
        }
    }

    #region Preset Media Creation & Status Tests

    [Fact]
    public void MountBlankCdR700_ReportsCorrectGeometryAndStatus()
    {
        // 1. Verify mounting CreateBlankCdR700 reports IsBlank = true, TotalSectors = 360_000, FreeSectors = 360_000
        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();

        recorder.InsertMedia(medium);
        var status = recorder.QueryMediaStatus();

        Assert.True(status.MediaPresent);
        Assert.True(status.IsBlank);
        Assert.False(status.IsRewritable);
        Assert.Equal(OpticalConstants.Cd700MbSectors, status.TotalSectors);
        Assert.Equal(360_000, status.TotalSectors);
        Assert.Equal(360_000, status.FreeSectors);
        Assert.Equal(0, status.UsedSectors);
        Assert.Equal(DiscMediaType.CdR, status.MediaType);
        Assert.Equal(737_280_000L, status.TotalCapacityBytes);
        Assert.Equal(737_280_000L, status.FreeCapacityBytes);
        Assert.Equal(0, status.SessionCount);
        Assert.Equal(-1, status.LastRecordedLba);
        Assert.Equal("VIRTUAL-OPTICAL-01", status.MediaManufacturerId);

        // Verify recorder identity
        Assert.Equal("VDRV-01", recorder.UniqueId);
        Assert.Equal("IGNIS", recorder.VendorId);
        Assert.Equal("VIRTUAL DVD/CD-RW", recorder.ProductId);
        Assert.Equal("1.00", recorder.ProductRevision);
        Assert.Equal(@"V:\", recorder.VolumePath);
        Assert.True(recorder.IsVirtual);
    }

    [Fact]
    public void MountOtherPresets_ReportsExpectedGeometryAndRewritability()
    {
        using var recorder = new VirtualDiscRecorder();

        // CD-RW 700 MB
        using (var cdRw = VirtualDiscMedium.CreateBlankCdRw700())
        {
            recorder.InsertMedia(cdRw);
            var status = recorder.QueryMediaStatus();
            Assert.True(status.IsBlank);
            Assert.True(status.IsRewritable);
            Assert.Equal(360_000, status.TotalSectors);
            Assert.Equal(DiscMediaType.CdRw, status.MediaType);
        }

        // DVD-R 4.7 GB
        using (var dvdR = VirtualDiscMedium.CreateBlankDvdR47())
        {
            recorder.InsertMedia(dvdR);
            var status = recorder.QueryMediaStatus();
            Assert.True(status.IsBlank);
            Assert.False(status.IsRewritable);
            Assert.Equal(OpticalConstants.DvdMinusR5Sectors, status.TotalSectors);
            Assert.Equal(2_298_496, status.TotalSectors);
            Assert.Equal(DiscMediaType.DvdMinusR, status.MediaType);
        }

        // DVD+R DL 8.5 GB
        using (var dvdDl = VirtualDiscMedium.CreateBlankDvdPlusRDL85())
        {
            recorder.InsertMedia(dvdDl);
            var status = recorder.QueryMediaStatus();
            Assert.True(status.IsBlank);
            Assert.False(status.IsRewritable);
            Assert.Equal(OpticalConstants.DvdPlusR9Sectors, status.TotalSectors);
            Assert.Equal(4_173_824, status.TotalSectors);
            Assert.Equal(DiscMediaType.DvdPlusRDualLayer, status.MediaType);
        }

        // DVD-RW 4.7 GB
        using (var dvdRw = VirtualDiscMedium.CreateBlankDvdRw47())
        {
            recorder.InsertMedia(dvdRw);
            var status = recorder.QueryMediaStatus();
            Assert.True(status.IsBlank);
            Assert.True(status.IsRewritable);
            Assert.Equal(2_298_496, status.TotalSectors);
            Assert.Equal(DiscMediaType.DvdMinusRw, status.MediaType);
        }

        // DVD+RW 4.7 GB
        using (var dvdPlusRw = VirtualDiscMedium.CreateBlankDvdPlusRw47())
        {
            recorder.InsertMedia(dvdPlusRw);
            var status = recorder.QueryMediaStatus();
            Assert.True(status.IsBlank);
            Assert.True(status.IsRewritable);
            Assert.Equal(2_295_104, status.TotalSectors);
            Assert.Equal(DiscMediaType.DvdPlusRw, status.MediaType);
        }

        // DVD-RAM
        using (var dvdRam = VirtualDiscMedium.CreateBlankDvdRam())
        {
            recorder.InsertMedia(dvdRam);
            var status = recorder.QueryMediaStatus();
            Assert.True(status.IsBlank);
            Assert.True(status.IsRewritable);
            Assert.Equal(DiscMediaType.DvdRam, status.MediaType);
        }
    }

    #endregion

    #region Data Burning & Sector Read Verification Tests

    [Fact]
    public async Task BurnDataStreamAsync_Writes100Sectors_EmitsProgress0To100_UpdatesUsedSectors_AndReadsBackExactBytes()
    {
        // 2. Verify BurnDataStreamAsync writes 100 sectors (204,800 bytes), emits progress snapshots from 0% to 100%,
        // updates UsedSectors = 100, and ReadSectorsAsync reads back the exact 204,800 bytes.
        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        var burnEngine = new VirtualBurnEngine(recorder);

        // Generate exactly 100 sectors = 204,800 bytes with pseudorandom pattern
        const int sectorCount = 100;
        const int totalBytes = sectorCount * OpticalConstants.DataSectorSize; // 204,800
        byte[] originalData = new byte[totalBytes];
        Random.Shared.NextBytes(originalData);

        using var imageStream = new MemoryStream(originalData);
        var syncProgress = new SyncProgress<BurnProgressSnapshot>();
        var options = new DataBurnOptions
        {
            CloseDisc = false,
            EjectWhenDone = false
        };

        var burnResult = await burnEngine.BurnDataStreamAsync(
            recorder,
            imageStream,
            options,
            syncProgress,
            CancellationToken.None);

        // Assert burn result
        Assert.True(burnResult.Success, burnResult.ErrorMessage);
        Assert.Equal(100, burnResult.SectorsWritten);
        Assert.True(burnResult.TotalTime >= TimeSpan.Zero);

        // Assert progress snapshots from 0% to 100%
        Assert.NotEmpty(syncProgress.Snapshots);
        var firstSnapshot = syncProgress.Snapshots[0];
        var lastSnapshot = syncProgress.Snapshots[^1];

        Assert.Equal(0.0, firstSnapshot.ProgressPercentage);
        Assert.Equal("Initializing", firstSnapshot.CurrentActionDescription);

        Assert.Equal(100.0, lastSnapshot.ProgressPercentage);
        Assert.Equal("Completed", lastSnapshot.CurrentActionDescription);

        // Verify key action states occurred in progress sequence
        var actionDescriptions = syncProgress.Snapshots.Select(s => s.CurrentActionDescription).ToList();
        Assert.Contains("Initializing", actionDescriptions);
        Assert.Contains("CalibratingPower", actionDescriptions);
        Assert.Contains("WritingLeadIn", actionDescriptions);
        Assert.Contains("WritingData", actionDescriptions);
        Assert.Contains("WritingLeadOut", actionDescriptions);
        Assert.Contains("Completed", actionDescriptions);

        // Verify medium status updated
        Assert.False(medium.IsBlank);
        Assert.Equal(100, medium.UsedSectors);
        Assert.Equal(360_000 - 100, medium.FreeSectors);
        Assert.Single(medium.Sessions);
        Assert.Equal(100, medium.Sessions[0].SectorCount);

        var mediaStatus = recorder.QueryMediaStatus();
        Assert.False(mediaStatus.IsBlank);
        Assert.Equal(100, mediaStatus.UsedSectors);
        Assert.Equal(359_900, mediaStatus.FreeSectors);
        Assert.Equal(99, mediaStatus.LastRecordedLba);

        // Read back the exact 204,800 bytes using ReadSectorsAsync
        using var readBuffer = new MemoryStream();
        var readProgress = new SyncProgress<BurnProgressSnapshot>();

        var ripResult = await burnEngine.ReadSectorsAsync(
            recorder,
            startLba: 0,
            sectorCount: 100,
            raw2352Mode: false,
            sectorConsumer: async (lba, memory) =>
            {
                await readBuffer.WriteAsync(memory);
            },
            progress: readProgress,
            cancellationToken: CancellationToken.None);

        Assert.True(ripResult.Success, ripResult.ErrorMessage);
        Assert.Equal(100, ripResult.SectorsRead);
        Assert.Equal(0, ripResult.BadSectorsCount);

        byte[] readData = readBuffer.ToArray();
        Assert.Equal(204_800, readData.Length);
        Assert.Equal(originalData, readData);
    }

    #endregion

    #region Erase Media Tests

    [Fact]
    public async Task EraseMediaAsync_FailsOnNonRewritableCdR_AndSucceedsOnCdRw_RestoringBlank()
    {
        // 3. Verify EraseMediaAsync fails on non-rewritable CdR and succeeds on CdRw, restoring IsBlank = true.
        using var recorder = new VirtualDiscRecorder();
        var burnEngine = new VirtualBurnEngine(recorder);

        // Part A: Non-rewritable CD-R
        using (var cdR = VirtualDiscMedium.CreateBlankCdR700())
        {
            recorder.InsertMedia(cdR);

            // Write 1 sector so it is not blank
            byte[] sector = new byte[2048];
            cdR.WriteSector(0, sector);
            Assert.False(cdR.IsBlank);

            var eraseResult = await burnEngine.EraseMediaAsync(recorder, fullErase: false, null, CancellationToken.None);

            Assert.False(eraseResult.Success);
            Assert.NotNull(eraseResult.ErrorMessage);
            Assert.Contains("not rewritable", eraseResult.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.False(cdR.IsBlank); // Still not blank
        }

        // Part B: Rewritable CD-RW (Quick Erase)
        using (var cdRw = VirtualDiscMedium.CreateBlankCdRw700())
        {
            recorder.InsertMedia(cdRw);

            // Write 100 sectors to simulate a recorded disc
            byte[] dummyData = new byte[2048];
            for (int i = 0; i < 100; i++)
            {
                cdRw.WriteSector(i, dummyData);
            }
            Assert.False(cdRw.IsBlank);
            Assert.Equal(100, cdRw.UsedSectors);

            var eraseResult = await burnEngine.EraseMediaAsync(recorder, fullErase: false, null, CancellationToken.None);

            Assert.True(eraseResult.Success, eraseResult.ErrorMessage);
            Assert.True(cdRw.IsBlank);
            Assert.Equal(0, cdRw.UsedSectors);
            Assert.Equal(360_000, cdRw.FreeSectors);
            Assert.Empty(cdRw.Sessions);

            var status = recorder.QueryMediaStatus();
            Assert.True(status.IsBlank);
            Assert.Equal(0, status.UsedSectors);
            Assert.Equal(360_000, status.FreeSectors);
        }

        // Part C: Rewritable CD-RW (Full Erase)
        using (var cdRw = VirtualDiscMedium.CreateBlankCdRw700())
        {
            recorder.InsertMedia(cdRw);

            byte[] dummyData = new byte[2048];
            cdRw.WriteSector(0, dummyData);
            Assert.False(cdRw.IsBlank);

            var eraseResult = await burnEngine.EraseMediaAsync(recorder, fullErase: true, null, CancellationToken.None);

            Assert.True(eraseResult.Success, eraseResult.ErrorMessage);
            Assert.True(cdRw.IsBlank);
            Assert.Equal(0, cdRw.UsedSectors);
        }
    }

    #endregion

    #region Simulated Bad LBA Tests

    [Fact]
    public async Task SimulatedBadLbas_TriggersUnrecoveredReadError_AtExactInjectedLba_DuringReadSectorsAsync()
    {
        // 4. Verify SimulatedBadLbas triggers an unrecovered read error at the exact injected LBA during ReadSectorsAsync.
        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        var burnEngine = new VirtualBurnEngine(recorder);

        // Write 100 sectors first
        byte[] testSector = new byte[2048];
        for (int i = 0; i < 100; i++)
        {
            testSector[0] = (byte)i;
            medium.WriteSector(i, testSector);
        }

        const int badLba = 42;
        medium.SimulatedBadLbas.Add(badLba);

        // Test Mode 1: Reporting error via RipOperationResult (ThrowOnReadError = false)
        burnEngine.ThrowOnReadError = false;
        var readSectorsList = new List<int>();

        var ripResult = await burnEngine.ReadSectorsAsync(
            recorder,
            startLba: 0,
            sectorCount: 100,
            raw2352Mode: false,
            sectorConsumer: (lba, _) =>
            {
                readSectorsList.Add(lba);
                return ValueTask.CompletedTask;
            },
            progress: null,
            cancellationToken: CancellationToken.None);

        Assert.False(ripResult.Success);
        Assert.NotNull(ripResult.ErrorMessage);
        Assert.Contains("42", ripResult.ErrorMessage);
        Assert.Contains("Unrecovered read error", ripResult.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, ripResult.BadSectorsCount);
        Assert.Contains(badLba, ripResult.BadSectorsList);
        Assert.Equal(42, ripResult.SectorsRead); // Sectors 0..41 were read successfully
        Assert.Equal(42, readSectorsList.Count);
        Assert.DoesNotContain(badLba, readSectorsList);

        // Test Mode 2: Throwing OpticalReadException (ThrowOnReadError = true)
        burnEngine.ThrowOnReadError = true;
        var ex = await Assert.ThrowsAsync<OpticalReadException>(async () =>
        {
            await burnEngine.ReadSectorsAsync(
                recorder,
                startLba: 0,
                sectorCount: 100,
                raw2352Mode: false,
                sectorConsumer: (_, _) => ValueTask.CompletedTask,
                progress: null,
                cancellationToken: CancellationToken.None);
        });

        Assert.Equal(badLba, ex.Lba);
        Assert.Equal(0x03, ex.SenseKey); // Medium Error
        Assert.Equal(0x11, ex.Asc);      // Unrecovered Read Error
        Assert.Equal(0x00, ex.Ascq);

        // Test Mode 3: Direct ReadSector on VirtualDiscMedium throws OpticalReadException
        byte[] dest = new byte[2048];
        var mediumEx = Assert.Throws<OpticalReadException>(() => medium.ReadSector(badLba, dest));
        Assert.Equal(badLba, mediumEx.Lba);

        // Non-bad sector reads without error
        medium.ReadSector(0, dest);
        Assert.Equal(0, dest[0]);
    }

    #endregion

    #region Backing File Persistence & Audio Tests

    [Fact]
    public async Task FileBackingStore_PersistsSectorsToDisk_AndAllowsVerification()
    {
        string tempBackingFile = Path.Combine(Path.GetTempPath(), $"cdburner_test_{Guid.NewGuid():N}.bin");

        try
        {
            byte[] payload = new byte[2048 * 10]; // 10 sectors
            RandomNumberGenerator.Fill(payload);

            using (var medium = VirtualDiscMedium.CreateBlankCdR700(tempBackingFile))
            using (var recorder = new VirtualDiscRecorder())
            {
                recorder.InsertMedia(medium);
                var engine = new VirtualBurnEngine(recorder);

                using var stream = new MemoryStream(payload);
                var result = await engine.BurnDataStreamAsync(recorder, stream, new DataBurnOptions(), null, CancellationToken.None);
                Assert.True(result.Success);
            }

            // File should exist on disk with at least 10 * 2048 bytes
            Assert.True(File.Exists(tempBackingFile));
            var fileBytes = await File.ReadAllBytesAsync(tempBackingFile);
            Assert.True(fileBytes.Length >= payload.Length);
            Assert.Equal(payload, fileBytes[..payload.Length]);

            // Mount file as existing disc medium
            using var mountedMedium = VirtualDiscMedium.FromExistingFile(tempBackingFile);
            Assert.Equal(10, mountedMedium.UsedSectors);
            Assert.False(mountedMedium.IsBlank);

            byte[] readBack = new byte[2048 * 10];
            mountedMedium.ReadSectors(0, 10, readBack);
            Assert.Equal(payload, readBack);
        }
        finally
        {
            if (File.Exists(tempBackingFile))
            {
                try { File.Delete(tempBackingFile); } catch { /* ignore */ }
            }
        }
    }

    [Fact]
    public async Task BurnAudioTracksAsync_Writes2352ByteFrames_AndRecordsTrackMetadata()
    {
        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdRw700();
        recorder.InsertMedia(medium);

        var engine = new VirtualBurnEngine(recorder);

        var tracks = new List<AudioTrackSource>
        {
            new()
            {
                TrackNumber = 1,
                Title = "Track One",
                Performer = "Artist Alpha",
                TotalPcmBytes = 2352 * 50 // 50 frames
            },
            new()
            {
                TrackNumber = 2,
                Title = "Track Two",
                Performer = "Artist Beta",
                TotalPcmBytes = 2352 * 75 // 75 frames
            }
        };

        var progress = new SyncProgress<BurnProgressSnapshot>();
        var burnResult = await engine.BurnAudioTracksAsync(
            recorder,
            tracks,
            new AudioBurnOptions { WriteCdText = true, UseDiscAtOnce = true },
            progress,
            CancellationToken.None);

        Assert.True(burnResult.Success, burnResult.ErrorMessage);
        Assert.Equal(125, burnResult.SectorsWritten);
        Assert.True(medium.Raw2352Mode);
        Assert.True(medium.IsFinalized);
        Assert.NotNull(medium.CdTextBytes);
        Assert.Single(medium.Sessions);

        var session = medium.Sessions[0];
        Assert.Equal(2, session.Tracks.Count);
        Assert.Equal("Track One", session.Tracks[0].Title);
        Assert.Equal(50, session.Tracks[0].SectorCount);
        Assert.Equal("Track Two", session.Tracks[1].Title);
        Assert.Equal(75, session.Tracks[1].SectorCount);
        Assert.Equal(50, session.Tracks[1].StartLba);
    }

    [Fact]
    public void Recorder_TrayAndExclusiveAccess_OperateCorrectly()
    {
        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();

        // Tray open/close
        recorder.InsertMedia(medium);
        Assert.True(recorder.QueryMediaStatus().MediaPresent);

        recorder.EjectMedia();
        Assert.True(recorder.TrayOpen);
        Assert.False(recorder.QueryMediaStatus().MediaPresent);

        recorder.CloseTray();
        Assert.False(recorder.TrayOpen);
        Assert.True(recorder.QueryMediaStatus().MediaPresent);

        // Exclusive access
        Assert.True(recorder.AcquireExclusiveAccess("ClientA"));
        Assert.False(recorder.AcquireExclusiveAccess("ClientB"));
        Assert.True(recorder.AcquireExclusiveAccess("ClientA")); // Re-entrant for same client

        recorder.ReleaseExclusiveAccess();
        Assert.True(recorder.AcquireExclusiveAccess("ClientB"));

        // Capabilities
        var caps = recorder.GetCapabilities();
        Assert.True(caps.CanWriteCdR);
        Assert.True(caps.CanWriteCdRw);
        Assert.True(caps.CanWriteDvdR);
        Assert.True(caps.CanWriteDvdRw);
        Assert.True(caps.CanWriteDvdRam);
        Assert.NotEmpty(caps.SupportedWriteSpeeds);
        Assert.NotEmpty(caps.SupportedMediaTypes);
    }

    [Fact]
    public void VirtualDiscBackend_Facade_MountsAndEjectsMedia()
    {
        using var backend = new VirtualDiscBackend();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();

        backend.Mount(medium);
        Assert.NotNull(backend.CurrentMedium);
        Assert.Equal(DiscMediaType.CdR, backend.CurrentMedium.MediaType);

        backend.Eject();
        Assert.Null(backend.CurrentMedium);

        backend.CloseTray();
        Assert.NotNull(backend.CurrentMedium);
    }

    #endregion
}
