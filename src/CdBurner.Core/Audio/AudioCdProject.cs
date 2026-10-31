using CdBurner.Core.Primitives;

namespace CdBurner.Core.Audio;

/// <summary>
/// Mode of recording for Red Book Audio CDs.
/// </summary>
public enum AudioRecordingMode
{
    /// <summary>
    /// Track-at-Once (TAO): Burns each track individually with standard 2-second (150-frame) run-out/link blocks.
    /// </summary>
    TrackAtOnce,

    /// <summary>
    /// Disc-at-Once (DAO): Writes all tracks gaplessly in a continuous laser pass, enabling seamless transitions and Lead-In CD-Text.
    /// </summary>
    DiscAtOnceGapless
}

/// <summary>
/// Represents a track item within an Audio CD compilation project.
/// </summary>
public record AudioCdTrackItem(
    int TrackNumber,
    string SourceFilePath,
    string Title,
    string Performer,
    string? Isrc = null,
    int PreGapFrames = 150,
    bool NormalizeAudio = false,
    Func<Stream>? CustomPcmStreamFactory = null)
{
    private int _durationFrames;

    /// <summary>
    /// Songwriter name for CD-Text.
    /// </summary>
    public string? Songwriter { get; init; }

    /// <summary>
    /// Composer name for CD-Text.
    /// </summary>
    public string? Composer { get; init; }

    /// <summary>
    /// Arranger name for CD-Text.
    /// </summary>
    public string? Arranger { get; init; }

    /// <summary>
    /// Liner notes or message for CD-Text.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// Total duration of this track in 2,352-byte Red Book frames (75 fps).
    /// </summary>
    public int DurationFrames
    {
        get
        {
            if (_durationFrames > 0) return _durationFrames;
            if (CustomPcmStreamFactory != null)
            {
                try
                {
                    using var s = CustomPcmStreamFactory();
                    if (s != null && s.CanSeek && s.Length > 0)
                    {
                        _durationFrames = (int)((s.Length + OpticalConstants.RawSectorSize - 1) / OpticalConstants.RawSectorSize);
                        return _durationFrames;
                    }
                }
                catch
                {
                }
            }
            return 0;
        }
        set => _durationFrames = value;
    }

    /// <summary>
    /// Total duration as a <see cref="TimeSpan"/>.
    /// </summary>
    public TimeSpan Duration => TimeSpan.FromSeconds((double)DurationFrames / OpticalConstants.AudioFramesPerSecond);

    /// <summary>
    /// Total duration as a Red Book Minute:Second:Frame address.
    /// </summary>
    public MsfAddress DurationMsf => MsfAddress.FromFrameCount(DurationFrames);
}

/// <summary>
/// Audio CD project compilation managing an ordered list of up to 99 Red Book audio tracks,
/// overall duration, CD-Text metadata generation, and media capacity validation.
/// </summary>
public class AudioCdCompilation
{
    /// <summary>
    /// Maximum number of tracks allowed on a Red Book Audio CD (99 tracks).
    /// </summary>
    public const int MaxTracks = 99;

    /// <summary>
    /// Nominal capacity in frames for a 74-minute disc (333,000 frames).
    /// </summary>
    public const int Capacity74MinFrames = OpticalConstants.Cd650MbSectors; // 333,000

    /// <summary>
    /// Nominal capacity in frames for an 80-minute disc (360,000 frames).
    /// </summary>
    public const int Capacity80MinFrames = OpticalConstants.Cd700MbSectors; // 360,000

    private readonly List<AudioCdTrackItem> _tracks = new();

    /// <summary>
    /// Album or compilation title.
    /// </summary>
    public string AlbumTitle { get; set; } = "Untitled Album";

    /// <summary>
    /// Primary album performer or artist.
    /// </summary>
    public string AlbumPerformer { get; set; } = "Various Artists";

    /// <summary>
    /// Album songwriter for CD-Text.
    /// </summary>
    public string? Songwriter { get; set; }

    /// <summary>
    /// Album composer for CD-Text.
    /// </summary>
    public string? Composer { get; set; }

    /// <summary>
    /// Album arranger for CD-Text.
    /// </summary>
    public string? Arranger { get; set; }

    /// <summary>
    /// Album message or liner notes for CD-Text.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Disc identification or catalog code for CD-Text.
    /// </summary>
    public string? DiscId { get; set; }

    /// <summary>
    /// UPC/EAN Media Catalog Number (13 decimal digits).
    /// </summary>
    public string? UpcEan { get; set; }

    /// <summary>
    /// Recording mode: Track-at-Once (TAO) or gapless Disc-at-Once (DAO).
    /// </summary>
    public AudioRecordingMode RecordingMode { get; set; } = AudioRecordingMode.DiscAtOnceGapless;

    /// <summary>
    /// Whether to generate and burn Lead-In CD-Text binary packs.
    /// </summary>
    public bool IncludeCdText { get; set; } = true;

    /// <summary>
    /// Whether to perform peak audio normalization across all tracks during burning.
    /// </summary>
    public bool NormalizeAllTracks { get; set; } = false;

    /// <summary>
    /// Default pre-gap in frames for tracks (default 150 frames = 2.0 seconds).
    /// </summary>
    public int DefaultPreGapFrames { get; set; } = OpticalConstants.CdLeadInFrames;

    /// <summary>
    /// Ordered list of tracks in this compilation.
    /// </summary>
    public IReadOnlyList<AudioCdTrackItem> Tracks => _tracks.AsReadOnly();

    /// <summary>
    /// Current number of tracks.
    /// </summary>
    public int TrackCount => _tracks.Count;

    /// <summary>
    /// Total duration in 2352-byte Red Book frames across all tracks.
    /// </summary>
    public int TotalFrames => _tracks.Sum(t => t.DurationFrames);

    /// <summary>
    /// Total duration as an MSF address.
    /// </summary>
    public MsfAddress TotalDurationMsf => MsfAddress.FromFrameCount(TotalFrames);

    /// <summary>
    /// Total duration as a <see cref="TimeSpan"/>.
    /// </summary>
    public TimeSpan TotalDuration => TimeSpan.FromSeconds((double)TotalFrames / OpticalConstants.AudioFramesPerSecond);

    /// <summary>
    /// Indicates whether the compilation fits within standard 74-minute media (333,000 frames).
    /// </summary>
    public bool FitsOn74MinMedia => TotalFrames <= Capacity74MinFrames;

    /// <summary>
    /// Indicates whether the compilation fits within standard 80-minute media (360,000 frames).
    /// </summary>
    public bool FitsOn80MinMedia => TotalFrames <= Capacity80MinFrames;

    /// <summary>
    /// Adds a track to the compilation.
    /// </summary>
    /// <param name="track">The track item to add.</param>
    /// <returns>The added track item with assigned TrackNumber.</returns>
    public AudioCdTrackItem AddTrack(AudioCdTrackItem track)
    {
        ArgumentNullException.ThrowIfNull(track);

        if (_tracks.Count >= MaxTracks)
        {
            throw new InvalidOperationException($"An Audio CD supports a maximum of {MaxTracks} tracks.");
        }

        int targetTrackNumber = track.TrackNumber > 0 ? track.TrackNumber : _tracks.Count + 1;
        var itemToAdd = track.TrackNumber == targetTrackNumber ? track : track with { TrackNumber = targetTrackNumber };

        _tracks.Add(itemToAdd);
        return itemToAdd;
    }

    /// <summary>
    /// Adds a new track to the compilation with the specified source and metadata.
    /// </summary>
    public AudioCdTrackItem AddTrack(
        string sourceFilePath,
        string title,
        string performer,
        string? isrc = null,
        int preGapFrames = 150,
        bool normalizeAudio = false,
        Func<Stream>? customPcmStreamFactory = null)
    {
        int trackNum = _tracks.Count + 1;
        var item = new AudioCdTrackItem(
            trackNum,
            sourceFilePath,
            title,
            performer,
            isrc,
            preGapFrames,
            normalizeAudio,
            customPcmStreamFactory);

        return AddTrack(item);
    }

    /// <summary>
    /// Adds multiple tracks to the compilation.
    /// </summary>
    public void AddTracks(IEnumerable<AudioCdTrackItem> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        foreach (var trk in tracks)
        {
            AddTrack(trk);
        }
    }

    /// <summary>
    /// Removes a track by its 1-based track number and renumbers remaining tracks sequentially.
    /// </summary>
    public bool RemoveTrack(int trackNumber)
    {
        int index = _tracks.FindIndex(t => t.TrackNumber == trackNumber);
        if (index >= 0)
        {
            _tracks.RemoveAt(index);
            RenumberTracks();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Clears all tracks from the compilation.
    /// </summary>
    public void ClearTracks()
    {
        _tracks.Clear();
    }

    /// <summary>
    /// Renumbers all tracks sequentially from 1 to Count.
    /// </summary>
    public void RenumberTracks()
    {
        for (int i = 0; i < _tracks.Count; i++)
        {
            if (_tracks[i].TrackNumber != i + 1)
            {
                _tracks[i] = _tracks[i] with { TrackNumber = i + 1 };
            }
        }
    }

    /// <summary>
    /// Validates the compilation capacity against a specified maximum frame count.
    /// </summary>
    public bool ValidateCapacity(int maxFrames, out string? errorMessage)
    {
        if (_tracks.Count == 0)
        {
            errorMessage = "Audio CD compilation contains no tracks.";
            return false;
        }

        if (_tracks.Count > MaxTracks)
        {
            errorMessage = $"Audio CD compilation contains {_tracks.Count} tracks, which exceeds the Red Book limit of {MaxTracks}.";
            return false;
        }

        if (TotalFrames > maxFrames)
        {
            errorMessage = $"Compilation duration ({TotalFrames} frames / {TotalDurationMsf}) exceeds media capacity of {maxFrames} frames.";
            return false;
        }

        errorMessage = null;
        return true;
    }

    /// <summary>
    /// Validates compilation capacity against standard 74-minute (333,000 frames) or 80-minute (360,000 frames) media.
    /// </summary>
    public bool ValidateCapacity(out string? errorMessage, bool use80MinMedia = true)
    {
        int maxFrames = use80MinMedia ? Capacity80MinFrames : Capacity74MinFrames;
        return ValidateCapacity(maxFrames, out errorMessage);
    }

    /// <summary>
    /// Validates compilation capacity against standard 74-minute (333,000 frames) or 80-minute (360,000 frames) media.
    /// </summary>
    public bool ValidateCapacity(bool use80MinMedia, out string? errorMessage)
    {
        int maxFrames = use80MinMedia ? Capacity80MinFrames : Capacity74MinFrames;
        return ValidateCapacity(maxFrames, out errorMessage);
    }

    /// <summary>
    /// Generates structured <see cref="CdTextAlbumMetadata"/> from the compilation properties and tracks.
    /// </summary>
    public CdTextAlbumMetadata CreateCdTextMetadata()
    {
        var trackMetaList = _tracks.Select(t => new CdTextTrackMetadata(
            TrackNumber: t.TrackNumber,
            Title: t.Title,
            Performer: !string.IsNullOrEmpty(t.Performer) ? t.Performer : AlbumPerformer,
            Songwriter: t.Songwriter ?? Songwriter ?? string.Empty,
            Composer: t.Composer ?? Composer ?? string.Empty,
            Arranger: t.Arranger ?? Arranger ?? string.Empty,
            Message: t.Message ?? Message ?? string.Empty,
            Isrc: t.Isrc ?? string.Empty
        )).ToList();

        return new CdTextAlbumMetadata(
            AlbumTitle: AlbumTitle,
            AlbumPerformer: AlbumPerformer,
            Songwriter: Songwriter ?? string.Empty,
            Composer: Composer ?? string.Empty,
            Arranger: Arranger ?? string.Empty,
            Message: Message ?? string.Empty,
            DiscId: DiscId ?? string.Empty,
            UpcEan: UpcEan ?? string.Empty,
            Tracks: trackMetaList
        );
    }

    /// <summary>
    /// Builds raw Red Book Annex J CD-Text 18-byte binary packs for this compilation.
    /// </summary>
    public byte[] BuildCdTextPacks() => CdTextPackBuilder.BuildPacks(CreateCdTextMetadata());
}
