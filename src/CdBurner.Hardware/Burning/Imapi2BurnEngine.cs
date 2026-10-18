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
        IRawCDImageCreator? rawCreator = null;
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

            // Compute total padded Red Book bytes and sectors across all tracks
            const long MinRedBookTrackBytes = 300L * OpticalConstants.RawSectorSize; // 4 seconds (300 frames) minimum per Red Book standard
            long totalPaddedBytes = 0;
            foreach (var t in tracks)
            {
                long rawBytes = Math.Max(0L, t.TotalPcmBytes);
                long aligned = ((rawBytes + OpticalConstants.RawSectorSize - 1) / OpticalConstants.RawSectorSize) * OpticalConstants.RawSectorSize;
                totalPaddedBytes += Math.Max(MinRedBookTrackBytes, aligned);
            }
            int totalSectors = Math.Max(1, OpticalConstants.BytesToSectors(totalPaddedBytes, OpticalConstants.RawSectorSize));
            long bytesWritten = 0;

            void ReportAudioProgress(int bytesDelta, string statusMsg)
            {
                if (progress == null) return;
                long currentBytes = Interlocked.Add(ref bytesWritten, bytesDelta);
                int currentSector = Math.Clamp(OpticalConstants.BytesToSectors(currentBytes, OpticalConstants.RawSectorSize), 0, totalSectors);
                TimeSpan elapsed = sw.Elapsed;
                TimeSpan remaining = currentSector > 0 && currentSector < totalSectors
                    ? TimeSpan.FromSeconds(elapsed.TotalSeconds * (totalSectors - currentSector) / currentSector)
                    : TimeSpan.Zero;
                double speedMult = elapsed.TotalSeconds > 0.1
                    ? (currentBytes / elapsed.TotalSeconds) / (75.0 * OpticalConstants.RawSectorSize)
                    : (options.Speed?.Multiplier > 0 ? options.Speed.Value.Multiplier : 16.0);

                progress.Report(new BurnProgressSnapshot
                {
                    ElapsedTime = elapsed,
                    RemainingTime = remaining,
                    CurrentLba = currentSector,
                    TotalLba = totalSectors,
                    BufferLevelPercent = 95.0,
                    CurrentSpeedMultiplier = speedMult,
                    CurrentBytesPerSecond = (long)(speedMult * 75 * OpticalConstants.RawSectorSize),
                    CurrentActionDescription = statusMsg
                });
            }

            ReportAudioProgress(0, "Preparing optical drive and laser calibration for Audio CD...");

            bool completedViaDao = false;

            if (options.UseDiscAtOnce)
            {
                rawCd = Imapi2ComFactory.CreateDiscFormat2RawCD();
                rawCreator = Imapi2ComFactory.CreateRawCDImageCreator();

                if (rawCd != null && rawCreator != null)
                {
                    var disposables = new List<IDisposable>();
                    System.Runtime.InteropServices.ComTypes.IStream? resultImageStream = null;
                    try
                    {
                        rawCd.put_Recorder(comRecorder);
                        rawCd.put_ClientName(ClientName);

                        try { rawCd.put_BufferUnderrunFreeDisabled(false); } catch { }

                        for (int i = 0; i < tracks.Count; i++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var trk = tracks[i];
                            Stream? rawStream = OpenAudioTrackStream(trk);
                            if (rawStream == null) continue;

                            var paddedStream = new RedBookPaddedStream(rawStream, leaveOpen: false);
                            disposables.Add(paddedStream);

                            int trackNum = i + 1;
                            var trackComStream = new ManagedComStream(
                                paddedStream,
                                leaveOpen: true,
                                bytesRead => ReportAudioProgress(bytesRead, $"Writing Audio CD (DAO) Track {trackNum}/{tracks.Count}..."));
                            disposables.Add(trackComStream);

                            rawCreator.AddTrack(0 /* IMAPI_CD_SECTOR_AUDIO */, trackComStream);
                        }

                        resultImageStream = rawCreator.CreateResultImage();
                        ImapiFormat2RawCdDataType resultingSectorType = rawCreator.get_ResultingImageType();

                        // PrepareMedia MUST be called before put_RequestedSectorType and SetWriteSpeed
                        rawCd.PrepareMedia();

                        try { rawCd.put_RequestedSectorType(resultingSectorType); } catch { }

                        if (options.Speed != null && options.Speed.Value.BytesPerSecond > 0)
                        {
                            int sectorsPerSec = Math.Max(1, options.Speed.Value.BytesPerSecond / OpticalConstants.RawSectorSize);
                            try { rawCd.SetWriteSpeed(sectorsPerSec, false); } catch { }
                        }

                        CancellationTokenRegistration daoReg = default;
                        if (cancellationToken.CanBeCanceled)
                        {
                            daoReg = cancellationToken.Register(() =>
                            {
                                try { rawCd.CancelWrite(); } catch { }
                            });
                        }

                        bool writeStarted = false;
                        try
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            writeStarted = true;
                            ReportAudioProgress(0, $"Writing Audio CD (Disc-At-Once) to {recorder.VolumePath}...");
                            rawCd.WriteMedia(resultImageStream);
                            ReportAudioProgress(0, "Finalizing Audio CD lead-out...");
                            completedViaDao = true;
                        }
                        catch
                        {
                            if (writeStarted)
                            {
                                throw;
                            }
                        }
                        finally
                        {
                            daoReg.Dispose();
                            try { rawCd.ReleaseMedia(); } catch { }
                        }
                    }
                    catch when (!completedViaDao && !cancellationToken.IsCancellationRequested)
                    {
                        // If DAO preparation failed before writing started (e.g. drive only supports TAO), fall back to TAO below.
                        Interlocked.Exchange(ref bytesWritten, 0);
                    }
                    finally
                    {
                        if (resultImageStream != null && Marshal.IsComObject(resultImageStream))
                        {
                            try { Marshal.FinalReleaseComObject(resultImageStream); } catch { }
                        }
                        foreach (var d in disposables)
                        {
                            try { d.Dispose(); } catch { }
                        }
                    }
                }
            }

            if (!completedViaDao)
            {
                tao = Imapi2ComFactory.CreateDiscFormat2TrackAtOnce();
                if (tao == null)
                {
                    return BurnOperationResult.Failed("Failed to instantiate MsftDiscFormat2TrackAtOnce COM object.");
                }

                tao.put_Recorder(comRecorder);
                tao.put_ClientName(ClientName);

                try { tao.put_BufferUnderrunFreeDisabled(false); } catch { }
                try { tao.put_DoNotFinalizeMedia(false); } catch { }

                tao.PrepareMedia();

                try
                {
                    if (options.Speed != null && options.Speed.Value.BytesPerSecond > 0)
                    {
                        int sectorsPerSec = Math.Max(1, options.Speed.Value.BytesPerSecond / OpticalConstants.RawSectorSize);
                        try { tao.SetWriteSpeed(sectorsPerSec, false); } catch { }
                    }

                    CancellationTokenRegistration taoReg = default;
                    if (cancellationToken.CanBeCanceled)
                    {
                        taoReg = cancellationToken.Register(() =>
                        {
                            try { tao.CancelAddTrack(); } catch { }
                        });
                    }

                    try
                    {
                        for (int i = 0; i < tracks.Count; i++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var trk = tracks[i];
                            Stream? rawStream = OpenAudioTrackStream(trk);
                            if (rawStream == null) continue;

                            int trackNum = i + 1;
                            ReportAudioProgress(0, $"Writing Audio Track {trackNum}/{tracks.Count}: {trk.Title}...");

                            using var paddedStream = new RedBookPaddedStream(rawStream, leaveOpen: false);
                            using var comStream = new ManagedComStream(
                                paddedStream,
                                leaveOpen: true,
                                bytesRead => ReportAudioProgress(bytesRead, $"Writing Audio Track {trackNum}/{tracks.Count}: {trk.Title}..."));

                            tao.AddAudioTrack(comStream);
                        }
                    }
                    finally
                    {
                        taoReg.Dispose();
                    }

                    ReportAudioProgress(0, "Finalizing Audio CD table of contents...");
                }
                finally
                {
                    try { tao.ReleaseMedia(); } catch { }
                }
            }

            sw.Stop();
            double speedMultiplier = options.Speed?.Multiplier > 0 ? options.Speed.Value.Multiplier : 16.0;

            if (options.EjectWhenDone)
            {
                try { recorder.EjectMedia(); } catch { }
            }

            progress?.Report(new BurnProgressSnapshot
            {
                ElapsedTime = sw.Elapsed,
                RemainingTime = TimeSpan.Zero,
                CurrentLba = totalSectors,
                TotalLba = totalSectors,
                BufferLevelPercent = 0.0,
                CurrentSpeedMultiplier = speedMultiplier,
                CurrentActionDescription = $"Audio CD burn completed ({tracks.Count} tracks written to {recorder.VolumePath})."
            });

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
            if (rawCreator != null) Imapi2ComFactory.FinalRelease(ref rawCreator);
            if (tao != null) Imapi2ComFactory.FinalRelease(ref tao);
            if (rawCd != null) Imapi2ComFactory.FinalRelease(ref rawCd);
            if (createdComRecorder && comRecorder != null) Imapi2ComFactory.FinalRelease(ref comRecorder);
            if (acquiredLock)
            {
                try { recorder.ReleaseExclusiveAccess(); } catch { }
            }
        }
    }

    private static Stream? OpenAudioTrackStream(AudioTrackSource trk)
    {
        if (trk.PcmStream != null)
        {
            if (trk.PcmStream.CanSeek)
            {
                trk.PcmStream.Position = 0;
            }
            return trk.PcmStream;
        }

        if (trk.PcmStreamFactory != null)
        {
            return trk.PcmStreamFactory();
        }

        if (!string.IsNullOrEmpty(trk.FilePath) && File.Exists(trk.FilePath))
        {
            return File.OpenRead(trk.FilePath);
        }

        return null;
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

/// <summary>
/// Wraps a raw 16-bit 44.1kHz stereo PCM audio track stream so that it is guaranteed seekable,
/// aligned to 2352-byte Red Book sector boundaries, and padded with digital silence to at least
/// 300 frames (4 seconds = 705,600 bytes) as required by IMAPIv2 (<see cref="IDiscFormat2TrackAtOnce"/>
/// and <see cref="IRawCDImageCreator"/>).
/// </summary>
internal sealed class RedBookPaddedStream : Stream
{
    public const long MinRedBookTrackBytes = 300L * OpticalConstants.RawSectorSize; // 705,600 bytes

    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private readonly long _innerLength;
    private readonly long _paddedLength;
    private long _position;

    public RedBookPaddedStream(Stream source, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (!source.CanSeek)
        {
            var ms = new MemoryStream();
            source.CopyTo(ms);
            ms.Position = 0;
            if (!leaveOpen)
            {
                source.Dispose();
            }
            _inner = ms;
            _leaveOpen = false;
        }
        else
        {
            _inner = source;
            _leaveOpen = leaveOpen;
        }

        _innerLength = _inner.Length;
        long sectorAligned = ((_innerLength + OpticalConstants.RawSectorSize - 1) / OpticalConstants.RawSectorSize) * OpticalConstants.RawSectorSize;
        _paddedLength = Math.Max(MinRedBookTrackBytes, sectorAligned);
        _position = 0;
        if (_inner.Position != 0)
        {
            _inner.Position = 0;
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _paddedLength;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count <= 0 || _position >= _paddedLength)
        {
            return 0;
        }

        int totalToRead = (int)Math.Min(count, _paddedLength - _position);
        int bytesReadTotal = 0;

        if (_position < _innerLength)
        {
            int fromInner = (int)Math.Min(totalToRead, _innerLength - _position);
            if (_inner.Position != _position)
            {
                _inner.Position = _position;
            }

            while (bytesReadTotal < fromInner)
            {
                int r = _inner.Read(buffer, offset + bytesReadTotal, fromInner - bytesReadTotal);
                if (r <= 0) break;
                bytesReadTotal += r;
                _position += r;
            }
        }

        int remainingPad = totalToRead - bytesReadTotal;
        if (remainingPad > 0)
        {
            Array.Clear(buffer, offset + bytesReadTotal, remainingPad);
            _position += remainingPad;
            bytesReadTotal += remainingPad;
        }

        return bytesReadTotal;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _paddedLength + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        _position = Math.Clamp(target, 0L, _paddedLength);
        if (_position <= _innerLength)
        {
            _inner.Position = _position;
        }
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
        {
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }
}


