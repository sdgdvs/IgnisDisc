using System.Buffers.Binary;
using System.Text;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Burning;
using CdBurner.Core.Compilation;
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

        public Task<BurnOperationResult> BurnDataStreamAsync(IDiscRecorderBackend recorder, Stream imageStream, DataBurnOptions options, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken)
        {
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
}
