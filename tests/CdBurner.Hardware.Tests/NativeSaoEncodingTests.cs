using System.Buffers.Binary;
using CdBurner.Core.Audio;
using CdBurner.Hardware.NativeWriting;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// Byte-level checks of the Session-at-Once building blocks. Expected values are worked out from MMC-5 and the
/// CD-Text layout (as written by cdrdao), not taken from the code under test.
/// </summary>
public class NativeSaoEncodingTests
{
    [Theory]
    [InlineData(-150, 0, 0, 0)]
    [InlineData(0, 0, 2, 0)]
    [InlineData(450, 0, 8, 0)]
    [InlineData(-151, 99, 59, 74)]
    [InlineData(-11077, 97, 34, 23)]
    [InlineData(359_849, 79, 59, 74)]
    public void Msf_ConvertsBothWays(int lba, byte m, byte s, byte f)
    {
        var msf = Msf.FromLba(lba);
        Assert.Equal(new Msf(m, s, f), msf);
        Assert.Equal(lba, msf.ToLba());
    }

    [Fact]
    public void Layout_PlacesTracksFromLbaMinus150()
    {
        var layout = SaoAudioLayout.Create(new[] { new SaoTrack(1, 300, 150), new SaoTrack(2, 450, 150) });

        Assert.Equal(new SaoTrackPlacement(1, -150, 0, 300, null), layout.Tracks[0]);
        Assert.Equal(new SaoTrackPlacement(2, 300, 450, 900, null), layout.Tracks[1]);
        Assert.Equal(900, layout.LeadOutLba);
        Assert.Equal(1050, layout.TotalSectors);
    }

    [Fact]
    public void Layout_GivesTrackOneTheTwoSecondPreGapEvenWhenAskedForLess()
    {
        var layout = SaoAudioLayout.Create(new[] { new SaoTrack(1, 300, 0) });
        Assert.Equal(-150, layout.Tracks[0].PreGapLba);
        Assert.Equal(0, layout.Tracks[0].Index01Lba);
    }

    [Theory]
    [InlineData(new[] { 2 }, "Track numbers")]
    [InlineData(new[] { 1, 3 }, "Track numbers")]
    public void Layout_RejectsTrackNumbersOutOfOrder(int[] numbers, string message)
    {
        var tracks = numbers.Select(n => new SaoTrack(n, 300)).ToArray();
        var ex = Assert.Throws<ArgumentException>(() => SaoAudioLayout.Create(tracks));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void Layout_RejectsTracksShorterThanFourSeconds()
    {
        Assert.Throws<ArgumentException>(() => SaoAudioLayout.Create(new[] { new SaoTrack(1, 299) }));
    }

    [Fact]
    public void CueSheet_TwoTracksWithGap_MatchesMmcLayout()
    {
        var layout = SaoAudioLayout.Create(new[] { new SaoTrack(1, 300, 150), new SaoTrack(2, 450, 150) });

        byte[] sheet = SaoCueSheetBuilder.Build(layout, mcn: null, cdTextLeadInStart: null);

        byte[] expected =
        {
            0x01, 0x00, 0x00, 0x01, 0x00, 0, 0, 0,    // lead-in, drive-generated
            0x01, 0x01, 0x00, 0x00, 0x00, 0, 0, 0,    // track 1 INDEX 00 at 00:00:00 (LBA -150)
            0x01, 0x01, 0x01, 0x00, 0x00, 0, 2, 0,    // track 1 INDEX 01 at 00:02:00 (LBA 0)
            0x01, 0x02, 0x00, 0x00, 0x00, 0, 6, 0,    // track 2 INDEX 00 at 00:06:00 (LBA 300)
            0x01, 0x02, 0x01, 0x00, 0x00, 0, 8, 0,    // track 2 INDEX 01 at 00:08:00 (LBA 450)
            0x01, 0xAA, 0x01, 0x01, 0x00, 0, 14, 0    // lead-out at 00:14:00 (LBA 900)
        };
        Assert.Equal(expected, sheet);
    }

    [Fact]
    public void CueSheet_GaplessTracks_HaveNoIndex00ForLaterTracks()
    {
        var layout = SaoAudioLayout.Create(new[] { new SaoTrack(1, 300), new SaoTrack(2, 300) });
        byte[] sheet = SaoCueSheetBuilder.Build(layout, null, null);

        Assert.Equal(5 * 8, sheet.Length); // lead-in, T1 I00, T1 I01, T2 I01, lead-out
        Assert.Equal(new byte[] { 0x01, 0x02, 0x01, 0x00, 0x00, 0, 6, 0 }, sheet.AsSpan(24, 8).ToArray());
    }

    [Fact]
    public void CueSheet_CdTextLeadIn_UsesDataForm41AndLeadInTime()
    {
        var layout = SaoAudioLayout.Create(new[] { new SaoTrack(1, 300) });

        byte[] withTime = SaoCueSheetBuilder.Build(layout, null, new Msf(97, 34, 23));
        byte[] withoutTime = SaoCueSheetBuilder.Build(layout, null, new Msf(97, 34, 23), CueSheetVariant.CdTextLeadInWithoutTime);

        Assert.Equal(new byte[] { 0x01, 0x00, 0x00, 0x41, 0x00, 97, 34, 23 }, withTime.AsSpan(0, 8).ToArray());
        Assert.Equal(new byte[] { 0x01, 0x00, 0x00, 0x41, 0x00, 0, 0, 0 }, withoutTime.AsSpan(0, 8).ToArray());
    }

    [Fact]
    public void CueSheet_McnAndIsrc_AreAsciiInAdr2AndAdr3Entries()
    {
        var layout = SaoAudioLayout.Create(new[] { new SaoTrack(1, 300, 150, "US-RC1-76-07839") });

        byte[] sheet = SaoCueSheetBuilder.Build(layout, "0724384260927", null);
        byte[] noCtl = SaoCueSheetBuilder.Build(layout, "0724384260927", null, CueSheetVariant.IsrcWithoutControl);

        Assert.Equal(new byte[] { 0x02, (byte)'0', (byte)'7', (byte)'2', (byte)'4', (byte)'3', (byte)'8', (byte)'4' }, sheet.AsSpan(0, 8).ToArray());
        Assert.Equal(new byte[] { 0x02, (byte)'2', (byte)'6', (byte)'0', (byte)'9', (byte)'2', (byte)'7', 0x00 }, sheet.AsSpan(8, 8).ToArray());
        Assert.Equal(0x01, sheet[16]); // lead-in follows the catalog number
        Assert.Equal(new byte[] { 0x03, 0x01, (byte)'U', (byte)'S', (byte)'R', (byte)'C', (byte)'1', (byte)'7' }, sheet.AsSpan(24, 8).ToArray());
        Assert.Equal(new byte[] { 0x03, 0x01, (byte)'6', (byte)'0', (byte)'7', (byte)'8', (byte)'3', (byte)'9' }, sheet.AsSpan(32, 8).ToArray());
        Assert.Equal(new byte[] { 0x01, 0x01, 0x00, 0x00, 0x00, 0, 0, 0 }, sheet.AsSpan(40, 8).ToArray());
        Assert.Equal(sheet.Length, noCtl.Length);
    }

    [Theory]
    [InlineData("USRC17607839", "USRC17607839")]
    [InlineData("us-rc1-76-07839", "USRC17607839")]
    [InlineData("1SRC17607839", null)]   // country code must be letters
    [InlineData("USRC1760783", null)]    // too short
    [InlineData("USRC1A607839", null)]   // year must be digits
    [InlineData("", null)]
    public void Isrc_IsValidatedBeforeItGoesIntoTheCueSheet(string input, string? expected)
    {
        Assert.Equal(expected, SaoAudioLayout.NormalizeIsrc(input));
    }

    [Theory]
    [InlineData("0724384260927", "0724384260927")]
    [InlineData("0 724384 260927", "0724384260927")] // spaces as printed under a barcode
    [InlineData("724384260927", "0724384260927")]    // 12-digit UPC-A: EAN-13 adds the leading 0
    [InlineData("0000000000000", null)]
    [InlineData("72438426092", null)]                // 11 digits
    [InlineData("07243842609AB", null)]
    public void Mcn_IsThirteenDigits_OrATwelveDigitUpcA(string input, string? expected)
    {
        Assert.Equal(expected, SaoAudioLayout.NormalizeMcn(input));
    }

    [Fact]
    public void ProblemsAreDescribed_OnlyForCodesThatWouldBeLeftOut()
    {
        Assert.Null(SaoAudioLayout.DescribeIsrcProblem(null));
        Assert.Null(SaoAudioLayout.DescribeIsrcProblem("US-RC1-76-07839"));
        Assert.Contains("12 characters", SaoAudioLayout.DescribeIsrcProblem("USRC1760783"));
        Assert.Null(SaoAudioLayout.DescribeMcnProblem(""));
        Assert.Null(SaoAudioLayout.DescribeMcnProblem("724384260927"));
        Assert.Contains("13 digits", SaoAudioLayout.DescribeMcnProblem("12345"));
    }

    [Fact]
    public void CdTextSpread_PutsThreeBytesIntoFourSixBitSymbols()
    {
        byte[] data = new byte[72];
        data[0] = 0x80; // pack type of a title pack
        data[3] = 0x12; data[4] = 0x34; data[5] = 0x56;
        data[69] = 0xFF; data[70] = 0xFF; data[71] = 0xFF;
        byte[] sub = new byte[96];

        CdTextLeadInEncoder.SpreadIntoRw(data, sub);

        Assert.Equal(new byte[] { 0x20, 0x00, 0x00, 0x00 }, sub.AsSpan(0, 4).ToArray());
        Assert.Equal(new byte[] { 0x04, 0x23, 0x11, 0x16 }, sub.AsSpan(4, 4).ToArray()); // 000100 100011 010001 010110
        Assert.Equal(new byte[] { 0x3F, 0x3F, 0x3F, 0x3F }, sub.AsSpan(92, 4).ToArray());
        Assert.All(sub, b => Assert.Equal(0, b & 0xC0)); // P and Q bits stay clear for the drive

        byte[] back = new byte[72];
        CdTextLeadInEncoder.GatherFromRw(sub, back);
        Assert.Equal(data, back);
    }

    [Theory]
    [InlineData(4, 1)]
    [InlineData(8, 2)]
    [InlineData(6, 3)]   // 6 packs: 3 sectors carry the set twice
    [InlineData(5, 5)]   // odd counts: the set repeats four times over 5 sectors
    [InlineData(7, 7)]
    public void CdTextCycle_KeepsPackOrderAcrossSectors(int packCount, int expectedSectors)
    {
        byte[] packs = new byte[packCount * 18];
        for (int p = 0; p < packCount; p++)
        {
            packs[p * 18] = (byte)(0x80 + p); // mark each pack by its first byte
        }

        byte[] cycle = CdTextLeadInEncoder.EncodeCycle(packs);

        Assert.Equal(expectedSectors * 96, cycle.Length);
        byte[] data = new byte[72];
        int next = 0;
        for (int s = 0; s < expectedSectors; s++)
        {
            CdTextLeadInEncoder.GatherFromRw(cycle.AsSpan(s * 96, 96), data);
            for (int k = 0; k < 4; k++)
            {
                Assert.Equal((byte)(0x80 + next), data[k * 18]);
                next = (next + 1) % packCount;
            }
        }
        Assert.Equal(0, next); // the cycle ends on a whole pack set, so repeating it keeps the order
    }

    [Fact]
    public void CdTextCycle_RealPacksSurviveTheRoundTripWithValidCrcs()
    {
        byte[] packs = CdTextPackBuilder.BuildPacks(new CdTextAlbumMetadata("Album", "Artist",
            Tracks: new[] { new CdTextTrackMetadata(1, "First"), new CdTextTrackMetadata(2, "Second") }));

        byte[] cycle = CdTextLeadInEncoder.EncodeCycle(packs);
        byte[] data = new byte[72];
        CdTextLeadInEncoder.GatherFromRw(cycle.AsSpan(0, 96), data);

        for (int k = 0; k < 4; k++)
        {
            Assert.True(CdTextCrc16.VerifyPack(data.AsSpan(k * 18, 18)));
            Assert.Equal(packs.AsSpan(k * 18, 18).ToArray(), data.AsSpan(k * 18, 18).ToArray());
        }
    }

    [Fact]
    public void CdTextCycle_RejectsPartialPacksAndOversizedBlocks()
    {
        Assert.Throws<ArgumentException>(() => CdTextLeadInEncoder.EncodeCycle(new byte[17]));
        Assert.Throws<ArgumentException>(() => CdTextLeadInEncoder.EncodeCycle(new byte[257 * 18]));
    }

    [Fact]
    public void LeadInStart_FromDiscInformationTime()
    {
        Assert.Equal(-11077, CdTextLeadInEncoder.LeadInStartLba(new Msf(97, 34, 23)));
        Assert.Throws<ArgumentException>(() => CdTextLeadInEncoder.LeadInStartLba(new Msf(0, 0, 0)));
    }

    [Fact]
    public void WriteParameters_SetsSaoAndKeepsTheDrivesOtherFields()
    {
        byte[] page = new byte[0x34];
        page[0] = 0x85; page[1] = 0x32;
        page[2] = 0x21; // LS_V + TAO
        page[3] = 0xC4; // multi-session 11, track mode 4
        page[4] = 0x08; // Mode 1
        page[5] = 0x07; // link size
        page[15] = 0x96;
        byte[] modeSense = new byte[8 + page.Length];
        BinaryPrimitives.WriteUInt16BigEndian(modeSense, (ushort)(modeSense.Length - 2));
        page.CopyTo(modeSense, 8);

        var current = WriteParametersPage.FromModeSense10(modeSense)!;
        var sao = current.ForSessionAtOnce(testWrite: true, bufferUnderrunFree: true, cdTextLeadIn: true);
        byte[] list = sao.ToModeSelect10ParameterList();

        Assert.Equal(new byte[8], list.AsSpan(0, 8).ToArray()); // header: lengths zero, no block descriptor
        Assert.Equal(0x05, list[8]);                            // PS cleared
        Assert.Equal(0x32, list[9]);
        Assert.Equal(0x20 | 0x40 | 0x10 | 0x02, list[10]);      // LS_V kept, BUFE, Test Write, SAO
        Assert.Equal(0x04, list[11]);                           // multi-session 00, track mode kept
        Assert.Equal(0x03, list[12]);                           // raw + raw P-W for the CD-Text lead-in
        Assert.Equal(0x07, list[13]);                           // link size kept
        Assert.Equal(0x96, list[8 + 15]);                       // audio pause length kept
        Assert.Equal(0x00, sao.WithRawDataBlockType().DataBlockType);
        Assert.True(sao.TestWrite);
    }

    [Fact]
    public void WriteCommands_HaveTheMmcLayout()
    {
        Assert.Equal(new byte[] { 0x2A, 0, 0xFF, 0xFF, 0xD4, 0xBB, 0, 0x02, 0x80, 0 }, NativeWriteCommands.BuildWrite10(-11077, 640));
        Assert.Equal(new byte[] { 0x55, 0x10, 0, 0, 0, 0, 0, 0, 0x3C, 0 }, NativeWriteCommands.BuildModeSelect10(60));
        Assert.Equal(new byte[] { 0x5D, 0, 0, 0, 0, 0, 0, 0, 0x30, 0 }, NativeWriteCommands.BuildSendCueSheet(48));
        Assert.Equal(new byte[] { 0x5C, 0, 0, 0, 0, 0, 0, 0, 12, 0 }, NativeWriteCommands.BuildReadBufferCapacity());
    }
}
