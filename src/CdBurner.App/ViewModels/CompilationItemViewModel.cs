using System.IO;
using CdBurner.App.Mvvm;
using CdBurner.Core.Compilation;

namespace CdBurner.App.ViewModels;

/// <summary>
/// View model representing an individual file or directory item within the active data compilation view.
/// </summary>
public class CompilationItemViewModel : ObservableObject
{
    private string _name;
    private string _virtualPath;
    private bool _isDirectory;
    private long _sizeBytes;
    private string? _sourcePath;

    /// <summary>
    /// File or directory name.
    /// </summary>
    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    /// <summary>
    /// Full virtual path inside compilation hierarchy (e.g. "/Docs/ReadMe.txt").
    /// </summary>
    public string VirtualPath
    {
        get => _virtualPath;
        set => SetProperty(ref _virtualPath, value);
    }

    /// <summary>
    /// Indicates whether this item is a directory.
    /// </summary>
    public bool IsDirectory
    {
        get => _isDirectory;
        set
        {
            if (SetProperty(ref _isDirectory, value))
            {
                OnPropertyChanged(nameof(DisplaySize));
                OnPropertyChanged(nameof(TypeDescription));
                OnPropertyChanged(nameof(IconGlyph));
            }
        }
    }

    /// <summary>
    /// File size in bytes (0 for directories).
    /// </summary>
    public long SizeBytes
    {
        get => _sizeBytes;
        set
        {
            if (SetProperty(ref _sizeBytes, value))
            {
                OnPropertyChanged(nameof(DisplaySize));
            }
        }
    }

    /// <summary>
    /// Physical path on local storage, or null if generated in-memory or a virtual directory.
    /// </summary>
    public string? SourcePath
    {
        get => _sourcePath;
        set => SetProperty(ref _sourcePath, value);
    }

    /// <summary>
    /// Underlying compilation tree node reference.
    /// </summary>
    public CompilationNode? Node { get; init; }

    /// <summary>
    /// Formatted display size string.
    /// </summary>
    public string DisplaySize => IsDirectory ? "<DIR>" : FormatBytes(SizeBytes);

    /// <summary>
    /// User-friendly item classification description.
    /// </summary>
    public string TypeDescription
    {
        get
        {
            if (IsDirectory) return "File folder";
            string ext = Path.GetExtension(Name);
            if (string.IsNullOrEmpty(ext)) return "File";
            return $"{ext.TrimStart('.').ToUpperInvariant()} File";
        }
    }

    /// <summary>
    /// Fluent icon glyph for item representation.
    /// </summary>
    public string IconGlyph => IsDirectory ? "📁" : "📄";

    public CompilationItemViewModel(
        string name,
        string virtualPath,
        bool isDirectory,
        long sizeBytes,
        string? sourcePath = null,
        CompilationNode? node = null)
    {
        _name = name;
        _virtualPath = virtualPath;
        _isDirectory = isDirectory;
        _sizeBytes = sizeBytes;
        _sourcePath = sourcePath;
        Node = node;
    }

    /// <summary>
    /// Formats raw byte counts into human-readable metric strings.
    /// </summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{(bytes / 1024.0):F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{(bytes / (1024.0 * 1024)):F1} MB";
        return $"{(bytes / (1024.0 * 1024 * 1024)):F2} GB";
    }
}
