using System.Runtime.InteropServices;
using CdBurner.Core.Audio;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Interop;

namespace CdBurner.Hardware.Audio;

/// <summary>
/// Encodes CD audio to AAC-LC in an .m4a (MPEG-4) file with the AAC encoder that ships with Windows, through a
/// Media Foundation sink writer. Nothing is bundled: if Windows has no AAC encoder the call fails with its HRESULT.
/// Title, artist, album and track number are written through Windows' own M4A property handler afterwards.
/// </summary>
public sealed class MediaFoundationAacEncoder : IAacFileEncoder
{
    private const int FramesPerChunk = 4096;
    private const uint AacLcProfileLevel = 0x29; // AAC Profile L2, the value Microsoft's AAC encoder documents for 44.1 kHz stereo

    public Task<string?> EncodeAsync(
        string wavPath,
        string m4aPath,
        AudioTrackTags tags,
        int bitrateKbps,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wavPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(m4aPath);
        ArgumentNullException.ThrowIfNull(tags);
        string? bitrateError = AudioRipFormats.ValidateBitrate(AudioRipFormat.Aac, bitrateKbps);
        if (bitrateError != null)
        {
            throw new ArgumentOutOfRangeException(nameof(bitrateKbps), bitrateError);
        }
        if (!File.Exists(wavPath))
        {
            throw new FileNotFoundException($"Source WAV file not found: {wavPath}", wavPath);
        }

        // Media Foundation calls block; keep them off the caller's (possibly UI) thread.
        return Task.Run(() =>
        {
            WriteAac(wavPath, m4aPath, bitrateKbps, cancellationToken);
            return ShellAudioTagWriter.TryWrite(m4aPath, tags);
        }, cancellationToken);
    }

    private static void Check(int hr, string what)
    {
        if (hr != 0)
        {
            throw new COMException($"{what} failed (HRESULT 0x{hr:X8}). The Windows AAC encoder may be missing or unavailable.", hr);
        }
    }

    private static void WriteAac(string wavPath, string m4aPath, int bitrateKbps, CancellationToken cancellationToken)
    {
        MediaFoundationAudioDecoder.EnsureMediaFoundationInitialized();

        if (File.Exists(m4aPath))
        {
            File.Delete(m4aPath);
        }

        IMFAttributes? writerAttributes = null;
        IMFSinkWriter? writer = null;
        IMFMediaType? outType = null;
        IMFMediaType? inType = null;
        bool finalized = false;
        try
        {
            Check(MediaFoundationNative.MFCreateAttributes(out writerAttributes, 1), "MFCreateAttributes");
            Check(writerAttributes.SetGUID(MediaFoundationGuids.MF_TRANSCODE_CONTAINERTYPE, MediaFoundationGuids.MFTranscodeContainerType_MPEG4), "Selecting the MPEG-4 container");
            Check(MediaFoundationNative.MFCreateSinkWriterFromURL(Path.GetFullPath(m4aPath), IntPtr.Zero, writerAttributes, out writer), "MFCreateSinkWriterFromURL");

            // Output: AAC-LC, raw access units, at the requested bitrate (bytes per second).
            Check(MediaFoundationNative.MFCreateMediaType(out outType), "MFCreateMediaType");
            outType.SetGUID(MediaFoundationGuids.MF_MT_MAJOR_TYPE, MediaFoundationGuids.MFMediaType_Audio);
            outType.SetGUID(MediaFoundationGuids.MF_MT_SUBTYPE, MediaFoundationGuids.MFAudioFormat_AAC);
            outType.SetUINT32(MediaFoundationGuids.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
            outType.SetUINT32(MediaFoundationGuids.MF_MT_AUDIO_SAMPLES_PER_SECOND, OpticalConstants.CdAudioSampleRate);
            outType.SetUINT32(MediaFoundationGuids.MF_MT_AUDIO_NUM_CHANNELS, OpticalConstants.CdAudioChannels);
            outType.SetUINT32(MediaFoundationGuids.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, (uint)(bitrateKbps * 1000 / 8));
            outType.SetUINT32(MediaFoundationGuids.MF_MT_AAC_PAYLOAD_TYPE, 0);
            outType.SetUINT32(MediaFoundationGuids.MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, AacLcProfileLevel);
            Check(writer.AddStream(outType, out uint streamIndex), "Adding the AAC stream");

            // Input: the CD's own PCM.
            Check(MediaFoundationNative.MFCreateMediaType(out inType), "MFCreateMediaType");
            inType.SetGUID(MediaFoundationGuids.MF_MT_MAJOR_TYPE, MediaFoundationGuids.MFMediaType_Audio);
            inType.SetGUID(MediaFoundationGuids.MF_MT_SUBTYPE, MediaFoundationGuids.MFAudioFormat_PCM);
            inType.SetUINT32(MediaFoundationGuids.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
            inType.SetUINT32(MediaFoundationGuids.MF_MT_AUDIO_SAMPLES_PER_SECOND, OpticalConstants.CdAudioSampleRate);
            inType.SetUINT32(MediaFoundationGuids.MF_MT_AUDIO_NUM_CHANNELS, OpticalConstants.CdAudioChannels);
            inType.SetUINT32(MediaFoundationGuids.MF_MT_AUDIO_BLOCK_ALIGNMENT, OpticalConstants.CdAudioBytesPerSample);
            inType.SetUINT32(MediaFoundationGuids.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, (uint)(OpticalConstants.CdAudioSampleRate * OpticalConstants.CdAudioBytesPerSample));
            Check(writer.SetInputMediaType(streamIndex, inType, null), "Setting the PCM input type");
            Check(writer.BeginWriting(), "BeginWriting");

            using var pcm = WavFileCodec.OpenRedBookPcmStream(wavPath);
            byte[] chunk = new byte[FramesPerChunk * OpticalConstants.CdAudioBytesPerSample];
            long framesWritten = 0;
            int read;
            while ((read = ReadFull(pcm, chunk)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int frames = read / OpticalConstants.CdAudioBytesPerSample;
                if (frames == 0)
                {
                    break;
                }

                WriteChunk(writer, streamIndex, chunk, frames * OpticalConstants.CdAudioBytesPerSample, framesWritten, frames);
                framesWritten += frames;
            }

            Check(writer.DoFinalize(), "Finishing the .m4a file");
            finalized = true;
        }
        catch
        {
            if (!finalized)
            {
                ReleaseAll(ref writer, ref outType, ref inType, ref writerAttributes);
                try { File.Delete(m4aPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            throw;
        }
        finally
        {
            // The file must be fully released before the tag writer opens it.
            ReleaseAll(ref writer, ref outType, ref inType, ref writerAttributes);
        }
    }

    private static void ReleaseAll(ref IMFSinkWriter? writer, ref IMFMediaType? outType, ref IMFMediaType? inType, ref IMFAttributes? attributes)
    {
        if (writer != null) { Marshal.ReleaseComObject(writer); writer = null; }
        if (outType != null) { Marshal.ReleaseComObject(outType); outType = null; }
        if (inType != null) { Marshal.ReleaseComObject(inType); inType = null; }
        if (attributes != null) { Marshal.ReleaseComObject(attributes); attributes = null; }
    }

    private static int ReadFull(Stream stream, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = stream.Read(buffer, total, buffer.Length - total);
            if (n <= 0)
            {
                break;
            }
            total += n;
        }
        return total;
    }

    private static void WriteChunk(IMFSinkWriter writer, uint streamIndex, byte[] data, int byteCount, long firstFrame, int frames)
    {
        IMFMediaBuffer? buffer = null;
        IMFSample? sample = null;
        try
        {
            Check(MediaFoundationNative.MFCreateMemoryBuffer((uint)byteCount, out buffer), "MFCreateMemoryBuffer");
            Check(buffer.Lock(out IntPtr dest, out _, out _), "Locking the sample buffer");
            try
            {
                Marshal.Copy(data, 0, dest, byteCount);
            }
            finally
            {
                buffer.Unlock();
            }
            Check(buffer.SetCurrentLength((uint)byteCount), "SetCurrentLength");

            Check(MediaFoundationNative.MFCreateSample(out sample), "MFCreateSample");
            Check(sample.AddBuffer(buffer), "AddBuffer");
            const long TicksPerSecond = 10_000_000;
            sample.SetSampleTime(firstFrame * TicksPerSecond / OpticalConstants.CdAudioSampleRate);
            sample.SetSampleDuration(frames * TicksPerSecond / OpticalConstants.CdAudioSampleRate);
            Check(writer.WriteSample(streamIndex, sample), "WriteSample");
        }
        finally
        {
            if (sample != null) Marshal.ReleaseComObject(sample);
            if (buffer != null) Marshal.ReleaseComObject(buffer);
        }
    }
}
