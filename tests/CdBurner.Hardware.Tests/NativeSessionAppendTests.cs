using System.Buffers.Binary;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Burning;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.NativeWriting;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// Adding data sessions to a disc left open, with the direct writer on the simulated drive: where each session starts
/// (the multi-session layout of the Orange Book for CD; the GP60NB50's measured layout for DVD-R), that the new
/// session's file system is laid out from there, that every file of every session reads back through the last
/// session, and that nothing is written when the image and the disc disagree.
/// </summary>
public sealed class NativeSessionAppendTests : IDisposable
{
    // Orange Book multi-session layout (also what cdrecord -msinfo reports): after the first session's lead-out start
    // come 6,750 lead-out + 4,500 lead-in + 150 pre-gap sectors before the next track; after later sessions
    // 2,250 + 4,500 + 150.
    private const int CdGapAfterFirstSession = 11_400;
    private const int CdGapAfterLaterSessions = 6_900;
    private const int CdRunOutSectors = 2; // Track-at-Once: two run-out blocks end each track

    // GP60NB50, DVD-R: a 36,032-sector first session left open reported its lead-out at 65,264 and the next session at
    // 93,952; a 102,464-sector one was followed by a session at 131,152 (2026-11-10/11).
    private const int DvdFirstSessionMinimumEnd = 65_264;
    private const int DvdGap = 28_688;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cdburner-test-" + Guid.NewGuid().ToString("N"));

    public NativeSessionAppendTests() => Directory.CreateDirectory(_dir);

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

    private (NativeDataBurnEngine Engine, List<VirtualMmcDrive> Drives) Engine()
    {
        var drives = new List<VirtualMmcDrive>();
        var engine = new NativeDataBurnEngine(logDirectory: _dir)
        {
            Wait = _ => { },
            ConfigureVirtualDrive = d =>
            {
                drives.Add(d);
                d.WriteSpeedPerformanceResponse = Speeds(1764, 1411);
            }
        };
        return (engine, drives);
    }

    private static VirtualDiscRecorder Recorder(VirtualDiscMedium medium)
    {
        var recorder = new VirtualDiscRecorder();
        recorder.InsertMedia(medium);
        return recorder;
    }

    private static byte[] Content(string name, int bytes)
    {
        byte[] data = new byte[bytes];
        new Random(name.GetHashCode(StringComparison.Ordinal) ^ bytes).NextBytes(data);
        return data;
    }

    /// <summary>The files of session <paramref name="session"/>: two at the root, two in a folder of their own.</summary>
    private static Dictionary<string, byte[]> SessionFiles(int session) => new()
    {
        [$"S{session}_A.BIN"] = Content($"a{session}", 70_000 + 1_000 * session),
        [$"S{session}_B.TXT"] = Content($"b{session}", 513),
        [$"DIR{session}/C.BIN"] = Content($"c{session}", 300_000),
        [$"DIR{session}/D.BIN"] = Content($"d{session}", 2_048 * 3)
    };

    /// <summary>
    /// What the command line and the app do for a data disc: import the last session of an open disc, add the new
    /// files, and let <see cref="DataBurnService"/> lay the image out from the disc's next writable address and read it
    /// back afterwards.
    /// </summary>
    private static async Task<DataBurnResult> BurnSessionAsync(VirtualDiscRecorder recorder, IBurnEngineBackend engine,
        Dictionary<string, byte[]> files, bool closeDisc)
    {
        var status = recorder.QueryMediaStatus();
        var root = !status.IsBlank && status.IsAppendable
            ? await MultiSessionManager.ImportFromRecorderAsync(recorder, engine)
            : CompilationDirectory.CreateRoot();
        foreach (var (path, data) in files)
        {
            string[] parts = path.Split('/');
            var directory = parts.Length > 1 ? root.AddDirectory(parts[0]) : root;
            directory.AddFile(new CompilationFile(parts[^1], () => new MemoryStream(data)));
        }

        return await new DataBurnService(engine).BurnCompilationAsync(recorder, root,
            new DataBurnOptions { CloseDisc = closeDisc, VerifyAfterBurn = true },
            new IsoBuildOptions(VolumeLabel: "SESSIONS", IncludeUdf: false));
    }

    public static TheoryData<DiscMediaType, int> AppendCases() => new()
    {
        { DiscMediaType.CdR, 2 },
        { DiscMediaType.CdR, 3 },
        { DiscMediaType.DvdMinusR, 2 },
        { DiscMediaType.DvdMinusR, 3 }
    };

    [Theory]
    [MemberData(nameof(AppendCases))]
    public async Task SessionsAdded_StartAtTheDrivesNextAddress_AndEveryFileReadsBackThroughTheLastSession(DiscMediaType type, int sessions)
    {
        var (engine, drives) = Engine();
        using var medium = type == DiscMediaType.CdR ? VirtualDiscMedium.CreateBlankCdR700() : VirtualDiscMedium.CreateBlankDvdR47();
        var recorder = Recorder(medium);
        var allFiles = new Dictionary<string, byte[]>();
        var starts = new List<int>();
        var fileLbaInOwnSession = new Dictionary<string, int>();

        for (int n = 1; n <= sessions; n++)
        {
            int expectedStart = n == 1 ? 0 : recorder.QueryMediaStatus().NextWritableLba;
            var files = SessionFiles(n);
            bool last = n == sessions;
            var result = await BurnSessionAsync(recorder, engine, files, closeDisc: last);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.True(result.Verified); // FUA read-back of the new session from its first sector
            Assert.Null(engine.LastFallbackReason); // written with the direct writer, not handed to Windows
            Assert.True(engine.LastResult!.DiscRecorded);
            if (n > 1)
            {
                Assert.Contains($"Session {n} was added at sector {expectedStart:N0}", engine.LastResult.Message);
                Assert.Contains($"Track {n} starts at sector {expectedStart:N0}", engine.LastResult.Message);
            }
            Assert.Equal(n, medium.Sessions.Count);
            Assert.Equal(expectedStart, medium.Sessions[^1].StartLba);
            Assert.Equal(last, medium.IsFinalized);
            starts.Add(expectedStart);

            foreach (var (path, data) in files)
            {
                allFiles[path] = data;
            }
            using var own = IsoImageReader.FromStream(medium.CreateReadStream(), sessionStartLba: expectedStart);
            foreach (var path in files.Keys)
            {
                fileLbaInOwnSession[path] = own.FindFile(path)!.StartLba;
            }
        }

        // Where the drive put each session: the next one after the previous lead-out and the gap of that disc type.
        for (int n = 2; n <= sessions; n++)
        {
            var previous = medium.Sessions[n - 2];
            int end = previous.StartLba + previous.SectorCount;
            int expected = type == DiscMediaType.CdR
                ? end + CdRunOutSectors + (n == 2 ? CdGapAfterFirstSession : CdGapAfterLaterSessions)
                : (n == 2 ? Math.Max(end, DvdFirstSessionMinimumEnd) : end) + DvdGap;
            Assert.Equal(expected, starts[n - 1]);
            if (type == DiscMediaType.DvdMinusR)
            {
                Assert.Equal(0, starts[n - 1] % 16); // a DVD session starts on an ECC block
            }
        }

        // The disc as a reader sees it: the last session's volume lists every file of every session, each intact.
        int lastStart = starts[^1];
        using var reader = IsoImageReader.FromStream(medium.CreateReadStream(), sessionStartLba: lastStart);
        Assert.Equal(allFiles.Count, reader.AllFiles.Count);
        foreach (var (path, data) in allFiles)
        {
            var entry = reader.FindFile(path);
            Assert.NotNull(entry);
            Assert.Equal(data, reader.ReadFileBytes(entry!));
        }

        // Every extent the new session wrote lies at or after its start; files kept from earlier sessions still point
        // at the sectors they were written to there (they are not copied).
        Assert.True(reader.RootDirectory.StartLba >= lastStart);
        Assert.All(reader.AllDirectories, d => Assert.True(d.StartLba >= lastStart, $"{d.VirtualPath} at {d.StartLba}"));
        foreach (var path in allFiles.Keys)
        {
            var entry = reader.FindFile(path)!;
            if (path.StartsWith($"S{sessions}_", StringComparison.Ordinal) || path.StartsWith($"DIR{sessions}/", StringComparison.Ordinal))
            {
                Assert.True(entry.StartLba >= lastStart, $"{path} at {entry.StartLba}, session from {lastStart}");
            }
            else
            {
                Assert.True(entry.StartLba < lastStart);
                Assert.Equal(fileLbaInOwnSession[path], entry.StartLba);
            }
        }

        // One drive per burn, and each later one was asked for the invisible track before anything was written.
        Assert.Equal(sessions, drives.Count);
    }

    [Fact]
    public async Task SessionAddedToADvdPlusR_IsWrittenDirectly_AndReadsBack()
    {
        var (engine, _) = Engine();
        using var medium = new VirtualDiscMedium(DiscMediaType.DvdPlusR, OpticalConstants.DvdPlusR5Sectors);
        var recorder = Recorder(medium);

        Assert.True((await BurnSessionAsync(recorder, engine, SessionFiles(1), closeDisc: false)).Success);
        int next = recorder.QueryMediaStatus().NextWritableLba;
        var second = await BurnSessionAsync(recorder, engine, SessionFiles(2), closeDisc: true);

        Assert.True(second.Success, second.ErrorMessage);
        Assert.True(second.Verified);
        Assert.Null(engine.LastFallbackReason);
        Assert.True(medium.IsFinalized);
        using var reader = IsoImageReader.FromStream(medium.CreateReadStream(), sessionStartLba: next);
        foreach (var (path, data) in SessionFiles(1).Concat(SessionFiles(2)))
        {
            Assert.Equal(data, reader.ReadFileBytes(path));
        }
    }

    [Fact]
    public async Task OpenDisc_IsNeverErased_AndAnImageNotBuiltForTheNextSessionIsNotWritten()
    {
        // An image built for sector 0 (an .iso file) or one with no ISO 9660 volume would be unreadable as the next
        // session; even with leave to replace data, the earlier session is kept and nothing is written.
        var (engine, drives) = Engine();
        using var medium = VirtualDiscMedium.CreateBlankCdRw700();
        var recorder = Recorder(medium);
        Assert.True((await BurnSessionAsync(recorder, engine, SessionFiles(1), closeDisc: false)).Success);
        int firstSectors = medium.Sessions[0].SectorCount;
        byte[] firstSession = new byte[firstSectors * 2048];
        medium.ReadSectors(0, firstSectors, firstSession, 2048);

        var isoForSectorZero = new MemoryStream();
        var root = CompilationDirectory.CreateRoot();
        root.AddFile(new CompilationFile("NEW.BIN", () => new MemoryStream(Content("new", 9_000))));
        IsoUdfImageBuilder.Build(root, isoForSectorZero, new IsoBuildOptions(VolumeLabel: "AT_ZERO", IncludeUdf: false));
        byte[] notIso = Content("raw", 40 * 2048);

        foreach (bool overwrite in new[] { false, true })
        {
            foreach (byte[] image in new[] { isoForSectorZero.ToArray(), notIso })
            {
                var result = await new DataBurnService(engine).BurnStreamAsync(recorder, new MemoryStream(image),
                    new DataBurnOptions { CloseDisc = false, OverwriteExistingData = overwrite, VerifyAfterBurn = true });

                Assert.False(result.Success);
                Assert.Contains("Nothing was written to the disc", result.ErrorMessage);
                Assert.Null(engine.LastFallbackReason);
                Assert.Equal(0, drives[^1].WriteCommandCount);
                Assert.Single(medium.Sessions);
                Assert.False(medium.IsFinalized);
                byte[] now = new byte[firstSession.Length];
                medium.ReadSectors(0, firstSectors, now, 2048);
                Assert.Equal(firstSession, now);
            }
        }
    }

    [Fact]
    public async Task OpenBluRayDisc_GoesToTheWindowsWriter_WithTheReason()
    {
        var (engine, drives) = Engine();
        using var medium = VirtualDiscMedium.CreateBlankBluRay(DiscMediaType.BdR);
        var recorder = Recorder(medium);
        var first = await new DataBurnService(new VirtualBurnEngine()).BurnStreamAsync(recorder, new MemoryStream(IsoAt(0, "BD.BIN", 64 * 2048)),
            new DataBurnOptions { CloseDisc = false });
        Assert.True(first.Success, first.ErrorMessage);
        Assert.False(medium.IsFinalized);

        var second = await BurnSessionAsync(recorder, engine, SessionFiles(2), closeDisc: false);

        Assert.True(second.Success, second.ErrorMessage);
        Assert.Contains("adds sessions to CD-R, CD-RW, DVD-R, DVD-RW and DVD+R", engine.LastFallbackReason);
        Assert.Empty(drives); // the direct writer never opened the drive
    }

    // ---- Writer level: one simulated drive, several runs ----

    private static byte[] IsoAt(int sessionStartLba, string fileName, int bytes)
    {
        var root = CompilationDirectory.CreateRoot();
        root.AddFile(new CompilationFile(fileName, () => new MemoryStream(Content(fileName, bytes))));
        var image = new MemoryStream();
        IsoUdfImageBuilder.Build(root, image, new IsoBuildOptions(VolumeLabel: "S", IncludeUdf: false), sessionStartLba);
        return image.ToArray();
    }

    private static NativeDataWriteResult Write(VirtualMmcDrive drive, byte[] image, int? sessionStart, bool testWrite = false, bool closeDisc = false) =>
        new NativeTaoDataWriter(drive, sleep: _ => { }).Write(
            new NativeDataWriteRequest(new MemoryStream(image), 0, image.Length, testWrite, closeDisc, SessionStartLba: sessionStart));

    private static VirtualMmcDrive CdLeftOpen(out int nextSession)
    {
        var drive = new VirtualMmcDrive { Profile = NativeSaoAudioWriter.ProfileCdR, WriteSpeedPerformanceResponse = Speeds(1764) };
        var first = Write(drive, IsoAt(0, "ONE.BIN", 50_000), sessionStart: null);
        Assert.True(first.Success, first.Message);
        Assert.Equal(1, drive.DiscStatus);
        nextSession = drive.NextSessionAddress!.Value;
        return drive;
    }

    [Fact]
    public void ImageBuiltForAnotherAddress_IsNotWritten()
    {
        var drive = CdLeftOpen(out int next);
        int writes = drive.WriteCommandCount;

        var result = Write(drive, IsoAt(next + 32, "TWO.BIN", 9_000), sessionStart: next + 32);

        Assert.True(result.StoppedBeforeWriting);
        Assert.Contains($"next writable address is sector {next:N0}", result.Message);
        Assert.Equal(writes, drive.WriteCommandCount);
        Assert.Single(drive.DataSessions);
    }

    [Fact]
    public void SessionImage_OnABlankDisc_IsNotWritten()
    {
        var drive = new VirtualMmcDrive { Profile = NativeSaoAudioWriter.ProfileCdR };

        var result = Write(drive, IsoAt(20_000, "TWO.BIN", 9_000), sessionStart: 20_000);

        Assert.True(result.StoppedBeforeWriting);
        Assert.Contains("blank", result.Message);
        Assert.Equal(0, drive.WriteCommandCount);
    }

    [Fact]
    public void ClosedDisc_TakesNoNewSession()
    {
        var drive = new VirtualMmcDrive { Profile = NativeSaoAudioWriter.ProfileCdR, WriteSpeedPerformanceResponse = Speeds(1764) };
        Assert.True(Write(drive, IsoAt(0, "ONE.BIN", 50_000), sessionStart: null, closeDisc: true).Success);
        Assert.Equal(2, drive.DiscStatus);
        int writes = drive.WriteCommandCount;

        var result = Write(drive, IsoAt(20_000, "TWO.BIN", 9_000), sessionStart: 20_000);

        Assert.True(result.StoppedBeforeWriting);
        Assert.Contains("closed", result.Message);
        Assert.Equal(writes, drive.WriteCommandCount);
    }

    [Fact]
    public void TestWriteOfASession_LeavesTheDiscAsItWas_ThenTheRealWriteAddsIt()
    {
        var drive = CdLeftOpen(out int next);
        byte[] image = IsoAt(next, "TWO.BIN", 9_000);

        var test = Write(drive, image, sessionStart: next, testWrite: true);

        Assert.True(test.Success, test.Message);
        Assert.True(test.DiscStillBlankAfterTestWrite);
        Assert.Single(drive.DataSessions);
        Assert.Equal(next, drive.NextSessionAddress);

        var real = Write(drive, image, sessionStart: next, closeDisc: true);

        Assert.True(real.Success, real.Message);
        Assert.Equal(2, drive.DataSessions.Count);
        Assert.Equal(next, drive.DataSessions[1].TrackStartLba);
        Assert.Equal(2, drive.DiscStatus);
        Assert.Null(drive.NextSessionAddress);
        // Multi-session field 00b for the session that closes the disc (MMC mode page 05h byte 3 bits 7-6).
        Assert.Equal(0, drive.CurrentModePage05[3] >> 6);
    }

    [Fact]
    public void SessionAddedToADvdMinusR_IsWrittenIncrementally_EvenWhenItClosesTheDisc()
    {
        var drive = new VirtualMmcDrive
        {
            Profile = NativeTaoDataWriter.ProfileDvdMinusR,
            CapacitySectors = OpticalConstants.DvdMinusR5Sectors,
            WriteSpeedPerformanceResponse = Speeds(11_080)
        };
        Assert.True(Write(drive, IsoAt(0, "ONE.BIN", 50_000), sessionStart: null).Success);
        int next = drive.NextSessionAddress!.Value;

        var result = Write(drive, IsoAt(next, "TWO.BIN", 70_000), sessionStart: next, closeDisc: true);

        Assert.True(result.Success, result.Message);
        Assert.StartsWith("incremental", result.WriteMode); // Disc-At-Once needs a blank disc
        Assert.DoesNotContain(NativeWriteCommands.OpCodeReserveTrack, drive.CommandsReceived);
        Assert.Equal(2, drive.DiscStatus);
        Assert.Equal(next, drive.DataSessions[1].TrackStartLba);
    }
}
