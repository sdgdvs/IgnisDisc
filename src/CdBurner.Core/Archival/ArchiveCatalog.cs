using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Archival;

/// <summary>One file of the whole archive set: where it lives and its checksum.</summary>
/// <param name="DiscNumber">1-based number of the disc that holds the file.</param>
/// <param name="Path">Path on that disc, '/' separators.</param>
/// <param name="SizeBytes">Length in bytes.</param>
/// <param name="Sha256">SHA-256 in lowercase hex.</param>
public sealed record ArchiveCatalogEntry(int DiscNumber, string Path, long SizeBytes, string Sha256);

/// <summary>The catalog file's content: the whole set, identical on every disc.</summary>
public sealed record ArchiveCatalogDocument(
    string SetId,
    int DiscCount,
    string MediaType,
    IReadOnlyList<ArchiveCatalogEntry> Files);

/// <summary>
/// Builds CATALOG.JSON and INDEX.HTML, the two files every archive disc carries so a file can be found without
/// loading the other discs: the JSON lists every file of the set (disc number, path, size, SHA-256), the HTML page
/// shows the same list as a table with a search box. The page has no external references and its table is plain
/// HTML, so it reads with scripts switched off.
/// </summary>
public static class ArchiveCatalog
{
    /// <summary>File name of the machine-readable catalog, in the root of every archive disc.</summary>
    public const string CatalogFileName = "CATALOG.JSON";

    /// <summary>File name of the self-contained search page, in the root of every archive disc.</summary>
    public const string HtmlIndexFileName = "INDEX.HTML";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // The default encoder escapes <, >, & and every non-ASCII character, so the text is plain ASCII and safe anywhere.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>
    /// Hashes every file of the plan from its source and returns one entry per file, ordered by disc then path.
    /// </summary>
    public static async Task<IReadOnlyList<ArchiveCatalogEntry>> BuildEntriesAsync(
        ArchivePlan plan, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var entries = new List<ArchiveCatalogEntry>();
        int total = plan.Discs.Sum(d => d.Files.Count);
        foreach (var disc in plan.Discs)
        {
            foreach (var file in disc.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"Cataloging {entries.Count + 1}/{total}: {file.RelativePath}");
                string hash;
                using (var stream = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                           bufferSize: 1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous))
                {
                    hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                }

                entries.Add(new ArchiveCatalogEntry(disc.DiscNumber, file.RelativePath.Replace('\\', '/'), file.SizeBytes, hash));
            }
        }

        return entries;
    }

    /// <summary>
    /// Sectors the two catalog files will take at most for these files. The planner keeps this much room free on
    /// every disc. Each file is listed twice in JSON-sized text (catalog and page table), plus a fixed page.
    /// </summary>
    public static long EstimateSectors(IEnumerable<ArchiveSourceFile> files)
    {
        long bytes = 24 * 1024;
        foreach (var file in files)
        {
            long pathCost = 0;
            foreach (char c in file.RelativePath)
            {
                // Letters, digits and common punctuation are written as they are. Anything else may be escaped:
                // \uXXXX in JSON (6 bytes), "&quot;" or a numeric entity in HTML (up to 8 bytes).
                pathCost += char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '/' or ' ' ? 1 : 8;
            }

            // JSON row: ~125 bytes beyond the path. HTML row: ~200 bytes beyond the path (64 hash digits included).
            bytes += 2 * pathCost + 340;
        }

        return (bytes + OpticalConstants.DataSectorSize - 1) / OpticalConstants.DataSectorSize;
    }

    /// <summary>Catalog JSON text for the whole set.</summary>
    public static string ToJson(ArchivePlan plan, IReadOnlyList<ArchiveCatalogEntry> entries)
    {
        var doc = new ArchiveCatalogDocument(plan.SetId, plan.Discs.Count, plan.MediaType.ToString(), entries);
        return JsonSerializer.Serialize(doc, JsonOptions) + "\n";
    }

    /// <summary>Reads a catalog written by <see cref="ToJson"/>.</summary>
    public static ArchiveCatalogDocument? ParseJson(string json) =>
        JsonSerializer.Deserialize<ArchiveCatalogDocument>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

    /// <summary>
    /// The search page: inline CSS, a table with one row per file of the set (disc, path, size, SHA-256) and a short
    /// inline script that hides the rows not matching the search box. No external references of any kind.
    /// </summary>
    public static string ToHtml(ArchivePlan plan, int currentDisc, IReadOnlyList<ArchiveCatalogEntry> entries)
    {
        var sb = new StringBuilder(64 * 1024);
        string set = WebUtility.HtmlEncode(plan.SetId);
        sb.Append("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n");
        sb.Append("<title>Archive set ").Append(set).Append(" - file index</title>\n");
        sb.Append("<style>\n");
        sb.Append("body { font-family: Segoe UI, Arial, sans-serif; margin: 1.5em; color: #111; background: #fff; }\n");
        sb.Append("h1 { font-size: 1.3em; }\n");
        sb.Append("p { max-width: 60em; }\n");
        sb.Append("input { font-size: 1em; padding: 0.3em; width: 24em; max-width: 100%; }\n");
        sb.Append("table { border-collapse: collapse; margin-top: 1em; }\n");
        sb.Append("th, td { border: 1px solid #888; padding: 0.2em 0.6em; text-align: left; vertical-align: top; }\n");
        sb.Append("td.num { text-align: right; }\n");
        sb.Append("td.hash { font-family: Consolas, monospace; font-size: 0.8em; }\n");
        sb.Append("tr.here td { background: #eef6ee; }\n");
        sb.Append("</style>\n</head>\n<body>\n");
        sb.Append("<h1>Archive set ").Append(set).Append("</h1>\n");
        sb.Append("<p>This is disc ").Append(currentDisc.ToString(CultureInfo.InvariantCulture)).Append(" of ")
            .Append(plan.Discs.Count.ToString(CultureInfo.InvariantCulture))
            .Append(". The table lists every file of the whole set and the disc that holds it. ")
            .Append("The same list, as data, is in CATALOG.JSON on this disc.</p>\n");
        sb.Append("<p><label>Search the file list: <input type=\"search\" id=\"q\" placeholder=\"part of a file name\"></label> ");
        sb.Append("<span id=\"count\"></span></p>\n");
        sb.Append("<table id=\"files\">\n<thead><tr><th>Disc</th><th>Path</th><th>Size (bytes)</th><th>SHA-256</th></tr></thead>\n<tbody>\n");
        foreach (var e in entries)
        {
            sb.Append(e.DiscNumber == currentDisc ? "<tr class=\"here\">" : "<tr>");
            sb.Append("<td class=\"num\">").Append(e.DiscNumber.ToString(CultureInfo.InvariantCulture)).Append("</td>");
            sb.Append("<td>").Append(WebUtility.HtmlEncode(e.Path)).Append("</td>");
            sb.Append("<td class=\"num\">").Append(e.SizeBytes.ToString(CultureInfo.InvariantCulture)).Append("</td>");
            sb.Append("<td class=\"hash\">").Append(e.Sha256).Append("</td></tr>\n");
        }

        sb.Append("</tbody>\n</table>\n");
        sb.Append("<script>\n");
        sb.Append("(function () {\n");
        sb.Append("  var box = document.getElementById('q');\n");
        sb.Append("  var rows = document.getElementById('files').tBodies[0].rows;\n");
        sb.Append("  var count = document.getElementById('count');\n");
        sb.Append("  function filter() {\n");
        sb.Append("    var words = box.value.toLowerCase().split(/\\s+/).filter(function (w) { return w.length > 0; });\n");
        sb.Append("    var shown = 0;\n");
        sb.Append("    for (var i = 0; i < rows.length; i++) {\n");
        sb.Append("      var text = rows[i].cells[0].textContent + ' ' + rows[i].cells[1].textContent;\n");
        sb.Append("      text = text.toLowerCase();\n");
        sb.Append("      var match = words.every(function (w) { return text.indexOf(w) >= 0; });\n");
        sb.Append("      rows[i].style.display = match ? '' : 'none';\n");
        sb.Append("      if (match) { shown++; }\n");
        sb.Append("    }\n");
        sb.Append("    count.textContent = shown + ' of ' + rows.length + ' files shown';\n");
        sb.Append("  }\n");
        sb.Append("  box.addEventListener('input', filter);\n");
        sb.Append("  filter();\n");
        sb.Append("})();\n");
        sb.Append("</script>\n</body>\n</html>\n");
        return sb.ToString();
    }

    /// <summary>Writes CATALOG.JSON and INDEX.HTML into a staged disc folder.</summary>
    public static async Task WriteAsync(
        string discRoot, ArchivePlan plan, int discNumber, IReadOnlyList<ArchiveCatalogEntry> entries, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(discRoot);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(entries);

        await File.WriteAllTextAsync(Path.Combine(discRoot, CatalogFileName), ToJson(plan, entries), Utf8NoBom, cancellationToken)
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(discRoot, HtmlIndexFileName), ToHtml(plan, discNumber, entries), Utf8NoBom, cancellationToken)
            .ConfigureAwait(false);
    }
}
