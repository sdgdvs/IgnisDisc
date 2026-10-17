using CdBurner.Core.Primitives;

namespace CdBurner.Hardware.Simulation;

/// <summary>
/// Represents an optical recording session stored on a virtual disc.
/// </summary>
public class VirtualSession
{
    public int SessionNumber { get; set; } = 1;
    public int StartLba { get; set; }
    public int SectorCount { get; set; }
    public int EndLba => StartLba + SectorCount - 1;
    public List<VirtualTrack> Tracks { get; } = new();
}

/// <summary>
/// Represents a track within a virtual optical recording session.
/// </summary>
public class VirtualTrack
{
    public int TrackNumber { get; set; } = 1;
    public int StartLba { get; set; }
    public int SectorCount { get; set; }
    public int EndLba => StartLba + SectorCount - 1;
    public bool IsAudio { get; set; }
    public int PreGapFrames { get; set; }
    public string? Title { get; set; }
    public string? Performer { get; set; }
    public string? Songwriter { get; set; }
    public string? Composer { get; set; }
    public string? Isrc { get; set; }
}

/// <summary>
/// Software-defined virtual optical disc medium supporting memory or file-backed storage,
/// artificial bad sector injection, multi-session recording, and rewritable media blanking.
/// </summary>
public class VirtualDiscMedium : IDisposable
{
    private readonly object _syncRoot = new();
    private readonly Dictionary<int, byte[]> _memorySectors = new();
    private FileStream? _fileStream;
    private readonly bool _deleteBackingFileOnDispose;
    private bool _disposed;

    /// <summary>
    /// Optical physical medium classification (e.g., CD-R, CD-RW, DVD-R, DVD+R DL).
    /// </summary>
    public DiscMediaType MediaType { get; set; }

    /// <summary>
    /// Nominal total capacity in logical sectors.
    /// </summary>
    public int TotalCapacitySectors { get; set; }

    /// <summary>
    /// Indicates whether the media contains zero recorded data.
    /// </summary>
    public bool IsBlank { get; set; }

    /// <summary>
    /// Indicates whether this medium supports physical erasure and rewriting.
    /// </summary>
    public bool IsRewritable => MediaType.IsRewritable();

    /// <summary>
    /// Indicates whether the disc has been closed/finalized, prohibiting further multi-session appending.
    /// </summary>
    public bool IsFinalized { get; set; }

    /// <summary>
    /// Optional file path on disk backing the sector store. If null, an in-memory sparse store is used.
    /// </summary>
    public string? BackingFilePath { get; }

    /// <summary>
    /// Indicates whether sectors are stored in raw 2352-byte frames (CD-DA / Mode 2) or standard 2048-byte Mode 1 blocks.
    /// </summary>
    public bool Raw2352Mode { get; set; }

    /// <summary>
    /// Raw Lead-In CD-Text pack bytes (Red Book Annex J) if recorded on this disc.
    /// </summary>
    public byte[]? CdTextBytes { get; set; }

    /// <summary>
    /// Recorded sessions present on this media.
    /// </summary>
    public List<VirtualSession> Sessions { get; } = new();

    /// <summary>
    /// Set of LBA addresses configured to trigger unrecovered read errors during rip and verification scans.
    /// </summary>
    public HashSet<int> SimulatedBadLbas { get; } = new();

    /// <summary>
    /// Total number of logical sectors recorded so far.
    /// </summary>
    public int UsedSectors { get; set; }

    /// <summary>
    /// Remaining unwritten sector capacity on this medium.
    /// </summary>
    public int FreeSectors => Math.Max(0, TotalCapacitySectors - UsedSectors);

    /// <summary>
    /// LBA address of the last recorded sector, or -1 if the medium is blank.
    /// </summary>
    public int LastRecordedLba => UsedSectors > 0
        ? (Sessions.Count > 0 ? Sessions[^1].EndLba : UsedSectors - 1)
        : -1;

    /// <summary>
    /// Default sector size in bytes for the current recording mode (2,352 for raw audio/binary, 2,048 for data).
    /// </summary>
    public int SectorSize => Raw2352Mode ? OpticalConstants.RawSectorSize : OpticalConstants.DataSectorSize;

    public VirtualDiscMedium(
        DiscMediaType mediaType,
        int totalCapacitySectors,
        bool isBlank = true,
        string? backingFilePath = null,
        bool raw2352Mode = false,
        bool deleteBackingFileOnDispose = false)
    {
        MediaType = mediaType;
        TotalCapacitySectors = totalCapacitySectors;
        IsBlank = isBlank;
        BackingFilePath = backingFilePath;
        Raw2352Mode = raw2352Mode;
        _deleteBackingFileOnDispose = deleteBackingFileOnDispose;

        if (!string.IsNullOrEmpty(backingFilePath))
        {
            string? dir = Path.GetDirectoryName(backingFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            _fileStream = new FileStream(backingFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        }
    }

    #region Factory Presets

    /// <summary>
    /// Creates a blank 80-minute / 700 MB CD-R virtual medium (360,000 sectors).
    /// </summary>
    public static VirtualDiscMedium CreateBlankCdR700(string? backingFile = null) =>
        new(DiscMediaType.CdR, OpticalConstants.Cd700MbSectors, isBlank: true, backingFilePath: backingFile);

    /// <summary>
    /// Creates a blank 80-minute / 700 MB CD-RW rewritable virtual medium (360,000 sectors).
    /// </summary>
    public static VirtualDiscMedium CreateBlankCdRw700(string? backingFile = null) =>
        new(DiscMediaType.CdRw, OpticalConstants.Cd700MbSectors, isBlank: true, backingFilePath: backingFile);

    /// <summary>
    /// Creates a blank single-layer 4.7 GB DVD-R virtual medium (2,298,496 sectors).
    /// </summary>
    public static VirtualDiscMedium CreateBlankDvdR47(string? backingFile = null) =>
        new(DiscMediaType.DvdMinusR, OpticalConstants.DvdMinusR5Sectors, isBlank: true, backingFilePath: backingFile);

    /// <summary>
    /// Creates a blank dual-layer 8.5 GB DVD+R DL virtual medium (4,173,824 sectors).
    /// </summary>
    public static VirtualDiscMedium CreateBlankDvdPlusRDL85(string? backingFile = null) =>
        new(DiscMediaType.DvdPlusRDualLayer, OpticalConstants.DvdPlusR9Sectors, isBlank: true, backingFilePath: backingFile);

    /// <summary>
    /// Creates a blank single-layer 4.7 GB DVD-RW rewritable virtual medium.
    /// </summary>
    public static VirtualDiscMedium CreateBlankDvdRw47(string? backingFile = null) =>
        new(DiscMediaType.DvdMinusRw, OpticalConstants.DvdMinusR5Sectors, isBlank: true, backingFilePath: backingFile);

    /// <summary>
    /// Creates a blank single-layer 4.7 GB DVD+RW rewritable virtual medium.
    /// </summary>
    public static VirtualDiscMedium CreateBlankDvdPlusRw47(string? backingFile = null) =>
        new(DiscMediaType.DvdPlusRw, OpticalConstants.DvdPlusR5Sectors, isBlank: true, backingFilePath: backingFile);

    /// <summary>
    /// Creates a blank DVD-RAM rewritable virtual medium.
    /// </summary>
    public static VirtualDiscMedium CreateBlankDvdRam(string? backingFile = null) =>
        new(DiscMediaType.DvdRam, OpticalConstants.DvdMinusR5Sectors, isBlank: true, backingFilePath: backingFile);

    /// <summary>
    /// Creates a virtual disc medium mounted from an existing image file on disk (e.g. .iso).
    /// </summary>
    public static VirtualDiscMedium FromExistingFile(string filePath, DiscMediaType mediaType = DiscMediaType.CdRom)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException("Backing image file does not exist.", filePath);
        }

        int sectors = (int)(fileInfo.Length / OpticalConstants.DataSectorSize);
        int totalCapacity = Math.Max(sectors, mediaType.GetNominalCapacitySectors());
        if (totalCapacity == 0) totalCapacity = OpticalConstants.Cd700MbSectors;

        var medium = new VirtualDiscMedium(mediaType, totalCapacity, isBlank: false, backingFilePath: filePath)
        {
            UsedSectors = sectors,
            IsFinalized = true
        };

        var session = new VirtualSession
        {
            SessionNumber = 1,
            StartLba = 0,
            SectorCount = sectors
        };
        session.Tracks.Add(new VirtualTrack
        {
            TrackNumber = 1,
            StartLba = 0,
            SectorCount = sectors,
            IsAudio = false
        });
        medium.Sessions.Add(session);

        return medium;
    }

    #endregion

    #region Sector Reading & Writing

    /// <summary>
    /// Reads a single sector from the virtual disc into the destination span.
    /// Throws <see cref="OpticalReadException"/> if the target LBA is registered in <see cref="SimulatedBadLbas"/>.
    /// </summary>
    public void ReadSector(int lba, Span<byte> destination, int? sectorSize = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (SimulatedBadLbas.Contains(lba))
        {
            throw new OpticalReadException(lba, $"Unrecovered read error at LBA {lba} (Sense: 03/11/00)");
        }

        if (lba < 0 || lba >= TotalCapacitySectors)
        {
            throw new ArgumentOutOfRangeException(nameof(lba), $"LBA {lba} is out of media capacity bounds [0..{TotalCapacitySectors - 1}].");
        }

        int sz = sectorSize ?? SectorSize;
        if (destination.Length < sz)
        {
            throw new ArgumentException($"Destination buffer length ({destination.Length}) is less than sector size ({sz}).", nameof(destination));
        }

        lock (_syncRoot)
        {
            if (_fileStream != null)
            {
                long offset = (long)lba * sz;
                if (offset >= _fileStream.Length)
                {
                    destination[..sz].Clear();
                }
                else
                {
                    _fileStream.Seek(offset, SeekOrigin.Begin);
                    int read = _fileStream.Read(destination[..sz]);
                    if (read < sz)
                    {
                        destination[read..sz].Clear();
                    }
                }
            }
            else
            {
                if (_memorySectors.TryGetValue(lba, out var stored))
                {
                    int copyLen = Math.Min(sz, stored.Length);
                    stored.AsSpan(0, copyLen).CopyTo(destination);
                    if (copyLen < sz)
                    {
                        destination[copyLen..sz].Clear();
                    }
                }
                else
                {
                    destination[..sz].Clear();
                }
            }
        }
    }

    /// <summary>
    /// Reads a single sector and returns a newly allocated byte array.
    /// </summary>
    public byte[] ReadSector(int lba, int? sectorSize = null)
    {
        int sz = sectorSize ?? SectorSize;
        byte[] buffer = new byte[sz];
        ReadSector(lba, buffer, sz);
        return buffer;
    }

    /// <summary>
    /// Reads consecutive sectors starting at <paramref name="startLba"/>.
    /// </summary>
    public void ReadSectors(int startLba, int sectorCount, Span<byte> destination, int? sectorSize = null)
    {
        if (sectorCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sectorCount), "Sector count cannot be negative.");
        }

        int sz = sectorSize ?? SectorSize;
        if (destination.Length < sectorCount * sz)
        {
            throw new ArgumentException($"Destination buffer length ({destination.Length}) is less than required ({sectorCount * sz}).", nameof(destination));
        }

        for (int i = 0; i < sectorCount; i++)
        {
            ReadSector(startLba + i, destination.Slice(i * sz, sz), sz);
        }
    }

    /// <summary>
    /// Writes a single sector to the virtual disc.
    /// </summary>
    public void WriteSector(int lba, ReadOnlySpan<byte> source, int? sectorSize = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (lba < 0 || lba >= TotalCapacitySectors)
        {
            throw new ArgumentOutOfRangeException(nameof(lba), $"LBA {lba} is out of media capacity bounds [0..{TotalCapacitySectors - 1}].");
        }

        int sz = sectorSize ?? SectorSize;
        if (source.Length < sz)
        {
            throw new ArgumentException($"Source buffer length ({source.Length}) is less than sector size ({sz}).", nameof(source));
        }

        lock (_syncRoot)
        {
            if (_fileStream != null)
            {
                long offset = (long)lba * sz;
                _fileStream.Seek(offset, SeekOrigin.Begin);
                _fileStream.Write(source[..sz]);
                _fileStream.Flush();
            }
            else
            {
                byte[] sectorData = new byte[sz];
                source[..sz].CopyTo(sectorData);
                _memorySectors[lba] = sectorData;
            }

            IsBlank = false;
            if (lba + 1 > UsedSectors)
            {
                UsedSectors = lba + 1;
            }
        }
    }

    /// <summary>
    /// Writes a single sector using a byte array.
    /// </summary>
    public void WriteSector(int lba, byte[] source, int? sectorSize = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        WriteSector(lba, source.AsSpan(), sectorSize);
    }

    /// <summary>
    /// Writes contiguous sectors starting at <paramref name="startLba"/>.
    /// </summary>
    public void WriteSectors(int startLba, ReadOnlySpan<byte> source, int? sectorSize = null)
    {
        int sz = sectorSize ?? SectorSize;
        int sectorCount = source.Length / sz;
        if (sectorCount == 0 && source.Length > 0)
        {
            throw new ArgumentException($"Source buffer length ({source.Length}) is smaller than single sector ({sz}).", nameof(source));
        }

        for (int i = 0; i < sectorCount; i++)
        {
            WriteSector(startLba + i, source.Slice(i * sz, sz), sz);
        }
    }

    #endregion

    #region Erase / Blanking

    /// <summary>
    /// Erases rewritable media (CD-RW, DVD-RW, DVD+RW, DVD-RAM).
    /// Quick erase resets the table of contents and marks the disc blank; full erase zeroes out all written sectors.
    /// </summary>
    public void Erase(bool fullErase)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsRewritable)
        {
            throw new InvalidOperationException($"Cannot erase non-rewritable media type '{MediaType.GetDisplayName()}'.");
        }

        lock (_syncRoot)
        {
            if (_fileStream != null)
            {
                if (fullErase)
                {
                    long currentLen = _fileStream.Length;
                    _fileStream.Seek(0, SeekOrigin.Begin);
                    byte[] zeroes = new byte[65536];
                    long written = 0;
                    while (written < currentLen)
                    {
                        int toWrite = (int)Math.Min(zeroes.Length, currentLen - written);
                        _fileStream.Write(zeroes, 0, toWrite);
                        written += toWrite;
                    }
                    _fileStream.Flush();
                }

                _fileStream.SetLength(0);
                _fileStream.Flush();
            }

            _memorySectors.Clear();
            Sessions.Clear();
            CdTextBytes = null;
            UsedSectors = 0;
            IsBlank = true;
            IsFinalized = false;
        }
    }

    #endregion

    #region Disposal

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                lock (_syncRoot)
                {
                    if (_fileStream != null)
                    {
                        try
                        {
                            _fileStream.Flush();
                            _fileStream.Dispose();
                        }
                        catch
                        {
                            // ignore cleanup exceptions
                        }
                        _fileStream = null;
                    }

                    if (_deleteBackingFileOnDispose && !string.IsNullOrEmpty(BackingFilePath) && File.Exists(BackingFilePath))
                    {
                        try
                        {
                            File.Delete(BackingFilePath);
                        }
                        catch
                        {
                            // ignore temp file delete failures
                        }
                    }
                }
            }

            _disposed = true;
        }
    }

    #endregion
}
