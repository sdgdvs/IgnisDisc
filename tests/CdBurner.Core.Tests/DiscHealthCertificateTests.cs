using System.Text.Json;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Verification;

namespace CdBurner.Core.Tests;

/// <summary>The health certificate written after an archive burn: its JSON schema, its SVG chart, and the comparison with a later read.</summary>
public class DiscHealthCertificateTests
{
    private static DiscHealthCertificate Sample(CertificateScan? scan = null) => new(
        SchemaVersion: 1,
        CreatedUtc: new DateTimeOffset(2026, 11, 13, 9, 30, 0, TimeSpan.Zero),
        SetId: "abc123def456",
        DiscNumber: 2,
        VolumeLabel: "ARCABC123_02",
        Drive: "TEST DRIVE X1",
        MediaType: "CdR",
        MediaId: "97:26.66 (Example Media)",
        SectorsWritten: 1000,
        AverageSpeedX: 10.5,
        Verified: true,
        VerificationNote: "12 files matched",
        TransferCurve: [new TransferCurvePoint(0, 4), new TransferCurvePoint(50, 8), new TransferCurvePoint(100, 10)],
        Scan: scan ?? new CertificateScan("Weak", 4000, 3, 0, true, [0, 2, 0, 1], [new TrackHealth(1, false, 0, 4000, 3, 0)]));

    private static DiscHealthReport Report(params int[] perMinute)
    {
        int flagged = perMinute.Sum();
        return new DiscHealthReport(4000, flagged, 0, true, [new TrackHealth(1, false, 0, 4000, flagged, 0)], perMinute, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Json_HasTheDocumentedFields_AndRoundTrips()
    {
        var cert = Sample();
        using var doc = JsonDocument.Parse(cert.ToJson());
        var root = doc.RootElement;

        string[] names = root.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(["schemaVersion", "createdUtc", "setId", "discNumber", "volumeLabel", "drive", "mediaType", "mediaId",
            "sectorsWritten", "averageSpeedX", "verified", "verificationNote", "transferCurve", "scan"], names);
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("2026-11-13T09:30:00+00:00", root.GetProperty("createdUtc").GetString());
        Assert.Equal(3, root.GetProperty("transferCurve").GetArrayLength());
        Assert.Equal(8, root.GetProperty("transferCurve")[1].GetProperty("speedX").GetDouble());
        var scan = root.GetProperty("scan");
        Assert.Equal(["verdict", "sectorsRead", "flaggedSectors", "unreadableSectors", "c2Available", "problemsPerMinute", "tracks"],
            scan.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal([0, 2, 0, 1], scan.GetProperty("problemsPerMinute").EnumerateArray().Select(e => e.GetInt32()).ToArray());

        var back = DiscHealthCertificate.Parse(cert.ToJson());
        Assert.Equal(cert.CreatedUtc, back.CreatedUtc);
        Assert.Equal(cert.MediaId, back.MediaId);
        Assert.Equal(cert.TransferCurve, back.TransferCurve);
        Assert.Equal(cert.Scan!.ProblemsPerMinute, back.Scan!.ProblemsPerMinute);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{\"schemaVersion\":2,\"transferCurve\":[]}")]
    [InlineData("{\"schemaVersion\":1}")]
    public void Parse_RejectsWhatIsNotACertificate(string text)
    {
        Assert.Throws<FormatException>(() => DiscHealthCertificate.Parse(text));
    }

    [Fact]
    public void Svg_MatchesTheReviewedSnapshot()
    {
        string expected = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Health", "sample-certificate.svg"));
        Assert.Equal(expected.ReplaceLineEndings("\n"), Sample().ToSvg().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Svg_IsWellFormedXml_AndPlacesTheBarsWhereTheCountsSay()
    {
        var doc = System.Xml.Linq.XDocument.Parse(Sample().ToSvg());
        System.Xml.Linq.XNamespace ns = "http://www.w3.org/2000/svg";
        var bars = doc.Descendants(ns + "rect").Where(r => (string?)r.Attribute("fill") == "#b91c1c").ToList();

        // Minutes 1 (2 sectors) and 3 (1 sector) have problems; the tallest bar fills the plot (220..340 = 120 high).
        Assert.Equal(2, bars.Count);
        Assert.Equal(120, (double)bars[0].Attribute("height")!);
        Assert.Equal(60, (double)bars[1].Attribute("height")!);
        // Four minutes across 56..700 is 161 wide each: minute 1 starts at 56 + 161 and minute 3 at 56 + 3 * 161.
        Assert.Equal(217, (double)bars[0].Attribute("x")!);
        Assert.Equal(539, (double)bars[1].Attribute("x")!);
        // The curve ends at the right edge, at the top (10x is the axis maximum).
        string points = (string)doc.Descendants(ns + "polyline").Single().Attribute("points")!;
        Assert.EndsWith("700,40", points);
        Assert.StartsWith("56,112", points); // 4x of 10x: 160 - 0.4 * 120
    }

    [Fact]
    public void Svg_WithoutACheck_SaysSoAndDrawsNoBars()
    {
        var cert = Sample() with { Scan = null };
        string svg = cert.ToSvg();
        Assert.Contains("no check-disc counts", svg);
        Assert.DoesNotContain("#b91c1c", svg);
    }

    [Fact]
    public void Svg_EscapesTextFromTheCertificate()
    {
        string svg = (Sample() with { Drive = "A<B>&\"C" }).ToSvg();
        System.Xml.Linq.XDocument.Parse(svg); // throws if it is not well formed
        Assert.DoesNotContain("A<B>", svg);
    }

    [Fact]
    public void Compare_ReportsEachMinuteThatChanged()
    {
        var cert = Sample(); // minutes 0..3: 0, 2, 0, 1
        var comparison = cert.Compare(Report(0, 5, 0, 0));

        Assert.True(comparison.SameLength);
        Assert.Equal([new MinuteChange(1, 2, 5), new MinuteChange(3, 1, 0)], comparison.Changes);
        Assert.True(comparison.Worse);
        Assert.Equal(3, comparison.ProblemsBefore);
        Assert.Equal(5, comparison.ProblemsAfter);
        Assert.Equal("Weak", comparison.VerdictBefore);
        string text = string.Join("\n", comparison.Describe());
        Assert.Contains("Minute 1: 2 -> 5 (worse)", text);
        Assert.Contains("Minute 3: 1 -> 0 (better)", text);
    }

    [Fact]
    public void Compare_WithNothingChanged_SaysSo_AndWithAnotherLengthWarns()
    {
        var cert = Sample();
        var same = cert.Compare(Report(0, 2, 0, 1));
        Assert.Empty(same.Changes);
        Assert.False(same.Worse);
        Assert.Contains("No minute of the disc changed.", same.Describe());

        var longer = cert.Compare(Report(0, 2, 0, 1, 4));
        Assert.False(longer.SameLength);
        Assert.Equal([new MinuteChange(4, 0, 4)], longer.Changes);
        Assert.Contains(longer.Describe(), l => l.Contains("another disc"));
    }

    [Fact]
    public void Compare_WithoutStoredCounts_IsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => (Sample() with { Scan = null }).Compare(Report(0)));
    }

    [Fact]
    public void Save_WritesBothFilesWhole_AndLoadReadsThemBack()
    {
        string dir = Directory.CreateTempSubdirectory("health-cert").FullName;
        try
        {
            string json = Path.Combine(dir, "disc02.HEALTH.json");
            string svg = Path.Combine(dir, "disc02.HEALTH.svg");
            Sample().Save(json, svg);
            Sample().Save(json, svg); // saving again replaces them

            Assert.True(File.Exists(svg));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
            Assert.Equal("abc123def456", DiscHealthCertificate.Load(json).SetId);
            Assert.False(File.ReadAllBytes(json).AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })); // no byte order mark
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void CurveRecorder_KeepsOneSamplePerPercent_AndIgnoresPhasesThatAreNotWriting()
    {
        var recorder = new TransferCurveRecorder();
        void Report(int lba, double speed) => recorder.Report(new BurnProgressSnapshot { CurrentLba = lba, TotalLba = 1000, CurrentSpeedMultiplier = speed });

        Report(0, 4);
        Report(2, 4.5);     // rounds into percent 0 again: the last one wins
        Report(500, 8);
        Report(1000, 10);
        Report(600, 0);     // no speed reported: not a writing phase
        recorder.Report(new BurnProgressSnapshot { CurrentLba = 5, TotalLba = 0, CurrentSpeedMultiplier = 9 }); // no size

        Assert.Equal([new TransferCurvePoint(0, 4.5), new TransferCurvePoint(50, 8), new TransferCurvePoint(100, 10)], recorder.Curve);
    }
}
