using System.Buffers.Binary;
using CdBurner.Core.Images;
using CdBurner.Core.Primitives;
using Xunit;

namespace CdBurner.Core.Tests;

public class Phase11_CueSheetAndRawSectorTests
{
    [Fact]
    public void CueSheet_ComplexMixedModeSheet_RoundTripStructuralEquality()
    {
        // Complex mixed-mode CUE sheet: Track 1 = MODE1/2352 data, Tracks 2..5 = AUDIO with metadata
        const string originalCueText =
            """
            REM GENRE "Progressive Rock"
            REM DATE 1997
            REM DISCID 4A0A5705
            REM COMMENT "ExactAudioCopy v0.99pb4"
            CATALOG 0012345678901
            TITLE "Mixed Mode Master Disc"
            PERFORMER "Master Artist"
            SONGWRITER "Master Songwriter"
            FILE "game_data.bin" BINARY
              TRACK 01 MODE1/2352
                TITLE "Data Track"
                PERFORMER "Software Corp"
                INDEX 01 00:00:00
            FILE "audio_tracks.bin" BINARY
              TRACK 02 AUDIO
                TITLE "Intro Audio"
                PERFORMER "Master Artist"
                SONGWRITER "Master Songwriter"
                ISRC USPR37300001
                FLAGS DCP
                PREGAP 00:02:00
                INDEX 00 00:00:00
                INDEX 01 00:02:00
                POSTGAP 00:01:00
              TRACK 03 AUDIO
                TITLE "Main Theme"
                PERFORMER "Master Artist"
                SONGWRITER "Master Songwriter"
                ISRC USPR37300002
                INDEX 01 03:15:20
              TRACK 04 AUDIO
                TITLE "Boss Battle"
                PERFORMER "Master Artist"
                SONGWRITER "Guest Composer"
                ISRC USPR37300003
                PREGAP 00:01:50
                INDEX 00 07:42:10
                INDEX 01 07:44:00
              TRACK 05 AUDIO
                TITLE "Ending Credits"
                PERFORMER "Master Artist"
                SONGWRITER "Master Songwriter"
                ISRC USPR37300004
                INDEX 00 11:58:00
                INDEX 01 12:00:00
            """;

        // 1. Parse initial CUE sheet
        CueSheet parsedSheet1 = CueSheetParser.Parse(originalCueText);

        // Verify top-level properties
        Assert.Equal("0012345678901", parsedSheet1.Catalog);
        Assert.Equal("Mixed Mode Master Disc", parsedSheet1.Title);
        Assert.Equal("Master Artist", parsedSheet1.Performer);
        Assert.Equal("Master Songwriter", parsedSheet1.Songwriter);
        Assert.Equal("Progressive Rock", parsedSheet1.Remarks["GENRE"]);
        Assert.Equal("1997", parsedSheet1.Remarks["DATE"]);
        Assert.Equal("4A0A5705", parsedSheet1.Remarks["DISCID"]);
        Assert.Equal("ExactAudioCopy v0.99pb4", parsedSheet1.Remarks["COMMENT"]);

        // Verify File 1 & Track 1
        Assert.Equal(2, parsedSheet1.Files.Count);
        CueFileEntry file1 = parsedSheet1.Files[0];
        Assert.Equal("game_data.bin", file1.FileName);
        Assert.Equal("BINARY", file1.FileType);
        Assert.Single(file1.Tracks);

        CueTrack track1 = file1.Tracks[0];
        Assert.Equal(1, track1.TrackNumber);
        Assert.Equal(CueTrackDataType.Mode1_2352, track1.DataType);
        Assert.Equal("Data Track", track1.Title);
        Assert.Equal("Software Corp", track1.Performer);
        Assert.Single(track1.Indices);
        Assert.Equal(1, track1.Indices[0].IndexNumber);
        Assert.Equal(new MsfAddress(0, 0, 0), track1.Indices[0].Position);

        // Verify File 2 & Tracks 2..5
        CueFileEntry file2 = parsedSheet1.Files[1];
        Assert.Equal("audio_tracks.bin", file2.FileName);
        Assert.Equal("BINARY", file2.FileType);
        Assert.Equal(4, file2.Tracks.Count);

        CueTrack track2 = file2.Tracks[0];
        Assert.Equal(2, track2.TrackNumber);
        Assert.Equal(CueTrackDataType.Audio, track2.DataType);
        Assert.Equal("Intro Audio", track2.Title);
        Assert.Equal("Master Artist", track2.Performer);
        Assert.Equal("Master Songwriter", track2.Songwriter);
        Assert.Equal("USPR37300001", track2.Isrc);
        Assert.Contains("DCP", track2.Flags);
        Assert.Equal(new MsfAddress(0, 2, 0), track2.PreGap);
        Assert.Equal(new MsfAddress(0, 1, 0), track2.PostGap);
        Assert.Equal(2, track2.Indices.Count);
        Assert.Equal(0, track2.Indices[0].IndexNumber);
        Assert.Equal(new MsfAddress(0, 0, 0), track2.Indices[0].Position);
        Assert.Equal(1, track2.Indices[1].IndexNumber);
        Assert.Equal(new MsfAddress(0, 2, 0), track2.Indices[1].Position);

        CueTrack track3 = file2.Tracks[1];
        Assert.Equal(3, track3.TrackNumber);
        Assert.Equal(CueTrackDataType.Audio, track3.DataType);
        Assert.Equal("Main Theme", track3.Title);

        CueTrack track4 = file2.Tracks[2];
        Assert.Equal(4, track4.TrackNumber);
        Assert.Equal("Guest Composer", track4.Songwriter);
        Assert.Equal(new MsfAddress(0, 1, 50), track4.PreGap);

        CueTrack track5 = file2.Tracks[3];
        Assert.Equal(5, track5.TrackNumber);
        Assert.Equal("Ending Credits", track5.Title);
        Assert.Equal(2, track5.Indices.Count);

        // 2. Format back using CueSheetWriter
        string generatedCueText = CueSheetWriter.Format(parsedSheet1);

        // 3. Re-parse
        CueSheet parsedSheet2 = CueSheetParser.Parse(generatedCueText);

        // 4. Assert full structural equality
        Assert.Equal(parsedSheet1, parsedSheet2);
        Assert.True(parsedSheet1 == parsedSheet2);
        Assert.Equal(parsedSheet1.GetHashCode(), parsedSheet2.GetHashCode());
    }

    [Fact]
    public void RawSectorCodec_Convert2048To2352Mode1_Lba0_StructureAndEdcValidation()
    {
        // 2048 bytes of test user data
        byte[] userData = new byte[2048];
        for (int i = 0; i < userData.Length; i++)
        {
            userData[i] = (byte)(i & 0xFF);
        }

        // Convert LBA 0 (MSF 00:02:00) to raw 2352 bytes
        byte[] rawSector = new byte[2352];
        RawSectorCodec.Convert2048To2352Mode1(userData, lba: 0, rawSector);

        // Verify 12-byte sync pattern: 00 FF FF FF FF FF FF FF FF FF FF 00
        byte[] expectedSync = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];
        Assert.True(rawSector.AsSpan(0, 12).SequenceEqual(expectedSync));

        // Verify 4-byte header: LBA 0 = MSF 00:02:00 -> BCD 0x00, 0x02, 0x00 + Mode 0x01
        Assert.Equal(0x00, rawSector[12]);
        Assert.Equal(0x02, rawSector[13]);
        Assert.Equal(0x00, rawSector[14]);
        Assert.Equal(0x01, rawSector[15]);

        // Verify User Data copied exactly to 16..2063
        Assert.True(rawSector.AsSpan(16, 2048).SequenceEqual(userData));

        // Verify 32-bit EDC at 2064..2067 is non-zero
        uint edc = BinaryPrimitives.ReadUInt32LittleEndian(rawSector.AsSpan(2064, 4));
        Assert.NotEqual(0U, edc);

        // Verify 8 zero bytes at 2068..2075
        for (int i = 2068; i <= 2075; i++)
        {
            Assert.Equal(0, rawSector[i]);
        }

        // Verify 276 bytes of ECC at 2076..2351 are populated (non-zero parity symbols)
        bool hasNonZeroEcc = false;
        for (int i = 2076; i < 2352; i++)
        {
            if (rawSector[i] != 0)
            {
                hasNonZeroEcc = true;
                break;
            }
        }
        Assert.True(hasNonZeroEcc);

        // Extract back via Extract2048From2352
        byte[] extractedData = new byte[2048];
        bool extractResult = RawSectorCodec.Extract2048From2352(rawSector, extractedData, out bool edcValid);
        Assert.True(extractResult);
        Assert.True(edcValid);
        Assert.Equal(userData, extractedData);

        // Verify flipping 1 byte in user data sets edcValid == false
        rawSector[100] ^= 0x55; // byte 100 is within user data 16..2063
        RawSectorCodec.Extract2048From2352(rawSector, extractedData, out bool edcValidAfterFlip);
        Assert.False(edcValidAfterFlip);
    }

    [Theory]
    [InlineData(0, 0x00, 0x02, 0x00)]       // LBA 0 = 00:02:00
    [InlineData(150, 0x00, 0x04, 0x00)]     // LBA 150 = 00:04:00
    [InlineData(4500, 0x01, 0x02, 0x00)]    // LBA 4500 = 01:02:00
    [InlineData(45074, 0x10, 0x02, 0x74)]   // LBA 45074 = 10:02:74
    public void RawSectorCodec_VariousLbaAddresses_EncodeProperBcdHeadersAndEdc(int lba, byte bcdM, byte bcdS, byte bcdF)
    {
        byte[] userData = new byte[2048];
        Random.Shared.NextBytes(userData);

        byte[] rawSector = RawSectorCodec.Convert2048To2352Mode1(userData, lba);

        Assert.Equal(bcdM, rawSector[12]);
        Assert.Equal(bcdS, rawSector[13]);
        Assert.Equal(bcdF, rawSector[14]);
        Assert.Equal(0x01, rawSector[15]);

        byte[] extracted = RawSectorCodec.Extract2048From2352(rawSector, out bool edcValid);
        Assert.True(edcValid);
        Assert.Equal(userData, extracted);
    }

    [Fact]
    public void RawSectorCodec_Mode2XaForm1_RoundTripAndCorruptionDetection()
    {
        byte[] userData = new byte[2048];
        Random.Shared.NextBytes(userData);

        byte[] rawSector = new byte[2352];
        RawSectorCodec.Convert2048To2352Mode2Xa(
            userData,
            lba: 150,
            rawSector,
            fileNumber: 1,
            channelNumber: 2,
            submode: 0x08,
            codingInfo: 0x00);

        Assert.Equal(0x02, rawSector[15]); // Mode 2

        byte[] extracted = new byte[2048];
        bool success = RawSectorCodec.Extract2048From2352(rawSector, extracted, out bool edcValid);
        Assert.True(success);
        Assert.True(edcValid);
        Assert.Equal(userData, extracted);

        // Corrupt user data
        rawSector[1000] ^= 0xAA;
        RawSectorCodec.Extract2048From2352(rawSector, extracted, out bool edcValidCorrupt);
        Assert.False(edcValidCorrupt);
    }

    [Fact]
    public void RawSectorCodec_InvalidSync_ThrowsInvalidDataException()
    {
        byte[] userData = new byte[2048];
        byte[] rawSector = RawSectorCodec.Convert2048To2352Mode1(userData, 0);

        // Corrupt sync pattern
        rawSector[0] = 0xAA;

        byte[] dest = new byte[2048];
        Assert.Throws<InvalidDataException>(() =>
            RawSectorCodec.Extract2048From2352(rawSector, dest, out _));
    }

    [Fact]
    public void CueSheetParser_SupportsAllTrackDataTypes()
    {
        const string cueText =
            """
            FILE "image.bin" BINARY
              TRACK 01 AUDIO
                INDEX 01 00:00:00
              TRACK 02 MODE1/2048
                INDEX 01 01:00:00
              TRACK 03 MODE1/2352
                INDEX 01 02:00:00
              TRACK 04 MODE2/2336
                INDEX 01 03:00:00
              TRACK 05 MODE2/2352
                INDEX 01 04:00:00
            """;

        CueSheet sheet = CueSheetParser.Parse(cueText);
        Assert.Equal(5, sheet.Files[0].Tracks.Count);
        Assert.Equal(CueTrackDataType.Audio, sheet.Files[0].Tracks[0].DataType);
        Assert.Equal(CueTrackDataType.Mode1_2048, sheet.Files[0].Tracks[1].DataType);
        Assert.Equal(CueTrackDataType.Mode1_2352, sheet.Files[0].Tracks[2].DataType);
        Assert.Equal(CueTrackDataType.Mode2_2336, sheet.Files[0].Tracks[3].DataType);
        Assert.Equal(CueTrackDataType.Mode2_2352, sheet.Files[0].Tracks[4].DataType);

        string serialized = CueSheetWriter.Format(sheet);
        CueSheet reParsed = CueSheetParser.Parse(serialized);
        Assert.Equal(sheet, reParsed);
    }

    [Fact]
    public async Task CueSheet_AsyncFileIO_RoundTrip()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"cue_test_{Guid.NewGuid():N}.cue");
        try
        {
            var sheet = new CueSheet(
                Catalog: "9876543210123",
                Title: "Async File Test",
                Performer: "Async Artist",
                Files:
                [
                    new CueFileEntry("disc.bin", "BINARY",
                    [
                        new CueTrack(1, CueTrackDataType.Mode1_2352, Title: "Data Part", Indices: [new CueIndex(1, new MsfAddress(0, 0, 0))]),
                        new CueTrack(2, CueTrackDataType.Audio, Title: "Audio Part", PreGap: new MsfAddress(0, 2, 0), Indices: [new CueIndex(1, new MsfAddress(0, 2, 0))])
                    ])
                ],
                Remarks: new Dictionary<string, string> { ["GENRE"] = "Electronic", ["YEAR"] = "2026" });

            await CueSheetWriter.WriteToFileAsync(sheet, tempFile);
            CueSheet readSheet = await CueSheetParser.ParseFileAsync(tempFile);

            Assert.Equal(sheet, readSheet);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }
}
