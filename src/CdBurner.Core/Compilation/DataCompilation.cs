using CdBurner.Core.Primitives;

namespace CdBurner.Core.Compilation;

/// <summary>
/// Represents a data disc compilation containing a virtual filesystem hierarchy,
/// volume metadata, and target optical media capacity configuration.
/// </summary>
public class DataCompilation
{
    /// <summary>
    /// Root directory of the compilation virtual filesystem.
    /// </summary>
    public CompilationDirectory Root { get; }

    /// <summary>
    /// Volume identifier label for ISO 9660, Joliet, and UDF descriptors.
    /// </summary>
    public string VolumeLabel { get; set; }

    /// <summary>
    /// Default duplicate file collision handling policy.
    /// </summary>
    public DuplicateFilePolicy DefaultDuplicatePolicy { get; set; }

    /// <summary>
    /// Target optical media type used for real-time capacity calculations.
    /// </summary>
    public DiscMediaType TargetMediaType { get; set; }

    public DataCompilation(
        string volumeLabel = "CDBURNER_DISC",
        DiscMediaType targetMediaType = DiscMediaType.CdR,
        DuplicateFilePolicy defaultDuplicatePolicy = DuplicateFilePolicy.Overwrite)
    {
        VolumeLabel = string.IsNullOrWhiteSpace(volumeLabel) ? "CDBURNER_DISC" : volumeLabel;
        TargetMediaType = targetMediaType;
        DefaultDuplicatePolicy = defaultDuplicatePolicy;
        Root = CompilationDirectory.CreateRoot();
    }

    /// <summary>
    /// Adds a file at the specified virtual path (e.g. "/Docs/Readme.txt").
    /// Intermediate directories will be created automatically if they do not exist.
    /// </summary>
    public CompilationFile? AddFile(
        string virtualPath,
        string sourceFilePath,
        long sizeBytes,
        DuplicateFilePolicy? policy = null,
        int? importedStartLba = null,
        Func<Stream>? streamFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(virtualPath);

        var (dirPath, fileName) = SplitVirtualPath(virtualPath);
        var dir = GetOrAddDirectory(dirPath);

        var file = new CompilationFile(
            name: fileName,
            sourceFilePath: sourceFilePath,
            sizeBytes: sizeBytes,
            importedStartLba: importedStartLba,
            streamFactory: streamFactory);

        return dir.AddFile(file, policy ?? DefaultDuplicatePolicy);
    }

    /// <summary>
    /// Adds an in-memory stream file at the specified virtual path.
    /// </summary>
    public CompilationFile? AddFile(
        string virtualPath,
        Func<Stream> streamFactory,
        long sizeBytes,
        DuplicateFilePolicy? policy = null)
    {
        return AddFile(virtualPath, string.Empty, sizeBytes, policy, null, streamFactory);
    }

    /// <summary>
    /// Adds a byte array as an in-memory file at the specified virtual path.
    /// </summary>
    public CompilationFile? AddFile(
        string virtualPath,
        byte[] content,
        DuplicateFilePolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        return AddFile(virtualPath, string.Empty, content.Length, policy, null, () => new MemoryStream(content, writable: false));
    }

    /// <summary>
    /// Navigates or creates a directory path hierarchy starting from the root.
    /// </summary>
    public CompilationDirectory GetOrAddDirectory(string virtualPath)
    {
        if (string.IsNullOrWhiteSpace(virtualPath) || virtualPath == "/")
        {
            return Root;
        }

        return Root.AddDirectory(virtualPath);
    }

    /// <summary>
    /// Finds a node by its absolute or relative virtual path.
    /// </summary>
    public CompilationNode? FindByVirtualPath(string virtualPath) => Root.FindByVirtualPath(virtualPath);

    /// <summary>
    /// Finds a file node by its virtual path.
    /// </summary>
    public CompilationFile? FindFileByVirtualPath(string virtualPath) => Root.FindFileByVirtualPath(virtualPath);

    /// <summary>
    /// Finds a directory node by its virtual path.
    /// </summary>
    public CompilationDirectory? FindDirectoryByVirtualPath(string virtualPath) => Root.FindDirectoryByVirtualPath(virtualPath);

    /// <summary>
    /// Returns all files recursively contained in this compilation.
    /// </summary>
    public IReadOnlyList<CompilationFile> GetAllFiles() => Root.GetAllFilesRecursive();

    /// <summary>
    /// Calculates the current capacity assessment for this compilation.
    /// </summary>
    public CapacityAssessment CalculateCapacity(
        DiscMediaType? mediaType = null,
        bool includeIso9660 = true,
        bool includeJoliet = true,
        bool includeUdf = true,
        bool isBootable = false)
    {
        return CompilationCapacityCalculator.Calculate(
            Root,
            mediaType ?? TargetMediaType,
            includeIso9660,
            includeJoliet,
            includeUdf,
            isBootable);
    }

    private static (string DirPath, string FileName) SplitVirtualPath(string virtualPath)
    {
        string normalized = virtualPath.Replace('\\', '/').Trim('/');
        int lastSlash = normalized.LastIndexOf('/');
        if (lastSlash < 0)
        {
            return ("/", normalized);
        }

        string dir = normalized[..lastSlash];
        string file = normalized[(lastSlash + 1)..];
        return (dir, file);
    }
}
