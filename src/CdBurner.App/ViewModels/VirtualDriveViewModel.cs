using CdBurner.App.Resources;
using System.Collections.ObjectModel;
using System.IO;
using CdBurner.App.Mvvm;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;

namespace CdBurner.App.ViewModels;

/// <summary>
/// View model managing software-defined optical disc recorders, mounting synthetic
/// and file-backed virtual media (.iso / .bin), and injecting artificial bad sector LBAs for testing.
/// </summary>
public class VirtualDriveViewModel : ObservableObject
{
    private readonly IDiscDeviceService _deviceService;
    private IDiscRecorderBackend? _selectedRecorder;
    private int _newBadLba;
    private string _customImageFilePath = string.Empty;
    private string _statusMessage = "Ready";

    #region Properties

    /// <summary>
    /// Optical device enumeration service.
    /// </summary>
    public IDiscDeviceService DeviceService => _deviceService;

    /// <summary>
    /// Optical recorders discovered on the system.
    /// </summary>
    public ObservableCollection<IDiscRecorderBackend> AvailableRecorders { get; } = new();

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
                OnPropertyChanged(nameof(CurrentVirtualRecorder));
                RefreshMediaState();
                RaiseCommandCanExecute();
            }
        }
    }

    /// <summary>
    /// Cast of the active recorder to a <see cref="VirtualDiscRecorder"/>, if virtual.
    /// </summary>
    public VirtualDiscRecorder? CurrentVirtualRecorder =>
        _selectedRecorder as VirtualDiscRecorder
        ?? AvailableRecorders.OfType<VirtualDiscRecorder>().FirstOrDefault();

    /// <summary>
    /// Currently loaded virtual disc medium inside the virtual drive.
    /// </summary>
    public VirtualDiscMedium? CurrentMedium => CurrentVirtualRecorder?.CurrentMedium;

    /// <summary>
    /// Indicates whether a virtual medium is presently mounted in the drive.
    /// </summary>
    public bool HasMedia => CurrentMedium != null;

    /// <summary>
    /// Indicates whether the virtual drive tray is currently open/ejected.
    /// </summary>
    public bool IsTrayOpen => CurrentVirtualRecorder?.TrayOpen ?? false;

    /// <summary>
    /// Media type of currently mounted virtual medium.
    /// </summary>
    public DiscMediaType MediaType => CurrentMedium?.MediaType ?? DiscMediaType.NoMedia;

    /// <summary>
    /// Total nominal sector capacity of mounted virtual medium.
    /// </summary>
    public int TotalCapacitySectors => CurrentMedium?.TotalCapacitySectors ?? 0;

    /// <summary>
    /// Number of recorded sectors on mounted medium.
    /// </summary>
    public int UsedSectors => CurrentMedium?.UsedSectors ?? 0;

    /// <summary>
    /// Remaining unrecorded sector capacity.
    /// </summary>
    public int FreeSectors => CurrentMedium?.FreeSectors ?? 0;

    /// <summary>
    /// Indicates whether the mounted medium contains zero recorded sectors.
    /// </summary>
    public bool IsBlank => CurrentMedium?.IsBlank ?? true;

    /// <summary>
    /// Indicates whether the mounted medium is physically rewritable.
    /// </summary>
    public bool IsRewritable => CurrentMedium?.IsRewritable ?? false;

    /// <summary>
    /// Formatted status summary for the mounted virtual disc.
    /// </summary>
    public string MediaSummary
    {
        get
        {
            if (CurrentMedium == null) return "No virtual media mounted (Tray empty)";
            string name = CurrentMedium.MediaType.GetDisplayName();
            string rw = CurrentMedium.IsRewritable ? "Rewritable" : "Write-Once";
            return $"{name} | {rw} | {CurrentMedium.TotalCapacitySectors:N0} total sectors | {(CurrentMedium.IsBlank ? "Blank" : $"{CurrentMedium.UsedSectors:N0} used")}";
        }
    }

    /// <summary>
    /// Next logical block address (LBA) to add to the simulated bad sectors table.
    /// </summary>
    public int NewBadLba
    {
        get => _newBadLba;
        set => SetProperty(ref _newBadLba, value);
    }

    /// <summary>
    /// Observable list of simulated defective sector LBAs.
    /// </summary>
    public ObservableCollection<int> SimulatedBadLbas { get; } = new();

    /// <summary>
    /// Disc image file path (.iso, .bin) to mount into the virtual recorder.
    /// </summary>
    public string CustomImageFilePath
    {
        get => _customImageFilePath;
        set => SetProperty(ref _customImageFilePath, value);
    }

    /// <summary>
    /// Informational or status message displayed to the user.
    /// </summary>
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    #endregion

    #region Commands

    public RelayCommand<string> MountPresetCommand { get; }
    public RelayCommand MountCdRCommand { get; }
    public RelayCommand MountCdRwCommand { get; }
    public RelayCommand MountDvdRCommand { get; }
    public RelayCommand MountDvdRwCommand { get; }
    public RelayCommand MountDvdPlusRDLCommand { get; }
    public RelayCommand MountImageFileCommand { get; }
    public RelayCommand EjectMediaCommand { get; }
    public RelayCommand CloseTrayCommand { get; }
    public RelayCommand InjectBadLbaCommand { get; }
    public RelayCommand<int> RemoveBadLbaCommand { get; }
    public RelayCommand ClearBadLbasCommand { get; }
    public RelayCommand RefreshCommand { get; }

    #endregion

    /// <summary>
    /// Initializes a new instance of <see cref="VirtualDriveViewModel"/>.
    /// </summary>
    public VirtualDriveViewModel(IDiscDeviceService? deviceService = null)
    {
        _deviceService = deviceService ?? new DiscDeviceManager(enablePhysicalEnumeration: false);

        MountPresetCommand = new RelayCommand<string>(preset => MountPreset(preset ?? "CD-R"));
        MountCdRCommand = new RelayCommand(() => MountPreset("CD-R"));
        MountCdRwCommand = new RelayCommand(() => MountPreset("CD-RW"));
        MountDvdRCommand = new RelayCommand(() => MountPreset("DVD-R"));
        MountDvdRwCommand = new RelayCommand(() => MountPreset("DVD-RW"));
        MountDvdPlusRDLCommand = new RelayCommand(() => MountPreset("DVD+RDL"));
        MountImageFileCommand = new RelayCommand(() => MountImageFile(CustomImageFilePath));
        EjectMediaCommand = new RelayCommand(EjectMedia, () => CurrentVirtualRecorder != null);
        CloseTrayCommand = new RelayCommand(CloseTray, () => CurrentVirtualRecorder != null);
        InjectBadLbaCommand = new RelayCommand(() => InjectBadLba(NewBadLba), () => HasMedia);
        RemoveBadLbaCommand = new RelayCommand<int>(RemoveBadLba);
        ClearBadLbasCommand = new RelayCommand(ClearBadLbas, () => SimulatedBadLbas.Count > 0);
        RefreshCommand = new RelayCommand(RefreshRecorders);

        RefreshRecorders();
    }

    /// <summary>
    /// Re-enumerates drives and selects default virtual recorder.
    /// </summary>
    public void RefreshRecorders()
    {
        AvailableRecorders.Clear();
        foreach (var r in _deviceService.GetRecorders())
        {
            AvailableRecorders.Add(r);
        }

        var defaultRecorder = AvailableRecorders.FirstOrDefault(r => string.Equals(r.UniqueId, "VDRV-01", StringComparison.OrdinalIgnoreCase))
                               ?? AvailableRecorders.FirstOrDefault(r => r.IsVirtual)
                               ?? AvailableRecorders.FirstOrDefault();

        SelectedRecorder = defaultRecorder;
    }

    /// <summary>
    /// Mounts a predefined synthetic optical medium preset into the virtual drive.
    /// </summary>
    /// <param name="preset">Preset name ("CD-R", "CD-RW", "DVD-R", "DVD-RW", "DVD+RW", "DVD+RDL", "DVD-RAM").</param>
    public void MountPreset(string preset)
    {
        var vrec = CurrentVirtualRecorder;
        if (vrec == null)
        {
            StatusMessage = Strings.StatusVirtualDriveNoVirtualOpticalDriveIsCurrently;
            return;
        }

        VirtualDiscMedium medium = preset.ToUpperInvariant() switch
        {
            "CD-RW" => VirtualDiscMedium.CreateBlankCdRw700(),
            "DVD-R" or "DVD-MINUSR" => VirtualDiscMedium.CreateBlankDvdR47(),
            "DVD-RW" or "DVD-MINUSRW" => VirtualDiscMedium.CreateBlankDvdRw47(),
            "DVD+RW" or "DVD-PLUSRW" => VirtualDiscMedium.CreateBlankDvdPlusRw47(),
            "DVD+RDL" or "DVD+R DL" or "DVDPLUSRDL" => VirtualDiscMedium.CreateBlankDvdPlusRDL85(),
            "DVD-RAM" => VirtualDiscMedium.CreateBlankDvdRam(),
            _ => VirtualDiscMedium.CreateBlankCdR700()
        };

        vrec.InsertMedia(medium);
        RefreshMediaState();
        StatusMessage = $"Mounted {medium.MediaType.GetDisplayName()} into {vrec.UniqueId}.";
    }

    /// <summary>
    /// Mounts an existing disc image file (.iso, .bin) from disk.
    /// </summary>
    public void MountImageFile(string? filePath = null)
    {
        filePath ??= CustomImageFilePath;
        if (string.IsNullOrWhiteSpace(filePath) && System.Windows.Application.Current?.MainWindow?.IsLoaded == true)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Disc Image File to Mount in Virtual Drive",
                Filter = "Disc Images (*.iso;*.bin)|*.iso;*.bin|All Files (*.*)|*.*"
            };
            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FileName))
            {
                CustomImageFilePath = dlg.FileName;
                filePath = dlg.FileName;
            }
        }

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            StatusMessage = $"Image file does not exist: {filePath}";
            return;
        }

        var vrec = CurrentVirtualRecorder;
        if (vrec == null)
        {
            StatusMessage = Strings.StatusVirtualDriveNoVirtualOpticalDriveIsAvailable;
            return;
        }

        try
        {
            var medium = VirtualDiscMedium.FromExistingFile(filePath);
            vrec.InsertMedia(medium);
            RefreshMediaState();
            StatusMessage = $"Mounted disc image '{Path.GetFileName(filePath)}' ({medium.TotalCapacitySectors:N0} sectors).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to mount image file: {ex.Message}";
        }
    }

    /// <summary>
    /// Injects an artificial bad sector LBA into the mounted medium.
    /// </summary>
    /// <param name="lba">Logical block address to mark defective.</param>
    public void InjectBadLba(int lba)
    {
        var vrec = CurrentVirtualRecorder;
        if (vrec?.CurrentMedium == null)
        {
            StatusMessage = Strings.StatusVirtualDriveCannotInjectBadLbaNoMedia;
            return;
        }

        vrec.CurrentMedium.SimulatedBadLbas.Add(lba);
        if (!SimulatedBadLbas.Contains(lba))
        {
            SimulatedBadLbas.Add(lba);
        }
        StatusMessage = $"Injected simulated bad sector at LBA {lba}. Total bad LBAs: {SimulatedBadLbas.Count}";
        RaiseCommandCanExecute();
    }

    /// <summary>
    /// Removes a simulated bad LBA address from the mounted medium.
    /// </summary>
    /// <param name="lba">Logical block address to restore.</param>
    public void RemoveBadLba(int lba)
    {
        var vrec = CurrentVirtualRecorder;
        vrec?.CurrentMedium?.SimulatedBadLbas.Remove(lba);
        SimulatedBadLbas.Remove(lba);
        StatusMessage = $"Removed simulated bad sector at LBA {lba}.";
        RaiseCommandCanExecute();
    }

    /// <summary>
    /// Clears all simulated bad sector LBAs from the mounted medium.
    /// </summary>
    public void ClearBadLbas()
    {
        var vrec = CurrentVirtualRecorder;
        vrec?.CurrentMedium?.SimulatedBadLbas.Clear();
        SimulatedBadLbas.Clear();
        StatusMessage = Strings.StatusVirtualDriveClearedAllSimulatedBadLbas;
        RaiseCommandCanExecute();
    }

    /// <summary>
    /// Opens the virtual drive tray.
    /// </summary>
    public void EjectMedia()
    {
        CurrentVirtualRecorder?.EjectMedia();
        RefreshMediaState();
        StatusMessage = Strings.StatusVirtualDriveVirtualDriveTrayOpened;
    }

    /// <summary>
    /// Closes the virtual drive tray.
    /// </summary>
    public void CloseTray()
    {
        CurrentVirtualRecorder?.CloseTray();
        RefreshMediaState();
        StatusMessage = Strings.StatusVirtualDriveVirtualDriveTrayClosed;
    }

    /// <summary>
    /// Synchronizes internal view model state with the current virtual medium.
    /// </summary>
    public void RefreshMediaState()
    {
        var medium = CurrentMedium;
        SimulatedBadLbas.Clear();
        if (medium != null)
        {
            foreach (var lba in medium.SimulatedBadLbas.OrderBy(x => x))
            {
                SimulatedBadLbas.Add(lba);
            }
        }

        OnPropertyChanged(nameof(HasMedia));
        OnPropertyChanged(nameof(CurrentMedium));
        OnPropertyChanged(nameof(CurrentVirtualRecorder));
        OnPropertyChanged(nameof(MediaType));
        OnPropertyChanged(nameof(TotalCapacitySectors));
        OnPropertyChanged(nameof(UsedSectors));
        OnPropertyChanged(nameof(FreeSectors));
        OnPropertyChanged(nameof(IsBlank));
        OnPropertyChanged(nameof(IsRewritable));
        OnPropertyChanged(nameof(IsTrayOpen));
        OnPropertyChanged(nameof(MediaSummary));

        RaiseCommandCanExecute();
    }

    private void RaiseCommandCanExecute()
    {
        EjectMediaCommand.RaiseCanExecuteChanged();
        CloseTrayCommand.RaiseCanExecuteChanged();
        InjectBadLbaCommand.RaiseCanExecuteChanged();
        ClearBadLbasCommand.RaiseCanExecuteChanged();
    }
}
