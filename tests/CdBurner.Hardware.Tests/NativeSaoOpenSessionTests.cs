using CdBurner.Core.Audio;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Scsi;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// The first session of a CD-Extra disc: audio written Session-at-Once with mode page 05h multi-session 11b, so the
/// drive writes the lead-out and a B0h pointer and leaves the disc open for a data session.
/// </summary>
public class NativeSaoOpenSessionTests
{
    private const int SectorSize = 2352;
    private const int TrackSectors = 300;

    private static byte[] Pcm(int seed, int sectors)
    {
        byte[] pcm = new byte[sectors * SectorSize];
        new Random(seed).NextBytes(pcm);
        return pcm;
    }

    private static byte[] Packs() => CdTextPackBuilder.BuildPacks(new CdTextAlbumMetadata("Test Album", "Test Artist",
        Tracks: new[] { new CdTextTrackMetadata(1, "One"), new CdTextTrackMetadata(2, "Two") }));

    // Track 1 at LBA 0; track 2 with a 2-second pre-gap that holds audio (INDEX 00 at 300, INDEX 01 at 450); lead-out 750.
    private static NativeSaoWriteRequest TwoTracks(bool leaveOpen) => new NativeSaoWriteRequest(
        new[] { new SaoTrack(1, TrackSectors, 150), new SaoTrack(2, TrackSectors, 150, PreGapAudioSectors: 150) },
        i => new MemoryStream(Pcm(i + 1, TrackSectors + (i == 1 ? 150 : 0))),
        Packs(),
        MediaCatalogNumber: null,
        TestWrite: false) { LeaveSessionOpen = leaveOpen };

    private static NativeSaoWriteResult Run(VirtualMmcDrive drive, NativeSaoWriteRequest request) =>
        new NativeSaoAudioWriter(drive, sleep: _ => { }).Write(request);

    private static byte[] Execute(VirtualMmcDrive drive, byte[] cdb, int length)
    {
        byte[] buffer = new byte[length];
        var r = drive.ExecuteCommand(cdb, buffer, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, TimeSpan.FromSeconds(5));
        Assert.True(r.IsSuccess, $"command 0x{cdb[0]:X2} failed");
        return buffer.AsSpan(0, (int)r.DataTransferLength).ToArray();
    }

    /// <summary>The 11-byte descriptors of a READ TOC format 2 reply, keyed by POINT.</summary>
    private static Dictionary<byte, byte[]> FullToc(VirtualMmcDrive drive)
    {
        byte[] r = Execute(drive, MmcCommandBuilder.BuildReadTocPmaAtipCdb(0x02, 1, 1024, msf: true), 1024);
        int length = (r[0] << 8 | r[1]) + 2;
        Assert.Equal(length, r.Length);
        Assert.Equal(0, (length - 4) % 11);
        var entries = new Dictionary<byte, byte[]>();
        for (int o = 4; o < length; o += 11)
        {
            entries.Add(r[o + 3], r.AsSpan(o, 11).ToArray());
        }
        return entries;
    }

    [Theory]
    // MMC-3 5.5.4 / MMC-5 7.5.4 Write Parameters page, byte 3: Multi-session bits 7-6, FP bit 5, Copy bit 4, Track
    // Mode bits 3-0. 11b = next session allowed (B0 pointer = next possible program area), 00b = no next session.
    [InlineData(true, 0x04, 0xC4)]
    [InlineData(false, 0x04, 0x04)]
    [InlineData(false, 0xC4, 0x04)]
    [InlineData(true, 0x44, 0xC4)]  // 01b (reserved for CD in MMC-3) becomes 11b
    public void ModePage_MultiSessionField_MatchesMmc(bool leaveOpen, byte byte3Before, byte byte3After)
    {
        byte[] page = new byte[0x34];
        page[0] = 0x85;          // PS set, as MODE SENSE returns it
        page[1] = 0x32;
        page[2] = 0x01;          // write type 1 (TAO)
        page[3] = byte3Before;
        page[4] = 0x08;          // data block type 8 (Mode 1)
        page[8] = 0x20;          // session format 20h (CD-ROM XA)
        page[15] = 0x96;         // audio pause length 150

        byte[] sent = WriteParametersPage.FromPageBytes(page)
            .ForSessionAtOnce(testWrite: false, bufferUnderrunFree: true, cdTextLeadIn: true, leaveOpen: leaveOpen)
            .ToModeSelect10ParameterList();

        Assert.Equal(new byte[8], sent[..8]);       // mode parameter header: lengths zero, no block descriptor
        Assert.Equal(0x05, sent[8 + 0]);            // page code 05h, PS cleared for MODE SELECT
        Assert.Equal(0x32, sent[8 + 1]);
        Assert.Equal(0x42, sent[8 + 2]);            // BUFE (bit 6) + write type 2 (SAO)
        Assert.Equal(byte3After, sent[8 + 3]);
        Assert.Equal(0x03, sent[8 + 4]);            // data block type 3: 2,352 bytes + raw P-W, for the CD-Text lead-in
        Assert.Equal(0x00, sent[8 + 8]);            // session format 00h: CD-DA / CD-ROM
        Assert.Equal(0x96, sent[8 + 15]);           // the rest as the drive reported it
    }

    [Fact]
    public void LeftOpen_DriveReportsAnIncompleteDisc_WithTheB0PointerToTheNextSession()
    {
        var drive = new VirtualMmcDrive();

        var result = Run(drive, TwoTracks(leaveOpen: true));

        Assert.True(result.Success, result.Message);
        Assert.True(result.Verification!.Passed);
        Assert.True(result.DiscLeftOpen);
        Assert.Equal(WriteParametersPage.MultiSessionNextAllowed, drive.CurrentModePage05[3] >> 6);

        // READ DISC INFORMATION byte 2 bits 1-0: disc status 01b = incomplete (appendable).
        byte[] info = Execute(drive, MmcCommandBuilder.BuildReadDiscInformationCdb(34), 34);
        Assert.Equal(1, info[2] & 0x03);

        // Orange Book: lead-out of a first session 6,750 sectors, next lead-in 4,500, then the next program area
        // (its first pre-gap). Lead-out at LBA 750, so the next program area starts at LBA 12,000 = 02:42:00
        // absolute, and the next session's first track at LBA 12,150 (what cdrecord -msinfo reports).
        Assert.Equal(12_150, drive.NextSessionAddress);

        var toc = FullToc(drive);
        Assert.Equal(new byte[] { 0xA0, 0xA1, 0xA2, 0x01, 0x02, 0xB0, 0xC0 }, toc.Keys.ToArray());
        Assert.Equal(new byte[] { 1, 0x10, 0, 0xA0, 0, 0, 0, 0, 1, 0x00, 0 }, toc[0xA0]);   // first track 1, CD-DA disc
        Assert.Equal(new byte[] { 1, 0x10, 0, 0xA1, 0, 0, 0, 0, 2, 0, 0 }, toc[0xA1]);      // last track 2
        Assert.Equal(new byte[] { 1, 0x10, 0, 0xA2, 0, 0, 0, 0, 0, 12, 0 }, toc[0xA2]);     // lead-out 00:12:00 = LBA 750
        Assert.Equal(new byte[] { 1, 0x10, 0, 0x01, 0, 0, 0, 0, 0, 2, 0 }, toc[0x01]);      // 00:02:00 = LBA 0
        Assert.Equal(new byte[] { 1, 0x10, 0, 0x02, 0, 0, 0, 0, 0, 8, 0 }, toc[0x02]);      // 00:08:00 = LBA 450
        // B0h, ADR 5: next program area 02:42:00, two ADR 5 pointers, last possible lead-out 79:59:74 (ATIP).
        Assert.Equal(new byte[] { 1, 0x50, 0, 0xB0, 2, 42, 0, 2, 79, 59, 74 }, toc[0xB0]);
        // C0h, ADR 5: start of the first lead-in 97:34:23 (ATIP).
        Assert.Equal(new byte[] { 1, 0x50, 0, 0xC0, 0, 0, 0, 0, 97, 34, 23 }, toc[0xC0]);
    }

    [Fact]
    public void Closed_HasNoB0Pointer_AndTheDiscIsComplete()
    {
        var drive = new VirtualMmcDrive();

        var result = Run(drive, TwoTracks(leaveOpen: false));

        Assert.True(result.Success, result.Message);
        Assert.Null(result.DiscLeftOpen);
        Assert.Equal(0, drive.CurrentModePage05[3] >> 6);
        Assert.Equal(2, Execute(drive, MmcCommandBuilder.BuildReadDiscInformationCdb(34), 34)[2] & 0x03);
        Assert.Null(drive.NextSessionAddress);
        Assert.Equal(new byte[] { 0xA0, 0xA1, 0xA2, 0x01, 0x02 }, FullToc(drive).Keys.ToArray());
    }

    [Fact]
    public void LeavingTheSessionOpen_ChangesNothingElseThatIsWritten()
    {
        var closed = new VirtualMmcDrive();
        var open = new VirtualMmcDrive();

        Assert.True(Run(closed, TwoTracks(leaveOpen: false)).Success);
        Assert.True(Run(open, TwoTracks(leaveOpen: true)).Success);

        // Same audio (pre-gap audio included), same CD-Text lead-in, same cue sheet (INDEX 00/01 marks).
        Assert.Equal(closed.ProgramAreaWritten.ToArray(), open.ProgramAreaWritten.ToArray());
        Assert.Equal(closed.LeadInWritten.ToArray(), open.LeadInWritten.ToArray());
        Assert.Equal(closed.RecordedCueSheet, open.RecordedCueSheet);
        Assert.Equal(closed.RecordedTracks, open.RecordedTracks);
        Assert.Equal(closed.RecordedLeadOutLba, open.RecordedLeadOutLba);
        Assert.True(open.LeadInWritten.Length > 0);

        // Only byte 3 of the mode page differs, and only in its multi-session bits.
        byte[] a = closed.CurrentModePage05.ToArray(), b = open.CurrentModePage05.ToArray();
        Assert.Equal(0xC0, a[3] ^ b[3]);
        a[3] = b[3] = 0;
        Assert.Equal(a, b);
    }

    [Fact]
    public void DriveThatClosesTheDiscAnyway_IsReported()
    {
        var drive = new VirtualMmcDrive { IgnoresMultiSessionField = true };

        var result = Run(drive, TwoTracks(leaveOpen: true));

        Assert.False(result.Success);
        Assert.True(result.DiscRecorded);
        Assert.True(result.Verification!.Passed);
        Assert.False(result.DiscLeftOpen);
        Assert.Contains("no further session can be added", result.Message);
    }
}
