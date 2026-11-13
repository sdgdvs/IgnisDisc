using CdBurner.Core.Abstractions;

namespace CdBurner.Core.Verification;

/// <summary>
/// Keeps the write speed a burn reported along the disc: one sample per whole percent (the last one seen in that
/// percent), so the curve has at most 101 points however many progress reports come. Snapshots without a speed or a
/// size (phases that are not writing) are ignored. Passes every snapshot on to <paramref name="inner"/>.
/// </summary>
public sealed class TransferCurveRecorder : IProgress<BurnProgressSnapshot>
{
    private readonly IProgress<BurnProgressSnapshot>? _inner;
    private readonly SortedDictionary<int, double> _byPercent = new();
    private readonly object _lock = new();

    public TransferCurveRecorder(IProgress<BurnProgressSnapshot>? inner = null) => _inner = inner;

    public void Report(BurnProgressSnapshot value)
    {
        if (value.TotalLba > 0 && value.CurrentLba >= 0 && value.CurrentSpeedMultiplier > 0 && !double.IsNaN(value.CurrentSpeedMultiplier))
        {
            int percent = (int)Math.Clamp(Math.Round(100.0 * value.CurrentLba / value.TotalLba), 0, 100);
            lock (_lock) { _byPercent[percent] = Math.Round(value.CurrentSpeedMultiplier, 2); }
        }
        _inner?.Report(value);
    }

    public IReadOnlyList<TransferCurvePoint> Curve
    {
        get
        {
            lock (_lock) { return _byPercent.Select(kv => new TransferCurvePoint(kv.Key, kv.Value)).ToList(); }
        }
    }
}
