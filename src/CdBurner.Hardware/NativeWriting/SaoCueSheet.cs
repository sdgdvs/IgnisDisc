namespace CdBurner.Hardware.NativeWriting;

/// <summary>One audio track to be written Session-at-Once.</summary>
/// <param name="TrackNumber">1–99.</param>
/// <param name="SectorCount">Length of the track's audio in 2,352-byte sectors (from INDEX 01 to the next track).</param>
/// <param name="PreGapSectors">Silence written before INDEX 01. Track 1 always gets at least 150 (two seconds).</param>
/// <param name="Isrc">Optional ISRC (12 characters, hyphens allowed); invalid codes are left out.</param>
public sealed record SaoTrack(int TrackNumber, int SectorCount, int PreGapSectors = 0, string? Isrc = null);

/// <summary>Where each track lands on the disc.</summary>
public sealed record SaoTrackPlacement(int TrackNumber, int PreGapLba, int Index01Lba, int EndLba, string? Isrc)
{
    public int PreGapSectors => Index01Lba - PreGapLba;
    public int SectorCount => EndLba - Index01Lba;
}

/// <summary>
/// The program area of a Session-at-Once audio disc. The host writes every sector from LBA −150 (track 1's pre-gap)
/// up to <see cref="LeadOutLba"/>; the drive writes the lead-out itself.
/// </summary>
public sealed class SaoAudioLayout
{
    public const int FirstWritableLba = -150;

    public IReadOnlyList<SaoTrackPlacement> Tracks { get; }
    public int LeadOutLba { get; }
    public int TotalSectors => LeadOutLba - FirstWritableLba;

    private SaoAudioLayout(IReadOnlyList<SaoTrackPlacement> tracks, int leadOutLba)
    {
        Tracks = tracks;
        LeadOutLba = leadOutLba;
    }

    public static SaoAudioLayout Create(IReadOnlyList<SaoTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (tracks.Count is 0 or > 99)
        {
            throw new ArgumentException("An audio CD holds 1 to 99 tracks.", nameof(tracks));
        }

        var placements = new List<SaoTrackPlacement>(tracks.Count);
        int lba = FirstWritableLba;
        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            if (t.TrackNumber != i + 1)
            {
                throw new ArgumentException($"Track numbers must run 1, 2, 3, …; found {t.TrackNumber} at position {i + 1}.", nameof(tracks));
            }
            if (t.SectorCount < 4 * Msf.FramesPerSecond)
            {
                throw new ArgumentException($"Track {t.TrackNumber} is shorter than the 4-second Red Book minimum.", nameof(tracks));
            }
            if (t.PreGapSectors < 0)
            {
                throw new ArgumentException($"Track {t.TrackNumber} has a negative pre-gap.", nameof(tracks));
            }

            int preGap = i == 0 ? Math.Max(150, t.PreGapSectors) : t.PreGapSectors;
            int index01 = lba + preGap;
            int end = index01 + t.SectorCount;
            placements.Add(new SaoTrackPlacement(t.TrackNumber, lba, index01, end, NormalizeIsrc(t.Isrc)));
            lba = end;
        }

        // 99:59:74 is the last addressable frame; real discs end long before that, the capacity check is the caller's.
        if (lba + 150 >= 100 * 60 * Msf.FramesPerSecond)
        {
            throw new ArgumentException("The tracks are longer than any CD.", nameof(tracks));
        }

        return new SaoAudioLayout(placements, lba);
    }

    /// <summary>Returns the 12-character ISRC (CC OOO YY NNNNN) in upper case, or null if it is not valid.</summary>
    public static string? NormalizeIsrc(string? isrc)
    {
        if (string.IsNullOrWhiteSpace(isrc))
        {
            return null;
        }

        string s = isrc.Replace("-", string.Empty).Trim().ToUpperInvariant();
        if (s.Length != 12)
        {
            return null;
        }

        for (int i = 0; i < 12; i++)
        {
            char c = s[i];
            bool ok = i < 5 ? char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) : char.IsAsciiDigit(c);
            if (!ok || (i < 2 && !char.IsAsciiLetterUpper(c)))
            {
                return null;
            }
        }

        return s;
    }

    /// <summary>Returns the 13-digit UPC/EAN media catalog number, or null if it is not 13 digits.</summary>
    public static string? NormalizeMcn(string? mcn)
    {
        if (string.IsNullOrWhiteSpace(mcn))
        {
            return null;
        }

        string s = mcn.Trim();
        return s.Length == 13 && s.All(char.IsAsciiDigit) && s.Any(c => c != '0') ? s : null;
    }
}

/// <summary>Alternative cue sheet layouts. Some drives reject the standard one; these follow cdrdao's fallbacks.</summary>
[Flags]
public enum CueSheetVariant
{
    Standard = 0,

    /// <summary>ISRC entries carry ADR 3 without the track's control bits.</summary>
    IsrcWithoutControl = 1,

    /// <summary>The CD-Text lead-in entry gives 00:00:00 instead of the disc's lead-in start time.</summary>
    CdTextLeadInWithoutTime = 2
}

/// <summary>
/// Builds the SEND CUE SHEET parameter list (MMC-5 6.33): 8 bytes per entry — CTL/ADR, TNO, INDEX, DATA FORM, SCMS,
/// MIN, SEC, FRAME (absolute time).
/// </summary>
public static class SaoCueSheetBuilder
{
    public const int EntrySize = 8;

    /// <summary>Data form: CD-DA, 2,352 bytes of audio per sector from the host.</summary>
    public const byte DataFormAudio = 0x00;

    /// <summary>Data form for the lead-in and lead-out: CD-DA generated by the drive.</summary>
    public const byte DataFormGenerated = 0x01;

    /// <summary>Data form for a CD-Text lead-in: main data generated by the drive, R-W sub-channel from the host.</summary>
    public const byte DataFormLeadInWithRw = 0x41;

    public const byte LeadOutTrackNumber = 0xAA;

    /// <param name="layout">Track placement.</param>
    /// <param name="mcn">Optional 13-digit media catalog number.</param>
    /// <param name="cdTextLeadInStart">Lead-in start time when CD-Text is written, otherwise null.</param>
    /// <param name="variant">Fallback layout.</param>
    public static byte[] Build(SaoAudioLayout layout, string? mcn, Msf? cdTextLeadInStart, CueSheetVariant variant = CueSheetVariant.Standard)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var entries = new List<byte[]>();
        const byte ctl = 0x00; // two-channel audio, no pre-emphasis, digital copy not permitted

        string? catalog = SaoAudioLayout.NormalizeMcn(mcn);
        if (catalog != null)
        {
            entries.Add(Entry((byte)(ctl | 0x02), (byte)catalog[0], (byte)catalog[1], (byte)catalog[2], (byte)catalog[3],
                (byte)catalog[4], (byte)catalog[5], (byte)catalog[6]));
            entries.Add(Entry((byte)(ctl | 0x02), (byte)catalog[7], (byte)catalog[8], (byte)catalog[9], (byte)catalog[10],
                (byte)catalog[11], (byte)catalog[12], 0x00));
        }

        Msf leadInTime = cdTextLeadInStart.HasValue && (variant & CueSheetVariant.CdTextLeadInWithoutTime) == 0
            ? cdTextLeadInStart.Value
            : default;
        entries.Add(Entry((byte)(ctl | 0x01), 0x00, 0x00,
            cdTextLeadInStart.HasValue ? DataFormLeadInWithRw : DataFormGenerated, 0x00, leadInTime));

        for (int i = 0; i < layout.Tracks.Count; i++)
        {
            var t = layout.Tracks[i];
            byte tno = (byte)t.TrackNumber;

            if (t.Isrc is { } isrc)
            {
                byte isrcCtlAdr = (variant & CueSheetVariant.IsrcWithoutControl) != 0 ? (byte)0x03 : (byte)(ctl | 0x03);
                entries.Add(Entry(isrcCtlAdr, tno, (byte)isrc[0], (byte)isrc[1], (byte)isrc[2], (byte)isrc[3], (byte)isrc[4], (byte)isrc[5]));
                entries.Add(Entry(isrcCtlAdr, tno, (byte)isrc[6], (byte)isrc[7], (byte)isrc[8], (byte)isrc[9], (byte)isrc[10], (byte)isrc[11]));
            }

            if (i == 0 || t.PreGapSectors > 0)
            {
                entries.Add(Entry((byte)(ctl | 0x01), tno, 0x00, DataFormAudio, 0x00, Msf.FromLba(t.PreGapLba)));
            }

            entries.Add(Entry((byte)(ctl | 0x01), tno, 0x01, DataFormAudio, 0x00, Msf.FromLba(t.Index01Lba)));
        }

        entries.Add(Entry((byte)(ctl | 0x01), LeadOutTrackNumber, 0x01, DataFormGenerated, 0x00, Msf.FromLba(layout.LeadOutLba)));

        byte[] sheet = new byte[entries.Count * EntrySize];
        for (int i = 0; i < entries.Count; i++)
        {
            entries[i].CopyTo(sheet, i * EntrySize);
        }
        return sheet;
    }

    private static byte[] Entry(byte ctlAdr, byte tno, byte index, byte dataForm, byte scms, Msf time) =>
        Entry(ctlAdr, tno, index, dataForm, scms, time.Minute, time.Second, time.Frame);

    private static byte[] Entry(byte b0, byte b1, byte b2, byte b3, byte b4, byte b5, byte b6, byte b7) =>
        new[] { b0, b1, b2, b3, b4, b5, b6, b7 };
}
