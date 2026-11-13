using System.Buffers.Binary;
using System.Text;
using CdBurner.Core.Audio;
using CdBurner.Core.DvdVideo;
using CdBurner.Hardware.Audio;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// MP3, Opus and Ogg Vorbis files: the length of the music is read from the file's own headers (LAME/Xing tag,
/// RFC 7845 pre-skip and granule position, Vorbis granule position) and the decoder output is cut to exactly that.
/// The MP3 and Opus files are written byte by byte from those specifications, so the expected answer does not come
/// from the code under test. Decode tests run where an ffmpeg is installed.
/// </summary>
public sealed class GaplessLossyDecodeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "CdBurner_Gapless_" + Guid.NewGuid().ToString("N"));

    public GaplessLossyDecodeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ---- fixtures written from the specifications ------------------------------------------------------------------

    /// <summary>
    /// MPEG-1 Layer III, 44.1 kHz, 128 kbit/s, stereo. Frame 0 is the Info frame with a LAME extension (delay and padding
    /// as two 12-bit fields); the audio frames are all-zero (decode to silence).
    /// </summary>
    private static byte[] BuildMp3(int audioFrames, int delay, int padding, bool withInfoFrame = true, int sampleRateIndex = 0)
    {
        byte[] header = [0xFF, 0xFB, 0x90, 0x00];
        header[2] = (byte)(0x90 | (sampleRateIndex << 2));
        int frameSize = sampleRateIndex switch { 0 => 417, 1 => 384, _ => 576 }; // 144 * 128000 / rate, no padding bit
        using var ms = new MemoryStream();
        if (withInfoFrame)
        {
            byte[] f = new byte[frameSize];
            header.CopyTo(f, 0);
            int p = 4 + 32; // side information of an MPEG-1 stereo frame
            Encoding.ASCII.GetBytes("Info").CopyTo(f, p);
            p += 4;
            BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(p), 0x0F); // frames, bytes, TOC and quality present
            p += 4;
            BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(p), (uint)audioFrames);
            p += 4;
            BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(p), (uint)(audioFrames * frameSize));
            p += 4 + 100 + 4;
            Encoding.ASCII.GetBytes("LAME3.99r").CopyTo(f, p);
            int d = p + 21;
            f[d] = (byte)(delay >> 4);
            f[d + 1] = (byte)(((delay & 0xF) << 4) | (padding >> 8));
            f[d + 2] = (byte)(padding & 0xFF);
            ms.Write(f);
        }
        for (int i = 0; i < audioFrames; i++)
        {
            byte[] f = new byte[frameSize];
            header.CopyTo(f, 0);
            ms.Write(f);
        }
        return ms.ToArray();
    }

    private static readonly uint[] OggCrcTable = BuildOggCrcTable();

    private static uint[] BuildOggCrcTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint r = i << 24;
            for (int k = 0; k < 8; k++) r = (r & 0x80000000) != 0 ? (r << 1) ^ 0x04C11DB7 : r << 1;
            t[i] = r;
        }
        return t;
    }

    private static byte[] OggPage(byte flags, long granule, uint serial, uint sequence, params byte[][] packets)
    {
        var table = new List<byte>();
        var body = new List<byte>();
        foreach (var packet in packets)
        {
            int left = packet.Length;
            while (left >= 255) { table.Add(255); left -= 255; }
            table.Add((byte)left);
            body.AddRange(packet);
        }
        byte[] page = new byte[27 + table.Count + body.Count];
        Encoding.ASCII.GetBytes("OggS").CopyTo(page, 0);
        page[5] = flags;
        BinaryPrimitives.WriteInt64LittleEndian(page.AsSpan(6), granule);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14), serial);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(18), sequence);
        page[26] = (byte)table.Count;
        table.CopyTo(page, 27);
        body.CopyTo(page, 27 + table.Count);
        uint crc = 0;
        foreach (byte b in page) crc = (crc << 8) ^ OggCrcTable[(byte)((crc >> 24) ^ b)];
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22), crc);
        return page;
    }

    private static byte[] OpusComments(params string[] entries)
    {
        using var ms = new MemoryStream();
        ms.Write("OpusTags"u8);
        byte[] vendor = Encoding.UTF8.GetBytes("test");
        ms.Write(BitConverter.GetBytes((uint)vendor.Length));
        ms.Write(vendor);
        ms.Write(BitConverter.GetBytes((uint)entries.Length));
        foreach (string e in entries)
        {
            byte[] b = Encoding.UTF8.GetBytes(e);
            ms.Write(BitConverter.GetBytes((uint)b.Length));
            ms.Write(b);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Ogg Opus, mono, one 20 ms CELT packet (960 samples at 48 kHz) per page. The final granule position is
    /// packets x 960 - <paramref name="endTrim"/> (RFC 7845 counts the pre-skip samples in the granule position), which is
    /// how an encoder marks padding in the last packet. The music is that count minus the pre-skip.
    /// </summary>
    private static byte[] BuildOpus(int packets, int preSkip, int endTrim, params string[] comments)
    {
        const uint serial = 0x1234;
        using var ms = new MemoryStream();
        byte[] head = new byte[19];
        "OpusHead"u8.CopyTo(head);
        head[8] = 1; head[9] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(10), (ushort)preSkip);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12), 44100);
        ms.Write(OggPage(0x02, 0, serial, 0, head));
        ms.Write(OggPage(0, 0, serial, 1, OpusComments(comments)));
        for (int i = 0; i < packets; i++)
        {
            byte[] packet = new byte[1 + 40];
            packet[0] = 0xF8; // CELT fullband, 20 ms, one frame, mono
            bool last = i == packets - 1;
            long granule = (long)(i + 1) * 960 - (last ? endTrim : 0);
            ms.Write(OggPage((byte)(last ? 0x04 : 0), granule, serial, (uint)(2 + i), packet));
        }
        return ms.ToArray();
    }

    private static string? FindFfmpeg() =>
        FfmpegLocator.FindFfmpegExecutable(Environment.GetEnvironmentVariable("IGNISDISC_TEST_FFMPEG"));

    private string Write(string name, byte[] data)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    private static string VorbisFixture() => Path.Combine(AppContext.BaseDirectory, "Fixtures", "sine-1s-44k-stereo.ogg");

    // ---- header parsing --------------------------------------------------------------------------------------------

    [Fact]
    public void Mp3_LameTag_GivesFramesMinusDelayMinusPadding()
    {
        string path = Write("a.mp3", BuildMp3(audioFrames: 40, delay: 1105, padding: 1376));
        var info = GaplessStreamInfo.TryRead(path);

        Assert.NotNull(info);
        Assert.Equal(GaplessCodec.Mp3, info!.Codec);
        Assert.Equal(44100, info.SampleRate);
        Assert.Equal(2, info.Channels);
        Assert.Equal(40 * 1152 - 1105 - 1376, info.TrimmedSamples);
        Assert.Equal(40 * 1152, info.UntrimmedSamples);
        Assert.Equal(1105 + 529, info.LeadingSkipWhenUntrimmed); // encoder delay + the decoder's own 529
    }

    [Fact]
    public void Mp3_AfterId3v2Tag_StillFindsTheInfoFrame()
    {
        byte[] audio = BuildMp3(10, 576, 900);
        // ID3v2.3 header: "ID3", version, flags, synchsafe size 300, then 300 bytes of tag data.
        byte[] id3 = new byte[10 + 300];
        Encoding.ASCII.GetBytes("ID3").CopyTo(id3, 0);
        id3[3] = 3;
        id3[8] = (byte)(300 >> 7);
        id3[9] = (byte)(300 & 0x7F);
        string path = Write("tagged.mp3", [.. id3, .. audio]);

        Assert.Equal(10 * 1152 - 576 - 900, GaplessStreamInfo.TryRead(path)!.TrimmedSamples);
    }

    [Fact]
    public void Mp3_WithoutInfoFrame_HasNoExactLength()
    {
        var info = GaplessStreamInfo.TryRead(Write("plain.mp3", BuildMp3(10, 0, 0, withInfoFrame: false)));

        Assert.NotNull(info);
        Assert.False(info!.HasExactLength);
        Assert.Equal(44100, info.SampleRate);
    }

    [Fact]
    public void Opus_HeaderGivesGranuleMinusPreSkip_AndTags()
    {
        string path = Write("a.opus", BuildOpus(packets: 50, preSkip: 312, endTrim: 400, "TITLE=First", "ARTIST=Band", "TRACKNUMBER=3/12"));
        var info = GaplessStreamInfo.TryRead(path);

        Assert.NotNull(info);
        Assert.Equal(GaplessCodec.Opus, info!.Codec);
        Assert.Equal(48000, info.SampleRate);
        Assert.Equal(1, info.Channels);
        Assert.Equal(50 * 960 - 400 - 312, info.TrimmedSamples);    // last granule 47600, minus pre-skip 312
        Assert.Equal(50 * 960 - 400, info.UntrimmedSamples);
        Assert.Equal(312, info.LeadingSkipWhenUntrimmed);
        Assert.Equal("First", info.Comments!["TITLE"]);
        Assert.Equal("3/12", info.Comments["TRACKNUMBER"]);
    }

    [Fact]
    public void Vorbis_HeaderReadsRateChannelsAndLastGranule()
    {
        var info = GaplessStreamInfo.TryRead(VorbisFixture());

        Assert.NotNull(info);
        Assert.Equal(GaplessCodec.Vorbis, info!.Codec);
        Assert.Equal(44100, info.SampleRate);
        Assert.Equal(2, info.Channels);
        Assert.True(info.HasExactLength);
        Assert.Equal(info.TrimmedSamples, info.UntrimmedSamples);
    }

    [Fact]
    public void NonAudioFile_IsNotRecognised()
    {
        Assert.Null(GaplessStreamInfo.TryRead(Write("x.mp3", new byte[2000])));
    }

    [Fact]
    public void TruncatedOggFile_DoesNotThrow()
    {
        byte[] opus = BuildOpus(5, 312, 0);
        string path = Write("cut.opus", opus[..(opus.Length - 20)]);

        var info = GaplessStreamInfo.TryRead(path);

        // The incomplete last page is ignored, so the length is that of the last complete page.
        Assert.NotNull(info);
        Assert.Equal(4 * 960 - 312, info!.TrimmedSamples);
    }

    // ---- trimming rule ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(46000L, 0L, 46000L)] // decoder already trimmed exactly: unchanged
    [InlineData(46100L, 0L, 46000L)] // extra samples at the end are cut
    [InlineData(45000L, 0L, 45000L)] // short decode is never padded
    [InlineData(47634L, 1634L, 46000L)] // decoder returned every frame untrimmed: lead-in dropped, then cut
    public void PlanTrim_CutsToTheLengthOfTheMusic(long decoded, long expectedSkip, long expectedTake)
    {
        // 1152 x 41 = 47232 would be the untrimmed total; use explicit numbers instead.
        var info = new GaplessStreamInfo(GaplessCodec.Mp3, 44100, 2, TrimmedSamples: 46000, UntrimmedSamples: 47634, LeadingSkipWhenUntrimmed: 1634);

        var (skip, take) = GaplessStreamInfo.PlanTrim(info, decoded);

        Assert.Equal(expectedSkip, skip);
        Assert.Equal(expectedTake, take);
    }

    [Fact]
    public void PlanTrim_UnknownLength_KeepsEverything()
    {
        var info = new GaplessStreamInfo(GaplessCodec.Mp3, 44100, 2, 0, 0, 0);
        Assert.Equal((0L, 1234L), GaplessStreamInfo.PlanTrim(info, 1234));
    }

    // ---- probe (no ffmpeg needed) ----------------------------------------------------------------------------------

    [Fact]
    public void Probe_Opus_ReportsMusicLengthAtRedBookRate_AndTags()
    {
        string path = Write("p.opus", BuildOpus(50, 312, 400, "TITLE=Song", "ALBUM=Record", "TRACKNUMBER=2"));
        using var decoder = new MediaFoundationAudioDecoder(() => null);

        var meta = decoder.ProbeAudioFile(path);

        long redBookFrames = (long)Math.Round((50 * 960 - 400 - 312) * 44100.0 / 48000.0);
        Assert.Equal(redBookFrames * 4, meta.TotalRedBookBytes);
        Assert.Equal("Opus", meta.FormatName);
        Assert.Equal("Song", meta.Title);
        Assert.Equal("Record", meta.Album);
        Assert.Equal(2, meta.TrackNumber);
    }

    [Fact]
    public void Open_OpusWithoutFfmpeg_SaysHowToGetIt()
    {
        string path = Write("n.opus", BuildOpus(5, 312, 0));
        using var decoder = new MediaFoundationAudioDecoder(() => null);

        var ex = Assert.Throws<FfmpegNotInstalledException>(() => decoder.OpenRedBookPcmStream(path));

        Assert.Contains("n.opus", ex.Message);
        Assert.Contains("ffmpeg", ex.Message);
        Assert.Contains("Install FFmpeg", ex.Message);
    }

    // ---- decoding through ffmpeg (skipped where ffmpeg is not installed) -------------------------------------------

    private static long Samples(Stream s) => s.Length / 4;

    [Fact]
    public void Decode_Mp3_ReturnsExactlyTheMusic()
    {
        string? ffmpeg = FindFfmpeg();
        if (ffmpeg == null) return;
        string path = Write("d.mp3", BuildMp3(audioFrames: 40, delay: 1105, padding: 1376));
        using var decoder = new MediaFoundationAudioDecoder(() => ffmpeg);

        using var pcm = decoder.OpenRedBookPcmStream(path);

        Assert.Equal(40 * 1152 - 1105 - 1376, Samples(pcm));
        Assert.Equal(pcm.Length, decoder.ProbeAudioFile(path).TotalRedBookBytes); // the probe predicts the decode
    }

    [Fact]
    public void Decode_Opus_ReturnsMusicLengthAtRedBookRate()
    {
        string? ffmpeg = FindFfmpeg();
        if (ffmpeg == null) return;
        string path = Write("d.opus", BuildOpus(50, 312, 400));
        using var decoder = new MediaFoundationAudioDecoder(() => ffmpeg);

        using var pcm = decoder.OpenRedBookPcmStream(path);

        long expected = (long)Math.Round((50 * 960 - 400 - 312) * 44100.0 / 48000.0);
        Assert.Equal(expected, Samples(pcm));
        Assert.Equal(pcm.Length, decoder.ProbeAudioFile(path).TotalRedBookBytes);
    }

    [Fact]
    public void Decode_Vorbis_ReturnsLastGranuleSamples()
    {
        string? ffmpeg = FindFfmpeg();
        if (ffmpeg == null) return;
        using var decoder = new MediaFoundationAudioDecoder(() => ffmpeg);
        var info = GaplessStreamInfo.TryRead(VorbisFixture())!;

        using var pcm = decoder.OpenRedBookPcmStream(VorbisFixture());

        // The fixture comes from ffmpeg's experimental Vorbis encoder, whose last granule position can claim a few
        // samples more than its own stream decodes to. The rule is: never more than the granule says, and the same
        // count as ffmpeg itself returns.
        Assert.True(Samples(pcm) <= info.TrimmedSamples);
        Assert.Equal(ReferenceSampleCount(ffmpeg, VorbisFixture()), Samples(pcm));
    }

    private long ReferenceSampleCount(string ffmpeg, string input)
    {
        string raw = Path.Combine(_dir, "reference.raw");
        var psi = new System.Diagnostics.ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (string a in new[] { "-hide_banner", "-nostdin", "-v", "error", "-y", "-i", input, "-ac", "2", "-ar", "44100", "-f", "s16le", raw })
        {
            psi.ArgumentList.Add(a);
        }
        using var proc = System.Diagnostics.Process.Start(psi)!;
        proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        Assert.Equal(0, proc.ExitCode);
        return new FileInfo(raw).Length / 4;
    }

    [Fact]
    public void GaplessAlbum_TracksJoinWithoutAddedSamples()
    {
        string? ffmpeg = FindFfmpeg();
        if (ffmpeg == null) return;
        // Three tracks cut from one recording: each has its own delay and padding, as an encoder writes them.
        (int frames, int delay, int pad)[] tracks = [(120, 1105, 1376), (97, 1105, 811), (143, 1105, 2000)];
        using var decoder = new MediaFoundationAudioDecoder(() => ffmpeg);

        long total = 0;
        for (int i = 0; i < tracks.Length; i++)
        {
            string path = Write($"t{i}.mp3", BuildMp3(tracks[i].frames, tracks[i].delay, tracks[i].pad));
            using var pcm = decoder.OpenRedBookPcmStream(path);
            Assert.Equal(tracks[i].frames * 1152 - tracks[i].delay - tracks[i].pad, Samples(pcm));
            total += Samples(pcm);
        }

        long expectedTotal = tracks.Sum(t => (long)t.frames * 1152 - t.delay - t.pad);
        Assert.Equal(expectedTotal, total);
    }
}
