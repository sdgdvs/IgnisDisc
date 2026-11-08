using System.Buffers.Binary;
using System.Text;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.Burning;
using CdBurner.Core.Compilation;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Images;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using Xunit;

namespace CdBurner.Core.Tests;

/// <summary>
/// Regression and edge-case test suite targeting historical bugs found across optical burning software
/// (ImgBurn, CDBurnerXP, InfraRecorder, mkisofs/xorriso, Exact Audio Copy, and Windows IMAPIv2).
/// </summary>
public class IndustryBugRegressionTests
{
    [Fact]
    public void CueSheetParser_HandlesUtf8BomOnFirstLine_WithoutDroppingCommand()
    {
        string cueWithBom = "\uFEFFPERFORMER \"The Prodigy\"\r\n" +
                            "TITLE \"The Fat of the Land\"\r\n" +
                            "FILE \"album.wav\" WAVE\r\n" +
                            "  TRACK 01 AUDIO\r\n" +
                            "    TITLE \"Smack My Pitch Up\"\r\n" +
                            "    INDEX 01 00:00:00\r\n";

        var sheet = CueSheetParser.Parse(cueWithBom);

        Assert.Equal("The Prodigy", sheet.Performer);
        Assert.Equal("The Fat of the Land", sheet.Title);
        Assert.Single(sheet.Files);
        Assert.Equal("album.wav", sheet.Files[0].FileName);
    }

    [Fact]
    public async Task CueSheetParser_ParseAsync_HandlesUtf8BomAndUnquotedFileWithSpaces()
    {
        string cueText = "\uFEFFFILE My Live Concert 1999.wav WAVE\n" +
                         "  TRACK 01 AUDIO\n" +
                         "    TITLE \"Opening\"\n" +
                         "    INDEX 00 00:00:00\n" +
                         "    INDEX 01 00:02:00\n";

        using var reader = new StringReader(cueText);
        var sheet = await CueSheetParser.ParseAsync(reader);

        Assert.Single(sheet.Files);
        Assert.Equal("My Live Concert 1999.wav", sheet.Files[0].FileName);
        Assert.Equal("WAVE", sheet.Files[0].FileType);
        Assert.Single(sheet.Files[0].Tracks);
        Assert.Equal(2, sheet.Files[0].Tracks[0].Indices.Count);
        Assert.Equal(150, sheet.Files[0].Tracks[0].Indices[1].Position.ToFrameCount());
    }

    [Fact]
    public void CdTextPackBuilder_FoldsUnicodeSmartPunctuationAndEuropeanDiacritics_WithoutQuestionMarks()
    {
        var metadata = new CdTextAlbumMetadata(
            AlbumTitle: "Don\u2019t Stop \u2014 \u201CDeluxe\u201D\u2026",
            AlbumPerformer: "Anton\u00EDn Dvo\u0159\u00E1k & Lech Wa\u0142\u0119sa",
            Tracks: new[]
            {
                new CdTextTrackMetadata(
                    TrackNumber: 1,
                    Title: "G\u00FCne\u015F & Ya\u011Fmur (\u0160e\u0159\u00EDk)",
                    Performer: "Zolt\u00E1n Kod\u00E1ly")
            });

        byte[] rawPacks = CdTextPackBuilder.BuildPacks(metadata);
        var parsed = CdTextPackParser.ParsePacks(rawPacks);

        Assert.DoesNotContain("?", parsed.AlbumTitle);
        Assert.DoesNotContain("?", parsed.AlbumPerformer);
        Assert.Equal("Don't Stop - \"Deluxe\"...", parsed.AlbumTitle);
        Assert.Equal("Anton\u00EDn Dvor\u00E1k & Lech Walesa", parsed.AlbumPerformer);
        Assert.NotNull(parsed.Tracks);
        Assert.Single(parsed.Tracks);
        Assert.Equal("G\u00FCnes & Yagmur (Ser\u00EDk)", parsed.Tracks[0].Title);
    }

    [Fact]
    public void WavFileCodec_DecodesNonSeekableStreamWithExtensibleFmtJunkAndListChunks()
    {
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            int sampleFrames = 100;
            int dataBytes = sampleFrames * 4;

            bw.Write("RIFF"u8);
            bw.Write((uint)(200 + dataBytes));
            bw.Write("WAVE"u8);

            // 1. Odd-sized JUNK chunk (7 bytes + 1 pad byte)
            bw.Write("JUNK"u8);
            bw.Write((uint)7);
            bw.Write(new byte[] { 1, 2, 3, 4, 5, 6, 7 });
            bw.Write((byte)0); // RIFF word alignment pad

            // 2. 40-byte WAVE_FORMAT_EXTENSIBLE 'fmt ' chunk
            bw.Write("fmt "u8);
            bw.Write((uint)40);
            bw.Write((ushort)0xFFFE); // WAVE_FORMAT_EXTENSIBLE
            bw.Write((ushort)2);      // 2 channels
            bw.Write((uint)44100);    // 44.1 kHz
            bw.Write((uint)(44100 * 4));
            bw.Write((ushort)4);
            bw.Write((ushort)16);     // 16-bit
            bw.Write((ushort)22);     // cbSize
            bw.Write((ushort)16);     // wValidBitsPerSample
            bw.Write((uint)3);        // dwChannelMask (SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT)
            bw.Write(new Guid("00000001-0000-0010-8000-00aa00389b71").ToByteArray()); // SubFormat PCM

            // 3. LIST INFO chunk with odd-sized INAM tag
            bw.Write("LIST"u8);
            byte[] inamValue = "Song1"u8.ToArray(); // 5 bytes (odd)
            uint listSize = (uint)(4 + 8 + inamValue.Length + 1);
            bw.Write(listSize);
            bw.Write("INFO"u8);
            bw.Write("INAM"u8);
            bw.Write((uint)inamValue.Length);
            bw.Write(inamValue);
            bw.Write((byte)0); // pad byte

            // 4. 'data' chunk
            bw.Write("data"u8);
            bw.Write((uint)dataBytes);
            for (int i = 0; i < sampleFrames; i++)
            {
                bw.Write((short)1234);
                bw.Write((short)-1234);
            }
        }

        ms.Position = 0;
        using var nonSeekable = new NonSeekableReadStream(ms, maxBytesPerRead: 19);

        byte[] decoded = WavFileCodec.DecodeToRedBookPcm(nonSeekable);

        Assert.Equal(400, decoded.Length);
        Assert.Equal(1234, BinaryPrimitives.ReadInt16LittleEndian(decoded.AsSpan(0, 2)));
        Assert.Equal(-1234, BinaryPrimitives.ReadInt16LittleEndian(decoded.AsSpan(2, 2)));
    }

    [Fact]
    public void RedBookPcmDspProcessor_MeasurePeakAmplitude_HandlesOddBytePartialReadsWithoutSampleSwap()
    {
        // Construct 200 stereo samples where every sample is +4000 (0x0FA0).
        // If an odd-byte read shifts alignment by 1 byte, 0x0FA0 swaps to 0xA00F (-24561), corrupting PeakSample!
        byte[] pcm = new byte[800];
        for (int i = 0; i < 400; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), 4000);
        }

        using var ms = new MemoryStream(pcm);
        using var oddChunkStream = new NonSeekableReadStream(ms, maxBytesPerRead: 13); // 13 is odd!

        var peak = RedBookPcmDspProcessor.MeasurePeakAmplitude(oddChunkStream);

        Assert.Equal((short)4000, peak.PeakSample);
    }

    [Fact]
    public async Task DataBurnService_VerifyAfterBurn_SucceedsWhenSourceStreamReturnsPartialReads()
    {
        var recorder = new InMemoryLoopbackRecorder();
        var engine = new InMemoryLoopbackBurnEngine();

        var root = CompilationDirectory.CreateRoot();
        byte[] payload = new byte[10_000];
        Random.Shared.NextBytes(payload);
        root.AddFile(CreateInMemoryFile("random.bin", payload));

        using var isoMs = IsoUdfImageBuilder.BuildToMemoryStream(root);
        using var chunkedIsoStream = new SeekablePartialReadStream(isoMs, maxBytesPerRead: 333);

        var service = new DataBurnService(engine);
        var result = await service.BurnStreamAsync(
            recorder,
            chunkedIsoStream,
            new DataBurnOptions { VerifyAfterBurn = true });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.Verified);
    }

    [Fact]
    public void IsoUdfImageBuilder_TruncatingCollidingEmojiFilenames_NeverProducesUnpairedUtf16Surrogates()
    {
        var root = CompilationDirectory.CreateRoot();

        // Build two 80-char filenames filled with 4-byte emoji surrogate pairs ("🔥" = 2 UTF-16 chars)
        // so truncation at 64 chars (Joliet) or 127 chars (UDF) plus "~1" disambiguation lands on surrogate boundaries.
        string emojiPrefix = string.Concat(Enumerable.Repeat("🔥🎵💿", 15)); // 90 UTF-16 code units
        root.AddFile(CreateInMemoryFile(emojiPrefix + "_A.txt", new byte[] { 1, 2, 3 }));
        root.AddFile(CreateInMemoryFile(emojiPrefix + "_B.txt", new byte[] { 4, 5, 6 }));
        root.AddFile(CreateInMemoryFile(emojiPrefix + "_C.txt", new byte[] { 7, 8, 9 }));

        using var isoStream = IsoUdfImageBuilder.BuildToMemoryStream(
            root,
            new IsoBuildOptions(IncludeJoliet: true, IncludeUdf: true));

        using var reader = IsoImageReader.FromStream(isoStream, leaveOpen: true);
        Assert.Equal(3, reader.AllFiles.Count);

        foreach (var file in reader.AllFiles)
        {
            AssertValidUtf16(file.Name);
        }
    }

    [Fact]
    public void IsoImageReader_StripsEcma119TrailingDotBeforeVersionSuffixOnExtensionlessFiles()
    {
        var root = CompilationDirectory.CreateRoot();
        root.AddFile(CreateInMemoryFile("MAKEFILE", "all:\n\techo ok\n"u8.ToArray()));

        using var isoStream = IsoUdfImageBuilder.BuildToMemoryStream(
            root,
            new IsoBuildOptions(IncludeJoliet: false, IncludeUdf: false));

        // Patch the ISO 9660 directory record identifier from "MAKEFILE;1" to "MAKEFILE.;1" (or "MAKEFIL.;1")
        // to simulate mkisofs / xorriso / imapi2fs.dll ECMA-119 §7.5.1 output.
        byte[] bytes = isoStream.ToArray();
        byte[] needle = "MAKEFILE;1"u8.ToArray();
        byte[] replacement = "MAKEFIL.;1"u8.ToArray();
        int idx = bytes.AsSpan().IndexOf(needle);
        Assert.True(idx > 0);
        replacement.CopyTo(bytes.AsSpan(idx, replacement.Length));

        using var patchedStream = new MemoryStream(bytes);
        using var reader = IsoImageReader.FromStream(patchedStream);

        var found = reader.FindFile("/MAKEFIL");
        Assert.NotNull(found);
        Assert.Equal("MAKEFIL", found!.Name);
    }

    [Fact]
    public void CompilationFolderImporter_SkipsCircularDirectoryJunctionOrSymlink_WithoutStackOverflow()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"ignis_cycle_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllBytes(Path.Combine(tempDir, "root.txt"), new byte[] { 1, 2, 3 });
            string subDir = Path.Combine(tempDir, "sub");
            Directory.CreateDirectory(subDir);
            File.WriteAllBytes(Path.Combine(subDir, "child.txt"), new byte[] { 4, 5, 6 });

            // Try creating a directory symbolic link or junction pointing back to tempDir
            string loopPath = Path.Combine(subDir, "loop_to_root");
            bool linkCreated = false;
            try
            {
                Directory.CreateSymbolicLink(loopPath, tempDir);
                linkCreated = true;
            }
            catch
            {
                // Symbolic links may require developer mode or elevation; fall back to testing self-import guard
            }

            var root = CompilationFolderImporter.ImportAsRoot(tempDir, strict: false);
            var allFiles = root.GetAllFilesRecursive().ToList();

            Assert.Equal(2, allFiles.Count);
            if (linkCreated)
            {
                Assert.True(Directory.Exists(loopPath));
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DvdLayerBreakCalculator_SelectsValidEccAlignedBreak_AcrossBoundarySizes()
    {
        // Just above single-layer DVD-5 limit (2,295,104 sectors)
        int totalSectors = DvdLayerBreakCalculator.Dvd5SectorLimit + 17;
        var optimal = DvdLayerBreakCalculator.SelectOptimalBreak(
            totalSectors,
            candidateCellLbas: new[] { 1_147_555, 1_147_568, 1_200_003 },
            alignToEcc: true);

        Assert.NotNull(optimal);
        Assert.True(optimal!.IsValid);
        Assert.Equal(0, optimal.BreakLba % 16);
        Assert.True(optimal.Layer0Sectors >= optimal.Layer1Sectors);
        Assert.True(optimal.Layer0Sectors <= DvdLayerBreakCalculator.MaxLayer0Sectors);
    }

    private static CompilationFile CreateInMemoryFile(string name, byte[] bytes) =>
        new(name, bytes.Length, () => new MemoryStream(bytes, writable: false));

    private static void AssertValidUtf16(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]))
            {
                Assert.True(i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]),
                    $"Unpaired high surrogate at index {i} in '{s}'.");
                i++;
            }
            else
            {
                Assert.False(char.IsLowSurrogate(s[i]),
                    $"Unpaired low surrogate at index {i} in '{s}'.");
            }
        }
    }

    private sealed class NonSeekableReadStream(Stream inner, int maxBytesPerRead) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, Math.Min(count, maxBytesPerRead));

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class SeekablePartialReadStream(Stream inner, int maxBytesPerRead) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, Math.Min(count, maxBytesPerRead));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, Math.Min(count, maxBytesPerRead), cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer[..Math.Min(buffer.Length, maxBytesPerRead)], cancellationToken);

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class InMemoryLoopbackRecorder : IDiscRecorderBackend
    {
        public string UniqueId => "LOOP-01";
        public string VolumePath => "V:\\";
        public string VendorId => "TEST";
        public string ProductId => "LOOPBACK";
        public string ProductRevision => "1.0";
        public bool IsVirtual => true;
        public DriveCapabilitiesDto GetCapabilities() => new() { CanWriteCdR = true };
        public MediaStatusDto QueryMediaStatus() => new()
        {
            MediaPresent = true,
            MediaType = DiscMediaType.CdR,
            IsBlank = true,
            IsAppendable = true,
            TotalSectors = 360_000,
            FreeSectors = 360_000
        };
        public IReadOnlyList<WriteSpeedDescriptor> GetSupportedWriteSpeeds() => Array.Empty<WriteSpeedDescriptor>();
        public void EjectMedia() { }
        public void CloseTray() { }
        public bool AcquireExclusiveAccess(string clientName) => true;
        public void ReleaseExclusiveAccess() { }
        public void Dispose() { }
    }

    private sealed class InMemoryLoopbackBurnEngine : IBurnEngineBackend
    {
        private byte[] _written = Array.Empty<byte>();

        public async Task<BurnOperationResult> BurnDataStreamAsync(
            IDiscRecorderBackend recorder,
            Stream imageStream,
            DataBurnOptions options,
            IProgress<BurnProgressSnapshot>? progress,
            CancellationToken cancellationToken)
        {
            using var ms = new MemoryStream();
            await imageStream.CopyToAsync(ms, cancellationToken);
            _written = ms.ToArray();
            int sectors = OpticalConstants.BytesToSectors(_written.Length, OpticalConstants.DataSectorSize);
            return BurnOperationResult.Succeeded(sectors, TimeSpan.FromMilliseconds(10), 16.0);
        }

        public Task<BurnOperationResult> BurnAudioTracksAsync(
            IDiscRecorderBackend recorder,
            IReadOnlyList<AudioTrackSource> tracks,
            AudioBurnOptions options,
            IProgress<BurnProgressSnapshot>? progress,
            CancellationToken cancellationToken) =>
            Task.FromResult(BurnOperationResult.Succeeded(0, TimeSpan.Zero));

        public Task<BurnOperationResult> EraseMediaAsync(
            IDiscRecorderBackend recorder,
            bool fullErase,
            IProgress<BurnProgressSnapshot>? progress,
            CancellationToken cancellationToken) =>
            Task.FromResult(BurnOperationResult.Succeeded(0, TimeSpan.Zero));

        public async Task<RipOperationResult> ReadSectorsAsync(
            IDiscRecorderBackend recorder,
            int startLba,
            int sectorCount,
            bool raw2352Mode,
            Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer,
            IProgress<BurnProgressSnapshot>? progress,
            CancellationToken cancellationToken)
        {
            for (int i = 0; i < sectorCount; i++)
            {
                int offset = (startLba + i) * OpticalConstants.DataSectorSize;
                await sectorConsumer(startLba + i, _written.AsMemory(offset, OpticalConstants.DataSectorSize));
            }
            return RipOperationResult.Succeeded(sectorCount, TimeSpan.FromMilliseconds(5));
        }
    }
}
