using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using CdBurner.App.Mvvm;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Burning;
using CdBurner.Core.Compilation;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;

namespace CdBurner.App.ViewModels;

/// <summary>
/// View model orchestrating Data Disc mastering, ISO/Joliet/UDF filesystem configuration,
/// virtual folder navigation, El Torito BIOS/UEFI boot options, capacity assessment, and burning.
/// </summary>
public class DataBurnViewModel : ObservableObject
{
    private readonly IDiscDeviceService _deviceService;
    private readonly IBurnEngineBackend _burnEngine;

    private string _volumeLabel = "DATA_DISC";
    private FileSystemPreset _selectedFileSystemPreset = FileSystemPreset.IsoJolietUdf;
    private bool _closeDisc = true;
    private bool _verifyAfterBurn = false;
    private bool _isBootableEnabled = false;
    private string? _biosBootImagePath;
    private string? _uefiBootImagePath;
    private string _currentFolderPath = "/";
    private CompilationItemViewModel? _selectedItem;
    private CapacityAssessment _capacity = CompilationCapacityCalculator.AssessCapacity(0, DiscMediaType.CdR);
    private IDiscRecorderBackend? _selectedRecorder;
    private bool _isBusy;
    private string _statusMessage = "Ready";
    private double _burnProgress;
    private string? _lastExportedIsoPath;
    private bool _canNavigateUp;

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
    /// In-memory virtual data disc compilation hierarchy.
    /// </summary>
    public DataCompilation Compilation { get; }

    /// <summary>
    /// Volume identifier label written to primary and supplementary descriptors.
    /// </summary>
    public string VolumeLabel
    {
        get => _volumeLabel;
        set
        {
            if (SetProperty(ref _volumeLabel, value))
            {
                Compilation.VolumeLabel = value;
            }
        }
    }

    /// <summary>
    /// Active filesystem preset profile governing ISO 9660, Joliet, and UDF emission.
    /// </summary>
    public FileSystemPreset SelectedFileSystemPreset
    {
        get => _selectedFileSystemPreset;
        set
        {
            if (SetProperty(ref _selectedFileSystemPreset, value))
            {
                RecalculateCapacity();
            }
        }
    }

    /// <summary>
    /// Whether to finalize and close the target disc medium upon burn completion.
    /// </summary>
    public bool CloseDisc
    {
        get => _closeDisc;
        set => SetProperty(ref _closeDisc, value);
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
    /// Enables or disables El Torito bootable disc authoring with BIOS and/or UEFI boot streams.
    /// </summary>
    public bool IsBootableEnabled
    {
        get => _isBootableEnabled;
        set
        {
            if (SetProperty(ref _isBootableEnabled, value))
            {
                RecalculateCapacity();
            }
        }
    }

    /// <summary>
    /// Path to x86 BIOS boot image file (e.g. "etfsboot.com").
    /// </summary>
    public string? BiosBootImagePath
    {
        get => _biosBootImagePath;
        set
        {
            if (SetProperty(ref _biosBootImagePath, value))
            {
                RecalculateCapacity();
            }
        }
    }

    /// <summary>
    /// Path to EFI/UEFI boot image file (e.g. "efisys.bin").
    /// </summary>
    public string? UefiBootImagePath
    {
        get => _uefiBootImagePath;
        set
        {
            if (SetProperty(ref _uefiBootImagePath, value))
            {
                RecalculateCapacity();
            }
        }
    }

    /// <summary>
    /// Optional stream factory for in-memory BIOS boot image testing without files on disk.
    /// </summary>
    public Func<Stream>? BiosBootImageStreamFactory { get; set; }

    /// <summary>
    /// Optional stream factory for in-memory UEFI boot image testing without files on disk.
    /// </summary>
    public Func<Stream>? UefiBootImageStreamFactory { get; set; }

    /// <summary>
    /// Current virtual directory path viewed in the explorer grid (e.g. "/", "/Documents").
    /// </summary>
    public string CurrentFolderPath
    {
        get => _currentFolderPath;
        set
        {
            if (SetProperty(ref _currentFolderPath, value))
            {
                RefreshCurrentItems();
            }
        }
    }

    /// <summary>
    /// Child files and subdirectories located within the current virtual directory.
    /// </summary>
    public ObservableCollection<CompilationItemViewModel> CurrentItems { get; } = new();

    /// <summary>
    /// Real-time assessment of compilation byte and sector requirements relative to media capacity.
    /// </summary>
    public CapacityAssessment Capacity
    {
        get => _capacity;
        private set
        {
            if (SetProperty(ref _capacity, value))
            {
                OnPropertyChanged(nameof(CapacityUsedMbText));
                OnPropertyChanged(nameof(CapacityStatusText));
                OnPropertyChanged(nameof(TotalFilesCount));
                OnPropertyChanged(nameof(TotalBytes));
            }
        }
    }

    /// <summary>
    /// Currently highlighted file or folder item in the explorer view.
    /// </summary>
    public CompilationItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetProperty(ref _selectedItem, value))
            {
                RemoveSelectedItemCommand.RaiseCanExecuteChanged();
            }
        }
    }

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
                RecalculateCapacity();
            }
        }
    }

    /// <summary>
    /// Discovered optical recorder drives (physical and virtual).
    /// </summary>
    public ObservableCollection<IDiscRecorderBackend> AvailableRecorders { get; } = new();

    /// <summary>
    /// Indicates whether navigating up to parent directory is possible.
    /// </summary>
    public bool CanNavigateUp
    {
        get => _canNavigateUp;
        private set => SetProperty(ref _canNavigateUp, value);
    }

    /// <summary>
    /// Total count of all files contained recursively in this compilation.
    /// </summary>
    public int TotalFilesCount => Compilation.GetAllFiles().Count;

    /// <summary>
    /// Total combined payload size in bytes of all files in compilation.
    /// </summary>
    public long TotalBytes => Compilation.GetAllFiles().Sum(f => f.SizeBytes);

    /// <summary>
    /// Formatted megabytes consumed by the compilation.
    /// </summary>
    public string CapacityUsedMbText => $"{Capacity.TotalBytes / (1024.0 * 1024.0):F1} MB";

    /// <summary>
    /// Status description of capacity classification.
    /// </summary>
    public string CapacityStatusText => Capacity.Status switch
    {
        CapacityStatus.FitsComfortably => "Fits Comfortably",
        CapacityStatus.NearCapacityWarning => "Near Capacity (>95%)",
        CapacityStatus.OverburnRequired => "Overburn Required",
        CapacityStatus.ExceedsMediaCapacity => "Exceeds Capacity",
        _ => "Ready"
    };

    /// <summary>
    /// Indicates whether a long-running burning or mastering task is actively executing.
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
    /// Absolute path of the most recently exported ISO file.
    /// </summary>
    public string? LastExportedIsoPath
    {
        get => _lastExportedIsoPath;
        private set => SetProperty(ref _lastExportedIsoPath, value);
    }

    #endregion

    #region Commands

    public RelayCommand AddFilesCommand { get; }
    public RelayCommand AddFolderCommand { get; }
    public RelayCommand CreateVirtualFolderCommand { get; }
    public RelayCommand RemoveSelectedItemCommand { get; }
    public RelayCommand NavigateUpFolderCommand { get; }
    public RelayCommand BrowseBiosBootImageCommand { get; }
    public RelayCommand BrowseUefiBootImageCommand { get; }
    public AsyncRelayCommand ImportPreviousSessionCommand { get; }
    public AsyncRelayCommand ExportIsoCommand { get; }
    public AsyncRelayCommand StartDataBurnCommand { get; }

    #endregion

    public DataBurnViewModel(
        IDiscDeviceService? deviceService = null,
        IBurnEngineBackend? burnEngine = null)
    {
        _deviceService = deviceService ?? new DiscDeviceManager(enablePhysicalEnumeration: false);
        _burnEngine = burnEngine ?? new VirtualBurnEngine();

        Compilation = new DataCompilation(_volumeLabel);

        // Initialize Commands
        AddFilesCommand = new RelayCommand(ExecuteAddFiles);
        AddFolderCommand = new RelayCommand(ExecuteAddFolder);
        CreateVirtualFolderCommand = new RelayCommand(ExecuteCreateVirtualFolder);
        RemoveSelectedItemCommand = new RelayCommand(ExecuteRemoveSelectedItem, _ => SelectedItem != null);
        NavigateUpFolderCommand = new RelayCommand(_ => NavigateUp(), _ => CanNavigateUp);
        BrowseBiosBootImageCommand = new RelayCommand(ExecuteBrowseBiosBootImage);
        BrowseUefiBootImageCommand = new RelayCommand(ExecuteBrowseUefiBootImage);
        ImportPreviousSessionCommand = new AsyncRelayCommand(async param => await ImportPreviousSessionAsync(param));
        ExportIsoCommand = new AsyncRelayCommand(async param =>
        {
            string? path = param as string;
            if (string.IsNullOrWhiteSpace(path) && System.Windows.Application.Current?.MainWindow?.IsLoaded == true)
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Export Compilation as ISO Image",
                    Filter = "ISO Disc Images (*.iso)|*.iso|All Files (*.*)|*.*",
                    FileName = $"{(string.IsNullOrWhiteSpace(VolumeLabel) ? "DATA_DISC" : VolumeLabel)}.iso"
                };
                if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.FileName))
                {
                    return;
                }
                path = dlg.FileName;
            }
            await ExportIsoAsync(path);
        });
        StartDataBurnCommand = new AsyncRelayCommand(async param => await StartDataBurnAsync(param));

        RefreshDrives();
        RefreshCurrentItems();
        RecalculateCapacity();
    }

    #region Explorer & Tree Operations

    /// <summary>
    /// Adds one or more physical files to the active virtual directory.
    /// </summary>
    public void AddFiles(IEnumerable<string> filePaths, string? virtualDir = null)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        string targetPath = virtualDir ?? CurrentFolderPath;
        var dir = Compilation.GetOrAddDirectory(targetPath);

        foreach (var path in filePaths)
        {
            if (File.Exists(path))
            {
                var fi = new FileInfo(path);
                dir.AddFile(new CompilationFile(path, fi.Length));
            }
        }

        RefreshCurrentItems();
        RecalculateCapacity();
    }

    /// <summary>
    /// Adds a single physical file to the active virtual directory.
    /// </summary>
    public void AddFile(string filePath, string? virtualDir = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        AddFiles(new[] { filePath }, virtualDir);
    }

    /// <summary>
    /// Adds an in-memory byte array file to the compilation.
    /// </summary>
    public void AddFile(string fileName, byte[] content, string? virtualDir = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(content);

        string targetPath = virtualDir ?? CurrentFolderPath;
        var dir = Compilation.GetOrAddDirectory(targetPath);
        dir.AddFile(fileName, content);

        RefreshCurrentItems();
        RecalculateCapacity();
    }

    /// <summary>
    /// Adds an in-memory stream factory file to the compilation.
    /// </summary>
    public void AddFile(string fileName, Func<Stream> streamFactory, long sizeBytes, string? virtualDir = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(streamFactory);

        string targetPath = virtualDir ?? CurrentFolderPath;
        var dir = Compilation.GetOrAddDirectory(targetPath);
        dir.AddFile(fileName, streamFactory, sizeBytes);

        RefreshCurrentItems();
        RecalculateCapacity();
    }

    /// <summary>
    /// Adds a physical folder from disk and imports all its child files and subdirectories.
    /// </summary>
    public void AddFolder(string folderPath, string? virtualDir = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);

        if (!Directory.Exists(folderPath))
        {
            throw new DirectoryNotFoundException($"Directory not found: {folderPath}");
        }

        var di = new DirectoryInfo(folderPath);
        string targetPath = virtualDir ?? CurrentFolderPath;
        var parentDir = Compilation.GetOrAddDirectory(targetPath);
        var childDir = parentDir.AddDirectory(di.Name);

        PopulateDirectoryFromDisk(childDir, di.FullName);

        RefreshCurrentItems();
        RecalculateCapacity();
    }

    /// <summary>
    /// Creates a virtual subdirectory in the current folder.
    /// </summary>
    public CompilationDirectory CreateVirtualFolder(string? folderName = null, string? virtualDir = null)
    {
        string name = string.IsNullOrWhiteSpace(folderName) ? "New Folder" : folderName.Trim();
        string targetPath = virtualDir ?? CurrentFolderPath;
        var parentDir = Compilation.GetOrAddDirectory(targetPath);

        // Deduplicate name if needed
        string effectiveName = name;
        int counter = 1;
        while (parentDir.Children.OfType<CompilationDirectory>().Any(d => string.Equals(d.Name, effectiveName, StringComparison.OrdinalIgnoreCase)))
        {
            effectiveName = $"{name} ({counter++})";
        }

        var newDir = parentDir.AddDirectory(effectiveName);

        RefreshCurrentItems();
        RecalculateCapacity();
        return newDir;
    }

    /// <summary>
    /// Removes a file or directory item from the compilation.
    /// </summary>
    public bool RemoveItem(CompilationItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);

        string targetPath = CurrentFolderPath;
        var dir = Compilation.GetOrAddDirectory(targetPath);

        bool removed = false;
        if (item.Node != null)
        {
            removed = dir.RemoveChild(item.Node);
        }
        else
        {
            removed = dir.RemoveChild(item.Name);
        }

        if (removed)
        {
            RefreshCurrentItems();
            RecalculateCapacity();
        }

        return removed;
    }

    /// <summary>
    /// Navigates the current folder view into the specified directory path.
    /// </summary>
    public void NavigateToFolder(string virtualPath)
    {
        if (string.IsNullOrWhiteSpace(virtualPath))
        {
            CurrentFolderPath = "/";
            return;
        }

        CurrentFolderPath = virtualPath;
    }

    /// <summary>
    /// Navigates up one folder level toward the root.
    /// </summary>
    public void NavigateUp()
    {
        if (CurrentFolderPath == "/" || string.IsNullOrEmpty(CurrentFolderPath))
        {
            return;
        }

        string normalized = CurrentFolderPath.TrimEnd('/');
        int lastSlash = normalized.LastIndexOf('/');
        if (lastSlash <= 0)
        {
            CurrentFolderPath = "/";
        }
        else
        {
            CurrentFolderPath = normalized[..lastSlash];
        }
    }

    /// <summary>
    /// Reloads the <see cref="CurrentItems"/> collection for the directory at <see cref="CurrentFolderPath"/>.
    /// </summary>
    public void RefreshCurrentItems()
    {
        CurrentItems.Clear();
        var dir = Compilation.GetOrAddDirectory(CurrentFolderPath);

        var dirs = dir.Children.OfType<CompilationDirectory>().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var subDir in dirs)
        {
            CurrentItems.Add(new CompilationItemViewModel(
                name: subDir.Name,
                virtualPath: subDir.VirtualPath,
                isDirectory: true,
                sizeBytes: 0,
                sourcePath: null,
                node: subDir));
        }

        var files = dir.Children.OfType<CompilationFile>().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            CurrentItems.Add(new CompilationItemViewModel(
                name: file.Name,
                virtualPath: file.VirtualPath,
                isDirectory: false,
                sizeBytes: file.SizeBytes,
                sourcePath: file.SourceFilePath,
                node: file));
        }

        CanNavigateUp = CurrentFolderPath != "/" && !string.IsNullOrEmpty(CurrentFolderPath);
        NavigateUpFolderCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Recalculates capacity metrics based on compilation contents, boot configuration, and filesystem preset.
    /// </summary>
    public void RecalculateCapacity()
    {
        bool includeIso = SelectedFileSystemPreset != FileSystemPreset.UdfOnly201;
        bool includeJoliet = SelectedFileSystemPreset == FileSystemPreset.IsoJolietUdf;
        bool includeUdf = SelectedFileSystemPreset == FileSystemPreset.IsoJolietUdf
                       || SelectedFileSystemPreset == FileSystemPreset.UdfOnly201
                       || SelectedFileSystemPreset == FileSystemPreset.DvdVideoBridge;

        var mediaType = DiscMediaType.CdR;
        if (SelectedRecorder != null)
        {
            var st = SelectedRecorder.QueryMediaStatus();
            if (st.MediaPresent && st.MediaType != DiscMediaType.NoMedia && st.MediaType != DiscMediaType.Unknown)
            {
                mediaType = st.MediaType;
            }
        }

        Capacity = Compilation.CalculateCapacity(
            mediaType: mediaType,
            includeIso9660: includeIso,
            includeJoliet: includeJoliet,
            includeUdf: includeUdf,
            isBootable: IsBootableEnabled);
    }

    #endregion

    #region Export & Burn Operations

    /// <summary>
    /// Builds and exports the current compilation as a standard or bootable ISO disc image file.
    /// </summary>
    public async Task<string> ExportIsoAsync(string? outputPath = null, CancellationToken cancellationToken = default)
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

        IsBusy = true;
        StatusMessage = "Compiling optical disc image...";
        try
        {
            var buildOptions = CreateIsoBuildOptions();
            await IsoUdfImageBuilder.BuildToFileAsync(Compilation.Root, outputPath, buildOptions, null, cancellationToken);
            LastExportedIsoPath = outputPath;
            StatusMessage = $"ISO image exported successfully: {Path.GetFileName(outputPath)}";
            return outputPath;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Burns the current compilation to the target optical recorder drive.
    /// </summary>
    public async Task<DataBurnResult> StartDataBurnAsync(object? parameter = null, CancellationToken cancellationToken = default)
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

        // Auto-insert simulated blank media if virtual drive has empty tray
        if (targetRecorder is VirtualDiscRecorder vRec)
        {
            var mediaStatus = vRec.QueryMediaStatus();
            if (!mediaStatus.MediaPresent || (!mediaStatus.IsBlank && !mediaStatus.IsAppendable && !mediaStatus.IsRewritable))
            {
                long bytes = Capacity.TotalBytes;
                if (bytes > 700 * 1024 * 1024)
                {
                    vRec.InsertMedia(VirtualDiscMedium.CreateBlankDvdR47());
                }
                else
                {
                    vRec.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());
                }
            }
        }

        IsBusy = true;
        StatusMessage = $"Burning data compilation to {targetRecorder.UniqueId}...";
        BurnProgress = 0;

        try
        {
            var buildOptions = CreateIsoBuildOptions();
            var burnOptions = new DataBurnOptions
            {
                VolumeLabel = VolumeLabel,
                CloseDisc = CloseDisc,
                VerifyAfterBurn = VerifyAfterBurn
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

            var result = await burnService.BurnCompilationAsync(targetRecorder, Compilation.Root, burnOptions, buildOptions, progressHandler, cancellationToken);
            StatusMessage = result.Success ? "Data burn completed successfully." : $"Burn failed: {result.ErrorMessage}";
            return result;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Imports previous session directory hierarchy from the selected recorder.
    /// </summary>
    public async Task<bool> ImportPreviousSessionAsync(object? parameter = null, CancellationToken cancellationToken = default)
    {
        IDiscRecorderBackend? targetRecorder = null;
        if (parameter is IDiscRecorderBackend rec) targetRecorder = rec;
        else if (parameter is string driveId)
        {
            targetRecorder = AvailableRecorders.FirstOrDefault(r => string.Equals(r.UniqueId, driveId, StringComparison.OrdinalIgnoreCase));
        }
        targetRecorder ??= SelectedRecorder;

        if (targetRecorder == null) return false;

        IsBusy = true;
        StatusMessage = $"Importing previous session from {targetRecorder.UniqueId}...";
        try
        {
            var importedDir = await MultiSessionManager.ImportFromRecorderAsync(targetRecorder, _burnEngine, null, cancellationToken);
            if (importedDir != null)
            {
                foreach (var child in importedDir.Children)
                {
                    if (child is CompilationFile file)
                    {
                        Compilation.Root.AddFile(file);
                    }
                    else if (child is CompilationDirectory dir)
                    {
                        Compilation.Root.AddDirectory(dir);
                    }
                }

                RefreshCurrentItems();
                RecalculateCapacity();
                StatusMessage = "Previous session imported successfully.";
                return true;
            }
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Constructs <see cref="IsoBuildOptions"/> reflecting currently configured presets, volume label, and boot parameters.
    /// </summary>
    public IsoBuildOptions CreateIsoBuildOptions()
    {
        bool includeIso = SelectedFileSystemPreset != FileSystemPreset.UdfOnly201;
        bool includeJoliet = SelectedFileSystemPreset == FileSystemPreset.IsoJolietUdf;
        bool includeUdf = SelectedFileSystemPreset == FileSystemPreset.IsoJolietUdf
                       || SelectedFileSystemPreset == FileSystemPreset.UdfOnly201
                       || SelectedFileSystemPreset == FileSystemPreset.DvdVideoBridge;
        ushort udfRev = SelectedFileSystemPreset == FileSystemPreset.UdfOnly201 ? (ushort)0x0201 : (ushort)0x0102;

        ElToritoBootConfiguration? bootConfig = null;
        if (IsBootableEnabled)
        {
            bootConfig = BuildBootConfiguration();
        }

        return new IsoBuildOptions(
            VolumeLabel: string.IsNullOrWhiteSpace(VolumeLabel) ? "DATA_DISC" : VolumeLabel,
            IncludeIso9660: includeIso,
            IncludeJoliet: includeJoliet,
            IncludeUdf: includeUdf,
            UdfRevision: udfRev,
            BootConfiguration: bootConfig);
    }

    private ElToritoBootConfiguration? BuildBootConfiguration()
    {
        BootCatalogEntrySpec? biosEntry = null;
        if (BiosBootImageStreamFactory != null)
        {
            biosEntry = new BootCatalogEntrySpec(
                Platform: BootPlatformId.X86Bios,
                Emulation: BootEmulationType.NoEmulation,
                LoadSegment: 0,
                SectorCount: 4,
                BootImageStreamFactory: BiosBootImageStreamFactory,
                Name: "boot_bios.img");
        }
        else if (!string.IsNullOrWhiteSpace(BiosBootImagePath) && File.Exists(BiosBootImagePath))
        {
            var fi = new FileInfo(BiosBootImagePath);
            ushort sectors = (ushort)Math.Max(1, (fi.Length + 511) / 512);
            biosEntry = new BootCatalogEntrySpec(
                Platform: BootPlatformId.X86Bios,
                Emulation: BootEmulationType.NoEmulation,
                LoadSegment: 0,
                SectorCount: sectors,
                BootImageStreamFactory: () => new FileStream(BiosBootImagePath, FileMode.Open, FileAccess.Read, FileShare.Read),
                Name: Path.GetFileName(BiosBootImagePath));
        }

        BootCatalogEntrySpec? uefiEntry = null;
        if (UefiBootImageStreamFactory != null)
        {
            uefiEntry = new BootCatalogEntrySpec(
                Platform: BootPlatformId.Efi,
                Emulation: BootEmulationType.NoEmulation,
                LoadSegment: 0,
                SectorCount: 64,
                BootImageStreamFactory: UefiBootImageStreamFactory,
                Name: "efisys.bin");
        }
        else if (!string.IsNullOrWhiteSpace(UefiBootImagePath) && File.Exists(UefiBootImagePath))
        {
            var fi = new FileInfo(UefiBootImagePath);
            ushort sectors = (ushort)Math.Max(1, (fi.Length + 511) / 512);
            uefiEntry = new BootCatalogEntrySpec(
                Platform: BootPlatformId.Efi,
                Emulation: BootEmulationType.NoEmulation,
                LoadSegment: 0,
                SectorCount: sectors,
                BootImageStreamFactory: () => new FileStream(UefiBootImagePath, FileMode.Open, FileAccess.Read, FileShare.Read),
                Name: Path.GetFileName(UefiBootImagePath));
        }

        string devId = !string.IsNullOrWhiteSpace(VolumeLabel) ? VolumeLabel : "CDBURNER";

        if (biosEntry != null && uefiEntry != null)
        {
            return new ElToritoBootConfiguration(
                DefaultEntry: biosEntry,
                AdditionalSectionEntries: new[] { uefiEntry },
                DeveloperId: devId);
        }

        if (biosEntry != null)
        {
            return new ElToritoBootConfiguration(
                DefaultEntry: biosEntry,
                DeveloperId: devId);
        }

        if (uefiEntry != null)
        {
            return new ElToritoBootConfiguration(
                DefaultEntry: uefiEntry,
                DeveloperId: devId);
        }

        return null;
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

    private void ExecuteAddFiles(object? parameter)
    {
        if (parameter is IEnumerable<string> paths)
        {
            AddFiles(paths);
            return;
        }

        if (parameter is string singlePath && !string.IsNullOrWhiteSpace(singlePath))
        {
            if (Directory.Exists(singlePath))
            {
                AddFolder(singlePath);
            }
            else
            {
                AddFile(singlePath);
            }
            return;
        }

        // Interactive open file dialog
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add Files to Data Compilation",
            Multiselect = true,
            Filter = "All Files (*.*)|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            AddFiles(dialog.FileNames);
        }
    }

    private void ExecuteAddFolder(object? parameter)
    {
        if (parameter is string folderPath && !string.IsNullOrWhiteSpace(folderPath))
        {
            AddFolder(folderPath);
            return;
        }

        // Interactive folder dialog
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Folder to Add to Data Compilation",
            Multiselect = false
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.FolderName))
        {
            AddFolder(dialog.FolderName);
        }
    }

    private void ExecuteCreateVirtualFolder(object? parameter)
    {
        string? name = parameter as string;
        CreateVirtualFolder(name);
    }

    private void ExecuteRemoveSelectedItem(object? parameter)
    {
        var item = parameter as CompilationItemViewModel ?? SelectedItem;
        if (item != null)
        {
            RemoveItem(item);
        }
    }

    private void ExecuteBrowseBiosBootImage(object? parameter)
    {
        if (parameter is string path && !string.IsNullOrWhiteSpace(path))
        {
            BiosBootImagePath = path;
            return;
        }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select x86 BIOS El Torito Boot Image",
            Filter = "Boot Images (*.com;*.bin;*.img;*.ima)|*.com;*.bin;*.img;*.ima|All Files (*.*)|*.*"
        };

        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FileName))
        {
            BiosBootImagePath = dlg.FileName;
        }
    }

    private void ExecuteBrowseUefiBootImage(object? parameter)
    {
        if (parameter is string path && !string.IsNullOrWhiteSpace(path))
        {
            UefiBootImagePath = path;
            return;
        }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select EFI/UEFI El Torito Boot Image",
            Filter = "UEFI Boot Images (*.bin;*.img;*.efi)|*.bin;*.img;*.efi|All Files (*.*)|*.*"
        };

        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FileName))
        {
            UefiBootImagePath = dlg.FileName;
        }
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
