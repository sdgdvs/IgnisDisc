using System.Collections.ObjectModel;
using System.Windows.Input;
using CdBurner.App.Mvvm;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Compilation;
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

    private IDiscRecorderBackend? _selectedRecorder;
    private MediaStatusDto _currentMediaStatus = new() { MediaType = DiscMediaType.NoMedia, MediaPresent = false };
    private WriteSpeedDescriptor? _selectedWriteSpeed;
    private NavigationTab _selectedNavigationTab = (NavigationTab)(-1);
    private object? _currentWorkspaceViewModel;
    private CapacityAssessment _currentCapacity;
    private bool _disposed;

    #region Properties

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
        set => SetProperty(ref _selectedWriteSpeed, value);
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

        // Set default workspace
        SelectedNavigationTab = NavigationTab.DataDisc;
    }

    private void InitializeWorkspaces()
    {
        _workspaces[NavigationTab.DataDisc] = new WorkspaceViewModel(
            NavigationTab.DataDisc,
            "Data Disc Studio",
            "Master ISO 9660, Joliet, and UDF data discs with optional El Torito bootable media.",
            "📁");

        _workspaces[NavigationTab.AudioCd] = new WorkspaceViewModel(
            NavigationTab.AudioCd,
            "Audio CD Studio",
            "Author Red Book audio CDs with CD-Text metadata, gapless DAO, and DSP normalization.",
            "🎵");

        _workspaces[NavigationTab.DvdVideo] = new WorkspaceViewModel(
            NavigationTab.DvdVideo,
            "DVD-Video Authoring",
            "Master compliant DVD-Video discs with 32 KB IFO/BUP sector gap alignment and dual-layer break analysis.",
            "🎬");

        _workspaces[NavigationTab.ImageStudio] = new WorkspaceViewModel(
            NavigationTab.ImageStudio,
            "Disc Image Studio",
            "Burn, create, rip, and verify ISO and BIN/CUE disc image archives.",
            "💿");

        _workspaces[NavigationTab.DriveUtilities] = new WorkspaceViewModel(
            NavigationTab.DriveUtilities,
            "Drive Utilities & Diagnostics",
            "Inspect optical drive capabilities, erase rewritable media, bitsetting, and run surface benchmarks.",
            "🔧");

        _workspaces[NavigationTab.VirtualDrive] = new WorkspaceViewModel(
            NavigationTab.VirtualDrive,
            "Virtual Drive Manager",
            "Configure and inspect software-defined optical disc simulators and virtual media files.",
            "💾");

        _workspaces[NavigationTab.Settings] = new WorkspaceViewModel(
            NavigationTab.Settings,
            "Application Settings",
            "Configure default write speeds, overburn policies, temporary scratch directories, and logging.",
            "⚙️");
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

        var recorders = _deviceService.GetRecorders();

        AvailableRecorders.Clear();
        foreach (var r in recorders)
        {
            AvailableRecorders.Add(r);
        }

        // Prefer selecting "VDRV-01" simulator drive, or fallback to first available
        var target = AvailableRecorders.FirstOrDefault(r => string.Equals(r.UniqueId, "VDRV-01", StringComparison.OrdinalIgnoreCase))
                     ?? AvailableRecorders.FirstOrDefault();

        SelectedRecorder = target;
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
                    SelectedWriteSpeed = AvailableWriteSpeeds[^1];
                }
            }
            else
            {
                SelectedWriteSpeed = null;
            }

            // Update Capacity Assessment
            var mediaType = _currentMediaStatus.MediaType != DiscMediaType.NoMedia && _currentMediaStatus.MediaType != DiscMediaType.Unknown
                ? _currentMediaStatus.MediaType
                : DiscMediaType.CdR;

            int maxSectors = _currentMediaStatus.TotalSectors > 0
                ? _currentMediaStatus.TotalSectors
                : mediaType.GetNominalCapacitySectors();

            if (maxSectors <= 0)
            {
                maxSectors = OpticalConstants.Cd700MbSectors;
            }

            int usedSectors = _currentMediaStatus.UsedSectors;
            CurrentCapacity = CompilationCapacityCalculator.AssessCapacity(usedSectors, mediaType, maxSectors);
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
            if (_selectedRecorder != null && string.Equals(_selectedRecorder.UniqueId, e.UniqueId, StringComparison.OrdinalIgnoreCase))
            {
                UpdateMediaStatus();
            }
            else if (e.ChangeType == DeviceChangeType.Added || e.ChangeType == DeviceChangeType.Removed)
            {
                RefreshDrives();
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
