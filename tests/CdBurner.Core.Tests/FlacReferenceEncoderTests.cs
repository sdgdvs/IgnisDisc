using System.Security.Cryptography;
using CdBurner.Core.Audio;
using Xunit;

namespace CdBurner.Core.Tests;

/// <summary>
/// Files made by the official xiph.org <c>flac.exe</c> (see Fixtures/Flac/README.md) decode sample-exact. The proof is
/// the MD5 that flac.exe stored in each file's STREAMINFO block over the original samples: it comes from the reference
/// encoder, not from this codebase.
/// </summary>
public class FlacReferenceEncoderTests
{
    private const int SourceSamples = 6000;
    private const int Md5Offset = 4 + 4 + 18; // "fLaC" + STREAMINFO block header + 18 bytes before the MD5

    public static TheoryData<string, int, int, int> Fixtures => new()
    {
        { "flac_s16_stereo_44k_b4096.flac", 2, 16, 44100 },
        { "flac_s16_stereo_44k_b1152.flac", 2, 16, 44100 },
        { "flac_s16_stereo_44k_b192.flac", 2, 16, 44100 },
        { "flac_s16_mono_44k_b4608.flac", 1, 16, 44100 },
        { "flac_s16_mono_44k_b576.flac", 1, 16, 44100 },
        { "flac_s24_stereo_48k_b4096.flac", 2, 24, 48000 },
        { "flac_s24_stereo_48k_b1024.flac", 2, 24, 48000 },
        { "flac_s24_mono_96k_b4096.flac", 1, 24, 96000 },
    };

    private static string PathOf(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Flac", name);

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void DecodedSamples_MatchTheMd5FlacExeStored(string name, int channels, int bits, int rate)
    {
        byte[] file = File.ReadAllBytes(PathOf(name));
        Assert.Contains(file[Md5Offset..(Md5Offset + 16)], b => b != 0); // the reference encoder did store an MD5

        var report = FlacFileCodec.CheckIntegrity(new MemoryStream(file));

        Assert.Equal(SourceSamples, report.DecodedSamples);
        Assert.Equal(0, report.ConcealedSamples);
        Assert.True(report.Md5Matches, "decoded samples differ from the MD5 flac.exe computed over the original audio");
        Assert.False(report.IsDamaged);

        var meta = FlacFileCodec.ProbeMetadata(PathOf(name));
        Assert.Equal(channels, meta.Channels);
        Assert.Equal(bits, meta.BitsPerSample);
        Assert.Equal(rate, meta.SampleRate);
        Assert.Equal(SourceSamples, meta.TotalSamples);
    }

    [Theory]
    [InlineData("flac_s16_stereo_44k_b4096.flac")]
    [InlineData("flac_s16_stereo_44k_b1152.flac")]
    [InlineData("flac_s16_stereo_44k_b192.flac")]
    public void SixteenBitStereo44k_RedBookOutputIsTheOriginalPcm(string name)
    {
        // For 44.1 kHz 16-bit stereo the Red Book bytes are the source samples unchanged, so their MD5 is flac.exe's.
        byte[] file = File.ReadAllBytes(PathOf(name));
        byte[] stored = file[Md5Offset..(Md5Offset + 16)];

        byte[] pcm = FlacFileCodec.DecodeToRedBookPcm(new MemoryStream(file));

        Assert.Equal(SourceSamples * 4, pcm.Length);
        Assert.Equal(Convert.ToHexString(stored), Convert.ToHexString(MD5.HashData(pcm)));
    }
}
