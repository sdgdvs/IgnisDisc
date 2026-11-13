namespace CdBurner.Core.OpticalLithography;

/// <summary>
/// Track pitch (radial distance between neighbouring turns of the spiral) as a function of radius.
/// </summary>
public interface ITrackPitchModel
{
    /// <summary>Track pitch in meters at the given radius in meters. Must be positive. Must not allocate.</summary>
    double PitchAt(double radiusMeters);
}

/// <summary>The same track pitch at every radius (the nominal case for CD, DVD and BD).</summary>
public sealed class ConstantTrackPitch : ITrackPitchModel
{
    /// <summary>Creates a constant pitch model.</summary>
    /// <param name="pitchMeters">Track pitch in meters; must be finite and greater than zero.</param>
    public ConstantTrackPitch(double pitchMeters)
    {
        if (!double.IsFinite(pitchMeters) || pitchMeters <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(pitchMeters), pitchMeters,
                "Track pitch must be a finite number greater than zero (meters).");
        }
        PitchMeters = pitchMeters;
    }

    /// <summary>The track pitch in meters.</summary>
    public double PitchMeters { get; }

    /// <inheritdoc />
    public double PitchAt(double radiusMeters)
    {
        return PitchMeters;
    }
}

/// <summary>
/// Track pitch that varies with radius as a cubic polynomial:
/// p(r) = a0 + a1·x + a2·x² + a3·x³ with x = r − rRef.
/// Using x (a few centimetres at most) instead of r keeps the coefficients well conditioned.
/// </summary>
public sealed class CubicTrackPitch : ITrackPitchModel
{
    private readonly double _a0;
    private readonly double _a1;
    private readonly double _a2;
    private readonly double _a3;

    /// <summary>Creates a cubic pitch model and checks that the pitch is positive over its whole radius range.</summary>
    /// <param name="a0">Pitch at the reference radius, in meters.</param>
    /// <param name="a1">Linear coefficient (meters of pitch per meter of radius, so unitless).</param>
    /// <param name="a2">Quadratic coefficient, in 1/meters.</param>
    /// <param name="a3">Cubic coefficient, in 1/meters².</param>
    /// <param name="referenceRadiusMeters">The radius rRef where x = 0, in meters.</param>
    /// <param name="minRadiusMeters">Smallest radius the model is valid for, in meters.</param>
    /// <param name="maxRadiusMeters">Largest radius the model is valid for, in meters.</param>
    public CubicTrackPitch(
        double a0,
        double a1,
        double a2,
        double a3,
        double referenceRadiusMeters,
        double minRadiusMeters,
        double maxRadiusMeters)
    {
        RequireFinite(a0, nameof(a0));
        RequireFinite(a1, nameof(a1));
        RequireFinite(a2, nameof(a2));
        RequireFinite(a3, nameof(a3));
        RequireFinite(referenceRadiusMeters, nameof(referenceRadiusMeters));
        if (!double.IsFinite(minRadiusMeters) || minRadiusMeters <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(minRadiusMeters), minRadiusMeters,
                "Minimum radius must be a finite number greater than zero (meters).");
        }
        if (!double.IsFinite(maxRadiusMeters) || maxRadiusMeters <= minRadiusMeters)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRadiusMeters), maxRadiusMeters,
                "Maximum radius must be finite and greater than the minimum radius (meters).");
        }

        _a0 = a0;
        _a1 = a1;
        _a2 = a2;
        _a3 = a3;
        ReferenceRadiusMeters = referenceRadiusMeters;
        MinRadiusMeters = minRadiusMeters;
        MaxRadiusMeters = maxRadiusMeters;

        // A cubic has its lowest value on a range either at one of the two ends or where its slope is zero.
        // Checking those (at most four) points proves the pitch is positive everywhere in the range.
        double xMin = minRadiusMeters - referenceRadiusMeters;
        double xMax = maxRadiusMeters - referenceRadiusMeters;
        RequirePositivePitchAt(xMin);
        RequirePositivePitchAt(xMax);

        // Slope: a1 + 2·a2·x + 3·a3·x² = 0.
        double qa = 3.0 * a3;
        double qb = 2.0 * a2;
        double qc = a1;
        if (qa != 0.0)
        {
            double discriminant = qb * qb - 4.0 * qa * qc;
            if (discriminant >= 0.0)
            {
                double root = Math.Sqrt(discriminant);
                CheckTurningPoint((-qb + root) / (2.0 * qa), xMin, xMax);
                CheckTurningPoint((-qb - root) / (2.0 * qa), xMin, xMax);
            }
        }
        else if (qb != 0.0)
        {
            CheckTurningPoint(-qc / qb, xMin, xMax);
        }
    }

    /// <summary>The radius rRef where x = 0, in meters.</summary>
    public double ReferenceRadiusMeters { get; }

    /// <summary>Smallest radius the model is valid for, in meters.</summary>
    public double MinRadiusMeters { get; }

    /// <summary>Largest radius the model is valid for, in meters.</summary>
    public double MaxRadiusMeters { get; }

    /// <inheritdoc />
    public double PitchAt(double radiusMeters)
    {
        // Written so that NaN also fails the test.
        if (!(radiusMeters >= MinRadiusMeters && radiusMeters <= MaxRadiusMeters))
        {
            throw new ArgumentOutOfRangeException(nameof(radiusMeters), radiusMeters,
                $"Radius is outside the pitch model's range of {MinRadiusMeters} to {MaxRadiusMeters} meters.");
        }
        return Evaluate(radiusMeters - ReferenceRadiusMeters);
    }

    private double Evaluate(double x)
    {
        return _a0 + x * (_a1 + x * (_a2 + x * _a3));
    }

    private void CheckTurningPoint(double x, double xMin, double xMax)
    {
        if (x > xMin && x < xMax)
        {
            RequirePositivePitchAt(x);
        }
    }

    private void RequirePositivePitchAt(double x)
    {
        double pitch = Evaluate(x);
        if (!double.IsFinite(pitch) || pitch <= 0.0)
        {
            throw new ArgumentOutOfRangeException("a0",
                $"The cubic pitch model gives a pitch of {pitch} m at radius {x + ReferenceRadiusMeters} m. "
                + "Pitch must be greater than zero over the whole radius range.");
        }
    }

    private static void RequireFinite(double value, string name)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(name, value, "Value must be a finite number.");
        }
    }
}
