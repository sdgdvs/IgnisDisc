using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Burning;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Burning;
using CdBurner.Hardware.Interop;
using CdBurner.Hardware.Simulation;
using Xunit;
using DataBurnStatus = CdBurner.Core.Abstractions.BurnStatus;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// Unit tests for Data Disc & ISO Burning Pipeline + Event Sink.
/// Verifies MtaBurnWorker execution on MTA threads with FIFO Channel streaming,
/// DiscFormatDataEventsSink translation, CompositeBurnEngine multiplexing,
/// and DataBurnService end-to-end compilation burning, EjectAfterBurn, and mid-burn cancellation.
/// </summary>
public class DataBurnPipelineTests
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

    #region MtaBurnWorker Tests

    [Fact]
    public async Task MtaBurnWorker_ExecutesOnDedicatedMtaThread_AndStreamsProgressInOrder()
    {
        var progress = new ThreadSafeListProgress<BurnProgressSnapshot>();
        ApartmentState capturedApartmentState = ApartmentState.Unknown;
        int capturedManagedThreadId = -1;
        int callingThreadId = Environment.CurrentManagedThreadId;

        const int snapshotCount = 20;

        var result = await MtaBurnWorker.RunAsync((workerProgress, token) =>
        {
            capturedApartmentState = Thread.CurrentThread.GetApartmentState();
            capturedManagedThreadId = Environment.CurrentManagedThreadId;

            for (int i = 1; i <= snapshotCount; i++)
            {
                workerProgress.Report(new BurnProgressSnapshot
                {
                    CurrentLba = i * 100,
                    TotalLba = snapshotCount * 100,
                    CurrentActionDescription = $"Step {i}",
                    BufferLevelPercent = 100.0,
                    ElapsedTime = TimeSpan.FromSeconds(i),
                    RemainingTime = TimeSpan.FromSeconds(snapshotCount - i)
                });
            }

            return BurnOperationResult.Succeeded(snapshotCount * 100, TimeSpan.FromSeconds(snapshotCount), 16.0);
        }, progress);

        // 1. Apartment state must be MTA
        Assert.Equal(ApartmentState.MTA, capturedApartmentState);

        // 2. Must run on a distinct dedicated background thread
        Assert.NotEqual(callingThreadId, capturedManagedThreadId);

        // 3. Result succeeded
        Assert.True(result.Success);
        Assert.Equal(BurnStatus.Succeeded, result.Status);
        Assert.Equal(snapshotCount * 100, result.SectorsWritten);

        // 4. All snapshots received in exact FIFO sequential order
        Assert.Equal(snapshotCount, progress.Items.Count);
        for (int i = 0; i < snapshotCount; i++)
        {
            var expectedLba = (i + 1) * 100;
            Assert.Equal(expectedLba, progress.Items[i].CurrentLba);
            Assert.Equal($"Step {i + 1}", progress.Items[i].CurrentActionDescription);
        }
    }

    [Fact]
    public async Task MtaBurnWorker_SupportsAsyncWorkDelegate_OnMtaThread()
    {
        var progress = new ThreadSafeListProgress<BurnProgressSnapshot>();
        ApartmentState capturedApartment = ApartmentState.Unknown;

        var result = await MtaBurnWorker.RunTaskAsync(async (workerProgress, token) =>
        {
            capturedApartment = Thread.CurrentThread.GetApartmentState();

            workerProgress.Report(new BurnProgressSnapshot
            {
                CurrentLba = 50,
                TotalLba = 100,
                CurrentActionDescription = "Halfway"
            });

            await Task.Delay(10, token);

            workerProgress.Report(new BurnProgressSnapshot
            {
                CurrentLba = 100,
                TotalLba = 100,
                CurrentActionDescription = "Completed"
            });

            return 42;
        }, progress);

        Assert.Equal(ApartmentState.MTA, capturedApartment);
        Assert.Equal(42, result);
        Assert.Equal(2, progress.Items.Count);
        Assert.Equal("Halfway", progress.Items[0].CurrentActionDescription);
        Assert.Equal("Completed", progress.Items[1].CurrentActionDescription);
    }

    [Fact]
    public async Task MtaBurnWorker_PropagatesCancellation_WhenRequested()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<IProgress<BurnProgressSnapshot>, CancellationToken, BurnOperationResult> cancelWork = (workerProgress, token) =>
        {
            token.ThrowIfCancellationRequested();
            return BurnOperationResult.Succeeded(100, TimeSpan.Zero);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await MtaBurnWorker.RunAsync(cancelWork, null, cts.Token);
        });
    }

    [Fact]
    public async Task MtaBurnWorker_PropagatesExceptions_FromWorkerThread()
    {
        Func<IProgress<BurnProgressSnapshot>, CancellationToken, BurnOperationResult> failWork = (workerProgress, token) =>
        {
            throw new InvalidOperationException("Simulated hardware bus reset failure.");
        };

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await MtaBurnWorker.RunAsync(failWork, null);
        });
    }

    #endregion

    #region DiscFormatDataEventsSink Tests

    [Fact]
    public void DiscFormatDataEventsSink_CreateSnapshot_TranslatesPropertiesAccurately()
    {
        var snapshot = DiscFormatDataEventsSink.CreateSnapshot(
            elapsedTimeSec: 15,
            remainingTimeSec: 45,
            startLba: 0,
            sectorCount: 10_000,
            lastReadLba: 2_600,
            lastWrittenLba: 2_500,
            totalSystemBuffer: 8_388_608,
            usedSystemBuffer: 4_194_304,
            freeSystemBuffer: 4_194_304,
            currentAction: ImapiFormat2DataWriteAction.WritingData);

        Assert.Equal(TimeSpan.FromSeconds(15), snapshot.ElapsedTime);
        Assert.Equal(TimeSpan.FromSeconds(45), snapshot.RemainingTime);
        Assert.Equal(2_501, snapshot.CurrentLba);
        Assert.Equal(10_000, snapshot.TotalLba);
        Assert.Equal(50.0, snapshot.BufferLevelPercent, precision: 1);
        Assert.Equal("WritingData", snapshot.CurrentActionDescription);
        Assert.True(snapshot.ProgressPercentage > 24.9 && snapshot.ProgressPercentage < 25.1);
        Assert.True(snapshot.CurrentBytesPerSecond > 0);
        Assert.True(snapshot.CurrentSpeedMultiplier > 0);
    }

    [Fact]
    public void DiscFormatDataEventsSink_CreateSnapshot_HandlesCompletedAction()
    {
        var snapshot = DiscFormatDataEventsSink.CreateSnapshot(
            elapsedTimeSec: 60,
            remainingTimeSec: 0,
            startLba: 0,
            sectorCount: 5_000,
            lastReadLba: 5_000,
            lastWrittenLba: 4_999,
            totalSystemBuffer: 8_388_608,
            usedSystemBuffer: 0,
            freeSystemBuffer: 8_388_608,
            currentAction: ImapiFormat2DataWriteAction.Completed);

        Assert.Equal(TimeSpan.Zero, snapshot.RemainingTime);
        Assert.Equal(5_000, snapshot.CurrentLba);
        Assert.Equal(5_000, snapshot.TotalLba);
        Assert.Equal(100.0, snapshot.ProgressPercentage);
        Assert.Equal(0.0, snapshot.BufferLevelPercent);
        Assert.Equal("Completed", snapshot.CurrentActionDescription);
    }

    private class MockDiscFormat2DataEventArgs : IDiscFormat2DataEventArgs
    {
        public int StartLba { get; set; } = 0;
        public int SectorCount { get; set; } = 20_000;
        public int LastReadLba { get; set; } = 10_000;
        public int LastWrittenLba { get; set; } = 9_999;
        public int TotalSystemBuffer { get; set; } = 4_000_000;
        public int UsedSystemBuffer { get; set; } = 3_000_000;
        public int FreeSystemBuffer { get; set; } = 1_000_000;
        public int ElapsedTime { get; set; } = 30;
        public int RemainingTime { get; set; } = 30;
        public int TotalTime { get; set; } = 60;
        public ImapiFormat2DataWriteAction CurrentAction { get; set; } = ImapiFormat2DataWriteAction.WritingData;
    }

    [Fact]
    public void DiscFormatDataEventsSink_Update_DispatchesSnapshotToProgressAndEvent()
    {
        var progress = new ThreadSafeListProgress<BurnProgressSnapshot>();
        BurnProgressSnapshot? eventSnapshot = null;

        using var sink = new DiscFormatDataEventsSink(progress);
        sink.ProgressReported += (s, snap) => eventSnapshot = snap;

        var args = new MockDiscFormat2DataEventArgs
        {
            ElapsedTime = 20,
            RemainingTime = 40,
            StartLba = 0,
            SectorCount = 10_000,
            LastWrittenLba = 4_999,
            TotalSystemBuffer = 10_000_000,
            UsedSystemBuffer = 7_500_000,
            CurrentAction = ImapiFormat2DataWriteAction.WritingData
        };

        sink.Update(sender: null!, progress: args);

        Assert.Single(progress.Items);
        var snapshot = progress.Items[0];
        Assert.Equal(5_000, snapshot.CurrentLba);
        Assert.Equal(10_000, snapshot.TotalLba);
        Assert.Equal(50.0, snapshot.ProgressPercentage);
        Assert.Equal(75.0, snapshot.BufferLevelPercent, precision: 1);
        Assert.Equal("WritingData", snapshot.CurrentActionDescription);

        Assert.NotNull(eventSnapshot);
        Assert.Same(snapshot, eventSnapshot);
    }

    private class MockConnectionPointContainer : IConnectionPointContainer
    {
        public MockConnectionPoint ConnectionPoint { get; } = new();

        public void EnumConnectionPoints(out IEnumConnectionPoints ppEnum) => throw new NotImplementedException();

        public void FindConnectionPoint(ref Guid riid, out IConnectionPoint ppCP)
        {
            ppCP = ConnectionPoint;
        }
    }

    private class MockConnectionPoint : IConnectionPoint
    {
        public bool Advised { get; private set; }
        public bool Unadvised { get; private set; }
        public int AdvisedCookie { get; private set; }

        public void GetConnectionInterface(out Guid pIID) => throw new NotImplementedException();
        public void GetConnectionPointContainer(out IConnectionPointContainer ppCPC) => throw new NotImplementedException();

        public void Advise(object pUnkSink, out int pdwCookie)
        {
            Advised = true;
            pdwCookie = 12345;
            AdvisedCookie = pdwCookie;
        }

        public void Unadvise(int dwCookie)
        {
            if (dwCookie == AdvisedCookie)
            {
                Unadvised = true;
                Advised = false;
            }
        }

        public void EnumConnections(out IEnumConnections ppEnum) => throw new NotImplementedException();
    }

    [Fact]
    public void DiscFormatDataEventsSink_ConnectAndDisconnect_ManagesConnectionPointLifecycle()
    {
        var container = new MockConnectionPointContainer();
        using var sink = new DiscFormatDataEventsSink();

        Assert.False(sink.IsConnected);

        sink.Connect(container);
        Assert.True(sink.IsConnected);
        Assert.True(container.ConnectionPoint.Advised);

        sink.Disconnect();
        Assert.False(sink.IsConnected);
        Assert.True(container.ConnectionPoint.Unadvised);
    }

    #endregion

    #region CompositeBurnEngine Tests

    [Fact]
    public async Task CompositeBurnEngine_RoutesToVirtualEngine_WhenRecorderIsVirtual()
    {
        using var virtualRecorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        virtualRecorder.InsertMedia(medium);

        var compositeEngine = new CompositeBurnEngine();

        byte[] payload = new byte[OpticalConstants.DataSectorSize * 4];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 256);
        using var stream = new MemoryStream(payload);

        var options = new DataBurnOptions { VolumeLabel = "COMPOSITE_TEST", CloseDisc = true };
        var result = await compositeEngine.BurnDataStreamAsync(virtualRecorder, stream, options, null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(4, result.SectorsWritten);
        Assert.Equal(4, medium.UsedSectors);
        Assert.True(medium.IsFinalized);
    }

    [Fact]
    public async Task CompositeBurnEngine_RoutesToPhysicalEngine_WhenRecorderIsNotVirtual()
    {
        var dummyPhysical = new DummyPhysicalRecorder();
        bool physicalEngineCalled = false;

        var mockPhysical = new MockBurnEngine
        {
            OnBurnDataStream = (rec, strm, opt, prog, tok) =>
            {
                physicalEngineCalled = true;
                return Task.FromResult(BurnOperationResult.Succeeded(1, TimeSpan.Zero));
            }
        };

        var compositeEngine = new CompositeBurnEngine(physicalEngine: mockPhysical);

        using var stream = new MemoryStream(new byte[2048]);
        var result = await compositeEngine.BurnDataStreamAsync(dummyPhysical, stream, new DataBurnOptions(), null, CancellationToken.None);

        Assert.True(physicalEngineCalled, "CompositeBurnEngine must route non-virtual recorder calls to the physical engine backend.");
        Assert.True(result.Success);
    }

    [Fact]
    public async Task Imapi2BurnEngine_HandlesMissingPhysicalDrive_Gracefully()
    {
        var dummyPhysical = new DummyPhysicalRecorder();
        var physicalEngine = new Imapi2BurnEngine();

        using var stream = new MemoryStream(new byte[2048]);
        var result = await physicalEngine.BurnDataStreamAsync(dummyPhysical, stream, new DataBurnOptions(), null, CancellationToken.None);

        // When physical recorder has invalid ID, Imapi2BurnEngine returns failed result without unhandled crash
        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Equal(BurnStatus.Failed, result.Status);
    }

    private class MockBurnEngine : IBurnEngineBackend
    {
        public Func<IDiscRecorderBackend, Stream, DataBurnOptions, IProgress<BurnProgressSnapshot>?, CancellationToken, Task<BurnOperationResult>>? OnBurnDataStream { get; set; }

        public Task<BurnOperationResult> BurnDataStreamAsync(IDiscRecorderBackend recorder, Stream imageStream, DataBurnOptions options, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken)
            => OnBurnDataStream?.Invoke(recorder, imageStream, options, progress, cancellationToken) ?? Task.FromResult(BurnOperationResult.Succeeded(0, TimeSpan.Zero));

        public Task<BurnOperationResult> BurnAudioTracksAsync(IDiscRecorderBackend recorder, IReadOnlyList<AudioTrackSource> tracks, AudioBurnOptions options, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken)
            => Task.FromResult(BurnOperationResult.Succeeded(0, TimeSpan.Zero));

        public Task<BurnOperationResult> EraseMediaAsync(IDiscRecorderBackend recorder, bool fullErase, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken)
            => Task.FromResult(BurnOperationResult.Succeeded(0, TimeSpan.Zero));

        public Task<RipOperationResult> ReadSectorsAsync(IDiscRecorderBackend recorder, int startLba, int sectorCount, bool raw2352Mode, Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken)
            => Task.FromResult(RipOperationResult.Succeeded(0, TimeSpan.Zero));
    }

    private class DummyPhysicalRecorder : IDiscRecorderBackend
    {
        public string UniqueId => "PHYS-DRV-01";
        public string VendorId => "VENDOR";
        public string ProductId => "DRIVE";
        public string ProductRevision => "1.00";
        public string VolumePath => "Z:\\";
        public bool IsVirtual => false;

        public DriveCapabilitiesDto GetCapabilities() => new();
        public MediaStatusDto QueryMediaStatus() => new() { MediaPresent = false };
        public void EjectMedia() { }
        public void CloseTray() { }
        public bool AcquireExclusiveAccess(string clientName) => true;
        public void ReleaseExclusiveAccess() { }
        public void Dispose() { }
    }

    #endregion

    #region DataBurnService Tests

    [Fact]
    public async Task DataBurnService_BurnCompilationAsync_CompilesMultiFileAndBurnsToVirtualRecorder()
    {
        // 1. Prepare Virtual Recorder with blank CD-R
        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        // 2. Build multi-file CompilationDirectory
        var rootDir = CompilationDirectory.CreateRoot();
        rootDir.AddFile(new CompilationFile("README.TXT", () => new MemoryStream(Encoding.ASCII.GetBytes("CdBurner Project Root Readme"))));
        rootDir.AddFile(new CompilationFile("CONFIG.DAT", () => new MemoryStream(new byte[4096])));

        var docsDir = rootDir.AddDirectory("DOCS");
        docsDir.AddFile(new CompilationFile("MANUAL.PDF", () => new MemoryStream(new byte[8192])));
        docsDir.AddFile(new CompilationFile("CHANGELOG.MD", () => new MemoryStream(Encoding.UTF8.GetBytes("# Version 1.0\nInitial release."))));

        var subDir = docsDir.AddDirectory("ARCH");
        subDir.AddFile(new CompilationFile("DIAGRAM.PNG", () => new MemoryStream(new byte[16384])));

        // 3. Track progress snapshots
        var progressList = new ThreadSafeListProgress<BurnProgressSnapshot>();

        var burnOptions = new DataBurnOptions
        {
            VolumeLabel = "TEST0_DATA",
            EjectAfterBurn = true,
            CloseDisc = true
        };

        var service = new DataBurnService(new VirtualBurnEngine());
        var result = await service.BurnCompilationAsync(
            recorder,
            rootDir,
            burnOptions: burnOptions,
            progress: progressList);

        // 4. Verify burn result
        Assert.True(result.Success, $"Burn failed: {result.ErrorMessage}");
        Assert.Equal(BurnStatus.Succeeded, result.Status);
        Assert.Equal(DataBurnStatus.Succeeded, result.Status);
        Assert.True(result.SectorsWritten > 0);

        // 5. Verify EjectAfterBurn opened the tray
        Assert.True(recorder.TrayOpen, "Tray must be open after burn when EjectAfterBurn is true.");

        // 6. Verify progress snapshots were emitted spanning 0% to 100%
        Assert.NotEmpty(progressList.Items);
        var firstSnap = progressList.Items.First();
        var lastSnap = progressList.Items.Last();

        Assert.Equal(0.0, firstSnap.ProgressPercentage);
        Assert.Equal(100.0, lastSnap.ProgressPercentage);

        var actions = progressList.Items.Select(s => s.CurrentActionDescription).ToList();
        Assert.Contains("BuildingFileSystem", actions);
        Assert.Contains("WritingData", actions);
        Assert.Contains("Completed", actions);

        // 7. Close tray and verify burned PVD volume label at LBA 16
        recorder.CloseTray();
        var discMedium = recorder.CurrentMedium;
        Assert.NotNull(discMedium);
        Assert.True(discMedium.IsFinalized);

        byte[] pvdSector = new byte[OpticalConstants.DataSectorSize];
        discMedium.ReadSector(16, pvdSector, OpticalConstants.DataSectorSize);
        string volDescId = Encoding.ASCII.GetString(pvdSector, 1, 5);
        Assert.Equal("CD001", volDescId);

        string volumeLabel = Encoding.ASCII.GetString(pvdSector, 40, 32).Trim();
        Assert.StartsWith("TEST0_DATA", volumeLabel);
    }

    [Fact]
    public async Task DataBurnService_BurnCompilationAsync_RespectsEjectAfterBurnFalse()
    {
        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        var rootDir = CompilationDirectory.CreateRoot();
        rootDir.AddFile(new CompilationFile("HELLO.TXT", () => new MemoryStream(Encoding.ASCII.GetBytes("Hello World"))));

        var burnOptions = new DataBurnOptions
        {
            VolumeLabel = "NO_EJECT_TEST",
            EjectAfterBurn = false,
            CloseDisc = true
        };

        var service = new DataBurnService(new VirtualBurnEngine());
        var result = await service.BurnCompilationAsync(recorder, rootDir, burnOptions: burnOptions);

        Assert.True(result.Success);
        Assert.False(recorder.TrayOpen, "Tray should remain closed when EjectAfterBurn is false.");
    }

    [Fact]
    public async Task DataBurnService_BurnCompilationAsync_SupportsMidBurnCancellation()
    {
        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        // Multi-file compilation with substantial data
        var rootDir = CompilationDirectory.CreateRoot();
        for (int i = 0; i < 20; i++)
        {
            rootDir.AddFile(new CompilationFile($"FILE_{i:D2}.BIN", () => new MemoryStream(new byte[65536])));
        }

        using var cts = new CancellationTokenSource();
        var progress = new ThreadSafeListProgress<BurnProgressSnapshot>();

        var burnProgress = new Progress<BurnProgressSnapshot>(snap =>
        {
            progress.Report(snap);
            // Cancel as soon as WritingData begins
            if (snap.CurrentActionDescription == "WritingData" && snap.CurrentLba >= 10)
            {
                cts.Cancel();
            }
        });

        var service = new DataBurnService(new VirtualBurnEngine());
        var result = await service.BurnCompilationAsync(
            recorder,
            rootDir,
            burnOptions: new DataBurnOptions { VolumeLabel = "CANCEL_TEST" },
            progress: burnProgress,
            cancellationToken: cts.Token);

        // Verification of cancellation status
        Assert.False(result.Success);
        Assert.Equal(BurnStatus.Cancelled, result.Status);
        Assert.Equal(DataBurnStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task DataBurnService_BurnCompilationAsync_PostBurnVerification_Succeeds()
    {
        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        var rootDir = CompilationDirectory.CreateRoot();
        rootDir.AddFile(new CompilationFile("VERIFY.TXT", () => new MemoryStream(Encoding.UTF8.GetBytes("Data integrity check 1234567890"))));

        var burnOptions = new DataBurnOptions
        {
            VolumeLabel = "VERIFY_TEST",
            VerifyAfterBurn = true,
            EjectAfterBurn = true
        };

        var service = new DataBurnService(new VirtualBurnEngine());
        var result = await service.BurnCompilationAsync(recorder, rootDir, burnOptions: burnOptions);

        Assert.True(result.Success);
        Assert.True(result.Verified);
        Assert.True(recorder.TrayOpen);
    }

    [Fact]
    public async Task DataBurnService_BurnIsoFileAsync_BurnsExistingIsoFile()
    {
        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        // Create a minimal ISO image on disk
        string tempIsoPath = Path.Combine(Path.GetTempPath(), $"burn_test_{Guid.NewGuid():N}.iso");
        try
        {
            var rootDir = CompilationDirectory.CreateRoot();
            rootDir.AddFile(new CompilationFile("ISOFILE.TXT", () => new MemoryStream(Encoding.ASCII.GetBytes("From ISO File"))));

            IsoUdfImageBuilder.BuildToFile(rootDir, tempIsoPath, new IsoBuildOptions(VolumeLabel: "ISO_FILE_TEST"));

            var service = new DataBurnService(new VirtualBurnEngine());
            var result = await service.BurnIsoFileAsync(
                recorder,
                tempIsoPath,
                new DataBurnOptions { VolumeLabel = "ISO_FILE_TEST", EjectAfterBurn = false });

            Assert.True(result.Success);
            Assert.True(result.SectorsWritten > 0);
            Assert.Equal(BurnStatus.Succeeded, result.Status);
        }
        finally
        {
            if (File.Exists(tempIsoPath))
            {
                try { File.Delete(tempIsoPath); } catch { }
            }
        }
    }

    [Fact]
    public async Task DataBurnService_FailsGracefully_WhenNoMediaPresent()
    {
        using var recorder = new VirtualDiscRecorder(); // Empty drive (TrayOpen = false, Medium = null)

        var rootDir = CompilationDirectory.CreateRoot();
        rootDir.AddFile(new CompilationFile("TEST.TXT", () => new MemoryStream(new byte[100])));

        var service = new DataBurnService(new VirtualBurnEngine());
        var result = await service.BurnCompilationAsync(recorder, rootDir);

        Assert.False(result.Success);
        Assert.Equal(BurnStatus.Failed, result.Status);
        Assert.Contains("No optical media present", result.ErrorMessage);
    }

    [Fact]
    public async Task DataBurnService_FailsGracefully_WhenCompilationExceedsMediaCapacity()
    {
        using var recorder = new VirtualDiscRecorder();
        // Custom medium with only 10 sectors of capacity
        using var tinyMedium = new VirtualDiscMedium(DiscMediaType.CdR, totalCapacitySectors: 10);
        recorder.InsertMedia(tinyMedium);

        var rootDir = CompilationDirectory.CreateRoot();
        // Add a 1 MB file (well above 10 sectors = 20 KB)
        rootDir.AddFile(new CompilationFile("LARGE.DAT", () => new MemoryStream(new byte[1024 * 1024])));

        var service = new DataBurnService(new VirtualBurnEngine());
        var result = await service.BurnCompilationAsync(recorder, rootDir);

        Assert.False(result.Success);
        Assert.Equal(BurnStatus.Failed, result.Status);
        Assert.Contains("exceeds available media capacity", result.ErrorMessage);
    }

    #endregion
}
