using System.Collections.ObjectModel;
using System.Net.Http;
using CdBurner.App.Mvvm;
using CdBurner.App.Themes;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Archival;
using CdBurner.Core.DvdVideo;
using CdBurner.Core.Persistence;
using CdBurner.Core.Primitives;
using CdBurner.Core.Utilities;

namespace CdBurner.App.ViewModels;

/// <summary>
/// View model managing application preferences, default write speeds, overburn/buffer underrun policies,
/// scratch directories, external tool installation, online services, visual themes, and diagnostic logging configuration.
/// </summary>
public class SettingsViewModel : ObservableObject
{
    private readonly AppSettingsService _settingsService;
    private readonly MainViewModel? _mainViewModel;

    private int _defaultWriteSpeedMultiplier;
    private bool _verifyAfterBurnByDefault;
    private bool _strictVerify;
    private bool _ejectAfterBurnByDefault;
    private bool _enableBurnFreeByDefault;
    private string _tempStagingDirectory = string.Empty;
    private ThemeMode _themeMode = ThemeMode.System;
    private string _ffmpegExecutablePath = string.Empty;
    private bool _enableVirtualDrive;
    private int _logRingBufferCapacity;
    private bool _lookupMusicBrainzByDefault = true;
    private bool _verifyAccurateRipByDefault = true;
    private IDiscRecorderBackend? _selectedRecorder;
    private string _statusMessage = "Ready";
    private bool _checkForUpdatesOnStartup;
    private string _updateStatusMessage = "Update check has not been run.";
    private bool _isCheckingForUpdates;
    private bool _isInstallingTool;
    private bool _isFfmpegInstalled;
    private string _ffmpegStatusText = string.Empty;
    private bool _isPar2Installed;
    private string _par2StatusText = string.Empty;

    /// <summary>
    /// Raised when settings are saved or reset so the shell can apply changes immediately.
    /// </summary>
    public event EventHandler? SettingsSaved;

    #region Properties

    /// <summary>
    /// Available optical recorders discovered on the system.
    /// </summary>
    public ObservableCollection<IDiscRecorderBackend> AvailableRecorders { get; } = new();

    /// <summary>
    /// Currently selected default optical recorder drive.
    /// </summary>
    public IDiscRecorderBackend? SelectedRecorder
    {
        get => _selectedRecorder;
        set => SetProperty(ref _selectedRecorder, value);
    }

    /// <summary>
    /// Underlying persistent configuration service.
    /// </summary>
    public AppSettingsService SettingsService => _settingsService;

    /// <summary>
    /// Default optical burn speed multiplier (0 = Maximum supported speed).
    /// </summary>
    public int DefaultWriteSpeedMultiplier
    {
        get => _defaultWriteSpeedMultiplier;
        set => SetProperty(ref _defaultWriteSpeedMultiplier, value);
    }

    /// <summary>
    /// Whether to automatically verify written media sectors against source hash after burning.
    /// </summary>
    public bool VerifyAfterBurnByDefault
    {
        get => _verifyAfterBurnByDefault;
        set => SetProperty(ref _verifyAfterBurnByDefault, value);
    }

    /// <summary>
    /// Verify with every read forced from the disc itself (several times slower). Off: only the parts that can
    /// still be in the drive's memory after a burn are forced.
    /// </summary>
    public bool StrictVerify
    {
        get => _strictVerify;
        set => SetProperty(ref _strictVerify, value);
    }

    /// <summary>
    /// Whether to eject the optical drive tray after a burn operation completes.
    /// </summary>
    public bool EjectAfterBurnByDefault
    {
        get => _ejectAfterBurnByDefault;
        set => SetProperty(ref _ejectAfterBurnByDefault, value);
    }

    /// <summary>
    /// Whether to enable buffer underrun protection (BURN-Proof / Burn-Free) by default.
    /// </summary>
    public bool EnableBurnFreeByDefault
    {
        get => _enableBurnFreeByDefault;
        set => SetProperty(ref _enableBurnFreeByDefault, value);
    }

    /// <summary>
    /// Temporary directory path used for ISO staging, audio decoding, and DVD transcode scratch files.
    /// </summary>
    public string TempStagingDirectory
    {
        get => _tempStagingDirectory;
        set => SetProperty(ref _tempStagingDirectory, value);
    }

    /// <summary>
    /// Visual theme selection (Dark, Light, or follow Windows System preference).
    /// </summary>
    public ThemeMode ThemeMode
    {
        get => _themeMode;
        set
        {
            if (SetProperty(ref _themeMode, value))
            {
                ThemeManager.ApplyTheme(value);
            }
        }
    }

    /// <summary>
    /// Full file path or command name for the FFmpeg transcoder executable used in DVD-Video authoring.
    /// </summary>
    public string FfmpegExecutablePath
    {
        get => _ffmpegExecutablePath;
        set
        {
            if (SetProperty(ref _ffmpegExecutablePath, value))
            {
                RefreshToolStatuses();
            }
        }
    }

    /// <summary>
    /// Whether FFmpeg is currently detected on the host machine.
    /// </summary>
    public bool IsFfmpegInstalled
    {
        get => _isFfmpegInstalled;
        private set => SetProperty(ref _isFfmpegInstalled, value);
    }

    /// <summary>
    /// Human-readable status of FFmpeg detection or installation.
    /// </summary>
    public string FfmpegStatusText
    {
        get => _ffmpegStatusText;
        private set => SetProperty(ref _ffmpegStatusText, value);
    }

    /// <summary>
    /// Whether par2cmdline (<c>par2.exe</c>) is currently detected on the host machine.
    /// </summary>
    public bool IsPar2Installed
    {
        get => _isPar2Installed;
        private set => SetProperty(ref _isPar2Installed, value);
    }

    /// <summary>
    /// Human-readable status of par2cmdline detection or installation.
    /// </summary>
    public string Par2StatusText
    {
        get => _par2StatusText;
        private set => SetProperty(ref _par2StatusText, value);
    }

    /// <summary>
    /// Whether to expose the software-defined virtual optical drive backend simulator.
    /// </summary>
    public bool EnableVirtualDrive
    {
        get => _enableVirtualDrive;
        set => SetProperty(ref _enableVirtualDrive, value);
    }

    /// <summary>
    /// In-memory diagnostic ring buffer capacity for the structured burn logger.
    /// </summary>
    public int LogRingBufferCapacity
    {
        get => _logRingBufferCapacity;
        set => SetProperty(ref _logRingBufferCapacity, value);
    }

    /// <summary>
    /// Whether to look up Audio CD album and track titles from MusicBrainz by default when ripping.
    /// </summary>
    public bool LookupMusicBrainzByDefault
    {
        get => _lookupMusicBrainzByDefault;
        set => SetProperty(ref _lookupMusicBrainzByDefault, value);
    }

    /// <summary>
    /// Whether to verify extracted Audio CD tracks against AccurateRip by default when ripping.
    /// </summary>
    public bool VerifyAccurateRipByDefault
    {
        get => _verifyAccurateRipByDefault;
        set => SetProperty(ref _verifyAccurateRipByDefault, value);
    }

    /// <summary>
    /// Whether to check for updates against GitHub Releases on startup. Strictly opt-in (default false).
    /// </summary>
    public bool CheckForUpdatesOnStartup
    {
        get => _checkForUpdatesOnStartup;
        set => SetProperty(ref _checkForUpdatesOnStartup, value);
    }

    /// <summary>
    /// Status message from the most recent GitHub update check.
    /// </summary>
    public string UpdateStatusMessage
    {
        get => _updateStatusMessage;
        set => SetProperty(ref _updateStatusMessage, value);
    }

    /// <summary>
    /// User-facing status notification message.
    /// </summary>
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>
    /// Available theme options for binding.
    /// </summary>
    public IReadOnlyList<ThemeMode> AvailableThemes { get; } = new[]
    {
        ThemeMode.System,
        ThemeMode.Dark,
        ThemeMode.Light
    };

    /// <summary>
    /// Default write speed choices: Recommended (the default) first, then Slowest, Maximum and fixed multipliers.
    /// </summary>
    public IReadOnlyList<SpeedChoice> AvailableSpeeds { get; } =
        new[] { WriteSpeedPolicy.Recommended, WriteSpeedPolicy.Slowest, WriteSpeedPolicy.Maximum, 4, 8, 10, 16, 24, 32, 48, 52 }
            .Select(v => new SpeedChoice(v, WriteSpeedPolicy.Describe(v)))
            .ToArray();

    /// <summary>One entry of the default write speed list.</summary>
    public sealed record SpeedChoice(int Value, string Label);

    #endregion

    #region Commands

    /// <summary>
    /// Downloads and installs the official LGPL build of FFmpeg into <c>%LOCALAPPDATA%\Programs\ffmpeg</c>.
    /// </summary>
    public AsyncRelayCommand InstallFfmpegCommand { get; }

    /// <summary>
    /// Downloads and installs <c>par2cmdline</c> (<c>par2.exe</c>) into <c>%LOCALAPPDATA%\Programs\par2cmdline</c>.
    /// </summary>
    public AsyncRelayCommand InstallPar2Command { get; }

    /// <summary>
    /// Queries GitHub Releases API to check for newer versions of IgnisDisc.
    /// </summary>
    public AsyncRelayCommand CheckForUpdatesCommand { get; }

    /// <summary>
    /// Saves current settings to disk.
    /// </summary>
    public RelayCommand SaveSettingsCommand { get; }

    /// <summary>
    /// Resets all settings back to factory defaults.
    /// </summary>
    public RelayCommand ResetSettingsCommand { get; }

    /// <summary>
    /// Reloads settings from disk.
    /// </summary>
    public RelayCommand ReloadSettingsCommand { get; }

    #endregion

    /// <summary>
    /// Initializes a new instance of <see cref="SettingsViewModel"/>.
    /// </summary>
    public SettingsViewModel(AppSettingsService? settingsService = null, MainViewModel? mainViewModel = null)
    {
        _settingsService = settingsService ?? new AppSettingsService();
        _mainViewModel = mainViewModel;

        SaveSettingsCommand = new RelayCommand(SaveSettings);
        ResetSettingsCommand = new RelayCommand(ResetSettings);
        ReloadSettingsCommand = new RelayCommand(LoadSettings);
        CheckForUpdatesCommand = new AsyncRelayCommand(async _ => await CheckForUpdatesAsync(), _ => !_isCheckingForUpdates);
        InstallFfmpegCommand = new AsyncRelayCommand(async _ => await InstallFfmpegAsync(), _ => !_isInstallingTool);
        InstallPar2Command = new AsyncRelayCommand(async _ => await InstallPar2Async(), _ => !_isInstallingTool);

        LoadSettings();
    }

    /// <summary>
    /// Re-evaluates whether <c>ffmpeg.exe</c> and <c>par2.exe</c> are available on the host machine.
    /// </summary>
    public void RefreshToolStatuses()
    {
        string? resolvedFfmpeg = FfmpegLocator.FindFfmpegExecutable(
            string.IsNullOrWhiteSpace(_ffmpegExecutablePath) ? null : _ffmpegExecutablePath);
        IsFfmpegInstalled = !string.IsNullOrEmpty(resolvedFfmpeg);
        FfmpegStatusText = IsFfmpegInstalled
            ? $"Installed: {resolvedFfmpeg}"
            : "Not found — click 'Install FFmpeg (1-Click)' to download the LGPL build automatically.";

        string? resolvedPar2 = Par2Runner.FindPar2Executable();
        IsPar2Installed = !string.IsNullOrEmpty(resolvedPar2);
        Par2StatusText = IsPar2Installed
            ? $"Installed: {resolvedPar2}"
            : "Not found — click 'Install par2 (1-Click)' to enable PAR2 archive recovery.";
    }

    /// <summary>
    /// Loads settings from the service into view model properties.
    /// </summary>
    public void LoadSettings()
    {
        var settings = _settingsService.CurrentSettings;
        DefaultWriteSpeedMultiplier = settings.DefaultWriteSpeedMultiplier;
        VerifyAfterBurnByDefault = settings.VerifyAfterBurnByDefault;
        StrictVerify = settings.StrictVerify;
        EjectAfterBurnByDefault = settings.EjectAfterBurnByDefault;
        EnableBurnFreeByDefault = settings.EnableBurnFreeByDefault;
        TempStagingDirectory = settings.TempStagingDirectory;
        ThemeMode = settings.ThemeMode;
        FfmpegExecutablePath = settings.FfmpegExecutablePath;
        EnableVirtualDrive = settings.EnableVirtualDrive;
        LogRingBufferCapacity = settings.LogRingBufferCapacity;
        LookupMusicBrainzByDefault = settings.LookupMusicBrainzByDefault;
        VerifyAccurateRipByDefault = settings.VerifyAccurateRipByDefault;
        CheckForUpdatesOnStartup = settings.CheckForUpdatesOnStartup;
        RefreshToolStatuses();
        StatusMessage = "Settings loaded.";
    }

    /// <summary>
    /// Persists current view model properties to the settings service and file.
    /// </summary>
    public void SaveSettings()
    {
        // Start from the current settings so values owned by other screens (e.g. audio read offset) are kept.
        var settings = _settingsService.CurrentSettings.Clone();
        settings.DefaultWriteSpeedMultiplier = DefaultWriteSpeedMultiplier;
        settings.VerifyAfterBurnByDefault = VerifyAfterBurnByDefault;
        settings.StrictVerify = StrictVerify;
        settings.EjectAfterBurnByDefault = EjectAfterBurnByDefault;
        settings.EnableBurnFreeByDefault = EnableBurnFreeByDefault;
        settings.TempStagingDirectory = TempStagingDirectory;
        settings.ThemeMode = ThemeMode;
        settings.FfmpegExecutablePath = FfmpegExecutablePath;
        settings.EnableVirtualDrive = EnableVirtualDrive;
        settings.LogRingBufferCapacity = LogRingBufferCapacity;
        settings.LookupMusicBrainzByDefault = LookupMusicBrainzByDefault;
        settings.VerifyAccurateRipByDefault = VerifyAccurateRipByDefault;
        settings.CheckForUpdatesOnStartup = CheckForUpdatesOnStartup;

        _settingsService.CurrentSettings = settings;
        _settingsService.Save();
        ThemeManager.ApplyTheme(ThemeMode);
        if (_mainViewModel != null && SelectedRecorder != null)
        {
            _mainViewModel.SelectedRecorder = SelectedRecorder;
        }
        RefreshToolStatuses();
        StatusMessage = "Settings saved successfully.";
        SettingsSaved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Resets configuration back to defaults and refreshes view model properties.
    /// </summary>
    public void ResetSettings()
    {
        _settingsService.ResetToDefaults();
        LoadSettings();
        StatusMessage = "Settings reset to default values.";
        SettingsSaved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Downloads and extracts the LGPL Windows build of FFmpeg, updates <see cref="FfmpegExecutablePath"/>, and persists settings.
    /// </summary>
    public async Task<ToolInstallResult> InstallFfmpegAsync(
        string? targetDirectory = null,
        HttpClient? httpClient = null,
        string? downloadUrlOverride = null,
        CancellationToken ct = default)
    {
        _isInstallingTool = true;
        InstallFfmpegCommand.RaiseCanExecuteChanged();
        InstallPar2Command.RaiseCanExecuteChanged();

        try
        {
            var progress = new Progress<string>(msg =>
            {
                FfmpegStatusText = msg;
                StatusMessage = msg;
            });

            var result = await ExternalToolInstallerService.InstallFfmpegAsync(
                targetDirectory,
                httpClient,
                downloadUrlOverride,
                progress,
                ct).ConfigureAwait(true);

            if (result.Success && !string.IsNullOrEmpty(result.ExecutablePath))
            {
                FfmpegExecutablePath = result.ExecutablePath;
                SaveSettings();
                StatusMessage = $"FFmpeg (LGPL) installed: {result.ExecutablePath}";
            }
            else
            {
                FfmpegStatusText = $"FFmpeg install failed: {result.ErrorMessage}";
                StatusMessage = FfmpegStatusText;
            }

            return result;
        }
        finally
        {
            _isInstallingTool = false;
            InstallFfmpegCommand.RaiseCanExecuteChanged();
            InstallPar2Command.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Downloads and extracts <c>par2cmdline</c> (<c>par2.exe</c>) and refreshes workspace tool availability.
    /// </summary>
    public async Task<ToolInstallResult> InstallPar2Async(
        string? targetDirectory = null,
        HttpClient? httpClient = null,
        string? downloadUrlOverride = null,
        CancellationToken ct = default)
    {
        _isInstallingTool = true;
        InstallFfmpegCommand.RaiseCanExecuteChanged();
        InstallPar2Command.RaiseCanExecuteChanged();

        try
        {
            var progress = new Progress<string>(msg =>
            {
                Par2StatusText = msg;
                StatusMessage = msg;
            });

            var result = await ExternalToolInstallerService.InstallPar2Async(
                targetDirectory,
                httpClient,
                downloadUrlOverride,
                progress,
                ct).ConfigureAwait(true);

            if (result.Success && !string.IsNullOrEmpty(result.ExecutablePath))
            {
                RefreshToolStatuses();
                StatusMessage = $"par2cmdline installed: {result.ExecutablePath}";
                SettingsSaved?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                Par2StatusText = $"par2 install failed: {result.ErrorMessage}";
                StatusMessage = Par2StatusText;
            }

            return result;
        }
        finally
        {
            _isInstallingTool = false;
            InstallFfmpegCommand.RaiseCanExecuteChanged();
            InstallPar2Command.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Runs the update check once at startup when the user turned it on; the result appears on the Settings page.
    /// Never throws: a failed check must not affect startup.
    /// </summary>
    public async Task CheckForUpdatesOnStartupAsync()
    {
        try
        {
            var result = await CheckForUpdatesAsync().ConfigureAwait(true);
            if (result.Success && result.IsUpdateAvailable)
            {
                StatusMessage = $"IgnisDisc {result.LatestVersion} is available (you have {result.CurrentVersion}). See Settings.";
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// Checks GitHub Releases for updates.
    /// </summary>
    public async Task<UpdateCheckResult> CheckForUpdatesAsync(HttpClient? httpClient = null, CancellationToken ct = default)
    {
        _isCheckingForUpdates = true;
        CheckForUpdatesCommand.RaiseCanExecuteChanged();
        UpdateStatusMessage = "Checking GitHub Releases for updates...";

        try
        {
            var result = await GitHubUpdateChecker.CheckForUpdateAsync(null, httpClient, null, ct);
            if (!result.Success)
            {
                UpdateStatusMessage = $"Update check failed: {result.ErrorMessage}";
            }
            else if (result.IsUpdateAvailable)
            {
                UpdateStatusMessage = $"Update available: {result.LatestVersion} (Current: {result.CurrentVersion}). Visit: {result.ReleaseUrl}";
            }
            else
            {
                UpdateStatusMessage = $"IgnisDisc is up to date (version {result.CurrentVersion}).";
            }
            return result;
        }
        catch (Exception ex)
        {
            UpdateStatusMessage = $"Update check error: {ex.Message}";
            return new UpdateCheckResult(false, string.Empty, string.Empty, string.Empty, ex.Message);
        }
        finally
        {
            _isCheckingForUpdates = false;
            CheckForUpdatesCommand.RaiseCanExecuteChanged();
        }
    }
}
