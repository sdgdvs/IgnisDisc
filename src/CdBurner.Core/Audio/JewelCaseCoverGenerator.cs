using System.Net;
using System.Text;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Audio;

/// <summary>
/// Generates a print-ready, self-contained HTML5/CSS3/SVG CD Jewel Case Booklet Cover (120mm x 120mm),
/// Back Tray Inlay Card (150mm x 118mm with dual 6mm spines), and Tracklist Mastering Sheet
/// from an <see cref="AudioCdCompilation"/>.
/// </summary>
public static class JewelCaseCoverGenerator
{
    /// <summary>
    /// Generates and writes a printable CD Jewel Case Insert &amp; Tracklist HTML file to disk.
    /// </summary>
    public static void SavePrintableCoverHtml(
        AudioCdCompilation compilation,
        string outputHtmlPath,
        DateTimeOffset? generatedAt = null)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputHtmlPath);

        string? dir = Path.GetDirectoryName(outputHtmlPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string html = GeneratePrintableCoverHtml(compilation, generatedAt);
        File.WriteAllText(outputHtmlPath, html, Encoding.UTF8);
    }

    /// <summary>
    /// Generates a self-contained HTML document with exact millimeter CSS print geometry (`@page`, `120mm x 120mm` front cover,
    /// `150mm x 118mm` rear tray card with `6mm` left/right spines) and a clean print button.
    /// </summary>
    public static string GeneratePrintableCoverHtml(
        AudioCdCompilation compilation,
        DateTimeOffset? generatedAt = null)
    {
        ArgumentNullException.ThrowIfNull(compilation);

        string albumTitle = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(compilation.AlbumTitle) ? "Untitled Album" : compilation.AlbumTitle);
        string performer = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(compilation.AlbumPerformer) ? "Various Artists" : compilation.AlbumPerformer);
        string songwriter = WebUtility.HtmlEncode(compilation.Songwriter ?? string.Empty);
        string upcEan = WebUtility.HtmlEncode(compilation.UpcEan ?? string.Empty);
        string totalMsf = compilation.TotalDurationMsf.ToString();
        string recMode = compilation.RecordingMode == AudioRecordingMode.DiscAtOnceGapless
            ? "Disc-at-Once (Gapless Red Book CD-DA)"
            : "Track-at-Once (TAO)";
        string timestamp = (generatedAt ?? DateTimeOffset.Now).ToString("yyyy-MM-dd HH:mm");

        var rowsBuilder = new StringBuilder();
        var trayRowsBuilder = new StringBuilder();

        foreach (var track in compilation.Tracks)
        {
            string trkTitle = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(track.Title) ? $"Track {track.TrackNumber:D2}" : track.Title);
            string trkPerformer = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(track.Performer) ? compilation.AlbumPerformer : track.Performer);
            string trkDuration = track.DurationMsf.ToString();
            string trkIsrc = WebUtility.HtmlEncode(track.Isrc ?? "—");
            string preGapSec = $"{(track.PreGapFrames / 75.0):F2}s";

            rowsBuilder.AppendLine($"""
                        <tr>
                          <td class="num">{track.TrackNumber:D2}</td>
                          <td class="title">{trkTitle}</td>
                          <td class="artist">{trkPerformer}</td>
                          <td class="isrc">{trkIsrc}</td>
                          <td class="pregap">{preGapSec}</td>
                          <td class="dur">{trkDuration}</td>
                        </tr>
                """);

            trayRowsBuilder.AppendLine($"""
                        <div class="tray-track">
                          <span class="t-num">{track.TrackNumber:D2}.</span>
                          <span class="t-title">{trkTitle}</span>
                          <span class="t-dots"></span>
                          <span class="t-dur">{trkDuration}</span>
                        </div>
                """);
        }

        string metaExtras = string.Empty;
        if (!string.IsNullOrWhiteSpace(songwriter))
        {
            metaExtras += $"<span><strong>Songwriter:</strong> {songwriter}</span> ";
        }
        if (!string.IsNullOrWhiteSpace(upcEan))
        {
            metaExtras += $"<span><strong>UPC/EAN:</strong> {upcEan}</span> ";
        }

        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8" />
              <title>{{albumTitle}} — {{performer}} (CD Jewel Case Insert &amp; Tracklist)</title>
              <style>
                :root {
                  --ink: #111827;
                  --muted: #4b5563;
                  --accent: #d97706;
                  --border: #9ca3af;
                  --bg: #f3f4f6;
                }
                * { box-sizing: border-box; }
                body {
                  margin: 0;
                  padding: 24px;
                  font-family: "Segoe UI", -apple-system, BlinkMacSystemFont, sans-serif;
                  color: var(--ink);
                  background: var(--bg);
                }
                .toolbar {
                  max-width: 210mm;
                  margin: 0 auto 20px auto;
                  display: flex;
                  justify-content: space-between;
                  align-items: center;
                  background: #1f2937;
                  color: #f9fafb;
                  padding: 12px 18px;
                  border-radius: 8px;
                }
                .toolbar button {
                  background: #f59e0b;
                  color: #111827;
                  border: none;
                  padding: 8px 18px;
                  font-weight: 700;
                  font-size: 14px;
                  border-radius: 6px;
                  cursor: pointer;
                }
                .sheet {
                  max-width: 210mm;
                  margin: 0 auto;
                  background: #ffffff;
                  padding: 14mm;
                  box-shadow: 0 4px 16px rgba(0,0,0,0.08);
                }
                h2.section-label {
                  font-size: 11px;
                  text-transform: uppercase;
                  letter-spacing: 1.2px;
                  color: var(--muted);
                  margin: 0 0 4mm 0;
                }
                /* Standard CD Front Booklet Insert: 120mm x 120mm */
                .front-booklet {
                  width: 120mm;
                  height: 120mm;
                  border: 0.4mm dashed var(--border);
                  padding: 10mm;
                  display: flex;
                  flex-direction: column;
                  justify-content: space-between;
                  background: linear-gradient(145deg, #111827 0%, #1f2937 65%, #374151 100%);
                  color: #f9fafb;
                  margin-bottom: 10mm;
                  position: relative;
                }
                .front-booklet .disc-badge {
                  display: inline-block;
                  font-size: 9pt;
                  letter-spacing: 2px;
                  text-transform: uppercase;
                  color: #fbbf24;
                  border: 1px solid rgba(251,191,36,0.5);
                  padding: 3px 8px;
                  align-self: flex-start;
                }
                .front-booklet .album-title {
                  font-size: 22pt;
                  font-weight: 800;
                  line-height: 1.15;
                  margin: 0 0 4mm 0;
                  word-break: break-word;
                }
                .front-booklet .album-artist {
                  font-size: 13pt;
                  font-weight: 600;
                  color: #d1d5db;
                  margin: 0;
                }
                .front-booklet .front-footer {
                  display: flex;
                  justify-content: space-between;
                  font-size: 8.5pt;
                  color: #9ca3af;
                  border-top: 1px solid rgba(255,255,255,0.15);
                  padding-top: 3mm;
                }
                /* Standard CD Back Tray Inlay: 150mm x 118mm (6mm Left Spine + 138mm Back + 6mm Right Spine) */
                .back-tray {
                  width: 150mm;
                  height: 118mm;
                  border: 0.4mm dashed var(--border);
                  display: grid;
                  grid-template-columns: 6mm 138mm 6mm;
                  margin-bottom: 10mm;
                  background: #ffffff;
                }
                .spine {
                  display: flex;
                  align-items: center;
                  justify-content: center;
                  background: #111827;
                  color: #f9fafb;
                  font-size: 7.5pt;
                  font-weight: 700;
                  letter-spacing: 0.6px;
                  overflow: hidden;
                  white-space: nowrap;
                }
                .spine-left {
                  border-right: 0.3mm dotted var(--border);
                  writing-mode: vertical-rl;
                  transform: rotate(180deg);
                }
                .spine-right {
                  border-left: 0.3mm dotted var(--border);
                  writing-mode: vertical-rl;
                }
                .tray-center {
                  padding: 7mm 9mm;
                  display: flex;
                  flex-direction: column;
                  justify-content: space-between;
                }
                .tray-header {
                  border-bottom: 1.5px solid #111827;
                  padding-bottom: 2.5mm;
                  margin-bottom: 3mm;
                }
                .tray-header h3 {
                  margin: 0;
                  font-size: 13pt;
                  font-weight: 800;
                }
                .tray-header p {
                  margin: 1mm 0 0 0;
                  font-size: 9.5pt;
                  color: var(--muted);
                }
                .tray-tracklist {
                  flex: 1;
                  overflow: hidden;
                  column-count: {{(compilation.Tracks.Count > 14 ? 2 : 1)}};
                  column-gap: 6mm;
                }
                .tray-track {
                  display: flex;
                  align-items: baseline;
                  font-size: 8.5pt;
                  line-height: 1.45;
                  break-inside: avoid;
                }
                .tray-track .t-num {
                  font-weight: 700;
                  width: 6mm;
                  flex-shrink: 0;
                }
                .tray-track .t-title {
                  overflow: hidden;
                  text-overflow: ellipsis;
                  white-space: nowrap;
                }
                .tray-track .t-dots {
                  flex: 1;
                  border-bottom: 1px dotted #d1d5db;
                  margin: 0 2mm;
                }
                .tray-track .t-dur {
                  font-family: Consolas, monospace;
                  font-size: 8pt;
                  color: var(--muted);
                  flex-shrink: 0;
                }
                .tray-footer {
                  border-top: 1px solid #e5e7eb;
                  padding-top: 2mm;
                  display: flex;
                  justify-content: space-between;
                  font-size: 7.5pt;
                  color: var(--muted);
                }
                /* Full Reference Tracklist Table */
                table.master-table {
                  width: 100%;
                  border-collapse: collapse;
                  font-size: 9.5pt;
                  margin-top: 4mm;
                }
                table.master-table th, table.master-table td {
                  border-bottom: 1px solid #e5e7eb;
                  padding: 6px 8px;
                  text-align: left;
                }
                table.master-table th {
                  background: #f9fafb;
                  font-weight: 700;
                  font-size: 8.5pt;
                  text-transform: uppercase;
                  color: var(--muted);
                }
                table.master-table td.num, table.master-table td.dur, table.master-table td.isrc, table.master-table td.pregap {
                  font-family: Consolas, monospace;
                }
                @media print {
                  body { background: #fff; padding: 0; }
                  .toolbar { display: none; }
                  .sheet { box-shadow: none; padding: 5mm; }
                }
              </style>
            </head>
            <body>
              <div class="toolbar">
                <div>
                  <strong>IgnisDisc Optical Studio</strong> — Printable Jewel Case Insert &amp; Red Book Track Sheet
                </div>
                <button onclick="window.print()">🖨️ Print Cover &amp; Tracklist (100% Scale)</button>
              </div>

              <div class="sheet">
                <h2 class="section-label">Front Booklet Insert (120 mm × 120 mm — Cut Along Dashed Line)</h2>
                <div class="front-booklet">
                  <div class="disc-badge">COMPACT DISC DIGITAL AUDIO</div>
                  <div>
                    <h1 class="album-title">{{albumTitle}}</h1>
                    <p class="album-artist">{{performer}}</p>
                  </div>
                  <div class="front-footer">
                    <span>{{compilation.Tracks.Count}} Tracks • {{totalMsf}}</span>
                    <span>Red Book 44.1 kHz / 16-Bit Stereo</span>
                  </div>
                </div>

                <h2 class="section-label">Rear Jewel Case Tray Inlay (150 mm × 118 mm — Fold 6 mm Left &amp; Right Spines)</h2>
                <div class="back-tray">
                  <div class="spine spine-left">{{performer}} — {{albumTitle}}</div>
                  <div class="tray-center">
                    <div class="tray-header">
                      <h3>{{albumTitle}}</h3>
                      <p>{{performer}}</p>
                    </div>
                    <div class="tray-tracklist">
            {{trayRowsBuilder}}
                    </div>
                    <div class="tray-footer">
                      <span>{{recMode}} {{metaExtras}}</span>
                      <span>Total Time: {{totalMsf}} ({{compilation.TotalFrames:N0}} Frames)</span>
                    </div>
                  </div>
                  <div class="spine spine-right">{{performer}} — {{albumTitle}}</div>
                </div>

                <h2 class="section-label">Mastered CD-DA Track Sheet ({{timestamp}})</h2>
                <table class="master-table">
                  <thead>
                    <tr>
                      <th>#</th>
                      <th>Title</th>
                      <th>Performer</th>
                      <th>ISRC</th>
                      <th>Pre-Gap</th>
                      <th>Duration (MM:SS:FF)</th>
                    </tr>
                  </thead>
                  <tbody>
            {{rowsBuilder}}
                  </tbody>
                </table>
              </div>
            </body>
            </html>
            """;
    }
}
