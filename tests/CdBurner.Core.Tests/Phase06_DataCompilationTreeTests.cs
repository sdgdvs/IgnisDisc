using System.Text.RegularExpressions;
using CdBurner.Core.Compilation;
using CdBurner.Core.Primitives;
using Xunit;

namespace CdBurner.Core.Tests;

public class Phase06_DataCompilationTreeTests
{
    #region VirtualPath & Tree Hierarchy Tests

    [Fact]
    public void CompilationDirectory_CreateRoot_HasRootVirtualPath()
    {
        var root = CompilationDirectory.CreateRoot();

        Assert.Equal("/", root.VirtualPath);
        Assert.Null(root.Parent);
        Assert.Empty(root.Children);
    }

    [Fact]
    public void CompilationTree_NestedDirectoriesAndFiles_BuildsAccurateVirtualPaths()
    {
        var root = CompilationDirectory.CreateRoot();

        var docs = root.AddDirectory("Docs");
        var sub = docs.AddDirectory("Sub");
        var file = sub.AddFile("Readme.txt", "/local/readme.txt", 1024);
        var rootFile = root.AddFile("README.TXT", "/local/README.TXT", 2048);

        Assert.NotNull(file);
        Assert.NotNull(rootFile);
        Assert.Equal("/Docs", docs.VirtualPath);
        Assert.Equal("/Docs/Sub", sub.VirtualPath);
        Assert.Equal("/Docs/Sub/Readme.txt", file.VirtualPath);
        Assert.Equal("/README.TXT", rootFile.VirtualPath);
    }

    [Fact]
    public void CompilationTree_AddDirectory_MultiSegmentPath_CreatesIntermediateFolders()
    {
        var root = CompilationDirectory.CreateRoot();

        var deepDir = root.AddDirectory("A/B/C");

        Assert.Equal("/A/B/C", deepDir.VirtualPath);
        Assert.Single(root.Children);
        Assert.NotNull(root.FindByVirtualPath("/A/B/C"));
    }

    [Fact]
    public void CompilationTree_FindByVirtualPath_FindsNodes_CaseInsensitiveAndNormalized()
    {
        var root = CompilationDirectory.CreateRoot();
        var docs = root.AddDirectory("Docs");
        var sub = docs.AddDirectory("Sub");
        var file = sub.AddFile("Report.pdf", "/local/report.pdf", 4096);

        var foundFromRoot1 = root.FindByVirtualPath("/Docs/Sub/Report.pdf");
        var foundFromRoot2 = root.FindByVirtualPath("docs/sub/report.pdf");
        var foundFromRoot3 = root.FindByVirtualPath(@"\Docs\Sub\Report.pdf");
        var foundDir = root.FindByVirtualPath("/docs/sub");
        var foundFromSub = sub.FindByVirtualPath("Report.pdf");
        var foundAbsoluteFromSub = sub.FindByVirtualPath("/Docs/Sub/Report.pdf");
        var nonExistent = root.FindByVirtualPath("/Docs/Missing.txt");

        Assert.Same(file, foundFromRoot1);
        Assert.Same(file, foundFromRoot2);
        Assert.Same(file, foundFromRoot3);
        Assert.Same(sub, foundDir);
        Assert.Same(file, foundFromSub);
        Assert.Same(file, foundAbsoluteFromSub);
        Assert.Null(nonExistent);
    }

    [Fact]
    public void CompilationTree_RenameChild_DynamicallyUpdatesDescendantVirtualPaths()
    {
        var root = CompilationDirectory.CreateRoot();
        var docs = root.AddDirectory("Docs");
        var file = docs.AddFile("Readme.txt", "/local/readme.txt", 100);

        Assert.Equal("/Docs/Readme.txt", file!.VirtualPath);

        root.RenameChild(docs, "Documentation");

        Assert.Equal("/Documentation", docs.VirtualPath);
        Assert.Equal("/Documentation/Readme.txt", file.VirtualPath);

        docs.RenameChild(file, "Manual.txt");

        Assert.Equal("/Documentation/Manual.txt", file.VirtualPath);
    }

    [Fact]
    public void CompilationTree_RenameChild_ConflictingName_ThrowsInvalidOperationException()
    {
        var root = CompilationDirectory.CreateRoot();
        root.AddFile("FileA.txt", "/local/a.txt", 100);
        var fileB = root.AddFile("FileB.txt", "/local/b.txt", 100);

        Assert.Throws<InvalidOperationException>(() => root.RenameChild(fileB!, "FileA.txt"));
    }

    [Fact]
    public void CompilationTree_RemoveChild_DetachesParentAndRemovesFromTree()
    {
        var root = CompilationDirectory.CreateRoot();
        var file = root.AddFile("Test.dat", "/local/test.dat", 500);

        Assert.Single(root.Children);

        bool removed = root.RemoveChild(file!);

        Assert.True(removed);
        Assert.Empty(root.Children);
        Assert.Null(file!.Parent);
        Assert.Null(root.FindByVirtualPath("/Test.dat"));
    }

    [Fact]
    public void CompilationTree_GetAllFilesRecursive_CollectsAllFilesAcrossDepths()
    {
        var root = CompilationDirectory.CreateRoot();
        root.AddFile("RootFile.txt", "/root.txt", 100);
        var d1 = root.AddDirectory("D1");
        d1.AddFile("F1.txt", "/d1/f1.txt", 200);
        var d2 = d1.AddDirectory("D2");
        d2.AddFile("F2.txt", "/d1/d2/f2.txt", 300);

        var allFiles = root.GetAllFilesRecursive();

        Assert.Equal(3, allFiles.Count);
        Assert.Contains(allFiles, f => f.Name == "RootFile.txt");
        Assert.Contains(allFiles, f => f.Name == "F1.txt");
        Assert.Contains(allFiles, f => f.Name == "F2.txt");
    }

    #endregion

    #region DuplicateFilePolicy Tests

    [Fact]
    public void DuplicateFilePolicy_Overwrite_ReplacesExistingFile()
    {
        var dir = CompilationDirectory.CreateRoot();
        var f1 = dir.AddFile("report.pdf", "/local/old.pdf", 1000);
        var f2 = dir.AddFile("report.pdf", "/local/new.pdf", 2500, DuplicateFilePolicy.Overwrite);

        Assert.NotNull(f2);
        Assert.Single(dir.Children);
        Assert.Same(f2, dir.Children[0]);
        Assert.Equal(2500, ((CompilationFile)dir.Children[0]).SizeBytes);
        Assert.Null(f1!.Parent);
    }

    [Fact]
    public void DuplicateFilePolicy_RenameWithNumberSuffix_GeneratesNumberedSuffix()
    {
        var dir = CompilationDirectory.CreateRoot();
        var f1 = dir.AddFile("report.pdf", "/local/report.pdf", 100);
        var f2 = dir.AddFile("report.pdf", "/local/report.pdf", 200, DuplicateFilePolicy.RenameWithNumberSuffix);
        var f3 = dir.AddFile("report.pdf", "/local/report.pdf", 300, DuplicateFilePolicy.RenameWithNumberSuffix);

        Assert.NotNull(f1);
        Assert.NotNull(f2);
        Assert.NotNull(f3);
        Assert.Equal(3, dir.Children.Count);
        Assert.Equal("report.pdf", f1.Name);
        Assert.Equal("report (2).pdf", f2.Name);
        Assert.Equal("report (3).pdf", f3.Name);
        Assert.Equal("/report.pdf", f1.VirtualPath);
        Assert.Equal("/report (2).pdf", f2.VirtualPath);
        Assert.Equal("/report (3).pdf", f3.VirtualPath);
    }

    [Fact]
    public void DuplicateFilePolicy_RenameWithNumberSuffix_FileWithoutExtension_AppendsNumber()
    {
        var dir = CompilationDirectory.CreateRoot();
        var f1 = dir.AddFile("notes", "/local/notes", 100);
        var f2 = dir.AddFile("notes", "/local/notes", 200, DuplicateFilePolicy.RenameWithNumberSuffix);

        Assert.NotNull(f1);
        Assert.NotNull(f2);
        Assert.Equal("notes", f1.Name);
        Assert.Equal("notes (2)", f2.Name);
    }

    [Fact]
    public void DuplicateFilePolicy_Skip_LeavesExistingFileAndReturnsNull()
    {
        var dir = CompilationDirectory.CreateRoot();
        var f1 = dir.AddFile("report.pdf", "/local/report.pdf", 1000);
        var f2 = dir.AddFile("report.pdf", "/local/report.pdf", 5000, DuplicateFilePolicy.Skip);

        Assert.NotNull(f1);
        Assert.Null(f2);
        Assert.Single(dir.Children);
        Assert.Same(f1, dir.Children[0]);
        Assert.Equal(1000, ((CompilationFile)dir.Children[0]).SizeBytes);
    }

    [Fact]
    public void DuplicateFilePolicy_ThrowError_ThrowsInvalidOperationException()
    {
        var dir = CompilationDirectory.CreateRoot();
        dir.AddFile("report.pdf", "/local/report.pdf", 1000);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            dir.AddFile("report.pdf", "/local/report.pdf", 2000, DuplicateFilePolicy.ThrowError));

        Assert.Contains("already exists", ex.Message);
        Assert.Single(dir.Children);
    }

    #endregion

    #region IsoNameSanitizer Tests

    [Fact]
    public void IsoNameSanitizer_TargetSample_ConvertsToValidIso9660Level1()
    {
        const string input = "My Long File-Name.text.txt";

        string level1 = IsoNameSanitizer.ToIso9660Level1Name(input);

        // Required by prompt: 8.3 uppercase [A-Z0-9_] + ;1 version suffix
        Assert.Equal("MY_LONG_.TXT;1", level1);
        Assert.Matches(@"^[A-Z0-9_]{1,8}(\.[A-Z0-9_]{1,3})?;1$", level1);
    }

    [Fact]
    public void IsoNameSanitizer_TargetSample_ConvertsWithCollisionIndex()
    {
        const string input = "My Long File-Name.text.txt";

        string level1Col2 = IsoNameSanitizer.ToIso9660Level1Name(input, collisionIndex: 2);

        Assert.Equal("MY_LON_2.TXT;1", level1Col2);
        Assert.Matches(@"^[A-Z0-9_]{1,8}(\.[A-Z0-9_]{1,3})?;1$", level1Col2);
    }

    [Fact]
    public void IsoNameSanitizer_TargetSample_ConvertsToValidIso9660Level2()
    {
        const string input = "My Long File-Name.text.txt";

        string level2 = IsoNameSanitizer.ToIso9660Level2Name(input);

        // Required by prompt: up to 31 chars uppercase [A-Z0-9_.] + ;1
        Assert.Equal("MY_LONG_FILE_NAME_TEXT.TXT;1", level2);
        Assert.EndsWith(";1", level2);
        string nameWithoutVersion = level2[..^2];
        Assert.True(nameWithoutVersion.Length <= 31);
        Assert.Matches(@"^[A-Z0-9_.]+$", nameWithoutVersion);
    }

    [Fact]
    public void IsoNameSanitizer_TargetSample_ConvertsToValidJoliet()
    {
        const string input = "My Long File-Name.text.txt";

        string joliet = IsoNameSanitizer.ToJolietName(input);

        // Required by prompt: preserves extension, replaces illegal Joliet chars, up to 64 chars
        Assert.Equal("My Long File-Name.text.txt", joliet);
        Assert.True(joliet.Length <= 64);
    }

    [Fact]
    public void IsoNameSanitizer_Joliet_ReplacesIllegalCharacters()
    {
        const string input = @"Folder\My:File*Name?\""With<Illegal>Chars|And;Semi.dat";

        string joliet = IsoNameSanitizer.ToJolietName(input);

        Assert.DoesNotContain('\\', joliet);
        Assert.DoesNotContain('/', joliet);
        Assert.DoesNotContain(':', joliet);
        Assert.DoesNotContain('*', joliet);
        Assert.DoesNotContain('?', joliet);
        Assert.DoesNotContain('"', joliet);
        Assert.DoesNotContain('<', joliet);
        Assert.DoesNotContain('>', joliet);
        Assert.DoesNotContain('|', joliet);
        Assert.DoesNotContain(';', joliet);
        Assert.EndsWith(".dat", joliet);
    }

    [Fact]
    public void IsoNameSanitizer_Joliet_TruncatesTo64Chars_PreservingExtension()
    {
        // 75 character file name ending with .backup.tar.gz
        string input = new string('A', 50) + "SuperLongFileName.backup.tar.gz";

        string joliet = IsoNameSanitizer.ToJolietName(input);

        Assert.Equal(64, joliet.Length);
        Assert.EndsWith(".gz", joliet);
    }

    [Fact]
    public void IsoNameSanitizer_DirectoryName_ProducesValidLevel1DirName()
    {
        string dirName = IsoNameSanitizer.ToIso9660DirectoryName("My Sub Directory");

        Assert.Equal("MY_SUB_D", dirName);
        Assert.True(dirName.Length <= 8);
        Assert.Matches(@"^[A-Z0-9_]+$", dirName);
    }

    #endregion

    #region CompilationCapacityCalculator Tests

    [Fact]
    public void CompilationCapacityCalculator_TenOneByteFiles_ConsumeTenDataSectors()
    {
        // Required by prompt: "ten 1-byte files consume 10 data sectors, not 1"
        var files = Enumerable.Range(1, 10)
            .Select(i => new CompilationFile($"file{i}.txt", sizeBytes: 1))
            .ToList();

        int totalDataSectors = CompilationCapacityCalculator.CalculateTotalFileDataSectors(files);

        Assert.Equal(10, totalDataSectors);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2048, 1)]
    [InlineData(2049, 2)]
    [InlineData(4096, 2)]
    [InlineData(4097, 3)]
    public void CompilationCapacityCalculator_CalculateFileSectors_RoundsUpTo2048Bytes(long bytes, int expectedSectors)
    {
        int sectors = CompilationCapacityCalculator.CalculateFileSectors(bytes);
        Assert.Equal(expectedSectors, sectors);
    }

    [Fact]
    public void CompilationCapacityCalculator_Classifies_FitsComfortably_ForCdR()
    {
        // CD-R: 360,000 sectors. 100,000 sectors = 27.78% <= 95%
        var assessment = CompilationCapacityCalculator.Calculate(totalSectors: 100_000, DiscMediaType.CdR);

        Assert.Equal(CapacityStatus.FitsComfortably, assessment.Status);
        Assert.Equal(360_000, assessment.MediaMaxSectors);
        Assert.Equal(27.78, assessment.UsagePercent);
        Assert.False(assessment.IsOverburn);
        Assert.True(assessment.CanFitOnMedia);
        Assert.Equal(260_000, assessment.FreeSectors);
    }

    [Fact]
    public void CompilationCapacityCalculator_Classifies_NearCapacityWarning_ForCdR()
    {
        // CD-R: 360,000 sectors. 350,000 sectors = 97.22% (> 95% and <= 100%)
        var assessment = CompilationCapacityCalculator.Calculate(totalSectors: 350_000, DiscMediaType.CdR);

        Assert.Equal(CapacityStatus.NearCapacityWarning, assessment.Status);
        Assert.Equal(97.22, assessment.UsagePercent);
        Assert.False(assessment.IsOverburn);
        Assert.True(assessment.CanFitOnMedia);
    }

    [Fact]
    public void CompilationCapacityCalculator_Classifies_OverburnRequired_ForCdR()
    {
        // Required by prompt: "CD 360,000..405,000 sectors"
        var assessment = CompilationCapacityCalculator.Calculate(totalSectors: 380_000, DiscMediaType.CdR);

        Assert.Equal(CapacityStatus.OverburnRequired, assessment.Status);
        Assert.True(assessment.IsOverburn);
        Assert.True(assessment.CanFitOnMedia);
        Assert.Equal(0, assessment.FreeSectors);
    }

    [Fact]
    public void CompilationCapacityCalculator_Classifies_ExceedsMediaCapacity_ForCdR()
    {
        // CD-R overburn max is 405,000 sectors (90 min). 410,000 exceeds it.
        var assessment = CompilationCapacityCalculator.Calculate(totalSectors: 410_000, DiscMediaType.CdR);

        Assert.Equal(CapacityStatus.ExceedsMediaCapacity, assessment.Status);
        Assert.False(assessment.IsOverburn);
        Assert.False(assessment.CanFitOnMedia);
    }

    [Fact]
    public void CompilationCapacityCalculator_Classifies_ExceedsMediaCapacity_ForDvdMinusR()
    {
        // DVD-R has no overburn; exceeds nominal 2,298,496 sectors
        var assessment = CompilationCapacityCalculator.Calculate(totalSectors: 2_300_000, DiscMediaType.DvdMinusR);

        Assert.Equal(CapacityStatus.ExceedsMediaCapacity, assessment.Status);
        Assert.False(assessment.CanFitOnMedia);
    }

    [Fact]
    public void CompilationCapacityCalculator_CalculatesTreeWithOverhead()
    {
        var root = CompilationDirectory.CreateRoot();
        root.AddFile("File1.bin", "/f1.bin", 2048);
        root.AddFile("File2.bin", "/f2.bin", 4096);
        var sub = root.AddDirectory("Sub");
        sub.AddFile("File3.bin", "/sub/f3.bin", 1024);

        var assessment = CompilationCapacityCalculator.Calculate(root, DiscMediaType.CdR);

        Assert.Equal(CapacityStatus.FitsComfortably, assessment.Status);
        Assert.Equal(360_000, assessment.MediaMaxSectors);
        // File 1: 1 sector; File 2: 2 sectors; File 3: 1 sector = 4 data sectors
        Assert.Equal(4, assessment.TotalFileDataSectors);
        Assert.True(assessment.FileSystemOverheadSectors > 0);
        Assert.Equal(assessment.TotalFileDataSectors + assessment.FileSystemOverheadSectors, assessment.TotalSectors);
        Assert.Equal((long)assessment.TotalSectors * OpticalConstants.DataSectorSize, assessment.TotalBytes);
    }

    #endregion

    #region DataCompilation Integration Tests

    [Fact]
    public void DataCompilation_AddFile_CreatesHierarchyAndCalculatesCapacity()
    {
        var compilation = new DataCompilation("BACKUP_2026", DiscMediaType.CdR);

        var f1 = compilation.AddFile("/Documents/Work/Report.docx", "/local/report.docx", 50_000);
        var f2 = compilation.AddFile("/Bin/Setup.exe", "/local/setup.exe", 1_000_000);

        Assert.NotNull(f1);
        Assert.NotNull(f2);
        Assert.Equal("/Documents/Work/Report.docx", f1.VirtualPath);
        Assert.Equal("/Bin/Setup.exe", f2.VirtualPath);

        var allFiles = compilation.GetAllFiles();
        Assert.Equal(2, allFiles.Count);

        var assessment = compilation.CalculateCapacity();
        Assert.Equal(CapacityStatus.FitsComfortably, assessment.Status);
        Assert.True(assessment.TotalSectors > 0);
    }

    [Fact]
    public void DataCompilation_InMemoryStream_CanBeReadViaOpenRead()
    {
        var compilation = new DataCompilation();
        byte[] payload = "Hello Optical Burning World"u8.ToArray();

        var file = compilation.AddFile("/Message.txt", payload);

        Assert.NotNull(file);
        Assert.Equal(payload.Length, file.SizeBytes);

        using var stream = file.OpenRead();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);

        Assert.Equal(payload, ms.ToArray());
    }

    #endregion
}
