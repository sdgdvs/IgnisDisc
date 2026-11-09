using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Scsi;
using CdBurner.Hardware.Simulation;

namespace CdBurner.Hardware.NativeWriting;

/// <summary>
/// Burns audio CDs Session-at-Once with IgnisDisc's own drive commands instead of Windows' burning service. Opt-in
/// only, and in this version limited to test writes (laser off). Every run saves a full SCSI command log.
/// </summary>
public sealed class NativeAudioBurnEngine : IBurnEngineBackend
{
    private readonly string _logDirectory;

    /// <param name="logDirectory">Where command logs go; defaults to %LOCALAPPDATA%\IgnisDisc\logs.</param>
    public NativeAudioBurnEngine(string? logDirectory = null)
    {
        _logDirectory = logDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IgnisDisc", "logs");
    }

    /// <summary>The command log written by the last burn, if any.</summary>
    public string? LastLogPath { get; private set; }

    /// <summary>The writer's own result for the last burn.</summary>
    public NativeSaoWriteResult? LastResult { get; private set; }

    /// <summary>Builds the virtual drive's SCSI side for the disc in a virtual recorder.</summary>
    public static VirtualMmcDrive CreateVirtualDrive(VirtualDiscRecorder recorder)
    {
        var medium = recorder.CurrentMedium;
        var drive = new VirtualMmcDrive { MediaPresent = medium != null && !recorder.TrayOpen, CaptureWrites = false };
        if (medium != null)
        {
            drive.Profile = medium.MediaType switch
            {
                DiscMediaType.CdR => NativeSaoAudioWriter.ProfileCdR,
                DiscMediaType.CdRw => NativeSaoAudioWriter.ProfileCdRw,
                _ => 0x0011
            };
            drive.DiscStatus = medium.IsBlank ? 0 : medium.IsFinalized ? 2 : 1;
            drive.LastLeadOutStart = Msf.FromLba(Math.Max(0, medium.TotalCapacitySectors - 1));
        }
        return drive;
    }

    public Task<BurnOperationResult> BurnAudioTracksAsync(
        IDiscRecorderBackend recorder,
        IReadOnlyList<AudioTrackSource> tracks,
        AudioBurnOptions options,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(tracks);
        options ??= new AudioBurnOptions();
        LastLogPath = null;
        LastResult = null;

        if (!options.Simulate)
        {
            return Task.FromResult(BurnOperationResult.Failed(
                "Native writing only does test writes (laser off) in this version. Nothing was written to the disc."));
        }

        var saoTracks = new List<SaoTrack>(tracks.Count);
        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            int sectors = (int)((t.TotalPcmBytes + OpticalConstants.RawSectorSize - 1) / OpticalConstants.RawSectorSize);
            int preGap = i == 0 || options.UseDiscAtOnce
                ? Math.Max(0, t.PreGapFrames)
                : Math.Max(OpticalConstants.CdLeadInFrames, t.PreGapFrames); // Track-at-Once leaves 2-second gaps
            saoTracks.Add(new SaoTrack(i + 1, sectors, preGap, t.Isrc));
        }

        var request = new NativeSaoWriteRequest(
            saoTracks,
            i => OpenTrack(tracks[i]),
            options.WriteCdText && options.CdTextBytes is { Length: > 0 } ? options.CdTextBytes : null,
            MediaCatalogNumber: null,
            TestWrite: true);

        return Task.Run(() => Run(recorder, request, progress, cancellationToken), CancellationToken.None);
    }

    private BurnOperationResult Run(IDiscRecorderBackend recorder, NativeSaoWriteRequest request, IProgress<BurnProgressSnapshot>? progress, CancellationToken ct)
    {
        IScsiTransport device;
        IDisposable? exclusive = null;
        try
        {
            if (recorder is VirtualDiscRecorder virtualRecorder)
            {
                device = CreateVirtualDrive(virtualRecorder);
            }
            else
            {
                if (!OperatingSystem.IsWindows())
                {
                    return BurnOperationResult.Failed("Native writing needs Windows.");
                }
                var win32 = Win32ScsiTransport.Open(recorder.VolumePath);
                device = win32;
                try
                {
                    exclusive = CdromExclusiveAccess.Acquire(win32.Handle, "IgnisDisc");
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    win32.Dispose();
                    return BurnOperationResult.Failed($"{ex.Message} Nothing was written to the disc.", ex);
                }
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return BurnOperationResult.Failed($"Could not open the drive: {ex.Message} Nothing was written to the disc.", ex);
        }

        using var log = new LoggingScsiTransport(device, ownsInner: true);
        NativeSaoWriteResult result;
        try
        {
            log.Note($"IgnisDisc native Session-at-Once writer, test write. Drive: {recorder.VendorId} {recorder.ProductId} {recorder.ProductRevision} ({(recorder.IsVirtual ? "virtual" : "physical")}).");
            log.Note($"Tracks: {request.Tracks.Count}, CD-Text: {(request.CdTextPacks != null ? $"{request.CdTextPacks.Length / 18} packs" : "none")}.");
            result = new NativeSaoAudioWriter(log, log.Note).Write(request, progress, ct);
            log.Note($"Result: {(result.Success ? "success" : "failed")}: {result.Message}");
        }
        finally
        {
            exclusive?.Dispose();
            SaveLog(log);
        }

        LastResult = result;
        if (result.Success)
        {
            return BurnOperationResult.Succeeded(result.LeadInSectorsWritten + result.AudioSectorsWritten, result.Elapsed);
        }

        string message = result.StoppedBeforeWriting ? $"{result.Message} Nothing was written to the disc." : result.Message;
        if (LastLogPath != null)
        {
            message += $" Command log: {LastLogPath}";
        }
        return result.Cancelled ? BurnOperationResult.CancelledResult(message) : BurnOperationResult.Failed(message);
    }

    private void SaveLog(LoggingScsiTransport log)
    {
        try
        {
            string path = Path.Combine(_logDirectory, $"native-write-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            log.SaveTo(path);
            LastLogPath = path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastLogPath = null;
        }
    }

    private static Stream OpenTrack(AudioTrackSource track)
    {
        if (track.PcmStreamFactory != null)
        {
            return track.PcmStreamFactory();
        }
        if (track.PcmStream is { CanSeek: true } s)
        {
            s.Position = 0;
            return new NonClosingStream(s);
        }
        if (track.PcmStream != null)
        {
            return new NonClosingStream(track.PcmStream);
        }
        throw new IOException($"Track {track.TrackNumber} has no audio.");
    }

    public Task<BurnOperationResult> BurnDataStreamAsync(IDiscRecorderBackend recorder, Stream imageStream, DataBurnOptions options,
        IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken) =>
        Task.FromResult(BurnOperationResult.Failed("Native writing supports audio CDs only."));

    public Task<BurnOperationResult> EraseMediaAsync(IDiscRecorderBackend recorder, bool fullErase,
        IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken) =>
        Task.FromResult(BurnOperationResult.Failed("Native writing supports audio CDs only."));

    public Task<RipOperationResult> ReadSectorsAsync(IDiscRecorderBackend recorder, int startLba, int sectorCount, bool raw2352Mode,
        Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken) =>
        Task.FromResult(RipOperationResult.Failed("Native writing supports audio CDs only."));

    private sealed class NonClosingStream : Stream
    {
        private readonly Stream _inner;
        public NonClosingStream(Stream inner) => _inner = inner;
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _inner.Read(buffer);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
