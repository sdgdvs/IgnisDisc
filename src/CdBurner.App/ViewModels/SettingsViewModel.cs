using System.Collections.ObjectModel;
using CdBurner.App.Mvvm;
using CdBurner.App.Themes;
using CdBurner.Core.Abstractions;
using CdBurner.Core.Persistence;

namespace CdBurner.App.ViewModels;

/// <summary>
/// View model managing application preferences, default write speeds, overburn/buffer underrun policies,
/// scratch directories, visual themes, and diagnostic logging configuration.
/// </summary>
public class SettingsViewModel : ObservableObject
{
    private readonly AppSettingsService _settingsService;
    private readonly MainViewModel? _mainViewModel;

    private int _defaultWriteSpeedMultiplier;
    private bool _verifyAfterBurnByDefault;
    private bool _ejectAfterBurnByDefault;
    private bool _enableBurnFreeByDefault;
    private string _tempStagingDirectory = string.Empty;
    private ThemeMode _themeMode = ThemeMode.System;
    private string _ffmpegExecutablePath = string.Empty;
    private bool _enableVirtualDrive;
    private int _logRingBufferCapacity;
    private IDiscRecorderBackend? _selectedRecorder;
    private string _statusMessage = "Ready";

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
        set => SetProperty(ref _ffmpegExecutablePath, value);
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
    /// Available default burn speeds.
    /// </summary>
    public IReadOnlyList<int> AvailableSpeeds { get; } = new[]
    {
        0, 1, 2, 4, 8, 16, 24, 32, 40, 48, 52
    };

    #endregion

    #region Commands

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

        LoadSettings();
    }

    /// <summary>
    /// Loads settings from the service into view model properties.
    /// </summary>
    public void LoadSettings()
    {
        var settings = _settingsService.CurrentSettings;
        DefaultWriteSpeedMultiplier = settings.DefaultWriteSpeedMultiplier;
        VerifyAfterBurnByDefault = settings.VerifyAfterBurnByDefault;
        EjectAfterBurnByDefault = settings.EjectAfterBurnByDefault;
        EnableBurnFreeByDefault = settings.EnableBurnFreeByDefault;
        TempStagingDirectory = settings.TempStagingDirectory;
        ThemeMode = settings.ThemeMode;
        FfmpegExecutablePath = settings.FfmpegExecutablePath;
        EnableVirtualDrive = settings.EnableVirtualDrive;
        LogRingBufferCapacity = settings.LogRingBufferCapacity;
        StatusMessage = "Settings loaded.";
    }

    /// <summary>
    /// Persists current view model properties to the settings service and file.
    /// </summary>
    public void SaveSettings()
    {
        // Start from the current settings so values owned by other screens (e.g. audio rip options) are kept.
        var settings = _settingsService.CurrentSettings.Clone();
        settings.DefaultWriteSpeedMultiplier = DefaultWriteSpeedMultiplier;
        settings.VerifyAfterBurnByDefault = VerifyAfterBurnByDefault;
        settings.EjectAfterBurnByDefault = EjectAfterBurnByDefault;
        settings.EnableBurnFreeByDefault = EnableBurnFreeByDefault;
        settings.TempStagingDirectory = TempStagingDirectory;
        settings.ThemeMode = ThemeMode;
        settings.FfmpegExecutablePath = FfmpegExecutablePath;
        settings.EnableVirtualDrive = EnableVirtualDrive;
        settings.LogRingBufferCapacity = LogRingBufferCapacity;

        _settingsService.CurrentSettings = settings;
        _settingsService.Save();
        ThemeManager.ApplyTheme(ThemeMode);
        if (_mainViewModel != null && SelectedRecorder != null)
        {
            _mainViewModel.SelectedRecorder = SelectedRecorder;
        }
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
}
