using System.IO;
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
    private string _formatName = string.Empty;
    private int _sampleRate = OpticalConstants.CdAudioSampleRate;
    private int _bitsPerSample = OpticalConstants.CdAudioBitsPerSample;
    private int _channels = OpticalConstants.CdAudioChannels;

    /// <summary>
    /// Optional custom PCM stream factory (used when a track is a frame slice of a single-file CUE + FLAC/WAV album image).
    /// </summary>
    public Func<Stream>? CustomPcmStreamFactory { get; set; }

    /// <summary>
    /// Slice start (75 fps frames) inside <see cref="SourceFilePath"/> for single-file CUE album tracks; null = whole file.
    /// Carried into the compilation so project files can rebuild the slice.
    /// </summary>
    public int? SourceStartFrame { get; set; }

    /// <summary>
    /// Slice length (75 fps frames), or null to run to the end of the file.
    /// </summary>
    public int? SourceFrameCount { get; set; }

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
        set
        {
            if (SetProperty(ref _sourceFilePath, value))
            {
                OnPropertyChanged(nameof(FormatSummary));
            }
        }
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
        set
        {
            if (SetProperty(ref _isrc, value))
            {
                OnPropertyChanged(nameof(IsrcProblem));
                OnPropertyChanged(nameof(HasIsrcProblem));
            }
        }
    }

    /// <summary>What is wrong with <see cref="Isrc"/>, in plain words; null when it is empty or valid.</summary>
    public string? IsrcProblem => CdBurner.Hardware.NativeWriting.SaoAudioLayout.DescribeIsrcProblem(_isrc);

    public bool HasIsrcProblem => IsrcProblem != null;

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

    /// <summary>
    /// Source codec/container format name (e.g. "FLAC 24-bit", "WAV PCM 16-bit", "MP3").
    /// </summary>
    public string FormatName
    {
        get => _formatName;
        set
        {
            if (SetProperty(ref _formatName, value))
            {
                OnPropertyChanged(nameof(FormatSummary));
            }
        }
    }

    /// <summary>
    /// Source sample rate in Hz (e.g. 44100, 96000).
    /// </summary>
    public int SampleRate
    {
        get => _sampleRate;
        set
        {
            if (SetProperty(ref _sampleRate, value))
            {
                OnPropertyChanged(nameof(FormatSummary));
            }
        }
    }

    /// <summary>
    /// Source bit depth per sample (e.g. 16, 24).
    /// </summary>
    public int BitsPerSample
    {
        get => _bitsPerSample;
        set
        {
            if (SetProperty(ref _bitsPerSample, value))
            {
                OnPropertyChanged(nameof(FormatSummary));
            }
        }
    }

    /// <summary>
    /// Source channel count (1 = mono, 2 = stereo).
    /// </summary>
    public int Channels
    {
        get => _channels;
        set
        {
            if (SetProperty(ref _channels, value))
            {
                OnPropertyChanged(nameof(FormatSummary));
            }
        }
    }

    /// <summary>
    /// Concise source format badge shown in the track table (e.g. "FLAC 24-bit / 96 kHz", "FLAC 16-bit / 44.1 kHz", "WAV 16-bit").
    /// </summary>
    public string FormatSummary
    {
        get
        {
            string baseFormat = !string.IsNullOrWhiteSpace(_formatName)
                ? _formatName
                : System.IO.Path.GetExtension(_sourceFilePath).TrimStart('.').ToUpperInvariant();

            if (string.IsNullOrWhiteSpace(baseFormat))
            {
                baseFormat = "PCM";
            }

            if (_sampleRate > 0)
            {
                double khz = _sampleRate / 1000.0;
                string khzStr = (_sampleRate % 1000 == 0) ? $"{_sampleRate / 1000} kHz" : $"{khz:F1} kHz";
                if (_bitsPerSample > 0 && !baseFormat.Contains("-bit", StringComparison.OrdinalIgnoreCase))
                {
                    return $"{baseFormat} {_bitsPerSample}-bit / {khzStr}";
                }
                return $"{baseFormat} / {khzStr}";
            }

            return baseFormat;
        }
    }
}
