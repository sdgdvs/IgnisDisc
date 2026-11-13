namespace CdBurner.Lithography.Geometry;

/// <summary>
/// Nominal (textbook) spiral geometry for CD, DVD and single-layer BD.
/// These are only starting values: real discs differ, and calibration (Phase 2) will replace the start
/// radius, the pitch model p(r), the sector length and the start angle θ0 for each disc.
/// </summary>
public static class DiscGeometryPresets
{
    /// <summary>Largest radius used by all presets: 58.5 mm, in meters.</summary>
    public const double MaxRadiusMeters = 58.5e-3;

    /// <summary>CD start of the program area (LBA 0): 25.0 mm, in meters.</summary>
    public const double CdStartRadiusMeters = 25.0e-3;

    /// <summary>CD track pitch: 1.6 µm, in meters.</summary>
    public const double CdTrackPitchMeters = 1.6e-6;

    /// <summary>
    /// CD sector length in meters. A CD plays 75 sectors per second at a linear speed of 1.2 m/s,
    /// so one sector is 1.2 / 75 = 0.016 m = 16.0 mm of track.
    /// </summary>
    public const double CdSectorLengthMeters = 1.2 / 75.0;

    /// <summary>DVD start of the data zone (LBA 0 = physical sector number 0x30000): 24.0 mm, in meters.</summary>
    public const double DvdStartRadiusMeters = 24.0e-3;

    /// <summary>DVD single-layer track pitch: 0.74 µm, in meters.</summary>
    public const double DvdTrackPitchMeters = 0.74e-6;

    /// <summary>
    /// DVD sector length in meters. One physical sector is 26 sync frames × 1488 channel bits
    /// = 38,688 channel bits, each 0.1333 µm long: 38,688 × 0.1333 µm ≈ 5.157 mm of track.
    /// </summary>
    public const double DvdSectorLengthMeters = 26.0 * 1488.0 * 0.1333e-6;

    /// <summary>BD start of the data zone (LBA 0): 24.0 mm, in meters.</summary>
    public const double BdStartRadiusMeters = 24.0e-3;

    /// <summary>BD track pitch: 0.32 µm, in meters.</summary>
    public const double BdTrackPitchMeters = 0.32e-6;

    /// <summary>
    /// BD (25 GB layer) sector length in meters. One physical cluster is 496 frames × 1932 channel bits
    /// = 958,272 channel bits, each 74.5 nm long: ≈ 71.39 mm of track. A cluster holds 32 sectors of
    /// 2048 bytes, so one sector is ≈ 71.39 mm / 32 ≈ 2.231 mm of track.
    /// </summary>
    public const double BdSingleLayer25GbSectorLengthMeters = 496.0 * 1932.0 * 74.5e-9 / 32.0;

    /// <summary>
    /// Nominal CD: start radius 25.0 mm (LBA 0), pitch 1.6 µm, sector length 1.2 m/s ÷ 75 sectors/s = 16.0 mm,
    /// start angle 0. Nominal values only; calibration (Phase 2) replaces them per disc.
    /// </summary>
    public static SpiralGeometry CdNominal { get; } = new SpiralGeometry(
        new ConstantTrackPitch(CdTrackPitchMeters),
        CdStartRadiusMeters,
        MaxRadiusMeters,
        CdSectorLengthMeters,
        lbaAtStartRadius: 0,
        startAngleRadians: 0.0);

    /// <summary>
    /// Nominal single-layer DVD: start radius 24.0 mm (LBA 0 = PSN 0x30000), pitch 0.74 µm, sector length
    /// 26 × 1488 channel bits × 0.1333 µm ≈ 5.157 mm, start angle 0. Nominal values only; calibration
    /// (Phase 2) replaces them per disc.
    /// </summary>
    public static SpiralGeometry DvdNominal { get; } = new SpiralGeometry(
        new ConstantTrackPitch(DvdTrackPitchMeters),
        DvdStartRadiusMeters,
        MaxRadiusMeters,
        DvdSectorLengthMeters,
        lbaAtStartRadius: 0,
        startAngleRadians: 0.0);

    /// <summary>
    /// Nominal single-layer 25 GB BD: start radius 24.0 mm (LBA 0), pitch 0.32 µm, sector length
    /// 496 × 1932 channel bits × 74.5 nm ÷ 32 sectors ≈ 2.231 mm, start angle 0. Nominal values only;
    /// calibration (Phase 2) replaces them per disc.
    /// </summary>
    public static SpiralGeometry BdSingleLayer25GbNominal { get; } = new SpiralGeometry(
        new ConstantTrackPitch(BdTrackPitchMeters),
        BdStartRadiusMeters,
        MaxRadiusMeters,
        BdSingleLayer25GbSectorLengthMeters,
        lbaAtStartRadius: 0,
        startAngleRadians: 0.0);
}
