using System.Text;

namespace CdBurner.Core.Audio;

/// <summary>
/// Parses Red Book Annex J / IEC 60908 CD-Text binary packs into structured album and track metadata.
/// </summary>
public static class CdTextPackParser
{
    /// <summary>
    /// Parses raw CD-Text pack bytes into CdTextAlbumMetadata.
    /// Verifies 18-byte pack boundaries and CRC-16 checksums.
    /// </summary>
    public static CdTextAlbumMetadata ParsePacks(ReadOnlySpan<byte> rawPackBytes)
    {
        if (rawPackBytes.Length == 0)
        {
            throw new ArgumentException("CD-Text pack data cannot be empty.", nameof(rawPackBytes));
        }

        // Support optional 4-byte header (e.g. from cdtext.dat files)
        if (rawPackBytes.Length % CdTextPack.PackSizeBytes != 0)
        {
            if (rawPackBytes.Length >= 22 && (rawPackBytes.Length - 4) % CdTextPack.PackSizeBytes == 0)
            {
                rawPackBytes = rawPackBytes.Slice(4);
            }
            else
            {
                throw new ArgumentException(
                    $"CD-Text byte length ({rawPackBytes.Length}) must be an exact multiple of {CdTextPack.PackSizeBytes} bytes.",
                    nameof(rawPackBytes));
            }
        }

        int packCount = rawPackBytes.Length / CdTextPack.PackSizeBytes;
        var packs = new List<CdTextPack>(packCount);

        for (int i = 0; i < packCount; i++)
        {
            var packSpan = rawPackBytes.Slice(i * CdTextPack.PackSizeBytes, CdTextPack.PackSizeBytes);
            if (!CdTextCrc16.VerifyPack(packSpan))
            {
                throw new InvalidDataException($"CD-Text CRC verification failed at pack index {i}.");
            }
            packs.Add(new CdTextPack(packSpan));
        }

        // Find SizeInfo packs
        int firstTrack = 1;
        int lastTrack = 0;
        bool hasSizeInfo = false;

        foreach (var p in packs)
        {
            if (p.PackType == CdTextPackType.SizeInfo && p.TrackNumberIndicator == 0)
            {
                hasSizeInfo = true;
                firstTrack = p.TextData[1];
                lastTrack = p.TextData[2];
                break;
            }
        }

        // Group packs by pack type (excluding SizeInfo)
        var packsByType = packs
            .Where(p => p.PackType != CdTextPackType.SizeInfo)
            .GroupBy(p => p.PackType)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.SequenceNumber).ToList());

        // If SizeInfo is missing or lastTrack == 0, infer lastTrack from Title packs
        if (!hasSizeInfo || lastTrack == 0)
        {
            if (packsByType.TryGetValue(CdTextPackType.Title, out var titlePacks))
            {
                var titleStrings = ExtractStrings(titlePacks, -1);
                lastTrack = Math.Max(0, titleStrings.Count - 1);
            }
        }

        int totalTracks = Math.Max(0, lastTrack - firstTrack + 1);
        int expectedStringsPerType = totalTracks + 1; // Track 0 (album) + Tracks 1..N

        var extractedStringsByType = new Dictionary<CdTextPackType, List<string>>();
        foreach (var (type, packList) in packsByType)
        {
            extractedStringsByType[type] = ExtractStrings(packList, expectedStringsPerType);
        }

        string GetString(CdTextPackType type, int trackIndex)
        {
            if (extractedStringsByType.TryGetValue(type, out var list) && trackIndex < list.Count)
            {
                return list[trackIndex];
            }
            return string.Empty;
        }

        string albumTitle = GetString(CdTextPackType.Title, 0);
        string albumPerformer = GetString(CdTextPackType.Performer, 0);
        string albumSongwriter = GetString(CdTextPackType.Songwriter, 0);
        string albumComposer = GetString(CdTextPackType.Composer, 0);
        string albumArranger = GetString(CdTextPackType.Arranger, 0);
        string albumMessage = GetString(CdTextPackType.Message, 0);
        string albumDiscId = GetString(CdTextPackType.DiscId, 0);
        string albumUpcEan = GetString(CdTextPackType.UpcEanIsrc, 0);

        var tracksList = new List<CdTextTrackMetadata>(totalTracks);
        for (int i = 1; i <= totalTracks; i++)
        {
            int trackNumber = firstTrack + i - 1;
            string trackTitle = GetString(CdTextPackType.Title, i);
            string trackPerformer = GetString(CdTextPackType.Performer, i);
            string trackSongwriter = GetString(CdTextPackType.Songwriter, i);
            string trackComposer = GetString(CdTextPackType.Composer, i);
            string trackArranger = GetString(CdTextPackType.Arranger, i);
            string trackMessage = GetString(CdTextPackType.Message, i);
            string trackIsrc = GetString(CdTextPackType.UpcEanIsrc, i);

            tracksList.Add(new CdTextTrackMetadata(
                TrackNumber: trackNumber,
                Title: trackTitle,
                Performer: trackPerformer,
                Songwriter: trackSongwriter,
                Composer: trackComposer,
                Arranger: trackArranger,
                Message: trackMessage,
                Isrc: trackIsrc));
        }

        return new CdTextAlbumMetadata(
            AlbumTitle: albumTitle,
            AlbumPerformer: albumPerformer,
            Songwriter: albumSongwriter,
            Composer: albumComposer,
            Arranger: albumArranger,
            Message: albumMessage,
            DiscId: albumDiscId,
            UpcEan: albumUpcEan,
            Tracks: tracksList);
    }

    private static List<string> ExtractStrings(List<CdTextPack> packList, int expectedCount)
    {
        var byteList = new List<byte>(packList.Count * CdTextPack.PayloadSizeBytes);
        foreach (var pack in packList)
        {
            byteList.AddRange(pack.TextData);
        }

        var result = new List<string>();
        int start = 0;

        for (int i = 0; i < byteList.Count; i++)
        {
            if (byteList[i] == 0x00)
            {
                int len = i - start;
                string s = len > 0 ? Encoding.Latin1.GetString(byteList.GetRange(start, len).ToArray()) : string.Empty;
                result.Add(s);
                start = i + 1;

                if (expectedCount > 0 && result.Count == expectedCount)
                {
                    break;
                }
            }
        }

        if (expectedCount > 0)
        {
            while (result.Count < expectedCount)
            {
                result.Add(string.Empty);
            }
        }

        return result;
    }
}
