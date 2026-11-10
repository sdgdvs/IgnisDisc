using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace CdBurner.Core.Tests;

/// <summary>
/// A strict, test-only UDF reader (ECMA-167 / OSTA UDF 1.02-2.60). It starts from the Anchor Volume Descriptor
/// Pointers like a real driver, checks every descriptor tag it touches (identifier, version, checksum, CRC and
/// recorded location), resolves the Type 1 and Type 2 (metadata) partition maps, and reads every directory and file.
/// It never looks at the ISO 9660 side, so it proves the UDF tree stands on its own.
/// </summary>
internal sealed class UdfTestWalker
{
    private const int B = 2048;
    private readonly byte[] _image;

    public ushort Revision { get; private set; }
    public string NsrIdentifier { get; private set; } = "";
    public ushort TagVersion { get; private set; }
    public int PartitionStart { get; private set; }
    public int PartitionMapCount { get; private set; }
    public bool UsedExtendedFileEntries { get; private set; }
    public bool UsedPlainFileEntries { get; private set; }
    public int FsdPartitionReference { get; private set; }
    public byte[]? MetadataFileData { get; private set; }
    public byte[]? MetadataMirrorData { get; private set; }
    public (ushort MinRead, ushort MinWrite, ushort MaxWrite) LvidRevisions { get; private set; }
    public SortedDictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
    public SortedSet<string> Directories { get; } = new(StringComparer.Ordinal);

    private List<(long Start, long Length)>? _metadataExtents; // physical-partition byte extents of the metadata file

    public UdfTestWalker(byte[] image)
    {
        _image = image;
        Walk();
    }

    private Span<byte> Sector(long lba) => _image.AsSpan(checked((int)(lba * B)), B);

    /// <summary>Checks a descriptor tag and returns its identifier.</summary>
    private ushort CheckTag(ReadOnlySpan<byte> d, uint expectedLocation, string what)
    {
        ushort id = BinaryPrimitives.ReadUInt16LittleEndian(d);
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(d[2..]);
        byte sum = 0;
        for (int i = 0; i < 16; i++) if (i != 4) sum += d[i];
        Assert.True(sum == d[4], $"{what}: tag checksum");
        ushort crcLength = BinaryPrimitives.ReadUInt16LittleEndian(d[10..]);
        Assert.True(16 + crcLength <= d.Length, $"{what}: CRC length {crcLength} runs past the descriptor");
        Assert.True(Crc(d.Slice(16, crcLength)) == BinaryPrimitives.ReadUInt16LittleEndian(d[8..]), $"{what}: descriptor CRC");
        Assert.True(BinaryPrimitives.ReadUInt32LittleEndian(d[12..]) == expectedLocation,
            $"{what}: tag location {BinaryPrimitives.ReadUInt32LittleEndian(d[12..])}, expected {expectedLocation}");
        if (TagVersion != 0) Assert.True(version == TagVersion, $"{what}: descriptor version {version}, expected {TagVersion}");
        return id;
    }

    private static ushort Crc(ReadOnlySpan<byte> data)
    {
        // CRC-ITU-T (x^16 + x^12 + x^5 + 1), initial 0, as ECMA-167 7.2.6.
        ushort crc = 0;
        foreach (byte b in data)
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++) crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
        }
        return crc;
    }

    private void Walk()
    {
        long sectors = _image.Length / B;

        // Volume Recognition Sequence
        for (int lba = 16; lba < 32 && lba < sectors; lba++)
        {
            string id = Encoding.ASCII.GetString(_image, lba * B + 1, 5);
            if (id is "NSR02" or "NSR03") NsrIdentifier = id;
        }
        Assert.NotEqual("", NsrIdentifier);
        TagVersion = NsrIdentifier == "NSR03" ? (ushort)3 : (ushort)2;

        // Anchors at 256 and at the last sector, both pointing at the same sequence.
        uint vds = 0;
        foreach (long anchor in new[] { 256L, sectors - 1 })
        {
            var a = Sector(anchor);
            Assert.Equal(2, CheckTag(a, (uint)anchor, $"AVDP@{anchor}"));
            uint location = BinaryPrimitives.ReadUInt32LittleEndian(a[20..]);
            Assert.True(vds == 0 || vds == location, "anchors disagree");
            vds = location;
        }

        uint partitionLength = 0, integrityLocation = 0, fsdLbn = 0;
        ushort fsdRef = 0;
        var maps = new List<byte[]>();
        for (uint lba = vds; ; lba++)
        {
            var d = Sector(lba);
            ushort id = CheckTag(d, lba, $"VDS@{lba}");
            if (id == 8) break;
            switch (id)
            {
                case 5: // Partition Descriptor
                    NsrPartitionContents = Encoding.ASCII.GetString(d.Slice(25, 6));
                    PartitionStart = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[188..]);
                    partitionLength = BinaryPrimitives.ReadUInt32LittleEndian(d[192..]);
                    break;
                case 6: // Logical Volume Descriptor
                    Assert.Equal("*OSTA UDF Compliant", Encoding.ASCII.GetString(d.Slice(217, 19)));
                    Revision = BinaryPrimitives.ReadUInt16LittleEndian(d[(216 + 24)..]);
                    fsdLbn = BinaryPrimitives.ReadUInt32LittleEndian(d[252..]);
                    fsdRef = BinaryPrimitives.ReadUInt16LittleEndian(d[256..]);
                    integrityLocation = BinaryPrimitives.ReadUInt32LittleEndian(d[436..]);
                    PartitionMapCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(d[268..]);
                    int offset = 440;
                    for (int i = 0; i < PartitionMapCount; i++)
                    {
                        int length = d[offset + 1];
                        maps.Add(d.Slice(offset, length).ToArray());
                        offset += length;
                    }
                    Assert.Equal((int)BinaryPrimitives.ReadUInt32LittleEndian(d[264..]), offset - 440);
                    break;
            }
        }
        Assert.Equal(NsrIdentifier == "NSR03" ? "+NSR03" : "+NSR02", NsrPartitionContents);
        Assert.True(PartitionStart + partitionLength <= sectors, "partition runs past the image");

        // Integrity: closed, with the revisions.
        var lvid = Sector(integrityLocation);
        Assert.Equal(9, CheckTag(lvid, integrityLocation, "LVID"));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(lvid[28..])); // close
        int partitions = (int)BinaryPrimitives.ReadUInt32LittleEndian(lvid[72..]);
        Assert.Equal(PartitionMapCount, partitions);
        int iu = 80 + 8 * partitions;
        LvidRevisions = (BinaryPrimitives.ReadUInt16LittleEndian(lvid[(iu + 40)..]),
            BinaryPrimitives.ReadUInt16LittleEndian(lvid[(iu + 42)..]),
            BinaryPrimitives.ReadUInt16LittleEndian(lvid[(iu + 44)..]));

        Assert.Equal(1, maps[0][0]);
        if (maps.Count > 1)
        {
            var m = maps[1];
            Assert.Equal(2, m[0]);
            Assert.Equal("*UDF Metadata Partition", Encoding.ASCII.GetString(m, 5, 23));
            uint fileLoc = BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(40));
            uint mirrorLoc = BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(44));
            var (metaExtents, metaData, metaType) = ReadEntry(new Address(fileLoc, 0));
            var (_, mirrorData, mirrorType) = ReadEntry(new Address(mirrorLoc, 0));
            Assert.Equal(250, metaType);
            Assert.Equal(251, mirrorType);
            _metadataExtents = metaExtents;
            MetadataFileData = metaData;
            MetadataMirrorData = mirrorData;
            // A driver falls back to the mirror when the main copy is unreadable, so both must be the same.
            Assert.True(metaData.AsSpan().SequenceEqual(mirrorData), "Metadata Mirror File differs from the Metadata File");
        }

        FsdPartitionReference = fsdRef;
        var fsd = Block(new Address(fsdLbn, fsdRef));
        Assert.Equal(256, CheckTag(fsd, fsdLbn, "FSD"));
        var root = new Address(BinaryPrimitives.ReadUInt32LittleEndian(fsd[404..]), BinaryPrimitives.ReadUInt16LittleEndian(fsd[408..]));
        WalkDirectory(root, "");
    }

    public string NsrPartitionContents { get; private set; } = "";

    private readonly record struct Address(uint Lbn, ushort Ref);

    /// <summary>Absolute byte offset of a block in partition reference 0 (physical) or 1 (metadata).</summary>
    private long ByteOffset(Address a)
    {
        if (a.Ref == 0) return (PartitionStart + (long)a.Lbn) * B;
        Assert.True(a.Ref == 1 && _metadataExtents != null, $"unknown partition reference {a.Ref}");
        long within = (long)a.Lbn * B;
        foreach (var (start, length) in _metadataExtents!)
        {
            if (within < length) return start + within;
            within -= length;
        }
        throw new InvalidDataException($"metadata block {a.Lbn} is outside the metadata file");
    }

    private Span<byte> Block(Address a) => _image.AsSpan(checked((int)ByteOffset(a)), B);

    /// <summary>Reads a File Entry or Extended File Entry: its data extents (absolute bytes), data and file type.</summary>
    private (List<(long Start, long Length)> Extents, byte[] Data, byte FileType) ReadEntry(Address at)
    {
        var d = Block(at);
        ushort id = CheckTag(d, at.Lbn, $"FE@{at}");
        bool extended = id == 266;
        Assert.True(id is 261 or 266, $"FE@{at}: tag {id}");
        if (extended) UsedExtendedFileEntries = true; else UsedPlainFileEntries = true;

        byte fileType = d[27];
        int adType = BinaryPrimitives.ReadUInt16LittleEndian(d[34..]) & 7;
        ulong infoLength = BinaryPrimitives.ReadUInt64LittleEndian(d[56..]);
        int fixedSize = extended ? 216 : 176;
        uint eaLength = BinaryPrimitives.ReadUInt32LittleEndian(d[(extended ? 208 : 168)..]);
        uint adLength = BinaryPrimitives.ReadUInt32LittleEndian(d[(extended ? 212 : 172)..]);
        if (extended) Assert.Equal(infoLength, BinaryPrimitives.ReadUInt64LittleEndian(d[64..])); // object size

        var extents = new List<(long, long)>();
        int pos = fixedSize + (int)eaLength;
        int adSize = adType == 0 ? 8 : 16;
        Assert.True(adType is 0 or 1, $"FE@{at}: allocation type {adType}");
        for (int i = 0; i < adLength / adSize; i++, pos += adSize)
        {
            uint raw = BinaryPrimitives.ReadUInt32LittleEndian(d[pos..]);
            Assert.Equal(0u, raw >> 30); // recorded and allocated
            uint length = raw & 0x3FFFFFFF;
            uint lbn = BinaryPrimitives.ReadUInt32LittleEndian(d[(pos + 4)..]);
            // short_ad: same partition as the entry; long_ad names its partition.
            ushort pref = adType == 0 ? at.Ref : BinaryPrimitives.ReadUInt16LittleEndian(d[(pos + 8)..]);
            extents.Add((ByteOffset(new Address(lbn, pref)), length));
        }

        byte[] data = new byte[infoLength];
        long copied = 0;
        foreach (var (start, length) in extents)
        {
            int n = (int)Math.Min(length, (long)infoLength - copied);
            _image.AsSpan((int)start, n).CopyTo(data.AsSpan((int)copied));
            copied += n;
        }
        Assert.Equal((long)infoLength, copied);
        return (extents, data, fileType);
    }

    private void WalkDirectory(Address icb, string path)
    {
        var (extents, stream, fileType) = ReadEntry(icb);
        Assert.Equal(4, fileType);
        Directories.Add(path == "" ? "/" : path);
        Assert.NotEmpty(extents);
        long streamStart = extents[0].Start;
        // FID tag location: the block number, in the stream's partition, of the block where the FID starts.
        uint streamLbn = BlockNumberOf(streamStart, icb.Ref);

        int offset = 0;
        bool first = true;
        while (offset < stream.Length)
        {
            var f = stream.AsSpan(offset);
            Assert.Equal(257, CheckTag(f, streamLbn + (uint)(offset / B), $"FID {path}+{offset}"));
            byte characteristics = f[18];
            int nameLength = f[19];
            var child = new Address(BinaryPrimitives.ReadUInt32LittleEndian(f[24..]), BinaryPrimitives.ReadUInt16LittleEndian(f[28..]));
            int iuLength = BinaryPrimitives.ReadUInt16LittleEndian(f[36..]);
            string name = DecodeName(f.Slice(38 + iuLength, nameLength));
            offset += (38 + iuLength + nameLength + 3) & ~3;

            if ((characteristics & 0x08) != 0)
            {
                Assert.True(first, "parent entry must come first");
            }
            else if ((characteristics & 0x02) != 0)
            {
                WalkDirectory(child, path + "/" + name);
            }
            else
            {
                var (_, data, type) = ReadEntry(child);
                Assert.Equal(5, type);
                Files.Add(path + "/" + name, data);
            }
            first = false;
        }
    }

    private uint BlockNumberOf(long absoluteByte, ushort partitionReference)
    {
        if (partitionReference == 0) return (uint)(absoluteByte / B - PartitionStart);
        long before = 0;
        foreach (var (start, length) in _metadataExtents!)
        {
            if (absoluteByte >= start && absoluteByte < start + length) return (uint)((before + absoluteByte - start) / B);
            before += length;
        }
        throw new InvalidDataException("directory stream outside the metadata file");
    }

    private static string DecodeName(ReadOnlySpan<byte> d)
    {
        if (d.Length == 0) return "";
        return d[0] switch
        {
            8 => Encoding.Latin1.GetString(d[1..]),
            16 => Encoding.BigEndianUnicode.GetString(d[1..]),
            _ => throw new InvalidDataException($"compression id {d[0]}")
        };
    }
}
