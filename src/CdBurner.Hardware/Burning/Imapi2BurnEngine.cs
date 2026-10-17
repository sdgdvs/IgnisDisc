using System.Diagnostics;
using System.Runtime.InteropServices;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Interop;

namespace CdBurner.Hardware.Burning;

/// <summary>
/// Hardware burning backend executing against physical IMAPIv2 optical drives
/// via <see cref="IDiscFormat2Data"/>, <see cref="ManagedComStream"/>, and <see cref="DiscFormatDataEventsSink"/>
/// dispatched through <see cref="MtaBurnWorker"/> on a dedicated MTA thread.
/// </summary>
public class Imapi2BurnEngine : IBurnEngineBackend
{
    public const string DefaultClientName = "CdBurner";

    /// <summary>
    /// Client name used for IMAPIv2 exclusive drive locking.
    /// </summary>
    public string ClientName { get; set; } = DefaultClientName;

    /// <inheritdoc/>
    public async Task<BurnOperationResult> BurnDataStreamAsync(
        IDiscRecorderBackend recorder,
        Stream imageStream,
        DataBurnOptions options,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(imageStream);
        options ??= new DataBurnOptions();

        if (!OperatingSystem.IsWindows())
        {
            return BurnOperationResult.Failed("Physical burning via IMAPIv2 is only supported on Windows.");
        }

        return await MtaBurnWorker.RunBurnAsync((mtaProgress, token) =>
        {
            return ExecutePhysicalBurn(recorder, imageStream, options, mtaProgress, token);
        }, progress, cancellationToken);
    }

    private BurnOperationResult ExecutePhysicalBurn(
        IDiscRecorderBackend recorder,
        Stream imageStream,
        DataBurnOptions options,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IDiscFormat2Data? formatData = null;
        DiscFormatDataEventsSink? eventSink = null;
        ManagedComStream? comStream = null;
        IDiscRecorder2? comRecorder = null;
        bool createdComRecorder = false;
        bool acquiredLock = false;
        var sw = Stopwatch.StartNew();

        try
        {
            // 1. Resolve COM recorder
            if (recorder is ImapiDiscRecorder imapiRecorder && imapiRecorder.Recorder != null)
            {
                comRecorder = imapiRecorder.Recorder;
            }
            else
            {
                comRecorder = Imapi2ComFactory.CreateDiscRecorder2();
                if (comRecorder == null)
                {
                    return BurnOperationResult.Failed("Failed to instantiate MsftDiscRecorder2 COM object.");
                }
                comRecorder.InitializeDiscRecorder(recorder.UniqueId);
                createdComRecorder = true;
            }

            // Acquire exclusive access if needed
            acquiredLock = recorder.AcquireExclusiveAccess(ClientName);

            // 2. Create and configure MsftDiscFormat2Data
            formatData = Imapi2ComFactory.CreateDiscFormat2Data();
            if (formatData == null)
            {
                return BurnOperationResult.Failed("Failed to instantiate MsftDiscFormat2Data COM object.");
            }

            formatData.put_Recorder(comRecorder);
            formatData.put_ClientName(ClientName);

            // Buffer underrun free (BURN-Free)
            try
            {
                formatData.put_BufferUnderrunFreeDisabled(false);
            }
            catch
            {
            }

            // Close disc
            try
            {
                formatData.put_ForceMediaToBeClosed(options.CloseDisc);
            }
            catch
            {
            }

            // Write speed
            if (options.Speed != null && options.Speed.Value.BytesPerSecond > 0)
            {
                int sectorsPerSec = options.Speed.Value.BytesPerSecond / OpticalConstants.DataSectorSize;
                try
                {
                    formatData.SetWriteSpeed(sectorsPerSec, false);
                }
                catch
                {
                }
            }

            // 3. Connect event sink
            eventSink = new DiscFormatDataEventsSink(progress);
            eventSink.Connect(formatData);

            // 4. Wrap image stream in COM IStream
            comStream = new ManagedComStream(imageStream, leaveOpen: true);

            // 5. Register cancellation hook
            CancellationTokenRegistration reg = default;
            if (cancellationToken.CanBeCanceled)
            {
                reg = cancellationToken.Register(() =>
                {
                    try
                    {
                        formatData.CancelWrite();
                    }
                    catch
                    {
                    }
                });
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 6. Execute blocking COM write
                formatData.Write(comStream);
            }
            finally
            {
                reg.Dispose();
            }

            sw.Stop();

            long streamLength = imageStream.CanSeek ? imageStream.Length : 0;
            int sectorsWritten = OpticalConstants.BytesToSectors(streamLength, OpticalConstants.DataSectorSize);
            double speedMultiplier = options.Speed?.Multiplier > 0 ? options.Speed.Value.Multiplier : 16.0;

            if (options.EjectWhenDone || options.EjectAfterBurn)
            {
                try
                {
                    recorder.EjectMedia();
                }
                catch
                {
                }
            }

            return BurnOperationResult.Succeeded(sectorsWritten, sw.Elapsed, speedMultiplier);
        }
        catch (OperationCanceledException oce)
        {
            return BurnOperationResult.CancelledResult(oce.Message);
        }
        catch (COMException cex)
        {
            if (cancellationToken.IsCancellationRequested || (uint)cex.ErrorCode == 0xC0AA0406u)
            {
                return BurnOperationResult.CancelledResult("Burn operation was cancelled by user.");
            }
            return BurnOperationResult.Failed($"IMAPIv2 COM error (0x{cex.ErrorCode:X8}): {cex.Message}", cex);
        }
        catch (Exception ex)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return BurnOperationResult.CancelledResult("Burn operation was cancelled.");
            }
            return BurnOperationResult.Failed($"Data burning failed: {ex.Message}", ex);
        }
        finally
        {
            eventSink?.Dispose();
            comStream?.Dispose();

            if (formatData != null)
            {
                Imapi2ComFactory.FinalRelease(ref formatData);
            }

            if (createdComRecorder && comRecorder != null)
            {
                Imapi2ComFactory.FinalRelease(ref comRecorder);
            }

            if (acquiredLock)
            {
                try { recorder.ReleaseExclusiveAccess(); } catch { }
            }
        }
    }

    /// <inheritdoc/>
    public async Task<BurnOperationResult> BurnAudioTracksAsync(
        IDiscRecorderBackend recorder,
        IReadOnlyList<AudioTrackSource> tracks,
        AudioBurnOptions options,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(tracks);
        options ??= new AudioBurnOptions();

        if (!OperatingSystem.IsWindows())
        {
            return BurnOperationResult.Failed("Physical audio burning via IMAPIv2 is only supported on Windows.");
        }

        return await MtaBurnWorker.RunBurnAsync((mtaProgress, token) =>
        {
            return ExecutePhysicalAudioBurn(recorder, tracks, options, mtaProgress, token);
        }, progress, cancellationToken);
    }

    private BurnOperationResult ExecutePhysicalAudioBurn(
        IDiscRecorderBackend recorder,
        IReadOnlyList<AudioTrackSource> tracks,
        AudioBurnOptions options,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IDiscRecorder2? comRecorder = null;
        bool createdComRecorder = false;
        bool acquiredLock = false;
        IDiscFormat2TrackAtOnce? tao = null;
        IDiscFormat2RawCD? rawCd = null;
        var sw = Stopwatch.StartNew();

        try
        {
            if (recorder is ImapiDiscRecorder imapiRecorder && imapiRecorder.Recorder != null)
            {
                comRecorder = imapiRecorder.Recorder;
            }
            else
            {
                comRecorder = Imapi2ComFactory.CreateDiscRecorder2();
                if (comRecorder == null)
                {
                    return BurnOperationResult.Failed("Failed to instantiate MsftDiscRecorder2 COM object.");
                }
                comRecorder.InitializeDiscRecorder(recorder.UniqueId);
                createdComRecorder = true;
            }

            acquiredLock = recorder.AcquireExclusiveAccess(ClientName);

            if (options.UseDiscAtOnce)
            {
                rawCd = Imapi2ComFactory.CreateDiscFormat2RawCD();
                if (rawCd == null)
                {
                    return BurnOperationResult.Failed("Failed to instantiate MsftDiscFormat2RawCD COM object.");
                }

                rawCd.Recorder = comRecorder;
                rawCd.ClientName = ClientName;

                if (options.Speed != null && options.Speed.Value.BytesPerSecond > 0)
                {
                    int sectorsPerSec = options.Speed.Value.BytesPerSecond / OpticalConstants.RawSectorSize;
                    try { rawCd.SetWriteSpeed(sectorsPerSec, false); } catch { }
                }

                rawCd.PrepareMedia();

                using var compositeStream = new CompositeAudioStream(tracks);
                using var comStream = new ManagedComStream(compositeStream, leaveOpen: true);
                rawCd.WriteMedia(comStream);
                rawCd.ReleaseMedia();
            }
            else
            {
                tao = Imapi2ComFactory.CreateDiscFormat2TrackAtOnce();
                if (tao == null)
                {
                    return BurnOperationResult.Failed("Failed to instantiate MsftDiscFormat2TrackAtOnce COM object.");
                }

                tao.Recorder = comRecorder;
                tao.ClientName = ClientName;

                if (options.Speed != null && options.Speed.Value.BytesPerSecond > 0)
                {
                    int sectorsPerSec = options.Speed.Value.BytesPerSecond / OpticalConstants.RawSectorSize;
                    try { tao.SetWriteSpeed(sectorsPerSec, false); } catch { }
                }

                tao.PrepareMedia();

                foreach (var trk in tracks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Stream? s = trk.PcmStream ?? (trk.PcmStreamFactory != null ? trk.PcmStreamFactory() : (!string.IsNullOrEmpty(trk.FilePath) ? File.OpenRead(trk.FilePath) : null));
                    if (s == null) continue;

                    using (s)
                    using (var comStream = new ManagedComStream(s, leaveOpen: true))
                    {
                        tao.AddAudioTrack(comStream);
                    }
                }

                tao.ReleaseMedia();
            }

            sw.Stop();
            int totalSectors = tracks.Sum(t => OpticalConstants.BytesToSectors(t.TotalPcmBytes, OpticalConstants.RawSectorSize));
            double speedMultiplier = options.Speed?.Multiplier > 0 ? options.Speed.Value.Multiplier : 16.0;

            if (options.EjectWhenDone)
            {
                try { recorder.EjectMedia(); } catch { }
            }

            return BurnOperationResult.Succeeded(totalSectors, sw.Elapsed, speedMultiplier);
        }
        catch (OperationCanceledException oce)
        {
            return BurnOperationResult.CancelledResult(oce.Message);
        }
        catch (COMException cex)
        {
            if (cancellationToken.IsCancellationRequested || (uint)cex.ErrorCode == 0xC0AA0406u)
            {
                return BurnOperationResult.CancelledResult("Burn operation was cancelled by user.");
            }
            return BurnOperationResult.Failed($"IMAPIv2 COM error (0x{cex.ErrorCode:X8}): {cex.Message}", cex);
        }
        catch (Exception ex)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return BurnOperationResult.CancelledResult("Burn operation was cancelled.");
            }
            return BurnOperationResult.Failed($"Audio burning failed: {ex.Message}", ex);
        }
        finally
        {
            if (tao != null) Imapi2ComFactory.FinalRelease(ref tao);
            if (rawCd != null) Imapi2ComFactory.FinalRelease(ref rawCd);
            if (createdComRecorder && comRecorder != null) Imapi2ComFactory.FinalRelease(ref comRecorder);
            if (acquiredLock)
            {
                try { recorder.ReleaseExclusiveAccess(); } catch { }
            }
        }
    }

    /// <inheritdoc/>
    public async Task<BurnOperationResult> EraseMediaAsync(
        IDiscRecorderBackend recorder,
        bool fullErase,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recorder);

        if (!OperatingSystem.IsWindows())
        {
            return BurnOperationResult.Failed("Physical media erasing via IMAPIv2 is only supported on Windows.");
        }

        return await MtaBurnWorker.RunBurnAsync((mtaProgress, token) =>
        {
            token.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();

            IDiscFormat2Erase? erase = null;
            try
            {
                erase = Imapi2ComFactory.CreateDiscFormat2Erase();
                if (erase == null)
                {
                    return BurnOperationResult.Failed("Failed to create MsftDiscFormat2Erase COM object.");
                }

                IDiscRecorder2? comRecorder = (recorder as ImapiDiscRecorder)?.Recorder;
                if (comRecorder == null)
                {
                    comRecorder = Imapi2ComFactory.CreateDiscRecorder2();
                    comRecorder?.InitializeDiscRecorder(recorder.UniqueId);
                }

                if (comRecorder == null)
                {
                    return BurnOperationResult.Failed("Failed to resolve physical recorder COM instance.");
                }

                erase.put_Recorder(comRecorder);
                erase.put_ClientName(ClientName);
                erase.put_FullErase(fullErase);

                token.ThrowIfCancellationRequested();
                erase.EraseMedia();

                return BurnOperationResult.Succeeded(0, sw.Elapsed);
            }
            catch (Exception ex)
            {
                return BurnOperationResult.Failed($"Erase failed: {ex.Message}", ex);
            }
            finally
            {
                if (erase != null)
                {
                    Imapi2ComFactory.FinalRelease(ref erase);
                }
            }
        }, progress, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<RipOperationResult> ReadSectorsAsync(
        IDiscRecorderBackend recorder,
        int startLba,
        int sectorCount,
        bool raw2352Mode,
        Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(sectorConsumer);

        return await Task.FromResult(RipOperationResult.Failed("Direct sector reading on physical recorder is supported via SCSI pass-through."));
    }
}

/// <summary>
/// Alias for <see cref="Imapi2BurnEngine"/>.
/// </summary>
public class PhysicalImapiBurnEngine : Imapi2BurnEngine
{
}

/// <summary>
/// Composite stream concatenating multiple audio tracks sequentially for raw DAO burning.
/// </summary>
internal sealed class CompositeAudioStream : Stream
{
    private readonly IReadOnlyList<AudioTrackSource> _tracks;
    private int _currentIndex;
    private Stream? _currentStream;
    private long _position;
    private readonly long _totalLength;

    public CompositeAudioStream(IReadOnlyList<AudioTrackSource> tracks)
    {
        _tracks = tracks ?? Array.Empty<AudioTrackSource>();
        _totalLength = _tracks.Sum(t => t.TotalPcmBytes);
        OpenTrack(0);
    }

    private void OpenTrack(int index)
    {
        _currentStream?.Dispose();
        _currentStream = null;
        _currentIndex = index;

        if (index < _tracks.Count)
        {
            var trk = _tracks[index];
            _currentStream = trk.PcmStream ?? (trk.PcmStreamFactory != null ? trk.PcmStreamFactory() : (!string.IsNullOrEmpty(trk.FilePath) && File.Exists(trk.FilePath) ? File.OpenRead(trk.FilePath) : null));
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _totalLength;
    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int totalRead = 0;
        while (count > 0 && _currentIndex < _tracks.Count)
        {
            if (_currentStream == null)
            {
                OpenTrack(_currentIndex + 1);
                continue;
            }

            int read = _currentStream.Read(buffer, offset, count);
            if (read == 0)
            {
                OpenTrack(_currentIndex + 1);
            }
            else
            {
                totalRead += read;
                offset += read;
                count -= read;
                _position += read;
            }
        }
        return totalRead;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _currentStream?.Dispose();
            _currentStream = null;
        }
        base.Dispose(disposing);
    }
}

