using System.Buffers.Binary;
using CdBurner.Core.Audio;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

public class ReadOffsetDetectorTests
{
    // Straight transcription of the published algorithm (accuraterip-checksum by L. Bogert).
    private static (uint V1, uint V2) ReferenceAccurateRip(ReadOnlySpan<byte> pcm, bool first, bool last)
    {
        unchecked
        {
            uint n = (uint)(pcm.Length / 4);
            uint checkStart = first ? 588u * 5 : 0;
            uint checkEnd = last ? n - 588u * 5 : n;
            uint v1 = 0, v2 = 0;
            for (uint i = 0, mul = 1; i < n; i++, mul++)
            {
                uint value = BinaryPrimitives.ReadUInt32LittleEndian(pcm.Slice((int)(i * 4), 4));
                if (mul >= checkStart && mul <= checkEnd)
                {
                    ulong p = (ulong)value * mul;
                    v1 += (uint)p;
                    v2 += (uint)(p >> 32);
                    v2 += (uint)p;
                }
            }
            return (v1, v2);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-6)]
    [InlineData(667)]
    [InlineData(-1164)]
    [InlineData(2999)]
    [InlineData(-3000)]
    public void FindOffsets_FindsPlantedOffset(int planted)
    {
        const int margin = 3000;
        const int trackSamples = 588 * 40;
        int totalSamples = trackSamples + 2 * margin;

        var rng = new Random(42);
        byte[] padded = new byte[totalSamples * 4];
        rng.NextBytes(padded);

        int windowStartByte = (margin + planted) * 4;
        int windowByteLength = trackSamples * 4;
        var window = padded.AsSpan(windowStartByte, windowByteLength);
        var (refV1, _) = ReferenceAccurateRip(window, first: false, last: false);

        var pressing = new AccurateRipPressing(
            0x11111111u,
            0x22222222u,
            0x33333333u,
            new AccurateRipTrackEntry[]
            {
                new(Confidence: 1, Crc: 0xAAAAAAAAu, Frame450Crc: 0),
                new(Confidence: 9, Crc: refV1, Frame450Crc: 0),
                new(Confidence: 1, Crc: 0xBBBBBBBBu, Frame450Crc: 0)
            });
        var pressings = new[] { pressing };

        var candidates = ReadOffsetDetector.FindOffsets(
            padded,
            margin,
            trackSamples,
            pressings,
            trackIndex: 1,
            trackNumber: 2);

        Assert.Contains(candidates, c => c.OffsetSamples == planted && c.Confidence == 9);
    }

    [Fact]
    public void FindOffsets_NoDatabaseEntries_ReturnsEmpty()
    {
        const int margin = 100;
        const int trackSamples = 588 * 10;
        byte[] padded = new byte[(trackSamples + 2 * margin) * 4];

        var candidates = ReadOffsetDetector.FindOffsets(
            padded,
            margin,
            trackSamples,
            Array.Empty<AccurateRipPressing>(),
            trackIndex: 1,
            trackNumber: 2);

        Assert.Empty(candidates);
    }

    [Fact]
    public void FindOffsets_PaddedBufferTooShort_Throws()
    {
        const int margin = 100;
        const int trackSamples = 588 * 10;
        byte[] tooShort = new byte[(trackSamples + 2 * margin) * 4 - 1];

        Assert.Throws<ArgumentException>(() => ReadOffsetDetector.FindOffsets(
            tooShort,
            margin,
            trackSamples,
            Array.Empty<AccurateRipPressing>(),
            trackIndex: 1,
            trackNumber: 2));
    }

    [Fact]
    public async Task DetectAsync_FewerThanThreeTracks_ReturnsError()
    {
        using var recorder = new VirtualDiscRecorder();
        var engine = new VirtualBurnEngine(recorder);

        var tracks = new[]
        {
            new AudioDiscoveredTrack(1, 0, 100, 150, "Track 1", "Artist"),
            new AudioDiscoveredTrack(2, 100, 100, 0, "Track 2", "Artist")
        };

        var result = await ReadOffsetDetector.DetectAsync(
            engine,
            recorder,
            tracks,
            Array.Empty<AccurateRipPressing>());

        Assert.NotNull(result.ErrorMessage);
        Assert.Empty(result.Candidates);
        Assert.Empty(result.ProbedTracks);
        Assert.Null(result.Best);
    }

    [Fact]
    public async Task DetectAsync_VirtualDisc_FindsPlantedOffset()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "CdBurner_OffsetDet_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var rng = new Random(12345);
            int[] trackSectorCounts = [60, 120, 150, 60];
            byte[][] trackPcms = new byte[4][];
            int totalBytes = 0;

            for (int i = 0; i < 4; i++)
            {
                trackPcms[i] = new byte[trackSectorCounts[i] * OpticalConstants.RawSectorSize];
                rng.NextBytes(trackPcms[i]);
                totalBytes += trackPcms[i].Length;
            }

            byte[] wholeDiscPcm = new byte[totalBytes];
            int offset = 0;
            for (int i = 0; i < 4; i++)
            {
                trackPcms[i].CopyTo(wholeDiscPcm, offset);
                offset += trackPcms[i].Length;
            }

            var compilation = new AudioCdCompilation
            {
                AlbumTitle = "Offset Detection Album",
                AlbumPerformer = "Test Artist",
                RecordingMode = AudioRecordingMode.DiscAtOnceGapless,
                IncludeCdText = true
            };

            for (int i = 0; i < 4; i++)
            {
                string wavPath = Path.Combine(tempDir, $"burn_track_{i + 1}.wav");
                WavFileCodec.WriteWavFile(wavPath, trackPcms[i]);
                compilation.AddTrack(new AudioCdTrackItem(
                    i + 1,
                    wavPath,
                    $"Track {i + 1:D2}",
                    "Test Artist",
                    PreGapFrames: i == 0 ? 150 : 0));
            }

            using var recorder = new VirtualDiscRecorder();
            using var medium = VirtualDiscMedium.CreateBlankCdR700();
            recorder.InsertMedia(medium);

            var engine = new VirtualBurnEngine(recorder);
            var burnService = new AudioCdBurnService(engine);
            var burnResult = await burnService.BurnCompilationAsync(recorder, compilation);
            Assert.True(burnResult.Success, burnResult.ErrorMessage);

            const int planted = 37;

            // Track index 1 (Track 2): starts at sector 60, length 120 sectors
            int track1StartSample = trackSectorCounts[0] * (OpticalConstants.RawSectorSize / 4);
            int track1Samples = trackSectorCounts[1] * (OpticalConstants.RawSectorSize / 4);
            var window1 = wholeDiscPcm.AsSpan((track1StartSample + planted) * 4, track1Samples * 4);
            var (refV1Track1, _) = ReferenceAccurateRip(window1, first: false, last: false);

            // Track index 2 (Track 3): starts at sector 60 + 120 = 180, length 150 sectors
            int track2StartSample = (trackSectorCounts[0] + trackSectorCounts[1]) * (OpticalConstants.RawSectorSize / 4);
            int track2Samples = trackSectorCounts[2] * (OpticalConstants.RawSectorSize / 4);
            var window2 = wholeDiscPcm.AsSpan((track2StartSample + planted) * 4, track2Samples * 4);
            var (refV1Track2, _) = ReferenceAccurateRip(window2, first: false, last: false);

            var pressing = new AccurateRipPressing(
                0x12345678,
                0x87654321,
                0xabcdef01,
                new AccurateRipTrackEntry[]
                {
                    new(Confidence: 5, Crc: 0x11111111u, Frame450Crc: 0),
                    new(Confidence: 5, Crc: refV1Track1, Frame450Crc: 0),
                    new(Confidence: 5, Crc: refV1Track2, Frame450Crc: 0),
                    new(Confidence: 5, Crc: 0x44444444u, Frame450Crc: 0)
                });
            var pressings = new[] { pressing };

            var (tracks, _) = AudioCdRipperService.QueryTracksAndCdText(recorder);
            var result = await ReadOffsetDetector.DetectAsync(engine, recorder, tracks, pressings, maxOffsetSamples: 3000);

            Assert.Null(result.ErrorMessage);
            Assert.Contains(2, result.ProbedTracks);
            Assert.Contains(3, result.ProbedTracks);
            Assert.NotNull(result.Best);
            Assert.Equal(37, result.Best.OffsetSamples);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
            catch { }
        }
    }
}
