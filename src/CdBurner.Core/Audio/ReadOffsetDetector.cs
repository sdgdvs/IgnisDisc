using System.Buffers.Binary;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Images;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Audio;

/// <summary>A read offset at which a track's AccurateRip v1 checksum matched the database.</summary>
/// <param name="OffsetSamples">Offset to pass as <see cref="AudioRipOptions.ReadOffsetSamples"/>.</param>
/// <param name="Confidence">Sum of the confidences of the matching database entries.</param>
/// <param name="TrackNumber">Track that matched.</param>
public sealed record ReadOffsetCandidate(int OffsetSamples, int Confidence, int TrackNumber);

/// <summary>Outcome of <see cref="ReadOffsetDetector.DetectAsync"/>.</summary>
/// <param name="Candidates">Matching offsets across the probed tracks, best first. Empty if nothing matched.</param>
/// <param name="ProbedTracks">Track numbers that were read and checked.</param>
/// <param name="ErrorMessage">Set when detection could not run (too few tracks, read error).</param>
public sealed record ReadOffsetDetectionResult(
    IReadOnlyList<ReadOffsetCandidate> Candidates,
    IReadOnlyList<int> ProbedTracks,
    string? ErrorMessage = null)
{
    /// <summary>The most likely offset: the candidate confirmed by the most tracks, then the highest confidence.</summary>
    public ReadOffsetCandidate? Best => Candidates.Count == 0
        ? null
        : Candidates
            .GroupBy(c => c.OffsetSamples)
            .OrderByDescending(g => g.Select(c => c.TrackNumber).Distinct().Count())
            .ThenByDescending(g => g.Sum(c => c.Confidence))
            .Select(g => new ReadOffsetCandidate(g.Key, g.Sum(c => c.Confidence), g.First().TrackNumber))
            .First();
}

/// <summary>
/// Finds a drive's read offset by sliding a middle track's AccurateRip v1 window across a range of offsets and
/// comparing each checksum with the AccurateRip database.
/// <para>
/// v1 is linear in the samples, so with S(o) the sum of the window starting at offset o and N its length,
/// CRC(o+1) = CRC(o) - S(o) + N * s[o+N] (all mod 2^32). One pass over the track covers every offset. Middle tracks
/// are used because the first and last tracks skip edge samples, which breaks this recurrence.
/// </para>
/// </summary>
public static class ReadOffsetDetector
{
    public const int DefaultMaxOffsetSamples = 3000;
    private const int MaxProbeTrackSectors = 45_000; // 10 minutes of audio
    private const int SamplesPerSector = OpticalConstants.RawSectorSize / 4;

    /// <summary>
    /// Scans offsets -<paramref name="marginSamples"/>..+<paramref name="marginSamples"/>. <paramref name="paddedPcm"/>
    /// holds the track's PCM with <paramref name="marginSamples"/> extra samples before and after it.
    /// </summary>
    public static IReadOnlyList<ReadOffsetCandidate> FindOffsets(
        ReadOnlySpan<byte> paddedPcm,
        int marginSamples,
        int trackSamples,
        IReadOnlyList<AccurateRipPressing> pressings,
        int trackIndex,
        int trackNumber)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(marginSamples);
        ArgumentOutOfRangeException.ThrowIfLessThan(trackSamples, 1);
        if (paddedPcm.Length < (long)(trackSamples + 2 * marginSamples) * 4)
        {
            throw new ArgumentException("Padded PCM is shorter than the track plus both margins.", nameof(paddedPcm));
        }

        // Database CRCs for this track, with their confidences (v1 and v2 entries alike; only v1 can match here).
        var wanted = new Dictionary<uint, int>();
        foreach (var pressing in pressings)
        {
            if (trackIndex < pressing.Tracks.Count)
            {
                var e = pressing.Tracks[trackIndex];
                if (e.Crc != 0)
                {
                    wanted[e.Crc] = wanted.GetValueOrDefault(e.Crc) + e.Confidence;
                }
            }
        }

        var results = new List<ReadOffsetCandidate>();
        if (wanted.Count == 0)
        {
            return results;
        }

        static uint Sample(ReadOnlySpan<byte> pcm, int index) => BinaryPrimitives.ReadUInt32LittleEndian(pcm.Slice(index * 4, 4));

        // Window for offset -margin starts at sample index 0 of the padded buffer.
        uint crc = 0;
        uint sum = 0;
        for (int k = 0; k < trackSamples; k++)
        {
            uint s = Sample(paddedPcm, k);
            crc += s * (uint)(k + 1);
            sum += s;
        }

        uint n = (uint)trackSamples;
        for (int o = -marginSamples; ; o++)
        {
            if (wanted.TryGetValue(crc, out int confidence))
            {
                results.Add(new ReadOffsetCandidate(o, confidence, trackNumber));
            }
            if (o == marginSamples)
            {
                break;
            }

            int start = o + marginSamples;           // window start index in the padded buffer
            uint incoming = Sample(paddedPcm, start + trackSamples);
            crc = crc - sum + n * incoming;
            sum = sum - Sample(paddedPcm, start) + incoming;
        }

        return results;
    }

    /// <summary>
    /// Reads up to <paramref name="tracksToProbe"/> middle tracks with a margin of
    /// <paramref name="maxOffsetSamples"/> samples on each side and looks for offsets whose v1 checksum is in the
    /// database. Requires at least three audio tracks.
    /// </summary>
    public static async Task<ReadOffsetDetectionResult> DetectAsync(
        IBurnEngineBackend engine,
        IDiscRecorderBackend recorder,
        IReadOnlyList<AudioDiscoveredTrack> audioTracks,
        IReadOnlyList<AccurateRipPressing> pressings,
        int maxOffsetSamples = DefaultMaxOffsetSamples,
        int tracksToProbe = 2,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(audioTracks);
        ArgumentNullException.ThrowIfNull(pressings);

        var ordered = audioTracks.OrderBy(t => t.StartLba).ToList();
        if (ordered.Count < 3)
        {
            return new ReadOffsetDetectionResult(Array.Empty<ReadOffsetCandidate>(), Array.Empty<int>(),
                "Offset detection needs a disc with at least three audio tracks.");
        }

        int marginSectors = (maxOffsetSamples + SamplesPerSector - 1) / SamplesPerSector;
        var candidates = new List<ReadOffsetCandidate>();
        var probed = new List<int>();

        // Prefer the longest middle tracks up to 10 minutes: more samples make an accidental match less likely, but each
        // probed track is held in memory (10 minutes = ~106 MB) and very long tracks would overflow 32-bit offsets.
        var interior = Enumerable.Range(1, ordered.Count - 2).ToList();
        var middle = interior.Where(i => ordered[i].SectorCount <= MaxProbeTrackSectors).OrderByDescending(i => ordered[i].SectorCount)
            .Concat(interior.Where(i => ordered[i].SectorCount > MaxProbeTrackSectors).OrderBy(i => ordered[i].SectorCount))
            .Where(i => ordered[i].SectorCount <= MaxProbeTrackSectors * 3)
            .Take(Math.Max(1, tracksToProbe))
            .ToList();
        if (middle.Count == 0)
        {
            return new ReadOffsetDetectionResult(Array.Empty<ReadOffsetCandidate>(), Array.Empty<int>(),
                "All middle tracks are longer than 30 minutes; use a disc with shorter tracks for offset detection.");
        }

        foreach (int index in middle)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var track = ordered[index];
            int firstSector = track.StartLba - marginSectors;
            int sectorCount = track.SectorCount + 2 * marginSectors;
            var buffer = new byte[(long)sectorCount * OpticalConstants.RawSectorSize];
            int written = 0;

            var outcome = await BatchedSectorReader.ReadRangeAsync(
                engine,
                recorder,
                firstSector,
                sectorCount,
                raw2352Mode: true,
                maxRetriesPerSector: 3,
                onSector: (_, data) =>
                {
                    data.Span.CopyTo(buffer.AsSpan(written));
                    written += data.Length;
                    return ValueTask.CompletedTask;
                },
                onUnreadable: _ => ValueTask.FromResult(false),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!outcome.Completed)
            {
                return new ReadOffsetDetectionResult(candidates, probed,
                    $"Unreadable sector at LBA {outcome.StoppedAtLba} while reading track {track.TrackNumber}.");
            }

            probed.Add(track.TrackNumber);

            // The buffer holds marginSectors * 588 samples before the track; scan only maxOffsetSamples of them.
            int skipSamples = marginSectors * SamplesPerSector - maxOffsetSamples;
            var padded = buffer.AsSpan(skipSamples * 4);
            candidates.AddRange(FindOffsets(
                padded,
                maxOffsetSamples,
                track.SectorCount * SamplesPerSector,
                pressings,
                index,
                track.TrackNumber));
        }

        return new ReadOffsetDetectionResult(
            candidates.OrderByDescending(c => c.Confidence).ToList(),
            probed);
    }
}
