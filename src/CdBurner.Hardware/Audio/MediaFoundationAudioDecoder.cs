using System.Runtime.InteropServices;
using CdBurner.Core.Audio;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Interop;

namespace CdBurner.Hardware.Audio;

/// <summary>
/// Audio decoder implementation combining a native C# RIFF WAVE engine (fast-path for .wav files)
/// with Windows Media Foundation (IMFSourceReader) for decoding MP3, FLAC, OGG, AAC, M4A, and WMA
/// directly to canonical Red Book CD-DA PCM (44,100 Hz, 16-bit signed LE, 2 channels).
/// </summary>
public class MediaFoundationAudioDecoder : IAudioDecoder, IDisposable
{
    private static readonly object InitLock = new();
    private static bool _mfInitialized;
    private bool _disposed;

    /// <summary>
    /// Ensures Windows Media Foundation platform is initialized for this process.
    /// </summary>
    public static void EnsureMediaFoundationInitialized()
    {
        if (_mfInitialized) return;
        lock (InitLock)
        {
            if (!_mfInitialized)
            {
                int hr = MediaFoundationNative.MFStartup(MediaFoundationConstants.MF_VERSION, MediaFoundationConstants.MFSTARTUP_NOSOCKET);
                if (hr != 0)
                {
                    // Fall back to full startup if NOSOCKET fails
                    hr = MediaFoundationNative.MFStartup(MediaFoundationConstants.MF_VERSION, MediaFoundationConstants.MFSTARTUP_FULL);
                    if (hr != 0)
                    {
                        throw new COMException($"Windows Media Foundation MFStartup failed with HRESULT 0x{hr:X8}.", hr);
                    }
                }
                _mfInitialized = true;
            }
        }
    }

    /// <summary>
    /// Inspects and extracts metadata from an audio file. Uses pure C# WavFileCodec for .wav files,
    /// or Windows Media Foundation source reader for other formats.
    /// </summary>
    public AudioFormatMetadata ProbeAudioFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Audio file not found: '{filePath}'.", filePath);
        }

        if (AudioDrmDetector.IsDrmProtected(filePath, out string drmDesc))
        {
            throw new DrmProtectedAudioException(filePath, drmDesc);
        }

        string ext = Path.GetExtension(filePath);
        if (string.Equals(ext, ".wav", StringComparison.OrdinalIgnoreCase))
        {
            return WavFileCodec.ProbeAudioFile(filePath);
        }

        return ProbeViaMediaFoundation(filePath);
    }

    /// <summary>
    /// Opens and decodes an audio file into canonical Red Book CD-DA PCM
    /// (44,100 Hz, 16-bit signed LE, 2 channels, 2,352 bytes/frame).
    /// </summary>
    public Stream OpenRedBookPcmStream(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Audio file not found: '{filePath}'.", filePath);
        }

        if (AudioDrmDetector.IsDrmProtected(filePath, out string drmDesc))
        {
            throw new DrmProtectedAudioException(filePath, drmDesc);
        }

        string ext = Path.GetExtension(filePath);
        if (string.Equals(ext, ".wav", StringComparison.OrdinalIgnoreCase))
        {
            return WavFileCodec.OpenRedBookPcmStream(filePath);
        }

        return OpenRedBookPcmStreamViaMediaFoundation(filePath);
    }

    /// <summary>
    /// Decodes an audio file directly using the Windows Media Foundation source reader pipeline.
    /// </summary>
    public Stream OpenRedBookPcmStreamViaMediaFoundation(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        EnsureMediaFoundationInitialized();

        if (AudioDrmDetector.IsDrmProtected(filePath, out string drmDesc))
        {
            throw new DrmProtectedAudioException(filePath, drmDesc);
        }

        int hr = MediaFoundationNative.MFCreateSourceReaderFromURL(filePath, IntPtr.Zero, out IMFSourceReader reader);
        if (hr != 0)
        {
            if (hr == unchecked((int)0xC00D36FA) ||
                (hr == unchecked((int)0x80004005) && (string.Equals(Path.GetExtension(filePath), ".m4p", StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetExtension(filePath), ".m4a", StringComparison.OrdinalIgnoreCase))))
            {
                throw new DrmProtectedAudioException(filePath, "Apple FairPlay DRM");
            }
            throw new COMException($"Failed to open audio file '{Path.GetFileName(filePath)}' for decoding via Windows Media Foundation (HRESULT 0x{hr:X8}). The file may be corrupt, DRM-protected, or in an unsupported format.", hr);
        }

        IMFMediaType? partialType = null;
        var outputStream = new MemoryStream();
        int actualChannels = OpticalConstants.CdAudioChannels;
        int actualSampleRate = OpticalConstants.CdAudioSampleRate;
        int actualBitsPerSample = OpticalConstants.CdAudioBitsPerSample;

        try
        {
            // Deselect all non-audio streams and select the primary audio stream
            reader.SetStreamSelection(MediaFoundationConstants.MF_SOURCE_READER_ALL_STREAMS, false);
            reader.SetStreamSelection(MediaFoundationConstants.MF_SOURCE_READER_FIRST_AUDIO_STREAM, true);

            // Configure target audio type: Uncompressed PCM, 44,100 Hz, 16-bit, Stereo (2 channels)
            hr = MediaFoundationNative.MFCreateMediaType(out partialType);
            if (hr != 0)
            {
                throw new COMException($"MFCreateMediaType failed with HRESULT 0x{hr:X8}.", hr);
            }

            partialType.SetGUID(MediaFoundationGuids.MF_MT_MAJOR_TYPE, MediaFoundationGuids.MFMediaType_Audio);
            partialType.SetGUID(MediaFoundationGuids.MF_MT_SUBTYPE, MediaFoundationGuids.MFAudioFormat_PCM);
            partialType.SetUINT32(MediaFoundationGuids.MF_MT_AUDIO_NUM_CHANNELS, 2);
            partialType.SetUINT32(MediaFoundationGuids.MF_MT_AUDIO_SAMPLES_PER_SECOND, 44100);
            partialType.SetUINT32(MediaFoundationGuids.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);

            int setTypeHr = reader.SetCurrentMediaType(MediaFoundationConstants.MF_SOURCE_READER_FIRST_AUDIO_STREAM, IntPtr.Zero, partialType);
            if (setTypeHr != 0)
            {
                // Fall back to unconstrained PCM if resampler MFT is not available
                partialType.DeleteItem(MediaFoundationGuids.MF_MT_AUDIO_NUM_CHANNELS);
                partialType.DeleteItem(MediaFoundationGuids.MF_MT_AUDIO_SAMPLES_PER_SECOND);
                partialType.DeleteItem(MediaFoundationGuids.MF_MT_AUDIO_BITS_PER_SAMPLE);
                reader.SetCurrentMediaType(MediaFoundationConstants.MF_SOURCE_READER_FIRST_AUDIO_STREAM, IntPtr.Zero, partialType);
            }

            // Read the actual uncompressed PCM format negotiated by the decoder
            IMFMediaType? actualType = null;
            try
            {
                if (reader.GetCurrentMediaType(MediaFoundationConstants.MF_SOURCE_READER_FIRST_AUDIO_STREAM, out actualType) == 0 && actualType != null)
                {
                    if (actualType.GetUINT32(MediaFoundationGuids.MF_MT_AUDIO_NUM_CHANNELS, out uint ch) == 0) actualChannels = (int)ch;
                    if (actualType.GetUINT32(MediaFoundationGuids.MF_MT_AUDIO_SAMPLES_PER_SECOND, out uint sr) == 0) actualSampleRate = (int)sr;
                    if (actualType.GetUINT32(MediaFoundationGuids.MF_MT_AUDIO_BITS_PER_SAMPLE, out uint bps) == 0) actualBitsPerSample = (int)bps;
                }
            }
            finally
            {
                if (actualType != null)
                {
                    Marshal.ReleaseComObject(actualType);
                }
            }

            // Read decoded PCM samples until end of stream
            int consecutiveNullSamples = 0;
            while (true)
            {
                int readHr = reader.ReadSample(
                    MediaFoundationConstants.MF_SOURCE_READER_FIRST_AUDIO_STREAM,
                    0,
                    out _,
                    out uint streamFlags,
                    out _,
                    out IMFSample? sample);

                if (readHr != 0 || (streamFlags & (MediaFoundationConstants.MF_SOURCE_READERF_ENDOFSTREAM | MediaFoundationConstants.MF_SOURCE_READERF_ERROR)) != 0)
                {
                    if (sample != null)
                    {
                        Marshal.ReleaseComObject(sample);
                    }
                    break;
                }

                if (sample == null)
                {
                    if (++consecutiveNullSamples > 16)
                    {
                        break;
                    }
                    continue;
                }
                consecutiveNullSamples = 0;

                try
                {
                    if (sample.GetBufferCount(out uint bufCount) == 0 && bufCount > 0)
                    {
                        if (sample.ConvertToContiguousBuffer(out IMFMediaBuffer buffer) == 0 && buffer != null)
                        {
                            try
                            {
                                bool locked = false;
                                try
                                {
                                    int lockHr = buffer.Lock(out IntPtr pBuffer, out _, out uint currentLength);
                                    if (lockHr == 0 && pBuffer != IntPtr.Zero)
                                    {
                                        locked = true;
                                        if (currentLength > 0)
                                        {
                                            byte[] managedBuffer = new byte[currentLength];
                                            Marshal.Copy(pBuffer, managedBuffer, 0, (int)currentLength);
                                            outputStream.Write(managedBuffer, 0, (int)currentLength);
                                        }
                                    }
                                }
                                finally
                                {
                                    if (locked)
                                    {
                                        buffer.Unlock();
                                    }
                                }
                            }
                            finally
                            {
                                Marshal.ReleaseComObject(buffer);
                            }
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(sample);
                }
            }
        }
        finally
        {
            if (partialType != null)
            {
                Marshal.ReleaseComObject(partialType);
            }
            Marshal.ReleaseComObject(reader);
        }

        if (actualSampleRate == OpticalConstants.CdAudioSampleRate &&
            actualChannels == OpticalConstants.CdAudioChannels &&
            actualBitsPerSample == OpticalConstants.CdAudioBitsPerSample)
        {
            outputStream.Position = 0;
            return outputStream;
        }

        // Resample, upmix or convert bit-depth to canonical Red Book PCM if needed
        byte[] rawBytes = outputStream.ToArray();
        byte[] header = WavFileCodec.CreateWavHeader(rawBytes.Length, actualSampleRate, (short)actualChannels, (short)actualBitsPerSample);
        using var combinedStream = new MemoryStream(header.Length + rawBytes.Length);
        combinedStream.Write(header);
        combinedStream.Write(rawBytes);
        combinedStream.Position = 0;

        byte[] redBookBytes = WavFileCodec.DecodeToRedBookPcm(combinedStream);
        return new MemoryStream(redBookBytes, 0, redBookBytes.Length, writable: false, publiclyVisible: true);
    }

    /// <summary>
    /// Extracts format and duration metadata via Windows Media Foundation source reader.
    /// </summary>
    public AudioFormatMetadata ProbeViaMediaFoundation(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        EnsureMediaFoundationInitialized();

        int hr = MediaFoundationNative.MFCreateSourceReaderFromURL(filePath, IntPtr.Zero, out IMFSourceReader reader);
        if (hr != 0)
        {
            if (hr == unchecked((int)0xC00D36FA) ||
                (hr == unchecked((int)0x80004005) && (string.Equals(Path.GetExtension(filePath), ".m4p", StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetExtension(filePath), ".m4a", StringComparison.OrdinalIgnoreCase))))
            {
                throw new DrmProtectedAudioException(filePath, "Apple FairPlay DRM");
            }
            throw new COMException($"Failed to open audio file '{Path.GetFileName(filePath)}' via Windows Media Foundation (HRESULT 0x{hr:X8}). The file may be corrupt, DRM-protected, or in an unsupported format.", hr);
        }

        IMFMediaType? nativeType = null;
        try
        {
            TimeSpan duration = TimeSpan.Zero;
            var varDuration = new PropVariant();
            int hrDuration = reader.GetPresentationAttribute(
                MediaFoundationConstants.MF_SOURCE_READER_MEDIASOURCE,
                MediaFoundationGuids.MF_PD_DURATION,
                out varDuration);

            if (hrDuration == 0)
            {
                try
                {
                    // VT_UI8 = 21, duration in 100ns units
                    if (varDuration.vt == 21 && varDuration.uhVal > 0)
                    {
                        duration = TimeSpan.FromTicks((long)varDuration.uhVal);
                    }
                }
                finally
                {
                    MediaFoundationNative.PropVariantClear(ref varDuration);
                }
            }

            int channels = OpticalConstants.CdAudioChannels;
            int sampleRate = OpticalConstants.CdAudioSampleRate;
            int bitsPerSample = OpticalConstants.CdAudioBitsPerSample;
            string formatName = Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant();

            try
            {
                int hrNative = reader.GetNativeMediaType(MediaFoundationConstants.MF_SOURCE_READER_FIRST_AUDIO_STREAM, 0, out nativeType);
                if (hrNative == 0 && nativeType != null)
                {
                    if (nativeType.GetUINT32(MediaFoundationGuids.MF_MT_AUDIO_NUM_CHANNELS, out uint ch) == 0) channels = (int)ch;
                    if (nativeType.GetUINT32(MediaFoundationGuids.MF_MT_AUDIO_SAMPLES_PER_SECOND, out uint sr) == 0) sampleRate = (int)sr;
                    if (nativeType.GetUINT32(MediaFoundationGuids.MF_MT_AUDIO_BITS_PER_SAMPLE, out uint bps) == 0) bitsPerSample = (int)bps;
                    if (nativeType.GetGUID(MediaFoundationGuids.MF_MT_SUBTYPE, out Guid sub) == 0)
                    {
                        if (sub == MediaFoundationGuids.MFAudioFormat_MP3) formatName = "MP3";
                        else if (sub == MediaFoundationGuids.MFAudioFormat_AAC) formatName = "AAC";
                        else if (sub == MediaFoundationGuids.MFAudioFormat_FLAC) formatName = "FLAC";
                        else if (sub == MediaFoundationGuids.MFAudioFormat_PCM) formatName = "PCM";
                    }
                }
            }
            catch
            {
                // Fall back to default Red Book estimates if native media type query fails
            }

            long redBookFrames = (long)Math.Round(duration.TotalSeconds * OpticalConstants.CdAudioSampleRate);
            long totalRedBookBytes = redBookFrames * OpticalConstants.CdAudioBytesPerSample;
            long estimatedPcmBytes = (long)Math.Round(duration.TotalSeconds * sampleRate) * channels * (bitsPerSample / 8);

            return new AudioFormatMetadata
            {
                FilePath = filePath,
                FormatName = formatName,
                Channels = channels,
                SampleRate = sampleRate,
                BitsPerSample = bitsPerSample,
                Duration = duration,
                TotalPcmBytes = estimatedPcmBytes,
                TotalRedBookBytes = totalRedBookBytes
            };
        }
        finally
        {
            if (nativeType != null)
            {
                Marshal.ReleaseComObject(nativeType);
            }
            Marshal.ReleaseComObject(reader);
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
        }
    }
}
