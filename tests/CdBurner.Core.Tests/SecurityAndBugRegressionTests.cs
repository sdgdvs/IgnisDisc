using System.Buffers.Binary;
using System.Text;
using CdBurner.Core.Audio;
using CdBurner.Core.Compilation;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Images;
using CdBurner.Core.Iso;
using CdBurner.Core.Persistence;
using CdBurner.Core.Primitives;
using Xunit;

namespace CdBurner.Core.Tests;

/// <summary>
/// Regression tests verifying security & bug fixes across CdBurner.Core
/// (WAV decompression bombs, DVD IFO VTS_ATRT/C_ADT layout, path traversal,
/// CUE directive injection, UDF Root ICB, and compilation cycle safety).
/// </summary>
public sealed class SecurityAndBugRegressionTests
{
    [Fact]
    public void DvdIfoGenerator_BuildVmgIfo_ManyTitleSets_DynamicallyExpandsSectorsAndPreservesCAdtOffsets()
    {
        // 15 title sets exceeds a single 2048-byte sector for VTS_ATRT (15 * 776 + 8 = 11,648 bytes -> 6 sectors)
        byte[] vmgIfo = DvdIfoGenerator.BuildVmgIfo(
            titleSetCount: 15,
            standard: DvdVideoStandard.Ntsc,
            aspect: DvdAspectRatio.Widescreen16x9,
            menuVobSectors: 10);

        Assert.True(vmgIfo.Length > 6 * 2048);
        Assert.Equal(0, vmgIfo.Length % 2048);

        // Verify VMGM_C_ADT sector pointer in VMGI_MAT (offset 0xDC)
        uint cAdtSector = BinaryPrimitives.ReadUInt32BigEndian(vmgIfo.AsSpan(0xDC, 4));
        int cAdtOffset = (int)(cAdtSector * 2048);

        // Verify C_ADT header (8 bytes) + cell_adr_t entry (12 bytes -> last_byte = 19)
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(vmgIfo.AsSpan(cAdtOffset + 0, 2)));
        Assert.Equal(19u, BinaryPrimitives.ReadUInt32BigEndian(vmgIfo.AsSpan(cAdtOffset + 4, 4)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(vmgIfo.AsSpan(cAdtOffset + 8, 2))); // vob_id
        Assert.Equal(1, vmgIfo[cAdtOffset + 10]); // cell_id
        Assert.Equal(9u, BinaryPrimitives.ReadUInt32BigEndian(vmgIfo.AsSpan(cAdtOffset + 16, 4))); // last_sector
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(0xFFFFFFFFu)]
    public void WavFileCodec_InvalidSampleRate_ThrowsInvalidDataException(uint maliciousSampleRate)
    {
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            bw.Write("RIFF"u8);
            bw.Write(36 + 100);
            bw.Write("WAVE"u8);
            bw.Write("fmt "u8);
            bw.Write(16);
            bw.Write((ushort)1); // PCM
            bw.Write((ushort)2); // 2 channels
            bw.Write(maliciousSampleRate);
            bw.Write(176400u);
            bw.Write((ushort)4);
            bw.Write((ushort)16);
            bw.Write("data"u8);
            bw.Write(100);
            bw.Write(new byte[100]);
        }

        ms.Position = 0;
        Assert.Throws<InvalidDataException>(() => WavFileCodec.DecodeToRedBookPcm(ms));
    }

    [Theory]
    [InlineData(".", "Track")]
    [InlineData("..", "Track")]
    [InlineData("CON", "_CON")]
    [InlineData("NUL.wav", "_NUL.wav")]
    [InlineData("Song Title...", "Song Title")]
    public void AudioCdRipperService_SanitizeFileName_BlocksTraversalAndReservedDeviceNames(string input, string expected)
    {
        string actual = AudioCdRipperService.SanitizeFileName(input);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void CueSheetWriter_NewlineInMetadata_CannotInjectDirectiveLine()
    {
        var sheet = new CueSheet(
            Title: "Good Title\r\nFILE \"C:\\secret.bin\" BINARY",
            Performer: "Artist\nREM Injected",
            Files:
            [
                new CueFileEntry(
                    FileName: "disc.bin",
                    FileType: "BINARY",
                    Tracks:
                    [
                        new CueTrack(
                            TrackNumber: 1,
                            DataType: CueTrackDataType.Audio,
                            Title: "Track1\r\nINDEX 01 99:00:00",
                            Indices: [new CueIndex(1, new MsfAddress(0, 0, 0))])
                    ])
            ]);

        string serialized = CueSheetWriter.WriteToString(sheet);
        string[] lines = serialized.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        // Only a single FILE directive line and a single INDEX directive line should exist
        Assert.Single(lines, l => l.TrimStart().StartsWith("FILE ", StringComparison.OrdinalIgnoreCase));
        Assert.Single(lines, l => l.TrimStart().StartsWith("INDEX ", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CompilationDirectory_AddDirectory_SelfOrAncestorCycle_ThrowsInvalidOperationException()
    {
        var root = CompilationDirectory.CreateRoot();
        var parent = root.AddDirectory("Parent");
        var child = parent.AddDirectory("Child");

        Assert.Throws<InvalidOperationException>(() => child.AddDirectory(parent));
        Assert.Throws<InvalidOperationException>(() => parent.AddDirectory(parent));
    }

    [Fact]
    public void CompilationFile_OpenRead_WhenSourcePathDoesNotExist_ThrowsFileNotFoundException()
    {
        var missingFile = new CompilationFile(
            "missing.txt",
            @"C:\NonExistentDirectory_IgnisDisc_Test\missing_file_12345.txt",
            sizeBytes: 1024);

        Assert.Throws<FileNotFoundException>(() => missingFile.OpenRead());
    }

    [Fact]
    public void ProjectSerializer_MaliciousNodeNameWithPathTraversal_ThrowsInvalidDataException()
    {
        var doc = new CdBurnerProjectDocument
        {
            Kind = ProjectKind.DataDisc,
            RootNode = new SerializedCompilationNode
            {
                Name = "",
                IsDirectory = true,
                Children =
                {
                    new SerializedCompilationNode
                    {
                        Name = @"..\..\evil.exe",
                        IsDirectory = false,
                        SizeBytes = 128
                    }
                }
            }
        };

        Assert.Throws<InvalidDataException>(() => ProjectSerializer.ToCompilationNode(doc.RootNode));
    }

    [Fact]
    public void IsoUdfImageBuilder_CollidingLongFileNames_DisambiguatesIso9660Level1Names()
    {
        var root = CompilationDirectory.CreateRoot();
        root.AddFile("quarterly_financial_report_january.txt", "jan"u8.ToArray());
        root.AddFile("quarterly_financial_report_february.txt", "feb"u8.ToArray());

        using var isoStream = IsoUdfImageBuilder.BuildToMemoryStream(root, new IsoBuildOptions(
            VolumeLabel: "COLLISION_TEST",
            IncludeIso9660: true,
            IncludeJoliet: false,
            IncludeUdf: false));

        using var reader = IsoImageReader.FromStream(isoStream, leaveOpen: true);
        var files = reader.RootDirectory.Files.ToList();
        Assert.Equal(2, files.Count);
        Assert.NotEqual(files[0].Name, files[1].Name, StringComparer.OrdinalIgnoreCase);
    }
}
