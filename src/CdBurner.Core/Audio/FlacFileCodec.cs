using System.Buffers.Binary;
using System.Text;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Audio;

/// <summary>
/// Probed format and Vorbis Comment / Picture metadata from a FLAC bitstream.
/// </summary>
public sealed record FlacMetadata
{
    public string FilePath { get; init; } = string.Empty;
    public string FormatName { get; init; } = "FLAC";
    public int Channels { get; init; }
    public int SampleRate { get; init; }
    public int BitsPerSample { get; init; }
    public TimeSpan Duration { get; init; }
    public long TotalSamples { get; init; }
    public long TotalPcmBytes { get; init; }
    public long TotalRedBookBytes { get; init; }
    public int TotalFrames => (int)((TotalRedBookBytes + 2351) / 2352);
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public string? AlbumArtist { get; init; }
    public string? Songwriter { get; init; }
    public string? Composer { get; init; }
    public string? Isrc { get; init; }
    public string? Date { get; init; }
    public int? TrackNumber { get; init; }
    public int? TrackTotal { get; init; }
    public string? ReplayGainTrackGain { get; init; }
    public string? ReplayGainTrackPeak { get; init; }
    public string? ReplayGainAlbumGain { get; init; }
    public string? ReplayGainAlbumPeak { get; init; }
    public bool HasEmbeddedCoverArt { get; init; }
    public byte[]? CoverArtBytes { get; init; }
    public string? CoverArtMimeType { get; init; }
}

/// <summary>What a full decode of a FLAC file found (see <see cref="FlacFileCodec.CheckIntegrity(Stream)"/>).</summary>
/// <param name="StatedSamples">Length in samples the header gives (0 = not given).</param>
/// <param name="DecodedSamples">Samples produced, including silence put in place of damaged frames.</param>
/// <param name="ConcealedSamples">Samples of silence put in place of frames that failed their checks.</param>
/// <param name="MissingSamplesAtEnd">Samples the header promises that the file does not hold (cut short).</param>
/// <param name="SamplesBeyondStatedLength">Decodable samples after the length the header gives (a damaged length).</param>
/// <param name="Md5Matches">Whether the audio matches the MD5 in the header; null when the encoder stored none.</param>
/// <param name="SampleRate">Samples a second, for describing the damage in time.</param>
public sealed record FlacIntegrityReport(
    long StatedSamples,
    long DecodedSamples,
    long ConcealedSamples,
    long MissingSamplesAtEnd,
    long SamplesBeyondStatedLength,
    bool? Md5Matches,
    int SampleRate)
{
    public bool IsDamaged => ConcealedSamples > 0 || MissingSamplesAtEnd > 0 || SamplesBeyondStatedLength > 0 || Md5Matches == false;

    /// <summary>One plain sentence naming each kind of damage found, or "intact".</summary>
    public string Describe()
    {
        if (!IsDamaged)
        {
            return Md5Matches == true ? "intact (MD5 matches)." : "intact.";
        }

        var parts = new List<string>();
        if (ConcealedSamples > 0) parts.Add($"{Time(ConcealedSamples)} of audio failed its checks and would be replaced by silence");
        if (MissingSamplesAtEnd > 0) parts.Add($"the file ends {Time(MissingSamplesAtEnd)} early (cut short)");
        if (SamplesBeyondStatedLength > 0) parts.Add($"the length in its header is wrong ({Time(SamplesBeyondStatedLength)} of audio after it would be dropped)");
        if (Md5Matches == false) parts.Add("the audio does not match the MD5 fingerprint stored in the file");
        return string.Join("; ", parts) + ".";
    }

    private string Time(long samples)
    {
        double seconds = SampleRate > 0 ? (double)samples / SampleRate : 0;
        return seconds >= 1 ? $"{seconds:0.#} s" : $"{seconds * 1000:0} ms";
    }
}

/// <summary>
/// Pure C# RFC 9639 Free Lossless Audio Codec (FLAC) parser, Vorbis Comment tag reader, and lossless PCM decoder.
/// Supports 8/12/16/20/24/32-bit sample depths, arbitrary sample rates, mono/stereo/multichannel streams,
/// prepended ID3v2 headers of any size, and automatic conversion to canonical Red Book CD-DA PCM (44.1 kHz, 16-bit Stereo).
/// </summary>
public static class FlacFileCodec
{
    private static readonly byte[] FlacMagic = "fLaC"u8.ToArray();
    private static readonly byte[] Id3Magic = "ID3"u8.ToArray();

    /// <summary>
    /// Writes a standard RFC 9639 lossless FLAC file to disk from interleaved little-endian PCM bytes
    /// (8, 16, or 24-bit) with optional Vorbis Comment metadata tags.
    /// </summary>
    public static void WriteFlacFile(
        string filePath,
        ReadOnlySpan<byte> pcmData,
        int sampleRate = OpticalConstants.CdAudioSampleRate,
        short channels = OpticalConstants.CdAudioChannels,
        short bitsPerSample = OpticalConstants.CdAudioBitsPerSample,
        AudioTrackTags? tags = null,
        string? songwriter = null,
        string? composer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        byte[] flacBytes = CreateFlacBytes(pcmData, sampleRate, channels, bitsPerSample, tags, songwriter, composer);
        File.WriteAllBytes(filePath, flacBytes);
    }

    /// <summary>
    /// Encodes interleaved little-endian PCM bytes (8, 16, or 24-bit) into a valid RFC 9639 FLAC byte stream
    /// with STREAMINFO and optional VORBIS_COMMENT metadata blocks.
    /// </summary>
    public static byte[] CreateFlacBytes(
        ReadOnlySpan<byte> pcmData,
        int sampleRate = OpticalConstants.CdAudioSampleRate,
        short channels = OpticalConstants.CdAudioChannels,
        short bitsPerSample = OpticalConstants.CdAudioBitsPerSample,
        AudioTrackTags? tags = null,
        string? songwriter = null,
        string? composer = null)
    {
        if (channels is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(channels), "FLAC supports 1 to 8 channels.");
        }

        if (bitsPerSample is not (8 or 16 or 24))
        {
            throw new ArgumentOutOfRangeException(nameof(bitsPerSample), "WriteFlacFile supports 8, 16, or 24 bits per sample.");
        }

        int bytesPerSample = bitsPerSample / 8;
        int bytesPerFrame = channels * bytesPerSample;
        int totalSamples = bytesPerFrame > 0 ? pcmData.Length / bytesPerFrame : 0;
        const int maxBlockSize = 4096;
        int minBlockSize = totalSamples > 0 ? Math.Min(totalSamples, maxBlockSize) : maxBlockSize;

        using var ms = new MemoryStream(pcmData.Length + 512);

        // 1. "fLaC" magic
        ms.Write(FlacMagic);

        // 2. STREAMINFO metadata block (type 0, 34 bytes)
        bool hasPictureBlock = tags?.CoverArtBytes is { Length: > 0 };
        bool hasVorbisBlock = tags != null || !string.IsNullOrWhiteSpace(songwriter) || !string.IsNullOrWhiteSpace(composer);
        bool hasAnyMetadata = hasVorbisBlock || hasPictureBlock;
        Span<byte> streamInfoBlock = stackalloc byte[4 + 34];
        streamInfoBlock[0] = hasAnyMetadata ? (byte)0x00 : (byte)0x80; // isLast flag + type 0
        streamInfoBlock[1] = 0;
        streamInfoBlock[2] = 0;
        streamInfoBlock[3] = 34;

        BinaryPrimitives.WriteUInt16BigEndian(streamInfoBlock.Slice(4, 2), (ushort)minBlockSize);
        BinaryPrimitives.WriteUInt16BigEndian(streamInfoBlock.Slice(6, 2), (ushort)maxBlockSize);
        // Min/Max frame size = 0 (unknown)
        ulong packed = ((ulong)sampleRate << 44)
                     | ((ulong)(channels - 1) << 41)
                     | ((ulong)(bitsPerSample - 1) << 36)
                     | ((ulong)totalSamples & 0x0FFFFFFFFFUL);
        BinaryPrimitives.WriteUInt64BigEndian(streamInfoBlock.Slice(14, 8), packed);
        // 16-byte MD5 signature left as 0 (valid per RFC 9639 when uncomputed)
        ms.Write(streamInfoBlock);

        // 3. Optional VORBIS_COMMENT metadata block (type 4)
        if (hasVorbisBlock)
        {
            var comments = new List<string>();
            if (tags != null)
            {
                if (!string.IsNullOrWhiteSpace(tags.Title)) comments.Add($"TITLE={tags.Title}");
                if (!string.IsNullOrWhiteSpace(tags.Artist)) comments.Add($"ARTIST={tags.Artist}");
                if (!string.IsNullOrWhiteSpace(tags.Album)) comments.Add($"ALBUM={tags.Album}");
                if (tags.TrackNumber > 0) comments.Add($"TRACKNUMBER={tags.TrackNumber}");
                if (tags.TrackTotal > 0) comments.Add($"TRACKTOTAL={tags.TrackTotal}");
                if (!string.IsNullOrWhiteSpace(tags.Isrc)) comments.Add($"ISRC={tags.Isrc}");
                if (!string.IsNullOrWhiteSpace(tags.Date)) comments.Add($"DATE={tags.Date}");
                if (!string.IsNullOrWhiteSpace(tags.ReplayGainTrackGain)) comments.Add($"REPLAYGAIN_TRACK_GAIN={tags.ReplayGainTrackGain}");
                if (!string.IsNullOrWhiteSpace(tags.ReplayGainTrackPeak)) comments.Add($"REPLAYGAIN_TRACK_PEAK={tags.ReplayGainTrackPeak}");
                if (!string.IsNullOrWhiteSpace(tags.ReplayGainAlbumGain)) comments.Add($"REPLAYGAIN_ALBUM_GAIN={tags.ReplayGainAlbumGain}");
                if (!string.IsNullOrWhiteSpace(tags.ReplayGainAlbumPeak)) comments.Add($"REPLAYGAIN_ALBUM_PEAK={tags.ReplayGainAlbumPeak}");
            }
            if (!string.IsNullOrWhiteSpace(songwriter)) comments.Add($"SONGWRITER={songwriter}");
            if (!string.IsNullOrWhiteSpace(composer)) comments.Add($"COMPOSER={composer}");

            using var vcBody = new MemoryStream();
            byte[] vendorBytes = "IgnisDisc"u8.ToArray();
            Span<byte> u32 = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)vendorBytes.Length);
            vcBody.Write(u32);
            vcBody.Write(vendorBytes);

            BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)comments.Count);
            vcBody.Write(u32);

            foreach (string c in comments)
            {
                byte[] cBytes = Encoding.UTF8.GetBytes(c);
                BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)cBytes.Length);
                vcBody.Write(u32);
                vcBody.Write(cBytes);
            }

            byte[] vcArray = vcBody.ToArray();
            Span<byte> vcHeader = stackalloc byte[4];
            bool isLastVc = !hasPictureBlock;
            vcHeader[0] = (byte)((isLastVc ? 0x80 : 0x00) | 0x04); // blockType 4 (VORBIS_COMMENT)
            vcHeader[1] = (byte)((vcArray.Length >> 16) & 0xFF);
            vcHeader[2] = (byte)((vcArray.Length >> 8) & 0xFF);
            vcHeader[3] = (byte)(vcArray.Length & 0xFF);
            ms.Write(vcHeader);
            ms.Write(vcArray);
        }

        // 3b. Optional METADATA_BLOCK_PICTURE block (type 6, isLast = true)
        if (hasPictureBlock && tags?.CoverArtBytes != null)
        {
            string mime = !string.IsNullOrWhiteSpace(tags.CoverArtMimeType) ? tags.CoverArtMimeType : "image/jpeg";
            byte[] mimeBytes = Encoding.ASCII.GetBytes(mime);
            using var picMs = new MemoryStream(32 + mimeBytes.Length + tags.CoverArtBytes.Length);
            Span<byte> u32 = stackalloc byte[4];

            BinaryPrimitives.WriteUInt32BigEndian(u32, 3); // Cover (front)
            picMs.Write(u32);

            BinaryPrimitives.WriteUInt32BigEndian(u32, (uint)mimeBytes.Length);
            picMs.Write(u32);
            picMs.Write(mimeBytes);

            BinaryPrimitives.WriteUInt32BigEndian(u32, 0); // Description len
            picMs.Write(u32);

            Span<byte> zeros16 = stackalloc byte[16];
            picMs.Write(zeros16);

            BinaryPrimitives.WriteUInt32BigEndian(u32, (uint)tags.CoverArtBytes.Length);
            picMs.Write(u32);
            picMs.Write(tags.CoverArtBytes);

            byte[] picArray = picMs.ToArray();
            Span<byte> picHeader = stackalloc byte[4];
            picHeader[0] = 0x86; // isLast (0x80) | blockType 6 (PICTURE)
            picHeader[1] = (byte)((picArray.Length >> 16) & 0xFF);
            picHeader[2] = (byte)((picArray.Length >> 8) & 0xFF);
            picHeader[3] = (byte)(picArray.Length & 0xFF);
            ms.Write(picHeader);
            ms.Write(picArray);
        }

        // 4. Write audio frames using SUBFRAME_VERBATIM
        byte sampleSizeCode = bitsPerSample switch
        {
            8 => 1,
            16 => 4,
            24 => 6,
            _ => 0
        };

        int sampleOffset = 0;
        int frameNumber = 0;
        using var frameMem = new MemoryStream(maxBlockSize * bytesPerFrame + 64);

        while (sampleOffset < totalSamples)
        {
            int blockSamples = Math.Min(maxBlockSize, totalSamples - sampleOffset);
            frameMem.SetLength(0);

            // Frame header:
            // Sync (0xFFF8 = fixed blocksize strategy), blockSizeCode = 7 (16-bit blockSize-1 at end of header),
            // sampleRateCode = 0 (get from STREAMINFO)
            frameMem.WriteByte(0xFF);
            frameMem.WriteByte(0xF8);
            frameMem.WriteByte(0x70); // blockSizeCode=7 (0111), sampleRateCode=0 (0000)
            byte chAndBps = (byte)(((channels - 1) << 4) | (sampleSizeCode << 1));
            frameMem.WriteByte(chAndBps);

            // UTF-8 coded frame number
            WriteUtf8Number(frameMem, (ulong)frameNumber);

            // 16-bit big-endian (blockSamples - 1)
            frameMem.WriteByte((byte)(((blockSamples - 1) >> 8) & 0xFF));
            frameMem.WriteByte((byte)((blockSamples - 1) & 0xFF));

            // Header CRC-8
            byte[] hdrBytes = frameMem.ToArray();
            byte crc8 = ComputeCrc8(hdrBytes);
            frameMem.WriteByte(crc8);

            // Subframes: one per channel
            for (int c = 0; c < channels; c++)
            {
                // Subframe header: 0 (pad) | 000001 (SUBFRAME_VERBATIM) | 0 (no wasted bits) = 0x02
                frameMem.WriteByte(0x02);

                for (int i = 0; i < blockSamples; i++)
                {
                    int srcIdx = ((sampleOffset + i) * bytesPerFrame) + (c * bytesPerSample);
                    if (bitsPerSample == 8)
                    {
                        // 8-bit WAV is unsigned (0..255, center 128); FLAC is signed (-128..127)
                        sbyte s = (sbyte)(pcmData[srcIdx] - 128);
                        frameMem.WriteByte((byte)s);
                    }
                    else if (bitsPerSample == 16)
                    {
                        short s = BinaryPrimitives.ReadInt16LittleEndian(pcmData.Slice(srcIdx, 2));
                        frameMem.WriteByte((byte)((s >> 8) & 0xFF));
                        frameMem.WriteByte((byte)(s & 0xFF));
                    }
                    else if (bitsPerSample == 24)
                    {
                        // Little-endian 24-bit in WAV -> Big-endian 24-bit in FLAC verbatim subframe
                        frameMem.WriteByte(pcmData[srcIdx + 2]);
                        frameMem.WriteByte(pcmData[srcIdx + 1]);
                        frameMem.WriteByte(pcmData[srcIdx]);
                    }
                }
            }

            // Frame footer: 16-bit big-endian CRC-16 (polynomial 0x8005)
            byte[] frameBody = frameMem.ToArray();
            ushort crc16 = ComputeCrc16(frameBody);
            ms.Write(frameBody);
            ms.WriteByte((byte)((crc16 >> 8) & 0xFF));
            ms.WriteByte((byte)(crc16 & 0xFF));

            sampleOffset += blockSamples;
            frameNumber++;
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Returns true if the file path has a .flac/.fla extension or begins with a FLAC / ID3+FLAC header.
    /// </summary>
    public static bool IsFlacFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;

        string ext = Path.GetExtension(filePath);
        if (string.Equals(ext, ".flac", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".fla", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!File.Exists(filePath)) return false;

        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return SkipId3v2AndVerifyFlacSignature(fs);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Inspects and parses the format and Vorbis Comment metadata of a FLAC file.
    /// </summary>
    public static AudioFormatMetadata ProbeAudioFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"FLAC audio file not found: '{filePath}'.", filePath);
        }

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ReadMetadata(stream, filePath);
    }

    /// <summary>
    /// Parses format and Vorbis Comment metadata from a FLAC stream.
    /// </summary>
    public static AudioFormatMetadata ReadMetadata(Stream stream, string filePath = "")
    {
        ArgumentNullException.ThrowIfNull(stream);
        long initialPos = stream.CanSeek ? stream.Position : 0;

        try
        {
            var info = ParseFlacMetadataBlocks(stream);

            long totalSamples = info.TotalSamples;
            if (totalSamples <= 0 && stream.CanSeek)
            {
                // Fallback for streaming FLAC files where STREAMINFO total_samples was left 0:
                // decode or count frame samples so duration is accurate.
                long frameStartPos = stream.Position;
                totalSamples = CountSamplesAcrossFrames(stream, info);
                stream.Position = frameStartPos;
            }

            double durationSec = info.SampleRate > 0 ? (double)totalSamples / info.SampleRate : 0.0;
            TimeSpan duration = TimeSpan.FromSeconds(durationSec);

            int bytesPerInputSample = Math.Max(1, (info.BitsPerSample + 7) / 8);
            long totalPcmBytes = totalSamples * info.Channels * bytesPerInputSample;

            long redBookFrames = (long)Math.Round(durationSec * OpticalConstants.CdAudioSampleRate);
            long totalRedBookBytes = redBookFrames * OpticalConstants.CdAudioBytesPerSample;

            string formatDesc = $"FLAC {info.BitsPerSample}-bit";

            return new AudioFormatMetadata
            {
                FilePath = filePath,
                FormatName = formatDesc,
                Channels = info.Channels,
                SampleRate = info.SampleRate,
                BitsPerSample = info.BitsPerSample,
                Duration = duration,
                TotalPcmBytes = totalPcmBytes,
                TotalRedBookBytes = totalRedBookBytes,
                Title = info.Title,
                Artist = info.Artist,
                Album = info.Album,
                AlbumArtist = info.AlbumArtist,
                Songwriter = info.Songwriter,
                Composer = info.Composer,
                Isrc = info.Isrc,
                Date = info.Date,
                TrackNumber = info.TrackNumber,
                TrackTotal = info.TrackTotal,
                ReplayGainTrackGain = info.ReplayGainTrackGain,
                ReplayGainTrackPeak = info.ReplayGainTrackPeak,
                ReplayGainAlbumGain = info.ReplayGainAlbumGain,
                ReplayGainAlbumPeak = info.ReplayGainAlbumPeak,
                HasEmbeddedCoverArt = info.HasEmbeddedCoverArt,
                CoverArtBytes = info.CoverArtBytes,
                CoverArtMimeType = info.CoverArtMimeType
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
    /// Inspects and parses the format, Vorbis Comment tags, ReplayGain tags, and embedded cover art of a FLAC file.
    /// </summary>
    public static FlacMetadata ProbeMetadata(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"FLAC audio file not found: '{filePath}'.", filePath);
        }

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ProbeMetadata(stream, filePath);
    }

    /// <summary>
    /// Parses format, Vorbis Comment tags, ReplayGain tags, and embedded cover art from a FLAC stream.
    /// </summary>
    public static FlacMetadata ProbeMetadata(Stream stream, string filePath = "")
    {
        var meta = ReadMetadata(stream, filePath);
        long samples = meta.Channels > 0 && meta.BitsPerSample > 0
            ? meta.TotalPcmBytes / (meta.Channels * Math.Max(1, (meta.BitsPerSample + 7) / 8))
            : 0;

        return new FlacMetadata
        {
            FilePath = meta.FilePath,
            FormatName = meta.FormatName,
            Channels = meta.Channels,
            SampleRate = meta.SampleRate,
            BitsPerSample = meta.BitsPerSample,
            Duration = meta.Duration,
            TotalSamples = samples,
            TotalPcmBytes = meta.TotalPcmBytes,
            TotalRedBookBytes = meta.TotalRedBookBytes,
            Title = meta.Title,
            Artist = meta.Artist,
            Album = meta.Album,
            AlbumArtist = meta.AlbumArtist,
            Songwriter = meta.Songwriter,
            Composer = meta.Composer,
            Isrc = meta.Isrc,
            Date = meta.Date,
            TrackNumber = meta.TrackNumber,
            TrackTotal = meta.TrackTotal,
            ReplayGainTrackGain = meta.ReplayGainTrackGain,
            ReplayGainTrackPeak = meta.ReplayGainTrackPeak,
            ReplayGainAlbumGain = meta.ReplayGainAlbumGain,
            ReplayGainAlbumPeak = meta.ReplayGainAlbumPeak,
            HasEmbeddedCoverArt = meta.HasEmbeddedCoverArt,
            CoverArtBytes = meta.CoverArtBytes,
            CoverArtMimeType = meta.CoverArtMimeType
        };
    }

    /// <summary>
    /// Opens and decodes a FLAC file into canonical Red Book CD-DA PCM
    /// (44,100 Hz, 16-bit signed LE, 2 channels).
    /// </summary>
    public static MemoryStream OpenRedBookPcmStream(string filePath)
    {
        byte[] pcm = DecodeToRedBookPcm(filePath);
        return new MemoryStream(pcm, 0, pcm.Length, writable: false, publiclyVisible: true);
    }

    /// <summary>
    /// Decodes a FLAC file into canonical Red Book CD-DA PCM bytes
    /// (44,100 Hz, 16-bit signed LE, 2 channels).
    /// </summary>
    public static byte[] DecodeToRedBookPcm(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"FLAC audio file not found: '{filePath}'.", filePath);
        }

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return DecodeToRedBookPcm(stream);
    }

    /// <summary>
    /// Decodes a FLAC stream into canonical Red Book CD-DA PCM bytes
    /// (44,100 Hz, 16-bit signed LE, 2 channels).
    /// </summary>
    public static byte[] DecodeToRedBookPcm(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var info = ParseFlacMetadataBlocks(stream);

        long expectedOut = info.TotalSamples > 0
            ? RedBookPcmConverter.ExpectedOutputFrames(info.TotalSamples, info.SampleRate)
            : 0;
        if (expectedOut > RedBookPcmConverter.MaxOutputFrames)
        {
            throw new InvalidDataException(
                $"FLAC stream is {TimeSpan.FromSeconds((double)info.TotalSamples / info.SampleRate):g} long, which exceeds any Red Book Audio CD.");
        }

        // Streaming decode: frames are decoded from a bounded window of the compressed stream and converted
        // block-by-block, so a 600 MB 24-bit/96 kHz album never needs more than the Red Book output in memory.
        var converter = new RedBookPcmConverter(info.SampleRate, info.TotalSamples);
        DecodeFrames(
            stream,
            info,
            frame => converter.WriteBlock(frame.Samples, frame.Channels, frame.BitsPerSample, frame.Count),
            converter.WriteSilence);

        return converter.Finish();
    }

    /// <summary>
    /// Decodes like <see cref="DecodeToRedBookPcm(Stream)"/>, but throws when the stream is damaged (see
    /// <see cref="CheckIntegrity(Stream)"/>) instead of handing back audio with silence in place of the damage.
    /// The stream must be seekable: it is read twice.
    /// </summary>
    public static byte[] DecodeToRedBookPcm(Stream stream, bool strict)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (strict)
        {
            long start = stream.Position;
            var report = CheckIntegrity(stream);
            if (report.IsDamaged)
            {
                throw new InvalidDataException("Damaged FLAC: " + report.Describe());
            }
            stream.Position = start;
        }
        return DecodeToRedBookPcm(stream);
    }

    /// <summary>Checks a FLAC file the way <c>flac -t</c> does, without keeping the audio.</summary>
    public static FlacIntegrityReport CheckIntegrity(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return CheckIntegrity(stream);
    }

    /// <summary>
    /// Decodes every frame and reports damage: frames that failed their CRC (concealed with silence), audio missing at
    /// the end, frames past the length the header gives, and an MD5 of the audio that differs from the one stored in
    /// the header (when the encoder stored one).
    /// </summary>
    /// <exception cref="InvalidDataException">Not a FLAC stream, or its header cannot be read.</exception>
    public static FlacIntegrityReport CheckIntegrity(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var info = ParseFlacMetadataBlocks(stream);
        bool hasMd5 = info.Md5 is { } stored && stored.AsSpan().ContainsAnyExcept((byte)0);
        using var md5 = hasMd5 ? System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.MD5) : null;
        var tally = new DecodeTally { Md5 = md5 };

        DecodeFrames(stream, info, _ => { }, _ => { }, tally);

        long missing = info.TotalSamples > 0 ? Math.Max(0, info.TotalSamples - tally.Emitted) : 0;
        bool? md5Matches = md5 == null ? null : md5.GetHashAndReset().AsSpan().SequenceEqual(info.Md5);
        return new FlacIntegrityReport(
            info.TotalSamples, tally.Emitted, tally.ConcealedSamples, missing, tally.SamplesBeyondStatedLength, md5Matches, info.SampleRate);
    }

    private sealed class ParsedFlacInfo
    {
        public int MinBlockSize { get; set; }
        public int MaxBlockSize { get; set; }
        public int MinFrameSize { get; set; }
        public int MaxFrameSize { get; set; }
        public int SampleRate { get; set; }
        public int Channels { get; set; }
        public int BitsPerSample { get; set; }
        public long TotalSamples { get; set; }
        public byte[]? Md5 { get; set; }
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? Album { get; set; }
        public string? AlbumArtist { get; set; }
        public string? Songwriter { get; set; }
        public string? Composer { get; set; }
        public string? Isrc { get; set; }
        public string? Date { get; set; }
        public int? TrackNumber { get; set; }
        public int? TrackTotal { get; set; }
        public string? ReplayGainTrackGain { get; set; }
        public string? ReplayGainTrackPeak { get; set; }
        public string? ReplayGainAlbumGain { get; set; }
        public string? ReplayGainAlbumPeak { get; set; }
        public bool HasEmbeddedCoverArt { get; set; }
        public byte[]? CoverArtBytes { get; set; }
        public string? CoverArtMimeType { get; set; }
    }

    private static bool SkipId3v2AndVerifyFlacSignature(Stream stream)
    {
        Span<byte> magic = stackalloc byte[4];

        bool skippedId3 = false;

        // Allow skipping one or more prepended ID3v2 tags
        for (int id3Pass = 0; id3Pass < 4; id3Pass++)
        {
            if (ReadFully(stream, magic) < 4) return false;

            if (magic.SequenceEqual(FlacMagic))
            {
                return true;
            }

            if (magic[..3].SequenceEqual(Id3Magic))
            {
                // Read remaining 6 bytes of the 10-byte ID3v2 header (already read 4 bytes: 'I','D','3', verMajor).
                // The syncsafe size already covers the extended header, padding and unsynchronised frame data,
                // so neither the unsynchronisation (0x80) nor extended-header (0x40) flag changes how far we skip.
                byte versionMajor = magic[3];
                Span<byte> id3Rest = stackalloc byte[6];
                if (ReadFully(stream, id3Rest) < 6) return false;

                byte flags = id3Rest[1];
                int size = ((id3Rest[2] & 0x7F) << 21) |
                           ((id3Rest[3] & 0x7F) << 14) |
                           ((id3Rest[4] & 0x7F) << 7) |
                           (id3Rest[5] & 0x7F);

                // Only ID3v2.4 defines the footer flag (bit 4, +10 bytes); in v2.2/2.3 that bit is reserved.
                if (versionMajor >= 4 && (flags & 0x10) != 0)
                {
                    size += 10;
                }

                SkipBytes(stream, size);
                skippedId3 = true;
                continue;
            }

            break;
        }

        // Some taggers write a tag size that undercounts trailing padding. After an ID3 tag, resynchronise on "fLaC"
        // within the next 64 KB instead of rejecting an otherwise valid file.
        if (skippedId3 && stream.CanSeek)
        {
            long start = stream.Position - 4;
            int window = (int)Math.Min(64 * 1024, Math.Max(0, stream.Length - start));
            byte[] scan = new byte[window];
            stream.Position = start;
            int got = ReadFully(stream, scan);
            int hit = scan.AsSpan(0, got).IndexOf(FlacMagic);
            if (hit >= 0)
            {
                stream.Position = start + hit + 4;
                return true;
            }
        }

        return false;
    }

    private static ParsedFlacInfo ParseFlacMetadataBlocks(Stream stream)
    {
        if (!SkipId3v2AndVerifyFlacSignature(stream))
        {
            throw new InvalidDataException("Invalid FLAC file: missing 'fLaC' stream signature.");
        }

        var info = new ParsedFlacInfo();
        bool streamInfoFound = false;
        Span<byte> blockHeader = stackalloc byte[4];

        while (true)
        {
            int read = ReadFully(stream, blockHeader);
            if (read < 4)
            {
                throw new InvalidDataException("Unexpected end of FLAC stream while reading metadata block header.");
            }

            bool isLast = (blockHeader[0] & 0x80) != 0;
            int blockType = blockHeader[0] & 0x7F;
            int length = (blockHeader[1] << 16) | (blockHeader[2] << 8) | blockHeader[3];

            if (blockType == 127)
            {
                throw new InvalidDataException("Invalid FLAC metadata block type 127.");
            }

            if (blockType == 0) // STREAMINFO (34 bytes)
            {
                if (length < 34)
                {
                    throw new InvalidDataException($"Invalid FLAC STREAMINFO block length: {length} (expected 34).");
                }

                byte[] buf = ReadExactBytes(stream, length);
                info.MinBlockSize = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(0, 2));
                info.MaxBlockSize = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(2, 2));
                info.MinFrameSize = (buf[4] << 16) | (buf[5] << 8) | buf[6];
                info.MaxFrameSize = (buf[7] << 16) | (buf[8] << 8) | buf[9];

                // Bytes 10..17 (64 bits BE):
                // [20 bits sample rate] [3 bits (channels - 1)] [5 bits (bps - 1)] [36 bits total samples]
                ulong packed = BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(10, 8));
                info.SampleRate = (int)(packed >> 44);
                info.Channels = (int)((packed >> 41) & 0x07UL) + 1;
                info.BitsPerSample = (int)((packed >> 36) & 0x1FUL) + 1;
                info.TotalSamples = (long)(packed & 0x0FFFFFFFFFUL);
                info.Md5 = buf.AsSpan(18, 16).ToArray(); // MD5 of the unencoded audio; all zero when the encoder left it out

                if (info.SampleRate is < 1 or > 655350)
                {
                    throw new InvalidDataException($"Invalid FLAC sample rate in STREAMINFO: {info.SampleRate} Hz.");
                }

                streamInfoFound = true;
            }
            else if (blockType == 4 && length > 0 && length <= 16 * 1024 * 1024) // VORBIS_COMMENT
            {
                byte[] buf = ReadExactBytes(stream, length);
                ParseVorbisComments(buf, info);
            }
            else if (blockType == 6 && length > 0 && length <= 32 * 1024 * 1024) // METADATA_BLOCK_PICTURE
            {
                byte[] buf = ReadExactBytes(stream, length);
                ParsePictureBlock(buf, info);
            }
            else
            {
                SkipBytes(stream, length);
            }

            if (isLast)
            {
                break;
            }
        }

        if (!streamInfoFound)
        {
            throw new InvalidDataException("Invalid FLAC file: missing mandatory STREAMINFO metadata block.");
        }

        return info;
    }

    private static void ParseVorbisComments(ReadOnlySpan<byte> block, ParsedFlacInfo info)
    {
        if (block.Length < 8) return;

        uint vendorLen = BinaryPrimitives.ReadUInt32LittleEndian(block[..4]);
        long pos = 4L + vendorLen;
        if (pos + 4 > block.Length) return;

        uint commentCount = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice((int)pos, 4));
        pos += 4;

        uint maxComments = Math.Min(commentCount, 256u);
        for (uint i = 0; i < maxComments; i++)
        {
            if (pos + 4 > block.Length) break;
            uint commentLen = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice((int)pos, 4));
            pos += 4;

            if (commentLen == 0 || pos + commentLen > block.Length)
            {
                pos += commentLen;
                continue;
            }

            string entry = Encoding.UTF8.GetString(block.Slice((int)pos, (int)commentLen));
            pos += commentLen;

            int eq = entry.IndexOf('=');
            if (eq <= 0) continue;

            string key = entry[..eq].Trim();
            string val = entry[(eq + 1)..].Trim();
            if (string.IsNullOrEmpty(val)) continue;

            if (key.Equals("TITLE", StringComparison.OrdinalIgnoreCase))
            {
                info.Title ??= val;
            }
            else if (key.Equals("ARTIST", StringComparison.OrdinalIgnoreCase) ||
                     key.Equals("PERFORMER", StringComparison.OrdinalIgnoreCase))
            {
                info.Artist ??= val;
            }
            else if (key.Equals("ALBUM", StringComparison.OrdinalIgnoreCase))
            {
                info.Album ??= val;
            }
            else if (key.Equals("ALBUMARTIST", StringComparison.OrdinalIgnoreCase) ||
                     key.Equals("ALBUM_ARTIST", StringComparison.OrdinalIgnoreCase) ||
                     key.Equals("ALBUM ARTIST", StringComparison.OrdinalIgnoreCase))
            {
                info.AlbumArtist ??= val;
            }
            else if (key.Equals("SONGWRITER", StringComparison.OrdinalIgnoreCase) ||
                     key.Equals("LYRICIST", StringComparison.OrdinalIgnoreCase))
            {
                info.Songwriter ??= val;
            }
            else if (key.Equals("COMPOSER", StringComparison.OrdinalIgnoreCase))
            {
                info.Composer ??= val;
            }
            else if (key.Equals("ISRC", StringComparison.OrdinalIgnoreCase))
            {
                info.Isrc ??= val;
            }
            else if (key.Equals("TRACKNUMBER", StringComparison.OrdinalIgnoreCase) ||
                     key.Equals("TRACK", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = val.Split('/');
                if (int.TryParse(parts[0].Trim(), out int tn) && tn > 0)
                {
                    info.TrackNumber ??= tn;
                }
                if (parts.Length > 1 && int.TryParse(parts[1].Trim(), out int tt) && tt > 0)
                {
                    info.TrackTotal ??= tt;
                }
            }
            else if (key.Equals("TRACKTOTAL", StringComparison.OrdinalIgnoreCase) ||
                     key.Equals("TOTALTRACKS", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(val, out int tt) && tt > 0)
                {
                    info.TrackTotal ??= tt;
                }
            }
            else if (key.Equals("DATE", StringComparison.OrdinalIgnoreCase) ||
                     key.Equals("YEAR", StringComparison.OrdinalIgnoreCase))
            {
                info.Date ??= val;
            }
            else if (key.Equals("REPLAYGAIN_TRACK_GAIN", StringComparison.OrdinalIgnoreCase))
            {
                info.ReplayGainTrackGain ??= val;
            }
            else if (key.Equals("REPLAYGAIN_TRACK_PEAK", StringComparison.OrdinalIgnoreCase))
            {
                info.ReplayGainTrackPeak ??= val;
            }
            else if (key.Equals("REPLAYGAIN_ALBUM_GAIN", StringComparison.OrdinalIgnoreCase))
            {
                info.ReplayGainAlbumGain ??= val;
            }
            else if (key.Equals("REPLAYGAIN_ALBUM_PEAK", StringComparison.OrdinalIgnoreCase))
            {
                info.ReplayGainAlbumPeak ??= val;
            }
        }
    }

    private static void ParsePictureBlock(ReadOnlySpan<byte> block, ParsedFlacInfo info)
    {
        if (block.Length < 32) return;

        int pos = 0;
        uint pictureType = BinaryPrimitives.ReadUInt32BigEndian(block.Slice(pos, 4));
        pos += 4;

        int mimeLen = (int)BinaryPrimitives.ReadUInt32BigEndian(block.Slice(pos, 4));
        pos += 4;
        if (mimeLen < 0 || mimeLen > block.Length - pos) return;

        string mime = Encoding.ASCII.GetString(block.Slice(pos, mimeLen));
        pos += mimeLen;

        if (pos + 4 > block.Length) return;
        int descLen = (int)BinaryPrimitives.ReadUInt32BigEndian(block.Slice(pos, 4));
        pos += 4;
        if (descLen < 0 || descLen > block.Length - pos) return;
        pos += descLen;

        if (pos + 16 > block.Length) return;
        pos += 16; // width (4), height (4), depth (4), colors (4)

        if (pos + 4 > block.Length) return;
        int dataLen = (int)BinaryPrimitives.ReadUInt32BigEndian(block.Slice(pos, 4));
        pos += 4;
        if (dataLen < 0 || dataLen > block.Length - pos) return;

        byte[] pictureData = block.Slice(pos, dataLen).ToArray();

        // Prefer Cover (front, type 3) or take first picture block
        if (!info.HasEmbeddedCoverArt || pictureType == 3)
        {
            info.HasEmbeddedCoverArt = true;
            info.CoverArtBytes = pictureData;
            info.CoverArtMimeType = mime;
        }
    }

    private static long CountSamplesAcrossFrames(Stream stream, ParsedFlacInfo info)
    {
        long total = 0;
        DecodeFrames(stream, info, frame => total += frame.Count, gap => total += gap);
        return total;
    }

    /// <summary>
    /// One decoded FLAC frame. <see cref="Samples"/> arrays are reused between frames and are only valid during the callback.
    /// </summary>
    private readonly record struct FlacFrame(int[][] Samples, int Channels, int BitsPerSample, int Count);

    /// <summary>
    /// Worst-case encoded size of one frame: every subframe verbatim at 33 bits/sample, plus headers.
    /// </summary>
    private static int EstimateMaxFrameBytes(ParsedFlacInfo info)
    {
        long maxBlock = info.MaxBlockSize > 0 ? info.MaxBlockSize : 65536;
        long bytes = 64 + (info.Channels * (8 + ((maxBlock * 33) + 7) / 8));
        if (info.MaxFrameSize > 0)
        {
            bytes = Math.Max(bytes, info.MaxFrameSize + 64L);
        }
        return (int)Math.Min(bytes, 16L * 1024 * 1024);
    }

    /// <summary>
    /// Decodes every audio frame from <paramref name="stream"/> (positioned just after the metadata blocks) through a bounded
    /// sliding window. Corrupt frames (bad header CRC-8, undecodable subframes, or bad CRC-16 footer) are dropped, and the gap
    /// they leave — derived from the coded frame/sample numbers — is reported via <paramref name="onGap"/> so later audio keeps
    /// its exact timing (critical for single-file CUE albums whose track offsets are absolute).
    /// </summary>
    /// <summary>What went wrong while decoding a stream; filled in by <see cref="DecodeFrames"/> when asked for.</summary>
    private sealed class DecodeTally
    {
        public long ConcealedSamples;
        public long SamplesBeyondStatedLength;
        public long Emitted;
        public System.Security.Cryptography.IncrementalHash? Md5;
    }

    /// <summary>Feeds decoded samples to the MD5 as RFC 9639 defines it: interleaved, signed little-endian, whole bytes.</summary>
    private static void HashSamples(System.Security.Cryptography.IncrementalHash md5, FlacFrame frame)
    {
        int width = (frame.BitsPerSample + 7) / 8;
        byte[] bytes = new byte[frame.Count * frame.Channels * width];
        int o = 0;
        for (int i = 0; i < frame.Count; i++)
        {
            for (int c = 0; c < frame.Channels; c++)
            {
                int v = frame.Samples[c][i];
                for (int b = 0; b < width; b++)
                {
                    bytes[o++] = (byte)(v >> (8 * b));
                }
            }
        }
        md5.AppendData(bytes);
    }

    private static void DecodeFrames(Stream stream, ParsedFlacInfo info, Action<FlacFrame> onFrame, Action<long> onGap, DecodeTally? tally = null)
    {
        int maxFrameBytes = EstimateMaxFrameBytes(info);
        byte[] buffer = new byte[Math.Max(4 * 1024 * 1024, maxFrameBytes * 3)];
        int valid = 0;
        bool eof = false;

        var samplePool = new int[8][];
        long? baselineSample = null;
        long emitted = 0;
        long totalLimit = info.TotalSamples > 0 ? info.TotalSamples : long.MaxValue;

        while (true)
        {
            while (!eof && valid < buffer.Length)
            {
                int r = stream.Read(buffer, valid, buffer.Length - valid);
                if (r <= 0) eof = true;
                else valid += r;
            }

            // A frame may only start where its worst-case body is fully inside the window (or anywhere once at EOF).
            int searchLimit = eof ? valid : valid - maxFrameBytes;
            var reader = new FlacBitReader(buffer.AsSpan(0, valid));

            while (TryDecodeNextFrame(ref reader, info, searchLimit, samplePool, out var frame, out long firstSample))
            {
                if (emitted >= totalLimit)
                {
                    if (tally != null) tally.SamplesBeyondStatedLength += frame.Count; // the header says the audio ended
                    continue;
                }

                // Conceal dropped/corrupt frames with silence so the timeline never shifts. A stream starts at
                // sample 0, so a lost first frame is concealed too instead of moving everything after it earlier.
                if (firstSample >= 0)
                {
                    baselineSample ??= 0;
                    long gap = firstSample - (baselineSample.Value + emitted);
                    if (gap > 0 && gap <= (long)info.SampleRate * 60)
                    {
                        gap = Math.Min(gap, totalLimit - emitted);
                        onGap(gap);
                        emitted += gap;
                        if (tally != null) tally.ConcealedSamples += gap;
                    }
                }

                int count = (int)Math.Min(frame.Count, totalLimit - emitted);
                if (count > 0)
                {
                    onFrame(frame with { Count = count });
                    if (tally?.Md5 != null) HashSamples(tally.Md5, frame with { Count = count });
                    emitted += count;
                }
                if (tally != null && count < frame.Count) tally.SamplesBeyondStatedLength += frame.Count - Math.Max(0, count);
            }

            if (eof)
            {
                if (tally != null) tally.Emitted = emitted;
                return;
            }

            int resumeAt = Math.Clamp(reader.BytePosition, 0, valid);
            int keep = valid - resumeAt;
            Buffer.BlockCopy(buffer, resumeAt, buffer, 0, keep);
            valid = keep;
        }
    }

    private static bool TryDecodeNextFrame(
        ref FlacBitReader reader,
        ParsedFlacInfo streamInfo,
        int searchLimit,
        int[][] samplePool,
        out FlacFrame frame,
        out long firstSample)
    {
        frame = default;
        firstSample = -1;

        reader.AlignToByte();

        // Search for 14-bit sync code (0xFF followed by 0xF8 or 0xF9) with valid header CRC-8 and footer CRC-16
        while (reader.BytePosition < searchLimit)
        {
            int frameStartByte = reader.BytePosition;
            if (reader.RemainingBytes < 6)
            {
                reader.ResetToByte(searchLimit);
                return false;
            }

            byte b0 = reader.PeekByte(0);
            byte b1 = reader.PeekByte(1);
            if (b0 != 0xFF || (b1 & 0xFC) != 0xF8)
            {
                reader.AdvanceBytes(1);
                continue;
            }

            if (TryParseFrameHeader(ref reader, frameStartByte, streamInfo, out int blockSize, out _, out int channelAssignment, out int bitsPerSample, out long codedFirstSample))
            {
                int channels = channelAssignment <= 7 ? channelAssignment + 1 : 2;
                int[][] channelSamples = new int[channels][];
                for (int c = 0; c < channels; c++)
                {
                    if (samplePool[c] == null || samplePool[c].Length < blockSize)
                    {
                        samplePool[c] = new int[Math.Max(blockSize, 4096)];
                    }
                    channelSamples[c] = samplePool[c];
                }

                bool subframeOk = true;
                for (int c = 0; c < channels; c++)
                {
                    // Side channel has +1 bit depth in stereo decorrelation modes
                    int subBps = bitsPerSample;
                    if ((channelAssignment == 8 && c == 1) ||
                        (channelAssignment == 9 && c == 0) ||
                        (channelAssignment == 10 && c == 1))
                    {
                        subBps++;
                    }

                    if (subBps > 32)
                    {
                        // A 33-bit side channel (32-bit source with stereo decorrelation) cannot be held in Int32.
                        throw new InvalidDataException(
                            "32-bit FLAC with stereo decorrelation is not supported. Re-encode the file at 24-bit or lower.");
                    }

                    if (!TryDecodeSubframe(ref reader, blockSize, subBps, channelSamples[c]))
                    {
                        subframeOk = false;
                        break;
                    }
                }

                // Byte-align and validate the 16-bit frame footer CRC-16 over the whole frame
                reader.AlignToByte();
                if (subframeOk && reader.RemainingBytes >= 2)
                {
                    int footerPos = reader.BytePosition;
                    ushort expectedCrc16 = (ushort)reader.ReadBits(16);
                    subframeOk = ComputeCrc16(reader.GetSpan(frameStartByte, footerPos - frameStartByte)) == expectedCrc16;
                }
                else
                {
                    subframeOk = false;
                }

                if (!subframeOk)
                {
                    reader.ResetToByte(frameStartByte + 1);
                    continue;
                }

                // Channel decorrelation
                if (channelAssignment == 8) // Left-Side: ch0 = left, ch1 = left - right
                {
                    int[] left = channelSamples[0];
                    int[] side = channelSamples[1];
                    for (int i = 0; i < blockSize; i++)
                    {
                        side[i] = left[i] - side[i];
                    }
                }
                else if (channelAssignment == 9) // Right-Side: ch0 = left - right, ch1 = right
                {
                    int[] side = channelSamples[0];
                    int[] right = channelSamples[1];
                    for (int i = 0; i < blockSize; i++)
                    {
                        side[i] = right[i] + side[i];
                    }
                }
                else if (channelAssignment == 10) // Mid-Side: ch0 = mid, ch1 = side
                {
                    int[] mid = channelSamples[0];
                    int[] side = channelSamples[1];
                    for (int i = 0; i < blockSize; i++)
                    {
                        int m = mid[i];
                        int s = side[i];
                        m = (m << 1) | (s & 1);
                        mid[i] = (m + s) >> 1;
                        side[i] = (m - s) >> 1;
                    }
                }

                frame = new FlacFrame(channelSamples, channels, bitsPerSample, blockSize);
                firstSample = codedFirstSample;
                return true;
            }

            reader.ResetToByte(frameStartByte + 1);
        }

        return false;
    }

    private static bool TryParseFrameHeader(
        ref FlacBitReader reader,
        int frameStartByte,
        ParsedFlacInfo streamInfo,
        out int blockSize,
        out int sampleRate,
        out int channelAssignment,
        out int bitsPerSample,
        out long firstSample)
    {
        blockSize = 0;
        sampleRate = streamInfo.SampleRate;
        channelAssignment = 0;
        bitsPerSample = streamInfo.BitsPerSample;
        firstSample = -1;

        uint syncAndStrategy = reader.ReadBits(16);
        if ((syncAndStrategy >> 2) != 0x3FFE || ((syncAndStrategy >> 1) & 1) != 0)
        {
            return false;
        }

        bool variableBlockSize = (syncAndStrategy & 1) != 0;

        int blockSizeCode = (int)reader.ReadBits(4);
        int sampleRateCode = (int)reader.ReadBits(4);
        channelAssignment = (int)reader.ReadBits(4);
        int sampleSizeCode = (int)reader.ReadBits(3);
        uint reservedBit = reader.ReadBits(1);

        if (blockSizeCode == 0 || sampleRateCode == 15 || channelAssignment > 10 || sampleSizeCode == 3 || reservedBit != 0)
        {
            return false;
        }

        // Read UTF-8 coded frame number (fixed block size) or first sample number (variable block size), 1..7 bytes
        if (!reader.TryReadUtf8Number(out ulong codedNumber))
        {
            return false;
        }

        // Decode block size
        blockSize = blockSizeCode switch
        {
            1 => 192,
            >= 2 and <= 5 => 576 << (blockSizeCode - 2),
            6 => (int)reader.ReadBits(8) + 1,
            7 => (int)reader.ReadBits(16) + 1,
            >= 8 and <= 15 => 256 << (blockSizeCode - 8),
            _ => 0
        };

        if (blockSize is <= 0 or > 65536)
        {
            return false;
        }

        // Decode sample rate
        sampleRate = sampleRateCode switch
        {
            0 => streamInfo.SampleRate,
            1 => 88200,
            2 => 176400,
            3 => 192000,
            4 => 8000,
            5 => 16000,
            6 => 22050,
            7 => 24000,
            8 => 32000,
            9 => 44100,
            10 => 48000,
            11 => 96000,
            12 => (int)reader.ReadBits(8) * 1000,
            13 => (int)reader.ReadBits(16),
            14 => (int)reader.ReadBits(16) * 10,
            _ => 0
        };

        if (sampleRate <= 0)
        {
            return false;
        }

        bitsPerSample = sampleSizeCode switch
        {
            0 => streamInfo.BitsPerSample,
            1 => 8,
            2 => 12,
            4 => 16,
            5 => 20,
            6 => 24,
            7 => 32,
            _ => 0
        };

        if (bitsPerSample is < 4 or > 32)
        {
            return false;
        }

        int headerEndByte = reader.BytePosition;
        byte expectedCrc8 = (byte)reader.ReadBits(8);
        byte computedCrc8 = ComputeCrc8(reader.GetSpan(frameStartByte, headerEndByte - frameStartByte));
        if (expectedCrc8 != computedCrc8)
        {
            return false;
        }

        long fixedBlock = streamInfo.MaxBlockSize > 0 ? streamInfo.MaxBlockSize : blockSize;
        firstSample = variableBlockSize
            ? (long)Math.Min(codedNumber, long.MaxValue)
            : (codedNumber <= (ulong)(long.MaxValue / fixedBlock) ? (long)codedNumber * fixedBlock : -1);
        return true;
    }

    private static bool TryDecodeSubframe(ref FlacBitReader reader, int blockSize, int subBps, int[] dest)
    {
        if (reader.ReadBits(1) != 0) // Zero-bit padding
        {
            return false;
        }

        int typeCode = (int)reader.ReadBits(6);
        int wastedBits = 0;
        if (reader.ReadBits(1) == 1)
        {
            if (!reader.TryReadUnaryZeroCount(out int unary))
            {
                return false;
            }
            wastedBits = unary + 1;
        }

        int bps = subBps - wastedBits;
        if (bps is <= 0 or > 33)
        {
            return false;
        }

        bool ok;
        if (typeCode == 0) // SUBFRAME_CONSTANT
        {
            int val = (int)reader.ReadSignedBits(bps);
            Array.Fill(dest, val, 0, blockSize);
            ok = true;
        }
        else if (typeCode == 1) // SUBFRAME_VERBATIM
        {
            for (int i = 0; i < blockSize; i++)
            {
                dest[i] = (int)reader.ReadSignedBits(bps);
            }
            ok = true;
        }
        else if (typeCode is >= 8 and <= 12) // SUBFRAME_FIXED (order 0..4)
        {
            int order = typeCode & 0x07;
            ok = TryDecodeFixedSubframe(ref reader, blockSize, bps, order, dest);
        }
        else if (typeCode >= 32) // SUBFRAME_LPC (order 1..32)
        {
            int order = (typeCode & 0x1F) + 1;
            ok = TryDecodeLpcSubframe(ref reader, blockSize, bps, order, dest);
        }
        else
        {
            return false;
        }

        if (!ok)
        {
            return false;
        }

        if (wastedBits > 0)
        {
            for (int i = 0; i < blockSize; i++)
            {
                dest[i] <<= wastedBits;
            }
        }

        return true;
    }

    private static bool TryDecodeFixedSubframe(ref FlacBitReader reader, int blockSize, int bps, int order, int[] dest)
    {
        if (order > blockSize) return false;

        for (int i = 0; i < order; i++)
        {
            dest[i] = (int)reader.ReadSignedBits(bps);
        }

        if (!TryDecodeResiduals(ref reader, blockSize, order, dest))
        {
            return false;
        }

        switch (order)
        {
            case 0:
                break;
            case 1:
                for (int i = 1; i < blockSize; i++)
                {
                    dest[i] += dest[i - 1];
                }
                break;
            case 2:
                for (int i = 2; i < blockSize; i++)
                {
                    dest[i] += (2 * dest[i - 1]) - dest[i - 2];
                }
                break;
            case 3:
                for (int i = 3; i < blockSize; i++)
                {
                    dest[i] += (3 * dest[i - 1]) - (3 * dest[i - 2]) + dest[i - 3];
                }
                break;
            case 4:
                for (int i = 4; i < blockSize; i++)
                {
                    dest[i] += (4 * dest[i - 1]) - (6 * dest[i - 2]) + (4 * dest[i - 3]) - dest[i - 4];
                }
                break;
            default:
                return false;
        }

        return true;
    }

    private static bool TryDecodeLpcSubframe(ref FlacBitReader reader, int blockSize, int bps, int order, int[] dest)
    {
        if (order > blockSize) return false;

        for (int i = 0; i < order; i++)
        {
            dest[i] = (int)reader.ReadSignedBits(bps);
        }

        int coeffPrecision = (int)reader.ReadBits(4) + 1;
        if (coeffPrecision == 16) // 0b1111 + 1 is invalid per spec
        {
            return false;
        }

        int qlpShift = (int)reader.ReadSignedBits(5);
        if (qlpShift < 0)
        {
            // Negative shifts are invalid in compliant streams; clamp to 0
            qlpShift = 0;
        }

        Span<int> coeffs = stackalloc int[order];
        for (int i = 0; i < order; i++)
        {
            coeffs[i] = (int)reader.ReadSignedBits(coeffPrecision);
        }

        if (!TryDecodeResiduals(ref reader, blockSize, order, dest))
        {
            return false;
        }

        for (int i = order; i < blockSize; i++)
        {
            long sum = 0;
            for (int j = 0; j < order; j++)
            {
                sum += (long)coeffs[j] * dest[i - j - 1];
            }
            dest[i] += (int)(sum >> qlpShift);
        }

        return true;
    }

    private static bool TryDecodeResiduals(ref FlacBitReader reader, int blockSize, int predictorOrder, int[] dest)
    {
        int codingMethod = (int)reader.ReadBits(2);
        if (codingMethod > 1)
        {
            return false;
        }

        int paramBits = codingMethod == 0 ? 4 : 5;
        int escapeParam = codingMethod == 0 ? 15 : 31;

        int partitionOrder = (int)reader.ReadBits(4);
        int numPartitions = 1 << partitionOrder;
        int partitionSamples = partitionOrder == 0 ? blockSize : (blockSize >> partitionOrder);

        if (partitionSamples < predictorOrder || (blockSize % numPartitions) != 0)
        {
            return false;
        }

        int sampleIndex = predictorOrder;
        for (int p = 0; p < numPartitions; p++)
        {
            int count = p == 0 ? (partitionSamples - predictorOrder) : partitionSamples;
            int riceParam = (int)reader.ReadBits(paramBits);

            if (riceParam == escapeParam)
            {
                int rawBits = (int)reader.ReadBits(5);
                if (rawBits == 0)
                {
                    Array.Clear(dest, sampleIndex, count);
                    sampleIndex += count;
                }
                else
                {
                    for (int i = 0; i < count; i++)
                    {
                        dest[sampleIndex++] = (int)reader.ReadSignedBits(rawBits);
                    }
                }
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    if (!reader.TryReadUnaryZeroCount(out int q))
                    {
                        return false;
                    }
                    uint r = riceParam > 0 ? reader.ReadBits(riceParam) : 0u;
                    uint u = ((uint)q << riceParam) | r;
                    int val = (int)(u >> 1) ^ -(int)(u & 1);
                    dest[sampleIndex++] = val;
                }
            }
        }

        return sampleIndex == blockSize;
    }

    private static byte ComputeCrc8(ReadOnlySpan<byte> data)
    {
        byte crc = 0;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 0x80) != 0 ? (byte)((crc << 1) ^ 0x07) : (byte)(crc << 1);
            }
        }
        return crc;
    }

    // Table-driven CRC-16 (poly 0x8005): it runs over every byte of every frame, so a bitwise loop would dominate decode time.
    private static readonly ushort[] Crc16Table = BuildCrc16Table();

    private static ushort[] BuildCrc16Table()
    {
        var table = new ushort[256];
        for (int n = 0; n < 256; n++)
        {
            ushort crc = (ushort)(n << 8);
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x8005) : (ushort)(crc << 1);
            }
            table[n] = crc;
        }
        return table;
    }

    private static ushort ComputeCrc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        ushort[] table = Crc16Table;
        foreach (byte b in data)
        {
            crc = (ushort)((crc << 8) ^ table[(crc >> 8) ^ b]);
        }
        return crc;
    }

    private static void WriteUtf8Number(Stream stream, ulong value)
    {
        if (value < 0x80UL)
        {
            stream.WriteByte((byte)value);
        }
        else if (value < 0x800UL)
        {
            stream.WriteByte((byte)(0xC0UL | (value >> 6)));
            stream.WriteByte((byte)(0x80UL | (value & 0x3FUL)));
        }
        else if (value < 0x10000UL)
        {
            stream.WriteByte((byte)(0xE0UL | (value >> 12)));
            stream.WriteByte((byte)(0x80UL | ((value >> 6) & 0x3FUL)));
            stream.WriteByte((byte)(0x80UL | (value & 0x3FUL)));
        }
        else if (value < 0x200000UL)
        {
            stream.WriteByte((byte)(0xF0UL | (value >> 18)));
            stream.WriteByte((byte)(0x80UL | ((value >> 12) & 0x3FUL)));
            stream.WriteByte((byte)(0x80UL | ((value >> 6) & 0x3FUL)));
            stream.WriteByte((byte)(0x80UL | (value & 0x3FUL)));
        }
        else
        {
            stream.WriteByte((byte)(0xF8UL | (value >> 24)));
            stream.WriteByte((byte)(0x80UL | ((value >> 18) & 0x3FUL)));
            stream.WriteByte((byte)(0x80UL | ((value >> 12) & 0x3FUL)));
            stream.WriteByte((byte)(0x80UL | ((value >> 6) & 0x3FUL)));
            stream.WriteByte((byte)(0x80UL | (value & 0x3FUL)));
        }
    }

    private static byte[] ReadExactBytes(Stream stream, int count)
    {
        byte[] buf = new byte[count];
        int total = 0;
        while (total < count)
        {
            int r = stream.Read(buf, total, count - total);
            if (r <= 0)
            {
                throw new EndOfStreamException($"Unexpected EOF while reading {count} bytes from FLAC stream.");
            }
            total += r;
        }
        return buf;
    }

    private static int ReadFully(Stream stream, Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int r = stream.Read(destination[total..]);
            if (r <= 0) break;
            total += r;
        }
        return total;
    }

    private static void SkipBytes(Stream stream, int count)
    {
        if (count <= 0) return;
        if (stream.CanSeek)
        {
            stream.Position = Math.Min(stream.Length, stream.Position + count);
            return;
        }

        byte[] scratch = new byte[Math.Min(count, 8192)];
        int remaining = count;
        while (remaining > 0)
        {
            int r = stream.Read(scratch, 0, Math.Min(remaining, scratch.Length));
            if (r <= 0) break;
            remaining -= r;
        }
    }

    private ref struct FlacBitReader
    {
        private readonly ReadOnlySpan<byte> _buffer;
        private int _bytePos;
        private int _bitOffset; // 0..7 within _buffer[_bytePos]

        public FlacBitReader(ReadOnlySpan<byte> buffer)
        {
            _buffer = buffer;
            _bytePos = 0;
            _bitOffset = 0;
        }

        public bool IsAtEnd => _bytePos >= _buffer.Length;
        public int BytePosition => _bytePos;
        public int RemainingBytes => _buffer.Length - _bytePos;

        public byte PeekByte(int offset) => _buffer[_bytePos + offset];

        public ReadOnlySpan<byte> GetSpan(int start, int length) => _buffer.Slice(start, length);

        public void AdvanceBytes(int count)
        {
            _bytePos = Math.Min(_buffer.Length, _bytePos + count);
            _bitOffset = 0;
        }

        public void ResetToByte(int bytePos)
        {
            _bytePos = Math.Clamp(bytePos, 0, _buffer.Length);
            _bitOffset = 0;
        }

        public void AlignToByte()
        {
            if (_bitOffset != 0)
            {
                _bitOffset = 0;
                _bytePos++;
            }
        }

        public uint ReadBits(int count)
        {
            return (uint)ReadBitsUInt64(count);
        }

        public ulong ReadBitsUInt64(int count)
        {
            if (count == 0) return 0;

            ulong result = 0;
            int bitsLeft = count;

            while (bitsLeft > 0)
            {
                if (_bytePos >= _buffer.Length)
                {
                    return result << bitsLeft;
                }

                int availInByte = 8 - _bitOffset;
                int take = Math.Min(bitsLeft, availInByte);
                int shift = availInByte - take;
                uint mask = (1u << take) - 1u;
                uint bits = ((uint)_buffer[_bytePos] >> shift) & mask;

                result = (result << take) | bits;
                bitsLeft -= take;
                _bitOffset += take;

                if (_bitOffset == 8)
                {
                    _bitOffset = 0;
                    _bytePos++;
                }
            }

            return result;
        }

        public long ReadSignedBits(int count)
        {
            if (count <= 0) return 0;
            ulong u = ReadBitsUInt64(count);
            if (count < 64)
            {
                ulong signBit = 1UL << (count - 1);
                if ((u & signBit) != 0)
                {
                    u |= ~((1UL << count) - 1UL);
                }
            }
            return unchecked((long)u);
        }

        public bool TryReadUnaryZeroCount(out int count)
        {
            count = 0;
            while (_bytePos < _buffer.Length)
            {
                byte current = _buffer[_bytePos];
                if (_bitOffset == 0 && current == 0)
                {
                    count += 8;
                    _bytePos++;
                    if (count > 1_000_000) return false;
                    continue;
                }

                int mask = 1 << (7 - _bitOffset);
                _bitOffset++;
                if (_bitOffset == 8)
                {
                    _bitOffset = 0;
                    _bytePos++;
                }

                if ((current & mask) != 0)
                {
                    return true;
                }

                count++;
                if (count > 1_000_000) return false;
            }

            return false;
        }

        public bool TryReadUtf8Number(out ulong value)
        {
            value = 0;
            if (RemainingBytes < 1) return false;
            byte first = (byte)ReadBits(8);
            if ((first & 0x80) == 0)
            {
                value = first;
                return true;
            }

            int extraBytes = 0;
            byte mask = 0x40;
            while (mask != 0 && (first & mask) != 0)
            {
                extraBytes++;
                mask >>= 1;
            }

            if (extraBytes is < 1 or > 6 || RemainingBytes < extraBytes)
            {
                return false;
            }

            value = (ulong)(first & (0x3F >> extraBytes));
            for (int i = 0; i < extraBytes; i++)
            {
                byte cont = (byte)ReadBits(8);
                if ((cont & 0xC0) != 0x80)
                {
                    return false;
                }
                value = (value << 6) | (uint)(cont & 0x3F);
            }

            return true;
        }
    }
}
