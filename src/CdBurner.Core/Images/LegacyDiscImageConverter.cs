using System.Buffers.Binary;
using System.Text;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Images;

/// <summary>
/// Metadata inspected from a legacy or third-party optical disc image container
/// (.nrg Nero, .mdf/.mds Alcohol 120%, .ccd/.img CloneCD, .cdi DiscJuggler).
/// </summary>
public sealed record LegacyDiscImageInfo(
    string SourceFilePath,
    string BinaryPayloadPath,
    string FormatName,
    long PayloadOffsetBytes,
    long PayloadLengthBytes,
    int SectorSizeBytes,
    int SectorCount,
    bool IsRaw2352,
    bool IsAudioDisc,
    int TrackCount
);

/// <summary>
/// Inspects and converts legacy proprietary optical disc image formats
/// (Nero .nrg, Alcohol 120% .mds/.mdf, CloneCD .ccd/.img) into canonical ISO 9660 (.iso)
/// or CDRWIN (.bin + .cue) streams for burning and verification.
/// </summary>
public static class LegacyDiscImageConverter
{
    private const int SectorSize2336 = 2336;
    private const int SectorSize2448 = 2448;
    private const int StandardPregapSectors = 150;
    private static readonly byte[] IsoCd001Magic = "CD001"u8.ToArray();
    private static readonly byte[] MdsSignature = "MEDIA DESCRIPTOR"u8.ToArray();

    /// <summary>
    /// Returns true if the file path has a supported legacy disc image extension (.nrg, .mdf, .mds, .ccd, .img).
    /// </summary>
    public static bool IsLegacyImageFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext is ".nrg" or ".mdf" or ".mds" or ".ccd" or ".img";
    }

    /// <summary>
    /// Inspects a legacy disc image (.nrg, .mdf, .mds, .ccd, .img) and returns its geometry and sector layout.
    /// </summary>
    public static LegacyDiscImageInfo Inspect(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Disc image file not found: '{filePath}'.", filePath);
        }

        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".nrg" => InspectNrg(filePath),
            ".ccd" => InspectCloneCdCcd(filePath),
            ".img" => InspectCloneCdImg(filePath),
            ".mds" => InspectAlcoholMds(filePath),
            ".mdf" => InspectAlcoholMdf(filePath),
            _ => throw new NotSupportedException($"Unsupported legacy image extension '{ext}'.")
        };
    }

    /// <summary>
    /// Converts any supported legacy disc image (.nrg, .mdf/.mds, .ccd/.img, or raw 2352-byte Mode 1 .bin)
    /// into a standard 2,048-byte Mode 1 ISO 9660 file (.iso).
    /// </summary>
    public static async Task<int> ConvertToIsoAsync(
        string sourceImagePath,
        string outputIsoPath,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceImagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputIsoPath);

        var info = Inspect(sourceImagePath);
        if (info.IsAudioDisc)
        {
            throw new InvalidOperationException(
                $"Cannot convert Red Book Audio image '{Path.GetFileName(sourceImagePath)}' to a 2048-byte data ISO. Use BIN/CUE or burn directly.");
        }

        string? outDir = Path.GetDirectoryName(outputIsoPath);
        if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        await using var outStream = new FileStream(outputIsoPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
        return await ExtractMode1IsoStreamAsync(info, outStream, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a read-only, seekable view of the image's 2,048-byte user data (exactly what an .iso of the disc would contain),
    /// transforming raw sectors on the fly. Nothing is buffered beyond one sector, so 4.7 GB / 8.5 GB DVD images burn and
    /// verify without hitting the 2 GB <see cref="MemoryStream"/> limit or needing an intermediate temp file.
    /// </summary>
    public static Stream OpenIsoUserDataStream(LegacyDiscImageInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (info.IsAudioDisc)
        {
            throw new InvalidOperationException(
                $"'{Path.GetFileName(info.SourceFilePath)}' is a Red Book Audio image; it has no 2048-byte data sectors to burn as an ISO.");
        }

        return new LegacyIsoUserDataStream(info);
    }

    /// <summary>
    /// Inspects <paramref name="filePath"/> and opens its 2,048-byte user-data view.
    /// </summary>
    public static Stream OpenIsoUserDataStream(string filePath) => OpenIsoUserDataStream(Inspect(filePath));

    /// <summary>
    /// Returns where the 2,048-byte user payload starts inside one stored sector:
    /// 2048 = 0; 2336 (Mode 2 formless, header stripped) = 8 (after the subheader);
    /// 2352 / 2448 (raw, optionally + 96-byte subchannel) = 16 for Mode 1, 24 for Mode 2 / CD-ROM XA Form 1
    /// (PlayStation, VCD, Photo CD), where an 8-byte subheader follows the 4-byte header.
    /// </summary>
    internal static int GetUserDataOffset(ReadOnlySpan<byte> sector, int sectorSize)
    {
        if (sectorSize == OpticalConstants.DataSectorSize) return 0;
        if (sectorSize == SectorSize2336) return 8;
        return sector.Length > 15 && sector[15] == 2 ? 24 : 16;
    }

    /// <summary>
    /// Extracts 2,048-byte user data sectors from a legacy disc image into a writable destination stream.
    /// Raw sectors have their sync/header (plus the Mode 2 subheader) and EDC/ECC trailer stripped.
    /// </summary>
    public static async Task<int> ExtractMode1IsoStreamAsync(
        LegacyDiscImageInfo info,
        Stream destinationIsoStream,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(destinationIsoStream);

        await using var src = new FileStream(info.BinaryPayloadPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        src.Position = Math.Clamp(info.PayloadOffsetBytes, 0, src.Length);

        int totalSectors = info.SectorCount;
        int srcSectorSize = info.SectorSizeBytes;
        byte[] sectorBuf = new byte[srcSectorSize];
        int writtenSectors = 0;

        while (writtenSectors < totalSectors)
        {
            ct.ThrowIfCancellationRequested();

            int readTotal = 0;
            while (readTotal < srcSectorSize)
            {
                int r = await src.ReadAsync(sectorBuf.AsMemory(readTotal, srcSectorSize - readTotal), ct).ConfigureAwait(false);
                if (r == 0) break;
                readTotal += r;
            }

            if (readTotal == 0)
            {
                break;
            }

            if (readTotal < srcSectorSize)
            {
                Array.Clear(sectorBuf, readTotal, srcSectorSize - readTotal);
            }

            if (srcSectorSize == OpticalConstants.DataSectorSize)
            {
                int writeLen = Math.Min(OpticalConstants.DataSectorSize, readTotal);
                await destinationIsoStream.WriteAsync(sectorBuf.AsMemory(0, writeLen), ct).ConfigureAwait(false);
            }
            else
            {
                // Raw sector: strip sync/header (+ Mode 2 subheader) and EDC/ECC, keeping the 2048-byte user payload.
                int userOffset = GetUserDataOffset(sectorBuf, srcSectorSize);
                await destinationIsoStream.WriteAsync(sectorBuf.AsMemory(userOffset, OpticalConstants.DataSectorSize), ct).ConfigureAwait(false);
            }

            writtenSectors++;

            if (progress != null && (writtenSectors % 64 == 0 || writtenSectors == totalSectors))
            {
                progress.Report(new BurnProgressSnapshot
                {
                    CurrentLba = writtenSectors,
                    TotalLba = totalSectors,
                    BufferLevelPercent = 100.0,
                    CurrentActionDescription = $"Converting {info.FormatName} to ISO"
                });
            }
        }

        await destinationIsoStream.FlushAsync(ct).ConfigureAwait(false);
        return writtenSectors;
    }

    #region Synthetic Builders for Deterministic Testing

    /// <summary>
    /// Creates a valid Nero 5.5+ (.nrg) image file wrapping a 2,048-byte ISO payload and appending a canonical NER5 trailer
    /// (ETN2 + END! chunks + "NER5" + 64-bit big-endian trailer offset).
    /// </summary>
    public static void CreateNrgFile(string nrgFilePath, ReadOnlySpan<byte> isoPayloadBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nrgFilePath);
        string? dir = Path.GetDirectoryName(nrgFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var fs = new FileStream(nrgFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
        fs.Write(isoPayloadBytes);

        ulong trailerOffset = (ulong)fs.Position;

        // Write ETN2 chunk (32 bytes body):
        // [0..7] uint64 BE offset = 0
        // [8..15] uint64 BE size = isoPayloadBytes.Length
        // [16..19] uint32 BE mode = 0 (2048-byte Mode 1)
        // [20..23] uint32 BE startLba = 0
        // [24..31] uint64 BE lengthSectors
        Span<byte> etn2 = stackalloc byte[8 + 32];
        "ETN2"u8.CopyTo(etn2.Slice(0, 4));
        BinaryPrimitives.WriteUInt32BigEndian(etn2.Slice(4, 4), 32);
        BinaryPrimitives.WriteUInt64BigEndian(etn2.Slice(8, 8), 0UL);
        BinaryPrimitives.WriteUInt64BigEndian(etn2.Slice(16, 8), (ulong)isoPayloadBytes.Length);
        BinaryPrimitives.WriteUInt32BigEndian(etn2.Slice(24, 4), 0U); // Mode 0 = 2048-byte sectors
        BinaryPrimitives.WriteUInt32BigEndian(etn2.Slice(28, 4), 0U);
        BinaryPrimitives.WriteUInt64BigEndian(etn2.Slice(32, 8), (ulong)(isoPayloadBytes.Length / OpticalConstants.DataSectorSize));
        fs.Write(etn2);

        // Write END! chunk + NER5 12-byte footer
        Span<byte> footer = stackalloc byte[8 + 12];
        "END!"u8.CopyTo(footer.Slice(0, 4));
        BinaryPrimitives.WriteUInt32BigEndian(footer.Slice(4, 4), 0U);
        "NER5"u8.CopyTo(footer.Slice(8, 4));
        BinaryPrimitives.WriteUInt64BigEndian(footer.Slice(12, 8), trailerOffset);
        fs.Write(footer);
    }

    /// <summary>
    /// Creates a CloneCD (.ccd + .img) image pair from 2,048-byte Mode 1 sector data (wrapping each sector into a 2,352-byte Mode 1 raw sector).
    /// </summary>
    public static void CreateCloneCdImage(string ccdFilePath, ReadOnlySpan<byte> mode1IsoBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ccdFilePath);
        string imgFilePath = Path.ChangeExtension(ccdFilePath, ".img");
        int sectorCount = OpticalConstants.BytesToSectors(mode1IsoBytes.Length, OpticalConstants.DataSectorSize);

        using (var imgStream = new FileStream(imgFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            Span<byte> rawSector = stackalloc byte[OpticalConstants.RawSectorSize];
            for (int lba = 0; lba < sectorCount; lba++)
            {
                rawSector.Clear();
                // 12-byte CD-ROM sync pattern: 00 FF FF FF FF FF FF FF FF FF FF 00
                rawSector[0] = 0x00;
                rawSector.Slice(1, 10).Fill(0xFF);
                rawSector[11] = 0x00;
                // Mode 1 byte at offset 15
                rawSector[15] = 0x01;

                int srcOffset = lba * OpticalConstants.DataSectorSize;
                int copyLen = Math.Min(OpticalConstants.DataSectorSize, mode1IsoBytes.Length - srcOffset);
                if (copyLen > 0)
                {
                    mode1IsoBytes.Slice(srcOffset, copyLen).CopyTo(rawSector.Slice(16, copyLen));
                }

                imgStream.Write(rawSector);
            }
        }

        string ccdText = $"""
            [CloneCD]
            Version=3
            [Disc]
            TocEntries=1
            Sessions=1
            DataTracksScrambled=0
            [Track 1]
            MODE=1
            INDEX 1=0
            """;
        File.WriteAllText(ccdFilePath, ccdText, Encoding.ASCII);
    }

    /// <summary>
    /// Creates an Alcohol 120% (.mds + .mdf) image pair from a 2,048-byte ISO payload.
    /// </summary>
    public static void CreateAlcoholMdsMdfImage(string mdsFilePath, ReadOnlySpan<byte> isoPayloadBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mdsFilePath);
        string mdfFilePath = Path.ChangeExtension(mdsFilePath, ".mdf");
        File.WriteAllBytes(mdfFilePath, isoPayloadBytes.ToArray());

        // Write 88-byte Alcohol 120% v1.x MEDIA DESCRIPTOR header
        byte[] mdsBytes = new byte[88];
        MdsSignature.CopyTo(mdsBytes.AsSpan(0, 16));
        mdsBytes[16] = 1; // Version 1.2
        mdsBytes[17] = 2;
        File.WriteAllBytes(mdsFilePath, mdsBytes);
    }

    #endregion

    #region Format Inspectors

    private static LegacyDiscImageInfo InspectNrg(string nrgPath)
    {
        using var fs = new FileStream(nrgPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        long fileLen = fs.Length;
        long trailerOffset = fileLen;
        string versionTag = "Nero Burning ROM (.nrg)";
        int sectorSize = OpticalConstants.DataSectorSize;
        long payloadOffset = 0;
        long payloadLength = fileLen;
        int trackCount = 1;
        bool isAudio = false;

        if (fileLen >= 12)
        {
            Span<byte> tail12 = stackalloc byte[12];
            fs.Position = fileLen - 12;
            if (fs.Read(tail12) == 12)
            {
                if (tail12.Slice(0, 4).SequenceEqual("NER5"u8))
                {
                    ulong off = BinaryPrimitives.ReadUInt64BigEndian(tail12.Slice(4, 8));
                    if (off > 0 && (long)off < fileLen - 12)
                    {
                        trailerOffset = (long)off;
                        payloadLength = trailerOffset;
                        versionTag = "Nero Burning ROM v5.5+ (NER5)";
                    }
                }
                else if (tail12.Slice(4, 4).SequenceEqual("NERO"u8))
                {
                    uint off = BinaryPrimitives.ReadUInt32BigEndian(tail12.Slice(8, 4));
                    if (off > 0 && off < fileLen - 8)
                    {
                        trailerOffset = off;
                        payloadLength = trailerOffset;
                        versionTag = "Nero Burning ROM Legacy (NERO)";
                    }
                }
            }
        }

        // Parse IFF-style chunks in trailer if present
        if (trailerOffset < fileLen - 12)
        {
            fs.Position = trailerOffset;
            Span<byte> chunkHeader = stackalloc byte[8];
            Span<byte> etn2 = stackalloc byte[32];
            Span<byte> etnf = stackalloc byte[20];
            Span<byte> sinf = stackalloc byte[4];
            while (fs.Position + 8 <= fileLen - 8)
            {
                if (fs.Read(chunkHeader) < 8) break;
                string id = Encoding.ASCII.GetString(chunkHeader.Slice(0, 4));
                uint len = BinaryPrimitives.ReadUInt32BigEndian(chunkHeader.Slice(4, 4));
                long nextChunkPos = fs.Position + len;

                if (id == "END!")
                {
                    break;
                }

                if (id == "ETN2" && len >= 32 && fs.Position + 32 <= fileLen)
                {
                    if (fs.Read(etn2) == 32)
                    {
                        ulong off = BinaryPrimitives.ReadUInt64BigEndian(etn2.Slice(0, 8));
                        ulong sz = BinaryPrimitives.ReadUInt64BigEndian(etn2.Slice(8, 8));
                        uint mode = BinaryPrimitives.ReadUInt32BigEndian(etn2.Slice(16, 4));
                        if ((long)off >= 0 && (long)(off + sz) <= trailerOffset && sz > 0)
                        {
                            payloadOffset = (long)off;
                            payloadLength = (long)sz;
                        }
                        sectorSize = NrgModeToSectorSize(mode, sectorSize);
                        isAudio = mode is 7 or 16;
                    }
                }
                else if (id == "ETNF" && len >= 20 && fs.Position + 20 <= fileLen)
                {
                    if (fs.Read(etnf) == 20)
                    {
                        uint off = BinaryPrimitives.ReadUInt32BigEndian(etnf.Slice(0, 4));
                        uint sz = BinaryPrimitives.ReadUInt32BigEndian(etnf.Slice(4, 4));
                        uint mode = BinaryPrimitives.ReadUInt32BigEndian(etnf.Slice(8, 4));
                        if (off + sz <= trailerOffset && sz > 0)
                        {
                            payloadOffset = off;
                            payloadLength = sz;
                        }
                        sectorSize = NrgModeToSectorSize(mode, sectorSize);
                        isAudio = mode is 7 or 16;
                    }
                }
                else if (id == "SINF" && len >= 4 && fs.Position + 4 <= fileLen)
                {
                    if (fs.Read(sinf) == 4)
                    {
                        int trks = (int)BinaryPrimitives.ReadUInt32BigEndian(sinf);
                        if (trks is > 0 and <= 99) trackCount = trks;
                    }
                }

                if (nextChunkPos <= fs.Position || nextChunkPos > fileLen) break;
                fs.Position = nextChunkPos;
            }
        }

        // Locate the ISO 9660 Primary Volume Descriptor to confirm the sector geometry and skip a stored
        // 150-sector pre-gap (Nero DAO images often keep track 1's 2-second pre-gap in front of the data).
        if (!isAudio && TryProbeIsoGeometry(fs, payloadOffset, payloadLength, out int probedSize, out long pregapBytes))
        {
            sectorSize = probedSize;
            payloadOffset += pregapBytes;
            payloadLength -= pregapBytes;
        }

        int sectors = OpticalConstants.BytesToSectors(payloadLength, sectorSize);
        return new LegacyDiscImageInfo(
            SourceFilePath: nrgPath,
            BinaryPayloadPath: nrgPath,
            FormatName: versionTag,
            PayloadOffsetBytes: payloadOffset,
            PayloadLengthBytes: payloadLength,
            SectorSizeBytes: sectorSize,
            SectorCount: sectors,
            IsRaw2352: sectorSize == OpticalConstants.RawSectorSize,
            IsAudioDisc: isAudio,
            TrackCount: trackCount);
    }

    private static LegacyDiscImageInfo InspectCloneCdCcd(string ccdPath)
    {
        string dir = Path.GetDirectoryName(ccdPath) ?? string.Empty;
        string baseName = Path.GetFileNameWithoutExtension(ccdPath);
        string imgPath = Path.Combine(dir, baseName + ".img");
        if (!File.Exists(imgPath))
        {
            string binCandidate = Path.Combine(dir, baseName + ".bin");
            if (File.Exists(binCandidate)) imgPath = binCandidate;
        }

        int trackCount = 0;
        var trackModes = new List<int>();
        foreach (string rawLine in File.ReadLines(ccdPath))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("[Track ", StringComparison.OrdinalIgnoreCase))
            {
                trackCount++;
            }
            else if (line.StartsWith("MODE=", StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(line.AsSpan(5).Trim(), out int parsedMode))
            {
                trackModes.Add(parsedMode);
            }
        }

        if (trackCount == 0) trackCount = 1;

        // Only a disc whose tracks are ALL audio (MODE=0) is an Audio CD. Mixed-mode / Enhanced CDs
        // (data track + audio tracks) still carry a convertible data track.
        bool isAudio = trackModes.Count > 0 && trackModes.All(m => m == 0);

        long payloadOffset = 0;
        long payloadLen = File.Exists(imgPath) ? new FileInfo(imgPath).Length : new FileInfo(ccdPath).Length;
        int sectorSize = OpticalConstants.RawSectorSize;
        if (File.Exists(imgPath) && !isAudio)
        {
            using var fs = new FileStream(imgPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (TryProbeIsoGeometry(fs, 0, payloadLen, out int probedSize, out long pregapBytes))
            {
                sectorSize = probedSize;
                payloadOffset = pregapBytes;
                payloadLen -= pregapBytes;
            }
        }

        int sectors = OpticalConstants.BytesToSectors(payloadLen, sectorSize);
        return new LegacyDiscImageInfo(
            SourceFilePath: ccdPath,
            BinaryPayloadPath: File.Exists(imgPath) ? imgPath : ccdPath,
            FormatName: "CloneCD Control Image (.ccd/.img)",
            PayloadOffsetBytes: payloadOffset,
            PayloadLengthBytes: payloadLen,
            SectorSizeBytes: sectorSize,
            SectorCount: sectors,
            IsRaw2352: sectorSize == OpticalConstants.RawSectorSize,
            IsAudioDisc: isAudio,
            TrackCount: trackCount);
    }

    private static LegacyDiscImageInfo InspectCloneCdImg(string imgPath)
    {
        string ccdCandidate = Path.ChangeExtension(imgPath, ".ccd");
        if (File.Exists(ccdCandidate))
        {
            var ccdInfo = InspectCloneCdCcd(ccdCandidate);
            return ccdInfo with { SourceFilePath = imgPath, BinaryPayloadPath = imgPath };
        }

        using var fs = new FileStream(imgPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var (sectorSize, offset, len) = ProbeOrGuessGeometry(fs);
        bool isRaw = sectorSize != OpticalConstants.DataSectorSize;
        int sectors = OpticalConstants.BytesToSectors(len, sectorSize);

        return new LegacyDiscImageInfo(
            SourceFilePath: imgPath,
            BinaryPayloadPath: imgPath,
            FormatName: isRaw ? $"CloneCD / Raw Sector Image (.img, {sectorSize} B)" : "Raw ISO Sector Image (.img, 2048 B)",
            PayloadOffsetBytes: offset,
            PayloadLengthBytes: len,
            SectorSizeBytes: sectorSize,
            SectorCount: sectors,
            IsRaw2352: isRaw,
            IsAudioDisc: false,
            TrackCount: 1);
    }

    private static LegacyDiscImageInfo InspectAlcoholMds(string mdsPath)
    {
        string mdfPath = Path.ChangeExtension(mdsPath, ".mdf");
        string binaryPath = File.Exists(mdfPath) ? mdfPath : mdsPath;
        var info = InspectAlcoholMdf(binaryPath);
        return info with
        {
            SourceFilePath = mdsPath,
            BinaryPayloadPath = binaryPath,
            FormatName = "Alcohol 120% Media Descriptor (.mds/.mdf)"
        };
    }

    private static LegacyDiscImageInfo InspectAlcoholMdf(string mdfPath)
    {
        using var fs = new FileStream(mdfPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var (sectorSize, offset, len) = ProbeOrGuessGeometry(fs);
        bool isRaw = sectorSize != OpticalConstants.DataSectorSize;
        int sectors = OpticalConstants.BytesToSectors(len, sectorSize);

        return new LegacyDiscImageInfo(
            SourceFilePath: mdfPath,
            BinaryPayloadPath: mdfPath,
            FormatName: isRaw ? $"Alcohol 120% Raw Image (.mdf, {sectorSize} B)" : "Alcohol 120% ISO Image (.mdf, 2048 B)",
            PayloadOffsetBytes: offset,
            PayloadLengthBytes: len,
            SectorSizeBytes: sectorSize,
            SectorCount: sectors,
            IsRaw2352: isRaw,
            IsAudioDisc: false,
            TrackCount: 1);
    }

    /// <summary>
    /// Maps a Nero ETN2/ETNF track mode code to its stored sector size:
    /// 0/2 = 2048 (Mode 1 / Mode 2 Form 1 cooked), 3 = 2336 (Mode 2 formless), 5/6/7 = 2352 raw (Mode 1 / Mode 2 / Audio),
    /// 15/16/17 = 2448 (2352 raw + 96-byte subchannel).
    /// </summary>
    private static int NrgModeToSectorSize(uint mode, int fallback) => mode switch
    {
        0 or 2 => OpticalConstants.DataSectorSize,
        3 => SectorSize2336,
        5 or 6 or 7 => OpticalConstants.RawSectorSize,
        15 or 16 or 17 => SectorSize2448,
        _ => fallback
    };

    /// <summary>
    /// Finds the ISO 9660 Primary Volume Descriptor ("CD001" at user-data byte 1 of sector 16) by trying every stored sector
    /// geometry (2048; 2352 Mode 1 @16; 2352 Mode 2 XA @24; 2336 @8; 2448 with subchannel) with and without a stored
    /// 150-sector pre-gap. Returns the sector size and how many pre-gap bytes precede sector 0 of the data track.
    /// </summary>
    private static bool TryProbeIsoGeometry(Stream stream, long baseOffset, long payloadLength, out int sectorSize, out long pregapBytes)
    {
        (int Size, int UserOffset)[] geometries =
        {
            (OpticalConstants.DataSectorSize, 0),
            (OpticalConstants.RawSectorSize, 16),
            (OpticalConstants.RawSectorSize, 24),
            (SectorSize2336, 8),
            (SectorSize2448, 16),
            (SectorSize2448, 24),
        };

        Span<byte> buf = stackalloc byte[5];
        foreach (int lead in new[] { 0, StandardPregapSectors })
        {
            foreach (var (size, userOffset) in geometries)
            {
                long pvd = baseOffset + ((lead + 16L) * size) + userOffset + 1;
                if (pvd + 5 > baseOffset + payloadLength || pvd + 5 > stream.Length) continue;
                stream.Position = pvd;
                if (stream.Read(buf) == 5 && buf.SequenceEqual(IsoCd001Magic))
                {
                    sectorSize = size;
                    pregapBytes = (long)lead * size;
                    return true;
                }
            }
        }

        sectorSize = 0;
        pregapBytes = 0;
        return false;
    }

    /// <summary>
    /// Probes the PVD geometry; without an ISO 9660 descriptor (e.g. pure UDF) falls back to a size-divisibility guess.
    /// </summary>
    private static (int SectorSize, long Offset, long Length) ProbeOrGuessGeometry(FileStream fs)
    {
        long len = fs.Length;
        if (TryProbeIsoGeometry(fs, 0, len, out int size, out long pregap))
        {
            return (size, pregap, len - pregap);
        }

        bool isRaw = len % OpticalConstants.RawSectorSize == 0 && len % OpticalConstants.DataSectorSize != 0;
        return (isRaw ? OpticalConstants.RawSectorSize : OpticalConstants.DataSectorSize, 0, len);
    }

    /// <summary>
    /// Read-only seekable stream exposing the 2,048-byte user data of every sector of a legacy image, one sector at a time.
    /// </summary>
    private sealed class LegacyIsoUserDataStream : Stream
    {
        private readonly LegacyDiscImageInfo _info;
        private readonly FileStream _source;
        private readonly byte[] _sector;
        private readonly long _length;
        private long _position;
        private long _cachedSector = -1;

        public LegacyIsoUserDataStream(LegacyDiscImageInfo info)
        {
            _info = info;
            _source = new FileStream(info.BinaryPayloadPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            _sector = new byte[Math.Max(info.SectorSizeBytes, OpticalConstants.DataSectorSize)];
            _length = (long)info.SectorCount * OpticalConstants.DataSectorSize;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => _position = Math.Clamp(value, 0, _length);
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int total = 0;
            while (buffer.Length > 0 && _position < _length)
            {
                long sectorIndex = _position / OpticalConstants.DataSectorSize;
                int within = (int)(_position % OpticalConstants.DataSectorSize);
                ReadOnlySpan<byte> user = LoadUserData(sectorIndex);
                int n = Math.Min(buffer.Length, OpticalConstants.DataSectorSize - within);
                user.Slice(within, n).CopyTo(buffer);
                buffer = buffer[n..];
                _position += n;
                total += n;
            }
            return total;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private ReadOnlySpan<byte> LoadUserData(long sectorIndex)
        {
            int size = _info.SectorSizeBytes;
            if (_cachedSector != sectorIndex)
            {
                _source.Position = _info.PayloadOffsetBytes + (sectorIndex * size);
                int got = 0;
                while (got < size)
                {
                    int r = _source.Read(_sector, got, size - got);
                    if (r <= 0) break;
                    got += r;
                }

                if (got < size)
                {
                    Array.Clear(_sector, got, size - got); // truncated image tail reads as zeros
                }

                _cachedSector = sectorIndex;
            }

            int userOffset = GetUserDataOffset(_sector.AsSpan(0, size), size);
            return _sector.AsSpan(userOffset, OpticalConstants.DataSectorSize);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            return _position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _source.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    #endregion
}
