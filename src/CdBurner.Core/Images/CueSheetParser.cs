using System.Diagnostics.CodeAnalysis;
using System.Text;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Images;

/// <summary>
/// Parses CDRWIN / Red Book CUE sheets from text, streams, or files into a CueSheet object model.
/// </summary>
public static class CueSheetParser
{
    /// <summary>
    /// Parses a CUE sheet from a string.
    /// </summary>
    public static CueSheet Parse(string cueText)
    {
        ArgumentNullException.ThrowIfNull(cueText);
        using var reader = new StringReader(cueText);
        return Parse(reader);
    }

    /// <summary>
    /// Parses a CUE sheet from a file on disk.
    /// </summary>
    public static CueSheet ParseFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        using var reader = new StreamReader(filePath, Encoding.UTF8);
        return Parse(reader);
    }

    /// <summary>
    /// Asynchronously parses a CUE sheet from a file on disk.
    /// </summary>
    public static async Task<CueSheet> ParseFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        using var reader = new StreamReader(filePath, Encoding.UTF8);
        return await ParseAsync(reader, cancellationToken);
    }

    /// <summary>
    /// Attempts to parse a CUE sheet from a string.
    /// </summary>
    public static bool TryParse(string? cueText, [NotNullWhen(true)] out CueSheet? cueSheet, out string? errorMessage)
    {
        cueSheet = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(cueText))
        {
            errorMessage = "CUE sheet text cannot be null or empty.";
            return false;
        }

        try
        {
            cueSheet = Parse(cueText);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Parses a CUE sheet from a TextReader.
    /// </summary>
    public static CueSheet Parse(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        string? catalog = null;
        string? discTitle = null;
        string? discPerformer = null;
        string? discSongwriter = null;
        var remarks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<CueFileEntry>();

        CueFileEntryBuilder? currentFile = null;
        CueTrackBuilder? currentTrack = null;

        void FinishCurrentTrack()
        {
            if (currentTrack != null)
            {
                currentFile ??= new CueFileEntryBuilder(string.Empty, "BINARY");
                currentFile.Tracks.Add(currentTrack.Build());
                currentTrack = null;
            }
        }

        void FinishCurrentFile()
        {
            FinishCurrentTrack();
            if (currentFile != null)
            {
                files.Add(currentFile.Build());
                currentFile = null;
            }
        }

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var tokens = TokenizeLine(trimmed);
            if (tokens.Count == 0)
            {
                continue;
            }

            string cmd = tokens[0].ToUpperInvariant();
            switch (cmd)
            {
                case "REM":
                    if (tokens.Count >= 2)
                    {
                        string key = tokens[1];
                        string val = tokens.Count >= 3 ? string.Join(" ", tokens.Skip(2)) : string.Empty;
                        remarks[key] = val;
                    }
                    break;

                case "CATALOG":
                    if (tokens.Count >= 2)
                    {
                        catalog = tokens[1];
                    }
                    break;

                case "TITLE":
                    if (tokens.Count >= 2)
                    {
                        string title = tokens.Count == 2 ? tokens[1] : string.Join(" ", tokens.Skip(1));
                        if (currentTrack != null)
                        {
                            currentTrack.Title = title;
                        }
                        else
                        {
                            discTitle = title;
                        }
                    }
                    break;

                case "PERFORMER":
                    if (tokens.Count >= 2)
                    {
                        string performer = tokens.Count == 2 ? tokens[1] : string.Join(" ", tokens.Skip(1));
                        if (currentTrack != null)
                        {
                            currentTrack.Performer = performer;
                        }
                        else
                        {
                            discPerformer = performer;
                        }
                    }
                    break;

                case "SONGWRITER":
                    if (tokens.Count >= 2)
                    {
                        string songwriter = tokens.Count == 2 ? tokens[1] : string.Join(" ", tokens.Skip(1));
                        if (currentTrack != null)
                        {
                            currentTrack.Songwriter = songwriter;
                        }
                        else
                        {
                            discSongwriter = songwriter;
                        }
                    }
                    break;

                case "FILE":
                    FinishCurrentFile();
                    string fileName = tokens.Count >= 2 ? tokens[1] : string.Empty;
                    string fileType = tokens.Count >= 3 ? tokens[2] : "BINARY";
                    currentFile = new CueFileEntryBuilder(fileName, fileType);
                    break;

                case "TRACK":
                    FinishCurrentTrack();
                    int trackNum = tokens.Count >= 2 ? int.Parse(tokens[1]) : 1;
                    CueTrackDataType dataType = tokens.Count >= 3 ? ParseTrackDataType(tokens[2]) : CueTrackDataType.Audio;
                    currentTrack = new CueTrackBuilder(trackNum, dataType);
                    break;

                case "ISRC":
                    if (tokens.Count >= 2 && currentTrack != null)
                    {
                        currentTrack.Isrc = tokens[1];
                    }
                    break;

                case "FLAGS":
                    if (currentTrack != null)
                    {
                        for (int i = 1; i < tokens.Count; i++)
                        {
                            currentTrack.Flags.Add(tokens[i].ToUpperInvariant());
                        }
                    }
                    break;

                case "PREGAP":
                    if (tokens.Count >= 2 && currentTrack != null)
                    {
                        currentTrack.PreGap = MsfAddress.Parse(tokens[1]);
                    }
                    break;

                case "POSTGAP":
                    if (tokens.Count >= 2 && currentTrack != null)
                    {
                        currentTrack.PostGap = MsfAddress.Parse(tokens[1]);
                    }
                    break;

                case "INDEX":
                    if (tokens.Count >= 3 && currentTrack != null)
                    {
                        int idxNum = int.Parse(tokens[1]);
                        MsfAddress pos = MsfAddress.Parse(tokens[2]);
                        currentTrack.Indices.Add(new CueIndex(idxNum, pos));
                    }
                    break;
            }
        }

        FinishCurrentFile();
        return new CueSheet(catalog, discTitle, discPerformer, discSongwriter, files, remarks);
    }

    /// <summary>
    /// Asynchronously parses a CUE sheet from a TextReader.
    /// </summary>
    public static async Task<CueSheet> ParseAsync(TextReader reader, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);

        string? catalog = null;
        string? discTitle = null;
        string? discPerformer = null;
        string? discSongwriter = null;
        var remarks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<CueFileEntry>();

        CueFileEntryBuilder? currentFile = null;
        CueTrackBuilder? currentTrack = null;

        void FinishCurrentTrack()
        {
            if (currentTrack != null)
            {
                currentFile ??= new CueFileEntryBuilder(string.Empty, "BINARY");
                currentFile.Tracks.Add(currentTrack.Build());
                currentTrack = null;
            }
        }

        void FinishCurrentFile()
        {
            FinishCurrentTrack();
            if (currentFile != null)
            {
                files.Add(currentFile.Build());
                currentFile = null;
            }
        }

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var tokens = TokenizeLine(trimmed);
            if (tokens.Count == 0)
            {
                continue;
            }

            string cmd = tokens[0].ToUpperInvariant();
            switch (cmd)
            {
                case "REM":
                    if (tokens.Count >= 2)
                    {
                        string key = tokens[1];
                        string val = tokens.Count >= 3 ? string.Join(" ", tokens.Skip(2)) : string.Empty;
                        remarks[key] = val;
                    }
                    break;

                case "CATALOG":
                    if (tokens.Count >= 2)
                    {
                        catalog = tokens[1];
                    }
                    break;

                case "TITLE":
                    if (tokens.Count >= 2)
                    {
                        string title = tokens.Count == 2 ? tokens[1] : string.Join(" ", tokens.Skip(1));
                        if (currentTrack != null)
                        {
                            currentTrack.Title = title;
                        }
                        else
                        {
                            discTitle = title;
                        }
                    }
                    break;

                case "PERFORMER":
                    if (tokens.Count >= 2)
                    {
                        string performer = tokens.Count == 2 ? tokens[1] : string.Join(" ", tokens.Skip(1));
                        if (currentTrack != null)
                        {
                            currentTrack.Performer = performer;
                        }
                        else
                        {
                            discPerformer = performer;
                        }
                    }
                    break;

                case "SONGWRITER":
                    if (tokens.Count >= 2)
                    {
                        string songwriter = tokens.Count == 2 ? tokens[1] : string.Join(" ", tokens.Skip(1));
                        if (currentTrack != null)
                        {
                            currentTrack.Songwriter = songwriter;
                        }
                        else
                        {
                            discSongwriter = songwriter;
                        }
                    }
                    break;

                case "FILE":
                    FinishCurrentFile();
                    string fileName = tokens.Count >= 2 ? tokens[1] : string.Empty;
                    string fileType = tokens.Count >= 3 ? tokens[2] : "BINARY";
                    currentFile = new CueFileEntryBuilder(fileName, fileType);
                    break;

                case "TRACK":
                    FinishCurrentTrack();
                    int trackNum = tokens.Count >= 2 ? int.Parse(tokens[1]) : 1;
                    CueTrackDataType dataType = tokens.Count >= 3 ? ParseTrackDataType(tokens[2]) : CueTrackDataType.Audio;
                    currentTrack = new CueTrackBuilder(trackNum, dataType);
                    break;

                case "ISRC":
                    if (tokens.Count >= 2 && currentTrack != null)
                    {
                        currentTrack.Isrc = tokens[1];
                    }
                    break;

                case "FLAGS":
                    if (currentTrack != null)
                    {
                        for (int i = 1; i < tokens.Count; i++)
                        {
                            currentTrack.Flags.Add(tokens[i].ToUpperInvariant());
                        }
                    }
                    break;

                case "PREGAP":
                    if (tokens.Count >= 2 && currentTrack != null)
                    {
                        currentTrack.PreGap = MsfAddress.Parse(tokens[1]);
                    }
                    break;

                case "POSTGAP":
                    if (tokens.Count >= 2 && currentTrack != null)
                    {
                        currentTrack.PostGap = MsfAddress.Parse(tokens[1]);
                    }
                    break;

                case "INDEX":
                    if (tokens.Count >= 3 && currentTrack != null)
                    {
                        int idxNum = int.Parse(tokens[1]);
                        MsfAddress pos = MsfAddress.Parse(tokens[2]);
                        currentTrack.Indices.Add(new CueIndex(idxNum, pos));
                    }
                    break;
            }
        }

        FinishCurrentFile();
        return new CueSheet(catalog, discTitle, discPerformer, discSongwriter, files, remarks);
    }

    /// <summary>
    /// Parses a CUE track data type string (e.g. AUDIO, MODE1/2048, MODE1/2352, MODE2/2336, MODE2/2352).
    /// </summary>
    public static CueTrackDataType ParseTrackDataType(string s)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(s);
        string normalized = s.Trim().ToUpperInvariant().Replace('_', '/');
        return normalized switch
        {
            "AUDIO" => CueTrackDataType.Audio,
            "MODE1/2048" => CueTrackDataType.Mode1_2048,
            "MODE1/2352" => CueTrackDataType.Mode1_2352,
            "MODE2/2336" => CueTrackDataType.Mode2_2336,
            "MODE2/2352" => CueTrackDataType.Mode2_2352,
            _ => throw new FormatException($"Unsupported or unrecognized CUE track data type: '{s}'.")
        };
    }

    internal static List<string> TokenizeLine(string line)
    {
        var tokens = new List<string>();
        int i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i]))
            {
                i++;
            }

            if (i >= line.Length)
            {
                break;
            }

            if (line[i] == '"')
            {
                i++; // skip opening quote
                var sb = new StringBuilder();
                while (i < line.Length && line[i] != '"')
                {
                    if (line[i] == '\\' && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i += 2;
                    }
                    else
                    {
                        sb.Append(line[i]);
                        i++;
                    }
                }
                tokens.Add(sb.ToString());
                if (i < line.Length && line[i] == '"')
                {
                    i++; // skip closing quote
                }
            }
            else
            {
                int start = i;
                while (i < line.Length && !char.IsWhiteSpace(line[i]))
                {
                    i++;
                }
                tokens.Add(line[start..i]);
            }
        }
        return tokens;
    }

    private sealed class CueFileEntryBuilder(string fileName, string fileType)
    {
        public string FileName { get; } = fileName;
        public string FileType { get; } = fileType;
        public List<CueTrack> Tracks { get; } = new();

        public CueFileEntry Build() => new(FileName, FileType, Tracks);
    }

    private sealed class CueTrackBuilder(int trackNumber, CueTrackDataType dataType)
    {
        public int TrackNumber { get; } = trackNumber;
        public CueTrackDataType DataType { get; } = dataType;
        public string? Title { get; set; }
        public string? Performer { get; set; }
        public string? Songwriter { get; set; }
        public string? Isrc { get; set; }
        public MsfAddress? PreGap { get; set; }
        public MsfAddress? PostGap { get; set; }
        public List<string> Flags { get; } = new();
        public List<CueIndex> Indices { get; } = new();

        public CueTrack Build() => new(
            TrackNumber,
            DataType,
            Title,
            Performer,
            Songwriter,
            Isrc,
            PreGap,
            PostGap,
            Flags,
            Indices);
    }
}
