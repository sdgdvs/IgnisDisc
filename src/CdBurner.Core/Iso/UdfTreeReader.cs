using System.Buffers.Binary;
using System.Text;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Iso;

/// <summary>
/// Reads the UDF file tree of a disc image (UDF 1.02 to 2.60, including the 2.50 metadata partition), giving every
/// file its full name. ISO 9660 alone has 8.3 names and Joliet stops at 64 characters, so a disc burned as ISO + UDF
/// only has its real names here.
/// <para>
/// Made for damaged images: it tries both anchors, the main and the reserve descriptor sequence, and the metadata
/// file's mirror, and skips an entry it cannot read instead of giving up. Files are reported by their first sector;
/// only files stored in one contiguous run (as IgnisDisc writes them) are listed.
/// </para>
/// </summary>
public sealed class UdfTreeReader
{
    private const int B = OpticalConstants.DataSectorSize;
    private readonly Stream _image;
    private readonly long _sectors;
    private int _partitionStart;
    private List<(long Start, long Length)>? _metadataExtents;

    private UdfTreeReader(Stream image)
    {
        _image = image;
        _sectors = image.Length / B;
    }

    /// <summary>Entries that could not be read (damaged or not contiguous), as "path: reason".</summary>
    public List<string> Skipped { get; } = new();

    /// <summary>
    /// Reads the tree, or returns null when no usable UDF volume is found. Never throws for damaged data.
    /// </summary>
    public static IsoDirectoryEntry? TryRead(Stream image, List<string>? skipped = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        var reader = new UdfTreeReader(image);
        try
        {
            var root = reader.Read();
            skipped?.AddRange(reader.Skipped);
            return root;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    private IsoDirectoryEntry? Read()
    {
        foreach (long anchor in new[] { 256L, _sectors - 1, _sectors - 257 })
        {
            if (anchor < 0 || anchor >= _sectors || !TryTag(ReadSector(anchor), (uint)anchor, 2, out var avdp))
            {
                continue;
            }

            // Main sequence first, then the reserve copy.
            foreach (int field in new[] { 16, 24 })
            {
                uint vds = BinaryPrimitives.ReadUInt32LittleEndian(avdp.AsSpan(field + 4));
                var root = TryVolume(vds);
                if (root != null)
                {
                    return root;
                }
            }
        }
        return null;
    }

    private IsoDirectoryEntry? TryVolume(uint vds)
    {
        int partitionStart = -1;
        byte[]? lvd = null;
        for (uint i = 0; i < 64; i++)
        {
            if (!TryTag(ReadSector(vds + i), vds + i, null, out var d))
            {
                return null;
            }
            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(d);
            if (id == 8) break;                                                                   // terminator
            if (id == 5) partitionStart = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(188));
            if (id == 6) lvd = d;
        }
        if (partitionStart < 0 || lvd == null)
        {
            return null;
        }

        _partitionStart = partitionStart;
        _metadataExtents = null;

        // Partition maps: type 1 (physical) and, for UDF 2.50+, type 2 "*UDF Metadata Partition".
        int mapCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(lvd.AsSpan(268));
        int offset = 440;
        for (int m = 0; m < mapCount && offset + 2 <= lvd.Length; m++)
        {
            int length = lvd[offset + 1];
            if (lvd[offset] == 2 && length >= 64 &&
                Encoding.ASCII.GetString(lvd, offset + 5, 23) == "*UDF Metadata Partition")
            {
                uint file = BinaryPrimitives.ReadUInt32LittleEndian(lvd.AsSpan(offset + 40));
                uint mirror = BinaryPrimitives.ReadUInt32LittleEndian(lvd.AsSpan(offset + 44));
                _metadataExtents = TryExtents(new Address(file, 0)) ?? TryExtents(new Address(mirror, 0));
                if (_metadataExtents == null)
                {
                    return null;
                }
            }
            offset += Math.Max(length, 1);
        }

        var fsdAt = new Address(BinaryPrimitives.ReadUInt32LittleEndian(lvd.AsSpan(252)), BinaryPrimitives.ReadUInt16LittleEndian(lvd.AsSpan(256)));
        if (!TryBlock(fsdAt, 256, out var fsd))
        {
            return null;
        }

        var rootAt = new Address(BinaryPrimitives.ReadUInt32LittleEndian(fsd.AsSpan(404)), BinaryPrimitives.ReadUInt16LittleEndian(fsd.AsSpan(408)));
        var root = new IsoDirectoryEntry { Name = string.Empty, VirtualPath = "/", IsDirectory = true };
        if (!ReadDirectory(rootAt, root, "", depth: 0))
        {
            return null;
        }
        return root;
    }

    private readonly record struct Address(uint Lbn, ushort Ref);

    private bool ReadDirectory(Address icb, IsoDirectoryEntry into, string path, int depth)
    {
        if (depth > 64 || !TryEntry(icb, out var entry) || entry.FileType != 4)
        {
            Skipped.Add($"{(path == "" ? "/" : path)}: directory unreadable");
            return false;
        }

        byte[] stream = ReadExtents(entry.Extents, entry.Length);
        int offset = 0;
        while (offset + 38 <= stream.Length)
        {
            var f = stream.AsSpan(offset);
            if (BinaryPrimitives.ReadUInt16LittleEndian(f) != 257)
            {
                Skipped.Add($"{(path == "" ? "/" : path)}: damaged directory record");
                break;
            }
            byte characteristics = f[18];
            int nameLength = f[19];
            int iuLength = BinaryPrimitives.ReadUInt16LittleEndian(f[36..]);
            int recordLength = (38 + iuLength + nameLength + 3) & ~3;
            if (offset + 38 + iuLength + nameLength > stream.Length)
            {
                break;
            }
            var child = new Address(BinaryPrimitives.ReadUInt32LittleEndian(f[24..]), BinaryPrimitives.ReadUInt16LittleEndian(f[28..]));
            string? name = DecodeName(f.Slice(38 + iuLength, nameLength));
            offset += recordLength;

            if ((characteristics & 0x08) != 0 || (characteristics & 0x04) != 0 || string.IsNullOrEmpty(name))
            {
                continue; // parent entry, deleted entry, or a name we cannot decode
            }

            string childPath = path + "/" + name;
            if ((characteristics & 0x02) != 0)
            {
                var directory = new IsoDirectoryEntry { Name = name, VirtualPath = childPath, IsDirectory = true };
                if (ReadDirectory(child, directory, childPath, depth + 1))
                {
                    into.Children.Add(directory);
                }
                continue;
            }

            if (!TryEntry(child, out var file) || file.FileType != 5)
            {
                Skipped.Add($"{childPath}: file entry unreadable");
                continue;
            }

            long start = file.Extents.Count > 0 ? file.Extents[0].Start : 0;
            bool contiguous = true;
            for (int i = 1; i < file.Extents.Count; i++)
            {
                var (prevStart, prevLength) = file.Extents[i - 1];
                contiguous &= file.Extents[i].Start == prevStart + ((prevLength + B - 1) / B) * B;
            }
            if (!contiguous)
            {
                Skipped.Add($"{childPath}: stored in pieces");
                continue;
            }

            into.Children.Add(new IsoFileEntry
            {
                Name = name,
                VirtualPath = childPath,
                StartLba = (int)(start / B),
                SizeBytes = file.Length,
                IsDirectory = false
            });
        }
        return true;
    }

    private readonly record struct Entry(byte FileType, long Length, List<(long Start, long Length)> Extents);

    /// <summary>File Entry (261) or Extended File Entry (266): type, length and data extents as absolute bytes.</summary>
    private bool TryEntry(Address at, out Entry entry)
    {
        entry = default;
        if (!TryBlock(at, null, out var d))
        {
            return false;
        }
        ushort id = BinaryPrimitives.ReadUInt16LittleEndian(d);
        if (id is not (261 or 266))
        {
            return false;
        }

        bool extended = id == 266;
        int adType = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(34)) & 7;
        long length = (long)BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(56));
        uint eaLength = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(extended ? 208 : 168));
        uint adLength = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(extended ? 212 : 172));
        int pos = (extended ? 216 : 176) + (int)eaLength;
        if (adType is not (0 or 1) || pos + adLength > B || length < 0)
        {
            return false;
        }

        int adSize = adType == 0 ? 8 : 16;
        var extents = new List<(long, long)>();
        for (int i = 0; i < adLength / adSize; i++, pos += adSize)
        {
            uint raw = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(pos));
            uint extentLength = raw & 0x3FFFFFFF;
            if (extentLength == 0) break;
            uint lbn = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(pos + 4));
            ushort reference = adType == 0 ? at.Ref : BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(pos + 8));
            if (!TryByteOffset(new Address(lbn, reference), out long start))
            {
                return false;
            }
            extents.Add((start, extentLength));
        }

        entry = new Entry(d[27], length, extents);
        return true;
    }

    private List<(long Start, long Length)>? TryExtents(Address at) =>
        TryEntry(at, out var entry) && entry.FileType is 250 or 251 ? entry.Extents : null;

    private bool TryByteOffset(Address a, out long offset)
    {
        offset = 0;
        if (a.Ref == 0)
        {
            offset = (_partitionStart + (long)a.Lbn) * B;
            return offset < _image.Length;
        }
        if (a.Ref != 1 || _metadataExtents == null)
        {
            return false;
        }

        long within = (long)a.Lbn * B;
        foreach (var (start, length) in _metadataExtents)
        {
            if (within < length)
            {
                offset = start + within;
                return true;
            }
            within -= length;
        }
        return false;
    }

    private bool TryBlock(Address a, ushort? expectedId, out byte[] block)
    {
        block = Array.Empty<byte>();
        return TryByteOffset(a, out long offset) && TryTag(ReadAt(offset), a.Lbn, expectedId, out block);
    }

    /// <summary>A descriptor whose tag checksum, identifier and location are right (ECMA-167 3/7.2).</summary>
    private static bool TryTag(byte[] d, uint location, ushort? expectedId, out byte[] descriptor)
    {
        descriptor = d;
        if (d.Length < 16) return false;
        int sum = 0;
        for (int i = 0; i < 16; i++) if (i != 4) sum += d[i];
        ushort id = BinaryPrimitives.ReadUInt16LittleEndian(d);
        return (byte)sum == d[4] && id != 0 && (expectedId == null || id == expectedId)
               && BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(12)) == location;
    }

    private byte[] ReadExtents(List<(long Start, long Length)> extents, long length)
    {
        byte[] data = new byte[Math.Min(length, 64L * 1024 * 1024)];
        long copied = 0;
        foreach (var (start, extentLength) in extents)
        {
            int n = (int)Math.Min(extentLength, data.Length - copied);
            if (n <= 0) break;
            _image.Position = start;
            _image.ReadAtLeast(data.AsSpan((int)copied, n), n, throwOnEndOfStream: false);
            copied += n;
        }
        return data;
    }

    private byte[] ReadSector(long lba) => ReadAt(lba * B);

    private byte[] ReadAt(long offset)
    {
        byte[] sector = new byte[B];
        if (offset < 0 || offset + B > _image.Length) return Array.Empty<byte>();
        _image.Position = offset;
        _image.ReadExactly(sector);
        return sector;
    }

    private static string? DecodeName(ReadOnlySpan<byte> d)
    {
        if (d.Length < 2) return null;
        return d[0] switch
        {
            8 => Encoding.Latin1.GetString(d[1..]),
            16 => Encoding.BigEndianUnicode.GetString(d[1..]),
            _ => null
        };
    }
}
