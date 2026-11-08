using System.Text;

namespace CdBurner.Core.Audio;

/// <summary>
/// Encodes CD-Text metadata into Red Book Annex J / IEC 60908 binary packs (18 bytes per pack).
/// </summary>
public static class CdTextPackBuilder
{
    /// <summary>
    /// Builds raw CD-Text 18-byte binary packs for the specified album metadata.
    /// </summary>
    public static byte[] BuildPacks(CdTextAlbumMetadata metadata)
    {
        var packList = BuildPackList(metadata);
        byte[] buffer = new byte[packList.Count * CdTextPack.PackSizeBytes];
        for (int i = 0; i < packList.Count; i++)
        {
            packList[i].WriteTo(buffer.AsSpan(i * CdTextPack.PackSizeBytes, CdTextPack.PackSizeBytes));
        }
        return buffer;
    }

    /// <summary>
    /// Builds the list of CdTextPack structures for the specified album metadata.
    /// </summary>
    public static List<CdTextPack> BuildPackList(CdTextAlbumMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var tracks = metadata.Tracks ?? Array.Empty<CdTextTrackMetadata>();
        int trackCount = tracks.Count;

        var packs = new List<CdTextPack>();
        byte sequenceNumber = 0;
        var packCounts = new Dictionary<CdTextPackType, int>();

        // Text pack types in canonical order (0x80 .. 0x8E)
        var textPackTypes = new[]
        {
            CdTextPackType.Title,      // 0x80
            CdTextPackType.Performer,  // 0x81
            CdTextPackType.Songwriter, // 0x82
            CdTextPackType.Composer,   // 0x83
            CdTextPackType.Arranger,   // 0x84
            CdTextPackType.Message,    // 0x85
            CdTextPackType.DiscId,     // 0x86
            CdTextPackType.Genre,      // 0x87
            CdTextPackType.UpcEanIsrc  // 0x8E
        };

        foreach (var packType in textPackTypes)
        {
            string[] strings = GetStringsForPackType(metadata, tracks, packType);
            bool isPopulated = strings.Any(s => !string.IsNullOrEmpty(s));

            // Per Red Book specification: Title (0x80) is always emitted
            if (!isPopulated && packType != CdTextPackType.Title)
            {
                continue;
            }

            int countBefore = packs.Count;
            EncodeTextPacks(packType, strings, packs, ref sequenceNumber);
            packCounts[packType] = packs.Count - countBefore;
        }

        // Append 3 Size Information packs (0x8F)
        AppendSizeInfoPacks(packs, ref sequenceNumber, trackCount, packCounts);

        return packs;
    }

    private static string[] GetStringsForPackType(
        CdTextAlbumMetadata metadata,
        IReadOnlyList<CdTextTrackMetadata> tracks,
        CdTextPackType packType)
    {
        int count = tracks.Count + 1;
        var result = new string[count];

        switch (packType)
        {
            case CdTextPackType.Title:
                result[0] = metadata.AlbumTitle ?? string.Empty;
                for (int i = 0; i < tracks.Count; i++)
                    result[i + 1] = tracks[i].Title ?? string.Empty;
                break;

            case CdTextPackType.Performer:
                result[0] = metadata.AlbumPerformer ?? string.Empty;
                for (int i = 0; i < tracks.Count; i++)
                    result[i + 1] = tracks[i].Performer ?? string.Empty;
                break;

            case CdTextPackType.Songwriter:
                result[0] = metadata.Songwriter ?? string.Empty;
                for (int i = 0; i < tracks.Count; i++)
                    result[i + 1] = tracks[i].Songwriter ?? string.Empty;
                break;

            case CdTextPackType.Composer:
                result[0] = metadata.Composer ?? string.Empty;
                for (int i = 0; i < tracks.Count; i++)
                    result[i + 1] = tracks[i].Composer ?? string.Empty;
                break;

            case CdTextPackType.Arranger:
                result[0] = metadata.Arranger ?? string.Empty;
                for (int i = 0; i < tracks.Count; i++)
                    result[i + 1] = tracks[i].Arranger ?? string.Empty;
                break;

            case CdTextPackType.Message:
                result[0] = metadata.Message ?? string.Empty;
                for (int i = 0; i < tracks.Count; i++)
                    result[i + 1] = tracks[i].Message ?? string.Empty;
                break;

            case CdTextPackType.DiscId:
                result[0] = metadata.DiscId ?? string.Empty;
                for (int i = 0; i < tracks.Count; i++)
                    result[i + 1] = string.Empty;
                break;

            case CdTextPackType.Genre:
                for (int i = 0; i < count; i++)
                    result[i] = string.Empty;
                break;

            case CdTextPackType.UpcEanIsrc:
                result[0] = metadata.UpcEan ?? string.Empty;
                for (int i = 0; i < tracks.Count; i++)
                    result[i + 1] = tracks[i].Isrc ?? string.Empty;
                break;

            default:
                for (int i = 0; i < count; i++)
                    result[i] = string.Empty;
                break;
        }

        return result;
    }

    private static void EncodeTextPacks(
        CdTextPackType packType,
        string[] strings,
        List<CdTextPack> packs,
        ref byte sequenceNumber)
    {
        var trackByteArrays = new byte[strings.Length][];
        for (int i = 0; i < strings.Length; i++)
        {
            string sanitized = SanitizeToIso8859_1(strings[i]);
            byte[] strBytes = Encoding.Latin1.GetBytes(sanitized);
            byte[] withNull = new byte[strBytes.Length + 1];
            strBytes.CopyTo(withNull, 0);
            withNull[^1] = 0x00;
            trackByteArrays[i] = withNull;
        }

        int currentTrackIndex = 0;
        int currentTrackOffset = 0;
        Span<byte> pack16 = stackalloc byte[16];

        while (currentTrackIndex < trackByteArrays.Length)
        {
            byte trackIndicator = (byte)currentTrackIndex;
            byte characterPosition = (byte)Math.Min(15, currentTrackOffset);
            byte blockCharacterIndicator = (byte)(characterPosition & 0x0F);

            byte[] payload = new byte[CdTextPack.PayloadSizeBytes];
            int payloadOffset = 0;

            while (payloadOffset < CdTextPack.PayloadSizeBytes && currentTrackIndex < trackByteArrays.Length)
            {
                byte[] currentTrackBytes = trackByteArrays[currentTrackIndex];
                int bytesRemaining = currentTrackBytes.Length - currentTrackOffset;
                int bytesToCopy = Math.Min(CdTextPack.PayloadSizeBytes - payloadOffset, bytesRemaining);

                Array.Copy(currentTrackBytes, currentTrackOffset, payload, payloadOffset, bytesToCopy);
                payloadOffset += bytesToCopy;
                currentTrackOffset += bytesToCopy;

                if (currentTrackOffset >= currentTrackBytes.Length)
                {
                    currentTrackIndex++;
                    currentTrackOffset = 0;
                }
            }

            pack16[0] = (byte)packType;
            pack16[1] = trackIndicator;
            pack16[2] = sequenceNumber;
            pack16[3] = blockCharacterIndicator;
            payload.CopyTo(pack16.Slice(4, 12));

            ushort crc = CdTextCrc16.ComputeInvertedCrc16(pack16);
            packs.Add(new CdTextPack(packType, trackIndicator, sequenceNumber, blockCharacterIndicator, payload, crc));
            sequenceNumber++;
        }
    }

    private static void AppendSizeInfoPacks(
        List<CdTextPack> packs,
        ref byte sequenceNumber,
        int trackCount,
        Dictionary<CdTextPackType, int> packCounts)
    {
        int count80 = packCounts.GetValueOrDefault(CdTextPackType.Title, 0);
        int count81 = packCounts.GetValueOrDefault(CdTextPackType.Performer, 0);
        int count82 = packCounts.GetValueOrDefault(CdTextPackType.Songwriter, 0);
        int count83 = packCounts.GetValueOrDefault(CdTextPackType.Composer, 0);
        int count84 = packCounts.GetValueOrDefault(CdTextPackType.Arranger, 0);
        int count85 = packCounts.GetValueOrDefault(CdTextPackType.Message, 0);
        int count86 = packCounts.GetValueOrDefault(CdTextPackType.DiscId, 0);
        int count87 = packCounts.GetValueOrDefault(CdTextPackType.Genre, 0);
        int count8E = packCounts.GetValueOrDefault(CdTextPackType.UpcEanIsrc, 0);

        byte seq0 = sequenceNumber++;
        byte seq1 = sequenceNumber++;
        byte seq2 = sequenceNumber++;

        // Pack 0 of SizeInfo
        byte[] payload0 = new byte[CdTextPack.PayloadSizeBytes];
        payload0[0] = 0x00; // Character code: 0 = ISO 8859-1
        payload0[1] = (byte)(trackCount > 0 ? 1 : 0); // First track
        payload0[2] = (byte)trackCount; // Last track
        payload0[3] = 0x00; // Copy protection flags
        payload0[4] = (byte)count80;
        payload0[5] = (byte)count81;
        payload0[6] = (byte)count82;
        payload0[7] = (byte)count83;
        payload0[8] = (byte)count84;
        payload0[9] = (byte)count85;
        payload0[10] = (byte)count86;
        payload0[11] = (byte)count87;

        Span<byte> pack16_0 = stackalloc byte[16];
        pack16_0[0] = (byte)CdTextPackType.SizeInfo;
        pack16_0[1] = 0x00;
        pack16_0[2] = seq0;
        pack16_0[3] = 0x00;
        payload0.CopyTo(pack16_0.Slice(4, 12));
        ushort crc0 = CdTextCrc16.ComputeInvertedCrc16(pack16_0);
        packs.Add(new CdTextPack(CdTextPackType.SizeInfo, 0x00, seq0, 0x00, payload0, crc0));

        // Pack 1 of SizeInfo
        byte[] payload1 = new byte[CdTextPack.PayloadSizeBytes];
        payload1[0] = 0x00; // 0x88 TOC
        payload1[1] = 0x00; // 0x89 Second TOC
        payload1[2] = 0x00; // 0x8A
        payload1[3] = 0x00; // 0x8B
        payload1[4] = 0x00; // 0x8C
        payload1[5] = 0x00; // 0x8D
        payload1[6] = (byte)count8E; // 0x8E UPC/ISRC
        payload1[7] = 0x03; // 0x8F SizeInfo count = 3
        payload1[8] = seq2; // Last sequence number of block 0
        payload1[9] = 0x00;
        payload1[10] = 0x00;
        payload1[11] = 0x00;

        Span<byte> pack16_1 = stackalloc byte[16];
        pack16_1[0] = (byte)CdTextPackType.SizeInfo;
        pack16_1[1] = 0x01;
        pack16_1[2] = seq1;
        pack16_1[3] = 0x00;
        payload1.CopyTo(pack16_1.Slice(4, 12));
        ushort crc1 = CdTextCrc16.ComputeInvertedCrc16(pack16_1);
        packs.Add(new CdTextPack(CdTextPackType.SizeInfo, 0x01, seq1, 0x00, payload1, crc1));

        // Pack 2 of SizeInfo
        byte[] payload2 = new byte[CdTextPack.PayloadSizeBytes];
        payload2[0] = 0x00;
        payload2[1] = 0x00;
        payload2[2] = 0x00;
        payload2[3] = 0x00;
        payload2[4] = 0x09; // Language code for block 0: 0x09 = English
        payload2[5] = 0x00;
        payload2[6] = 0x00;
        payload2[7] = 0x00;
        payload2[8] = 0x00;
        payload2[9] = 0x00;
        payload2[10] = 0x00;
        payload2[11] = 0x00;

        Span<byte> pack16_2 = stackalloc byte[16];
        pack16_2[0] = (byte)CdTextPackType.SizeInfo;
        pack16_2[1] = 0x02;
        pack16_2[2] = seq2;
        pack16_2[3] = 0x00;
        payload2.CopyTo(pack16_2.Slice(4, 12));
        ushort crc2 = CdTextCrc16.ComputeInvertedCrc16(pack16_2);
        packs.Add(new CdTextPack(CdTextPackType.SizeInfo, 0x02, seq2, 0x00, payload2, crc2));
    }

    internal static string SanitizeToIso8859_1(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(input.Length);
        foreach (char c in input)
        {
            if (c == '\0')
            {
                continue;
            }

            if (c <= 0xFF)
            {
                sb.Append(c);
                continue;
            }

            switch (c)
            {
                case '\u2018':
                case '\u2019':
                case '\u201A':
                case '\u201B':
                case '\u2032':
                    sb.Append('\'');
                    break;
                case '\u201C':
                case '\u201D':
                case '\u201E':
                case '\u201F':
                case '\u2033':
                    sb.Append('"');
                    break;
                case '\u2010':
                case '\u2011':
                case '\u2012':
                case '\u2013':
                case '\u2014':
                case '\u2015':
                case '\u2212':
                    sb.Append('-');
                    break;
                case '\u2026':
                    sb.Append("...");
                    break;
                case 'ł':
                    sb.Append('l');
                    break;
                case 'Ł':
                    sb.Append('L');
                    break;
                case 'đ':
                    sb.Append('d');
                    break;
                case 'Đ':
                    sb.Append('D');
                    break;
                default:
                    string decomposed = c.ToString().Normalize(NormalizationForm.FormD);
                    bool appendedBase = false;
                    foreach (char dc in decomposed)
                    {
                        if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(dc) != System.Globalization.UnicodeCategory.NonSpacingMark && dc <= 0xFF)
                        {
                            sb.Append(dc);
                            appendedBase = true;
                        }
                    }
                    if (!appendedBase)
                    {
                        sb.Append('?');
                    }
                    break;
            }
        }

        return sb.ToString();
    }
}
