using System.Buffers.Binary;
using CdBurner.Core.Audio;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Simulation;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// Shared virtual disc fixture for AccurateRip and offset verification tests.
/// Burns a deterministic 3-track gapless Audio CD once against VirtualDiscRecorder.
/// </summary>
public sealed class VirtualAudioDiscFixture : IAsyncLifetime
{
    public string TempDir { get; }
    public VirtualDiscRecorder Recorder { get; private set; } = null!;
    public VirtualBurnEngine Engine { get; private set; } = null!;
    public VirtualDiscMedium Medium { get; private set; } = null!;
    public byte[] Track1Pcm { get; private set; } = null!;
    public byte[] Track2Pcm { get; private set; } = null!;
    public byte[] Track3Pcm { get; private set; } = null!;
    public byte[] WholeAudioPcm { get; private set; } = null!;

    public const int Track1Frames = 50;
    public const int Track2Frames = 75;
    public const int Track3Frames = 100;

    public VirtualAudioDiscFixture()
    {
        TempDir = Path.Combine(Path.GetTempPath(), "CdBurner_AccurateRip_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TempDir);
    }

    public async Task InitializeAsync()
    {
        var rng = new Random(12345);
        Track1Pcm = new byte[Track1Frames * OpticalConstants.RawSectorSize];
        rng.NextBytes(Track1Pcm);
        Track2Pcm = new byte[Track2Frames * OpticalConstants.RawSectorSize];
        rng.NextBytes(Track2Pcm);
        Track3Pcm = new byte[Track3Frames * OpticalConstants.RawSectorSize];
        rng.NextBytes(Track3Pcm);

        string wavPath1 = Path.Combine(TempDir, "burn_input_track1.wav");
        string wavPath2 = Path.Combine(TempDir, "burn_input_track2.wav");
        string wavPath3 = Path.Combine(TempDir, "burn_input_track3.wav");
        WavFileCodec.WriteWavFile(wavPath1, Track1Pcm);
        WavFileCodec.WriteWavFile(wavPath2, Track2Pcm);
        WavFileCodec.WriteWavFile(wavPath3, Track3Pcm);

        var compilation = new AudioCdCompilation
        {
            AlbumTitle = "AccurateRip Offset Verification Album",
            AlbumPerformer = "Reference Test Performer",
            RecordingMode = AudioRecordingMode.DiscAtOnceGapless,
            IncludeCdText = true
        };

        compilation.AddTrack(new AudioCdTrackItem(1, wavPath1, "Test Track 01", "Reference Test Performer", PreGapFrames: 150));
        compilation.AddTrack(new AudioCdTrackItem(2, wavPath2, "Test Track 02", "Reference Test Performer", PreGapFrames: 0));
        compilation.AddTrack(new AudioCdTrackItem(3, wavPath3, "Test Track 03", "Reference Test Performer", PreGapFrames: 0));

        Recorder = new VirtualDiscRecorder();
        Medium = VirtualDiscMedium.CreateBlankCdR700();
        Recorder.InsertMedia(Medium);

        Engine = new VirtualBurnEngine(Recorder);
        var burnService = new AudioCdBurnService(Engine);

        var burnResult = await burnService.BurnCompilationAsync(Recorder, compilation);
        if (!burnResult.Success)
        {
            throw new InvalidOperationException($"Virtual audio CD burn failed: {burnResult.ErrorMessage}");
        }

        // Build the whole audio area by concatenating the burned track PCM streams
        // (verified in test 6 to be byte-identical to the zero-offset ripped streams)
        WholeAudioPcm = new byte[Track1Pcm.Length + Track2Pcm.Length + Track3Pcm.Length];
        Track1Pcm.CopyTo(WholeAudioPcm, 0);
        Track2Pcm.CopyTo(WholeAudioPcm, Track1Pcm.Length);
        Track3Pcm.CopyTo(WholeAudioPcm, Track1Pcm.Length + Track2Pcm.Length);
    }

    public Task DisposeAsync()
    {
        try { Recorder?.Dispose(); } catch { }
        try { Medium?.Dispose(); } catch { }
        try
        {
            if (Directory.Exists(TempDir))
            {
                Directory.Delete(TempDir, true);
            }
        }
        catch { }
        return Task.CompletedTask;
    }
}

/// <summary>
/// Unit and integration tests for AccurateRip v1/v2 checksums, dBAR database parsing/matching,
/// and DAE read-offset sample correction.
/// </summary>
public class AccurateRipAndOffsetTests : IClassFixture<VirtualAudioDiscFixture>
{
    private readonly VirtualAudioDiscFixture _fixture;

    public AccurateRipAndOffsetTests(VirtualAudioDiscFixture fixture)
    {
        _fixture = fixture;
    }

    // Straight transcription of the published algorithm (accuraterip-checksum by L. Bogert).
    private static (uint V1, uint V2) ReferenceAccurateRip(byte[] pcm, bool first, bool last)
    {
        unchecked
        {
            uint n = (uint)(pcm.Length / 4);
            uint checkStart = first ? 588u * 5 : 0;
            uint checkEnd = last ? n - 588u * 5 : n;
            uint v1 = 0, v2 = 0;
            for (uint i = 0, mul = 1; i < n; i++, mul++)
            {
                uint value = BitConverter.ToUInt32(pcm, (int)(i * 4));
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

    private static byte[] SliceWithZeroPadding(byte[] source, int start, int length)
    {
        byte[] result = new byte[length];
        if (start < source.Length)
        {
            int available = Math.Min(length, source.Length - start);
            if (available > 0)
            {
                Array.Copy(source, start, result, 0, available);
            }
        }
        return result;
    }

    private static byte[] BuildPressingBytes(
        byte trackCount,
        uint discId1,
        uint discId2,
        uint freeDbId,
        (byte Confidence, uint Crc, uint Frame450Crc)[] tracks)
    {
        byte[] block = new byte[13 + trackCount * 9];
        block[0] = trackCount;
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(1, 4), discId1);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(5, 4), discId2);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(9, 4), freeDbId);

        for (int i = 0; i < tracks.Length; i++)
        {
            int offset = 13 + i * 9;
            block[offset] = tracks[i].Confidence;
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(offset + 1, 4), tracks[i].Crc);
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(offset + 5, 4), tracks[i].Frame450Crc);
        }
        return block;
    }

    private static byte[] BuildTwoPressingDbar(
        uint id1A, uint id2A, uint freeDbA, (byte Confidence, uint Crc, uint Frame450Crc)[] tracksA,
        uint id1B, uint id2B, uint freeDbB, (byte Confidence, uint Crc, uint Frame450Crc)[] tracksB)
    {
        byte[] blockA = BuildPressingBytes((byte)tracksA.Length, id1A, id2A, freeDbA, tracksA);
        byte[] blockB = BuildPressingBytes((byte)tracksB.Length, id1B, id2B, freeDbB, tracksB);

        byte[] dbar = new byte[blockA.Length + blockB.Length];
        blockA.CopyTo(dbar, 0);
        blockB.CopyTo(dbar, blockA.Length);
        return dbar;
    }

    private async Task<byte[]> GetZeroOffsetWholeAudioPcmAsync()
    {
        string zeroFolder = Path.Combine(_fixture.TempDir, "rip_zero_reference");
        if (!Directory.Exists(zeroFolder))
        {
            var ripper = new AudioCdRipperService();
            await ripper.RipAudioCdAsync(_fixture.Recorder, _fixture.Engine, zeroFolder);
        }

        var wavFiles = Directory.GetFiles(zeroFolder, "*.wav").OrderBy(f => f).ToList();
        var pcmBytes = new List<byte>();
        foreach (string wavPath in wavFiles)
        {
            byte[] wav = await File.ReadAllBytesAsync(wavPath);
            pcmBytes.AddRange(wav[44..]);
        }
        return pcmBytes.ToArray();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AccurateRipChecksum_MatchesReference(bool first, bool last)
    {
        byte[] pcm = new byte[20 * OpticalConstants.RawSectorSize];
        new Random(1234).NextBytes(pcm);

        var checksum = new AccurateRipChecksum(pcm.Length / 4, first, last);

        int[] chunkSizes = [1, 3, 5, 2351, 7];
        int offset = 0;
        foreach (int size in chunkSizes)
        {
            if (offset + size <= pcm.Length)
            {
                checksum.Append(pcm.AsSpan(offset, size));
                offset += size;
            }
        }
        if (offset < pcm.Length)
        {
            checksum.Append(pcm.AsSpan(offset));
        }

        var (refV1, refV2) = ReferenceAccurateRip(pcm, first, last);
        Assert.Equal(refV1, checksum.V1);
        Assert.Equal(refV2, checksum.V2);
    }

    [Fact]
    public void AccurateRipChecksum_SingleChunk_EqualsChunked()
    {
        byte[] pcm = new byte[20 * OpticalConstants.RawSectorSize];
        new Random(1234).NextBytes(pcm);

        var single = new AccurateRipChecksum(pcm.Length / 4, isFirstTrack: true, isLastTrack: true);
        single.Append(pcm);

        var chunked = new AccurateRipChecksum(pcm.Length / 4, isFirstTrack: true, isLastTrack: true);
        int[] chunkSizes = [1, 3, 5, 2351, 7];
        int offset = 0;
        foreach (int size in chunkSizes)
        {
            if (offset + size <= pcm.Length)
            {
                chunked.Append(pcm.AsSpan(offset, size));
                offset += size;
            }
        }
        if (offset < pcm.Length)
        {
            chunked.Append(pcm.AsSpan(offset));
        }

        Assert.Equal(single.V1, chunked.V1);
        Assert.Equal(single.V2, chunked.V2);
    }

    [Fact]
    public void Parse_TwoPressings_ReadsHeadersAndEntries()
    {
        byte[] dbar = BuildTwoPressingDbar(
            id1A: 0x11111111u, id2A: 0x22222222u, freeDbA: 0x33333333u,
            tracksA:
            [
                (Confidence: 10, Crc: 0x10000001u, Frame450Crc: 0x20000001u),
                (Confidence: 20, Crc: 0x10000002u, Frame450Crc: 0x20000002u),
                (Confidence: 30, Crc: 0x10000003u, Frame450Crc: 0x20000003u)
            ],
            id1B: 0x44444444u, id2B: 0x55555555u, freeDbB: 0x66666666u,
            tracksB:
            [
                (Confidence: 5, Crc: 0x30000001u, Frame450Crc: 0x40000001u),
                (Confidence: 15, Crc: 0x30000002u, Frame450Crc: 0x40000002u),
                (Confidence: 25, Crc: 0x30000003u, Frame450Crc: 0x40000003u)
            ]);

        var pressings = AccurateRipDatabase.Parse(dbar);

        Assert.Equal(2, pressings.Count);

        // Pressing A
        var pA = pressings[0];
        Assert.Equal(0x11111111u, pA.DiscId1);
        Assert.Equal(0x22222222u, pA.DiscId2);
        Assert.Equal(0x33333333u, pA.FreeDbId);
        Assert.Equal(3, pA.Tracks.Count);
        Assert.Equal(10, pA.Tracks[0].Confidence);
        Assert.Equal(0x10000001u, pA.Tracks[0].Crc);
        Assert.Equal(0x20000001u, pA.Tracks[0].Frame450Crc);
        Assert.Equal(20, pA.Tracks[1].Confidence);
        Assert.Equal(0x10000002u, pA.Tracks[1].Crc);
        Assert.Equal(0x20000002u, pA.Tracks[1].Frame450Crc);
        Assert.Equal(30, pA.Tracks[2].Confidence);
        Assert.Equal(0x10000003u, pA.Tracks[2].Crc);
        Assert.Equal(0x20000003u, pA.Tracks[2].Frame450Crc);

        // Pressing B
        var pB = pressings[1];
        Assert.Equal(0x44444444u, pB.DiscId1);
        Assert.Equal(0x55555555u, pB.DiscId2);
        Assert.Equal(0x66666666u, pB.FreeDbId);
        Assert.Equal(3, pB.Tracks.Count);
        Assert.Equal(5, pB.Tracks[0].Confidence);
        Assert.Equal(0x30000001u, pB.Tracks[0].Crc);
        Assert.Equal(0x40000001u, pB.Tracks[0].Frame450Crc);
        Assert.Equal(15, pB.Tracks[1].Confidence);
        Assert.Equal(0x30000002u, pB.Tracks[1].Crc);
        Assert.Equal(0x40000002u, pB.Tracks[1].Frame450Crc);
        Assert.Equal(25, pB.Tracks[2].Confidence);
        Assert.Equal(0x30000003u, pB.Tracks[2].Crc);
        Assert.Equal(0x40000003u, pB.Tracks[2].Frame450Crc);
    }

    [Fact]
    public void Parse_TruncatedBlock_Throws()
    {
        byte[] dbar = BuildTwoPressingDbar(
            id1A: 0x11111111u, id2A: 0x22222222u, freeDbA: 0x33333333u,
            tracksA: [(10, 0x10000001u, 0x20000001u), (20, 0x10000002u, 0x20000002u), (30, 0x10000003u, 0x20000003u)],
            id1B: 0x44444444u, id2B: 0x55555555u, freeDbB: 0x66666666u,
            tracksB: [(5, 0x30000001u, 0x40000001u), (15, 0x30000002u, 0x40000002u), (25, 0x30000003u, 0x40000003u)]);

        // Dropping the last byte makes the block size smaller than expected -> InvalidDataException
        byte[] truncated = dbar[..^1];
        Assert.Throws<InvalidDataException>(() => AccurateRipDatabase.Parse(truncated));
    }

    [Fact]
    public void Match_SumsConfidenceAcrossPressings_AndDetectsV2()
    {
        const uint matchingV1 = 0xAAAA0001u;
        const uint matchingV2 = 0xBBBB0002u;

        var pressingA = new AccurateRipPressing(
            0x11111111u, 0x22222222u, 0x33333333u,
            new AccurateRipTrackEntry[]
            {
                new(Confidence: 10, Crc: 0x10000001u, Frame450Crc: 0),
                new(Confidence: 5, Crc: matchingV1, Frame450Crc: 0),
                new(Confidence: 20, Crc: 0x10000003u, Frame450Crc: 0)
            });

        var pressingB = new AccurateRipPressing(
            0x44444444u, 0x55555555u, 0x66666666u,
            new AccurateRipTrackEntry[]
            {
                new(Confidence: 15, Crc: 0x20000001u, Frame450Crc: 0),
                new(Confidence: 7, Crc: matchingV2, Frame450Crc: 0),
                new(Confidence: 25, Crc: 0x20000003u, Frame450Crc: 0)
            });

        var pressings = new[] { pressingA, pressingB };

        // Match track index 1: matches v1 in pressing A (conf 5) and v2 in pressing B (conf 7)
        var matchedVerdict = AccurateRipDatabase.Match(pressings, trackIndex: 1, v1: matchingV1, v2: matchingV2);

        Assert.True(matchedVerdict.IsAccurate);
        Assert.Equal(12, matchedVerdict.Confidence);
        Assert.Equal(12, matchedVerdict.TotalSubmissions); // 5 + 7
        Assert.True(matchedVerdict.MatchedV2);

        // Non-matching track (track index 0 with different CRCs): IsAccurate false, Confidence 0
        var nonMatchVerdict = AccurateRipDatabase.Match(pressings, trackIndex: 0, v1: 0x99999999u, v2: 0x88888888u);

        Assert.False(nonMatchVerdict.IsAccurate);
        Assert.Equal(0, nonMatchVerdict.Confidence);
        Assert.Equal(25, nonMatchVerdict.TotalSubmissions); // 10 + 15
        Assert.False(nonMatchVerdict.MatchedV2);
    }

    [Fact]
    public async Task Rip_ZeroOffset_TrackInfosMatchReferenceChecksums()
    {
        string ripFolder = Path.Combine(_fixture.TempDir, "rip_zero");
        var ripper = new AudioCdRipperService();
        var ripResult = await ripper.RipAudioCdAsync(_fixture.Recorder, _fixture.Engine, ripFolder);

        Assert.True(ripResult.Success, ripResult.ErrorMessage);
        Assert.Equal(3, ripResult.TracksRipped);
        Assert.Equal(3, ripResult.TrackInfos.Count);

        byte[][] burnedTracks = [_fixture.Track1Pcm, _fixture.Track2Pcm, _fixture.Track3Pcm];

        for (int i = 0; i < 3; i++)
        {
            var info = ripResult.TrackInfos[i];
            Assert.Equal(i, info.AudioTrackIndex);
            Assert.Equal(0, info.UnreadableSectors);

            byte[] wavBytes = await File.ReadAllBytesAsync(info.FilePath);
            Assert.True(wavBytes.Length >= 44, "Ripped WAV file must be at least 44 bytes (header size).");
            byte[] rippedPcm = wavBytes[44..];

            // Because each track was generated with an exact multiple of 2,352 bytes (one raw Red Book sector)
            // and no normalization was applied, no sector-boundary padding or 4-second minimum padding was added,
            // so the ripped WAV PCM is bit-for-bit identical to the generated input PCM.
            Assert.Equal(burnedTracks[i], rippedPcm);

            var (expectedV1, expectedV2) = ReferenceAccurateRip(rippedPcm, first: i == 0, last: i == 2);
            Assert.Equal(expectedV1, info.AccurateRipV1);
            Assert.Equal(expectedV2, info.AccurateRipV2);
        }
    }

    [Fact]
    public async Task Rip_PositiveOffset_ShiftsAudioLaterAndPadsEndWithSilence()
    {
        // Build the expected stream by concatenating the zero-offset rips of all tracks (the whole audio area)
        byte[] whole = await GetZeroOffsetWholeAudioPcmAsync();

        string ripFolder = Path.Combine(_fixture.TempDir, "rip_pos6");
        var ripper = new AudioCdRipperService();
        var options = new AudioRipOptions { ReadOffsetSamples = 6 };
        var ripResult = await ripper.RipAudioCdAsync(_fixture.Recorder, _fixture.Engine, ripFolder, options);

        Assert.True(ripResult.Success, ripResult.ErrorMessage);
        Assert.Equal(3, ripResult.TrackInfos.Count);

        int[] trackLengths = [_fixture.Track1Pcm.Length, _fixture.Track2Pcm.Length, _fixture.Track3Pcm.Length];
        int trackStartByte = 0;

        for (int i = 0; i < 3; i++)
        {
            int len = trackLengths[i];
            // for each track the expected PCM is whole[(trackStartByte + 24) .. (trackStartByte + 24 + trackLength)] with zeros beyond the end of whole
            byte[] expectedPcm = SliceWithZeroPadding(whole, trackStartByte + 24, len);

            byte[] wavBytes = await File.ReadAllBytesAsync(ripResult.TrackInfos[i].FilePath);
            byte[] rippedPcm = wavBytes[44..];

            Assert.Equal(expectedPcm, rippedPcm);
            trackStartByte += len;
        }
    }

    [Fact]
    public async Task Rip_NegativeOffset_PadsStartWithSilence()
    {
        // Build the expected stream by concatenating the zero-offset rips of all tracks (the whole audio area)
        byte[] whole = await GetZeroOffsetWholeAudioPcmAsync();

        string ripFolder = Path.Combine(_fixture.TempDir, "rip_neg6");
        var ripper = new AudioCdRipperService();
        var options = new AudioRipOptions { ReadOffsetSamples = -6 };
        var ripResult = await ripper.RipAudioCdAsync(_fixture.Recorder, _fixture.Engine, ripFolder, options);

        Assert.True(ripResult.Success, ripResult.ErrorMessage);
        Assert.Equal(3, ripResult.TrackInfos.Count);

        int[] trackLengths = [_fixture.Track1Pcm.Length, _fixture.Track2Pcm.Length, _fixture.Track3Pcm.Length];
        int trackStartByte = 0;

        for (int i = 0; i < 3; i++)
        {
            int len = trackLengths[i];
            byte[] expectedPcm;
            if (i == 0)
            {
                // the first 24 bytes of track 1 are zero, the rest equals whole[0 .. len-24]
                expectedPcm = new byte[len];
                Array.Copy(whole, 0, expectedPcm, 24, len - 24);
            }
            else
            {
                // other tracks equal whole[start-24 .. start-24+len]
                expectedPcm = whole[(trackStartByte - 24)..(trackStartByte - 24 + len)];
            }

            byte[] wavBytes = await File.ReadAllBytesAsync(ripResult.TrackInfos[i].FilePath);
            byte[] rippedPcm = wavBytes[44..];

            Assert.Equal(expectedPcm, rippedPcm);
            trackStartByte += len;
        }
    }

    [Fact]
    public async Task Rip_OffsetLargerThanASector_IsHandled()
    {
        // Build the expected stream by concatenating the zero-offset rips of all tracks (the whole audio area)
        byte[] whole = await GetZeroOffsetWholeAudioPcmAsync();

        string ripFolder = Path.Combine(_fixture.TempDir, "rip_pos700");
        var ripper = new AudioCdRipperService();
        var options = new AudioRipOptions { ReadOffsetSamples = 700 };
        var ripResult = await ripper.RipAudioCdAsync(_fixture.Recorder, _fixture.Engine, ripFolder, options);

        Assert.True(ripResult.Success, ripResult.ErrorMessage);
        Assert.Equal(3, ripResult.TrackInfos.Count);

        const int offsetBytes = 700 * 4; // 2,800 bytes (> 2,352 bytes / 1 sector)
        int[] trackLengths = [_fixture.Track1Pcm.Length, _fixture.Track2Pcm.Length, _fixture.Track3Pcm.Length];
        int trackStartByte = 0;

        for (int i = 0; i < 3; i++)
        {
            int len = trackLengths[i];
            byte[] expectedPcm = SliceWithZeroPadding(whole, trackStartByte + offsetBytes, len);

            byte[] wavBytes = await File.ReadAllBytesAsync(ripResult.TrackInfos[i].FilePath);
            byte[] rippedPcm = wavBytes[44..];

            Assert.Equal(expectedPcm, rippedPcm);
            trackStartByte += len;
        }
    }
}
