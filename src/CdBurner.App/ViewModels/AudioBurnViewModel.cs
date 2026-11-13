using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows.Input;
using CdBurner.App.Mvvm;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Images;
using CdBurner.Core.Persistence;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Audio;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;
using Microsoft.Win32;

namespace CdBurner.App.ViewModels;

/// <summary>
/// View model orchestrating Red Book Audio CD mastering, CD-Text album and track metadata editing,
/// TAO vs gapless DAO recording modes, DSP peak normalization, duration/capacity tracking, and Digital Audio Extraction (ripper).
/// </summary>
public class AudioBurnViewModel : ObservableObject, IDisposable
{
    private bool _disposed;

    /// <summary>
    /// Stops any playing preview (releasing the SoundPlayer and its WAV buffer) and cancels an in-flight burn or rip.
    /// Called when the main window closes so preview audio never keeps playing after shutdown.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopPreview();

        try
        {
            _activeBurnCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private readonly IDiscDeviceService _deviceService;
    private readonly IBurnEngineBackend _burnEngine;
    private readonly IAudioDecoder _audioDecoder;
    private readonly AudioCdBurnService _audioBurnService;
    private readonly AudioCdRipperService _ripperService;
    private readonly AppSettingsService? _settingsService;

    private string _albumTitle = "Untitled Album";
    private string _albumPerformer = "Various Artists";
    private string? _albumSongwriter;
    private string? _upcEanCode;
    private AudioRecordingMode _recordingMode = AudioRecordingMode.DiscAtOnceGapless;
    private bool _normalizeAllTracks = false;
    private bool _useRmsLoudnessLeveling = false;
    private double _crossfadeSeconds = 0.0;
    private int _defaultPreGapFrames = OpticalConstants.CdLeadInFrames; // 150 frames = 2.0s
    private bool _includeCdText = true;
    private bool _isPreviewPlaying;
    private string _previewStatusText = "Select a track to preview";
    private string? _lastExportedCoverHtmlPath;
    private System.Media.SoundPlayer? _soundPlayer;
    private MemoryStream? _previewWavStream;
    private CancellationTokenSource? _previewCts;
    private MsfAddress _totalDuration = new(0, 0, 0);
    private double _discUsagePercent;
    private string _ripOutputFolder = string.Empty;
    private int _readOffsetSamples;
    private bool _secureRip = true; // matches AppSettings.SecureAudioRipByDefault
    private bool _lookupMetadata = true;
    private bool _verifyAccurateRip = true;
    private bool _continueOnUnreadable;
    private string _ripReport = string.Empty;
    private AudioRipFormat _selectedRipFormat = AudioRipFormat.Wav;
    private string _ripBitrateKbps = string.Empty;

    private AudioTrackRowViewModel? _selectedTrack;
    private IDiscRecorderBackend? _selectedRecorder;
    private bool _isBusy;
    private string _statusMessage = "Ready";
    private double _burnProgress;
    private BurnOperationResult? _lastBurnResult;
    private AudioCdRipResult? _lastRipResult;

    #region Properties

    /// <summary>
    /// Optical drive enumeration service.
    /// </summary>
    public IDiscDeviceService DeviceService => _deviceService;

    /// <summary>
    /// Optical burn engine backend.
    /// </summary>
    public IBurnEngineBackend BurnEngine => _burnEngine;

    /// <summary>
    /// Audio decoder used to probe and decode audio files.
    /// </summary>
    public IAudioDecoder AudioDecoder => _audioDecoder;

    /// <summary>
    /// High-level Audio CD burning service.
    /// </summary>
    public AudioCdBurnService AudioBurnService => _audioBurnService;

    /// <summary>
    /// High-level Audio CD ripping service.
    /// </summary>
    public AudioCdRipperService RipperService => _ripperService;

    /// <summary>
    /// Album or compilation title for CD-Text.
    /// </summary>
    public string AlbumTitle
    {
        get => _albumTitle;
        set => SetProperty(ref _albumTitle, value);
    }

    /// <summary>
    /// Primary album performer or artist for CD-Text.
    /// </summary>
    public string AlbumPerformer
    {
        get => _albumPerformer;
        set
        {
            if (SetProperty(ref _albumPerformer, value))
            {
                // Update performers that haven't been customized
                foreach (var trk in Tracks)
                {
                    if (string.IsNullOrWhiteSpace(trk.Performer) || trk.Performer == "Various Artists")
                    {
                        trk.Performer = value;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Album songwriter metadata for CD-Text.
    /// </summary>
    public string? AlbumSongwriter
    {
        get => _albumSongwriter;
        set => SetProperty(ref _albumSongwriter, value);
    }

    /// <summary>
    /// Universal Product Code / European Article Number (UPC/EAN) media catalog number for CD-Text.
    /// </summary>
    public string? UpcEanCode
    {
        get => _upcEanCode;
        set
        {
            if (SetProperty(ref _upcEanCode, value))
            {
                OnPropertyChanged(nameof(UpcEanProblem));
            }
        }
    }

    /// <summary>What is wrong with <see cref="UpcEanCode"/>, in plain words; null when it is empty or valid.</summary>
    public string? UpcEanProblem => CdBurner.Hardware.NativeWriting.SaoAudioLayout.DescribeMcnProblem(_upcEanCode);

    /// <summary>
    /// Why a direct burn cannot start with the codes as typed, or null. The direct writer leaves out an invalid ISRC or
    /// catalog number, so a typo would vanish from the disc without a word; it is caught here instead.
    /// </summary>
    public string? CodeProblem()
    {
        if (UpcEanProblem is { } upc)
        {
            return $"UPC/EAN \"{UpcEanCode}\": {upc}";
        }
        var bad = Tracks.FirstOrDefault(t => t.HasIsrcProblem);
        return bad is null ? null : $"Track {bad.TrackNumber} ISRC \"{bad.Isrc}\": {bad.IsrcProblem}";
    }

    /// <summary>
    /// Audio recording mode: Track-at-Once (TAO) or gapless Disc-at-Once (DAO).
    /// </summary>
    public AudioRecordingMode RecordingMode
    {
        get => _recordingMode;
        set => SetProperty(ref _recordingMode, value);
    }

    /// <summary>
    /// When true, peak audio DSP normalization is applied across all tracks prior to burning.
    /// </summary>
    public bool NormalizeAllTracks
    {
        get => _normalizeAllTracks;
        set => SetProperty(ref _normalizeAllTracks, value);
    }

    /// <summary>
    /// When true, perceptual RMS / ReplayGain-style loudness leveling (-14 dBFS) is applied across all tracks.
    /// </summary>
    public bool UseRmsLoudnessLeveling
    {
        get => _useRmsLoudnessLeveling;
        set => SetProperty(ref _useRmsLoudnessLeveling, value);
    }

    /// <summary>
    /// Equal-power crossfade duration in seconds between adjacent tracks (0.0 = disabled, max 10.0s).
    /// </summary>
    public double CrossfadeSeconds
    {
        get => _crossfadeSeconds;
        set => SetProperty(ref _crossfadeSeconds, Math.Clamp(value, 0.0, 10.0));
    }

    /// <summary>
    /// Indicates whether the built-in audio track preview player is currently playing.
    /// </summary>
    public bool IsPreviewPlaying
    {
        get => _isPreviewPlaying;
        set
        {
            if (SetProperty(ref _isPreviewPlaying, value))
            {
                PreviewSelectedTrackCommand?.RaiseCanExecuteChanged();
                StopPreviewCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Status text for the built-in audio track preview player.
    /// </summary>
    public string PreviewStatusText
    {
        get => _previewStatusText;
        set => SetProperty(ref _previewStatusText, value);
    }

    /// <summary>
    /// Path to the most recently exported printable CD Jewel Case Cover &amp; Tracklist HTML file.
    /// </summary>
    public string? LastExportedCoverHtmlPath
    {
        get => _lastExportedCoverHtmlPath;
        set => SetProperty(ref _lastExportedCoverHtmlPath, value);
    }

    /// <summary>
    /// Default pre-gap in 75 fps frames assigned to new tracks (default 150 frames = 2.0 seconds).
    /// </summary>
    public int DefaultPreGapFrames
    {
        get => _defaultPreGapFrames;
        set => SetProperty(ref _defaultPreGapFrames, value);
    }

    /// <summary>
    /// Whether to generate and record Lead-In CD-Text binary packs.
    /// </summary>
    public bool IncludeCdText
    {
        get => _includeCdText;
        set => SetProperty(ref _includeCdText, value);
    }

    private bool _useDirectDriveCommands;
    private bool _testWriteFirst;

    /// <summary>
    /// Burn with IgnisDisc's own drive commands (Session-at-Once) instead of the Windows burning service. Off by
    /// default, chosen per burn. This writes CD-Text, ISRC and catalog codes, pre-emphasis flags and a CUE sheet's
    /// gap marks to the disc, and reads the disc back afterwards.
    /// </summary>
    public bool UseDirectDriveCommands
    {
        get => _useDirectDriveCommands;
        set
        {
            if (SetProperty(ref _useDirectDriveCommands, value) && !value)
            {
                TestWriteFirst = false;
            }
        }
    }

    /// <summary>
    /// With <see cref="UseDirectDriveCommands"/>: run a laser-off test write before the real one. Most drives then
    /// need the disc reloaded before they record it, so the burn stops after the test and says so.
    /// </summary>
    public bool TestWriteFirst
    {
        get => _testWriteFirst;
        set => SetProperty(ref _testWriteFirst, value && _useDirectDriveCommands);
    }

    /// <summary>Folder for the command logs of direct burns; null for the default under %LOCALAPPDATA%.</summary>
    public string? DirectBurnLogDirectory { get; set; }

    /// <summary>The direct writer's own result for the last burn made with <see cref="UseDirectDriveCommands"/>.</summary>
    public CdBurner.Hardware.NativeWriting.NativeSaoWriteResult? LastDirectBurnResult { get; private set; }

    /// <summary>The command log of the last direct burn.</summary>
    public string? LastDirectBurnLogPath { get; private set; }

    /// <summary>
    /// Ordered list of audio tracks in this compilation.
    /// </summary>
    public ObservableCollection<AudioTrackRowViewModel> Tracks { get; } = new();

    /// <summary>
    /// Indicates whether the compilation currently contains at least one audio track.
    /// </summary>
    public bool HasTracks => Tracks.Count > 0;

    /// <summary>
    /// Currently selected track in the track table.
    /// </summary>
    public AudioTrackRowViewModel? SelectedTrack
    {
        get => _selectedTrack;
        set
        {
            if (SetProperty(ref _selectedTrack, value))
            {
                MoveTrackUpCommand.RaiseCanExecuteChanged();
                MoveTrackDownCommand.RaiseCanExecuteChanged();
                RemoveTrackCommand.RaiseCanExecuteChanged();
                PreviewSelectedTrackCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Total duration in Red Book Minute:Second:Frame format across all compilation tracks.
    /// </summary>
    public MsfAddress TotalDuration
    {
        get => _totalDuration;
        set
        {
            if (SetProperty(ref _totalDuration, value))
            {
                OnPropertyChanged(nameof(TotalDurationText));
                OnPropertyChanged(nameof(DiscUsageText));
            }
        }
    }

    /// <summary>
    /// Percentage (0.0 to 100.0) of target CD media capacity consumed by this compilation.
    /// </summary>
    public double DiscUsagePercent
    {
        get => _discUsagePercent;
        set
        {
            if (SetProperty(ref _discUsagePercent, value))
            {
                OnPropertyChanged(nameof(DiscUsageText));
            }
        }
    }

    /// <summary>
    /// Target destination folder for ripping Audio CD tracks to uncompressed WAV files.
    /// </summary>
    public string RipOutputFolder
    {
        get => _ripOutputFolder;
        set => SetProperty(ref _ripOutputFolder, value);
    }

    /// <summary>
    /// Available optical recorders discovered on the system.
    /// </summary>
    public ObservableCollection<IDiscRecorderBackend> AvailableRecorders { get; } = new();

    /// <summary>
    /// Optical recorder selected as target for burning or ripping.
    /// </summary>
    public IDiscRecorderBackend? SelectedRecorder
    {
        get => _selectedRecorder;
        set
        {
            if (SetProperty(ref _selectedRecorder, value))
            {
                RecalculateTotals();
            }
        }
    }

    /// <summary>
    /// Currently selected write speed for burning operations.
    /// </summary>
    public WriteSpeedDescriptor? SelectedWriteSpeed
    {
        get => _selectedWriteSpeed;
        set => SetProperty(ref _selectedWriteSpeed, value);
    }

    private WriteSpeedDescriptor? _selectedWriteSpeed;
    private CancellationTokenSource? _activeBurnCts;

    /// <summary>
    /// Indicates whether a burn or rip operation is currently executing.
    /// </summary>
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                StartAudioBurnCommand.RaiseCanExecuteChanged();
                RipAudioCdCommand.RaiseCanExecuteChanged();
                DetectReadOffsetCommand?.RaiseCanExecuteChanged();
                CancelBurnCommand.RaiseCanExecuteChanged();
                ClearAllTracksCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Real-time status description displayed in the view.
    /// </summary>
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>
    /// Burning or ripping progress percentage (0.0 to 100.0).
    /// </summary>
    public double BurnProgress
    {
        get => _burnProgress;
        set => SetProperty(ref _burnProgress, value);
    }

    /// <summary>
    /// Formatted total duration string (mm:ss:ff).
    /// </summary>
    public string TotalDurationText => _totalDuration.ToString();

    /// <summary>
    /// Formatted disc usage summary string (e.g. "42:15:30 / 80:00:00 (52.8%)").
    /// </summary>
    public string DiscUsageText => $"{_totalDuration} ({Math.Min(100.0, _discUsagePercent):F1}%)";

    /// <summary>
    /// Formatted track count summary.
    /// </summary>
    public string TotalTracksText => $"{Tracks.Count} / {AudioCdCompilation.MaxTracks} Tracks";

    /// <summary>
    /// Result of the most recent burn operation.
    /// </summary>
    public BurnOperationResult? LastBurnResult
    {
        get => _lastBurnResult;
        set => SetProperty(ref _lastBurnResult, value);
    }

    /// <summary>
    /// Result of the most recent rip operation.
    /// </summary>
    public AudioCdRipResult? LastRipResult
    {
        get => _lastRipResult;
        set => SetProperty(ref _lastRipResult, value);
    }

    /// <summary>
    /// Hardware read offset correction in samples (-10000 to +10000) applied during Digital Audio Extraction.
    /// </summary>
    public int ReadOffsetSamples
    {
        get => _readOffsetSamples;
        set
        {
            int clamped = Math.Clamp(value, -10000, 10000);
            if (_readOffsetSamples != clamped)
            {
                _readOffsetSamples = clamped;
                OnPropertyChanged();
                SaveSettingsIfAvailable();
            }
            else if (_readOffsetSamples != value)
            {
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Whether to use C2 error pointer based secure extraction and re-read suspect sectors.
    /// </summary>
    public bool SecureRip
    {
        get => _secureRip;
        set
        {
            if (SetProperty(ref _secureRip, value))
            {
                SaveSettingsIfAvailable();
            }
        }
    }

    /// <summary>
    /// Whether to query MusicBrainz online database for album and track metadata prior to ripping.
    /// </summary>
    public bool LookupMetadata
    {
        get => _lookupMetadata;
        set
        {
            if (SetProperty(ref _lookupMetadata, value))
            {
                SaveSettingsIfAvailable();
            }
        }
    }

    /// <summary>
    /// Whether to verify extracted track checksums against the online AccurateRip database after ripping.
    /// </summary>
    public bool VerifyAccurateRip
    {
        get => _verifyAccurateRip;
        set
        {
            if (SetProperty(ref _verifyAccurateRip, value))
            {
                SaveSettingsIfAvailable();
            }
        }
    }

    /// <summary>Output formats offered by the rip screen.</summary>
    public IReadOnlyList<AudioRipFormat> RipFormats { get; } =
        [AudioRipFormat.Wav, AudioRipFormat.Flac, AudioRipFormat.Mp3, AudioRipFormat.Opus, AudioRipFormat.Aac];

    /// <summary>Format the ripped tracks are saved in. WAV needs nothing extra; AAC uses the Windows encoder; FLAC, MP3 and Opus need ffmpeg.</summary>
    public AudioRipFormat SelectedRipFormat
    {
        get => _selectedRipFormat;
        set
        {
            if (SetProperty(ref _selectedRipFormat, value))
            {
                OnPropertyChanged(nameof(RipFormatUsesBitrate));
            }
        }
    }

    /// <summary>Whether the chosen format takes a bitrate (MP3, Opus, AAC).</summary>
    public bool RipFormatUsesBitrate => AudioRipFormats.UsesBitrate(_selectedRipFormat);

    /// <summary>Bitrate in kbit/s for MP3, Opus and AAC; empty uses the default for the format.</summary>
    public string RipBitrateKbps
    {
        get => _ripBitrateKbps;
        set => SetProperty(ref _ripBitrateKbps, value ?? string.Empty);
    }

    /// <summary>
    /// When true, unreadable sectors that fail all retries are zero-filled with silence instead of failing the rip.
    /// </summary>
    public bool ContinueOnUnreadable
    {
        get => _continueOnUnreadable;
        set => SetProperty(ref _continueOnUnreadable, value);
    }

    /// <summary>
    /// Multi-line summary report of the most recent Audio CD extraction and verification operation.
    /// </summary>
    public string RipReport
    {
        get => _ripReport;
        set => SetProperty(ref _ripReport, value);
    }

    #endregion

    #region Commands

    /// <summary>
    /// Adds one or more audio files to the compilation.
    /// </summary>
    public RelayCommand AddAudioFilesCommand { get; }

    /// <summary>
    /// Removes the currently selected track from the compilation.
    /// </summary>
    public RelayCommand RemoveTrackCommand { get; }

    /// <summary>
    /// Removes all tracks from the compilation.
    /// </summary>
    public RelayCommand ClearAllTracksCommand { get; }

    /// <summary>
    /// Moves the selected track up one position in the track sequence.
    /// </summary>
    public RelayCommand MoveTrackUpCommand { get; }

    /// <summary>
    /// Moves the selected track down one position in the track sequence.
    /// </summary>
    public RelayCommand MoveTrackDownCommand { get; }

    /// <summary>
    /// Starts burning the Audio CD compilation to the optical recorder.
    /// </summary>
    public AsyncRelayCommand StartAudioBurnCommand { get; }

    /// <summary>
    /// Cancels any actively executing Audio CD burn operation.
    /// </summary>
    public RelayCommand CancelBurnCommand { get; }

    /// <summary>
    /// Extracts Red Book audio tracks from the inserted CD into WAV files and CUE sheet.
    /// </summary>
    public AsyncRelayCommand RipAudioCdCommand { get; }

    /// <summary>
    /// Measures the optical drive's audio read offset using an inserted CD matched against AccurateRip.
    /// </summary>
    public AsyncRelayCommand DetectReadOffsetCommand { get; }

    /// <summary>
    /// Queries MusicBrainz with 1 click for the inserted Audio CD's table-of-contents DiscID.
    /// </summary>
    public AsyncRelayCommand LookupDiscMetadataCommand { get; }

    /// <summary>
    /// Browses for an output directory to store ripped audio files.
    /// </summary>
    public RelayCommand BrowseRipOutputFolderCommand { get; }

    /// <summary>
    /// Decodes and plays a Red Book audio preview of the currently selected track.
    /// </summary>
    public RelayCommand PreviewSelectedTrackCommand { get; }

    /// <summary>
    /// Stops any active audio track preview playback.
    /// </summary>
    public RelayCommand StopPreviewCommand { get; }

    /// <summary>
    /// Generates and exports a printable CD Jewel Case Front Booklet, Rear Tray Inlay, and Tracklist HTML sheet.
    /// </summary>
    public RelayCommand ExportCoverSheetCommand { get; }

    #endregion

    /// <summary>
    /// Initializes a new instance of <see cref="AudioBurnViewModel"/>.
    /// </summary>
    /// <param name="deviceService">Optional optical device enumeration service.</param>
    /// <param name="burnEngine">Optional optical burning engine backend.</param>
    /// <param name="audioDecoder">Optional audio decoder implementation.</param>
    /// <param name="settingsService">Optional application settings persistence service.</param>
    public AudioBurnViewModel(
        IDiscDeviceService? deviceService = null,
        IBurnEngineBackend? burnEngine = null,
        IAudioDecoder? audioDecoder = null,
        AppSettingsService? settingsService = null)
    {
        _deviceService = deviceService ?? new DiscDeviceManager(enablePhysicalEnumeration: false);
        _burnEngine = burnEngine ?? new VirtualBurnEngine();
        _audioDecoder = audioDecoder ?? new MediaFoundationAudioDecoder();
        _audioBurnService = new AudioCdBurnService(_burnEngine, _audioDecoder);
        _ripperService = new AudioCdRipperService();
        _settingsService = settingsService;

        if (_settingsService != null)
        {
            var s = _settingsService.CurrentSettings;
            _readOffsetSamples = Math.Clamp(s.AudioReadOffsetSamples, -10000, 10000);
            _secureRip = s.SecureAudioRipByDefault;
            _lookupMetadata = s.LookupMusicBrainzByDefault;
            _verifyAccurateRip = s.VerifyAccurateRipByDefault;
        }
        else
        {
            _readOffsetSamples = 0;
            _secureRip = false;
            _lookupMetadata = true;
            _verifyAccurateRip = true;
        }
        _continueOnUnreadable = false;
        _ripReport = string.Empty;

        try
        {
            _ripOutputFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                "CdBurner_RippedAudio");
        }
        catch
        {
            _ripOutputFolder = Path.Combine(Path.GetTempPath(), "CdBurner_RippedAudio");
        }

        AddAudioFilesCommand = new RelayCommand(p =>
        {
            if (p is IEnumerable<string> paths) AddAudioFiles(paths);
            else if (p is string s && !string.IsNullOrWhiteSpace(s)) AddAudioFile(s);
            else BrowseAndAddFiles();
        });

        RemoveTrackCommand = new RelayCommand(p => RemoveTrack(p as AudioTrackRowViewModel), _ => SelectedTrack != null || Tracks.Count > 0);
        ClearAllTracksCommand = new RelayCommand(_ => ClearAllTracks(), _ => !IsBusy && Tracks.Count > 0);
        MoveTrackUpCommand = new RelayCommand(p => MoveTrackUp(p as AudioTrackRowViewModel), _ => CanMoveTrackUp(SelectedTrack));
        MoveTrackDownCommand = new RelayCommand(p => MoveTrackDown(p as AudioTrackRowViewModel), _ => CanMoveTrackDown(SelectedTrack));
        StartAudioBurnCommand = new AsyncRelayCommand(async p => await StartAudioBurnAsync(p as string), _ => !IsBusy && Tracks.Count > 0);
        CancelBurnCommand = new RelayCommand(_ => CancelActiveBurn(), _ => IsBusy);
        RipAudioCdCommand = new AsyncRelayCommand(async p => await RipAudioCdAsync(p as string), _ => !IsBusy);
        DetectReadOffsetCommand = new AsyncRelayCommand(DetectReadOffsetAsync, () => !IsBusy);
        LookupDiscMetadataCommand = new AsyncRelayCommand(async _ => await LookupDiscMetadataAsync(), _ => !IsBusy);
        BrowseRipOutputFolderCommand = new RelayCommand(BrowseRipOutputFolder);
        PreviewSelectedTrackCommand = new RelayCommand(p => PreviewTrack(p as AudioTrackRowViewModel), _ => SelectedTrack != null || Tracks.Count > 0);
        StopPreviewCommand = new RelayCommand(_ => StopPreview(), _ => IsPreviewPlaying);
        ExportCoverSheetCommand = new RelayCommand(p => ExportJewelCaseCoverHtml(p as string), _ => Tracks.Count > 0);

        foreach (var r in _deviceService.GetRecorders())
        {
            AvailableRecorders.Add(r);
        }

        // Pre-select physical optical drive if available, otherwise fallback
        SelectedRecorder = AvailableRecorders.FirstOrDefault(r => !r.IsVirtual)
                           ?? AvailableRecorders.FirstOrDefault(r => string.Equals(r.UniqueId, "VDRV-01", StringComparison.OrdinalIgnoreCase))
                           ?? AvailableRecorders.FirstOrDefault();

        RecalculateTotals();
    }

    #region Track Management Operations

    /// <summary>
    /// Adds a single physical audio file to the compilation.
    /// </summary>
    public void AddAudioFile(string filePath)
    {
        AddAudioFiles(new[] { filePath });
    }

    private static readonly HashSet<string> SupportedAudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".fla", ".wav", ".mp3", ".m4a", ".aac", ".ogg", ".wma", ".m4p", ".raw", ".pcm"
    };

    private static List<string> ExpandAudioPaths(IEnumerable<string> rawPaths)
    {
        var result = new List<string>();
        foreach (var raw in rawPaths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            if (File.Exists(raw))
            {
                result.Add(raw);
            }
            else if (Directory.Exists(raw))
            {
                try
                {
                    var dirFiles = Directory.EnumerateFiles(raw, "*.*", SearchOption.AllDirectories)
                        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    var audioInDir = dirFiles.Where(f => SupportedAudioExtensions.Contains(Path.GetExtension(f))).ToList();
                    var cuesInDir = dirFiles.Where(f => string.Equals(Path.GetExtension(f), ".cue", StringComparison.OrdinalIgnoreCase)).ToList();

                    // If a folder is a single-file album rip (1 audio file + 1 .cue sheet), import via the .cue sheet so tracks are split by INDEX 01
                    if (audioInDir.Count == 1 && cuesInDir.Count == 1)
                    {
                        result.Add(cuesInDir[0]);
                    }
                    else if (audioInDir.Count > 0)
                    {
                        result.AddRange(audioInDir);
                    }
                    else if (cuesInDir.Count > 0)
                    {
                        result.AddRange(cuesInDir);
                    }
                }
                catch
                {
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Adds multiple physical audio files (or folders / CUE sheets) to the compilation, skipping and reporting any DRM-protected or unreadable files.
    /// </summary>
    public void AddAudioFiles(IEnumerable<string> filePaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        // One shared context for the whole drop, including files pulled in through playlists, so that tracks are
        // re-indexed once, every skipped file lands in a single report, and the final status counts everything.
        var ctx = new AddFilesContext();
        AddAudioFilesCore(filePaths, ctx, depth: 0);
        ReportAddResults(ctx);
    }

    private sealed class AddFilesContext
    {
        public List<string> DrmProtectedFiles { get; } = new();
        public List<(string FilePath, string Reason)> UnreadableFiles { get; } = new();
        public HashSet<string> VisitedPlaylists { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> MixedModeNotices { get; } = new();
        public int AddedCount { get; set; }
        public bool TrackLimitReached { get; set; }
    }

    private const int MaxPlaylistNesting = 4;

    private void AddAudioFilesCore(IEnumerable<string> filePaths, AddFilesContext ctx, int depth)
    {
        var pathList = ExpandAudioPaths(filePaths);
        if (pathList.Count == 0) return;

        var drmProtectedFiles = ctx.DrmProtectedFiles;
        var unreadableFiles = ctx.UnreadableFiles;

        foreach (var path in pathList)
        {
            if (Tracks.Count >= AudioCdCompilation.MaxTracks)
            {
                ctx.TrackLimitReached = true;
                break;
            }

            // Support M3U / M3U8 / PLS Playlists (.m3u, .m3u8, .pls)
            if (AudioPlaylistParser.IsPlaylistFile(path))
            {
                // Guard against playlists that include themselves (directly or via another playlist).
                string fullPlaylistPath;
                try { fullPlaylistPath = Path.GetFullPath(path); } catch { fullPlaylistPath = path; }
                if (depth >= MaxPlaylistNesting || !ctx.VisitedPlaylists.Add(fullPlaylistPath))
                {
                    unreadableFiles.Add((path, "Playlist is nested too deeply or includes itself; skipped."));
                    continue;
                }

                try
                {
                    var playlistEntries = AudioPlaylistParser.ParseFile(path);
                    if (playlistEntries.Count == 0)
                    {
                        unreadableFiles.Add((path, "Playlist contains no valid local audio tracks."));
                        continue;
                    }

                    foreach (var plEntry in playlistEntries)
                    {
                        if (Tracks.Count >= AudioCdCompilation.MaxTracks) break;
                        if (!File.Exists(plEntry.FilePath))
                        {
                            unreadableFiles.Add((plEntry.FilePath, $"Referenced in playlist '{Path.GetFileName(path)}' but file not found."));
                            continue;
                        }

                        if (!string.IsNullOrWhiteSpace(plEntry.Album) &&
                            (string.IsNullOrWhiteSpace(AlbumTitle) || AlbumTitle == "Untitled Album"))
                        {
                            AlbumTitle = plEntry.Album;
                        }

                        if (!string.IsNullOrWhiteSpace(plEntry.Performer) &&
                            (string.IsNullOrWhiteSpace(AlbumPerformer) || AlbumPerformer == "Various Artists"))
                        {
                            AlbumPerformer = plEntry.Performer;
                        }

                        int countBefore = Tracks.Count;
                        AddAudioFilesCore(new[] { plEntry.FilePath }, ctx, depth + 1); // counts & reports via ctx
                        if (Tracks.Count == countBefore + 1)
                        {
                            var lastRow = Tracks[^1];
                            if (!string.IsNullOrWhiteSpace(plEntry.Title) &&
                                (string.IsNullOrWhiteSpace(lastRow.Title) || lastRow.Title == Path.GetFileNameWithoutExtension(plEntry.FilePath)))
                            {
                                lastRow.Title = plEntry.Title;
                            }
                            if (!string.IsNullOrWhiteSpace(plEntry.Performer) &&
                                (string.IsNullOrWhiteSpace(lastRow.Performer) || lastRow.Performer == "Various Artists"))
                            {
                                lastRow.Performer = plEntry.Performer;
                            }
                        }
                    }
                }
                catch (Exception plEx)
                {
                    unreadableFiles.Add((path, plEx.Message));
                }
                continue;
            }

            // Support CUE sheets (.cue) referencing .flac, .wav, .mp3, or single-file album images
            if (string.Equals(Path.GetExtension(path), ".cue", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var cueSheet = CueSheetParser.ParseFile(path);
                    bool isMixed = cueSheet.IsMixedMode;
                    int audioTrackCount = cueSheet.Tracks.Count(t => t.IsAudio);
                    int dataTrackCount = cueSheet.Tracks.Count(t => !t.IsAudio);

                    var tempComp = new AudioCdCompilation
                    {
                        AlbumTitle = AlbumTitle,
                        AlbumPerformer = AlbumPerformer,
                        Songwriter = AlbumSongwriter,
                        UpcEan = UpcEanCode,
                        DefaultPreGapFrames = DefaultPreGapFrames
                    };

                    int cueAdded = tempComp.ImportCueSheet(cueSheet, Path.GetDirectoryName(path) ?? string.Empty, _audioDecoder);
                    if (cueAdded == 0)
                    {
                        unreadableFiles.Add((path, "CUE sheet contains no Red Book AUDIO tracks."));
                        continue;
                    }

                    if (isMixed)
                    {
                        bool keptData = tempComp.HasDataTrack && Tracks.Count == 0;
                        int skipped = dataTrackCount - (keptData ? 1 : 0);
                        ctx.MixedModeNotices.Add(keptData
                            ? $"Track 1 of this mixed-mode CUE is a data track: it is kept, so the disc is a mixed-mode CD, which only direct drive commands can write."
                              + (skipped > 0 ? $" {skipped} other data track(s) were left out." : string.Empty)
                            : $"Imported {audioTrackCount} audio track(s) from mixed-mode CUE (skipped {dataTrackCount} data track(s)).");
                    }

                    if ((string.IsNullOrWhiteSpace(AlbumTitle) || AlbumTitle == "Untitled Album") &&
                        !string.IsNullOrWhiteSpace(tempComp.AlbumTitle) && tempComp.AlbumTitle != "Untitled Album")
                    {
                        AlbumTitle = tempComp.AlbumTitle;
                    }

                    if ((string.IsNullOrWhiteSpace(AlbumPerformer) || AlbumPerformer == "Various Artists") &&
                        !string.IsNullOrWhiteSpace(tempComp.AlbumPerformer) && tempComp.AlbumPerformer != "Various Artists")
                    {
                        AlbumPerformer = tempComp.AlbumPerformer;
                    }

                    if (string.IsNullOrWhiteSpace(AlbumSongwriter) && !string.IsNullOrWhiteSpace(tempComp.Songwriter))
                    {
                        AlbumSongwriter = tempComp.Songwriter;
                    }

                    if (string.IsNullOrWhiteSpace(UpcEanCode) && !string.IsNullOrWhiteSpace(tempComp.UpcEan))
                    {
                        UpcEanCode = tempComp.UpcEan;
                    }

                    AudioTrackRowViewModel? previousCueRow = null;
                    foreach (var cueTrk in tempComp.Tracks)
                    {
                        if (Tracks.Count >= AudioCdCompilation.MaxTracks) break;
                        if (cueTrk.IsDataTrack && Tracks.Count > 0) continue; // a data track can only be track 1

                        long trkSize = 0;
                        string trkFormat = Path.GetExtension(cueTrk.SourceFilePath).TrimStart('.').ToUpperInvariant();
                        int trkRate = OpticalConstants.CdAudioSampleRate;
                        int trkBps = OpticalConstants.CdAudioBitsPerSample;
                        int trkCh = OpticalConstants.CdAudioChannels;

                        if (cueTrk.DataTrack is { } dataImage)
                        {
                            trkSize = dataImage.UserDataBytes;
                            trkFormat = "Data (Mode 1)";
                        }
                        else if (File.Exists(cueTrk.SourceFilePath))
                        {
                            try
                            {
                                trkSize = new FileInfo(cueTrk.SourceFilePath).Length;
                                var m = _audioDecoder.ProbeAudioFile(cueTrk.SourceFilePath);
                                if (!string.IsNullOrWhiteSpace(m.FormatName)) trkFormat = m.FormatName;
                                if (m.SampleRate > 0) trkRate = m.SampleRate;
                                if (m.BitsPerSample > 0) trkBps = m.BitsPerSample;
                                if (m.Channels > 0) trkCh = m.Channels;
                            }
                            catch
                            {
                            }
                        }

                        int nextNum = Tracks.Count + 1;
                        var cueRow = new AudioTrackRowViewModel
                        {
                            TrackNumber = nextNum,
                            SourceFilePath = cueTrk.SourceFilePath,
                            Title = cueTrk.Title,
                            Performer = cueTrk.Performer,
                            Songwriter = cueTrk.Songwriter ?? AlbumSongwriter,
                            Composer = cueTrk.Composer,
                            Isrc = cueTrk.Isrc,
                            PreGapFrames = nextNum == 1 && cueTrk.PreGapFrames == 0 ? DefaultPreGapFrames : cueTrk.PreGapFrames,
                            DurationFrames = cueTrk.DurationFrames,
                            FileSizeBytes = trkSize,
                            FormatName = trkFormat,
                            SampleRate = trkRate,
                            BitsPerSample = trkBps,
                            Channels = trkCh,
                            CustomPcmStreamFactory = cueTrk.CustomPcmStreamFactory,
                            DataTrack = cueTrk.DataTrack,
                            RawFileType = cueTrk.RawFileType,
                            PreEmphasis = cueTrk.PreEmphasis,
                            CopyPermitted = cueTrk.CopyPermitted,
                            Index00LeadFrames = cueTrk.Index00LeadFrames,
                            Index00Predecessor = cueTrk.Index00LeadFrames > 0 ? previousCueRow : null,
                            HiddenAudioPcmStreamFactory = cueTrk.HiddenAudioPcmStreamFactory,
                            HiddenAudioFrames = cueTrk.HiddenAudioFrames,
                            AlbumSource = cueTrk.AlbumSource,
                            SourceStartFrame = cueTrk.SourceStartFrame,
                            SourceFrameCount = cueTrk.SourceFrameCount
                        };

                        Tracks.Add(cueRow);
                        previousCueRow = cueRow;
                        SelectedTrack = cueRow;
                        ctx.AddedCount++;
                    }
                }
                catch (Exception cueEx)
                {
                    unreadableFiles.Add((path, cueEx.Message));
                }
                continue;
            }

            // 1. Explicit DRM check (.m4p, FairPlay atoms, ASF DRM)
            if (AudioDrmDetector.IsDrmProtected(path, out string drmDescription))
            {
                drmProtectedFiles.Add(path);
                continue;
            }

            long fileSize = 0;
            try
            {
                fileSize = new FileInfo(path).Length;
            }
            catch { }

            string fileName = Path.GetFileNameWithoutExtension(path);
            string title = fileName;
            string performer = AlbumPerformer;
            string? songwriter = AlbumSongwriter;
            string? composer = null;
            string? isrc = null;
            string formatName = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
            int sampleRate = OpticalConstants.CdAudioSampleRate;
            int bitsPerSample = OpticalConstants.CdAudioBitsPerSample;
            int channels = OpticalConstants.CdAudioChannels;
            int durationFrames = 0;

            try
            {
                var meta = _audioDecoder.ProbeAudioFile(path);
                if (!string.IsNullOrWhiteSpace(meta.Title)) title = meta.Title;
                if (!string.IsNullOrWhiteSpace(meta.Artist)) performer = meta.Artist;
                if (!string.IsNullOrWhiteSpace(meta.Songwriter)) songwriter = meta.Songwriter;
                if (!string.IsNullOrWhiteSpace(meta.Composer)) composer = meta.Composer;
                if (!string.IsNullOrWhiteSpace(meta.Isrc)) isrc = meta.Isrc;
                if (!string.IsNullOrWhiteSpace(meta.FormatName)) formatName = meta.FormatName;
                if (meta.SampleRate > 0) sampleRate = meta.SampleRate;
                if (meta.BitsPerSample > 0) bitsPerSample = meta.BitsPerSample;
                if (meta.Channels > 0) channels = meta.Channels;

                if ((string.IsNullOrWhiteSpace(AlbumTitle) || AlbumTitle == "Untitled Album") &&
                    !string.IsNullOrWhiteSpace(meta.Album))
                {
                    AlbumTitle = meta.Album;
                }

                string? albumArtistCandidate = !string.IsNullOrWhiteSpace(meta.AlbumArtist) ? meta.AlbumArtist : meta.Artist;
                if ((string.IsNullOrWhiteSpace(AlbumPerformer) || AlbumPerformer == "Various Artists") &&
                    !string.IsNullOrWhiteSpace(albumArtistCandidate))
                {
                    AlbumPerformer = albumArtistCandidate;
                }

                if (meta.TotalRedBookBytes > 0)
                {
                    durationFrames = OpticalConstants.BytesToSectors(meta.TotalRedBookBytes, OpticalConstants.RawSectorSize);
                }
                else if (meta.Duration > TimeSpan.Zero)
                {
                    durationFrames = (int)Math.Round(meta.Duration.TotalSeconds * OpticalConstants.AudioFramesPerSecond);
                }
                else
                {
                    throw new InvalidDataException("Audio probe reported zero duration.");
                }
            }
            catch (DrmProtectedAudioException)
            {
                drmProtectedFiles.Add(path);
                continue;
            }
            catch (Exception ex)
            {
                string ext = Path.GetExtension(path);
                // Allow raw PCM fallback strictly for headerless .raw / .pcm files
                if ((string.Equals(ext, ".raw", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(ext, ".pcm", StringComparison.OrdinalIgnoreCase)) && fileSize > 0)
                {
                    durationFrames = OpticalConstants.BytesToSectors(fileSize, OpticalConstants.RawSectorSize);
                }
                else
                {
                    unreadableFiles.Add((path, ex.Message));
                    continue;
                }
            }

            int trackNum = Tracks.Count + 1;
            var row = new AudioTrackRowViewModel
            {
                TrackNumber = trackNum,
                SourceFilePath = path,
                Title = title,
                Performer = performer,
                Songwriter = songwriter,
                Composer = composer,
                Isrc = isrc,
                PreGapFrames = trackNum == 1 ? DefaultPreGapFrames : 0,
                DurationFrames = durationFrames,
                FileSizeBytes = fileSize,
                FormatName = formatName,
                SampleRate = sampleRate,
                BitsPerSample = bitsPerSample,
                Channels = channels
            };

            Tracks.Add(row);
            SelectedTrack = row;
            ctx.AddedCount++;
        }
    }

    /// <summary>
    /// Re-indexes once and publishes a single status line / dialog for everything a drop added or skipped.
    /// </summary>
    private void ReportAddResults(AddFilesContext ctx)
    {
        var drmProtectedFiles = ctx.DrmProtectedFiles;
        var unreadableFiles = ctx.UnreadableFiles;
        int addedCount = ctx.AddedCount;

        if (addedCount > 0)
        {
            ReindexTracks();
        }

        // The status line always reports what was added AND what was skipped, so a playlist with
        // 10 good tracks and 1 missing file reads "Added 10 ... Skipped 1 ..." rather than only the failure.
        var parts = new List<string>();
        if (ctx.MixedModeNotices.Count > 0)
        {
            parts.AddRange(ctx.MixedModeNotices);
        }
        else if (addedCount > 0)
        {
            parts.Add($"Added {addedCount} audio track(s). Total: {Tracks.Count}.");
        }
        if (addedCount > 0 && ctx.MixedModeNotices.Count > 0)
        {
            parts.Add($"Total: {Tracks.Count}.");
        }
        if (drmProtectedFiles.Count > 0) parts.Add($"Skipped {drmProtectedFiles.Count} DRM-protected Apple Music file(s). Use DRM-free tracks.");
        if (unreadableFiles.Count > 0) parts.Add($"Skipped {unreadableFiles.Count} unsupported, missing or corrupt audio file(s).");
        if (ctx.TrackLimitReached) parts.Add($"Audio CD compilation has reached the Red Book limit of {AudioCdCompilation.MaxTracks} tracks.");
        if (parts.Count > 0)
        {
            StatusMessage = string.Join(" ", parts);
        }

        // Inform user about skipped DRM or corrupted files
        if (drmProtectedFiles.Count > 0)
        {
            string fileList = string.Join("\n", drmProtectedFiles.Take(6).Select(p => $"  • {Path.GetFileName(p)}"));
            if (drmProtectedFiles.Count > 6)
            {
                fileList += $"\n  ...and {drmProtectedFiles.Count - 6} more file(s)";
            }

            string warning = $"The following {drmProtectedFiles.Count} file(s) are encrypted with Apple FairPlay DRM (.m4p) and cannot be added or burned to an Audio CD:\n\n" +
                             $"{fileList}\n\n" +
                             "Why: Apple Music subscription downloads are encrypted by Apple to prevent copying to physical media.\n\n" +
                             "To burn an Audio CD, please use DRM-free audio formats such as WAV, MP3, FLAC, M4A, OGG, or WMA (including DRM-free purchases from iTunes Store).";

            if (CdBurner.App.Views.BurnTargetDialog.IsInteractiveSession)
            {
                System.Windows.MessageBox.Show(
                    warning,
                    "DRM-Protected Audio Files Skipped — IgnisDisc",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            }
        }

        if (unreadableFiles.Count > 0)
        {
            string failList = string.Join("\n", unreadableFiles.Take(6).Select(f => $"  • {Path.GetFileName(f.FilePath)}: {f.Reason}"));
            if (unreadableFiles.Count > 6)
            {
                failList += $"\n  ...and {unreadableFiles.Count - 6} more file(s)";
            }

            string addedNote = addedCount > 0 ? $"{addedCount} track(s) were added. " : string.Empty;
            string errorMsg = $"{addedNote}The following {unreadableFiles.Count} file(s) could not be read or are unsupported:\n\n{failList}";

            if (CdBurner.App.Views.BurnTargetDialog.IsInteractiveSession)
            {
                System.Windows.MessageBox.Show(
                    errorMsg,
                    "Unsupported Audio Files — IgnisDisc",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }
    }

    /// <summary>
    /// Removes a track from the compilation and re-indexes remaining tracks sequentially.
    /// </summary>
    public void RemoveTrack(AudioTrackRowViewModel? track = null)
    {
        var target = track ?? SelectedTrack;
        if (target != null && Tracks.Remove(target))
        {
            ReindexTracks();
            SelectedTrack = Tracks.FirstOrDefault();
        }
    }

    /// <summary>
    /// Removes all audio tracks from the compilation to allow burning consecutive CDs rapidly.
    /// </summary>
    public void ClearAllTracks()
    {
        if (Tracks.Count == 0) return;

        Tracks.Clear();
        SelectedTrack = null;
        RecalculateTotals();
        MoveTrackUpCommand.RaiseCanExecuteChanged();
        MoveTrackDownCommand.RaiseCanExecuteChanged();
        RemoveTrackCommand.RaiseCanExecuteChanged();
        ClearAllTracksCommand.RaiseCanExecuteChanged();
        StartAudioBurnCommand.RaiseCanExecuteChanged();
        StatusMessage = "Cleared all tracks from compilation.";
    }

    /// <summary>
    /// Moves the specified track up one position in the track order.
    /// </summary>
    public void MoveTrackUp(AudioTrackRowViewModel? track = null)
    {
        var target = track ?? SelectedTrack;
        if (target == null) return;

        int index = Tracks.IndexOf(target);
        if (index > 0)
        {
            Tracks.Move(index, index - 1);
            ReindexTracks();
            SelectedTrack = target;
        }
    }

    /// <summary>
    /// Moves the specified track down one position in the track order.
    /// </summary>
    public void MoveTrackDown(AudioTrackRowViewModel? track = null)
    {
        var target = track ?? SelectedTrack;
        if (target == null) return;

        int index = Tracks.IndexOf(target);
        if (index >= 0 && index < Tracks.Count - 1)
        {
            Tracks.Move(index, index + 1);
            ReindexTracks();
            SelectedTrack = target;
        }
    }

    private bool CanMoveTrackUp(AudioTrackRowViewModel? track = null)
    {
        var target = track ?? SelectedTrack;
        if (target == null) return false;
        int index = Tracks.IndexOf(target);
        return index > 0;
    }

    private bool CanMoveTrackDown(AudioTrackRowViewModel? track = null)
    {
        var target = track ?? SelectedTrack;
        if (target == null) return false;
        int index = Tracks.IndexOf(target);
        return index >= 0 && index < Tracks.Count - 1;
    }

    /// <summary>
    /// Re-indexes all tracks sequentially from 1 to Count and recalculates compilation metrics.
    /// </summary>
    public void ReindexTracks()
    {
        for (int i = 0; i < Tracks.Count; i++)
        {
            Tracks[i].TrackNumber = i + 1;
        }

        if (Tracks.Count > 0 && Tracks[0].PreGapFrames == 0)
        {
            Tracks[0].PreGapFrames = DefaultPreGapFrames;
        }

        RecalculateTotals();
        MoveTrackUpCommand.RaiseCanExecuteChanged();
        MoveTrackDownCommand.RaiseCanExecuteChanged();
        RemoveTrackCommand.RaiseCanExecuteChanged();
        ClearAllTracksCommand.RaiseCanExecuteChanged();
        StartAudioBurnCommand.RaiseCanExecuteChanged();
        PreviewSelectedTrackCommand?.RaiseCanExecuteChanged();
        ExportCoverSheetCommand?.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Recalculates total duration and disc capacity usage percentage.
    /// </summary>
    public void RecalculateTotals()
    {
        int totalFrames = Tracks.Sum(t => t.DurationFrames);
        TotalDuration = MsfAddress.FromFrameCount(totalFrames);

        int maxFrames = OpticalConstants.Cd700MbSectors; // 360,000 frames (80-min CD-R)
        var mediaStatus = _selectedRecorder?.QueryMediaStatus();
        if (mediaStatus != null && mediaStatus.TotalSectors > 0)
        {
            maxFrames = mediaStatus.TotalSectors;
        }

        DiscUsagePercent = maxFrames > 0 ? ((double)totalFrames / maxFrames * 100.0) : 0.0;

        OnPropertyChanged(nameof(TotalDurationText));
        OnPropertyChanged(nameof(DiscUsageText));
        OnPropertyChanged(nameof(TotalTracksText));
        OnPropertyChanged(nameof(HasTracks));
    }

    private void BrowseAndAddFiles()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Add Audio Tracks, Playlists, or CUE Sheets to Audio CD Compilation",
            Filter = "Supported Audio, Playlists & CUE (*.flac;*.wav;*.mp3;*.m4a;*.aac;*.ogg;*.wma;*.cue;*.m3u;*.m3u8;*.pls)|*.flac;*.fla;*.wav;*.mp3;*.m4a;*.aac;*.ogg;*.wma;*.cue;*.m3u;*.m3u8;*.pls|FLAC Lossless Audio (*.flac;*.fla)|*.flac;*.fla|WAV Audio (*.wav)|*.wav|MP3 Audio (*.mp3)|*.mp3|Playlists (*.m3u;*.m3u8;*.pls)|*.m3u;*.m3u8;*.pls|CUE Sheet (*.cue)|*.cue|All Files (*.*)|*.*",
            Multiselect = true
        };

        if (dlg.ShowDialog() == true && dlg.FileNames.Length > 0)
        {
            AddAudioFiles(dlg.FileNames);
        }
    }

    /// <summary>
    /// Decodes and plays a Red Book audio preview of the specified or currently selected track.
    /// </summary>
    public void PreviewTrack(AudioTrackRowViewModel? track = null) => _ = PreviewTrackAsync(track);

    /// <summary>
    /// Decodes the first 30 seconds of a track on a background thread (a single-file album or a long hi-res FLAC can take
    /// seconds to decode, which used to freeze the window), then plays it. <see cref="IsPreviewPlaying"/> returns to false
    /// when the clip finishes on its own, when <see cref="StopPreview"/> is called, or when another preview starts.
    /// </summary>
    public async Task PreviewTrackAsync(AudioTrackRowViewModel? track = null)
    {
        var target = track ?? SelectedTrack ?? Tracks.FirstOrDefault();
        if (target == null)
        {
            PreviewStatusText = "No track selected to preview.";
            return;
        }

        StopPreview();

        if (target.IsDataTrack)
        {
            PreviewStatusText = $"Track {target.TrackNumber} is a data track: there is no audio to preview.";
            return;
        }

        bool hasSource = target.CustomPcmStreamFactory != null ||
                         (!string.IsNullOrWhiteSpace(target.SourceFilePath) && File.Exists(target.SourceFilePath));
        if (!hasSource)
        {
            PreviewStatusText = $"Cannot preview Track {target.TrackNumber}: source file not found.";
            return;
        }

        var cts = new CancellationTokenSource();
        _previewCts = cts;
        PreviewStatusText = $"Preparing preview of Track {target.TrackNumber:D2}…";

        bool leveling = UseRmsLoudnessLeveling;
        bool normalize = NormalizeAllTracks || target.NormalizeAudio;

        try
        {
            byte[] pcmBytes = await Task.Run(() => DecodePreviewPcm(target, leveling, normalize, cts.Token), cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(_previewCts, cts))
            {
                return; // superseded by StopPreview or a newer preview
            }

            _previewWavStream = new MemoryStream(44 + pcmBytes.Length);
            WavFileCodec.WriteWavHeader(_previewWavStream, pcmBytes.Length);
            _previewWavStream.Write(pcmBytes, 0, pcmBytes.Length);
            _previewWavStream.Position = 0;

            if (CdBurner.App.Views.BurnTargetDialog.IsInteractiveSession)
            {
                _soundPlayer = new System.Media.SoundPlayer(_previewWavStream);
                _soundPlayer.Play();
            }

            IsPreviewPlaying = true;
            PreviewStatusText = $"🔊 Playing Track {target.TrackNumber:D2}: {target.Title} ({target.FormatSummary})";
            StatusMessage = PreviewStatusText;

            double clipSeconds = (double)pcmBytes.Length / (OpticalConstants.CdAudioSampleRate * OpticalConstants.CdAudioBytesPerSample);
            _ = ResetPreviewWhenClipEndsAsync(TimeSpan.FromSeconds(clipSeconds), cts);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_previewCts, cts))
            {
                IsPreviewPlaying = false;
                PreviewStatusText = $"Preview failed: {ex.Message}";
                StatusMessage = PreviewStatusText;
            }
        }
    }

    private byte[] DecodePreviewPcm(AudioTrackRowViewModel target, bool leveling, bool normalize, CancellationToken ct)
    {
        Stream pcmStream = target.CustomPcmStreamFactory != null
            ? target.CustomPcmStreamFactory()
            : _audioDecoder.OpenRedBookPcmStream(target.SourceFilePath);

        using (pcmStream)
        {
            ct.ThrowIfCancellationRequested();

            // Cap preview buffer to first 30 seconds (5.29 MB) for snappy playback
            const int maxPreviewBytes = 30 * OpticalConstants.CdAudioSampleRate * OpticalConstants.CdAudioBytesPerSample;
            using var cappedPcm = new MemoryStream();
            byte[] buf = new byte[16384];
            int remaining = maxPreviewBytes;
            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                int r = pcmStream.Read(buf, 0, Math.Min(buf.Length, remaining));
                if (r <= 0) break;
                cappedPcm.Write(buf, 0, r);
                remaining -= r;
            }

            byte[] pcmBytes = cappedPcm.ToArray();
            if (leveling)
            {
                pcmBytes = RedBookPcmDspProcessor.NormalizeRmsLoudness(pcmBytes);
            }
            else if (normalize)
            {
                pcmBytes = RedBookPcmDspProcessor.NormalizePcm(pcmBytes);
            }

            return pcmBytes;
        }
    }

    private async Task ResetPreviewWhenClipEndsAsync(TimeSpan clipLength, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(clipLength + TimeSpan.FromMilliseconds(250), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ReferenceEquals(_previewCts, cts) && IsPreviewPlaying)
        {
            ReleasePreviewPlayer();
            IsPreviewPlaying = false;
            PreviewStatusText = "Preview finished.";
        }
    }

    /// <summary>
    /// Stops any active audio track preview playback.
    /// </summary>
    public void StopPreview()
    {
        try
        {
            _previewCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        _previewCts = null;

        ReleasePreviewPlayer();

        if (IsPreviewPlaying)
        {
            IsPreviewPlaying = false;
            PreviewStatusText = "Preview stopped.";
            StatusMessage = "Audio preview stopped.";
        }
    }

    private void ReleasePreviewPlayer()
    {
        try
        {
            _soundPlayer?.Stop();
            _soundPlayer?.Dispose();
        }
        catch
        {
        }
        finally
        {
            _soundPlayer = null;
        }

        try
        {
            _previewWavStream?.Dispose();
        }
        catch
        {
        }
        finally
        {
            _previewWavStream = null;
        }
    }

    /// <summary>
    /// Builds an <see cref="AudioCdCompilation"/> snapshot from the current tracks and CD-Text settings.
    /// </summary>
    public AudioCdCompilation BuildCurrentCompilation()
    {
        var compilation = new AudioCdCompilation
        {
            AlbumTitle = AlbumTitle,
            AlbumPerformer = AlbumPerformer,
            Songwriter = AlbumSongwriter,
            UpcEan = UpcEanCode,
            RecordingMode = RecordingMode,
            NormalizeAllTracks = NormalizeAllTracks,
            UseRmsLoudnessLeveling = UseRmsLoudnessLeveling,
            CrossfadeSeconds = CrossfadeSeconds,
            DefaultPreGapFrames = DefaultPreGapFrames,
            IncludeCdText = IncludeCdText
        };

        for (int i = 0; i < Tracks.Count; i++)
        {
            var row = Tracks[i];
            compilation.AddTrack(new AudioCdTrackItem(
                TrackNumber: row.TrackNumber,
                SourceFilePath: row.SourceFilePath,
                Title: row.Title,
                Performer: !string.IsNullOrEmpty(row.Performer) ? row.Performer : AlbumPerformer,
                Isrc: row.Isrc,
                PreGapFrames: row.PreGapFrames,
                NormalizeAudio: row.NormalizeAudio,
                CustomPcmStreamFactory: row.CustomPcmStreamFactory
            )
            {
                Songwriter = row.Songwriter ?? AlbumSongwriter,
                Composer = row.Composer,
                DurationFrames = row.DurationFrames,
                SourceStartFrame = row.SourceStartFrame,
                SourceFrameCount = row.SourceFrameCount,
                DataTrack = row.DataTrack,
                RawFileType = row.RawFileType,
                PreEmphasis = row.PreEmphasis,
                CopyPermitted = row.CopyPermitted,
                // A gap mark stays only while the track it came after is still directly in front of it.
                Index00LeadFrames = i > 0 && row.Index00Predecessor != null && ReferenceEquals(Tracks[i - 1], row.Index00Predecessor)
                    ? row.Index00LeadFrames : 0,
                HiddenAudioPcmStreamFactory = row.HiddenAudioPcmStreamFactory,
                HiddenAudioFrames = row.HiddenAudioFrames,
                AlbumSource = row.AlbumSource
            });
        }

        return compilation;
    }

    /// <summary>
    /// Generates and saves a printable CD Jewel Case Booklet, Rear Tray Inlay, and Tracklist HTML file.
    /// </summary>
    public string? ExportJewelCaseCoverHtml(string? outputHtmlPath = null)
    {
        if (Tracks.Count == 0)
        {
            StatusMessage = "Add at least one track before exporting a printable CD cover.";
            return null;
        }

        if (string.IsNullOrWhiteSpace(outputHtmlPath))
        {
            if (CdBurner.App.Views.BurnTargetDialog.IsInteractiveSession)
            {
                var sfd = new SaveFileDialog
                {
                    Title = "Export Printable CD Jewel Case Cover & Tracklist",
                    Filter = "Printable HTML Cover & Tracklist (*.html)|*.html",
                    FileName = $"{SanitizeFilePart(AlbumTitle)}_JewelCaseCover.html"
                };
                if (sfd.ShowDialog() != true || string.IsNullOrWhiteSpace(sfd.FileName))
                {
                    return null;
                }
                outputHtmlPath = sfd.FileName;
            }
            else
            {
                outputHtmlPath = Path.Combine(Path.GetTempPath(), $"{SanitizeFilePart(AlbumTitle)}_JewelCaseCover.html");
            }
        }

        var compilation = BuildCurrentCompilation();
        JewelCaseCoverGenerator.SavePrintableCoverHtml(compilation, outputHtmlPath);
        LastExportedCoverHtmlPath = outputHtmlPath;
        StatusMessage = $"Exported printable CD jewel case cover & tracklist to '{Path.GetFileName(outputHtmlPath)}'.";
        return outputHtmlPath;
    }

    private static string SanitizeFilePart(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "AudioCD";
        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(input.Length);
        foreach (char c in input)
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }
        return sb.ToString().Trim();
    }

    private void BrowseRipOutputFolder()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Select Destination Folder for Ripped Audio Tracks",
            InitialDirectory = Directory.Exists(RipOutputFolder) ? RipOutputFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)
        };

        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
        {
            RipOutputFolder = dlg.FolderName;
        }
    }

    #endregion

    #region Burning & Ripping Operations

    /// <summary>
    /// Starts burning the Audio CD compilation to the specified or selected optical recorder.
    /// </summary>
    public async Task<BurnOperationResult> StartAudioBurnAsync(string? driveId = null, CancellationToken ct = default)
    {
        if (Tracks.Count == 0)
        {
            StatusMessage = "No audio tracks in compilation to burn.";
            var fail = BurnOperationResult.Failed(StatusMessage);
            LastBurnResult = fail;
            return fail;
        }

        if (string.IsNullOrWhiteSpace(driveId) && CdBurner.App.Views.BurnTargetDialog.IsInteractiveSession)
        {
            var confirmed = CdBurner.App.Views.BurnTargetDialog.ConfirmRecorderSelection(
                _deviceService,
                AvailableRecorders,
                SelectedRecorder,
                $"Burn Audio CD ({Tracks.Count} Tracks)");

            if (confirmed == null)
            {
                StatusMessage = "Audio CD burn cancelled by user.";
                var cancelled = BurnOperationResult.CancelledResult(StatusMessage);
                LastBurnResult = cancelled;
                return cancelled;
            }

            SelectedRecorder = confirmed;
        }

        var recorder = ResolveRecorder(driveId);
        if (recorder == null)
        {
            StatusMessage = "No optical recorder selected or available.";
            var fail = BurnOperationResult.Failed(StatusMessage);
            LastBurnResult = fail;
            return fail;
        }

        // Auto-insert blank CD-R into virtual drive if empty tray
        if (recorder is VirtualDiscRecorder vRec)
        {
            var mediaStatus = vRec.QueryMediaStatus();
            if (!mediaStatus.MediaPresent || (!mediaStatus.IsBlank && !mediaStatus.IsAppendable && !mediaStatus.IsRewritable))
            {
                vRec.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());
            }
        }

        _activeBurnCts?.Dispose();
        _activeBurnCts = ct.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : new CancellationTokenSource();
        var activeToken = _activeBurnCts.Token;

        IsBusy = true;
        CancelBurnCommand.RaiseCanExecuteChanged();
        BurnProgress = 0;
        StatusMessage = $"Preparing Audio CD compilation for {recorder.UniqueId}...";

        try
        {
            StopPreview();
            var compilation = BuildCurrentCompilation();

            var progress = new Progress<BurnProgressSnapshot>(p =>
            {
                if (!_isBusy) return; // Prevent stale background progress from overwriting final status
                if (p.TotalLba > 0)
                {
                    BurnProgress = Math.Min(100.0, (double)p.CurrentLba / p.TotalLba * 100.0);
                }
                StatusMessage = $"{p.CurrentActionDescription} ({BurnProgress:F0}%)";
            });

            // Direct drive commands are opt-in per burn and only for drives whose probe shows Session-at-Once.
            bool direct = UseDirectDriveCommands;
            CdBurner.Hardware.NativeWriting.NativeAudioBurnEngine? directEngine = null;
            LastDirectBurnResult = null;
            LastDirectBurnLogPath = null;
            if (direct && CodeProblem() is { } codeProblem)
            {
                StatusMessage = $"Burn not started: {codeProblem}";
                var refused = BurnOperationResult.Failed(StatusMessage);
                LastBurnResult = refused;
                return refused;
            }
            if (direct)
            {
                if (!recorder.GetCapabilities().SupportsNativeSao)
                {
                    StatusMessage = "Burn failed: this drive does not report Session-at-Once writing, so direct drive commands cannot be used with it. "
                                  + "Switch off \"Direct drive commands\" to burn with the Windows writer.";
                    var unsupported = BurnOperationResult.Failed(StatusMessage);
                    LastBurnResult = unsupported;
                    return unsupported;
                }
                directEngine = new CdBurner.Hardware.NativeWriting.NativeAudioBurnEngine(DirectBurnLogDirectory) { TestWriteFirst = TestWriteFirst };
            }

            AudioBurnOptions Options(bool allowDamagedAudio) => new()
            {
                UseDiscAtOnce = (RecordingMode == AudioRecordingMode.DiscAtOnceGapless),
                WriteCdText = IncludeCdText,
                PerformNormalization = NormalizeAllTracks,
                PreGapFrames = DefaultPreGapFrames,
                Speed = SelectedWriteSpeed,
                KeepIndexMarks = direct,
                AllowDamagedAudio = allowDamagedAudio
            };

            var burnService = directEngine != null ? new AudioCdBurnService(directEngine, _audioDecoder) : _audioBurnService;
            var burnResult = await burnService.BurnAsync(recorder, compilation, Options(allowDamagedAudio: false), progress, activeToken);

            // A damaged FLAC stops the burn before the drive is touched; the user may accept the damage and go on.
            if (burnResult.DamagedAudioFiles is { Count: > 0 } && CdBurner.App.Views.BurnTargetDialog.IsInteractiveSession)
            {
                var answer = System.Windows.MessageBox.Show(
                    $"{burnResult.ErrorMessage}\n\nBurn anyway? The damaged parts will be silence or missing on the disc.",
                    "Damaged audio file — IgnisDisc",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Warning,
                    System.Windows.MessageBoxResult.No);
                if (answer == System.Windows.MessageBoxResult.Yes)
                {
                    burnResult = await burnService.BurnAsync(recorder, compilation, Options(allowDamagedAudio: true), progress, activeToken);
                }
                else
                {
                    StatusMessage = $"Burn not started: {burnResult.ErrorMessage}";
                    LastBurnResult = burnResult;
                    return burnResult;
                }
            }
            LastBurnResult = burnResult;
            LastDirectBurnResult = directEngine?.LastResult;
            LastDirectBurnLogPath = directEngine?.LastLogPath;

            if (burnResult.Success && direct)
            {
                StatusMessage = $"Audio CD burned with direct drive commands and read back correctly ({burnResult.SectorsWritten:N0} frames, {burnResult.TotalTime}).";
                BurnProgress = 100.0;
                if (directEngine?.LastWriteOffsetPlan is { } offsetPlan)
                {
                    // Say whether the audio sits sample-exact for offset-corrected rippers, or why not yet.
                    StatusMessage += " " + offsetPlan.Explanation;
                }
                if (directEngine?.LastResult?.Verification?.Warning is { } readWarning)
                {
                    // Right audio, but parts of the disc needed careful re-reads: say so before it goes in a car.
                    StatusMessage += " " + readWarning;
                    if (CdBurner.App.Views.BurnTargetDialog.IsInteractiveSession)
                    {
                        System.Windows.MessageBox.Show(
                            $"The disc holds the right audio, but part of it was hard to read back.\n\n{readWarning}",
                            "Burned, hard to read in places — IgnisDisc",
                            System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Warning);
                    }
                }
            }
            else if (burnResult.Success)
            {
                StatusMessage = $"Audio CD burned successfully ({burnResult.SectorsWritten:N0} frames, {burnResult.TotalTime}).";
                bool hasDiscMetadata = (compilation.AlbumTitle is { Length: > 0 } title && title != "Untitled Album")
                    || compilation.Tracks.Any(t => !string.IsNullOrWhiteSpace(t.Isrc));
                if (!recorder.IsVirtual && IncludeCdText && hasDiscMetadata)
                {
                    // Windows' burning API (IMAPIv2) cannot write lead-in CD-Text or ISRC codes; be explicit about it.
                    StatusMessage += " Note: CD-Text and ISRC metadata are not written to physical discs in this version (they are kept in the project and .cue files).";
                }
                BurnProgress = 100.0;
            }
            else
            {
                StatusMessage = $"Burn failed: {burnResult.ErrorMessage}";
                if (direct && directEngine?.LastResult is { StoppedBeforeWriting: true } && !TestWriteFirst)
                {
                    // Stopped before the laser came on: the same disc can still be burned the standard way.
                    StatusMessage += " The disc is unchanged; switch off \"Direct drive commands\" to burn it with the Windows writer.";
                }
                if (CdBurner.App.Views.BurnTargetDialog.IsInteractiveSession)
                {
                    System.Windows.MessageBox.Show(
                        $"Audio CD burn could not be completed:\n\n{burnResult.ErrorMessage}",
                        "Burn Failed — IgnisDisc",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Error);
                }
            }

            return burnResult;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Audio CD burn cancelled by user.";
            var cancelled = BurnOperationResult.CancelledResult(StatusMessage);
            LastBurnResult = cancelled;
            return cancelled;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Burn error: {ex.Message}";
            var fail = BurnOperationResult.Failed(ex.Message);
            LastBurnResult = fail;
            if (CdBurner.App.Views.BurnTargetDialog.IsInteractiveSession)
            {
                System.Windows.MessageBox.Show(
                    $"An error occurred during Audio CD burning:\n\n{ex.Message}",
                    "Burn Error — IgnisDisc",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
            return fail;
        }
        finally
        {
            _activeBurnCts?.Dispose();
            _activeBurnCts = null;
            IsBusy = false;
            CancelBurnCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Cancels any actively executing Audio CD burn operation.
    /// </summary>
    public void CancelActiveBurn()
    {
        if (_activeBurnCts != null && !_activeBurnCts.IsCancellationRequested)
        {
            StatusMessage = "Cancelling Audio CD burn...";
            try
            {
                _activeBurnCts.Cancel();
            }
            catch
            {
            }
        }
    }

    private bool _applyingSettings;

    /// <summary>
    /// Applies saved rip options (called after the settings file is loaded at startup) without saving them back.
    /// </summary>
    public void ApplyRipSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _applyingSettings = true;
        try
        {
            ReadOffsetSamples = settings.AudioReadOffsetSamples;
            SecureRip = settings.SecureAudioRipByDefault;
            LookupMetadata = settings.LookupMusicBrainzByDefault;
            VerifyAccurateRip = settings.VerifyAccurateRipByDefault;
        }
        finally
        {
            _applyingSettings = false;
        }
    }

    /// <summary>
    /// Persists rip settings back to the configuration file if an <see cref="AppSettingsService"/> was supplied.
    /// </summary>
    private void SaveSettingsIfAvailable()
    {
        if (_settingsService == null || _applyingSettings) return;
        try
        {
            var settings = _settingsService.CurrentSettings;
            settings.AudioReadOffsetSamples = _readOffsetSamples;
            settings.SecureAudioRipByDefault = _secureRip;
            settings.LookupMusicBrainzByDefault = _lookupMetadata;
            settings.VerifyAccurateRipByDefault = _verifyAccurateRip;
            _settingsService.CurrentSettings = settings;
            _settingsService.Save();
        }
        catch
        {
            // Ignore settings persistence exceptions in UI
        }
    }

    /// <summary>
    /// Measures the optical drive's audio read offset using an inserted CD matched against AccurateRip.
    /// </summary>
    /// <summary>
    /// Queries MusicBrainz for the inserted Audio CD's table-of-contents DiscID and displays the release metadata.
    /// </summary>
    public async Task<CdTextAlbumMetadata?> LookupDiscMetadataAsync(HttpClient? httpClient = null, CancellationToken ct = default)
    {
        var recorder = ResolveRecorder();
        if (recorder == null)
        {
            StatusMessage = "No optical recorder selected or available.";
            RipReport = "MusicBrainz lookup failed: No optical recorder selected or available.";
            return null;
        }

        IsBusy = true;
        StatusMessage = $"Looking up Audio CD in {recorder.UniqueId} on MusicBrainz...";
        RipReport = string.Empty;

        try
        {
            var ids = DiscIdCalculator.CalculateForRecorder(recorder);
            if (ids == null)
            {
                StatusMessage = "No audio tracks found on inserted disc.";
                RipReport = "MusicBrainz lookup failed: No audio tracks found on the inserted disc.";
                return null;
            }

            bool ownsClient = httpClient == null;
            var client = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            try
            {
                var mbClient = new MusicBrainzClient(client, MusicBrainzClient.DefaultUserAgent);
                var releases = await mbClient.LookupDiscIdAsync(ids.MusicBrainzId, ct).ConfigureAwait(true);
                if (releases == null || releases.Count == 0)
                {
                    StatusMessage = $"No MusicBrainz match for DiscID {ids.MusicBrainzId}.";
                    RipReport = $"MusicBrainz lookup completed: No matching release found for DiscID {ids.MusicBrainzId} (freedb {ids.FreeDbIdHex}).";
                    return null;
                }

                var meta = MusicBrainzClient.ToAlbumMetadata(releases[0], ids.MusicBrainzId);
                if (meta != null)
                {
                    if (string.IsNullOrWhiteSpace(AlbumTitle) || AlbumTitle == "Untitled Album")
                    {
                        AlbumTitle = meta.AlbumTitle;
                    }
                    if (string.IsNullOrWhiteSpace(AlbumPerformer) || AlbumPerformer == "Various Artists")
                    {
                        AlbumPerformer = meta.AlbumPerformer;
                    }
                    for (int i = 0; i < Tracks.Count && i < (meta.Tracks?.Count ?? 0); i++)
                    {
                        if (!string.IsNullOrWhiteSpace(meta.Tracks![i].Title))
                        {
                            Tracks[i].Title = meta.Tracks![i].Title!;
                        }
                        if (!string.IsNullOrWhiteSpace(meta.Tracks![i].Performer))
                        {
                            Tracks[i].Performer = meta.Tracks![i].Performer!;
                        }
                    }

                    var sb = new StringBuilder();
                    sb.AppendLine($"MusicBrainz release found (DiscID {ids.MusicBrainzId}):");
                    sb.AppendLine($"  Album:   {meta.AlbumTitle}");
                    sb.AppendLine($"  Artist:  {meta.AlbumPerformer}");
                    sb.AppendLine($"  Tracks:  {meta.Tracks?.Count ?? 0}");
                    foreach (var t in meta.Tracks ?? Array.Empty<CdTextTrackMetadata>())
                    {
                        sb.AppendLine($"    Track {t.TrackNumber:D2}: {t.Performer} - {t.Title}");
                    }
                    RipReport = sb.ToString().TrimEnd();
                    StatusMessage = $"MusicBrainz: {meta.AlbumPerformer} — {meta.AlbumTitle} ({meta.Tracks?.Count ?? 0} tracks)";
                }
                return meta;
            }
            finally
            {
                if (ownsClient)
                {
                    client.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"MusicBrainz lookup error: {ex.Message}";
            RipReport = $"MusicBrainz lookup failed: {ex.Message}";
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task DetectReadOffsetAsync()
    {
        var recorder = ResolveRecorder();
        if (recorder == null)
        {
            StatusMessage = "No optical recorder selected or available.";
            RipReport = "Offset detection failed: No optical recorder selected or available.";
            return;
        }

        IsBusy = true;
        StatusMessage = $"Detecting read offset for {recorder.UniqueId}...";
        RipReport = string.Empty;

        try
        {
            var query = AudioCdRipperService.QueryTracksAndCdText(recorder);
            var tracks = query.Tracks;
            var ids = DiscIdCalculator.CalculateForRecorder(recorder);
            if (ids == null || tracks.Count == 0)
            {
                StatusMessage = "No audio tracks found on inserted disc.";
                RipReport = "Offset detection failed: No audio tracks found. Insert an audio CD to detect the drive's read offset.";
                return;
            }

            IReadOnlyList<AccurateRipPressing> pressings;
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
            {
                pressings = await AccurateRipDatabase.FetchAsync(http, ids).ConfigureAwait(false);
            }

            if (pressings.Count == 0)
            {
                StatusMessage = "Disc not found in AccurateRip database.";
                RipReport = "Offset detection failed: This disc is not in the AccurateRip database, so it cannot be used to detect the offset. Try a well-known commercial CD.";
                return;
            }

            var detection = await ReadOffsetDetector.DetectAsync(_burnEngine, recorder, tracks, pressings).ConfigureAwait(false);
            var best = detection.Best;

            if (best == null)
            {
                string errMsg = detection.ErrorMessage ?? "No offset within +/-3000 samples matched the AccurateRip database.";
                StatusMessage = $"Offset detection failed: {errMsg}";
                RipReport = $"Offset detection failed: {errMsg}";
                return;
            }

            ReadOffsetSamples = best.OffsetSamples;
            StatusMessage = $"Read offset detected: {best.OffsetSamples:+0;-0;0} samples (confidence {best.Confidence}).";

            var sb = new StringBuilder();
            sb.AppendLine("Read offset detected successfully:");
            sb.AppendLine($"  Drive:          {recorder.UniqueId}");
            sb.AppendLine($"  Read Offset:    {best.OffsetSamples:+0;-0;0} samples");
            sb.AppendLine($"  Confidence:     {best.Confidence} matching submissions");
            sb.AppendLine($"  Tracks Checked: {string.Join(", ", detection.ProbedTracks)}");
            RipReport = sb.ToString().TrimEnd();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Offset detection cancelled.";
            RipReport = "Offset detection cancelled by user.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Offset detection error: {ex.Message}";
            RipReport = $"Offset detection failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static AudioCdRipResult AudioRipResultWithFiles(AudioCdRipResult rip, IReadOnlyList<string> files) =>
        AudioCdRipResult.Succeeded(
            files, rip.TotalSectorsRead, rip.TotalTime, rip.CueSheetPath, rip.CdText, rip.TrackInfos,
            rip.BadSectorLbas, rip.SecureModeUsed, rip.ReplayGainAlbumGain, rip.ReplayGainAlbumPeak);

    /// <summary>
    /// Extracts Red Book audio tracks from the inserted CD into uncompressed WAV files and companion CUE sheet.
    /// </summary>
    public async Task<AudioCdRipResult> RipAudioCdAsync(string? driveId = null, string? outputFolder = null, CancellationToken ct = default)
    {
        RipReport = string.Empty;

        var recorder = ResolveRecorder(driveId);
        if (recorder == null)
        {
            StatusMessage = "No optical recorder selected or available.";
            var fail = AudioCdRipResult.Failed(StatusMessage);
            LastRipResult = fail;
            RipReport = $"Rip failed: {StatusMessage}";
            return fail;
        }

        string targetFolder = !string.IsNullOrWhiteSpace(outputFolder)
            ? outputFolder
            : (!string.IsNullOrWhiteSpace(RipOutputFolder) ? RipOutputFolder : Path.Combine(Path.GetTempPath(), "CdBurner_RippedAudio"));

        // Check the output format before reading the disc, so a missing ffmpeg or a bad bitrate costs no rip time.
        var transcoder = new AudioRipTranscoder(new MediaFoundationAacEncoder());
        int bitrate = 0;
        string? formatProblem = null;
        if (!string.IsNullOrWhiteSpace(RipBitrateKbps))
        {
            if (!int.TryParse(RipBitrateKbps.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out bitrate) || bitrate <= 0)
            {
                formatProblem = $"Invalid bitrate '{RipBitrateKbps}'. Enter kbit/s as a whole number or leave it empty.";
            }
        }
        string? customFfmpeg = _settingsService?.CurrentSettings.FfmpegExecutablePath;
        string? ffmpegPath = AudioRipFormats.RequiresFfmpeg(SelectedRipFormat)
            ? FfmpegLocator.FindFfmpegExecutable(string.IsNullOrWhiteSpace(customFfmpeg) ? null : customFfmpeg)
            : null;
        var transcodeSettings = new AudioRipTranscodeSettings(
            SelectedRipFormat, AudioRipFormats.UsesBitrate(SelectedRipFormat) ? bitrate : 0, ffmpegPath);
        formatProblem ??= transcoder.CheckPrerequisites(transcodeSettings);
        if (formatProblem != null)
        {
            StatusMessage = formatProblem;
            var fail = AudioCdRipResult.Failed(formatProblem);
            LastRipResult = fail;
            RipReport = $"Rip not started: {formatProblem}";
            return fail;
        }

        _activeBurnCts?.Dispose();
        _activeBurnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var activeToken = _activeBurnCts.Token;

        IsBusy = true;
        CancelBurnCommand.RaiseCanExecuteChanged();
        BurnProgress = 0;
        StatusMessage = $"Ripping Audio CD tracks from {recorder.UniqueId}...";

        try
        {
            var progress = new Progress<BurnProgressSnapshot>(p =>
            {
                if (!_isBusy) return;
                if (p.TotalLba > 0)
                {
                    BurnProgress = Math.Min(100.0, (double)p.CurrentLba / p.TotalLba * 100.0);
                }
                StatusMessage = $"{p.CurrentActionDescription} ({BurnProgress:F0}%)";
            });

            DiscIdentifiers? ids = null;
            try
            {
                ids = DiscIdCalculator.CalculateForRecorder(recorder);
            }
            catch
            {
            }

            string? warning = null;
            CdTextAlbumMetadata? metadataOverride = null;

            if (LookupMetadata && ids != null)
            {
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                    var mbClient = new MusicBrainzClient(http, MusicBrainzClient.DefaultUserAgent);
                    var releases = await mbClient.LookupDiscIdAsync(ids.MusicBrainzId, activeToken).ConfigureAwait(false);
                    if (releases != null && releases.Count > 0)
                    {
                        metadataOverride = MusicBrainzClient.ToAlbumMetadata(releases[0], ids.MusicBrainzId);
                    }
                }
                catch (HttpRequestException hre)
                {
                    warning = $"MusicBrainz lookup failed: {hre.Message}";
                }
                catch (TaskCanceledException tce)
                {
                    warning = $"MusicBrainz lookup timed out: {tce.Message}";
                }
                catch (Exception ex)
                {
                    warning = $"MusicBrainz lookup failed: {ex.Message}";
                }
            }

            var options = new AudioRipOptions
            {
                GenerateCueSheet = true,
                CueSheetFileName = "album.cue",
                ContinueOnUnreadableSector = ContinueOnUnreadable,
                MetadataOverride = metadataOverride,
                ReadOffsetSamples = ReadOffsetSamples,
                SecureMode = SecureRip
            };

            var ripResult = await _ripperService.RipAudioCdAsync(recorder, _burnEngine, targetFolder, options, progress, activeToken);
            LastRipResult = ripResult;

            string? conversionNote = null;
            if (ripResult.Success && SelectedRipFormat != AudioRipFormat.Wav)
            {
                StatusMessage = $"Converting tracks to {AudioRipFormats.DisplayName(SelectedRipFormat)}...";
                try
                {
                    var converted = await transcoder.TranscodeAsync(ripResult, transcodeSettings, cancellationToken: activeToken);
                    ripResult = AudioRipResultWithFiles(ripResult, converted.Files);
                    if (converted.Warnings.Count > 0)
                    {
                        conversionNote = string.Join(" ", converted.Warnings);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Runtime.InteropServices.COMException)
                {
                    conversionNote = $"Converting to {AudioRipFormats.DisplayName(SelectedRipFormat)} failed: {ex.Message} The WAV files were kept.";
                }
                LastRipResult = ripResult;
            }

            string metadataSource = metadataOverride != null
                ? "musicbrainz"
                : (ripResult.CdText != null ? "cd-text" : "none");

            string? accurateRipStatus = null;
            var accurateRipTracks = new List<(AudioTrackRipInfo Info, AccurateRipTrackVerdict? Verdict)>();

            if (ripResult.Success && VerifyAccurateRip)
            {
                if (ids == null)
                {
                    accurateRipStatus = "unavailable";
                }
                else
                {
                    try
                    {
                        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                        var pressings = await AccurateRipDatabase.FetchAsync(http, ids, cancellationToken: activeToken).ConfigureAwait(false);
                        accurateRipStatus = pressings.Count == 0 ? "not-in-database" : "checked";
                        foreach (var info in ripResult.TrackInfos)
                        {
                            accurateRipTracks.Add((info, pressings.Count == 0
                                ? null
                                : AccurateRipDatabase.Match(pressings, info.AudioTrackIndex, info.AccurateRipV1, info.AccurateRipV2)));
                        }
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
                    {
                        accurateRipStatus = "failed";
                        warning = (warning == null ? "" : warning + "\n") + $"AccurateRip lookup failed: {ex.Message}";
                    }
                    catch (Exception ex)
                    {
                        accurateRipStatus = "failed";
                        warning = (warning == null ? "" : warning + "\n") + $"AccurateRip lookup failed: {ex.Message}";
                    }
                }

                if (ReadOffsetSamples == 0 && accurateRipStatus == "checked" && accurateRipTracks.Any(t => t.Verdict is { IsAccurate: false }))
                {
                    warning = (warning == null ? "" : warning + "\n") +
                        "No read offset was configured; AccurateRip only matches when the drive's read offset is corrected (use 'Detect' or check the AccurateRip drive offset list).";
                }
            }

            var sb = new StringBuilder();
            if (ripResult.Success)
            {
                StatusMessage = $"Rip completed: {ripResult.TracksRipped} track(s) extracted to '{targetFolder}' ({ripResult.TotalTime}).";
                BurnProgress = 100.0;

                sb.AppendLine("Audio rip completed successfully:");
                sb.AppendLine($"  Drive:            {recorder.UniqueId}");
                sb.AppendLine($"  Output Folder:    {Path.GetFullPath(targetFolder)}");
                sb.AppendLine($"  Tracks Ripped:    {ripResult.TracksRipped}");
                sb.AppendLine($"  Sectors Read:     {ripResult.TotalSectorsRead:N0}");
                sb.AppendLine($"  Duration:         {ripResult.TotalTime}");
                sb.AppendLine($"  MusicBrainz ID:   {ids?.MusicBrainzId ?? "n/a"}");
                sb.AppendLine($"  freedb ID:        {ids?.FreeDbIdHex ?? "n/a"}");
                sb.AppendLine($"  AccurateRip:      {ids?.AccurateRipDatabasePath ?? "n/a"}");
                sb.AppendLine($"  Metadata Source:  {metadataSource}");
                sb.AppendLine($"  Read Offset:      {ReadOffsetSamples:+0;-0;0} samples");
                if (SecureRip)
                {
                    sb.AppendLine(ripResult.SecureModeUsed
                        ? $"  Secure Mode:      C2 error pointers ({ripResult.TrackInfos.Sum(t => t.RecoveredSectors)} sectors recovered, {ripResult.TrackInfos.Sum(t => t.SuspectSectors)} still suspect)"
                        : "  Secure Mode:      requested, but the drive does not report C2 error pointers (plain read used)");
                    foreach (var t in ripResult.TrackInfos.Where(t => t.SuspectSectors > 0))
                    {
                        sb.AppendLine($"    Track {t.TrackNumber:D2}: {t.SuspectSectors} suspect sector(s) - check AccurateRip or re-rip");
                    }
                }
                if (ripResult.BadSectorLbas.Count > 0)
                {
                    sb.AppendLine($"  Bad Sectors:      {ripResult.BadSectorLbas.Count} (zero-filled)");
                }
                if (accurateRipStatus != null)
                {
                    sb.AppendLine($"  AccurateRip:      {accurateRipStatus}");
                    foreach (var (info, verdict) in accurateRipTracks)
                    {
                        string line = verdict == null
                            ? "not in database"
                            : verdict.IsAccurate
                                ? $"accurate (confidence {verdict.Confidence}/{verdict.TotalSubmissions}, {(verdict.MatchedV2 ? "v2" : "v1")})"
                                : $"NOT accurate (0/{verdict.TotalSubmissions} submissions match)";
                        sb.AppendLine($"    Track {info.TrackNumber:D2}: {line}  [v1 {info.AccurateRipV1:x8} v2 {info.AccurateRipV2:x8}]");
                    }
                }
                if (!string.IsNullOrEmpty(warning))
                {
                    sb.AppendLine($"  Warning:          {warning}");
                }
                sb.AppendLine($"  Format:           {AudioRipFormats.DisplayName(SelectedRipFormat)}");
                if (!string.IsNullOrEmpty(conversionNote))
                {
                    sb.AppendLine($"  Conversion:       {conversionNote}");
                }
                sb.AppendLine("  Files:");
                foreach (var file in ripResult.RippedFilePaths)
                {
                    sb.AppendLine($"    {Path.GetFullPath(file)}");
                }
                if (!string.IsNullOrEmpty(ripResult.CueSheetPath) && File.Exists(ripResult.CueSheetPath))
                {
                    sb.AppendLine($"  CUE Sheet:        {Path.GetFullPath(ripResult.CueSheetPath)}");
                }
            }
            else
            {
                StatusMessage = $"Rip failed: {ripResult.ErrorMessage}";

                sb.AppendLine($"Audio rip failed: {ripResult.ErrorMessage}");
                if (ids != null)
                {
                    sb.AppendLine($"  MusicBrainz ID:   {ids.MusicBrainzId}");
                    sb.AppendLine($"  freedb ID:        {ids.FreeDbIdHex}");
                    sb.AppendLine($"  AccurateRip:      {ids.AccurateRipDatabasePath}");
                }
                if (!string.IsNullOrEmpty(warning))
                {
                    sb.AppendLine($"  Warning:          {warning}");
                }
            }

            RipReport = sb.ToString().TrimEnd();
            return ripResult;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Audio CD rip cancelled by user.";
            var cancelled = AudioCdRipResult.Failed(StatusMessage);
            LastRipResult = cancelled;
            RipReport = "Audio CD rip cancelled by user.";
            return cancelled;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Rip error: {ex.Message}";
            var fail = AudioCdRipResult.Failed(ex.Message, ex);
            LastRipResult = fail;
            RipReport = $"Rip error: {ex.Message}";
            return fail;
        }
        finally
        {
            _activeBurnCts?.Dispose();
            _activeBurnCts = null;
            IsBusy = false;
            CancelBurnCommand.RaiseCanExecuteChanged();
        }
    }

    private IDiscRecorderBackend? ResolveRecorder(string? driveId = null)
    {
        if (SelectedRecorder != null && (string.IsNullOrWhiteSpace(driveId) || string.Equals(SelectedRecorder.UniqueId, driveId, StringComparison.OrdinalIgnoreCase)))
        {
            return SelectedRecorder;
        }

        if (!string.IsNullOrWhiteSpace(driveId))
        {
            var found = _deviceService.GetRecorders().FirstOrDefault(r => string.Equals(r.UniqueId, driveId, StringComparison.OrdinalIgnoreCase));
            if (found != null)
            {
                SelectedRecorder = found;
                return found;
            }
        }

        if (SelectedRecorder != null)
        {
            return SelectedRecorder;
        }

        var defaultRec = _deviceService.GetRecorders().FirstOrDefault(r => !r.IsVirtual)
                         ?? _deviceService.GetRecorders().FirstOrDefault(r => string.Equals(r.UniqueId, "VDRV-01", StringComparison.OrdinalIgnoreCase))
                         ?? _deviceService.GetRecorders().FirstOrDefault();
        if (defaultRec != null)
        {
            SelectedRecorder = defaultRec;
        }
        return defaultRec;
    }

    #endregion
}
