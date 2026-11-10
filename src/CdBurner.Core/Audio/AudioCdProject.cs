using CdBurner.Core.Images;
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
    /// Disc-at-Once (DAO): writes all tracks in one pass with no forced gaps between them. (Lead-in CD-Text needs the
    /// planned native writer; IMAPIv2 cannot write it on physical drives.)
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
    /// When this track is one slice of a single-file album image (a multi-track CUE over one .flac/.wav),
    /// the start offset of the slice inside <see cref="SourceFilePath"/> in 75 fps frames (the track's INDEX 01).
    /// Null means the track is the whole file. Persisted so a saved project can rebuild the slice.
    /// </summary>
    public int? SourceStartFrame { get; init; }

    /// <summary>
    /// Length of the slice in 75 fps frames, or null to run to the end of <see cref="SourceFilePath"/>.
    /// Only meaningful when <see cref="SourceStartFrame"/> is set.
    /// </summary>
    public int? SourceFrameCount { get; init; }

    /// <summary>
    /// Optional back-reference to the single-file album source caching the decoded PCM for this track's slice.
    /// </summary>
    public SingleFileAlbumSource? AlbumSource { get; init; }

    /// <summary>
    /// How many frames at the end of the previous track's audio are this track's INDEX 00 (its pre-gap) in the CUE
    /// sheet. The audio stays where it is; the direct drive-command writer only moves the track mark, so a player
    /// counts down through it. Zero when the CUE sheet has no INDEX 00 for the track.
    /// </summary>
    public int Index00LeadFrames { get; init; }

    /// <summary>
    /// Audio in front of track 1's INDEX 01 in a single-file CUE album ("hidden track one audio"), which a player
    /// reaches by rewinding from the start. Only the direct drive-command writer can write it.
    /// </summary>
    public Func<Stream>? HiddenAudioPcmStreamFactory { get; init; }

    /// <summary>Length of the hidden audio in CD frames.</summary>
    public int HiddenAudioFrames { get; init; }

    /// <summary>CUE "FLAGS PRE": the track was mastered with pre-emphasis.</summary>
    public bool PreEmphasis { get; init; }

    /// <summary>CUE "FLAGS DCP": digital copying is permitted.</summary>
    public bool CopyPermitted { get; init; }

    /// <summary>
    /// Total duration of this track in 2,352-byte Red Book frames (75 fps).
    /// </summary>
    public int DurationFrames
    {
        get
        {
            if (_durationFrames > 0) return _durationFrames;

            // 1. Fast path: if the slice frame count was known from the CUE sheet, return it immediately
            // without decoding the album or opening streams.
            if (SourceFrameCount.HasValue && SourceFrameCount.Value > 0)
            {
                _durationFrames = SourceFrameCount.Value;
                return _durationFrames;
            }

            // 2. Sliced track running to the end of the file (SourceStartFrame set, SourceFrameCount null):
            // probe the file's header metadata instead of decoding the entire audio file synchronously.
            if (SourceStartFrame.HasValue && !string.IsNullOrWhiteSpace(SourceFilePath) && File.Exists(SourceFilePath))
            {
                try
                {
                    AudioFormatMetadata? meta = FlacFileCodec.IsFlacFile(SourceFilePath)
                        ? FlacFileCodec.ProbeAudioFile(SourceFilePath)
                        : WavFileCodec.ProbeAudioFile(SourceFilePath);

                    if (meta != null && meta.TotalFrames > 0)
                    {
                        _durationFrames = Math.Max(0, meta.TotalFrames - SourceStartFrame.Value);
                        return _durationFrames;
                    }
                }
                catch
                {
                    // Fall back to stream factory below if header probing fails
                }
            }

            // 3. Fallback: custom stream factory
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
/// A single-file album image (one .flac/.wav/... referenced by a multi-track CUE sheet). The file is decoded to
/// sector-aligned Red Book PCM once, on first use, and each track reads a zero-copy slice of that buffer.
/// </summary>
public sealed class SingleFileAlbumSource
{
    private readonly object _sync = new();
    private byte[]? _pcm;
    private readonly IAudioDecoder? _audioDecoder;

    /// <summary>
    /// Creates a lazily decoded album source for <paramref name="filePath"/>.
    /// </summary>
    public SingleFileAlbumSource(string filePath, IAudioDecoder? audioDecoder = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = filePath;
        _audioDecoder = audioDecoder;
    }

    /// <summary>
    /// Path of the album image.
    /// </summary>
    public string FilePath { get; }

    /// <summary>
    /// Whether the album has already been decoded and is currently cached in memory.
    /// </summary>
    public bool IsDecoded
    {
        get
        {
            lock (_sync)
            {
                return _pcm != null;
            }
        }
    }

    /// <summary>
    /// Releases the cached decoded PCM buffer (~800 MB) from memory.
    /// If accessed again, the audio will be re-decoded lazily on demand.
    /// </summary>
    public void ReleaseDecodedBuffer()
    {
        lock (_sync)
        {
            _pcm = null;
        }
    }

    private byte[] GetOrDecodePcm()
    {
        lock (_sync)
        {
            if (_pcm != null) return _pcm;
            _pcm = DecodeSectorAligned(FilePath, _audioDecoder);
            return _pcm;
        }
    }

    /// <summary>
    /// Total decoded length in 75 fps frames (decodes on first access).
    /// </summary>
    public int TotalFrames => GetOrDecodePcm().Length / OpticalConstants.RawSectorSize;

    /// <summary>
    /// Returns a factory that opens the slice [<paramref name="startFrame"/>, +<paramref name="frameCount"/>) as a read-only stream.
    /// A null <paramref name="frameCount"/> runs to the end of the album. Out-of-range requests are clamped.
    /// </summary>
    public Func<Stream> CreateSliceFactory(int startFrame, int? frameCount) => () =>
    {
        byte[] all = GetOrDecodePcm();
        int totalFrames = all.Length / OpticalConstants.RawSectorSize;
        int start = Math.Clamp(startFrame, 0, totalFrames);
        int remaining = totalFrames - start;
        int frames = frameCount.HasValue ? Math.Clamp(frameCount.Value, 0, remaining) : remaining;
        return new MemoryStream(
            all,
            start * OpticalConstants.RawSectorSize,
            frames * OpticalConstants.RawSectorSize,
            writable: false,
            publiclyVisible: true);
    };

    private static byte[] DecodeSectorAligned(string filePath, IAudioDecoder? audioDecoder)
    {
        Stream raw = OpenRedBookPcm(filePath, audioDecoder);
        using (raw)
        {
            if (raw is MemoryStream ms && ms.TryGetBuffer(out var seg) && seg.Offset == 0 &&
                seg.Count == seg.Array!.Length && seg.Count % OpticalConstants.RawSectorSize == 0)
            {
                return seg.Array; // already sector-aligned; no copy
            }

            using var padded = RedBookPcmDspProcessor.PadToRedBookSectorBoundary(raw);
            if (padded is MemoryStream pms && pms.TryGetBuffer(out var pseg) && pseg.Offset == 0 && pseg.Count == pseg.Array!.Length)
            {
                return pseg.Array;
            }

            using var mem = new MemoryStream();
            padded.CopyTo(mem);
            return mem.ToArray();
        }
    }

    /// <summary>
    /// Opens Red Book PCM for <paramref name="filePath"/> using <paramref name="audioDecoder"/>, or the built-in
    /// FLAC/WAV codecs when no decoder is supplied.
    /// </summary>
    public static Stream OpenRedBookPcm(string filePath, IAudioDecoder? audioDecoder)
    {
        if (audioDecoder != null) return audioDecoder.OpenRedBookPcmStream(filePath);
        return FlacFileCodec.IsFlacFile(filePath)
            ? FlacFileCodec.OpenRedBookPcmStream(filePath)
            : WavFileCodec.OpenRedBookPcmStream(filePath);
    }
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
    /// Whether to perform perceptual RMS / ReplayGain-style loudness leveling (-14 dBFS) across all tracks.
    /// </summary>
    public bool UseRmsLoudnessLeveling { get; set; } = false;

    /// <summary>
    /// Equal-power crossfade duration in seconds between adjacent tracks (0.0 = disabled).
    /// </summary>
    public double CrossfadeSeconds { get; set; } = 0.0;

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
        ReleaseCachedAlbumBuffers();
        _tracks.Clear();
    }

    /// <summary>
    /// Releases decoded in-memory PCM buffers for all single-file album sources in this compilation.
    /// Buffers will be re-decoded on demand if accessed again.
    /// </summary>
    public void ReleaseCachedAlbumBuffers()
    {
        foreach (var track in _tracks)
        {
            track.AlbumSource?.ReleaseDecodedBuffer();
        }
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

    /// <summary>
    /// Imports audio tracks and CD-Text metadata from a CUE sheet file (.cue), supporting both multi-file (.flac/.wav/.mp3)
    /// and single-file album images with multiple INDEX 01 track offsets.
    /// </summary>
    public int ImportCueSheet(string cueFilePath, IAudioDecoder? audioDecoder = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cueFilePath);
        var cueSheet = CueSheetParser.ParseFile(cueFilePath);
        string cueDir = Path.GetDirectoryName(cueFilePath) ?? string.Empty;
        return ImportCueSheet(cueSheet, cueDir, audioDecoder);
    }

    /// <summary>
    /// Imports audio tracks and CD-Text metadata from a parsed <see cref="CueSheet"/>.
    /// </summary>
    public int ImportCueSheet(CueSheet cueSheet, string cueDirectory, IAudioDecoder? audioDecoder = null)
    {
        ArgumentNullException.ThrowIfNull(cueSheet);
        cueDirectory ??= string.Empty;

        if (!string.IsNullOrWhiteSpace(cueSheet.Title) &&
            (string.IsNullOrWhiteSpace(AlbumTitle) || AlbumTitle == "Untitled Album" || AlbumTitle == "Audio CD"))
        {
            AlbumTitle = cueSheet.Title;
        }

        if (!string.IsNullOrWhiteSpace(cueSheet.Performer) &&
            (string.IsNullOrWhiteSpace(AlbumPerformer) || AlbumPerformer == "Various Artists"))
        {
            AlbumPerformer = cueSheet.Performer;
        }

        if (!string.IsNullOrWhiteSpace(cueSheet.Songwriter) && string.IsNullOrWhiteSpace(Songwriter))
        {
            Songwriter = cueSheet.Songwriter;
        }

        if (!string.IsNullOrWhiteSpace(cueSheet.Catalog) && string.IsNullOrWhiteSpace(UpcEan))
        {
            UpcEan = cueSheet.Catalog;
        }

        int added = 0;
        foreach (var file in cueSheet.Files)
        {
            string audioFilePath = Path.IsPathRooted(file.FileName)
                ? file.FileName
                : Path.Combine(cueDirectory, file.FileName);

            var audioTracks = file.Tracks.Where(t => t.DataType == CueTrackDataType.Audio).ToList();
            if (audioTracks.Count == 0)
            {
                continue;
            }

            AudioFormatMetadata? meta = null;
            if (File.Exists(audioFilePath))
            {
                try
                {
                    if (audioDecoder != null)
                    {
                        meta = audioDecoder.ProbeAudioFile(audioFilePath);
                    }
                    else if (FlacFileCodec.IsFlacFile(audioFilePath))
                    {
                        meta = FlacFileCodec.ProbeAudioFile(audioFilePath);
                    }
                    else if (string.Equals(Path.GetExtension(audioFilePath), ".wav", StringComparison.OrdinalIgnoreCase))
                    {
                        meta = WavFileCodec.ProbeAudioFile(audioFilePath);
                    }
                }
                catch
                {
                }
            }

            int totalFileFrames = 0;
            if (meta != null)
            {
                totalFileFrames = meta.TotalRedBookBytes > 0
                    ? OpticalConstants.BytesToSectors(meta.TotalRedBookBytes, OpticalConstants.RawSectorSize)
                    : (int)Math.Round(meta.Duration.TotalSeconds * OpticalConstants.AudioFramesPerSecond);
            }

            if (audioTracks.Count == 1)
            {
                if (_tracks.Count >= MaxTracks) break;

                var trk = audioTracks[0];
                int trackNum = _tracks.Count + 1;
                string title = !string.IsNullOrWhiteSpace(trk.Title)
                    ? trk.Title
                    : (!string.IsNullOrWhiteSpace(meta?.Title) ? meta.Title : Path.GetFileNameWithoutExtension(audioFilePath));
                string performer = !string.IsNullOrWhiteSpace(trk.Performer)
                    ? trk.Performer
                    : (!string.IsNullOrWhiteSpace(meta?.Artist) ? meta.Artist : AlbumPerformer);
                int preGap = trk.PreGap?.ToFrameCount() ?? (trackNum == 1 ? DefaultPreGapFrames : 0);

                AddTrack(new AudioCdTrackItem(
                    TrackNumber: trackNum,
                    SourceFilePath: audioFilePath,
                    Title: title,
                    Performer: performer,
                    Isrc: !string.IsNullOrWhiteSpace(trk.Isrc) ? trk.Isrc : meta?.Isrc,
                    PreGapFrames: preGap)
                {
                    Songwriter = trk.Songwriter ?? meta?.Songwriter ?? Songwriter,
                    Composer = meta?.Composer ?? Composer,
                    DurationFrames = totalFileFrames,
                    PreEmphasis = trk.Flags.Contains("PRE"),
                    CopyPermitted = trk.Flags.Contains("DCP")
                });
                added++;
            }
            else
            {
                // Single-file CUE sheet with multiple INDEX 01 track offsets inside one audio file (.flac / .wav / etc.).
                // The album is decoded once (lazily) and every track reads a zero-copy slice of it.
                var album = new SingleFileAlbumSource(audioFilePath, audioDecoder);

                // Each slice runs from this track's INDEX 01 to the next track's INDEX 01. Audio between the next
                // track's INDEX 00 and INDEX 01 (its pre-gap) therefore stays at the tail of this track: it is
                // played exactly once, never duplicated or clipped. (The burn engine writes pre-gaps as generated
                // silence, so moving that audio into PreGapFrames would replace real audio with silence.)
                static int? GetIndex01Frames(CueTrack track) =>
                    (track.Indices.FirstOrDefault(idx => idx.IndexNumber == 1) ?? track.Indices.FirstOrDefault())?.Position.ToFrameCount();

                static int? GetIndex00Frames(CueTrack track) =>
                    track.Indices.FirstOrDefault(idx => idx.IndexNumber == 0)?.Position.ToFrameCount();

                for (int k = 0; k < audioTracks.Count; k++)
                {
                    if (_tracks.Count >= MaxTracks) break;

                    var trk = audioTracks[k];
                    int trackNum = _tracks.Count + 1;
                    int startFrame = GetIndex01Frames(trk) ?? 0;
                    bool isLastInFile = k == audioTracks.Count - 1;
                    int? nextStartFrame = isLastInFile ? null : GetIndex01Frames(audioTracks[k + 1]);

                    // Last track (or unknown next offset) runs to the end of the decoded file.
                    int? sliceFrameCount = nextStartFrame.HasValue ? Math.Max(0, nextStartFrame.Value - startFrame) : null;
                    int displayFrames = sliceFrameCount ?? Math.Max(0, totalFileFrames - startFrame);

                    string title = !string.IsNullOrWhiteSpace(trk.Title) ? trk.Title : $"Track {trackNum:D2}";
                    string performer = !string.IsNullOrWhiteSpace(trk.Performer) ? trk.Performer : AlbumPerformer;
                    int preGap = trk.PreGap?.ToFrameCount() ?? (trackNum == 1 ? DefaultPreGapFrames : 0);

                    // INDEX 00 marks: for a later track the gap audio is the tail of the previous slice; for the
                    // first track of the file it is audio in front of INDEX 01 that no slice covers.
                    int index00Lead = 0;
                    int hiddenFrames = 0;
                    if (k > 0 && GetIndex00Frames(trk) is int index00 && index00 < startFrame)
                    {
                        int previousStart = GetIndex01Frames(audioTracks[k - 1]) ?? 0;
                        index00Lead = Math.Max(0, startFrame - Math.Max(index00, previousStart));
                    }
                    else if (k == 0 && trackNum == 1 && startFrame > 0)
                    {
                        hiddenFrames = startFrame;
                    }

                    AddTrack(new AudioCdTrackItem(
                        TrackNumber: trackNum,
                        SourceFilePath: audioFilePath,
                        Title: title,
                        Performer: performer,
                        Isrc: trk.Isrc,
                        PreGapFrames: preGap,
                        CustomPcmStreamFactory: album.CreateSliceFactory(startFrame, sliceFrameCount))
                    {
                        Songwriter = trk.Songwriter ?? Songwriter,
                        Composer = Composer,
                        DurationFrames = displayFrames,
                        SourceStartFrame = startFrame,
                        SourceFrameCount = sliceFrameCount,
                        AlbumSource = album,
                        Index00LeadFrames = index00Lead,
                        HiddenAudioFrames = hiddenFrames,
                        HiddenAudioPcmStreamFactory = hiddenFrames > 0 ? album.CreateSliceFactory(0, hiddenFrames) : null,
                        PreEmphasis = trk.Flags.Contains("PRE"),
                        CopyPermitted = trk.Flags.Contains("DCP")
                    });
                    added++;
                }
            }
        }

        return added;
    }

    /// <summary>
    /// Imports audio tracks and metadata from an M3U, M3U8, or PLS playlist file.
    /// </summary>
    public int ImportPlaylist(string playlistFilePath, IAudioDecoder? audioDecoder = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistFilePath);
        var entries = AudioPlaylistParser.ParseFile(playlistFilePath);
        int added = 0;

        foreach (var entry in entries)
        {
            if (_tracks.Count >= MaxTracks) break;
            if (!File.Exists(entry.FilePath)) continue;

            if (!string.IsNullOrWhiteSpace(entry.Album) &&
                (string.IsNullOrWhiteSpace(AlbumTitle) || AlbumTitle == "Untitled Album" || AlbumTitle == "Audio CD"))
            {
                AlbumTitle = entry.Album;
            }

            AudioFormatMetadata? meta = null;
            try
            {
                if (audioDecoder != null)
                {
                    meta = audioDecoder.ProbeAudioFile(entry.FilePath);
                }
                else if (FlacFileCodec.IsFlacFile(entry.FilePath))
                {
                    meta = FlacFileCodec.ProbeAudioFile(entry.FilePath);
                }
                else if (string.Equals(Path.GetExtension(entry.FilePath), ".wav", StringComparison.OrdinalIgnoreCase))
                {
                    meta = WavFileCodec.ProbeAudioFile(entry.FilePath);
                }
            }
            catch
            {
            }

            if (!string.IsNullOrWhiteSpace(meta?.Album) &&
                (string.IsNullOrWhiteSpace(AlbumTitle) || AlbumTitle == "Untitled Album" || AlbumTitle == "Audio CD"))
            {
                AlbumTitle = meta.Album!;
            }

            string? artistCandidate = entry.Performer ?? meta?.AlbumArtist ?? meta?.Artist;
            if (!string.IsNullOrWhiteSpace(artistCandidate) &&
                (string.IsNullOrWhiteSpace(AlbumPerformer) || AlbumPerformer == "Various Artists"))
            {
                AlbumPerformer = artistCandidate;
            }

            int trackNum = _tracks.Count + 1;
            string title = !string.IsNullOrWhiteSpace(entry.Title)
                ? entry.Title
                : (!string.IsNullOrWhiteSpace(meta?.Title) ? meta.Title! : Path.GetFileNameWithoutExtension(entry.FilePath));
            string performer = !string.IsNullOrWhiteSpace(entry.Performer)
                ? entry.Performer
                : (!string.IsNullOrWhiteSpace(meta?.Artist) ? meta.Artist! : AlbumPerformer);

            int frames = 0;
            if (meta != null && meta.TotalRedBookBytes > 0)
            {
                frames = OpticalConstants.BytesToSectors(meta.TotalRedBookBytes, OpticalConstants.RawSectorSize);
            }
            else if (entry.DurationSeconds.HasValue && entry.DurationSeconds.Value > 0)
            {
                frames = entry.DurationSeconds.Value * OpticalConstants.AudioFramesPerSecond;
            }

            AddTrack(new AudioCdTrackItem(
                TrackNumber: trackNum,
                SourceFilePath: entry.FilePath,
                Title: title,
                Performer: performer,
                Isrc: meta?.Isrc,
                PreGapFrames: trackNum == 1 ? DefaultPreGapFrames : 0)
            {
                Songwriter = meta?.Songwriter ?? Songwriter,
                Composer = meta?.Composer ?? Composer,
                DurationFrames = frames
            });
            added++;
        }

        return added;
    }
}
