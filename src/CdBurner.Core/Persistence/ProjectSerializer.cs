using System.Text.Json;
using System.Text.Json.Serialization;
using CdBurner.Core.Audio;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;

namespace CdBurner.Core.Persistence;

/// <summary>
/// Custom JSON converter for <see cref="FileAttributes"/> supporting both single and multi-flag enum values.
/// </summary>
public sealed class FileAttributesJsonConverter : JsonConverter<FileAttributes>
{
    public override FileAttributes Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            return (FileAttributes)reader.GetInt32();
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            string? str = reader.GetString();
            if (string.IsNullOrWhiteSpace(str))
            {
                return FileAttributes.Normal;
            }

            if (Enum.TryParse<FileAttributes>(str, ignoreCase: true, out var result))
            {
                return result;
            }

            FileAttributes combined = 0;
            var parts = str.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var part in parts)
            {
                if (Enum.TryParse<FileAttributes>(part, ignoreCase: true, out var attr))
                {
                    combined |= attr;
                }
            }
            return combined;
        }

        return FileAttributes.Normal;
    }

    public override void Write(Utf8JsonWriter writer, FileAttributes value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}

/// <summary>
/// Serializes, deserializes, and validates optical disc authoring project documents (.cdbproj).
/// Supports Data, Bootable, Audio CD, and DVD-Video projects.
/// </summary>
public static class ProjectSerializer
{
    /// <summary>
    /// Standard JSON serialization settings for .cdbproj project documents and configuration files.
    /// Indented, camelCase naming, case-insensitive reading, string enums.
    /// </summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
            new FileAttributesJsonConverter()
        }
    };

    #region File I/O Operations

    /// <summary>
    /// Asynchronously saves a project document to disk in JSON format (.cdbproj).
    /// </summary>
    public static async Task SaveToFileAsync(
        CdBurnerProjectDocument project,
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        project.ModifiedAtUtc = DateTime.UtcNow;

        await using var stream = new FileStream(
            filePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            useAsync: true);

        await JsonSerializer.SerializeAsync(stream, project, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Synchronously saves a project document to disk in JSON format (.cdbproj).
    /// </summary>
    public static void SaveToFile(CdBurnerProjectDocument project, string filePath)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        project.ModifiedAtUtc = DateTime.UtcNow;

        string json = JsonSerializer.Serialize(project, JsonOptions);
        File.WriteAllText(filePath, json);
    }

    /// <summary>
    /// Asynchronously loads a project document from a .cdbproj file on disk.
    /// </summary>
    public static async Task<CdBurnerProjectDocument> LoadFromFileAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Project file not found at '{filePath}'.", filePath);
        }

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);

        var doc = await JsonSerializer.DeserializeAsync<CdBurnerProjectDocument>(
            stream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);

        return doc ?? throw new InvalidDataException($"Failed to deserialize project document from '{filePath}'.");
    }

    /// <summary>
    /// Synchronously loads a project document from a .cdbproj file on disk.
    /// </summary>
    public static CdBurnerProjectDocument LoadFromFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Project file not found at '{filePath}'.", filePath);
        }

        string json = File.ReadAllText(filePath);
        var doc = JsonSerializer.Deserialize<CdBurnerProjectDocument>(json, JsonOptions);

        return doc ?? throw new InvalidDataException($"Failed to deserialize project document from '{filePath}'.");
    }

    /// <summary>
    /// Serializes a project document directly to a JSON string.
    /// </summary>
    public static string ToJson(CdBurnerProjectDocument project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return JsonSerializer.Serialize(project, JsonOptions);
    }

    /// <summary>
    /// Deserializes a project document from a JSON string.
    /// </summary>
    public static CdBurnerProjectDocument FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var doc = JsonSerializer.Deserialize<CdBurnerProjectDocument>(json, JsonOptions);
        return doc ?? throw new InvalidDataException("Failed to deserialize project document from JSON string.");
    }

    #endregion

    #region Source File Validation

    /// <summary>
    /// Inspects all external source file references in the project document and returns
    /// a list of file paths that do not exist on disk.
    /// </summary>
    /// <param name="project">The project document to validate.</param>
    /// <returns>A list of unique missing source file paths on disk.</returns>
    public static IReadOnlyList<string> ValidateProjectSources(CdBurnerProjectDocument project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var missingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Validate virtual filesystem nodes (DataDisc, BootableDisc, DvdVideo)
        if (project.RootNode != null)
        {
            CollectMissingNodeSources(project.RootNode, missingPaths);
        }

        // 2. Validate boot catalog images (BootableDisc)
        if (project.BootOptions != null)
        {
            if (project.BootOptions.DefaultEntry != null &&
                !string.IsNullOrWhiteSpace(project.BootOptions.DefaultEntry.BootImageFilePath))
            {
                if (!File.Exists(project.BootOptions.DefaultEntry.BootImageFilePath))
                {
                    missingPaths.Add(project.BootOptions.DefaultEntry.BootImageFilePath);
                }
            }

            if (project.BootOptions.AdditionalSectionEntries != null)
            {
                foreach (var section in project.BootOptions.AdditionalSectionEntries)
                {
                    if (!string.IsNullOrWhiteSpace(section.BootImageFilePath) &&
                        !File.Exists(section.BootImageFilePath))
                    {
                        missingPaths.Add(section.BootImageFilePath);
                    }
                }
            }
        }

        // 3. Validate audio track sources (AudioCd)
        if (project.AudioTracks != null)
        {
            foreach (var track in project.AudioTracks)
            {
                if (!string.IsNullOrWhiteSpace(track.SourceFilePath) && !File.Exists(track.SourceFilePath))
                {
                    missingPaths.Add(track.SourceFilePath);
                }
            }
        }

        return missingPaths.ToList().AsReadOnly();
    }

    private static void CollectMissingNodeSources(SerializedCompilationNode node, HashSet<string> missingPaths)
    {
        if (!node.IsDirectory && !string.IsNullOrWhiteSpace(node.SourceFilePath))
        {
            if (!File.Exists(node.SourceFilePath))
            {
                missingPaths.Add(node.SourceFilePath);
            }
        }

        if (node.Children != null)
        {
            foreach (var child in node.Children)
            {
                CollectMissingNodeSources(child, missingPaths);
            }
        }
    }

    #endregion

    #region Data Compilation Tree Conversion

    /// <summary>
    /// Converts a live in-memory <see cref="CompilationNode"/> (file or directory)
    /// into a serializable <see cref="SerializedCompilationNode"/> DTO hierarchy.
    /// </summary>
    public static SerializedCompilationNode ToSerializedNode(CompilationNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (node is CompilationDirectory dir)
        {
            var serializedDir = new SerializedCompilationNode
            {
                IsDirectory = true,
                Name = dir.Name,
                Attributes = dir.Attributes,
                LastWriteTimeUtc = dir.LastWriteTimeUtc,
                Children = new List<SerializedCompilationNode>(dir.Children.Count)
            };

            foreach (var child in dir.Children)
            {
                serializedDir.Children.Add(ToSerializedNode(child));
            }

            return serializedDir;
        }

        if (node is CompilationFile file)
        {
            return new SerializedCompilationNode
            {
                IsDirectory = false,
                Name = file.Name,
                SourceFilePath = file.SourceFilePath,
                SizeBytes = file.SizeBytes,
                Attributes = file.Attributes,
                LastWriteTimeUtc = file.LastWriteTimeUtc,
                ImportedStartLba = file.ImportedStartLba
            };
        }

        throw new NotSupportedException($"Unsupported compilation node type: '{node.GetType().FullName}'.");
    }

    /// <summary>
    /// Converts a <see cref="SerializedCompilationNode"/> DTO hierarchy into live <see cref="CompilationNode"/> instances.
    /// </summary>
    public static CompilationNode ToCompilationNode(SerializedCompilationNode serialized)
    {
        ArgumentNullException.ThrowIfNull(serialized);

        if (serialized.IsDirectory)
        {
            var dir = new CompilationDirectory(
                name: serialized.Name,
                parent: null,
                lastWriteTimeUtc: serialized.LastWriteTimeUtc,
                attributes: serialized.Attributes);

            if (serialized.Children != null)
            {
                foreach (var child in serialized.Children)
                {
                    var childNode = ToCompilationNode(child);
                    if (childNode is CompilationDirectory childDir)
                    {
                        dir.AddDirectory(childDir);
                    }
                    else if (childNode is CompilationFile childFile)
                    {
                        dir.AddFile(childFile);
                    }
                }
            }

            return dir;
        }
        else
        {
            return new CompilationFile(
                name: serialized.Name,
                sourceFilePath: serialized.SourceFilePath ?? string.Empty,
                sizeBytes: serialized.SizeBytes,
                importedStartLba: serialized.ImportedStartLba,
                streamFactory: null,
                parent: null,
                lastWriteTimeUtc: serialized.LastWriteTimeUtc,
                attributes: serialized.Attributes);
        }
    }

    /// <summary>
    /// Converts a <see cref="SerializedCompilationNode"/> representing a directory into a live <see cref="CompilationDirectory"/>.
    /// </summary>
    public static CompilationDirectory ToCompilationDirectory(SerializedCompilationNode serialized)
    {
        ArgumentNullException.ThrowIfNull(serialized);

        var node = ToCompilationNode(serialized);
        if (node is CompilationDirectory dir)
        {
            return dir;
        }

        throw new InvalidOperationException($"Node '{serialized.Name}' is a file, not a directory.");
    }

    /// <summary>
    /// Creates a persistent project document from a live <see cref="DataCompilation"/>.
    /// </summary>
    public static CdBurnerProjectDocument FromDataCompilation(
        DataCompilation compilation,
        string projectName = "Data Project")
    {
        ArgumentNullException.ThrowIfNull(compilation);

        return new CdBurnerProjectDocument
        {
            Kind = ProjectKind.DataDisc,
            Name = projectName,
            VolumeLabel = compilation.VolumeLabel,
            TargetMediaType = compilation.TargetMediaType,
            DefaultDuplicatePolicy = compilation.DefaultDuplicatePolicy,
            RootNode = ToSerializedNode(compilation.Root),
            CreatedAtUtc = DateTime.UtcNow,
            ModifiedAtUtc = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Creates a live <see cref="DataCompilation"/> from a persistent project document.
    /// </summary>
    public static DataCompilation ToDataCompilation(CdBurnerProjectDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var compilation = new DataCompilation(
            volumeLabel: document.VolumeLabel,
            targetMediaType: document.TargetMediaType,
            defaultDuplicatePolicy: document.DefaultDuplicatePolicy);

        if (document.RootNode != null)
        {
            var rootDir = ToCompilationDirectory(document.RootNode);
            foreach (var child in rootDir.Children.ToList())
            {
                if (child is CompilationDirectory subDir)
                {
                    compilation.Root.AddDirectory(subDir);
                }
                else if (child is CompilationFile file)
                {
                    compilation.Root.AddFile(file, compilation.DefaultDuplicatePolicy);
                }
            }
        }

        return compilation;
    }

    /// <summary>
    /// Creates a persistent project document from a root <see cref="CompilationDirectory"/>.
    /// </summary>
    public static CdBurnerProjectDocument FromCompilationDirectory(
        CompilationDirectory root,
        ProjectKind kind = ProjectKind.DataDisc,
        string projectName = "Project",
        string volumeLabel = "CDBURNER_DISC")
    {
        ArgumentNullException.ThrowIfNull(root);

        return new CdBurnerProjectDocument
        {
            Kind = kind,
            Name = projectName,
            VolumeLabel = volumeLabel,
            RootNode = ToSerializedNode(root),
            CreatedAtUtc = DateTime.UtcNow,
            ModifiedAtUtc = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Extracts a live root <see cref="CompilationDirectory"/> from a project document.
    /// </summary>
    public static CompilationDirectory ToCompilationDirectory(CdBurnerProjectDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document.RootNode == null)
        {
            return CompilationDirectory.CreateRoot();
        }

        return ToCompilationDirectory(document.RootNode);
    }

    #endregion

    #region Audio CD Compilation Conversion

    /// <summary>
    /// Creates a persistent project document from a live <see cref="AudioCdCompilation"/>.
    /// </summary>
    public static CdBurnerProjectDocument FromAudioCdCompilation(
        AudioCdCompilation compilation,
        string projectName = "Audio CD Project")
    {
        ArgumentNullException.ThrowIfNull(compilation);

        var doc = new CdBurnerProjectDocument
        {
            Kind = ProjectKind.AudioCd,
            Name = projectName,
            RecordingMode = compilation.RecordingMode,
            IncludeCdText = compilation.IncludeCdText,
            NormalizeAllTracks = compilation.NormalizeAllTracks,
            DefaultPreGapFrames = compilation.DefaultPreGapFrames,
            CreatedAtUtc = DateTime.UtcNow,
            ModifiedAtUtc = DateTime.UtcNow,
            CdText = new SerializedCdText
            {
                AlbumTitle = compilation.AlbumTitle,
                AlbumPerformer = compilation.AlbumPerformer,
                Songwriter = compilation.Songwriter,
                Composer = compilation.Composer,
                Arranger = compilation.Arranger,
                Message = compilation.Message,
                DiscId = compilation.DiscId,
                UpcEan = compilation.UpcEan
            }
        };

        foreach (var track in compilation.Tracks)
        {
            doc.AudioTracks.Add(new SerializedAudioTrack
            {
                TrackNumber = track.TrackNumber,
                SourceFilePath = track.SourceFilePath,
                Title = track.Title,
                Performer = track.Performer,
                Isrc = track.Isrc,
                PreGapFrames = track.PreGapFrames,
                NormalizeAudio = track.NormalizeAudio,
                DurationFrames = track.DurationFrames,
                Songwriter = track.Songwriter,
                Composer = track.Composer,
                Arranger = track.Arranger,
                Message = track.Message
            });
        }

        return doc;
    }

    /// <summary>
    /// Reconstitutes a live <see cref="AudioCdCompilation"/> from a persistent project document.
    /// </summary>
    public static AudioCdCompilation ToAudioCdCompilation(CdBurnerProjectDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var compilation = new AudioCdCompilation
        {
            RecordingMode = document.RecordingMode,
            IncludeCdText = document.IncludeCdText,
            NormalizeAllTracks = document.NormalizeAllTracks,
            DefaultPreGapFrames = document.DefaultPreGapFrames
        };

        if (document.CdText != null)
        {
            if (!string.IsNullOrEmpty(document.CdText.AlbumTitle)) compilation.AlbumTitle = document.CdText.AlbumTitle;
            if (!string.IsNullOrEmpty(document.CdText.AlbumPerformer)) compilation.AlbumPerformer = document.CdText.AlbumPerformer;
            compilation.Songwriter = document.CdText.Songwriter;
            compilation.Composer = document.CdText.Composer;
            compilation.Arranger = document.CdText.Arranger;
            compilation.Message = document.CdText.Message;
            compilation.DiscId = document.CdText.DiscId;
            compilation.UpcEan = document.CdText.UpcEan;
        }

        foreach (var track in document.AudioTracks.OrderBy(t => t.TrackNumber))
        {
            var trackItem = new AudioCdTrackItem(
                track.TrackNumber,
                track.SourceFilePath,
                track.Title,
                track.Performer,
                track.Isrc,
                track.PreGapFrames,
                track.NormalizeAudio)
            {
                DurationFrames = track.DurationFrames,
                Songwriter = track.Songwriter,
                Composer = track.Composer,
                Arranger = track.Arranger,
                Message = track.Message
            };

            compilation.AddTrack(trackItem);
        }

        return compilation;
    }

    #endregion

    #region El Torito Boot Options Conversion

    /// <summary>
    /// Reconstitutes live <see cref="ElToritoBootConfiguration"/> from serialized options.
    /// </summary>
    public static ElToritoBootConfiguration? ToBootConfiguration(SerializedBootOptions? options)
    {
        if (options == null || options.DefaultEntry == null)
        {
            return null;
        }

        Func<Stream> defaultStreamFactory = () =>
        {
            if (!string.IsNullOrEmpty(options.DefaultEntry.BootImageFilePath) &&
                File.Exists(options.DefaultEntry.BootImageFilePath))
            {
                return new FileStream(options.DefaultEntry.BootImageFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }
            return Stream.Null;
        };

        var defaultEntrySpec = new BootCatalogEntrySpec(
            Platform: options.DefaultEntry.Platform,
            Emulation: options.DefaultEntry.Emulation,
            LoadSegment: options.DefaultEntry.LoadSegment,
            SectorCount: options.DefaultEntry.SectorCount,
            BootImageStreamFactory: defaultStreamFactory,
            Name: options.DefaultEntry.Name)
        {
            SystemType = options.DefaultEntry.SystemType
        };

        List<BootCatalogEntrySpec>? additionalEntries = null;
        if (options.AdditionalSectionEntries != null && options.AdditionalSectionEntries.Count > 0)
        {
            additionalEntries = new List<BootCatalogEntrySpec>(options.AdditionalSectionEntries.Count);
            foreach (var entry in options.AdditionalSectionEntries)
            {
                string path = entry.BootImageFilePath;
                Func<Stream> streamFactory = () =>
                {
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    {
                        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    }
                    return Stream.Null;
                };

                var spec = new BootCatalogEntrySpec(
                    Platform: entry.Platform,
                    Emulation: entry.Emulation,
                    LoadSegment: entry.LoadSegment,
                    SectorCount: entry.SectorCount,
                    BootImageStreamFactory: streamFactory,
                    Name: entry.Name)
                {
                    SystemType = entry.SystemType
                };
                additionalEntries.Add(spec);
            }
        }

        return new ElToritoBootConfiguration(
            DefaultEntry: defaultEntrySpec,
            AdditionalSectionEntries: additionalEntries,
            DeveloperId: options.DeveloperId ?? "CDBURNER");
    }

    /// <summary>
    /// Serializes an <see cref="ElToritoBootConfiguration"/> into <see cref="SerializedBootOptions"/>.
    /// </summary>
    public static SerializedBootOptions FromBootConfiguration(
        ElToritoBootConfiguration config,
        string defaultBootFilePath = "",
        IDictionary<string, string>? additionalBootPaths = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        var options = new SerializedBootOptions
        {
            DeveloperId = config.DeveloperId,
            DefaultEntry = new SerializedBootCatalogEntry
            {
                Platform = config.DefaultEntry.Platform,
                Emulation = config.DefaultEntry.Emulation,
                LoadSegment = config.DefaultEntry.LoadSegment,
                SectorCount = config.DefaultEntry.SectorCount,
                BootImageFilePath = defaultBootFilePath,
                Name = config.DefaultEntry.Name,
                SystemType = config.DefaultEntry.SystemType
            }
        };

        if (config.AdditionalSectionEntries != null)
        {
            foreach (var entry in config.AdditionalSectionEntries)
            {
                string path = (additionalBootPaths != null && additionalBootPaths.TryGetValue(entry.Name, out var p))
                    ? p
                    : string.Empty;

                options.AdditionalSectionEntries.Add(new SerializedBootCatalogEntry
                {
                    Platform = entry.Platform,
                    Emulation = entry.Emulation,
                    LoadSegment = entry.LoadSegment,
                    SectorCount = entry.SectorCount,
                    BootImageFilePath = path,
                    Name = entry.Name,
                    SystemType = entry.SystemType
                });
            }
        }

        return options;
    }

    #endregion
}
