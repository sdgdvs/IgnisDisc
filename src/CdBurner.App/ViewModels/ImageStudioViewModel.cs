using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Input;
using CdBurner.App.Mvvm;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Audio;
using CdBurner.Core.Burning;
using CdBurner.Core.Images;
using CdBurner.Core.Iso;
using CdBurner.Core.Primitives;
using CdBurner.Hardware.Devices;
using CdBurner.Hardware.Simulation;
using Microsoft.Win32;

namespace CdBurner.App.ViewModels;

/// <summary>
/// View model managing Disc Image Studio operations: burning .iso and .cue image archives to disc,
/// ripping optical media to .iso or raw .bin + companion .cue sheets, and running standalone SHA-256/sector verification.
/// </summary>
public class ImageStudioViewModel : ObservableObject
{
    private readonly IDiscDeviceService _deviceService;
    private readonly IBurnEngineBackend _burnEngine;
    private readonly DataBurnService _dataBurnService;
    private readonly AudioCdBurnService _audioBurnService;
    private readonly DiscRipperService _ripperService;
    private readonly DiscVerificationService _verificationService;

    private string _sourceImagePath = string.Empty;
    private string _detectedImageFormat = "No Image Loaded";
    private long _imageSizeBytes;
    private int _imageSectorCount;
    private bool _verifyAfterBurn = true;

    private string _ripTargetFilePath = string.Empty;
    private bool _ripRawBinCueMode = false;

    private string _verificationStatusText = "Ready to verify";
    private string _calculatedSha256 = string.Empty;
    private string _discSha256 = string.Empty;
    private bool _isChecksumMatch;

    private IDiscRecorderBackend? _selectedRecorder;
    private WriteSpeedDescriptor? _selectedWriteSpeed;
    private CancellationTokenSource? _activeCts;
    private bool _isBusy;
    private string _statusMessage = "Ready";
    private double _burnProgress;
    private BurnOperationResult? _lastBurnResult;
    private RipOperationResult? _lastRipResult;
    private VerificationResult? _lastVerificationResult;

    #region Properties

    /// <summary>
    /// Optical device enumeration service.
    /// </summary>
    public IDiscDeviceService DeviceService => _deviceService;

    /// <summary>
    /// Optical burn engine backend.
    /// </summary>
    public IBurnEngineBackend BurnEngine => _burnEngine;

    /// <summary>
    /// Disc ripper orchestration service.
    /// </summary>
    public DiscRipperService RipperService => _ripperService;

    /// <summary>
    /// Sector verification service.
    /// </summary>
    public DiscVerificationService VerificationService => _verificationService;

    /// <summary>
    /// File path of the source disc image (.iso, .cue, or .bin) to inspect, burn, or verify.
    /// </summary>
    public string SourceImagePath
    {
        get => _sourceImagePath;
        set
        {
            if (SetProperty(ref _sourceImagePath, value))
            {
                InspectImage();
                BurnImageToDiscCommand.RaiseCanExecuteChanged();
                VerifyDiscAgainstImageCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Format classification detected from source image file inspection (e.g. "ISO 9660 Image", "CDRWIN CUE Sheet").
    /// </summary>
    public string DetectedImageFormat
    {
        get => _detectedImageFormat;
        set => SetProperty(ref _detectedImageFormat, value);
    }

    /// <summary>
    /// Total byte length of the inspected disc image or referenced binary payload.
    /// </summary>
    public long ImageSizeBytes
    {
        get => _imageSizeBytes;
        set
        {
            if (SetProperty(ref _imageSizeBytes, value))
            {
                OnPropertyChanged(nameof(ImageSizeFormatted));
            }
        }
    }

    /// <summary>
    /// Total logical sector count required by the inspected disc image.
    /// </summary>
    public int ImageSectorCount
    {
        get => _imageSectorCount;
        set => SetProperty(ref _imageSectorCount, value);
    }

    /// <summary>
    /// Whether to automatically execute sector/checksum verification after burning completes.
    /// </summary>
    public bool VerifyAfterBurn
    {
        get => _verifyAfterBurn;
        set => SetProperty(ref _verifyAfterBurn, value);
    }

    /// <summary>
    /// Target file path where ripped disc images will be saved (.iso or .bin).
    /// </summary>
    public string RipTargetFilePath
    {
        get => _ripTargetFilePath;
        set => SetProperty(ref _ripTargetFilePath, value);
    }

    /// <summary>
    /// When true, rips in raw 2352-byte sector mode (.bin + companion .cue);
    /// when false, rips in standard 2048-byte user data mode (.iso).
    /// </summary>
    public bool RipRawBinCueMode
    {
        get => _ripRawBinCueMode;
        set
        {
            if (SetProperty(ref _ripRawBinCueMode, value))
            {
                if (!string.IsNullOrWhiteSpace(_ripTargetFilePath))
                {
                    _ripTargetFilePath = Path.ChangeExtension(_ripTargetFilePath, value ? ".bin" : ".iso");
                    OnPropertyChanged(nameof(RipTargetFilePath));
                }
            }
        }
    }

    /// <summary>
    /// Status description of the standalone sector/checksum verification scan.
    /// </summary>
    public string VerificationStatusText
    {
        get => _verificationStatusText;
        set => SetProperty(ref _verificationStatusText, value);
    }

    /// <summary>
    /// Expected SHA-256 hexadecimal hash calculated from the source image.
    /// </summary>
    public string CalculatedSha256
    {
        get => _calculatedSha256;
        set => SetProperty(ref _calculatedSha256, value);
    }

    /// <summary>
    /// Actual SHA-256 hexadecimal hash calculated from sectors read back from optical media.
    /// </summary>
    public string DiscSha256
    {
        get => _discSha256;
        set => SetProperty(ref _discSha256, value);
    }

    /// <summary>
    /// Indicates whether the media checksum perfectly matches the source image checksum.
    /// </summary>
    public bool IsChecksumMatch
    {
        get => _isChecksumMatch;
        set => SetProperty(ref _isChecksumMatch, value);
    }

    /// <summary>
    /// Discovered optical recorder drives.
    /// </summary>
    public ObservableCollection<IDiscRecorderBackend> AvailableRecorders { get; } = new();

    /// <summary>
    /// Currently selected optical recorder drive.
    /// </summary>
    public IDiscRecorderBackend? SelectedRecorder
    {
        get => _selectedRecorder;
        set => SetProperty(ref _selectedRecorder, value);
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
    /// Indicates whether an active operation can be cancelled.
    /// </summary>
    public bool CanCancel => _isBusy;

    /// <summary>
    /// Indicates whether an asynchronous operation is in progress.
    /// </summary>
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanCancel));
                BurnImageToDiscCommand.RaiseCanExecuteChanged();
                RipDiscToImageCommand.RaiseCanExecuteChanged();
                VerifyDiscAgainstImageCommand.RaiseCanExecuteChanged();
                CancelCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Real-time status text displayed on the UI.
    /// </summary>
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>
    /// Real-time progress percentage (0.0 to 100.0).
    /// </summary>
    public double BurnProgress
    {
        get => _burnProgress;
        set => SetProperty(ref _burnProgress, value);
    }

    /// <summary>
    /// Formatted image size string in megabytes or gigabytes.
    /// </summary>
    public string ImageSizeFormatted
    {
        get
        {
            if (_imageSizeBytes <= 0) return "0 MB";
            double mb = _imageSizeBytes / (1024.0 * 1024.0);
            if (mb >= 1024)
            {
                return $"{(mb / 1024.0):F2} GB ({_imageSizeBytes:N0} bytes)";
            }
            return $"{mb:F1} MB ({_imageSizeBytes:N0} bytes)";
        }
    }

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
    public RipOperationResult? LastRipResult
    {
        get => _lastRipResult;
        set => SetProperty(ref _lastRipResult, value);
    }

    /// <summary>
    /// Result of the most recent disc verification operation.
    /// </summary>
    public VerificationResult? LastVerificationResult
    {
        get => _lastVerificationResult;
        set => SetProperty(ref _lastVerificationResult, value);
    }

    #endregion

    #region Commands

    /// <summary>
    /// Opens a file dialog to browse for a source image (.iso, .cue, or .bin).
    /// </summary>
    public RelayCommand BrowseImageCommand { get; }

    /// <summary>
    /// Inspects the current source image file and extracts format headers, size, and sector count.
    /// </summary>
    public RelayCommand InspectImageCommand { get; }

    /// <summary>
    /// Burns the selected source image file to optical disc.
    /// </summary>
    public AsyncRelayCommand BurnImageToDiscCommand { get; }

    /// <summary>
    /// Rips the inserted optical disc to an ISO or BIN/CUE disc image file.
    /// </summary>
    public AsyncRelayCommand RipDiscToImageCommand { get; }

    /// <summary>
    /// Compares sectors on the inserted optical disc bit-for-bit against the source image.
    /// </summary>
    public AsyncRelayCommand VerifyDiscAgainstImageCommand { get; }

    /// <summary>
    /// Opens a save file dialog to select the destination path for ripped disc images.
    /// </summary>
    public RelayCommand BrowseRipTargetFileCommand { get; }

    /// <summary>
    /// Cancels any active image burn, rip, or verification operation.
    /// </summary>
    public RelayCommand CancelCommand { get; }

    #endregion

    /// <summary>
    /// Initializes a new instance of <see cref="ImageStudioViewModel"/>.
    /// </summary>
    public ImageStudioViewModel(
        IDiscDeviceService? deviceService = null,
        IBurnEngineBackend? burnEngine = null)
    {
        _deviceService = deviceService ?? new DiscDeviceManager(enablePhysicalEnumeration: false);
        _burnEngine = burnEngine ?? new VirtualBurnEngine();
        _dataBurnService = new DataBurnService(_burnEngine);
        _audioBurnService = new AudioCdBurnService(_burnEngine);
        _ripperService = new DiscRipperService();
        _verificationService = new DiscVerificationService();

        try
        {
            _ripTargetFilePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "DiscImage.iso");
        }
        catch
        {
            _ripTargetFilePath = Path.Combine(Path.GetTempPath(), "DiscImage.iso");
        }

        BrowseImageCommand = new RelayCommand(BrowseImage);
        InspectImageCommand = new RelayCommand(() => InspectImage());
        BurnImageToDiscCommand = new AsyncRelayCommand(async p => await BurnImageToDiscAsync(p as string), _ => !IsBusy && !string.IsNullOrWhiteSpace(SourceImagePath));
        RipDiscToImageCommand = new AsyncRelayCommand(async p => await RipDiscToImageAsync(p as string), _ => !IsBusy);
        VerifyDiscAgainstImageCommand = new AsyncRelayCommand(async p => await VerifyDiscAgainstImageAsync(p as string), _ => !IsBusy && !string.IsNullOrWhiteSpace(SourceImagePath));
        BrowseRipTargetFileCommand = new RelayCommand(BrowseRipTargetFile);
        CancelCommand = new RelayCommand(_ => CancelActiveOperation(), _ => IsBusy);

        foreach (var r in _deviceService.GetRecorders())
        {
            AvailableRecorders.Add(r);
        }

        // Pre-select physical optical recorder if available
        SelectedRecorder = AvailableRecorders.FirstOrDefault(r => !r.IsVirtual)
                           ?? AvailableRecorders.FirstOrDefault(r => string.Equals(r.UniqueId, "VDRV-01", StringComparison.OrdinalIgnoreCase))
                           ?? AvailableRecorders.FirstOrDefault();
    }

    #region Image Inspection Operations

    /// <summary>
    /// Inspects the source image file and updates format, byte size, and sector metrics.
    /// </summary>
    public void InspectImage(string? path = null)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            _sourceImagePath = path;
            OnPropertyChanged(nameof(SourceImagePath));
        }

        if (string.IsNullOrWhiteSpace(SourceImagePath) || !File.Exists(SourceImagePath))
        {
            DetectedImageFormat = "No Image Loaded";
            ImageSizeBytes = 0;
            ImageSectorCount = 0;
            return;
        }

        string ext = Path.GetExtension(SourceImagePath).ToLowerInvariant();
        if (ext == ".iso")
        {
            var fi = new FileInfo(SourceImagePath);
            ImageSizeBytes = fi.Length;
            ImageSectorCount = OpticalConstants.BytesToSectors(ImageSizeBytes, OpticalConstants.DataSectorSize);
            DetectedImageFormat = "ISO 9660 Image";

            try
            {
                using var reader = IsoImageReader.FromFile(SourceImagePath);
                if (reader.HasJoliet)
                {
                    DetectedImageFormat = "ISO 9660 / Joliet Image";
                }
            }
            catch
            {
                // Fallback to standard ISO 9660
            }
        }
        else if (ext == ".cue")
        {
            DetectedImageFormat = "CDRWIN CUE Sheet";
            try
            {
                var cueSheet = CueSheetParser.ParseFile(SourceImagePath);
                string cueDir = Path.GetDirectoryName(SourceImagePath) ?? "";
                long totalBytes = 0;
                int totalSectors = 0;

                foreach (var file in cueSheet.Files)
                {
                    string refPath = Path.IsPathRooted(file.FileName) ? file.FileName : Path.Combine(cueDir, file.FileName);
                    if (File.Exists(refPath))
                    {
                        var fi = new FileInfo(refPath);
                        totalBytes += fi.Length;
                        bool isRaw = file.Tracks.Any(t => t.DataType == CueTrackDataType.Mode1_2352 || t.DataType == CueTrackDataType.Mode2_2352 || t.DataType == CueTrackDataType.Audio);
                        int secSize = isRaw ? OpticalConstants.RawSectorSize : OpticalConstants.DataSectorSize;
                        totalSectors += OpticalConstants.BytesToSectors(fi.Length, secSize);
                    }
                }

                if (totalBytes == 0)
                {
                    var fi = new FileInfo(SourceImagePath);
                    totalBytes = fi.Length;
                }

                if (totalSectors == 0)
                {
                    int maxLba = 0;
                    foreach (var f in cueSheet.Files)
                    {
                        foreach (var t in f.Tracks)
                        {
                            foreach (var idx in t.Indices)
                            {
                                if (idx.Position.ToLba() > maxLba) maxLba = idx.Position.ToLba();
                            }
                        }
                    }
                    totalSectors = maxLba > 0 ? maxLba : OpticalConstants.BytesToSectors(totalBytes, OpticalConstants.RawSectorSize);
                }

                ImageSizeBytes = totalBytes;
                ImageSectorCount = totalSectors;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to parse CUE sheet: {ex.Message}";
            }
        }
        else if (ext == ".bin")
        {
            var fi = new FileInfo(SourceImagePath);
            ImageSizeBytes = fi.Length;
            ImageSectorCount = OpticalConstants.BytesToSectors(ImageSizeBytes, OpticalConstants.RawSectorSize);
            DetectedImageFormat = "Raw BIN Image (2352-byte sectors)";
        }
        else
        {
            var fi = new FileInfo(SourceImagePath);
            ImageSizeBytes = fi.Length;
            ImageSectorCount = OpticalConstants.BytesToSectors(ImageSizeBytes, OpticalConstants.DataSectorSize);
            DetectedImageFormat = "Generic Disc Image";
        }

        OnPropertyChanged(nameof(ImageSizeFormatted));
    }

    private void BrowseImage()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select Disc Image to Open",
            Filter = "Disc Images (*.iso;*.cue;*.bin)|*.iso;*.cue;*.bin|ISO Images (*.iso)|*.iso|CUE Sheets (*.cue)|*.cue|Raw Binary Images (*.bin)|*.bin|All Files (*.*)|*.*"
        };

        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FileName))
        {
            SourceImagePath = dlg.FileName;
        }
    }

    private void BrowseRipTargetFile()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Select Destination Path for Ripped Disc Image",
            Filter = RipRawBinCueMode
                ? "Raw Disc Image (*.bin)|*.bin|All Files (*.*)|*.*"
                : "Standard ISO Image (*.iso)|*.iso|All Files (*.*)|*.*",
            FileName = Path.GetFileName(RipTargetFilePath)
        };

        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FileName))
        {
            RipTargetFilePath = dlg.FileName;
        }
    }

    #endregion

    #region Burning Operations

    /// <summary>
    /// Cancels any actively executing image burn, rip, or verification operation.
    /// </summary>
    public void CancelActiveOperation()
    {
        if (_activeCts != null && !_activeCts.IsCancellationRequested)
        {
            StatusMessage = "Cancelling operation...";
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
    /// Burns the current source image to the target optical recorder drive.
    /// </summary>
    public async Task<BurnOperationResult> BurnImageToDiscAsync(string? driveId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(SourceImagePath) || !File.Exists(SourceImagePath))
        {
            StatusMessage = "Source disc image file does not exist.";
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
                $"Burn Disc Image ({Path.GetFileName(SourceImagePath)})");

            if (confirmed == null)
            {
                StatusMessage = "Image burn cancelled by user.";
                var cancelled = BurnOperationResult.CancelledResult(StatusMessage);
                LastBurnResult = cancelled;
                return cancelled;
            }

            SelectedRecorder = confirmed;
        }

        var recorder = ResolveRecorder(driveId);
        if (recorder == null)
        {
            StatusMessage = "No optical recorder drive selected or available.";
            var fail = BurnOperationResult.Failed(StatusMessage);
            LastBurnResult = fail;
            return fail;
        }

        // Auto-mount blank media in virtual drive if empty
        if (recorder is VirtualDiscRecorder vRec)
        {
            var mediaStatus = vRec.QueryMediaStatus();
            if (!mediaStatus.MediaPresent || (!mediaStatus.IsBlank && !mediaStatus.IsAppendable && !mediaStatus.IsRewritable))
            {
                if (ImageSizeBytes > 700 * 1024 * 1024)
                {
                    vRec.InsertMedia(VirtualDiscMedium.CreateBlankDvdR47());
                }
                else
                {
                    vRec.InsertMedia(VirtualDiscMedium.CreateBlankCdR700());
                }
            }
        }

        _activeCts?.Dispose();
        _activeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var linkedToken = _activeCts.Token;

        IsBusy = true;
        BurnProgress = 0;
        StatusMessage = $"Burning image '{Path.GetFileName(SourceImagePath)}' to {recorder.VolumePath} ({recorder.ProductId})...";

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

            string ext = Path.GetExtension(SourceImagePath).ToLowerInvariant();
            BurnOperationResult burnResult;

            if (ext == ".iso")
            {
                var dataResult = await _dataBurnService.BurnIsoFileAsync(
                    recorder,
                    SourceImagePath,
                    new DataBurnOptions
                    {
                        VolumeLabel = Path.GetFileNameWithoutExtension(SourceImagePath),
                        CloseDisc = true,
                        Speed = SelectedWriteSpeed
                    },
                    progress,
                    linkedToken);

                burnResult = dataResult.Success
                    ? BurnOperationResult.Succeeded(dataResult.SectorsWritten, dataResult.TotalTime, dataResult.AverageSpeedMultiplier)
                    : BurnOperationResult.Failed(dataResult.ErrorMessage ?? "Burn failed.");
            }
            else if (ext == ".cue")
            {
                burnResult = await BurnCueSheetAsync(recorder, SourceImagePath, progress, linkedToken);
            }
            else // .bin or raw stream
            {
                await using var fs = new FileStream(SourceImagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                burnResult = await _burnEngine.BurnDataStreamAsync(
                    recorder,
                    fs,
                    new DataBurnOptions { CloseDisc = true, Speed = SelectedWriteSpeed },
                    progress,
                    linkedToken);
            }

            LastBurnResult = burnResult;

            if (burnResult.Success)
            {
                StatusMessage = $"Image burned successfully ({burnResult.SectorsWritten:N0} sectors, {burnResult.TotalTime}).";
                BurnProgress = 100.0;

                // Automatic verification
                if (VerifyAfterBurn)
                {
                    StatusMessage = "Burn complete. Verifying disc bit-for-bit against image...";
                    await VerifyDiscAgainstImageAsync(recorder.UniqueId, linkedToken);
                    StatusMessage = VerificationStatusText;
                }
            }
            else
            {
                StatusMessage = $"Burn failed: {burnResult.ErrorMessage}";
            }

            return burnResult;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Image burn cancelled by user.";
            var cancelled = BurnOperationResult.CancelledResult(StatusMessage);
            LastBurnResult = cancelled;
            return cancelled;
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
            _activeCts?.Dispose();
            _activeCts = null;
            IsBusy = false;
        }
    }

    private async Task<BurnOperationResult> BurnCueSheetAsync(
        IDiscRecorderBackend recorder,
        string cuePath,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken ct)
    {
        var cueSheet = CueSheetParser.ParseFile(cuePath);
        string cueDir = Path.GetDirectoryName(cuePath) ?? "";

        // Check if Audio CUE sheet
        bool hasAudio = cueSheet.Files.Any(f => f.Tracks.Any(t => t.DataType == CueTrackDataType.Audio));
        if (hasAudio)
        {
            var compilation = new AudioCdCompilation
            {
                AlbumTitle = cueSheet.Title ?? Path.GetFileNameWithoutExtension(cuePath),
                AlbumPerformer = cueSheet.Performer ?? "Various Artists",
                Songwriter = cueSheet.Songwriter,
                RecordingMode = AudioRecordingMode.DiscAtOnceGapless,
                IncludeCdText = !string.IsNullOrEmpty(cueSheet.Title) || !string.IsNullOrEmpty(cueSheet.Performer)
            };

            int trackNumber = 1;
            foreach (var file in cueSheet.Files)
            {
                string audioFile = Path.IsPathRooted(file.FileName) ? file.FileName : Path.Combine(cueDir, file.FileName);
                foreach (var trk in file.Tracks)
                {
                    compilation.AddTrack(new AudioCdTrackItem(
                        TrackNumber: trk.TrackNumber > 0 ? trk.TrackNumber : trackNumber,
                        SourceFilePath: audioFile,
                        Title: trk.Title ?? $"Track {trackNumber:D2}",
                        Performer: trk.Performer ?? compilation.AlbumPerformer,
                        Isrc: trk.Isrc,
                        PreGapFrames: trk.PreGap?.ToFrameCount() ?? (trackNumber == 1 ? 150 : 0)
                    )
                    {
                        Songwriter = trk.Songwriter
                    });
                    trackNumber++;
                }
            }

            return await _audioBurnService.BurnAsync(
                recorder,
                compilation,
                new AudioBurnOptions { UseDiscAtOnce = true, WriteCdText = compilation.IncludeCdText, Speed = SelectedWriteSpeed },
                progress,
                ct);
        }

        // Binary / Data CUE sheet
        var firstFile = cueSheet.Files.FirstOrDefault();
        if (firstFile == null)
        {
            return BurnOperationResult.Failed("CUE sheet contains no file entries.");
        }

        string binPath = Path.IsPathRooted(firstFile.FileName) ? firstFile.FileName : Path.Combine(cueDir, firstFile.FileName);
        if (!File.Exists(binPath))
        {
            return BurnOperationResult.Failed($"Referenced binary file not found: '{binPath}'.");
        }

        bool isRaw = firstFile.Tracks.Any(t => t.DataType == CueTrackDataType.Mode1_2352 || t.DataType == CueTrackDataType.Mode2_2352);
        int sectorSize = isRaw ? OpticalConstants.RawSectorSize : OpticalConstants.DataSectorSize;

        if (recorder is VirtualDiscRecorder vRec && vRec.CurrentMedium != null)
        {
            vRec.CurrentMedium.Raw2352Mode = isRaw;
            await using var binStream = new FileStream(binPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] sectorBuf = new byte[sectorSize];
            int lba = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int bytesRead = 0;
                while (bytesRead < sectorSize)
                {
                    int r = await binStream.ReadAsync(sectorBuf.AsMemory(bytesRead, sectorSize - bytesRead), ct);
                    if (r == 0) break;
                    bytesRead += r;
                }

                if (bytesRead == 0) break;
                if (bytesRead < sectorSize)
                {
                    Array.Clear(sectorBuf, bytesRead, sectorSize - bytesRead);
                }

                vRec.CurrentMedium.WriteSector(lba++, sectorBuf, sectorSize);
            }

            vRec.CurrentMedium.UsedSectors = lba;
            var session = new VirtualSession { SessionNumber = 1, StartLba = 0, SectorCount = lba };
            foreach (var trk in firstFile.Tracks)
            {
                int trkStart = trk.Indices.Count > 0 ? trk.Indices[0].Position.ToLba() : 0;
                session.Tracks.Add(new VirtualTrack
                {
                    TrackNumber = trk.TrackNumber,
                    StartLba = trkStart,
                    SectorCount = Math.Max(0, lba - trkStart),
                    IsAudio = false,
                    Title = trk.Title,
                    Performer = trk.Performer
                });
            }
            vRec.CurrentMedium.Sessions.Add(session);
            vRec.CurrentMedium.IsFinalized = true;

            return BurnOperationResult.Succeeded(lba, TimeSpan.FromMilliseconds(50), 24.0);
        }

        // Generic stream burn fallback
        await using var stream = new FileStream(binPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await _burnEngine.BurnDataStreamAsync(recorder, stream, new DataBurnOptions { CloseDisc = true, Speed = SelectedWriteSpeed }, progress, ct);
    }

    #endregion

    #region Ripping Operations

    /// <summary>
    /// Alias for <see cref="RipDiscToImageAsync"/>.
    /// </summary>
    public Task<RipOperationResult> RipDiscToIsoAsync(
        string? driveId = null,
        string? targetPath = null,
        bool? rawMode = null,
        CancellationToken ct = default)
        => RipDiscToImageAsync(driveId, targetPath, rawMode, ct);

    /// <summary>
    /// Rips the inserted optical disc to an ISO or BIN/CUE disc image file.
    /// </summary>
    public async Task<RipOperationResult> RipDiscToImageAsync(
        string? driveId = null,
        string? targetPath = null,
        bool? rawMode = null,
        CancellationToken ct = default)
    {
        var recorder = ResolveRecorder(driveId);
        if (recorder == null)
        {
            StatusMessage = "No optical recorder drive selected or available.";
            var fail = RipOperationResult.Failed(StatusMessage);
            LastRipResult = fail;
            return fail;
        }

        string outputPath = !string.IsNullOrWhiteSpace(targetPath)
            ? targetPath
            : (!string.IsNullOrWhiteSpace(RipTargetFilePath) ? RipTargetFilePath : Path.Combine(Path.GetTempPath(), "ripped_disc.iso"));

        bool raw = rawMode ?? (RipRawBinCueMode || outputPath.EndsWith(".bin", StringComparison.OrdinalIgnoreCase));

        _activeCts?.Dispose();
        _activeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var linkedToken = _activeCts.Token;

        IsBusy = true;
        BurnProgress = 0;
        StatusMessage = $"Ripping disc from {recorder.UniqueId} to '{Path.GetFileName(outputPath)}'...";

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

            RipOperationResult ripResult;
            if (raw)
            {
                string companionCue = Path.ChangeExtension(outputPath, ".cue");
                ripResult = await _ripperService.RipToBinCueAsync(recorder, _burnEngine, outputPath, companionCue, progress: progress, ct: linkedToken);
            }
            else
            {
                ripResult = await _ripperService.RipToIsoAsync(recorder, _burnEngine, outputPath, progress: progress, ct: linkedToken);
            }

            LastRipResult = ripResult;

            if (ripResult.Success)
            {
                StatusMessage = $"Disc ripped successfully ({ripResult.SectorsRead:N0} sectors, {ripResult.TotalTime}).";
                BurnProgress = 100.0;
            }
            else
            {
                StatusMessage = $"Rip failed: {ripResult.ErrorMessage}";
            }

            return ripResult;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Disc rip cancelled by user.";
            var fail = RipOperationResult.Failed(StatusMessage);
            LastRipResult = fail;
            return fail;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Rip error: {ex.Message}";
            var fail = RipOperationResult.Failed(ex.Message);
            LastRipResult = fail;
            return fail;
        }
        finally
        {
            _activeCts?.Dispose();
            _activeCts = null;
            IsBusy = false;
        }
    }

    #endregion

    #region Verification Operations

    /// <summary>
    /// Compares sectors on the inserted optical disc bit-for-bit against the source image.
    /// </summary>
    public async Task<VerificationResult> VerifyDiscAgainstImageAsync(string? driveId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(SourceImagePath) || !File.Exists(SourceImagePath))
        {
            VerificationStatusText = "Source disc image file does not exist.";
            var fail = new VerificationResult { IsMatch = false, ErrorMessage = VerificationStatusText };
            LastVerificationResult = fail;
            return fail;
        }

        var recorder = ResolveRecorder(driveId);
        if (recorder == null)
        {
            VerificationStatusText = "No optical recorder drive selected or available.";
            var fail = new VerificationResult { IsMatch = false, ErrorMessage = VerificationStatusText };
            LastVerificationResult = fail;
            return fail;
        }

        bool ownsOperation = _activeCts == null;
        if (ownsOperation)
        {
            _activeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            IsBusy = true;
        }
        var linkedToken = _activeCts!.Token;

        BurnProgress = 0;
        VerificationStatusText = $"Verifying {recorder.UniqueId} bit-for-bit against '{Path.GetFileName(SourceImagePath)}'...";

        try
        {
            var progress = new Progress<BurnProgressSnapshot>(p =>
            {
                if (!_isBusy) return;
                if (p.TotalLba > 0)
                {
                    BurnProgress = Math.Min(100.0, (double)p.CurrentLba / p.TotalLba * 100.0);
                }
                VerificationStatusText = $"{p.CurrentActionDescription} ({BurnProgress:F0}%)";
            });

            string ext = Path.GetExtension(SourceImagePath).ToLowerInvariant();
            VerificationResult result;

            if (ext == ".iso")
            {
                result = await _verificationService.VerifyDiscAgainstIsoFileAsync(
                    recorder,
                    _burnEngine,
                    SourceImagePath,
                    progress: progress,
                    ct: linkedToken);
            }
            else if (ext == ".cue")
            {
                result = await VerifyCueSheetAsync(recorder, SourceImagePath, progress, linkedToken);
            }
            else // .bin or raw
            {
                await using var fs = new FileStream(SourceImagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                result = await _verificationService.VerifyDiscAgainstStreamAsync(
                    recorder,
                    _burnEngine,
                    fs,
                    raw2352Mode: true,
                    progress: progress,
                    ct: linkedToken);
            }

            LastVerificationResult = result;
            CalculatedSha256 = result.ExpectedSha256;
            DiscSha256 = result.ActualSha256;
            IsChecksumMatch = result.IsMatch;

            if (result.IsMatch)
            {
                VerificationStatusText = $"Verification PASSED: SHA-256 Checksums match bit-for-bit ({result.TotalSectorsVerified:N0} sectors verified).";
                BurnProgress = 100.0;
            }
            else
            {
                VerificationStatusText = $"Verification FAILED: {result.MismatchedLbas.Count} sector mismatch(es). {result.ErrorMessage}";
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            VerificationStatusText = "Verification cancelled by user.";
            var fail = new VerificationResult { IsMatch = false, ErrorMessage = VerificationStatusText };
            LastVerificationResult = fail;
            IsChecksumMatch = false;
            return fail;
        }
        catch (Exception ex)
        {
            VerificationStatusText = $"Verification error: {ex.Message}";
            var fail = new VerificationResult { IsMatch = false, ErrorMessage = ex.Message };
            LastVerificationResult = fail;
            IsChecksumMatch = false;
            return fail;
        }
        finally
        {
            if (ownsOperation)
            {
                _activeCts?.Dispose();
                _activeCts = null;
                IsBusy = false;
            }
        }
    }

    private async Task<VerificationResult> VerifyCueSheetAsync(
        IDiscRecorderBackend recorder,
        string cuePath,
        IProgress<BurnProgressSnapshot>? progress,
        CancellationToken ct)
    {
        var cueSheet = CueSheetParser.ParseFile(cuePath);
        string cueDir = Path.GetDirectoryName(cuePath) ?? "";
        var firstFile = cueSheet.Files.FirstOrDefault();

        if (firstFile == null)
        {
            return new VerificationResult { IsMatch = false, ErrorMessage = "CUE sheet has no files to verify against." };
        }

        string binPath = Path.IsPathRooted(firstFile.FileName) ? firstFile.FileName : Path.Combine(cueDir, firstFile.FileName);
        if (!File.Exists(binPath))
        {
            return new VerificationResult { IsMatch = false, ErrorMessage = $"Referenced binary file '{binPath}' not found." };
        }

        bool isRaw = firstFile.Tracks.Any(t => t.DataType == CueTrackDataType.Mode1_2352 || t.DataType == CueTrackDataType.Mode2_2352 || t.DataType == CueTrackDataType.Audio);
        await using var fs = new FileStream(binPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        return await _verificationService.VerifyDiscAgainstStreamAsync(
            recorder,
            _burnEngine,
            fs,
            raw2352Mode: isRaw,
            progress: progress,
            ct: ct);
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
