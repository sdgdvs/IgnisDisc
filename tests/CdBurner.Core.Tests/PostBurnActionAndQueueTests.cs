using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.Burning;
using CdBurner.Core.Primitives;
using Xunit;

namespace CdBurner.Core.Tests;

public class PostBurnActionAndQueueTests : IDisposable
{
    private readonly string _testTempDir;

    public PostBurnActionAndQueueTests()
    {
        _testTempDir = Path.Combine(Path.GetTempPath(), "CdBurner_PostBurnQueue_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testTempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testTempDir))
            {
                Directory.Delete(_testTempDir, true);
            }
        }
        catch
        {
        }
    }

    #region Fake Recorder and Engine

    private sealed class FakeRecorder : IDiscRecorderBackend
    {
        public string UniqueId { get; set; } = "FAKE-01";
        public string VendorId { get; set; } = "TEST";
        public string ProductId { get; set; } = "FAKE RECORDER";
        public string ProductRevision { get; set; } = "1.00";
        public string VolumePath { get; set; } = @"T:\";
        public bool IsVirtual { get; set; } = true;

        public int EjectCount { get; private set; }
        public bool ThrowOnEject { get; set; }

        public MediaStatusDto MediaStatus { get; set; } = new()
        {
            MediaPresent = true,
            IsBlank = true,
            IsRewritable = false,
            IsAppendable = false,
            TotalCapacityBytes = 700 * 1024 * 1024,
            FreeCapacityBytes = 700 * 1024 * 1024,
            TotalSectors = 350000,
            FreeSectors = 350000,
            MediaType = DiscMediaType.CdR
        };

        public DriveCapabilitiesDto Capabilities { get; set; } = new()
        {
            CanWriteCdR = true,
            CanWriteCdRw = true,
            CanWriteDvdR = true,
            SupportedMediaTypes = new[] { DiscMediaType.CdR, DiscMediaType.DvdMinusR },
            SupportedWriteSpeeds = new[] { new WriteSpeedDescriptor(2457600, DiscMediaType.CdR) }
        };

        public void EjectMedia()
        {
            EjectCount++;
            if (ThrowOnEject)
            {
                throw new InvalidOperationException("Hardware tray motor failure simulation.");
            }
        }

        public void CloseTray() { }
        public DriveCapabilitiesDto GetCapabilities() => Capabilities;
        public MediaStatusDto QueryMediaStatus() => MediaStatus;
        public MediaStatusDto ReadFullToc() => MediaStatus;
        public bool AcquireExclusiveAccess(string clientName) => true;
        public void ReleaseExclusiveAccess() { }
        public void Dispose() { }
    }

    private sealed class FakeBurnEngine : IBurnEngineBackend
    {
        public int BurnCount { get; private set; }
        public List<string> BurnedLabels { get; } = new();

        public Task<BurnOperationResult> BurnDataStreamAsync(
            IDiscRecorderBackend recorder,
            Stream dataStream,
            DataBurnOptions? options = null,
            IProgress<BurnProgressSnapshot>? progress = null,
            CancellationToken cancellationToken = default)
        {
            BurnCount++;
            BurnedLabels.Add(options?.VolumeLabel ?? string.Empty);
            int sectors = OpticalConstants.BytesToSectors(dataStream.Length, OpticalConstants.DataSectorSize);
            return Task.FromResult(BurnOperationResult.Succeeded(sectors, TimeSpan.FromSeconds(1), 16.0));
        }

        public Task<BurnOperationResult> BurnAudioTracksAsync(
            IDiscRecorderBackend recorder,
            IReadOnlyList<AudioTrackSource> tracks,
            AudioBurnOptions options,
            IProgress<BurnProgressSnapshot>? progress,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<BurnOperationResult> EraseMediaAsync(
            IDiscRecorderBackend recorder,
            bool fullErase,
            IProgress<BurnProgressSnapshot>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<RipOperationResult> ReadSectorsAsync(
            IDiscRecorderBackend recorder,
            int startLba,
            int sectorCount,
            bool raw2352Mode,
            Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer,
            IProgress<BurnProgressSnapshot>? progress,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();
    }

    #endregion

    #region PostBurnActionExecutor Tests

    [Fact]
    public void PostBurnActionExecutor_None_DoesNotInvokeAnyCallback()
    {
        var recorder = new FakeRecorder();
        bool soundPlayed = false;
        bool shutdownCalled = false;

        PostBurnActionExecutor.Execute(
            PostBurnAction.None,
            recorder,
            soundPlayer: () => soundPlayed = true,
            shutdownInitiator: () => shutdownCalled = true);

        Assert.Equal(0, recorder.EjectCount);
        Assert.False(soundPlayed);
        Assert.False(shutdownCalled);
    }

    [Fact]
    public void PostBurnActionExecutor_Eject_CallsEjectMediaSafely()
    {
        var recorder = new FakeRecorder();
        bool soundPlayed = false;
        bool shutdownCalled = false;

        PostBurnActionExecutor.Execute(
            PostBurnAction.Eject,
            recorder,
            soundPlayer: () => soundPlayed = true,
            shutdownInitiator: () => shutdownCalled = true);

        Assert.Equal(1, recorder.EjectCount);
        Assert.False(soundPlayed);
        Assert.False(shutdownCalled);
    }

    [Fact]
    public void PostBurnActionExecutor_Eject_HandlesHardwareExceptionGracefullyWithoutThrowing()
    {
        var recorder = new FakeRecorder { ThrowOnEject = true };

        // Should not throw even when hardware tray motor fails
        var ex = Record.Exception(() =>
            PostBurnActionExecutor.Execute(PostBurnAction.Eject, recorder));

        Assert.Null(ex);
        Assert.Equal(1, recorder.EjectCount);
    }

    [Fact]
    public void PostBurnActionExecutor_PlaySound_InvokesSoundPlayerCallback()
    {
        var recorder = new FakeRecorder();
        bool soundPlayed = false;
        bool shutdownCalled = false;

        PostBurnActionExecutor.Execute(
            PostBurnAction.PlaySound,
            recorder,
            soundPlayer: () => soundPlayed = true,
            shutdownInitiator: () => shutdownCalled = true);

        Assert.Equal(0, recorder.EjectCount);
        Assert.True(soundPlayed);
        Assert.False(shutdownCalled);
    }

    [Fact]
    public void PostBurnActionExecutor_EjectAndPlaySound_CallsEjectAndPlaysSound()
    {
        var recorder = new FakeRecorder();
        bool soundPlayed = false;
        bool shutdownCalled = false;

        PostBurnActionExecutor.Execute(
            PostBurnAction.EjectAndPlaySound,
            recorder,
            soundPlayer: () => soundPlayed = true,
            shutdownInitiator: () => shutdownCalled = true);

        Assert.Equal(1, recorder.EjectCount);
        Assert.True(soundPlayed);
        Assert.False(shutdownCalled);
    }

    [Fact]
    public void PostBurnActionExecutor_ShutDown_InvokesShutdownInitiator()
    {
        var recorder = new FakeRecorder();
        bool soundPlayed = false;
        bool shutdownCalled = false;

        PostBurnActionExecutor.Execute(
            PostBurnAction.ShutDown,
            recorder,
            soundPlayer: () => soundPlayed = true,
            shutdownInitiator: () => shutdownCalled = true);

        Assert.Equal(0, recorder.EjectCount);
        Assert.False(soundPlayed);
        Assert.True(shutdownCalled);
    }

    [Fact]
    public void PostBurnActionExecutor_ShutDown_WithoutCallback_DoesNotThrow()
    {
        var recorder = new FakeRecorder();

        var ex = Record.Exception(() =>
            PostBurnActionExecutor.Execute(PostBurnAction.ShutDown, recorder, soundPlayer: null, shutdownInitiator: null));

        Assert.Null(ex);
    }

    #endregion

    #region BurnQueueService Tests

    [Fact]
    public async Task BurnQueueService_EmptyQueue_ReturnsSuccessImmediately()
    {
        var engine = new FakeBurnEngine();
        var service = new BurnQueueService(engine);
        var recorder = new FakeRecorder();

        var result = await service.ExecuteQueueAsync(recorder, Array.Empty<BurnQueueItem>());

        Assert.True(result.Success);
        Assert.Equal(0, result.TotalItems);
        Assert.Equal(0, result.CompletedItems);
        Assert.Equal(0, result.FailedItems);
    }

    [Fact]
    public async Task BurnQueueService_SequentiallyBurns3Images_WithDiscSwapPromptCallback()
    {
        var engine = new FakeBurnEngine();
        var service = new BurnQueueService(engine);
        var recorder = new FakeRecorder();

        // Create 3 dummy ISO images
        string iso1 = Path.Combine(_testTempDir, "disc1.iso");
        string iso2 = Path.Combine(_testTempDir, "disc2.iso");
        string iso3 = Path.Combine(_testTempDir, "disc3.iso");
        byte[] dummyData = new byte[2048 * 4];
        await File.WriteAllBytesAsync(iso1, dummyData);
        await File.WriteAllBytesAsync(iso2, dummyData);
        await File.WriteAllBytesAsync(iso3, dummyData);

        var items = new List<BurnQueueItem>
        {
            new(iso1, WriteSpeedMultiplier: 16.0, VerifyAfterBurn: false),
            new(iso2, WriteSpeedMultiplier: 8.0, VerifyAfterBurn: false),
            new(iso3, WriteSpeedMultiplier: 4.0, VerifyAfterBurn: false)
        };

        var swapPromptIndices = new List<int>();
        var swapPromptItems = new List<BurnQueueItem>();

        var result = await service.ExecuteQueueAsync(
            recorder,
            items,
            promptForNextDiscAsync: (index, nextItem, ct) =>
            {
                swapPromptIndices.Add(index);
                swapPromptItems.Add(nextItem);
                return Task.FromResult(true);
            },
            postBurnAction: PostBurnAction.None);

        Assert.True(result.Success);
        Assert.Equal(3, result.TotalItems);
        Assert.Equal(3, result.CompletedItems);
        Assert.Equal(0, result.FailedItems);
        Assert.Equal(3, engine.BurnCount);

        // Verify disc swap prompts occurred before item 1 and item 2 (not before item 0)
        Assert.Equal(new[] { 1, 2 }, swapPromptIndices);
        Assert.Equal(iso2, swapPromptItems[0].ImagePath);
        Assert.Equal(iso3, swapPromptItems[1].ImagePath);

        // Verify eject media was called between discs (at least twice for swaps)
        Assert.True(recorder.EjectCount >= 2);

        // Verify individual item statuses
        Assert.All(items, itm => Assert.Equal(BurnQueueItemStatus.Completed, itm.Status));
        Assert.All(items, itm => Assert.NotNull(itm.BurnResult));
    }

    [Fact]
    public async Task BurnQueueService_WhenDiscSwapPromptDeclined_CancelsQueueAndSkipsRemaining()
    {
        var engine = new FakeBurnEngine();
        var service = new BurnQueueService(engine);
        var recorder = new FakeRecorder();

        string iso1 = Path.Combine(_testTempDir, "cancel1.iso");
        string iso2 = Path.Combine(_testTempDir, "cancel2.iso");
        string iso3 = Path.Combine(_testTempDir, "cancel3.iso");
        byte[] dummyData = new byte[2048 * 2];
        await File.WriteAllBytesAsync(iso1, dummyData);
        await File.WriteAllBytesAsync(iso2, dummyData);
        await File.WriteAllBytesAsync(iso3, dummyData);

        var items = new List<BurnQueueItem>
        {
            new(iso1, VerifyAfterBurn: false),
            new(iso2, VerifyAfterBurn: false),
            new(iso3, VerifyAfterBurn: false)
        };

        var result = await service.ExecuteQueueAsync(
            recorder,
            items,
            promptForNextDiscAsync: (index, nextItem, ct) =>
            {
                // Decline swap on item 1 (the 2nd disc)
                return Task.FromResult(false);
            });

        Assert.False(result.Success);
        Assert.Equal(3, result.TotalItems);
        Assert.Equal(1, result.CompletedItems);
        Assert.Equal(1, engine.BurnCount);

        Assert.Equal(BurnQueueItemStatus.Completed, items[0].Status);
        Assert.Equal(BurnQueueItemStatus.Cancelled, items[1].Status);
        Assert.Equal(BurnQueueItemStatus.Skipped, items[2].Status);
    }

    [Fact]
    public async Task BurnQueueService_ExecutesPostBurnAction_AfterQueueFinishes()
    {
        var engine = new FakeBurnEngine();
        var service = new BurnQueueService(engine);
        var recorder = new FakeRecorder();

        string iso1 = Path.Combine(_testTempDir, "action1.iso");
        await File.WriteAllBytesAsync(iso1, new byte[2048]);

        var items = new List<BurnQueueItem> { new(iso1, VerifyAfterBurn: false) };
        bool soundPlayed = false;

        var result = await service.ExecuteQueueAsync(
            recorder,
            items,
            postBurnAction: PostBurnAction.PlaySound,
            soundPlayer: () => soundPlayed = true);

        Assert.True(result.Success);
        Assert.True(soundPlayed);
    }

    #endregion
}
