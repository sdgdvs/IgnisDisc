using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using CdBurner.App.Mvvm;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Burning;
using CdBurner.Core.Compilation;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Iso;
using System.Net.Http;
using CdBurner.Core.Primitives;
using CdBurner.Core.Utilities;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;

namespace CdBurner.App.ViewModels;

/// <summary>
/// View model managing DVD-Video authoring, VIDEO_TS structure verification,
/// 32 KB (16-sector) IFO/BUP ECC alignment gap inspection, DVD-9 layer break selection, and burning.
/// </summary>
public class DvdVideoViewModel : ObservableObject
{
    private readonly IDiscDeviceService _deviceService;
    private readonly IBurnEngineBackend _burnEngine;

    private string _videoTsFolderPath = string.Empty;
    private string _volumeLabel = "DVD_VIDEO";
    private bool _isStructureValid;
    private bool _isDualLayerRequired;
    private int _selectedLayerBreakLba;
    private IDiscRecorderBackend? _selectedRecorder;
    private bool _isBusy;
    private string _statusMessage = "Ready";
    private double _burnProgress;
    private string? _lastBuiltIsoPath;
    private long _totalSizeBytes;
    private int _totalSectors;
    private int _titleSetCount;
    private bool _hasAudioTs;

    private DvdVideoStandard _selectedStandard = DvdVideoStandard.Ntsc;
    private DvdAspectRatio _selectedAspectRatio = DvdAspectRatio.Widescreen16x9;
    private bool _autoCreatePlayMenu = true;
    private string _menuTitle = "DVD Video";
    private bool _requiresAutoAuthoring;
    private string? _lastAuthoredVideoTsPath;
    private string? _tempAuthoredDirectory;
    private WriteSpeedDescriptor? _selectedWriteSpeed;
    private string? _customFfmpegPath;
    private bool _isFfmpegAvailable;
    private string _ffmpegStatusText = string.Empty;
    private bool _verifyAfterBurn;
    private bool _ejectAfterBurn;
    private CancellationTokenSource? _activeCts;

    #region Properties

    /// <summary>
    /// Underlying optical drive discovery and enumeration service.
    /// </summary>
    public IDiscDeviceService DeviceService => _deviceService;

    /// <summary>
    /// Underlying optical burning backend engine.
    /// </summary>
    public IBurnEngineBackend BurnEngine => _burnEngine;

    /// <summary>
    /// Selected source video files when authoring from arbitrary movie media.
    /// </summary>
    public ObservableCollection<string> SourceMediaFiles { get; } = new();

    /// <summary>
    /// Physical path to the VIDEO_TS directory, root folder containing VIDEO_TS, or delimited movie file list.
    /// </summary>
    public string VideoTsFolderPath
    {
        get => _videoTsFolderPath;
        set
        {
            if (SetProperty(ref _videoTsFolderPath, value))
            {
                SourceMediaFiles.Clear();
                LastAuthoredVideoTsPath = null;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    ValidateAndPlanLayout();
                }
                else
                {
                    ResetState();
                }
            }
        }
    }

    /// <summary>
    /// Optical volume label (max 32 uppercase characters).
    /// </summary>
    public string VolumeLabel
    {
        get => _volumeLabel;
        set => SetProperty(ref _volumeLabel, value);
    }

    /// <summary>
    /// Target television broadcast standard (NTSC or PAL).
    /// </summary>
    public DvdVideoStandard SelectedStandard
    {
        get => _selectedStandard;
        set => SetProperty(ref _selectedStandard, value);
    }

    /// <summary>
    /// Available TV broadcast standards.
    /// </summary>
    public IReadOnlyList<DvdVideoStandard> AvailableStandards { get; } = new[] { DvdVideoStandard.Ntsc, DvdVideoStandard.Pal };

    /// <summary>
    /// Target display aspect ratio (16:9 Widescreen or 4:3 Standard).
    /// </summary>
    public DvdAspectRatio SelectedAspectRatio
    {
        get => _selectedAspectRatio;
        set => SetProperty(ref _selectedAspectRatio, value);
    }

    /// <summary>
    /// Available display aspect ratios.
    /// </summary>
    public IReadOnlyList<DvdAspectRatio> AvailableAspectRatios { get; } = new[] { DvdAspectRatio.Widescreen16x9, DvdAspectRatio.Standard4x3 };

    /// <summary>
    /// Whether to automatically generate an interactive DVD Root Menu.
    /// </summary>
    public bool AutoCreatePlayMenu
    {
        get => _autoCreatePlayMenu;
        set
        {
            if (SetProperty(ref _autoCreatePlayMenu, value))
            {
                if (RequiresAutoAuthoring)
                {
                    ValidateAndPlanLayout();
                }
            }
        }
    }

    /// <summary>
    /// Title displayed on the interactive DVD Root Menu.
    /// </summary>
    public string MenuTitle
    {
        get => _menuTitle;
        set => SetProperty(ref _menuTitle, value);
    }

    /// <summary>
    /// Indicates whether the input media requires automated MPEG-2 transcoding or structure normalization.
    /// </summary>
    public bool RequiresAutoAuthoring
    {
        get => _requiresAutoAuthoring;
        private set
        {
            if (SetProperty(ref _requiresAutoAuthoring, value))
            {
                OnPropertyChanged(nameof(ValidationStatusText));
            }
        }
    }

    /// <summary>
    /// Path to the most recently authored compliant VIDEO_TS folder in temporary storage.
    /// </summary>
    public string? LastAuthoredVideoTsPath
    {
        get => _lastAuthoredVideoTsPath;
        private set => SetProperty(ref _lastAuthoredVideoTsPath, value);
    }

    /// <summary>
    /// Whether to use FFmpeg when available on the host system to transcode arbitrary video streams.
    /// </summary>
    public bool UseFfmpegWhenAvailable { get; set; } = true;

    /// <summary>
    /// Optional custom path to the FFmpeg executable configured in application settings.
    /// </summary>
    public string? CustomFfmpegPath
    {
        get => _customFfmpegPath;
        set
        {
            if (SetProperty(ref _customFfmpegPath, value))
            {
                RefreshFfmpegStatus();
            }
        }
    }

    /// <summary>
    /// Whether an FFmpeg executable was found on the host system.
    /// </summary>
    public bool IsFfmpegAvailable
    {
        get => _isFfmpegAvailable;
        private set
        {
            if (SetProperty(ref _isFfmpegAvailable, value))
            {
                OnPropertyChanged(nameof(IsFfmpegMissing));
            }
        }
    }

    /// <summary>
    /// True when <c>ffmpeg.exe</c> is not installed on the system.
    /// </summary>
    public bool IsFfmpegMissing => !_isFfmpegAvailable;

    /// <summary>
    /// Informational status line describing FFmpeg availability.
    /// </summary>
    public string FfmpegStatusText
    {
        get => _ffmpegStatusText;
        private set => SetProperty(ref _ffmpegStatusText, value);
    }

    /// <summary>
    /// Currently selected write speed for burning operations.
    /// </summary>
    public WriteSpeedDescriptor? SelectedWriteSpeed
    {
        get => _selectedWriteSpeed;
        set => SetProperty(ref _selectedWriteSpeed, value);
    }

    /// <summary>
    /// Whether to perform post-burn sector read verification.
    /// </summary>
    public bool VerifyAfterBurn
    {
        get => _verifyAfterBurn;
        set => SetProperty(ref _verifyAfterBurn, value);
    }

    /// <summary>
    /// Whether to eject the optical drive tray after burning completes.
    /// </summary>
    public bool EjectAfterBurn
    {
        get => _ejectAfterBurn;
        set => SetProperty(ref _ejectAfterBurn, value);
    }

    /// <summary>
    /// Indicates whether an active operation can be cancelled.
    /// </summary>
    public bool CanCancel => _isBusy;

    /// <summary>
    /// Indicates whether the specified VIDEO_TS structure passes all DVD-Video standards.
    /// </summary>
    public bool IsStructureValid
    {
        get => _isStructureValid;
        private set
        {
            if (SetProperty(ref _isStructureValid, value))
            {
                OnPropertyChanged(nameof(ValidationStatusText));
                BuildDvdIsoCommand.RaiseCanExecuteChanged();
                StartDvdBurnCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Collection of structure validation error, warning, and informational messages.
    /// </summary>
    public ObservableCollection<string> ValidationMessages { get; } = new();

    /// <summary>
    /// Planned physical extent placement rows showing each IFO, VOB, BUP file and 32 KB ECC padding gaps.
    /// </summary>
    public ObservableCollection<DvdExtentAlignmentRow> ExtentLayoutRows { get; } = new();

    /// <summary>
    /// Indicates whether the compilation sector count exceeds single-layer DVD-5 (2,295,104 sectors)
    /// and strictly requires DVD-9 Dual-Layer media.
    /// </summary>
    public bool IsDualLayerRequired
    {
        get => _isDualLayerRequired;
        private set
        {
            if (SetProperty(ref _isDualLayerRequired, value))
            {
                OnPropertyChanged(nameof(MediaRequirementText));
            }
        }
    }

    /// <summary>
    /// Candidate Layer Break LBAs satisfying 16-sector ECC block alignment and Layer 0 >= Layer 1 capacity.
    /// </summary>
    public ObservableCollection<int> CandidateLayerBreakLbas { get; } = new();

    /// <summary>
    /// Currently chosen Layer Break transition LBA for Dual-Layer DVD-9 (OTP) recording.
    /// </summary>
    public int SelectedLayerBreakLba
    {
        get => _selectedLayerBreakLba;
        set => SetProperty(ref _selectedLayerBreakLba, value);
    }

    /// <summary>
    /// Currently selected optical recorder backend.
    /// </summary>
    public IDiscRecorderBackend? SelectedRecorder
    {
        get => _selectedRecorder;
        set => SetProperty(ref _selectedRecorder, value);
    }

    /// <summary>
    /// Discovered optical recorder drives.
    /// </summary>
    public ObservableCollection<IDiscRecorderBackend> AvailableRecorders { get; } = new();

    /// <summary>
    /// Total byte size of all DVD-Video files combined.
    /// </summary>
    public long TotalSizeBytes
    {
        get => _totalSizeBytes;
        private set
        {
            if (SetProperty(ref _totalSizeBytes, value))
            {
                OnPropertyChanged(nameof(FormattedTotalSize));
            }
        }
    }

    /// <summary>
    /// Total 2048-byte optical data sectors required including 32 KB ECC padding blocks.
    /// </summary>
    public int TotalSectors
    {
        get => _totalSectors;
        private set => SetProperty(ref _totalSectors, value);
    }

    /// <summary>
    /// Number of title sets discovered (VTS_01, VTS_02, etc.).
    /// </summary>
    public int TitleSetCount
    {
        get => _titleSetCount;
        private set => SetProperty(ref _titleSetCount, value);
    }

    /// <summary>
    /// Indicates whether an AUDIO_TS directory is present.
    /// </summary>
    public bool HasAudioTs
    {
        get => _hasAudioTs;
        private set => SetProperty(ref _hasAudioTs, value);
    }

    /// <summary>
    /// Formatted total compilation size string.
    /// </summary>
    public string FormattedTotalSize => CompilationItemViewModel.FormatBytes(TotalSizeBytes);

    /// <summary>
    /// Display status of DVD-Video structure check.
    /// </summary>
    public string ValidationStatusText => !IsStructureValid ? "Structure Invalid" : (RequiresAutoAuthoring ? "Ready (Auto-Convert & Menu)" : "DVD-Video Compliant");

    /// <summary>
    /// Display text describing media type requirement.
    /// </summary>
    public string MediaRequirementText => IsDualLayerRequired ? "Dual-Layer DVD-9 (OTP)" : "Single-Layer DVD-5 (~4.7 GB)";

    /// <summary>
    /// Indicates whether an authoring, ISO build, or burn operation is actively executing.
    /// </summary>
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanCancel));
                CancelCommand?.RaiseCanExecuteChanged();
                BuildDvdIsoCommand?.RaiseCanExecuteChanged();
                StartDvdBurnCommand?.RaiseCanExecuteChanged();
                InstallFfmpegCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// User-facing status line.
    /// </summary>
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>
    /// Active burning or image building progress percentage (0.0 to 100.0).
    /// </summary>
    public double BurnProgress
    {
        get => _burnProgress;
        set => SetProperty(ref _burnProgress, value);
    }

    /// <summary>
    /// Absolute path of the most recently built DVD-Video ISO image file.
    /// </summary>
    public string? LastBuiltIsoPath
    {
        get => _lastBuiltIsoPath;
        private set => SetProperty(ref _lastBuiltIsoPath, value);
    }

    #endregion

    #region Commands

    public RelayCommand AddMovieFilesCommand { get; }
    public RelayCommand ClearSourceCommand { get; }
    public RelayCommand SelectVideoTsFolderCommand { get; }
    public RelayCommand ValidateAndPlanLayoutCommand { get; }
    public AsyncRelayCommand BuildDvdIsoCommand { get; }
    public AsyncRelayCommand StartDvdBurnCommand { get; }
    public AsyncRelayCommand InstallFfmpegCommand { get; }
    public RelayCommand CancelCommand { get; }

    #endregion

    public DvdVideoViewModel(
        IDiscDeviceService? deviceService = null,
        IBurnEngineBackend? burnEngine = null)
    {
        _deviceService = deviceService ?? new DiscDeviceManager(enablePhysicalEnumeration: false);
        _burnEngine = burnEngine ?? new VirtualBurnEngine();

        AddMovieFilesCommand = new RelayCommand(ExecuteAddMovieFiles);
        ClearSourceCommand = new RelayCommand(_ => ClearSource());
        SelectVideoTsFolderCommand = new RelayCommand(ExecuteSelectVideoTsFolder);
        ValidateAndPlanLayoutCommand = new RelayCommand(_ => ValidateAndPlanLayout());
        CancelCommand = new RelayCommand(_ => CancelActiveOperation(), _ => CanCancel);
        BuildDvdIsoCommand = new AsyncRelayCommand(async param =>
        {
            string? path = param as string;
            if (string.IsNullOrWhiteSpace(path) && System.Windows.Application.Current?.MainWindow?.IsLoaded == true)
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Build DVD-Video ISO Image",
                    Filter = "DVD-Video ISO Image (*.iso)|*.iso|All Files (*.*)|*.*",
                    FileName = $"{(string.IsNullOrWhiteSpace(VolumeLabel) ? "DVD_VIDEO" : VolumeLabel)}.iso"
                };
                if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.FileName))
                {
                    return;
                }
                path = dlg.FileName;
            }
            await BuildDvdIsoAsync(path);
        }, _ => IsStructureValid);
        StartDvdBurnCommand = new AsyncRelayCommand(async param => await StartDvdBurnAsync(param), _ => IsStructureValid);
        InstallFfmpegCommand = new AsyncRelayCommand(async _ => await InstallFfmpegAsync(), _ => !IsBusy);

        RefreshFfmpegStatus();

        RefreshDrives();
    }

    #region Operations

    /// <summary>
    /// Re-evaluates whether <c>ffmpeg.exe</c> is available on the host machine and updates UI status.
    /// </summary>
    public void RefreshFfmpegStatus()
    {
        string? exe = FfmpegLocator.FindFfmpegExecutable(
            string.IsNullOrWhiteSpace(_customFfmpegPath) ? null : _customFfmpegPath);
        IsFfmpegAvailable = !string.IsNullOrEmpty(exe);
        FfmpegStatusText = IsFfmpegAvailable
            ? $"FFmpeg: {Path.GetFileName(exe)}"
            : "FFmpeg not installed";
    }

    /// <summary>
    /// Downloads and extracts the LGPL Windows build of FFmpeg into per-user storage and enables video transcoding.
    /// </summary>
    public async Task<ToolInstallResult> InstallFfmpegAsync(
        string? targetDirectory = null,
        HttpClient? httpClient = null,
        string? downloadUrlOverride = null,
        CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        BurnProgress = 0;
        StatusMessage = "Downloading FFmpeg (LGPL)...";

        try
        {
            var progressHandler = new Progress<string>(msg =>
            {
                StatusMessage = msg;
            });

            var result = await ExternalToolInstallerService.InstallFfmpegAsync(
                targetDirectory,
                httpClient,
                downloadUrlOverride,
                progressHandler,
                cancellationToken).ConfigureAwait(true);

            if (result.Success && !string.IsNullOrEmpty(result.ExecutablePath))
            {
                CustomFfmpegPath = result.ExecutablePath;
                RefreshFfmpegStatus();
                BurnProgress = 100.0;
                StatusMessage = $"FFmpeg (LGPL) installed: {result.ExecutablePath}";
            }
            else
            {
                StatusMessage = $"FFmpeg install failed: {result.ErrorMessage}";
            }

            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void AddMovieFiles(IEnumerable<string> filePaths)
    {
        if (filePaths == null) return;

        foreach (var path in filePaths)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                string full = Path.GetFullPath(path);
                if (!SourceMediaFiles.Contains(full, StringComparer.OrdinalIgnoreCase))
                {
                    SourceMediaFiles.Add(full);
                }
            }
        }

        _videoTsFolderPath = string.Join("; ", SourceMediaFiles);
        OnPropertyChanged(nameof(VideoTsFolderPath));
        ValidateAndPlanLayout();
    }

    /// <summary>
    /// Clears all source media files and resets state.
    /// </summary>
    public void ClearSource()
    {
        CleanupTempAuthoredDirectory();
        SourceMediaFiles.Clear();
        VideoTsFolderPath = string.Empty;
    }

    private void CleanupTempAuthoredDirectory()
    {
        if (!string.IsNullOrEmpty(_tempAuthoredDirectory))
        {
            try
            {
                if (Directory.Exists(_tempAuthoredDirectory))
                {
                    Directory.Delete(_tempAuthoredDirectory, recursive: true);
                }
            }
            catch
            {
            }
            _tempAuthoredDirectory = null;
        }
        LastAuthoredVideoTsPath = null;
    }

    /// <summary>
    /// Validates the DVD-Video structure (or movie source files) and computes the
    /// 32 KB ECC aligned physical extent layout.
    /// </summary>
    public void ValidateAndPlanLayout()
    {
        ValidationMessages.Clear();
        ExtentLayoutRows.Clear();
        CandidateLayerBreakLbas.Clear();
        SelectedLayerBreakLba = 0;
        IsDualLayerRequired = false;
        TotalSizeBytes = 0;
        TotalSectors = 0;
        TitleSetCount = 0;
        HasAudioTs = false;
        LastAuthoredVideoTsPath = null;
        RequiresAutoAuthoring = false;

        List<string> inputs;
        if (SourceMediaFiles.Count > 0)
        {
            inputs = SourceMediaFiles.ToList();
        }
        else if (!string.IsNullOrWhiteSpace(VideoTsFolderPath))
        {
            inputs = VideoTsFolderPath
                .Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }
        else
        {
            IsStructureValid = false;
            ValidationMessages.Add("Please specify a VIDEO_TS folder or movie files.");
            return;
        }

        if (inputs.Count == 0)
        {
            IsStructureValid = false;
            ValidationMessages.Add("Please specify a VIDEO_TS folder or movie files.");
            return;
        }

        // 1. First check if a direct VIDEO_TS directory was provided that passes standard validation
        bool isDirectFolder = SourceMediaFiles.Count == 0 && inputs.Count == 1 && Directory.Exists(inputs[0]);
        if (isDirectFolder)
        {
            string actualVideoTsPath = ResolveVideoTsPath(inputs[0]);
            if (Directory.Exists(actualVideoTsPath))
            {
                var validator = new DvdVideoValidator();
                var valResult = validator.ValidateDirectory(actualVideoTsPath);

                bool hasMovieFiles = false;
                try
                {
                    var dirFiles = Directory.GetFiles(actualVideoTsPath, "*.*", SearchOption.TopDirectoryOnly);
                    hasMovieFiles = dirFiles.Any(f => DvdInputNormalizer.IsSupportedVideoFile(f) && !f.EndsWith(".vob", StringComparison.OrdinalIgnoreCase));
                }
                catch { }

                if (valResult.IsValid && !hasMovieFiles)
                {
                    RequiresAutoAuthoring = false;
                    IsStructureValid = true;
                    TotalSizeBytes = valResult.TotalSizeBytes;
                    TitleSetCount = valResult.TitleSetNumbers.Count;
                    HasAudioTs = valResult.HasAudioTs;

                    foreach (var err in valResult.Errors)
                    {
                        ValidationMessages.Add($"[Error] {err}");
                    }
                    foreach (var warn in valResult.Warnings)
                    {
                        ValidationMessages.Add($"[Warning] {warn}");
                    }

                    ValidationMessages.Add($"VIDEO_TS structure verified: {valResult.TitleSetNumbers.Count} title set(s), {valResult.FileCount} file(s), {CompilationItemViewModel.FormatBytes(valResult.TotalSizeBytes)}.");

                    var planner = new DvdVideoLayoutPlanner();
                    var videoTsDirInfo = new DirectoryInfo(actualVideoTsPath);
                    var fileEntries = videoTsDirInfo.GetFiles()
                        .Where(f => !DvdInputNormalizer.IsIgnoredUselessFile(f.FullName))
                        .Select(f => (IDvdVideoFileEntry)new FileSystemDvdVideoFileEntry(f))
                        .ToList();

                    var layout = planner.Plan(fileEntries, startLba: 0);
                    PopulateLayoutAndDualLayer(layout);
                    return;
                }
            }
        }

        // 2. Otherwise analyze with DvdInputNormalizer
        var analysis = DvdInputNormalizer.AnalyzeInputs(inputs);
        if (!analysis.CanAuthorOrBurn)
        {
            IsStructureValid = false;
            if (analysis.NormalizationNotes.Count > 0)
            {
                foreach (var note in analysis.NormalizationNotes)
                {
                    ValidationMessages.Add($"[Error] {note}");
                }
            }
            else
            {
                ValidationMessages.Add("Input does not contain valid DVD-Video structures or supported movie files.");
            }
            return;
        }

        if (analysis.Kind == DvdInputKind.CompliantVideoTs && analysis.IgnoredFiles.Count == 0)
        {
            string actualVideoTsPath = analysis.ExistingVideoTsPath!;
            var validator = new DvdVideoValidator();
            var valResult = validator.ValidateDirectory(actualVideoTsPath);
            RequiresAutoAuthoring = false;
            IsStructureValid = valResult.IsValid;
            TotalSizeBytes = valResult.TotalSizeBytes;
            TitleSetCount = valResult.TitleSetNumbers.Count;
            HasAudioTs = valResult.HasAudioTs;

            foreach (var err in valResult.Errors)
            {
                ValidationMessages.Add($"[Error] {err}");
            }
            foreach (var warn in valResult.Warnings)
            {
                ValidationMessages.Add($"[Warning] {warn}");
            }

            if (!valResult.IsValid) return;

            ValidationMessages.Add($"VIDEO_TS structure verified: {valResult.TitleSetNumbers.Count} title set(s), {valResult.FileCount} file(s), {CompilationItemViewModel.FormatBytes(valResult.TotalSizeBytes)}.");

            var planner = new DvdVideoLayoutPlanner();
            var videoTsDirInfo = new DirectoryInfo(actualVideoTsPath);
            var fileEntries = videoTsDirInfo.GetFiles()
                .Select(f => (IDvdVideoFileEntry)new FileSystemDvdVideoFileEntry(f))
                .ToList();

            var layout = planner.Plan(fileEntries, startLba: 0);
            PopulateLayoutAndDualLayer(layout);
            return;
        }

        if (analysis.RequiresAuthoring || analysis.IgnoredFiles.Count > 0)
        {
            RequiresAutoAuthoring = true;
            IsStructureValid = true;

            if (string.IsNullOrWhiteSpace(VolumeLabel) || VolumeLabel == "DVD_VIDEO")
            {
                VolumeLabel = analysis.SuggestedVolumeLabel;
            }
            if (string.IsNullOrWhiteSpace(MenuTitle) || MenuTitle == "DVD Video")
            {
                MenuTitle = analysis.SuggestedMenuTitle;
            }

            foreach (var note in analysis.NormalizationNotes)
            {
                ValidationMessages.Add($"[Info] {note}");
            }
            if (analysis.IgnoredFiles.Count > 0)
            {
                ValidationMessages.Add($"[Info] Ignored useless file(s): {string.Join(", ", analysis.IgnoredFiles.Select(Path.GetFileName))}");
            }

            // Build preview layout in ExtentLayoutRows using DvdVideoLayoutPlanner
            var previewEntries = new List<IDvdVideoFileEntry>();
            const int ifoBytes = 12288; // 6 sectors = 12,288 bytes
            previewEntries.Add(new DvdVideoMemoryFileEntry("VIDEO_TS.IFO", new byte[ifoBytes]));
            if (AutoCreatePlayMenu)
            {
                previewEntries.Add(new DvdVideoMemoryFileEntry("VIDEO_TS.VOB", new byte[32768])); // 16 sectors
            }
            previewEntries.Add(new DvdVideoMemoryFileEntry("VIDEO_TS.BUP", new byte[ifoBytes]));

            int count = Math.Max(1, analysis.MovieFilePaths.Count);
            for (int i = 0; i < count; i++)
            {
                int setNum = i + 1;
                long fileLength = 65536L;
                if (i < analysis.MovieFilePaths.Count)
                {
                    try
                    {
                        var fi = new FileInfo(analysis.MovieFilePaths[i]);
                        if (fi.Exists)
                        {
                            fileLength = fi.Length;
                        }
                    }
                    catch { }
                }
                long vobSize = Math.Max(65536L, fileLength);

                previewEntries.Add(new DvdVideoMemoryFileEntry($"VTS_{setNum:D2}_0.IFO", new byte[ifoBytes]));
                if (vobSize <= 10_000_000)
                {
                    previewEntries.Add(new DvdVideoMemoryFileEntry($"VTS_{setNum:D2}_1.VOB", new byte[(int)vobSize]));
                }
                else
                {
                    previewEntries.Add(new PreviewDvdVideoFileEntry($"VTS_{setNum:D2}_1.VOB", vobSize));
                }
                previewEntries.Add(new DvdVideoMemoryFileEntry($"VTS_{setNum:D2}_0.BUP", new byte[ifoBytes]));
            }

            var planner = new DvdVideoLayoutPlanner();
            var layout = planner.Plan(previewEntries, startLba: 0);

            TotalSizeBytes = previewEntries.Sum(e => e.SizeBytes);
            TitleSetCount = count;
            HasAudioTs = true;
            PopulateLayoutAndDualLayer(layout);
        }
    }

    /// <summary>
    /// Cancels any active DVD authoring, transcoding, ISO build, or burn operation.
    /// </summary>
    public void CancelActiveOperation()
    {
        if (_activeCts != null && !_activeCts.IsCancellationRequested)
        {
            StatusMessage = "Cancelling DVD-Video operation...";
            try
            {
                _activeCts.Cancel();
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Executes automated DVD authoring (menu generation and MPEG-2 transcoding) if required,
    /// populating temporary VIDEO_TS output.
    /// </summary>
    public async Task<DvdAuthoringResult> EnsureAuthoredAsync(CancellationToken cancellationToken = default)
    {
        List<string> inputs;
        if (SourceMediaFiles.Count > 0)
        {
            inputs = SourceMediaFiles.ToList();
        }
        else if (!string.IsNullOrWhiteSpace(VideoTsFolderPath))
        {
            inputs = VideoTsFolderPath
                .Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }
        else
        {
            inputs = new List<string>();
        }

        if (inputs.Count == 0)
        {
            return new DvdAuthoringResult
            {
                Success = false,
                ErrorMessage = "No input files specified for authoring."
            };
        }

        CleanupTempAuthoredDirectory();
        string tempAuthorDir = Path.Combine(Path.GetTempPath(), "IgnisDisc_DvdAuthor_" + Guid.NewGuid().ToString("N"));
        _tempAuthoredDirectory = tempAuthorDir;

        var options = new DvdAuthoringOptions
        {
            Standard = SelectedStandard,
            AspectRatio = SelectedAspectRatio,
            VolumeLabel = string.IsNullOrWhiteSpace(VolumeLabel) ? "DVD_VIDEO" : VolumeLabel,
            MenuTitle = string.IsNullOrWhiteSpace(MenuTitle) ? "DVD Video" : MenuTitle,
            AutoCreatePlayMenu = AutoCreatePlayMenu,
            UseFfmpegWhenAvailable = UseFfmpegWhenAvailable,
            CustomFfmpegPath = string.IsNullOrWhiteSpace(CustomFfmpegPath) ? null : CustomFfmpegPath
        };

        var authoringService = new DvdVideoAuthoringService();
        var progressHandler = new Progress<(double Percent, string Status)>(snap =>
        {
            BurnProgress = Math.Clamp(snap.Percent, 0, 100);
            StatusMessage = snap.Status;
        });

        bool manageBusy = !IsBusy;
        if (manageBusy)
        {
            IsBusy = true;
            BurnProgress = 1.0;
            StatusMessage = "Preparing DVD-Video authoring pipeline...";
        }

        try
        {
            var result = await authoringService.AuthorAsync(inputs, tempAuthorDir, options, progressHandler, cancellationToken);

            foreach (var note in result.LogNotes)
            {
                ValidationMessages.Add($"[Authoring] {note}");
            }

            if (result.Success)
            {
                LastAuthoredVideoTsPath = result.VideoTsDirectory;
                TotalSizeBytes = result.TotalSizeBytes;
                TotalSectors = result.TotalSectors;
                TitleSetCount = result.TitleCount;
                HasAudioTs = true;

                if (Directory.Exists(result.VideoTsDirectory))
                {
                    var dirInfo = new DirectoryInfo(result.VideoTsDirectory);
                    var fileEntries = dirInfo.GetFiles()
                        .Select(f => (IDvdVideoFileEntry)new FileSystemDvdVideoFileEntry(f))
                        .ToList();

                    var planner = new DvdVideoLayoutPlanner();
                    var layout = planner.Plan(fileEntries, startLba: 0);
                    ExtentLayoutRows.Clear();
                    CandidateLayerBreakLbas.Clear();
                    PopulateLayoutAndDualLayer(layout);
                }
            }
            else if (!string.IsNullOrEmpty(result.ErrorMessage))
            {
                ValidationMessages.Add($"[Error] {result.ErrorMessage}");
            }

            return result;
        }
        finally
        {
            if (manageBusy)
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>
    /// Builds a DVD-Video compliant ISO image stream (UDF 1.02 + ISO 9660 Bridge) with
    /// 32 KB IFO/BUP ECC gap alignment.
    /// </summary>
    public async Task<string> BuildDvdIsoAsync(string? outputPath = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            outputPath = Path.Combine(Path.GetTempPath(), $"{VolumeLabel}_{DateTime.Now:yyyyMMddHHmmss}.iso");
        }

        string? dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (!IsStructureValid)
        {
            ValidateAndPlanLayout();
            if (!IsStructureValid)
            {
                throw new InvalidOperationException($"Cannot build DVD ISO: structure is not valid. {string.Join("; ", ValidationMessages)}");
            }
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activeCts = cts;

        IsBusy = true;
        BurnProgress = 1.0;
        try
        {
            if (RequiresAutoAuthoring && (string.IsNullOrEmpty(LastAuthoredVideoTsPath) || !Directory.Exists(LastAuthoredVideoTsPath)))
            {
                var authResult = await EnsureAuthoredAsync(cts.Token);
                if (!authResult.Success)
                {
                    throw new InvalidOperationException(authResult.ErrorMessage ?? "DVD authoring failed.");
                }
            }

            StatusMessage = "Authoring DVD-Video ISO image...";
            string actualVideoTsPath = !string.IsNullOrEmpty(LastAuthoredVideoTsPath) && Directory.Exists(LastAuthoredVideoTsPath)
                ? LastAuthoredVideoTsPath
                : ResolveVideoTsPath(VideoTsFolderPath);

            var (root, videoTsDir) = CreateDvdCompilationTree(actualVideoTsPath);

            var planner = new DvdVideoLayoutPlanner();
            planner.ApplyToCompilation(videoTsDir, startLba: 0);

            var buildOptions = new IsoBuildOptions(
                VolumeLabel: string.IsNullOrWhiteSpace(VolumeLabel) ? "DVD_VIDEO" : VolumeLabel,
                IncludeIso9660: true,
                IncludeUdf: true,
                IncludeJoliet: false,
                UdfRevision: 0x0102);

            await IsoUdfImageBuilder.BuildToFileAsync(root, outputPath, buildOptions, null, cts.Token);
            BurnProgress = 100.0;
            LastBuiltIsoPath = outputPath;
            StatusMessage = $"DVD-Video ISO built successfully: {Path.GetFileName(outputPath)}";
            return outputPath;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "DVD-Video ISO build cancelled.";
            return string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error building DVD ISO: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"BuildDvdIsoAsync error: {ex}");
            return string.Empty;
        }
        finally
        {
            _activeCts = null;
            IsBusy = false;
        }
    }

    /// <summary>
    /// Burns the DVD-Video authoring compilation to the target optical recorder drive.
    /// </summary>
    public async Task<DataBurnResult> StartDvdBurnAsync(object? parameter = null, CancellationToken cancellationToken = default)
    {
        if (!IsStructureValid)
        {
            ValidateAndPlanLayout();
            if (!IsStructureValid)
            {
                return DataBurnResult.Failed($"Cannot burn DVD: VIDEO_TS structure is invalid. {string.Join("; ", ValidationMessages)}");
            }
        }

        // Resolve and confirm the target optical recorder FIRST before running lengthy transcoding/authoring
        IDiscRecorderBackend? targetRecorder = null;
        bool replaceAllowed = false; // true only after the user saw the "will replace files" warning and clicked Burn
        if (parameter is IDiscRecorderBackend rec)
        {
            targetRecorder = rec;
        }
        else if (parameter is string driveId && !string.IsNullOrWhiteSpace(driveId))
        {
            targetRecorder = AvailableRecorders.FirstOrDefault(r => string.Equals(r.UniqueId, driveId, StringComparison.OrdinalIgnoreCase))
                             ?? DeviceService.GetRecorders().FirstOrDefault(r => string.Equals(r.UniqueId, driveId, StringComparison.OrdinalIgnoreCase));
        }
        else if (CdBurner.App.Views.BurnTargetDialog.IsInteractiveSession)
        {
            var confirmed = CdBurner.App.Views.BurnTargetDialog.ConfirmRecorderSelection(
                _deviceService,
                AvailableRecorders,
                SelectedRecorder,
                $"Burn DVD-Video Disc ({VolumeLabel})");

            if (confirmed == null)
            {
                StatusMessage = "DVD-Video burn cancelled by user.";
                return DataBurnResult.Failed(StatusMessage);
            }

            SelectedRecorder = confirmed;
            targetRecorder = confirmed;
            replaceAllowed = true;
        }

        targetRecorder ??= SelectedRecorder;
        if (targetRecorder == null)
        {
            RefreshDrives();
            targetRecorder = SelectedRecorder;
        }

        if (targetRecorder == null)
        {
            return DataBurnResult.Failed("No optical recorder selected or available.");
        }

        // Auto-mount blank DVD media on virtual drive if needed
        if (targetRecorder is VirtualDiscRecorder vRec)
        {
            var mediaStatus = vRec.QueryMediaStatus();
            if (!mediaStatus.MediaPresent || (!mediaStatus.IsBlank && !mediaStatus.IsAppendable && !mediaStatus.IsRewritable))
            {
                vRec.InsertMedia(VirtualDiscMedium.CreateBlankDvdR47());
            }
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activeCts = cts;

        IsBusy = true;
        BurnProgress = 1.0;

        try
        {
            if (RequiresAutoAuthoring && (string.IsNullOrEmpty(LastAuthoredVideoTsPath) || !Directory.Exists(LastAuthoredVideoTsPath)))
            {
                StatusMessage = "Transcoding & authoring DVD-Video structure...";
                var authResult = await EnsureAuthoredAsync(cts.Token);
                if (!authResult.Success)
                {
                    StatusMessage = $"Authoring failed: {authResult.ErrorMessage}";
                    return DataBurnResult.Failed(authResult.ErrorMessage ?? "DVD authoring failed.");
                }
            }

            StatusMessage = $"Building ISO & burning DVD-Video disc to {targetRecorder.VolumePath} ({targetRecorder.ProductId})...";
            BurnProgress = 0;

            string actualVideoTsPath = !string.IsNullOrEmpty(LastAuthoredVideoTsPath) && Directory.Exists(LastAuthoredVideoTsPath)
                ? LastAuthoredVideoTsPath
                : ResolveVideoTsPath(VideoTsFolderPath);

            var (root, videoTsDir) = CreateDvdCompilationTree(actualVideoTsPath);

            var planner = new DvdVideoLayoutPlanner();
            planner.ApplyToCompilation(videoTsDir, startLba: 0);

            string effectiveLabel = !string.IsNullOrWhiteSpace(VolumeLabel) ? VolumeLabel : "DVD_VIDEO";
            var buildOptions = new IsoBuildOptions(
                VolumeLabel: effectiveLabel,
                IncludeIso9660: true,
                IncludeUdf: true,
                IncludeJoliet: false,
                UdfRevision: 0x0102);

            var burnOptions = new DataBurnOptions
            {
                VolumeLabel = effectiveLabel,
                CloseDisc = true,
                VerifyAfterBurn = VerifyAfterBurn,
                EjectAfterBurn = EjectAfterBurn,
                Speed = SelectedWriteSpeed,
                OverwriteExistingData = replaceAllowed
            };

            var burnService = new DataBurnService(_burnEngine);
            var progressHandler = new Progress<BurnProgressSnapshot>(snap =>
            {
                if (snap.TotalLba > 0)
                {
                    BurnProgress = Math.Clamp((double)snap.CurrentLba / snap.TotalLba * 100.0, 0, 100);
                }
                else
                {
                    BurnProgress = Math.Clamp(snap.BufferLevelPercent, 0, 100);
                }

                if (!string.IsNullOrEmpty(snap.CurrentActionDescription))
                {
                    StatusMessage = $"{snap.CurrentActionDescription} ({BurnProgress:F0}%)";
                }
            });

            var result = await burnService.BurnCompilationAsync(targetRecorder, root, burnOptions, buildOptions, progressHandler, cts.Token);
            if (result.Success)
            {
                BurnProgress = 100.0;
                StatusMessage = $"DVD-Video burn completed successfully ({result.SectorsWritten:N0} sectors written to {targetRecorder.VolumePath}).";
            }
            else
            {
                StatusMessage = $"Burn failed: {result.ErrorMessage}";
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "DVD-Video burn cancelled.";
            return DataBurnResult.Cancelled(StatusMessage);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"StartDvdBurnAsync error: {ex}");
            return DataBurnResult.Failed(ex.Message, ex);
        }
        finally
        {
            CleanupTempAuthoredDirectory();
            _activeCts = null;
            IsBusy = false;
        }
    }

    #endregion

    #region Helpers

    private void PopulateLayoutAndDualLayer(DvdVideoLayout layout)
    {
        TotalSectors = layout.TotalSectors;

        foreach (var placement in layout.Placements)
        {
            var row = new DvdExtentAlignmentRow
            {
                FileName = placement.FileName,
                StartLba = placement.StartLba.Value,
                EndLba = placement.EndLba.Value,
                SectorCount = placement.SectorCount,
                SizeBytes = placement.SizeBytes,
                IsPadding = placement.IsPadding,
                EccPaddingSectors = placement.IsPadding ? placement.SectorCount : 0,
                Status = placement.IsPadding ? "32 KB ECC Alignment Padding" : "Compliant Extent"
            };
            ExtentLayoutRows.Add(row);
        }

        // 3. Verify IFO/BUP ECC Separation
        if (layout.VerifyIfoBupSeparation(out var sepError))
        {
            ValidationMessages.Add("32 KB ECC block separation verified: all .BUP files are safe from .IFO write collisions.");
        }
        else
        {
            ValidationMessages.Add($"[Warning] ECC separation: {sepError}");
        }

        // 4. Dual-Layer assessment
        IsDualLayerRequired = DvdLayerBreakCalculator.RequiresLayerBreak(layout.TotalSectors);
        if (IsDualLayerRequired)
        {
            var candidates = DvdLayerBreakCalculator.CalculateCandidates(layout.TotalSectors, Array.Empty<int>());
            if (candidates.Count == 0)
            {
                var opt = DvdLayerBreakCalculator.SelectOptimalBreak(layout.TotalSectors);
                if (opt != null)
                {
                    CandidateLayerBreakLbas.Add(opt.BreakLba);
                    SelectedLayerBreakLba = opt.BreakLba;
                }
            }
            else
            {
                foreach (var c in candidates)
                {
                    CandidateLayerBreakLbas.Add(c.BreakLba);
                }
                SelectedLayerBreakLba = candidates[0].BreakLba;
            }

            ValidationMessages.Add($"Dual-Layer DVD-9 required ({layout.TotalSectors:N0} sectors). Optimal Layer Break LBA: {SelectedLayerBreakLba:N0}.");
        }
        else
        {
            SelectedLayerBreakLba = 0;
            ValidationMessages.Add($"Single-Layer DVD-5 sufficient ({layout.TotalSectors:N0} sectors <= {DvdLayerBreakCalculator.Dvd5SectorLimit:N0}).");
        }
    }

    private void RefreshDrives()
    {
        var recorders = _deviceService.GetRecorders();
        AvailableRecorders.Clear();
        foreach (var r in recorders)
        {
            AvailableRecorders.Add(r);
        }

        SelectedRecorder = AvailableRecorders.FirstOrDefault(r => !r.IsVirtual)
                           ?? AvailableRecorders.FirstOrDefault(r => string.Equals(r.UniqueId, "VDRV-01", StringComparison.OrdinalIgnoreCase))
                           ?? AvailableRecorders.FirstOrDefault();
    }

    private void ResetState()
    {
        CleanupTempAuthoredDirectory();
        IsStructureValid = false;
        RequiresAutoAuthoring = false;
        LastAuthoredVideoTsPath = null;
        ValidationMessages.Clear();
        ExtentLayoutRows.Clear();
        CandidateLayerBreakLbas.Clear();
        SelectedLayerBreakLba = 0;
        IsDualLayerRequired = false;
        TotalSizeBytes = 0;
        TotalSectors = 0;
        TitleSetCount = 0;
        HasAudioTs = false;
    }

    private void ExecuteAddMovieFiles(object? parameter)
    {
        if (parameter is IEnumerable<string> paths)
        {
            AddMovieFiles(paths);
            return;
        }
        if (parameter is string single && !string.IsNullOrWhiteSpace(single))
        {
            AddMovieFiles([single]);
            return;
        }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Movie Video Files",
            Multiselect = true,
            Filter = "Video Files (*.mkv;*.mp4;*.avi;*.mov;*.wmv;*.mpg;*.vob;*.ts)|*.mkv;*.mp4;*.avi;*.mov;*.wmv;*.mpg;*.vob;*.ts;*.m4v;*.webm;*.m2ts|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true && dlg.FileNames != null && dlg.FileNames.Length > 0)
        {
            AddMovieFiles(dlg.FileNames);
        }
    }

    private void ExecuteSelectVideoTsFolder(object? parameter)
    {
        if (parameter is string path && !string.IsNullOrWhiteSpace(path))
        {
            VideoTsFolderPath = path;
            return;
        }

        // Interactive folder dialog
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select VIDEO_TS or DVD Root Directory",
            Multiselect = false
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.FolderName))
        {
            VideoTsFolderPath = dialog.FolderName;
        }
    }

    private static string ResolveVideoTsPath(string folderPath)
    {
        var dirInfo = new DirectoryInfo(folderPath);
        if (string.Equals(dirInfo.Name, "VIDEO_TS", StringComparison.OrdinalIgnoreCase))
        {
            return dirInfo.FullName;
        }

        string subVideoTs = Path.Combine(folderPath, "VIDEO_TS");
        if (Directory.Exists(subVideoTs))
        {
            return subVideoTs;
        }

        return folderPath;
    }

    private static (CompilationDirectory Root, CompilationDirectory VideoTsDir) CreateDvdCompilationTree(string actualVideoTsPath)
    {
        var root = CompilationDirectory.CreateRoot();
        var videoTsDir = root.AddDirectory("VIDEO_TS");
        PopulateDirectoryFromDisk(videoTsDir, actualVideoTsPath);

        // Check for sibling or parent AUDIO_TS; guarantee AUDIO_TS always exists
        bool hasAudioTs = false;
        var dirInfo = new DirectoryInfo(actualVideoTsPath);
        string? parent = dirInfo.Parent?.FullName;
        if (!string.IsNullOrEmpty(parent))
        {
            string siblingAudioTs = Path.Combine(parent, "AUDIO_TS");
            if (Directory.Exists(siblingAudioTs))
            {
                var audioTsDir = root.AddDirectory("AUDIO_TS");
                PopulateDirectoryFromDisk(audioTsDir, siblingAudioTs);
                hasAudioTs = true;
            }
        }

        if (!hasAudioTs)
        {
            root.AddDirectory("AUDIO_TS");
        }

        return (root, videoTsDir);
    }

    private static void PopulateDirectoryFromDisk(CompilationDirectory targetDir, string diskDir)
    {
        var di = new DirectoryInfo(diskDir);
        if (!di.Exists) return;

        foreach (var file in di.GetFiles())
        {
            if (DvdInputNormalizer.IsIgnoredUselessFile(file.FullName))
            {
                continue;
            }
            targetDir.AddFile(new CompilationFile(file.FullName, file.Length));
        }

        foreach (var sub in di.GetDirectories())
        {
            var child = targetDir.AddDirectory(sub.Name);
            PopulateDirectoryFromDisk(child, sub.FullName);
        }
    }

    private sealed class PreviewDvdVideoFileEntry : IDvdVideoFileEntry
    {
        public string Name { get; }
        public long SizeBytes { get; }

        public PreviewDvdVideoFileEntry(string name, long sizeBytes)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            SizeBytes = sizeBytes;
        }

        public Stream OpenRead() => Stream.Null;
    }

    #endregion
}
