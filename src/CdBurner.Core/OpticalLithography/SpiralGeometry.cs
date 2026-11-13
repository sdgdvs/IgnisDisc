namespace CdBurner.Core.OpticalLithography;

/// <summary>
/// Maps positions along the data spiral of an optical disc to physical positions on the disc surface.
/// <para>
/// Physics (SI units, meters and radians). One turn of track at radius r covers a ring of area 2πr·p(r),
/// so 2πr·dr = p(r)·ds, and the track length from the start radius r0 is s(r) = ∫ 2πr'/p(r') dr'.
/// The angle depends only on the radius: dθ = ds/r = 2π·dr/p(r). With Φ(r) = ∫ dr'/p(r') (the number of
/// turns since r0), θ(r) = θ0 − 2π·Φ(r). The minus sign is <see cref="DiscOrientation.TrackAngleSign"/>.
/// </para>
/// <para>
/// A constant pitch uses closed formulas. Any other pitch model uses tables of s and Φ on a radius grid
/// of at most 1 µm, built once in the constructor. Instances are immutable and safe to share between threads.
/// </para>
/// </summary>
public sealed class SpiralGeometry
{
    private const double TwoPi = 2.0 * Math.PI;

    /// <summary>Largest allowed spacing of the radius grid for the tables, in meters (1 µm).</summary>
    private const double MaxTableStepMeters = 1.0e-6;

    // 3-point Gauss-Legendre rule on [-1, 1]: exact for polynomials up to degree 5.
    private const double GaussNode = 0.7745966692414834;      // sqrt(3/5)
    private const double GaussWeightOuter = 5.0 / 9.0;
    private const double GaussWeightCentre = 8.0 / 9.0;

    private readonly ITrackPitchModel _pitch;
    private readonly bool _isConstantPitch;
    private readonly double _constantPitchMeters;

    // Tables for the non-constant case. Entry i belongs to radius r0 + i·_tableStepMeters.
    private readonly double[] _trackLengthTable;
    private readonly double[] _turnsTable;
    private readonly double _tableStepMeters;
    private readonly int _tableIntervalCount;

    /// <summary>Creates the geometry of one spiral.</summary>
    /// <param name="pitch">Track pitch as a function of radius.</param>
    /// <param name="startRadiusMeters">Radius r0 where the spiral (and <paramref name="lbaAtStartRadius"/>) starts, in meters.</param>
    /// <param name="maxRadiusMeters">Largest usable radius, in meters.</param>
    /// <param name="sectorLengthMeters">Length of track taken by one sector, in meters.</param>
    /// <param name="lbaAtStartRadius">The logical block address whose first channel bit sits at r0.</param>
    /// <param name="startAngleRadians">Angle θ0 of the track at r0, in radians.</param>
    public SpiralGeometry(
        ITrackPitchModel pitch,
        double startRadiusMeters,
        double maxRadiusMeters,
        double sectorLengthMeters,
        long lbaAtStartRadius,
        double startAngleRadians)
    {
        ArgumentNullException.ThrowIfNull(pitch);
        if (!double.IsFinite(startRadiusMeters) || startRadiusMeters <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(startRadiusMeters), startRadiusMeters,
                "Start radius must be a finite number greater than zero (meters).");
        }
        if (!double.IsFinite(maxRadiusMeters) || maxRadiusMeters <= startRadiusMeters)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRadiusMeters), maxRadiusMeters,
                "Maximum radius must be finite and greater than the start radius (meters).");
        }
        if (!double.IsFinite(sectorLengthMeters) || sectorLengthMeters <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(sectorLengthMeters), sectorLengthMeters,
                "Sector length must be a finite number greater than zero (meters).");
        }
        if (!double.IsFinite(startAngleRadians))
        {
            throw new ArgumentOutOfRangeException(nameof(startAngleRadians), startAngleRadians,
                "Start angle must be a finite number (radians).");
        }

        _pitch = pitch;
        StartRadiusMeters = startRadiusMeters;
        MaxRadiusMeters = maxRadiusMeters;
        SectorLengthMeters = sectorLengthMeters;
        LbaAtStartRadius = lbaAtStartRadius;
        StartAngleRadians = startAngleRadians;

        if (pitch is ConstantTrackPitch constant)
        {
            _isConstantPitch = true;
            _constantPitchMeters = constant.PitchMeters;
            _trackLengthTable = [];
            _turnsTable = [];
        }
        else
        {
            CheckedPitch(startRadiusMeters);
            CheckedPitch(maxRadiusMeters);

            double span = maxRadiusMeters - startRadiusMeters;
            _tableIntervalCount = Math.Max(1, (int)Math.Ceiling(span / MaxTableStepMeters));
            _tableStepMeters = span / _tableIntervalCount;
            _trackLengthTable = new double[_tableIntervalCount + 1];
            _turnsTable = new double[_tableIntervalCount + 1];

            // Running totals with Neumaier compensation, so that adding tens of thousands of small pieces
            // does not build up rounding error (the angle has to stay good to 1e-6 rad out of ~1e6 rad).
            double lengthSum = 0.0;
            double lengthCarry = 0.0;
            double turnsSum = 0.0;
            double turnsCarry = 0.0;
            for (int i = 0; i < _tableIntervalCount; i++)
            {
                double a = startRadiusMeters + i * _tableStepMeters;
                double b = startRadiusMeters + (i + 1) * _tableStepMeters;
                IntegrateInterval(a, b, validatePitch: true, out double pieceLength, out double pieceTurns);

                double t = lengthSum + pieceLength;
                lengthCarry += Math.Abs(lengthSum) >= Math.Abs(pieceLength)
                    ? (lengthSum - t) + pieceLength
                    : (pieceLength - t) + lengthSum;
                lengthSum = t;

                t = turnsSum + pieceTurns;
                turnsCarry += Math.Abs(turnsSum) >= Math.Abs(pieceTurns)
                    ? (turnsSum - t) + pieceTurns
                    : (pieceTurns - t) + turnsSum;
                turnsSum = t;

                _trackLengthTable[i + 1] = lengthSum + lengthCarry;
                _turnsTable[i + 1] = turnsSum + turnsCarry;
            }
        }

        TotalTrackLengthMeters = TrackLengthAtRadiusUnchecked(maxRadiusMeters);
    }

    /// <summary>Radius r0 where the spiral starts, in meters.</summary>
    public double StartRadiusMeters { get; }

    /// <summary>Largest usable radius, in meters.</summary>
    public double MaxRadiusMeters { get; }

    /// <summary>Length of track taken by one sector, in meters.</summary>
    public double SectorLengthMeters { get; }

    /// <summary>The logical block address that starts at the start radius.</summary>
    public long LbaAtStartRadius { get; }

    /// <summary>Angle θ0 of the track at the start radius, in radians.</summary>
    public double StartAngleRadians { get; }

    /// <summary>Track length from the start radius to the maximum radius, in meters.</summary>
    public double TotalTrackLengthMeters { get; }

    /// <summary>Track pitch in meters at the given radius in meters (start radius to maximum radius).</summary>
    public double PitchAtRadius(double radiusMeters)
    {
        RequireRadiusInRange(radiusMeters);
        return _isConstantPitch ? _constantPitchMeters : _pitch.PitchAt(radiusMeters);
    }

    /// <summary>Track length s in meters from the start radius to the given radius in meters.</summary>
    public double TrackLengthAtRadius(double radiusMeters)
    {
        RequireRadiusInRange(radiusMeters);
        return TrackLengthAtRadiusUnchecked(radiusMeters);
    }

    /// <summary>Radius in meters reached after the given track length in meters (0 to <see cref="TotalTrackLengthMeters"/>).</summary>
    public double RadiusAtTrackLength(double trackLengthMeters)
    {
        RequireTrackLengthInRange(trackLengthMeters);
        return RadiusAtTrackLengthUnchecked(trackLengthMeters);
    }

    /// <summary>
    /// Angle θ in radians of the track at the given radius in meters. Not wrapped: it decreases by 2π for
    /// every turn of the spiral (see <see cref="DiscOrientation"/>).
    /// </summary>
    public double AngleAtRadius(double radiusMeters)
    {
        RequireRadiusInRange(radiusMeters);
        return AngleAtRadiusUnchecked(radiusMeters);
    }

    /// <summary>Track length in meters from the start radius to a point inside the given sector.</summary>
    /// <param name="lba">Logical block address; must not be before <see cref="LbaAtStartRadius"/>.</param>
    /// <param name="fractionWithinSector">0 = start of the sector, 1 = its end.</param>
    public double TrackLengthAtLba(long lba, double fractionWithinSector = 0)
    {
        if (lba < LbaAtStartRadius)
        {
            throw new ArgumentOutOfRangeException(nameof(lba), lba,
                $"LBA is before the start of the spiral (LBA {LbaAtStartRadius}).");
        }
        // Written so that NaN also fails the test.
        if (!(fractionWithinSector >= 0.0 && fractionWithinSector <= 1.0))
        {
            throw new ArgumentOutOfRangeException(nameof(fractionWithinSector), fractionWithinSector,
                "Fraction within the sector must be between 0 and 1.");
        }
        double sectorsFromStart = (double)lba - (double)LbaAtStartRadius;
        return (sectorsFromStart + fractionWithinSector) * SectorLengthMeters;
    }

    /// <summary>Physical position (radius, angle) of a point inside the given sector.</summary>
    /// <param name="lba">Logical block address; must not be before <see cref="LbaAtStartRadius"/>.</param>
    /// <param name="fractionWithinSector">0 = start of the sector, 1 = its end.</param>
    public DiscPosition PositionAtLba(long lba, double fractionWithinSector = 0)
    {
        return PositionAtTrackLength(TrackLengthAtLba(lba, fractionWithinSector));
    }

    /// <summary>Physical position (radius, angle) after the given track length in meters.</summary>
    public DiscPosition PositionAtTrackLength(double trackLengthMeters)
    {
        RequireTrackLengthInRange(trackLengthMeters);
        double radius = RadiusAtTrackLengthUnchecked(trackLengthMeters);
        return new DiscPosition(radius, AngleAtRadiusUnchecked(radius));
    }

    /// <summary>
    /// The last LBA whose whole sector lies at or inside the given radius in meters.
    /// Returns <see cref="LbaAtStartRadius"/> − 1 when not even one sector fits.
    /// </summary>
    public long LastLbaInside(double radiusMeters)
    {
        RequireRadiusInRange(radiusMeters);
        double wholeSectors = Math.Floor(TrackLengthAtRadiusUnchecked(radiusMeters) / SectorLengthMeters);
        return LbaAtStartRadius + (long)wholeSectors - 1;
    }

    private double TrackLengthAtRadiusUnchecked(double radius)
    {
        if (_isConstantPitch)
        {
            // s = π(r² − r0²)/p, with the difference of squares factored to keep precision near r0.
            return Math.PI * (radius - StartRadiusMeters) * (radius + StartRadiusMeters) / _constantPitchMeters;
        }
        int i = TableIndexForRadius(radius);
        double nodeRadius = StartRadiusMeters + i * _tableStepMeters;
        IntegrateInterval(nodeRadius, radius, validatePitch: false, out double pieceLength, out _);
        return _trackLengthTable[i] + pieceLength;
    }

    private double AngleAtRadiusUnchecked(double radius)
    {
        double turns;
        if (_isConstantPitch)
        {
            turns = (radius - StartRadiusMeters) / _constantPitchMeters;
        }
        else
        {
            int i = TableIndexForRadius(radius);
            double nodeRadius = StartRadiusMeters + i * _tableStepMeters;
            IntegrateInterval(nodeRadius, radius, validatePitch: false, out _, out double pieceTurns);
            turns = _turnsTable[i] + pieceTurns;
        }
        return StartAngleRadians + DiscOrientation.TrackAngleSign * TwoPi * turns;
    }

    private double RadiusAtTrackLengthUnchecked(double trackLength)
    {
        double radius;
        if (_isConstantPitch)
        {
            // Closed form: r = sqrt(r0² + p·s/π).
            radius = Math.Sqrt(StartRadiusMeters * StartRadiusMeters + _constantPitchMeters * trackLength / Math.PI);
        }
        else
        {
            // Binary search for the table interval that contains this track length.
            int low = 0;
            int high = _tableIntervalCount - 1;
            while (low < high)
            {
                int middle = (low + high + 1) / 2;
                if (_trackLengthTable[middle] <= trackLength)
                {
                    low = middle;
                }
                else
                {
                    high = middle - 1;
                }
            }

            double intervalStart = StartRadiusMeters + low * _tableStepMeters;
            double intervalEnd = Math.Min(StartRadiusMeters + (low + 1) * _tableStepMeters, MaxRadiusMeters);
            double lengthAtStart = _trackLengthTable[low];
            double lengthOfInterval = _trackLengthTable[low + 1] - lengthAtStart;

            // First guess: straight line across the interval. Then Newton steps on s(r) − s* = 0,
            // using ds/dr = 2πr/p(r). The interval is only 1 µm wide, so two or three steps are enough.
            radius = intervalStart + _tableStepMeters * (trackLength - lengthAtStart) / lengthOfInterval;
            radius = Math.Clamp(radius, intervalStart, intervalEnd);
            for (int iteration = 0; iteration < 20; iteration++)
            {
                IntegrateInterval(intervalStart, radius, validatePitch: false, out double pieceLength, out _);
                double error = lengthAtStart + pieceLength - trackLength;
                double step = error * _pitch.PitchAt(radius) / (TwoPi * radius);
                radius = Math.Clamp(radius - step, intervalStart, intervalEnd);
                if (Math.Abs(step) < 1.0e-14)
                {
                    break;
                }
            }
        }

        // Guard against the last rounding step landing a hair outside the valid range.
        // (Inputs were already range-checked; this is not input clamping.)
        return Math.Clamp(radius, StartRadiusMeters, MaxRadiusMeters);
    }

    /// <summary>Index of the table node at or just below the given radius, never the last node.</summary>
    private int TableIndexForRadius(double radius)
    {
        int i = (int)((radius - StartRadiusMeters) / _tableStepMeters);
        if (i > _tableIntervalCount - 1)
        {
            i = _tableIntervalCount - 1;
        }
        return i;
    }

    /// <summary>
    /// Integrates over the radius range a..b: track length (∫ 2πr/p dr) and number of turns (∫ dr/p).
    /// Uses a 3-point Gauss-Legendre rule, which is far more exact than needed on a range of 1 µm or less.
    /// </summary>
    private void IntegrateInterval(double a, double b, bool validatePitch, out double trackLength, out double turns)
    {
        double half = 0.5 * (b - a);
        double middle = 0.5 * (a + b);
        double r1 = middle - half * GaussNode;
        double r3 = middle + half * GaussNode;

        double p1 = validatePitch ? CheckedPitch(r1) : _pitch.PitchAt(r1);
        double p2 = validatePitch ? CheckedPitch(middle) : _pitch.PitchAt(middle);
        double p3 = validatePitch ? CheckedPitch(r3) : _pitch.PitchAt(r3);

        double lengthSum = GaussWeightOuter * (r1 / p1) + GaussWeightCentre * (middle / p2) + GaussWeightOuter * (r3 / p3);
        double turnsSum = GaussWeightOuter / p1 + GaussWeightCentre / p2 + GaussWeightOuter / p3;
        trackLength = TwoPi * half * lengthSum;
        turns = half * turnsSum;
    }

    private double CheckedPitch(double radius)
    {
        double pitch = _pitch.PitchAt(radius);
        if (!double.IsFinite(pitch) || pitch <= 0.0)
        {
            throw new ArgumentOutOfRangeException("pitch", pitch,
                $"The pitch model returned {pitch} m at radius {radius} m. Pitch must be finite and greater than zero.");
        }
        return pitch;
    }

    private void RequireRadiusInRange(double radiusMeters)
    {
        // Written so that NaN also fails the test.
        if (!(radiusMeters >= StartRadiusMeters && radiusMeters <= MaxRadiusMeters))
        {
            throw new ArgumentOutOfRangeException(nameof(radiusMeters), radiusMeters,
                $"Radius must be between the start radius {StartRadiusMeters} m and the maximum radius {MaxRadiusMeters} m.");
        }
    }

    private void RequireTrackLengthInRange(double trackLengthMeters)
    {
        // Written so that NaN also fails the test.
        if (!(trackLengthMeters >= 0.0 && trackLengthMeters <= TotalTrackLengthMeters))
        {
            throw new ArgumentOutOfRangeException(nameof(trackLengthMeters), trackLengthMeters,
                $"Track length must be between 0 and {TotalTrackLengthMeters} m (the end of the spiral at the maximum radius).");
        }
    }
}
