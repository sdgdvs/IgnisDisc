namespace CdBurner.Core.OpticalLithography;

/// <summary>
/// The one place that defines the angle convention of this module.
/// <para>
/// The disc-fixed frame is viewed from the READ (laser) side and the angle θ increases counterclockwise.
/// ECMA-130 has the disc spinning counterclockwise as seen by the optics, so in the disc's own frame the
/// head traces the track CLOCKWISE. That is why θ decreases as track length (and LBA) increases.
/// </para>
/// </summary>
public static class DiscOrientation
{
    /// <summary>
    /// Sign of the change in θ per unit of track length, read-side view: −1 (clockwise).
    /// To confirm on a real burned disc in Phase 2 (calibration).
    /// </summary>
    public const double TrackAngleSign = -1.0;
}

/// <summary>
/// A point on the disc surface in polar coordinates, in the read-side frame of <see cref="DiscOrientation"/>.
/// </summary>
/// <param name="RadiusMeters">Distance from the disc centre, in meters.</param>
/// <param name="AngleRadians">
/// Angle in radians, counterclockwise seen from the read side. NOT wrapped to 0..2π: it keeps counting
/// turns along the spiral, so it can be a large negative number.
/// </param>
public readonly record struct DiscPosition(double RadiusMeters, double AngleRadians)
{
    /// <summary>Cartesian x in meters (x = r·cos θ), read-side view.</summary>
    public double X => RadiusMeters * Math.Cos(AngleRadians);

    /// <summary>Cartesian y in meters (y = r·sin θ), read-side view.</summary>
    public double Y => RadiusMeters * Math.Sin(AngleRadians);

    /// <summary>
    /// The same point as seen from the LABEL side of the disc. Looking from the other side mirrors the
    /// picture, so θ becomes −θ. To confirm on a real burned disc in Phase 2 (calibration).
    /// </summary>
    public DiscPosition ToLabelSideView()
    {
        return new DiscPosition(RadiusMeters, -AngleRadians);
    }
}
