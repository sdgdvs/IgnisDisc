using System.Text;
using CdBurner.Core.Audio;
using Xunit;

namespace CdBurner.Core.Tests;

/// <summary>
/// MP3 tags for CD-Text, built byte by byte from the id3.org ID3v2.2, v2.3, v2.4 and ID3v1 layouts.
/// Before this reader existed, MP3 titles never reached CD-Text: a real album burned with no CD-Text at all.
/// </summary>
public class Id3TagReaderTests
{
    // ---- builders following the specifications ----

    private static byte[] Synchsafe(int value) =>
        [(byte)((value >> 21) & 0x7F), (byte)((value >> 14) & 0x7F), (byte)((value >> 7) & 0x7F), (byte)(value & 0x7F)];

    private static byte[] BigEndian(int value) =>
        [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static byte[] Latin1Text(string s) => [0, .. Encoding.Latin1.GetBytes(s)];
    private static byte[] Utf8Text(string s) => [3, .. Encoding.UTF8.GetBytes(s)];
    private static byte[] Utf16BomText(string s) => [1, 0xFF, 0xFE, .. Encoding.Unicode.GetBytes(s), 0, 0];

    private static byte[] FrameV23(string id, byte[] data, byte formatFlags = 0) =>
        [.. Encoding.ASCII.GetBytes(id), .. BigEndian(data.Length), 0, formatFlags, .. data];

    private static byte[] FrameV24(string id, byte[] data, byte formatFlags = 0) =>
        [.. Encoding.ASCII.GetBytes(id), .. Synchsafe(data.Length), 0, formatFlags, .. data];

    private static byte[] FrameV22(string id, byte[] data) =>
        [.. Encoding.ASCII.GetBytes(id), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length, .. data];

    private static byte[] Tag(int major, byte flags, byte[] body, int padding = 32) =>
        [(byte)'I', (byte)'D', (byte)'3', (byte)major, 0, flags, .. Synchsafe(body.Length + padding), .. body, .. new byte[padding]];

    /// <summary>Some fake MPEG audio after the tag, so the tag is not at the end of the file.</summary>
    private static readonly byte[] Audio = [0xFF, 0xFB, 0x90, 0x64, .. new byte[400]];

    private static AudioTags? Read(params byte[][] parts) =>
        Id3TagReader.TryRead(new MemoryStream(parts.SelectMany(p => p).ToArray()));

    private static byte[] V1(string title, string artist, string album)
    {
        var tag = new byte[128];
        Encoding.ASCII.GetBytes("TAG").CopyTo(tag, 0);
        Encoding.Latin1.GetBytes(title).CopyTo(tag, 3);
        Encoding.Latin1.GetBytes(artist).CopyTo(tag, 33);
        Encoding.Latin1.GetBytes(album).CopyTo(tag, 63);
        return tag;
    }

    // ---- tests ----

    [Fact]
    public void V23_Latin1_ReadsTitleArtistAlbumAndAlbumArtist()
    {
        byte[] body =
        [
            .. FrameV23("TIT2", Latin1Text("To the Ground")),
            .. FrameV23("TPE1", Latin1Text("Schaffer, Riley, and the Double Ice Backfire")),
            .. FrameV23("TALB", Latin1Text("Hold Person")),
            .. FrameV23("TPE2", Latin1Text("Album Band")),
            .. FrameV23("TCOM", Latin1Text("A. Composer")),
            .. FrameV23("TEXT", Latin1Text("A. Lyricist")),
        ];

        var tags = Read(Tag(3, 0, body), Audio);

        Assert.NotNull(tags);
        Assert.Equal("To the Ground", tags.Title);
        Assert.Equal("Schaffer, Riley, and the Double Ice Backfire", tags.Artist);
        Assert.Equal("Hold Person", tags.Album);
        Assert.Equal("Album Band", tags.AlbumArtist);
        Assert.Equal("A. Composer", tags.Composer);
        Assert.Equal("A. Lyricist", tags.Songwriter);
    }

    [Fact]
    public void V23_Utf16WithByteOrderMark_KeepsAccentsAndPunctuation()
    {
        byte[] body = [.. FrameV23("TIT2", Utf16BomText("Café ñandú — 'Jesus (Genesis Remix)"))];

        Assert.Equal("Café ñandú — 'Jesus (Genesis Remix)", Read(Tag(3, 0, body), Audio)?.Title);
    }

    [Fact]
    public void V23_Utf16BigEndianByteOrderMark_IsHonoured()
    {
        byte[] data = [1, 0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("Ünïcode"), 0, 0];

        Assert.Equal("Ünïcode", Read(Tag(3, 0, FrameV23("TIT2", data)), Audio)?.Title);
    }

    [Fact]
    public void V24_Utf8_UsesSynchsafeFrameSizes()
    {
        // A 200-byte title: its synchsafe size (00 00 01 48) differs from the plain size (00 00 00 C8),
        // so reading it with the wrong rule would lose this frame and the next one.
        string longTitle = new string('x', 190) + " ending";
        byte[] body =
        [
            .. FrameV24("TIT2", Utf8Text(longTitle)),
            .. FrameV24("TPE1", Utf8Text("Björk")),
        ];

        var tags = Read(Tag(4, 0, body), Audio);

        Assert.Equal(longTitle, tags?.Title);
        Assert.Equal("Björk", tags?.Artist);
    }

    [Fact]
    public void V24_SeveralValues_AreJoined()
    {
        byte[] data = [3, .. Encoding.UTF8.GetBytes("First Artist"), 0, .. Encoding.UTF8.GetBytes("Second Artist")];

        Assert.Equal("First Artist; Second Artist", Read(Tag(4, 0, FrameV24("TPE1", data)), Audio)?.Artist);
    }

    [Fact]
    public void V24_DataLengthIndicatorAndFrameUnsynchronisation_AreRemoved()
    {
        // Title "ÿx" in Latin-1 is FF 78; unsynchronised it becomes FF 00 78. Flags: unsync 0x02 + data length 0x01.
        byte[] plain = [0, 0xFF, 0x78];
        byte[] stored = [.. Synchsafe(plain.Length), 0, 0xFF, 0x00, 0x78];

        Assert.Equal("ÿx", Read(Tag(4, 0, FrameV24("TIT2", stored, 0x03)), Audio)?.Title);
    }

    [Fact]
    public void V23_WholeTagUnsynchronisation_IsRemovedBeforeFramesAreRead()
    {
        byte[] body = [.. FrameV23("TIT2", [0, 0xFF, 0x78]), .. FrameV23("TPE1", Latin1Text("After"))];
        // Insert 00 after every FF in the tag body, as an unsynchronising writer does.
        var stored = new List<byte>();
        foreach (byte b in body)
        {
            stored.Add(b);
            if (b == 0xFF) stored.Add(0);
        }

        var tags = Read(Tag(3, 0x80, stored.ToArray()), Audio);

        Assert.Equal("ÿx", tags?.Title);
        Assert.Equal("After", tags?.Artist);
    }

    [Fact]
    public void V23_ExtendedHeader_IsSkipped()
    {
        // v2.3 extended header: size 6 (excluding itself), flags 00 00, padding size 00 00 00 00.
        byte[] extended = [0, 0, 0, 6, 0, 0, 0, 0, 0, 0];
        byte[] body = [.. extended, .. FrameV23("TIT2", Latin1Text("Title"))];

        Assert.Equal("Title", Read(Tag(3, 0x40, body), Audio)?.Title);
    }

    [Fact]
    public void V23_CompressedFrame_IsSkippedAndLaterFramesStillRead()
    {
        byte[] body =
        [
            .. FrameV23("TIT2", [0x78, 0x9C, 1, 2, 3, 4, 5], 0x80),
            .. FrameV23("TPE1", Latin1Text("Artist")),
        ];

        var tags = Read(Tag(3, 0, body), Audio);

        Assert.Null(tags?.Title);
        Assert.Equal("Artist", tags?.Artist);
    }

    [Fact]
    public void V22_ThreeLetterFrames_AreRead()
    {
        byte[] body =
        [
            .. FrameV22("TT2", Latin1Text("Old Title")),
            .. FrameV22("TP1", Latin1Text("Old Artist")),
            .. FrameV22("TAL", Latin1Text("Old Album")),
        ];

        var tags = Read(Tag(2, 0, body), Audio);

        Assert.Equal("Old Title", tags?.Title);
        Assert.Equal("Old Artist", tags?.Artist);
        Assert.Equal("Old Album", tags?.Album);
    }

    [Fact]
    public void V1Only_IsRead()
    {
        var tags = Read(Audio, V1("V1 Title", "V1 Artist", "V1 Album"));

        Assert.Equal("V1 Title", tags?.Title);
        Assert.Equal("V1 Artist", tags?.Artist);
        Assert.Equal("V1 Album", tags?.Album);
    }

    [Fact]
    public void V2WinsAndV1FillsOnlyMissingFields()
    {
        var tags = Read(Tag(3, 0, FrameV23("TIT2", Latin1Text("Long v2 title beyond thirty characters"))), Audio,
            V1("Long v2 title beyond thirty ch", "From V1", "Album V1"));

        Assert.Equal("Long v2 title beyond thirty characters", tags?.Title);
        Assert.Equal("From V1", tags?.Artist);
        Assert.Equal("Album V1", tags?.Album);
    }

    [Theory]
    [InlineData("USRC17607839", "USRC17607839")]
    [InlineData("US-RC1-76-07839", "USRC17607839")]
    [InlineData("gbaye0601498", "GBAYE0601498")]
    [InlineData("not an isrc", null)]
    [InlineData("USRC1760783", null)]
    public void Isrc_IsNormalisedOrDropped(string tag, string? expected)
    {
        Assert.Equal(expected, Read(Tag(3, 0, FrameV23("TSRC", Latin1Text(tag))), Audio)?.Isrc);
    }

    [Fact]
    public void NoTag_ReturnsNull()
    {
        Assert.Null(Read(Audio));
    }

    [Fact]
    public void MalformedTags_DoNotThrow()
    {
        // Frame size runs past the end of the tag; size bytes not synchsafe; truncated header; empty file.
        byte[] badFrame = [.. Encoding.ASCII.GetBytes("TIT2"), 0x7F, 0xFF, 0xFF, 0xFF, 0, 0, 0, (byte)'x'];
        Assert.Null(Read(Tag(3, 0, badFrame, 0)));
        Assert.Null(Read([(byte)'I', (byte)'D', (byte)'3', 3, 0, 0, 0x80, 0, 0, 0], Audio));
        Assert.Null(Read([(byte)'I', (byte)'D', (byte)'3', 3]));
        Assert.Null(Read([]));
    }
}
