using System.Text;
using CdBurner.Core.Compilation;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Primitives;
using Xunit;

namespace CdBurner.Core.Tests;

public class Phase16_DvdVideoMasteringTests
{
    // --- Helper Methods ---

    private static byte[] CreateVmgIfo(int size = OpticalConstants.DataSectorSize) =>
        DvdVideoValidator.CreateSyntheticVmgIfo(size);

    private static byte[] CreateVtsIfo(int size = OpticalConstants.DataSectorSize) =>
        DvdVideoValidator.CreateSyntheticVtsIfo(size);

    private static IDvdVideoFileEntry CreateFile(string name, byte[] data) =>
        new DvdVideoMemoryFileEntry(name, data);

    private static IDvdVideoFileEntry CreateDummyVob(string name, int sectorCount)
    {
        byte[] dummy = new byte[sectorCount * OpticalConstants.DataSectorSize];
        return new DvdVideoMemoryFileEntry(name, dummy);
    }

    private static CompilationDirectory CreateSyntheticDvdVideoTree(
        bool includeVmgMenuVob = true,
        bool includeTitleVobs = true,
        bool includeAudioTs = true)
    {
        var root = CompilationDirectory.CreateRoot();
        var videoTs = root.AddDirectory("VIDEO_TS");

        if (includeAudioTs)
        {
            root.AddDirectory("AUDIO_TS");
        }

        byte[] vmgBytes = CreateVmgIfo();
        videoTs.AddFile("VIDEO_TS.IFO", (byte[])vmgBytes.Clone());
        if (includeVmgMenuVob)
        {
            videoTs.AddFile("VIDEO_TS.VOB", new byte[2048]);
        }
        videoTs.AddFile("VIDEO_TS.BUP", (byte[])vmgBytes.Clone());

        byte[] vtsBytes = CreateVtsIfo();
        videoTs.AddFile("VTS_01_0.IFO", (byte[])vtsBytes.Clone());
        videoTs.AddFile("VTS_01_0.VOB", new byte[2048]);
        if (includeTitleVobs)
        {
            videoTs.AddFile("VTS_01_1.VOB", new byte[4096]);
        }
        videoTs.AddFile("VTS_01_0.BUP", (byte[])vtsBytes.Clone());

        return root;
    }

    // =========================================================================
    // 1. DvdVideoValidator Tests
    // =========================================================================

    [Fact]
    public void Validator_ValidSyntheticVideoTs_PassesValidation()
    {
        var root = CreateSyntheticDvdVideoTree(includeVmgMenuVob: true, includeTitleVobs: true, includeAudioTs: true);
        var videoTs = root.FindDirectoryByVirtualPath("/VIDEO_TS")!;

        var validator = new DvdVideoValidator();
        var result = validator.ValidateDirectory(videoTs, hasAudioTs: true);

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Single(result.TitleSetNumbers);
        Assert.Equal(1, result.TitleSetNumbers[0]);
        Assert.True(result.HasAudioTs);
        Assert.True(result.HasVmgMenuVob);
    }

    [Fact]
    public void Validator_MissingVmgBup_FailsValidation()
    {
        byte[] vmgBytes = CreateVmgIfo();
        byte[] vtsBytes = CreateVtsIfo();

        var files = new List<IDvdVideoFileEntry>
        {
            CreateFile("VIDEO_TS.IFO", vmgBytes),
            // Missing VIDEO_TS.BUP!
            CreateFile("VTS_01_0.IFO", vtsBytes),
            CreateFile("VTS_01_0.BUP", (byte[])vtsBytes.Clone())
        };

        var validator = new DvdVideoValidator();
        var result = validator.ValidateFiles(files);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("VIDEO_TS.BUP", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_MissingVtsBup_FailsValidation()
    {
        byte[] vmgBytes = CreateVmgIfo();
        byte[] vtsBytes = CreateVtsIfo();

        var files = new List<IDvdVideoFileEntry>
        {
            CreateFile("VIDEO_TS.IFO", vmgBytes),
            CreateFile("VIDEO_TS.BUP", (byte[])vmgBytes.Clone()),
            CreateFile("VTS_01_0.IFO", vtsBytes)
            // Missing VTS_01_0.BUP!
        };

        var validator = new DvdVideoValidator();
        var result = validator.ValidateFiles(files);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("VTS_01_0.BUP", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_MismatchedVmgBupContent_FailsValidation()
    {
        byte[] vmgBytes = CreateVmgIfo();
        byte[] corruptedBup = (byte[])vmgBytes.Clone();
        corruptedBup[100] = (byte)(corruptedBup[100] ^ 0xFF); // Mutate 1 byte

        byte[] vtsBytes = CreateVtsIfo();

        var files = new List<IDvdVideoFileEntry>
        {
            CreateFile("VIDEO_TS.IFO", vmgBytes),
            CreateFile("VIDEO_TS.BUP", corruptedBup),
            CreateFile("VTS_01_0.IFO", vtsBytes),
            CreateFile("VTS_01_0.BUP", (byte[])vtsBytes.Clone())
        };

        var validator = new DvdVideoValidator();
        var result = validator.ValidateFiles(files);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("not byte-identical", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_MismatchedVtsBupSize_FailsValidation()
    {
        byte[] vmgBytes = CreateVmgIfo();
        byte[] vtsBytes = CreateVtsIfo(2048);
        byte[] differentSizeBup = new byte[4096];
        Array.Copy(vtsBytes, differentSizeBup, 2048);

        var files = new List<IDvdVideoFileEntry>
        {
            CreateFile("VIDEO_TS.IFO", vmgBytes),
            CreateFile("VIDEO_TS.BUP", (byte[])vmgBytes.Clone()),
            CreateFile("VTS_01_0.IFO", vtsBytes),
            CreateFile("VTS_01_0.BUP", differentSizeBup)
        };

        var validator = new DvdVideoValidator();
        var result = validator.ValidateFiles(files);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("size", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_InvalidVmgMagicHeader_FailsValidation()
    {
        byte[] badVmg = new byte[2048];
        Encoding.ASCII.GetBytes("BADMAGIC1234").CopyTo(badVmg, 0); // Invalid signature

        byte[] vtsBytes = CreateVtsIfo();

        var files = new List<IDvdVideoFileEntry>
        {
            CreateFile("VIDEO_TS.IFO", badVmg),
            CreateFile("VIDEO_TS.BUP", (byte[])badVmg.Clone()),
            CreateFile("VTS_01_0.IFO", vtsBytes),
            CreateFile("VTS_01_0.BUP", (byte[])vtsBytes.Clone())
        };

        var validator = new DvdVideoValidator();
        var result = validator.ValidateFiles(files);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("invalid magic signature", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_InvalidVtsMagicHeader_FailsValidation()
    {
        byte[] vmgBytes = CreateVmgIfo();
        byte[] badVts = new byte[2048];
        Encoding.ASCII.GetBytes("INVALID-VTS!").CopyTo(badVts, 0);

        var files = new List<IDvdVideoFileEntry>
        {
            CreateFile("VIDEO_TS.IFO", vmgBytes),
            CreateFile("VIDEO_TS.BUP", (byte[])vmgBytes.Clone()),
            CreateFile("VTS_01_0.IFO", badVts),
            CreateFile("VTS_01_0.BUP", (byte[])badVts.Clone())
        };

        var validator = new DvdVideoValidator();
        var result = validator.ValidateFiles(files);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("invalid magic signature", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_LowercaseFileName_FailsValidation()
    {
        byte[] vmgBytes = CreateVmgIfo();
        byte[] vtsBytes = CreateVtsIfo();

        var files = new List<IDvdVideoFileEntry>
        {
            CreateFile("video_ts.ifo", vmgBytes), // Lowercase!
            CreateFile("VIDEO_TS.BUP", (byte[])vmgBytes.Clone()),
            CreateFile("VTS_01_0.IFO", vtsBytes),
            CreateFile("VTS_01_0.BUP", (byte[])vtsBytes.Clone())
        };

        var validator = new DvdVideoValidator();
        var result = validator.ValidateFiles(files);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("lowercase", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_MultiTitleSet_ValidatesSequentially()
    {
        byte[] vmgBytes = CreateVmgIfo();
        byte[] vts1Bytes = CreateVtsIfo();
        byte[] vts2Bytes = CreateVtsIfo();

        var files = new List<IDvdVideoFileEntry>
        {
            CreateFile("VIDEO_TS.IFO", vmgBytes),
            CreateFile("VIDEO_TS.BUP", (byte[])vmgBytes.Clone()),
            CreateFile("VTS_01_0.IFO", vts1Bytes),
            CreateFile("VTS_01_0.BUP", (byte[])vts1Bytes.Clone()),
            CreateFile("VTS_02_0.IFO", vts2Bytes),
            CreateFile("VTS_02_0.BUP", (byte[])vts2Bytes.Clone())
        };

        var validator = new DvdVideoValidator();
        var result = validator.ValidateFiles(files, hasAudioTs: true);

        Assert.True(result.IsValid);
        Assert.Equal(2, result.TitleSetNumbers.Count);
        Assert.Equal(1, result.TitleSetNumbers[0]);
        Assert.Equal(2, result.TitleSetNumbers[1]);
    }

    [Fact]
    public void Validator_DataCompilation_ValidatesSuccessfully()
    {
        var compilation = new DataCompilation("MY_DVD_VIDEO", DiscMediaType.DvdPlusR);
        var videoTs = compilation.Root.AddDirectory("VIDEO_TS");
        compilation.Root.AddDirectory("AUDIO_TS");

        byte[] vmgBytes = CreateVmgIfo();
        byte[] vtsBytes = CreateVtsIfo();

        videoTs.AddFile("VIDEO_TS.IFO", (byte[])vmgBytes.Clone());
        videoTs.AddFile("VIDEO_TS.BUP", (byte[])vmgBytes.Clone());
        videoTs.AddFile("VTS_01_0.IFO", (byte[])vtsBytes.Clone());
        videoTs.AddFile("VTS_01_0.BUP", (byte[])vtsBytes.Clone());

        var validator = new DvdVideoValidator();
        var result = validator.ValidateCompilation(compilation);

        Assert.True(result.IsValid);
        Assert.True(result.HasAudioTs);
    }

    // =========================================================================
    // 2. DvdVideoLayoutPlanner Tests
    // =========================================================================

    [Fact]
    public void LayoutPlanner_PlacesFilesInCanonicalDvdVideoOrder()
    {
        // Provide files in completely scrambled order
        var files = new List<IDvdVideoFileEntry>
        {
            CreateDummyVob("VTS_02_1.VOB", 10),
            CreateFile("VIDEO_TS.BUP", CreateVmgIfo()),
            CreateFile("VTS_01_0.IFO", CreateVtsIfo()),
            CreateFile("VIDEO_TS.IFO", CreateVmgIfo()),
            CreateFile("VTS_01_0.BUP", CreateVtsIfo()),
            CreateFile("VTS_02_0.BUP", CreateVtsIfo()),
            CreateFile("VTS_02_0.IFO", CreateVtsIfo()),
            CreateDummyVob("VIDEO_TS.VOB", 20),
            CreateDummyVob("VTS_01_1.VOB", 30),
            CreateDummyVob("VTS_01_2.VOB", 40),
            CreateDummyVob("VTS_01_0.VOB", 5),
        };

        var planner = new DvdVideoLayoutPlanner();
        var layout = planner.Plan(files, startLba: 0);

        // Extract ordered file names (excluding virtual padding)
        var orderedNames = layout.FilePlacements.Select(p => p.FileName).ToList();

        var expectedOrder = new List<string>
        {
            "VIDEO_TS.IFO",
            "VIDEO_TS.VOB",
            "VIDEO_TS.BUP",
            "VTS_01_0.IFO",
            "VTS_01_0.VOB",
            "VTS_01_1.VOB",
            "VTS_01_2.VOB",
            "VTS_01_0.BUP",
            "VTS_02_0.IFO",
            "VTS_02_1.VOB",
            "VTS_02_0.BUP"
        };

        Assert.Equal(expectedOrder, orderedNames);
    }

    [Fact]
    public void LayoutPlanner_Enforces16SectorSeparation_WhenNoVobExistsBetweenIfoAndBup()
    {
        // Minimal DVD-Video: IFO and BUP with NO intervening .VOB
        byte[] vmgBytes = CreateVmgIfo(2048); // 1 sector
        byte[] vtsBytes = CreateVtsIfo(2048); // 1 sector

        var files = new List<IDvdVideoFileEntry>
        {
            CreateFile("VIDEO_TS.IFO", vmgBytes),
            CreateFile("VIDEO_TS.BUP", (byte[])vmgBytes.Clone()),
            CreateFile("VTS_01_0.IFO", vtsBytes),
            CreateFile("VTS_01_0.BUP", (byte[])vtsBytes.Clone())
        };

        var planner = new DvdVideoLayoutPlanner();
        var layout = planner.Plan(files, startLba: 0);

        var vmgIfo = layout.GetPlacement("VIDEO_TS.IFO")!;
        var vmgBup = layout.GetPlacement("VIDEO_TS.BUP")!;
        var vtsIfo = layout.GetPlacement("VTS_01_0.IFO")!;
        var vtsBup = layout.GetPlacement("VTS_01_0.BUP")!;

        // 1. Verify exact formula: LBA(.BUP) - EndLBA(.IFO) >= 16
        int vmgDiff = vmgBup.StartLba.Value - vmgIfo.EndLba.Value;
        int vtsDiff = vtsBup.StartLba.Value - vtsIfo.EndLba.Value;

        Assert.True(vmgDiff >= 16, $"VIDEO_TS: LBA(.BUP) - EndLBA(.IFO) is {vmgDiff}, expected >= 16");
        Assert.True(vtsDiff >= 16, $"VTS_01: LBA(.BUP) - EndLBA(.IFO) is {vtsDiff}, expected >= 16");

        // 2. Verify VerifyIfoBupSeparation helper succeeds
        Assert.True(layout.VerifyIfoBupSeparation(out var failureReason), failureReason);

        // 3. Verify padding placements were created
        Assert.Equal(2, layout.PaddingPlacements.Count);
        Assert.All(layout.PaddingPlacements, p => Assert.True(p.SectorCount >= 16));
    }

    [Fact]
    public void LayoutPlanner_NoPaddingNeeded_WhenInterveningVobIsAtLeast16Sectors()
    {
        byte[] vmgBytes = CreateVmgIfo(2048); // 1 sector
        byte[] vtsBytes = CreateVtsIfo(2048); // 1 sector

        var files = new List<IDvdVideoFileEntry>
        {
            CreateFile("VIDEO_TS.IFO", vmgBytes),
            CreateDummyVob("VIDEO_TS.VOB", 20), // 20 sectors >= 16
            CreateFile("VIDEO_TS.BUP", (byte[])vmgBytes.Clone()),
            CreateFile("VTS_01_0.IFO", vtsBytes),
            CreateDummyVob("VTS_01_1.VOB", 100), // 100 sectors >= 16
            CreateFile("VTS_01_0.BUP", (byte[])vtsBytes.Clone())
        };

        var planner = new DvdVideoLayoutPlanner();
        var layout = planner.Plan(files, startLba: 0);

        // Since intervening VOBs >= 16 sectors, 0 padding sectors should be inserted
        Assert.Empty(layout.PaddingPlacements);
        Assert.Equal(0, layout.TotalPaddingSectors);

        int vmgDiff = layout.GetInterveningSectors("VIDEO_TS.IFO", "VIDEO_TS.BUP");
        int vtsDiff = layout.GetInterveningSectors("VTS_01_0.IFO", "VTS_01_0.BUP");

        Assert.Equal(20, vmgDiff);
        Assert.Equal(100, vtsDiff);
        Assert.True(layout.VerifyIfoBupSeparation(out _));
    }

    [Fact]
    public void LayoutPlanner_ApplyToCompilation_AssignsImportedStartLba()
    {
        var root = CreateSyntheticDvdVideoTree(includeVmgMenuVob: false, includeTitleVobs: false);
        var videoTs = root.FindDirectoryByVirtualPath("/VIDEO_TS")!;

        var planner = new DvdVideoLayoutPlanner();
        var layout = planner.ApplyToCompilation(videoTs, startLba: 100);

        var ifoFile = videoTs.FindFileByVirtualPath("VIDEO_TS.IFO")!;
        var bupFile = videoTs.FindFileByVirtualPath("VIDEO_TS.BUP")!;

        Assert.NotNull(ifoFile.ImportedStartLba);
        Assert.NotNull(bupFile.ImportedStartLba);

        Assert.Equal(100, ifoFile.ImportedStartLba.Value);
        // VIDEO_TS.IFO is 1 sector (100..101), padding is 16 sectors (101..117), BUP starts at 117
        Assert.Equal(117, bupFile.ImportedStartLba.Value);
        Assert.True(bupFile.ImportedStartLba.Value - (ifoFile.ImportedStartLba.Value + ifoFile.RequiredSectors) >= 16);
    }

    // =========================================================================
    // 3. DvdLayerBreakCalculator Tests
    // =========================================================================

    [Fact]
    public void LayerBreak_Selects16AlignedCandidatesSatisfyingConstraints()
    {
        // DVD-9 compilation: 3,000,000 sectors (> 2,295,104 limit)
        // Min Layer 0 = ceil(3,000,000 / 2.0) = 1,500,000
        // Max Layer 0 = 2,086,912
        int totalSectors = 3_000_000;

        var candidateCellLbas = new List<int>
        {
            1_400_000, // Invalid: L0 < L1 (1,400,000 < 1,500,000)
            1_500_000, // Valid: 16-aligned (1,500,000 % 16 == 0), L0 >= L1, L0 <= 2,086,912
            1_600_000, // Valid: 16-aligned, L0 >= L1, L0 <= 2,086,912
            1_600_005, // Invalid: Not 16-aligned (1,600,005 % 16 == 5)
            2_086_912, // Valid: Exactly MaxLayer0Sectors, 16-aligned
            2_086_928  // Invalid: Exceeds MaxLayer0Sectors (2,086,928 > 2,086,912)
        };

        var candidates = DvdLayerBreakCalculator.CalculateCandidates(totalSectors, candidateCellLbas);

        Assert.Equal(3, candidates.Count);

        var breakLbas = candidates.Select(c => c.BreakLba).ToList();
        Assert.Contains(1_500_000, breakLbas);
        Assert.Contains(1_600_000, breakLbas);
        Assert.Contains(2_086_912, breakLbas);

        foreach (var c in candidates)
        {
            // Verify constraint 1: 16-sector ECC aligned
            Assert.Equal(0, c.BreakLba % 16);
            Assert.True(c.IsEccAligned);

            // Verify constraint 2: L0 >= L1
            Assert.True(c.Layer0Sectors >= c.Layer1Sectors);

            // Verify constraint 3: L0 <= 2,086,912
            Assert.True(c.Layer0Sectors <= DvdLayerBreakCalculator.MaxLayer0Sectors);

            Assert.True(c.IsValid);
        }
    }

    [Fact]
    public void LayerBreak_Dvd5Compilation_ReturnsNoCandidates()
    {
        // 2,000,000 sectors <= 2,295,104 single-layer limit
        int totalSectors = 2_000_000;

        Assert.False(DvdLayerBreakCalculator.RequiresLayerBreak(totalSectors));

        var candidates = DvdLayerBreakCalculator.CalculateCandidates(totalSectors, new[] { 1_000_000, 1_200_000 });
        Assert.Empty(candidates);

        var optimal = DvdLayerBreakCalculator.SelectOptimalBreak(totalSectors);
        Assert.Null(optimal);
    }

    [Fact]
    public void LayerBreak_SelectOptimalBreak_SelectsSmallestDifference()
    {
        int totalSectors = 3_200_000; // min L0 = 1,600,000
        var candidates = new[] { 1_600_000, 1_700_000, 1_800_000 };

        var optimal = DvdLayerBreakCalculator.SelectOptimalBreak(totalSectors, candidates);

        Assert.NotNull(optimal);
        Assert.Equal(1_600_000, optimal.BreakLba);
        Assert.Equal(0, optimal.SectorDifference); // Perfect 50/50 split
    }

    [Fact]
    public void LayerBreak_SelectOptimalBreak_NoCandidates_GeneratesDefault16AlignedBreak()
    {
        int totalSectors = 3_000_001;
        // min L0 = ceil(3,000,001 / 2.0) = 1,500,001
        // 1,500,001 / 16 = 93750.0625 -> ceil * 16 = 1,500,016
        var optimal = DvdLayerBreakCalculator.SelectOptimalBreak(totalSectors);

        Assert.NotNull(optimal);
        Assert.Equal(1_500_016, optimal.BreakLba);
        Assert.Equal(0, optimal.BreakLba % 16);
        Assert.True(optimal.Layer0Sectors >= optimal.Layer1Sectors);
        Assert.True(optimal.Layer0Sectors <= DvdLayerBreakCalculator.MaxLayer0Sectors);
    }

    [Fact]
    public void LayerBreak_ExceedingDvd9Capacity_ThrowsArgumentOutOfRangeException()
    {
        int oversized = DvdLayerBreakCalculator.MaxDvd9Sectors + 1000;
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DvdLayerBreakCalculator.CalculateCandidates(oversized, new[] { 2_000_000 }));
    }

    // =========================================================================
    // 4. FfmpegDvdTranscodeBuilder Tests
    // =========================================================================

    [Fact]
    public void FfmpegBuilder_Ntsc_GeneratesDeterministicArgumentsWith16x9()
    {
        string input = @"C:\Media\Movie.mp4";
        string output = @"C:\Media\VTS_01_1.VOB";

        var builder = FfmpegDvdTranscodeBuilder.ForNtsc(input, output, DvdAspectRatio.Widescreen16x9);
        var args = builder.BuildArgumentList().ToList();
        string cli = builder.BuildCommandLine();

        // Verify target ntsc-dvd and aspect 16:9
        Assert.Contains("-target", args);
        int targetIdx = args.IndexOf("-target");
        Assert.Equal("ntsc-dvd", args[targetIdx + 1]);

        Assert.Contains("-aspect", args);
        int aspectIdx = args.IndexOf("-aspect");
        Assert.Equal("16:9", args[aspectIdx + 1]);

        Assert.Contains("-target ntsc-dvd -aspect 16:9", cli);
    }

    [Fact]
    public void FfmpegBuilder_Pal_GeneratesDeterministicArgumentsWith16x9()
    {
        string input = @"C:\Media\Movie.mp4";
        string output = @"C:\Media\VTS_01_1.VOB";

        var builder = FfmpegDvdTranscodeBuilder.ForPal(input, output, DvdAspectRatio.Widescreen16x9);
        var args = builder.BuildArgumentList().ToList();
        string cli = builder.BuildCommandLine();

        Assert.Contains("-target", args);
        int targetIdx = args.IndexOf("-target");
        Assert.Equal("pal-dvd", args[targetIdx + 1]);

        Assert.Contains("-aspect", args);
        int aspectIdx = args.IndexOf("-aspect");
        Assert.Equal("16:9", args[aspectIdx + 1]);

        Assert.Contains("-target pal-dvd -aspect 16:9", cli);
    }

    [Fact]
    public void FfmpegBuilder_Ntsc_4x3_BuildsCorrectAspect()
    {
        string input = "video.avi";
        string output = "video.vob";

        var builder = FfmpegDvdTranscodeBuilder.ForNtsc(input, output, DvdAspectRatio.Standard4x3);
        var args = builder.BuildArgumentList().ToList();

        int aspectIdx = args.IndexOf("-aspect");
        Assert.Equal("4:3", args[aspectIdx + 1]);
    }

    [Fact]
    public void FfmpegBuilder_CustomBitratesAndAudio_EmitsDeterministicArguments()
    {
        var builder = new FfmpegDvdTranscodeBuilder()
            .WithInput("source.mkv")
            .WithOutput("out.vob")
            .WithStandard(DvdVideoStandard.Ntsc)
            .WithAspectRatio(DvdAspectRatio.Widescreen16x9)
            .WithVideoBitrate(6000, maxRateKbps: 8000, bufferSizeKb: 1835)
            .WithAudio("ac3", bitrateKbps: 448, channels: 6);

        var args = builder.BuildArgumentList().ToList();

        Assert.Contains("-b:v", args);
        Assert.Equal("6000k", args[args.IndexOf("-b:v") + 1]);

        Assert.Contains("-maxrate", args);
        Assert.Equal("8000k", args[args.IndexOf("-maxrate") + 1]);

        Assert.Contains("-bufsize", args);
        Assert.Equal("1835k", args[args.IndexOf("-bufsize") + 1]);

        Assert.Contains("-c:a", args);
        Assert.Equal("ac3", args[args.IndexOf("-c:a") + 1]);

        Assert.Contains("-b:a", args);
        Assert.Equal("448k", args[args.IndexOf("-b:a") + 1]);

        Assert.Contains("-ac", args);
        Assert.Equal("6", args[args.IndexOf("-ac") + 1]);
    }
}
