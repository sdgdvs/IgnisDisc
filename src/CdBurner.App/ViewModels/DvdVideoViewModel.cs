using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using CdBurner.App.Mvvm;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Burning;
using CdBurner.Core.Compilation;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
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
    /// Physical path to the VIDEO_TS directory or root folder containing VIDEO_TS.
    /// </summary>
    public string VideoTsFolderPath
    {
        get => _videoTsFolderPath;
        set
        {
            if (SetProperty(ref _videoTsFolderPath, value))
            {
                if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value))
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
    public string ValidationStatusText => IsStructureValid ? "DVD-Video Compliant" : "Structure Invalid";

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
        set => SetProperty(ref _isBusy, value);
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

    public RelayCommand SelectVideoTsFolderCommand { get; }
    public RelayCommand ValidateAndPlanLayoutCommand { get; }
    public AsyncRelayCommand BuildDvdIsoCommand { get; }
    public AsyncRelayCommand StartDvdBurnCommand { get; }

    #endregion

    public DvdVideoViewModel(
        IDiscDeviceService? deviceService = null,
        IBurnEngineBackend? burnEngine = null)
    {
        _deviceService = deviceService ?? new DiscDeviceManager(enablePhysicalEnumeration: false);
        _burnEngine = burnEngine ?? new VirtualBurnEngine();

        SelectVideoTsFolderCommand = new RelayCommand(ExecuteSelectVideoTsFolder);
        ValidateAndPlanLayoutCommand = new RelayCommand(_ => ValidateAndPlanLayout());
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

        RefreshDrives();
    }

    #region Operations

    /// <summary>
    /// Validates the VIDEO_TS structure at <see cref="VideoTsFolderPath"/> and computes the
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

        if (string.IsNullOrWhiteSpace(VideoTsFolderPath))
        {
            IsStructureValid = false;
            ValidationMessages.Add("Please specify a VIDEO_TS folder path.");
            return;
        }

        if (!Directory.Exists(VideoTsFolderPath))
        {
            IsStructureValid = false;
            ValidationMessages.Add($"Directory does not exist: {VideoTsFolderPath}");
            return;
        }

        string actualVideoTsPath = ResolveVideoTsPath(VideoTsFolderPath);

        // 1. Structure validation
        var validator = new DvdVideoValidator();
        var valResult = validator.ValidateDirectory(actualVideoTsPath);
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

        if (!valResult.IsValid)
        {
            return;
        }

        ValidationMessages.Add($"VIDEO_TS structure verified: {valResult.TitleSetNumbers.Count} title set(s), {valResult.FileCount} file(s), {CompilationItemViewModel.FormatBytes(valResult.TotalSizeBytes)}.");

        // 2. Physical Layout Planning with 32 KB ECC gap alignment
        var planner = new DvdVideoLayoutPlanner();
        var videoTsDirInfo = new DirectoryInfo(actualVideoTsPath);
        var fileEntries = videoTsDirInfo.GetFiles()
            .Select(f => (IDvdVideoFileEntry)new FileSystemDvdVideoFileEntry(f))
            .ToList();

        var layout = planner.Plan(fileEntries, startLba: 0);
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

        IsBusy = true;
        StatusMessage = "Authoring DVD-Video ISO image...";
        try
        {
            string actualVideoTsPath = ResolveVideoTsPath(VideoTsFolderPath);
            var (root, videoTsDir) = CreateDvdCompilationTree(actualVideoTsPath);

            var planner = new DvdVideoLayoutPlanner();
            planner.ApplyToCompilation(videoTsDir, startLba: 0);

            var buildOptions = new IsoBuildOptions(
                VolumeLabel: string.IsNullOrWhiteSpace(VolumeLabel) ? "DVD_VIDEO" : VolumeLabel,
                IncludeIso9660: true,
                IncludeUdf: true,
                IncludeJoliet: false,
                UdfRevision: 0x0102);

            await IsoUdfImageBuilder.BuildToFileAsync(root, outputPath, buildOptions, null, cancellationToken);
            LastBuiltIsoPath = outputPath;
            StatusMessage = $"DVD-Video ISO built successfully: {Path.GetFileName(outputPath)}";
            return outputPath;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Burns the DVD-Video authoring compilation to the target optical recorder drive.
    /// </summary>
    public async Task<DataBurnResult> StartDvdBurnAsync(object? parameter = null, CancellationToken cancellationToken = default)
    {
        IDiscRecorderBackend? targetRecorder = null;
        if (parameter is IDiscRecorderBackend rec)
        {
            targetRecorder = rec;
        }
        else if (parameter is string driveId && !string.IsNullOrWhiteSpace(driveId))
        {
            targetRecorder = AvailableRecorders.FirstOrDefault(r => string.Equals(r.UniqueId, driveId, StringComparison.OrdinalIgnoreCase))
                             ?? DeviceService.GetRecorders().FirstOrDefault(r => string.Equals(r.UniqueId, driveId, StringComparison.OrdinalIgnoreCase));
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

        if (!IsStructureValid)
        {
            ValidateAndPlanLayout();
            if (!IsStructureValid)
            {
                return DataBurnResult.Failed($"Cannot burn DVD: VIDEO_TS structure is invalid. {string.Join("; ", ValidationMessages)}");
            }
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

        IsBusy = true;
        StatusMessage = $"Burning DVD-Video disc to {targetRecorder.UniqueId}...";
        BurnProgress = 0;

        try
        {
            string actualVideoTsPath = ResolveVideoTsPath(VideoTsFolderPath);
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
                VerifyAfterBurn = false
            };

            var burnService = new DataBurnService(_burnEngine);
            var progressHandler = new Progress<BurnProgressSnapshot>(snap =>
            {
                BurnProgress = Math.Clamp(snap.BufferLevelPercent, 0, 100);
                if (!string.IsNullOrEmpty(snap.CurrentActionDescription))
                {
                    StatusMessage = snap.CurrentActionDescription;
                }
            });

            var result = await burnService.BurnCompilationAsync(targetRecorder, root, burnOptions, buildOptions, progressHandler, cancellationToken);
            StatusMessage = result.Success ? "DVD-Video burn completed successfully." : $"Burn failed: {result.ErrorMessage}";
            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    #endregion

    #region Helpers

    private void RefreshDrives()
    {
        var recorders = _deviceService.GetRecorders();
        AvailableRecorders.Clear();
        foreach (var r in recorders)
        {
            AvailableRecorders.Add(r);
        }

        SelectedRecorder = AvailableRecorders.FirstOrDefault(r => string.Equals(r.UniqueId, "VDRV-01", StringComparison.OrdinalIgnoreCase))
                           ?? AvailableRecorders.FirstOrDefault();
    }

    private void ResetState()
    {
        IsStructureValid = false;
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

        // Check for sibling or parent AUDIO_TS
        var dirInfo = new DirectoryInfo(actualVideoTsPath);
        string? parent = dirInfo.Parent?.FullName;
        if (!string.IsNullOrEmpty(parent))
        {
            string siblingAudioTs = Path.Combine(parent, "AUDIO_TS");
            if (Directory.Exists(siblingAudioTs))
            {
                var audioTsDir = root.AddDirectory("AUDIO_TS");
                PopulateDirectoryFromDisk(audioTsDir, siblingAudioTs);
            }
        }

        return (root, videoTsDir);
    }

    private static void PopulateDirectoryFromDisk(CompilationDirectory targetDir, string diskDir)
    {
        var di = new DirectoryInfo(diskDir);
        if (!di.Exists) return;

        foreach (var file in di.GetFiles())
        {
            targetDir.AddFile(new CompilationFile(file.FullName, file.Length));
        }

        foreach (var sub in di.GetDirectories())
        {
            var child = targetDir.AddDirectory(sub.Name);
            PopulateDirectoryFromDisk(child, sub.FullName);
        }
    }

    #endregion
}
