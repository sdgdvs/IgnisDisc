using System.Buffers.Binary;
using System.Text;

namespace CdBurner.Hardware.Scsi;

/// <summary>
/// Structured optical media manufacturer identification decoded from CD-R/RW ATIP (0x43 Format 0x04),
/// DVD-R/RW Pre-recorded Lead-In (0xAD Format 0x0E), DVD+R/RW ADIP (0xAD Format 0x11), or BD Disc Info (0xAD Format 0x00).
/// </summary>
public sealed record MmcMediaIdInfo(
    string RawManufacturerId,
    string ManufacturerName,
    string MediaCodeType,
    string? DyeOrStrategy = null,
    string? NominalLeadOutMsf = null)
{
    /// <summary>
    /// Formatted human-readable string combining the raw MID/ATIP code, manufacturer name, and dye strategy.
    /// </summary>
    public string FormattedSummary
    {
        get
        {
            if (string.IsNullOrWhiteSpace(RawManufacturerId))
            {
                return string.Empty;
            }

            var sb = new StringBuilder(RawManufacturerId);
            if (!string.IsNullOrWhiteSpace(ManufacturerName) &&
                !string.Equals(ManufacturerName, "Unknown Manufacturer", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append(" (").Append(ManufacturerName);
                if (!string.IsNullOrWhiteSpace(DyeOrStrategy))
                {
                    sb.Append(" - ").Append(DyeOrStrategy);
                }
                sb.Append(')');
            }
            else if (!string.IsNullOrWhiteSpace(DyeOrStrategy))
            {
                sb.Append(" (").Append(DyeOrStrategy).Append(')');
            }

            return sb.ToString();
        }
    }
}

/// <summary>
/// Pure, byte-exact parsers and manufacturer lookup tables for optical disc ATIP, ADIP, DVD Pre-recorded Lead-In, and BD Disc Information.
/// </summary>
public static class MmcMediaIdInspector
{
    /// <summary>
    /// Parses a CD-R / CD-RW ATIP response buffer from MMC READ TOC/PMA/ATIP (OpCode 0x43, Format 0x04).
    /// </summary>
    public static MmcMediaIdInfo? ParseCdAtip(ReadOnlySpan<byte> response)
    {
        // Header (4 bytes) + ATIP descriptor (at least 11 bytes to reach bytes 8..10 Lead-In Start MSF)
        if (response.Length < 12)
        {
            return null;
        }

        ushort dataLength = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(0, 2));
        if (dataLength < 10)
        {
            return null;
        }

        byte discFlags = response[6];
        bool isCdRw = (discFlags & 0x40) != 0;

        byte startM = DecodeBcdOrBinary(response[8]);
        byte startS = DecodeBcdOrBinary(response[9]);
        byte startF = DecodeBcdOrBinary(response[10]);

        // Valid Orange Book ATIP Lead-In start times are in the 95..99 minute range
        if (startM < 90 || startM > 99 || startS > 59 || startF > 74)
        {
            return null;
        }

        string rawAtip = $"{startM:D2}m{startS:D2}s{startF:D2}f";

        string? leadOutMsf = null;
        if (response.Length >= 15 && dataLength >= 13)
        {
            byte outM = DecodeBcdOrBinary(response[12]);
            byte outS = DecodeBcdOrBinary(response[13]);
            byte outF = DecodeBcdOrBinary(response[14]);
            if (outM > 0 && outM <= 99 && outS <= 59 && outF <= 74)
            {
                leadOutMsf = $"{outM:D2}m{outS:D2}s{outF:D2}f";
            }
        }

        string dyeStrategy = isCdRw
            ? "Phase-Change Rewritable"
            : (startF % 10 < 5 ? "Long Strategy (Cyanine / AZO)" : "Short Strategy (Phthalocyanine)");

        string manufacturer = LookupCdAtipManufacturer(startM, startS, startF / 10);

        return new MmcMediaIdInfo(
            RawManufacturerId: rawAtip,
            ManufacturerName: manufacturer,
            MediaCodeType: "ATIP",
            DyeOrStrategy: dyeStrategy,
            NominalLeadOutMsf: leadOutMsf);
    }

    /// <summary>
    /// Parses a DVD-R / DVD-RW Pre-recorded Information in Lead-In response from MMC READ DISC STRUCTURE (OpCode 0xAD, Format 0x0E).
    /// </summary>
    public static MmcMediaIdInfo? ParseDvdPreRecordedLeadIn(ReadOnlySpan<byte> response)
    {
        // The response is a 4-byte header followed by 8-byte pre-pit fields, each starting with its field ID.
        // Field 1 (offset 4) holds the disc application code and physical data; field 2 (offset 12) the write
        // strategy. The manufacturer ID is split across field 3 (offset 20, characters at 21..26) and
        // field 4 (offset 28, characters at 29..34), the same bytes dvd+rw-mediainfo prints as "Media ID".
        if (response.Length < 35)
        {
            return null;
        }

        Span<byte> midBytes = stackalloc byte[12];
        if (response[20] == 0x03 && response[28] == 0x04)
        {
            response.Slice(21, 6).CopyTo(midBytes.Slice(0, 6));
            response.Slice(29, 6).CopyTo(midBytes.Slice(6, 6));
        }
        else
        {
            return null;
        }

        string rawMid = ExtractPrintableAscii(midBytes);
        if (string.IsNullOrWhiteSpace(rawMid))
        {
            return null;
        }

        string manufacturer = LookupManufacturerByCode(rawMid);
        return new MmcMediaIdInfo(
            RawManufacturerId: rawMid,
            ManufacturerName: manufacturer,
            MediaCodeType: "DVD-PreRecorded");
    }

    /// <summary>
    /// Parses a DVD+R / DVD+RW / DVD+R DL ADIP response from MMC READ DISC STRUCTURE (OpCode 0xAD, Format 0x11 or 0x00).
    /// </summary>
    public static MmcMediaIdInfo? ParseDvdAdip(ReadOnlySpan<byte> response)
    {
        // 4-byte header + 32-byte ADIP block (Manufacturer ID at ADIP bytes 19..26 => response[23..31], Media Type at 27..29 => response[31..34], Revision at 30 => response[34])
        if (response.Length < 35)
        {
            return null;
        }

        string vendor = ExtractPrintableAscii(response.Slice(23, 8));
        string mediaTypeId = ExtractPrintableAscii(response.Slice(31, 3));
        byte revision = response[34];

        if (string.IsNullOrWhiteSpace(vendor))
        {
            return null;
        }

        string rawMid = string.IsNullOrWhiteSpace(mediaTypeId)
            ? vendor
            : $"{vendor}-{mediaTypeId}-{revision:D2}";

        string manufacturer = LookupManufacturerByCode(vendor);
        return new MmcMediaIdInfo(
            RawManufacturerId: rawMid,
            ManufacturerName: manufacturer,
            MediaCodeType: "ADIP");
    }

    /// <summary>
    /// Parses a Blu-ray (BD-R / BD-RE / BD-ROM) Disc Information response from MMC READ DISC STRUCTURE (OpCode 0xAD, MediaType 0x01, Format 0x00).
    /// </summary>
    public static MmcMediaIdInfo? ParseBdDiscInfo(ReadOnlySpan<byte> response)
    {
        // 4-byte header + DI Unit header ("DI" at offset 4..5) + BD DI body
        if (response.Length < 113)
        {
            return null;
        }

        if (response[4] != (byte)'D' || response[5] != (byte)'I')
        {
            return null;
        }

        // In BD Disc Information (MMC-6 §6.23.3.3.1), Disc Manufacturer ID is 6 ASCII chars at DI unit offset 100 (response offset 104)
        // and Media Type ID is 3 ASCII chars at DI unit offset 106 (response offset 110).
        string vendor = ExtractPrintableAscii(response.Slice(104, 6));
        string mediaTypeId = ExtractPrintableAscii(response.Slice(110, 3));

        if (string.IsNullOrWhiteSpace(vendor))
        {
            return null;
        }

        string rawMid = string.IsNullOrWhiteSpace(mediaTypeId)
            ? vendor
            : $"{vendor}-{mediaTypeId}";

        string manufacturer = LookupManufacturerByCode(vendor);
        return new MmcMediaIdInfo(
            RawManufacturerId: rawMid,
            ManufacturerName: manufacturer,
            MediaCodeType: "BD-DI");
    }

    // READ TOC/PMA/ATIP format 0100b returns the ATIP times as plain binary M/S/F (MMC-6 §6.26.3.6), not BCD.
    // Out-of-range values are rejected by the callers' range checks.
    private static byte DecodeBcdOrBinary(byte value) => value;

    private static string ExtractPrintableAscii(ReadOnlySpan<byte> bytes)
    {
        Span<char> chars = stackalloc char[bytes.Length];
        int len = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            if (b == 0)
            {
                continue;
            }
            if (b is >= 0x20 and <= 0x7E)
            {
                chars[len++] = (char)b;
            }
        }

        return new string(chars.Slice(0, len)).Trim();
    }

    /// <summary>
    /// Maps an ATIP lead-in start time (minute, second, frame / 10) to the disc manufacturer. The codes are the
    /// manufacturer assignments from the Orange Book licensing list, as also used by cdrecord / wodim (diskid.c).
    /// The last digit of the frame is not part of the code; it carries the dye type (0-4 long strategy, 5-9 short).
    /// </summary>
    public static string LookupCdAtipManufacturer(int minute, int second, int frameTens)
    {
        return (minute, second, frameTens) switch
        {
            (97, 15, 0) => "TDK Corporation",
            (97, 15, 1) => "Ritek Co.",
            (97, 15, 2) => "Mitsubishi Chemical Corporation",
            (97, 15, 3) => "NAN-YA Plastics Corporation",
            (97, 16, 2) => "SHENZEN SG&GAST DIGITAL OPTICAL DISCS",
            (97, 16, 3) => "Grand Advance Technology Ltd.",
            (97, 17, 0) => "Moser Baer India Limited",
            (97, 18, 1) => "WEALTH FAIR INVESTMENT LIMITED",
            (97, 18, 6) => "TAROKO INTERNATIONAL CO.,LTD.",
            (97, 20, 1) => "??? CDA Datentr�ger Albrechts GmbH.",
            (97, 21, 3) => "Bestdisc Technology Corporation",
            (97, 21, 4) => "Optical Disc Manufacturing Equipment",
            (97, 21, 5) => "Sound Sound Multi-Media Development Limited",
            (97, 22, 0) => "Woongjin Media corp",
            (97, 22, 1) => "Seantram Technology Inc.",
            (97, 22, 2) => "Advanced Digital Media",
            (97, 22, 3) => "EXIMPO",
            (97, 22, 4) => "CIS Technology Inc.",
            (97, 22, 5) => "Hong Kong Digital Technology Co., Ltd.",
            (97, 22, 6) => "Acer Media Technology, Inc.",
            (97, 23, 0) => "Matsushita Electric Industrial Co.,Ltd.",
            (97, 23, 1) => "Doremi Media Co., Ltd.",
            (97, 23, 2) => "Nacar Media srl",
            (97, 23, 3) => "AUDIO DISTRIBUTORS CO., LTD.",
            (97, 23, 4) => "VICTOR COMPANY OF JAPAN, LIMITED",
            (97, 23, 5) => "OPTROM.INC.",
            (97, 23, 6) => "Customer Pressing Oosterhout",
            (97, 24, 0) => "Taiyo Yuden Company Limited",
            (97, 24, 1) => "SONY Corporation",
            (97, 24, 2) => "Computer Support Italy s.r.l.",
            (97, 24, 3) => "UNITECH JAPAN INC.",
            (97, 24, 4) => "kdg mediatech AG",
            (97, 24, 5) => "Guann Yinn Co.,Ltd.",
            (97, 24, 6) => "Harmonic Hall Optical Disc Ltd.",
            (97, 25, 0) => "MPO",
            (97, 25, 2) => "Hitachi Maxell, Ltd.",
            (97, 25, 3) => "INFODISC Technology Co., Ltd.",
            (97, 25, 4) => "VIVASTAR AG",
            (97, 25, 5) => "AMS Technology Inc.",
            (97, 25, 6) => "Xcitec Inc.",
            (97, 26, 0) => "FORNET INTERNATIONAL PTE LTD.",
            (97, 26, 1) => "POSTECH Corporation",
            (97, 26, 2) => "SKC Co., Ltd.",
            (97, 26, 3) => "OPTICAL DISC CORPRATION",
            (97, 26, 4) => "FUJI Photo Film Co., Ltd.",
            (97, 26, 5) => "Lead Data Inc.",
            (97, 26, 6) => "CMC Magnetics Corporation",
            (97, 27, 0) => "DIGITAL STORAGE TECHNOLOGY CO.,LTD",
            (97, 27, 1) => "Plasmon Data systems Ltd.",
            (97, 27, 2) => "Princo Corporation",
            (97, 27, 3) => "Pioneer Video Corporation",
            (97, 27, 4) => "Kodak Japan Limited",
            (97, 27, 5) => "Mitsui Chemicals, Inc.",
            (97, 27, 6) => "Ricoh Company Limited",
            (97, 28, 0) => "Opti.Me.S. S.p.A.",
            (97, 28, 1) => "GIGASTORAGE CORPORATION",
            (97, 28, 2) => "Multi Media Masters & Machinary SA",
            (97, 28, 3) => "Auvistar Industry Co.,Ltd.",
            (97, 28, 4) => "King Pro Mediatek Inc.",
            (97, 28, 5) => "DELPHI TECHNOLOGY INC.",
            (97, 28, 6) => "Friendly CD-Tek Co.",
            (97, 29, 0) => "Taeil Media Co.,Ltd.",
            (97, 29, 1) => "Vanguard Disc Inc.",
            (97, 29, 2) => "UNIDISC TECHNOLOGY CO.,LTD",
            (97, 29, 3) => "Hile Optical Disc Technology Corp.",
            (97, 29, 4) => "VIVA MAGNETICS LIMITED",
            (97, 29, 5) => "General Magnetics Ld",
            (97, 30, 1) => "CDA Datentr�ger Albrechts GmbH.",
            (97, 31, 0) => "Ritek Co.",
            (97, 31, 3) => "Grand Advance Technology Ltd.",
            (97, 32, 0) => "TDK Corporation",
            (97, 32, 1) => "Prodisc Technology Inc.",
            (97, 34, 2) => "Mitsubishi Chemical Corporation",
            (97, 42, 2) => "Advanced Digital Media",
            (97, 45, 0) => "FORNET INTERNATIONAL PTE LTD.",
            (97, 45, 1) => "UNITECH JAPAN INC.",
            (97, 45, 2) => "Acer Media Technology, Inc.",
            (97, 45, 4) => "CIS Technology Inc.",
            (97, 45, 5) => "Guann Yinn Co.,Ltd.",
            (97, 45, 6) => "Xcitec Inc.",
            (97, 46, 0) => "Taiyo Yuden Company Limited",
            (97, 46, 1) => "Hong Kong Digital Technology Co., Ltd.",
            (97, 46, 2) => "Multi Media Masters & Machinary SA",
            (97, 46, 3) => "Computer Support Italy s.r.l.",
            (97, 46, 4) => "FUJI Photo Film Co., Ltd.",
            (97, 46, 5) => "Auvistar Industry Co.,Ltd.",
            (97, 46, 6) => "CMC Magnetics Corporation",
            (97, 47, 1) => "Hitachi Maxell, Ltd.",
            (97, 47, 2) => "Princo Corporation",
            (97, 47, 4) => "POSTECH Corporation",
            (97, 47, 5) => "Ritek Co.",
            (97, 47, 6) => "Prodisc Technology Inc.",
            (97, 48, 0) => "Ricoh Company Limited",
            (97, 48, 1) => "Kodak Japan Limited",
            (97, 48, 2) => "Plasmon Data systems Ltd.",
            (97, 48, 3) => "Pioneer Video Corporation",
            (97, 48, 4) => "DIGITAL STORAGE TECHNOLOGY CO.,LTD",
            (97, 48, 5) => "Mitsui Chemicals, Inc.",
            (97, 48, 6) => "Lead Data Inc.",
            (97, 49, 0) => "TDK Corporation",
            (97, 49, 1) => "GIGASTORAGE CORPORATION",
            (97, 49, 2) => "King Pro Mediatek Inc.",
            (97, 49, 3) => "Opti.Me.S. S.p.A.",
            (97, 49, 4) => "VICTOR COMPANY OF JAPAN, LIMITED",
            (97, 49, 6) => "Matsushita Electric Industrial Co.,Ltd.",
            (97, 50, 1) => "Vanguard Disc Inc.",
            (97, 50, 2) => "Mitsubishi Chemical Corporation",
            (97, 50, 3) => "CDA Datentr�ger Albrechts GmbH.",
            (97, 51, 1) => "Grand Advance Technology Ltd.",
            (97, 51, 2) => "INFODISC Technology Co., Ltd.",
            (97, 51, 5) => "Hile Optical Disc Technology Corp.",
            _ => "Unknown Manufacturer"
        };
    }

    /// <summary>
    /// Maps a DVD or Blu-ray Manufacturer ID (MID) string prefix to the optical media manufacturer name.
    /// </summary>
    public static string LookupManufacturerByCode(string? rawMid)
    {
        if (string.IsNullOrWhiteSpace(rawMid))
        {
            return "Unknown Manufacturer";
        }

        string upper = rawMid.Trim().ToUpperInvariant();

        if (upper.StartsWith("MCC", StringComparison.Ordinal) ||
            upper.StartsWith("MKM", StringComparison.Ordinal) ||
            upper.StartsWith("VERBAT", StringComparison.Ordinal))
        {
            return "Mitsubishi Chemical Corp. (Verbatim)";
        }

        if (upper.StartsWith("YUDEN", StringComparison.Ordinal) ||
            upper.StartsWith("TYG", StringComparison.Ordinal))
        {
            return "Taiyo Yuden (JVC)";
        }

        if (upper.StartsWith("MEI", StringComparison.Ordinal) ||
            upper.StartsWith("MATSHITA", StringComparison.Ordinal) ||
            upper.StartsWith("PANASON", StringComparison.Ordinal))
        {
            return "Panasonic (Matsushita)";
        }

        if (upper.StartsWith("MILLEN", StringComparison.Ordinal))
        {
            return "Millenniata (M-DISC)";
        }

        if (upper.StartsWith("RITEK", StringComparison.Ordinal))
        {
            return "Ritek Corp.";
        }

        if (upper.StartsWith("CMC", StringComparison.Ordinal))
        {
            return "CMC Magnetics";
        }

        if (upper.StartsWith("SONY", StringComparison.Ordinal))
        {
            return "Sony Corp.";
        }

        if (upper.StartsWith("RICOH", StringComparison.Ordinal))
        {
            return "Ricoh Co.";
        }

        if (upper.StartsWith("MBI", StringComparison.Ordinal))
        {
            return "Moser Baer India";
        }

        if (upper.StartsWith("MXL", StringComparison.Ordinal) ||
            upper.StartsWith("MAXELL", StringComparison.Ordinal))
        {
            return "Hitachi Maxell";
        }

        if (upper.StartsWith("TDK", StringComparison.Ordinal) ||
            upper.StartsWith("TTH", StringComparison.Ordinal))
        {
            return "TDK Corp.";
        }

        if (upper.StartsWith("PVC", StringComparison.Ordinal) ||
            upper.StartsWith("PIO", StringComparison.Ordinal))
        {
            return "Pioneer Corp.";
        }

        if (upper.StartsWith("PRODISC", StringComparison.Ordinal))
        {
            return "Prodisc Technology";
        }

        if (upper.StartsWith("PRINCO", StringComparison.Ordinal))
        {
            return "Princo Corp.";
        }

        if (upper.StartsWith("OPTODISC", StringComparison.Ordinal))
        {
            return "Optodisc Technology";
        }

        return "Unknown Manufacturer";
    }
}
