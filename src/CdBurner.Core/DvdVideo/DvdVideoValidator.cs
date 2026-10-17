using System.Text;
using System.Text.RegularExpressions;
using CdBurner.Core.Compilation;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.DvdVideo;

/// <summary>
/// Abstraction representing a file entry within a DVD-Video directory structure.
/// </summary>
public interface IDvdVideoFileEntry
{
    /// <summary>
    /// File name including extension (e.g. "VIDEO_TS.IFO").
    /// </summary>
    string Name { get; }

    /// <summary>
    /// File size in bytes.
    /// </summary>
    long SizeBytes { get; }

    /// <summary>
    /// Opens a readable seekable stream containing the file content.
    /// </summary>
    Stream OpenRead();
}

/// <summary>
/// In-memory or delegate-backed DVD-Video file entry.
/// </summary>
public sealed class DvdVideoMemoryFileEntry : IDvdVideoFileEntry
{
    private readonly byte[] _content;

    public string Name { get; }
    public long SizeBytes => _content.Length;

    public DvdVideoMemoryFileEntry(string name, byte[] content)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _content = content ?? throw new ArgumentNullException(nameof(content));
    }

    public Stream OpenRead() => new MemoryStream(_content, writable: false);
}

/// <summary>
/// Wrapper adapting a <see cref="CompilationFile"/> to <see cref="IDvdVideoFileEntry"/>.
/// </summary>
public sealed class CompilationDvdVideoFileEntry : IDvdVideoFileEntry
{
    private readonly CompilationFile _file;

    public string Name => _file.Name;
    public long SizeBytes => _file.SizeBytes;

    public CompilationDvdVideoFileEntry(CompilationFile file)
    {
        _file = file ?? throw new ArgumentNullException(nameof(file));
    }

    public Stream OpenRead() => _file.OpenRead();
}

/// <summary>
/// Wrapper adapting a filesystem file to <see cref="IDvdVideoFileEntry"/>.
/// </summary>
public sealed class FileSystemDvdVideoFileEntry : IDvdVideoFileEntry
{
    private readonly FileInfo _fileInfo;

    public string Name => _fileInfo.Name;
    public long SizeBytes => _fileInfo.Length;

    public FileSystemDvdVideoFileEntry(FileInfo fileInfo)
    {
        _fileInfo = fileInfo ?? throw new ArgumentNullException(nameof(fileInfo));
    }

    public Stream OpenRead() => _fileInfo.OpenRead();
}

/// <summary>
/// Results of a DVD-Video structure and integrity validation operation.
/// </summary>
public sealed class DvdVideoValidationResult
{
    /// <summary>
    /// Gets whether the DVD-Video structure is fully valid and compliant with DVD-Video specifications.
    /// </summary>
    public bool IsValid => Errors.Count == 0;

    /// <summary>
    /// List of fatal or non-compliant errors.
    /// </summary>
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    /// <summary>
    /// List of warnings or non-fatal recommendations.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>
    /// List of title set numbers found in the compilation (e.g. 1, 2).
    /// </summary>
    public IReadOnlyList<int> TitleSetNumbers { get; init; } = Array.Empty<int>();

    /// <summary>
    /// Indicates whether an AUDIO_TS directory was present alongside VIDEO_TS.
    /// </summary>
    public bool HasAudioTs { get; init; }

    /// <summary>
    /// Total count of valid files validated.
    /// </summary>
    public int FileCount { get; init; }

    /// <summary>
    /// Total byte size of all DVD-Video files combined.
    /// </summary>
    public long TotalSizeBytes { get; init; }

    /// <summary>
    /// Indicates whether a Video Manager Menu VOB (VIDEO_TS.VOB) was present.
    /// </summary>
    public bool HasVmgMenuVob { get; init; }

    public override string ToString()
    {
        return IsValid
            ? $"Valid DVD-Video: {TitleSetNumbers.Count} Title Set(s), {FileCount} files, {TotalSizeBytes:N0} bytes, AudioTS: {HasAudioTs}"
            : $"Invalid DVD-Video ({Errors.Count} error(s)): {string.Join("; ", Errors)}";
    }
}

/// <summary>
/// Validates DVD-Video structure, uppercase naming standards, 12-byte ASCII magic signatures
/// (DVDVIDEO-VMG and DVDVIDEO-VTS), and ensures .BUP backup files are byte-identical to their .IFO counterparts.
/// </summary>
public class DvdVideoValidator
{
    /// <summary>
    /// Magic 12-byte ASCII signature required at bytes 0..11 of VIDEO_TS.IFO.
    /// </summary>
    public const string VmgMagicSignature = "DVDVIDEO-VMG";

    /// <summary>
    /// Magic 12-byte ASCII signature required at bytes 0..11 of VTS_xx_0.IFO.
    /// </summary>
    public const string VtsMagicSignature = "DVDVIDEO-VTS";

    /// <summary>
    /// Length in bytes of the DVD-Video IFO header signature (12 bytes).
    /// </summary>
    public const int SignatureByteLength = 12;

    private static readonly Regex VtsFileRegex = new(@"^VTS_(?<set>\d{2})_(?<index>\d)\.(?<ext>IFO|BUP|VOB)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex VmgFileRegex = new(@"^VIDEO_TS\.(?<ext>IFO|BUP|VOB)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Validates a <see cref="DataCompilation"/> virtual filesystem tree.
    /// </summary>
    public DvdVideoValidationResult ValidateCompilation(DataCompilation compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);

        var videoTsDir = compilation.Root.FindDirectoryByVirtualPath("/VIDEO_TS")
                         ?? compilation.Root.Children.OfType<CompilationDirectory>()
                             .FirstOrDefault(d => string.Equals(d.Name, "VIDEO_TS", StringComparison.OrdinalIgnoreCase));

        bool hasAudioTs = compilation.Root.FindDirectoryByVirtualPath("/AUDIO_TS") != null
                          || compilation.Root.Children.OfType<CompilationDirectory>()
                              .Any(d => string.Equals(d.Name, "AUDIO_TS", StringComparison.OrdinalIgnoreCase));

        if (videoTsDir != null)
        {
            return ValidateDirectory(videoTsDir, hasAudioTs);
        }

        // Check if root directory itself contains VIDEO_TS.IFO
        var rootIfo = compilation.Root.FindFileByVirtualPath("/VIDEO_TS.IFO")
                      ?? compilation.Root.Children.OfType<CompilationFile>()
                          .FirstOrDefault(f => string.Equals(f.Name, "VIDEO_TS.IFO", StringComparison.OrdinalIgnoreCase));

        if (rootIfo != null)
        {
            return ValidateDirectory(compilation.Root, hasAudioTs);
        }

        return new DvdVideoValidationResult
        {
            Errors = new[] { "Neither a 'VIDEO_TS' directory nor root 'VIDEO_TS.IFO' was found in the compilation." },
            Warnings = Array.Empty<string>(),
            HasAudioTs = hasAudioTs
        };
    }

    /// <summary>
    /// Validates a <see cref="CompilationDirectory"/> representing a VIDEO_TS folder.
    /// </summary>
    public DvdVideoValidationResult ValidateDirectory(CompilationDirectory videoTsDir, bool hasAudioTs = false)
    {
        ArgumentNullException.ThrowIfNull(videoTsDir);

        var fileEntries = videoTsDir.Children
            .OfType<CompilationFile>()
            .Select(f => (IDvdVideoFileEntry)new CompilationDvdVideoFileEntry(f))
            .ToList();

        return ValidateFiles(fileEntries, hasAudioTs);
    }

    /// <summary>
    /// Validates a physical directory on disk.
    /// </summary>
    public DvdVideoValidationResult ValidateDirectory(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        if (!Directory.Exists(directoryPath))
        {
            return new DvdVideoValidationResult
            {
                Errors = new[] { $"Directory '{directoryPath}' does not exist." }
            };
        }

        var dirInfo = new DirectoryInfo(directoryPath);
        DirectoryInfo videoTsInfo;
        bool hasAudioTs;

        if (string.Equals(dirInfo.Name, "VIDEO_TS", StringComparison.OrdinalIgnoreCase))
        {
            videoTsInfo = dirInfo;
            var parent = dirInfo.Parent;
            hasAudioTs = parent != null && parent.GetDirectories("AUDIO_TS", SearchOption.TopDirectoryOnly).Length > 0;
        }
        else
        {
            var subVideoTs = dirInfo.GetDirectories("VIDEO_TS", SearchOption.TopDirectoryOnly);
            if (subVideoTs.Length > 0)
            {
                videoTsInfo = subVideoTs[0];
                hasAudioTs = dirInfo.GetDirectories("AUDIO_TS", SearchOption.TopDirectoryOnly).Length > 0;
            }
            else
            {
                videoTsInfo = dirInfo;
                hasAudioTs = dirInfo.GetDirectories("AUDIO_TS", SearchOption.TopDirectoryOnly).Length > 0;
            }
        }

        var entries = videoTsInfo.GetFiles()
            .Select(f => (IDvdVideoFileEntry)new FileSystemDvdVideoFileEntry(f))
            .ToList();

        return ValidateFiles(entries, hasAudioTs);
    }

    /// <summary>
    /// Validates a collection of DVD-Video file entries.
    /// </summary>
    public DvdVideoValidationResult ValidateFiles(IReadOnlyList<IDvdVideoFileEntry> files, bool hasAudioTs = false)
    {
        ArgumentNullException.ThrowIfNull(files);

        var errors = new List<string>();
        var warnings = new List<string>();

        if (files.Count == 0)
        {
            errors.Add("VIDEO_TS directory is empty.");
            return new DvdVideoValidationResult
            {
                Errors = errors,
                Warnings = warnings,
                HasAudioTs = hasAudioTs
            };
        }

        long totalSizeBytes = 0;
        bool hasVmgMenuVob = false;

        // 1. Verify uppercase naming and index files
        var fileMap = new Dictionary<string, IDvdVideoFileEntry>(StringComparer.Ordinal);
        var caseInsensitiveMap = new Dictionary<string, IDvdVideoFileEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            totalSizeBytes += file.SizeBytes;

            // Check uppercase
            if (!string.Equals(file.Name, file.Name.ToUpperInvariant(), StringComparison.Ordinal))
            {
                errors.Add($"File '{file.Name}' contains lowercase characters. DVD-Video standard strictly mandates uppercase ASCII file names.");
            }

            if (!fileMap.TryAdd(file.Name, file))
            {
                errors.Add($"Duplicate file entry detected: '{file.Name}'.");
            }

            caseInsensitiveMap.TryAdd(file.Name, file);
        }

        // 2. Validate VIDEO_TS.IFO presence and signature
        if (!caseInsensitiveMap.TryGetValue("VIDEO_TS.IFO", out var vmgIfo))
        {
            errors.Add("Missing required Video Manager Information file: 'VIDEO_TS.IFO'.");
        }
        else
        {
            VerifyIfoHeader(vmgIfo, VmgMagicSignature, errors);
        }

        // 3. Validate VIDEO_TS.BUP presence and byte identity to VIDEO_TS.IFO
        if (!caseInsensitiveMap.TryGetValue("VIDEO_TS.BUP", out var vmgBup))
        {
            errors.Add("Missing required Video Manager Information backup file: 'VIDEO_TS.BUP'.");
        }
        else if (vmgIfo != null)
        {
            VerifyBupIdenticalToIfo(vmgIfo, vmgBup, errors);
        }

        if (caseInsensitiveMap.ContainsKey("VIDEO_TS.VOB"))
        {
            hasVmgMenuVob = true;
        }

        // 4. Identify all Title Sets and validate IFO/BUP pairs
        var titleSetNumbers = new SortedSet<int>();
        var titleSetFiles = new Dictionary<int, List<IDvdVideoFileEntry>>();

        foreach (var file in files)
        {
            var match = VtsFileRegex.Match(file.Name);
            if (match.Success)
            {
                int setNum = int.Parse(match.Groups["set"].Value);
                titleSetNumbers.Add(setNum);

                if (!titleSetFiles.TryGetValue(setNum, out var list))
                {
                    list = new List<IDvdVideoFileEntry>();
                    titleSetFiles[setNum] = list;
                }
                list.Add(file);
            }
            else if (!VmgFileRegex.IsMatch(file.Name))
            {
                // Non-standard file in VIDEO_TS
                warnings.Add($"Non-standard file '{file.Name}' encountered in VIDEO_TS directory.");
            }
        }

        if (titleSetNumbers.Count == 0)
        {
            errors.Add("No Video Title Sets found. A valid DVD-Video compilation must contain at least 'VTS_01_0.IFO' and 'VTS_01_0.BUP'.");
        }
        else
        {
            // Verify sequential numbering starting from 1
            int expected = 1;
            foreach (var setNum in titleSetNumbers)
            {
                if (setNum != expected)
                {
                    errors.Add($"Title Sets must be sequentially numbered starting from 01 with no gaps. Expected {expected:D2}, but found {setNum:D2}.");
                }
                expected++;
            }

            // Verify each title set's IFO and BUP
            foreach (var setNum in titleSetNumbers)
            {
                string ifoName = $"VTS_{setNum:D2}_0.IFO";
                string bupName = $"VTS_{setNum:D2}_0.BUP";

                caseInsensitiveMap.TryGetValue(ifoName, out var vtsIfo);
                caseInsensitiveMap.TryGetValue(bupName, out var vtsBup);

                if (vtsIfo == null)
                {
                    errors.Add($"Title Set {setNum:D2} is missing required information file: '{ifoName}'.");
                }
                else
                {
                    VerifyIfoHeader(vtsIfo, VtsMagicSignature, errors);
                }

                if (vtsBup == null)
                {
                    errors.Add($"Title Set {setNum:D2} is missing required backup file: '{bupName}'.");
                }
                else if (vtsIfo != null)
                {
                    VerifyBupIdenticalToIfo(vtsIfo, vtsBup, errors);
                }
            }
        }

        // 5. AUDIO_TS recommendation
        if (!hasAudioTs)
        {
            warnings.Add("Empty 'AUDIO_TS' directory was not detected. While optional for playback, an empty AUDIO_TS folder is recommended by DVD-Video specifications for legacy standalone player compatibility.");
        }

        return new DvdVideoValidationResult
        {
            Errors = errors,
            Warnings = warnings,
            TitleSetNumbers = titleSetNumbers.ToList(),
            HasAudioTs = hasAudioTs,
            FileCount = files.Count,
            TotalSizeBytes = totalSizeBytes,
            HasVmgMenuVob = hasVmgMenuVob
        };
    }

    private static void VerifyIfoHeader(IDvdVideoFileEntry file, string expectedMagic, List<string> errors)
    {
        if (file.SizeBytes < SignatureByteLength)
        {
            errors.Add($"'{file.Name}' is too small ({file.SizeBytes} bytes) to contain a valid DVD-Video header (minimum {SignatureByteLength} bytes required).");
            return;
        }

        try
        {
            using var stream = file.OpenRead();
            Span<byte> header = stackalloc byte[SignatureByteLength];
            int read = stream.Read(header);

            if (read < SignatureByteLength)
            {
                errors.Add($"'{file.Name}' could not read {SignatureByteLength} header bytes.");
                return;
            }

            string magic = Encoding.ASCII.GetString(header);
            if (!string.Equals(magic, expectedMagic, StringComparison.Ordinal))
            {
                errors.Add($"'{file.Name}' has invalid magic signature '{magic}'. Expected '{expectedMagic}'.");
            }
        }
        catch (Exception ex)
        {
            errors.Add($"Failed to read header from '{file.Name}': {ex.Message}");
        }
    }

    private static void VerifyBupIdenticalToIfo(IDvdVideoFileEntry ifo, IDvdVideoFileEntry bup, List<string> errors)
    {
        if (ifo.SizeBytes != bup.SizeBytes)
        {
            errors.Add($"'{bup.Name}' size ({bup.SizeBytes} bytes) does not match '{ifo.Name}' size ({ifo.SizeBytes} bytes).");
            return;
        }

        try
        {
            using var ifoStream = ifo.OpenRead();
            using var bupStream = bup.OpenRead();

            Span<byte> ifoBuf = stackalloc byte[4096];
            Span<byte> bupBuf = stackalloc byte[4096];

            long offset = 0;
            while (offset < ifo.SizeBytes)
            {
                int ifoRead = ifoStream.Read(ifoBuf);
                int bupRead = bupStream.Read(bupBuf);

                if (ifoRead != bupRead)
                {
                    errors.Add($"Read mismatch between '{ifo.Name}' and '{bup.Name}' at byte offset {offset}.");
                    return;
                }

                if (!ifoBuf[..ifoRead].SequenceEqual(bupBuf[..bupRead]))
                {
                    errors.Add($"'{bup.Name}' is not byte-identical to '{ifo.Name}' at or after byte offset {offset}.");
                    return;
                }

                offset += ifoRead;
            }
        }
        catch (Exception ex)
        {
            errors.Add($"Failed comparing '{bup.Name}' with '{ifo.Name}': {ex.Message}");
        }
    }

    /// <summary>
    /// Helper to generate synthetic IFO bytes with the given ASCII magic signature.
    /// </summary>
    public static byte[] CreateSyntheticIfo(string magic, int sizeBytes = OpticalConstants.DataSectorSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(magic);
        int totalSize = Math.Max(sizeBytes, OpticalConstants.DataSectorSize);
        byte[] buffer = new byte[totalSize];
        byte[] magicBytes = Encoding.ASCII.GetBytes(magic);
        magicBytes.CopyTo(buffer, 0);
        return buffer;
    }

    /// <summary>
    /// Helper to generate synthetic VIDEO_TS.IFO bytes with "DVDVIDEO-VMG" signature.
    /// </summary>
    public static byte[] CreateSyntheticVmgIfo(int sizeBytes = OpticalConstants.DataSectorSize) =>
        CreateSyntheticIfo(VmgMagicSignature, sizeBytes);

    /// <summary>
    /// Helper to generate synthetic VTS_xx_0.IFO bytes with "DVDVIDEO-VTS" signature.
    /// </summary>
    public static byte[] CreateSyntheticVtsIfo(int sizeBytes = OpticalConstants.DataSectorSize) =>
        CreateSyntheticIfo(VtsMagicSignature, sizeBytes);
}
