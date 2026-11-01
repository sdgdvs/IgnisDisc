using System.Security.Cryptography;
using System.Text;
using CdBurner.Core.Abstractions;

namespace CdBurner.Core.Audio;

/// <summary>
/// Online-database identifiers for an audio CD, all derived from its Table of Contents.
/// </summary>
/// <param name="MusicBrainzId">28-character MusicBrainz disc ID (e.g. "arIS30RPWowvwNEqsqdDnZzDGhk-").</param>
/// <param name="FreeDbId">32-bit freedb/CDDB/GnuDB disc ID.</param>
/// <param name="AccurateRipId1">AccurateRip disc ID 1 (sum of audio track offsets plus lead-out).</param>
/// <param name="AccurateRipId2">AccurateRip disc ID 2 (track-number-weighted sum of offsets).</param>
/// <param name="AudioTrackCount">Number of audio tracks covered by the IDs.</param>
public sealed record DiscIdentifiers(
    string MusicBrainzId,
    uint FreeDbId,
    uint AccurateRipId1,
    uint AccurateRipId2,
    int AudioTrackCount)
{
    /// <summary>freedb ID in its conventional 8-digit lowercase hex form.</summary>
    public string FreeDbIdHex => FreeDbId.ToString("x8");

    /// <summary>
    /// AccurateRip database path for this disc, e.g.
    /// <c>accuraterip/a/b/c/dBAR-012-0012abc3-00f1e2d3-a40b2c0c.bin</c>.
    /// </summary>
    public string AccurateRipDatabasePath =>
        $"accuraterip/{AccurateRipId1 & 0xF:x}/{(AccurateRipId1 >> 4) & 0xF:x}/{(AccurateRipId1 >> 8) & 0xF:x}/" +
        $"dBAR-{AudioTrackCount:D3}-{AccurateRipId1:x8}-{AccurateRipId2:x8}-{FreeDbId:x8}.bin";
}

/// <summary>
/// Computes MusicBrainz, freedb and AccurateRip disc IDs from a CD Table of Contents.
/// <para>
/// All three algorithms work on track start addresses. LBAs here are 0-based (the first track normally starts at
/// LBA 0); MusicBrainz and freedb use absolute frame addresses, which add the 150-frame (2 second) lead-in pre-gap.
/// </para>
/// <para>
/// For Enhanced CDs (audio session followed by a data session) the TOC reader already shortens the last audio track
/// by the 11,400-sector session gap, so "end of the last audio track" is the audio lead-out used by MusicBrainz and
/// AccurateRip, while freedb counts every track and uses the true disc lead-out.
/// </para>
/// </summary>
public static class DiscIdCalculator
{
    private const int LeadInFrames = 150;
    private const int FramesPerSecond = 75;

    /// <summary>
    /// Disc IDs for the disc in a recorder: from the physical TOC when the drive provides one (it includes data
    /// tracks, which freedb counts), otherwise from the discovered audio tracks. Null if there are no audio tracks.
    /// </summary>
    public static DiscIdentifiers? CalculateForRecorder(IDiscRecorderBackend recorder)
    {
        ArgumentNullException.ThrowIfNull(recorder);

        IReadOnlyList<DiscTocTrack> toc = recorder is IDiscTocProvider tocProvider
            ? tocProvider.ReadTableOfContents()
            : Array.Empty<DiscTocTrack>();

        if (toc.Count == 0)
        {
            toc = AudioCdRipperService.QueryTracksAndCdText(recorder).Tracks
                .Select(t => new DiscTocTrack(t.TrackNumber, t.StartLba, t.SectorCount, true))
                .ToList();
        }

        return toc.Count > 0 ? Calculate(toc) : null;
    }

    /// <summary>
    /// Computes the disc identifiers, or returns null if the TOC contains no audio tracks.
    /// </summary>
    public static DiscIdentifiers? Calculate(IReadOnlyList<DiscTocTrack> toc)
    {
        ArgumentNullException.ThrowIfNull(toc);

        var allTracks = toc.OrderBy(t => t.TrackNumber).ToList();
        var audioTracks = allTracks.Where(t => t.IsAudio).ToList();
        if (audioTracks.Count == 0)
        {
            return null;
        }

        var lastAudio = audioTracks[^1];
        int audioLeadOutLba = lastAudio.StartLba + lastAudio.SectorCount;
        var lastAny = allTracks[^1];
        int discLeadOutLba = lastAny.StartLba + lastAny.SectorCount;

        string musicBrainzId = CalculateMusicBrainzId(
            audioTracks[0].TrackNumber, lastAudio.TrackNumber, audioLeadOutLba, audioTracks);

        uint freeDbId = CalculateFreeDbId(allTracks, discLeadOutLba);

        // AccurateRip: audio tracks only, numbered 1..n in disc order.
        uint arId1 = 0;
        uint arId2 = 0;
        uint position = 0;
        foreach (var track in audioTracks)
        {
            uint offset = (uint)track.StartLba;
            arId1 += offset;
            arId2 += Math.Max(offset, 1u) * ++position;
        }
        arId1 += (uint)audioLeadOutLba;
        arId2 += Math.Max((uint)audioLeadOutLba, 1u) * ++position;

        return new DiscIdentifiers(musicBrainzId, freeDbId, arId1, arId2, audioTracks.Count);
    }

    /// <summary>
    /// MusicBrainz disc ID: SHA-1 over the uppercase-hex string of first track (2 digits), last track (2 digits),
    /// lead-out frame offset (8 digits) and the frame offsets of tracks 1..99 (8 digits each, 0 for absent tracks),
    /// then base64 with '+', '/', '=' replaced by '.', '_', '-'.
    /// </summary>
    internal static string CalculateMusicBrainzId(int firstTrack, int lastTrack, int leadOutLba, IReadOnlyList<DiscTocTrack> audioTracks)
    {
        var sb = new StringBuilder(4 + 8 * 100);
        sb.Append(firstTrack.ToString("X2"));
        sb.Append(lastTrack.ToString("X2"));
        sb.Append((leadOutLba + LeadInFrames).ToString("X8"));

        var offsets = new int[100];
        foreach (var track in audioTracks)
        {
            if (track.TrackNumber is >= 1 and <= 99)
            {
                offsets[track.TrackNumber] = track.StartLba + LeadInFrames;
            }
        }
        for (int i = 1; i <= 99; i++)
        {
            sb.Append(offsets[i].ToString("X8"));
        }

        byte[] digest = SHA1.HashData(Encoding.ASCII.GetBytes(sb.ToString()));
        return Convert.ToBase64String(digest).Replace('+', '.').Replace('/', '_').Replace('=', '-');
    }

    /// <summary>
    /// freedb/CDDB disc ID: (sum of the decimal digit sums of each track's start second, mod 255) &lt;&lt; 24
    /// | (disc length in whole seconds) &lt;&lt; 8 | track count. Seconds are absolute (LBA + 150) / 75.
    /// </summary>
    internal static uint CalculateFreeDbId(IReadOnlyList<DiscTocTrack> tracks, int leadOutLba)
    {
        int checksum = 0;
        foreach (var track in tracks)
        {
            checksum += DigitSum((track.StartLba + LeadInFrames) / FramesPerSecond);
        }

        int discSeconds = (leadOutLba + LeadInFrames) / FramesPerSecond
                        - (tracks[0].StartLba + LeadInFrames) / FramesPerSecond;

        return ((uint)(checksum % 0xFF) << 24) | ((uint)discSeconds << 8) | (uint)tracks.Count;
    }

    private static int DigitSum(int value)
    {
        int sum = 0;
        while (value > 0)
        {
            sum += value % 10;
            value /= 10;
        }
        return sum;
    }
}
