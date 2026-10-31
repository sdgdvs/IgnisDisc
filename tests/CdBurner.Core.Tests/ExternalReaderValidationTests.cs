using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using DiscUtils;
using DiscUtils.Iso9660;
using DiscUtils.Udf;

namespace CdBurner.Core.Tests;

/// <summary>
/// Validates generated images with an independent implementation (DiscUtils) instead of this project's own
/// IsoImageReader, so the builder cannot pass by agreeing with itself. Each test reads every file back through
/// ISO 9660, Joliet and UDF and compares contents byte-for-byte with the source files.
/// </summary>
public class ExternalReaderValidationTests : IDisposable
{
    private readonly string _sourceDir;

    public ExternalReaderValidationTests()
    {
        _sourceDir = Path.Combine(Path.GetTempPath(), "ignisdisc_extval_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sourceDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sourceDir, recursive: true); } catch { }
    }

    private void WriteSourceFile(string relativePath, byte[] content)
    {
        string full = Path.Combine(_sourceDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
    }

    private static byte[] Bytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private MemoryStream BuildImage(IsoBuildOptions options)
    {
        var compilation = new DataCompilation(options.VolumeLabel);
        foreach (string file in Directory.GetFiles(_sourceDir, "*", SearchOption.AllDirectories))
        {
            string virtualPath = "/" + Path.GetRelativePath(_sourceDir, file).Replace(Path.DirectorySeparatorChar, '/');
            compilation.AddFile(virtualPath, file, new FileInfo(file).Length);
        }
        return IsoUdfImageBuilder.BuildToMemoryStream(compilation.Root, options);
    }

    private void AssertAllFilesReadable(DiscFileSystem fs, bool expectFullNames)
    {
        var sourceFiles = Directory.GetFiles(_sourceDir, "*", SearchOption.AllDirectories);
        var discFiles = fs.GetFiles("", "*.*", SearchOption.AllDirectories).ToList();
        Assert.Equal(sourceFiles.Length, discFiles.Count);

        if (!expectFullNames)
        {
            return;
        }

        foreach (string src in sourceFiles)
        {
            string rel = Path.GetRelativePath(_sourceDir, src).Replace(Path.DirectorySeparatorChar, '\\');
            Assert.True(fs.FileExists(rel), $"Missing on disc: {rel}");
            using var s = fs.OpenFile(rel, FileMode.Open);
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            Assert.Equal(File.ReadAllBytes(src), ms.ToArray());
        }
    }

    private void CreateMixedTree()
    {
        WriteSourceFile("readme.txt", "hello"u8.ToArray());
        WriteSourceFile("empty.bin", Array.Empty<byte>());
        WriteSourceFile(Path.Combine("sub", "data.bin"), Bytes(300_000, 1));
        WriteSourceFile(Path.Combine("sub", "deeper", "A long file name with spaces.txt"), Bytes(5000, 2));
        WriteSourceFile(Path.Combine("Ünïcödé 日本語", "音楽 track.dat"), Bytes(4097, 3));
        WriteSourceFile(Path.Combine("VIDEO_TS", "VIDEO_TS.IFO"), Bytes(12_288, 4));
        WriteSourceFile(Path.Combine("VIDEO_TS", "VIDEO_TS.BUP"), Bytes(12_288, 4));
    }

    [Fact]
    public void UdfBridgeImage_IsReadableByIndependentUdfReader()
    {
        CreateMixedTree();
        using var image = BuildImage(new IsoBuildOptions(VolumeLabel: "EXTVAL", IncludeUdf: true, IncludeJoliet: true));

        Assert.True(UdfReader.Detect(image));
        image.Position = 0;
        using var udf = new UdfReader(image);
        AssertAllFilesReadable(udf, expectFullNames: true);
    }

    [Fact]
    public void UdfBridgeImage_JolietAndIso9660_AreReadableByIndependentReader()
    {
        CreateMixedTree();
        using var image = BuildImage(new IsoBuildOptions(VolumeLabel: "EXTVAL", IncludeUdf: true, IncludeJoliet: true));

        using (var joliet = new CDReader(image, joliet: true))
        {
            AssertAllFilesReadable(joliet, expectFullNames: true);
        }

        image.Position = 0;
        using var iso = new CDReader(image, joliet: false);
        AssertAllFilesReadable(iso, expectFullNames: false);
    }

    [Fact]
    public void LargeDirectoryTree_PushingIsoMetadataPastLba256_StillProducesValidUdf()
    {
        for (int d = 0; d < 300; d++)
        {
            for (int f = 0; f < 3; f++)
            {
                WriteSourceFile(Path.Combine($"dir{d:D3}", $"file{f}.txt"), Bytes(100 + f, d * 10 + f));
            }
        }

        using var image = BuildImage(new IsoBuildOptions(VolumeLabel: "MANYDIRS", IncludeUdf: true));

        using (var udf = new UdfReader(image))
        {
            AssertAllFilesReadable(udf, expectFullNames: true);
        }

        image.Position = 0;
        using var joliet = new CDReader(image, joliet: true);
        AssertAllFilesReadable(joliet, expectFullNames: true);
    }

    [Fact]
    public void JolietNames_TruncatedTo64Chars_AreDisambiguated()
    {
        string common = new string('A', 70);
        WriteSourceFile(common + "_one.txt", "one"u8.ToArray());
        WriteSourceFile(common + "_two.txt", "two"u8.ToArray());

        using var image = BuildImage(new IsoBuildOptions(VolumeLabel: "LONGNAMES", IncludeUdf: false, IncludeJoliet: true));
        using var joliet = new CDReader(image, joliet: true);

        var names = joliet.GetFiles("", "*.*", SearchOption.AllDirectories).ToList();
        Assert.Equal(2, names.Count);
        Assert.Equal(2, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void FileLargerThan4GiB_IsDescribedCorrectlyInUdf()
    {
        const long size = 4L * 1024 * 1024 * 1024 + 123_456; // 4 GiB + change
        var root = CompilationDirectory.CreateRoot();
        root.AddFile(new CompilationFile("huge.bin", size, () => new ZeroStream(size)));
        root.AddFile("small.txt", "after the big one"u8.ToArray());

        using var image = new SparseStream();
        IsoUdfImageBuilder.Build(root, image, new IsoBuildOptions(VolumeLabel: "HUGE", IncludeUdf: true));

        image.Position = 0;
        using var udf = new UdfReader(image);
        Assert.Equal(size, udf.GetFileLength("huge.bin"));
        using var small = udf.OpenFile("small.txt", FileMode.Open);
        using var ms = new MemoryStream();
        small.CopyTo(ms);
        Assert.Equal("after the big one"u8.ToArray(), ms.ToArray());
    }

    [Fact]
    public void FileLargerThan4GiB_WithoutUdf_IsRejectedWithClearError()
    {
        const long size = 4L * 1024 * 1024 * 1024 + 1;
        var root = CompilationDirectory.CreateRoot();
        root.AddFile(new CompilationFile("huge.bin", size, () => new ZeroStream(size)));

        var ex = Assert.Throws<NotSupportedException>(() =>
            IsoUdfImageBuilder.Build(root, Stream.Null, new IsoBuildOptions(IncludeUdf: false)));
        Assert.Contains("UDF", ex.Message);
    }

    /// <summary>Read-only stream of zeros with a fixed length (stands in for a huge source file).</summary>
    private sealed class ZeroStream(long length) : Stream
    {
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => _position = value; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = (int)Math.Min(count, length - _position);
            if (n <= 0) return 0;
            Array.Clear(buffer, offset, n);
            _position += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => length + offset
        };
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Seekable in-memory stream that only stores non-zero 2 KiB blocks, so multi-GiB images fit in RAM.</summary>
    private sealed class SparseStream : Stream
    {
        private const int Block = 2048;
        private readonly Dictionary<long, byte[]> _blocks = new();
        private long _length;
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get => _position; set => _position = value; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                long blockIndex = _position / Block;
                int inBlock = (int)(_position % Block);
                int n = Math.Min(count, Block - inBlock);
                var span = buffer.AsSpan(offset, n);
                if (_blocks.TryGetValue(blockIndex, out var existing))
                {
                    span.CopyTo(existing.AsSpan(inBlock));
                }
                else if (span.ContainsAnyExcept((byte)0))
                {
                    var block = new byte[Block];
                    span.CopyTo(block.AsSpan(inBlock));
                    _blocks[blockIndex] = block;
                }
                _position += n;
                offset += n;
                count -= n;
                _length = Math.Max(_length, _position);
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int total = 0;
            count = (int)Math.Min(count, Math.Max(0, _length - _position));
            while (count > 0)
            {
                long blockIndex = _position / Block;
                int inBlock = (int)(_position % Block);
                int n = Math.Min(count, Block - inBlock);
                if (_blocks.TryGetValue(blockIndex, out var block))
                {
                    block.AsSpan(inBlock, n).CopyTo(buffer.AsSpan(offset, n));
                }
                else
                {
                    Array.Clear(buffer, offset, n);
                }
                _position += n;
                offset += n;
                count -= n;
                total += n;
            }
            return total;
        }

        public override long Seek(long offset, SeekOrigin origin) => _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset
        };
        public override void Flush() { }
        public override void SetLength(long value) => _length = value;
    }
}
