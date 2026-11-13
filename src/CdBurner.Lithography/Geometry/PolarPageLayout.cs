using System.Globalization;

namespace CdBurner.Lithography.Geometry;

/// <summary>A point found on a page: which page, and where on it.</summary>
/// <param name="Ring">Ring index, 0 = innermost.</param>
/// <param name="Sector">Index of the page within its ring, counted in the direction of increasing θ.</param>
/// <param name="UMeters">
/// Tangential arc length in meters measured at the ring's mid radius, 0 to the page width, in the
/// direction of increasing θ.
/// </param>
/// <param name="VMeters">Radial distance in meters from the ring's inner edge, 0 to the page height.</param>
public readonly record struct PageHit(int Ring, int Sector, double UMeters, double VMeters);

/// <summary>The outline of one page: a piece of a ring between two radii and two angles.</summary>
/// <param name="InnerRadiusMeters">Inner edge of the page, in meters.</param>
/// <param name="OuterRadiusMeters">Outer edge of the page, in meters.</param>
/// <param name="StartAngleRadians">Angle where the page starts, in radians, 0 ≤ start &lt; 2π.</param>
/// <param name="EndAngleRadians">Angle where the page ends, in radians, start &lt; end ≤ 2π.</param>
public readonly record struct PageBounds(
    double InnerRadiusMeters,
    double OuterRadiusMeters,
    double StartAngleRadians,
    double EndAngleRadians)
{
    /// <summary>Radius of the middle of the page, in meters.</summary>
    public double MidRadiusMeters => 0.5 * (InnerRadiusMeters + OuterRadiusMeters);

    /// <summary>Angle of the middle of the page, in radians.</summary>
    public double MidAngleRadians => 0.5 * (StartAngleRadians + EndAngleRadians);
}

/// <summary>
/// Divides a band of the disc (an annulus) into a grid of small "polar microfiche pages".
/// <para>
/// Rings are numbered from the inside out, starting at 0. Ring k spans the radii
/// innerR + k·(h + gr) to that plus h, where h is the page height and gr the radial gutter.
/// A ring holds floor(2π·rMid / (w + gt)) pages, where w is the page width and gt the tangential gutter,
/// both measured as arc length at the ring's mid radius rMid. Whatever angle is left over is shared
/// equally between the gutters, so the pages are spread evenly around the full circle.
/// Page j of a ring starts at angle j·(2π / pages in ring). Angles use the read-side frame of
/// <see cref="DiscOrientation"/>.
/// </para>
/// <para>
/// This is a pure polar grid, so a page is slightly wider at its outer edge than at its inner edge.
/// This keystone is about h/r (about 4 % for 1.5 mm pages at 38 mm) and is accepted.
/// </para>
/// </summary>
public sealed class PolarPageLayout
{
    private const double TwoPi = 2.0 * Math.PI;

    private readonly int[] _sectorsInRing;
    private readonly double _ringPitchMeters;

    /// <summary>Creates a page layout. All lengths are in meters.</summary>
    /// <param name="innerRadiusMeters">Inner edge of the band.</param>
    /// <param name="outerRadiusMeters">Outer edge of the band.</param>
    /// <param name="pageRadialHeightMeters">Page height h (radial direction).</param>
    /// <param name="pageTangentialWidthMeters">Page width w (arc length at the ring's mid radius).</param>
    /// <param name="radialGutterMeters">Gap between rings; may be 0.</param>
    /// <param name="tangentialGutterMeters">Smallest gap between pages in a ring; may be 0.</param>
    /// <param name="columns">Content cells per page across the width.</param>
    /// <param name="rows">Content cells per page across the height.</param>
    public PolarPageLayout(
        double innerRadiusMeters,
        double outerRadiusMeters,
        double pageRadialHeightMeters,
        double pageTangentialWidthMeters,
        double radialGutterMeters,
        double tangentialGutterMeters,
        int columns,
        int rows)
    {
        RequirePositive(innerRadiusMeters, nameof(innerRadiusMeters));
        if (!double.IsFinite(outerRadiusMeters) || outerRadiusMeters <= innerRadiusMeters)
        {
            throw new ArgumentOutOfRangeException(nameof(outerRadiusMeters), outerRadiusMeters,
                "Outer radius must be finite and greater than the inner radius (meters).");
        }
        RequirePositive(pageRadialHeightMeters, nameof(pageRadialHeightMeters));
        RequirePositive(pageTangentialWidthMeters, nameof(pageTangentialWidthMeters));
        RequireZeroOrPositive(radialGutterMeters, nameof(radialGutterMeters));
        RequireZeroOrPositive(tangentialGutterMeters, nameof(tangentialGutterMeters));
        if (columns < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(columns), columns, "A page needs at least one column.");
        }
        if (rows < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(rows), rows, "A page needs at least one row.");
        }

        InnerRadiusMeters = innerRadiusMeters;
        OuterRadiusMeters = outerRadiusMeters;
        PageRadialHeightMeters = pageRadialHeightMeters;
        PageTangentialWidthMeters = pageTangentialWidthMeters;
        RadialGutterMeters = radialGutterMeters;
        TangentialGutterMeters = tangentialGutterMeters;
        Columns = columns;
        Rows = rows;
        _ringPitchMeters = pageRadialHeightMeters + radialGutterMeters;

        // Number of whole rings that fit. The last ring needs no gutter after it, hence the "+ gutter".
        // The tiny 1e-9 keeps an exact fit from being lost to rounding.
        double ringsThatFit = (outerRadiusMeters - innerRadiusMeters + radialGutterMeters) / _ringPitchMeters;
        int ringCount = (int)Math.Floor(ringsThatFit + 1.0e-9);
        if (ringCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageRadialHeightMeters), pageRadialHeightMeters,
                "The page height does not fit between the inner and outer radius.");
        }

        _sectorsInRing = new int[ringCount];
        long totalPages = 0;
        for (int ring = 0; ring < ringCount; ring++)
        {
            double circumference = TwoPi * RingMidRadius(ring);
            int sectors = (int)Math.Floor(circumference / (pageTangentialWidthMeters + tangentialGutterMeters));
            if (sectors < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(pageTangentialWidthMeters), pageTangentialWidthMeters,
                    $"A page plus its gutter is longer than the circumference of ring {ring}.");
            }
            _sectorsInRing[ring] = sectors;
            totalPages += sectors;
        }
        TotalPages = totalPages;
    }

    /// <summary>Inner edge of the band, in meters.</summary>
    public double InnerRadiusMeters { get; }

    /// <summary>Outer edge of the band, in meters.</summary>
    public double OuterRadiusMeters { get; }

    /// <summary>Page height h in the radial direction, in meters.</summary>
    public double PageRadialHeightMeters { get; }

    /// <summary>Page width w as arc length at the ring's mid radius, in meters.</summary>
    public double PageTangentialWidthMeters { get; }

    /// <summary>Gap between rings, in meters.</summary>
    public double RadialGutterMeters { get; }

    /// <summary>Smallest gap between neighbouring pages of a ring, in meters at the ring's mid radius.</summary>
    public double TangentialGutterMeters { get; }

    /// <summary>Content cells per page across the width.</summary>
    public int Columns { get; }

    /// <summary>Content cells per page across the height.</summary>
    public int Rows { get; }

    /// <summary>Number of rings of pages in the band.</summary>
    public int RingCount => _sectorsInRing.Length;

    /// <summary>Number of pages on the whole band.</summary>
    public long TotalPages { get; }

    /// <summary>Number of pages in the given ring (0 = innermost).</summary>
    public int SectorsInRing(int ring)
    {
        RequireRing(ring);
        return _sectorsInRing[ring];
    }

    /// <summary>
    /// Finds the page under a point of the disc. Constant time: it computes the ring and page directly.
    /// Returns false when the point is in a gutter, outside the band, or not a finite number.
    /// </summary>
    /// <param name="radiusMeters">Radius of the point, in meters.</param>
    /// <param name="angleRadians">Angle of the point, in radians. Any value; it is wrapped to 0..2π.</param>
    /// <param name="hit">The page and the position on it, when found.</param>
    public bool TryLocate(double radiusMeters, double angleRadians, out PageHit hit)
    {
        hit = default;
        if (!double.IsFinite(radiusMeters) || !double.IsFinite(angleRadians))
        {
            return false;
        }
        if (radiusMeters < InnerRadiusMeters)
        {
            return false;
        }

        int ring = (int)((radiusMeters - InnerRadiusMeters) / _ringPitchMeters);
        if (ring >= _sectorsInRing.Length)
        {
            return false;
        }
        double v = radiusMeters - RingInnerRadius(ring);
        if (v < 0.0 || v > PageRadialHeightMeters)
        {
            return false; // radial gutter
        }

        int sectors = _sectorsInRing[ring];
        double midRadius = RingMidRadius(ring);
        double anglePerSector = TwoPi / sectors;
        double pageAngle = PageTangentialWidthMeters / midRadius;

        double wrapped = WrapAngle(angleRadians);
        int sector = (int)(wrapped / anglePerSector);
        if (sector >= sectors)
        {
            sector = sectors - 1; // rounding right at 2π
        }
        double angleIntoSector = wrapped - sector * anglePerSector;
        if (angleIntoSector < 0.0 || angleIntoSector > pageAngle)
        {
            return false; // tangential gutter
        }

        hit = new PageHit(ring, sector, angleIntoSector * midRadius, v);
        return true;
    }

    /// <summary>The outline (radii and angles) of one page.</summary>
    /// <param name="ring">Ring index, 0 = innermost.</param>
    /// <param name="sector">Index of the page within its ring.</param>
    public PageBounds GetBounds(int ring, int sector)
    {
        RequireRing(ring);
        int sectors = _sectorsInRing[ring];
        if (sector < 0 || sector >= sectors)
        {
            throw new ArgumentOutOfRangeException(nameof(sector), sector,
                $"Ring {ring} has pages 0 to {sectors - 1}.");
        }
        double innerRadius = RingInnerRadius(ring);
        double startAngle = sector * (TwoPi / sectors);
        double pageAngle = PageTangentialWidthMeters / RingMidRadius(ring);
        return new PageBounds(innerRadius, innerRadius + PageRadialHeightMeters, startAngle, startAngle + pageAngle);
    }

    /// <summary>Content column (0 to <see cref="Columns"/> − 1) for a tangential position u in meters on a page.</summary>
    public int CellColumnAt(double uMeters)
    {
        int column = (int)Math.Floor(uMeters / PageTangentialWidthMeters * Columns);
        return Math.Clamp(column, 0, Columns - 1);
    }

    /// <summary>Content row (0 at the page's inner edge, to <see cref="Rows"/> − 1) for a radial position v in meters on a page.</summary>
    public int CellRowAt(double vMeters)
    {
        int row = (int)Math.Floor(vMeters / PageRadialHeightMeters * Rows);
        return Math.Clamp(row, 0, Rows - 1);
    }

    /// <summary>
    /// Human-readable page name such as "R04-S18": ring and page number, at least two digits each,
    /// more when the number needs them.
    /// </summary>
    public static string PageLabel(int ring, int sector)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ring);
        ArgumentOutOfRangeException.ThrowIfNegative(sector);
        return string.Create(CultureInfo.InvariantCulture, $"R{ring:D2}-S{sector:D2}");
    }

    /// <summary>Wraps any finite angle in radians into the range 0 ≤ angle &lt; 2π.</summary>
    public static double WrapAngle(double angleRadians)
    {
        double wrapped = angleRadians - TwoPi * Math.Floor(angleRadians / TwoPi);
        // A tiny negative input can round up to exactly 2π; fold it back to 0.
        if (wrapped >= TwoPi || wrapped < 0.0)
        {
            wrapped = 0.0;
        }
        return wrapped;
    }

    private double RingInnerRadius(int ring)
    {
        return InnerRadiusMeters + ring * _ringPitchMeters;
    }

    private double RingMidRadius(int ring)
    {
        return RingInnerRadius(ring) + 0.5 * PageRadialHeightMeters;
    }

    private void RequireRing(int ring)
    {
        if (ring < 0 || ring >= _sectorsInRing.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(ring), ring,
                $"The layout has rings 0 to {_sectorsInRing.Length - 1}.");
        }
    }

    private static void RequirePositive(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0.0)
        {
            throw new ArgumentOutOfRangeException(name, value, "Value must be a finite number greater than zero (meters).");
        }
    }

    private static void RequireZeroOrPositive(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0.0)
        {
            throw new ArgumentOutOfRangeException(name, value, "Value must be a finite number, zero or greater (meters).");
        }
    }
}
