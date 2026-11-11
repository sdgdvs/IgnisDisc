using System.Diagnostics;
using System.Runtime.InteropServices;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Interop;
using CdBurner.Hardware.Scsi;

namespace CdBurner.Hardware.Burning;

/// <summary>
/// Hardware burning backend executing against physical IMAPIv2 optical drives
/// via <see cref="IDiscFormat2Data"/>, <see cref="ManagedComStream"/>, and <see cref="DiscFormatDataEventsSink"/>
/// dispatched through <see cref="MtaBurnWorker"/> on a dedicated MTA thread.
/// </summary>
public class Imapi2BurnEngine : IBurnEngineBackend, ISecureAudioReader
{
    public const string DefaultClientName = "CdBurner";

    private readonly IImapiComProvider _comProvider;

    /// <summary>
    /// Client name used for IMAPIv2 exclusive drive locking.
    /// </summary>
    public string ClientName { get; set; } = DefaultClientName;

    public Imapi2BurnEngine(IImapiComProvider? comProvider = null)
    {
        _comProvider = comProvider ?? DefaultImapiComProvider.Instance;
    }

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

        if (options.Simulate)
        {
            return BurnOperationResult.Failed("Test-write (simulation) is not supported on physical drives by Windows' burning API. Nothing was written.");
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
        bool wasCancelled = false;
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
                comRecorder = _comProvider.CreateDiscRecorder2();
                if (comRecorder == null)
                {
                    return BurnOperationResult.Failed("Failed to instantiate MsftDiscRecorder2 COM object.");
                }
                comRecorder.InitializeDiscRecorder(recorder.UniqueId);
                createdComRecorder = true;
            }

            // 2. Create and configure MsftDiscFormat2Data
            formatData = _comProvider.CreateDiscFormat2Data();
            if (formatData == null)
            {
                return BurnOperationResult.Failed("Failed to instantiate MsftDiscFormat2Data COM object.");
            }

            acquiredLock = recorder.AcquireExclusiveAccess(ClientName);
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

            // Used DVD+RW / DVD-RAM: IMAPI refuses to write over existing data (0xC0AA0405) unless told to.
            if (options.OverwriteExistingData)
            {
                try
                {
                    formatData.put_ForceOverwrite(true);
                }
                catch
                {
                }
            }

            // Write speed: explicitly configure safe CLV/Z-CLV write speed for portable USB drives
            int targetSectorsPerSec = -1;
            if (options.Speed != null && options.Speed.Value.BytesPerSecond > 0 && options.Speed.Value.BytesPerSecond != int.MaxValue)
            {
                targetSectorsPerSec = options.Speed.Value.BytesPerSecond / OpticalConstants.DataSectorSize;
            }
            else
            {
                try
                {
                    // No speed requested: use the fastest speed the drive supports for this media. The list's order
                    // is not guaranteed (on an HL-DT-ST GP60NB50 the last entry was the slowest, so every default
                    // burn ran at 1x: 5 minutes to write 45 MB), so take the maximum value instead of an end.
                    targetSectorsPerSec = SelectMaxSupportedWriteSpeed(formatData.SupportedWriteSpeeds);
                }
                catch
                {
                }
            }

            if (targetSectorsPerSec > 0)
            {
                try
                {
                    formatData.SetWriteSpeed(targetSectorsPerSec, false);
                }
                catch
                {
                }
            }

            int negotiatedSectorsPerSec = targetSectorsPerSec;
            DiscMediaType physicalMedia = DiscMediaType.CdR;
            try
            {
                physicalMedia = ImapiDiscRecorder.MapPhysicalTypeToDiscMediaType(formatData.CurrentPhysicalMediaType);
                int curSpeed = formatData.CurrentWriteSpeed;
                if (curSpeed > 0)
                {
                    negotiatedSectorsPerSec = curSpeed;
                }
            }
            catch
            {
            }

            // 3. Connect event sink
            eventSink = new DiscFormatDataEventsSink(progress);
            eventSink.Connect(formatData);

            // 4. Wrap image stream in COM IStream with real-time sector progress
            long totalStreamBytes = imageStream.CanSeek ? imageStream.Length : 0;
            long readStreamBytes = 0;
            comStream = new ManagedComStream(imageStream, leaveOpen: true, onBytesRead: delta =>
            {
                long current = Interlocked.Add(ref readStreamBytes, delta);
                if (!eventSink.IsConnected && totalStreamBytes > 0 && progress != null)
                {
                    int totalSecs = OpticalConstants.BytesToSectors(totalStreamBytes, OpticalConstants.DataSectorSize);
                    int currentSec = Math.Clamp(OpticalConstants.BytesToSectors(current, OpticalConstants.DataSectorSize), 0, totalSecs);
                    progress.Report(new BurnProgressSnapshot
                    {
                        CurrentActionDescription = "WritingData",
                        CurrentLba = currentSec,
                        TotalLba = totalSecs,
                        BufferLevelPercent = 100.0,
                        ElapsedTime = sw.Elapsed
                    });
                }
            });

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
            int baseRate = WriteSpeedDescriptor.GetBaseRate(physicalMedia, isAudio: false);
            double speedMultiplier = negotiatedSectorsPerSec > 0 && baseRate > 0
                ? (double)((long)negotiatedSectorsPerSec * OpticalConstants.DataSectorSize) / baseRate
                : (options.Speed?.Multiplier > 0 ? options.Speed.Value.Multiplier : 16.0);

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
            wasCancelled = true;
            return BurnOperationResult.CancelledResult(oce.Message);
        }
        catch (COMException cex)
        {
            if (cancellationToken.IsCancellationRequested || (uint)cex.ErrorCode == 0xC0AA0406u)
            {
                wasCancelled = true;
                return BurnOperationResult.CancelledResult("Burn operation was cancelled by user.");
            }
            if ((uint)cex.ErrorCode is 0xC0AA0405u or 0xC0AA0407u or 0xC0AA0206u)
            {
                string? friendly = ResolveNonBlankMediaErrorMessage(recorder, (uint)cex.ErrorCode == 0xC0AA0405u);
                if (friendly != null)
                {
                    return BurnOperationResult.Failed(friendly, cex);
                }
            }
            return BurnOperationResult.Failed(FormatComExceptionMessage(cex), cex);
        }
        catch (Exception ex)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                wasCancelled = true;
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

            if (wasCancelled && !recorder.IsVirtual)
            {
                _comProvider.TrySpinDown(recorder.VolumePath);
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

        if (options.Simulate)
        {
            return BurnOperationResult.Failed("Test-write (simulation) is not supported on physical drives by Windows' burning API. Nothing was written.");
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
        bool wasCancelled = false;
        int negotiatedAudioSectorsPerSec = -1;
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
                comRecorder = _comProvider.CreateDiscRecorder2();
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
                rawCd = _comProvider.CreateDiscFormat2RawCD();
                rawCreator = _comProvider.CreateRawCDImageCreator();

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
                            var (rawStream, ownsStream) = OpenAudioTrackStream(trk);
                            if (rawStream == null) continue;

                            RedBookPaddedStream paddedStream;
                            try
                            {
                                paddedStream = new RedBookPaddedStream(rawStream, leaveOpen: !ownsStream);
                            }
                            catch
                            {
                                if (ownsStream) rawStream.Dispose();
                                throw;
                            }
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

                        int rawTargetSectorsPerSec = -1;
                        if (options.Speed != null && options.Speed.Value.BytesPerSecond > 0 && options.Speed.Value.BytesPerSecond != int.MaxValue)
                        {
                            rawTargetSectorsPerSec = Math.Max(1, options.Speed.Value.BytesPerSecond / OpticalConstants.RawSectorSize);
                        }
                        else
                        {
                            try { rawTargetSectorsPerSec = SelectMaxSupportedWriteSpeed(rawCd.SupportedWriteSpeeds); } catch { }
                        }

                        if (rawTargetSectorsPerSec > 0)
                        {
                            try { rawCd.SetWriteSpeed(rawTargetSectorsPerSec, false); } catch { }
                            negotiatedAudioSectorsPerSec = rawTargetSectorsPerSec;
                        }
                        try
                        {
                            int curRawSpeed = rawCd.CurrentWriteSpeed;
                            if (curRawSpeed > 0) negotiatedAudioSectorsPerSec = curRawSpeed;
                        }
                        catch
                        {
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
                    catch (Exception daoEx) when (!completedViaDao && !cancellationToken.IsCancellationRequested)
                    {
                        // If DAO preparation failed before writing started (e.g. drive only supports TAO), fall back to TAO below.
                        Interlocked.Exchange(ref bytesWritten, 0);
                        System.Diagnostics.Trace.TraceWarning($"IgnisDisc: Disc-at-Once setup failed, falling back to Track-at-Once: {daoEx}");
                        ReportAudioProgress(0, $"Disc-at-Once is not available on this drive/media ({daoEx.Message}). Falling back to Track-at-Once (2-second gaps between tracks)...");
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
                tao = _comProvider.CreateDiscFormat2TrackAtOnce();
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
                    int taoTargetSectorsPerSec = -1;
                    if (options.Speed != null && options.Speed.Value.BytesPerSecond > 0 && options.Speed.Value.BytesPerSecond != int.MaxValue)
                    {
                        taoTargetSectorsPerSec = Math.Max(1, options.Speed.Value.BytesPerSecond / OpticalConstants.RawSectorSize);
                    }
                    else
                    {
                        try { taoTargetSectorsPerSec = SelectMaxSupportedWriteSpeed(tao.SupportedWriteSpeeds); } catch { }
                    }

                    if (taoTargetSectorsPerSec > 0)
                    {
                        try { tao.SetWriteSpeed(taoTargetSectorsPerSec, false); } catch { }
                        negotiatedAudioSectorsPerSec = taoTargetSectorsPerSec;
                    }
                    try
                    {
                        int curTaoSpeed = tao.CurrentWriteSpeed;
                        if (curTaoSpeed > 0) negotiatedAudioSectorsPerSec = curTaoSpeed;
                    }
                    catch
                    {
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
                            var (rawStream, ownsStream) = OpenAudioTrackStream(trk);
                            if (rawStream == null) continue;

                            int trackNum = i + 1;
                            ReportAudioProgress(0, $"Writing Audio Track {trackNum}/{tracks.Count}: {trk.Title}...");

                            RedBookPaddedStream paddedStream;
                            try
                            {
                                paddedStream = new RedBookPaddedStream(rawStream, leaveOpen: !ownsStream);
                            }
                            catch
                            {
                                if (ownsStream) rawStream.Dispose();
                                throw;
                            }

                            using (paddedStream)
                            using (var comStream = new ManagedComStream(
                                paddedStream,
                                leaveOpen: true,
                                bytesRead => ReportAudioProgress(bytesRead, $"Writing Audio Track {trackNum}/{tracks.Count}: {trk.Title}...")))
                            {
                                tao.AddAudioTrack(comStream);
                            }
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
            double speedMultiplier = negotiatedAudioSectorsPerSec > 0
                ? (double)((long)negotiatedAudioSectorsPerSec * OpticalConstants.RawSectorSize) / OpticalConstants.CdAudio1XBytesPerSec
                : (options.Speed?.Multiplier > 0 ? options.Speed.Value.Multiplier : 16.0);

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
            wasCancelled = true;
            return BurnOperationResult.CancelledResult(oce.Message);
        }
        catch (COMException cex)
        {
            if (cancellationToken.IsCancellationRequested || (uint)cex.ErrorCode == 0xC0AA0406u)
            {
                wasCancelled = true;
                return BurnOperationResult.CancelledResult("Burn operation was cancelled by user.");
            }
            if ((uint)cex.ErrorCode is 0xC0AA0405u or 0xC0AA0407u or 0xC0AA0206u)
            {
                string? friendly = ResolveNonBlankMediaErrorMessage(recorder, (uint)cex.ErrorCode == 0xC0AA0405u);
                if (friendly != null)
                {
                    return BurnOperationResult.Failed(friendly, cex);
                }
            }
            return BurnOperationResult.Failed(FormatComExceptionMessage(cex), cex);
        }
        catch (Exception ex)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                wasCancelled = true;
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
            if (wasCancelled && !recorder.IsVirtual)
            {
                _comProvider.TrySpinDown(recorder.VolumePath);
            }
        }
    }

    private static int SelectMaxSupportedWriteSpeed(Array? supportedSpeeds)
    {
        int maxSectorsPerSec = -1;
        if (supportedSpeeds == null) return maxSectorsPerSec;

        foreach (var speed in supportedSpeeds)
        {
            if (speed != null)
            {
                maxSectorsPerSec = Math.Max(maxSectorsPerSec, Convert.ToInt32(speed));
            }
        }

        return maxSectorsPerSec;
    }

    private static string FormatComExceptionMessage(COMException cex)
    {
        uint code = (uint)cex.ErrorCode;
        string? hint = code switch
        {
            0xC0AA0300u => "Buffer underrun / loss of streaming (source disk or USB bus could not feed data fast enough; try a lower write speed).",
            0xC0AA0301u => "Unexpected response from optical drive (laser power calibration / OPC failed on this disc, or USB power dropped; try a lower write speed or different media brand).",
            0xC0AA020Du => "Optical drive command timed out (drive stopped responding on the SATA/USB bus).",
            0xC0AA0207u or 0xC0AA0210u => "Optical drive is busy or locked by another application (close Windows Explorer burn sessions, iTunes, or other disc tools).",
            0xC0AA0203u => "Inserted disc is incompatible with the requested recording mode.",
            0xC0AA020Fu => "Optical drive does not support writing to this media type.",
            _ => null
        };

        return hint != null
            ? $"IMAPIv2 COM error (0x{code:X8}): {hint} ({cex.Message})"
            : $"IMAPIv2 COM error (0x{code:X8}): {cex.Message}";
    }

    private static string? ResolveNonBlankMediaErrorMessage(IDiscRecorderBackend recorder, bool isForceOverwriteError)
    {
        try
        {
            var st = recorder.QueryMediaStatus();
            if (st.IsRewritable && !st.IsOverwriteOnly)
            {
                return CdBurner.Core.Burning.DataBurnService.EraseRequiredMessage;
            }
            if (st.IsOverwriteOnly || isForceOverwriteError)
            {
                return CdBurner.Core.Burning.DataBurnService.ReplaceExistingDataMessage;
            }
        }
        catch
        {
            if (isForceOverwriteError)
            {
                return CdBurner.Core.Burning.DataBurnService.ReplaceExistingDataMessage;
            }
        }

        return null;
    }

    private static (Stream? Stream, bool OwnsStream) OpenAudioTrackStream(AudioTrackSource trk)
    {
        if (trk.PcmStream != null)
        {
            if (trk.PcmStream.CanSeek)
            {
                trk.PcmStream.Position = 0;
            }
            return (trk.PcmStream, false);
        }

        if (trk.PcmStreamFactory != null)
        {
            return (trk.PcmStreamFactory(), true);
        }

        if (!string.IsNullOrEmpty(trk.FilePath) && File.Exists(trk.FilePath))
        {
            return (File.OpenRead(trk.FilePath), true);
        }

        return (null, false);
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
            IDiscRecorder2? comRecorder = null;
            bool createdComRecorder = false;
            bool acquiredLock = false;
            bool wasCancelled = false;
            try
            {
                erase = _comProvider.CreateDiscFormat2Erase();
                if (erase == null)
                {
                    return BurnOperationResult.Failed("Failed to create MsftDiscFormat2Erase COM object.");
                }

                comRecorder = (recorder as ImapiDiscRecorder)?.Recorder;
                if (comRecorder == null)
                {
                    comRecorder = _comProvider.CreateDiscRecorder2();
                    if (comRecorder != null)
                    {
                        createdComRecorder = true;
                        comRecorder.InitializeDiscRecorder(recorder.UniqueId);
                    }
                }

                if (comRecorder == null)
                {
                    return BurnOperationResult.Failed("Failed to resolve physical recorder COM instance.");
                }

                acquiredLock = recorder.AcquireExclusiveAccess(ClientName);

                erase.put_Recorder(comRecorder);
                erase.put_ClientName(ClientName);
                erase.put_FullErase(fullErase);

                token.ThrowIfCancellationRequested();

                mtaProgress?.Report(new BurnProgressSnapshot
                {
                    ElapsedTime = sw.Elapsed,
                    RemainingTime = TimeSpan.Zero,
                    CurrentLba = 0,
                    TotalLba = 100,
                    BufferLevelPercent = 100.0,
                    CurrentActionDescription = fullErase ? "FullErasing" : "QuickErasing"
                });

                erase.EraseMedia();
                sw.Stop();

                mtaProgress?.Report(new BurnProgressSnapshot
                {
                    ElapsedTime = sw.Elapsed,
                    RemainingTime = TimeSpan.Zero,
                    CurrentLba = 100,
                    TotalLba = 100,
                    BufferLevelPercent = 0.0,
                    CurrentActionDescription = "Completed"
                });

                return BurnOperationResult.Succeeded(0, sw.Elapsed);
            }
            catch (OperationCanceledException oce)
            {
                wasCancelled = true;
                return BurnOperationResult.CancelledResult(oce.Message);
            }
            catch (COMException cex) when (token.IsCancellationRequested || (uint)cex.ErrorCode == 0xC0AA0406u)
            {
                wasCancelled = true;
                return BurnOperationResult.CancelledResult("Erase operation was cancelled by user.");
            }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested)
                {
                    wasCancelled = true;
                    return BurnOperationResult.CancelledResult("Erase operation was cancelled.");
                }
                return BurnOperationResult.Failed($"Erase failed: {ex.Message}", ex);
            }
            finally
            {
                if (erase != null)
                {
                    Imapi2ComFactory.FinalRelease(ref erase);
                }

                if (createdComRecorder && comRecorder != null)
                {
                    Imapi2ComFactory.FinalRelease(ref comRecorder);
                }

                if (acquiredLock)
                {
                    try { recorder.ReleaseExclusiveAccess(); } catch { }
                }

                if (wasCancelled && !recorder.IsVirtual)
                {
                    _comProvider.TrySpinDown(recorder.VolumePath);
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

        return await PhysicalSectorReader.ReadAsync(
            recorder.VolumePath,
            startLba,
            sectorCount,
            raw2352Mode,
            sectorConsumer,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task<RipOperationResult> ReadSectorsForVerifyAsync(
        IDiscRecorderBackend recorder,
        int startLba,
        int sectorCount,
        Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken cancellationToken,
        bool strict = false,
        SectorRange? writtenRange = null)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(sectorConsumer);
        return PhysicalSectorReader.ReadAsync(recorder.VolumePath, startLba, sectorCount, raw2352Mode: false,
            sectorConsumer, progress, cancellationToken,
            strict ? DriveCacheBypass.EveryRead : DriveCacheBypass.RecentlyWritten,
            writtenRange?.Start, writtenRange?.Count);
    }

    /// <inheritdoc/>
    public bool SupportsC2ErrorPointers(IDiscRecorderBackend recorder, int probeLba)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        return PhysicalSectorReader.ProbeC2Support(recorder.VolumePath, probeLba);
    }

    /// <inheritdoc/>
    public Task<RipOperationResult> ReadAudioSectorsWithC2Async(
        IDiscRecorderBackend recorder,
        int startLba,
        int sectorCount,
        Func<int, ReadOnlyMemory<byte>, int, ValueTask> sectorConsumer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        return PhysicalSectorReader.ReadAudioWithC2Async(recorder.VolumePath, startLba, sectorCount, sectorConsumer, cancellationToken);
    }

    /// <inheritdoc/>
    public Task FlushReadCacheAsync(IDiscRecorderBackend recorder, int nearLba, int audioEndLba, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        return PhysicalSectorReader.FlushReadCacheAsync(recorder.VolumePath, nearLba, audioEndLba, cancellationToken);
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


