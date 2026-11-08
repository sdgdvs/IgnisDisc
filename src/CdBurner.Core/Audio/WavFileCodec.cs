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

        bool isRedBookStereo = parsed.SampleRate == OpticalConstants.CdAudioSampleRate &&
                               parsed.Channels == OpticalConstants.CdAudioChannels &&
                               parsed.BitsPerSample == OpticalConstants.CdAudioBitsPerSample &&
                               !parsed.IsFloat;
        bool isRedBookMono = parsed.SampleRate == OpticalConstants.CdAudioSampleRate &&
                             parsed.Channels == 1 &&
                             parsed.BitsPerSample == 16 &&
                             !parsed.IsFloat;

        // Everything that needs resampling, re-quantizing or downmixing streams through the shared band-limited
        // converter (same quality as FLAC, no whole-file float copies, no 2 GB limit).
        if (!isRedBookStereo && !isRedBookMono)
        {
            return ConvertViaRedBookConverter(stream, parsed);
        }

        byte[] rawAudioBytes = reader.ReadBytes((int)Math.Min(parsed.DataSize, int.MaxValue));

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

        // Unreachable: only the two Red Book fast paths above reach this point.
        throw new InvalidOperationException("Unexpected WAV layout after fast-path selection.");
    }

    /// <summary>
    /// Streams the data chunk through <see cref="RedBookPcmConverter"/> in fixed-size blocks: band-limited resampling,
    /// multichannel downmix and dithered re-quantization, using memory proportional to the Red Book output only.
    /// </summary>
    private static byte[] ConvertViaRedBookConverter(Stream stream, ParsedWavInfo parsed)
    {
        int bytesPerSample = parsed.BitsPerSample / 8;
        int channels = parsed.Channels;
        if (channels is < 1 or > 8)
        {
            throw new NotSupportedException($"WAV files with {channels} channels are not supported (1 to 8).");
        }

        if (parsed.IsFloat ? parsed.BitsPerSample != 32 : bytesPerSample is < 1 or > 4 || parsed.BitsPerSample % 8 != 0)
        {
            throw new NotSupportedException($"WAV bit depth {parsed.BitsPerSample}{(parsed.IsFloat ? " (float)" : string.Empty)} is not supported.");
        }

        int frameBytes = channels * bytesPerSample;
        long totalFrames = parsed.DataSize / frameBytes;
        if (totalFrames <= 0)
        {
            return Array.Empty<byte>();
        }

        if (RedBookPcmConverter.ExpectedOutputFrames(totalFrames, parsed.SampleRate) > RedBookPcmConverter.MaxOutputFrames)
        {
            throw new InvalidDataException("WAV audio is longer than any Red Book Audio CD can hold.");
        }

        // 32-bit float is mapped onto the full signed 32-bit integer range.
        int sourceBits = parsed.IsFloat ? 32 : parsed.BitsPerSample;
        var converter = new RedBookPcmConverter(parsed.SampleRate, totalFrames);

        const int blockFrames = 16384;
        byte[] buffer = new byte[blockFrames * frameBytes];
        var samples = new int[channels][];
        for (int c = 0; c < channels; c++) samples[c] = new int[blockFrames];

        long remaining = totalFrames;
        while (remaining > 0)
        {
            int want = (int)Math.Min(blockFrames, remaining);
            int gotBytes = 0;
            int wantBytes = want * frameBytes;
            while (gotBytes < wantBytes)
            {
                int r = stream.Read(buffer, gotBytes, wantBytes - gotBytes);
                if (r <= 0) break;
                gotBytes += r;
            }

            int got = gotBytes / frameBytes;
            if (got == 0) break;

            ReadOnlySpan<byte> span = buffer;
            for (int i = 0; i < got; i++)
            {
                int frameOffset = i * frameBytes;
                for (int c = 0; c < channels; c++)
                {
                    int o = frameOffset + (c * bytesPerSample);
                    samples[c][i] = bytesPerSample switch
                    {
                        1 => span[o] - 128, // 8-bit WAV is unsigned
                        2 => BinaryPrimitives.ReadInt16LittleEndian(span.Slice(o, 2)),
                        3 => (span[o] | (span[o + 1] << 8) | (span[o + 2] << 16)) << 8 >> 8, // sign-extend 24-bit
                        _ => parsed.IsFloat
                            ? FloatToInt32(BinaryPrimitives.ReadSingleLittleEndian(span.Slice(o, 4)))
                            : BinaryPrimitives.ReadInt32LittleEndian(span.Slice(o, 4))
                    };
                }
            }

            converter.WriteBlock(samples, channels, sourceBits, got);
            remaining -= got;
            if (got < want) break;
        }

        return converter.Finish();

        static int FloatToInt32(float f)
        {
            if (float.IsNaN(f)) return 0;
            // ±1.0 is full scale; the 2^31 scale keeps -1.0 exact (int.MinValue) and clamps +1.0 to int.MaxValue.
            double scaled = Math.Round(Math.Clamp(f, -1.0f, 1.0f) * 2147483648.0);
            return (int)Math.Clamp(scaled, int.MinValue, int.MaxValue);
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

        while (!stream.CanSeek || stream.Position < stream.Length)
        {
            byte[] chunkIdBytes = reader.ReadBytes(4);
            if (chunkIdBytes.Length < 4) break;

            uint chunkSize = reader.ReadUInt32();
            long chunkStart = stream.CanSeek ? stream.Position : 0;
            int bytesConsumedInChunk = 0;

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
                bytesConsumedInChunk = 16;

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
                    bytesConsumedInChunk += 8 + guidBytes.Length;
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
                bytesConsumedInChunk += listType.Length;
                long infoRead = 0;
                long maxInfoBytes = (long)chunkSize - 4;
                if (listType.AsSpan().SequenceEqual("INFO"u8))
                {
                    long listEnd = chunkStart + chunkSize;
                    if (stream.CanSeek)
                    {
                        listEnd = Math.Min(listEnd, stream.Length);
                    }
                    while (infoRead + 8 <= maxInfoBytes)
                    {
                        byte[] tagBytes = reader.ReadBytes(4);
                        if (tagBytes.Length < 4) break;
                        uint tagSize = reader.ReadUInt32();
                        bytesConsumedInChunk += 4 + tagBytes.Length;
                        infoRead += 8;
                        if (tagSize > 0 && tagSize <= 65536 && infoRead + tagSize <= maxInfoBytes)
                        {
                            byte[] valBytes = reader.ReadBytes((int)tagSize);
                            bytesConsumedInChunk += valBytes.Length;
                            infoRead += valBytes.Length;
                            string text = Encoding.ASCII.GetString(valBytes).TrimEnd('\0', ' ');
                            if (tagBytes.AsSpan().SequenceEqual("INAM"u8))
                            {
                                info.Title = text;
                            }
                            else if (tagBytes.AsSpan().SequenceEqual("IART"u8))
                            {
                                info.Artist = text;
                            }
                            if ((tagSize % 2) != 0 && infoRead < maxInfoBytes)
                            {
                                _ = reader.ReadBytes(1);
                                bytesConsumedInChunk++;
                                infoRead++;
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
                long toSkip = Math.Max(0L, (long)chunkSize - bytesConsumedInChunk) + ((chunkSize % 2) != 0 ? 1L : 0L);
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
