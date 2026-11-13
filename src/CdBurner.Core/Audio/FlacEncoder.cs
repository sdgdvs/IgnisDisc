using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace CdBurner.Core.Audio;

/// <summary>
/// Audio track metadata tags used for FLAC stream encoding and container tagging.
/// </summary>
public sealed record AudioTrackTags(
    string Title,
    string Artist,
    string Album,
    int TrackNumber,
    int TrackTotal,
    string? Isrc = null,
    string? Date = null,
    string? ReplayGainTrackGain = null,
    string? ReplayGainTrackPeak = null,
    string? ReplayGainAlbumGain = null,
    string? ReplayGainAlbumPeak = null,
    byte[]? CoverArtBytes = null,
    string? CoverArtMimeType = null);

/// <summary>
/// Encodes uncompressed RIFF WAVE audio files to lossless FLAC using external FFmpeg,
/// and provides pure-managed FLAC metadata block embedding (cover art & ReplayGain).
/// </summary>
public static class FlacEncoder
{
    private static readonly byte[] FlacMagic = "fLaC"u8.ToArray();
    private static readonly byte[] Id3Magic = "ID3"u8.ToArray();

    /// <summary>FLAC metadata block lengths are 24-bit.</summary>
    public const int MaxMetadataBlockLength = 0xFF_FFFF;

    /// <summary>
    /// Largest cover image that fits in a FLAC PICTURE block: the block's 24-bit length also covers 32 bytes of fields
    /// and the MIME type (at most "image/webp"), so 64 bytes are kept back.
    /// </summary>
    public const int MaxCoverArtBytes = MaxMetadataBlockLength - 64;

    /// <summary>Why a cover image cannot be embedded, or null when it can. Checked before a rip starts.</summary>
    public static string? CoverArtSizeProblem(int imageBytes) =>
        imageBytes > MaxCoverArtBytes
            ? $"The cover image is {imageBytes / (1024.0 * 1024.0):0.0} MB; the largest that fits in a FLAC file is " +
              $"{MaxCoverArtBytes / (1024.0 * 1024.0):0.0} MB. Choose a smaller image."
            : null;

    /// <summary>Identifies JPEG, PNG, GIF and WebP from their signatures, or null.</summary>
    public static string? DetectImageMimeType(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return "image/jpeg";
        if (data.Length >= 8 && data[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return "image/png";
        if (data.Length >= 6 && (data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8))) return "image/gif";
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }

    /// <summary>
    /// Builds the argument list for FFmpeg FLAC encoding in the required order.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(string wavPath, string flacPath, AudioTrackTags tags)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wavPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(flacPath);
        ArgumentNullException.ThrowIfNull(tags);

        var args = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-y",
            "-i",
            wavPath,
            "-map_metadata",
            "-1",
            "-c:a",
            "flac",
            "-compression_level",
            "8",
            "-metadata",
            $"TITLE={tags.Title}",
            "-metadata",
            $"ARTIST={tags.Artist}",
            "-metadata",
            $"ALBUM={tags.Album}",
            "-metadata",
            $"TRACKNUMBER={tags.TrackNumber}",
            "-metadata",
            $"TRACKTOTAL={tags.TrackTotal}"
        };

        if (!string.IsNullOrWhiteSpace(tags.Isrc))
        {
            args.Add("-metadata");
            args.Add($"ISRC={tags.Isrc}");
        }

        if (!string.IsNullOrWhiteSpace(tags.Date))
        {
            args.Add("-metadata");
            args.Add($"DATE={tags.Date}");
        }

        if (!string.IsNullOrWhiteSpace(tags.ReplayGainTrackGain))
        {
            args.Add("-metadata");
            args.Add($"REPLAYGAIN_TRACK_GAIN={tags.ReplayGainTrackGain}");
        }

        if (!string.IsNullOrWhiteSpace(tags.ReplayGainTrackPeak))
        {
            args.Add("-metadata");
            args.Add($"REPLAYGAIN_TRACK_PEAK={tags.ReplayGainTrackPeak}");
        }

        if (!string.IsNullOrWhiteSpace(tags.ReplayGainAlbumGain))
        {
            args.Add("-metadata");
            args.Add($"REPLAYGAIN_ALBUM_GAIN={tags.ReplayGainAlbumGain}");
        }

        if (!string.IsNullOrWhiteSpace(tags.ReplayGainAlbumPeak))
        {
            args.Add("-metadata");
            args.Add($"REPLAYGAIN_ALBUM_PEAK={tags.ReplayGainAlbumPeak}");
        }

        args.Add(flacPath);
        return args;
    }

    /// <summary>
    /// Executes FFmpeg to encode a WAV file into a FLAC file with embedded tags.
    /// </summary>
    public static async Task EncodeAsync(
        string ffmpegPath,
        string wavPath,
        string flacPath,
        AudioTrackTags tags,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(wavPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(flacPath);
        ArgumentNullException.ThrowIfNull(tags);

        if (!File.Exists(wavPath))
        {
            throw new FileNotFoundException($"Source WAV file not found: {wavPath}", wavPath);
        }

        var args = BuildArguments(wavPath, flacPath, tags);

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = new Process { StartInfo = psi };

        if (!proc.Start())
        {
            throw new InvalidOperationException($"Failed to start FFmpeg process at '{ffmpegPath}'.");
        }

        var outTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
        var errTask = proc.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await Task.WhenAll(outTask, errTask, proc.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
            throw;
        }
        catch
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
            throw;
        }

        if (proc.ExitCode != 0)
        {
            string stderr = errTask.IsCompletedSuccessfully ? errTask.Result : string.Empty;
            string lastStderr = stderr.Length > 500 ? stderr[^500..] : stderr;
            throw new InvalidOperationException(
                $"FFmpeg FLAC encoding failed with exit code {proc.ExitCode}: {lastStderr}");
        }
    }

    /// <summary>
    /// Injects or updates FLAC metadata blocks (VORBIS_COMMENT and/or METADATA_BLOCK_PICTURE)
    /// into a FLAC stream without requiring FFmpeg, returning the resulting FLAC byte array.
    /// </summary>
    public static byte[] EmbedMetadataBlocks(Stream flacStream, AudioTrackTags tags)
    {
        ArgumentNullException.ThrowIfNull(flacStream);
        ArgumentNullException.ThrowIfNull(tags);

        // 1. Skip ID3v2 if prepended
        SkipId3v2IfPresent(flacStream);

        Span<byte> magic = stackalloc byte[4];
        if (ReadExact(flacStream, magic) < 4 || !magic.SequenceEqual(FlacMagic))
        {
            throw new InvalidDataException("Invalid FLAC stream: missing 'fLaC' signature.");
        }

        // 2. Read existing metadata blocks
        var blocks = new List<(int BlockType, byte[] Data)>();
        Span<byte> header = stackalloc byte[4];

        while (true)
        {
            if (ReadExact(flacStream, header) < 4)
            {
                throw new InvalidDataException("Unexpected end of FLAC stream while reading metadata headers.");
            }

            bool isLast = (header[0] & 0x80) != 0;
            int blockType = header[0] & 0x7F;
            int length = (header[1] << 16) | (header[2] << 8) | header[3];

            byte[] body = new byte[length];
            if (length > 0 && ReadExact(flacStream, body) < length)
            {
                throw new InvalidDataException("Unexpected end of FLAC stream while reading metadata block body.");
            }

            blocks.Add((blockType, body));
            if (isLast) break;
        }

        // 3. Update VORBIS_COMMENT block (type 4)
        int vcIndex = blocks.FindIndex(b => b.BlockType == 4);
        string vendor = "IgnisDisc";
        var existingComments = new List<string>();

        if (vcIndex >= 0)
        {
            (vendor, existingComments) = ParseVorbisComments(blocks[vcIndex].Data);
        }

        var commentMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in existingComments)
        {
            int eq = c.IndexOf('=');
            if (eq > 0)
            {
                commentMap[c[..eq].Trim()] = c[(eq + 1)..];
            }
        }

        if (!string.IsNullOrWhiteSpace(tags.Title)) commentMap["TITLE"] = tags.Title;
        if (!string.IsNullOrWhiteSpace(tags.Artist)) commentMap["ARTIST"] = tags.Artist;
        if (!string.IsNullOrWhiteSpace(tags.Album)) commentMap["ALBUM"] = tags.Album;
        if (tags.TrackNumber > 0) commentMap["TRACKNUMBER"] = tags.TrackNumber.ToString(CultureInfo.InvariantCulture);
        if (tags.TrackTotal > 0) commentMap["TRACKTOTAL"] = tags.TrackTotal.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(tags.Isrc)) commentMap["ISRC"] = tags.Isrc;
        if (!string.IsNullOrWhiteSpace(tags.Date)) commentMap["DATE"] = tags.Date;
        if (!string.IsNullOrWhiteSpace(tags.ReplayGainTrackGain)) commentMap["REPLAYGAIN_TRACK_GAIN"] = tags.ReplayGainTrackGain;
        if (!string.IsNullOrWhiteSpace(tags.ReplayGainTrackPeak)) commentMap["REPLAYGAIN_TRACK_PEAK"] = tags.ReplayGainTrackPeak;
        if (!string.IsNullOrWhiteSpace(tags.ReplayGainAlbumGain)) commentMap["REPLAYGAIN_ALBUM_GAIN"] = tags.ReplayGainAlbumGain;
        if (!string.IsNullOrWhiteSpace(tags.ReplayGainAlbumPeak)) commentMap["REPLAYGAIN_ALBUM_PEAK"] = tags.ReplayGainAlbumPeak;

        byte[] newVcBody = SerializeVorbisComments(vendor, commentMap.Select(kvp => $"{kvp.Key}={kvp.Value}"));
        if (vcIndex >= 0)
        {
            blocks[vcIndex] = (4, newVcBody);
        }
        else
        {
            // Insert VORBIS_COMMENT right after STREAMINFO (block 0)
            int insertPos = blocks.Count > 0 ? 1 : 0;
            blocks.Insert(insertPos, (4, newVcBody));
        }

        // 4. Update or inject METADATA_BLOCK_PICTURE (type 6) if cover art is present
        if (tags.CoverArtBytes is { Length: > 0 })
        {
            byte[] picBody = CreatePictureBlockBody(tags.CoverArtBytes, tags.CoverArtMimeType);
            int picIndex = blocks.FindIndex(b => b.BlockType == 6);
            if (picIndex >= 0)
            {
                blocks[picIndex] = (6, picBody);
            }
            else
            {
                blocks.Add((6, picBody));
            }
        }

        // 5. Build output FLAC stream with rewritten metadata blocks + untouched audio frames
        using var outMs = new MemoryStream();
        outMs.Write(FlacMagic);

        Span<byte> blockHdr = stackalloc byte[4];
        for (int i = 0; i < blocks.Count; i++)
        {
            bool isLast = (i == blocks.Count - 1);
            int bType = blocks[i].BlockType;
            byte[] body = blocks[i].Data;
            if (body.Length > MaxMetadataBlockLength)
            {
                throw new InvalidDataException(
                    $"FLAC metadata block type {bType} is {body.Length:N0} bytes; FLAC allows at most {MaxMetadataBlockLength:N0} (use a smaller cover image).");
            }

            blockHdr[0] = (byte)((isLast ? 0x80 : 0x00) | (bType & 0x7F));
            blockHdr[1] = (byte)((body.Length >> 16) & 0xFF);
            blockHdr[2] = (byte)((body.Length >> 8) & 0xFF);
            blockHdr[3] = (byte)(body.Length & 0xFF);

            outMs.Write(blockHdr);
            outMs.Write(body);
        }

        // Copy audio frames from remaining stream
        flacStream.CopyTo(outMs);
        return outMs.ToArray();
    }

    /// <summary>
    /// Injects or updates FLAC metadata blocks into <paramref name="destination"/> stream.
    /// </summary>
    public static void EmbedMetadataBlocks(Stream flacStream, Stream destination, AudioTrackTags tags)
    {
        ArgumentNullException.ThrowIfNull(destination);
        byte[] bytes = EmbedMetadataBlocks(flacStream, tags);
        destination.Write(bytes, 0, bytes.Length);
    }

    /// <summary>
    /// Embeds or updates FLAC metadata blocks (VORBIS_COMMENT and/or METADATA_BLOCK_PICTURE)
    /// in the specified FLAC file on disk without requiring FFmpeg.
    /// </summary>
    public static void EmbedCoverArtAndReplayGain(string flacFilePath, AudioTrackTags tags)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flacFilePath);
        ArgumentNullException.ThrowIfNull(tags);

        if (!File.Exists(flacFilePath))
        {
            throw new FileNotFoundException($"FLAC file not found: '{flacFilePath}'.", flacFilePath);
        }

        byte[] updated;
        using (var fs = new FileStream(flacFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            updated = EmbedMetadataBlocks(fs, tags);
        }

        // Write a sibling temp file and swap it in, so a crash or full disk never leaves a half-written FLAC.
        string tempPath = flacFilePath + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            File.WriteAllBytes(tempPath, updated);
            File.Move(tempPath, flacFilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static byte[] CreatePictureBlockBody(byte[] pictureData, string? mimeType)
    {
        string mime = DetectImageMimeType(pictureData) ?? (!string.IsNullOrWhiteSpace(mimeType) ? mimeType : "image/jpeg");
        byte[] mimeBytes = Encoding.ASCII.GetBytes(mime);

        using var ms = new MemoryStream(32 + mimeBytes.Length + pictureData.Length);
        Span<byte> u32 = stackalloc byte[4];

        // 1. Picture type: 3 = Cover (front)
        BinaryPrimitives.WriteUInt32BigEndian(u32, 3);
        ms.Write(u32);

        // 2. MIME type length + string
        BinaryPrimitives.WriteUInt32BigEndian(u32, (uint)mimeBytes.Length);
        ms.Write(u32);
        ms.Write(mimeBytes);

        // 3. Description length (0 = empty description)
        BinaryPrimitives.WriteUInt32BigEndian(u32, 0);
        ms.Write(u32);

        // 4. Width (4), Height (4), Color Depth (4), Colors (4) = 16 bytes of 0
        Span<byte> zeros16 = stackalloc byte[16];
        ms.Write(zeros16);

        // 5. Picture data length + data
        BinaryPrimitives.WriteUInt32BigEndian(u32, (uint)pictureData.Length);
        ms.Write(u32);
        ms.Write(pictureData);

        return ms.ToArray();
    }

    private static (string Vendor, List<string> Comments) ParseVorbisComments(byte[] data)
    {
        if (data.Length < 8)
        {
            return ("IgnisDisc", new List<string>());
        }

        int pos = 0;
        uint vendorLen = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4));
        pos += 4;
        string vendor = "IgnisDisc";
        if (pos + vendorLen <= data.Length)
        {
            vendor = Encoding.UTF8.GetString(data, pos, (int)vendorLen);
            pos += (int)vendorLen;
        }

        var comments = new List<string>();
        if (pos + 4 <= data.Length)
        {
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4));
            pos += 4;
            for (uint i = 0; i < count && pos + 4 <= data.Length; i++)
            {
                uint cLen = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4));
                pos += 4;
                if (pos + cLen <= data.Length)
                {
                    string comment = Encoding.UTF8.GetString(data, pos, (int)cLen);
                    comments.Add(comment);
                    pos += (int)cLen;
                }
            }
        }

        return (vendor, comments);
    }

    private static byte[] SerializeVorbisComments(string vendor, IEnumerable<string> comments)
    {
        using var ms = new MemoryStream();
        Span<byte> u32 = stackalloc byte[4];

        byte[] vendorBytes = Encoding.UTF8.GetBytes(vendor);
        BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)vendorBytes.Length);
        ms.Write(u32);
        ms.Write(vendorBytes);

        var commentList = comments.ToList();
        BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)commentList.Count);
        ms.Write(u32);

        foreach (var c in commentList)
        {
            byte[] cBytes = Encoding.UTF8.GetBytes(c);
            BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)cBytes.Length);
            ms.Write(u32);
            ms.Write(cBytes);
        }

        return ms.ToArray();
    }

    private static int ReadExact(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int r = stream.Read(buffer[total..]);
            if (r <= 0) break;
            total += r;
        }
        return total;
    }

    private static void SkipId3v2IfPresent(Stream stream)
    {
        if (!stream.CanSeek) return;
        long startPos = stream.Position;

        Span<byte> id3Header = stackalloc byte[10];
        if (ReadExact(stream, id3Header) == 10 && id3Header[..3].SequenceEqual(Id3Magic))
        {
            int size = ((id3Header[6] & 0x7F) << 21) |
                       ((id3Header[7] & 0x7F) << 14) |
                       ((id3Header[8] & 0x7F) << 7) |
                       (id3Header[9] & 0x7F);

            byte version = id3Header[3];
            byte flags = id3Header[5];
            if (version >= 4 && (flags & 0x10) != 0) size += 10;

            stream.Position = startPos + 10 + size;
        }
        else
        {
            stream.Position = startPos;
        }
    }
}
