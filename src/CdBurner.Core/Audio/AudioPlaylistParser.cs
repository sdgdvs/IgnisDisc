using System.Text;

namespace CdBurner.Core.Audio;

/// <summary>
/// Represents a single audio track entry parsed from an M3U, M3U8, or PLS playlist file.
/// </summary>
public sealed record AudioPlaylistEntry(
    string FilePath,
    string? Title = null,
    string? Performer = null,
    string? Album = null,
    int? DurationSeconds = null
);

/// <summary>
/// Parses standard M3U (.m3u), UTF-8 Extended M3U (.m3u8), and Winamp/Shoutcast PLS (.pls)
/// audio playlists into resolved local track paths and optional #EXTINF / Title metadata.
/// </summary>
public static class AudioPlaylistParser
{
    /// <summary>
    /// Returns true if the file path has a supported playlist extension (.m3u, .m3u8, .pls).
    /// </summary>
    public static bool IsPlaylistFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext is ".m3u" or ".m3u8" or ".pls";
    }

    /// <summary>
    /// Parses a playlist file (.m3u, .m3u8, or .pls) from disk and resolves relative track paths
    /// against the playlist file's directory.
    /// </summary>
    public static IReadOnlyList<AudioPlaylistEntry> ParseFile(string playlistFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistFilePath);
        if (!File.Exists(playlistFilePath))
        {
            throw new FileNotFoundException($"Playlist file not found: '{playlistFilePath}'.", playlistFilePath);
        }

        string baseDir = Path.GetDirectoryName(Path.GetFullPath(playlistFilePath)) ?? string.Empty;
        string ext = Path.GetExtension(playlistFilePath).ToLowerInvariant();
        string content = DecodePlaylistText(File.ReadAllBytes(playlistFilePath));

        return ext == ".pls"
            ? ParsePlsContent(content, baseDir)
            : ParseM3uContent(content, baseDir);
    }

    /// <summary>
    /// Decodes playlist bytes. .m3u8 is UTF-8 by definition, but classic .m3u / .pls files written by Winamp, Windows Media
    /// Player and older tools use the ANSI code page (Windows-1252), where "Beyoncé" is the single byte 0xE9. Reading those as
    /// UTF-8 turns accented paths into U+FFFD and every such track becomes "file not found". Strict UTF-8 is tried first
    /// (honouring a BOM); invalid UTF-8 falls back to Windows-1252.
    /// </summary>
    internal static string DecodePlaylistText(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes, start, bytes.Length - start);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252).GetString(bytes);
        }
    }

    /// <summary>
    /// Resolves a playlist path against <paramref name="baseDirectory"/>; returns null for paths the OS cannot represent
    /// (illegal characters, malformed UNC) so one bad line does not abort the whole playlist.
    /// </summary>
    private static string? TryResolvePath(string path, string baseDirectory)
    {
        try
        {
            return Path.IsPathRooted(path) || string.IsNullOrEmpty(baseDirectory)
                ? path
                : Path.GetFullPath(Path.Combine(baseDirectory, path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses M3U / Extended M3U (#EXTM3U) text content.
    /// </summary>
    public static IReadOnlyList<AudioPlaylistEntry> ParseM3uContent(string content, string baseDirectory = "")
    {
        ArgumentNullException.ThrowIfNull(content);
        baseDirectory ??= string.Empty;

        var entries = new List<AudioPlaylistEntry>();
        string? pendingTitle = null;
        string? pendingPerformer = null;
        string? globalAlbum = null;
        string? globalArtist = null;
        int? pendingDuration = null;

        using var reader = new StringReader(content);
        string? rawLine;
        while ((rawLine = reader.ReadLine()) != null)
        {
            string line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.StartsWith('#'))
            {
                if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
                {
                    string metaPart = line.Substring(8).Trim();
                    int commaIdx = metaPart.IndexOf(',');
                    if (commaIdx >= 0)
                    {
                        string durPart = metaPart.Substring(0, commaIdx).Trim();
                        int spaceIdx = durPart.IndexOf(' ');
                        if (spaceIdx > 0) durPart = durPart.Substring(0, spaceIdx);
                        if (double.TryParse(durPart, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double sec) && sec >= 0)
                        {
                            pendingDuration = (int)Math.Round(sec);
                        }

                        string display = metaPart.Substring(commaIdx + 1).Trim();
                        SplitArtistAndTitle(display, out pendingPerformer, out pendingTitle);
                    }
                }
                else if (line.StartsWith("#EXTALB:", StringComparison.OrdinalIgnoreCase))
                {
                    globalAlbum = line.Substring(8).Trim();
                }
                else if (line.StartsWith("#EXTART:", StringComparison.OrdinalIgnoreCase))
                {
                    globalArtist = line.Substring(8).Trim();
                }

                continue;
            }

            // Skip network streams (http://, https://, rtsp://)
            if (line.Contains("://", StringComparison.Ordinal))
            {
                if (line.StartsWith("file://", StringComparison.OrdinalIgnoreCase) &&
                    Uri.TryCreate(line, UriKind.Absolute, out var fileUri) &&
                    fileUri.IsFile)
                {
                    line = fileUri.LocalPath;
                }
                else
                {
                    pendingTitle = null;
                    pendingPerformer = null;
                    pendingDuration = null;
                    continue;
                }
            }

            string? resolvedPath = TryResolvePath(line, baseDirectory);
            if (resolvedPath == null)
            {
                pendingTitle = null;
                pendingPerformer = null;
                pendingDuration = null;
                continue;
            }

            entries.Add(new AudioPlaylistEntry(
                FilePath: resolvedPath,
                Title: pendingTitle,
                Performer: pendingPerformer ?? globalArtist,
                Album: globalAlbum,
                DurationSeconds: pendingDuration));

            pendingTitle = null;
            pendingPerformer = null;
            pendingDuration = null;
        }

        return entries;
    }

    /// <summary>
    /// Parses PLS ([playlist]) INI-style text content.
    /// </summary>
    public static IReadOnlyList<AudioPlaylistEntry> ParsePlsContent(string content, string baseDirectory = "")
    {
        ArgumentNullException.ThrowIfNull(content);
        baseDirectory ??= string.Empty;

        var files = new SortedDictionary<int, string>();
        var titles = new Dictionary<int, string>();
        var lengths = new Dictionary<int, int>();

        using var reader = new StringReader(content);
        string? rawLine;
        while ((rawLine = reader.ReadLine()) != null)
        {
            string line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('[') || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            int eqIdx = line.IndexOf('=');
            if (eqIdx <= 0) continue;

            string key = line.Substring(0, eqIdx).Trim();
            string val = line.Substring(eqIdx + 1).Trim();

            if (key.StartsWith("File", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(key.AsSpan(4), out int fileIndex) &&
                !string.IsNullOrWhiteSpace(val))
            {
                files[fileIndex] = val;
            }
            else if (key.StartsWith("Title", StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(key.AsSpan(5), out int titleIndex) &&
                     !string.IsNullOrWhiteSpace(val))
            {
                titles[titleIndex] = val;
            }
            else if (key.StartsWith("Length", StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(key.AsSpan(6), out int lenIndex) &&
                     int.TryParse(val, out int seconds) &&
                     seconds >= 0)
            {
                lengths[lenIndex] = seconds;
            }
        }

        var entries = new List<AudioPlaylistEntry>(files.Count);
        foreach (var (index, rawPath) in files)
        {
            if (rawPath.Contains("://", StringComparison.Ordinal) &&
                !rawPath.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string cleanPath = rawPath;
            if (cleanPath.StartsWith("file://", StringComparison.OrdinalIgnoreCase) &&
                Uri.TryCreate(cleanPath, UriKind.Absolute, out var uri) && uri.IsFile)
            {
                cleanPath = uri.LocalPath;
            }

            string? resolvedPath = TryResolvePath(cleanPath, baseDirectory);
            if (resolvedPath == null) continue;

            string? performer = null;
            string? title = null;
            if (titles.TryGetValue(index, out string? rawTitle))
            {
                SplitArtistAndTitle(rawTitle, out performer, out title);
            }

            lengths.TryGetValue(index, out int durSec);
            entries.Add(new AudioPlaylistEntry(
                FilePath: resolvedPath,
                Title: title,
                Performer: performer,
                Album: null,
                DurationSeconds: lengths.ContainsKey(index) ? durSec : null));
        }

        return entries;
    }

    private static void SplitArtistAndTitle(string display, out string? performer, out string? title)
    {
        performer = null;
        title = null;
        if (string.IsNullOrWhiteSpace(display)) return;

        int sepIdx = display.IndexOf(" - ", StringComparison.Ordinal);
        if (sepIdx > 0 && sepIdx + 3 < display.Length)
        {
            performer = display.Substring(0, sepIdx).Trim();
            title = display.Substring(sepIdx + 3).Trim();
        }
        else
        {
            title = display.Trim();
        }
    }
}
