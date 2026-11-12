namespace CdBurner.Core.Verification;

/// <summary>How a CD reads in one pass, the way a player meets it.</summary>
public enum DiscHealthVerdict
{
    /// <summary>Every sector read without an uncorrectable (C2) error.</summary>
    ReadsCleanly,
    /// <summary>Some sectors carry uncorrectable errors: a player fills them in or skips there.</summary>
    Weak,
    /// <summary>Some sectors could not be read at all.</summary>
    Damaged
}

/// <summary>One track's share of a disc read-health check.</summary>
public sealed record TrackHealth(int TrackNumber, bool IsAudio, int StartLba, int Sectors, int FlaggedSectors, int UnreadableSectors);

/// <summary>
/// Result of reading every sector of a CD once with C2 error pointers. A CD player reads each spot once and covers
/// what its error correction cannot fix: on audio that is a click, a mute or a skip. C2 pointers mark exactly those
/// spots, so the sectors they flag are where a disc is weak. One pass by one drive, at its own speed: a player at 1x
/// may do better or worse, and another drive differently.
/// </summary>
public sealed record DiscHealthReport(
    int TotalSectors,
    int FlaggedSectors,
    int UnreadableSectors,
    bool C2Available,
    IReadOnlyList<TrackHealth> Tracks,
    IReadOnlyList<int> ProblemsPerMinute, // flagged + unreadable sectors in each minute of the disc
    TimeSpan Elapsed)
{
    /// <summary>Sectors a second on a CD (75): the disc's own clock, for saying where along it something is.</summary>
    public const int SectorsPerSecond = 75;

    public DiscHealthVerdict Verdict =>
        UnreadableSectors > 0 ? DiscHealthVerdict.Damaged
        : FlaggedSectors > 0 ? DiscHealthVerdict.Weak
        : DiscHealthVerdict.ReadsCleanly;

    /// <summary>Share of problem sectors in the last fifth of the disc, the outer edge where weak dye shows first.</summary>
    public double FlaggedShareInOuterFifth
    {
        get
        {
            int problems = FlaggedSectors + UnreadableSectors;
            if (problems == 0 || ProblemsPerMinute.Count == 0)
            {
                return 0;
            }
            int from = (int)Math.Floor(ProblemsPerMinute.Count * 0.8);
            return (double)ProblemsPerMinute.Skip(from).Sum() / problems;
        }
    }

    /// <summary>A plain summary for people: what was found, where, and what it means for playback.</summary>
    public string Describe()
    {
        string scope = $"{TotalSectors:N0} sectors ({Clock(TotalSectors)}) read once";
        if (!C2Available)
        {
            scope += "; this drive does not report uncorrectable errors, so only sectors that would not read at all count";
        }

        switch (Verdict)
        {
            case DiscHealthVerdict.ReadsCleanly:
                return $"Reads cleanly: {scope}, no uncorrectable errors.";
            case DiscHealthVerdict.Weak:
            {
                string where = Where();
                string edge = FlaggedShareInOuterFifth >= 0.8 ? " Most of it is near the outer edge, where weak or aging discs fail first." : "";
                return $"Weak in places: {FlaggedSectors:N0} of {scope} had uncorrectable errors ({where}). A player covers these up, "
                     + $"which can be heard as a click or a skip.{edge} The data may still be right (a careful rip re-reads such spots); "
                     + "a copy on a better disc is the safe choice.";
            }
            default:
                return $"Damaged: {UnreadableSectors:N0} sector(s) could not be read at all, and {FlaggedSectors:N0} more had uncorrectable "
                     + $"errors ({scope}; {Where()}). Players will skip or stop there. Rip it now with secure ripping while it still reads.";
        }
    }

    /// <summary>"tracks 5–8, mostly 24:00–31:00" style description of where the trouble is.</summary>
    private string Where()
    {
        var bad = Tracks.Where(t => t.FlaggedSectors + t.UnreadableSectors > 0).Select(t => t.TrackNumber).ToList();
        string tracks = bad.Count == 0 ? "" : bad.Count == 1 ? $"track {bad[0]}" : $"tracks {string.Join(", ", bad)}";
        var minutes = ProblemsPerMinute.Select((n, m) => (n, m)).Where(x => x.n > 0).Select(x => x.m).ToList();
        string span = minutes.Count == 0 ? "" : minutes.Count == 1 ? $"minute {minutes[0]}" : $"minutes {minutes[0]}–{minutes[^1]}";
        return string.Join(", ", new[] { tracks, span }.Where(s => s.Length > 0));
    }

    private static string Clock(int sectors)
    {
        var t = TimeSpan.FromSeconds((double)sectors / SectorsPerSecond);
        return $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }
}
