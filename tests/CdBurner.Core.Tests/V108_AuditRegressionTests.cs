using System.Buffers.Binary;
using System.Text;
using CdBurner.Core.Audio;
using CdBurner.Core.Compilation;
using CdBurner.Core.Images;
using CdBurner.Core.Iso;
using CdBurner.Core.Persistence;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Tests;

/// <summary>
/// Regression tests for the v1.08 pre-release audit: FLAC bitstream decoding (FIXED/LPC predictors, stereo decorrelation,
/// wasted bits, corruption, truncation, ID3), streaming Red Book conversion (band-limited resampling, downmix),
/// CUE INDEX 00 handling and EAC multi-file layouts, single-file CUE persistence, crossfade, legacy image geometry,
/// and playlist decoding.
/// </summary>
public sealed class V108_AuditRegressionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "IgnisDisc_V108_" + Guid.NewGuid().ToString("N"));

    public V108_AuditRegressionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    #region FLAC bitstream: predictors and stereo decorrelation

    public enum Sub { Verbatim, Fixed0, Fixed1, Fixed2, Fixed3, Fixed4, Lpc2, Lpc8 }

    public static IEnumerable<object[]> StereoModeMatrix()
    {
        foreach (int assignment in new[] { 1, 8, 9, 10 }) // independent, left-side, right-side, mid-side
        {
            foreach (Sub sub in Enum.GetValues<Sub>())
            {
                yield return new object[] { assignment, sub };
            }
        }
    }

    [Theory]
    [MemberData(nameof(StereoModeMatrix))]
    public void Flac16_AllStereoModesAndPredictors_DecodeBitExact(int assignment, Sub sub)
    {
        var (left, right) = MakeStereoSignal(5000, bits: 16, seed: assignment * 31 + (int)sub);
        byte[] flac = FlacTestEncoder.Encode(44100, 16, new[] { left, right }, blockSize: 1152, assignment, sub);

        byte[] pcm = FlacFileCodec.DecodeToRedBookPcm(new MemoryStream(flac));

        Assert.Equal(ExpectedRedBook(left, right, 16), pcm);
    }

    [Theory]
    [InlineData(8, Sub.Fixed2)]
    [InlineData(9, Sub.Lpc8)]
    [InlineData(10, Sub.Fixed4)]
    [InlineData(10, Sub.Lpc2)]
    [InlineData(10, Sub.Verbatim)]
    public void Flac24_FullScaleOppositePolarity_SideChannelUses25Bits_NoOverflow(int assignment, Sub sub)
    {
        // L = +max, R = -min gives side = L - R = 16,777,215: only representable with bps + 1 = 25 bits.
        const int n = 4096;
        var left = new int[n];
        var right = new int[n];
        var rng = new Random(7);
        for (int i = 0; i < n; i++)
        {
            bool extreme = (i / 64) % 2 == 0;
            left[i] = extreme ? ((i % 2 == 0) ? 8_388_607 : -8_388_608) : rng.Next(-8_388_608, 8_388_607);
            right[i] = extreme ? ((i % 2 == 0) ? -8_388_608 : 8_388_607) : rng.Next(-8_388_608, 8_388_607);
        }

        byte[] flac = FlacTestEncoder.Encode(44100, 24, new[] { left, right }, blockSize: 4096, assignment, sub);
        byte[] pcm = FlacFileCodec.DecodeToRedBookPcm(new MemoryStream(flac));

        // 24 → 16-bit adds TPDF dither, so each sample may differ from plain rounding by one step. A decorrelation or
        // overflow bug would be off by thousands, so this still proves the 25-bit side channel decodes correctly.
        AssertWithinOneLsb(ExpectedRedBook(left, right, 24), pcm);
    }

    [Fact]
    public void Flac_WastedBitsSubframes_AreShiftedBack()
    {
        // Samples that are all multiples of 8 are encoded with 3 "wasted" low bits.
        var (l, r) = MakeStereoSignal(3000, bits: 16, seed: 3);
        for (int i = 0; i < l.Length; i++) { l[i] &= ~7; r[i] &= ~7; }

        byte[] flac = FlacTestEncoder.Encode(44100, 16, new[] { l, r }, 1000, assignment: 1, Sub.Fixed2, wastedBits: 3);

        Assert.Equal(ExpectedRedBook(l, r, 16), FlacFileCodec.DecodeToRedBookPcm(new MemoryStream(flac)));
    }

    [Fact]
    public void Flac_CorruptMiddleFrame_IsConcealedWithSilence_AndLaterAudioKeepsItsTiming()
    {
        byte[] pcm = RandomPcm(3 * 4096, seed: 11);
        byte[] flac = FlacFileCodec.CreateFlacBytes(pcm);

        // Built-in encoder layout: 42 bytes of metadata, then fixed-size VERBATIM frames.
        const int frameBytes = 8 + (2 * (1 + (4096 * 2))) + 2;
        int frame1 = 42 + frameBytes;
        flac[frame1 + 500] ^= 0x5A; // corrupt audio payload of frame 1 (CRC-16 must catch it)

        byte[] decoded = FlacFileCodec.DecodeToRedBookPcm(new MemoryStream(flac));

        Assert.Equal(pcm.Length, decoded.Length); // timeline preserved: no frame silently dropped
        Assert.Equal(pcm.AsSpan(0, 4096 * 4).ToArray(), decoded.AsSpan(0, 4096 * 4).ToArray());
        Assert.True(decoded.AsSpan(4096 * 4, 4096 * 4).ToArray().All(b => b == 0), "Corrupt frame must decode as silence, not noise.");
        Assert.Equal(pcm.AsSpan(8192 * 4).ToArray(), decoded.AsSpan(8192 * 4).ToArray());
    }

    [Fact]
    public void Flac_TruncatedFinalFrame_DecodesCompleteFramesWithoutThrowing()
    {
        byte[] pcm = RandomPcm(3 * 4096, seed: 12);
        byte[] flac = FlacFileCodec.CreateFlacBytes(pcm);
        const int frameBytes = 8 + (2 * (1 + (4096 * 2))) + 2;
        byte[] truncated = flac.AsSpan(0, 42 + (2 * frameBytes) + 1000).ToArray();

        byte[] decoded = FlacFileCodec.DecodeToRedBookPcm(new MemoryStream(truncated));

        Assert.Equal(pcm.AsSpan(0, 8192 * 4).ToArray(), decoded);
    }

    [Fact]
    public void Flac_ZeroTotalSamplesInStreamInfo_ProbeCountsFramesAndDecodeIsExact()
    {
        byte[] pcm = RandomPcm(10_000, seed: 13);
        byte[] flac = FlacFileCodec.CreateFlacBytes(pcm);
        // STREAMINFO body starts at byte 8; total_samples is the low 36 bits of body bytes 13..17.
        flac[8 + 13] &= 0xF0;
        flac[8 + 14] = flac[8 + 15] = flac[8 + 16] = flac[8 + 17] = 0;

        string path = Path.Combine(_dir, "streamed.flac");
        File.WriteAllBytes(path, flac);

        var meta = FlacFileCodec.ProbeAudioFile(path);
        Assert.Equal(10_000L * 4, meta.TotalRedBookBytes);
        Assert.Equal(pcm, FlacFileCodec.DecodeToRedBookPcm(path));
    }

    [Fact]
    public void Flac_Id3v23WithReservedBit4_IsNotTreatedAsV24Footer()
    {
        byte[] pcm = RandomPcm(2000, seed: 14);
        byte[] flac = FlacFileCodec.CreateFlacBytes(pcm);

        // ID3v2.3 with unsynchronisation (0x80), extended header (0x40) and the reserved 0x10 bit set.
        byte[] tag = BuildId3v2(versionMajor: 3, flags: 0xD0, declaredBodySize: 64, actualBodySize: 64);
        byte[] file = tag.Concat(flac).ToArray();

        Assert.Equal(pcm, FlacFileCodec.DecodeToRedBookPcm(new MemoryStream(file)));
    }

    [Fact]
    public void Flac_Id3TagThatUndercountsItsPadding_ResynchronisesOnFlacMagic()
    {
        byte[] pcm = RandomPcm(2000, seed: 15);
        byte[] flac = FlacFileCodec.CreateFlacBytes(pcm);
        byte[] tag = BuildId3v2(versionMajor: 4, flags: 0x00, declaredBodySize: 100, actualBodySize: 160);
        byte[] file = tag.Concat(flac).ToArray();

        Assert.Equal(pcm, FlacFileCodec.DecodeToRedBookPcm(new MemoryStream(file)));
    }

    [Fact]
    public void Flac_StreamLargerThanDecodeWindow_DecodesExactlyFromNonSeekableStream()
    {
        // ~30 s of 16-bit stereo VERBATIM = ~5.3 MB compressed, larger than the 4 MB sliding decode window.
        byte[] pcm = RandomPcm(44100 * 30, seed: 16);
        byte[] flac = FlacFileCodec.CreateFlacBytes(pcm);
        Assert.True(flac.Length > 4 * 1024 * 1024);

        using var nonSeekable = new NonSeekableStream(new MemoryStream(flac));
        Assert.Equal(pcm, FlacFileCodec.DecodeToRedBookPcm(nonSeekable));
    }

    [Fact]
    public void Flac_SixChannel24Bit48k_IsDownmixedAndResampled()
    {
        const int n = 48000;
        var data = new byte[n * 6 * 3];
        for (int i = 0; i < n; i++)
        {
            // Only the centre channel (index 2) carries a 1 kHz tone.
            int c = (int)(0.5 * 8_388_607 * Math.Sin(2 * Math.PI * 1000 * i / 48000.0));
            int off = ((i * 6) + 2) * 3;
            data[off] = (byte)c;
            data[off + 1] = (byte)(c >> 8);
            data[off + 2] = (byte)(c >> 16);
        }

        byte[] flac = FlacFileCodec.CreateFlacBytes(data, 48000, channels: 6, bitsPerSample: 24);
        byte[] pcm = FlacFileCodec.DecodeToRedBookPcm(new MemoryStream(flac));

        Assert.Equal(44100 * 4, pcm.Length);
        var (l, r) = SplitStereo(pcm);
        // Centre folds equally into both sides (each side gets its own dither, hence the 2-step tolerance).
        Assert.All(Enumerable.Range(0, l.Length), i => Assert.InRange(l[i] - r[i], -2, 2));
        Assert.True(Rms(l, 4410, 39690) > 1000, "Centre (dialogue/vocal) channel must not be dropped by the stereo fold-down.");
    }

    #endregion

    #region Streaming Red Book conversion

    [Theory]
    [InlineData(96000)]
    [InlineData(48000)]
    [InlineData(88200)]
    [InlineData(192000)]
    [InlineData(22050)]
    [InlineData(32000)]
    [InlineData(44056)] // coprime-ish rate: exercises the quantized-phase polyphase path
    public void Converter_UnusualRates_ProduceExactLengthAndPreserveAudibleTone(int rate)
    {
        int n = rate; // 1 second
        var (ch, _) = Tone(rate, n, 1000, 0.5, bits: 24);
        var conv = new RedBookPcmConverter(rate, n);
        for (int off = 0; off < n; off += 4096)
        {
            int count = Math.Min(4096, n - off);
            conv.WriteBlock(new[] { ch[0][off..(off + count)], ch[1][off..(off + count)] }, 2, 24, count);
        }

        byte[] pcm = conv.Finish();
        long expectedFrames = (long)Math.Round((double)n * 44100 / rate);
        Assert.Equal(expectedFrames * 4, pcm.Length);

        var (l, _) = SplitStereo(pcm);
        double rms = Rms(l, 4410, l.Length - 4410);
        double expected = 0.5 * 32768 / Math.Sqrt(2);
        Assert.InRange(rms, expected * 0.97, expected * 1.03);
    }

    [Theory]
    [InlineData(96000, 30000)]
    [InlineData(192000, 40000)]
    [InlineData(88200, 25000)]
    public void Converter_DownsamplingHiRes_RejectsUltrasonicContentInsteadOfAliasing(int rate, int toneHz)
    {
        // An ultrasonic tone must be filtered out, not folded back into the audible band (linear interpolation aliases it).
        int n = rate;
        var (ch, _) = Tone(rate, n, toneHz, 0.5, bits: 24);
        var conv = new RedBookPcmConverter(rate, n);
        conv.WriteBlock(ch, 2, 24, n);
        var (l, _) = SplitStereo(conv.Finish());

        double inputRms = 0.5 * 32768 / Math.Sqrt(2);
        double outRms = Rms(l, 4410, l.Length - 4410);
        Assert.True(outRms < inputRms * 0.01, $"Ultrasonic {toneHz} Hz leaked at {20 * Math.Log10(outRms / inputRms):F1} dB.");
    }

    [Fact]
    public void Converter_Downmix_DropsLfe_AndNeverClipsAtFullScale()
    {
        const int n = 1000;
        var six = Enumerable.Range(0, 6).Select(_ => new int[n]).ToArray();
        for (int i = 0; i < n; i++) six[3][i] = 30000; // LFE only

        var conv = new RedBookPcmConverter(44100);
        conv.WriteBlock(six, 6, 16, n);
        Assert.True(conv.Finish().All(b => b == 0), "LFE must not be folded into the stereo mix.");

        var full = Enumerable.Range(0, 6).Select(_ => Enumerable.Repeat(32767, n).ToArray()).ToArray();
        var conv2 = new RedBookPcmConverter(44100);
        conv2.WriteBlock(full, 6, 16, n);
        var (l, r) = SplitStereo(conv2.Finish());
        Assert.All(l, s => Assert.InRange(s, 32000, 32767));
        Assert.All(r, s => Assert.InRange(s, 32000, 32767));
    }

    [Fact]
    public void Converter_SilenceInsertion_AdvancesTimelineAtSourceRate()
    {
        var conv = new RedBookPcmConverter(48000, 96000);
        conv.WriteSilence(48000);
        conv.WriteSilence(48000);
        Assert.Equal(88200 * 4, conv.Finish().Length);
    }

    #endregion

    #region CUE sheets: EAC multi-file layout, INDEX 00, persistence

    [Fact]
    public void Cue_EacGapsAppendedLayout_KeepsEachTracksIndex01InItsOwnFile()
    {
        // EAC "noncompliant" layout: TRACK n+1's INDEX 00 sits in FILE n, its INDEX 01 after the next FILE line.
        int[] lengths = { 150, 225, 75 };
        for (int t = 0; t < 3; t++)
        {
            WavFileCodec.WriteWavFile(Path.Combine(_dir, $"0{t + 1}.wav"), RandomPcm(lengths[t] * 588, seed: 20 + t));
        }

        string cue = """
            PERFORMER "Gap Band"
            TITLE "Appended Gaps"
            FILE "01.wav" WAVE
              TRACK 01 AUDIO
                TITLE "One"
                INDEX 01 00:00:00
              TRACK 02 AUDIO
                TITLE "Two"
                INDEX 00 00:01:50
            FILE "02.wav" WAVE
                INDEX 01 00:00:00
              TRACK 03 AUDIO
                TITLE "Three"
                INDEX 00 00:02:60
            FILE "03.wav" WAVE
                INDEX 01 00:00:00
            """;

        var sheet = CueSheetParser.Parse(cue);
        Assert.Equal(3, sheet.Files.Count);
        Assert.All(sheet.Files, f => Assert.Single(f.Tracks));
        Assert.All(sheet.Files, f => Assert.Contains(f.Tracks[0].Indices, i => i.IndexNumber == 1));

        var comp = new AudioCdCompilation();
        Assert.Equal(3, comp.ImportCueSheet(sheet, _dir));
        Assert.Equal(new[] { "One", "Two", "Three" }, comp.Tracks.Select(t => t.Title));
        Assert.Equal(lengths, comp.Tracks.Select(t => t.DurationFrames));
        Assert.All(comp.Tracks, t => Assert.Null(t.CustomPcmStreamFactory)); // whole files, not slices
    }

    [Fact]
    public void Cue_SingleFileWithIndex00Pregap_SlicesAtIndex01_SoNoAudioIsDuplicatedOrClipped()
    {
        byte[] album = RandomPcm(300 * 588, seed: 30);
        WavFileCodec.WriteWavFile(Path.Combine(_dir, "album.wav"), album);

        string cue = """
            FILE "album.wav" WAVE
              TRACK 01 AUDIO
                INDEX 01 00:00:00
              TRACK 02 AUDIO
                INDEX 00 00:01:50
                INDEX 01 00:02:00
              TRACK 03 AUDIO
                INDEX 01 00:03:00
            """;

        var comp = new AudioCdCompilation();
        comp.ImportCueSheet(CueSheetParser.Parse(cue), _dir);

        byte[][] slices = comp.Tracks.Select(t => ReadAll(t.CustomPcmStreamFactory!())).ToArray();

        // Track 1 = [0, 150): includes Track 2's 25-frame INDEX 00 gap exactly once. Track 2 = [150, 225). Track 3 = [225, 300).
        Assert.Equal(album.AsSpan(0, 150 * 2352).ToArray(), slices[0]);
        Assert.Equal(album.AsSpan(150 * 2352, 75 * 2352).ToArray(), slices[1]);
        Assert.Equal(album.AsSpan(225 * 2352).ToArray(), slices[2]);
        Assert.Equal(album.Length, slices.Sum(s => s.Length));
    }

    [Fact]
    public void Project_SingleFileCueTracks_SurviveSaveAndReload_AsSlicesNotWholeAlbum()
    {
        byte[] album = RandomPcm(300 * 588, seed: 31);
        WavFileCodec.WriteWavFile(Path.Combine(_dir, "live.wav"), album);
        string cue = """
            FILE "live.wav" WAVE
              TRACK 01 AUDIO
                INDEX 01 00:00:00
              TRACK 02 AUDIO
                INDEX 01 00:01:00
              TRACK 03 AUDIO
                INDEX 01 00:03:00
            """;

        var comp = new AudioCdCompilation { UseRmsLoudnessLeveling = true, CrossfadeSeconds = 1.5 };
        comp.ImportCueSheet(CueSheetParser.Parse(cue), _dir);

        string json = ProjectSerializer.ToJson(ProjectSerializer.FromAudioCdCompilation(comp));
        var reloaded = ProjectSerializer.ToAudioCdCompilation(ProjectSerializer.FromJson(json));

        Assert.True(reloaded.UseRmsLoudnessLeveling);
        Assert.Equal(1.5, reloaded.CrossfadeSeconds);
        Assert.Equal(3, reloaded.Tracks.Count);
        Assert.Equal(new int?[] { 0, 75, 225 }, reloaded.Tracks.Select(t => t.SourceStartFrame));
        Assert.Equal(new int?[] { 75, 150, null }, reloaded.Tracks.Select(t => t.SourceFrameCount));

        for (int i = 0; i < 3; i++)
        {
            Assert.NotNull(reloaded.Tracks[i].CustomPcmStreamFactory);
            Assert.Equal(ReadAll(comp.Tracks[i].CustomPcmStreamFactory!()), ReadAll(reloaded.Tracks[i].CustomPcmStreamFactory!()));
        }

        // Before the fix, each reloaded track pointed at the whole album: 3 x 300 frames.
        Assert.Equal(300, reloaded.Tracks.Sum(t => ReadAll(t.CustomPcmStreamFactory!()).Length / 2352));
    }

    #endregion

    #region DSP: crossfade and sector padding

    [Fact]
    public void Crossfade_OverlapsTracks_SoTheTransitionPlaysOnce()
    {
        byte[] a = ConstantPcm(75 * 588, 1000);  // 1 s
        byte[] b = RampPcm(75 * 588);            // 1 s, every sample distinct

        var (fa, fb) = RedBookPcmDspProcessor.ApplyCrossfadeBetweenTracks(a, b, 0.5);

        const int overlap = 37 * 588; // 0.5 s rounded down to whole sectors
        Assert.Equal(a.Length, fa.Length);
        Assert.Equal(b.Length - (overlap * 4), fb.Length);
        Assert.Equal(0, fb.Length % 2352);
        Assert.Equal(0, fa.Length % 2352);

        // Track B resumes exactly where the fade ended: its first sample is original sample [overlap], not sample 0 again.
        Assert.Equal(b.AsSpan(overlap * 4).ToArray(), fb);

        // Before the overlap, track A is untouched; at the end of the fade B dominates.
        Assert.Equal(a.AsSpan(0, (a.Length / 4 - overlap) * 4).ToArray(), fa.AsSpan(0, (a.Length / 4 - overlap) * 4).ToArray());
        short lastMixed = BinaryPrimitives.ReadInt16LittleEndian(fa.AsSpan(fa.Length - 4));
        short bAtFadeEnd = BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan((overlap - 1) * 4));
        Assert.InRange(lastMixed, bAtFadeEnd - 50, bAtFadeEnd + 50);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-3.0)]
    [InlineData(double.NaN)]
    [InlineData(60.0)]
    public void Crossfade_DegenerateDurations_NeverCorruptTracks(double seconds)
    {
        byte[] a = ConstantPcm(3 * 588, 500);
        byte[] b = ConstantPcm(3 * 588, -500);
        var (fa, fb) = RedBookPcmDspProcessor.ApplyCrossfadeBetweenTracks(a, b, seconds);

        Assert.Equal(0, fa.Length % 2352);
        Assert.Equal(0, fb.Length % 2352);
        Assert.True(fb.Length > 0);
        Assert.True(fa.Length + fb.Length <= a.Length + b.Length);
    }

    [Fact]
    public void PadToSectorBoundary_FromOffsetSliceStream_PadsOnlyTheSlice()
    {
        byte[] backing = Enumerable.Range(0, 10_000).Select(i => (byte)i).ToArray();
        using var slice = new MemoryStream(backing, 1000, 3000, writable: false, publiclyVisible: true);

        using var padded = RedBookPcmDspProcessor.PadToRedBookSectorBoundary(slice);
        byte[] result = ReadAll(padded);

        Assert.Equal(2 * 2352, result.Length);
        Assert.Equal(backing.AsSpan(1000, 3000).ToArray(), result.AsSpan(0, 3000).ToArray());
        Assert.True(result.AsSpan(3000).ToArray().All(x => x == 0));
    }

    #endregion

    #region Legacy images: Mode 2 XA, pre-gaps, subchannel, streaming view

    [Fact]
    public async Task Legacy_Mode2Form1RawImg_UsesUserDataAtOffset24()
    {
        byte[] iso = await BuildIsoAsync();
        string img = Path.Combine(_dir, "psx.img");
        File.WriteAllBytes(img, WrapRaw(iso, mode: 2, sectorSize: 2352));

        var info = LegacyDiscImageConverter.Inspect(img);
        Assert.Equal(2352, info.SectorSizeBytes);
        Assert.Equal(iso.Length / 2048, info.SectorCount);

        string outIso = Path.Combine(_dir, "psx.iso");
        await LegacyDiscImageConverter.ConvertToIsoAsync(img, outIso);
        Assert.Equal(iso, File.ReadAllBytes(outIso));
    }

    [Fact]
    public async Task Legacy_CloneCdMode2_ConvertsAndStreamsIdentically()
    {
        byte[] iso = await BuildIsoAsync();
        string ccd = Path.Combine(_dir, "vcd.ccd");
        File.WriteAllBytes(Path.ChangeExtension(ccd, ".img"), WrapRaw(iso, mode: 2, sectorSize: 2352));
        File.WriteAllText(ccd, "[CloneCD]\nVersion=3\n[Track 1]\nMODE=2\nINDEX 1=0\n");

        using var stream = LegacyDiscImageConverter.OpenIsoUserDataStream(ccd);
        Assert.Equal(iso, ReadAll(stream));
    }

    [Fact]
    public async Task Legacy_NeroDaoImageWithStoredPregap_SkipsThe150SectorPregap()
    {
        byte[] iso = await BuildIsoAsync();
        byte[] withPregap = new byte[(150 * 2048) + iso.Length];
        iso.CopyTo(withPregap, 150 * 2048);
        string nrg = Path.Combine(_dir, "dao.nrg");
        LegacyDiscImageConverter.CreateNrgFile(nrg, withPregap);

        var info = LegacyDiscImageConverter.Inspect(nrg);
        Assert.Equal(150L * 2048, info.PayloadOffsetBytes);
        Assert.Equal(iso.Length / 2048, info.SectorCount);

        using var stream = LegacyDiscImageConverter.OpenIsoUserDataStream(info);
        Assert.Equal(iso, ReadAll(stream));
    }

    [Fact]
    public async Task Legacy_AlcoholMdfWithSubchannel2448_IsDetectedAndConverted()
    {
        byte[] iso = await BuildIsoAsync();
        string mds = Path.Combine(_dir, "sub.mds");
        LegacyDiscImageConverter.CreateAlcoholMdsMdfImage(mds, Array.Empty<byte>());
        File.WriteAllBytes(Path.ChangeExtension(mds, ".mdf"), WrapRaw(iso, mode: 1, sectorSize: 2448));

        var info = LegacyDiscImageConverter.Inspect(mds);
        Assert.Equal(2448, info.SectorSizeBytes);

        using var stream = LegacyDiscImageConverter.OpenIsoUserDataStream(info);
        Assert.Equal(iso, ReadAll(stream));
    }

    [Fact]
    public async Task Legacy_UserDataStream_SupportsRandomSeeksAcrossSectorBoundaries()
    {
        byte[] iso = await BuildIsoAsync();
        string img = Path.Combine(_dir, "seek.img");
        File.WriteAllBytes(img, WrapRaw(iso, mode: 1, sectorSize: 2352));

        using var stream = LegacyDiscImageConverter.OpenIsoUserDataStream(img);
        Assert.True(stream.CanSeek);
        Assert.Equal(iso.Length, stream.Length);

        var rng = new Random(5);
        byte[] buf = new byte[5000];
        for (int k = 0; k < 50; k++)
        {
            long pos = rng.NextInt64(0, iso.Length - buf.Length);
            stream.Position = pos;
            Assert.Equal(buf.Length, stream.Read(buf, 0, buf.Length));
            Assert.Equal(iso.AsSpan((int)pos, buf.Length).ToArray(), buf);
        }

        stream.Seek(-10, SeekOrigin.End);
        Assert.Equal(10, stream.Read(buf, 0, buf.Length));
    }

    [Theory]
    [InlineData("MODE=1\n[Track 2]\nMODE=0", false)] // mixed-mode: data track + audio
    [InlineData("MODE=0\n[Track 2]\nMODE=0", true)]  // pure audio
    public void Legacy_CloneCdAudioDetection_RequiresEveryTrackToBeAudio(string tracks, bool expectedAudio)
    {
        string ccd = Path.Combine(_dir, $"mix_{expectedAudio}.ccd");
        File.WriteAllBytes(Path.ChangeExtension(ccd, ".img"), new byte[2352 * 20]);
        File.WriteAllText(ccd, $"[CloneCD]\nVersion=3\n[Track 1]\n{tracks}\n");

        Assert.Equal(expectedAudio, LegacyDiscImageConverter.Inspect(ccd).IsAudioDisc);
    }

    #endregion

    #region Playlists

    [Fact]
    public void Playlist_AnsiEncodedM3u_ResolvesAccentedPaths()
    {
        string trackPath = Path.Combine(_dir, "Beyoncé - Halo.flac");
        File.WriteAllBytes(trackPath, new byte[] { 1 });

        byte[] ansi = Encoding.Latin1.GetBytes("#EXTM3U\r\n#EXTINF:261,Beyoncé - Halo\r\nBeyoncé - Halo.flac\r\n");
        string m3u = Path.Combine(_dir, "ansi.m3u");
        File.WriteAllBytes(m3u, ansi);

        var entries = AudioPlaylistParser.ParseFile(m3u);
        Assert.Single(entries);
        Assert.Equal(trackPath, entries[0].FilePath);
        Assert.True(File.Exists(entries[0].FilePath));
        Assert.Equal("Beyoncé", entries[0].Performer);
    }

    [Fact]
    public void Playlist_LineWithInvalidPathCharacters_IsSkippedNotFatal()
    {
        string content = "#EXTM3U\nfirst.flac\nbad\0name.flac\nthird.flac\n";
        var entries = AudioPlaylistParser.ParseM3uContent(content, _dir);

        Assert.Equal(2, entries.Count);
        Assert.EndsWith("first.flac", entries[0].FilePath);
        Assert.EndsWith("third.flac", entries[1].FilePath);
    }

    #endregion

    #region v1.10: dither and WAV through the shared converter

    [Fact]
    public void Dither_KeepsDetailBelow16Bits_InsteadOfTruncatingItToSilence()
    {
        // A 1 kHz tone at a quarter of one 16-bit step: plain rounding turns it into pure digital silence.
        const int n = 44100;
        var tone = new int[n];
        for (int i = 0; i < n; i++) tone[i] = (int)Math.Round(64 * Math.Sin(2 * Math.PI * 1000 * i / 44100.0)); // 24-bit units
        var conv = new RedBookPcmConverter(44100, n);
        conv.WriteBlock(new[] { tone, tone }, 2, 24, n);
        var (l, _) = SplitStereo(conv.Finish());

        Assert.Contains(l, s => s != 0);
        Assert.All(l, s => Assert.InRange(s, -2, 2));

        // The dithered output still carries the tone: it correlates positively with the input.
        double correlation = 0;
        for (int i = 0; i < n; i++) correlation += l[i] * Math.Sin(2 * Math.PI * 1000 * i / 44100.0);
        Assert.True(correlation > 0.1 * n * 0.25, $"Tone lost under dither (correlation {correlation:F0}).");
    }

    [Fact]
    public void Dither_LeavesExactSamplesAndDigitalSilenceUntouched_AndIsDeterministic()
    {
        const int n = 4096;
        var exact = Enumerable.Range(0, n).Select(i => ((i % 200) - 100) << 8).ToArray(); // 24-bit with zero low byte
        var silence = new int[n];

        var c1 = new RedBookPcmConverter(44100);
        c1.WriteBlock(new[] { exact, silence }, 2, 24, n);
        var (l, r) = SplitStereo(c1.Finish());
        Assert.Equal(exact.Select(v => (short)(v >> 8)), l);
        Assert.All(r, s => Assert.Equal(0, s));

        var noisy = Enumerable.Range(0, n).Select(i => (i * 7919) % 100_000 - 50_000).ToArray();
        byte[] Run()
        {
            var c = new RedBookPcmConverter(48000);
            c.WriteBlock(new[] { noisy, noisy }, 2, 24, n);
            return c.Finish();
        }
        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void Wav96k24Bit_UsesBandLimitedConversion_NotLinearInterpolation()
    {
        string path = Path.Combine(_dir, "hires.wav");
        int n = 96000;
        byte[] data = new byte[n * 2 * 3];
        for (int i = 0; i < n; i++)
        {
            int v = (int)(0.5 * 8_388_607 * Math.Sin(2 * Math.PI * 30000 * i / 96000.0)); // ultrasonic
            for (int c = 0; c < 2; c++)
            {
                int o = (i * 2 + c) * 3;
                data[o] = (byte)v; data[o + 1] = (byte)(v >> 8); data[o + 2] = (byte)(v >> 16);
            }
        }
        WavFileCodec.WriteWavFile(path, data, 96000, 2, 24);

        var (l, _) = SplitStereo(WavFileCodec.DecodeToRedBookPcm(path));

        Assert.Equal(44100, l.Length);
        double inputRms = 0.5 * 32768 / Math.Sqrt(2);
        Assert.True(Rms(l, 4410, l.Length - 4410) < inputRms * 0.01, "30 kHz content aliased into the audible band.");
    }

    [Fact]
    public void WavSixChannel_KeepsTheCentreChannel()
    {
        string path = Path.Combine(_dir, "surround.wav");
        int n = 48000;
        byte[] data = new byte[n * 6 * 2];
        for (int i = 0; i < n; i++)
        {
            short v = (short)(0.5 * 32767 * Math.Sin(2 * Math.PI * 1000 * i / 48000.0));
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(((i * 6) + 2) * 2), v); // centre only
        }
        WavFileCodec.WriteWavFile(path, data, 48000, 6, 16);

        var (l, r) = SplitStereo(WavFileCodec.DecodeToRedBookPcm(path));

        Assert.Equal(44100, l.Length);
        Assert.True(Rms(l, 4410, 39690) > 1000 && Rms(r, 4410, 39690) > 1000, "Centre channel was dropped.");
    }

    [Fact]
    public void WavFloat32_44k_DecodesToFullScaleWithoutWrapping()
    {
        string path = Path.Combine(_dir, "float.wav");
        int n = 1000;
        byte[] data = new byte[n * 2 * 4];
        for (int i = 0; i < n * 2; i++)
        {
            float f = i % 4 < 2 ? 1.5f : -1.0f; // includes an out-of-range +1.5 that must clamp, not wrap
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(i * 4), f);
        }
        byte[] header = WavFileCodec.CreateWavHeader(data.Length, 44100, 2, 32);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20, 2), 3); // WAVE_FORMAT_IEEE_FLOAT
        File.WriteAllBytes(path, header.Concat(data).ToArray());

        var (l, r) = SplitStereo(WavFileCodec.DecodeToRedBookPcm(path));

        Assert.Equal(n, l.Length);
        Assert.All(l.Where((_, i) => i % 2 == 0), s => Assert.Equal(short.MaxValue, s));
        Assert.All(l.Where((_, i) => i % 2 == 1), s => Assert.Equal(short.MinValue, s));
    }

    #endregion

    #region Helpers

    private static void AssertWithinOneLsb(byte[] expected, byte[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i += 2)
        {
            int e = BinaryPrimitives.ReadInt16LittleEndian(expected.AsSpan(i));
            int a = BinaryPrimitives.ReadInt16LittleEndian(actual.AsSpan(i));
            if (Math.Abs(e - a) > 1)
            {
                Assert.Fail($"Sample {i / 2}: expected {e} ±1, got {a}.");
            }
        }
    }

    private static (int[] L, int[] R) MakeStereoSignal(int n, int bits, int seed)
    {
        var rng = new Random(seed);
        int max = (1 << (bits - 1)) - 1;
        var l = new int[n];
        var r = new int[n];
        for (int i = 0; i < n; i++)
        {
            double s = Math.Sin(2 * Math.PI * 440 * i / 44100.0) * 0.6 + Math.Sin(2 * Math.PI * 3100 * i / 44100.0) * 0.2;
            double c = Math.Cos(2 * Math.PI * 660 * i / 44100.0) * 0.5;
            l[i] = Math.Clamp((int)(s * max) + rng.Next(-200, 200), -max - 1, max);
            r[i] = Math.Clamp((int)(c * max) + rng.Next(-200, 200), -max - 1, max);
        }

        // A few full-scale opposite-polarity samples so the side channel needs bps + 1 bits.
        for (int i = 100; i < 110; i++)
        {
            l[i] = max;
            r[i] = -max - 1;
        }
        return (l, r);
    }

    private static byte[] ExpectedRedBook(int[] l, int[] r, int bits)
    {
        byte[] pcm = new byte[l.Length * 4];
        for (int i = 0; i < l.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4), To16(l[i], bits));
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 2), To16(r[i], bits));
        }
        return pcm;

        static short To16(int v, int bits) => bits == 16
            ? (short)v
            : (short)Math.Clamp(((long)v + (1L << (bits - 17))) >> (bits - 16), short.MinValue, short.MaxValue);
    }

    private static (int[][] Channels, int N) Tone(int rate, int n, double hz, double amp, int bits)
    {
        int max = (1 << (bits - 1)) - 1;
        var l = new int[n];
        for (int i = 0; i < n; i++) l[i] = (int)(amp * max * Math.Sin(2 * Math.PI * hz * i / rate));
        return (new[] { l, (int[])l.Clone() }, n);
    }

    private static (short[] L, short[] R) SplitStereo(byte[] pcm)
    {
        int n = pcm.Length / 4;
        var l = new short[n];
        var r = new short[n];
        for (int i = 0; i < n; i++)
        {
            l[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 4));
            r[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 4 + 2));
        }
        return (l, r);
    }

    private static double Rms(short[] s, int from, int to)
    {
        double sum = 0;
        for (int i = from; i < to; i++) sum += (double)s[i] * s[i];
        return Math.Sqrt(sum / Math.Max(1, to - from));
    }

    private static byte[] RandomPcm(int stereoFrames, int seed)
    {
        var b = new byte[stereoFrames * 4];
        new Random(seed).NextBytes(b);
        return b;
    }

    private static byte[] ConstantPcm(int stereoFrames, short value)
    {
        var b = new byte[stereoFrames * 4];
        for (int i = 0; i < stereoFrames * 2; i++) BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(i * 2), value);
        return b;
    }

    private static byte[] RampPcm(int stereoFrames)
    {
        var b = new byte[stereoFrames * 4];
        for (int i = 0; i < stereoFrames; i++)
        {
            short v = (short)(i % 30000);
            BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(i * 4), v);
            BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(i * 4 + 2), v);
        }
        return b;
    }

    private static byte[] ReadAll(Stream s)
    {
        using (s)
        {
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }

    private static byte[] BuildId3v2(byte versionMajor, byte flags, int declaredBodySize, int actualBodySize)
    {
        var tag = new byte[10 + actualBodySize];
        "ID3"u8.CopyTo(tag);
        tag[3] = versionMajor;
        tag[5] = flags;
        tag[6] = (byte)((declaredBodySize >> 21) & 0x7F);
        tag[7] = (byte)((declaredBodySize >> 14) & 0x7F);
        tag[8] = (byte)((declaredBodySize >> 7) & 0x7F);
        tag[9] = (byte)(declaredBodySize & 0x7F);
        return tag; // body (and any undeclared padding) is zeros
    }

    private static async Task<byte[]> BuildIsoAsync()
    {
        var root = CompilationDirectory.CreateRoot();
        root.AddFile("README.TXT", Encoding.ASCII.GetBytes(new string('x', 70_000)));
        using var ms = new MemoryStream();
        await IsoUdfImageBuilder.BuildAsync(root, ms, new IsoBuildOptions(VolumeLabel: "AUDIT_108"));
        return ms.ToArray();
    }

    /// <summary>
    /// Wraps 2048-byte sectors into raw sectors: Mode 1 user data at 16; Mode 2 Form 1 at 24 (after an 8-byte subheader);
    /// 2448-byte sectors append 96 bytes of subchannel.
    /// </summary>
    private static byte[] WrapRaw(byte[] iso, int mode, int sectorSize)
    {
        int sectors = iso.Length / 2048;
        var raw = new byte[sectors * sectorSize];
        for (int s = 0; s < sectors; s++)
        {
            var sec = raw.AsSpan(s * sectorSize, sectorSize);
            sec[0] = 0;
            sec.Slice(1, 10).Fill(0xFF);
            sec[11] = 0;
            sec[15] = (byte)mode;
            int userOffset = mode == 2 ? 24 : 16;
            if (mode == 2)
            {
                sec[18] = 0x08; sec[22] = 0x08; // subheader: data, Form 1 (copied twice)
            }
            iso.AsSpan(s * 2048, 2048).CopyTo(sec.Slice(userOffset));
            if (sectorSize == 2448) sec.Slice(2352).Fill(0xAA); // subchannel noise
        }
        return raw;
    }

    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, 7919)); // short reads
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    #endregion
}

/// <summary>
/// Minimal RFC 9639 FLAC encoder for tests: emits FIXED (order 0-4), LPC (order 2 and 8), or VERBATIM subframes with every
/// stereo decorrelation mode and optional wasted bits, so the decoder's predictor and channel arithmetic can be verified
/// bit-exactly (the production encoder only writes VERBATIM / independent channels).
/// </summary>
internal static class FlacTestEncoder
{
    public static byte[] Encode(int sampleRate, int bps, int[][] channels, int blockSize, int assignment, V108_AuditRegressionTests.Sub sub, int wastedBits = 0)
    {
        int nch = channels.Length;
        int total = channels[0].Length;
        var output = new List<byte>();
        output.AddRange("fLaC"u8.ToArray());

        var si = new byte[38];
        si[0] = 0x80;
        si[3] = 34;
        BinaryPrimitives.WriteUInt16BigEndian(si.AsSpan(4), (ushort)blockSize);
        BinaryPrimitives.WriteUInt16BigEndian(si.AsSpan(6), (ushort)blockSize);
        ulong packed = ((ulong)(uint)sampleRate << 44) | ((ulong)(uint)(nch - 1) << 41) | ((ulong)(uint)(bps - 1) << 36) | (uint)total;
        BinaryPrimitives.WriteUInt64BigEndian(si.AsSpan(14), packed);
        output.AddRange(si);

        int sizeCode = bps switch { 8 => 1, 12 => 2, 16 => 4, 20 => 5, 24 => 6, _ => 0 };
        int frameNo = 0;
        for (int start = 0; start < total; start += blockSize, frameNo++)
        {
            int n = Math.Min(blockSize, total - start);
            var w = new BitWriter();
            w.Write(0xFFF8, 16);
            w.Write(7, 4);
            w.Write(0, 4);
            int chCode = assignment == 1 ? nch - 1 : assignment;
            w.Write((ulong)chCode, 4);
            w.Write((ulong)sizeCode, 3);
            w.Write(0, 1);
            foreach (byte b in Utf8Number((ulong)frameNo)) w.Write(b, 8);
            w.Write((ulong)(n - 1), 16);
            w.Write(Crc8(w.ToArray()), 8);

            long[] l = channels[0].Skip(start).Take(n).Select(x => (long)x).ToArray();
            long[]? r = nch > 1 ? channels[1].Skip(start).Take(n).Select(x => (long)x).ToArray() : null;
            var subframes = new List<(long[] Data, int Bps)>();
            switch (assignment)
            {
                case 8: subframes.Add((l, bps)); subframes.Add((l.Zip(r!, (a, b) => a - b).ToArray(), bps + 1)); break;
                case 9: subframes.Add((l.Zip(r!, (a, b) => a - b).ToArray(), bps + 1)); subframes.Add((r!, bps)); break;
                case 10:
                    subframes.Add((l.Zip(r!, (a, b) => (a + b) >> 1).ToArray(), bps));
                    subframes.Add((l.Zip(r!, (a, b) => a - b).ToArray(), bps + 1));
                    break;
                default:
                    for (int c = 0; c < nch; c++) subframes.Add((channels[c].Skip(start).Take(n).Select(x => (long)x).ToArray(), bps));
                    break;
            }

            foreach (var (data, sbps) in subframes)
            {
                WriteSubframe(w, data, sbps, sub, wastedBits);
            }

            w.Align();
            byte[] frame = w.ToArray();
            output.AddRange(frame);
            ushort crc = Crc16(frame);
            output.Add((byte)(crc >> 8));
            output.Add((byte)crc);
        }

        return output.ToArray();
    }

    private static void WriteSubframe(BitWriter w, long[] samples, int bps, V108_AuditRegressionTests.Sub sub, int wasted)
    {
        int n = samples.Length;
        int typeCode = sub switch
        {
            V108_AuditRegressionTests.Sub.Verbatim => 1,
            V108_AuditRegressionTests.Sub.Lpc2 => 32 + 1,
            V108_AuditRegressionTests.Sub.Lpc8 => 32 + 7,
            _ => 8 + ((int)sub - (int)V108_AuditRegressionTests.Sub.Fixed0)
        };

        w.Write(0, 1);
        w.Write((ulong)typeCode, 6);
        if (wasted > 0)
        {
            w.Write(1, 1);
            w.WriteUnary(wasted - 1);
        }
        else
        {
            w.Write(0, 1);
        }

        int ebps = bps - wasted;
        long[] s = samples.Select(x => x >> wasted).ToArray();

        if (typeCode == 1)
        {
            foreach (long v in s) w.WriteSigned(v, ebps);
            return;
        }

        if (typeCode < 32)
        {
            int order = typeCode - 8;
            for (int i = 0; i < order; i++) w.WriteSigned(s[i], ebps);
            var res = new long[n - order];
            for (int i = order; i < n; i++)
            {
                long p = order switch
                {
                    0 => 0,
                    1 => s[i - 1],
                    2 => 2 * s[i - 1] - s[i - 2],
                    3 => 3 * s[i - 1] - 3 * s[i - 2] + s[i - 3],
                    _ => 4 * s[i - 1] - 6 * s[i - 2] + 4 * s[i - 3] - s[i - 4]
                };
                res[i - order] = s[i] - p;
            }
            WriteResidual(w, res);
            return;
        }

        int lpcOrder = typeCode - 31;
        (int[] coeffs, int precision, int shift) = lpcOrder == 2
            ? (new[] { 8192, -4096 }, 15, 12)
            : (new[] { 3000, -1500, 700, -300, 100, -50, 20, -5 }, 13, 11);
        for (int i = 0; i < lpcOrder; i++) w.WriteSigned(s[i], ebps);
        w.Write((ulong)(precision - 1), 4);
        w.WriteSigned(shift, 5);
        foreach (int c in coeffs) w.WriteSigned(c, precision);

        var lres = new long[n - lpcOrder];
        for (int i = lpcOrder; i < n; i++)
        {
            long sum = 0;
            for (int j = 0; j < lpcOrder; j++) sum += coeffs[j] * s[i - j - 1];
            lres[i - lpcOrder] = s[i] - (sum >> shift);
        }
        WriteResidual(w, lres);
    }

    private static void WriteResidual(BitWriter w, long[] res)
    {
        w.Write(1, 2); // RICE2 (5-bit parameters)
        w.Write(0, 4); // partition order 0
        double mean = res.Length == 0 ? 0 : res.Average(v => (double)Math.Abs(v));
        int k = mean < 1 ? 0 : Math.Min(30, (int)Math.Floor(Math.Log2(mean)));
        w.Write((ulong)k, 5);
        foreach (long v in res)
        {
            ulong u = v >= 0 ? (ulong)v << 1 : ((ulong)(-v) << 1) - 1;
            w.WriteUnary((int)(u >> k));
            w.Write(u & ((1UL << k) - 1), k);
        }
    }

    private static IEnumerable<byte> Utf8Number(ulong v)
    {
        if (v < 0x80) return new[] { (byte)v };
        if (v < 0x800) return new[] { (byte)(0xC0 | (v >> 6)), (byte)(0x80 | (v & 0x3F)) };
        return new[] { (byte)(0xE0 | (v >> 12)), (byte)(0x80 | ((v >> 6) & 0x3F)), (byte)(0x80 | (v & 0x3F)) };
    }

    private static byte Crc8(byte[] data)
    {
        byte crc = 0;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (crc & 0x80) != 0 ? (byte)((crc << 1) ^ 0x07) : (byte)(crc << 1);
        }
        return crc;
    }

    private static ushort Crc16(byte[] data)
    {
        ushort crc = 0;
        foreach (byte b in data)
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++) crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x8005) : (ushort)(crc << 1);
        }
        return crc;
    }

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = new();
        private int _acc;
        private int _bits;

        public void Write(ulong value, int count)
        {
            for (int i = count - 1; i >= 0; i--) Bit((int)((value >> i) & 1));
        }

        public void WriteSigned(long value, int count) => Write((ulong)value & (count == 64 ? ulong.MaxValue : (1UL << count) - 1), count);

        public void WriteUnary(int zeros)
        {
            for (int i = 0; i < zeros; i++) Bit(0);
            Bit(1);
        }

        public void Align()
        {
            while (_bits != 0) Bit(0);
        }

        public byte[] ToArray() => _bytes.ToArray();

        private void Bit(int b)
        {
            _acc = (_acc << 1) | b;
            if (++_bits == 8)
            {
                _bytes.Add((byte)_acc);
                _acc = 0;
                _bits = 0;
            }
        }
    }
}
