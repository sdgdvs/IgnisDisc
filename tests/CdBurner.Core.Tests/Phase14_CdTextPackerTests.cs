using System.Buffers.Binary;
using System.Text;
using CdBurner.Core.Audio;
using Xunit;

namespace CdBurner.Core.Tests;

public class Phase14_CdTextPackerTests
{
    [Fact]
    public void Crc16_KnownRealWorldVectors_CalculatesExpectedInvertedChecksums()
    {
        // Vectors from commercial CD-Text lead-in dumps (IEC 60908 Annex J)

        // Vector 1: Title pack "REVOLUTION D"
        // 80 00 00 00 52 45 56 4f 4c 55 54 49 4f 4e 20 44 -> CRC: 8D 1F
        byte[] pack1Data = [0x80, 0x00, 0x00, 0x00, 0x52, 0x45, 0x56, 0x4F, 0x4C, 0x55, 0x54, 0x49, 0x4F, 0x4E, 0x20, 0x44];
        ushort crc1 = CdTextCrc16.ComputeInvertedCrc16(pack1Data);
        Assert.Equal(0x8D1F, crc1);

        // Vector 2: SizeInfo pack 0 (12 tracks, 19 title packs, 4 performer packs)
        // 8f 00 17 00 00 01 0c 00 13 04 00 00 00 00 00 00 -> CRC: 33 55
        byte[] pack2Data = [0x8F, 0x00, 0x17, 0x00, 0x00, 0x01, 0x0C, 0x00, 0x13, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        ushort crc2 = CdTextCrc16.ComputeInvertedCrc16(pack2Data);
        Assert.Equal(0x3355, crc2);

        // Vector 3: SizeInfo pack 1 (3 size info packs, last sequence 0x19)
        // 8f 01 18 00 00 00 00 00 00 00 00 03 19 00 00 00 -> CRC: 0B F6
        byte[] pack3Data = [0x8F, 0x01, 0x18, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x19, 0x00, 0x00, 0x00];
        ushort crc3 = CdTextCrc16.ComputeInvertedCrc16(pack3Data);
        Assert.Equal(0x0BF6, crc3);

        // Vector 4: SizeInfo pack 2 (English language code 0x09)
        // 8f 02 19 00 00 00 00 00 09 00 00 00 00 00 00 00 -> CRC: CC B9
        byte[] pack4Data = [0x8F, 0x02, 0x19, 0x00, 0x00, 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        ushort crc4 = CdTextCrc16.ComputeInvertedCrc16(pack4Data);
        Assert.Equal(0xCCB9, crc4);
    }

    [Fact]
    public void Crc16_VerifyPackAndBitFlipping_FailsOnEverySingleBitFlip()
    {
        // Create an 18-byte pack with sample data
        byte[] pack = new byte[18];
        pack[0] = (byte)CdTextPackType.Title;
        pack[1] = 0x01; // Track 1
        pack[2] = 0x05; // Seq 5
        pack[3] = 0x02; // Char pos 2
        Encoding.Latin1.GetBytes("HELLO WORLD!").CopyTo(pack.AsSpan(4, 12));

        // Compute valid inverted CRC and store in big-endian
        ushort crc = CdTextCrc16.ComputeInvertedCrc16(pack.AsSpan(0, 16));
        BinaryPrimitives.WriteUInt16BigEndian(pack.AsSpan(16, 2), crc);

        // Verify the original uncorrupted pack passes
        Assert.True(CdTextCrc16.VerifyPack(pack));

        // Test that flipping any single bit in any of the 18 bytes (144 individual bit flips) fails
        for (int byteIndex = 0; byteIndex < 18; byteIndex++)
        {
            for (int bitIndex = 0; bitIndex < 8; bitIndex++)
            {
                byte mask = (byte)(1 << bitIndex);
                pack[byteIndex] ^= mask; // Flip bit

                bool isValid = CdTextCrc16.VerifyPack(pack);
                Assert.False(isValid, $"Pack verification should have failed when bit {bitIndex} of byte {byteIndex} was flipped.");

                pack[byteIndex] ^= mask; // Revert bit
            }
        }

        // Verify pack is intact again
        Assert.True(CdTextCrc16.VerifyPack(pack));
    }

    [Fact]
    public void CdTextPack_StructSerialization_WorksAsExpected()
    {
        byte[] payload = Encoding.Latin1.GetBytes("TRACK TITLE ");
        ushort crc = 0x1234;
        var pack = new CdTextPack(CdTextPackType.Title, 1, 2, 3, payload, crc);

        Assert.Equal(CdTextPackType.Title, pack.PackType);
        Assert.Equal(1, pack.TrackNumberIndicator);
        Assert.Equal(2, pack.SequenceNumber);
        Assert.Equal(3, pack.CharacterPosition);
        Assert.Equal(0, pack.BlockNumber);
        Assert.False(pack.IsDbcs);
        Assert.Equal(crc, pack.InvertedCrc16);
        Assert.Equal(payload, pack.TextData);

        byte[] raw = pack.ToByteArray();
        Assert.Equal(18, raw.Length);
        Assert.Equal((byte)CdTextPackType.Title, raw[0]);
        Assert.Equal(1, raw[1]);
        Assert.Equal(2, raw[2]);
        Assert.Equal(3, raw[3]);
        Assert.Equal(0x12, raw[16]);
        Assert.Equal(0x34, raw[17]);

        var deserialized = new CdTextPack(raw);
        Assert.Equal(pack.PackType, deserialized.PackType);
        Assert.Equal(pack.TrackNumberIndicator, deserialized.TrackNumberIndicator);
        Assert.Equal(pack.SequenceNumber, deserialized.SequenceNumber);
        Assert.Equal(pack.BlockCharacterIndicator, deserialized.BlockCharacterIndicator);
        Assert.Equal(pack.InvertedCrc16, deserialized.InvertedCrc16);
        Assert.Equal(pack.TextData, deserialized.TextData);
    }

    [Fact]
    public void BuildPacks_FourTrackMetadata_RoundTripsIdenticallyAndValidatesPacks()
    {
        // 4-track metadata with titles spanning multiple 12-byte pack boundaries,
        // performers, songwriters, UPC/EAN, and per-track ISRCs
        var originalMetadata = new CdTextAlbumMetadata(
            AlbumTitle: "Symphony of the Digital Dawn (Extended Collector's Edition)",
            AlbumPerformer: "The Quantum Synthesizer Philharmonic Orchestra",
            Songwriter: "Dr. Evelyn Vance & Marcus Thorne",
            Composer: "Marcus Thorne",
            Arranger: "Evelyn Vance",
            Message: "Mastered at 96kHz/24bit - Special Red Book Reissue",
            DiscId: "SYMPH-DD-2026-X",
            UpcEan: "075678123456",
            Tracks: new List<CdTextTrackMetadata>
            {
                new(
                    TrackNumber: 1,
                    Title: "Overture: Awakening of the Mechanical Consciousness (Part I & II)",
                    Performer: "The Quantum Philharmonic feat. Synth Quartet",
                    Songwriter: "Dr. Evelyn Vance",
                    Composer: "Marcus Thorne",
                    Arranger: "Evelyn Vance",
                    Message: "Recorded in Berlin",
                    Isrc: "USPR37300001"),
                new(
                    TrackNumber: 2,
                    Title: "Binary Pulse in the Cybernetic Forest",
                    Performer: "The Quantum Philharmonic",
                    Songwriter: "Marcus Thorne",
                    Composer: "Marcus Thorne",
                    Arranger: "Evelyn Vance",
                    Message: "Solo violin by Sarah Chen",
                    Isrc: "USPR37300002"),
                new(
                    TrackNumber: 3,
                    Title: "Resonance Across the Silicon Void: An Atmospheric Journey Through Superconducting Landscapes",
                    Performer: "The Quantum Philharmonic feat. Ambient Ensemble",
                    Songwriter: "Dr. Evelyn Vance & Marcus Thorne",
                    Composer: "Marcus Thorne",
                    Arranger: "Evelyn Vance",
                    Message: "Continuous transition to Track 4",
                    Isrc: "USPR37300003"),
                new(
                    TrackNumber: 4,
                    Title: "Finale: Transcendence Beyond the Event Horizon",
                    Performer: "The Quantum Synthesizer Philharmonic Orchestra",
                    Songwriter: "Marcus Thorne",
                    Composer: "Marcus Thorne",
                    Arranger: "Evelyn Vance",
                    Message: "Grand pipe organ finale",
                    Isrc: "USPR37300004")
            });

        // 1. Build binary packs
        byte[] packBytes = CdTextPackBuilder.BuildPacks(originalMetadata);

        // 2. Verify total output byte length is an exact multiple of 18 bytes
        Assert.NotNull(packBytes);
        Assert.True(packBytes.Length > 0);
        Assert.Equal(0, packBytes.Length % CdTextPack.PackSizeBytes);
        int totalPacks = packBytes.Length / CdTextPack.PackSizeBytes;
        Assert.True(totalPacks >= 4); // Must have at least text packs + 3 size info packs

        // 3. Verify every 18-byte pack passes CRC validation
        for (int i = 0; i < totalPacks; i++)
        {
            var packSpan = packBytes.AsSpan(i * CdTextPack.PackSizeBytes, CdTextPack.PackSizeBytes);
            bool isValid = CdTextCrc16.VerifyPack(packSpan);
            Assert.True(isValid, $"Pack #{i} (Type=0x{packSpan[0]:X2}, Seq={packSpan[2]}) failed CRC-16 validation.");
        }

        // 4. Verify 0x8F Size Information packs are present at the end (3 packs per block)
        var last3Packs = new CdTextPack[3];
        for (int i = 0; i < 3; i++)
        {
            int packIndex = totalPacks - 3 + i;
            var slice = packBytes.AsSpan(packIndex * CdTextPack.PackSizeBytes, CdTextPack.PackSizeBytes);
            last3Packs[i] = new CdTextPack(slice);
        }

        Assert.Equal(CdTextPackType.SizeInfo, last3Packs[0].PackType);
        Assert.Equal(0, last3Packs[0].TrackNumberIndicator);
        Assert.Equal(1, last3Packs[0].TextData[1]); // First track = 1
        Assert.Equal(4, last3Packs[0].TextData[2]); // Last track = 4

        Assert.Equal(CdTextPackType.SizeInfo, last3Packs[1].PackType);
        Assert.Equal(1, last3Packs[1].TrackNumberIndicator);
        Assert.Equal(3, last3Packs[1].TextData[7]); // Count of 0x8F packs = 3
        Assert.Equal((byte)(totalPacks - 1), last3Packs[1].TextData[8]); // Last sequence number

        Assert.Equal(CdTextPackType.SizeInfo, last3Packs[2].PackType);
        Assert.Equal(2, last3Packs[2].TrackNumberIndicator);
        Assert.Equal(0x09, last3Packs[2].TextData[4]); // English language code 0x09

        // 5. Parse packs and verify round-trip equality
        var parsedMetadata = CdTextPackParser.ParsePacks(packBytes);

        Assert.Equal(originalMetadata.AlbumTitle, parsedMetadata.AlbumTitle);
        Assert.Equal(originalMetadata.AlbumPerformer, parsedMetadata.AlbumPerformer);
        Assert.Equal(originalMetadata.Songwriter, parsedMetadata.Songwriter);
        Assert.Equal(originalMetadata.Composer, parsedMetadata.Composer);
        Assert.Equal(originalMetadata.Arranger, parsedMetadata.Arranger);
        Assert.Equal(originalMetadata.Message, parsedMetadata.Message);
        Assert.Equal(originalMetadata.DiscId, parsedMetadata.DiscId);
        Assert.Equal(originalMetadata.UpcEan, parsedMetadata.UpcEan);

        Assert.NotNull(parsedMetadata.Tracks);
        Assert.Equal(4, parsedMetadata.Tracks.Count);

        for (int i = 0; i < 4; i++)
        {
            var exp = originalMetadata.Tracks![i];
            var act = parsedMetadata.Tracks[i];

            Assert.Equal(exp.TrackNumber, act.TrackNumber);
            Assert.Equal(exp.Title, act.Title);
            Assert.Equal(exp.Performer, act.Performer);
            Assert.Equal(exp.Songwriter, act.Songwriter);
            Assert.Equal(exp.Composer, act.Composer);
            Assert.Equal(exp.Arranger, act.Arranger);
            Assert.Equal(exp.Message, act.Message);
            Assert.Equal(exp.Isrc, act.Isrc);
        }

        // Full structural record equality
        Assert.Equal(originalMetadata, parsedMetadata);
    }

    [Fact]
    public void BuildPacks_WithSparseAndEmptyFields_RoundTripsProperly()
    {
        // Album with only Title and Tracks with partial fields
        var metadata = new CdTextAlbumMetadata(
            AlbumTitle: "Minimalist EP",
            AlbumPerformer: "Soloist",
            Tracks: new List<CdTextTrackMetadata>
            {
                new(1, "Track A"),
                new(2, "Track B", Performer: "Guest Artist"),
                new(3, "Track C", Isrc: "GBAYE0100003")
            });

        byte[] bytes = CdTextPackBuilder.BuildPacks(metadata);
        Assert.Equal(0, bytes.Length % 18);

        var parsed = CdTextPackParser.ParsePacks(bytes);

        Assert.Equal("Minimalist EP", parsed.AlbumTitle);
        Assert.Equal("Soloist", parsed.AlbumPerformer);
        Assert.Equal(string.Empty, parsed.Songwriter);
        Assert.Equal(string.Empty, parsed.UpcEan);

        Assert.NotNull(parsed.Tracks);
        Assert.Equal(3, parsed.Tracks.Count);

        Assert.Equal(1, parsed.Tracks[0].TrackNumber);
        Assert.Equal("Track A", parsed.Tracks[0].Title);
        Assert.Equal(string.Empty, parsed.Tracks[0].Performer);

        Assert.Equal(2, parsed.Tracks[1].TrackNumber);
        Assert.Equal("Track B", parsed.Tracks[1].Title);
        Assert.Equal("Guest Artist", parsed.Tracks[1].Performer);

        Assert.Equal(3, parsed.Tracks[2].TrackNumber);
        Assert.Equal("Track C", parsed.Tracks[2].Title);
        Assert.Equal("GBAYE0100003", parsed.Tracks[2].Isrc);
    }

    [Fact]
    public void BuildPacks_WithCdTextDatHeader_ParsesCorrectly()
    {
        var metadata = new CdTextAlbumMetadata(
            AlbumTitle: "Dat File Test",
            AlbumPerformer: "Artist",
            Tracks: new List<CdTextTrackMetadata>
            {
                new(1, "Title 1")
            });

        byte[] rawPacks = CdTextPackBuilder.BuildPacks(metadata);

        // cdtext.dat has a 4-byte header: 2 bytes length + 2 bytes 0x0000
        byte[] datFile = new byte[rawPacks.Length + 4];
        BinaryPrimitives.WriteUInt16BigEndian(datFile.AsSpan(0, 2), (ushort)(rawPacks.Length + 2));
        datFile[2] = 0x00;
        datFile[3] = 0x00;
        rawPacks.CopyTo(datFile.AsSpan(4));

        var parsed = CdTextPackParser.ParsePacks(datFile);
        Assert.Equal("Dat File Test", parsed.AlbumTitle);
        Assert.Equal("Artist", parsed.AlbumPerformer);
        Assert.Single(parsed.Tracks!);
        Assert.Equal("Title 1", parsed.Tracks![0].Title);
    }

    [Fact]
    public void ParsePacks_CorruptedPackCrc_ThrowsInvalidDataException()
    {
        var metadata = new CdTextAlbumMetadata(
            AlbumTitle: "Corruption Test",
            Tracks: new List<CdTextTrackMetadata> { new(1, "Track 1") });

        byte[] rawPacks = CdTextPackBuilder.BuildPacks(metadata);

        // Corrupt a byte in the first pack
        rawPacks[5] ^= 0xFF;

        var ex = Assert.Throws<InvalidDataException>(() => CdTextPackParser.ParsePacks(rawPacks));
        Assert.Contains("CRC verification failed", ex.Message);
    }

    [Fact]
    public void ParsePacks_InvalidLength_ThrowsArgumentException()
    {
        byte[] invalidBytes = new byte[25]; // Not a multiple of 18
        Assert.Throws<ArgumentException>(() => CdTextPackParser.ParsePacks(invalidBytes));

        Assert.Throws<ArgumentException>(() => CdTextPackParser.ParsePacks(ReadOnlySpan<byte>.Empty));
    }
}
