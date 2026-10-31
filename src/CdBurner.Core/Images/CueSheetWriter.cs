using System.Text;

namespace CdBurner.Core.Images;

/// <summary>
/// Formats a CueSheet object model into canonical CDRWIN / Red Book CUE sheet text.
/// </summary>
public static class CueSheetWriter
{
    /// <summary>
    /// Formats a CueSheet into canonical CUE sheet text.
    /// </summary>
    public static string Format(CueSheet cueSheet) => WriteToString(cueSheet);

    /// <summary>
    /// Writes a CueSheet to a string.
    /// </summary>
    public static string WriteToString(CueSheet cueSheet)
    {
        ArgumentNullException.ThrowIfNull(cueSheet);
        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        Write(cueSheet, writer);
        return sb.ToString();
    }

    /// <summary>
    /// Writes a CueSheet to a file on disk.
    /// </summary>
    public static void WriteToFile(CueSheet cueSheet, string filePath)
    {
        ArgumentNullException.ThrowIfNull(cueSheet);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        using var writer = new StreamWriter(filePath, false, Encoding.UTF8);
        Write(cueSheet, writer);
    }

    /// <summary>
    /// Asynchronously writes a CueSheet to a file on disk.
    /// </summary>
    public static async Task WriteToFileAsync(CueSheet cueSheet, string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cueSheet);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        await using var writer = new StreamWriter(filePath, false, Encoding.UTF8);
        await WriteAsync(cueSheet, writer, cancellationToken);
    }

    /// <summary>
    /// Writes a CueSheet to a TextWriter.
    /// </summary>
    public static void Write(CueSheet cueSheet, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(cueSheet);
        ArgumentNullException.ThrowIfNull(writer);

        // Remarks
        foreach (var kvp in cueSheet.Remarks)
        {
            if (string.IsNullOrEmpty(kvp.Value))
            {
                writer.WriteLine($"REM {kvp.Key}");
            }
            else if (kvp.Value.Contains(' ') || kvp.Value.Contains('\t') || kvp.Value.Contains('"') || kvp.Value.Contains('\r') || kvp.Value.Contains('\n'))
            {
                writer.WriteLine($"REM {kvp.Key} \"{EscapeString(kvp.Value)}\"");
            }
            else
            {
                writer.WriteLine($"REM {kvp.Key} {kvp.Value}");
            }
        }

        // Disc Catalog
        if (!string.IsNullOrEmpty(cueSheet.Catalog))
        {
            writer.WriteLine($"CATALOG {cueSheet.Catalog}");
        }

        // Disc Title
        if (!string.IsNullOrEmpty(cueSheet.Title))
        {
            writer.WriteLine($"TITLE \"{EscapeString(cueSheet.Title)}\"");
        }

        // Disc Performer
        if (!string.IsNullOrEmpty(cueSheet.Performer))
        {
            writer.WriteLine($"PERFORMER \"{EscapeString(cueSheet.Performer)}\"");
        }

        // Disc Songwriter
        if (!string.IsNullOrEmpty(cueSheet.Songwriter))
        {
            writer.WriteLine($"SONGWRITER \"{EscapeString(cueSheet.Songwriter)}\"");
        }

        // Files
        foreach (var file in cueSheet.Files)
        {
            writer.WriteLine($"FILE \"{EscapeString(file.FileName)}\" {file.FileType.ToUpperInvariant()}");

            foreach (var track in file.Tracks)
            {
                writer.WriteLine($"  TRACK {track.TrackNumber:D2} {FormatDataType(track.DataType)}");

                if (!string.IsNullOrEmpty(track.Title))
                {
                    writer.WriteLine($"    TITLE \"{EscapeString(track.Title)}\"");
                }

                if (!string.IsNullOrEmpty(track.Performer))
                {
                    writer.WriteLine($"    PERFORMER \"{EscapeString(track.Performer)}\"");
                }

                if (!string.IsNullOrEmpty(track.Songwriter))
                {
                    writer.WriteLine($"    SONGWRITER \"{EscapeString(track.Songwriter)}\"");
                }

                if (!string.IsNullOrEmpty(track.Isrc))
                {
                    writer.WriteLine($"    ISRC {track.Isrc}");
                }

                if (track.Flags.Count > 0)
                {
                    writer.WriteLine($"    FLAGS {string.Join(" ", track.Flags)}");
                }

                if (track.PreGap.HasValue)
                {
                    writer.WriteLine($"    PREGAP {track.PreGap.Value}");
                }

                foreach (var index in track.Indices)
                {
                    writer.WriteLine($"    INDEX {index.IndexNumber:D2} {index.Position}");
                }

                if (track.PostGap.HasValue)
                {
                    writer.WriteLine($"    POSTGAP {track.PostGap.Value}");
                }
            }
        }
    }

    /// <summary>
    /// Asynchronously writes a CueSheet to a TextWriter.
    /// </summary>
    public static async Task WriteAsync(CueSheet cueSheet, TextWriter writer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cueSheet);
        ArgumentNullException.ThrowIfNull(writer);

        foreach (var kvp in cueSheet.Remarks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(kvp.Value))
            {
                await writer.WriteLineAsync($"REM {kvp.Key}");
            }
            else if (kvp.Value.Contains(' ') || kvp.Value.Contains('\t') || kvp.Value.Contains('"') || kvp.Value.Contains('\r') || kvp.Value.Contains('\n'))
            {
                await writer.WriteLineAsync($"REM {kvp.Key} \"{EscapeString(kvp.Value)}\"");
            }
            else
            {
                await writer.WriteLineAsync($"REM {kvp.Key} {kvp.Value}");
            }
        }

        if (!string.IsNullOrEmpty(cueSheet.Catalog))
        {
            await writer.WriteLineAsync($"CATALOG {cueSheet.Catalog}");
        }

        if (!string.IsNullOrEmpty(cueSheet.Title))
        {
            await writer.WriteLineAsync($"TITLE \"{EscapeString(cueSheet.Title)}\"");
        }

        if (!string.IsNullOrEmpty(cueSheet.Performer))
        {
            await writer.WriteLineAsync($"PERFORMER \"{EscapeString(cueSheet.Performer)}\"");
        }

        if (!string.IsNullOrEmpty(cueSheet.Songwriter))
        {
            await writer.WriteLineAsync($"SONGWRITER \"{EscapeString(cueSheet.Songwriter)}\"");
        }

        foreach (var file in cueSheet.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteLineAsync($"FILE \"{EscapeString(file.FileName)}\" {file.FileType.ToUpperInvariant()}");

            foreach (var track in file.Tracks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writer.WriteLineAsync($"  TRACK {track.TrackNumber:D2} {FormatDataType(track.DataType)}");

                if (!string.IsNullOrEmpty(track.Title))
                {
                    await writer.WriteLineAsync($"    TITLE \"{EscapeString(track.Title)}\"");
                }

                if (!string.IsNullOrEmpty(track.Performer))
                {
                    await writer.WriteLineAsync($"    PERFORMER \"{EscapeString(track.Performer)}\"");
                }

                if (!string.IsNullOrEmpty(track.Songwriter))
                {
                    await writer.WriteLineAsync($"    SONGWRITER \"{EscapeString(track.Songwriter)}\"");
                }

                if (!string.IsNullOrEmpty(track.Isrc))
                {
                    await writer.WriteLineAsync($"    ISRC {track.Isrc}");
                }

                if (track.Flags.Count > 0)
                {
                    await writer.WriteLineAsync($"    FLAGS {string.Join(" ", track.Flags)}");
                }

                if (track.PreGap.HasValue)
                {
                    await writer.WriteLineAsync($"    PREGAP {track.PreGap.Value}");
                }

                foreach (var index in track.Indices)
                {
                    await writer.WriteLineAsync($"    INDEX {index.IndexNumber:D2} {index.Position}");
                }

                if (track.PostGap.HasValue)
                {
                    await writer.WriteLineAsync($"    POSTGAP {track.PostGap.Value}");
                }
            }
        }
    }

    /// <summary>
    /// Formats a CueTrackDataType into canonical CUE sheet keyword.
    /// </summary>
    public static string FormatDataType(CueTrackDataType type) => type switch
    {
        CueTrackDataType.Audio => "AUDIO",
        CueTrackDataType.Mode1_2048 => "MODE1/2048",
        CueTrackDataType.Mode1_2352 => "MODE1/2352",
        CueTrackDataType.Mode2_2336 => "MODE2/2336",
        CueTrackDataType.Mode2_2352 => "MODE2/2352",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown track data type.")
    };

    private static string EscapeString(string s) => s.Replace("\r", " ").Replace("\n", " ").Replace("\"", "\\\"");
}
