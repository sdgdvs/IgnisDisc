using System.Buffers.Binary;
using System.Text;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.Burning;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Core.Utilities;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// Regression tests for behaviour that previously only worked (or silently pretended to work) on the virtual drive.
/// </summary>
public class PhysicalDriveSafetyTests
{
    private sealed class FakePhysicalRecorder : IDiscRecorderBackend
    {
        public MediaStatusDto Status { get; init; } = new() { MediaPresent = false };
        public string UniqueId => "PHYS-TEST";
        public string VendorId => "VENDOR";
        public string ProductId => "DRIVE";
        public string ProductRevision => "1.00";
        public string VolumePath => "Z:\\";
        public bool IsVirtual => false;
        public DriveCapabilitiesDto GetCapabilities() => new();
        public MediaStatusDto QueryMediaStatus() => Status;
        public void EjectMedia() { }
        public void CloseTray() { }
        public bool AcquireExclusiveAccess(string clientName) => true;
        public void ReleaseExclusiveAccess() { }
        public void Dispose() { }
    }

    private sealed class CapturingEngine : IBurnEngineBackend
    {
        public byte[]? Captured { get; private set; }
        public DataBurnOptions? CapturedOptions { get; private set; }

        public Task<BurnOperationResult> BurnDataStreamAsync(IDiscRecorderBackend recorder, Stream imageStream, DataBurnOptions options, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken)
        {
            CapturedOptions = options;
            using var ms = new MemoryStream();
            imageStream.CopyTo(ms);
            Captured = ms.ToArray();
            return Task.FromResult(BurnOperationResult.Succeeded(Captured.Length / 2048, TimeSpan.Zero));
        }

        public Task<BurnOperationResult> BurnAudioTracksAsync(IDiscRecorderBackend recorder, IReadOnlyList<AudioTrackSource> tracks, AudioBurnOptions options, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<BurnOperationResult> EraseMediaAsync(IDiscRecorderBackend recorder, bool fullErase, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<RipOperationResult> ReadSectorsAsync(IDiscRecorderBackend recorder, int startLba, int sectorCount, bool raw2352Mode, Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    [Fact]
    public void BookType_OnPhysicalDrive_ReportsUnsupportedInsteadOfFakeSuccess()
    {
        var recorder = new FakePhysicalRecorder
        {
            Status = new MediaStatusDto { MediaPresent = true, MediaType = DiscMediaType.DvdPlusR }
        };

        var result = new BookTypeService().SetBookType(recorder, DvdBookType.DvdRom);

        Assert.False(result.Success);
        Assert.Contains("not yet supported", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AppendingSession_LaysOutFileSystemAtNextWritableAddress()
    {
        const int nextWritable = 12_000;
        var recorder = new FakePhysicalRecorder
        {
            Status = new MediaStatusDto
            {
                MediaPresent = true,
                MediaType = DiscMediaType.CdR,
                IsBlank = false,
                IsAppendable = true,
                FreeSectors = 200_000,
                NextWritableLba = nextWritable
            }
        };

        var root = CompilationDirectory.CreateRoot();
        root.AddFile("NEW.TXT", Encoding.ASCII.GetBytes("second session"));

        var engine = new CapturingEngine();
        var service = new DataBurnService(engine);
        var result = await service.BurnCompilationAsync(recorder, root, new DataBurnOptions { CloseDisc = false });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(engine.Captured);

        // The Primary Volume Descriptor is 16 sectors into the session; its root directory extent must point
        // past the session start, not into the earlier session.
        var pvd = engine.Captured!.AsSpan(16 * 2048, 2048);
        Assert.Equal((byte)1, pvd[0]);
        Assert.Equal("CD001", Encoding.ASCII.GetString(pvd.Slice(1, 5)));
        uint rootExtentLba = BinaryPrimitives.ReadUInt32LittleEndian(pvd.Slice(156 + 2, 4));
        Assert.True(rootExtentLba > nextWritable, $"Root directory LBA {rootExtentLba} should be beyond session start {nextWritable}.");

        uint volumeSpace = BinaryPrimitives.ReadUInt32LittleEndian(pvd.Slice(80, 4));
        Assert.Equal((uint)(nextWritable + engine.Captured.Length / 2048), volumeSpace);
    }

    private static FakePhysicalRecorder UsedDvdPlusRw() => new()
    {
        // As reported by an HL-DT-ST GP60NB50 for a DVD+RW holding one 60 MB burn.
        Status = new MediaStatusDto
        {
            MediaPresent = true,
            MediaType = DiscMediaType.DvdPlusRw,
            IsBlank = false,
            IsRewritable = true,
            IsAppendable = false,
            IsOverwriteOnly = true,
            TotalSectors = 2_295_104,
            FreeSectors = 2_295_104,
            NextWritableLba = 0
        }
    };

    [Fact]
    public async Task UsedDvdPlusRw_WithoutPermission_IsNotOverwritten()
    {
        var root = CompilationDirectory.CreateRoot();
        root.AddFile("NEW.TXT", Encoding.ASCII.GetBytes("replacement"));
        var engine = new CapturingEngine();

        var result = await new DataBurnService(engine).BurnCompilationAsync(UsedDvdPlusRw(), root, new DataBurnOptions());

        Assert.False(result.Success);
        Assert.Equal(DataBurnService.ReplaceExistingDataMessage, result.ErrorMessage);
        Assert.Null(engine.Captured);
    }

    [Fact]
    public async Task UsedDvdPlusRw_WithPermission_IsRewrittenFromTheStart()
    {
        var root = CompilationDirectory.CreateRoot();
        root.AddFile("NEW.TXT", Encoding.ASCII.GetBytes("replacement"));
        var engine = new CapturingEngine();

        var result = await new DataBurnService(engine).BurnCompilationAsync(
            UsedDvdPlusRw(), root, new DataBurnOptions { OverwriteExistingData = true });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(engine.CapturedOptions!.OverwriteExistingData);

        // The new file system starts at LBA 0, not after the old contents.
        var pvd = engine.Captured!.AsSpan(16 * 2048, 2048);
        uint volumeSpace = BinaryPrimitives.ReadUInt32LittleEndian(pvd.Slice(80, 4));
        Assert.Equal((uint)(engine.Captured!.Length / 2048), volumeSpace);
    }

    private static FakePhysicalRecorder FinalizedCdRw() => new()
    {
        Status = new MediaStatusDto
        {
            MediaPresent = true,
            MediaType = DiscMediaType.CdRw,
            IsBlank = false,
            IsRewritable = true,
            IsAppendable = false,
            IsOverwriteOnly = false,
            TotalSectors = 359_847,
            FreeSectors = 0,
            UsedSectors = 1_200,
            SessionCount = 1
        }
    };

    [Fact]
    public async Task FinalizedCdRw_DataBurn_RefusesWithEraseRequired_EvenWithOverwriteFlag()
    {
        var recorder = FinalizedCdRw();
        Assert.True(recorder.QueryMediaStatus().RequiresEraseToWrite);
        Assert.False(recorder.QueryMediaStatus().HasDataThatBurningReplaces);

        var root = CompilationDirectory.CreateRoot();
        root.AddFile("NOTE.TXT", Encoding.ASCII.GetBytes("cannot overwrite sequential CD-RW in place"));
        var engine = new CapturingEngine();
        var service = new DataBurnService(engine);

        var res1 = await service.BurnCompilationAsync(recorder, root, new DataBurnOptions());
        Assert.False(res1.Success);
        Assert.Equal(DataBurnService.EraseRequiredMessage, res1.ErrorMessage);
        Assert.Null(engine.Captured);

        var res2 = await service.BurnCompilationAsync(recorder, root, new DataBurnOptions { OverwriteExistingData = true });
        Assert.False(res2.Success);
        Assert.Equal(DataBurnService.EraseRequiredMessage, res2.ErrorMessage);
        Assert.Null(engine.Captured);

        using var stream = new MemoryStream(new byte[32 * 2048]);
        var res3 = await service.BurnStreamAsync(recorder, stream, new DataBurnOptions { OverwriteExistingData = true });
        Assert.False(res3.Success);
        Assert.Equal(DataBurnService.EraseRequiredMessage, res3.ErrorMessage);
    }

    [Fact]
    public async Task FinalizedCdRw_AudioBurn_RefusesWithEraseRequired()
    {
        var recorder = FinalizedCdRw();
        var compilation = new AudioCdCompilation { AlbumTitle = "Test" };
        compilation.AddTrack(new AudioCdTrackItem(
            TrackNumber: 1,
            SourceFilePath: string.Empty,
            Title: "Tone",
            Performer: "Test",
            CustomPcmStreamFactory: () => new MemoryStream(new byte[300 * 2352])));

        var result = await new AudioCdBurnService(new CapturingEngine()).BurnAsync(recorder, compilation);

        Assert.False(result.Success);
        Assert.Equal(DataBurnService.EraseRequiredMessage, result.ErrorMessage);
    }

    private sealed class FakeTocPhysicalRecorder : IDiscRecorderBackend, IDiscTocProvider
    {
        public required MediaStatusDto Status { get; init; }
        public required IReadOnlyList<DiscTocTrack> Toc { get; init; }
        public string UniqueId => "PHYS-TOC";
        public string VendorId => "HL-DT-ST";
        public string ProductId => "DVDRAM GP60NB50";
        public string ProductRevision => "RF02";
        public string VolumePath => "H:\\";
        public bool IsVirtual => false;
        public DriveCapabilitiesDto GetCapabilities() => new();
        public MediaStatusDto QueryMediaStatus() => Status;
        public IReadOnlyList<DiscTocTrack> ReadTableOfContents() => Toc;
        public void EjectMedia() { }
        public void CloseTray() { }
        public bool AcquireExclusiveAccess(string clientName) => true;
        public void ReleaseExclusiveAccess() { }
        public void Dispose() { }
    }

    private sealed class StrictSessionReadEngine : IBurnEngineBackend
    {
        private readonly int _sessionStartLba;
        private readonly byte[] _sessionBytes;

        public StrictSessionReadEngine(int sessionStartLba, byte[] sessionBytes)
        {
            _sessionStartLba = sessionStartLba;
            _sessionBytes = sessionBytes;
        }

        public int SessionSectorCount => _sessionBytes.Length / OpticalConstants.DataSectorSize;

        public async Task<RipOperationResult> ReadSectorsAsync(
            IDiscRecorderBackend recorder,
            int startLba,
            int sectorCount,
            bool raw2352Mode,
            Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer,
            IProgress<BurnProgressSnapshot>? progress,
            CancellationToken cancellationToken)
        {
            for (int i = 0; i < sectorCount; i++)
            {
                int lba = startLba + i;
                int rel = lba - _sessionStartLba;
                if (rel < 0 || rel >= SessionSectorCount)
                {
                    // Simulate TAO run-out / unrecorded inter-session gap returning ILLEGAL MODE FOR THIS TRACK.
                    return new RipOperationResult
                    {
                        Success = false,
                        ErrorMessage = $"TAO run-out / gap reached at LBA {lba}",
                        SectorsRead = i,
                        BadSectorsCount = 1,
                        BadSectorsList = new[] { lba }
                    };
                }

                await sectorConsumer(lba, _sessionBytes.AsMemory(rel * 2048, 2048));
            }

            return RipOperationResult.Succeeded(sectorCount, TimeSpan.Zero);
        }

        public Task<BurnOperationResult> BurnDataStreamAsync(IDiscRecorderBackend recorder, Stream imageStream, DataBurnOptions options, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<BurnOperationResult> BurnAudioTracksAsync(IDiscRecorderBackend recorder, IReadOnlyList<AudioTrackSource> tracks, AudioBurnOptions options, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<BurnOperationResult> EraseMediaAsync(IDiscRecorderBackend recorder, bool fullErase, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    [Fact]
    public async Task MultiSessionImport_OnPhysicalTocDrive_ReadsLastDataTrackAndStopsAtPvdVolumeEnd()
    {
        const int session2StartLba = 11_802;
        var s2Root = CompilationDirectory.CreateRoot();
        s2Root.AddFile("KEEP_FROM_S1.TXT", "unused.txt", 42, importedStartLba: 28);
        s2Root.AddFile("ADDED_IN_S2.TXT", Encoding.UTF8.GetBytes("hello from session 2"));

        using var s2Image = IsoUdfImageBuilder.BuildToMemoryStream(
            s2Root,
            new IsoBuildOptions(VolumeLabel: "MS_TEST", IncludeIso9660: true, IncludeJoliet: true, IncludeUdf: false),
            sessionStartLba: session2StartLba);
        byte[] s2Bytes = s2Image.ToArray();
        int s2Sectors = s2Bytes.Length / 2048;

        var recorder = new FakeTocPhysicalRecorder
        {
            Status = new MediaStatusDto
            {
                MediaPresent = true,
                MediaType = DiscMediaType.CdRw,
                IsBlank = false,
                IsRewritable = true,
                IsAppendable = true,
                TotalSectors = 359_847,
                FreeSectors = 330_000,
                // Simulate READ CAPACITY returning more sectors (including post-gap / next session lead-in) than ISO VolumeSpaceSize!
                UsedSectors = session2StartLba + s2Sectors + 6_900,
                SessionCount = 2,
                NextWritableLba = session2StartLba + s2Sectors + 6_900
            },
            Toc = new[]
            {
                new DiscTocTrack(1, StartLba: 0, SectorCount: 300, IsAudio: false),
                new DiscTocTrack(2, StartLba: session2StartLba, SectorCount: s2Sectors + 2, IsAudio: false)
            }
        };

        var engine = new StrictSessionReadEngine(session2StartLba, s2Bytes);
        var imported = await MultiSessionManager.ImportFromRecorderAsync(recorder, engine);

        var importedFiles = imported.GetAllFilesRecursive().ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(28, importedFiles["KEEP_FROM_S1.TXT"].ImportedStartLba);
        Assert.True(importedFiles["ADDED_IN_S2.TXT"].ImportedStartLba > session2StartLba);
    }

    [Fact]
    public async Task SurfaceScan_OnMultiSessionTocDrive_SkipsInterSessionGapAndReportsZeroDamagedBlocks()
    {
        var recorder = new FakeTocPhysicalRecorder
        {
            Status = new MediaStatusDto
            {
                MediaPresent = true,
                MediaType = DiscMediaType.CdRw,
                IsBlank = false,
                IsRewritable = true,
                IsAppendable = false,
                TotalSectors = 360_000,
                UsedSectors = 13_932,
                SessionCount = 2
            },
            Toc = new[]
            {
                new DiscTocTrack(1, StartLba: 0, SectorCount: 2_230, IsAudio: false),
                new DiscTocTrack(2, StartLba: 13_630, SectorCount: 302, IsAudio: false)
            }
        };

        var engine = new MultiRangeReadEngine(new[] { (0, 2_230, false), (13_630, 302, false) });
        var report = await new SurfaceScanService(engine).ScanAsync(recorder);

        Assert.Equal(0, report.DamagedBlocks);
        Assert.Equal(2_230 + 302, report.TotalSectorsScanned);
        Assert.True(report.GoodBlocks > 0);
    }

    private sealed class MultiRangeReadEngine : IBurnEngineBackend
    {
        private readonly IReadOnlyList<(int Start, int Count, bool Raw)> _validRanges;

        public MultiRangeReadEngine(IReadOnlyList<(int Start, int Count, bool Raw)> validRanges) => _validRanges = validRanges;

        public async Task<RipOperationResult> ReadSectorsAsync(
            IDiscRecorderBackend recorder,
            int startLba,
            int sectorCount,
            bool raw2352Mode,
            Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer,
            IProgress<BurnProgressSnapshot>? progress,
            CancellationToken cancellationToken)
        {
            byte[] buf = new byte[raw2352Mode ? 2352 : 2048];
            for (int i = 0; i < sectorCount; i++)
            {
                int lba = startLba + i;
                bool ok = _validRanges.Any(r => r.Raw == raw2352Mode && lba >= r.Start && lba < r.Start + r.Count);
                if (!ok)
                {
                    return new RipOperationResult
                    {
                        Success = false,
                        ErrorMessage = $"Unrecorded inter-session gap at LBA {lba}",
                        SectorsRead = i,
                        BadSectorsCount = 1,
                        BadSectorsList = new[] { lba }
                    };
                }

                await sectorConsumer(lba, buf);
            }

            return RipOperationResult.Succeeded(sectorCount, TimeSpan.FromMilliseconds(5));
        }

        public Task<BurnOperationResult> BurnDataStreamAsync(IDiscRecorderBackend recorder, Stream imageStream, DataBurnOptions options, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<BurnOperationResult> BurnAudioTracksAsync(IDiscRecorderBackend recorder, IReadOnlyList<AudioTrackSource> tracks, AudioBurnOptions options, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<BurnOperationResult> EraseMediaAsync(IDiscRecorderBackend recorder, bool fullErase, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    [Fact]
    public void ParseFullToc_MultiSessionDataDisc_UsesPerSessionLeadOutBoundaries()
    {
        // Build a synthetic MMC Format 0x02 Full TOC response (4-byte header + four 11-byte descriptors):
        // Session 1: Track 1 (data, Start MSF 00:02:00 -> LBA 0), Point 0xA2 Lead-Out (MSF 00:31:55 -> LBA 2,230)
        // Session 2: Track 2 (data, Start MSF 03:03:55 -> LBA 13,630), Point 0xA2 Lead-Out (MSF 03:07:57 -> LBA 13,932)
        static byte[] Entry(byte session, byte adrCtrl, byte point, byte pm, byte ps, byte pf) =>
            new byte[] { session, adrCtrl, 0, point, 0, 0, 0, 0, pm, ps, pf };

        var entries = new List<byte>();
        entries.AddRange(Entry(1, 0x14, 0x01, 0, 2, 0));     // Track 1 @ LBA 0
        entries.AddRange(Entry(1, 0x14, 0xA2, 0, 31, 55));   // Session 1 Lead-Out @ LBA 2,230
        entries.AddRange(Entry(2, 0x14, 0x02, 3, 3, 55));    // Track 2 @ LBA 13,630
        entries.AddRange(Entry(2, 0x14, 0xA2, 3, 7, 57));    // Session 2 Lead-Out @ LBA 13,932

        int dataLen = 2 + entries.Count;
        byte[] resp = new byte[2 + dataLen];
        resp[0] = (byte)(dataLen >> 8);
        resp[1] = (byte)(dataLen & 0xFF);
        entries.CopyTo(resp, 4);

        var tracks = CdBurner.Hardware.Scsi.PhysicalSectorReader.ParseFullToc(resp, resp.Length);

        Assert.Equal(2, tracks.Count);
        Assert.Equal(new DiscTocTrack(1, StartLba: 0, SectorCount: 2_230, IsAudio: false), tracks[0]);
        Assert.Equal(new DiscTocTrack(2, StartLba: 13_630, SectorCount: 302, IsAudio: false), tracks[1]);
    }
}
