using System.Diagnostics;
using System.Security.Cryptography;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Images;

/// <summary>
/// Maps a file in a disc compilation or filesystem image to its allocated sector extent (Start LBA, sector count, and byte size).
/// </summary>
public class LbaFileExtentMap
{
    /// <summary>
    /// Virtual path of the file within the disc compilation (e.g. "/Bin/app.bin" or "/README.TXT").
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Starting logical block address (LBA) on media.
    /// </summary>
    public int StartLba { get; set; }

    /// <summary>
    /// Total number of logical sectors allocated for this file.
    /// </summary>
    public int SectorCount { get; set; }

    /// <summary>
    /// File size in bytes.
    /// </summary>
    public long FileSizeBytes { get; set; }

    /// <summary>
    /// Ending logical block address (inclusive).
    /// </summary>
    public int EndLba => StartLba + (SectorCount > 0 ? SectorCount - 1 : 0);

    public LbaFileExtentMap() { }

    public LbaFileExtentMap(string filePath, int startLba, int sectorCount, long fileSizeBytes = 0)
    {
        FilePath = filePath;
        StartLba = startLba;
        SectorCount = sectorCount;
        FileSizeBytes = fileSizeBytes;
    }

    /// <summary>
    /// Returns true if the specified LBA address falls within this file's allocated sector extent.
    /// </summary>
    public bool ContainsLba(int lba)
    {
        if (SectorCount <= 0)
        {
            return lba == StartLba;
        }
        return lba >= StartLba && lba < StartLba + SectorCount;
    }

    /// <summary>
    /// Builds a list of LBA file extents from an ISO image stream.
    /// </summary>
    public static IReadOnlyList<LbaFileExtentMap> FromIsoStream(Stream isoStream, int sessionStartLba = 0)
    {
        ArgumentNullException.ThrowIfNull(isoStream);
        long originalPos = isoStream.CanSeek ? isoStream.Position : 0;
        try
        {
            using var reader = IsoImageReader.FromStream(isoStream, leaveOpen: true, sessionStartLba);
            return FromIsoReader(reader);
        }
        finally
        {
            if (isoStream.CanSeek)
            {
                isoStream.Position = originalPos;
            }
        }
    }

    /// <summary>
    /// Builds a list of LBA file extents from an <see cref="IsoImageReader"/>.
    /// </summary>
    public static IReadOnlyList<LbaFileExtentMap> FromIsoReader(IsoImageReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var map = new List<LbaFileExtentMap>();
        foreach (var file in reader.AllFiles)
        {
            int sectors = OpticalConstants.BytesToSectors(file.SizeBytes, OpticalConstants.DataSectorSize);
            map.Add(new LbaFileExtentMap(file.VirtualPath, file.StartLba, sectors, file.SizeBytes));
        }
        return map;
    }

    public override string ToString() => $"{FilePath} [LBA {StartLba}..{EndLba}, {SectorCount} sectors, {FileSizeBytes} B]";
}

/// <summary>
/// Detailed result of a disc verification scan comparing physical/virtual disc sectors against an expected image stream.
/// </summary>
public class VerificationResult
{
    /// <summary>
    /// Indicates whether every sector on the disc matched the expected stream bit-for-bit without read errors.
    /// </summary>
    public bool IsMatch { get; init; }

    /// <summary>
    /// Synonym for <see cref="IsMatch"/>.
    /// </summary>
    public bool Success => IsMatch;

    /// <summary>
    /// Total number of logical sectors verified.
    /// </summary>
    public int TotalSectorsVerified { get; init; }

    /// <summary>
    /// List of all LBA addresses where data differed or an unrecovered read error occurred.
    /// </summary>
    public IReadOnlyList<int> MismatchedLbas { get; init; } = Array.Empty<int>();

    /// <summary>
    /// Compilation file paths corresponding to mismatched or unreadable LBAs.
    /// </summary>
    public IReadOnlyList<string> CorruptedFiles { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Hexadecimal CRC32 checksum of the expected source stream.
    /// </summary>
    public string ExpectedCrc32 { get; init; } = string.Empty;

    /// <summary>
    /// Hexadecimal CRC32 checksum computed from sectors read back from disc.
    /// </summary>
    public string ActualCrc32 { get; init; } = string.Empty;

    /// <summary>
    /// Hexadecimal MD5 hash of the expected source stream.
    /// </summary>
    public string ExpectedMd5 { get; init; } = string.Empty;

    /// <summary>
    /// Hexadecimal MD5 hash computed from sectors read back from disc.
    /// </summary>
    public string ActualMd5 { get; init; } = string.Empty;

    /// <summary>
    /// Hexadecimal SHA-256 hash of the expected source stream.
    /// </summary>
    public string ExpectedSha256 { get; init; } = string.Empty;

    /// <summary>
    /// Hexadecimal SHA-256 hash computed from sectors read back from disc.
    /// </summary>
    public string ActualSha256 { get; init; } = string.Empty;

    /// <summary>
    /// Numeric CRC32 value of expected stream.
    /// </summary>
    public uint ExpectedCrc32Value { get; init; }

    /// <summary>
    /// Numeric CRC32 value of read-back disc stream.
    /// </summary>
    public uint ActualCrc32Value { get; init; }

    /// <summary>
    /// Total execution duration of verification.
    /// </summary>
    public TimeSpan TotalTime { get; init; }

    /// <summary>
    /// Error message describing mismatches or read failures, or null if matched.
    /// </summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// IEEE 802.3 32-bit Cyclic Redundancy Check incremental calculator.
/// </summary>
internal sealed class Crc32Algorithm
{
    private static readonly uint[] Table = GenerateTable();

    private static uint[] GenerateTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint crc = i;
            for (int j = 0; j < 8; j++)
            {
                if ((crc & 1) != 0)
                {
                    crc = (crc >> 1) ^ 0xEDB88320u;
                }
                else
                {
                    crc >>= 1;
                }
            }
            table[i] = crc;
        }
        return table;
    }

    private uint _crc = 0xFFFFFFFFu;

    public void Append(ReadOnlySpan<byte> data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            _crc = Table[(_crc ^ data[i]) & 0xFF] ^ (_crc >> 8);
        }
    }

    public uint GetCurrentHashAsUInt32() => ~_crc;

    public string GetCurrentHashAsHexString() => GetCurrentHashAsUInt32().ToString("X8");
}

/// <summary>
/// Sector-by-sector verification service for optical media, validating recorded discs against
/// source image streams or compilations with CRC32, MD5, and SHA-256 checksums and file defect mapping.
/// </summary>
public class DiscVerificationService
{
    /// <summary>
    /// Verifies a recorded disc sector-by-sector against an expected image stream.
    /// </summary>
    public async Task<VerificationResult> VerifyDiscAgainstStreamAsync(
        IDiscRecorderBackend recorder,
        IBurnEngineBackend engine,
        Stream expectedStream,
        bool raw2352Mode,
        IReadOnlyList<LbaFileExtentMap>? fileMap = null,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(expectedStream);

        ct.ThrowIfCancellationRequested();

        var mediaStatus = recorder.QueryMediaStatus();
        if (!mediaStatus.MediaPresent)
        {
            return new VerificationResult
            {
                IsMatch = false,
                ErrorMessage = "No optical media present in recorder for verification."
            };
        }

        int sectorSize = raw2352Mode ? OpticalConstants.RawSectorSize : OpticalConstants.DataSectorSize;

        // Auto-extract LBA file map from ISO stream if not provided
        if ((fileMap == null || fileMap.Count == 0) && !raw2352Mode && expectedStream.CanSeek && expectedStream.Length >= 17 * OpticalConstants.DataSectorSize)
        {
            try
            {
                fileMap = LbaFileExtentMap.FromIsoStream(expectedStream);
            }
            catch
            {
                // Not an ISO 9660 stream or parsing failed; proceed without file mapping
                fileMap = Array.Empty<LbaFileExtentMap>();
            }
        }

        if (expectedStream.CanSeek)
        {
            expectedStream.Position = 0;
        }

        long streamLength = expectedStream.CanSeek ? expectedStream.Length : -1;
        int totalSectors = streamLength >= 0
            ? OpticalConstants.BytesToSectors(streamLength, sectorSize)
            : (mediaStatus.UsedSectors > 0 ? mediaStatus.UsedSectors : 0);

        if (totalSectors <= 0)
        {
            return new VerificationResult
            {
                IsMatch = false,
                ErrorMessage = "Cannot determine sector count to verify from empty or unseekable stream."
            };
        }

        var sw = Stopwatch.StartNew();
        var mismatchedLbas = new List<int>();
        var corruptedFiles = new List<string>();

        var expectedCrc = new Crc32Algorithm();
        var actualCrc = new Crc32Algorithm();

        using var expectedMd5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var actualMd5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);

        using var expectedSha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var actualSha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        byte[] expectedBuffer = new byte[sectorSize];
        byte[] zeroBuffer = new byte[sectorSize];

        // Disc sectors arrive strictly in LBA order (a retried sector is always the next one), so the expected
        // image is consumed in lockstep with them.
        async ValueTask ReadNextExpectedSectorAsync()
        {
            int bytesRead = 0;
            while (bytesRead < sectorSize)
            {
                int read = await expectedStream.ReadAsync(expectedBuffer.AsMemory(bytesRead, sectorSize - bytesRead), ct).ConfigureAwait(false);
                if (read == 0) break;
                bytesRead += read;
            }
            if (bytesRead < sectorSize)
            {
                Array.Clear(expectedBuffer, bytesRead, sectorSize - bytesRead);
            }

            expectedCrc.Append(expectedBuffer);
            expectedMd5.AppendData(expectedBuffer);
            expectedSha.AppendData(expectedBuffer);
        }

        void AppendActual(ReadOnlySpan<byte> actual)
        {
            actualCrc.Append(actual);
            actualMd5.AppendData(actual);
            actualSha.AppendData(actual);
        }

        await BatchedSectorReader.ReadRangeAsync(
            engine,
            recorder,
            startLba: 0,
            totalSectors,
            raw2352Mode,
            maxRetriesPerSector: 1,
            onSector: async (lba, data) =>
            {
                await ReadNextExpectedSectorAsync().ConfigureAwait(false);
                if (!data.Span.SequenceEqual(expectedBuffer))
                {
                    mismatchedLbas.Add(lba);
                    MapCorruptedLba(lba, fileMap, corruptedFiles);
                }
                AppendActual(data.Span);
            },
            onUnreadable: async lba =>
            {
                await ReadNextExpectedSectorAsync().ConfigureAwait(false);
                mismatchedLbas.Add(lba);
                MapCorruptedLba(lba, fileMap, corruptedFiles);
                AppendActual(zeroBuffer);
                return true;
            },
            onProgress: done => progress?.Report(new BurnProgressSnapshot
            {
                ElapsedTime = sw.Elapsed,
                CurrentLba = done,
                TotalLba = totalSectors,
                CurrentActionDescription = "VerifyingSectors",
                BufferLevelPercent = 100.0,
                CurrentSpeedMultiplier = 24.0,
                CurrentBytesPerSecond = 24 * (raw2352Mode ? OpticalConstants.CdAudio1XBytesPerSec : OpticalConstants.CdData1XBytesPerSec)
            }),
            cancellationToken: ct).ConfigureAwait(false);

        progress?.Report(new BurnProgressSnapshot
        {
            ElapsedTime = sw.Elapsed,
            CurrentLba = totalSectors,
            TotalLba = totalSectors,
            CurrentActionDescription = "Completed"
        });

        uint expCrcVal = expectedCrc.GetCurrentHashAsUInt32();
        uint actCrcVal = actualCrc.GetCurrentHashAsUInt32();

        string expCrc = expectedCrc.GetCurrentHashAsHexString();
        string actCrc = actualCrc.GetCurrentHashAsHexString();

        string expMd5 = Convert.ToHexString(expectedMd5.GetHashAndReset());
        string actMd5 = Convert.ToHexString(actualMd5.GetHashAndReset());

        string expSha = Convert.ToHexString(expectedSha.GetHashAndReset());
        string actSha = Convert.ToHexString(actualSha.GetHashAndReset());

        bool isMatch = mismatchedLbas.Count == 0 && string.Equals(expSha, actSha, StringComparison.OrdinalIgnoreCase);

        return new VerificationResult
        {
            IsMatch = isMatch,
            TotalSectorsVerified = totalSectors,
            MismatchedLbas = mismatchedLbas,
            CorruptedFiles = corruptedFiles,
            ExpectedCrc32 = expCrc,
            ActualCrc32 = actCrc,
            ExpectedMd5 = expMd5,
            ActualMd5 = actMd5,
            ExpectedSha256 = expSha,
            ActualSha256 = actSha,
            ExpectedCrc32Value = expCrcVal,
            ActualCrc32Value = actCrcVal,
            TotalTime = sw.Elapsed,
            ErrorMessage = isMatch ? null : $"Verification failed: {mismatchedLbas.Count} sector mismatch(es) detected."
        };
    }

    /// <summary>
    /// Verifies a recorded data disc against an .iso file on disk.
    /// </summary>
    public async Task<VerificationResult> VerifyDiscAgainstIsoFileAsync(
        IDiscRecorderBackend recorder,
        IBurnEngineBackend engine,
        string isoFilePath,
        IReadOnlyList<LbaFileExtentMap>? fileMap = null,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(isoFilePath);
        if (!File.Exists(isoFilePath))
        {
            throw new FileNotFoundException("ISO image file not found.", isoFilePath);
        }

        await using var fs = new FileStream(isoFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        return await VerifyDiscAgainstStreamAsync(recorder, engine, fs, raw2352Mode: false, fileMap, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Verifies a recorded disc against a virtual filesystem compilation tree.
    /// </summary>
    public async Task<VerificationResult> VerifyDiscAgainstCompilationAsync(
        IDiscRecorderBackend recorder,
        IBurnEngineBackend engine,
        CompilationDirectory rootDirectory,
        IsoBuildOptions? buildOptions = null,
        IProgress<BurnProgressSnapshot>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rootDirectory);

        long estimatedPayloadBytes = rootDirectory.GetAllFilesRecursive().Sum(f => f.SizeBytes);
        Stream isoStream = estimatedPayloadBytes > 128L * 1024 * 1024
            ? new FileStream(
                Path.Combine(Path.GetTempPath(), $"cdburner_verify_{Guid.NewGuid():N}.tmp"),
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 65536,
                options: FileOptions.DeleteOnClose | FileOptions.SequentialScan | FileOptions.Asynchronous)
            : new MemoryStream();

        await using (isoStream.ConfigureAwait(false))
        {
            await IsoUdfImageBuilder.BuildAsync(rootDirectory, isoStream, buildOptions, null, ct).ConfigureAwait(false);
            isoStream.Position = 0;
            var fileMap = LbaFileExtentMap.FromIsoStream(isoStream);
            isoStream.Position = 0;
            return await VerifyDiscAgainstStreamAsync(recorder, engine, isoStream, raw2352Mode: false, fileMap, progress, ct).ConfigureAwait(false);
        }
    }

    private static void MapCorruptedLba(int lba, IReadOnlyList<LbaFileExtentMap>? fileMap, List<string> corruptedFiles)
    {
        if (fileMap == null) return;
        foreach (var extent in fileMap)
        {
            if (extent.ContainsLba(lba))
            {
                if (!corruptedFiles.Contains(extent.FilePath, StringComparer.OrdinalIgnoreCase))
                {
                    corruptedFiles.Add(extent.FilePath);
                }
            }
        }
    }
}
