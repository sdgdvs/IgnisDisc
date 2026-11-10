using System.Buffers.Binary;
using CdBurner.Core.Audio;
using Xunit;

namespace CdBurner.Core.Tests;

/// <summary>
/// Each CD-Text pack has its own CRC, which cannot show that a whole pack is absent. The parser has to notice
/// that from the pack sequence numbers instead of returning shortened or merged titles.
/// </summary>
public class CdTextPackSequenceTests
{
    private static readonly CdTextAlbumMetadata Album = new(
        AlbumTitle: "AlbumTitleNumberOneIsLong",
        Tracks:
        [
            new CdTextTrackMetadata(1, "FirstTrackTitleIsLong"),
            new CdTextTrackMetadata(2, "SecondTrackTitleIsLong")
        ]);

    private static List<byte[]> BuildPacks()
    {
        byte[] raw = CdTextPackBuilder.BuildPacks(Album);
        return raw.Chunk(CdTextPack.PackSizeBytes).ToList();
    }

    private static byte[] Join(IEnumerable<byte[]> packs) => packs.SelectMany(p => p).ToArray();

    [Fact]
    public void BuiltPacks_AreNumberedConsecutivelyFromZero()
    {
        var packs = BuildPacks();

        Assert.True(packs.Count >= 6);
        Assert.Equal(Enumerable.Range(0, packs.Count).Select(i => (byte)i), packs.Select(p => p[2]));
    }

    [Fact]
    public void ParsePacks_WithAnyOnePackMissing_Throws()
    {
        int count = BuildPacks().Count;

        for (int dropped = 0; dropped < count; dropped++)
        {
            var packs = BuildPacks();
            packs.RemoveAt(dropped);

            Assert.Throws<InvalidDataException>(() => CdTextPackParser.ParsePacks(Join(packs)));
        }
    }

    [Fact]
    public void ParsePacks_WithAPackDeliveredTwice_ReturnsTheSameText()
    {
        var packs = BuildPacks();
        packs.Insert(2, packs[1]);

        Assert.Equal(Album, CdTextPackParser.ParsePacks(Join(packs)));
    }

    [Fact]
    public void ParsePacks_WithTwoPacksSwapped_ReturnsTheSameText()
    {
        var packs = BuildPacks();
        (packs[1], packs[4]) = (packs[4], packs[1]);

        Assert.Equal(Album, CdTextPackParser.ParsePacks(Join(packs)));
    }

    [Fact]
    public void ParsePacks_WithTwoDifferentPacksSharingANumber_Throws()
    {
        var packs = BuildPacks();
        byte[] renumbered = (byte[])packs[2].Clone();
        renumbered[2] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(renumbered.AsSpan(16, 2), CdTextCrc16.ComputeInvertedCrc16(renumbered.AsSpan(0, 16)));
        packs[2] = renumbered;

        var ex = Assert.Throws<InvalidDataException>(() => CdTextPackParser.ParsePacks(Join(packs)));
        Assert.Contains("sequence number 1", ex.Message);
    }
}
