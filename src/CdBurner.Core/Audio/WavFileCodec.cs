using System.Buffers.Binary;
using System.Text;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Audio;

/// <summary>
/// Pure C# RIFF WAVE encoder and decoder supporting 8/16/24/32-bit PCM and IEEE Float formats,
/// automatic resampling to 44,100 Hz, mono-to-stereo channel upmixing, and standard 44-byte RIFF headers.
/// </summary>
public static class WavFileCodec
{
    private static readonly byte[] RiffHeader = "RIFF"u8.ToArray();
    private static readonly byte[] WaveHeader = "WAVE"u8.ToArray();
    private static readonly byte[] FmtHeader = "fmt "u8.ToArray();
    private static readonly byte[] DataHeader = "data"u8.ToArray();

    private const ushort WaveFormatPcm = 0x0001;
    private const ushort WaveFormatIeeeFloat = 0x0003;
    private const ushort WaveFormatExtensible = 0xFFFE;

    private static readonly Guid SubFormatPcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubFormatIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    /// <summary>
    /// Creates a standard 44-byte RIFF WAVE header for canonical Red Book or custom PCM streams.
    /// </summary>
    public static byte[] CreateWavHeader(
        int pcmByteLength,
        int sampleRate = OpticalConstants.CdAudioSampleRate,
        short channels = OpticalConstants.CdAudioChannels,
        short bitsPerSample = OpticalConstants.CdAudioBitsPerSample)
    {
        byte[] header = new byte[44];
        WriteWavHeader(header.AsSpan(), pcmByteLength, sampleRate, channels, bitsPerSample);
        return header;
    }

    /// <summary>
    /// Writes a standard 44-byte RIFF WAVE header to a span.
    /// </summary>
    public static void WriteWavHeader(
        Span<byte> destination,
        int pcmByteLength,
        int sampleRate = OpticalConstants.CdAudioSampleRate,
        short channels = OpticalConstants.CdAudioChannels,
        short bitsPerSample = OpticalConstants.CdAudioBitsPerSample)
    {
        if (destination.Length < 44)
        {
            throw new ArgumentException("Destination span must be at least 44 bytes.", nameof(destination));
        }

        short bytesPerSample = (short)(bitsPerSample / 8);
        short blockAlign = (short)(channels * bytesPerSample);
        int byteRate = sampleRate * blockAlign;
        uint totalChunkSize = (uint)(pcmByteLength + 36);

        // "RIFF"
        RiffHeader.CopyTo(destination[..4]);
        // ChunkSize (FileSize - 8)
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(4, 4), totalChunkSize);
        // "WAVE"
        WaveHeader.CopyTo(destination.Slice(8, 4));
        // "fmt "
        FmtHeader.CopyTo(destination.Slice(12, 4));
        // Subchunk1Size = 16 for PCM
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(16, 4), 16);
        // AudioFormat = 1 (PCM)
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(20, 2), WaveFormatPcm);
        // NumChannels
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(22, 2), (ushort)channels);
        // SampleRate
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(24, 4), (uint)sampleRate);
        // ByteRate
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(28, 4), (uint)byteRate);
        // BlockAlign
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(32, 2), (ushort)blockAlign);
        // BitsPerSample
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(34, 2), (ushort)bitsPerSample);
        // "data"
        DataHeader.CopyTo(destination.Slice(36, 4));
        // Subchunk2Size = pcmByteLength
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(40, 4), (uint)pcmByteLength);
    }

    /// <summary>
    /// Writes a standard 44-byte RIFF WAVE header directly to a stream.
    /// </summary>
    public static void WriteWavHeader(
        Stream stream,
        int pcmByteLength,
        int sampleRate = OpticalConstants.CdAudioSampleRate,
        short channels = OpticalConstants.CdAudioChannels,
        short bitsPerSample = OpticalConstants.CdAudioBitsPerSample)
    {
        byte[] header = CreateWavHeader(pcmByteLength, sampleRate, channels, bitsPerSample);
        stream.Write(header, 0, header.Length);
    }

    /// <summary>
    /// Writes uncompressed PCM audio bytes along with a standard 44-byte RIFF WAVE header to disk.
    /// </summary>
    public static void WriteWavFile(
        string filePath,
        ReadOnlySpan<byte> pcmData,
        int sampleRate = OpticalConstants.CdAudioSampleRate,
        short channels = OpticalConstants.CdAudioChannels,
        short bitsPerSample = OpticalConstants.CdAudioBitsPerSample)
    {
        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        byte[] header = CreateWavHeader(pcmData.Length, sampleRate, channels, bitsPerSample);
        fileStream.Write(header, 0, header.Length);
        fileStream.Write(pcmData);
    }

    /// <summary>
    /// Inspects and parses the format metadata of a RIFF WAVE file.
    /// </summary>
    public static AudioFormatMetadata ProbeAudioFile(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadMetadata(stream, filePath);
    }

    /// <summary>
    /// Parses format metadata from a RIFF WAVE stream.
    /// </summary>
    public static AudioFormatMetadata ReadMetadata(Stream stream, string filePath = "")
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        long initialPos = stream.CanSeek ? stream.Position : 0;

        try
        {
            var header = ParseWavHeader(reader, stream);

            int bytesPerFrame = header.Channels * (header.BitsPerSample / 8);
            long totalFrames = bytesPerFrame > 0 ? header.DataSize / bytesPerFrame : 0;
            double durationSec = header.SampleRate > 0 ? (double)totalFrames / header.SampleRate : 0;
            TimeSpan duration = TimeSpan.FromSeconds(durationSec);

            // Canonical Red Book: 44,100 Hz, 16-bit (2 bytes), 2 channels = 4 bytes per frame
            long redBookFrames = (long)Math.Round(durationSec * OpticalConstants.CdAudioSampleRate);
            long totalRedBookBytes = redBookFrames * OpticalConstants.CdAudioBytesPerSample;

            string formatDesc = header.IsFloat
                ? $"WAV IEEE Float {header.BitsPerSample}-bit"
                : $"WAV PCM {header.BitsPerSample}-bit";

            return new AudioFormatMetadata
            {
                FilePath = filePath,
                FormatName = formatDesc,
                Channels = header.Channels,
                SampleRate = header.SampleRate,
                BitsPerSample = header.BitsPerSample,
                Duration = duration,
                TotalPcmBytes = header.DataSize,
                TotalRedBookBytes = totalRedBookBytes,
                Title = header.Title,
                Artist = header.Artist
            };
        }
        finally
        {
            if (stream.CanSeek)
            {
                stream.Position = initialPos;
            }
        }
    }

    /// <summary>
    /// Reads and decodes a RIFF WAVE file into canonical Red Book CD-DA PCM
    /// (44,100 Hz, 16-bit signed LE, 2 channels).
    /// </summary>
    public static MemoryStream OpenRedBookPcmStream(string filePath)
    {
        byte[] pcm = DecodeToRedBookPcm(filePath);
        return new MemoryStream(pcm, 0, pcm.Length, writable: false, publiclyVisible: true);
    }

    /// <summary>
    /// Decodes a RIFF WAVE file into canonical Red Book PCM bytes.
    /// </summary>
    public static byte[] DecodeToRedBookPcm(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return DecodeToRedBookPcm(stream);
    }

    /// <summary>
    /// Decodes any supported RIFF WAVE stream into canonical Red Book PCM bytes
    /// (44,100 Hz, 16-bit signed LE, 2 channels).
    /// </summary>
    public static byte[] DecodeToRedBookPcm(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var parsed = ParseWavHeader(reader, stream);

        if (parsed.DataSize <= 0)
        {
            return Array.Empty<byte>();
        }

        // Seek to data chunk start
        if (stream.CanSeek && stream.Position != parsed.DataOffset)
        {
            stream.Position = parsed.DataOffset;
        }

        byte[] rawAudioBytes = reader.ReadBytes((int)parsed.DataSize);

        int bytesPerSample = parsed.BitsPerSample / 8;
        int bytesPerInputFrame = parsed.Channels * bytesPerSample;
        int inputFrames = bytesPerInputFrame > 0 ? rawAudioBytes.Length / bytesPerInputFrame : 0;

        if (inputFrames <= 0)
        {
            return Array.Empty<byte>();
        }

        // Fast-path 1: Already Red Book PCM (44.1 kHz, 16-bit signed LE, 2 channels, non-float)
        if (parsed.SampleRate == OpticalConstants.CdAudioSampleRate &&
            parsed.Channels == OpticalConstants.CdAudioChannels &&
            parsed.BitsPerSample == OpticalConstants.CdAudioBitsPerSample &&
            !parsed.IsFloat)
        {
            int exactLength = inputFrames * OpticalConstants.CdAudioBytesPerSample;
            if (rawAudioBytes.Length == exactLength)
            {
                return rawAudioBytes;
            }
            byte[] fastCopy = new byte[exactLength];
            Array.Copy(rawAudioBytes, fastCopy, exactLength);
            return fastCopy;
        }

        // Fast-path 2: 44.1 kHz, 16-bit signed LE, 1 channel (Mono) -> direct stereo duplication without float conversion
        if (parsed.SampleRate == OpticalConstants.CdAudioSampleRate &&
            parsed.Channels == 1 &&
            parsed.BitsPerSample == 16 &&
            !parsed.IsFloat)
        {
            byte[] stereoBytes = new byte[inputFrames * 4];
            ReadOnlySpan<byte> inSpan = rawAudioBytes;
            Span<byte> outSpan = stereoBytes;
            for (int i = 0; i < inputFrames; i++)
            {
                short sample = BinaryPrimitives.ReadInt16LittleEndian(inSpan.Slice(i * 2, 2));
                BinaryPrimitives.WriteInt16LittleEndian(outSpan.Slice(i * 4, 2), sample);
                BinaryPrimitives.WriteInt16LittleEndian(outSpan.Slice(i * 4 + 2, 2), sample);
            }
            return stereoBytes;
        }

        long targetFramesLong = (long)Math.Round((double)inputFrames * OpticalConstants.CdAudioSampleRate / parsed.SampleRate);
        if (targetFramesLong <= 0 || targetFramesLong > 200_000_000)
        {
            throw new InvalidDataException($"Decoded audio frame count ({targetFramesLong}) is invalid or exceeds maximum supported Red Book CD-DA length.");
        }

        // General conversion: Extract normalized floating-point channels [-1.0f .. +1.0f]
        float[] leftChannel = new float[inputFrames];
        float[] rightChannel = new float[inputFrames];

        ExtractFloatingChannels(
            rawAudioBytes,
            parsed.Channels,
            parsed.BitsPerSample,
            parsed.IsFloat,
            inputFrames,
            leftChannel,
            rightChannel);

        // Resample to 44,100 Hz if needed
        float[] outLeft;
        float[] outRight;
        int outputFrames;

        if (parsed.SampleRate == OpticalConstants.CdAudioSampleRate)
        {
            outLeft = leftChannel;
            outRight = rightChannel;
            outputFrames = inputFrames;
        }
        else
        {
            outputFrames = (int)targetFramesLong;
            outLeft = new float[outputFrames];
            outRight = new float[outputFrames];

            ResampleStereo(
                leftChannel,
                rightChannel,
                parsed.SampleRate,
                OpticalConstants.CdAudioSampleRate,
                outLeft,
                outRight);
        }

        // Convert normalized float samples to 16-bit signed LE Stereo PCM
        byte[] result = new byte[outputFrames * OpticalConstants.CdAudioBytesPerSample];
        Span<byte> destSpan = result;

        for (int i = 0; i < outputFrames; i++)
        {
            short sL = (short)Math.Clamp(Math.Round(outLeft[i] * 32767.0f), short.MinValue, short.MaxValue);
            short sR = (short)Math.Clamp(Math.Round(outRight[i] * 32767.0f), short.MinValue, short.MaxValue);

            BinaryPrimitives.WriteInt16LittleEndian(destSpan.Slice(i * 4, 2), sL);
            BinaryPrimitives.WriteInt16LittleEndian(destSpan.Slice(i * 4 + 2, 2), sR);
        }

        return result;
    }

    private static void ExtractFloatingChannels(
        byte[] rawBytes,
        int channels,
        int bitsPerSample,
        bool isFloat,
        int frameCount,
        float[] left,
        float[] right)
    {
        ReadOnlySpan<byte> span = rawBytes;
        int bytesPerSample = bitsPerSample / 8;
        int frameBytes = channels * bytesPerSample;

        for (int i = 0; i < frameCount; i++)
        {
            int frameOffset = i * frameBytes;

            float sampleL;
            float sampleR;

            if (bitsPerSample == 8)
            {
                // 8-bit unsigned PCM: 0..255, center 128
                sampleL = (span[frameOffset] - 128) / 128.0f;
                sampleR = channels > 1 ? (span[frameOffset + 1] - 128) / 128.0f : sampleL;
            }
            else if (bitsPerSample == 16)
            {
                // 16-bit signed LE PCM
                short s0 = BinaryPrimitives.ReadInt16LittleEndian(span.Slice(frameOffset, 2));
                sampleL = s0 / 32768.0f;
                if (channels > 1)
                {
                    short s1 = BinaryPrimitives.ReadInt16LittleEndian(span.Slice(frameOffset + 2, 2));
                    sampleR = s1 / 32768.0f;
                }
                else
                {
                    sampleR = sampleL;
                }
            }
            else if (bitsPerSample == 24)
            {
                // 24-bit signed LE PCM
                int s0 = span[frameOffset] | (span[frameOffset + 1] << 8) | (span[frameOffset + 2] << 16);
                if ((s0 & 0x800000) != 0) s0 |= unchecked((int)0xFF000000);
                sampleL = s0 / 8388608.0f;

                if (channels > 1)
                {
                    int s1 = span[frameOffset + 3] | (span[frameOffset + 4] << 8) | (span[frameOffset + 5] << 16);
                    if ((s1 & 0x800000) != 0) s1 |= unchecked((int)0xFF000000);
                    sampleR = s1 / 8388608.0f;
                }
                else
                {
                    sampleR = sampleL;
                }
            }
            else if (bitsPerSample == 32)
            {
                if (isFloat)
                {
                    // 32-bit IEEE float LE
                    sampleL = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(frameOffset, 4));
                    sampleR = channels > 1
                        ? BinaryPrimitives.ReadSingleLittleEndian(span.Slice(frameOffset + 4, 4))
                        : sampleL;
                }
                else
                {
                    // 32-bit signed integer LE
                    int s0 = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(frameOffset, 4));
                    sampleL = (float)(s0 / 2147483648.0);
                    if (channels > 1)
                    {
                        int s1 = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(frameOffset + 4, 4));
                        sampleR = (float)(s1 / 2147483648.0);
                    }
                    else
                    {
                        sampleR = sampleL;
                    }
                }
            }
            else
            {
                throw new NotSupportedException($"WAV bit depth {bitsPerSample} is not supported.");
            }

            left[i] = sampleL;
            right[i] = sampleR;
        }
    }

    /// <summary>
    /// Resamples stereo audio channels to the target sample rate using band-limited / linear interpolation.
    /// </summary>
    private static void ResampleStereo(
        float[] inLeft,
        float[] inRight,
        int inRate,
        int outRate,
        float[] outLeft,
        float[] outRight)
    {
        int inCount = inLeft.Length;
        int outCount = outLeft.Length;
        if (inCount == 0 || outCount == 0) return;

        double step = (double)inRate / outRate;

        for (int i = 0; i < outCount; i++)
        {
            double pos = i * step;
            int idx = (int)pos;
            float frac = (float)(pos - idx);

            if (idx >= inCount - 1)
            {
                outLeft[i] = inLeft[inCount - 1];
                outRight[i] = inRight[inCount - 1];
            }
            else
            {
                outLeft[i] = (1.0f - frac) * inLeft[idx] + frac * inLeft[idx + 1];
                outRight[i] = (1.0f - frac) * inRight[idx] + frac * inRight[idx + 1];
            }
        }
    }

    private sealed class ParsedWavInfo
    {
        public ushort AudioFormat { get; set; }
        public ushort Channels { get; set; }
        public int SampleRate { get; set; }
        public ushort BitsPerSample { get; set; }
        public bool IsFloat { get; set; }
        public long DataOffset { get; set; }
        public long DataSize { get; set; }
        public string? Title { get; set; }
        public string? Artist { get; set; }
    }

    private static ParsedWavInfo ParseWavHeader(BinaryReader reader, Stream stream)
    {
        byte[] riff = reader.ReadBytes(4);
        if (riff.Length < 4 || !riff.AsSpan().SequenceEqual(RiffHeader))
        {
            throw new InvalidDataException("Invalid WAVE file: missing 'RIFF' header signature.");
        }

        uint fileSizeMinus8 = reader.ReadUInt32();
        byte[] wave = reader.ReadBytes(4);
        if (wave.Length < 4 || !wave.AsSpan().SequenceEqual(WaveHeader))
        {
            throw new InvalidDataException("Invalid WAVE file: missing 'WAVE' format signature.");
        }

        var info = new ParsedWavInfo();
        bool fmtFound = false;
        bool dataFound = false;

        while (stream.Position < stream.Length)
        {
            byte[] chunkIdBytes = reader.ReadBytes(4);
            if (chunkIdBytes.Length < 4) break;

            uint chunkSize = reader.ReadUInt32();
            long chunkStart = stream.Position;

            if (chunkIdBytes.AsSpan().SequenceEqual(FmtHeader))
            {
                if (chunkSize < 16)
                {
                    throw new InvalidDataException("Invalid WAVE file: 'fmt ' chunk size must be at least 16 bytes.");
                }

                fmtFound = true;
                info.AudioFormat = reader.ReadUInt16();
                info.Channels = reader.ReadUInt16();
                info.SampleRate = (int)reader.ReadUInt32();
                uint byteRate = reader.ReadUInt32();
                ushort blockAlign = reader.ReadUInt16();
                info.BitsPerSample = reader.ReadUInt16();

                if (info.AudioFormat != WaveFormatPcm &&
                    info.AudioFormat != WaveFormatIeeeFloat &&
                    info.AudioFormat != WaveFormatExtensible)
                {
                    throw new NotSupportedException($"Unsupported WAVE audio format tag: 0x{info.AudioFormat:X4}.");
                }

                if (info.Channels is < 1 or > 32)
                {
                    throw new InvalidDataException($"Invalid WAVE channel count: {info.Channels}.");
                }

                if (info.SampleRate is < 1000 or > 384000)
                {
                    throw new InvalidDataException($"Invalid WAVE sample rate: {info.SampleRate} Hz.");
                }

                if (info.AudioFormat == WaveFormatIeeeFloat)
                {
                    info.IsFloat = true;
                }
                else if (info.AudioFormat == WaveFormatExtensible && chunkSize >= 40)
                {
                    ushort cbSize = reader.ReadUInt16();
                    ushort validBits = reader.ReadUInt16();
                    uint channelMask = reader.ReadUInt32();
                    byte[] guidBytes = reader.ReadBytes(16);
                    if (guidBytes.Length == 16)
                    {
                        var subFormat = new Guid(guidBytes);
                        if (subFormat == SubFormatIeeeFloat)
                        {
                            info.IsFloat = true;
                        }
                        else if (subFormat != SubFormatPcm)
                        {
                            throw new NotSupportedException($"Unsupported WAVE extensible sub-format: {subFormat}.");
                        }
                    }
                }
            }
            else if (chunkIdBytes.AsSpan().SequenceEqual(DataHeader))
            {
                dataFound = true;
                info.DataOffset = chunkStart;
                info.DataSize = chunkSize;

                // Adjust data size if stream is shorter than declared chunkSize
                if (stream.CanSeek && (chunkStart + chunkSize) > stream.Length)
                {
                    info.DataSize = Math.Max(0, stream.Length - chunkStart);
                }

                // If fmt was already found, we can proceed or keep scanning for LIST metadata
                if (stream.CanSeek)
                {
                    stream.Position = Math.Min(stream.Length, chunkStart + chunkSize);
                    if ((chunkSize % 2) != 0 && stream.Position < stream.Length)
                    {
                        stream.Position++; // Pad byte
                    }
                    continue;
                }
                break;
            }
            else if (chunkIdBytes.AsSpan().SequenceEqual("LIST"u8) && chunkSize > 4)
            {
                byte[] listType = reader.ReadBytes(4);
                if (listType.AsSpan().SequenceEqual("INFO"u8))
                {
                    long listEnd = chunkStart + chunkSize;
                    if (stream.CanSeek)
                    {
                        listEnd = Math.Min(listEnd, stream.Length);
                    }
                    while (stream.Position + 8 <= listEnd)
                    {
                        byte[] tagBytes = reader.ReadBytes(4);
                        if (tagBytes.Length < 4) break;
                        uint tagSize = reader.ReadUInt32();
                        if (tagSize > 0 && tagSize <= 65536 && stream.Position + tagSize <= listEnd)
                        {
                            byte[] valBytes = reader.ReadBytes((int)tagSize);
                            string text = Encoding.ASCII.GetString(valBytes).TrimEnd('\0', ' ');
                            if (tagBytes.AsSpan().SequenceEqual("INAM"u8))
                            {
                                info.Title = text;
                            }
                            else if (tagBytes.AsSpan().SequenceEqual("IART"u8))
                            {
                                info.Artist = text;
                            }
                            if ((tagSize % 2) != 0 && stream.Position < listEnd)
                            {
                                stream.Position++;
                            }
                        }
                        else
                        {
                            break;
                        }
                    }
                }
            }

            // Seek past current chunk
            if (stream.CanSeek)
            {
                stream.Position = Math.Min(stream.Length, chunkStart + chunkSize);
                if ((chunkSize % 2) != 0 && stream.Position < stream.Length)
                {
                    stream.Position++; // RIFF word alignment
                }
            }
            else
            {
                long toSkip = (long)chunkSize + ((chunkSize % 2) != 0 ? 1L : 0L);
                if (toSkip > 64 * 1024 * 1024)
                {
                    throw new InvalidDataException($"WAVE chunk size ({chunkSize}) is too large to skip on a non-seekable stream.");
                }
                reader.ReadBytes((int)toSkip);
            }
        }

        if (!fmtFound)
        {
            throw new InvalidDataException("Invalid WAVE file: missing 'fmt ' chunk.");
        }
        if (!dataFound)
        {
            throw new InvalidDataException("Invalid WAVE file: missing 'data' chunk.");
        }

        return info;
    }
}
