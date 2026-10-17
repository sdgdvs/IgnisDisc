using System.Text.Json.Serialization;
using CdBurner.Core.Audio;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;

namespace CdBurner.Core.Persistence;

/// <summary>
/// Supported optical disc compilation project kinds.
/// </summary>
public enum ProjectKind
{
    /// <summary>
    /// ISO 9660 / Joliet / UDF Data disc compilation.
    /// </summary>
    DataDisc,

    /// <summary>
    /// El Torito bootable disc compilation (BIOS / UEFI).
    /// </summary>
    BootableDisc,

    /// <summary>
    /// Red Book Audio CD compilation (up to 99 tracks with CD-Text).
    /// </summary>
    AudioCd,

    /// <summary>
    /// DVD-Video authoring compilation with VIDEO_TS / AUDIO_TS structure.
    /// </summary>
    DvdVideo
}

/// <summary>
/// Master project document DTO for persistent .cdbproj project files.
/// </summary>
public class CdBurnerProjectDocument
{
    public const string DefaultSchemaVersion = "1.0";

    /// <summary>
    /// Document schema version (e.g. "1.0").
    /// </summary>
    public string SchemaVersion { get; set; } = DefaultSchemaVersion;

    /// <summary>
    /// Project compilation kind.
    /// </summary>
    public ProjectKind Kind { get; set; } = ProjectKind.DataDisc;

    /// <summary>
    /// User-friendly name of the project.
    /// </summary>
    public string Name { get; set; } = "Untitled Project";

    /// <summary>
    /// Optional project description or user notes.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// UTC timestamp when this project document was initially created.
    /// </summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// UTC timestamp when this project document was last modified.
    /// </summary>
    public DateTime ModifiedAtUtc { get; set; } = DateTime.UtcNow;

    // --- Volume & Media Settings (Data, Bootable, DVD-Video) ---

    /// <summary>
    /// Volume identifier label for optical filesystem headers.
    /// </summary>
    public string VolumeLabel { get; set; } = "CDBURNER_DISC";

    /// <summary>
    /// Target optical media type (CD-R, DVD-R, etc.).
    /// </summary>
    public DiscMediaType TargetMediaType { get; set; } = DiscMediaType.CdR;

    /// <summary>
    /// Default collision resolution policy for duplicate filenames.
    /// </summary>
    public DuplicateFilePolicy DefaultDuplicatePolicy { get; set; } = DuplicateFilePolicy.Overwrite;

    // --- Virtual Filesystem Tree ---

    /// <summary>
    /// Root directory node representing the compilation virtual filesystem tree
    /// for DataDisc, BootableDisc, and DvdVideo projects.
    /// </summary>
    public SerializedCompilationNode? RootNode { get; set; }

    // --- Boot Options (BootableDisc) ---

    /// <summary>
    /// El Torito bootable disc configuration options.
    /// </summary>
    public SerializedBootOptions? BootOptions { get; set; }

    // --- Audio CD Settings & Tracks (AudioCd) ---

    /// <summary>
    /// Audio CD recording mode: Track-at-Once (TAO) or Disc-at-Once (DAO).
    /// </summary>
    public AudioRecordingMode RecordingMode { get; set; } = AudioRecordingMode.DiscAtOnceGapless;

    /// <summary>
    /// Whether to generate and burn Lead-In CD-Text binary packs.
    /// </summary>
    public bool IncludeCdText { get; set; } = true;

    /// <summary>
    /// Whether to normalize audio volume across all tracks.
    /// </summary>
    public bool NormalizeAllTracks { get; set; } = false;

    /// <summary>
    /// Default pre-gap in Red Book frames (75 fps) for audio tracks (150 frames = 2.0s).
    /// </summary>
    public int DefaultPreGapFrames { get; set; } = 150;

    /// <summary>
    /// Ordered list of audio tracks in this compilation.
    /// </summary>
    public List<SerializedAudioTrack> AudioTracks { get; set; } = new();

    /// <summary>
    /// Structured CD-Text metadata for album and tracks.
    /// </summary>
    public SerializedCdText? CdText { get; set; }

    // --- DVD-Video Specific Settings ---

    /// <summary>
    /// Optional DVD-Video title set label.
    /// </summary>
    public string? DvdVideoTitle { get; set; }
}

/// <summary>
/// Serializable representation of a directory or file node within a virtual compilation filesystem.
/// </summary>
public class SerializedCompilationNode
{
    /// <summary>
    /// Indicates whether this node is a directory (<c>true</c>) or a file (<c>false</c>).
    /// </summary>
    public bool IsDirectory { get; set; }

    /// <summary>
    /// Name of the file or directory node.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Local physical file path on disk for file nodes.
    /// </summary>
    public string? SourceFilePath { get; set; }

    /// <summary>
    /// Size of the file in bytes.
    /// </summary>
    public long SizeBytes { get; set; }

    /// <summary>
    /// Standard file system attributes for this node.
    /// </summary>
    public FileAttributes Attributes { get; set; } = FileAttributes.Normal;

    /// <summary>
    /// Last write timestamp in UTC.
    /// </summary>
    public DateTime LastWriteTimeUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Optional starting LBA for imported multi-session files.
    /// </summary>
    public int? ImportedStartLba { get; set; }

    /// <summary>
    /// Child nodes contained in this directory.
    /// </summary>
    public List<SerializedCompilationNode> Children { get; set; } = new();
}

/// <summary>
/// Specification describing a boot image entry for serialization.
/// </summary>
public class SerializedBootCatalogEntry
{
    /// <summary>
    /// Hardware platform identifier (BIOS, EFI, etc.).
    /// </summary>
    public BootPlatformId Platform { get; set; } = BootPlatformId.X86Bios;

    /// <summary>
    /// Boot media emulation type (NoEmulation, Floppy, HardDisk).
    /// </summary>
    public BootEmulationType Emulation { get; set; } = BootEmulationType.NoEmulation;

    /// <summary>
    /// BIOS RAM load segment (0x0000 defaults to 0x07C0).
    /// </summary>
    public ushort LoadSegment { get; set; } = 0x0000;

    /// <summary>
    /// Number of virtual 512-byte sectors to load.
    /// </summary>
    public ushort SectorCount { get; set; } = 4;

    /// <summary>
    /// Local file path on disk to the boot image file.
    /// </summary>
    public string BootImageFilePath { get; set; } = string.Empty;

    /// <summary>
    /// Descriptive name or filename for the boot image.
    /// </summary>
    public string Name { get; set; } = "boot.img";

    /// <summary>
    /// System type byte (matches partition table type).
    /// </summary>
    public byte SystemType { get; set; } = 0x00;
}

/// <summary>
/// El Torito boot options for creating bootable optical disc compilations.
/// </summary>
public class SerializedBootOptions
{
    /// <summary>
    /// Developer or vendor identifier string recorded in the Validation Entry (up to 24 characters).
    /// </summary>
    public string DeveloperId { get; set; } = "CDBURNER";

    /// <summary>
    /// Initial / default boot entry (typically x86 BIOS).
    /// </summary>
    public SerializedBootCatalogEntry? DefaultEntry { get; set; }

    /// <summary>
    /// Optional additional section entries (e.g. UEFI multi-boot).
    /// </summary>
    public List<SerializedBootCatalogEntry> AdditionalSectionEntries { get; set; } = new();

    public SerializedBootOptions()
    {
    }

    public SerializedBootOptions(
        string bootImageFilePath,
        BootPlatformId platform = BootPlatformId.X86Bios,
        BootEmulationType emulation = BootEmulationType.NoEmulation,
        ushort sectorCount = 4,
        string developerId = "CDBURNER")
    {
        DeveloperId = developerId;
        DefaultEntry = new SerializedBootCatalogEntry
        {
            BootImageFilePath = bootImageFilePath,
            Platform = platform,
            Emulation = emulation,
            SectorCount = sectorCount
        };
    }
}

/// <summary>
/// Serializable audio track item in an Audio CD project.
/// </summary>
public class SerializedAudioTrack
{
    /// <summary>
    /// 1-based sequential track number (1..99).
    /// </summary>
    public int TrackNumber { get; set; }

    /// <summary>
    /// Physical source file path on disk (e.g. .wav, .flac, .mp3).
    /// </summary>
    public string SourceFilePath { get; set; } = string.Empty;

    /// <summary>
    /// Track song title.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Track performer or artist name.
    /// </summary>
    public string Performer { get; set; } = string.Empty;

    /// <summary>
    /// Songwriter name for CD-Text.
    /// </summary>
    public string? Songwriter { get; set; }

    /// <summary>
    /// Composer name for CD-Text.
    /// </summary>
    public string? Composer { get; set; }

    /// <summary>
    /// Arranger name for CD-Text.
    /// </summary>
    public string? Arranger { get; set; }

    /// <summary>
    /// Liner notes or message for CD-Text.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// 12-character International Standard Recording Code (ISRC).
    /// </summary>
    public string? Isrc { get; set; }

    /// <summary>
    /// Pre-gap in 75 fps Red Book frames (default 150 = 2.0s).
    /// </summary>
    public int PreGapFrames { get; set; } = 150;

    /// <summary>
    /// Whether to apply peak audio normalization during burning.
    /// </summary>
    public bool NormalizeAudio { get; set; }

    /// <summary>
    /// Total duration of this track in Red Book frames (75 fps).
    /// </summary>
    public int DurationFrames { get; set; }
}

/// <summary>
/// Structured album-level and track-level CD-Text metadata.
/// </summary>
public class SerializedCdText
{
    public string AlbumTitle { get; set; } = string.Empty;
    public string AlbumPerformer { get; set; } = string.Empty;
    public string? Songwriter { get; set; }
    public string? Composer { get; set; }
    public string? Arranger { get; set; }
    public string? Message { get; set; }
    public string? DiscId { get; set; }
    public string? UpcEan { get; set; }
    public string? Genre { get; set; }
    public List<SerializedCdTextTrack>? Tracks { get; set; }
}

/// <summary>
/// Individual track CD-Text metadata.
/// </summary>
public class SerializedCdTextTrack
{
    public int TrackNumber { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Performer { get; set; } = string.Empty;
    public string? Songwriter { get; set; }
    public string? Composer { get; set; }
    public string? Arranger { get; set; }
    public string? Message { get; set; }
    public string? Isrc { get; set; }
}
