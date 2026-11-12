using CdBurner.Core.Images;
using CdBurner.Core.Verification;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Scsi;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// Mixed-mode CDs (a Mode 1 data track, then audio) written Session-at-Once: data form 10h with 2,048-byte sectors
/// for the data, a two-second post-gap after it, audio as usual (libburn's cookbook; the Yellow Book).
/// </summary>
public class MixedModeCdTests
{
    private const int DataSectors = 400;
    private const int AudioSectors = 300;

    private static byte[] Image(int seed = 7)
    {
        var image = new byte[DataSectors * 2048];
        new Random(seed).NextBytes(image);
        return image;
    }

    private static byte[] Pcm(int seed)
    {
        var pcm = new byte[AudioSectors * 2352];
        new Random(seed).NextBytes(pcm);
        return pcm;
    }

    private static NativeSaoWriteRequest Request(Func<byte[]>? image = null, int shift = 0) => new(
        new[] { new SaoTrack(1, DataSectors, 150, IsData: true), new SaoTrack(2, AudioSectors, 150), new SaoTrack(3, AudioSectors) },
        i => new MemoryStream(i == 0 ? (image ?? (() => Image()))() : Pcm(i)),
        null, null, TestWrite: false)
    { ShiftSamples = shift };

    private static NativeSaoWriteResult Run(VirtualMmcDrive drive, NativeSaoWriteRequest request) =>
        new NativeSaoAudioWriter(drive, sleep: _ => { }).Write(request);

    [Fact]
    public void Layout_PutsATwoSecondPostGapAfterTheDataTrack()
    {
        var layout = SaoAudioLayout.Create(Request().Tracks);

        var data = layout.Tracks[0];
        Assert.True(data.IsData);
        Assert.Equal(-150, data.PreGapLba);
        Assert.Equal(0, data.Index01Lba);
        Assert.Equal(150, data.PostGapSectors);
        Assert.Equal(DataSectors + 150, data.EndLba);
        Assert.Equal(DataSectors + 150, layout.AudioStartLba);          // track 2's pre-gap follows the post-gap
        Assert.Equal(DataSectors + 300, layout.Tracks[1].Index01Lba);
        Assert.Equal(0x40, data.CueControl);
    }

    [Fact]
    public void CueSheet_MarksTheDataTrack_WithControl41hAndDataForm10h()
    {
        var layout = SaoAudioLayout.Create(Request().Tracks);
        byte[] sheet = SaoCueSheetBuilder.Build(layout, null, null);
        var entries = Enumerable.Range(0, sheet.Length / 8).Select(i => sheet.AsSpan(i * 8, 8).ToArray()).ToList();

        Assert.Equal(0x41, entries[0][0]);                                   // lead-in: the first track's control (data)
        Assert.Equal([0x41, 0x01, 0x00, 0x10], entries[1][..4]);             // track 1 INDEX 00, Mode 1
        Assert.Equal([0x41, 0x01, 0x01, 0x10], entries[2][..4]);             // track 1 INDEX 01, Mode 1
        Assert.Equal([0x01, 0x02, 0x00, 0x00], entries[3][..4]);             // track 2 INDEX 00, audio
        Assert.Equal([0x01, 0xAA, 0x01, 0x01], entries[^1][..4]);            // lead-out: the last track's control (audio)
    }

    [Fact]
    public void MixedModeDisc_IsWritten_AndBothPartsReadBackCorrectly()
    {
        var drive = new VirtualMmcDrive { ReadOffsetSamples = 30 };

        var result = Run(drive, Request());

        Assert.True(result.Success, result.Message);
        Assert.Equal(150 + DataSectors + 150, result.DataSectorsWritten);
        Assert.True(result.Verification!.Passed, result.Verification.Message);
        Assert.Equal(30, result.Verification.OffsetSamples);                 // the audio offset is found after the data
        Assert.Equal(0x40, drive.RecordedTracks[0].CueControl);
        Assert.Equal(0x00, drive.RecordedTracks[1].CueControl);

        // The drive recorded real Mode 1 sectors: the data reads back through the sector codec, address included.
        byte[] raw = drive.ProgramAreaWritten.GetBuffer().AsSpan(150 * 2352, 2352).ToArray();
        Assert.Equal(Image().AsSpan(0, 2048).ToArray(), RawSectorCodec.Extract2048From2352(raw, out bool valid));
        Assert.True(valid);
    }

    [Fact]
    public void DataThatDiffersOnTheDisc_FailsTheCheck_AndSaysWhere()
    {
        int opens = 0;
        byte[] Changing() => opens++ == 0 ? Image() : Image(seed: 8); // the check reads a different image than was written

        var result = Run(new VirtualMmcDrive(), Request(Changing));

        Assert.False(result.Success);
        Assert.Contains("data track sectors read back differently, the first at LBA 0", result.Message);
    }

    [Fact]
    public void WriteOffsetShift_IsNotAppliedToAMixedModeDisc()
    {
        var drive = new VirtualMmcDrive();
        var notes = new List<string>();

        var result = new NativeSaoAudioWriter(drive, notes.Add, _ => { }).Write(Request(shift: 6));

        Assert.True(result.Success, result.Message);
        Assert.Contains(notes, n => n.Contains("write-offset correction (+6 samples) is not applied"));
    }

    [Fact]
    public void OnlyTrackOneCanBeData()
    {
        var tracks = new[] { new SaoTrack(1, AudioSectors, 150), new SaoTrack(2, DataSectors, IsData: true) };

        var ex = Assert.Throws<ArgumentException>(() => SaoAudioLayout.Create(tracks));
        Assert.Contains("only track 1", ex.Message);
    }

    [Fact]
    public void TheDiscCheck_ReadsAMixedModeDiscCleanly()
    {
        var drive = new VirtualMmcDrive();
        Assert.True(Run(drive, Request()).Success);

        var report = new DiscHealthScanner(drive).Scan();

        Assert.Equal(DiscHealthVerdict.ReadsCleanly, report.Verdict);
        Assert.False(report.Tracks[0].IsAudio);
        Assert.True(report.Tracks[1].IsAudio);
    }
}
