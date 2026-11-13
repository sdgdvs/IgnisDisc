using System.Buffers.Binary;
using CdBurner.Core.Audio;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Audio;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// Loudness normalization to -18 LUFS with a -1 dBTP true-peak limiter. Reference levels come from the EBU Tech 3341
/// test signals (1 kHz stereo sine at -23 / -33 dBFS measures -23 / -33 LUFS; -36/-23/-36 dBFS for 10/60/10 s
/// measures -23 LUFS) and ITU-R BS.1770-4.
/// </summary>
public sealed class LoudnessNormalizationTests : IDisposable
{
    private const double CeilingLinear = 0.8912509381337456; // -1 dBTP
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "IgnisDisc_Loudness_" + Guid.NewGuid().ToString("N"));

    public LoudnessNormalizationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static byte[] StereoSine(double dbfs, double seconds, double frequency = 1000.0)
    {
        int frames = (int)(seconds * 44_100);
        double amplitude = Math.Pow(10.0, dbfs / 20.0) * 32767.0;
        byte[] pcm = new byte[frames * 4];
        for (int i = 0; i < frames; i++)
        {
            short s = (short)Math.Round(amplitude * Math.Sin(2 * Math.PI * frequency * i / 44_100.0));
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4, 2), s);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 2, 2), s);
        }

        return pcm;
    }

    private static double LoudnessOf(byte[] pcm) => ReplayGainCalculator.CalculateTrackReplayGain(pcm).IntegratedLoudnessLufs;

    private static int SamplePeak(byte[] pcm)
    {
        int peak = 0;
        for (int i = 0; i < pcm.Length / 2; i++)
        {
            peak = Math.Max(peak, Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2, 2))));
        }
        return peak;
    }

    private static byte[] Normalize(byte[] pcm)
        => RedBookPcmDspProcessor.ApplyLoudnessGain(pcm, RedBookPcmDspProcessor.MeasureLoudnessGainDb(pcm));

    [Theory]
    [InlineData(-23.0)]
    [InlineData(-33.0)]
    public void EbuSineSignals_MeasureMinus18AfterProcessing(double dbfs)
    {
        byte[] result = Normalize(StereoSine(dbfs, 20));

        Assert.InRange(LoudnessOf(result), -18.5, -17.5);
        Assert.True(RedBookPcmDspProcessor.MeasureTruePeak(result) <= CeilingLinear);
    }

    [Fact]
    public void EbuGatedSignal_MeasuresMinus18AfterProcessing()
    {
        byte[] pcm = [.. StereoSine(-36, 10), .. StereoSine(-23, 60), .. StereoSine(-36, 10)];

        byte[] result = Normalize(pcm);

        Assert.InRange(LoudnessOf(result), -18.5, -17.5);
    }

    [Fact]
    public void SparsePeaks_AreLimitedToTheTruePeakCeiling_WithoutMovingLoudness()
    {
        // A quiet sine with a loud click every second: the +12 dB gain would push each click far over the ceiling.
        byte[] pcm = StereoSine(-30, 20);
        for (int second = 1; second < 20; second++)
        {
            int offset = second * 44_100 * 4;
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(offset, 2), 16000);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(offset + 2, 2), -16000);
        }

        byte[] result = Normalize(pcm);

        Assert.True(SamplePeak(result) <= CeilingLinear * 32768.0);
        Assert.True(RedBookPcmDspProcessor.MeasureTruePeak(result) <= CeilingLinear);
        Assert.InRange(LoudnessOf(result), -18.5, -17.5);
    }

    [Fact]
    public void InterSamplePeak_IsCaughtByOversampling()
    {
        // A sine at fs/4 with a 45 degree phase has every sample at 0.707 of its true peak: samples under the
        // ceiling can still reconstruct above it.
        int frames = 44_100 * 3;
        byte[] pcm = new byte[frames * 4];
        for (int i = 0; i < frames; i++)
        {
            short s = (short)Math.Round(32767 * 0.98 * Math.Sin(Math.PI / 2 * i + Math.PI / 4));
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4, 2), s);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 4 + 2, 2), s);
        }

        Assert.True(SamplePeak(pcm) < 0.75 * 32768); // samples alone look safe
        byte[] result = RedBookPcmDspProcessor.ApplyLoudnessGain(pcm, 2.0);

        Assert.True(RedBookPcmDspProcessor.MeasureTruePeak(result) <= CeilingLinear);
    }

    [Fact]
    public void DigitalSilence_StaysExactlyZero()
    {
        byte[] pcm = [.. new byte[44_100 * 4], .. StereoSine(-30, 5)];

        byte[] result = Normalize(pcm);

        Assert.All(result.Take(44_100 * 4), b => Assert.Equal(0, b));
    }

    private static async Task<List<byte[]>> BurnAndRip(AudioCdCompilation compilation, string folder)
    {
        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);
        var engine = new VirtualBurnEngine(recorder);

        var burn = await new AudioCdBurnService(engine).BurnCompilationAsync(recorder, compilation);
        Assert.True(burn.Success, burn.ErrorMessage);

        var rip = await new AudioCdRipperService().RipAudioCdAsync(recorder, engine, folder);
        Assert.True(rip.Success);
        return rip.RippedFilePaths.Select(WavFileCodec.DecodeToRedBookPcm).ToList();
    }

    private static AudioCdCompilation TwoTrackCompilation(byte[] quiet, byte[] loud, LoudnessNormalizationMode mode)
    {
        var compilation = new AudioCdCompilation
        {
            RecordingMode = AudioRecordingMode.DiscAtOnceGapless,
            LoudnessNormalization = mode
        };
        compilation.AddTrack(new AudioCdTrackItem(1, string.Empty, "Quiet", "A", CustomPcmStreamFactory: () => new MemoryStream(quiet)));
        compilation.AddTrack(new AudioCdTrackItem(2, string.Empty, "Loud", "A", CustomPcmStreamFactory: () => new MemoryStream(loud)));
        return compilation;
    }

    [Fact]
    public async Task Burn_PerTrack_BringsEveryTrackToMinus18()
    {
        byte[] quiet = StereoSine(-33, 12);
        byte[] loud = StereoSine(-23, 12);

        var tracks = await BurnAndRip(TwoTrackCompilation(quiet, loud, LoudnessNormalizationMode.PerTrack), Path.Combine(_dir, "track"));

        Assert.Equal(2, tracks.Count);
        foreach (byte[] t in tracks)
        {
            Assert.InRange(LoudnessOf(t), -18.5, -17.5);
            Assert.True(RedBookPcmDspProcessor.MeasureTruePeak(t) <= CeilingLinear);
        }
    }

    [Fact]
    public async Task Burn_PerAlbum_UsesOneGain_AndKeepsTheLevelDifference()
    {
        byte[] quiet = StereoSine(-33, 12);
        byte[] loud = StereoSine(-23, 12);

        var tracks = await BurnAndRip(TwoTrackCompilation(quiet, loud, LoudnessNormalizationMode.PerAlbum), Path.Combine(_dir, "album"));

        // Both blocks pass the relative gate (-10 LU below the mean: -33 sits 10 LU under -23 but the mean of the
        // two is about -25.6, so the quiet track stays in). Album loudness 10*log10((1+0.1)/2) = -2.6 dB re the
        // loud track = -25.6 LUFS, gain +7.6 dB: the tracks end at -15.4 and -25.4 LUFS, still 10 LU apart.
        double a = LoudnessOf(tracks[0]);
        double b = LoudnessOf(tracks[1]);
        Assert.InRange(b - a, 9.5, 10.5);
        Assert.InRange(b, -15.4 - 0.5, -15.4 + 0.5);
    }

    [Fact]
    public async Task Burn_WithOptionOff_LeavesAudioBitExact()
    {
        byte[] quiet = StereoSine(-33, 12);
        byte[] loud = StereoSine(-23, 12);

        var tracks = await BurnAndRip(TwoTrackCompilation(quiet, loud, LoudnessNormalizationMode.Off), Path.Combine(_dir, "off"));

        Assert.Equal(quiet, tracks[0].AsSpan(0, quiet.Length).ToArray());
        Assert.Equal(loud, tracks[1].AsSpan(0, loud.Length).ToArray());
    }

    [Theory]
    [InlineData(LoudnessNormalizationMode.Off)]
    [InlineData(LoudnessNormalizationMode.PerTrack)]
    [InlineData(LoudnessNormalizationMode.PerAlbum)]
    public void ProjectFile_KeepsTheLoudnessMode(LoudnessNormalizationMode mode)
    {
        var compilation = new AudioCdCompilation { LoudnessNormalization = mode };

        string json = CdBurner.Core.Persistence.ProjectSerializer.ToJson(
            CdBurner.Core.Persistence.ProjectSerializer.FromAudioCdCompilation(compilation));
        var reloaded = CdBurner.Core.Persistence.ProjectSerializer.ToAudioCdCompilation(
            CdBurner.Core.Persistence.ProjectSerializer.FromJson(json));

        Assert.Equal(mode, reloaded.LoudnessNormalization);
        Assert.Equal(LoudnessNormalizationMode.Off, new AudioCdCompilation().LoudnessNormalization);
    }
}
