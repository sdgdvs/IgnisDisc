using System.Security.Cryptography;
using CdBurner.Core.Audio;
using Xunit;

namespace CdBurner.Core.Tests;

/// <summary>
/// Damaged FLAC input is found before it reaches a disc (audit finding F3, 2026-11-09). Damage is made the ways the
/// audit found it slipping through: a corrupt frame, a cut-short file, a bit flip in the header's length field, and a
/// corrupt first frame that used to shift all later audio 93 ms earlier.
/// </summary>
public class FlacIntegrityTests
{
    private const int Block = 4096;                 // samples per frame from FlacFileCodec.CreateFlacBytes
    private const int AudioStart = 4 + 4 + 34;      // "fLaC" + STREAMINFO header + STREAMINFO
    private const int Md5Offset = 4 + 4 + 18;       // the 16-byte MD5 inside STREAMINFO

    /// <summary>Noise-like 16-bit stereo PCM, <paramref name="blocks"/> frames long.</summary>
    private static byte[] Pcm(int blocks, int seed = 1)
    {
        var pcm = new byte[blocks * Block * 4];
        new Random(seed).NextBytes(pcm);
        return pcm;
    }

    private static FlacIntegrityReport Check(byte[] flac) => FlacFileCodec.CheckIntegrity(new MemoryStream(flac));

    /// <summary>Puts the real MD5 of <paramref name="pcm"/> (16-bit LE interleaved, as RFC 9639 defines it) in the header.</summary>
    private static byte[] WithMd5(byte[] flac, byte[] pcm)
    {
        MD5.HashData(pcm).CopyTo(flac, Md5Offset);
        return flac;
    }

    [Fact]
    public void IntactFile_IsNotDamaged()
    {
        var report = Check(FlacFileCodec.CreateFlacBytes(Pcm(3)));

        Assert.False(report.IsDamaged);
        Assert.Equal(3 * Block, report.DecodedSamples);
        Assert.Null(report.Md5Matches); // this encoder stores no MD5
        Assert.Equal("intact.", report.Describe());
    }

    [Fact]
    public void CorruptFrame_IsReportedAsConcealedSilence()
    {
        byte[] flac = FlacFileCodec.CreateFlacBytes(Pcm(3));
        flac[AudioStart + 6000] ^= 0x40; // inside the first frame's audio

        var report = Check(flac);

        Assert.True(report.IsDamaged);
        Assert.True(report.ConcealedSamples > 0);
        Assert.Contains("replaced by silence", report.Describe());
    }

    [Fact]
    public void CorruptFirstFrame_NoLongerShiftsTheRestOfTheAudio()
    {
        byte[] pcm = Pcm(3);
        byte[] flac = FlacFileCodec.CreateFlacBytes(pcm);
        flac[AudioStart + 100] ^= 0x01;

        byte[] decoded = FlacFileCodec.DecodeToRedBookPcm(new MemoryStream(flac));

        // The lost first frame becomes silence in its own place; frames 2 and 3 keep their positions.
        Assert.Equal(pcm.Length, decoded.Length);
        Assert.True(decoded.AsSpan(0, Block * 4).IndexOfAnyExcept((byte)0) < 0);
        Assert.True(decoded.AsSpan(Block * 4).SequenceEqual(pcm.AsSpan(Block * 4)));
    }

    [Fact]
    public void TruncatedFile_IsReportedAsCutShort()
    {
        byte[] flac = FlacFileCodec.CreateFlacBytes(Pcm(3));

        var report = Check(flac[..(flac.Length / 2)]);

        Assert.True(report.IsDamaged);
        Assert.True(report.MissingSamplesAtEnd > 0);
        Assert.Contains("cut short", report.Describe());
    }

    [Fact]
    public void BitFlipInTheHeaderLength_IsReported()
    {
        byte[] flac = FlacFileCodec.CreateFlacBytes(Pcm(2));
        flac[24] ^= 0x30; // 8192 samples -> 4096, the audit's example

        var report = Check(flac);

        Assert.True(report.IsDamaged);
        Assert.Equal(Block, report.SamplesBeyondStatedLength);
        Assert.Contains("length in its header is wrong", report.Describe());
    }

    [Fact]
    public void StoredMd5_IsCheckedBothWays()
    {
        byte[] pcm = Pcm(2);
        byte[] good = WithMd5(FlacFileCodec.CreateFlacBytes(pcm), pcm);
        var intact = Check(good);
        Assert.True(intact.Md5Matches);
        Assert.False(intact.IsDamaged);
        Assert.Equal("intact (MD5 matches).", intact.Describe());

        byte[] wrongMd5 = (byte[])good.Clone();
        wrongMd5[Md5Offset] ^= 0x01;
        var report = Check(wrongMd5);
        Assert.False(report.Md5Matches);
        Assert.True(report.IsDamaged);
        Assert.Contains("MD5", report.Describe());
    }

    [Fact]
    public void StrictDecode_ThrowsOnDamage_AndReturnsIntactAudioExactly()
    {
        byte[] pcm = Pcm(2);
        byte[] flac = FlacFileCodec.CreateFlacBytes(pcm);
        Assert.Equal(pcm, FlacFileCodec.DecodeToRedBookPcm(new MemoryStream(flac), strict: true));

        flac[AudioStart + 6000] ^= 0x40;
        var ex = Assert.Throws<InvalidDataException>(() => FlacFileCodec.DecodeToRedBookPcm(new MemoryStream(flac), strict: true));
        Assert.StartsWith("Damaged FLAC:", ex.Message);
    }
}
