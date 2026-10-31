using System.Buffers.Binary;
using System.Net;

namespace CdBurner.Core.Audio;

/// <summary>
/// Incremental AccurateRip v1 and v2 checksum of one audio track (16-bit stereo PCM, little-endian).
/// <para>
/// Each stereo sample is read as a little-endian 32-bit word and weighted by its 1-based position in the track.
/// v1 sums the low 32 bits of each product; v2 sums both halves of the 64-bit product. The first track skips its
/// first 2939 samples (five frames less one sample) and the last track skips its final 2940 samples, because drives
/// with different read offsets cannot agree on audio at the very edges of the disc.
/// </para>
/// </summary>
public sealed class AccurateRipChecksum
{
    private const uint EdgeSamples = 588 * 5;

    private readonly uint _checkStart;
    private readonly uint _checkEnd;
    private uint _position = 1;
    private int _pendingCount;
    private readonly byte[] _pending = new byte[4];

    public AccurateRipChecksum(long totalSamples, bool isFirstTrack, bool isLastTrack)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalSamples);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(totalSamples, uint.MaxValue);

        _checkStart = isFirstTrack ? EdgeSamples : 0;
        _checkEnd = isLastTrack ? (uint)Math.Max(0, totalSamples - EdgeSamples) : (uint)totalSamples;
    }

    /// <summary>AccurateRip v1 checksum of the samples appended so far.</summary>
    public uint V1 { get; private set; }

    /// <summary>AccurateRip v2 checksum of the samples appended so far.</summary>
    public uint V2 { get; private set; }

    /// <summary>Appends PCM bytes. Chunks need not be sample-aligned.</summary>
    public void Append(ReadOnlySpan<byte> pcm)
    {
        if (_pendingCount > 0)
        {
            int take = Math.Min(4 - _pendingCount, pcm.Length);
            pcm[..take].CopyTo(_pending.AsSpan(_pendingCount));
            _pendingCount += take;
            pcm = pcm[take..];
            if (_pendingCount < 4)
            {
                return;
            }
            AddSample(BinaryPrimitives.ReadUInt32LittleEndian(_pending));
            _pendingCount = 0;
        }

        int whole = pcm.Length & ~3;
        for (int i = 0; i < whole; i += 4)
        {
            AddSample(BinaryPrimitives.ReadUInt32LittleEndian(pcm.Slice(i, 4)));
        }

        if (whole < pcm.Length)
        {
            pcm[whole..].CopyTo(_pending);
            _pendingCount = pcm.Length - whole;
        }
    }

    private void AddSample(uint sample)
    {
        if (_position >= _checkStart && _position <= _checkEnd)
        {
            ulong product = (ulong)sample * _position;
            V1 += (uint)product;
            V2 += (uint)product + (uint)(product >> 32);
        }
        _position++;
    }
}

/// <summary>One track entry of one pressing in an AccurateRip dBAR file.</summary>
/// <param name="Confidence">Number of submissions that agreed on this CRC.</param>
/// <param name="Crc">AccurateRip v1 or v2 CRC of the track.</param>
/// <param name="Frame450Crc">CRC of the track's 450th frame, used for drive offset detection.</param>
public sealed record AccurateRipTrackEntry(int Confidence, uint Crc, uint Frame450Crc);

/// <summary>One pressing (block) of an AccurateRip dBAR file.</summary>
public sealed record AccurateRipPressing(uint DiscId1, uint DiscId2, uint FreeDbId, IReadOnlyList<AccurateRipTrackEntry> Tracks);

/// <summary>Result of checking one ripped track against the AccurateRip database.</summary>
/// <param name="IsAccurate">True if v1 or v2 matched at least one submission.</param>
/// <param name="Confidence">Sum of the confidences of all entries whose CRC matched.</param>
/// <param name="TotalSubmissions">Sum of the confidences of all entries for this track, matching or not.</param>
/// <param name="MatchedV2">True if the match was on the v2 CRC.</param>
public sealed record AccurateRipTrackVerdict(bool IsAccurate, int Confidence, int TotalSubmissions, bool MatchedV2);

/// <summary>
/// Parses, fetches and matches AccurateRip dBAR database files.
/// <para>
/// A dBAR file is a sequence of pressings. Each starts with a 13-byte header (track count, disc ID 1, disc ID 2,
/// freedb ID; little-endian) followed by 9 bytes per track (confidence, CRC, frame-450 CRC).
/// </para>
/// </summary>
public static class AccurateRipDatabase
{
    /// <summary>The AccurateRip server only serves plain HTTP.</summary>
    public const string DefaultBaseUrl = "http://www.accuraterip.com/";

    private const int HeaderSize = 13;
    private const int TrackEntrySize = 9;

    public static IReadOnlyList<AccurateRipPressing> Parse(ReadOnlySpan<byte> dbar)
    {
        var pressings = new List<AccurateRipPressing>();
        int offset = 0;

        while (dbar.Length - offset >= HeaderSize)
        {
            int trackCount = dbar[offset];
            int blockSize = HeaderSize + trackCount * TrackEntrySize;
            if (trackCount == 0 || dbar.Length - offset < blockSize)
            {
                throw new InvalidDataException($"Truncated or malformed AccurateRip block at byte {offset}.");
            }

            uint id1 = BinaryPrimitives.ReadUInt32LittleEndian(dbar.Slice(offset + 1, 4));
            uint id2 = BinaryPrimitives.ReadUInt32LittleEndian(dbar.Slice(offset + 5, 4));
            uint freeDb = BinaryPrimitives.ReadUInt32LittleEndian(dbar.Slice(offset + 9, 4));

            var tracks = new AccurateRipTrackEntry[trackCount];
            for (int t = 0; t < trackCount; t++)
            {
                int e = offset + HeaderSize + t * TrackEntrySize;
                tracks[t] = new AccurateRipTrackEntry(
                    dbar[e],
                    BinaryPrimitives.ReadUInt32LittleEndian(dbar.Slice(e + 1, 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(dbar.Slice(e + 5, 4)));
            }

            pressings.Add(new AccurateRipPressing(id1, id2, freeDb, tracks));
            offset += blockSize;
        }

        return pressings;
    }

    /// <summary>
    /// Matches a track's v1/v2 CRCs against every pressing. <paramref name="trackIndex"/> is the 0-based position of
    /// the track among the disc's audio tracks.
    /// </summary>
    public static AccurateRipTrackVerdict Match(IReadOnlyList<AccurateRipPressing> pressings, int trackIndex, uint v1, uint v2)
    {
        int confidence = 0;
        int total = 0;
        bool matchedV2 = false;

        foreach (var pressing in pressings)
        {
            if (trackIndex < 0 || trackIndex >= pressing.Tracks.Count)
            {
                continue;
            }

            var entry = pressing.Tracks[trackIndex];
            total += entry.Confidence;
            if (entry.Crc == v2 && v2 != 0)
            {
                confidence += entry.Confidence;
                matchedV2 = true;
            }
            else if (entry.Crc == v1 && v1 != 0)
            {
                confidence += entry.Confidence;
            }
        }

        return new AccurateRipTrackVerdict(confidence > 0, confidence, total, matchedV2);
    }

    /// <summary>
    /// Downloads and parses the dBAR file for a disc. Returns an empty list if the disc is not in the database.
    /// </summary>
    public static async Task<IReadOnlyList<AccurateRipPressing>> FetchAsync(
        HttpClient httpClient,
        DiscIdentifiers ids,
        string baseUrl = DefaultBaseUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(ids);

        var uri = new Uri(baseUrl.TrimEnd('/') + "/" + ids.AccurateRipDatabasePath);
        using var response = await httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return Array.Empty<AccurateRipPressing>();
        }

        response.EnsureSuccessStatusCode();
        byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return Parse(body);
    }
}
