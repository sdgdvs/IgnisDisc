namespace CdBurner.Core.Primitives;

/// <summary>
/// Where a sector lies on the disc, as a radius in millimetres. Tracks are a spiral of constant pitch read at constant
/// linear velocity, so every sector covers the same area: the area from the first data sector out to sector n grows
/// linearly with n, and r(n) = sqrt(r0² + n · a / π).
/// <para>
/// The area per sector a comes from each format's data zone and nominal capacity: a disc filled to capacity ends at
/// 58 mm (ECMA-130 program area for CD; ECMA-267 data zone 24.0 to 58.0 mm for DVD; the same for single-layer BD).
/// CD uses the 80-minute disc (360,000 sectors), the densest common one, so on 74-minute discs the real distances are
/// larger, never smaller. Only single-layer media are modelled.
/// </para>
/// </summary>
public static class DiscRadius
{
    /// <summary>Outer radius of the data area on a disc filled to its nominal capacity.</summary>
    public const double OuterRadiusMm = 58.0;

    /// <summary>CD: program area starts at 25 mm; LBA 0 follows the 150-sector pre-gap of track 1.</summary>
    public const double CdStartRadiusMm = 25.0;

    /// <summary>DVD and BD: the data zone starts at 24 mm, at LBA 0.</summary>
    public const double DvdBdStartRadiusMm = 24.0;

    /// <summary>80-minute CD: 80 × 60 × 75 sectors.</summary>
    public const long CdNominalSectors = 360_000;

    /// <summary>DVD±R single layer (4.7 GB).</summary>
    public const long DvdNominalSectors = 2_295_104;

    /// <summary>BD-R single layer (25 GB).</summary>
    public const long BdNominalSectors = 12_219_392;

    /// <summary>Area one sector takes, in mm², or null for media not modelled (multi-layer, unknown).</summary>
    public static double? AreaPerSectorMm2(DiscMediaType media) => Model(media)?.Area;

    /// <summary>Radius of <paramref name="lba"/> in mm, or null for media not modelled.</summary>
    public static double? RadiusMm(DiscMediaType media, long lba)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lba);
        var model = Model(media);
        if (model == null)
        {
            return null;
        }
        var (start, offset, area) = model.Value;
        return Math.Sqrt(start * start + (lba + offset) * area / Math.PI);
    }

    private static (double Start, long Offset, double Area)? Model(DiscMediaType media)
    {
        return media switch
        {
            DiscMediaType.CdRom or DiscMediaType.CdR or DiscMediaType.CdRw =>
                (CdStartRadiusMm, OpticalConstants.CdLeadInFrames, Area(CdStartRadiusMm, CdNominalSectors + OpticalConstants.CdLeadInFrames)),
            DiscMediaType.DvdRom or DiscMediaType.DvdMinusR or DiscMediaType.DvdMinusRw or DiscMediaType.DvdPlusR or DiscMediaType.DvdPlusRw =>
                (DvdBdStartRadiusMm, 0, Area(DvdBdStartRadiusMm, DvdNominalSectors)),
            DiscMediaType.BdRom or DiscMediaType.BdR or DiscMediaType.BdRe =>
                (DvdBdStartRadiusMm, 0, Area(DvdBdStartRadiusMm, BdNominalSectors)),
            _ => null
        };
    }

    private static double Area(double startMm, long sectors) =>
        Math.PI * (OuterRadiusMm * OuterRadiusMm - startMm * startMm) / sectors;
}
