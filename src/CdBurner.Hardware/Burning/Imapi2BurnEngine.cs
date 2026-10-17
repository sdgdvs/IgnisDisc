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

        if (!OperatingSystem.IsWindows())
        {
            return BurnOperationResult.Failed("Physical audio burning via IMAPIv2 is only supported on Windows.");
        }

        return await Task.FromResult(BurnOperationResult.Failed("Audio track physical burn is not yet implemented for physical drives in this phase."));
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
