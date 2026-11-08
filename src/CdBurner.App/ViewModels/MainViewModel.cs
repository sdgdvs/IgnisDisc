using System.Collections.ObjectModel;
using System.Windows.Input;
using CdBurner.App.Mvvm;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Compilation;
using CdBurner.Core.Persistence;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;

namespace CdBurner.App.ViewModels;

/// <summary>
/// Root view model for CdBurner WPF Fluent Shell, managing hardware optical drives,
/// virtual media mounting, module workspace navigation, and compilation capacity assessment.
/// </summary>
public class MainViewModel : ObservableObject, IDisposable
{
    private readonly IDiscDeviceService _deviceService;
    private readonly IBurnEngineBackend _burnEngine;
    private readonly bool _ownsDeviceService;
    private readonly Dictionary<NavigationTab, WorkspaceViewModel> _workspaces = new();
    private readonly DataBurnViewModel _dataBurn;
    private readonly DvdVideoViewModel _dvdVideo;
    private readonly AudioBurnViewModel _audioBurn;
    private readonly ImageStudioViewModel _imageStudio;
    private readonly ArchiveViewModel _archive;
    private readonly DriveUtilitiesViewModel _driveUtilities;
    private readonly VirtualDriveViewModel _virtualDrive;
    private readonly SettingsViewModel _settings;

    private IDiscRecorderBackend? _selectedRecorder;
    private bool _syncingRecorder;
    private bool _hadPhysicalRecorder;
    private MediaStatusDto _currentMediaStatus = new() { MediaType = DiscMediaType.NoMedia, MediaPresent = false };
    private WriteSpeedDescriptor? _selectedWriteSpeed;
    private NavigationTab _selectedNavigationTab = (NavigationTab)(-1);
    private object? _currentWorkspaceViewModel;
    private CapacityAssessment _currentCapacity;
    private System.Windows.Threading.DispatcherTimer? _mediaPollTimer;
    private bool _isPollingMedia;
    private bool _disposed;

    #region Properties

    /// <summary>
    /// Gets the Data Disc mastering workspace view model.
    /// </summary>
    public DataBurnViewModel DataBurn => _dataBurn;

    /// <summary>
    /// Gets the DVD-Video authoring workspace view model.
    /// </summary>
    public DvdVideoViewModel DvdVideo => _dvdVideo;

    /// <summary>
    /// Gets the Audio CD mastering workspace view model.
    /// </summary>
    public AudioBurnViewModel AudioBurn => _audioBurn;

    /// <summary>
    /// Gets the Disc Image Studio workspace view model.
    /// </summary>
    public ImageStudioViewModel ImageStudio => _imageStudio;

    /// <summary>
    /// Gets the Archive Studio workspace view model.
    /// </summary>
    public ArchiveViewModel Archive => _archive;

    /// <summary>
    /// Gets the Drive Utilities and Diagnostics workspace view model.
    /// </summary>
    public DriveUtilitiesViewModel DriveUtilities => _driveUtilities;

    /// <summary>
    /// Gets the Virtual Drive Simulator manager workspace view model.
    /// </summary>
    public VirtualDriveViewModel VirtualDrive => _virtualDrive;

    /// <summary>
    /// Gets the Application Settings configuration workspace view model.
    /// </summary>
    public SettingsViewModel Settings => _settings;

    /// <summary>
    /// Gets the underlying disc device enumeration service.
    /// </summary>
    public IDiscDeviceService DeviceService => _deviceService;

    /// <summary>
    /// Gets the underlying optical burn engine backend.
    /// </summary>
    public IBurnEngineBackend BurnEngine => _burnEngine;

    /// <summary>
    /// Available optical recorders (physical and virtual) discovered on the system.
    /// </summary>
    public ObservableCollection<IDiscRecorderBackend> AvailableRecorders { get; } = new();

    /// <summary>
    /// Supported write speed descriptors for the currently selected recorder and media.
    /// </summary>
    public ObservableCollection<WriteSpeedDescriptor> AvailableWriteSpeeds { get; } = new();

    /// <summary>
    /// Currently selected optical recorder backend.
    /// </summary>
    public IDiscRecorderBackend? SelectedRecorder
    {
        get => _selectedRecorder;
        set
        {
            if (SetProperty(ref _selectedRecorder, value))
            {
                bool prevSync = _syncingRecorder;
                _syncingRecorder = true;
                try
                {
                    if (_dataBurn != null) _dataBurn.SelectedRecorder = value;
                    if (_dvdVideo != null) _dvdVideo.SelectedRecorder = value;
                    if (_audioBurn != null) _audioBurn.SelectedRecorder = value;
                    if (_imageStudio != null) _imageStudio.SelectedRecorder = value;
                    if (_archive != null) _archive.SelectedRecorder = value;
                    if (_driveUtilities != null) _driveUtilities.SelectedRecorder = value;
                    if (_virtualDrive != null) _virtualDrive.SelectedRecorder = value;
                    if (_settings != null) _settings.SelectedRecorder = value;
                }
                finally
                {
                    _syncingRecorder = prevSync;
                }

                UpdateMediaStatus();
                OnPropertyChanged(nameof(IsDriveVirtual));
                OnPropertyChanged(nameof(DriveStatusSummary));
            }
        }
    }

    /// <summary>
    /// Current media status and physical geometry for the selected recorder.
    /// </summary>
    public MediaStatusDto CurrentMediaStatus
    {
        get => _currentMediaStatus;
        set
        {
            if (SetProperty(ref _currentMediaStatus, value))
            {
                OnPropertyChanged(nameof(MediaBadgeText));
                OnPropertyChanged(nameof(MediaDetailsText));
                OnPropertyChanged(nameof(IsMediaPresent));
                OnPropertyChanged(nameof(IsBlankMedia));
            }
        }
    }

    /// <summary>
    /// Currently selected write speed for burning operations.
    /// </summary>
    public WriteSpeedDescriptor? SelectedWriteSpeed
    {
        get => _selectedWriteSpeed;
        set
        {
            if (SetProperty(ref _selectedWriteSpeed, value))
            {
                if (_dataBurn != null) _dataBurn.SelectedWriteSpeed = value;
                if (_audioBurn != null) _audioBurn.SelectedWriteSpeed = value;
                if (_dvdVideo != null) _dvdVideo.SelectedWriteSpeed = value;
                if (_imageStudio != null) _imageStudio.SelectedWriteSpeed = value;
            }
        }
    }

    /// <summary>
    /// Indicates whether any burn, erase, or surface scan operation is actively executing.
    /// </summary>
    public bool IsAnyBurnActive =>
        (_dataBurn?.IsBusy ?? false) ||
        (_audioBurn?.IsBusy ?? false) ||
        (_dvdVideo?.IsBusy ?? false) ||
        (_imageStudio?.IsBusy ?? false) ||
        (_archive?.IsBusy ?? false) ||
        (_driveUtilities?.IsBusy ?? false);

    /// <summary>
    /// Safely cancels all running burns/scans and stops optical drives from spinning.
    /// </summary>
    public void CancelAllActiveOperations()
    {
        try { _dataBurn?.CancelActiveBurn(); } catch { }
        try { _dvdVideo?.CancelActiveOperation(); } catch { }
        try { _audioBurn?.CancelActiveBurn(); } catch { }
        try { _imageStudio?.CancelActiveOperation(); } catch { }
        try { _archive?.CancelActiveOperation(); } catch { }
        try { _driveUtilities?.CancelActiveOperation(); } catch { }

        if (_selectedRecorder != null)
        {
            try
            {
                _selectedRecorder.ReleaseExclusiveAccess();
            }
            catch
            {
            }

            if (!_selectedRecorder.IsVirtual)
            {
                CdBurner.Hardware.Scsi.PhysicalSectorReader.TrySpinDown(_selectedRecorder.VolumePath);
            }
        }
    }

    /// <summary>
    /// Currently selected module navigation tab in the left rail.
    /// </summary>
    public NavigationTab SelectedNavigationTab
    {
        get => _selectedNavigationTab;
        set
        {
            if (SetProperty(ref _selectedNavigationTab, value))
            {
                UpdateCurrentWorkspace();
            }
        }
    }

    /// <summary>
    /// Active workspace view model presented in the central content host.
    /// </summary>
    public object? CurrentWorkspaceViewModel
    {
        get => _currentWorkspaceViewModel;
        set => SetProperty(ref _currentWorkspaceViewModel, value);
    }

    /// <summary>
    /// Real-time assessment of compilation size relative to target media capacity.
    /// </summary>
    public CapacityAssessment CurrentCapacity
    {
        get => _currentCapacity;
        set
        {
            if (SetProperty(ref _currentCapacity, value))
            {
                OnPropertyChanged(nameof(CapacityUsedMbText));
                OnPropertyChanged(nameof(CapacityMaxMbText));
                OnPropertyChanged(nameof(CapacityLbaText));
                OnPropertyChanged(nameof(CapacityStatusText));
                OnPropertyChanged(nameof(CapacityUsagePercent));
            }
        }
    }

    #endregion

    #region UI Helper Display Properties

    /// <summary>
    /// Indicates whether the selected recorder is a virtual software-defined drive.
    /// </summary>
    public bool IsDriveVirtual => _selectedRecorder?.IsVirtual ?? false;

    /// <summary>
    /// Formatted hardware summary string for the selected recorder.
    /// </summary>
    public string DriveStatusSummary
    {
        get
        {
            if (_selectedRecorder == null) return "No Optical Drive Selected";
            return $"{_selectedRecorder.VendorId} {_selectedRecorder.ProductId} ({_selectedRecorder.VolumePath})";
        }
    }

    /// <summary>
    /// Indicates whether media is currently inserted and ready in the drive tray.
    /// </summary>
    public bool IsMediaPresent => _currentMediaStatus.MediaPresent;

    /// <summary>
    /// Indicates whether the inserted medium is unrecorded / blank.
    /// </summary>
    public bool IsBlankMedia => _currentMediaStatus.IsBlank;

    /// <summary>
    /// Converts a <see cref="DiscMediaType"/> to standard user-facing optical branding string (e.g. CD-R, DVD-R).
    /// </summary>
    public static string FormatMediaTypeName(DiscMediaType mediaType) => mediaType.GetDisplayName();

    /// <summary>
    /// Badge label describing inserted media (e.g. "CD-R (700 MB)", "DVD-R (4.7 GB)", "Empty Tray").
    /// </summary>
    public string MediaBadgeText
    {
        get
        {
            if (!_currentMediaStatus.MediaPresent || _currentMediaStatus.MediaType == DiscMediaType.NoMedia)
            {
                return "Empty Tray";
            }

            string name = FormatMediaTypeName(_currentMediaStatus.MediaType);
            double mb = _currentMediaStatus.TotalCapacityBytes / (1024.0 * 1024.0);
            if (mb >= 1000)
            {
                return $"{name} ({(mb / 1024.0):F1} GB)";
            }
            return $"{name} ({mb:F0} MB)";
        }
    }

    /// <summary>
    /// Detailed media metrics including free sectors and rewritable capabilities.
    /// </summary>
    public string MediaDetailsText
    {
        get
        {
            if (!_currentMediaStatus.MediaPresent) return "Please insert or mount blank optical media.";
            var rw = _currentMediaStatus.IsRewritable ? "Rewritable" : "Write-Once";
            string name = FormatMediaTypeName(_currentMediaStatus.MediaType);
            return $"{name} | {rw} | Free: {_currentMediaStatus.FreeSectors:N0} LBAs";
        }
    }

    /// <summary>
    /// Megabytes consumed by compilation.
    /// </summary>
    public string CapacityUsedMbText => $"{(_currentCapacity.TotalBytes / (1024.0 * 1024.0)):F1} MB";

    /// <summary>
    /// Maximum nominal megabytes capacity of target medium.
    /// </summary>
    public string CapacityMaxMbText => $"{((long)_currentCapacity.MediaMaxSectors * OpticalConstants.DataSectorSize / (1024.0 * 1024.0)):F0} MB";

    /// <summary>
    /// Formatted LBA sector metrics for bottom status bar.
    /// </summary>
    public string CapacityLbaText => $"{_currentCapacity.TotalSectors:N0} / {_currentCapacity.MediaMaxSectors:N0} LBAs ({_currentCapacity.FreeSectors:N0} free)";

    /// <summary>
    /// Formatted status classification string.
    /// </summary>
    public string CapacityStatusText => _currentCapacity.Status switch
    {
        CapacityStatus.FitsComfortably => "Fits Comfortably",
        CapacityStatus.NearCapacityWarning => "Near Capacity (>95%)",
        CapacityStatus.OverburnRequired => "Overburn Required (CD-R 90m)",
        CapacityStatus.ExceedsMediaCapacity => "Exceeds Media Capacity",
        _ => "Ready"
    };

    /// <summary>
    /// Percentage consumed (0.0 to 100.0) for progress bar binding.
    /// </summary>
    public double CapacityUsagePercent => Math.Min(100.0, _currentCapacity.UsagePercent);

    #endregion

    #region Commands

    /// <summary>
    /// Refreshes optical drive list and selects default virtual drive if present.
    /// </summary>
    public RelayCommand RefreshDrivesCommand { get; }

    /// <summary>
    /// Ejects the drive tray or dismounts virtual medium.
    /// </summary>
    public RelayCommand EjectTrayCommand { get; }

    /// <summary>
    /// Closes the drive tray, reloading media if present.
    /// </summary>
    public RelayCommand CloseTrayCommand { get; }

    /// <summary>
    /// Quick-mounts a blank 80-minute / 700 MB CD-R virtual medium into the virtual recorder.
    /// </summary>
    public RelayCommand MountVirtualBlankCdRCommand { get; }

    /// <summary>
    /// Quick-mounts a blank 80-minute / 700 MB CD-RW rewritable virtual medium into the virtual recorder.
    /// </summary>
    public RelayCommand MountVirtualBlankCdRwCommand { get; }

    /// <summary>
    /// Quick-mounts a blank single-layer 4.7 GB DVD-R virtual medium into the virtual recorder.
    /// </summary>
    public RelayCommand MountVirtualBlankDvdRCommand { get; }

    /// <summary>
    /// Navigates to a specific module workspace in the shell.
    /// </summary>
    public RelayCommand NavigateCommand { get; }

    #endregion

    /// <summary>
    /// Initializes a new instance of <see cref="MainViewModel"/>.
    /// </summary>
    /// <param name="deviceService">Optional optical device enumeration service; defaults to <see cref="DiscDeviceManager"/>.</param>
    /// <param name="burnEngine">Optional burn engine backend; defaults to <see cref="VirtualBurnEngine"/>.</param>
    public MainViewModel(IDiscDeviceService? deviceService = null, IBurnEngineBackend? burnEngine = null)
    {
        if (deviceService == null)
        {
            _deviceService = new DiscDeviceManager(enablePhysicalEnumeration: false);
            _ownsDeviceService = true;
        }
        else
        {
            _deviceService = deviceService;
            _ownsDeviceService = false;
        }

        _burnEngine = burnEngine ?? new VirtualBurnEngine();
        _currentCapacity = CompilationCapacityCalculator.AssessCapacity(0, DiscMediaType.CdR);

        _dataBurn = new DataBurnViewModel(_deviceService, _burnEngine);
        _dvdVideo = new DvdVideoViewModel(_deviceService, _burnEngine);
        // One settings service shared by the Settings screen and the audio rip options, so both save the same file.
        var settingsService = new AppSettingsService();
        _audioBurn = new AudioBurnViewModel(_deviceService, _burnEngine, settingsService: settingsService);
        _imageStudio = new ImageStudioViewModel(_deviceService, _burnEngine);
        _archive = new ArchiveViewModel(_deviceService, _burnEngine);
        _driveUtilities = new DriveUtilitiesViewModel(_deviceService, _burnEngine);
        _virtualDrive = new VirtualDriveViewModel(_deviceService);
        _settings = new SettingsViewModel(settingsService, this);

        _dataBurn.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DataBurnViewModel.Capacity) && _selectedNavigationTab == NavigationTab.DataDisc)
            {
                CurrentCapacity = _dataBurn.Capacity;
            }
            else if (e.PropertyName == nameof(DataBurnViewModel.SelectedRecorder) && !_syncingRecorder && _dataBurn.SelectedRecorder != null)
            {
                SelectedRecorder = _dataBurn.SelectedRecorder;
            }
        };

        _dvdVideo.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DvdVideoViewModel.SelectedRecorder) && !_syncingRecorder && _dvdVideo.SelectedRecorder != null)
            {
                SelectedRecorder = _dvdVideo.SelectedRecorder;
            }
            else if ((e.PropertyName == nameof(DvdVideoViewModel.TotalSectors) || e.PropertyName == nameof(DvdVideoViewModel.IsDualLayerRequired)) && _selectedNavigationTab == NavigationTab.DvdVideo)
            {
                UpdateCapacityForActiveWorkspace();
            }
        };

        _audioBurn.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AudioBurnViewModel.SelectedRecorder) && !_syncingRecorder && _audioBurn.SelectedRecorder != null)
            {
                SelectedRecorder = _audioBurn.SelectedRecorder;
            }
        };

        _imageStudio.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ImageStudioViewModel.SelectedRecorder) && !_syncingRecorder && _imageStudio.SelectedRecorder != null)
            {
                SelectedRecorder = _imageStudio.SelectedRecorder;
            }
        };

        _archive.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ArchiveViewModel.SelectedRecorder) && !_syncingRecorder && _archive.SelectedRecorder != null)
            {
                SelectedRecorder = _archive.SelectedRecorder;
            }
            else if (e.PropertyName == nameof(ArchiveViewModel.IsBusy))
            {
                OnPropertyChanged(nameof(IsAnyBurnActive));
            }
        };

        _driveUtilities.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DriveUtilitiesViewModel.SelectedRecorder) && !_syncingRecorder && _driveUtilities.SelectedRecorder != null)
            {
                SelectedRecorder = _driveUtilities.SelectedRecorder;
            }
        };

        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.SelectedRecorder) && !_syncingRecorder && _settings.SelectedRecorder != null)
            {
                SelectedRecorder = _settings.SelectedRecorder;
            }
            else if (e.PropertyName == nameof(SettingsViewModel.EnableVirtualDrive))
            {
                ReloadRecordersList();
            }
        };

        _settings.SettingsSaved += (_, _) =>
        {
            var preferredDrive = _settings.SelectedRecorder;
            ReloadRecordersList();
            if (preferredDrive != null && AvailableRecorders.Contains(preferredDrive))
            {
                SelectedRecorder = preferredDrive;
            }
            ApplySettingsToWorkspaces();
            SelectDefaultWriteSpeed();
        };

        // Initialize Commands
        RefreshDrivesCommand = new RelayCommand(RefreshDrives);
        EjectTrayCommand = new RelayCommand(EjectTray, () => _selectedRecorder != null);
        CloseTrayCommand = new RelayCommand(CloseTray, () => _selectedRecorder != null);
        MountVirtualBlankCdRCommand = new RelayCommand(MountVirtualBlankCdR, CanMountVirtual);
        MountVirtualBlankCdRwCommand = new RelayCommand(MountVirtualBlankCdRw, CanMountVirtual);
        MountVirtualBlankDvdRCommand = new RelayCommand(MountVirtualBlankDvdR, CanMountVirtual);
        NavigateCommand = new RelayCommand(Navigate);

        // Initialize Module Workspaces
        InitializeWorkspaces();

        // Subscribe to device arrival/removal and media change events
        _deviceService.DeviceChanged += OnDeviceChanged;

        // Perform initial drive enumeration
        RefreshDrives();
        ApplySettingsToWorkspaces();

        // Set default workspace
        SelectedNavigationTab = NavigationTab.DataDisc;
    }

    /// <summary>
    /// Propagates user settings (default verification, eject, Burn-Free, and FFmpeg path) to child workspace view models.
    /// </summary>
    public void ApplySettingsToWorkspaces()
    {
        if (_settings == null) return;

        if (_settings.SelectedRecorder != null && AvailableRecorders.Contains(_settings.SelectedRecorder))
        {
            SelectedRecorder = _settings.SelectedRecorder;
        }

        if (_dataBurn != null)
        {
            _dataBurn.VerifyAfterBurn = _settings.VerifyAfterBurnByDefault;
            _dataBurn.EjectAfterBurn = _settings.EjectAfterBurnByDefault;
            _dataBurn.BufferUnderrunFreeEnabled = _settings.EnableBurnFreeByDefault;
        }

        if (_imageStudio != null)
        {
            _imageStudio.VerifyAfterBurn = _settings.VerifyAfterBurnByDefault;
        }

        if (_archive != null)
        {
            _archive.VerifyAfterBurn = _settings.VerifyAfterBurnByDefault;
        }

        _audioBurn?.ApplyRipSettings(_settings.SettingsService.CurrentSettings);

        if (_dvdVideo != null)
        {
            _dvdVideo.CustomFfmpegPath = _settings.FfmpegExecutablePath;
            _dvdVideo.VerifyAfterBurn = _settings.VerifyAfterBurnByDefault;
            _dvdVideo.EjectAfterBurn = _settings.EjectAfterBurnByDefault;
        }
    }

    private void InitializeWorkspaces()
    {
        _workspaces[NavigationTab.DataDisc] = new WorkspaceViewModel(
            NavigationTab.DataDisc,
            "Data Disc Studio",
            "Master ISO 9660, Joliet, and UDF data discs with optional El Torito bootable media.",
            "📁",
            _dataBurn);

        _workspaces[NavigationTab.AudioCd] = new WorkspaceViewModel(
            NavigationTab.AudioCd,
            "Audio CD Studio",
            "Author Red Book audio CDs with CD-Text metadata, gapless DAO, and DSP normalization.",
            "🎵",
            _audioBurn);

        _workspaces[NavigationTab.DvdVideo] = new WorkspaceViewModel(
            NavigationTab.DvdVideo,
            "DVD-Video Authoring",
            "Master compliant DVD-Video discs with 32 KB IFO/BUP sector gap alignment and dual-layer break analysis.",
            "🎬",
            _dvdVideo);

        _workspaces[NavigationTab.ImageStudio] = new WorkspaceViewModel(
            NavigationTab.ImageStudio,
            "Disc Image Studio",
            "Burn, create, rip, and verify ISO and BIN/CUE disc image archives.",
            "💿",
            _imageStudio);

        _workspaces[NavigationTab.Archive] = new WorkspaceViewModel(
            NavigationTab.Archive,
            "Archive Studio",
            "Plan, stage, burn, verify, and repair multi-disc archival sets with PAR2 recovery.",
            "🗄️",
            _archive);

        _workspaces[NavigationTab.DriveUtilities] = new WorkspaceViewModel(
            NavigationTab.DriveUtilities,
            "Drive Utilities & Diagnostics",
            "Inspect optical drive capabilities, erase rewritable media, bitsetting, and run surface benchmarks.",
            "🔧",
            _driveUtilities);

        _workspaces[NavigationTab.VirtualDrive] = new WorkspaceViewModel(
            NavigationTab.VirtualDrive,
            "Virtual Drive Manager",
            "Configure and inspect software-defined optical disc simulators and virtual media files.",
            "💾",
            _virtualDrive);

        _workspaces[NavigationTab.Settings] = new WorkspaceViewModel(
            NavigationTab.Settings,
            "Application Settings",
            "Configure default write speeds, overburn policies, temporary scratch directories, and logging.",
            "⚙️",
            _settings);
    }

    /// <summary>
    /// Re-enumerates optical recorders and updates the drive dropdown.
    /// </summary>
    public void RefreshDrives()
    {
        if (_deviceService is DiscDeviceManager ddm)
        {
            ddm.RefreshRecorders();
        }

        ReloadRecordersList();
    }

    private void ReloadRecordersList()
    {
        var rawRecorders = _deviceService.GetRecorders();
        bool hasAnyPhysical = rawRecorders.Any(r => !r.IsVirtual);

        // Order physical drives first so hardware drives are always top priority
        var ordered = rawRecorders
            .Where(r => !r.IsVirtual || _settings.EnableVirtualDrive || !hasAnyPhysical)
            .OrderBy(r => r.IsVirtual ? 1 : 0)
            .ThenBy(r => r.VolumePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var previousSelection = _selectedRecorder;

        bool prevSync = _syncingRecorder;
        _syncingRecorder = true;
        try
        {
            AvailableRecorders.Clear();
            _dataBurn.AvailableRecorders.Clear();
            _dvdVideo.AvailableRecorders.Clear();
            _audioBurn.AvailableRecorders.Clear();
            _imageStudio.AvailableRecorders.Clear();
            _archive.AvailableRecorders.Clear();
            _driveUtilities.AvailableRecorders.Clear();
            _virtualDrive.AvailableRecorders.Clear();
            _settings.AvailableRecorders.Clear();

            foreach (var r in ordered)
            {
                AvailableRecorders.Add(r);
                _dataBurn.AvailableRecorders.Add(r);
                _dvdVideo.AvailableRecorders.Add(r);
                _audioBurn.AvailableRecorders.Add(r);
                _imageStudio.AvailableRecorders.Add(r);
                _archive.AvailableRecorders.Add(r);
                _driveUtilities.AvailableRecorders.Add(r);
                _settings.AvailableRecorders.Add(r);
            }

            foreach (var r in rawRecorders)
            {
                _virtualDrive.AvailableRecorders.Add(r);
            }
        }
        finally
        {
            _syncingRecorder = prevSync;
        }

        var firstPhysical = AvailableRecorders.FirstOrDefault(r => !r.IsVirtual);
        bool newlyDiscoveredPhysical = firstPhysical != null && !_hadPhysicalRecorder;
        if (firstPhysical != null)
        {
            _hadPhysicalRecorder = true;
        }

        if (newlyDiscoveredPhysical && firstPhysical != null)
        {
            // Automatically switch from virtual drive to the newly discovered physical optical drive
            SelectedRecorder = firstPhysical;
        }
        else if (previousSelection != null && AvailableRecorders.Contains(previousSelection))
        {
            SelectedRecorder = previousSelection;
        }
        else
        {
            // Prefer physical optical drive first, then fallback to virtual simulator
            var target = firstPhysical
                         ?? AvailableRecorders.FirstOrDefault(r => string.Equals(r.UniqueId, "VDRV-01", StringComparison.OrdinalIgnoreCase))
                         ?? AvailableRecorders.FirstOrDefault();

            SelectedRecorder = target;
        }
    }

    /// <summary>
    /// Selects the best matching write speed based on the configured default multiplier (defaults to 16x).
    /// </summary>
    public void SelectDefaultWriteSpeed()
    {
        if (AvailableWriteSpeeds.Count == 0)
        {
            SelectedWriteSpeed = null;
            return;
        }

        int targetMultiplier = _settings.DefaultWriteSpeedMultiplier > 0 ? _settings.DefaultWriteSpeedMultiplier : 16;

        // 1. Try exact or near-match multiplier (e.g. 16.0x)
        var match = AvailableWriteSpeeds.FirstOrDefault(s => Math.Abs(s.Multiplier - targetMultiplier) < 0.5);
        if (match.BytesPerSecond > 0)
        {
            SelectedWriteSpeed = match;
            return;
        }

        // 2. Try the closest speed >= targetMultiplier (to avoid too-slow speeds on modern media)
        var higherOrEqual = AvailableWriteSpeeds
            .Where(s => s.Multiplier >= targetMultiplier)
            .OrderBy(s => s.Multiplier)
            .FirstOrDefault();
        if (higherOrEqual.BytesPerSecond > 0)
        {
            SelectedWriteSpeed = higherOrEqual;
            return;
        }

        // 3. Fallback: closest supported speed
        var closest = AvailableWriteSpeeds
            .OrderBy(s => Math.Abs(s.Multiplier - targetMultiplier))
            .FirstOrDefault();
        SelectedWriteSpeed = closest.BytesPerSecond > 0 ? closest : AvailableWriteSpeeds[^1];
    }

    /// <summary>
    /// Updates media status, supported write speeds, and capacity assessment for current recorder.
    /// </summary>
    public void UpdateMediaStatus()
    {
        if (_selectedRecorder != null)
        {
            CurrentMediaStatus = _selectedRecorder.QueryMediaStatus();

            // Populate write speeds
            AvailableWriteSpeeds.Clear();
            var caps = _selectedRecorder.GetCapabilities();
            if (caps.SupportedWriteSpeeds != null)
            {
                foreach (var speed in caps.SupportedWriteSpeeds)
                {
                    AvailableWriteSpeeds.Add(speed);
                }
            }

            if (AvailableWriteSpeeds.Count > 0)
            {
                if (!_selectedWriteSpeed.HasValue || !AvailableWriteSpeeds.Contains(_selectedWriteSpeed.Value))
                {
                    SelectDefaultWriteSpeed();
                }
            }
            else
            {
                SelectedWriteSpeed = null;
            }

            UpdateCapacityForActiveWorkspace();
        }
        else
        {
            CurrentMediaStatus = new MediaStatusDto
            {
                MediaType = DiscMediaType.NoMedia,
                MediaPresent = false
            };
            AvailableWriteSpeeds.Clear();
            SelectedWriteSpeed = null;
            CurrentCapacity = CompilationCapacityCalculator.AssessCapacity(0, DiscMediaType.CdR);
        }

        RefreshCommandStates();
    }

    private void UpdateCapacityForActiveWorkspace()
    {
        if (_selectedNavigationTab == NavigationTab.DvdVideo && _dvdVideo != null && _dvdVideo.TotalSectors > 0)
        {
            var dvdMediaType = _currentMediaStatus.MediaPresent && _currentMediaStatus.TotalSectors > OpticalConstants.Cd90MinSectors
                ? _currentMediaStatus.MediaType
                : (_dvdVideo.IsDualLayerRequired ? DiscMediaType.DvdPlusRDualLayer : DiscMediaType.DvdMinusR);

            int dvdMaxSectors = _currentMediaStatus.MediaPresent && _currentMediaStatus.TotalSectors > OpticalConstants.Cd90MinSectors
                ? _currentMediaStatus.TotalSectors
                : dvdMediaType.GetNominalCapacitySectors();

            if (dvdMaxSectors <= 0)
            {
                dvdMaxSectors = OpticalConstants.DvdMinusR5Sectors;
            }

            CurrentCapacity = CompilationCapacityCalculator.AssessCapacity(_dvdVideo.TotalSectors, dvdMediaType, dvdMaxSectors);
            return;
        }

        if (_selectedNavigationTab == NavigationTab.DataDisc && _dataBurn != null && _dataBurn.TotalBytes > 0)
        {
            CurrentCapacity = _dataBurn.Capacity;
            return;
        }

        var mediaType = _currentMediaStatus.MediaType != DiscMediaType.NoMedia && _currentMediaStatus.MediaType != DiscMediaType.Unknown
            ? _currentMediaStatus.MediaType
            : (_selectedNavigationTab == NavigationTab.DvdVideo ? DiscMediaType.DvdMinusR : DiscMediaType.CdR);

        int maxSectors = _currentMediaStatus.TotalSectors > 0
            ? _currentMediaStatus.TotalSectors
            : mediaType.GetNominalCapacitySectors();

        if (maxSectors <= 0)
        {
            maxSectors = _selectedNavigationTab == NavigationTab.DvdVideo
                ? OpticalConstants.DvdMinusR5Sectors
                : OpticalConstants.Cd700MbSectors;
        }

        int usedSectors = _currentMediaStatus.UsedSectors;
        CurrentCapacity = CompilationCapacityCalculator.AssessCapacity(usedSectors, mediaType, maxSectors);
    }

    /// <summary>
    /// Processes a Win32 window message (such as WM_DEVICECHANGE) from MainWindow.
    /// </summary>
    public void ProcessWindowMessage(int msg, nint wParam, nint lParam)
    {
        if (_deviceService is DiscDeviceManager ddm)
        {
            ddm.ChangeMonitor.ProcessMessage(msg, wParam, lParam);
        }

        if (msg == DeviceChangeMonitor.WM_DEVICECHANGE)
        {
            _ = PollHardwareMediaStatusOnceAsync();
        }
    }

    /// <summary>
    /// Starts a lightweight background timer that polls the selected physical optical drive for newly inserted
    /// or ejected blank/recorded optical media (including USB drives that do not emit volume arrival messages for blank discs).
    /// </summary>
    public void StartHardwareMediaMonitor()
    {
        if (_mediaPollTimer != null)
        {
            return;
        }

        _mediaPollTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3.0)
        };
        _mediaPollTimer.Tick += async (_, _) => await PollHardwareMediaStatusOnceAsync();
        _mediaPollTimer.Start();

        _ = PollHardwareMediaStatusOnceAsync();
    }

    private async Task PollHardwareMediaStatusOnceAsync()
    {
        if (_disposed || _isPollingMedia || IsAnyBurnActive)
        {
            return;
        }

        var recorder = _selectedRecorder;
        if (recorder == null || recorder.IsVirtual)
        {
            return;
        }

        _isPollingMedia = true;
        try
        {
            var prevStatus = _currentMediaStatus;
            var polled = await Task.Run(() =>
            {
                try
                {
                    var st = recorder.QueryMediaStatus();
                    bool hasChanged =
                        st.MediaPresent != prevStatus.MediaPresent ||
                        st.MediaType != prevStatus.MediaType ||
                        st.IsBlank != prevStatus.IsBlank ||
                        st.TotalSectors != prevStatus.TotalSectors ||
                        st.FreeSectors != prevStatus.FreeSectors;

                    DriveCapabilitiesDto? caps = hasChanged ? recorder.GetCapabilities() : null;
                    return (Status: (MediaStatusDto?)st, Caps: caps, Changed: hasChanged);
                }
                catch
                {
                    return (Status: (MediaStatusDto?)null, Caps: (DriveCapabilitiesDto?)null, Changed: false);
                }
            });

            if (polled.Status == null || !polled.Changed || _disposed || _selectedRecorder != recorder || IsAnyBurnActive)
            {
                return;
            }

            ApplyPolledMediaStatus(polled.Status, polled.Caps);
        }
        finally
        {
            _isPollingMedia = false;
        }
    }

    private void ApplyPolledMediaStatus(MediaStatusDto status, DriveCapabilitiesDto? caps)
    {
        CurrentMediaStatus = status;

        if (caps != null)
        {
            AvailableWriteSpeeds.Clear();
            if (caps.SupportedWriteSpeeds != null)
            {
                foreach (var speed in caps.SupportedWriteSpeeds)
                {
                    AvailableWriteSpeeds.Add(speed);
                }
            }

            if (AvailableWriteSpeeds.Count > 0)
            {
                if (!_selectedWriteSpeed.HasValue || !AvailableWriteSpeeds.Contains(_selectedWriteSpeed.Value))
                {
                    SelectDefaultWriteSpeed();
                }
            }
            else
            {
                SelectedWriteSpeed = null;
            }
        }

        UpdateCapacityForActiveWorkspace();
        RefreshCommandStates();
    }

    private void EjectTray()
    {
        _selectedRecorder?.EjectMedia();
        UpdateMediaStatus();
    }

    private void CloseTray()
    {
        _selectedRecorder?.CloseTray();
        UpdateMediaStatus();
    }

    private bool CanMountVirtual()
    {
        return _selectedRecorder is VirtualDiscRecorder || AvailableRecorders.Any(r => r.IsVirtual);
    }

    private VirtualDiscRecorder? ResolveVirtualRecorder()
    {
        var vRec = _selectedRecorder as VirtualDiscRecorder
                   ?? AvailableRecorders.OfType<VirtualDiscRecorder>().FirstOrDefault();

        if (vRec != null && _selectedRecorder != vRec)
        {
            SelectedRecorder = vRec;
        }

        return vRec;
    }

    private void MountVirtualBlankCdR()
    {
        var vRec = ResolveVirtualRecorder();
        if (vRec != null)
        {
            vRec.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());
            UpdateMediaStatus();
        }
    }

    private void MountVirtualBlankCdRw()
    {
        var vRec = ResolveVirtualRecorder();
        if (vRec != null)
        {
            vRec.InsertMedia(VirtualDiscMedium.CreateBlankCdRw700());
            UpdateMediaStatus();
        }
    }

    private void MountVirtualBlankDvdR()
    {
        var vRec = ResolveVirtualRecorder();
        if (vRec != null)
        {
            vRec.InsertMedia(VirtualDiscMedium.CreateBlankDvdR47());
            UpdateMediaStatus();
        }
    }

    private void Navigate(object? parameter)
    {
        NavigationTab? target = parameter switch
        {
            NavigationTab tab => tab,
            string str when Enum.TryParse<NavigationTab>(str, true, out var parsedTab) => parsedTab,
            int idx when Enum.IsDefined(typeof(NavigationTab), idx) => (NavigationTab)idx,
            _ => null
        };

        if (target.HasValue)
        {
            SelectedNavigationTab = target.Value;
            UpdateCurrentWorkspace();
        }
    }

    private void UpdateCurrentWorkspace()
    {
        if (_workspaces.TryGetValue(_selectedNavigationTab, out var workspace))
        {
            CurrentWorkspaceViewModel = workspace;
        }
        UpdateCapacityForActiveWorkspace();
    }

    private void RefreshCommandStates()
    {
        EjectTrayCommand.RaiseCanExecuteChanged();
        CloseTrayCommand.RaiseCanExecuteChanged();
        MountVirtualBlankCdRCommand.RaiseCanExecuteChanged();
        MountVirtualBlankCdRwCommand.RaiseCanExecuteChanged();
        MountVirtualBlankDvdRCommand.RaiseCanExecuteChanged();
    }

    private void OnDeviceChanged(object? sender, DeviceChangeEventArgs e)
    {
        void Action()
        {
            if (e.ChangeType == DeviceChangeType.Added || e.ChangeType == DeviceChangeType.Removed)
            {
                ReloadRecordersList();
                UpdateMediaStatus();
            }
            else if (_selectedRecorder != null &&
                     (string.IsNullOrEmpty(e.UniqueId) ||
                      string.Equals(_selectedRecorder.UniqueId, e.UniqueId, StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(_selectedRecorder.VolumePath, e.UniqueId, StringComparison.OrdinalIgnoreCase)))
            {
                UpdateMediaStatus();
            }
            else if (e.ChangeType == DeviceChangeType.MediaInserted || e.ChangeType == DeviceChangeType.MediaEjected)
            {
                UpdateMediaStatus();
            }
        }

        if (System.Windows.Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(Action);
        }
        else
        {
            Action();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                if (_mediaPollTimer != null)
                {
                    _mediaPollTimer.Stop();
                    _mediaPollTimer = null;
                }
                _audioBurn.Dispose(); // stops audio preview playback on shutdown
                _deviceService.DeviceChanged -= OnDeviceChanged;
                if (_ownsDeviceService && _deviceService is IDisposable d)
                {
                    d.Dispose();
                }
            }
            _disposed = true;
        }
    }
}
