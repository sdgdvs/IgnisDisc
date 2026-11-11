using CdBurner.Core.Abstractions;
using CdBurner.Core.Burning;
using CdBurner.Core.Compilation;
using CdBurner.Core.Images;
using CdBurner.Core.Iso;
using CdBurner.Hardware.Scsi;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// Roadmap M1.3: post-burn verify reads the disc, not the drive's cache (READ (10) with FUA, with a cache flush for
/// drives that refuse FUA), names the file that owns a bad sector, and checks the disc's own SHA256SUMS/BLAKE3SUMS
/// from raw sectors with no drive letter.
/// </summary>
public class VerifyReadBackTests
{
    /// <summary>
    /// An engine that "burns" into a <see cref="VirtualMmcDrive"/> and reads back through the real
    /// <see cref="PhysicalSectorReader"/> SCSI path, so every verify command can be inspected.
    /// </summary>
    private sealed class MmcDataEngine(VirtualMmcDrive drive) : IBurnEngineBackend
    {
        /// <summary>Damage this sector on the disc after the burn (the drive's memory still holds the good copy).</summary>
        public int? DamageLba { get; set; }

        /// <summary>Sectors the drive keeps in its memory after the burn, with the bytes it was sent.</summary>
        public Func<int, bool> KeepInDriveMemory { get; set; } = _ => true;

        public async Task<BurnOperationResult> BurnDataStreamAsync(IDiscRecorderBackend recorder, Stream imageStream, DataBurnOptions options,
            IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken)
        {
            using var ms = new MemoryStream();
            await imageStream.CopyToAsync(ms, cancellationToken);
            byte[] sent = ms.ToArray();
            Array.Resize(ref sent, (sent.Length + 2047) / 2048 * 2048);
            byte[] disc = (byte[])sent.Clone();
            if (DamageLba is int bad)
            {
                disc[bad * 2048 + 7] ^= 0x5A;
            }
            drive.DataDiscImage = disc;
            for (int lba = 0; lba < sent.Length / 2048; lba++)
            {
                if (KeepInDriveMemory(lba))
                {
                    drive.StaleReadCache[lba] = sent.AsSpan(lba * 2048, 2048).ToArray();
                }
            }
            return BurnOperationResult.Succeeded(sent.Length / 2048, TimeSpan.Zero);
        }

        public Task<RipOperationResult> ReadSectorsAsync(IDiscRecorderBackend recorder, int startLba, int sectorCount, bool raw2352Mode,
            Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken) =>
            PhysicalSectorReader.ReadDataSectorsAsync(drive, startLba, sectorCount, DriveCacheBypass.None, sectorConsumer, progress, cancellationToken);

        public Task<RipOperationResult> ReadSectorsForVerifyAsync(IDiscRecorderBackend recorder, int startLba, int sectorCount,
            Func<int, ReadOnlyMemory<byte>, ValueTask> sectorConsumer, IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken,
            bool strict = false) =>
            PhysicalSectorReader.ReadDataSectorsAsync(drive, startLba, sectorCount,
                strict ? DriveCacheBypass.EveryRead : DriveCacheBypass.RecentlyWritten, sectorConsumer, progress, cancellationToken);

        public Task<BurnOperationResult> BurnAudioTracksAsync(IDiscRecorderBackend recorder, IReadOnlyList<AudioTrackSource> tracks, AudioBurnOptions options,
            IProgress<BurnProgressSnapshot>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<BurnOperationResult> EraseMediaAsync(IDiscRecorderBackend recorder, bool fullErase, IProgress<BurnProgressSnapshot>? progress,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static CompilationDirectory CreateTree()
    {
        var rng = new Random(7);
        var root = CompilationDirectory.CreateRoot();
        var docs = root.AddDirectory("Docs");
        foreach (var (dir, name, size) in new[] { (root, "a.bin", 50_000), (docs, "report.pdf", 120_000), (docs, "notes.txt", 3_000), (root, "empty.txt", 0) })
        {
            byte[] data = new byte[size];
            rng.NextBytes(data);
            dir.AddFile(name, data);
        }
        return root;
    }

    private static byte[] BuildImage(bool manifests = true) =>
        IsoUdfImageBuilder.BuildToMemoryStream(CreateTree(),
            new IsoBuildOptions(WriteSha256Manifest: manifests, WriteBlake3Manifest: manifests)).ToArray();

    private static int FirstLbaOf(byte[] image, string path)
    {
        using var reader = IsoImageReader.FromStream(new MemoryStream(image));
        return reader.FindFile(path)!.StartLba;
    }

    private static async Task<DataBurnResult> BurnAndVerify(MmcDataEngine engine, byte[] image, bool strict = false)
    {
        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);
        return await new DataBurnService(engine).BurnStreamAsync(recorder, new MemoryStream(image),
            new DataBurnOptions { VerifyAfterBurn = true, StrictVerify = strict });
    }

    // ---- Fast (default) and strict verify on an image larger than the drive's memory ----

    /// <summary>The drive reports a 1 MiB buffer, so 1,024 sectors at each end of the disc are read with FUA.</summary>
    private const int Guard = 1024;

    private static VirtualMmcDrive SmallBufferDrive() => new() { BufferCapacityBytes = 1024 * 1024 };

    /// <summary>About 6 MB: three times the two guarded stretches, so most of it lies between them.</summary>
    private static byte[] BuildLargeImage()
    {
        var rng = new Random(11);
        var root = CompilationDirectory.CreateRoot();
        foreach (string name in new[] { "1-start.bin", "2-middle.bin", "3-end.bin" })
        {
            byte[] data = new byte[2_000_000];
            rng.NextBytes(data);
            root.AddFile(name, data);
        }
        return IsoUdfImageBuilder.BuildToMemoryStream(root, new IsoBuildOptions()).ToArray();
    }

    /// <summary>What a drive really holds after a burn: the last stretch it was sent, no more than its buffer.</summary>
    private static Func<int, bool> LastWritten(byte[] image, int sectors) => lba => lba >= image.Length / 2048 - sectors;

    /// <summary>The one pass over the whole disc (the file system is read first, in small pieces).</summary>
    private static List<(int Lba, int Count, bool Fua)> FullPass(VirtualMmcDrive drive)
    {
        int start = drive.Read10Log.FindLastIndex(r => r.Lba == 0);
        return drive.Read10Log.Skip(start).ToList();
    }

    [Fact]
    public async Task FastVerify_ForcesDiscReadsAtBothEnds_AndReadsTheMiddleNormally()
    {
        byte[] image = BuildLargeImage();
        int sectors = image.Length / 2048;
        Assert.True(sectors > 2 * Guard + 500);
        var drive = SmallBufferDrive();

        var result = await BurnAndVerify(new MmcDataEngine(drive) { KeepInDriveMemory = LastWritten(image, 512) }, image);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.Verified);
        var pass = FullPass(drive);
        Assert.Equal(sectors, pass.Sum(r => r.Count));
        Assert.All(pass.Where(r => r.Lba < Guard), r => Assert.True(r.Fua, $"start, LBA {r.Lba}"));
        Assert.All(pass.Where(r => r.Lba + r.Count > sectors - Guard), r => Assert.True(r.Fua, $"end, LBA {r.Lba}"));
        var middle = pass.Where(r => r.Lba >= Guard && r.Lba + r.Count <= sectors - Guard).ToList();
        Assert.NotEmpty(middle);
        Assert.All(middle, r => Assert.False(r.Fua, $"middle, LBA {r.Lba}"));
        Assert.All(pass.Take(pass.Count - 1), r => Assert.Equal(32, r.Count)); // 64 KiB per command
    }

    [Fact]
    public async Task FastVerify_DamageInTheLastSectorsWritten_HiddenByDriveMemory_IsCaught()
    {
        byte[] image = BuildLargeImage();
        int bad = FirstLbaOf(image, "/3-end.bin") + 900; // inside the last megabyte written
        Assert.True(bad >= image.Length / 2048 - 512);
        var engine = new MmcDataEngine(SmallBufferDrive()) { DamageLba = bad, KeepInDriveMemory = LastWritten(image, 512) };

        var result = await BurnAndVerify(engine, image);

        Assert.False(result.Success);
        Assert.Contains("/3-end.bin", result.ErrorMessage);
    }

    [Fact]
    public async Task FastVerify_DamageInTheMiddle_IsCaughtByTheNormalRead()
    {
        byte[] image = BuildLargeImage();
        int bad = FirstLbaOf(image, "/2-middle.bin") + 300;
        Assert.InRange(bad, Guard, image.Length / 2048 - Guard - 1);
        var engine = new MmcDataEngine(SmallBufferDrive()) { DamageLba = bad, KeepInDriveMemory = LastWritten(image, 512) };

        var result = await BurnAndVerify(engine, image);

        Assert.False(result.Success);
        Assert.Contains("/2-middle.bin", result.ErrorMessage);
    }

    [Fact]
    public async Task StrictVerify_ForcesEveryRead_AndCatchesWhatOnlyAnOversizedDriveMemoryCouldHide()
    {
        // A drive that still holds a sector from the middle of a 6 MB burn would need far more memory than the
        // 1 MiB it reports. Fast verify relies on that; strict verify does not.
        byte[] image = BuildLargeImage();
        int bad = FirstLbaOf(image, "/2-middle.bin") + 300;

        var fastDrive = SmallBufferDrive();
        var fast = await BurnAndVerify(new MmcDataEngine(fastDrive) { DamageLba = bad, KeepInDriveMemory = lba => lba == bad }, image);
        Assert.True(fast.Success, "fast verify reads the middle normally, so this impossible-in-practice case gets past it");

        var strictDrive = SmallBufferDrive();
        var strict = await BurnAndVerify(new MmcDataEngine(strictDrive) { DamageLba = bad, KeepInDriveMemory = lba => lba == bad }, image, strict: true);
        Assert.False(strict.Success);
        Assert.Contains("/2-middle.bin", strict.ErrorMessage);
        Assert.All(strictDrive.Read10Log, r => Assert.True(r.Fua, $"READ (10) of LBA {r.Lba} had no FUA"));
    }

    [Fact]
    public async Task DriveThatDoesNotReportItsBuffer_GetsAWiderGuard()
    {
        byte[] image = BuildLargeImage(); // smaller than two 8 MiB guards: everything is forced
        var drive = new VirtualMmcDrive { BufferCapacityBytes = 0 };

        var result = await BurnAndVerify(new MmcDataEngine(drive), image);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.All(drive.Read10Log, r => Assert.True(r.Fua));
    }

    [Fact]
    public async Task AdapterThatPassesLessThan64KiB_IsReadInSmallerPieces()
    {
        byte[] image = BuildImage();
        var drive = new VirtualMmcDrive { MaxTransferBytesOverride = 40_000 };

        var result = await BurnAndVerify(new MmcDataEngine(drive), image);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.Verified);
        Assert.Contains(drive.Read10Log, r => r.Count == 16);
    }

    [Fact]
    public void Read10Cdb_HasFuaOnlyWhenAsked()
    {
        Assert.Equal(0x08, MmcCommandBuilder.BuildRead10Cdb(100, 16, forceUnitAccess: true)[1]);
        Assert.Equal(0x00, MmcCommandBuilder.BuildRead10Cdb(100, 16)[1]);
        Assert.Equal(new byte[] { 0x28, 0x08, 0, 0, 0x01, 0x02, 0, 0, 0x10, 0 }, MmcCommandBuilder.BuildRead10Cdb(0x0102, 16, true));
    }

    [Fact]
    public async Task DriveMemoryHidesADamagedSector_PlainReadIsFooled_FuaReadIsNot()
    {
        byte[] image = BuildImage();
        int bad = FirstLbaOf(image, "/Docs/report.pdf") + 3;
        var drive = new VirtualMmcDrive();
        await new MmcDataEngine(drive) { DamageLba = bad }.BurnDataStreamAsync(null!, new MemoryStream(image), new DataBurnOptions(), null, default);

        async Task<byte[]> ReadSector(bool fua)
        {
            byte[] copy = Array.Empty<byte>();
            await PhysicalSectorReader.ReadDataSectorsAsync(drive, bad, 1, fua ? DriveCacheBypass.EveryRead : DriveCacheBypass.None, (_, d) => { copy = d.ToArray(); return ValueTask.CompletedTask; }, null, default);
            return copy;
        }

        Assert.Equal(image.AsSpan(bad * 2048, 2048).ToArray(), await ReadSector(fua: false)); // the trap: looks fine
        Assert.NotEqual(image.AsSpan(bad * 2048, 2048).ToArray(), await ReadSector(fua: true)); // the disc as it is
    }

    [Fact]
    public async Task HealthyBurn_EveryVerifyReadHasFua_AndEveryFileMatchesTheDiscsHashLists()
    {
        var drive = new VirtualMmcDrive();
        var result = await BurnAndVerify(new MmcDataEngine(drive), BuildImage());

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.Verified);
        Assert.NotEmpty(drive.Read10Log);
        Assert.All(drive.Read10Log, r => Assert.True(r.Fua, $"READ (10) of LBA {r.Lba} had no FUA"));
        Assert.Contains("All 4 file(s) match the disc's SHA256SUMS and BLAKE3SUMS", result.VerificationNote);
    }

    [Fact]
    public async Task DamagedSectorHiddenByDriveMemory_IsCaught_AndNamesTheFile()
    {
        byte[] image = BuildImage();
        var drive = new VirtualMmcDrive();
        var engine = new MmcDataEngine(drive) { DamageLba = FirstLbaOf(image, "/Docs/report.pdf") + 10 };

        var result = await BurnAndVerify(engine, image);

        Assert.False(result.Success);
        Assert.Contains("/Docs/report.pdf", result.ErrorMessage);
        Assert.DoesNotContain("/a.bin", result.ErrorMessage);
    }

    [Fact]
    public async Task DriveRefusesFua_CacheIsFlushedFirst_DamageIsStillCaught_AndTheUserIsTold()
    {
        byte[] image = BuildImage();
        int bad = FirstLbaOf(image, "/a.bin") + 1;
        var drive = new VirtualMmcDrive { RejectsFua = true, ReadCacheCapacitySectors = 8 };
        var engine = new MmcDataEngine(drive) { DamageLba = bad, KeepInDriveMemory = lba => lba == bad };

        var result = await BurnAndVerify(engine, image);

        Assert.False(result.Success);
        Assert.Contains("/a.bin", result.ErrorMessage);
        Assert.True(drive.Read10Log[0].Fua);                     // FUA was tried first...
        Assert.All(drive.Read10Log.Skip(1), r => Assert.False(r.Fua)); // ...then dropped for the rest
    }

    [Fact]
    public async Task DriveRefusesFua_HealthyDisc_PassesWithANote()
    {
        var drive = new VirtualMmcDrive { RejectsFua = true };
        var result = await BurnAndVerify(new MmcDataEngine(drive), BuildImage());

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Contains("would not read past its own memory", result.VerificationNote);
    }

    [Fact]
    public async Task UnreadableSector_IsReportedWithTheFileThatOwnsIt()
    {
        byte[] image = BuildImage();
        var drive = new VirtualMmcDrive { ReadErrorAtLba = FirstLbaOf(image, "/Docs/notes.txt") };

        var result = await BurnAndVerify(new MmcDataEngine(drive), image);

        Assert.False(result.Success);
        Assert.Contains("/Docs/notes.txt", result.ErrorMessage);
    }

    [Fact]
    public async Task ImageChangedAfterItsHashListWasMade_SectorsMatchButTheHashListCheckFails()
    {
        byte[] image = BuildImage();
        image[FirstLbaOf(image, "/a.bin") * 2048 + 5] ^= 0xFF; // the disc will equal this image exactly

        var result = await BurnAndVerify(new MmcDataEngine(new VirtualMmcDrive()), image);

        Assert.False(result.Success);
        Assert.Contains("do not match its SHA256SUMS and BLAKE3SUMS", result.ErrorMessage);
        Assert.Contains("/a.bin", result.ErrorMessage);
        Assert.DoesNotContain("/Docs/report.pdf", result.ErrorMessage);
    }

    [Fact]
    public async Task DiscWithoutHashLists_StillVerifiesSectorBySector()
    {
        var result = await BurnAndVerify(new MmcDataEngine(new VirtualMmcDrive()), BuildImage(manifests: false));
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Null(result.VerificationNote);
    }

    [Fact]
    public async Task ManifestCheck_FromRawSectorsAlone_NoDriveLetter()
    {
        // The disc is reached only through SCSI commands: parse it and check every listed file.
        byte[] image = BuildImage();
        var drive = new VirtualMmcDrive { DataDiscImage = image };
        int sectors = image.Length / 2048;
        string? Read(int lba, int count, byte[] buffer)
        {
            int n = 0;
            var r = PhysicalSectorReader.ReadDataSectorsAsync(drive, lba, count, DriveCacheBypass.EveryRead,
                (_, d) => { d.Span.CopyTo(buffer.AsSpan(n++ * 2048)); return ValueTask.CompletedTask; }, null, default).GetAwaiter().GetResult();
            return r.Success ? null : r.ErrorMessage;
        }

        using var reader = IsoImageReader.FromStream(new SectorReadStream(Read, sectors));
        var check = DiscManifestCheck.Prepare(reader)!;
        await PhysicalSectorReader.ReadDataSectorsAsync(drive, 0, sectors, DriveCacheBypass.EveryRead,
            (lba, d) => { check.AddSector(lba, d.Span); return ValueTask.CompletedTask; }, null, default);
        var result = check.Finish();

        Assert.True(result.Passed);
        Assert.Equal(4, result.FilesChecked);
        Assert.Equal(new[] { "SHA256SUMS", "BLAKE3SUMS" }, result.ManifestNames);
    }
}
