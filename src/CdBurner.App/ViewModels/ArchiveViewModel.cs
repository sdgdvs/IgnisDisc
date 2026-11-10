using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using CdBurner.App.Mvvm;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Archival;
using CdBurner.Core.Burning;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Core.Utilities;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;
using Microsoft.Win32;

namespace CdBurner.App.ViewModels;

/// <summary>
/// Selectable media option for multi-disc archival spanning.
/// </summary>
/// <param name="Name">Display name of the media option.</param>
/// <param name="MediaType">Optical media type with capacity profile.</param>
public sealed record ArchiveMediaOption(string Name, DiscMediaType MediaType);

/// <summary>
/// Observable presentation item representing a single planned or staged disc in an archival set.
/// </summary>
public sealed class ArchiveDiscRow : ObservableObject
{
    private string? _stagedPath;
    private string _status = "Planned";

    /// <summary>
    /// 1-based disc index in the multi-disc set.
    /// </summary>
    public int DiscNumber { get; }

    /// <summary>
    /// Total count of source files assigned to this disc.
    /// </summary>
    public int FileCount { get; }

    /// <summary>
    /// Total raw payload size in bytes.
    /// </summary>
    public long DataBytes { get; }

    /// <summary>
    /// Formatted human-readable payload size (e.g. "2.9 MB" or "4.21 GB").
    /// </summary>
    public string SizeText { get; }

    /// <summary>
    /// Filesystem directory where this disc's files and manifest have been staged.
    /// </summary>
    public string? StagedPath
    {
        get => _stagedPath;
        set => SetProperty(ref _stagedPath, value);
    }

    /// <summary>
    /// Current execution status: "Planned", "Staged", "Burning…", "Burned", "Burned + verified", "Failed: …"
    /// </summary>
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    /// <summary>
    /// Initializes a new instance of <see cref="ArchiveDiscRow"/>.
    /// </summary>
    public ArchiveDiscRow(
        int discNumber,
        int fileCount,
        long dataBytes,
        string sizeText,
        string? stagedPath = null,
        string status = "Planned")
    {
        DiscNumber = discNumber;
        FileCount = fileCount;
        DataBytes = dataBytes;
        SizeText = sizeText;
        _stagedPath = stagedPath;
        _status = status;
    }
}

/// <summary>
/// View model managing multi-disc spanning archive planning, staging with SHA-256 manifests
/// and PAR2 recovery data, burning, post-burn verification, and file repair.
/// </summary>
public class ArchiveViewModel : ObservableObject
{
    private readonly IDiscDeviceService _deviceService;
    private readonly IBurnEngineBackend _burnEngine;

    private string _sourceFolder = string.Empty;
    private string _stagingFolder;
    private ArchiveMediaOption _selectedMedia;
    private int _redundancyPercent;
    private ArchivePlan? _currentPlan;
    private string _planSummary = string.Empty;
    private bool _par2Available;
    private string _par2StatusText = string.Empty;
    private string? _customPar2Path;

    private IDiscRecorderBackend? _selectedRecorder;
    private bool _verifyAfterBurn = true;

    private string _verifyPath = string.Empty;
    private ArchiveVerificationResult? _lastVerification;
    private string _verificationSummary = string.Empty;
    private bool _canRepair;

    private bool _isBusy;
    private double _progress;
    private string _statusMessage = "Ready";

    private CancellationTokenSource? _activeCts;

    #region Properties - Plan & Stage

    /// <summary>
    /// Source folder on local disk to archive.
    /// </summary>
    public string SourceFolder
    {
        get => _sourceFolder;
        set => SetProperty(ref _sourceFolder, value);
    }

    /// <summary>
    /// Base staging folder where disc subdirectories (disc01, disc02, etc.) are generated.
    /// </summary>
    public string StagingFolder
    {
        get => _stagingFolder;
        set => SetProperty(ref _stagingFolder, value);
    }

    /// <summary>
    /// Supported optical media types available for archival spanning.
    /// </summary>
    public IReadOnlyList<ArchiveMediaOption> MediaOptions { get; }

    /// <summary>
    /// Target optical media type selected for spanning calculations.
    /// </summary>
    public ArchiveMediaOption SelectedMedia
    {
        get => _selectedMedia;
        set => SetProperty(ref _selectedMedia, value);
    }

    /// <summary>
    /// PAR2 recovery data redundancy percentage (0 to 100). Forced to 0 if par2 is unavailable.
    /// </summary>
    public int RedundancyPercent
    {
        get => _redundancyPercent;
        set
        {
            int clamped = Math.Clamp(value, 0, 100);
            if (!Par2Available)
            {
                clamped = 0;
            }
            SetProperty(ref _redundancyPercent, clamped);
        }
    }

    /// <summary>
    /// Currently active archive spanning plan, or null if not yet planned.
    /// </summary>
    public ArchivePlan? CurrentPlan
    {
        get => _currentPlan;
        private set => SetProperty(ref _currentPlan, value);
    }

    /// <summary>
    /// Collection of discs defined by the current plan.
    /// </summary>
    public ObservableCollection<ArchiveDiscRow> Discs { get; } = new();

    /// <summary>
    /// Formatted summary of the active plan (e.g. "4 discs · 17.2 GB in 1,204 files · 10% PAR2").
    /// </summary>
    public string PlanSummary
    {
        get => _planSummary;
        private set => SetProperty(ref _planSummary, value);
    }

    /// <summary>
    /// Indicates whether the par2cmdline executable was found on the host machine.
    /// </summary>
    public bool Par2Available
    {
        get => _par2Available;
        private set
        {
            if (SetProperty(ref _par2Available, value))
            {
                OnPropertyChanged(nameof(IsPar2Missing));
                UpdateCanRepair();
                InstallPar2Command?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// True when <c>par2.exe</c> is not installed on the system.
    /// </summary>
    public bool IsPar2Missing => !_par2Available;

    /// <summary>
    /// Informational text describing PAR2 installation status.
    /// </summary>
    public string Par2StatusText
    {
        get => _par2StatusText;
        private set => SetProperty(ref _par2StatusText, value);
    }

    #endregion

    #region Properties - Burn

    /// <summary>
    /// Optical recorders available for burning archival discs.
    /// </summary>
    public ObservableCollection<IDiscRecorderBackend> AvailableRecorders { get; } = new();

    /// <summary>
    /// Optical recorder currently selected for burning.
    /// </summary>
    public IDiscRecorderBackend? SelectedRecorder
    {
        get => _selectedRecorder;
        set
        {
            if (SetProperty(ref _selectedRecorder, value))
            {
                VerifyDriveCommand?.RaiseCanExecuteChanged();
                BurnDiscCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Indicates whether burned discs should be verified immediately after burning.
    /// </summary>
    public bool VerifyAfterBurn
    {
        get => _verifyAfterBurn;
        set => SetProperty(ref _verifyAfterBurn, value);
    }

    #endregion

    #region Properties - Verify & Repair

    /// <summary>
    /// Directory path or optical drive root to verify against its manifest.
    /// </summary>
    public string VerifyPath
    {
        get => _verifyPath;
        set
        {
            if (SetProperty(ref _verifyPath, value))
            {
                UpdateCanRepair();
                VerifyCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Result of the most recent verification operation.
    /// </summary>
    public ArchiveVerificationResult? LastVerification
    {
        get => _lastVerification;
        private set
        {
            if (SetProperty(ref _lastVerification, value))
            {
                UpdateCanRepair();
            }
        }
    }

    /// <summary>
    /// List of file errors discovered during verification (e.g. "MISSING path" or "CORRUPT path").
    /// </summary>
    public ObservableCollection<string> VerificationProblems { get; } = new();

    /// <summary>
    /// Summary line of verification results.
    /// </summary>
    public string VerificationSummary
    {
        get => _verificationSummary;
        private set => SetProperty(ref _verificationSummary, value);
    }

    /// <summary>
    /// Indicates whether in-place PAR2 repair can be executed on <see cref="VerifyPath"/>.
    /// True when verification found defects, recovery files exist, par2 is available, and the path is not a read-only drive root.
    /// </summary>
    public bool CanRepair
    {
        get => _canRepair;
        private set
        {
            if (SetProperty(ref _canRepair, value))
            {
                RepairCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    #endregion

    #region Properties - Shared

    /// <summary>
    /// Indicates whether a long-running plan, stage, burn, verify, or repair operation is in progress.
    /// </summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                BrowseSourceCommand?.RaiseCanExecuteChanged();
                BrowseStagingCommand?.RaiseCanExecuteChanged();
                BrowseVerifyFolderCommand?.RaiseCanExecuteChanged();
                PlanCommand?.RaiseCanExecuteChanged();
                StageCommand?.RaiseCanExecuteChanged();
                BurnDiscCommand?.RaiseCanExecuteChanged();
                VerifyCommand?.RaiseCanExecuteChanged();
                VerifyDriveCommand?.RaiseCanExecuteChanged();
                RepairCommand?.RaiseCanExecuteChanged();
                InstallPar2Command?.RaiseCanExecuteChanged();
                CancelCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Progress percentage (0.0 to 100.0) of the active operation.
    /// </summary>
    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    /// <summary>
    /// Current user-facing status message.
    /// </summary>
    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    #endregion

    #region Commands

    /// <summary>
    /// Command to browse for the source folder to archive.
    /// </summary>
    public RelayCommand BrowseSourceCommand { get; }

    /// <summary>
    /// Command to browse for the staging destination folder.
    /// </summary>
    public RelayCommand BrowseStagingCommand { get; }

    /// <summary>
    /// Command to browse for an archive folder to verify.
    /// </summary>
    public RelayCommand BrowseVerifyFolderCommand { get; }

    /// <summary>
    /// Command to calculate disc spanning for the source folder.
    /// </summary>
    public AsyncRelayCommand PlanCommand { get; }

    /// <summary>
    /// Command to stage files, generate manifests, and create PAR2 recovery data.
    /// </summary>
    public AsyncRelayCommand StageCommand { get; }

    /// <summary>
    /// Command to burn a single staged disc row to the selected optical recorder.
    /// </summary>
    public AsyncRelayCommand BurnDiscCommand { get; }

    /// <summary>
    /// Command to verify the archive directory specified in <see cref="VerifyPath"/>.
    /// </summary>
    public AsyncRelayCommand VerifyCommand { get; }

    /// <summary>
    /// Command to set <see cref="VerifyPath"/> to the selected drive's volume path and run verification.
    /// </summary>
    public AsyncRelayCommand VerifyDriveCommand { get; }

    /// <summary>
    /// Command to repair corrupted files in place using PAR2 recovery volumes.
    /// </summary>
    public AsyncRelayCommand RepairCommand { get; }

    /// <summary>
    /// Command to download and install <c>par2cmdline</c> (<c>par2.exe</c>) with 1 click.
    /// </summary>
    public AsyncRelayCommand InstallPar2Command { get; }

    /// <summary>
    /// Command to cancel the active operation.
    /// </summary>
    public RelayCommand CancelCommand { get; }

    #endregion

    /// <summary>
    /// Initializes a new instance of <see cref="ArchiveViewModel"/>.
    /// </summary>
    /// <param name="deviceService">Optical device enumeration service.</param>
    /// <param name="burnEngine">Optical burn engine backend.</param>
    public ArchiveViewModel(IDiscDeviceService? deviceService = null, IBurnEngineBackend? burnEngine = null)
    {
        _deviceService = deviceService ?? new DiscDeviceManager(enablePhysicalEnumeration: false);
        _burnEngine = burnEngine ?? new VirtualBurnEngine();

        MediaOptions = new ArchiveMediaOption[]
        {
            new("CD-R (700 MB)", DiscMediaType.CdR),
            new("DVD±R (4.7 GB)", DiscMediaType.DvdPlusR),
            new("DVD±R DL (8.5 GB)", DiscMediaType.DvdMinusRDualLayer),
            // Blu-ray plans use the BD-RE size (smaller, because of its spare area), so a plan fits either kind.
            new("BD-R/RE (25 GB)", DiscMediaType.BdRe),
            new("BD-R/RE DL (50 GB)", DiscMediaType.BdReDualLayer),
            new("BD-R XL (100 GB)", DiscMediaType.BdxlRTripleLayer),
            new("BD-R XL (128 GB)", DiscMediaType.BdxlRQuadLayer)
        };
        _selectedMedia = MediaOptions[1]; // default DVD

        RefreshPar2Status();
        _redundancyPercent = Par2Available ? 10 : 0;
        _stagingFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "IgnisDisc Archives");

        BrowseSourceCommand = new RelayCommand(BrowseSourceFolder, () => !IsBusy);
        BrowseStagingCommand = new RelayCommand(BrowseStagingFolder, () => !IsBusy);
        BrowseVerifyFolderCommand = new RelayCommand(BrowseVerifyFolder, () => !IsBusy);

        PlanCommand = new AsyncRelayCommand(async _ => await PlanAsync(), _ => !IsBusy);
        StageCommand = new AsyncRelayCommand(async _ => await StageAsync(), _ => !IsBusy);
        BurnDiscCommand = new AsyncRelayCommand(
            async param =>
            {
                if (param is ArchiveDiscRow row)
                {
                    await BurnDiscAsync(row);
                }
            },
            param => !IsBusy && param is ArchiveDiscRow);

        VerifyCommand = new AsyncRelayCommand(async _ => await VerifyAsync(VerifyPath), _ => !IsBusy);
        VerifyDriveCommand = new AsyncRelayCommand(
            async _ =>
            {
                if (!string.IsNullOrWhiteSpace(SelectedRecorder?.VolumePath))
                {
                    VerifyPath = SelectedRecorder.VolumePath;
                    await VerifyAsync(SelectedRecorder.VolumePath);
                }
            },
            _ => !IsBusy && SelectedRecorder != null && !string.IsNullOrWhiteSpace(SelectedRecorder.VolumePath));

        RepairCommand = new AsyncRelayCommand(async _ => await RepairAsync(), _ => !IsBusy && CanRepair);
        InstallPar2Command = new AsyncRelayCommand(async _ => await InstallPar2Async(), _ => !IsBusy);
        CancelCommand = new RelayCommand(CancelActiveOperation, () => IsBusy);

        RefreshRecorders();
    }

    #region Public Async Entry Points

    /// <summary>
    /// Re-checks whether <c>par2.exe</c> is installed on the host machine and updates UI status.
    /// </summary>
    public void RefreshPar2Status(string? customPar2Path = null)
    {
        if (!string.IsNullOrWhiteSpace(customPar2Path))
        {
            _customPar2Path = customPar2Path;
        }

        string? par2Path = Par2Runner.FindPar2Executable(_customPar2Path);
        bool wasAvailable = _par2Available;
        Par2Available = par2Path != null;
        Par2StatusText = Par2Available
            ? $"par2 found: {par2Path}"
            : "par2 not installed — recovery data disabled (click 'Install par2 (1-Click)' to enable)";

        if (Par2Available && !wasAvailable && _redundancyPercent == 0)
        {
            RedundancyPercent = 10;
        }
    }

    /// <summary>
    /// Downloads and installs <c>par2cmdline</c> (<c>par2.exe</c>) into per-user storage and enables PAR2 recovery data.
    /// </summary>
    public async Task<ToolInstallResult> InstallPar2Async(
        string? targetDirectory = null,
        HttpClient? httpClient = null,
        string? downloadUrlOverride = null,
        CancellationToken ct = default)
    {
        IsBusy = true;
        Progress = 0;
        StatusMessage = "Downloading par2cmdline (par2.exe)...";

        try
        {
            var progressHandler = new Progress<string>(msg =>
            {
                StatusMessage = msg;
                Par2StatusText = msg;
            });

            var result = await ExternalToolInstallerService.InstallPar2Async(
                targetDirectory,
                httpClient,
                downloadUrlOverride,
                progressHandler,
                ct).ConfigureAwait(true);

            if (result.Success && !string.IsNullOrEmpty(result.ExecutablePath))
            {
                RefreshPar2Status(result.ExecutablePath);
                Progress = 100.0;
                StatusMessage = $"par2cmdline installed ({result.ExecutablePath}). PAR2 recovery enabled.";
            }
            else
            {
                StatusMessage = $"par2 install failed: {result.ErrorMessage}";
            }

            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Calculates multi-disc spanning layout for the current source folder and media selection.
    /// </summary>
    /// <returns>The generated <see cref="ArchivePlan"/>, or null on error.</returns>
    public async Task<ArchivePlan?> PlanAsync()
    {
        if (string.IsNullOrWhiteSpace(SourceFolder))
        {
            StatusMessage = "Source folder is not specified.";
            return null;
        }

        if (!Directory.Exists(SourceFolder))
        {
            StatusMessage = $"Source folder not found: '{SourceFolder}'.";
            return null;
        }

        _activeCts?.Dispose();
        _activeCts = new CancellationTokenSource();
        IsBusy = true;
        Progress = 0;
        StatusMessage = "Planning archive spanning...";

        try
        {
            var mediaType = SelectedMedia.MediaType;
            var redundancy = RedundancyPercent;
            var folder = SourceFolder;

            var plan = await Task.Run(() => ArchiveSpanPlanner.Plan(folder, mediaType, redundancy)).ConfigureAwait(true);
            CurrentPlan = plan;

            Discs.Clear();
            foreach (var disc in plan.Discs)
            {
                var row = new ArchiveDiscRow(
                    discNumber: disc.DiscNumber,
                    fileCount: disc.Files.Count,
                    dataBytes: disc.DataBytes,
                    sizeText: FormatBytes(disc.DataBytes),
                    status: "Planned");
                Discs.Add(row);
            }

            int totalFiles = plan.Discs.Sum(d => d.Files.Count);
            string discWord = plan.Discs.Count == 1 ? "disc" : "discs";
            PlanSummary = $"{plan.Discs.Count} {discWord} · {FormatBytes(plan.TotalBytes)} in {totalFiles:N0} files · {plan.RedundancyPercent}% PAR2";
            StatusMessage = $"Plan created: {plan.Discs.Count} {discWord}, {FormatBytes(plan.TotalBytes)}.";
            return plan;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Planning failed: {ex.Message}";
            CurrentPlan = null;
            Discs.Clear();
            PlanSummary = string.Empty;
            return null;
        }
        finally
        {
            _activeCts?.Dispose();
            _activeCts = null;
            IsBusy = false;
        }
    }

    /// <summary>
    /// Stages the planned discs onto local disk with manifests and optional PAR2 recovery files.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if staging succeeded; false otherwise.</returns>
    public async Task<bool> StageAsync(CancellationToken ct = default)
    {
        var plan = CurrentPlan;
        if (plan == null)
        {
            plan = await PlanAsync();
            if (plan == null)
            {
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(StagingFolder))
        {
            StatusMessage = "Staging folder is not specified.";
            return false;
        }

        string stagingRoot = Path.Combine(StagingFolder, $"archive-{plan.SetId}");

        _activeCts?.Dispose();
        _activeCts = ct.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : new CancellationTokenSource();
        var token = _activeCts.Token;

        IsBusy = true;
        Progress = 0;
        StatusMessage = $"Staging archive set {plan.SetId}...";

        try
        {
            string? par2Path = Par2Runner.FindPar2Executable(_customPar2Path);
            if (plan.RedundancyPercent > 0 && par2Path == null)
            {
                StatusMessage = "PAR2 recovery data requires par2cmdline (par2.exe). Install it or set redundancy to 0.";
                return false;
            }

            var progressHandler = new Progress<string>(msg =>
            {
                StatusMessage = msg;
                if (plan.Discs.Count > 0)
                {
                    int idx = msg.IndexOf("Staging disc ", StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        int start = idx + "Staging disc ".Length;
                        int slash = msg.IndexOf('/', start);
                        if (slash > start && int.TryParse(msg[start..slash], out int discNum))
                        {
                            Progress = Math.Clamp((double)(discNum - 1) / plan.Discs.Count * 100.0, 0, 100);
                        }
                    }
                }
            });

            var stagedDiscs = await ArchiveStager.StageAsync(plan, stagingRoot, par2Path, progressHandler, token).ConfigureAwait(true);

            for (int i = 0; i < stagedDiscs.Count && i < Discs.Count; i++)
            {
                Discs[i].StagedPath = stagedDiscs[i];
                Discs[i].Status = "Staged";
            }

            Progress = 100.0;
            StatusMessage = $"Staging complete: {stagedDiscs.Count} disc{(stagedDiscs.Count == 1 ? "" : "s")} staged to {stagingRoot}.";
            return true;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Staging cancelled.";
            return false;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Staging failed: {ex.Message}";
            return false;
        }
        finally
        {
            _activeCts?.Dispose();
            _activeCts = null;
            IsBusy = false;
        }
    }

    /// <summary>
    /// Burns the specified staged disc row to the selected optical recorder.
    /// </summary>
    /// <param name="row">The disc row to burn.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of the data burn operation.</returns>
    public async Task<DataBurnResult> BurnDiscAsync(ArchiveDiscRow row, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (string.IsNullOrWhiteSpace(row.StagedPath) || !Directory.Exists(row.StagedPath))
        {
            StatusMessage = $"Disc {row.DiscNumber} staged folder not found.";
            row.Status = "Failed: Staged folder missing";
            return DataBurnResult.Failed(StatusMessage);
        }

        if (SelectedRecorder == null)
        {
            StatusMessage = "No optical recorder selected.";
            row.Status = "Failed: No recorder selected";
            return DataBurnResult.Failed(StatusMessage);
        }

        // Auto-insert blank medium for virtual recorder if empty tray
        if (SelectedRecorder is VirtualDiscRecorder vRec)
        {
            var mediaStatus = vRec.QueryMediaStatus();
            if (!mediaStatus.MediaPresent || (!mediaStatus.IsBlank && !mediaStatus.IsAppendable && !mediaStatus.IsRewritable))
            {
                vRec.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());
            }
        }

        // A used DVD+RW / DVD-RAM would be wiped by the burn: ask first.
        if (!CdBurner.App.Views.BurnTargetDialog.ConfirmReplaceExistingData(SelectedRecorder, out bool replaceAllowed))
        {
            StatusMessage = $"Disc {row.DiscNumber} not burned: the disc in the drive still has files you chose to keep.";
            row.Status = "Cancelled";
            return DataBurnResult.Failed(StatusMessage);
        }

        _activeCts?.Dispose();
        _activeCts = ct.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : new CancellationTokenSource();
        var token = _activeCts.Token;

        IsBusy = true;
        Progress = 0;
        row.Status = "Burning…";

        int count = CurrentPlan?.Discs.Count ?? Discs.Count;
        string driveName = !string.IsNullOrWhiteSpace(SelectedRecorder.VolumePath) ? SelectedRecorder.VolumePath : SelectedRecorder.UniqueId;
        StatusMessage = $"Insert a blank disc for disc {row.DiscNumber} of {count} in {driveName}, then burning starts…";

        try
        {
            string setId = CurrentPlan?.SetId ?? "ARCHIV";
            if (setId.Length > 6)
            {
                setId = setId[..6];
            }
            string label = $"ARC{setId}_{row.DiscNumber:D2}".ToUpperInvariant();

            var root = CompilationFolderImporter.ImportAsRoot(row.StagedPath, strict: true);

            var buildOptions = new IsoBuildOptions(
                VolumeLabel: label,
                IncludeIso9660: true,
                IncludeJoliet: true,
                IncludeUdf: true,
                BootConfiguration: null);

            var burnOptions = new DataBurnOptions
            {
                VolumeLabel = label,
                CloseDisc = true,
                VerifyAfterBurn = VerifyAfterBurn,
                EjectAfterBurn = false,
                OverwriteExistingData = replaceAllowed
            };

            var burnService = new DataBurnService(_burnEngine);
            var progressHandler = new Progress<BurnProgressSnapshot>(snap =>
            {
                if (!_isBusy) return;
                if (snap.TotalLba > 0)
                {
                    Progress = Math.Clamp((double)snap.CurrentLba / snap.TotalLba * 100.0, 0, 100);
                }
                else
                {
                    Progress = Math.Clamp(snap.BufferLevelPercent, 0, 100);
                }

                if (!string.IsNullOrEmpty(snap.CurrentActionDescription))
                {
                    StatusMessage = $"{snap.CurrentActionDescription} ({Progress:F0}%)";
                }
            });

            var result = await burnService.BurnCompilationAsync(SelectedRecorder, root, burnOptions, buildOptions, progressHandler, token).ConfigureAwait(true);

            if (result.Success)
            {
                Progress = 100.0;
                row.Status = "Burned";
                StatusMessage = $"Disc {row.DiscNumber} burned successfully.";

                if (VerifyAfterBurn && !SelectedRecorder.IsVirtual && !string.IsNullOrWhiteSpace(SelectedRecorder.VolumePath))
                {
                    StatusMessage = $"Verifying disc {row.DiscNumber} in {SelectedRecorder.VolumePath}...";
                    var verifyResult = await VerifyAsync(SelectedRecorder.VolumePath, token);
                    if (verifyResult != null && verifyResult.IsIntact)
                    {
                        row.Status = "Burned + verified";
                    }
                    else
                    {
                        row.Status = "Burned - verification found problems";
                    }
                }
            }
            else
            {
                row.Status = $"Failed: {result.ErrorMessage}";
                StatusMessage = $"Burn failed: {result.ErrorMessage}";
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            row.Status = "Failed: Cancelled";
            StatusMessage = "Burn cancelled.";
            return DataBurnResult.Cancelled("Burn cancelled.");
        }
        catch (Exception ex)
        {
            row.Status = $"Failed: {ex.Message}";
            StatusMessage = $"Burn error: {ex.Message}";
            return DataBurnResult.Failed(ex.Message, ex);
        }
        finally
        {
            _activeCts?.Dispose();
            _activeCts = null;
            IsBusy = false;
        }
    }

    /// <summary>
    /// Verifies all files in the specified directory or optical disc against its checksum manifest.
    /// </summary>
    /// <param name="path">The disc root or local directory path.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Verification result, or null on error.</returns>
    public async Task<ArchiveVerificationResult?> VerifyAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            StatusMessage = "Verify path is empty.";
            return null;
        }

        string normalized = path.Trim();
        if (normalized.Length == 2 && normalized[1] == ':')
        {
            normalized += "\\";
        }
        VerifyPath = normalized;

        _activeCts?.Dispose();
        _activeCts = ct.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : new CancellationTokenSource();
        var token = _activeCts.Token;

        IsBusy = true;
        Progress = 0;
        StatusMessage = $"Verifying archive at {normalized}...";
        VerificationProblems.Clear();

        try
        {
            var progressHandler = new Progress<string>(msg =>
            {
                StatusMessage = msg;
            });

            var result = await ArchiveManifest.VerifyAsync(normalized, progressHandler, token).ConfigureAwait(true);
            LastVerification = result;
            Progress = 100.0;

            foreach (var m in result.Missing)
            {
                VerificationProblems.Add($"MISSING  {m}");
            }
            foreach (var c in result.Corrupt)
            {
                VerificationProblems.Add($"CORRUPT  {c}");
            }

            if (result.IsIntact)
            {
                string discInfo = (result.DiscNumber != null && result.DiscCount != null && result.SetId != null)
                    ? $" (disc {result.DiscNumber} of {result.DiscCount}, set {result.SetId})"
                    : "";
                VerificationSummary = $"Intact - {result.FilesChecked:N0} files checked{discInfo}";
                StatusMessage = $"Verification passed: all {result.FilesChecked:N0} files intact.";
            }
            else
            {
                int problemCount = result.Missing.Count + result.Corrupt.Count;
                string probWord = problemCount == 1 ? "problem" : "problems";
                VerificationSummary = $"{problemCount} {probWord} found ({result.Missing.Count} missing, {result.Corrupt.Count} corrupt)";
                StatusMessage = $"Verification finished: {problemCount} {probWord} found.";
            }

            UpdateCanRepair();
            return result;
        }
        catch (FileNotFoundException)
        {
            LastVerification = null;
            VerificationSummary = $"No IgnisDisc archive manifest found at {normalized}";
            StatusMessage = VerificationSummary;
            UpdateCanRepair();
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            LastVerification = null;
            VerificationSummary = $"Directory not found: {normalized}";
            StatusMessage = VerificationSummary;
            UpdateCanRepair();
            return null;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Verification cancelled.";
            UpdateCanRepair();
            return null;
        }
        catch (Exception ex)
        {
            LastVerification = null;
            VerificationSummary = $"Verification error: {ex.Message}";
            StatusMessage = VerificationSummary;
            UpdateCanRepair();
            return null;
        }
        finally
        {
            _activeCts?.Dispose();
            _activeCts = null;
            IsBusy = false;
        }
    }

    /// <summary>
    /// Executes in-place PAR2 recovery on damaged files in <see cref="VerifyPath"/> and re-verifies the folder.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if all files are intact after repair; false otherwise.</returns>
    public async Task<bool> RepairAsync(CancellationToken ct = default)
    {
        if (!CanRepair)
        {
            StatusMessage = "Repair is not available for the current verification.";
            return false;
        }

        string? par2Path = Par2Runner.FindPar2Executable(_customPar2Path);
        if (string.IsNullOrEmpty(par2Path))
        {
            StatusMessage = "par2 executable not found.";
            return false;
        }

        _activeCts?.Dispose();
        _activeCts = ct.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : new CancellationTokenSource();
        var token = _activeCts.Token;

        IsBusy = true;
        Progress = 0;
        StatusMessage = $"Repairing archive at {VerifyPath}...";

        try
        {
            Par2RepairResult repair = await Par2Runner.RepairAsync(par2Path, VerifyPath, token).ConfigureAwait(true);
            int exitCode = repair.ExitCode;
            if (exitCode != 0)
            {
                StatusMessage = $"par2 repair returned exit code {exitCode}. Some files may not be repairable.";
            }

            // Remove the copies par2 made of each damaged original. Only the files this repair created are
            // touched (an archived file that happens to be called "name.1" stays), and only after a successful
            // repair: a failed run must never cost the user a copy of their data.
            if (exitCode == 0)
            {
                foreach (string backup in repair.BackupFiles)
                {
                    try
                    {
                        File.Delete(backup);
                    }
                    catch
                    {
                    }
                }
            }

            var reVerify = await VerifyAsync(VerifyPath, token).ConfigureAwait(true);
            bool success = reVerify != null && reVerify.IsIntact;
            StatusMessage = success
                ? "Archive repaired and verified intact."
                : "Archive repair completed, but verification still reported problems.";
            return success;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Repair cancelled.";
            return false;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Repair error: {ex.Message}";
            return false;
        }
        finally
        {
            _activeCts?.Dispose();
            _activeCts = null;
            IsBusy = false;
        }
    }

    /// <summary>
    /// Cancels any actively executing archive operation.
    /// </summary>
    public void CancelActiveOperation()
    {
        if (_activeCts != null && !_activeCts.IsCancellationRequested)
        {
            StatusMessage = "Cancelling archive operation...";
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
    /// Re-enumerates available optical recorders.
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

    #endregion

    #region Helper Methods

    private void UpdateCanRepair()
    {
        bool isDrive = IsDriveRoot(_verifyPath);
        CanRepair = LastVerification is { IsIntact: false, HasRecoveryData: true } && Par2Available && !isDrive;
    }

    private static bool IsDriveRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string trimmed = path.Trim();
        try
        {
            string? root = Path.GetPathRoot(trimmed);
            if (!string.IsNullOrEmpty(root))
            {
                string normalizedPath = trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
        }
        return false;
    }

    private void BrowseSourceFolder()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Select Source Folder to Archive",
            InitialDirectory = Directory.Exists(SourceFolder) ? SourceFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
        {
            SourceFolder = dlg.FolderName;
        }
    }

    private void BrowseStagingFolder()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Select Staging Destination Folder",
            InitialDirectory = Directory.Exists(StagingFolder) ? StagingFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
        {
            StagingFolder = dlg.FolderName;
        }
    }

    private void BrowseVerifyFolder()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Select Archive Folder or Drive Root to Verify",
            InitialDirectory = Directory.Exists(VerifyPath) ? VerifyPath : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
        {
            VerifyPath = dlg.FolderName;
        }
    }

    /// <summary>
    /// Formats raw byte count into standard human-readable size text.
    /// </summary>
    /// <param name="bytes">File or disc size in bytes.</param>
    /// <returns>Formatted string like "2.9 MB" or "4.21 GB".</returns>
    public static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return $"{(double)bytes / (1024L * 1024 * 1024):F2} GB";
        }
        if (bytes >= 1024L * 1024)
        {
            return $"{(double)bytes / (1024L * 1024):F1} MB";
        }
        if (bytes >= 1024)
        {
            return $"{(double)bytes / 1024.0:F1} KB";
        }
        return $"{bytes} B";
    }

    #endregion
}
