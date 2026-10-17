using CdBurner.App.Mvvm;
using CdBurner.Core.Primitives;

namespace CdBurner.App.ViewModels;

/// <summary>
/// Represents an individual audio track item row in the Audio CD compilation list.
/// </summary>
public class AudioTrackRowViewModel : ObservableObject
{
    private int _trackNumber;
    private string _sourceFilePath = string.Empty;
    private string _title = string.Empty;
    private string _performer = string.Empty;
    private string? _songwriter;
    private string? _composer;
    private string? _isrc;
    private int _preGapFrames = 150;
    private bool _normalizeAudio;
    private int _durationFrames;
    private long _fileSizeBytes;

    /// <summary>
    /// Sequential 1-based track number on the Audio CD.
    /// </summary>
    public int TrackNumber
    {
        get => _trackNumber;
        set => SetProperty(ref _trackNumber, value);
    }

    /// <summary>
    /// Full physical file path of the source audio stream on disk.
    /// </summary>
    public string SourceFilePath
    {
        get => _sourceFilePath;
        set => SetProperty(ref _sourceFilePath, value);
    }

    /// <summary>
    /// Track title metadata for CD-Text.
    /// </summary>
    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    /// <summary>
    /// Performer / artist metadata for CD-Text.
    /// </summary>
    public string Performer
    {
        get => _performer;
        set => SetProperty(ref _performer, value);
    }

    /// <summary>
    /// Songwriter metadata for CD-Text.
    /// </summary>
    public string? Songwriter
    {
        get => _songwriter;
        set => SetProperty(ref _songwriter, value);
    }

    /// <summary>
    /// Composer metadata for CD-Text.
    /// </summary>
    public string? Composer
    {
        get => _composer;
        set => SetProperty(ref _composer, value);
    }

    /// <summary>
    /// International Standard Recording Code (ISRC) for CD-Text.
    /// </summary>
    public string? Isrc
    {
        get => _isrc;
        set => SetProperty(ref _isrc, value);
    }

    /// <summary>
    /// Pre-gap duration in 75 fps Red Book frames (150 frames = 2 seconds).
    /// </summary>
    public int PreGapFrames
    {
        get => _preGapFrames;
        set
        {
            if (SetProperty(ref _preGapFrames, value))
            {
                OnPropertyChanged(nameof(PreGapText));
            }
        }
    }

    /// <summary>
    /// Formatted pre-gap duration string.
    /// </summary>
    public string PreGapText => $"{((double)_preGapFrames / OpticalConstants.AudioFramesPerSecond):F1}s ({_preGapFrames}f)";

    /// <summary>
    /// Whether peak normalization is individually enabled for this track.
    /// </summary>
    public bool NormalizeAudio
    {
        get => _normalizeAudio;
        set => SetProperty(ref _normalizeAudio, value);
    }

    /// <summary>
    /// Total duration in 2,352-byte Red Book audio frames (75 fps).
    /// </summary>
    public int DurationFrames
    {
        get => _durationFrames;
        set
        {
            if (SetProperty(ref _durationFrames, value))
            {
                OnPropertyChanged(nameof(Duration));
                OnPropertyChanged(nameof(DurationMsf));
                OnPropertyChanged(nameof(DurationText));
            }
        }
    }

    /// <summary>
    /// Total duration as a standard <see cref="TimeSpan"/>.
    /// </summary>
    public TimeSpan Duration => TimeSpan.FromSeconds((double)_durationFrames / OpticalConstants.AudioFramesPerSecond);

    /// <summary>
    /// Total duration as an MSF address (Minutes:Seconds:Frames).
    /// </summary>
    public MsfAddress DurationMsf => MsfAddress.FromFrameCount(_durationFrames);

    /// <summary>
    /// Formatted duration string (mm:ss:ff).
    /// </summary>
    public string DurationText => DurationMsf.ToString();

    /// <summary>
    /// File size in bytes.
    /// </summary>
    public long FileSizeBytes
    {
        get => _fileSizeBytes;
        set
        {
            if (SetProperty(ref _fileSizeBytes, value))
            {
                OnPropertyChanged(nameof(FileSizeFormatted));
            }
        }
    }

    /// <summary>
    /// Formatted human-readable file size string.
    /// </summary>
    public string FileSizeFormatted
    {
        get
        {
            double mb = _fileSizeBytes / (1024.0 * 1024.0);
            return $"{mb:F2} MB";
        }
    }
}
