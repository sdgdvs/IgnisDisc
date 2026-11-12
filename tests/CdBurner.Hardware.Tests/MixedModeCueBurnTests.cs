using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.Images;
using CdBurner.Core.Persistence;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// Mixed-mode CDs from a CUE sheet: a MODE1 data track, then audio, in one BINARY file or one file per track (as disc
/// rips lay them out). The import finds the data track and the raw audio; the direct writer puts them on the disc
/// where the file has them; the Windows writer refuses.
/// </summary>
public sealed class MixedModeCueBurnTests : IDisposable
{
    private const int Raw = 2352;
    private const int DataSectors = 400;    // the data, then a 150-sector post-gap of zeros, as a rip keeps it
    private const int GapFrames = 150;      // track 2's INDEX 00
    private const int Audio2Frames = 300;
    private const int Audio3Frames = 320;
    private const int Track2Lba = DataSectors + 150 + GapFrames; // 700
    private const int Track3Lba = Track2Lba + Audio2Frames;      // 1000
    private const int LeadOutLba = Track3Lba + Audio3Frames;     // 1320

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cdburner-mixed-" + Guid.NewGuid().ToString("N"));

    public MixedModeCueBurnTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
        }
    }

    private static byte[] UserData()
    {
        var data = new byte[DataSectors * 2048];
        new Random(11).NextBytes(data);
        return data;
    }

    private static byte[] Noise(int seed, int frames)
    {
        var pcm = new byte[frames * Raw];
        new Random(seed).NextBytes(pcm);
        return pcm;
    }

    /// <summary>Track 1 as a rip has it: Mode 1 sectors from LBA 0, the last 150 of them the post-gap (zeros).</summary>
    private static byte[] DataTrackRaw(byte mode = 1)
    {
        byte[] user = UserData();
        var raw = new byte[(DataSectors + 150) * Raw];
        for (int lba = 0; lba < DataSectors + 150; lba++)
        {
            ReadOnlySpan<byte> sector = lba < DataSectors ? user.AsSpan(lba * 2048, 2048) : new byte[2048];
            RawSectorCodec.Convert2048To2352Mode1(sector, lba).CopyTo(raw, lba * Raw);
            raw[lba * Raw + 15] = mode;
        }
        return raw;
    }

    /// <summary>The whole disc from LBA 0 to the lead-out, as one file.</summary>
    private static byte[] WholeDisc() =>
        [.. DataTrackRaw(), .. new byte[GapFrames * Raw], .. Noise(2, Audio2Frames), .. Noise(3, Audio3Frames)];

    private string SingleFileCue(string mode = "MODE1/2352")
    {
        File.WriteAllBytes(Path.Combine(_dir, "mixed.bin"), WholeDisc());
        string cue = Path.Combine(_dir, "mixed.cue");
        File.WriteAllText(cue, string.Join("\n",
            "TITLE \"Mixed Disc\"",
            "FILE \"mixed.bin\" BINARY",
            $"  TRACK 01 {mode}",
            "    INDEX 01 00:00:00",
            "  TRACK 02 AUDIO",
            "    TITLE \"Song Two\"",
            "    INDEX 00 00:07:25",   // 550
            "    INDEX 01 00:09:25",   // 700
            "  TRACK 03 AUDIO",
            "    TITLE \"Song Three\"",
            "    INDEX 01 00:13:25",   // 1000
            ""));
        return cue;
    }

    /// <summary>One file per track, the pre-gap kept at the head of track 2's file (the usual rip layout).</summary>
    private string FilePerTrackCue()
    {
        File.WriteAllBytes(Path.Combine(_dir, "t1.bin"), DataTrackRaw());
        File.WriteAllBytes(Path.Combine(_dir, "t2.bin"), [.. new byte[GapFrames * Raw], .. Noise(2, Audio2Frames)]);
        File.WriteAllBytes(Path.Combine(_dir, "t3.bin"), Noise(3, Audio3Frames));
        string cue = Path.Combine(_dir, "split.cue");
        File.WriteAllText(cue, string.Join("\n",
            "FILE \"t1.bin\" BINARY",
            "  TRACK 01 MODE1/2352",
            "    INDEX 01 00:00:00",
            "FILE \"t2.bin\" BINARY",
            "  TRACK 02 AUDIO",
            "    INDEX 00 00:00:00",
            "    INDEX 01 00:02:00",
            "FILE \"t3.bin\" BINARY",
            "  TRACK 03 AUDIO",
            "    INDEX 01 00:00:00",
            ""));
        return cue;
    }

    private static byte[] ReadAll(Func<Stream> open)
    {
        using var s = open();
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    private async Task<(BurnOperationResult Result, NativeAudioBurnEngine Engine, VirtualMmcDrive Drive, VirtualDiscMedium Medium)> BurnNative(string cue)
    {
        var compilation = new AudioCdCompilation { RecordingMode = AudioRecordingMode.DiscAtOnceGapless };
        compilation.ImportCueSheet(cue);
        var recorder = new VirtualDiscRecorder();
        recorder.InsertMedia(VirtualDiscMedium.CreateBlankCdRw700());
        VirtualMmcDrive? drive = null;
        var engine = new NativeAudioBurnEngine(_dir) { Wait = _ => { }, ConfigureVirtualDrive = d => drive = d };
        var result = await new AudioCdBurnService(engine).BurnAsync(recorder, compilation, new AudioBurnOptions { UseDiscAtOnce = true });
        return (result, engine, drive!, recorder.CurrentMedium!);
    }

    [Fact]
    public void Import_FindsTheDataTrack_LeavesItsPostGapToTheWriter_AndReadsTheAudioFromTheBin()
    {
        var compilation = new AudioCdCompilation();

        Assert.Equal(3, compilation.ImportCueSheet(SingleFileCue()));

        var data = compilation.Tracks[0];
        Assert.True(data.IsDataTrack);
        Assert.True(compilation.HasDataTrack);
        Assert.Equal(DataSectors, data.DataTrack!.SectorCount);                 // the 150 zero sectors are the post-gap
        Assert.Equal(UserData(), ReadAll(data.DataTrack.OpenUserData));

        Assert.False(compilation.Tracks[1].IsDataTrack);
        Assert.Equal(GapFrames, compilation.Tracks[1].PreGapFrames);
        Assert.Equal("Song Two", compilation.Tracks[1].Title);
        Assert.Equal(Noise(2, Audio2Frames), ReadAll(compilation.Tracks[1].CustomPcmStreamFactory!));
        Assert.Equal(Noise(3, Audio3Frames), ReadAll(compilation.Tracks[2].CustomPcmStreamFactory!));
    }

    [Fact]
    public async Task SingleFileMixedCue_IsWrittenExactlyAsTheFile_AndVerified()
    {
        var (result, engine, drive, medium) = await BurnNative(SingleFileCue());

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(engine.LastResult!.Verification!.Passed, engine.LastResult.Verification.Message);
        Assert.Equal(new[] { 0, Track2Lba, Track3Lba }, drive.RecordedTracks.Select(t => t.Index01Lba));
        Assert.Equal(LeadOutLba, drive.RecordedLeadOutLba);
        Assert.Equal(new byte[] { 0x40, 0x00, 0x00 }, drive.RecordedTracks.Select(t => t.CueControl));

        // From LBA 0 to the lead-out the disc holds the .bin byte for byte: Mode 1 sectors with their headers and
        // error correction, the post-gap, the silent pre-gap, and the audio.
        byte[] program = drive.ProgramAreaWritten.ToArray();
        Assert.Equal(WholeDisc(), program.AsSpan(150 * Raw).ToArray());

        var tracks = Assert.Single(medium.Sessions).Tracks;
        Assert.Equal(new[] { false, true, true }, tracks.Select(t => t.IsAudio));
    }

    [Fact]
    public async Task FilePerTrackCue_GivesTheSameDisc()
    {
        var (result, _, drive, _) = await BurnNative(FilePerTrackCue());

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(new[] { 0, Track2Lba, Track3Lba }, drive.RecordedTracks.Select(t => t.Index01Lba));
        Assert.Equal(WholeDisc(), drive.ProgramAreaWritten.ToArray().AsSpan(150 * Raw).ToArray());
    }

    [Fact]
    public async Task TheWindowsWriter_RefusesAMixedCue_BeforeTouchingTheDisc()
    {
        var compilation = new AudioCdCompilation();
        compilation.ImportCueSheet(SingleFileCue());
        var recorder = new VirtualDiscRecorder();
        recorder.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());

        var result = await new AudioCdBurnService(new VirtualBurnEngine(recorder)).BurnAsync(recorder, compilation);

        Assert.False(result.Success);
        Assert.Contains("Only direct drive commands can write one", result.ErrorMessage);
        Assert.Contains("Nothing was written", result.ErrorMessage);
        Assert.True(recorder.QueryMediaStatus().IsBlank);
    }

    [Fact]
    public async Task ADataTrackMovedOffTrackOne_IsRefused()
    {
        var compilation = new AudioCdCompilation();
        compilation.ImportCueSheet(SingleFileCue());
        var data = compilation.Tracks[0];
        compilation.RemoveTrack(1);
        compilation.AddTrack(data with { TrackNumber = 3 });
        var recorder = new VirtualDiscRecorder();
        recorder.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());

        var result = await new AudioCdBurnService(new NativeAudioBurnEngine(_dir) { Wait = _ => { } }).BurnAsync(recorder, compilation);

        Assert.False(result.Success);
        Assert.Contains("Only track 1 of a mixed-mode CD can be a data track", result.ErrorMessage);
    }

    [Fact]
    public void Mode2DataTrack_IsLeftOut_AsBefore()
    {
        var compilation = new AudioCdCompilation();

        Assert.Equal(2, compilation.ImportCueSheet(SingleFileCue("MODE2/2352")));
        Assert.False(compilation.HasDataTrack);
    }

    [Fact]
    public void ACueThatCallsMode2SectorsMode1_IsCaught()
    {
        File.WriteAllBytes(Path.Combine(_dir, "m2.bin"), [.. DataTrackRaw(mode: 2), .. Noise(2, Audio2Frames)]);
        string cue = Path.Combine(_dir, "m2.cue");
        File.WriteAllText(cue, "FILE \"m2.bin\" BINARY\n  TRACK 01 MODE1/2352\n    INDEX 01 00:00:00\n  TRACK 02 AUDIO\n    INDEX 01 00:07:25\n");

        var ex = Assert.Throws<InvalidDataException>(() => new AudioCdCompilation().ImportCueSheet(cue));
        Assert.Contains("Mode 2 sectors, not Mode 1", ex.Message);
    }

    [Fact]
    public void Mode1_2048DataFile_IsReadAsIs()
    {
        File.WriteAllBytes(Path.Combine(_dir, "data.iso"), UserData());
        File.WriteAllBytes(Path.Combine(_dir, "a.bin"), Noise(2, Audio2Frames));
        string cue = Path.Combine(_dir, "iso.cue");
        File.WriteAllText(cue, "FILE \"data.iso\" BINARY\n  TRACK 01 MODE1/2048\n    INDEX 01 00:00:00\nFILE \"a.bin\" BINARY\n  TRACK 02 AUDIO\n    INDEX 01 00:00:00\n");
        var compilation = new AudioCdCompilation();

        compilation.ImportCueSheet(cue);

        Assert.Equal(DataSectors, compilation.Tracks[0].DataTrack!.SectorCount);
        Assert.Equal(UserData(), ReadAll(compilation.Tracks[0].DataTrack!.OpenUserData));
        Assert.Equal(150, compilation.Tracks[1].PreGapFrames);                   // two seconds after the data
        Assert.Equal(Noise(2, Audio2Frames), ReadAll(compilation.Tracks[1].CustomPcmStreamFactory!));
    }

    [Fact]
    public void MotorolaAudio_IsByteSwappedToLittleEndian()
    {
        byte[] pcm = Noise(5, Audio2Frames);
        byte[] swapped = (byte[])pcm.Clone();
        for (int i = 0; i < swapped.Length; i += 2)
        {
            (swapped[i], swapped[i + 1]) = (swapped[i + 1], swapped[i]);
        }
        File.WriteAllBytes(Path.Combine(_dir, "be.bin"), swapped);
        string cue = Path.Combine(_dir, "be.cue");
        File.WriteAllText(cue, "FILE \"be.bin\" MOTOROLA\n  TRACK 01 AUDIO\n    INDEX 01 00:00:00\n");
        var compilation = new AudioCdCompilation();

        compilation.ImportCueSheet(cue);

        Assert.Equal(pcm, ReadAll(compilation.Tracks[0].CustomPcmStreamFactory!));
        Assert.Equal(Audio2Frames, compilation.Tracks[0].DurationFrames);
    }

    [Fact]
    public void AProjectFile_KeepsTheDataTrackAndTheRawAudio()
    {
        var compilation = new AudioCdCompilation();
        compilation.ImportCueSheet(SingleFileCue());

        var reloaded = ProjectSerializer.ToAudioCdCompilation(ProjectSerializer.FromAudioCdCompilation(compilation));

        Assert.Equal(compilation.Tracks[0].DataTrack, reloaded.Tracks[0].DataTrack);
        Assert.Equal(UserData(), ReadAll(reloaded.Tracks[0].DataTrack!.OpenUserData));
        Assert.Equal(Noise(2, Audio2Frames), ReadAll(reloaded.Tracks[1].CustomPcmStreamFactory!));
        Assert.Equal(Noise(3, Audio3Frames), ReadAll(reloaded.Tracks[2].CustomPcmStreamFactory!));
    }
}
