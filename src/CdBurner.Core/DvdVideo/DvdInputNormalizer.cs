using System.Text;
using System.Text.RegularExpressions;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.DvdVideo;

/// <summary>
/// Categorization of user-provided input for DVD-Video authoring or burning.
/// </summary>
public enum DvdInputKind
{
    /// <summary>
    /// Input is empty, missing, or contains no recognizable video files or DVD structures.
    /// </summary>
    EmptyOrInvalid,

    /// <summary>
    /// Input is an existing, 100% compliant DVD-Video VIDEO_TS directory ready for immediate burning.
    /// </summary>
    CompliantVideoTs,

    /// <summary>
    /// Input contains DVD-Video files (IFO, BUP, VOB) with non-compliant quirks (lowercase names,
    /// missing BUPs, non-sequential title sets, or extra junk files) that can be automatically repaired.
    /// </summary>
    RepairableVideoTs,

    /// <summary>
    /// Input consists of arbitrary movie video files (MKV, MP4, AVI, MOV, etc.) requiring full authoring.
    /// </summary>
    MovieFiles
}

/// <summary>
/// Analysis and normalization result describing the nature of the DVD input.
/// </summary>
public sealed class DvdNormalizedInputResult
{
    public DvdInputKind Kind { get; init; } = DvdInputKind.EmptyOrInvalid;
    public bool CanAuthorOrBurn => Kind != DvdInputKind.EmptyOrInvalid;
    public bool RequiresAuthoring => Kind is DvdInputKind.MovieFiles or DvdInputKind.RepairableVideoTs;
    public IReadOnlyList<string> MovieFilePaths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> CleanedMovieTitles { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> IgnoredFiles { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> NormalizationNotes { get; init; } = Array.Empty<string>();
    public string SuggestedVolumeLabel { get; init; } = "DVD_VIDEO";
    public string SuggestedMenuTitle { get; init; } = "DVD Video";
    public string? ExistingVideoTsPath { get; init; }
}

/// <summary>
/// Inspects, cleans, and normalizes user input (movie files or existing VIDEO_TS folders)
/// preparing them for universal one-click DVD authoring and burning.
/// </summary>
public static class DvdInputNormalizer
{
    public static IReadOnlySet<string> SupportedVideoExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".webm", ".mpg", ".mpeg", ".m2v",
        ".ts", ".m2ts", ".mts", ".flv", ".f4v", ".ogv", ".3gp", ".3g2", ".asf", ".divx",
        ".xvid", ".rmvb", ".vob", ".dat"
    };

    private static readonly HashSet<string> IgnoredExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".nfo", ".txt", ".srt", ".sub", ".idx", ".ass", ".ssa", ".vtt",
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".ico",
        ".db", ".ds_store", ".ini",
        ".torrent", ".nzb", ".sfv", ".md5", ".sha1", ".par2",
        ".url", ".lnk", ".exe", ".dll", ".bat", ".cmd", ".ps1",
        ".zip", ".rar", ".7z", ".part", ".tmp", ".crdownload",
        ".json", ".xml", ".log", ".cue", ".iso"
    };

    private static readonly HashSet<string> IgnoredFilenames = new(StringComparer.OrdinalIgnoreCase)
    {
        "thumbs.db", ".ds_store", "desktop.ini"
    };

    private static readonly Regex ReleaseGroupTagsRegex = new(
        @"(?i)\b(1080p|720p|480p|2160p|4k|bluray|blu-ray|brrip|bdrip|web-dl|webrip|web|hdrip|dvdrip|hdtv|x264|x265|h264|h\.264|h265|hevc|aac|ac3|dts|dts-hd|truehd|atmos|ddp5\.1|dd5\.1|10bit|remux|proper|repack|extended|unrated|directors\.cut|criterion|ita|eng|fre|ger|spa|yify|rarbg|eztv|galaxytv|cine|xvid|divx)\b",
        RegexOptions.Compiled);

    private static readonly Regex BracketedTagRegex = new(@"\[[^\]]*\]", RegexOptions.Compiled);
    private static readonly Regex YearRegex = new(@"\b(19\d{2}|20\d{2})\b", RegexOptions.Compiled);
    private static readonly Regex VtsFilePattern = new(@"^VTS_(?<set>\d{2})_(?<index>\d)\.(?<ext>IFO|BUP|VOB)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex VmgFilePattern = new(@"^VIDEO_TS\.(?<ext>IFO|BUP|VOB)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Checks whether the given file path has a supported video container extension.
    /// </summary>
    public static bool IsSupportedVideoFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string ext = Path.GetExtension(path);
        return SupportedVideoExtensions.Contains(ext);
    }

    /// <summary>
    /// Checks whether the given file path represents a useless or non-video accessory file
    /// (subtitles, NFOs, artwork, checksums, torrent files, system files) that should be ignored during authoring.
    /// </summary>
    public static bool IsIgnoredUselessFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string name = Path.GetFileName(path);
        if (IgnoredFilenames.Contains(name)) return true;

        string ext = Path.GetExtension(path);
        return IgnoredExtensions.Contains(ext);
    }

    /// <summary>
    /// Cleans release group tags, resolution tags, audio codec info, and delimiters from a filename
    /// producing a human-friendly movie title (e.g. "The.Matrix.1999.1080p.BluRay.x264.mkv" -> "The Matrix (1999)").
    /// </summary>
    public static string CleanMovieTitle(string fileOrFolderName)
    {
        if (string.IsNullOrWhiteSpace(fileOrFolderName)) return "Movie";

        string name = Path.GetFileName(fileOrFolderName);
        if (IsSupportedVideoFile(name) || !string.IsNullOrEmpty(Path.GetExtension(name)))
        {
            name = Path.GetFileNameWithoutExtension(name);
        }

        // Remove bracketed tags like [RARBG] or [1080p]
        name = BracketedTagRegex.Replace(name, " ");

        // Extract year if present
        var yearMatch = YearRegex.Match(name);
        string? year = yearMatch.Success ? yearMatch.Value : null;

        // If year was found, remove text after the year
        if (yearMatch.Success)
        {
            name = name[..yearMatch.Index];
        }

        // Clean release tags
        name = ReleaseGroupTagsRegex.Replace(name, " ");

        // Replace dots, underscores, hyphens with spaces
        name = name.Replace('.', ' ').Replace('_', ' ');

        // Collapse multiple spaces
        name = Regex.Replace(name, @"\s+", " ").Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            name = Path.GetFileNameWithoutExtension(fileOrFolderName);
        }

        // Capitalize words
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        for (int i = 0; i < words.Length; i++)
        {
            string w = words[i];
            if (w.Length == 1)
            {
                sb.Append(char.ToUpperInvariant(w[0]));
            }
            else
            {
                sb.Append(char.ToUpperInvariant(w[0]));
                sb.Append(w[1..].ToLowerInvariant());
            }

            if (i < words.Length - 1)
            {
                sb.Append(' ');
            }
        }

        string cleaned = sb.ToString();

        if (!string.IsNullOrEmpty(year) && !cleaned.Contains(year, StringComparison.Ordinal))
        {
            cleaned = $"{cleaned} ({year})";
        }

        return string.IsNullOrWhiteSpace(cleaned) ? "Movie" : cleaned;
    }

    /// <summary>
    /// Sanitizes an input string into a DVD-compliant ISO/UDF volume label
    /// (uppercase ASCII A-Z, 0-9, and underscores, max 32 characters).
    /// </summary>
    public static string SanitizeVolumeLabel(string rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return "DVD_VIDEO";

        var sb = new StringBuilder();
        foreach (char c in rawName.ToUpperInvariant())
        {
            if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_')
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('_');
            }
        }

        string result = Regex.Replace(sb.ToString(), @"_+", "_").Trim('_');
        if (string.IsNullOrWhiteSpace(result))
        {
            return "DVD_VIDEO";
        }

        if (result.Length > 32)
        {
            result = result[..32].TrimEnd('_');
        }

        return string.IsNullOrWhiteSpace(result) ? "DVD_VIDEO" : result;
    }

    /// <summary>
    /// Analyzes an input path or semicolon/pipe-separated path list.
    /// </summary>
    public static DvdNormalizedInputResult AnalyzeInput(string pathOrList)
    {
        if (string.IsNullOrWhiteSpace(pathOrList))
        {
            return new DvdNormalizedInputResult { Kind = DvdInputKind.EmptyOrInvalid };
        }

        var paths = pathOrList.Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return AnalyzeInputs(paths);
    }

    /// <summary>
    /// Analyzes multiple paths and determines whether they represent a compliant VIDEO_TS,
    /// a repairable VIDEO_TS, or arbitrary movie files requiring authoring.
    /// </summary>
    public static DvdNormalizedInputResult AnalyzeInputs(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var validPaths = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Path.GetFullPath).Distinct().ToList();
        if (validPaths.Count == 0)
        {
            return new DvdNormalizedInputResult { Kind = DvdInputKind.EmptyOrInvalid };
        }

        var notes = new List<string>();

        // Check if a single directory was provided that might be a VIDEO_TS or parent folder
        if (validPaths.Count == 1 && Directory.Exists(validPaths[0]))
        {
            string dir = validPaths[0];
            string videoTsDir = dir;

            if (!string.Equals(Path.GetFileName(dir), "VIDEO_TS", StringComparison.OrdinalIgnoreCase))
            {
                string candidate = Path.Combine(dir, "VIDEO_TS");
                if (Directory.Exists(candidate))
                {
                    videoTsDir = candidate;
                }
            }

            // Inspect directory for DVD-Video files
            var dirFiles = Directory.GetFiles(videoTsDir, "*.*", SearchOption.TopDirectoryOnly);
            bool hasIfo = dirFiles.Any(f => f.EndsWith(".ifo", StringComparison.OrdinalIgnoreCase));
            bool hasVob = dirFiles.Any(f => f.EndsWith(".vob", StringComparison.OrdinalIgnoreCase));

            if (hasIfo || hasVob)
            {
                var validator = new DvdVideoValidator();
                var valResult = validator.ValidateDirectory(videoTsDir);

                // Check for non-standard files in directory
                bool hasExtraFiles = valResult.Warnings.Any(w => w.Contains("Non-standard file", StringComparison.OrdinalIgnoreCase));
                bool hasLowercase = valResult.Errors.Any(e => e.Contains("contains lowercase characters", StringComparison.OrdinalIgnoreCase));
                bool hasMissingBup = valResult.Errors.Any(e => e.Contains("missing required", StringComparison.OrdinalIgnoreCase) && e.Contains(".BUP", StringComparison.OrdinalIgnoreCase));
                bool hasBupMismatch = valResult.Errors.Any(e => e.Contains("not byte-identical", StringComparison.OrdinalIgnoreCase) || e.Contains("mismatch", StringComparison.OrdinalIgnoreCase));
                bool hasNonSequential = valResult.Errors.Any(e => e.Contains("sequentially numbered", StringComparison.OrdinalIgnoreCase));

                // If only VOB files exist without any valid IFO files, treat VOBs as movie files for authoring
                if (!hasIfo)
                {
                    notes.Add("Found loose .VOB files with no .IFO navigation structures. Treating .VOB files as movie tracks for authoring.");
                }
                else if (valResult.IsValid && !hasExtraFiles)
                {
                    notes.Add("Found 100% compliant DVD-Video VIDEO_TS structure.");
                    string folderName = Path.GetFileName(Path.GetDirectoryName(videoTsDir) ?? videoTsDir);
                    return new DvdNormalizedInputResult
                    {
                        Kind = DvdInputKind.CompliantVideoTs,
                        ExistingVideoTsPath = videoTsDir,
                        NormalizationNotes = notes,
                        SuggestedVolumeLabel = SanitizeVolumeLabel(folderName),
                        SuggestedMenuTitle = CleanMovieTitle(folderName)
                    };
                }
                else
                {
                    // Repairable VIDEO_TS
                    notes.Add($"Directory contains repairable DVD-Video structures ({valResult.Errors.Count} issue(s) detected: lowercase names, missing BUPs, or extra files).");
                    string folderName = Path.GetFileName(Path.GetDirectoryName(videoTsDir) ?? videoTsDir);
                    return new DvdNormalizedInputResult
                    {
                        Kind = DvdInputKind.RepairableVideoTs,
                        ExistingVideoTsPath = videoTsDir,
                        NormalizationNotes = notes,
                        SuggestedVolumeLabel = SanitizeVolumeLabel(folderName),
                        SuggestedMenuTitle = CleanMovieTitle(folderName)
                    };
                }
            }
        }

        // Collect movie files and ignored files across all provided paths
        var movieFiles = new List<string>();
        var ignoredFiles = new List<string>();

        foreach (var path in validPaths)
        {
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories))
                {
                    CategorizeFile(file, movieFiles, ignoredFiles);
                }
            }
            else if (File.Exists(path))
            {
                CategorizeFile(path, movieFiles, ignoredFiles);
            }
        }

        // Filter out small sample files if bigger movie files exist (> 20 MB)
        const long sampleSizeLimit = 20 * 1024 * 1024; // 20 MB
        bool hasFullSizedMovie = movieFiles.Any(f =>
        {
            try { return new FileInfo(f).Length >= sampleSizeLimit; }
            catch { return false; }
        });

        if (hasFullSizedMovie)
        {
            var filteredMovies = new List<string>();
            foreach (var m in movieFiles)
            {
                try
                {
                    var fi = new FileInfo(m);
                    if (fi.Length < sampleSizeLimit && Path.GetFileName(m).Contains("sample", StringComparison.OrdinalIgnoreCase))
                    {
                        ignoredFiles.Add(m);
                        notes.Add($"Ignored sample video file: '{Path.GetFileName(m)}' ({fi.Length:N0} bytes).");
                        continue;
                    }
                }
                catch
                {
                    // Ignore file access issues
                }
                filteredMovies.Add(m);
            }
            movieFiles = filteredMovies;
        }

        if (movieFiles.Count == 0)
        {
            notes.Add("No supported video files or valid DVD structures were discovered.");
            return new DvdNormalizedInputResult
            {
                Kind = DvdInputKind.EmptyOrInvalid,
                IgnoredFiles = ignoredFiles,
                NormalizationNotes = notes
            };
        }

        // Generate cleaned movie titles
        var cleanedTitles = movieFiles.Select(CleanMovieTitle).ToList();

        string suggestedLabel = "DVD_VIDEO";
        string suggestedMenu = "DVD Video";

        if (cleanedTitles.Count == 1)
        {
            suggestedLabel = SanitizeVolumeLabel(cleanedTitles[0]);
            suggestedMenu = cleanedTitles[0];
        }
        else if (validPaths.Count == 1 && Directory.Exists(validPaths[0]))
        {
            string folderName = Path.GetFileName(validPaths[0]);
            suggestedLabel = SanitizeVolumeLabel(folderName);
            suggestedMenu = CleanMovieTitle(folderName);
        }
        else if (cleanedTitles.Count > 1)
        {
            suggestedLabel = SanitizeVolumeLabel(cleanedTitles[0]);
            suggestedMenu = "Movie Collection";
        }

        notes.Add($"Identified {movieFiles.Count} movie title(s) for DVD authoring; ignored {ignoredFiles.Count} accessory file(s).");

        return new DvdNormalizedInputResult
        {
            Kind = DvdInputKind.MovieFiles,
            MovieFilePaths = movieFiles,
            CleanedMovieTitles = cleanedTitles,
            IgnoredFiles = ignoredFiles,
            NormalizationNotes = notes,
            SuggestedVolumeLabel = suggestedLabel,
            SuggestedMenuTitle = suggestedMenu
        };
    }

    /// <summary>
    /// Repairs and normalizes an existing VIDEO_TS directory into target directory:
    /// uppercasing filenames, filtering junk, renumbering VTS sets sequentially from 01,
    /// creating identical BUP files, ensuring VMG/VTS signatures, and creating an AUDIO_TS directory.
    /// </summary>
    public static void RepairVideoTsDirectory(string sourceDirPath, string targetVideoTsDirPath, List<string>? logNotes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetVideoTsDirPath);

        string srcDir = sourceDirPath;
        if (!string.Equals(Path.GetFileName(srcDir), "VIDEO_TS", StringComparison.OrdinalIgnoreCase))
        {
            string candidate = Path.Combine(srcDir, "VIDEO_TS");
            if (Directory.Exists(candidate))
            {
                srcDir = candidate;
            }
        }

        Directory.CreateDirectory(targetVideoTsDirPath);

        // Ensure sibling AUDIO_TS exists
        string? parent = Path.GetDirectoryName(targetVideoTsDirPath);
        if (!string.IsNullOrEmpty(parent))
        {
            string audioTs = Path.Combine(parent, "AUDIO_TS");
            Directory.CreateDirectory(audioTs);
            logNotes?.Add($"Created or verified '{audioTs}'.");
        }

        var sourceFiles = Directory.GetFiles(srcDir, "*.*", SearchOption.TopDirectoryOnly);

        // Filter and inspect DVD files
        var vmgFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var vtsSetNumbers = new SortedSet<int>();
        var vtsFilesBySet = new Dictionary<int, List<string>>();

        foreach (var file in sourceFiles)
        {
            string name = Path.GetFileName(file);
            var vmgMatch = VmgFilePattern.Match(name);
            if (vmgMatch.Success)
            {
                vmgFiles[vmgMatch.Groups["ext"].Value.ToUpperInvariant()] = file;
                continue;
            }

            var vtsMatch = VtsFilePattern.Match(name);
            if (vtsMatch.Success)
            {
                int setNum = int.Parse(vtsMatch.Groups["set"].Value);
                vtsSetNumbers.Add(setNum);
                if (!vtsFilesBySet.TryGetValue(setNum, out var list))
                {
                    list = new List<string>();
                    vtsFilesBySet[setNum] = list;
                }
                list.Add(file);
                continue;
            }

            logNotes?.Add($"Ignored non-DVD file during repair: '{name}'.");
        }

        // Map old Title Set numbers to sequential 1, 2, ...
        var renumberMap = new Dictionary<int, int>();
        int newIndex = 1;
        foreach (int oldSet in vtsSetNumbers)
        {
            renumberMap[oldSet] = newIndex++;
        }
        bool anySetRenumbered = renumberMap.Any(kvp => kvp.Key != kvp.Value);

        // 1. Repair VIDEO_TS.VOB if present
        int menuVobSectors = 0;
        if (vmgFiles.TryGetValue("VOB", out var srcVmgVob))
        {
            string dstVmgVob = Path.Combine(targetVideoTsDirPath, "VIDEO_TS.VOB");
            CopyOrUppercaseInPlace(srcVmgVob, dstVmgVob);
            menuVobSectors = (int)(new FileInfo(dstVmgVob).Length / OpticalConstants.DataSectorSize);
            logNotes?.Add($"Copied '{Path.GetFileName(srcVmgVob)}' to 'VIDEO_TS.VOB' ({menuVobSectors} sectors).");
        }

        // 2. Repair VIDEO_TS.IFO and VIDEO_TS.BUP
        string dstVmgIfo = Path.Combine(targetVideoTsDirPath, "VIDEO_TS.IFO");
        string dstVmgBup = Path.Combine(targetVideoTsDirPath, "VIDEO_TS.BUP");

        bool hasValidVmgIfo = false;
        if (!anySetRenumbered && vmgFiles.TryGetValue("IFO", out var srcVmgIfo))
        {
            try
            {
                byte[] header = new byte[DvdVideoValidator.SignatureByteLength];
                using (var fs = File.OpenRead(srcVmgIfo))
                {
                    int r = fs.Read(header);
                    if (r == DvdVideoValidator.SignatureByteLength &&
                        Encoding.ASCII.GetString(header) == DvdVideoValidator.VmgMagicSignature)
                    {
                        CopyOrUppercaseInPlace(srcVmgIfo, dstVmgIfo);
                        hasValidVmgIfo = true;
                        logNotes?.Add("Copied valid existing 'VIDEO_TS.IFO'.");
                    }
                }
            }
            catch
            {
                // Fall back to synthesis
            }
        }

        if (!hasValidVmgIfo)
        {
            byte[] synthesizedVmg = DvdIfoGenerator.BuildVmgIfo(
                titleSetCount: Math.Max(1, vtsSetNumbers.Count),
                standard: DvdVideoStandard.Ntsc,
                aspect: DvdAspectRatio.Widescreen16x9,
                menuVobSectors: menuVobSectors);
            File.WriteAllBytes(dstVmgIfo, synthesizedVmg);
            logNotes?.Add("Synthesized compliant 'VIDEO_TS.IFO'.");
        }

        // BUP must be exact byte-clone of IFO
        File.Copy(dstVmgIfo, dstVmgBup, overwrite: true);
        logNotes?.Add("Created byte-identical backup 'VIDEO_TS.BUP'.");

        // 3. Repair each Video Title Set (VTS_xx)
        foreach (var (oldSet, files) in vtsFilesBySet)
        {
            int mappedSet = renumberMap[oldSet];
            string dstIfoName = $"VTS_{mappedSet:D2}_0.IFO";
            string dstBupName = $"VTS_{mappedSet:D2}_0.BUP";
            string dstIfoPath = Path.Combine(targetVideoTsDirPath, dstIfoName);
            string dstBupPath = Path.Combine(targetVideoTsDirPath, dstBupName);

            string? srcVtsIfo = files.FirstOrDefault(f => Path.GetFileName(f).EndsWith("_0.IFO", StringComparison.OrdinalIgnoreCase));
            int vtsMenuVobSectors = 0;
            int totalVobSectors = 0;

            // Copy and renumber VOBs
            foreach (var vobFile in files.Where(f => f.EndsWith(".VOB", StringComparison.OrdinalIgnoreCase)))
            {
                var match = VtsFilePattern.Match(Path.GetFileName(vobFile));
                int index = int.Parse(match.Groups["index"].Value);
                string newVobName = $"VTS_{mappedSet:D2}_{index}.VOB";
                string newVobPath = Path.Combine(targetVideoTsDirPath, newVobName);

                CopyOrUppercaseInPlace(vobFile, newVobPath);
                int vobSectors = (int)(new FileInfo(newVobPath).Length / OpticalConstants.DataSectorSize);
                if (index == 0)
                {
                    vtsMenuVobSectors += vobSectors;
                }
                else
                {
                    totalVobSectors += vobSectors;
                }
                logNotes?.Add($"Normalized VOB '{Path.GetFileName(vobFile)}' -> '{newVobName}'.");
            }

            bool hasValidVtsIfo = false;
            if (srcVtsIfo != null)
            {
                try
                {
                    byte[] header = new byte[DvdVideoValidator.SignatureByteLength];
                    using (var fs = File.OpenRead(srcVtsIfo))
                    {
                        int r = fs.Read(header);
                        if (r == DvdVideoValidator.SignatureByteLength &&
                            Encoding.ASCII.GetString(header) == DvdVideoValidator.VtsMagicSignature)
                        {
                            CopyOrUppercaseInPlace(srcVtsIfo, dstIfoPath);
                            hasValidVtsIfo = true;
                            logNotes?.Add($"Copied valid '{dstIfoName}'.");
                        }
                    }
                }
                catch
                {
                    // Fall back to synthesis
                }
            }

            if (!hasValidVtsIfo)
            {
                byte[] synthVts = DvdIfoGenerator.BuildVtsIfo(
                    titleSetNumber: mappedSet,
                    standard: DvdVideoStandard.Ntsc,
                    aspect: DvdAspectRatio.Widescreen16x9,
                    titleVobTotalSectors: Math.Max(16, totalVobSectors),
                    menuVobSectors: vtsMenuVobSectors);
                File.WriteAllBytes(dstIfoPath, synthVts);
                logNotes?.Add($"Synthesized compliant '{dstIfoName}'.");
            }

            File.Copy(dstIfoPath, dstBupPath, overwrite: true);
            logNotes?.Add($"Created byte-identical backup '{dstBupName}'.");
        }
    }

    private static void CopyOrUppercaseInPlace(string sourcePath, string destPath)
    {
        string fullSrc = Path.GetFullPath(sourcePath);
        string fullDst = Path.GetFullPath(destPath);
        if (string.Equals(fullSrc, fullDst, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(Path.GetFileName(fullSrc), Path.GetFileName(fullDst), StringComparison.Ordinal))
            {
                string tempPath = fullDst + $".tmp_{Guid.NewGuid():N}";
                File.Move(fullSrc, tempPath);
                File.Move(tempPath, fullDst);
            }
            return;
        }

        File.Copy(fullSrc, fullDst, overwrite: true);
    }

    private static void CategorizeFile(string path, List<string> movieFiles, List<string> ignoredFiles)
    {
        try
        {
            var fi = new FileInfo(path);
            if (fi.Length == 0)
            {
                ignoredFiles.Add(path);
                return;
            }

            if (IsSupportedVideoFile(path))
            {
                movieFiles.Add(path);
            }
            else if (IsIgnoredUselessFile(path))
            {
                ignoredFiles.Add(path);
            }
            else
            {
                // Unrecognized extension - also ignore for DVD authoring
                ignoredFiles.Add(path);
            }
        }
        catch
        {
            // Ignore file access errors
        }
    }
}
