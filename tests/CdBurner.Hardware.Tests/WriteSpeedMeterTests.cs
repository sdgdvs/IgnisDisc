using CdBurner.Core.Primitives;
using CdBurner.Hardware.Burning;
using CdBurner.Hardware.Interop;
using Xunit;

namespace CdBurner.Hardware.Tests;

/// <summary>
/// The speed a Windows-writer burn reports is now measured from its progress reports during the data-writing phase,
/// not taken from the speed that was asked for.
/// </summary>
public sealed class WriteSpeedMeterTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void Rate_IsTakenBetweenTheFirstAndLastReportsOfTheWritingPhase()
    {
        var meter = new WriteSpeedMeter();
        meter.Observe(writingData: false, 0, S(0));      // preparing the drive: ignored
        meter.Observe(writingData: false, 0, S(20));     // calibrating: ignored
        meter.Observe(writingData: true, 750, S(30));
        meter.Observe(writingData: true, 4_500, S(80));
        meter.Observe(writingData: true, 8_250, S(130));
        meter.Observe(writingData: false, 8_250, S(180)); // closing the disc: ignored

        Assert.Equal(75.0, meter.SectorsPerSecond!.Value, precision: 6); // 7,500 sectors in 100 s
    }

    [Fact]
    public void TooLittleToMeasure_GivesNoRate()
    {
        var meter = new WriteSpeedMeter();
        Assert.Null(meter.SectorsPerSecond);

        meter.Observe(writingData: true, 100, S(5));
        Assert.Null(meter.SectorsPerSecond);             // one report

        meter.Observe(writingData: true, 200, S(5.5));
        Assert.Null(meter.SectorsPerSecond);             // less than a second apart

        var stalled = new WriteSpeedMeter();
        stalled.Observe(writingData: true, 100, S(5));
        stalled.Observe(writingData: true, 100, S(9));
        Assert.Null(stalled.SectorsPerSecond);           // no progress
    }

    [Fact]
    public void ReportsThatGoBackward_DoNotLowerTheLastPoint()
    {
        var meter = new WriteSpeedMeter();
        meter.Observe(writingData: true, 1_000, S(10));
        meter.Observe(writingData: true, 2_000, S(20));
        meter.Observe(writingData: true, 1_500, S(25)); // out of order: ignored as the end point

        Assert.Equal(100.0, meter.SectorsPerSecond!.Value, precision: 6);
    }

    [Theory]
    [InlineData(DiscMediaType.CdR, 75.0, 1.0)]                     // CD 1x = 75 data sectors a second
    [InlineData(DiscMediaType.CdR, 600.0, 8.0)]
    [InlineData(DiscMediaType.DvdMinusR, 1_385_000.0 / 2048, 1.0)]  // DVD 1x = 1,385,000 bytes a second
    [InlineData(DiscMediaType.DvdPlusRw, 4 * 1_385_000.0 / 2048, 4.0)]
    [InlineData(DiscMediaType.BdR, 6 * 4_495_500.0 / 2048, 6.0)]    // Blu-ray 1x = 4,495,500 bytes a second
    public void DataMultiplier_UsesEachDiscsOwn1x(DiscMediaType type, double sectorsPerSecond, double expected)
    {
        Assert.Equal(expected, WriteSpeedMeter.DataMultiplier(sectorsPerSecond, type), precision: 6);
    }

    [Fact]
    public void WholeBurnRate_IsSectorsOverTime_AndZeroWhenThereIsNothingToDivide()
    {
        Assert.Equal(50.0, WriteSpeedMeter.WholeBurnSectorsPerSecond(5_000, S(100)), precision: 6);
        Assert.Equal(0.0, WriteSpeedMeter.WholeBurnSectorsPerSecond(5_000, TimeSpan.Zero));
        Assert.Equal(0.0, WriteSpeedMeter.WholeBurnSectorsPerSecond(0, S(100)));
    }

    [Fact]
    public void EventSinkSnapshots_NameTheWritingPhaseTheWayTheMeterExpects()
    {
        // Imapi2BurnEngine feeds the meter only snapshots whose action text is "WritingData".
        var writing = DiscFormatDataEventsSink.CreateSnapshot(30, 60, 0, 10_000, 4_000, 3_999, 0, 0, 0, ImapiFormat2DataWriteAction.WritingData);
        var closing = DiscFormatDataEventsSink.CreateSnapshot(90, 0, 0, 10_000, 10_000, 9_999, 0, 0, 0, ImapiFormat2DataWriteAction.Finalization);

        Assert.Equal(nameof(ImapiFormat2DataWriteAction.WritingData), writing.CurrentActionDescription);
        Assert.Equal(4_000, writing.CurrentLba);
        Assert.NotEqual(nameof(ImapiFormat2DataWriteAction.WritingData), closing.CurrentActionDescription);
    }
}
