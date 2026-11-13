using CdBurner.Core.Audio;
using Xunit;

namespace CdBurner.Core.Tests;

/// <summary>
/// A FLAC metadata block's length is 24 bits (RFC 9639 8.1). The rip's FLAC writer put a cover image of any size into
/// that field: past about 16 MB the length wrapped and the file was corrupt although the rip reported success. It now
/// refuses such a block, and the rip checks the cover's size before the disc is read.
/// </summary>
public class FlacCoverArtSizeTests
{
    private static readonly byte[] OneSecondOfSilence = new byte[44_100 * 4];

    private static byte[] Jpeg(int length)
    {
        var data = new byte[length];
        data[0] = 0xFF; data[1] = 0xD8; data[2] = 0xFF; // JPEG signature
        data[^1] = 0x5A;
        return data;
    }

    private static AudioTrackTags Tags(byte[] cover) =>
        new("Title", "Artist", "Album", 1, 1, CoverArtBytes: cover, CoverArtMimeType: "image/jpeg");

    [Fact]
    public void CoverAtTheLimit_IsEmbedded_AndReadsBackWhole()
    {
        byte[] cover = Jpeg(FlacEncoder.MaxCoverArtBytes);

        byte[] flac = FlacFileCodec.CreateFlacBytes(OneSecondOfSilence, tags: Tags(cover));

        var meta = FlacFileCodec.ProbeMetadata(new MemoryStream(flac));
        Assert.True(meta.HasEmbeddedCoverArt);
        Assert.Equal(cover.Length, meta.CoverArtBytes!.Length);
        Assert.Equal(0x5A, meta.CoverArtBytes[^1]);
        Assert.Equal(OneSecondOfSilence, FlacFileCodec.DecodeToRedBookPcm(new MemoryStream(flac)));
    }

    [Fact]
    public void CoverPastTheFlacLimit_IsRefused_InsteadOfWritingACorruptFile()
    {
        byte[] cover = Jpeg(FlacEncoder.MaxMetadataBlockLength);

        var ex = Assert.Throws<InvalidDataException>(() => FlacFileCodec.CreateFlacBytes(OneSecondOfSilence, tags: Tags(cover)));
        Assert.Contains("Choose a smaller image", ex.Message);
    }

    [Fact]
    public void SizeCheckBeforeTheRip_RefusesOnlyImagesThatCannotFit()
    {
        Assert.Null(FlacEncoder.CoverArtSizeProblem(5 * 1024 * 1024));
        Assert.Null(FlacEncoder.CoverArtSizeProblem(FlacEncoder.MaxCoverArtBytes));
        Assert.Contains("16.0 MB", FlacEncoder.CoverArtSizeProblem(FlacEncoder.MaxCoverArtBytes + 1));
    }
}
