namespace CdBurner.Core.Primitives;

/// <summary>
/// Picks the write speed used when the user has not chosen one for this burn.
/// The default is the slowest speed the drive offers for the inserted disc: the drive builds that list from the
/// disc it reads, so every speed on it is one the drive says it can write on that disc, and the slowest one leaves
/// the most margin on cheap or marginal media. Faster speeds stay one click (or <c>--speed</c>) away.
/// </summary>
public static class WriteSpeedPolicy
{
    /// <summary>Preference value: the slowest speed the drive offers for this disc (the default).</summary>
    public const int Safest = -1;

    /// <summary>Preference value: the fastest speed the drive offers for this disc.</summary>
    public const int Maximum = 0;

    /// <summary>
    /// Chooses a speed from the drive's list. <paramref name="preference"/> is <see cref="Safest"/>,
    /// <see cref="Maximum"/>, or a multiplier such as 16. Returns null when the drive offers no speeds,
    /// in which case the drive keeps its own speed.
    /// </summary>
    public static WriteSpeedDescriptor? Choose(IEnumerable<WriteSpeedDescriptor>? offered, int preference)
    {
        var speeds = (offered ?? []).Where(s => s.BytesPerSecond > 0 && s.Multiplier > 0).ToList();
        if (speeds.Count == 0) return null;

        if (preference < 0) return speeds.MinBy(s => s.BytesPerSecond);
        if (preference == Maximum) return speeds.MaxBy(s => s.BytesPerSecond);

        // A chosen multiplier: the offered speed within half a step of it, else the nearest offered speed above it,
        // else the nearest one below.
        var near = speeds.Where(s => Math.Abs(s.Multiplier - preference) < 0.5).OrderBy(s => Math.Abs(s.Multiplier - preference)).ToList();
        if (near.Count > 0) return near[0];

        var above = speeds.Where(s => s.Multiplier >= preference).ToList();
        if (above.Count > 0) return above.MinBy(s => s.BytesPerSecond);

        return speeds.OrderBy(s => Math.Abs(s.Multiplier - preference)).First();
    }

    /// <summary>
    /// The speeds in a drive's list that belong to the inserted disc: those labelled with its exact kind, else those of
    /// the same family (CD, DVD or Blu-ray), else the whole list. A DVD speed must never be chosen for a CD.
    /// </summary>
    public static IReadOnlyList<WriteSpeedDescriptor> ForDisc(IEnumerable<WriteSpeedDescriptor>? offered, DiscMediaType mediaType)
    {
        var all = (offered ?? []).ToList();
        var exact = all.Where(s => s.MediaType == mediaType).ToList();
        if (exact.Count > 0) return exact;

        var family = all.Where(s => Family(s.MediaType) == Family(mediaType)).ToList();
        return family.Count > 0 ? family : all;
    }

    private static int Family(DiscMediaType type) => type.IsDvd() ? 1 : type.IsBluRay() ? 2 : 0;

    /// <summary>Plain-language name of a preference value for settings screens.</summary>
    public static string Describe(int preference) => preference switch
    {
        < 0 => "Safest (slowest the drive offers)",
        Maximum => "Maximum",
        _ => $"{preference}x"
    };
}
