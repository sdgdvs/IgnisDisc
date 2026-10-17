using CdBurner.Core.Abstractions;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// Unit tests for Phase 09: Multi-Session Data Disc Import & Session Merging.
/// Verifies importing previous session trees, non-finalized appendable disc tracking,
/// LBA pointer reuse without data duplication, and multi-session merged volume reading.
/// </summary>
public class Phase09_MultiSessionTests
{
    [Fact]
    public async Task MultiSession_TwoSessions_ImportsSession1_AppendsSession2_PreservesPointers_AndReadsBothFiles()
    {
        // 1. Setup virtual CD-R medium and recorder
        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);

        var burnEngine = new VirtualBurnEngine(recorder);

        // 2. Prepare Session 1: /Session1/fileA.txt = 10 KB
        var root1 = CompilationDirectory.CreateRoot();
        var session1Dir = root1.AddDirectory("Session1");
        byte[] fileABytes = new byte[10 * 1024]; // 10 KB = 10,240 bytes
        Random.Shared.NextBytes(fileABytes);
        session1Dir.AddFile("fileA.txt", fileABytes);

        var session1BuildOptions = new IsoBuildOptions("SESSION1", IncludeUdf: false);
        using var session1Stream = IsoUdfImageBuilder.BuildToMemoryStream(root1, session1BuildOptions);

        // 3. Burn Session 1 with CloseDisc = false
        var burnResult1 = await burnEngine.BurnDataStreamAsync(
            recorder,
            session1Stream,
            new DataBurnOptions { CloseDisc = false },
            progress: null,
            CancellationToken.None);

        Assert.True(burnResult1.Success, burnResult1.ErrorMessage);
        Assert.True(burnResult1.SectorsWritten > 0);

        // 4. Verify QueryMediaStatus() after Session 1
        var status1 = recorder.QueryMediaStatus();
        Assert.Equal(1, status1.Sessions);
        Assert.Equal(1, status1.SessionCount);
        Assert.False(status1.IsBlank);
        Assert.True(status1.IsAppendable);
        Assert.True(status1.NextWritableLba > 0);

        int nextWritableLba = status1.NextWritableLba;

        // 5. Import Session 1 via MultiSessionManager
        using var session1DiscStream = medium.CreateReadStream();
        var mergedRoot = MultiSessionManager.ImportSessionTree(session1DiscStream, sessionStartLba: 0);

        // Verify /Session1/fileA.txt imported with its already-recorded LBA
        var importedFileA = mergedRoot.FindFileByVirtualPath("/Session1/fileA.txt");
        Assert.NotNull(importedFileA);
        Assert.Equal(fileABytes.Length, importedFileA.SizeBytes);
        Assert.NotNull(importedFileA.ImportedStartLba);
        int session1FileALba = importedFileA.ImportedStartLba.Value;
        Assert.True(session1FileALba > 0);

        // 6. Add /Session2/fileB.txt = 8 KB
        var session2Dir = mergedRoot.AddDirectory("Session2");
        byte[] fileBBytes = new byte[8 * 1024]; // 8 KB = 8,192 bytes
        Random.Shared.NextBytes(fileBBytes);
        session2Dir.AddFile("fileB.txt", fileBBytes);

        // 7. Build appended session stream at NextWritableLba
        var session2BuildOptions = new IsoBuildOptions("SESSION2", IncludeUdf: false);
        using var session2Stream = MultiSessionManager.BuildAppendedSessionStream(
            mergedRoot,
            nextWritableLba,
            session2BuildOptions);

        Assert.NotNull(session2Stream);
        Assert.True(session2Stream.Length > 0);

        // 8. Burn Session 2 with CloseDisc = true
        var burnResult2 = await burnEngine.BurnDataStreamAsync(
            recorder,
            session2Stream,
            new DataBurnOptions { CloseDisc = true },
            progress: null,
            CancellationToken.None);

        Assert.True(burnResult2.Success, burnResult2.ErrorMessage);

        // 9. Verify media status after Session 2
        var status2 = recorder.QueryMediaStatus();
        Assert.Equal(2, status2.Sessions);
        Assert.Equal(2, status2.SessionCount);
        Assert.False(status2.IsAppendable);

        // 10. Verify merged volume reading
        using var mergedDiscStream = medium.CreateReadStream();
        using var mergedReader = IsoImageReader.FromStream(mergedDiscStream, leaveOpen: true, sessionStartLba: nextWritableLba);

        // Verify /Session1/fileA.txt still points to its Session 1 LBA and is readable
        var fileAEntry = mergedReader.FindFile("/Session1/fileA.txt");
        Assert.NotNull(fileAEntry);
        Assert.Equal(session1FileALba, fileAEntry.StartLba);
        byte[] readBackA = mergedReader.ReadFileBytes(fileAEntry);
        Assert.Equal(fileABytes, readBackA);

        // Verify /Session2/fileB.txt points to Session 2 LBA and is readable
        var fileBEntry = mergedReader.FindFile("/Session2/fileB.txt");
        Assert.NotNull(fileBEntry);
        Assert.True(fileBEntry.StartLba >= nextWritableLba);
        byte[] readBackB = mergedReader.ReadFileBytes(fileBEntry);
        Assert.Equal(fileBBytes, readBackB);
    }

    [Fact]
    public async Task MultiSession_ModifiedFile_AllocatesNewLba_AndUnchangedFilePreservesLba()
    {
        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);
        var engine = new VirtualBurnEngine(recorder);

        // Session 1: two files
        var root1 = CompilationDirectory.CreateRoot();
        byte[] keepBytes = new byte[4096];
        Random.Shared.NextBytes(keepBytes);
        root1.AddFile("keep.dat", keepBytes);

        byte[] originalModBytes = new byte[6144];
        Random.Shared.NextBytes(originalModBytes);
        root1.AddFile("modified.dat", originalModBytes);

        using var s1Stream = IsoUdfImageBuilder.BuildToMemoryStream(root1, new IsoBuildOptions(IncludeUdf: false));
        await engine.BurnDataStreamAsync(recorder, s1Stream, new DataBurnOptions { CloseDisc = false }, null, CancellationToken.None);

        var status1 = recorder.QueryMediaStatus();
        int nextWritable = status1.NextWritableLba;

        // Import Session 1
        using var discStream1 = medium.CreateReadStream();
        var mergedRoot = MultiSessionManager.ImportSessionTree(discStream1, sessionStartLba: 0);

        var keepFile = mergedRoot.FindFileByVirtualPath("/keep.dat");
        Assert.NotNull(keepFile);
        int keepLba = keepFile.ImportedStartLba!.Value;

        // Modify "modified.dat": replace with new content and clear ImportedStartLba
        byte[] newModBytes = new byte[8192];
        Random.Shared.NextBytes(newModBytes);
        mergedRoot.RemoveChild("modified.dat");
        mergedRoot.AddFile("modified.dat", newModBytes); // Newly added file has ImportedStartLba == null

        using var s2Stream = MultiSessionManager.BuildAppendedSessionStream(mergedRoot, nextWritable, new IsoBuildOptions(IncludeUdf: false));
        await engine.BurnDataStreamAsync(recorder, s2Stream, new DataBurnOptions { CloseDisc = true }, null, CancellationToken.None);

        // Inspect Session 2
        using var mergedDiscStream = medium.CreateReadStream();
        using var reader = IsoImageReader.FromStream(mergedDiscStream, leaveOpen: true, sessionStartLba: nextWritable);

        var readKeep = reader.FindFile("/keep.dat");
        Assert.NotNull(readKeep);
        Assert.Equal(keepLba, readKeep.StartLba);
        Assert.Equal(keepBytes, reader.ReadFileBytes(readKeep));

        var readModified = reader.FindFile("/modified.dat");
        Assert.NotNull(readModified);
        Assert.True(readModified.StartLba >= nextWritable);
        Assert.Equal(newModBytes, reader.ReadFileBytes(readModified));
    }

    [Fact]
    public async Task MultiSession_DeletedFile_IsNotPresentInAppendedSession()
    {
        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);
        var engine = new VirtualBurnEngine(recorder);

        // Session 1: keep.txt and delete.txt
        var root1 = CompilationDirectory.CreateRoot();
        root1.AddFile("keep.txt", new byte[] { 1, 2, 3, 4 });
        root1.AddFile("delete.txt", new byte[] { 5, 6, 7, 8 });

        using var s1Stream = IsoUdfImageBuilder.BuildToMemoryStream(root1, new IsoBuildOptions(IncludeUdf: false));
        await engine.BurnDataStreamAsync(recorder, s1Stream, new DataBurnOptions { CloseDisc = false }, null, CancellationToken.None);

        int nextWritable = recorder.QueryMediaStatus().NextWritableLba;

        // Import and delete
        using var discStream = medium.CreateReadStream();
        var mergedRoot = MultiSessionManager.ImportSessionTree(discStream, 0);
        bool removed = mergedRoot.RemoveChild("delete.txt");
        Assert.True(removed);

        using var s2Stream = MultiSessionManager.BuildAppendedSessionStream(mergedRoot, nextWritable, new IsoBuildOptions(IncludeUdf: false));
        await engine.BurnDataStreamAsync(recorder, s2Stream, new DataBurnOptions { CloseDisc = true }, null, CancellationToken.None);

        // Inspect Session 2
        using var finalDiscStream = medium.CreateReadStream();
        using var reader = IsoImageReader.FromStream(finalDiscStream, leaveOpen: true, sessionStartLba: nextWritable);

        Assert.NotNull(reader.FindFile("/keep.txt"));
        Assert.Null(reader.FindFile("/delete.txt"));
    }

    [Fact]
    public async Task MultiSession_ThreeSessions_ProgressivelyAppendsAndMaintainsAllPointers()
    {
        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);
        var engine = new VirtualBurnEngine(recorder);

        // Session 1
        var r1 = CompilationDirectory.CreateRoot();
        byte[] doc1 = new byte[2048];
        Random.Shared.NextBytes(doc1);
        r1.AddDirectory("S1").AddFile("doc1.txt", doc1);

        using var s1Stream = IsoUdfImageBuilder.BuildToMemoryStream(r1, new IsoBuildOptions(IncludeUdf: false));
        await engine.BurnDataStreamAsync(recorder, s1Stream, new DataBurnOptions { CloseDisc = false }, null, CancellationToken.None);

        var st1 = recorder.QueryMediaStatus();
        Assert.Equal(1, st1.Sessions);
        Assert.True(st1.IsAppendable);
        int lbaS2 = st1.NextWritableLba;

        // Session 2
        using var discStream1 = medium.CreateReadStream();
        var r2 = MultiSessionManager.ImportSessionTree(discStream1, 0);
        byte[] doc2 = new byte[4096];
        Random.Shared.NextBytes(doc2);
        r2.AddDirectory("S2").AddFile("doc2.txt", doc2);

        using var s2Stream = MultiSessionManager.BuildAppendedSessionStream(r2, lbaS2, new IsoBuildOptions(IncludeUdf: false));
        await engine.BurnDataStreamAsync(recorder, s2Stream, new DataBurnOptions { CloseDisc = false }, null, CancellationToken.None);

        var st2 = recorder.QueryMediaStatus();
        Assert.Equal(2, st2.Sessions);
        Assert.True(st2.IsAppendable);
        int lbaS3 = st2.NextWritableLba;

        // Session 3
        using var discStream2 = medium.CreateReadStream();
        var r3 = MultiSessionManager.ImportSessionTree(discStream2, sessionStartLba: lbaS2);
        byte[] doc3 = new byte[1024];
        Random.Shared.NextBytes(doc3);
        r3.AddDirectory("S3").AddFile("doc3.txt", doc3);

        using var s3Stream = MultiSessionManager.BuildAppendedSessionStream(r3, lbaS3, new IsoBuildOptions(IncludeUdf: false));
        await engine.BurnDataStreamAsync(recorder, s3Stream, new DataBurnOptions { CloseDisc = true }, null, CancellationToken.None);

        var st3 = recorder.QueryMediaStatus();
        Assert.Equal(3, st3.Sessions);
        Assert.False(st3.IsAppendable);

        // Verify all 3 files from Session 3
        using var finalDiscStream = medium.CreateReadStream();
        using var reader = IsoImageReader.FromStream(finalDiscStream, leaveOpen: true, sessionStartLba: lbaS3);

        var f1 = reader.FindFile("/S1/doc1.txt");
        Assert.NotNull(f1);
        Assert.Equal(doc1, reader.ReadFileBytes(f1));

        var f2 = reader.FindFile("/S2/doc2.txt");
        Assert.NotNull(f2);
        Assert.Equal(doc2, reader.ReadFileBytes(f2));

        var f3 = reader.FindFile("/S3/doc3.txt");
        Assert.NotNull(f3);
        Assert.Equal(doc3, reader.ReadFileBytes(f3));
    }

    [Fact]
    public async Task MultiSession_ImportFromRecorderAsync_ImportsActiveSessionDirectly()
    {
        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);
        var engine = new VirtualBurnEngine(recorder);

        var root = CompilationDirectory.CreateRoot();
        root.AddDirectory("Sub").AddFile("test.txt", new byte[] { 10, 20, 30 });

        using var sStream = IsoUdfImageBuilder.BuildToMemoryStream(root, new IsoBuildOptions(IncludeUdf: false));
        await engine.BurnDataStreamAsync(recorder, sStream, new DataBurnOptions { CloseDisc = false }, null, CancellationToken.None);

        var imported = await MultiSessionManager.ImportFromRecorderAsync(recorder, engine);
        Assert.NotNull(imported);
        var file = imported.FindFileByVirtualPath("/Sub/test.txt");
        Assert.NotNull(file);
        Assert.NotNull(file.ImportedStartLba);
        Assert.Equal(3, file.SizeBytes);
    }

    [Fact]
    public async Task MultiSession_AppendedSessionStream_DoesNotDuplicateUnchangedFileBytes()
    {
        using var recorder = new VirtualDiscRecorder();
        using var medium = VirtualDiscMedium.CreateBlankCdR700();
        recorder.InsertMedia(medium);
        var engine = new VirtualBurnEngine(recorder);

        // Session 1: 500 KB file (250 sectors)
        var root1 = CompilationDirectory.CreateRoot();
        byte[] bigFile = new byte[500 * 1024];
        root1.AddFile("big.bin", bigFile);

        using var s1Stream = IsoUdfImageBuilder.BuildToMemoryStream(root1, new IsoBuildOptions(IncludeUdf: false));
        await engine.BurnDataStreamAsync(recorder, s1Stream, new DataBurnOptions { CloseDisc = false }, null, CancellationToken.None);

        int nextWritable = recorder.QueryMediaStatus().NextWritableLba;
        Assert.True(nextWritable >= 250);

        // Import Session 1 and add tiny 50-byte file
        using var discStream = medium.CreateReadStream();
        var mergedRoot = MultiSessionManager.ImportSessionTree(discStream, 0);
        mergedRoot.AddFile("small.txt", new byte[50]);

        using var s2Stream = MultiSessionManager.BuildAppendedSessionStream(mergedRoot, nextWritable, new IsoBuildOptions(IncludeUdf: false));

        // The appended session stream should only contain descriptors and the small file,
        // which is around 25-30 sectors (< 70 KB), definitely NOT 500 KB!
        Assert.True(s2Stream.Length < 100 * 1024, $"Appended session stream was {s2Stream.Length} bytes, expected < 100 KB.");
    }
}
