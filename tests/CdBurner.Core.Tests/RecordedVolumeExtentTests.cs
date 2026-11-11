using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;

namespace CdBurner.Core.Tests;

/// <summary>
/// The size of what was burned, read from the disc's own volume descriptors. A rewritable disc that is overwritten
/// in place has no end-of-recording mark, so a wrong answer here cuts sectors off a rip or a copy.
/// </summary>
public class RecordedVolumeExtentTests : IDisposable
{
    private const int SectorSize = 2048;
    private readonly string _sourceDir = Path.Combine(Path.GetTempPath(), "ignisdisc_extent_" + Guid.NewGuid().ToString("N"));

    public RecordedVolumeExtentTests()
    {
        Directory.CreateDirectory(Path.Combine(_sourceDir, "folder"));
        File.WriteAllBytes(Path.Combine(_sourceDir, "a.bin"), Bytes(300_000, 1));
        File.WriteAllBytes(Path.Combine(_sourceDir, "folder", "b.bin"), Bytes(70_001, 2));
    }

    public void Dispose()
    {
        try { Directory.Delete(_sourceDir, recursive: true); } catch { }
    }

    private static byte[] Bytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private byte[] BuildImage(IsoBuildOptions options)
    {
        var compilation = new DataCompilation(options.VolumeLabel);
        foreach (string file in Directory.GetFiles(_sourceDir, "*", SearchOption.AllDirectories))
        {
            string virtualPath = "/" + Path.GetRelativePath(_sourceDir, file).Replace(Path.DirectorySeparatorChar, '/');
            compilation.AddFile(virtualPath, file, new FileInfo(file).Length);
        }
        using var stream = IsoUdfImageBuilder.BuildToMemoryStream(compilation.Root, options);
        return stream.ToArray();
    }

    /// <summary>
    /// The image as a tool that writes UDF alone would leave it: the builder always adds ISO 9660 descriptors, whose
    /// volume size would answer the question on its own, so they are wiped here.
    /// </summary>
    private byte[] BuildPureUdfImage(string label)
    {
        byte[] image = BuildImage(new IsoBuildOptions(VolumeLabel: label, IncludeIso9660: false, IncludeJoliet: false));
        for (int lba = 16; lba < 32; lba++)
        {
            var sector = image.AsSpan(lba * SectorSize, SectorSize);
            if (sector.Slice(1, 5).SequenceEqual("CD001"u8))
            {
                sector.Clear();
            }
        }
        return image;
    }

    /// <summary>A disc holding <paramref name="content"/> from sector 0; sectors past it cannot be read.</summary>
    private static Func<int, byte[]?> Disc(byte[] content) => lba =>
        lba < 0 || (long)(lba + 1) * SectorSize > content.Length ? null : content.AsSpan(lba * SectorSize, SectorSize).ToArray();

    [Theory]
    [InlineData(true, true, true, 0x0102)]
    [InlineData(true, false, true, 0x0102)]
    [InlineData(false, false, true, 0x0102)]
    [InlineData(false, false, true, 0x0150)]
    [InlineData(false, false, true, 0x0201)]
    [InlineData(false, false, true, 0x0250)]
    [InlineData(false, false, true, 0x0260)]
    [InlineData(true, true, true, 0x0250)]
    [InlineData(true, true, false, 0x0102)]
    [InlineData(true, false, false, 0x0102)]
    public void EndSector_IsTheWholeImage_ForEveryFileSystemCombination(bool iso, bool joliet, bool udf, int udfRevision)
    {
        byte[] image = BuildImage(new IsoBuildOptions(VolumeLabel: "EXTENT", IncludeIso9660: iso, IncludeJoliet: joliet,
            IncludeUdf: udf, UdfRevision: (ushort)udfRevision));

        Assert.Equal(image.Length / SectorSize, RecordedVolumeExtent.FindEndSector(Disc(image)));
    }

    [Theory]
    [InlineData(0x0102)]
    [InlineData(0x0150)]
    [InlineData(0x0200)]
    [InlineData(0x0201)]
    [InlineData(0x0250)]
    [InlineData(0x0260)]
    public void EndSector_IsTheWholeImage_FromTheUdfDescriptorsAlone(int udfRevision)
    {
        byte[] image = BuildImage(new IsoBuildOptions(VolumeLabel: "EXTENT", IncludeIso9660: false, IncludeJoliet: false,
            UdfRevision: (ushort)udfRevision));
        for (int lba = 16; lba < 32; lba++)
        {
            var sector = image.AsSpan(lba * SectorSize, SectorSize);
            if (sector.Slice(1, 5).SequenceEqual("CD001"u8))
            {
                sector.Clear();
            }
        }

        Assert.Equal(image.Length / SectorSize, RecordedVolumeExtent.FindEndSector(Disc(image)));
    }

    [Fact]
    public void UdfOnlyImage_EndsWithAClosingAnchor_ThatTheMountedVolumeSizeLeavesOut()
    {
        byte[] image = BuildPureUdfImage("EXTENT");
        int sectors = image.Length / SectorSize;

        // The last sector is an anchor (tag 2) that carries its own address: without it the count is one short.
        var last = image.AsSpan((sectors - 1) * SectorSize, SectorSize);
        Assert.Equal(2, BitConverter.ToUInt16(last.Slice(0, 2)));
        Assert.Equal((uint)(sectors - 1), BitConverter.ToUInt32(last.Slice(12, 4)));

        byte[] withoutAnchor = image.AsSpan(0, (sectors - 1) * SectorSize).ToArray();
        Assert.Equal(sectors - 1, RecordedVolumeExtent.FindEndSector(Disc(withoutAnchor)));
        Assert.Equal(sectors, RecordedVolumeExtent.FindEndSector(Disc(image)));
    }

    [Fact]
    public void LeftoversOfAnOlderLargerBurn_BehindTheImage_AreNotCounted()
    {
        byte[] small = BuildPureUdfImage("NEW");
        File.WriteAllBytes(Path.Combine(_sourceDir, "c.bin"), Bytes(900_000, 3));
        byte[] large = BuildPureUdfImage("OLD");
        Assert.True(large.Length > small.Length + 100 * SectorSize);

        // A rewritable disc after the small image was burned over the large one.
        byte[] disc = (byte[])large.Clone();
        small.CopyTo(disc, 0);

        Assert.Equal(small.Length / SectorSize, RecordedVolumeExtent.FindEndSector(Disc(disc)));
    }

    [Fact]
    public void DiscWithoutAFileSystem_GivesNoAnswer()
    {
        Assert.Null(RecordedVolumeExtent.FindEndSector(Disc(new byte[600 * SectorSize])));
        Assert.Null(RecordedVolumeExtent.FindEndSector(Disc(Bytes(600 * SectorSize, 9))));
        Assert.Null(RecordedVolumeExtent.FindEndSector(_ => null));
    }

    [Fact]
    public void DamagedAnchor_IsNotBelieved()
    {
        byte[] image = BuildPureUdfImage("EXTENT");
        Assert.NotNull(RecordedVolumeExtent.FindEndSector(Disc(image)));

        image[256 * SectorSize + 5] ^= 0x01; // a tag byte changed: the tag checksum no longer fits
        Assert.Null(RecordedVolumeExtent.FindEndSector(Disc(image)));
    }
}
