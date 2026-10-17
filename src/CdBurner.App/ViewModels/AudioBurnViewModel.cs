using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using CdBurner.App.Mvvm;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
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
public class AudioBurnViewModel : ObservableObject
{
    private readonly IDiscDeviceService _deviceService;
    private readonly IBurnEngineBackend _burnEngine;
    private readonly IAudioDecoder _audioDecoder;
    private readonly AudioCdBurnService _audioBurnService;
    private readonly AudioCdRipperService _ripperService;

    private string _albumTitle = "Untitled Album";
    private string _albumPerformer = "Various Artists";
    private string? _albumSongwriter;
    private string? _upcEanCode;
    private AudioRecordingMode _recordingMode = AudioRecordingMode.DiscAtOnceGapless;
    private bool _normalizeAllTracks = false;
    private int _defaultPreGapFrames = OpticalConstants.CdLeadInFrames; // 150 frames = 2.0s
    private bool _includeCdText = true;
    private MsfAddress _totalDuration = new(0, 0, 0);
    private double _discUsagePercent;
    private string _ripOutputFolder = string.Empty;

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
        set => SetProperty(ref _upcEanCode, value);
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

    /// <summary>
    /// Ordered list of audio tracks in this compilation.
    /// </summary>
    public ObservableCollection<AudioTrackRowViewModel> Tracks { get; } = new();

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
    /// Extracts Red Book audio tracks from the inserted CD into WAV files and CUE sheet.
    /// </summary>
    public AsyncRelayCommand RipAudioCdCommand { get; }

    /// <summary>
    /// Browses for an output directory to store ripped audio files.
    /// </summary>
    public RelayCommand BrowseRipOutputFolderCommand { get; }

    #endregion

    /// <summary>
    /// Initializes a new instance of <see cref="AudioBurnViewModel"/>.
    /// </summary>
    /// <param name="deviceService">Optional optical device enumeration service.</param>
    /// <param name="burnEngine">Optional optical burning engine backend.</param>
    /// <param name="audioDecoder">Optional audio decoder implementation.</param>
    public AudioBurnViewModel(
        IDiscDeviceService? deviceService = null,
        IBurnEngineBackend? burnEngine = null,
        IAudioDecoder? audioDecoder = null)
    {
        _deviceService = deviceService ?? new DiscDeviceManager(enablePhysicalEnumeration: false);
        _burnEngine = burnEngine ?? new VirtualBurnEngine();
        _audioDecoder = audioDecoder ?? new MediaFoundationAudioDecoder();
        _audioBurnService = new AudioCdBurnService(_burnEngine, _audioDecoder);
        _ripperService = new AudioCdRipperService();

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
        MoveTrackUpCommand = new RelayCommand(p => MoveTrackUp(p as AudioTrackRowViewModel), _ => CanMoveTrackUp(SelectedTrack));
        MoveTrackDownCommand = new RelayCommand(p => MoveTrackDown(p as AudioTrackRowViewModel), _ => CanMoveTrackDown(SelectedTrack));
        StartAudioBurnCommand = new AsyncRelayCommand(async p => await StartAudioBurnAsync(p as string), _ => !IsBusy && Tracks.Count > 0);
        RipAudioCdCommand = new AsyncRelayCommand(async p => await RipAudioCdAsync(p as string), _ => !IsBusy);
        BrowseRipOutputFolderCommand = new RelayCommand(BrowseRipOutputFolder);

        // Pre-select default recorder if available
        SelectedRecorder = _deviceService.GetRecorders().FirstOrDefault(r => string.Equals(r.UniqueId, "VDRV-01", StringComparison.OrdinalIgnoreCase))
                           ?? _deviceService.GetRecorders().FirstOrDefault();

        RecalculateTotals();
    }

    #region Track Management Operations

    /// <summary>
    /// Adds a single physical audio file to the compilation.
    /// </summary>
    public void AddAudioFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return;
        }

        if (Tracks.Count >= AudioCdCompilation.MaxTracks)
        {
            StatusMessage = $"Audio CD supports a maximum of {AudioCdCompilation.MaxTracks} tracks.";
            return;
        }

        int trackNum = Tracks.Count + 1;
        string fileName = Path.GetFileNameWithoutExtension(filePath);
        string title = fileName;
        string performer = AlbumPerformer;
        int durationFrames = 0;
        long fileSize = 0;

        try
        {
            var fi = new FileInfo(filePath);
            fileSize = fi.Length;
        }
        catch { }

        try
        {
            var meta = _audioDecoder.ProbeAudioFile(filePath);
            if (!string.IsNullOrWhiteSpace(meta.Title)) title = meta.Title;
            if (!string.IsNullOrWhiteSpace(meta.Artist)) performer = meta.Artist;

            if (meta.TotalRedBookBytes > 0)
            {
                durationFrames = OpticalConstants.BytesToSectors(meta.TotalRedBookBytes, OpticalConstants.RawSectorSize);
            }
            else if (meta.Duration > TimeSpan.Zero)
            {
                durationFrames = (int)Math.Round(meta.Duration.TotalSeconds * OpticalConstants.AudioFramesPerSecond);
            }
        }
        catch
        {
            // Fallback for raw PCM or non-standard test files
            if (fileSize > 0)
            {
                durationFrames = OpticalConstants.BytesToSectors(fileSize, OpticalConstants.RawSectorSize);
            }
            if (durationFrames == 0)
            {
                durationFrames = OpticalConstants.CdLeadInFrames; // 150 frames (2.0s) default
            }
        }

        var row = new AudioTrackRowViewModel
        {
            TrackNumber = trackNum,
            SourceFilePath = filePath,
            Title = title,
            Performer = performer,
            Songwriter = AlbumSongwriter,
            PreGapFrames = trackNum == 1 ? DefaultPreGapFrames : 0,
            DurationFrames = durationFrames,
            FileSizeBytes = fileSize
        };

        Tracks.Add(row);
        SelectedTrack = row;
        ReindexTracks();
    }

    /// <summary>
    /// Adds multiple physical audio files to the compilation.
    /// </summary>
    public void AddAudioFiles(IEnumerable<string> filePaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        foreach (var path in filePaths)
        {
            AddAudioFile(path);
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

        RecalculateTotals();
        MoveTrackUpCommand.RaiseCanExecuteChanged();
        MoveTrackDownCommand.RaiseCanExecuteChanged();
        RemoveTrackCommand.RaiseCanExecuteChanged();
        StartAudioBurnCommand.RaiseCanExecuteChanged();
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
    }

    private void BrowseAndAddFiles()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Add Audio Tracks to Audio CD Compilation",
            Filter = "Audio Files (*.wav;*.mp3;*.flac;*.ogg;*.wma;*.m4a)|*.wav;*.mp3;*.flac;*.ogg;*.wma;*.m4a|All Files (*.*)|*.*",
            Multiselect = true
        };

        if (dlg.ShowDialog() == true && dlg.FileNames.Length > 0)
        {
            AddAudioFiles(dlg.FileNames);
        }
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
        var recorder = ResolveRecorder(driveId);
        if (recorder == null)
        {
            StatusMessage = "No optical recorder selected or available.";
            var fail = BurnOperationResult.Failed(StatusMessage);
            LastBurnResult = fail;
            return fail;
        }

        if (Tracks.Count == 0)
        {
            StatusMessage = "No audio tracks in compilation to burn.";
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

        IsBusy = true;
        BurnProgress = 0;
        StatusMessage = $"Preparing Audio CD compilation for {recorder.UniqueId}...";

        try
        {
            var compilation = new AudioCdCompilation
            {
                AlbumTitle = AlbumTitle,
                AlbumPerformer = AlbumPerformer,
                Songwriter = AlbumSongwriter,
                UpcEan = UpcEanCode,
                RecordingMode = RecordingMode,
                NormalizeAllTracks = NormalizeAllTracks,
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
                    NormalizeAudio: row.NormalizeAudio
                )
                {
                    Songwriter = row.Songwriter ?? AlbumSongwriter,
                    Composer = row.Composer
                });
            }

            var progress = new Progress<BurnProgressSnapshot>(p =>
            {
                if (p.TotalLba > 0)
                {
                    BurnProgress = Math.Min(100.0, (double)p.CurrentLba / p.TotalLba * 100.0);
                }
                StatusMessage = $"{p.CurrentActionDescription} ({BurnProgress:F0}%)";
            });

            var audioBurnOptions = new AudioBurnOptions
            {
                UseDiscAtOnce = (RecordingMode == AudioRecordingMode.DiscAtOnceGapless),
                WriteCdText = IncludeCdText,
                PerformNormalization = NormalizeAllTracks,
                PreGapFrames = DefaultPreGapFrames
            };

            var burnResult = await _audioBurnService.BurnAsync(recorder, compilation, audioBurnOptions, progress, ct);
            LastBurnResult = burnResult;

            if (burnResult.Success)
            {
                StatusMessage = $"Audio CD burned successfully ({burnResult.SectorsWritten:N0} frames, {burnResult.TotalTime}).";
                BurnProgress = 100.0;
            }
            else
            {
                StatusMessage = $"Burn failed: {burnResult.ErrorMessage}";
            }

            return burnResult;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Burn error: {ex.Message}";
            var fail = BurnOperationResult.Failed(ex.Message);
            LastBurnResult = fail;
            return fail;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Extracts Red Book audio tracks from the inserted CD into uncompressed WAV files and companion CUE sheet.
    /// </summary>
    public async Task<AudioCdRipResult> RipAudioCdAsync(string? driveId = null, string? outputFolder = null, CancellationToken ct = default)
    {
        var recorder = ResolveRecorder(driveId);
        if (recorder == null)
        {
            StatusMessage = "No optical recorder selected or available.";
            var fail = AudioCdRipResult.Failed(StatusMessage);
            LastRipResult = fail;
            return fail;
        }

        string targetFolder = !string.IsNullOrWhiteSpace(outputFolder)
            ? outputFolder
            : (!string.IsNullOrWhiteSpace(RipOutputFolder) ? RipOutputFolder : Path.Combine(Path.GetTempPath(), "CdBurner_RippedAudio"));

        IsBusy = true;
        BurnProgress = 0;
        StatusMessage = $"Ripping Audio CD tracks from {recorder.UniqueId}...";

        try
        {
            var progress = new Progress<BurnProgressSnapshot>(p =>
            {
                if (p.TotalLba > 0)
                {
                    BurnProgress = Math.Min(100.0, (double)p.CurrentLba / p.TotalLba * 100.0);
                }
                StatusMessage = $"{p.CurrentActionDescription} ({BurnProgress:F0}%)";
            });

            var options = new AudioRipOptions
            {
                GenerateCueSheet = true,
                CueSheetFileName = "album.cue"
            };

            var ripResult = await _ripperService.RipAudioCdAsync(recorder, _burnEngine, targetFolder, options, progress, ct);
            LastRipResult = ripResult;

            if (ripResult.Success)
            {
                StatusMessage = $"Rip completed: {ripResult.TracksRipped} track(s) extracted to '{targetFolder}' ({ripResult.TotalTime}).";
                BurnProgress = 100.0;
            }
            else
            {
                StatusMessage = $"Rip failed: {ripResult.ErrorMessage}";
            }

            return ripResult;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Rip error: {ex.Message}";
            var fail = AudioCdRipResult.Failed(ex.Message, ex);
            LastRipResult = fail;
            return fail;
        }
        finally
        {
            IsBusy = false;
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

        var defaultRec = _deviceService.GetRecorders().FirstOrDefault(r => string.Equals(r.UniqueId, "VDRV-01", StringComparison.OrdinalIgnoreCase))
                         ?? _deviceService.GetRecorders().FirstOrDefault();
        if (defaultRec != null)
        {
            SelectedRecorder = defaultRec;
        }
        return defaultRec;
    }

    #endregion
}
