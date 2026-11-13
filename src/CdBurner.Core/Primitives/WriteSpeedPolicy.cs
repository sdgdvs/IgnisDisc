namespace CdBurner.Core.Primitives;

/// <summary>
/// Picks the write speed used when the user has not chosen one for this burn.
/// <para>
/// The default, <see cref="Recommended"/>, works from what the drive reports for the inserted disc, not from a fixed
/// number. Two reasons. Writing at a constant linear velocity (CLV) keeps one laser setting from the first track to
/// the last; CAV and partial CAV speeds climb towards the outer edge, so the drive has to change its power as it
/// goes, and reviews of such burns found errors rising late in the disc. And the slowest speed is not automatically
/// the best: drives and discs are tuned for a range of speeds, and a recordable made for fast writing is not
/// necessarily better written well below it.
/// </para>
/// <para>
/// So: when the drive marks some speeds CLV and others CAV, only its CLV speeds are considered (on slim drives that
/// is often just the lowest, e.g. CD-R 10x CLV, 16x partial CAV, 24x CAV). From those, the fastest at or below a
/// ceiling: 16x for CD and 8x for DVD, the usual top of constant-speed writing on these discs; Blu-ray has no
/// ceiling. When nothing is at or below the ceiling, the slowest speed offered.
/// </para>
/// </summary>
public static class WriteSpeedPolicy
{
    /// <summary>Preference value: the drive-aware choice described on the class (the default).</summary>
    public const int Recommended = -1;

    /// <summary>Preference value: the slowest speed the drive offers for this disc.</summary>
    public const int Slowest = -2;

    /// <summary>Preference value: the fastest speed the drive offers for this disc.</summary>
    public const int Maximum = 0;

    /// <summary>Ceiling for <see cref="Recommended"/> on CD.</summary>
    public const double CdCeiling = 16;

    /// <summary>Ceiling for <see cref="Recommended"/> on DVD.</summary>
    public const double DvdCeiling = 8;

    /// <summary>
    /// Chooses a speed from the drive's list for the inserted disc (see <see cref="ForDisc"/>).
    /// <paramref name="preference"/> is <see cref="Recommended"/>, <see cref="Slowest"/>, <see cref="Maximum"/>, or a
    /// multiplier such as 16. Returns null when the drive offers no speeds; the drive then keeps its own speed.
    /// </summary>
    public static WriteSpeedDescriptor? Choose(IEnumerable<WriteSpeedDescriptor>? offered, int preference)
    {
        var speeds = (offered ?? []).Where(s => s.BytesPerSecond > 0 && s.Multiplier > 0).ToList();
        if (speeds.Count == 0) return null;

        switch (preference)
        {
            case Recommended:
                return ChooseRecommended(speeds);
            case < 0:
                return speeds.MinBy(s => s.BytesPerSecond);
            case Maximum:
                return speeds.MaxBy(s => s.BytesPerSecond);
        }

        // A chosen multiplier: the offered speed within half a step of it, else the nearest offered speed above it,
        // else the nearest one below. CLV wins a tie with CAV at the same figure.
        var near = speeds.Where(s => Math.Abs(s.Multiplier - preference) < 0.5)
            .OrderBy(s => Math.Abs(s.Multiplier - preference)).ThenBy(s => s.Rotation == WriteRotation.Cav).ToList();
        if (near.Count > 0) return near[0];

        var above = speeds.Where(s => s.Multiplier >= preference).ToList();
        if (above.Count > 0) return above.MinBy(s => s.BytesPerSecond);

        return speeds.OrderBy(s => Math.Abs(s.Multiplier - preference)).First();
    }

    /// <summary>
    /// Like <see cref="Choose"/>, but holds the result to the ceiling <see cref="MediaSpeedCaps"/> lists for this disc's
    /// media ID, if any. Over the cap, the fastest offered speed at or below it is used, else the slowest offered.
    /// <see cref="Slowest"/> is never raised. <c>Reason</c> is set whenever the disc has a cap, to show next to the speed.
    /// </summary>
    public static (WriteSpeedDescriptor? Speed, string? Reason) ChooseForMedia(
        IEnumerable<WriteSpeedDescriptor>? offered, int preference, string? mediaId, DiscMediaType mediaType)
    {
        var speeds = (offered ?? []).Where(s => s.BytesPerSecond > 0 && s.Multiplier > 0).ToList();
        var chosen = Choose(speeds, preference);
        var cap = MediaSpeedCaps.Find(mediaId, mediaType);
        if (chosen is null || cap is null) return (chosen, null);

        if (chosen.Value.Multiplier > cap.MaxMultiplier + 0.25)
        {
            var under = speeds.Where(s => s.Multiplier <= cap.MaxMultiplier + 0.25).ToList();
            chosen = under.Count > 0
                ? under.OrderBy(s => s.BytesPerSecond).ThenBy(s => s.Rotation == WriteRotation.Clv).Last()
                : speeds.MinBy(s => s.BytesPerSecond);
        }
        return (chosen, cap.Reason);
    }

    /// <summary>Why <see cref="Recommended"/> picked what it did, in one plain sentence (for logs and help).</summary>
    public static string ExplainRecommended(IEnumerable<WriteSpeedDescriptor>? offered)
    {
        var speeds = (offered ?? []).Where(s => s.BytesPerSecond > 0 && s.Multiplier > 0).ToList();
        var chosen = Choose(speeds, Recommended);
        if (chosen is null) return "The drive lists no write speeds for this disc, so it keeps its own.";

        bool rotationKnown = HasMixedRotation(speeds);
        double? ceiling = Ceiling(chosen.Value.MediaType);
        string from = rotationKnown ? "the speeds the drive writes at a constant linear velocity" : "the speeds the drive offers";
        string limit = ceiling is { } c ? $" up to {c:0}x" : string.Empty;
        return $"{chosen.Value.Multiplier:0.#}x: the fastest of {from}{limit}.";
    }

    private static WriteSpeedDescriptor ChooseRecommended(List<WriteSpeedDescriptor> speeds)
    {
        // Only trust the drive's CLV marks when it uses both; a list marked all one way says nothing.
        var pool = HasMixedRotation(speeds) ? speeds.Where(s => s.Rotation == WriteRotation.Clv).ToList() : speeds;

        double? ceiling = Ceiling(pool[0].MediaType);
        var underCeiling = ceiling is { } c ? pool.Where(s => s.Multiplier <= c + 0.25).ToList() : pool;
        return underCeiling.Count > 0
            ? underCeiling.OrderBy(s => s.BytesPerSecond).ThenBy(s => s.Rotation == WriteRotation.Clv).Last()
            : pool.MinBy(s => s.BytesPerSecond);
    }

    private static bool HasMixedRotation(List<WriteSpeedDescriptor> speeds) =>
        speeds.Any(s => s.Rotation == WriteRotation.Clv) && speeds.Any(s => s.Rotation == WriteRotation.Cav);

    private static double? Ceiling(DiscMediaType type) =>
        type.IsDvd() ? DvdCeiling : type.IsBluRay() ? null : CdCeiling;

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

    /// <summary>
    /// Marks each speed in Windows' list (which has no rotation) with the rotation the drive reports for the same
    /// figure, within 0.6x: CLV when the drive offers that figure CLV at all, else CAV, else left unknown.
    /// </summary>
    public static IReadOnlyList<WriteSpeedDescriptor> WithRotationFrom(
        IEnumerable<WriteSpeedDescriptor> windowsSpeeds, IEnumerable<WriteSpeedDescriptor>? driveSpeeds)
    {
        var drive = (driveSpeeds ?? []).Where(d => d.Rotation != WriteRotation.Unknown && d.Multiplier > 0).ToList();
        return windowsSpeeds.Select(w =>
        {
            if (w.Rotation != WriteRotation.Unknown) return w;
            var same = drive.Where(d => Family(d.MediaType) == Family(w.MediaType) && Math.Abs(d.Multiplier - w.Multiplier) < 0.6).ToList();
            var rotation = same.Any(d => d.Rotation == WriteRotation.Clv) ? WriteRotation.Clv
                : same.Count > 0 ? WriteRotation.Cav
                : WriteRotation.Unknown;
            return w with { Rotation = rotation };
        }).ToList();
    }

    private static int Family(DiscMediaType type) => type.IsDvd() ? 1 : type.IsBluRay() ? 2 : 0;

    /// <summary>Plain-language name of a preference value for settings screens.</summary>
    public static string Describe(int preference) => preference switch
    {
        Recommended => "Recommended for the drive and disc",
        < 0 => "Slowest the drive offers",
        Maximum => "Maximum",
        _ => $"{preference}x"
    };
}
