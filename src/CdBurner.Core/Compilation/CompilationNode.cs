using System.Text.RegularExpressions;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Compilation;

/// <summary>
/// Abstract base class for all nodes in an in-memory virtual optical disc compilation filesystem tree.
/// </summary>
public abstract class CompilationNode
{
    /// <summary>
    /// File or directory node name within its parent directory.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Parent directory containing this node, or <c>null</c> if this is the root directory.
    /// </summary>
    public CompilationDirectory? Parent { get; internal set; }

    /// <summary>
    /// Last write timestamp in UTC.
    /// </summary>
    public DateTime LastWriteTimeUtc { get; set; }

    /// <summary>
    /// Standard file system attributes for this node.
    /// </summary>
    public FileAttributes Attributes { get; set; }

    /// <summary>
    /// Fully qualified virtual path of this node from the compilation root (e.g. "/Docs/Readme.txt").
    /// </summary>
    public string VirtualPath
    {
        get
        {
            if (Parent == null)
            {
                return string.IsNullOrEmpty(Name) || Name == "/" ? "/" : $"/{Name}";
            }

            string parentPath = Parent.VirtualPath;
            return parentPath == "/" ? $"/{Name}" : $"{parentPath}/{Name}";
        }
    }

    protected CompilationNode(
        string name,
        CompilationDirectory? parent = null,
        DateTime? lastWriteTimeUtc = null,
        FileAttributes attributes = FileAttributes.Normal)
    {
        Name = name ?? string.Empty;
        Parent = parent;
        LastWriteTimeUtc = lastWriteTimeUtc ?? DateTime.UtcNow;
        Attributes = attributes;
    }
}

/// <summary>
/// Represents a file node in the virtual compilation filesystem tree.
/// </summary>
public class CompilationFile : CompilationNode
{
    /// <summary>
    /// Physical source file path on disk, or empty if generated in-memory.
    /// </summary>
    public string SourceFilePath { get; set; }

    /// <summary>
    /// File size in bytes.
    /// </summary>
    public long SizeBytes { get; set; }

    /// <summary>
    /// Optional starting LBA on media for multi-session files imported from previous sessions.
    /// When specified, the file data extent is already recorded on disc.
    /// </summary>
    public int? ImportedStartLba { get; set; }

    /// <summary>
    /// Optional stream factory delegate for testing or in-memory content generation without temporary files.
    /// </summary>
    public Func<Stream>? StreamFactory { get; set; }

    /// <summary>
    /// Initializes a new compilation file with source path and size.
    /// </summary>
    public CompilationFile(
        string sourceFilePath,
        long sizeBytes,
        int? importedStartLba = null,
        Func<Stream>? streamFactory = null)
        : base(
            ExtractFileName(sourceFilePath),
            parent: null,
            lastWriteTimeUtc: DateTime.UtcNow,
            attributes: FileAttributes.Normal)
    {
        SourceFilePath = sourceFilePath ?? string.Empty;
        SizeBytes = sizeBytes;
        ImportedStartLba = importedStartLba;
        StreamFactory = streamFactory;
    }

    /// <summary>
    /// Initializes a new compilation file with an explicit node name, source path, and size.
    /// </summary>
    public CompilationFile(
        string name,
        string sourceFilePath,
        long sizeBytes,
        int? importedStartLba = null,
        Func<Stream>? streamFactory = null,
        CompilationDirectory? parent = null,
        DateTime? lastWriteTimeUtc = null,
        FileAttributes attributes = FileAttributes.Normal)
        : base(name, parent, lastWriteTimeUtc, attributes)
    {
        SourceFilePath = sourceFilePath ?? string.Empty;
        SizeBytes = sizeBytes;
        ImportedStartLba = importedStartLba;
        StreamFactory = streamFactory;
    }

    /// <summary>
    /// Initializes a new in-memory compilation file with a custom stream factory.
    /// </summary>
    public CompilationFile(
        string name,
        long sizeBytes,
        Func<Stream> streamFactory)
        : this(name, string.Empty, sizeBytes, null, streamFactory)
    {
    }

    /// <summary>
    /// Opens a readable stream for this file's data content.
    /// </summary>
    public Stream OpenRead()
    {
        if (StreamFactory != null)
        {
            return StreamFactory();
        }

        if (!string.IsNullOrEmpty(SourceFilePath) && File.Exists(SourceFilePath))
        {
            return new FileStream(SourceFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        }

        return Stream.Null;
    }

    /// <summary>
    /// Computes the number of 2048-byte optical data sectors required to store this file.
    /// </summary>
    public int RequiredSectors => SizeBytes <= 0
        ? 0
        : (int)((SizeBytes + OpticalConstants.DataSectorSize - 1) / OpticalConstants.DataSectorSize);

    private static string ExtractFileName(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "file.dat";
        }
        string fn = Path.GetFileName(path);
        return string.IsNullOrEmpty(fn) ? path : fn;
    }

    public override string ToString() => $"[File] {VirtualPath} ({SizeBytes} bytes)";
}

/// <summary>
/// Represents a directory node in the virtual compilation filesystem tree containing child nodes.
/// </summary>
public class CompilationDirectory : CompilationNode
{
    private readonly List<CompilationNode> _children = new();

    /// <summary>
    /// Read-only snapshot of all child files and subdirectories.
    /// </summary>
    public IReadOnlyList<CompilationNode> Children => _children;

    public CompilationDirectory(
        string name = "",
        CompilationDirectory? parent = null,
        DateTime? lastWriteTimeUtc = null,
        FileAttributes attributes = FileAttributes.Directory)
        : base(name, parent, lastWriteTimeUtc, attributes | FileAttributes.Directory)
    {
    }

    /// <summary>
    /// Creates a root directory instance.
    /// </summary>
    public static CompilationDirectory CreateRoot(string name = "") => new(name, parent: null);

    /// <summary>
    /// Adds a file node to this directory using the specified collision policy.
    /// </summary>
    /// <param name="file">The file node to add.</param>
    /// <param name="policy">Duplicate file collision policy.</param>
    /// <returns>The added file node, or <c>null</c> if skipped due to <see cref="DuplicateFilePolicy.Skip"/>.</returns>
    public CompilationFile? AddFile(CompilationFile file, DuplicateFilePolicy policy = DuplicateFilePolicy.Overwrite)
    {
        ArgumentNullException.ThrowIfNull(file);

        var existing = _children.FirstOrDefault(c => string.Equals(c.Name, file.Name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            switch (policy)
            {
                case DuplicateFilePolicy.Overwrite:
                    _children.Remove(existing);
                    existing.Parent = null;
                    break;

                case DuplicateFilePolicy.RenameWithNumberSuffix:
                    file.Name = GenerateUniqueFileName(file.Name);
                    break;

                case DuplicateFilePolicy.Skip:
                    return null;

                case DuplicateFilePolicy.ThrowError:
                    throw new InvalidOperationException($"A file or directory named '{file.Name}' already exists in '{VirtualPath}'.");

                default:
                    throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown duplicate file policy.");
            }
        }

        file.Parent = this;
        _children.Add(file);
        return file;
    }

    /// <summary>
    /// Convenience helper to construct and add a file node with source file path.
    /// </summary>
    public CompilationFile? AddFile(
        string name,
        string sourceFilePath,
        long sizeBytes,
        DuplicateFilePolicy policy = DuplicateFilePolicy.Overwrite,
        int? importedStartLba = null,
        Func<Stream>? streamFactory = null)
    {
        var file = new CompilationFile(name, sourceFilePath, sizeBytes, importedStartLba, streamFactory);
        return AddFile(file, policy);
    }

    /// <summary>
    /// Convenience helper to construct and add an in-memory stream file.
    /// </summary>
    public CompilationFile? AddFile(
        string name,
        Func<Stream> streamFactory,
        long sizeBytes,
        DuplicateFilePolicy policy = DuplicateFilePolicy.Overwrite)
    {
        var file = new CompilationFile(name, sizeBytes, streamFactory);
        return AddFile(file, policy);
    }

    /// <summary>
    /// Convenience helper to construct and add an in-memory byte array file.
    /// </summary>
    public CompilationFile? AddFile(
        string name,
        byte[] content,
        DuplicateFilePolicy policy = DuplicateFilePolicy.Overwrite)
    {
        var file = new CompilationFile(
            name,
            content.Length,
            () => new MemoryStream(content, writable: false));
        return AddFile(file, policy);
    }

    /// <summary>
    /// Adds or traverses a directory path relative to this directory.
    /// Handles multiple path segments separated by '/' or '\\'.
    /// </summary>
    /// <param name="path">Subdirectory name or relative path.</param>
    /// <returns>The deepest directory created or retrieved.</returns>
    public CompilationDirectory AddDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var segments = path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        CompilationDirectory current = this;

        foreach (var segment in segments)
        {
            var existingDir = current._children.OfType<CompilationDirectory>()
                .FirstOrDefault(d => string.Equals(d.Name, segment, StringComparison.OrdinalIgnoreCase));

            if (existingDir != null)
            {
                current = existingDir;
            }
            else
            {
                var newDir = new CompilationDirectory(segment, current);
                current._children.Add(newDir);
                current = newDir;
            }
        }

        return current;
    }

    /// <summary>
    /// Adds an existing directory node as a child.
    /// </summary>
    public CompilationDirectory AddDirectory(CompilationDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        var existing = _children.FirstOrDefault(c => string.Equals(c.Name, directory.Name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            if (existing is CompilationDirectory existingDir)
            {
                return existingDir;
            }
            _children.Remove(existing);
            existing.Parent = null;
        }

        directory.Parent = this;
        _children.Add(directory);
        return directory;
    }

    /// <summary>
    /// Removes a child node from this directory.
    /// </summary>
    public bool RemoveChild(CompilationNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (_children.Remove(node))
        {
            node.Parent = null;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Removes a child node by name from this directory.
    /// </summary>
    public bool RemoveChild(string name)
    {
        var node = _children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        return node != null && RemoveChild(node);
    }

    /// <summary>
    /// Renames a child node within this directory.
    /// </summary>
    public void RenameChild(CompilationNode node, string newName)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);

        if (newName.Contains('/') || newName.Contains('\\'))
        {
            throw new ArgumentException("New name cannot contain path separators.", nameof(newName));
        }

        if (!_children.Contains(node))
        {
            throw new InvalidOperationException($"The node '{node.Name}' is not a child of '{VirtualPath}'.");
        }

        if (string.Equals(node.Name, newName, StringComparison.OrdinalIgnoreCase))
        {
            node.Name = newName;
            return;
        }

        if (_children.Any(c => !ReferenceEquals(c, node) && string.Equals(c.Name, newName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"A child named '{newName}' already exists in '{VirtualPath}'.");
        }

        node.Name = newName;
    }

    /// <summary>
    /// Renames a child node by its current name.
    /// </summary>
    public bool RenameChild(string currentName, string newName)
    {
        var node = _children.FirstOrDefault(c => string.Equals(c.Name, currentName, StringComparison.OrdinalIgnoreCase));
        if (node == null)
        {
            return false;
        }
        RenameChild(node, newName);
        return true;
    }

    /// <summary>
    /// Finds a node by its virtual path relative to this directory or absolute from root.
    /// </summary>
    public CompilationNode? FindByVirtualPath(string virtualPath)
    {
        if (string.IsNullOrWhiteSpace(virtualPath) || virtualPath == "/")
        {
            return this;
        }

        if ((virtualPath.StartsWith('/') || virtualPath.StartsWith('\\')) && Parent != null)
        {
            return GetRoot().FindByVirtualPath(virtualPath);
        }

        string[] parts = virtualPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        CompilationNode current = this;

        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            if (current is not CompilationDirectory currentDir)
            {
                return null;
            }

            var matchingChild = currentDir.Children.FirstOrDefault(
                c => string.Equals(c.Name, part, StringComparison.OrdinalIgnoreCase));

            if (matchingChild == null)
            {
                return null;
            }

            current = matchingChild;
        }

        return current;
    }

    /// <summary>
    /// Finds a file node by its virtual path.
    /// </summary>
    public CompilationFile? FindFileByVirtualPath(string virtualPath) => FindByVirtualPath(virtualPath) as CompilationFile;

    /// <summary>
    /// Finds a directory node by its virtual path.
    /// </summary>
    public CompilationDirectory? FindDirectoryByVirtualPath(string virtualPath) => FindByVirtualPath(virtualPath) as CompilationDirectory;

    /// <summary>
    /// Recursively retrieves all compilation files under this directory and all subdirectories.
    /// </summary>
    public IReadOnlyList<CompilationFile> GetAllFilesRecursive()
    {
        var result = new List<CompilationFile>();
        CollectFiles(this, result);
        return result.AsReadOnly();
    }

    /// <summary>
    /// Recursively retrieves all directories under this directory (including this directory).
    /// </summary>
    public IReadOnlyList<CompilationDirectory> GetAllDirectoriesRecursive()
    {
        var result = new List<CompilationDirectory> { this };
        CollectDirectories(this, result);
        return result.AsReadOnly();
    }

    /// <summary>
    /// Navigates upward to find the root directory of this tree.
    /// </summary>
    public CompilationDirectory GetRoot()
    {
        CompilationDirectory current = this;
        while (current.Parent != null)
        {
            current = current.Parent;
        }
        return current;
    }

    /// <summary>
    /// Clears all children from this directory.
    /// </summary>
    public void Clear()
    {
        foreach (var child in _children)
        {
            child.Parent = null;
        }
        _children.Clear();
    }

    private string GenerateUniqueFileName(string originalName)
    {
        string cleanName = Path.GetFileName(originalName);
        if (string.IsNullOrEmpty(cleanName))
        {
            cleanName = originalName;
        }

        int lastDot = cleanName.LastIndexOf('.');
        string baseName = lastDot > 0 ? cleanName[..lastDot] : cleanName;
        string extension = lastDot > 0 ? cleanName[lastDot..] : string.Empty;

        string basePattern = baseName;
        int suffixIndex = 2;

        var match = Regex.Match(baseName, @"^(.*) \((\d+)\)$");
        if (match.Success)
        {
            basePattern = match.Groups[1].Value;
            if (int.TryParse(match.Groups[2].Value, out int currentNum))
            {
                suffixIndex = currentNum + 1;
            }
        }

        while (true)
        {
            string candidate = $"{basePattern} ({suffixIndex}){extension}";
            if (!_children.Any(c => string.Equals(c.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
            suffixIndex++;
        }
    }

    private static void CollectFiles(CompilationDirectory dir, List<CompilationFile> result)
    {
        foreach (var child in dir.Children)
        {
            if (child is CompilationFile file)
            {
                result.Add(file);
            }
            else if (child is CompilationDirectory subDir)
            {
                CollectFiles(subDir, result);
            }
        }
    }

    private static void CollectDirectories(CompilationDirectory dir, List<CompilationDirectory> result)
    {
        foreach (var child in dir.Children)
        {
            if (child is CompilationDirectory subDir)
            {
                result.Add(subDir);
                CollectDirectories(subDir, result);
            }
        }
    }

    public override string ToString() => $"[Dir] {VirtualPath} ({Children.Count} children)";
}
