using CdBurner.App.Resources;
using System.Collections.ObjectModel;
using CdBurner.App.Mvvm;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Verification;
using CdBurner.Core.Primitives;
using CdBurner.Core.Utilities;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;

namespace CdBurner.App.ViewModels;

/// <summary>
/// View model managing drive capabilities inspection, rewritable disc erasing (Quick and Full),
/// DVD Book Type bitsetting (DVD-ROM, DVD+R, etc.), and optical sector surface scanning.
/// </summary>
public class DriveUtilitiesViewModel : ObservableObject
{
    private readonly IDiscDeviceService _deviceService;
    private readonly IBurnEngineBackend _burnEngine;
    private readonly DiscEraseService _eraseService;
    private readonly SurfaceScanService _scanService;
    private readonly BookTypeService _bookTypeService;

    private IDiscRecorderBackend? _selectedRecorder;
    private bool _isFullEraseSelected;
    private DvdBookType _selectedBookType = DvdBookType.DvdRom;
    private int _scanBlockSizeSectors = 256;
    private SurfaceScanReport? _latestScanReport;
    private DriveCapabilitiesDto? _capabilities;
    private MediaStatusDto? _mediaDetails;
    private bool _isBusy;
    private string _statusMessage = "Ready";
    private double _operationProgress;
    private string? _lastCompatibilityReport;

    #region Properties

    /// <summary>
    /// Optical device discovery service.
    /// </summary>
    public IDiscDeviceService DeviceService => _deviceService;

    /// <summary>
    /// Optical engine backend.
    /// </summary>
    public IBurnEngineBackend BurnEngine => _burnEngine;

    /// <summary>
    /// Service for erasing and blanking rewritable media.
    /// </summary>
    public DiscEraseService EraseService => _eraseService;

    /// <summary>
    /// Service for sector benchmarking and surface scanning.
    /// </summary>
    public SurfaceScanService ScanService => _scanService;

    /// <summary>
    /// Service for DVD Book Type bitsetting.
    /// </summary>
    public BookTypeService BookTypeService => _bookTypeService;

    /// <summary>
    /// Available optical disc recorders.
    /// </summary>
    public ObservableCollection<IDiscRecorderBackend> AvailableRecorders { get; } = new();

    /// <summary>
    /// Contiguous sector blocks produced by the surface scanner.
    /// </summary>
    public ObservableCollection<SurfaceScanBlockResult> ScanBlocks { get; } = new();

    /// <summary>
    /// Supported DVD Book Types available for bitsetting.
    /// </summary>
    public IReadOnlyList<DvdBookType> AvailableBookTypes { get; } = new[]
    {
        DvdBookType.DvdRom,
        DvdBookType.DvdPlusR,
        DvdBookType.DvdPlusRw,
        DvdBookType.DvdPlusRDualLayer
    };

    /// <summary>
    /// Recommended block sizes in sectors for surface scanning.
    /// </summary>
    public IReadOnlyList<int> AvailableBlockSizes { get; } = new[]
    {
        32,
        64,
        128,
        256,
        512,
        1024
    };

    /// <summary>
    /// Currently selected optical recorder.
    /// </summary>
    public IDiscRecorderBackend? SelectedRecorder
    {
        get => _selectedRecorder;
        set
        {
            if (SetProperty(ref _selectedRecorder, value))
            {
                RefreshCapabilities();
                EraseDiscCommand.RaiseCanExecuteChanged();
                ApplyBookTypeCommand.RaiseCanExecuteChanged();
                StartSurfaceScanCommand.RaiseCanExecuteChanged();
                CheckDiscCommand?.RaiseCanExecuteChanged();
                GenerateCompatibilityReportCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Indicates whether Full Erase (all sectors zeroed) is selected instead of Quick Erase (TOC wipe).
    /// </summary>
    public bool IsFullEraseSelected
    {
        get => _isFullEraseSelected;
        set => SetProperty(ref _isFullEraseSelected, value);
    }

    /// <summary>
    /// Target DVD Book Type to configure when applying bitsetting.
    /// </summary>
    public DvdBookType SelectedBookType
    {
        get => _selectedBookType;
        set => SetProperty(ref _selectedBookType, value);
    }

    /// <summary>
    /// Number of logical sectors per surface scan block (default 256).
    /// </summary>
    public int ScanBlockSizeSectors
    {
        get => _scanBlockSizeSectors;
        set => SetProperty(ref _scanBlockSizeSectors, value);
    }

    /// <summary>
    /// Latest complete surface scan benchmark report.
    /// </summary>
    public SurfaceScanReport? LatestScanReport
    {
        get => _latestScanReport;
        set
        {
            if (SetProperty(ref _latestScanReport, value))
            {
                ExportScanCsvCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Hardware capabilities profile for the selected recorder.
    /// </summary>
    public DriveCapabilitiesDto? Capabilities
    {
        get => _capabilities;
        set => SetProperty(ref _capabilities, value);
    }

    /// <summary>
    /// Physical geometry and media status for the selected recorder.
    /// </summary>
    public MediaStatusDto? MediaDetails
    {
        get => _mediaDetails;
        set => SetProperty(ref _mediaDetails, value);
    }

    /// <summary>
    /// Indicates whether a long-running utility task is currently in progress.
    /// </summary>
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                EraseDiscCommand.RaiseCanExecuteChanged();
                ApplyBookTypeCommand.RaiseCanExecuteChanged();
                StartSurfaceScanCommand.RaiseCanExecuteChanged();
                CheckDiscCommand?.RaiseCanExecuteChanged();
                ExportScanCsvCommand?.RaiseCanExecuteChanged();
                GenerateCompatibilityReportCommand?.RaiseCanExecuteChanged();
                CancelCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Current status or informational message.
    /// </summary>
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>
    /// Progress percentage for the active utility operation (0.0 to 100.0).
    /// </summary>
    /// <summary>
    /// Most recently generated Markdown compatibility report.
    /// </summary>
    public string? LastCompatibilityReport
    {
        get => _lastCompatibilityReport;
        set => SetProperty(ref _lastCompatibilityReport, value);
    }

    public double OperationProgress
    {
        get => _operationProgress;
        set => SetProperty(ref _operationProgress, value);
    }

    #endregion

    #region Commands

    /// <summary>
    /// Command to erase rewritable optical media (Quick or Full mode).
    /// </summary>
    public AsyncRelayCommand EraseDiscCommand { get; }

    /// <summary>
    /// Command to apply DVD Book Type bitsetting on the inserted media.
    /// </summary>
    public AsyncRelayCommand ApplyBookTypeCommand { get; }

    /// <summary>
    /// Command to execute an optical disc surface scan and latency benchmark.
    /// </summary>
    public AsyncRelayCommand StartSurfaceScanCommand { get; }

    /// <summary>
    /// Command to re-query drive capabilities and media status.
    /// </summary>
    public RelayCommand RefreshCapabilitiesCommand { get; }

    /// <summary>
    /// Command to cancel the active scan, erase, or booktype operation.
    /// </summary>
    /// <summary>
    /// Command to generate a copy-pasteable Markdown drive compatibility report.
    /// </summary>
    public RelayCommand GenerateCompatibilityReportCommand { get; }

    /// <summary>
    /// Command to export the latest surface scan benchmark report as a CSV file.
    /// </summary>
    public AsyncRelayCommand ExportScanCsvCommand { get; }

    public RelayCommand CancelCommand { get; }

    /// <summary>
    /// Reads the CD once with C2 error pointers and says where it is weak (where a player may skip). Read-only.
    /// </summary>
    public AsyncRelayCommand CheckDiscCommand { get; }

    /// <summary>The last disc check, or null.</summary>
    public DiscHealthReport? LastDiscHealth
    {
        get => _lastDiscHealth;
        private set
        {
            if (SetProperty(ref _lastDiscHealth, value))
            {
                OnPropertyChanged(nameof(DiscHealthSummary));
                OnPropertyChanged(nameof(HasDiscHealth));
            }
        }
    }

    /// <summary>The last disc check in plain words, for the screen.</summary>
    public string? DiscHealthSummary => _lastDiscHealth?.Describe();

    public bool HasDiscHealth => _lastDiscHealth != null;

    /// <summary>
    /// Opens a drive for the disc check. The default opens a physical drive by its volume; tests pass the simulated one.
    /// Null for a drive that cannot be checked (the virtual drive).
    /// </summary>
    public Func<IDiscRecorderBackend, CdBurner.Hardware.Scsi.IScsiTransport?> OpenDriveForCheck { get; set; } = recorder =>
        recorder.IsVirtual || string.IsNullOrWhiteSpace(recorder.VolumePath)
            ? null
            : CdBurner.Hardware.Scsi.Win32ScsiTransport.Open(recorder.VolumePath);

    private DiscHealthReport? _lastDiscHealth;

    private CancellationTokenSource? _activeCts;

    #endregion

    /// <summary>
    /// Initializes a new instance of <see cref="DriveUtilitiesViewModel"/>.
    /// </summary>
    public DriveUtilitiesViewModel(
        IDiscDeviceService? deviceService = null,
        IBurnEngineBackend? burnEngine = null,
        DiscEraseService? eraseService = null,
        SurfaceScanService? scanService = null,
        BookTypeService? bookTypeService = null)
    {
        _deviceService = deviceService ?? new DiscDeviceManager(enablePhysicalEnumeration: false);
        _burnEngine = burnEngine ?? new VirtualBurnEngine();
        _eraseService = eraseService ?? new DiscEraseService(_burnEngine);
        _scanService = scanService ?? new SurfaceScanService(_burnEngine);
        _bookTypeService = bookTypeService ?? new BookTypeService();

        EraseDiscCommand = new AsyncRelayCommand(
            execute: async param => await EraseDiscAsync(param),
            canExecute: _ => SelectedRecorder != null && !IsBusy);

        ApplyBookTypeCommand = new AsyncRelayCommand(
            execute: async param => await ApplyBookTypeAsync(param),
            canExecute: _ => SelectedRecorder != null && !IsBusy);

        StartSurfaceScanCommand = new AsyncRelayCommand(
            execute: async param => await StartSurfaceScanAsync(param),
            canExecute: _ => SelectedRecorder != null && !IsBusy);

        RefreshCapabilitiesCommand = new RelayCommand(RefreshCapabilities);
        GenerateCompatibilityReportCommand = new RelayCommand(
            execute: () => GenerateCompatibilityReport(),
            canExecute: () => SelectedRecorder != null);
        ExportScanCsvCommand = new AsyncRelayCommand(
            execute: async param => await ExportScanCsvAsync(param),
            canExecute: _ => LatestScanReport != null && LatestScanReport.Blocks.Count > 0 && !IsBusy);
        CancelCommand = new RelayCommand(_ => CancelActiveOperation(), _ => IsBusy);
        CheckDiscCommand = new AsyncRelayCommand(
            execute: async param => await CheckDiscAsync(param),
            canExecute: _ => SelectedRecorder != null && !IsBusy);

        RefreshRecorders();
    }

    /// <summary>
    /// Cancels any actively running scan, erase, or hardware utility operation.
    /// </summary>
    public void CancelActiveOperation()
    {
        if (_activeCts != null && !_activeCts.IsCancellationRequested)
        {
            StatusMessage = Strings.StatusDriveUtilitiesCancellingHardwareOperation;
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
    /// Re-enumerates optical recorders and updates the drive collection.
    /// </summary>
    public void RefreshRecorders()
    {
        AvailableRecorders.Clear();
        foreach (var r in _deviceService.GetRecorders())
        {
            AvailableRecorders.Add(r);
        }

        var defaultRecorder = AvailableRecorders.FirstOrDefault(r => !r.IsVirtual)
                               ?? AvailableRecorders.FirstOrDefault(r => string.Equals(r.UniqueId, "VDRV-01", StringComparison.OrdinalIgnoreCase))
                               ?? AvailableRecorders.FirstOrDefault();

        SelectedRecorder = defaultRecorder;
    }

    /// <summary>
    /// Queries the selected recorder for hardware capabilities and media geometry.
    /// </summary>
    public void RefreshCapabilities()
    {
        if (SelectedRecorder != null)
        {
            Capabilities = SelectedRecorder.GetCapabilities();
            MediaDetails = SelectedRecorder.WithManufacturerId(SelectedRecorder.QueryMediaStatus());
        }
        else
        {
            Capabilities = null;
            MediaDetails = null;
        }
    }

    /// <summary>
    /// Asynchronously erases the rewritable media currently inserted in the selected recorder.
    /// </summary>
    public async Task<BurnOperationResult> EraseDiscAsync(object? parameter = null, CancellationToken cancellationToken = default)
    {
        var targetRecorder = ResolveTargetRecorder(parameter);
        if (targetRecorder == null)
        {
            StatusMessage = Strings.StatusDriveUtilitiesNoOpticalRecorderSelectedForErase;
            return BurnOperationResult.Failed(StatusMessage);
        }

        _activeCts?.Dispose();
        _activeCts = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : new CancellationTokenSource();
        var ct = _activeCts.Token;

        IsBusy = true;
        OperationProgress = 0.0;
        StatusMessage = $"Erasing media in {targetRecorder.UniqueId} ({(IsFullEraseSelected ? "Full Erase" : "Quick Erase")})...";

        try
        {
            var progress = new ClosableProgress<BurnProgressSnapshot>(snap =>
            {
                OperationProgress = snap.ProgressPercentage;
                if (!string.IsNullOrWhiteSpace(snap.CurrentActionDescription))
                {
                    StatusMessage = snap.CurrentActionDescription;
                }
            });

            var result = await _eraseService.EraseAsync(targetRecorder, IsFullEraseSelected, progress, ct);
            progress.Close(); // a late progress report must not overwrite the final status line
            StatusMessage = result.Success ? "Erase operation completed successfully." : $"Erase failed: {result.ErrorMessage}";
            MediaDetails = targetRecorder.WithManufacturerId(targetRecorder.QueryMediaStatus());
            return result;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Strings.StatusDriveUtilitiesEraseOperationCancelledByUser;
            return BurnOperationResult.CancelledResult(StatusMessage);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Erase operation error: {ex.Message}";
            return BurnOperationResult.Failed(ex.Message, ex);
        }
        finally
        {
            _activeCts?.Dispose();
            _activeCts = null;
            IsBusy = false;
        }
    }

    /// <summary>
    /// Asynchronously sets the DVD Book Type (bitsetting) on the inserted media.
    /// </summary>
    public async Task<BurnOperationResult> ApplyBookTypeAsync(object? parameter = null, CancellationToken cancellationToken = default)
    {
        var targetRecorder = ResolveTargetRecorder(parameter);
        if (targetRecorder == null)
        {
            StatusMessage = Strings.StatusDriveUtilitiesNoOpticalRecorderSelectedForBitsetting;
            return BurnOperationResult.Failed(StatusMessage);
        }

        IsBusy = true;
        StatusMessage = $"Applying Book Type {SelectedBookType} to media in {targetRecorder.UniqueId}...";

        try
        {
            var result = await _bookTypeService.SetBookTypeAsync(targetRecorder, SelectedBookType, cancellationToken);
            StatusMessage = result.Success
                ? $"Book Type successfully set to {SelectedBookType}."
                : $"Failed to set Book Type: {result.ErrorMessage}";
            MediaDetails = targetRecorder.WithManufacturerId(targetRecorder.QueryMediaStatus());
            return result;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Bitsetting error: {ex.Message}";
            return BurnOperationResult.Failed(ex.Message, ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Asynchronously runs an optical sector surface scan and throughput benchmark.
    /// </summary>
    public async Task<SurfaceScanReport> StartSurfaceScanAsync(object? parameter = null, CancellationToken cancellationToken = default)
    {
        var targetRecorder = ResolveTargetRecorder(parameter);
        if (targetRecorder == null)
        {
            StatusMessage = Strings.StatusDriveUtilitiesNoOpticalRecorderSelectedForSurface;
            return new SurfaceScanReport(0, 0, 0, 0, 0, 0, 0, Array.Empty<SurfaceScanBlockResult>());
        }

        _activeCts?.Dispose();
        _activeCts = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : new CancellationTokenSource();
        var ct = _activeCts.Token;

        IsBusy = true;
        ScanBlocks.Clear();
        LatestScanReport = null;
        OperationProgress = 0.0;
        StatusMessage = $"Starting surface scan on {targetRecorder.UniqueId} (block size: {ScanBlockSizeSectors} sectors)...";

        try
        {
            var blockProgress = new Progress<SurfaceScanBlockResult>(block =>
            {
                ScanBlocks.Add(block);
            });

            var snapProgress = new ClosableProgress<BurnProgressSnapshot>(snap =>
            {
                OperationProgress = snap.ProgressPercentage;
                if (!string.IsNullOrWhiteSpace(snap.CurrentActionDescription))
                {
                    StatusMessage = snap.CurrentActionDescription;
                }
            });

            var options = new SurfaceScanOptions
            {
                BlockSizeSectors = ScanBlockSizeSectors > 0 ? ScanBlockSizeSectors : 256,
                BlockProgress = blockProgress
            };

            var report = await _scanService.ScanAsync(targetRecorder, options, snapProgress, ct);
            snapProgress.Close(); // a late progress report must not overwrite the final status line
            LatestScanReport = report;
            OperationProgress = 100.0;
            StatusMessage = $"Surface scan finished: {report.TotalSectorsScanned:N0} sectors, {report.GoodBlocks} good, {report.SlowBlocks} slow, {report.DamagedBlocks} damaged.";
            return report;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Strings.StatusDriveUtilitiesSurfaceScanCancelledByUser;
            var fallbackReport = new SurfaceScanReport(0, 0, 0, 0, 0, 0, 0, Array.Empty<SurfaceScanBlockResult>());
            LatestScanReport = fallbackReport;
            return fallbackReport;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Surface scan error: {ex.Message}";
            var fallbackReport = new SurfaceScanReport(0, 0, 0, 0, 0, 0, 0, Array.Empty<SurfaceScanBlockResult>());
            LatestScanReport = fallbackReport;
            return fallbackReport;
        }
        finally
        {
            _activeCts?.Dispose();
            _activeCts = null;
            IsBusy = false;
        }
    }

    /// <summary>
    /// Reads every sector of the CD once with C2 error pointers (<see cref="CdBurner.Hardware.Scsi.DiscHealthScanner"/>)
    /// and puts the verdict in <see cref="DiscHealthSummary"/>. Read-only; cancellable.
    /// </summary>
    public async Task<DiscHealthReport?> CheckDiscAsync(object? parameter = null, CancellationToken cancellationToken = default)
    {
        var targetRecorder = ResolveTargetRecorder(parameter);
        if (targetRecorder == null)
        {
            StatusMessage = Strings.StatusDriveUtilitiesNoDriveSelectedForTheDisc;
            return null;
        }

        _activeCts?.Dispose();
        _activeCts = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : new CancellationTokenSource();
        var ct = _activeCts.Token;

        IsBusy = true;
        LastDiscHealth = null;
        OperationProgress = 0.0;
        StatusMessage = Strings.StatusDriveUtilitiesCheckingTheDiscReadingEverySector;
        try
        {
            var progress = new Progress<double>(percent =>
            {
                OperationProgress = percent;
                StatusMessage = $"Checking the disc: {percent:F0}%";
            });
            IProgress<double> report = progress;
            var result = await Task.Run(() =>
            {
                using var transport = OpenDriveForCheck(targetRecorder)
                    ?? throw new InvalidOperationException("The disc check reads a real disc: choose a physical drive.");
                return new CdBurner.Hardware.Scsi.DiscHealthScanner(transport).Scan((done, total) => report.Report(100.0 * done / Math.Max(1, total)), ct);
            }, ct);

            LastDiscHealth = result;
            OperationProgress = 100.0;
            StatusMessage = result.Verdict switch
            {
                DiscHealthVerdict.ReadsCleanly => "Disc check finished: reads cleanly.",
                DiscHealthVerdict.Weak => "Disc check finished: weak in places (see below).",
                _ => "Disc check finished: damaged (see below)."
            };
            return result;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Strings.StatusDriveUtilitiesDiscCheckCancelled;
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            StatusMessage = $"Disc check could not run: {ex.Message}";
            return null;
        }
        finally
        {
            _activeCts?.Dispose();
            _activeCts = null;
            IsBusy = false;
        }
    }

    private IDiscRecorderBackend? ResolveTargetRecorder(object? parameter)
    {
        if (parameter is IDiscRecorderBackend rec) return rec;
        if (parameter is string id && !string.IsNullOrWhiteSpace(id))
        {
            return AvailableRecorders.FirstOrDefault(r => string.Equals(r.UniqueId, id, StringComparison.OrdinalIgnoreCase))
                   ?? SelectedRecorder;
        }
        return SelectedRecorder;
    }

    /// <summary>
    /// Builds a Markdown drive compatibility report and stores it in <see cref="LastCompatibilityReport"/>.
    /// </summary>
    public string? GenerateCompatibilityReport()
    {
        if (SelectedRecorder == null)
        {
            StatusMessage = Strings.StatusDriveUtilitiesNoOpticalRecorderDriveSelected;
            return null;
        }

        var report = DriveCompatibilityReportBuilder.BuildMarkdownReport(
            SelectedRecorder,
            Capabilities,
            MediaDetails);

        LastCompatibilityReport = report;
        StatusMessage = Strings.StatusDriveUtilitiesDriveCompatibilityReportGeneratedAndReady;
        return report;
    }

    /// <summary>
    /// Exports the latest surface scan report to a CSV file.
    /// </summary>
    public async Task<string?> ExportScanCsvAsync(object? parameter = null, CancellationToken cancellationToken = default)
    {
        if (LatestScanReport == null || LatestScanReport.Blocks.Count == 0)
        {
            StatusMessage = Strings.StatusDriveUtilitiesRunASurfaceScanBeforeExporting;
            return null;
        }

        string? targetPath = parameter as string;
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export Surface Scan CSV",
                Filter = "CSV Files (*.csv)|*.csv|All Files (*.*)|*.*",
                FileName = $"IgnisDisc-SurfaceScan-{SelectedRecorder?.UniqueId ?? "Drive"}.csv"
            };
            if (dlg.ShowDialog() != true)
            {
                return null;
            }
            targetPath = dlg.FileName;
        }

        await LatestScanReport.ExportCsvAsync(targetPath, cancellationToken);
        StatusMessage = $"Surface scan CSV exported to {targetPath}.";
        return targetPath;
    }
}
