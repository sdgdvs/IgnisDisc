using System.Buffers.Binary;
using CdBurner.Core.Audio;
using CdBurner.Core.Compilation;
using CdBurner.Core.Images;
using CdBurner.Core.Iso;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Scsi;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// The second session of a CD-Extra disc on the simulated drive: audio written Session-at-Once and left open, then one
/// Mode 1 data track written Track-at-Once at the next writable address the drive reports for the invisible track,
/// from an image laid out from that address, and the disc closed. The volume is read back through READ (10) only.
/// </summary>
public class CdExtraDataSessionTests
{
    private const int AudioSectorSize = 2352;
    private const int DataSectorSize = 2048;
    private const int TrackSectors = 300;

    // Two audio tracks: track 1 at LBA 0, track 2 at 450 after a 150-sector pre-gap holding audio; lead-out at 750.
    private const int AudioLeadOutLba = 750;

    // Orange Book multi-session layout: after the first session's lead-out start come 6,750 lead-out + 4,500 lead-in
    // + 150 pre-gap sectors before the next session's first track (cdrecord -msinfo reports the same address).
    private const int SessionTwoStartLba = AudioLeadOutLba + 6_750 + 4_500 + 150;

    private static byte[] Pcm(int seed, int sectors)
    {
        byte[] pcm = new byte[sectors * AudioSectorSize];
        new Random(seed).NextBytes(pcm);
        return pcm;
    }

    private static byte[] Content(int seed, int bytes)
    {
        byte[] data = new byte[bytes];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>A GET PERFORMANCE type 03h reply listing these write speeds (kB/s).</summary>
    private static byte[] Speeds(params int[] kiloBytesPerSecond)
    {
        byte[] r = new byte[8 + 16 * kiloBytesPerSecond.Length];
        BinaryPrimitives.WriteUInt32BigEndian(r, (uint)(r.Length - 4));
        for (int i = 0; i < kiloBytesPerSecond.Length; i++)
        {
            var d = r.AsSpan(8 + 16 * i, 16);
            BinaryPrimitives.WriteUInt32BigEndian(d[4..], 359_849);
            BinaryPrimitives.WriteUInt32BigEndian(d[8..], (uint)kiloBytesPerSecond[i]);
            BinaryPrimitives.WriteUInt32BigEndian(d[12..], (uint)kiloBytesPerSecond[i]);
        }
        return r;
    }

    private static readonly Dictionary<string, byte[]> SessionFiles = new()
    {
        ["README.TXT"] = Content(1, 1_234),
        ["VIDEO.BIN"] = Content(2, 400_000),
        ["EXTRAS/LYRICS.TXT"] = Content(3, 5_000),
        ["EXTRAS/PHOTOS/COVER.BIN"] = Content(4, 2_048 * 7)
    };

    /// <summary>A CD-R with two audio tracks written Session-at-Once and left open for another session.</summary>
    private static VirtualMmcDrive DiscWithOpenAudioSession()
    {
        var drive = new VirtualMmcDrive
        {
            Profile = NativeSaoAudioWriter.ProfileCdR,
            WriteSpeedPerformanceResponse = Speeds(1764, 1411)
        };
        var packs = CdTextPackBuilder.BuildPacks(new CdTextAlbumMetadata("Album", "Artist",
            Tracks: new[] { new CdTextTrackMetadata(1, "One"), new CdTextTrackMetadata(2, "Two") }));
        var audio = new NativeSaoAudioWriter(drive, sleep: _ => { }).Write(new NativeSaoWriteRequest(
            new[] { new SaoTrack(1, TrackSectors, 150), new SaoTrack(2, TrackSectors, 150, PreGapAudioSectors: 150) },
            i => new MemoryStream(Pcm(i + 1, TrackSectors + (i == 1 ? 150 : 0))),
            packs,
            MediaCatalogNumber: null,
            TestWrite: false) { LeaveSessionOpen = true });
        Assert.True(audio.Success, audio.Message);
        Assert.True(audio.DiscLeftOpen);
        return drive;
    }

    private static byte[] Execute(VirtualMmcDrive drive, byte[] cdb, int length)
    {
        byte[] buffer = new byte[length];
        var r = drive.ExecuteCommand(cdb, buffer, ScsiPassThroughNative.SCSI_IOCTL_DATA_IN, TimeSpan.FromSeconds(5));
        Assert.True(r.IsSuccess, $"command 0x{cdb[0]:X2} failed");
        return buffer.AsSpan(0, (int)r.DataTransferLength).ToArray();
    }

    private static byte[] DiscInformation(VirtualMmcDrive drive) => Execute(drive, MmcCommandBuilder.BuildReadDiscInformationCdb(34), 34);

    private static byte[] TrackInformation(VirtualMmcDrive drive, int number) =>
        Execute(drive, NativeWriteCommands.BuildReadTrackInformation(number), 36);

    /// <summary>The ISO 9660 + Joliet image of <see cref="SessionFiles"/>, laid out to start at <paramref name="startLba"/>.</summary>
    private static MemoryStream SessionImage(int startLba)
    {
        var root = CompilationDirectory.CreateRoot();
        foreach (var (path, data) in SessionFiles)
        {
            int slash = path.LastIndexOf('/');
            var directory = slash < 0 ? root : root.AddDirectory(path[..slash]);
            string[] parts = path.Split('/');
            directory.AddFile(new CompilationFile(parts[^1], () => new MemoryStream(data)));
        }
        return IsoUdfImageBuilder.BuildToMemoryStream(root, new IsoBuildOptions(VolumeLabel: "EXTRA", IncludeUdf: false), startLba);
    }

    private static NativeDataWriteResult WriteDataSession(VirtualMmcDrive drive, MemoryStream image, int? sessionStartLba) =>
        new NativeTaoDataWriter(drive, sleep: _ => { }).Write(
            new NativeDataWriteRequest(image, 0, image.Length, TestWrite: false, CloseDisc: true, SessionStartLba: sessionStartLba), null, default);

    /// <summary>Sectors read with READ (10) through the drive, as a reader of the disc gets them.</summary>
    private static SectorReadStream DiscStream(VirtualMmcDrive drive, int totalSectors)
    {
        string? Read(int lba, int count, byte[] buffer)
        {
            int n = 0;
            var r = PhysicalSectorReader.ReadDataSectorsAsync(drive, lba, count, DriveCacheBypass.EveryRead,
                (_, d) => { d.Span.CopyTo(buffer.AsSpan(n++ * DataSectorSize)); return ValueTask.CompletedTask; }, null, default)
                .GetAwaiter().GetResult();
            return r.Success ? null : r.ErrorMessage;
        }
        return new SectorReadStream(Read, totalSectors);
    }

    [Fact]
    public void OpenAudioDisc_ReportsTheInvisibleTrackOfSessionTwo()
    {
        var drive = DiscWithOpenAudioSession();

        // READ DISC INFORMATION (MMC-5 6.22.3.1): byte 2 bits 1-0 disc status 01b (incomplete), bits 3-2 state of the
        // last session 00b (empty); byte 4 number of sessions; bytes 5 and 6 first and last track in the last session.
        // On an open disc the empty last session is counted, and its one track is the invisible track after the audio.
        byte[] info = DiscInformation(drive);
        Assert.Equal(1, info[2] & 0x03);
        Assert.Equal(0, (info[2] >> 2) & 0x03);
        Assert.Equal(1, info[3]);   // first track on the disc
        Assert.Equal(2, info[4]);   // sessions: the audio one and the empty one
        Assert.Equal(3, info[5]);
        Assert.Equal(3, info[6]);

        // READ TRACK INFORMATION of track 3 (MMC-5 6.27.3): session 2, blank (byte 6 bit 6), NWA valid (byte 7 bit 0),
        // track start and next writable address at the start of session 2's program area.
        byte[] invisible = TrackInformation(drive, 3);
        Assert.Equal(3, invisible[2]);
        Assert.Equal(2, invisible[3]);
        Assert.NotEqual(0, invisible[6] & 0x40);
        Assert.NotEqual(0, invisible[7] & 0x01);
        Assert.Equal(SessionTwoStartLba, BinaryPrimitives.ReadInt32BigEndian(invisible.AsSpan(8)));
        Assert.Equal(SessionTwoStartLba, BinaryPrimitives.ReadInt32BigEndian(invisible.AsSpan(12)));

        // The audio tracks themselves: session 1, recorded, audio track mode (control bit 2 clear), at their INDEX 01.
        byte[] first = TrackInformation(drive, 1);
        Assert.Equal(1, first[3]);
        Assert.Equal(0, first[5] & 0x04);
        Assert.Equal(0, first[6] & 0x40);
        Assert.Equal(0, BinaryPrimitives.ReadInt32BigEndian(first.AsSpan(8)));
        byte[] second = TrackInformation(drive, 2);
        Assert.Equal(450, BinaryPrimitives.ReadInt32BigEndian(second.AsSpan(8)));
        Assert.Equal(AudioLeadOutLba - 450, BinaryPrimitives.ReadInt32BigEndian(second.AsSpan(24)));
    }

    [Fact]
    public void DataSession_IsWrittenAtTheNextWritableAddress_ClosesTheDisc_AndItsVolumeReadsBack()
    {
        var drive = DiscWithOpenAudioSession();
        byte[] audioBefore = drive.RecordedProgram();

        // The address the image is built for comes from the drive: the invisible track named by READ DISC INFORMATION.
        int invisibleTrack = DiscInformation(drive)[6];
        int nwa = BinaryPrimitives.ReadInt32BigEndian(TrackInformation(drive, invisibleTrack).AsSpan(12));
        Assert.Equal(SessionTwoStartLba, nwa);

        using var image = SessionImage(nwa);
        int imageSectors = (int)(image.Length / DataSectorSize);
        var result = WriteDataSession(drive, image, nwa);

        Assert.True(result.Success, result.Message);
        Assert.True(result.DiscRecorded);
        Assert.Contains($"Session 2 was added at sector {nwa:N0}", result.Message);
        Assert.Contains($"Track 3 starts at sector {nwa:N0}", result.Message);
        Assert.Contains("disc closed", result.Message);

        // Closed: status 10b (complete), last session complete (11b), two sessions, track 3 alone in the last one.
        byte[] info = DiscInformation(drive);
        Assert.Equal(2, info[2] & 0x03);
        Assert.Equal(3, (info[2] >> 2) & 0x03);
        Assert.Equal(2, info[4]);
        Assert.Equal(3, info[5]);
        Assert.Equal(3, info[6]);

        // READ TOC format 0: the two audio tracks unchanged, then data track 3 (control bit 2 set) at the NWA, and the
        // lead-out after the image and the two Track-at-Once run-out blocks.
        byte[] toc = Execute(drive, MmcCommandBuilder.BuildReadTocPmaAtipCdb(0x00, 1, 804), 804);
        Assert.Equal(1, toc[2]);
        Assert.Equal(3, toc[3]);
        Assert.Equal(4 + 8 * 4, (toc[0] << 8 | toc[1]) + 2);
        var tracks = Enumerable.Range(0, 4).Select(i => toc.AsSpan(4 + 8 * i, 8).ToArray()).ToArray();
        Assert.Equal(new byte[] { 1, 2, 3, 0xAA }, tracks.Select(t => t[2]).ToArray());
        Assert.Equal(0, tracks[0][1] & 0x04);
        Assert.Equal(0, tracks[1][1] & 0x04);
        Assert.NotEqual(0, tracks[2][1] & 0x04);
        Assert.Equal(0, BinaryPrimitives.ReadInt32BigEndian(tracks[0].AsSpan(4)));
        Assert.Equal(450, BinaryPrimitives.ReadInt32BigEndian(tracks[1].AsSpan(4)));
        Assert.Equal(nwa, BinaryPrimitives.ReadInt32BigEndian(tracks[2].AsSpan(4)));
        int leadOut = BinaryPrimitives.ReadInt32BigEndian(tracks[3].AsSpan(4));
        Assert.Equal(nwa + Math.Max(imageSectors, 300) + 2, leadOut);

        // The audio of session 1 is untouched.
        Assert.Equal(audioBefore, drive.RecordedProgram());

        // Session 2's volume, read through the drive from its start: every file intact.
        using var reader = IsoImageReader.FromStream(DiscStream(drive, leadOut), sessionStartLba: nwa);
        Assert.Equal(SessionFiles.Count, reader.AllFiles.Count);
        foreach (var (path, data) in SessionFiles)
        {
            var entry = reader.FindFile(path);
            Assert.NotNull(entry);
            Assert.Equal(data, reader.ReadFileBytes(entry!));
        }

        // Every extent of session 2 lies at or after the NWA and inside the track: both directory trees and all files.
        int trackEnd = nwa + imageSectors;
        foreach (var root in new[] { reader.IsoRootDirectory!, reader.JolietRootDirectory! })
        {
            var stack = new Stack<IsoDirectoryEntry>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var directory = stack.Pop();
                Assert.InRange(directory.StartLba, nwa, trackEnd - 1);
                foreach (var file in directory.Files)
                {
                    Assert.InRange(file.StartLba, nwa, trackEnd - 1);
                    Assert.True(file.StartLba + (file.SizeBytes + DataSectorSize - 1) / DataSectorSize <= trackEnd, file.VirtualPath);
                }
                foreach (var sub in directory.SubDirectories)
                {
                    stack.Push(sub);
                }
            }
        }

        // The volume descriptors read straight off the disc (ECMA-119 8.4/8.5): sector NWA+16 the primary, NWA+17 the
        // Joliet supplementary one. Bytes 80-83 volume space size (both-endian, LE half), 140-143 type L path table
        // (LE), 148-151 type M path table (BE), 158-161 the root directory extent (LE half of the record at 156).
        using var disc = DiscStream(drive, leadOut);
        byte[] descriptor = new byte[DataSectorSize];
        foreach (var (lba, type) in new[] { (nwa + 16, 1), (nwa + 17, 2) })
        {
            disc.Position = (long)lba * DataSectorSize;
            disc.ReadExactly(descriptor);
            Assert.Equal(type, descriptor[0]);
            Assert.Equal("CD001", System.Text.Encoding.ASCII.GetString(descriptor, 1, 5));
            Assert.InRange(BinaryPrimitives.ReadInt32LittleEndian(descriptor.AsSpan(80)), nwa + 18, trackEnd);
            Assert.InRange(BinaryPrimitives.ReadInt32LittleEndian(descriptor.AsSpan(140)), nwa, trackEnd - 1);
            Assert.InRange(BinaryPrimitives.ReadInt32BigEndian(descriptor.AsSpan(148)), nwa, trackEnd - 1);
            Assert.InRange(BinaryPrimitives.ReadInt32LittleEndian(descriptor.AsSpan(158)), nwa, trackEnd - 1);
        }
    }

    [Fact]
    public void ImageBuiltForSectorZero_IsNotWrittenAfterTheAudio()
    {
        var drive = DiscWithOpenAudioSession();
        int writesBefore = drive.WriteCommandCount;

        // An image laid out from sector 0 would point into the audio session; told it starts at the NWA, the writer
        // finds its root directory outside the new session and stops before writing.
        using var wrongLayout = SessionImage(0);
        var result = WriteDataSession(drive, wrongLayout, SessionTwoStartLba);

        Assert.False(result.Success);
        Assert.True(result.StoppedBeforeWriting);
        Assert.Contains("not built to start at sector", result.Message);
        Assert.Equal(writesBefore, drive.WriteCommandCount);
        Assert.Equal(1, DiscInformation(drive)[2] & 0x03); // still open
    }

    [Fact]
    public void ImageForTheWrongAddress_IsNotWritten()
    {
        var drive = DiscWithOpenAudioSession();
        int writesBefore = drive.WriteCommandCount;

        // Built for the address a data first session of the same length would give (lead-out + 6,900, a later session):
        // the drive's NWA differs, so nothing is written.
        using var image = SessionImage(AudioLeadOutLba + 6_900);
        var result = WriteDataSession(drive, image, AudioLeadOutLba + 6_900);

        Assert.False(result.Success);
        Assert.True(result.StoppedBeforeWriting);
        Assert.Contains($"next writable address is sector {SessionTwoStartLba:N0}", result.Message);
        Assert.Equal(writesBefore, drive.WriteCommandCount);
    }
}
