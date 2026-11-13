using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CdBurner.Core.Verification;

/// <summary>One sample of the write-speed curve: how far into the disc the burn was (0-100) and the speed then (x).</summary>
public sealed record TransferCurvePoint(double Percent, double SpeedX);

/// <summary>The counts of one read-health pass (see <see cref="DiscHealthReport"/>) as kept in a certificate.</summary>
public sealed record CertificateScan(
    string Verdict,
    int SectorsRead,
    int FlaggedSectors,
    int UnreadableSectors,
    bool C2Available,
    IReadOnlyList<int> ProblemsPerMinute,
    IReadOnlyList<TrackHealth> Tracks);

/// <summary>
/// A record of one archive disc made right after it was burned: the read-back result, the write-speed curve, the
/// drive, the media ID, the date and (when the drive could read the disc once more) the per-minute counts of a
/// read-health pass. Written only when asked for. A later <c>check-disc --compare</c> against it shows ageing.
/// </summary>
public sealed record DiscHealthCertificate(
    int SchemaVersion,
    DateTimeOffset CreatedUtc,
    string? SetId,
    int DiscNumber,
    string? VolumeLabel,
    string? Drive,
    string? MediaType,
    string? MediaId,
    int SectorsWritten,
    double AverageSpeedX,
    bool Verified,
    string? VerificationNote,
    IReadOnlyList<TransferCurvePoint> TransferCurve,
    CertificateScan? Scan)
{
    public const int CurrentSchemaVersion = 1;
    public const string JsonFileSuffix = "HEALTH.json";
    public const string SvgFileSuffix = "HEALTH.svg";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    /// <summary>Makes the scan section from a finished read-health pass.</summary>
    public static CertificateScan ScanFrom(DiscHealthReport report) => new(
        report.Verdict.ToString(), report.TotalSectors, report.FlaggedSectors, report.UnreadableSectors,
        report.C2Available, report.ProblemsPerMinute.ToArray(), report.Tracks.ToArray());

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <exception cref="FormatException">The text is not a certificate this version understands.</exception>
    public static DiscHealthCertificate Parse(string json)
    {
        DiscHealthCertificate? cert;
        try
        {
            cert = JsonSerializer.Deserialize<DiscHealthCertificate>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"Not a health certificate: {ex.Message}", ex);
        }
        if (cert == null)
        {
            throw new FormatException("Not a health certificate: the file is empty.");
        }
        if (cert.SchemaVersion != CurrentSchemaVersion)
        {
            throw new FormatException($"Health certificate schema {cert.SchemaVersion} is not supported (this version reads schema {CurrentSchemaVersion}).");
        }
        if (cert.TransferCurve == null)
        {
            throw new FormatException("Not a health certificate: no transfer curve.");
        }
        return cert;
    }

    public static DiscHealthCertificate Load(string path) => Parse(File.ReadAllText(path, Encoding.UTF8));

    /// <summary>Writes the JSON and the SVG chart. Each file is written whole to a temp name first, then moved into place.</summary>
    public void Save(string jsonPath, string svgPath)
    {
        WriteAtomic(jsonPath, ToJson());
        WriteAtomic(svgPath, ToSvg());
    }

    private static void WriteAtomic(string path, string text)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>
    /// Compares a later read-health pass with the one in this certificate, minute by minute of the disc.
    /// </summary>
    /// <exception cref="InvalidOperationException">The certificate holds no read-health counts.</exception>
    public HealthComparison Compare(DiscHealthReport now)
    {
        if (Scan == null)
        {
            throw new InvalidOperationException("This certificate has no check-disc counts to compare with (the disc could not be read after the burn).");
        }
        return HealthComparison.Between(Scan, now);
    }

    // ---- SVG chart -------------------------------------------------------------------------------------------

    private const int SvgWidth = 720;
    private const int PlotLeft = 56, PlotRight = 700;
    private const int SpeedTop = 40, SpeedBottom = 160;
    private const int MinuteTop = 220, MinuteBottom = 340;

    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Esc(string? s) => System.Security.SecurityElement.Escape(s ?? "") ?? "";

    /// <summary>
    /// A plain chart of the certificate: the write-speed curve above, problem sectors per minute of the disc below.
    /// Text only from the certificate's own fields; the same input always gives the same bytes.
    /// </summary>
    public string ToSvg()
    {
        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{SvgWidth}\" height=\"380\" viewBox=\"0 0 {SvgWidth} 380\" font-family=\"monospace\" font-size=\"11\">\n");
        sb.Append($"<rect width=\"{SvgWidth}\" height=\"380\" fill=\"#ffffff\"/>\n");
        string title = $"{SetId ?? "disc"} disc {DiscNumber}: {(Verified ? "read-back passed" : "not read back")}";
        sb.Append($"<text x=\"{PlotLeft}\" y=\"16\" font-size=\"13\" fill=\"#111111\">{Esc(title)}</text>\n");
        sb.Append($"<text x=\"{PlotLeft}\" y=\"30\" fill=\"#555555\">{Esc($"{CreatedUtc:yyyy-MM-dd} UTC, {Drive ?? "unknown drive"}, media {MediaId ?? "unknown"}")}</text>\n");

        // Write-speed curve
        sb.Append($"<text x=\"{PlotLeft}\" y=\"{SpeedTop - 4}\" fill=\"#333333\">Write speed (x) along the disc (%)</text>\n");
        sb.Append($"<rect x=\"{PlotLeft}\" y=\"{SpeedTop}\" width=\"{PlotRight - PlotLeft}\" height=\"{SpeedBottom - SpeedTop}\" fill=\"none\" stroke=\"#999999\"/>\n");
        double maxSpeed = TransferCurve.Count == 0 ? 1 : Math.Max(1, Math.Ceiling(TransferCurve.Max(p => p.SpeedX)));
        sb.Append($"<text x=\"4\" y=\"{SpeedTop + 4}\" fill=\"#555555\">{N(maxSpeed)}x</text>\n");
        sb.Append($"<text x=\"4\" y=\"{SpeedBottom}\" fill=\"#555555\">0x</text>\n");
        if (TransferCurve.Count > 0)
        {
            var pts = TransferCurve.Select(p =>
            {
                double x = PlotLeft + Math.Clamp(p.Percent, 0, 100) / 100.0 * (PlotRight - PlotLeft);
                double y = SpeedBottom - Math.Clamp(p.SpeedX, 0, maxSpeed) / maxSpeed * (SpeedBottom - SpeedTop);
                return $"{N(x)},{N(y)}";
            });
            sb.Append($"<polyline fill=\"none\" stroke=\"#c2410c\" stroke-width=\"1.5\" points=\"{string.Join(' ', pts)}\"/>\n");
        }

        // Problem sectors per minute
        sb.Append($"<text x=\"{PlotLeft}\" y=\"{MinuteTop - 4}\" fill=\"#333333\">Problem sectors per minute of the disc</text>\n");
        sb.Append($"<rect x=\"{PlotLeft}\" y=\"{MinuteTop}\" width=\"{PlotRight - PlotLeft}\" height=\"{MinuteBottom - MinuteTop}\" fill=\"none\" stroke=\"#999999\"/>\n");
        if (Scan is { ProblemsPerMinute.Count: > 0 } scan)
        {
            int max = Math.Max(1, scan.ProblemsPerMinute.Max());
            double slot = (double)(PlotRight - PlotLeft) / scan.ProblemsPerMinute.Count;
            sb.Append($"<text x=\"4\" y=\"{MinuteTop + 4}\" fill=\"#555555\">{max}</text>\n");
            sb.Append($"<text x=\"4\" y=\"{MinuteBottom}\" fill=\"#555555\">0</text>\n");
            for (int m = 0; m < scan.ProblemsPerMinute.Count; m++)
            {
                int n = scan.ProblemsPerMinute[m];
                if (n <= 0) continue;
                double h = (double)n / max * (MinuteBottom - MinuteTop);
                sb.Append($"<rect x=\"{N(PlotLeft + m * slot)}\" y=\"{N(MinuteBottom - h)}\" width=\"{N(Math.Max(1, slot - 1))}\" height=\"{N(h)}\" fill=\"#b91c1c\"/>\n");
            }
            sb.Append($"<text x=\"{PlotLeft}\" y=\"{MinuteBottom + 16}\" fill=\"#333333\">{Esc($"{scan.Verdict}: {scan.FlaggedSectors} flagged, {scan.UnreadableSectors} unreadable of {scan.SectorsRead} sectors; minute 0 to {scan.ProblemsPerMinute.Count - 1}")}</text>\n");
        }
        else
        {
            sb.Append($"<text x=\"{PlotLeft + 8}\" y=\"{MinuteTop + 20}\" fill=\"#555555\">no check-disc counts (the disc was not read after the burn)</text>\n");
        }
        sb.Append("</svg>\n");
        return sb.ToString();
    }
}

/// <summary>One minute of the disc whose problem count differs between two read-health passes.</summary>
public sealed record MinuteChange(int Minute, int Before, int After);

/// <summary>A later read-health pass set against the one in a health certificate.</summary>
public sealed record HealthComparison(
    string VerdictBefore,
    string VerdictAfter,
    int ProblemsBefore,
    int ProblemsAfter,
    bool SameLength,
    IReadOnlyList<MinuteChange> Changes)
{
    public static HealthComparison Between(CertificateScan before, DiscHealthReport now)
    {
        var a = before.ProblemsPerMinute;
        var b = now.ProblemsPerMinute;
        var changes = new List<MinuteChange>();
        for (int m = 0; m < Math.Max(a.Count, b.Count); m++)
        {
            int x = m < a.Count ? a[m] : 0;
            int y = m < b.Count ? b[m] : 0;
            if (x != y) changes.Add(new MinuteChange(m, x, y));
        }
        return new HealthComparison(before.Verdict, now.Verdict.ToString(),
            before.FlaggedSectors + before.UnreadableSectors, now.FlaggedSectors + now.UnreadableSectors,
            a.Count == b.Count, changes);
    }

    /// <summary>True when any minute got worse or any sector became unreadable.</summary>
    public bool Worse => Changes.Any(c => c.After > c.Before);

    public IReadOnlyList<string> Describe()
    {
        var lines = new List<string>
        {
            $"Against the certificate: {VerdictBefore} then, {VerdictAfter} now; problem sectors {ProblemsBefore:N0} then, {ProblemsAfter:N0} now."
        };
        if (!SameLength)
        {
            lines.Add("The disc is not the same length as when the certificate was made: this may be another disc.");
        }
        if (Changes.Count == 0)
        {
            lines.Add("No minute of the disc changed.");
            return lines;
        }
        foreach (var c in Changes)
        {
            string word = c.After > c.Before ? "worse" : "better";
            lines.Add($"  Minute {c.Minute}: {c.Before} -> {c.After} ({word})");
        }
        return lines;
    }
}
