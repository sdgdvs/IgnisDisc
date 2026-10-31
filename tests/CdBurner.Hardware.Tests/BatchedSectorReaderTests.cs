using CdBurner.Core.Abstractions;
using CdBurner.Core.Images;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// Unit tests for BatchedSectorReader.
/// Verifies batch reading of sectors with fault isolation, retry handling, cancellation,
/// raw 2352 mode, and invalid argument guards.
/// </summary>
public class BatchedSectorReaderTests
{
    #region Test Doubles

    private sealed class CallCountingEngine : IBurnEngineBackend
    {
        private readonly IBurnEngineBackend _inner;
        public List<(int StartLba, int SectorCount)> Calls { get; } = new();

        public CallCountingEngine(IBurnEngineBackend inner)
        {
            _inner = inner;
        }

        public Task<BurnOperationResult> BurnDataStreamAsync(
            IDiscRecorderBackend recorder,
            Stream imageStream,
            DataBurnOptions options,
            IProgress<BurnProgressSnapshot>? progress,
            CancellationToken cancellationToken)
            => _inner.BurnDataStreamAsync(recorder, imageStream, options, progress, cancellationToken);

        public Task<BurnOperationResult> BurnAudioTracksAsync(
            IDiscRecorderBackend recorder,
            IReadOnlyList<AudioTrackSource> tracks,
            AudioBurnOptions options,
            IProgress<BurnProgressSnapshot>? progress,
            CancellationToken cancellationToken)
            => _inner.BurnAudioTracksAsync(recorder, tracks, options, progress, cancellationToken);

        public Task<BurnOperationResult> EraseMediaAsync(
            IDiscRecorderBackend recorder,
            bool fullErase,
            IProgress<BurnProgressSnapshot>? progress,
            CancellationToken cancellationToken)
            => _inner.EraseMediaAsync(recorder, fullErase, progress, cancellationToken);

        public Task<RipOperationResult> ReadSectorsAsync(
            IDiscRecorderBackend recorder,
            int startLba,
            int sectorCount,
            bool raw2352Mode,
            Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer,
            IProgress<BurnProgressSnapshot>? progress,
            CancellationToken cancellationToken)
        {
            Calls.Add((startLba, sectorCount));
            return _inner.ReadSectorsAsync(recorder, startLba, sectorCount, raw2352Mode, sectorConsumer, progress, cancellationToken);
        }
    }

    private sealed class FlakyEngine : IBurnEngineBackend
    {
        private readonly IBurnEngineBackend _inner;
        private readonly int _flakyLba;
        private int _failuresRemaining;

        public FlakyEngine(IBurnEngineBackend inner, int flakyLba, int failuresBeforeSuccess)
        {
            _inner = inner;
            _flakyLba = flakyLba;
            _failuresRemaining = failuresBeforeSuccess;
        }

        public Task<BurnOperationResult> BurnDataStreamAsync(
            IDiscRecorderBackend recorder,
            Stream imageStream,
            DataBurnOptions options,
            IProgress<BurnProgressSnapshot>? progress,
            CancellationToken cancellationToken)
            => _inner.BurnDataStreamAsync(recorder, imageStream, options, progress, cancellationToken);

        public Task<BurnOperationResult> BurnAudioTracksAsync(
            IDiscRecorderBackend recorder,
            IReadOnlyList<AudioTrackSource> tracks,
            AudioBurnOptions options,
            IProgress<BurnProgressSnapshot>? progress,
            CancellationToken cancellationToken)
            => _inner.BurnAudioTracksAsync(recorder, tracks, options, progress, cancellationToken);

        public Task<BurnOperationResult> EraseMediaAsync(
            IDiscRecorderBackend recorder,
            bool fullErase,
            IProgress<BurnProgressSnapshot>? progress,
            CancellationToken cancellationToken)
            => _inner.EraseMediaAsync(recorder, fullErase, progress, cancellationToken);

        public async Task<RipOperationResult> ReadSectorsAsync(
            IDiscRecorderBackend recorder,
            int startLba,
            int sectorCount,
            bool raw2352Mode,
            Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer,
            IProgress<BurnProgressSnapshot>? progress,
            CancellationToken cancellationToken)
        {
            bool containsFlakyLba = _flakyLba >= startLba && _flakyLba < startLba + sectorCount;
            if (containsFlakyLba && _failuresRemaining > 0)
            {
                _failuresRemaining--;
                int sectorsBefore = _flakyLba - startLba;
                if (sectorsBefore > 0)
                {
                    await _inner.ReadSectorsAsync(recorder, startLba, sectorsBefore, raw2352Mode, sectorConsumer, progress, cancellationToken);
                }

                return new RipOperationResult
                {
                    Success = false,
                    ErrorMessage = "simulated transient error",
                    SectorsRead = sectorsBefore,
                    BadSectorsCount = 1,
                    BadSectorsList = new[] { _flakyLba }
                };
            }

            return await _inner.ReadSectorsAsync(recorder, startLba, sectorCount, raw2352Mode, sectorConsumer, progress, cancellationToken);
        }
    }

    #endregion

    #region Test 1: All Sectors Good

    [Fact]
    public async Task ReadRange_AllSectorsGood_DeliversEachSectorOnceInOrder_UsingBatches()
    {
        const int totalSectors = 1300;

        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        byte[] writeBuffer = new byte[OpticalConstants.DataSectorSize];
        for (int lba = 0; lba < totalSectors; lba++)
        {
            Array.Fill(writeBuffer, (byte)(lba % 251 + 1));
            medium.WriteSector(lba, writeBuffer);
        }

        var baseEngine = new VirtualBurnEngine();
        var countingEngine = new CallCountingEngine(baseEngine);

        var deliveredLbas = new List<int>();
        var deliveredData = new List<byte[]>();
        bool onUnreadableCalled = false;
        var progressReports = new List<int>();

        var outcome = await BatchedSectorReader.ReadRangeAsync(
            countingEngine,
            recorder,
            startLba: 0,
            sectorCount: totalSectors,
            raw2352Mode: false,
            maxRetriesPerSector: 3,
            onSector: (lba, data) =>
            {
                deliveredLbas.Add(lba);
                deliveredData.Add(data.ToArray());
                return ValueTask.CompletedTask;
            },
            onUnreadable: _ =>
            {
                onUnreadableCalled = true;
                return ValueTask.FromResult(true);
            },
            onProgress: p => progressReports.Add(p),
            batchSectors: 512,
            cancellationToken: CancellationToken.None);

        Assert.True(outcome.Completed);
        Assert.Null(outcome.StoppedAtLba);
        Assert.Equal(0, outcome.TotalRetries);
        Assert.Equal(Enumerable.Range(0, totalSectors), deliveredLbas);
        Assert.Equal(totalSectors, deliveredData.Count);

        for (int lba = 0; lba < totalSectors; lba++)
        {
            byte expectedByte = (byte)(lba % 251 + 1);
            Assert.Equal(OpticalConstants.DataSectorSize, deliveredData[lba].Length);
            Assert.All(deliveredData[lba], b => Assert.Equal(expectedByte, b));
        }

        Assert.Equal(new (int, int)[] { (0, 512), (512, 512), (1024, 276) }, countingEngine.Calls);
        Assert.False(onUnreadableCalled);
        Assert.NotEmpty(progressReports);
        Assert.Equal(totalSectors, progressReports.Last());

        for (int i = 1; i < progressReports.Count; i++)
        {
            Assert.True(progressReports[i] > progressReports[i - 1]);
        }
    }

    #endregion

    #region Test 2: Permanent Bad Sector - Continue Past It

    [Fact]
    public async Task ReadRange_PermanentBadSector_ContinuePastIt_RetriesAndReportsOnce()
    {
        const int totalSectors = 64;
        const int badLba = 20;
        const int batchSectors = 16;
        const int maxRetries = 3;

        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        byte[] buffer = new byte[OpticalConstants.DataSectorSize];
        for (int lba = 0; lba < totalSectors; lba++)
        {
            Array.Fill(buffer, (byte)(lba % 251 + 1));
            medium.WriteSector(lba, buffer);
        }
        medium.SimulatedBadLbas.Add(badLba);

        var baseEngine = new VirtualBurnEngine();
        var countingEngine = new CallCountingEngine(baseEngine);

        var deliveredLbas = new List<int>();
        var unreadableCalls = new List<int>();

        var outcome = await BatchedSectorReader.ReadRangeAsync(
            countingEngine,
            recorder,
            startLba: 0,
            sectorCount: totalSectors,
            raw2352Mode: false,
            maxRetriesPerSector: maxRetries,
            onSector: (lba, data) =>
            {
                deliveredLbas.Add(lba);
                return ValueTask.CompletedTask;
            },
            onUnreadable: lba =>
            {
                unreadableCalls.Add(lba);
                return ValueTask.FromResult(true);
            },
            batchSectors: batchSectors,
            cancellationToken: CancellationToken.None);

        Assert.Single(unreadableCalls);
        Assert.Equal(badLba, unreadableCalls[0]);
        Assert.Equal(Enumerable.Range(0, totalSectors).Where(lba => lba != badLba), deliveredLbas);
        Assert.Equal(maxRetries, outcome.TotalRetries);
        Assert.True(outcome.Completed);
        Assert.Null(outcome.StoppedAtLba);
        Assert.Equal(3, countingEngine.Calls.Count(c => c == (badLba, 1)));
    }

    #endregion

    #region Test 3: Permanent Bad Sector - Stop Requested

    [Fact]
    public async Task ReadRange_PermanentBadSector_StopRequested_ReturnsStoppedAtLba()
    {
        const int totalSectors = 64;
        const int badLba = 20;

        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        byte[] buffer = new byte[OpticalConstants.DataSectorSize];
        for (int lba = 0; lba < totalSectors; lba++)
        {
            Array.Fill(buffer, (byte)(lba % 251 + 1));
            medium.WriteSector(lba, buffer);
        }
        medium.SimulatedBadLbas.Add(badLba);

        var engine = new VirtualBurnEngine();
        var deliveredLbas = new List<int>();

        var outcome = await BatchedSectorReader.ReadRangeAsync(
            engine,
            recorder,
            startLba: 0,
            sectorCount: totalSectors,
            raw2352Mode: false,
            maxRetriesPerSector: 3,
            onSector: (lba, data) =>
            {
                deliveredLbas.Add(lba);
                return ValueTask.CompletedTask;
            },
            onUnreadable: _ => ValueTask.FromResult(false),
            cancellationToken: CancellationToken.None);

        Assert.False(outcome.Completed);
        Assert.Equal(badLba, outcome.StoppedAtLba);
        Assert.Equal(Enumerable.Range(0, badLba), deliveredLbas);
    }

    #endregion

    #region Test 4: Transient Bad Sector - Recovers On Retry

    [Fact]
    public async Task ReadRange_TransientBadSector_RecoversOnRetry_WithoutReportingUnreadable()
    {
        const int totalSectors = 64;
        const int flakyLba = 10;

        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        byte[] buffer = new byte[OpticalConstants.DataSectorSize];
        for (int lba = 0; lba < totalSectors; lba++)
        {
            Array.Fill(buffer, (byte)(lba % 251 + 1));
            medium.WriteSector(lba, buffer);
        }

        var flakyEngine = new FlakyEngine(new VirtualBurnEngine(), flakyLba: flakyLba, failuresBeforeSuccess: 2);

        var deliveredLbas = new List<int>();
        var deliveredData = new List<byte[]>();
        bool onUnreadableCalled = false;

        var outcome = await BatchedSectorReader.ReadRangeAsync(
            flakyEngine,
            recorder,
            startLba: 0,
            sectorCount: totalSectors,
            raw2352Mode: false,
            maxRetriesPerSector: 3,
            onSector: (lba, data) =>
            {
                deliveredLbas.Add(lba);
                deliveredData.Add(data.ToArray());
                return ValueTask.CompletedTask;
            },
            onUnreadable: _ =>
            {
                onUnreadableCalled = true;
                return ValueTask.FromResult(true);
            },
            cancellationToken: CancellationToken.None);

        // the batch read consumes failure 1, single-sector retry #1 consumes failure 2, retry #2 succeeds
        Assert.False(onUnreadableCalled);
        Assert.Equal(Enumerable.Range(0, totalSectors), deliveredLbas);
        Assert.Equal(totalSectors, deliveredData.Count);

        for (int lba = 0; lba < totalSectors; lba++)
        {
            byte expectedByte = (byte)(lba % 251 + 1);
            Assert.All(deliveredData[lba], b => Assert.Equal(expectedByte, b));
        }

        Assert.Equal(2, outcome.TotalRetries);
        Assert.True(outcome.Completed);
        Assert.Null(outcome.StoppedAtLba);
    }

    #endregion

    #region Test 5: Engine Throws On Bad Sector

    [Fact]
    public async Task ReadRange_EngineThrowsOnBadSector_IsTreatedLikeAFailedRead()
    {
        const int totalSectors = 64;
        const int badLba = 20;
        const int batchSectors = 16;
        const int maxRetries = 3;

        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        byte[] buffer = new byte[OpticalConstants.DataSectorSize];
        for (int lba = 0; lba < totalSectors; lba++)
        {
            Array.Fill(buffer, (byte)(lba % 251 + 1));
            medium.WriteSector(lba, buffer);
        }
        medium.SimulatedBadLbas.Add(badLba);

        var baseEngine = new VirtualBurnEngine { ThrowOnReadError = true };
        var countingEngine = new CallCountingEngine(baseEngine);

        var deliveredLbas = new List<int>();
        var unreadableCalls = new List<int>();

        var outcome = await BatchedSectorReader.ReadRangeAsync(
            countingEngine,
            recorder,
            startLba: 0,
            sectorCount: totalSectors,
            raw2352Mode: false,
            maxRetriesPerSector: maxRetries,
            onSector: (lba, data) =>
            {
                deliveredLbas.Add(lba);
                return ValueTask.CompletedTask;
            },
            onUnreadable: lba =>
            {
                unreadableCalls.Add(lba);
                return ValueTask.FromResult(true);
            },
            batchSectors: batchSectors,
            cancellationToken: CancellationToken.None);

        Assert.Single(unreadableCalls);
        Assert.Equal(badLba, unreadableCalls[0]);
        Assert.Equal(Enumerable.Range(0, totalSectors).Where(lba => lba != badLba), deliveredLbas);
        Assert.Equal(maxRetries, outcome.TotalRetries);
        Assert.True(outcome.Completed);
        Assert.Null(outcome.StoppedAtLba);
        Assert.Equal(3, countingEngine.Calls.Count(c => c == (badLba, 1)));
    }

    #endregion

    #region Test 6: Cancelled Range Read

    [Fact]
    public async Task ReadRange_Cancelled_ThrowsOperationCanceledException()
    {
        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);
        var engine = new VirtualBurnEngine();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BatchedSectorReader.ReadRangeAsync(
                engine,
                recorder,
                startLba: 0,
                sectorCount: 16,
                raw2352Mode: false,
                maxRetriesPerSector: 3,
                onSector: (_, _) => ValueTask.CompletedTask,
                onUnreadable: _ => ValueTask.FromResult(true),
                cancellationToken: cts.Token));
    }

    #endregion

    #region Test 7: Raw 2352 Mode

    [Fact]
    public async Task ReadRange_Raw2352Mode_DeliversRawSizedSectors()
    {
        const int sectorCount = 8;

        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        medium.Raw2352Mode = true;
        recorder.InsertMedia(medium);

        var writtenBuffers = new List<byte[]>();
        for (int lba = 0; lba < sectorCount; lba++)
        {
            byte[] rawBuffer = new byte[OpticalConstants.RawSectorSize];
            Array.Fill(rawBuffer, (byte)(lba * 17 + 1));
            medium.WriteSector(lba, rawBuffer, OpticalConstants.RawSectorSize);
            writtenBuffers.Add(rawBuffer);
        }

        var engine = new VirtualBurnEngine();
        var deliveredBuffers = new List<byte[]>();

        var outcome = await BatchedSectorReader.ReadRangeAsync(
            engine,
            recorder,
            startLba: 0,
            sectorCount: sectorCount,
            raw2352Mode: true,
            maxRetriesPerSector: 3,
            onSector: (lba, data) =>
            {
                deliveredBuffers.Add(data.ToArray());
                return ValueTask.CompletedTask;
            },
            onUnreadable: _ => ValueTask.FromResult(true),
            cancellationToken: CancellationToken.None);

        Assert.True(outcome.Completed);
        Assert.Equal(sectorCount, deliveredBuffers.Count);
        Assert.All(deliveredBuffers, buf => Assert.Equal(OpticalConstants.RawSectorSize, buf.Length));

        for (int i = 0; i < sectorCount; i++)
        {
            Assert.Equal(writtenBuffers[i], deliveredBuffers[i]);
        }
    }

    #endregion

    #region Test 8: Invalid Batch Size Guards

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task ReadRange_InvalidBatchSize_Throws(int batchSectors)
    {
        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);
        var engine = new VirtualBurnEngine();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            BatchedSectorReader.ReadRangeAsync(
                engine,
                recorder,
                startLba: 0,
                sectorCount: 10,
                raw2352Mode: false,
                maxRetriesPerSector: 3,
                onSector: (_, _) => ValueTask.CompletedTask,
                onUnreadable: _ => ValueTask.FromResult(true),
                batchSectors: batchSectors));
    }

    #endregion
}
